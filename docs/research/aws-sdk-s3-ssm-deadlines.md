# What pinned AWSSDK.S3 and AWSSDK.SimpleSystemsManagement do on timeouts, retries, and aborted streams

- **Ticket**: #1581, "What do pinned AWSSDK.S3 and AWSSDK.SimpleSystemsManagement do on timeouts, retries, and aborted streams?", a child of wayfinder map #1580 (gateway runtime hygiene, old-asset continuity, and supportability).
- **Date**: 2026-11-27
- **Branch**: `research/aws-sdk-s3-ssm-deadlines`, based on `origin/main` at `8de2d621`.
- **Method**: inspected repository source and package pins (`Directory.Packages.props`, `Platform.Registry`, `Platform.Hosting`, `Platform.Identity`), inspected the shipped assemblies and XML documentation in `~/.nuget/packages/` for `AWSSDK.S3` `4.0.101.6`, `AWSSDK.SimpleSystemsManagement` `4.0.104.2`, `AWSSDK.Core` `4.0.102.9` (resolved by `Platform.Registry` alone) and `AWSSDK.Core` `4.0.102.12` (resolved by `Gateway`), read AWS SDK documentation, and executed local loopback TCP/HTTP probes on macOS Apple silicon (Darwin 27.0.0 arm64) with .NET 10 SDK `10.0.401` (runtime `10.0.12`) in a throwaway probe app outside the repository. **No live AWS calls were made.** All probe servers were raw loopback `TcpListener` sockets on `127.0.0.1` returning synthetic HTTP/1.1 responses, XML/JSON error payloads, header stalls, mid-body stalls, premature TCP FIN, and TCP RST; DNS failure used `.invalid`.

## Answer in brief

1. **Defaults, timeouts, and retries.** `ReadWriteTimeout` does not exist on `ClientConfig`, `AmazonS3Config`, or `AmazonSimpleSystemsManagementConfig` in AWSSDK v4 (`net8.0`). `AmazonS3Config.Timeout` defaults to `TimeSpan.FromMilliseconds(int.MaxValue)` (`24.20:31:23.647`, ~24.86 days), whereas `AmazonSimpleSystemsManagementConfig.Timeout` defaults to `null`, leaving the underlying `HttpClient.Timeout` at .NET's `100 s` (`00:01:40`). `ConnectTimeout` defaults to `null` on both, leaving `SocketsHttpHandler.ConnectTimeout` at `Timeout.InfiniteTimeSpan` (`-00:00:00.001`). Both clients default to `RetryMode = Standard` and `MaxErrorRetry = 2`, producing **3 total HTTP attempts** on HTTP 500, connection refused (`HttpRequestException`), and `ClientConfig.Timeout` header stalls (`TimeoutException`). With `AWS_NEW_RETRIES_2026=true`, the attempt count stays **3** for both S3 and SSM (unlike DynamoDB, whose default `MaxErrorRetry` is `10`), while retry backoff drops from seconds to ~60–140 ms total.
2. **`S3ObjectStore.GetAsync` vs `ResponseStream` reads.** `s3.GetObjectAsync(request, ct)` resolves as soon as **HTTP response headers** arrive (observed in 1–5 ms while the body stalled). Both `ClientConfig.Timeout` and the `ct` passed to `GetObjectAsync` govern only the header/retry phase; cancelling `ct` after `GetObjectAsync` returns has no effect on `ResponseStream`, and an explicit `AmazonS3Config.Timeout` (`200 ms` or `400 ms` in probes) does **not** abort a mid-body stall (`1500 ms` or `5000 ms`) during `CopyToAsync` or `ReadToEndAsync`. Under default `AmazonS3Config`, `ResponseStream` is `Amazon.Runtime.Internal.Util.MD5Stream`; a premature TCP FIN throws `System.Net.Http.HttpIOException` (`ResponseEnded`), a TCP RST throws `System.IO.IOException` (inner `SocketException`), and an ETag/MD5 mismatch at EOF throws `Amazon.Runtime.AmazonClientException`. In `Assets.InvokeAsync`, `CopyToAsync(response.Body, context.RequestAborted)` runs outside the `try/catch` after response headers start and has no server-side read timeout; in `RegistrySource.RefreshOnceAsync`, a mid-body stall on `ReadToEndAsync(stoppingToken)` holds the `polling` semaphore indefinitely until host shutdown.
3. **`ParameterStoreClientSecretSource.PreloadAsync` batch timing and exceptions.** Because `AmazonSimpleSystemsManagementConfig` has a `100 s` `HttpClient.Timeout`, `MaxErrorRetry = 2` (3 attempts), and retries timeouts, a single stalled parameter call takes ~`3 × 100 s + backoff` (~`303 s`, observed at scaled `250 ms` timeout as `6338 ms` / 24 HTTP requests for 8 parameters across 2 waves of `MaxDegreeOfParallelism = 4`). Under defaults, `N` stalled parameters hold one `PreloadAsync` batch (and `RegistrySource`'s `polling` semaphore) for `ceil(N / 4) × ~303 s`. When `ClientConfig.Timeout` fires while caller `ct` is not cancelled, `ssm.GetParameterAsync` throws `System.TimeoutException` (wrapping `TaskCanceledException`) in both `AWSSDK.Core` `4.0.102.9` and `4.0.102.12`, which lands in `LoadAsync`'s `catch (Exception ex)` and records `"TimeoutException"` for that reference; when a caller `CancellationTokenSource` deadline fires (`ct.IsCancellationRequested`), `GetParameterAsync` throws `TaskCanceledException`, which `LoadAsync` and `Parallel.ForEachAsync` rethrow.
4. **`Exception.Message` contents.** `AmazonS3Exception` messages for XML error payloads (`NoSuchKeyException`, `AccessDenied`, `NoSuchBucket`, `InternalError`, `PermanentRedirect`) carry the XML `<Message>` text without the key/bucket in our probes, whereas `S3ObjectStore` itself embeds `key` in `ObjectReadRefusedException`. Non-XML S3 errors include the status/error code, and `SocketsHttpHandler` `HttpRequestException` messages include `(host:port)` — which for default virtual-hosted S3 includes the bucket hostname (`{bucket}.s3...`), and for loopback/IP endpoints includes `ip:port`. SSM `ParameterVersionNotFoundException` includes the parameter name and version; `AmazonSimpleSystemsManagementException` for `AccessDeniedException` includes the IAM principal ARN and full parameter ARN/name; and `ParameterNotFoundException` with an empty `message` echoes the raw JSON HTTP body verbatim. `RegistrySource` and `RegistryPoller` log `ex.Message` directly, whereas `Assets`, `ParameterStoreClientSecretSource`, and `ProviderDiscovery` log bounded failure fields (`ex.GetType()`, `ErrorCode`, or mapped outcomes).

---

## 1. Defaults, timeouts, `MaxErrorRetry`, and `AWS_NEW_RETRIES_2026`

In `src/Platform.Registry/PlatformRegistryExtensions.cs` (lines 45–46), both AWS clients are registered with parameterless constructors:

```csharp
services.TryAddSingleton<IAmazonSimpleSystemsManagement>(_ => new AmazonSimpleSystemsManagementClient());
services.TryAddSingleton<IAmazonS3>(_ => new AmazonS3Client());
```

`Platform.Registry` pins `AWSSDK.S3` `4.0.101.6` and `AWSSDK.SimpleSystemsManagement` `4.0.104.2` (`Directory.Packages.props`), resolving `AWSSDK.Core` `4.0.102.9` standalone and `4.0.102.12` in `Gateway` (lifted by `AWSSDK.DynamoDBv2` `4.0.108`).

### Observed config properties and underlying `HttpClient` / `SocketsHttpHandler`

Inspecting `new AmazonS3Config()` and `new AmazonSimpleSystemsManagementConfig()` via reflection, shipped XML docs, and inspecting the `HttpClient` created inside `HttpRequestMessageFactory._httpClientCache` after the first call:

| Property | `AmazonS3Config` (default) | `AmazonSimpleSystemsManagementConfig` (default) |
| --- | --- | --- |
| `ReadWriteTimeout` | Does not exist in AWSSDK v4 (`net8.0`) | Does not exist in AWSSDK v4 (`net8.0`) |
| `ClientConfig.Timeout` | `24.20:31:23.6470000` (`TimeSpan.FromMilliseconds(int.MaxValue)`) | `null` |
| Effective `HttpClient.Timeout` | `24.20:31:23.6470000` (~24.86 days) | `00:01:40` (100 s, .NET `HttpClient` default) |
| `ClientConfig.ConnectTimeout` | `null` | `null` |
| Effective `SocketsHttpHandler.ConnectTimeout` | `-00:00:00.0010000` (`Timeout.InfiniteTimeSpan`) | `-00:00:00.0010000` (`Timeout.InfiniteTimeSpan`) |
| `SocketsHttpHandler.PooledConnectionLifetime` | `-00:00:00.0010000` (infinite) | `-00:00:00.0010000` (infinite) |
| `SocketsHttpHandler.PooledConnectionIdleTimeout` | `00:01:00` (60 s) | `00:01:00` (60 s) |
| `ClientConfig.RetryMode` | `Standard` | `Standard` |
| `ClientConfig.MaxErrorRetry` (`IsMaxErrorRetrySet`) | `2` (`False`) | `2` (`False`) |

`AWSSDK.Core.xml` (`ClientConfig.MaxErrorRetry`) documents that for `Standard` and `Adaptive` retry modes, `MaxErrorRetry` returns `2` (3 total requests) for all services except `DynamoDBv2` and `DynamoDBStreams` (which default to `10`). `AWS_MAX_ATTEMPTS=5` moved `Config.MaxErrorRetry` to `4` (`5 - 1`) and caused 5 total HTTP attempts in the SSM timeout probe.

### Retry counts and `AWS_NEW_RETRIES_2026`

`AWSSDK.Core.xml` (`RetryPolicy.UseNewRetries2026`) documents that `AWS_NEW_RETRIES_2026=true` enables updated backoff timing and retry quota costs (default `false` in the pinned `4.0.102.9` / `4.0.102.12` assemblies, scheduled to become default at the end of 2026). Note that `BeforeRequestEvent` runs in `CallbackHandler` above `RetryHandler` in the SDK pipeline, so it fires only once per SDK call; attempt counts below were measured at the loopback server / `DelegatingHandler`.

| Probe | `AWS_NEW_RETRIES_2026` unset (attempts, timestamps) | `AWS_NEW_RETRIES_2026=true` (attempts, timestamps) | Resulting exception |
| --- | --- | --- | --- |
| S3 HTTP 500 (`InternalError`) | 3 (`[69, 487, 2107]` ms) | 3 (`[62, 83, 139]` ms) | `AmazonS3Exception` |
| SSM HTTP 500 (`InternalServerError`) | 3 (`[5, 570, 1923]` ms) | 3 (`[3, 8, 106]` ms) | `InternalServerErrorException` |
| S3 closed port (`Connection refused`) | 3 (`[0, 95, 1923]` ms, total 1932 ms) | 3 (`[0, 52, 119]` ms, total 126 ms) | `HttpRequestException` (inner `SocketException`) |
| SSM closed port (`Connection refused`) | 3 (`[0, 518, 1122]` ms, total 1125 ms) | 3 (`[0, 30, 59]` ms, total 60 ms) | `HttpRequestException` (inner `SocketException`) |
| S3 header stall (`Timeout=300ms`) | 3 (`[1, 986, 1948]` ms, total 2249 ms) | 3 (`[0, 329, 717]` ms, total 1020 ms) | `TimeoutException` (inner `TaskCanceledException`) |
| SSM header stall (`Timeout=300ms`) | 3 (`[2, 740, 2935]` ms, total 3237 ms) | 3 (`[1, 310, 632]` ms, total 934 ms) | `TimeoutException` (inner `TaskCanceledException`) |

All three failure classes (HTTP 500, connection refused, and per-attempt `TimeoutException`) are retried up to `MaxErrorRetry = 2` (3 attempts total). Enabling `AWS_NEW_RETRIES_2026=true` leaves the attempt count at 3 for both S3 and SSM while reducing backoff delays substantially.

---

## 2. `S3ObjectStore.GetAsync` vs reading `ResponseStream`

`S3ObjectStore.GetAsync` (`src/Platform.Registry/S3ObjectStore.cs:42–69`) awaits `s3.GetObjectAsync(new GetObjectRequest { ... }, ct)` and returns `new StoredObject(response.ResponseStream, response.ETag, response.ContentLength, NotModified: false)` without reading the body.

### What `GetObjectAsync` covers vs what `ResponseStream` reads cover

Probe measurements against a loopback HTTP server sending `200 OK`, `Content-Length`, a valid MD5 `ETag`, and partial body bytes before stalling or aborting:

- **Headers vs body boundary:** When the server sent headers and 10 bytes (of 1000) and then stalled, `s3.GetObjectAsync` returned in **1–5 ms**.
- **Stream wrapper:** Under default `new AmazonS3Config()` (`ResponseChecksumValidation` unset / `WHEN_SUPPORTED`), `response.ResponseStream` is `Amazon.Runtime.Internal.Util.MD5Stream`. At stream EOF, `MD5Stream.CalculateHash()` compares the computed MD5 against the `ETag` (when it is a 32-hex single-part ETag) and throws `Amazon.Runtime.AmazonClientException: Expected hash not equal to calculated hash` if they differ (observed when a server returned `"1234567890"` with empty-string MD5 `ETag`).
- **`ClientConfig.Timeout` does not apply to `ResponseStream`:** With `AmazonS3Config.Timeout = 200 ms`, a `1500 ms` mid-body stall between bytes 5 and 6 did **not** time out: `GetObjectAsync` returned in `1 ms`, and `StreamReader.ReadToEndAsync(CancellationToken.None)` waited `1503 ms` and returned `"1234567890"`. Similarly, with `Timeout = 400 ms` and a `5000 ms` mid-body stall, both `CopyToAsync(..., CancellationToken.None)` and `ReadToEndAsync(CancellationToken.None)` hung for `5003–5004 ms` until the server closed the socket.
- **Post-header cancellation of the `GetObjectAsync` token:** Cancelling `getCts` **after** `s3.GetObjectAsync(..., getCts.Token)` returned did not cancel `response.ResponseStream.CopyToAsync(ms, copyCts.Token)`; the read stayed open until `copyCts` (`200 ms`) fired (`TaskCanceledException`).
- **Premature close / abort exceptions during `CopyToAsync` / `ReadToEndAsync`:**
  - Clean TCP FIN before `Content-Length`: `System.Net.Http.HttpIOException` (`HttpRequestError.ResponseEnded`, `"The response ended prematurely, with at least 990 additional bytes expected. (ResponseEnded)"`).
  - TCP RST mid-body: `System.IO.IOException` with inner `System.Net.Sockets.SocketException` (`"Unable to read data from the transport connection: Connection reset by peer."`).
  - Cancelled read `CancellationToken`: `System.Threading.Tasks.TaskCanceledException` (`"The operation was canceled."`).
  - Cancelled `GetObjectAsync` while waiting for headers: `System.Threading.Tasks.TaskCanceledException` (`"A task was canceled."`).

### Consequences in the two gateway callers

1. **`Assets.InvokeAsync` (`src/Platform.Hosting/Assets.cs:165, 227–238`):**
   - `store.GetAsync(key, ifNoneMatch, context.RequestAborted)` runs inside `try/catch` (mapping non-aborted exceptions to `503 store_unavailable` with `LogStoreUnavailable`).
   - Once headers arrive, `Assets` sets `StatusCode = 200`, `ContentType`, `ContentLength`, `ETag`, `CacheControl`, and `XContentTypeOptions`, and then awaits `body.CopyToAsync(response.Body, context.RequestAborted)` **outside** the `try/catch`.
   - Because `AmazonS3Config.Timeout` is ~24.86 days and `ClientConfig.Timeout` does not govern `ResponseStream` anyway, **nothing on the server bounds a mid-body stall**; the request remains open until the client/ALB disconnects (`context.RequestAborted`) or TCP fails. A premature FIN (`HttpIOException`) or RST (`IOException`) escapes unhandled after response headers have started.
2. **`RegistrySource.RefreshOnceAsync` (`src/Platform.Registry/RegistrySource.cs:78–86, 97, 127–139`):**
   - `RefreshAsync` holds `SemaphoreSlim polling(1, 1)` across both `store.GetAsync(..., ct)` and `reader.ReadToEndAsync(ct)`.
   - `RegistryPoller` passes only `stoppingToken`. Because `AmazonS3Config.Timeout` is ~24.86 days and `ClientConfig.Timeout` does not apply to `ReadToEndAsync`, **either a header stall or a mid-body stall on the registry object blocks `RefreshOnceAsync` and holds `polling` until host shutdown**, starving every subsequent poll.
   - A mid-body FIN or RST during `ReadToEndAsync` is caught by `catch (Exception ex)` (lines 133–139), keeps the live snapshot, returns `RefreshOutcome.Failed`, and logs `ex.Message`.

---

## 3. `ParameterStoreClientSecretSource.PreloadAsync` batch timing and exceptions

`ParameterStoreClientSecretSource.PreloadAsync` (`src/Platform.Registry/ParameterStoreClientSecretSource.cs:70–73`) fans out `LoadAsync` with `Parallel.ForEachAsync` and `MaxDegreeOfParallelism = 4`.

### Stalled-endpoint batch duration

Under default `new AmazonSimpleSystemsManagementConfig()`:
- `HttpClient.Timeout` is `100 s`.
- `MaxErrorRetry` is `2` (**3 attempts** per call), and **per-attempt timeouts are retried**.
- In the loopback probe with 8 distinct parameter references, `MaxDegreeOfParallelism = 4`, and `Timeout = 250 ms`, the client sent **24 HTTP requests** (`8 × 3`) across 2 sequential waves of 4, finishing in `6338 ms` with `AWS_NEW_RETRIES_2026` unset (`1764 ms` with `AWS_NEW_RETRIES_2026=true`).
- Extrapolating the observed retry-on-timeout behavior (`3 × Timeout + retry backoff` per wave of 4) to the default `100 s` `HttpClient.Timeout`: one wave of 1–4 stalled parameters blocks for **~`300 s + ~2–3 s` backoff (~`303 s`)**, and `N` uncached pending parameters block `PreloadAsync` (and `RegistrySource`'s `polling` semaphore) for **`ceil(N / 4) × ~303 s`** (e.g., ~10 minutes for 5–8 pending secrets). `RegistryPoller` passes only `stoppingToken`, so during normal operation no shorter deadline stops a stalled batch; during shutdown, `stoppingToken` cancels the in-flight calls immediately.

### Exact exceptions reaching `LoadAsync`'s `catch` blocks

`LoadAsync` (`ParameterStoreClientSecretSource.cs:119–140`) has three catch clauses:
1. `catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }`
2. `catch (AmazonServiceException ex) { Fail(secretRef, ex.ErrorCode ?? ex.GetType().Name); return; }`
3. `catch (Exception ex) { Fail(secretRef, ex.GetType().Name); return; }`

Observed outcomes:
- **When `ClientConfig.Timeout` (`HttpClient.Timeout`) fires** while caller `ct` is not cancelled: `ssm.GetParameterAsync` throws **`System.TimeoutException`** (`Message = "The operation was canceled."`, `InnerException = System.Threading.Tasks.TaskCanceledException`) in both `AWSSDK.Core` `4.0.102.9` and `4.0.102.12`. It does not match clause 1 or clause 2, so it lands in clause 3 and records `Reason = "TimeoutException"`. (Note: the comment on line 135 mentions `TaskCanceledException`, whereas at the pinned v4 Core versions `HttpHandler` surfaces `TimeoutException` wrapping `TaskCanceledException`.)
- **When a caller `CancellationTokenSource` deadline fires** (`ct.IsCancellationRequested == true`): `ssm.GetParameterAsync` throws **`System.Threading.Tasks.TaskCanceledException`** (`InnerException = null`, `Message = "A task was canceled."`). Clause 1 rethrows it, and `Parallel.ForEachAsync` propagates `TaskCanceledException` out of `PreloadAsync`.
- **Deadline interaction note for `RegistrySource.TryPreloadAsync`:** `TryPreloadAsync` (`RegistrySource.cs:241–253`) uses `catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }` where `ct` is the poll's outer token. If a caller passes a separate linked `timeoutCts.Token` to `secrets.PreloadAsync` while the outer `ct` is not cancelled, `TaskCanceledException` from `timeoutCts` will not match `when (ct.IsCancellationRequested)` and will instead enter `catch (Exception ex)` at line 245 (logging `ex.Message` and returning `false` / `RefreshOutcome.Failed`). Conversely, a per-call timeout inside `LoadAsync` using a per-call token would abort the whole `Parallel.ForEachAsync` batch if it passed that per-call token as `ct` to `LoadAsync`'s `when (ct.IsCancellationRequested)` filter instead of catching its own timeout per reference.

---

## 4. `Exception.Message` contents across S3, SSM, and `SocketsHttpHandler`

All failure messages observed in the loopback probes for `AWSSDK.S3` `4.0.101.6`, `AWSSDK.SimpleSystemsManagement` `4.0.104.2`, and `SocketsHttpHandler` (.NET `10.0.12`):

| Client / Layer | Failure case | Exception type (`ErrorCode`) | Observed `ex.Message` | Carries key, URL, bucket, or param name? |
| --- | --- | --- | --- | --- |
| S3 | 404 `NoSuchKey` XML | `NoSuchKeyException` (`NoSuchKey`) | `The specified key does not exist.` | No (XML `<Key>` omitted from `Message`) |
| S3 | 403 `AccessDenied` XML | `AmazonS3Exception` (`AccessDenied`) | `Access Denied` | No (`S3ObjectStore` wraps in `ObjectReadRefusedException`, whose message includes `key`) |
| S3 | 404 `NoSuchBucket` XML | `AmazonS3Exception` (`NoSuchBucket`) | `The specified bucket does not exist` | No |
| S3 | 500 `InternalError` XML | `AmazonS3Exception` (`InternalError`) | `We encountered an internal error. Please try again.` | No |
| S3 | 301 `PermanentRedirect` XML | `AmazonS3Exception` (`PermanentRedirect`) | `The bucket you are attempting to access must be addressed using the specified endpoint.` | No (`<Endpoint>` omitted from `Message`) |
| S3 | 403 / 502 empty or HTML body | `AmazonS3Exception` (`Forbidden` / `BadGateway`) | `Error making request with Error Code BadGateway and Http Status Code BadGateway. No further error information was returned by the service.` | No |
| S3 | Mid-body TCP FIN | `HttpIOException` | `The response ended prematurely, with at least 990 additional bytes expected. (ResponseEnded)` | No |
| S3 | Mid-body TCP RST | `IOException` | `Unable to read data from the transport connection: Connection reset by peer.` | No |
| S3 (`ForcePathStyle=true`) | DNS failure | `HttpRequestException` | `nodename nor servname provided, or not known (nonexistent.invalid:9999)` | **Yes**: `host:port` |
| S3 (`ForcePathStyle=false`, default) | DNS failure | `HttpRequestException` | `nodename nor servname provided, or not known (corp-spa-bucket-prod.s3.nonexistent.invalid:9999)` | **Yes**: `{bucket}.host:port` |
| S3 / SSM | Connection refused | `HttpRequestException` | `Connection refused (127.0.0.1:56143)` | **Yes**: `ip:port` |
| S3 / SSM | `ClientConfig.Timeout` | `TimeoutException` | `The operation was canceled.` | No |
| SSM | 400 `ParameterNotFound` (empty `message`) | `ParameterNotFoundException` (`ParameterNotFound`) | `The service returned an error with Error Code ParameterNotFound and HTTP Body: {"__type":"ParameterNotFound","message":""}` | **Yes, if present in JSON body** (raw HTTP body echoed verbatim) |
| SSM | 400 `ParameterVersionNotFound` | `ParameterVersionNotFoundException` (`ParameterVersionNotFound`) | `Parameter /corp/spa-app/oauth/client-secret:7 not found.` | **Yes**: parameter name & version |
| SSM | 400 `AccessDeniedException` | `AmazonSimpleSystemsManagementException` (`AccessDeniedException`) | `User: arn:aws:sts::123456789012:assumed-role/gw/t is not authorized to perform: ssm:GetParameter on resource: arn:aws:ssm:us-east-1:123456789012:parameter/corp/spa-app/oauth/client-secret:7` | **Yes**: IAM principal ARN, account, region, parameter ARN/name |
| SSM | 400 / 502 HTML or empty body | `AmazonSimpleSystemsManagementException` (`null`) | `The service returned an error. See inner exception for details.` | No |

How repository callers log these today:
- **`Assets.cs`** (`LogStoreUnavailable`, line 363) and **`ParameterStoreClientSecretSource.cs`** (`Fail`, line 165) never log `ex.Message`; they log bounded fields (`ExceptionType`, `ErrorCode`, `SecretRef`).
- **`RegistrySource.cs`** interpolates `{Message}` (`ex.Message`) on `store.GetAsync` failure (line 107), `reader.ReadToEndAsync` failure (line 137), and `secrets.PreloadAsync` failure (line 251). On `store.GetAsync`, a 403 `AccessDenied` on the registry object throws `ObjectReadRefusedException(key, ex)`, whose message carries `key`; a `SocketsHttpHandler` transport failure carries the endpoint `host:port` (including the bucket hostname under default virtual-hosted S3); and an unexpected `AmazonServiceException` carries the service message/body.
- **`RegistryPoller.cs`** interpolates `{Message}` (`ex.Message`) when `Registry:*` configuration binding throws (line 100, which can carry a malformed config value) and when constructing `RegistrySource` or polling throws (line 122).

---

## Executed and read

**Executed** (2026-11-27, macOS Darwin 27.0.0 arm64, .NET 10 SDK `10.0.401` / runtime `10.0.12`, local loopback HTTP stand-in only; no live AWS calls):
- Built and ran throwaway probe console apps referencing pinned NuGet packages `AWSSDK.S3` `4.0.101.6`, `AWSSDK.SimpleSystemsManagement` `4.0.104.2`, and `AWSSDK.Core` `4.0.102.12` (and re-verified `AWSSDK.Core` `4.0.102.9` for constructor/DNS/timeout/retry parity):
  - Property/reflection inspection of `AmazonS3Config`, `AmazonSimpleSystemsManagementConfig`, runtime handler pipelines, and populated `HttpRequestMessageFactory._httpClientCache` (`HttpClient.Timeout`, `SocketsHttpHandler.ConnectTimeout`, `PooledConnectionLifetime`, `PooledConnectionIdleTimeout`).
  - Loopback HTTP/TCP server probes (`127.0.0.1`) for 500 responses, closed-port `Connection refused`, header stalls (`ClientConfig.Timeout`), `AWS_NEW_RETRIES_2026=true`, `AWS_MAX_ATTEMPTS=5`, mid-body stalls (`1500 ms` and `5000 ms` under `200 ms` / `400 ms` `Timeout`), post-header token cancellation, premature TCP FIN, TCP RST, `MD5Stream` mismatch, 8-item `Parallel.ForEachAsync` (`MaxDegreeOfParallelism = 4`) stalled batch, and S3/SSM error payload `Exception.Message` formatting.

**Read** (all on 2026-11-27):
- Repository source and package pins on `origin/main` (`8de2d621`): `Directory.Packages.props`, `src/Platform.Registry/PlatformRegistryExtensions.cs`, `src/Platform.Registry/S3ObjectStore.cs`, `src/Platform.Registry/ParameterStoreClientSecretSource.cs`, `src/Platform.Registry/RegistrySource.cs`, `src/Platform.Registry/RegistryPoller.cs`, `src/Platform.Hosting/Assets.cs`, `src/Platform.Identity/DynamoDbSessionTable.cs`, `tests/Platform.Identity.Tests/DynamoDbProtocolTests.cs`, `tests/Platform.Registry.Tests/FakeS3Server.cs`, `contracts/http-contract.md`, `contracts/telemetry.md`, `docs/gaps.md`.
- Shipped NuGet XML documentation in `~/.nuget/packages/`:
  - `awssdk.core/4.0.102.9/lib/net8.0/AWSSDK.Core.xml`
  - `awssdk.core/4.0.102.12/lib/net8.0/AWSSDK.Core.xml`
  - `awssdk.s3/4.0.101.6/lib/net8.0/AWSSDK.S3.xml`
  - `awssdk.simplesystemsmanagement/4.0.104.2/lib/net8.0/AWSSDK.SimpleSystemsManagement.xml`
- GitHub issues `#1580`, `#1581`, and `#1586`.
- AWS SDK documentation (web search on `docs.aws.amazon.com` and `github.com/aws/aws-sdk-net`): AWS SDKs retry behavior (`AWS_NEW_RETRIES_2026`, Standard mode, 2026 defaults) and `AmazonS3Config` `ClientConfig.MaxTimeout` initialization.
