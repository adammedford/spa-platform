# Reviewing a pull request

What `/code-review` needs that is specific to this repo. The skill supplies the two axes and the
smell baseline; this page supplies the sources, the sandbox rules and the bar. Read it before
dispatching either axis, and give each sub-agent the pointer rather than a transcription of it.

## Read the branch, never the checkout

The working tree is shared and usually dirty, and a local `main` runs behind `origin/main` by
however many merges landed while the session was reviewing. A review that reads the checkout reports
on neither the branch nor `main`.

```bash
git fetch origin --tags                        # current trunk, branches and gate evidence
git diff origin/main...origin/<branch>          # three-dot: against the merge base
git log origin/main..origin/<branch> --oneline
git show origin/<branch>:<path>                 # a file at its branch version
git show origin/main:<path>                     # a file as main has it now
```

Never check the branch out, and never reset the shared tree. When a finding genuinely needs a build
or a test run, make a throwaway worktree outside the repo, run the one project there, and remove it.

## The two axes' sources

**Standards** is `CODING_STANDARDS.md`, plus `AGENTS.md` and `CLAUDE.md` for the rules an
implementation agent carries, plus `OWNERSHIP.md`: a diff that writes outside its owner's paths is a
finding on its own, and `.github/path-ownership.py <base> <head>` decides it. A linear branch is
checked by its whole diff. A history containing a merge is audited per commit, including reverted
commits, with merge resolutions checked separately from imported commits. This signal also applies
when a task branch merges main: it establishes history shape, not task scope or authorization.
Review the plan row and ownership scope even when the mechanical check passes.

**Spec** has no per-task spec file. `docs/agents/landing-work.md` fixes it: the plan row plus the
contracts rows naming it, cited by path. In practice:

| Source | Where |
| --- | --- |
| The row: what, needs, done when | `docs/plan/03-workstreams-and-tasks.md` |
| The ticket, where one exists | the issue the merge body cites: `gh issue view <n> --json body,comments`; a ticket filed before ADR-0020 is the archived file `.scratch/<feature-slug>/issues/NN-<slug>.md`, and read its `## Comments` |
| The interfaces and the stage order | `contracts/`, `docs/plan/02-contracts.md` |
| A test id the row names (`F-`, `N-`, `P-`, `A-`) | `docs/plan/04-tests.md` |
| An evidence id (`S-`, `R-`, `T-`) | `docs/plan/07-evidence.md` |
| A decision the task was handed | `contracts/changes/`, `docs/adr/` |

The pull request body is the author's claim, not the spec. Check it against the diff and say which
claims you verified and how.

## CI has already run

Every open pull request here carries a green `Documents`, and a green `Build and test` and
`Terraform` when its diff touched the half each one checks; a pull request that touched neither
shows both as skipped, which `main`'s protection reads as passing, and `.github/touches.sh` says
what touching means. Where they ran, the build, the full suite and `tools/smoke.sh` are not
findings waiting to be discovered, and re-running the solution costs minutes and buys nothing.
Spend the effort on what CI cannot see: whether an assertion could fail, whether a comment is true,
whether the diff matches the row.

## The bar for a finding

`CODING_STANDARDS.md` → "A reviewer verifies before reporting" is the rule. Three habits carry it:

- **Mutate, do not infer.** The claim "this test cannot fail" is cheap to settle: break the thing it
  guards in a throwaway worktree and see whether it goes red. Three of the nine pull requests
  reviewed on 15 October 2026 stated their own mutation runs and every one reproduced exactly; of
  the five that stated none, every one shipped an assertion that could not fail.
- **Say which findings you checked.** A verified finding and a suspicion are both worth reporting
  and only one is worth acting on without looking, so mark which is which.
- **Grep the noun, then check the scope.** A false claim in prose is rarely in one place. Search for
  the noun it cannot paraphrase away — an identifier, a status code, a field name — and at each site
  ask whether the claim's scope is the thing that actually has the property. One false sentence sat
  in nine places across four files on 16 October 2026: grepping the sentence found a phrasing,
  grepping `security.rejections` found all nine. Report them as one finding. No check decides the
  scope question; `CODING_STANDARDS.md` → "A universal names its exceptions by the writer" is the
  standard it holds the author to.

Report a sub-agent's finding only after you have taken its severity for yourself. A finding handed
up as "looks wrong" is sometimes right, sometimes the contract's own design: on 15 October two axes
independently called `UsePlatformTelemetry`'s captured feature a defect, and the contract turned out to give later
stages a feature they write rather than replace.

## Landing or holding

Every pull request into `main` is reviewed by an agent session that wrote none of its commits, with
`/code-review`, and its findings are answered before merge. No pull request is exempt by what it
touches or where it came from: workstream and integrator work, a tracker-only or docs-only change, a
generated one and an overnight run's integration branch each need a review of their exact head. A
lane merged into an integration branch is covered by the review of the pull request that brings that
branch to `main`, which also covers what no lane review saw: the integration merges, the reviewer's
own fix commits and the roadmap refresh. The human's reading is the decision to land, not a review.

Merge when nothing found is a defect: the spec is met, no assertion is unfailable on the thing it
claims, and the diff stayed in its owner's paths. Record the rest as an `Outstanding:` line in the
merge body, which `docs/agents/landing-work.md` requires anyway. Hold when a finding shows the change
does not do what it says, or when the test that proves it cannot fail.

### The review record

When both axes have reported, the reviewing session posts its verdict as a review record: one line,
on a line of its own, in a pull request comment or in the body of a review submitted as a comment.

```text
Reviewed <40-hex head> against <40-hex base>: merge
Reviewed <40-hex head> against <40-hex base>: hold
```

- **The head is the commit the review read**, `git rev-parse origin/<branch>` after the fetch the
  review started from, and not whatever the branch points at when the record is posted. The base is
  the commit it diffed against, `git merge-base origin/main origin/<branch>`. Both are written in
  full, lowercase as git prints them: an abbreviated sha is not a record.
- **A record covers that head and nothing pushed after it.** A commit pushed after the review, a fix
  for its own findings included, needs a review of its own by an agent session that wrote none of
  those commits. That review may cover only the commits since the last reviewed head. A merge of
  `main` into the branch is such a commit, and its resolution is reviewed as "Standards" above says.
- **The latest record naming the current head decides.** A hold answered without a new commit, a
  finding shown not to be a defect, is cleared by the reviewer posting `merge` on the same head.
- **Nothing else is a record**: not a sha mentioned in passing, which once named a head as where
  fixes went that only their writer had verified; not the `commit_id` GitHub stores on a review,
  which defaults to the newest commit whether or not the reviewer read it; not a review submitted
  as an approval, a request for changes, or one later dismissed; not a line posted by an account
  from outside the repository, since it is public and anyone signed in to GitHub can comment (only
  an `author_association` of `OWNER`, `MEMBER` or `COLLABORATOR` is read); and not a line in any
  other form, backticks included.

Post it with the findings, from the session that ran the review:

```bash
gh pr comment <n> --body-file - <<'EOF'
## Standards
...
## Spec
...

Reviewed <40-hex head> against <40-hex base>: merge
EOF
```

Merge with `tools/land.sh`, never `gh pr merge` alone. It reads the records with
`tools/review_record.py`, refuses a head no record clears, pins the merge to the head the record
names, and writes `Reviewed: <head> <record URL>` at the end of the merge body. #67 decided this.
Every record the check reads is posted through the one GitHub account that owns this repository,
which every agent session and the human share, so a record cannot show which of them wrote it: the
check stops a merge made before its review, or after commits the review never saw, and cannot stop a
session that writes the line without reviewing.

A merge that cannot wait for a review, a red `main` or no session free, uses
`tools/land.sh --unreviewed "<reason>"`. Its review stays open: a record naming the merged head is
posted on the merged pull request when the review runs, its findings land in a follow-up, and
`docs/agents/landing-work.md` → "Gates" says what waits for it.
