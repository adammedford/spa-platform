#!/usr/bin/env python3
"""Test companion for .github/startup-bench.sh: the nightly ARM64 job's one run of the startup
benchmark's local tier, its one re-run, and the informational mode it holds until promoted.

Owner: integrator. Built in #576. The job itself runs on `ubuntu-24.04-arm` and never on the Mac
that runs this file, so the script's rules are the only part of the job a push can prove: which
arguments the benchmark is given, that a `bench: no verdict:` (exit 2) earns exactly one re-run and
a `bench: fail:` (exit 1) none, that both runs' files are kept, that the job's exit code is the
verdict's only once STARTUP_BENCH_ASSERTS is `true`, and that the last line is the verdict either
way (ADR-0033 -> Decision, the cadence and the reporting; #175 Q7; #176 Q4 to Q6; #575's Answer,
decision 2).

The benchmark is a double: a `python3` first on PATH that records its arguments, writes the `--json`
file it was named, prints a verdict line and exits with the code the case scripted for that call.
Nothing here starts Docker.

Run: python3 -I .github/startup_bench_tests.py
"""

from __future__ import annotations

import os
import pathlib
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / ".github" / "startup-bench.sh"

PASS = "bench: pass: N=10; healthy max 1500 ms <= 4000 ms; ready max 2000 ms <= 6000 ms"
FAIL = "bench: fail: ready max 6500 ms over its budget of 6000 ms; medians ..."
NO_VERDICT = "bench: no verdict: the floor's /healthz max 1900 ms shows a loaded machine; re-run once"

# The python3 double. Each line of $BENCH_SCRIPT is one call's answer, `<exit code> <verdict line>`;
# a call past the last line is a defect of the script under test, so it exits 99 and says so. The
# double writes the --json file it was named, as the benchmark does, prints a report line and the
# verdict line, and appends its whole argument list to $BENCH_CALLS, one call a line.
DOUBLE = r"""#!/bin/sh
printf '%s\n' "$*" >> "$BENCH_CALLS"
line="$(sed -n 1p "$BENCH_SCRIPT")"
[ -n "$line" ] || { echo "python3 double: no answer scripted for this call"; exit 99; }
sed -i.bak 1d "$BENCH_SCRIPT"
status="${line%% *}"
verdict="${line#* }"
while [ $# -gt 0 ]; do
  case "$1" in
    --json) echo '{"runs": []}' > "$2"; shift ;;
  esac
  shift
done
echo "a report line the benchmark printed"
echo "$verdict"
exit "$status"
"""


class Run:
    def __init__(self, test: unittest.TestCase, answers: list[tuple[int, str]], *,
                 asserts: str | None = None, summary: bool = False, out_arg: str | None = None):
        scratch = tempfile.TemporaryDirectory()
        test.addCleanup(scratch.cleanup)
        self.dir = pathlib.Path(scratch.name).resolve()
        bin_dir = self.dir / "bin"
        bin_dir.mkdir()
        double = bin_dir / "python3"
        double.write_text(DOUBLE)
        double.chmod(0o755)
        self.calls = self.dir / "calls.log"
        self.calls.touch()
        script = self.dir / "answers"
        script.write_text("".join(f"{status} {line}\n" for status, line in answers))
        self.out = self.dir / "out"
        self.summary = self.dir / "summary.md"
        env = {
            "PATH": f"{bin_dir}:/usr/bin:/bin",
            "HOME": str(self.dir),
            "LC_ALL": "C",
            "BENCH_CALLS": str(self.calls),
            "BENCH_SCRIPT": str(script),
        }
        if asserts is not None:
            env["STARTUP_BENCH_ASSERTS"] = asserts
        if summary:
            env["GITHUB_STEP_SUMMARY"] = str(self.summary)
        # The out-dir as the job passes it, absolute, unless a case passes its own (a relative one).
        completed = subprocess.run(["bash", str(SCRIPT), out_arg or str(self.out)], cwd=self.dir, env=env,
                                   capture_output=True, text=True, timeout=60)
        self.status = completed.returncode
        self.stdout = completed.stdout
        self.stderr = completed.stderr
        self.lines = self.stdout.splitlines()

    @property
    def call_args(self) -> list[str]:
        return self.calls.read_text().splitlines()

    def kept(self) -> list[str]:
        return sorted(path.name for path in self.out.iterdir()) if self.out.exists() else []


class StartupBenchTests(unittest.TestCase):
    def assertBenchmarkCall(self, call: str, *extra: str):
        """One call of the benchmark: the verdict mode with ADR-0033's three local budgets."""
        for expected in ("-I tools/bench/startup.py", "--verdict", "--budget healthz=4",
                         "--budget readyz=6", "--budget app_host=7"):
            self.assertIn(expected, call, call)
        for expected in extra:
            self.assertIn(expected, call, call)

    def test_a_pass_runs_the_benchmark_once_and_keeps_both_files(self):
        run = Run(self, [(0, PASS)])
        self.assertEqual(0, run.status, run.stdout + run.stderr)
        self.assertEqual(1, len(run.call_args), run.call_args)
        self.assertBenchmarkCall(run.call_args[0], f"--json {run.out}/run-1.json")
        self.assertNotIn("--no-build", run.call_args[0])
        self.assertEqual(["run-1.json", "run-1.txt"], run.kept())
        self.assertIn(PASS, (run.out / "run-1.txt").read_text())
        self.assertEqual(f"startup-bench: informational: {PASS}", run.lines[-1])

    def test_a_fail_is_not_re_run_and_does_not_fail_the_job_until_promoted(self):
        run = Run(self, [(1, FAIL)])
        self.assertEqual(0, run.status, run.stdout + run.stderr)
        self.assertEqual(1, len(run.call_args), run.call_args)
        self.assertEqual(["run-1.json", "run-1.txt"], run.kept())
        self.assertEqual(f"startup-bench: informational: {FAIL}", run.lines[-1])
        self.assertTrue(any(line.startswith("::warning::") and FAIL in line for line in run.lines), run.stdout)

    def test_a_fail_fails_the_job_once_promoted(self):
        run = Run(self, [(1, FAIL)], asserts="true")
        self.assertEqual(1, run.status, run.stdout + run.stderr)
        self.assertEqual(1, len(run.call_args), run.call_args)
        self.assertEqual(f"startup-bench: asserting: {FAIL}", run.lines[-1])
        self.assertTrue(any(line.startswith("::error::") and FAIL in line for line in run.lines), run.stdout)

    def test_a_pass_exits_zero_once_promoted(self):
        run = Run(self, [(0, PASS)], asserts="true")
        self.assertEqual(0, run.status, run.stdout + run.stderr)
        self.assertEqual(f"startup-bench: asserting: {PASS}", run.lines[-1])

    def test_no_verdict_earns_exactly_one_re_run_on_the_same_images_and_keeps_both_runs(self):
        run = Run(self, [(2, NO_VERDICT), (0, PASS)], asserts="true")
        self.assertEqual(0, run.status, run.stdout + run.stderr)
        self.assertEqual(2, len(run.call_args), run.call_args)
        self.assertBenchmarkCall(run.call_args[1], "--no-build", f"--json {run.out}/run-2.json")
        self.assertEqual(["run-1.json", "run-1.txt", "run-2.json", "run-2.txt"], run.kept())
        self.assertIn(NO_VERDICT, (run.out / "run-1.txt").read_text())
        self.assertIn(PASS, (run.out / "run-2.txt").read_text())
        self.assertEqual(f"startup-bench: asserting: {PASS}", run.lines[-1])

    def test_the_re_run_is_judged_on_its_own_verdict(self):
        run = Run(self, [(2, NO_VERDICT), (1, FAIL)], asserts="true")
        self.assertEqual(1, run.status, run.stdout + run.stderr)
        self.assertEqual(2, len(run.call_args), run.call_args)
        self.assertEqual(f"startup-bench: asserting: {FAIL}", run.lines[-1])

    def test_a_second_no_verdict_is_never_re_run_again(self):
        run = Run(self, [(2, NO_VERDICT), (2, NO_VERDICT)], asserts="true")
        self.assertEqual(2, run.status, run.stdout + run.stderr)
        self.assertEqual(2, len(run.call_args), run.call_args)
        self.assertEqual(["run-1.json", "run-1.txt", "run-2.json", "run-2.txt"], run.kept())
        self.assertEqual(f"startup-bench: asserting: {NO_VERDICT}", run.lines[-1])

    def test_a_second_no_verdict_does_not_fail_the_job_until_promoted(self):
        run = Run(self, [(2, NO_VERDICT), (2, NO_VERDICT)])
        self.assertEqual(0, run.status, run.stdout + run.stderr)
        self.assertEqual(f"startup-bench: informational: {NO_VERDICT}", run.lines[-1])

    def test_a_benchmark_that_did_not_run_fails_the_job_in_either_mode(self):
        # Exit codes other than 0, 1 and 2 are not the benchmark's verdicts: startup.py's verdict
        # mode ends on one of its three lines whatever happens inside it, so another code means
        # the job itself is broken (no python3), which no calibration run should read as
        # informational.
        run = Run(self, [(127, "bash: python3: command not found")])
        self.assertEqual(127, run.status, run.stdout + run.stderr)
        self.assertEqual(1, len(run.call_args), run.call_args)
        self.assertTrue(run.lines[-1].startswith("startup-bench: the benchmark did not run"), run.stdout)

    def test_a_refused_argument_is_not_a_no_verdict_and_fails_the_job_without_a_re_run(self):
        # argparse refuses an argument with exit 2 and a usage line, the code `bench: no verdict:`
        # shares; read by the exit code alone it earned a re-run and then an informational pass
        # (#576's review). A verdict is a verdict line, so this fails the job in both modes, once.
        for asserts in (None, "true"):
            with self.subTest(asserts=asserts):
                run = Run(self, [(2, "startup.py: error: unrecognized arguments: --bogus")], asserts=asserts)
                self.assertEqual(2, run.status, run.stdout + run.stderr)
                self.assertEqual(1, len(run.call_args), run.call_args)
                self.assertTrue(run.lines[-1].startswith("startup-bench: the benchmark did not run"), run.stdout)

    def test_a_verdict_line_under_another_exit_code_is_not_a_verdict(self):
        # Both halves of the rule: the line and the code. startup.py never pairs a verdict line with
        # a code outside 0, 1 and 2, so one that does is not startup.py's verdict mode.
        run = Run(self, [(3, PASS)])
        self.assertEqual(3, run.status, run.stdout + run.stderr)
        self.assertTrue(run.lines[-1].startswith("startup-bench: the benchmark did not run"), run.stdout)

    def test_an_exit_zero_without_a_verdict_line_still_fails_the_job(self):
        # A pass is `bench: pass:`, not exit 0: a benchmark that ended on something else measured
        # nothing the job can read (CODING_STANDARDS.md -> "A check fails closed").
        run = Run(self, [(0, "a report line and no verdict")])
        self.assertNotEqual(0, run.status, run.stdout + run.stderr)
        self.assertTrue(run.lines[-1].startswith("startup-bench: the benchmark did not run"), run.stdout)

    def test_a_relative_out_dir_is_the_callers_and_the_verdict_still_ends_the_log(self):
        # The job passes an absolute path; a workstation may pass `out`. Before #576's review the
        # directory was made in the caller's cwd and written to under the checkout's root, so tee
        # and tail failed and the last line was an empty verdict with exit 0, failing open.
        run = Run(self, [(0, PASS)], out_arg="out")
        self.assertEqual(0, run.status, run.stdout + run.stderr)
        self.assertEqual(["run-1.json", "run-1.txt"], run.kept())
        self.assertBenchmarkCall(run.call_args[0], f"--json {run.out}/run-1.json")
        self.assertEqual(f"startup-bench: informational: {PASS}", run.lines[-1])

    def test_the_verdict_goes_to_the_step_summary_when_github_offers_one(self):
        run = Run(self, [(2, NO_VERDICT), (0, PASS)], summary=True)
        self.assertEqual(0, run.status, run.stdout + run.stderr)
        text = run.summary.read_text()
        self.assertIn(NO_VERDICT, text)
        self.assertIn(PASS, text)

    def test_the_script_names_its_argument(self):
        completed = subprocess.run(["bash", str(SCRIPT)], cwd=ROOT, capture_output=True, text=True,
                                   env={"PATH": "/usr/bin:/bin", "LC_ALL": "C"}, timeout=60)
        self.assertNotEqual(0, completed.returncode)
        self.assertIn("startup-bench.sh <out-dir>", completed.stdout + completed.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
