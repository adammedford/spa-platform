# Change request P2-2: a pin, a `DataProtection` section and a home for the shared key ring

- **Raised by:** the Phase 2 planning session of 2026-10-19, on behalf of workstream 2 (Hosting and
  registry), which this request recommends as the owner, with workstream 3 (OAuth and sessions)
  for the identity-side proof
- **Tasks:** P2-2; P2-1, whose ticket encryption needs the ring to be shared; P2-9, whose read-only
  root filesystem needs no key on disk
- **Date:** 2026-10-19
- **Status:** open

## The gap

Plan row P2-2: "Data-protection keys persisted to Parameter Store under a KMS key, through the AWS
provider for ASP.NET Core data protection, with one application name per cell", accepted when "a
cookie and a login state issued by one task are accepted by another"
(`docs/plan/06-phase-2-and-risks.md:12`).

Nothing in `src/` configures data protection. `AddPlatformIdentity` calls
`services.AddAuthentication()` (`src/Platform.Identity/PlatformIdentityExtensions.cs:37`), which
registers the framework's defaults, and the one `AddDataProtection` in the tree is the ephemeral
provider of a security test host (`tests/Platform.Security.Tests/AntiforgeryHost.cs:123`). Under
those defaults each task keeps a key ring of its own, on disk under the process's home directory
when one exists and in memory otherwise
(https://learn.microsoft.com/aspnet/core/security/data-protection/configuration/default-settings),
which is the "key durability" gap in the same plan file: "cookies and in-flight logins die with the
task". Nothing sets the application name either, although `Platform:Cell`'s description already
says the cell name is "part of the data-protection application name"
(`contracts/platform-config.schema.json:19`).

Three consumers read the keys: the cookie handler's ticket format and the OpenID Connect handler's
state and nonce protectors, both workstream 3's, and the antiforgery tokens behind
`__Host-spa-csrf`, workstream 4's. Whoever registers the ring serves all three.

## The change

### 1. Pin `Amazon.AspNetCore.DataProtection.SSM` 4.0.3

Confirmed on nuget.org on 2026-10-19: 4.0.3 is the newest of the 21 versions listed. Its nuspec
declares a licence file rather than an expression; the `LICENSE` inside the package is the Apache
License 2.0, read from the package itself. It has a `net10.0` group with three dependencies:

| Dependency | Floor | Satisfied by |
| --- | --- | --- |
| `AWSSDK.SimpleSystemsManagement` | 4.0.7.3 | The existing pin, 4.0.104.2 (the newest on nuget.org that day is 4.0.104.5) |
| `AWSSDK.Extensions.NETCore.Setup` | 4.0.3.25 | Not pinned. Transitive, Apache-2.0 by its own nuspec, resolving to 4.0.3.25 unless something asks for more; central package management does not pin transitives here, so no pin is needed |
| `Microsoft.AspNetCore.DataProtection` | 10.0.7 | The shared framework carries 10.0.12, which should win the conflict at build and run time. Unverified without a build; see Blocking |

**No KMS package is needed.** The nuspec names none, and the package's own documentation says why:
`PersistOptions.KMSKeyId` is "the KMS Key ID that you want to use to encrypt a parameter when you
choose the SecureString data type. If you don't specify a key ID, the system uses the default key
associated with your AWS account", and `SSMXmlRepository.StoreElement` "stores the DataProtection
key as parameter in SSM's parameter store. The parameter type will be set to SecureString".
Parameter Store makes the KMS call on the server. The gateway sends `PutParameter` and
`GetParametersByPath`, the two operations the assembly references, through an
`IAmazonSimpleSystemsManagement`. The task role therefore needs no `kms:` grant for the AWS-managed
key, by the reasoning `infra/modules/cell/iam.tf:157-163` records for client secrets, and does
need an explicit one for a customer-managed key, as the same comment warns.

So the pin, under `Label="AWS"`:

```xml
<PackageVersion Include="Amazon.AspNetCore.DataProtection.SSM" Version="4.0.3" />
```

### 2. A `DataProtection` section in `contracts/platform-config.schema.json`

The root allows unknown sections (`contracts/README.md` -> "Using the schemas": the task environment
carries `Logging` and `OTEL_*`), so a new section whose members are all optional is a widening:
every document valid today stays valid. A member required within the section would be a narrowing,
because `DataProtection: {}` validates under the open root today and would not, and a narrowed
schema waits for the next gate. So the section is asked as a closed object with no `required`, and
the code refuses the section without its prefix until a gate adds that rule to the schema. Adding
the section to the root's `required` is not asked either.

| Key | Type | Meaning |
| --- | --- | --- |
| `ParameterPrefix` | string, optional in the schema until the next gate and required by the code, pattern `^/[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*/?$` | The Parameter Store prefix the ring lives under, for example `/spa-platform/poc/data-protection/`. The package expects that "only DataProtection keys will be stored with this prefix", so it is a prefix of its own beside the client secrets' `/spa-platform/{cell}/apps/` |
| `KmsKeyId` | string, optional | The key id, ARN or alias the SecureString parameters are encrypted under. Absent means the account's default `aws/ssm` key, which the package documents and which is the Phase 1 arrangement for client secrets; the plan's "under a KMS key" is met by the cell's own key once workstream 1 creates it |

The application name is not a key: it is `Platform:Cell`, which the schema already promises, set
with `SetApplicationName` as `spa-platform-{cell}` or whatever string the integrator fixes, so two
tasks of one cell agree and two cells never do.

One rule the code enforces now, and the schema may carry as an `if`/`then` only at the next gate,
since it narrows what validates today: **`Session:Store = dynamodb` requires `DataProtection`.**
The DynamoDB store protects its tickets with data protection (change request
`p2-1-dynamodb-session-store.md`, section 6), so a cell with durable sessions and
a per-task ring would write items that no other task, and no restarted task, could read; "killing a
task loses no session" would then fail quietly. With the section absent, the ring stays the
framework's default and the gateway behaves as in Phase 1, which is what every hermetic host and
`tools/smoke.sh` want.

### 3. Which entry point registers it

`contracts/pipeline.md:14-25` assigns six registrations and the request adds no seventh. Two of the
six could carry this one.

| Option | For | Against |
| --- | --- | --- |
| `AddPlatformHosting`, workstream 2 | Hosting owns platform configuration and `Platform:Cell`, the value the application name is made from. Workstream 2 already registers the SSM client (`src/Platform.Registry/PlatformRegistryExtensions.cs:31-35`), owns the Parameter Store conventions and holds the stub that answers as Parameter Store does (`tests/Platform.Registry.Tests/StubParameterStore.cs`), so the hermetic proof extends a double it has. The ring serves three consumers in two workstreams, which is the shape of infrastructure, not of identity. It runs before `AddPlatformIdentity` in the service table, though the order does not matter: `AddDataProtection` is idempotent, and `PersistKeysToAWSSystemsManager` configures `KeyManagementOptions`, read when the first key is needed | `src/Platform.Hosting` gains an AWS package and a reason to know about Parameter Store |
| `AddPlatformIdentity`, workstream 3 | The cookies and the login state the keys protect are workstream 3's, and the acceptance is stated in their terms | The antiforgery tokens are workstream 4's and would depend on a registration inside identity. S8 already puts P2-1 and P2-3 on workstream 3, the critical path. The application name would be set by a workstream that does not own `Platform:Cell` |

**Recommended: `AddPlatformHosting`.** The registration is one call, made only when the section
is present:

```csharp
services.AddDataProtection()
    .SetApplicationName(name)
    .PersistKeysToAWSSystemsManager(prefix, o => o.KMSKeyId = keyId);
```

As far as its assembly's references show, the package
resolves its `IAmazonSimpleSystemsManagement` with `GetService` and otherwise constructs a default
client; whether a client registered ahead of it is the one it uses is listed under Blocking as
unverified. If it is, the stub Parameter Store registered first is the whole test seam, as it is
for the secret source. If it is not, the `PersistKeysToAWSSystemsManager` call gives way to the
same package's repository registered behind the framework's `IXmlRepository`, whose constructor
takes the client explicitly.

### 4. How it is proved hermetically

Two tests in two owners' projects, and a deployed one.

- **Workstream 2, `tests/Platform.Hosting.Tests`.** A host built by `AddPlatformHosting` with the
  section set and a stub SSM client registered first. Protecting one payload writes one
  `SecureString` parameter under the prefix with the configured key id; a second host over the same
  stub unprotects it; with the section absent, the stub sees no request and the two hosts cannot
  read each other's payloads. The last clause is the mutation that lets the first assertion fail.
- **Workstream 3, `tests/Platform.Identity.Tests`.** Two `IdentityGateway`s
  (`tests/Platform.Identity.Tests/IdentityGateway.cs:186-205`) sharing one `IXmlRepository` double
  over a list of `XElement`, which is what the SSM repository is at the framework's seam. A login
  completed on the first presents its `__Host-spa-session` cookie to the second and gets 200 from
  `/bff/session`; a login started on the first, with its correlation and nonce cookies, completes on
  the second's callback. The control is two gateways with two repositories, which must refuse
  both. One hazard to design around: with no repository configured, the framework's default on a
  workstation is a directory under the user's home, which two hosts in one test process share by
  accident, so a naive two-host test passes for the wrong reason. Both gateways set their
  repository explicitly, or the control cannot fail. A `StageHost` as #140 specifies would serve
  the same test once it lands, with #143 deciding its shape.
- **Deployed, at G6.** The acceptance as written, "a cookie and a login state issued by one task
  are accepted by another", needs two tasks (P2-4) and the real parameters under the cell's key. It
  is a browser-suite row in the shape of 6.5 (#61), run against two tasks after the deployed gate
  runs of #63 have closed G5.

### 5. A row in `docs/dependencies.md`

| Package | Version | Licence | Expected in | Note |
| --- | --- | --- | --- | --- |
| Amazon.AspNetCore.DataProtection.SSM | 4.0.3 | Apache-2.0 (the package's LICENSE file; its nuspec names no expression) | `Platform.Hosting` | The shared key ring (P2-2). Confirmed on nuget.org on 2026-10-19. Brings `AWSSDK.Extensions.NETCore.Setup` 4.0.3.25, transitive. Needs no KMS package: Parameter Store encrypts on the server |

### 6. The P2-9 link

With the ring in Parameter Store, nothing in the gateway writes to disk: the framework's default
key repository is the one file writer the process has. The other reason the task's root filesystem
is writable today is the one `docs/plan/05-infrastructure-and-cost.md:79` names for ECS Exec,
which P2-9 also removes. P2-2 is therefore a prerequisite of P2-9's read-only root filesystem and
should be listed as one on the Phase 2 spec issue (to be filed).

## What workstream 2 can do meanwhile

Nothing that lands, for the reason the P2-1 request gives: the project cannot reference the package
until it is pinned. The `IXmlRepository` double and the two-host identity test of section 4 depend
on no package and could be written against the framework alone, but they prove the framework, not
the platform, until the registration exists.

## Why this is a request rather than an edit

`Directory.Packages.props`, `contracts/` and `docs/dependencies.md` are the integrator's (AGENTS.md,
OWNERSHIP.md); the schema and the service table froze at G1a (`contracts/README.md`).

## Blocking

Items 1 and 2 block everything; item 3 decides who writes it, and the Phase 2 spec issue (to be
filed) cannot assign the ticket until it is decided. Unverified without a build, and to be checked
in the first session that has one: that the package takes a registered
`IAmazonSimpleSystemsManagement` ahead of constructing its own; that the 10.0.7 floor on
`Microsoft.AspNetCore.DataProtection` restores cleanly beside the 10.0.12 shared framework; and
that a data-protection key element fits a standard-tier parameter, since
`TierStorageMode.StandardOnly`, the package's default, refuses to exceed it.
