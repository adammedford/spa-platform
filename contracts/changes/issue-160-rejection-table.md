# Change request issue-160: `Rejection` and `Problems` in `Platform.Abstractions`, the `code` column, and a new ADR

- **Raised by:** the integrator, for the `rejection-table` spec #142, as its first ticket #159
- **Tasks:** none in the plan. The tickets of #142: #160 (the expand, which this request is named
  for), #161 (workstream 2's migrate), #162 (workstream 4's migrate, both its projects) and #163
  (workstream 3's migrate). #158, under #139, extends workstream 6's architecture rule to the codes
  once #160 lands.
- **Date:** 2026-10-20
- **Status:** decided, 2026-10-20. See the Decision section below.

## What is missing

Three contracts meet at every rejection: the rule id in `contracts/request-gate.md` → Rule ids; the
`rule` field of the request line and the tag of `security.rejections` in `contracts/telemetry.md`;
and the status and stable `code` of the problem body in `contracts/http-contract.md` → Errors.
Nothing in production holds the join between them. Each stage that ends a rejection writes it by
hand: its own Warning line, the null-conditional write of the rule id to
`IRequestTelemetryFeature`, and a problem body whose status and code it spells itself.

- **Four copies of one writer.** `src/Platform.Hosting/Problems.cs`, `src/Platform.Security/Problems.cs`,
  `src/Platform.Proxy/Problems.cs` and `src/Platform.Identity/Problems.cs`, 107 lines between them,
  write the same two fields. A-03 keeps a workstream project from referencing another's, so each
  owner keeps one. Three reviews found the duplication and set it aside as A-03's fault (archived
  overnight tickets 13, 22 and 31), so it has been seen three times and never recorded as a decision.
- **The join spelled at the site.** `src/Platform.Security/RequestGate.cs` pairs each rule id with
  its status and code as literals (lines 71, 76, 85, 99, 104, 117 and 325), and so do the method
  allowlist, antiforgery and the unknown-api endpoint. Three gate ids (`gate.fetch-site`,
  `gate.origin`, `gate.navigation`) share one Errors row, `403 origin_rejected`, and only the rows'
  wording says so, so a guard can only pair by hand. No production type maps a rule id to its status
  and code, so no test can hold one to the contract.
- **The copies have diverged.** The asset endpoint's backup 405 (`src/Platform.Hosting/Assets.cs:66-68`)
  carries no rule id, though the rule-id table gives its case `gate.method`. The unknown-api endpoint
  logs through an ad hoc logger (`src/Platform.Proxy/UnknownApi.cs:25-27`). Platform.Proxy's writer
  says task 4.6 owns problem-details errors for the gateway as a whole
  (`src/Platform.Proxy/Problems.cs:10`); task 4.6 declined that, with no ADR. The OpenID Connect
  events' 401 says "Not a rejection, so no rule id"
  (`src/Platform.Identity/AuthenticationEvents.cs:50`), which is false for a dead cookie:
  `UsePlatformShellGate` notes `auth.session` before the challenge (`src/Platform.Identity/ShellGate.cs:50`).
- **The documents trail the code.** `contracts/request-gate.md` says nothing writes `auth.callback`
  or `auth.session` yet; `Bff.NoteDeadSession` writes `auth.session` (`src/Platform.Identity/Bff.cs:141-145`,
  called from `/bff/session`, `UsePlatformShellGate` and `AccessTokenSource`), and the remote-failure
  event writes `auth.callback` (`src/Platform.Identity/AuthenticationEvents.cs:69`). `GLOSSARY.md`'s
  Rejection entry lists "a 400, 403, 404, 405 or a 401", while a shell-gate test asserts
  `auth.session` on a 302.

No workstream can fix this for the others, because every other copy is another owner's, and the
join belongs in the one project all four reach.

## The change

`src/Platform.Abstractions` gains two public static types in its one namespace, beside `RuleIds`,
`IRequestTelemetryFeature` and `SessionRef`, built and proven by #160. The interface below is #142's
Implementation Decisions, as its grilling record (Q1 to Q20, on #142) and the decisions shared with
the other three specs (S1 to S8, on #142) settled it. Each part carries its reason.

### 1. `Problems`, the problem body

- `const string InvalidRequest, Unauthenticated, SessionEnded, OriginRejected, CsrfFailed, NotFound,
  UnknownApi, MethodNotAllowed, UpstreamUnavailable, IdpUnavailable, UpstreamTimeout`: the eleven
  codes of `contracts/http-contract.md` → Errors.
- `ImmutableArray<(int Status, string Code)> Rows`: the Errors table's rows. Order is not part of the
  interface.
- `int StatusOf(string code)`: `ArgumentException` for a code not in `Rows`.
- `Task WriteAsync(HttpContext context, string code)`: the status from the table,
  `Content-Type: application/problem+json`, a body whose properties are exactly `status` and `code`,
  the code JSON-encoded, cancelled on the request's abort token.
- The response has not started: the caller's invariant, guarded where only it can know it (the
  upstream-failure writer checks before writing). Required configuration: none.

*Why the constants.* `unauthenticated` is spelled today by workstreams 3 and 4, and `not_found` by
the request gate and the asset endpoint. A value the contracts fix, which more than one workstream
must spell identically, is ADR-0015 decision 1's case exactly (#142 Q16).

*Why the code alone.* Every code in the Errors table has exactly one status (eleven rows, eleven
distinct codes), so code → status is a function, and a code cannot then be written with the wrong
status. The upstream-failure writer already chooses its code from the status YARP set
(`src/Platform.Proxy/UpstreamFailure.cs:37-38`), so the derived status is YARP's own. The question
reopens the day the contract lists one code under two statuses, and the two-table test fails loudly
on that day (#142 Q7).

*Why tuples, unordered.* A tuple array keeps nested public types off `PublicSurfaceTests`. Nothing
walks `Rows` in order, unlike `RuleIds.All`, so a cosmetic reorder of the contract must not fail a
test; the two-table test asserts sets.

*Why the answers' writer moves too.* The rejection body is the same two fields, so the writer must be
in `Platform.Abstractions` anyway; leaving the answers (the proxy's 401s, 502, 503 and 504, the absent
asset's 404) on per-project copies keeps three copies of what the module holds (#142 Q15).

### 2. `Rejection`, ending a request under a rule id

- `ImmutableArray<(string Rule, string Code)> Rows`: the `code` column of
  `contracts/request-gate.md` → Rule ids, eleven rows: every id but `auth.callback`, with
  `auth.session` → `unauthenticated`, the fetch's body. Order not asserted: `RuleIds.All` already fixes
  the order consumers see.
- `void Note(HttpContext context, string rule)`: the null-conditional write of `rule` to
  `IRequestTelemetryFeature`, and no response. For a rejection whose response another writer ends.
- `Task EndAsync(HttpContext context, string rule)`: `Note`, then `Problems.WriteAsync` with the
  row's code.
- `Task EndMethodAsync(HttpContext context, params string[] allowed)`: `Allow` set to the joined,
  non-empty list, then ends under `gate.method`.

*Why keyed by rule id.* Ten of the twelve ids map to one (status, code) pair at every site, and the
pair is contract-fixed and spelled by four workstreams: ADR-0015 decision 1's condition again. A stage
then passes the rule id and nothing else (#142 Q5). The two that do not map, `auth.callback` and
`auth.session`, go through `Note`.

*Why `Note`.* `auth.session` is noted before a challenge (the shell gate's 302) or a 401 that
Platform.Identity writes, and `auth.callback` before the static failure page. Those writers know the
case and keep it; the module only counts it. `Bff.NoteDeadSession` is already that entry and becomes
a call to `Note` in #163, and the dead-cookie decision, which reads the framework's authentication
result, stays in Platform.Identity (#142 Q11).

*Why `EndMethodAsync`.* `Allow` is the caller's knowledge: the request gate names the path's methods,
the method allowlist all seven, the asset endpoint `GET, HEAD`. The one entry that ends a 405 is the
one that cannot ship it without the `Allow` header RFC 9110 section 15.5.6 requires (#142 Q13).

### 3. The two throws

`EndAsync(context, RuleIds.GateMethod)` throws `InvalidOperationException` naming `EndMethodAsync`.
`EndAsync(context, RuleIds.AuthCallback)` throws `InvalidOperationException` saying the page is the
remote-failure event's. An id not in `RuleIds.All` is an `ArgumentException`.

*Why throw here, when change request 6.4 refused a throwing setter.* 6.4 refused a write-once setter
because it would turn a runtime logging mistake into a 500 on whichever request happened to trip it.
Both throws here are deterministic per call site: a stage that calls `EndAsync` with either id throws
on every request that reaches that line, so the stage's own tests find it before a client does
(#142 Q13).

### 4. The ordering

The feature is written before the body, as every site does today and as the stage hosts' recording
of the feature requires (change request issue-145, section 3: a test reading the recorded feature must
not race the body back to the client). At most one `End*` per request; nothing enforces it, for
change request 6.4's reason.

### 5. Never logs, no dependency injection, no state but the two tables

The module writes the feature and the body and nothing else. The Warning line stays the caller's.

*Why.* Change request 6.4 decided the lines are their owners': `UsePlatformHostResolution` is silent
on purpose, because unknown hosts are the cheapest traffic for anyone to send; the texts and
categories are pinned by the asset tests and the composed method tests; and the stage host's
`LinesFor(rule)` reads lines by the `Rule` state property, so its query survives any template (S1).
The cost is honest: a site is two lines, log then end, not one call. Platform.Proxy's unknown-api
endpoint gains a `LoggerMessage` template in #162, replacing the ad hoc logger call.

There is one implementation and no adapter at the module's own seam, so it has no interface and no
dependency injection: a static table and a writer, varied by nothing. The seam it uses,
`IRequestTelemetryFeature`, has two adapters already, the gateway's in `Platform.Telemetry` and
`SettableRequestTelemetryFeature` in `Platform.Testing`, which the module's tests use.

*Why the feature write is inside.* After the migrates no stage writes the feature itself, so "a
rejection is unchanged with no feature installed" is proved once, in the module's own
`DefaultHttpContext` test, and six per-stage copies of that proof retire in their owners' tickets.
The five "a passed request leaves `Rule` null" proofs stay: the module cannot prove that a stage does
not call it (S2).

## Alternatives rejected

| Option | Why not |
| --- | --- |
| A new integrator project for the two types | A `SpaPlatform.slnx` change and a second project reference in every workstream project, for about eighty lines. `Platform.Abstractions` already carries the ASP.NET Core framework reference, so `HttpContext` is in reach, and one implementation of a contract-fixed formula, `SessionRef.Of` (#142 Q3). |
| `Platform.Testing` | Production code would name a type in a test-support project, and a `src/` project would reference test code: ADR-0015's own row against putting the rule ids there. |
| A shared source file compiled into the four projects | A coupling no project reference shows, in nobody's path, so no owner's review sees a change to it and no architecture rule can name it. |
| The status quo | Four copies agreeing with the contract by care, three reviews suppressing the duplication as A-03's fault, and no decision record, so the next survey re-files it. |

## Decisions shared with the other specs

- **#139, the contract reader.** S5: the readers do not share. #160's two-table test keeps its own
  parser in `tests/Platform.Abstractions.Tests`, the `RuleIdsTests` pattern, because that project
  cannot reference `Gateway.TestHarness`; #139 consolidates the composed suites' three readers, which
  keep pairing an Errors row with a `RuleIds` constant by hand (Q20). S6: both guards key a contract
  row by `code` exactly, which is why the column lands. S7: the rule-id scan becomes one rule in
  `tests/Gateway.ArchitectureTests` over `ReferenceRules.WorkstreamProjects` (#154), and `Problems`'
  eleven constants feed its extension to the codes (#158), so no migrate ticket here extends a scan
  and the three `SharedRuleIdsTests` retire there.
- **#141, the app's sessions.** S3: #141's one key-read call (`AppSessions.KeyOf`) lands before #163,
  workstream 3's migrate, so that `Rejection.Note` has one call site in Platform.Identity instead of
  three. The dead-cookie test stays in Platform.Identity either way, and the `SessionRef` write in the
  ticket store is #141's, not this request's.

## The three steering edits

Each is a steering-document edit whose authority is #142's acceptance, carried by #159's acceptance
criteria (ADR-0023 decision 3). This request edits none of them. A scripted session drafts and never
commits an ADR or a frozen contract's sentence (ADR-0021 decision 4), so #159's lane drafted all
three in its Answer on #159, and the integrator commits them.

### 1. A new ADR, extending ADR-0015 decision 1 from a value to a behaviour

Titled "A contract-fixed behaviour every workstream would copy lives once in `Platform.Abstractions`",
numbered next in `docs/adr/` by the session that commits it and indexed in that directory's README.
It is a new ADR, not an amendment: ADR-0015 is about constants and is a dated record. It records:

- the decision: such a behaviour lives once in `Platform.Abstractions` as a public static type, added
  under `contracts/README.md`'s between-gates rule, pinned by `PublicSurfaceTests`, and held to the
  contract by a test that reads the contract's own tables; the project stays "records and interfaces"
  plus the implementations a contract fixes (`SessionRef`, `Rejection`, `Problems`);
- the trade-off: runtime code that writes an HTTP response enters the project whose every later
  change is a freeze question, and a body-shape change (roadmap gap MR-6's correlation id) becomes one
  integrator ticket rather than four owner tickets that must agree;
- the four alternatives above;
- its consequences, with #142's draft clause "a stage may not write a status or code of its own,
  which each owner's scan enforces" replaced: S7 on #142 superseded the per-owner scans, so the
  enforcement is the one architecture rule of #154, extended to the codes by #158.

#159's Answer names the full draft, uncommitted, as "ADR draft: A contract-fixed behaviour every workstream
would copy lives once in `Platform.Abstractions`".

### 2. The `code` column on `contracts/request-gate.md` → Rule ids

The table gains a `code` column, appended last. `—` stands for an id that ends on no problem body,
and the writer of that case is named in the prose under the table:

```text
| Rule id | Fires when | `code` |
| --- | --- | --- |
| `host.unknown` | The request authority matches no registry `host` | `not_found` |
| `path.invalid` | The path failed normalisation | `invalid_request` |
| `path.reserved` | A reserved path that is switched off | `not_found` |
| `gate.cookie-name` | A cookie name imitates a platform cookie, in another letter case or sent twice | `invalid_request` |
| `gate.fetch-site` | `Sec-Fetch-Site` is absent, `same-site` or `cross-site` where `same-origin` is required | `origin_rejected` |
| `gate.origin` | `Origin` is present and is not the app origin | `origin_rejected` |
| `gate.method` | The path does not accept the method | `method_not_allowed` |
| `gate.navigation` | `/bff/login` without `Sec-Fetch-Mode: navigate` and `Sec-Fetch-Dest: document` | `origin_rejected` |
| `csrf.token` | The antiforgery token was missing or wrong | `csrf_failed` |
| `auth.callback` | Any refused login callback | — |
| `auth.session` | A cookie that resolves to no session for this app | `unauthenticated` |
| `api.unknown` | The app lists no upstream under that name | `unknown_api` |
```

Each cell is the code the stage that raises the id writes today, read from the code at `7831323`
(`RequestGate.cs`, `MethodAllowlist.cs`, `PlatformAntiforgery.cs`, `UnknownApi.cs`,
`HostResolution.cs`, `Assets.cs`, and the `unauthenticated` 401s of `Bff.cs` and
`AuthenticationEvents.cs`). The stale sentence under the table,
"Nothing writes `auth.callback` or `auth.session` yet; they wait on workstream 3.", is replaced by:

```text
`auth.callback` ends on no problem body: the OpenID Connect handler's remote-failure event writes
the static failure page `contracts/http-contract.md` → Errors describes, under that id.
`auth.session` is noted by workstream 3 wherever a cookie resolves to no session for this app, and
another writer then ends the response: the `/bff/session` endpoint answers `401 unauthenticated`;
the access-token step behind `MapPlatformProxy` answers `401 unauthenticated`; and
`UsePlatformShellGate`'s challenge answers a fetch with `401 unauthenticated` and a navigation with
a redirect to the identity provider, which has no problem body.
```

The existing parser in `RuleIdsTests` reads the first cell of each row only
(`tests/Platform.Abstractions.Tests/RuleIdsTests.cs`, `ParseRuleIds`), so it stays green over the
widened table. #159's lane checked that by applying the table above in its worktree and running
`dotnet test --project tests/Platform.Abstractions.Tests` (56 passed) and
`tests/Contracts.Tests` (135 passed), then reverting the file; a planted misspelling in the first
cell turned one test red, so the run read the widened file.

### 3. `GLOSSARY.md`: Rejection, Answer and Problem body

The Rejection entry's status list gains the shell gate's challenge of a navigation whose cookie
resolves to no session, and the entries Answer and Problem body are added, as #142's Further Notes
give them. The exact text is under #159's Answer.

## Decision

Integrator, 2026-10-20. **Accepted as written**, on the answers Adam accepted on 2026-10-19 and
recorded on #142: the grilling record's Q1 to Q20 and the shared decisions S1 to S8. The integrator
role is played by agent sessions (`GLOSSARY.md` → Integrator); this decision is recorded under
#159's acceptance, and Adam's landing of it is the human's act (ADR-0021 decision 5).

What is decided, each for the reason given beside it under "The change":

- **The interface** (sections 1 and 2): `Problems` with the eleven constants, `Rows`, `StatusOf` and
  `WriteAsync(context, code)`; `Rejection` with `Rows`, `Note`, `EndAsync` and `EndMethodAsync`;
  public static, in the one namespace `Platform.Abstractions`, both names added to
  `PublicSurfaceTests` in #160's commit with no `[Freeze]` marker, so both are frozen on landing.
- **The throws** (section 3): `EndAsync` refuses `gate.method` and `auth.callback`, naming the right
  entry, because the mistake is deterministic per call site.
- **The ordering** (section 4): the feature before the body.
- **The never-logs rule** (section 5): no logger, no dependency injection, no state but the two
  tables; the Warning line stays each stage's.
- **The alternatives** above, rejected for the reasons given.

### Landing it between gates

`contracts/README.md` → "What may land between gates": prose, and a new type that changes no
existing stage, owner or entry point's signature, land on the integrator's decision. `Rejection` and
`Problems` are new types; no `Add*`, `Use*` or `Map*` signature moves, no rule id is added and no
Errors row changes, because every edit the migrates make is inside a stage body or a test project.
`IRequestTelemetryFeature` landed this way (change request 6.4).

**The `code` column lands now, and does not wait for G3's tag.** A new column is on neither of the
rule's lists: it is not prose or a new type, which land, and it is not one of the examples of what
waits, since it adds no rule id, renames no field and changes no stage, owner or signature. So it is
decided by the rule's reason, that nothing already built against the frozen contract has to change
to keep compiling, and by precedent. It states a join the contract already implies by wording, and
every cell is what the code writes today, so nothing built against the frozen table has to change,
and the one parser of that table reads the first cell only. Change request 4.2's
`503 | idp_unavailable` row is the precedent for a frozen table gaining a cell between gates, and it
is that precedent, more than the rule's wording, that carries this decision. Because it lands now, #160's two-table test keys a
contract row by `code` exactly and does not pair by hand. The corrected sentence under the table is
the prose case.

### What is left, and to whom

| Ticket | Owner | Does |
| --- | --- | --- |
| #159 | integrator | This request; then the integrator commits the ADR, the `code` column with the corrected sentence, and the three glossary entries, from the drafts on #159, each citing #142's acceptance |
| #160 | integrator | `Rejection` and `Problems`, their tests and the two-table test, the two names on `PublicSurfaceTests` |
| #161 | workstream 2 | Platform.Hosting: host resolution and the asset endpoint through the module, the backup 405 under `gate.method` with one new test, the misattributed quote in the asset tests, its `Problems` copy deleted |
| #162 | workstream 4 | Platform.Security and Platform.Proxy: the request gate, the method allowlist, antiforgery, the unknown-api endpoint with a `LoggerMessage` template, the access-token step's answers and the upstream-failure writer; the `Refuse*` renames; both `Problems` copies deleted |
| #163 | workstream 3 | Platform.Identity: `NoteDeadSession` to `Rejection.Note`, the two `unauthenticated` answers to `Problems.WriteAsync`, the remote-failure event to `Note` plus the page, the wrong comment corrected, its `Problems` copy deleted |
| #158 | workstream 6 | The rule-id-literal rule of #154 extended to the eleven codes once `Problems` holds them |

#160 may open once this request lands. The migrates are adoption, not blocking: each waits for #160
only (and #163 also for #141's key-read call), each project compiles and is correct before and after,
because its own internal `Problems` is what the name resolves to until its file is deleted.
