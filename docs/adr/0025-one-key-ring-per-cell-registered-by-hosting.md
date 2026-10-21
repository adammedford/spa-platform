# ADR-0025: One data-protection key ring per cell, named from the cell and kept in Parameter Store, registered by hosting

- **Status:** Accepted
- **Date:** 2026-10-20
- **Deciders:** integrator, on Adam's answers to
  https://github.com/adammedford/spa-platform/issues/219 (wayfinder map #215)
- **Plan ref:** `docs/plan/06-phase-2-and-risks.md` → P2-2 and the "key durability" gap; change
  request `contracts/changes/p2-2-data-protection-key-ring.md`. Gives ADR-0012's "part of the
  data-protection application name" its string and its owner.

## Context

Nothing in `src/` configures data protection, so each task keeps a key ring of its own and cookies
and in-flight logins die with the task. Four consumers read the ring: the cookie handler's ticket
format and the OpenID Connect state and nonce protectors (workstream 3), the antiforgery tokens
(workstream 4) and, from P2-1, the envelope around stored session tickets. Whoever registers the ring
serves all four, and the choice of entry point decides which workstream owns the work.

## Decision

1. **One key ring per cell.** Every task of a cell shares it and no two cells share one. The ring
   lives in Parameter Store under a per-cell prefix, as the AWS data-protection provider stores it,
   encrypted on the server under the cell's KMS key or the account's default.
2. **The application name is `spa-platform-{cell}`**, derived from `Platform:Cell` and never
   configurable. A configurable name is the one way two tasks of one cell could disagree; renaming
   it later unprotects everything live. A `DataProtection` section present with no `Platform:Cell`
   fails the start.
3. **With no `DataProtection` section, nothing is registered** and the gateway behaves as in
   Phase 1, logging one line that says so. Hermetic hosts and the smoke test carry no section.
4. **The same-cell half of the invariant is proved hermetically** by two hosts over one repository
   reading each other, with two hosts over two repositories as the control; the cross-cell half is
   held by the per-cell prefix and the task role's grant, as client secrets are, with no gateway
   check.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Register the ring in `AddPlatformIdentity` (workstream 3) | The antiforgery tokens are workstream 4's and would depend on a registration inside identity; the name would be set by a workstream that does not own `Platform:Cell`; and workstream 3 already carries P2-1 and P2-3 on the critical path. |
| Register it in `AddPlatformRegistry` (workstream 2) | It already carries the SDK and the client, but `tools/spactl` references `Platform.Registry`, so a command-line tool would gain data protection and the key-ring package. |
| A configurable application name | Two tasks of one cell could disagree; the invariant must hold by construction. |
| The ephemeral provider when the section is absent | A behaviour change to every Phase 1 host and the smoke test for a reason none has; it also hides a misconfigured cell behind a ring that is per-task either way. |

## Consequences

- The ring is registered by `AddPlatformHosting` (workstream 2), one call made only when the section
  is present. `Platform.Hosting` gains the AWS data-protection package and a direct SDK reference,
  and shares the SSM client `AddPlatformRegistry` registers.
- Workstream 2 proves it in `tests/Platform.Hosting.Tests` over a stub SSM client of its own;
  workstream 3 proves two `IdentityGateway`s over one `IXmlRepository`, which gives that harness a
  repository parameter and stops the identity suite writing keys under the user's profile.
- P2-2 is a prerequisite of P2-9's read-only root filesystem: with the ring in Parameter Store the
  gateway has no file writer left.
- The pin, the section's two keys and the dependencies row are decided with the rest of the request
  on https://github.com/adammedford/spa-platform/issues/222.
- `GLOSSARY.md` gains *Key ring*, and *Cell* states the invariant.

## Evidence

Change request `contracts/changes/p2-2-data-protection-key-ring.md` and its Decision section; the
grilling record and answer on https://github.com/adammedford/spa-platform/issues/219; the package
facts verified on https://github.com/adammedford/spa-platform/issues/216 (branch
`research/phase-2-package-facts`).
