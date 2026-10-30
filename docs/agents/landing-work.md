# Landing work

How a task gets from the dispatch table into `main`. One page, because the process is not the
product.

## Take a task

The dispatch table in `docs/plan/03-workstreams-and-tasks.md` says what is open. A task's `Needs`
column names gates and other tasks: a gate is met when `git tag` shows it, another task is met when
`git log --oneline` shows a merge naming its id.

**The branch is the claim.** Git refuses a second branch of the same name, so nothing else is
needed:

```bash
git worktree add -b task/<id>-<slug> ../spa-<id> origin/main
git config core.hooksPath .githooks   # once per clone, not per worktree
```

**The main checkout is shared.** Concurrent sessions run in the clone's own working tree, and its
branch is whichever one a session last left it on, so a commit, rebase or switch made there acts on
another session's work. A session never moves the main checkout's branch, rewrites its history or
changes its files or index: its own branch lives in its own worktree, made as above. The one
exception is a fast-forward of a clean `main` to what has landed, which leaves the checkout no
longer behind origin/main after a landing: `git fetch origin`, then exactly `git merge --ff-only
origin/main`, `--quiet` or `-q` allowed, while HEAD is `main` and no tracked file has changes
(untracked files are allowed). `git pull` is not that fast-forward. While HEAD is `main`, a tracked
file whose changes equal its origin/main content, with nothing staged, may first be restored with
`git restore <path>` or `git checkout -- <path>`, as a call of its own, since once `main` is
fast-forwarded that restore has changed nothing (#376). In a Claude Code session started in a
checkout that holds it, the tracked `.claude/settings.json` runs `tools/main_checkout_guard.py`
before each Bash call, and in the main checkout it refuses the branch-moving git commands and those
that change files or the index (`git rm`, `mv`, `add`, `restore`, `clean`, `apply`, and `git
checkout -- <path>`), admits that one fast-forward and that one restore, and names this route (#114,
#200). A person's terminal runs no such hook.

The same file sets `CLAUDE_CODE_SHELL=/bin/bash`, which a Claude Code process reads when it starts:
after a change to `.claude/settings.json` or after trusting a checkout, restart Claude Code (the
editor, for the extension), and confirm with `ps -p $$ -o comm=` in the Bash tool, which should
print `/bin/bash` (#183).

## Build it

Write only inside your owner's paths. `OWNERSHIP.md` lists them, and `.githooks/pre-push` fails a
push that reaches outside one owner's set. When it fires, either the change went somewhere it did
not mean to, or it needs an integrator path.

**Needing an integrator path** — a contract, a package version, a new project, anything in
`src/Platform.Abstractions` — means filing a change request and stopping the task:
`docs/agents/tracker-policy.md` → "Change requests".

Needing *another workstream's* path means something has gone wrong: A-03 says a workstream project
references `Platform.Abstractions` and no other workstream's project, so a real need for another
workstream's code is a need for Abstractions, which is the case above. Otherwise the row was taken
out of order.

**A commit that changes a steering document names its authority** (ADR-0023). The steering
documents are `GLOSSARY.md`, `AGENTS.md`, `CLAUDE.md`, `OWNERSHIP.md`, `CODING_STANDARDS.md`,
`docs/agents/`, `docs/adr/` and `contracts/`, except `docs/agents/issue-tracker.md`,
`docs/agents/triage-labels.md` and `docs/agents/aws-rules.md`, which a setup skill rewrites when it
is run again (#411, #412). `docs/agents/domain.md` stays in the set while it holds a rule of this
repository's. The commit's message ends with a trailer in one of two forms, on a line of its own in
the last paragraph, beside any other trailers:

```
Authority: #<n>     the ticket whose acceptance criterion asks for the change,
                    a ticket the human wrote or approved
Authority: <URL>    the comment on this repository that holds a confirmed answer, as GitHub
                    links it: https://github.com/<owner>/<repo>/issues/<n>#issuecomment-<id>
```

A change request, or a frozen contract's sentence, lands only on a decision comment's URL or on an
integrator ticket the human labelled `ready-for-agent`; which `#<n>` the check refuses, and why:
`docs/agents/tracker-policy.md` → "Change requests".
A pre-accepted answer, a follow-up a night raised, an archived ticket's proposal and a reviewer's
finding are not authority. `git commit --amend --no-edit --trailer "Authority: #<n>"` adds the
trailer to the last commit. git reads trailers from the message's last paragraph alone, and an
`Authority:` line inside the prose is not one.

`.github/steering.py` holds this, in the pre-push hook and in CI, on every branch (#102). It reads
each non-merge commit between the base the check judges against and HEAD, made since the starting
points the script records, and fails one that changes a steering document with no `Authority:`
trailer, a `#<n>` that names no issue (a pull request's number included) or names a change request,
or a URL that is not a comment on this repository, naming the commit and the paths. A merge needs
no trailer. A file added under `contracts/changes/`, the read-only archive of requests filed before
they were issues, fails whatever its trailer cites; editing or removing one needs a trailer like any
other steering change. Whether what the trailer cites decides the change is the reviewer's call, not
the check's.

## Land it

One branch per task, one pull request, merged with a merge commit. CI must be green.

The merge subject names every task id it lands. Two forms, because a row can outlive one session:

```
Merge task(s) <id...>: <what landed>      the row is landed (landed is not done)
Merge part of task <id>: <what landed>    more of the row to come, on this branch or a part ticket's
```

The subject is read by a check: `tools/roadmap.py` derives the roadmap's state from these
subjects, so "Merge task 2.4: ..." lands the row and "Merge part of task 2.4: ..." does not. Run the
script before the push and commit what it writes, or `.githooks/pre-push` will say so.

**Two halves that must land together land as one combined branch**
(`docs/agents/tracker-policy.md` rule 4). Each half commits on its own owner's branch, and neither
is pushed alone when it fails the check without the other: on 2026-10-29 #504's half failed
`.github/steering.py` on its own, because
`AGENTS.md` names the `docs/gaps.md` that only #503 writes, so the halves reached `origin` only
combined (#568). The combined branch is `task/<a>-and-<b>-<slug>`, the form `tools/prune_branches.py`
reads, such as `task/issue-503-and-issue-504-gap-register`: make it from the base and merge each
half into it with `--no-ff`, so every commit that changes a file keeps one owner and A-05 still
reads each commit as single-owner. One pull request carries a `Closes` line per half, and its merge
subject is `Merge tasks <a>, <b>: ...`. A half's acceptance boxes are judged on the combined tree, so
a half red on its own is the expected state, not a defect. Before running `tools/land.sh`, a session
removes its own task worktrees, because the prune below deletes no branch a worktree has checked
out: on 2026-10-29 both halves' branches stayed behind for that reason.

**Merge with `tools/land.sh <pr> --subject "..."`.** The merge subject is the input to that
derivation and `docs/roadmap.md` is its output, so the merge commit is the one commit that moves the
roadmap — and a merge made through the GitHub API or the web UI is created on the server, where the
hook cannot reach it. The script merges, derives the roadmap and pushes the refresh as its own
commit. `gh pr merge` on its own leaves `main` red on the roadmap check until someone notices.

**It runs from a worktree of its own, which is disposable.** The roadmap is derived from the tree
the merge makes, so the script runs where `HEAD` sits at the base with nothing uncommitted:

```bash
git worktree add --detach ../spa-land origin/main
git worktree remove ../spa-land        # the script does this when the landing succeeds
```

Once the merge, the roadmap refresh and its push have all succeeded, a landing run from a linked
worktree that is detached and clean, untracked files included, removes that worktree as its last
act and names it (#490). A worktree on a branch, or one holding an untracked file such as a body
file, stays, and the script prints the `git worktree remove` line for it; a landing that fails
leaves its worktree for inspection. The repository's own working tree is never removed.

**It merges only a reviewed head.** Before merging, `tools/land.sh` reads the pull request's review
records and refuses unless the latest record naming its current head says `merge`
(`docs/agents/review.md` → "The review record"). It then merges pinned to that head, so a push that
lands while it runs makes GitHub refuse the merge, and it never merges with `--admin`, so the
required CI checks still bind. It ends the merge body with `Reviewed: <head> <record URL>`. The one
escape is `--unreviewed "<reason>"`, for a red `main` that cannot wait or no session free to review:
it merges pinned to the head it saw, ends the body with `Unreviewed: <reason>`, and leaves that
review open until a record names the merged head ("Gates" below). A merge made any other way is not
checked at all, which is why nothing else is a sanctioned merge path.

**The pull request's description closes the tickets it completes.** It carries one `Closes #n`
line for each of them and one `Ticket: #n` line for each ticket it only advances. When
`tools/land.sh` merges the pull request into `main`, GitHub closes each ticket a `Closes` line
names; `Ticket:` is not a closing keyword, so its ticket stays open
(https://docs.github.com/en/issues/tracking-your-work-with-issues/using-issues/linking-a-pull-request-to-an-issue).
A complete ticket stays open and assigned until then. The lines go in the description, not in the
merge body passed with `--body-file`.

The body says what was verified and how; write it to a file and pass `--body-file <path>`, so it
rides in the merge commit rather than staying on the pull request. A named pipe or a process
substitution, `--body-file <(...)`, works as well; `-` does not, because the script keeps standard
input for the confirmation it asks when no `--subject` is given. If the row landed without
being finished, add an `Outstanding:` line saying what it still owes, and an
`Outstanding test ids:` line for any test id the row names that is not yet proven. The claim
branch is deleted when the **task** is done, not when a pull request merges.

In a run-branch landing, each ticket's task merge on the run branch is that ticket's merge body. A
box that asks for something in the merge body is met there, and the landing body cites those merges
by sha rather than copying them. The morning drafts that landing body at
`.scratch/overnight/log/morning-<date>/merge-body.md`: start from it, and add the commits since
the head it names and the review record's carried findings as `Outstanding:` lines.

**A task merge's message is changed by rebuilding the run branch.** A fix to what a ticket built
goes on top as a commit of its own, but git changes a message only by rewriting its commit and every
one after it. Rewrite every commit after the run's base with `git commit-tree`, parents first: each
keeps its tree and its author and committer, dates included; each parent inside the range becomes
its rewritten form, the second parents included; and only the messages being edited change.
Rewriting the first-parent line alone leaves the old graph, and every subject `tools/roadmap.py`
reads in it, reachable through the second parents (#287). Before committing anything on top,
confirm that the rebuilt tip's tree equals the old tip's, so every build and test result of the old
head holds for the rebuilt one, and that `git merge-base --is-ancestor` fails for each commit the
rebuild replaced. A reworded subject leaves the generated blocks of every commit from it to the tip
derived from the old subject: reset the branch to the rebuilt tip, run `tools/roadmap.py`, commit
what it writes, and leave the commits between with their old blocks. A rebuild is
foreground-session work, because the push rewrites a branch that a pull request and its review
records name. Push with `git push --force-with-lease=<branch>:<origin's old tip>` and the pre-push
hook running, then update the pull request's description to the new shas.

**A rebuilt overnight branch carries its run's record with it.** The Overnight issue cites the old
merge shas, and every lane worktree's tip falls out of the branch's ancestry, which stops the
morning's Verify step 3; on 2026-10-22 neither was followed up and the map was rebuilt by hand
(#318). So the rebuilding session runs, from the repository, `python3 -I tools/remap_rebuild.py
--old <old tip> --new <new tip> --issue <Overnight issue> --worktree-root <the run's worktree root>
--session "<this session, and why it rebuilt>"`. It maps every replaced commit to the rebuilt one
with its tree, the commits reached through second parents included, and refuses when one has no
such partner; it prints the map, the issue body rewritten to the new shas with a line naming the
rebuild, and the lane worktrees it would move, and writes nothing. Read that, then run it again with
`--apply`: it moves each clean lane worktree under the root to its rebuilt tip, names each dirty one
and leaves it to its owner, writes the body, and comments the map on the issue.

**`tools/land.sh` deletes it**, after a merge into `main`, by running `tools/prune_branches.py
--delete` (#71), and when that deletes anything, land.sh's last line names the file holding the
commands that recreate each deleted branch at its old tip:
`pruned-branches/<UTC date>-<12-hex HEAD sha>.txt` in the repository's common git directory,
`git rev-parse --git-common-dir`, which outlives the worktree a landing ran in; a cleanup that
cannot write that file stops deleting, and the last line says it stopped (#115). A command
recreates its branch while the repository still has the commit it names: a tip `main` does not
reach, one whose work landed only as patch-equivalents, has no other copy once git's garbage
collection prunes it. A `task/*` branch goes, locally and on `origin`, once every id its name carries
reads as landed in `tools/roadmap.py --json`, as a merge subject of the first form above makes it,
and a split no merge subject names reads as its row; a name whose ids are no plan row, split or
ticket, left from the archived tracker, claims nothing and is held to the rest of this paragraph
alone; an `overnight/*` branch goes once its pull request has merged. Either goes only when its
current tip's work is on `main`, as an ancestor or as patch-equivalent commits with no merge
carrying changes of its own, and no worktree has it checked out or is rebasing it, and a push to it
on `origin` since it was checked keeps it there. Everything else is listed with the reason it stays.
A part-landed row keeps its claim, because the claim is how the next session knows the row is
taken. Run without `--delete` from a checkout at `origin/main`, the script says what it would do,
deletes nothing and writes no file; with it, it clears what an earlier landing left, by the same
rule, and when it deletes anything its own last line names that file.

A task is **done** as `GLOSSARY.md` → "Done" defines it.

## The morning's launches

An overnight run lands through the morning workflow, `.claude/workflows/morning.js` (#78), which a
session launches with the Workflow tool by `scriptPath`, so the landed file runs and not a session's
snapshot of it. It has three launches, below in the order a session meets them. Each line is
complete: copy it and change the night's date. `repo` is the main checkout. `wt` is the worktree
root the overnight run used, the one its own launch was given, outside the repository:
`<wt>/overnight` is the integration worktree and `<wt>/<owner>-<task id>` each ticket's. The
Overnight issue's number, as `"issue": <n>`, may stand for `date`.

The morning pass, the morning after the run: it replays the night's pending tracker writes,
verifies and pushes the integration branch, opens its one pull request, applies the confirmed
answers and posts the hand-over. A re-run, after a Confirm comment is posted, takes the same line.

```js
Workflow({ scriptPath: "/Users/adammedford/Projects/spa-platform/.claude/workflows/morning.js", args: { "repo": "/Users/adammedford/Projects/spa-platform", "date": "2026-10-29", "wt": "/Users/adammedford/.worktrees/overnight-2026-10-29", "after": false } })
```

The draft check, before a Confirm comment is posted: `confirmFile` is the draft's absolute path.
The run sorts the questions the way the morning pass would, prints the result and writes nothing.
It reads no worktree, so `wt` may be left out, and `after` is refused beside it.

```js
Workflow({ scriptPath: "/Users/adammedford/Projects/spa-platform/.claude/workflows/morning.js", args: { "repo": "/Users/adammedford/Projects/spa-platform", "date": "2026-10-29", "wt": "/Users/adammedford/.worktrees/overnight-2026-10-29", "confirmFile": "/Users/adammedford/Projects/spa-platform/.scratch/overnight/log/confirm-2026-10-29.md" } })
```

The after pass, once `tools/land.sh` has merged the pull request: it refuses until `origin/main`
contains the integration tip, then closes the landed tickets, unassigns, removes the worktrees whose
tips are on `origin/main`, the integration worktree last, and prunes.

```js
Workflow({ scriptPath: "/Users/adammedford/Projects/spa-platform/.claude/workflows/morning.js", args: { "repo": "/Users/adammedford/Projects/spa-platform", "date": "2026-10-29", "wt": "/Users/adammedford/.worktrees/overnight-2026-10-29", "after": true } })
```

The script's `badArguments` is the arguments' checker, and `morning_tests.mjs` beside it holds these
three lines to it: a line here missing an argument the checker requires, or carrying one it does
not take, fails that test. The `morning` skill under `~/.claude/skills/synced` is a different,
unrelated skill of the same name, a personal morning brief; a search for this workflow's launch
stops here.

## Review

Every pull request into `main` is reviewed by an agent session that wrote none of its commits, with
`/code-review`, and its findings answered before merge. The integrator's own work is no exception:
the unit a review is independent of is the agent session, not the workstream, so any fresh session
can review it, and the human's reading is the decision to land rather than a review. The spec axis
is the plan row plus the contracts rows naming it, cited by path; work with no row is held to its
issue and the decisions it was handed. `docs/agents/review.md` has the rest: how to read a branch
without touching the shared tree, every source each axis is held to, when a finding holds a merge
rather than becoming an `Outstanding:` line, and the review record a review leaves.

A review covers exactly the head its record names, and `tools/land.sh` refuses a head no record
clears ("Land it" above). The check sits in the merge script rather than in branch protection
because this repository has one GitHub account, which can neither approve its own pull request nor
be held to a required review without `--admin`, and `--admin` would skip the required CI checks too.
#67 records that choice and when it reopens.

Only one kind of change reaches `main` without a pull request: a generated roadmap refresh, the one
`tools/land.sh` pushes after a merge or the one made after a gate tag. Everything else arrives
through a pull request whose head a review record names. This is a rule in prose, not a check, until
the first time it is broken.

### Reading the records

To ask whether a pull request `<n>` is cleared, ask the record reader by its number. It gives the
verdict `tools/land.sh` would act on, from the repository the working directory is in:

```bash
python3 -I tools/review_record.py --pr <n>                       # its current head
python3 -I tools/review_record.py --pr <n> --head <40-hex head>  # a head you name, such as a merged head
```

It looks up the head itself unless `--head` names one, and lists the pull request's comments and
reviews with gh, every page. It prints the URL of the record that clears the head and exits 0;
otherwise it says on standard error why not, the latest record naming the head saying `hold` or no
record naming it, and exits 1. A wrong argument, a gh call that fails or a listing it cannot read
exits 2. `tools/land.sh` runs the `--head` form with the head it pins the merge to.

A caller already holding the two listings, each as `gh api --paginate --slurp` gives it, hands
them over on standard input instead, with the head as the one argument:

```bash
python3 -I tools/review_record.py <40-hex head> < listings.json   # {"comments": <pages>, "reviews": <pages>}
```

Both forms give the same answer for the same pull request. A number where the head belongs, or a
head after `--pr`, is refused with both forms named.

## Drafting a Confirm comment

A `Confirm` comment on an `Overnight <date>` issue is Adam's act, and no agent session posts one
(`docs/agents/tracker-policy.md`). A session that drafts one for him writes the draft to a file and
runs the morning's own parser over it and the issue's comments before handing it over:

```bash
gh issue view <n> --json comments > .scratch/overnight/log/confirm-<date>-comments.json
node .claude/workflows/confirm_draft.mjs .scratch/overnight/log/confirm-<date>.md .scratch/overnight/log/confirm-<date>-comments.json
```

It prints the questions the draft covers, those it overrides, those answered no and those left
open, then one verdict line. It exits 0 when every question is confirmed and none is answered no by
an override, 1 when a question is left open, an override opens "no", the draft names a number no
questions comment yields or does not begin `Confirm`, and 2 when it cannot judge, such as on an
issue that already has a `Confirm` comment. `--json` prints the sort as JSON. It reads the parser
out of `.claude/workflows/morning.js`, so it runs the code the morning runs, and needs no agent.
The morning workflow's `confirmFile` argument is the full check, with an agent reading the issue;
its verdict fails only on a number no questions comment yields.

The rules it holds, which the morning applies:

- The first sentence sets the range: the questions named on the draft's first line, up to its
  first full stop (`Confirm Q1 to Q57.`, `Q1-Q3`, `Q58`), are the ones it confirms; a question named
  after that stop is not, and a bare `Confirm` confirms only its own override lines.
- An override is a line `Q<n>: <answer>`; the last one for a question wins, and it confirms that
  question even when the first sentence does not name it.
- An override opening "no" applies nothing: the question is recorded as answered no, and nothing is
  done for it. One opening "open" leaves it open.

## Gates

A gate closes when its exit test in `docs/plan/01-plan.md` passes and no review of a change it
covers is open. A gate tag covers every merge since the previous gate tag, and it is permanent:
what its commit says stays said there however soon `main` corrects it. So it waits while any merge
in its range has an open review:

- a review still running, or one whose latest record says `hold`;
- commits pushed after the last reviewed head;
- a merge made with `--unreviewed`, or before the check existed, that no record clears yet;
- a re-review of a merged pull request still running.

A review stays open until a record naming the head says `merge` and the findings it raised have
landed, fixed or carried as `Outstanding:` lines. For a merged pull request that is a record naming
its **merged head**, the merge commit's second parent, posted on the merged pull request, with its
findings landing in a follow-up pull request; that clears the review debt the merge left. Then, once
the exit test's CI run on the commit to be tagged has passed, tag it:

```bash
git tag -a <name> -m "<gate>: <exit test>, proved by <CI run URL>"
```

To see what the range holds, list its merges, each with both parents and the trailer
`tools/land.sh` wrote:

```bash
git fetch origin --tags
git log --first-parent --merges --format='%h %s%n  parents: %P%n%(trailers:key=Reviewed,key=Unreviewed)' <previous gate tag>..origin/main
```

A `Reviewed:` line names the record the merge was checked against. An `Unreviewed:` line, or no
line at all, is a merge to check by hand: its pull request must carry a record naming the merged
head with `merge`. Before the check landed, a review naming the merged head in full counts in
whatever shape it was written, as #41 to #43 do; after, only the record line does. To ask the record
reader about a merged pull request `<n>`, name its merged head ("Reading the records" above):

```bash
python3 -I tools/review_record.py --pr <n> --head <merged head>
```

Whether a review is still running is in no record: the tagger checks that none they or anyone else
dispatched is. That is why this rule is prose and a command rather than a hook or a tag script.

The tag is the record. There is no separate checklist. The name each gate's tag takes is in the
ladder table in `docs/roadmap.md`, which is where `tools/roadmap.py` reads it to tell a closed gate
from an open one.
