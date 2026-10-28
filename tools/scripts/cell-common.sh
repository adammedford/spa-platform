# cell-common.sh -- what seed.sh, deploy.sh, rollback.sh, teardown.sh, logs.sh and tunnel.sh share.
# Sourced, never run.
#
# Task 1.7 (#58). The plumbing is tunnel.sh's and verify-release-immutability.sh's: every AWS call
# names the Region and --output text, a failure is reported by its error code and never by its
# message (which can carry an ARN), and nothing printed names the account id. The cell's documents
# are built and read by cell_documents.py beside this file, because bash 3.2 has no JSON reader.
#
# The caller sets PROGRAM before sourcing this, and calls cell_start once its arguments are read.
# tunnel.sh is the exception: it asks AWS only for its service's tasks, never who the caller is, so
# it sets REGION, WORK, OUT and ERR itself.
#
# Written for the bash 3.2 that ships with macOS, like tools/scripts/certs.sh.
# Owner: workstream 1, AWS and IaC.

# The AWS CLI pages a terminal's output through less unless told not to.
# https://docs.aws.amazon.com/cli/latest/userguide/cli-usage-pagination.html#cli-usage-pagination-clientside
export AWS_PAGER=""

# CDPATH empty for this cd, for the reason verify-release-immutability.sh gives (PR #130's review).
SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPO_ROOT="$(CDPATH='' cd -- "${SCRIPTS_DIR}/../.." && pwd -P)"
IDP_ROOT="${REPO_ROOT}/infra/envs/poc/idp"
APPS_ROOT="${REPO_ROOT}/infra/envs/poc/apps"
PLATFORM_ROOT="${REPO_ROOT}/infra/envs/poc/platform"
DOCUMENTS="${SCRIPTS_DIR}/cell_documents.py"

# contracts/infra-outputs.schema.json's own description: "infra/envs/poc/outputs.json, written by
# workstream 1 after every apply. The scripts and the deployed tests read it."
OUTPUTS_FILE="${REPO_ROOT}/infra/envs/poc/outputs.json"

# The images the platform root last applied, as Terraform variables. Terraform loads *.auto.tfvars
# after terraform.tfvars, so every later plain `terraform apply` in the root passes these back and
# never moves the image (plan row 1.7: "Every other apply passes the digest already in state").
# *.tfvars is ignored by git (change request 1.5): the values carry the account id.
# https://developer.hashicorp.com/terraform/language/values/variables#variable-definition-precedence
IMAGES_TFVARS="${PLATFORM_ROOT}/images.auto.tfvars"

# The gateway answers /readyz 200 once a registry snapshot is live, and the load balancer's health
# check follows the image (docs/plan/05-infrastructure-and-cost.md, First deployment, step 3).
READY_PATH="/readyz"

# The container names in infra/modules/cell/ecs.tf.
GATEWAY_CONTAINER="gateway"
MOCK_API_CONTAINER="mock-api"

note() { printf '%s: %s\n' "$PROGRAM" "$*"; }

# Did not start: a usage error, a missing tool, no Region, no credentials, a root not initialised.
refuse() {
  printf '%s: %s\n' "$PROGRAM" "$*" >&2
  exit 2
}

# Started, and stopped short of what it is for.
fail() {
  printf '%s: %s\n' "$PROGRAM" "$*" >&2
  exit 1
}

WORK=""
cell_cleanup() {
  status=$?
  if [ -n "$WORK" ] && [ -d "$WORK" ]; then
    rm -rf "$WORK"
  fi
  return "$status"
}

need_tool() {
  command -v "$1" > /dev/null 2>&1 || refuse "$1 is not on PATH. $2"
}

# The Region, the scratch directory and the caller. Sets REGION, WORK, OUT, ERR, ACCOUNT,
# CALLER_ROLE.
cell_start() {
  need_tool aws "brew install awscli"
  need_tool python3 "xcode-select --install"
  REGION="${AWS_REGION:-${AWS_DEFAULT_REGION:-}}"
  [ -n "$REGION" ] || refuse "no Region in AWS_REGION or AWS_DEFAULT_REGION. set -a; . tools/aws-project.env; set +a"
  WORK="$(mktemp -d "${TMPDIR:-/tmp}/${PROGRAM}.XXXXXX")"
  OUT="${WORK}/out"
  ERR="${WORK}/err"

  aws_call sts get-caller-identity --query '[Account, Arn]'
  [ "$OUTCOME" = "success" ] || refuse "no AWS credentials answered get-caller-identity (${OUTCOME}). Sign in with the spa-platform profile."
  ACCOUNT=""
  local caller_arn=""
  read -r ACCOUNT caller_arn < "$OUT" || true
  case "$ACCOUNT" in
    [0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]) ;;
    *) refuse "get-caller-identity named no 12-digit account id." ;;
  esac
  CALLER_ROLE=""
  case "$caller_arn" in
    arn:*:sts::*:assumed-role/*/*)
      CALLER_ROLE="${caller_arn#*:assumed-role/}"
      CALLER_ROLE="${CALLER_ROLE%%/*}"
      ;;
  esac
  # The teardown role deletes releases and may write nothing (ADR-0014). None of these scripts runs
  # as it; teardown.sh assumes it itself for the one thing it is for.
  case "$CALLER_ROLE" in
    spa-platform-*-teardown) refuse "the caller is a session of the teardown role ${CALLER_ROLE}. Run as the operator, with the spa-platform profile (ADR-0014)." ;;
  esac
}

# The account id never reaches the output: change request 1.5 keeps account identifiers out of the
# tracked tree, and this output is pasted into tickets in a public repository (#122's rule).
#
# Nor does a client id, once a script has called redact_clients: each app's client id in the outputs
# document becomes <client-id:appId>. The mock API's start line names both apps' clients, and the
# sitting of #58 put them on the terminal and into its log by tailing the log group directly (#208).
# A clientId may be any printable ASCII (contracts/registry.schema.json), so each value is replaced
# as text, never made into a pattern. Each line is flushed as it is written, so a followed log
# reaches a terminal or a tee as it arrives.
REDACTIONS=""

# After cell_start. Refuses, as outputs_field does, when the outputs document is missing or for
# another Region.
redact_clients() {
  [ -f "$OUTPUTS_FILE" ] ||
    refuse "there is no $(root_name "$OUTPUTS_FILE"). Run tools/scripts/seed.sh first: it writes it from the three roots."
  REDACTIONS="${WORK}/redactions"
  documents clients "$OUTPUTS_FILE" "$REGION" > "$REDACTIONS" ||
    refuse "$(root_name "$OUTPUTS_FILE") gives no clients for ${REGION}. Run tools/scripts/seed.sh again to rewrite it from the three roots."
}

redact() { printf '%s' "$1" | redact_stream; }

# ENVIRON rather than -v, which would read a backslash in a value as an escape.
# https://pubs.opengroup.org/onlinepubs/9799919799/utilities/awk.html (-v assignment, ENVIRON)
redact_stream() {
  REDACT_ACCOUNT="$ACCOUNT" REDACT_CLIENTS="$REDACTIONS" awk '
    function swap(line, term, label,    out, at) {
      out = ""
      while ((at = index(line, term)) > 0) {
        out = out substr(line, 1, at - 1) label
        line = substr(line, at + length(term))
      }
      return out line
    }
    BEGIN {
      count = 0
      file = ENVIRON["REDACT_CLIENTS"]
      if (file != "") {
        while ((getline entry < file) > 0) {
          tab = index(entry, "\t")
          count++
          labels[count] = "<client-id:" substr(entry, 1, tab - 1) ">"
          terms[count] = substr(entry, tab + 1)
        }
      }
      account = ENVIRON["REDACT_ACCOUNT"]
    }
    {
      line = $0
      for (i = 1; i <= count; i++) line = swap(line, terms[i], labels[i])
      if (account != "") line = swap(line, account, "<account>")
      print line
      fflush()
    }'
}

# --- one AWS call ----------------------------------------------------------------------------------
#
# Standard output lands in $OUT, standard error in $ERR, and OUTCOME says how AWS answered:
# "success", or the error code the CLI prints in "An error occurred (<Code>) when calling the
# <Operation> operation" (`aws help return-codes`), or "no answer from AWS".
#
# SESSION says whose credentials a call carries: the operator's, from the environment the script was
# started in, or the teardown role's, which teardown.sh exports to that one call's subshell, as
# verify-release-immutability.sh does. Exported rather than written as `env NAME=value aws`, which
# would put them in a process's arguments; and the CLI reads credentials from the environment before
# any profile. https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-envvars.html
SESSION="operator"
TEARDOWN_KEY_ID=""
TEARDOWN_SECRET=""
TEARDOWN_TOKEN=""

run_aws() {
  if [ "$SESSION" = "teardown" ]; then
    (
      unset AWS_PROFILE AWS_DEFAULT_PROFILE
      AWS_ACCESS_KEY_ID="$TEARDOWN_KEY_ID"
      AWS_SECRET_ACCESS_KEY="$TEARDOWN_SECRET"
      AWS_SESSION_TOKEN="$TEARDOWN_TOKEN"
      export AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY AWS_SESSION_TOKEN
      exec aws --region "$REGION" --output text "$@"
    )
  else
    aws --region "$REGION" --output text "$@"
  fi
}

# The error code in the CLI's standard error, $ERR, or nothing.
error_code() {
  awk 'match($0, /An error occurred \([^)]*\)/) { print substr($0, RSTART + 19, RLENGTH - 20); exit }' "$ERR"
}

aws_call() {
  if run_aws "$@" > "$OUT" 2> "$ERR"; then
    OUTCOME="success"
    return 0
  fi
  OUTCOME="$(error_code)"
  [ -n "$OUTCOME" ] || OUTCOME="no answer from AWS"
  return 0
}

# One call that must succeed: on failure it says which, and stops.
aws_read() {
  local what="$1"
  shift
  aws_call "$@"
  [ "$OUTCOME" = "success" ] || fail "${what}: ${OUTCOME}"
}

# The first line of the last call's output.
printed() { awk 'NR == 1' "$OUT"; }

# --- spactl ----------------------------------------------------------------------------------------
#
# spactl is `dotnet run --project tools/spactl`, unless SPACTL names an executable to run instead (a
# published spactl, say). seed.sh publishes and adds through it, and wait_for_revision waits with it.

need_spactl() {
  if [ -z "${SPACTL:-}" ]; then
    need_tool dotnet "brew install --cask dotnet-sdk"
  fi
}

spactl() {
  if [ -n "${SPACTL:-}" ]; then
    "$SPACTL" "$@"
  else
    dotnet run --project "${REPO_ROOT}/tools/spactl" -- "$@"
  fi
}

# spactl's own output, through the redaction, and its status kept.
spactl_shown() {
  local status=0
  spactl "$@" 2>&1 | redact_stream || status=$?
  return "$status"
}

# --- Terraform -------------------------------------------------------------------------------------

need_terraform() { need_tool terraform "brew install hashicorp/tap/terraform"; }

root_name() { printf '%s' "${1#"${REPO_ROOT}"/}"; }

# One output of a root, raw, from its state, or nothing when the root has none: never initialised,
# never applied, or destroyed. contracts/infra-outputs.schema.json says the scripts read
# outputs.json and none of them reads Terraform state, and the one exception among these four is
# teardown.sh's (change request 1.5a, decision 2; Q49 on #129, confirmed). platform-tfvars.sh calls
# it too, as a writer upstream of that document rather than a reader of it, like write_outputs below:
# it writes the platform root's inputs before that root's first apply, when outputs.json, which is
# written from all three roots, cannot exist yet (#470).
tf_output() {
  terraform -chdir="$1" output -no-color -raw "$2" 2> "$ERR" || true
}

# Terraform's own output, through the redaction: a plan names the bucket, the repositories and the
# images, and each carries the account id.
terraform_shown() {
  local status=0
  terraform "$@" 2>&1 | redact_stream || status=$?
  return "$status"
}

# --- the cell's documents --------------------------------------------------------------------------

documents() { python3 -I "$DOCUMENTS" "$@"; }

# A field of infra/envs/poc/outputs.json that a script cannot run without, in the contract's names
# (contracts/infra-outputs.schema.json). seed.sh writes the document first, and deploy.sh and
# rollback.sh rewrite it after their apply, so a script that reads it reads the last apply's.
outputs_field() {
  [ -f "$OUTPUTS_FILE" ] ||
    refuse "there is no $(root_name "$OUTPUTS_FILE"). Run tools/scripts/seed.sh first: it writes it from the three roots."
  documents field "$OUTPUTS_FILE" "$1" "$REGION" ||
    refuse "$(root_name "$OUTPUTS_FILE") gives no $1 for ${REGION}. Run tools/scripts/seed.sh again to rewrite it from the three roots."
}

# infra/envs/poc/outputs.json, from the three roots' outputs. After every apply.
write_outputs() {
  local root dir
  for root in idp apps platform; do
    case "$root" in
      idp) dir="$IDP_ROOT" ;;
      apps) dir="$APPS_ROOT" ;;
      platform) dir="$PLATFORM_ROOT" ;;
    esac
    terraform -chdir="$dir" output -no-color -json > "${WORK}/${root}-outputs.json" 2> "$ERR" ||
      refuse "could not read the ${root} root's outputs. Run terraform init -backend-config=backend.hcl in $(root_name "$dir")."
  done
  documents outputs "$REGION" "${WORK}/idp-outputs.json" "${WORK}/apps-outputs.json" \
    "${WORK}/platform-outputs.json" "$OUTPUTS_FILE" || fail "could not write $(root_name "$OUTPUTS_FILE")."
  note "wrote $(root_name "$OUTPUTS_FILE") from the idp, apps and platform roots' outputs"
}

# The service's current task definition: sets TASK_DEFINITION (its ARN) and TARGET_GROUP.
read_service() {
  aws_read "the service ${SERVICE} in ${CLUSTER}" ecs describe-services --cluster "$CLUSTER" --services "$SERVICE" \
    --query 'services[0].[taskDefinition, loadBalancers[0].targetGroupArn]'
  TASK_DEFINITION=""
  TARGET_GROUP=""
  IFS="$(printf '\t')" read -r TASK_DEFINITION TARGET_GROUP < "$OUT" || true
  case "$TASK_DEFINITION" in
    arn:*:task-definition/*:*) ;;
    *) fail "the service ${SERVICE} in ${CLUSTER} has no task definition. Is the platform root applied?" ;;
  esac
}

# The task definition's family:revision, which names it without the account.
revision_of() { printf '%s' "${1##*/}"; }

# One container's image in a task definition, or "None".
image_in() {
  aws_read "the task definition $(revision_of "$1")" ecs describe-task-definition --task-definition "$1" \
    --query "taskDefinition.containerDefinitions[?name=='$2'] | [0].image"
  printed
}

# The platform configuration the gateway runs with, in contracts/platform-config.schema.json's JSON
# form, read back from its container's environment in the service's task definition: that is what
# bounds the registry at run time, so it is what spactl bounds it with here. Writes
# $WORK/platform-config.json; sets REGISTRY_BUCKET and REGISTRY_KEY.
read_platform_config() {
  aws_read "the environment of task definition $(revision_of "$TASK_DEFINITION")" \
    ecs describe-task-definition --task-definition "$TASK_DEFINITION" \
    --query "taskDefinition.containerDefinitions[?name=='${GATEWAY_CONTAINER}'] | [0].environment[].[name, value]"
  cat "$OUT" > "${WORK}/environment.tsv"
  documents config "${WORK}/environment.tsv" "${REPO_ROOT}/contracts/platform-config.schema.json" \
    > "${WORK}/platform-config.json" || fail "the gateway container's environment is not a platform configuration."
  REGISTRY_BUCKET="$(awk -F '\t' '$1 == "Registry__Bucket" { print $2 }' "${WORK}/environment.tsv")"
  REGISTRY_KEY="$(awk -F '\t' '$1 == "Registry__Key" { print $2 }' "${WORK}/environment.tsv")"
}

# --- apply, wait, assert ---------------------------------------------------------------------------

# Plan and apply the platform root with these two images and the gateway's health path, then record
# them for every later apply. $1 the gateway image, $2 the mock API image or "".
apply_images() {
  local gateway="$1" mock="$2" plan="${WORK}/platform.tfplan"
  note "planning $(root_name "$PLATFORM_ROOT") with the gateway at $(redact "$gateway")"
  # The plan file holds the values the apply will write, so it stays in the scratch directory and
  # never in the tree (change request 1.1).
  terraform_shown -chdir="$PLATFORM_ROOT" plan -input=false -no-color -out="$plan" \
    -var "gateway_image=${gateway}" -var "mock_api_image=${mock}" -var "health_check_path=${READY_PATH}" ||
    fail "the platform root's plan failed; nothing was applied."
  terraform_shown -chdir="$PLATFORM_ROOT" apply -input=false -no-color "$plan" ||
    fail "the platform root's apply failed. Read its output above; $(root_name "$IMAGES_TFVARS") is unchanged."
  # Only after the apply: these are now the values in state, and the next apply passes them back.
  {
    printf '# Written by tools/scripts/%s.sh after its apply. Every later apply of this root passes\n' "$PROGRAM"
    printf '# these back, so it never moves the image (plan row 1.7). teardown.sh removes this file.\n'
    printf 'gateway_image     = "%s"\n' "$gateway"
    printf 'mock_api_image    = "%s"\n' "$mock"
    printf 'health_check_path = "%s"\n' "$READY_PATH"
  } > "$IMAGES_TFVARS"
  note "recorded the applied images in $(root_name "$IMAGES_TFVARS")"
  # The infrastructure outputs are written after every apply (contracts/infra-outputs.schema.json),
  # whatever the wait and the assertions find next.
  write_outputs
}

# Wait for a steady service, then assert that what runs is what was applied: $1 the gateway image
# by digest, repository@sha256:<hex>.
wait_and_assert() {
  local gateway="$1" digest="${1##*@}"
  note "waiting for ${SERVICE} in ${CLUSTER} to be stable"
  # services-stable polls every 15 seconds, 40 times, and exits 255 when it gives up.
  # https://awscli.amazonaws.com/v2/documentation/api/latest/reference/ecs/wait/services-stable.html
  aws_call ecs wait services-stable --cluster "$CLUSTER" --services "$SERVICE"
  # A waiter that gives up prints "Waiter ServicesStable failed: Max attempts exceeded" and no error
  # code, so the reason is named here rather than read from the CLI.
  [ "$OUTCOME" = "success" ] ||
    fail "${SERVICE} did not become stable within the waiter's ten minutes. The circuit breaker may be rolling it back: read the service's events."

  # Stable is not deployed: the circuit breaker rolls a failed deployment back to the previous task
  # definition, and the service is then stable on that one. The primary deployment has to be the
  # applied task definition, completed.
  # https://docs.aws.amazon.com/AmazonECS/latest/developerguide/deployment-circuit-breaker.html
  aws_read "the service ${SERVICE}" ecs describe-services --cluster "$CLUSTER" --services "$SERVICE" \
    --query 'services[0].[taskDefinition, deployments[?status==`PRIMARY`] | [0].rolloutState, length(deployments), loadBalancers[0].targetGroupArn]'
  local definition rollout count group
  IFS="$(printf '\t')" read -r definition rollout count group < "$OUT" || true
  [ "$rollout" = "COMPLETED" ] || fail "the primary deployment's rollout is ${rollout}, not COMPLETED."
  [ "$count" = "1" ] || fail "${SERVICE} has ${count} deployments; the previous one has not drained."

  local placed
  placed="$(image_in "$definition" "$GATEWAY_CONTAINER")" || exit 1
  [ "$placed" = "$gateway" ] ||
    fail "the service runs task definition $(revision_of "$definition"), whose gateway image is $(redact "$placed"), not the one applied. The circuit breaker rolled it back, or another apply ran."

  # Every running task's gateway container, by the digest ECS resolved when it started it, and the
  # task's private address, which is its target's id: the task runs in awsvpc mode and the target
  # group's target type is ip (infra/modules/cell/ecs.tf, alb.tf), whose target id is the address.
  # https://docs.aws.amazon.com/elasticloadbalancing/latest/APIReference/API_TargetDescription.html
  # https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_Container.html (imageDigest,
  # networkInterfaces)
  aws_read "the running tasks of ${SERVICE}" ecs list-tasks --cluster "$CLUSTER" --service-name "$SERVICE" \
    --desired-status RUNNING --query taskArns
  # shellcheck disable=SC2046
  set -- $(tr '\t' ' ' < "$OUT")
  [ "$#" -gt 0 ] || fail "${SERVICE} is stable with no running task."
  aws_read "the tasks of ${SERVICE}" ecs describe-tasks --cluster "$CLUSTER" --tasks "$@" \
    --query "tasks[].[taskArn, lastStatus, taskDefinitionArn, containers[?name=='${GATEWAY_CONTAINER}'] | [0].imageDigest, containers[?name=='${GATEWAY_CONTAINER}'] | [0].networkInterfaces[0].privateIpv4Address]"
  local arn status task_definition running address wrong="" addresses=""
  while IFS="$(printf '\t')" read -r arn status task_definition running address; do
    [ -n "$arn" ] || continue
    addresses="${addresses} ${arn##*/}=${address}"
    if [ "$status" != "RUNNING" ] || [ "$task_definition" != "$definition" ] || [ "$running" != "$digest" ]; then
      wrong="${wrong}  task ${arn##*/}: ${status}, $(revision_of "$task_definition"), gateway digest ${running}"$'\n'
    fi
  done < "$OUT"
  if [ -n "$wrong" ]; then
    printf '%s: a running task is not the applied gateway %s:\n%s' "$PROGRAM" "$digest" "$wrong" >&2
    exit 1
  fi

  # The target is healthy on /readyz, which answers 200 only once a registry snapshot is live
  # (contracts/pipeline.md): this is the "gateway build that answers /readyz" of the row's done-when.
  aws_read "the target group's health check" elbv2 describe-target-groups --target-group-arns "$group" \
    --query 'TargetGroups[0].HealthCheckPath'
  [ "$(printed)" = "$READY_PATH" ] || fail "the target group checks $(printed), not ${READY_PATH}."
  aws_read "the target group's targets" elbv2 describe-target-health --target-group-arn "$group" \
    --query 'TargetHealthDescriptions[].[Target.Id, TargetHealth.State]'
  # Every target healthy or draining, and nothing else: a list of the bad states would pass one it
  # left out. TargetHealth's State is initial, healthy, unhealthy, unhealthy.draining, unused,
  # draining or unavailable, and draining means the target is being deregistered:
  # https://docs.aws.amazon.com/elasticloadbalancing/latest/APIReference/API_TargetHealth.html
  # A stable service still lists the previous task's target as draining for the group's
  # deregistration delay, and on the first deployment that task, the sample image, also fails the
  # new /readyz check meanwhile; deploy.sh exited 1 on exactly that in the sitting of #58 (#207).
  # unhealthy.draining is not draining and still fails. A draining target is tolerated only as a
  # task that is going away: each running task's own target has to be healthy, below.
  local id state states="" healthy=" "
  while IFS="$(printf '\t')" read -r id state; do
    [ -n "$id" ] || continue
    states="${states} ${state}"
    if [ "$state" = "healthy" ]; then healthy="${healthy}${id} "; fi
  done < "$OUT"
  states="$(printf '%s' "$states" | awk '{$1 = $1; print}')"
  [ -n "$states" ] || fail "the target group has no target."
  # Word splitting on purpose: one state per word, and no state holds a space.
  # shellcheck disable=SC2086
  for state in $states; do
    case "$state" in
      healthy | draining) ;;
      *) fail "the target group's targets are: ${states}." ;;
    esac
  done
  # Each running task, all of them the applied gateway by the checks above, has its own target
  # healthy on the ready path: a healthy target that is no running task's does not stand in for it.
  local entry task found
  for entry in $addresses; do
    task="${entry%%=*}" address="${entry#*=}"
    case "$healthy" in
      *" ${address} "*) ;;
      *)
        state="not registered"
        while IFS="$(printf '\t')" read -r id found; do
          [ "$id" = "$address" ] && state="$found"
        done < "$OUT"
        fail "task ${task}'s target ${address} is not healthy on ${READY_PATH}: ${state}."
        ;;
    esac
  done

  # The image every reader of the outputs document is told runs, which apply_images rewrote from
  # the platform root's gateway_image output.
  local recorded
  recorded="$(outputs_field gatewayImage)"
  [ "$recorded" = "$gateway" ] || fail "the outputs document's gatewayImage is $(redact "$recorded"), not the image applied."
  note "steady: task definition $(revision_of "$definition") runs gateway ${digest}, healthy on ${READY_PATH}"
}

# --- convergence -----------------------------------------------------------------------------------

# Wait until every running task of $SERVICE in $CLUSTER has answered /readyz at revision $2 or later,
# read through the gateway origin $1. Change request P2-5, decision 6 (#309): the load balancer hands
# each read to the task it picks, so one answer at N says only that one task is at N.
#
# The tasks are the service's RUNNING ones, each named by its task id, the last segment of its ARN
# and never the ARN, which carries the account id and the Region (P2-5, decision 1). spactl wait
# reads through the one origin until each id has answered at $2 or later, and times out naming each
# one still behind or never answered: a task the load balancer never picks fails the wait rather
# than passing it. A task that starts during the wait is not listed and is not waited for; it reads
# the registry when it starts.
#
# It knows nothing of ECS states. A task ECS is stopping has desired status STOPPED and leaves the
# list, and a draining target receives no new requests, so neither is read here (P2-5, decision 6;
# desiredStatus in https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_ListTasks.html,
# deregistration delay in https://docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-target-groups.html#deregistration-delay).
# The 2-second poll and the 5-minute deadline are spactl's own.
#
# deploy.sh is its first caller. The ALB is internal, so from a workstation the origin is the
# tunnel's (tools/scripts/tunnel.sh), which the caller names.
wait_for_revision() {
  local origin="$1" revision="$2" arn ids="" status=0
  aws_read "the running tasks of ${SERVICE}" ecs list-tasks --cluster "$CLUSTER" --service-name "$SERVICE" \
    --desired-status RUNNING --query taskArns
  # Word splitting on purpose: --output text prints a list's strings tab-separated, a line for each
  # page the CLI fetched, and no ARN holds a space or a glob character.
  # https://docs.aws.amazon.com/cli/latest/userguide/cli-usage-output-format.html#text-output
  for arn in $(tr '\t' ' ' < "$OUT"); do
    ids="${ids:+${ids},}${arn##*/}"
  done
  [ -n "$ids" ] || fail "${SERVICE} has no running task to wait for."
  note "waiting through ${origin} until each running task reports revision ${revision} or later: ${ids}"
  spactl wait --url "$origin" --revision "$revision" --instances "$ids" > "$OUT" 2> "$ERR" || status=$?
  redact_stream < "$OUT"
  if [ "$status" -ne 0 ]; then
    redact_stream < "$ERR" >&2
    fail "not every running task of ${SERVICE} reports revision ${revision}. If nothing answered at ${origin}, the tunnel is not running or its task was replaced: restart tools/scripts/tunnel.sh."
  fi
}
