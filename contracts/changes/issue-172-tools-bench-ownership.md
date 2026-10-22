# Change request issue-172: workstream 6 owns `tools/bench/`

- **Raised by:** the integrator lane of the 2026-10-21 overnight run, for #172 under the map
  "Wayfinder: gateway startup benchmark, floor and budget" (#169)
- **Tasks:** none in the plan. #169's harness and floor-artifact tickets, which write under
  `tools/bench/`; #172, which files this request
- **Date:** 2026-10-21
- **Status:** decided, 2026-10-22, on https://github.com/adammedford/spa-platform/issues/246. See the
  Decision section below.

## What is missing

Charting #169 decided, on 2026-10-19, that the gateway startup benchmark's repeatable harness and its
reference **startup floor** artifact live under `tools/bench`, owned by workstream 6, verification
and operations. The map's Destination says so: "a repeatable harness under `tools/bench` owned by
workstream 6".

No line gives workstream 6 that path. `OWNERSHIP.md` opens with `*  integrator`, and the last
matching line wins, so today every file under `tools/` that no later line names is the integrator's.
Two later lines carve out `/tools/scripts/` (ws1-infra) and `/tools/spactl/` (ws2-hosting); nothing
carves out `/tools/bench/`. A workstream 6 branch that adds the harness would touch an integrator
path, and A-05 (`.github/path-ownership.py`, run by `.githooks/pre-push` through `tools/check.sh`)
refuses a linear branch whose diff crosses two owners' sets, so the harness could not land beside the
rest of workstream 6's work.

`AGENTS.md` -> Who owns what repeats the split in prose, so it needs the same path, or the table a
session reads first contradicts the block the check enforces.

## What is asked of the integrator

Two edits in files AGENTS.md reserves to the integrator.

1. **`OWNERSHIP.md`**, in the block under `# 6 Verification and operations`, one line in the
   block's column layout:

   ```text
   /tools/bench/                         ws6-verification
   ```

   Its place in the block does not change what it matches: `/tools/bench/` is more specific than
   `*`, and no other line names a path under it.

2. **`AGENTS.md`** -> Who owns what, the workstream 6 row gains `` `tools/bench/` ``:

   ```markdown
   | 6 Verification and operations | `src/Platform.Telemetry`, its test project, `tests/Gateway.*`, `tests/e2e`, `tools/bench/`, `docs/runbooks/`, `docs/gaps.md` |
   ```

Both files are steering documents (ADR-0023 decision 2), so the commit that edits them ends with an
`Authority:` trailer (ADR-0023 decision 3; `docs/agents/landing-work.md` has the forms). One commit
for both keeps the block and the table from disagreeing for a commit.

## What this does not change

- **No contract, project or package.** The harness is outside `SpaPlatform.slnx`; if it later needs
  a project or a package, that is its own request.
- **The checks need nothing new for the path itself.** `tools/check.sh` already finds a script's
  test companion (`<name>_tests.py` or `<name>-tests.py`) at any depth under `tools/`, so a companion
  beside a harness script under `tools/bench/` runs in the full check when the diff touches it.
  `.github/touches.sh` puts `tools/bench/` in neither half, as it does `tools/scripts/`, which is
  right for a harness that builds nothing in the solution. Whether CI runs the harness itself, on
  every change, at gates or on a schedule, is open on #169 ("Not yet specified") and is not asked
  here; a CI step for the harness's companion, in the shape of
  `changes/issue-122-ci-runs-the-verify-companion.md`, would be a later request once a companion
  exists.
- **The plan export.** `docs/plan/` is read-only and does not mention `tools/bench`; #169 records
  its decisions in its ADR, and the plan is re-exported later.

## Why this is a change request and not an edit

`OWNERSHIP.md` and `AGENTS.md` are the integrator's and are steering documents. #172 asks for the
request, not the edit: the integrator decides and edits.

## Blocking

**Yes, for #169's harness tickets only.** A workstream 6 branch cannot add `tools/bench/` past A-05
until item 1 lands. Nothing else in the repository waits on it, and no test does.

## Decision

Integrator, 2026-10-22, on Adam's answer on https://github.com/adammedford/spa-platform/issues/246:
**workstream 6 owns `tools/bench/`, as asked.** He chose this over keeping the floor project with
the integrator and over declining the request.

1. **Both edits land in one commit**, this one: `OWNERSHIP.md` gains `/tools/bench/` under
   workstream 6, and `AGENTS.md` -> Who owns what gains `tools/bench/` in the workstream 6 row.
2. **The floor project goes with the path.** `tools/bench/floor/Bench.Floor.csproj` is workstream
   6's. `SpaPlatform.slnx`, which lists it, stays the integrator's, so adding or removing a project
   under `tools/bench/` is still a change request.

Two lines of "What this does not change" were made false by #173 before this decision, as the
comment of 2026-10-21 on #246 records:

- "The harness is outside `SpaPlatform.slnx`" no longer holds. `SpaPlatform.slnx` lists
  `tools/bench/floor/Bench.Floor.csproj`, because `Gateway.ArchitectureTests` requires every
  `.csproj` outside `spikes/` to be listed.
- "`.github/touches.sh` puts `tools/bench/` in neither half" holds only in part. The floor's
  `Program.cs` and `Bench.Floor.csproj` match the build half's extension pattern. `startup.py`, the
  floor's `Dockerfile` and `results/` match neither half.

This unblocks #177, whose ADR records the harness's location and owner, and lets the verdict mode
#176 Q7 assigns to workstream 6 land as a workstream 6 branch.
