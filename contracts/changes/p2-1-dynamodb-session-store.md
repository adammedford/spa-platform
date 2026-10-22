# Change request P2-1: a pin, a table name and a value-comparing suite for the DynamoDB store

- **Raised by:** the Phase 2 planning session of 2026-10-19, on behalf of workstream 3 (OAuth and
  sessions)
- **Tasks:** P2-1 (the durable store); P2-2, whose key ring the store's ticket encryption rests on;
  P2-3, whose lease the store's conditional writes can carry; MR-9, whose format version the item
  carries
- **Date:** 2026-10-19
- **Status:** decided, 2026-10-21, on https://github.com/adammedford/spa-platform/issues/221. See the
  Decision section below.

## The gap

Plan row P2-1 (`docs/plan/06-phase-2-and-risks.md`): "`ISessionStore` on DynamoDB. The key is a
hash of the session key, the ticket is envelope-encrypted, expiry uses the table's time-to-live, and
replace is versioned", accepted when "the store contract suite passes" and "killing a task loses no
session".

What is in place. `ISessionStore` and `SessionRecord` froze at G1b, with `ReplaceAsync`'s expected
version put there because "the Phase 2 store needs it for optimistic concurrency, and having it now
means Phase 2 changes no caller" (`src/Platform.Abstractions/ISessionStore.cs:36-41`).
`Session:Store` already admits `dynamodb` (`contracts/platform-config.schema.json:134`), and
`MemorySessionStore.FromConfiguration` refuses it today with "This gateway has only the 'memory'
store" (`src/Platform.Identity/MemorySessionStore.cs:48-57`), which
`tests/Platform.Identity.Tests/SessionStoreRegistrationTests.cs:57-71` proves. Infrastructure
anticipates the enum and nothing more: `session_store` in `infra/modules/cell/variables.tf:306`
reads it, and P-01's test names `aws_dynamodb_table` as a Phase 1 tripwire
(`infra/envs/poc/platform/tests/root.tftest.hcl:807-809`).

Four things the store needs sit in integrator paths. Items 1 to 4 ask; items 5 and 6 inform.

## The change

### 1. Pin `AWSSDK.DynamoDBv2` 4.0.108

`Directory.Packages.props` gains, under `Label="AWS"`:

```xml
<PackageVersion Include="AWSSDK.DynamoDBv2" Version="4.0.108" />
```

Confirmed on nuget.org on 2026-10-19: 4.0.108 is the newest of the 1,475 versions listed,
Apache-2.0 by its nuspec's licence expression, with one dependency,
`AWSSDK.Core [4.0.102.12, 5.0.0)`. The two AWS packages already pinned ask for
`AWSSDK.Core [4.0.100.9, 5.0.0)` (`AWSSDK.S3` 4.0.101.6) and `[4.0.102.9, 5.0.0)`
(`AWSSDK.SimpleSystemsManagement` 4.0.104.2), read from their nuspecs the same day. The three
ranges overlap. `AWSSDK.Core` is transitive and not pinned, and `Directory.Build.props` does not
turn on transitive pinning, so NuGet resolves it per project graph, to the lowest version every
package in that graph accepts. A graph that carries `AWSSDK.DynamoDBv2` resolves to 4.0.102.12:
`Platform.Identity`, and the gateway, which references every workstream project. A graph that
carries only the S3 and Parameter Store packages stays at 4.0.102.9: `Platform.Registry` and
`spactl`. Nothing conflicts, and nothing moves outside the graphs that take the new package.

Only `Platform.Identity` would reference it, with `<PackageReference Include="AWSSDK.DynamoDBv2" />`
and no version, as AGENTS.md says. A-03 is untouched, because a package is not a workstream
project, and A-04 restricts the token-management package only.

### 2. A table name under `Session` in `contracts/platform-config.schema.json`

The `Session` object is `additionalProperties: false` with `Store` and `MaxSessions` required
(`contracts/platform-config.schema.json:127-140`), so the table's name cannot be read from
configuration until the schema admits it. Asked:

- **`TableName`**, optional, `type: string`, pattern `^[A-Za-z0-9_.-]{3,255}$`, which is
  DynamoDB's table naming rule
  (https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/HowItWorks.NamingRulesDataTypes.html),
  described as "The session table. Required when `Store` is `dynamodb`; ignored otherwise."
- **The requirement itself** stays in the code until the next gate: the DynamoDB store's
  `FromConfiguration` refuses a missing name as `MemorySessionStore`'s refuses a missing bound
  today. A schema `if`/`then` on `Store = dynamodb` would say the same, and it narrows the schema,
  so it waits for the next gate (below); this request records it for that gate and asks for the
  property alone now.

A flat key, `Session:TableName`, rather than a `Session:DynamoDb:TableName` sub-object, because
every section the schema owns is flat today (`Registry:Bucket`, `Assets:Bucket`,
`Session:MaxSessions`) and one key does not need a level. No `ServiceUrl` override is asked for:
the store takes its `IAmazonDynamoDB` from the container, registered with `TryAddSingleton` so a
host that brings its own client first wins, which is how `AddPlatformRegistry` registers
`IAmazonS3` and `IAmazonSimpleSystemsManagement`
(`src/Platform.Registry/PlatformRegistryExtensions.cs:31-36`) and how the S3 tests point the SDK
at `FakeS3Server`
(`tests/Platform.Registry.Tests/FakeS3Server.cs:61-67`). The fake's endpoint never reaches
configuration.

`MaxSessions` stays required and the DynamoDB store does not read it. Its description already says
"The bound on the in-memory store". Dropping it from `required` would widen the schema, a change
the integrator may land between gates; it is not asked here, because the memory store needs it and
the DynamoDB store ignores it either way.

**Whether this lands between gates.** `contracts/README.md` -> "What may land between gates" lets
prose and a new type that changes no existing signature land on the integrator's decision, and
holds "a narrowed schema" for the next gate. The optional property is a widening: every document
valid today stays valid, so it lands now. The `if`/`then` is a narrowing: a document that says
`Store = dynamodb` and names no table validates today and would not. That no such document can run
(the gateway refuses `dynamodb` at the first session, `MemorySessionStore.cs:52-57`) does not
change what the rule holds for, so the `if`/`then` waits for the next gate, and until then the
store's `FromConfiguration` is the one place the requirement lives.

### 3. A row in `docs/dependencies.md`

Under "NuGet packages", after `AWSSDK.SimpleSystemsManagement`:

| Package | Version | Licence | Expected in | Note |
| --- | --- | --- | --- | --- |
| AWSSDK.DynamoDBv2 | 4.0.108 | Apache-2.0 | `Platform.Identity` | The Phase 2 session store (P2-1). Confirmed on nuget.org on 2026-10-19. Lifts the transitive `AWSSDK.Core` to 4.0.102.12 in the graphs that carry it, `Platform.Identity` and the gateway. |

### 4. The contract suite's first assertion must compare by value

`SessionStoreContract.A_created_session_reads_back_under_the_key_it_returned` asserts
`Assert.Equal(record, await store.GetAsync(key, Ct))`
(`src/Platform.Testing/SessionStoreContract.cs:21-29`). `SessionRecord` is a record whose `Ticket`
is a `ReadOnlyMemory<byte>`, and `ReadOnlyMemory<T>.Equals` is true only for the same backing
object, offset and length (https://learn.microsoft.com/dotnet/api/system.readonlymemory-1.equals).
Both in-memory stores pass because they hand back the instance they were given
(`src/Platform.Testing/InMemorySessionStore.cs:22`,
`src/Platform.Identity/MemorySessionStore.cs:97-99`). A store that serializes the record cannot:
its ticket comes back in a new array, equal in content and unequal to the comparer. The suite's own
last test already compares the bytes with `ToArray()` (`SessionStoreContract.cs:140-152`), so the
intent is value equality; only the first test says otherwise.

Asked: change that one assertion to compare the six scalar members and the ticket bytes by value.
The suite is the integrator's, in `src/Platform.Testing`. It changes no interface and no double's
behaviour, so it lands between gates under the README's rule, and the existing subclass in
`tests/Platform.Identity.Tests/MemorySessionStoreContractTests.cs` keeps passing. Without it the
DynamoDB store cannot meet "the store contract suite passes" as written, whatever it does.

One more condition of the suite is worth naming, since a serializing store meets it only on purpose:
the two `DateTimeOffset` members must round-trip exactly, which means storing them as ISO-8601 in
the round-trip format or as ticks, not as the whole-second number the table's time-to-live wants.
The item in section 6 keeps both.

### 5. How the store is proved hermetically

The plan's pattern is `S3ObjectStore`: proved in its owner's test project against a fake the SDK
talks to over HTTP, and meeting a real bucket for the first time in task 1.7 (plan row 2.2,
`docs/plan/03-workstreams-and-tasks.md:109`). Three ways to do that for DynamoDB, weighed.

**(a) A fake DynamoDB HTTP server in `tests/Platform.Identity.Tests`, the `FakeS3Server`
precedent.** `FakeS3Server` is 173 lines: a `WebApplication` on a loopback port, GET and PUT with
`If-None-Match` and `If-Match`, S3's XML error body, and a client built with `ServiceURL` and
anonymous credentials (`tests/Platform.Registry.Tests/FakeS3Server.cs:16-25, 61-67`). A DynamoDB
fake speaks the JSON 1.0 protocol instead: every call is `POST /` with
`Content-Type: application/x-amz-json-1.0` and `X-Amz-Target: DynamoDB_20120810.<Operation>`, and
an error is a 400 whose body names `__type`, such as
`com.amazonaws.dynamodb.v20120810#ConditionalCheckFailedException`
(https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Programming.LowLevelAPI.html).
The fake needs exactly the operations and conditions the store sends:

| Store method | Operation | Condition, and why |
| --- | --- | --- |
| `CreateAsync` | `PutItem` | `attribute_not_exists(pk)`: a key the CSPRNG already handed out fails loudly rather than overwriting |
| `GetAsync` | `GetItem` with `ConsistentRead: true` | By `pk`. A default read is eventually consistent (https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/HowItWorks.ReadConsistency.html); the re-read before a refusal (T-B8) and "ended on one task, refused by the other" (P2-3) need the write that just happened. The fake refuses a read without the flag. |
| `ReplaceAsync` | `PutItem` (or `UpdateItem`) | `attribute_exists(pk) AND version = :expected`. `ConditionalCheckFailedException` reads as `false`, as the S3 store reads 412 as a refused write |
| `RemoveAsync` | `DeleteItem` | Unconditional. A missing key succeeds, which is the contract's "not an error" |
| `RemoveAppAsync` | `Query` on a global secondary index keyed by `appId`, then `DeleteItem` per key, or `BatchWriteItem` 25 at a time | An index query is eventually consistent, so a session created in the same second may be missed. `AppBoundTicketStore.Owned` already treats a record bound to a client the app has left as absent (`src/Platform.Identity/AppBoundTicketStore.cs:107-112`), so the purge is hygiene and correctness does not rest on it |
| Expiry | None: `expiresAt` is the table's time-to-live attribute | The fake has no sweeper, and DynamoDB's is slow: expired items are typically deleted within a few days and can be read until then (https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/TTL.html). So the store reads `expiresAt <= now` as absent, one rule for read and replace, as `MemorySessionStore.Live` does (`MemorySessionStore.cs:202-222`). A test that moves the clock past expiry proves it against the fake and the real table alike |

What the fake must evaluate in C# is the condition expressions above and nothing else, and that is
its honest limit: it proves that the store sends each operation with the right target, keys,
attributes and condition, and that the SDK turns a 400 `ConditionalCheckFailedException` into the
exception the store maps to `false`; it proves the expressions' meaning only as far as the
evaluator's author read the documentation. The real table at the G6 deployed run proves the rest,
as the real bucket proves the S3 store at task 1.7. The two-instance test of P2-3 reuses the same
fake with two stores over it.

**(b) DynamoDB Local in a container.** Rejected for the suite. Decision D-2 in
`docs/dependencies.md` puts Docker Desktop on every workstation, so a session can run DynamoDB
Local; the suite still cannot depend on it, because `CODING_STANDARDS.md` -> "An assertion must be
able to fail" forbids "a test that skips on the machine it runs on", CI's build job runs no
container, and a test that needs one either fails where the engine is absent or checks nothing
there. It stays a tool for a session that wants to try the real expression grammar before G6, as
D-2 records, not a check.

**(c) A stub `IAmazonDynamoDB`.** `StubParameterStore` is the precedent: 76 lines subclassing the
SDK client and overriding one virtual operation
(`tests/Platform.Registry.Tests/StubParameterStore.cs:10-13`). For DynamoDB this is weaker than (a)
by exactly the serialization and the error mapping: the store is then proved against request
objects the test built the expectations of, and never sends a byte.
The condition-expression evaluator is the same work in either shape. Acceptable if (a) proves too
costly; not the recommendation.

**Recommendation: (a)**, with the fake in `tests/Platform.Identity.Tests`, workstream 3's own path,
so no integrator path is involved in the store's own proof. One question the integrator should
answer now rather than later. Workstream 6's composed harness references `Gateway`,
`Platform.Abstractions`, `Platform.Testing`, `TestIdp` and `MockApi` and no `tests/*.Tests` project
(`tests/Gateway.TestHarness/Gateway.TestHarness.csproj:7-11`), so a fake in workstream 3's test
project is out of its reach. If the two-task composed proof for P2-3 is to run over the real
DynamoDB store rather than over the `Platform.Testing` doubles, the fake has to live in
`src/Platform.Testing`, which is the integrator's. Change request
`p2-3-session-lease-abstraction.md` recommends the doubles route, under which the fake stays where
it is; if the integrator prefers the composed suite to exercise the real store, this request also
asks for the fake's home to be `src/Platform.Testing`, written by workstream 3 and landed by the
integrator.

### 6. The item, for the integrator's information

Nothing here is asked for. It is written down so that the schema key, the key ring and the lease
are read against one layout.

| Attribute | Type | Holds |
| --- | --- | --- |
| `pk` | S | Lowercase hex SHA-256 of the session key's UTF-8 bytes, 64 characters. The row says "the key is a hash of the session key"; the key itself is a credential (`contracts/telemetry.md:16`) and the table never holds it. The first 12 characters equal `session_ref` by construction (`SessionRef.Of`), so a supporter can match a log line to a row without either being a credential |
| `appId`, `clientId`, `subject` | S | As the record; `appId` is also the index's hash key |
| `createdAt`, `recordExpiresAt` | S | ISO-8601, round-trip format, so the record reads back equal (item 4) |
| `expiresAt` | N | Epoch seconds of `ExpiresAt`, the table's time-to-live attribute |
| `version` | N | `SessionRecord.Version`, the conditional write's subject |
| `ticket` | B | The serialized ticket, protected by the framework's data protection under a purpose of the store's own, `Platform.Identity.SessionTicket` and the app id. With a `SessionStore` set, the cookie handler's ticket format protects only the cookie; the bytes `TicketSerializer` produces (`AppBoundTicketStore.cs:105`) are plaintext and hold the tokens, which was harmless in process memory and is not harmless in a table. Data protection is the envelope the row names, and it ties P2-1 to P2-2: a task can read another task's items only when both share the key ring. Hermetically any provider serves; in the cell, "killing a task loses no session" is true only once P2-2 has landed |
| `format` | N | The item's shape version, `1`, for MR-9 (`docs/roadmap.md:390-395`): a build reads its own format and the one before it, and reads an unknown newer format as absent, so a rolled-back image logs once and asks the browser to log in again rather than failing per request. On the item rather than on `SessionRecord`, so the frozen record is untouched |
| `leaseUntil`, `leaseToken` | N, S | Present only while P2-3's refresh lease is held; see that request. Outside the record, so `GetAsync` never returns them |

Two consequences of the layout. The store keeps no in-process cache of any kind: "a session ended
through one task is refused by the other on its next request" (P2-3) is true only because every
`GetAsync` reads the table, and the cost line in `docs/plan/05-infrastructure-and-cost.md:151`
already assumes "a read on every request". And the request's cancellation token must not drop a
save (T-B17, `docs/plan/07-evidence.md:204`): `AccessTokenSource` already passes
`CancellationToken.None` to the store, but the cookie handler's renew through
`AppBoundTicketStore.RenewAsync` passes the request's token (`AppBoundTicketStore.cs:59-66`), so the
store should treat a cancellation that arrives after the write was sent as a completed write. How
the SDK behaves there is something the fake can show.

## What workstream 3 can do meanwhile

Nothing that lands. `Platform.Identity` cannot reference `AWSSDK.DynamoDBv2` until item 1 pins it,
so the store, the fake and the suite subclass cannot compile on a branch CI would accept, and A-05
refuses a branch that pins it itself. Item 4 is the same shape: a branch could add the DynamoDB
subclass of the suite, and it would fail. The design, the fake's protocol surface and the item
layout can be written down against this request, which is what the Phase 2 spec issue (to be filed)
will carry.

## Why this is a request rather than an edit

`Directory.Packages.props`, `contracts/`, `docs/dependencies.md` and `src/Platform.Testing` are the
integrator's (AGENTS.md, OWNERSHIP.md). `platform-config.schema.json` froze at G1a, and the store's
contract suite shares the G1b freeze with the record it tests (`contracts/README.md` -> "What G1b
froze"), so each change is a written request the integrator decides.

## Blocking

Every part of P2-1 waits on items 1 and 4. Items 2 and 3 can follow in the same decision; without
item 2 the store can be built and proved against the fake but cannot be configured in a cell. The
deployed half of the acceptance, "killing a task loses no session", also waits on the table and
the task role in `infra/` (workstream 1), on P2-2, and on the deployed gate runs of #63 reaching G5,
since G6 opens after it.

## Decision

Integrator, 2026-10-21, on Adam's answers to the nine questions of
https://github.com/adammedford/spa-platform/issues/221 (all accepted as recommended). This decides
items 1 to 5, places the refresh lease that P2-3's Decision item 2 left to this request, and records
item 6.

**Two landings.** This decision record lands now and alone. The four frozen-artifact edits (the pin
and its comment, the `docs/dependencies.md` row and its preface, the `TableName` property and its
contracts cases, the `SessionStoreContract` change) are one integrator ticket on the Phase 2 spec
issue. Workstream 3's store ticket is blocked by it, because nothing compiles without the pin.

1. **Pin `AWSSDK.DynamoDBv2` 4.0.108** under `Label="AWS"`: the version the trial restore of
   https://github.com/adammedford/spa-platform/issues/216 built, not the newest. The pin carries a
   comment in the `AWSSDK.Signin` style (`Directory.Packages.props:28-31`) naming why this version,
   its `AWSSDK.Core` floor `[4.0.102.12, 5.0.0)`, and the date it was confirmed on nuget.org.
   `Platform.Identity.csproj` gains the versionless reference. `tests/Platform.Identity.Tests` needs
   none of its own: the package flows transitively, as `AWSSDK.S3` reaches `Platform.Registry.Tests`.
2. **The `docs/dependencies.md` row** (item 3) is as written, placed after the `AWSSDK.Signin` row.
   The table's preface, "the three AWS SDK packages" (`docs/dependencies.md:44`), says four.
3. **`Session:TableName`** (item 2) is as asked: optional, flat, with the request's pattern and
   description. `Session` is already a closed object, so adding a property is a widening and lands
   between gates. The `if`/`then` requirement waits for the next gate with the other deferred
   narrowings, and the store's `FromConfiguration` enforces it until then. There is no `ServiceUrl`
   key: the store takes `IAmazonDynamoDB` from the container with `TryAddSingleton`, as
   `AddPlatformRegistry` does. The integrator's ticket adds an accepted name and a refused one (two
   characters, or one carrying a `/`) beside `Session_store_is_memory_in_phase_1_and_dynamodb_in_phase_2`
   (`tests/Contracts.Tests/PlatformConfigurationTests.cs:135-140`). `MaxSessions` stays required,
   with its description unchanged.
4. **The assertion change lands between gates** (item 4). `contracts/README.md` -> "What may land
   between gates" admits prose and a new type, and a corrected assertion in a suite under the G1b
   freeze is neither. It meets the rule's reason instead: nothing built against the frozen contract
   has to change. Every store that passes today's `Assert.Equal(record, ...)`
   (`src/Platform.Testing/SessionStoreContract.cs:28`) also passes a value comparison, because
   today's passes only when the ticket is the same backing array. The README is not amended, since
   one case is not yet a pattern.
5. **The assertion's shape.** `Assert.NotNull(stored)`, then `record with { Ticket = default }`
   equal to `stored with { Ticket = default }`, and the ticket bytes compared as arrays. The six
   scalars compare in one statement, so a seventh added at a later gate is covered. Timestamps
   compare by instant: `DateTimeOffset.Equals` ignores the offset, so item 4's "must round-trip
   exactly" overstates what the suite checks, and a store may hand back UTC. The first test's record
   carries sub-second ticks, so a store that truncates to seconds fails it. The shared `Record()`
   keeps its whole-second times (`SessionStoreContract.cs:164-165`), which alone could not fail such
   a store (`CODING_STANDARDS.md` -> "An assertion must be able to fail").
6. **The fake is option (a)** (item 5): a JSON 1.0 DynamoDB HTTP server in
   `tests/Platform.Identity.Tests`, whose home P2-3's Decision item 7 settled. Its surface is item 5's
   operations table and item 7's lease conditions, and nothing else. Only (a) shows what the SDK does
   when cancellation fires after a request is sent. Only (a) proves the attribute serialization,
   `ConsistentRead` as sent, and the mapping of a 400 `ConditionalCheckFailedException`. The SDK
   checks a response CRC only when the `x-amz-crc32` header is present, so the fake omits it, and the
   fake's first test confirms that at the pin. Its honest limit stands as item 5 words it, and the
   real table at G6 proves the rest.
7. **The refresh lease lives on a lease item of its own.** Its `pk` is `lease#` followed by the
   session key's SHA-256 hex, which no 64-character session `pk` can equal. Acquire is a `PutItem`
   under `attribute_not_exists(pk) OR leaseUntil < :now`. Release is a `DeleteItem` under
   `leaseToken = :token`, where a failed condition is the stale release P2-3's Decision item 5
   swallows. `leaseUntil` is in epoch milliseconds, because a 15 s lease compared in seconds loses up
   to a second. `expiresAt` is set to the lease's expiry in seconds, so the table's time to live
   sweeps a lease whose holder died. The item carries no `appId`, so the `appId` index never projects
   it and `RemoveAppAsync` never sees it. `ReplaceAsync` therefore stays `PutItem`, `RemoveAsync`
   stays unconditional, and no read rule is added. On the session item, a `PutItem` would erase a
   held lease, a `DeleteItem` would free it, and a lease on a key with no record would create a
   ticketless item. Each lease write is one write unit. The fake's evaluator gains `OR`, `<` on `N`,
   and the `DeleteItem` condition.
8. **Item 6 is recorded, not decided.** It is the layout workstream 3 builds against, except its
   `leaseUntil`/`leaseToken` row, which item 7 moves to the lease item: `pk`, `leaseUntil` (N,
   milliseconds), `leaseToken` (S) and `expiresAt` (N). The `expiresAt <= now` read rule and the
   behaviour of a cancellation after the write was sent are left to the store ticket, as facts the
   fake shows. The session item keeps `appId` and `subject` as attributes. Back-channel logout
   (`p2-6-pingfederate-profile.md` item 7, decided on
   https://github.com/adammedford/spa-platform/issues/224) adds an index on that pair at G7, and
   DynamoDB can add a global secondary index to a live table and backfill it
   (https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/GSI.OnlineOps.html), so no item
   is rewritten then. The store does not implement the session index at G6.
9. **No ADR and no glossary entry.** The pin, the optional key and the assertion fix are routine.
   The lease placement is internal to the store and cheap to change, because a lease lives 15 s and
   moving it migrates no data; the contract it implements already has ADR-0024. Session record,
   Session key, session_ref and Refresh lease already read true against the layout.

Corrections this record makes to the request, against `main` at `b313c14`:

- Item 1 says 4.0.108 is "the newest of the 1,475 versions listed" (line 42). 4.0.109
  (2026-10-20) and 4.0.109.1 (2026-10-21) have since been published, the second with an
  `AWSSDK.Core` floor of 4.0.102.13. The pin is the version the trial restore proved.
- Item 1 counts "the two AWS packages already pinned" (line 44). There are three, since
  `AWSSDK.Signin` 4.0.101.13 (`Directory.Packages.props:32`, #206). Its Core floor is 4.0.102.9, so
  the graph conclusions stand.
- Item 2 cites the `Session` object as `contracts/platform-config.schema.json:127-140` (line 61). It
  is `:127-143`.
- Item 5's table cites `AppBoundTicketStore.Owned` at `:107-112` (line 155). No such member exists,
  and the file is 101 lines. The ownership check that makes the purge hygiene is
  `AppSessions.OwnedBy` (`src/Platform.Identity/AppSessions.cs:194-199`).
- Item 6 cites `TicketSerializer` at `AppBoundTicketStore.cs:105` (line 207). It serializes at `:97`
  and deserializes at `:76`.
- Item 6 says `AppBoundTicketStore.RenewAsync` passes the request's token to the store (line 217).
  It throws `NotSupportedException` (`AppBoundTicketStore.cs:70-71`, refused under #141). T-B17 still
  holds for the Phase 2 store, through the paths that do pass the request's token: create
  (`AppBoundTicketStore.cs:54`, then `AppSessions.cs:96`), read (`AppSessions.cs:86`) and remove
  (`AppSessions.cs:140`). Every store call under the session gate passes `CancellationToken.None`
  (`AppSessions.cs:64, 69, 290, 322`). So "a cancellation after the write was sent" concerns a
  create and a remove, never a replace.

What this decides for the spec issue: one integrator ticket with the four frozen-artifact edits,
blocking workstream 3's store ticket; workstream 3's store ticket, which carries the fake, the
lease item of item 7 and the two facts of item 8; and, for workstream 1's row on the map, that the
table's `expiresAt` time to live sweeps lease items as well as sessions.
