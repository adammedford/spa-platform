# 66: Hold cross-document claims with a check, not with review

**Task:** none (retrospective, 2026-10-16)
**Owner:** integrator
**Blocked by:** None
**Status:** ready-for-agent

## Snapshot and current scope

The problem statement and counts below record the retrospective's pre-PR41/PR42 snapshot, not
current trunk. PR41/PR42 landed the method allowlist telemetry write, its composed assertions,
and the related contract corrections; tickets 62–65 are now tracked on trunk. Claims below that
trunk stops at 61, that the 405 write is unpushed, or that fifteen exception sites still await that
write are historical evidence. Their deletion obligation is satisfied by those landed changes.
The proposed cross-reference checks, test companion and landing-script body option remain future
work; this ticket does not claim them implemented. Ticket 67 is the separate G2 session-composition
work. The duplicate 61 is resolved: task 6.1a's ticket is 68 now, renumbered by
cross-reference-checks ticket 04, and the security suite's flake ticket kept 61.

## Problem Statement

A document in this repository can assert something the repository contradicts, and nothing fails.

On 2026-10-16 one false sentence in the contracts — that every rejection reaches
`security.rejections`, when the method allowlist's 405 does not — took six review rounds and four
pull requests to eliminate. It survived in nine places across four files. Every round found a new
defect in the *fix* rather than in the original claim: a missed file, a missed phrasing, a missed
path scope, an invented test-coverage claim, and a qualifier scoped to a rule id that two stages
write when only one was excepted.

None of it was reachable by a test. `Contracts.Tests` asserts that a contract file exists, exceeds
200 bytes, and contains the literal `Freezes at:`. So the only thing standing between a frozen
contract and a falsehood is a reviewer reading carefully, and `contracts-v1` was tagged over one of
these sentences while the review that found it was still running.

Three instances of the same shape are mechanical and currently unchecked:

- **A freeze claim that disagrees with its own target.** `contracts/README.md`'s index table carries
  a "Freezes at" cell per contract, and each contract carries its own `**Freezes at:**` header.
  Nothing compares them. The Pipeline row was landed reading "G1a, except the stage order at G1b",
  asserting a freeze at a gate the stage order never froze at, contradicting
  `contracts/pipeline.md` two directories away.
- **A cited ticket that does not exist.** `docs/agents/issue-tracker.md` says a document citing a
  ticket by path "is citing a file every other session and CI can open, which is the whole point of
  citing it". Merged code in `tests/Gateway.SecurityTests` cites tickets 63 and 64; `origin/main`
  stops at 61.
- **Two tickets sharing a number.** `.scratch/overnight/issues/` currently holds two files numbered
  `61`, written by two sessions that could not see each other's unlanded work.

Separately, `tools/land.sh` cannot do what `docs/agents/landing-work.md` requires of it. The guide
says a merge body carries `Outstanding:` lines; the script accepts only `--subject`. Five merges in
this session were landed by running the script's sequence by hand — fetch, fast-forward,
`tools/roadmap.py`, commit the refresh, push — which is precisely the error path the script exists
to prevent.

## Solution

Extend `.github/steering.py` from the two steering files to the whole tracked tree, and give it the
test companion it is the only check in its family to lack.

Its docstring already carries the rule: **"No dangling pointer."** The three new rules are that rule
applied wider. A pointer that resolves to nothing and a cell that disagrees with the file it indexes
are the same defect — a claim about the repository that the repository does not support — and they
belong behind one check that runs in the pre-push hook's one-second tier, beside the steering rules
it already holds.

Add `--body-file` to `tools/land.sh`, so landing a pull request through the sanctioned path can
carry the body the guide requires.

## User Stories

1. As an integrator, I want the contracts index checked against each contract's own freeze header,
   so that a freeze gate cannot be claimed in the index and denied in the file it points at.
2. As a workstream agent, I want a cited ticket number to resolve to a file, so that following a
   citation in merged code lands on a spec rather than on nothing.
3. As a session filing a ticket, I want a duplicate number to fail before it is pushed, so that two
   sessions working concurrently cannot both claim `61`.
4. As a reviewer, I want these three classes caught mechanically, so that my attention goes to the
   judgement calls no check can make instead of to cross-referencing cells by hand.
5. As an integrator landing a pull request, I want `tools/land.sh` to take a merge body, so that the
   `Outstanding:` lines `landing-work.md` requires survive the merge.
6. As an integrator, I want `land.sh` to remain the one sanctioned merge path, so that the roadmap
   derivation it performs is never skipped because the script could not express what was needed.
7. As a maintainer of `steering.py`, I want a test companion, so that widening its charter does not
   silently break the two rules it already holds.
8. As a session running `git push`, I want the new rules in the one-second tier, so that the guard
   costs an amended commit rather than a red pull request.
9. As a session on a machine without `python3`, I want the check to report that it did not run,
   matching how the existing checks degrade.
10. As an author of a contract, I want the failure message to name both sides of the disagreement,
    so that I can see which of the index and the header is wrong without opening either.
11. As an author citing a ticket early, I want a documented way to name one that does not exist yet,
    matching how `PLANNED` already exempts a promised path in `steering.py`.
12. As a reviewer of this change, I want each new rule to fail on a case built for it, so that a
    rule that cannot fire is caught before it ships.
13. As a session reading `docs/agents/issue-tracker.md`, I want the numbering convention to say what
    happens when several sessions file concurrently, so that the collision has a documented answer
    rather than a check that only reports it.
14. As an integrator, I want `CODING_STANDARDS.md` to carry the scoping rule that cost four review
    rounds, so that the reviewer agent enforces it on the next contract sentence.
15. As a reviewer, I want `docs/agents/review.md` to carry the search discipline that ended the
    cycle, so that the next reviewer greps the noun rather than the sentence.

## Implementation Decisions

**One seam.** All three rules go into `.github/steering.py`. It is an existing check, already wired
into both the pre-push hook and the CI step named "Steering files claim no progress and every
pointer resolves", and its `PATHLIKE`/`dangling()` machinery already resolves backticked paths
against the tree. Widening it costs no new file and no new wiring. The CI step's name and the
script's module docstring both need updating to say what it now covers, since the current name
describes only the narrower charter.

**Rule one — the freeze column agrees with the header.** Parse the index table in
`contracts/README.md`, take each row's target file and its "Freezes at" cell, read that file's own
`**Freezes at:**` header, and fail when the cell's leading gate differs from the header's gate. The
cell is allowed to say more than the header — `G1a; stage order changed at G1b` and `G1a, except the
`oauth` section at G1b` are both legitimate — so the comparison is on the leading gate token, not on
string equality. A cell naming a *different* gate than its target, or naming a second gate as the
freeze rather than as a change, is the failure.

**Rule two — a cited ticket resolves.** Scan tracked files for ticket citations and fail when the
number resolves to no file under `.scratch/*/issues/`. The citation shapes seen in the tree are
`ticket NN`, `tickets NN and NN`, and `overnight ticket NN`; the pattern must cover all three,
because the session that introduced the dangling citation wrote two of them and a narrower grep
found neither. Follow `PLANNED`'s precedent for a ticket named before it is filed: an explicit
allow-list in the script, which fails once the ticket appears, so the list cannot outlive its
entries.

**Rule three — ticket numbers are unique per feature directory.** Within each
`.scratch/<feature-slug>/issues/`, no two files share a leading number. Numbering restarts per
directory by design, so the check is scoped to one directory at a time rather than across the
tracker.

**The test companion.** Add `.github/steering_tests.py`, following `path_ownership_tests.py`: build
a throwaway tree per case rather than asserting against the live repository, so a case is a shape
rather than a search. It must cover the two existing rules as well as the three new ones —
`steering.py` is the only check in its family shipping untested, and widening it is the moment to
close that. Wire it into the CI step beside the check itself, as `plan-secrecy-tests.py` and
`roadmap_tests.py` already are.

**`tools/land.sh`.** Add `--body-file <path>`, passed through to `gh pr merge --body-file`. The
existing `--subject` handling and the "no subject given" confirmation prompt stay as they are. The
script's header comment documents the two forms a merge subject may take; it should now also say
that the body carries the `Outstanding:` lines, with the pointer to `landing-work.md` that makes
that requirement findable.

**Two prose additions, which no check substitutes for.** `CODING_STANDARDS.md` gains the scoping
rule: a sentence stating a universal names its exceptions by the *writer*, never by an identifier,
because an identifier may have several writers and only some be excepted. `docs/agents/review.md`
gains the search discipline: grep the noun a claim cannot paraphrase away, then verify the claim's
scope is the thing that actually has the property. Both are judgement calls about cross-file
consistency, which is what `CODING_STANDARDS.md` is for.

## Testing Decisions

**What makes a good test here.** Each rule is tested through the check's own exit code and output on
a tree built to trip it — external behaviour, not the parsing helpers underneath. A test that
asserted on `dangling()`'s return shape would pin an implementation detail; a test that builds a
tree with a mismatched freeze cell and asserts the check fails pins the rule.

**Every rule fails on a case built for it.** This is the bar the repository already states in
`CODING_STANDARDS.md` → "An assertion must be able to fail", and the reason it matters here is
specific: a cross-reference check whose pattern does not match the shape in the tree passes
silently and looks identical to a clean repository. The session that produced this spec ran a
`git grep -E` with a `\b` that POSIX ERE does not support; it matched nothing and read as "zero stale
references" until a second search contradicted it. Each rule therefore needs a red case and a green
case, and the red case must be built from a shape actually seen in the tree, not an invented one.

**Modules tested.** `.github/steering.py` only. `tools/land.sh` is not tested — the repository has
no shell test harness, and adding one is out of scope below.

**Prior art.** `.github/path_ownership_tests.py` is the closest model: throwaway repositories built
per case, the module resolved from `__file__` so the root can be patched, `contextlib` and `io` used
to capture output. `tools/roadmap_tests.py` and `.github/plan-secrecy-tests.py` are the same family
and show the CI wiring pattern — tests run immediately before the check in the same step.

## Out of Scope

- **The 405's actual write.** `MethodAllowlist` writing its rule id to `IRequestTelemetryFeature` is
  workstream 4's, tracked as ticket 63, already written on an unpushed branch. Workstream 6's
  assertion is ticket 64.
- **The 82 stale `stage 1`/`stage 2` references** in `src/Platform.Security`,
  `src/Platform.Telemetry`, `src/Platform.Hosting`, `src/Platform.Proxy` and their tests, including
  `RequestTelemetry.cs`'s inverse-wrong "outside both". Workstream 2, 4 and 6's paths.
- **Workstream 1's five `infra/` stage references**, two inside Terraform `error_message` strings.
- **A shell test harness.** `tools/preflight.sh` shipped five defects this session and its
  parse-versus-shell agreement is verified only by manual runs. A real gap, and a larger piece of
  work than this spec; filing it separately keeps it visible.
- **Enforcing review before merge.** Two pull requests merged without a completed review this
  session, and one was tagged mid-review, which is how a false guarantee ended up frozen. Worth its
  own decision, because the options differ in kind — a check on `land.sh`, a branch protection rule,
  or accepting it as the documented weak link.
- **Dropping the `@docs/agents/aws-rules.md` import** from `CLAUDE.md`, which loads 76 lines of
  vendor-generated rules into every session for services this platform does not use.

## Further Notes

The deletion obligation matters to whoever picks this up. Fifteen sites currently say "except the
method allowlist's 405", and all fifteen become wrong in the other direction the moment ticket 63
lands its one line. The list is recorded in
`contracts/changes/6.4-request-telemetry-feature.md`, which is the file that implementer will be
reading — but nothing enforces it, and the new freeze-column rule does not cover it. If a fourth
cross-reference rule is ever worth writing, that is the candidate.

On why this is a check and not a standard: the three rules are fixed, syntactic, and decidable from
the tree alone, which is the test `CODING_STANDARDS.md` itself applies when deciding between a rule
and a guardrail. The two prose additions in this spec are the residue that genuinely cannot be
decided mechanically.
