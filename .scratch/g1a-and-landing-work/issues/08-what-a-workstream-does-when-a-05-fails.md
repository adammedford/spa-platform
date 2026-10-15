# What a workstream does when A-05 fails, and how wide its escape hatch is

Type: grilling
Status: resolved

## Question

Two of the map's fog patches, which it suspected were one question. Ticket 04 settled the briefing
unit and the landing unit, which makes them specifiable, and confirms they are indeed one question.

**The procedure.** A-05 fails a PR that touches paths outside one owner's set. What the workstream
then does is unwritten: split the PR, ask for a declaration it is not allowed to make, or file a
change request. Note `.github/path-ownership.py`'s `ALWAYS_ALLOWED = ("contracts/changes/",)` — a
change request rides along in any workstream's PR **without** tripping A-05, which narrows the
question: the escape route is already open and costs nothing.

**The escape hatch is wider than the map settled.** Found by `/code-review` on PR #1:

- `declaration()` greps the whole `merge-base..head` range, so **one trailer licenses every
  cross-owner commit in the PR**. Ticket 04's decision that one PR lands one row bounds the blast
  radius but does not close it.
- The check only tests that the trailer *says* `integrator`. Nothing verifies who wrote it.
- Any non-empty reason passes, though the map's Settled table requires it to cite a task id.

**And it is untested.** The I-5 row makes A-05 a "done when", and `AGENTS.md` says that must be
proven in the owner's own test project, but the six scenarios were run by hand in a throwaway clone.
Ticket 05 accepted that hand run as G1a's evidence for item 5 *and recorded the gap*, pointing at
`docs/plan/01-plan.md:235`, which puts A-01 to A-05 in **G2's** exit test. So this ticket decides
whether the test arrives before G2 or the gap stands as G1a recorded it.

## Answer

Twenty-four decisions over five rounds. Three findings came from probing the repository rather than
from reasoning, and each changed an answer that had already been accepted.

### Findings

1. **Not one of the five declarations in `main` is a git trailer.** Each is followed by a blank line
   and then `Co-Authored-By:`, so git's trailer block -- the last paragraph -- holds only the
   co-author line. `git interpret-trailers --parse` returns no `Path-Ownership` for 91e2103,
   27d6b64, 536ab68 or 9399f75. The docstring, the map's Settled table and ticket 11 all call it a
   "commit trailer" for something git does not parse as one.

2. **The truncation 357a475 claimed to fix is still live.** Running the current regex over the real
   commits, 91e2103 -- written *after* that retrospective -- yields
   `'(task I-3 and I-5 review fixes: Platform.Abstractions'`: cut mid-clause, paren unbalanced. The
   fix taught the regex to follow *indented* continuations; the author wrapped unindented. It
   covered a shape nobody writes, and the one declaration since has the defect it was written for.

3. **33eb5fb is a cross-owner merge in `main` with no trailer in its own body.** It merged I-3, I-4
   and I-5; I-4 seeds stubs in all six workstream projects, so its diff spans owners. Only the
   branch tip 91e2103 carries a declaration, and that branch is deleted. It passed because the old
   rule greps the whole range.

Also verified: the tip rule is **forward-only by construction** -- every invocation diffs a range
whose base is already in `main`, so a past merge is never the tip again, and no history goes red.
All five existing declarations contain `I-3`, `I-4` or `I-5`, so the task-id rule would have passed
them. Every file this work touches resolves to `integrator`, including `tools/preflight.sh` and
`CODEOWNERS`, so the handoff needs no declaration of its own.

### 1. The procedure: two branches, not three

A-05 fails only when the diff spans two owners, and under ticket 04 one PR lands one task for one
owner. So there are two causes, not the three this ticket assumed:

- **An accidental touch.** Revert the stray file. Done.
- **The task genuinely needs an integrator path changed.** Write
  `contracts/changes/<task-id>-<slug>.md`, which `ALWAYS_ALLOWED` lets ride along free, merge what
  you have, declare the request open in the merge body, and stop the task. Precedent: tasks 1.0 and
  1.6a.

The third case -- "the task needs another workstream's path" -- **does not exist**. A-03 says a
workstream project references `Platform.Abstractions` and no other workstream's project, so a
legitimate need for another workstream's code is by construction a need for an Abstractions change,
which is an integrator path and therefore the second branch already. What remains is a misread of
the plan or a row taken out of order, and neither wants a route; both want the task stopped.

This also keeps `GLOSSARY.md` honest. A change request is defined as the way a workstream asks for a
change to an **integrator** path. Routing "please edit `Platform.Registry`" through
`contracts/changes/` would have widened the term to "any cross-owner ask" and forced the glossary to
loosen. It does not have to, and the entry stands unedited.

**A workstream never requests a declaration.** The trailer is not a favour the integrator grants on
request; it covers only work the integrator is itself doing. Stating this is the point, because
otherwise the hatch widens socially however tight the code is. `landing-work.md` says so, and names
the change request as the reason it can.

`ALWAYS_ALLOWED` stays a bare path prefix with no filename check. The exempted files are inert --
not in the solution, read by nobody but the integrator, who reads every change request anyway -- so
the worst case is an untidy directory, and a filename regex would be a second place task ids are
parsed under a different grammar from the one below, guarding nothing. One sentence in the docstring
records that the carve-out is a prefix by choice.

### 2. The hatch: narrowed on four axes, one left open deliberately

**The grammar is git's.** `git interpret-trailers --parse` replaces the hand-rolled regex, which is
deleted. Five of five declarations are malformed and nothing noticed, which is 357a475's own lesson
pointed back at it: it invented a grammar and then tested the invention against shapes it had
imagined. Git's parser gives the repository exactly one definition of "trailer" -- last paragraph,
indented continuations folded -- and ticket 11 inherits it for `Outstanding:` and
`Outstanding test ids:` instead of inventing a second. Verified: an indented continuation folds to
one line; a blank line before `Co-Authored-By:` drops the declaration entirely, reproducing the
5-of-5 failure live. The cost is that the declaration must share the final paragraph with
`Co-Authored-By:` -- the exact mistake made every time so far, which argues for the check rather
than against it, provided the failure message prints a literal correct example. The key matches
case-insensitively, because rejecting a correct declaration over capitalisation costs a blocked
merge while accepting one costs nothing; every example and message uses `Path-Ownership:`.

**The scope is the tip commit.** The old `declaration()` greps `merge-base..head`, so a trailer on
commit 1 licenses commit 9 -- and the failure that bites is a long-lived claim branch, which ticket
07 makes normal, where an early legitimate declaration silently covers a later accidental reach.
Per-commit evaluation was rejected as stricter but weaker where it counts: a PR whose commits are
individually single-owner but collectively span two owners is exactly what A-05 exists to catch, and
per-commit alone passes it.

**It is therefore written twice.** The three invocations have different tips: pre-push and the PR
job see the branch tip, the push-to-main job sees the merge commit. Ticket 11 needs the merge
commit's own message, first parent, because the branch is deleted and that is the permanent record.
Requiring it at the tip of whatever range is under test is the only arrangement where both the gate
and the record work. The duplication is small -- this fires on integrator work only, rare by
construction -- and the merge body is named the canonical record in `landing-work.md`.

Unverified risk, named rather than assumed: GitHub's merge-commit body may prefill the branch
commits as a bulleted list, which would make the line `* Path-Ownership: ...` and parse as no
trailer. If so the check fails loudly rather than passing wrongly, so it is self-correcting, but the
first integrator merge after this lands is the test.

**The reason must cite a task id.** The map's Settled table already decided this; the code accepted
any non-empty string, so this implements a settled rule. The shape is `\b(I-\d+|\d+\.\d+[a-z]?)\b`,
covering both forms the plan uses, and the failure message names both. Integrator work with no plan
row is refused, which is correct: it is a signal the work wants a task row or wants splitting.

**The id is not checked against the plan.** `docs/plan/` is a frozen export, and parsing a frozen
markdown table would couple the only mechanical guard to that document's formatting -- the exact
drift class 357a475 fixed by deriving one side from the other, except that here there is nothing to
derive from. A typo'd id still leaves an auditable record a human resolves, which is what the
citation is for. Recorded as deliberate in the docstring so the next reviewer does not file it as a
gap.

**Who wrote it stays unverifiable, and is recorded rather than fixed.** The check tests that the
trailer says `integrator`; under one human author nothing can test more. The map already concluded
author-based ownership is dead under one author, and ticket 01 rewrote history to a single noreply
address, so even the author field carries no signal. This is the model's honest floor, and the
precedent is ticket 03: the 26 errors stand because they are true. It sits in `landing-work.md`
beside the review carve-out already named as the weakest link.

**A missing `python3` fails instead of warning.** The widest hatch was never the trailer:
`.githooks/pre-push` skipped **both** A-05 and the steering check with a warning when `python3` was
absent, and neither `tools/preflight.sh` nor `Brewfile` named Python at all. On a workstation
without it the only local teeth were silently gone, announced in a line the session scrolls past.
Both branches now `fail`. The measured workstation runs 3.9.6, the Xcode CLT build, so it arrives
with the `xcode-select --install` preflight already prescribes for git -- the hole is narrower than
it looked, but the hook exists to catch things before the push. `NEED_PYTHON="3.9"` joins the
day-one section, with a line in `docs/dependencies.md` naming the two checks as the reason. Both
scripts are 3.8-compatible, so the floor has room.

**33eb5fb is left alone.** `main` is protected against force-pushes and the history was already
rewritten once for the email; backfilling a trailer would be a far larger risk than the thing it
documents, and the merge is *correct* -- I-4 genuinely spans owners and the branch tip says why. It
goes in ADR-0014 as the worked example of why the rule changed: the old range-wide match let the
only cross-owner merge in the repository land with nothing in the permanent record. Ticket 11 asks
the general version of this for the other merge-body parts; this settles the declaration's case
only, and 11 inherits the precedent rather than the question.

### 3. The test: `tests/RepositoryChecks.Tests`, before `skeleton-v1`

Deferring to G2 was defensible -- the plan puts A-01 to A-05 in G2's exit test and ticket 05
accepted the hand run as G1a evidence while recording the gap -- and it is rejected anyway. Three
of the decisions above change this script; A-05 is the only mechanical guard on the entire
six-workstream model; and it has now been found wrong **three** times by review rather than by test
(the shadowing in ticket 03, the truncation in 357a475, and the truncation again here, still
unfixed). Handing an untested, just-rewritten guard to six concurrent sessions is the worst possible
moment.

A new integrator-owned project, in the solution so `dotnet test --solution` runs it -- the one
command `AGENTS.md` names. A pytest file under `.github/` would need a new CI step and a new tool
and would run nowhere that matters. `MSBuildProjectName.EndsWith('Tests')` is verified to make it an
xUnit v3 executable with no csproj boilerplate. It needs **no** `CODEOWNERS` entry: the path already
falls to the catch-all, and adding a line would imply the record needs one per path when its design
is a catch-all plus six exceptions. It covers `steering.py` from the start rather than as an
afterthought, since that script is equally untested. It **fails** rather than skips when `python3`
is absent -- a skipping test for the guard the hook now hard-fails on would be the same silence in a
new place.

Twelve cases, each building a throwaway repository in a temp directory:

| | Case |
| --- | --- |
| pass | single-owner diff |
| pass | `contracts/changes/` only |
| pass | cross-owner with a conforming tip-commit trailer |
| pass | **the literal message of 91e2103**, asserting the whole reason survives |
| fail | cross-owner, no trailer |
| fail | a trailer naming something other than `integrator` |
| fail | an empty reason |
| fail | a reason with no task id |
| fail | a trailer present but not in the tip commit |
| fail | a trailer above the trailer block, after a blank line |
| fail | a path no `CODEOWNERS` line matches |
| steering | one progress claim, one dangling pointer |

The fourth case replaces the imagined "indented continuation" case: under git's parser that would
test git rather than us, and the case that would actually have caught finding 2 is the real commit.
Both new rules are proved red before green, the discipline 357a475 set for itself, and the file
header records why one case quotes a commit by sha.

### Consequences for the gate and for resolved tickets

G1a item 5 cites the test instead of the hand-run transcript, keeping **all five** of ticket 05's
evidence fields -- behaviour, run URL, head sha, job and step name, quoted decisive line. A test-
backed item fits them without loosening, and a shorter field set would make `docs/gates/README.md`
carry two conventions across seven gates when most later items will be test-backed.

It names **A-05 alone**. All four `tests/Gateway.*` projects are still empty (4 of 16 test projects
hold any tests; `RepositoryChecks.Tests` becomes the fifth), so A-01 to A-04 run nowhere and stay in
G2's exit test. Without that clause the natural reading of "item 5 now cites a test" is that the
architecture rules are covered.

**ADR-0014 widens rather than gaining an ADR-0015.** Ticket 03 queued it for "why `CODEOWNERS`
carries 26 deliberate errors"; the unverifiable declaration has the same root cause -- one account,
no resolvable handles, author-based ownership dead. Two ADRs would split one decision across two
files and leave each half a story. Retitled to cover path ownership under a single account, it
records that the handles do not resolve and that is true rather than broken, that the check reads
the file as text, that the declaration names a role nothing can verify, and that the mitigations are
a task id, the tip-commit scope and the log -- with 33eb5fb as the worked example. It also keeps the
destination at six artifacts rather than seven.

**`GLOSSARY.md` gains one term**, *Path-Ownership declaration*, in "Work, gates and evidence":
implementation-free, meaning git's definition of a trailer. Deliberately not a term: "escape hatch",
which is how these tickets talk about the thing, not a word in the domain. No other entry changes.

**Tickets 03 and 05 take addenda, neither reopened.** The precedent is ticket 06, reversed by ticket
01 via an addendum with `Status: resolved` untouched. Reopening 05 would also break the frontier
rule, since 09 depends on it and is already unblocked.

### What this ticket does not do

Nothing is written. The map is explicit that every artifact is written at handoff and that ticket 10
decides the order; these decisions touch thirteen files across six artifacts and two script
rewrites, three of them forced into shared commits by the steering check. Writing now would be
writing the commits in the order they were thought of.
