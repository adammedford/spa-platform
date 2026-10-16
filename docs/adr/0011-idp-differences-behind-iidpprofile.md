# ADR-0011: Put identity-provider differences behind IIdpProfile, chosen per issuer

- **Status:** Accepted. **Amended 2026-10-16 by
  [ADR-0017](0017-g0-findings-fold-into-the-identity-contracts.md): the profile gains a client
  credential style, and the C-2.2 reading below is narrowed to what the Cognito run exercised.**
- **Date:** 2026-10-11
- **Deciders:** integrator
- **Plan ref:** `docs/plan/01-plan.md` → Decisions, D11 (Doc revision 36)

## Context

Cognito's logout is not standard OpenID Connect: it takes `client_id` and `logout_uri`, not the
RP-initiated parameters. Phase 2 adds PingFederate. Scattering those differences through the options
setup would make each new provider a redesign.

## Decision

Provider differences sit behind `IIdpProfile`. The profile is chosen per issuer in platform
configuration (`Issuers:{name}:Profile`), never in a registry entry.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Cognito specifics in the options setup | Every new provider becomes a change to shared identity code. |
| A profile chosen per app | A registry entry could pick a profile that does not match its issuer. |

## Consequences

- An entry cannot pick the wrong profile: it names an issuer, and the issuer determines the profile.
- PingFederate becomes a profile, not a redesign.
- `Issuers:{name}:BrowserOrigin` carries the origin the browser visits at the provider; it goes into `form-action` and is the base of Cognito's logout URL.
- Gate G0 item 2 recorded both, on 2026-10-16 (ADR-0017). Discovery advertises `end_session_endpoint` (C-2.2), so the cognito profile stays as decided; and it advertises `revocation_endpoint` (C-2.3), so the library's revoke call finds the endpoint on its own and no profile member is needed for it. Discovery is not an exercise: C-2.2 does not show that the standard profile's RP-initiated logout works against Cognito, because only the cognito profile's redirect was run (ADR-0017, and the G0 report's "the standard profile's RP-initiated logout was not exercised"). Task 3.7 inherits that as an open question, not a settled one.
- **The profile gains a client credential style** (ADR-0017). Cognito's revocation endpoint refuses a form-body client secret with `401 Unauthorized` and accepts a Basic header (C-2.19), while the code exchange and the refresh accept either (C-2.21). The profile states one style and the code exchange, the refresh and the revocation all use it. Per call site it would be three places to get wrong, and one of them fails only at logout.
- PingFederate inherits two profile questions rather than a redesign: which credential style its endpoints accept, and whether its grace window protects a replayed refresh token as Cognito's does not.

## Evidence

Platform configuration table in `docs/plan/02-contracts.md`. G0 item 2 in `docs/plan/01-plan.md` →
What Gate G0 must still prove.
