#!/usr/bin/env bash
#
# logs.sh -- read the cell's gateway log group, every line through the redaction.
#
# This is the way a sitting reads the log. Never `aws logs tail` by hand: the mock API's start line
# names both apps' client ids, and an ARN in a gateway line names the account, and the sitting of
# #58 put both on the terminal and into its log that way (#208). Here every line passes through the
# redaction the cell scripts share (cell-common.sh), which elides the account id and each client id
# in infra/envs/poc/outputs.json.
#
#   tools/scripts/logs.sh                          # the last ten minutes, both containers
#   tools/scripts/logs.sh --since 2h               # the last two hours
#   tools/scripts/logs.sh --container mock-api     # one container's streams
#   tools/scripts/logs.sh --since 5m --follow      # and keep reading until Control-C
#
# The group is the outputs document's logGroup, which seed.sh, deploy.sh and rollback.sh write after
# every apply (contracts/infra-outputs.schema.json), and never Terraform state. One group holds both
# containers' streams, each named <container>/<container>/<task id> (infra/modules/cell/ecs.tf), so
# --container reads the streams with that prefix.
#
# Run it as the operator's own session: the spa-platform profile, with tools/aws-project.env
# sourced. It needs the AWS CLI and python3. It never prints the account id or a client id.
#
# Exit status: 0 when the read finished; 130 when Control-C ended it, as it ends --follow; 1 when
# AWS refused the read, named by its error code; 2 when it did not start (no outputs document among
# the reasons).
#
# Written for the bash 3.2 that ships with macOS, like tools/scripts/certs.sh.
# Owner: workstream 1, AWS and IaC.

set -euo pipefail
umask 077

PROGRAM="logs"
# shellcheck source=tools/scripts/cell-common.sh
. "$(dirname -- "$0")/cell-common.sh"

# The CLI's own default window: "By default, logs will be displayed starting from ten minutes in the
# past." https://awscli.amazonaws.com/v2/documentation/api/latest/reference/logs/tail.html
SINCE="10m"
CONTAINER=""
FOLLOW=""

usage() {
  # The header above, up to the Owner line, is this script's manual.
  sed -n '3,/^# Owner:/p' "$0" | sed 's/^# \{0,1\}//'
  cat <<EOF

Options
  --since WHEN        From when: a number and one unit of s, m, h, d or w (5m, 2h), or an ISO 8601
                      timestamp (2026-10-21T02:00:00Z). The default is ${SINCE}.
  --container NAME    Only ${GATEWAY_CONTAINER} or only ${MOCK_API_CONTAINER}. The default is both.
  --follow            Keep reading new lines until Control-C.
  -h, --help          This text.
EOF
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --since)
      [ "$#" -ge 2 ] || refuse "--since needs a value. Try --help."
      SINCE="$2"
      shift
      ;;
    --container)
      [ "$#" -ge 2 ] || refuse "--container needs a value. Try --help."
      CONTAINER="$2"
      shift
      ;;
    --follow) FOLLOW="--follow" ;;
    -h | --help)
      usage
      exit 0
      ;;
    *) refuse "unknown argument '$1'. Try --help." ;;
  esac
  shift
done

# The two forms tail's --since takes, from the page above: a relative time is a number and a single
# unit ("multiple units are not supported"), or an ISO 8601 timestamp. Checked here so that a typo is
# a usage error before any AWS call, not an error the CLI prints.
since_relative='^[1-9][0-9]*[smhdw]$'
since_timestamp='^[0-9]{4}-[0-9]{2}-[0-9]{2}(T[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]+)?)?(Z|[+-][0-9]{2}:?[0-9]{2})?)?$'
if ! [[ "$SINCE" =~ $since_relative ]] && ! [[ "$SINCE" =~ $since_timestamp ]]; then
  refuse "--since must be a number and one unit of s, m, h, d or w, or an ISO 8601 timestamp: got '${SINCE}'"
fi

STREAM_PREFIX=""
case "$CONTAINER" in
  "") ;;
  "$GATEWAY_CONTAINER" | "$MOCK_API_CONTAINER") STREAM_PREFIX="${CONTAINER}/" ;;
  *) refuse "--container must be ${GATEWAY_CONTAINER} or ${MOCK_API_CONTAINER}: got '${CONTAINER}'" ;;
esac

trap cell_cleanup EXIT
trap 'exit 130' INT TERM

cell_start
redact_clients

# From the outputs document, never from state (contracts/infra-outputs.schema.json).
LOG_GROUP="$(outputs_field logGroup)"
note "reading ${LOG_GROUP} since ${SINCE}${CONTAINER:+, the ${CONTAINER} container}${FOLLOW:+, following}"

# Not through aws_call, which holds the whole answer in a file: a followed read never ends. The
# detailed format names each line's stream, and so its container; colour off, so no escape sequence
# sits inside a value the redaction looks for.
set -- logs tail "$LOG_GROUP" --since "$SINCE" --format detailed --color off
if [ -n "$STREAM_PREFIX" ]; then set -- "$@" --log-stream-name-prefix "$STREAM_PREFIX"; fi
if [ -n "$FOLLOW" ]; then set -- "$@" "$FOLLOW"; fi

status=0
run_aws "$@" 2> "$ERR" | redact_stream || status=$?
if [ "$status" -ne 0 ]; then
  # The code and never the message, which can carry the caller's ARN.
  code="$(error_code)"
  fail "AWS did not answer the read of ${LOG_GROUP}: ${code:-no answer from AWS}"
fi
