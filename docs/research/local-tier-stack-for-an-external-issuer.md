# What the Local tier and `tests/e2e/Stack` provide today, and what a compose stack needs to run the composed suites against an external issuer

- **Ticket:** #949, "What the Local tier and tests/e2e/Stack provide today, and what a compose stack
  needs to run the composed suites against an external issuer" (`wayfinder:research`), on the map
  #944, "Wayfinder: PingFederate as the evaluation provider, locally and in the cell".
- **Date:** 2026-11-04. **Branch:** `research/local-tier-stack-for-an-external-issuer`, pushed and
  never merged, as the map rules.
- **Method:** repository reading at commit `8ceeee3` (origin/main on 2026-11-04, fetched at the start
  of the session): `GLOSSARY.md`, the startup benchmark map ("Wayfinder: gateway startup benchmark,
  floor and budget", #169) and its nine tickets through `gh`, `tests/e2e/Stack`, `tests/e2e/Functional`,
  `tests/e2e/Negative`, `tests/e2e/Deployed`, `tests/Gateway.TestHarness`,
  `tests/Gateway.IntegrationTests`, `tests/Gateway.SecurityTests`, `fixtures/TestIdp`,
  `fixtures/MockApi`, `fixtures/apps`, `fixtures/cognito`, `tools/bench`, `tools/image-check.sh`,
  `tools/smoke.sh`, `src/Gateway/Dockerfile`, `src/Platform.Identity` (read only),
  `src/Platform.Registry` (read only), `infra/modules/cell/ecs.tf` (read only), `contracts/`,
  `docs/plan/04-tests.md`, `docs/plan/05-infrastructure-and-cost.md`, ADR-0033, the P2-6 change
  request and the October draft spec in `.scratch/phase-2/`. Web pages read on 2026-11-04 are listed
  in section 9 with what each gave. Docker Desktop 29.9.1 (arm64, Compose v5) is on this machine but
  nothing was executed in a container: no `docker` command, no `dotnet test`, no `tools/bench`, no AWS
  credential or API (`docs/agents/aws-rules.md`), and nothing against PingFederate, for which no DevOps
  user key or licence exists yet. Every PingFederate fact here is either read from a page named in
  section 9 or marked "not found"; the sibling ticket on the evaluation licence, image and
  configuration owns the rest. Nothing was written in the repository but this file.

## Answer in brief

1. **The Local (L) tier as built is the startup benchmark, and only that.** `GLOSSARY.md:511-515`
   defines L as a fresh container of the built gateway image under the task's limits against
   stand-ins for the AWS services and the provider. The one thing that runs it is
   `tools/bench/startup.py` (#173, #174, ADR-0033): it builds `src/Gateway/Dockerfile` from
   `git archive HEAD`, starts the container with `--cpus 0.25 --memory 1g` inside the network
   namespace of a holder container, feeds it moto 5.2.4 for S3 and Parameter Store over
   `AWS_ENDPOINT_URL` on an IP literal, and points every issuer at a static discovery document on
   `127.0.0.1:9000` that can never complete a login (`tools/bench/startup.py:16-20, 41-57, 375-399,
   405-422, 598-616`). No composed suite runs against a container today. The class named
   `LocalStack` in `tests/e2e/Stack` is *not* the L tier: it is the hermetic in-process cell behind an
   in-process TLS listener, in a real Chromium (`tests/e2e/Stack/LocalStack.cs:8-14`,
   `StackSwitch.cs:6-7`). Section 1 has the inventory, including `tools/image-check.sh` and
   `tools/smoke.sh`, which start the gateway with no stand-in at all.
2. **The composed suites split by project, not by row.** `tests/Gateway.IntegrationTests` and
   `tests/Gateway.SecurityTests` build the gateway in process through `WebApplicationFactory`
   (`tests/Gateway.TestHarness/GatewayFactory.cs:23`) and drive the stand-ins directly
   (`Idp.Faults`, `SessionStore.Failing`, `GatewayStore.Refuse`, `SteppedClock`,
   `RotateClientAsync`, `UpdateRegistryAsync`); nothing in them can run against an external issuer
   (section 2.1, section 6). The browser suite under `tests/e2e` can: every row goes through
   `IBrowserStack`, and every step that reads the stand-in's record is already behind
   `if (stack is LocalStack)` as an "In H" clause (section 2.3). What fixes it to `fixtures/TestIdp`
   today is `GatewayHarness.StartAsync`, which constructs the test provider and writes
   `Issuers:{name}:Authority` and `BrowserOrigin` as its loopback URL
   (`tests/Gateway.TestHarness/GatewayHarness.cs:176-178, 210-212`), and `StackSwitch.Choose`, which
   admits only `local` and `deployed` (`tests/e2e/Stack/StackSwitch.cs:24-30`). A run against a real
   issuer needs a third `IBrowserStack`: `TlsLoadBalancer` in front of the container's published
   port (its constructor takes any target URL, `TlsLoadBalancer.cs:69`), `DeployedStack`'s shape for
   the login form, the two users and the stop at the provider's login
   (`DeployedStack.cs:232-274, 291, 317-325, 345-353`), the gateway's JSON log read from the
   container's stdout rather than from `Harness.Log` or CloudWatch, and the stand-in's levers
   replaced per row (section 2.4). Four rows read the authorize URL's query or the callback URL's
   `code` and `state` (F-02, N-03, N-04, N-05) and change under PAR and `form_post` whatever the
   provider is; F-07 is the one row with no path until a client can be created at the real provider
   at run time (section 2.4).
3. **The mock API validates against one issuer it discovers from `MockApi:Issuer`, compared verbatim
   with the token's `iss`** (`fixtures/MockApi/Program.cs:21-29`, `MockApiHost.cs:113-126`), admits
   the two client ids it is given and listens on 5080 and 5081, the loopback ports every app
   definition names as its upstreams (`fixtures/apps/app1.json`, `fixtures/MockApi/Dockerfile:64-67`).
   So in a compose stack the mock API must share the gateway's network namespace
   (`network_mode: "service:gateway"`), as the Fargate task and the benchmark's `--network
   container:` do, or the registry's upstreams and `Policy:AllowedUpstreams` change (section 3). The
   apps are `app1.spa-poc.test:8443` and `app2.spa-poc.test:8443`, served from `fixtures/spa`, with
   the test users `alice` and `bob` of `fixtures/cognito/pool.json`.
4. **The stack could run on a GitHub runner, as facts; whether it does is the map's deferred
   decision.** The Ubuntu 24.04 runner image carries Docker 28.0.4, Compose 2.38.2 and Buildx;
   `ubuntu-latest` is x64 and `ubuntu-24.04-arm` is arm64, both for public and private repositories;
   the nightly tier-L job already runs Docker on `ubuntu-24.04-arm`
   (`.github/workflows/startup-bench.yml:57-58`); the gateway Dockerfile builds for the runner's own
   architecture (`src/Gateway/Dockerfile:21-22`); the licence goes in as the `PING_IDENTITY_DEVOPS_USER`
   and `PING_IDENTITY_DEVOPS_KEY` environment pair from the `secrets` context, which fork-triggered
   runs do not receive (section 4). The PingFederate image's architectures and the licence's
   duration were not re-verified here: the sibling ticket's.

Section 5 is the concrete list a compose stack must provide; section 6 the suite ids that can and
cannot run against a real issuer; section 7 the open points for the fixture and stack tickets.

## 1. The Local (L) tier: as defined and as built

### 1.1 The definition and its decisions

- `GLOSSARY.md:511-515`: **Local (L)** "runs a fresh container of the built gateway image under the
  task's limits, against stand-ins for the AWS services and the provider, on a workstation or a CI
  runner. It proves the method and the local budget, and never the platform's pull, placement or
  health checks." Hermetic (H) is "in process ... against the test identity provider, an in-memory
  object store, the real mock API and an echo upstream" (`:503-505`); Deployed (D) is "through the
  tunnel ... against the ALB, the task, S3, Parameter Store and Cognito, with a real browser plus a
  raw HTTP client" (`:507-509`).
- The tier was decided on the startup benchmark map (#169, "Wayfinder: gateway startup benchmark,
  floor and budget"): its ticket "The startup test id: class, tier and when it runs" (#176) decided
  "a new class `B-` ... and a third tier, Local", running "nightly on an ARM64 runner, informational
  until ten calibration runs fit" (#169, Decisions so far). ADR-0033 records it:
  "A third tier, **Local (L)**, runs a fresh container of the built image under the task's limits,
  against the stand-ins, on a workstation or a CI runner; hermetic keeps its in-process meaning.
  `B-01` runs in L and D, `B-02` in D" (`docs/adr/0033-...md:98-100`). The same ADR fixes what stands
  in: "moto 5.2.4 stands in for S3 and Parameter Store; a static discovery document on
  `127.0.0.1:9000` in a shared namespace stands in for the provider. The local 401 needs that
  stand-in because the OpenID Connect handler reads discovery before `OnRedirectToIdentityProvider`
  writes the 401; without it the answer is a 500" (`:62-65`).
- #169's charting note that matters here: "Locally the first-app-host clock stops at an
  unauthenticated fetch's 401 on a registered host, because no local stand-in answers provider
  discovery", and its Out of scope: "An image for `fixtures/TestIdp` so a local navigation can
  complete discovery. The 401 clock stands in locally." So the map that created the L tier chose,
  deliberately, a tier on which no login completes. Eight of its nine tickets are closed; "Run the
  benchmark on Fargate once the cell is applied" (#178) is open.
- `GLOSSARY.md:536-541` ("Test ids"): a `B-` id is proved "by the startup benchmark's verdict on its
  tier, never a test project". The L tier has no test project today.

### 1.2 What runs the tier: `tools/bench/startup.py`

The benchmark (a prototype, #173; the one L-tier runner) does, per `tools/bench/startup.py`:

| Step | Where | What |
| --- | --- | --- |
| Build | `:523-540` | `docker build` of `src/Gateway/Dockerfile` and `tools/bench/floor/Dockerfile` from `git archive --format=tar HEAD` piped to the build, tagged `spa-bench/gateway:<commit>`; `--no-build` reuses them; moto `motoserver/moto:5.2.4` is pulled (`:96`). |
| Network | `:509-514, 548-552` | A bench network with a fixed `--subnet`; moto at the subnet's tenth host address, the measured container at the twentieth. The IP literal is deliberate: the SDK uses path style only on an IP endpoint (`:41-45`, from "Which local stand-in feeds the gateway container S3 and Parameter Store", #170). |
| Seed | `:442-469, 553-566` | A moto-image container runs `startup.py seed` with boto3: the bucket from `contracts/platform-config.example.json`, versioning on, one `SecureString` parameter per app named by the example registry's `clientSecretRef` (value `bench-not-a-secret`, asserted at version 1), every `fixtures/spa/<app>` file under `apps/<app>/releases/<release>/`, and `contracts/registry.example.json` as the registry object. The seed is also moto's warm-up and retries for 30 s. |
| Provider stand-in | `:405-439, 600-602` | A holder container (the moto image, `startup.py stand-in`) serving a static `/.well-known/openid-configuration` and an empty `/jwks` on `127.0.0.1:9000`: "Enough for the handler to build a challenge ... no login can complete against it, and none is attempted" (`:406-408`). |
| The gateway | `:375-399, 607-611` | `docker create ... --network container:<holder> --cpus 0.25 --memory 1g`, with the environment `gateway_environment` builds: the example platform configuration flattened the way `infra/modules/cell/ecs.tf` flattens it (`Policy__AllowedUpstreams__0`), every `Issuers__<name>__Authority` overwritten with `http://127.0.0.1:9000`, plus `AWS_ENDPOINT_URL`, `AWS_REGION=us-east-1` and a static key pair. The shared namespace "is the shape of a Fargate task, whose containers share one namespace under awsvpc" (`:54-57`). |
| Probe | `:154-204, 612-616` | A probe container on the bench network polls `listening`, `/healthz`, `/readyz`, then `GET /` on the registry's host with `Sec-Fetch-Mode: cors` and no cookie, expecting 401. |
| Verdict | `:301-369` | `--verdict` with `--budget healthz=4 --budget readyz=6 --budget app_host=7` (ADR-0033's local budgets), N=10 fixed. |

The gateway container is reached only on the bench network: "It binds no host port" (`:17-18`). The
mock API is not started (the first-app-host clock stops at the 401 before any upstream). Two runs
are recorded in `tools/bench/results/README.md` (Apple M4 Pro, Docker Desktop engine 29.9.1,
2026-10-21); worst gateway maxima healthy 1819 ms, ready 2813 ms, first app-host 3323 ms (#169,
Decisions so far, "Run the local benchmark").

The nightly job: `.github/workflows/startup-bench.yml:57-58` runs on `ubuntu-24.04-arm`, through
`.github/startup-bench.sh`, which calls `tools/bench/startup.py --verdict` with the three budgets
and re-runs once on `bench: no verdict:` (`.github/startup-bench.sh:7-8, 14-21`).

### 1.3 The other two container and process checks

- `tools/image-check.sh` builds `src/Gateway/Dockerfile` from a copy of the tracked tree with a
  planted host `obj/` and an owner-only `appsettings.json`, starts the image as its own user with no
  environment and no stand-in, and passes when the gateway logs that it started
  (`tools/image-check.sh:13-24, 114-118`). It then holds the hardened shape: no `/bin/sh` (read by
  exporting the container's file system, since the chiseled image has nothing to look with),
  `DOTNET_EnableDiagnostics=0`, the three OCI labels (`:25-30`). It builds "For this machine's
  architecture, not the ECS task's ARM64" (`:32-36`). CI runs it in the x64 build job
  (`.github/workflows/ci.yml:684-689`).
- `tools/smoke.sh` is G1a's exit test, not a container: `dotnet publish --no-build` and the
  published `Gateway.dll` on a port, then `curl` with a spoofed `Host` for 200 on `/healthz`, 503
  on `/readyz` ("cannot happen on stubs") and 404 on every app path (`tools/smoke.sh:24-26, 50-57`).
  CI runs it after the tests (`ci.yml:676-682`).

### 1.4 The gateway image, as the tier runs it

`src/Gateway/Dockerfile`: multi-stage, both bases pinned by digest; the final stage is
`mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra` ("distroless, with no shell and no
package manager, and a non-root user by default", `:60-67`), `DOTNET_EnableDiagnostics=0` (`:75`),
`EXPOSE 8080` and `ASPNETCORE_HTTP_PORTS=8080`, "No TLS in the container: the listener terminates
it" (`:88-91`), `USER $APP_UID` (`:95`). The mock API image takes the same two `FROM` lines
"character for character" and `fixtures/Fixtures.Tests` holds them equal
(`fixtures/MockApi/Dockerfile:9-13, 46`; `fixtures/Fixtures.Tests/MockApiDockerfileTests.cs:6-7`).
The task runs ARM64 at 256 CPU units and 1024 MB (`infra/modules/cell/ecs.tf:272-273, 281-283`).

Two consequences for a compose stack follow from the chiseled base: no in-container `healthcheck`
command can run in the gateway or the mock API container (nothing to run it with), and a CA
certificate cannot be installed with a package manager; section 5.3 says what that leaves.

### 1.5 What `tests/e2e/Stack` is, and is not

`tests/e2e/Stack/LocalStack.cs:8-14`: "The stack a browser test runs against, locally: the hermetic
cell (task 6.1) ... behind a `TlsLoadBalancer`, and a Chromium whose resolver sends every app host
to that listener". `StackKind.Local` is "The hermetic cell behind the stand-in load balancer ... the
default, and what CI runs" (`StackSwitch.cs:6-7`). It starts `GatewayHarness` in the test process
(`LocalStack.cs:42`), a Kestrel HTTPS listener on loopback with a self-signed certificate for the
app domain that forwards to the harness through `LoadBalancerTransport` (`TlsLoadBalancer.cs:15-18,
69-80`), and headless Chromium with `--host-resolver-rules=MAP *.spa-poc.test <listener>, MAP
attacker.example <listener>` (`LocalStack.cs:46-51`; `Chromium.cs:24-38` installs the browser
through `Microsoft.Playwright.Program.Main(["install","chromium"])`). The browser context ignores
certificate errors because "The stand-in's certificate is this run's own, trusted by no store"
(`LocalStack.cs:165-166`). So the browser suite's "local" is the H tier in a real browser; the
container of the L tier is nowhere in `tests/e2e`.

## 2. The composed suites

### 2.1 Two in-process suites, one harness

`tests/Gateway.IntegrationTests` (the F rows and the P2 continuity rows) and
`tests/Gateway.SecurityTests` (the N rows) run on `GatewayHarness`, "The hermetic cell (task 6.1):
the real gateway composition, and everything it talks to, in one test process"
(`tests/Gateway.TestHarness/GatewayHarness.cs:18-34`). `GatewayHarness.StartAsync`:

- constructs and starts `TestIdentityProvider` unconditionally, with `EndSessionStyle` from
  `Options.Profile` (`GatewayHarness.cs:176-178`; `GatewayHarnessOptions.cs:20-27`: `IssuerName`
  default `test-idp`, `Profile` `standard` or `cognito`);
- starts `MockApiHost` on two loopback listeners against the stand-in's issuer and discovery URI,
  admitting `client-app1` and `client-app2` (`:181-183, 720`), and an echo upstream (`:185-186`);
- writes the gateway's configuration: `Platform:Cell=harness`, `Registry:Bucket`/`Assets:Bucket`
  `harness-bucket`, `Registry:PollSeconds=3600`, `Issuers:{name}:Authority` **and**
  `Issuers:{name}:BrowserOrigin` both `idp.BaseUrl` (the stand-in's `http://127.0.0.1:<port>`),
  `Issuers:{name}:Profile`, `Policy:AllowedHostSuffixes:0=.spa-poc.test:8443`,
  `Session:Store=memory`, and `ForwardedHeaders:TrustedNetworks` default `127.0.0.1/32`
  (`:203-226`; `GatewayHarnessOptions.cs:40`);
- replaces `IObjectStore` with `SwitchableObjectStore` over `InMemoryObjectStore`,
  `IClientSecretSource` with `FakeClientSecretSource`, the clock when a `SteppedClock` is given, and
  under a key ring the session store, the lease table and the instance id (`:287-309`); under
  `KeyRingSharing` it puts `ParameterStoreStub` under the real ring's keyed client (`:435-448`;
  `ParameterStoreStub.cs:20-23` overrides `GetParametersByPathAsync` and `PutParameterAsync`);
- registers each app at the provider with a random per-run secret and `ssm:/spa-platform/harness/
  apps/{appId}/client-secret:1`, and puts the secret in the fake source (`:694-717`); the gateway is
  built by `GatewayFactory : WebApplicationFactory<Program>` (`GatewayFactory.cs:23`).

The plain-HTTP authority works only because `SharedOidc` admits plain HTTP to a loopback IP
address and nothing else (`src/Platform.Identity/IssuerDirectory.cs:98-107`); the schema itself
requires `https://` for both `Authority` and `BrowserOrigin`
(`contracts/platform-config.schema.json:62, 77`).

Every lever below is a member of these in-process objects, so a row that pulls one has no meaning
outside the test process:

| Lever | Member | Rows that pull it (file:line) |
| --- | --- | --- |
| Token lifetimes, refresh grace, outage, delays, unpublished key, wrong `iss`, `token_use` | `TestIdpFaults` (`fixtures/TestIdp/TestIdpFaults.cs:19-102`; "ways a real provider will not produce on demand", `:3-6`) | F-03 `RefreshTests.cs:38-48, 78-80`; F-10 `:96-103`; the refresh-outage control `:135-168`; F-05 `TokenExposureTests.cs:35-37`; F-09 `RegistryChangeTests.cs:52-54`; N-23 `BffBodyTests.cs:32-34`; P2-3 F3/F5 `RefreshAcrossHostsTests.cs:113-124, 211-216` |
| Who the next login is | `Idp.DefaultSubject` (`TestIdentityProvider.cs:78`; `/authorize` has no login page, `:308-316`) | N-06 `CsrfTests.cs:41-46`; N-18 `LoginTests.cs:167-175`; F-01/F-08 assert it `SignInTests.cs:107`, `MockApiRouteTests.cs:48` |
| A client's secret changed or a client added at the provider | `Idp.SetClientSecret`, `Idp.AddClient` | F-09 `RegistryChangeTests.cs:60-62, 92`; N-22 `SessionBindingTests.cs:174-181` |
| The gateway's secret source | `harness.Secrets[ref]` | F-09 `RegistryChangeTests.cs:61`; N-22 `SessionBindingTests.cs:179` |
| The registry object written and polled at once | `UpdateRegistryAsync`, `PublishRegistryTextAsync` | F-06 `ReleaseSwitchTests.cs:49-127`; F-09 `RegistryChangeTests.cs:28, 62, 110-125`; F-07 `OnboardingTests.cs` via `OnboardAsync`; N-10 `UnknownApiTests.cs:107`; N-12 `UpstreamResponseTests.cs:146`; N-14 `RegistryRejectionTests.cs:74-108`; N-22 `SessionBindingTests.cs:142-181`; per-app headers `PerAppHeadersTests.cs:133` |
| The mock API admitting a client at run time | `MockApi.AdmissionOf("reports").Admit` | F-07 `OnboardingTests.cs:38` |
| The session store failing, its purge failing, a stalled replace | `SwitchableSessionStore.Failing`, `.FailingPurge`, `.StallNextReplace()` (`SwitchableSessionStore.cs:22, 33, 74, 91`) | F-18 `StoreOutageTests.cs:25-47`; N-30 `RotationTests.cs:61`; P2-3 F3 `RefreshAcrossHostsTests.cs:123, 181` |
| The bucket refusing a key | `SwitchableObjectStore.Refuse` (`SwitchableObjectStore.cs:23`) and `MissingKeys`/`RegistryAtStart` | `RegistryReadRefusalTests.cs:30, 56-60`; F-06 refusal mode `AssetReadRefusalTests.cs:75` |
| A stepped clock | `SteppedClock` through `Options.Clock` | P2-3 F3 `RefreshAcrossHostsTests.cs:106-107` |
| A second or third gateway host, one or two Parameter Store stubs | `StartHostAsync`, `KeyRingSharing` | F-13 to F-15, N-28 `AcrossHostsTests.cs:34, 61, 90, 108`; F-12 `TwoHostHarnessTests.cs`; F-16, F-17 `ConvergenceTests.cs` |
| A client rotated in place | `RotateClientAsync` | N-29 to N-31 `RotationTests.cs:31, 62, 94` |
| The trusted networks | `GatewayHarnessOptions.TrustedNetworks` | N-09 `ForwardedHeaderTests.cs:91, 127, 147` |
| N-16's canary sweep over the captured log | `CanaryTests.cs:7` ("Run the full suite with canary values"), over `Harness.Log` | N-16 |

The browser inside these suites is `TestBrowser`, "A scripted browser for the hermetic tier"
(`TestBrowser.cs:9-11`), which "enforces no cookie prefix and no `SameSite`, runs no script, and
applies no content security policy" (`:29-33`): a real provider's login form would need a script
engine it does not have.

### 2.2 The browser suite and `IBrowserStack`

`tests/e2e` (project `Gateway.E2ETests`, "Browser suite (task 6.5): every row docs/plan/04-tests.md
marks D, in headless Chromium", `tests/e2e/Gateway.E2ETests.csproj:3-5`) runs each row on an
`IBrowserStack`, "The stack a functional row (#209) or a negative row (#211) runs against, local or
deployed, as the row needs it ... A clause only one stack can show (what the test provider counted,
say) is asserted by the row on a `LocalStack` alone; docs/plan/04-tests.md names those clauses 'In
H'" (`tests/e2e/Stack/IBrowserStack.cs:6-14`). The switch is `SPA_E2E_STACK`: unset, empty or
`local` is `LocalStack`, `deployed` is `DeployedStack`, "Any other value is refused rather than read
as local" (`StackSwitch.cs:13-30`; `Stacks.StartAsync`, `IBrowserStack.cs:157-163`).

The interface's members, and how each stack fills them (`IBrowserStack.cs:17-138`):

| Member | `LocalStack` (hermetic) | `DeployedStack` (cell) |
| --- | --- | --- |
| `App(appId)` | `Harness.App` (`LocalStack.cs:68`) | `Cell.App`: the host from `fixtures/apps`, the client id from `infra/envs/poc/outputs.json` `clients` (`Cell.cs:83-99`) |
| `OpenAsync` | a context with `IgnoreHTTPSErrors = true` (`:163-168`) | `IgnoreHTTPSErrors = false`: "Chromium must trust the cell's certificate through the local authority" (`DeployedStack.cs:211-220`) |
| `CompleteLoginAsync` | `Task.CompletedTask`: "The test provider approves every authorization at once" (`:76-77`) | fills Cognito's managed login, `input[name='username']`, `input[name='password']`, `button[type='submit']`, password read from Parameter Store with `aws ssm get-parameter --with-decryption` (`DeployedStack.cs:232-274`; `CellAws.cs:32-39`; `Cell.cs:105-107`) |
| `ExchangesAsync` | the listener's record, bodies included (`:79`; `TlsLoadBalancer.cs:32-33`) | the browser's own record, `BrowserExchanges` (`DeployedStack.cs:216-222`) |
| `ProviderHost`, `AuthorizeEndpoint`, `EndSessionEndpoint` | the stand-in's URLs (`:70-74`) | `cognitoDomain` from the outputs, `/oauth2/authorize`, `/logout` (`Cell.cs:26, 55, 61`) |
| `LogLinesAsync`, `SettledLogLinesAsync`, `MarkLog`, `AssertRuleLoggedAsync` | the in-process `CapturedLog`, exact counts (`:81-94, 210-242`) | CloudWatch `filter-log-events` by time, with a 90 s arrival and a quiet period between stacks (`DeployedStack.cs:59-79, 276-301`; `CellAws.cs:46-52`) |
| `SignInWithExpiredAccessTokenAsync` (F-03, F-05, N-23) | `Idp.Faults.AccessTokenLifetime = -10 min` around the login (`:100-105`) | waits out the five-minute lifetime plus 30 s: "nothing can issue one already expired" (`DeployedStack.cs:53-57, 338-342`) |
| `StopAtProviderLoginAsync` (F-04, N-06, N-23) | the listener holds `/signin-oidc`, since the stand-in "would sign straight back in" (`:107-124`; `TlsLoadBalancer.cs:176, 190, 205`) | waits for the provider host and a visible username field (`DeployedStack.cs:345-353`) |
| `StopAtCallbackAsync` (N-03, N-04, N-05, N-26) | the listener holds the callback and hands back its URL (`:183-200`) | `NavigationHold` inside the browser, after the form (`DeployedStack.cs:317-325`) |
| `SignInAs(user)` | `Idp.DefaultSubject = user` (`:174`) | remembered for the next form fill (`DeployedStack.cs:291`); the users are `alice` and `bob` (`:48-51`) |
| `ReleaseOfAsync`, `PublishReleaseAsync`, `SwitchReleaseAsync`, `RegistryPropagation` (F-06) | the harness writes the bucket and polls at once; propagation 5 s (`:126-148`) | `spactl release publish` / `activate --config $SPA_E2E_PLATFORM_CONFIG --outputs`; propagation two minutes (`DeployedStack.cs:138, 363-384, 427-431`) |
| `OnboardAsync`, `OffboardAsync` (F-07) | the harness registers a client at the stand-in and writes a revision (`:151-160`; `GatewayHarness.cs:596-602`) | the apps root's plan and apply, `spactl app add`, `spactl wait`, with Terraform on the path (`DeployedStack.cs:386-425`) |
| `SendRawAsync` | through the listener (`:177`) | `RawClient.To("127.0.0.1:<tunnel port>")` (`DeployedStack.cs:303-310`) |
| `OpenForeignPageAsync`, `Sibling` | the listener serves `attacker.example` and `evil.spa-poc.test` (`:202-207`; `ForeignSites.cs:30-33`) | a loopback listener of the run's own, trusted by SPKI (`DeployedStack.cs:176-183`; `ForeignSites.cs:55, 81`) |

The deployed stack reads everything it needs from the environment and one document: the outputs
document at `infra/envs/poc/outputs.json` or `SPA_E2E_OUTPUTS` (`DeployedStack.cs:145-154`),
`SPA_TUNNEL_PORT` else 8443 (`:450-455`), `SPA_E2E_PLATFORM_CONFIG` for a release switch (`:427-431`),
`SPACTL` else `dotnet run --project tools/spactl` (`:434-437`), and the operator's own AWS session
(`:13-19`). The outputs document gives `region`, `bucket`, `logGroup`, `cognitoDomain`,
`ecsCluster`, `ecsService`, `clients.{appId}.clientId` and `testUsers.{user}.passwordParameter`
(`Cell.cs:23-30, 86-88, 105-107`; `contracts/infra-outputs.schema.json`, properties `cognitoDomain`,
`clients`, `testUsers`). No password and no secret is ever in a file: "the value is read from
Parameter Store when it is used" (`Cell.cs:13-14`).

### 2.3 The steps that drive the stand-in directly, row by row (`tests/e2e`)

Every one of these is already conditional on the stack, so the row itself runs on a stack that is
not `LocalStack`; the clause is skipped. The list is what a compose stack cannot assert, not what it
cannot run:

| Row | Stand-in step | Where |
| --- | --- | --- |
| F-03 | the provider counted one refresh grant | `Functional/SessionLifecycleTests.cs:34-36` |
| F-04 | the provider accepted one logout | `SessionLifecycleTests.cs:59-61` |
| F-05 | an expired token at login (`SignInWithExpiredAccessTokenAsync` on the local stack only, a plain login elsewhere), one grant counted, and no token value the provider issued appears in what the browser holds | `Functional/ExposureTests.cs:23-41, 93` |
| F-07 | the same `IServiceProvider` before and after (no restart), three authorizations counted; the admission step is the harness's | `Functional/OnboardingTests.cs:25, 67-71` |
| N-03 | no authorization code was redeemed | `Negative/LoginTests.cs:38-40` |
| N-04 | the one code exchange was app 2's client and ended `invalid_grant` | `LoginTests.cs:73-77` |
| N-07 | the echo upstream saw nothing; the refusal body is kept only on the local stack | `Negative/OriginTests.cs:75, 80-85` |
| N-08 | the provider saw no authorization | `Negative/HostTests.cs:39-41` |
| N-10 | the echo upstream saw nothing, or exactly the one direct call; the echo API's name comes from `Harness.Options.EchoApi` on the local stack and from the app's second API elsewhere | `Negative/ApiTests.cs:43-45, 61-62, 84` |
| N-17 | app 2's last authorization carried no `resource` | `ApiTests.cs:111-113` |
| N-23 | an expired token at login (local only), and no issued token value in any `/bff` body | `Negative/HeaderTests.cs:20-27, 52` |
| N-26 | no code was redeemed for app 1's client | `Negative/CookiePlantingTests.cs:105-107` |

Levers that are the stack's, not the row's, and so change implementation rather than being
skipped: `SignInAs` with the second user (N-04 `LoginTests.cs:62-66`, N-06 `Negative/CsrfTests.cs:32-38`,
N-26 `CookiePlantingTests.cs:76-83`), `StopAtProviderLoginAsync` (F-04, N-06 `CsrfTests.cs:41`,
N-23 `HeaderTests.cs:35`), `StopAtCallbackAsync` (N-03 `LoginTests.cs:27`, N-04 `:63-67`, N-05 `:91`,
N-26 `CookiePlantingTests.cs:77`, N-01 `Negative/SessionTests.cs:50`), the release switch (F-06
`Functional/ShellTests.cs:69-76, 116`) and the onboarding (F-07 `OnboardingTests.cs:35`).

Three things the ticket asks about by name are absent from the stand-in altogether: there is no
`sid` anywhere in `fixtures/TestIdp` or `src/Platform.Identity` (a search for the word finds
nothing); the P2-6 change request records that the stand-in has "no `form_post`, no client
assertion, no `sid` and no back-channel logout" (`contracts/changes/p2-6-pingfederate-profile.md:237`);
and outages are `TestIdpFaults.RefreshOutageStatus`, `RevokeFailureStatus` and the delays
(`TestIdpFaults.cs:51-53, 82-102`), pulled only by the in-process suites (section 2.1), never by
`tests/e2e`.

### 2.4 What a run against a real issuer at a real host needs

What the gateway reads, and where it would come from:

- **The issuer**: `Issuers:{name}:Authority` (https, the discovery base), `Issuers:{name}:Profile`
  and `Issuers:{name}:BrowserOrigin` (https, scheme and authority only; it goes into `form-action`
  and is the base of Cognito's logout URL), all three required
  (`contracts/platform-config.schema.json:58-77`; `src/Platform.Identity/IssuerDirectory.cs:41-47`;
  `src/Platform.Security/SecurityHeaders.cs:118-120`). Under the decided pingfederate profile also
  `Issuers:{name}:ClientAssertion:KeyRef` in the `ssm:{name}:{version}` form, with `Algorithm` and
  `KeyId` optional; a pingfederate issuer whose key cannot be read fails the start
  (`contracts/changes/p2-6-pingfederate-profile.md:80-93, 343-350`). The cell builds the same keys
  as `Issuers__{name}__Authority` and so on (`infra/modules/cell/ecs.tf:68-74`).
- **The client ids and the secret references**: each registry entry's `oauth.clientId` and
  `oauth.clientSecretRef`; the reference is resolved through `GetParameter` with `WithDecryption`
  and its version checked (`src/Platform.Registry/ParameterStoreClientSecretSource.cs:13, 100-115,
  149-154`). `clientSecretRef` stays required under `PrivateKeyJwt`: "the parameter exists and
  resolves; its value is never placed on the scheme's options nor sent" (P2-6 decision 4,
  `p2-6-pingfederate-profile.md:351-352`). So the stand-in for Parameter Store holds, per app, a
  client-secret parameter at the named version, and per issuer the assertion key.
- **The test users' passwords**: the suite types them into a real form (`DeployedStack.cs:252-274`),
  reading each from Parameter Store by the name the outputs document gives (`Cell.cs:105-107`;
  `CellAws.cs:32-39`). A compose stack needs the same two users at the provider, `alice` and `bob`
  with the pool's `sub`, `email` and scope shape (the map's decision), and a way for the test to
  read each password that is not a tracked file.
- **A browser that completes a real login form**: `DeployedStack.FillLoginFormIfShownAsync` is the
  shape, with Cognito's selectors (`DeployedStack.cs:243-274`); PingFederate's form field names were
  not read here (not found: the configuration ticket's or the prototype's).
- **Host names the stack resolves**: the app hosts `app1.spa-poc.test:8443` and
  `app2.spa-poc.test:8443`, port included, because `Policy:AllowedHostSuffixes` is
  `.spa-poc.test:8443` and "refuses a host with no port" (`fixtures/apps/app1.json:3`;
  `src/Platform.Registry/HostPolicy.cs:12`; `contracts/platform-config.example.json`). Chromium's
  `MAP` rule replaces the port too, "so the URL keeps the registry's port while the connection goes
  to the tunnel's" (`Cell.cs:76-81`). The provider's host must resolve to the same issuer URL from
  the browser (on the workstation) and from the gateway and the mock API (in containers), because
  the mock API compares `iss` with `MockApi:Issuer` verbatim (section 3).
- **The exact issuer URL string in three places**: the gateway's `Authority`, the mock API's
  `MockApi:Issuer`, and the provider's own `issuer` in its discovery document and tokens.
- **The gateway's JSON log**: one JSON line per request on stdout (`contracts/telemetry.md:7`);
  `AssertRuleLoggedAsync` needs it (every negative row), so the stack reads the container's log.

Rows that change for a reason other than the stand-in, under the decided profile:

- **F-02** asserts `client_id` and `redirect_uri` in the authorize URL's query
  (`tests/e2e/Functional/SignInTests.cs:55-60`); under the pingfederate profile's PAR `Require`
  "the authorize URL after a push carries" only `client_id` and `request_uri`
  (`p2-6-pingfederate-profile.md:370-373`; the October draft's acceptance box, `.scratch/phase-2/
  spec-p2-6-pingfederate.md:143`).
- **N-03, N-04, N-05** read `code` and `state` from the held callback URL and replay or tamper a
  `GET /signin-oidc?...` (`tests/e2e/Negative/LoginTests.cs:27, 63-67, 91`); under `form_post` the
  callback is a cross-site `POST` body (P2-6 decision 5, `:353-369`). The draft's P2-6e already
  names this: "the rows that read the authorize URL, replay a callback or send a canary callback
  read the stand-in's record or post a body instead" (`spec-p2-6-pingfederate.md:143`). On a real
  provider there is no record to read, so the body must come from the held `POST` itself: the
  `TlsLoadBalancer` hold matches by path (`TlsLoadBalancer.cs:176`) and `NavigationHold` by URL,
  and both hand back a `Uri` today (`IBrowserStack.cs:114`).
- **F-07** onboards by registering a client at the stand-in in process (`GatewayHarness.cs:596-602,
  694-717`) or by the apps root's Terraform apply on the cell (`DeployedStack.cs:386-394`). Against a
  real local provider neither exists: the row needs a client created at the provider at run time,
  from the test, or a third client pre-provisioned by the fixture and only *admitted* into the
  registry during the row. The fixture ticket's question.
- **F-03, F-05, N-23** need an expired access token: the stand-in issues one, the cell waits five and
  a half minutes (`DeployedStack.cs:338-342`). A compose stack either waits out the provider's
  lifetime or configures a short one in the fixture; not decided here.

## 3. The mock API and the apps

- `fixtures/MockApi/Program.cs:21-29`: `MockApi:Issuer` is required, `MockApi:MetadataAddress`
  defaults to `{issuer}/.well-known/openid-configuration`, and `MockApi:App1Client` and
  `MockApi:App2Client` are required; the listeners are fixed, "The two listeners the plan fixes, and
  only those". `MockApiOptions.Default` makes `orders` on 5080 with audience
  `https://orders.spa-poc.test` and `reports` on 5081 with no audience
  (`fixtures/MockApi/MockApiOptions.cs:28-47`). `MockApiHost.cs:113-126, 135-136`: JWT bearer with
  `MetadataAddress`, `RequireHttpsMetadata = false`, `ValidateIssuer` against `options.Issuer`
  ("Checked against this value and not against whatever the metadata document happens to say",
  `MockApiOptions.cs:9-13`), signing keys from discovery, audience only where set.
  `RequireHttpsMetadata = false` admits an `http://` metadata address; it does not make an untrusted
  `https://` certificate acceptable, which the default `HttpClient` validation decides.
- The image: `fixtures/MockApi/Dockerfile:64-76`: `EXPOSE 5080 5081`, "They are reached from the
  gateway container over loopback inside the task, never from outside it", and the three
  environment keys with no default, `MockApi__Issuer`, `MockApi__App1Client`, `MockApi__App2Client`.
  The cell passes `MockApi__Issuer` as the single issuer's authority (`infra/modules/cell/ecs.tf:116-124`).
- The apps: `fixtures/apps/app1.json` (host `app1.spa-poc.test:8443`, issuer `cognito-poc`, scopes
  `openid profile https://orders.spa-poc.test/read`, `resource` `https://orders.spa-poc.test`,
  upstreams `http://127.0.0.1:5080` and `:5081`, session 480 min) and `app2.json` (host
  `app2.spa-poc.test:8443`, the `reports` scope, no `resource`, an `img-src data:` addition). The
  harness rewrites `oauth.issuer`, `clientId`, `clientSecretRef` and every upstream to its own
  listeners (`GatewayHarness.cs:648-675`); the benchmark seeds `contracts/registry.example.json` as
  is, whose one app names `cognito-poc` and `http://127.0.0.1:5080`. The shells in
  `fixtures/spa/{app1,app2,app3}` fetch `/bff/session` and show `signed in` (`fixtures/bff-client.js:16-29`;
  `fixtures/spa/app1/index.html:16`), which `Profile.WaitSignedInAsync` waits for (`Profile.cs:34-38`).
- The users: `fixtures/cognito/pool.json` names `alice` and `bob` with `@spa-poc.test` addresses and
  the two resource servers with `read` and `admin` scopes; "No password is here: task 1.1 generates
  them into Parameter Store".

The consequence for the stack's shape: the loopback upstreams mean the mock API runs in the
gateway's network namespace, as it does in the task (`ecs.tf`; `fixtures/MockApi/Dockerfile:64-67`)
and as the benchmark places the gateway in its holder's namespace with `--network container:`
(`startup.py:610`). Compose's `network_mode: "service:{name}"` "Gives the container access to the
specified container by referring to its service name" (Compose services reference, section 9). The
alternative is upstreams of the form `http://mock-api:5080` in a registry of the stack's own, with
`Policy:AllowedUpstreams` to match; that changes the registry the suites and the benchmark seed.

## 4. CI

Facts only; the map defers the decision to after the fixture ticket.

- The build job runs on `ubuntu-latest` and already uses Docker there for `tools/image-check.sh`
  (`.github/workflows/ci.yml:635, 684-689`); the nightly L-tier job runs on `ubuntu-24.04-arm`
  (`startup-bench.yml:57-58`). GitHub's reference lists `ubuntu-latest` as x64 and
  `ubuntu-24.04-arm` as arm64 in both the public and the private repository tables (4 vCPU / 16 GB
  public, 2 vCPU / 8 GB private); the Ubuntu 24.04 image README lists Docker Client and Server
  28.0.4, Docker Compose 2.38.2, Docker-Buildx 0.38.1 and Python 3.12.3 (section 9).
- The gateway image builds for whatever the runner is (`--platform=$BUILDPLATFORM` and
  `$TARGETARCH`, `src/Gateway/Dockerfile:21-22`; `tools/image-check.sh:32-36`). Compose's `platform`
  attribute "defines the target platform the containers for the service run on" in
  `os[/arch[/variant]]` form, so a provider image can be pinned to `linux/amd64` or `linux/arm64`
  per runner if its manifest offers both; the PingFederate image's architectures are the map's
  charting note (13.1.4, amd64 and arm64) and were not re-verified here.
- The licence: Ping's DevOps page says a container "attempts to find the DevOps registration
  information first in the DevOps property file" and otherwise in environment variables passed
  through `docker run` or "through the YAML file for a stack", naming `PING_IDENTITY_DEVOPS_USER`
  and `PING_IDENTITY_DEVOPS_KEY`, with `PING_IDENTITY_ACCEPT_EULA=YES` in its stack example; the
  registration page says the credentials let you "evaluate Ping Identity products using trial
  licenses automatically". The licence's duration and non-production terms were **not found** on
  those two pages (the licence ticket's). On a runner the pair comes from the `secrets` context
  ("To provide an action with a secret as an input or environment variable, you can use the
  `secrets` context"), and "secrets are not passed to the runner when a workflow is triggered from a
  forked repository". Compose's own `secrets` element takes a `file` or a host `environment`
  variable and a service reads one only when "explicitly granted by a `secrets` attribute".
- The Playwright browser installs itself in the test process (`Chromium.cs:24-29`), so the test
  runner stays on the runner's host, outside compose, as it does today; the compose stack's
  published ports are what it reaches.

## 5. What a compose stack must provide

The list, with the evidence that makes each line necessary. Where two shapes are possible both are
named; nothing here is decided.

### 5.1 Services

| Service | Image | Why |
| --- | --- | --- |
| `gateway` | `src/Gateway/Dockerfile`, built from the commit under test (`startup.py:523-537` builds from `git archive HEAD`) | the thing proved; port 8080 inside (`Dockerfile:90-91`) |
| `mock-api` | `fixtures/MockApi/Dockerfile` | the F-08, N-10, N-17 upstreams; `network_mode: "service:gateway"` so `http://127.0.0.1:5080` and `:5081` resolve as in the task (section 3) |
| `aws` (moto) | `motoserver/moto:5.2.4`, the research's pin (`startup.py:96`) | S3 for the registry and releases, SSM for the client secrets and the assertion key; a static address on a defined subnet (`ipv4_address`: "Specify a static IP address for a service container when joining the network") because the SDK uses path style only on an IP-literal `AWS_ENDPOINT_URL` (`startup.py:41-45`) |
| `seed` | the moto image running a seed, as `startup.py seed` does (`:442-469`), or `spactl` | a one-shot service the others `depends_on` with `condition: service_completed_successfully` ("Specifies that a dependency is expected to run" to completion) |
| `idp` | the PingFederate image under the evaluation licence, from `fixtures/pingfederate/` (the map's decision) | the issuer; its port, health endpoint and start-up time are the sibling tickets' |
| the TLS terminator | either `tests/e2e/Stack/TlsLoadBalancer` in the test process, pointed at the gateway's published port (`TlsLoadBalancer.StartAsync(Uri gateway, string domain, ...)`, `:69`), or a container in the stack | the ALB stand-in: HTTPS for `*.spa-poc.test`, `X-Forwarded-Proto`, `-Port`, `-For` set, `X-Forwarded-Host` passed through (`LoadBalancerTransport.cs:12-14`) |
| an echo upstream | optional; `GatewayHarnessOptions.EchoApi` is an in-process listener | only the in-process N-11/N-12 use it; the browser rows fall back to the app's second API (`ApiTests.cs:62`) |

The gateway runs under the task's limits: Compose's `cpus` ("a fractional number") and `mem_limit`
("a limit on the amount of memory a container can allocate"), which "must be consistent with" the
Deploy Specification's `resources.limits.cpus` and `.memory` when both are set; the Deploy
Specification is optional and "If not implemented the `deploy` section is ignored", so the two
service-level attributes are the ones to write. The benchmark's values are `--cpus 0.25 --memory 1g`
(`startup.py:607`), the task's 256 units and 1024 MB (`ecs.tf:272-273`).

Ordering: `depends_on` with `condition: service_healthy` ("Specifies that a dependency is expected
to be 'healthy'") can gate the gateway on `idp` and `aws`, each with a `healthcheck` (`test`,
`interval`, `timeout`, `retries`, `start_period`). It cannot gate anything on the gateway's own
health: the chiseled image has no shell or tool to run a `healthcheck` command with
(`Dockerfile:60-67`; `image-check.sh:25-30` fails an image that has `/bin/sh`), so readiness is
polled from outside, as the benchmark's probe polls `/readyz` (`startup.py:198-204`) and
`GatewayHarness.StartHostCoreAsync` waits for the revision to go live (`GatewayHarness.cs:395-402`).

### 5.2 Hosts

- The app hosts `app1.spa-poc.test:8443`, `app2.spa-poc.test:8443` and, for F-07,
  `app3.spa-poc.test:8443`, plus the hostile sibling `evil.spa-poc.test` and the foreign site
  `attacker.example` (`ForeignSites.cs:30-33`; `LocalStack.cs:34`): all resolved by Chromium's
  `--host-resolver-rules` to the terminator (`LocalStack.cs:46-51`), so no hosts-file line is
  needed for them on the workstation, as none is today. The terminator listens on 8443 or the
  `MAP` rule rewrites the port (`Cell.cs:77-81`).
- **One issuer host name, resolved the same way everywhere**: the browser on the workstation
  (through a `MAP` rule or a hosts-file line, as `docs/plan/05-infrastructure-and-cost.md:65` adds
  one for the app hosts), the gateway container and the mock API container (a Compose network
  alias, or `extra_hosts`, which "adds hostname mappings to the container network interface
  configuration (`/etc/hosts` for Linux)"). The issuer URL carries the port, so the port published
  to the workstation equals the port inside the network, or the URLs differ and the login breaks at
  `iss` (`MockApiHost.cs:124-125`) or at discovery. If the provider's URL must instead name the
  workstation (for example when the provider's hostname is fixed by its own configuration), the
  containers reach the host through `extra_hosts: ["<name>:host-gateway"]`, where `host-gateway`
  "resolves to the internal IP address of the host" and "Docker Desktop automatically resolves this
  hostname".
- Loopback is not an option for the issuer: `SharedOidc` admits plain HTTP only to a loopback IP
  (`IssuerDirectory.cs:98-107`), the schema requires `https://` (`platform-config.schema.json:62,
  77`), and a container's `127.0.0.1` is not the workstation's, so the browser could not reach it.

### 5.3 Certificates

- **The app domain**: the terminator's certificate. `TlsLoadBalancer` makes a self-signed one per
  run and the browser context ignores certificate errors (`TlsLoadBalancer.cs:73`;
  `LocalStack.cs:165-166`); a container terminator can reuse `tools/scripts/certs.sh`'s authority
  and wildcard (`certs.sh:3-11, 39`), which the deployed stack already requires the workstation to
  trust (`DeployedStack.cs:13-16, 211-214`).
- **The issuer**: an `https://` authority that the gateway's backchannel and the mock API's
  `HttpClient` validate with the default chain, and that Chromium reaches directly (the browser's
  `IgnoreHTTPSErrors` covers Chromium's side on the local stack; the two containers have no such
  switch). On Linux ".NET uses OpenSSL (libssl) to locate trusted root certificates. OpenSSL
  determines the certificate store location using environment variables (`SSL_CERT_FILE` and
  `SSL_CERT_DIR`) and distribution-specific default paths"; the certificate files must be "in PEM
  format with the `BEGIN CERTIFICATE` marker" (Microsoft Learn, section 9). Both images are
  chiseled, with no package manager to install a CA with and "You can only install packages that
  are available as slices" (`ubuntu-chiseled.md`), so the stack mounts a PEM bundle (the stack's own
  authority plus the base image's bundle, or the authority alone where the provider is the only
  TLS peer) and sets `SSL_CERT_FILE` or `SSL_CERT_DIR` on both containers, or rebuilds the final
  stage. Whether the PingFederate image can be given a certificate from that same authority, and
  on which port it serves TLS, was not read here (the configuration ticket's).

### 5.4 Environment keys

The gateway's, flattened as `infra/modules/cell/ecs.tf:49-113` and `startup.py:375-399` flatten the
platform configuration (`:` to `__`, every value a string):

| Key | Value in the stack | Evidence |
| --- | --- | --- |
| `Platform__Cell` | a name for the stack | `ecs.tf:81`; `GatewayHarness.cs:205` |
| `Registry__Bucket`, `Registry__Key`, `Registry__PollSeconds`, `Assets__Bucket` | the seeded bucket, `registry/registry.json`, a short poll for F-06 | `GatewayHarness.cs:206-209`; `contracts/platform-config.example.json` |
| `Issuers__<name>__Authority`, `__Profile`, `__BrowserOrigin` | the provider's https URL, `pingfederate`, the same origin | `IssuerDirectory.cs:41-47`; `ecs.tf:68-74` |
| `Issuers__<name>__ClientAssertion__KeyRef` (and optional `__Algorithm`, `__KeyId`) | `ssm:/.../assertion-key:1` in the stand-in | P2-6 `:80-93, 343-350` |
| `Policy__AllowedHostSuffixes__0` | `.spa-poc.test:8443` | `GatewayHarness.cs:213`; `HostPolicy.cs:12` |
| `Policy__AllowedUpstreams__0`, `__1` | `http://127.0.0.1:5080`, `http://127.0.0.1:5081` (shared namespace) or the service names | `ecs.tf:96-97`; `fixtures/apps` |
| `ForwardedHeaders__TrustedNetworks__0` | the terminator's address as the gateway sees it: the bridge address of the test process's connections, or the terminator container's address; not `127.0.0.1/32` | `GatewayHarnessOptions.cs:36-40`; `ecs.tf:60-64, 105` (the cell trusts the ALB's subnets) |
| `Session__Store`, `Session__MaxSessions` | `memory`, 10000; no `DataProtection__ParameterPrefix` means no ring, which one container does not need | `ecs.tf:91, 109-113`; `platform-config.schema.json:134-135` |
| `AWS_ENDPOINT_URL` (or `_S3` and `_SSM`), `AWS_REGION`, `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` | moto's IP-literal URL, `us-east-1`, any pair | `startup.py:397-398`; the stand-in research (#170): the .NET chain reads only `AWS_REGION` |
| `ASPNETCORE_HTTP_PORTS` | 8080, as the image sets | `Dockerfile:91`; `ecs.tf:137-139` |
| `SSL_CERT_FILE` or `SSL_CERT_DIR` | the mounted bundle | section 5.3 |

The mock API's: `MockApi__Issuer` (the same issuer string), `MockApi__App1Client`,
`MockApi__App2Client` (`fixtures/MockApi/Dockerfile:74-76`; `ecs.tf:122-124`), optionally
`MockApi__MetadataAddress` (`Program.cs:22-24`), and the same `SSL_CERT_*`.

The provider's: the licence pair and the EULA acceptance (section 4) and whatever the server
profile in `fixtures/pingfederate/` needs; the sibling tickets'.

The test process's: a new value of `SPA_E2E_STACK` for the third stack (`StackSwitch.cs:24-30`
refuses an unknown one today), the terminator's or gateway's published port, where to read the
gateway's log, how to read the two passwords, and, if the row keeps its deployed shape, the release
switch's `SPA_E2E_PLATFORM_CONFIG`.

### 5.5 Secrets

| Secret | Where it lives in the stack | Evidence |
| --- | --- | --- |
| Each app's client secret, at the version its `clientSecretRef` names | a moto SSM `SecureString`, as the seed writes it (`startup.py:458-463`) | `ParameterStoreClientSecretSource.cs:100-154`; P2-6 decision 4 (still required, never sent under `PrivateKeyJwt`) |
| The gateway's assertion key, PKCS#8 PEM | a moto SSM parameter at `KeyRef`; its public JWK pasted on the provider's client records | P2-6 `:80-93`; the map's "assertion key" decision ("locally, the compose stack generates one into the L tier's file-backed stand-in") |
| The two test users' passwords | at the provider, and readable by the test: on the cell they are Parameter Store parameters read with `--with-decryption` (`CellAws.cs:32-39`); the stand-in's SSM can hold them the same way, or the compose `secrets` element (a `file` or a host `environment` variable, read only by a service that declares it) | `Cell.cs:13-14, 105-107` |
| The licence pair `PING_IDENTITY_DEVOPS_USER` / `_KEY` | the shell's environment on the workstation, the `secrets` context on a runner, never in the compose file or the fixture | section 4 |
| The terminator's and the issuer's private keys | generated per run or per fixture; "Private keys never leave the certificates directory and are never printed" (`certs.sh:25`) | section 5.3 |

A mismatch to record, not resolve: the map's assertion-key decision says "the L tier's file-backed
stand-in", but the one L-tier stand-in today, moto in server mode, is seeded fresh on every `up`
from the tracked files (`startup.py:442-469, 548-566`) and keeps nothing between runs. A key that
must survive a `docker compose down` needs a volume for moto, a key generated at seed time and
pasted on the provider's client in the same seed, or a key committed to the fixture (which the
map's rotation runbook line argues against). The fixture ticket's.

## 6. Which suite ids can and cannot run against a real issuer

"Can" means the row's assertions on a non-`LocalStack` stack make sense against a real provider
once the third stack of section 2.2 exists; "cannot" means the row drives an in-process stand-in
and has no deployed form either. Tiers are the export's (`docs/plan/04-tests.md:18-66`) and the
register's (`contracts/test-ids.md:35-56`).

**Can, as the browser rows stand (`tests/e2e`), with the "In H" clause skipped:** F-01, F-03 (needs a
real expiry: a wait or a short lifetime), F-04, F-05, F-06 (needs the release switch against the
stand-in bucket and a real poll), F-08, N-01, N-06, N-07, N-08, N-10, N-17, N-19, N-23, N-24, N-25,
N-26, N-27. N-25 and N-26 are the two the export says have their "deciding run ... in a real
browser" (`04-tests.md:68`), and the P2-6 decision names "N-25's shape" as where `SameSite=None;
Secure` on the cross-site `POST` is proved (`p2-6-pingfederate-profile.md:361-362`).

**Can, after the P2-6e rework the October draft already lists:** F-02 (reads `client_id` and
`redirect_uri` from the authorize URL; PAR hides the latter), N-03, N-04, N-05 (read `code` and
`state` from a held `GET`; `form_post` moves them into a `POST` body). The rework is independent of
the provider being real.

**Can only with a client created at the real provider during the row, or pre-provisioned:** F-07
(section 2.4).

**Cannot: every row of `tests/Gateway.IntegrationTests` and `tests/Gateway.SecurityTests`**, because
the gateway is built in the test process by `WebApplicationFactory` with its stand-ins injected
(`GatewayFactory.cs:23`; `GatewayHarness.cs:287-316`), whatever the row asserts. Among them the ids
the export marks H only, each with the stand-in lever it pulls (section 2.1): F-09 (`SetClientSecret`,
`Secrets[]`, registry writes), F-10 (`Faults.TokenDelay`, `RefreshAnswerDelay`, `RefreshRotationGrace`),
F-11 (the captured telemetry), N-02, N-18, N-21, N-22 (`DefaultSubject`, registry writes,
`AddClient`), N-09 (`TrustedNetworks`), N-11, N-12 (the echo upstream and a hostile upstream in
process), N-13, N-20 (raw sends into the in-process host), N-14 (`PublishRegistryTextAsync`), N-15,
N-16 (the canary sweep over `Harness.Log`), and the register's F-12 to F-18 and N-28 to N-31 (two
hosts, `SwitchableSessionStore`, `ParameterStoreStub`, `RotateClientAsync`, `SteppedClock`). Their
H-tier copies of F-01 to F-08 and N-01, N-03 to N-08, N-10, N-17, N-19, N-23 to N-27 are the same:
in process, and so not candidates; the browser rows are those ids' candidates.

**Not a composed suite:** `B-01` is proved by the benchmark's verdict, never a test project
(`GLOSSARY.md:536-541`); the two owed ids for item 7's logout token (`contracts/test-ids.md:93-96`)
have no test yet and will need the real provider to deliver a back-channel `POST` to an app host the
stack resolves, which the map's "Which Ping" decision already requires.

## 7. Open points this leaves to the fixture and stack tickets

1. The provider's issuer URL, TLS port, certificate and health endpoint, and how the stack's
   authority reaches the PingFederate image: not read here.
2. F-07's third client: created through the provider's administrative API during the row, or
   pre-provisioned and only admitted into the registry.
3. The expired-token rows: a wait of the provider's access-token lifetime, or a shorter lifetime in
   the fixture's OIDC policy.
4. Where the two passwords live so the test can read them without a tracked file: the stand-in's
   SSM, mirroring `CellAws.PasswordAsync`, or compose `secrets`.
5. The assertion key's persistence across `down`/`up` against the map's "file-backed stand-in"
   wording (section 5.5).
6. The terminator: the existing in-process `TlsLoadBalancer` over a published port (keeps `Hold`,
   `ServeForeign` and the exchange record; needs the gateway to trust the bridge address) or a
   container (needs the holds and the foreign pages re-done in the browser, as `DeployedStack` does).
7. The held-callback shape for `form_post` (a `POST` body, not a `Uri`), shared with the hermetic
   P2-6e rework.
8. Whether the gateway's and the mock API's final stages gain `SSL_CERT_FILE` at the image or at the
   stack; an image change is the integrator's and workstream 5's, a mount is the stack's.

## 8. Sources in the repository (commit `8ceeee3`)

`GLOSSARY.md` (503-515, 475-478, 536-541); `docs/plan/04-tests.md` (7-66); `docs/plan/
05-infrastructure-and-cost.md` (60-65); `docs/adr/0033-seven-startup-clocks-against-a-startup-floor-
budgeted-at-twice-the-worst-max.md` (44-49, 62-65, 95-100); `contracts/test-ids.md` (6, 31-56, 75-96);
`contracts/platform-config.schema.json` (58-77, 134-135); `contracts/platform-config.example.json`;
`contracts/registry.example.json`; `contracts/infra-outputs.schema.json`; `contracts/telemetry.md`
(7); `contracts/changes/p2-6-pingfederate-profile.md` (80-93, 229-243, 343-373);
`.scratch/phase-2/spec-p2-6-pingfederate.md` (107, 143); `tests/e2e/Stack/{LocalStack,
DeployedStack, IBrowserStack, StackSwitch, TlsLoadBalancer, Chromium, Cell, CellAws, ForeignSites,
Profile, RawClient}.cs`; `tests/e2e/Functional/*.cs`; `tests/e2e/Negative/*.cs`;
`tests/e2e/Gateway.E2ETests.csproj`; `tests/Gateway.TestHarness/{GatewayHarness,
GatewayHarnessOptions, GatewayFactory, GatewayHost, TestBrowser, ParameterStoreStub,
SwitchableSessionStore, SwitchableObjectStore, LoadBalancerTransport}.cs`;
`tests/Gateway.IntegrationTests/**` and `tests/Gateway.SecurityTests/**` (the driver lines in
section 2.1); `fixtures/TestIdp/{TestIdentityProvider, TestIdpFaults, TestIdpRecords}.cs`;
`fixtures/MockApi/{Program, MockApiOptions, MockApiHost, ClientAdmission}.cs` and `Dockerfile`;
`fixtures/apps/*.json`; `fixtures/cognito/pool.json`; `fixtures/spa/`; `fixtures/bff-client.js`;
`fixtures/Fixtures.Tests/MockApiDockerfileTests.cs`; `tools/bench/startup.py`, `tools/bench/results/
README.md`, `tools/bench/floor/Dockerfile`; `tools/image-check.sh`; `tools/smoke.sh`;
`tools/scripts/certs.sh` (3-25, 39); `src/Gateway/Dockerfile`; `src/Platform.Identity/
{IssuerDirectory, OidcOptionsSetup}.cs`; `src/Platform.Registry/{ParameterStoreClientSecretSource,
HostPolicy}.cs`; `src/Platform.Security/SecurityHeaders.cs`; `src/Platform.Testing/TestApps.cs`;
`infra/modules/cell/ecs.tf`; `.github/workflows/{ci,startup-bench}.yml`; `.github/startup-bench.sh`.
Tracker: #949 and #944 (bodies), #169 (body, Decisions so far) and its sub-issues through
`gh api repos/adammedford/spa-platform/issues/169/sub_issues` (#170 to #178; eight closed, #178 open).
`docs/research/local-aws-stand-in.md` on branch `research/local-aws-stand-in` (its Answer in brief,
for the `AWS_ENDPOINT_URL` and `AWS_REGION` facts the benchmark relies on).

## 9. What was executed and what was only read

**Executed** (2026-11-04): `git fetch origin`, `git worktree add`, `gh issue edit/view/api` for the
tickets named above, and `grep`, `sed`, `awk`, `cat` and `python3 -I -c` (JSON reads of the schemas
and fixtures) over the checkout. Nothing else: no `docker`, no `dotnet build` or `dotnet test`, no
`tools/bench/startup.py`, no `tools/image-check.sh`, no `tools/smoke.sh`, no AWS CLI or SDK call, no
Terraform, nothing against PingFederate.

**Read on the web** (2026-11-04), each with what it gave:

- Docker Compose services reference, https://docs.docker.com/reference/compose-file/services/ :
  `extra_hosts` ("adds hostname mappings to the container network interface configuration
  (`/etc/hosts` for Linux)"), `healthcheck` (`test`, durations), `depends_on` long syntax
  (`condition`: `service_started`, `service_healthy`, `service_completed_successfully`; `required`),
  `platform` (`os[/arch[/variant]]`), `network_mode` (`service:{name}`), `ipv4_address`, `cpus`,
  `mem_limit`, and "If not implemented the `deploy` section is ignored". The page does not mention
  `host-gateway`.
- Docker Compose deploy specification, https://docs.docker.com/reference/compose-file/deploy/ :
  `resources.limits.cpus` and `.memory`; "Deploy is an optional part of the Compose Specification".
- Docker Compose secrets, https://docs.docker.com/reference/compose-file/secrets/ : a secret from a
  `file` or a host `environment` variable; "Services can only access secrets when explicitly granted
  by a `secrets` attribute". The page does not state the mount path.
- `docker container run` reference, https://docs.docker.com/reference/cli/docker/container/run/ ,
  `--add-host`: "a special `host-gateway` value that resolves to the internal IP address of the
  host"; "Docker Desktop automatically resolves this hostname".
- Microsoft Learn, Cross-platform cryptography in .NET,
  https://learn.microsoft.com/en-us/dotnet/standard/security/cross-platform-cryptography : "Trusted
  root certificate locations on Linux" (OpenSSL, `SSL_CERT_FILE`, `SSL_CERT_DIR`, the `/etc/ssl/certs`
  fallback, PEM with `BEGIN CERTIFICATE`); `LocalMachine\Root` is read-only on Linux.
- dotnet-docker, Ubuntu Chiseled,
  https://github.com/dotnet/dotnet-docker/blob/main/documentation/ubuntu-chiseled.md : "No package
  manager", "No shell", "Non-root user by default", "You can only install packages that are available
  as slices". It does not mention CA certificates.
- GitHub Docs, GitHub-hosted runners reference,
  https://docs.github.com/en/actions/reference/runners/github-hosted-runners : the Linux x64 and
  arm64 label rows for public (4 vCPU, 16 GB) and private (2 vCPU, 8 GB) repositories.
- actions/runner-images, Ubuntu 24.04 README,
  https://github.com/actions/runner-images/blob/main/images/ubuntu/Ubuntu2404-Readme.md : Docker
  Client and Server 28.0.4, Docker Compose 2.38.2, Docker-Buildx 0.38.1, Python 3.12.3 (image
  version 20261026.354.1).
- GitHub Docs, Using secrets in GitHub Actions,
  https://docs.github.com/en/actions/security-for-github-actions/security-guides/using-secrets-in-github-actions :
  the `secrets` context, forks, the 48 KB workaround.
- Ping Identity DevOps, Using Your DevOps User and Key,
  https://developer.pingidentity.com/devops/how-to/devopsUserKey.html : the lookup order (property
  file, then `docker run` or the stack YAML), `PING_IDENTITY_DEVOPS_USER`, `PING_IDENTITY_DEVOPS_KEY`,
  `PING_IDENTITY_ACCEPT_EULA=YES` in the stack example. Licence duration and terms: not on page.
- Ping Identity DevOps Registration,
  https://developer.pingidentity.com/devops/how-to/devopsRegistration.html : "evaluate Ping Identity
  products using trial licenses automatically"; registration through the Support and Community
  profile. Duration, non-production and the 90-day image rule: not on page.
- Not reached: https://devops.pingidentity.com/get-started/devopsUserKey/ and
  `.../devopsRegistration/` (404), and the GitHub-hosted runners concepts page (no architecture
  facts); the reference page above replaced it. `hub.docker.com/r/pingidentity/pingfederate` was not
  read: the image's architectures and tags are the sibling ticket's.
