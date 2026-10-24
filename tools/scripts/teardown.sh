#!/usr/bin/env bash
#
# teardown.sh -- empty the platform bucket, destroy the three roots in reverse order, delete what
# Terraform leaves behind, and prove that nothing carrying the platform tag remains in the platform
# project (test P-05), by ending on tools/scripts/verify-teardown.sh (#308).
#
# Task 1.7 (#58); docs/plan/05-infrastructure-and-cost.md, Teardown: "teardown.sh empties the bucket
# and repositories, destroys the three roots in reverse order, deletes the imported certificate and
# the inactive task definitions, and then queries for the platform tag. Any resource still tagged
# fails the script. The tagging API can lag a deletion, so the query retries for a few minutes
# first. The state bucket goes last, by hand."
#
#   tools/scripts/teardown.sh --yes
#
# In order, and it stops at the first that fails:
#   1. The bucket is the platform root's `bucket` output, read from its state and never derived, so a
#      bucket_name override is honoured (change request 1.5a, decision 2; #58). Every version and
#      delete marker under apps/ is deleted as the teardown role, the one principal the bucket policy
#      lets delete a release (ADR-0014), and everything else, the registry's versions, as the
#      operator. The repositories need nothing: they are force_delete, so the destroy removes their
#      images (infra/modules/cell/storage.tf).
#   2. terraform destroy in infra/envs/poc/platform, then apps, then idp. images.auto.tfvars, which
#      deploy.sh wrote, goes with the platform root, so the next first deployment starts on the
#      sample image again.
#   3. The certificate certs.sh imported, found by its Platform and Cell tags, retried while ACM
#      still counts it in use after the listener went; the cell's inactive task definitions,
#      which keep their tags until deleted; and the key ring the gateway wrote under
#      /spa-platform/<cell>/data-protection/, which no root's state holds (P2-2 decision 17).
#   4. tools/scripts/verify-teardown.sh, and its exit status is this script's (#308). Its readers
#      look for Platform = spa-platform exactly (contracts/resource-tags.md): IAM roles and customer
#      managed policies by their tags, which the tagging API cannot list (#118); the cell's roles by
#      name; and the tag query in the selected Region, which drops the ECS records a correct destroy
#      leaves INACTIVE and the EC2 ids EC2 no longer knows (#118 step 2). Each retries for the wait
#      below. The bootstrap root's three resources carry spa-platform-bootstrap and do not answer;
#      they, and the state bucket, are removed by hand.
#
# --yes is required: nothing here can be undone.
#
# Run it as the operator's own session: the spa-platform profile, with tools/aws-project.env
# sourced, after `terraform init -backend-config=backend.hcl` in each root. It needs the AWS CLI,
# Terraform and python3. It never prints the account id: of a resource still tagged it names the
# ARN with the account replaced.
#
# Exit status: 0 when verify-teardown.sh passes; 1 when a step failed or it found something still
# standing; 2 when it, or verify-teardown.sh, did not start.
#
# Written for the bash 3.2 that ships with macOS, like tools/scripts/certs.sh.
# Owner: workstream 1, AWS and IaC.

set -euo pipefail
umask 077

PROGRAM="teardown"
# shellcheck source=tools/scripts/cell-common.sh
. "$(dirname -- "$0")/cell-common.sh"

CELL="${SPA_CELL:-poc}"
CONFIRMED=0
# contracts/resource-tags.md: the platform tag, matched exactly.
PLATFORM_TAG_KEY="Platform"
PLATFORM_TAG_VALUE="spa-platform"
# "Retries for a few minutes": five, every thirty seconds, unless the environment says otherwise.
# verify-teardown.sh reads the same two from the environment; they are checked here too, so that a
# bad value refuses before anything is destroyed.
TAG_WAIT_SECONDS="${SPA_TEARDOWN_TAG_WAIT:-300}"
TAG_POLL_SECONDS="${SPA_TEARDOWN_TAG_POLL:-30}"
# How long the certificate's delete retries while ACM still counts it in use, polling as the tag
# query does. Twenty minutes, the AWS provider's own bound for the same wait (step 3).
CERT_WAIT_SECONDS="${SPA_TEARDOWN_CERT_WAIT:-1200}"

usage() {
  # The header above, up to the Owner line, is this script's manual.
  sed -n '3,/^# Owner:/p' "$0" | sed 's/^# \{0,1\}//'
  cat <<EOF

Options
  --yes         Required: tear the cell down.
  --cell NAME   The cell: its teardown role, its certificate's Cell tag and its task definition
                family. Default: \$SPA_CELL, else poc.
  -h, --help    This text.

Environment
  SPA_TEARDOWN_TAG_WAIT   Seconds verify-teardown.sh's readers retry for. Default 300.
  SPA_TEARDOWN_TAG_POLL   Seconds between their attempts, and the certificate delete's. Default 30.
  SPA_TEARDOWN_CERT_WAIT  Seconds the certificate delete retries while ACM says it is in use.
                          Default 1200.
EOF
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --yes) CONFIRMED=1 ;;
    --cell)
      [ "$#" -ge 2 ] || refuse "--cell needs a value. Try --help."
      CELL="$2"
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

[ "$CONFIRMED" -eq 1 ] || refuse "this destroys the cell and cannot be undone. Run it again with --yes."
# The cell's lexical form, as tools/scripts/certs.sh checks it.
case "$CELL" in
  *[!a-z0-9-]* | "" | -* | *-) refuse "--cell must be lowercase letters, digits and inner hyphens: got '${CELL}'" ;;
esac
for value in "$TAG_WAIT_SECONDS" "$TAG_POLL_SECONDS" "$CERT_WAIT_SECONDS"; do
  case "$value" in
    '' | *[!0-9]*) refuse "SPA_TEARDOWN_TAG_WAIT, SPA_TEARDOWN_TAG_POLL and SPA_TEARDOWN_CERT_WAIT are whole seconds: got '${value}'" ;;
  esac
done

trap cell_cleanup EXIT
trap 'exit 130' INT TERM

need_terraform
cell_start

# --- 1. the bucket ---------------------------------------------------------------------------------

BUCKET="$(tf_output "$PLATFORM_ROOT" bucket)"
GATEWAY_IMAGE="$(tf_output "$PLATFORM_ROOT" gateway_image)"

# One call as the teardown role: its credentials go to that one call's environment and nowhere else
# (cell-common.sh, run_aws).
as_teardown() {
  SESSION="teardown"
  aws_call "$@"
  SESSION="operator"
}

assume_teardown_role() {
  local role="spa-platform-${CELL}-teardown" credentials
  # The name is derived from the cell and the ARN is the one IAM returns for it (ADR-0014 item 2).
  aws_read "the teardown role ${role}" iam get-role --role-name "$role" --query Role.Arn
  # Captured into variables and never into a file or the output. The three fields are an access key
  # id and two base64 strings, so they split on the CLI's tabs and hold nothing to glob.
  credentials="$(aws --region "$REGION" --output text sts assume-role --role-arn "$(printed)" \
    --role-session-name "teardown-$(date -u +%Y%m%dT%H%M%SZ)" \
    --query 'Credentials.[AccessKeyId, SecretAccessKey, SessionToken]' 2> "$ERR")" ||
    fail "could not assume the teardown role ${role}: $(error_code)"
  set -f
  # shellcheck disable=SC2086
  set -- $credentials
  set +f
  TEARDOWN_KEY_ID="${1:-}"
  TEARDOWN_SECRET="${2:-}"
  TEARDOWN_TOKEN="${3:-}"
  [ -n "$TEARDOWN_TOKEN" ] || fail "assume-role answered the teardown role without credentials."
}

if [ -z "$BUCKET" ]; then
  note "the platform root has no bucket output, so there is no bucket to empty: the root is destroyed already, or was never applied"
else
  note "emptying the bucket $(redact "$BUCKET"), from the platform root's state"
  aws_read "the versions in the bucket" s3api list-object-versions --bucket "$BUCKET" \
    --query '[Versions, DeleteMarkers][].[Key, VersionId]'
  cat "$OUT" > "${WORK}/versions"
  releases=0
  others=0
  tab="$(printf '\t')"
  while IFS="$tab" read -r key version; do
    [ -n "$key" ] || continue
    case "$key" in
      apps/*) releases=$((releases + 1)) ;;
      *) others=$((others + 1)) ;;
    esac
  done < "${WORK}/versions"

  if [ "$releases" -gt 0 ]; then
    assume_teardown_role
  fi
  deleted=0
  while IFS="$tab" read -r key version; do
    [ -n "$key" ] || continue
    case "$key" in
      apps/*) as_teardown s3api delete-object --bucket "$BUCKET" --key "$key" --version-id "$version" ;;
      *) aws_call s3api delete-object --bucket "$BUCKET" --key "$key" --version-id "$version" ;;
    esac
    [ "$OUTCOME" = "success" ] || fail "could not delete version ${version} of ${key}: ${OUTCOME}"
    deleted=$((deleted + 1))
  done < "${WORK}/versions"
  TEARDOWN_KEY_ID=""
  TEARDOWN_SECRET=""
  TEARDOWN_TOKEN=""

  aws_read "the versions left in the bucket" s3api list-object-versions --bucket "$BUCKET" \
    --query '[length(Versions || `[]`), length(DeleteMarkers || `[]`)]'
  left_versions=""
  left_markers=""
  read -r left_versions left_markers < "$OUT" || true
  [ "$left_versions" = "0" ] && [ "$left_markers" = "0" ] ||
    fail "the bucket still holds ${left_versions} versions and ${left_markers} delete markers."
  note "deleted ${deleted} versions and delete markers: ${releases} under apps/ as the teardown role, ${others} as the operator"
fi

# --- 2. the roots, in reverse order -----------------------------------------------------------------

destroy() {
  local dir="$1"
  shift
  note "destroying $(root_name "$dir")"
  terraform_shown -chdir="$dir" destroy -auto-approve -input=false -no-color "$@" ||
    fail "terraform destroy in $(root_name "$dir") failed. Read its output above; nothing after it ran."
}

# gateway_image has no default, and destroy evaluates the configuration, so it is passed back from
# state, or from images.auto.tfvars and terraform.tfvars when the output is gone.
if [ -n "$GATEWAY_IMAGE" ]; then
  destroy "$PLATFORM_ROOT" -var "gateway_image=${GATEWAY_IMAGE}"
else
  destroy "$PLATFORM_ROOT"
fi
rm -f "$IMAGES_TFVARS"
destroy "$APPS_ROOT"
destroy "$IDP_ROOT"

# --- 3. what Terraform leaves -----------------------------------------------------------------------

# certs.sh tags the certificate in the call that imports it (contracts/resource-tags.md). The
# listener that held it went with the platform root, but ACM can go on counting the certificate in
# use for a while after, and refuses the delete with ResourceInUseException until it stops. The AWS
# provider retries exactly that error on its own certificate deletes, for up to twenty minutes
# (terraform-provider-aws v6.68.0, internal/service/acm/certificate.go, resourceCertificateDelete
# and certificateCrossServicePropagationTimeout), so this does too. A certificate already gone,
# ResourceNotFoundException, is one the tagging API still listed: it counts as deleted.
# https://docs.aws.amazon.com/acm/latest/APIReference/API_DeleteCertificate.html
delete_certificate() {
  local arn="$1" waited=0
  while :; do
    aws_call acm delete-certificate --certificate-arn "$arn"
    case "$OUTCOME" in
      success)
        note "deleted the certificate ${arn##*/}"
        return 0
        ;;
      ResourceNotFoundException)
        note "the certificate ${arn##*/} was deleted already"
        return 0
        ;;
      ResourceInUseException)
        [ "$waited" -lt "$CERT_WAIT_SECONDS" ] ||
          fail "the certificate ${arn##*/} was still in use after ${CERT_WAIT_SECONDS} seconds. Read which resource holds it in the ACM console, then run teardown.sh --yes again."
        sleep "$TAG_POLL_SECONDS"
        waited=$((waited + TAG_POLL_SECONDS))
        [ "$TAG_POLL_SECONDS" -gt 0 ] || waited=$((waited + 1))
        ;;
      *) fail "could not delete the certificate ${arn##*/}: ${OUTCOME}" ;;
    esac
  done
}

aws_read "the cell's imported certificates" resourcegroupstaggingapi get-resources \
  --resource-type-filters acm:certificate \
  --tag-filters "Key=${PLATFORM_TAG_KEY},Values=${PLATFORM_TAG_VALUE}" "Key=Cell,Values=${CELL}" \
  --query 'ResourceTagMappingList[].ResourceARN'
for arn in $(tr '\t' ' ' < "$OUT"); do
  delete_certificate "$arn"
done

# A replaced or destroyed revision is deregistered, INACTIVE, and keeps its tags until it is deleted.
# Exact family: the prefix spa-platform-poc would also match spa-platform-poc2's. Ten per call, the
# API's limit. https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_DeleteTaskDefinitions.html
FAMILY="spa-platform-${CELL}"
aws_read "the inactive task definitions of ${FAMILY}" ecs list-task-definitions --family-prefix "$FAMILY" \
  --status INACTIVE --query taskDefinitionArns
batch=""
count=0
removed=0
flush() {
  [ -n "$batch" ] || return 0
  # shellcheck disable=SC2086
  aws_call ecs delete-task-definitions --task-definitions $batch
  [ "$OUTCOME" = "success" ] || fail "could not delete the inactive task definitions: ${OUTCOME}"
  removed=$((removed + count))
  batch=""
  count=0
}
for arn in $(tr '\t' ' ' < "$OUT"); do
  case "${arn##*/}" in
    "${FAMILY}":[0-9]*) ;;
    *) continue ;;
  esac
  batch="${batch} ${arn}"
  count=$((count + 1))
  [ "$count" -lt 10 ] || flush
done
flush
note "deleted ${removed} inactive task definitions of ${FAMILY}"

# The cell's data-protection key ring (P2-2 decision 17). The gateway writes it at run time under
# /spa-platform/{cell}/data-protection/, the one prefix infra/modules/cell's
# data_protection_parameter_prefix admits, so no root's state holds it and no destroy removes it; a
# cell rebuilt under the same name would load the old ring. Deleted here, after the platform root,
# so that no task is left to write a new key, and before the proof, which would otherwise find the
# parameters by their platform tag (#274). An empty prefix, a memory cell or a second teardown, is
# nothing to delete. The task role cannot delete there (infra/modules/cell/iam.tf); the operator can.
# The path takes no trailing slash, and the CLI pages the listing on its own. Ten names a call, the
# API's limit; a name already gone is answered under InvalidParameters, not as an error.
# https://docs.aws.amazon.com/systems-manager/latest/APIReference/API_GetParametersByPath.html
# https://docs.aws.amazon.com/systems-manager/latest/APIReference/API_DeleteParameters.html
RING_PREFIX="/spa-platform/${CELL}/data-protection/"
aws_read "the key ring parameters under ${RING_PREFIX}" ssm get-parameters-by-path \
  --path "${RING_PREFIX%/}" --recursive --query 'Parameters[].Name'
names=""
count=0
ring_deleted=0
flush_ring() {
  [ -n "$names" ] || return 0
  # shellcheck disable=SC2086
  aws_call ssm delete-parameters --names $names --query 'length(DeletedParameters)'
  [ "$OUTCOME" = "success" ] || fail "could not delete the key ring parameters under ${RING_PREFIX}: ${OUTCOME}"
  ring_deleted=$((ring_deleted + count))
  names=""
  count=0
}
for name in $(tr '\t' ' ' < "$OUT"); do
  case "$name" in
    "$RING_PREFIX"*) ;;
    *) continue ;;
  esac
  names="${names} ${name}"
  count=$((count + 1))
  [ "$count" -lt 10 ] || flush_ring
done
flush_ring
note "deleted ${ring_deleted} key ring parameters under ${RING_PREFIX}"

# --- 4. the proof ------------------------------------------------------------------------------------

# verify-teardown.sh reads SPA_TEARDOWN_TAG_WAIT and SPA_TEARDOWN_TAG_POLL from the environment this
# script was started with. It runs under this bash, $BASH, rather than through its #! line, so that
# it runs under the same one. Not exec'd: the EXIT trap still removes this script's scratch directory.
note "proving nothing carrying ${PLATFORM_TAG_KEY}=${PLATFORM_TAG_VALUE} remains: tools/scripts/verify-teardown.sh"
status=0
"$BASH" "${SCRIPTS_DIR}/verify-teardown.sh" --cell "$CELL" || status=$?
exit "$status"
