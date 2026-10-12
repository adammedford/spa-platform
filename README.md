# Enterprise SPA Platform

An internal-only AWS platform that serves many SPAs from one private S3 bucket, through one
internal ALB and one ASP.NET Core 10 gateway. Each SPA has its own OAuth client, and tokens
stay on the server.

This is the repository skeleton, task I-1 of the plan. It holds no product code yet.

| Read | For |
| --- | --- |
| `docs/plan/` | The plan: architecture, contracts, tasks, tests, infrastructure, evidence |
| `AGENTS.md` | The rules every coding agent works under, and who owns which path |
| `docs/dependencies.md` | Tools, package versions, licences and two open decisions |
| `spikes/g0/README.md` | The feasibility harness, and the runs of Gate G0 so far |

## First steps on a workstation

```bash
tools/preflight.sh        # 1. what this machine is missing
brew bundle               # 2. install the tools in the Brewfile, or only the missing ones
git init -b main          # 3. the first commit is yours to make
dotnet build              # 4. expect 27 projects and no warnings
```

Then run the first item of Gate G0, the real handler from the shipped package:

```bash
cd spikes/g0/real-handler/RealOidcNuGet
dotnet run -c Release     # expect the last line to start "32 expectations held, 0 did not"
```

## State of the skeleton

Task I-1 is done. On 11 October 2026 `dotnet build` passed on an Apple silicon Mac with
.NET SDK 10.0.401: 27 projects, restored from nuget.org, no warnings.

| Part of task I-1 | State |
| --- | --- |
| Solution, 27 projects, reference rules | Built |
| Test projects on xUnit v3 | The 14 test projects built as executables on `xunit.v3` 4.0.2 and Microsoft Testing Platform 2.5.1. `dotnet test` has not been run yet. |
| Central package versions | `xunit.v3` is restored. The other 17 are confirmed to exist on nuget.org and first restore when a project references them. |
| `AGENTS.md`, `CODEOWNERS` | Written. The handles in `CODEOWNERS` are placeholders for real teams. |
| Git | Not initialised |

Three of Gate G0's five items passed the same day: the real handler, token management and
YARP, each from its shipped package. Cognito remains. `spikes/g0/README.md` has the details.

## Layout

```text
src/            the gateway and its libraries, one project per owner
tests/          component tests beside each owner, and the composed suites
fixtures/       two sample SPAs and a third for onboarding, the mock API, the test identity provider
infra/          Terraform: bootstrap, modules, and the three roots of the proof of concept
tools/          spactl, the scripts, and preflight.sh
contracts/      schemas and written contracts, from task I-2
spikes/g0/      the feasibility harness, outside the solution
docs/           the plan, dependencies, decision records, runbooks
```

`AGENTS.md` has the owner of every path.
