# What `tests/e2e/Stack` already gives a sitting browser, and what a long-lived driver needs beyond it

- **Ticket:** #1022, "What tests/e2e/Stack already gives a sitting browser, and what a long-lived
  driver needs beyond it", on the map #1020 (Wayfinder: the sitting browser, the browser binding
  driven by wiz).
- **Date:** 2026-11-05 (UTC).
- **Branch:** `research/sitting-browser`, from origin/main at `c084038` (2026-11-04). Never merged.
- **Method:** the code was read in a worktree of origin/main at `c084038` with `cat`, `sed` and
  `grep`; nothing was built, no test ran, and no AWS call was made, because the cell is absent (D10 of
  the sitting of 2026-11-04 tore it down). The tracker was read with `gh issue view` (#1022, #1020,
  #358 and the D posts on #348 of 2026-11-04). Question 4's Playwright facts and question 3's and 5's
  library facts were read from Playwright's official .NET documentation and from xUnit's documentation
  and source, each fetched on 2026-11-05 and listed in the closing section. The Chrome extension
  connected to this account was not driven. No password, cookie, session name, account id or ARN
  appears here; the provider's host is named only as the outputs field it comes from.

## Answer in brief

1. **What exists.** `DeployedStack.StartAsync()` checks the tunnel's port, launches one headless
   Chromium with every app host mapped to the tunnel, and `OpenAsync()` gives a `Profile`: one context
   (its own cookie jar) and one page. `Profile.SignInAsync(app)` navigates to `/bff/login`, has the
   stack fill Cognito's managed-login form (`DeployedStack.FillLoginFormIfShownAsync`, username then
   password, or nothing when Cognito still knows the browser), then waits for the page to be the
   app's `/` and for `#session-state` to read `signed in`. `Profile.GotoAsync` is a navigation whose
   failure prints no query; `#marker` and `#session-state` are read by Playwright locators in
   `ShellTests` and in `Profile.WaitSignedInAsync`. A page `fetch` with the session's cookies is
   `Profile.FetchAsync(url, ...)`, `credentials: same-origin`, returning status, type, headers and
   body; a raw request with the cookies is `ContinuityDriver.CookiesAsync` (the context's cookies
   joined into one `Cookie` header, in a `SessionCookies` that prints as nothing) sent through
   `DeployedStack.SendRawAsync`. The endpoint is `/bff/session`: nothing in `src/` or `contracts/`
   serves `/bff/user`, which exists only in the wiz skill's observables line and in `spikes/g0`.
2. **Configuration.** `SPA_E2E_STACK=deployed` (the switch; any other non-empty value but `local` is
   refused), `SPA_E2E_OUTPUTS` (else `infra/envs/poc/outputs.json`, which `seed.sh` and `deploy.sh`
   write), `SPA_TUNNEL_PORT` (else 8443, the same default `tunnel.sh` reads), `SPA_E2E_PLATFORM_CONFIG`
   (read only by the release switch and the onboarding), `SPACTL` (else `dotnet run --project
   tools/spactl`; only the release switch and the onboarding run it), `SPA_E2E_EVIDENCE` (the
   continuity rows only). Also needed and not a variable: the tunnel up, the `certs.sh` authority
   trusted by the workstation, the AWS CLI v2 signed in as the project's profile (`AWS_PROFILE` and
   `AWS_REGION` from `tools/aws-project.env`), and the repository root above the binaries, which
   `FixtureFiles.RepositoryRoot` finds by walking up to `SpaPlatform.slnx`. The wiz prelude sets
   `AWS_PROFILE` and `AWS_REGION` in every call and defines `spactl` as a function so `SPACTL` stays
   unset; `SPA_E2E_STACK` and `SPA_E2E_PLATFORM_CONFIG` are exported only in the C2, C3 and row
   blocks, and `SPA_E2E_EVIDENCE` only in the row block. The prelude never sets `SPA_E2E_OUTPUTS` or
   `SPA_TUNNEL_PORT`; the defaults carry.
3. **The password's path.** `Cell` holds the parameter's name only; `CellAws.PasswordAsync` runs
   `aws ssm get-parameter --with-decryption` through `ProcessCommandRunner` (output to a string,
   nowhere else) and wraps the value in `Password`, whose `ToString()` is `[password]` and whose only
   reader is `Reveal()`; `DeployedStack` caches it per user in a dictionary and calls `Reveal()` once,
   into `secret.FillAsync`. `Redacted.PlaywrightAsync` scrubs only URLs (query and fragment) out of a
   Playwright or timeout failure's message, since a login's call log carries `code` and `state`; it
   has no rule for a typed value. Nothing in `tests/e2e` starts `Tracing`, takes a screenshot,
   records video or subscribes `Page.Console`; `BrowserExchanges` records only hosts under the app
   domain, so the provider's form post is not in its record. The leak surfaces outside the code are
   Playwright's opt-in ones: `DEBUG=pw:api` verbose logging, `PWDEBUG`, a trace (which records every
   action with its locator, DOM snapshots and console), and a `Page.Console` handler; and the wiz
   prelude's own record, to which `shown` writes a command's whole output unredacted, with the
   redactor's eight rules covering account ids, ARNs, tokens, cookies and client ids but no
   password-shaped string. Whether a failed `FillAsync` message or `pw:api` output carries the value
   typed is not stated on the pages read, and was not executed.
4. **Clearing one domain's cookies.** Yes: `IBrowserContext.ClearCookiesAsync(new() { Domain = ... })`
   (also `DomainRegex`, `Name`, `Path`, since Playwright 1.43; the pinned version is 1.63.0) removes
   the matching cookies and nothing else; the page stays open, since only `CloseAsync` closes a
   context's pages. The provider's cookies sit on `Cell.ProviderHost` (the outputs' `cognitoDomain`),
   the app's on hosts under `Cell.Domain`. Whether Cognito's cookies carry a leading-dot domain, and so
   what string the filter needs, is not in the code. Across a loop: a context from `NewContextAsync`
   is non-persistent and the documentation states no lifetime for it; `ReloadAsync` defaults to a
   30-second timeout and `load`. The suite's only multi-minute browser loop is row 6's
   `RollingDeployAsync` (a five-second cadence under a bound, several profiles), and it has not run on
   a cell: the D6 post of 2026-11-05 says "Not run". The evidence for a long-lived context is the
   design, not a run.
5. **Process lifecycle.** The suite installs Chromium once per process (`Microsoft.Playwright.Program.Main(["install","chromium"])`),
   keeps one static `IPlaywright` it never disposes, and launches one browser per stack; a stack lives
   inside one `[Fact]` (`await using IBrowserStack stack = await Stacks.StartAsync()`), one deployed
   stack at a time under a semaphore with a 12-second quiet after the last, and `DisposeAsync` closes
   the browser. A process that outlives a Bash call needs what `dotnet test` cannot give: an entry
   point that is not the test host (a `*Tests` project is made an `Exe` on xUnit v3 and Microsoft
   Testing Platform by `Directory.Build.props`, so `dotnet run --project tests/e2e` runs the test
   runner), a command channel across calls (the ticket names stdin, a Unix socket or a `dotnet run`
   project; none exists in the repository), its own `CancellationToken` where the stack reads
   `TestContext.Current.CancellationToken` (13 sites in `DeployedStack`; outside a test xUnit's
   `TestContext.Current` is an idle context in stage `Unknown` whose token never cancels), and a
   replacement for the xUnit `Assert` calls in `Profile.OnOriginOfAsync`, `LocalStack` and
   `ContinuityDriver`. The types it would reuse are public (`DeployedStack`, `Profile`, `Chromium`,
   `Cell`, `CellAws`, `Password`), except `Redacted` (internal, no `InternalsVisibleTo`),
   `DeployedStackSetup` and `Stacks.Instead` (internal) and `FillLoginFormIfShownAsync` (private).
6. **Ownership.** Every file in `tests/e2e/Stack`, and `tests/Gateway.TestHarness` (`HarnessApp`,
   `FixtureFiles`), is ws6's by `OWNERSHIP.md`; `tools/scripts/tunnel.sh` is ws1's; a new `tools/<x>`
   directory is the integrator's by the `*` rule, as `SpaPlatform.slnx` is (so is adding a project to
   the solution). A-03 as enforced binds only the six `Platform.*` projects
   (`ReferenceRules.WorkstreamProjects`): a `tools/` project referencing `tests/e2e` would break no
   architecture test. What it would do is pull `Gateway.TestHarness` and, through it, `Gateway`,
   `Platform.Testing`, `TestIdp` and `MockApi` into its closure, since `Gateway.E2ETests` references
   the harness and the harness references those five. The precedent of a `tools/` project
   referencing another owner's code is `tools/spactl` (ws2) referencing `Platform.Registry` (ws2),
   one owner.

## 1. What `DeployedStack`, `Profile`, `Chromium` and `Redacted` expose

**Opening a context.** `DeployedStack.StartAsync()` reads the outputs document, refuses if the file
is missing (`tests/e2e/Stack/DeployedStack.cs:142-156`), then `StartAsync(DeployedStackSetup)` checks
that something listens on the tunnel's port (`:168`, `:458-471`), takes the one-at-a-time semaphore
and waits out the quiet (`:170-174`), starts the foreign listener and launches Chromium with
`--host-resolver-rules` mapping `*.{Domain}` to `127.0.0.1:{tunnelPort}` plus the sibling rule, and a
trust switch for the listener's key (`:176-183`; `Cell.ResolverRule`, `tests/e2e/Stack/Cell.cs:81`).
`Chromium.LaunchAsync` runs the package's browser installer once per process, creates one static
`IPlaywright`, and launches headless with `--no-proxy-server` before the caller's switches
(`tests/e2e/Stack/Chromium.cs:17-38`). `OpenAsync()` makes a context with `IgnoreHTTPSErrors = false`
(the cell's certificate must chain to the `certs.sh` authority), one page, attaches the exchange
recorder, and returns `new Profile(this, context, page)` (`DeployedStack.cs:211-220`). `Profile`
exposes `Context`, `Page` and `Stack` (`tests/e2e/Stack/Profile.cs:17-19`, `:126`) and disposes the
context (`:128`).

**`/bff/login` with the managed-login fill.** `Profile.SignInAsync(app)` is three steps: `GotoAsync`
of `app.Url("/bff/login")`, `Stack.CompleteLoginAsync` under `Redacted.PlaywrightAsync`, then
`WaitSignedInAsync` (`Profile.cs:26-31`). On the deployed stack `CompleteLoginAsync` calls
`FillLoginFormIfShownAsync(profile, url => url == shell)` (`DeployedStack.cs:232-236`), which waits
until the URL is the shell or its host is `ProviderHost`, returns if no form was shown (Cognito still
holds a session for this browser, `:246-250`), fetches and caches the password per user (`:252-256`),
locates `input[name='username']:visible`, `input[name='password']:visible` and
`button[type='submit']:visible` (`:260-262`), fills the user name, clicks submit if the password field
is not yet visible and waits for it (`:264-269`), then fills the password and submits (`:272-273`).
The user is `alice` unless `SignInAs` names another (`:48`, `:291`). The same method serves
`StopAtCallbackAsync`, held in the browser by `NavigationHold` at `/signin-oidc` (`:317-325`), and the
form is also what `StopAtProviderLoginAsync` waits to see after a logout (`:345-353`).

**Waiting for the shell's signed-in state.** `Profile.WaitSignedInAsync(app)` is
`Page.WaitForURLAsync(app.Url("/"))` then `Expect(Page.Locator("#session-state")).ToHaveTextAsync("signed in")`
(`Profile.cs:33-38`, the constant at `:15`). The fixture shell sets that text after `/bff/session`
answers 200 (`fixtures/spa/app1/assets/app.43eda712.js:32`, `:52`); on a 401 it starts a login
(`:34-37`) and on any other failure shows the error message (`:57`).

**Navigating to `/` and reading `#marker` and `#session-state`.** `Profile.GotoAsync(url)` is
`Page.GotoAsync` under the URL scrub (`Profile.cs:45`); `OnOriginOfAsync` goes to `/healthz` instead
when a page on the origin is wanted without a login (`:51-55`). There is no `Profile` method for the
marker: `ShellTests` reads it with `Expect(profile.Page.Locator("#marker")).ToHaveTextAsync("Fixture SPA app1")`
and `Locator("#marker").TextContentAsync()` (`tests/e2e/Functional/ShellTests.cs:72`, `:130`), on
the `<h1 id="marker">` of each fixture (`fixtures/spa/app1/index.html:13`), and `#session-state` the
same way (`ShellTests.cs:48`, `:98`; `index.html:16`).

**A fetch with the session's cookies.** From the page: `Profile.FetchAsync(url, method, headers, body,
credentials = "same-origin")` evaluates a `fetch(..., { redirect: "manual" })` in the page and returns
`FetchResult(Status, Type, ContentType, Location, Body, Error)` with a `Code` reader for a problem
body (`Profile.cs:71-94`, `:132-151`); `ContinuityDriver.AntiforgeryAsync` uses it on `/bff/session`
and `/api/orders/echo` (`tests/e2e/Stack/ContinuityDriver.cs:127-138`). Raw: `ContinuityDriver.CookiesAsync`
joins `profile.CookiesOfAsync(app)` into one header value inside `SessionCookies`, which prints as
nothing (`ContinuityDriver.cs:70-71`, summary at `:14-15`), and `SendAsync` passes it to the traced
raw client over `DeployedStack.SendRawAsync`, a `RawClient` to `127.0.0.1:{tunnelPort}` that carries
exactly the headers given, no cookie jar, no redirects (`ContinuityDriver.cs:50-52`;
`DeployedStack.cs:303-310`; `tests/e2e/Stack/RawClient.cs:8-14`, `:27-31`). The cookies the context
holds for an origin are `Profile.CookiesOfAsync` and `HoldsAsync` (`Profile.cs:113-119`).

**The endpoint's name.** The gateway's BFF endpoints in `contracts/*.md`, `src/Gateway` and
`src/Platform.Identity` are `/bff/login`, `/bff/session` and `/bff/logout` (a `grep -o` over those
files counts 17, 12 and 12 mentions and no other). `/bff/user` occurs in the repository only at
`.claude/skills/wiz/SKILL.md:417` ("ending an app's sessions: `/bff/user` answering 200 before") and
in `spikes/g0/real-handler/*` (`RealOidc/Gateway.cs:107` maps it). The observable the skill names
is therefore served today as `/bff/session`.

**What `Redacted` exposes.** `Url(Uri)`, `Url(string)` and `Urls(...)` print scheme, authority and
path only (`tests/e2e/Stack/Redacted.cs:16-22`); `PlaywrightAsync(Func<Task>)` and its generic form
rethrow a `PlaywrightException` or `TimeoutException` with every absolute URL in the message scrubbed,
the stack trace kept and the inner exception dropped (`:36-66`). The class is `internal static partial`
(`:14`).

## 2. The configuration they need

| Variable | Read at | Default or refusal | The file or thing behind it | Set by the wiz prelude? |
| --- | --- | --- | --- | --- |
| `SPA_E2E_STACK` | `StackSwitch.Choose`, `tests/e2e/Stack/StackSwitch.cs:22-30` | unset, empty or `local` is local; `deployed` is the cell; anything else throws | — | Not in the prelude (`SKILL.md:62-73`); exported in the C2, C3 and continuity-row blocks (`SKILL.md:214`, `:254`, `:718`) |
| `SPA_E2E_OUTPUTS` | `DeployedStack.StartAsync`, `DeployedStack.cs:38-39`, `:145-152` | `infra/envs/poc/outputs.json`; a missing file throws naming `seed.sh` and `deploy.sh` | the infrastructure outputs document (`contracts/infra-outputs.schema.json`), whose `region`, `bucket`, `logGroup`, `cognitoDomain`, `ecsCluster`, `ecsService`, `clients` and `testUsers` `Cell` requires (`Cell.cs:20-31`) | No; the default carries |
| `SPA_TUNNEL_PORT` | `DeployedStack.TunnelPort`, `DeployedStack.cs:41-42`, `:450-455` | 8443; a non-port throws | the same variable `tools/scripts/tunnel.sh:52` reads (`LOCAL_PORT="${SPA_TUNNEL_PORT:-8443}"`), and the port in every registry host (`fixtures/apps/app1.json:3`, `app1.spa-poc.test:8443`) | No; the default carries |
| `SPA_E2E_PLATFORM_CONFIG` | `DeployedStack.PlatformConfig`, `DeployedStack.cs:44-45`, `:427-431` | throws only when a release switch or an onboarding runs | the platform configuration rebuilt from the task definition by `tools/scripts/cell_documents.py config` (`SKILL.md:204-208`) | Not in the prelude; exported in the same three blocks as the switch |
| `SPACTL` | `DeployedStack.Spactl`, `DeployedStack.cs:433-437` | `dotnet run --project tools/spactl --` | an executable, when named; used by `PublishReleaseAsync`, `SwitchReleaseAsync` and the onboarding only | The prelude defines `spactl` as a shell function "so `SPACTL` stays unset (Q80)" (`SKILL.md:73`); C3 runs `unset SPACTL` (`:253`) |
| `SPA_E2E_EVIDENCE` | `ContinuityDriver` constructor, `ContinuityDriver.cs:27-29`; `tests/e2e/Stack/ContinuityEvidence.cs:37` | throws; must be a directory git ignores (`ContinuityEvidence.cs:98`) | `.scratch/sitting/log/<date>/continuity` | Only in the continuity-row block (`SKILL.md:718-719`) |
| `AWS_PROFILE`, `AWS_REGION` | not read by the suite; inherited by every child process `ProcessCommandRunner` starts (`tests/e2e/Stack/CellAws.cs:233-247`, `:256-275`), which also sets `AWS_PAGER=""` | the AWS CLI's own defaults | `tools/aws-project.env` (those two names) | Yes: `set -a && . tools/aws-project.env && set +a` (`SKILL.md:68`) |

Not variables, also required: the tunnel listening (`DeployedStack.cs:458-471`); the ALB's
certificate chaining to the authority `tools/scripts/certs.sh` installed, since the context does not
ignore HTTPS errors (`:176-179`, `:213-214`); the AWS CLI v2 signed in as the project's profile, the
one way the stack reaches AWS (`CellAws.cs:10-17`); the app definitions and fixtures read from the
repository, found by `FixtureFiles.RepositoryRoot` walking up from `AppContext.BaseDirectory` to the
directory holding `SpaPlatform.slnx` (`tests/Gateway.TestHarness/FixtureFiles.cs:17`, `:75-87`;
`Cell.HostOf`, `Cell.cs:109`); and Playwright's Chromium build, installed by the suite itself into the
package's cache (`Chromium.cs:24-28`; `~/Library/Caches/ms-playwright` on macOS, overridable with
`PLAYWRIGHT_BROWSERS_PATH`, per the Playwright browsers page). The stand-in rules in the prelude that
matter to a browser: the shell is `/bin/bash` and keeps no state between calls (`SKILL.md:64-65`), so
every variable above must be re-exported in the call that uses it.

## 3. How the password reaches the page, and where it could leak

**The path.** The outputs document names the parameter, never the value (`Cell.cs:13-14`,
`:104-107`). `CellAws.PasswordAsync` runs `aws ssm get-parameter --name <parameter> --with-decryption
--query Parameter.Value --output text --region <region>` through the runner and returns `new
Password(value)`, or throws if empty (`CellAws.cs:32-39`). `ProcessCommandRunner` redirects stdout and
stderr into strings and "nothing it reads is written anywhere but the returned string"
(`CellAws.cs:239`, `:259-291`); a failed command's exception carries the program, two arguments and
stderr with account ids masked (`:230-231`). `Password` is a sealed class with an internal
constructor, `Reveal()` as the one reader and `ToString()` of `[password]` (`CellAws.cs:183-197`).
`DeployedStack` caches one per user (`DeployedStack.cs:95`, `:252-256`) and calls `Reveal()` once,
into `secret.FillAsync`, under the comment "The one place the password goes. Nothing here traces,
screenshots or prints the page" (`:271-273`).

**`Redacted.PlaywrightAsync`.** It catches `PlaywrightException` and `TimeoutException` only, and
rewrites every absolute URL in the message to scheme, authority and path (`Redacted.cs:36-70`); the
reason given is the call log of URLs a timeout's message carries, with a callback's `code` and
`state` (`:24-30`). It has no rule for anything that is not a URL. A `FillAsync` that fails on a
locator would surface as a `PlaywrightException` or `TimeoutException` through this scrub; whether
Playwright's message for a failed fill names the value typed is not stated on the pages read.

**Playwright's own output.** Nothing in `tests/e2e` calls `Tracing`, takes a screenshot, records video
or a HAR, or subscribes `Page.Console`: a grep for those names finds only the comment at
`DeployedStack.cs:271`. Per the Playwright documentation (trace-viewer, debug, class-consolemessage):
a trace is recorded only after `Context.Tracing.StartAsync` and shows, per action, "what locator was
used", DOM snapshots, network and console; verbose logging is opt-in with `DEBUG=pw:api`; the
Inspector with `PWDEBUG`; and console messages reach the process only through a `Page.Console`
handler. None is on in the suite; each is a surface a driver that turned it on would add.
`BrowserExchanges` records responses and requests for hosts under the app domain only
(`tests/e2e/Stack/BrowserExchanges.cs:5-9`, `:18`, `:28-32`), so the
form post to the provider's host is not in the exchanges record; what it does hold for app hosts is
headers, including cookies the browser sent (`:12-16`).

**The prelude's record.** `shown` writes the command and its whole output to the record file
unredacted and prints it through `tools/wiz_redact.py --stream` (`SKILL.md:69`, `:75-83`); the
redactor's eight rules cover account ids, ARNs, the named query and JSON members (`code`, `state`,
tokens, secrets, a parameter's `Value`), cookie and authorization headers, CSRF headers and Terraform's
client ids (`tools/wiz_redact.py:11-39`), and "catches shapes, not meaning" (`:41`). No rule names a
password-shaped string, so a driver that printed one through `shown` would have it in the record and
on the terminal.

## 4. Clearing one domain's cookies, and a context across a long loop

**Clearing.** `IBrowserContext.ClearCookiesAsync` "Removes cookies from context. Accepts optional
filter", with options `Domain|DomainRegex` ("Only removes cookies with the given domain"),
`Name|NameRegex` and `Path|PathRegex`, each a string or a `Regex`, added in v1.43; the documented
example is `await context.ClearCookiesAsync(new() { Domain = "my-origin.com" });` (Playwright .NET
class-browsercontext, read 2026-11-05). The pinned package is `Microsoft.Playwright` 1.63.0
(`Directory.Packages.props:70`; `docs/dependencies.md:30`, `:69`). The call does not close anything:
"Closes the browser context. All the pages that belong to the browser context will be closed" is
`CloseAsync`'s description, and the `Close` event lists only a context close, a browser close or a
crash. The provider's cookies are on `Cell.ProviderHost`, the outputs' `cognitoDomain` (`Cell.cs:26`,
`:49`), and the app's on hosts under `Cell.Domain` (`:64-71`); `IBrowserStack.ProviderHost` is
documented as "The provider's host, whose own cookies are not the platform's"
(`tests/e2e/Stack/IBrowserStack.cs:34-35`). `CookiesAsync(urls)` returns "only cookies that affect
those URLs", which is how `Profile.CookiesOfAsync` scopes to an app (`Profile.cs:114-115`); the
domain string Cognito's cookies carry (with or without a leading dot) is not in the code, so the
exact `Domain` filter a driver would pass was not verified.

**Across a reload loop.** A context from `Browser.NewContextAsync` is "isolated" and "non-persistent"
and writes nothing to disk; the documentation states no lifetime or idle limit for it.
`Page.ReloadAsync` "reloads the current page, in the same way as if the user had triggered a browser
refresh", with `Timeout` defaulting to 30 seconds and `WaitUntil` to `load`. The suite's own
multi-minute browser work is `ContinuityDriver.RollingDeployAsync`: a loop on a five-second delay
under a bound, opening a fresh profile per sign-in and one "straddling" profile held at the provider's
form until the old tasks stop (`ContinuityDriver.cs:158-205`), and `SignInWithExpiredAccessTokenAsync`,
which holds a signed-in profile for five and a half minutes (`DeployedStack.cs:338-342`). Neither has
run on the cell as a long hold of one page: the D6 post on #348 says "Not run" (2026-11-05,
https://github.com/adammedford/spa-platform/issues/348#issuecomment-6645865498), and D5's login
stopped with a timeout before `/signin-oidc`
(https://github.com/adammedford/spa-platform/issues/348#issuecomment-6645865170). The map #1020
records that rows 3, 8 and 9 signed in through the tunnel unattended on 2026-11-04. So what the code
shows is a design for minutes-long contexts, and what the record shows is logins that succeed, not a
10-second reload loop of several minutes.

**Default timeouts a loop meets.** `BrowserContext.SetDefaultTimeout` "will change the default
maximum time for all the methods accepting timeout option", and page-level settings take priority
over it; `Expect(...).ToHaveTextAsync` and `WaitForURLAsync`, which `WaitSignedInAsync` uses, are
such methods. `NavigationHold.DefaultWait` is 60 seconds for a held request (`NavigationHold.cs:26`).

## 5. How the suite starts and stops the browser, and what a long-lived process needs

**Start.** Per process: `Chromium.LaunchAsync` runs `Microsoft.Playwright.Program.Main(["install",
"chromium"])` once under a semaphore, a no-op when the pinned build is cached, and creates one static
`IPlaywright` that is never disposed (`Chromium.cs:13-36`). Per stack: one `IBrowser` from
`playwright.Chromium.LaunchAsync(new() { Headless = true, Args = [...] })` (`:38`), after the tunnel
check, the one-at-a-time semaphore and the quiet of `2 * 5s + 2s` since the last stack ended
(`DeployedStack.cs:67-79`, `:168-174`, `:204-207`). Per test: every `[Fact]` opens its own stack and
profile with `await using` (`ShellTests.cs:21-23`, `:39-41`; `ContinuityDriver.StartAsync`,
`ContinuityDriver.cs:40-44`).

**Stop.** `Profile.DisposeAsync` closes the context (`Profile.cs:128`); `DeployedStack.DisposeAsync`
disposes the raw client, the browser and the foreign listener, records `lastEnded` and releases the
semaphore (`DeployedStack.cs:473-486`). The browser process ends with the `IBrowser`; the Playwright
driver ends with the test process, since the `IPlaywright` is static.

**What `dotnet test` gives that a long-lived process must supply itself.**

- *An entry point.* `Directory.Build.props:97-107` makes any project whose name ends in `Tests` an
  `Exe` with `xunit.v3` and the `Xunit` using, on Microsoft Testing Platform (`global.json`), so the
  test host owns `Main` in `tests/e2e`; `dotnet run --project tests/e2e` runs the runner, and the wiz
  skill drives rows as `dotnet test --project tests/e2e -- --filter-method ...` (`SKILL.md:720-721`).
  A driver that stays up across Bash calls needs a project of its own, whose name does not end in
  `Tests`, or a different host.
- *A command channel.* The ticket names a stdin loop, a Unix socket or a `dotnet run` project; the
  repository has none of them for the browser. The prelude's shell keeps no state between calls
  (`SKILL.md:64-65`), so the channel is what a later call finds the process by (a socket path, a pid
  file or a named pipe), and nothing in `tests/e2e` or `tools/` provides one.
- *A cancellation token.* The stack reads `TestContext.Current.CancellationToken` at 13 sites in
  `DeployedStack.cs` (`:144`, `:165`, `:254`, `:277`, `:281`, `:285`, `:301`, `:304`, `:341`, `:356`,
  `:372`, `:392`, `:439`), 5 in `LocalStack.cs` and one each in `NavigationHold.cs` and
  `TlsLoadBalancer.cs`. Outside a test, xUnit v3's `TestContext.Current` "will return a test context
  that is in the `TestPipelineStage.Unknown` stage": the idle context is constructed with a `default`
  cancellation token, so its token never cancels (xunit `TestContext.cs`, read 2026-11-05). The calls
  work, but the per-test timeout and Ctrl-C handling that `dotnet test` wires into that token are
  absent; a long-lived process brings its own token and its own stop.
- *Assertions.* `Profile.OnOriginOfAsync` uses xUnit's `Assert.Equal` (`Profile.cs:54`), as do
  `LocalStack` (3 sites), `ContinuityDriver` (2) and `TokenShape` and `WebStorage`; `Assertions.Expect`
  in `WaitSignedInAsync` is Playwright's own, not xUnit's. A non-xUnit host either references
  `xunit.v3.assert` or avoids those members.
- *Visibility.* The reusable surface is public: `DeployedStack` (`StartAsync()`, `OpenAsync`,
  `CompleteLoginAsync`, `StopAtCallbackAsync`, `StopAtProviderLoginAsync`, `SignInAs`, `SendRawAsync`,
  `Cell`, `Aws`), `Profile` and `FetchResult`, `Chromium`, `Cell`, `CellAws`, `Password` (public type,
  internal constructor), `ICommandRunner` and `ProcessCommandRunner`, `RawClient`, `NavigationHold`,
  `BrowserExchanges`. Internal: `Redacted` (`Redacted.cs:14`), `DeployedStackSetup`
  (`DeployedStack.cs:495`), `Stacks.Instead` (`IBrowserStack.cs:155`); private:
  `FillLoginFormIfShownAsync` (`DeployedStack.cs:243`). `Gateway.E2ETests.csproj` grants no
  `InternalsVisibleTo` (the file has none; `Gateway.TestHarness.csproj` grants one, to
  `Gateway.IntegrationTests`).
- *The Playwright driver's lifetime.* Playwright for .NET is used as a library through
  `Playwright.CreateAsync()` in a `using`, with the browser in an `await using` (Playwright .NET
  library page); the suite's static, undisposed `IPlaywright` relies on process exit. A process that
  stays up holds the driver and the browser for as long as it lives, and must close both on its stop.

## 6. Which parts are ws6-owned, and what the integrator could reuse

**By `OWNERSHIP.md`** (last matching line wins, `OWNERSHIP.md:3`): `/tests/e2e/` and
`/tests/Gateway.TestHarness/` are `ws6-verification` (`OWNERSHIP.md:49`, `:53`), so every file in
question 1 and `HarnessApp`, `FixtureFiles` and `Contract` are ws6's; `/tools/scripts/` (`tunnel.sh`,
`certs.sh`, `cell_documents.py`) is `ws1-infra` (`:22`); `/tools/spactl/` is `ws2-hosting` (`:27`);
`/tools/bench/` is ws6 (`:54`); everything else under `tools/`, and `docs/research/`, is the
integrator's by `*` (`:18`), as are `SpaPlatform.slnx` and `Directory.Packages.props` (`AGENTS.md`,
"The integrator's paths are off limits"). The solution lists `tools/bench/floor/Bench.Floor.csproj` and
`tools/spactl/spactl.csproj` under `/tools/` (`SpaPlatform.slnx:35-38`); adding a project means
editing that file.

**A-03 as written and as enforced.** The rule: "A workstream project references
`Platform.Abstractions` and no other workstream's project. Only `Gateway` references them all"
(`docs/plan/04-tests.md:84`; `AGENTS.md:92`). The architecture test binds exactly six projects,
`Platform.Registry`, `Platform.Hosting`, `Platform.Identity`, `Platform.Proxy`, `Platform.Security`
and `Platform.Telemetry` (`tests/Gateway.ArchitectureTests/ReferenceRules.cs:32-43`, run by
`ArchitectureTests.cs:30-40`). A `tools/` project referencing `tests/e2e/Gateway.E2ETests.csproj` or
`tests/Gateway.TestHarness` is outside that set and would break no architecture test; whether the
rule's words reach it is a reading of "workstream project" the code does not make. Precedents of a
`tools/` project referencing another directory's code: `tools/spactl` references
`Platform.Abstractions` and `Platform.Registry` (`tools/spactl/spactl.csproj:10-13`), both ws2's or
the integrator's; `tools/bench/floor` references nothing on purpose (`Bench.Floor.csproj:3-6`).

**What a reference drags in.** `Gateway.E2ETests.csproj` references `Gateway.TestHarness` and the
`Microsoft.Playwright` package with a framework reference to `Microsoft.AspNetCore.App`
(`tests/e2e/Gateway.E2ETests.csproj:10-19`); `Gateway.TestHarness.csproj` references `Gateway`
(aliased `GatewayApp`), `Platform.Abstractions`, `Platform.Testing`, `fixtures/TestIdp` and
`fixtures/MockApi` (`tests/Gateway.TestHarness/Gateway.TestHarness.csproj:5-12`). The pieces that
make the deployed browser work, `Chromium`, `DeployedStack`, `Profile`, `Cell`, `CellAws`, `Redacted`,
`BrowserExchanges`, `NavigationHold`, `RawClient`, depend on the harness for `HarnessApp` (a record
of app id, host, origin, client id, secret, secret ref; `tests/Gateway.TestHarness/GatewayHarness.cs:726-738`),
`FixtureFiles` (the repository root and app definitions) and, in `Redacted`, `RegexTimeout`
(`Redacted.cs:3`, `:69`); `LocalStack` depends on the harness's `GatewayHarness` and on
`Platform.Registry` and `Platform.Testing` (`LocalStack.cs:1-4`). The `Deployed` classes carry their
own `RecordedRunner` doubles and the stand-in cell (`tests/e2e/Deployed/DeployedRunTests.cs:14-21`).

**What is integrator-owned today that touches this.** `.claude/skills/wiz/SKILL.md` (the prelude,
D0, D2 and the observables) and `tools/wiz_redact.py` ("Owner: integrator", `tools/wiz_redact.py:2`)
are the integrator's by `*`; the map #1020 states the same ("the wiz skill is the integrator's;
`tests/e2e` is ws6; `tools/scripts` is ws1").

## What was executed and what was only read

**Executed, 2026-11-05 (UTC):**

- `gh issue edit 1022 --add-assignee @me`; `gh issue view 1022`, `1020`, `358 --comments`,
  `348 --comments` (the comments of 2026-11-03 and later, filtered for the row posts).
- `git -C /Users/adammedford/.worktrees/research-sitting-browser status`, `log -1`, `branch -r`,
  `ls-tree`/`show` of `origin/research/analyzer-diagnostic-count` for the finding convention.
- `cat`, `sed -n`, `grep` over the files cited above, at origin/main `c084038`.
- Nothing else: no `dotnet build`, no `dotnet test`, no `dotnet run`, no AWS call, no browser
  driven, no tunnel.

**Read, with the date:**

- Code at `c084038` (2026-11-05): `tests/e2e/Stack/DeployedStack.cs`, `Profile.cs`, `Chromium.cs`,
  `Redacted.cs`, `CellAws.cs`, `Cell.cs`, `IBrowserStack.cs`, `LocalStack.cs`, `StackSwitch.cs`,
  `ContinuityDriver.cs`, `ContinuityEvidence.cs` (one line), `BrowserExchanges.cs` (header),
  `NavigationHold.cs` (header), `RawClient.cs`; `tests/e2e/Gateway.E2ETests.csproj`;
  `tests/e2e/Functional/ShellTests.cs` (head); `tests/e2e/Deployed/DeployedRunTests.cs` (head);
  `tests/Gateway.TestHarness/Gateway.TestHarness.csproj`, `FixtureFiles.cs`, `GatewayHarness.cs:718-738`;
  `tests/Gateway.ArchitectureTests/ReferenceRules.cs:32-43`, `ArchitectureTests.cs:30-40`;
  `.claude/skills/wiz/SKILL.md:62-100`, `:195-260`, `:370-420`, `:572-660`, `:714-722`;
  `tools/wiz_redact.py:1-60`; `tools/aws-project.env` (names only); `tools/scripts/tunnel.sh:1-65`;
  `tools/spactl/spactl.csproj`; `tools/bench/floor/Bench.Floor.csproj`; `OWNERSHIP.md`;
  `Directory.Build.props`; `Directory.Packages.props:70`; `SpaPlatform.slnx`; `AGENTS.md:92`;
  `docs/plan/04-tests.md:84`; `docs/dependencies.md:30-35`, `:69`; `docs/agents/issue-tracker.md:47`;
  `fixtures/spa/app1/index.html`, `fixtures/spa/app1/assets/app.43eda712.js:30-60`,
  `fixtures/apps/app1.json:3`; `spikes/g0/real-handler/RealOidc/Gateway.cs:107` (grep hit only).
- Playwright for .NET documentation (2026-11-05): https://playwright.dev/dotnet/docs/api/class-browsercontext
  (`ClearCookiesAsync`, `CookiesAsync`, `AddCookiesAsync`, `SetDefaultTimeout`, `CloseAsync`, the
  `Close` event, non-persistent contexts); https://playwright.dev/dotnet/docs/api/class-page
  (`ReloadAsync`, `CloseAsync`; the first 100,000 characters, which did not reach the `Console`
  event); https://playwright.dev/dotnet/docs/api/class-consolemessage;
  https://playwright.dev/dotnet/docs/trace-viewer; https://playwright.dev/dotnet/docs/debug;
  https://playwright.dev/dotnet/docs/browsers; https://playwright.dev/dotnet/docs/library;
  https://playwright.dev/dotnet/docs/api/class-playwright (no prose on the driver process).
- xUnit (2026-11-05): https://xunit.net/docs/getting-started/v3/whats-new (`TestContext`, in-process
  runner, Microsoft Testing Platform) and
  https://raw.githubusercontent.com/xunit/xunit/main/src/xunit.v3.core/TestContext.cs (`Current`,
  the idle context's construction). https://api.xunit.net/xunit.net/v3/Xunit.TestContext.html
  returned 404 and was not read.
- Tracker (2026-11-05): #1022 and #1020 bodies; #358's comments of 2026-10-24 to 2026-10-28; #348's
  D1 to D7 posts of 2026-11-04 and 2026-11-05.
