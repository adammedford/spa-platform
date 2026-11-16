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
#   tools/scripts/tunnel.sh --task <task id>     # through that task, not the first one reachable
#   tools/scripts/tunnel.sh --keepalive 300      # and hold the session open while it is idle
#
# It stays in the foreground until the session ends; Ctrl-C ends it. In a second terminal,
# tools/scripts/verify-tunnel.sh <path> checks it: / on the sample image, /healthz on the gateway.
#
# A session also ends on its own. Session Manager ends one that carries no traffic for its idle
# timeout, 20 minutes unless the account's Session Manager preferences set another: "Idle session
# timeout" in the Systems Manager console's Session Manager preferences, the idleSessionTimeout of
# the account's SSM-SessionManagerRunShell document, from 1 to 60 minutes.
# https://docs.aws.amazon.com/systems-manager/latest/userguide/session-preferences-timeout.html
# Nothing here says so when it happens: the plugin's last line is all the terminal shows, and the
# next request through the port finds nothing. --keepalive <seconds> holds one open: while the
# session lasts it requests /healthz through the forwarded port every <seconds>, which is traffic
# whether or not the request succeeds, and it stops when the session ends. Give it an interval
# shorter than the idle timeout.
#
# Without --task it forwards through the first running task of the service that a session can
# reach. A deployment can run two for a while, and either reaches the same load balancer, but a
# sitting that stops one task needs the tunnel on the other: --task names it, by the id that ends
# its ARN, and a task that is not a running task of the service a session can reach is a failure
# that names it, never a fall back to another.
#
# Its names come from the outputs document, infra/envs/poc/outputs.json, and from nowhere else: the
# ECS cluster is its ecsCluster, the service its ecsService and the load balancer its albDnsName
# (contracts/infra-outputs.schema.json: "The scripts and the deployed tests read it"). seed.sh,
# deploy.sh and rollback.sh write it after every apply, so it names the last apply's cell. Nothing
# is looked up in AWS by name and nothing is read from Terraform state (#198); without the
# document, or with one for another Region, it refuses. AWS is asked only for the service's running
# tasks, and the hop is a task's gateway container (infra/modules/cell/ecs.tf). The load balancer is
# the cell module's, which is internal (alb.tf).
#
# Run it as the operator's own session: the spa-platform profile, with tools/aws-project.env
# sourced, after seed.sh. It needs the AWS CLI v2, the Session Manager plugin and python3, and
# curl with --keepalive.
#
# It never prints the account id: of a task it names only the id, never the ARN. The session's own
# stderr goes through the same rule as its reads: an AWS error reaches the terminal as its code
# alone, and any other line with every twelve-digit run masked. The session replaces this script, so
# nothing is left to wait for that filter: when the session ends, Ctrl-C or not, the filter's last
# line can print after the shell's prompt.
#
# Exit status: the AWS CLI's once the session is handed to it, 254 when StartSession itself is
# refused (`aws help return-codes`); 1 when the cell has no task a session can reach, or the task
# --task names is not one; 2 when it did not start (a usage error, a missing tool, no Region, the
# local port already held, or no outputs document with the three names for this Region).
#
# Written for the bash 3.2 that ships with macOS, like tools/scripts/certs.sh.
# Owner: workstream 1, AWS and IaC.

set -euo pipefail
umask 077

PROGRAM="tunnel"
# The outputs document, the AWS plumbing and note, refuse and fail are the cell scripts' own:
# OUTPUTS_FILE and outputs_field, aws_read, and the scratch directory's clean-up.
# shellcheck source=tools/scripts/cell-common.sh
. "$(dirname -- "$0")/cell-common.sh"

LOCAL_PORT="${SPA_TUNNEL_PORT:-8443}"
TASK=""
TASK_GIVEN=0
KEEPALIVE=""
KEEPALIVE_GIVEN=0
# The host --keepalive asks, as verify-tunnel.sh names it. Its name is pinned to the loopback for
# the request, so it needs no hosts-file line.
KEEPALIVE_HOST="app1.${SPA_CERT_DOMAIN:-spa-poc.test}"

# The hop. The task has two containers and ECS Exec reaches both, but the plan names the gateway
# task as the hop and the gateway container is the one the ALB's target group sends to.
CONTAINER="$GATEWAY_CONTAINER"

usage() {
  # The header above, up to the Owner line, is this script's manual.
  sed -n '3,/^# Owner:/p' "$0" | sed 's/^# \{0,1\}//'
  cat <<EOF

Options
  --local-port N   The workstation port the Session Manager plugin listens on. Default:
                   \$SPA_TUNNEL_PORT, else 8443, the port in every app's registry host and
                   callback URL (docs/plan/05-infrastructure-and-cost.md, step 5).
  --task ID        Forward through this task of the service, by its id (the part of its ARN
                   after the last /). Exit 1 when it is not a running task a session can reach.
                   Default: the first running task a session can reach.
  --keepalive S    While the session lasts, request /healthz through the forwarded port every
                   S seconds, so the idle timeout does not end it. Default: none.
  -h, --help       This text.
EOF
}

trap cell_cleanup EXIT
trap 'exit 130' INT TERM

value_of() {
  [ "$2" -ge 2 ] || refuse "$1 needs a value. Try --help."
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --local-port) value_of "$1" "$#"; LOCAL_PORT="$2"; shift ;;
    --task) value_of "$1" "$#"; TASK="$2"; TASK_GIVEN=1; shift ;;
    --keepalive) value_of "$1" "$#"; KEEPALIVE="$2"; KEEPALIVE_GIVEN=1; shift ;;
    -h | --help)
      usage
      exit 0
      ;;
    *) refuse "unknown argument '$1'. Try --help." ;;
  esac
  shift
done

case "$LOCAL_PORT" in
  '' | *[!0-9]* | 0*) refuse "--local-port must be a port number: got '${LOCAL_PORT}'" ;;
esac
[ "$LOCAL_PORT" -le 65535 ] || refuse "--local-port must be a port number: got '${LOCAL_PORT}'"
# An ECS task id is the hex run that ends the task's ARN. Nothing else is taken, so the value
# reaches neither the target nor a message as anything but an id, and an ARN, which carries the
# account id, is refused rather than echoed.
# https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_Task.html (taskArn)
if [ "$TASK_GIVEN" = "1" ]; then
  case "$TASK" in
    '' | *[!0-9a-f]*) refuse "--task takes a task id, the hex after the last / of its ARN: got something else" ;;
  esac
fi
if [ "$KEEPALIVE_GIVEN" = "1" ]; then
  case "$KEEPALIVE" in
    '' | *[!0-9]* | 0*) refuse "--keepalive must be a whole number of seconds above 0: got '${KEEPALIVE}'" ;;
  esac
  need_tool curl "It makes --keepalive's requests. brew install curl"
fi

command -v aws > /dev/null 2>&1 || refuse "the AWS CLI is not on PATH. brew install awscli"
# The CLI hands a session to the plugin and cannot start one without it.
# https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager-working-with-install-plugin.html
command -v session-manager-plugin > /dev/null 2>&1 ||
  refuse "the Session Manager plugin is not on PATH. brew install --cask session-manager-plugin"

# cell_documents.py reads the outputs document: bash 3.2 has no JSON reader.
need_tool python3 "xcode-select --install"

REGION="${AWS_REGION:-${AWS_DEFAULT_REGION:-}}"
[ -n "$REGION" ] || refuse "no Region in AWS_REGION or AWS_DEFAULT_REGION. set -a; . tools/aws-project.env; set +a"

# The three names, before any AWS call. Each refuses, exit 2, when the document is absent, is for
# another Region or lacks the name, and says to run seed.sh (cell-common.sh, outputs_field).
CLUSTER="$(outputs_field ecsCluster)"
SERVICE="$(outputs_field ecsService)"
ALB="$(outputs_field albDnsName)"

# Something already answering on the port would take the browser's connections, or the plugin
# would fail to bind it after the session had started. bash's /dev/tcp connects, so a refusal means
# the port is free. https://www.gnu.org/software/bash/manual/html_node/Redirections.html
if (exec 3<> "/dev/tcp/127.0.0.1/${LOCAL_PORT}") 2> /dev/null; then
  refuse "something already listens on 127.0.0.1:${LOCAL_PORT}: an earlier tunnel.sh, or the G0 harness (spikes/g0/README.md). Stop it, or pass --local-port."
fi

# Not cell_start, which asks AWS who the caller is: this script reads only the service's tasks.
# Every read goes through cell-common.sh's aws_read: the Region and --output text on each call, and
# a failure named by its error code, never by its message, which can carry an ARN.
WORK="$(mktemp -d "${TMPDIR:-/tmp}/${PROGRAM}.XXXXXX")"
OUT="${WORK}/out"
ERR="${WORK}/err"

# --- the task ------------------------------------------------------------------------------------

aws_read "the running tasks of ${SERVICE} in ${CLUSTER}" \
  ecs list-tasks --cluster "$CLUSTER" --service-name "$SERVICE" --desired-status RUNNING --query taskArns
TASK_ARNS="$(tr '\t' ' ' < "$OUT")"
# Word splitting on purpose: list-tasks prints the ARNs tab-separated on one line, and none holds a
# space. Bash 3.2 has no mapfile.
# shellcheck disable=SC2086
set -- $TASK_ARNS
# A named task is one of these or nothing: list-tasks asks for this service's running tasks, so a
# stopped task, a task of another service and an id no task has all fall here, and only its id is
# printed. The running ones are named beside it, by id, for the one to give instead.
if [ -n "$TASK" ]; then
  NAMED=""
  RUNNING=""
  for arn in "$@"; do
    RUNNING="${RUNNING} ${arn##*/}"
    if [ "${arn##*/}" = "$TASK" ]; then
      NAMED="$arn"
    fi
  done
  [ -n "$NAMED" ] ||
    fail "task ${TASK} is not a running task of ${SERVICE} in ${CLUSTER}. Its running tasks:${RUNNING:- none}"
  set -- "$NAMED"
fi
[ "$#" -gt 0 ] || fail "${SERVICE} in ${CLUSTER} has no running task, so there is nothing to forward through. Is the service steady?"

# One row per task: its ARN, its status, whether ECS Exec is on, and the gateway container's
# runtime id and Exec agent status. The three conditions the plan names are a service with Exec
# on, the task role's ssmmessages permissions and a writable root filesystem; the first two show
# here as the agent running, and the third is in the task definition (ecs.tf).
# https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs-exec.html
aws_read "the tasks of ${SERVICE}" \
  ecs describe-tasks --cluster "$CLUSTER" --tasks "$@" \
  --query "tasks[].[taskArn, lastStatus, enableExecuteCommand, containers[?name=='${CONTAINER}'] | [0].runtimeId, containers[?name=='${CONTAINER}'] | [0].managedAgents[?name=='ExecuteCommandAgent'] | [0].lastStatus]"

# The first task a session can reach, or with --task the one it names, which is then the only task
# described. A deployment can run two for a while; either one forwards to the same load balancer.
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

if [ -z "$TARGET_TASK" ] && [ -n "$TASK" ]; then
  printf '%s: task %s of %s in %s cannot carry a session:\n%s' "$PROGRAM" "$TASK" "$SERVICE" "$CLUSTER" "$WHY" >&2
  exit 1
fi
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
if [ -n "$KEEPALIVE" ]; then
  note "keepalive: a request to /healthz through local port ${LOCAL_PORT} every ${KEEPALIVE} seconds while the session lasts"
fi
note "Ctrl-C ends the session"

# The session's stderr, a line at a time as it arrives. The CLI prints a refused StartSession as
# aws_read's reads fail, and its message names the caller's ARN, which carries the account id
# ("User: arn:aws:sts::<account>:assumed-role/... is not authorized to perform: ssm:StartSession",
# https://docs.aws.amazon.com/IAM/latest/UserGuide/troubleshoot_access-denied.html), so that line
# becomes its code, as aws_read's does. Any other line, the plugin's own among them, is passed on
# with every twelve-digit run masked, which an account id always is. It ignores Ctrl-C, which goes
# to the whole foreground group: the plugin prints its last lines after it, and the filter ends
# when they do, at end of input. bash already starts a process substitution with SIGINT ignored,
# since a script runs without job control and "asynchronous commands ignore SIGINT" then
# (https://www.gnu.org/software/bash/manual/html_node/Signals.html); the trap says so where the
# filter is written. Nothing waits for it once the session exits, so its last line can follow the
# prompt (#505). Written out rather than as {12}, which not every awk reads.
session_errors() {
  trap '' INT
  awk -v program="$PROGRAM" -v what="the session through task ${TARGET_TASK}" '
    match($0, /An error occurred \([^)]*\)/) {
      print program ": " what ": " substr($0, RSTART + 19, RLENGTH - 20)
      fflush()
      next
    }
    {
      gsub(/[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]/, "<account id>")
      print
      fflush()
    }'
}

# --keepalive's loop. It runs beside the session and asks whether the session lasts once a second,
# by the pid the session takes over below, so it stops within a second of the session's end however
# long the interval. Its request is traffic through the session whatever it answers, a TLS failure
# included, so it ignores the answer and prints nothing: verify-tunnel.sh is the check. Its name is
# pinned to the loopback and no proxy is used, so the request goes through the forwarded port or
# nowhere. Started with no terminal: its streams are /dev/null, so it holds none of the session's
# open and a caller reading them sees their end when the session's. A Ctrl-C reaches it with the
# rest of the foreground group, or bash has it ignore one ("asynchronous commands ignore SIGINT",
# https://www.gnu.org/software/bash/manual/html_node/Signals.html), and then the session's end stops
# it as above. https://curl.se/docs/manpage.html#--resolve
keepalive() {
  local waited=0
  while sleep 1; do
    kill -0 "$SESSION_PID" 2> /dev/null || break
    waited=$((waited + 1))
    [ "$waited" -ge "$KEEPALIVE" ] || continue
    waited=0
    curl -q --silent --noproxy '*' --proto '=https' --connect-timeout 10 --max-time 20 \
      --output /dev/null --resolve "${KEEPALIVE_HOST}:${LOCAL_PORT}:127.0.0.1" \
      "https://${KEEPALIVE_HOST}:${LOCAL_PORT}/healthz" || true
  done
}

rm -rf "$WORK"
WORK=""
trap - EXIT
if [ -n "$KEEPALIVE" ]; then
  # The exec below keeps this process's pid, so the session's pid is this script's.
  SESSION_PID=$$
  keepalive < /dev/null > /dev/null 2>&1 &
fi
# Still exec, so the session is this process: stdin and stdout stay the terminal's, Ctrl-C and a
# signal reach the CLI and the plugin as they did, and the exit status is the CLI's. Only stderr is
# routed, through a process substitution, which bash 3.2 has.
# https://www.gnu.org/software/bash/manual/html_node/Process-Substitution.html
exec aws --region "$REGION" ssm start-session \
  --target "$TARGET" \
  --document-name AWS-StartPortForwardingSessionToRemoteHost \
  --parameters "$PARAMETERS" 2> >(session_errors >&2)
