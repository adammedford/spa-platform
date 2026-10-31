# ADR-0022: Hold review at the one sanctioned merge path, not with branch protection

- **Status:** Accepted. See #507 (merged 2026-10-28 as 1e9ea6b) for the five review-debt merges
  named in Consequences: a record naming each head says `merge`, and the `landing-work.md` section
  that bullet cites was removed.
- **Date:** 2026-10-18
- **Deciders:** integrator, on Adam's confirmed answers
- **Plan ref:** No decision row. #67, which carries archived overnight ticket 70's questions Q1 to
  Q19, confirmed by Adam on #67 on 2026-10-17, and Q20's answer there (#66 lands first). Drafted by
  the overnight run of 2026-10-18 and committed by the human, because ADR-0021 decision 4 lets a
  scripted session draft an ADR and never commit one.

## Context

Every agent session and the human act through one GitHub account. It opened and merged every pull
request here, its formal reviews are all `COMMENTED`, and GitHub says "pull request authors cannot
approve their own pull requests". `main`'s protection requires the checks `Documents`,
`Build and test` and `Terraform` with `strict: false`, requires no review, and has `enforce_admins`
off; there are no rulesets. gh refuses a pull request GitHub reports as blocked unless given
`--admin`, and `--admin` merges "a pull request that does not meet requirements", skipping the
required checks with the rest.

`docs/agents/landing-work.md` required a review by "a session other than the one that wrote the
code" and let the integrator's own work merge "on CI plus the human", calling that the weakest link.
Nothing enforced either. On 16 October 2026 four integrator pull requests merged without a completed
review: #34 and #35 on fix commits only their writer verified, #36 with no review, and #37 two
minutes before `contracts-v1` was tagged on the roadmap refresh after it, while its review was still
running. That review then found claims in three contracts, `contracts/pipeline.md` and `Program.cs`
that #39 and #40 corrected on `main` 34 and 71 minutes after the tag. The tag still points at the
false sentences. None of the four broke the written rule: the carve-out exempted all of them.

Reviews were recorded free-form, and a sha in a record proved nothing: `a80e906`, #34's merge body,
named the merged head as the commit the fixes went into, and that head was verified only by its
writer. GitHub's own record of a review's commit is no better, because `commit_id` "defaults to the
most recent commit in the pull request when you do not specify a value".

## Decision

1. **A review covers exactly the head its record names.** Commits pushed after it, fix commits
   included, need a review of their own by an agent session that wrote none of them, which may cover
   only the commits since the last reviewed head (Q1).
2. **Every pull request into `main` is reviewed by an agent session that wrote none of its
   commits.** The integrator's carve-out goes: the unit of independence is the agent session, not
   the workstream. Nothing is exempt by what it touches or where it came from, the overnight run's
   integration pull request included, and the human's reading is the decision to land, not a
   review (Q2, Q8, Q9).
3. **The guard is against haste.** A record cannot show who wrote it under one account, and that is
   accepted: this is a sequencing guard, not a permission (Q3).
4. **`tools/land.sh` holds it.** It refuses unless the latest review record naming the pull
   request's current head says `merge`, merges with `--match-head-commit` pinned to that head, and
   never passes `--admin`, so the required checks keep binding. `tools/review_record.py` reads the
   records, with a test companion in CI, and `tools/land_tests.py` runs the refusals through the
   real script (Q6, Q14, Q20).
5. **The review record is one fixed line**, `Reviewed <40-hex head> against <40-hex base>: merge` or
   `: hold`, on a line of its own in a pull request comment or a review submitted as a comment, full
   shas only, posted by an account the repository lets in (`author_association` `OWNER`, `MEMBER`
   or `COLLABORATOR`), because the repository is public and anyone signed in can comment; the
   latest naming the current head decides, and the base is not compared. Its form is
   in `docs/agents/review.md` → "The review record" (Q7).
6. **`--unreviewed "<reason>"` is the one escape.** It merges pinned to the head it saw and writes
   `Unreviewed: <reason>` at the end of the merge body; an ordinary merge writes
   `Reviewed: <head> <record URL>` there. The script writes both, and nothing asserts them (Q12, Q13).
7. **No gate tag is cut while a review of a change it covers is open**: one running or holding,
   commits after the last reviewed head, a merge no record clears, or a re-review in flight. The
   rule is prose in `docs/agents/landing-work.md` → "Gates" with a `git log` reading command, and no
   hook or tag script (Q11, Q16).
8. **Only generated roadmap refreshes reach `main` without a pull request**, as prose until the rule
   is first broken (Q5).

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Branch protection requiring approving reviews | The author cannot approve their own pull request, so it needs a second identity or a bypass. A machine account every agent session on the workstation can reach gives the same self-attestation as a comment, plus an account and token to secure. The bypass is `--admin`, which skips the required CI checks too. `enforce_admins` would refuse the direct roadmap-refresh pushes and lock the human out whenever the second identity was unavailable (Q4). |
| A required "Reviewed" status check set by a workflow on review events | Holds for web-UI merges too, but brings back the required-check deadlock the cut removed when it took A-05 out of CI, needs a workflow with `checks: write`, and the same admin account bypasses it (Q6 c). |
| Prose only | It already was the rule, and four pull requests passed it in one evening (Q6 b). |
| GitHub's `commit_id`, or any mention of the head, as the record | Both fail open: `commit_id` defaults to the newest commit, and a mention would have passed `a80e906` (Q7 b, c). |

**This reopens** when a second person with their own GitHub account joins, or when a regulated
adopter takes the platform into its own organisation. Then required approving reviews from someone
other than the last pusher are the right control, as the governance audit's gap 8 recommends (Q10).

## Consequences

- A merge made outside `tools/land.sh`, through the web UI or a bare `gh pr merge`, is unchecked.
  `docs/agents/landing-work.md` already forbids it.
- The trailers are written by the script and asserted by nothing, which keeps the decision of
  g1a-and-landing-work ticket 11 that the merge body is not checked.
- The pull request that lands the check is held by convention, once: the `tools/land.sh` on `main`
  before it lands has no check, so a fresh agent session reviews it first and posts the first
  record in the new form.
- Five merges after `contracts-v1` carry no record naming their merged heads: #38, #39, #40, #44
  and #45. Q15 put their retrospective reviews before G2; `g2-local-slice` was tagged on
  2026-10-17 before that was done, and stands as cut. As read on 2026-10-18 none carries a comment
  or review. They are review debt the next gate tag waits for, listed with their heads in
  `docs/agents/landing-work.md` → "Review debt from before the check".
- The governance audit (`.scratch/governance-audit/spec.md`, local and untracked) said in gap 8
  that "no branch protection" was declared. That is corrected in the local copy to say that `main`'s
  protection requires the checks `Documents`, `Build and test` and `Terraform` and no review, with a
  pointer to this ADR; nothing else in the audit was changed, and none of it is imported here.
- `GLOSSARY.md` gains Review, Review record (its form now fixed), Open review, Agent session and
  Gate tag; Integrator is "a role that agent sessions play, not a person".
- The overnight workflow's morning step, a local file, may remind its caller that the night's pull
  request needs a review record naming its exact head. It is never the guard.

## Evidence

- `main`'s protection: `gh api repos/{owner}/{repo}/branches/main/protection`, read 2026-10-18.
- The incident: the bodies of #38 and #39; #37 merged at 04:45:09Z on 16 October 2026 and
  `contracts-v1` was tagged at 04:47:46Z on `3c5cfbb`; #39 and #40 merged at 05:21:57Z and 05:59:16Z.
- gh's flags: https://cli.github.com/manual/gh_pr_merge; `--match-head-commit` is sent as
  `expectedHeadOid`, and only when not empty (cli/cli `pkg/cmd/pr/merge/merge.go`, `http.go`), which
  GitHub's schema describes as the "OID that the pull request head ref must match to allow merge; if
  omitted, no check is performed".
- `commit_id`'s default: https://docs.github.com/en/rest/pulls/reviews#create-a-review-for-a-pull-request
- The guard can fail: `tools/review_record_tests.py` and `tools/land_tests.py`, with the mutations
  and their counts in the merge body that landed them.
