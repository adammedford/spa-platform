# CodeQL for C# on this solution

- **Ticket:** #587, "CodeQL for C# on this solution: setup mode, build mode, query suite, SARIF upload of
  Roslyn diagnostics, and the required-check pattern" (`wayfinder:research`), on the map #584.
- **Date:** 2026-10-29. **Branch:** `research/codeql-csharp`, pushed and never merged, as the map rules.
- **Method:** primary sources only: docs.github.com, the GitHub changelog, the `github/codeql-action`
  source and README (at `2d7aead`), the C# query suites, suite helpers and extractor source in `github/codeql`
  (at `18b48aa`), and Microsoft Learn for the compiler's `ErrorLog`. Every source was read on 2026-10-29.
  Section 8 lists what I ran and what I only read. Nothing in repository settings was changed and no
  workflow file was added. Nothing was uploaded to code scanning, so every claim about how an upload
  behaves comes from the docs or the action's source, not from a run.

## Answer in brief

1. **Setup mode: advanced** (a job in a workflow), not default setup. Default setup fixes C# to
   `build-mode: none` and offers only the `default` and `security-extended` suites. It has no config
   file, so `spikes/` cannot be excluded. It runs outside the classifier pattern. Turning it on
   disables any CodeQL workflow and blocks CodeQL API uploads ([default setup][gh-default]).
2. **Build mode: `manual`**, running the same `dotnet build SpaPlatform.slnx` as CI. It analyses
   what the solution builds, and only that, so `spikes/` stays out without any config. It sees
   source-generator output, because the tracer injects `EmitCompilerGeneratedFiles=true`. It uses the
   SDK that `setup-dotnet` resolves from `global.json`, roll-forward included. `none` would put
   every tracked `.cs` file, `spikes/` too, into one compilation without the repository's source
   generators (`GeneratedRegex`, `LoggerMessage`). It would also install exactly SDK `10.0.100`,
   ignoring `rollForward`. `none` stays a working fallback if it also sets `paths-ignore: [spikes/**]`.
3. **Query suite: `security-extended`.** For C# it adds 11 queries to the 44 in `default`. These are
   medium-precision security queries, and high-precision ones at any severity. `security-and-quality`
   adds a fixed list of 101 maintainability and reliability queries on top. It is noisier and cannot
   fail the build in a useful way, so it is not worth it here.
4. **Required check:** put a `CodeQL` job in `ci.yml` with `needs: documents` and the Build job's own
   `if` (`!cancelled() && needs.documents.outputs.build != 'false'`). Give it job-level
   `security-events: write`. Require it **by its job name**, like `Build and test`. A pull request
   that touches no C# skips the job by its `if`, and GitHub reports that as Success. **Do not
   require the generated `Code scanning results / CodeQL` check.** It exists only when an analysis is
   uploaded, so a pull request without C# would never get it and would wait forever.
5. **Roslyn SARIF:** possible, with work, but it adds little here. With `TreatWarningsAsErrors` on,
   a green build's error log holds only `note`-level results: 73 notes across 29 projects, no
   warnings or errors (executed). Uploading needs SARIF 2.1.0 (`ErrorLog=<file>,version=2.1`; the
   default is SARIF 1). The 29 per-project runs must be merged into one, because they share a tool
   name and have no category, and the action refuses that on github.com. The rules have no `help.text`,
   which the docs mark as required.
6. **Test projects:** keep them in the database. Code scanning labels alerts in test paths `Test`
   by path. In `manual` mode the config file's `paths-ignore` does not filter C# that is built. The
   only ways to keep tests out of the database are to build fewer projects or to filter the SARIF
   afterwards. `InternalsVisibleTo` does not matter to the extractor in either mode (section 6).
7. **Cost:** nothing on this public repository. Standard GitHub-hosted runner minutes are free for
   public repositories ([Actions billing][gh-actions-bill]). The action "may be used on open source
   projects hosted on GitHub" ([codeql-action README][cqa-readme]); private repositories need a
   GitHub Code Security licence ([about code scanning][gh-about-cs]).

## 1. What the repository has today (executed)

- `gh api repos/adammedford/spa-platform/code-scanning/default-setup` returned
  `"state":"not-configured"`, `"query_suite":"default"`, with detected languages `actions, csharp,
  javascript, javascript-typescript, python, ruby, typescript`.
- `gh api .../code-scanning/analyses` returned `404 no analysis found`. No code scanning of any kind
  has uploaded here.
- `gh api .../branches/main/protection`: classic branch protection with required contexts
  `Build and test`, `Terraform` and `Documents` (all `app_id` 15368, GitHub Actions), `strict: false`.
  `enforce_admins` is off.
- `gh api .../rulesets` returned `[]`, so no ruleset exists, and in particular no "Require code
  scanning results" rule. Run alone, it exits 0.
- The workflows are `.github/workflows/ci.yml` and `ticket-shape.yml`. `ci.yml` has the jobs
  `Documents`, `Scripts under bash 3.2`, `Build and test` (with
  `if: ${{ !cancelled() && needs.documents.outputs.build != 'false' }}`) and `Terraform`. The
  `build` answer comes from `.github/touches.sh build`, whose pattern is
  `^(src|tests|fixtures|contracts|tools/spactl)/|\.(cs|csproj|props|targets|slnx)$|^(global\.json|nuget\.config|\.dockerignore)$`.
- `global.json` pins `sdk.version` `10.0.100` with `rollForward: latestFeature`. The local SDK is
  `10.0.401`.
- Tracked `.cs` files by top-level folder: `src` 114, `tests` 295, `fixtures` 31, `spikes` 37, `tools` 9.
  `spikes/` is outside the solution and has its own `Directory.Packages.props`. Ten files named
  `Program.cs` are tracked, six of them under `spikes/`. Source generators are used in `src`, with 21
  `GeneratedRegex`/`LoggerMessage` sites, for example in `src/Platform.Telemetry/Redaction.cs`.
  `InternalsVisibleTo` appears in four projects (Telemetry, Hosting, Identity and Gateway.TestHarness).

## 2. Setup mode

**Default setup** creates a database "without requiring a build" for C#, sets C# to build mode `none`,
needs GitHub Actions enabled, and offers a suite choice in its settings ([default setup][gh-default];
[compiled languages][gh-compiled]). Of the built-in suites, `default` and `security-extended` are
"available with default setup", and `security-and-quality` is available only with advanced setup
([query suites][gh-suites]). Switching to default setup "will override existing code scanning
configurations", "will disable the existing workflow file and block any CodeQL analysis API uploads"
([default setup][gh-default]). It also schedules a weekly scan, which is disabled after six months
with no pushes or pull requests.

Why not default setup here:
- It cannot take a config file, so `paths-ignore` is unavailable, and with `none` the `spikes/` tree
  joins the database (section 3).
- It runs as GitHub's own dynamic workflow, outside `ci.yml`, so its check is not under the classifier
  `if`. The docs I read do not say whether its pull-request runs are path-filtered. I have not
  established how its check behaves for a pull request that touches no C#, so requiring it is unsafe
  until someone observes it.
- It would also scan the other detected languages (`actions`, `javascript-typescript`, `python`,
  `ruby`) unless they are removed in its settings.

**Advanced setup** is a job in a workflow using `github/codeql-action/init` and `analyze`. The current
major version is `v4`, and `v3` is still listed as supported ([codeql-action README][cqa-readme]; the
CHANGELOG's latest is 4.39.1 of 21 Oct 2026, [CHANGELOG][cqa-changelog]). It accepts `build-mode` and
`queries` inputs and a config file with `paths`/`paths-ignore` ([workflow options][gh-wfopts]).

## 3. Build mode for this solution

C# supports `none`, `autobuild` and `manual` ([build options][gh-buildopts]).

### `none`, read from the docs and the extractor source

- The docs say restore uses `*.csproj`, `*.sln`, `nuget.config`, `packages.config`, `global.json` and
  `project.assets.json`. It needs internet or private-feed access. When several versions of a NuGet
  package are present, CodeQL uses one, "usually the newer". When several .NET versions are referenced,
  it uses one. "Colliding class names" can lose call targets ([build options][gh-buildopts]). The
  build-mode table lists "Generated code not analyzed" for `none` ([compiled languages][gh-compiled]).
- **Which files.** `FileProvider` enumerates every file under the source directory recursively
  (`SourceDir.GetFiles("*.*", … RecurseSubdirectories = true)`), takes the `.cs` files, and filters
  them only by `LGTM_INDEX_FILTERS` ([FileProvider.cs][cq-fileprovider];
  [FilePathFilter.cs][cq-filepathfilter]). The config file's `paths`/`paths-ignore` reach that
  variable, and the docs list them as "currently supported for C/C++, C#, Java and Rust" in no-build
  mode ([workflow options][gh-wfopts]). On this repository that set includes `spikes/`: 37 files,
  six `Program.cs` files, and a separate central-package file with its own versions. Those trigger the
  colliding-class-name and multiple-versions caveats.
- **One compilation.** The standalone extractor builds a single
  `CSharpCompilation.Create(output, syntaxTrees, references, new CSharpCompilationOptions(OutputKind.ConsoleApplication, allowUnsafe: true))`
  from `dependencyManager.AllSourceFiles` ([Standalone/Extractor.cs][cq-standalone]). Every project's
  sources, tests included, are one assembly, so `InternalsVisibleTo` has nothing to grant.
- **Which generated files.** `AllSourceFiles` is the files on disk plus the output of three of
  CodeQL's own generators: `ImplicitUsingsGenerator`, `RazorGenerator` and `ResxGenerator`
  ([DependencyManager.cs][cq-depmgr]). The project's Roslyn source generators do not run. The
  `LoggerMessage` and `GeneratedRegex` partial methods are therefore declared but have no body in
  the database. That flow through generated logging code is then invisible to the log-injection queries
  is my inference; I did not test it.
- **SDK.** `DotNet.WithDotNet` reads only `sdk.version` from each `global.json` and needs an
  exact match (`sdk.StartsWith(version + " ")`). If no installed SDK matches, it runs `dotnet-install`
  for that version, and falls back to `LatestDotNetSdkVersion = "10.0.100"`
  ([DotNet.cs][cq-dotnet]; [DependencyManager.cs][cq-depmgr]). `rollForward` is ignored, so on a
  runner without 10.0.100 the extractor downloads that exact SDK for its restore.
- **Central package management.** No GitHub doc or changelog entry I read mentions
  `Directory.Packages.props`. Buildless mode runs `dotnet restore` against the reachable feeds
  (changelog 6.0.0 and 7.3.0, [C# CHANGELOG][cq-cs-changelog]). Central package management is
  resolved by restore itself, so I expect it to work, but that is an inference.
- **.NET 10 and `.slnx`.** Changelog 5.4.5 added `.slnx` support to autobuild and `build-mode: none`,
  and made .NET 10 the default in `none`. 6.0.2 says "Full support for C# 14 / .NET 10" ([C# CHANGELOG][cq-cs-changelog]).

### `manual` (recommended)

- "CodeQL will analyze whatever source code is built by your specified build steps" ([compiled
  languages][gh-compiled]). A `dotnet build SpaPlatform.slnx -c Release` after `setup-dotnet` with
  `global-json-file: global.json` analyses exactly the solution CI builds, on the SDK CI resolves. That
  build leaves out `spikes/`, honours roll-forward, and resolves central package management the way
  CI does.
- The tracer injects `/p:UseSharedCompilation=false` (slower builds), `/p:EmitCompilerGeneratedFiles=true`
  (writes source-generator output to disk so it is extracted) and `/p:MvcBuildViews=true`. The docs
  warn that "your C# code [may] build in a different way" ([build options][gh-buildopts]). Changelog
  5.4.5 fixed "compiler-generated files were not being extracted".
- Put CodeQL in its own job, not in `Build and test`. Otherwise a CodeQL failure turns the build
  check red, and the build check stops measuring the real build. The cost is a second checkout,
  restore and build. Locally the whole solution builds in about 5 s (executed, section 8). The CodeQL
  analysis itself is extra, and I did not measure it.
- **`autobuild`** would run `dotnet build` on the solution or project closest to the root, which is
  `SpaPlatform.slnx` here (`.slnx` is supported from 5.4.5) ([build options][gh-buildopts]). It does
  the same work with less control over configuration and flags, so `manual` is preferred.

## 4. Query suites for C#

Read from `github/codeql` at `18b48aa` and the docs ([C# queries][gh-cs-queries]):

| Suite | Definition | C# count |
| --- | --- | --- |
| `default` (`csharp-code-scanning.qls`) | `problem`/`path-problem` with precision `high`/`very-high`, severity `error`/`warning`, tagged `security` ([selector][cq-sel-cs]) | 44 |
| `security-extended` | Any severity for `security`-tagged queries with precision `high`/`very-high`, plus `security`-tagged queries with precision `medium` and severity `error`/`warning` ([selector][cq-sel-ext]) | 44 + 11 = 55 |
| `security-and-quality` | `security-extended`, plus a frozen explicit list of 101 `cs/...` quality ids ([suite][cq-saq]; [frozen selector][cq-sel-frozen]) | 55 + 101 ids (not counted on any docs page) |

The 11 queries `security-extended` adds for C# are: empty password in configuration file (CWE-258/862),
insecure direct object reference (639), insecure SQL connection (327), missing function-level access
control (285/284/862), missing XML validation (112), serialization check bypass (020), two
thread-unsafe `ICryptoTransform` queries (362), use of file upload (434), and two value-shadowing queries
(348) ([C# queries][gh-cs-queries]). Several concern ASP.NET authorisation and request handling, which
is this gateway's surface.

The `security-and-quality` additions were frozen on 2025-06-10 (commit "C#: Freeze quality queries in
the security-and-quality suite"). New quality work goes to separate `csharp-code-quality*.qls` suites
([codeql-suites][cq-suites-dir]). The frozen list includes `cs/catch-of-all-exceptions`,
`cs/dereferenced-value-may-be-null`, `cs/local-not-disposed`, `cs/useless-assignment-to-local`,
`cs/xmldoc/missing-summary` and similar maintainability checks. Many of them overlap the .NET analyzers
the build already enforces.

Selection: `queries: security-extended` on `init` ([workflow options][gh-wfopts]).

## 5. The required-check pattern

GitHub's table ([troubleshooting required checks][gh-skipped]):
- A workflow skipped by path filtering, branch filtering or a commit message: "Associated checks stay
  in a 'Pending' state and block merging".
- "A job is skipped by a conditional": "The job reports 'Success'".
- "A job depends on a failed job": the dependent job is skipped and may not block. The fix is
  `always()` with `needs`. The repository's `!cancelled()` does the same job, as `ci.yml`'s header
  explains.

Three different things can look like "the CodeQL check":

1. **The Actions job check**, the job's `name`, for example `CodeQL`. Under the classifier `if`, a
   pull request that touches no C# skips the job, which reports Success. It is exactly as safe to
   require as `Build and test`, so **this is the one to require.** Adding it to the required contexts
   is a settings change that this session did not make.
2. **`Code scanning results / CodeQL`**, the check GitHub itself creates after an analysis is
   uploaded. It fails when it finds results of severity `error`, `critical` or `high`, and the
   threshold can be configured. Only alerts on changed lines are annotated
   ([triaging alerts in PRs][gh-pr-alerts]). It exists only after an upload, so on a pull request
   whose CodeQL job was skipped it never appears. Requiring it by name under classic protection would
   leave such a pull request waiting, which is the same failure the `paths` rule exists to avoid.
   This conclusion comes from how required checks work; I did not observe it on this repository.
3. **The ruleset "Require code scanning results" rule.** It blocks a merge when a required tool finds
   an alert at the configured threshold, when "a required tool's analysis is still in progress", or
   when "a required tool is not configured for the repository" ([available rules][gh-rules];
   [merge protection][gh-merge-prot]). It "is not related to status checks". It applies only to alerts
   whose lines are all in the pull request diff, and it does not apply to merge queue groups. None of
   the pages I read say how it treats a pull request whose head commit has no analysis because the
   job was skipped. That is unverified, and the repository has no rulesets today.

To make a high-severity finding block the merge without option 2 or 3, the required job can fail
itself. For example, `analyze` with `output:` and a step that fails on any result above a chosen
level. That is a design option; I did not verify it.

Other consequences of living in `ci.yml`:
- **Job permissions.** The workflow sets `permissions: contents: read`, so the job needs its own
  `permissions: { contents: read, security-events: write }`. `upload-sarif` and `analyze` require
  `security-events: write`. `actions: read` is needed only on private repositories
  ([uploading SARIF][gh-upload]; [upload-sarif action.yml][cqa-upload-yml]).
- **Pushes to main.** These also go through the classifier, so main's analysis updates only when a
  push touches build inputs. Pull-request alerts are compared against the latest main analysis. That
  this is enough, given main's C# changes only through pushes that run the job, is my inference.
- **Schedule.** A `schedule:` trigger added to `ci.yml` would run every job. The decision step's
  `*) base="$BEFORE"` branch has no `before` on a schedule event, so `git rev-parse` fails and
  "every job runs" (read from `ci.yml`, not executed). A weekly re-scan, which picks up new queries,
  fits better in a small separate workflow that only schedules. No check needs to be required there,
  so `paths` and pending checks do not come up.
- **Category.** Without `category`, `analyze` derives one from the workflow file name, the action and
  any matrix variables ([workflow options][gh-wfopts]). A job in `ci.yml` and one in a scheduled
  workflow therefore produce two categories, and so two analyses. Set the same explicit `category`
  (for example `/language:csharp`) in both so they are one analysis.

## 6. Test projects, `InternalsVisibleTo`, source generators

- **Labels, not exclusion.** Code scanning labels alerts outside application code as `Generated`,
  `Test`, `Library` or `Documentation`, "by file path"; "You cannot manually categorize source files"
  ([about alerts][gh-about-alerts]). The page does not say whether `Test` alerts are left out of the
  results check.
- **Excluding from the database.** In `manual` mode the config file's `paths`/`paths-ignore` "don't
  limit scanning" for a compiled language that is built; "you must change the build steps"
  ([workflow options][gh-wfopts]). To keep tests out, build only `src/`, `fixtures/` and
  `tools/spactl`, not the solution. That would make the CodeQL build differ from CI's. In `none`
  mode, `paths-ignore: [tests/**]` would exclude them.
- **Recommendation: keep tests in the database.** They do not change the flow analysed in `src`. The
  `Test` label separates them in the alert list. Dismissing a test alert costs less than running a
  build that drifts from CI's. If test alerts turn out to be noisy, filter the output SARIF, which is
  an extra step and is unverified here.
- **`InternalsVisibleTo`.** In `manual` mode each project is extracted from its real `csc` invocation,
  so the compiler's own handling applies (from the "whatever source code is built" model; I did not
  check the tracer source). In `none` mode the single compilation makes it irrelevant (section 3).
- **Source generators.** `manual` extracts their output through `EmitCompilerGeneratedFiles=true`.
  `none` does not run them (section 3).

## 7. Uploading Roslyn diagnostics with `upload-sarif`

**What the build produces (executed).** In the worktree I ran
`dotnet build -c Release "-p:ErrorLog=roslyn.sarif%2Cversion=2.1" -v q`, with the comma escaped
as `%2C` for the command line. It wrote one `roslyn.sarif` per project directory: 29 files, each with
`version` `2.1.0` and exactly one run whose tool is `Microsoft (R) Visual C# Compiler`
`5.9.0-1.26423.113`. No file has `automationDetails`. Their results total 73, all `level: note`, with
no `warning` or `error`. Examples: `CA1859`, `CA1873`, `CA1861`, `SYSLIB1045`, `CA1822`, `ASP0015`,
`xUnit2032`. Most are in `tests/` and `fixtures/`, and nine `CA1873` and a few others are in `src/`.
NuGet advisories (`NU1903`) appear in the build output, not in these logs, because they are not
compiler diagnostics. URIs are absolute (`file:///Users/...`). Rules carry
`id`, `shortDescription`, `helpUri`, and `fullDescription` on 294 of 371 in the Identity project's log, the one checked for it. No rule in that log has `help`.
Gzipped together the 29 files are about 1.09 MB. I deleted them afterwards.

**Why only notes:** `Directory.Build.props` sets `TreatWarningsAsErrors`, so any warning fails the
build. A green build's log therefore holds only diagnostics below warning. With the results check's
default thresholds, these notes never fail anything ([triaging alerts in PRs][gh-pr-alerts]).

**Mechanics and limits:**
- **Version.** "Code scanning only supports SARIF version `2.1.0`" ([SARIF support][gh-sarif]).
  `ErrorLog` defaults to version "1". `version=2` and `version=2.1` "both mean SARIF version 2.1.0"
  ([ErrorLog][ms-errorlog]). The `,version=2.1` suffix is mandatory, and on the command line its comma
  has to be escaped.
- **Same tool, same category.** For a directory, the action combines the files. If they are not all
  from CodeQL and the runs are not unique by tool and category, it throws "The CodeQL Action does not
  support uploading multiple SARIF runs with the same category" ([upload-lib.ts][cqa-uploadlib]).
  Server-side combining stopped on github.com on 22 July 2025 ([changelog][gh-cl-combine]).
  Twenty-nine runs of one tool with no category are refused. The options are to merge the runs into
  one run before upload, or to make 29 `upload-sarif` calls with distinct `category` values, which
  would create 29 analyses.
- **Limits.** 10 MB gzipped per file, 20 runs per file, 25,000 rules per run, 25,000 results per run
  (the top 5,000 shown), 20 tags per rule (10 shown) ([SARIF support][gh-sarif]). Merged into one
  run, this repository is far inside them. Concatenated, 29 runs would exceed the 20-run limit.
- **Required properties.** The page's table marks `help.text` and `fullDescription.text` on each rule,
  and `partialFingerprints` on each result, as required. The action computes `partialFingerprints` when
  the source is in the checkout ([uploading SARIF][gh-upload]; [SARIF support][gh-sarif]). Roslyn
  emits no `help`. The action validates only against the 2.1.0 JSON schema ([upload-lib.ts][cqa-uploadlib]).
  Whether the service rejects rules without `help.text` is unverified until a real upload, so a merge
  step should fill it in from `helpUri`.
- **Paths.** `checkout_path` (default `${{ github.workspace }}`) is "used to relativize any absolute
  paths in the uploaded SARIF file" ([upload-sarif action.yml][cqa-upload-yml]).
- **Interaction with default setup.** If default setup is ever enabled, it blocks CodeQL analysis API
  uploads ([default setup][gh-default]). The docs I read do not say whether third-party uploads are
  blocked too.

**Verdict:** it is feasible as a separate analysis category (for example `roslyn`) next to CodeQL in the
same Security tab, after a merge step. Today it would show 73 informational notes and could not
block anything, so it is low value until the project deliberately runs analyzers below warning
severity. The `ErrorLog` property belongs in `Directory.Build.props`, which is the integrator's file.

## 8. What I executed and what I read

**Executed** (this session, 2026-10-29):
- `gh issue edit 587 --add-assignee @me`; `gh issue view 587`.
- Read-only API calls: `code-scanning/default-setup` (`not-configured`), `code-scanning/analyses`
  (404), `branches/main/protection` (three required contexts), `rulesets` (`[]`). Section 1 has the
  results.
- `gh api` reads of `github/codeql` and `github/codeql-action` sources and suite files at the shas above.
- In the worktree: `dotnet --version` (10.0.401), the `ErrorLog` build in section 7 (succeeded,
  100 warnings in the console, all NuGet advisories, 0 errors, 4.7 s), and a Python inspection of the 29
  logs. I deleted the logs. Nothing went to code scanning.

**Read only:** everything else, through WebFetch and WebSearch. WebFetch answers are condensed by a
model under a quote-length limit, so where precise wording mattered I used the verbatim YAML or
TypeScript fetched through `gh api` instead. **Not done:** no repository setting was changed, no
workflow file was added, and no SARIF was uploaded.

**Open, to settle on the first real run:** whether a ruleset's code-scanning rule treats a pull
request with no head analysis as "not configured"; whether the service accepts Roslyn rules without
`help.text`; how long CodeQL `manual` analysis takes on this solution; and how default setup's check
behaves on a pull request that touches no C#.

[gh-default]: https://docs.github.com/en/code-security/code-scanning/enabling-code-scanning/configuring-default-setup-for-code-scanning
[gh-compiled]: https://docs.github.com/en/code-security/code-scanning/creating-an-advanced-setup-for-code-scanning/codeql-code-scanning-for-compiled-languages
[gh-buildopts]: https://docs.github.com/en/code-security/reference/code-scanning/codeql/codeql-build-options-and-steps-for-compiled-languages
[gh-suites]: https://docs.github.com/en/code-security/code-scanning/managing-your-code-scanning-configuration/codeql-query-suites
[gh-cs-queries]: https://docs.github.com/en/code-security/code-scanning/managing-your-code-scanning-configuration/csharp-built-in-queries
[gh-wfopts]: https://docs.github.com/en/code-security/reference/code-scanning/workflow-configuration-options
[gh-skipped]: https://docs.github.com/en/pull-requests/collaborating-with-pull-requests/collaborating-on-repositories-with-code-quality-features/troubleshooting-required-status-checks#handling-skipped-but-required-checks
[gh-pr-alerts]: https://docs.github.com/en/code-security/code-scanning/managing-code-scanning-alerts/triaging-code-scanning-alerts-in-pull-requests
[gh-rules]: https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets
[gh-merge-prot]: https://docs.github.com/en/code-security/concepts/code-scanning/merge-protection
[gh-about-alerts]: https://docs.github.com/en/code-security/code-scanning/managing-code-scanning-alerts/about-code-scanning-alerts
[gh-about-cs]: https://docs.github.com/en/code-security/code-scanning/introduction-to-code-scanning/about-code-scanning
[gh-sarif]: https://docs.github.com/en/code-security/code-scanning/integrating-with-code-scanning/sarif-support-for-code-scanning
[gh-upload]: https://docs.github.com/en/code-security/code-scanning/integrating-with-code-scanning/uploading-a-sarif-file-to-github
[gh-cl-combine]: https://github.blog/changelog/2025-07-21-code-scanning-will-stop-combining-multiple-sarif-runs-uploaded-in-the-same-sarif-file/
[gh-actions-bill]: https://docs.github.com/en/billing/concepts/product-billing/github-actions
[ms-errorlog]: https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-options/errors-warnings#errorlog
[cqa-readme]: https://github.com/github/codeql-action/blob/2d7aeadbf01df0b0a833f218ff2eccfb1b5fc291/README.md
[cqa-changelog]: https://github.com/github/codeql-action/blob/2d7aeadbf01df0b0a833f218ff2eccfb1b5fc291/CHANGELOG.md
[cqa-upload-yml]: https://github.com/github/codeql-action/blob/2d7aeadbf01df0b0a833f218ff2eccfb1b5fc291/upload-sarif/action.yml
[cqa-uploadlib]: https://github.com/github/codeql-action/blob/2d7aeadbf01df0b0a833f218ff2eccfb1b5fc291/src/upload-lib.ts
[cq-sel-cs]: https://github.com/github/codeql/blob/18b48aac513b7915adf742aa3fe226963a6ebcb6/misc/suite-helpers/code-scanning-selectors.yml
[cq-sel-ext]: https://github.com/github/codeql/blob/18b48aac513b7915adf742aa3fe226963a6ebcb6/misc/suite-helpers/security-extended-selectors.yml
[cq-sel-frozen]: https://github.com/github/codeql/blob/18b48aac513b7915adf742aa3fe226963a6ebcb6/misc/suite-helpers/security-and-frozen-quality-selectors.yml
[cq-saq]: https://github.com/github/codeql/blob/18b48aac513b7915adf742aa3fe226963a6ebcb6/csharp/ql/src/codeql-suites/csharp-security-and-quality.qls
[cq-suites-dir]: https://github.com/github/codeql/tree/18b48aac513b7915adf742aa3fe226963a6ebcb6/csharp/ql/src/codeql-suites
[cq-cs-changelog]: https://github.com/github/codeql/blob/18b48aac513b7915adf742aa3fe226963a6ebcb6/csharp/ql/lib/CHANGELOG.md
[cq-standalone]: https://github.com/github/codeql/blob/18b48aac513b7915adf742aa3fe226963a6ebcb6/csharp/extractor/Semmle.Extraction.CSharp.Standalone/Extractor.cs
[cq-depmgr]: https://github.com/github/codeql/blob/18b48aac513b7915adf742aa3fe226963a6ebcb6/csharp/extractor/Semmle.Extraction.CSharp.DependencyFetching/DependencyManager.cs
[cq-fileprovider]: https://github.com/github/codeql/blob/18b48aac513b7915adf742aa3fe226963a6ebcb6/csharp/extractor/Semmle.Extraction.CSharp.DependencyFetching/FileProvider.cs
[cq-filepathfilter]: https://github.com/github/codeql/blob/18b48aac513b7915adf742aa3fe226963a6ebcb6/csharp/extractor/Semmle.Extraction.CSharp.DependencyFetching/FilePathFilter.cs
[cq-dotnet]: https://github.com/github/codeql/blob/18b48aac513b7915adf742aa3fe226963a6ebcb6/csharp/extractor/Semmle.Extraction.CSharp.DependencyFetching/DotNet.cs
