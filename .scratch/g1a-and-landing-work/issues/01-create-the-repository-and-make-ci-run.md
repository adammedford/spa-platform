# Create the repository and make CI run

Type: task
Status: resolved

## Question

Nothing to decide here. This is the manual work that unblocks two decisions: the tag cannot be
attested until a runner has executed `.github/workflows/ci.yml`, and whether branch protection is
enforceable cannot be known until the repository exists.

Checklist:

1. **Hygiene first, before any push.** Add `.claude/` to `.gitignore` and prune the three stale
   worktrees (`git worktree list` shows them at `27be97d`, `cac4e4b`, `b7c20ce`, with branches
   `worktree-agent-*`). `.claude/` is currently untracked but not ignored, which is one `git add -A`
   away from committing agent scratch into the repository. Doing this after the first push means the
   scratch is in the history.
2. Create a **private** repository under `adammedford` — ticket 06 decided private on the Free plan.
   `gh` is already authenticated with `repo` and `workflow` scope.
3. Push `main`, then push the branch `task/i-3-abstractions-and-doubles` and open a PR for it.
4. **Watch CI actually run** and record what each job did: `build-and-test`, `terraform`,
   `path-ownership`. The A-05 job is the interesting one, because this branch exercises the
   integrator declaration path.
5. Record anything the workflow got wrong on a real runner. It has only ever been verified
   command-by-command on this workstation. Likely candidates: `hashicorp/setup-terraform` on the
   runner, the `origin/${base_ref}` reference resolving under `actions/checkout`, and
   `terraform fmt -check -recursive infra` against a runner's Terraform version, which will not be
   1.16.5.
6. Attempt to enable **required status checks** on `main` anyway, to observe what Free refuses.
   Ticket 06 already decided not to pay for it, so this is an observation, not a blocker: the rule is
   convention-carried and the gate checklist says so.
7. **Observe the three things ticket 02 could not establish**, since this is the first time a real
   repository exists:
   - Whether a repository on a plan without code owners still computes the CODEOWNERS error list, or
     ignores the file entirely: `GET /repos/adammedford/<repo>/codeowners/errors`.
   - Whether GitHub reports `Unknown owner` against the **root** `CODEOWNERS` when a
     `.github/CODEOWNERS` shadows it. Ticket 03's option 5 depends on this.
   - Whether a Free private repository **refuses** branch protection outright, with a 403 or a
     disabled form, or accepts the setting and leaves it unenforced. The second would be worse,
     because it looks protected and is not.

## Progress, 2026-10-12

Still claimed, not resolved: **CI does not run**, which is the whole point of the ticket.

### Done

1. **Hygiene.** `.claude/` added to `.gitignore`; three stale worktrees removed and their
   `worktree-agent-*` branches deleted, each verified an ancestor of `main` and each worktree
   verified clean first, so nothing unique was lost. Committed before the first push.
2. **Repository created:** <https://github.com/adammedford/spa-platform>, **private**, as ticket 06
   decided.
3. **Pushed**, but not before an unplanned detour. The first push was **rejected**: the account
   blocks command-line pushes that expose a private email, and all 13 commits carried the human's
   personal address (redacted here, since this ticket is now tracked in a public repository and the
   address is the one that rewrite existed to remove). With the human's decision, the history was
   rewritten with `git filter-branch` to the GitHub noreply address (author and committer), and
   `git config user.email` set to the same so future commits are clean. Every SHA changed. The tree
   was verified byte-identical against the pre-rewrite refs, and the tip commit's message was amended
   because it cited three now-stale SHAs. `refs/original/` still holds the old refs locally.
4. **PR #1 open:** <https://github.com/adammedford/spa-platform/pull/1>.

### The blocker

**GitHub Actions does not execute.** Three jobs, **zero steps each**, no logs at all
(`BlobNotFound`), failing 2-3 seconds after start. Reproduced on a re-run, so not transient.

What this rules out:

- Not a YAML error: all three job names resolved, so the workflow parsed.
- Not a repository setting: `actions/permissions` reports `{"enabled": true, "allowed_actions":
  "all"}`.
- Not a workflow bug: no step ran, so nothing in the file was reached. **Item 5 of this ticket is
  therefore still unknown** — the workflow has never been executed on a runner and may still be
  wrong.

What it points at: an account-level Actions entitlement or spending problem. GitHub Free does include
Actions minutes for private repositories, so this is **not** a documented plan gate like branch
protection is, and it should not be conflated with one. Diagnosing it needs the Actions usage page at
<https://github.com/settings/billing> or a token with `user` scope; the current token has
`gist, read:org, repo, workflow`, and scope was not escalated to find out.

Also note: pushing `main` triggered no run, correctly — `main` does not yet contain
`.github/workflows/ci.yml`, which is on this branch.

### Facts later tickets depend on

- **Branch protection is refused outright on Free private**, with a clean 403 and GitHub's own
  wording: *"Upgrade to GitHub Pro or make this repository public to enable this feature."* It does
  **not** accept the setting and silently leave it unenforced, which was the worse of the two
  possibilities ticket 02 left open. Nothing can look protected while not being protected.
- **A Free private repository does compute the CODEOWNERS error list.** 26 errors, one per rule line,
  every one `kind: "Unknown owner"`, naming the team and suggesting it "exists, is publicly visible,
  and has write access". So **ticket 03 is not moot**: GitHub evaluates the file here, and the
  annotation is real and visible. Ticket 03 should proceed on its merits.
- Still untested: whether adding a `.github/CODEOWNERS` suppresses the errors against the root file.
  That needs the file to exist, which is ticket 03's decision to make, not this ticket's.

## Answer

**Resolved.** The repository is <https://github.com/adammedford/spa-platform>, **public**, CI is
green on a runner, and `main` is protected with all three checks required.

### How the blocker was cleared

The human chose to **make the repository public**, which is what GitHub's own 403 on branch
protection recommended and which also makes Actions free and unlimited. That reversed ticket 06's
decision; see that ticket's addendum. A safety scan of tracked files ran first: no key or certificate
material, no AWS account ids, no email addresses beyond the noreply one. The matches on "secret" were
filenames of legitimate source (`IClientSecretSource.cs`, `FakeClientSecretSource.cs`, ADR-0003).

So the Actions failure was the private-repository entitlement, not a workflow fault and not a billing
misconfiguration to fix. One flip, and the same re-run went green.

### The workflow needed no corrections

Item 5 is answered: **nothing was wrong with it.** First execution on a real runner, all three jobs
succeeded, 10, 5 and 8 steps. The three risks this ticket predicted — `setup-terraform` on the
runner, `origin/${base_ref}` resolving under `actions/checkout`, and `fmt -check` against a different
Terraform version — all turned out to be non-issues.

| Job | Evidence from the run |
| --- | --- |
| Build and test | `Build succeeded. 0 Warning(s)`, then `total: 306` |
| Terraform | `--- infra/bootstrap`, init ok, `Success! The configuration is valid.`, and the plan-policy step emitting both notices as designed rather than passing silently |
| Path ownership (A-05) | Detected the multi-owner span and **allowed it via the integrator declaration** — the exact branch that could not be tested before a real PR existed |

### Facts later tickets depend on

- **Branch protection on `main` is active**, requiring all three checks, `strict: false`,
  `enforce_admins: false` so the human is never locked out, force-pushes and deletions blocked. Free,
  because the repository is public.
- **On Free *private*, protection is refused outright** — clean 403, *"Upgrade to GitHub Pro or make
  this repository public to enable this feature."* It does **not** accept the setting and leave it
  unenforced, which was the worse of the two possibilities ticket 02 left open.
- **CODEOWNERS errors are computed** — observed while still private: 26 errors, one per rule line,
  all `kind: "Unknown owner"`. **Ticket 03 is not moot**, and now matters more: code owners is a
  working feature on a public repository, so the seven unresolvable handles are a live defect rather
  than an inert annotation.
- Still untested: whether a `.github/CODEOWNERS` suppresses the errors against the root file. That
  needs the file to exist, which is ticket 03's decision.
- `main` had no CI run of its own, correctly: it does not yet contain `.github/workflows/ci.yml`,
  which is on this branch.

### One consequence for the gate

Task I-5's "done when" — green CI on the skeleton — **is now met and evidenced by a run URL**, which
ticket 05 can cite. Branch protection is enforced rather than convention-carried, so ticket 05's
third item changes again: there is no convention-enforced item to word carefully after all.
