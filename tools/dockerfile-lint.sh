#!/usr/bin/env bash
# hadolint over every tracked Dockerfile (#785). Owner: integrator.
#
#     tools/dockerfile-lint.sh        from anywhere in the repository; tools/check.sh --full runs it
#
# hadolint was out of #584's scope, and nothing linted a Dockerfile. This runs the pinned hadolint
# image over every Dockerfile git tracks, found by name rather than listed, so a new one is linted the
# day it lands, with the repository's .hadolint.yaml, whose comments give the reason for each rule it
# turns off. The checkout is mounted read-only and hadolint reads the files there, by the paths git
# names. tools/check.sh --full runs it when the diff touches a Dockerfile, .hadolint.yaml or this
# script, or under --both-halves.
#
# It fails, on a verdict line, when hadolint reports a finding at the configuration's threshold or does
# not run; when the listing fails or finds no Dockerfile, because a lint of nothing passes everything;
# and when .hadolint.yaml is missing, because hadolint would then lint by its defaults and the exceptions
# would be lost without a word (CODING_STANDARDS.md, "A check fails closed").
#
# Docker: the docker on PATH, else Docker Desktop's own ~/.docker/bin/docker, as tools/image-check.sh
# finds it; neither, or a daemon that does not answer, fails. Its lines say "dockerfile-lint:", and its
# last line is its verdict. Written for the bash 3.2 macOS ships. Its companion is
# tools/dockerfile_lint_tests.py; docs/dependencies.md lists the pin.

set -euo pipefail

# hadolint 2.15.1, released 2026-07-31, by the digest of its multi-architecture index, read on
# 2026-11-11 with `docker buildx imagetools inspect hadolint/hadolint:v2.15.1`.
HADOLINT_IMAGE="hadolint/hadolint:v2.15.1@sha256:32dac94127fd60b7b7e3fbfc65e1383b9b5e25c9bfd7b8536de7a539fe68a12d"

fail() {
  echo "dockerfile-lint: fail: $1" >&2
  exit 1
}

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

[ -f .hadolint.yaml ] \
  || fail ".hadolint.yaml is not there, so the rules it turns off and its threshold would be hadolint's defaults."

# git's pathspec `*` matches across `/`, so `*/Dockerfile` is a Dockerfile at any depth below the top
# (https://git-scm.com/docs/gitglossary#Documentation/gitglossary.txt-aiddefpathspecapathspec).
dockerfiles="$(git ls-files -- Dockerfile '*/Dockerfile')" \
  || fail "the Dockerfiles were not listed: git ls-files failed."
[ -n "$dockerfiles" ] || fail "git tracks no Dockerfile, so nothing was linted."

count=0
listed=""
for found in $dockerfiles; do
  count=$((count + 1))
  listed="${listed:+$listed, }$found"
done

# $dockerfiles is unquoted on purpose: one argument per path, and no tracked Dockerfile's path holds a
# space (bash 3.2, no arrays).
"$docker" run --rm -v "$root:/repo:ro" -w /repo "$HADOLINT_IMAGE" \
  hadolint --no-color --config .hadolint.yaml $dockerfiles \
  || fail "hadolint failed a Dockerfile, or did not run; its lines are above."

echo "dockerfile-lint: pass: $count Dockerfiles: $listed."
