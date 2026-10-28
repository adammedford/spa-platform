#!/usr/bin/env python3
"""Keeps branch work, and changes to its files and index, out of the repository's main checkout.
Owner: integrator.

A Claude Code PreToolUse hook (#114, #200). The entry in the tracked `.claude/settings.json` runs it
before each Bash call of a session started in a checkout that holds both files. The main checkout is
the clone's own working tree, the worktree whose git directory is the common directory, and the sessions
running at once share it: its branch is whichever one a session last left it on. On 2026-10-18 a
session ran `git rebase origin/main` there, taking it for its own branch, after another had switched
it to `ws1/certs-domain-tag` and committed on it; the rebase did nothing only because that branch
already held origin/main. On 2026-10-20 a smoke test ran `git rm` there and deleted a tracked test
file (#187, #200). So in that checkout this refuses a Bash call that runs `git rebase`, `switch`,
`checkout` in every form but one, `reset`, `commit`, `merge`, `pull`, `cherry-pick`, `revert`, `am`
or `stash`, which move its branch or rewrite its history, and `git rm`, `mv`, `add`, `restore`,
`clean` or `apply`, which change its files or index, dry runs and `apply --check` and `--stat`
included (#226 Q16 to Q18). It names the route instead: a worktree of the session's own, as
docs/agents/landing-work.md -> "Take a task" says.

**The one merge it admits** is the fast-forward of a clean `main` to what has landed (#226 Q15,
Q19, Q20): `git merge` with exactly the arguments `--ff-only` and `origin/main`, in either order,
with `--quiet` or `-q` allowed once beside them (#468) and no git option before the subcommand but
`-C`, while `git symbolic-ref -q HEAD` is `refs/heads/main` and
`git status --porcelain --untracked-files=no` prints nothing. Untracked files are allowed: they are
Adam's, and `--ff-only` will not overwrite one. Every other merge, ref or extra option is refused as
before, and so is `git pull`, which reads the configured upstream and merge settings rather than
one named ref; a session runs `git fetch origin` and then the merge.

**The one restore it admits** is one that changes no content once `main` is fast-forwarded (#376):
on 2026-10-23 the checkout was behind with two tracked files changed to exactly origin/main's
content, and the guard refused the restore that would have let the fast-forward run. It admits
`git restore <paths>` with no option but `--`, and `git checkout -- <paths>`, which restore the
working tree from the index, when HEAD is `main` (off it no fast-forward follows) and each path,
read from the directory git runs in, is a regular file at origin/main whose working-tree bytes are
that blob's (`git hash-object --no-filters`) and has no staged change, so the index holds HEAD's
blob. A restore with `--staged`, `--source` or any other
option, `git checkout <source> -- <paths>`, no path, a directory, a glob or a pathspec with magic
is refused as before, and so is one whose paths fail the test, with a line naming the first path
that does. The guard reads the files before the call runs, so a restore and the fast-forward after
it are two calls: in one, the merge is refused while the file still shows as changed. What it reads
can change before the restore runs, as for the fast-forward below.

**What passes.** The same commands in a linked worktree; git that reads, `git stash list` and
`git stash show` among it; `git fetch`; `git worktree add`; and every other command, file edits
included. Left out on purpose, as plumbing and ref commands a confused session does not type
(#226 Q18): `update-index`, `read-tree`, `checkout-index`, `update-ref`, `branch -f` and `-D`,
`worktree remove`, `sparse-checkout` and `submodule update`. The checkout guarded is the main
worktree of the repository this file is tracked in, so another repository's, a scratch one or a
clone made for a mutation run, is left alone.

**How it reads a command.** Claude Code hands the hook the command as text, and as `cwd` the
directory the session is in, which follows its `cd`s (https://code.claude.com/docs/en/hooks). The
hook reads the text as a shell would, as far as finding each git invocation and where it runs needs:
separators, quoting, `cd`, `pushd` and `~`, a subshell's own directory, `$(...)` and backticks,
here-document bodies as text rather than commands, redirections, assignment and `env` prefixes and
the other words that run the word after them, and git's own options before the subcommand, `-C`
among them (https://git-scm.com/docs/git#_options). A variable is what the command itself assigned,
`$PWD` is the directory the reading has reached, and any other variable is what the hook's own
environment holds, so one the session's shell holds differently is read wrong. Whether a directory
is the main worktree is git's answer: its git directory and its common directory are one directory
(https://git-scm.com/docs/git-rev-parse#Documentation/git-rev-parse.txt---git-common-dir).

**What it does not read**, which makes it a tripwire for a session that mistook the checkout for its
own and not a sandbox: a command run through another layer -- `sh -c`, `eval`, a script, a shell or
git alias -- a directory only the running shell knows, such as `"$(pwd)"`, and a repository named by
`--git-dir`, `--work-tree` or `GIT_DIR`. What it cannot read it lets through. It reads the main
checkout's HEAD and tracked files when the hook runs, with no lock, so they can change before the
fast-forward or the restore does: another session can switch the checkout, or the same call can edit
a file before its merge or restore (#226 Q21). `--ff-only` still refuses a branch that has diverged and a change it would
overwrite, so the merge cannot take a commit. It binds Claude Code sessions, a sub-agent's Bash calls
included, and never a person's terminal.

**Its answer** is Claude Code's PreToolUse contract: exit 2 blocks the call and hands standard error
to the session as the reason, and exit 0 lets it run. Any other exit, a crash or a missing git, also
lets the call run, which is Claude Code's default for a failing hook, so a broken guard never stops
a session's work. The settings entry runs this file only when it exists, for the same reason:
python3 exits 2 on a missing script, which Claude Code would read as a refusal of every Bash call.
That also makes a mistyped path in the entry silent, which is why the test companion runs the entry
as written.

Written to run on Python 3.9, the python3 Apple's command line tools install on a Mac.

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
{reason}That is this repository's main checkout. The sessions running at once share it, and its
branch is whichever one a session last left it on, so a commit, rebase or switch there acts on
another session's work, and an rm, add or restore changes files another session has in hand (#114, #200).
Do branch work in a worktree of your own, and run the command there:

    {route}

Here, read-only git, `git worktree add`, file edits, and `git fetch origin` followed by
`git merge --ff-only origin/main` (`--quiet` allowed) on a clean `main` still run.
docs/agents/landing-work.md -> "Take a task" says why.
"""

# The line a refused fast-forward adds, naming the condition of a clean `main` that failed (#226 Q22).
NOT_MAIN = "The fast-forward was refused because HEAD is not `main`.\n"
CHANGED = "The fast-forward was refused because tracked files have changes, or git could not list them.\n"

# The lines a refused restore of the admitted form adds, naming the path that failed (#376).
NOT_LANDED = "The restore was refused because `{path}` does not match its blob at origin/main.\n"
ABSENT = "The restore was refused because `{path}` is not a file at origin/main, or git could not read it.\n"
STAGED = "The restore was refused because `{path}` has a staged change, or git could not read the index.\n"
RESTORE_NOT_MAIN = "The restore was refused because HEAD is not `main`.\n"

# The subcommands refused whatever follows them. Most forms of #114's move the branch or rewrite
# history, and that ticket refused the few that do not, such as `git reset -- <path>`, with the rest;
# `am` commits on the branch as `commit` does (#226 Q18). `checkout` joins them because its path form
# overwrites files and the index as `restore` does (Q17). `merge` is refused but for the one
# fast-forward, in refusal(), and `stash` is decided by what follows it.
MOVES = {"rebase", "switch", "checkout", "reset", "commit", "merge", "pull", "cherry-pick", "revert", "am"}

# The subcommands #200 refuses whatever follows them, because they change the checkout's files or
# index; their dry runs too, which belong in the session's worktree like the real run (#226 Q16).
CHANGES = {"rm", "mv", "add", "restore", "clean", "apply"}

# The one merge admitted, as its arguments in either order (#226 Q20), and the one option allowed
# beside them, once, which changes what the merge prints and not what it does (#468).
FAST_FORWARD = ["--ff-only", "origin/main"]
QUIET = ("--quiet", "-q")

# What a pathspec holding one of these means is git's pattern or magic, not one file: the glob
# characters and their escape, and a leading colon (https://git-scm.com/docs/gitglossary#def_pathspec).
PATTERN = re.compile(r"[*?\[\\]|^:")

# The modes of a regular file in a tree, plain and executable; a symbolic link or a submodule is not one.
FILE_MODES = {"100644", "100755"}

# git's options before the subcommand that take the next word as their value, so that word is not
# the subcommand (https://git-scm.com/docs/git#_options; git 2.54 takes each in this separate form).
VALUED = {"-C", "-c", "--git-dir", "--work-tree", "--namespace", "--config-env", "--attr-source"}

# The shell's operators, longest first, so `&&` is read before `&` and `<<-` before `<<`.
OPERATORS = sorted({"<<<", "<<-", "&>>", "&&", "||", ";;", "|&", "&>", ">>", ">&", "<&", ">|", "<>", "<<",
                    ";", "&", "|", "(", ")", "<", ">", "\n"}, key=len, reverse=True)
# The operators that redirect: the word after one is its target, and the command goes on past it.
REDIRECTIONS = {"<<<", "<<-", "&>>", "&>", ">>", ">&", "<&", ">|", "<>", "<<", "<", ">"}
HEREDOCS = {"<<", "<<-"}

# Words after which the shell runs the next word as the command: reserved words that open or go on
# with a compound command, and builtins and utilities that run their arguments as one.
PREFIXES = {"!", "{", "if", "then", "else", "elif", "do", "while", "until", "time", "command", "builtin",
            "exec", "nohup", "noglob", "env"}

NAME = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
ASSIGNMENT = re.compile(r"([A-Za-z_][A-Za-z0-9_]*)=")


class Variable:
    """A `$name` or `${name}` in a word."""

    def __init__(self, name):
        self.name = name


class Substitution:
    """A `$(...)` or backticks in a word: the tokens of the command it runs."""

    def __init__(self, tokens):
        self.tokens = tokens


# A part of a word whose value only the running shell knows: `$'...'`, `${name:-default}`.
UNKNOWN = object()


class Word:
    """One shell word: its literal text, with the variables and substitutions it holds."""

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
                add(Variable("HOME"))
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
        """The expansion at a `$` or a backtick: a Variable, a Substitution, UNKNOWN, or a plain `$`."""
        if self.at("`"):
            end = self.closing(self.i + 1, "`")
            inner = self.text[self.i + 1:end]
            self.i = end + 1
            return Substitution(Lexer(inner).tokens())
        if self.at("$("):
            self.i += 2
            return Substitution(self.tokens(nested=True))
        if self.at("${"):
            start = self.i + 2
            self.i += 1
            self.skip_balanced("{", "}")
            content = self.text[start:self.i - 1]
            return Variable(content) if NAME.fullmatch(content) else UNKNOWN
        if self.at("$'"):
            self.i = self.closing(self.i + 2, "'") + 1
            return UNKNOWN
        name = NAME.match(self.text, self.i + 1)
        if name:
            self.i = name.end()
            return Variable(name.group())
        self.i += 1
        return "$"

    def closing(self, j, mark):
        """The index of the first mark at or after j that no backslash escapes, or the text's end."""
        while j < len(self.text) and self.text[j] != mark:
            j += 2 if self.text[j] == "\\" else 1
        return j

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
            value = None
            if isinstance(part, str):
                value = part
            elif isinstance(part, Variable):
                # The shell keeps PWD where its cds took it; the hook's own PWD is somewhere else.
                fallback = self.cwd if part.name == "PWD" else os.environ.get(part.name)
                value = self.variables.get(part.name, fallback)
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
                if isinstance(part, Substitution):
                    yield from git_calls(part.tokens, shell.copy())
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
    """(directory, subcommand, its arguments, whether an option other than -C came before it)."""
    i = 0
    other = False
    while i < len(args) and args[i] is not None and args[i].startswith("-"):
        if args[i] == "-C":
            directory = within(directory, args[i + 1] if i + 1 < len(args) else None)
        else:
            other = True
        i += 2 if args[i] in VALUED else 1
    subcommand = args[i] if i < len(args) else None
    return directory, subcommand, args[i + 1:], other


def restore_paths(subcommand, args, options):
    """The paths of a restore in the one form admitted (#376), or None: `git restore` with no option but
    `--`, or `git checkout --`, each followed by one or more paths, every one a plain path to a file and
    none a pattern or magic. With no `--source` or `--staged` both restore the working tree from the
    index, which a path with no staged change holds as HEAD has it: with no --source, restore reads
    "from HEAD if --staged is given, otherwise from the index"
    (https://git-scm.com/docs/git-restore#_options), and checkout's `[--] <pathspec>` form, with no
    tree-ish, overwrites the working tree "with the contents in the index"
    (https://git-scm.com/docs/git-checkout#_description)."""
    if options or any(arg is None for arg in args):
        return None
    if subcommand == "checkout":
        if not args or args[0] != "--":
            return None
        paths = args[1:]
    elif subcommand == "restore":
        split = args.index("--") if "--" in args else len(args)
        if any(arg.startswith("-") for arg in args[:split]):
            return None
        paths = args[:split] + args[split + 1:]
    else:
        return None
    if not paths or any(not path or path.endswith("/") or PATTERN.search(path) for path in paths):
        return None
    return paths


def fast_forward(args):
    """Whether a merge's arguments are the one fast-forward admitted: `--ff-only` and `origin/main`,
    with at most one `--quiet` or `-q` beside them."""
    words = [arg or "" for arg in args]
    quiet = [word for word in words if word in QUIET]
    if len(quiet) > 1:
        return False
    return sorted(word for word in words if word not in QUIET) == FAST_FORWARD


def refusal(subcommand, args, options):
    """Whether the main checkout refuses the call: False, True, "fast-forward" when it is the one
    merge admitted, which is refused only on a `main` that is not clean, or "restore" when it is the
    one restore admitted, which is refused unless it would change no content."""
    if subcommand == "merge" and not options and fast_forward(args):
        return "fast-forward"
    if restore_paths(subcommand, args, options) is not None:
        return "restore"
    if subcommand in MOVES or subcommand in CHANGES:
        return True
    if subcommand == "stash":
        return not args or args[0] not in ("list", "show")
    return False


def unclean(directory):
    """The line saying which condition of a clean `main` fails in directory, or "" when none does.
    git status runs with --no-optional-locks, so reading does not take the index lock a session's own
    git may be holding (https://git-scm.com/docs/git#Documentation/git.txt---no-optional-locks)."""
    head = subprocess.run(["git", "-C", directory, "symbolic-ref", "-q", "HEAD"], capture_output=True,
                          text=True)
    if head.returncode != 0 or head.stdout.strip() != "refs/heads/main":
        return NOT_MAIN
    status = subprocess.run(["git", "--no-optional-locks", "-C", directory, "status", "--porcelain",
                             "--untracked-files=no"], capture_output=True, text=True)
    if status.returncode != 0 or status.stdout.strip():
        return CHANGED
    return ""


def unrestorable(directory, paths):
    """The line saying why a restore of paths in directory would change content, "" when it would
    change none, or None when it is not the admitted form after all, a path naming a directory. A path
    is read from directory, as git reads it. HEAD must be `main`, as for the fast-forward, and each
    path must be a regular file at origin/main whose working-tree bytes are that blob's, and must have
    no staged change, so the index the restore copies from holds HEAD's blob; once `main` is
    fast-forwarded the file holds the same bytes it holds now.
    Any git that fails refuses, as a failing status read does in unclean(). `--no-filters` hashes the
    bytes as they are, with no clean filter or line-ending conversion
    (https://git-scm.com/docs/git-hash-object#Documentation/git-hash-object.txt---no-filters), and
    `diff --quiet` implies --exit-code, so it exits 1 on a difference
    (https://git-scm.com/docs/git-diff#Documentation/git-diff.txt---quiet)."""
    for path in paths:
        target = within(directory, path)
        if os.path.isdir(target):
            return None
    # Off `main` no fast-forward follows, so the restore would leave HEAD's blob where origin/main's
    # bytes were: it changes content, which is what #376 admits a restore for not doing.
    head = run_git(directory, "symbolic-ref", "-q", "HEAD")
    if head.returncode != 0 or head.stdout.strip() != "refs/heads/main":
        return RESTORE_NOT_MAIN
    for path in paths:
        target = within(directory, path)
        listing = run_git(directory, "ls-tree", "origin/main", "--", path)
        entry = listing.stdout.split("\t", 1)[0].split() if listing.returncode == 0 else []
        if len(listing.stdout.splitlines()) != 1 or len(entry) != 3 or entry[0] not in FILE_MODES:
            return ABSENT.format(path=path)
        landed = entry[2]
        if os.path.islink(target) or not os.path.isfile(target):
            return NOT_LANDED.format(path=path)
        held = run_git(directory, "hash-object", "--no-filters", "--", path)
        if held.returncode != 0 or held.stdout.strip() != landed:
            return NOT_LANDED.format(path=path)
        staged = run_git(directory, "diff", "--cached", "--quiet", "HEAD", "--", path)
        if staged.returncode != 0:
            return STAGED.format(path=path)
    return ""


def run_git(directory, *args):
    """git in directory, reading pathspecs as literal paths and taking no optional lock."""
    return subprocess.run(["git", "--no-optional-locks", "--literal-pathspecs", "-C", directory, *args],
                          capture_output=True, text=True)


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
    for directory, subcommand, args, options in git_calls(Lexer(command).tokens(), Shell(event.get("cwd"))):
        refused = None if directory is None else refusal(subcommand, args, options)
        if not refused:
            continue
        if guarded is None:
            own = git_dirs(os.path.dirname(os.path.abspath(__file__)))
            if own is None:
                return 0
            guarded = own[1]
        found = git_dirs(directory)
        if found and found[0] == found[1] == guarded:
            reason = ""
            if refused == "fast-forward":
                reason = unclean(directory)
                if not reason:
                    continue
            elif refused == "restore":
                reason = unrestorable(directory, restore_paths(subcommand, args, options))
                if reason == "":
                    continue
                # A directory is not the admitted form, so its refusal is the plain one.
                reason = reason or ""
            print(REFUSAL.format(subcommand=subcommand, directory=directory, reason=reason, route=ROUTE),
                  end="", file=sys.stderr)
            return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
