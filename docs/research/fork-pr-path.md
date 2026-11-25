# What a pull request from a fork meets in CI and at landing

- **Ticket:** #1550, "What does a pull request from a fork meet in CI and at landing?", under the
  wayfinder map #1548 (outside contribution).
- **Date:** 2026-11-25
- **Branch:** `research/fork-pr-path`, off origin/main at `feafd88c`.
- **Method:** read `.githooks/pre-push`, `tools/check.sh`, `.github/workflows/*.yml`,
  `.github/plan-secrecy.py`, `.github/steering.py`, `.github/path-ownership.py`, `tools/land.sh`,
  `tools/review_record.py`, `tools/prune_branches.py`, `.claude/workflows/review-queue.js` and
  `docs/agents/review.md` at that commit; read main's protection and the Actions settings through
  `gh api`; read GitHub's documentation (list at the end). Executed locally: the CI Documents
  job's decision step and its steering, plan-secrecy and A-05 scripts against a throwaway branch
  of commits by an outside identity, `tools/check.sh` on it, and the git commands review-queue and
  land.sh run against a stand-in for a fork head in a fresh full clone. Not done: no fork, no pull
  request and no workflow run was made, since the repository has never had a fork (forks 0), so
  nothing here observed a real fork run's token, approval gate or CodeQL upload. No AWS work.

## Answer in brief

1. **Most of the hook's checks run on a fork PR, two classes do not.** The Documents job runs the
   steering check (with the `Authority:` rule), plan secrecy, the roadmap, the analysis guard, the
   endpoint paths, the mode split, hadolint, the shell and workflow parse checks and every listed
   test companion; the Build and test job builds with the analyzers as errors and runs the tests,
   the smoke test, the image check and the image scan. **A-05 (path ownership) does not run in
   CI**, by a decision written into `ci.yml` and `path-ownership.py`; CI runs only its test
   companion. Nor do the hook's part-row and full-landing merge checks, the duplicated-artifacts
   check or the file-mode check (the last two are local-machine defects a fork cannot carry). No
   DCO `Signed-off-by:` check exists anywhere yet. Ticket shape is not a push or PR check at all:
   `ticket-shape.yml` runs on `issues` events for `ready-for-agent` issues.
2. **`plan-secrecy.py` is a source grep over `.github/` and `tools/`** (`.sh`, `.bash`, `.yml`,
   `.yaml`): it fails any `terraform`/`tofu` line naming `plan` or `show`, any
   `actions/upload-artifact` other than the one reviewed path in `startup-bench.yml`, and any
   `actions/cache` other than `ci.yml`'s `~/.nuget/packages`. It does not judge triggers, token
   permissions, secrets or `pull_request_target`: executed, a workflow that checks out a PR head
   under `pull_request_target` with a secret passes it.
3. **On a fork's `pull_request` run the token is read-only and no secret is passed** (GitHub docs).
   CI needs neither: every job asks for read scopes but CodeQL, whose `security-events: write` is
   lowered to read, and GitHub's code-scanning docs say upload "always" works on a `pull_request`
   run. No job reads a secret. So no job should fail or skip for being a fork's; a skip by `if`
   reports success and satisfies protection, as PR #1540 shows (executed: Build and test, Terraform
   and CodeQL `SKIPPED` on a docs-only PR that merged). Two fork-specific things do block or weaken
   the four required checks: the repository's approval policy is `first_time_contributors`, so a
   first-time contributor's runs wait for Adam's approval, and no job runs and no required check
   passes until then (how GitHub displays them meanwhile was not observed); and the run checks out
   the PR's own merge commit, so the check scripts are the contributor's copy, and, by inference
   (no fetched page says which commit's workflow file runs), so is `ci.yml`: a PR that edits them
   could turn the four required names green without the checks main holds.
4. **Neither review-queue nor land.sh is fork-proof, and the break is in review-queue.** A fork
   head lives in the base repository only under `refs/pull/<n>/head`, which `git fetch origin
   --tags` does not fetch. Executed against a stand-in (PR #282's head, reachable only that way):
   after that fetch, `git merge-base origin/main <head>`, `git log origin/main..<head>` and `git
   worktree add --detach <wt> <head>`, the commands review-queue's inventory, A-05 audit and
   mutation steps run, all fail; `git fetch origin <sha>` or `git fetch origin pull/<n>/head`
   fixes it. `docs/agents/review.md`'s hand procedure names the head as `origin/<branch>`, which
   does not exist for a fork, and resolves to main when the fork's branch is called `main`.
   Repairing findings pushes to `origin/<branch>`, which a fork PR does not have. land.sh already
   fetches the head by sha before reading its commits, reads only records by
   `OWNER`/`MEMBER`/`COLLABORATOR`, pins the merge with `--match-head-commit` and never uses the
   branch name, so it should merge a reviewed fork head; its branch cleanup only reads `task/*` and
   `overnight/*` on origin and leaves a fork's branch alone. Review-queue's mutation step also
   builds and runs the head's code on the maintainer's machine, which for a fork is untrusted code.

## 1. Which of the hook's checks run on a fork PR

`.githooks/pre-push` only runs `tools/check.sh` (default mode). Its checks, and where CI holds each:

| Check in `tools/check.sh` (default mode) | In `ci.yml` on a PR | Where |
| --- | --- | --- |
| A-05, `.github/path-ownership.py <base> HEAD` | **No**, only `path_ownership_tests.py` | Documents, "A-05's two rules hold" |
| Steering, `.github/steering.py --base` (citations, ticket numbers, `Authority:` trailers, ADR bodies) | Yes | Documents, base from the decision step |
| Part-row merges carry `Outstanding:` lines | **No** | |
| Full-landing merges, `tools/landing_subjects.py` | **No** | |
| Roadmap, `tools/roadmap.py --check` | Yes | Documents |
| Analysis guard, `tools/analysis_guard.py` | Yes | Documents |
| Endpoint paths, `tools/endpoint_paths.py` | Yes | Documents |
| Mode split, `tools/mode_split.py` | Yes | Documents |
| Duplicated build artifacts (iCloud) | No; a local-disk defect a commit cannot carry | |
| Tracked files only the owner can read | No; git records only the exec bit, so a fork cannot carry it (`check.sh`'s own comment: "CI checks out with 644 and never sees it") | |
| Build (warnings and analyzers as errors) | Yes, Release | Build and test; and CodeQL builds again |
| Terraform fmt/validate at the floor | Yes | Terraform |

CI also runs what only `--full` runs locally (tests, smoke, image check, Terraform tests, the test
companions) and what the hook never runs: plan secrecy (in `check.sh` only as a test companion
when touched), hadolint on every PR, the image scan, and the bash 3.2 job (not required).

A-05 is out of CI on purpose. `ci.yml` lines 35-38 and `path-ownership.py`'s docstring: "A
blocking CI check would need an override for the integrator's own cross-owner work, and an override
nobody can verify ... is a permissions model for a team of one." That reasoning assumed one author;
an outside contributor never runs the hook (or can `--no-verify`), so on their PR A-05 is judged
only by review-queue's inventory step (section 4). The map's standing note says "CI is the
contributor's pre-push: ... a gap is a ticket", so A-05 and the two merge-subject checks are gaps.
The merge-subject checks matter less: their subjects are written by `tools/land.sh`, not by a PR's
commits, though nothing stops a fork commit's own subject starting "Merge task".

Ticket shape (`tools/ticket_shape.py`) is in neither the hook nor the PR workflow. It runs from
`ticket-shape.yml` on `issues: [opened, edited, labeled]` for issues carrying `ready-for-agent`,
with `issues: write`; an `issues` event runs in the base repository, so a fork is irrelevant to it.
`codeql-weekly.yml` and `startup-bench.yml` are schedule-only and never run on a PR.

**The steering `Authority:` rule on a fork PR.** Executed: a commit by an outside identity that
edits `AGENTS.md` with no trailer fails the CI step's command (`STEERING FAILED: a commit changes a
steering document and cites no authority`); the same commit with `Authority: #1547`, an issue that
has nothing to do with the edit, passes. The URL form checks the comment's author against the
human's account; the `#<n>` form checks only that the number is an issue and not a
`change-request`. Anyone can open an issue, so an outsider can cite one they filed. Whether the
ticket decides the change stays the reviewer's, as `steering.py`'s docstring says, but the check
itself does not tell an outsider's ticket from Adam's.

**Executed A-05 on the same branch.** A commit touching `README.md` (integrator) and
`src/Platform.Identity/` fails `path-ownership.py origin/main HEAD` (exit 1), and `tools/check.sh
--base origin/main` stops with `pre-push: check A-05 failed.` In CI that branch would pass this
point, since no CI step runs the script against the change.

## 2. What `plan-secrecy.py` enforces

It is a "conservative source check ... a regression guard for accidental CI edits, not a shell
interpreter or a sandbox" (its docstring). It reads every `.sh`, `.bash`, `.yml` and `.yaml` file
under `.github/` and `tools/`, joins backslash continuations, skips lines starting `#`, strips
quotes, and fails on:

- `\b(terraform|tofu)\b ... \b(plan|show)\b` on one command: plan generation stays disabled
  (ADR-0016 decision 4; CR 1.1, plans hold plaintext secrets).
- `actions/upload-artifact@` anywhere but `.github/workflows/startup-bench.yml` with exactly the
  one `path: ${{ runner.temp }}/startup-bench/`.
- `actions/cache`, `/save` or `/restore` anywhere but `ci.yml` with exactly `path: ~/.nuget/packages`.

Executed: a new workflow with `terraform -chdir=infra plan` and an upload fails it with both
messages; a new `pull_request_target` workflow that checks out `github.event.pull_request.head.sha`
and passes `secrets.ANY` to a build step passes it. Nothing else in the repository judges
workflow triggers or permissions either, so the "pwn request" shape is caught only by review.

## 3. Token, secrets, and the required checks on a fork's `pull_request` run

From GitHub's documentation (quotes as served, 2026-11-25):

- "The `GITHUB_TOKEN` has read-only permissions in pull requests from forked repositories." and
  "With the exception of `GITHUB_TOKEN`, secrets are not passed to the runner when a workflow is
  triggered from a forked repository." (events that trigger workflows; using secrets)
- For such runs "the permissions are adjusted to change any write permissions to read only", unless
  an admin selected "Send write tokens to workflows from pull requests" (workflow syntax,
  `permissions`).
- `GITHUB_SHA` "is the last merge commit of the pull request merge branch", `GITHUB_REF` the merge
  branch `refs/pull/<n>/merge`.
- Code scanning: "code scanning always allows the uploading of results when the `pull_request`
  event triggers the action run." (troubleshooting, "Resource not accessible")
- Cache: a PR run "can also restore caches created in the base branch", and saves "for the merge
  ref (`refs/pull/.../merge`)", restorable only by re-runs of that PR.
- Approval: options are first-time contributors new to GitHub, first-time contributors, or all
  external contributors; "Only users who have never had a commit or pull request merged into this
  repository will require approval to run workflows"; a run awaiting approval over 30 days expires
  and is marked failed.

Read from the repository (executed with `gh api` on 2026-11-25): main requires `Build and test`,
`Terraform`, `Documents` and `CodeQL` (app 15368, GitHub Actions), not strict, no required reviews,
`enforce_admins` off; Actions default token `read`; fork approval policy `first_time_contributors`;
repository public, forks 0, `delete_branch_on_merge` on.

Job by job, read against those facts:

- **Documents** asks for `contents`, `issues`, `pull-requests: read`, all granted. The steering
  step calls `gh api` with `github.token`; reading issues of a public repository needs only read.
  `actions/checkout` with `fetch-depth: 0` fetches the base repository's branches and tags, so
  `origin/main` and the gate tags the roadmap check needs are there. Expected to run as on a
  same-repository PR.
- **Build and test** uses no secret; the NuGet cache restores from main and saves to the merge ref.
  The image scan downloads Trivy's database with no token in the scripts. Expected to run.
- **Terraform** uses no credential (`ci.yml`: "CI holds no AWS credential"). Expected to run.
- **CodeQL** asks for `security-events: write`, which becomes read; per the code-scanning quote the
  upload is still accepted. Not observed: the repository has had no fork or Dependabot PR, so no
  read-only-token CodeQL run exists to look at.
- The bash 3.2 macOS job is not required.

What does affect the required checks:

- **First-time approval.** Until Adam approves the run, no job runs, so none of the four reports
  and the PR cannot merge. The docs fetched do not say how the checks display meanwhile; read as
  "pending", not observed.
- **Skips satisfy protection.** Executed: PR #1540 (docs only) merged with Documents and the bash
  3.2 job `SUCCESS` and Build and test, Terraform and CodeQL `SKIPPED`. A fork's docs-only PR gets
  the same.
- **The contributor's copy runs.** `actions/checkout` with no `ref` checks out `GITHUB_SHA`, the
  merge commit, so every script the jobs call (`touches.sh`, `steering.py`, `plan-secrecy.py`, the
  companions) is the PR's version. That the workflow definition is read from the same commit is
  inferred, not quoted: no fetched page states which commit's workflow file a `pull_request` run
  uses. The required checks are matched by job name, so a PR that edits a check script (and, on
  that inference, `ci.yml`) can pass all four names while dropping what they check. It cannot gain a secret or a write token by doing
  so. This is true of a same-repository PR too, but there the author is the integrator.
- **`concurrency: ci-${{ github.ref }}`** is `refs/pull/<n>/merge` per PR, so forks do not cancel
  each other.

## 4. review-queue.js and tools/land.sh against a fork head

**Where a fork head is.** "Commits in a pull request are available in a repository even before the
pull request is merged", under the read-only `refs/pull/<id>/head` (GitHub docs). The main
checkout's refspec is `+refs/heads/*:refs/remotes/origin/*` and it is not a partial clone
(`remote.origin.promisor` unset), so `git fetch origin --tags` does not bring a fork head.

**Executed stand-in.** PR #282's head `a7b67eea` belongs to a closed, unmerged PR whose branch is
deleted, so the base repository holds it only as `refs/pull/282/head`, as it would a fork's head. In
a fresh full clone (a blobless clone hides the problem by fetching the commit on demand):

| Command, after `git fetch origin --tags` | Result |
| --- | --- |
| `git cat-file -e a7b67eea^{commit}` | absent |
| `git merge-base origin/main a7b67eea` | `fatal: Not a valid commit name`, exit 128 |
| `git worktree add --detach <wt> a7b67eea` | `fatal: invalid reference` |
| `git log --format=%H origin/main..a7b67eea` | `fatal: Invalid revision range` |
| `git fetch origin a7b67eea...` (full sha), then `merge-base` | fetched; merge-base `0419ed99` |
| `git fetch origin pull/282/head` | `FETCH_HEAD` = `a7b67eea` |

**review-queue.js** (read; prompts at lines 442, 666-737, 852):

- The inventory step gets `headRefOid` from `gh pr view`, which is right for a fork, then runs
  `merge-base origin/main <head>`, `log <forkPoint>..<head>`, `diff <forkPoint>...<head>` and
  `path-ownership.py <forkPoint> <head>` after only `fetch origin --tags`. All fail on a fork head
  as above, so the review errors out (or an agent improvises a fetch the prompt does not name).
  The fix is one line: `git fetch origin pull/<n>/head` or the head sha before step 3.
- The A-05 audit is that same step 4 (the ticket's "A-05 audit of imported commits"; `git grep`
  finds no other home for the phrase, so this reads it as the inventory's ownership run). Once the head is fetched it judges the fork's commits by
  path, which is the only place A-05 meets an outside PR (section 1). A contributor is no role in
  `OWNERSHIP.md`, so a fork PR spanning two owners fails it.
- The review record's head is `headRefOid`, and `review_record.py` and the script's filter count a
  record only from `OWNER`, `MEMBER` or `COLLABORATOR`, so an outsider cannot post a clearing
  record. That half holds for forks.
- The mutation step (line 852) makes a worktree at the head and builds and runs its tests on the
  maintainer's machine. For a fork that is running untrusted code with the maintainer's
  credentials in reach (gh login, any AWS profile), which CI deliberately avoids by giving forks a
  read-only token.

**`docs/agents/review.md`** names the head as `git rev-parse origin/<branch>` and the base as
`merge-base origin/main origin/<branch>`, and its repair rule says "Work in a new worktree off
`origin/<branch>` ... and push". A fork's `headRefName` is a branch in the fork, not on origin:
`origin/<branch>` does not resolve, and if the contributor's branch is `main` it silently resolves
to origin/main. A repair push needs the fork's remote and `maintainerCanModify`; otherwise a
finding goes back to the contributor as a comment.

**tools/land.sh** (read; not run against any PR, as instructed):

- Takes the head from `gh pr view --json headRefOid` and refuses anything but 40 hex.
- Before reading the commits for the closing-reference rule it runs `git cat-file -e "$head^{commit}"
  || git fetch -q origin "$head"` (line 337), the sha fetch shown to work above.
- Reads records through `review_record.py`, repository accounts only.
- Merges with `gh pr merge --merge --match-head-commit <head>` and never `--admin`, so a fork PR is
  held by the four required checks, including while they await approval.
- Never uses `headRefName`. Its cleanup, `tools/prune_branches.py --delete`, reads only `task/*`
  and `overnight/*` branches on origin, so it neither finds nor deletes a fork's branch;
  `delete_branch_on_merge` does not delete branches in forks.
- So land.sh should merge a reviewed fork head as it is; nothing in it was seen to break. Its
  closing-reference rule needs a commit message naming each `Closes #n`, which a contributor has to
  be told.

## Executed and read

Executed on 2026-11-25, on this machine with Adam's `gh` login (a fork run's token was not
reproduced; for a public repository the reads it makes need only read access):

- `gh api` of main's protection, `actions/permissions`, `actions/permissions/workflow`,
  `actions/permissions/fork-pr-contributor-approval` and the repository; `gh pr view 1540
  --json statusCheckRollup`; `gh pr view 282`; a search for Dependabot PRs (none).
- On a throwaway detached worktree off origin/main with two commits by "Outside Contributor": the
  decision step's `touches.sh` calls (build true, terraform false, workflows false);
  `path-ownership.py origin/main HEAD` (exit 1); `steering.py --base origin/main` without and with
  `Authority: #1547` (exit 1, exit 0); `plan-secrecy.py` with two throwaway workflows (exit 1,
  exit 0); `tools/check.sh --base origin/main` (stops on A-05). The worktree and branch were
  removed; nothing was pushed.
- In a fresh full clone in the session scratchpad: the git commands in the table of section 4.

Read on 2026-11-25:

- The repository at `feafd88c`: `.githooks/pre-push`; `tools/check.sh`; `.github/workflows/ci.yml`,
  `ticket-shape.yml`, `issue-labels.yml`, `codeql-weekly.yml`, `startup-bench.yml`;
  `.github/plan-secrecy.py`, `.github/steering.py`, `.github/path-ownership.py`; `tools/land.sh`,
  `tools/review_record.py`, `tools/prune_branches.py`; `.claude/workflows/review-queue.js`;
  `docs/agents/review.md`.
- https://docs.github.com/en/actions/writing-workflows/choosing-when-your-workflow-runs/events-that-trigger-workflows
- https://docs.github.com/en/actions/writing-workflows/workflow-syntax-for-github-actions (`permissions`)
- https://docs.github.com/en/actions/security-for-github-actions/security-guides/using-secrets-in-github-actions
- https://docs.github.com/en/code-security/code-scanning/troubleshooting-code-scanning/resource-not-accessible
- https://docs.github.com/en/actions/writing-workflows/choosing-what-your-workflow-does/caching-dependencies-to-speed-up-workflows
- https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/enabling-features-for-your-repository/managing-github-actions-settings-for-a-repository
- https://docs.github.com/en/actions/managing-workflow-runs-and-deployments/managing-workflow-runs/approving-workflow-runs-from-public-forks
- https://docs.github.com/en/pull-requests/collaborating-with-pull-requests/reviewing-changes-in-pull-requests/checking-out-pull-requests-locally

The documentation was read through a summarising fetch; the quotes above are as it returned them,
and none of the pages showed a date. Not established by either method: how the required checks
display while a first-time contributor's run awaits approval, an actual CodeQL upload from a
read-only token, and which commit's workflow file a fork's `pull_request` run reads (inferred to
be the merge commit's).
