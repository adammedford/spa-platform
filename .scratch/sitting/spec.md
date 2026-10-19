**Owner:** integrator; the scripts it runs are workstream 1's
**Plan row:** none
**Raised by:** the Phase 2 planning session of 2026-10-19; #125's Q8 (the sitting's record per
ticket, from captured results), #56's Q9 and Q10 (one read pass, run by a foreground session with
the human's approval of each command) and #55's Q11 and Q14 (the sitting's order, and the destroy).
Those answers were confirmed on #129 on 2026-10-19 at 17:39Z, by a comment that is the one word
`Confirm`: under the report's own rule it covers every pre-accepted answer and none of the
questions that read `Answer: open`. This spec follows the confirmed answers and changes nothing on
its own.

## Problem Statement

Every gate from G3 to G5, and the deployed half of every Phase 2 package, waits on work that only
a human's session in the platform project can do: applies, read-backs, a browser login, a script
that deletes through a break-glass role, a project closed in AWS Settings, a teardown. The
2026-10-19 run (#129) found the C# lanes idle for want of it. The sitting of 2026-10-18 showed what
that work costs when it is run by hand from a script in `/private/tmp`: the apps root was applied
twice because a `grep -q` at the end of a pipe skipped the certificate import; the one check that
proves the login left no record; #55's and #56's comments were a fixed sentence the script posted
without reading its own result, so the next grill had to rebuild the night from S3 lock-file
versions and file times; and the script itself was gone after a reboot (#125's facts 1 and 2,
#56's fact 2). The confirmed and pre-accepted answers since then fix the standard: evidence per
ticket from captured output, a failed check that posts nothing reading as success, no secret or
account id in a post, and the script kept in the repository's ignored log directory.

What remains is bounded and can be listed. The table below is the inventory as of 2026-10-19,
read from the open tickets, the grills on #55, #56 and #125, and the plan's First deployment order.
"Hands" says what only the human can do; everything else a session can run with the human's
approval of each command, which is what `/wizard` is for.

### The inventory

Minutes are the planning session's estimates of the human's attention, not wall-clock.

| # | Ticket | What | Hands | Session, with approval | Human minutes |
| --- | --- | --- | --- | --- | --- |
| A0 | all | Preflight ready, `tools/aws-project.env` sourced, the account pinned | — | `tools/preflight.sh`; refuses on a mismatch | 2 |
| A1 | #56 | Whether the cell still stands (#56's fact 12: not on record) | — | `aws ecs describe-services`; branches the sitting if it is gone | 1 |
| A2 | #125 | The manual code-flow login: app 1's client, managed login, a test user, `?code=` in the address bar | The browser; say whether the code appeared | Prints the URL from the apps README's "Done when" block; posts the outcome, never the code | 3 |
| A3 | #55 | Q12's evidence: the platform root's empty plan, and the load balancer, listener and target group read back | — | `terraform plan -detailed-exitcode` with no plan file; three `elbv2` reads; the post | 4 |
| A4 | #56 | Q6 to Q8's read-back: steady service, running image equal to the pin, healthy target, task definition, service flags, the Exec agent, log retention, the task role's one inline policy, the platform tag on six resources | — | About fifteen reads; the post with names only | 8 |
| A5 | #118 | Step 1: the platform tag query in the selected Region and us-east-1, and `iam list-roles` filtered to the cell's roles, showing the query omits the roles | — | Two reads; the post | 2 |
| A6 | #122, #92 | P-04 against the applied bucket: `tools/scripts/verify-release-immutability.sh`, one line per case | Decide `--clean-up-as-teardown-role`: yes, so the bucket is empty for Q14's destroy | The run; the post on both tickets; the output kept for `Merge task 1.5`'s body | 5 |
| A7 | #129 Q16 | Delete `infra/envs/poc/platform/terraform.tfvars.bak` | — | `rm`, with approval | 1 |
| A8 | #125, #55, #56, #57, #58 | The tracker writes the grills decided: close #125 and #55 with an `## Answer`, relabel #56 `ready-for-agent`, remove #56 as a blocker of #57 and #58, remove #125 as a blocker of #55 | — (the `Confirm` of 2026-10-19 covers each) | Shows each write, then makes it | 5 |
| B0 | #57 | A clean workstation for the tunnel: the Session Manager plugin, the hosts entries, the local authority trusted | Admin prompts for the hosts file and the keychain, if not done | `tools/preflight.sh`; `tools/scripts/certs.sh status` | 5 |
| B1 | #57 | `tunnel.sh`, then `verify-tunnel.sh /` on the sample image: exits 0 | Watch | The run; the post | 5 |
| B2 | #55 Q14 | `terraform destroy` of the platform root, keeping the certificate, the identity root and the apps root; then the tag query and `list-roles` again (#118's second data point) | Confirm the destroy plan; the spend call it rests on | The plan, the destroy on confirm, the reads, the post | 10 |
| C0 | #58 | Docker Desktop installed and running (D-2) | Install once; accept its terms | `brew bundle`; `docker version` | 10 |
| C1 | #58, #63 | First deployment from nothing: the platform root on the sample image, `seed.sh`, `deploy.sh` (both images built for ARM64 and pushed by digest; the apply with `/readyz`; steady; the digest asserted), `tunnel.sh`, `verify-tunnel.sh /healthz` | Confirm each apply; wait | Every step is a script or a plan; the posts on #58 | 20, of about 45 wall-clock |
| C2 | #63 | G3: F-01 to F-06 and F-08 deployed (the browser suite through the tunnel), P-04 (the script again), P-06 (a connection from outside to the task's public address on 8080 times out), P-05 (`teardown.sh`, then the tag query empty and the roles gone by name), then deploy again from nothing | Confirm the teardown and the redeploy; wait | The suite, the script, the reads, the posts | 30, of about 90 wall-clock |
| C3 | #63 | G4: the deployed negative subset, N-26 included. G5: F-07, the third SPA: the apps root's plan shows three resources, `spactl` publishes and adds app 3, the task id and revision are unchanged | Confirm the apps apply; wait | The suites and the reads; the posts | 15, of about 40 wall-clock |
| C4 | G3, G4, G5 | The gate tags, each after its CI run and after the review debt in `docs/agents/landing-work.md` → "Review debt from before the check" is cleared (five merges, each needing a record from a fresh review session) | Cut each annotated tag | Checks the range's `Reviewed:` lines; drafts the tag message | 10 per gate |
| C5 | — | After G5: keep the stack for Phase 2's deployed tier at about $30 a month, or destroy it until then | The spend decision | The destroy, on confirm | 2 |
| H1 | #129 | The 25 questions the one-word `Confirm` left open: Q10, Q11, Q25, Q41 and the steering proposals Q42 to Q63, among them Q42's `contracts/README.md` paragraph, Q25's #61 to #63 edge and Q41's routing of #75. (*Sitting*, *Read-back*, *Sample image*, *Platform tag* and *Teardown* are Q24's and Q32's, pre-accepted and so covered.) | A second comment naming the ones accepted, overrides as `Q<n>: <answer>` lines; or leave them open on purpose. Nobody but you writes it | The digest is written: `.scratch/sitting/open-questions-129.md`, each with its recommendation | 15 to 30 |
| H2 | #90 | Close the sample project in AWS Settings after three pre-checks (the account ending 4665, no CloudTrail trail, not paused); then delete the two local files; preflight still ready; the ADR-0017 clause drafted | The console pre-checks and the one irreversible click | The two `rm`s with approval, preflight, the draft | 10 |
| H3 | #68 | `tools/check.sh --full --both-halves --port 5180` once from `zsh -l` and once from `/bin/bash`, on `main` | Start it | The two runs; the post | 2, of about 30 wall-clock |
| H4 | #71 | The one-off `tools/prune_branches.py` pass from a checkout at `origin/main` | — | The run; the post | 1 |
| H5 | the Phase 2 drafts | File the two spec issues, land the ADR draft with its `Authority:` trailer, decide the nine change requests, file the tickets | Decide | Prepares every command and body from `.scratch/phase-2/` | 120 to 180 |
| P | Phase 2 | After G5: the table, the KMS key and the task role's statements (P2-1c, P2-2c); two tasks with private egress (P2-4: $35 to $110 a month more); a PingFederate instance (P2-6f), which nobody has | The spend and procurement decisions; the applies on confirm | The plans and applies; the deployed suites | not estimated |

Three sittings, then: **A**, the read pass on the cell as it stands, about an hour of attention and
no apply; **B**, the tunnel on the sample image and the destroy, once #57's scripts have landed,
about twenty minutes; **C**, the first deployment and the three gate runs, once #58's scripts and
Docker are in place, about an hour and a half of attention over three or four hours of wall-clock.
The `Confirm` of 2026-10-19 covers every pre-accepted answer, so A8's writes may be made in sitting
A, and the glossary terms of Q24 and Q32 may be committed by a session with an `Authority:` trailer
naming that comment; it names none of the open questions, so the proposals among them stay
proposals until a comment names them (H1).

Owed by the morning run's apply now that the `Confirm` exists, as the tracker read on 2026-10-19
and to be checked before sitting A rather than taken from here: #125's body rewrite and retitle
(its Q5) and its removal as #55's blocker (Q6); #56's body rewrite on its Q1 to Q13; the dated note
on #58 that `deploy.sh` carries the mock API container (#56's Q4); and the ws1 follow-up for the
replaced-client trap (#125's Q9). None needs the cell or a sitting.

## Solution

`/wizard` is a Claude Code skill the human runs in a foreground session on the workstation. It
reads the state (preflight, the tracker, the cell), says which sitting applies and lists its
stages, then runs each stage's commands with the human's approval of each one, posts each stage's
evidence to the ticket it serves from the captured output, and stops where only the human can act.
It replaces `aws-sitting-2.sh` and keeps what that script got right: preflight first, the account
compared with the pin, a plan read before any apply, and an apply only on a confirm and only when
the plan destroys and replaces nothing.

What it never does: write a `Confirm` comment; relabel, unblock or close a ticket before the
confirmation that decided it exists on the run's `Overnight <date>` issue; run an apply, a destroy
or a deletion without a confirm on the plan it just showed; print or post a secret, a client id, an
authorization code, an ARN or an account id beyond its last four digits; or leave its own record
anywhere but `.scratch/sitting/log/`, which `.gitignore` already ignores.

## User Stories

1. As Adam at the start of a sitting, I want one command that tells me which sitting this is and
   what it will ask of me, so that I know whether I have the time before anything runs.
2. As Adam, I want every AWS call shown before it runs and run only on my approval, so that the
   sitting is mine even when a session types.
3. As the next grill, I want each ticket to carry the command, its exit code and its key lines, so
   that nothing has to be rebuilt from lock-file versions.
4. As a reviewer of a public repository, I want no account id, ARN, client id, secret or code in any
   post, so that evidence can be pasted without a second redaction pass.
5. As Adam, I want the browser login and the sample project's close left to me, with the exact URL
   and the exact pre-checks in front of me, so that the two irreversible or human-only steps are
   the only ones I do by hand.
6. As Adam, I want the tracker writes the grills decided prepared but held until I have confirmed
   on #129, so that ADR-0021 holds without my re-reading each grill.
7. As Adam, I want the wizard to stop and name the cause when preflight is not ready, the account
   is not the pin, a plan would destroy or replace, or AWS answers Access Denied where it did not
   before (a spend limit, per `docs/agents/aws-rules.md`), so that a wrong project or a paused
   project is found before an apply.
8. As the overnight run, I want #56 removed as a blocker of #57 and #58 the moment the read-back
   holds and is confirmed, so that the next night's lanes get the scripts.

## Implementation Decisions

- **A skill, not a script, for the orchestration.** The stages are mostly reads and posts with a
  human in the loop, which is what a foreground session does; the parts that must be scripts
  (`certs.sh`, `verify-release-immutability.sh`, `tunnel.sh`, `verify-tunnel.sh`, `seed.sh`,
  `deploy.sh`, `rollback.sh`, `teardown.sh`) are workstream 1's and the wizard calls them. Decision
  D-3's shape, a tested script, is kept for anything that writes to AWS.
- **Where the skill lives.** `.claude/skills/wizard/SKILL.md`, which `.gitignore` ignores today
  (`.claude/*`, with only `settings.json` kept). The draft is `.scratch/sitting/wizard-SKILL.md`
  beside this spec. Tracking it is #135's question (track `.claude/workflows/`); if that is decided
  yes, a `!.claude/skills/` line beside the existing `!.claude/settings.json` tracks this one too,
  and the skill gains a test companion in D-3's shape for its redaction and its stop conditions.
- **The record.** One log per sitting at `.scratch/sitting/log/<date>.log`, every command and its
  output, redacted before any of it is posted; the post is the stage's command, exit code and key
  lines, under a heading that names the stage. A skipped or failed stage posts "not run" or
  "failed", never a sentence that reads as success.
- **Redaction** is one function applied to every post: a twelve-digit number becomes
  `<account>` except the last four digits where a ticket asks for them; an ARN becomes its resource
  name; a Cognito client id, a secret parameter value, a session name and anything after `code=`
  are removed outright. Output the wizard cannot redact with confidence is not posted.
- **The tracker writes** (A8) are gated on a `Confirm` comment on the run's `Overnight <date>`
  issue that covers the question: a comment that is only `Confirm` covers every pre-accepted
  answer and no question that read `Answer: open`, which is the rule #129's body states. The wizard
  reads the comments, and where none covers a write it prints the write it would make and moves
  on.
- **The sitting is chosen by state, not by argument:** A when the cell stands and #56's read-back
  is not posted; B when #57's scripts are on `main` and the read-back is; C when #58's are and the
  platform root is destroyed or absent. An argument may force one.
- **Spend.** Before any apply, the wizard prints the plan's resource count against P-01's allowlist
  and the plan's monthly figure from `docs/plan/05-infrastructure-and-cost.md` (about $30 left
  running; about $1 a day), and it ends every sitting by asking whether the stack stays up, as
  `docs/agents/aws-rules.md` requires.

## Testing Decisions

- The skill is prose, so its proof is a dry run: `/wizard --dry-run` lists every command it would
  run for the sitting it chose and posts nothing. The first real sitting is watched end to end.
- The redaction function and the stop conditions are the two parts that can fail quietly, so they
  become a tested script the moment the skill is tracked: a companion in `tools/`, in D-3's shape,
  with cases for a twelve-digit id inside an ARN, a `code=` in a URL, a client id in JSON, and an
  Access Denied body.
- Every post the wizard makes is checked by the next grill against the ticket's acceptance list,
  which is the check that exists today.

## Out of Scope

- Writing `tunnel.sh`, `verify-tunnel.sh`, `seed.sh`, `deploy.sh`, `rollback.sh` or `teardown.sh`:
  #57 and #58, workstream 1's.
- Any `Confirm` comment, any gate tag and any apply the human has not confirmed.
- The sample project's close: one click in AWS Settings, after the pre-checks #90 lists.
- Phase 2's deployed tier beyond listing it in the inventory.

## Further Notes

**The ticket graph.**

| Ticket | Owner | Does | Blocked by |
| --- | --- | --- | --- |
| W-1 Install the draft skill and run sitting A | the human, with a foreground session | `.scratch/sitting/wizard-SKILL.md` copied to `.claude/skills/wizard/SKILL.md`; A0 to A7 run; A8 prepared, made once #129 is confirmed | nothing; the cell must still stand, else A becomes a re-apply of the platform root on the sample image |
| W-2 Sitting B | the human, with a session | B0 to B2 | #57 landed; A done |
| W-3 Sitting C | the human, with a session | C0 to C5 | #58 landed; D-2's install; B done |
| W-4 Track the skill and give it a companion | integrator | A `!.claude/skills/` line in `.gitignore`; `tools/wizard_redact.py` and its companion in D-3's shape; the skill's stop conditions tested | #135 decided yes |

**What the draft skill rests on that is unverified here:** the exact AWS CLI output shapes of the
reads in A3 to A5 (written from the AWS CLI reference and #56's grill, not from a run), the
`verify-tunnel.sh` and `deploy.sh` interfaces (unwritten; #57 and #58 decide them), and which of
the morning run's applies have been made since the `Confirm`. The sandbox this was written in has
no AWS session and no Mac.
