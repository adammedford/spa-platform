# The enterprise gap register

Each entry is one known gap between what Phase 1 delivers and what an enterprise adoption would
need. Workstream 6 keeps it current from findings (plan row 6.8,
`docs/plan/03-workstreams-and-tasks.md`): a review, a test, a walk or a decision that finds a gap
adds an entry here, and the entry closes when the row or ticket it names lands. G5's sign-off on #63
states that this register was reviewed.

An entry says what is missing, the risk it leaves, where it was found, and what would close it: a
Phase 2 row in `docs/plan/06-phase-2-and-risks.md`, a ticket, or "accepted for Phase 1, no plan". An
entry that a section elsewhere already argues in full points at that section rather than copying
it. `docs/plan/06-phase-2-and-risks.md` → "Enterprise gaps" is a source the register carries in
full: each of its areas has an entry, or names the entry that carries it, in GAP-17 to GAP-27 below.

## From decisions

### GAP-1. The task shares the load balancer's trusted subnets

- **Missing.** The task sits in the load balancer's two subnets, so its own address is inside
  `ForwardedHeaders:TrustedNetworks`.
- **Risk.** A tunnel holder can forge forwarded headers (threat T6), by the path
  `infra/modules/cell/README.md` → "One gap the shared subnets leave" sets out. Only operators hold
  that tunnel, and they can already run commands in the task (GAP-15).
- **Found.** `.scratch/overnight/issues/03-1.2-cell-network.md`: question 5 kept the shared subnets
  as a Phase 1 gap, and question 11 sent the entry here, which `.scratch/overnight/questions.md` Q23
  confirmed.
- **Closes with.** P2-4 (private subnets) and P2-9 (no ECS Exec). Accepted for Phase 1.

### GAP-2. Ending an app's sessions leaves the provider's session

- **Missing.** Ending every session of one app ends the gateway's sessions only. It is done with
  four `spactl` steps (`docs/runbooks/end-app-sessions.md`): `spactl app remove <app>`,
  `spactl wait` for that revision, `spactl app add` with the same definition and release, and
  `spactl wait` again. The app's host answers 404 between the remove and the add.
- **Risk.** The provider's session survives, so a user whose sign-in session at the provider is
  still live may be signed back in with no prompt. Nothing is revoked at the provider either.
- **Found.** #244, Q89 and Q94, which said #62 would note this gap here; #62 closed without it.
- **Closes with.** Accepted for Phase 1, which asks for no new lever. Provider logout and
  back-channel logout are P2-6's decisions, and ending one subject's sessions is ADR-0028's
  (`docs/adr/0028-back-channel-logout-outside-bff-by-subject.md`).

## What a mature release still needs (GAP-3 to GAP-12)

Each is argued in `docs/roadmap.md` → "What a mature release still needs, and nothing has written
down", under its MR number, which also names where it would land. Found by reading the plan against
the question "could another team adopt this".

| Entry | Roadmap | Gap |
| --- | --- | --- |
| GAP-3 | MR-1 | No gateway version, and no rule for which registry documents a build accepts |
| GAP-4 | MR-2 | Start-up and shut-down under load are undefined |
| GAP-5 | MR-3 | No performance budget and no capacity model, so the load test has no pass mark |
| GAP-6 | MR-4 | No upstream timeout, no per-app bound, no trigger for rate limiting |
| GAP-7 | MR-5 | No catalogue of what the gateway does when a dependency is down |
| GAP-8 | MR-6 | The log and metric schema is not yet a contract a supporter can rely on |
| GAP-9 | MR-7 | Package advisories only warn, and no cadence moves the pins |
| GAP-10 | MR-8 | Platform configuration is not validated at start-up |
| GAP-11 | MR-9 | A rollback across a session-record change is undefined |
| GAP-12 | MR-10 | No support statement and no service objectives |

## Threats accepted rather than refused (GAP-13 to GAP-16)

The threats in `docs/plan/01-plan.md` → "Threats" whose mitigation is a written acceptance, not a
refusal a test proves.

### GAP-13. The authorization code appears in URLs and access logs (T13)

- **Missing.** The callback carries the code in its query string, so it reaches the load balancer's
  access logs. Cognito documents no `form_post` mode.
- **Risk.** A reader of those logs sees codes. They are single-use, client-bound and protected by
  PKCE, so a code alone redeems nothing.
- **Found.** `docs/plan/01-plan.md` → "Threats", T13, which has no test.
- **Closes with.** P2-6's `form_post` decision for PingFederate. Accepted for Phase 1.

### GAP-14. The shared gateway process is compromised (T14)

- **Missing.** Every app is served by the same gateway, so one compromise reaches every app's sessions
  and secrets.
- **Risk.** Very high impact, low likelihood (`docs/plan/06-phase-2-and-risks.md`, risk 1).
  Least-privilege IAM is the only Phase 1 bound.
- **Found.** `docs/plan/01-plan.md` → "Threats", T14, which has no test.
- **Closes with.** P2-8 (security cells). Accepted for Phase 1.

### GAP-15. Operators reach a shell in the gateway through ECS Exec (T15)

- **Missing.** ECS Exec is on, because the operator tunnel needs it, and the root filesystem is
  writable.
- **Risk.** An operator, or whoever holds an operator's credentials, can run commands in the task.
  GAP-1 is one consequence.
- **Found.** `docs/plan/01-plan.md` → "Threats", T15, which has no test.
- **Closes with.** P2-9 (no ECS Exec, read-only root filesystem). Accepted for Phase 1.

### GAP-16. S3 is a hard dependency, and nothing bounds a login storm (T16)

- **Missing.** The registry keeps serving its last good snapshot, which F-09 proves. The rest of
  T16 is accepted: assets have no cache in front of S3, and no limit bounds a login storm.
- **Risk.** An S3 outage stops every app's assets, and a login storm reaches the provider unbounded.
- **Found.** `docs/plan/01-plan.md` → "Threats", T16, and `docs/plan/06-phase-2-and-risks.md`,
  risk 10.
- **Closes with.** Phase 2's caching and limits, which T16 and risk 10 name and no Phase 2 row's
  "What changes" column adds. The enterprise-gaps table in `docs/plan/06-phase-2-and-risks.md`
  sends "every asset request reads S3" to P2-10, whose load test measures that cost and adds no
  cache (GAP-5 is why the measurement has no pass mark yet). The cache and the limit themselves sit
  only in the deferred-features table there, which leaves asset caching to hosting and rate limiting
  to the request gate, with no trigger. GAP-6 and GAP-7 are the written-plan half.

## What the plan's enterprise-gap table names (GAP-17 to GAP-27)

One row per area of `docs/plan/06-phase-2-and-risks.md` → "Enterprise gaps", in that table's order.
#568's review found that eleven of the table's fourteen areas had no entry here, and #569 read
the table against GAP-1 to GAP-16 to place each. These entries were added after G5's
review on #63, so the header's sentence about that review does not cover them. They index the
frozen Phase 1 inventory, some of whose named closure work has already landed: they are not eleven
newly open gaps. The two middle columns are 06's cells verbatim, as the plan export froze them, and
the last column, read from the trunk when this section was written, is where an entry's current
disposition is established, by the evidence it links, under the closure rule above. A carried area
gets no new id and points at the entry that carries it. A plan row's merge is that row landing
under the closure rule, not its stage's gate: for P2-1 to P2-5 the gate is G6's deployed run and its
`g6-continuity` tag (#348), which `docs/roadmap.md` → "S8 Continuous: it survives its own
deployment" names.

| Entry | Area | Phase 1 state (historical: 06's cell) | Closes with (historical: 06's "Closed by") | Current disposition / evidence |
| --- | --- | --- | --- | --- |
| GAP-17 | Availability | One task. A deployment or crash interrupts everyone. | P2-4 | Open. Part of P2-4 merged in [1fbd6c8](https://github.com/adammedford/spa-platform/commit/1fbd6c8a1fb98c4defceebfd79dae57c2aeadf62): F1's shutdown and deployment settings, the poc root's continuity inputs and the two-task doubles. Two tasks across zones remain, in #347, which waits on #583. |
| GAP-18 | Session durability | In memory. Lost on restart. | P2-1 | Closed under the register's rule: P2-1 merged in [fa96201](https://github.com/adammedford/spa-platform/commit/fa96201287903c78ed639e230605472cfb5ceec1). The G6 gate is #348's deployed run and its `g6-continuity` tag, not this merge. |
| GAP-19 | Key durability | In memory. Cookies and in-flight logins die with the task. | P2-2 | Closed under the register's rule: P2-2 merged in [98e2317](https://github.com/adammedford/spa-platform/commit/98e231723d99912c4ff22dc1bacfb9f219c99cab). The G6 gate is #348's deployed run and its `g6-continuity` tag, not this merge. |
| GAP-20 | Revocation | A removed session stops working at once, but only because there is one task | P2-3 | Closed under the register's rule: P2-3 merged in [7bdfa2e](https://github.com/adammedford/spa-platform/commit/7bdfa2e0420f656f14a3abe9eeda09fc21cb8461). The G6 gate is #348's deployed run and its `g6-continuity` tag, not this merge. |
| GAP-21 | Network | Public address on the task, unrestricted outbound 443 | P2-4, P2-9; GAP-1 carries the shared-subnet half | Open. Part of P2-4 merged in [1fbd6c8](https://github.com/adammedford/spa-platform/commit/1fbd6c8a1fb98c4defceebfd79dae57c2aeadf62), with teardown's egress cases; private subnets, no public address and controlled egress remain, in #347, which waits on #583. P2-9's egress allowlist has not landed. |
| none | Operator access | ECS Exec enabled, so an operator can reach a shell in the gateway | P2-9; carried by GAP-15 | Carried by GAP-15. P2-9 has not landed. |
| GAP-22 | Identity provider | Cognito only. Non-standard logout. Authorization code in the URL. | P2-6; GAP-13 carries the code in the URL, GAP-2 the provider's session on logout | Open. P2-6 has not landed. Its contract decisions merged in [9231482](https://github.com/adammedford/spa-platform/commit/9231482b2c67f9bc3885f89092c9b388b3850ad3) (items 1 to 6) and [115e79d](https://github.com/adammedford/spa-platform/commit/115e79d5024f746d9a0450565c1d8f883dc5ec2a) (item 7, ADR-0028); the profile itself remains, and #545, CI's run of the suites under it. |
| GAP-23 | Secrets | Parameter Store with manual rotation | P2-7 | Open. P2-7 has not landed. |
| GAP-24 | Publishing | One publisher, the deployer, writes every app's releases | P2-7 | Open. P2-7 has not landed. |
| GAP-25 | Telemetry and audit | One log stream. No trace or metric export, no ALB access logs. | P2-7 | Open. P2-7 has not landed. |
| GAP-26 | Registry governance | One writer controlled by IAM. No review step. | P2-7 | Open. P2-7 has not landed. |
| none | Isolation | One cell for every app | P2-8; carried by GAP-14 | Carried by GAP-14. P2-8 has not landed. |
| none | Performance | Unmeasured. Every asset request reads S3. | P2-10; carried by GAP-5 and GAP-16 | Carried by GAP-5 and GAP-16. P2-10 has not landed. |
| GAP-27 | Assurance | No penetration test, no formal threat model review | P2-10 | Open. P2-10 has not landed. |
