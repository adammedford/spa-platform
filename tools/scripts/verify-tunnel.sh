#!/usr/bin/env bash
#
# verify-tunnel.sh -- check that a health path answers 200 through the tunnel, over TLS this
# workstation trusts.
#
# Task 1.6b (#57): the "verified tunnel" of docs/plan/05-infrastructure-and-cost.md, Workstation
# access, step 4. With tools/scripts/tunnel.sh running in another terminal:
#
#   tools/scripts/verify-tunnel.sh /           # the sample image, which answers / only
#   tools/scripts/verify-tunnel.sh /healthz    # the gateway (contracts/pipeline.md, Health endpoints)
#
# It requests https://app1.spa-poc.test:8443<path> and passes only when all of these hold:
#   - the name resolved to the loopback address, so the request went into the tunnel and not
#     somewhere else (the hosts-file line certs.sh names);
#   - TLS validated against this workstation's own trust store: no -k, no CA file of its own, no
#     curlrc (-q) and no CA bundle from the environment, so a pass means the local authority
#     certs.sh trusts is the one that signed what the ALB presented;
#   - the answer was 200, with no redirect followed and no proxy in the way.
# A failure says which of those broke, and a connection the tunnel accepted but nothing answered
# reads as a dead session (risk 15 in docs/plan/06-phase-2-and-risks.md).
#
# It needs no AWS credentials: it reads nothing but the one request.
#
# Exit status: 0 when the path answered 200 over trusted TLS through the tunnel, 1 when it did not,
# 2 when it did not start (a usage error, or no curl).
#
# Written for the bash 3.2 that ships with macOS, like tools/scripts/certs.sh.
# Owner: workstream 1, AWS and IaC.

set -euo pipefail
umask 077

PROGRAM="verify-tunnel"
DOMAIN="${SPA_CERT_DOMAIN:-spa-poc.test}"
HOST=""
PORT="${SPA_TUNNEL_PORT:-8443}"
REQUEST_PATH=""

refuse() {
  printf '%s: %s\n' "$PROGRAM" "$*" >&2
  exit 2
}

fail() {
  printf '%s: FAIL: %s\n' "$PROGRAM" "$*" >&2
  exit 1
}

usage() {
  # The header above, up to the Owner line, is this script's manual.
  sed -n '3,/^# Owner:/p' "$0" | sed 's/^# \{0,1\}//'
  cat <<EOF

Usage: verify-tunnel.sh <path> [--host NAME] [--port N]
  <path>        The health path, absolute: / on the sample image, /healthz on the gateway.
  --host NAME   The app host to ask. Default: app1.\$SPA_CERT_DOMAIN, else app1.spa-poc.test.
  --port N      The tunnel's local port. Default: \$SPA_TUNNEL_PORT, else 8443.
  -h, --help    This text.
EOF
}

value_of() {
  [ "$2" -ge 2 ] || refuse "$1 needs a value. Try --help."
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --host) value_of "$1" "$#"; HOST="$2"; shift ;;
    --port) value_of "$1" "$#"; PORT="$2"; shift ;;
    -h | --help)
      usage
      exit 0
      ;;
    -*) refuse "unknown option '$1'. Try --help." ;;
    *)
      [ -z "$REQUEST_PATH" ] || refuse "give one path, not '${REQUEST_PATH}' and '$1'"
      REQUEST_PATH="$1"
      ;;
  esac
  shift
done

[ -n "$REQUEST_PATH" ] || refuse "give the health path: / on the sample image, /healthz on the gateway. Try --help."
# The form modules/cell's health_check_path takes (variables.tf): absolute, no query string, and
# nothing a URL would need escaped.
case "$REQUEST_PATH" in
  /*) ;;
  *) refuse "the path must start with /: got '${REQUEST_PATH}'" ;;
esac
case "$REQUEST_PATH" in
  *[!A-Za-z0-9._~/-]*) refuse "the path may hold letters, digits and ._~/- only, with no query string: got '${REQUEST_PATH}'" ;;
esac
case "$PORT" in
  '' | *[!0-9]* | 0*) refuse "--port must be a port number: got '${PORT}'" ;;
esac
[ "$PORT" -le 65535 ] || refuse "--port must be a port number: got '${PORT}'"
[ -n "$HOST" ] || HOST="app1.${DOMAIN}"
case "$HOST" in
  *[!A-Za-z0-9.-]* | "") refuse "--host must be a host name: got '${HOST}'" ;;
esac

command -v curl > /dev/null 2>&1 || refuse "curl is not on PATH, so the tunnel cannot be checked"

URL="https://${HOST}:${PORT}${REQUEST_PATH}"
HOSTS_LINE="127.0.0.1 app1.${DOMAIN} app2.${DOMAIN} app3.${DOMAIN}"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/${PROGRAM}.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT
ERR="${WORK}/err"

# The trust store is the workstation's, and nothing else may answer for it. curl takes a CA bundle
# from CURL_CA_BUNDLE, SSL_CERT_FILE and SSL_CERT_DIR, and reads a curlrc from CURL_HOME or HOME
# unless -q comes first. https://curl.se/docs/manpage.html#-q and #--cacert
unset CURL_CA_BUNDLE SSL_CERT_FILE SSL_CERT_DIR CURL_HOME

# --noproxy '*': a proxy from the environment would carry the request away from 127.0.0.1.
# --proto =https: never fall back to plain HTTP. No --location: a redirect is not a health answer.
status=0
answer="$(curl -q --silent --show-error --noproxy '*' --proto '=https' \
  --connect-timeout 10 --max-time 20 --output /dev/null \
  --write-out '%{http_code} %{remote_ip} %{ssl_verify_result}' \
  "$URL" 2> "$ERR")" || status=$?

detail="$(awk 'NF { line = $0 } END { print line }' "$ERR")"

# curl's exit codes: https://curl.se/docs/manpage.html#EXIT
case "$status" in
  0) ;;
  6) fail "${HOST} does not resolve (${detail}). Add this line to /etc/hosts: ${HOSTS_LINE}" ;;
  7) fail "nothing answers on ${HOST}:${PORT} (${detail}). start tools/scripts/tunnel.sh in another terminal, then run this again." ;;
  60) fail "this workstation does not trust the certificate ${HOST}:${PORT} presented (${detail}). Run 'tools/scripts/certs.sh trust', or 'certs.sh fetch-ca' on a second workstation, then 'certs.sh status'." ;;
  28 | 35 | 52 | 56) fail "the tunnel took the connection but nothing answered through it (${detail}): the session has ended or the task is gone. Restart tools/scripts/tunnel.sh." ;;
  *) fail "curl exited ${status} asking ${URL} (${detail})" ;;
esac

code="${answer%% *}"
rest="${answer#* }"
remote_ip="${rest%% *}"
verify_result="${rest##* }"

# Only the loopback is the tunnel: the hosts line points the app hosts at 127.0.0.1, where
# tunnel.sh's port is, and a name that resolved anywhere else was answered by something else.
case "$remote_ip" in
  127.0.0.1 | ::1) ;;
  *) fail "${HOST} resolved to ${remote_ip}, so the request went not through the tunnel but elsewhere. Add this line to /etc/hosts: ${HOSTS_LINE}" ;;
esac
[ "$verify_result" = "0" ] || fail "TLS verification reported ${verify_result} for ${HOST}:${PORT}"
[ "$code" = "200" ] ||
  fail "${URL} answered ${code}, not 200. The sample image answers / and the gateway /healthz: is the path the one for the image that runs?"

printf '%s: pass: %s answered 200 through the tunnel (%s), over TLS this workstation trusts\n' \
  "$PROGRAM" "$URL" "$remote_ip"
