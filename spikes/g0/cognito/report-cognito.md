### Review of the saved Cognito run

The saved `output-cognito.txt` is the original observation and has not been rewritten or rerun.
The tables below retain its expectation verdicts, with these interpretations corrected after review:

- **C-2.12, C-2.13 and C-2.16:** logout completed and removed the app's session, but the library
  sent revocation with form-body authentication and did not expose its response. C-2.13 shows the
  token still refreshed after that attempt; it does not show that an accepted revocation failed.
  C-2.19 separately observed form-body authentication refused with 401 and Basic authentication
  accepted with 200. [Cognito's revocation documentation](https://docs.aws.amazon.com/cognito/latest/developerguide/revocation-endpoint.html)
  requires Basic authentication for clients with a secret. I-6 must settle that configuration;
  successful logout revocation through the corrected library configuration remains unproven here.
- **C-2.17:** the inside-window replay was granted and its original successor was then refused.
  This is an additional hazard beyond the ticket's original Q11 definition (an inside replay
  refused, or a late replay invalidating its successor). It supports asking I-6 to adopt the
  reserve design; this report does not itself change that decision. C-2.18 is one independent
  late-replay observation and does not bound every possible race or replay outcome.
- **C-2.2:** discovery advertised an end-session endpoint. The standard profile's RP-initiated
  logout was not exercised; the observed logout used the Cognito profile.
- **Cleanup:** all 24 revocation requests received a success response. The run did not try
  refreshing every token afterwards, so the count is acknowledgement evidence, not a separate
  proof that every token became unusable.

This is a spike-host run using the saved package versions and pool configuration, not a deployed
production gateway test. The 19 held expectations and five findings are the original harness's
classifications; the qualifications above control how its generated labels are interpreted.

Run: `spikes/g0/cognito/output-cognito.txt`, driver `playwright`. Evidence ids `C-<item>.<n>` are listed in that file.

| G0 item | Verdict | Where |
| --- | --- | --- |
| 1. The shipped handler, with the overrides | Passed on 11 October 2026 (cited, not run again: Q8) | `real-handler/RealOidcNuGet/output-nuget.txt` |
| 2. Two OAuth clients complete login, callback, refresh and logout | passed: both OAuth clients completed login, callback, refresh and logout | `cognito/output-cognito.txt`: C-2.4, C-2.5, C-2.8, C-2.10, C-2.11, C-2.12, C-2.15, C-2.16 |
| 3. A callback URL with a port; managed login for a client Terraform made | passed: the provider accepted the callback and sign-out URLs with the port, and managed login worked for both clients Terraform made | `cognito/output-cognito.txt`: C-3.1, C-3.2, C-3.3, C-3.4, C-3.5 |
| 4. Duende.AccessTokenManagement under derived schemes | Passed on 11 October 2026 (cited, not run again: Q8). The run above uses its configuration against a real provider | `token-and-proxy/output-token-and-proxy.txt` |
| 5. YARP 2.3.0 on .NET 10 | Passed on 11 October 2026 (cited, not run again: Q8) | `token-and-proxy/output-token-and-proxy.txt` |

| Item 2 question | Evidence | Answer | Route |
| --- | --- | --- | --- |
| What does a rotated-out refresh token get inside the 10-second grace window? | C-2.17 | FINDING: rotation granted, a new refresh token: yes; the replay at 2.0 s: granted, returning new tokens and a different refresh token; the successor afterwards: refused 400 invalid_grant | additional inside-window successor failure: I-6 must decide whether to adopt the reserve design |
| Does a replay after the window harm the current token? | C-2.18 | not-rep: rotation granted; the replay at 12.0 s: refused 400 invalid_grant; the successor afterwards: granted | a late replay is refused and the successor keeps working: the expected behaviour, not a finding |
| Does revoking a refresh token revoke its successor? | C-2.19 | not-rep: rotation granted; revoking the rotated-out token at https://spa-platform-poc-146873694665.auth.us-east-2.amazoncognito.com/oauth2/revoke: accepted (form body refused 401 Unauthorized, then Basic header 200); the successor afterwards: refused 400 invalid_grant | revocation reaches the successor: recorded, and nothing changes |
| How long are refresh tokens, at login and after a refresh? | C-2.20 | not-rep: app1 refresh token 1,752 chars at login, 1,845 after 1 refresh, 1,845 after 5; access token 1,047 chars; ID token 1,217 chars; app2 refresh token 1,690 chars at login, 1,782 after 1 refresh, 1,782 after 5; access token 1,001 chars; ID token 1,217 chars | every refresh token fits the library's limit |
| Does `aud` survive a refresh that sends no `resource`? | C-2.9 | not-rep: aud at login: https://orders.spa-poc.test; after the library's refresh, which sends no resource: https://orders.spa-poc.test | task 3.8 leaves resource off the refresh (T-B12) |
| Does discovery list a revocation endpoint? | C-2.3 | not-rep: revocation_endpoint https://spa-platform-poc-146873694665.auth.us-east-2.amazoncognito.com/oauth2/revoke | the library's revoke call finds the endpoint in discovery; no IIdpProfile member is needed for it |

| Also recorded | Evidence | Answer | Route |
| --- | --- | --- | --- |
| What discovery advertises for logout | C-2.2 | not-rep: end_session_endpoint https://spa-platform-poc-146873694665.auth.us-east-2.amazoncognito.com/logout | discovery advertises the endpoint; standard-profile RP-initiated logout was not exercised |
| Resource binding sets `aud` (Q15) | C-2.7 | not-rep: app1 sent resource at authorize: yes; app1 aud=https://orders.spa-poc.test, client_id=present, scope=openid profile https://orders.spa-poc.test/read, token_use=access, client_id is app1's client; app2 aud=(none), client_id=present, scope=https://reports.spa-poc.test/read openid profile, token_use=access, client_id is app2's client | the plan's assumption holds: aud carries the resource |
| Client credential style (Q17) | C-2.21 | FINDING: code exchange in the form body (the handler, every login): yes; in a Basic header: yes (ok); refresh in the form body (the library and raw): yes; in a Basic header: yes (granted); revocations through the profile route and Q12 accepted with: Basic header | Q17: the style that works becomes the cognito profile's setting, for the code exchange, the refresh and the revocation alike |
| Logout is per app against a global provider logout (Q19) | C-2.6, C-2.14 | FINDING: asked for a password: no; FINDING: the logout landed on /, which challenged at once; asked for a password: yes | none for the gateway: its logout is per app (C-2.15), the provider's is global. I-6 records it under the glossary's logical boundary |
| Token use after an unconfirmed logout revocation attempt | C-2.13 | FINDING: a refresh with it afterwards: granted | the library's PostBody attempt had no observed response; accepted logout revocation is not proven. I-6 must settle the credential style; the app's session was removed |
