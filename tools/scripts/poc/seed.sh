#!/usr/bin/env bash
#
# seed.sh -- publish the fixture releases and write the first registry, through spactl.
#
# Task 1.7 (#58); step 4 of docs/plan/05-infrastructure-and-cost.md, First deployment: "seed.sh
# publishes the two fixture releases and writes the first registry". deploy.sh refuses to run
# before it, because a gateway started against an empty registry never becomes ready.
#
#   tools/scripts/poc/seed.sh
#   tools/scripts/poc/seed.sh --release 2026-10-20.1    # one release name for every app
#   tools/scripts/poc/seed.sh --shell fixtures/fragments/shell.json   # and a shell with its fragments
#
# In order:
#   1. It writes the outputs document, $SPA_OUTPUTS_FILE, from the outputs of the roots on
#      $SPA_ROOTS through the field map $SPA_FIELD_MAP (cell-common.sh's write_outputs), which is
#      where spactl reads the bucket and each app's client from.
#   2. It reads the platform configuration the gateway runs with out of its container's environment
#      in the service's task definition: the issuers and the policy that bound a registry entry at
#      run time are the ones spactl bounds it with here.
#   3. For each app definition in fixtures/apps/, the apps the apps root made a client for, it runs
#      `spactl release publish <appId> fixtures/spa/<appId> <release>` and then
#      `spactl app add fixtures/apps/<appId>.json --release <release>`. App 3 is onboarding's
#      (fixtures/onboarding/), never the seed's. A definition there with fragments is refused: a
#      fragment-bearing definition is seeded only when named, as step 4 says.
#   4. Only for each definition --shell names, a shell with fragments (#999, part of #961), and
#      only after every fragment's directory and entry are found: for each fragment, in the
#      definition's order, `spactl fragment publish <appId> <fragmentId>
#      fixtures/fragments/<appId>/<fragmentId> <release> --entry <entry>`, with <entry> the
#      definition's (entry.js when it names none); then the shell's release from fixtures/spa/<appId>
#      as in step 3; then one `spactl app add <definition> --release <shell release>
#      --fragment-release <fragmentId>=<release>,...` naming every fragment's release. A failed
#      publish stops the seed before that app add. Without --shell the seed writes no
#      fragment-bearing document: none reaches a cell until it runs the whole fragments build, and
#      the first such write is the G9 run's (#961, "The deployment prerequisite"). No runbook runs
#      step 4: docs/runbooks/deploy.md runs the seed without --shell, so steps 1 to 3 as before,
#      and the one run of step 4 is the G9 sitting's (#1011), whose ticket names it.
#
# The release is named for its content unless --release names it: fixture-<12 hex digits of the
# fixture's SHA-256>, per app and per fragment. A release prefix is written once (ADR-0009) and
# spactl's publish leaves identical bytes alone, so seeding again is a no-op, and a changed fixture
# gets a new release rather than a refused overwrite.
#
# spactl is `dotnet run --project tools/spactl`, unless SPACTL names an executable to run instead
# (a published spactl, say).
#
# Run it as the operator's own session, with credentials for the account the cell is in and
# AWS_REGION set to the cell's Region, and the proof cell's entry file, infra/envs/poc/cell.env,
# sourced beside it, after the three roots are applied, the platform root on the sample image. It
# reads the entry values SPA_ROOTS, SPA_FIELD_MAP and SPA_OUTPUTS_FILE, and refuses, exit 2, when one
# is unset. It needs the AWS CLI, Terraform, python3 and the .NET SDK.
#
# It never prints the account id.
#
# Exit status: 0 when every app is published and in the registry; 1 when one is not; 2 when it did
# not start (a usage error, an entry value unset, a missing tool, no Region, no credentials, a root
# not applied).
#
# Written for the bash 3.2 that ships with macOS, like tools/scripts/poc/certs.sh.
# Owner: workstream 1, AWS and IaC.

set -euo pipefail
umask 077

PROGRAM="seed"
# shellcheck source=tools/scripts/cell-common.sh
. "$(dirname -- "$0")/../cell-common.sh"

RELEASE=""
SHELLS=()

usage() {
  # The header above, up to the Owner line, is this script's manual.
  sed -n '3,/^# Owner:/p' "$0" | sed 's/^# \{0,1\}//'
  cat <<EOF

Options
  --release NAME   One release name for every app and fragment, in registry.schema.json's form:
                   1 to 64 letters, digits, dots, hyphens or underscores. Default: named for the
                   content.
  --shell FILE     An app definition with fragments to seed as step 4 says. Repeatable. Without
                   it no fragment is published and no fragment-bearing definition is written.
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
    --shell)
      [ "$#" -ge 2 ] || refuse "--shell needs a definition file. Try --help."
      [ -f "$2" ] || refuse "--shell names no file: '$2'."
      SHELLS+=("$2")
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

need_entry SPA_ROOTS SPA_FIELD_MAP SPA_OUTPUTS_FILE

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
FRAGMENTS_DIR="${REPO_ROOT}/fixtures/fragments"
TAB="$(printf '\t')"

# Each definition's fragments, one <appId><tab><fragmentId><tab><entry> line each, into a file.
fragments_of() {
  documents fragments "$1" > "$2" || refuse "$(root_name "$1") is not an app definition the seed can read."
}

# Before anything is published: no default definition has fragments, and every named shell has
# them, its release directory, and each fragment's directory with its entry at the root.
for definition in "$DEFINITIONS"/*.json; do
  [ -f "$definition" ] || continue
  fragments_of "$definition" "${WORK}/fragments"
  [ ! -s "${WORK}/fragments" ] ||
    fail "fixtures/apps/$(basename "$definition") has fragments. A fragment-bearing definition is seeded only when --shell names it (#961, \"The deployment prerequisite\")."
done
SHELL_COUNT=0
for definition in ${SHELLS[@]+"${SHELLS[@]}"}; do
  SHELL_COUNT=$((SHELL_COUNT + 1))
  fragments_of "$definition" "${WORK}/shell-${SHELL_COUNT}"
  [ -s "${WORK}/shell-${SHELL_COUNT}" ] || refuse "--shell ${definition} has no fragments: its app is seeded from fixtures/apps/ or onboarded."
  while IFS="$TAB" read -r app fragment entry <&3; do
    [ -d "${REPO_ROOT}/fixtures/spa/${app}" ] || fail "${app} has no release directory at fixtures/spa/${app}."
    [ -f "${FRAGMENTS_DIR}/${app}/${fragment}/${entry}" ] ||
      fail "fragment ${fragment} of ${app} has no ${entry} at fixtures/fragments/${app}/${fragment}."
  done 3< "${WORK}/shell-${SHELL_COUNT}"
done

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

# --- 4 ---------------------------------------------------------------------------------------------

SHELL_COUNT=0
for definition in ${SHELLS[@]+"${SHELLS[@]}"}; do
  SHELL_COUNT=$((SHELL_COUNT + 1))
  releases=""
  shell_app=""
  # read empties its variables at the end of the file, so the shell's appId is kept apart.
  while IFS="$TAB" read -r app fragment entry <&3; do
    shell_app="$app"
    directory="${FRAGMENTS_DIR}/${app}/${fragment}"
    release="$RELEASE"
    [ -n "$release" ] || release="$(documents fragment-release "$directory" "$entry")" ||
      fail "could not name a release for fixtures/fragments/${app}/${fragment}."
    note "publishing fixtures/fragments/${app}/${fragment} as fragment ${fragment} of ${app} release ${release}, entry ${entry}"
    spactl_shown fragment publish "$app" "$fragment" "$directory" "$release" --entry "$entry" --outputs "$OUTPUTS_FILE" ||
      fail "spactl could not publish fragment ${fragment} of ${app} release ${release}."
    releases="${releases:+${releases},}${fragment}=${release}"
  done 3< "${WORK}/shell-${SHELL_COUNT}"

  app="$shell_app"
  site="${REPO_ROOT}/fixtures/spa/${app}"
  release="$RELEASE"
  [ -n "$release" ] || release="$(documents release "$site")" || fail "could not name a release for fixtures/spa/${app}."
  note "publishing fixtures/spa/${app} as ${app} release ${release}"
  spactl_shown release publish "$app" "$site" "$release" --outputs "$OUTPUTS_FILE" ||
    fail "spactl could not publish ${app} release ${release}."
  note "adding ${app} to the registry at release ${release} with fragment releases ${releases}"
  spactl_shown app add "$definition" --release "$release" --fragment-release "$releases" --config "$CONFIG" --outputs "$OUTPUTS_FILE" ||
    fail "spactl could not add ${app} to the registry."
done

if [ "$SHELL_COUNT" -gt 0 ]; then
  note "pass: ${SEEDED} apps and ${SHELL_COUNT} shells with their fragments published and in the registry. tools/scripts/deploy.sh is next."
else
  note "pass: ${SEEDED} apps published and in the registry. tools/scripts/deploy.sh is next."
fi
