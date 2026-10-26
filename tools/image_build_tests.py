#!/usr/bin/env python3
"""What a workstation's image build may send, and what the images can read (#204). Owner: integrator.

#204 came from the first deployment (#58, sitting C, stage C1). A clean CI checkout cannot show its
three defects, so each case here builds the workstation's condition itself rather than reading
the tree this run happens to be in:

- **The build context.** With no `.dockerignore`, `COPY src/ src/` copied the host's own bin/ and
  obj/ over the image's linux-arm64 restore, and `dotnet publish --no-restore` failed with
  NETSDK1047; every build also sent the local certificate authority's private key, the scratch
  directory and the git-ignored Terraform variables to the builder. The cases hold the root
  `.dockerignore` to Docker's own matching rules, read below from the vendor's documentation, over
  paths named for each thing the context must not carry, and over every tracked file a Dockerfile
  copies, which it must.
- **The outputs document.** `tools/scripts/cell-common.sh` writes infra/envs/poc/outputs.json, which
  carries the account id and both apps' client ids. git itself answers whether it is ignored.
- **The runtime user's read.** `COPY` and `dotnet publish` keep the modes the files had on the
  workstation, so an owner-only appsettings.json reached the final stage owner-only and the gateway,
  running as `$APP_UID`, died with UnauthorizedAccessException in `WebApplication.CreateBuilder`.
  The cases run each Dockerfile's own build-stage steps after its publish against a published tree
  whose modes are owner-only, and read the modes that come out.

What no case here can show is the image itself, because these cases need no Docker.
tools/image-check.sh builds and starts it from a tree given both conditions, and runs in
tools/check.sh --full and in CI's Build and test job (#210).

Every case reads the checkout and none reads a script of its own, so a change to what it reads can
turn a case red with no change here. The line below names those files for tools/check.sh, whose full
mode runs this companion when a change touches one (#390), as CI's Documents job runs it on every
change: the root .dockerignore, every Dockerfile, which the census below finds wherever it is, the
files each Dockerfile copies from the context, git's ignore files and the script that writes the
outputs document. A file a case starts reading joins the line; tools/check_tests.py fails while one
is missing.

Checkout inputs: .dockerignore Dockerfile */Dockerfile global.json nuget.config Directory.Build.props Directory.Packages.props contracts/*.schema.json src/* fixtures/MockApi/* tools/bench/floor/* .gitignore */.gitignore tools/scripts/cell-common.sh

Run: python3 -I tools/image_build_tests.py
"""

from __future__ import annotations

import os
import pathlib
import re
import shlex
import stat
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
DOCKERIGNORE = ROOT / ".dockerignore"

# The Dockerfiles whose final stage runs a published tree as `$APP_UID`, each built from the
# repository root. tools/bench/floor/Dockerfile is in because its header says it follows the
# gateway's line for line, so a difference between them is a difference in the startup floor (#173).
# fixtures/MockApi/Dockerfile is workstream 5's, and is in since it carries the gateway's chmod step
# (#233), so the runtime user's read holds for all three images (#234).
DOCKERFILES = ("src/Gateway/Dockerfile", "tools/bench/floor/Dockerfile",
               "fixtures/MockApi/Dockerfile")

# Tracked Dockerfiles another owner writes that the runtime-read case does not yet hold, held to the
# context cases only: this file is the integrator's, and A-05 keeps one branch to one owner, so a
# case here cannot ask for a line in a file the same branch may not edit. One moves to DOCKERFILES
# once its owner's branch has landed the step (#234). Empty since the mock API's did.
OTHER_OWNERS: tuple[str, ...] = ()

# One path for each thing #204's first box says the context must not carry, at the depths the
# workstation puts them. Paths, not files: the matcher reads names, so none needs to exist.
MUST_NOT_SEND = {
    "the gateway's host build output": ("src/Gateway/bin/Debug/net10.0/Gateway.dll",
                                        "src/Gateway/obj/project.assets.json"),
    "a workstream project's host build output": ("src/Platform.Identity/obj/project.assets.json",),
    "the startup floor's host build output": ("tools/bench/floor/obj/project.assets.json",
                                              "tools/bench/floor/bin/Debug/net10.0/Bench.Floor.dll"),
    "the mock API's host build output": ("fixtures/MockApi/obj/project.assets.json",),
    "a test project's host build output": ("tests/Gateway.IntegrationTests/bin/Debug/x.dll",
                                           "TestResults/run.trx"),
    # tools/scripts/certs.sh writes to <repo>/.certs by default, and *.key and *.pem wherever --dir
    # puts them.
    "the local certificate authority": (".certs/spa-poc-ca.key", ".certs/spa-poc-ca.pem",
                                        "infra/local/ca.key", "some/where/leaf.pem", "a.p12",
                                        "b/c.pfx", ".certs/spa-poc-ca.srl"),
    "the scratch directory": (".scratch/overnight/log/research/notes.md", ".scratch/x"),
    "git's own directory, and a worktree's .git file": (".git/HEAD", ".git/objects/ab/cdef", ".git"),
    "Terraform's working directories": ("infra/envs/poc/platform/.terraform/providers/x",
                                        "infra/modules/m/.terraform/terraform.tfstate"),
    "Terraform state": ("infra/envs/poc/platform/terraform.tfstate",
                        "infra/envs/poc/platform/terraform.tfstate.backup"),
    "Terraform variables, backends and plans": ("infra/envs/poc/platform/images.auto.tfvars",
                                                "infra/envs/poc/idp/terraform.tfvars.json",
                                                "infra/envs/poc/apps/backend.hcl",
                                                "infra/envs/poc/apps/apps.tfplan"),
    "the infrastructure outputs document": ("infra/envs/poc/outputs.json",),
    "local settings and secrets": (".env", ".env.local", "tools/aws-project.local.env",
                                   "src/Gateway/appsettings.local.json"),
    "agent session state": (".claude/worktrees/x/README.md",),
}

OUTPUTS_DOCUMENT = "infra/envs/poc/outputs.json"
# The script that writes the outputs document, which a case reads for the path it writes to.
CELL_COMMON = "tools/scripts/cell-common.sh"


# --- Docker's .dockerignore rules ------------------------------------------------------------------
#
# https://docs.docker.com/build/concepts/context/#dockerignore-files: patterns follow Go's
# filepath.Match, with `**` matching any number of directories, zero included; the root of the
# context is both the working and the root directory, so `/a` and `a` are the same pattern; a
# pattern matches a path or a directory above it; lines starting `#` are comments and `!` makes an
# exception; and the last line that matches a path decides it. A bare `*.pem` therefore matches
# only at the root, which is why the patterns below the root are written `**/`.

def _pattern_regex(pattern: str) -> re.Pattern:
    out, i = "^", 0
    while i < len(pattern):
        c = pattern[i]
        if pattern.startswith("**/", i):
            out += "(?:.*/)?"
            i += 3
            continue
        if pattern.startswith("**", i):
            out += ".*"
            i += 2
            continue
        if c == "*":
            out += "[^/]*"
        elif c == "?":
            out += "[^/]"
        elif c == "[":
            end = pattern.index("]", i)
            body = pattern[i + 1:end]
            out += "[" + ("^" + body[1:] if body.startswith("^") else body) + "]"
            i = end
        else:
            out += re.escape(c)
        i += 1
    return re.compile(out + "$")


def read_dockerignore(text: str) -> list[tuple[bool, re.Pattern]]:
    rules = []
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        exception = line.startswith("!")
        line = line[1:].strip() if exception else line
        line = os.path.normpath(line).lstrip("/")
        rules.append((exception, _pattern_regex(line)))
    return rules


def excluded(rules: list[tuple[bool, re.Pattern]], path: str) -> bool:
    parts = path.split("/")
    candidates = ["/".join(parts[:n]) for n in range(1, len(parts) + 1)]
    verdict = False
    for exception, regex in rules:
        if any(regex.match(c) for c in candidates):
            verdict = not exception
    return verdict


# --- what the Dockerfiles say --------------------------------------------------------------------

def instructions(dockerfile: str) -> list[tuple[str, str]]:
    """(INSTRUCTION, arguments) for each instruction, continuation lines joined."""
    text = (ROOT / dockerfile).read_text()
    joined = re.sub(r"\\\n", " ", text)
    out = []
    for line in joined.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        keyword, _, rest = line.partition(" ")
        out.append((keyword.upper(), rest.strip()))
    return out


def copied_from_context(dockerfile: str) -> list[str]:
    """The context sources of each COPY that does not copy from a stage."""
    sources = []
    for keyword, rest in instructions(dockerfile):
        if keyword not in ("COPY", "ADD"):
            continue
        words = shlex.split(rest)
        if any(w.startswith("--from") for w in words):
            continue
        words = [w for w in words if not w.startswith("--")]
        sources.extend(words[:-1])
    return sources


def tracked(paths: list[str]) -> list[str]:
    return subprocess.run(["git", "-C", str(ROOT), "ls-files", "-z", "--", *paths],
                          capture_output=True, text=True, check=True).stdout.split("\0")[:-1]


def published_tree_steps(dockerfile: str) -> tuple[str, list[str]]:
    """The directory the final stage copies out of the build stage, and the build stage's RUN
    steps after its `dotnet publish`, in order."""
    steps = instructions(dockerfile)
    stages: list[tuple[str, list[tuple[str, str]]]] = []
    for keyword, rest in steps:
        if keyword == "FROM":
            name = rest.split()[-1] if " AS " in rest.upper() else ""
            stages.append((name.lower(), []))
        elif stages:
            stages[-1][1].append((keyword, rest))
    final = stages[-1][1]
    copy = next(rest for keyword, rest in final if keyword == "COPY" and "--from=" in rest)
    words = shlex.split(copy)
    stage = next(w for w in words if w.startswith("--from=")).split("=", 1)[1].lower()
    source = [w for w in words if not w.startswith("--")][0].rstrip("/")
    build = next(body for name, body in stages if name == stage)
    publish = next(i for i, (k, rest) in enumerate(build) if k == "RUN" and "dotnet publish" in rest)
    return source, [rest for k, rest in build[publish + 1:] if k == "RUN"]


# --- the cases -----------------------------------------------------------------------------------

class TheMatcherIsDockers(unittest.TestCase):
    """The matcher answers as Docker documents, so a green context case means what it says."""

    def test_documented_examples(self):
        rules = read_dockerignore("# comment\n*/temp*\n*/*/temp*\ntemp?\n**/*.go\n!keep.go\n/root\n")
        for path, want in (("somedir/temporary.txt", True), ("somedir/temp", True),
                           ("somedir/subdir/temporary.txt", True), ("temp", False),
                           ("tempa", True), ("a/b/c.go", True), ("x.go", True), ("keep.go", False),
                           ("root/file", True), ("a/root", False), ("comment", False)):
            with self.subTest(path=path):
                self.assertEqual(excluded(rules, path), want)

    def test_a_bare_pattern_matches_only_at_the_root(self):
        rules = read_dockerignore("*.pem\nbin\n")
        self.assertTrue(excluded(rules, "ca.pem"))
        self.assertFalse(excluded(rules, ".certs/ca.pem"))
        self.assertFalse(excluded(rules, "src/Gateway/bin/x.dll"))


class TheBuildContext(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        if not DOCKERIGNORE.is_file():
            raise AssertionError(f"{DOCKERIGNORE.relative_to(ROOT)} is missing: every image build "
                                 "sends the whole working tree, keys and host build output included")
        cls.rules = read_dockerignore(DOCKERIGNORE.read_text())

    def test_carries_nothing_the_first_box_names(self):
        for what, paths in MUST_NOT_SEND.items():
            for path in paths:
                with self.subTest(what=what, path=path):
                    self.assertTrue(excluded(self.rules, path),
                                    f"the build context would carry {path} ({what})")

    def test_carries_every_tracked_file_a_dockerfile_copies(self):
        dockerfiles = (*DOCKERFILES, *OTHER_OWNERS)
        for dockerfile in dockerfiles:
            sources = copied_from_context(dockerfile)
            self.assertTrue(sources, f"{dockerfile} copies nothing from the context")
            files = tracked(sources)
            self.assertTrue(files, f"no tracked file matches {dockerfile}'s COPY sources")
            for path in files:
                with self.subTest(dockerfile=dockerfile, path=path):
                    self.assertFalse(excluded(self.rules, path),
                                     f"{dockerfile} copies {path}, which the context would leave out")


class TheOutputsDocument(unittest.TestCase):

    def test_git_ignores_it(self):
        # check-ignore answers for a path whether or not it exists, and --no-index answers as the
        # ignore files read, even for a path some checkout has tracked by mistake.
        result = subprocess.run(["git", "-C", str(ROOT), "check-ignore", "--no-index", "-q",
                                 OUTPUTS_DOCUMENT], capture_output=True, text=True)
        self.assertEqual(result.returncode, 0,
                         f"git does not ignore {OUTPUTS_DOCUMENT}; one `git add .` commits it")

    def test_the_scripts_still_write_it_there(self):
        # A guard reads the thing it guards: the ignore line is only right while the scripts write
        # where it points.
        common = (ROOT / CELL_COMMON).read_text()
        self.assertIn('OUTPUTS_FILE="${REPO_ROOT}/' + OUTPUTS_DOCUMENT + '"', common)


class TheRuntimeUserReadsThePublishedTree(unittest.TestCase):

    def test_every_dockerfile_is_held_here_or_named_as_another_owners(self):
        found = [p for p in tracked(["*Dockerfile"]) if pathlib.PurePath(p).name == "Dockerfile"]
        self.assertEqual(sorted(found), sorted((*DOCKERFILES, *OTHER_OWNERS)))

    def test_owner_only_files_come_out_readable_by_everyone_and_writable_by_no_one_else(self):
        for dockerfile in DOCKERFILES:
            with self.subTest(dockerfile=dockerfile), tempfile.TemporaryDirectory() as tmp:
                source, runs = published_tree_steps(dockerfile)
                tree = pathlib.Path(tmp) / "app"
                (tree / "wwwroot" / "css").mkdir(parents=True)
                files = {"appsettings.json": 0o600, "Gateway.dll": 0o600, "Gateway": 0o700,
                         "wwwroot/css/site.css": 0o600}
                for name, mode in files.items():
                    (tree / name).write_text("x")
                    (tree / name).chmod(mode)
                for d in (tree / "wwwroot" / "css", tree / "wwwroot", tree):
                    d.chmod(0o700)
                self.assertTrue(runs, f"{dockerfile} runs nothing on {source} after its publish, "
                                      "so the final stage gets the workstation's modes")
                for run in runs:
                    command = re.sub(rf"(?<![\w/]){re.escape(source)}(?![\w/])", str(tree), run)
                    subprocess.run(["/bin/sh", "-c", command], check=True)
                for path in [tree, *tree.rglob("*")]:
                    mode = stat.S_IMODE(path.stat().st_mode)
                    rel = path.relative_to(tree)
                    self.assertEqual(mode & 0o004, 0o004, f"{dockerfile}: {rel} is {oct(mode)}")
                    self.assertEqual(mode & 0o022, 0, f"{dockerfile}: {rel} is {oct(mode)}")
                    if path.is_dir():
                        self.assertEqual(mode & 0o001, 0o001, f"{dockerfile}: {rel}/ is {oct(mode)}")
                self.assertEqual(stat.S_IMODE((tree / "Gateway").stat().st_mode) & 0o001, 0o001,
                                 f"{dockerfile}: the apphost lost its execute bit")
                self.assertEqual(stat.S_IMODE((tree / "appsettings.json").stat().st_mode) & 0o111, 0,
                                 f"{dockerfile}: a data file became executable")


if __name__ == "__main__":
    unittest.main()
