# Read test JSON without the file provider, and drop the hidden-file skip

Status: moved to #75
Type: task
Owners: workstream 2 (`tests/Platform.Registry.Tests`), workstream 6 (`tests/Gateway.*`)

## Problem

Agent worktrees live under `.claude/worktrees/`, and macOS marks every file there `hidden`
(`ls -lO`). .NET's `PhysicalFileProvider` excludes hidden files by default, so `AddJsonFile`
throws `FileNotFoundException` and four Registry tests fail in every isolated agent run:

- `RegistrationTests.The_object_store_is_s3_over_the_registry_bucket`
- `RegistrationTests.Platform_policy_is_read_from_configuration`
- `RegistryPollerTests.The_poller_reads_at_start_and_again_after_one_interval`
- `RegistryPollerTests.A_poll_that_throws_does_not_stop_the_host_and_the_next_one_runs`

They pass 142/142 from the main checkout and on CI's Linux runner. Each agent spent effort
diagnosing them, and task 6.1 (PR #16) answered the same cause with an `Assert.Skip` in the test
that proves the gateway reads `src/Gateway/appsettings.json`, which turns it into a pass that
checked nothing on a Mac. `CODING_STANDARDS.md` -> "An assertion must be able to fail" now names
that shape.

## Done when

- Workstream 2: `tests/Platform.Registry.Tests/RegistrationTests.cs` (lines 71 and 88) loads the
  example with `AddJsonStream(File.OpenRead(path))`, or an equivalent that bypasses the provider's
  exclusion filters. The four tests pass inside a `.claude/worktrees/` checkout.
- Workstream 6: the `appsettings.json` test in the harness has no `Assert.Skip`, and passes in a
  `.claude/worktrees/` checkout. If the gateway's own host loads `appsettings.json` through the
  provider, the test sets a content root or copies the file so the provider sees it; it does not
  change `src/Gateway` (integrator path).
- Each lands in its own PR, one owner per diff (A-05).

## Comments

- 2026-10-17: moved to #75 under ADR-0020; this file is the archive copy.
