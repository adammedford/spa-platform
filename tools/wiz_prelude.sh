# The wiz skill's prelude: what every Bash call of a sitting begins with. Owner: integrator.
#
#     . "$(git rev-parse --path-format=absolute --git-common-dir)/../tools/wiz_prelude.sh"
#     bash "$(git rev-parse --path-format=absolute --git-common-dir)/../tools/wiz_prelude.sh" --pin
#
# Sourced, as the first line of every call, it moves to the top level of the main checkout (the
# skill's rule 8: a call made from a worktree still runs the landed scripts), exports the AWS
# profile and Region from tools/aws-project.env, puts a per-user Docker Desktop on PATH, sets umask
# 077, names the sitting's record R and its evidence bundle E after the date Stage 0 pinned, makes
# the bundle's directory once git confirms it is ignored, and defines shown, captured, spactl and
# sitting_browser.
# Run with --pin, once, in Stage 0, it writes today's UTC date to the pin file .scratch/sitting/log/
# date, ignored as the rest of log/ is, and replaces an earlier sitting's.
#
# Why a file (#1010). The Bash tool's shell is /bin/bash, 3.2 on macOS (#183), and keeps no state
# between calls, so this block was repeated by hand at the head of each call from a copy in
# .claude/skills/wiz/SKILL.md. In sitting D one call left out the Docker PATH line, and
# tools/scripts/platform-tfvars.sh failed on docker-credential-desktop. At midnight UTC the copy's
# `date -u +%F` moved the record and bundle paths to the next day in the middle of the sitting, and
# a test run did nothing. So the date is read from the pin, and without a pin the prelude exits
# rather than return: a sourced `return` would let the rest of the call run with R and E unset.
# Each Bash call is its own shell, so the exit ends that call and nothing else.
#
# Run its companion: python3 -I tools/wiz_prelude_tests.py

wiz_pin=".scratch/sitting/log/date"

if [ "${BASH_SOURCE[0]}" = "$0" ]; then
  # Run, not sourced: the one thing it does is Stage 0's pin.
  if [ "$#" -ne 1 ] || [ "$1" != "--pin" ]; then
    echo "usage: bash tools/wiz_prelude.sh --pin   (Stage 0, once per sitting); every other call sources it" >&2
    exit 2
  fi
  cd "$(git rev-parse --path-format=absolute --git-common-dir)/.." || exit 1
  umask 077
  mkdir -p "${wiz_pin%/*}" || exit 1
  date -u +%F > "$wiz_pin" || exit 1
  echo "wiz prelude: the sitting's date is pinned to $(cat "$wiz_pin") in $wiz_pin"
  exit 0
fi

cd "$(git rev-parse --path-format=absolute --git-common-dir)/.." || exit 1
set -a && . tools/aws-project.env && set +a || exit 1
export PATH="$HOME/.docker/bin:$PATH"   # a per-user Docker Desktop; this shell is not a login shell
umask 077

# The pin is one date and nothing else. Anything else is not a pin: a call that ran on it would
# write the record somewhere no post names.
wiz_date=""
[ -f "$wiz_pin" ] && wiz_date="$(cat "$wiz_pin")"
case "$wiz_date" in
  [0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]) ;;
  *)
    echo "wiz prelude: no sitting date is pinned in $wiz_pin; Stage 0 pins it, once per sitting:" \
      "bash tools/wiz_prelude.sh --pin" >&2
    exit 1
    ;;
esac

R=".scratch/sitting/log/$wiz_date.log"
E="${R%.log}"
git check-ignore -q "$E/x" && mkdir -p "$E"
unset wiz_date wiz_pin

shown() { echo "\$ $*" >> "$R"; "$@" 2>&1 | tee -a "$R" | python3 -I tools/wiz_redact.py --stream; echo "exit=${PIPESTATUS[0]}" | tee -a "$R"; }
captured() { echo "\$ $*" >> "$R"; "$@" 2> >(tee -a "$R" | python3 -I tools/wiz_redact.py --stream >&2) | tee -a "$R"; local rc=${PIPESTATUS[0]}; echo "exit=$rc" | tee -a "$R" >&2; return "$rc"; }
spactl() { dotnet run --project tools/spactl -- "$@"; }   # a function, so SPACTL stays unset (Q80)
sitting_browser() { dotnet run --project tests/e2e -- sitting-browser --bundle "$E" "$@"; }   # #1023 Q5 and Q6; its socket is in the bundle
