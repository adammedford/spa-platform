# Whether the repository is private, and what mechanical enforcement costs

Type: grilling
Status: resolved

## Question

Charting settled "private, personal, under `adammedford`" and treated branch protection as
desired-but-conditional, on the assumption that if it needed a paid plan the gate checklist would
simply record the rule as convention-enforced. Ticket 02 found a third option that was not on the
table, so the assumption is worth one more look before ticket 01 creates anything.

What ticket 02 established, as of 2026-10-12:

- On a **private personal** repository, GitHub Pro or better is the minimum for **protected
  branches, rulesets and code owners alike**. There is no separate gate for required status checks.
- On a **public** repository, all three are **free**.
- **The current price of Pro could not be established.** `github.com/pricing` no longer lists Pro or
  any figure for it, while the docs still describe Pro as a current individual plan. The only
  GitHub-published figure found was "$4" on a 2020 FAQ, which is not usable as current. The
  authenticated upgrade flow would settle it.

So the options are:

1. **Private on Free.** Nothing is mechanically enforced: no required status checks, no rulesets, no
   code owners. CI still runs and still reports, and A-05 still fails a bad change — but nothing
   stops a merge that ignores them. The gate checklist records the rule as convention-enforced.
2. **Private on Pro.** Everything is enforced. Costs an unknown monthly figure, which the human can
   read off the upgrade flow in a few seconds.
3. **Public on Free.** Everything is enforced, at no cost, in exchange for publishing the repository.

Option 3 is the one charting did not consider, and it is not obviously wrong: nothing secret is
committed. `.gitignore` excludes private keys, certificates, Terraform state and `.env`, no AWS
account id or Cognito pool exists yet, and the client secrets live in Parameter Store by reference
(ADR-0003), never in the repository. What *would* become public is the whole plan, the ADRs, the
architecture, and a commit history that is visibly agent-authored.

Questions for the human:

1. Is publishing acceptable? This is a judgement about the work, not a technical question: the plan
   describes an internal enterprise platform, and its architecture, gaps register and evidence of
   what has not been proved would all be readable.
2. If not, is Pro worth the figure the upgrade flow shows, or is convention-enforcement good enough
   for a proof of concept?
3. If convention-enforcement is chosen, does anything compensate? A local `pre-push` hook running
   the same checks is weaker than a server-side rule but is not nothing, and `setup-pre-commit` is
   an available skill.

Resolve this before ticket 01: the visibility decision is an input to creating the repository, and
flipping a repository from private to public later means its history was already pushed under the
other setting.

## Answer

**Private, on Free. Enforcement is carried by convention, with a tracked pre-push hook as its only
teeth. Pro is revisited the first time the convention actually fails.** Option 1, not 2 or 3.

### Why not public, given it gets everything free

Because free enforcement buys little here. With one human and agent sessions, server-side protection
guards against your own haste, not against a hostile contributor: the build, the 306 tests and check
A-05 run and report on every pull request whatever the plan, and protection only stops a deliberate
merge of a red PR.

Against that, publishing is the one irreversible option. Forks, caches and archives outlive a flip
back to private, and what would become public is not code with its secrets stripped but a complete
internal enterprise architecture, its decision records, its gaps register, and commit messages
documenting exactly what has not been proved. Private-on-Free and private-on-Pro are each reversible
at any time for about $4 a month; public is not reversible at all. Where one option is reversible and
the other is not, and their benefits are close, take the reversible one.

Pro's price is indicative, not confirmed: about **$4/month ($48/year)**, from a June 2026 community
post and GitHub's older plan-change FAQ. `github.com/pricing` no longer lists Pro at all. The
authenticated upgrade flow is authoritative if the question ever becomes live.

### What this costs, and what the gate checklist must say

Nothing is mechanically enforced: no required status checks, no rulesets, and **no code owners at
all**, since that needs Pro on a private repository too. `docs/gates/G1a.md` records the rule as
convention-carried rather than enforced, which is weaker evidence and has to be visible on the
checklist rather than hidden in it.

### The hook

A tracked `.githooks/pre-push`, wired with `git config core.hooksPath .githooks`, running **check
A-05 and the build only** — not the tests, which stay in CI. Reasons: a hook in `.git/hooks` is
neither committed nor shared, so it would exist on one machine and in none of the six worktrees,
while `core.hooksPath` pointing at a tracked directory costs one `git config` per clone; the
`pre-commit` framework was rejected because it needs Python tooling this .NET repository does not
otherwise have; and keeping the hook near-instant is what stops `--no-verify` becoming a habit.
`--no-verify` is documented as the deliberate escape rather than pretended away.

The `git config core.hooksPath` step is now part of how a workstream session starts, so it belongs in
`docs/agents/landing-work.md` and is noted on ticket 04.

### Addendum, 2026-10-12: reversed by ticket 01

**The repository is public.** This ticket's decision did not survive contact with ticket 01, and the
reason is a cost it did not know about.

The reasoning above weighed "nothing mechanically enforced" against "publishing is irreversible", and
judged the enforcement worth little because it guards against one human's haste. That trade was
sound on the facts available. The missing fact: on a Free **private** repository, GitHub Actions does
not run at all. Three jobs, zero steps, no logs, reproducible. So the price of staying private was
never "no enforcement" — it was **no CI**, which makes task I-5's "done when" unreachable and G1a
unclosable. That is not a trade-off, it is a dead end.

The human chose public. Both things GitHub gates behind Pro for private repositories — Actions and
branch protection — are free on a public one, which is what GitHub's own 403 message said. CI went
green on the same re-run, and `main` now has all three checks required.

What stands from the reasoning above: the irreversibility is real, and it was accepted knowingly
rather than overlooked. A safety scan ran first and found nothing sensitive in tracked files.

What falls: the hook is no longer the *only* teeth, because protection is now enforced server-side.
The hook decision still stands on its own merits — it catches a problem before a push rather than
after — so `.githooks/pre-push` remains a handoff artifact, but it is a convenience now, not the
last line of defence.

### What this changed elsewhere

- **Ticket 01 is unblocked**, and creates a **private** repository.
- **Ticket 03 is now blocked by ticket 01, not by ticket 02.** Code owners does not function on a
  Free private repository, so GitHub may not evaluate `CODEOWNERS` at all here — which would make
  the whole question nearly moot and option 5 pointless. Ticket 01 observes whether the error list is
  even computed.
- **Ticket 05's third item is settled**: an item may be attested as convention-enforced, and branch
  protection is the first one that will be.
- The handoff now has a third artifact: `.githooks/pre-push`.
