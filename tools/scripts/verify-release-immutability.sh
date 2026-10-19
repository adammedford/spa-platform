#!/usr/bin/env bash
#
# verify-release-immutability.sh -- test P-04 against the applied platform bucket: S3 refuses a
# second write to an existing release key, and refuses the delete that would allow one.
#
# Task 1.5's verification script (#122). It writes one probe key under an appId no registry entry
# names and asserts, one line per case, that the bucket policy contracts/s3-layout.md -> Rules
# describes holds on real S3; then it reads back the row's other properties. #63 runs it as P-04
# at G3.
#
#   tools/scripts/verify-release-immutability.sh                              # the cases
#   tools/scripts/verify-release-immutability.sh --clean-up-as-teardown-role  # and then remove the probe
#
# Run it as the operator's own session, the principal spactl writes as: the spa-platform profile,
# with tools/aws-project.env sourced. A Deny in a bucket policy applies to administrators too, so
# the operator's refusal is the strongest test of an ordinary writer. It refuses to start as the
# teardown role, which would observe the policy's exception rather than its rule (ADR-0014).
#
# Its output is evidence for a merge body in a public repository, so it never prints the account
# id, the caller's session name or anything S3's error messages say beyond the error code.
#
# Exit status: 0 when every case is as expected, 1 when any case is not, 2 when it did not start.
#
# Written for the bash 3.2 that ships with macOS, like tools/scripts/certs.sh.
# Owner: workstream 1, AWS and IaC.

set -euo pipefail
umask 077

PROGRAM="verify-release-immutability"
CELL="${SPA_CELL:-poc}"
CLEAN_UP=0

# The AWS CLI pages a terminal's output through less unless told not to.
# https://docs.aws.amazon.com/cli/latest/userguide/cli-usage-pagination.html#cli-usage-pagination-clientside
export AWS_PAGER=""

# No checksum the CLI would add on its own. Since 2.23 it sends a CRC64NVME checksum with
# upload-part and put-object by default (seen in 2.37.12's --debug: create-multipart-upload names no
# algorithm, upload-part sends x-amz-checksum-crc64nvme), and the completion below lists its part
# without one. when_required sends what each call requires and no more, so the bucket policy is the
# only thing that decides the multipart case (#98's Q26).
# https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-envvars.html
export AWS_REQUEST_CHECKSUM_CALCULATION="when_required"

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd -P)"
PLATFORM_ROOT="${REPO_ROOT}/infra/envs/poc/platform"

# --- plumbing ------------------------------------------------------------------------------------

note() { printf '%s: %s\n' "$PROGRAM" "$*"; }

# Did not start: a usage error, a missing prerequisite, or the teardown role.
refuse() {
  printf '%s: %s\n' "$PROGRAM" "$*" >&2
  exit 2
}

usage() {
  # The header above, up to the Owner line, is this script's manual.
  sed -n '3,/^# Owner:/p' "$0" | sed 's/^# \{0,1\}//'
  cat <<EOF

Options
  --clean-up-as-teardown-role  After every case is recorded, assume the teardown role in a
                               separate session, show that it cannot write, and delete the probe's
                               versions. Off by default, so P-04 alone never acts as that role.
  --cell NAME                  The cell whose teardown role the clean-up assumes, as
                               spa-platform-<cell>-teardown. Default: \$SPA_CELL, else poc.
  -h, --help                   This text.
EOF
}

WORK=""
REGION=""
BUCKET=""
KEY=""
UPLOAD_ID=""

# An upload left open is storage nobody sees, and the bucket may carry no lifecycle rule to expire
# it (ADR-0014), so whatever stops the run aborts the probe's upload first.
cleanup() {
  status=$?
  if [ -n "$UPLOAD_ID" ]; then
    aws --region "$REGION" --output text s3api abort-multipart-upload \
      --bucket "$BUCKET" --key "$KEY" --upload-id "$UPLOAD_ID" > /dev/null 2>&1 || true
  fi
  if [ -n "$WORK" ] && [ -d "$WORK" ]; then
    rm -rf "$WORK"
  fi
  return "$status"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

# --- arguments and prerequisites -------------------------------------------------------------------

while [ "$#" -gt 0 ]; do
  case "$1" in
    --clean-up-as-teardown-role) CLEAN_UP=1 ;;
    --cell)
      [ "$#" -ge 2 ] || refuse "--cell needs a value. Try --help."
      CELL="$2"
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

# The cell's lexical form, as tools/scripts/certs.sh checks it.
case "$CELL" in
  *[!a-z0-9-]* | "" | -* | *-) refuse "--cell must be lowercase letters, digits and inner hyphens: got '${CELL}'" ;;
esac

command -v aws > /dev/null 2>&1 || refuse "the AWS CLI is not on PATH. brew install awscli"
command -v terraform > /dev/null 2>&1 || refuse "terraform is not on PATH. brew install hashicorp/tap/terraform"

# The Region the AWS CLI would build in, named on every call so nothing else can choose it.
REGION="${AWS_REGION:-${AWS_DEFAULT_REGION:-}}"
[ -n "$REGION" ] || refuse "no Region in AWS_REGION or AWS_DEFAULT_REGION. set -a; . tools/aws-project.env; set +a"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/${PROGRAM}.XXXXXX")"
OUT="${WORK}/out"
ERR="${WORK}/err"

# --- one AWS call ----------------------------------------------------------------------------------
#
# Every call names the Region and --output text, so a profile's own output setting cannot change
# what is read back. Its standard output lands in $OUT, its standard error in $ERR, and OUTCOME says
# how AWS answered: "success", or the error code the CLI prints in "An error occurred (<Code>) when
# calling the <Operation> operation", which botocore's ClientError writes and the CLI exits 254 on
# (`aws help return-codes`). S3's message itself is never printed: for a same-account request it
# names the caller's ARN, which carries the account id and the session name.
# https://docs.aws.amazon.com/AmazonS3/latest/userguide/troubleshoot-403-errors.html
#
# SESSION says whose credentials a call carries: the operator's, from the environment the script was
# started in, or the teardown role's, which the clean-up exports to that one call's subshell. Exported
# rather than written as `env NAME=value aws`, which would put them in a process's arguments; and the
# CLI reads credentials from the environment before any profile.
# https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-envvars.html
SESSION="operator"
TEARDOWN_KEY_ID=""
TEARDOWN_SECRET=""
TEARDOWN_TOKEN=""

run_aws() {
  if [ "$SESSION" = "teardown" ]; then
    (
      unset AWS_PROFILE AWS_DEFAULT_PROFILE
      AWS_ACCESS_KEY_ID="$TEARDOWN_KEY_ID"
      AWS_SECRET_ACCESS_KEY="$TEARDOWN_SECRET"
      AWS_SESSION_TOKEN="$TEARDOWN_TOKEN"
      export AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY AWS_SESSION_TOKEN
      exec aws --region "$REGION" --output text "$@"
    )
  else
    aws --region "$REGION" --output text "$@"
  fi
}

failed_with() {
  OUTCOME="$(awk 'match($0, /An error occurred \([^)]*\)/) { print substr($0, RSTART + 19, RLENGTH - 20); exit }' "$ERR")"
  [ -n "$OUTCOME" ] || OUTCOME="no answer from AWS: the AWS CLI exited $1"
}

aws_call() {
  if run_aws "$@" > "$OUT" 2> "$ERR"; then
    OUTCOME="success"
  else
    failed_with "$?"
  fi
}

# The first line the last call printed, its tab-separated fields joined by single spaces: how
# --output text writes a query's list (awscli/text.py).
printed() { awk 'NR == 1 { gsub(/\t/, " "); print }' "$OUT"; }

# --- the record ------------------------------------------------------------------------------------

CASES=0
FAILED=0

# How an outcome reads in a case line: S3's error codes beside the HTTP status S3's error code list
# gives them, https://docs.aws.amazon.com/AmazonS3/latest/API/ErrorResponses.html#ErrorCodeList
shown() {
  case "$1" in
    success) printf 'succeeded' ;;
    AccessDenied) printf '403 AccessDenied' ;;
    PreconditionFailed) printf '412 PreconditionFailed' ;;
    NoSuchKey | NoSuchUpload | NoSuchBucket | NoSuchLifecycleConfiguration | ReplicationConfigurationNotFoundError)
      printf '404 %s' "$1"
      ;;
    ConditionalRequestConflict) printf '409 %s' "$1" ;;
    InvalidRequest | InvalidPart) printf '400 %s' "$1" ;;
    # IAM's, from its API reference: https://docs.aws.amazon.com/IAM/latest/APIReference/API_GetRole.html
    NoSuchEntity) printf '404 %s' "$1" ;;
    *) printf '%s' "$1" ;;
  esac
}

# The kind of policy S3 says refused a same-account request, and nothing else from its message,
# which also names the caller's ARN. "with an explicit deny in a <type> policy" and "because no
# <type> policy allows" are S3's documented forms; a cross-account request carries neither, so this
# is shown beside a refusal and never asserted.
# https://docs.aws.amazon.com/AmazonS3/latest/userguide/troubleshoot-403-errors.html
denial() {
  awk '
    match($0, /with an explicit deny in an? [A-Za-z -]+ policy/) { print substr($0, RSTART + 8, RLENGTH - 8); exit }
    match($0, /because no [A-Za-z -]+ policy allows/) { print substr($0, RSTART + 8, RLENGTH - 8) " it"; exit }
  ' "$ERR"
}

# One case, one line: what it expects, and what came back. A case that is not as expected says
# FAIL and counts against the exit status, and every later case still runs.
record() {
  description="$1"
  expected="$2"
  observed="$3"
  detail=""
  if [ "$observed" = "AccessDenied" ]; then
    detail="$(denial)"
  fi
  CASES=$((CASES + 1))
  if [ "$observed" = "$expected" ]; then
    printf 'pass  %s: %s%s\n' "$description" "$(shown "$observed")" "${detail:+ (${detail})}"
  else
    FAILED=$((FAILED + 1))
    printf 'FAIL  %s: expected %s, got %s%s\n' "$description" "$(shown "$expected")" "$(shown "$observed")" \
      "${detail:+ (${detail})}"
  fi
}

# A case that could not run, because what it is read against does not exist. It counts as not as
# expected: a refusal nobody observed proves nothing.
not_run() {
  CASES=$((CASES + 1))
  FAILED=$((FAILED + 1))
  printf 'FAIL  %s: not run, because %s\n' "$1" "$2"
}

# A read-back: the call's answer when it succeeds, else its error code.
read_back() {
  description="$1"
  expected="$2"
  shift 2
  aws_call "$@"
  if [ "$OUTCOME" = "success" ]; then
    record "$description" "$expected" "$(printed)"
  else
    record "$description" "$expected" "$OUTCOME"
  fi
}

counted() {
  if [ "$1" = 1 ]; then printf '1 %s' "$2"; else printf '%s %ss' "$1" "$2"; fi
}

# --- who is calling --------------------------------------------------------------------------------
#
# First, before the platform root's state or the bucket is read. The teardown role is named
# spa-platform-<cell>-teardown (ADR-0014 item 2), and a session of any cell's teardown role is
# refused, so a --cell naming the wrong cell cannot let one through. get-caller-identity prints the
# assumed-role session ARN, arn:<partition>:sts::<account>:assumed-role/<role>/<session>.
aws_call sts get-caller-identity --query '[Account, Arn]'
[ "$OUTCOME" = "success" ] || refuse "no AWS credentials answered get-caller-identity (${OUTCOME}). Sign in with the spa-platform profile."
ACCOUNT=""
CALLER_ARN=""
read -r ACCOUNT CALLER_ARN < "$OUT" || true

caller_role=""
case "$CALLER_ARN" in
  arn:*:sts::*:assumed-role/*/*)
    caller_role="${CALLER_ARN#*:assumed-role/}"
    caller_role="${caller_role%%/*}"
    ;;
esac
case "$caller_role" in
  spa-platform-*-teardown)
    refuse "the caller is a session of the teardown role ${caller_role}. P-04 runs as the operator, never as that role (ADR-0014); --clean-up-as-teardown-role assumes it itself, after the cases."
    ;;
esac

case "$ACCOUNT" in
  [0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]) ;;
  *) refuse "get-caller-identity named no 12-digit account id." ;;
esac

# The account id never reaches the output: change request 1.5 keeps account identifiers out of the
# tracked tree, and this output is pasted into a merge body.
redact() { printf '%s' "$1" | sed "s/${ACCOUNT}/<account>/g"; }

case "$CALLER_ARN" in
  *:assumed-role/*) caller="a session of the role ${caller_role}" ;;
  *:user/*) caller="an IAM user" ;;
  *:root) caller="the account root" ;;
  *) caller="a principal that is not a role session" ;;
esac

# --- the platform root's outputs ---------------------------------------------------------------------
#
# Every consumer reads the bucket from the platform root's output and never derives the name
# (contracts/s3-layout.md; change request 1.5a, decision 2), and the repositories the same way.
# The storage-only targeted apply #92's sitting runs (Q18 on #98) writes the outputs that depend on
# the resources it targeted, which are these three, and not `cell` or `region`, which read only a
# variable: so the cell comes from --cell, and the Region from the environment. Checked on
# 2026-10-19 with Terraform 1.11.0, the floor, and 1.16.5: after `apply -target`, `terraform output
# -raw` reports an output that reads only a variable as not found.
output() {
  terraform -chdir="$PLATFORM_ROOT" output -no-color -raw "$1" 2> "$ERR"
}

BUCKET="$(output bucket)" || BUCKET=""
[ -n "$BUCKET" ] || refuse "could not read the platform root's bucket output. Run terraform init -backend-config=backend.hcl in infra/envs/poc/platform, and apply the bucket first."
ECR_GATEWAY="$(output ecr_gateway)" || ECR_GATEWAY=""
ECR_MOCK_API="$(output ecr_mock_api)" || ECR_MOCK_API=""

# --- the probe -------------------------------------------------------------------------------------
#
# One key under an appId no registry entry names, so the gateway never serves it, and a release
# named for this run's UTC second, so a rerun is independent of a probe an earlier run left behind
# (#92's Q11, Q28 on #98). Both segments match registry.schema.json's appId and release patterns.

RELEASE="$(date -u +%Y%m%dT%H%M%SZ)"
PROBE_PREFIX="apps/p04-probe/releases/${RELEASE}/"
KEY="${PROBE_PREFIX}index.html"
BODY="${WORK}/index.html"
printf '<!doctype html>\n<title>P-04 probe</title>\n' > "$BODY"

note "bucket $(redact "$BUCKET") in ${REGION}, from the platform root's output"
note "running as ${caller}"
note "probe key ${KEY}"

# --- P-04: the second write, in each form a writer can send (#98's Q25) ---------------------------
#
# The policy refuses a write under apps/ that carries no If-None-Match, and S3 refuses one that
# carries it over an existing key with 412. Both halves, and the If-Match form a writer could use to
# overwrite, are what "a second write to a release key is refused" means.
# https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-requests.html

ETAG=""
VERSION_ID=""
aws_call s3api put-object --bucket "$BUCKET" --key "$KEY" --body "$BODY" --content-type text/html \
  --if-none-match '*' --query '[ETag, VersionId]'
record "a first PUT with If-None-Match: * succeeds" success "$OUTCOME"
if [ "$OUTCOME" = "success" ]; then
  read -r ETAG VERSION_ID < "$OUT" || true
fi
NO_PROBE="the first PUT did not succeed, so there is no release key to refuse"

# A case read against the probe key: the call's outcome against the one expected.
probe_case() {
  description="$1"
  expected="$2"
  shift 2
  if [ -z "$ETAG" ]; then
    not_run "$description" "$NO_PROBE"
    return 0
  fi
  aws_call "$@"
  record "$description" "$expected" "$OUTCOME"
}

probe_case "a second PUT with If-None-Match: * gets 412" PreconditionFailed \
  s3api put-object --bucket "$BUCKET" --key "$KEY" --body "$BODY" --content-type text/html --if-none-match '*'
probe_case "a PUT without If-None-Match gets 403" AccessDenied \
  s3api put-object --bucket "$BUCKET" --key "$KEY" --body "$BODY" --content-type text/html
probe_case "a PUT with only If-Match gets 403" AccessDenied \
  s3api put-object --bucket "$BUCKET" --key "$KEY" --body "$BODY" --content-type text/html --if-match "$ETAG"

# --- P-04: the delete that would allow one, in both forms, then the write ----------------------------
#
# Without the delete rule a delete followed by a write would replace a release (ADR-0009), and S3
# accepts a conditional write over a delete marker, so the count after the deletes is what shows
# that no marker slipped through.

probe_case "a DELETE with no version id gets 403" AccessDenied \
  s3api delete-object --bucket "$BUCKET" --key "$KEY"
if [ -n "$ETAG" ] && { [ -z "$VERSION_ID" ] || [ "$VERSION_ID" = "None" ]; }; then
  not_run "a DELETE of the version gets 403" "the first PUT returned no version id, so the bucket is not versioning"
else
  probe_case "a DELETE of the version gets 403" AccessDenied \
    s3api delete-object --bucket "$BUCKET" --key "$KEY" --version-id "$VERSION_ID"
fi

if [ -z "$ETAG" ]; then
  not_run "the key still has exactly one version and no delete marker" "$NO_PROBE"
else
  aws_call s3api list-object-versions --bucket "$BUCKET" --prefix "$KEY" \
    --query "[length(Versions[?Key=='${KEY}'] || \`[]\`), length(DeleteMarkers[?Key=='${KEY}'] || \`[]\`)]"
  if [ "$OUTCOME" = "success" ]; then
    versions=""
    markers=""
    read -r versions markers < "$OUT" || true
    observed="$(counted "$versions" version), $(counted "$markers" "delete marker")"
  else
    observed="$OUTCOME"
  fi
  record "the key still has exactly one version and no delete marker" "1 version, 0 delete markers" "$observed"
fi

probe_case "a PUT with If-None-Match: * after the delete attempts still gets 412" PreconditionFailed \
  s3api put-object --bucket "$BUCKET" --key "$KEY" --body "$BODY" --content-type text/html --if-none-match '*'

# --- P-04: the two other ways to write a key (#98's Q26) ---------------------------------------------
#
# A bucket policy that enforces conditional writes refuses a CopyObject that carries no conditional
# header, and exempts the calls that start and fill a multipart upload but not the one that
# completes it, through s3:ObjectCreationOperation. spactl uses neither route, so only this shows
# what the exemption does on real S3.
# https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes-enforce.html
#
# REPLACE, because a copy of a key onto itself that changes nothing is refused for that reason
# instead, with 400, which would hide the answer this case is for.
probe_case "a CopyObject onto the key gets 403" AccessDenied \
  s3api copy-object --bucket "$BUCKET" --key "$KEY" --copy-source "${BUCKET}/${KEY}" \
  --metadata-directive REPLACE --content-type text/html

MULTIPART="a multipart upload completed without If-None-Match gets 403"
ABORTED="the multipart upload is aborted"
if [ -z "$ETAG" ]; then
  not_run "$MULTIPART" "$NO_PROBE"
  not_run "$ABORTED" "$NO_PROBE"
else
  part=""
  aws_call s3api create-multipart-upload --bucket "$BUCKET" --key "$KEY" --content-type text/html --query UploadId
  if [ "$OUTCOME" = "success" ]; then
    UPLOAD_ID="$(printed)"
    aws_call s3api upload-part --bucket "$BUCKET" --key "$KEY" --upload-id "$UPLOAD_ID" --part-number 1 \
      --body "$BODY" --query ETag
    if [ "$OUTCOME" = "success" ]; then
      part="$(printed | tr -d '"')"
    fi
  fi
  if [ -z "$part" ]; then
    not_run "$MULTIPART" "starting the upload or sending its one part failed: $(shown "$OUTCOME")"
  else
    # The part's ETag without its quotes, as the AWS CLI reference's own example lists one.
    aws_call s3api complete-multipart-upload --bucket "$BUCKET" --key "$KEY" --upload-id "$UPLOAD_ID" \
      --multipart-upload "{\"Parts\":[{\"ETag\":\"${part}\",\"PartNumber\":1}]}"
    record "$MULTIPART" AccessDenied "$OUTCOME"
  fi
  if [ -z "$UPLOAD_ID" ]; then
    not_run "$ABORTED" "no upload was started"
  else
    aws_call s3api abort-multipart-upload --bucket "$BUCKET" --key "$KEY" --upload-id "$UPLOAD_ID"
    record "$ABORTED" success "$OUTCOME"
    # Gone either way, aborted or completed; anything else is left to the exit trap's second try.
    if [ "$OUTCOME" = "success" ] || [ "$OUTCOME" = "NoSuchUpload" ]; then
      UPLOAD_ID=""
    fi
  fi
fi

# --- the row's other properties (#98's Q27) ----------------------------------------------------------
#
# "Private, versioned, TLS only" and "immutable tags", each one read. No lifecycle rule may exist:
# S3's own expiration carries no aws:PrincipalArn, so one would delete releases while P-04 still
# passed (ADR-0014). The plain-HTTP request is the operator's own, signed, so only the policy's
# DenyInsecureTransport can refuse it; an anonymous request would be refused on any private bucket
# and prove nothing.

read_back "versioning is Enabled" Enabled \
  s3api get-bucket-versioning --bucket "$BUCKET" --query Status
read_back "all four public access block settings are on" "True True True True" \
  s3api get-public-access-block --bucket "$BUCKET" \
  --query 'PublicAccessBlockConfiguration.[BlockPublicAcls, IgnorePublicAcls, BlockPublicPolicy, RestrictPublicBuckets]'
aws_call s3api get-bucket-policy-status --bucket "$BUCKET" --query PolicyStatus.IsPublic
if [ "$OUTCOME" = "success" ]; then OUTCOME="IsPublic $(printed)"; fi
record "the bucket's policy status is not public" "IsPublic False" "$OUTCOME"

aws_call s3api get-bucket-lifecycle-configuration --bucket "$BUCKET"
if [ "$OUTCOME" = "success" ]; then OUTCOME="a lifecycle configuration"; fi
record "the bucket has no lifecycle configuration" NoSuchLifecycleConfiguration "$OUTCOME"

aws_call s3api get-bucket-replication --bucket "$BUCKET"
if [ "$OUTCOME" = "success" ]; then OUTCOME="a replication configuration"; fi
record "the bucket has no replication configuration" ReplicationConfigurationNotFoundError "$OUTCOME"

aws_call --endpoint-url "http://s3.${REGION}.amazonaws.com" s3api get-bucket-versioning --bucket "$BUCKET"
record "a plain-HTTP request is refused" AccessDenied "$OUTCOME"

# The repository's name is its URL's path: <registry>/<name>.
# https://docs.aws.amazon.com/AmazonECR/latest/userguide/Repositories.html
for pair in "ecr_gateway ${ECR_GATEWAY}" "ecr_mock_api ${ECR_MOCK_API}"; do
  name="${pair#* }"
  if [ -z "$name" ]; then
    not_run "the image repository the platform root's ${pair% *} output names has immutable tags" \
      "the platform root has no ${pair% *} output"
    continue
  fi
  name="${name#*/}"
  read_back "the image repository ${name} has immutable tags" IMMUTABLE \
    ecr describe-repositories --repository-names "$name" --query 'repositories[0].imageTagMutability'
done

# --- the clean-up, as the teardown role, behind the flag (#98's Q29) -----------------------------------
#
# Every case above is recorded first, so P-04's verdict is decided before anything acts as the
# teardown role, and without the flag nothing does: P-04 stays negative-only (ADR-0014; #98's Q32).
# With it, the run proves for cents what ADR-0014 says would otherwise surface only at teardown: that
# the operator can assume the role, and that the policy's exception names the role's ARN. The role
# holds the delete and no write, which is what stops it replacing a release, so its write is a case.
# It deletes this run's release prefix and nothing else; a probe an earlier run left is teardown.sh's.
#
# The name is derived from the cell and the ARN is the one IAM returns for it (ADR-0014 item 2).

if [ "$CLEAN_UP" -eq 1 ]; then
  note "P-04 and the read-backs are recorded: ${CASES} cases, ${FAILED} not as expected. Removing the probe as the teardown role."
  ROLE="spa-platform-${CELL}-teardown"
  ASSUMED="the teardown role ${ROLE} is assumed in a separate session"
  CANNOT_WRITE="the teardown role cannot write: a PUT with If-None-Match: * to a new key gets 403"
  DELETES="the teardown role deletes the probe's versions"
  NOTHING_LEFT="nothing is left under the probe's release prefix"

  role_arn=""
  aws_call iam get-role --role-name "$ROLE" --query Role.Arn
  if [ "$OUTCOME" = "success" ]; then
    role_arn="$(printed)"
    # Captured into variables and never into a file or the output. The three fields are an access
    # key id and two base64 strings, so they split on the CLI's tabs and hold nothing to glob.
    if credentials="$(run_aws sts assume-role --role-arn "$role_arn" --role-session-name "p04-cleanup-${RELEASE}" \
      --query 'Credentials.[AccessKeyId, SecretAccessKey, SessionToken]' 2> "$ERR")"; then
      OUTCOME="success"
      set -f
      set -- $credentials
      set +f
      TEARDOWN_KEY_ID="${1:-}"
      TEARDOWN_SECRET="${2:-}"
      TEARDOWN_TOKEN="${3:-}"
      credentials=""
      [ -n "$TEARDOWN_TOKEN" ] || OUTCOME="assume-role answered without credentials"
    else
      failed_with "$?"
    fi
  fi
  record "$ASSUMED" success "$OUTCOME"

  if [ -z "$TEARDOWN_TOKEN" ]; then
    not_run "$CANNOT_WRITE" "the teardown role was not assumed"
    not_run "$DELETES" "the teardown role was not assumed"
    not_run "$NOTHING_LEFT" "the teardown role was not assumed"
  else
    SESSION="teardown"
    aws_call s3api put-object --bucket "$BUCKET" --key "${PROBE_PREFIX}teardown-write.html" --body "$BODY" \
      --content-type text/html --if-none-match '*'
    record "$CANNOT_WRITE" AccessDenied "$OUTCOME"

    # Every version and delete marker under this run's prefix, the role's own write included if it
    # was wrongly allowed one. Read from a copy, because each delete below rewrites $OUT.
    aws_call s3api list-object-versions --bucket "$BUCKET" --prefix "$PROBE_PREFIX" \
      --query '[Versions, DeleteMarkers][].[Key, VersionId]'
    if [ "$OUTCOME" != "success" ]; then
      record "$DELETES" success "$OUTCOME"
    else
      cat "$OUT" > "${WORK}/versions"
      tab="$(printf '\t')"
      while IFS="$tab" read -r key version; do
        [ -n "$key" ] || continue
        case "$key" in
          "$PROBE_PREFIX"*) ;;
          *)
            not_run "the teardown role deletes ${key}" "it is outside the probe's release prefix"
            continue
            ;;
        esac
        aws_call s3api delete-object --bucket "$BUCKET" --key "$key" --version-id "$version"
        record "the teardown role deletes version ${version} of ${key}" success "$OUTCOME"
      done < "${WORK}/versions"
    fi

    aws_call s3api list-object-versions --bucket "$BUCKET" --prefix "$PROBE_PREFIX" \
      --query '[length(Versions || `[]`), length(DeleteMarkers || `[]`)]'
    if [ "$OUTCOME" = "success" ]; then
      versions=""
      markers=""
      read -r versions markers < "$OUT" || true
      OUTCOME="$(counted "$versions" version), $(counted "$markers" "delete marker")"
    fi
    record "$NOTHING_LEFT" "0 versions, 0 delete markers" "$OUTCOME"
    SESSION="operator"
  fi
  TEARDOWN_KEY_ID=""
  TEARDOWN_SECRET=""
  TEARDOWN_TOKEN=""
fi

# --- the verdict -----------------------------------------------------------------------------------

if [ "$FAILED" -eq 0 ]; then
  note "pass: ${CASES} cases as expected."
  exit 0
fi
note "fail: ${FAILED} of ${CASES} cases not as expected."
exit 1
