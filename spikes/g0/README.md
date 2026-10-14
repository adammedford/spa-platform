# G0 harness

The console programs behind the plan's Evidence tab (`docs/plan/07-evidence.md`).
Each drives ASP.NET Core 10's own authentication middleware with OAuth clients that are
loaded, rotated and removed at runtime, prints one line per check, and exits non-zero if
an expectation fails.

This folder is outside the solution. `spikes/Directory.Build.props` and
`spikes/Directory.Packages.props` keep it apart from the solution's build settings.
Task 3.0 extends it for Gate G0: `cognito/` runs items 2 and 3 against Cognito, and section 5
says how. `token-and-proxy/Idp.cs` is the starting point for the test identity provider in task 5.4.

| Folder | What it runs | Result on .NET 10.0.12 |
| --- | --- | --- |
| `stand-in-spike/` | The host-scoped scheme provider with a stand-in OIDC handler and a fake authorization server. No package references. | 34 of 34 checks in every run |
| `real-handler/RealOidc/` | The same mechanism with the real `OpenIdConnectHandler` and a hermetic OpenID Connect provider. Framework defaults, so the probes report what is wrong with them. | 32 expectations held, 9 findings |
| `real-handler/RealOidcHardened/` | The same sources with four overrides made the defaults. | 32 expectations held, 8 findings still report |
| `real-handler/RealOidcNuGet/` | The hardened sources against the shipped NuGet package. | 32 expectations held, 8 findings still report. Same verdict on all 41 checks as the source build. |
| `token-and-proxy/` | The hardened mechanism with `Duende.AccessTokenManagement.OpenIdConnect` 4.2.0 and `Yarp.ReverseProxy` 2.3.0 added, for Gate G0 items 4 and 5. | 43 expectations held, 23 findings, 1 probe not reproduced |
| `cognito/` | The same mechanism and token library against Cognito, with Playwright Chromium driving managed login, for Gate G0 items 2 and 3. | Not yet run against Cognito (task 3.0b). Its dry run against `fixtures/TestIdp` is recorded and is not evidence. |

Recorded output is beside each program: `spike-output.txt`, `output.txt`, `output-hardened.txt`, `output-nuget.txt`, `output-token-and-proxy.txt`.
`cognito/output-dry-run.txt` is a dry run against the test identity provider, not evidence; `cognito/output-cognito.txt` will be the Cognito run.

## 1. Stand-in spike

Needs the .NET 10 SDK and nothing else.

```bash
cd spikes/g0/stand-in-spike
dotnet run -c Release
```

## 2. Real handler from the NuGet package (Gate G0, item 1)

The first item of Gate G0. It passed on 11 October 2026 on a Mac with Apple silicon,
.NET SDK 10.0.401 and runtime 10.0.12. `output-nuget.txt` is that run.

```bash
cd spikes/g0/real-handler/RealOidcNuGet
dotnet run -c Release
```

The project compiles the files in `../RealOidcHardened/` and references
`Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.12, the version pinned in
`Directory.Packages.props`. The harness built unchanged. A run prints:

- a first line that names the handler assembly `Microsoft.AspNetCore.Authentication.OpenIdConnect`
  and reports IdentityModel 8.19.2
- a last line that starts with `32 expectations held, 0 did not`

Each of the 41 checks gave the same verdict as the source build of section 3. The detail
lines differ only in the handler's assembly name, random cookie suffixes, timestamps and
port numbers. The package resolved all seven IdentityModel assemblies at 8.19.2, so the
two protocol assemblies were shipped binaries as well.

`dotnet run` also prints one build warning, ASP0000, ahead of the harness output. It comes
from a probe that calls `BuildServiceProvider`, and it does not affect the result.

## 3. Real handler built from source (the first run, offline)

Only needed to reproduce the offline run exactly. The `vendor` inputs are not in the
repository, because they are upstream source and Microsoft binaries. `.gitignore` keeps
them out.

1. Fetch the two sources.

   ```bash
   cd spikes/g0/real-handler
   git clone --filter=blob:none --sparse https://github.com/dotnet/aspnetcore vendor/aspnetcore
   git -C vendor/aspnetcore sparse-checkout set src/Security/Authentication/OpenIdConnect/src
   git -C vendor/aspnetcore checkout 9aaf828061dd5dc00e2dfeec654568e5ee0168ce   # release/10.0

   git clone --filter=blob:none --branch 8.19.2 \
     https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet vendor/idm
   ```

2. Copy the five IdentityModel assemblies that ship inside the SDK into `vendor/lib/`.
   They sit in the `dotnet-user-jwts` tool folder. On the Ubuntu 24.04 SDK that is
   `/usr/lib/dotnet/sdk/10.0.112/DotnetTools/dotnet-user-jwts/10.0.12-servicing.26422.108/tools/net10.0/any/`.
   On macOS look under `/usr/local/share/dotnet/sdk/<version>/DotnetTools/dotnet-user-jwts/`.

   ```
   Microsoft.IdentityModel.Abstractions.dll   Microsoft.IdentityModel.JsonWebTokens.dll
   Microsoft.IdentityModel.Logging.dll        Microsoft.IdentityModel.Tokens.dll
   System.IdentityModel.Tokens.Jwt.dll
   ```

   The Ubuntu SDK carries version 8.19.2, which is why step 1 checks out the 8.19.2 tag.
   If your SDK carries another version, check out the matching tag instead.

3. Make the compile-time reference copies. The protocol assemblies use internals of
   the core ones, so they compile against copies with those internals made visible.
   The assemblies that run are the unmodified originals.

   ```bash
   mkdir -p vendor/ref
   for f in vendor/lib/*.dll; do
     dotnet run --project tools/Publicize -- "$f" "vendor/ref/$(basename "$f")"
   done
   ```

4. Run either harness.

   ```bash
   cd RealOidc && dotnet run -c Release            # framework defaults: 32 held, 9 findings
   cd ../RealOidcHardened && dotnet run -c Release # overrides on:       32 held, 8 findings
   ```

An independent reviewer made the `vendor` folder first. I then replayed steps 1 to 4 from
an empty `vendor` folder on Linux, using only the files here. Both harnesses gave the
recorded verdict on every check. The macOS path in step 2 has not been tried.

What `vendor/AspNetOidc` adds to the handler source is two support files: `Resources.cs`
holds the eight message strings the ASP.NET Core build generates from `Resources.resx`,
and `Shared/StringHelpers.cs` is a shared helper the handler links in. No handler file is
changed.

## 4. Token management and YARP (Gate G0, items 4 and 5)

Items 4 and 5 of Gate G0. Both passed on 11 October 2026 on a Mac with Apple silicon and
runtime 10.0.12, against the shipped packages. `output-token-and-proxy.txt` is that run:
43 expectations held, none failed, 23 findings, and one probe not reproduced.

```bash
cd spikes/g0/token-and-proxy
dotnet run -c Release 2>&1 | tee output-token-and-proxy.txt
```

The project compiles `Registry.cs`, `Auth.cs` and `Browser.cs` from
`../real-handler/RealOidcHardened/` unchanged and adds four files of its own:

| File | What it is |
| --- | --- |
| `Idp.cs` | A test identity provider with the refresh grant, rotating refresh tokens, a grace window and revocation |
| `Upstream.cs` | An API that answers with what it received |
| `Gateway.cs` | The gateway with both packages, and the knobs the checks turn |
| `Program.cs` | The checks |

It references `Duende.AccessTokenManagement.OpenIdConnect` 4.2.0 and `Yarp.ReverseProxy`
2.3.0, the versions pinned in `Directory.Packages.props`. A run takes about half a minute.

| Section | What it covers |
| --- | --- |
| A | The token library configured as task 3.8 says: a sentinel scheme, and both scheme names on every call |
| B | The same library's defaults and edges |
| C | YARP doing what the upstream contract and task 4.2 ask |
| D | The reserve design for task 3.8, not adopted: the gateway owns the locking, and uses the library for the refresh and revoke calls only |

`dotnet run -c Release -- C` runs one section.

Reading the output:

- **Ten checks are the gate.** A0 to A3 and B1 to B3 answer item 4. C0 to C2 answer item 5.
  Every other line is a finding for the task it touches. The plan's Evidence tab
  (`docs/plan/07-evidence.md`) lists them under ids that start `T-`.
- **A0 and C0 say which builds are loaded.** They pass only on the shipped packages: the
  4.2.0 token-management assemblies, and the YARP 2.3.0 assembly built for .NET 8 on a
  .NET 10 runtime.
- **`FINDING` is not a failure.** It marks a probe that shows something the plan must
  allow for. Only `BAD` fails the run.
- **C14 is a rate.** It repeats a burst of calls 100 times and reports how often a race
  showed. On a machine where the race does not show, it prints `not-rep`. It did not show
  on the Mac. On Linux, against source builds of both libraries, it showed in 3 to 18
  rounds of 100.
- **The test provider's rules are its own.** Where a result depends on them (B7, B18, D9
  and D10), the check says so and names G0 item 2, which asks Cognito.
- **`/bff/token` is a diagnostic for the checks.** It hands a token to its caller, which the
  platform never does. Nothing in `src/` copies it.

## 5. Cognito (Gate G0, items 2 and 3)

Items 2 and 3 of Gate G0, built by task 3.0a and run by task 3.0b. The decisions behind it are
the 23 questions in the ticket's grilling record (Q1 to Q23); this section cites them by number.

The harness signs in through Cognito's managed login with Playwright Chromium, runs the gateway
as tasks 3.8 and 3.9 configure it, and makes the raw token-endpoint calls the item 2 questions
need. It reads its configuration from the infra outputs document and `fixtures/apps/*.json`, and
its secrets from Parameter Store at run time. It creates nothing in AWS (Q23): it describes the two
OAuth clients that task 1.8 made, and at the end it revokes every refresh token it obtained.

### Prerequisites (Q9)

The run checks all of these before it starts anything, and refuses with exit code 3, naming each
one that is missing. It binds nothing and writes no file when it refuses.

1. **Task 1.1 applied**: `infra/envs/poc/idp`, the user pool, its managed-login domain and the test users.
2. **Task 1.8 applied**: `infra/envs/poc/apps`, an OAuth client, a branding style and a secret parameter for
   app1 and app2, with the manual code-flow login its README describes.
3. **`certs.sh` run on this workstation** (task 1.6a): `tools/scripts/certs.sh issue`, then `trust`. The harness
   serves `https://app{1,2}.spa-poc.test:8443` with that certificate, from `.certs/` or `SPA_CERTS_DIR`.
4. **The hosts-file line**: `127.0.0.1 app1.spa-poc.test app2.spa-poc.test app3.spa-poc.test`.
5. **Local port 8443 free.** The harness takes 8443 itself, so stop `tunnel.sh` first.
6. **AWS credentials** as in assumption A7, able to read the two client-secret parameters and the test users'
   password parameters, and to call `cognito-idp:DescribeUserPoolClient` and
   `cognito-idp:DescribeManagedLoginBrandingByClient`. The harness reads; it writes nothing.

It also needs Chromium for Playwright 1.63.0 (revision 1243), unless a person signs in by hand with `--manual`.

### Running it (task 3.0b)

```bash
cd spikes/g0/cognito
./outputs-from-terraform.sh                    # the outputs document's identity fields, into outputs.local.json
dotnet run -c Release -- --install-browser     # once: Chromium for Playwright, with no PowerShell
dotnet run -c Release -- --cognito             # the run: about seven minutes, five of them waiting for a token to age
```

`outputs-from-terraform.sh` assembles `outputs.local.json` from the two roots' outputs, in the shape of
`contracts/infra-outputs.schema.json`, because task 1.7's `infra/envs/poc/outputs.json` needs the platform
root applied too. The harness reads `infra/envs/poc/outputs.json` instead when it exists, or `--outputs PATH`.
It reads only the identity fields (`region`, `issuer`, `cognitoDomain`, `clients`, `testUsers`) and holds
each to its rule in the contract. `outputs.local.json` is ignored by git, and it names parameters, never values.

Other switches: `--manual` (a person signs in, in their own browser, when Chromium will not install), `--headed`
(watch Chromium), `--user` and `--second-user` (default `alice` and `bob`), `--skip-natural-expiry` (drops the
five-minute wait, and C-2.11 with it, so item 2 reads "not decided": Q20 asks for one natural expiry), `--certs-dir`.

The run writes `output-cognito.txt` and `report-cognito.md` itself, after a final check that the output holds no
credential (Q4), and replaces the table between the markers in "Gate G0, as run" below. Do not pipe it through
`tee`: a file written past the check is a file the check never saw. Exit codes: 0 every expectation held; 1 one did
not; 2 usage; 3 a prerequisite is missing; 4 the output held a credential, and nothing was written.

### What it compiles

| File | What it is |
| --- | --- |
| `../real-handler/RealOidcHardened/Registry.cs`, `Auth.cs`, `Browser.cs` | Unchanged: the mechanism that passed item 1 |
| `../token-and-proxy/Gateway.cs` | Unchanged, for `TokenFlow` and `TokenSwitches`: the token step that passed item 4 |
| `CognitoGateway.cs` | The gateway for this run. Task 3.8's configuration unchanged (sentinel scheme, both scheme names on every call, `PostBody`), plus a TLS listener on 8443, Cognito's logout, the revocation route of Q16 and task 3.9's logout order |
| `Drivers.cs` | The login drivers: Playwright (Q2), `--manual`, and a redirect-follower for the dry run only |
| `Raw.cs` | The item 2 questions' raw calls through `Duende.IdentityModel` (Q5), and the token claims it may print |
| `Config.cs`, `Aws.cs`, `Prerequisites.cs` | The outputs document and app definitions (Q3), Parameter Store and the two Cognito describe calls (Q4, Q23), and the prerequisite check (Q9) |
| `Output.cs`, `Report.cs`, `Harness.cs` | Redaction, the "Gate G0, as run" table (Q7), and the run itself |
| `../../../fixtures/TestIdp` | The test identity provider, for the dry run only. `token-and-proxy/Idp.cs` is not compiled (Q1) |

It pins `Microsoft.Playwright` 1.63.0, `AWSSDK.SimpleSystemsManagement` 4.0.104.2 and
`AWSSDK.CognitoIdentityProvider` 4.0.105.4, besides the three packages `token-and-proxy` pins. The last is not in
`Directory.Packages.props`; spikes pin their own (`spikes/Directory.Packages.props`), and only this spike uses it.

What differs from `token-and-proxy`'s gateway:

| Change | Why |
| --- | --- |
| The browser reaches the gateway over TLS on 8443, and the harness on a plain loopback port as the load balancer would | Cognito sends the browser back to `https://app1.spa-poc.test:8443` (item 3) |
| Logout goes to `{BrowserOrigin}/logout?client_id=..&logout_uri=https://{host}/` | ADR-0011's cognito profile: Cognito's discovery lists no `end_session_endpoint`, and its logout returns no state (R-C7) |
| Logout revokes the refresh token read from the session record, through the library or, when discovery lists no revocation endpoint, through `{BrowserOrigin}/oauth2/revoke`, then removes the session whether or not that worked | Task 3.9, with Q12's and Q16's routes |
| Revocation tries the form body, then a Basic header if the client is refused | Cognito's revocation endpoint may want the header; the outcome says which it took, and a revocation that needed the header confirms C-2.21 (Q17), so the retry cannot hide it |
| A switch makes one code exchange send the secret in a Basic header | Q17 |
| No YARP routes and no upstream API | Item 5 is cited, not run again (Q8) |

### Reading the output

The vocabulary is the earlier harnesses': an **expectation** holds (`OK`) or fails the run (`BAD`); a
**finding** (`FINDING`) is a probe that confirmed something the plan must allow for, and `not-rep` one that did
not. Neither fails the run. `skip` is a check the mode cannot make. Each item 2 question is a finding whose line
names the route the grilling settled (`->`), so a surprising answer is a route, not a stop (Q21).

Nothing secret is printed (Q4): tokens appear as lengths and eight-hex-character hashes (`h:1a2b3c4d`), URLs lose
their query, and an access token's `aud`, `client_id` presence, `scope` and `token_use` are the only claims shown.
Every line is checked against every secret, password, token, code, state and cookie value the run has seen, and
withheld if it holds one; the whole output is checked again before the file is written. H-1 to H-5 show the check
working at the top of every run.

Evidence ids are `C-<item>.<n>` (Q6). Browser A is Alice's: app1, then app2, in one browser (Q19). Browser R holds
the raw experiments' logins; browser B signs in to app2 fresh, as the second user; browser C is Q17's Basic-header
code exchange.

| Id | Kind | What it shows | Verdict it maps to |
| --- | --- | --- | --- |
| C-2.1 | expectation | Discovery loads from the outputs' issuer, and authorization is at the managed-login origin | None: it fails the run, not item 2 (Q21) |
| C-2.2 | finding | Discovery lists no `end_session_endpoint` | The cognito profile's logout (ADR-0011) |
| C-2.3 | finding | Discovery lists no `revocation_endpoint` (Q16, T-B14) | I-6 adds an `IIdpProfile` member; the cognito profile supplies `{BrowserOrigin}/oauth2/revoke`; task 3.9 deletes the session regardless |
| C-2.4, C-2.5 | expectation | app1, then app2 in the same browser: login and callback complete, two session records | Item 2 |
| C-2.6 | finding | app2's login was silent because of app1's provider session (Q19) | None for the gateway |
| C-2.7 | finding | Resource binding does not put the resource into `aud` (Q15) | Q15(a): APIs accept Cognito tokens by `client_id` and scope; resource binding moves to Phase 2 for Cognito |
| C-2.8, C-2.10 | expectation | A refresh through the token library, as task 3.8 configures it, is granted and rotates the refresh token, for each app | Item 2 |
| C-2.9 | finding | `aud` is lost on a refresh that sends no `resource` (Q14) | Task 3.8 sends `resource` on every refresh and accepts T-B12 |
| C-2.11 | expectation | A token that comes due on Cognito's five-minute lifetime refreshes unforced (Q20) | Item 2 |
| C-2.12, C-2.16 | expectation | Each app's logout revokes, removes its session record and lands on `/` through Cognito's logout (R-C7) | Item 2 |
| C-2.13 | finding | The refresh token logout revoked still refreshes | Task 3.9 records the outcome; the token is held by no one |
| C-2.14 | finding | After app1's logout, the next login asks for a password: the provider's logout is global (Q19) | None for the gateway |
| C-2.15 | expectation | app2's session and refresh survive app1's logout (Q19) | Item 2 |
| C-2.17 | finding | Grace window outcome (i): a replay inside the window is refused, or leaves the successor unusable (Q11). `BAD` instead if the first refresh did not rotate, because then nothing was measured | Insufficient: ADR-0005's reserve design reopens |
| C-2.18 | finding | Grace window outcome (ii): a replay after the window makes the successor unusable (Q11). `BAD` instead if the first refresh did not rotate | Insufficient: ADR-0005's reserve design reopens |
| C-2.19 | finding | Revoking a rotated-out token leaves its successor alive (Q12, T-B18). `BAD` instead if the refresh did not rotate or the revocation was refused, because then Q12 was not asked | Task 3.9 revokes the token re-read from the session record; the T-B18 race stays a documented residual |
| C-2.20 | finding | A refresh token is over 4,096 characters, at login, after one refresh or after five (Q13, T-B11) | Q13(a): item 4's way out, an in-house refresh class over `Duende.IdentityModel` |
| C-2.21 | finding | Only one client credential style works, for the code exchange or the refresh, or a revocation needed the Basic header (Q17, T-B4, T-B5) | That style becomes the cognito profile's setting; if both work, `PostBody` stays |
| C-3.1 | expectation | Each OAuth client, as created, lists the callback and sign-out URLs with the port | Item 3, the port |
| C-3.2 | expectation | Every authorize call carried that callback, and Cognito sent the browser back to it | Item 3, the port |
| C-3.3 | expectation | Cognito accepted each app's sign-out URL | Item 3, the port |
| C-3.4 | expectation | Each OAuth client has the branding style Terraform created | Item 3, managed login |
| C-3.5 | expectation | Managed login renders, asks for a password, and completes, for both apps | Item 3, managed login |

**Verdicts.** Item 2 passes when C-2.4, C-2.5, C-2.8, C-2.10, C-2.11, C-2.12, C-2.15 and C-2.16 hold: both
OAuth clients complete login, callback, refresh and logout (Q21). Nothing else fails item 2: not discovery, not an
item 2 question, and not a section that stopped after those checks ran, which the verdict names instead. With
C-2.11 skipped, item 2 is not decided. If item 2 fails, identity work stops and the
integrator compares Finbuckle's per-tenant options, Duende BFF v4 and a dedicated tenant-aware OIDC client stack.
Item 3 passes when C-3.1 to C-3.5 hold for both apps (Q18). If it fails, its way out is the plan's (local port
443, or a branding style made through the AWS CLI), but task 3.0b does not take it (Q22): it records the failure.
For the port it opens a ticket for workstream 1 and for workstream 5 and adds a change request for the host
examples in `contracts/`; for the branding style it opens a ticket for workstream 1. The verdict line names which.

**Decided after review.** Four calls the grilling (Q1 to Q23) left open, settled by Adam on the review of task 3.0a:

- A precondition that fails makes C-2.17, C-2.18 and C-2.19 `BAD`, not skipped. A run that measured nothing must
  be run again, never read as an answer for ADR-0005 or task 3.9. It exits 1 and leaves item 2's verdict alone.
- `--skip-natural-expiry` leaves item 2 not decided. It is for iterating on the harness; Q20 asks for one natural
  expiry, so a G0 verdict comes only from a full run.
- C-2.15 is inside item 2's gate. app2's refresh failing after app1's logout is a refresh that does not complete
  for a client, which is what fails item 2 (Q21, read with Q19).
- Q11's two replays run in two token families, one per outcome, not in one. A replay that revokes the family at
  2 s would otherwise leave nothing for the 12 s replay to measure.

### The dry run (not evidence)

```bash
cd spikes/g0/cognito
dotnet run -c Release -- --dry-run                  # Playwright Chromium against fixtures/TestIdp
dotnet run -c Release -- --dry-run --driver http    # the same with no browser
dotnet run -c Release -- --dry-run --leak-test      # plants a token in the output: the run must refuse to write it
```

It runs every flow against the test identity provider in `fixtures/TestIdp`, set to behave like Cognito where it
can: no `end_session_endpoint` and no `revocation_endpoint` in discovery, logout at `/logout` with `client_id`
and `logout_uri`, a 10-second grace window. Access tokens last 65 seconds, so the natural expiry takes seconds.
The two OAuth clients use the fixture hosts on port 5300, which Chromium resolves to loopback by itself, and a
certificate made in memory. The outputs document is synthesised, and the provider has no login page and no
branding styles, so C-3.4 and C-3.5 are skipped and item 3 stays undecided. The test identity provider takes a
loopback port of its own choosing.

**It proves the harness, not Cognito.** Each answer below is the test identity provider's own rule, and none of
them may be cited for G0.

Recorded on 14 October 2026 on a Mac with Apple silicon, .NET 10.0.12, Chromium revision 1243:
`output-dry-run.txt` and `report-dry-run.md` are that run.

```
OK      H-1 .. H-5  the redactor withholds a client secret, an unknown JWT, a callback's code and a token in JSON; it prints hashes
OK      C-2.1       discovery                        (fails the run if it does not hold, not item 2)
FINDING C-2.2       no end_session_endpoint          (the test provider set to Cognito style)
FINDING C-2.3       no revocation_endpoint           (set so; the profile's revocation route ran at logout)
OK      C-2.4       app1 login and callback          OK      C-2.5   app2 in the same browser
FINDING C-2.6       app2's login silent              (the test provider never asks)
not-rep C-2.7       aud carries the resource         not-rep C-2.9   aud survives the refresh
OK      C-2.8       app1 library refresh, rotated    OK      C-2.10  app2 library refresh, rotated
OK      C-2.11      natural expiry after 8 s
OK      C-2.12      app1 logout through /logout, lands on /, session removed, revoked through the profile route
not-rep C-2.13      the revoked token is refused     not-rep C-2.14  the test provider never asks for a password
OK      C-2.15      app2 survives app1's logout      OK      C-2.16  app2 logout
not-rep C-2.17      a replay at 2.0 s is granted, and the successor still works
not-rep C-2.18      a replay at 12.0 s is refused, and the successor still works
FINDING C-2.19      revoking the rotated-out token leaves its successor alive (the provider's rule, T-B18)
not-rep C-2.20      refresh tokens of 38 characters
not-rep C-2.21      both credential styles work, for the code exchange and the refresh; every revocation took the form body
OK      C-3.1 .. C-3.3   callback and sign-out URLs with the port, registered, used and accepted
skip    C-3.4, C-3.5     no branding styles and no login page at the test provider

17 expectations held, 0 did not, 4 findings confirmed, 8 probes not reproduced, 2 skipped
G0 item 2 (DRY RUN, not evidence): passed
G0 item 3 (DRY RUN, not evidence): not decided: C-3.4, C-3.5 skipped in this mode
redaction: 149 credential values held as canaries; 0 line(s) withheld
```

The `--driver http` run gave the same verdict on every check. The `--leak-test` run ended:

```
REFUSED the output holds a value the harness holds as a credential. Nothing was written: not output-dry-run.txt, not report-dry-run.md.
```

with exit code 4, and left the recorded `output-dry-run.txt` byte for byte as it was. And `--cognito`, on the
same machine with nothing applied and no AWS credentials, refused with exit code 3:

```
Prerequisites (Q9)
MISSING 1.1 applied: the user pool, its managed-login domain and the test users (infra/envs/poc/idp)
               [no infra outputs document at infra/envs/poc/outputs.json or spikes/g0/cognito/outputs.local.json; run spikes/g0/cognito/outputs-from-terraform.sh after applying infra/envs/poc/idp and infra/envs/poc/apps]
MISSING 1.8 applied: an OAuth client, branding style and secret parameter for app1 and app2 (infra/envs/poc/apps)
               [no infra outputs document at infra/envs/poc/outputs.json or spikes/g0/cognito/outputs.local.json; run spikes/g0/cognito/outputs-from-terraform.sh after applying infra/envs/poc/idp and infra/envs/poc/apps]
MISSING certs.sh run on this workstation (1.6a): the *.spa-poc.test certificate, trusted
               [no certificate and key at .certs/spa-platform-poc-wildcard.fullchain.pem and spa-platform-poc-wildcard.key: run tools/scripts/certs.sh issue, then tools/scripts/certs.sh trust]
MISSING the hosts-file line: 127.0.0.1 app1.spa-poc.test app2.spa-poc.test app3.spa-poc.test
               [app1.spa-poc.test does not resolve; app2.spa-poc.test does not resolve. Add the line to /etc/hosts]
present local port 8443 free (stop tunnel.sh first: the harness takes 8443 itself)
               [nothing accepts connections on 127.0.0.1:8443]
MISSING AWS credentials as in assumption A7, able to read the secret and password parameters
               [no AWS credentials: Failed to resolve AWS credentials. ...]
present Chromium for Microsoft.Playwright 1.63.0 (or pass --manual)
               [it launches]

REFUSED 5 prerequisite(s) missing. Nothing was started, nothing was bound, and no output file was written.
```

What the dry run cannot show: Cognito's managed-login page and its selectors (`Drivers.cs` keeps them in one
place, `ManagedLoginPage`), the workstation's trust in the local authority, Parameter Store, the two describe
calls, and every answer to an item 2 question.

## Gate G0, as run

The report to the integrator (Q7). One row per G0 item, and one per item 2 question with its answer and route.
Task 3.0b's run replaces the table between the markers with what it found; until then items 2 and 3 are open.

<!-- g0-as-run:begin (written by spikes/g0/cognito --cognito) -->
| G0 item | Verdict | Where |
| --- | --- | --- |
| 1. The shipped handler, with the overrides | Passed on 11 October 2026 (cited, not run again: Q8) | `real-handler/RealOidcNuGet/output-nuget.txt` |
| 2. Two OAuth clients complete login, callback, refresh and logout | Open: awaiting task 3.0b's run of `cognito/` | `cognito/output-cognito.txt`, once run |
| 3. A callback URL with a port; managed login for a client Terraform made | Open: awaiting task 3.0b's run of `cognito/` | `cognito/output-cognito.txt`, once run |
| 4. Duende.AccessTokenManagement under derived schemes | Passed on 11 October 2026 (cited, not run again: Q8) | `token-and-proxy/output-token-and-proxy.txt` |
| 5. YARP 2.3.0 on .NET 10 | Passed on 11 October 2026 (cited, not run again: Q8) | `token-and-proxy/output-token-and-proxy.txt` |

| Item 2 question | Evidence | Answer | Route |
| --- | --- | --- | --- |
| What does a rotated-out refresh token get inside the 10-second grace window? | C-2.17 | Open | |
| Does a replay after the window harm the current token? | C-2.18 | Open | |
| Does revoking a refresh token revoke its successor? | C-2.19 | Open | |
| How long are refresh tokens, at login and after a refresh? | C-2.20 | Open | |
| Does `aud` survive a refresh that sends no `resource`? | C-2.9 | Open | |
| Does discovery list a revocation endpoint? | C-2.3 | Open | |
<!-- g0-as-run:end -->

## What differs between RealOidc and RealOidcHardened

Four defaults in the `Switches` class in `Auth.cs`, and one test line in `Program.cs`
that looks for the renamed correlation cookie:

| Switch | Effect |
| --- | --- |
| `DisableRemoteSignOutPath` | `RemoteSignOutPath = default`, so `GET /signout-oidc` no longer signs the user out |
| `HostPrefixedLoginCookies` | Correlation and nonce cookies named `__Host-spa-corr.` and `__Host-spa-nonce.` on `Path=/` |
| `BlindSessionCookieOnCallback` | A cookie manager hides the session cookie on the callback path, so sign-in mints a new session key |
| `AntiforgeryBindApp` | The app id is bound into the antiforgery token |

## Reading the hardened output

A line starting `FINDING` is not a failure. With the overrides on, probe D1 no longer
reproduces and eight still report:

- **D5, D7, D8, E3 and E7** set their own options on purpose, to show the stock or
  unpatched behaviour.
- **D3** reports because the test client enforces no cookie prefix. The `__Host-`
  protection is the browser's, so the plan tests it in a real browser.
- **D10 and E4** report because their fixes are not in this harness: evicting a failed
  options build (task 3.6) and clearing the antiforgery cookie at logout (task 4.4).

## What these do not cover

Sections 1 to 4 cover no Cognito, browser or TLS. Section 5 covers Cognito, a real browser and
TLS on the workstation, once task 3.0b has run it. None of them covers the deployed tier: the load
balancer, the ECS task and the tunnel. The deployed test tier covers those.
