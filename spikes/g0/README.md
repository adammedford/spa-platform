# G0 harness

The console programs behind the plan's Evidence tab (`docs/plan/07-evidence.md`).
Each drives ASP.NET Core 10's own authentication middleware with OAuth clients that are
loaded, rotated and removed at runtime, prints one line per check, and exits non-zero if
an expectation fails.

This folder is outside the solution. `spikes/Directory.Build.props` and
`spikes/Directory.Packages.props` keep it apart from the solution's build settings.
Task 3.0 extends it for Gate G0. `token-and-proxy/Idp.cs` is the starting point for the
test identity provider in task 5.4.

| Folder | What it runs | Result on .NET 10.0.12 |
| --- | --- | --- |
| `stand-in-spike/` | The host-scoped scheme provider with a stand-in OIDC handler and a fake authorization server. No package references. | 34 of 34 checks in every run |
| `real-handler/RealOidc/` | The same mechanism with the real `OpenIdConnectHandler` and a hermetic OpenID Connect provider. Framework defaults, so the probes report what is wrong with them. | 32 expectations held, 9 findings |
| `real-handler/RealOidcHardened/` | The same sources with four overrides made the defaults. | 32 expectations held, 8 findings still report |
| `real-handler/RealOidcNuGet/` | The hardened sources against the shipped NuGet package. | 32 expectations held, 8 findings still report. Same verdict on all 41 checks as the source build. |
| `token-and-proxy/` | The hardened mechanism with `Duende.AccessTokenManagement.OpenIdConnect` 4.2.0 and `Yarp.ReverseProxy` 2.3.0 added, for Gate G0 items 4 and 5. | 43 expectations held, 23 findings, 1 probe not reproduced |

Recorded output is beside each program: `spike-output.txt`, `output.txt`, `output-hardened.txt`, `output-nuget.txt`, `output-token-and-proxy.txt`.

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

Cognito, a browser and TLS. Gate G0 items 2 and 3 cover Cognito, and the deployed test
tier covers the rest.
