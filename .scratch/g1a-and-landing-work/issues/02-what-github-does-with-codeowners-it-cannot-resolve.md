# What GitHub does with CODEOWNERS it cannot resolve

Type: research
Status: resolved

## Question

`CODEOWNERS` names team handles under an organisation that does not exist
(`@spa-platform/ws2-hosting` and eight others). The repository will be a private personal one, where
teams cannot exist at all. The decision about what to do (ticket 03) waits on knowing what GitHub
actually does, rather than on a guess.

Find out, from GitHub's own documentation and its behaviour:

1. **What happens to an unresolvable owner.** Does GitHub show a syntax error on the `CODEOWNERS`
   file, warn, or silently ignore the line? Is there a UI surface that reports it, and does it block
   anything or merely annotate?
2. **What a personal repository supports.** Can `CODEOWNERS` work at all without an organisation?
   Does automatic review assignment function with user handles on a private personal repository?
3. **Whether an unresolvable entry weakens anything we rely on.** Specifically: does a broken
   `CODEOWNERS` affect required-reviewer rules or branch protection, or is it inert?
4. **Whether a comment-only ownership record is a recognised pattern.** Is there a documented way to
   record intended ownership without GitHub trying to resolve it — for instance a file that is not at
   a path GitHub reads.
5. **Branch protection on a private personal repository.** Which plan is required for required status
   checks and rulesets. This overlaps ticket 01, which tries it directly; the documented answer and
   the observed answer are both useful and may differ.

Note that `git`'s own `CODEOWNERS` has no meaning: the file is a GitHub feature. Check A-05 parses it
as plain text and does not care whether a handle resolves, so the platform's own enforcement is
unaffected either way. That is worth confirming rather than assuming.

## Answer

Full findings, with a dated source per claim:
[`research/02-codeowners-findings.md`](../research/02-codeowners-findings.md). Sources retrieved
2026-10-12.

1. **An unresolvable owner is annotated, not fatal, and not silent.** GitHub parses the line, assigns
   nobody, and records a per-line error of kind `Unknown owner`. It surfaces in the CODEOWNERS file
   view and at `GET /repos/{owner}/{repo}/codeowners/errors`. It blocks nothing by itself, and one
   bad line does not invalidate the others. Observed on three public forks of `github/docs`, whose
   single owner line names a `@github/...` team: each reports `Unknown owner`, while `github/docs`
   itself reports none.
2. **CODEOWNERS is evaluated on personal repositories, but on a private one it needs GitHub Pro.**
   Free covers public repositories only. On a personal account only `@username` or a user email can
   resolve, never `@org/team`, and the owner must be a collaborator with write access. So none of our
   handles can resolve on a personal account under any plan.
3. **It is inert with respect to everything this project relies on.** Required status checks,
   required approval counts, force-push and deletion blocks, linear history and signed commits are
   all configured independently. The only coupling is the optional "Require review from Code Owners".
   Confirmed independently against `.github/path-ownership.py`: it reads the root file as text and
   makes no network call, so check A-05 is unaffected by resolvability.
4. **GitHub documents no pattern for recording ownership elsewhere.** It documents only the three
   paths it reads — `.github/CODEOWNERS`, the root, then `docs/`, first found wins — so a record at
   any other path is simply not an input. The recognised precedent is outside GitHub: Kubernetes'
   Prow `OWNERS` files, which GitHub's feature was modelled on.
5. **GitHub Pro is the minimum for a private personal repository**, as of 2026-10-12, for protected
   branches, rulesets and code owners alike. There is no separate gate for required status checks.
   Two things are organisation-only at any plan: restricting who can push to matching branches, and
   ruleset bypass lists.

Two corrections to this ticket and the map, both verified here:

- **Seven distinct handles, not nine.** `integrator`, `ws1-infra`, `ws2-hosting`, `ws3-identity`,
  `ws4-proxy`, `ws5-fixtures`, `ws6-verification`, across 26 rule lines.
- **A-05 reads the root file; GitHub prefers `.github/CODEOWNERS` over it.** Different precedence,
  which ticket 03 can use rather than work around.

Two mechanical facts ticket 03 needs:

- **Commenting out the owner lines is not an option.** A-05 would have no rules, every path would
  resolve to no owner, and the check fails closed. Verified against the script.
- **A minimal `.github/CODEOWNERS` shadows the root file for GitHub only.** That is the basis of the
  new option 5 in ticket 03.

Four things could not be established and were flagged rather than guessed: whether "Require review
from Code Owners" can block a merge when no owner resolves; whether a private repository on a plan
without code owners still computes the error list; whether a Free private repository refuses branch
protection outright or accepts it unenforced; and **the current price of GitHub Pro**, which
`github.com/pricing` no longer lists at all while the docs still describe Pro as a current plan. The
first three are observations ticket 01 can make. The fourth is a question for the human.
