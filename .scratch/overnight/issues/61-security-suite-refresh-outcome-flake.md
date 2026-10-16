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

It is intermittent and the row it lands on changes between occurrences. Both observations are in
`tests/Gateway.SecurityTests/Negative/`.

## The mechanism, and why the race is needed to trigger it

`RegistrySource.RefreshOnceAsync` ends a successful swap with `settledETag = stored.ETag`
(`RegistrySource.cs:183`), and every poll opens with
`store.GetAsync(key, settledETag ?? cold?.ETag, ct)` (`:96`). So **whichever poll performs the
swap, the next poll is told `NotModified` while the document is live.** The outcome token
describes what one poll did; it is not a statement about liveness.

**The race is strictly necessary to reach that state after a write.**
`InMemoryObjectStore.NextETag()` is `Interlocked.Increment` on a counter, not a content hash, so
every `PutAsync` yields a fresh ETag and no race-free path exists from a write to `NotModified`.
Measured: 40 consecutive `UpdateRegistryAsync` calls with no interleaving returned **`Swapped`
40/40**, revisions 2 to 41. Absent a foreign poll, the doc comment holds.

So the failure needs the interleaving: write the object, let *another* poll swap it, and the poll
`UpdateRegistryAsync` makes is then told `NotModified`. Forced directly, that reproduces:

```
foreign=Swapped update=NotModified live=2
```

That `update=` is a real `UpdateRegistryAsync` call returning `NotModified` with its own write
live. With step 2's fix applied the same test reads `update=Swapped`, so this is the shape that is
red before and green after.

**A caution for whoever picks this up.** A simpler demonstration — `UpdateRegistryAsync`, then
`RefreshRegistryAsync` again — also prints `first=Swapped ... second=NotModified liveRev=2`, and
it proves nothing. `RefreshRegistryAsync` is documented as "Polls the registry object once, as the
poller would on its next tick" and makes no liveness claim, so a second poll finding nothing
changed is the conditional GET working as designed. An earlier revision of this ticket mistook
that for proof that `UpdateRegistryAsync`'s contract was broken. It is not: only the forced
interleaving above shows the defect.

**What is true regardless: the fix does not require reproducing the race.**
`UpdateRegistryAsync`'s doc comment says "The outcome says whether it went live", and on the
interleaving above it does not. Checking liveness when the poll returns `NotModified` — what
`StartAsync` already does at `GatewayHarness.cs:233-234` — removes the flake whether or not anyone
catches the race in the wild.

## The racy half: which other poll is it

Suspected, not confirmed: the poller's first tick. `RegistryPoller.ExecuteAsync` opens with
`await Task.Yield()` so the first read is a thread-pool continuation with no ordering guarantee,
then polls immediately, then waits `Registry:PollSeconds` — which `GatewayHarness.cs:49` pins at
**3600**, jittered downward only. Two consequences:

- Only the first tick can ever race inside a test; the second is an hour away.
- Under a saturated thread pool that continuation can land past `StartAsync`'s line-232 refresh
  and inside the test body's `UpdateRegistryAsync`. Both failing rows call `Suite.StartAsync`
  immediately before `UpdateRegistryAsync`, which fits.

**Corroboration already in the tree.** `AssetPathTests.A_background_registry_poll_does_not_count_as_a_request_store_read`,
added by `d17839f`, carries this comment:

> A real poll inside the observation interval reproduces the Linux CI race without depending on
> the hosted poller's first read winning a scheduling race (N-13).

Same assembly, same week, already merged. Read it for what it is: the comment *presupposes* the
race rather than evidencing it, and cites no run, so it is a second author reaching the same
conclusion independently rather than proof. Its value is the technique — force a real poll inside
the window rather than wait for one.

## Observations, with their denominators

| When | Where | Row |
| --- | --- | --- |
| 2026-10-16, CI run 38849080050 attempt 1 | GitHub runner, Release | `UpstreamResponseTests.An_upstreams_cors_header_and_policy_are_replaced_by_the_gateways_on_any_status(status: 200)` and `UnknownApiTests.An_api_name_another_app_lists_is_still_unlisted_for_this_one` |
| 2026-10-16, local full-solution run | macOS arm64, Release | `UpstreamResponseTests.An_upstreams_cors_header_and_policy_are_replaced_by_the_gateways_on_any_status(status: 500)` |

That run's head is **`55c4599`**, the two-line roadmap-refresh commit that landed after #34 and
#35 — not the merge of either, though its tree contains both. Attempt 2 of the same run passed.

Twice in nine local full-solution runs and one CI attempt, so "about one run in ten" is an
estimate from a single local occurrence, not a measured rate. Eight instrumented runs that did not
reproduce it are **not** evidence against the mechanism: at a 1-in-10 rate, P(zero in eight) =
0.9^8 ~= 0.43, the most likely single outcome. The instrumentation only emits when the flake
occurs, so those eight runs carry one bit between them.

Neither #34 nor #35 touches `src/Platform.Registry`, `tests/Gateway.TestHarness` or
`tests/Gateway.SecurityTests`. They are not small changes otherwise — #34 is 33 files and adds an
`IIdpProfile` member — but nothing in either reaches this path.

## Suggested order of work

1. **Write the test that fails before the fix and passes after it.** Two shapes do not work: one
   that merely refreshes twice is green both ways, and one built on a bare `RefreshRegistryAsync`
   is red both ways, because step 2 changes `UpdateRegistryAsync` and `PublishRegistryAsync` and a
   bare refresh never calls them. **The write has to go through `UpdateRegistryAsync`**, with a
   foreign poll held inside `RegistrySource.polling` across it. There is no seam between the
   `PutAsync` and the refresh inside `PublishRegistryTextAsync` (`GatewayHarness.cs:284-303`), so
   the way in is a gated `IObjectStore` decorator registered through
   `GatewayHarnessOptions.ConfigureServices`, which runs at `GatewayHarness.cs:200`, after the
   store is registered at `:186`; the harness writes through its concrete `Store` property rather
   than DI, so the decorator intercepts only `RegistrySource`'s reads. Arm the gate, start a
   refresh so it blocks inside `GetAsync` holding `polling`, call `UpdateRegistryAsync` so it
   writes and then blocks on `polling`, then release the gate.
2. **Fix the harness contract.** `UpdateRegistryAsync` and `PublishRegistryAsync` should report
   the outcome of the write they made: on `NotModified`, check whether the written revision is
   live and say so. Correct the doc comment with it.
3. **Only if the wild race still matters**, force it rather than wait: gate the poller's first
   `RefreshAsync` on a `TaskCompletionSource`, or delay after `Task.Yield()`, and run
   `Gateway.SecurityTests` alone.

A printf probe in `PublishRegistryTextAsync` was used to chase this and is **not** recommended: it
costs a full-solution run per attempt, each with a 0.9 chance of learning nothing and 0.43
across eight, and a naive one that
parses `text` unconditionally throws on the malformed documents that method exists to accept
(F-09, N-14).

## Why it is filed rather than fixed

`tests/Gateway.TestHarness` and `tests/Gateway.SecurityTests` are workstream 6's and
`src/Platform.Registry` is workstream 2's. The integrator found it while landing #34 and #35 and
owns neither (`AGENTS.md` -> "Write only inside your owner's paths"). `docs/gaps.md` is also
workstream 6's, so **whoever picks this up should add the gap line there**; the integrator could
not.

## What it costs to leave

A red `main` on a commit that is fine. The 2026-10-15 red on `main` was also
`Gateway.SecurityTests`, a different row, and that one was a real defect — a test defect, an
over-broad store-read assertion fixed by `d17839f`, with no `src/` change. So a flake here trains
the next reader to re-run rather than look, in the one assembly that has already hidden a genuine
assertion bug behind the same reflex.

## Comments

Reported by the integrator session that landed #34 and #35 on 2026-10-16, after the merge of #34
went red and the re-run of the same commit went green.

Revised 2026-10-16 after `/code-review` on PR #36: the failing run is attributed to `55c4599`
rather than to the merge of #34, the `d17839f` reproduction technique is recorded, the evidence
from eight non-reproducing runs is given its actual weight, and the probe is withdrawn.

Corrected 2026-10-16 after `/code-review` on PR #38, which found the previous revision's headline
claim wrong. It asserted that a second `RefreshRegistryAsync` returning `NotModified` proved
`UpdateRegistryAsync`'s contract false "independently of any scheduling race". It does not: that
call is documented as polling once with no liveness claim, `InMemoryObjectStore.NextETag()` is a
counter so every write yields a fresh ETag, and 40 consecutive race-free `UpdateRegistryAsync`
calls returned `Swapped` 40/40. The race is necessary, and the work order has been rewritten --
the earlier step 1 would have had someone write a test that is green before and after the fix.
The arithmetic is also corrected: 0.43 is the chance across eight runs, 0.9 per run.

Note for triage: another session has an unlanded ticket on the same race, task id 6.1a, also
numbered 61, reaching the same mechanism independently and marked ready-for-agent. These should be
merged into one before either is worked, and the number collision resolved by whoever lands
second. Given that twin is `ready-for-agent` with a prescribed fix, it is probably the better
starting point and this one is the evidence trail.
