#!/usr/bin/env bash
#
# deploy.sh -- build the gateway and mock API images, push them, apply the platform root with their
# digests, wait for a steady service, and assert that the running gateway is the one pushed.
#
# Task 1.7 (#58); step 5 of docs/plan/05-infrastructure-and-cost.md, First deployment, and the
# Gateway lever of its Deployment and rollback table: "deploy.sh builds, pushes, applies the new
# digest and checks that the running task has it".
#
#   tools/scripts/deploy.sh
#   tools/scripts/deploy.sh --url https://app1.spa-poc.test:9443    # the wait's origin, another port
#
# In order, and it stops at the first that fails:
#   1. It reads the live registry from the bucket the gateway reads, and refuses when there is none
#      or it holds no app: a gateway started against an empty registry never becomes ready, so the
#      deployment would fail its health checks and roll back (contracts/README.md). Run seed.sh
#      first. Nothing is built before this. It keeps the registry's revision for step 5.
#   2. It builds both images for ARM64 and pushes each under a tag of this run's own, since the
#      repositories' tags are immutable, then takes each digest from ECR. Each build pulls its base
#      image (--pull), so every deployment resolves the floating tag, mcr.microsoft.com/dotnet/
#      aspnet:10.0-noble among them, afresh: without it a workstation whose cache holds an older
#      image builds on that one, and could ship a .NET runtime that predates the security fixes
#      the tag's current image carries (#273, #288). --pull is Docker's "Always attempt to pull all
#      referenced images": https://docs.docker.com/reference/cli/docker/buildx/build/
#   3. It plans and applies infra/envs/poc/platform with both images by digest and the health
#      check on /readyz, and records them in images.auto.tfvars there, so every later apply of the
#      root passes the same digests back and never moves the image.
#   4. It writes infra/envs/poc/outputs.json from the three roots' outputs.
#   5. It waits for the service to be stable, then asserts the primary deployment completed on the
#      applied task definition, every running task's gateway container has the pushed digest, each
#      running task's own target is healthy on /readyz, every other target is healthy or draining,
#      and the outputs document names the applied gateway. It tolerates draining because a stable
#      service still lists the previous task's target as draining for the target group's
#      deregistration delay, and on the first deployment that task, the sample image, fails the new
#      /readyz check meanwhile (#207). Any other state, unhealthy.draining among them, fails.
#      Then it waits, through the gateway origin, until each running task has answered /readyz at
#      the revision step 1 read or later (cell-common.sh's wait_for_revision; change request P2-5,
#      decision 6, #309): one answer through the load balancer speaks for one task only, and a task
#      the load balancer never picks fails the wait. The load balancer is internal, so the origin is
#      the tunnel's, https://app1.$SPA_CERT_DOMAIN:$SPA_TUNNEL_PORT, by default
#      https://app1.spa-poc.test:8443, unless --url names another. tools/scripts/tunnel.sh has to be
#      running in another terminal by then, through a task of this deployment: the tunnel's hop is a
#      gateway task, and a deployment replaces it.
#
# The cell's names come from infra/envs/poc/outputs.json, which seed.sh wrote, and never from
# Terraform state (contracts/infra-outputs.schema.json).
#
# Run it as the operator's own session: the spa-platform profile, with tools/aws-project.env
# sourced, after `terraform init -backend-config=backend.hcl` in each root. It needs the AWS CLI,
# Terraform, python3, Docker with buildx (decision D-2 in docs/dependencies.md), and the .NET SDK for
# spactl, or SPACTL naming a spactl to run instead (cell-common.sh).
#
# It redacts two kinds of value, and Docker's and Terraform's output pass through the filter that
# does it (cell-common.sh's redact_stream):
#   - the account id, which the repositories, the bucket and every ARN carry, becomes <account>;
#   - each app's client id that infra/envs/poc/outputs.json lists becomes <client-id:appId>. The
#     plan shows the mock API's environment, MockApi__App1Client and MockApi__App2Client among it
#     (#251). The list is the outputs document's as this run starts: a client id the apps root
#     changed after seed.sh last wrote that document is not on it. Run seed.sh after any apply of the
#     apps root.
#
# Exit status: 0 when the applied gateway runs steady and healthy; 1 when a step failed; 2 when it
# did not start (a usage error, a missing tool, no Region, no credentials, no outputs document).
#
# Written for the bash 3.2 that ships with macOS, like tools/scripts/certs.sh.
# Owner: workstream 1, AWS and IaC.

set -euo pipefail
umask 077

PROGRAM="deploy"
# shellcheck source=tools/scripts/cell-common.sh
. "$(dirname -- "$0")/cell-common.sh"

usage() {
  # The header above, up to the Owner line, is this script's manual.
  sed -n '3,/^# Owner:/p' "$0" | sed 's/^# \{0,1\}//'
  cat <<EOF

Options
  --url ORIGIN   The gateway origin step 5's wait reads /readyz through. Default: the tunnel's,
                 https://app1.\$SPA_CERT_DOMAIN:\$SPA_TUNNEL_PORT, else https://app1.spa-poc.test:8443.
  -h, --help     This text.
EOF
}

value_of() {
  [ "$2" -ge 2 ] || refuse "$1 needs a value. Try --help."
}

# tools/scripts/verify-tunnel.sh's names for the tunnel: the first app's host under the certificate's
# domain, on the tunnel's local port.
ORIGIN="https://app1.${SPA_CERT_DOMAIN:-spa-poc.test}:${SPA_TUNNEL_PORT:-8443}"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --url) value_of "$1" "$#"; ORIGIN="$2"; shift ;;
    -h | --help)
      usage
      exit 0
      ;;
    *) refuse "unknown argument '$1'. Try --help." ;;
  esac
  shift
done

trap cell_cleanup EXIT
trap 'exit 130' INT TERM

case "$ORIGIN" in
  https://?* | http://?*) ;;
  *) refuse "--url must be an http or https origin: got '${ORIGIN}'" ;;
esac

need_terraform
need_spactl
need_tool docker "brew install --cask docker-desktop (decision D-2, docs/dependencies.md)"
docker buildx version > /dev/null 2>&1 || refuse "docker buildx does not answer. Start Docker Desktop."
cell_start

# From the outputs document seed.sh wrote, never from state (contracts/infra-outputs.schema.json).
ECR_GATEWAY="$(outputs_field ecrGateway)"
ECR_MOCK_API="$(outputs_field ecrMockApi)"
CLUSTER="$(outputs_field ecsCluster)"
SERVICE="$(outputs_field ecsService)"
# After the reads above, so that a missing or foreign outputs document is refused by them, naming
# the field. Before anything prints: Terraform's plan names both apps' client ids (#251).
redact_clients

# --- 1. the registry -------------------------------------------------------------------------------
#
# The bucket and key the gateway polls are the ones in its own task definition's environment, so
# this reads what the gateway will read, not a second copy of the names.

read_service
read_platform_config
[ -n "$REGISTRY_BUCKET" ] && [ -n "$REGISTRY_KEY" ] ||
  fail "the gateway container's environment names no Registry__Bucket or Registry__Key."
aws_call s3api get-object --bucket "$REGISTRY_BUCKET" --key "$REGISTRY_KEY" "${WORK}/registry.json"
case "$OUTCOME" in
  success) ;;
  NoSuchKey) fail "there is no live registry at ${REGISTRY_KEY}: a gateway started against it never becomes ready. Run tools/scripts/seed.sh first." ;;
  *) fail "could not read the live registry at ${REGISTRY_KEY}: ${OUTCOME}" ;;
esac
APPS="$(documents apps "${WORK}/registry.json")" || fail "the live registry at ${REGISTRY_KEY} is not a registry document."
# contracts/README.md: "An empty apps array is a valid document. Deployment refuses to start a
# gateway against an empty registry (task 1.7); that is a deployment rule, not a document rule."
[ "$APPS" -gt 0 ] ||
  fail "the live registry at ${REGISTRY_KEY} holds no app, and a gateway started against an empty registry never becomes ready. Run tools/scripts/seed.sh first."
note "the live registry holds ${APPS} apps"
REVISION="$(documents revision "${WORK}/registry.json")" || fail "the live registry at ${REGISTRY_KEY} has no revision."
note "the live registry is at revision ${REVISION}"

# --- 2. build and push -----------------------------------------------------------------------------
#
# One tag per run, because the repositories' tags are immutable (infra/modules/cell/storage.tf): a
# second push of a tag is refused, and a tag can never name two images.

TAG="$(date -u +%Y%m%dT%H%M%SZ)-$$"

# The password goes from the AWS CLI to Docker on a pipe and never into an argument, a file or the
# output. https://docs.aws.amazon.com/AmazonECR/latest/userguide/registry_auth.html
REGISTRY_HOST="${ECR_GATEWAY%%/*}"
aws --region "$REGION" ecr get-login-password 2> "$ERR" |
  docker login --username AWS --password-stdin "$REGISTRY_HOST" 2>&1 | redact_stream ||
  fail "could not log Docker in to the cell's registry."

# $1 the Dockerfile, $2 the repository URI. Prints the image by digest, repository@sha256:<hex>.
build_and_push() {
  local dockerfile="$1" repository="$2" name="${2#*/}"
  note "building ${dockerfile} for linux/arm64 as $(redact "${repository}:${TAG}")" >&2
  # ARM64, the task's platform (ecs.tf's runtime_platform). No provenance or SBOM attestation: with
  # either, buildx pushes an image index whose digest is not the platform manifest's, and the digest
  # pushed is the one asserted against the running task below.
  # https://docs.docker.com/build/metadata/attestations/#creating-attestations
  docker_build "$dockerfile" "${repository}:${TAG}" >&2 ||
    fail "building ${dockerfile} failed."
  aws_read "the digest of ${name}:${TAG}" ecr describe-images --repository-name "$name" \
    --image-ids "imageTag=${TAG}" --query 'imageDetails[0].imageDigest'
  local digest
  digest="$(printed)"
  case "$digest" in
    sha256:[0-9a-f]*) ;;
    *) fail "ECR named no digest for ${name}:${TAG}." ;;
  esac
  printf '%s@%s' "$repository" "$digest"
}

# Docker's own output, through the redaction: it names the repository, which carries the account.
docker_build() {
  local status=0
  docker buildx build --pull --platform linux/arm64 --provenance=false --sbom=false \
    --file "${REPO_ROOT}/$1" --tag "$2" --push "$REPO_ROOT" 2>&1 | redact_stream || status=$?
  return "$status"
}

# The Dockerfiles say to build from the repository root (src/Gateway/Dockerfile,
# fixtures/MockApi/Dockerfile).
GATEWAY_IMAGE="$(build_and_push src/Gateway/Dockerfile "$ECR_GATEWAY")" || exit 1
MOCK_API_IMAGE="$(build_and_push fixtures/MockApi/Dockerfile "$ECR_MOCK_API")" || exit 1
note "pushed the gateway as ${GATEWAY_IMAGE##*@} and the mock API as ${MOCK_API_IMAGE##*@}"

# --- 3, 4 and 5 ------------------------------------------------------------------------------------

apply_images "$GATEWAY_IMAGE" "$MOCK_API_IMAGE"
wait_and_assert "$GATEWAY_IMAGE"
wait_for_revision "$ORIGIN" "$REVISION"
note "pass: the gateway ${GATEWAY_IMAGE##*@} runs steady in ${CLUSTER}, and every running task reports revision ${REVISION} or later on ${READY_PATH}"
