# ADR-0017: What Gate G0 found against Cognito, and what it changes

- **Status:** Accepted
- **Date:** 2026-10-16
- **Deciders:** integrator
- **Plan ref:** `docs/plan/03-workstreams-and-tasks.md` → task I-6 ("fold the G0 findings into the
  identity contracts, record them as the first decision record, and tag the freeze");
  `docs/plan/01-plan.md` → "What Gate G0 must still prove", items 2 and 3.
- **Evidence:** `spikes/g0/cognito/report-cognito.md` and `output-cognito.txt`, run 2026-10-16
  01:09Z against Cognito user pool `us-east-2_iRHq0u84V` in project `146873694665`. 19 expectations
  held, none failed, 5 findings. Its 24 cleanup revocation requests received successful HTTP
  responses; no refresh checks followed that cleanup. The harness created nothing in AWS.

## Context

Gate G0 exists because the architecture rests on framework and provider behaviour that the plan
could only predict. Items 1, 4 and 5 passed on 11 October 2026 against the shipped packages. Items
2 and 3 needed a real Cognito pool and two app clients, which did not exist until now.

Both passed. **Two OAuth clients completed login, callback, refresh and logout** (C-2.4, C-2.5,
C-2.8, C-2.10, C-2.11, C-2.12, C-2.15, C-2.16), and **Cognito accepted callback and sign-out URLs
carrying a port, with managed login serving clients Terraform created** (C-3.1 to C-3.5). This
supports freezing the contracts subject to the caveats below, the report landing, and green CI.
These are spike-host observations, not proof of the unimplemented production gateway.

Three of item 2's five questions came back the way the plan assumed, and change nothing:

- **`aud` carries the resource, and survives a refresh that sends no `resource`** (C-2.7, C-2.9).
  Task 3.8 leaves `resource` off the refresh, which is T-B12 as written.
- **Revocation reaches a rotated-out token's successor** (C-2.19).
- **Every refresh token fits the token library's 4,096-character limit** with room to spare: 1,752
  characters at login and 1,845 after refreshes for app1, 1,690 and 1,782 for app2 (C-2.20). The
  hazard ADR-0005 recorded does not arise at this scale.

Discovery also advertises both an `end_session_endpoint` and a `revocation_endpoint` (C-2.2,
C-2.3), so the library's revoke call finds its endpoint without help and no `IIdpProfile` member is
needed for it.

Two findings change decisions, and two change only what the glossary says.

## Decision

### 1. The reserve refresh design is adopted: ADR-0005 reopens

**Cognito's grace window does not protect a replayed refresh token** (C-2.17). With rotation
enabled and a 10-second grace configured, a replay **2.0 seconds inside the window was granted** and
returned a different refresh token — and the original successor was then **refused `400
invalid_grant`**. This is a distinct observed outcome: the inside-window replay succeeded and invalidated the
previous successor. It is not literally the original Q11 alternatives (inside-window refusal or
late-replay successor invalidation). The integrator adopts the reserve design on this evidence.

ADR-0005 named the condition for reopening the reserve: "the reserve reopens if G0 item 2 shows Cognito's grace window
does not cover a replayed refresh token." On the observed inside-window outcome,
**gateway-owned locking over the library's
refresh call moves from reserve design to adopted design**, and task 3.8 implements it rather than
resting on the library's token manager alone.

What the finding means concretely: two concurrent calls on one session can both reach the refresh
grant, both succeed, and the second's success invalidates the first's refresh token. The session then
holds a token the provider has already superseded, and the next refresh fails. One refresh at a time
per session is therefore a correctness requirement, not an optimisation.

A separate late-replay control at 12.0 seconds was refused and the successor kept working
(C-2.18). These two probes do not bound every provider timing outcome. The adopted per-session
lock is supported by the existing reserve-design concurrency evidence (T-D1 to T-D10), including
the deadline caveat in T-D10; task 3.8 must prove its implementation against that contract.
Rotation settings remain unchanged.

### 2. The client credential style belongs to the profile: ADR-0011 gains a member

**Only one credential style worked across the exercised calls** (C-2.19, C-2.21). The code
exchange and the refresh accept
the client secret either in the form body or in a Basic header. **Revocation does not**: the form
body was refused `401 Unauthorized` and the Basic header returned `200` (C-2.19). This matches
[AWS's revocation endpoint documentation](https://docs.aws.amazon.com/cognito/latest/developerguide/revocation-endpoint.html),
which requires Basic authentication for a client with a secret.

So the style is a provider difference, which is what `IIdpProfile` is for. The Cognito profile
states one style and it is used for the code exchange, the refresh and the revocation alike, rather
than each call site choosing. A profile that picked per call site would be three places to get wrong
and one of them only fails at logout, which is the worst place to discover it.

### 3. The provider's logout is global; the gateway's is per app

Two findings, one fact. **A second app's login asked for no password** because the provider's session
from the first app's login made it silent (C-2.6), and **after the first app's logout the next login
did ask**, because the provider's logout ended its session for every client in that browser (C-2.14).

The spike host's logout remained per app: app2's session still answered
`/bff/session` and still refreshed after app1 logged out (C-2.15). The production gateway must
prove that separately in tasks 3.9 and 6.2. What changes is the glossary's
**logical boundary**, which must say that it bounds the gateway's sessions and not the provider's.
An operator who reads "each app has its own session" and expects a per-app login prompt is reading a
guarantee the platform does not make.

### 4. Logout's revocation is recorded, not assumed

**A token still refreshed after an unconfirmed logout revocation attempt** (C-2.13).
C-2.12 and C-2.16 record the library's form-body request without the provider's response. C-2.19
independently shows that the endpoint refuses that style with 401 and accepts Basic. Therefore
C-2.13 does not show that an accepted revocation failed or that the token survives until expiry.

Task 3.9 uses the profile's credential style, checks the provider response without logging tokens,
and always removes the local session even when revocation fails. The accepted Basic revocation in
C-2.19 was followed by successor refusal; the cleanup requests only received successful HTTP
responses and were not followed by refresh checks. Endpoint discovery (C-2.2) alone also does not
prove that the standard profile's RP-initiated logout works against Cognito; only the Cognito
profile's redirect was exercised.

### 5. The identity contracts freeze as they stand, with those amendments

The report supports the draft identity shapes with the operational amendments above.
`contracts/README.md` → "What G1b froze" lists the affected parts; the amendments touch
`IIdpProfile` (the credential
style) and leave the `oauth` section of a registry entry, the `Profile` enum and the session-record
shape intact. The browser contract clarifies nullable display claims, session expiry and local
session removal even when revocation fails. I-6 follows answered question Q63: schema statuses
become frozen-at-g1b, root exceptions are removed, all G1b code markers are removed, and tests
assert an empty draft set. FreezeAttribute remains for later gates. Once the evidence lands and
main CI is green, the annotated contracts-v1 tag records closure; no draft cleanup alone opens
identity work.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Raise Cognito's grace window instead of locking | The window is already at its configured 10 seconds and the replay was granted at 2.0. A longer window widens the period in which two grants can both succeed; it does not make the second one harmless. |
| Accept the race and let the failed refresh surface as a 401 | A user is signed out mid-session by a coincidence of timing, and the gateway cannot tell that case from a genuinely revoked session. The reserve design exists because this was foreseen. |
| Retry the refresh when it fails with `invalid_grant` | The successor is already invalid, so there is nothing to retry with. The session's token is unrecoverable at that point. |
| Put the credential style on the issuer in platform configuration | It is a property of the provider, not of a deployment of it. Every Cognito issuer would have to repeat the same value, and a wrong one fails only at logout. |
| Let each call site choose its credential style | Three places, one of which is only exercised at logout. The profile exists to hold exactly this kind of difference. |
| Treat C-2.13 as proof that accepted revocation failed | The logout attempt has no observed response; C-2.19 instead demonstrates accepted Basic revocation followed by successor refusal. |

## Consequences

- **Task 3.8 gains the locking requirement** and is no longer a thin wrapper over the library's
  token manager. ADR-0005's Consequences change from "reserve design, not adopted" to adopted, and
  the reserve's evidence ids T-D1 onward become the design's.
- **Task 3.9 records the revocation's outcome** in the session's removal path.
- **`IIdpProfile` gains a credential-style member** and loses its "draft until G1b" note. The
  standard and cognito profiles each state a style.
- **The glossary's logical boundary gains a sentence** separating the gateway's per-app sessions
  from the provider's single session per browser.
- **Gate closure is recorded by tags after evidence lands.** PR33 supplies the G0 report. Its
  annotated `g0-mechanism` tag and the green main CI cited by `contracts-v1` record closure.
  Nothing in `src/Platform.Identity` starts before `contracts-v1`. The open stage-order decision
  in ticket 53 remains a separate G1b prerequisite; this record alone does not close the gate.
- **The PingFederate gate inherits two open questions**, not one: whether its grace window behaves
  as Cognito's does, and which credential style its endpoints accept. Credential style is a profile
  member; locking is the gateway-owned refresh design, not a
  profile setting. Those questions remain evidence work for the PingFederate gate.
- **No change to rotation, token lifetimes or scopes.** The measured values match what tasks 1.8 and
  3.8 already specify: 5-minute access tokens, 780-minute refresh tokens, rotation with a 10-second
  grace, `ALLOW_REFRESH_TOKEN_AUTH` off.

## Evidence

- The run: `spikes/g0/cognito/report-cognito.md`, `spikes/g0/cognito/output-cognito.txt`, and the
  "Gate G0, as run" table the harness wrote into `spikes/g0/README.md`. 2026-10-16 01:09Z, .NET
  10.0.12, `Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.12,
  `Duende.AccessTokenManagement.OpenIdConnect` 4.2.0, `Duende.IdentityModel` 8.1.0, Playwright
  1.63.0 with Chromium 1243.
- The grace-window finding: C-2.17, with C-2.18 as the control for a late replay.
- The credential-style finding: C-2.21, and C-2.19, including the `401` on a form-body revocation.
- The logout findings: C-2.6, C-2.13, C-2.14, with C-2.15 as the control that the gateway's logout
  is per app.
- The questions that changed nothing: C-2.7, C-2.9, C-2.19, C-2.20, C-2.2, C-2.3.
- Items 1, 4 and 5, cited rather than re-run: `real-handler/RealOidcNuGet/output-nuget.txt` and
  `token-and-proxy/output-token-and-proxy.txt`, 11 October 2026.
