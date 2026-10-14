# Agent guide

This repository builds the Enterprise SPA Platform: one internal ALB, one ASP.NET Core 10
gateway and one private S3 bucket that serve many SPAs, each with its own OAuth client.
Tokens stay on the server.

The plan is in `docs/plan/`. Read the part for your task before you write anything.

## Where to look

| You need | Read |
| --- | --- |
| Architecture, gates, decisions, assumptions | `docs/plan/01-plan.md` |
| Interfaces, schemas, the request pipeline order | `docs/plan/02-contracts.md`, and `contracts/` once task I-2 has landed |
| Your task: what, needs, done when | `docs/plan/03-workstreams-and-tasks.md` |
| Test ids (`F-`, `N-`, `P-`, `A-`) | `docs/plan/04-tests.md` |
| Terraform roots, scripts, deployment, cost | `docs/plan/05-infrastructure-and-cost.md` |
| Evidence ids (`S-`, `R-`) and the mechanism's source | `docs/plan/07-evidence.md` |
| Tools, package versions, licences, open decisions | `docs/dependencies.md` |

## Rules for every task

- **Write only inside your owner's paths.** Reading anywhere is fine. `OWNERSHIP.md` lists the paths,
  and `.githooks/pre-push` checks them. How a task lands: `docs/agents/landing-work.md`.
- **The integrator's paths are off limits**, even for a one-line fix. The one exception is adding a change request.
- **Need a contract changed?** Add `contracts/changes/<task-id>-<slug>.md` describing the change, and stop. The integrator decides and edits.
- **Need a new project, a new package or a different version?** Same route. `SpaPlatform.slnx` and `Directory.Packages.props` are the integrator's.
- **Build against `Platform.Testing`**, not against another workstream's branch.
- **Fill in the entry points the pipeline contract assigns to you, and add no others.** `Program.cs` calls them.
- **A test id under "Done when" names a behaviour.** Prove it in your own test project, against the doubles. Workstream 6 proves it again in the composed suites.
- **A task is done** when its tests pass in CI, it touched only its own paths, and it left no change request open.

## Who owns what

| Owner | Paths |
| --- | --- |
| Integrator | Everything not listed below: root files, `.github/`, `contracts/`, `docs/adr/`, `docs/plan/`, `docs/dependencies.md`, `src/Platform.Abstractions`, `src/Platform.Testing`, `src/Gateway`, their two test projects, `tools/preflight.sh` |
| 1 AWS and IaC | `infra/`, `tools/scripts/` |
| 2 Hosting and registry | `src/Platform.Registry`, `src/Platform.Hosting`, `tools/spactl`, and their test projects |
| 3 OAuth and sessions | `src/Platform.Identity`, its test project, `spikes/g0/` |
| 4 API and security | `src/Platform.Proxy`, `src/Platform.Security`, and their test projects |
| 5 Fixtures | `fixtures/` |
| 6 Verification and operations | `src/Platform.Telemetry`, its test project, `tests/Gateway.*`, `tests/e2e`, `docs/runbooks/`, `docs/gaps.md` |

A component test project is `tests/<Project>.Tests` and belongs to the owner of `<Project>`.

## Build and test

```bash
tools/preflight.sh                              # what this machine is missing
dotnet build                                    # the whole solution
dotnet test --solution SpaPlatform.slnx --ignore-exit-code 8   # every test project; 8 = a project with no tests
dotnet test --project tests/Platform.Registry.Tests
tools/smoke.sh Debug 5180                       # G1a's exit test, after a build
```

- **Tests run on Microsoft Testing Platform**, set in `global.json`. Name what to run with `--solution` or `--project`: both forms work on every .NET 10 SDK.
- **A test project with no tests reports exit code 8**, the platform's "zero tests" code. On the empty skeleton that is expected, and `--ignore-exit-code 8` suppresses it.
- **A project named `*Tests` is a test project.** `Directory.Build.props` makes it an executable and adds xUnit v3. Do not add xUnit yourself.
- **Packages:** add `<PackageReference Include="Name" />` with no version to your own project file. The version comes from `Directory.Packages.props`.
- **Warnings are errors.** Package advisories are the exception: they warn.
- **`spikes/` is outside the solution** and does not inherit the solution's build settings.
- **Reach the gateway over a raw socket when the `Host` header matters.** In an agent sandbox, HTTP
  through `curl` is proxied and answered on the `Host` header, so a spoofed host never reaches the
  process: it returns 400 even with nothing listening. Everything here routes by host, so test it
  with a socket that sends the request line and `Host` itself. CI is a plain Linux runner and does
  not proxy, so `curl` is right there.

## Rules that hold everywhere

- **Never log a credential, token, cookie, authorization code or state value.** Test N-16 searches every log line for canary values.
- **No custom cryptography and no hand-written OAuth protocol code** where a supported library does the job. The test identity provider in `fixtures/TestIdp` is the one bounded exception.
- **The gateway never references `fixtures/TestIdp`** (A-01), and the mock API references no `Platform.*` project (A-02).
- **A workstream project references `Platform.Abstractions` and no other workstream's project** (A-03). Only `Gateway` references them all.
- **Only `Platform.Identity` references the token-management package** (A-04).

## Where the work stands

Read it, do not take it from here. This file does not track progress, because a copy of the state
goes stale and then misleads every session that loads it.

- **Which gates have closed:** `git tag`. Each annotated tag names the gate and the CI run that
  proves its exit test.
- **What has landed:** `git log --oneline`, where each task's merge names its id.
- **What opens next:** the dispatch table in `docs/plan/03-workstreams-and-tasks.md`, read against those
  tags.
- **Which gate is open:** the gates table in `docs/plan/01-plan.md`, read against `git tag`.

One rule does not depend on progress, so it is safe to state here: nothing in
`src/Platform.Identity` starts before task I-6 tags `contracts-v1`, because everything identity is
draft until then. `contracts/README.md` -> "What is draft until G1b" lists what that covers.
