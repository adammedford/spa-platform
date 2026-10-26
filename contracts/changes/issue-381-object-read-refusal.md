# Change request issue-381: a refused object read, the shell rule, and the refusal on the request line

- **Raised by:** workstream 2 (Hosting and registry), for the defect #381
- **Tasks:** none in the plan. #381, which blocks #63's G3 row (F-06). Work falls to workstream 2
  (`S3ObjectStore`, `Assets.cs`, `tools/spactl`, `FakeS3Server` and their tests), workstream 6
  (`UsePlatformTelemetry`, composed F-06 and readiness, the deployed sitting) and workstream 1
  (wording in `infra/modules/cell`)
- **Date:** 2026-10-24
- **Status:** decided, 2026-10-26, on
  https://github.com/adammedford/spa-platform/issues/381#issuecomment-6423650225. See the Decision
  section below. The tickets are #425, #427, #428, #429 and #426, in the build order

The full proposal, with the options weighed and the tests listed per project, is on #381:
https://github.com/adammedford/spa-platform/issues/381#issuecomment-6392882099, revision 4,
which takes in the audit of revision 3. This request names only the integrator's part.

## The gap

The task role grants `s3:GetObject` and no `s3:ListBucket` (`infra/modules/cell/iam.tf`,
`ReadRegistryAndReleases`; P-03). Without `ListBucket`, S3 answers GetObject on a missing key with
403, not 404 `NoSuchKey` (https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObject.html,
"Permissions"). `S3ObjectStore` reads only `NoSuchKey` as a missing key, on purpose, so that a
refused registry read is not taken for "no registry". The asset route reads through the same
store, and on the deployed cell `/assets/missing.0badf00d.js` answered 500 from an unhandled
`AmazonS3Exception` (#63, comment 6390244202). `contracts/http-contract.md` says 404 `not_found`.

The fix cannot be workstream 2's alone:

- `src/Platform.Hosting` does not reference the AWS SDK, and `IObjectStore` has no word for "the
  store refused this read". Null is its only answer short of an exception.
- `ObjectStoreContract.A_key_that_was_never_written_reads_as_null` requires every store to answer a
  missing key with null. A GetObject-only S3 store cannot, so the shared contract states the
  mismatch as a rule, and `InMemoryObjectStore`, which `GatewayHarness` uses, cannot model the
  deployed answer.
- `http-contract.md` has no row for a release whose `index.html` cannot be read, and
  `Problems.Rows` cannot write a code that is not in it. The asset route's 404 carries no
  `Cache-Control`; RFC 9110 section 15.5.5 lets a cache keep a 404 heuristically, so a 404 caused
  by a passing permission fault could outlive the fault.
- Phase 1 exports only the log stream (`src/Platform.Telemetry/PlatformTelemetryExtensions.cs`;
  `contracts/telemetry.md`). Today the refusal escapes as an unhandled exception and the 500 is its
  signal (#63, comment 6390244202); once items 3 and 4 answer it as a handled 404, nothing on the
  log stream distinguishes a refused read from a confirmed-missing key, so an operator cannot see a
  permission fault. The request line's fields are a closed set (`contracts/telemetry.md` -> Log
  fields). (Revision 4 said the asset route's Debug lines are off at Information; `Assets.cs` has
  no Debug logging, as #382's review noted. Corrected here on the decision.)

## What is asked

1. **`ObjectReadRefusedException` in `src/Platform.Abstractions`,** with a `Key` property,
   documented on `IObjectStore.GetAsync`: null means the store confirmed the key does not exist;
   the exception means the store refused the read, which is ambiguous (a missing key under a role
   without listing, or an existing key the role may not read), and the caller decides what it means
   for its key. `S3ObjectStore` throws it for status 403 with error code `AccessDenied` alone, with
   the SDK exception inner; every other 403 code, `NoSuchBucket`, 5xx, transport failures and
   cancellation propagate unchanged. `PublicSurfaceTests`' allowlist gains the type, and
   `Platform.Abstractions.Tests` covers it.

   Its callers, each workstream 2's, and what each does with it:

   | Caller | On a refusal |
   | --- | --- |
   | `RegistrySource.RefreshOnceAsync` | `RefreshOutcome.Failed`, as for any exception today; never `Missing` |
   | `Assets.cs` | Item 3 for the shell, item 4 for any other key |
   | `tools/spactl` (`Commands.cs`: the registry read, `ExistsAsync` for the shell, `SameBytesAsync` for a resumed publish) | Propagates to `SpactlApp.RunAsync`, which catches it beside its two AWS handlers: exit 1, a diagnostic naming the key and saying the read was refused, which means missing or not readable. Never read as "missing" or "different bytes", and no write follows |

   `SpactlApp.RunAsync` catches `AmazonServiceException` and `AmazonClientException` today; the
   neutral exception matches neither, so without its own handler it escapes with no diagnostic and
   no exit code (the audit's probe on #381).

2. **`ObjectStoreContract` in two modes.** Each store declares how it answers a missing key,
   confirmed absence or ambiguous refusal, and each run asserts exactly that answer, never either.
   Both modes keep the successful read with its ETag, the `NotModified` answer to a matching
   `If-None-Match`, and the conditional-write cases. The unconditional
   `A_key_that_was_never_written_reads_as_null` goes. `InMemoryObjectStore` gains the refusal mode,
   and the contract runs over it in both.

3. **The shell.** A new Errors row in `http-contract.md`,
   `500 | shell_unavailable | The app's release has no readable index.html`, with
   `Problems.ShellUnavailable` and its `Rows` entry, so the shared writer can answer it and the
   drift test holds the two together. The shell is the resolved key whose release-relative path is
   exactly `index.html`, compared ordinally: `/`, `/index.html` and every extensionless fallback,
   including one under `/assets/`, and not `/Index.html` or `/assets/index.html`. It answers 500
   whether the store returned null or refused. Today a confirmed-missing shell answers 404
   `not_found`; this changes it, so that the same absent shell does not answer 404 or 500 depending
   on the role's permissions. The `not_found` row's "missing asset" then means any key but the
   shell.

4. **Caching.** Every 404 the asset endpoint answers carries `Cache-Control: private, no-store`:
   a key the store answered null or refused, and the reserved-path 404 that returns before any read.
   The `shell_unavailable` 500 carries it too. Under the deployed role every missing asset is a
   refusal, so one rule covers both.

5. **`IRequestTelemetryFeature.ReadRefused`,** a `string?` the asset route writes
   null-conditionally when the store refuses: `asset` or `shell`, never a key or a path. Every
   other case leaves it null, the reserved-path 404 included. `RequestTelemetryFeatureTests`'
   two-field assertion becomes three, and `SettableRequestTelemetryFeature` gains the property.

6. **`contracts/telemetry.md`.** `read_refused` joins the Log fields row, logged by
   `UsePlatformTelemetry` on the request line it already writes at Information.
   `storage.read_refusals` joins the Metrics row, on the `Platform.Telemetry` meter, tagged
   `app_id` and `key_class`: it counts the requests marked with a refusal, of both classes.
   "The instruments" records it as a second exception to "the instrument belongs to the project
   that raises the event", for the reason `security.rejections` is one. What one owner guarantees
   is one emission decision per request: the count is added from the same field the line logs, as
   `security.rejections` is today, before the line's `IsEnabled(Information)` check. It is not a
   promise that two exported streams agree in production.

   The evidence is tiered to what Phase 1 exports. Hermetic: the count and the line's field agree,
   over an in-memory metric exporter with Information logging on. Deployed: `read_refused` appears
   on the log stream in the fresh sitting. No deployed metric export is required, because Phase 1
   deliberately exports none.

Not asked: a release manifest (none is in `s3-layout.md` or `spactl` today, and its design and
migration are their own question), and `s3:ListBucket` limited by `s3:prefix` (it changes P-03 and
the `module.tftest.hcl` assertions, and nothing shows that S3 consults a prefix-limited grant when
it chooses 404 or 403 on GetObject). The GetObject-only grant stays.

## What it leaves unchanged

- **The registry fails loud already.** `RegistrySource.RefreshOnceAsync` reads any exception from
  `GetAsync` as `RefreshOutcome.Failed`, keeps requested cancellation, and reads null as `Missing`
  (`src/Platform.Registry/RegistrySource.cs`). Workstream 2's tests pin `Failed` and the snapshot
  before the first one and after a working one; the `/readyz` answer is asserted in workstream 6's
  composed tier, since the registry's tests cannot reach Hosting (A-03).
- **No S3 request is added** per asset, and `IObjectStore`'s methods keep their signatures, so
  the store wrappers in the test projects compile unchanged.
- **P-03 and the grant.** Workstream 1 corrects only the "keys the registry names" wording in the
  `iam.tf` comment and one tftest message.
- **No new package and no new project.**

## Order once decided

Adding `ReadRefused` to the interface breaks the build until every implementation has it:
`RequestTelemetry.RequestTelemetryFeature` in `Platform.Telemetry` (workstream 6) and
`SettableRequestTelemetryFeature` in `Platform.Testing` (the integrator) are the only two. Adding
the member to the interface alone fails with CS0535 on the first (the audit's probe on #381).

1. Workstream 6 lands an inert `public string? ReadRefused { get; set; }` on its private class. It
   implements nothing yet, so it builds alone.
2. The integrator lands items 1 to 6: the exception, the interface member and the Testing
   stand-in, `Problems.ShellUnavailable`, the two-mode contract, the Abstractions tests and the
   contract text, in one landing that builds.
3. Workstreams 2 and 6 then land their behaviour in either order. Workstream 1's wording fix can
   land at any time.

#381 closes when F-06 passes in a fresh deployed sitting of the fixed commit (#63); hermetic
passes do not replace it.

## Decision

Integrator, 2026-10-26, on https://github.com/adammedford/spa-platform/issues/381#issuecomment-6423650225. **Accepted as written**: items 1 to 6 under "What is asked",
the "Not asked" list, "What it leaves unchanged" and the build order under "Order once decided", on
revision 4 of the proposal (https://github.com/adammedford/spa-platform/issues/381#issuecomment-6392882099).
The integrator role is played by agent sessions (`GLOSSARY.md` → Integrator); the human's act is the
decision comment this section cites (ADR-0021 decision 5).

What is decided, each for the reason given beside it under "What is asked":

- **The refusal** (item 1): `ObjectReadRefusedException` in `Platform.Abstractions`, with `Key` and
  the store's exception inner, thrown by `S3ObjectStore` for 403 `AccessDenied` alone; null keeps
  meaning confirmed absence; the interface's signatures do not change.
- **The contract in two modes** (item 2): each store declares its answer to a never-written key and
  the run asserts exactly that answer; `InMemoryObjectStore` gains the refusal mode.
- **The shell** (item 3): the `shell_unavailable` row and `Problems.ShellUnavailable`; an unreadable
  `index.html`, null or refused, answers 500 whatever the role's permissions.
- **Caching** (item 4): every 404 the asset endpoint answers, and the `shell_unavailable` 500, carry
  `Cache-Control: private, no-store`.
- **The signal** (items 5 and 6): `IRequestTelemetryFeature.ReadRefused`, the `read_refused` field on
  the request line and the `storage.read_refusals` counter on the `Platform.Telemetry` meter, with
  the evidence tiered to what Phase 1 exports.
- **The order**: workstream 6's inert property first, then the integrator's items in one landing,
  then workstreams 2 and 6 in either order; workstream 1's wording fix at any time.

### Landing it between gates

`contracts/README.md` → "What may land between gates": a new type that changes no existing stage,
owner or entry point's signature lands on the integrator's decision. `ObjectReadRefusedException` is
such a type; `ReadRefused` is a new member on `IRequestTelemetryFeature`, whose two implementations
are named above and gain it in the stated order, so nothing built against the frozen contract has
to change to keep compiling; the `shell_unavailable` row is a new Errors row, as change request
4.2's `503 idp_unavailable` was; `read_refused` and `storage.read_refusals` are new entries in
`contracts/telemetry.md`'s two closed lists, which the same precedent carries.

The tickets are #381's sub-issues, one per owner in the build order above: #425 (workstream 6,
the inert property), #427 (the integrator, blocked by #425), #428 (workstream 2, blocked by #427),
#429 (workstream 6, blocked by #427 and #428) and #426 (workstream 1, at any time).
