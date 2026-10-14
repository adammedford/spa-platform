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
| Pipeline and entry points | [`pipeline.md`](pipeline.md), `src/Gateway/Program.cs` | G1a | Everyone |
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

## What is draft until G1b

The identity parts wait for G0. Task I-6 folds the G0 findings in, records them as a decision record
and tags `contracts-v1`. Until that tag exists, these may change without a change request, and
nothing in `src/Platform.Identity` starts:

| Draft part | Where |
| --- | --- |
| The `oauth` section of a registry entry | `registry.schema.json#/$defs/oauth`, `app-definition.schema.json#/properties/oauth` |
| `IIdpProfile` names, which `Issuers:{name}:Profile` selects | `platform-config.schema.json`, the `Profile` enum |
| The `/bff` endpoints and the session record behind `__Host-spa-session` | `http-contract.md` |
| Scheme names, the session record, `IAccessTokenSource`, `IIdpProfile` | `src/Platform.Abstractions` (task I-3) |

Every draft subschema carries an `x-freeze` annotation naming the gate, so the marker travels with
the schema rather than only with this page. `x-freeze` is an annotation: it constrains no instance.

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
belongs to.

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
| The per-entry fingerprint, and eviction when it changes | Not validation at all: a hash of the entry's canonical JSON | Task 2.1 |
| A content security policy source that embeds a wildcard, such as `https://*.example` | The plan rejects the source `*`, which the schema rejects, and says nothing about wildcards inside a host | Task 2.1, if the platform wants it. A change request would be the way to add it. |

Two more things the schemas deliberately do not decide:

- **An empty `apps` array is a valid document.** Deployment refuses to start a gateway against an
  empty registry (task 1.7); that is a deployment rule, not a document rule.
- **`timeoutSeconds` has no upper bound**, because the plan states none. It must be a positive
  integer.
