# Questions for Adam — overnight run of 2026-10-16

**30 questions, Q1..Q30, grouped by ticket, most-unblocking first.** Each has a recommendation and an
empty `Answer:` line.

- Q1–Q23 come from the two tickets grilled tonight (their `## Questions for Adam` sections). The
  `## Comments` sections of every ticket touched tonight hold integrator prep notes and filing
  notes only, no questions.
- Q24–Q30 are not in any ticket's Questions section. They are the integrator decisions that
  tonight's implementers raised in their results (open change request, held-back bindings, MR-1,
  contract wording), filed under the ticket they came from.
- Questions in older tickets (01, 17, 43–46, 57–60) were answered `yes` on 2026-10-14 and
  2026-10-15 and are not re-asked. Those files are preserved as `questions-2026-10-14-answered.md`
  and `questions-2026-10-15-answered.md`.
- The 2026-10-15 file records that you pre-accepted the recommendation on every question tonight's
  grillings raise (Q1–Q23), and says they would land in `questions-2026-10-16.md`. The run was told
  to write them here instead. The `Answer:` lines are left empty so you can confirm or override.

---

## Ticket 65 — CR-1.5a — Change request 1.5a: the bucket name in two frozen examples, and Object Lock in ADR-0009

File: `/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/65-CR-1.5a-bucket-name-and-object-lock.md`

This goes first because the bucket name is free to change only until the first platform-root apply.
Tasks 1.5 and 1.7 then carry the follow-ups, and 1.4, 1.6b, 1.9 and 6.9 sit behind them.

**Q1.** Should the platform bucket default to `spa-platform-<cell>-<account id>-<region>-an` in the
S3 account regional namespace, falling back to `spa-platform-<cell>-<account id>` only if the
pinned provider cannot set the namespace?
Recommended: yes. No bucket exists yet, so the change costs nothing now. Only the regional namespace
stops another account from claiming the name between task 6.9's teardown and its redeploy.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q2.** Should `modules/cell` keep the `bucket_name` override, with an empty value meaning the
convention?
Recommended: yes. ADR-0014 requires grants to respect it, and a landed module test uses it.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q3.** Should the name be a default that every consumer reads and never derives, with
`teardown.sh` reading `bucket` from the platform root's remote state?
Recommended: yes. With an override in place, a derived name points teardown at the wrong bucket.
The destroy that follows needs that state anyway, so reading it is not the circularity ADR-0013
rejected.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q4.** Should the three example bucket values become `spa-platform-poc-111122223333-us-east-1-an`
(or `spa-platform-poc-111122223333` under the fallback)?
Recommended: yes. They would then match the examples' own Region and the placeholder account that
the Terraform tests already use.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q5.** Should the same edit fix the stale cognito domain in both examples (`cognitoDomain` and
`Issuers.cognito-poc.BrowserOrigin` become
`spa-platform-poc-111122223333.auth.us-east-1.amazoncognito.com`)?
Recommended: yes. It has the same defect as the bucket: the idp root defaults to that name and its
test asserts it, but the examples contradict it.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q6.** Should `modules/cell` fail at plan when the computed bucket name exceeds 63 characters,
rather than narrowing the 22-character cell bound?
Recommended: yes. A 22-character cell fits in `us-east-2` (61 characters) but not in a
14-character Region (66). ADR-0014 and a landed test both pin the bound.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q7.** Should `contracts/s3-layout.md` gain one prose sentence that gives the default name and
the rule "read `bucket`, never derive it"?
Recommended: yes. All three owners read that file, prose may land between gates, and the frozen
plan export cannot carry the rule.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q8.** Should ADR-0009 gain an Object Lock row that leads with "Object Lock protects versions, not
keys: a simple DELETE adds an unprotected delete marker, and a conditional write then succeeds"?
The row would then give the request's reasons (enabling it is irreversible, compliance mode blocks
P-05, governance mode and legal hold only move the scoping problem) and point to ADR-0014 for who
holds the delete.
Recommended: yes. The delete-marker gap shows that Object Lock could not replace the bucket policy,
which answers the request more strongly than the cost argument does.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q9.** Should ADR-0009 be amended in place, with a dated "Amended by CR-1.5a" note on its Status
line and no new ADR?
Recommended: yes. The decision does not change, only its list of alternatives grows, and ADR-0005
set the in-place precedent.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q10.** Should workstream 1's follow-ups go into tasks 1.5 and 1.7, each named in the decision?
They are: the namespaced default built from the existing data sources, the `bucket_name`
description, the length precondition, a module test for the default name, and `teardown.sh`
reading the state output.
Recommended: yes. Neither task is done, so no new ticket is needed.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q11.** Should the state bucket stay in the global namespace as it is?
Recommended: yes. It is applied and holds every root's state, and its name already carries the
account id. Moving it would be a state migration that no scenario needs.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q12.** If you choose the global form (b) over the regional one, should the cell validation also
reject names that end in `-tfstate`?
Recommended: yes, but only under (b). Under (b), cell `a-tfstate`'s platform bucket has exactly
the name of cell `a`'s state bucket.
Answer: yes (pre-accepted by Adam, 2026-10-15)

---

## Ticket 03 — task 1.2 — modules/cell network

File: `/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/03-1.2-cell-network.md`

1.2 sits at the root of the infra chain: 1.3, 1.4, 1.6b, 1.7, 1.9 and 6.9 all wait on it. The
grill found the ticket stale, because its code landed in #5 (`5e2cbcc`). The ticket's Status still
reads `claimed`.

**Q13.** Should the ticket be rewritten as "code landed in #5", with the remaining scope being the
exact-rule assertion, the human apply and P-06 (run by 6.9)?
Recommended: yes. `5e2cbcc` is on main, and the "not merged" text misleads every lane that reads it.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q14.** Should 1.2 add a `terraform test` run that asserts exactly the four security-group rules:
ports, protocols, the referenced groups, and a `cidr_ipv4` only on the task's 443 egress?
Recommended: yes. No test on main stops a third ingress rule, and "no other ingress rule" is 1.2's
own done-when.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q15.** Should the module adopt the VPC's default security group with
`aws_default_security_group` and declare no rules?
Recommended: yes. AWS's allow-all default group makes "no other ingress rule" false when read
literally against the account, and stripping it costs nothing.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q16.** Should zone selection filter on `opt-in-status = opt-in-not-required`?
Recommended: yes. Without the filter, an opted-in Local Zone could become one of the two subnets.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q17.** Should the ALB and the task keep sharing the two public subnets, with the forged-header
path for a tunnel holder accepted as a Phase 1 gap?
Recommended: yes. Only operators hold the tunnel, and splitting the subnets would change frozen row
1.2 and P-01.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q18.** Should the cell be defined as owning its network (the VPC, the subnets and the two security
groups), with Phase 2's network placement left to the P2 rows?
Recommended: yes. That is what the module builds, and the glossary's **Cell** entry leaves it out.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q19.** Should Phase 1 have no VPC endpoints, no flow logs and no IPv6?
Recommended: yes. The plan routes 443 out through a public address, and neither P-01 nor P-06 needs
any of the three.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q20.** Should the two groups be called "task security group" and "ALB security group", and
never the bare "task group"?
Recommended: yes. In ECS, "task group" is an attribute with a different meaning.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q21.** Should `aws_default_security_group` join the module's type allowlist, with the rule
assertion requiring that it has no rules?
Recommended: yes. Adopting it (Q15) otherwise fails the allowlist test, and the assertion is what
proves the group was stripped.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q22.** Should 1.2 close when its mock-provider tests are green in CI, with the apply carried in the
first deployment's Outstanding line and P-06 carried by 6.9?
Recommended: yes. Under ADR-0016 the done-when is a plan property, and the network is applied only
together with 1.3 to 1.5.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**Q23.** Should the integrator open a workstream 6 ticket to record the shared-subnet gap in
`docs/gaps.md`?
Recommended: yes. That file belongs to workstream 6, and workstream 1 cannot write it.
Answer: yes (pre-accepted by Adam, 2026-10-15)

---

## Ticket 26 — task 3.9 — /bff endpoints and the shell gate (also ticket 25, task 3.8)

File: `/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/26-3.9-bff-endpoints.md`
(and `/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/25-3.8-access-token-source.md`)

Raised by the 3.8, 3.9 and 6.2 results. Not in a ticket's Questions section.

**Q24.** The shipping gateway does not yet run the shell gate or bind `IAccessTokenSource`. Both sit
behind the internal `AddPlatformShellGate()` and `AddPlatformAccessTokenSource()`, which the
`tests/Gateway.*` harness switches on by reflection when `SessionRequired` is set. Tonight 6.2, 6.3
and 6.5 made the session rows sign in, but the shared sessionless fixtures in
`tests/Gateway.SecurityTests` (6.3a's rows) still make sessionless requests. Should the integrator
file two tickets: first a ws6 ticket that moves every remaining composed row onto a signed-in
harness (or one that expects the 401 or redirect), and then a ws3 ticket that folds both
registrations into `AddPlatformIdentity` and drops the harness's reflection?
Recommended: yes. Until then, the gateway as built serves assets and forwards `/api` with no
session, which is what 3.8 and 3.9 exist to prevent. This is the one step between tonight's work
and a gateway that enforces login.
Answer: yes (pre-accepted by Adam, 2026-10-15)

---

## Ticket 27 — task 4.2 — Proxy pipeline: token attach and header stripping

File: `/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/27-4.2-proxy-pipeline.md`

Raised by the 4.2 and 3.8 results. Not in a ticket's Questions section.

**Q25.** Decide the change request
`contracts/changes/4.2-access-token-result-provider-unavailable.md` (on the overnight branch): should
`AccessTokenResult` gain a provider-unavailable outcome, and should `contracts/http-contract.md`'s
error table gain `503 idp_unavailable`?
Recommended: yes. ADR-0005 already says both exist. Without them, a provider outage during refresh
throws `IdentityProviderUnavailableException`, which the proxy answers with 500. The request is the
only thing keeping 4.2 and 3.8 at partial.
Answer: yes (pre-accepted by Adam, 2026-10-15)

---

## Ticket 49 — task 2.1b — The validator refuses a `timeoutSeconds` the proxy cannot apply

File: `/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/49-2.1b-validator-refuses-unapplicable-timeout.md`

Raised by the run's blocked list, which says this decision "should go in the morning questions". Not
in a ticket's Questions section.

**Q26.** Should roadmap gap MR-1 (the gateway has no version, and nothing says which registry
documents a build accepts) get an ADR now, so that ADR-0015 decision 4's `timeoutSeconds` maximum
can be set and 2.1b can open?
Recommended: yes. Keep it narrow: a build accepts the `schemaVersion` values it was built with, a
later shape gets a later number, and the validator refuses a `timeoutSeconds` above that build's
proxy ceiling. This recommendation comes from the report writer, not from a grill: no grill has
tested MR-1.
Answer: yes (pre-accepted by Adam, 2026-10-15)

---

## Ticket 22 — task 3.5 — Authentication events and the failure page

File: `/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/22-3.5-events.md`

Raised by the 3.5 result. Not in a ticket's Questions section.

**Q27.** Should `contracts/http-contract.md` → Errors gain a row for a refused OIDC callback: static
HTML, 400, `Cache-Control: no-store`, rule `auth.callback`?
Recommended: yes. That is what 3.5 ships and 6.3 tests (N-05, N-18), and the contract has no row
for it today.
Answer: yes (pre-accepted by Adam, 2026-10-15)

---

## Ticket 24 — task 3.7 — IIdpProfile: standard and cognito

File: `/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/24-3.7-idp-profile.md`

Raised by the 3.2, 3.7 and 3.8 results. Not in a ticket's Questions section.

**Q28.** Should ADR-0005's statement that `ClientCredentialStyle` governs "all three calls" be
amended to cover refresh and revocation only, with the authorization-code exchange keeping the OIDC
handler's form-body secret?
Recommended: yes. The handler supports only that style, and Cognito accepts it at the token
endpoint (C-2.19). Following the ADR literally would mean redeeming the code outside the handler,
which is hand-written protocol code.
Answer: yes (pre-accepted by Adam, 2026-10-15)

**PR43 review correction, 2026-10-16:** this recommendation predates PR41. The supported token client now redeems Basic-style codes, with the handler's token/correlation validation and explicit protocol nonce validation preserved (`src/Platform.Identity/CodeExchange.cs`). ADR-0017's all-three-calls contract is implemented and remains the decision; Q28's hand-written-protocol premise is superseded.

---

## Ticket 61 — CR-6.x — Gateway.SecurityTests intermittently reads NotModified where it expects Swapped

File: `/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/61-security-suite-refresh-outcome-flake.md`

Raised by the run's blocked list. Not in a ticket's Questions section.

**Q29.** 6.1a reproduced this exact failure and fixed it in the harness. Should this ticket be set
to `resolved` as closed by 6.1a?
Recommended: yes. Same rows, same message, and the mechanism was proven by reproduction. The review
also fixed the matching `Rejected` flake in six further asserts.
Answer: yes (pre-accepted by Adam, 2026-10-15)

---

## Ticket 60 — task 6.3a — The negative rows that need no session

File: `/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/60-6.3a-negative-rows-no-session.md`

Raised by the run's blocked list. Not in a ticket's Questions section.

**Q30.** 6.3 has now landed the session halves that 6.3a left open (N-07, N-10, N-11, N-12, N-24 and
N-27), and 6.5 covers the browser clauses of N-25 and N-26. Should row 6.3a be recorded as landed
together with 6.3's merge (`d0e81ad`)?
Recommended: yes. Under the run's own classification, 6.3a has no work of its own and closes with
6.3.
Answer: yes (pre-accepted by Adam, 2026-10-15)
