#!/usr/bin/env bash
#
# mock-api.sh -- build the proof cell's mock API image, push it, and record its digest for the
# platform root's next apply. It applies nothing.
#
# #1349. The mock API is a fixture of the proof cell (fixtures/MockApi), the extra container the
# proof-cell platform root runs beside the gateway when mock_api_image is set (#1342), and no part of
# the product: tools/scripts/deploy.sh builds the gateway alone, so the deploy an adopter's operator
# copies builds no fixture. This script is the mock API's half.
#
#   tools/scripts/poc/mock-api.sh
#   tools/scripts/deploy.sh                 # next: its apply loads the file this one writes
#
# In order, and it stops at the first that fails:
#   1. It reads the mock API's repository, ecrMockApi, from the outputs document, $SPA_OUTPUTS_FILE,
#      never from Terraform state (contracts/infra-outputs.schema.json).
#   2. It builds fixtures/MockApi/Dockerfile for ARM64, from the repository root, and pushes it under
#      a tag of this run's own, as deploy.sh builds the gateway: the same build, attestations
#      included (cell-common.sh's build_and_push, whose header deploy.sh's step 2 explains), and the
#      digest ECR names for the tag.
#   3. It writes that digest as mock_api_image to mock-api.auto.tfvars in the platform root,
#      $SPA_PLATFORM_ROOT. It runs no Terraform: the next apply of the root loads the file. So on the
#      proof cell one apply still moves both images: this script, then deploy.sh.
#
# Terraform loads *.auto.tfvars files in lexical order of their names, and a later file's value for a
# variable wins
# (https://developer.hashicorp.com/terraform/language/values/variables#variable-definition-precedence).
# An images.auto.tfvars that deploy.sh wrote before #1349 still sets mock_api_image until the next
# deploy.sh rewrites it, so this file is named to sort after it, and the first plan after the split
# applies this digest rather than that one. Like images.auto.tfvars it is git-ignored, by .gitignore's
# *.tfvars, since the digest's repository carries the account id; teardown.sh removes every
# *.auto.tfvars in the platform root with the root.
#
# Run it as the operator's own session, with credentials for the account the cell is in and
# AWS_REGION set to the cell's Region, with the proof cell's entry file sourced (infra/envs/poc/cell.env).
# It reads the entry values SPA_PLATFORM_ROOT and SPA_OUTPUTS_FILE (cell-common.sh's header), and
# refuses, exit 2, when one is unset, before anything is built. It needs the AWS CLI, python3, and
# Docker with buildx and the containerd image store (deploy.sh's header says why). Docker's output
# passes through cell-common.sh's redact_stream, so the account id becomes <account>.
#
# Exit status: 0 when the image is pushed and its digest recorded; 1 when a step failed; 2 when it
# did not start (a usage error, an entry value unset, a missing tool, no Region, no credentials, no
# outputs document or no ecrMockApi in it).
#
# Written for the bash 3.2 that ships with macOS, like tools/scripts/poc/certs.sh.
# Owner: workstream 1, AWS and IaC.

set -euo pipefail
umask 077

PROGRAM="mock-api"
# shellcheck source=tools/scripts/cell-common.sh
. "$(dirname -- "$0")/../cell-common.sh"

# The file in the platform root this script writes; the header says why it sorts after
# images.auto.tfvars. Its companion reads the name from here.
MOCK_API_TFVARS_NAME="mock-api.auto.tfvars"

usage() {
  # The header above, up to the Owner line, is this script's manual.
  sed -n '3,/^# Owner:/p' "$0" | sed 's/^# \{0,1\}//'
  cat <<EOF

Options
  -h, --help     This text.
EOF
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    -h | --help)
      usage
      exit 0
      ;;
    *) refuse "unknown argument '$1'. Try --help." ;;
  esac
done

need_entry SPA_PLATFORM_ROOT SPA_OUTPUTS_FILE

trap cell_cleanup EXIT
trap 'exit 130' INT TERM

need_tool docker "brew install --cask docker-desktop (decision D-2, docs/dependencies.md)"
docker buildx version > /dev/null 2>&1 || refuse "docker buildx does not answer. Start Docker Desktop."
cell_start

# --- 1. the repository -----------------------------------------------------------------------------

ECR_MOCK_API="$(outputs_field ecrMockApi)"

# --- 2. build and push -----------------------------------------------------------------------------

TAG="$(push_tag)"
ecr_login "$ECR_MOCK_API"
MOCK_API_IMAGE="$(build_and_push fixtures/MockApi/Dockerfile "$ECR_MOCK_API" "$TAG")" || exit 1
note "pushed the mock API as ${MOCK_API_IMAGE##*@}"

# --- 3. record it ----------------------------------------------------------------------------------

RECORD="${PLATFORM_ROOT}/${MOCK_API_TFVARS_NAME}"
{
  printf '# Written by tools/scripts/poc/mock-api.sh after its push. The next apply of this root loads\n'
  printf '# it after images.auto.tfvars, so its value wins (#1349). teardown.sh removes this file.\n'
  printf 'mock_api_image = "%s"\n' "$MOCK_API_IMAGE"
} > "$RECORD"
note "recorded the mock API in $(root_name "$RECORD"); the next apply of $(root_name "$PLATFORM_ROOT") moves it: run tools/scripts/deploy.sh"
