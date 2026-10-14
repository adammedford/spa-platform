#!/usr/bin/env bash
#
# outputs-from-terraform.sh — the identity fields of the infra outputs document, for the G0 Cognito run.
#
# Task 1.7 writes infra/envs/poc/outputs.json after every apply, and the harness reads that when it exists.
# Before the platform root is applied it cannot exist in full, so this assembles the fields the harness reads
# (region, cell, issuer, cognitoDomain, clients, testUsers) from the identity and apps roots' own outputs, in the
# shape of contracts/infra-outputs.schema.json. No output of either root carries a secret or a password: the
# document names parameters only. The file it writes, outputs.local.json, is ignored by git (*.local.json).
#
#   spikes/g0/cognito/outputs-from-terraform.sh
#
# Needs terraform, jq and the AWS credentials that read the state bucket. Reads only.

set -euo pipefail

root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
out="${root}/spikes/g0/cognito/outputs.local.json"

for tool in terraform jq; do
  command -v "$tool" >/dev/null 2>&1 || { echo "outputs-from-terraform.sh: $tool is not installed" >&2; exit 1; }
done

idp="$(terraform -chdir="${root}/infra/envs/poc/idp" output -json)"
apps="$(terraform -chdir="${root}/infra/envs/poc/apps" output -json)"

jq -n --argjson idp "$idp" --argjson apps "$apps" '{
  region:        $idp.region.value,
  cell:          $idp.cell.value,
  issuer:        $idp.issuer.value,
  cognitoDomain: $idp.cognito_domain.value,
  clients:       $apps.clients.value,
  testUsers:     $idp.test_users.value
}' >"$out"

echo "wrote ${out#"$root"/}: $(jq -r '.clients | keys | join(", ")' "$out") clients, $(jq -r '.testUsers | keys | join(", ")' "$out") test users"
