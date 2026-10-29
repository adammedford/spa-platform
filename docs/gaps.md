# The enterprise gap register

Each entry is one known gap between what Phase 1 delivers and what an enterprise adoption would
need. Workstream 6 keeps it current from findings (plan row 6.8,
`docs/plan/03-workstreams-and-tasks.md`): a review, a test, a walk or a decision that finds a gap
adds an entry here, and the entry closes when the row or ticket it names lands. G5's sign-off on #63
states that this register was reviewed.

An entry says what is missing, the risk it leaves, where it was found, and what would close it: a
Phase 2 row in `docs/plan/06-phase-2-and-risks.md`, a ticket, or "accepted for Phase 1, no plan". An
entry that a section elsewhere already argues in full points at that section rather than copying
it.

## From decisions

### GAP-1. The task shares the load balancer's trusted subnets

- **Missing.** The task sits in the load balancer's two subnets, so its own address is inside
  `ForwardedHeaders:TrustedNetworks`.
- **Risk.** Whoever holds a Session Manager session on the task can forward a port to the task's own
  address. That connection never leaves the task, no security group sees it, and the gateway trusts
  the `X-Forwarded-For` and `X-Forwarded-Proto` it carries: a tunnel holder can forge forwarded
  headers (threat T6). Only operators hold that session, and they can already run commands in the
  task (GAP-14).
- **Found.** `.scratch/overnight/issues/03-1.2-cell-network.md`, questions 5 and 11, and
  `.scratch/overnight/questions.md` Q23, all answered yes. `infra/modules/cell/README.md` → "One gap
  the shared subnets leave" states the mechanism.
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

- **Missing.** Every app is served by one process, so one compromise reaches every app's sessions
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
- **Found.** `docs/plan/01-plan.md` → "Threats", T16.
- **Closes with.** Phase 2's caching and limits, which T16 names and no row yet holds; GAP-6 and
  GAP-7 are the written-plan half.
