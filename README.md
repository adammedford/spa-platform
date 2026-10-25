# Enterprise SPA Platform

An internal-only AWS platform that serves many SPAs from one private S3 bucket, through one
internal ALB and one ASP.NET Core 10 gateway. Each SPA has its own OAuth client, and tokens
stay on the server.

**The gateway is the product.** One process resolves the app from the request host, holds that app's
OAuth client and server-side session, and is the only thing in the platform that handles a browser
request. The Terraform, the fixtures, the test identity provider and the suites all exist to prove
something about it. `docs/roadmap.md` is the path from the mechanism that was proved on a spike to a
release an enterprise could adopt.

| Read | For |
| --- | --- |
| `docs/roadmap.md` | The gateway's capability ladder, what each stage is proved by, and what a mature release still needs |
| `docs/plan/` | The plan: architecture, contracts, tasks, tests, infrastructure, evidence |
| `AGENTS.md` | The rules every coding agent works under, and who owns which path |
| `docs/agents/landing-work.md` | How a task gets from the dispatch table into `main` |
| `docs/dependencies.md` | Tools, package versions, licences, and the decisions behind them, open and closed |
| `spikes/g0/README.md` | The feasibility harness, and the runs of Gate G0 so far |

## Where the gateway stands

Derived from `git tag`, from the merge subjects that name each task, and from the plan's own task
tables, by `tools/roadmap.py`. Never typed by hand: a progress line written into a file read at the
top of every session misleads from the first token, which is why `.github/steering.py` forbids one
in `AGENTS.md` and why this block is generated.

<!-- BEGIN GENERATED summary -->
**Closed gates:** G1a (`skeleton-v1`), G0 (`g0-mechanism`), G1b (`contracts-v1`), G2 (`g2-local-slice`).
**Where the gateway is:** S5 The same gateway on real infrastructure, which closes at gate G3.
**Plan rows landed:** 45 of 62, with 8 part-landed. S5 still owes `1.3`, `1.4`, `1.5`, `1.6b`, `1.7`, `6.5`, `6.9`.
<!-- END GENERATED summary -->

`docs/roadmap.md` has the same figures per stage, and what each stage still owes. For the detail
behind them: `git tag` for gates, `git log --oneline` for what landed, and the dispatch table in
`docs/plan/03-workstreams-and-tasks.md` read against both for what opens next.

## First steps on a workstation

```bash
tools/preflight.sh        # 1. what this machine is missing
brew bundle               # 2. install the tools in the Brewfile, or only the missing ones
git config core.hooksPath .githooks   # 3. once per clone: the pre-push checks
dotnet build              # 4. the whole solution, with no warnings
```

Then the tests, and the gateway's own exit test for Gate G1a:

```bash
dotnet test --solution SpaPlatform.slnx --ignore-exit-code 8   # 8 = a project with no tests yet
tools/smoke.sh Debug 5180                                      # starts, and 404s an unknown host
```

The first item of Gate G0, the real handler from the shipped package, runs on its own:

```bash
cd spikes/g0/real-handler/RealOidcNuGet
dotnet run -c Release     # expect the last line to start "32 expectations held, 0 did not"
```

## Layout

```text
src/            the gateway and its libraries, one project per owner
tests/          component tests beside each owner, and the composed suites
fixtures/       two sample SPAs and a third for onboarding, the mock API, the test identity provider
infra/          Terraform: bootstrap, modules, and the three roots of the proof of concept
tools/          spactl, the scripts, preflight.sh, smoke.sh and roadmap.py
contracts/      schemas and written contracts, frozen at G1a and G1b
spikes/g0/      the feasibility harness, outside the solution
docs/           the roadmap, the plan, dependencies, decision records, runbooks
```

`AGENTS.md` and `OWNERSHIP.md` have the owner of every path, and `.githooks/pre-push` enforces it as
check A-05.
