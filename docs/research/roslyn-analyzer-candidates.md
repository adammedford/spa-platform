# Third-party Roslyn analyzer candidates

Research for [#586](https://github.com/adammedford/spa-platform/issues/586), under the map
[#584](https://github.com/adammedford/spa-platform/issues/584). Read on 2026-10-29. This is the first
file in `docs/research/`. The branch is never merged.

**The map's rule.** A package is eligible only if its licence is MIT, Apache-2.0 or BSD and it had a
stable release against a current SDK within 18 months, so on or after **2025-04-29**. Anything else
is declined unless a ticket argues it in.

**What "against a current SDK" means here.** The reference point is the SDK on this machine, 10.0.401,
whose compiler is `Microsoft.CodeAnalysis` 5.9.0 (product version `5.9.0-1.26423.113`). `global.json`
pins 10.0.100 with `rollForward: latestFeature`. An analyzer compiled against an older Roslyn still
loads in a newer compiler, so a Roslyn 4.x target runs on this SDK. The Roslyn column below therefore
reports what each package was built against, and it fails a package only when no release falls inside
the window.

**The licence convention** is the one in `docs/dependencies.md`: the SPDX expression from the package's
own nuspec, or its LICENSE file when the nuspec has none. A GitHub repository's licence badge is not
the source. GitHub reports `NOASSERTION` for Roslynator, vs-threading, xunit.analyzers and Sonar
because its detector cannot classify the file, and that says nothing about the licence.

## Verdicts

| Verdict | Package | Latest stable | Released | Licence (nuspec) | Built against Roslyn |
| --- | --- | --- | --- | --- | --- |
| **Eligible** | `Meziantou.Analyzer` | 3.0.317 | 2026-10-25 | MIT | 4.8, 4.14, 5.0, 5.6, **5.9** (one folder each) |
| **Eligible** | `Roslynator.Analyzers` | 5.0.1 | 2026-10-04 | Apache-2.0 | 3.8, 4.7, **5.0** |
| **Eligible** | `Microsoft.VisualStudio.Threading.Analyzers` | 18.12.1 | 2026-10-12 | MIT | 4.11 |
| **Eligible** | `Microsoft.CodeAnalysis.BannedApiAnalyzers` | 5.6.0 | 2026-07-02 | MIT | 4.12 |
| **Eligible** | `AsyncFixer` | 2.1.0 | 2025-12-29 | Apache-2.0 | 4.8 |
| Declined on licence | `SecurityCodeScan.VS2019` | 5.6.7 | 2022-09-05 | LGPL-3.0-or-later | 3.8 |
| Declined on licence | `Puma.Security.Rules.2022` | 2.4.30 | 2026-08-07 | MPL-2.0 | 4.8 |
| Declined on licence | `SonarAnalyzer.CSharp` | 10.35.0.4138 | 2026-09-28 | **SONAR Source-Available License v1.0** (licence file), not LGPL | 1.3.1 |
| Declined on maintenance | `IDisposableAnalyzers` | 4.0.8 | 2024-06-19 | MIT | 4.5 |
| Declined on maintenance | `ErrorProne.NET.CoreAnalyzers` | 0.1.2 (betas since) | 2018-11-01 | none in the package; repo says MIT | 2.9 (newest beta: 4.13) |
| Not a candidate: already in the SDK | `Microsoft.AspNetCore.Analyzers` | not on nuget.org | n/a | n/a | ships with SDK 10.0.401 |
| Not a candidate: already in every test project | `xunit.analyzers` | 2.2.0 | 2026-10-09 | Apache-2.0 | 4.11 |

Three findings change what the map expected:

1. **Sonar is no longer LGPL.** Its package declared `LGPL-3.0-only` from 8.17.0.26580 (2021-01-19)
   through the 10.2 line. From 10.3.0.106239 (2024-11-26) the nuspec has `<license type="file">`, and
   the file is the "SONAR Source-Available License v1.0, Last Updated November 13, 2024". That licence
   is not open source and restricts "Competing" uses. It is declined on licence either way, but the
   map should record SSALv1, not LGPL-3.0.
2. **No eligible package adds security dataflow (taint) analysis.** The two security scanners
   (SecurityCodeScan, Puma) and Sonar are all declined on licence. The SDK already has its own taint
   rules, CA3001 to CA3012, which are off by default in .NET 10. For the security half, then, the
   choice is whether to turn those SDK rules on, not which package to add. CA3001's page says the rule
   "can't track data across assemblies" and that how deep it follows data flow is configurable.
3. **`Microsoft.AspNetCore.Analyzers` is not a package you add.** nuget.org returns 404 for its page and
   its flat container, and search finds 0 hits. The analyzers ship inside the SDK and the ASP.NET Core
   targeting pack (see its section below).

## Environment, and what was executed versus read

- **Nothing touched the solution.** No `dotnet restore`, `dotnet build` or `dotnet add package` was run
  against `SpaPlatform.slnx` or any project, and no file other than this one was changed.
  `Directory.Packages.props` (owned by the integrator) and `docs/dependencies.md` were only read.
- **Executed** (all on 2026-10-29, from an Apple-silicon Mac, with output kept in the session
  scratchpad):
  - `curl` GETs against the nuget.org v3 APIs: the registration index
    (`https://api.nuget.org/v3/registration5-gz-semver2/<id>/index.json`, and the `semver1` hive for
    SecurityCodeScan), the flat container (`https://api.nuget.org/v3-flatcontainer/<id>/index.json`,
    `.../<id>/<ver>/<id>.nuspec`, `.../<id>.<ver>.nupkg`) and the search API
    (`https://azuresearch-usnc.nuget.org/query?q=packageid:<id>`). Versions, publish dates, licence
    expressions and deprecation records come from these.
  - Downloaded the latest-stable `.nupkg` of each candidate into its own scratchpad folder.
    `unzip -l` listed the analyzer folders, and `unzip -p` printed each nuspec and licence file.
  - Ran a `dotnet fsi` script that used `System.Reflection.Metadata` to read each extracted analyzer
    DLL's `Microsoft.CodeAnalysis*` assembly references. It reads metadata only and loads no analyzer
    code. The "Built against Roslyn" column comes from this. The same script read the version of the
    local SDK's `sdk/10.0.401/Roslyn/bincore/Microsoft.CodeAnalysis.dll`.
  - Listed `/usr/local/share/dotnet/sdk/10.0.401/Sdks/*/analyzers` and
    `/usr/local/share/dotnet/packs/Microsoft.AspNetCore.App.Ref/10.0.12/analyzers`.
  - `gh api repos/<owner>/<repo>` for the archived flag, last push, open issue-plus-PR count and
    licence; `gh api repos/<r>/commits` and `/releases` for five repositories; and `gh api
    search/issues` for open-issue counts. The search ran into GitHub's rate limit after ten
    repositories, so the BannedApi count in `dotnet/roslyn` is missing.
  - `grep` over this repository's `*.csproj` files to see which projects use `Microsoft.NET.Sdk.Web`.
- **Read** (web pages, 2026-10-29): each package's GitHub README, Microsoft Learn's code-analysis
  overview, CA3001, the ASP.NET Core diagnostics list, the vs-threading analyzers index, the
  BannedApiAnalyzers help file and the Roslynator analyzers list. Each URL is listed under its section.
- **Not measured:** build cost. No package was added to a build, so there is no
  `-p:ReportAnalyzer=true` timing. Wherever build cost appears below, it is what the project says
  about itself.
- API timestamps are in UTC and some fall after midnight UTC. Every "read" date is the local date,
  2026-10-29.

## What the SDK already brings (the baseline)

- SDK 10.0.401 ships `Microsoft.CodeAnalysis.NetAnalyzers` (the CA rules) in
  `Sdks/Microsoft.NET.Sdk/analyzers/`. Its standalone package is at 10.0.403, published 2026-10-25.
  Learn says these rules are "included with the .NET SDK", that analysis "is enabled, by default, for
  projects that target .NET 5 or later", and that in the default `AnalysisMode` "only a small number
  of rules are enabled as build warnings". `AnalysisMode` can be `Minimum`, `Recommended` or `All`.
  Code-style (IDE) rules are "disabled, by default" on command-line builds unless
  `EnforceCodeStyleInBuild` is set.
  [Code analysis overview](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/overview)
  (page `ms.date` 2025-11-05, read 2026-10-29)
- This repository sets none of `AnalysisLevel`, `AnalysisMode` or `EnforceCodeStyleInBuild`; a `grep`
  of the `*.props`, `*.targets` and `*.csproj` files found nothing. `Directory.Build.props` sets
  `TreatWarningsAsErrors`, so any added analyzer whose rules default to warning breaks the build until
  it is configured.
- **Security taint rules CA3001 to CA3012** (SQL injection, XSS, path injection and the rest) are in the
  SDK. CA3001 is "Enabled by default in .NET 10: No". It "can't track data across assemblies", and its
  dataflow depth is configurable.
  [CA3001](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca3001)
  (page `ms.date` 2026-10-19, read 2026-10-29)

## Eligible

### Meziantou.Analyzer 3.0.317

- **Release:** 3.0.317, published 2026-10-25. Licence `MIT` in the nuspec `<license type="expression">`,
  and the package contains `LICENSE.txt` ("MIT License, Copyright (c) Gérald Barré"). Repository
  [meziantou/Meziantou.Analyzer](https://github.com/meziantou/Meziantou.Analyzer), nuspec commit
  `0d0970fc`. Source: nuget.org registration and the nupkg, read 2026-10-29.
- **Roslyn:** one build per Roslyn line, `analyzers/dotnet/roslyn{4.8,4.14,5.0,5.6,5.9}/cs/`. The 5.9 build
  matches the local SDK's compiler exactly. Source: the nupkg listing and the DLL references, executed
  2026-10-29.
- **Maintenance:** last push 2026-10-28, not archived. One open item counting issues and PRs together,
  and 0 open issues by search. Source: `gh api`, 2026-10-29.
- **What it adds over the SDK:** about 240 rules, MA0001 to MA0243, in the categories Usage, Style,
  Design, Performance, Security and Naming. Examples: MA0004 "Use Task.ConfigureAwait" (warning);
  MA0001 "StringComparison is missing"; MA0074 and MA0075, culture-sensitive calls; MA0009 "Add regex
  evaluation timeout"; MA0039 "Do not write your own certificate validation method"; and MA0110, the
  Regex source generator. Source: the
  [README](https://github.com/meziantou/Meziantou.Analyzer), read 2026-10-29.
- **Overlap:** MA0004 covers the same ground as the SDK's CA2007, which is off by default. MA0001 and
  MA0074 overlap CA1307, CA1309 and CA1310, also off by default in `Default` mode. This is my
  comparison, not a statement from either project.
- **Reputation:** the README says generated code is skipped "as analyzing it costs build time". It
  offers `<MeziantouAnalysisMode>` to set every rule's default severity at once, and it ships built-in
  suppressions, for example CA1822 on BenchmarkDotNet and System.Text.Json members. The README makes no
  false-positive claims, and search finds no open issue that mentions "false positive".

### Roslynator.Analyzers 5.0.1

- **Release:** 5.0.1, published 2026-10-04. Licence `Apache-2.0` (nuspec expression). Repository
  [dotnet/roslynator](https://github.com/dotnet/roslynator). Source: nuget.org registration and the
  nuspec, read 2026-10-29.
- **Roslyn:** `analyzers/dotnet/roslyn{3.8,4.7,5.0}/cs/`, so it is built against Roslyn 5.0. Executed
  2026-10-29.
- **Maintenance:** last push 2026-10-04, not archived. 221 open issues and PRs together, 205 open issues.
  Source: `gh api`, 2026-10-29.
- **What it adds:** the [analyzer list](https://josefpihrt.github.io/docs/roslynator/analyzers) (read
  2026-10-29) has about 278 RCS ids, 26 of them deprecated. This package holds the RCS1xxx "common"
  set. The RCS0xxx formatting set is in `Roslynator.Formatting.Analyzers` and RCS9xxx is in
  `Roslynator.CodeAnalysis.Analyzers`, both separate packages. Many RCS1 rules list no default severity
  ("-"), and a few are Warning, for example RCS1044 "Remove original exception from throw statement" and
  RCS1155 "Use StringComparison when comparing strings".
- **Overlap:** RCS1 rules are mostly about simplifying and removing redundancy. Much of that overlaps
  the SDK's IDE style rules, which run on the command line only with `EnforceCodeStyleInBuild`. This is
  my comparison.
- **Reputation:** 6 open issues mention "false positive" and 0 have "performance" in the title (search,
  2026-10-29). The README makes no build-cost claim.

### Microsoft.VisualStudio.Threading.Analyzers 18.12.1

- **Release:** 18.12.1, published 2026-10-12. Licence `MIT` (nuspec expression). Repository
  [microsoft/vs-threading](https://github.com/microsoft/vs-threading), nuspec commit `816950dc`. It has
  no package dependencies. Read 2026-10-29.
- **Roslyn:** the DLLs reference `Microsoft.CodeAnalysis` 4.11. Its folder layout is `analyzers/cs` and
  `analyzers/vb`, without the `dotnet/` level. Executed 2026-10-29.
- **Maintenance:** last push 2026-10-27, not archived. 89 open issues and PRs together, 75 open issues.
  `gh api`, 2026-10-29.
- **What it adds:** the VSTHRD rules. VSTHRD100 to VSTHRD117 and VSTHRD200 (async naming) apply to any
  async code, for example VSTHRD100 on `async void`, VSTHRD103 on calling a synchronous method when an
  async one exists, and VSTHRD110 on an unobserved result. VSTHRD001 to VSTHRD013 support the
  library's "threading rules" around `JoinableTaskFactory`, and VSTHRD012 and VSTHRD115 name it in
  their titles. The [analyzers index](https://microsoft.github.io/vs-threading/analyzers/) (read
  2026-10-29) does not say which rules are specific to `JoinableTaskFactory`. **Open:** which VSTHRD rules
  mean anything in a gateway with no `JoinableTaskFactory`. The rule pages decide that, and it is a
  configuration question for whoever adopts the package.
- **Overlap:** async void, blocking calls and naming overlap AsyncFixer's 02 and 03, and partly
  Meziantou.
- **Reputation:** 2 open issues mention "false positive" and 1 has "performance" in the title (search,
  2026-10-29).

### Microsoft.CodeAnalysis.BannedApiAnalyzers 5.6.0

- **Release:** 5.6.0, published 2026-07-02. Licence `MIT` (nuspec expression). The nuspec's repository is
  [dotnet/roslyn](https://github.com/dotnet/roslyn), commit `c0573ed0`. The analyzers moved there from
  `dotnet/roslyn-analyzers`, whose default branch is now `archive` (`gh api`, 2026-10-29).
- **Roslyn:** references `Microsoft.CodeAnalysis` 4.12. Executed 2026-10-29.
- **Maintenance:** ships from the Roslyn repository, last push 2026-10-30 UTC. Its open count (6,715
  issues and PRs together) covers all of Roslyn and says nothing about this package alone. The count
  of BannedApi issues was not taken because of the rate limit.
- **What it adds:** a check that a project defines for itself. Symbols listed in `BannedSymbols.txt`,
  added as `AdditionalFiles`, one documentation-comment id per line with an optional message, become
  diagnostics wherever they are used. Generated code is analyzed unless
  `dotnet_banned_api_analyzer.exclude_generated_code = true`. Source: the
  [help file](https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.BannedApiAnalyzers/BannedApiAnalyzers.Help.md),
  read 2026-10-29. The SDK has no equivalent. This could turn the repository's existing rules into build
  errors, for example "no hand-written OAuth protocol code" and "never log a token", by banning the
  specific APIs those rules forbid. That use is my suggestion, not the help file's.
- **Reputation:** it reports only what the project lists, so false positives come from the list, not
  from inference.

### AsyncFixer 2.1.0

- **Release:** 2.1.0, published 2025-12-29, inside the window. Licence `Apache-2.0` (nuspec expression,
  with `requireLicenseAcceptance` true). Repository
  [semihokur/AsyncFixer](https://github.com/semihokur/AsyncFixer), nuspec commit `d5926667`. The
  release before it was v1.6.0 on 2022-05-10. Source: nuget.org registration and `gh api releases`,
  2026-10-29.
- **Roslyn:** references `Microsoft.CodeAnalysis` 4.8, the .NET 8 compiler line. It loads under 5.9.
  Executed 2026-10-29.
- **Maintenance:** last push 2025-12-29, the same day as the release, which added a GitHub Actions
  workflow. 8 open issues. Releases come in bursts several years apart, so it passes the 18-month rule
  but would fail it again after 2027-06-29 without another release.
- **What it adds:** six rules. AsyncFixer01, unnecessary async/await; 02, blocking calls such as
  `.Wait()`, `.Result` and `Thread.Sleep` inside async methods; 03, async void; 04, a fire-and-forget
  call inside a `using`; 05, a nested `Task<Task>`; 06, a discarded `Task<T>` result. Source: the
  [README](https://github.com/semihokur/AsyncFixer), read 2026-10-29.
- **Overlap:** 02 and 03 overlap VSTHRD103 and VSTHRD100. 01, 04 and 05 have no counterpart in the SDK
  or vs-threading that I found.
- **Reputation:** the README says 02 used to flag `.Result` after `await Task.WhenAll()` and "doesn't
  anymore". 0 open issues mention "false positive".

## Declined on licence

### SecurityCodeScan.VS2019 5.6.7

- **Release:** 5.6.7, published 2022-09-05. Licence `LGPL-3.0-or-later` (nuspec expression). The older
  ids, `SecurityCodeScan` and `SecurityCodeScan.VS2017`, end at 3.5.4 (2021-02-10) with a `licenseUrl`
  to LGPL-3.0. Repository
  [security-code-scan/security-code-scan](https://github.com/security-code-scan/security-code-scan),
  default branch `vs2019`. Read 2026-10-29.
- **Roslyn:** references 3.8. Executed 2026-10-29.
- **Maintenance:** it fails this gate too. The last commit was 2022-11-11, the last push 2024-07-08,
  and there are 48 open issues, 7 of which mention "false positive". `gh api`, 2026-10-29.
- **What it would add:** taint analysis for injection and XSS, which the SDK's CA3001 to CA3012 also
  cover in part.

### Puma Scan: Puma.Security.Rules.2022 2.4.30

- **Ids:** the old `Puma.Security.Rules` (2.4.11, 2022-02-01) is deprecated on nuget.org with the reason
  `Legacy` and the alternate `Puma.Security.Rules.2022`. The current id is `Puma.Security.Rules.2022`
  2.4.30, published 2026-08-07. Read 2026-10-29.
- **Licence:** `MPL-2.0` (nuspec expression, with `requireLicenseAcceptance` true). That is weak
  copyleft and not on the map's allowlist. Repository
  [pumasecurity/puma-scan](https://github.com/pumasecurity/puma-scan).
- **Roslyn:** references 4.8. Unusually for an analyzer, the nuspec declares package dependencies on
  `Microsoft.CodeAnalysis` 4.8.0 and `Microsoft.Web.Xdt` 3.1.0, and the package carries
  `Microsoft.Web.XmlTransform.dll` in its analyzer folder. Executed 2026-10-29.
- **Maintenance:** active. The last push was 2026-08-07, merging "modernize-vs2022". 23 open issues, 7
  of which mention "false positive". It would pass the maintenance gate.

### SonarAnalyzer.CSharp 10.35.0.4138

- **Release:** 10.35.0.4138, published 2026-09-28. The nuspec has `<license type="file">licenses\LICENSE.txt</license>`.
  The file begins "SONAR Source-Available License v1.0 / Last Updated November 13, 2024" and defines
  "Competing" as "marketing a product or service as a substitute for the functionality or value of
  SonarQube". The package also carries third-party licences for Roslyn and Google.Protobuf.
  Repository [SonarSource/sonar-dotnet](https://github.com/SonarSource/sonar-dotnet). Read 2026-10-29.
- **Licence history**, from the registration's `licenseExpression` and `licenseUrl` across versions:
  a GitHub `LICENSE` URL from 2016, a file licence from 7.11 (2019-02-12), `LGPL-3.0-only` from
  8.17.0.26580 (2021-01-19), and the licence file (SSALv1) from **10.3.0.106239 (2024-11-26)**. Executed
  2026-10-29. Only an LGPL version, 10.2.x or earlier, could be an older open-source option, and LGPL
  is declined as well.
- **Roslyn:** compiled against `Microsoft.CodeAnalysis` 1.3.1, the lowest version, so that it loads
  everywhere. Executed 2026-10-29.
- **Maintenance:** active. Last push 2026-10-30 UTC, 250 open issues, 56 of which mention "false
  positive".

## Declined on maintenance

### IDisposableAnalyzers 4.0.8

- **Release:** 4.0.8, published 2024-06-19, before the 2025-04-29 cutoff. Licence `MIT` (nuspec
  expression). Repository
  [DotNetAnalyzers/IDisposableAnalyzers](https://github.com/DotNetAnalyzers/IDisposableAnalyzers). Read
  2026-10-29.
- **Roslyn:** references 4.5. Executed 2026-10-29.
- **Maintenance:** the last commit and push were both 2024-06-19 ("use net8.0"). 100 open issues, 18 of
  which mention "false positive". `gh api`, 2026-10-29.
- **What it would add:** IDISP001 to IDISP026, disposal tracking (create, dispose, ownership,
  `HttpClient` reuse, the finalizer pattern). The SDK's CA2000 and CA2213 are its nearest equivalents.
  The README calls it "Early alpha, finds bugs in the code but there are also bugs in the analyzer".

### ErrorProne.NET.CoreAnalyzers

- **Releases:** the latest *stable* release on nuget.org is 0.1.2, published 2018-11-01, built against
  Roslyn 2.9. Every later version is a prerelease: 0.2.0-beta.7 (2019) through 0.8.2-beta.1
  (2025-07-31) and **0.9.0-beta.4 (2026-06-05)**, built against Roslyn 4.13. GitHub's releases page marks
  "0.8.0" (2025-07-08) as not a prerelease, while nuget.org has only `0.8.0-beta.1` for it. Source:
  nuget.org registration and flat container, and `gh api releases`, 2026-10-29.
- **Licence:** neither nupkg (0.1.2 or 0.9.0-beta.4) has a `<license>` expression or a LICENSE file,
  only a `licenseUrl` pointing to the repository's LICENSE. GitHub reports that repository as MIT. Under
  the `docs/dependencies.md` convention the package itself declares no SPDX licence.
- **Maintenance:** the code is active, with its last commit on 2026-08-13 (EPC42) and a push on
  2026-09-08. 92 open issues, 3 of which mention "false positive".
- **Why it is declined:** no stable release since 2018. A ticket could argue in the 0.9.0 beta line,
  which is current and built against Roslyn 4.13, but it would have to accept a prerelease package and a
  licence that is declared only by URL.

## Not candidates

### Microsoft.AspNetCore.Analyzers: shipped with the SDK

- nuget.org returns 404 for `https://www.nuget.org/packages/Microsoft.AspNetCore.Analyzers` and for
  its flat-container index, and the search API finds 0 hits. Executed 2026-10-29.
- SDK 10.0.401 ships `Sdks/Microsoft.NET.Sdk.Web/analyzers/cs/` with `Microsoft.AspNetCore.Analyzers.dll`,
  `Microsoft.AspNetCore.Mvc.Analyzers.dll`, `Microsoft.AspNetCore.Mvc.Api.Analyzers.dll` and
  `Microsoft.AspNetCore.Components.SdkAnalyzers.dll`. The targeting pack
  `packs/Microsoft.AspNetCore.App.Ref/10.0.12/analyzers/dotnet/cs/` carries
  `Microsoft.AspNetCore.App.Analyzers.dll`, `Microsoft.AspNetCore.App.CodeFixes.dll` and the
  source generators. Directory listing executed 2026-10-29.
- **Projects that get them.** These use `Sdk="Microsoft.NET.Sdk.Web"`: `src/Gateway`,
  `fixtures/MockApi`, `tools/bench/floor` and the `spikes/g0` projects. These reference the
  `Microsoft.AspNetCore.App` framework: `src/Platform.Abstractions`, `fixtures/TestIdp`,
  `fixtures/Fixtures.Tests` and `tests/e2e`. `grep`, 2026-10-29.
- The rules are ASP0000 to ASP0038, BL0001 to BL0008 and MVC1000 to MVC1006. Examples: ASP0001
  "Authorization middleware is incorrectly configured" and ASP0026 "`[Authorize]` is overridden by
  `[AllowAnonymous]` from 'farther away'".
  [ASP.NET Core code analysis](https://learn.microsoft.com/en-us/aspnet/core/diagnostics/code-analysis)
  (page `ms.date` 2026-08-25, read 2026-10-29)

### xunit.analyzers 2.2.0: already in every test project

- `Directory.Build.props` adds `xunit.v3` to every project whose name ends in `Tests`, and
  `Directory.Packages.props` pins it at 4.0.2. The nuspec of `xunit.v3` 4.0.2 depends on
  `xunit.v3.mtp-v2` [4.0.2], and that package depends on **`xunit.analyzers` 2.2.0**, along with
  `xunit.v3.assert` and `xunit.v3.core.mtp-v2`. Source: the flat-container nuspecs, read 2026-10-29.
- `xunit.analyzers` 2.2.0 was published 2026-10-09 under `Apache-2.0` (nuspec expression), and it
  references Roslyn 4.11. Its repository is [xunit/xunit.analyzers](https://github.com/xunit/xunit.analyzers),
  last push 2026-10-29, with 0 open issues by search.
- So every test project already runs the xUnit analyzers through `xunit.v3`. Libraries like
  `Platform.Testing` reference `xunit.v3.extensibility.core` and `xunit.v3.assert` directly instead,
  and this research did not check whether those two bring the analyzers in.

## Sources

Each source was read or queried on 2026-10-29.

- nuget.org registration: `https://api.nuget.org/v3/registration5-gz-semver2/<lowercase-id>/index.json`
  (and `registration5-semver1` for the SecurityCodeScan ids)
- nuget.org flat container: `https://api.nuget.org/v3-flatcontainer/<id>/index.json`,
  `.../<id>/<ver>/<id>.nuspec` and `.../<id>/<ver>/<id>.<ver>.nupkg`
- nuget.org search: `https://azuresearch-usnc.nuget.org/query?q=packageid:Microsoft.AspNetCore.Analyzers`
- GitHub REST: `repos/<owner>/<repo>`, `/commits`, `/releases` and `search/issues`, for the repositories
  linked above
- [Code analysis in .NET](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/overview)
- [CA3001](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca3001)
- [ASP.NET Core diagnostic code analysis](https://learn.microsoft.com/en-us/aspnet/core/diagnostics/code-analysis)
- [vs-threading analyzers](https://microsoft.github.io/vs-threading/analyzers/)
- [BannedApiAnalyzers help](https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.BannedApiAnalyzers/BannedApiAnalyzers.Help.md)
- [Roslynator analyzers](https://josefpihrt.github.io/docs/roslynator/analyzers)
- READMEs: [Meziantou.Analyzer](https://github.com/meziantou/Meziantou.Analyzer),
  [Roslynator](https://github.com/dotnet/roslynator), [AsyncFixer](https://github.com/semihokur/AsyncFixer),
  [IDisposableAnalyzers](https://github.com/DotNetAnalyzers/IDisposableAnalyzers)
- The local SDK at `/usr/local/share/dotnet` (10.0.401, ASP.NET Core targeting pack 10.0.12)
