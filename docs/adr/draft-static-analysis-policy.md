# ADR draft: Analyze source in every build at `10-all`, in a strict and a relaxed tier, with every security rule at error, ramped through pending lines in `.globalconfig`; CodeQL `security-extended` as a required job

- **Status:** Accepted. Three routes change it later:
  1. **A Status-line amendment** records the CI runner's clean-build time before and after, which
     the switch-on task measures, and a declined package that becomes eligible under the admission
     rule (decision 9), once its own ticket has admitted it.
  2. **A new ADR** is needed to decline another rule (a diagnostic at `none` or `silent` in the
     strict tier), to move the `10-all` pin to a new .NET major, to change the folders that make up
     either tier, to weaken "every security rule at error, with no exceptions", or to make CodeQL
     alerts block a merge.
  3. **The ramp finishing changes nothing here.** The pending list lives only in `.globalconfig`
     (decision 6), so `grep pending .globalconfig` printing nothing is the finished state, and no
     line of this record restates it.
- **Date:** 2026-10-30
- **Deciders:** integrator, on Adam's answers to #589, #590, #591, #592, #593 and #594 (wayfinder
  map #584)
- **Plan ref:** No decision row. Map #584 and its tickets #585 to #594. The parallel grill over
  #589 to #593, answered by Adam on 2026-10-30 with the cross-ticket items X0 to X8, which override
  the questions they name; #594's round, answered "all yes" the same day. Amends no ADR.

## Context

Before this record no analyzer was enabled anywhere. `Directory.Build.props` set `Nullable` and
`TreatWarningsAsErrors`, with NU1901 to NU1904 as the warning exception; there was no
`.globalconfig`, no `dotnet format` and no CodeQL. The repository's rules that matter most to a
gateway holding tokens, "never log a credential" and "no custom cryptography", rested on N-16's
run-time canary sweep, the architecture tests A-01 to A-04 and review.

The .NET 10 SDK ships its analyzers in the box, but enables 174 CA rules by default, 29 of them
visible and none a security rule (#585). The `Security` category holds 94 rules: 70 off by default,
among them CA3001 to CA3012 (the taint rules, XSS among them), the cookie, randomness, revocation
and token-validation rules; the other 24 are on but Hidden. So the security half is something the
build must switch on, and no eligible third-party package adds taint analysis to it (#586).

#588 counted what each candidate setting would report. At the SDK's broadest settings
(`AnalysisMode=All`, security and style at `latest-all`) the build reports 555 findings in shipping
code and 5,831 in tests and tools; the security share is 17, one of them in shipping code. 699
enabled rules, 90 of the 94 security rules among them, are clean today. A cliff to error would
stop every lane; a ramp keeps the build green while each rule is made clean.

Two traps shape the mechanism. Setting `AnalysisLevel` equal to `AnalysisLevelStyle` silently turns
every IDE rule off. A conditional `GlobalAnalyzerConfigFiles` needs a `global_level` above the root
`.globalconfig`'s or it silently loses, while an `.editorconfig` path section beats every global
config (#585, #589).

## Decision

1. **Scope.** This record governs source analysis of the solution's C#. Supply-chain analysis
   (NuGetAudit severity, SBOM, the pin cadence) is roadmap gap MR-7's, and the map ruled it out.
   `spikes/` is outside the solution and outside this record.

2. **Vocabulary.** A **diagnostic id** is an analyzer rule's identifier (CA2007, IDE0160, MA0009),
   never "rule id", which is the request gate's rejection id. An **analysis tier** is a set of
   source files held to one severity profile; there are two, the **strict tier** and the **relaxed
   tier**, never "test tier", which means Hermetic, Deployed or Local, and never "shipping tier",
   since `tools/spactl` ships and is relaxed.

3. **Tier membership is by folder.** The relaxed tier is every file under `tests/`, `fixtures/`,
   `tools/spactl/`, `tools/bench/floor/` and `src/Platform.Testing/`. Everything else the solution
   builds, `src/Gateway` and every other `src/Platform.*`, is the strict tier. A new project under
   one of those folders joins the relaxed tier without an edit. `fixtures/TestIdp` has no third
   tier: a security rule that fires there is handled per site, as anywhere. For the few things an
   `.editorconfig` section cannot express (decision 10's `AdditionalFiles`), `Directory.Build.props`
   computes one MSBuild property, `SpaRelaxedTier`, from the project's path against the same five
   folders, so both mechanisms read one definition.

4. **A security rule is one in an analyzer's `Security` category**: the SDK's 94 at SDK 10.0.401,
   plus the `Security` category of an admitted package (Meziantou's MA0009 and MA0039). It is not
   the prefixes CA2xxx, CA3xxx or CA5xxx, which take in 65 Usage and Reliability rules (CA2007,
   CA2000, CA2234) and leave out MA0009. Every security rule is at error in **both** tiers, with no
   severity exception. A finding is fixed, or suppressed at its site under decision 12; never by a
   relaxation, and never in a `GlobalSuppressions.cs` or any other assembly-level suppression.

5. **The mechanism.**
   - `Directory.Build.props` pins `AnalysisLevel` to `10-all` and sets `AnalysisMode` to `All`, the
     security and style levels to their `10` all-rules values, and `EnforceCodeStyleInBuild` to
     `true`. The pin is `10-`, not `latest-`, so that `rollForward: latestFeature` cannot add rules
     unseen; `AnalysisLevel` and `AnalysisLevelStyle` never take equal values.
   - The strict severities for the whole solution live in one root **`.globalconfig`**, as per-id
     `dotnet_diagnostic.<id>.severity` lines only. No `dotnet_analyzer_diagnostic.category-*` line
     is written anywhere: under `AnalysisMode=All` every rule is already a warning, which
     `TreatWarningsAsErrors` makes an error, so a category line adds nothing and its reach into
     third-party rules sharing a category name is undocumented.
   - The style options live in `.editorconfig`'s `[*.cs]` section (decision 7).
   - The relaxations live in **one `.editorconfig` section**, the file's last section matching
     `*.cs`: `[{tests,fixtures,tools/spactl,tools/bench/floor,src/Platform.Testing}/**.cs]`, one
     `dotnet_diagnostic.<id>.severity = none` line per id, each with a comment giving its reason.
   - The end state is every enabled diagnostic at error. The only per-id lines are pending lines
     (decision 6) and declines (decision 8); the relaxations are the only `.editorconfig` severity
     lines.

6. **The ramp.** A rule reaches error only once every project is clean for it. Until then it is a
   **pending rule**: one `.globalconfig` line at `suggestion`, whose comment names its batch's
   promotion ticket, `# pending #N`. `suggestion` keeps the build green under
   `TreatWarningsAsErrors` and keeps thousands of messages out of every lane's build output. A
   batch has one integrator promotion ticket, blocked by one fix ticket per owner path its rules
   fire in. The promotion pull request deletes the batch's pending lines, and `Build and test`
   passing on it is the gate: a rule at error with a finding left fails the required job. The
   batches, in order:
   - **Day one:** every rule that is already clean goes to error by having no line (235 SDK CA
     rules and 84 IDE rules, then each admitted package's clean rules when its line lands).
   - **Batch 1, security:** CA5394, CA5351, CA5359, CA5399 and MA0009.
   - **Batch 2:** Usage, Maintainability, Naming apart from the relaxed CA1707, and
     Interoperability.
   - **Batch 3:** Globalization, Performance and Design. No Globalization rule is declined: the one
     that fires in shipping code, CA1307, is about ordinal comparison, and this gateway routes and
     gates on exact host strings.
   - **Batch 4:** Reliability apart from the declined CA2007, and the residual Style findings.

   A firing third-party rule rides the batch of its category; MA0049, which fires once on a class
   named like its namespace, is a Design rule and pending in batch 3, not declined: workstream 6's
   rename of `ArchitectureTests` is one of batch 3's fix tickets. Each batch's promotion ticket is
   filed with the first; batches 2 to 4's fix tickets are filed when the batch before is promoted. The ramp's state is the file: this
   record holds the policy and the declines, and no copy of the pending list.

7. **The house style settles the opposing IDE pairs before day one.** Four `.editorconfig` options
   follow the style the code already shows: file-scoped namespaces (113 of 113 `src` files),
   explicit types rather than `var` (shipping code has none), expression bodies at
   `when_on_single_line`, and no requirement to discard an unused value. They remove 2,504 of the
   2,745 IDE findings (IDE0160 445, IDE0008 357, IDE0022 779, IDE0058 923) and must land before any
   IDE rule is promoted, or a later option change makes a promoted rule fire.

8. **The declined rules**, each set solution-wide in `.globalconfig`:

   | Diagnostic id | Severity | Why |
   | --- | --- | --- |
   | CA2007 | `none` | Microsoft's page says to suppress it entirely for application code: an ASP.NET Core host has no `SynchronizationContext`, so `ConfigureAwait` changes nothing. Every `Platform.*` library runs inside the gateway or `tools/spactl`, a console app with none either. `src/` has no `ConfigureAwait` call, and the decline leaves about 1,600 awaits unedited. |
   | MA0004, VSTHRD111 | `none` | The same awaits as CA2007 (125, 125 and 124 in shipping code), declined for the same reason. |
   | IDE0055 | `none` | Whitespace formatting, which `.editorconfig` sets and the map ruled out of enforcement; `latest-all` style would otherwise enforce it in the build. |
   | IDE0022, IDE0058 | `silent` | Decision 7: expression bodies are mixed (126 to 199) and only churn satisfies IDE0022; IDE0058's `_ =` discards add noise and harden nothing. |
   | MA0137 | `none` | VSTHRD200 is the one Async-suffix rule; it matches `Platform.Abstractions`' existing convention and costs 71 renames. |
   | MA0240 | `none` | `BannedSymbols.*.txt` is the one banned-list mechanism (decision 10); two formats would split one policy across two files. |

   Microsoft.VisualStudio.Threading.Analyzers' JoinableTaskFactory rules stay at their defaults:
   nothing here uses JoinableTaskFactory, so they cannot fire.

9. **Third-party analyzers.** A package is eligible only under MIT, Apache-2.0 or BSD, with a
   stable release against a current SDK within the last 18 months; anything else is declined unless
   a ticket argues it in. Each admitted package enters at its own DLL defaults, with per-id lines in
   `.globalconfig` for its overrides, and no package-wide mode such as `MeziantouAnalysisMode`.
   This departs from "the broadest mode" for packages: 60 off-by-default Meziantou rules stay off,
   because the forced all-warnings mode is where VSTHRD111's 4,010 and RCS1046's 1,010 findings,
   almost all duplicates, come from. A rule turned on by id later is a two-way edit.

   | Package | Version | Licence | Decision |
   | --- | --- | --- | --- |
   | Meziantou.Analyzer | 3.0.317 | MIT | **In.** String comparison and culture beyond CA1307, ReDoS timeouts (MA0009), certificate validation (MA0039), file and type layout. |
   | Microsoft.VisualStudio.Threading.Analyzers | 18.12.1 | MIT | **In.** The only package finding sync-over-async in shipping code (VSTHRD002, VSTHRD003), where CA1849 finds none. |
   | Microsoft.CodeAnalysis.BannedApiAnalyzers | 5.6.0 | MIT | **In**, because decision 10's list is non-empty. |
   | Roslynator.Analyzers | — | Apache-2.0 | Declined. 2 findings at defaults; maximising it would take 220 per-id lines, and its large rules duplicate or oppose IDE rules (RCS1046 against VSTHRD200, RCS1002 against IDE0011). |
   | AsyncFixer | — | Apache-2.0 | Declined. Rules 02 and 03 overlap VSTHRD103 and VSTHRD100, its unique rules are clean, and rule 01 pushes eliding async, which costs stack traces; it fails the 18-month rule after 2027-06-29. |
   | SecurityCodeScan | — | LGPL | Declined on licence; last release 2022. |
   | Puma Scan | — | MPL-2.0 | Declined on licence. |
   | SonarAnalyzer | — | Sonar Source-Available License since 10.3.0 | Declined on licence. |
   | IDisposableAnalyzers, ErrorProne.NET | — | — | Declined on maintenance; ErrorProne.NET has no stable release and no package licence. |

   Microsoft.AspNetCore.Analyzers ships in the Web SDK, and xunit.analyzers reaches every `*Tests`
   project through xunit.v3, so neither is a package line. Each admitted package is a `PackageVersion`
   in a `Label="Analyzers"` group in `Directory.Packages.props` and one unconditional
   `PackageReference` with `PrivateAssets="all"` in `Directory.Build.props`, not a
   `GlobalPackageReference`, which the pins sweep cannot see; `tools/pins_inventory.py` gains an
   `analyzers` family that upgrades with a whole-solution trial, because an upgrade can bring a
   rule that errors by default. Each has a `docs/dependencies.md` row giving its licence and the
   date of the release that made it eligible.

10. **Banned APIs.** BannedApiAnalyzers bans symbols in the strict tier only, from four root files
    attached as `AdditionalFiles` where `SpaRelaxedTier` is false, and RS0030 stays at its default
    warning, an error under `TreatWarningsAsErrors`:
    - `BannedSymbols.Shipping.txt`, every strict-tier project: `System.Console`,
      `System.Diagnostics.Debug` and `System.Diagnostics.Trace`, with the message "write through
      ILogger, so LogFloors, Redaction and N-16 see it"; and the cryptographic primitives that
      "custom cryptography" means, by type: `Aes`, `AesGcm`, `AesCcm`, `ChaCha20Poly1305`, `HMAC`
      and its subclasses, `RSA`, `ECDsa`, `ECDiffieHellman`, `DSA`, `Rfc2898DeriveBytes`,
      `ICryptoTransform`, `CryptoStream`, `SymmetricAlgorithm` and `AsymmetricAlgorithm`. `SHA256`'s
      `HashData`, `RandomNumberGenerator`, `CryptographicOperations` and Data Protection stay
      allowed; the file format cannot allow a type back inside a banned namespace, so the namespace
      is not banned. MD5, SHA1 and DES are left to CA5350 and CA5351, and `System.Random` to
      CA5394, rather than banned twice.
    - `BannedSymbols.NotIdentity.txt`, every strict-tier project except `Platform.Identity`:
      `N:Duende.AccessTokenManagement` and `N:Duende.IdentityModel`. A-04 has two halves: its
      architecture test is the reference half, and checks direct references by design; this ban is
      the use half, closing the transitive reach `Gateway` has through its reference to Identity.
      No new A- id.
    - `BannedSymbols.NoToken.txt`, in `Platform.Registry`, `Platform.Hosting`, `Platform.Security`,
      `Platform.Telemetry` and `Gateway`: `T:Platform.Abstractions.AccessTokenResult.Token`, so a
      token cannot reach a project that has no use for one.
    - `BannedSymbols.NotProxy.txt`, every strict-tier project except `Platform.Proxy`: the
      `AuthenticationHeaderValue(string, string)` constructor, the mechanical part of "no
      hand-written OAuth protocol code".

    The list is clean on day one and needs no ramp. The relaxed tier carries no banned file: its
    code never ships, and `fixtures/TestIdp` and `tests/e2e` both need the primitives.

    **A banned list catches a forbidden call shape at build time; it cannot prove that no token is
    logged, so N-16 remains the proof.** It bans a symbol at a call site and follows no data flow,
    so it cannot see a token in a log argument, an implicit `ToString()` inside an interpolation, or
    an `object?` parameter. The never-log rule's analyzer half is limited to the sinks that bypass
    the logger and the token type where it does not belong; it does not attempt to cover which
    arguments a log call receives. No check is retired: N-16, A-01 to A-04 and the rule-id literal
    scan stay as they are. The record's own generated `ToString` printing its token is outside
    any analyzer's reach and is change request #600.

11. **CodeQL.** An advanced-setup job named `CodeQL` in `.github/workflows/ci.yml`, with
    `needs: documents` and the Build job's own `if`, so a pull request with no C# skips it and does
    not stick; job-level permissions `{ contents: read, security-events: write }`; analysis category
    `/language:csharp`; suite `security-extended`; build mode `manual`, running CI's own
    `dotnet build SpaPlatform.slnx -c Release` after `setup-dotnet` from `global.json`, so the scan
    covers what the solution builds, sees generator output and keeps `spikes/` out, with test
    projects left in under CodeQL's `Test` label. It runs on pull requests and pushes to main, and a
    weekly rescan runs in its own schedule-only workflow under the same category. It scans C# only.
    - `CodeQL` is a required check on main by its job name. The generated `Code scanning results /
      CodeQL` check is never required: it stays pending on a pull request with no upload.
    - Required means the analysis ran. Alerts stay advisory until a step that fails the job on a
      new `high` or `critical` result in shipping code has been proven on one run; making them
      block is a new ADR (Status, route 2).
    - Default setup, on since 2026-10-30 12:49Z, goes off once the job lands: it blocks workflow
      uploads, locks C# to `build-mode: none`, cannot exclude `spikes/` and runs outside the
      classifier's `if`.
    - The Security tab is the alert record. Each open alert in shipping code is filed as one issue
      to its path's owner, labelled `needs-triage`; a relaxed-tier alert is read there and dismissed
      with a reason.
    - Roslyn diagnostics are not uploaded as SARIF: a green build under `TreatWarningsAsErrors`
      logs only notes, which can fail nothing.

    If the `manual` scan's first measured run is well over about six minutes, the required-check
    and build-mode answers are asked again; `none` with `paths-ignore: [spikes/**]` is the fallback.

12. **Suppressions.** A suppression is per site. A `[SuppressMessage]` carries a `Justification`
    citing an evidence id (`S-`, `R-`), a test id (`F-`, `N-`, `P-`, `A-`), an issue (`#N`) or an
    ADR; a `#pragma warning disable` carries the same citation in a trailing comment. The local
    check fails a suppression with no such citation and any `GlobalSuppressions.cs`; review judges
    whether the citation justifies the suppression, and an unjustified one is a Standards-axis
    finding. A rule at `none` or `silent` in the strict tier needs a line in an ADR (decision 8 here,
    a new ADR after it).

13. **The relaxations**, at `none` in the relaxed tier only, each permanent and true of test code by
    its nature; a rule merely not yet clean in the tier rides the ramp instead, and an entry whose
    reason is "too many to fix" is refused:

    | Diagnostic id | Why it holds for test and tool code |
    | --- | --- |
    | CA1707 | Test names use underscores as sentences; xunit has no suppressor for it. |
    | CA1062 | xUnit supplies public test methods' arguments. |
    | CA2234, CA1054, CA1056 | Tests address relative paths as strings. |
    | CA1515 | Types that xUnit or a harness reaches must be public. xunit's CA1515 suppressor runs only where xunit.analyzers does: `src/Platform.Testing` references `xunit.v3.extensibility.core` and `xunit.v3.assert`, which do not bring it, and neither do `Gateway.TestHarness`, `fixtures/TestIdp` or `tools/spactl`. So the suppressor never runs there, and the id is listed for the tier by folder. |

    CA2000, CA1849, CA1861 and CA1031 stay off the list and ride the ramp, as does every IDE rule.
    The local check fails the section if it names a security rule, uses a category line, has an
    entry without a reason comment, or is not the last `.editorconfig` section matching `*.cs`. Any
    admitted package's rule would join the same section under the same test. The security findings
    in the tier are batch 1's: CA5399's 13 fixed by setting `CheckCertificateRevocationList`,
    CA5351 suppressed at `FakeS3Server` (S3's ETag is MD5), CA5359 suppressed at `RawClient.To`.

14. **Analyzers run in every build**, `dotnet build`, `tools/check.sh` and CI alike, not in CI
    alone. There is no build-time budget. Measured locally on SDK 10.0.401 (#588): a clean build
    took 3.8 s with no analyzers, 8.4 s with the SDK tier and 8 to 10 s with the packages (14, 27
    and 35 s with no build server); a no-op build and a one-file gateway edit stayed at 1.3 to
    1.8 s. The CI runner's number arrives by a Status-line amendment from the switch-on task.

15. **No test id.** "The build is analyzer-clean" is not a test id and not a roadmap row: under
    `TreatWarningsAsErrors` a promoted rule with a finding fails the required `Build and test`
    job, so the build is the proof.

16. **The day-one order.** The style options land first, then `.globalconfig` with every firing id
    as a pending line, then the package lines. Meziantou's 17 firing rules and Threading's 5 break
    the build on the day they land otherwise. The banned list is clean on day one.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Analyzers in CI only | A lane learns of a finding after its push, and the measured cost of running them everywhere is a few seconds on a clean build and nothing on an incremental one. |
| A suppression baseline file | Charting refused it: a baseline is permanent debt under another name. The ramp's pending lines name a ticket and are deleted. |
| A second, conditional `.globalconfig` for the relaxed tier | Below a `global_level` above the root's it silently loses, and at an equal level it silently unsets a rule while the build stays green. |
| `AnalysisMode<Category>` properties per tier | Category properties cannot express per-id exceptions, which the relaxation list needs. |
| `dotnet_analyzer_diagnostic.category-*` severity lines | Redundant under `AnalysisMode=All`, ignored for rules it enables, and of unmeasured reach into third-party categories sharing the name. |
| Tiers by project name (`SpaTestProject` and named exceptions) | Leaves `Gateway.TestHarness` strict, with 178 findings, and needs a props edit for each new test project; the counts were measured by folder. |
| "Security rule" as the CA2xxx, CA3xxx and CA5xxx prefixes | Takes in CA2007 and CA2000, 1,724 findings at error with no ramp, and leaves out MA0009 and MA0039. |
| `latest-all` rather than `10-all` | `rollForward: latestFeature` could add rules unseen. |
| CodeQL default setup | Locks C# to `build-mode: none`, cannot exclude `spikes/`, runs outside the classifier's `if` and blocks workflow uploads. |
| The `security-and-quality` suite | Its quality queries were frozen in June 2025 and overlap the Roslyn tier the build already enforces. |
| Requiring `Code scanning results / CodeQL` | It stays pending on a pull request with no upload, so a required one sticks. |
| Uploading Roslyn SARIF | A green build logs only notes; the upload needs a 29-log merge step for nothing that can fail. |
| Meziantou's all-warnings mode | Thousands of duplicate findings (VSTHRD111 4,010 and RCS1046 1,010 exist only in forced modes) for rules turned on unseen. |
| Roslynator and AsyncFixer | Decision 9. |
| Meziantou's `BannedSyntaxes.txt` (MA0240) | A second banned-list format for one policy. |
| Retiring N-16, or A-04's architecture test, as covered by the analyzer | A symbol ban expresses neither a run-time value search nor a package-reference rule, and a retired test id cannot be reissued. |
| A ConfigureAwait rule at error | Decision 8: about 1,600 edits that change no behaviour in a host with no `SynchronizationContext`. |

## Consequences

- The integrator tasks this record authorises, one spec with one ticket per landing step, each
  green on its own and each blocked by the one before:
  1. **Land this record** at the next free number, with the glossary's analysis terms and the
     suppression paragraph in `CODING_STANDARDS.md`.
  2. **The style options** in `.editorconfig`.
  3. **The switch-on task:** the analysis properties and `SpaRelaxedTier` in `Directory.Build.props`;
     the root `.globalconfig` with its pending lines and declines; the relaxation section; the
     local check's guards for the section and for suppressions; the CI runner's clean-build time before and after; and
     `AGENTS.md` → "Build and test", which says analyzers run in every build and how to read a
     diagnostic id.
  4. **The package lines:** `Directory.Packages.props`, the references in `Directory.Build.props`,
     the pins inventory's `analyzers` family, the `docs/dependencies.md` rows, pending lines for
     the firing Meziantou and Threading rules, and a probe of one per-id line against a package's
     `global_level = -100` file, the first point a package is in the build.
  5. **The banned lists:** the four `BannedSymbols.*.txt` files and their `AdditionalFiles`.
  6. **The `CodeQL` job** in `ci.yml` and the weekly rescan workflow.
  7. **A ready-for-human ticket** for Adam to switch default setup off and add `CodeQL` to main's
     required checks.

  Batch 1's promotion ticket and its per-owner fix tickets follow: the CA5xxx fixes are blocked by
  the switch-on task and the MA0009 fixes by the package lines.
- Each steering edit lands with the task that makes it true; `AGENTS.md` says analyzers run in
  every build only from the switch-on task.
- About 1,600 awaits are never given `ConfigureAwait`.
- What is pending is read from `.globalconfig`, not from this record or the roadmap.
- A package upgrade goes through the pins sweep's whole-solution trial, because a new
  default-error rule would break the build.
- The two Python alerts default setup raised in `tools/scripts/cell_doubles.py` go stale when it
  goes off; they are filed as #601. Whether to scan Python or actions again is MR-7's or a map of
  its own.

## Evidence

- #585, `research/dotnet-sdk-analyzer-defaults`: what the SDK enables by default, the security
  category's 94 rules, the severity mechanics and both traps.
- #586, `research/roslyn-analyzer-candidates`: the third-party candidates' licences and maintenance.
- #587, `research/codeql-csharp`: setup mode, build mode, suites, SARIF and the required-check
  pattern.
- #588, `research/analyzer-diagnostic-count`: the counts per diagnostic id and project, the zero
  list, the build times.
- #589 to #593: the grills and their facts; #594: this record's round.
