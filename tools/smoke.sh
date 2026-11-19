#!/usr/bin/env bash
# G1a's exit test: the gateway starts and answers 404 to an unknown host. Owner: integrator.
#
# It starts the gateway with a whole platform configuration, contracts/platform-config.example.json
# as environment variables (#544, ADR-0042): a gateway that validates its configuration at start-up
# refuses to start with none.
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
