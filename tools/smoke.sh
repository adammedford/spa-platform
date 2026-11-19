#!/usr/bin/env bash
# G1a's exit test: the gateway starts and answers 404 to an unknown host. Owner: integrator.
#
# It starts the gateway with a whole platform configuration, the required sections of
# contracts/platform-config.example.json as environment variables (#544, ADR-0042): a gateway that
# validates its configuration at start-up refuses to start with none.
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

# The required sections of contracts/platform-config.example.json, in the deployed form: a colon is
# a double underscore and every value is a string. With Registry:* set and no AWS Region the poller
# logs that the S3 client could not be built and /readyz stays 503, which is what the probes expect.
export Platform__Cell=poc
export Registry__Bucket=spa-platform-poc-111122223333-us-east-1-an
export Registry__Key=registry/registry.json
export Registry__PollSeconds=15
export Assets__Bucket=spa-platform-poc-111122223333-us-east-1-an
export Policy__AllowedHostSuffixes__0=.spa-poc.test:8443
export Policy__AllowedUpstreams__0=http://127.0.0.1:5080
export Policy__AllowedUpstreams__1=http://127.0.0.1:5081
export ForwardedHeaders__TrustedNetworks__0=10.20.0.0/16
export Session__Store=memory
export Session__MaxSessions=10000
# The issuer's name carries a hyphen, which a shell variable's name cannot, so env sets the three.
issuer=(
  "Issuers__cognito-poc__Authority=https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE"
  "Issuers__cognito-poc__Profile=cognito"
  "Issuers__cognito-poc__BrowserOrigin=https://spa-platform-poc-111122223333.auth.us-east-1.amazoncognito.com"
)

env "${issuer[@]}" ASPNETCORE_HTTP_PORTS="$port" dotnet "$out/Gateway.dll" > "$out/gateway.log" 2>&1 &
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
