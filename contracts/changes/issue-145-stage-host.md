# Change request issue-145: `StageHost`, `CapturedLog` and `RawHttp` in `Platform.Testing`, and the widened charter

- **Raised by:** the integrator, for the `stage-host` spec #140, as its first ticket #143
- **Tasks:** none in the plan. The tickets of #140: #145 (the expand, which this request is named
  for), #146 (workstream 6's adoption, a commit on #145's pull request), #147 to #152 (one migrate
  ticket per test project) and #144 (the test identity provider's `AddClient` overload)
- **Date:** 2026-10-20
- **Status:** decided, 2026-10-20. See the Decision section below.

## What is missing

A workstream proves its stage in its own test project, against the doubles, and workstream 6 proves
it again in the composed suites. To do that, every workstream first builds the same cut-down
gateway: Kestrel on a loopback port and the address it picked; a stand-in for `UsePlatformTelemetry`
that puts a `SettableRequestTelemetryFeature` on each request; a logger provider that keeps the
lines for a never-logged search (N-16); and a raw-socket sender for the requests no client library
sends, because the repository reaches the gateway over a raw socket wherever the `Host` header
matters (`AGENTS.md` -> Build and test). Seven test projects carry a copy, and the copies have
drifted apart. None of the copied code has a test of its own, and the drift is where the defects
sit:

- **A reversed stand-in.** `tests/Platform.Security.Tests/AntiforgeryHost.cs` calls
  `UsePlatformForwardedHeaders` and installs the telemetry stand-in after it, the reverse of
  `contracts/pipeline.md` and ADR-0018 decision 1, so the method allowlist's 405 would find no
  feature to write `gate.method` on. No test sends that host a method the allowlist refuses, so the
  reversal shows nowhere.
- **Never-logged searches at Information.** The framework's default floor is Information
  (`AddLogging` registers `DefaultLoggerLevelConfigureOptions(LogLevel.Information)`, dotnet/runtime
  `src/libraries/Microsoft.Extensions.Logging/src/LoggingServiceCollectionExtensions.cs`). Of the
  hosts that capture a log, only `AntiforgeryHost` raises it (`SetMinimumLevel(LogLevel.Trace)`), so
  the others' N-16 searches cannot see a Debug or Trace line, which is where the OpenID Connect
  handler writes the callback URL with its code and state, unless a test raises the floor through
  configuration, as only the telemetry project's canary tests do.
- **Exception-blind searches.** Two of the four log doubles drop an exception's text
  (`tests/Platform.Security.Tests/ListLoggerProvider.cs`,
  `tests/Platform.Hosting.Tests/RecordingLoggerProvider.cs`), so a credential in an exception
  message is invisible to the search.
- **Four raw senders and three parsers.** `HostingHost`'s parser collapses repeated headers into a
  dictionary and so hides the second `Set-Cookie` a security test exists to see;
  `tests/Gateway.TestHarness/RawHttp.cs` keeps the wire bytes and refuses to de-chunk, so
  `tests/Gateway.SecurityTests/Negative/RawBody.cs` exists beside it only to de-chunk.

Two copies say in their own comments that the shared helper belongs in `Platform.Testing`, an
integrator path their owners cannot write to (`tests/Platform.Security.Tests/SecurityHost.cs:27`,
`tests/Platform.Proxy.Tests/Loopback.cs:12-13`), and a third says it is copied only because the
project it copies from is another workstream's
(`tests/Platform.Registry.Tests/ListLoggerProvider.cs:7-8`). No workstream can fix the shared part
for everyone, because every other copy is another owner's.

## The change

`src/Platform.Testing` gains three public modules, built and proven by #145. The interface below is
#140's Implementation Decisions, as its grilling record (Q1 to Q19, on #140) and the decisions shared
with the other three specs (S1, S2, S4 and S8, on #140) settled it. Each part carries its reason.

### 1. The interface: two delegates and an option

`StageHost.StartAsync(StageHostOptions, CancellationToken)` returns a started `StageHost`.
`StageHostOptions` carries `Services`, an optional `Action<WebApplicationBuilder>`; `Pipeline`, a
required `Action<WebApplication>`; and `TelemetryStandIn`, false by default. A started host gives
back `BaseAddress` (the loopback URI with its port), `Services` (the application's
`IServiceProvider`), `Log` (the `CapturedLog`), `Client` (the `HttpClient`), `RawAsync` in two
forms, `TelemetryRules`, `LastTelemetry`, and `DisposeAsync`, which stops the listener. A start that
yields no address throws `InvalidOperationException`; a server that closes without answering a raw
request throws `InvalidDataException`; a connect after dispose fails. It needs no configuration:
trusted networks, issuers and the like are the test host's, set in its `Services` delegate.

The test host calls its own `Add*` entry points inside `Services` and its `Use*` and `Map*` entry
points inside `Pipeline`, and the module calls none.

*Why.* The entry points are frozen in `contracts/pipeline.md`, and composing them in the contract's
order is the test host's job, so nothing stage-specific may cross the seam. Two delegates are what
the hosts already need: they call `Map*` on the built `WebApplication` itself
(`tests/Platform.Hosting.Tests/HostingHost.cs`, `tests/Platform.Proxy.Tests/ProxyHost.cs`), and an
options record is the proxy tests' own precedent (`tests/Platform.Proxy.Tests/ProxyHostOptions.cs`).
There is no `IRawSender`, no builder-kind option and no host-resolution pin option: each would be a
seam with one adapter. The pin stays in each host's `Pipeline` delegate, because five pins hold five
rules and a reader of the host must see the rule its stage is composed on.

### 2. The construction order, which callers rely on

1. The slim builder, in the `Production` environment.
2. A loopback URL on port zero.
3. Every logger provider cleared.
4. The minimum level set to Trace.
5. The `Services` delegate.
6. The capture provider registered.
7. `Build()`.
8. The telemetry stand-in, if asked.
9. The `Pipeline` delegate.
10. Start, and the address read.

The order is part of the interface, written into it, so a caller learns that its `Services`
delegate runs before the capture is registered and its `Pipeline` delegate after the stand-in
without reading the implementation.

*Why.* `AddPlatformTelemetry` clears the providers
(`src/Platform.Telemetry/PlatformTelemetryExtensions.cs:31`), so a capture registered before a
host's `Services` delegate is removed by it; `TelemetryHost` orders this by hand today. Registering
the capture after the delegate makes that survival structural. The slim builder binds `Logging`
configuration and adds a console logger (dotnet/aspnetcore
`src/DefaultBuilder/src/WebApplicationBuilder.cs`, `AddDefaultServicesSlim`), so the telemetry
project's everything-at-Trace configuration works on it, and `Production` is set the way the test
identity provider and the gateway test harness already set it.

### 3. The telemetry stand-in: an option, off by default, outermost

`TelemetryStandIn` is false by default. On, the module installs one middleware before the
`Pipeline` delegate runs. It creates a `SettableRequestTelemetryFeature`, records it, sets it on the
request as `IRequestTelemetryFeature`, and then calls the next middleware. `TelemetryRules` is the
rule id of each request in the order the requests entered, null where none was left; `LastTelemetry`
is the last request's feature; both are read from one list, and both are empty or null unless the
option is on.

*Why off by default.* A stage writes the feature null-conditionally, because its own test project
runs without `UsePlatformTelemetry` (change request 6.4, Decision 1). Six tests rest on the hosts'
default being off; a host that installed the stand-in unasked would turn each of those proofs into
a test of nothing. Shared decision S2 keeps it so after #142's `Rejection` module lands.

*Why before the delegate.* `UsePlatformTelemetry` is the outermost stage (`contracts/pipeline.md`,
ADR-0018 decision 1). Installing the stand-in before the test host's own pipeline fixes
`AntiforgeryHost`'s reversed order structurally, where a comment could not, and the module's own
test puts the position under proof where no host could.

*Why record before the request runs.* Each stage sets its rule id before it writes the response, so
a test reading the recorded feature cannot race the body back to the client. `GateHost` and
`ProxyHost` already state this invariant in their comments; the module makes it the one behaviour.

*Why that name.* An option named `telemetry` means the stand-in in `GateHost` and the real
`UsePlatformTelemetry` in `TelemetryHost`. The name must say which.

### 4. The captured log and the Trace floor

`CapturedLog` is its own public module, usable with or without a stage host. A `CapturedLine`
carries `Category`, `Level`, `Message`, `Exception` and `State`, with `Text` joining the message and
the exception. `Lines` is a snapshot. `LinesFor(rule, level?)` reads each line's `Rule` state
property; `Contains(value)` searches `Text`. A test with no host attaches it through `Provider` (an
`ILoggerProvider` that records only), `ProviderFor(services)` (records, and renders through the
console formatter those services configure; what the stage host registers after `Services`), or
`CreateLogger<T>()`. `Rendered` and `Json` carry what stdout would, present only when a console
formatter is configured, resolved on first use by `ConsoleLoggerOptions.FormatterName`.

The module sets the minimum level to **Trace** and leaves configuration rules in force.

*Why the Trace floor.* A never-logged search that cannot see a Debug or Trace line is a pass that
checked nothing. Trace is the floor at which a capture sees everything a logger could write. The
risk is a single-line assertion meeting a second, lower-level line, which is why `LinesFor` takes an
optional level and why each migrate ticket runs its own suite.

*Why exception text in `Text`.* A credential in an exception message, or an inner exception, is
something N-16 must be able to find.

*Why `LinesFor` reads the property.* Every rule-logging site names its property `Rule`, but not with
one wording: hosting says "refused under {Rule}", the rest "rule={Rule}", so a `rule=` text search
already misses hosting's line. Reading the property is a guard that reads the thing it guards
(`CODING_STANDARDS.md`), and shared decision S1, which leaves the Warning line each stage's own,
means a host's query must survive any template.

*Why a snapshot.* The hosts' callers hold `lock` blocks over a live list today; a snapshot retires
them and gives a test a list that cannot change under it.

*Why by name, never by type.* The platform's JSON formatter is `internal` to `Platform.Telemetry`
(`src/Platform.Telemetry/PlatformJsonFormatter.cs:26`), visible to that project's tests alone, so the
lookup `Platform.Testing` can make is the configured name, as `tests/Gateway.TestHarness/GatewayHarness.cs`
does. `TelemetryHost` names the type, which only its own project can.

*Why the record's names.* `Category` and `Level` keep every category and level pin in the hosting
and identity tests compiling as they are, so the migrate tickets wait on no other spec's refactor
(shared decision S4).

### 5. The one `RawResponse`

`RawHttp` sends a request as written over a socket and returns one `RawResponse`: `StatusCode`,
`StatusLine`, `Headers` in order with repeats kept, `Body` as the bytes on the wire, `BodyText`
with chunked framing taken off, `Values(name)` and `Header(name)`. A reset after the answer is
tolerated. `RawHttp` keeps the gateway test harness's endpoint-taking forms, `SendAsync(endpoint,
request)`, `SendAsync(endpoint, bytes)` and `SendAsync(endpoint, method, target, host, headers)`, the
last building the head and adding `Connection: close`. `StageHost.RawAsync(request)` and
`RawAsync(method, target, host, headers)` are that sender bound to `BaseAddress`.

*Why as written.* A path with a dot segment, a backslash or a NUL must reach the stage as the test
wrote it, not as `Uri` would normalise it, and a spoofed `Host` must reach the process at all.

*Why ordered repeats.* The composed suites assert repeated headers, and a collapsing dictionary
hides the second value a security test exists to see.

*Why `Body` raw and `BodyText` de-chunked.* The hosting tests and the composed suites both parse
bodies Kestrel chunks, and a test about the wire still needs the framing. One response with both
retires `RawBody.cs`, which exists only because the harness's sender refused to de-chunk.

*Why the endpoint forms stay.* A composed negative test sends to the gateway test harness's address
with no stage host, as it does today.

The reset-after-answer tolerance is kept and deliberately untested: no deterministic way to make
Kestrel reset after answering was found. #145's merge body says so.

### 6. The client: no cookies, no redirects

`Client` is one `HttpClient` with the base address set, no cookie container
(`UseCookies = false`) and no automatic redirect (`AllowAutoRedirect = false`). Each test host
keeps its own `SendAsync`, carrying its stage's headers.

*Why.* A test writes the `Cookie` header itself, case variants and all, and a challenge is an answer
it reads rather than a redirect it follows. Three security hosts build this same client for this
same reason today; the headers differ per stage, so they stay in the host that proves it.

### 7. The private loopback seam

`EchoUpstreamListener` is rebuilt on the stage host's loopback start, which has those two callers
inside `Platform.Testing` and no public name. Its existing tests pass unchanged.

## The charter, and the two edits it takes

`Platform.Testing`'s charter is "a test double for every interface"
(`src/Platform.Testing/Platform.Testing.csproj:3`) and its row in `contracts/README.md` (line 34) is
"Test doubles". A stage host is no double, so this request widens the charter to **"the doubles, and
the test host and captures a workstream composes them in"**, in two edits, which #145 makes:

1. **The test doubles row of `contracts/README.md`**, a steering document. Proposed wording, for
   #145 to apply:

   ```text
   | Test doubles, and the stage host | `src/Platform.Testing` (task I-3; #145) | A double with the interface it stands in for; the stage host, the captured log and the raw sender when #145 lands | Everyone |
   ```

   The freeze column has to change with the name. Today it reads "With the interface each one stands
   in for", which a stage host cannot satisfy, because it stands in for no interface. The proposal
   freezes the three modules when they land, so a change to the shared part after that is a change
   request like any other part of `Platform.Testing`. That is the locality the spec buys: only a
   change to the shared part needs the integrator.

2. **The project file's description**, the comment at `src/Platform.Testing/Platform.Testing.csproj:3`,
   becomes "The doubles for every interface in Platform.Abstractions, and the test host and captures
   a workstream composes them in. Owner: integrator."

**Their authority is #140's acceptance**, the spec this request serves: its user story 2 and its
Implementation Decisions ask for both edits, and its Further Notes say a later steering-document
edit, to `GLOSSARY.md` or to the test doubles row, cites #140's acceptance. #145 carries both, with
the six glossary entries #140's Further Notes give verbatim (test host, stage host, telemetry
stand-in, captured log, terminal, pin), each commit citing that authority. This request edits none
of them.

## ADR-0015, and why it is not reversed

ADR-0015's Alternatives rejected kept the rule ids out of `Platform.Testing`
(`docs/adr/0015-shared-constants-across-a-workstream-boundary.md:76`):

> Put the ids in `Platform.Testing` instead, which every workstream already references |
> `Platform.Testing` is doubles for tests. A production refusal would then name a constant that
> lives in a test-support project, and a workstream would reference test code from `src/`.

That row read the charter as a reason. **A test-only module reverses nothing in it.** The hazard the
row names is a `src/` project referencing test code; no `src/` project references `Platform.Testing`
(none of the `src/*/*.csproj` files at `96b04dc` names it), and none will, because the three modules
are used from `tests/` only and nothing in `src/` needs a stage host. The constant the row refused
stays in `Platform.Abstractions`, where ADR-0015 put it; widening the charter for test-only modules
moves no production name into a test-support project. The row stands, and #140's "no ADR" (Q19)
stands only on this reading, which is why it is stated here (shared decision S8).

One thing the reading rests on is not held by a check: A-03
(`tests/Gateway.ArchitectureTests/ReferenceRules.cs:32-34`) exempts `Platform.Testing` by name and
binds the six workstream projects only, so nothing fails today if a `src/` project references
`Platform.Testing`. That was so before this request and this request does not change it; an
architecture rule for it would be workstream 6's to propose, and none is asked for here.

## Why this project, and not a new one

A-03's reason, that a project every workstream reaches must be nobody's workstream, is why the shared
part belongs in an integrator project. `Platform.Testing` is that project already: it carries xUnit
(`Platform.Testing.csproj`, for the store contract suites), already starts Kestrel for
`EchoUpstreamListener`, and already has a socket test (`tests/Platform.Testing.Tests/EchoUpstreamTests.cs`).
Two copies ask for it by name in their own comments. A new project would be the same decision with a
second project file, a new solution entry and a new reference in every test project. No new package
is needed either: `ConsoleFormatter` and `ConsoleLoggerOptions` ship in the shared framework every
project already reaches.

## What this request does not cover

- **Candidate 4's rule-id scan** (#139) is a separate change request. The two may land in one
  integrator pull request as a convenience; neither depends on the other.
- **`IdentityGateway`** stays apart from the stage host, because it has no socket by design, and
  takes only `CapturedLog` (#151). **`Loopback`** stays in the proxy tests for the upstream doubles
  (#148). The **contract reader** stays in the gateway test harness.
- **No stage, entry point or production log line changes.** This is test code only.
- **The `AddClient` overload** on the test identity provider is workstream 5's ticket, #144, outside
  this request.

## Decision

Integrator, 2026-10-20. **Accepted as written**, on the answers Adam accepted on 2026-10-19 and
recorded on #140: the grilling record's Q1 to Q19 and the shared decisions S1, S2, S4 and S8. The
integrator role is played by agent sessions (`GLOSSARY.md` -> Integrator); this decision is recorded
under #143's acceptance, and Adam's landing of it is the human's act (ADR-0021 decision 5).

What is decided, each for the reason given beside it under "The change":

- **The interface** (section 1): `StageHost.StartAsync(StageHostOptions, CancellationToken)` with a
  `Services` delegate, a required `Pipeline` delegate and `TelemetryStandIn`; the module calls no
  entry point, because composing the frozen entry points is the test host's job.
- **The construction order** (section 2), the ten steps as listed, part of the interface: the
  capture is registered after `Services`, so a delegate that clears providers cannot remove it.
- **The stand-in default** (section 3): off unless asked, so the stages' null-conditional writes
  stay proven; installed before `Pipeline`, outermost as `UsePlatformTelemetry` is; recording each
  feature before the request runs, so a read cannot race the body.
- **The Trace floor** (section 4), configuration rules left in force, so a never-logged search sees
  every line a logger could write, with `LinesFor(rule, level?)` for the single-line assertions it
  would otherwise break.
- **The raw response semantics** (section 5): one `RawResponse`, the request sent as written,
  headers ordered with repeats kept, `Body` the wire bytes and `BodyText` de-chunked, so neither the
  second `Set-Cookie` nor a chunked body is lost to a test.
- **The client** (section 6): no cookie container and no redirects, so a test writes its own
  `Cookie` header and reads a challenge as an answer.

### Landing it between gates

`contracts/README.md` -> "What may land between gates": a new type that changes no existing stage,
owner or entry point's signature lands on the integrator's decision. `StageHost`,
`StageHostOptions`, `CapturedLog`, `CapturedLine`, `RawHttp` and `RawResponse` are new types in
`Platform.Testing`; `EchoUpstreamListener` keeps its public surface and changes only behind it; no
stage, owner, entry point, schema or rule id moves. So the expand lands now, on this decision, and
waits for no gate. The charter row is a prose edit to `contracts/README.md`, the other case the rule
names.

### No ADR

The decision is recorded here and, through #145, in the glossary. It is reversible (the copies can
come back), unsurprising (the copies' own comments ask for it), and the trade-off is modest. The
strongest case against was weighed on #140: the copying is the ownership model working as designed,
a shared host turns every tweak to the shared part into a change request, and the price is nine
tickets across five owners for test code that passes today. Against that, the three defects found
all sit in the copied part, none of it tested, and each fix lands once; the delegates keep
stage-specific tweaks inside the host; and the module's own tests put the stand-in's position under
proof. A "no" would have deserved an ADR, so the question does not return on the next survey; the
answer is yes, and it does not.

### What is left, and to whom

| Ticket | Owner | Does |
| --- | --- | --- |
| #145 | integrator | The three modules and their thirteen tests over a real socket; `EchoUpstreamListener` on the private seam; the charter row, the project file's description and the six glossary entries, citing #140's acceptance |
| #146 | workstream 6 | Adopts `CapturedLog` and `RawHttp` in the gateway test harness and the composed suites and deletes its own `CapturedLog`, `RawHttp` and `RawBody`, as a commit on #145's pull request: two composed suites import both namespaces and would fail on an ambiguous reference (CS0104) otherwise |
| #147, #148 | workstream 4 | The security tests' four hosts, and `ProxyHost`, onto `StageHost` |
| #149, #150 | workstream 2 | `HostingHost` onto `StageHost`; the registry tests attach `CapturedLog` |
| #151 | workstream 3 | The identity tests attach `CapturedLog` and adopt #144's overload |
| #152 | workstream 6 | `TelemetryHost` onto `StageHost` whole, with the real `UsePlatformTelemetry` |
| #144 | workstream 5 | The `AddClient` overload taking several post-logout URIs |

#145 may open now. Every migrate ticket waits for #145, and each deletes only its own owner's copies.
