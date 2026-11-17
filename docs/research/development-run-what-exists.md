# What already runs a working app on a workstation outside a test

- **Ticket:** #1318, "What already runs a working app on a workstation outside a test: the sitting
  browser's local cell, the gateway test harness and the Local tier", on the map #1317, whose
  destination is one spec that separates development, the proof cell and adoption.
- **Date:** 2026-11-17.
- **Branch:** `research/development-run-what-exists`, pushed and never merged, as the map rules.
- **Method:** repository reading at commit `df4cf96` (origin/main on 2026-11-17, fetched at the start
  of the session), and four things executed on this workstation (macOS, arm64, .NET SDK 10.0.401)
  from a worktree of that commit: the sitting browser on its local stack, a raw-socket login through
  that stack's TLS listener, the mock API started by hand, and nothing else. Section 9 lists each
  and separates it from what was only read. Not done: no AWS call of any kind, no apply, no
  credential step, no read of the deployed cell (the `aws-mcp` connector failed to connect and the
  map rules it out); no `docker` command, so the startup benchmark and `tools/image-check.sh` are
  described from their source; no `dotnet test`; nothing against any identity provider but
  `fixtures/TestIdp`. Line numbers are at `df4cf96`.

## Answer in brief

1. **Yes, one thing already stands up the gateway with an identity provider, an API and an app
   outside `dotnet test`, and it is one command.** `dotnet run --project tests/e2e --
   sitting-browser --bundle <dir> start`, with `SPA_E2E_STACK` unset or `local`, starts the whole
   hermetic cell in one process and keeps it up until `stop` or a signal: the shipped gateway
   composition on Kestrel, `fixtures/TestIdp`, the real `fixtures/MockApi` on two listeners, an echo
   upstream, an in-memory bucket holding `fixtures/spa` and a registry built from `fixtures/apps`, a
   TLS stand-in for the ALB, and a headless Chromium. Executed here: it printed
   `ready ... stack=local`, logged `alice` in to app 1 and `bob` in to app 2, read each app's marker
   and session, and got 200 from `/api/orders/whoami` and `/api/reports/whoami` (section 1).
2. **It is the sitting browser, built for the proof cell, and its local stack is a side effect of
   the stack switch's default.** The wiz skill treats `stack=local` at a sitting as a failed step.
   No document offers it as a way to run the gateway; the README and `AGENTS.md` name build, test
   and smoke only.
3. **A person cannot point their own browser at it as it stands, but the cell does answer a client
   that is not the suite's Chromium.** The browser is headless by a hard-coded flag and is driven
   through observation lines, never seen. The TLS listener is on a loopback port the system picks,
   printed nowhere (found here with `lsof`), behind a certificate made for the run and trusted by
   no store, and the gateway answers only the authority `appN.spa-poc.test:8443`. Executed here: a
   raw TLS client that sent `Host: app1.spa-poc.test:8443` to that port completed a login and
   received the fixture page; the same host without `:8443`, or with the listener's real port, got
   404. So a hosts entry alone can never be enough: a browser needs the name and port 8443 both
   remapped to the listener (Chromium's `--host-resolver-rules`, or a hosts entry plus a forward
   from 8443) and the certificate accepted (section 2).
4. **The gateway test harness is the cell in item 1; by itself it is a library with no entry
   point.** `tests/Gateway.TestHarness` is what `LocalStack` starts. Nothing else launches it
   outside a test (section 3).
5. **The Local (L) tier is not this, and cannot serve an app to a person.** L is the startup
   benchmark, `tools/bench/startup.py`: the built gateway image in Docker against moto and a static
   discovery document. It binds no host port, has no token endpoint and no mock API, and no login
   can complete against it. The name `LocalStack` and the phrase "the local cell's hosts" belong to
   item 1, which the glossary calls Hermetic (section 4).
6. **Three things start the gateway alone, and none serves an app.** `tools/smoke.sh` (empty
   registry, already established), `tools/image-check.sh` (the image, no configuration, no port
   published) and the benchmark (item 5) (sections 4 and 5).
7. **The pieces differ in what they are without the harness.** `fixtures/MockApi` is a program:
   executed here by hand with three settings, it listened on 5080 and 5081 and answered 401 with no
   token. `fixtures/TestIdp` is a class library with no entry point and no login page.
   `fixtures/apps` and `fixtures/spa` are files (section 6).
8. **The shipped gateway has no configuration that selects an in-memory bucket or secret source.**
   It registers S3 and Parameter Store unconditionally, and the harness replaces them through
   `ConfigureTestServices`. Running the published gateway by hand with an app therefore needs an
   AWS stand-in; the only recipe in the repository is the benchmark's (section 7).
9. **Conclusion: an interactive development run exists under another name, for an agent and not for
   a person.** The sitting browser's local stack is a resident, working cell with a command surface,
   needing only the .NET SDK and Playwright's Chromium. What it lacks for a person is small and
   specific: a printed address, a fixed or forwarded port, and a headed or external browser
   (section 8 lists it for the ticket "The development run").

## 1. The sitting browser on its local stack

**What it starts.** `tests/e2e/Program.cs:25` sends a first argument of `sitting-browser` to the
sitting browser instead of the test runner. `start` runs the resident process
(`Program.cs:87-126`), which calls `Stacks.StartCancellableAsync` (`Program.cs:113`). That reads the
switch `SPA_E2E_STACK`: unset, empty or `local` is the local stack, `deployed` is the cell, anything
else is refused (`tests/e2e/Stack/StackSwitch.cs:24-30`, `IBrowserStack.cs:177-183`).
`LocalStack.StartCancellableAsync` (`tests/e2e/Stack/LocalStack.cs:42-66`) then starts, in this one
process:

- `GatewayHarness.StartAsync` (section 3): the gateway, the test identity provider, the mock API's
  two listeners, the echo upstream, the in-memory bucket and registry;
- `TlsLoadBalancer`, an HTTPS listener on loopback that forwards to the gateway with the headers
  the ALB sets (`tests/e2e/Stack/TlsLoadBalancer.cs:15-34, 74-92`);
- a Chromium whose resolver sends `*.spa-poc.test` and `attacker.example` to that listener
  (`LocalStack.cs:52-53`).

**What it stands in for.** The proof cell as a browser sees it: the internal ALB, one gateway task,
the bucket, Parameter Store, the identity provider and the mock API container. The glossary calls
this the Hermetic tier (`GLOSSARY.md:529`); `StackKind.Local` is the suite's name for it and is the
default CI runs (`StackSwitch.cs:6-7`).

**How it is launched.** `tools/wiz_prelude.sh:78` defines the one launch line:
`dotnet run --project tests/e2e -- sitting-browser --bundle "$E" "$@"`. The wiz skill sets
`SPA_E2E_STACK=deployed` before `start` and says a ready line without `stack=deployed` is a failed
step, because "the switch unset starts the local stack" (`.claude/skills/wiz/d.md:74, 82-84`). The
suite's own facts run the same engine on the local stack in process
(`tests/e2e/SittingBrowser/SittingBrowserTests.cs`). The commands after `start` are `status`,
`open`, `login`, `close`, `reload`, `marker`, `session`, `api`, `clear-provider`, `watch`, `unwatch`
and `stop` (`tests/e2e/SittingBrowser/SittingCommand.cs:22-34, 87-95`); each is a short client call
over a Unix socket in the bundle directory.

**What it needs.**

- The .NET 10 SDK, and Playwright's Chromium, which the process installs itself on first use
  (`tests/e2e/Stack/Chromium.cs:22-30`). No Docker, no AWS credential, no hosts entry, no
  certificate in any store, no fixed port.
- A bundle directory that exists, whose path keeps `browser.sock` under the Unix socket limit (103
  bytes on macOS; a longer one is refused as `socket-path-too-long`,
  `SittingBrowserServer.cs:24, 92-96`). The session's scratch directory was too long, so the run
  below used a short symbolic link to it.
- `PWDEBUG` unset and no `pw:` channel in `DEBUG`, or the start is refused
  (`SittingBrowserServer.cs:38-47`).

**Executed (2026-11-17, test identity provider only).** From the worktree, after
`dotnet build tests/e2e`:

```
SPA_E2E_STACK=local dotnet run --no-build --project tests/e2e -- sitting-browser --bundle <dir> start
```

run in the background. Its standard output, in full:

```
ready         2026-11-17T19:28:57Z - stack=local
stopped       2026-11-17T19:29:44Z - reason=stop
```

The client calls, each exit 0:

```
status        2026-11-17T19:29:05Z - profiles=0
signed-in     2026-11-17T19:29:06Z main app=app1 revision=1 instance=0d4ce261f10c426abf68c19cf48127fa      (open main --app app1)
signed-in     2026-11-17T19:29:06Z main app=app1 as=alice revision=1 instance=0d4ce261f10c426abf68c19cf48127fa   (login main --app app1)
marker        2026-11-17T19:29:07Z main app=app1 text=Fixture SPA app1
session       2026-11-17T19:29:07Z main app=app1 http=200 problem=-
api           2026-11-17T19:29:07Z main app=app1 path=/api/orders/whoami http=200 problem=-
signed-in     2026-11-17T19:29:08Z two app=app2 as=bob revision=1 instance=0d4ce261f10c426abf68c19cf48127fa      (login two --app app2 --as bob)
api           2026-11-17T19:29:08Z two app=app2 path=/api/reports/whoami http=200 problem=-
```

`open` alone already reports `signed-in`: the test provider approves every authorization at once
and has no login page (`fixtures/TestIdp/TestIdentityProvider.cs:315`), so the first navigation to
an app completes a login. The process held six loopback listeners (`lsof -a -p <pid> -iTCP
-sTCP:LISTEN`): by the order they start and by the server log, the provider, the two mock API
listeners, the echo upstream, the gateway and the TLS stand-in. `stop` closed both profiles; the
process, the headless Chromium and the socket were gone afterwards, and `browser.server.log` (mode
600) stayed in the bundle.

## 2. Whether a person can point a browser at it

Not as it stands. Four facts, the first three read and the fourth executed:

- **The browser is headless and cannot be made otherwise from outside.**
  `Chromium.LaunchAsync` passes `Headless = true` as a literal (`Chromium.cs:38`), and the refusal of
  `PWDEBUG` closes Playwright's own headed switch. The sitting browser is defined to yield
  observation lines and never a screenshot (`GLOSSARY.md:52-57`).
- **The address is not printed.** The TLS listener binds loopback port 0
  (`TlsLoadBalancer.cs:80`). The ready line carries only `stack=local`
  (`tests/e2e/SittingBrowser/Observations.cs:89-90`). In the run above the server log named the
  gateway's and the mock API's ports and neither the TLS listener's nor the provider's; `lsof` on
  the process was the only way to find them.
- **The certificate is the run's own.** Self-signed for `*.spa-poc.test`, valid one day, generated
  at start and trusted by no store (`TlsLoadBalancer.cs:347-362`); the suite's contexts ignore
  certificate errors (`LocalStack.cs:175`). Read from the listener in the run above: subject and
  issuer `CN=*.spa-poc.test`, one SAN `DNS:*.spa-poc.test`.
- **The gateway answers one authority per app, and it names port 8443.** The registry hosts come
  from `fixtures/apps` unchanged, `app1.spa-poc.test:8443` and `app2.spa-poc.test:8443`
  (`fixtures/apps/app1.json`, `GatewayHarness.cs:695-717`), and host resolution requires the
  request authority to equal a registry host exactly (`src/Gateway/Program.cs`, the comment on
  `UsePlatformHostResolution`). Chromium's rule `MAP *.spa-poc.test <listener>` rewrites name and
  port together, which is how the suite's browser reaches a listener on another port while still
  sending `Host: app1.spa-poc.test:8443`.

**Executed.** A Python script opened raw TLS sockets to the stand-in's port (certificate
verification off, SNI `app1.spa-poc.test`), sent the request line and `Host` itself with a
navigation's Fetch Metadata headers, and followed redirects with its own cookie jar; the provider
on loopback was reached directly over plain HTTP. No cookie, code or state value was printed.

```
GET https://app1.spa-poc.test:8443/                 -> 302 -> http://127.0.0.1:<provider>/authorize
GET http://127.0.0.1:<provider>/authorize?...       -> 302 -> https://app1.spa-poc.test:8443/signin-oidc
GET https://app1.spa-poc.test:8443/signin-oidc?...  -> 302 -> https://app1.spa-poc.test:8443/
GET https://app1.spa-poc.test:8443/                 -> 200, body contains "Fixture SPA app1"
GET /bff/session, Host app1.spa-poc.test:8443, with the cookies    -> 200
GET /, Host app1.spa-poc.test (no port)                            -> 404
GET /, Host app1.spa-poc.test:<the listener's real port>           -> 404
```

So the cell behind the sitting browser serves any client that presents the right authority; what
stands between it and a person's browser is reaching it. Read, not tried: an ordinary Chrome
started with `--host-resolver-rules="MAP *.spa-poc.test 127.0.0.1:<port>"` and a certificate
exception, or a hosts entry for the app names plus a forward from 127.0.0.1:8443 to the listener,
would each satisfy the four facts. A hosts entry alone would not: the browser would have to name
the listener's port in its URL, and the gateway 404s that authority.

## 3. The gateway test harness

**What it starts** (`tests/Gateway.TestHarness/GatewayHarness.cs:18-54, 172-365`):

- the gateway's own `Program.cs` through `WebApplicationFactory`, on Kestrel on a loopback port the
  system picks, in the `Production` environment, with a copy of `src/Gateway/appsettings.json` as
  its content root (`GatewayFactory.cs:23, 36, 55, 104-115`). Nothing in the pipeline is replaced;
- `fixtures/TestIdp` on a loopback port, one client per app (`GatewayHarness.cs:176-178, 695-717`);
- the real mock API on two loopback listeners, `orders` and `reports`, ports picked by the system
  (`GatewayHarness.cs:181-183`), and an echo upstream;
- an `InMemoryObjectStore` holding the registry object and every release from `fixtures/spa`, and a
  `FakeClientSecretSource`, registered over the gateway's S3 and Parameter Store services
  (`GatewayHarness.cs:287-290`);
- gateway configuration written in code: the issuer pointed at the test provider, the allowed host
  suffix `.spa-poc.test:8443`, the mock API's and echo's origins as allowed upstreams,
  `Session:Store` `memory` (`GatewayHarness.cs:203-225`). Each registry entry is the fixture
  definition with the issuer name, client id, secret reference and upstream addresses rewritten
  (`GatewayHarness.cs:648-675`). Apps 1 and 2 by default; app 3 is left for onboarding
  (`GatewayHarnessOptions.cs:14`).

**What it stands in for.** The proof cell without TLS and without a browser: a test reaches it with
`TestBrowser`, an `HttpClient` over `LoadBalancerTransport`, which is the ALB's forwarding in
process (`tests/Gateway.TestHarness/LoadBalancerTransport.cs:5-27`).

**How it is launched.** Only by code that references the project: the hermetic suites under
`dotnet test` (`tests/Gateway.IntegrationTests`, `tests/Gateway.SecurityTests`, `tests/e2e`), and
the sitting browser's `start` through `LocalStack`. The project has no entry point of its own. Its
gateway listener speaks plain HTTP on a system-picked port and expects the load balancer's
forwarded headers (`LoadBalancerTransport.cs:9-17`), so a browser pointed straight at it is not
the intended path, and that was not tried: the TLS stand-in in section 1 is what the suite puts in
front of it for a browser.

**What a launch by hand would take.** A program of a few lines that calls
`GatewayHarness.StartAsync` and waits; the sitting browser's `start` is already that program with a
browser attached.

## 4. The Local (L) tier

Not executed in this session; read from `tools/bench/startup.py` and checked against the earlier
finding `docs/research/local-tier-stack-for-an-external-issuer.md` on the branch
`research/local-tier-stack-for-an-external-issuer` (2026-11-04), whose section 1 holds the full
inventory.

- **What runs it.** `GLOSSARY.md:537-541` defines L as a fresh container of the built gateway image
  under the task's limits against stand-ins. The one runner is `tools/bench/startup.py`, on a
  workstation by hand and nightly in `.github/workflows/startup-bench.yml`. `tools/check.sh --full`
  does not run it.
- **What it starts.** The gateway image built from `src/Gateway/Dockerfile`, under
  `--cpus 0.25 --memory 1g`; moto in server mode for S3 and Parameter Store, seeded from
  `contracts/` and `fixtures/` (`startup.py:41-45, 566`); a container that serves a static
  discovery document over TLS on `127.0.0.1:9000` under a CA the run issues
  (`startup.py:47-71`); and a floor container to compare against.
- **What it stands in for.** One cold start of an ECS task, for timing.
- **Whether a browser can reach it.** No. "It binds no host port: everything it reaches is on a
  bench network of its own" (`startup.py:16-17`). The discovery stand-in is "enough for the handler
  to build a challenge ...; no login can complete against it, and none is attempted"
  (`startup.py:439-456`). There is no mock API in it. Each container lives for one measured start.
- **What it needs.** Docker and git, and python3.
- **What a launch by hand takes.** `tools/bench/startup.py`; it is a measurement, not a run.

Three things carry the word "local" and are different things: the **Local (L)** tier is this
benchmark; `LocalStack` and `stack=local` are the hermetic cell of section 1; "the local cell's
hosts" in the comments on the sitting browser's server log (`Program.cs:82-85`,
`SittingBrowserServer.cs:26`) is again section 1's cell. The map's rule against the phrase "local
development" stands for the same reason.

## 5. What starts the gateway alone

- **`tools/smoke.sh`** (already established on the map; read only). Publishes the gateway and runs
  it with `ASPNETCORE_HTTP_PORTS` set and nothing else (`tools/smoke.sh:26`): no registry, so
  `/readyz` is 503 and every app path is 404. A fixed port (default 5080; 5180 in the documented
  call), no Docker.
- **`tools/image-check.sh`** (read only). Builds the gateway image from a copy of the tree and
  starts it with no configuration and no published port, once plainly and once on a read-only root,
  then builds and starts the mock API image with three placeholder settings
  (`tools/image-check.sh:133, 243, 267-269`). It
  proves the images start; nothing is asked of them. Needs Docker. `tools/check.sh --full` runs it
  after the smoke test.
- **`tests/e2e` with no arguments** is not a run either: anything but `sitting-browser` goes to
  xUnit's console runner or the testing platform (`Program.cs:30-35`), so it is the test suite.
- **`spikes/g0/`** (read only) holds console programs with their own gateway-like host, provider
  and upstream (`spikes/g0/README.md`). They are feasibility harnesses outside the solution that
  print check lines and exit; none runs the gateway that ships.

## 6. The pieces by themselves

- **`fixtures/MockApi`** is a web program (`fixtures/MockApi/Program.cs`). It needs
  `MockApi:Issuer`, `MockApi:App1Client` and `MockApi:App2Client` and refuses to start without
  them; it binds 5080 (`orders`) and 5081 (`reports`) on loopback itself
  (`fixtures/MockApi/MockApiOptions.cs:35, 42`). **Executed:** with no settings,
  `dotnet run --project fixtures/MockApi` ended on `MockApi:Issuer is not set`; with
  `MockApi__Issuer=https://issuer.invalid/` and two placeholder client ids it listened on both
  ports and answered `GET /whoami` with 401 on each (no token; the issuer was never reached). It
  also ships as an image (`fixtures/MockApi/Dockerfile`), which `deploy.sh` builds for the proof
  cell.
- **`fixtures/TestIdp`** is a class library (`Microsoft.NET.Sdk`, no entry point;
  `fixtures/TestIdp/TestIdp.csproj`). `TestIdentityProvider.StartAsync` listens on a loopback port
  the system picks (`TestIdentityProvider.cs:248-263`), over plain HTTP, and signs every
  authorization in as `login_hint` or its default subject with no page shown
  (`TestIdentityProvider.cs:315-323`). It runs only inside a process that constructs it: the
  harness, `fixtures/Fixtures.Tests`, and so the sitting browser's local stack.
- **`fixtures/apps`** holds the two app definitions (hosts `appN.spa-poc.test:8443`, upstreams
  `http://127.0.0.1:5080` and `:5081`, issuer name `cognito-poc`); **`fixtures/spa`** holds one
  static page and one script per app with no build step. The harness reads both in place; so do
  the benchmark's seed and the proof cell's `seed.sh`.

## 7. What the shipped gateway would need to be run by hand with an app

Read only. `src/Platform.Registry/PlatformRegistryExtensions.cs:35-45` registers the S3 client, the
Parameter Store client, `ParameterStoreClientSecretSource` and `S3ObjectStore` with `TryAdd`, and
no configuration key selects another implementation; the in-memory ones live in
`src/Platform.Testing` and reach the gateway only through the harness's `ConfigureTestServices`.
`Session:Store` is the one store with an in-memory setting (`src/Platform.Identity/SessionStores.cs`).
So the published gateway, started as `tools/smoke.sh` starts it, can serve an app only if it is
given something that answers as S3 and Parameter Store, a registry object and releases in it, a
client secret, an issuer whose discovery it can read over https, and the platform configuration as
environment variables. The repository's one recipe for all of that is the benchmark's
`gateway_environment` and `seed` (`tools/bench/startup.py:405-434, 566`), which uses moto in Docker
and stops short of a provider that can complete a login. The earlier finding
`docs/research/local-aws-stand-in.md` (branch `research/local-aws-stand-in`) has the SDK's endpoint
rules. No compose stack is on origin/main; the branch
`research/local-tier-stack-for-an-external-issuer` describes what one would need, for another map.

## 8. Conclusion, and what the ticket "The development run" inherits

An interactive run of the gateway with the test identity provider, the mock API and the fixture
apps already exists: the sitting browser's `start` on the local stack. It is interactive for an
agent, through twelve commands and observation lines. It was built as the proof cell's instrument
(the glossary entry, the wiz skill, the bundle directory and the secrecy rules are all a
sitting's), and its local stack exists so the instrument's own tests run without a cell.

For a person it is short of a run by these, each a fact from sections 1 and 2:

- nothing prints the TLS listener's address, and the port changes every start;
- the browser is headless by a literal, and the Playwright switches that would show it are refused;
- the app authority is fixed at port 8443 by `fixtures/apps`, so an outside browser needs a
  resolver rule or a forward, and a certificate exception for a certificate that changes every
  start;
- the launch needs a bundle directory and a short path, which mean nothing outside a sitting;
- the gateway inside it is the in-process composition with two services replaced, not the published
  binary or the image; a run of the image with an app has no existing form (section 7);
- everything is in one process and in memory: a stop loses the sessions, the registry and the
  releases, and a change to gateway code needs a restart.

What it already gives a contributor, unchanged: a way to see a login, a session, an API call
through the proxy, a second user and a second app, against the composition that ships, within
seconds of the start on a built tree, with no Docker and no AWS.

## 9. What was executed and what was read

**Executed on 2026-11-17**, on this workstation, from a worktree at `df4cf96`, against
`fixtures/TestIdp` only:

| What | Result |
| --- | --- |
| `dotnet build tests/e2e` | 0 errors |
| `sitting-browser --bundle <dir> start` with `SPA_E2E_STACK=local`, in the background | `ready ... stack=local`; later `stopped ... reason=stop`, exit 0 |
| `status`, `open`, `login` (alice on app 1, bob on app 2), `marker`, `session`, `api`, `stop` | the lines in section 1, each exit 0 |
| `lsof` on the resident process | six loopback listeners, all on system-picked ports |
| A raw TLS login through the stand-in's port with `Host: app1.spa-poc.test:8443`; two wrong authorities | the hops in section 2: 200 with the fixture page; 404 and 404 |
| The stand-in's certificate, read from the listener | `CN=*.spa-poc.test`, self-signed, one day |
| `dotnet run --project fixtures/MockApi`, with no settings and then with three | refused to start; then listening on 5080 and 5081, `GET /whoami` 401 on each |
| After each: the processes, the listeners and the socket | gone; the worktree holds nothing but this file |

**Read on 2026-11-17, not executed:** `tools/bench/startup.py` and the Local tier (no `docker`
command was run), `tools/image-check.sh`, `tools/smoke.sh`, `tools/check.sh`,
`.github/workflows/startup-bench.yml`, `spikes/g0/README.md`, `src/Gateway/Program.cs`,
`src/Platform.Registry/PlatformRegistryExtensions.cs`, `src/Platform.Identity/SessionStores.cs`,
`tests/Gateway.TestHarness/` (`GatewayHarness`, `GatewayHost`, `GatewayFactory`,
`GatewayHarnessOptions`, `LoadBalancerTransport`), `tests/e2e/Program.cs`,
`tests/e2e/SittingBrowser/` (`SittingBrowserServer`, `SittingCommand`, `Observations`),
`tests/e2e/Stack/` (`LocalStack`, `StackSwitch`, `IBrowserStack`, `TlsLoadBalancer`, `Chromium`),
`fixtures/TestIdp`, `fixtures/MockApi`, `fixtures/apps/app1.json`, `fixtures/spa/app1/index.html`,
`GLOSSARY.md`, `README.md`, `tools/wiz_prelude.sh`, `.claude/skills/wiz/d.md`, and the two earlier
findings named in sections 4 and 7 on their branches. The two ways an ordinary browser could reach
the cell (section 2, last paragraph) are reasoned from those facts and were not tried. The deployed
stack of the sitting browser was not started and no AWS call was made.
