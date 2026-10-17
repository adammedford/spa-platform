#!/usr/bin/env bash
# Which half of the repository a change touches. Owner: integrator.
#
#     git diff --name-only <base>...<head> | .github/touches.sh build       # exit 0 when .NET changed
#     git diff --name-only <base>...<head> | .github/touches.sh terraform   # exit 0 when infra/ changed
#
# .githooks/pre-push skips the build and Terraform when a push touches neither half, and
# .github/workflows/ci.yml skips the same two jobs for the same reason. Those were two copies of two
# patterns, and a copy agrees with its source until one of them is edited; this is the one place both
# read them from. CI additionally runs both jobs when its workflow, this classifier or tools/smoke.sh
# changes, or when it cannot resolve the base; those overrides belong to the workflow's decision step.
#
# Written for the bash 3.2 that ships with macOS, and for grep -E rather than -P, which BSD grep
# does not have.

case "${1:-}" in
  # Contracts are embedded by Platform.Registry and read by behavior tests; nuget.config controls
  # restore. Neither is documentation-only input (ADR-0020's CI filtering, PR #50).
  build) pattern='^(src|tests|fixtures|contracts|tools/spactl)/|\.(cs|csproj|props|targets|slnx)$|^(global\.json|nuget\.config)$' ;;
  # Every Terraform root and module is under infra/.
  terraform) pattern='^infra/' ;;
  *)
    # An unknown half is reported and answered yes, not no: both callers skip a check on a no, so a
    # typo here has to cost a build rather than save one.
    echo "touches.sh: '${1:-}' is not a half; say build or terraform." >&2
    exit 0
    ;;
esac

# Drain the complete input: grep -q would close the pipe at an early match and make printf fail
# with SIGPIPE under both callers' pipefail, turning a large matching diff into a skip (PR #50).
grep -E "$pattern" > /dev/null
