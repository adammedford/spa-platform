#!/usr/bin/env bash
#
# tunnel.sh -- a Session Manager port forward from this workstation, through the cell's gateway
# task, to the internal load balancer on 443.
#
# Task 1.6b (#57). The ALB is internal and no bastion exists, so the gateway task is the hop
# (docs/plan/05-infrastructure-and-cost.md, Workstation access). The tunnel carries TCP: the
# browser's TLS handshake reaches the ALB unchanged, and the wildcard certificate certs.sh imported
# is the one presented.
#
#   tools/scripts/tunnel.sh                      # forward local port 8443 to the ALB on 443
#   tools/scripts/tunnel.sh --local-port 9443    # another local port
#
# It stays in the foreground until the session ends; Ctrl-C ends it. In a second terminal,
# tools/scripts/verify-tunnel.sh <path> checks it: / on the sample image, /healthz on the gateway.
#
# It finds what it forwards through by the names the cell module gives them, read from AWS, not
# from Terraform state: the cluster and the load balancer are spa-<cell>, the service is gateway
# and the hop is its gateway container (infra/modules/cell/ecs.tf and alb.tf, and
# contracts/infra-outputs.example.json). Each name can be given instead.
#
# Run it as the operator's own session: the spa-platform profile, with tools/aws-project.env
# sourced. It needs the AWS CLI v2 and the Session Manager plugin.
#
# It never prints the account id: of a task it names only the id, never the ARN.
#
# Exit status: the session's, once it has started; 1 when the cell has no load balancer or no task
# a session can reach; 2 when it did not start (a usage error, a missing tool, no Region, or the
# local port already held).
#
# Written for the bash 3.2 that ships with macOS, like tools/scripts/certs.sh.
# Owner: workstream 1, AWS and IaC.

set -euo pipefail
umask 077

PROGRAM="tunnel"
CELL="${SPA_CELL:-poc}"
LOCAL_PORT="${SPA_TUNNEL_PORT:-8443}"
CLUSTER=""
SERVICE=""
ALB=""

# The hop. The task has two containers and ECS Exec reaches both, but the plan names the gateway
# task as the hop and the gateway container is the one the ALB's target group sends to.
CONTAINER="gateway"

# The AWS CLI pages a terminal's output through less unless told not to.
# https://docs.aws.amazon.com/cli/latest/userguide/cli-usage-pagination.html#cli-usage-pagination-clientside
export AWS_PAGER=""

note() { printf '%s: %s\n' "$PROGRAM" "$*"; }

refuse() {
  printf '%s: %s\n' "$PROGRAM" "$*" >&2
  exit 2
}

fail() {
  printf '%s: %s\n' "$PROGRAM" "$*" >&2
  exit 1
}

usage() {
  # The header above, up to the Owner line, is this script's manual.
  sed -n '3,/^# Owner:/p' "$0" | sed 's/^# \{0,1\}//'
  cat <<EOF

Options
  --local-port N   The workstation port the Session Manager plugin listens on. Default:
                   \$SPA_TUNNEL_PORT, else 8443, the port in every app's registry host and
                   callback URL (docs/plan/05-infrastructure-and-cost.md, step 5).
  --cell NAME      The cell. Default: \$SPA_CELL, else poc.
  --cluster NAME   The ECS cluster. Default: spa-<cell>.
  --service NAME   The ECS service. Default: gateway.
  --alb DNSNAME    The load balancer's DNS name. Default: looked up as spa-<cell>.
  -h, --help       This text.
EOF
}

WORK=""
cleanup() {
  status=$?
  if [ -n "$WORK" ] && [ -d "$WORK" ]; then
    rm -rf "$WORK"
  fi
  return "$status"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

value_of() {
  [ "$2" -ge 2 ] || refuse "$1 needs a value. Try --help."
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --local-port) value_of "$1" "$#"; LOCAL_PORT="$2"; shift ;;
    --cell) value_of "$1" "$#"; CELL="$2"; shift ;;
    --cluster) value_of "$1" "$#"; CLUSTER="$2"; shift ;;
    --service) value_of "$1" "$#"; SERVICE="$2"; shift ;;
    --alb) value_of "$1" "$#"; ALB="$2"; shift ;;
    -h | --help)
      usage
      exit 0
      ;;
    *) refuse "unknown argument '$1'. Try --help." ;;
  esac
  shift
done

# The cell's lexical form, as tools/scripts/certs.sh checks it.
case "$CELL" in
  *[!a-z0-9-]* | "" | -* | *-) refuse "--cell must be lowercase letters, digits and inner hyphens: got '${CELL}'" ;;
esac
case "$LOCAL_PORT" in
  '' | *[!0-9]* | 0*) refuse "--local-port must be a port number: got '${LOCAL_PORT}'" ;;
esac
[ "$LOCAL_PORT" -le 65535 ] || refuse "--local-port must be a port number: got '${LOCAL_PORT}'"

# infra/modules/cell: ecs.tf's cluster_name and service_name, alb.tf's lb_name.
[ -n "$CLUSTER" ] || CLUSTER="spa-${CELL}"
[ -n "$SERVICE" ] || SERVICE="gateway"
LB_NAME="spa-${CELL}"

command -v aws > /dev/null 2>&1 || refuse "the AWS CLI is not on PATH. brew install awscli"
# The CLI hands a session to the plugin and cannot start one without it.
# https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager-working-with-install-plugin.html
command -v session-manager-plugin > /dev/null 2>&1 ||
  refuse "the Session Manager plugin is not on PATH. brew install --cask session-manager-plugin"

REGION="${AWS_REGION:-${AWS_DEFAULT_REGION:-}}"
[ -n "$REGION" ] || refuse "no Region in AWS_REGION or AWS_DEFAULT_REGION. set -a; . tools/aws-project.env; set +a"

# Something already answering on the port would take the browser's connections, or the plugin
# would fail to bind it after the session had started. bash's /dev/tcp connects, so a refusal means
# the port is free. https://www.gnu.org/software/bash/manual/html_node/Redirections.html
if (exec 3<> "/dev/tcp/127.0.0.1/${LOCAL_PORT}") 2> /dev/null; then
  refuse "something already listens on 127.0.0.1:${LOCAL_PORT}: an earlier tunnel.sh, or the G0 harness (spikes/g0/README.md). Stop it, or pass --local-port."
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/${PROGRAM}.XXXXXX")"
OUT="${WORK}/out"
ERR="${WORK}/err"

# One read. Every call names the Region and --output text, so a profile's own output setting cannot
# change what is read back. On failure it says which call and the error code the CLI printed, in
# "An error occurred (<Code>) when calling the <Operation> operation" (`aws help return-codes`),
# and not the message, which can carry an ARN.
aws_read() {
  local what="$1"
  shift
  if aws --region "$REGION" --output text "$@" > "$OUT" 2> "$ERR"; then
    return 0
  fi
  local code
  code="$(awk 'match($0, /An error occurred \([^)]*\)/) { print substr($0, RSTART + 19, RLENGTH - 20); exit }' "$ERR")"
  [ -n "$code" ] || code="no answer from AWS"
  fail "${what}: ${code}"
}

# --- the load balancer ---------------------------------------------------------------------------

if [ -z "$ALB" ]; then
  aws_read "the load balancer ${LB_NAME} in ${REGION}" \
    elbv2 describe-load-balancers --names "$LB_NAME" --query "LoadBalancers[0].[DNSName, Scheme]"
  ALB="$(awk -F '\t' 'NR == 1 { print $1 }' "$OUT")"
  SCHEME="$(awk -F '\t' 'NR == 1 { print $2 }' "$OUT")"
  [ -n "$ALB" ] && [ "$ALB" != "None" ] || fail "the load balancer ${LB_NAME} has no DNS name"
  # Task 1.3's load balancer is internal; one that is not would be reached without the tunnel,
  # and this would be forwarding to the wrong thing.
  [ "$SCHEME" = "internal" ] || fail "the load balancer ${LB_NAME} is ${SCHEME}, not internal"
fi

# --- the task ------------------------------------------------------------------------------------

aws_read "the running tasks of ${SERVICE} in ${CLUSTER}" \
  ecs list-tasks --cluster "$CLUSTER" --service-name "$SERVICE" --desired-status RUNNING --query taskArns
TASK_ARNS="$(tr '\t' ' ' < "$OUT")"
# Word splitting on purpose: list-tasks prints the ARNs tab-separated on one line, and none holds a
# space. Bash 3.2 has no mapfile.
# shellcheck disable=SC2086
set -- $TASK_ARNS
[ "$#" -gt 0 ] || fail "${SERVICE} in ${CLUSTER} has no running task, so there is nothing to forward through. Is the service steady?"

# One row per task: its ARN, its status, whether ECS Exec is on, and the gateway container's
# runtime id and Exec agent status. The three conditions the plan names are a service with Exec
# on, the task role's ssmmessages permissions and a writable root filesystem; the first two show
# here as the agent running, and the third is in the task definition (ecs.tf).
# https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs-exec.html
aws_read "the tasks of ${SERVICE}" \
  ecs describe-tasks --cluster "$CLUSTER" --tasks "$@" \
  --query "tasks[].[taskArn, lastStatus, enableExecuteCommand, containers[?name=='${CONTAINER}'] | [0].runtimeId, containers[?name=='${CONTAINER}'] | [0].managedAgents[?name=='ExecuteCommandAgent'] | [0].lastStatus]"

# The first task a session can reach. A deployment can run two for a while; either one forwards to
# the same load balancer.
TARGET_TASK=""
TARGET_RUNTIME=""
WHY=""
while IFS="$(printf '\t')" read -r arn status exec_on runtime agent; do
  [ -n "$arn" ] || continue
  id="${arn##*/}"
  if [ "$status" != "RUNNING" ]; then
    WHY="${WHY}  task ${id} is ${status}"$'\n'
  elif [ "$exec_on" != "True" ]; then
    WHY="${WHY}  task ${id} has ECS Exec off; the service needs enable_execute_command (ecs.tf)"$'\n'
  elif [ -z "$runtime" ] || [ "$runtime" = "None" ]; then
    WHY="${WHY}  task ${id} has no running ${CONTAINER} container"$'\n'
  elif [ "$agent" != "RUNNING" ]; then
    WHY="${WHY}  task ${id}: the ${CONTAINER} container's ExecuteCommandAgent is ${agent}"$'\n'
  else
    TARGET_TASK="$id"
    TARGET_RUNTIME="$runtime"
    break
  fi
done < "$OUT"

if [ -z "$TARGET_TASK" ]; then
  printf '%s: no task of %s in %s can carry a session:\n%s' "$PROGRAM" "$SERVICE" "$CLUSTER" "$WHY" >&2
  exit 1
fi

# --- the session ---------------------------------------------------------------------------------

# ecs:<cluster>_<task id>_<container runtime id> is how Session Manager names an ECS container, and
# AWS-StartPortForwardingSessionToRemoteHost forwards a local port to a host the target can reach.
# https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager-working-with-sessions-start.html#sessions-remote-port-forwarding
TARGET="ecs:${CLUSTER}_${TARGET_TASK}_${TARGET_RUNTIME}"
PARAMETERS="{\"host\":[\"${ALB}\"],\"portNumber\":[\"443\"],\"localPortNumber\":[\"${LOCAL_PORT}\"]}"

note "forwarding local port ${LOCAL_PORT} to ${ALB}:443"
note "through task ${TARGET_TASK}, container ${CONTAINER}, of ${SERVICE} in ${CLUSTER}"
note "check it from another terminal: tools/scripts/verify-tunnel.sh / (the sample image) or /healthz (the gateway)"
note "Ctrl-C ends the session"

rm -rf "$WORK"
WORK=""
trap - EXIT
exec aws --region "$REGION" ssm start-session \
  --target "$TARGET" \
  --document-name AWS-StartPortForwardingSessionToRemoteHost \
  --parameters "$PARAMETERS"
