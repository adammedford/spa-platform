# Phase 2 and risks

Phase 2 moves sessions and keys out of process memory, then proves the platform under failure and against PingFederate.

## Phase 2 work packages

Each package ends in a test, not a review. Three gates group them: G6 is multi-task continuity, G7 is PingFederate and G8 is enterprise readiness. The first five packages are one body of work, making the gateway safe to run as more than one task.

| Package | Gate | What changes | Accepted when |
| --- | --- | --- | --- |
| P2-1 Durable sessions | G6 | `ISessionStore` on DynamoDB. The key is a hash of the session key, the ticket is envelope-encrypted, expiry uses the table's time-to-live, and replace is versioned. | The store contract suite passes. Killing a task loses no session. |
| P2-2 Shared key ring | G6 | Data-protection keys persisted to Parameter Store under a KMS key, through the AWS provider for ASP.NET Core data protection, with one application name per cell | A cookie and a login state issued by one task are accepted by another |
| P2-3 Distributed refresh and revocation | G6 | One refresh per session across tasks: a short lease taken with a conditional write, and losers re-read. The provider's rotation grace period is the backstop. Expiry and revocation hold on every task at once. | Fifty parallel calls split across two tasks cause one refresh grant and no refused grant. A session ended through one task is refused by the other on its next request. |
| P2-4 Multi-task service | G6 | Two or more tasks across zones, private subnets, no public address, controlled egress | A rolling deployment and a killed task, with live sessions, cause no sign-out and no failed login |
| P2-5 Registry convergence | G6 | Onboarding and rotation wait until every task reports the revision | A login that starts on one task and finishes on another succeeds during a reload |
| P2-6 PingFederate | G7 | A profile for PingFederate, with decisions on `form_post`, pushed authorization, `private_key_jwt`, audience, logout and back-channel logout | The functional and negative suites pass against PingFederate |
| P2-7 Corporate integration | G8 | Corporate certificates on the ALB and for upstream trust, the enterprise secret store, telemetry export and audit events to the security tooling, deployment approvals, per-team publishing rights, registry changes by reviewed pull request | Evidence recorded per control |
| P2-8 Security cells | G8 | The cell module instantiated per risk class, apps assigned to cells by data classification and criticality, and IAM, network and data separated per cell | A compromise drill in one cell reads nothing from another |
| P2-9 Hardening | G8 | No ECS Exec, read-only root filesystem, non-root user, minimal image, egress allowlist enforced by the network | Scanner and policy checks pass |
| P2-10 Assurance | G8 | Threat model review, penetration test, recovery objectives, rehearsed runbooks, load test | Findings closed or accepted in writing |

### The brief's Phase 2 acceptance, mapped

| Criterion | Covered by |
| --- | --- |
| Multi-replica session continuity | P2-1, P2-2, P2-4 |
| Safe token rotation | P2-3 |
| Isolation under failures | P2-4, P2-8 |
| Observable enforcement | Every rejection already carries a rule id, a metric and a log line. P2-7 sends them to the security tooling. |
| Reproducible deployment and rollback | P2-7, and the Phase 1 scripts run from a pipeline |
| No per-SPA runtime resources | Plan policy P-02 stays in CI for every cell |

Two Phase 2 options would remove whole categories of work. `private_key_jwt` with one gateway key replaces every per-app client secret. `form_post` keeps the authorization code out of URLs and access logs. It turns the callback into a cross-site POST, so the login-state cookies would need `SameSite=None`; the `__Host-` prefix still protects them.

## Enterprise gaps

What Phase 1 does not show, stated plainly so nobody reads it as production evidence.

| Area | Phase 1 state | Closed by |
| --- | --- | --- |
| Availability | One task. A deployment or crash interrupts everyone. | P2-4 |
| Session durability | In memory. Lost on restart. | P2-1 |
| Key durability | In memory. Cookies and in-flight logins die with the task. | P2-2 |
| Revocation | A removed session stops working at once, but only because there is one task | P2-3 |
| Network | Public address on the task, unrestricted outbound 443 | P2-4, P2-9 |
| Operator access | ECS Exec enabled, so an operator can reach a shell in the gateway | P2-9 |
| Identity provider | Cognito only. Non-standard logout. Authorization code in the URL. | P2-6 |
| Secrets | Parameter Store with manual rotation | P2-7 |
| Publishing | One publisher, the deployer, writes every app's releases | P2-7 |
| Telemetry and audit | One log stream. No trace or metric export, no ALB access logs. | P2-7 |
| Registry governance | One writer controlled by IAM. No review step. | P2-7 |
| Isolation | One cell for every app | P2-8 |
| Performance | Unmeasured. Every asset request reads S3. | P2-10 |
| Assurance | No penetration test, no formal threat model review | P2-10 |

## Risks

Likelihood and impact are my judgement, not measurements. Sorted by impact.

| # | Risk | Likelihood | Impact | Response |
| --- | --- | --- | --- | --- |
| 1 | The shared gateway process is compromised | Low | Very high | Accepted for Phase 1 with least-privilege IAM. Cells in Phase 2. |
| 2 | Cognito behaves differently from the hermetic runs. The shipped handler matched them on 11 October 2026. | Low | High | G0 runs before any identity contract freezes. Its failure path is in the Plan tab. |
| 3 | A sibling SPA finds a gap in the request gate | Low | High | A missing header is a rejection. Antiforgery is a second layer. N-06 and N-07 run in both tiers. |
| 4 | A browser treats cookie prefixes loosely, and a sibling plants a platform cookie | Low | High | Current managed browsers are assumption A2. The gateway also rejects case variants of its cookie names. N-25 and N-26 run in a real browser. |
| 5 | The registry is poisoned or lost | Low | High | Validation, last-known-good, versioning and a single writer. Review in Phase 2. |
| 6 | A late call replays a rotated-out refresh token, which the token library's own locking does not prevent | Medium | Medium | Cognito's 10-second grace window, a re-read before a refusal ends the session, and no request cancellation on the token call. G0 item 2 confirms the window. Gateway-owned locking over the library's refresh call is the reserve design. The scheme-pinning risk this row first named passed its G0 test on 11 October 2026. |
| 7 | PingFederate differs: opaque tokens, logout, audience | Medium | Medium | The profile seam and a separate gate. Nothing outside the profile depends on Cognito. |
| 8 | Agents collide on shared files | Medium | Medium | One owner per path, integrator-only files, and a path-ownership check in CI |
| 9 | The hand-written test provider drifts from how real providers behave | Medium | Medium | The functional suite and N-03 to N-05 also run against Cognito. OpenIddict is the alternative. |
| 10 | S3 latency or an outage affects every page load | Medium | Medium | Accepted in Phase 1. Caching in Phase 2. |
| 11 | Hundreds of apps strain options memory, secret preload or registry size | Low | Medium | The spike ran 303 apps. Only changed secret references are preloaded. Load test in Phase 2. |
| 12 | YARP 2.3 misbehaves on .NET 10 | Low | Medium | A G0 smoke test. YARP's main branch, a 3.0 preview that is not on nuget.org, can be built from source if needed. |
| 13 | Browser behaviour differs from the plan: `form-action` on redirects, cookie rules | Medium | Low | Deployed browser tests on login and logout |
| 14 | Recent Cognito features lag in Terraform: resource binding, refresh rotation, second secrets, branding styles | Medium | Low | Scripts fall back to the AWS CLI, and CloudFormation has a resource type for the branding style. G0 confirms the behaviour itself. |
| 15 | The tunnel is fragile: sessions time out, ECS Exec has constraints | Medium | Low | Proof of concept only. `verify-tunnel.sh` detects a dead tunnel. |

## Deferred features

Left out on purpose. Each names where it would land, so the contracts leave room for it.

| Feature | Why it waits | Where it lands |
| --- | --- | --- |
| Token exchange, and several audiences per app | The brief excludes it from Phase 1 | `IAccessTokenSource` gains a resource argument |
| A public shell per app, with login started by the SPA | Every app in scope is internal. It would add a second, untested path through the shell gate. | Hosting and the shell gate |
| WebSockets and server-sent events through the proxy | Needs an `Origin` rule for upgrades and a decision on long-lived connections | Proxy |
| Back-channel logout, and ending every session for one user | Needs a session index by subject and provider support | Session store, decided with P2-6 |
| Idle timeout and step-up authentication | Policy decisions the brief does not make | Cookie options and the profile |
| Per-route authorization at the gateway | The APIs already validate for themselves | The `apis` section of an entry |
| Rate limiting | Internal network with no abuse case yet | Request gate |
| Asset caching, compression tuning, range requests | Correctness first | Hosting |
| Old-asset continuity across a release switch | A page loaded before a switch can request an asset the new release lacks | The entry gains retained releases, and hosting falls back for immutable paths |
| Canary releases per app | Needs sticky assignment of users to releases | Registry and hosting |
| Sender-constrained tokens and pushed authorization | They depend on the provider and are a PingFederate decision | The profile, in Phase 2 |
| A published package for the SPA helper | One file is enough for three fixtures | Fixtures |
| Per-app error pages | Cosmetic | Hosting |
