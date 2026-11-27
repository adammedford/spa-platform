# Concurrent authorization-code grants on revocation, and `contracts/telemetry.md` instrument coverage

- **Ticket**: #1582, "Does revoking a losing code-flow refresh token touch a sibling grant, and which contracts/telemetry.md instruments lack code or tickets?", a child of wayfinder map #1580 ("Wayfinder: gateway runtime hygiene, old-asset continuity, and supportability (MR-1, MR-6)").
- **Date**: 2026-11-27
- **Branch**: `research/grant-revocation-and-telemetry-inventory`, based on `origin/main` at `8de2d621`.
- **Method**: inspected gateway and test-fixture source, contracts, change requests, and `spikes/g0/cognito/` on `origin/main` at `8de2d621`; queried the repository's GitHub issues (`gh issue list` / `gh issue view`); read primary protocol and vendor documentation through web search (RFC 7009, Amazon Cognito User Pools documentation, and PingFederate public documentation). **PingFederate was not run**, and no PingFederate product test result or opinion is recorded (`AGENTS.md`, #1269). **No gateway code was edited or executed** for this inventory beyond running `tools/check.sh` on the branch.

## Answer in brief

1. **Two authorization-code exchanges for the same `(sub, client_id)` are independent grants on revocation under RFC 7009, Amazon Cognito, `fixtures/TestIdp`, and PingFederate's default configuration — with one explicit PingFederate exception.**
   - **RFC 7009** scopes cascading revocation to *"the same authorization grant"*, not to distinct grants for the same `(sub, client_id)`.
   - **Amazon Cognito** isolates refresh-token revocation per token family (`POST /oauth2/revoke` revokes the target refresh token and tokens derived from it, without affecting other refresh tokens issued to the same user; `revoke_associated_sessions` defaults to `NONE`, and `LINKED` revokes the parent SSO session rather than sibling token sessions). `spikes/g0/cognito/report-cognito.md` (`C-2.19`) proved single-grant ancestor-to-successor revocation, while `C-2.4`/`C-2.5`/`C-2.12`/`C-2.15` tested distinct `client_id`s (`app1`, `app2`), not two concurrent code grants for the same `(sub, client_id)`.
   - **`fixtures/TestIdp`** assigns a fresh `FamilyId` (`"family-" + serial`) on every `authorization_code` exchange, so revoking one code grant's refresh token never touches a sibling grant, whether `RevokeCascadesToFamily` is `false` or `true`.
   - **PingFederate (public documentation only, #1269)** creates separate persistent grants per `authorization_code` exchange by default, **unless** *"Reuse Existing Persistent Access Grants for Grant Types"* is enabled for **Authorization Code** (globally or per OAuth client). When that setting is enabled, PingFederate enforces **one persistent grant per `(user, client)`**, reusing and updating the single persistent grant across code flows; revoking the losing callback's refresh token at `/as/revoke_token.oauth2` then revokes the shared persistent grant and can invalidate the winning grant's tokens.
   - **Impact on #1587**: revoking on `MergeOutcome.Missing` (no live session record) is safe across all four sources; revoking on `MergeOutcome.Exhausted` (a winning session still lives) is safe on Cognito, `TestIdp`, and default PingFederate, but **not unconditionally safe** if a PingFederate deployment enables persistent-grant reuse for `authorization_code`.
2. **Of the ten metrics named in `contracts/telemetry.md`, three counters are implemented in `src/`, two counters have an open contract CR naming their future owner, and five have neither code nor an owning implementation ticket.**
   - **Implemented in `src/` (3)**: `gateway.requests`, `security.rejections`, and `storage.read_refusals`, all in `src/Platform.Telemetry/RequestTelemetry.cs` (lines 54–59). Note that `gateway.requests` and `security.rejections` still omit the `fragment_id` tag promised by `contracts/telemetry.md`, which open ticket #1007 owns adding.
   - **Contract-assigned to an open CR, not yet in `src/` (2)**: `auth.logins` and `auth.refreshes` are unassigned in `src/` today and carried by open change request #1275 (under P2-7 map #1220 and open spec-writing ticket #1236), which moves them from `Platform.Identity` to `Platform.Telemetry`'s audit writer with an `outcome` tag.
   - **Unowned and unimplemented (5)**: `auth.sessions.active`, `registry.revision`, `registry.age_seconds`, `registry.rejected`, and `proxy.upstream.duration` have no implementation in `src/` and no open or closed implementation ticket. (Closed CR `contracts/changes/p2-5-readiness-reports-registry-revision.md` left `registry.revision`, `registry.age_seconds`, and `registry.rejected` deferred to MR-6 / P2-7, and #1579 specifies stall-signal observables without naming those three instruments.)

## 1. Concurrent authorization-code grants and revocation

### 1.1 Gateway context: `FragmentEvents.WriteAsync` vs `AccessTokenSource.WriteAsync`

In `src/Platform.Identity/FragmentEvents.cs`, a fragment callback (`OnTicketReceived`, lines 118–142) arrives after ASP.NET Core's `OpenIdConnectHandler` has already redeemed an authorization code at the provider's token endpoint and populated `context.Properties?.GetTokens()`. `FragmentEvents.WriteAsync` (lines 174–220) enters the fragment's per-token-set gate (`{sessionKey}:{fragmentId}` in `AppSessions.EnterAsync`, `src/Platform.Identity/AppSessions.cs:65–69`) and runs a bounded optimistic merge (`AppSessions.Turn.MergeAsync`, `AppSessions.cs:507–545`, default `MergeBudget = 3` within `StoreDeadline`):

- **`MergeOutcome.Missing`** (`AppSessions.cs:515–518`): `Owned` is `null` (the session record was removed, expired, or no longer owned by `(appId, clientId)` on initial read or on a post-conflict `RereadAsync`). Today `FragmentEvents.WriteAsync` (lines 213–217) redirects to `hop.Continuation.ReturnUrl` and **drops the new tokens unrevoked**.
- **`MergeOutcome.Exhausted`** (`AppSessions.cs:525–529`): `Owned` is still non-null, meaning a live session record exists, but `budget` attempts or `StoreDeadline` ran out against concurrent writers (such as another fragment's write or another concurrent callback/refresh on the same session record). Today `FragmentEvents.WriteAsync` (lines 207–211) logs `Exhausted` (`"this hop's tokens are dropped unrevoked"`) and **drops the new tokens unrevoked**.

By contrast, `AccessTokenSource.WriteAsync` (`src/Platform.Identity/AccessTokenSource.cs:145–177, 541–596`), updated by #1543 and #1544:

- On `missing` (`AccessTokenSource.cs:568–578`): captures `refresh.OrphanedRefreshToken`, releases the gate/lease, calls `RevokeOrphanAsync` -> `SessionEnd.RevokeAsync` (`src/Platform.Identity/SessionEnd.cs:199–231`) under `ProviderDeadline` (sending `token` and `token_type_hint=refresh_token` in the issuer's `ClientCredentialStyle`), logs an `Orphaned successor line` (`contracts/telemetry.md:17`), and returns `Unauthenticated`.
- On `superseded` or `exhausted` (`AccessTokenSource.cs:523–525, 551–557, 580–586`): drops the tokens unrevoked (`contracts/http-contract.md:164–166`) because the session still holds the provider grant and #1543 noted that `C-2.19` established only predecessor-to-successor revocation, not successor-to-predecessor.

The structural difference in `FragmentEvents.WriteAsync` is that the losing callback did **not** present the winning session's refresh token; it redeemed a separate authorization code for the same `(sub, client_id)`. Whether revoking that losing refresh token can reach a sibling authorization-code grant depends on the provider's grant model.

### 1.2 Primary-source findings

| Source | Grant isolation for two `authorization_code` exchanges with same `(sub, client_id)` | Can revoking the losing callback's refresh token invalidate the winning grant? | Primary evidence |
| --- | --- | --- | --- |
| **RFC 7009** | Scopes cascading revocation to *"the same authorization grant"*; does not mandate cross-grant revocation for `(sub, client_id)`. | **No**, if the authorization server treats each code exchange as its own authorization grant. Server grant-grouping policy is outside RFC 7009. | RFC 7009 Abstract, Section 2.1, Section 2.2 (returns `200 OK` whether revoked or unknown/already invalid). |
| **Amazon Cognito** | Each refresh token is an independent grant chain. Documented revocation invalidates the target refresh token and access/ID tokens derived from it, leaving other refresh tokens for the same user unaffected. | **No** (under Cognito's documented semantics). `revoke_associated_sessions` defaults to `NONE` (and `SessionEnd.RevokeAsync` does not send it); even `LINKED` revokes only the parent SSO session, not sibling token sessions. `C-2.*` in `report-cognito.md` does not itself test same-`(sub, client_id)` concurrent code grants: `C-2.19` tested one family, and `C-2.4`/`C-2.5`/`C-2.12`/`C-2.15` used distinct `client_id`s (`app1`, `app2`). | `revocation-endpoint.html`, `token-revocation.html`, `API_RevokeToken.html`; `spikes/g0/cognito/report-cognito.md` (`C-2.19`, `C-2.4`, `C-2.5`, `C-2.12`, `C-2.15`), `spikes/g0/cognito/output-cognito.txt`. |
| **PingFederate** *(public docs only; #1269)* | **Configuration-dependent.** Default for `authorization_code`: separate persistent grant per exchange. When *"Reuse Existing Persistent Access Grants for Grant Types"* is enabled for `authorization_code` (globally or per client), PingFederate limits active grants to **one persistent grant per `(user, client)`** and updates that shared grant across requests. | **No** under the default setting. **Yes, if grant reuse is enabled for `authorization_code`**: `/as/revoke_token.oauth2` invalidates the refresh token's backing persistent grant (and associated reference tokens / JWTs linked by Access Grant GUID), so revoking a token tied to that single shared persistent grant can invalidate the winning session's grant/tokens. (PingFederate docs also warn that reusing the grant across concurrent sessions already risks invalidating earlier refresh tokens.) | PingFederate Administrator's Reference Guide: *Authorization Server Settings* (*Reuse Existing Persistent Access Grants for Grant Types*), *Token revocation endpoint* (`/as/revoke_token.oauth2`); `contracts/changes/p2-6-pingfederate-profile.md` (does not constrain this server setting). |
| **`fixtures/TestIdp`** | Separate `FamilyId` (`"family-" + serial`) assigned on every `authorization_code` exchange at `/token`. | **No**, under both `Faults.RevokeCascadesToFamily = false` (default) and `true`. `TestIdp` has no switch modelling single-grant reuse per `(sub, client_id)`. | `fixtures/TestIdp/TestIdentityProvider.cs:397–408, 462–477, 572–579`; `fixtures/TestIdp/TestIdpFaults.cs:56–59`. |

### 1.3 Bearing on #1587

- **`MergeOutcome.Missing`**: No winning session record survives in the gateway's store, so no winning grant is at risk from revocation; revoking the losing callback's refresh token behaves analogously to `#1544` (`AccessTokenSource` `missing`).
- **`MergeOutcome.Exhausted`**: The session record still lives. While RFC 7009 (separate grants), Cognito, `TestIdp`, and default PingFederate isolate sibling `authorization_code` grants, PingFederate's *"Reuse Existing Persistent Access Grants for Grant Types"* setting for `authorization_code` breaks that isolation. In addition, `FragmentEvents.WriteAsync` can reach `Exhausted` from *any* concurrent writer touching the session record (e.g., another fragment's write), not only a duplicate login for the same fragment. `#1587` therefore remains valid and must decide `Exhausted` accounting for whether the gateway assumes independent grants or avoids revoking while a live session remains (or adds a PingFederate deployment requirement).

## 2. `contracts/telemetry.md` instrument coverage

`contracts/telemetry.md` (line 18) names ten metrics, all expected on `Platform.<Project>` meters created via `IMeterFactory` (`Platform.Telemetry` subscribes via `.AddMeter("Platform.*")` in `src/Platform.Telemetry/PlatformTelemetryExtensions.cs:85`):

### 2.1 Ten-metric coverage matrix

| # | Metric (`contracts/telemetry.md:18`) | Contracted meter / owner (`contracts/telemetry.md`) | Implemented in `src/` today | Owning ticket(s) (open / closed) | Current status & gaps |
| --- | --- | --- | --- | --- | --- |
| 1 | `gateway.requests` | `Platform.Telemetry` (`RequestTelemetry`) | **Yes**: `src/Platform.Telemetry/RequestTelemetry.cs:46, 54–55, 106–109` (`Counter<long>`, tags `app_id`, `route`, `status`) | Closed: Task 6.4 (`docs/plan/03-workstreams-and-tasks.md`). Open (tag gap): **#1007** | **Implemented with partial tag gap**: `contracts/telemetry.md:9–10, 46` requires `fragment_id` tag when a fragment resolved; `RequestTelemetry.cs` does not yet emit `fragment_id`. Covered by open #1007. |
| 2 | `auth.logins` | Originally `Platform.Identity`; moved on paper by open CR #1275 to `Platform.Telemetry` (audit writer) | **No** (0 hits in `src/`) | Open CR: **#1275** (raised by closed #1226, under P2-7 map **#1220** and open spec **#1236**) | **Contract-owned by open CR, unimplemented**: #1275 moves `auth.logins` to the `Platform.Telemetry` audit writer with `app_id` and `outcome` tags; implementation tickets wait on #1275 via #1236. |
| 3 | `auth.refreshes` | Originally `Platform.Identity`; moved on paper by open CR #1275 to `Platform.Telemetry` (audit writer) | **No** (0 hits in `src/`) | Open CR: **#1275** (raised by closed #1226, under P2-7 map **#1220** and open spec **#1236**) | **Contract-owned by open CR, unimplemented**: same as `auth.logins`. |
| 4 | `auth.sessions.active` | `Platform.Identity` (retained there in #1275 Reading 2) | **No** (0 hits in `src/`) | **Unowned** (no implementation ticket; #1275 only adjusts prose around it) | **Neither code nor owning ticket**: no open or closed issue implements it. Note: with `DynamoDbSessionTable` across tasks, per-task in-memory counting vs store-wide active sessions is also unsettled. |
| 5 | `security.rejections` | `Platform.Telemetry` (`RequestTelemetry`) | **Yes**: `src/Platform.Telemetry/RequestTelemetry.cs:47, 56–57, 112–117` (`Counter<long>`, tags `rule`, `app_id`) | Closed: CR `6.4-request-telemetry-feature.md`, Task 6.4. Open (tag gap): **#1007** | **Implemented with partial tag gap**: counts every rejection with `rule`, but does not yet emit the `fragment_id` tag required by `contracts/telemetry.md:9–10, 46, 92–93`. Covered by open #1007. |
| 6 | `registry.revision` | `Platform.Registry` | **No** (0 hits in `src/`) | **Unowned** (deferred in closed **#220** / `contracts/changes/p2-5-readiness-reports-registry-revision.md`) | **Neither code nor owning implementation ticket**: #220 chose `/readyz` `instance` for rollout waits and noted *"The `registry.revision` gauge stays owed to MR-6 and P2-7"*, but neither P2-7 (#1220/#1236/#1275) nor #1579 owns implementing `registry.revision`. |
| 7 | `registry.age_seconds` | `Platform.Registry` | **No** (0 hits in `src/`) | **Unowned** (referenced in `p2-5-readiness-reports-registry-revision.md:31, 111`; related signal in **#1579**) | **Neither code nor owning implementation ticket**: #1579 asks Registry and Telemetry to *"expose consecutive unsuccessful polls, bounded failure categories, and elapsed time since the last successful source confirmation"* without naming `registry.age_seconds`. |
| 8 | `registry.rejected` | `Platform.Registry` | **No** (0 hits in `src/`) | **Unowned** (referenced in `p2-5-readiness-reports-registry-revision.md:31, 111`; related signal in **#1579** and #1275's `registry_swap` `rejected` audit event) | **Neither code nor owning implementation ticket**: #1579 asks for consecutive unsuccessful polls and bounded failure categories without naming `registry.rejected`, and #1275 adds a log audit event rather than a metric. |
| 9 | `proxy.upstream.duration` | `Platform.Proxy` (by `Platform.<Project>` convention) | **No** (0 hits in `src/` or `tests/`) | **Unowned** (appears only in `contracts/telemetry.md:18` and `docs/plan/02-contracts.md:377`) | **Neither code nor owning ticket**: no GitHub issue (open or closed) mentions `proxy.upstream.duration`. (`#1251`/`#1260`/`#1261` and `#1579` measure upstream latency in the `tools/bench` load harness rather than adding a gateway metric.) |
| 10 | `storage.read_refusals` | `Platform.Telemetry` (`RequestTelemetry`) | **Yes**: `src/Platform.Telemetry/RequestTelemetry.cs:48, 58–59, 121–126` (`Counter<long>`, tags `app_id`, `key_class`) | Closed: **#429** (`contracts/changes/issue-381-object-read-refusal.md`) | **Complete**: implemented and tested (`tests/Platform.Telemetry.Tests/ReadRefusalTests.cs`). |

### 2.2 Bearing on #1588

- All five **unowned** metrics (`auth.sessions.active`, `registry.revision`, `registry.age_seconds`, `registry.rejected`, `proxy.upstream.duration`) are live candidates for `#1588`'s second question: implement in `src/`, defer/remove from `contracts/telemetry.md` via change request until a consumer needs them, or map onto an existing spec (`#1579` for `registry.age_seconds` / `registry.rejected`, `#1236` for P2-7 metrics).
- `auth.logins` and `auth.refreshes` are already owned on the contract side by `#1275` (awaiting decision and P2-7 spec `#1236` implementation tickets), so `#1588` should avoid duplicating their contract decisions unless `#1275` is superseded.
- `gateway.requests` and `security.rejections` already exist in `src/Platform.Telemetry/RequestTelemetry.cs` and need only `#1007`'s `fragment_id` tag work to match `contracts/telemetry.md`.

## Executed and read

**Executed / inspected locally** (2026-11-27, `origin/main` at `8de2d621` and branch worktree):

- Source inspection (`read_file` / `rg`):
  - `src/Platform.Identity/FragmentEvents.cs`, `src/Platform.Identity/AppSessions.cs`, `src/Platform.Identity/AccessTokenSource.cs`, `src/Platform.Identity/SessionEnd.cs`
  - `src/Platform.Telemetry/RequestTelemetry.cs`, `src/Platform.Telemetry/PlatformTelemetryExtensions.cs`
  - `fixtures/TestIdp/TestIdentityProvider.cs`, `fixtures/TestIdp/TestIdpRecords.cs`, `fixtures/TestIdp/TestIdpFaults.cs`
  - `contracts/telemetry.md`, `contracts/http-contract.md`, `contracts/changes/p2-6-pingfederate-profile.md`, `contracts/changes/p2-5-readiness-reports-registry-revision.md`
  - `spikes/g0/cognito/report-cognito.md`, `spikes/g0/cognito/output-cognito.txt`
  - `docs/gaps.md`, `docs/roadmap.md`, `docs/plan/03-workstreams-and-tasks.md`, `tests/Gateway.IntegrationTests/Functional/ApiTelemetryTests.cs`
- GitHub tracker queries (`gh issue list` / `gh issue view` on 2026-11-27): `#220`, `#429`, `#961`, `#986`, `#994`, `#995`, `#1007`, `#1220`, `#1226`, `#1231`, `#1235`, `#1236`, `#1251`, `#1260`, `#1261`, `#1269`, `#1275`, `#1529`, `#1543`, `#1544`, `#1575`, `#1578`, `#1579`, `#1580`, `#1582`, `#1584`, `#1585`, `#1587`, `#1588`.
- Validation: `tools/check.sh` executed inside the `research/grant-revocation-and-telemetry-inventory` worktree.
- Not executed: PingFederate (prohibited from public testing/reporting per `AGENTS.md`, #1269).

**Read via web search** (all on 2026-11-27):

- RFC 7009, OAuth 2.0 Token Revocation (Abstract, Sections 2.1 and 2.2): https://www.rfc-editor.org/rfc/rfc7009
- Amazon Cognito User Pools — Token revocation and `/oauth2/revoke` endpoint:
  - https://docs.aws.amazon.com/cognito/latest/developerguide/token-revocation.html
  - https://docs.aws.amazon.com/cognito/latest/developerguide/revocation-endpoint.html
  - https://docs.aws.amazon.com/cognito-user-identity-pools/latest/APIReference/API_RevokeToken.html
- PingFederate public documentation (via `docs.pingidentity.com`, no product executed):
  - Configuring authorization server settings (*Reuse Existing Persistent Access Grants for Grant Types*): https://docs.pingidentity.com/pingfederate/latest/administrators_reference_guide/pf_config_authorization_server_settings.html
  - Token revocation endpoint (`/as/revoke_token.oauth2`): `docs.pingidentity.com/pingfederate` Administrator's Reference Guide (indexed via web search on 2026-11-27).
