#!/usr/bin/env bash
# Which half of the repository a change touches. Owner: integrator.
#
#     git diff --name-only <base>...<head> | .github/touches.sh build       # exit 0 when .NET changed
#     git diff --name-only <base>...<head> | .github/touches.sh terraform   # exit 0 when infra/ or the floor changed
#     git diff --name-only <base>...<head> | .github/touches.sh workflows   # exit 0 when .claude/workflows/ changed
#
# tools/check.sh, which .githooks/pre-push runs, skips the build and Terraform when a change touches
# neither half, and .github/workflows/ci.yml skips the same two jobs for the same reason. Those were
# two copies of two patterns, and a copy agrees with its source until one of them is edited; this is
# the one place both read them from. CI additionally runs both jobs when its workflow, this
# classifier, tools/smoke.sh or tools/image-check.sh changes, or when it cannot resolve the base;
# those overrides belong to the workflow's decision step. The workflow scripts' test companions are
# a third half, cheaper than the other two, which tools/check.sh --full and CI's Documents job run
# by the same answer (#276).
#
# Written for the bash 3.2 that ships with macOS, and for grep -E rather than -P, which BSD grep
# does not have.

case "${1:-}" in
  # Contracts are embedded by Platform.Registry and read by behavior tests; nuget.config controls
  # restore. Neither is documentation-only input (ADR-0020's CI filtering, PR #50). .dockerignore
  # decides what the gateway's image is built from, and the half builds that image (#210). The
  # analyzer configs set the severities the build enforces, so only a build proves a change to them:
  # a ramp batch's promotion deletes pending lines from the root .globalconfig and nothing else (#606).
  build) pattern='^(src|tests|fixtures|contracts|tools/spactl)/|\.(cs|csproj|props|targets|slnx|globalconfig)$|^(global\.json|nuget\.config|\.dockerignore|\.editorconfig)$' ;;
  # Every Terraform root and module is under infra/. The floor's file and the helper that reads it
  # decide which Terraform checks them, so raising the floor runs them too (#69).
  terraform) pattern='^infra/|^\.terraform-version$|^tools/terraform-floor\.sh$' ;;
  # A workflow script, a test companion or a fixture the companions read: the companions share the
  # fixtures, so any change in the folder runs every one of them (#276).
  workflows) pattern='^\.claude/workflows/' ;;
  *)
    # An unknown half is reported and answered yes, not no: both callers skip a check on a no, so a
    # typo here has to cost a build rather than save one.
    echo "touches.sh: '${1:-}' is not a half; say build, terraform or workflows." >&2
    exit 0
    ;;
esac

# Drain the complete input: grep -q would close the pipe at an early match and make printf fail
# with SIGPIPE under both callers' pipefail, turning a large matching diff into a skip (PR #50).
grep -E "$pattern" > /dev/null
