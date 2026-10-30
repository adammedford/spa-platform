# .NET 10 SDK analyzer defaults, and what AnalysisMode=All, the security categories and EnforceCodeStyleInBuild add

Research for #585 (map #584). Read and executed on 2026-10-29 against origin/main at `5f2ebe1`.
Nothing on this branch changes a build setting. Every run below passed its settings on the
command line or used a throwaway project in the session scratchpad.

## Answer in brief

- **Default (`AnalysisLevel` unset, which means `latest`, which means 10.0):** the SDK loads
  `analysislevel_10_default.globalconfig`. That file is almost empty. Its only entry turns off
  CA1516, a rule released after 10.0. So each rule's default comes from its descriptor in the
  analyzer DLL. 174 CA rules are enabled by default, but only the 29 the overview page lists
  are build warnings or errors. The rest are Info or Hidden, and no security rule is among the 29.
  On this repository the default build reports **0** CA warnings.
- **Security rules:** the Security category has 94 rules. **70 are disabled by default** and
  stay off until you switch them on by id, or with `AnalysisModeSecurity=All` or
  `AnalysisLevelSecurity=latest-all`. **24 are enabled but Hidden**: they run but report nothing
  at build. `Recommended` raises those 24 to warnings, and so does a category-level severity.
  Both lists are below.
- **`AnalysisMode=All` / `latest-all`:** sets 280 rules to warning. The exceptions are a few
  legacy rules and the code-metrics rules. On this repository it gives **3,641 unique findings:
  223 in production code and 3,418 in the test tier**. With `AnalysisLevelSecurity=latest-all`
  alone it gives **17 unique findings, 1 of them in production** (CA5394 in
  `src/Platform.Registry/RegistryPoller.cs:123`).
- **`EnforceCodeStyleInBuild=true` does nothing by itself:** 0 IDE findings here. IDE rules
  report at build only when a severity is set for them. With `AnalysisLevelStyle=latest-all` as
  well, it gives thousands of findings (IDE0058 923, IDE0022 779, IDE0160 445, IDE0008 357, ...).
- **`TreatWarningsAsErrors=true`** turns every `warning` into an error and leaves `suggestion`
  alone. You can hold a rule below error without a suppression in two ways, both tested here:
  set its severity to `suggestion`, or set it to `warning` and list it in `WarningsNotAsErrors`.
- **Test tier:** two ways work, both tested. The first is a separate global config added under
  `Condition="'$(SpaTestProject)' == 'true'"`. It **must declare `global_level` above 100**,
  because otherwise a root `.globalconfig` silently wins. The second is path sections in
  `.editorconfig`, which beats every global config.
- **Version:** the analyzers ship inside the SDK. The machine measured here runs SDK 10.0.401,
  not 10.0.1xx, because `global.json` sets `rollForward: latestFeature`. Its
  `_NETAnalyzersSDKAssemblyVersion` is `10.0.401`. A `PackageReference` to
  `Microsoft.CodeAnalysis.NetAnalyzers` turns the SDK's copy off and replaces it.

## Environment facts

- Tools: web fetch only (WebFetch/WebSearch). The local machine ran `dotnet` builds; no package
  was added and no build setting was committed.
- `global.json`: `"version": "10.0.100", "rollForward": "latestFeature"`. Installed SDKs:
  `10.0.401` only, so the 10.0.1xx band was **not measured**. Because of `latestFeature`, two
  machines can load different analyzer builds at `AnalysisLevel=latest`. The rule *set* for level
  10 stays fixed, but bug fixes and new post-10.0 rules (kept off by the level-10 file) can differ.
- `Directory.Build.props` sets `TreatWarningsAsErrors=true` and
  `WarningsNotAsErrors=NU1901..NU1904`, and keys the test tier off
  `$(MSBuildProjectName.EndsWith('Tests'))` → `SpaTestProject=true`. It sets no analysis
  property. `.editorconfig` has only whitespace and charset settings. There is no
  `.globalconfig`. Nothing enables an analyzer today.
- `/analyzer:` arguments seen in `dotnet build src/Platform.Abstractions -v:n`:
  `sdk/10.0.401/Sdks/Microsoft.NET.Sdk/analyzers/Microsoft.CodeAnalysis.NetAnalyzers.dll` and
  `Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll`, plus the shared-framework analyzers and
  generators (`Microsoft.AspNetCore.App.Analyzers.dll`, logging and options generators and
  others; ASP.NET Core's own ASP/MVC/BL rules are on regardless and are out of scope here).
  `/analyzerconfig:` arguments: the repo `.editorconfig`,
  `analysislevel_10_default.globalconfig` and the generated MSBuild editorconfig. The compiler
  flags include `/warnaserror+` (from `TreatWarningsAsErrors`) and `/warn:10`.

## What was executed and what was read

### Executed (2026-10-29, macOS, SDK 10.0.401)

| # | Command | Result |
| --- | --- | --- |
| E1 | `dotnet --version` | `10.0.401` |
| E2 | `dotnet build src/Platform.Abstractions -v:n` | analyzer and config arguments as listed above |
| E3 | `dotnet build SpaPlatform.slnx --no-incremental -p:TreatWarningsAsErrors=false` (no analysis property) | 0 CA warnings |
| E4 | same + `-p:AnalysisLevelSecurity=latest-all` | 17 unique: CA5399 ×13, CA5359 ×2, CA5351 ×1, CA5394 ×1 |
| E5 | same + `-p:AnalysisLevelSecurity=latest-recommended` | 3 unique: CA5359 ×2, CA5351 ×1 (the Hidden tier raised) |
| E6 | same + `-p:AnalysisMode=All` | 3,641 unique (file, position, id); 223 production, 3,418 test tier |
| E7 | same + `-p:EnforceCodeStyleInBuild=true` | 0 IDE warnings |
| E8 | same + `-p:EnforceCodeStyleInBuild=true -p:AnalysisLevelStyle=latest-all` | IDE0058 923, IDE0022 779, IDE0160 445, IDE0008 357, IDE0011 51, IDE0046 32, IDE0055 19, ... |
| E9 | Scratch probe project (see "Probe results") | precedence and severity behaviour |
| E10 | Python parse of every `analysislevel*_10_*.globalconfig` shipped in the SDK | rule counts and lists below |

Each warning appears twice in console output, once inline and once in the summary. The counts
above are de-duplicated by file, position and rule id. "Test tier" here means everything outside
`src/` plus `src/Platform.Testing` (that is `tests/`, `fixtures/`, `tools/`).

Production findings under `AnalysisMode=All` (E6), by rule: CA2007 125, CA1848 23, CA1062 21,
CA1307 16, CA1873 9, CA1031 9, CA1034 4, CA2000 2, CA1822 2, CA1812 2, CA1032 2, CA1001 2,
CA5394 1, CA1859 1, CA1716 1, CA1508 1, CA1068 1, CA1064 1. The largest whole-solution counts
come from the test tier: CA2007 (ConfigureAwait), CA1707 (underscores in test names), CA2000,
CA1062 and CA1515.

Security findings under `AnalysisLevelSecurity=latest-all` (E4), all 17:

| Rule | Where |
| --- | --- |
| CA5394 (insecure randomness) | `src/Platform.Registry/RegistryPoller.cs(123,54)`, the only production hit |
| CA5399 (HttpClient without CRL check) | `fixtures/Fixtures.Tests/IdpHarness.cs(83)`; `tests/Platform.Identity.Tests/IdentityGateway.cs(439)`, `LogoutTests.cs(117)`; `tests/Platform.Proxy.Tests/AccessTokenStepTests.cs(25,44,64,143)`, `ProxyRouteTests.cs(133)`, `RequestHeaderTests.cs(124)`, `ResponseHeaderTests.cs(72)`; `tests/Platform.Telemetry.Tests/LogCanaryTests.cs(46)`, `RequestLineTests.cs(63,82)` |
| CA5359 (certificate validation disabled) | `tests/e2e/Deployed/RawClientTests.cs(20)`, `tests/e2e/Stack/TlsLoadBalancer.cs(215)` |
| CA5351 (broken crypto algorithm) | `tests/Platform.Registry.Tests/FakeS3Server.cs(153)` |

### Read

The SDK files on disk, all read 2026-10-29, under `/usr/local/share/dotnet/sdk/10.0.401/`:

- `Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.Sdk.Analyzers.targets`:
  `_LatestAnalysisLevel=10.0`, `_PreviewAnalysisLevel=11.0`. `AnalysisLevel` defaults to
  `latest` when the TFM equals the latest level. It sets `EnableNETAnalyzers=true` for level 5
  and above and `EnforceCodeStyleInBuild=false` when unset, and it adds the CodeStyle analyzer
  DLLs only when `EnforceCodeStyleInBuild` is true.
- `Sdks/Microsoft.NET.Sdk/analyzers/build/Microsoft.CodeAnalysis.NetAnalyzers.props`:
  `_NETAnalyzersSDKAssemblyVersion=10.0.401`. The full `CodeAnalysisRuleIds` list is there too.
  When `CodeAnalysisTreatWarningsAsErrors=false` and `TreatWarningsAsErrors=true`, the props
  append all of those ids to `WarningsNotAsErrors`.
- `Sdks/Microsoft.NET.Sdk/analyzers/build/Microsoft.CodeAnalysis.NetAnalyzers.targets`: the
  config file name is `AnalysisLevel<Category>_<level>_<mode>[_warnaserror].globalconfig`,
  lower-cased. `_warnaserror` is chosen only when `CodeAnalysisTreatWarningsAsErrors` is `true`.
  Nothing in the SDK targets defaults that property from `TreatWarningsAsErrors`, and E2 shows
  this repo loading the plain `default` file. So today a CA warning becomes an error because of
  the compiler's `/warnaserror+`, not because of the config layer.
- `Sdks/Microsoft.NET.Sdk/analyzers/build/config/`: the configs for levels 5 to 11, modes
  `none|minimum|recommended|default|all`, each with a `_warnaserror` twin, plus per-category
  versions (`analysislevel{design,documentation,globalization,interoperability,maintainability,naming,performance,reliability,security,usage}_*`).
  All carry `is_global = true` and `global_level = -100`, so any user config overrides them.
- `Roslyn/Microsoft.Managed.Core.targets`, target `_ComputeSkipAnalyzers`: `RunAnalyzers=false`
  skips analyzers everywhere. With `RunAnalyzers` unset, `RunAnalyzersDuringBuild=false` skips
  them on explicit (non-design-time) builds only. Builds that VS triggers implicitly (Run Tests,
  F5) skip analyzers only when `TreatWarningsAsErrors` is not true, unless
  `OptimizeImplicitlyTriggeredBuild=true`.

Web sources, all read 2026-10-29:

| Id | URL | Page date |
| --- | --- | --- |
| W1 | https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/overview | ms.date 2025-11-05, updated 2026-05-28 |
| W2 | https://learn.microsoft.com/en-us/dotnet/core/project-sdk/msbuild-props#code-analysis-properties | ms.date 2026-09-21 |
| W3 | https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/configuration-files | ms.date 2021-07-07, updated 2024-08-31 |
| W4 | https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/configuration-options | ms.date 2026-02-13 |
| W5 | https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/security-warnings | ms.date 2019-10-02, updated 2025-07-18 (index page; the fetched text carried no table) |
| W6 | https://raw.githubusercontent.com/dotnet/sdk/main/src/Microsoft.CodeAnalysis.NetAnalyzers/src/Microsoft.CodeAnalysis.NetAnalyzers.md | `main` as of 2026-10-29 (the analyzer source moved from dotnet/roslyn-analyzers into dotnet/sdk; W1 and W4 link there) |
| W7 | https://learn.microsoft.com/en-us/visualstudio/code-quality/disable-code-analysis | ms.date 2026-04-21, updated 2026-10-20 |
| W8 | https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-options/errors-warnings | ms.date 2025-05-30, updated 2026-05-01 |
| W9 | https://raw.githubusercontent.com/dotnet/roslyn/main/src/Analyzers/Core/Analyzers/EnforceOnBuildValues.cs | `main` as of 2026-10-29 |
| W10 | https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/style-rules/ide0005 | ms.date 2023-12-13 |

## 1. What the default enables

W1, ".NET 10" tab: "The following rules are enabled, by default, as errors or warnings in .NET
10. Additional rules are enabled as suggestions." The 29 rules: CA1416, CA1417, CA1418, CA1420,
CA1422 (interoperability); CA1831, CA1856 (Error), CA1857 (performance); CA2013, CA2014,
CA2015, CA2017, CA2018, CA2021, CA2022, CA2023 (reliability); CA2200, CA2247, CA2252 (Error),
CA2255 to CA2261, CA2264, CA2265, CA2266 (usage). None is in the Security category.

How the default works, read from the SDK (E10):

- `analysislevel_10_default.globalconfig` holds one entry, `CA1516 = none`. Its header says:
  "Rules with enabled-by-default state from '10.0' release ... Rules that are first released in a
  version later than '10.0' are disabled." So severity in Default mode is each descriptor's
  built-in default.
- `analysislevel_10_none.globalconfig` sets 174 rules to `none`. Those 174 are the rules that
  are enabled by default at some severity. A rule absent from that file is disabled by default.
- `minimum` raises 92 rules to warning, `recommended` raises 145, and `all` raises 280. Each
  file also sets one rule to `none`, CA1516.
- W1 and W2 say `All` does **not** enable CA1017, CA1045, CA1005, CA1014, CA1060, CA1021 or the
  code-metrics rules CA1501, CA1502, CA1505, CA1506 and CA1509. You have to enable those by id.

Rules per category in the level-10 files: Usage 47, Reliability 20, Performance 65, Design 42,
Naming 14, Maintainability 9, Globalization 9, Interoperability 8, Documentation 1, Security 94.

## 2. Security rules: what is off and must be switched on by id

Two sources agree on every id. The first is the SDK's own `analysislevelsecurity_10_*.globalconfig`
files on disk (E10): a rule is enabled by default exactly when the `none` file lists it. The
second is W6's "Enabled" and "Severity" columns.

**Tier A: disabled by default (70 rules).** These report nothing until you enable them by id,
or with `AnalysisModeSecurity=All` or `AnalysisLevelSecurity=latest-all`. A category-level
`dotnet_analyzer_diagnostic` entry does **not** reach them (W4, "Important"). W6 gives each a
default severity of Warning once it is enabled.

- CA2xxx: CA2100 (SQL queries), CA2119, CA2153.
- CA23xx, insecure deserialization: CA2300, CA2301, CA2302, CA2305, CA2310, CA2311, CA2312,
  CA2315, CA2321, CA2322, CA2326, CA2327, CA2328, CA2329, CA2330, CA2350, CA2351, CA2352,
  CA2353, CA2354, CA2355, CA2356, CA2361, CA2362.
- CA30xx, taint-flow injection: CA3001 (SQL injection), CA3002 (XSS), CA3003 (file path),
  CA3004 (information disclosure), CA3005 (LDAP), CA3006 (process command), CA3007 (open
  redirect), CA3008 (XPath), CA3009 (XML), CA3010 (XAML), CA3011 (DLL), CA3012 (regex).
- CA5xxx, cryptography, TLS, cookies and tokens: CA5358 (cipher mode), CA5361 (SChannel strong
  crypto), CA5362, CA5367, CA5375, CA5376, CA5377, CA5378 (ServicePointManager protocols),
  CA5380, CA5381 (root store), CA5382, CA5383 (secure cookies in ASP.NET Core), CA5386
  (hard-coded SecurityProtocolType), CA5387, CA5388 (weak KDF iteration count), CA5389 (archive
  path traversal), CA5390 (hard-coded key), CA5391 (antiforgery in MVC), CA5392, CA5393
  (DllImport search path), CA5394 (insecure randomness), CA5395 (missing HttpVerb), CA5396
  (HttpOnly cookie), CA5398 (hard-coded SslProtocols), CA5399, CA5400 (CRL check), CA5401,
  CA5402 (CreateEncryptor IV), CA5403 (hard-coded certificate), CA5404, CA5405 (token
  validation checks).

**Tier B: enabled by default but Hidden (24 rules).** These run at build and report nothing
(W4: Hidden corresponds to `silent`). `Recommended` and `All` raise them to warning, and so does
`dotnet_analyzer_diagnostic.category-Security.severity = warning`:
CA3061, CA3075, CA3076, CA3077, CA3147, CA5350, CA5351, CA5359, CA5360, CA5363, CA5364,
CA5365, CA5366, CA5368, CA5369, CA5370, CA5371, CA5372, CA5373, CA5374, CA5379, CA5384, CA5385,
CA5397.

How much the taint rules (CA3001 to CA3012) cost, and how many false positives they raise, was
not measured. They produced no finding on this codebase under E4.

## 3. Incantations

MSBuild (W2):

```xml
<!-- whole security category as warnings, pinned to the 10.0 rule set -->
<AnalysisLevelSecurity>10-all</AnalysisLevelSecurity>        <!-- or latest-all / latest-recommended -->
<!-- equivalent mode-only form; AnalysisLevel wins if both are set -->
<AnalysisModeSecurity>All</AnalysisModeSecurity>
<!-- whole solution -->
<AnalysisLevel>latest-recommended</AnalysisLevel>             <!-- or latest-all, 10-all ... -->
<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>       <!-- IDE rules on build; inert until severities are set -->
<AnalysisLevelStyle>latest-recommended</AnalysisLevelStyle>  <!-- IDE rules' bulk level (needs EnforceCodeStyleInBuild) -->
```

The available `AnalysisLevel<Category>` and `AnalysisMode<Category>` suffixes are Design,
Documentation, Globalization, Interoperability, Maintainability, Naming, Performance,
SingleFile, Reliability, Security, Style and Usage (W2). Pinning a number such as `10-all`
instead of `latest-all` keeps an SDK roll-forward from enabling new rules (W1, "Latest
updates").

Per-id, in a global config (no section headers) or an `.editorconfig` section (W3, W4):

```ini
is_global = true
dotnet_diagnostic.CA3001.severity = warning
dotnet_diagnostic.CA3002.severity = warning
dotnet_diagnostic.CA5394.severity = suggestion   # held below error under TreatWarningsAsErrors
```

Category and all-rules entries use a different prefix and reach only enabled-by-default rules
(W4):

```ini
dotnet_analyzer_diagnostic.category-Security.severity = warning   # Tier B only
dotnet_analyzer_diagnostic.severity = suggestion
```

W4 gives the order of precedence: id beats category, and category beats all. When
`AnalysisMode` or `AnalysisLevel` sets a mode, bulk `dotnet_analyzer_diagnostic` entries are
ignored.

IDE rules: set `EnforceCodeStyleInBuild=true` and give each rule a severity:
`dotnet_diagnostic.IDE0040.severity = warning`, or since .NET 9
`dotnet_style_require_accessibility_modifiers = always:warning` (W1). IDE0005 reports on build
only if the project generates XML documentation (`GenerateDocumentationFile`) (W10).

## 4. Severity, precedence and the test tier

Precedence between files (W3): within one file, the later entry wins. Between two
`.editorconfig` files, the deeper one wins. **An `.editorconfig` beats any global config.**
Between global configs, the higher `global_level` wins. A file named `.globalconfig` defaults
to 100, and any other global config defaults to 0. Two files at an equal level trigger a compiler
warning, and both entries are ignored. Command-line `-nowarn` and `-warnaserror` beat both kinds
of file. The SDK's own files sit at `-100`.

Probe results (E9). The probe was a scratch library with `new System.Random().Next()`,
`TreatWarningsAsErrors=true` and `AnalysisLevelSecurity=latest-all`:

| Case | Setup | Result |
| --- | --- | --- |
| A | no override | `error CA5394`, build fails |
| B | root `.globalconfig`: `CA5394 = suggestion` | build succeeds; suggestion is not escalated |
| C | root `.globalconfig` `= error` + `test.globalconfig` (via `GlobalAnalyzerConfigFiles`, no `global_level`) `= none` | `error CA5394`: the test file loses (level 0 < 100) |
| D | as C, test file `global_level = 200` | build succeeds: the test file wins |
| E | as C, test file `global_level = 100` | `warning MultipleGlobalAnalyzerKeys ... It has been unset`; CA5394 not reported; build succeeded (this warning was not escalated) |
| F | root `= warning`, `-p:WarningsNotAsErrors=CA5394` | `warning CA5394`, build succeeds |
| G | root `.globalconfig` `= error`, `.editorconfig` `[sub/**.cs]` `CA5394 = none` | only the root file errors; `sub/Q.cs` is silent |

Ways to give the test tier a different severity from the one `Directory.Build.props`:

1. **A conditional global config.** W3 shows this pattern for test projects. The override file
   must set `global_level` above 100 whenever a root `.globalconfig` exists (cases C and D):

   ```xml
   <ItemGroup Condition="'$(SpaTestProject)' == 'true'">
     <GlobalAnalyzerConfigFiles Include="$(MSBuildThisFileDirectory)CodeAnalysis.tests.globalconfig" />
   </ItemGroup>
   ```

   ```ini
   is_global = true
   global_level = 200
   dotnet_diagnostic.CA2007.severity = none
   dotnet_diagnostic.CA1707.severity = none
   ```

   `SpaTestProject` covers only names ending in `Tests`. `Platform.Testing`, `tools/spactl` and
   `tools/bench/floor` would need their own condition, for example a second property set from
   `$(MSBuildProjectName)` or `$(MSBuildProjectDirectory)`. Projects under `fixtures/` whose
   names do not end in `Tests` would need one too.
2. **Conditional MSBuild properties**, such as `AnalysisLevelSecurity` or `AnalysisMode` inside
   a `PropertyGroup Condition="'$(SpaTestProject)' == 'true'"`. This changes whole modes per
   tier but cannot express per-id exceptions.
3. **Path sections in `.editorconfig`** (`[tests/**.cs]`, `[fixtures/**.cs]`,
   `[src/Platform.Testing/**.cs]`, `[tools/spactl/**.cs]`). These need no MSBuild condition and
   beat every global config (case G). The cost is that a production setting written in
   `.editorconfig` can no longer be relaxed from a global config.

## 5. TreatWarningsAsErrors and the ramp

- W4: `warning` "do[es] not cause builds to fail (unless you have an option set to treat
  warnings as errors)". `suggestion` appears as a build message. W8: `TreatWarningsAsErrors`
  turns all warnings into errors, and `WarningsNotAsErrors` exempts a list.
- Cases B and F confirm both routes for holding a rule below error without a `#pragma` or
  `SuppressMessage`.
- The SDK's switch for a whole category is `CodeAnalysisTreatWarningsAsErrors=false` (W1, W2).
  Its props append every CA id to `WarningsNotAsErrors`, so CA warnings stay warnings while CS
  warnings still fail the build.
- Under `TreatWarningsAsErrors=true`, any rule a mode raises to warning breaks the build at
  once. Raising a mode therefore needs a ramp: rules held at `suggestion`, or listed in
  `WarningsNotAsErrors`, until they are clean.

## 6. Build-time cost

- The docs give no figures. W1 and W2 say only that, "for performance reasons", a handful of
  IDE rules run in Visual Studio alone. W9 lists the rules marked `EnforceOnBuild.Never`: IDE0001,
  IDE0002, IDE0003, IDE0009, IDE0049, IDE0050, IDE0035, IDE0079, IDE3000 and JSON002. The source
  comment on the IDE0001 group reads "TODO: Allow enforcing simplify names ... once we validate
  their performance characteristics". The same file sorts the other IDE rules into
  WhenExplicitlyEnabled (30), Recommended (72) and HighlyRecommended (20).
- W7 documents `RunAnalyzersDuringBuild`, `RunAnalyzersDuringLiveAnalysis` and `RunAnalyzers`
  (each defaults to `true`; `RunAnalyzers=false` overrides the other two). The page states this
  under its .NET Framework heading; the Roslyn targets read in section "Read" apply to every
  SDK project. W8: `<ReportAnalyzer>true</ReportAnalyzer>` with `-v:detailed` reports each
  analyzer's execution time, which is how to measure the cost here.
- Wall-clock cost on this repository was not measured.

## 7. SDK-bundled versus a PackageReference

W1, "Latest updates": installing the `Microsoft.CodeAnalysis.NetAnalyzers` NuGet package "turns
off the built-in SDK analyzers". If the SDK's copy is newer, you get a build warning, which
`_SkipUpgradeNetAnalyzersNuGetWarning=true` silences. With the package installed,
`EnableNETAnalyzers` should not be set; setting it to true raises a warning. The SDK props
confirm the mechanism: the package carries `DisableNETAnalyzersForNuGetPackage.props`, and the
SDK's copy sets `_NETAnalyzersSDKAssemblyVersion` only when that file is absent. Under
`rollForward: latestFeature` the bundled version follows the newest SDK installed on each machine.
What SDK 10.0.1xx ships was not measured, because only 10.0.401 is installed.
