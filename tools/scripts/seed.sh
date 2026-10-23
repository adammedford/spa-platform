#!/usr/bin/env bash
#
# seed.sh -- publish the fixture releases and write the first registry, through spactl.
#
# Task 1.7 (#58); step 4 of docs/plan/05-infrastructure-and-cost.md, First deployment: "seed.sh
# publishes the two fixture releases and writes the first registry". deploy.sh refuses to run
# before it, because a gateway started against an empty registry never becomes ready.
#
#   tools/scripts/seed.sh
#   tools/scripts/seed.sh --release 2026-10-20.1    # one release name for every app
#
# In order:
#   1. It writes infra/envs/poc/outputs.json from the idp, apps and platform roots' outputs, which
#      is where spactl reads the bucket and each app's client from.
#   2. It reads the platform configuration the gateway runs with out of its container's environment
#      in the service's task definition: the issuers and the policy that bound a registry entry at
#      run time are the ones spactl bounds it with here.
#   3. For each app definition in fixtures/apps/, the apps the apps root made a client for, it runs
#      `spactl release publish <appId> fixtures/spa/<appId> <release>` and then
#      `spactl app add fixtures/apps/<appId>.json --release <release>`. App 3 is onboarding's
#      (fixtures/onboarding/), never the seed's.
#
# The release is named for its content unless --release names it: fixture-<12 hex digits of the
# fixture's SHA-256>. A release prefix is written once (ADR-0009) and spactl's publish leaves
# identical bytes alone, so seeding again is a no-op, and a changed fixture gets a new release
# rather than a refused overwrite.
#
# spactl is `dotnet run --project tools/spactl`, unless SPACTL names an executable to run instead
# (a published spactl, say).
#
# Run it as the operator's own session: the spa-platform profile, with tools/aws-project.env
# sourced, after the three roots are applied, the platform root on the sample image. It needs the
# AWS CLI, Terraform, python3 and the .NET SDK.
#
# It never prints the account id.
#
# Exit status: 0 when every app is published and in the registry; 1 when one is not; 2 when it did
# not start (a usage error, a missing tool, no Region, no credentials, a root not applied).
#
# Written for the bash 3.2 that ships with macOS, like tools/scripts/certs.sh.
# Owner: workstream 1, AWS and IaC.

set -euo pipefail
umask 077

PROGRAM="seed"
# shellcheck source=tools/scripts/cell-common.sh
. "$(dirname -- "$0")/cell-common.sh"

RELEASE=""

usage() {
  # The header above, up to the Owner line, is this script's manual.
  sed -n '3,/^# Owner:/p' "$0" | sed 's/^# \{0,1\}//'
  cat <<EOF

Options
  --release NAME   One release name for every app, in registry.schema.json's form: 1 to 64
                   letters, digits, dots, hyphens or underscores. Default: named for the content.
  -h, --help       This text.
EOF
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --release)
      [ "$#" -ge 2 ] || refuse "--release needs a value. Try --help."
      RELEASE="$2"
      shift
      ;;
    -h | --help)
      usage
      exit 0
      ;;
    *) refuse "unknown argument '$1'. Try --help." ;;
  esac
  shift
done

# contracts/registry.schema.json -> release: ^[A-Za-z0-9._-]+$, 1 to 64 characters.
if [ -n "$RELEASE" ]; then
  case "$RELEASE" in
    *[!A-Za-z0-9._-]*) refuse "--release must be letters, digits, dots, hyphens or underscores: got '${RELEASE}'" ;;
  esac
  [ "${#RELEASE}" -le 64 ] || refuse "--release must be at most 64 characters."
fi

trap cell_cleanup EXIT
trap 'exit 130' INT TERM

need_terraform
need_spactl
cell_start

# --- 1 and 2 ---------------------------------------------------------------------------------------

# Written from the three roots, then read back like any other reader of it: the scripts read the
# outputs document, never Terraform state (contracts/infra-outputs.schema.json).
write_outputs
CLUSTER="$(outputs_field ecsCluster)"
SERVICE="$(outputs_field ecsService)"
read_service
read_platform_config
CONFIG="${WORK}/platform-config.json"
note "bounding the registry with the configuration of task definition $(revision_of "$TASK_DEFINITION")"

# --- 3 ---------------------------------------------------------------------------------------------

DEFINITIONS="${REPO_ROOT}/fixtures/apps"
SEEDED=0
for definition in "$DEFINITIONS"/*.json; do
  [ -f "$definition" ] || continue
  app="$(basename "$definition" .json)"
  site="${REPO_ROOT}/fixtures/spa/${app}"
  [ -d "$site" ] || fail "fixtures/apps/${app}.json has no release directory at fixtures/spa/${app}."
  release="$RELEASE"
  [ -n "$release" ] || release="$(documents release "$site")" || fail "could not name a release for fixtures/spa/${app}."

  note "publishing fixtures/spa/${app} as ${app} release ${release}"
  spactl_shown release publish "$app" "$site" "$release" --outputs "$OUTPUTS_FILE" ||
    fail "spactl could not publish ${app} release ${release}."
  note "adding ${app} to the registry at release ${release}"
  spactl_shown app add "$definition" --release "$release" --config "$CONFIG" --outputs "$OUTPUTS_FILE" ||
    fail "spactl could not add ${app} to the registry."
  SEEDED=$((SEEDED + 1))
done

[ "$SEEDED" -gt 0 ] || fail "fixtures/apps/ holds no app definition."
note "pass: ${SEEDED} apps published and in the registry. tools/scripts/deploy.sh is next."
