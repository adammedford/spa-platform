# 70: What enforces review before a merge

**Task:** none (raised by overnight ticket 66, "Out of Scope")
**Owner:** integrator
**Blocked by:** None
**Status:** ready-for-human

## Problem

`docs/agents/landing-work.md` requires a pull request to be reviewed by a session other than the
one that wrote it, and says the integrator's own work "merges on CI plus the human. That is the
weakest link here, and it is known." Nothing enforces either. In the session ticket 66 records, two
pull requests merged without a completed review and one was tagged while its review was still
running, which is how a false guarantee came to be frozen in `contracts-v1`.

## The decision

The options differ in kind, which is why this is a decision rather than a task:

- **A check in `tools/land.sh`** — refuse to merge unless the pull request carries an approving
  review or a review-evidence line. Cheap, and holds only for merges through the script.
- **A branch protection rule** requiring an approving review — holds for every merge, but on a
  single-account repository the author cannot approve their own pull request, so it needs a
  second account or a bypass, and a bypass is the weak link again.
- **Accept it as the documented weak link** — and say in `landing-work.md` what a tag must wait for,
  since the tag mid-review was the costliest instance.

## Done when

- [ ] One option is chosen and recorded as an ADR under `docs/adr/`.
- [ ] `landing-work.md` states the rule, including whether a gate tag may be cut while a review is
      open.

## Grilling record

Run on paper in the overnight run of 2026-10-17, for Adam, who answers in bulk in the morning. His
standing answer of 2026-10-15 accepts every recommendation, so each round below treats the previous
round's recommendations as decided and recomputes the frontier from them. Facts come from the
repository, read-only calls to GitHub's API, and GitHub's own documentation; nothing was written
anywhere but this file.

**Snapshot.** `origin/main` at `eb65be8` (2026-10-16 22:40 -0500), as this checkout last fetched
it. Not re-fetched tonight, to keep this session's writes out of the `.git` the lanes share, so
anything landed after that is not reflected. Gate tags reachable from it: `skeleton-v1`,
`g0-mechanism`, `contracts-v1`.

### Facts established (not questions)

1. **One account does everything.** `adammedford/spa-platform` is public, on a personal account.
   All 45 pull requests target `main`, all 45 were opened and merged by `adammedford`, and
   `reviewDecision` is empty on every one. The eleven formal reviews in the history (#5 to #15, one
   each) are all `COMMENTED`, `author_association: OWNER`. GitHub: "Pull request authors cannot
   approve their own pull requests." Actions cannot either (`can_approve_pull_request_reviews:
   false`).
2. **What `main`'s protection does today** (`GET .../branches/main/protection`): it requires the
   checks `Build and test` and `Terraform`, `strict: false`; `enforce_admins: false`; no review
   requirement; force-pushes and deletions blocked. No rulesets. GitHub: "the restrictions of a
   branch protection rule don't apply to people with admin permissions to the repository" unless
   bypassing is disallowed, so the one account can bypass all of it.
3. **The incident, from the pull requests' own bodies: four pull requests, not two.** #38: "#34,
   #35 and #36 were merged before they were properly cleared: I wrote the fix commits for #34 and
   #35 and then verified them myself, and I opened and merged #36 with no independent review at
   all." #39: "#37 merged and `contracts-v1` was tagged while that review was still running." #37
   was opened at 04:35:04Z and merged at 04:45:09Z on 2026-10-16; `contracts-v1` was tagged at
   04:47:46Z, 2 min 37 s later, on `3c5cfbb`, the roadmap refresh after that merge. The review went
   on to find three contracts, then `contracts/pipeline.md` and `Program.cs`, asserting a count the
   method allowlist's 405 did not reach; #39 and #40 corrected `main` 34 and 71 minutes after the
   tag. The tag still points at the false sentences.
4. **All four were integrator pull requests** (`integrator/*` branches): the work the carve-out
   exempts. Under the written rule they needed only CI and the human, so none of them broke it. The
   sessions had started reviews on three, merged ahead of or around them, and gave the fourth none.
5. **Reviews are recorded free-form, on the pull request.** 15 of 45 merged PRs carry a comment or
   review naming the full merged head (#9, #12, #13, #15, #19, #23, #28 to #33, #41 to #43). 13
   carry no comment or review at all (#2 to #4, #26, #27, #35 to #40, #44, #45). Merge bodies began
   naming the merged head on 2026-10-15 (11 of them, `97098df` to `df2c754`) and stopped at
   `37fdca2` (#44) and `eb65be8` (#45). The shapes vary: "Reviewed `<sha>` against `<sha>`" (#41,
   #43), "Review disposition at `<sha>`" (#33), "PASS at `<sha>`" (#42).
6. **A sha in a record is not proof that sha was reviewed.** `a80e906`, #34's merge body: "Reviewed
   by a second session with `/code-review`, which held the merge on two defects. Both were real and
   both are fixed in 833bb79". It names the merged head as where the fixes went; the head reviewed
   was an earlier one, and `833bb79` was verified only by its writer (fact 3). A check accepting any
   mention of the head would have passed it.
7. **GitHub records a review's commit, but defaults it unsafely.** Every submitted review carries
   `commit_id`; #9's sits on `f34a083` while the merged head was `d49b860`, three commits later. The
   REST API says `commit_id` "Defaults to the most recent commit in the pull request when you do not
   specify a value", so a review submitted after the author pushes again attaches to a head the
   reviewer never read.
8. **The merge path.** `tools/land.sh` runs `gh pr merge --merge`, with neither `--admin` nor
   `--match-head-commit`. gh 2.96.0 refuses a pull request GitHub reports as blocked ("the base
   branch policy prohibits the merge") unless given `--admin`, and `--admin` skips every
   requirement, the two required checks included (cli/cli `pkg/cmd/pr/merge/merge.go`).
   `--match-head-commit` is sent as `expectedHeadOid`, so GitHub refuses the merge if the head has
   moved.
9. **Direct pushes, and merges git cannot attribute.** Six commits reached `main` without a pull
   request: the import `8a78eae`, and five generated roadmap refreshes (`fed3172`, `02a7c45`,
   `55c4599`, `3c5cfbb`, `ba40be2`). Each refresh landed on a branch whose required checks had not
   run on it, which a protected branch accepts only from an account allowed to bypass. Five merges
   carry GitHub's default subject (#1, #2, #9, #12, #45). Nothing in git says which merges went
   through `land.sh`: its only trace is a refresh commit, made only when the generated blocks move.
10. **The overnight workflow never merges to `main`.** `.claude/workflows/overnight.js` merges lanes
    into `overnight/<date>` locally after a separate reviewer agent, which fixes findings itself and
    commits `Review: ...`; the human pushes the branch and opens one PR in the morning. #41 carried
    a PR-level record naming its head. The file is gitignored (`.gitignore:66`), so it is local
    setup, not a repository path.
11. **A second identity is possible, and is still the same person's.** GitHub's terms allow "no
    more than one free machine account in addition to your free Personal Account", one "used
    exclusively for performing automated tasks", and say "the owner of the Account is ultimately
    responsible for the machine's actions". GitHub's "Require approval of the most recent reviewable
    push" needs the latest push "approved by someone other than the person who pushed it".
12. **Precedents this decision sits beside.**
    - The cut (`3d344fd`, 13 October) removed process that outweighed the product.
    - `ci.yml`'s header: "an override nobody can verify -- one author, one reviewer -- is a
      permissions model for a team of one".
    - g1a-and-landing-work ticket 06: protection "guards against your own haste, not against a
      hostile contributor".
    - g1a-and-landing-work ticket 11, closed at the cut: "Nothing else in the body is asserted, and
      that is now the decision rather than a deferral."
    - Overnight ticket 66, user story 6: `land.sh` stays "the one sanctioned merge path, so that the
      roadmap derivation it performs is never skipped because the script could not express what
      was needed".
    - `CODING_STANDARDS.md`: "A mechanical rule belongs in a check instead".
13. **The governance audit already proposes option B.** `.scratch/governance-audit/spec.md` gap 8
    says "PR review are not CI-enforced ... no branch protection declared" and recommends "branch
    protection with required reviews and required status checks" for SOX ITGC. "No branch protection
    declared" is inaccurate against fact 2: protection exists, with checks and without reviews.
14. **A tag is never corrected.** `contracts/README.md` → "What may land between gates" lets prose
    change on the integrator's decision, which is how #39 and #40 corrected `main`. The tag stays
    "the record" (`landing-work.md` → Gates).
15. **`land.sh` has no test harness.** Ticket 69 is open for one. `--body-file` was verified by
    extracting its parse loop, and a review then found a defect in it that no test could have
    caught (`2c9962f`).

### Round 1 — what a review is, whose work needs one, and what the guard is for

The frontier: the five decisions with no prerequisites.

❓ **Q1 - A review covers exactly the head it read**: Today a review attaches to a pull request,
not to a commit, so fix commits pushed after it ride on its verdict. That is how #34 and #35 merged
(facts 3 and 6). Options:
(a) A review covers the pull request, whatever is pushed after it. This is today's practice.
(b) A review covers exactly the head it names. Commits pushed after it need a review of their own,
by an agent session that wrote none of them, which may cover only the commits since the last
reviewed head. A merge of `main` into the branch is such a commit; `review.md` already audits its
resolution separately.
(c) As (b), except that commits answering only the review's own findings may be verified by their
writer.

➡️ (b). (c) is exactly the #34/#35 path, and (a) lets a verdict cover code its reviewer never saw. A
delta review keeps the cost of a fix round proportional to the fix.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q2 - The integrator's carve-out goes**: `landing-work.md` and `review.md` let the integrator's
own work merge "on CI plus the human" because it "has no second workstream to review it". The
premise conflates two units. A workstream's pull request is not reviewed by another workstream
either: the rule's unit is the agent session ("a session other than the one that wrote the code"),
and any fresh agent session can review integrator work. Practice already does: #12, #13, #15, #19,
#29 to #31, #42 and #43 are integrator pull requests whose records name the merged head. All four
pull requests in the incident were integrator work the carve-out exempted (fact 4). The Spec axis
already has a source for integrator work: `review.md`'s table lists the ticket and "A decision the
task was handed", and a grilled ticket's "Questions for Adam", with their answers, is that decision.
Options:
(a) Keep "CI plus the human".
(b) Drop the carve-out: every pull request into `main` gets a review from an agent session that
wrote none of its commits.

➡️ (b), and with it both "weakest link" paragraphs and `review.md`'s "earns the thorough pass" line.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q3 - The guard is aimed at haste, and a self-attested record is accepted**: Every agent session
and the human act through one GitHub account (fact 1), so any record a review leaves (a comment, a
review, a line) could have been written by anyone, the writing session included. Options:
(a) Guard against haste: make the review's output a precondition of the merge, and accept that a
dishonest record is undetectable. This is the floor A-05's integrator declaration reached
(g1a-and-landing-work ticket 08) and the one `ci.yml`'s header states.
(b) Guard against forgery too. That needs a second identity the writing session cannot reach, and
nothing on one workstation provides one.

➡️ (a). Every instance in fact 3 was haste: a merge before the review existed or finished, or after
commits it never saw. This is a sequencing guard, not a permission. Unlike the override `ci.yml`
rejects, it grants nothing; a hasty lander simply does not have the record yet.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q4 - Branch protection requiring approving reviews is rejected while one person holds the only
account**: This is the ticket's option B. On one account it costs one of two things:
- **A second identity.** A machine account (fact 11), added as a collaborator, could approve. Every
  agent session on the workstation, the writing session included, could reach its token, so its
  approval is the same self-attestation as a comment. It also adds an account to secure and a token
  every review session must hold. GitHub's "most recent reviewable push" option, the native form of
  Q1, needs that identity too. Creating an organisation was ruled out of scope as an account-structure
  question once already (g1a-and-landing-work ticket 03, option 4).
- **A bypass.** gh refuses a blocked pull request without `--admin`, and `--admin` drops the two
  required checks as well (fact 8). `land.sh` would then trade CI's enforcement for a review
  requirement it skips.

Turning `enforce_admins` on instead would refuse the direct roadmap-refresh pushes (fact 9), and
would lock the human out whenever the second identity was unavailable.

➡️ Yes, reject it for this repository. Q10 says when it reopens.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q5 - Only generated roadmap refreshes reach `main` without a pull request**: A review before a
merge covers nothing that reaches `main` without one, and the admin account can push straight to it
(fact 2). Today only the import and five generated refreshes ever have (fact 9). Options:
(a) Leave it unsaid.
(b) State it in `landing-work.md`: the refresh `land.sh` pushes after a merge and the refresh made
after a gate tag are the only direct pushes, and every other change reaches `main` through a pull
request with a complete review. This stays prose until it is first broken, when it becomes a hook
check on pushes to `main`.
(c) A pre-push check now.

➡️ (b). It is true today, costs one sentence, and closes the hole beneath the rule. A check ahead
of any violation is the apparatus the cut removed.

Answer: yes, pre-accepted by Adam, 2026-10-15

**Adopted:** Q1 to Q5. **Frontier recomputed.** A review is defined (Q1, Q3), its scope is set (Q2)
and option B is rejected (Q4). That opens: what enforces it, what the record looks like, who may
write it, whether any pull request is exempt, how the audit's gap 8 is answered, and the tag question.

### Round 2 — the mechanism, the record and the tag rule

❓ **Q6 - `tools/land.sh` refuses a pull request whose head no review record clears, and pins the
merge to that head**: This is the ticket's option A, as Q1 and Q3 shape it. Before merging,
`land.sh` reads the pull request's records and refuses unless the latest record naming the current
head carries a merge verdict. It then merges with `--match-head-commit <that head>`, so a push
between the check and the merge is refused by GitHub, and it never passes `--admin`, so the required
checks keep binding (fact 8). Every refusal comes before the merge, as with `--body-file`. Judged by
substance (a record naming the merged head, fact 5), it would have refused all four incident pull
requests and every later one with no such record: #34 to #40, #44 and #45. No past record uses the
new line (Q7), so its format binds from the merge that lands it. Options:
(a) The `land.sh` check.
(b) Prose only. The rule already was prose, and four pull requests passed it in one evening.
(c) A required "Reviewed" status check, set by a workflow on review events. It holds for web-UI
merges too, but it reintroduces the required-check deadlock the cut removed when it took A-05 out
of CI. It needs a workflow with `checks: write`, and the same admin account bypasses it.

➡️ (a). Whether a record naming the head exists is decidable, so by `CODING_STANDARDS.md` it belongs
in a check, and `land.sh` is the one sanctioned merge path. Accepted limit: a merge made outside
`land.sh` (the web UI, or a bare `gh pr merge`) is unchecked, as `landing-work.md` already forbids.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q7 - The review record is one fixed line naming the full head sha**: The line is
`Reviewed <40-hex head> against <40-hex base>: merge`, or `: hold`. It sits on a line of its own in
a PR comment or a submitted `COMMENTED` review, posted by the reviewing agent session after both
axes report, and its format is specified in `docs/agents/review.md` → "Landing or holding". The
latest line naming the current head decides. Abbreviated shas do not match. The check ignores the
base, since `strict: false` deliberately leaves base drift to CI. Options:
(a) The fixed line.
(b) GitHub's own `commit_id` on a `COMMENTED` review, with a verdict word in its body.
(c) Any comment that mentions the head sha.

➡️ (a). It fails closed: a record naming any other head matches nothing. (b) fails open, because
`commit_id` defaults to the newest commit when the reviewer omits it, which is exactly the
push-during-review race (fact 7). (c) would have passed `a80e906`'s "fixed in 833bb79" (fact 6).
The line is close to what #41 and #43 already write.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q8 - Only an agent session writes a review record**: With the carve-out gone (Q2), the human's
reading is no longer the integrator's review. Options:
(a) A record from the human counts too, written as the same line.
(b) Only an agent session that ran `/code-review` and wrote none of the commits it covers writes a
record. The human's reading, however careful, is the decision to land, not a review.

➡️ (b). "CI plus the human" was the weakest link, and haste is the threat (Q3), so a human-written
line would bring it back through the side door. Dispatching a review session costs the human one
command.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q9 - No pull request into `main` is exempt by what it touches or where it came from**: Tracker-only,
docs-only and generated-roadmap pull requests (#26, #36, #45), and an overnight run's integration
pull request, each need a record naming their head. The overnight lanes' per-ticket reviews are
inputs, not substitutes: their reviewer agent writes its own fix commits (`Review: ...`), and the
integration merges, the tracker commit and the roadmap refresh are commits nobody reviewed
(fact 10). The rule binds pull requests into `main`. A lane merged into an integration branch is
covered by the review of the pull request that brings that branch to `main`. Options:
(a) No exemption.
(b) Exempt tracker-only and generated pull requests.
(c) Exempt the overnight pull request when every lane in it was reviewed.

➡️ (a). #36 was tracker-only, and four claims in the ticket it filed were wrong (#38's body). #41
already carried the PR-level record that (c) would drop. One local, optional change follows: the
overnight workflow's morning step "open one PR" gains "and have a fresh agent session review it".

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q10 - Option B stays rejected while one person holds the repository's only account, and the
audit's gap 8 stays right for an adopter**: The governance audit's gap 8 recommends required
reviews for SOX ITGC (fact 13). That is the correct control once there are two people with their own
accounts. So the rejection in Q4 is scoped "while this repository has one human and one GitHub
account". It reopens when a second person with their own account joins, or when a regulated adopter
takes the platform into its own organisation. Gap 8's "no branch protection declared" is corrected
to "protection requires the two CI checks and no review", with a pointer to this decision.

➡️ Yes.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q11 - No gate tag is cut while any review of a change it covers is open**: This answers the
ticket's done-when directly. An **open review** lasts from the moment a pull request's head needs a
review until a review record names that head with a merge verdict and the findings it raised have
landed: fixed, or carried as `Outstanding:` lines. That covers every case the incident and its
aftermath produced: a review still running (#37); a hold; commits pushed after the last reviewed
head (#34, #35); a merge made without a review (#36); and a re-review of a merged pull request still
running (the re-review of #37 that found `pipeline.md`, #40). A gate tag covers every merge since
the previous gate tag. It waits until none of them has an open review, then for its exit test's CI
run on the tagged commit, as now. Options:
(a) No tag while a review is open.
(b) A tag may proceed over an open review if its message says so.

➡️ (a). A tag is permanent (fact 14), and `contracts-v1` still freezes sentences that `main`
corrected 34 and 71 minutes after it was cut.

Answer: yes, pre-accepted by Adam, 2026-10-15

**Adopted:** Q6 to Q11. **Frontier recomputed.** The check is in `land.sh` (Q6) and its record is
fixed (Q7). That opens: what happens when no review can be had, what the merge commit keeps, where
the check's logic lives, and how far back the tag rule reaches.

### Round 3 — the escape, the trail, the code and the history

❓ **Q12 - `land.sh --unreviewed "<reason>"` is the one escape, and it leaves the review open**:
Without a record, `land.sh` refuses (Q6). What remains is a red `main` needing a fix in minutes, or
no session free to review. Options:
(a) No escape. The lander leaves `land.sh` for a bare `gh pr merge`. That skips the roadmap
derivation the script exists for (overnight ticket 66, user story 6) and leaves nothing saying why.
(b) `--unreviewed "<reason>"`, with the reason mandatory. It merges with no record, still pins the
head it saw, and writes `Unreviewed: <reason>` into the merge body. The pull request's review stays
open (Q11) until a record naming the merged head says `merge`, posted on the merged pull request,
with its findings landing as a follow-up.
(c) An escape with no reason.

➡️ (b). It follows the hook's `--no-verify` precedent: the escape is named, and using it shows. It
is not the override `ci.yml` rejects, because it claims nothing was verified; it records that
nothing was, and Q11 turns that into a debt the next gate pays.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q13 - Every merge `land.sh` makes ends with `Reviewed: <head sha> <record URL>`, or the
`Unreviewed:` line, and nothing asserts it**: The evidence then rides in the merge commit, which is
where `landing-work.md` already wants the body to carry what was verified. It survives GitHub, and
it tells a `land.sh` merge from any other, which nothing can do today (fact 9). The script writes
it, not the author, so it costs nobody a habit. No hook or CI step asserts it, which keeps
g1a-and-landing-work ticket 11's decision that the merge body is not checked.

➡️ Yes.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q14 - The record-reading logic is a small Python module `land.sh` calls, with a test companion
in CI**: For example `tools/review_record.py`, called the way `land.sh` already calls
`python3 -I tools/roadmap.py`, so it needs no new tool. It is tested on fixture JSON of PR comments
and reviews, with each refusal red and the accepted case green. The refusals are: no record, a
record of an earlier head, a hold, an abbreviated sha, and a malformed line. The companion runs
beside `tools/roadmap_tests.py`, and `land.sh`'s glue stays untested until ticket 69's harness
exists. Options:
(a) Bash inside `land.sh`, untested, as `--body-file` shipped.
(b) A Python module with tests.
(c) Wait for ticket 69.

➡️ (b). A guard nobody has seen fail is the unfailable-assertion shape `CODING_STANDARDS.md` rules
out, and `--body-file` showed what untested `land.sh` code ships (fact 15). It does not wait on
ticket 69, nor pre-empt its choice.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q15 - The next gate tag's range is reviewed in full: the five merges since `contracts-v1` that
no record names get one before G2 is tagged**: The five are #38, #39, #40, #44 and #45 (fact 5).
#41 to #43 already have records naming their heads, in the older free-form shape, and those count.
Merges before `contracts-v1` are history already covered by tags (the precedent: "33eb5fb stands",
g1a-and-landing-work ticket 11). Options:
(a) Forward-only: the rule binds merges after the one that lands it, and G2's tag covers the rest as
they are.
(b) Review the five before G2.

➡️ (b). (a) would let the first tag the rule governs cover exactly the unreviewed fix-forwards the
incident produced (#38 to #40). Five reviews, each scoped to its own diff, cost an evening of agent
time.

Answer: yes, pre-accepted by Adam, 2026-10-15

**Adopted:** Q12 to Q15. **Frontier recomputed.** One branch is left: how the tag rule is held.

### Round 4 — how the tag rule is held

❓ **Q16 - The tag rule is prose in `landing-work.md` → Gates plus one reading command, with no
hook and no script**: The command lists the merges since the previous gate tag with their
`Reviewed:` and `Unreviewed:` lines (Q13). For each `Unreviewed:` merge, and each merge from before
the trailer (Q15), the tagger confirms that a record on its pull request names the merged head with
`merge`. They also confirm that no review they or anyone else dispatched is still running. Options:
(a) Prose plus the command.
(b) A pre-push check on tag pushes. The trailer half is decidable from git, but an `Unreviewed:`
merge's follow-up record lives on GitHub, so the hook would need the network, or would refuse every
tag over such a merge forever and train `--no-verify`. "A review is still running" is decidable from
nothing.
(c) A `tools/tag-gate.sh` that mirrors `land.sh`. Only four Phase 1 tags remain (G2 to G5), and the
`land.sh` check already closes the path that produced the incident.

➡️ (a).

Answer: yes, pre-accepted by Adam, 2026-10-15

**Adopted:** Q16. **Frontier recomputed.** Only the record of the decision is left.

### Round 5 — recording it

❓ **Q17 - Record the decision as ADR-0020, or the next free number on `origin/main` when it
lands**: The title is "Hold review at the one sanctioned merge path, not with branch protection".
- **Decision:** Q1 to Q3, Q6 to Q9, Q11 and Q12.
- **Alternatives rejected:** option B, with Q4's reasons and Q10's reopening trigger; the required
  status check (Q6 c); prose only (Q6 b).
- **Consequences:** merges outside `land.sh` are unchecked; the trailer is written and nothing
  asserts it; the five retroactive reviews come before G2; and the first pull request under the rule
  is held only by convention, because it lands the check.

➡️ Yes. The three criteria are assessed under "ADR candidates" below.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q18 - Apply it as one integrator pull request, so no document claims a check before it
exists**: That is the defect class overnight ticket 66 was about. The pull request touches:
- `docs/adr/0020-*.md` and its row in `docs/adr/README.md`.
- `docs/agents/landing-work.md`. Under "Review": the rule, the carve-out removed, and the
  direct-push sentence. Under "Land it": the check, the escape and the trailer. Under "Gates": the
  tag rule and the command.
- `docs/agents/review.md` → "Landing or holding": the record line and how to post it, with the
  carve-out paragraph removed.
- `tools/land.sh`: the check, `--match-head-commit`, `--unreviewed`, the trailer and the header.
- `tools/review_record.py` and its test companion, and the CI step.
- `GLOSSARY.md` (Q19), and gap 8 in `.scratch/governance-audit/spec.md` (Q10).
- This ticket: an `## Answer`, and `Status: resolved` once the pull request merges.

A fresh agent session reviews that pull request and posts the first record in the new shape. The old
`land.sh` on `main` lands it, so it is held by convention, once. The five retroactive reviews (Q15)
follow as their own records. The overnight workflow's morning step (Q9) is offered as a local edit,
not committed.

➡️ Yes.

Answer: yes, pre-accepted by Adam, 2026-10-15

---

❓ **Q19 - Add the glossary terms below, and amend Integrator**: The new terms are Review, Review
record, Open review, Agent session and Gate tag. The Integrator entry gains "a role that agent
sessions play, not a person". The wordings are under "Proposed glossary / ADR".

➡️ Yes.

Answer: yes, pre-accepted by Adam, 2026-10-15

**Adopted:** Q17 to Q19. **Frontier empty.** Every branch has been visited, and the scenarios below
test the definitions. Left to their own tickets: whether overnight ticket 66 resolves
(cross-reference-checks ticket 09), ticket 69's harness choice, and whether `strict: false` should
change, which this decision deliberately leaves alone.

### Domain-modelling notes

- **"Session" means two things here.** `GLOSSARY.md`'s Sessions section is the gateway's login
  session: session record, session key, `session_ref`. The review rule's "a session other than the
  one that wrote it" is an agent conversation. In a review of identity work the two collide ("the
  session that refreshed" against "the session that wrote it"), hence the term Agent session.
- **Four names for one thing.** "Review-evidence line" (this ticket), "review disposition" (#33),
  "review remediation" (#34) and "Reviewed `<sha>` against `<sha>`" (#41, #43) are one thing, now the
  Review record. "Approving review", the ticket's option A, names something this repository cannot
  produce (fact 1), so it is avoided.
- **The carve-out conflated a workstream with an agent session** (Q2). It is the same unit error
  g1a-and-landing-work ticket 07 found in `/implement-spec`, whose own review "does not discharge
  the map's separate-session review".
- **"Integrator" has been read two ways.** `landing-work.md` separates "the integrator" from "the
  human". The g1a map's gate checklist, since cut, had "Agent sessions never attest ... the
  integrator attests", where the integrator is the human. The record shows agent sessions writing
  integrator pull requests (#35 to #40 each carry "Generated with Claude Code") on the human's
  decisions (#37: "taken on Adam's acceptance of Q105–Q116"). The amendment keeps the role reading.
  It deliberately says nothing about who decides, so it does not contradict the Change request
  entry's "the integrator decides and edits".
- **Scenarios run against the adopted definitions:**
  1. A review passes at head A, then the author pushes a typo fix B. B is unreviewed until a delta
     review of A..B by another agent session (Q1).
  2. The overnight reviewer agent fixes a finding itself. It wrote that commit, so the morning pull
     request's review covers it (Q9).
  3. `main` is merged into a branch. That is a new head, so its resolution is reviewed (Q1,
     `review.md`).
  4. A record says `hold`. The author answers one finding as not a defect, without a new commit,
     and the reviewer then posts `merge` on the same head. The latest line wins, and it merges (Q7).
  5. Adam reads a pull request and is satisfied. That is his decision to land, and a session
     reviews it (Q8).
  6. `main` is red. `--unreviewed "<reason>"` lands the fix, and the next gate waits for its review
     (Q12, Q11).
  7. A session writes the line without reviewing. Nothing under one account detects it, and that
     is accepted (Q3).
  8. The author pushes while a review is running. The record names the head the reviewer read, so
     it matches nothing and `land.sh` refuses (Q7). With `commit_id` it would have attached to the
     new head.

## Questions for Adam

1. **Should a review cover exactly the head its record names, so that commits pushed after it, fix
   commits included, need their own review by an agent session that wrote none of them? That
   review may cover only the commits since the last reviewed head.**
   Recommendation: yes, Q1 (b).
   Why: #34 and #35 merged on fix commits only their writer verified, and a delta review keeps a
   fix round cheap.
   Answer: yes, pre-accepted by Adam, 2026-10-15
2. **Should the integrator's carve-out ("merges on CI plus the human") be removed, so that every
   pull request into `main`, the integrator's included, gets a review from an agent session that
   wrote none of its commits?**
   Recommendation: yes, Q2 (b), with both "weakest link" paragraphs.
   Why: its premise conflates a workstream with an agent session, and all four pull requests in the
   incident were integrator work it exempted.
   Answer: yes, pre-accepted by Adam, 2026-10-15
3. **Should the guard aim at haste and accept a self-attested record, since every agent session and
   the human act through one GitHub account?**
   Recommendation: yes, Q3 (a): a sequencing guard, not a permission.
   Why: every instance was haste, and a forged record is undetectable under one account, the floor
   A-05 already reached.
   Answer: yes, pre-accepted by Adam, 2026-10-15
4. **Should branch protection requiring approving reviews (the ticket's option B) be rejected for
   this repository?**
   Recommendation: yes.
   Why: satisfying it needs a second identity the writing session could also reach, or `--admin`,
   which drops the two required CI checks too.
   Answer: yes, pre-accepted by Adam, 2026-10-15
5. **Should `landing-work.md` state that only generated roadmap refreshes reach `main` without a pull
   request, as prose until the rule is first broken?**
   Recommendation: yes, Q5 (b).
   Why: it is true today (six direct pushes ever: the import and five refreshes), and it closes the
   hole beneath the rule.
   Answer: yes, pre-accepted by Adam, 2026-10-15
6. **Should `tools/land.sh` refuse to merge unless the latest review record naming the pull
   request's current head says `merge`, then merge with `--match-head-commit` pinned to that head,
   and never with `--admin`?**
   Recommendation: yes, Q6 (a).
   Why: it would have refused all four incident pull requests, and a decidable rule belongs in a
   check at the one sanctioned merge path.
   Answer: yes, pre-accepted by Adam, 2026-10-15
7. **Should the review record be the fixed line `Reviewed <40-hex head> against <40-hex base>: merge`
   (or `: hold`) in a PR comment or `COMMENTED` review, specified in `docs/agents/review.md`, full
   shas only?**
   Recommendation: yes, Q7 (a).
   Why: it fails closed. GitHub's `commit_id` defaults to the newest commit and fails open, and
   "any mention of the head" would have passed `a80e906`.
   Answer: yes, pre-accepted by Adam, 2026-10-15
8. **Should only an agent session that ran `/code-review` and wrote none of the commits write a
   review record, the human's reading being the decision to land rather than a review?**
   Recommendation: yes, Q8 (b).
   Why: "CI plus the human" was the weakest link, and a review session costs the human one command.
   Answer: yes, pre-accepted by Adam, 2026-10-15
9. **Should no pull request into `main` be exempt by content or origin, so that tracker-only,
   docs-only, generated and overnight integration pull requests all need a record naming their
   head?**
   Recommendation: yes, Q9 (a), plus an optional local edit to the overnight workflow's morning step.
   Why: #36 was tracker-only and its ticket carried four wrong claims, and the overnight pull request
   carries commits no lane review saw.
   Answer: yes, pre-accepted by Adam, 2026-10-15
10. **Should the rejection of option B be scoped "while this repository has one human and one GitHub
    account", reopening when a second person with their own account joins or a regulated adopter
    takes the platform into an organisation? Should the governance audit's gap 8 be corrected to
    say protection requires the two checks and no review?**
    Recommendation: yes.
    Why: gap 8's required reviews are right once there are two people, and its "no branch
    protection declared" is inaccurate.
    Answer: yes, pre-accepted by Adam, 2026-10-15
11. **Should no gate tag be cut while any review of a change it covers is open? Open would mean a
    review running or holding, commits behind the last reviewed head, a merge with no review, or a
    re-review in flight, with "open review" defined as in Q11.**
    Recommendation: yes, Q11 (a).
    Why: a tag is permanent, and `contracts-v1` still freezes sentences that `main` corrected 34 and
    71 minutes after it.
    Answer: yes, pre-accepted by Adam, 2026-10-15
12. **Should `land.sh` take `--unreviewed "<reason>"` as its one escape, writing
    `Unreviewed: <reason>` into the merge body and leaving the review open until a record names the
    merged head?**
    Recommendation: yes, Q12 (b).
    Why: it keeps `land.sh` the one merge path (overnight ticket 66, user story 6) and turns haste
    into a debt the next gate pays.
    Answer: yes, pre-accepted by Adam, 2026-10-15
13. **Should every merge `land.sh` makes end with a `Reviewed: <head sha> <record URL>` line, or the
    `Unreviewed:` line, with nothing asserting it?**
    Recommendation: yes.
    Why: the evidence rides in git and tells `land.sh` merges from the rest, while
    g1a-and-landing-work ticket 11's decision that the body is not checked stands.
    Answer: yes, pre-accepted by Adam, 2026-10-15
14. **Should the record-reading logic be a Python module `land.sh` calls, with a fixture-based test
    companion run in CI, rather than untested bash?**
    Recommendation: yes, Q14 (b).
    Why: a guard nobody has seen fail is an unfailable assertion, and this need not wait on ticket
    69.
    Answer: yes, pre-accepted by Adam, 2026-10-15
15. **Should the five merges since `contracts-v1` that no record names (#38, #39, #40, #44, #45) get
    a retroactive review before G2 is tagged?**
    Recommendation: yes, Q15 (b).
    Why: otherwise the first tag the rule governs covers exactly the incident's unreviewed
    fix-forwards.
    Answer: yes, pre-accepted by Adam, 2026-10-15
16. **Should the tag rule be held by prose in `landing-work.md` → Gates plus one `git log` reading
    command, with no hook and no tag script?**
    Recommendation: yes, Q16 (a).
    Why: an `Unreviewed:` merge's follow-up record lives on GitHub, "a review is running" is
    decidable from nothing, and only four Phase 1 tags remain.
    Answer: yes, pre-accepted by Adam, 2026-10-15
17. **Should the decision be recorded as ADR-0020 (or the next free number on `origin/main`), with
    option B, a required status check and prose only as its rejected alternatives?**
    Recommendation: yes.
    Why: the rejection is non-obvious, and the governance audit has already proposed it.
    Answer: yes, pre-accepted by Adam, 2026-10-15
18. **Should it land as one integrator pull request (the ADR, both agent guides, `land.sh`, the
    module and its tests, CI, the glossary, gap 8, and this ticket's Answer), reviewed by a fresh
    agent session and held by convention this once?**
    Recommendation: yes.
    Why: no document may claim a check before the check exists, which is the defect class overnight
    ticket 66 was about.
    Answer: yes, pre-accepted by Adam, 2026-10-15
19. **Should `GLOSSARY.md` gain Review, Review record, Open review, Agent session and Gate tag, and
    the Integrator entry gain "a role that agent sessions play, not a person"?**
    Recommendation: yes, worded as below.
    Why: the ticket's vocabulary was fuzzy in four places, and "session" collides with the gateway's
    sessions.
    Answer: yes, pre-accepted by Adam, 2026-10-15

## Proposed glossary / ADR

### Proposed `GLOSSARY.md` terms

All are for "Work, gates and evidence". None exists today. The wordings are free of implementation
detail.

- **Review**: a two-axis `/code-review` (Standards and Spec) of one pull request head, by an agent
  session that wrote none of the commits it covers. It is not a GitHub approval, which one account
  cannot give its own pull request.
  _Avoid_: approval, sign-off
- **Review record**: the line a review leaves on its pull request, naming the exact head it read and
  its verdict, merge or hold. It covers that head and nothing pushed after it.
  _Avoid_: review-evidence line, review disposition, approval
- **Open review**: the state of a pull request whose head no review record yet clears. It may have
  a review running or holding, commits pushed after the last reviewed head, or a merge made without
  a review. It closes when a record names that head with a merge verdict and the findings it raised
  have landed.
- **Agent session**: one agent conversation working in the repository, and the unit in which review
  independence is counted. One workstream, or the integrator, may be many agent sessions.
  _Avoid_: bare "session" wherever the gateway's sessions (session record, session key) could be
  meant
- **Gate tag**: the annotated tag that closes a gate, its message naming the CI run that proved the
  exit test. It is permanent, so what its commit says stays said there. Other tags are not gates.
  _Avoid_: release tag, and "the tag" where another tag could be meant
- **Integrator** (amend the existing entry): append "A role that agent sessions play, not a
  person."

### ADR candidates

- **ADR-0020 (or the next free number on `origin/main`): hold review at the one sanctioned merge
  path, not with branch protection.**
  - **Decision:** `tools/land.sh` refuses a pull request whose current head no review record clears,
    and pins the merge to that head. The integrator's work is reviewed like any other. No gate tag
    is cut while a review of a change it covers is open. `--unreviewed` is the one recorded escape.
  - **Rejected:** required approving reviews, a required "Reviewed" status check, and prose only.
  - **Reopens:** when a second person with their own account joins, or a regulated adopter takes the
    platform into an organisation (governance audit gap 8).

  All three criteria hold:
  - **Hard to reverse**, the weakest of the three but met. Moving to the rejected alternative needs
    a second GitHub identity, an account-structure change, and the records and trailers this creates
    accumulate in history that cannot be edited.
  - **Surprising without context.** A public repository with free branch protection deliberately
    requires no review, and the governance audit has already proposed the reverse.
  - **A real trade-off.** Three options differing in kind, two rejected for stated reasons.
- **No second ADR.** Q5, Q13, Q14, Q15 and Q16 are procedure, cheap to reverse and unsurprising once
  ADR-0020 exists, so they belong in `landing-work.md` and `review.md`. The glossary amendments are
  vocabulary, not decisions.
