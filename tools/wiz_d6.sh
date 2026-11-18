#!/usr/bin/env bash
#
# wiz_d6.sh -- row 6's lever in the wiz skill's sitting D: launch deploy.sh when the row's driver
# says to, and on deploy.sh's pass line restart the tunnel, whose hop is the cell's bastion task
# (#1168), never a gateway task. Owner: integrator.
#
#   tools/wiz_d6.sh --test-pid <pid> --test-output <file> --record "$R" [--wait <seconds>]
#                   [--local-port <port>] [--keepalive <seconds>] [--tunnel-log <file>]
#                   [--tunnel-wait <seconds>] [-- <deploy.sh's arguments>]
#
# Built in #1204. Row 6 (F1) drives sessions while the operator deploys, and in sitting D of
# 2026-11-14 the agent wrote this lever by hand three times as a ten-line inline script. One launch
# went to a test that had already died, and only a long delay left time to stop deploy.sh before
# it applied. In order, and it stops at the first that fails:
#
#   1. It waits for the driver's launch line, `row6: launch deploy.sh now` (LaunchLine in
#      tests/e2e/Stack/RollingDeployLoop.cs, #1203), in the row 6 test's output file, --test-output,
#      checking once a second that the test, --test-pid, is still running. It launches nothing, and
#      exits 1 saying which, when the test is no longer running (a zombie counts as ended) or the
#      line has not come within --wait seconds, 600 by default. The driver writes the line to its
#      standard output, which `dotnet test` does not pass to the terminal or a file; the line
#      reaches the file when the row runs through the suite's console runner (#1216).
#   2. It runs tools/scripts/deploy.sh with the arguments after `--`. Its output goes whole to the
#      record, as the prelude's `shown` writes it, and through tools/wiz_redact.py --stream to the
#      terminal. The human's confirm on launch is the skill's, given before this command runs.
#   3. Without deploy.sh's pass line (`deploy: pass: ...`), it stops and starts no tunnel and exits
#      with deploy.sh's own status, or 1 when that was 0 (its --help, say): the skill's D6 reads
#      this script's exit 0 as the tunnel having moved, so no run that moved none gives it (#1284).
#   4. On the pass line it stops every running tunnel.sh on --local-port (8443 by default): a
#      tunnel.sh execs into `aws ssm start-session`, so it is the process whose arguments name
#      AWS-StartPortForwardingSessionToRemoteHost with that localPortNumber. The CLI runs the
#      session-manager-plugin as a child, which holds the local port, and a TERM to the CLI alone
#      leaves it running: the CLI ignores only SIGINT, SIGQUIT and SIGTSTP around the plugin's
#      check_call and forwards no signal to it, so the CLI dies and the plugin keeps the port, and
#      tunnel.sh's restart then refuses a busy port. The plugin's arguments carry the same request
#      as json.dumps writes it, `"localPortNumber": ["<port>"]` with a space, so the pattern takes
#      both processes. https://github.com/aws/aws-cli/blob/v2/awscli/customizations/sessionmanager.py
#      and ignore_user_entered_signals in https://github.com/aws/aws-cli/blob/v2/awscli/compat.py.
#      A tunnel on another port is left alone. TERM first, then KILL after ten seconds. It writes
#      the time the last one ended to the record, or that none was running.
#   5. It starts tools/scripts/tunnel.sh --local-port <port> [--keepalive <seconds>] again in the
#      background, where it outlives this script, with its output in --tunnel-log (by default
#      tunnel-d6-<UTC time>.out beside --test-output, in the bundle), and waits up to --tunnel-wait
#      seconds, 60 by default, for its `through task <id>` line. The id is the bastion task's
#      (#1168): deploy.sh rolls the gateway's service and replaces no bastion task, so it is the
#      task the stopped tunnel went through. It writes the start time and that id to the record and
#      exits 0, or exits 1 when tunnel.sh ends, or the wait runs out, before it names a task.
#      verify-tunnel.sh /healthz is the skill's next step; this script does not run it.
#
# The record gets three lines of its own, besides deploy.sh's output:
#   wiz_d6: tunnel stopped at <UTC time>    (or: wiz_d6: no tunnel was running on local port <port>; none was stopped)
#   wiz_d6: tunnel started at <UTC time>
#   wiz_d6: through task <task id>
#
# It prints no account id, ARN or session id. deploy.sh's output reaches the terminal only through
# wiz_redact.py's rules, and of tunnel.sh's output it prints only the task id: the Session Manager
# plugin's SessionId lines stay in --tunnel-log, which it never prints; read that file through
# wiz_redact.py. Its own lines name a pid, a port, a file and a task id.
#
# Exit status: 0 only when the tunnel was started through a task after deploy.sh's pass line;
# deploy.sh's when it printed no pass line and exited non-zero; 1 when it exited 0 with no pass line,
# when it launched nothing (step 1) or when the tunnel named no task; 2 on a usage error.
#
# Run its companion: python3 -I tools/wiz_d6_tests.py. Written for the bash 3.2 that ships with macOS.

set -u

PROGRAM="wiz_d6"
LAUNCH_LINE="row6: launch deploy.sh now"
HERE="$(cd "$(dirname "$0")" && pwd)"
DEPLOY="$HERE/scripts/deploy.sh"
TUNNEL="$HERE/scripts/tunnel.sh"
REDACT="$HERE/wiz_redact.py"

usage() {
  echo "usage: tools/wiz_d6.sh --test-pid <pid> --test-output <file> --record <file> [--wait <seconds>]" \
    "[--local-port <port>] [--keepalive <seconds>] [--tunnel-log <file>] [--tunnel-wait <seconds>]" \
    "[-- <deploy.sh's arguments>]" >&2
  [ "$#" -gt 0 ] && echo "$PROGRAM: $*" >&2
  exit 2
}

TEST_PID="" TEST_OUTPUT="" RECORD="" WAIT=600 PORT=8443 KEEPALIVE="" TUNNEL_LOG="" TUNNEL_WAIT=60
while [ "$#" -gt 0 ]; do
  case "$1" in
    --) shift; break ;;
    --test-pid | --test-output | --record | --wait | --local-port | --keepalive | --tunnel-log | --tunnel-wait)
      [ "$#" -ge 2 ] || usage "$1 needs a value"
      case "$1" in
        --test-pid) TEST_PID="$2" ;;
        --test-output) TEST_OUTPUT="$2" ;;
        --record) RECORD="$2" ;;
        --wait) WAIT="$2" ;;
        --local-port) PORT="$2" ;;
        --keepalive) KEEPALIVE="$2" ;;
        --tunnel-log) TUNNEL_LOG="$2" ;;
        --tunnel-wait) TUNNEL_WAIT="$2" ;;
      esac
      shift 2 ;;
    *) usage "unknown argument $1" ;;
  esac
done
[ -n "$TEST_PID" ] && [ -n "$TEST_OUTPUT" ] && [ -n "$RECORD" ] || usage "--test-pid, --test-output and --record are required"
for number in "$TEST_PID" "$WAIT" "$PORT" "$TUNNEL_WAIT" ${KEEPALIVE:+"$KEEPALIVE"}; do
  case "$number" in '' | *[!0-9]*) usage "$number is not a whole number" ;; esac
done
[ -n "$TUNNEL_LOG" ] || TUNNEL_LOG="$(dirname "$TEST_OUTPUT")/tunnel-d6-$(date -u +%Y%m%dT%H%M%SZ).out"

now() { date -u +%Y-%m-%dT%H:%M:%SZ; }
record() { printf '%s: %s\n' "$PROGRAM" "$*" | tee -a "$RECORD"; }
fail() { printf '%s: %s\n' "$PROGRAM" "$*" | tee -a "$RECORD" >&2; exit "${CODE:-1}"; }

# Running, and not a zombie: an ended child its parent has not yet reaped still answers kill -0.
alive() {
  kill -0 "$1" 2> /dev/null || return 1
  case "$(ps -o stat= -p "$1" 2> /dev/null)" in Z* | '') return 1 ;; esac
  return 0
}

# 1. The launch line, while the test runs.
waited=0
while :; do
  alive "$TEST_PID" || fail "the row 6 test (pid $TEST_PID) is no longer running; deploy.sh was not launched"
  if [ -f "$TEST_OUTPUT" ] && grep -qF -- "$LAUNCH_LINE" "$TEST_OUTPUT"; then
    break
  fi
  [ "$waited" -lt "$WAIT" ] ||
    fail "the launch line \"$LAUNCH_LINE\" did not come in $TEST_OUTPUT within $WAIT s (--wait); deploy.sh was not launched"
  sleep 1
  waited=$((waited + 1))
done
alive "$TEST_PID" || fail "the row 6 test (pid $TEST_PID) is no longer running; deploy.sh was not launched"
record "the launch line came; deploy.sh launched at $(now)"

# 2. deploy.sh: whole to the record and a copy of ours, redacted to the terminal.
WORK="$(mktemp -d)" || exit 1
trap 'rm -rf "$WORK"' EXIT
echo "\$ tools/scripts/deploy.sh $*" >> "$RECORD"
"$DEPLOY" "$@" 2>&1 | tee -a "$RECORD" "$WORK/deploy.out" | python3 -I "$REDACT" --stream
DEPLOYED=${PIPESTATUS[0]}
echo "exit=$DEPLOYED" >> "$RECORD"

# 3. No pass line, no tunnel. Never exit 0 here: D6 reads the lever's 0 as the tunnel moved (#1284).
if ! grep -q '^deploy: pass:' "$WORK/deploy.out"; then
  CODE=$DEPLOYED
  [ "$CODE" -ne 0 ] || CODE=1
  fail "deploy.sh exited $DEPLOYED without its pass line; no tunnel was stopped or started"
fi

# 4. Stop the tunnel on this port: tunnel.sh has exec'd into the CLI, and the CLI's plugin child holds
# the port, so both are stopped. The optional space after the colon is the plugin's json.dumps.
SESSION="AWS-StartPortForwardingSessionToRemoteHost.*\"localPortNumber\": ?\\[\"${PORT}\"\\]"
pids="$(pgrep -f -- "$SESSION")"
if [ -z "$pids" ]; then
  record "no tunnel was running on local port $PORT; none was stopped"
else
  # shellcheck disable=SC2086 # one pid per word
  kill $pids 2> /dev/null
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    pgrep -f -- "$SESSION" > /dev/null || break
    sleep 1
  done
  # One of the pids may already be gone, which makes kill's status non-zero; the wait still runs.
  # shellcheck disable=SC2086
  if pgrep -f -- "$SESSION" > /dev/null; then kill -KILL $pids 2> /dev/null; sleep 1; fi
  pgrep -f -- "$SESSION" > /dev/null && fail "the tunnel on local port $PORT (pid $(echo $pids)) did not stop; no tunnel was started"
  record "tunnel stopped at $(now)"
fi

# 5. Start it again, through the bastion's task (#1168), and keep the id it names.
started="$(now)"
nohup "$TUNNEL" --local-port "$PORT" ${KEEPALIVE:+--keepalive "$KEEPALIVE"} < /dev/null > "$TUNNEL_LOG" 2>&1 &
tunnel_pid=$!
task=""
waited=0
while [ "$waited" -le "$TUNNEL_WAIT" ]; do
  task="$(sed -n 's/^tunnel: through task \([0-9A-Za-z]*\),.*/\1/p' "$TUNNEL_LOG" | head -n 1)"
  [ -n "$task" ] && break
  alive "$tunnel_pid" || fail "tunnel.sh exited before it named its task; its output is in $TUNNEL_LOG"
  sleep 1
  waited=$((waited + 1))
done
[ -n "$task" ] || fail "tunnel.sh (pid $tunnel_pid) named no task within $TUNNEL_WAIT s (--tunnel-wait); its output is in $TUNNEL_LOG"
record "tunnel started at $started"
record "through task $task"
exit 0
