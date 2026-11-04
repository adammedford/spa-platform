#!/usr/bin/env bash
#
# platform-tfvars.sh -- write the platform root's issuers and the sample image pin into its
# terraform.tfvars, from the identity root's outputs and the sample image's current digest.
#
# #470, raised by #242. Before the platform root's first apply (docs/plan/05-infrastructure-and-cost.md,
# First deployment, step 3) its terraform.tfvars needs three things only a run can know: the new
# identity root's issuer, its Cognito domain, and the digest the sample image's tag points to now.
# On the from-nothing run of 2026-10-27 that rewrite was done by hand, ECR Public answered the digest
# lookup with a 429, and the file was left with gateway_image = "public.ecr.aws/nginx/nginx-unprivileged@".
# This script does the rewrite, and leaves the file as it was unless every value is well formed.
#
#   tools/scripts/platform-tfvars.sh
#   tools/scripts/platform-tfvars.sh --sample-digest sha256:<64 hex>   # no lookup
#
# It writes into infra/envs/poc/platform/terraform.tfvars, which you copy from
# terraform.tfvars.example first:
#   - issuers["cognito-poc"]'s authority, the identity root's `issuer` output, and its
#     browser_origin, https:// plus the identity root's `cognito_domain` output (the platform root's
#     variables.tf -> issuers). A file with no issuers block gains one, with profile "cognito".
#   - gateway_image, the sample image public.ecr.aws/nginx/nginx-unprivileged pinned by the digest
#     --sample-digest gives, or else by the current digest of its stable-alpine tag, read with
#     `docker buildx imagetools inspect`.
#   - health_check_path = "/", the sample image's (infra/modules/cell/variables.tf -> health_check_path).
# A key the file sets is changed in place, its comments and every other line kept; a key it does not
# set is added, gateway_image under the example's commented line for it. Every value is checked
# first, and the new file is written beside the old one and moved over it only then, so a failed or
# malformed lookup leaves the file byte for byte as it was. A run that would change nothing writes
# nothing.
#
# It prints which keys changed and which did not, and no value but the sample digest: the issuer
# names the user pool, and the Cognito domain carries the account id.
#
# Run it as the operator's own session after the identity root is applied, with its backend
# initialised. It needs Terraform and python3, and Docker unless --sample-digest is given. It makes
# no AWS call of its own.
#
# Exit status: 0 when the file holds the values, written or already there; 1 when a lookup failed or
# answered a malformed value, or the file holds a key in a shape this script does not edit, each
# named, and the file is unchanged; 2 when it did not start (a usage error, a malformed
# --sample-digest, a missing tool, no terraform.tfvars).
#
# Written for the bash 3.2 that ships with macOS, like tools/scripts/certs.sh.
# Owner: workstream 1, AWS and IaC.

set -euo pipefail
umask 077

PROGRAM="platform-tfvars"
# shellcheck source=tools/scripts/cell-common.sh
. "$(dirname -- "$0")/cell-common.sh"

# The sample image the platform root's first apply runs (Q5 on #98; terraform.tfvars.example).
SAMPLE_REPOSITORY="public.ecr.aws/nginx/nginx-unprivileged"
SAMPLE_TAG="stable-alpine"
# The one issuer of Assumption A4, named as contracts/platform-config.example.json names it.
ISSUER_NAME="cognito-poc"
TFVARS="${PLATFORM_ROOT}/terraform.tfvars"

# A SHA-256 digest in full: 64 lowercase hex.
# https://github.com/opencontainers/image-spec/blob/v1.1.1/descriptor.md#sha-256
DIGEST_FORM='^sha256:[0-9a-f]{64}$'
# The identity root's issuer: an https URL whose characters need no quoting in HCL. The cell module
# holds an authority to https and no whitespace (variables.tf -> issuers).
ISSUER_FORM='^https://[A-Za-z0-9.-]+(:[0-9]+)?(/[A-Za-z0-9._~/-]*)?$'
# The Cognito domain: a bare host name, which browser_origin's https:// is put in front of. The
# module holds a browser origin to https plus an authority only.
DOMAIN_FORM='^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$'

SAMPLE_DIGEST=""
DIGEST_GIVEN=""

usage() {
  # The header above, up to the Owner line, is this script's manual.
  sed -n '3,/^# Owner:/p' "$0" | sed 's/^# \{0,1\}//'
  cat <<EOF

Options
  --sample-digest sha256:<64 hex>   Pin the sample image by this digest, with no lookup.
  -h, --help                        This text.
EOF
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --sample-digest)
      [ "$#" -ge 2 ] || refuse "--sample-digest needs a value. Try --help."
      SAMPLE_DIGEST="$2"
      DIGEST_GIVEN="yes"
      shift
      ;;
    -h | --help)
      usage
      exit 0
      ;;
    *) refuse "unknown argument '$1'. Try --help." ;;
  esac
  shift
done

if [ -n "$DIGEST_GIVEN" ] && ! [[ "$SAMPLE_DIGEST" =~ $DIGEST_FORM ]]; then
  refuse "--sample-digest takes a full digest, sha256: and 64 lowercase hex digits."
fi

need_terraform
need_tool python3 "xcode-select --install"
[ -n "$DIGEST_GIVEN" ] || need_tool docker "Install Docker Desktop, or pass --sample-digest."
[ -f "$TFVARS" ] ||
  refuse "there is no $(root_name "$TFVARS"). Copy $(root_name "$PLATFORM_ROOT")/terraform.tfvars.example to it first."

WORK="$(mktemp -d "${TMPDIR:-/tmp}/${PROGRAM}.XXXXXX")"
ERR="${WORK}/err"
trap cell_cleanup EXIT

# --- the identity root's outputs -------------------------------------------------------------------

# From its state, as seed.sh's write_outputs reads the roots: this runs before the platform root's
# first apply, so there is no outputs.json to read them from yet (contracts/infra-outputs.schema.json
# is written from all three roots).
identity_output() {
  local value
  value="$(tf_output "$IDP_ROOT" "$1")"
  [ -n "$value" ] ||
    fail "the identity root's $1 output could not be read. Apply $(root_name "$IDP_ROOT") first, with its backend initialised; $(root_name "$TFVARS") is unchanged."
  [[ "$value" =~ $2 ]] ||
    fail "the identity root's $1 output is not of the form this file takes: $3. $(root_name "$TFVARS") is unchanged."
  printf '%s' "$value"
}

ISSUER="$(identity_output issuer "$ISSUER_FORM" "an https URL")"
DOMAIN="$(identity_output cognito_domain "$DOMAIN_FORM" "a host name")"

# --- the sample image's digest ---------------------------------------------------------------------

if [ -z "$DIGEST_GIVEN" ]; then
  LOOKUP="the digest lookup of ${SAMPLE_REPOSITORY}:${SAMPLE_TAG}"
  # --format '{{json .Manifest}}' prints the reference's own descriptor as JSON: for a multi-platform
  # image, the index's, which is what ECS resolves the task's platform from.
  # https://docs.docker.com/reference/cli/docker/buildx/imagetools/inspect/
  status=0
  docker buildx imagetools inspect --format '{{json .Manifest}}' "${SAMPLE_REPOSITORY}:${SAMPLE_TAG}" \
    > "${WORK}/manifest" 2> "$ERR" || status=$?
  if [ "$status" -ne 0 ]; then
    # The registry's own line names the status, a 429 among them (#242). A public registry's URL
    # carries no account id.
    fail "${LOOKUP} failed (exit ${status}): $(sed -n '$p' "$ERR"). Try again later, or pass --sample-digest. $(root_name "$TFVARS") is unchanged."
  fi
  SAMPLE_DIGEST="$(python3 -I -c 'import json, sys
try:
    print(json.load(open(sys.argv[1]))["digest"])
except Exception:
    pass' "${WORK}/manifest")"
  [[ "$SAMPLE_DIGEST" =~ $DIGEST_FORM ]] ||
    fail "${LOOKUP} answered no full digest. Try again later, or pass --sample-digest. $(root_name "$TFVARS") is unchanged."
fi

# --- the rewrite -----------------------------------------------------------------------------------

python3 -I - "$TFVARS" "$ISSUER_NAME" "$ISSUER" "https://${DOMAIN}" "${SAMPLE_REPOSITORY}@${SAMPLE_DIGEST}" "/" <<'PY' ||
"""Rewrites the named keys of a terraform.tfvars in place, line by line, and moves the result over
the file only when every key is in a shape it can edit. It changes the value between a key's quotes
and nothing else on the line, so a trailing comment stays. Its messages name keys, never values."""
import os
import re
import stat
import sys
import tempfile

path, issuer_name, authority, origin, image, health = sys.argv[1:7]


class Refused(Exception):
    pass


with open(path, "rb") as handle:
    original = handle.read()
lines = original.decode("utf-8").splitlines(keepends=True)


def depth_changes(line: str) -> int:
    """The braces a line opens less those it closes, outside strings and comments."""
    change, quoted, index = 0, False, 0
    while index < len(line):
        char = line[index]
        if quoted:
            if char == "\\":
                index += 1
            elif char == '"':
                quoted = False
        elif char == '"':
            quoted = True
        elif char == "#" or line.startswith("//", index):
            break
        elif char == "{":
            change += 1
        elif char == "}":
            change -= 1
        index += 1
    return change


def top_level() -> list[int]:
    """The indexes of the lines at depth 0, where a top-level key starts."""
    found, depth = [], 0
    for index, line in enumerate(lines):
        if depth == 0:
            found.append(index)
        depth += depth_changes(line)
    return found


def string_line(index: int, key: str, label: str) -> tuple[re.Match, str]:
    match = re.match(r'^(\s*' + re.escape(key) + r'\s*=\s*")([^"\\$%]*)(".*)$', lines[index], re.DOTALL)
    if match is None:
        raise Refused(f"{label} is not a plain quoted string")
    return match, match[2]


changed, unchanged = [], []


def record(label: str, before: str, after: str):
    (changed if before != after else unchanged).append(label)


def set_line(index: int, match: re.Match, value: str):
    lines[index] = match[1] + value + match[3]


def set_scalar(key: str, value: str, after_comment: bool):
    starts = [i for i in top_level() if re.match(r'^' + re.escape(key) + r'\s*=', lines[i])]
    if len(starts) > 1:
        raise Refused(f"{key} is set {len(starts)} times")
    if starts:
        match, before = string_line(starts[0], key, key)
        set_line(starts[0], match, value)
        record(key, before, value)
        return
    new = f'{key} = "{value}"\n'
    commented = [i for i, line in enumerate(lines) if re.match(r'^#\s*' + re.escape(key) + r'\s*=', line)]
    if after_comment and commented:
        lines.insert(commented[0] + 1, new)
    else:
        if lines and not lines[-1].endswith("\n"):
            lines[-1] += "\n"
        lines.append(new)
    changed.append(key)


def block_end(start: int) -> int:
    """The index of the line that closes the block opening on line start."""
    depth = 0
    for index in range(start, len(lines)):
        depth += depth_changes(lines[index])
        if depth <= 0:
            return index
    raise Refused("a block is not closed")


def set_issuer():
    starts = [i for i in top_level() if re.match(r'^issuers\s*=', lines[i])]
    if len(starts) > 1:
        raise Refused(f"issuers is set {len(starts)} times")
    if not starts:
        if lines and not lines[-1].endswith("\n"):
            lines[-1] += "\n"
        lines.extend(["\n", "issuers = {\n", f'  "{issuer_name}" = {{\n', f'    authority      = "{authority}"\n',
                      '    profile        = "cognito"\n', f'    browser_origin = "{origin}"\n', "  }\n", "}\n"])
        changed.append("issuers")
        return
    if not re.match(r'^issuers\s*=\s*\{', lines[starts[0]]):
        raise Refused("issuers is not a block this script edits")
    end = block_end(starts[0])
    # The entry for the one issuer, at the block's first depth.
    entries, depth = [], 0
    for index in range(starts[0], end + 1):
        if depth == 1 and re.match(r'^\s*"?' + re.escape(issuer_name) + r'"?\s*=\s*\{', lines[index]):
            entries.append(index)
        depth += depth_changes(lines[index])
    if len(entries) != 1:
        raise Refused(f'issuers holds {len(entries)} "{issuer_name}" entries, not one')
    entry_end = block_end(entries[0])
    for key, value in (("authority", authority), ("browser_origin", origin)):
        label = f'issuers["{issuer_name}"].{key}'
        found = [i for i in range(entries[0] + 1, entry_end) if re.match(r'^\s*' + key + r'\s*=', lines[i])]
        if len(found) != 1:
            raise Refused(f"{label} is set {len(found)} times, not once")
        match, before = string_line(found[0], key, label)
        set_line(found[0], match, value)
        record(label, before, value)


try:
    set_scalar("gateway_image", image, after_comment=True)
    set_scalar("health_check_path", health, after_comment=False)
    set_issuer()
except Refused as refused:
    print(f"platform-tfvars: {refused}, so the file is unchanged. Edit that key by hand, or remove it "
          "for this script to write.", file=sys.stderr)
    sys.exit(1)

updated = "".join(lines).encode("utf-8")
if changed and updated != original:
    directory = os.path.dirname(path)
    # A name Terraform never loads as variables, in the same directory, so the move is a rename.
    # https://developer.hashicorp.com/terraform/language/values/variables#variable-definition-files
    descriptor, staged = tempfile.mkstemp(prefix=".terraform.tfvars.", suffix=".staged", dir=directory)
    try:
        with os.fdopen(descriptor, "wb") as handle:
            handle.write(updated)
            handle.flush()
            os.fsync(handle.fileno())
        os.chmod(staged, stat.S_IMODE(os.stat(path).st_mode))
        # https://docs.python.org/3/library/os.html#os.replace
        os.replace(staged, path)
    except BaseException:
        if os.path.exists(staged):
            os.unlink(staged)
        raise
if changed:
    print("platform-tfvars: changed: " + ", ".join(changed))
if unchanged:
    print("platform-tfvars: unchanged: " + ", ".join(unchanged))
PY
  fail "$(root_name "$TFVARS") was not rewritten; it is unchanged."

note "the sample image is pinned at ${SAMPLE_DIGEST}"
note "pass: $(root_name "$TFVARS") holds the identity root's issuer and the sample image pin. The platform root's first apply is next."
