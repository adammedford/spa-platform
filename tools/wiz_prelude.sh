# The wiz skill's prelude: what every Bash call of a sitting begins with. Owner: integrator.
#
#     . "$(git rev-parse --path-format=absolute --git-common-dir)/../tools/wiz_prelude.sh"
#     bash "$(git rev-parse --path-format=absolute --git-common-dir)/../tools/wiz_prelude.sh" --pin
#
# Sourced, as the first line of every call, it moves to the top level of the main checkout (the
# skill's rule 8: a call made from a worktree still runs the landed scripts), exports the AWS
# profile and Region from tools/aws-project.env, puts a per-user Docker Desktop on PATH, sets umask
# 077, names the sitting's record R and its evidence bundle E after the date Stage 0 pinned, makes
# the bundle's directory once git confirms it is ignored, and defines shown, captured, spactl,
# sitting_browser, wiz_password and guarded_init.
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
# wiz_password and guarded_init (#760). In the sitting of 2026-11-02 and 2026-11-03 the human asked
# three times for a test user's password, and each time the session improvised a read piped to the
# clipboard that no rule named; `wiz_password <user>` is that read, which prints only the password's
# length and when the clipboard clears. In the same sitting N's `init` exited 1 on "Backend
# configuration changed" over a cache that lacked a `region`; `-reconfigure` rewrites such a cache,
# and would as readily point a root whose key had moved at empty state, whose plan shows only
# creates. `guarded_init <root>`, run as `shown guarded_init <root>`, compares before it runs either
# `init`, and never prints the state bucket's name, which carries the account id. The decision on
# #760 gives its branches and the lines it prints.
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

# A test user's password from the parameter the identity root writes (infra/modules/idp-cognito,
# password_parameter), onto the clipboard. The job that empties it keeps none of the call's output
# open, so the call ends at once and the job outlives it.
wiz_password() {
  [ "$#" -eq 1 ] || { echo "usage: wiz_password <user>" >&2; return 2; }
  local parameter="/spa-platform/poc/test-users/$1" secret clears
  secret="$(aws ssm get-parameter --name "$parameter" --with-decryption --query Parameter.Value --output text)" \
    || { echo "wiz_password $1: $parameter could not be read; the clipboard is unchanged" >&2; return 1; }
  [ -n "$secret" ] || { echo "wiz_password $1: $parameter is empty; the clipboard is unchanged" >&2; return 1; }
  printf '%s' "$secret" | pbcopy || { unset secret; echo "wiz_password $1: pbcopy failed" >&2; return 1; }
  ( sleep 45; printf '' | pbcopy ) < /dev/null > /dev/null 2>&1 &
  clears="$(python3 -I -c 'import datetime as d; print((d.datetime.now(d.timezone.utc) + d.timedelta(seconds=45)).strftime("%H:%M:%SZ"))')"
  echo "wiz_password $1: ${#secret} characters on the clipboard; it clears at $clears"
  unset secret
}

# N's step 2: a root's init, under the guard the decision on #760 gives. The comparison prints one
# line and leaves its branch in its exit: 10 no cached backend, 11 a match, anything else a stop.
guarded_init() {
  [ "$#" -eq 1 ] || { echo "usage: guarded_init <root>" >&2; return 2; }
  python3 -I - "$1" << 'GUARD'
import json, pathlib, re, sys
root = sys.argv[1]
here = pathlib.Path(root)
def stop(why):
    print(f"guarded_init {root}: stop, no init: {why}")
    sys.exit(1)
def settings(text):
    return dict(re.findall(r'^\s*(\w+)\s*=\s*"([^"]*)"\s*(?:#.*)?$', text, re.M))
cache_file = here / ".terraform" / "terraform.tfstate"
cache_name = f"{root}/.terraform/terraform.tfstate"
if not cache_file.is_file():
    cached = None
else:
    try:
        cached = json.loads(cache_file.read_text()).get("backend")
    except (OSError, ValueError, AttributeError):
        stop(f"cannot read backend from {cache_name} (not JSON)")
if not isinstance(cached, dict):
    print(f"guarded_init {root}: no cached backend; init -backend-config=backend.hcl")
    sys.exit(10)
config = cached.get("config")
config = config if isinstance(config, dict) else {}
for setting in ("bucket", "key"):
    if not isinstance(config.get(setting), str):
        stop(f"cannot read backend.config.{setting} from {cache_name} (not set)")
hcl_name = f"{root}/backend.hcl"
try:
    hcl = settings((here / "backend.hcl").read_text())
except OSError:
    stop(f"cannot read bucket from {hcl_name} (absent)")
if "bucket" not in hcl:
    stop(f"cannot read bucket from {hcl_name} (not set)")
if "key" in hcl:
    key, source = hcl["key"], "backend.hcl"
else:
    try:
        block = re.search(r'backend\s+"s3"\s*\{(.*?)\}', (here / "versions.tf").read_text(), re.S)
    except OSError:
        block = None
    found = settings(block.group(1)) if block else {}
    if "key" not in found:
        stop(f"cannot read key from {hcl_name} or {root}/versions.tf (not set)")
    key, source = found["key"], "versions.tf"
bucket = "same" if config["bucket"] == hcl["bucket"] else "differs"
same_key = "same" if config["key"] == key else "differs"
if bucket == "same" and same_key == "same":
    print(f"guarded_init {root}: bucket same, key same (key from {source}); init -reconfigure -backend-config=backend.hcl")
    sys.exit(11)
stop(f"bucket {bucket}, key {same_key} (cached {config['key']}, configured {key} from {source})")
GUARD
  case "$?" in
    10) terraform -chdir="$1" init -backend-config=backend.hcl ;;
    11) terraform -chdir="$1" init -reconfigure -backend-config=backend.hcl ;;
    *) return 1 ;;
  esac
}
