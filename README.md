# Enterprise SPA Platform

An internal-only AWS platform that serves many SPAs from one private S3 bucket, through one
internal ALB and one ASP.NET Core 10 gateway. Each SPA has its own OAuth client, and tokens
stay on the server.

**The gateway is the product.** One process resolves the app from the request host, holds that app's
OAuth client and server-side session, and is the only thing in the platform that handles a browser
request. The Terraform, the fixtures, the test identity provider and the suites all exist to prove
something about it. `docs/roadmap.md` is the path from the mechanism that was proved on a spike to a
release an enterprise could adopt.

Licensed under the Apache License, Version 2.0: see [LICENSE](LICENSE).

## Where the gateway stands

Written by `tools/roadmap.py` from `git tag`, the merge subjects that name each task and the plan's
own task tables, and never typed by hand.

<!-- BEGIN GENERATED summary -->
**Closed gates:** G1a (`skeleton-v1`), G0 (`g0-mechanism`), G1b (`contracts-v1`), G2 (`g2-local-slice`), G3 (`g3-deployed-slice`), G4 (`g4-negative-green`), G5 (`g5-phase-1-accepted`), G6 (`g6-continuity`).
**Where the gateway is:** S9 Portable: a second provider behind the same seam, which closes at gate G7.
**Plan rows landed:** 48 of 62, with 8 part-landed. S9 still owes `P2-6`.
<!-- END GENERATED summary -->

`docs/roadmap.md` has the same figures per stage, and what each stage still owes. For the detail
behind them: `git tag --merged origin/main` for the gates and `git log --oneline origin/main` for
what landed. What opens next is the dispatch table in `docs/plan/03-workstreams-and-tasks.md` and
the package table in `docs/plan/06-phase-2-and-risks.md`, read against those tags and merges; a gate
defined after the plan was exported is in the gate register, `contracts/gates.md`.

## Get it running

```bash
tools/preflight.sh                    # 1. what this machine is missing
brew bundle                           # 2. install the tools in the Brewfile, or only the missing ones
git config core.hooksPath .githooks   # 3. once per clone: the pre-push checks
dotnet build                          # 4. the whole solution: warnings are errors, package advisories warn
```

Then the tests, the gateway's own exit test for Gate G1a, and the checks the hook from step 3 runs:

```bash
dotnet test --solution SpaPlatform.slnx --ignore-exit-code 8   # every test project; 8 = a project with no tests
tools/smoke.sh Debug 5180                                      # starts, and 404s an unknown host
tools/check.sh                                                 # the pre-push hook's checks, by hand
```

`AGENTS.md` → "Build and test" has the rest: one test project on its own, the full check, and the
rules the build holds a change to.

## Where to read

| Read | For |
| --- | --- |
| `docs/roadmap.md` | The gateway's capability ladder, what each stage is proved by, and what a mature release still needs |
| `docs/plan/` | The plan as exported: architecture, contracts, tasks, tests, infrastructure, Phase 2 and risks, evidence |
| `contracts/` | The schemas and written contracts the workstreams build against, and the registers of test ids and gates added since the export |
| `docs/adr/README.md` | The index of the decision records |
| `GLOSSARY.md` | The project's vocabulary, and which synonym to avoid |
| `docs/dependencies.md` | Tools, package versions, licences, and the decisions behind them, open and closed |
| `docs/gaps.md` | The known gaps between what Phase 1 delivers and what an enterprise adoption needs |
| `docs/runbooks/README.md` | The operations an operator performs on a cell |
| `AGENTS.md` | The rules every coding agent works under, and who owns which path |
| `OWNERSHIP.md` | The owner of every path, which `.githooks/pre-push` enforces as check A-05 |
| `CODING_STANDARDS.md` | What a review holds a change to |
| `docs/agents/landing-work.md` | How a task gets from the dispatch table into `main` |
| `docs/agents/tracker-policy.md` | How tickets and specs live as GitHub issues on this repository |
| `spikes/g0/README.md` | The feasibility harness behind Gate G0, how to run it, and what its runs found |
