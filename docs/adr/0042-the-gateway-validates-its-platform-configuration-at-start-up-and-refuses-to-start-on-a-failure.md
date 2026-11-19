# ADR-0042: The gateway validates its platform configuration at start-up and refuses to start on a failure

- **Status:** Accepted.
- **Date:** 2026-11-18
- **Deciders:** Adam, on change request #544: the decision of 2026-10-30
  (https://github.com/adammedford/spa-platform/issues/544#issuecomment-6513043682) and the decision
  of 2026-11-18 that adds the harness.
- **Plan ref:** No decision row. `docs/roadmap.md` MR-8; change request #544, moved from
  `contracts/changes/mr-8-platform-configuration-validated-at-start-up.md`.

## Context

`contracts/platform-config.schema.json` describes the settings a task's environment supplies. Until
this decision the gateway never read it. Each section was read by its own consumer, and a missing
or bad value failed where it was first used: `Registry:*` left `/readyz` at 503 for the life of the
process, `Issuers:*` threw on the first login, `Session:*` on the first use of the store, and an
empty `ForwardedHeaders:TrustedNetworks` or `Platform:Cell` failed nothing and logged nothing. A
task started with `Session__MaxSessions=0` turned healthy, became ready, served assets and
answered the first login with a 500. `tools/smoke.sh`, G1a's exit test, started the gateway with
no platform configuration at all and expected it to serve.

A registry that cannot be read is a runtime condition, and the last snapshot survives it. A
configuration the schema refuses is not: nothing in the process can be right. The cell's deployment
circuit breaker already rolls back a deployment whose task never turns healthy.

## Decision

1. **The gateway validates its configuration against the embedded schema at start-up and refuses
   to start on a failure.** `AddPlatformRegistry` rebuilds the JSON form from `IConfiguration`,
   reading each value as the type the schema gives its property, validates it, logs one Error line
   per finding under `Platform.Registry.Configuration`, naming a key and never a value, and throws.
   `spactl` validates a configuration file the same way, with the same engine.
2. **The smoke test supplies a configuration, and proves the refusal.** `tools/smoke.sh` sets the
   required sections from `contracts/platform-config.example.json` and keeps its probes; one more
   case starts the same environment with `Session__MaxSessions=0`, and the process exits non-zero
   with a log line naming `Session:MaxSessions`.
3. **The schema states the one exception the code already made.** `Authority` and `BrowserOrigin`
   admit plain http on a loopback address beside https. `IssuerDirectory` already fetches
   discovery that way for the hermetic tier's test provider; nothing off the machine is fetched
   without TLS. The schema, the validation, `spactl` and the code now hold one rule.

## Alternatives rejected

- **Keep failing at first use.** The failure reaches a user as a 500 on a task the load balancer
  calls healthy.
- **Validate in `AddPlatformHosting`**, as the roadmap named it. The engine and the embedded schemas
  are in `Platform.Registry`, and A-03 keeps `Platform.Hosting` from referencing it.
- **A switch, or an environment name, that turns validation off for tests.** A bypass in shipping
  code for the check whose point is that a task cannot start without it; the hermetic tier would
  then never run the validation.
- **Serve the test provider over TLS so the harness passes the https-only schema.** It needs work
  from three owners landing together and a way for an in-process gateway to trust a test
  certificate, for a path that never leaves the machine.

## Consequences

- A task with a bad or missing platform configuration never turns healthy, and the deployment
  rolls back. The failure moves from the first request to the deploy.
- The operating assumption `docs/roadmap.md` S1 wrote down changes: a gateway with no platform
  configuration no longer starts and answers `/readyz` 503. Everything that starts the process
  supplies a whole configuration: the smoke test, the harness, the benchmark, the cell.
- The start-up log names the keys that failed and no value, so a secret reference or an origin
  never reaches a log line through this path.
- A configuration naming a loopback http issuer passes validation. Deployed, it reaches no provider
  and its first login fails, as a wrong authority does.
- The change is one-way for `tools/smoke.sh`'s contract with CI: the validation and the smoke
  test's configuration land together, or the smoke test is red on the next push.

## Evidence

- Change request #544 and its two decision comments.
- The relayed scope: https://github.com/adammedford/spa-platform/issues/544#issuecomment-6747150630,
  from #1112's grilling record.
- `src/Platform.Identity/IssuerDirectory.cs`, the loopback exception and its comment.
