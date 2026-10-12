# Tests

Thirty-eight automated tests carry the acceptance criteria: eleven functional and twenty-seven negative. Eleven infrastructure and architecture checks sit beside them. The gates table on the Plan tab says which gate runs which ids.

These are separate from the `S-` and `R-` checks of the two feasibility runs. Those are listed in the Evidence tab and become component tests in workstreams 3 and 4.

## How the suites run

| Tier | Runs | Uses | Proves |
| --- | --- | --- | --- |
| Hermetic (H) | In process, on every change, in seconds | The real gateway composition with the test identity provider, the real registry over an in-memory object store, the real mock API and an echo upstream | Logic and security rules, including faults a real provider will not produce on demand |
| Deployed (D) | Through the tunnel, at G3 and after: a real browser, plus a raw HTTP client for requests no browser will send | The ALB, the task, S3, Parameter Store and Cognito | The same behaviour on real infrastructure, plus what only a browser enforces: cookie prefixes, `SameSite`, content security policy |

Each deployed run saves a browser trace, a cookie and storage dump, the gateway log for the run and the Terraform plans. Those files are the evidence attached to a gate checklist.

## Functional tests

| ID | Test | Expect | Tier | Criterion |
| --- | --- | --- | --- | --- |
| F-01 | Signed in, request `/` on two hosts | Two different shells, served by one task behind one ALB | H, D | 1 |
| F-02 | Log in on each app | Each redirects to the provider with its own client id and returns to its own callback | H, D | 2 |
| F-03 | Wait past token expiry, then call the API | The call succeeds after one refresh. In H the provider counts one grant and the stored refresh token has changed. In D the gateway log holds one refresh event. | H, D | 2 |
| F-04 | Log out | The session is gone, the antiforgery cookie is cleared, and the next navigation shows the provider's login page. In H the provider also saw the revocation. | H, D | 2 |
| F-05 | After login, dump cookies, web storage and every response body | Two cookies. No token-shaped string anywhere. In H, none of the token values the provider issued. | H, D | 3 |
| F-06 | Read headers per app, request the fallback matrix, switch release and switch back | App-specific CSP. Extensionless HTML paths return `index.html`; a missing asset returns 404. Content changes with no deployment. | H, D | 5 |
| F-07 | Onboard app 3 while apps 1 and 2 are in use | App 3 is served and logs in with its own client, with no restart. In D the task id, start time and task definition are unchanged, the onboarding plan shows one client, one branding style and one parameter, and the platform plan is empty. The mock API refuses app 3's call, because it has not admitted that client. | H, D | 6 |
| F-08 | Call each mock API route through the gateway, from both apps | `/whoami` reports the right client and scopes on each listener, and the bound audience on `orders`. `/admin` returns 403. | H, D | 7 |
| F-09 | Publish a bad registry, a rotated secret and a removal | The bad document is ignored. The new secret is used. The removed host returns 404 and its sessions are gone. | H | None |
| F-10 | Fifty parallel API calls on an expired token, at a test provider with the 10-second grace window that task 1.8 gives Cognito | All fifty succeed and the session survives. No grant is refused. The provider counts one grant, or more when a late call replays inside the window. | H | 2 |
| F-11 | Follow one API call through the telemetry | The gateway's log line, its spans and the `traceparent` the mock API received share one trace id, and the line names the app | H | None |

F-07 leaves API admission out on purpose. Onboarding gives a new SPA a host, a client and a session. Whether an API accepts that client is the API owner's decision, and the refusal is criterion 7 at work. In the hermetic tier the harness then admits client 3 and the call succeeds.

## Negative security tests

Each row is an attempt that must fail. The deployed tier repeats the rows a browser or real infrastructure can change.

| ID | Attempt | Expect | Tier |
| --- | --- | --- | --- |
| N-01 | Present app 1's session cookie to app 2 | 401 on the API, a fresh challenge on navigation | H, D |
| N-02 | Forge an app 2 cookie that names app 1's session key | 401. The store is bound to app and client. | H |
| N-03 | Replay app 1's callback, with its code, state and login cookies, on app 2 | Rejected. No session created. | H, D |
| N-04 | Inject a code issued to client 1 into app 2's own login | Rejected by the provider at code exchange | H, D |
| N-05 | Send a callback with no correlation cookie, no nonce cookie, or tampered state | Rejected | H, D |
| N-06 | Unsafe API call with no CSRF token, with another user's token, with another app's token and cookie pair, and with a token from before the last logout | 403 `csrf_failed` each time | H, D |
| N-07 | Call `/api` and `/bff` with `Sec-Fetch-Site` of `cross-site`, `same-site` or absent, or a foreign `Origin` | 403 `origin_rejected`. No `Access-Control-Allow-*` header on any response, including preflight. | H, D |
| N-08 | Unknown host, right host with the wrong port, spoofed `X-Forwarded-Host` | 404 for the first two. The spoofed header changes nothing. | H, D |
| N-09 | Send `X-Forwarded-Proto` and `X-Forwarded-For` from an address outside the known networks | Ignored | H |
| N-10 | Call an API name the app does not list, and paths built to escape the prefix | 404 `unknown_api` for the unlisted name, 400 or 404 for the escape paths. No upstream logs the request. | H, D |
| N-11 | Send `Cookie`, a bearer token, the CSRF header and forwarding headers on an API call | The upstream sees none of them. It sees the session's token, and the three forwarding headers as the gateway set them. | H |
| N-12 | Upstream answers with `Set-Cookie`, a CORS header and its own CSP, on a 200 and on a 500 | The browser receives no cookie, no CORS header and the gateway's CSP, both times | H |
| N-13 | Asset paths with dot segments, encoded slashes, backslashes, doubled slashes and null bytes | 400 or 404. No object outside the app's release prefix is read. | H |
| N-14 | Publish a registry with a duplicate host, a disallowed upstream, or a forbidden CSP source | Rejected whole. The live snapshot is unchanged. | H |
| N-15 | `returnUrl` of `//evil.test`, `https://evil.test` and a backslash variant | Redirects to `/` | H |
| N-16 | Run the full suite with canary values for tokens, secrets, cookies, codes and state, then search every log line | No canary appears | H |
| N-17 | Send app 1's token to the `reports` API and app 2's to `orders`, through the second upstream each app lists | The mock API refuses both: wrong client, and on `orders` a missing audience | H, D |
| N-18 | Plant a session cookie, then complete a login | The login creates a new session key. The planted key never carries the new identity. | H |
| N-19 | Unauthenticated call to `/api` and `/bff/session` | 401 with a problem body, never a redirect | H, D |
| N-20 | `TRACE`, `CONNECT`, an unsafe method on an asset path, oversized headers | 405 or 431 | H |
| N-21 | Present a cookie whose session was removed | 401. Decryption succeeds; the store lookup does not. | H |
| N-22 | Use a session after its app was removed or its client replaced | 401 | H |
| N-23 | Inspect every `/bff` response body | No token and no token-shaped string | H, D |
| N-24 | Read framing and embedding headers on a shell, an asset, a `/bff` response and an API error | `frame-ancestors 'none'`, `nosniff` and `Cross-Origin-Resource-Policy` on all four | H, D |
| N-25 | From a sibling host, set each platform cookie for the parent domain, under its exact name and in other letter cases | The browser refuses the exact name. The gateway rejects a request that carries a case variant. | H, D |
| N-26 | Login CSRF: a sibling tries to plant login-state cookies, then sends the victim to a callback carrying the attacker's code and state | The cookies are never set, so the callback is rejected and no session is created | D |
| N-27 | `GET` and `POST` to `/signout-oidc` with a live session | 404. The session survives. | H, D |

N-25 and N-26 rest on the browser enforcing the `__Host-` prefix, so their deciding run is in a real browser. The hermetic client enforces no prefixes: there a planted cookie with the exact name is accepted, as R-D3 in the Evidence tab shows.

## Infrastructure and architecture tests

These hold the properties that no gateway code can break, because they are checked outside it.

| ID | Check | Expect |
| --- | --- | --- |
| P-01 | Platform plan allowlist | Exactly one ALB, one listener, one target group, one ECS service, one bucket. No NAT gateway, database or EC2 instance. |
| P-02 | Apps plan allowlist | A new app adds one Cognito client, one branding style and one parameter, nothing else |
| P-03 | Task role policy | Read on this cell's bucket prefixes and client-secret parameter path, the four `ssmmessages` permissions, and nothing else. No write to the registry. |
| P-04 | Release immutability | S3 refuses a second write to an existing release key, and refuses the delete that would allow one |
| P-05 | Teardown | A query for the platform tag returns no resources |
| P-06 | Network reachability | The task group admits 8080 from the ALB group only. A connection from outside to the task's public address on 8080 times out. |
| A-01 | Gateway references | The gateway never references the test identity provider |
| A-02 | Mock API references | The mock API references no `Platform.*` project |
| A-03 | Workstream references | A workstream project references `Platform.Abstractions` and no other workstream. Only `Gateway` references them all. |
| A-04 | Token library scope | Only `Platform.Identity` references the token-management package |
| A-05 | Path ownership | A change that touches a path outside its owner's set fails CI |

P-01 to P-03 read Terraform plans in CI. P-04 to P-06 run against the deployed stack at G3. P-05 tears the stack down, so G3 ends with a second deployment from nothing, which also shows the first can be repeated. The A checks run on every change.
