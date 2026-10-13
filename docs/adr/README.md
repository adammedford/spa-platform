# Architecture decisions

One file per decision, numbered, in the format every file here follows: status, date, deciders,
plan ref, then Context, Decision, Alternatives rejected, Consequences, Evidence.

ADR-0001 to ADR-0012 restate decisions D1 to D12 from the Plan tab, which is exported to
`docs/plan/01-plan.md`. The plan is a **frozen export** whose source of truth is the Claude Doc, so
it is never edited in place. These files are the **live decision record**: a decision made or
revised after the 11 October 2026 export belongs here, as a new ADR or a status change on an
existing one, not as an edit to `docs/plan/`.

Owner: integrator (`docs/plan/01-plan.md` → Workstreams).

| ADR | Decision | Plan |
| --- | --- | --- |
| [0001](0001-host-resolved-scheme-provider.md) | Resolve per-app schemes by host through a registry-backed scheme provider | D1 |
| [0002](0002-registry-as-versioned-s3-document.md) | Keep the registry as one versioned JSON document in S3 | D2 |
| [0003](0003-client-secrets-in-parameter-store.md) | Reference client secrets in SSM Parameter Store by name and version | D3 |
| [0004](0004-server-side-sessions-via-ticketstore.md) | Keep sessions in the cookie handler with `ITicketStore` and `SaveTokens` | D4 |
| [0005](0005-token-management-behind-iaccesstokensource.md) | Use Duende.AccessTokenManagement behind our own `IAccessTokenSource` | D5 |
| [0006](0006-yarp-routes-generated-from-registry.md) | Generate YARP routes from the registry | D6 |
| [0007](0007-host-prefix-and-fetch-metadata-over-samesite.md) | Defend across apps with `__Host-` cookies, Fetch Metadata and antiforgery, not `SameSite` | D7 |
| [0008](0008-session-required-before-any-asset.md) | Require a session before serving any asset | D8 |
| [0009](0009-immutable-release-prefixes.md) | Publish releases as immutable prefixes named by the registry | D9 |
| [0010](0010-wildcard-ingress-with-no-listener-rules.md) | Serve every host from one wildcard listener with no listener rules | D10 |
| [0011](0011-idp-differences-behind-iidpprofile.md) | Put identity-provider differences behind `IIdpProfile`, chosen per issuer | D11 |
| [0012](0012-cell-as-deployment-unit.md) | Make the cell the deployment unit | D12 |
| [0013](0013-platform-tag-and-bootstrap-exemption.md) | Name the platform tag `Platform = spa-platform`, and exempt the bootstrap root | — |

Three decisions carry open gate work: ADR-0001 (G0 items 2 and 3, Cognito), ADR-0005 (the reserve
refresh design reopens if G0 item 2 finds Cognito's grace window insufficient) and ADR-0011 (G0
item 2 records Cognito's logout and revocation endpoints).
