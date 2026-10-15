# Questions for Adam — overnight run of 2026-10-14

145 questions, numbered Q1..Q145 continuously, grouped by ticket. Each has a recommendation and an
empty `Answer:` line. Most-unblocking first.

Read Q1..Q13 before anything else: ticket 55's grilling found that `origin/main` is 37 commits ahead
of tonight's base and already carries five of the tasks this run re-landed. That decision changes
what you do with the integration branch, so it comes first. See REPORT.md -> "Flag".

---

## Ticket 55 — task 4.3b — The security and proxy stages read the shared rule ids
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/55-4.3b-security-and-proxy-adopt-the-shared-rule-ids.md`
(Status: ready-for-human. One decision here unblocks tickets 54, 55 and 56 and settles what happens
to `overnight/2026-10-14`.)

**Q1.** Re-dispatch ticket 55 from `origin/main` rather than merging anything into the overnight
line? ADR-0015's three pieces are already on `origin/main` (PR #24, `c97c82a`), where the ticket's
edit table applies at identical line numbers; the local clone is 37 commits behind, which is the
whole of the reported blocker.
Recommended: yes — fetch, re-fork the ws4 lane from `origin/main`, run 55 there.
Answer: yes

**Q2.** Abandon `overnight/2026-10-14`, tagged for the record, instead of reconciling it? All 13 of
its commits re-land tasks already merged upstream, and it conflicts with trunk in 25 files.
Recommended: yes — tag `archive/overnight-2026-10-14`, re-fork every open lane from `origin/main`.
Answer: yes

**Q3.** Close change request 4.3b as already satisfied rather than as decided, and say so on tickets
54 and 56? Nothing is open to author or judge; the branch point was stale, not the decision.
Recommended: yes — one closing line citing PR #24, and the same note on 54 and 56.
Answer: yes

**Q4.** Set `MethodAllowlist.cs:18`'s const from `RuleIds.GateMethod` with nothing else in the file
moving? It is the only reading on which the ticket's first and fourth acceptance boxes both hold, and
it adds no telemetry write, so ticket 53's gap is untouched.
Recommended: yes.
Answer: yes

**Q5.** Add the `gate.method` -> `MethodAllowlist.cs:18` row to ticket 55's table, and the grep
acceptance box ticket 54 already has? The table omits the one line the first acceptance box forbids,
which is how that tenth literal survived task 4.3a.
Recommended: yes — add the row and a grep box over `src/Platform.Security src/Platform.Proxy`.
Answer: yes

**Q6.** Reword the ticket's table from "`Platform.Security`'s own constants" to "where the string is
spelled today"? Only three of the ten are declared constants; the seven in `RequestGate.cs` are
inline arguments.
Recommended: yes — and make the acceptance box read "declares or spells a rule id literal of its own".
Answer: yes

**Q7.** Have the test projects keep their rule-id literals, and say so in the ticket? Asserting
against `RuleIds` would make the test tautological.
Recommended: yes — tests keep literals; one line in the ticket so a reviewer does not file them as
further copies.
Answer: yes

**Q8.** Leave `src/Platform.Hosting/RuleIds.cs` entirely to ticket 54, and correct the review's note
about it? That file is already on both lines from task 2.3a, so no merge "brings it along".
Recommended: yes — 54 deletes it; 54 and 55 touch disjoint projects and may land in either order.
Answer: yes

**Q9.** Resolve the `docs/adr/README.md` index collision as 0014 then 0015, keeping both rows? The
uncommitted ADR-0014 row and ADR-0015's row insert at the same line.
Recommended: yes — numeric order, one-line resolution, integrator's edit.
Answer: yes

**Q10.** File a new workstream-6 ticket for an `A-` rule that no `src/Platform.*` project outside
`Platform.Abstractions` contains a rule-id literal, to land after 54, 55 and 56?
Recommended: yes — a separate ticket, not inside 55, because `tests/Gateway.*` is ws6's path.
Answer: yes

**Q11.** Add `Rule id`, `Integration line` and `Adoption ticket` to `GLOSSARY.md` as the ticket
proposes?
Recommended: yes.
Answer: yes

**Q12.** Return ticket 55 to `ready-for-agent` once Q1 is answered, with `origin/main` named as the
branch point, and no other change beyond Q5 and Q6?
Recommended: yes.
Answer: yes

**Q13.** Record the branch-line rule as ADR-0016? Abandoning an integration line is expensive to undo
and surprising to a future reader who finds a tagged, green, abandoned branch carrying five landed
tasks.
Recommended: yes — one short ADR, written only if Q2 is a yes.
Answer: yes

---

## Ticket 17 — task 3.0 — G0: the Cognito items
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/17-3.0-g0-cognito.md`
(Status: ready-for-human. G0 gates I-6, which gates all twelve blocked workstream-3 rows and 4.2.
Q1..Q23 are the first pass of 13 October; Q24..Q38 are the second pass of tonight.)

**Q14.** (ticket Q1) Should the Cognito harness go in a new sibling folder, `spikes/g0/cognito/`,
built from the hardened and token-and-proxy sources, with `output-cognito.txt` committed?
Recommended: yes — the passed runs stay untouched and the run reuses the code that passed.
Answer: yes

**Q15.** (Q2) Should Playwright Chromium (1.63.0, pinned in the spike) drive managed login, with a
`--manual` fallback?
Recommended: yes — managed login is JavaScript-rendered and `Browser.cs` is only an `HttpClient`.
Answer: yes

**Q16.** (Q3) Should the harness read the infra outputs document and `fixtures/apps/*.json` for its
configuration?
Recommended: yes — it then runs the same clients and issuer the gateway will use.
Answer: yes

**Q17.** (Q4) Should secrets be read from Parameter Store at run time, with only lengths, hashes and
non-credential claims printed, and a self-check that fails the run if a credential appears?
Recommended: yes — the output is committed and redacting by hand misses things.
Answer: yes

**Q18.** (Q5) Should the gate path use the token library as task 3.8 configures it, and the item 2
questions use raw calls through `Duende.IdentityModel`?
Recommended: yes — only the library path proves 3.8 and 3.9; only raw calls choose which refresh
token is sent.
Answer: yes

**Q19.** (Q6) Should this run's evidence ids use a `C-` prefix numbered per item (`C-2.1`), listed in
the spike README?
Recommended: yes — `07-evidence.md` is frozen and item numbering avoids a clash with `T-C`.
Answer: yes

**Q20.** (Q7) Should 3.0 report through a "Gate G0, as run" section in `spikes/g0/README.md`, a row
per G0 item and per item 2 question?
Recommended: yes — `docs/` is the integrator's, and a change request is for asking, not reporting.
Answer: yes

**Q21.** (Q8) Should items 1, 4 and 5 be cited from the 11 October runs rather than run again?
Recommended: yes — they passed on the shipped packages.
Answer: yes

**Q22.** (Q9) Should "Blocked by" name 1.1 applied, 1.8 applied, `certs.sh` run, the hosts-file line,
port 8443 free and AWS credentials?
Recommended: yes — the ticket names only 1.8, and 1.6a landed written but not run. See also Q37.
Answer: yes

**Q23.** (Q10) Should the ticket split into 3.0a (agent writes and dry-runs the harness) and 3.0b (you
run it against Cognito)?
Recommended: yes — 3.0a has already landed on this reading.
Answer: yes

**Q24.** (Q11) Should the grace window count as insufficient — reopening ADR-0005's reserve design —
if a replay inside the window is refused or a late replay kills the successor token?
Recommended: yes — these are the two outcomes that break task 3.8.
Answer: yes

**Q25.** (Q12) If revoking a rotated-out token leaves its successor alive, should task 3.9 revoke the
token re-read from the session record, with the T-B18 race left a documented residual?
Recommended: yes.
Answer: yes

**Q26.** (Q13) If Cognito's refresh tokens exceed 4,096 characters, should the route be item 4's way
out — an in-house refresh class over `Duende.IdentityModel`?
Recommended: yes — it is already planned and small.
Answer: yes

**Q27.** (Q14) If `aud` is lost on a refresh without `resource`, should 3.8 send `resource` on every
refresh and accept T-B12's extra grant?
Recommended: yes — keeping the audience is worth one more call per login.
Answer: yes

**Q28.** (Q15) If resource binding does not set `aud` at all, should that be a finding rather than a
G0 failure (APIs check `client_id` and scope, as the mock API already does)?
Recommended: yes.
Answer: yes

**Q29.** (Q16) If discovery lists no revocation endpoint, should the `cognito` profile supply
`{BrowserOrigin}/oauth2/revoke` through a new `IIdpProfile` member that I-6 adds?
Recommended: yes — otherwise the library's revoke call throws (T-B14).
Answer: yes

**Q30.** (Q17) Should G0 try both client credential styles and keep `PostBody` if both work?
Recommended: yes — the code exchange and the refresh must agree.
Answer: yes

**Q31.** (Q18) Should item 3 pass only if Cognito accepts the `:8443` callback and sign-out URLs and
managed login works with the Terraform-made branding style, for both apps?
Recommended: yes — those are the two halves of item 3 as the plan states it.
Answer: yes

**Q32.** (Q19) Should G0 add an expectation that logging out of app1 at Cognito leaves app2's session
and refresh working?
Recommended: yes — Cognito's logout is global and the per-app boundary has never been checked.
Answer: yes

**Q33.** (Q20) Should refreshes be forced with `ForceTokenRenewal`, with one natural 5-minute expiry
on app1?
Recommended: yes — each run stays short and one natural expiry still proves the timing.
Answer: yes

**Q34.** (Q21) Should item 2 fail only when login, callback, refresh or logout does not complete for a
client, with the item 2 questions reported as findings with routes?
Recommended: yes — otherwise any surprising answer stops all identity work.
Answer: yes

**Q35.** (Q22) If item 3 fails, should 3.0b record it and open tickets for workstreams 1 and 5 plus a
change request for the contract examples, rather than take the way out itself?
Recommended: yes — workstream 3 owns none of those paths.
Answer: yes

**Q36.** (Q23) Should the harness create no AWS resources, with the pool and OAuth clients left
applied after G0 for G3?
Recommended: yes.
Answer: yes

**Q37.** (Q24) Should prerequisite 0 be "task 1.0 applied" — the bootstrap state bucket — added to
this ticket and to `spikes/g0/README.md` section 5? 1.1 and 1.8 use that backend and
`outputs-from-terraform.sh` reads it, and 1.0 has never been applied, so Q22's list started a step
too late.
Recommended: yes. (This is tonight's substantive find on this ticket.)
Answer: yes

**Q38.** (Q25) Should bootstrap be applied as it stands now, accepting a second apply when ws1 lands
ADR-0014's teardown role?
Recommended: yes — the role only gates deletes G0 never touches, and G0 heads the critical path.
Answer: yes

**Q39.** (Q26) Should the applies, `certs.sh`, the hosts line and the run happen in one sitting, in
that order, with `--cognito`'s exit-3 prerequisite check as the gate between them?
Recommended: yes — the check names every missing item and writes nothing.
Answer: yes

**Q40.** (Q27) Should each apply be recorded only by ticking the "Outstanding (human)" line in tickets
02 and 09, with the date and the check that proved it, and nothing committed under `infra/`?
Recommended: yes — an apply changes no repository path.
Answer: yes

**Q41.** (Q28) Should this ticket's Acceptance be rewritten as 3.0b's steps, keeping the 3.0a record
as history, rather than opening a separate ticket?
Recommended: yes — one ticket per plan row keeps `3.0` resolvable against the dispatch table.
Answer: yes

**Q42.** (Q29) Should a "not decided" verdict on item 2 or item 3 leave G0 open — no tag, I-6 waits,
the fix is another run?
Recommended: yes.
Answer: yes

**Q43.** (Q30) Should the `g0-mechanism` tag cite the commit carrying `output-cognito.txt` and
`report-cognito.md`, and say in its message that CI cannot run this exit test?
Recommended: yes — `spikes/` is outside the solution and needs AWS, a local CA and a browser.
Answer: yes

**Q44.** (Q31) Should the integrator tag `g0-mechanism` as part of I-6 and run `tools/roadmap.py`,
rather than 3.0b tagging it?
Recommended: yes.
Answer: yes

**Q45.** (Q32) If the run is attempted more than once, should only the last complete run's output,
report and README table be committed, with a spoiled attempt described in the ticket?
Recommended: yes — a committed `BAD` from a broken harness reads downstream as a G0 failure.
Answer: yes

**Q46.** (Q33) If a check fails for a harness reason, should you stop, leave the pool and clients
applied, and file a ws3 follow-up (3.0c) rather than editing the harness at the keyboard?
Recommended: yes.
Answer: yes

**Q47.** (Q34) Should I-6 start only after 3.0b reports both verdicts, rather than freezing everything
but the revocation member and the resource/`aud` decision?
Recommended: yes — G1b exists to fold G0's findings in.
Answer: yes

**Q48.** (Q35) Should G0's findings become ADR-0015 — ADR-0014 is now the teardown role — with the
consequences written into ADR-0005 and ADR-0011? Note: ADR-0015 is now taken by the shared rule ids
upstream (PR #24), so this number needs re-allocating; see Q52 (I-6 Q21).
Recommended: yes in substance, with the number left unallocated until it lands.
Answer: yes

**Q49.** (Q36) Should the integrator land the glossary terms now, before the run, leaving the two
conditional ADRs to I-6?
Recommended: yes — the report and follow-up tickets are written in those words.
Answer: yes

**Q50.** (Q37) Should the "Resource binding" glossary entry be reworded so it no longer asserts that
the provider sets `aud`, naming G0 item 2 as the open question for Cognito?
Recommended: yes.
Answer: yes

**Q51.** (Q38) Should the glossary gain a logout entry saying the platform's logout is per app while
the provider's sign-out may be global?
Recommended: yes.
Answer: yes

---

## Ticket 01 — task I-6 — Close G1b: fold G0 findings in and tag `contracts-v1`
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/01-I-6-close-g1b.md`
(Status: ready-for-human. 26 questions, rounds 1-3 from 13 October re-verified tonight plus a new
round 4-5. This gate unblocks 3.1-3.9, 4.2 and 3.5a — thirteen blocked rows.)

**Q52.** (I-6 Q21) Is the decision record's number left unallocated until I-6 lands, named by title in
the ticket, the index and the tag message? `docs/adr/0014-teardown-role-in-bootstrap.md` now exists
and ADR-0015 is taken, so Q5's "ADR-0014" is gone and a tag message cannot be edited.
Recommended: yes. (Answer this with Q48 and Q56.)
Answer: yes

**Q53.** (Q1) Should task 3.0 report G0 as a section in `spikes/g0/README.md` plus a recorded output
file, one line per gate item and sub-question?
Recommended: yes — the evidence stays with its harness.
Answer: yes

**Q54.** (Q2) Does G1b wait for all five G0 items, item 3 included?
Recommended: yes — item 3's way out changes every `Host` value.
Answer: yes

**Q55.** (Q3) Does only a failure of item 2's core block the tag, with each sub-question answered by a
response agreed in advance (Q26..Q30 here)?
Recommended: yes.
Answer: yes

**Q56.** (Q4) Are `AccessTokenResult` and `ISessionStore` part of the G1b draft set, changeable by I-6
without a change request?
Recommended: yes — their doc comments already say so.
Answer: yes

**Q57.** (Q5) Is the decision record always a new ADR titled "Freeze the identity contracts on the G0
report", plus status lines on ADR-0001, 0005 and 0011?
Recommended: yes — but see Q52 on the number.
Answer: yes

**Q58.** (Q6) Does the `Profile` enum keep both `standard` and `cognito`?
Recommended: yes — the test identity provider runs the standard style and PingFederate needs it.
Answer: yes

**Q59.** (Q7) Does `GET /bff/session` freeze `name` and `email` as nullable, and `expiresAt` as the
session's end (`SessionRecord.ExpiresAt`)?
Recommended: yes — the access token's expiry is internal.
Answer: yes

**Q60.** (Q8) Does `POST /bff/logout` become "attempts to revoke the refresh token, always removes the
session"?
Recommended: yes — it is the promise a browser can rely on.
Answer: yes

**Q61.** (Q9) Does the sentinel scheme stay private to `Platform.Identity`, out of `SchemeNames`?
Recommended: yes — no other workstream names it.
Answer: yes

**Q62.** (Q10) Is "G0 report" the term for the gate's verdicts and answers, leaving "finding" to the
Evidence tab's sense?
Recommended: yes.
Answer: yes

**Q63.** (Q11) At the freeze, do the schemas flip to `frozen-at-g1b` (dropping the root `exceptions`),
do the `[Freeze("G1b")]` markers go while `FreezeAttribute` stays, and are the three freeze tests
rewritten to assert an empty draft set?
Recommended: yes — it mirrors G1a's shape and keeps the mechanism for Phase 2.
Answer: yes

**Q64.** (Q12) Is `contracts-v1` an annotated tag on I-6's `main` merge, naming the gate, the ADR and
the green CI run?
Recommended: yes — that is the `skeleton-v1` convention.
Answer: yes

**Q65.** (Q13) If Cognito's grace window does not cover a replayed refresh token, does task 3.8 adopt
the reserve design behind an unchanged `IAccessTokenSource`?
Recommended: yes — ADR-0005 already names this trigger.
Answer: yes

**Q66.** (Q14) If a Cognito refresh token exceeds 4,096 characters, is item 4's way out taken (one
in-house class over Duende.IdentityModel) and ADR-0005 superseded?
Recommended: yes.
Answer: yes

**Q67.** (Q15) If `aud` is lost on a refresh without `resource`, does 3.8 send `resource` on every
refresh — and if Cognito refuses that, is resource binding declared unsupported for the `cognito`
profile with `oauth.resource` kept in the schema?
Recommended: yes.
Answer: yes

**Q68.** (Q16) If Cognito's discovery lists no revocation endpoint, does `IIdpProfile` gain an explicit
member that supplies one, frozen at G1b?
Recommended: yes — adding the member after the freeze needs a change request.
Answer: yes

**Q69.** (Q17) If revoking a refresh token does not revoke its successor, does 3.9 revoke the token it
reads after any in-flight refresh, with no contract change beyond Q60?
Recommended: yes.
Answer: yes

**Q70.** (Q18) If Cognito refuses a callback URL with a port, does I-6 bind 443, drop `:8443` from the
platform-config example, and open follow-up tickets for 5.5 and 1.8?
Recommended: yes — `fixtures/` and `infra/` are not I-6's paths.
Answer: yes

**Q71.** (Q19) If item 2 fails with no fix inside 3.0, does I-6 stay open and untagged, with a new
ticket comparing Finbuckle, Duende BFF v4 and a tenant-aware stack?
Recommended: yes.
Answer: yes

**Q72.** (Q20) Does I-6 also fix the stale ADR counts (`docs/agents/domain.md`, `CLAUDE.md`,
`docs/adr/README.md`) and `AGENTS.md:90`?
Recommended: yes — stale state misleads later sessions.
Answer: yes

**Q73.** (Q22) Does I-6 end with one commit that is `tools/roadmap.py`'s output, made after the tag,
with acceptance gaining "the generated block in `docs/roadmap.md` shows S3 closed by `contracts-v1`"?
Recommended: yes — `--check` reads `git tag`, so the block can only be regenerated once the tag exists.
Answer: yes

**Q74.** (Q23) Does `Platform.Abstractions.RuleIds` stay outside the G1b draft set, unmarked and frozen
on landing, with the record saying so in a line?
Recommended: yes — `auth.callback` and `auth.session` are text in the G1a-frozen contract.
Answer: yes

**Q75.** (Q24) Does the freeze cover the session key as an opaque, never-logged identifier, with the
record stating that `SessionRef` and `IRequestTelemetryFeature` need no change at G1b?
Recommended: yes — it avoids reopening the telemetry contract.
Answer: yes

**Q76.** (Q25) Does I-6 rewrite `docs/adr/README.md`'s "Three decisions carry open gate work"
paragraph instead of patching its number?
Recommended: yes — the count has already moved twice.
Answer: yes

**Q77.** (Q26) If ticket 47 has not landed when the G0 report arrives, does I-6 wait for it rather than
land red?
Recommended: yes — a gate must not be tagged on a red run. (Ticket 47 has now landed.)
Answer: yes

---

## Ticket 57 — CR-1.5 — Who the teardown role is, and two files `.gitignore` does not cover
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/57-CR-1.5-teardown-role-and-ignored-tfvars.md`
(Status: ready-for-human. Your working tree already holds the drafted answer — the five uncommitted
paths in the main checkout are exactly this decision. Closing it unblocks task 1.5, and through it
1.4, 1.7 and the teardown half of 6.7.)

**Q78.** Commit the decision that is already in your working tree? ADR-0014 (teardown role in
`infra/bootstrap`, ARN derived from the cell name) plus the `contracts/s3-layout.md` bullet, the
change request's `decided` Status and the `.gitignore` lines — with Q80 and Q81 folded in first.
Recommended: yes — the two rejected options check out against this repo.
Answer: yes

**Q79.** Keep the three `.gitignore` patterns (`*.tfvars`, `*.tfvars.json`, `backend.hcl`) without
`!*.tfvars.example`?
Recommended: yes — `git check-ignore -v` reports nothing ignored on the tracked `.example` and lock
files, so the negation is dead weight.
Answer: yes

**Q80.** Fix the teardown role's S3 scope before it lands? ADR-0014 and the request scope permissions
to `spa-platform-<cell>-*`; replace that with the bucket ARN `arn:<partition>:s3:::spa-platform-<cell>`
and `.../apps/*`.
Recommended: yes — the bucket is named exactly `spa-platform-<cell>`, so the drafted glob matches no
bucket and the role would be denied every delete.
Answer: yes

**Q81.** Make bootstrap's cell-name rule the only one and tighten `modules/cell` to match? Bootstrap
allows 2-20 characters starting with a letter; `modules/cell` allows 2-22 and a leading digit.
Recommended: yes — a name legal in the platform root and illegal in bootstrap derives an ARN for a
role that cannot exist, and the apply stays silent because the ARN sits in a policy condition.
Answer: yes

**Q82.** Have the platform root look the role up (`data "aws_iam_role"`) so a missing role fails the
plan?
Recommended: yes — it turns "nothing can ever delete under `apps/`" into a plan error instead of a G3
discovery.
Answer: yes

**Q83.** Make the teardown role's trust a required input with a `BoolIfExists` MFA condition, rather
than "recommended"? A new bootstrap variable, empty fails the apply, trusting named principals only —
not the CI plan role, not GitHub OIDC.
Recommended: yes — "recommended" in a decision record is how this change request started.
Answer: yes

**Q84.** Accept that one role per cell is the normative answer and that extra entries in
`teardown_principal_arns` are a documented deviation?
Recommended: yes — otherwise the platform root quietly becomes a place to add a second deleter and
P-04 stops meaning what it asserts.
Answer: yes

**Q85.** Record the two consequences in ADR-0014 — unbounded release retention, and no lifecycle or
replication rule on the platform bucket?
Recommended: yes — `aws:PrincipalArn` is absent for S3's own expiration, so a lifecycle rule would
delete "immutable" releases while P-04 still passed.
Answer: yes

**Q86.** Keep the derivation normative for every consumer — `teardown.sh`, the 6.7 runbook and P-05
derive the name, `contracts/infra-outputs.schema.json` gains no field — with bootstrap's
`teardown_role_arn` output kept for humans only?
Recommended: yes — if any consumer reads the output, option 2 is back through the side door.
Answer: yes

**Q87.** Leave P-04 negative-only, with teardown's positive half proved by task 6.9?
Recommended: yes — P-04 run as the teardown role would observe the exception instead of the rule.
Answer: yes

**Q88.** Add `infra/bootstrap/terraform.tfvars.example` to the request's "what stays with workstream
1" list, beside the README? Its first comment says "terraform.tfvars is not ignored by `.gitignore`",
which this commit makes false.
Recommended: yes.
Answer: yes

**Q89.** Land both halves in one commit? (Your own open question.)
Recommended: one commit — they share a change request and an ADR, and splitting leaves the request
reading `decided` in a commit whose `.gitignore` does not yet match its Decision section.
Answer: yes

---

## Ticket 06 — task 1.5 — Private versioned bucket and image repositories
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/06-1.5-bucket.md`
(Status: ready-for-human. 1.5 is one of the two things blocking 1.4, and through 1.4 blocks 1.6b, 1.7
and 1.9. Note the premise correction in Q90: there is nothing left to merge.)

**Q90.** Is task 1.5 still one open ticket, re-scoped to ADR-0014's implementation? Its code is
already in `main` (merged as `5e2cbcc`, PR #5), so the "merge it in the morning" note is stale.
Recommended: yes — keep one ticket whose remaining work is the teardown role, the root's defaulting
and the README fix; do not split out a 1.5b.
Answer: yes

**Q91.** Should workstream 1 add the teardown role to `infra/bootstrap` under task 1.5, rather than
reopening task 1.0?
Recommended: yes — both are workstream 1 paths and nothing has been applied.
Answer: yes

**Q92.** Should the platform root derive the role ARN and default `teardown_principal_arns` from it
(`default = null`, `coalesce(...)` in `main.tf`), leaving `modules/cell`'s no-default list and its
non-empty validation untouched?
Recommended: yes — it is ADR-0014 section 2.
Answer: yes

**Q93.** Should the operator's own principal stay out of `teardown_principal_arns`, so `teardown.sh`
assumes the role?
Recommended: yes — exempting the operator would invert P-04 at G3.
Answer: yes

**Q94.** Should the bucket's default name become `spa-platform-{cell}-{account id}`, matching the state
bucket's pattern, with a change request for the illustrative `spa-platform-poc` in the outputs example
and the `Registry:Bucket` row?
Recommended: yes — S3 names are global and every consumer reads the name from `outputs.json`.
Answer: yes

**Q95.** Should 1.5 ship `infra/modules/cell/tests/storage.tftest.hcl` — a plan-only `terraform test`
over the four statement sids, both condition keys, the `ArnNotLike`, immutable tags and the non-empty
validation?
Recommended: yes — `fmt` and `validate` both pass on a policy with the wrong condition key.
Answer: yes

**Q96.** Should the teardown role's trust policy require `aws:MultiFactorAuthPresent = true`?
Recommended: yes — its only power is deleting published releases and a human runs it. (See Q83.)
Answer: yes

**Q97.** Should the delete deny stay scoped to `apps/`, leaving the registry's version history
protected by IAM alone, recorded as an accepted gap?
Recommended: yes — widening it would force the break-glass role to be able to delete the registry too.
Answer: yes

**Q98.** Should the bucket get exactly one lifecycle rule — abort incomplete multipart uploads after 7
days — and no expiration of any object or noncurrent version?
Recommended: yes — any version expiry would delete the history `spactl registry rollback` reads.
Answer: yes

**Q99.** Should encryption stay SSE-S3, with the no-op `bucket_key_enabled = true` deleted?
Recommended: yes — SSE-KMS would force a `kms:Decrypt` onto the task role that P-03 asserts holds
nothing else.
Answer: yes

**Q100.** Should S3 Object Lock be declined, and recorded as a rejected alternative on ADR-0009?
Recommended: yes — it can only be enabled at bucket creation, and compliance mode would make P-05
unpassable.
Answer: yes

**Q101.** Should the repositories get `scan_on_push = true`, no lifecycle policy, keep
`force_delete = true`, and keep the `spa-{cell}/...` names the frozen outputs example shows?
Recommended: yes.
Answer: yes

**Q102.** Should the three integrator edits (the bucket-name example, a `terraform test` step in CI,
ADR-0009's Object Lock row) go in one change request `contracts/changes/1.5a-...`, which says itself
that the `docs/plan/` row wants an ADR rather than an edit?
Recommended: yes — one task's tail should be one review.
Answer: yes

**Q103.** Should the ticket's acceptance be restated in P-04's own words — "a second write to an
existing release key is refused, and so is the delete that would allow one" — plus boxes for the role,
the no-tfvars apply and bootstrap's README?
Recommended: yes — "delete followed by write" describes a sequence that cannot occur.
Answer: yes

**Q104.** Should 1.5 be callable done on the written-and-tested boxes, with the deployed P-04 box
deferred to task 6.9?
Recommended: yes — holding 1.5 open until G3 blocks the work that makes G3 reachable.
Answer: yes

---

## Ticket 03 — task 1.2 — `modules/cell` network
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/03-1.2-cell-network.md`
(Status: ready-for-human. The code is already on `main`; what is missing is proof. 1.9's allowlist
assertions depend on the answers here.)

**Q105.** Correct the ticket's reason and keep 1.2 open on proof? The two commits (7d63ded, 8773fa2)
are ancestors of `main`, so "not merged" is wrong; what is missing is any plan or test that has ever
looked at this module.
Recommended: yes.
Answer: yes

**Q106.** Read "no other ingress rule" as fencing ingress only, with egress bounded by 1.9's named
destinations instead of a count?
Recommended: yes — a hard count would make a correct addition look like a regression.
Answer: yes

**Q107.** Prove the row with a `mock_provider` `terraform test` under `infra/modules/cell/tests/`,
with the applied plan still required later for P-01 and P-06?
Recommended: yes — three sibling modules already prove their rows this way with no credentials.
Answer: yes

**Q108.** Keep deriving the two zones, but add an optional `availability_zones` variable (empty = first
two) to override it?
Recommended: yes — a zone without ARM64 Fargate capacity then costs a tfvars line.
Answer: yes

**Q109.** Lock the VPC's default security group to zero rules with `aws_default_security_group`?
Recommended: yes — it makes "no other ingress rule" true of the account, not only of the file.
Answer: yes

**Q110.** Add TCP 53 and UDP 53 egress from the task group to the VPC CIDR only, as insurance against
the Amazon-DNS security-group exemption not covering a Fargate ENI?
Recommended: yes — two VPC-scoped rules buy away a first-apply failure whose symptom is a task that
never starts. (This is the one latent correctness risk the grilling found.)
Answer: yes

**Q111.** Prove absence with a credential-free static check in `tools/scripts/` as well as leaving the
applied plan to 1.9's `plan-policy.sh`?
Recommended: yes — `terraform test` asserts attributes of named resources and cannot say "nothing else
exists".
Answer: yes

**Q112.** Raise `contracts/changes/1.2-terraform-test-in-ci.md` asking the integrator for one CI step
that runs `terraform test` wherever a `tests/` directory exists, plus the static check? Note: upstream
`main` may already carry this (commit 2320cb2, "Merge Terraform test discovery") — check before
filing.
Recommended: yes if not already landed.
Answer: yes

**Q113.** Note in that change request that the plan will now show three security groups (two
functional, one deliberately empty) and six rule resources, for 1.9's allowlist and the plan row's
wording?
Recommended: yes — silently breaking 1.9's "the two security groups" is worse than flagging it.
Answer: yes

**Q114.** Leave `ForwardedHeaders:TrustedNetworks` as the VPC CIDR rather than narrowing it to the two
subnet CIDRs?
Recommended: yes — the narrowing buys nothing and would re-add an output 8773fa2 deliberately deleted.
Answer: yes

**Q115.** Record the non-goals (no IPv6, no flow logs, no NACL changes, no VPC endpoints) in the module
README beside the no-NAT argument?
Recommended: yes.
Answer: yes

**Q116.** Propose an ADR for "no NAT: public subnets, a public task address, and the ALB internal with
the tunnel as its only path"?
Recommended: yes — reversing it re-addresses the VPC, and the alternative had a price ($33/month NAT,
or ~$7 per endpoint per zone).
Answer: yes

**Q117.** Adopt `ALB group`, `Task group`, `Gateway port` and `Tunnel path` into `GLOSSARY.md`, rather
than renaming the Terraform resources?
Recommended: yes — renaming would churn 1.3 and 1.4.
Answer: yes

---

## Ticket 53 — G1b — The stage-1 405 is logged but neither counted nor on a request line
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/53-stage-1-405-is-neither-counted-nor-on-a-request-line.md`
(Status: ready-for-human. The grilling found a fifth option the ticket never listed — swap stages 1
and 2 — cheaper than all four it did list.)

**Q118.** Hold the contract sentence rather than narrow it? Fix the pipeline so every rejection the
gateway answers reaches stage 2, and reject the ticket's options 2, 3 and 4.
Recommended: yes — the stage-1 405 is missing from three signals, not one: `security.rejections`, the
request line, and `gateway.requests`, which claims to count "requests the gateway answered".
Answer: yes

**Q119.** Bound "rejection" to what the application answers, and say so? Name the server-level refusals
(Kestrel's oversized-header 431, a malformed request line) as outside the rule-id scheme in
`contracts/request-gate.md`.
Recommended: yes — that exception already exists in `contracts/http-contract.md` and N-20.
Answer: yes

**Q120.** Add `Rejection`, `Rule id`, `Request line` and `Stage` to `GLOSSARY.md`, with "refusal" named
as the word to avoid for the gateway's own rejections?
Recommended: yes — "rule id" is used in three files and defined in none.
Answer: yes

**Q121.** Swap the first two stages — telemetry outermost, forwarded headers and the method allowlist
second — instead of moving the allowlist later? `MethodAllowlist` then writes
`IRequestTelemetryFeature?.Rule` like every other rejecting stage.
Recommended: yes — the request line carries nothing derived from a forwarded header, so stage 2's
reason to be first does not bind telemetry, and the method check ends up no later than it is today.
Answer: yes

**Q122.** Name the method allowlist explicitly in the pipeline table's row for
`UsePlatformForwardedHeaders`?
Recommended: yes — that silence produced change request 6.4's wrong writer table and this ticket.
Answer: yes

**Q123.** State in `contracts/telemetry.md` that the request line and span carry no value derived from
a forwarded header, and assert it in one test?
Recommended: yes — that is the only property that makes the swap safe.
Answer: yes

**Q124.** Renumber — telemetry becomes stage 1, forwarded headers stage 2 — rather than calling
telemetry "stage 0"?
Recommended: yes — a stage 0 keeps old sentences true at the cost of making the numbering lie.
Answer: yes

**Q125.** Land it as one integrator commit (pipeline, request-gate, telemetry, `Program.cs`, the two
abstractions comments) plus a new workstream-4 ticket and one extra acceptance line on ticket 50?
Recommended: yes. Note ticket 50 has now landed, so the extra line becomes a follow-up.
Answer: yes

**Q126.** Decide it now on paper and land it at G1b alongside I-6?
Recommended: yes — `contracts/README.md` holds a changed stage for the next gate.
Answer: yes

**Q127.** Replace `contracts/telemetry.md`'s gap paragraph with the invariant, and give
`contracts/request-gate.md`'s sentence its boundary clause?
Recommended: yes.
Answer: yes

**Q128.** Prove it by extending N-20 in both tiers, minting no new test id and no new rule id?
Recommended: yes — N-20 already owns `TRACE` and `CONNECT`.
Answer: yes

**Q129.** Record it as an ADR, "Telemetry is the outermost stage"?
Recommended: yes — the pipeline contract currently argues at length for the opposite order.
Answer: yes

---

## Ticket 43 — CR-5.5 — `TestApps` no longer mirrors `app1.json`
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/43-CR-5.5-testapps-mirror.md`
(Status: ready-for-human since 13 October — still open. Closing it is the only thing keeping task 5.5
from done.)

**Q130.** Keep `TestApps`' defaults and drop the mirror claim? No change to `HostSuffix`, `Apis` or
`issuer`, and no change to the fixtures.
Recommended: yes — equalising the double would break the Proxy tests that rely on empty `Apis`.
Answer: yes

**Q131.** Add `Fixtures.Tests` to the `JsonSchema.Net` row in `docs/dependencies.md`?
Recommended: yes — it references the package today.
Answer: yes

**Q132.** Close the request as I-7 was closed — Status `decided, <date>`, a `## Decision` section, both
edits in one commit?
Recommended: yes — prose-only edits to frozen files need no gate.
Answer: yes

**Q133.** Have the new comment say `Entry` builds a "registry entry", not an app definition?
Recommended: yes — `Entry` fills `clientId`, `clientSecretRef` and `release`.
Answer: yes

**Q134.** Replace the sentence with wording that describes only the double and points at
`fixtures/apps/`, without listing what `app1.json` contains?
Recommended: yes — the request's proposed sentence restates fixture facts and would go stale the same
way.
Answer: yes

**Q135.** Keep `issuer = "primary"` until I-6, and revisit it when composed suites pair `TestApps` with
real platform configuration?
Recommended: yes — `OAuthDefinition` is draft until G1b.
Answer: yes

**Q136.** Keep `HostSuffix` without the `:8443` port?
Recommended: yes — the schema accepts a port-less host.
Answer: yes

**Q137.** Have the Decision say the mirror was never true rather than that it drifted?
Recommended: yes — I-2 and I-3 contradicted the comment from its first commit.
Answer: yes

**Q138.** Also add `Fixtures.Tests` to the `Microsoft.Extensions.TimeProvider.Testing` row, and leave
`spactl` in the `JsonSchema.Net` row for task 2.1 to settle?
Recommended: yes — the column is "Expected in", so `spactl` is a planned user.
Answer: yes

**Q139.** Add no new `CODING_STANDARDS.md` rule?
Recommended: yes — "A guard reads the thing it guards" already covers the case.
Answer: yes

**Q140.** Leave the frozen `AppDefinition` record's name alone, handling the clash with the glossary
term only in comments and the glossary?
Recommended: yes — a rename after G1a touches every workstream.
Answer: yes

**Q141.** Confirm that workstream 5 has nothing further to do, so task 5.5 is done once this request is
decided?
Recommended: yes.
Answer: yes

---

## Ticket 49 — task 2.1b — The validator refuses a `timeoutSeconds` the proxy cannot apply
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/49-2.1b-validator-refuses-unapplicable-timeout.md`
(Status: blocked, waiting on MR-1 and 4.1a. From its Comments, and from task 4.1a's outstanding note.)

**Q142.** `Platform.Registry` cannot reference `ProxyTimeouts.MaxTimeoutSeconds` directly, because A-03
forbids referencing another workstream's project. Should workstream 2 get a change request to
republish the constant from `Platform.Abstractions`, or keep its own copy guarded by a drift test?
Recommended: republish from `Platform.Abstractions` by change request — ADR-0015 has just set the
precedent that a value the contracts fix lives there, and a second copy is the shape that produced
tickets 54, 55 and 56. No request was filed tonight; this is still open.
Answer: yes

---

## Ticket 50 — task 6.4a — Stage 2 writes `rule` and `session_ref`
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/50-6.4a-stage-2-writes-rule-and-session-ref.md`
(Status: resolved and integrated. One note left for you rather than filed as a change request.)

**Q143.** `IRequestTelemetryFeature`'s doc comment in `src/Platform.Abstractions` still says "Stage 2
does not install this yet" and that every `Features.Get<IRequestTelemetryFeature>()` returns null.
Stage 2 now installs it on every request. Retire the paragraph as part of I-6?
Recommended: yes — fold it into I-6's integrator-path sweep (Q72), since the path is yours and the
paragraph reads as deliberately temporary.
Answer: yes

---

## Ticket 54 — task 2.3b — Hosting reads the shared rule ids
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/54-2.3b-hosting-adopts-the-shared-rule-ids.md`
(Status: ready-for-agent, blocked on change request 2.3b. One gap decided rather than asked.)

**Q144.** The three rule-id literals in `tests/Platform.Hosting.Tests` (`AssetTests.cs` 305-306 and
334-335, `HostResolutionTests.cs` 182) were left as literals: a test asserting the contract's spelling
should write that spelling out rather than read the constant it checks, and the ticket's grep
acceptance is scoped to `src/Platform.Hosting`. Confirm?
Recommended: yes — same reading as Q7 on ticket 55; confirming both at once keeps the two tickets
consistent.
Answer: yes

---

## Ticket 47 — task 2.1a — Link the contract schemas
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/47-2.1a-link-contract-schemas.md`
(Status: resolved and integrated. One change request filed during review.)

**Q145.** `contracts/changes/2.1a-dockerfile-contracts-copy-comment.md` is open: `src/Gateway/Dockerfile`
lines 29-33 now say Platform.Registry "embeds its own copies under Schemas/ today, so nothing reads
these yet", which the deletion of `Schemas/` makes false. Suggested wording is in the request. Apply it?
Recommended: yes — it is a two-line integrator edit, and the request exists only because
`src/Gateway` is off limits to workstream 2.
Answer: yes

---

## Not questions — notes the integrator left on blocked tickets, for when they open

Tickets 20 (3.3), 22 (3.5) and 26 (3.9) carry the same integrator note from change request 6.4: how
to compute `session_ref` (`SessionRef.Of`, never by hand), that the session key must not reach stage 2
or any log line, that workstream 3 is the writer because only it holds the key, that whichever stage
rejects under `auth.callback` or `auth.session` sets it null-conditionally on the same feature and
declares no instrument of its own, and that task 4.2's 401 is not a rejection and leaves `Rule` null.
Ticket 56 (3.5a) carries ADR-0015's note that there is one adoption ticket per owner. Nothing is asked
of you there before `contracts-v1`.
