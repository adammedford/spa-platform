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
longer behind origin/main after a landing: `git fetch origin`, then exactly
`git merge --ff-only origin/main`, while HEAD is `main` and no tracked file has changes (untracked
files are allowed). `git pull` is not that fast-forward. While HEAD is `main`, a tracked file whose
changes equal its origin/main content, with nothing staged, may first be restored with
`git restore <path>` or `git checkout -- <path>`, as a call of its own, since once `main` is
fast-forwarded that restore has changed nothing (#376). In a Claude Code session started in a
checkout that holds it, the tracked `.claude/settings.json` runs `tools/main_checkout_guard.py`
before each Bash call, and in the main checkout it refuses the branch-moving git commands and those
that change files or the index (`git rm`, `mv`, `add`, `restore`, `clean`, `apply`, and
`git checkout -- <path>`), admits that one fast-forward and that one restore, and names this route
(#114, #200). A person's terminal runs no such hook.

The same file sets `CLAUDE_CODE_SHELL=/bin/bash`, which a Claude Code process reads when it starts:
after a change to `.claude/settings.json` or after trusting a checkout, restart Claude Code (the
editor, for the extension), and confirm with `ps -p $$ -o comm=` in the Bash tool, which should
print `/bin/bash` (#183).

## Build it

Write only inside your owner's paths. `OWNERSHIP.md` lists them, and `.githooks/pre-push` fails a
push that reaches outside one owner's set. When it fires, either the change went somewhere it did
not mean to, or it needs an integrator path.

**Needing an integrator path** — a contract, a package version, a new project, anything in
`src/Platform.Abstractions` — means writing `contracts/changes/<task-id>-<slug>.md` describing what
you need and why. Anyone may add one; the check exempts that directory, except a request added or
edited to say its Status is decided, which needs a confirmed answer as its authority (ADR-0021
decision 4). Then merge what you have, say in the merge body that the request is open, and stop the
task. The integrator decides and edits.

Needing *another workstream's* path means something has gone wrong: A-03 says a workstream project
references `Platform.Abstractions` and no other workstream's project, so a real need for another
workstream's code is a need for Abstractions, which is the case above. Otherwise the row was taken
out of order.

**A commit that changes a steering document names its authority** (ADR-0023). The steering
documents are `GLOSSARY.md`, `AGENTS.md`, `CLAUDE.md`, `OWNERSHIP.md`, `CODING_STANDARDS.md`,
`docs/agents/`, `docs/adr/` and `contracts/`. The commit's message ends with a trailer in one of two
forms, on a line of its own in the last paragraph, beside any other trailers:

```
Authority: #<n>     the ticket whose acceptance criterion asks for the change,
                    a ticket the human wrote or approved
Authority: <URL>    the comment on this repository that holds a confirmed answer, as GitHub
                    links it: https://github.com/<owner>/<repo>/issues/<n>#issuecomment-<id>
```

A pre-accepted answer, a follow-up a night raised, an archived ticket's proposal and a reviewer's
finding are not authority. `git commit --amend --no-edit --trailer "Authority: #<n>"` adds the
trailer to the last commit. git reads trailers from the message's last paragraph alone, and an
`Authority:` line inside the prose is not one.

`.github/steering.py` holds this, in the pre-push hook and in CI, on every branch (#102). It reads
each non-merge commit between the base the check judges against and HEAD, made since the starting
point the script records, and fails one that changes a steering document with no `Authority:`
trailer, a `#<n>` that names no issue (a pull request's number included), or a URL that is not a
comment on this repository, naming the commit and the paths. A merge needs no trailer, and neither
does a commit that only adds an open change request under `contracts/changes/`; editing or removing
one does, and a commit that sets a request's Status to decided needs a trailer that is the URL of a
confirmed answer, never a bare `#<n>`. Whether what the trailer cites decides the change is the
reviewer's call, not the check's.

## Land it

One branch per task, one pull request, merged with a merge commit. CI must be green.

The merge subject names every task id it lands. Two forms, because a row can outlive one session:

```
Merge task(s) <id...>: <what landed>      the row is done
Merge part of task <id>: <what landed>    more to come on the same branch
```

The subject is read by a check: `tools/roadmap.py` derives the roadmap's state from these
subjects, so "Merge task 2.4: ..." lands the row and "Merge part of task 2.4: ..." does not. Run the
script before the push and commit what it writes, or `.githooks/pre-push` will say so.

**Merge with `tools/land.sh <pr> --subject "..."`.** The merge subject is the input to that
derivation and `docs/roadmap.md` is its output, so the merge commit is the one commit that moves the
roadmap — and a merge made through the GitHub API or the web UI is created on the server, where the
hook cannot reach it. The script merges, derives the roadmap and pushes the refresh as its own
commit. `gh pr merge` on its own leaves `main` red on the roadmap check until someone notices.

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
by sha rather than copying them.

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

A task is **done** when its tests pass in CI, it touched only its own paths, and it leaves no change
request open. Merged is not done.

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
reader about a merged pull request `<n>` and its merged head:

```bash
printf '{"comments": %s, "reviews": %s}\n' \
  "$(gh api "repos/{owner}/{repo}/issues/<n>/comments" --paginate --slurp)" \
  "$(gh api "repos/{owner}/{repo}/pulls/<n>/reviews" --paginate --slurp)" \
  | python3 -I tools/review_record.py <merged head>
```

Whether a review is still running is in no record: the tagger checks that none they or anyone else
dispatched is. That is why this rule is prose and a command rather than a hook or a tag script.

The tag is the record. There is no separate checklist. The name each gate's tag takes is in the
ladder table in `docs/roadmap.md`, which is where `tools/roadmap.py` reads it to tell a closed gate
from an open one.

### Review debt from before the check

`contracts-v1` was tagged two minutes after #37 merged, while #37's review was still running; that
review found claims the tag still freezes, which #39 and #40 corrected on `main`. When #67 decided
what enforces review, the five merges after that tag that no record named were to be reviewed
before G2. `g2-local-slice` was tagged before that was done, and stands as it was cut. They are
review debt like any other, and the next gate tag waits until a record clears each.

| Pull request | Merged head | Merge commit |
| --- | --- | --- |
| #38 | `9202d6cc2e9af80cc4e27f24371d488f0399175f` | `733477a` |
| #39 | `9564347d640b80c86711ea93a6d7ad55da1b9802` | `e376fe2` |
| #40 | `6c07267ffd2ea422cdaa3a9aad4cda3d7ff0be1e` | `a3ec472` |
| #44 | `ced5d8d6a49867c85b5547ff9d6f59a0ab488499` | `37fdca2` |
| #45 | `91a8b2089df820237572719303c9d7195bad3dd9` | `eb65be8` |

As read on 18 October 2026, for #67: none of the five carries a comment or a review, and no merge
body, issue or pull request names its merged head. Each is cleared by a record naming that head,
posted on its pull request, by an agent session that wrote none of its commits.
