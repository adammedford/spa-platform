# ADR-0031: A contract-fixed behaviour every workstream would copy lives once in `Platform.Abstractions`

- **Status:** Accepted
- **Date:** 2026-10-20
- **Deciders:** integrator
- **Plan ref:** No decision row. Extends ADR-0015 decision 1 from a value to a behaviour. The
  `rejection-table` spec #142, its grilling record's Q1, Q3 and Q4, answered yes and accepted by
  Adam on 2026-10-19, and the shared decision S7 on #142. Change request
  `contracts/changes/issue-160-rejection-table.md`, decided under #159.

## Context

ADR-0015 put a value four workstreams must spell identically into `Platform.Abstractions`, with a
test against the contract. The same condition holds for a behaviour. The problem body, the rule id
→ status, code join and the write of the rule id to `IRequestTelemetryFeature` are fixed by two
contracts (`contracts/request-gate.md` → Rule ids and `contracts/http-contract.md` → Errors) and
written by four projects that A-03 keeps apart: Platform.Hosting, Platform.Security, Platform.Proxy
and Platform.Identity each keep a `Problems` writer of their own. The four copies have diverged: the
asset endpoint's backup 405 carries no rule id (`src/Platform.Hosting/Assets.cs:66-68`), the
unknown-api endpoint logs through an ad hoc logger (`src/Platform.Proxy/UnknownApi.cs:25-27`), and
the OpenID Connect events' 401 calls itself "not a rejection" where, for a dead cookie, it is one
(`src/Platform.Identity/AuthenticationEvents.cs:50-51`). Three reviews found the duplication and set
it aside as A-03's fault (archived overnight tickets 13, 22 and 31), with no decision recorded.

## Decision

Such a behaviour lives once in `Platform.Abstractions` as a public static type, added as a new type
under `contracts/README.md`'s between-gates rule, pinned by `PublicSurfaceTests`, and held to the
contract by a test that reads the contract's own tables. The project stays "records and interfaces"
plus the implementations a contract fixes: `SessionRef`, `Rejection`, `Problems`. The Warning line
stays each stage's.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| A new integrator project | A solution edit and a second reference in every workstream project, for about eighty lines. |
| `Platform.Testing` | Production code naming test support, as ADR-0015 found. |
| A shared source file compiled into four projects | A coupling no project reference shows, in nobody's path. |
| The status quo | Four copies, three reviews suppressing the duplication, no decision record. |

## Consequences

- Runtime code that writes an HTTP response enters the project whose every later change is a freeze
  question. That is the trade-off: a body-shape change (roadmap gap MR-6's correlation id) is one
  integrator ticket, not four that must agree. `SessionRef` had already crossed the line, and the body
  is five lines.
- A stage writes no problem body's status or code of its own: it names a rule id or a code, and
  `Problems` derives the status. Two responses are not problem bodies and keep their own writers: the
  OpenID Connect handler's remote-failure event writes the static failure page's 400 under
  `auth.callback`, and `UsePlatformShellGate`'s challenge redirects a navigation to the identity
  provider under `auth.session`. The code half is enforced by the one architecture rule of #154 in
  `tests/Gateway.ArchitectureTests`, which proves that no workstream project spells a rule id as a
  literal, once #158 extends that rule to the eleven codes `Problems` holds; the status half by
  `Problems.WriteAsync` taking no status. S7 on #142 superseded the per-owner scans the first draft
  named, and the three `SharedRuleIdsTests` retire there.
- The next contract-fixed behaviour is a visible decision at the public surface, as this one was.
- A future survey that finds the per-project `Problems` copies gone and asks why `Platform.Abstractions`
  holds runtime code stops here, rather than re-filing the candidate.

## Evidence

- `contracts/changes/issue-160-rejection-table.md`: the interface, the throws, the ordering and the
  never-logs rule, each with its reason, and the integrator's decision.
- #160: `Rejection` and `Problems`, their tests, and the two-table test that reads both contract
  tables.
