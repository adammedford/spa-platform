#!/usr/bin/env python3
"""Keeps branch work out of the repository's main checkout. Owner: integrator.

A Claude Code PreToolUse hook, run before every Bash call by the entry in the tracked
`.claude/settings.json` (#114). The main checkout is the clone's own working tree, the worktree whose
git directory is the common directory, and the sessions running at once share it: its branch is
whichever one a session last left it on. On 2026-10-18 a session ran `git rebase origin/main` there,
taking it for its own branch, after another had switched it to `ws1/certs-domain-tag` and committed
on it; the rebase did nothing only because that branch already held origin/main. So in that checkout
this refuses a Bash call that runs `git rebase`, `switch`, `checkout <branch-or-commit>`, `reset`,
`commit`, `merge`, `pull`, `cherry-pick`, `revert` or `stash`, and names the route instead: a
worktree of the session's own, as docs/agents/landing-work.md -> "Take a task" says.

**What passes.** The same commands in a linked worktree; git that reads, `git stash list` and
`git stash show` among it; `git worktree add`; `git checkout` with `--`, which restores files from
whatever commit it names and moves no branch; and every other command, file edits included. The
checkout guarded is the main worktree of the repository this file is tracked in, so another
repository's, a scratch one or a clone made for a mutation run, is left alone.

**How it reads a command.** Claude Code hands the hook the command as text, and as `cwd` the
directory the session is in, which follows its `cd`s (https://code.claude.com/docs/en/hooks). The
hook reads the text as a shell would, as far as finding each git invocation and where it runs needs:
separators, quoting, `cd`, `pushd` and `~`, a variable the command itself assigned, a subshell's own
directory, `$(...)` and backticks, here-document bodies as text rather than commands, assignment and
`env` prefixes, and git's own options before the subcommand, `-C` among them
(https://git-scm.com/docs/git#_options). Whether a directory is the main worktree is git's answer:
its git directory and its common directory are one directory
(https://git-scm.com/docs/git-rev-parse#Documentation/git-rev-parse.txt---git-common-dir).

**What it does not read**, which makes it a tripwire for a session that mistook the checkout for its
own and not a sandbox: a command run through another layer -- `sh -c`, `eval`, a script, a shell or
git alias -- a directory only the running shell knows, such as `"$(pwd)"`, and a repository named by
`--git-dir`, `--work-tree` or `GIT_DIR`. What it cannot read it lets through. It binds Claude Code
sessions, a sub-agent's Bash calls included, and never a person's terminal.

**Its answer** is Claude Code's PreToolUse contract: exit 2 blocks the call and hands standard error
to the session as the reason, and exit 0 lets it run. Any other exit, a crash or a missing git, also
lets the call run, which is Claude Code's default for a failing hook, so a broken guard never stops
a session's work. The settings entry runs this file only when it exists, for the same reason:
python3 exits 2 on a missing script, which Claude Code would read as a refusal of every Bash call.
That also makes a mistyped path in the entry silent, which is why the test companion runs the entry
as written.

Written for Python 3.9, the python3 Apple's command line tools install, which is the one a session on
a Mac finds first.

Test companion: tools/main_checkout_guard_tests.py.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys

ROUTE = "git worktree add -b <branch> <path> origin/main"

REFUSAL = """\
main-checkout guard: refused `git {subcommand}` in {directory}.
That is this repository's main checkout. The sessions running at once share it, and its branch is
whichever one a session last left it on, so a commit, rebase or switch there acts on another
session's work (#114). Do branch work in a worktree of your own, and run the command there:

    {route}

Here, read-only git, `git worktree add`, `git checkout -- <path>` and file edits still run.
docs/agents/landing-work.md -> "Take a task" says why.
"""

# The subcommands #114 names that move the checkout's branch or rewrite its history whatever follows
# them. `checkout` and `stash` are decided by what follows, in moves_the_checkout().
MOVES = {"rebase", "switch", "reset", "commit", "merge", "pull", "cherry-pick", "revert"}

# git's options before the subcommand that take the next word as their value, so that word is not
# the subcommand (https://git-scm.com/docs/git#_options; git 2.54 takes each in this separate form).
VALUED = {"-C", "-c", "--git-dir", "--work-tree", "--namespace", "--config-env", "--attr-source"}

# Longest first, so `&&` is read before `&` and `<<-` before `<<`.
OPERATORS = sorted({"<<<", "<<-", "&>>", "&&", "||", ";;", "|&", "&>", ">>", ">&", "<&", ">|", "<>", "<<",
                    ";", "&", "|", "(", ")", "<", ">", "\n"}, key=len, reverse=True)
REDIRECTIONS = {"<<<", "<<-", "&>>", "&>", ">>", ">&", "<&", ">|", "<>", "<<", "<", ">"}
HEREDOCS = {"<<", "<<-"}

# Words after which the shell runs the next word as the command: reserved words that open or go on
# with a compound command, and builtins and utilities that run their arguments as one.
PREFIXES = {"!", "{", "if", "then", "else", "elif", "do", "while", "until", "time", "command", "builtin",
            "exec", "nohup", "noglob", "env"}

NAME = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
ASSIGNMENT = re.compile(r"([A-Za-z_][A-Za-z0-9_]*)=")

# A part of a word whose value only the running shell knows.
UNKNOWN = ("unknown",)


class Word:
    """One shell word: its literal text, the variables it names and the commands it substitutes."""

    def __init__(self, parts):
        self.parts = []
        for part in parts:
            if isinstance(part, str) and self.parts and isinstance(self.parts[-1], str):
                self.parts[-1] += part
            else:
                self.parts.append(part)


class Lexer:
    """Reads shell text into words and operators, skipping here-document bodies."""

    def __init__(self, text):
        self.text = text
        self.i = 0
        self.heredocs = []

    def at(self, s, j=None):
        return self.text.startswith(s, self.i if j is None else j)

    def operator(self, j=None):
        return next((op for op in OPERATORS if self.at(op, j)), None)

    def tokens(self, nested=False):
        """The tokens up to the end of the text or, nested, up to the `)` that closes a `$(`."""
        out = []
        word = None
        delimiter = None

        def finish():
            nonlocal word, delimiter
            if word is None:
                return
            out.append(Word(word))
            if delimiter is not None:
                text = "".join(part for part in word if isinstance(part, str))
                self.heredocs.append((text, delimiter == "<<-"))
                delimiter = None
            word = None

        def add(*parts):
            nonlocal word
            if word is None:
                word = []
            word.extend(parts)

        while self.i < len(self.text):
            c = self.text[self.i]
            op = self.operator()
            if c in " \t":
                finish()
                self.i += 1
            elif c == "\\":
                if not self.at("\\\n"):
                    add(self.text[self.i + 1:self.i + 2])
                self.i += 2
            elif c == "#" and word is None:
                while self.i < len(self.text) and self.text[self.i] != "\n":
                    self.i += 1
            elif c == "'":
                end = self.text.find("'", self.i + 1)
                end = len(self.text) if end < 0 else end
                add(self.text[self.i + 1:end])
                self.i = end + 1
            elif c == '"':
                add(*self.double_quoted())
            elif c in "$`":
                add(self.expansion())
            elif c == "~" and word is None and self.ends_word(self.i + 1):
                add(("var", "HOME"))
                self.i += 1
            elif op:
                # The 2 in `2>/dev/null` names a file descriptor, not an argument.
                if op[0] in "<>" and word and all(isinstance(p, str) for p in word) and "".join(word).isdigit():
                    word = None
                finish()
                self.i += len(op)
                if op == ")" and nested:
                    return out
                out.append(op)
                if op in HEREDOCS:
                    delimiter = op
                if op == "\n":
                    self.skip_heredoc_bodies()
            else:
                add(c)
                self.i += 1
        finish()
        return out

    def ends_word(self, j):
        return j >= len(self.text) or self.text[j] in " \t/" or self.operator(j) is not None

    def skip_heredoc_bodies(self):
        """At the start of the line after a here-document's operator: past each body it opened."""
        for delimiter, strip_tabs in self.heredocs:
            while self.i < len(self.text):
                end = self.text.find("\n", self.i)
                end = len(self.text) if end < 0 else end
                line = self.text[self.i:end]
                self.i = end + 1
                if (line.lstrip("\t") if strip_tabs else line) == delimiter:
                    break
        self.heredocs = []

    def double_quoted(self):
        parts = [""]
        self.i += 1
        while self.i < len(self.text) and self.text[self.i] != '"':
            c = self.text[self.i]
            if c == "\\" and self.text[self.i + 1:self.i + 2] in ('"', "\\", "$", "`", "\n"):
                parts.append(self.text[self.i + 1].replace("\n", ""))
                self.i += 2
            elif c in "$`":
                parts.append(self.expansion())
            else:
                parts.append(c)
                self.i += 1
        self.i += 1
        return parts

    def expansion(self):
        """The expansion at a `$` or a backtick: a variable, a substituted command, or UNKNOWN."""
        if self.at("`"):
            return self.backticks()
        if self.at("$("):
            self.i += 2
            return ("sub", self.tokens(nested=True))
        if self.at("${"):
            start = self.i + 2
            self.i += 1
            self.skip_balanced("{", "}")
            content = self.text[start:self.i - 1]
            return ("var", content) if NAME.fullmatch(content) else UNKNOWN
        if self.at("$'"):
            j = self.i + 2
            while j < len(self.text) and self.text[j] != "'":
                j += 2 if self.text[j] == "\\" else 1
            self.i = j + 1
            return UNKNOWN
        name = NAME.match(self.text, self.i + 1)
        if name:
            self.i = name.end()
            return ("var", name.group())
        self.i += 1
        return "$"

    def backticks(self):
        j = self.i + 1
        while j < len(self.text) and self.text[j] != "`":
            j += 2 if self.text[j] == "\\" else 1
        inner = self.text[self.i + 1:j]
        self.i = j + 1
        return ("sub", Lexer(inner).tokens())

    def skip_balanced(self, opening, closing):
        depth = 0
        while self.i < len(self.text):
            c = self.text[self.i]
            self.i += 1
            if c == opening:
                depth += 1
            elif c == closing:
                depth -= 1
                if depth == 0:
                    return


class Shell:
    """What the reading knows of the shell a command runs in: its directory and its variables."""

    def __init__(self, cwd, variables=None):
        self.cwd = cwd
        self.variables = dict(variables or {})

    def copy(self):
        return Shell(self.cwd, self.variables)

    def resolve(self, word):
        """The word's text, or None when part of it is something only the running shell knows."""
        text = []
        for part in word.parts:
            if isinstance(part, str):
                text.append(part)
                continue
            value = None
            if part[0] == "var":
                value = self.variables.get(part[1], os.environ.get(part[1]))
            if value is None:
                return None
            text.append(value)
        return "".join(text)


def within(directory, path):
    """path, read from directory: None when it cannot be known."""
    if path is None:
        return None
    if os.path.isabs(path):
        return os.path.normpath(path)
    if directory is None:
        return None
    return os.path.normpath(os.path.join(directory, path))


def git_calls(tokens, shell):
    """Each git invocation the tokens hold: (directory or None, subcommand or None, its arguments)."""
    outer = []
    command = []
    target = False
    for token in tokens + ["\n"]:
        if isinstance(token, Word):
            for part in token.parts:
                if isinstance(part, tuple) and part[0] == "sub":
                    yield from git_calls(part[1], shell.copy())
            if target:
                target = False
            else:
                command.append(token)
            continue
        if token in REDIRECTIONS:
            target = True
            continue
        if command:
            yield from simple_command(command, shell)
            command = []
        if token == "(":
            outer.append(shell)
            shell = shell.copy()
        elif token == ")" and outer:
            shell = outer.pop()


def simple_command(words, shell):
    i = 0
    assigned = {}
    while i < len(words) and isinstance(words[i].parts[0], str) and ASSIGNMENT.match(words[i].parts[0]):
        name = ASSIGNMENT.match(words[i].parts[0]).group(1)
        value = shell.resolve(words[i])
        assigned[name] = None if value is None else value[len(name) + 1:]
        i += 1
    if i == len(words):
        # Assignments alone set the shell's variables; before a command they are its environment.
        shell.variables.update(assigned)
        return
    values = [shell.resolve(word) for word in words[i:]]
    while values and values[0] in PREFIXES:
        if values.pop(0) == "env":
            while values and values[0] is not None and (values[0].startswith("-") or ASSIGNMENT.match(values[0])):
                values.pop(0)
    if not values:
        return
    name, args = values[0], values[1:]
    if name in ("cd", "pushd"):
        shell.cwd = within(shell.cwd, args[0] if args else os.environ.get("HOME"))
    elif name is not None and os.path.basename(name) == "git":
        yield git_call(shell.cwd, args)


def git_call(directory, args):
    i = 0
    while i < len(args) and args[i] is not None and args[i].startswith("-"):
        if args[i] == "-C":
            directory = within(directory, args[i + 1] if i + 1 < len(args) else None)
        i += 2 if args[i] in VALUED else 1
    subcommand = args[i] if i < len(args) else None
    return directory, subcommand, args[i + 1:]


def moves_the_checkout(subcommand, args):
    if subcommand in MOVES:
        return True
    if subcommand == "checkout":
        return "--" not in args
    if subcommand == "stash":
        return not args or args[0] not in ("list", "show")
    return False


def git_dirs(directory):
    """The directory's git directory and common directory, or None outside a repository. git prints
    them "absolute and canonical", symbolic links resolved, so they compare as text:
    https://git-scm.com/docs/git-rev-parse#Documentation/git-rev-parse.txt---path-formatabsoluterelative"""
    try:
        probe = subprocess.run(["git", "-C", directory, "rev-parse", "--path-format=absolute", "--git-dir",
                                "--git-common-dir"], capture_output=True, text=True)
    except OSError:
        return None
    lines = probe.stdout.splitlines()
    if probe.returncode != 0 or len(lines) != 2:
        return None
    return lines[0], lines[1]


def main():
    try:
        event = json.load(sys.stdin)
        command = event["tool_input"]["command"]
    except (ValueError, KeyError, TypeError):
        return 0
    if not isinstance(command, str):
        return 0
    guarded = None
    for directory, subcommand, args in git_calls(Lexer(command).tokens(), Shell(event.get("cwd"))):
        if directory is None or not moves_the_checkout(subcommand, args):
            continue
        if guarded is None:
            own = git_dirs(os.path.dirname(os.path.abspath(__file__)))
            if own is None:
                return 0
            guarded = own[1]
        found = git_dirs(directory)
        if found and found[0] == found[1] == guarded:
            print(REFUSAL.format(subcommand=subcommand, directory=directory, route=ROUTE), end="",
                  file=sys.stderr)
            return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
