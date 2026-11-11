#!/usr/bin/env bash
# The images' vulnerability scan (#785). Owner: integrator.
#
#     tools/image-scan.sh                          build the gateway's and the mock API's images, scan both
#     tools/image-scan.sh <name>=<image>...        scan images already built, under those names
#
# CI's Build and test job runs the first form, under that job's `if`, so a change with no .NET input
# skips it as the job skips, and main's protection is not left waiting on a check a `paths` filter
# withheld (the workflow's header says why). The second form scans what is already there, an image
# with a planted finding or a deployed digest, through the same steps.
#
# #779 chose Trivy in CI, failing on a HIGH or CRITICAL finding that has a fix and is not accepted
# (https://github.com/adammedford/spa-platform/issues/779#issuecomment-6767167834, answers 1 and 2).
# This script reads and tools/image_scan.py decides: each image is built from the repository root
# for this machine's architecture, as tools/image-check.sh builds the gateway's (the runner's Docker
# has no emulator for ARM64), saved to an archive, and read by the pinned Trivy image from that
# archive with `--input`, so no daemon socket is handed to a container
# (https://trivy.dev/latest/docs/target/container_image/#tar-files). `--list-all-pkgs` puts every
# package Trivy read in its JSON report, which is how the policy tells a scan that read the chiseled
# image's package list from one that read nothing and passed (#779, answer 1). The vulnerability
# database is downloaded into a named volume, so a second run on one machine reuses it; Trivy
# refreshes it when it is out of date.
#
# The policy's verdict is the script's: its exit status, 1 for a finding and 2 for a report it could
# not judge, and its last line. A step before it that fails, a build, a save or a scan, fails with a
# verdict of its own. The images it built and its working directory are removed on every way out.
#
# Docker: the docker on PATH, else Docker Desktop's own ~/.docker/bin/docker, as tools/image-check.sh
# finds it; neither, or a daemon that does not answer, fails. Written for the bash 3.2 macOS ships.
# Its companion is tools/image_scan_tests.py, with a docker double; docs/dependencies.md lists the pin.

set -euo pipefail

# Trivy 0.76.1, released 2026-10-21, by the digest of its multi-architecture index, read on
# 2026-11-11 with `docker buildx imagetools inspect aquasec/trivy:0.76.1`.
TRIVY_IMAGE="aquasec/trivy:0.76.1@sha256:790fd66d1697395d8693d1acaa1ea702406a20f109b6fc7bac8db623eca6c06c"
CACHE_VOLUME="spa-image-scan-trivy-cache"

fail() {
  echo "image-scan: fail: $1" >&2
  exit "${2:-1}"
}

# The images to scan, as name=image lines; built ones are also listed for removal.
targets=""
for arg in "$@"; do
  case "$arg" in
    [!=]*=?*) targets="$targets $arg" ;;
    *) fail "$arg is not <name>=<image>." 2 ;;
  esac
done

if command -v docker > /dev/null; then
  docker="$(command -v docker)"
elif [ -x "${HOME:-}/.docker/bin/docker" ]; then
  PATH="$HOME/.docker/bin:$PATH"
  docker="$HOME/.docker/bin/docker"
else
  fail "docker is not on PATH, nor at ~/.docker/bin/docker, where Docker Desktop installs it."
fi
"$docker" info > /dev/null 2>&1 || fail "the Docker daemon behind $docker does not answer; start it."

root="$(git rev-parse --show-toplevel)" || fail "not inside a git checkout."
cd "$root"

work="$(mktemp -d "${TMPDIR:-/tmp}/image-scan.XXXXXX")" || fail "no working directory could be made."
built=""
cleanup() {
  for image in $built; do
    "$docker" image rm "$image" > /dev/null 2>&1 || true
  done
  rm -rf "$work"
}
trap cleanup EXIT

if [ -z "$targets" ]; then
  for pair in gateway=src/Gateway/Dockerfile mock-api=fixtures/MockApi/Dockerfile; do
    name="${pair%%=*}"
    dockerfile="${pair#*=}"
    image="spa-image-scan/$name:$$"
    echo "image-scan: building $dockerfile as $image"
    "$docker" build --progress=plain -f "$dockerfile" -t "$image" . \
      || fail "the $name image did not build from $dockerfile."
    built="$built $image"
    targets="$targets $name=$image"
  done
fi

reports=""
for target in $targets; do
  name="${target%%=*}"
  image="${target#*=}"
  "$docker" save -o "$work/$name.tar" "$image" || fail "the $name image $image could not be saved."
  echo "image-scan: scanning $name ($image) with $TRIVY_IMAGE"
  "$docker" run --rm -v "$work:/scan" -v "$CACHE_VOLUME:/root/.cache/trivy" "$TRIVY_IMAGE" \
    image --quiet --scanners vuln --list-all-pkgs --format json \
    --input "/scan/$name.tar" --output "/scan/$name.json" \
    || fail "Trivy did not scan the $name image; the reason is above."
  reports="$reports $name=$work/$name.json"
done

# $reports is unquoted on purpose: one argument per image, and no path here holds a space but
# TMPDIR's, which mktemp's own directory never does on macOS or the runner.
status=0
python3 -I tools/image_scan.py --accepted .github/image-scan-accepted.json $reports || status=$?
exit "$status"
