#!/usr/bin/env bash
#
# rollback.sh -- put the previous gateway back: apply the images of the previous task definition,
# wait for a steady service, and assert that the running gateway is that one.
#
# Task 1.7 (#58); the Gateway lever's rollback in docs/plan/05-infrastructure-and-cost.md,
# Deployment and rollback: "rollback.sh applies the digest of the previous task definition. The
# deployment circuit breaker also rolls back on failed health checks."
#
#   tools/scripts/rollback.sh                    # the previous gateway image
#   tools/scripts/rollback.sh --to-revision 7    # the images of revision 7 of the family
#
# The previous task definition is the newest revision below the service's own whose gateway image
# differs from the one running: an apply that changed something else made a new revision with the
# same image, and rolling back to it would change nothing. Its mock API image comes with it, since
# the two were deployed together. Each revision is read by family:revision, so a deregistered one,
# which every revision the platform root replaced is, is read as well as an active one.
#
# It refuses a revision whose gateway image is not a build in the cell's gateway repository: the
# sample image of the first apply answers / and not /readyz, so going back to it is a platform-root
# apply with health_check_path = "/", not a rollback. It refuses an image the repository no longer
# holds, since the task could not pull it.
#
# Then it does what deploy.sh does after its push: it plans and applies infra/envs/poc/platform with
# both images and the health check on /readyz, records them in images.auto.tfvars there, writes
# infra/envs/poc/outputs.json, waits for a stable service, and asserts the running digest and the
# targets' health as deploy.sh's step 5 does, tolerating the previous task's draining target.
#
# The cell's names come from infra/envs/poc/outputs.json, which seed.sh wrote, and never from
# Terraform state (contracts/infra-outputs.schema.json).
#
# Run it as the operator's own session: the spa-platform profile, with tools/aws-project.env
# sourced. It needs the AWS CLI, Terraform and python3. It never prints the account id.
#
# Exit status: 0 when the previous gateway runs steady and healthy; 1 when there is none to go back
# to, or a step failed; 2 when it did not start (no outputs document among the reasons).
#
# Written for the bash 3.2 that ships with macOS, like tools/scripts/certs.sh.
# Owner: workstream 1, AWS and IaC.

set -euo pipefail
umask 077

PROGRAM="rollback"
# shellcheck source=tools/scripts/cell-common.sh
. "$(dirname -- "$0")/cell-common.sh"

TO_REVISION=""
# How far back it looks for a different image. A revision is one apply, and twenty applies with one
# gateway image is more than Phase 1 has reason for.
LOOKBACK=20

usage() {
  # The header above, up to the Owner line, is this script's manual.
  sed -n '3,/^# Owner:/p' "$0" | sed 's/^# \{0,1\}//'
  cat <<EOF

Options
  --to-revision N   Roll back to revision N of the service's task definition family.
  -h, --help        This text.
EOF
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --to-revision)
      [ "$#" -ge 2 ] || refuse "--to-revision needs a value. Try --help."
      TO_REVISION="$2"
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

if [ -n "$TO_REVISION" ]; then
  case "$TO_REVISION" in
    '' | *[!0-9]* | 0*) refuse "--to-revision must be a revision number: got '${TO_REVISION}'" ;;
  esac
fi

trap cell_cleanup EXIT
trap 'exit 130' INT TERM

need_terraform
cell_start

# From the outputs document, never from state (contracts/infra-outputs.schema.json).
ECR_GATEWAY="$(outputs_field ecrGateway)"
CLUSTER="$(outputs_field ecsCluster)"
SERVICE="$(outputs_field ecsService)"

read_service
CURRENT="$(revision_of "$TASK_DEFINITION")"
FAMILY="${CURRENT%:*}"
REVISION="${CURRENT##*:}"
RUNNING_IMAGE="$(image_in "$TASK_DEFINITION" "$GATEWAY_CONTAINER")" || exit 1
note "the service runs ${CURRENT}, gateway ${RUNNING_IMAGE##*@}"

# --- which revision ---------------------------------------------------------------------------------

# A revision's gateway image, or nothing when ECS no longer describes it (deleted).
gateway_of_revision() {
  aws_call ecs describe-task-definition --task-definition "${FAMILY}:$1" \
    --query "taskDefinition.containerDefinitions[?name=='${GATEWAY_CONTAINER}'] | [0].image"
  if [ "$OUTCOME" = "success" ]; then printed; fi
}

TARGET=""
TARGET_IMAGE=""
if [ -n "$TO_REVISION" ]; then
  [ "$TO_REVISION" != "$REVISION" ] || fail "the service already runs revision ${TO_REVISION}."
  TARGET="$TO_REVISION"
  TARGET_IMAGE="$(gateway_of_revision "$TARGET")"
  [ -n "$TARGET_IMAGE" ] || fail "ECS does not describe ${FAMILY}:${TARGET}: it was deleted, or never registered."
else
  candidate=$((REVISION - 1))
  floor=$((REVISION - LOOKBACK))
  while [ "$candidate" -ge 1 ] && [ "$candidate" -gt "$floor" ]; do
    image="$(gateway_of_revision "$candidate")"
    if [ -n "$image" ] && [ "$image" != "$RUNNING_IMAGE" ]; then
      TARGET="$candidate"
      TARGET_IMAGE="$image"
      break
    fi
    candidate=$((candidate - 1))
  done
  [ -n "$TARGET" ] || fail "no revision of ${FAMILY} in the ${LOOKBACK} below ${REVISION} runs another gateway image: there is nothing to roll back to."
fi

# A build in the cell's gateway repository, by digest; anything else is not a gateway this lever can
# restore.
case "$TARGET_IMAGE" in
  "${ECR_GATEWAY}"@sha256:*) ;;
  *) fail "${FAMILY}:${TARGET} runs $(redact "$TARGET_IMAGE"), not a gateway build from $(redact "$ECR_GATEWAY"). Going back to the sample image is a platform-root apply with health_check_path = \"/\", not a rollback." ;;
esac
aws_call ecr describe-images --repository-name "${ECR_GATEWAY#*/}" --image-ids "imageDigest=${TARGET_IMAGE##*@}" \
  --query 'imageDetails[0].imageDigest'
[ "$OUTCOME" = "success" ] ||
  fail "the gateway repository no longer holds ${TARGET_IMAGE##*@} (${OUTCOME}), so a task could not pull it."

aws_read "the mock API image of ${FAMILY}:${TARGET}" ecs describe-task-definition --task-definition "${FAMILY}:${TARGET}" \
  --query "taskDefinition.containerDefinitions[?name=='${MOCK_API_CONTAINER}'] | [0].image"
MOCK_API_IMAGE="$(printed)"
[ "$MOCK_API_IMAGE" != "None" ] || MOCK_API_IMAGE=""

note "rolling back to ${FAMILY}:${TARGET}, gateway ${TARGET_IMAGE##*@}"

# --- apply, wait, assert ---------------------------------------------------------------------------

apply_images "$TARGET_IMAGE" "$MOCK_API_IMAGE"
wait_and_assert "$TARGET_IMAGE"
note "pass: the gateway ${TARGET_IMAGE##*@} of ${FAMILY}:${TARGET} runs steady in ${CLUSTER} and answers ${READY_PATH}"
