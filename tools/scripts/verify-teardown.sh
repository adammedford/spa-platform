#!/usr/bin/env bash
#
# verify-teardown.sh -- prove that nothing carrying the platform tag remains in the platform project
# (test P-05), through every reader that can see the tag. teardown.sh ends by running it, and its exit
# status is teardown's; workstream 6 runs it on its own as P-05 (#63).
#
# #308, in the shape #232 Q3 recommends and #118 grilled. One query of the tagging API cannot prove
# it, for three reasons read on the platform project:
#   - The tagging API's GetResources does not support IAM roles, in any Region (#118 step 1), and
#     the cell's two roles carry the platform tag (infra/modules/cell/iam.tf).
#   - It answers for the Region it is asked in, and the project's managed SCP refuses it outside the
#     selected Region (#118, 2026-10-20). Every root and certs.sh create in the selected Region (#241).
#   - After a correct destroy it is not empty (#118 step 2): ECS keeps INACTIVE clusters and services
#     for a while and deregistered task definitions until they are deleted, and the index went on
#     listing security groups and rules EC2 no longer knew.
#
# So four readers, each matching Platform = spa-platform exactly (contracts/resource-tags.md;
# ADR-0013), in this order, each one line:
#   1. IAM roles by tag: every role in the project, its Platform tag read through IAM's own API
#      (#129 Q29). Bootstrap's roles carry spa-platform-bootstrap and service-linked roles no tag, so
#      neither answers, and there is no name list or path exclusion here.
#   2. IAM customer managed policies by tag, the same way: the roots create none, so this is the
#      drift guard #129 Q27 asked for.
#   3. The cell's roles by name: the roles infra/modules/cell/iam.tf declares for the cell, each
#      confirmed absent by get-role. A role that stands without its tags is invisible to reader 1.
#   4. The tag query in the selected Region. An ECS cluster, service or task definition whose
#      describe says INACTIVE is dropped, and so is a task definition teardown.sh deleted
#      (DELETE_IN_PROGRESS) or one ECS no longer knows; an EC2 id is dropped when EC2's describe does
#      not know it, a NAT gateway or a VPC endpoint also when EC2 says it is deleted (#347), the
#      cell's session table when DynamoDB's does not know it (#338), and the identity root's user
#      pool when Cognito's does not (#466). Anything else listed stands: the key ring's parameters
#      among them, which teardown.sh deletes first.
# A reader that finds something is asked again, every SPA_TEARDOWN_TAG_POLL seconds, until it finds
# nothing or SPA_TEARDOWN_TAG_WAIT runs out: the tagging API can lag a deletion, so the query retries
# for a few minutes first (docs/plan/05-infrastructure-and-cost.md, Teardown). A reader that has
# passed is not asked again.
#
# What it does not read: the bootstrap root's three resources and the state bucket, which carry
# spa-platform-bootstrap and are removed by hand; and us-east-1, where the tagging API is refused
# and nothing of the platform's is created: certs.sh refuses rather than fall back there (#241).
#
#   tools/scripts/verify-teardown.sh [--cell NAME]
#
# Reads only. Run it as the operator's own session: the spa-platform profile, with
# tools/aws-project.env sourced. It needs the AWS CLI and python3, and no Terraform state. It never
# prints the account id: of a resource still standing it names the ARN with the account replaced.
#
# Exit status: 0 when no reader finds anything; 1 when one still does after the wait, or a read or a
# describe answered something other than found or not found; 2 when it did not start.
#
# Written for the bash 3.2 that ships with macOS, like tools/scripts/certs.sh.
# Owner: workstream 1, AWS and IaC.

set -euo pipefail
umask 077

PROGRAM="verify-teardown"
# shellcheck source=tools/scripts/cell-common.sh
. "$(dirname -- "$0")/cell-common.sh"

CELL="${SPA_CELL:-poc}"
# contracts/resource-tags.md: the platform tag, matched exactly.
PLATFORM_TAG_KEY="Platform"
PLATFORM_TAG_VALUE="spa-platform"
# "Retries for a few minutes": five, every thirty seconds, unless the environment says otherwise.
# The names are teardown.sh's, which runs this with its own environment.
TAG_WAIT_SECONDS="${SPA_TEARDOWN_TAG_WAIT:-300}"
TAG_POLL_SECONDS="${SPA_TEARDOWN_TAG_POLL:-30}"
# The roles infra/modules/cell/iam.tf declares, "${local.name_prefix}-<suffix>", where the prefix is
# spa-platform-<cell> (network.tf). verify_teardown_tests.py reads iam.tf and holds this list to it.
CELL_ROLE_SUFFIXES="task-execution task"

usage() {
  # The header above, up to the Owner line, is this script's manual.
  sed -n '3,/^# Owner:/p' "$0" | sed 's/^# \{0,1\}//'
  cat <<EOF

Options
  --cell NAME   The cell whose roles are checked by name. Default: \$SPA_CELL, else poc.
  -h, --help    This text.

Environment
  SPA_TEARDOWN_TAG_WAIT   Seconds a reader that finds something is retried for. Default 300.
  SPA_TEARDOWN_TAG_POLL   Seconds between its attempts. Default 30.
EOF
}

while [ "$#" -gt 0 ]; do
  case "$1" in
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
for value in "$TAG_WAIT_SECONDS" "$TAG_POLL_SECONDS"; do
  case "$value" in
    '' | *[!0-9]*) refuse "SPA_TEARDOWN_TAG_WAIT and SPA_TEARDOWN_TAG_POLL are whole seconds: got '${value}'" ;;
  esac
done

trap cell_cleanup EXIT
trap 'exit 130' INT TERM

cell_start
# Names and ARNs are split on whitespace below; none holds a glob character that should expand.
set -f

# Each reader writes what stands to $STANDING, one line each, and what it read to $SUMMARY.
STANDING=""
SUMMARY=""

stands() { printf '%s\n' "$*" >> "$STANDING"; }

# The listing's values, one per line: --output text puts a list of scalars on one line, tab
# separated, and a paginated listing one page per line.
listed() { tr '\t' '\n' < "$OUT" | awk 'NF'; }

# --- 1 and 2. IAM, by tag --------------------------------------------------------------------------

# $1 what is read, $2 the listing's subcommand and its arguments, then the tag read's subcommand and
# the option that names one. A role or policy deleted between the listing and the tag read answers
# NoSuchEntity, and is gone.
# https://docs.aws.amazon.com/IAM/latest/APIReference/API_ListRoleTags.html
# https://docs.aws.amazon.com/IAM/latest/APIReference/API_ListPolicyTags.html
iam_by_tag() {
  local what="$1" names tags_call="$2" option="$3" name count=0
  shift 3
  aws_read "the project's ${what}" iam "$@"
  names="$(listed)"
  for name in $names; do
    count=$((count + 1))
    aws_call iam "$tags_call" "$option" "$name" --query "Tags[?Key=='${PLATFORM_TAG_KEY}'].Value"
    case "$OUTCOME" in
      success) [ "$(printed)" != "$PLATFORM_TAG_VALUE" ] || stands "$name" ;;
      NoSuchEntity) ;;
      *) fail "reading the tags of $(redact "$name"): ${OUTCOME}" ;;
    esac
  done
  SUMMARY="none of ${count} carries ${PLATFORM_TAG_KEY}=${PLATFORM_TAG_VALUE}"
}

read_iam_roles() {
  iam_by_tag "IAM roles" list-role-tags --role-name list-roles --query 'Roles[].RoleName'
}

read_iam_policies() {
  iam_by_tag "IAM customer managed policies" list-policy-tags --policy-arn \
    list-policies --scope Local --query 'Policies[].Arn'
}

# --- 3. the cell's roles, by name --------------------------------------------------------------------

read_role_names() {
  local suffix role names=""
  for suffix in $CELL_ROLE_SUFFIXES; do
    role="spa-platform-${CELL}-${suffix}"
    names="${names}${names:+, }${role}"
    # https://docs.aws.amazon.com/IAM/latest/APIReference/API_GetRole.html: NoSuchEntity when the
    # role does not exist.
    aws_call iam get-role --role-name "$role" --query Role.Arn
    case "$OUTCOME" in
      success) stands "$role" ;;
      NoSuchEntity) ;;
      *) fail "reading the role ${role}: ${OUTCOME}" ;;
    esac
  done
  SUMMARY="none of ${names} stands"
}

# --- 4. the tag query in the selected Region ----------------------------------------------------------

INACTIVE=0
DELETED=0
UNKNOWN=0
EGRESS=0
TABLES=0
POOLS=0

# An ECS describe's status, or its failure's reason: INACTIVE is dropped (#118 step 2), and so is
# MISSING, which is how DescribeClusters and DescribeServices answer for one they do not know (a
# failure, not an error). Any other reason, or an answer with neither, stands: only MISSING says the
# resource is gone (CODING_STANDARDS.md, "A check fails closed"). $1 the ARN.
# https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_DescribeClusters.html
# https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_DescribeServices.html
ecs_status() {
  local arn="$1" status
  status="$(printed)"
  case "$status" in
    INACTIVE) INACTIVE=$((INACTIVE + 1)) ;;
    MISSING) DELETED=$((DELETED + 1)) ;;
    "") stands "${arn} (ECS answered neither a status nor a failure reason)" ;;
    # A task definition teardown.sh deleted, which ECS removes once no task uses it.
    # https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_DeleteTaskDefinitions.html
    DELETE_IN_PROGRESS) DELETED=$((DELETED + 1)) ;;
    *) stands "${arn} (ECS says ${status})" ;;
  esac
}

# An EC2 id confirmed by its describe: $1 the ARN, $2 the describe, $3 its id option, $4 the query,
# $5 the error code EC2 answers for an id it does not know.
# https://docs.aws.amazon.com/AWSEC2/latest/APIReference/errors-overview.html
ec2_describe() {
  local arn="$1"
  aws_call ec2 "$2" "$3" "${arn##*/}" --query "$4"
  case "$OUTCOME" in
    success)
      if [ -n "$(listed)" ]; then
        stands "${arn} (EC2 still describes it)"
      else
        UNKNOWN=$((UNKNOWN + 1))
      fi
      ;;
    "$5") UNKNOWN=$((UNKNOWN + 1)) ;;
    *) fail "describing $(redact "$arn"): ${OUTCOME}" ;;
  esac
}

# The two-task cell's NAT gateway and VPC endpoints (#347 Q8), which EC2 goes on describing after
# their deletion, in the state "deleted", and only later not at all: so the describe reads the
# state, and "deleted" is gone as the not-found code is. Any other state stands and is named,
# "deleting" included, which the wait asks again (CODING_STANDARDS.md, "A check fails closed"). $1
# the ARN, $2 the describe, $3 its id option, $4 the query, then the codes EC2 answers for an id it
# does not know: the errors page lists two for a NAT gateway.
# https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_DescribeNatGateways.html
# https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_VpcEndpoint.html
# https://docs.aws.amazon.com/AWSEC2/latest/APIReference/errors-overview.html
ec2_state() {
  local arn="$1" state code
  aws_call ec2 "$2" "$3" "${arn##*/}" --query "$4"
  shift 4
  case "$OUTCOME" in
    success)
      state="$(printed)"
      case "$state" in
        # The API documents the endpoint's states capitalised and the CLI prints them in lower case,
        # so both spellings count.
        deleted | Deleted) EGRESS=$((EGRESS + 1)) ;;
        "") stands "${arn} (EC2 answered no state)" ;;
        *) stands "${arn} (EC2 says ${state})" ;;
      esac
      ;;
    *)
      for code in "$@"; do
        if [ "$OUTCOME" = "$code" ]; then
          EGRESS=$((EGRESS + 1))
          return 0
        fi
      done
      fail "describing $(redact "$arn"): ${OUTCOME}"
      ;;
  esac
}

judge() {
  local arn="$1" rest service resource cluster
  # arn:partition:service:region:account:resource, and the resource part may hold colons.
  rest="${arn#arn:}"
  rest="${rest#*:}"
  service="${rest%%:*}"
  rest="${rest#*:}"
  rest="${rest#*:}"
  resource="${rest#*:}"
  case "${service}:${resource}" in
    ecs:cluster/*)
      aws_call ecs describe-clusters --clusters "$arn" --query '[clusters[].status, failures[].reason][]'
      [ "$OUTCOME" = "success" ] || fail "describing $(redact "$arn"): ${OUTCOME}"
      ecs_status "$arn"
      ;;
    ecs:service/*/*)
      cluster="${resource#service/}"
      cluster="${cluster%%/*}"
      aws_call ecs describe-services --cluster "$cluster" --services "$arn" --query '[services[].status, failures[].reason][]'
      case "$OUTCOME" in
        success) ecs_status "$arn" ;;
        # The service's cluster is gone, and the service with it.
        ClusterNotFoundException) DELETED=$((DELETED + 1)) ;;
        *) fail "describing $(redact "$arn"): ${OUTCOME}" ;;
      esac
      ;;
    ecs:task-definition/*)
      aws_call ecs describe-task-definition --task-definition "$arn" --query taskDefinition.status
      case "$OUTCOME" in
        success) ecs_status "$arn" ;;
        # What DescribeTaskDefinition answers for a revision ECS has removed: "Unable to describe
        # task definition", a ClientException. ECS also answers ClientException for a caller without
        # permission and for an identifier that is not valid, so the code alone cannot judge: only
        # that message counts as gone, and any other ClientException fails (CODING_STANDARDS.md, "A
        # check fails closed").
        # https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_DescribeTaskDefinition.html
        ClientException)
          if awk '/Unable to describe task definition/ { found = 1 } END { exit !found }' "$ERR"; then
            DELETED=$((DELETED + 1))
          else
            fail "describing $(redact "$arn"): ClientException, and not the answer for a removed revision"
          fi
          ;;
        *) fail "describing $(redact "$arn"): ${OUTCOME}" ;;
      esac
      ;;
    ec2:security-group/*) ec2_describe "$arn" describe-security-groups --group-ids 'SecurityGroups[].GroupId' InvalidGroup.NotFound ;;
    ec2:security-group-rule/*)
      ec2_describe "$arn" describe-security-group-rules --security-group-rule-ids \
        'SecurityGroupRules[].SecurityGroupRuleId' InvalidSecurityGroupRuleId.NotFound
      ;;
    ec2:vpc/*) ec2_describe "$arn" describe-vpcs --vpc-ids 'Vpcs[].VpcId' InvalidVpcID.NotFound ;;
    ec2:subnet/*) ec2_describe "$arn" describe-subnets --subnet-ids 'Subnets[].SubnetId' InvalidSubnetID.NotFound ;;
    ec2:internet-gateway/*)
      ec2_describe "$arn" describe-internet-gateways --internet-gateway-ids \
        'InternetGateways[].InternetGatewayId' InvalidInternetGatewayID.NotFound
      ;;
    ec2:route-table/*) ec2_describe "$arn" describe-route-tables --route-table-ids 'RouteTables[].RouteTableId' InvalidRouteTableID.NotFound ;;
    # The two-task cell's egress (#347): the NAT gateway and the two gateway endpoints by state, and
    # the NAT gateway's Elastic IP by allocation id, which a release makes unknown at once.
    ec2:natgateway/*)
      ec2_state "$arn" describe-nat-gateways --nat-gateway-ids 'NatGateways[].State' \
        NatGatewayNotFound InvalidNatGatewayID.NotFound
      ;;
    ec2:vpc-endpoint/*)
      ec2_state "$arn" describe-vpc-endpoints --vpc-endpoint-ids 'VpcEndpoints[].State' \
        InvalidVpcEndpointId.NotFound InvalidVpcEndpoint.NotFound
      ;;
    ec2:elastic-ip/*) ec2_describe "$arn" describe-addresses --allocation-ids 'Addresses[].AllocationId' InvalidAllocationID.NotFound ;;
    # The cell's session table under session_store = "dynamodb" (#338), which goes with the platform
    # root. The index lags its deletion as it lags every other, so a table DescribeTable no longer
    # knows is dropped; one it still describes stands with its status, DELETING included, and is
    # asked again within the wait.
    # https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_DescribeTable.html
    dynamodb:table/*)
      aws_call dynamodb describe-table --table-name "${resource#table/}" --query Table.TableStatus
      case "$OUTCOME" in
        success) stands "${arn} (DynamoDB says $(printed))" ;;
        ResourceNotFoundException) TABLES=$((TABLES + 1)) ;;
        *) fail "describing $(redact "$arn"): ${OUTCOME}" ;;
      esac
      ;;
    # The identity root's user pool. The index went on listing it 16 minutes after #242's teardown
    # while DescribeUserPool answered ResourceNotFoundException (#466), so a pool Cognito no longer
    # knows is dropped and one it still describes stands.
    # https://docs.aws.amazon.com/cognito-user-identity-pools/latest/APIReference/API_DescribeUserPool.html
    # The pool's Status is documented as no longer used, so the describe reads the id.
    # https://docs.aws.amazon.com/cognito-user-identity-pools/latest/APIReference/API_UserPoolType.html
    cognito-idp:userpool/*)
      aws_call cognito-idp describe-user-pool --user-pool-id "${resource##*/}" --query UserPool.Id
      case "$OUTCOME" in
        success) stands "${arn} (Cognito still describes it)" ;;
        ResourceNotFoundException) POOLS=$((POOLS + 1)) ;;
        *) fail "describing $(redact "$arn"): ${OUTCOME}" ;;
      esac
      ;;
    # Every other type the index lists stands: the index lists a resource that carries the tag.
    *) stands "$arn" ;;
  esac
}

read_tag_index() {
  local arns arn count=0 dropped=""
  INACTIVE=0
  DELETED=0
  UNKNOWN=0
  EGRESS=0
  TABLES=0
  POOLS=0
  # --tag-filters matches a value exactly: no prefix, no wildcard (contracts/resource-tags.md).
  # https://docs.aws.amazon.com/resourcegroupstagging/latest/APIReference/API_TagFilter.html
  aws_read "the platform tag query" resourcegroupstaggingapi get-resources \
    --tag-filters "Key=${PLATFORM_TAG_KEY},Values=${PLATFORM_TAG_VALUE}" \
    --query 'ResourceTagMappingList[].ResourceARN'
  arns="$(listed)"
  for arn in $arns; do
    count=$((count + 1))
    judge "$arn"
  done
  [ "$INACTIVE" -eq 0 ] || dropped="${dropped}, ${INACTIVE} ECS records INACTIVE"
  [ "$DELETED" -eq 0 ] || dropped="${dropped}, ${DELETED} ECS records deleted"
  [ "$UNKNOWN" -eq 0 ] || dropped="${dropped}, ${UNKNOWN} EC2 ids describe does not know"
  [ "$EGRESS" -eq 0 ] || dropped="${dropped}, ${EGRESS} NAT gateways or VPC endpoints EC2 says deleted or does not know"
  [ "$TABLES" -eq 0 ] || dropped="${dropped}, ${TABLES} DynamoDB tables DynamoDB does not know"
  [ "$POOLS" -eq 0 ] || dropped="${dropped}, ${POOLS} Cognito user pools Cognito does not know"
  SUMMARY="${count} listed, none standing${dropped:+:}${dropped#,}"
}

label() {
  case "$1" in
    iam_roles) printf 'IAM roles by tag' ;;
    iam_policies) printf 'IAM customer managed policies by tag' ;;
    role_names) printf "the cell's roles by name" ;;
    tag_index) printf 'the tag query in %s' "$REGION" ;;
  esac
}

# --- the readers, retried ------------------------------------------------------------------------

READERS="iam_roles iam_policies role_names tag_index"
pending="$READERS"
waited=0
while :; do
  still=""
  for reader in $pending; do
    STANDING="${WORK}/standing-${reader}"
    : > "$STANDING"
    "read_${reader}"
    printf '%s\n' "$SUMMARY" > "${WORK}/summary-${reader}"
    if [ -s "$STANDING" ]; then
      still="${still} ${reader}"
    fi
  done
  pending="${still# }"
  [ -n "$pending" ] || break
  [ "$waited" -lt "$TAG_WAIT_SECONDS" ] || break
  sleep "$TAG_POLL_SECONDS"
  waited=$((waited + TAG_POLL_SECONDS))
  [ "$TAG_POLL_SECONDS" -gt 0 ] || waited=$((waited + 1))
done

failed=0
for reader in $READERS; do
  case " ${pending} " in
    *" ${reader} "*)
      failed=$((failed + 1))
      printf '%s: %s: fail: after %s seconds, these still stand:\n' "$PROGRAM" "$(label "$reader")" "$TAG_WAIT_SECONDS" >&2
      redact_stream < "${WORK}/standing-${reader}" | sed 's/^/  /' >&2
      ;;
    *) printf '%s: %s: %s\n' "$PROGRAM" "$(label "$reader")" "$(cat "${WORK}/summary-${reader}")" ;;
  esac
done

if [ "$failed" -gt 0 ]; then
  printf '%s: fail: %s of 4 readers still find something carrying %s=%s.\n' "$PROGRAM" "$failed" \
    "$PLATFORM_TAG_KEY" "$PLATFORM_TAG_VALUE" >&2
  exit 1
fi
note "pass: nothing carrying ${PLATFORM_TAG_KEY}=${PLATFORM_TAG_VALUE} remains in the platform project that a reader can see (P-05): IAM roles and customer managed policies by their tags, the cell's roles by name, and the tag query in ${REGION}. The bootstrap root's resources carry spa-platform-bootstrap and go last, by hand, with the state bucket."
exit 0
