# Diagnostic count at the candidate analyzer settings, per rule and project, and the build-time cost

Ticket: "Count the diagnostics at the candidate settings, per rule and project, and the build-time
cost" (#588), on the map "Wayfinder: static code and security analysis for the gateway build"
(#584). Everything here was executed on 2026-10-30 in a worktree of origin/main at `5f2ebe1`, on
macOS, an Apple M4 Pro with 12 cores, SDK 10.0.401. No tracked build file was changed: the settings
were global properties on the command line and the packages were injected through
`CustomAfterMicrosoftCommonProps`. This branch is never merged.

The data is in [`analyzer-diagnostic-count/`](analyzer-diagnostic-count/):

| File | What it holds |
| --- | --- |
| `analyzer-diagnostic-count.csv` | One row per setting, rule and project: `setting, rule, source, category, project, tier, count` |
| `analyzer-zero-rules.csv` | Every rule enabled at warning in S3 that fired nowhere, with a `caveat` column for the 10 that could not fire |
| `sdk-rules.csv`, `thirdparty-rules.csv` | Rule inventory from the analyzer DLLs: `package, id, category, default severity, enabled by default` |
| `results.json` | Every timed build: setting, kind, round, seconds, exit code |
| `s2.props`, `s3.props`, `s3.globalconfig` | The injected package references and the S3 severity file |
| `harness.py`, `tally.py`, `rule-inventory.cs` | The timing run, the counter and the DLL inventory tool |

## Answer

- **The SDK tier (S1) raises 6,386 findings: 555 in shipping code, 5,831 in the test tier**, from 74
  rules. The CA half is 223 shipping and 3,418 test, the same numbers as the SDK-defaults research,
  so the two runs reconcile. The IDE half is 332 shipping and 2,413 test.
- **Security is unchanged: 17 findings, one in shipping code** (CA5394 in `Platform.Registry`). The
  other 16 are CA5399 (13), CA5359 (2) and CA5351 (1), all in the test tier.
- **The five third-party packages at their shipped defaults (S2) add 1,931**: Meziantou 1,806,
  Threading 94, AsyncFixer 69, Roslynator 2, BannedApi 0. **One Meziantou rule defaults to error**
  (MA0049, type name matches namespace, in `tests/Gateway.ArchitectureTests/ArchitectureTests.cs`),
  so adding the package breaks the build even with warnings-as-errors off.
- **Every third-party rule at warning (S3) gives 18,038: 1,538 shipping, 16,500 test**, from 183
  rules. Most of the rise is three rules: VSTHRD111 4,010, MA0217 1,176, RCS1046 1,012.
- **699 enabled rules are clean today** and could go to error with no fix (`analyzer-zero-rules.csv`,
  blank caveat): 235 SDK CA rules, 84 IDE rules, 179 Meziantou, 177 Roslynator, 21 Threading, 3
  AsyncFixer. **90 of the 94 SDK security rules are among them**; only CA5394, CA5399, CA5359 and
  CA5351 fire.
- **Above fifty in one project: 85 (rule, project) pairs in S3, 5 of them in shipping code**, all in
  `Platform.Identity`: CA2007 86, MA0004 86, VSTHRD111 86, IDE0022 69, RCS1002 68. At S1 only two of
  them remain (CA2007, IDE0022). The other 80 are test tier: ramp candidates, or relaxation candidates.
- **Build time: a clean build goes from 3.8 s to 8.4 s with the SDK tier, and to 8 to 10 s with the
  packages. A no-op build and a one-file gateway edit stay between 1.3 and 1.8 s at every setting.**
  With no build server, a clean build goes from 14 s to 27 s and then 35 s.

## Settings

Every build passed `-p:TreatWarningsAsErrors=false`, so it completed and every finding was logged.

| Id | Settings | Restore |
| --- | --- | --- |
| S0 | today's props | the solution as it is |
| S1 | `AnalysisMode=All`, `AnalysisLevelSecurity=latest-all`, `EnforceCodeStyleInBuild=true`, `AnalysisLevelStyle=latest-all` | as S0 |
| S2 | S1, plus the five eligible packages at their shipped defaults (`s2.props`) | with `s2.props` |
| S3 | S2, plus `s3.globalconfig`: all 496 third-party rule ids at warning, and `roslynator_analyzers.enabled_by_default = true` (`s3.props`) | as S2 |

Versions: Meziantou.Analyzer 3.0.317, Roslynator.Analyzers 5.0.1,
Microsoft.VisualStudio.Threading.Analyzers 18.12.1, Microsoft.CodeAnalysis.BannedApiAnalyzers
5.6.0, AsyncFixer 2.1.0. They were added with `VersionOverride` and `PrivateAssets="all"`. No
project reference was added, so A-01 and A-02 are untouched.

### Two traps met on the way, which the ADR's settings must avoid

1. **`AnalysisLevel=latest-all` with `AnalysisLevelStyle=latest-all` turns every IDE rule off.**
   `sdk/10.0.401/Sdks/Microsoft.NET.Sdk/codestyle/cs/build/Microsoft.CodeAnalysis.CSharp.CodeStyle.targets`
   line 52 adds the style global config only when `AnalysisLevelStyle` differs from `AnalysisLevel`,
   or `AnalysisModeStyle` from `AnalysisMode`, or the effective style level is 11 or higher. With the
   two equal, the build on `Platform.Registry` gave 0 IDE warnings, against 138 with
   `AnalysisMode=All`. This run used `AnalysisMode=All`. If the ADR sets both levels, they must
   differ, or it uses `AnalysisMode` for the CA rules.
2. **A props file passed through `CustomBeforeMicrosoftCommonProps` loses to `Directory.Build.props`**,
   which is imported after it and sets `TreatWarningsAsErrors` back to true. The first attempt built
   with errors instead of warnings. This run used global properties, plus `CustomAfterMicrosoftCommonProps`
   for the package items.

The ticket names `EnforceCodeStyleInBuild=true` alone for the style tier. On its own it reports 0
(SDK-defaults research, E7), so `AnalysisLevelStyle=latest-all` was added to make the IDE count
measurable.

## Counts

Findings are de-duplicated on (file, line, column, rule id). A finding with no file, such as a
Roslynator configuration warning, is de-duplicated on (project, rule id, message). Shipping is
`src/Gateway` and `src/Platform.*` except `src/Platform.Testing`. The test tier is everything else
in the solution: `tests/`, `fixtures/`, `tools/`, `src/Platform.Testing`. This matches the
SDK-defaults research.

| Setting | Shipping | Test tier | Rules that fired | Rules that fired in shipping code |
| --- | ---: | ---: | ---: | ---: |
| S0 | 0 | 0 | 0 | 0 |
| S1 | 555 | 5,831 | 74 | 34 |
| S2 | 775 | 7,542 (+1 error, MA0049) | 101 | 51 |
| S3 | 1,538 | 16,500 | 183 | 92 |

By source:

| Source | S1 shipping | S1 test | S2 shipping | S2 test | S3 shipping | S3 test |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| SDK CA rules | 223 | 3,418 | 223 | 3,418 | 223 | 3,418 |
| SDK IDE rules | 332 | 2,413 | 327 | 2,378 | 327 | 2,378 |
| Meziantou.Analyzer | | | 216 | 1,590 | 483 | 4,381 |
| Microsoft.VisualStudio.Threading.Analyzers | | | 6 | 88 | 131 | 3,999 |
| Roslynator.Analyzers | | | 2 | 0 | 373 | 2,256 |
| AsyncFixer | | | 1 | 68 | 1 | 68 |
| Microsoft.CodeAnalysis.BannedApiAnalyzers | | | 0 | 0 | 0 | 0 |

The IDE count drops by 40 from S1 to S2 (IDE0058 923 to 883). The packages were the only change.
The cause was not investigated.

Shipping code per project:

| Project | S1 | S3 |
| --- | ---: | ---: |
| Platform.Identity | 237 | 670 |
| Platform.Registry | 90 | 262 |
| Platform.Security | 61 | 152 |
| Platform.Hosting | 50 | 147 |
| Platform.Telemetry | 43 | 110 |
| Platform.Proxy | 43 | 89 |
| Platform.Abstractions | 31 | 103 |
| Gateway | 0 | 5 |

The largest rules in shipping code at S3: RCS1002 169, IDE0022 127, CA2007 125, MA0004 125,
VSTHRD111 124, IDE0160 90, IDE0058 73, MA0214 64, MA0217 62, MA0148 44. In the whole solution:
VSTHRD111 4,010, CA2007 1,581, CA1707 1,363, MA0217 1,176, RCS1046 1,012, IDE0058 883, MA0004
860, IDE0022 779, RCS1002 583, IDE0160 445.

### What the counts mean for the severity and relaxation tickets

- **Three rules report the same thing, ConfigureAwait on an await**: CA2007 1,581, VSTHRD111 4,010
  and MA0004 860. Each sees a different set of awaits, so the counts are not the same, but the three
  overlap. Turning on more than one makes the ramp longer for one fix.
- **CA1707 (underscores in identifiers) is 1,363, all in the test tier.** It is the test-method
  naming convention, so it is a relaxation, not a fix.
- **The IDE counts at `latest-all` are an upper bound, not a choice.** The `all` style config sets
  opposing rules to warning together, such as IDE0007 (use `var`) and IDE0008 (use an explicit
  type). Before an IDE count can be read as fix work, the style options in `.editorconfig` must be
  set. IDE0008's 357 findings are the explicit-type side of that pair.
- **MA0049 must be set below error, or the type renamed, before Meziantou joins the build.**
- **BannedApiAnalyzers reports nothing without a `BannedSymbols.txt`.** RS0030, RS0031 and RS0035
  are in the zero list with that caveat. They are not "free to set to error"; the Banned APIs
  ticket decides what goes in the file.
- **Four Roslynator rules need an option before they run**: RCS1018, RCS1050, RCS1051 and RCS1207,
  listed in the zero file with the option each needs. Without the option, each project logs ROS0003 (32 times
  in shipping code).
- **CA2218 and CA2224 are in the SDK's `all` config but in neither the C# nor the shared
  NetAnalyzers DLL**, so they cannot fire on C#. Both are in the zero list with that caveat.
- **IDE0005 (unnecessary using) needs `GenerateDocumentationFile=true`** to run in a build. Without
  it, the build logs an `EnableGenerateDocumentationFile` warning instead. IDE0005 is in the zero list with that
  caveat.

## Build time

Restore was run before each setting's builds and was not timed. The settings were interleaved, S0,
S1, S2, S3, round after round, so drift on the machine hit all four. Each round ran three builds: a
clean build (`--no-incremental`), a no-op build, and a build after `touch src/Gateway/Program.cs`.
"Cold" is one clean build per setting with `-nodeReuse:false -p:UseSharedCompilation=false`: fresh
MSBuild nodes and no compiler server. The shared build servers could not be shut down, because other
sessions were using them. About 15 other `dotnet` processes from parallel sessions were running
when the run started, so some noise remains (S2 clean: 6.5, 11.1, 9.7 s).

| Setting | Clean, median of 3 (s) | The 3 runs | Cold clean (s) | No-op, median (s) | Gateway touched, median (s) |
| --- | ---: | --- | ---: | ---: | ---: |
| S0 | 3.75 | 4.33, 3.04, 3.75 | 13.97 | 1.83 | 1.71 |
| S1 | 8.43 | 8.43, 8.45, 8.25 | 27.02 | 1.76 | 1.80 |
| S2 | 9.67 | 6.45, 11.11, 9.67 | 35.60 | 1.64 | 1.27 |
| S3 | 7.96 | 8.10, 7.25, 7.96 | 35.42 | 1.60 | 1.69 |

Every S2 build exited 1 because of MA0049. The error stops only that test project's own build. Its compile still ran and
logged every warning, and no other project depends on it, so the timing is comparable.

- **The no-op and touched builds do not move.** A no-op build runs no compiler. Touching
  `src/Gateway/Program.cs` recompiles only `Gateway`: its reference assembly does not change, so
  `Gateway.TestHarness` and the test projects skip `CoreCompile`. This was checked with `-v:n`. An
  edit to a public type in `Platform.Abstractions`, which every project references, was not
  measured. It would cost close to a clean build.
- **Analyzer CPU time at S3** (`-p:ReportAnalyzer=true`, summed over the 29 projects, so it can
  exceed wall-clock on 12 cores): Microsoft.CodeAnalysis.NetAnalyzers 47.9 s, CSharp.CodeStyle
  10.5 s, VisualStudio.Threading 5.4 s, Meziantou 5.0 s, CSharp.NetAnalyzers 3.8 s, AsyncFixer
  1.5 s, Roslynator 1.4 s, xunit.analyzers 1.1 s. **The SDK's own CA analyzers are the cost.** The
  five packages together add about 13 s of CPU time and almost no wall-clock.

## Not measured

- A CI runner. Cold here means no build server on this machine. A GitHub-hosted Linux runner has
  fewer cores, so expect it to be slower.
- SDK 10.0.1xx. Only 10.0.401 is installed.
- An incremental build after a change to a widely referenced project.
- Which awaits CA2007, VSTHRD111 and MA0004 have in common.
