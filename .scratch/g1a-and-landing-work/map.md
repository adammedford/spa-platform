# Map: close G1a, and write down how work lands

Labels: `wayfinder:map`

## Destination

~~Six artifacts~~ **Two.** See "The cut" below; this list is the destination as it stood before 3d344fd.

1. **`skeleton-v1` tagged** on a GitHub-hosted repository whose CI has actually executed, attested
   by `docs/gates/G1a.md`.
2. **`docs/agents/landing-work.md`**, the procedure the six workstreams follow to land work, pointed
   at from one line in `AGENTS.md`. Ticket 05 made this **item 6 of G1a's checklist**, so the gate
   closes on it: G1a's scope is "the six workstreams can start", not merely "the skeleton is frozen".
   It and the `AGENTS.md` pointer must land in one commit, or the steering check fails on a dangling
   pointer.
3. **`.githooks/pre-push`**, added by ticket 06: the only teeth the convention has, since nothing is
   mechanically enforced on this plan. Ticket 03 adds a second assertion to it, and the same one to
   CI: fail if `.github/CODEOWNERS` exists. Ticket 11 decides whether a third joins them, asserting
   the merge body's required parts.
4. **`docs/gates/README.md`**, added by ticket 05: the checklist convention, stated now because
   `docs/plan/01-plan.md` cannot generalise it — G1b's exit test is circular. It and `G1a.md` must
   land in the same commit as the removal of `"docs/gates/"` from `PLANNED` in `.github/steering.py`,
   which fails when a planned path appears.
5. **`docs/adr/0014-*.md`**, added by ticket 03: why `CODEOWNERS` carries 26 deliberate errors. The
   one handoff artifact that is an ADR rather than a procedure, because `.scratch/` is not committed
   and the finding behind it would otherwise be lost. **Widened by ticket 08** to *path ownership
   under a single account*, covering A-05's unverifiable declaration as the same root cause, with
   `33eb5fb` as the worked example — which kept the count at six rather than seven.

6. **`tests/RepositoryChecks.Tests`**, added by ticket 08: twelve cases over `.github/path-ownership.py`
   and `.github/steering.py`, integrator-owned, in the solution. It lands **before `skeleton-v1`**,
   so G1a's item 5 cites a test rather than the hand-run transcript. The only destination artifact
   that is code.

Reaching it means the six workstreams can start with nothing unresolved about how work lands or how
G1a closes.

Every artifact is written **at handoff**, after the last ticket resolves, and ticket 10 decides
the order they land in — three of them are forced into shared commits by the steering check. This map decides; it does
not write them. The one exception is the repository itself, which is a task ticket because the tag
decision cannot be made until a runner has executed the workflow.

## Notes

**Domain.** `GLOSSARY.md`, and `docs/adr/` for decisions in the area. The vocabulary is fixed: say
**workstream**, never "team" or "lane"; **integrator** for the owner of the shared skeleton;
**change request** for a file under `contracts/changes/`; **gate** for a named checkpoint with an
exit test. `docs/plan/` is a frozen export and is never edited in place.

**Skills every session should consult.** `grilling` and `domain-modeling` by default. The effort is
process design, not feature work, so `tdd` does not apply.

**This map is not committed.** `.gitignore` excludes `.scratch/`, deliberately. Anything that must
outlive the effort has to graduate into one of the destination's two artifacts.

### Settled while charting

These came out of the destination grilling, so they are not tickets. They are the constraints every
ticket works inside, and the raw material of `docs/agents/landing-work.md`.

| | Decision |
| --- | --- |
| Repository | **<https://github.com/adammedford/spa-platform>, public, personal, Free plan.** Decided private by ticket 06, then **reversed by ticket 01**: Actions does not run at all on a Free private repository, so staying private meant no CI and an unclosable G1a. Public makes both Actions and branch protection free. Six workstreams are agent sessions driven by one human, so an organisation with six teams would be theatre; the consequence is that the seven CODEOWNERS handles cannot resolve. |
| Landing unit | One branch per task, merged with a non-fast-forward merge commit whose body carries the verification note, expressed as a PR on GitHub. Matches the history already in `main`. **Refined by ticket 07**: one branch can carry **several** merges, so a task too big for one session lands in several PRs from the one claim branch, which is deleted when the task lands rather than when a PR merges. A workstream driven by `/implement-spec` lands one PR spanning several of its own tasks, which is fine because its paths stay inside one owner. |
| A-05 | Stays path-inference plus an integrator declaration in a commit trailer. Author-based ownership is dead under one author. The declaration must cite a task id, so the escape hatch is auditable against the plan rather than against the writer's self-assessment. **Narrowed by ticket 08**: the grammar is git's own (`git interpret-trailers`, not a regex), the scope is the **tip commit** rather than the whole range, the task id is enforced, and a missing `python3` now fails rather than skipping the check. Who wrote it stays unverifiable, recorded rather than fixed. |
| Session isolation | One git worktree per workstream session. `.claude/` is gitignored. The three stale worktrees are pruned. |
| Gate evidence | `docs/gates/<gate>.md`, each exit item carrying the evidence that proves it, closing with `Attested by: <name>, integrator, <date>`. `git tag -a` points at it. **Agent sessions never attest**: they produce evidence, the integrator attests. "Signed" in task I-7 reads as attested by a named decider, not GPG. |
| Change request | The workstream merges the work it has, declares the request open in the merge body, and stops that task. Merged is not done: a task is done only when it leaves no change request open. Precedent: tasks 1.0 and 1.6a. |
| Review | `/code-review` is required on a workstream PR, run by a session other than the one that wrote the code, with its findings answered before merge. Carve-out: the integrator's own skeleton work has no second workstream to review it, so it merges on CI plus the human. That is the weakest link in the model and is known. |
| Branch protection | **Enforced on `main`**, free, since the repository is public: all three checks required, `strict: false`, `enforce_admins: false` so the human is never locked out, force-pushes and deletions blocked. Ticket 06 planned for convention-carrying instead; ticket 01 made that unnecessary. `.githooks/pre-push` is still a handoff artifact on its own merits — it catches a problem before a push rather than after — but it is a convenience now, not the last line of defence. |
| Session setup | **Corrected by ticket 04.** `git config core.hooksPath .githooks` is **one per clone, never per worktree**: the setting lives in the shared `.git/config`, `extensions.worktreeConfig` is unset, and the hook fires from a linked worktree with no extra config. The row's old claim is still wrong in ticket 06 and in `.githooks/pre-push:9`'s own header, both fixed at handoff. Ticket 04 made session start a block of idempotent assertions the session runs itself. |

## Decisions so far

<!-- One line per resolved ticket: the gist, then the link for the detail the ticket holds. -->

- [What GitHub does with CODEOWNERS it cannot resolve](issues/02-what-github-does-with-codeowners-it-cannot-resolve.md):
  an unresolvable owner is annotated (`Unknown owner`), not fatal, and inert with respect to status
  checks and every other protection rule; A-05 is unaffected because it reads the file as text. But
  on a **private personal** repository, protected branches, rulesets **and** code owners all need
  GitHub Pro, while a **public** repository gets all three free — which put a third option on the
  table and produced ticket 06. GitHub also reads `.github/CODEOWNERS` in preference to the root
  file, which ticket 03 can exploit rather than work around.
- [Whether the repository is private, and what mechanical enforcement costs](issues/06-whether-the-repository-is-private-and-what-enforcement-costs.md):
  **private, on Free.** Nothing is mechanically enforced, including code owners, and the gate
  checklist records the rule as convention-carried rather than enforced. Public was rejected despite
  getting every protection free, because enforcement guards against one human's haste rather than a
  hostile contributor while publishing an internal architecture is the only irreversible option on
  the table. The teeth are a tracked `.githooks/pre-push` running check A-05 and the build, wired by
  `git config core.hooksPath .githooks`; Pro, about $4/month and unconfirmed, is revisited the first
  time the convention fails. **Reversed by ticket 01** — see its addendum.
- [Create the repository and make CI run](issues/01-create-the-repository-and-make-ci-run.md):
  <https://github.com/adammedford/spa-platform> is **public**, **CI is green on a runner** (306 tests,
  build clean, Terraform valid, A-05 exercising its integrator-declaration path), and `main` is
  protected with all three checks required. Public because Actions does not run **at all** on a Free
  private repository, which made staying private a dead end rather than a trade-off. The workflow
  needed **no corrections** on a real runner. The history was rewritten to a noreply email first,
  because the account blocks pushes exposing a private address. CODEOWNERS errors **are** computed
  (26, all `Unknown owner`), so ticket 03 is live — and now matters more, since code owners is a
  working feature on a public repository.

- [CODEOWNERS under a single-account repository](issues/03-codeowners-under-a-single-account-repository.md):
  **option 3** — the root `CODEOWNERS` stays the ownership record, and GitHub's handle-resolution job
  is declared **unused** rather than broken. The 26 `Unknown owner` errors stand because they are
  true. Option 5 was the front-runner and was **rejected on new evidence**: a `.github/CODEOWNERS`
  does suppress the root file's errors, but by shadowing the root file completely and **silently**
  (0 errors, `{"errors":[]}`, no diagnostic anywhere), which would delete the only honest signal that
  the handles do not resolve. That same precedence rule is a trap — a later `.github/CODEOWNERS`
  would silently decouple GitHub from the record while **A-05 kept passing** — so the handoff adds a
  guard to `.githooks/pre-push` and CI. Two defects fixed in passing: `GLOSSARY.md`'s "Not 'team'"
  ban contradicted by 26 team handles (resolved in the glossary, distinguishing *handle* from
  *team*), and `CODEOWNERS:4`'s header now instructing against the state the file is in. Option 4
  (create the organisation) went out of scope.

- [How a workstream session takes a task](issues/04-how-a-workstream-session-takes-a-task.md):
  **the branch is the claim** (`git worktree add -b task/<id>-<slug>`; git already refuses a double
  claim, so exclusivity is mechanical rather than conventional), a **fixed six-pointer hand-off** with
  nothing restated from the frozen plan, **one session per workstream** landing one branch and PR per
  row with `/implement-spec` opt-in, the **session verifies its own `Needs`** ("a gate name means the
  tag exists"), and the **merge subject names every task id**. Two of the ticket's premises were
  false: the hooks config is per-clone not per-worktree, and git already prevents a double claim.
  Also answers the map's old "`/code-review`'s spec axis" fog: for a workstream task the spec is the
  plan row plus the contracts rows naming it, cited by path. No per-task spec file.

- [What G1a's checklist requires as evidence](issues/05-what-g1a-s-checklist-requires-as-evidence.md):
  **six items** (the exit test's clauses, with I-1 to I-5 in a "produced by" column rather than as
  items), each carrying **five evidence fields** — behaviour, run URL, head sha, job **and step**
  name, and the quoted decisive line, because a URL alone decays and "CI was green" would otherwise
  attest a vacuum: four of fifteen test projects hold any tests and all four `tests/Gateway.*` are
  empty, so **A-01 to A-04 run nowhere**. Two premises corrected: the exit test was misquoted (two
  columns fused), and CI **does** prove the 404 host-based on a runner — the item CI structurally
  cannot evidence is **A-05's failing path**, which takes the one labelled hand-run transcript.
  `docs/gates/README.md` states the convention now, because the plan cannot generalise it (G1b's exit
  test is circular). The tag carries gate, checklist path and run URL, no sha, and **targets a commit
  that already contains its own checklist**. The human decided **item 6: `landing-work.md` exists**,
  so G1a's scope is "the six workstreams can start", not merely "the skeleton is frozen".

- [When a plan row is bigger than one session](issues/07-when-a-plan-row-is-bigger-than-one-session.md):
  **landed and done are two states**, and ticket 04's `Needs` check asks for the weaker one — a merge
  naming the id means every dependent has what it needs, with anything absent declared; done means
  "Done when" holds. Two reserved subject prefixes carry the signal (`Merge task(s) <id...>:` landed,
  `Merge part of task <id>:` not yet), **no sub-ids** ever, and an `Outstanding:` trailer records what a
  landed-but-unfinished task still owes plus any unproven test ids. Partial merges are the **normal** way
  a big task lands and the claim branch outlives them; a resuming session recovers its place by diffing
  the contract's rule list against the test names on the branch, so checking "Done when" and resuming
  are the same act. `/implement-spec` is the recommended tool, with implementer branches named
  `impl/*` — correcting ticket 04 — and its own `code-review` not discharging the separate-session
  review. Five premises were wrong, two helpfully: **2.1 is forced partial at any size** (its oauth
  rules are draft until G1b) and yet unblocks 2.2, 2.3 and 2.6 **legitimately**, since N-14 and S-D8 are
  the duplicate-host rules and no dependent touches oauth. The two-state model is **already in `main`**,
  unnamed, in the 1.0/1.6a merge; and **no gate exit test consumes "done"** — all seven are test ids and
  behaviours — so "done" never needed a mechanical consumer. Surfaced ticket 11.

- [What a workstream does when A-05 fails, and how wide its escape hatch is](issues/08-what-a-workstream-does-when-a-05-fails.md):
  **two branches, not three** — revert the stray file, or file a change request and stop the task.
  The third case collapses: A-03 makes a need for another workstream's code a need for
  `Platform.Abstractions`, which is already an integrator path, so `GLOSSARY.md`'s *change request*
  entry stays scoped to integrator paths. **A workstream never requests a declaration.** The hatch
  is narrowed on four axes — git's own trailer grammar replacing the hand-rolled regex, the **tip
  commit** replacing the whole range, an enforced task id, and `python3` failing instead of
  skipping — and left open on one: nothing can verify who wrote it, which is the floor of one-author
  ownership. A-05 gets a real test before `skeleton-v1` (artifact 6). Three findings came from
  probing: **none of the five declarations in `main` is a git trailer** (a blank line before
  `Co-Authored-By:` puts each outside the block); **the truncation 357a475 claimed to fix is still
  live** in 91e2103, written after the fix, because it taught the regex *indented* continuations
  while the author wrapped unindented; and **33eb5fb** is a cross-owner merge whose own body carries
  no declaration. The tip rule is forward-only by construction, so no history goes red. Ticket 11
  inherits git's trailer grammar and the precedent for pre-convention merges.

## Not yet specified

In scope, not yet sharp enough to ticket.

- **How a workstream's plan rows become `/implement-spec` tickets, across several rows at once.**
  Narrowed twice. Ticket 04: since `/implement-spec` is **opt-in**, this is a per-workstream recipe
  (`/to-tickets` over that workstream's rows, `Blocked by` from the `Needs` column) rather than a
  standing obligation to convert all 54 rows. Ticket 07 then settled the **single oversized row**
  case outright — the integration branch *is* the claim branch, implementer branches are `impl/*`,
  and the tickets are a session-local work plan that dies with the effort. What stays fog is the
  multi-row shape: whether a workstream converting five ready rows runs one integration branch across
  them (which would land one PR naming five ids, against one branch per task) or one per row. Still
  fog because nobody has opted in, and the answer turns on which workstream goes first — WS4 has five
  independent ready rows, WS3 a five-deep chain.

## The cut, and what is left of this map

**13 October 2026, commit 3d344fd.** The map's destination was six handoff artifacts. It is two.

The ratio that forced it: 6,437 lines of docs, contracts, tickets and process scripts against 611
lines of `src/`, of which 198 are stub entry points that do nothing. Nothing in the platform works,
all 54 plan rows are unstarted, and the effort had spent five sessions designing how six
workstreams -- which are one person driving agent sessions -- would coordinate.

What was cut, and what each cut retired:

| Cut | Retired |
| --- | --- |
| `CODEOWNERS` -> `OWNERSHIP.md`, role names not handles | the 26 errors, the shadowing trap and its guard, ADR-0014, and tickets 02, 03 and 09 |
| the `Path-Ownership` declaration, entirely | ticket 08's four narrowing axes, `RepositoryChecks.Tests`, and ticket 11 |
| A-05 out of CI, into `.githooks/pre-push` only | the required-check deadlock, and the need for any override at all |
| the gate checklist convention | `docs/gates/`, the five evidence fields, the attestation line, and most of ticket 05 |

What survived, because it earns the parallel-agent bet: `contracts/` and `Contracts.Tests`,
`Platform.Abstractions` and the 1,112 lines of doubles in `Platform.Testing`, CI's build, test,
Terraform and 404-on-unknown-host jobs, `.github/steering.py`, the freeze-marker test,
`docs/plan/`, ADR-0001 to ADR-0013, and `GLOSSARY.md`.

**Tickets 09, 10 and 11 are closed unresolved**, each carrying why. Tickets 01 to 08 stand as the
route actually walked; several of their decisions were reversed here and their closing sections say
so. The two artifacts landed in 3d344fd on the branch that was already open, so there was no
handoff sequence to decide.

**This map is done.** What opens next is `docs/plan/03-workstreams-and-tasks.md`, read against
`git tag`.

## Out of scope

Past the destination. Never graduates; returns only as a fresh effort.

- **Creating the `spa-platform` organisation.** Option 4 of
  [CODEOWNERS under a single-account repository](issues/03-codeowners-under-a-single-account-repository.md).
  How the account is structured is past this map's destination, which covers how work lands. Its only
  benefit was making the seven handles resolve, and that ticket decided nothing wants them to.

- **Gate G0 and the Cognito items.** Needs AWS credentials, a Cognito pool and a browser at a
  workstation. After this map's destination.
- **Gate G1b and task I-6.** Depends on G0 reporting.
- **What the six workstreams then build.** This map covers how work lands, not the content of any
  workstream's tasks.
- **Wiring the plan policy, P-01 to P-03.** Needs the read-only AWS role from task 1.0 applied and
  the allowlist from task 1.9 written. CI already has the hook and emits a notice until both exist.
