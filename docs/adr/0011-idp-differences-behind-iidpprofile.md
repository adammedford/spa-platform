# ADR-0011: Put identity-provider differences behind IIdpProfile, chosen per issuer

- **Status:** Accepted
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
- Gate G0 item 2 records what Cognito's discovery document advertises for logout, and whether it lists a revocation endpoint.

## Evidence

Platform configuration table in `docs/plan/02-contracts.md`. G0 item 2 in `docs/plan/01-plan.md` →
What Gate G0 must still prove.
