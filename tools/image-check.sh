#!/usr/bin/env bash
# The gateway image, built and started the way a workstation builds it (#210). Owner: integrator.
#
#     tools/image-check.sh        from anywhere in the repository; tools/check.sh --full runs it
#
# The first deployment found two defects that a clean CI checkout cannot show, and the sitting that
# found them was the first thing that built the image (#58, sitting C, stage C1; fixed by #204):
#   - With no .dockerignore, `COPY src/ src/` laid the workstation's own obj/ over the image's
#     restore, and `dotnet publish --no-restore` failed (NETSDK1047).
#   - COPY and publish keep the modes the files had in the build context, so an owner-only
#     appsettings.json reached the final stage owner-only, and the gateway, running as $APP_UID,
#     died in WebApplication.CreateBuilder with UnauthorizedAccessException.
# So this check does not build the tree it is in. It copies what git tracks, as the working tree has
# it, into a directory of its own, plants both conditions there, a host obj/ and bin/ under
# src/Gateway and an appsettings.json only its owner can read, and builds src/Gateway/Dockerfile
# from that copy. The tree it was run in is left as it was.
#
# It passes only when the image builds, its configured user is not root, and the gateway, started
# as that user, logs that it started, which it can do only after WebApplication.CreateBuilder has
# read appsettings.json. It fails when the image does not build, runs as root (root reads anything,
# so an image without its USER would pass for the wrong reason), or throws, exits or stays silent
# for STARTUP_SECONDS. Nothing is pushed.
#
# For this machine's architecture, not the ECS task's ARM64: both defects are the same on every
# architecture, and an image for another one needs an emulator a CI runner does not have. No port is
# published: the container's own log says whether the process started, so nothing on the host is
# bound and two runs at once do not collide. The image and the container are removed on every way
# out; Docker keeps the layers, so a second run rebuilds only what changed.
#
# Docker: the docker on PATH, else Docker Desktop's own ~/.docker/bin/docker, which a Mac's Docker
# Desktop installs without putting on PATH. Neither, or a daemon that does not answer, fails the
# check rather than skipping it, as tools/check.sh's header says a full check must.
#
# Its lines say "image-check:", and its last line is its verdict. Written for the bash 3.2 macOS
# ships. Its companion is tools/image_check_tests.py, which needs Docker too.

set -euo pipefail

STARTUP_SECONDS=60

fail() {
  echo "image-check: fail: $1" >&2
  exit 1
}

if command -v docker > /dev/null; then
  docker="$(command -v docker)"
elif [ -x "${HOME:-}/.docker/bin/docker" ]; then
  # Its credential helper lives beside it, and the CLI finds that on PATH.
  PATH="$HOME/.docker/bin:$PATH"
  docker="$HOME/.docker/bin/docker"
else
  fail "docker is not on PATH, nor at ~/.docker/bin/docker, where Docker Desktop installs it."
fi
"$docker" info > /dev/null 2>&1 || fail "the Docker daemon behind $docker does not answer; start it."

root="$(git rev-parse --show-toplevel)"
cd "$root"

work="$(mktemp -d)"
name="spa-image-check-$$"
image="spa-image-check/gateway:$$"
cleanup() {
  "$docker" rm -f "$name" > /dev/null 2>&1 || true
  "$docker" image rm "$image" > /dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

# The tracked files, as the working tree has them and with their modes. A tracked file deleted from
# the working tree is not there to copy.
context="$work/context"
mkdir "$context"
git ls-files -z \
  | while IFS= read -r -d '' path; do
      if [ -e "$path" ] || [ -L "$path" ]; then printf '%s\0' "$path"; fi
    done \
  | tar --null -T - -cf - \
  | tar -xpf - -C "$context"

# The workstation's conditions. A host restore's assets file is for the host and no runtime, so the
# image's `--no-restore` publish cannot use it; this one is not even JSON, which fails the same way
# on every architecture.
mkdir -p "$context/src/Gateway/obj" "$context/src/Gateway/bin/Debug/net10.0"
echo "host build output, not for the image" > "$context/src/Gateway/obj/project.assets.json"
echo "host build output, not for the image" > "$context/src/Gateway/bin/Debug/net10.0/Gateway.dll"
chmod 600 "$context/src/Gateway/appsettings.json"

echo "image-check: building src/Gateway/Dockerfile from a copy of the tree with a host obj/ and an owner-only appsettings.json."
if ! "$docker" build --progress=plain -f "$context/src/Gateway/Dockerfile" -t "$image" "$context" \
  > "$work/build.log" 2>&1; then
  tail -40 "$work/build.log" >&2
  fail "the gateway image did not build; a host bin/ or obj/ in the build context is one cause, which .dockerignore keeps out."
fi

user="$("$docker" image inspect -f '{{.Config.User}}' "$image")"
case "$user" in
  '' | 0 | root | 0:* | root:*)
    fail "the gateway image runs as root, not as the runtime user, so it cannot show what that user can read."
    ;;
esac

# As the image's own user: no --user, so what runs is what ECS runs.
"$docker" run -d --name "$name" "$image" > /dev/null

started=false
waited=0
while [ "$waited" -lt "$STARTUP_SECONDS" ]; do
  "$docker" logs "$name" > "$work/run.log" 2>&1 || true
  # The lifetime's "Application started" line, which follows the listener's start.
  if grep -F 'Application started' "$work/run.log" > /dev/null; then
    started=true
    break
  fi
  # A process that threw can stay "running" for a minute while the runtime writes its crash
  # report, so the exception itself ends the wait.
  if grep -F 'Unhandled exception' "$work/run.log" > /dev/null; then
    break
  fi
  if [ "$("$docker" inspect -f '{{.State.Running}}' "$name")" != true ]; then
    break
  fi
  sleep 1
  waited=$((waited + 1))
done

if ! $started; then
  "$docker" logs "$name" 2>&1 | tail -20 >&2
  if grep -F 'Unhandled exception' "$work/run.log" > /dev/null \
    || [ "$("$docker" inspect -f '{{.State.Running}}' "$name")" != true ]; then
    fail "the gateway failed before it started; a file the runtime user cannot read is one cause, which the Dockerfile's chmod clears."
  fi
  fail "the gateway did not log that it started within ${STARTUP_SECONDS}s."
fi

echo "image-check: pass: the gateway image built from a tree with a host obj/ and an owner-only appsettings.json, and the gateway started as user $user, past WebApplication.CreateBuilder."
