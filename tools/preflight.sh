#!/usr/bin/env bash
# Reports which workstation dependencies are present. It changes nothing.
#
#   tools/preflight.sh
#
# Exit code 0 when everything the day-one tasks need is present, 1 otherwise.
# "Later" items never fail the check. docs/dependencies.md explains each row.
# Written for the bash 3.2 that ships with macOS. Owner: integrator.

set -u

NEED_DOTNET_MAJOR="10.0"
NEED_AWSCLI="2.0"
NEED_PYTHON="3.9"
HOSTS="app1.spa-poc.test app2.spa-poc.test app3.spa-poc.test"

missing_now=0
missing_later=0

have() { command -v "$1" >/dev/null 2>&1; }

row() { printf '  %-8s %-24s %s\n' "$1" "$2" "$3"; }
ok() { row "ok" "$1" "$2"; }
info() { row "info" "$1" "$2"; }
missing() { row "MISSING" "$1" "$2"; missing_now=$((missing_now + 1)); }
later() { row "later" "$1" "$2"; missing_later=$((missing_later + 1)); }

# ver_ge A B: true when dotted version A is at least B.
ver_ge() {
  local a b i x y
  IFS=. read -r -a a <<<"$1"
  IFS=. read -r -a b <<<"$2"
  for i in 0 1 2 3; do
    x=${a[$i]:-0}; y=${b[$i]:-0}
    x=${x%%[!0-9]*}; y=${y%%[!0-9]*}
    x=${x:-0}; y=${y:-0}
    if [ "$x" -gt "$y" ]; then return 0; fi
    if [ "$x" -lt "$y" ]; then return 1; fi
  done
  return 0
}

# first_version TEXT: the first dotted number in TEXT, such as 2.37.12.
first_version() { printf '%s\n' "$1" | sed -n 's/[^0-9]*\([0-9][0-9]*\(\.[0-9][0-9]*\)\{1,3\}\).*/\1/p' | head -n 1; }

# The directory this script sits in, resolved once, here. The files beside the script are read from
# it: tools/terraform-floor.sh just below, and tools/aws-project.env and tools/aws-project.local.env
# further down, where the reason it is this directory and not the caller's is given; and
# .terraform-version is read from the directory above it.
#
# CDPATH is cleared because an exported one sends `cd` to another directory of the same name, and
# `cd` prints it, so every path built from this one would name another file over two lines: bash's
# manual, cd, https://www.gnu.org/software/bash/manual/html_node/Bourne-Shell-Builtins.html. Even
# CDPATH=. does it, since `cd` prints a directory found through any entry that is not empty. `--`
# ends each command's options, so a path starting with a hyphen is a path: POSIX utility syntax
# guideline 10, https://pubs.opengroup.org/onlinepubs/9799919799/basedefs/V1_chap12.html#tag_12_02.
# The same fix as tools/land.sh's body path (#66); tools/preflight_tests.py has both cases (#96).
tools_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"

# The Terraform floor's version and where it is installed come from the one file and the one
# helper that tools/check.sh and CI read them through (#69), so this cannot name a different floor
# from the one the check runs. The `cd` below is handed an absolute path, which CDPATH is never
# searched for, so it needs neither guard above.
. "$tools_dir/terraform-floor.sh"
floor_file="$(cd "$tools_dir/.." && pwd)/.terraform-version"

echo "Workstation: $(uname -s) $(uname -m)"
echo
echo "Needed on day one (tasks I-1, I-2, 1.0, 1.6a, 5.4 and the first G0 run)"

# git
if have git; then
  ok "git" "$(first_version "$(git --version 2>/dev/null)")"
else
  missing "git" "xcode-select --install"
fi

# Python and the GitHub CLI. tools/land.sh, the one sanctioned merge path, refuses to start without
# either, and its gh refusal sends the reader here; until #96 this script reported gh as optional
# and python3 not at all, so the machine land.sh sent a reader to diagnose read "Day one: ready".
#
# python3 runs the checks .githooks/pre-push and CI run, their test companions, tools/roadmap.py and
# land.sh's review record reader, all kept to the 3.9 that macOS's developer tools ship
# (docs/dependencies.md, D-3). Before those tools are installed macOS still has a python3 on PATH,
# which reports no version and asks for them, so having one is not enough.
if have python3; then
  pyv=$(first_version "$(python3 --version 2>&1)")
  if [ -n "$pyv" ] && ver_ge "$pyv" "$NEED_PYTHON"; then
    ok "Python ${NEED_PYTHON} or later" "$pyv"
  else
    missing "Python ${NEED_PYTHON} or later" "found: ${pyv:-unknown}. xcode-select --install"
  fi
else
  missing "Python ${NEED_PYTHON} or later" "xcode-select --install"
fi

# The tracker is GitHub Issues, worked through gh, and every gh call land.sh makes needs a signed-in
# account, so installed is not enough either. gh auth status exits 1 when an account it knows has a
# problem: https://cli.github.com/manual/gh_auth_status
if have gh; then
  if gh auth status >/dev/null 2>&1; then
    ok "GitHub CLI" "signed in"
  else
    missing "GitHub CLI" "installed, not signed in. gh auth login"
  fi
else
  missing "GitHub CLI" "brew install gh, then gh auth login"
fi

# .NET SDK
if have dotnet; then
  sdk=$(dotnet --list-sdks 2>/dev/null | awk '{print $1}' | grep "^${NEED_DOTNET_MAJOR}\." | sort -t. -k3,3n | tail -n 1)
  if [ -n "$sdk" ]; then
    ok ".NET SDK ${NEED_DOTNET_MAJOR}" "$sdk"
  else
    found=$(dotnet --list-sdks 2>/dev/null | awk '{print $1}' | tr '\n' ' ')
    missing ".NET SDK ${NEED_DOTNET_MAJOR}" "found: ${found:-none}. brew install --cask dotnet-sdk"
  fi
else
  missing ".NET SDK ${NEED_DOTNET_MAJOR}" "brew install --cask dotnet-sdk"
fi

# nuget.org
if have curl; then
  if curl -fsS -m 15 -o /dev/null https://api.nuget.org/v3/index.json 2>/dev/null; then
    ok "nuget.org" "reachable"
  else
    missing "nuget.org" "not reachable from here, so dotnet restore will fail"
  fi
else
  info "nuget.org" "curl not found, so this was not checked"
fi

# Terraform. Two rows, because two of them matter. The floor is the version CI runs, which
# tools/check.sh runs when this machine has it and names a substitute for when it does not; on
# 2026-10-17 six overnight agents each downloaded it by hand instead. The terraform on PATH is what
# an apply runs, and every root's required_version asks for at least the floor.
if floor=$(terraform_floor_read "$floor_file"); then
  terraform_choose "$floor"
  if [ "$terraform_used" = "$floor" ]; then
    ok "Terraform floor ${floor}" "${terraform_bin}, which CI runs and tools/check.sh uses"
  else
    floor_dir=$(terraform_floor_dir "$floor")
    if [ -n "$terraform_bin" ]; then
      instead="runs ${terraform_used:-a terraform of no known version} instead"
    else
      instead="runs no Terraform"
    fi
    # hc-install is HashiCorp's installer, from the tap the Brewfile already names; it verifies
    # the release's checksums and signature (https://github.com/hashicorp/hc-install#cli).
    missing "Terraform floor ${floor}" "not installed, so tools/check.sh ${instead}. brew install hashicorp/tap/hc-install && mkdir -p ${floor_dir} && hc-install install -version ${floor} -path ${floor_dir} terraform"
  fi
else
  floor=""
  missing "Terraform floor" "${floor_file} does not name one exact version, such as 1.11.0"
fi

if have terraform; then
  tf=$(terraform_version_of terraform)
  if [ -n "$tf" ] && { [ -z "$floor" ] || ver_ge "$tf" "$floor"; }; then
    ok "Terraform" "${tf} on PATH"
  else
    missing "Terraform >= ${floor:-the floor}" "found: ${tf:-unknown}. brew install hashicorp/tap/terraform"
  fi
else
  missing "Terraform >= ${floor:-the floor}" "brew install hashicorp/tap/terraform"
fi

# AWS CLI and credentials
aws_ok=0
if have aws; then
  awsv=$(first_version "$(aws --version 2>&1)")
  if [ -n "$awsv" ] && ver_ge "$awsv" "$NEED_AWSCLI"; then
    ok "AWS CLI v2" "$awsv"
    aws_ok=1
  else
    missing "AWS CLI v2" "found: ${awsv:-unknown}. brew install awscli"
  fi
else
  missing "AWS CLI v2" "brew install awscli"
fi

# The project, the profile and the Region have to be the intended ones, which tools/aws-project.env
# names. This check printed the account and the Region for weeks and compared them to nothing, so a
# sitting applied the bootstrap root into the sample project AWS creates beside the platform project
# and spent an evening on the sample project's guardrails. Having the values and no expectation to
# hold them to is what made that possible.
#
# Resolved from the script's own directory, tools_dir above, rather than the caller's. Every other
# row here is cwd-independent, and a guard that reports "nothing says which project is intended"
# when run from one directory up is the same silence the incident ran on, printed in a reassuring
# colour.
project_env="$tools_dir/aws-project.env"

# One key, read the way sourcing the file would read it: last assignment wins, leading whitespace
# and an optional `export` prefix are accepted, and a trailing comment, trailing whitespace and
# surrounding quotes are all removed. Accepting an indent assumes what this file is -- a flat list
# of comments and assignments. In a file with shell constructs it would also match an assignment
# indented inside an `if`, a function or a heredoc, which sourcing would not reach.
#
# The parse has to agree with the shell, because the file documents itself as sourceable:
# a value that parsed differently here would compare against something no `export` could ever
# produce, and the remedy this script prints would not satisfy the check it came from.
#
# The `export` prefix is accepted because the keys are the canonical AWS ones, which makes writing
# `export AWS_PROFILE=...` the natural thing to do -- and without this the guard would refuse a
# correctly configured operator while claiming the file names no profile.
#
# A CR is stripped for tidiness, not for correctness: `.gitattributes` line 1 is `* text=auto
# eol=lf` and `git check-attr -a tools/aws-project.env` reports `eol: lf`, so a CRLF copy cannot
# come from a checkout on any platform. If one is contrived locally, sourcing it yields a trailing
# CR that this parse does not, and the guard then fails closed on two strings that look identical.
project_env_get() {
  sed -n "s/^[[:space:]]*\(export[[:space:]][[:space:]]*\)\{0,1\}$1=//p" "${2:-$project_env}" 2>/dev/null \
    | tail -n 1 | tr -d '\r' \
    | sed -e 's/[[:space:]][[:space:]]*#.*$//' -e 's/[[:space:]]*$//' \
          -e 's/^"\(.*\)"$/\1/' -e 's/^'\''\(.*\)'\''$/\1/'
}

want_profile=""
want_region=""
if [ -f "$project_env" ]; then
  want_profile=$(project_env_get AWS_PROFILE)
  want_region=$(project_env_get AWS_REGION)
fi

if [ "$aws_ok" -eq 1 ]; then
  acct=$(AWS_MAX_ATTEMPTS=1 aws sts get-caller-identity --query Account --output text \
          --cli-connect-timeout 5 --cli-read-timeout 10 2>/dev/null)

  # Two Regions, because they can disagree and both matter. cfg_region is what the profile is
  # configured for; eff_region is what this shell will actually build in. The AWS CLI and the
  # Terraform AWS provider both read AWS_REGION, then AWS_DEFAULT_REGION, then the profile's
  # config, so `aws configure get region` alone sees only the last of the three and would pass a
  # run that creates everything somewhere else.
  #   https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-envvars.html
  #   https://registry.terraform.io/providers/hashicorp/aws/latest/docs#environment-variables
  cfg_region=$(aws configure get region 2>/dev/null)
  eff_region=${AWS_REGION:-${AWS_DEFAULT_REGION:-$cfg_region}}

  if [ -n "$acct" ] && [ "$acct" != "None" ]; then
    tail4=$(printf '%s' "$acct" | sed 's/.*\(....\)$/\1/')
    ok "AWS credentials" "account ending ${tail4}, region ${eff_region:-not set}"
  else
    missing "AWS credentials" "no working credentials for profile '${AWS_PROFILE:-default}'. aws configure sso, or aws configure"
  fi

  # Checked whether or not the credentials resolved. Which profile is in use is a string comparison
  # that needs no account, and a profile that does not exist in ~/.aws/config is better diagnosed
  # by name here than as the "no working credentials" above, which sends the reader to aws
  # configure when the export they were told to run is the thing that is wrong.
  using=${AWS_PROFILE:-default}
  if [ ! -f "$project_env" ]; then
    missing "AWS project" "${project_env} is absent, so nothing says which project is intended"
  elif [ -z "$want_profile" ]; then
    missing "AWS project" "tools/aws-project.env names no AWS_PROFILE, so nothing says which project is intended"
  elif [ "$using" != "$want_profile" ]; then
    # The profile, not the account id: change request 1.5 kept account identifiers out of the
    # tracked tree, and on a correctly configured workstation the profile resolves to the account.
    missing "AWS project" "using profile '${using}', expected '${want_profile}'. export AWS_PROFILE=${want_profile}"
  elif [ -n "${AWS_ACCESS_KEY_ID:-}" ]; then
    # Naming the right profile is not the same as using it. Static keys in the environment outrank
    # a profile's own credentials, so the account reached is whichever one those keys belong to
    # while every row above still says 'spa-platform'. Checked without a call: the variable's
    # presence is the whole condition.
    #   https://docs.aws.amazon.com/cli/latest/userguide/cli-chap-authentication.html
    missing "AWS project" "profile '${using}' is named, but AWS_ACCESS_KEY_ID in the environment outranks it. unset AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY AWS_SESSION_TOKEN"
  elif [ -n "$want_region" ] && [ "$eff_region" != "$want_region" ]; then
    missing "AWS Region" "this shell resolves to '${eff_region:-unset}', expected '${want_region}'. set -a; . tools/aws-project.env; set +a"
  elif [ -n "$want_region" ] && [ "$cfg_region" != "$want_region" ]; then
    missing "AWS Region" "profile '${using}' is configured for '${cfg_region:-unset}', expected '${want_region}', so a shell that has not sourced tools/aws-project.env would use it. aws configure set region ${want_region} --profile ${using}"
  else
    ok "AWS project" "profile '${using}', region ${eff_region}, as tools/aws-project.env names"
  fi

  # The profile is not the project. `aws login` stores whichever console session the human picked,
  # and on 2026-10-17 the same named profile resolved to a different project than it had two days
  # earlier, while the row above said ok both times: the stack had been built in the sample project
  # AWS creates beside the platform project. So the account is compared too, against a value kept
  # out of the tracked tree as change request 1.5 asks: tools/aws-project.local.env, git-ignored,
  # one line, AWS_ACCOUNT_ID=<12 digits>. The wizard that runs an apply writes it after the human
  # confirms the project's name in AWS Settings, which is the only place that name is shown.
  local_env="$tools_dir/aws-project.local.env"
  want_account=""
  [ -f "$local_env" ] && want_account=$(project_env_get AWS_ACCOUNT_ID "$local_env")
  if [ -z "$want_account" ]; then
    missing "AWS account" "nothing says which project's account is intended. Confirm the project's name in AWS Settings, then: echo AWS_ACCOUNT_ID=<its 12-digit account id> > tools/aws-project.local.env"
  elif [ -z "$acct" ] || [ "$acct" = "None" ]; then
    missing "AWS account" "not checked: no credentials resolved"
  elif [ "$acct" != "$want_account" ]; then
    want4=$(printf '%s' "$want_account" | sed 's/.*\(....\)$/\1/')
    missing "AWS account" "signed in to the account ending ${tail4}, expected the one ending ${want4}. Open that project's console from AWS Settings, then: aws login --profile ${using}"
  else
    ok "AWS account" "ending ${tail4}, as tools/aws-project.local.env names"
  fi
else
  missing "AWS credentials" "not checked: the AWS CLI is missing"
fi

# openssl, for certs.sh
if have openssl; then
  ok "openssl" "$(openssl version 2>/dev/null | cut -d' ' -f1-2)"
else
  missing "openssl" "brew install openssl@3"
fi

echo
echo "Needed later"

# Session Manager plugin
if have session-manager-plugin; then
  ok "Session Manager plugin" "$(session-manager-plugin --version 2>/dev/null | head -n 1)  (task 1.6b)"
else
  later "Session Manager plugin" "task 1.6b. brew install --cask session-manager-plugin"
fi

# Container engine
if have docker; then
  dv=$(docker version --format '{{.Server.Version}}' 2>/dev/null)
  if [ -n "$dv" ]; then
    ok "Container engine" "docker server ${dv}  (task 1.7)"
  else
    later "Container engine" "task 1.7. docker is installed but no engine is running"
  fi
else
  later "Container engine" "task 1.7. See decision D-2 in docs/dependencies.md"
fi

# Hosts file entries
hosts_missing=""
for h in $HOSTS; do
  if ! grep -Eq "^[[:space:]]*127\.0\.0\.1[[:space:]].*${h}" /etc/hosts 2>/dev/null; then
    hosts_missing="${hosts_missing} ${h}"
  fi
done
if [ -z "$hosts_missing" ]; then
  ok "Hosts file" "the three spa-poc.test names point at 127.0.0.1"
else
  later "Hosts file" "G0 against Cognito and task 1.6b. Add to /etc/hosts: 127.0.0.1 ${HOSTS}"
fi

# Local port for the tunnel and the G0 listener
if have lsof; then
  if lsof -nP -iTCP:8443 -sTCP:LISTEN >/dev/null 2>&1; then
    info "Port 8443" "in use now. G0 and the tunnel listen on it"
  else
    ok "Port 8443" "free"
  fi
fi

echo
if [ "$missing_now" -eq 0 ]; then
  echo "Day one: ready. ${missing_later} later item(s) open."
  exit 0
fi
echo "Day one: ${missing_now} item(s) missing. ${missing_later} later item(s) open."
exit 1
