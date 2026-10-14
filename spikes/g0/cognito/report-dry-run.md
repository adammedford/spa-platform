> **DRY RUN.** Rendered from a run against `fixtures/TestIdp`, not Cognito. It proves the harness and the report writing.
> It is not G0 evidence, and nothing here may be cited as an answer to item 2 or item 3.

Run: `spikes/g0/cognito/output-dry-run.txt`, driver `playwright`. The check ids `C-<item>.<n>` are listed in that file; in this run they name no evidence.

| G0 item | Verdict | Where |
| --- | --- | --- |
| 1. The shipped handler, with the overrides | Passed on 11 October 2026 (cited, not run again: Q8) | `real-handler/RealOidcNuGet/output-nuget.txt` |
| 2. Two OAuth clients complete login, callback, refresh and logout | DRY RUN, not evidence: passed: both OAuth clients completed login, callback, refresh and logout | `cognito/output-dry-run.txt`: C-2.4, C-2.5, C-2.8, C-2.10, C-2.11, C-2.12, C-2.15, C-2.16 |
| 3. A callback URL with a port; managed login for a client Terraform made | DRY RUN, not evidence: not decided: C-3.4, C-3.5 skipped in this mode | `cognito/output-dry-run.txt`: C-3.1, C-3.2, C-3.3, C-3.4, C-3.5 |
| 4. Duende.AccessTokenManagement under derived schemes | Passed on 11 October 2026 (cited, not run again: Q8). This dry run used its configuration against the test identity provider only | `token-and-proxy/output-token-and-proxy.txt` |
| 5. YARP 2.3.0 on .NET 10 | Passed on 11 October 2026 (cited, not run again: Q8) | `token-and-proxy/output-token-and-proxy.txt` |

| Item 2 question | Evidence | Answer | Route |
| --- | --- | --- | --- |
| What does a rotated-out refresh token get inside the 10-second grace window? | C-2.17 | DRY RUN, not evidence: not-rep: rotation granted, a new refresh token: yes; the replay at 2.0 s: granted, returning new tokens and a different refresh token; the successor afterwards: granted | the window covers a replay: task 3.8 stays on the library's token manager |
| Does a replay after the window harm the current token? | C-2.18 | DRY RUN, not evidence: not-rep: rotation granted; the replay at 12.0 s: refused 400 invalid_grant; the successor afterwards: granted | a late replay is refused and the successor keeps working: the expected behaviour, not a finding |
| Does revoking a refresh token revoke its successor? | C-2.19 | DRY RUN, not evidence: FINDING: rotation granted; revoking the rotated-out token at http://127.0.0.1:60352/revoke: accepted (form body 200); the successor afterwards: granted | Q12: task 3.9 revokes the refresh token it reads from the session record at logout time, not the one held at the start of the request. The T-B18 race stays a documented residual: the successor lives until it expires, held only by the server, whose session record is deleted |
| How long are refresh tokens, at login and after a refresh? | C-2.20 | DRY RUN, not evidence: not-rep: app1 refresh token 38 chars at login, 38 after 1 refresh, 38 after 5; access token 765 chars; ID token 922 chars; app2 refresh token 38 chars at login, 38 after 1 refresh, 38 after 5; access token 718 chars; ID token 922 chars | every refresh token fits the library's limit |
| Does `aud` survive a refresh that sends no `resource`? | C-2.9 | DRY RUN, not evidence: not-rep: aud at login: https://orders.spa-poc.test; after the library's refresh, which sends no resource: https://orders.spa-poc.test | task 3.8 leaves resource off the refresh (T-B12) |
| Does discovery list a revocation endpoint? | C-2.3 | DRY RUN, not evidence: FINDING: no revocation_endpoint | Q16: I-6 adds an IIdpProfile member, and the cognito profile supplies {BrowserOrigin}/oauth2/revoke; task 3.9 deletes the session whether or not revocation worked |

| Also recorded | Evidence | Answer | Route |
| --- | --- | --- | --- |
| What discovery advertises for logout | C-2.2 | DRY RUN, not evidence: FINDING: no end_session_endpoint | the cognito profile's logout, as ADR-0011 has it: {BrowserOrigin}/logout with client_id and logout_uri, landing on / (R-C7). This harness logs out that way |
| Resource binding sets `aud` (Q15) | C-2.7 | DRY RUN, not evidence: not-rep: app1 sent resource at authorize: yes; app1 aud=https://orders.spa-poc.test, client_id=present, scope=openid profile https://orders.spa-poc.test/read, token_use=access, client_id is app1's client; app2 aud=(none), client_id=present, scope=openid profile https://reports.spa-poc.test/read, token_use=access, client_id is app2's client | the plan's assumption holds: aud carries the resource |
| Client credential style (Q17) | C-2.21 | DRY RUN, not evidence: not-rep: code exchange in the form body (the handler, every login): yes; in a Basic header: yes (ok); refresh in the form body (the library and raw): yes; in a Basic header: yes (granted); revocations through the profile route and Q12 accepted with: form body, form body, form body | both styles work: task 3.8 keeps ClientCredentialStyle.PostBody, so the code exchange and the refresh agree |
| Logout is per app against a global provider logout (Q19) | C-2.6, C-2.14 | DRY RUN, not evidence: FINDING: asked for a password: no; DRY RUN, not evidence: not-rep: the logout landed on /, which challenged at once; asked for a password: no | the provider's session survived app1's logout |
| Logout's revocation took effect | C-2.13 | DRY RUN, not evidence: not-rep: a refresh with it afterwards: refused 400 invalid_grant | logout's revocation took effect at the provider |
