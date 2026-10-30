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
#   - An exit code that is none of 0, 1 and 2 is not a verdict: the verdict mode ends on one of its
#     three lines whatever happens inside it (#575, b3a0926), so another code means the job itself
#     did not run the benchmark (no python3, a refused argument). That fails the job in both modes,
#     because a calibration run that measured nothing must not count as one of the ten.
#
# bash 3.2 is enough: a workstation can run it the same way, and the macOS job parses it.

set -uo pipefail

if [ $# -ne 1 ] || [ -z "$1" ]; then
  echo "usage: .github/startup-bench.sh <out-dir>" >&2
  exit 64
fi
out="$1"
mkdir -p "$out" || { echo "startup-bench: the benchmark did not run: cannot create $out"; exit 73; }
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

run_once 1
if [ "$status" -eq 2 ]; then
  echo "startup-bench: run 1 gave no verdict, so the one re-run ADR-0033 allows follows on the same images; both runs' files are kept (#175 Q7)."
  run_once 2 --no-build
fi

case "$status" in
  0 | 1 | 2) ;;
  *)
    echo "::error::startup-bench: the benchmark did not run (exit $status): $verdict"
    echo "startup-bench: the benchmark did not run (exit $status): $verdict"
    exit "$status"
    ;;
esac

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
