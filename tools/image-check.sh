#!/usr/bin/env bash
# The gateway image, built and started the way a workstation builds it (#210), and the task's two
# images started on a read-only root (#1169). Owner: integrator.
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
# Last, it starts both of the task's images on a read-only root, the way #787 runs them (#1169): the
# gateway's again, with `--read-only --tmpfs /tmp`, since #780's third answer gives it /tmp and no
# other writable path; and fixtures/MockApi/Dockerfile's, built from the same copy of the tree, with
# `--read-only` and no tmpfs, since that answer names no path for it. Neither start passes --user.
# It passes only when the gateway logs that it started and the mock API prints both of its
# "listening on" lines, and fails when either throws, stops or stays silent for STARTUP_SECONDS. The
# pass line says that both images came up on a read-only root. What this does not measure: the
# Fargate volume #787 mounts at /tmp, which is ECS's and not Docker's tmpfs; a write that first
# happens on a request, since nothing is asked of either process; and where the gateway's default
# key ring keeps its keys, since a start line shows only that the process came up.
#
# For this machine's architecture, not the ECS task's ARM64: both defects are the same on every
# architecture, and an image for another one needs an emulator CI's runner is not given. No port is
# published: the container's own log says whether the process started, so nothing on the host is
# bound and two runs at once do not collide. The images and the containers are removed on every way
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
readonly_name="$name-readonly"
mock_name="$name-mock"
mock_image="spa-image-check/mock-api:$$"
cleanup() {
  "$docker" rm -f "$name" > /dev/null 2>&1 || true
  "$docker" rm -f "$readonly_name" > /dev/null 2>&1 || true
  "$docker" rm -f "$mock_name" > /dev/null 2>&1 || true
  "$docker" image rm "$image" > /dev/null 2>&1 || true
  "$docker" image rm "$mock_image" > /dev/null 2>&1 || true
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
# A listing that read nothing would read as a file system with no shell (CODING_STANDARDS.md, "A check
# fails closed"), so it must first name the file the Dockerfile itself puts there, the entry point.
grep -E '^(\./)?app/Gateway\.dll$' "$work/rootfs.txt" > /dev/null \
  || fail "the gateway container's exported file system does not list app/Gateway.dll, so it cannot show whether the image has a shell."
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

# The read-only starts (#1169). #780's third answer gives the gateway /tmp as its only writable path
# and the mock API none, and #787 sets readonlyRootFilesystem on both containers; these starts are
# what measures that contract. They come after every check of the first start, so each of those
# keeps its own verdict and the export above read the first container. Each is held to the wait
# the first start has: it ends on the line that says the process came up, on an unhandled
# exception, on a container that no longer runs, or after STARTUP_SECONDS.
#
# Waits for container $1 to log at least $2 lines holding the fixed string $3, copying its log to
# $4. Returns 0 when they came, 1 when the process threw or stopped first, and 2 when the wait ran
# out.
wait_for_lines() {
  local container="$1" want="$2" pattern="$3" log="$4" waited=0 count
  while [ "$waited" -lt "$STARTUP_SECONDS" ]; do
    "$docker" logs "$container" > "$log" 2>&1 || true
    count="$(grep -cF -- "$pattern" "$log" || true)"
    if [ "${count:-0}" -ge "$want" ]; then
      return 0
    fi
    if grep -F 'Unhandled exception' "$log" > /dev/null; then
      return 1
    fi
    if [ "$("$docker" inspect -f '{{.State.Running}}' "$container")" != true ]; then
      return 1
    fi
    sleep 1
    waited=$((waited + 1))
  done
  return 2
}

# The gateway again, on a read-only root with a tmpfs at /tmp, and as the image's own user.
"$docker" run -d --name "$readonly_name" --read-only --tmpfs /tmp "$image" > /dev/null \
  || fail "the gateway image could not be started a second time, on a read-only root with a tmpfs at /tmp."
waited_for=0
wait_for_lines "$readonly_name" 1 'Application started' "$work/readonly.log" || waited_for=$?
if [ "$waited_for" -ne 0 ]; then
  "$docker" logs "$readonly_name" 2>&1 | tail -20 >&2 || true
  if [ "$waited_for" -eq 1 ]; then
    fail "on a read-only root with a tmpfs at /tmp, the gateway image threw or stopped before it logged Application started; a write outside /tmp as it starts is one cause."
  fi
  fail "on a read-only root with a tmpfs at /tmp, the gateway image did not log Application started within ${STARTUP_SECONDS}s."
fi
echo "image-check: read-only: the gateway image came up on a read-only root with a tmpfs at /tmp."

# The mock API, from the same copy of the tree, which is the context its Dockerfile's header asks for.
echo "image-check: building fixtures/MockApi/Dockerfile from the same copy of the tree."
if ! "$docker" build --progress=plain -f "$context/fixtures/MockApi/Dockerfile" -t "$mock_image" "$context" \
  > "$work/mock-build.log" 2>&1; then
  tail -40 "$work/mock-build.log" >&2
  fail "the mock API image did not build from the copy of the tree."
fi

# On a read-only root with no tmpfs: #780 names no path the mock API writes, and no file under
# fixtures/MockApi/ names one. The three values its Dockerfile leaves empty, without which it
# refuses to start; nothing is asked of it, so the issuer is never reached.
"$docker" run -d --name "$mock_name" --read-only \
  -e MockApi__Issuer=https://issuer.invalid/ -e MockApi__App1Client=image-check-app1 \
  -e MockApi__App2Client=image-check-app2 "$mock_image" > /dev/null \
  || fail "the mock API image could not be started on a read-only root."
# fixtures/MockApi/Program.cs prints "<api> listening on <address>, ..." for each of its two
# listeners once MockApiHost.StartAsync has returned. Kestrel's own "Now listening on: <address>"
# lines come before them, one per listener, and carry a colon, so the space after "on" keeps them out.
waited_for=0
wait_for_lines "$mock_name" 2 'listening on ' "$work/mock.log" || waited_for=$?
if [ "$waited_for" -ne 0 ]; then
  "$docker" logs "$mock_name" 2>&1 | tail -20 >&2 || true
  if [ "$waited_for" -eq 1 ]; then
    fail "on a read-only root, the mock API image threw or stopped before both its listeners printed listening on; a write as it starts is one cause, and #780 names no writable path for it."
  fi
  fail "on a read-only root, the mock API image did not print both its listening on lines within ${STARTUP_SECONDS}s."
fi
echo "image-check: read-only: the mock API image came up on a read-only root, both listeners listening."

echo "image-check: pass: the gateway image built from a tree with a host obj/ and an owner-only appsettings.json, and the gateway started as user $user, past WebApplication.CreateBuilder; then the gateway and mock API images both came up on a read-only root."
