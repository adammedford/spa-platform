# ADR-0001: Resolve per-app schemes by host through a registry-backed scheme provider

- **Status:** Accepted. Confirmed by Gate G0 item 1 on 11 October 2026; items 2 and 3 (Cognito) remain open.
- **Date:** 2026-10-11
- **Deciders:** integrator
- **Plan ref:** `docs/plan/01-plan.md` → Decisions, D1 (Doc revision 36)

## Context

The platform serves many SPAs from one gateway process, each with its own OAuth client, and must
onboard, rotate and remove clients without a restart. ASP.NET Core asks the scheme provider for
every request-handling scheme on every request and constructs each handler
(`AuthenticationMiddleware.Invoke`). Registering every app's schemes globally therefore costs one
handler per app per request, and lets any host name any app's scheme. `IOptionsMonitor` alone adds
no schemes at all, and options cannot add or remove schemes.

## Decision

Replace `IAuthenticationSchemeProvider` with `RegistrySchemeProvider`. It resolves the app from the
request authority, pinned once per request against the current registry snapshot, and returns that
app's `oidc:{appId}` and `cookie:{appId}` as the defaults. Any other scheme name resolves to
nothing. Named options setup implements `IConfigureNamedOptions` for OIDC and cookie options,
reading the registry entry behind the scheme name; the framework's own post-configuration then runs
as usual. `OptionsInvalidator` evicts cached options after each registry swap for entries whose
fingerprint changed or disappeared, in a fixed order: swap the snapshot, then evict.

No handler is subclassed and no middleware is added. About 150 lines in the spike.

Because the framework derives state and cookie protector purposes from the scheme name
(`OpenIdConnectPostConfigureOptions`, `PostConfigureCookieAuthenticationOptions`), per-app scheme
names give per-app encryption purposes with no extra code.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Named schemes added to the stock provider at runtime | The documented API, but every app's handler runs on every request and any host can address any scheme by name. The control run measured 303 handlers per request at 303 apps and 2.3–3.6× the request time. |
| `IOptionsMonitor` reload on one scheme | One scheme is one client. A reload changes it for every host at once. |
| Our own tenant-aware middleware ahead of authentication | Re-implements default-scheme selection and callback dispatch, which the scheme provider already offers as a seam. Custom security code with no natural bound. |
| One shared scheme with per-tenant options (Finbuckle.MultiTenant 10.1) | Workable, but the scheme name is shared, so protector purposes are too unless reconfigured. Held as the first fallback if G0 fails. |
| Duende BFF v4 multi-frontend (commercial) | Uses this same mechanism, which confirms the design. Held as the buy-instead option; it would replace the identity workstream and part of the proxy workstream. |

## Consequences

- A request on one host cannot name another app's scheme.
- Options are read by scheme name once per request, so a request in flight keeps one consistent set and a login that straddles a reload completes.
- Eviction must be explicit. A failed options build would otherwise be cached under its name, so options setup does no I/O and the name is evicted on failure.
- For the instant between snapshot swap and eviction, a request can still see the previous options.
- Options validation and `SignInScheme` are wired only for statically named schemes, so the platform calls `Validate` itself and sets `SignInScheme` per app.
- Hundreds of apps on one issuer must share one backchannel `HttpClient` and one `ConfigurationManager`, since each options instance would otherwise create its own.
- If G0 item 2 fails with no fix inside the task, identity work stops and the integrator compares Finbuckle, Duende BFF v4 and a dedicated tenant-aware OIDC stack. Unbounded custom middleware is not on that list.

## Evidence

Stand-in run S-A1 to S-A7, S-F2, S-F3; real-handler run R-A1 to R-A6, R-D7. See `docs/plan/07-evidence.md`.
