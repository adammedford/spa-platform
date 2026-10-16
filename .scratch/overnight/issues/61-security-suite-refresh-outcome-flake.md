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

## The important part: half of this needs no race, and is proven

`RegistrySource.RefreshOnceAsync` ends a successful swap with `settledETag = stored.ETag`, and
every poll opens with `store.GetAsync(key, settledETag ?? cold?.ETag, ct)`. So **whichever poll
performs the swap, the next poll is told `NotModified` while the document is live.** The outcome
token describes what one poll did. It is not a statement about liveness.

Proven directly, no race involved, by a throwaway test against `origin/main` at `34f3f0e`: start a
harness, `UpdateRegistryAsync`, then `RefreshRegistryAsync` again.

```
first=Swapped liveRev=2; second=NotModified liveRev=2; writtenRev=2
```

The second refresh reports `NotModified` with the written revision live. **That makes
`UpdateRegistryAsync`'s own doc comment — "The outcome says whether it went live" — false in
general**, independently of any scheduling race.

**So the fix does not depend on reproducing the flake.** On `NotModified`, the harness should do
what `StartAsync` already does at `GatewayHarness.cs:232`: check whether the written revision is
live, and report accordingly. That removes the flake whether or not anyone ever catches the race.

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

Same assembly, same week, already merged: the background poller's first read is known to race the
test window on Linux CI. That is stronger evidence for this mechanism than anything below, and it
also hands over the reproduction technique — force a real poll inside the window rather than wait.

## Observations, with their denominators

| When | Where | Row |
| --- | --- | --- |
| 2026-10-16, CI run 38849080050 attempt 1 | GitHub runner, Release | `UpstreamResponseTests...(status: 200)` and `UnknownApiTests.An_api_name_another_app_lists_is_still_unlisted_for_this_one` |
| 2026-10-16, local full-solution run | macOS arm64, Release | `UpstreamResponseTests...(status: 500)` |

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

1. **Pin the deterministic half** with a unit test in `tests/Platform.Registry.Tests`: write once,
   `RefreshAsync` twice, assert the second is `NotModified` while `Current.Revision` is the new
   revision. Seconds, no flake.
2. **Fix the harness contract**, which step 1 shows is wrong on its own terms: `UpdateRegistryAsync`
   and `PublishRegistryAsync` should report the outcome of the write they made. Correct the doc
   comment with it.
3. **Only if the race itself still matters**, force it rather than wait: gate the poller's first
   `RefreshAsync` on a `TaskCompletionSource`, or delay after `Task.Yield()`, and run
   `Gateway.SecurityTests` alone. That turns a 1-in-10 observation into 10-in-10.

A printf probe in `PublishRegistryTextAsync` was used to chase this and is **not** recommended: it
costs a full-solution run per attempt for a ~0.43 chance of learning nothing, and a naive one that
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

Reported by the integrator session that landed #34 and #35 on 2026-10-16.

Revised the same day after `/code-review` on PR #36: the deterministic half is now proven rather
than suspected, the failing run is attributed to `55c4599` rather than to the merge of #34, the
`d17839f` corroboration and reproduction technique are recorded, the evidence from eight
non-reproducing runs is given its actual weight, and the probe is withdrawn in favour of the unit
test and the forced race.

Note for triage: another session has an unlanded ticket on the same race, numbered 6.1a against
the harness, reaching the same mechanism independently and marked ready-for-agent. These should be
merged into one before either is worked, and the number collision on 61 resolved.
