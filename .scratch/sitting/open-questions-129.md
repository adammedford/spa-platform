# The 25 questions on #129 that the `Confirm` of 2026-10-19 left open

Read from #129's comments on 2026-10-19. The one-word `Confirm` (17:39Z) covers every answer that
was pre-accepted and none that read `Answer: open`; these are the open ones, each with the
recommendation its source gave. Q24 (*Sample image*, *Sitting* in #56's wording, *Read-back*, the
*Landed* amendment) and Q32 (*Platform tag*, *Teardown*) were pre-accepted and are covered; a
session commits them on its own branch with an `Authority:` trailer naming the `Confirm` comment
(https://github.com/adammedford/spa-platform/issues/129#issuecomment-6273385627).

A second comment on #129 that names a question accepts it; an override is a `Q<n>: <answer>` line.
Nobody but the human writes it (ADR-0021 decision 2).

## Recommended yes (18)

| Q | From | What it changes | Recommendation |
| --- | --- | --- | --- |
| Q10 | #125's grill | `GLOSSARY.md` gains *Sitting* in #125's wording: one day's hands-on work, named by its date, one sitting may span several script runs | Yes, by #125's grill. Q24, now confirmed, adds #56's wording of the same term in another section; accepting Q10 means choosing it over Q24's with a `Q24:` override, else leave Q10 open |
| Q11 | #125's grill | `GLOSSARY.md` gains *Applied*: a root is applied when a plan against the project's state reports no changes; an apply's exit code or a state object alone does not mean applied | Yes, by #125's grill |
| Q42 | #128's review | `contracts/README.md` lines 136 to 141: the "Task 2.1b remains pending" paragraph replaced with the text in the question (the validator's bound is the schema's `maximum`; the proxy's test reads it) | Yes, by #128's review; `29f563b`'s `Outstanding:` line left it to the integrator |
| Q43 | #124's Answer and review | ADR-0009: an amendment on the Status line and a dated Consequences bullet (each key under a release is written once, not the prefix) | Yes, by the lane and the review |
| Q44 | #124's review | `contracts/s3-layout.md`, the bucket-name paragraph: an override changes only what precedes the `-<account id>-<region>-an` suffix | Yes, by the lane and the review |
| Q45 | #124's Answer | ADR-0014 Context: "makes a release prefix write-once" becomes "makes each key under a release write-once" | Yes, by the lane and the review |
| Q46 | #124's review (#92's term) | `GLOSSARY.md` gains *Platform bucket* after *Release key* | Yes, by the review; never put to you before |
| Q47 | #124's review | ADR-0014's Status line replaced, dating change request 1.5a's decision 2026-10-16 | Yes, by the review |
| Q48 | #124's review | ADR-0014 item 6 gains a sentence: bootstrap is the one consumer that reconstructs the bucket name | Yes, by the review |
| Q50 | #102's Answer | `AGENTS.md`, "Rules for every task": a bullet on the `Authority:` trailer | Yes, by the lane |
| Q51 | #102's Answer | `docs/agents/review.md`, "The two axes' sources": the reviewer's half of ADR-0023 decision 3 | Yes, by the lane |
| Q53 | #101's Answer | `docs/agents/landing-work.md`, "Land it": `gh pr view <pr> --json closingIssuesReferences …` before landing | Yes, by the lane, checked with gh 2.96.0 |
| Q54 | #101's Answer | Same paragraph: a keyword in any branch commit closes its issue at `main`, so a task commit names its ticket on a `Ticket: #n` line | Yes, by the lane; its anchor sentence is Q56's passage, so a yes needs Q56 or another place |
| Q55 | #101's Answer | `docs/agents/issue-tracker.md`, "An issue is held open by its own scope": closed by the `Closes #n` line at the merge, never by hand before its work is on `main` | Yes, by the lane |
| Q57 | #67's Answer (S3) | `docs/agents/landing-work.md`: `#67 records that choice` becomes `ADR-0022 records that choice` | Yes, by #111's review and the lane |
| Q58 | #67's Answer (S3) | `docs/agents/review.md` line 140: `#67 decided this.` becomes `ADR-0022 records the decision.` | Yes, by #111's review and the lane |
| Q59 | #67's Answer (S2) | ADR-0022 decision 6 and Consequences: "asserted by nothing" becomes "no check reads them back from a merged commit" | Yes, by #111's review; rewording an accepted ADR is your call |
| Q61 | #59's Answer | `GLOSSARY.md` gains *Plan policy* beside *Test ids* | Yes, by the lane |

## A recommendation between options (2)

| Q | From | What it changes | Recommendation |
| --- | --- | --- | --- |
| Q25 | #61's lane | The #61 to #63 edge: (A) close #61 with an Answer once the deployed binding is added to #63's body; (B) flip the edge so #61 is blocked by #63; (C) leave both, and #63 can never become ready | A: it follows "an issue is held open by its own scope" and leaves one issue holding the deployed work |
| Q49 | #122's Answer, reworded by its review | `contracts/infra-outputs.schema.json`'s description: which scripts read Terraform state instead of `outputs.json` | The review's wording, because change request 1.5a's decision 2 names only `teardown.sh`; the script's read rests on #122 |

## Optional (3)

| Q | From | What it changes | Recommendation |
| --- | --- | --- | --- |
| Q60 | #67's Answer (S5) | ADR-0022 lines 7 to 9: "committed by the human" becomes "committed on Adam's confirmation on #98" | Optional, per #111's review |
| Q62 | #126's Answer | `docs/agents/issue-tracker.md`, "Create an issue": pass a multi-line body as `--body-file - <<'EOF'` and a commit message as `git commit -F - <<'EOF'`, because bash 3.2 cannot parse the heredoc-in-substitution form on four kinds of text | Optional, by the lane; the review verified all four rejections under bash 3.2.57 |
| Q63 | #123's review | ADR-0007's Status line: an amendment recording that the load balancer writes no access logs in Phase 1, so the code appears in URLs only | Optional, by the review |

## No recommendation (3)

| Q | From | What it changes | Why none |
| --- | --- | --- | --- |
| Q41 | #75's status note | How workstream 6's open clause reaches a ws6 lane: (a) the Owner line names workstream 6 alone, or (b) the clause moves to its own ticket and #75 closes. Either stops the nightly dispatch to ws2; closing #75 as it stands drops the clause | The note offers both routes and picks neither |
| Q52 | #102's review, held | `docs/agents/landing-work.md`, "Build it": restore the sentence that a term or guide sentence no ticket asks for is proposed under `## Proposed steering changes` | Held for want of authority, not on its merits; it restates ADR-0023 decision 4 in the guide every session reads |
| Q56 | #101's review, held | `docs/agents/landing-work.md`, "Land it": the passage on closing keywords in merge bodies, with the review's one correction applied | Held for want of authority; every other fact in it found correct. Q54 hangs on it |

## What a second comment could look like

One line per decision, nothing else needed. For example: `Confirm Q11, Q25 (A), Q41 (a), Q42,
Q43, Q44, Q45, Q46, Q47, Q48, Q49 (the review's wording), Q50, Q51, Q53, Q54, Q55, Q56, Q57, Q58,
Q59, Q61` accepts every recommendation and both held passages, picks (a) for #75, and leaves Q10,
Q52, Q60, Q62 and Q63 open. The planning session's view, for what it is worth: Q52 and Q56 are
worth a yes, since each puts a rule sessions already live under where they read; Q10 is a no, since
Q24's wording is confirmed and two entries for one term would contradict the glossary's own rule.
