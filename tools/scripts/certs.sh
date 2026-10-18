#!/usr/bin/env bash
#
# certs.sh — the local certificate authority for a cell, and its wildcard
# certificate.
#
# Task 1.6a. A cell serves every app host from one wildcard listener with no
# listener rules (ADR-0010), so one authority and one certificate for
# `*.<domain>` cover app1, app2, app3 and anything onboarded later.
#
#   tools/scripts/certs.sh issue      # make the authority and the certificate
#   tools/scripts/certs.sh trust      # trust the authority on this workstation
#   tools/scripts/certs.sh import     # import the certificate to ACM
#   tools/scripts/certs.sh all        # the three above, in order
#
#   tools/scripts/certs.sh fetch-ca   # second workstation: trust the same
#                                     # authority, from ACM, with no key material
#   tools/scripts/certs.sh verify     # a local HTTPS listener on an app host,
#                                     # checked without -k
#   tools/scripts/certs.sh status     # what exists here and in ACM
#
# Terraform never creates the authority (docs/plan/05-infrastructure-and-cost.md,
# "What stays out of Terraform"). The ALB listener finds the certificate in ACM by
# its domain name, so this script runs before the platform root.
#
# Private keys never leave the certificates directory and are never printed. The
# directory and every key in it are mode 600/700, and .gitignore already excludes
# *.pem, *.key, *.p12 and .certs/. Nothing here writes a key to standard output,
# so `certs.sh ... | tee` is safe.
#
# Written for the bash 3.2 that ships with macOS, like tools/preflight.sh.
# Owner: workstream 1, AWS and IaC.

set -euo pipefail
umask 077

# --- defaults ----------------------------------------------------------------

CELL="${SPA_CELL:-poc}"
DOMAIN="${SPA_CERT_DOMAIN:-spa-poc.test}"
REGION="${AWS_REGION:-${AWS_DEFAULT_REGION:-us-east-1}}"
CERTS_DIR="${SPA_CERTS_DIR:-}"
VERIFY_HOST=""
VERIFY_PORT="${SPA_VERIFY_PORT:-8443}"
KEYCHAIN_KIND="${SPA_KEYCHAIN:-system}"
CA_DAYS="${SPA_CA_DAYS:-3650}"
# 397 days, not 825: Apple refuses a TLS server certificate with a longer
# validity, and a certificate Safari will not load is not worth issuing.
LEAF_DAYS="${SPA_LEAF_DAYS:-397}"
FORCE=0
NEW_CA=0
COMMAND=""

# The platform tag. teardown.sh queries it and test P-05 asserts it returns
# nothing, so everything this script creates in AWS carries it.
PLATFORM_TAG_KEY="Platform"
PLATFORM_TAG_VALUE="spa-platform"

# --- plumbing ----------------------------------------------------------------

PROGRAM="$(basename "$0")"
TMPDIR_RUN=""
SERVER_PID=""

say() { printf '%s\n' "$*"; }
step() { printf '\n== %s\n' "$*"; }
note() { printf '   %s\n' "$*"; }

die() {
  printf '%s: %s\n' "$PROGRAM" "$*" >&2
  exit 1
}

cleanup() {
  local code=$?
  if [ -n "$SERVER_PID" ] && kill -0 "$SERVER_PID" 2>/dev/null; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  [ -n "$TMPDIR_RUN" ] && [ -d "$TMPDIR_RUN" ] && rm -rf "$TMPDIR_RUN"
  return $code
}
trap cleanup EXIT
trap 'die "interrupted"' INT TERM

have() { command -v "$1" >/dev/null 2>&1; }

usage() {
  # The header comment above, up to the Owner line, is this script's manual.
  sed -n '2,31p' "$0" | sed 's/^# \{0,1\}//'
  cat <<EOF

Options
  --cell NAME        Cell name. Default: ${CELL}. Part of every resource name.
  --domain NAME      Certificate domain, without the wildcard. Default: ${DOMAIN}.
  --region NAME      AWS region for the ACM import. Default: ${REGION}.
  --dir PATH         Where keys and certificates live. Default: <repo>/.certs.
  --host NAME        Host for 'verify'. Default: app1.<domain>.
  --port N           Port for 'verify'. Default: ${VERIFY_PORT}.
  --keychain KIND    'system' (needs sudo, trusts every user) or 'login'.
                     Default: ${KEYCHAIN_KIND}.
  --ca-days N        Authority validity. Default: ${CA_DAYS}.
  --leaf-days N      Certificate validity. Default: ${LEAF_DAYS}.
  --force            Reissue the certificate, or overwrite a fetched authority,
                     instead of leaving what is there. Keeps the authority.
  --new-ca           Replace the authority itself. Every workstation that trusts
                     the old one loses trust, and the import must be redone.
  -h, --help         This text.
EOF
}

# --- argument parsing --------------------------------------------------------

while [ $# -gt 0 ]; do
  case "$1" in
    issue | trust | import | fetch-ca | verify | status | all)
      [ -z "$COMMAND" ] || die "give one command, not '$COMMAND' and '$1'"
      COMMAND="$1"
      ;;
    --cell) CELL="${2:-}"; shift ;;
    --domain) DOMAIN="${2:-}"; shift ;;
    --region) REGION="${2:-}"; shift ;;
    --dir) CERTS_DIR="${2:-}"; shift ;;
    --host) VERIFY_HOST="${2:-}"; shift ;;
    --port) VERIFY_PORT="${2:-}"; shift ;;
    --keychain) KEYCHAIN_KIND="${2:-}"; shift ;;
    --ca-days) CA_DAYS="${2:-}"; shift ;;
    --leaf-days) LEAF_DAYS="${2:-}"; shift ;;
    --force) FORCE=1 ;;
    --new-ca) NEW_CA=1; FORCE=1 ;;
    -h | --help) usage; exit 0 ;;
    *) die "unknown argument '$1'. Try --help." ;;
  esac
  shift
done

[ -n "$COMMAND" ] || { usage; exit 2; }

case "$CELL" in
  *[!a-z0-9-]* | "" | -* | *-)
    die "--cell must be lowercase letters, digits and inner hyphens: got '$CELL'"
    ;;
esac
case "$DOMAIN" in
  *.*) ;;
  *) die "--domain must be a dotted name, such as spa-poc.test: got '$DOMAIN'" ;;
esac
case "$KEYCHAIN_KIND" in
  system | login) ;;
  *) die "--keychain must be 'system' or 'login': got '$KEYCHAIN_KIND'" ;;
esac
case "$VERIFY_PORT" in
  '' | *[!0-9]*) die "--port must be a number: got '$VERIFY_PORT'" ;;
esac

# --- derived names -----------------------------------------------------------

repo_root() {
  if have git && git rev-parse --show-toplevel >/dev/null 2>&1; then
    git rev-parse --show-toplevel
  else
    cd "$(dirname "$0")/../.." && pwd
  fi
}

[ -n "$CERTS_DIR" ] || CERTS_DIR="$(repo_root)/.certs"

WILDCARD="*.${DOMAIN}"
# Every resource name carries the cell name (ADR-0012).
RESOURCE_NAME="spa-platform-${CELL}"
CA_CN="SPA Platform ${CELL} Local CA"
LEAF_CN="${WILDCARD}"

CA_KEY="${CERTS_DIR}/${RESOURCE_NAME}-ca.key"
CA_CERT="${CERTS_DIR}/${RESOURCE_NAME}-ca.pem"
CA_SERIAL="${CERTS_DIR}/${RESOURCE_NAME}-ca.srl"
LEAF_KEY="${CERTS_DIR}/${RESOURCE_NAME}-wildcard.key"
LEAF_CERT="${CERTS_DIR}/${RESOURCE_NAME}-wildcard.pem"
LEAF_CSR="${CERTS_DIR}/${RESOURCE_NAME}-wildcard.csr"
LEAF_CHAIN="${CERTS_DIR}/${RESOURCE_NAME}-wildcard.fullchain.pem"
ARN_FILE="${CERTS_DIR}/${RESOURCE_NAME}-wildcard.acm-arn"

[ -n "$VERIFY_HOST" ] || VERIFY_HOST="app1.${DOMAIN}"

# --- tools -------------------------------------------------------------------

# Prefer a real OpenSSL when Homebrew has one; macOS ships LibreSSL, which is
# fine for everything below but takes its extensions from a config file only.
pick_openssl() {
  if [ -n "${OPENSSL:-}" ]; then
    printf '%s\n' "$OPENSSL"
    return
  fi
  local candidate
  for candidate in /opt/homebrew/opt/openssl@3/bin/openssl /usr/local/opt/openssl@3/bin/openssl; do
    if [ -x "$candidate" ]; then
      printf '%s\n' "$candidate"
      return
    fi
  done
  have openssl || die "openssl not found. brew install openssl@3"
  command -v openssl
}

OPENSSL_BIN="$(pick_openssl)"

need_aws() {
  have aws || die "the AWS CLI v2 is not installed. brew install awscli"
  aws sts get-caller-identity >/dev/null 2>&1 ||
    die "no working AWS credentials for region ${REGION}. Run 'aws configure sso' or 'aws configure', then retry."
}

need_macos() {
  [ "$(uname -s)" = "Darwin" ] ||
    die "'$COMMAND' edits the macOS trust store and only runs on macOS. On another system, trust ${CA_CERT} the way that system does it."
  have security || die "the 'security' command is missing, so the trust store cannot be edited"
}

mktemp_run() {
  [ -n "$TMPDIR_RUN" ] && return
  TMPDIR_RUN="$(mktemp -d "${TMPDIR:-/tmp}/certs.sh.XXXXXX")"
  chmod 700 "$TMPDIR_RUN"
}

ensure_dir() {
  mkdir -p "$CERTS_DIR"
  chmod 700 "$CERTS_DIR"
}

# --- openssl configuration ---------------------------------------------------

# Two config files, one per certificate. They are written to a temporary
# directory because they are derived, not state: a stale file that no longer
# matched --domain would silently issue the wrong name.
#
# The distinguished name lives in the file and not in a -subj argument. LibreSSL,
# which is the openssl macOS ships, takes the subject from the config's
# distinguished_name section and ignores -subj for `req -x509`, so a single
# config with two DN sections gave the authority the certificate's common name.
write_openssl_conf() {
  mktemp_run
  CA_CONF="${TMPDIR_RUN}/ca.cnf"
  LEAF_CONF="${TMPDIR_RUN}/leaf.cnf"

  cat >"$CA_CONF" <<EOF
[ req ]
default_md         = sha256
prompt             = no
distinguished_name = dn
string_mask        = utf8only
x509_extensions    = ext_ca

[ dn ]
O  = SPA Platform
OU = cell ${CELL}
CN = ${CA_CN}

[ ext_ca ]
basicConstraints     = critical,CA:TRUE,pathlen:0
keyUsage             = critical,digitalSignature,cRLSign,keyCertSign
subjectKeyIdentifier = hash
EOF

  # [ req ] names no req_extensions on purpose. The request carries only the
  # distinguished name; every extension, the subject alternative names included,
  # is added by the signing step below, which is the only place that knows the
  # issuer and so the only place authorityKeyIdentifier can be computed.
  cat >"$LEAF_CONF" <<EOF
[ req ]
default_md         = sha256
prompt             = no
distinguished_name = dn
string_mask        = utf8only

[ dn ]
O  = SPA Platform
OU = cell ${CELL}
CN = ${LEAF_CN}

[ ext_leaf ]
basicConstraints       = critical,CA:FALSE
keyUsage               = critical,digitalSignature,keyEncipherment
extendedKeyUsage       = serverAuth
subjectKeyIdentifier   = hash
authorityKeyIdentifier = keyid,issuer
subjectAltName         = @san_leaf

[ san_leaf ]
DNS.1 = ${WILDCARD}
DNS.2 = ${DOMAIN}
EOF
}

# --- issue -------------------------------------------------------------------

leaf_is_usable() {
  [ -f "$CA_CERT" ] && [ -f "$CA_KEY" ] && [ -f "$LEAF_CERT" ] && [ -f "$LEAF_KEY" ] || return 1
  # Still valid in 30 days, chains to this authority, and carries the wildcard.
  "$OPENSSL_BIN" x509 -in "$LEAF_CERT" -noout -checkend 2592000 >/dev/null 2>&1 || return 1
  "$OPENSSL_BIN" verify -CAfile "$CA_CERT" "$LEAF_CERT" >/dev/null 2>&1 || return 1
  "$OPENSSL_BIN" x509 -in "$LEAF_CERT" -noout -text 2>/dev/null |
    grep -Fq "DNS:${WILDCARD}" || return 1
  return 0
}

cmd_issue() {
  ensure_dir
  step "Issue: authority '${CA_CN}' and certificate for ${WILDCARD}"

  if [ "$FORCE" -eq 0 ] && leaf_is_usable; then
    note "already present and valid for more than 30 days, so nothing to do"
    note "re-issue with --force"
    show_local
    return 0
  fi

  write_openssl_conf

  if [ "$NEW_CA" -eq 1 ] || [ ! -f "$CA_KEY" ] || [ ! -f "$CA_CERT" ]; then
    if [ -f "$CA_CERT" ]; then
      note "REPLACING the authority. Every workstation that trusts the old one,"
      note "and the certificate in ACM, must be redone: issue, trust, import, then"
      note "fetch-ca --force on each other workstation."
    fi
    note "creating the authority"
    "$OPENSSL_BIN" req -x509 -new -newkey rsa:4096 -nodes -sha256 \
      -days "$CA_DAYS" \
      -config "$CA_CONF" -extensions ext_ca \
      -keyout "$CA_KEY" -out "$CA_CERT" >/dev/null 2>&1 ||
      die "could not create the authority. Check that ${OPENSSL_BIN} works and ${CERTS_DIR} is writable."
    chmod 600 "$CA_KEY"
    chmod 644 "$CA_CERT"
    rm -f "$CA_SERIAL"
  else
    note "keeping the authority that is already here, so trust on other workstations survives"
  fi

  note "creating the certificate"
  "$OPENSSL_BIN" req -new -newkey rsa:2048 -nodes -sha256 \
    -config "$LEAF_CONF" \
    -keyout "$LEAF_KEY" -out "$LEAF_CSR" >/dev/null 2>&1 ||
    die "could not create the certificate request"
  chmod 600 "$LEAF_KEY"

  "$OPENSSL_BIN" x509 -req -in "$LEAF_CSR" -sha256 -days "$LEAF_DAYS" \
    -CA "$CA_CERT" -CAkey "$CA_KEY" -CAcreateserial -CAserial "$CA_SERIAL" \
    -extfile "$LEAF_CONF" -extensions ext_leaf \
    -out "$LEAF_CERT" >/dev/null 2>&1 ||
    die "could not sign the certificate"
  chmod 644 "$LEAF_CERT"
  rm -f "$LEAF_CSR"

  cat "$LEAF_CERT" "$CA_CERT" >"$LEAF_CHAIN"
  chmod 644 "$LEAF_CHAIN"

  # Prove it before claiming it.
  "$OPENSSL_BIN" verify -CAfile "$CA_CERT" "$LEAF_CERT" >/dev/null ||
    die "the certificate does not verify against the authority that signed it"
  keys_match || die "the certificate and its private key do not match"

  note "done"
  show_local
}

keys_match() {
  local a b
  a="$("$OPENSSL_BIN" x509 -noout -pubkey -in "$LEAF_CERT" 2>/dev/null | "$OPENSSL_BIN" dgst -sha256 2>/dev/null)"
  b="$("$OPENSSL_BIN" pkey -pubout -in "$LEAF_KEY" 2>/dev/null | "$OPENSSL_BIN" dgst -sha256 2>/dev/null)"
  [ -n "$a" ] && [ "$a" = "$b" ]
}

# The public halves. A second workstation has these after fetch-ca and no key, so
# the private key is required separately, by the two things that need one.
require_issued() {
  [ -f "$CA_CERT" ] ||
    die "no authority at ${CA_CERT}. Run '${PROGRAM} issue' first, or '${PROGRAM} fetch-ca' on a second workstation."
  [ -f "$LEAF_CERT" ] ||
    die "no certificate at ${LEAF_CERT}. Run '${PROGRAM} issue' first, or '${PROGRAM} fetch-ca' on a second workstation."
}

require_leaf_key() {
  [ -f "$LEAF_KEY" ] ||
    die "no private key at ${LEAF_KEY}. Only the workstation that issued the certificate has one, and it is never fetched from ACM. Run '${PROGRAM} $1' there."
}

ca_fingerprint() {
  "$OPENSSL_BIN" x509 -in "$1" -noout -fingerprint -sha256 2>/dev/null |
    sed 's/.*=//' | tr -d ':' | tr 'A-Z' 'a-z'
}

show_local() {
  local f
  for f in "$CA_CERT" "$LEAF_CERT"; do
    [ -f "$f" ] || continue
    note "$(basename "$f"): $("$OPENSSL_BIN" x509 -in "$f" -noout -subject -enddate 2>/dev/null | tr '\n' ' ')"
  done
  [ -f "$CA_CERT" ] && note "authority SHA-256: $(ca_fingerprint "$CA_CERT")"
  return 0
}

# --- trust -------------------------------------------------------------------

keychain_path() {
  if [ "$KEYCHAIN_KIND" = "system" ]; then
    printf '%s\n' "/Library/Keychains/System.keychain"
  else
    printf '%s\n' "${HOME}/Library/Keychains/login.keychain-db"
  fi
}

# True when the trust store already accepts the certificate this authority signed.
# Asking the leaf, rather than looking for the authority by name, is the question
# that matters and the one that survives a reissue.
trust_is_in_place() {
  [ -f "$LEAF_CERT" ] || return 1
  security verify-cert -c "$LEAF_CERT" >/dev/null 2>&1
}

cmd_trust() {
  need_macos
  require_issued
  step "Trust: add '${CA_CN}' to the ${KEYCHAIN_KIND} keychain"

  if [ "$NEW_CA" -eq 0 ] && trust_is_in_place; then
    note "the certificate already verifies against this workstation's trust store"
    return 0
  fi

  local keychain
  keychain="$(keychain_path)"
  [ -f "$keychain" ] || die "no keychain at ${keychain}"

  if [ "$KEYCHAIN_KIND" = "system" ]; then
    note "this needs administrator rights, so sudo will ask for your password"
    sudo security add-trusted-cert -d -r trustRoot \
      -k "$keychain" "$CA_CERT" ||
      die "could not add the authority to ${keychain}"
  else
    security add-trusted-cert -r trustRoot \
      -k "$keychain" "$CA_CERT" ||
      die "could not add the authority to ${keychain}"
  fi

  trust_is_in_place ||
    die "the authority was added but ${LEAF_CERT} still does not verify. Check for an older authority of the same name in Keychain Access."

  note "trusted. Authority SHA-256: $(ca_fingerprint "$CA_CERT")"
  note "to undo: sudo security delete-certificate -c '${CA_CN}' '${keychain}'"
}

# --- import to ACM -----------------------------------------------------------

# The ARN of an imported certificate for this domain that carries this cell's
# tags. Empty when there is none.
find_acm_certificate() {
  local arns arn tagged
  arns="$(aws acm list-certificates \
    --region "$REGION" \
    --includes keyTypes=RSA_2048,RSA_3072,RSA_4096,EC_prime256v1,EC_secp384r1 \
    --query "CertificateSummaryList[?DomainName=='${WILDCARD}'].CertificateArn" \
    --output text 2>/dev/null || true)"

  for arn in $arns; do
    tagged="$(aws acm list-tags-for-certificate \
      --region "$REGION" --certificate-arn "$arn" \
      --query "Tags[?Key=='Cell'&&Value=='${CELL}'].Value" \
      --output text 2>/dev/null || true)"
    if [ -n "$tagged" ] && [ "$tagged" != "None" ]; then
      printf '%s\n' "$arn"
      return 0
    fi
  done
  return 0
}

cmd_import() {
  need_aws
  require_issued
  step "Import: ${WILDCARD} into ACM in ${REGION}, tagged ${PLATFORM_TAG_KEY}=${PLATFORM_TAG_VALUE}"

  require_leaf_key import
  keys_match || die "the certificate and its private key do not match, so ACM would refuse the import"
  "$OPENSSL_BIN" x509 -in "$LEAF_CERT" -noout -checkend 0 >/dev/null ||
    die "the certificate has expired. Run '${PROGRAM} issue --force' first."

  # The tags go on in the same call as the first import. A certificate imported
  # and then left untagged would be invisible to both find_acm_certificate and
  # teardown's tag query, and a retry would import a second copy.
  local existing arn
  local tags
  tags=(
    "Key=${PLATFORM_TAG_KEY},Value=${PLATFORM_TAG_VALUE}"
    "Key=Cell,Value=${CELL}"
    "Key=Name,Value=${RESOURCE_NAME}-wildcard"
    # The domain without its wildcard: an ACM tag value may hold letters, numbers, spaces and
    # _.:/=+-@ only, so "*.${DOMAIN}" is refused with a ValidationException and nothing imports.
    # Nothing reads this tag; the lookup is by DomainName and the Cell tag (find_acm_certificate,
    # modules/cell/alb.tf). https://docs.aws.amazon.com/acm/latest/APIReference/API_Tag.html
    "Key=Domain,Value=${DOMAIN}"
    "Key=ManagedBy,Value=certs.sh"
  )

  existing="$(find_acm_certificate)"

  if [ -n "$existing" ]; then
    note "replacing the certificate already imported for this cell"
    note "  ${existing}"
    # A reimport keeps the ARN, so the ALB listener needs no change. ACM refuses
    # --tags together with --certificate-arn, so the tags are refreshed below.
    arn="$(aws acm import-certificate \
      --region "$REGION" \
      --certificate-arn "$existing" \
      --certificate "fileb://${LEAF_CERT}" \
      --private-key "fileb://${LEAF_KEY}" \
      --certificate-chain "fileb://${CA_CERT}" \
      --query CertificateArn --output text)" ||
      die "the reimport failed"
  else
    note "importing for the first time"
    arn="$(aws acm import-certificate \
      --region "$REGION" \
      --certificate "fileb://${LEAF_CERT}" \
      --private-key "fileb://${LEAF_KEY}" \
      --certificate-chain "fileb://${CA_CERT}" \
      --tags "${tags[@]}" \
      --query CertificateArn --output text)" ||
      die "the import failed"
  fi

  [ -n "$arn" ] && [ "$arn" != "None" ] || die "ACM returned no certificate ARN"

  # Idempotent, and the only tagging step the reimport path has: ACM refuses
  # --tags together with --certificate-arn.
  aws acm add-tags-to-certificate \
    --region "$REGION" --certificate-arn "$arn" --tags "${tags[@]}" ||
    die "the certificate is in ACM as ${arn} but could not be tagged. Fix it before the platform root runs, or teardown will not find it: aws acm add-tags-to-certificate --region ${REGION} --certificate-arn ${arn} --tags Key=${PLATFORM_TAG_KEY},Value=${PLATFORM_TAG_VALUE} Key=Cell,Value=${CELL}"

  printf '%s\n' "$arn" >"$ARN_FILE"
  chmod 644 "$ARN_FILE"

  note "in ACM as ${arn}"
  note "the platform root's listener finds it by domain name, so no ARN is passed to Terraform"
}

# --- fetch the authority from ACM, for a second workstation ------------------

cmd_fetch_ca() {
  need_aws
  step "Fetch: the authority for cell ${CELL} from ACM in ${REGION}"

  local arn
  arn="$(find_acm_certificate)"
  [ -n "$arn" ] ||
    die "no imported certificate for ${WILDCARD} tagged Cell=${CELL} in ${REGION}. Run '${PROGRAM} import' on the workstation that holds the authority."

  note "from ${arn}"
  ensure_dir
  mktemp_run

  # The chain of an imported certificate is the authority that signed it. Only
  # the public certificate comes back: ACM never returns an imported private key,
  # which is what makes this the right way to seed a second workstation.
  aws acm get-certificate \
    --region "$REGION" --certificate-arn "$arn" \
    --query CertificateChain --output text >"${TMPDIR_RUN}/ca.pem" ||
    die "could not read the certificate chain from ACM"

  "$OPENSSL_BIN" x509 -in "${TMPDIR_RUN}/ca.pem" -noout -subject >/dev/null 2>&1 ||
    die "what ACM returned as the chain is not a certificate"

  local fetched_fp
  fetched_fp="$(ca_fingerprint "${TMPDIR_RUN}/ca.pem")"

  if [ -f "$CA_CERT" ]; then
    local local_fp
    local_fp="$(ca_fingerprint "$CA_CERT")"
    if [ "$local_fp" = "$fetched_fp" ]; then
      note "already here, and the same authority: ${fetched_fp}"
    elif [ "$FORCE" -eq 1 ]; then
      note "replacing a different authority at ${CA_CERT}"
      cp "${TMPDIR_RUN}/ca.pem" "$CA_CERT"
    else
      die "a different authority is already at ${CA_CERT} (${local_fp}, ACM has ${fetched_fp}). Overwrite it with --force, after checking nothing still depends on it."
    fi
  else
    cp "${TMPDIR_RUN}/ca.pem" "$CA_CERT"
    note "written to ${CA_CERT}: ${fetched_fp}"
  fi
  chmod 644 "$CA_CERT"

  # Also keep the server certificate, so 'verify' and 'status' have something to
  # check. The private key stays on the workstation that issued it, so this
  # workstation can trust the authority but cannot serve the certificate.
  aws acm get-certificate \
    --region "$REGION" --certificate-arn "$arn" \
    --query Certificate --output text >"$LEAF_CERT" ||
    die "could not read the certificate from ACM"
  chmod 644 "$LEAF_CERT"
  printf '%s\n' "$arn" >"$ARN_FILE"

  step "Trust the fetched authority"
  if [ "$(uname -s)" != "Darwin" ] || ! have security; then
    note "not macOS, so the trust store is left alone"
    note "trust ${CA_CERT} the way this system does it, then run '${PROGRAM} status'"
    return 0
  fi

  local keychain
  keychain="$(keychain_path)"
  if [ "$FORCE" -eq 0 ] && trust_is_in_place; then
    note "this workstation already trusts it"
  elif [ "$KEYCHAIN_KIND" = "system" ]; then
    note "this needs administrator rights, so sudo will ask for your password"
    sudo security add-trusted-cert -d -r trustRoot -k "$keychain" "$CA_CERT" ||
      die "could not add the authority to ${keychain}"
  else
    security add-trusted-cert -r trustRoot -k "$keychain" "$CA_CERT" ||
      die "could not add the authority to ${keychain}"
  fi

  say ""
  note "this workstation now trusts cell ${CELL}'s authority. No private key was fetched or stored."
  note "next: add '127.0.0.1 app1.${DOMAIN} app2.${DOMAIN} app3.${DOMAIN}' to /etc/hosts, then run tunnel.sh"
}

# --- verify ------------------------------------------------------------------

hosts_entry_present() {
  grep -Eq "^[[:space:]]*127\.0\.0\.1[[:space:]].*${1}" /etc/hosts 2>/dev/null
}

cmd_verify() {
  require_issued
  step "Verify: an HTTPS listener on https://${VERIFY_HOST}:${VERIFY_PORT}/, trusted without -k"

  "$OPENSSL_BIN" verify -CAfile "$CA_CERT" "$LEAF_CERT" >/dev/null ||
    die "the certificate does not verify against ${CA_CERT}"
  note "chain: the certificate verifies against the authority"

  case "$VERIFY_HOST" in
    *".${DOMAIN}") ;;
    *) die "--host ${VERIFY_HOST} is not under ${DOMAIN}, so the wildcard does not cover it" ;;
  esac

  "$OPENSSL_BIN" x509 -in "$LEAF_CERT" -noout -text 2>/dev/null |
    grep -Fq "DNS:${WILDCARD}" ||
    die "the certificate has no ${WILDCARD} subject alternative name, so ${VERIFY_HOST} is not covered"
  note "names: ${WILDCARD} covers ${VERIFY_HOST}"

  hosts_entry_present "$VERIFY_HOST" ||
    die "/etc/hosts does not point ${VERIFY_HOST} at 127.0.0.1. Add: 127.0.0.1 app1.${DOMAIN} app2.${DOMAIN} app3.${DOMAIN}"
  note "hosts: ${VERIFY_HOST} resolves to 127.0.0.1"

  require_leaf_key verify

  if have lsof && lsof -nP -iTCP:"$VERIFY_PORT" -sTCP:LISTEN >/dev/null 2>&1; then
    die "port ${VERIFY_PORT} is already in use. Stop whatever holds it (the tunnel listens there too) and retry."
  fi

  have curl || die "curl is not installed, so trust cannot be checked end to end"
  [ -f "$LEAF_CHAIN" ] || cat "$LEAF_CERT" "$CA_CERT" >"$LEAF_CHAIN"

  mktemp_run
  local log="${TMPDIR_RUN}/s_server.log"

  # LibreSSL's s_server takes a bare port; OpenSSL 3 also takes host:port. Try
  # the loopback-only form first so the listener is never reachable off the
  # workstation, and fall back for the few seconds it is up.
  if ! start_listener "127.0.0.1:${VERIFY_PORT}" "$log"; then
    start_listener "$VERIFY_PORT" "$log" ||
      die "the local HTTPS listener would not start. $(tail -n 3 "$log" 2>/dev/null)"
  fi
  note "listener: up on port ${VERIFY_PORT}"

  # No -k and no --cacert: this has to pass through the workstation's own trust
  # store, which is the whole point of the check.
  if curl -fsS --max-time 10 -o /dev/null "https://${VERIFY_HOST}:${VERIFY_PORT}/"; then
    note "TLS: the workstation trusts it"
  else
    die "curl refused the connection, so this workstation does not trust the authority. Run '${PROGRAM} trust' (or 'fetch-ca' on a second workstation) and retry."
  fi

  say ""
  say "Verified: https://${VERIFY_HOST}:${VERIFY_PORT}/ is trusted on this workstation."
  say "The ALB listener presents the same certificate, so the tunnel of task 1.6b needs nothing more."
}

start_listener() {
  local accept="$1" log="$2" i
  "$OPENSSL_BIN" s_server -accept "$accept" -quiet -www \
    -cert "$LEAF_CHAIN" -key "$LEAF_KEY" >"$log" 2>&1 &
  SERVER_PID=$!

  if ! have lsof; then
    # Nothing to poll with, so wait once and settle for "still running".
    sleep 1
    if kill -0 "$SERVER_PID" 2>/dev/null; then
      return 0
    fi
  else
    for i in 1 2 3 4 5 6 7 8 9 10; do
      kill -0 "$SERVER_PID" 2>/dev/null || break
      if lsof -nP -iTCP:"$VERIFY_PORT" -sTCP:LISTEN >/dev/null 2>&1; then
        return 0
      fi
      sleep 0.5
    done
  fi

  kill "$SERVER_PID" 2>/dev/null || true
  wait "$SERVER_PID" 2>/dev/null || true
  SERVER_PID=""
  return 1
}

# --- status ------------------------------------------------------------------

cmd_status() {
  step "Status: cell ${CELL}, domain ${DOMAIN}, region ${REGION}"
  note "directory: ${CERTS_DIR}"

  if [ -f "$CA_CERT" ]; then
    show_local
    if [ -f "$CA_KEY" ]; then
      note "the authority's private key is here, so this workstation can issue and import"
    else
      note "no authority private key here, so this workstation can only trust (fetch-ca)"
    fi
  else
    note "nothing issued yet. Run '${PROGRAM} issue'."
  fi

  if [ "$(uname -s)" = "Darwin" ] && have security && [ -f "$LEAF_CERT" ]; then
    if trust_is_in_place; then
      note "trust: this workstation trusts the authority"
    else
      note "trust: NOT trusted here. Run '${PROGRAM} trust'."
    fi
  fi

  if have aws && aws sts get-caller-identity >/dev/null 2>&1; then
    local arn
    arn="$(find_acm_certificate)"
    if [ -n "$arn" ]; then
      note "ACM: ${arn}"
      aws acm describe-certificate --region "$REGION" --certificate-arn "$arn" \
        --query 'Certificate.{Type:Type,Status:Status,NotAfter:NotAfter,InUseBy:InUseBy}' \
        --output text 2>/dev/null | sed 's/^/     /'
    else
      note "ACM: nothing imported for ${WILDCARD} tagged Cell=${CELL}. Run '${PROGRAM} import'."
    fi
  else
    note "ACM: not checked, because there are no working AWS credentials here"
  fi

  if hosts_entry_present "app1.${DOMAIN}"; then
    note "hosts: app1.${DOMAIN} points at 127.0.0.1"
  else
    note "hosts: app1.${DOMAIN} is missing. Add: 127.0.0.1 app1.${DOMAIN} app2.${DOMAIN} app3.${DOMAIN}"
  fi
}

# --- dispatch ----------------------------------------------------------------

case "$COMMAND" in
  issue) cmd_issue ;;
  trust) cmd_trust ;;
  import) cmd_import ;;
  fetch-ca) cmd_fetch_ca ;;
  verify) cmd_verify ;;
  status) cmd_status ;;
  all)
    cmd_issue
    cmd_trust
    cmd_import
    say ""
    say "Next: add '127.0.0.1 app1.${DOMAIN} app2.${DOMAIN} app3.${DOMAIN}' to /etc/hosts,"
    say "then '${PROGRAM} verify' to check the listener is trusted."
    ;;
esac
