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
# for STARTUP_SECONDS. Nothing is pushed. Before its verdict it prints the runtimes the image carries,
# read with `dotnet --list-runtimes` inside it, which fail nothing (#273).
#
# Then it holds the image's hardened shape (#781). It fails when the image has /bin/sh, which the
# chiseled base does not ship; when its environment does not set DOTNET_EnableDiagnostics=0; or when
# it lacks any of the org.opencontainers.image.source, .revision and .version labels. The chiseled
# image has no shell to look with, so the file system is read from outside: the started container is
# exported and its listing searched. An export or a listing that cannot be read fails the check
# rather than reading as an image with no shell.
#
# For this machine's architecture, not the ECS task's ARM64: both defects are the same on every
# architecture, and an image for another one needs an emulator CI's runner is not given. No port is
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
  | tar -xpf - -C "$context" \
  || fail "the tracked tree could not be copied into $context."

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

# Every step below fails through fail(), so the last line is a verdict and never a bare set -e exit.
user="$("$docker" image inspect -f '{{.Config.User}}' "$image")" \
  || fail "the built image $image could not be inspected."
case "$user" in
  '' | 0 | root | 0:* | root:*)
    fail "the gateway image runs as root, not as the runtime user, so it cannot show what that user can read."
    ;;
esac

# As the image's own user: no --user, so what runs is what ECS runs.
"$docker" run -d --name "$name" "$image" > /dev/null || fail "the gateway image could not be started."

started=false
waited=0
while [ "$waited" -lt "$STARTUP_SECONDS" ]; do
  "$docker" logs "$name" > "$work/run.log" 2>&1 || true
  # The lifetime's "Application started" line, which the generic host's console lifetime logs once
  # the host has started, after the listener:
  # https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/generic-host#ihostapplicationlifetime
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
  # The container's last lines, for the reader of the verdict below; a logs call that fails here must
  # not end the script before it.
  "$docker" logs "$name" 2>&1 | tail -20 >&2 || true
  if grep -F 'Unhandled exception' "$work/run.log" > /dev/null \
    || [ "$("$docker" inspect -f '{{.State.Running}}' "$name")" != true ]; then
    fail "the gateway failed before it started; a file the runtime user cannot read is one cause, which the Dockerfile's chmod clears."
  fi
  fail "the gateway did not log that it started within ${STARTUP_SECONDS}s."
fi

# The shared framework the image runs, read inside it with the command a workstation and CI read
# their own with, so this log says which patch the image carries (#273). The final stage names its
# base by digest (#781), so the patch is the one that digest holds, whatever the daemon has cached.
# Informational: no version fails the check.
runtimes="$("$docker" run --rm --entrypoint dotnet "$image" --list-runtimes 2> /dev/null)" \
  || fail "the built image's runtimes could not be read with dotnet --list-runtimes."
runtimes="$(printf '%s\n' "$runtimes" | awk 'NF >= 2 { printf "%s%s %s", sep, $1, $2; sep = ", " }')"
[ -n "$runtimes" ] || fail "dotnet --list-runtimes in the built image named no runtime."
echo "image-check: runtime: $runtimes, read with \`dotnet --list-runtimes\` in the built image."

# The hardened shape (#781). After the runtime reading, so each failure above keeps its own verdict.
# No shell: the started container's file system, exported and listed. /bin is a link to usr/bin on
# Ubuntu, so the shell is listed under either name.
"$docker" export "$name" > "$work/rootfs.tar" 2> /dev/null \
  || fail "the gateway container's file system could not be exported to look for a shell."
tar -tf "$work/rootfs.tar" > "$work/rootfs.txt" 2> /dev/null \
  || fail "the gateway container's exported file system could not be listed to look for a shell."
if grep -E '^(\./)?(usr/)?bin/sh$' "$work/rootfs.txt" > /dev/null; then
  fail "the gateway image has a shell at /bin/sh; its runtime stage builds on the chiseled base, which ships none."
fi

# Diagnostics off: the variable exactly, as the runtime reads it.
"$docker" image inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$image" > "$work/env.txt" \
  || fail "the built image's environment could not be inspected."
if ! grep -x 'DOTNET_EnableDiagnostics=0' "$work/env.txt" > /dev/null; then
  fail "the gateway image does not set DOTNET_EnableDiagnostics=0, so its debugger, profiler and EventPipe endpoints are open."
fi

# The OCI labels, each read on its own; one that is absent indexes to the empty string.
labels=""
for key in source revision version; do
  value="$("$docker" image inspect -f "{{index .Config.Labels \"org.opencontainers.image.$key\"}}" "$image")" \
    || fail "the built image's labels could not be inspected."
  [ -n "$value" ] \
    || fail "the gateway image has no org.opencontainers.image.$key label; the Dockerfile's final stage sets it from a build argument."
  labels="${labels:+$labels, }org.opencontainers.image.$key=$value"
done
echo "image-check: hardened: no /bin/sh, DOTNET_EnableDiagnostics=0, and the labels $labels."

echo "image-check: pass: the gateway image built from a tree with a host obj/ and an owner-only appsettings.json, and the gateway started as user $user, past WebApplication.CreateBuilder."
