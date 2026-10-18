#!/usr/bin/env python3
"""Test companion for tools/main_checkout_guard.py (#114). Owner: integrator.

**What runs is the hook itself**, as Claude Code runs it: the tracked file, given a PreToolUse
event's JSON on standard input, answering with its exit code and standard error. Each case builds
the tool input a session's Bash call would send, so the cases are the commands themselves.

**Real git in a throwaway directory.** One fixture serves every case, because the hook only reads:
a repository whose first commit holds copies of the guard and of the tracked `.claude/settings.json`,
a linked worktree of it, a symbolic link to it, a second repository, and a directory that is in none.
HOME is the throwaway directory, so `~` names it, and git reads no system or global configuration.

**The settings entry runs as written.** Its command is read from this checkout's
`.claude/settings.json` and run through a shell with CLAUDE_PROJECT_DIR naming the fixture, as
Claude Code runs it (https://code.claude.com/docs/en/hooks), because the entry runs the guard only
when the file is there, and a mistyped path in it would otherwise be a guard that silently never
runs.

Run: python3 -I tools/main_checkout_guard_tests.py
"""

from __future__ import annotations

import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
GUARD = "tools/main_checkout_guard.py"
SETTINGS = ".claude/settings.json"

# The route the refusal must name, as #114's first acceptance criterion words it.
ROUTE = "git worktree add -b <branch> <path> origin/main"

# Each command #114's first box names, in the forms a session writes them. `git checkout` is there
# with a branch, a new branch and a commit; its path form is in PATH_CHECKOUTS below.
MOVES = (
    "git rebase origin/main",
    "git switch main",
    "git switch -c task/new origin/main",
    "git checkout task/linked",
    "git checkout -b task/new origin/main",
    "git checkout HEAD~1",
    "git reset --hard origin/main",
    "git reset HEAD~1",
    "git commit -m 'work in progress'",
    "git commit --amend --no-edit",
    "git merge task/linked",
    "git pull",
    "git pull --rebase origin main",
    "git cherry-pick 0123abc",
    "git revert HEAD",
    "git stash",
    "git stash push -m wip",
    "git stash pop",
)

# Git that reads, including the two names that share a prefix with a refused one: merge-base is not
# merge, and the stash's listing and showing move nothing.
READ_ONLY = (
    "git status",
    "git log --oneline -5 origin/main",
    "git diff origin/main...HEAD",
    "git show HEAD:README.md",
    "git fetch origin --tags",
    "git rev-parse HEAD",
    "git branch --show-current",
    "git worktree list",
    "git tag --merged origin/main",
    "git merge-base origin/main HEAD",
    "git stash list",
    "git stash show -p stash@{0}",
)

# `git checkout` with `--` restores files and moves no branch, whichever commit it reads them from.
PATH_CHECKOUTS = (
    "git checkout -- README.md",
    "git checkout HEAD -- README.md",
    "git checkout origin/main -- docs/agents/landing-work.md",
)


def settings_entry():
    """The tracked settings' PreToolUse matcher and hook that run the guard."""
    settings = json.loads((ROOT / SETTINGS).read_text())
    for group in settings.get("hooks", {}).get("PreToolUse", []):
        for hook in group.get("hooks", []):
            if GUARD in hook.get("command", ""):
                return group.get("matcher"), hook
    raise AssertionError(f"no PreToolUse hook in {SETTINGS} runs {GUARD}")


def selects(matcher, tool):
    """Whether a matcher selects a tool, by the matcher table in https://code.claude.com/docs/en/hooks:
    "*", "" or none matches every tool; letters, digits, _, -, spaces, commas and | are exact names
    separated by | or a comma; anything else is an unanchored JavaScript regular expression."""
    if matcher in (None, "", "*"):
        return True
    if re.fullmatch(r"[A-Za-z0-9_\- ,|]+", matcher):
        return tool in [name.strip() for name in re.split(r"[|,]", matcher)]
    return re.search(matcher, tool) is not None


def git(cwd, *args, env):
    return subprocess.run(["git", *args], cwd=cwd, env=env, check=True, text=True,
                          capture_output=True).stdout.strip()


class Fixture:
    """The repositories every case runs against, built once."""

    def __init__(self):
        self.directory = tempfile.TemporaryDirectory()
        top = pathlib.Path(os.path.realpath(self.directory.name))
        self.top = top
        config = top / "gitconfig"
        config.write_text("[user]\n\tname = Guard test\n\temail = test@example.test\n"
                          "[init]\n\tdefaultBranch = main\n[gc]\n\tauto = 0\n"
                          "[maintenance]\n\tauto = false\n")
        # The hook runs with the session's PATH, which is where it finds git; nothing else is
        # inherited.
        self.env = {"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "HOME": str(top), "LC_ALL": "C",
                    "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": str(config)}

        self.main = top / "repo"
        for name in (GUARD, SETTINGS):
            target = self.main / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / name, target)
        (self.main / "README.md").write_text("fixture\n")
        git(self.main, "init", "-q", env=self.env)
        git(self.main, "add", ".", env=self.env)
        git(self.main, "commit", "-qm", "fixture", env=self.env)
        self.linked = top / "linked"
        git(self.main, "worktree", "add", "-q", "-b", "task/linked", str(self.linked), env=self.env)

        self.other = top / "other"
        self.other.mkdir()
        (self.other / "README.md").write_text("another repository\n")
        git(self.other, "init", "-q", env=self.env)
        git(self.other, "add", ".", env=self.env)
        git(self.other, "commit", "-qm", "other", env=self.env)

        self.plain = top / "plain"
        self.plain.mkdir()
        self.alias = top / "alias"
        self.alias.symlink_to(self.main)

    def event(self, command, cwd):
        """A PreToolUse event for one Bash call, in the shape https://code.claude.com/docs/en/hooks gives."""
        return json.dumps({
            "session_id": "guard-test",
            "transcript_path": str(self.top / "transcript.jsonl"),
            "cwd": str(cwd),
            "permission_mode": "default",
            "hook_event_name": "PreToolUse",
            "tool_name": "Bash",
            "tool_input": {"command": command, "description": "a case", "timeout": 120000,
                           "run_in_background": False},
            "tool_use_id": "toolu_guard_test",
        })

    def hook(self, command, cwd):
        """Run the guard the fixture's main worktree holds on one Bash call; (exit code, stderr)."""
        # Started from the plain directory, so the guard reads where the command runs from the
        # event, never from its own working directory.
        result = subprocess.run([sys.executable, "-I", str(self.main / GUARD)], input=self.event(command, cwd),
                                cwd=self.plain, env=self.env, text=True, capture_output=True, timeout=60)
        return result.returncode, result.stderr

    def entry(self, shell, project, command, cwd):
        """Run the tracked settings entry's command as Claude Code does, for a session started in project."""
        _, hook = settings_entry()
        result = subprocess.run([shell, "-c", hook["command"]], input=self.event(command, cwd), cwd=self.plain,
                                env={**self.env, "CLAUDE_PROJECT_DIR": str(project)}, text=True,
                                capture_output=True, timeout=60)
        return result.returncode, result.stderr

    def close(self):
        self.directory.cleanup()


class GuardCase(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.fx = Fixture()

    @classmethod
    def tearDownClass(cls):
        cls.fx.close()

    def assertRefused(self, command, cwd):
        status, stderr = self.fx.hook(command, cwd)
        self.assertEqual(2, status, f"{command!r} in {cwd} was let through: {stderr}")
        self.assertIn(ROUTE, stderr)

    def assertPasses(self, command, cwd):
        status, stderr = self.fx.hook(command, cwd)
        self.assertEqual(0, status, f"{command!r} in {cwd} was refused: {stderr}")
        self.assertEqual("", stderr)


class Refused(GuardCase):
    """#114's first box: the branch-moving commands, in the main worktree, by either route."""

    def test_each_branch_moving_command_is_refused_in_the_main_checkout_and_told_the_route(self):
        for command in MOVES:
            with self.subTest(command=command):
                self.assertRefused(command, self.fx.main)

    def test_a_subdirectory_of_the_main_checkout_is_the_main_checkout(self):
        self.assertRefused("git commit -m 'work in progress'", self.fx.main / "tools")

    def test_a_symbolic_link_to_the_main_checkout_is_the_main_checkout(self):
        self.assertRefused("git commit -m 'work in progress'", self.fx.alias)
        self.assertRefused(f"git -C {self.fx.alias} pull", self.fx.plain)

    def test_git_C_reaches_the_main_checkout_from_anywhere(self):
        for command in MOVES:
            with self.subTest(command=command, cwd="plain"):
                self.assertRefused(command.replace("git ", f"git -C {self.fx.main} ", 1), self.fx.plain)
        with self.subTest(cwd="linked"):
            self.assertRefused(f"git -C {self.fx.main} commit -m 'work'", self.fx.linked)
        with self.subTest(cwd="a subdirectory", path="relative"):
            self.assertRefused("git -C .. reset --hard origin/main", self.fx.main / "tools")
        with self.subTest(path="two -C, each relative to the last"):
            self.assertRefused(f"git -C {self.fx.top} -C repo rebase origin/main", self.fx.plain)

    def test_cd_reaches_the_main_checkout(self):
        for command in (f"cd {self.fx.main} && git rebase origin/main",
                        f"cd {self.fx.linked}; cd ../repo; git pull",
                        "cd ~/repo\ngit commit -m 'work'"):
            with self.subTest(command=command):
                self.assertRefused(command, self.fx.plain)

    def test_the_shell_forms_a_session_writes_still_reach_git(self):
        main = self.fx.main
        for command, cwd in (
            ("GIT_EDITOR=true git rebase --continue", main),
            ("env GIT_EDITOR=true git merge --continue", main),
            ("command git commit -m 'work'", main),
            ("/usr/bin/git commit -m 'work'", main),
            ("git -c user.name=someone commit -m 'work'", main),
            ("true && git reset --hard origin/main", main),
            ("git stash list | head -1 && git stash pop", main),
            ("if git diff --quiet; then git pull; fi", main),
            ('for f in a; do git commit -m "$f"; done', main),
            ("{ git fetch origin; git merge origin/main; }", main),
            ("git \\\n  commit -m 'work'", main),
            # The form a session writes a commit message in, apostrophe and parenthesis included. zsh,
            # which the Bash tool runs here, parses it; the bash 3.2 macOS ships does not.
            ("git commit -m \"$(cat <<'EOF'\nDon't (break) this\nEOF\n)\"", main),
            ('echo "$(git stash)"', main),
            ("echo `git stash`", main),
            (f"(cd {main} && git commit -m 'work')", self.fx.plain),
            (f"wt={main}; git -C \"$wt\" commit -m 'work'", self.fx.plain),
            (f"wt={main}; git -C \"${{wt}}\" commit -m 'work'", self.fx.plain),
            # A redirection may come before the command, and its file descriptor is not a word.
            (">/dev/null 2>&1 git pull", main),
            # What follows a quote, a substitution or a here-document is a command again.
            ("printf $'it\\'s\\n'; git commit -m 'work'", main),
            (f"echo \"$( (cd {self.fx.linked} && git status) )\" && git -C {main} pull", self.fx.plain),
            ("cat > notes.txt <<'EOF'\nDon't stop\nEOF\ngit commit -am 'notes'", main),
            ("cat <<-EOF\n\tDon't stop\n\tEOF\ngit commit -am 'notes'", main),
            ("gh pr comment 1 --body \"$(cat <<'EOF'\nIt's done (mostly.\nEOF\n)\" && git commit -m 'x'", main),
        ):
            with self.subTest(command=command):
                self.assertRefused(command, cwd)


class Passes(GuardCase):
    """#114's second box: what the guard lets through."""

    def test_the_same_commands_pass_in_a_linked_worktree(self):
        for command in MOVES:
            with self.subTest(command=command):
                self.assertPasses(command, self.fx.linked)

    def test_git_C_or_cd_into_a_linked_worktree_passes_from_the_main_checkout(self):
        for command in MOVES:
            with self.subTest(command=command):
                self.assertPasses(command.replace("git ", f"git -C {self.fx.linked} ", 1), self.fx.main)
        for command in ("git -C ../linked commit -m 'work'", f"cd {self.fx.linked} && git commit -m 'work'",
                        "cd ../linked; git rebase origin/main", f"pushd {self.fx.linked} && git commit -m 'work'"):
            with self.subTest(command=command):
                self.assertPasses(command, self.fx.main)

    def test_another_repositorys_checkout_and_no_repository_pass(self):
        # The guard keeps this repository's main checkout; a scratch repository or a clone made
        # for a mutation run is a main worktree of its own, and is nobody else's checkout.
        for command in MOVES:
            with self.subTest(command=command, cwd="another repository"):
                self.assertPasses(command, self.fx.other)
            with self.subTest(command=command, cwd="no repository"):
                self.assertPasses(command, self.fx.plain)
        with self.subTest(command="git -C <another repository> commit, from the main checkout"):
            self.assertPasses(f"git -C {self.fx.other} commit -m 'work'", self.fx.main)

    def test_text_that_names_a_command_without_running_it_passes(self):
        for command in (
            "echo 'git commit -m work'",
            "echo git commit",
            "echo 'done; git reset --hard origin/main'",
            "gh pr comment 1 --body \"fixed && git commit -m x\"",
            "gh pr comment 1 --body \"say \\\"stop\\\"; then git commit -m x\"",
            "echo run this \\; git commit -m x",
            "grep -n 'git rebase' docs/agents/landing-work.md",
            "gh pr comment 1 --body 'git reset --hard origin/main'",
            "git status  # look first; git reset --hard comes later",
            "gh issue comment 1 --body-file - <<'EOF'\ngit reset --hard origin/main\nEOF",
            "gh issue comment 1 --body-file - <<-EOF\n\tgit pull\n\tEOF",
            "gh pr comment 1 --body \"$(cat <<'EOF'\nthen:\ngit reset --hard origin/main\nEOF\n)\"",
        ):
            with self.subTest(command=command):
                self.assertPasses(command, self.fx.main)

    def test_a_cd_inside_a_subshell_stays_there(self):
        for command in (f"(cd {self.fx.main} && git status); git commit -m 'work'",
                        f"(cd {self.fx.main}); git pull"):
            with self.subTest(command=command):
                self.assertPasses(command, self.fx.plain)

    def test_file_edits_in_the_main_checkout_pass(self):
        # The editing tools never reach the guard (Settings below); these are edits made through Bash.
        for command in ("printf 'x\\n' > notes.txt && sed -i '' 's/x/y/' notes.txt",
                        "mkdir -p scratch && cp README.md scratch/ && rm -r scratch",
                        "python3 - <<'EOF'\nopen('notes.txt', 'w').write('git commit')\nEOF"):
            with self.subTest(command=command):
                self.assertPasses(command, self.fx.main)

    def test_read_only_git_passes_in_the_main_checkout(self):
        for command in READ_ONLY:
            with self.subTest(command=command):
                self.assertPasses(command, self.fx.main)

    def test_a_new_worktree_passes_in_the_main_checkout(self):
        for command in ("git worktree add -b task/new ../spa-new origin/main",
                        "git worktree add --detach ../spa-land origin/main"):
            with self.subTest(command=command):
                self.assertPasses(command, self.fx.main)

    def test_a_path_checkout_passes_in_the_main_checkout(self):
        for command in PATH_CHECKOUTS:
            with self.subTest(command=command):
                self.assertPasses(command, self.fx.main)


class Settings(GuardCase):
    """The tracked .claude/settings.json entry, which is what makes Claude Code run the guard at all."""

    def test_the_entry_runs_on_bash_calls_and_no_other_tool(self):
        matcher, hook = settings_entry()
        self.assertEqual("command", hook.get("type"))
        self.assertTrue(selects(matcher, "Bash"), f"the matcher {matcher!r} does not select Bash")
        for tool in ("Edit", "Write", "MultiEdit", "NotebookEdit", "Read"):
            with self.subTest(tool=tool):
                self.assertFalse(selects(matcher, tool), f"the matcher {matcher!r} selects {tool}")

    def test_the_entry_as_written_refuses_in_the_main_checkout_and_passes_elsewhere(self):
        _, hook = settings_entry()
        # Without `args` Claude Code runs the command through a shell: sh -c, by the reference's
        # text, and bash by its `shell` field's default, so both are run here.
        self.assertNotIn("args", hook, "the entry is meant for a shell; these cases run it through one")
        for shell in ("/bin/sh", "/bin/bash"):
            with self.subTest(shell=shell, cwd="main"):
                status, stderr = self.fx.entry(shell, self.fx.main, "git rebase origin/main", self.fx.main)
                self.assertEqual(2, status, stderr)
                self.assertIn(ROUTE, stderr)
            with self.subTest(shell=shell, cwd="linked"):
                self.assertEqual((0, ""), self.fx.entry(shell, self.fx.linked, "git rebase origin/main",
                                                        self.fx.linked))
            # A session started in a linked worktree runs that worktree's copy, which guards the same
            # main checkout through the common directory they share.
            with self.subTest(shell=shell, project="linked", cwd="main"):
                status, stderr = self.fx.entry(shell, self.fx.linked, "git rebase origin/main", self.fx.main)
                self.assertEqual(2, status, stderr)

    def test_a_project_without_the_guard_runs_every_call(self):
        # python3 exits 2 on a missing script, which would refuse every Bash call in a session whose
        # project lacks the file; the entry runs the guard only when it is there.
        for shell in ("/bin/sh", "/bin/bash"):
            with self.subTest(shell=shell):
                self.assertEqual((0, ""), self.fx.entry(shell, self.fx.plain, "git rebase origin/main",
                                                        self.fx.main))


if __name__ == "__main__":
    unittest.main()
