# Questions for Adam — overnight run of 2026-10-15

## PR review amendment — 2026-10-15

This file preserves the overnight snapshot and its original questions. The final ADR-0014 and
change requests supersede these review errors in Q49, Q56, Q63, Q69 and their ticket-57 sources:

- Current Terraform at `070e3fae` defaults to `spa-platform-<cell>`, with a bucket-name override.
  The account-qualified name is proposed, not implemented; `spa-platform-<cell>-*` misses the former
  but matches the latter. Teardown grants must use the actual bucket ARN, including overrides.
- `Allow` plus `BoolIfExists: true` accepts long-term keys when the MFA key is absent. `Deny` plus
  `BoolIfExists: false` also denies federation when the key is absent. ADR-0014 now separates
  IAM-user trust (`Bool: true`) from explicitly allowlisted federated human roles whose MFA is
  enforced upstream; input validation rejects root and STS-session ARNs.
- Object Lock can be enabled on existing versioned buckets; its irreversible enablement and
  retention/bypass trade-offs, not mandatory bucket replacement, are the relevant constraints.
- Role separation protects against the ordinary writer, not a trusted human who can switch
  between teardown credentials and writer credentials. No AWS apply or live role is claimed.
- The plan-ignore patterns land here; the apply-recipe and CI artifact follow-ups remain open.

Primary sources checked on 2026-10-15:
[IAM MFA semantics](https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_policies_condition-keys.html#condition-keys-multifactorauthpresent),
[S3 Object Lock configuration](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock-configure.html),
[S3 conditional writes](https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes.html).


**118 questions, Q1..Q118, grouped by ticket, most-unblocking first.** Each has a recommendation and
an empty `Answer:` line.

**Read this first.** You answered all 145 questions of the 2026-10-14 file `yes` at 07:47 this
morning, and that file is preserved as
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/questions-2026-10-14-answered.md`.
Tonight's grills did **not** read your answers — the ticket files carry the questions, not the
answers — so several tickets restate questions you have already settled. This file asks only what is
genuinely open:

- questions **new** on tonight's base (`origin/main`, `070e3fa`),
- questions whose answer the rebase **changed or reversed**, flagged `REVERSES` or `CHANGED`,
- questions you have **not** seen (tickets 02 and 04 were grilled for the first time tonight).

Each entry names its ticket-local number, so you can read it in context. Where a question restates
one you answered `yes` yesterday without change, it is listed in the ticket's header line as
"already settled" rather than re-asked.

**Three cross-ticket conflicts to decide once, not three times.** Flagged in place below:

1. **The legal cell name.** Q33 (ticket 02) recommends 3–40 characters; Q53 (ticket 57) and Q64
   (ticket 06) recommend 3–22. The bucket and load-balancer names force 22, and a landed test on
   trunk pins 22. Answer Q53 and let Q33 follow it.
2. **The ADR number for G0's findings.** Q10 (ticket 17) and Q19 (ticket 01) both allocate it; Q19
   also sets the numbering rule. ADR-0016 is free once your ADR-0014 lands.
3. **Ticket 53's stage swap and ticket 55's landed work.** Q105–Q116 touch
   `MethodAllowlist.cs`, which task 4.3b changed tonight. Q111 sequences them.

---

## Ticket 17 — task 3.0 — G0: the Cognito items
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/17-3.0-g0-cognito.md`

Status: ready-for-human. **This is the head of the critical path**: G0 unblocks I-6, which unblocks
all nine workstream-3 rows plus 4.2, 6.2, 6.3, 6.5 and 3.5a — 15 of tonight's 21 blocked rows.
Third grilling pass; rounds 6–8. Ticket-local Q1–Q38 are already settled (Q1–Q23 by the 3.0a answer
and your four review calls, Q24–Q38 by your 2026-10-14 `yes`), **except** that Q41, Q45 and Q48 below
re-ask Q24, Q27 and Q35 because the facts under them moved.

**Q1.** (local Q39) Before the sitting, fast-forward the checkout to `origin/main` — this ticket
copied aside, the four modified files stashed, both ADR rows kept (0014 then 0015) — and commit your
change-request decision as one integrator commit whose subject names no plan row?
Recommended: yes. `HEAD` is 37 commits behind, two of those five paths also changed upstream, and the
2026-10-14 pass went stale for exactly this reason.
Answer:

**Q2.** (local Q40) Commit this ticket's second and third passes as the integrator in that same push,
over upstream's 278-line copy, together with the Acceptance rewrite you approved as Q28?
Recommended: yes. The tracker is tracked on this base, so the pass the sitting follows should be the
one other sessions can open and cite.
Answer:

**Q3.** (local Q41 — replaces the Q24 you answered yes) Should prerequisite 0 read "task 1.0 applied
**from this workstation**: `terraform.tfvars` written from the example, `terraform apply`, and
`infra/bootstrap/terraform.tfstate` kept on this machine", in this ticket and in
`spikes/g0/README.md` section 5, with no change to `Prerequisites.cs`?
Recommended: yes. Both later roots init from that local state, and a missing bucket already stops
`terraform init` of the identity root more loudly than the harness could.
Answer:

**Q4.** (local Q42) For the bootstrap apply, should every default stand and only
`github_repository = "adammedford/spa-platform"` be set — after checking the account for an existing
GitHub OIDC provider — leaving `vars.AWS_PLAN_ROLE_ARN` unset until task 1.9?
Recommended: yes. The root refuses to apply with neither trust input, and every price, backend block
and resource name assumes `cell = "poc"` in `us-east-1`.
Answer:

**Q5.** (local Q43) Should the sitting end by copying `infra/bootstrap/terraform.tfstate` and each
root's `terraform.tfvars` somewhere durable, with one line added to workstream 1's 1.7 row about
teardown needing a lasting answer?
Recommended: yes. Bootstrap keeps local state by design and your `.gitignore` change ignores tfvars
too, so one machine would hold the only record of what was applied.
Answer:

**Q6.** (local Q44) Should the run's evidence land as a workstream-3 pull request carrying only
`spikes/g0/`, reviewed by another session against this ticket rather than re-run, and merged with
`tools/land.sh` under the subject **"Merge task 3.0: ..."**?
Recommended: yes. `tools/roadmap.py` says a split id never lands its parent row, so a subject naming
3.0b would leave G0 at 4 of 6 for ever — and A-05 refuses a push that also edits a ticket.
Answer:

**Q7.** (local Q45 — replaces the Q27 you answered yes) Should the applies be recorded in a separate
integrator pull request that ticks tickets 02 and 09 and this one, merged under the subject **"Merge
task 1.1: the identity root applied, and its discovery document loads"**?
Recommended: yes. Row 1.1 is part-landed with no code left to merge, so this tick is the only commit
that can ever land it, and ownership forces it out of the evidence push anyway.
Answer:

**Q8.** (local Q46) Should the integrator tag `g0-mechanism` on the evidence merge commit, push the
tag first, then run `tools/roadmap.py` and push the refreshed blocks as their own commit?
Recommended: yes. CI checks the generated block against `git tag` on every push, so a refresh landing
before the tag is red, and `land.sh` covers a merge but not a tag.
Answer:

**Q9.** (local Q47) Should the tag cite that commit and say CI cannot run this exit test, **and**
`docs/agents/landing-work.md` gain one paragraph on when a committed workstation run proves a gate
and what it must carry?
Recommended: yes. `landing-work.md` asks for a CI run URL, and the same retro spent a CI step
avoiding machine-local Terraform evidence — so this exception is now surprising enough to write down,
and G3 will reuse it.
Answer:

**Q10.** (local Q48 — replaces the Q35 you answered yes) Should G0's findings become **ADR-0016**,
with the status changes written into ADR-0005 and ADR-0011?
Recommended: yes. 0015 is now the shared-constants decision and 0014 is your teardown role, so the
second pass's number is taken. Decide with Q19.
Answer:

**Q11.** (local Q49) If a check fails for a harness reason, should the 3.0c ticket be written into
Q7's integrator pull request, with no spoiled output committed?
Recommended: yes — **but not at the path the ticket names.** It says
`.scratch/overnight/issues/57-3.0c-<slug>.md`; 57 is CR-1.5. The next free number is **58**.
Answer:

**Q12.** (local Q50) Should the three-verdict vocabulary live in `GLOSSARY.md` (the **Gate** entry
plus a **Not decided** entry) and its consequence in ADR-0016, with `docs/plan/` untouched?
Recommended: yes. The plan is a frozen export and cannot carry a word it does not have.
Answer:

**Q13.** (local Q51) Should the glossary terms land before the sitting, with Q9's paragraph and
ADR-0016 landing with or after the tag?
Recommended: yes. The report and the follow-up tickets are written in those words, while the
paragraph describing the tag cannot be written before the tag is decided.
Answer:

---

## Ticket 01 — task I-6 — Close G1b: fold G0 findings in and tag `contracts-v1`
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/01-I-6-close-g1b.md`

Status: ready-for-human. Second in unblocking power: no `contracts-v1` tag exists, and AGENTS.md
forbids any `src/Platform.Identity` work before it. Rounds 6–8 against `origin/main`. Ticket-local
Q2–Q10, Q12–Q22, Q24 and Q25 are already settled by your 2026-10-14 `yes`; Q1, Q11, Q23 and Q26 are
**withdrawn** because a fact on trunk settles them (Q27 below confirms all four at once).

**Q14.** (local Q27) If a G0 item comes back `not decided` rather than passed or failed, does that
block the tag too, with the route being one more full run of `cognito/` rather than the plan's stop
clause?
Recommended: yes. `--skip-natural-expiry` and an absent branding style both produce "not decided",
which is an absent answer and not a failure; freezing on an unanswered question is what G1b exists to
prevent.
Answer:

**Q15.** (local Q28) Does I-6 ratify the landed harness's item 2, in which resource binding is a
finding (C-2.7) and not part of the pass bar, with the record saying the plan's "resource binding
sets the access token `aud`" is read as an item 2 question for Cognito?
Recommended: yes. Upstreams validate their own tokens by `client_id` and scope, so no gateway
contract depends on `aud`, and the alternative stops the critical path over an audience claim. Note:
the harness quietly narrows the plan's item 2 this way — this question is where you ratify it or not.
Answer:

**Q16.** (local Q29 — narrows the Q67 you answered yes) If C-2.7 shows Cognito ignores `resource`,
does task 3.8 omit `resource` for the `cognito` profile as T-B12 says, instead of sending it on every
refresh?
Recommended: yes. Sending it would pay T-B12's extra grant per login for an `aud` that never arrives,
and the frozen evidence tab already says 3.8 leaves it off; `oauth.resource` stays in the schema for
profiles that honour it.
Answer:

**Q17.** (local Q30) If the G0 report needs both a revocation endpoint (C-2.3) and a client
credential style (C-2.21), do they arrive as one new `IIdpProfile` member returning one small record,
rather than a member each?
Recommended: yes. Two of the six item-2 questions land on the same seam, and after the tag a third
costs a change request.
Answer:

**Q18.** (local Q31) Does I-6 freeze `SessionRecord` with no format version, leaving MR-9's fix (a
format version, a compatibility rule and a rollback test) to a change request in S8?
Recommended: yes. ADR-0015 decision 4 is the precedent — the bound lands with the rule, not before it
— and a version field nothing validates misleads the next reader.
Answer:

**Q19.** (local Q32) Is the next free ADR number always one above the highest the index lists, never
a filled gap, with that rule written into `docs/adr/README.md`?
Recommended: yes. Trunk's index runs 0013 then 0015 because 0014 sits in your working tree;
gap-filling makes two sessions collide on "the next free number". Consistent with Q46/Q73 (land 0014
in its own gap now) and with Q10 (G0 findings = 0016).
Answer:

**Q20.** (local Q33) Does the "What is draft until G1b" table gain a row naming the four test doubles
(`FakeAccessTokenSource`, `StubIdpProfile`, `InMemorySessionStore`, `SessionStoreContract`), with I-6
clearing their three "Draft until G1b" comments at the freeze?
Recommended: yes. Nothing mechanical covers `Platform.Testing`, and `StubIdpProfile` changes with any
new `IIdpProfile` member — which Q17 would add.
Answer:

**Q21.** (local Q34) Does I-6 record the three workstream-owned files that still assert the draft
state (`tools/spactl/AppEntry.cs:9`, `src/Platform.Proxy/PlatformProxyExtensions.cs:16`,
`src/Platform.Identity/PlatformIdentityExtensions.cs:13`) and open one ticket per owner, with none of
them blocking the tag?
Recommended: yes. AGENTS.md bars the integrator from those paths even for a one-line fix, and each
owner's next task touches the file anyway.
Answer:

**Q22.** (local Q35) When I-6 is picked up, does its header become `Blocked by: 17` (task 3.0b) with
acceptance item 5 struck?
Recommended: yes. Ticket 47 has landed and the schema copies it guarded are gone, task 3.0 has split,
and the tracker's convention is ticket numbers rather than task ids.
Answer:

**Q23.** (local Q36) Is the post-tag roadmap refresh one generated commit pushed straight to `main`,
as `tools/land.sh` pushes its own?
Recommended: yes. That is the pattern the script sets for exactly this commit, and leaving it to the
next task leaves `main` failing its own `--check`.
Answer:

**Q24.** (local Q37) Does the stale-prose sweep also cover `CLAUDE.md:15`,
`docs/agents/domain.md:21,34,55` and `docs/roadmap.md`'s S3 prose ("G0's findings")?
Recommended: yes. The ADR range and the "three decisions carry open gate work" claim are duplicated
in `docs/agents/domain.md`, and the roadmap's prose is explicitly the part no script checks.
Answer:

**Q25.** (local Q38) Does I-6 edit `GLOSSARY.md` in the same commit: `C-` added to **Evidence ids**,
**Resource binding** no longer promising `aud`, and **Reserve design**'s trigger pointed at C-2.17
and C-2.18?
Recommended: yes. "The provider sets the access token's `aud`" is the sentence a later reader would
cite against Q16.
Answer:

**Q26.** (local Q39) If the teardown ADR never lands, does `docs/adr/README.md` gain one line saying
0014 was allocated and not used?
Recommended: yes — and nothing if it does land. A missing number in a numbered register costs the
next session a search.
Answer:

**Q27.** (local Q40) Are the four withdrawals confirmed — local Q1 (the landed harness writes
`report-cognito.md` and the `g0-as-run` table itself), Q11 (change request 6.4 already commits the
freeze tests to an empty draft set), Q23 (ADR-0015 says `RuleIds` is not draft) and Q26 (ticket 47
has landed) — and local Q15 narrowed by Q16 above rather than reversed?
Recommended: yes. Each withdrawal is an agent's reading of a file, so one `yes` confirms all four.
Answer:

---

## Ticket 02 — task 1.1 — `modules/idp-cognito` and the identity root
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/02-1.1-idp-cognito-root.md`

Status: ready-for-human. **First grilling, so all 18 are new to you.** Row 1.1 is part-landed; what
remains is the apply, which is step 2 of ticket 17's sitting. Five rounds, frontier empty.

**Q28.** (local Q1) Keep this ticket's Status at `ready-for-human`, annotated "implementation
resolved 2026-10-14", since the only work left is your AWS apply? (The grill already flipped it from
`resolved`.)
Recommended: yes. Seven sibling tickets use that status for exactly this shape, and `resolved` hides
an unticked acceptance box.
Answer:

**Q29.** (local Q2) Replace the review's "if `infra/bootstrap` was already applied, re-apply it" with
"step 0: apply `infra/bootstrap`"?
Recommended: yes. Nothing in this platform has ever been applied, so there is no re-apply, and
`AdminGetUser` is already in the role.
Answer:

**Q30.** (local Q3) Do 1.1's apply as the second step of ticket 17's single sitting (bootstrap → idp
→ discovery → apps → G0) rather than as its own session?
Recommended: yes. The same credentials and the same state bucket serve all of it, and a 1.1-only
session leaves the account half-built.
Answer:

**Q31.** (local Q4) Strike the "(integrator) change request open" Outstanding?
Recommended: yes. It was decided 2026-10-14 and `docs/dependencies.md:73` carries the row.
Answer:

**Q32.** (local Q5) **Sharpest find of the night.** Move the three READMEs' `plan -out=` to a path
outside the repository, and file a change request adding `*.tfplan` and `*.tfplan.json` to
`.gitignore`?
Recommended: yes. The documented apply recipe writes `idp.tfplan` **inside the repo**, and it carries
every generated test-user password; the apps root's equivalent carries the client secrets. No
`.gitignore` pattern matches either, today or in your working-tree version. This is live the moment
you run the sitting.
Answer:

**Q33.** (local Q6 — **conflicts with Q53/Q64; answer those first**) Replace the three disagreeing
cell-name patterns with one, `^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$` (3 to 40 characters, no edge
hyphen), citing `registry.schema.json#/$defs/appId`?
Recommended: **the single pattern yes, the bound no** — take Q53's 3 to 22. Two contracts frozen at
G1a require an appId-shaped cell, and today a two-character cell applies cleanly and then breaks
them; but the bucket and load-balancer names cap the cell at 22, and a landed test on trunk pins 22.
Answer:

**Q34.** (local Q7) Add `var.cell == "poc"` validation to the three `envs/poc/*` roots, so a second
cell arrives as its own `envs/<cell>/` directory?
Recommended: yes. The backend key and the apps root's remote-state key are the literal `poc/...`, so
changing the variable writes one cell's resources into another's state.
Answer:

**Q35.** (local Q8) Keep `spa-platform-<cell>-<account id>` as the managed-login prefix, accepting
that the account id is visible in the login hostname?
Recommended: yes. The prefix namespace is global per region, so a collision is the costlier failure,
and an account id is not a credential. Revisit with a random suffix only if G3's second apply cannot
reclaim the freed prefix.
Answer:

**Q36.** (local Q9) Accept that the CI plan role can read `/spa-platform/poc/test-users/*`, with the
trust policy rather than the permission as the control?
Recommended: yes. `aws_ssm_parameter` reads its value to plan, so a role without it cannot produce a
complete plan of either root.
Answer:

**Q37.** (local Q10) Change task 1.0's lock check to hold the lock at `terraform apply`'s interactive
confirmation prompt instead of racing `-auto-approve`?
Recommended: yes. The prompt holds the state lock indefinitely, which turns the exit test from a
timing race into a deterministic check.
Answer:

**Q38.** (local Q11) Close row 1.1 on four checks — apply, discovery `issuer` equality,
`authorization_endpoint` on the Cognito domain, and `admin-get-user alice` reporting `CONFIRMED` —
recorded with the date and pool id?
Recommended: yes. The fourth is the one provider behaviour no mock can test, and every unattended
login later depends on it.
Answer:

**Q39.** (local Q12) Accept in writing that 1.1 does not prove managed login, which task 1.8's manual
code-flow login first exercises?
Recommended: yes. With no app client, a request to the login page proves only that Cognito answers an
error.
Answer:

**Q40.** (local Q13) File a change request asking the integrator to add `terraform test` to CI for
every directory with a `tests/` directory?
Recommended: **no — strike it, the premise is false on this base.** I verified it:
`.github/workflows/ci.yml` already has the step "Test every root and module with tests under
`infra/`", which discovers every `*.tftest.hcl` under `infra/` — including
`infra/envs/poc/idp/tests/root.tftest.hcl` and `infra/modules/idp-cognito/tests/module.tftest.hcl` —
and runs it on the pinned Terraform 1.11.0. The ticket's grilling record says "CI never runs the 11
Terraform test runs"; that is wrong, and ticket 03's re-grill has it right. Correct the record.
Answer:

**Q41.** (local Q14) Leave the test-user parameter without `ignore_changes`, unlike task 1.8's client
secret, and say why in the module?
Recommended: yes. Terraform owns a test user's password end to end, so the pool and the parameter
must not drift; nothing rotates a test user at the provider.
Answer:

**Q42.** (local Q15) Add the identity root to task 1.9's plan allowlist, with
`aws_cognito_user_pool_client` explicitly absent?
Recommended: yes. "No app clients" is the boundary the plan's Terraform table rests on and no test
asserts it; 1.9 has not started, so it costs a sentence.
Answer:

**Q43.** (local Q16) Have the platform root read the identity root's state for
`Issuers:cognito-poc:Authority` and `:BrowserOrigin`, as the apps root already reads it for the pool
id — recorded as task 1.4's edit, not 1.1's?
Recommended: yes. Nothing carries the issuer to the task today, and a hand-copied value in a `tfvars`
is the mistake ADR-0014 spent a page avoiding.
Answer:

**Q44.** (local Q17) Require that the identity and apps plans and their JSON are piped to the policy
checker inside the CI job and never uploaded as artifacts?
Recommended: yes. A plan's `after` holds the values it will write, so those two plans carry the
test-user passwords and the client secrets; `-refresh=false` does not change that. Pairs with Q32.
Answer:

**Q45.** (local Q18) Give the identity allowlist its own test id, P-07, rather than widening P-01?
Recommended: yes. A test id names one behaviour, and `docs/plan/` is a frozen export, so either way
it is the integrator's edit.
Answer:

---

## Ticket 57 — CR-1.5 — Who the teardown role is, and two files `.gitignore` does not cover
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/57-CR-1.5-teardown-role-and-ignored-tfvars.md`

Status: ready-for-human. **The only change request still reading `open`**, and the answer is sitting
uncommitted in your working tree. Second pass, re-run on `origin/main`; the ticket says this list
supersedes the twelve you answered `yes` yesterday. Two of those answers change: **Q53 reverses** the
old Q4 and **Q47 changes** the old Q2. Unblocks the tail of 1.5, then 1.7, 6.7, 6.9 and P-05.

**Q46.** (local Q1 — new) Rebase this decision onto `origin/main` and commit it there, treating your
working tree as the draft?
Recommended: yes. `origin/main` still reads `Status: open` with no ADR-0014, so nothing is lost — but
ADR-0015 has landed, so the README row belongs **above** the 0015 row rather than appended where your
diff puts it.
Answer:

**Q47.** (local Q2 — CHANGED; you answered the old form yes) Add only the three new `.gitignore`
lines to `origin/main`'s file, keeping its `.scratch/*/log/` stanza?
Recommended: yes. Your copy predates the un-ignoring of the issue tracker: `git check-ignore` says
committing it as-is would re-ignore **72 tracked files**, including every ticket this run wrote. The
three patterns themselves (`*.tfvars`, `*.tfvars.json`, `backend.hcl`) still need no
`!*.tfvars.example`.
Answer:

**Q48.** (local Q3 — unchanged in substance) Keep the substance — option 1, the teardown role in
`infra/bootstrap`, its ARN derived from the cell name?
Recommended: yes. `skeleton-v1` confirms G1a closed, and `contracts/README.md` lets prose land
between gates while sending a narrowed schema to the next gate, so the `s3-layout.md` bullet is yours
to land today and option 2 is still a gate away.
Answer:

**Q49.** (local Q4 — unchanged; the defect still stands) Fix the teardown role's S3 scope before it
lands — the bucket ARN and `…/apps/*`, not the glob `spa-platform-<cell>-*`?
Recommended: yes. The bucket is named exactly `spa-platform-<cell>`, so the drafted glob matches no
bucket and the role would be denied every delete; and `ci-role.tf`, cited as the pattern, scopes S3
with `${name_prefix}*` and no hyphen. See also Q63, which fixes it from the other end.
Answer:

**Q50.** (local Q5 — new) Have ADR-0014's Plan ref name the boundary it amends?
Recommended: yes. `docs/plan/05` → Terraform boundaries gives bootstrap "Never contains: Anything
else", and `infra/bootstrap/README.md` cites that row. The plan is a frozen export, so the amendment
can only live in the ADR — and only if the ADR says so.
Answer:

**Q51.** (local Q6 — new) Replace ADR-0014's "the platform root does not read bootstrap's state" with
the real reason — bootstrap keeps local state?
Recommended: yes. The same plan page has the platform root reading the apps root's state, so there is
no such rule; the binding fact is that bootstrap's outputs exist on one workstation.
Answer:

**Q52.** (local Q7 — new) Correct `contracts/resource-tags.md`'s bootstrap exemption in the same
commit?
Recommended: yes. It names the exempt set as "two resources" in three places, and it is the file
workstream 6 is told to read rather than infer from Terraform — so it would answer "is the teardown
role exempt?" wrongly.
Answer:

**Q53.** (local Q8 — **REVERSES** the old Q4 you answered yes) Settle the cell name as 3–22,
`^[a-z0-9]([a-z0-9-]*[a-z0-9])?$`, written identically in both roots — **widening bootstrap** rather
than tightening `modules/cell`?
Recommended: yes, and this reverses last night's answer. A landed test on trunk pins 22 as the maximum
(`cell = "abcdefghijklmnopqrstuv"`), so tightening to bootstrap's 20 would fail CI; and the frozen
contract makes a cell name appId-shaped, so the two-character names both roots accept today already
produce a contract-invalid `outputs.json`. **This is the answer Q33 and Q64 should follow.**
Answer:

**Q54.** (local Q9 — new detail) Look the role up in the platform root (`data "aws_iam_role"`) so a
missing or mis-derived role fails the plan, accepting a `mock_data` block in the landed root test?
Recommended: yes. It turns "nothing can ever delete under `apps/`" into a plan error instead of a G3
discovery, `iam:GetRole` is already granted on this name, and the sibling idp and apps roots set the
precedent for the mock. Known limit: the mock satisfies the lookup, so the property holds against AWS
and not in `terraform test`.
Answer:

**Q55.** (local Q10 — CHANGED from the old Q7) Derive the ARN in the platform root and make any extra
principal visibly additional — rename the root variable to `additional_teardown_principal_arns`
(default `[]`) and `concat`?
Recommended: yes. A variable that *replaces* the derived ARN is a way to lock out the only principal
that can empty the bucket, and because the ARN sits in a policy condition the apply would not notice.
The module's list and its non-empty validation stay exactly as task 1.5 wrote them.
Answer:

**Q56.** (local Q11 — CHANGED from the old Q6) Make the teardown role's trust a required bootstrap
input with a `BoolIfExists` MFA condition, on its **own** variable rather than reusing
`additional_trusted_principal_arns`?
Recommended: yes. The two trust lists are different populations (read everything vs delete releases),
and `BoolIfExists` denies long-term credentials without MFA without locking out a federated session
that never sets the key. Worth adding to the ADR: a pasted session ARN fails loudly here, unlike in
the `Deny` condition.
Answer:

**Q57.** (local Q12 — unchanged) Record both retention consequences in ADR-0014 — every release kept
for the life of the cell, and no lifecycle or replication rule on the platform bucket?
Recommended: yes. Pruning is now break-glass only (fine at "under $0.50" of storage), and
`aws:PrincipalArn` is absent for S3's own expiration, so a lifecycle rule would delete "immutable"
releases while P-04 still passed.
Answer:

**Q58.** (local Q13 — unchanged) Keep the derivation normative for every consumer, with bootstrap's
`teardown_role_arn` output for humans only and no new field in `infra-outputs.schema.json`?
Recommended: yes. `tools/scripts/` holds only `certs.sh`, so no consumer exists yet and the
convention is free; if any consumer reads the output, option 2 is back through the side door.
Answer:

**Q59.** (local Q14 — new) Have the 6.7 runbook say that teardown switches principals — the teardown
role for `apps/`, the operator for everything else?
Recommended: yes. Plan 05 gives `teardown.sh` five jobs and the role may do exactly one, so "assume
the role, then run teardown" fails four steps in.
Answer:

**Q60.** (local Q15 — unchanged) Leave P-04 negative-only, with the positive half proved by 6.9's
tear-down-and-deploy-again?
Recommended: yes. Run as the teardown role, P-04 would observe the exception instead of the rule.
Answer:

**Q61.** (local Q16 — widened from the old Q11) List all seven workstream-1 follow-ups in the change
request? The role and its two policies, the derived default and renamed variable, the cell-name
validation in both roots, the root test's mock and variables, `infra/bootstrap/README.md`,
`infra/bootstrap/terraform.tfvars.example` ("not ignored by `.gitignore`", which your change makes
untrue), and the teardown comment in `infra/envs/poc/platform/terraform.tfvars.example`.
Recommended: yes, so task 1.5's follow-up is one change with nothing left to notice later.
Answer:

**Q62.** (local Q17 — unchanged) Land it all in one commit — ADR-0014, the README row,
`s3-layout.md`, `resource-tags.md`, the change request's Status and Decision, and the `.gitignore`
lines?
Recommended: yes. Splitting leaves the request reading `decided` in a commit whose `.gitignore` and
`resource-tags.md` do not yet match its Decision section.
Answer:

---

## Ticket 06 — task 1.5 — Private versioned bucket and image repositories
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/06-1.5-bucket.md`

Status: ready-for-human. Rounds 5–7 on the new base. Ticket-local Q1–Q15 are already settled by your
2026-10-14 `yes`; these eleven continue the numbering. This is ADR-0014's implementation, so it
unblocks 1.4 and then 1.6b, 1.7, 1.9 and 6.9.

**Q63.** (local Q16) Make the bucket `spa-platform-<cell>-<account id>` (the Q94 you already approved)
so that ADR-0014's `spa-platform-<cell>-*` scope matches it, with the role's list actions on that
bucket ARN and its deletes on `.../apps/*`?
Recommended: yes. As ADR-0014 stands the role's S3 scope matches **no bucket** — the bucket is
`spa-platform-poc` and `spa-platform-poc-*` does not match it — so the exception would be empty,
teardown could not empty `apps/`, and P-05 could never pass, with nothing failing at apply time
because the ARN sits in a condition rather than a `Principal`. Declining means scoping the role to
the exact bucket name instead (Q49).
Answer:

**Q64.** (local Q17 — see Q53) Validate the cell name in `infra/bootstrap` and `modules/cell` with
one pattern, `^[a-z0-9][a-z0-9-]{1,20}[a-z0-9]$`, 3 to 22 characters, no leading or trailing hyphen,
with a comment naming where each bound comes from?
Recommended: yes — this is the same answer as Q53, expressed as the edit. Today `pc` is legal in both
roots and illegal in `infra-outputs.schema.json`, and `poc-` is legal in bootstrap and would make the
invalid bucket name `spa-platform-poc-`.
Answer:

**Q65.** (local Q18) Have the platform root look the role up — `data "aws_iam_role" "teardown"` on
`spa-platform-<cell>-teardown` — and pass its ARN, instead of assembling the ARN from
`aws_caller_identity` and `aws_partition`?
Recommended: yes; same decision as Q54, worded as an amendment to ADR-0014 §2 — the root derives the
**name**, AWS gives the ARN. The platform root already cannot plan before bootstrap is applied, so
the lookup adds no ordering constraint.
Answer:

**Q66.** (local Q19) Should `infra/bootstrap` take `teardown_trusted_principal_arns` with no default,
guarded by a precondition that fails the apply when nobody is named, the way `trust_is_configured`
already guards the CI role?
Recommended: yes. The humans cannot be derived and `*.tfvars` is now ignored, so the only safe shape
is a loud failure; defaulting it to the CI role's trusted principals would hand them a delete power
they have no reason to hold.
Answer:

**Q67.** (local Q20) Correct ADR-0014's Consequences sentence "`spactl`'s `If-None-Match: *` refuses
the second write anyway"?
Recommended: yes, fix it and quote the guide. It is wrong: S3's documented behaviour is that a
conditional write **succeeds** when the current version is a delete marker, so a delete followed by a
conditional write does replace a release. What actually prevents it is that no principal holds both
the delete and the write. That sentence is what a future reader would cite when asked to give the
teardown role a write "to repair a prefix".
Answer:

**Q68.** (local Q21) Put the plan-level proof in new `run` blocks in
`infra/envs/poc/platform/tests/root.tftest.hcl` rather than a new
`infra/modules/cell/tests/storage.tftest.hcl`?
Recommended: yes. Mock blocks are file-scoped, so a second file would duplicate this module's zones,
certificate, load-balancer and policy-document mocks — and the policy-document mock is load-bearing:
without it the plan fails on invalid JSON, and with it the rendered `json` is the mock, so assertions
must read the configured `statement` blocks. Verified on Terraform 1.11.0, CI's pin.
Answer:

**Q69.** (local Q22) Shrink the change request to two items — the four illustrative
`spa-platform-poc` values in the two frozen example files, and ADR-0009's Object Lock row — in one
`contracts/changes/1.5a-bucket-name-and-object-lock.md`?
Recommended: yes. The CI `terraform test` step your earlier Q13 asked for has already landed, and
ADR-0014's own corrections are edits to an unlanded draft you hold anyway.
Answer:

**Q70.** (local Q23) Delete `bucket_key_enabled = true` from **both** buckets rather than only the
platform one?
Recommended: yes. The state bucket carries the same inert line under the same `AES256`; Bucket Keys
only reduce SSE-KMS request costs, so it does nothing in either place.
Answer:

**Q71.** (local Q24) Keep exactly ADR-0014's four permissions, with the constraint that `teardown.sh`
empties `apps/` using `s3api list-object-versions` and `s3api delete-objects` recorded in
`infra/bootstrap/README.md` and `modules/cell/README.md` for task 1.7?
Recommended: yes. The high-level `aws s3 rm` may need `GetBucketLocation` and cannot remove
noncurrent versions or delete markers at all, so a script written that way fails at teardown.
Answer:

**Q72.** (local Q25) Keep the platform bucket's `force_destroy` off, with a comment saying why it is
not an oversight?
Recommended: yes. `force_destroy` deletes the objects as the operator, whom the policy denies under
`apps/`, so it would turn a destroy into `AccessDenied`.
Answer:

**Q73.** (local Q26) Rebase the five uncommitted ADR-0014 paths onto `origin/main` and commit them —
keeping the number 0014, with its README row placed above 0015's — before the 1.5 lane forks?
Recommended: yes. Upstream runs 0001–0013 then 0015, so there is a gap where 0014 belongs;
renumbering would orphan the references already written in `contracts/s3-layout.md`, the change
request and this ticket. Same as Q46, from 1.5's side.
Answer:

---

## Ticket 03 — task 1.2 — `modules/cell` network
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/03-1.2-cell-network.md`

Status: ready-for-human. Re-grilled on `origin/main`; the ticket says this list supersedes the
2026-10-14 one you answered `yes`. What changed: last night's Q8 (ask for a `terraform test` CI step)
is **moot — that step landed**; Q74 and Q82 are re-asked because their premises were wrong, and Q80
and Q81 are re-asked against test machinery that now exists.

**Q74.** (local Q1 — premise corrected) Close 1.2 when assertions proving its done-when are green in
CI, rather than holding it open for the AWS apply?
Recommended: yes. The row's done-when names no test id and is a statement about a plan, which a
mock-provider plan satisfies; the ids that need an account (P-01, P-06) belong to 1.9 and G3.
Answer:

**Q75.** (local Q2 — unchanged) Read "no other ingress rule" as fencing ingress only, with egress
bounded by named destinations and ports instead of by a count?
Recommended: yes. Egress is where necessary additions appear (DNS now, a VPC endpoint later), and a
count would make a correct change look like a regression.
Answer:

**Q76.** (local Q3 — widened) Keep deriving the two zones but add an optional `availability_zones`
variable through the root, record the applied pair in `terraform.tfvars`, and **cite or delete** the
"stable across applies" claim?
Recommended: yes. The subnets are keyed by zone name, so a changed pair recreates both and everything
hanging off them — and an uncited vendor claim is exactly what `CODING_STANDARDS.md` forbids.
Answer:

**Q77.** (local Q4 — unchanged) Adopt the VPC's default security group with zero rules?
Recommended: yes. The account has three groups either way; managing it is what makes "no other
ingress rule" true of the account and not only of the file.
Answer:

**Q78.** (local Q5 — new) Keep `map_public_ip_on_launch = true` but rewrite its comment to say the
task's public address comes from **task 1.4's service configuration**, with the AWS document cited?
Recommended: yes. The attribute does not apply to an ECS-created ENI, and the comment as written
invites 1.4 to skip the one setting the no-NAT design rests on.
Answer:

**Q79.** (local Q6 — unchanged) Leave `ForwardedHeaders:TrustedNetworks` as the VPC CIDR rather than
narrowing it to the two subnet CIDRs?
Recommended: yes. Only the ALB's interfaces and the task will ever hold an address in this VPC, and
narrowing would re-add an output `8773fa2` deliberately deleted.
Answer:

**Q80.** (local Q7 — re-asked against the landed test machinery) Put 1.2's assertions in the existing
`infra/envs/poc/platform/tests/root.tftest.hcl` rather than a new `infra/modules/cell/tests/`?
Recommended: yes. That file already mocks the provider and the zones and already runs against
`modules/cell`; a second test root means a second mock block and a directory CI inits only to leave a
lock file it ignores.
Answer:

**Q81.** (local Q8 — re-asked) Prove absence with two assertions — no forbidden resource type
anywhere in the module, and the security-group rule resources are exactly the agreed set — rather
than a whole-module allowlist?
Recommended: yes. The module now also holds 1.3's ALB and 1.5's storage and will hold 1.4's service,
so a full allowlist fails on every later task; type-and-count allowlists are 1.9's row.
(`aws_dynamodb_table` goes in the forbidden list as a Phase 1 tripwire, since `Session.Store` admits
`dynamodb` in Phase 2.)
Answer:

**Q82.** (local Q9 — premise corrected) Add TCP 53 and UDP 53 egress from the task group to the VPC
CIDR only, as insurance against the Amazon-DNS security-group exemption not covering an ECS-created
ENI?
Recommended: yes. Two VPC-scoped rules grant nothing outbound and replace an uncited vendor
assumption whose failure mode is a task that never starts.
Answer:

**Q83.** (local Q10 — new) Extend `task_https_out`'s comment to name the tunnel as its second
purpose, without adding a second rule?
Recommended: yes. That rule is what lets the tunnel's hop reach the internal ALB on 443, so narrowing
it later would break `verify-tunnel.sh` for a reason nothing names.
Answer:

**Q84.** (local Q11 — new) Prove P-06's first sentence offline and say so in the merge body, while the
`P-06` id itself stays outstanding for G3?
Recommended: yes. `04-tests.md` puts P-06 in the deployed tier, and an id two tiers both claim is an
id nobody owns.
Answer:

**Q85.** (local Q12 — new) Correct the README's claim that P-01 fails on "any other ingress rule", and
add the non-goals (no IPv6, no flow logs, no NACL changes, no VPC endpoints, no session-store table
before Phase 2) beside the no-NAT argument?
Recommended: yes. The ingress assertion is 1.9's, not P-01's, and a mis-attributed id stops the next
reader checking whether anything proves it.
Answer:

**Q86.** (local Q13 — replaces last night's change request) Skip the change request for 1.9's
wording, and record the expected three groups and six rules in the module README and the merge body's
`Outstanding:` line instead?
Recommended: yes. 1.9 is workstream 1's own task, so only the row's prose is the integrator's, and it
stays substantially true.
Answer:

**Q87.** (local Q14 — unchanged) Propose an ADR to the integrator for "no NAT: public subnets, a
public task address, and the load balancer internal with the tunnel as its only path"?
Recommended: yes. Reversing it re-addresses the VPC and recreates everything hanging off the subnets,
the public subnet is genuinely surprising here, and the alternatives are priced in the plan ($33/month
NAT, or about $7 per endpoint per zone).
Answer:

**Q88.** (local Q15 — widened) Adopt `ALB group`, `Task group`, `Gateway port`, `Tunnel` and `Cell
network` into `GLOSSARY.md` under a new network section, rather than renaming the Terraform
resources?
Recommended: yes. "ALB group", "task group" and 1.3's real "target group" all collide, the row uses
two of those senses in one sentence, and renaming would churn 1.3's landed test and 1.4's written
service.
Answer:

---

## Ticket 04 — task 1.3 — Internal ALB with the imported certificate
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/04-1.3-alb.md`

Status: ready-for-human. **First grilling, so all 16 are new to you.** The ticket was stale twice
over: 1.3's ALB is already written, reviewed and merged on `origin/main`, and 1.2 is an ancestor of
even your checkout's HEAD — the only acceptance line left is the apply. Three rounds, frontier empty.

**Q89.** (local Q1) Correct this ticket's header to "Blocked by: none" and keep the row open on the
apply alone?
Recommended: yes. 1.2 is merged and 1.3's own Terraform is on `origin/main` with 19 offline
assertions in CI, so "blocked" misdirects the next session.
Answer:

**Q90.** (local Q2) Never apply 1.3 on its own, and let the first platform-root apply — after 1.4 —
be its proof?
Recommended: yes. The root is the apply unit, and alone the ALB has no target, no reachable listener
and a $16-a-month bill for proving only that the certificate lookup resolves.
Answer:

**Q91.** (local Q3) **The find that matters most here.** Add an `idle_timeout_seconds` variable,
default 60, and name it the platform's deployed request ceiling?
Recommended: yes. Change request 2.1 deferred this exact number to task 1.3, and 1.3 left it at a
default nobody chose. Stating it is what lets ticket 49 (2.1b) bound `timeoutSeconds` and what makes
the hermetic and deployed ceilings comparable. Pairs with Q117.
Answer:

**Q92.** (local Q4) Pin `deregistration_delay = 30` and the health check's four timings (interval 10,
timeout 5, healthy 2, unhealthy 2) as variables with those defaults?
Recommended: yes. `deploy.sh`, `rollback.sh`, the circuit breaker and 6.9 are all timed against these
numbers, and AWS's 300-second drain makes every deployment of a one-task service slower than it needs
to be.
Answer:

**Q93.** (local Q5) Keep the load balancer without access logs in Phase 1, and note that threat T13's
"and ALB access logs" is therefore not live?
Recommended: yes. P2-7 already owns access logs, and enabling them now would start writing
authorization codes to S3 for the first time.
Answer:

**Q94.** (local Q6) Accept that TLS terminates at the listener and the hop to the task is plaintext
inside the cell, and record the reason once?
Recommended: yes. The plan's trust-boundary table already implies it by naming TLS on one hop and not
the other, but the reason — the task group admits nothing but the ALB — is written nowhere a security
reader would look.
Answer:

**Q95.** (local Q7) Accept `most_recent = true` on the tagged certificate lookup rather than failing
a plan on two matches?
Recommended: yes. `certs.sh` reimports in place and is the only importer, so the only way to two
matches is a hand import, and a loud plan failure would also break a routine reimport.
Answer:

**Q96.** (local Q8) Record the apply chain — `certs.sh issue|trust|import`, bootstrap, idp, apps,
platform — as this row's precondition, and ask task 1.9 to state the certificate as a precondition of
its plan-policy step?
Recommended: yes. The platform root cannot even *plan* without the certificate in ACM, and nothing
here has ever been applied, so CI's first real plan would fail on the lookup.
Answer:

**Q97.** (local Q9) Adopt five glossary terms from this row (Ingress, Certificate domain, Readiness,
Verified tunnel, Timeout ceiling) rather than renaming anything in the module?
Recommended: yes. "Ingress", "the health-check path" and "ceiling" each now mean three or four
different things in one row, and ticket 03's five proposed terms cover none of them.
Answer:

**Q98.** (local Q10) Raise one change request,
`contracts/changes/1.3-alb-idle-timeout-is-the-deployed-ceiling.md`, carrying both the
`upstream-contract.md` line and the number 2.1b's validator should refuse above?
Recommended: yes. `contracts/` is the integrator's, it is one decision with two edits, and change
request 2.1 left the hole in a single sentence that this closes.
Answer:

**Q99.** (local Q11) Keep the pinned timings as module variables and **not** as new `outputs.json`
fields?
Recommended: yes. `infra-outputs.schema.json` is `additionalProperties: false` and the root's own
`outputs.tf` warns that an extra output is a trap for task 1.7's assembly; the scripts need a wait
budget, not the exact number.
Answer:

**Q100.** (local Q12) Write the TLS-termination reason as a new ADR rather than as a consequence line
on ADR-0010?
Recommended: yes. ADR-0010 is about onboarding never touching ingress; transport is a different
decision. Note the number: the ticket says 0016, which Q10 also claims for G0's findings — one of
them becomes 0017.
Answer:

**Q101.** (local Q13) Leave 1.3 reported as part-landed until the platform root applies, with the
Outstanding line naming that apply?
Recommended: yes. "Landed is not done" is the roadmap's own rule, and 1.3's done-when is an apply it
shares with 1.2, 1.5 and 1.4.
Answer:

**Q102.** (local Q14) Keep the `certificate_domain` variable name and let a glossary entry draw the
line between it and Host?
Recommended: yes. "Domain" is ACM's own word for `*.spa-poc.test`, and what the vocabulary needs is
the boundary between the suffix and the authority a browser sends.
Answer:

**Q103.** (local Q15) Assert the deployed ceiling's behaviour in 1.6b and 6.9 without waiting for a
new test id, and name the missing id in the change request?
Recommended: yes. `docs/plan/04-tests.md` is a frozen export workstream 1 cannot add to, and a check
that waits for a number never gets written.
Answer:

**Q104.** (local Q16) Keep roadmap MR-2 whole, noting only that its deregistration half is now a
pinned value?
Recommended: yes. A number is not a specification — what the gateway does with an in-flight request
at SIGTERM is still unwritten, and that is the half S8 is held to.
Answer:

---

## Ticket 53 — CR-6.4a — The stage-1 405 is logged but neither counted nor on a request line
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/53-stage-1-405-is-neither-counted-nor-on-a-request-line.md`

Status: ready-for-human. Re-run on trunk; the ticket says these twelve **supersede** the twelve you
answered `yes` yesterday. The mechanism verdict survived the rebase but the facts under it did not:
stage 2 now ships the feature and both counters, the sweep is ~100 mentions in 38 files across four
owners rather than nine files, and the first run's option 1 is defective. Nothing is blocked by this;
it shapes task 6.3, so decide it at G1b.

**Q105.** (local Q13) Hold `contracts/request-gate.md`'s guarantee and fix the pipeline, with option
3 as the fallback if you refuse Q109's sweep?
Recommended: yes. `RuleIds.All` exists so workstream 6's negative suite can loop all twelve ids;
holding the guarantee keeps it a loop, narrowing makes it a loop plus a permanent exception.
Answer:

**Q106.** (local Q14) **The new, cheaper answer: swap the first two stages** — telemetry outermost,
forwarded headers and the method allowlist second — rather than moving the allowlist later or
inventing a stage 0?
Recommended: yes. This is a fifth option the ticket's original four did not list, and it is cheaper
than all of them. `Health.TryAnswerAsync` answers `/healthz` on any method and stage 6 is unreachable
on an unknown host, so moving the allowlist into the gate would make `TRACE /healthz` a 200 and break
N-20 — which is why the first run's option 1 is defective.
Answer:

**Q107.** (local Q15) Bound "rejection" to what the application answers, and say so in
`contracts/request-gate.md`?
Recommended: yes. Kestrel's oversized-header 431 already has no rule id and is already in
`contracts/http-contract.md` and N-20; leaving that unnamed is what made this hole look unique.
Answer:

**Q108.** (local Q16) Name the method allowlist in the pipeline table's row for
`UsePlatformForwardedHeaders`?
Recommended: yes. No contract says today which stage it runs in, and that silence produced change
request 6.4's wrong writer table and this ticket.
Answer:

**Q109.** (local Q17) Pay the renumbering cost by replacing stage **numbers** with entry-point
**names** outside `contracts/pipeline.md` — the integrator's ten files in the decision commit, one
follow-up ticket each for ws2, ws4 and ws6 — and add that rule to `CODING_STANDARDS.md`?
Recommended: yes. It is ~100 mentions in 38 files across four owners, not the nine the first run
named; a plain swap leaves "stage 1" meaning two different stages in the tree agents read, and rots
again on the next reorder.
Answer:

**Q110.** (local Q18) Record it as **ADR-0016**, "Telemetry is the outermost stage", with one pointer
line in change request 6.4 and no new change-request file?
Recommended: yes on the record, but **the number collides** with Q10 and Q100. `docs/plan/02-contracts.md`
numbers both rows and is a frozen export, so an ADR is required and not just advisable.
Answer:

**Q111.** (local Q21 — read with Q116) Land it as one integrator commit (pipeline, request-gate,
telemetry, `Program.cs`, the two abstractions and testing comments, `CODING_STANDARDS.md`, the ADR)
plus new ws4, ws2 and ws6 tickets and one line on task 6.3 — with the ws4 ticket sequenced **after**
ticket 55?
Recommended: yes — and note ticket 55 **landed tonight** (`a09a79d`), so that sequencing constraint
is already satisfied. `MethodAllowlist.cs`'s `const string Rule` now reads `RuleIds.GateMethod`.
Answer:

**Q112.** (local Q19) State in `contracts/telemetry.md` that the request line and span carry no
forwarded-header value, and prove it by asserting the line's field names are exactly the contract's
set?
Recommended: yes. That property is the only thing making the swap safe, and the first run's
value-based test would have passed even if someone logged `RemoteIpAddress`, because workstream 6's
host has no forwarded-headers stage.
Answer:

**Q113.** (local Q20) Extend N-20 in both tiers — the feature write asserted in ws4's
`RuleIdTelemetryTests.cs`, the composed line and two counters as an acceptance line on task 6.3 —
minting no test id and no rule id?
Recommended: yes. That file already asserts every other rule id against the settable double, and task
6.3 owns N-20's composed half.
Answer:

**Q114.** (local Q22) Replace the "known gap" text in all four places it lives —
`contracts/telemetry.md`, `IRequestTelemetryFeature.cs`, `RequestTelemetry.cs` and ws4's
`RuleIdTelemetryTests.cs` — rather than only the two the acceptance box names?
Recommended: yes. The first two are the integrator's and the last two are ws6's and ws4's, so a
decision that reaches only the contract leaves two shipped files contradicting it.
Answer:

**Q115.** (local Q23) Add `Stage`, `Rejection`, `Rule id` and `Request line` to `GLOSSARY.md`, with
`Stage` saying that only the pipeline table's numbers are normative?
Recommended: yes. Without that clause Q109's sweep is a style preference rather than a rule, and
"rule id" is used in four files and defined in none.
Answer:

**Q116.** (local Q24) Decide it tonight on paper and land it at G1b alongside I-6?
Recommended: yes. It no longer unblocks ticket 50, which landed — it is task 6.3 that should be
written against the final order, and `contracts/README.md` holds a changed stage for the next gate.
Answer:

---

## Ticket 49 — task 2.1b — The validator refuses a `timeoutSeconds` the proxy cannot apply
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/49-2.1b-validator-refuses-unapplicable-timeout.md`

Status: blocked on **MR-1**, which is an integrator decision with no ticket of its own. This is the
real head of that chain; your 2026-10-14 `yes` (republish the constant from `Platform.Abstractions`)
was overtaken by ADR-0015 decision 2, which keeps it in `Platform.Proxy`.

**Q117.** Decide gap **MR-1** — which registry documents a given gateway build accepts — so 2.1b can
land? The decided shape is "the contract states the bound and the proxy proves it":
`contracts/registry.schema.json` gives `timeoutSeconds` a `maximum`, the validator refuses anything
above it under the existing `integer-out-of-range` id, and task 4.1a's test asserts YARP can still
apply the contracted bound. The blocker is that `registry.schema.json:162` says "the plan states no
upper bound", so adding a `maximum` narrows a frozen schema and raises the `schemaVersion` question
MR-1 leaves unanswered.
Recommended: decide MR-1 at G1b with a `schemaVersion` rule, then land 2.1b against the number Q91
pins. Meanwhile the validator keeps `MaximumTimeoutSeconds = 922,337,203,685`, wrong by a factor of
~430,000 but wrong in the safe direction. Note the two numbers are different things: Q91's 60-second
ALB idle timeout is the deployed ceiling; `ProxyTimeout.MaximumTimeoutSeconds = 2,147,483` is what
YARP can hold.
Answer:

---

## Ticket 47 — task 2.1a — Link the contract schemas
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/47-2.1a-link-contract-schemas.md`

Status: resolved and landed (`9be7916`). One carried-over item: you answered yes to this yesterday as
Q145, but **the change request file it refers to exists only on the archived branch**, so nothing on
this base carries it.

**Q118.** `src/Gateway/Dockerfile` lines 29–34 still say `Platform.Registry` "embeds its own copies
under `Schemas/` today, so nothing reads these yet", which task 2.1a's deletion of `Schemas/` makes
false. Make the two-line edit directly, rather than re-filing
`contracts/changes/2.1a-dockerfile-contracts-copy-comment.md` on this base?
Recommended: yes, edit it directly. `src/Gateway` is yours; the change request existed only because
it is off limits to workstream 2, and the file it was written in is not reachable from `origin/main`.
Answer:

---

## Already settled — not re-asked

- **Ticket 43 (CR-5.5).** You answered its twelve questions `yes` (2026-10-14 Q130–Q141), and the
  collect pass confirms the change request is **decided** on this base. Its ticket Status line still
  reads `ready-for-human` and is stale.
- **Tickets 44 (CR-1.1), 45 (CR-2.1), 46 (CR-6.4).** All decided on this base.
- **Tickets 50 (6.4a), 51 (2.3a), 52 (4.3a), 54 (2.3b), 55 (4.3b).** Landed. 54 and 55 landed
  tonight; their 2026-10-14 questions were answered and their implementations follow those answers.
  The one thing to know: both implementers kept the rule-id literals in the **test** projects as
  literals, which is the Q144/Q7 reading you approved.
- **Ticket 56 (3.5a).** Blocked behind `contracts-v1`; nothing asked of you before the tag.
- **Tickets 20 (3.3), 22 (3.5), 26 (3.9).** Carry the integrator's change-request-6.4 note on
  computing `session_ref`; nothing asked.

---

# Added after 08:35 — the three lanes dispatched in the second half

Q1–Q118 above were written at 08:35, when `2.3b` and `4.3b` had landed. Three more lanes ran after
that and all three are merged: **1.4a** (ticket 58), **6.2a** (ticket 59) and **6.3a** (ticket 60).
None of these questions blocks anything — every one of the three is landed and green. They are here
because each lane either decided something for you or found something you should know. Q119–Q126
are the ones to read before the AWS sitting; the rest can wait for the PR.

---

## Ticket 58 — task 1.4a — The cell's ECS resources, written and planned
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/58-1.4a-cell-ecs-terraform.md`

Status: resolved, landed as `04c7489`. Ten files, all `infra/`. 28 new plan-time assertions, 30
mutations all caught, three review findings fixed. **Q119 and Q120 are the two the sitting needs.**

**Q119.** The task role was left at exactly the three grants plan row 1.4 enumerates — `s3:GetObject`
on `registry/*` and `apps/*`, `ssm:GetParameter`/`GetParameters` on `/spa-platform/{cell}/apps/*`,
and the four `ssmmessages` actions — with **no `kms:Decrypt`**. The client secrets are `SecureString`
under the AWS-managed key, and the reasoning in `iam.tf` is that the AWS-managed key's own policy
admits the account's principals when the call arrives through Systems Manager. That was not verified
against the vendor's documentation. Leave the role as it is and add `kms:Decrypt` only if the first
snapshot returns `AccessDenied`?
Recommended: yes. A missing grant is one statement to add and shows up loudly on the first read; a
grant added against a guess is a widening nobody will revisit. It is a liveness question, not a
security one.
Answer:

**Q120.** The platform root now has **three variables with no default**: `teardown_principal_arns`
(task 1.5's), and `gateway_image` and `issuers` (this sub-row's). The first apply needs a sample
image reference for one and the identity root's `issuer` and `cognito_domain` outputs for the other.
Bring both to the sitting, and keep `gateway_image` required rather than giving it a tag-naming
default?
Recommended: yes to both. One of the 30 mutations was exactly "`gateway_image` given a tag-naming
default", and it was caught deliberately: `docs/plan/05-infrastructure-and-cost.md` deploys by
digest, and a default tag is how a cell quietly runs an image nobody chose.
Answer:

**Q121.** The module's `cell` variable still has no default and the root still passes `poc`. Once you
settle the legal cell name (Q53 governs, with Q33 and Q64 following it), leave it that way?
Recommended: yes. The module stays reusable and the root is the one place a cell is named; the
pattern belongs in the variable's `validation`, which Q53's answer decides.
Answer:

**Q122.** The ticket asked to be re-read if your ADR-0014 changes who may delete under `apps/`. The
lane reports nothing has to follow: `ecs.tf` and `iam.tf` name no teardown role, and the task role
holds no `s3:DeleteObject` — the teardown role governs deletes, the task role only reads. Confirm no
re-read is needed after you land ADR-0014?
Recommended: yes, confirm. Worth one look at the ADR's final S3 scope all the same, because Q63–Q73
found three defects in it as drafted.
Answer:

**Q123.** `ForwardedHeaders:TrustedNetworks` was fixed in review from the whole VPC CIDR to **one
entry per subnet the load balancer sits in**, because `contracts/platform-config.schema.json` calls
the value "The ALB subnets, in CIDR form" and only two subnets of that range are allocated. The old
form trusted `X-Forwarded-For` from every unallocated address in the cell. Confirm the narrower
reading is the right one, and that this is what N-09 means?
Recommended: yes. The acceptance box ("names this cell's network only") was literally satisfied by
the wider form, which is why this came out of the Spec axis rather than as a plain defect — the
contract is narrower than the box, and the contract wins.
Answer:

**Q124.** Also fixed in review: `ASPNETCORE_HTTP_PORTS` is now set from `var.gateway_port` in the
gateway container's environment, in its own local and **deliberately outside**
`platform_configuration`, on the reading that the configuration contract's own `$comment` names
`ASPNETCORE_*` among the settings that are not the platform's. Before this, the variable drove the
port mapping and the registered target while `src/Gateway/Dockerfile` fixed the port at 8080, so any
value but the default sent the load balancer to a dead port. Confirm that placement?
Recommended: yes. The alternative — adding it to `platform_configuration` — would put a framework
setting inside the platform's own contracted set.
Answer:

**Q125.** Operational values with no contract behind them, chosen by the lane: Container Insights
explicitly **disabled** (the cost model has no line for it), `health_check_grace_period_seconds = 60`
(because `/readyz` is 200 only after the first registry poll), `log_retention_days` default 14,
`desired_count = 1`, `platform_version = LATEST`. Ratify as written?
Recommended: yes, and revisit the grace period after the first apply — it is the one of the five that
a slow first poll turns into a rollback loop.
Answer:

**Q126.** Two more shapes the lane chose: the **execution role's policy is written out** rather than
attaching the managed `AmazonECSTaskExecutionRolePolicy` (which grants ECR and logs on `"*"`), and
**both containers are `essential = true`**, so a mock API that cannot start fails the deployment and
the circuit breaker rolls it back rather than leaving `/api` returning 502. Keep both?
Recommended: yes to both. The second is the one with a test consequence: it keeps F-08 from reading
as a gateway fault when the fault is the mock API's.
Answer:

**Q127.** The platform root reads the two mock API client ids from the **apps root's remote state**,
per `docs/plan/05-infrastructure-and-cost.md` → Terraform boundaries, rather than taking them as
hand-copied variables; app 3 is deliberately not selected, and the root test's override carries a
third app so that the exclusion is tested. Confirm?
Recommended: yes. This is the boundary the plan already states; the alternative is two secrets
copied by hand into a tfvars file your `.gitignore` now ignores.
Answer:

---

## Ticket 59 — task 6.2a — The functional rows that need no session (F-06, F-11)
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/59-6.2a-functional-rows-no-session.md`

Status: resolved, landed as `6eb424f`. Seven new files in `tests/Gateway.IntegrationTests/Functional/`,
**20 `[Fact]` methods** (the ticket's prose says 16 — see Q133), 1115 tests green on the lane, 1116 on
the merge. The ticket says "nothing blocking", and that holds.

**Q128.** The request log line carries a **`category`** field that is not among the 13 names in
`contracts/telemetry.md`'s "Log fields" row, so the lane's field-list assertion compares the line
*minus* `category` against the contract and reads that row as the required set rather than the closed
one. Add `category` to that row? It is your path.
Recommended: yes, add it. Nothing is wrong today and the test stays green either way; the row simply
does not describe the line it governs, and the next lane to read it will ask the same question.
Answer:

**Q129.** Because the stage-8 shell gate is absent (task 3.9, blocked behind `contracts-v1`), these
rows are proven against a gateway that serves assets and `/api` **with no session**. Three class
summaries (`PerAppHeadersTests`, `AssetFallbackTests`, `ApiTelemetryTests`) name stage 8, ADR-0008 and
which assertions change legitimately when 3.9 lands. Confirm that when those status codes change, the
tests are re-read rather than treated as a regression?
Recommended: yes. This is the same caveat ticket 60 carries; answering it once covers both.
Answer:

**Q130.** `CapturedTelemetry.cs` hand-writes its span and metric exporters. The review found the
stated reason wrong — `OpenTelemetry.Exporter.InMemory` **is** pinned in `Directory.Packages.props`
at 1.19.1 and a workstream may add a versionless reference to its own csproj, which
`tests/Platform.Telemetry.Tests.csproj` already does — but the real reason stands: the pinned
exporter appends without synchronising readers, which is why task 6.4 wrote its own too. Keep the
hand-written exporters, with the comment now giving the true reason?
Recommended: yes, keep them. Left as originally written, the comment would have sent a later session
to file a change request for a package that is already available.
Answer:

**Q131.** The release-switch test derives the second release's bytes **from the app's own fixture
release** (a release comment inserted after the doctype), because `fixtures/` is workstream 5's path;
the helper asserts the doctype was found and that the bytes differ, so an edit that stopped applying
fails loudly. Leave it, or ask workstream 5 for a second release fixture?
Recommended: leave it. A fixture request would cross an owner boundary for something the test can
derive and check.
Answer:

**Q132.** Metric and span **counts are never asserted**, because an OpenTelemetry provider subscribes
to `ActivitySource` and `Meter` process-wide and so sees every harness alive in a parallel run; every
request-level assertion is keyed instead by the trace id on that harness's own request log line.
Confirm this constraint, which every later telemetry row inherits?
Recommended: yes. The alternative is serialising the suite, which costs more than the assertion is
worth.
Answer:

**Q133.** The ticket's own prose understates the suite: "seven files, 16 tests" and "all sixteen
clauses were green on arrival", where **20** `[Fact]` methods landed (6 + 5 + 4 + 4 + 1), which the
comments' own running totals (35 → 40 → 44 → 51) confirm. The correction was recorded in the ticket's
Review section rather than by rewriting the implementer's prose, so the correction is traceable.
Leave it that way?
Recommended: yes. The mutation table itself is consistent; only the summary count was wrong.
Answer:

**Q134.** `docs/gaps.md` was deliberately **not** created: `.github/steering.py` lists it under
`PLANNED` and fails the moment the path appears, so the lane left it to task 6.8. Confirm?
Recommended: yes, confirm. Worth knowing that the steering check is doing its job — a lane wanted
that file and the check stopped it.
Answer:

---

## Ticket 60 — task 6.3a — The negative rows that need no session
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/60-6.3a-negative-rows-no-session.md`

Status: resolved but recorded **partial**, landed as `7efaeff` under the subject "Merge **part of**
task 6.3a". 20 new files in `tests/Gateway.SecurityTests`, 1205 tests green, 10 `src/` mutations all
measured and reverted. **Q135 is the ticket's own question and the one that changes the tree.**

**Q135.** The ticket asks it directly: **N-07, N-10 and N-12 landed partial**, each with the unproven
clause named in the test that proves the rest. Recording them whole would overstate coverage. Keep
them partial, or leave them out entirely until G4?
Recommended: keep them partial. Each proves a real refusal today — N-07's request gate runs at stage
6, before authentication — and the named clause is what a reader needs in order not to trust the row
too far. Leaving them out loses proof that exists.
Answer:

**Q136.** **N-11 whole, and N-12's `Set-Cookie` clause, are not provable today, and the carve's own
spec was wrong that they were.** Measured, not assumed: on this branch an `/api` call's `Cookie`,
`Authorization`, `X-CSRF-TOKEN`, `Via`, `X-Real-IP` and `X-Original-URL` all reach the upstream, and
an upstream's `Set-Cookie` reaches the browser's cookie jar. That stripping is plan row **4.2**,
whose needs are "4.1, G1b", so it has not landed — workstream 6 owns the proof, not the stage. Two
acceptance boxes on the ticket are therefore unticked. Should a line go on ticket 27 (task 4.2) and
on row 4.2's done-when naming N-11 and N-12 as the rows that prove it, so the proof is not forgotten
when 4.2 lands?
Recommended: yes. `docs/plan/` is yours, so this is your edit or a change request, not the lane's.
Without it the two rows are recorded nowhere except in a ticket Comments section.
Answer:

**Q137.** The lane filed **no change request** for Q136: nothing of the integrator's is needed and no
contract is contradicted — the carve's spec was simply wrong, and the spec is the run's own artefact.
Confirm that is the right call rather than a change request against `docs/plan/`?
Recommended: yes, confirm. Writing the two rows red would make them guards against 4.2 landing;
writing them to today's behaviour would mean deleting them when it lands. Absent is the honest third
option.
Answer:

**Q138.** `UpstreamResponseTests` needs `Policy:AllowedUpstreams:2`, which assumes the task 6.1
harness fills indices 0 and 1 when `EchoApi` is null — "the one place this suite knows something
about the harness's internals". A mistake there surfaces as a rejected registry document and a
failing test, not a silent pass. Should task 6.1's harness document (or expose) the index it fills,
as a note on ticket 35?
Recommended: yes, a note on ticket 35. Workstream 6 owns both sides, so this needs nothing of yours
beyond saying it should be done.
Answer:

**Q139.** The suite starts **37 harnesses in a run** — one per test class plus one per row that needs
its own registry or trusted-network set — each with an identity provider, two mock API listeners and
an echo upstream. 35 seconds here. Accept that now?
Recommended: yes, accept, and if CI wall clock becomes a problem the fix is **fewer per-row
harnesses, not a shared mutable one**. The isolation is what makes "this request read nothing"
readable off a count.
Answer:

**Q140.** A real intermittent defect was found in the suite's own first draft: it read the log line
count immediately after a response arrived, but **stage 2 writes its line after the pipeline
returns**, so a previous request's line could land between the count and the next request. It failed
roughly one run in three. `Observed.SettledCountAsync` now waits for the log to stop growing first,
with the reason in its doc comment. Should "any later row that reads a rule id takes its count the
same way" be written down somewhere other than that doc comment?
Recommended: yes — one line on ticket 35 beside Q138's. This is the kind of thing that is rediscovered
as a flake by whoever writes the next suite.
Answer:

**Q141.** Kestrel-level refusals — the 431 for oversized headers and the 400 for an escaped NUL — are
asserted **without a rule id and in tests of their own**, because no stage runs and
`contracts/telemetry.md` records the stage-1 gap as a decision waiting for a gate rather than a
defect. That is the same seam ticket 53 (CR-6.4a) is about. Decide 53 first (Q105–Q116) and let these
follow?
Recommended: yes. If 53's fifth option lands — swapping stages 1 and 2 so telemetry is outermost —
these refusals acquire a stage and a rule id, and these tests get stricter rather than being rewritten.
Answer:

**Q142.** N-10's tenancy clause needed an input no fixture can supply (both fixture apps list the
same three API names, and `fixtures/` is workstream 5's), so it arrives as **a published registry
revision that takes one API name off app 2** — the same route task 6.2a used for its own refused
document. Confirm that an operator's publish is the sanctioned way for a test to reach an input the
fixtures do not hold?
Recommended: yes. It is the only route that does not cross an owner boundary, and it exercises the
publish path as a side effect.
Answer:

---

## Across tonight's three lanes — one question

**Q143.** The attribution trailer is spelled **two ways on the branch you are about to push**: the
four leaf and review commits written by the lanes say `Co-Authored-By: Claude Opus 5`, matching each
session's own attribution instruction and the model actually running, while all five merge commits
say `Claude Opus 5.5`, the run's house rule. Two lanes (1.4a and 6.2a) flagged it themselves and
offered to have it normalised on merge. Leave both spellings, or rewrite the branch to one?
Recommended: leave them. Rewriting rewrites five merge commits for a cosmetic difference, and the
history is honest as it stands — the leaves and the merges were written by different sessions. If you
want one spelling going forward, fix the house rule rather than the history.
Answer:
