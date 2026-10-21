# What a build says about the five package facts behind the DynamoDB store and the key ring

A research finding for the Phase 2 wayfinder (issue #215), answering its research ticket of the
same title (issue #216). It is read by the sessions that write tasks P2-1 and P2-2 (#221, #222) and
by the integrator who decides the two change requests; it decides nothing. Written 2026-10-20
against `origin/main` at `ad87e3c`, with the two trial pins applied in a scratch worktree that was
never committed. Every claim says whether it was **executed** on this workstation or **read** from
a primary source; the "Sources" list at the end holds the URLs once.

Environment the ticket fixed: .NET SDK 10.0.401; shared frameworks `Microsoft.AspNetCore.App`
10.0.12 and `Microsoft.NETCore.App` 10.0.12 (`dotnet --list-runtimes`); no Docker engine; no AWS
credentials, so nothing called AWS. nuget.org was reachable for restores.

## Summary

| # | Fact as the ticket states it | Verdict |
| --- | --- | --- |
| 1 | `AWSSDK.Core` resolves to 4.0.102.12 in `Platform.Identity` and `Gateway`, stays at 4.0.102.9 in `Platform.Registry` and `spactl`; nothing conflicts | **Confirmed**, with one graph the request did not mention: `Platform.Hosting` (and `Platform.Hosting.Tests`) resolve `AWSSDK.Core` 4.0.3.17 and `AWSSDK.SimpleSystemsManagement` 4.0.7.3, the key-ring package's own floors |
| 2 | The package takes an `IAmazonSimpleSystemsManagement` registered ahead of it; a stub registered first is the whole test seam | **Confirmed** by source and by execution. Four wording corrections to P2-2, items 1 and 3: the fallback item 3 names does not exist (`SSMXmlRepository` is `internal`); the call also replaces `IKeyManager`; the repository disposes the client it is given; the net10.0 assembly names three operations, not the two item 1 counts |
| 3 | The 10.0.7 floor restores cleanly beside the 10.0.12 shared framework, no NU1605/NU1608, no `Microsoft.AspNetCore.*` package in the output | **Corrected in part.** No NU1605, no NU1608, build exit 0, nothing from the floor reaches any output. But the floor's own dependencies `System.Security.Cryptography.Xml` 10.0.7 and `System.Security.Cryptography.Pkcs` 10.0.7 stay in the graph of `Platform.Hosting` and of every `Microsoft.NET.Sdk` project that references it, and raise 10 advisory warnings each (NU1902/NU1903), 50 in all; they pass only because `Directory.Build.props` already lists NU1901-NU1904 in `WarningsNotAsErrors` |
| 4 | A key element with the framework defaults fits a standard-tier parameter (4 KB); `StandardOnly` is the default | **Confirmed.** 956 characters (956 UTF-8 bytes) as the repository stores it, `AES_256_CBC` + `HMACSHA256`, chosen tier `Standard`. Holds with no XML encryptor configured, which is what the gateway gets; `ProtectKeysWithCertificate` with RSA-2048 grows it to 3,505 |
| 5 | `AWSSDK.Extensions.NETCore.Setup` arrives transitively at 4.0.3.25 and needs no pin | **Confirmed.** 4.0.3.25 in `Gateway`, `Platform.Hosting` and `Platform.Hosting.Tests`; Apache-2.0 by its nuspec's licence expression |

## What was executed

In a detached worktree of `origin/main` (`ad87e3c`):

1. `Directory.Packages.props` gained, under `Label="AWS"`,
   `<PackageVersion Include="AWSSDK.DynamoDBv2" Version="4.0.108" />` and
   `<PackageVersion Include="Amazon.AspNetCore.DataProtection.SSM" Version="4.0.3" />`;
   `src/Platform.Identity/Platform.Identity.csproj` gained `<PackageReference Include="AWSSDK.DynamoDBv2" />`;
   `src/Platform.Hosting/Platform.Hosting.csproj` gained `<PackageReference Include="Amazon.AspNetCore.DataProtection.SSM" />`.
   Four added lines, nothing else.
2. `dotnet restore` (exit 0, 5 x NU1902 and 45 x NU1903, no other NuGet code),
   `dotnet build --no-restore` (exit 0, "50 Warning(s), 0 Error(s)", the same codes),
   `dotnet list <project> package --include-transitive` for `Platform.Identity`, `Gateway`,
   `Platform.Registry`, `tools/spactl`, `Platform.Hosting` and `Platform.Hosting.Tests`,
   `dotnet nuget why` for the paths quoted below, and
   `dotnet msbuild src/Platform.Hosting/Platform.Hosting.csproj -t:ResolveAssemblyReferences -getItem:ReferencePath`.
3. A console project outside the solution (`scratch-ssm/`, `Microsoft.NET.Sdk` with
   `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, referencing the key-ring package and
   `AWSSDK.SimpleSystemsManagement` through the same central pins) that stubs
   `IAmazonSimpleSystemsManagement` with a `System.Reflection.DispatchProxy` recording every call,
   runs `AddDataProtection().SetApplicationName(..).PersistKeysToAWSSystemsManager("/x/")` with the
   stub registered before, after and not at all, protects a payload, and measures the key element
   both as the stub's `PutParameterRequest.Value` and from an in-memory `IXmlRepository`.

The restore, build and listing logs and the scratch program's output were kept only in the
session's scratchpad; the numbers below are copied from them.

## 1. `AWSSDK.Core` per project graph

**Executed.** `dotnet list package --include-transitive`, trimmed to the AWS rows:

| Project | `AWSSDK.DynamoDBv2` | `AWSSDK.S3` | `AWSSDK.SimpleSystemsManagement` | `AWSSDK.Extensions.NETCore.Setup` | `AWSSDK.Core` |
| --- | --- | --- | --- | --- | --- |
| `Platform.Identity` | 4.0.108 (top-level) | - | - | - | **4.0.102.12** |
| `Gateway` | 4.0.108 | 4.0.101.6 | 4.0.104.2 | 4.0.3.25 | **4.0.102.12** |
| `Platform.Registry` | - | 4.0.101.6 (top-level) | 4.0.104.2 (top-level) | - | **4.0.102.9** |
| `tools/spactl` | - | 4.0.101.6 (top-level) | 4.0.104.2 | - | **4.0.102.9** |
| `Platform.Hosting` | - | - | **4.0.7.3** | 4.0.3.25 | **4.0.3.17** |
| `Platform.Hosting.Tests` | - | - | **4.0.7.3** | 4.0.3.25 | **4.0.3.17** |

`dotnet nuget why src/Platform.Identity/Platform.Identity.csproj AWSSDK.Core` shows the one path
`AWSSDK.DynamoDBv2 (v4.0.108) -> AWSSDK.Core (v4.0.102.12)`. The restore log holds no NU1605 and
no NU1608; its only codes are the NU1902/NU1903 advisories of section 3.

**Read.** The nuspecs in the local package cache agree with the request: `AWSSDK.DynamoDBv2` 4.0.108
declares `AWSSDK.Core [4.0.102.12, 5.0.0)` and `license type="expression"` Apache-2.0.

**Where the ticket's fact is right.** The four graphs it names resolve exactly as P2-1 item 1 says,
and nothing conflicts. Item 1 needs no change.

**What it did not mention.** `Platform.Hosting` does not reference an AWS SDK package directly
today, so with the key-ring package added its own graph takes the package's floors: `AWSSDK.Core`
4.0.3.17 and `AWSSDK.SimpleSystemsManagement` 4.0.7.3, and `Platform.Hosting.Tests` inherits them
(`Platform.Testing` brings no AWS package). The SDK's `AWSSDK.Core.dll` and
`AWSSDK.SimpleSystemsManagement.dll` that `Platform.Hosting` compiles against come from those two
old packages (`ReferencePath`, executed), while the gateway runs the 4.0.102.12 and 4.0.104.2
copies. Nothing broke here: the solution built, and the gateway's graph unifies upward, so the
process loads one version of each. But the P2-2 item 1 table's row
"`AWSSDK.SimpleSystemsManagement` | 4.0.7.3 | The existing pin, 4.0.104.2" is true only in a graph
that carries the pin, which `Platform.Hosting` and its test project do not. The consequence for
P2-2 item 3's "`src/Platform.Hosting` gains an AWS package": if workstream 2 wants its hermetic
test and its compile to see the same SDK the gateway runs, `Platform.Hosting.csproj` also adds
`<PackageReference Include="AWSSDK.SimpleSystemsManagement" />` (no version, the pin exists), and
the request should say so or say why not.

## 2. Does the package take a registered `IAmazonSimpleSystemsManagement`?

**Read.** The package's source for 4.0.3 (see "Source provenance" below), file
`src/Amazon.AspNetCore.DataProtection.SSM/ExtensionMethods.cs`:

- line 60: `builder.Services.TryAddAWSService<IAmazonSimpleSystemsManagement>();`
- lines 62-74: an `IConfigureOptions<KeyManagementOptions>` singleton whose factory calls
  `services.GetService<IAmazonSimpleSystemsManagement>()` and sets
  `options.XmlRepository = new SSMXmlRepository(ssmClient, parameterNamePrefix, ssmOptions, loggerFactory)`
- lines 76-78: under `#if NET9_0_OR_GREATER`, `builder.Services.AddSingleton<IKeyManager, XmlDeletableKeyManager>();`

`TryAddAWSService<T>` in `AWSSDK.Extensions.NETCore.Setup` is documented in its own source as
"Adds the Amazon service client to the dependency injection framework if the service type hasn't
already been registered" (`ServiceCollectionExtensions.cs`, the `TryAddAWSService<T>` overloads).
So the registration order is: whatever is already registered for the interface wins; otherwise a
singleton factory for the SDK's default client is added; either way the repository is handed the
container's instance when `KeyManagementOptions` is first built.

`SSMXmlRepository.cs` line 38 (net9+) / 44: `internal class SSMXmlRepository`, and its constructor's
comment, lines 59-61: "This class is internal and the constructor isn't meant to be called outside
this assembly." Its constructor (line 67) takes `IAmazonSimpleSystemsManagement ssmClient` and
throws on null (line 69). `AddUserAgentHandlerToClient` (lines 371-384) attaches a user-agent
handler only when the client `is AmazonSimpleSystemsManagementClient`, so a stub is left alone.
`Dispose` (lines 352-363) disposes the injected client.

**Executed.** The scratch program, against the framework's DataProtection 10.0.12 (confirmed at
run time: `Microsoft.AspNetCore.DataProtection.dll` loaded from
`shared/Microsoft.AspNetCore.App/10.0.12/`) and the package's 4.0.3 assembly (informational
version `4.0.3`):

| Run | Registration | Descriptors for `IAmazonSimpleSystemsManagement` after the call | Resolved client is the stub | Calls recorded on the stub by one `Protect` |
| --- | --- | --- | --- | --- |
| A | Stub registered **before** `PersistKeysToAWSSystemsManager` | 1 (the stub instance) | yes | `GetParametersByPathAsync`, `PutParameterAsync`, `GetParametersByPathAsync` |
| B | Stub registered **after** | 2 (the package's factory, then the stub) | yes (last registration wins at `GetService`) | the same three |
| C | Nothing registered | 1 (a singleton factory) | - | resolving it threw `Amazon.Runtime.AmazonClientException: Failed to resolve AWS credentials` with `AWS_REGION` set and no network call |

Run A also showed: no call reached the stub before `Protect` (resolving
`IOptions<KeyManagementOptions>` constructs the `SSMXmlRepository` without touching SSM);
`KeyManagementOptions.XmlEncryptor` was null; `IKeyManager` had two descriptors,
`XmlKeyManager` then `Amazon.AspNetCore.DataProtection.SSM.XmlDeletableKeyManager`, and the
second one resolved; and the single `PutParameterRequest` carried
`Name=/x/key-<guid>`, `Type=SecureString`, `Tier=Standard`, `KeyId=null`,
`Description="ASP.NET Core DataProtection Key"`, no tags.

**Verdict.** Confirmed: a stub registered first is the whole test seam, and it even wins when
registered second. The request's sentence "resolves its `IAmazonSimpleSystemsManagement` with
`GetService` and otherwise constructs a default client" is right in effect; the mechanism is
`TryAddAWSService` at registration time and `GetService` when the options are built.

**Consequences for P2-2's wording (items 1 and 3).**

- Strike "If it is not, the `PersistKeysToAWSSystemsManager` call gives way to the same package's
  repository registered behind the framework's `IXmlRepository`, whose constructor takes the client
  explicitly." That fallback does not exist from outside the assembly: the class is `internal`.
  The condition it hedged against is now known false, so the sentence can go.
- Add that the call appends `XmlDeletableKeyManager` as `IKeyManager` (an `AddSingleton`, not a
  `TryAdd`), which replaces the framework's `XmlKeyManager`. A test that substitutes `IKeyManager`
  must register after this call; nothing in the gateway does so today.
- Add that `SSMXmlRepository.Dispose` disposes the client it was given. Nothing disposes the
  repository (it lives inside `KeyManagementOptions`, which the container does not dispose), so
  this is latent, but the client it would dispose is the registry's shared singleton from
  `PlatformRegistryExtensions.cs:31-35`.
- Correct "the two operations the assembly references" in item 1. **Executed:** the `net10.0`
  assembly in the 4.0.3 package names `PutParameterAsync`, `GetParametersByPathAsync` and
  `DeleteParameterAsync`; the `net8.0` assembly names only the first two. The third runs only when
  something calls `IKeyManager.DeleteKeys` / `DeleteAllKeys` (source `SSMXmlRepository.cs` lines
  213-298, under `#if NET9_0_OR_GREATER`), which the framework never does on its own, so a task role
  without `ssm:DeleteParameter` starts and serves normally and fails only on an explicit deletion.
  The request should either list the third operation in the IAM note or say that deletion is out of
  scope and why.

## 3. Does the 10.0.7 floor restore cleanly beside the 10.0.12 shared framework?

**Executed.** Restore and build both exit 0. The restore log carries no NU1605 (downgrade) and no
NU1608 (version outside the dependency constraint). Its only NuGet codes are advisories:

| Project | NU1902 (moderate) | NU1903 (high) |
| --- | --- | --- |
| `Platform.Hosting` | 1 | 9 |
| `Platform.Hosting.Tests` | 1 | 9 |
| `Gateway.TestHarness` | 1 | 9 |
| `Gateway.IntegrationTests` | 1 | 9 |
| `Gateway.SecurityTests` | 1 | 9 |
| `Gateway`, `Platform.Identity`, every other project | 0 | 0 |

The ten are the same ten everywhere: `System.Security.Cryptography.Xml` 10.0.7
(GHSA-23rf-6693-g89p, GHSA-7hqp-7c5m-4vq6, GHSA-8q5v-6pqq-x66h, GHSA-c399-9853-6jmj,
GHSA-cvvh-rhrc-wg4q, GHSA-g8r8-53c2-pm3f, GHSA-mmjf-rqrv-855v, GHSA-r955-8q5j-3h83, all high) and
`System.Security.Cryptography.Pkcs` 10.0.7 (GHSA-846f-f764-76mx high, GHSA-2fmv-rv2c-59x7
moderate). `dotnet nuget why src/Platform.Hosting/Platform.Hosting.csproj System.Security.Cryptography.Xml`:
`Amazon.AspNetCore.DataProtection.SSM (v4.0.3) -> Microsoft.AspNetCore.DataProtection (v10.0.7) -> System.Security.Cryptography.Xml (v10.0.7)`.

Why the gateway is silent and the library is not: the .NET 10 SDK prunes from a restore graph the
packages that its framework references supply. `Gateway` is `Microsoft.NET.Sdk.Web`, so its
restore spec (`obj/Gateway.csproj.nuget.dgspec.json`) lists `frameworkReferences`
`Microsoft.AspNetCore.App` and 412 `packagesToPrune`, among them
`Microsoft.AspNetCore.DataProtection -> (,10.0.32767]`,
`Microsoft.AspNetCore.DataProtection.Abstractions -> (,10.0.32767]` and
`System.Security.Cryptography.Xml -> (,10.0.32767]`; `dotnet nuget why src/Gateway/Gateway.csproj Microsoft.AspNetCore.DataProtection`
answers "does not have a dependency". `Platform.Hosting` is `Microsoft.NET.Sdk` and gets
`Microsoft.AspNetCore.App` only at build time, through `Platform.Abstractions`'s
`<FrameworkReference>`; its restore spec lists `Microsoft.NETCore.App` alone and 272 prune
entries, none of them these, so the packages stay in its graph and the audit sees them. The three
`Gateway.*` test projects and `Platform.Hosting.Tests` are `Microsoft.NET.Sdk` too.

What reaches compile and output (executed):

- `ReferencePath` for `Platform.Hosting` resolves `Microsoft.AspNetCore.DataProtection.dll`,
  `Microsoft.AspNetCore.DataProtection.Abstractions.dll` and `System.Security.Cryptography.Xml.dll`
  from `packs/Microsoft.AspNetCore.App.Ref/10.0.12/ref/net10.0/`, the framework winning the
  conflict; `System.Security.Cryptography.Pkcs.dll` it resolves from the 10.0.7 package, because
  that assembly is not in the reference pack. `AWSSDK.Core.dll` and
  `AWSSDK.SimpleSystemsManagement.dll` come from the 4.0.3.17 and 4.0.7.3 packages (section 1).
- No `Microsoft.AspNetCore.*.dll`, no `System.Security.Cryptography.Xml.dll` and no
  `System.Security.Cryptography.Pkcs.dll` is in the output of `Gateway`, `Platform.Hosting`,
  `Platform.Hosting.Tests`, `Gateway.IntegrationTests` or the scratch project; `Gateway.deps.json`
  lists none of them. The shared framework `Microsoft.AspNetCore.App/10.0.12` carries all three,
  and the scratch run loaded `Microsoft.AspNetCore.DataProtection.dll` and
  `System.Security.Cryptography.Xml.dll` from there.
- What the pin does add to `Gateway`'s output: `Amazon.AspNetCore.DataProtection.SSM.dll` and
  `AWSSDK.Extensions.NETCore.Setup.dll` (plus `AWSSDK.DynamoDBv2.dll` from P2-1).
  `Microsoft.Bcl.Cryptography.dll` 10.0.2 is there too but was already: `dotnet nuget why` traces it
  to `Microsoft.AspNetCore.Authentication.OpenIdConnect` through `Microsoft.IdentityModel.Tokens`.

**Read.** The GitHub advisory records give each advisory's range. Five of the ten list the shared
framework's own version as vulnerable: `System.Security.Cryptography.Xml >= 10.0.0, <= 10.0.12 ->
10.0.13` (GHSA-7hqp-7c5m-4vq6, GHSA-c399-9853-6jmj, GHSA-r955-8q5j-3h83) and
`System.Security.Cryptography.Pkcs >= 10.0.0, <= 10.0.12 -> 10.0.13` (GHSA-846f-f764-76mx,
GHSA-2fmv-rv2c-59x7); the other five end at `<= 10.0.9 -> 10.0.10`. The 10.0.12 runtime this
workstation runs therefore carries those five whether or not this package is added: NuGet audit reports package versions, not the shared framework, so the warnings are the
visible edge of a runtime-patch question (a 10.0.13 runtime), not something the pin introduces or
a different pin removes.

**Verdict and consequence for the wording.** "Restores cleanly" is true of version resolution and
false of the audit. P2-2 item 1's table row for `Microsoft.AspNetCore.DataProtection` should read
along the lines of: "The shared framework carries 10.0.12 and wins at compile and run time
(verified: nothing from the 10.0.7 floor reaches an output). The floor's dependencies
`System.Security.Cryptography.Xml` 10.0.7 and `.Pkcs` 10.0.7 stay in the restore graph of
`Platform.Hosting` and of every `Microsoft.NET.Sdk` project that references it, where NuGet audit
raises 10 NU1902/NU1903 warnings per project; `Directory.Build.props` already downgrades
NU1901-NU1904 to warnings, so the build passes, and the `docs/dependencies.md` row should name the
noise so the next session does not chase it." The Blocking paragraph's "restores cleanly beside the
10.0.12 shared framework" can be resolved as "restores without conflict; the advisory noise is
expected and explained in the finding". If the integrator wants the warnings gone rather than
explained, the levers are the integrator's: a central `PackageVersion` plus a direct reference in
`Platform.Hosting` to `System.Security.Cryptography.Xml` and `.Pkcs` at 10.0.13 (the advisories'
first patched version; its existence on nuget.org was not checked here), or `NuGetAuditSuppress`
items for the ten advisories. Neither was tried, and the finding takes no position.

The runtime sentence above is about this workstation. The gateway image's runtime is whatever
`mcr.microsoft.com/dotnet/aspnet:10.0-noble` resolves to at build time (`src/Gateway/Dockerfile`
line 39, read), a floating tag that was not resolved here; whether it is 10.0.12 or already
10.0.13 is a fact for the session that next builds the image.

**Source provenance for the floor.** The package's `net10.0` dependency group says
`Microsoft.AspNetCore.DataProtection` 10.0.7 (nuspec, read from the cache), but the repository tag
`release_2026-04-17` (`8b6a95a`) has `Version="10.0.5"` in the csproj. Commit `8296bf0`
(2026-04-23, "Update Amazon.AspNetCore.DataProtection.SSM.csproj (#141) (#142)") raised it to
10.0.7 after the tag, and the package's files are dated 2026-04-23. `compare/release_2026-04-17...8296bf0`
lists only the csproj, `CHANGELOG.md` and an `.autover` file, so the `.cs` files quoted in
section 2 are the 4.0.3 source either way.

## 4. Does a key element fit a standard-tier parameter?

**Read.** `SSMXmlRepository.StoreElementAsync` (lines 166-209) stores `element.ToString()` (the
indented form) as a `SecureString` parameter. `GetParameterTier` (lines 304-347) compares that
string's `Length` in characters against two constants: `advancedTierMaxSize = 8192` (throws
`SSMParameterToLongException` above it, any mode) and `standardTierMaxSize = 4096` (above it, mode
`StandardOnly` throws the same exception; `AdvancedUpgradeable` returns `Advanced`).
`PersistOptions.TierStorageMode` defaults to `TierStorageMode.StandardOnly` (`PersistOptions.cs`
line 33). AWS's `PutParameter` reference says the same limits in its own words: "Standard
parameters have a content size limit of 4 KB" and "Advanced parameters have a content size limit
of 8 KB"; the SDK's model documentation in the cached `AWSSDK.SimpleSystemsManagement` 4.0.104.2
carries the identical sentences.

**Executed.** With `AddDataProtection()` and nothing else about algorithms, on the 10.0.12
framework:

| Run | Repository | `element.ToString().Length` | UTF-8 bytes | `DisableFormatting` length | Algorithms in the descriptor | XML encryptor | Chosen tier |
| --- | --- | --- | --- | --- | --- | --- | --- |
| A | `SSMXmlRepository` via the stub (`PutParameterRequest.Value`) | **956** | 956 | 884 | `AES_256_CBC`, `HMACSHA256` | none (`XmlEncryptor` null) | `Standard` |
| D | In-memory `IXmlRepository` | **956** | 956 | 884 | `AES_256_CBC`, `HMACSHA256` | none | - |
| E | In-memory, plus `ProtectKeysWithCertificate(self-signed RSA-2048)` | 3,505 | 3,505 | 3,194 | `AES_256_CBC`, `HMACSHA256` | `EncryptedData` present, plaintext `masterKey` gone | - |

The element is `<key>` with a `<masterKey>` of 88 base64 characters (a 512-bit secret) in runs A
and D; `PersistOptions().TierStorageMode` read at run time was `StandardOnly`. Characters and
bytes coincide because the XML is ASCII.

**Verdict.** Confirmed: 956 of 4,096, under a quarter of the standard tier, and the package itself
picked `Standard`. Two qualifications for the request:

- The fact holds **with no XML encryptor**, which is what the gateway has: Linux has no DPAPI
  default, the request configures none, and the `SecureString` type makes Parameter Store the
  encryption at rest. If a later decision adds `ProtectKeysWithCertificate` or another encryptor,
  the element grows (3,505 with RSA-2048; a longer certificate chain or RSA-4096 will be larger)
  and `StandardOnly` throws instead of falling back. P2-2 should state the no-encryptor assumption
  next to the 4 KB claim, or set `TierStorageMode.AdvancedUpgradeable` and accept the advanced
  tier's charge.
- The package measures characters of the indented `ToString()`, not bytes of a compact form. Any
  future size estimate should use that form.

## 5. Does `AWSSDK.Extensions.NETCore.Setup` arrive at 4.0.3.25 without a pin?

**Executed.** Resolved 4.0.3.25 in `Gateway`, `Platform.Hosting` and `Platform.Hosting.Tests`
(section 1 table); `AWSSDK.Extensions.NETCore.Setup.dll` is in `Gateway`'s output and
`Gateway.deps.json` lists `AWSSDK.Extensions.NETCore.Setup/4.0.3.25`. Nothing else in the solution
asks for it.

**Read.** Its nuspec (local cache, 4.0.3.25): `license type="expression"` Apache-2.0; dependencies
`AWSSDK.Core` 4.0.3.17 and `Microsoft.Extensions.{Configuration,DependencyInjection,Logging}.Abstractions`
(2.0.0 floors, 8.0.0 for DependencyInjection.Abstractions in one group), all of which the graph
already satisfies.

**Verdict.** Confirmed; the request's wording stands. The one thing it carries that the request
does not say: this package is where `TryAddAWSService` lives (section 2), so it is a run-time
participant in the registration order, not only a dependency.

## Sources

Primary sources read, in the order the sections use them:

- Package metadata, from the local NuGet cache after the trial restore:
  `~/.nuget/packages/awssdk.dynamodbv2/4.0.108/awssdk.dynamodbv2.nuspec`,
  `~/.nuget/packages/amazon.aspnetcore.dataprotection.ssm/4.0.3/amazon.aspnetcore.dataprotection.ssm.nuspec`
  (and its `LICENSE`, Apache License 2.0),
  `~/.nuget/packages/awssdk.extensions.netcore.setup/4.0.3.25/awssdk.extensions.netcore.setup.nuspec`.
- The key-ring provider's source, `https://github.com/aws/aws-ssm-data-protection-provider-for-aspnet`:
  `src/Amazon.AspNetCore.DataProtection.SSM/{ExtensionMethods,SSMXmlRepository,PersistOptions,TierStorageMode,SSMParameterToLongException}.cs`
  at tag `release_2026-04-17` (`8b6a95af2d37b99c83663e5f66199c15d12a3ee4`); the csproj at that tag
  and at `8296bf0`; `compare/release_2026-04-17...8296bf0`.
- `TryAddAWSService<T>` in `https://github.com/aws/aws-sdk-net`,
  `extensions/src/AWSSDK.Extensions.NETCore.Setup/ServiceCollectionExtensions.cs`.
- Advisory records, `https://github.com/advisories/<id>` for the ten ids in section 3, read
  through the GitHub API (`vulnerable_version_range`, `first_patched_version`).
- AWS Systems Manager API reference, `PutParameter`, `Tier` parameter:
  `https://docs.aws.amazon.com/systems-manager/latest/APIReference/API_PutParameter.html`.
- This repository at `origin/main` `ad87e3c`: `Directory.Build.props` (`WarningsNotAsErrors`),
  `Directory.Packages.props`, `src/Platform.Abstractions/Platform.Abstractions.csproj`
  (`FrameworkReference`), `src/Gateway/Gateway.csproj` (`Microsoft.NET.Sdk.Web`),
  `contracts/changes/p2-1-dynamodb-session-store.md` item 1,
  `contracts/changes/p2-2-data-protection-key-ring.md` items 1, 3 and Blocking.
