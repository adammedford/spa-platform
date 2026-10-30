#!/usr/bin/env bash
# One run of the startup benchmark's local tier (L), as the nightly ARM64 job runs it. Owner:
# integrator, written for #576.
#
#     .github/startup-bench.sh <out-dir>                 informational: the job passes whatever the verdict
#     STARTUP_BENCH_ASSERTS=true .github/startup-bench.sh <out-dir>    asserting: the verdict's exit code
#
# .github/workflows/startup-bench.yml calls this from the checkout's root, on `ubuntu-24.04-arm`,
# nightly on main and on demand (ADR-0033 -> Decision, the cadence; #176 Q4 and Q5). It is a file
# rather than a `run:` block so that its rules are proved on every push by .github/startup_bench_tests.py,
# with the benchmark doubled, since the job itself never runs on a workstation and a quoting error
# in a nightly step is found a night later by nobody.
#
# What it does, each rule with its source:
#   - Runs `tools/bench/startup.py --verdict` with ADR-0033's three local budgets, healthy 4 s,
#     ready 6 s and the first app-host 401 7 s (#175 Q3). The budgets are named here and not in the
#     benchmark, which holds no defaults because they are per environment (#575's Answer, decision 5).
#   - Keeps what the run printed as `run-1.txt` beside its `--json` file, `run-1.json`; the
#     workflow uploads the directory as the run's artifact (#176 Q6).
#   - Re-runs exactly once, on the same images (`--no-build`), when the run gave no verdict, exit 2:
#     a loaded floor, or a run that stopped before judging. Both runs' files are kept, as `run-2.*`,
#     and the second run's verdict is the job's. A `bench: fail:`, exit 1, is never re-run: that would
#     be retry-until-green, which #176 Q6 and #175 Q7 forbid. The benchmark never re-runs itself
#     (#575's Answer, decision 2 names this job as the caller that does).
#   - Ends on the verdict, `startup-bench: <mode>: bench: <status>: ...`, so a log's last line is
#     the verdict (CODING_STANDARDS.md -> "A check fails closed"), and writes it to the step summary
#     when GitHub offers one, with a `::warning::` (informational) or `::error::` (asserting)
#     annotation on anything but a pass, so a breach is seen without opening the log.
#   - Informational until promoted: the job's exit code is the verdict's only when
#     STARTUP_BENCH_ASSERTS is `true`, which the workflow sets at the promotion the ADR names (ten
#     calibration runs fitting the budgets with their headroom). An informational breach prompts a
#     workstation run and is not the tuning trigger (ADR-0033 -> Decision, the tuning trigger).
#   - A run is a verdict only when its exit code is 0, 1 or 2 AND its last line is one of the three
#     verdict lines: the verdict mode ends on one of those lines whatever happens inside it (#575,
#     b3a0926). Another exit code means the job itself did not run the benchmark (no python3); so
#     does exit 2 with another last line, because argparse refuses an argument with exit 2 and a
#     usage line (https://docs.python.org/3/library/argparse.html#exiting-methods), which the exit
#     code alone would read as `bench: no verdict:` and re-run. Either fails the job in both modes,
#     without a re-run, because a calibration run that measured nothing must not count as one of
#     the ten (found in #576's review: a refused argument ended informational, exit 0).
#   - <out-dir> may be relative to the caller's directory: it is resolved before the change to the
#     checkout's root, so a workstation's `out` lands where the caller said (#576's review: before
#     this, tee and tail failed and the script ended on an empty verdict with exit 0, failing open).
#
# bash 3.2 is enough: a workstation can run it the same way, and the macOS job parses it.

set -uo pipefail

if [ $# -ne 1 ] || [ -z "$1" ]; then
  echo "usage: .github/startup-bench.sh <out-dir>" >&2
  exit 64
fi
out="$1"
mkdir -p "$out" || { echo "startup-bench: the benchmark did not run: cannot create $out"; exit 73; }
# Absolute before the cd below, or a relative <out-dir> is created here and written to under the root.
out="$(cd "$out" && pwd)" || { echo "startup-bench: the benchmark did not run: cannot enter $1"; exit 73; }
cd "$(dirname "$0")/.." || { echo "startup-bench: the benchmark did not run: cannot reach the checkout's root"; exit 73; }

mode=informational
if [ "${STARTUP_BENCH_ASSERTS:-false}" = "true" ]; then
  mode=asserting
fi

# ADR-0033's local budgets, in seconds per clock (#175 Q3). The deployed column is not this tier's.
budgets="--budget healthz=4 --budget readyz=6 --budget app_host=7"

status=0
verdict=""

# One run, numbered $1, with any further arguments passed to the benchmark. Its output goes to the
# log and to run-$1.txt; $status and $verdict are the benchmark's exit code and its last line.
run_once() {
  number="$1"
  shift
  # shellcheck disable=SC2086  # $budgets is a word list on purpose
  python3 -I tools/bench/startup.py --verdict $budgets --json "$out/run-$number.json" "$@" 2>&1 \
    | tee "$out/run-$number.txt"
  status="${PIPESTATUS[0]}"
  verdict="$(tail -n 1 "$out/run-$number.txt")"
}

# The last run was a verdict: exit 0, 1 or 2 and one of the verdict mode's three last lines. Anything
# else (no python3, a refused argument's usage line under exit 2) is a benchmark that did not run,
# which fails the job in both modes and is never re-run, with a non-zero code even if the exit was 0.
is_verdict() {
  case "$status" in
    0 | 1 | 2) ;;
    *) return 1 ;;
  esac
  case "$verdict" in
    "bench: pass:"* | "bench: fail:"* | "bench: no verdict:"*) return 0 ;;
    *) return 1 ;;
  esac
}

did_not_run() {
  echo "::error::startup-bench: the benchmark did not run (exit $status): $verdict"
  echo "startup-bench: the benchmark did not run (exit $status): $verdict"
  if [ "$status" -ne 0 ]; then exit "$status"; fi
  exit 70
}

run_once 1
is_verdict || did_not_run
if [ "$status" -eq 2 ]; then
  echo "startup-bench: run 1 gave no verdict, so the one re-run ADR-0033 allows follows on the same images; both runs' files are kept (#175 Q7)."
  run_once 2 --no-build
  is_verdict || did_not_run
fi

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    echo "## Startup benchmark, tier L ($mode)"
    echo
    for file in "$out"/run-*.txt; do
      [ -f "$file" ] || continue
      echo "- \`$(basename "$file")\`: $(tail -n 1 "$file")"
    done
  } >> "$GITHUB_STEP_SUMMARY"
fi

if [ "$status" -ne 0 ]; then
  if [ "$mode" = asserting ]; then
    echo "::error::startup-bench: $verdict"
  else
    echo "::warning::startup-bench (informational until promoted): $verdict"
  fi
fi

echo "startup-bench: $mode: $verdict"
if [ "$mode" = asserting ]; then
  exit "$status"
fi
exit 0
