# ADR-0006: Generate YARP routes from the registry

- **Status:** Accepted. Confirmed by Gate G0 item 5 on 11 October 2026.
- **Date:** 2026-10-11
- **Deciders:** integrator
- **Plan ref:** `docs/plan/01-plan.md` → Decisions, D6 (Doc revision 36)

## Context

`/api/{name}/*` must reach one approved upstream carrying the session's access token. Threat T5 is
the proxy being steered to an unapproved destination, which is the classic server-side request
forgery shape for a gateway.

## Decision

YARP routes are generated from the registry: host plus `/api/{name}/**` to a fixed upstream, bounded
by `Policy:AllowedUpstreams` in platform configuration so a registry writer cannot widen it. An
entry's `upstream` must have no path, query or credentials. Redirects are passed through, never
followed.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Destinations derived from the request | Any request input reaching destination selection makes the proxy steerable. |

## Consequences

- No request input reaches destination selection, so the proxy cannot be steered.
- An unlisted API name matches no route; the 404 `unknown_api` comes from a catch-all behind the proxy.
- YARP forwards an encoded slash or backslash, a `..;` segment and a doubled slash to the app's own upstream. None reached another app's upstream, and the request gate covers the escape paths.
- Enumerating a YARP request's typed headers rewrites some values, so the header-removal transform reads them unvalidated.
- YARP's activity timeout limits silence, not the whole exchange: 504 before the upstream's headers, 502 for headers followed by silence.
- An app with several upstreams sends the same token to each, which is safe only inside one trust domain. The validator warns when an entry lists more than one upstream origin.
- The shipped Yarp.ReverseProxy 2.3.0 assembly targets .NET 8 and ran on the .NET 10.0.12 runtime. Had item 5 failed, the fallback was building YARP's main branch (a 3.0 preview, not on nuget.org) from source.

## Evidence

G0 item 5; findings T-C4, T-C9, T-C10, T-C17, T-C18. Tests N-10, N-12.
