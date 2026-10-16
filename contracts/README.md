# Contracts

These files freeze at G1a, and their identity parts at G1b. After that, a change is a written
request to the integrator, never an edit: add `contracts/changes/<task-id>-<slug>.md` describing the
change, and stop. Anyone may add one. The integrator decides and edits.

**What may land between gates.** Prose, and a new type that changes no existing stage, owner or entry
point's signature, land on the integrator's decision, because nothing already built against the
frozen contract has to change to keep compiling. Anything else waits for the next gate: a changed
stage, owner or signature, a narrowed schema, a new rule id, a renamed field. The decision is still
written up beside the request, because the first question anyone asks of a frozen file is why it
moved. `changes/i-7-pipeline-stub-blocks-later-stages.md` is the prose case and
`changes/6.4-request-telemetry-feature.md` the added-type case.

Written from `docs/plan/02-contracts.md`, which is a frozen export and is never edited in place. A
decision made or revised after the 11 October 2026 export belongs in `docs/adr/`.

## What is here

| Contract | File | Freezes at | Consumed by |
| --- | --- | --- | --- |
| Registry document | [`registry.schema.json`](registry.schema.json), [`registry.example.json`](registry.example.json) | G1a, except the `oauth` section at G1b | Workstreams 2, 3, 4 and `spactl` |
| App definition | [`app-definition.schema.json`](app-definition.schema.json), [`app-definition.example.json`](app-definition.example.json) | With the registry document | Workstreams 1, 2 and 5 |
| Platform configuration | [`platform-config.schema.json`](platform-config.schema.json), [`platform-config.example.json`](platform-config.example.json) | G1a | Workstream 1 for the task environment, then everyone |
| Pipeline and entry points | [`pipeline.md`](pipeline.md), `src/Gateway/Program.cs` | G1a; stage order changed at G1b | Everyone |
| Browser contract | [`http-contract.md`](http-contract.md) | G1a | Workstreams 3, 4, 5, 6 |
| Request gate | [`request-gate.md`](request-gate.md) | G1a, with the browser contract | Workstream 4, and workstream 6's negative suite |
| Upstream contract | [`upstream-contract.md`](upstream-contract.md) | G1a | Workstream 4 and the mock API |
| S3 layout | [`s3-layout.md`](s3-layout.md) | G1a | Workstreams 1 and 2 |
| Infrastructure outputs | [`infra-outputs.schema.json`](infra-outputs.schema.json), [`infra-outputs.example.json`](infra-outputs.example.json) | G1a | Workstream 1, the scripts, workstream 6 |
| Resource tags | [`resource-tags.md`](resource-tags.md) | G1a | Workstream 1, `teardown.sh`, workstream 6 |
| Telemetry | [`telemetry.md`](telemetry.md) | G1a | Everyone |
| Records and interfaces | `src/Platform.Abstractions` (task I-3) | G1a, except the identity interfaces at G1b | Workstreams 2, 3, 4, 6 |
| Test doubles | `src/Platform.Testing` (task I-3) | With the interface each one stands in for | Everyone |

The integrator writes every file in this table. Workstreams 1 and 6 propose the infrastructure
outputs and telemetry contracts, and workstream 1 proposed the resource tags; they do not edit them.

`resource-tags.md` is the one file here that did not come from `docs/plan/02-contracts.md`. The plan
names "the platform tag" three times without defining it, and three parties have to agree on the
string for teardown's emptiness check to mean anything. Change request
`changes/1.6a-platform-tag-convention.md` raised it; ADR-0013 decides it.

`docs/plan/02-contracts.md` gives the request gate its own section under the browser contract's
freeze. Here it is its own file, because workstream 4 owns the gate and `http-contract.md` is what
an SPA author reads. Nothing in it differs from the plan. The app definition likewise has its own
schema file; the plan describes it inside the registry document section, as an entry without
`clientId`, `clientSecretRef` and `release`.

## What G1b froze

Task I-6 prepares these identity parts for the `contracts-v1` freeze using the G0 report and
[ADR-0017](../docs/adr/0017-g0-findings-fold-into-the-identity-contracts.md). The annotated tag on
`main`, citing its green CI run, closes G1b; until that tag is reachable from `origin/main`,
workstream 3 stays blocked. After the tag, these parts need a change request to alter:

| Part G1b froze | Where |
| --- | --- |
| The `oauth` section of a registry entry | `registry.schema.json#/$defs/oauth`, `app-definition.schema.json#/properties/oauth` |
| `IIdpProfile` names, which `Issuers:{name}:Profile` selects | `platform-config.schema.json`, the `Profile` enum |
| The `/bff` endpoints and the session record behind `__Host-spa-session` | `http-contract.md` |
| Scheme names, the session record, `IAccessTokenSource`, `AccessTokenResult`, `ISessionStore`, `IIdpProfile`, `ClientCredentialStyle` | `src/Platform.Abstractions` (task I-3) |

The schema annotations record `"status": "frozen-at-g1b"` for these parts and
`"frozen-at-g1a"` for the rest; root `exceptions` arrays are removed. These annotations constrain
no instance. In code, I-6 removes every `[Freeze("G1b")]` marker: marked means draft until its
gate is tagged, unmarked means frozen. `FreezeAttribute` stays available for later gates. The
contract and abstraction tests read the artifacts and assert that no draft part remains.

What G1b changed, rather than only froze: `IIdpProfile` gained `ClientCredentialStyle`, because
Cognito's revocation endpoint takes only a Basic header while its other endpoints take either
(ADR-0017, evidence C-2.19 and C-2.21). **The pipeline's stage order moved**: telemetry is now the
outermost stage, ahead of forwarded headers, so every rejection the application answers reaches a
request line and `security.rejections` under its rule id (ADR-0018, change request 6.4, and
tasks 4.7a and 6.3b). The
`oauth` section, the `Profile` enum and the session record keep their shapes. The browser
contract clarifies nullable display claims, session expiry and unconditional local logout removal.

## Using the schemas

Draft 2020-12. Every schema has an `$id` under `https://spa-platform.internal/contracts/`, and they
`$ref` each other by that URI: `app-definition`, `platform-config` and `infra-outputs` all reach
into `registry.schema.json`'s `$defs`, so a rule such as the `appId` pattern exists once. A consumer
must therefore **load every schema into one registry before evaluating any of them**. That applies
to `tests/Contracts.Tests` and to `spactl` (task 2.6). The URIs do not resolve over the network and
are not meant to.

Three readings are the integrator's, because the plan states a shape and not a rule:

- **Unknown properties are rejected** (`additionalProperties: false`) in every object the platform
  owns. A frozen document with a typo must fail, not be silently ignored. `platform-config` is the
  exception at its root, because the task environment also carries `Logging`, `ASPNETCORE_*` and
  `OTEL_*`; every section the platform owns inside it is closed.
- **`schemaVersion` is `const: 1`.** A later shape gets a later number.
- **An entry requires `appId`, `host`, `release`, `oauth` and `session`.** `apis` and `csp` may be
  absent, which reads as an app with no upstream and an app that adds nothing to the baseline
  content security policy. `session` is required because the plan fixes no default lifetime, and
  `AppDefinition.SessionLifetime` is not nullable.

## Rules that JSON Schema cannot express

Each of these is in the one validation library of task 2.1, which the gateway and `spactl` share so
they cannot disagree. The schema files repeat each note in the `description` of the property it
belongs to, with one exception: the last row below was added after G1a froze the schemas, so it is
recorded here only.

| Rule | Why not expressible | Checked by |
| --- | --- | --- |
| `revision` must be greater than the live one | Compares two documents | Task 2.1, against the live snapshot |
| `appId` unique in the registry | Cross-entry uniqueness of one property. `uniqueItems` compares whole entries only. | Task 2.1 |
| `host` unique in the registry | The same | Task 2.1 |
| `oauth.clientId` unique across apps | The same | Task 2.1 |
| `apis[].name` unique within the app | The same, one level down | Task 2.1 |
| `host` ends with an allowed suffix | Policy-bounded: the suffixes are in `Policy:AllowedHostSuffixes`, which the schema cannot see | Task 2.1 |
| `apis[].upstream` matches upstream policy | Policy-bounded, as above, for `Policy:AllowedUpstreams`. The schema still rejects a path, query, fragment or credentials in it. | Task 2.1 |
| `oauth.issuer` names an issuer in platform configuration | Policy-bounded, as above, for `Issuers:{name}` | Task 2.1 |
| `clientSecretRef` resolves to a readable parameter | Needs I/O. A snapshot that cannot read one is rejected and the live snapshot stays; at a cold start the affected apps are left out and reported, and the rest go live. | Tasks 2.2 and 2.5 |
| More than one upstream origin on an entry | A warning, and a schema has no notion of one. Task 2.1 keeps errors and warnings apart. | Task 2.1 |
| The `release` prefix exists in S3 | Needs I/O, and only `spactl` does it; the gateway does not | Task 2.6 |
| A port above 65535 | The pattern bounds the port's shape, not its value | Task 2.1 |
| A schema-valid integer the platform cannot hold or apply | Draft 2020-12 reads `30`, `30.0` and `3e1` all as integers, and bounds neither `revision` nor `apis[].timeoutSeconds` above. The limits are the runtime's, not the document format's: a `revision` beyond Int64, and a `timeoutSeconds` the proxy cannot apply exactly. Reported as `integer-out-of-range`. | Task 2.1 |
| The per-entry fingerprint, and eviction when it changes | Not validation at all: a hash of the entry's canonical JSON | Task 2.1 |
| A content security policy source that embeds a wildcard, such as `https://*.example` | The plan rejects the source `*`, which the schema rejects, and says nothing about wildcards inside a host | Task 2.1, if the platform wants it. A change request would be the way to add it. |

Two more things the schemas deliberately do not decide:

- **An empty `apps` array is a valid document.** Deployment refuses to start a gateway against an
  empty registry (task 1.7); that is a deployment rule, not a document rule.
- **`timeoutSeconds` has no upper bound in the currently frozen schema.** ADR-0019 supersedes
  the earlier decision to keep the numeric limit only in the validator. At G2, before the
  `g2-local-slice` tag, the schema will gain `maximum: 2147483`, the measured YARP 2.3.0 ceiling
  (ADR-0015). Refusing values no build could apply as written stays within `schemaVersion: 1`;
  later changes to valid document meaning require a new version. A proxy or runtime upgrade that
  changes this ceiling must update the contract schema, validator and the test proving the proxy
  applies the contracted value through the applicable freeze/gate process.

  **The schema and validator changes remain pending.** The validator's present bound is the
  `TimeSpan` limit, so values between it and the proxy ceiling still validate without being applied
  as written. ADR-0019 assigns the schema edit to the integrator at G2, then ticket 49 (task 2.1b)
  replaces the validator's bound with the schema's. Change request
  `changes/2.1-registry-schemas-and-jsonschema-net.md` retains the original measurement.
