#!/usr/bin/env bash
# Which half of the repository a change touches. Owner: integrator.
#
#     git diff --name-only <base>...<head> | .github/touches.sh build       # exit 0 when .NET changed
#     git diff --name-only <base>...<head> | .github/touches.sh terraform   # exit 0 when infra/ changed
#
# .githooks/pre-push skips the build and Terraform when a push touches neither half, and
# .github/workflows/ci.yml skips the same two jobs for the same reason. Those were two copies of two
# patterns, and a copy agrees with its source until one of them is edited; this is the one place both
# read them from, so a push the hook built is a pull request CI builds, and a push the hook let
# through without one is a pull request CI lets through too.
#
# Written for the bash 3.2 that ships with macOS, and for grep -E rather than -P, which BSD grep
# does not have.

case "${1:-}" in
  # What compiles: the solution's projects live under src, tests, fixtures and tools/spactl, and the
  # build settings in the root files, which the suffixes catch wherever they sit.
  build) pattern='^(src|tests|fixtures|tools/spactl)/|\.(cs|csproj|props|targets|slnx)$|^global\.json$' ;;
  # Every Terraform root and module is under infra/.
  terraform) pattern='^infra/' ;;
  *)
    # An unknown half is reported and answered yes, not no: both callers skip a check on a no, so a
    # typo here has to cost a build rather than save one.
    echo "touches.sh: '${1:-}' is not a half; say build or terraform." >&2
    exit 0
    ;;
esac

grep -Eq "$pattern"
