# Dependencies

What a workstation, the AWS account and the build need, in the order the tasks need them.
Checked on 11 October 2026. `tools/preflight.sh` reports which of these a machine is missing.

## Needed on day one

Day one is tasks I-1, I-2, 1.0, 1.6a and 5.4, plus the first G0 run in `spikes/g0`.

| Dependency | Offered today | Install on macOS | First needed by | Note |
| --- | --- | --- | --- | --- |
| Git | 2.38+ (`merge-tree --write-tree` for `tools/prune_branches.py`; `--remerge-diff`, 2.36, for A-05 merge audits) | `xcode-select --install` | Everything |  |
| Python 3.9 or later | 3.9.6, with macOS's developer tools | `xcode-select --install` | The checks in `.githooks/pre-push` and CI, their test companions, `tools/land.sh` and `tools/roadmap.py` | Kept 3.9-compatible with `from __future__ import annotations`; no CI job runs 3.9 (D-3) |
| GitHub CLI, signed in | 2.102.0 | `brew install gh`, then `gh auth login` | Every landing, and the tracker, which is GitHub Issues | `tools/land.sh` refuses to start without gh on PATH, and `tools/preflight.sh` counts gh as missing until it is signed in (#96) |
| .NET SDK 10.0 | SDK 10.0.401, runtime 10.0.12 (8 September 2026) | `brew install --cask dotnet-sdk` | I-1, I-2, 5.4, G0 | `global.json` accepts any 10.0 SDK |
| Access to nuget.org | n/a | n/a | Every build | See "Where agents can run" |
| Terraform 1.11 or later | 1.15.8 | `brew install hashicorp/tap/terraform` | 1.0 | The state backend uses the S3 lock file, which the plan requires 1.11 for. This is the `terraform` an apply runs |
| The Terraform floor, 1.11.0 | n/a | `tools/preflight.sh` prints the line, through `hc-install` (`brew install hashicorp/tap/hc-install`) | The local check, on a Terraform change | Below, under "The Terraform floor" |
| AWS CLI v2 | 2.37.12 | `brew install awscli` | 1.0, 1.6a |  |
| AWS account and credentials | n/a | `aws configure sso` or `aws configure` | 1.0 | Rights to create the plan's resources. The plan prices us-east-1. |
| openssl | macOS ships one | n/a | 1.6a | `certs.sh` makes the local certificate authority with it |
| Admin rights on the workstation | n/a | n/a | 1.6a, G0 | To trust the local authority in the keychain and to edit `/etc/hosts` |

## Needed later

| Dependency | Offered today | Install on macOS | First needed by | Note |
| --- | --- | --- | --- | --- |
| Session Manager plugin | 1.2.835.0 | `brew install --cask session-manager-plugin` | 1.6b | The tunnel to the internal ALB |
| Container engine with ARM64 builds | Docker Desktop 4.94.0 | `brew install --cask docker-desktop` | 1.7 | Decision D-2. `deploy.sh` builds the gateway and mock API images |
| Chromium for Playwright | Comes with Microsoft.Playwright 1.63.0 | Installed by the test suite | 5.2, 6.5 | Playwright needs macOS 14 or later. See the note below. |
| GitHub repository with Actions | n/a | n/a | I-5 | The plan's default CI. It needs a read-only AWS role to run Terraform plans, which task 1.0 creates. |
| A Cognito domain prefix | n/a | n/a | 1.1 | Must be unique in the region |

**Playwright's browser installer.** The documented step is `pwsh bin/Debug/net10.0/playwright.ps1 install`, which needs PowerShell. That script only calls `Microsoft.Playwright.Program.Main`. If the suite makes the same call, PowerShell stays off this list. Neither route has been tried here.

## Where agents can run

- **Any agent that builds or tests needs nuget.org.** The two sandboxes used to write this skeleton, Claude's cloud container and the sandbox on the Mac, reach GitHub but not nuget.org. Run building agents on the workstation itself, or in an environment whose network allowlist includes `api.nuget.org`.
- **Three kinds of run need AWS credentials:** every Terraform apply, G0, and the deployed test runs. CI needs a read-only role for plans. Workstreams 2, 4 and 5 need no AWS access.

## NuGet packages

All pinned in `Directory.Packages.props`. Each pinned version was confirmed to exist on nuget.org, and the licence is the one its package file declares. For the three AWS SDK packages the pin may not be the newest patch.

| Package | Version | Licence | Expected in | Note |
| --- | --- | --- | --- | --- |
| Microsoft.AspNetCore.Authentication.OpenIdConnect | 10.0.12 | MIT | `Platform.Identity`, and `Platform.Abstractions` since task I-3 | Depends on IdentityModel 8.19.2, the version the G0 source build ran on. `IIdpProfile` is written in terms of `OpenIdConnectOptions` and the handler's `RedirectContext`, so the contract cannot be expressed without it; every project referencing `Platform.Abstractions` therefore carries it transitively. It is the stock handler, not the token-management package A-04 restricts. |
| Duende.AccessTokenManagement.OpenIdConnect | 4.2.0 | Apache-2.0 | `Platform.Identity` only (A-04) | Has a `net10.0` build. Brings Duende.IdentityModel 8.1.0, HybridCache and Http.Resilience. G0 item 4 passed on it on 11 October 2026. |
| Duende.IdentityModel | 8.1.0 | Apache-2.0 | Fallback for G0 item 4, which passed without it | Already a transitive dependency of the package above |
| Yarp.ReverseProxy | 2.3.0 | MIT | `Platform.Proxy` | The newest release. Its newest target is `net8.0`; G0 item 5 ran it on .NET 10.0.12 on 11 October 2026. |
| AWSSDK.S3 | 4.0.101.6 | Apache-2.0 | `Platform.Registry`, `spactl` |  |
| AWSSDK.SimpleSystemsManagement | 4.0.104.2 | Apache-2.0 | `Platform.Registry` | Parameter Store |
| AWSSDK.Signin | 4.0.101.13 | Apache-2.0 | `spactl` | The project's profile signs in with `login_session`, which the AWS CLI and Terraform resolve on their own and the .NET SDK resolves only with this assembly; without it every provider in the credential chain fails with `Assembly AWSSDK.Signin could not be found or loaded` (#206). Its `AWSSDK.Core` floor is 4.0.102.9, the Core the solution already resolves, so it moves no other version. It brings `BouncyCastle.Cryptography` 2.6.2 (MIT) in transitively, a package new to the solution. |
| OpenTelemetry.Extensions.Hosting | 1.19.1 | Apache-2.0 | `Platform.Telemetry` |  |
| OpenTelemetry.Instrumentation.AspNetCore | 1.19.0 | Apache-2.0 | `Platform.Telemetry` |  |
| OpenTelemetry.Instrumentation.Http | 1.19.0 | Apache-2.0 | `Platform.Telemetry` |  |
| OpenTelemetry.Exporter.OpenTelemetryProtocol | 1.19.1 | Apache-2.0 | `Platform.Telemetry` |  |
| OpenTelemetry.Exporter.InMemory | 1.19.1 | Apache-2.0 | Telemetry tests | F-11 |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.12 | MIT | `fixtures/MockApi` |  |
| Microsoft.IdentityModel.JsonWebTokens | 8.19.2 | MIT | `fixtures/TestIdp` | Signing for the test identity provider |
| xunit.v3 | 4.0.2 | Apache-2.0 | Every `*Tests` project | Added by `Directory.Build.props`. Runs on Microsoft Testing Platform v2. |
| xunit.v3.extensibility.core | 4.0.2 | Apache-2.0 | `Platform.Testing` | The attributes without the runner. xUnit refuses `xunit.v3` on a library that is not an executable and names this instead. Already a transitive dependency of `xunit.v3`. |
| xunit.v3.assert | 4.0.2 | Apache-2.0 | `Platform.Testing` | `Assert`, for the store contract suites that each implementation subclasses (task 3.4). Same 4.0.2 set. |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.12 | MIT | `Gateway.TestHarness` |  |
| Microsoft.Extensions.TimeProvider.Testing | 10.10.0 | MIT | `Platform.Testing`, `Fixtures.Tests` | The fake clock |
| Microsoft.Playwright | 1.63.0 | MIT | `tests/e2e`, `Fixtures.Tests` |  |
| JsonSchema.Net | 8.0.5 | MIT | Schema tests, `Fixtures.Tests`, `Platform.Registry` (the gateway and `spactl`) | Decision D-1, decided |

The skeleton adds only `xunit.v3` to projects. Each owner adds the other references to their own project files as the work needs them.

## Terraform

| Item | Version | Note |
| --- | --- | --- |
| Terraform CLI | 1.15.8 | The plan needs 1.11 or later |
| Provider `hashicorp/aws` | 6.68.0, published 7 October 2026 | Constrain to `~> 6.0`. MPL-2.0 |
| Provider `hashicorp/random` | 3.9.1 | Constrain to `~> 3.7`. MPL-2.0. Task 1.1's test-user passwords, in `infra/modules/idp-cognito` and `infra/envs/poc/idp` only. Checked 13 October 2026 by `terraform init`, which resolved `~> 3.7` to 3.9.1 |

The AWS provider documents both Cognito features task 1.8 relies on: the `refresh_token_rotation` block on `aws_cognito_user_pool_client`, and the `aws_cognito_managed_login_branding` resource.

### The Terraform floor

The floor is the oldest Terraform the repository supports: 1.11.0. The S3 backend's native lock
file first shipped in 1.10.0, and 1.11.0 made it generally available and deprecated the DynamoDB
lock ([1.11.0's changelog](https://github.com/hashicorp/terraform/blob/v1.11.0/CHANGELOG.md)). One file,
`.terraform-version`, names it, and three readers share one function for it,
`tools/terraform-floor.sh` (#69):

- **CI's Terraform job** installs exactly that version through `hashicorp/setup-terraform`.
- **`tools/check.sh`**, the local check, runs it on a Terraform change when this machine has it,
  and otherwise runs the `terraform` on PATH and names that version in a warning and its last line.
- **`tools/preflight.sh`** says whether this machine has it, and prints the line that installs it.

It is installed at `~/.local/share/terraform/<version>/terraform`, one copy per machine for every
clone and worktree, and outside PATH, so the `terraform` an apply runs is still whatever the person
chose. HashiCorp's `hc-install` 0.10.0 puts it there, from the tap the Brewfile already names, and
verifies the release's checksums and signature ([its README](https://github.com/hashicorp/hc-install#cli)).
A `terraform` on PATH that reports the floor counts too, so a version manager that reads
`.terraform-version`, such as tfenv or tfswitch, satisfies it without that directory.

Raising the floor is a one-line change to `.terraform-version`, plus the `required_version` line
in each root and module under `infra/`, which workstream 1 owns.

## Decided

### D-1: the JSON Schema validator. JsonSchema.Net 8.0.5, in the gateway

Decided by the integrator on 14 October 2026, on change request 2.1. The plan asks for "a JSON Schema validator" and names none. The usual choice is JsonSchema.Net. Its current package, 9.4.0, ships under a maintenance-fee licence: any user that generates revenue must pay a monthly fee to use the binaries. The 8.0.5 package still declares MIT.

| Option | Licence | Trade-off |
| --- | --- | --- |
| **JsonSchema.Net 8.0.5, pinned** | MIT | No fee. An older release line, so fixes may stop reaching it. **Chosen.** |
| JsonSchema.Net 9.4.0 | Maintenance fee | Current and supported. Needs a paid sponsorship once the platform is in commercial use. |
| NJsonSchema 11.6.1 | MIT | Widely used. Brings Newtonsoft.Json with it. |
| LateApexEarlySpeed.Json.Schema 4.2.0 | BSD-3-Clause | Built on System.Text.Json, covers draft 2020-12. One maintainer. |

The engine runs in the gateway process, through `src/Platform.Registry`, and the earlier recommendation to keep it out is rejected. `contracts/README.md` -> "Rules that JSON Schema cannot express" makes the one shared library the guarantee that the gateway and `spactl` cannot disagree, and several security rules live only in the schema: the source `'*'` and `'unsafe-eval'` in every content security policy directive, `'unsafe-inline'` and `data:` in `script-src`, `additionalProperties: false`, and the `host`, `upstream` and `clientSecretRef` patterns. Keeping the engine out of the gateway would leave it weaker than `spactl` on those rules, or duplicate them in C#. The engine parses a registry document, which the plan already has the gateway trust.

**Revisit when** 8.x stops receiving a fix we need. Then pick a permissively licensed engine from the table above. In shipping code the engine is touched in one file, `src/Platform.Registry/RegistrySchemas.cs`, which is the whole of the gateway's and `spactl`'s exposure to it; the two test projects in the row above call it directly as well, and they are not what a swap has to keep working. A one-file change in the code that ships is why this is recorded here and not as an ADR.

### D-2: the container engine. Docker Desktop, through the `docker-desktop` cask

Decided by the integrator on 19 October 2026, in the Phase 2 planning session, ahead of task 1.7
(#58), whose `deploy.sh` builds the gateway and mock API images for ARM64, and of the sittings that
run it.

| Option | Licence | Trade-off |
| --- | --- | --- |
| **Docker Desktop 4.94.0** | Free for personal use and for companies under 250 employees and under $10 million revenue. Paid otherwise. | The simplest install, and the `docker` and `docker buildx` commands the two Dockerfiles and `deploy.sh` are written for. Needs macOS 14 or later. **Chosen.** |
| Colima 0.10.3 with the `docker` and `docker-buildx` formulas | MIT | The same commands without the licence question. A little more setup. |
| No engine | n/a | `dotnet publish /t:PublishContainer` would replace the two Dockerfiles of tasks I-4 and 5.3, so it is a plan change. |

What it changes: `Brewfile` installs the cask, `tools/preflight.sh` names the install when it finds
no engine, and `deploy.sh` (#58) may assume the `docker` CLI with buildx on the workstation. One
effect on Phase 2: with an engine on every workstation, DynamoDB Local in a container is available
to a session that wants to try the real condition-expression grammar before G6 (change request
`contracts/changes/p2-1-dynamodb-session-store.md`, option (b)). The suite's proof stays that
request's fake server, because `CODING_STANDARDS.md` forbids a test that skips on a machine without
the engine, and CI would need a container of its own.

**Revisit when** the organisation adopting the platform falls outside Docker Desktop's free terms.
The swap is Colima: the same `docker` CLI and the same Dockerfiles, so it is three Brewfile lines
and nothing in the repository's scripts.

### D-3: the shell scripts' tests. Python's `unittest`, running the real script

Decided on #66, from archived overnight ticket 69's Q1 and Q14, which Adam accepted. `tools/land.sh`
is the one sanctioned merge path and had no test: its `--body-file` handling was verified with a copy
of its parse loop, and a review then found it checking a relative path in one directory and reading
it from another (`2c9962f`).

| Option | Licence | Trade-off |
| --- | --- | --- |
| bats-core 1.14.0 | MIT | The usual choice. A new tool in CI and on every machine, and its assertions need `bats-support` and `bats-assert` as well. `tools/land.sh` has no functions to source, so bats would start it as a subprocess too. |
| Plain bash test scripts | n/a | No tool, and no assertion library: each file rebuilds the setup, the doubles and the reporting, in bash 3.2. |
| **Python's `unittest`, starting the real script as a subprocess** | Python Software Foundation License (standard library) | No new tool: python3 already runs the pre-push hook, CI's checks and `tools/roadmap.py`, and `tools/land.sh` refuses without it. The shape of the checks' own test companions. **Chosen.** |

The companion is `tools/land_tests.py`, run as `python3 -I`, and nothing is added to any package
list. It names `/bin/bash` rather than trusting the shebang, so each run tests one known shell. CI
holds both major versions the shebang `#!/usr/bin/env bash` can pick: bash 3.2.57 on
`macos-latest` (macOS 26 Arm64), the floor, where `SCRIPT_TESTS_REQUIRE_BASH=3.2` fails the job if
the image's `/bin/bash` changes, and bash 5.2.21 on `ubuntu-latest` (Ubuntu 24.04). A Mac with
Homebrew's bash first on PATH runs bash 5 too, but a newer one (5.3.20 on 18 October 2026), which no
job runs.

`tools/check.sh`, the local check, has a companion of the same shape, `tools/check_tests.py` (#68),
which also runs `.githooks/pre-push`; both run in the same two jobs. Its doubles stand in for
python3, dotnet, terraform and the smoke test, and git is real. `tools/preflight.sh` has one,
`tools/preflight_tests.py`, in the same two jobs. Built for its Terraform rows (#69) and extended to
the rest (#96), it runs a whole-file copy of the script beside crafted env files, since the script
reads them from beside itself, with an `aws` double and no `curl` on PATH. The branch cleanup
`tools/land.sh` runs after its merge, `tools/prune_branches.py`, has `tools/prune_branches_tests.py`
(#71), also in both jobs: real git with a bare origin, and a gh double whose merge really merges, so
it runs `tools/land.sh` past its merge to see the cleanup run. `tools/land_tests.py` goes past the
merge the same way since #97, for the fast-forward, the real `tools/roadmap.py` over copies of the
five files it reads, and the refresh commit and its push, and answers the cleanup with a double.

Those three, `tools/land_tests.py`, `tools/check_tests.py` and `tools/prune_branches_tests.py`, run
their cases at the same time through `tools/companion_runner.py` (#107): unittest's own text runner
and command line, but for `-b`, which it refuses, with the cases on threads, one per CPU up to eight
unless `SCRIPT_TESTS_JOBS` names another number. It uses nothing outside the standard library
either, and has a companion of its own, `tools/companion_runner_tests.py`, in both jobs.

**The Python floor is not checked.** The checks and their companions stay 3.9-compatible because
3.9.6 is the python3 that macOS's developer tools ship, but CI runs 3.12 on Linux and 3.14 on macOS,
and no job runs 3.9 for any of them.

**Revisit when** the repository goes private, since the macOS job then bills at $0.062 a minute for
the standard runner where it now costs nothing; or when a script needs a test that a subprocess
cannot observe. The choice lives in one test file per script and one CI step each, so it is recorded
here and not as an ADR, for D-1's reason.

Read on 18 October 2026: Homebrew's [bats-core](https://formulae.brew.sh/api/formula/bats-core.json)
and [bash](https://formulae.brew.sh/api/formula/bash.json) formulae;
GitHub's [Actions billing](https://docs.github.com/en/billing/concepts/product-billing/github-actions)
and [runner pricing](https://docs.github.com/en/billing/reference/actions-runner-pricing); the
[runner images' label table](https://github.com/actions/runner-images/blob/main/README.md) and the
[macOS 26 Arm64](https://github.com/actions/runner-images/blob/main/images/macos/macos-26-arm64-Readme.md)
and [Ubuntu 24.04](https://github.com/actions/runner-images/blob/main/images/ubuntu/Ubuntu2404-Readme.md)
image notes.

## Open decisions

None. D-2, the container engine, was the last and is decided above.

## What was checked, and what was not

- **Checked:** each tool version against Homebrew's catalogue, the HashiCorp tap and the Terraform registry. Each package version and licence against the package's own file on nuget.org.
- **Built:** on 11 October 2026 the solution built on an Apple silicon Mac with .NET SDK 10.0.401: 27 projects, restored from nuget.org, no warnings. The 14 test projects built as executables on `xunit.v3` 4.0.2 and Microsoft Testing Platform 2.5.1.
- **Installed on that Mac the same day by `brew bundle`:** the .NET SDK, Terraform, the AWS CLI and the Session Manager plugin.
- **Not checked:** `dotnet test`, and a restore of the 17 packages that no project references yet. `tools/preflight.sh` was linted on Linux and has since been run on macOS.

## Sources

Read on 11 October 2026.

- [.NET 10 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- Homebrew catalogue: [dotnet-sdk](https://formulae.brew.sh/api/cask/dotnet-sdk.json), [awscli](https://formulae.brew.sh/api/formula/awscli.json), [session-manager-plugin](https://formulae.brew.sh/api/cask/session-manager-plugin.json), [docker-desktop](https://formulae.brew.sh/api/cask/docker-desktop.json), [colima](https://formulae.brew.sh/api/formula/colima.json)
- Homebrew catalogue: [gh](https://formulae.brew.sh/api/formula/gh.json), read on 19 October 2026 for the GitHub CLI row (#96)
- [HashiCorp tap formula for Terraform](https://raw.githubusercontent.com/hashicorp/homebrew-tap/master/Formula/terraform.rb)
- [Terraform registry: hashicorp/aws](https://registry.terraform.io/v1/providers/hashicorp/aws), and the provider's pages for [aws_cognito_user_pool_client](https://raw.githubusercontent.com/hashicorp/terraform-provider-aws/main/website/docs/r/cognito_user_pool_client.html.markdown) and [aws_cognito_managed_login_branding](https://raw.githubusercontent.com/hashicorp/terraform-provider-aws/main/website/docs/r/cognito_managed_login_branding.html.markdown)
- [Terraform registry: hashicorp/random](https://registry.terraform.io/v1/providers/hashicorp/random), read on 13 October 2026 for the row above. The licence is the `LICENSE.txt` in the provider each lock file pins.
- Package files on nuget.org, at `https://api.nuget.org/v3-flatcontainer/<id>/<version>/<id>.nuspec`
- [xUnit.net v3: getting started](https://xunit.net/docs/getting-started/v3/getting-started) and [the 4.0.0 release notes](https://xunit.net/releases/v3/4.0.0)
- Microsoft Learn: [testing with dotnet test](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-dotnet-test), [dotnet test on Microsoft Testing Platform](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test-mtp), [exit codes](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-exit-codes), [containerize an app with dotnet publish](https://learn.microsoft.com/en-us/dotnet/core/containers/sdk-publish)
- [Playwright for .NET: installation](https://playwright.dev/dotnet/docs/intro)
- [json-everything: maintenance fee](https://github.com/json-everything/json-everything)
- [Docker Desktop licence](https://docs.docker.com/subscription/desktop-license/)
