# 61: Gateway.SecurityTests intermittently reads NotModified where it expects Swapped

**Task:** CR-6.x
**Owner:** ws6-verification
**Blocked by:** None
**Status:** needs-triage

**What happens:** a row that publishes a registry revision and asserts the outcome fails with

```
Assert.Equal() Failure: Values differ
Expected: Swapped
Actual:   NotModified
```

It is intermittent, it only appears under the full-solution run, and the row it lands on changes
between occurrences. Both observations so far are in `tests/Gateway.SecurityTests/Negative/`.

## Observed twice, on code that did not change

| When | Where | Row |
| --- | --- | --- |
| 2026-10-16, CI run 38849080050 on `main` | GitHub runner, Release | `UpstreamResponseTests.An_upstreams_cors_header_and_policy_are_replaced_by_the_gateways_on_any_status(status: 200)` and `UnknownApiTests.An_api_name_another_app_lists_is_still_unlisted_for_this_one` |
| 2026-10-16, local full-solution run | macOS arm64, Release | `UpstreamResponseTests...(status: 500)` — a different case of the same theory |

The CI failure was on the merge of #34 and #35. Neither touches `src/Platform.Registry`,
`tests/Gateway.TestHarness` or `tests/Gateway.SecurityTests`; the whole diff across both merges is
`Platform.Abstractions` doc comments plus one new enum, `Platform.Testing` doubles, and unit tests.
A re-run of the identical commit passed, so `main` is green and this is not a regression from
either merge.

## Not reproduced since, and the hypothesis is therefore unproven

Mechanism suspected from reading the code, **not** confirmed:
`GatewayHarness.PublishRegistryTextAsync` writes the object and then calls `RefreshAsync`, which
serialises on `RegistrySource.polling`. If a poll is already in flight — the first poll of a
just-started harness is the candidate, and both failing rows call `Suite.StartAsync` immediately
before `UpdateRegistryAsync` — that poll reads the object the harness has just written, swaps it
live and records the new ETag in `settledETag`. The harness's own refresh then sends that ETag as
`If-None-Match` and is told `NotModified`. On this theory the document **is** live and only the
returned token is wrong, which would make the assertion, not the gateway, the thing at fault.

`GatewayHarness.cs:230` shows the authors already handled this race for the startup poll — "the
poller's first read may still be in flight. One more poll waits for it … and settles the outcome
either way" — but `UpdateRegistryAsync` against a running gateway has no equivalent.

**Eight instrumented full-solution Release runs did not reproduce it.** The instrumentation printed
the live revision whenever `NotModified` came back, and it never fired, so the liveness half of the
hypothesis has no evidence behind it. Treat the paragraph above as a lead, not a finding.

The probe, to re-apply in a throwaway worktree — in `PublishRegistryTextAsync`, replace
`return await RefreshRegistryAsync(ct);` with:

```csharp
RefreshOutcome probeOutcome = await RefreshRegistryAsync(ct);
if (probeOutcome == RefreshOutcome.NotModified)
{
    long wrote = long.Parse(
        System.Text.Json.Nodes.JsonNode.Parse(text)!["revision"]!.ToJsonString(),
        CultureInfo.InvariantCulture);
    Console.WriteLine($"PROBE: NotModified returned; wrote revision {wrote}, live revision {Registry.Current.Revision}");
}

return probeOutcome;
```

If it prints with the two revisions equal, the document went live and the outcome token is the
defect. If they differ, the refresh genuinely missed the write and the problem is further down.

## Why it is filed rather than fixed

`tests/Gateway.TestHarness` and `tests/Gateway.SecurityTests` are workstream 6's, and
`src/Platform.Registry` is workstream 2's. The integrator found it while landing #34 and #35 and
owns neither path (`AGENTS.md` → "Write only inside your owner's paths").

## What it costs to leave

A red `main` on a commit that is fine, roughly one run in ten locally. The 2026-10-15 failure on
`main` was also `Gateway.SecurityTests`, a different row (`AssetPathTests`), and that one turned
out to be a real defect fixed by `d17839f`. So a flake here trains the next reader to re-run rather
than look, in the one assembly that has already hidden a real bug behind the same reflex.

## Comments

Reported by the integrator session that landed #34 and #35 on 2026-10-16, after the merge of #34
went red and the re-run of the same commit went green.
