# Change request MR-8: configuration validated at start-up, and a smoke test that supplies one

- **Raised by:** the Phase 2 planning session of 2026-10-19, on behalf of workstream 2 (Hosting and
  registry), for the Phase 2 spec issue (to be filed)
- **Tasks:** MR-8; 2.2 (`AddPlatformRegistry`, where the validation runs); 1.4 (the task
  environment, #56); I-5 (CI's smoke step)
- **Date:** 2026-10-19
- **Status:** moved to #544, 2026-10-29, by the cutover #439

## What the gateway does with its configuration today

`docs/roadmap.md` MR-8: "There is a schema for platform configuration. Nothing says the process
reads it, validates against it and refuses to serve when it fails." Read from the code, the floor
is lower than that, and uneven:

| Section | Read by | On a missing or bad value |
| --- | --- | --- |
| `Registry:*` | `src/Platform.Registry/RegistryPoller.cs`, bound as `RegistrySourceOptions` | One Error line, no poll ever, `/readyz` stays `503` for the life of the process while `/healthz` stays `200` |
| `Policy:*`, `Issuers:*` | `src/Platform.Registry/PlatformPolicyConfiguration.cs` | Fails closed and silently: an empty list allows nothing, and an issuer missing a field is not an issuer, so every entry naming it is rejected at the next poll under `unknown-issuer` |
| `Issuers:*` again | `src/Platform.Identity/IssuerDirectory.cs` | Throws on the first login, not at start-up |
| `Session:*` | `src/Platform.Identity/MemorySessionStore.cs`, `FromConfiguration` | Throws on the first use of the store, not at start-up |
| `ForwardedHeaders:TrustedNetworks` | `src/Platform.Security/PlatformForwardedHeaders.cs` | No networks means no forwarded header is read at all, so every request reaches the later stages as plain `http`, which the login redirect and the `__Host-` cookie attributes are built from (`src/Gateway/Program.cs`, the forwarded-headers comment); nothing is logged |
| `Platform:Cell` | `src/Platform.Telemetry/PlatformJsonFormatter.cs`, `PlatformTelemetryExtensions.cs` | Absent from every log line and every span; nothing is logged about that |
| `Assets:Bucket` | Nothing: `src/Platform.Registry/PlatformRegistryExtensions.cs` builds the one store from `Registry:Bucket` | Ignored |

The comment in `PlatformPolicyConfiguration.cs` says "the task environment is checked against the
schema before it reaches a gateway (task 1.4)". Nothing under `infra/` does that: the cell
module's variables carry their own `validation` blocks and cite the schema as the shape
(`infra/modules/cell/variables.tf`). The only reader of `contracts/platform-config.schema.json`
against a whole document is `ContractDocuments.ValidatePlatformConfiguration` in
`src/Platform.Registry`, which `spactl` runs on its `--config` file and the gateway never runs on
itself.

So a task started with `Session__MaxSessions=0` turns healthy, becomes ready, serves assets, and
fails the first login with a `500`. That is the per-request failure MR-8 names.

## What workstream 2 can do in its own paths

Validate the configuration, as the process sees it, against the embedded
`platform-config.schema.json` at start-up, and refuse to start on a failure. Three points of that
design are settled by the tree rather than by this request:

- **It runs in `AddPlatformRegistry`, not in `AddPlatformHosting`.** The roadmap names the latter.
  The engine and the embedded schemas are in `Platform.Registry` (`RegistrySchemas`,
  `ContractDocuments`), and A-03 forbids `Platform.Hosting` reaching `Platform.Registry`:
  `tests/Gateway.ArchitectureTests/ReferenceRules.cs` lists both among the workstream projects
  that may share only `Platform.Abstractions`. A second copy of the engine in `Platform.Hosting`
  would also break decision D-1's "in shipping code the engine is touched in one file"
  (`docs/dependencies.md`). `AddPlatformRegistry` is second in the service table and workstream
  2's, so no entry point moves and `docs/dependencies.md` -> "Expected in" needs no new project.
- **The JSON form is rebuilt from `IConfiguration` by the schema's own types.** The schema
  describes the JSON form and says the deployed form is strings in environment variables
  (`contracts/platform-config.schema.json`, `description`). A string `15` is read as an integer
  only where the schema says `integer` (`Registry:PollSeconds`, `Session:MaxSessions`), and a
  section whose keys are `0`, `1`, `2` is an array only where the schema says `array`. The rebuild
  is workstream 2's code; what it means for a value to be valid is item 2 below.
- **A failure stops the process.** `RegistryPoller` deliberately never stops the host, because a
  registry that cannot be read is a runtime condition the last snapshot survives
  (`RegistryPoller.cs`, the class comment). A configuration the schema refuses is not: nothing in
  the process can be right, and the ECS deployment circuit breaker rolls a deployment back on a
  task that never turns healthy (`infra/modules/cell/ecs.tf`, `deployment_circuit_breaker`). So
  `AddPlatformRegistry` throws, after one Error line per finding under the category
  `Platform.Registry.Configuration`, each naming a key and never a value.

None of that touches an integrator path. Two things do.

## The change

1. **`tools/smoke.sh` supplies a configuration, and gains one failing case.** The smoke test starts
   the gateway with no platform configuration at all and expects `/healthz` `200`, `/readyz` `503`
   and `404` on every app path (`tools/smoke.sh`, the `expect` lines). Under this request that
   process refuses to start, so the script sets the required sections from
   `contracts/platform-config.example.json` as environment variables: `Platform__Cell`, the
   `Registry__*` trio, `Assets__Bucket`, one issuer, the two `Policy__*` lists, one trusted
   network, `Session__Store=memory` and `Session__MaxSessions`. The probes stay as they are: with
   `Registry:*` set and no AWS region, the poller logs that the S3 client could not be built and
   leaves `/readyz` at `503`, which `tests/Platform.Registry.Tests/RegistryPollerTests.cs`
   (`A_poll_that_throws_does_not_stop_the_host_and_the_next_one_runs`) already proves. Then one
   more case: the same environment with `Session__MaxSessions=0` exits non-zero within the script's
   start-up wait, and its log names `Session:MaxSessions`. That is the case `docs/roadmap.md` S1
   wrote the smoke test for, "a settings file the binder rejects builds and tests clean", and the
   smoke test is the one place that starts the process.

   `.github/workflows/ci.yml` calls the script and needs no change.

2. **`contracts/platform-config.schema.json`, one sentence in its `description`:** "The gateway
   rebuilds this form from its configuration, reading each value as the type this schema gives its
   property, validates it at start-up, and refuses to start on a failure; `spactl` validates a
   configuration file the same way." Prose, so it lands on the integrator's decision
   (`contracts/README.md` -> What may land between gates). Without it, the gateway's rule for what
   a valid environment is would exist only in workstream 2's code.

3. **Optional, `contracts/README.md`:** a row in "Rules that JSON Schema cannot express" beside the
   `clientSecretRef` row: "the task environment is the JSON form, typed by the schema", checked by
   task 2.2 at start-up. Prose.

## Why this is a request rather than an edit

`tools/smoke.sh`, `.github/` and `contracts/` are the integrator's (`AGENTS.md`). The smoke test is
also G1a's exit test, so what it starts the process with is a gate's evidence.

## Blocking

Workstream 2 can build and prove the validation in `tests/Platform.Registry.Tests` now, and must
land it together with item 1: landed alone, it turns CI's smoke test red on the next push.
