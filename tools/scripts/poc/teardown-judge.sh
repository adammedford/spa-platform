#!/usr/bin/env bash
#
# teardown-judge.sh -- the proof cell's teardown judge: whether a listed ARN that
# tools/scripts/verify-teardown.sh has no describe for is gone or stands.
#
# #1351. verify-teardown.sh knows the cell module's types. The proof cell's identity root adds one,
# its Cognito user pool, which the tagging index went on listing 16 minutes after #242's teardown had
# destroyed it, while DescribeUserPool answered ResourceNotFoundException (#466). That case was
# verify-teardown.sh's own until #1351 took the proof cell's identity provider out of the reference
# scripts; the entry file infra/envs/poc/cell.env names this script in SPA_TEARDOWN_JUDGE, and
# verify-teardown.sh runs it with the ARN. Its contract is verify-teardown.sh's header.
#
#   tools/scripts/poc/teardown-judge.sh <arn>
#
# For a cognito-idp:userpool/<id> ARN it asks DescribeUserPool for the id: ResourceNotFoundException
# prints `gone`, an answer prints `stands Cognito still describes it`, and any other failure exits 1
# naming the error code. The pool's Status is documented as no longer used, so the describe reads
# the id.
# https://docs.aws.amazon.com/cognito-user-identity-pools/latest/APIReference/API_DescribeUserPool.html
# https://docs.aws.amazon.com/cognito-user-identity-pools/latest/APIReference/API_UserPoolType.html
# For an ARN of any other type it prints `stands` and asks nothing.
#
# It reads AWS_REGION, which verify-teardown.sh sets, and the operator's credentials. It names a call's
# error code, never its message, and the pool by its id, which carries no account id.
#
# Exit status: 0 with the answer; 1 when the describe answered neither; 2 when it did not start.
#
# Written for the bash 3.2 that ships with macOS.
# Owner: workstream 1, AWS and IaC.

set -euo pipefail
umask 077

PROGRAM="teardown-judge"
# shellcheck source=tools/scripts/cell-common.sh
. "$(dirname -- "$0")/../cell-common.sh"

[ "$#" -eq 1 ] || refuse "give one ARN."
ARN="$1"

case "$ARN" in
  arn:*:cognito-idp:*:*:userpool/?*) ;;
  *)
    echo "stands"
    exit 0
    ;;
esac

need_tool aws "brew install awscli"
REGION="${AWS_REGION:-${AWS_DEFAULT_REGION:-}}"
[ -n "$REGION" ] || refuse "no Region in AWS_REGION or AWS_DEFAULT_REGION."
trap cell_cleanup EXIT
WORK="$(mktemp -d "${TMPDIR:-/tmp}/${PROGRAM}.XXXXXX")"
OUT="${WORK}/out"
ERR="${WORK}/err"

POOL="${ARN##*/}"
aws_call cognito-idp describe-user-pool --user-pool-id "$POOL" --query UserPool.Id
case "$OUTCOME" in
  success) echo "stands Cognito still describes it" ;;
  ResourceNotFoundException) echo "gone" ;;
  *) fail "describing the user pool ${POOL}: ${OUTCOME}" ;;
esac
