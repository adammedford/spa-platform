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
NEED_TERRAFORM="1.11"
NEED_AWSCLI="2.0"
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

echo "Workstation: $(uname -s) $(uname -m)"
echo
echo "Needed on day one (tasks I-1, I-2, 1.0, 1.6a, 5.4 and the first G0 run)"

# git
if have git; then
  ok "git" "$(first_version "$(git --version 2>/dev/null)")"
else
  missing "git" "xcode-select --install"
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

# Terraform
if have terraform; then
  tf=$(first_version "$(terraform version 2>/dev/null | head -n 1)")
  if [ -n "$tf" ] && ver_ge "$tf" "$NEED_TERRAFORM"; then
    ok "Terraform" "$tf"
  else
    missing "Terraform >= ${NEED_TERRAFORM}" "found: ${tf:-unknown}. brew install hashicorp/tap/terraform"
  fi
else
  missing "Terraform >= ${NEED_TERRAFORM}" "brew install hashicorp/tap/terraform"
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

if [ "$aws_ok" -eq 1 ]; then
  acct=$(AWS_MAX_ATTEMPTS=1 aws sts get-caller-identity --query Account --output text \
          --cli-connect-timeout 5 --cli-read-timeout 10 2>/dev/null)
  if [ -n "$acct" ] && [ "$acct" != "None" ]; then
    tail4=$(printf '%s' "$acct" | sed 's/.*\(....\)$/\1/')
    region=$(aws configure get region 2>/dev/null)
    ok "AWS credentials" "account ending ${tail4}, region ${region:-not set (the plan uses us-east-1)}"
  else
    missing "AWS credentials" "no working credentials. aws configure sso, or aws configure"
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

# GitHub CLI, optional
if have gh; then
  if gh auth status >/dev/null 2>&1; then
    ok "GitHub CLI" "signed in  (optional, for the CI of task I-5)"
  else
    info "GitHub CLI" "installed, not signed in  (optional)"
  fi
else
  info "GitHub CLI" "not installed  (optional)"
fi

echo
if [ "$missing_now" -eq 0 ]; then
  echo "Day one: ready. ${missing_later} later item(s) open."
  exit 0
fi
echo "Day one: ${missing_now} item(s) missing. ${missing_later} later item(s) open."
exit 1
