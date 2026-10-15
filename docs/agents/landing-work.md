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

## Build it

Write only inside your owner's paths. `OWNERSHIP.md` lists them, and `.githooks/pre-push` fails a
push that reaches outside one owner's set. When it fires, either the change went somewhere it did
not mean to, or it needs an integrator path.

**Needing an integrator path** — a contract, a package version, a new project, anything in
`src/Platform.Abstractions` — means writing `contracts/changes/<task-id>-<slug>.md` describing what
you need and why. Anyone may add one; the check exempts that directory. Then merge what you have,
say in the merge body that the request is open, and stop the task. The integrator decides and edits.

Needing *another workstream's* path means something has gone wrong: A-03 says a workstream project
references `Platform.Abstractions` and no other workstream's project, so a real need for another
workstream's code is a need for Abstractions, which is the case above. Otherwise the row was taken
out of order.

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

The body says what was verified and how. If the row landed without being finished, add an
`Outstanding:` line saying what it still owes, and an `Outstanding test ids:` line for any test id
the row names that is not yet proven. The claim branch is deleted when the **task** is done, not
when a pull request merges.

A task is **done** when its tests pass in CI, it touched only its own paths, and it leaves no change
request open. Merged is not done.

## Review

A pull request is reviewed by a session other than the one that wrote the code, with `/code-review`,
and its findings answered before merge. The spec axis is the plan row plus the contracts rows naming
it, cited by path; there is no per-task spec file. `docs/agents/review.md` has the rest: how to read
a branch without touching the shared tree, every source each axis is held to, and when a finding
holds a merge rather than becoming an `Outstanding:` line.

The integrator's own skeleton work has no second workstream to review it, so it merges on CI plus
the human. That is the weakest link here, and it is known.

## Gates

A gate closes when its exit test in `docs/plan/01-plan.md` passes. Tag it:

```bash
git tag -a <name> -m "<gate>: <exit test>, proved by <CI run URL>"
```

The tag is the record. There is no separate checklist. The name each gate's tag takes is in the
ladder table in `docs/roadmap.md`, which is where `tools/roadmap.py` reads it to tell a closed gate
from an open one.
