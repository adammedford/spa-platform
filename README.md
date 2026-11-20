# Enterprise SPA Platform

An internal-only AWS platform that serves many SPAs from one private S3 bucket, through one
internal ALB and one ASP.NET Core 10 gateway. Each SPA has its own OAuth client, and tokens
stay on the server.

**The gateway is the product.** One process resolves the app from the request host, holds that app's
OAuth client and server-side session, and is the only thing in the platform that handles a browser
request. The Terraform, the fixtures, the test identity provider and the suites all exist to prove
something about it. `docs/roadmap.md` is the path from the mechanism that was proved on a spike to a
release an enterprise could adopt.

## Where the gateway stands

Written by `tools/roadmap.py` from `git tag`, the merge subjects that name each task and the plan's
own task tables, and never typed by hand.

<!-- BEGIN GENERATED summary -->
**Closed gates:** G1a (`skeleton-v1`), G0 (`g0-mechanism`), G1b (`contracts-v1`), G2 (`g2-local-slice`), G3 (`g3-deployed-slice`), G4 (`g4-negative-green`), G5 (`g5-phase-1-accepted`), G6 (`g6-continuity`).
**Where the gateway is:** S8a Composed: one host, many clients, which closes at gate G9.
**Plan rows landed:** 66 of 75, with 1 part-landed. S8a still owes `issue-993`, `issue-995`, `issue-1007`.
<!-- END GENERATED summary -->

`docs/roadmap.md` has the same figures per stage, and what each stage still owes. For the detail
behind them: `git tag --merged origin/main` for the gates and `git log --oneline origin/main` for
what landed. What opens next is the dispatch table in `docs/plan/03-workstreams-and-tasks.md` and
the package table in `docs/plan/06-phase-2-and-risks.md`, read against those tags and merges; a gate
defined after the plan was exported is in the gate register, `contracts/gates.md`.

## Development

The first of the repository's three modes: developing the gateway. The other two are proving it in
this project's own cell and adopting it, and the path table below gives every tracked path its mode.

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

**The tests are the development loop.** Every behaviour of the gateway is proved in the hermetic
suites, against the test identity provider, the mock API and an in-memory bucket, and there is no
development server to run it against instead. `AGENTS.md` → "Build and test" has the rest: one test
project on its own, the full check, and the rules the build holds a change to.

To see the hermetic cell work outside a test, from a clone and with no AWS, follow
[`tests/e2e/README.md`](tests/e2e/README.md); a run made that way is never evidence.

## Proof cell

The proof cell is this project's own cell: `infra/envs/poc`, its Cognito pool and the fixtures
deployed into it. It exists to prove the gateway on AWS, where the gates that need a real load
balancer, bucket and identity provider are closed, and an adopter takes nothing from it.
`docs/runbooks/README.md` lists the operations performed on it.

## Adoption

**An adopter takes the unit of adoption as it stands.** It is three artefacts, all built from one
ref of an unmodified clone, never from a fork:

- the gateway image, built from `src/Gateway/Dockerfile`;
- the contracts that the list in `contracts/README.md` names as the unit's;
- `tools/spactl`, which publishes a release and the registry.

**An adopter may copy and change the references:** the cell module `infra/modules/cell`, the
operator scripts under `tools/scripts/`, the runbooks under `docs/runbooks/`, and the browser client
under `client/`. Nothing promises that a reference stays as it is between refs.

Nothing else is taken. To see the gateway work before adopting it, without AWS, run the hermetic
cell from a clone, as `tests/e2e/README.md` shows.

Licensed under the Apache License, Version 2.0: see [LICENSE](LICENSE).

## The path table

The gateway image has no path of its own: it is built from `src/Gateway/Dockerfile`, and `src/`
stays development. Every other tracked path takes one value from the table below.
`tools/mode_split.py` reads the table and the list in `contracts/README.md`, and fails a tracked
path that has no value or two; its docstring gives the table's format.

<!-- BEGIN path values -->
| Path | Value |
| --- | --- |
| `src/`, `tests/`, `spikes/`, `fixtures/`, `tools/bench/` | Development |
| The root build files: `SpaPlatform.slnx`, `Directory.*.props`, `global.json`, `nuget.config`, `BannedSymbols.*`, `.globalconfig`, `.editorconfig`, `Brewfile`, `.dockerignore`, `.hadolint.yaml`, `.terraform-version` | Development |
| `docs/adr/`, `docs/roadmap.md`, `docs/gaps.md`, `docs/dependencies.md`, `GLOSSARY.md`, `CODING_STANDARDS.md`, `README.md`, `LICENSE` | Development |
| The files of `contracts/` its list names as the project's own: `README.md`, `pipeline.md`, `request-gate.md`, `resource-tags.md`, `gates.md`, `test-ids.md`, `changes/` | Development |
| `infra/envs/poc`, `infra/bootstrap`, `infra/modules/idp-cognito`, `tools/scripts/poc/`, `docs/runbooks/poc/`, `tools/aws-project*.env` | Proof cell |
| `tools/spactl`, and the files of `contracts/` its list names as the unit's | Unit of adoption |
| `infra/modules/cell`, `tools/scripts/`, `docs/runbooks/`, `client/` | Reference |
| Every other file directly in `tools/`, `.claude/`, `.githooks/`, `.github/`, `.scratch/`, `docs/agents/`, `docs/plan/`, `docs/research/`, `AGENTS.md`, `CLAUDE.md`, `OWNERSHIP.md`, `.gitignore`, `.gitattributes` | Process |
<!-- END path values -->

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
| `OWNERSHIP.md` | The owner of every path, and owners only, which `.githooks/pre-push` enforces as check A-05 |
| `CODING_STANDARDS.md` | What a review holds a change to |
| `docs/agents/landing-work.md` | How a task gets from the dispatch table into `main` |
| `docs/agents/tracker-policy.md` | How tickets and specs live as GitHub issues on this repository |
| `spikes/g0/README.md` | The feasibility harness behind Gate G0, how to run it, and what its runs found |
