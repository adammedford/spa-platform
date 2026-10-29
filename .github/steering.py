#!/usr/bin/env python3
"""Checks the documents that steer a session: a claim here misleads before any code is read.

Owner: integrator.

Two of the documents are loaded into every context window -- AGENTS.md and CLAUDE.md -- one is
read by every workstream before it builds, the contracts index, and the last three widen the scope
again: a ticket citation is a pointer, and any tracked file may carry one; a citation is only as
good as the number it names, which every tracker directory has to keep to one file; and since
ADR-0020 a ticket is a GitHub issue, so a `#NNN` names a number only GitHub can vouch for. Each rule
below was a prose paragraph first, and each paragraph was broken by the session after the one that
wrote it.

**No progress.** AGENTS.md says of itself that it "does not track progress, because a copy of the
state goes stale and then misleads every session that loads it". The skeleton import wrote three
progress lines; a retrospective removed two, wrote that paragraph, and left the third sitting
directly beneath it, where it stayed for another session. The file is loaded from the top of every
agent's context window, so a stale claim there misleads from the first token. A paragraph asking
for the rule is not the same as a check enforcing it.

**No dangling pointer.** These files are mostly context pointers, and a pointer at something that
does not exist costs a session the search before it finds nothing. `docs/gates/` was named twice
while absent, and was later dropped rather than written. A path that is promised rather than broken
goes in PLANNED below, which fails once the path appears, so the list cannot outlive its entries.

**No pointer that contradicts its target.** The same defect one step on: a claim about the
repository that the repository denies. `contracts/README.md` carries a freeze gate per row and each
contract carries its own freeze header, and the Pipeline row once landed naming a gate its target
never froze at, contradicting `contracts/pipeline.md` two directories away. A cell may say more
than the header -- `G1a; stage order changed at G1b` -- so the comparison is on the leading gate
token, and a cell naming no gate at all -- `With the registry document` -- borrows the gate of the
row it names, against which its own target's header is compared. A cell naming no row the index
holds fails, since it is a claim nothing can compare; the one row whose cell names no single row
by design goes in UNCOMPARED below, on PLANNED's terms. A row this rule cannot read fails as well,
and so does a target it cannot read a header from: one that does not exist, a markdown contract
with no freeze bullet, a header naming no gate, a schema stating no freeze, or JSON that does not
parse. Only code, a directory and a schema's example are passed over, since none of them carries
a header (#493).
The line it passes prints how many headers it compared: a rule that quietly compares nothing is
the defect it was written to catch, one level up.

**No citation that resolves to nothing.** The dangling-pointer rule again, for the one pointer
written as a number rather than a path. The tracker guide once said a document citing a
ticket "is citing a file every other session and CI can open, which is the whole point of citing
it", and merged code once cited overnight tickets 63 and 64 while `origin/main` stopped at 61. A
number alone does not say where, so where it resolves is this rule's decision: against the tracker
directory the citing file sits in, because numbering restarts per tracker, and against the
plan-wide tracker for a file outside `.scratch/`, which is what every ADR and contract here means
by an unqualified citation. A document meaning another tracker names it -- `overnight ticket 53`.
One global number-to-file map would mis-resolve both readings. A ticket named before it is filed
goes in UNFILED below, on PLANNED's terms, and the line this rule passes prints how many citations
it followed, for the reason the freeze count is printed.

**No number two tickets share.** The citation rule's other half: a number that resolves to two
files resolves, and still leaves its reader to guess. `.scratch/overnight/issues/` once held two
61s and two 66s, filed by sessions that could not see each other's unlanded work, and the fix for
that is a failure at the second push, since neither branch was wrong until the other landed. The
check is per tracker, read by the same trackers() the citation rule resolves in, because numbering
restarts in each and `06` in several of them is several tickets. What a session does when its
number is taken is in this rule's failure message, beside the archive's rules in
docs/agents/tracker-policy.md; the rule cannot do the part that matters,
which is reading each citation of the old number to see which ticket it meant, because after a
rename every one of them still resolves. The line it passes prints how many tickets it read.

**No `#NNN` that names nothing.** The citation rule once more, for the form a ticket takes since
ADR-0020 moved tickets to GitHub Issues. `docs/agents/tracker-policy.md` says to cite an issue or a
pull request as `#NNN`, that the two share one number sequence, and that this check fails a push
over a `#NNN` that names neither. GitHub is what knows which numbers exist, so the rule asks it,
once per run and through `gh`: GitHub's REST API "considers every pull request an issue", so one
listing of the repository's issues answers for both forms. What the rule reads is deliberately
narrow -- a `#` against one to four digits, ending at a word boundary and not following a word
character, a slash, an ampersand or another `#` -- so a heading, an anchor, an entity, a label
colour and a word's own suffix are not read; ISSUE_CITATION below says what each exclusion is for.
What no shape tells from a citation, the token counters in three tracked spike transcripts, goes in
TRANSCRIPTS below, on PLANNED's terms. With no network, no `gh` or no token the rule prints one
line saying how many citations went unchecked and why, and fails nothing, as the pre-push hook does
for a missing tool; the line it passes prints how many citations GitHub vouched for.

**No steering change without its authority.** The one rule that reads history rather than the
tree. ADR-0023 decision 3 says a change to a steering document cites its authority in the commit,
and that this check holds a commit citing none. On the night of 2026-10-18 five commits changed a
steering document on no confirmed answer -- two glossary entries their tickets did not ask for, a
lane's pre-accepted decision written into the landing guide, and two reviewers' rewrites of their
own -- and the night's reviews held none of them: the report found two, and a later review found the
reviewers' two. Whether a commit cites an authority is mechanical, so it is this check's; whether
what it cites decides the change stays the reviewer's. A commit is held when it sits between
`--base` and HEAD, is not a merge, was made from AUTHORITY_SINCE on, and changes a steering
document other than by adding an open change request; it then needs an `Authority:` trailer of one of two
forms, `#<n>` for the ticket whose acceptance criterion asks for the change, or the URL of the
comment on this repository that holds a confirmed answer. git's own trailer parser reads it, so an
`Authority:` line inside the prose is not one. With no `--base` every commit HEAD reaches is read,
so a caller cannot skip the rule by naming nothing. A merge passes because what it brings in is
commits, each held on its own; a commit made before the starting point passes because it could not
have known the rule, and rewording one would mean rewriting a branch that has landed. The citation
is checked the way the `#NNN` rule checks one, in the same single listing, which also says whether
a number is an issue or a pull request, and a ticket is an issue. The listing can come back short
with no error (#297), so a number it lacks is asked about alone before it is refused: a 404 or a
410 refuses it, and any other failure leaves it unchecked with GitHub's reason. A URL is checked
against the comment GitHub keeps under its id, and that comment's author against the account
that confirms (#316): the one docs/agents/tracker-policy.md names at the base, or the repository's
owner while it names none, since a `Confirm` by any other account that can comment confirms
nothing. Offline, the trailer is still required, and its citations are reported as not checked;
the line the rule passes prints how many held commits GitHub vouched for.

Run: python3 -I .github/steering_tests.py && python3 -I .github/steering.py --base origin/main
"""

from __future__ import annotations

import argparse
import datetime
import json
import pathlib
import re
import subprocess
import sys
import typing

FILES = ["AGENTS.md", "CLAUDE.md"]

# Paths named before they exist. Each is a planned artifact, and this check fails when one appears,
# so landing it forces the entry out rather than leaving a stale exemption behind.
PLANNED = {
    "docs/gaps.md": "workstream 6 owns it and has not started; the row declares ownership",
}

# Deliberately small, and aimed at the shapes the drift actually took rather than at every way a
# sentence could carry state. A false negative here is a missed line; a false positive is a check
# nobody trusts. Add a pattern when a new shape gets through, not in anticipation.
PROGRESS = [
    (re.compile(r"\b\d{4}-\d{2}-\d{2}\b"), "a date"),
    (
        re.compile(
            r"\b\d{1,2}\s+(January|February|March|April|May|June|July|August|September|October"
            r"|November|December)\s+\d{4}\b"
        ),
        "a date",
    ),
    (re.compile(r"^\s*-?\s*\*{0,2}Done:", re.MULTILINE), "a done list"),
    (re.compile(r"\bOpen on day one\b"), "a done list"),
    (re.compile(r"\bitems?\s+passed\b"), "a count of what passed"),
    (
        re.compile(
            r"\b(one|two|three|four|five|six|seven|eight|nine|ten|\d+)\s+of\s+"
            r"(its\s+|the\s+)?(one|two|three|four|five|six|seven|eight|nine|ten|\d+)\b"
        ),
        "a count of how far something got",
    ),
]

# A backticked token counts as a pointer only when it carries a slash, so it is relative to the
# repository root and resolvable. A bare name such as `Program.cs` or `GLOSSARY.md` is prose naming
# a file, not a path to it, and guessing where those live is how this check earns false positives.
PATHLIKE = re.compile(r"`([A-Za-z0-9_][A-Za-z0-9_.-]*(?:/[A-Za-z0-9_.-]*)+)`")

# The contracts index, whose "Freezes at" column makes a claim about a file two directories away.
INDEX = "contracts/README.md"
CONTRACT_COLUMN = "Contract"
FILE_COLUMN = "File"
FREEZE_COLUMN = "Freezes at"

# A table row, and the cells in it. Every cell this rule reads is found by its column's heading, so
# a column moved or added changes nothing here and the file's other tables are not mistaken for
# this one. A pipe inside a cell is escaped, and splitting on it would shift every cell after it.
ROW = re.compile(r"^\s*\|(.*)\|\s*$")
CELL_BOUNDARY = re.compile(r"(?<!\\)\|")
DIVIDER = re.compile(r"^[\s:|-]+$")

# A gate is G0, G1a, G2. The leading one in a cell is the freeze it claims; anything after it is
# allowed to say more, as in `G1a; stage order changed at G1b`.
GATE = re.compile(r"\bG\d[a-z]?\b")

# A cell naming no gate defers to another row: `With the registry document` freezes when the
# Registry document row does, so the App definition's own header is compared against the gate the
# Registry document row names. The row is named as its Contract cell spells it, in any case and
# with or without `With the` in front, and a name that no row carries is a failure: a cell that
# defers to nothing is a claim nothing compares, which is the defect this rule exists to catch.
# The named row has to name a gate itself: a cell deferring to a cell that defers again is
# reported rather than followed, because one hop is the shape the index writes, and failing the
# second hop rules out a cycle without walking one.
RELATIONAL = re.compile(r"^\s*(?:with\s+(?:the\s+)?)?(?P<row>.*?)\s*\.?\s*$", re.IGNORECASE)

# Rows whose relational cell names no single row, and so compared with nothing, keyed by the row's
# Contract cell and its freeze cell as the index writes them. PLANNED's rule one level over: this
# check fails once no row carries the pair, so a reworded cell or a renamed row is a new claim,
# compared or reported, rather than one waved through on the old words' exemption. The row is in
# the key because the reason is the row's and not the words': another row carrying the same cell
# over a contract with a header would otherwise go uncompared on a reason that is false of it,
# which is the exception scoped to the identifier that CODING_STANDARDS.md's "A universal names its
# exceptions by the writer" warns of.
#
# The Test doubles row stays exempt. Its cell defers to one interface per double, not to one row:
# the interfaces sit in the Records and interfaces row at two gates -- G1a, and G1b for the
# identity interfaces -- so there is no single gate to borrow; and its target, src/Platform.Testing,
# is code with no freeze header, so resolving the cell would compare nothing anyway. What holds a
# double to its interface is the compiler, since each one implements the interface it stands in
# for, and the shared contract suites (ObjectStoreContract, SessionStoreContract) that run the
# double and the real implementation through the same cases. Since #145 the row also carries the
# stage host, the captured log and the raw sender, which stand in for no interface and freeze when
# they land (change request issue-145, "The charter, and the two edits it takes"); they are code
# with no header too, and their own tests in Platform.Testing.Tests hold them.
UNCOMPARED = {
    (
        "Test doubles, and the stage host",
        "A double with the interface it stands in for; the stage host, the captured log and the "
        "raw sender when #145 lands",
    ): "each double freezes with its own interface, the stage host's modules when they land, and "
    "the code carries no header",
    (
        "Test id register",
        "Never; a row is added on the integrator's decision (#322)",
    ): "the register never freezes, so there is no gate to compare its header against (#322)",
}

# The two header shapes in the tree. A markdown contract carries a bullet under its title; a JSON
# schema carries a sentence inside its top-level description. Each is read down to the sentence
# that states the freeze, because the bullet runs on into its rationale over several lines and a
# message quoting half a clause is a message its reader has to open the file to finish. The
# bullet's continuation lines are read with it, so a claim is found wherever the author's line
# happened to run out; a gate that went unread would be a disagreement that went unreported.
MD_FREEZE = re.compile(
    r"^-\s+\*\*Freezes at:\*\*(?P<freeze>[^\n]*(?:\n[ \t]+[^\n]*)*)", re.MULTILINE
)
SCHEMA_FREEZE = re.compile(r"[Ff]reezes at\s+(?P<freeze>G\d[a-z]?[^.]*)\.")

# The file column names each target as a markdown link, or as a backticked path when there is
# nothing to link to, as `src/Gateway/Program.cs`.
TARGET = re.compile(r"\]\(([^)]+)\)|`([A-Za-z0-9_][A-Za-z0-9_.-]*(?:/[A-Za-z0-9_.-]+)+)`")

# The tracker: one directory per feature under .scratch/, each holding `issues/<NN>-<slug>.md` and
# each numbering from 01 of its own. `overnight` is the plan-wide one, and the one a document
# outside .scratch/ means when it names none: every ADR and contract that cites a ticket cites it.
TRACKER_ROOT = ".scratch"
ISSUES = "issues"
DEFAULT_TRACKER = "overnight"

# A ticket number is one or two digits, and two things are refused before it is read. A third digit,
# because no ticket has one and `ticket 100` would otherwise resolve as ticket 10. And a dot before
# a digit, because `workstream 3's tickets 3.3, 3.5 and 3.9` cites plan task ids: read as ticket 3
# that line is a dangling citation in a settled file, and a rule red on a settled file is a rule
# switched off within the week.
TICKET_NUMBER = r"\d{1,2}(?!\d)(?!\.\d)"

# The gap inside a citation: horizontal space, or horizontal space across one line break. The
# prose here wraps near column 100, so a citation split over two lines is the ordinary case and not
# an edge: eight in the tree today, `Ticket\n49` in docs/adr/0015 among them. A scan that read one
# line at a time saw none of them, which is this rule's own silent pass -- the shape it exists to
# catch, missed by the shape of the scan. One line break and no more, so a heading ending in the
# word and a list item starting with a number are not joined into a citation neither wrote.
GAP = r"(?:[ \t]+|[ \t]*\n[ \t]*)"

# The three shapes the tree writes, and no more: `ticket NN`; `tickets NN and NN`, with `,` and `/`
# enumerating the same way; and `<tracker> ticket NN`, where the word before the citation names a
# tracker directory. A range -- `tickets 62-65`, `tickets 01 to 08` -- is read at its first number
# only, because its interior is not cited and expanding it would assert tickets nobody named.
# `\b` is here and not in the greps that found these shapes: BSD grep -E does not support it, and
# a pattern that silently matched nothing is how the dangling citation went unnoticed.
CITATION = re.compile(
    rf"(?:(?P<tracker>[A-Za-z][A-Za-z0-9-]*){GAP})?"
    rf"(?P<word>\btickets?){GAP}"
    rf"(?P<numbers>{TICKET_NUMBER}(?:{GAP}?(?:,|and|/){GAP}?{TICKET_NUMBER})*)",
    re.IGNORECASE,
)

# Tickets named before they are filed, keyed by the tracker and the two-digit number. PLANNED's
# rule one level over: this check fails once the ticket appears, so landing it forces the entry out
# rather than leaving a stale exemption behind. Empty is the expected state.
UNFILED: dict[tuple[str, str], str] = {}

# The number a ticket file answers to: the two digits a citation of it is looked up by, as
# dangling_tickets() globs `NN-*`. A file named any other way is not a ticket a number can reach.
NUMBERED = re.compile(r"^(\d{2})-")

# A citation of an issue or pull request, in the form GitHub links: `#` against the number, as
# `(#NN)`, `#NN,` and `PR #NN` write it. Deliberately narrow, and each exclusion is a `#` the tree
# writes for another reason. Not after a word character, so `rt#1` -- a token counter's suffix --
# and `owner/repo#NN`, GitHub's form for another repository's number, are not read; not after `/`,
# `&` or `#`, so a URL fragment, an HTML entity and a doubled `#` are not; and the digits end at a
# word boundary, so `#0e8a16` -- a label colour, whose digits run into letters -- is not, though a
# colour written in digits alone would be, and none is in the tree. A heading puts a space after
# its `#` and `#region` puts a letter, so neither needs excluding. Four digits at most, and a longer
# run is not read at all rather than read at its first four: this repository's numbers have two
# digits, and the one longer run in the tree is the AWS provider's issue, cited bare from two
# Terraform comments, which their owner can write `owner/repo#NNNNN`. Widen the four when this
# repository's own numbers reach five digits.
ISSUE_CITATION = re.compile(r"(?<![\w/&#])#(?P<number>\d{1,4})\b")

# Where the numbers come from: one listing of the repository's issues. GitHub's REST API "considers
# every pull request an issue, but not every issue is a pull request", so the pull requests are in
# the listing and the two forms share one sequence; --paginate follows the pages and --jq keeps
# one line per number, saying which of the two it is, from the `pull_request` key the API gives a
# pull request alone. The `#NNN` rule takes either; the authority rule takes an issue only, since
# a ticket is an issue, and this listing tells the two apart. gh fills {owner}/{repo} from the
# remote of the directory it runs in, and signs in from its keyring or from GH_TOKEN, which is what
# CI hands it.
# https://docs.github.com/en/rest/issues/issues#list-repository-issues
GH_ISSUES = (
    "gh", "api", "--paginate", "repos/{owner}/{repo}/issues?state=all&per_page=100",
    "--jq", r'.[] | "\(.number) \(if .pull_request then "pull" else "issue" end)"',
)

# Where one number is looked up when the listing lacks it. The listing can exit 0 with fewer pages
# than the repository holds: on 2026-10-22 one push's run refused trailers citing #185, #135 and
# #159, each a real issue, and the next run over the same tree vouched for them (#297). So a number
# the listing lacks is not taken as one GitHub never issued until GitHub says so of that number
# alone. The endpoint serves an issue or a pull request by its number and answers 404 for a number
# it never issued, and 404 or 410 for a deleted issue; --jq writes the listing's line for it, the
# number first, so an answer about another number -- a transferred issue, which GitHub redirects to
# its new home -- is not read as this one. gh fills {owner}/{repo} as it does for GH_ISSUES, and
# {number} is the number.
# https://docs.github.com/en/rest/issues/issues#get-an-issue
GH_ISSUE = (
    "gh", "api", "repos/{owner}/{repo}/issues/{number}",
    "--jq", r'"\(.number) \(if .pull_request then "pull" else "issue" end)"',
)

# How long to wait for gh before saying so. The hook runs this check on every push, and a check that
# hangs on a bad connection is a check pushed past with --no-verify the second time.
GH_TIMEOUT = 60

# Captured program output that git tracks as a spike's evidence -- docs/plan/07-evidence.md names
# the runs -- and the one thing the `#NNN` rule does not read. `token #N -> #N` there counts tokens
# and `[#N refresh_token ...]` counts grants, and no shape of the `#` tells either from a citation:
# each is followed by a space and a word, or by a bracket, exactly as a citation is. The ticket rule
# met the same problem in session transcripts and was saved by git ignoring them; these are tracked
# on purpose, so they are named here, one file at a time and none in anticipation. PLANNED's rule
# one level over: this check fails when a listed path is no longer tracked, so the list cannot
# outlive its entries.
TRANSCRIPTS = {
    "spikes/g0/stand-in-spike/spike-output.txt": "the stand-in spike's run; `token #N -> #N`",
    "spikes/g0/stand-in-spike/spike-results.json": "the same run as JSON, in its Detail strings",
    "spikes/g0/token-and-proxy/output-token-and-proxy.txt":
        "the token spike's run; `#N refresh_token` numbers the grants",
}

# The steering documents, as ADR-0023 decision 2 lists them and GLOSSARY.md's *Steering document*
# defines them: a file at the repository's root, or anything under a directory, written with its
# trailing slash. A name is matched whole and only at the root, so `docs/GLOSSARY.md` and
# `contracts-old/` are not steering documents.
STEERING_DOCUMENTS = (
    "GLOSSARY.md",
    "AGENTS.md",
    "CLAUDE.md",
    "OWNERSHIP.md",
    "CODING_STANDARDS.md",
    "docs/agents/",
    "docs/adr/",
    "contracts/",
)

# Where a change request goes. ADR-0023 decision 2: adding one is not a change to a steering
# document, and AGENTS.md gives it to every workstream as the way to ask for a contract change, so
# holding it would hold every lane that followed the rules. Only an added file passes: a change
# request edited or removed afterwards is the integrator deciding it, which is a change to
# contracts/ like any other. An added request that already names any state but open is a decision
# too, and is held by the rule below (#187).
CHANGE_REQUESTS = "contracts/changes/"

# A change request's Status line, as every request under CHANGE_REQUESTS writes it:
# `- **Status:** open`, or `- **Status:** decided, <date>. <prose>`. The first word is the state, and
# any state but open is a decision: `accepted` decides a request as surely as `decided` does. A
# file whose Status lines name different states, such as an `open` left above a `decided` added
# under a Decision heading, cannot say which holds, so it is held as unreadable (the second #188
# review); one that names the same state twice reads as that state.
STATUS = re.compile(r"(?m)^[ \t]*(?:[-*][ \t]+)?\*\*Status:\*\*[ \t]*(?P<state>[A-Za-z]+)")

# A line that uses Status as a field's label, in a shape STATUS may not read: after any list marker,
# number, table pipe, heading, quote, bold, italic or code mark, the word and then a colon, a pipe, a
# dash, an equals sign or the end of the line. `**Status**: decided`, `- Status: decided`,
# `- **Status:** _decided_`, `| Status | decided |` and `## Status` are each one. A file that has
# such a line and no line STATUS reads cannot say whether it is decided, so it is held rather than
# read as open (#188's review): a decided request in a new shape would otherwise pass as open. A
# file with no such line names no Status at all, and counts as open. The decoration is matched one
# character at a time, so a long `|---|---|` row cannot make the match backtrack without end.
STATUS_LABEL = re.compile(
    r"(?im)^[ \t]*(?:[-*+>|#_`\d.)][ \t]*)*status[*_`]*[ \t]*(?:[:|=\u2013\u2014-]|$)"
)

# What state_at() returns for a file whose Status STATUS cannot read: no state word is a `?`.
UNREADABLE = "?"

# Where the decision rule starts (#187). A commit that sets a change request's Status to decided,
# by adding it so or by editing it to say so, is the integrator deciding it, and ADR-0021 decision 4
# says a change request's status line is committed on a confirmed answer. So its Authority: trailer
# must be the URL of the comment holding that answer; a `#<n>` alone is held, because a ticket's
# acceptance criterion asking a lane to record a decision is a lane deciding, which is what
# 9f56729 and 37af894 did on the night of 2026-10-19. Made is the committer date, as for
# AUTHORITY_SINCE, and set just before the commit that adds the rule.
DECIDED_SINCE = datetime.datetime(2026, 10, 20, 14, 10, tzinfo=datetime.timezone.utc)

# Where the authority rule starts. A commit made before it was written could not have known the
# rule, and rewording one would mean rewriting a branch that has landed. Made is the committer date:
# an amend or a rebase makes a commit again and keeps its author date, so the author date would let
# a commit first written before this point carry a steering change added after it. Set just before
# the commit that adds the rule, which is the first one it holds.
AUTHORITY_SINCE = datetime.datetime(2026, 10, 19, 10, 28, tzinfo=datetime.timezone.utc)

# The trailer's key. git's own parser reads it, matching the key in any case, so the rule agrees
# with `git interpret-trailers --parse` and with the %(trailers) a reader of the log sees: a
# trailer is a `Key: value` line in the group that ends the message after a blank line, and the
# group counts only when it is all trailers, or holds one git generates or is configured for and is
# at least a quarter trailers. `Authority:` is neither, so it goes in a group of trailers alone.
# https://git-scm.com/docs/git-interpret-trailers
AUTHORITY = "Authority"

# The two forms an authority takes (ADR-0023 decision 3): a ticket, `#<n>`, whose acceptance
# criterion asks for the change; or the URL of the comment on this repository that holds a
# confirmed answer, as GitHub links a comment on an issue or on a pull request's conversation.
# Each is matched whole, so a sentence that merely contains one is neither form.
ISSUE_AUTHORITY = re.compile(r"#(?P<number>[1-9]\d*)")
COMMENT_AUTHORITY = re.compile(
    r"https://github\.com/[^/\s]+/[^/\s]+/(?:issues|pull)/[1-9]\d*"
    r"#issuecomment-(?P<comment>[1-9]\d*)"
)

# Where a comment is looked up: the endpoint serving an issue comment by its id, which covers a
# pull request's conversation too, answers 404 for an id this repository has no comment under, and
# gives the comment's own URL as `html_url` and its author's login as `user.login`, on one line,
# the URL first; a deleted author is a null user, written as no login. gh fills {owner}/{repo} as it
# does for GH_ISSUES, and {comment} is the id.
# https://docs.github.com/en/rest/issues/comments#get-an-issue-comment
GH_COMMENT = (
    "gh", "api", "repos/{owner}/{repo}/issues/comments/{comment}",
    "--jq", r'"\(.html_url) \(.user.login // "")"',
)

# Who may write the comment an `Authority: <URL>` cites (#316). docs/agents/tracker-policy.md says a
# `Confirm` comment is the human's act, and CODING_STANDARDS.md -> "An authorisation is read from
# its author" counts one only when the account the rule names wrote it. The account is the one the
# guide names on a line of this shape, matched narrowly so that prose about accounts is not read as
# naming one; while it names none, the repository's owner, read off the URL GitHub keeps the comment
# at. tools/overnight_collect.py reads the same line for who may apply `ready-for-agent`, and
# tools/overnight_collect_tests.py holds the two equal.
GUIDE = "docs/agents/tracker-policy.md"
ACCOUNT_LINE = re.compile(
    r"(?m)^- \*\*The human's account is `@(?P<login>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))`\.\*\*"
)
URL_OWNER = re.compile(r"^https://github\.com/(?P<owner>[^/\s]+)/")


class Comment(typing.NamedTuple):
    """A comment as GitHub keeps it: where, and the login of who wrote it ("" for a deleted one)."""

    url: str
    author: str

# What the rule reads of each commit, in one `git log`: the commit, when it was made, its subject,
# and the values of its Authority: trailers, in fields no subject or trailer value carries.
LOG_FORMAT = (
    "%H%x1f%ct%x1f%s%x1f"
    f"%(trailers:key={AUTHORITY},valueonly,unfold,separator=%x1e)"
)


class Held(typing.NamedTuple):
    """A commit the authority rule holds: the paths that need an authority, the authorities it
    names, the change requests it sets to a decision with the state each now names, which need a
    confirmed answer's URL among those authorities, and the change requests whose Status it leaves
    unreadable, which no authority settles."""

    sha: str
    subject: str
    paths: tuple[str, ...]
    authorities: tuple[str, ...]
    decides: tuple[tuple[str, str], ...] = ()
    unreadable: tuple[str, ...] = ()


def progress_claims(root: pathlib.Path) -> list[str]:
    found = []
    for name in FILES:
        text = (root / name).read_text()
        for line_number, line in enumerate(text.splitlines(), start=1):
            for pattern, what in PROGRESS:
                if pattern.search(line):
                    found.append(f"{name}:{line_number}: {what}: {line.strip()}")
                    break
    return found


def dangling_pointers(root: pathlib.Path) -> tuple[list[str], list[str]]:
    """The backticked paths in the steering files that resolve to nothing, and the PLANNED paths
    that have landed. Named for its pointers since dangling_tickets joined it: both rules are
    about a reference that resolves to nothing, and one of them is not about paths.
    """
    missing, landed = [], []
    seen = set()

    for name in FILES:
        text = (root / name).read_text()
        for line_number, line in enumerate(text.splitlines(), start=1):
            for path in PATHLIKE.findall(line):
                seen.add(path)
                if (root / path).exists():
                    continue
                if path in PLANNED:
                    continue
                missing.append(f"{name}:{line_number}: points at '{path}', which does not exist")

    for path, why in PLANNED.items():
        if (root / path).exists():
            landed.append(f"'{path}' exists now ({why})")

    return missing, landed


COLUMNS = (CONTRACT_COLUMN, FILE_COLUMN, FREEZE_COLUMN)


def index_rows(text: str):
    """The rows of the one table carrying all three columns, as (line number, headers, cells).

    A row is recognised by its pipes, and which table it belongs to by the header row above it:
    contracts/README.md holds two other tables, and neither names these columns. A row whose cells
    do not line up with the headers is yielded too, because the caller reports it rather than
    passing over it in silence.
    """
    headers = None
    for number, line in enumerate(text.splitlines(), start=1):
        match = ROW.match(line)
        if not match:
            headers = None
            continue
        cells = [cell.strip() for cell in CELL_BOUNDARY.split(match.group(1))]
        if headers is None:
            if all(column in cells for column in COLUMNS):
                headers = cells
            continue
        if DIVIDER.match(line):
            continue
        yield number, headers, cells


class Freeze(typing.NamedTuple):
    """What one index target says about its own freeze: where it is, and either the header it
    states or why no header could be read from it."""

    where: str
    header: str = ""
    problem: str = ""


def freeze_header(root: pathlib.Path, target: str) -> Freeze | None:
    """Where the target is and what it says about its own freeze, or None when it carries no
    header by design.

    A link in the index is relative to the index, and a backticked path is relative to the root,
    so both are tried. A row may point at source code, a directory of it or a schema's example, and
    none of those carries a header: absence there is not a disagreement, since this rule compares
    two claims about a gate and where there is no header there is only the index's. Every other
    absence is a problem, because it is the same silence one step earlier (#493): a target that
    exists at neither reading, a markdown contract with no freeze bullet, a header naming no gate,
    a schema whose description states no freeze, and JSON that does not parse each left a row
    compared with nothing while the line this check prints still counted it as agreeing. The
    header naming no gate is the one of the five this function cannot see: it returns that header
    as read, and freeze_disagreements() fails the row when it finds no gate in it.
    """
    # A link may carry a fragment -- `registry.schema.json#/$defs/oauth` -- which names a part of
    # the file, not another file.
    path = target.split("#", 1)[0]
    for candidate in (root / INDEX).parent / path, root / path:
        if candidate.is_dir():
            return None
        if not candidate.is_file():
            continue
        where = candidate.relative_to(root).as_posix()
        if candidate.suffix == ".md":
            found = MD_FREEZE.search(candidate.read_text())
            if not found:
                return Freeze(where, problem="carries no '- **Freezes at:**' bullet")
            # The bullet's first sentence is the freeze; what follows it is the reason, and in
            # http-contract.md and request-gate.md it runs on past the end of the line.
            bullet = " ".join(found.group("freeze").split())
            sentence = bullet.split(". ")[0].strip().rstrip(".")
            return Freeze(where, f"Freezes at: {sentence}")
        if candidate.suffix == ".json":
            try:
                document = json.loads(candidate.read_text())
            except ValueError:
                return Freeze(where, problem="is not JSON that parses")
            description = document.get("description", "") if isinstance(document, dict) else ""
            found = SCHEMA_FREEZE.search(description)
            if found:
                return Freeze(where, found.group(0))
            # A schema states its freeze; the example beside it is sample data and states none.
            if candidate.name.endswith(".schema.json"):
                return Freeze(where, problem="is a schema whose description states no freeze")
        return None
    return Freeze(path, problem=f"exists neither beside {INDEX} nor at the repository root")


def spoken(name: str) -> str:
    """A row's name as a relational cell may write it: in any case, with its spacing collapsed."""
    return " ".join(name.strip("`").split()).lower()


def freeze_disagreements(
    root: pathlib.Path,
) -> tuple[list[str], list[str], list[str], list[str], list[str], int]:
    """The rows that contradict their target, the rows this rule cannot read, the targets whose
    header it cannot read, the relational cells that defer to no row, the UNCOMPARED entries no row
    carries, and how many headers it read to find out.

    The count is reported, so the line this check prints when it passes says how much it
    compared. A sentence that would be printed whether or not the comparison happened is a
    sentence no test can hold to anything. The unreadable rows and targets are reported for the
    same reason, and so are the cells deferring to nothing: a row passed over in silence is a
    freeze claim nothing compares.
    """
    index = root / INDEX
    exempt = set()
    found, unreadable, headerless, nowhere, compared = [], [], [], [], 0
    if not index.is_file():
        return found, unreadable, headerless, nowhere, outlived(exempt), compared

    rows = []
    for number, headers, cells in index_rows(index.read_text()):
        if len(cells) != len(headers):
            unreadable.append(
                f"{INDEX}:{number}: {len(cells)} cells in a {len(headers)}-column table: "
                f"{' | '.join(cells)}"
            )
            continue
        rows.append((number, dict(zip(headers, cells))))

    # Read whole before any row is compared, because a relational cell may name a row below it.
    by_name = {spoken(cells[CONTRACT_COLUMN]): cells for number, cells in rows}

    for number, cells in rows:
        name = cells[CONTRACT_COLUMN].strip("`")
        cell = cells[FREEZE_COLUMN]
        claimed = GATE.search(cell)
        claim = f"the index cell '{cell}'"
        if not claimed:
            deferred = spoken(RELATIONAL.match(cell).group("row"))
            named = by_name.get(deferred)
            if named is None and (name, cell) in UNCOMPARED:
                exempt.add((name, cell))
                continue
            if named is None:
                nowhere.append(
                    f"{INDEX}:{number}: row '{name}': the index cell '{cell}' names no gate, "
                    f"and no row of the table is called '{deferred}'"
                )
                continue
            claim += f" defers to row '{named[CONTRACT_COLUMN].strip('`')}', which"
            claimed = GATE.search(named[FREEZE_COLUMN])
            if not claimed:
                nowhere.append(f"{INDEX}:{number}: row '{name}': {claim} names no gate either")
                continue
        for link, backticked in TARGET.findall(cells[FILE_COLUMN]):
            read = freeze_header(root, link or backticked)
            if read is None:
                continue
            where, header, problem = read
            if problem:
                headerless.append(f"{INDEX}:{number}: row '{name}': {where} {problem}")
                continue
            stated = GATE.search(header)
            if not stated:
                headerless.append(
                    f"{INDEX}:{number}: row '{name}': {where}'s header '{header}' names no gate"
                )
                continue
            compared += 1
            if stated.group(0) != claimed.group(0):
                # Both sides in full, because the point of the message is that neither file has
                # to be opened to see which of the two is wrong.
                found.append(
                    f"{INDEX}:{number}: row '{name}': {claim} names "
                    f"{claimed.group(0)}, and {where}'s header '{header}' names "
                    f"{stated.group(0)}"
                )
    return found, unreadable, headerless, nowhere, outlived(exempt), compared


def outlived(exempt: set[tuple[str, str]]) -> list[str]:
    """The UNCOMPARED entries that exempted no row this run, so the list cannot outlive them."""
    return [
        f"no row '{row}' has the freeze cell '{cell}' naming no row ({why})"
        for (row, cell), why in UNCOMPARED.items()
        if (row, cell) not in exempt
    ]


def tracked_files(root: pathlib.Path) -> list[str]:
    """The files git tracks, which is the set a citation can be held to.

    Not a walk of the tree: `.scratch/<slug>/log/` is ignored on purpose -- a transcript, not a
    spec -- and a rule reading transcripts would fail a push over a line nobody can correct.
    Build output would be read too, and neither is a document that cites anything.
    """
    found = subprocess.run(
        ("git", "ls-files", "-z"), cwd=root, capture_output=True, text=True, check=False
    )
    if found.returncode != 0:
        # Not a repository, so nothing is tracked. The count this rule prints is what catches
        # that: zero citations read means no sentence, and the case asserting a floor goes red.
        return []
    return [name for name in found.stdout.split("\0") if name]


def text_of(root: pathlib.Path, relative: str) -> str | None:
    """A tracked file's text, or None for a file that is not text or cannot be read.

    Shared by the two citation rules, which scan the same files for different shapes. Neither kind
    of file is a document that cites anything, so neither is reported.
    """
    try:
        return (root / relative).read_text()
    except (OSError, UnicodeDecodeError):
        return None


def trackers(root: pathlib.Path) -> set[str]:
    """The tracker directories a number can resolve in, which is what makes a word a tracker name.

    A word is read as naming a tracker only when the directory is there, so `the overnight
    grilling of ticket 02` -- where the word before the citation is `of` -- is not mistaken for a
    tracker named `of`, and `overnight ticket 53` is not mistaken for prose.

    A directory counts only when it holds an `issues/` directory. Three under .scratch/ do not --
    they are working notes, not trackers -- and reading them as trackers would resolve every
    unqualified citation in them against a directory that cannot exist, so the only way to write
    one would be to name another tracker. They fall through to the plan-wide tracker instead,
    which is what a document with no tracker of its own means everywhere else in the tree.
    """
    directory = root / TRACKER_ROOT
    if not directory.is_dir():
        return set()
    return {child.name for child in directory.iterdir() if (child / ISSUES).is_dir()}


def citing_tracker(relative: str, available: set[str]) -> str:
    """The tracker a citation in this file resolves against when it names none itself.

    Its own, when the file sits in one: numbering restarts per tracker, so a document inside
    `.scratch/<slug>/` citing `ticket 06` means its sibling. Otherwise the plan-wide tracker,
    which is what every ADR and contract in the tree means by an unqualified citation.
    """
    parts = pathlib.PurePosixPath(relative).parts
    if len(parts) > 1 and parts[0] == TRACKER_ROOT and parts[1] in available:
        return parts[1]
    return DEFAULT_TRACKER


def dangling_tickets(root: pathlib.Path) -> tuple[list[str], list[str], int]:
    """The citations that resolve to no file, the UNFILED entries that have been filed, and how
    many citations resolved.

    The count is reported for the reason the freeze count is: a sentence printed whether or not
    anything was read is a sentence no test can hold to anything. A number matching two files
    resolves -- the citation lands on a spec, which is all this rule promises -- because two files
    sharing a number is a different defect with a rule of its own.
    """
    available = trackers(root)
    missing, filed, resolved = [], [], 0

    for relative in tracked_files(root):
        text = text_of(root, relative)
        if text is None:
            continue
        own = citing_tracker(relative, available)
        # Over the whole text rather than line by line, because a citation wraps where its
        # author's line ran out and not where its claim ends. The line is counted back from the
        # offset of the word itself, so the message names the line the citation starts on.
        for citation in CITATION.finditer(text):
            line_number = text.count("\n", 0, citation.start("word")) + 1
            named = (citation.group("tracker") or "").lower()
            tracker = named if named in available else own
            issues = root / TRACKER_ROOT / tracker / ISSUES
            for digits in re.findall(TICKET_NUMBER, citation.group("numbers")):
                number = f"{int(digits):02d}"
                if any(issues.glob(f"{number}-*")):
                    resolved += 1
                    continue
                if (tracker, number) in UNFILED:
                    continue
                missing.append(
                    f"{relative}:{line_number}: cites ticket {number}, which resolves to no "
                    f"file under {TRACKER_ROOT}/{tracker}/{ISSUES}/"
                )

    for (tracker, number), why in UNFILED.items():
        issues = root / TRACKER_ROOT / tracker / ISSUES
        if any(issues.glob(f"{number}-*")):
            filed.append(
                f"ticket {number} is filed under {TRACKER_ROOT}/{tracker}/{ISSUES}/ now ({why})"
            )

    return missing, filed, resolved


def shared_numbers(root: pathlib.Path) -> tuple[list[str], int]:
    """The numbers two or more tickets in one tracker share, and how many tickets were read.

    Per tracker and never across them, because numbering restarts in each: `06` names a file in
    several of them by design. The trackers are the ones a citation resolves in, read by trackers(),
    so this rule and the citation rule cannot disagree about which directories hold tickets. A
    file answers to a number when the citation rule's own lookup -- `NN-*` -- would land on it, so
    the files reported here are exactly the ones a citation of the number cannot choose between.

    The count is reported for the reason the other two are: a sentence printed whether or not any
    ticket was read is a sentence no test can hold to anything.
    """
    shared, read = [], 0
    for tracker in sorted(trackers(root)):
        issues = root / TRACKER_ROOT / tracker / ISSUES
        claimed: dict[str, list[str]] = {}
        for entry in issues.iterdir():
            found = NUMBERED.match(entry.name)
            if found:
                claimed.setdefault(found.group(1), []).append(entry.name)
        for number, names in sorted(claimed.items()):
            read += len(names)
            if len(names) > 1:
                shared.append(
                    f"{TRACKER_ROOT}/{tracker}/{ISSUES}/: {number} is claimed by "
                    + " and ".join(sorted(names))
                )
    return shared, read


def issue_citations(root: pathlib.Path) -> tuple[list[tuple[str, int, int]], list[str]]:
    """Every `#NNN` in the tracked files, as (path, line, number), and the TRANSCRIPTS entries that
    are no longer tracked.

    The scan is apart from the resolution because the two have different witnesses: the scan reads
    the tree and a test can hold it to a count, the resolution asks GitHub and a test must not. The
    transcripts are skipped by path, the one exemption the pattern could not make for itself.
    """
    tracked = tracked_files(root)
    cited = []
    for relative in tracked:
        if relative in TRANSCRIPTS:
            continue
        text = text_of(root, relative)
        if text is None:
            continue
        for citation in ISSUE_CITATION.finditer(text):
            line_number = text.count("\n", 0, citation.start()) + 1
            cited.append((relative, line_number, int(citation.group("number"))))
    untracked = [
        f"'{path}' is not tracked ({why})" for path, why in TRANSCRIPTS.items() if path not in tracked
    ]
    return cited, untracked


def issued_numbers(root: pathlib.Path) -> tuple[set[int], set[int], str]:
    """The numbers GitHub has issued to this repository's issues, and to its pull requests, or why
    they could not be read.

    Empty with a reason when gh is not on PATH, is not signed in, cannot reach GitHub or is refused
    by it. The caller prints the reason and fails nothing: a rule that failed every offline push
    would be a rule pushed past with --no-verify within the week, and the next push with a network
    checks what this one could not. Empty with no reason is an answer too -- a repository with no
    issues and no pull requests -- and every citation in it dangles, which is right. The cases on
    the check's rules replace this function, so none of them reaches GitHub; the cases on this
    function point GH_ISSUES at a stand-in for gh.
    """
    try:
        asked = subprocess.run(
            GH_ISSUES, cwd=root, capture_output=True, text=True, check=False, timeout=GH_TIMEOUT
        )
    except FileNotFoundError:
        return set(), set(), "gh is not on PATH"
    except subprocess.TimeoutExpired:
        return set(), set(), f"gh did not answer within {GH_TIMEOUT} seconds"
    if asked.returncode != 0:
        return set(), set(), first_complaint(asked)
    issues: set[int] = set()
    pulls: set[int] = set()
    for line in asked.stdout.splitlines():
        if not line.strip():
            continue
        number, _, kind = line.strip().partition(" ")
        if not number.isdigit() or kind not in ("issue", "pull"):
            return set(), set(), (
                f"gh answered with something other than numbers: {asked.stdout[:60]!r}"
            )
        (pulls if kind == "pull" else issues).add(int(number))
    return issues, pulls, ""


def first_complaint(asked: subprocess.CompletedProcess) -> str:
    """What gh said went wrong, in its first line of stderr.

    `gh: Bad credentials (HTTP 401)`, `error connecting to api.github.com`, `To get started with
    GitHub CLI, please run: gh auth login`: each says what a reader can act on, where the exit
    code alone would say nothing.
    """
    said = next((line.strip() for line in asked.stderr.splitlines() if line.strip()), "")
    return said or f"gh exited {asked.returncode} and said nothing"


def comment_url(root: pathlib.Path, comment: str) -> tuple[Comment | None, str]:
    """Where GitHub keeps the comment with this id on this repository and who wrote it; None when
    this repository has no comment under the id; or, with None, why GitHub could not be asked.

    One call per comment a held commit cites, which is a handful a run and usually none. A 404 is
    an answer, the one the rule fails on; anything else gh says is a reason the citation went
    unchecked, as issued_numbers() reports its own. The cases on the check's rules replace this
    function; the cases on this function point GH_COMMENT at a stand-in for gh.
    """
    command = tuple(part.replace("{comment}", comment) for part in GH_COMMENT)
    try:
        asked = subprocess.run(
            command, cwd=root, capture_output=True, text=True, check=False, timeout=GH_TIMEOUT
        )
    except FileNotFoundError:
        return None, "gh is not on PATH"
    except subprocess.TimeoutExpired:
        return None, f"gh did not answer within {GH_TIMEOUT} seconds"
    if asked.returncode != 0:
        said = first_complaint(asked)
        # gh's line for an id the repository has no comment under: `gh: Not Found (HTTP 404)`.
        return (None, "") if "(HTTP 404)" in said else (None, said)
    url, _, author = asked.stdout.strip().partition(" ")
    if not url:
        return None, "gh answered with no URL for the comment"
    return Comment(url, author.strip()), ""


def named_account(guide: str | None) -> str | None:
    """The account the tracker policy names on its "The human's account is `@<login>`." line, or
    None when it names none (or the tree has no guide)."""
    found = ACCOUNT_LINE.search(guide or "")
    return found.group("login") if found else None


def issue_kind(root: pathlib.Path, number: int) -> tuple[str | None, str]:
    """What GitHub says this one number is on this repository: "issue" or "pull"; None when it
    never issued the number; or, with None, why GitHub could not say.

    Asked only for a number an `Authority: #<n>` trailer cites and the listing lacks, which is
    none on a run whose listing came back whole. A 404 or a 410 is an answer, the one the rule
    refuses on; anything else gh says is a reason the trailer went unchecked, as comment_url()
    reports its own. The cases on the check's rules replace this function; the cases on this
    function point GH_ISSUE at a stand-in for gh.
    """
    command = tuple(part.replace("{number}", str(number)) for part in GH_ISSUE)
    try:
        asked = subprocess.run(
            command, cwd=root, capture_output=True, text=True, check=False, timeout=GH_TIMEOUT
        )
    except FileNotFoundError:
        return None, "gh is not on PATH"
    except subprocess.TimeoutExpired:
        return None, f"gh did not answer within {GH_TIMEOUT} seconds"
    if asked.returncode != 0:
        said = first_complaint(asked)
        # gh's line for a number the repository holds no issue under ends `(HTTP 404)`, or
        # `(HTTP 410)` for an issue deleted from it: GitHub answers a deleted issue with one or the
        # other by the reader's access alone, and the listing never held either, so both refuse
        # as a number missing from the listing did before #297.
        # https://docs.github.com/en/rest/issues/issues#get-an-issue
        gone = "(HTTP 404)" in said or "(HTTP 410)" in said
        return (None, "") if gone else (None, said)
    answered, _, kind = asked.stdout.strip().partition(" ")
    if answered != str(number) or kind not in ("issue", "pull"):
        return None, (
            f"gh answered #{number} with something other than its line: {asked.stdout[:60]!r}"
        )
    return kind, ""


def wanted_numbers(held: list[Held], listed: set[int]) -> list[int]:
    """The numbers the held commits' `Authority: #<n>` trailers cite and the listing lacks, each
    once, for issue_kind() to look up."""
    found = (ISSUE_AUTHORITY.fullmatch(value) for commit in held for value in commit.authorities)
    return sorted({int(match.group("number")) for match in found if match} - listed)


def dangling_issues(cited: list[tuple[str, int, int]], issued: set[int]) -> list[str]:
    """The `#NNN` citations that name no issue or pull request, in the order the tree holds them."""
    return [
        f"{relative}:{line_number}: cites #{number}, which names no issue or pull request of this "
        f"repository"
        for relative, line_number, number in cited
        if number not in issued
    ]


def git(root: pathlib.Path, *arguments: str) -> subprocess.CompletedProcess:
    """A git command in the repository, its output read as text whatever bytes a path holds."""
    return subprocess.run(
        ("git", *arguments), cwd=root, capture_output=True, encoding="utf-8", errors="replace",
        check=False,
    )


def resolved(root: pathlib.Path, revision: str) -> str | None:
    """The commit a revision names, or None when it names none -- a repository with no commit yet
    has no HEAD, and that is a range holding nothing rather than an error."""
    found = git(root, "rev-parse", "--verify", "--quiet", "--end-of-options",
                f"{revision}^{{commit}}")
    return found.stdout.strip() if found.returncode == 0 else None


def is_steering(path: str) -> bool:
    """Whether a path is a steering document: a STEERING_DOCUMENTS file, or under one of its
    directories."""
    return any(
        path == document or (document.endswith("/") and path.startswith(document))
        for document in STEERING_DOCUMENTS
    )


def steering_changes(root: pathlib.Path, sha: str) -> list[str]:
    """The steering documents a commit changes, less any change request it adds.

    Against the commit's parent, or the empty tree for a root commit, which is what --root asks
    for: a root commit is a change like any other. No rename detection, so a rename is the delete
    of one path and the add of another, and a change request renamed is a change request removed.
    NUL-separated, so a path is read as git stores it rather than as git would quote it.
    """
    found = git(root, "diff-tree", "--no-commit-id", "-r", "-z", "--no-renames", "--name-status",
                "--root", sha)
    fields = found.stdout.split("\0")
    return [
        path
        for status, path in zip(fields[0::2], fields[1::2])
        if is_steering(path) and not (status == "A" and path.startswith(CHANGE_REQUESTS))
    ]


def state_at(root: pathlib.Path, revision: str, path: str) -> str | None:
    """The first word of a change request's Status line at a revision, lower-cased, such as "open"
    or "decided"; UNREADABLE when its Status lines name different states, or when a line labels a
    Status that STATUS cannot read and none it can; "open" when no line names a Status at all; None
    when the file is absent there."""
    shown = git(root, "show", f"{revision}:{path}")
    if shown.returncode != 0:
        return None
    states = {found.group("state").lower() for found in STATUS.finditer(shown.stdout)}
    if len(states) == 1:
        return states.pop()
    if states:
        return UNREADABLE
    return UNREADABLE if STATUS_LABEL.search(shown.stdout) else "open"


def request_changes(root: pathlib.Path, sha: str) -> tuple[list[tuple[str, str]], list[str]]:
    """The change requests a commit sets to a decision, each with the state it now names, and the
    ones whose Status it leaves unreadable. A request is set to a decision when it is added naming
    any state but open, or edited to name one it did not name before, so an edit from `accepted` to
    `decided` is a decision as much as one from `open`; an edit that leaves the state as it was
    decides nothing new."""
    found = git(root, "diff-tree", "--no-commit-id", "-r", "-z", "--no-renames", "--name-status",
                "--root", sha)
    fields = found.stdout.split("\0")
    decides, unreadable = [], []
    for status, path in zip(fields[0::2], fields[1::2]):
        if status not in ("A", "M") or not path.startswith(CHANGE_REQUESTS) \
                or not path.endswith(".md"):
            continue
        state = state_at(root, sha, path)
        if state == UNREADABLE:
            unreadable.append(path)
        elif state not in (None, "open") and state != state_at(root, f"{sha}^", path):
            decides.append((path, state))
    return decides, unreadable


def held_commits(root: pathlib.Path, base: str | None) -> list[Held]:
    """The commits the authority rule holds, newest first: between base and HEAD, or every commit
    HEAD reaches when no base is named, that are not merges, were made from AUTHORITY_SINCE on, and
    change a steering document other than by adding an open change request. From DECIDED_SINCE on,
    a commit that sets a change request to any state but open is held too, adding it or not.

    The starting point is compared here, commit by commit, and not given to `git log --since`:
    git's manual says of `--since-as-filter` that it "visits all commits in the range, rather than
    stopping at the first commit which is older than <date>", which is what `--since` does, so a
    newer commit behind an older one, as a merge of older work puts it, would go unread.
    https://git-scm.com/docs/git-log
    """
    head = resolved(root, "HEAD")
    if head is None:
        return []
    revisions = f"{base}..{head}" if base else head
    log = git(root, "log", "-z", "--no-merges", "--no-show-signature", f"--format={LOG_FORMAT}",
              revisions, "--")
    if log.returncode != 0:
        raise SystemExit(f"steering: git log could not read {revisions}: {log.stderr.strip()}")
    since = AUTHORITY_SINCE.timestamp()
    decided_since = DECIDED_SINCE.timestamp()
    held = []
    for record in log.stdout.split("\0"):
        if not record:
            continue
        sha, made, subject, trailers = record.split("\x1f", 3)
        if int(made) < since:
            continue
        paths = steering_changes(root, sha)
        decides, unreadable = request_changes(root, sha) if int(made) >= decided_since else ([], [])
        if not paths and not decides and not unreadable:
            continue
        authorities = tuple(value.strip() for value in trailers.split("\x1e") if value.strip())
        named = tuple(paths) + tuple(path for path, _ in decides if path not in paths)
        held.append(Held(sha, subject, named, authorities, tuple(decides), tuple(unreadable)))
    return held


def unreadable_statuses(held: list[Held]) -> list[str]:
    """The change requests a held commit leaves with a Status the decision rule cannot read."""
    return [
        f'commit {commit.sha[:12]} ("{commit.subject}") leaves {path} with a Status this check '
        "cannot read"
        for commit in held
        for path in commit.unreadable
    ]


def wanted_comments(held: list[Held]) -> list[str]:
    """The comment ids the held commits cite, each once, for comment_url() to look up."""
    found = (COMMENT_AUTHORITY.fullmatch(value) for commit in held for value in commit.authorities)
    return sorted({match.group("comment") for match in found if match})


def judged(
    authority: str,
    issues: set[int],
    pulls: set[int],
    numbers_unread: str,
    looked_up: dict[int, tuple[str | None, str]],
    comments: dict[str, tuple[Comment | None, str]],
    account: str | None = None,
) -> tuple[str, str]:
    """What one Authority: value comes to: ("vouched", ""), ("refused", why it resolves to
    nothing), ("unconfirmed", who wrote the comment it cites, when that is not the account that
    confirms), or ("unchecked", why GitHub could not say). account is the one the tracker policy
    names, None when it names none, which leaves the repository's owner.

    A number the listing lacks is judged on what issue_kind() said of it alone, in `looked_up`:
    the listing may have stopped short, so its silence is not GitHub's 404."""
    issue = ISSUE_AUTHORITY.fullmatch(authority)
    if issue:
        if numbers_unread:
            return "unchecked", numbers_unread
        number = int(issue.group("number"))
        if number in issues:
            kind, why = "issue", ""
        elif number in pulls:
            kind, why = "pull", ""
        else:
            kind, why = looked_up[number]
        if why:
            return "unchecked", why
        if kind == "issue":
            return "vouched", ""
        if kind == "pull":
            return "refused", "names a pull request, not an issue"
        return "refused", "names no issue of this repository"
    comment = COMMENT_AUTHORITY.fullmatch(authority)
    if comment:
        kept, why = comments[comment.group("comment")]
        if why:
            return "unchecked", why
        if kept is None:
            return "refused", "names no comment on this repository"
        # GitHub's owner and repository names are case-insensitive, so the URL is compared so.
        if kept.url.casefold() != authority.casefold():
            where = f"is not where GitHub keeps comment {comment.group('comment')}: {kept.url}"
            return "refused", where
        if account:
            source = f"the account {GUIDE} names for a Confirm"
        else:
            owner = URL_OWNER.match(kept.url)
            account = owner.group("owner") if owner else ""
            source = f"the repository's owner, since {GUIDE} names no account"
        # Logins are case-insensitive too.
        if not account or kept.author.casefold() != account.casefold():
            who = f"@{kept.author}" if kept.author else "an account GitHub no longer names"
            return "unconfirmed", f"is a comment by {who}, not by @{account}, {source}"
        return "vouched", ""
    return "refused", "is neither #<n> nor the URL of a comment on this repository"


def unauthorised(
    held: list[Held],
    issues: set[int],
    pulls: set[int],
    numbers_unread: str,
    looked_up: dict[int, tuple[str | None, str]],
    comments: dict[str, tuple[Comment | None, str]],
    account: str | None = None,
) -> tuple[list[str], list[str], list[str], int, list[str], list[str]]:
    """The held commits naming no authority, the authorities that resolve to nothing, why each
    authority GitHub could not be asked about went unchecked, how many held commits GitHub
    vouched for every authority of, the commits that decide a change request on no confirmed
    answer's URL, and the authorities citing a comment someone other than the account wrote.

    Every authority a commit names is judged, not the first that resolves: a second trailer citing
    nothing is a pointer at nothing, which every other rule here fails.
    """
    missing, refused, unchecked, vouched, undecided, unconfirmed = [], [], [], 0, [], []
    for commit in held:
        if not commit.paths:
            continue  # held only for a Status it cannot read, which unreadable_statuses() says
        named = f'commit {commit.sha[:12]} ("{commit.subject}") changes {", ".join(commit.paths)}'
        if not commit.authorities:
            missing.append(f"{named} and carries no {AUTHORITY}: trailer")
            continue
        answered = any(COMMENT_AUTHORITY.fullmatch(value) for value in commit.authorities)
        if commit.decides and not answered:
            sets = ", ".join(f"{path} to {state}" for path, state in commit.decides)
            undecided.append(
                f"{named}, sets {sets}, and cites no confirmed answer: "
                f"{AUTHORITY}: {', '.join(commit.authorities)}"
            )
        verdicts = [
            (authority,
             *judged(authority, issues, pulls, numbers_unread, looked_up, comments, account))
            for authority in commit.authorities
        ]
        for authority, verdict, why in verdicts:
            if verdict == "refused":
                refused.append(f"{named}, and {AUTHORITY}: {authority} {why}")
            elif verdict == "unconfirmed":
                unconfirmed.append(f"{named}, and {AUTHORITY}: {authority} {why}")
            elif verdict == "unchecked":
                unchecked.append(why)
        if all(verdict == "vouched" for _, verdict, _ in verdicts):
            vouched += 1
    return missing, refused, unchecked, vouched, undecided, unconfirmed


def report(lines: list[str], headline: str, *advice: str) -> bool:
    """Print one rule's failures under its own headline, and say whether there were any.

    Every rule here reports the same way -- each failing line indented, then the headline, then
    what to do about it -- and before this was one function it was the same three statements
    written out per rule. The fourth rule made that seven copies, and a fifth would edit a count,
    a condition and a block in three places.
    """
    for line in lines:
        print(f"  {line}")
    if not lines:
        return False
    print()
    print(headline)
    for line in advice:
        print(line)
    return True


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog=".github/steering.py",
        description="Check the documents a session steers by, and the commits that change them.",
    )
    parser.add_argument(
        "--base", metavar="REF",
        help="judge the commits between REF and HEAD, as tools/check.sh and CI name it; without "
             "it, every commit HEAD reaches",
    )
    arguments = parser.parse_args(sys.argv[1:] if argv is None else argv)
    root = pathlib.Path(__file__).resolve().parent.parent

    # A base the caller names must resolve, as tools/check.sh's must: a typo there would otherwise
    # judge nothing and pass.
    base = None
    if arguments.base is not None:
        base = resolved(root, arguments.base)
        if base is None:
            parser.error(f"no commit {arguments.base} to judge the commits against")

    claims = progress_claims(root)
    missing, landed = dangling_pointers(root)
    disagreements, unreadable, headerless, nowhere, uncompared, compared = (
        freeze_disagreements(root)
    )
    unresolved, filed, resolved_tickets = dangling_tickets(root)
    shared, numbered = shared_numbers(root)
    cited, untracked = issue_citations(root)
    held = held_commits(root, base)
    # One listing per run, and none when nothing cites: a tree with no `#NNN` and a range with no
    # `Authority: #<n>` owe GitHub no call.
    cites_issues = cited or any(
        ISSUE_AUTHORITY.fullmatch(value) for commit in held for value in commit.authorities
    )
    issues, pulls, unchecked = issued_numbers(root) if cites_issues else (set(), set(), "")
    unissued = [] if unchecked else dangling_issues(cited, issues | pulls)
    # A number a trailer cites and the listing lacks is asked about alone before it is refused,
    # since the listing can come back short (#297); none is asked when the listing itself failed,
    # whose reason already leaves every such trailer unchecked.
    looked_up = {} if unchecked else {
        number: issue_kind(root, number) for number in wanted_numbers(held, issues | pulls)
    }
    comments = {comment: comment_url(root, comment) for comment in wanted_comments(held)}
    # The account is read from the guide as it stood at the base, not from the tree being judged:
    # a range that renamed the account in the guide would otherwise vouch its own steering commits,
    # the guide's renaming among them, on comments the new account wrote (#316 review). With no
    # base, the whole history is judged and the tree is the only guide there is.
    if base is None:
        guide = root / GUIDE
        account = named_account(guide.read_text() if guide.is_file() else None)
    else:
        # base resolved above, so a failure here is a guide the base does not have.
        shown = git(root, "show", f"{base}:{GUIDE}")
        account = named_account(shown.stdout if shown.returncode == 0 else None)
    nameless, refused, unverified, vouched, undecided, unconfirmed = unauthorised(
        held, issues, pulls, unchecked, looked_up, comments, account
    )
    unread = unreadable_statuses(held)

    # A list and not a chain of `or`, because every rule reports before any of them decides the
    # exit code: a session fixing one failure should see the others in the same run.
    failures = [
        report(
            claims,
            "STEERING FAILED: AGENTS.md and CLAUDE.md state progress.",
            "They are loaded into every session from the top of the window, so a stale claim",
            "misleads from the first token. Point at the source instead: git tag, git log, the",
            "dispatch table, the gates table in docs/plan/01-plan.md.",
        ),
        report(
            missing,
            "STEERING FAILED: a pointer names something that does not exist.",
            "Write the target, fix the pointer, or add the path to PLANNED in this script with",
            "the reason it is named early.",
        ),
        report(
            landed,
            "STEERING FAILED: a PLANNED path has landed.",
            "Remove it from PLANNED in this script, so the list never outlives its entries.",
        ),
        report(
            disagreements,
            "STEERING FAILED: the contracts index and a contract's own freeze header disagree.",
            "One of the two is wrong, and a freeze cell is frozen state: decide which side the",
            "gate actually froze at, and correct that side through a change request under",
            "contracts/changes/ rather than editing the other to match.",
        ),
        report(
            unreadable,
            "STEERING FAILED: the contracts index has a row this rule cannot read.",
            "Its cells do not line up with the columns above it, so nothing compared its freeze",
            "claim against the contract it points at. Fix the row, or the headings it sits",
            "under; a pipe inside a cell is written \\|.",
        ),
        report(
            headerless,
            "STEERING FAILED: a contracts index target has no readable freeze header.",
            "A row's freeze cell is compared against each target's own header, so a target that",
            "does not exist, a markdown contract with no '- **Freezes at:**' bullet naming a gate,",
            "a schema stating no freeze, or JSON that does not parse is a claim nothing compared.",
            "Fix the path or write the header. Code, a directory and a schema's example carry no",
            "header by design and are not read.",
        ),
        report(
            nowhere,
            "STEERING FAILED: a freeze cell names neither a gate nor a row of the contracts index.",
            "A cell naming no gate defers to the row it names -- 'With the registry document' --",
            "and is compared through that row's gate, so a name no row carries is a claim nothing",
            "compares. Name a gate, or a row naming one as its Contract cell spells it. A row",
            "whose cell defers to no single row goes in UNCOMPARED in this script, keyed by the",
            "row and its cell, with the reason.",
        ),
        report(
            uncompared,
            "STEERING FAILED: an UNCOMPARED cell is in no row that needs it.",
            "Remove it from UNCOMPARED in this script, so the list never outlives its entries.",
        ),
        report(
            unresolved,
            "STEERING FAILED: a cited ticket number resolves to no file.",
            "A citation resolves against one tracker directory: the one the citing file sits in,",
            f"or {TRACKER_ROOT}/{DEFAULT_TRACKER}/{ISSUES}/ for a file outside {TRACKER_ROOT}/.",
            "Write '<tracker> ticket NN' to cite across trackers, file the ticket, or add it to",
            "UNFILED in this script with the reason it is named early. The resolution rule is",
            "in docs/agents/tracker-policy.md.",
        ),
        report(
            filed,
            "STEERING FAILED: a ticket on UNFILED has been filed.",
            "Remove it from UNFILED in this script, so the list never outlives its entries.",
        ),
        report(
            shared,
            "STEERING FAILED: two tickets in one tracker share a number.",
            "A citation of the number cannot say which it means. Renumber the ticket that",
            "reached origin/main second to the next free number in its directory, and move",
            "every citation that meant it in the same commit. Read each one: after the rename",
            "the number still resolves, to the other file, so no check finds one left behind.",
            "The archive's rules are in docs/agents/tracker-policy.md.",
        ),
        report(
            unissued,
            "STEERING FAILED: a cited #NNN names no issue or pull request.",
            "Issues and pull requests share one number sequence, and this run asked GitHub for",
            "every number it has issued here. Fix the number, or write another repository's as",
            "owner/repo#NNN, which this rule does not read. The form is in",
            "docs/agents/tracker-policy.md.",
        ),
        report(
            untracked,
            "STEERING FAILED: a TRANSCRIPTS path is not tracked.",
            "Remove it from TRANSCRIPTS in this script, so the list never outlives its entries.",
        ),
        report(
            nameless,
            "STEERING FAILED: a commit changes a steering document and cites no authority.",
            "ADR-0023 decision 3: the commit names what authorises the change, in an Authority:",
            "trailer on a line of its own in the message's last paragraph, beside any other",
            "trailers. Authority: #<n> names the ticket whose acceptance criterion asks for the",
            "change; Authority: <URL> names the comment on this repository holding a confirmed",
            "answer. Amend the message to add one; docs/agents/landing-work.md has the forms.",
            "Adding an open change request under contracts/changes/ needs none.",
        ),
        report(
            undecided,
            "STEERING FAILED: a commit sets a change request to a decision on no confirmed answer.",
            "ADR-0021 decision 4: a change request's status line is committed on a confirmed",
            "answer, so the commit that says decided names the comment holding it, in an",
            "Authority: <URL> trailer. A #<n> alone does not decide a request: a ticket asking a",
            "lane to record a decision is a lane deciding. Any Status but open is a decision,",
            "accepted as much as decided. Leave the Status open and stop, or cite the human's",
            "answer.",
        ),
        report(
            unread,
            "STEERING FAILED: a change request's Status cannot be read.",
            "The decision rule reads a Status written `- **Status:** open` or `- **Status:**",
            "decided, <date>. ...`, as every change request has written it. Another shape could",
            "say decided, and two Status lines naming different states cannot say which holds, so",
            "either is held rather than read as open, whatever the Authority: trailer cites. Write",
            "one Status line, in one of those two shapes, and edit it in place to decide.",
        ),
        report(
            refused,
            "STEERING FAILED: an Authority: trailer names no issue and no comment on this "
            "repository.",
            "A #<n> names an issue, not a pull request, and a URL names a comment as GitHub links",
            "it: https://github.com/<owner>/<repo>/issues/<n>#issuecomment-<id>. Fix the trailer;",
            "whether what it cites decides the change is the reviewer's call, not this check's.",
        ),
        report(
            unconfirmed,
            "STEERING FAILED: an Authority: comment was written by an account that does not "
            "confirm.",
            "A Confirm comment is the human's act (docs/agents/tracker-policy.md), and",
            "CODING_STANDARDS.md -> \"An authorisation is read from its author\" counts one only",
            "when the account the rule names wrote it: the account docs/agents/tracker-policy.md",
            "names, or the repository's owner while it names none. Cite that account's comment",
            "holding the confirmed answer, or leave the steering document as it was.",
        ),
    ]

    if unchecked and cited:
        # Said whether or not the other rules passed, for the reason the counts are printed: a run
        # that could not ask GitHub must not look like one that did. Only when the tree cites a
        # `#NNN`: the lookup may have been for an Authority: trailer alone, said below.
        citations = "citation" if len(cited) == 1 else "citations"
        print(f"steering: {len(cited)} #NNN {citations} not checked: {unchecked}.")

    if unverified:
        citations = "citation" if len(unverified) == 1 else "citations"
        print(f"steering: {len(unverified)} {AUTHORITY}: {citations} not checked: "
              f"{unverified[0]}.")

    if any(failures):
        return 1

    summary = f"steering: {', '.join(FILES)} claim no progress and every pointer resolves."
    if compared:
        headers = "freeze header" if compared == 1 else "freeze headers"
        summary += f" {INDEX} agrees with the {compared} {headers} it indexes."
    if resolved_tickets:
        citations = "citation resolves" if resolved_tickets == 1 else "citations resolve"
        summary += f" {resolved_tickets} ticket {citations} to a spec."
    if numbered:
        tickets = "ticket holds" if numbered == 1 else "tickets each hold"
        summary += f" {numbered} {tickets} a number no other ticket in its tracker shares."
    if cited and not unchecked:
        citations = "citation names" if len(cited) == 1 else "citations name"
        summary += f" {len(cited)} #NNN {citations} an issue or pull request."
    if vouched:
        commits = "commit changing" if vouched == 1 else "commits changing"
        cite = "cites its" if vouched == 1 else "cite their"
        summary += f" {vouched} {commits} a steering document {cite} authority."
    print(summary)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
