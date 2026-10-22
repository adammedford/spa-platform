#!/usr/bin/env python3
"""Test companion for tools/image-check.sh, the gateway image built as a workstation builds it (#210).

Owner: integrator.

The first deployment found two defects a clean checkout cannot show (#58, sitting C, stage C1;
fixed by #204): with no `.dockerignore`, the host's own obj/ reached the image's restore and
`dotnet publish --no-restore` failed, and an owner-only appsettings.json reached the final stage
owner-only, so the gateway, running as `$APP_UID`, died in `WebApplication.CreateBuilder`.
tools/image-check.sh plants both conditions in a copy of the tree and builds and starts the image.
#210's third box is that it fails on the tree as it was before #204's fix and passes after it, so
the cases here run the real script, with real Docker, on copies of this repository's tracked tree:
one as it is, one without the `.dockerignore`, one without the Dockerfile's chmod, and one whose
image runs as root. Each copy is a git repository of its own, because the script copies what git
tracks.

**Docker is required, not optional.** A case that skipped without it would pass on the machine
that cannot run it, the failure CODING_STANDARDS.md names. The cases find Docker as the script
does, on PATH or at Docker Desktop's `~/.docker/bin/docker`, and one case runs the script without
either, to see it fail rather than pass. One class needs no daemon at all: a fake docker on a PATH of
its own takes the script down a failure path a real daemon cannot be made to take on demand.

Run: python3 -I tools/image_check_tests.py
"""

from __future__ import annotations

import importlib.util
import os
import pathlib
import shlex
import shutil
import stat
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("companion_runner", ROOT / "tools" / "companion_runner.py")
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)
SCRIPT = ROOT / "tools" / "image-check.sh"
DOCKERFILE = "src/Gateway/Dockerfile"
SETTINGS = "src/Gateway/appsettings.json"

# Docker Desktop's own install puts its CLI and the credential helper its config names here, which
# is where this Mac has them; a Linux runner has docker on PATH.
DESKTOP_BIN = pathlib.Path.home() / ".docker" / "bin"


def docker_path() -> str:
    """A PATH that reaches docker, its credential helper, git and tar, as the script's callers'
    do."""
    base = os.environ.get("PATH", "/usr/bin:/bin")
    if (DESKTOP_BIN / "docker").exists():
        return f"{DESKTOP_BIN}:{base}"
    return base


class Tree:
    """A copy of this repository's tracked files, committed in a repository of its own."""

    def __init__(self, test: unittest.TestCase):
        scratch = tempfile.TemporaryDirectory()
        test.addCleanup(scratch.cleanup)
        self.dir = pathlib.Path(scratch.name).resolve() / "tree"
        self.dir.mkdir()
        listed = subprocess.run(["git", "ls-files", "-z"], cwd=ROOT, capture_output=True, check=True).stdout
        for name in listed.decode().split("\0"):
            source = ROOT / name
            if not name or not source.is_file():
                continue
            target = self.dir / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target, follow_symlinks=False)
        self.git("init", "-q", "-b", "main")
        self.git("add", "-A")
        self.git("-c", "user.name=Image Check Tests", "-c", "user.email=image-check@users.noreply.github.com",
                 "commit", "-q", "-m", "The tree")

    def git(self, *args: str):
        subprocess.run(["git", *args], cwd=self.dir, check=True, capture_output=True,
                       env={"PATH": docker_path(), "HOME": str(self.dir.parent), "GIT_CONFIG_NOSYSTEM": "1"})

    def run(self, path: str | None = None) -> subprocess.CompletedProcess:
        env = {"PATH": path or docker_path(), "HOME": str(pathlib.Path.home()), "LC_ALL": "C"}
        return subprocess.run(["/bin/bash", str(SCRIPT)], cwd=self.dir, env=env, capture_output=True,
                              text=True, stdin=subprocess.DEVNULL, timeout=540)

    def without_lines(self, name: str, starting: str):
        path = self.dir / name
        lines = path.read_text().splitlines(keepends=True)
        kept = [line for line in lines if not line.startswith(starting)]
        assert kept != lines, f"{name} has no line starting {starting!r}"
        path.write_text("".join(kept))


def last_line(text: str) -> str:
    lines = text.splitlines()
    return lines[-1] if lines else ""


class ImageCheckTests(unittest.TestCase):
    def test_the_tree_as_it_is_builds_and_starts_as_the_runtime_user(self):
        tree = Tree(self)
        before = stat.S_IMODE((tree.dir / SETTINGS).stat().st_mode)
        result = tree.run()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertRegex(last_line(result.stdout),
                         r"^image-check: pass: the gateway image built from a tree with a host obj/ and an "
                         r"owner-only appsettings\.json, and the gateway started as user \d+, past "
                         r"WebApplication\.CreateBuilder\.$")
        # The runtime the image runs, read inside it the way a workstation and CI read theirs, so a
        # log says which patch of the shared framework the image carries (#273). A base image the
        # daemon cached before a patch shows here as the older version.
        self.assertRegex(result.stdout,
                         r"(?m)^image-check: runtime: (Microsoft\.\S+ 10\.0\.\d+, )*Microsoft\.NETCore\.App "
                         r"10\.0\.\d+(, Microsoft\.\S+ 10\.0\.\d+)*, read with `dotnet --list-runtimes` in the "
                         r"built image\.$")
        # The planted conditions are the copy's, never the tree's: the tree's file keeps its mode,
        # and no obj/ is left in it.
        self.assertEqual(before, stat.S_IMODE((tree.dir / SETTINGS).stat().st_mode))
        self.assertFalse((tree.dir / "src" / "Gateway" / "obj").exists())

    def test_without_the_dockerignore_the_image_does_not_build(self):
        # The tree before #204: the host's obj/ reaches `COPY src/ src/` and the publish fails.
        tree = Tree(self)
        (tree.dir / ".dockerignore").unlink()
        tree.git("commit", "-q", "-am", "No .dockerignore")
        result = tree.run()
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertEqual("image-check: fail: the gateway image did not build; a host bin/ or obj/ in the "
                         "build context is one cause, which .dockerignore keeps out.", last_line(result.stderr))

    def test_without_the_chmod_the_gateway_dies_in_create_builder(self):
        # The other half of the tree before #204: the image builds, and the runtime user cannot read
        # the owner-only appsettings.json.
        tree = Tree(self)
        tree.without_lines(DOCKERFILE, "RUN chmod")
        tree.git("commit", "-q", "-am", "No chmod")
        result = tree.run()
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertIn("UnauthorizedAccessException", result.stderr)
        self.assertEqual("image-check: fail: the gateway failed before it started; a file the runtime user "
                         "cannot read is one cause, which the Dockerfile's chmod clears.",
                         last_line(result.stderr))

    def test_an_image_that_runs_as_root_is_refused(self):
        # Root reads an owner-only file, so an image without its USER would pass for the wrong reason.
        tree = Tree(self)
        tree.without_lines(DOCKERFILE, "USER ")
        tree.git("commit", "-q", "-am", "No USER")
        result = tree.run()
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertEqual("image-check: fail: the gateway image runs as root, not as the runtime user, so it "
                         "cannot show what that user can read.", last_line(result.stderr))

    def test_without_docker_it_fails_rather_than_passing(self):
        tree = Tree(self)
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        bin_dir = pathlib.Path(scratch.name)
        for tool in ("git", "tar", "mktemp", "rm", "chmod", "mkdir", "cat"):
            found = shutil.which(tool)
            self.assertIsNotNone(found, f"{tool} is not on PATH")
            (bin_dir / tool).symlink_to(found)
        env = {"PATH": str(bin_dir), "HOME": scratch.name, "LC_ALL": "C"}
        result = subprocess.run(["/bin/bash", str(SCRIPT)], cwd=tree.dir, env=env, capture_output=True,
                                text=True, stdin=subprocess.DEVNULL, timeout=60)
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertEqual("image-check: fail: docker is not on PATH, nor at ~/.docker/bin/docker, where Docker "
                         "Desktop installs it.", last_line(result.stderr))


# A docker that answers every call the script makes, with no daemon behind it. Its image runs as a
# non-root user, and its container logs an unhandled exception and then is gone: the first `logs`
# call prints the exception and every later one fails as a removed container does. COUNTER is
# replaced with a file the case owns; the script's PATH holds no tool this needs.
FAKE_DOCKER = """#!/bin/bash
case "$1" in
  image) [ "$2" = inspect ] && echo 1654; exit 0 ;;
  run) echo fake-container; exit 0 ;;
  inspect) echo false; exit 0 ;;
  logs)
    calls=0
    [ -f COUNTER ] && read -r calls < COUNTER
    calls=$((calls + 1))
    echo "$calls" > COUNTER
    if [ "$calls" -eq 1 ]; then
      echo "Unhandled exception. System.UnauthorizedAccessException: Access to the path '/app/appsettings.json' is denied."
      exit 0
    fi
    echo "Error response from daemon: No such container: $2" >&2
    exit 3 ;;
  *) exit 0 ;;
esac
"""


class ImageCheckWithAFakeDockerTests(unittest.TestCase):
    def test_a_logs_call_that_fails_after_the_wait_still_ends_on_the_verdict(self):
        # The script's header promises that its last line is its verdict and never a bare set -e exit.
        # The container's last lines, printed before the verdict, come from a second `logs` call, and a
        # container that has gone by then makes that call fail.
        tree = Tree(self)
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        bin_dir = pathlib.Path(scratch.name) / "bin"
        bin_dir.mkdir()
        for tool in ("git", "tar", "mktemp", "rm", "chmod", "mkdir", "cat", "grep", "tail", "sleep"):
            found = shutil.which(tool)
            self.assertIsNotNone(found, f"{tool} is not on PATH")
            (bin_dir / tool).symlink_to(found)
        counter = pathlib.Path(scratch.name) / "logs-calls"
        docker = bin_dir / "docker"
        docker.write_text(FAKE_DOCKER.replace("COUNTER", shlex.quote(str(counter))))
        docker.chmod(0o755)

        env = {"PATH": str(bin_dir), "HOME": scratch.name, "LC_ALL": "C"}
        result = subprocess.run(["/bin/bash", str(SCRIPT)], cwd=tree.dir, env=env, capture_output=True,
                                text=True, stdin=subprocess.DEVNULL, timeout=60)
        self.assertEqual("2", counter.read_text().strip(), result.stdout + result.stderr)
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertEqual("image-check: fail: the gateway failed before it started; a file the runtime user "
                         "cannot read is one cause, which the Dockerfile's chmod clears.", last_line(result.stderr))


if __name__ == "__main__":
    runner.main()
