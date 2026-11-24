#!/usr/bin/env bash
# G1a's exit test: the gateway starts and answers 404 to an unknown host. Owner: integrator.
#
# It starts the gateway with a whole platform configuration, contracts/platform-config.example.json
# as environment variables (#544, ADR-0042): a gateway that validates its configuration at start-up
# refuses to start with none.
#
# It starts the gateway twice. The first run serves, and the five probes below prove it. The second
# (#1401, ADR-0042 decision 2) starts the same environment with Session__MaxSessions=0, which the
# schema refuses, and expects the process to exit non-zero within the start-up wait with a log that
# names Session:MaxSessions. The suite proves that refusal only against doubles; this is the one
# place the real process meets a refused configuration. Its lines name a key and an exit status,
# never a configured value, so it never prints the refused gateway's log.
#
# Nothing else proves it: the suite never starts the gateway, so a composition root that throws, or
# a settings file the configuration binder rejects, builds and tests clean. Both have happened.
# CI runs this after its Release build; run it locally after a build of the same configuration.
#
#     tools/smoke.sh [configuration] [port]      # defaults: Release 5080
#
# It publishes with --no-build, so it never builds: the build before it is the one being proved.
#
# Host-based, because host resolution is what answers 404. curl sends the Host header itself, which
# is right on a CI runner and on a workstation. In a proxied agent sandbox it is not: the proxy
# answers on the Host header and a spoofed host never reaches the process (see AGENTS.md).

set -euo pipefail

configuration="${1:-Release}"
port="${2:-5080}"

cd "$(git rev-parse --show-toplevel)"

out="$(mktemp -d)"
dotnet publish src/Gateway/Gateway.csproj --configuration "$configuration" --no-build -o "$out" > /dev/null

# The platform configuration is contracts/platform-config.example.json itself, read here rather
# than copied, in the deployed form: a colon is a double underscore, an array's items are numbered
# keys and every value is a string. An issuer's name can carry a hyphen, which a shell variable's
# name cannot, so env sets each pair.
config=()
while IFS= read -r pair; do
  config+=("$pair")
done < <(python3 -I -c '
import json, sys
def walk(key, value):
    if isinstance(value, dict):
        for name, item in value.items():
            yield from walk(key + [name], item)
    elif isinstance(value, list):
        for index, item in enumerate(value):
            yield from walk(key + [str(index)], item)
    else:
        text = ("true" if value else "false") if isinstance(value, bool) else str(value)
        yield "__".join(key) + "=" + text
for pair in walk([], json.load(open(sys.argv[1]))):
    print(pair)
' contracts/platform-config.example.json)

# The registry's S3 client is to find no AWS Region: each poll then logs "The registry could not
# be polled (AmazonClientException: No RegionEndpoint or ServiceURL configured)" and /readyz stays
# 503, which is what the probes expect. A
# workstation's region or profile, or an instance's metadata, would instead send a real GetObject to
# the example's placeholder bucket, so the gateway is started with none of them.
aws_off=(
  -u AWS_REGION -u AWS_DEFAULT_REGION -u AWS_PROFILE -u AWS_DEFAULT_PROFILE
  -u AWS_ACCESS_KEY_ID -u AWS_SECRET_ACCESS_KEY -u AWS_SESSION_TOKEN
  AWS_CONFIG_FILE=/dev/null AWS_SHARED_CREDENTIALS_FILE=/dev/null AWS_EC2_METADATA_DISABLED=true
)

env "${aws_off[@]}" "${config[@]}" ASPNETCORE_HTTP_PORTS="$port" dotnet "$out/Gateway.dll" > "$out/gateway.log" 2>&1 &
gateway=$!
trap 'kill $gateway 2> /dev/null || true; rm -rf "$out"' EXIT

for _ in $(seq 1 30); do
  if curl -fsS -o /dev/null "http://127.0.0.1:$port/healthz" 2> /dev/null; then break; fi
  sleep 1
done

probe() {
  curl -s -o /dev/null -w '%{http_code}' -H "Host: $1" "http://127.0.0.1:$port$2"
}

expect() {
  got="$(probe "$1" "$2")"
  if [ "$got" != "$3" ]; then
    echo "::error::Host $1 $2 answered $got, expected $3"
    echo "--- gateway output" >&2
    tail -20 "$out/gateway.log" >&2
    exit 1
  fi
  echo "  $1 $2 -> $got"
}

# /healthz answers on any host, because it runs before host resolution. /readyz is 503 until a
# snapshot is live, which cannot happen on stubs. Every app path is 404 while the registry is empty:
# with no registry, every host is an unknown host.
expect nope.example      /healthz       200
expect nope.example      /readyz        503
expect nope.example      /              404
expect app1.spa-poc.test /              404
expect app1.spa-poc.test /assets/app.js 404

# The refusal case. The serving gateway stops first, so the refused one cannot fail on a port in use
# instead of on its configuration. Session__MaxSessions=0 comes after the example's pairs, and env
# sets its pairs in order, so it replaces the example's 10000. The start-up wait is the first run's
# 30 seconds. A failure says which of the three expectations it broke; the refused gateway's log is
# not printed, because a gateway that does not refuse may log what it was configured with.
kill "$gateway" 2> /dev/null || true
wait "$gateway" 2> /dev/null || true

env "${aws_off[@]}" "${config[@]}" Session__MaxSessions=0 ASPNETCORE_HTTP_PORTS="$port" dotnet "$out/Gateway.dll" > "$out/refused.log" 2>&1 &
gateway=$!

refusal_failed() {
  echo "::error::With Session__MaxSessions=0 the gateway $1, expected it to refuse to start (ADR-0042)"
  exit 1
}

# A refusal ends in an unhandled exception, which aborts the process, and bash reports the aborted
# job on stderr with the command's text. The block's stderr is discarded so that line never prints.
status=0
{
  for _ in $(seq 1 30); do
    if ! kill -0 "$gateway"; then break; fi
    sleep 1
  done
  if kill -0 "$gateway"; then
    refusal_failed "was still running after the 30-second start-up wait"
  fi
  wait "$gateway" || status=$?
} 2> /dev/null
if [ "$status" -eq 0 ]; then
  refusal_failed "exited with status 0"
fi
if ! grep -qF 'Session:MaxSessions' "$out/refused.log"; then
  refusal_failed "exited with status $status and its log does not name Session:MaxSessions"
fi
echo "  Session__MaxSessions=0 -> exit $status, log names Session:MaxSessions"
