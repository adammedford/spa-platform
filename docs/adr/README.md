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
| [0014](0014-teardown-role-in-bootstrap.md) | Create the teardown role in `infra/bootstrap`, and derive its name from the cell | — |
| [0015](0015-shared-constants-across-a-workstream-boundary.md) | Keep a contract-fixed shared constant in `Platform.Abstractions`, and a dependency-measured one where it is measured | — |
| [0016](0016-plan-policy-without-aws-credentials.md) | Assert plan policy with mock providers, and keep AWS credentials out of CI | — |
| [0017](0017-g0-findings-fold-into-the-identity-contracts.md) | Fold Gate G0's Cognito findings in: adopt the reserve refresh design, and put the client credential style on the profile | — |
| [0018](0018-telemetry-is-the-outermost-stage.md) | Put `UsePlatformTelemetry` first, so every rejection the application answers is logged on a request line, and all but the method allowlist's 405 are counted; name stages, never number them, outside the pipeline table | — |

Those three open items closed on 2026-10-16, when Gate G0's items 2 and 3 passed against Cognito.
ADR-0017 records what the run found and amends each of them: ADR-0001's mechanism held, ADR-0005's
reserve refresh design is now **adopted** rather than held in reserve, and ADR-0011's profile gains
a client credential style. No decision carries open gate work at G0 now; the next gate work is
ADR-0011's PingFederate profile at G7.
