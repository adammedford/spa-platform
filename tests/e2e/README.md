# The hermetic cell by hand

This page tells a contributor, or a team weighing the gateway, how to watch it work on a
workstation with no AWS: a login, a session and a call proxied to an API, through the gateway's
shipped composition, from a clone, without writing a test. It uses what already runs, the hermetic
cell behind the sitting browser's `start`, and adds nothing to it.

**A run made this way is never evidence.** No gate, test id or review record cites one. The
hermetic suites prove every behaviour this run shows; the run only lets a person see it.

## What it is

The hermetic cell, started in one process:

- the gateway's own composition (`src/Gateway/Program.cs`), on Kestrel;
- the test identity provider (`fixtures/TestIdp`);
- the mock API (`fixtures/MockApi`);
- an echo upstream;
- an in-memory bucket holding the fixture apps' releases (`fixtures/spa`) and a registry built from
  `fixtures/apps`;
- a TLS stand-in for the load balancer;
- a headless Chromium, driven only through the commands below. There is no window to look at and no
  address to open in your own browser: what the browser sees comes back as observation lines.

It needs the .NET 10 SDK and Playwright's Chromium, which the process installs on its first start.
It needs no Docker, no AWS credential, no hosts entry and no certificate in any store.

## Before the start

**A bundle directory.** Every call names one with `--bundle`: a temporary directory that exists
before the start. The process listens on a Unix socket at `browser.sock` in it, and writes
`browser.server.log` there. The path of `browser.sock` must stay under the Unix socket limit: at
most 103 bytes on macOS and 107 on Linux. A longer path is refused as `socket-path-too-long`, so
make the directory under a short one such as `/tmp`, not under a deep checkout or a session's
scratch directory:

```bash
E=$(mktemp -d /tmp/cell.XXXXXX)
```

**The environment.** `SPA_E2E_STACK` unset: it is the switch a sitting sets to reach the deployed
cell, and with it unset the process starts the hermetic cell. `PWDEBUG` unset, and no `pw:` channel
in `DEBUG`, or the start is refused (`refused ... reason=pwdebug`, or `reason=debug-pw`), since
Playwright's debug output would print what the page holds.

## The launch line

```bash
dotnet run --project tests/e2e -- sitting-browser --bundle "$E" start
```

`start` is the resident process: it stays until `stop` or a signal (Ctrl-C, or `kill`), so run it in
a terminal of its own or in the background. Its standard output is two lines and nothing else:

```text
ready         <time> - stack=local
stopped       <time> - reason=stop
```

(`reason=cancelled` when a signal stopped it.)

The code spells the hermetic cell `LocalStack` and its ready line `stack=local`; it is not the Local
(L) tier, which is the startup benchmark of the built image. Everything else the process writes,
the cell's hosts and Playwright among it, goes to `browser.server.log` in the bundle (mode 600),
never to the terminal. The `ready` line comes once the cell has started, which on a first run
includes Chromium's install.

## The commands

Each is a short client call with the same `--bundle`, from another terminal:

```bash
dotnet run --project tests/e2e -- sitting-browser --bundle "$E" <command>
```

A profile is a browser context with its own cookies; it defaults to `main`, and is lowercase letters,
digits and `-`. An app is an app id from `fixtures/apps`, such as `app1` or `app2`. A user is `alice`
or `bob`, `alice` when none is named; there is no password option.

| Command | What it does |
| --- | --- |
| `status` | One line per open profile: its app and its last observation; `profiles=0` when none is open. |
| `open [profile] --app <appId>` | Opens the profile on the app's `/` and prints what the page shows. |
| `login [profile] --app <appId> [--as alice\|bob]` | Logs the user in to the app through the gateway's `/bff/login`, opening the profile if it is not open. |
| `marker [profile]` | The fixture page's marker text. |
| `session [profile]` | Fetches `/bff/session` from the page: its status, never its body. |
| `api [profile] <name>/<path>` | Fetches `/api/<name>/<path>` from the page, GET only: its status, never its body. The path is letters, digits and `. _ ~ -`, with no query. |
| `reload [profile]` | Reloads the page and prints what it shows. |
| `close [profile]` | Closes the profile. |
| `clear-provider [profile]` | Removes the profile's cookies on the identity provider's host, leaving the app's. |
| `watch [profile] --every <N>s [--clear-provider] [--relogin]` | Reloads every N seconds, at least 1, and prints each observation, here and in `browser.log` in the bundle, until `unwatch`. `--clear-provider` clears the provider's cookies before each reload; `--relogin` logs in again when a reload lands on the provider's login. |
| `unwatch [profile]` | Ends the profile's watch. |
| `stop` | Closes every profile and ends the process and its browser. |

A command exits 0 when it made an observation, whatever it says, 1 when it could make none (a
`failed` line, or `not-running` when no process answers at the socket), and 2 on a usage error.

## What the run shows

In order, with `<time>` and `<instance>` standing for the values of your run:

```text
$ ... status
status        <time> - profiles=0
$ ... open main --app app1
signed-in     <time> main app=app1 revision=1 instance=<instance>
$ ... marker
marker        <time> main app=app1 text=Fixture SPA app1
$ ... session
session       <time> main app=app1 http=200 problem=-
$ ... api main orders/whoami
api           <time> main app=app1 path=/api/orders/whoami http=200 problem=-
$ ... login two --app app2 --as bob
signed-in     <time> two app=app2 as=bob revision=1 instance=<instance>
$ ... api two reports/whoami
api           <time> two app=app2 path=/api/reports/whoami http=200 problem=-
$ ... stop
closed        <time> main app=app1
closed        <time> two app=app2
```

- **A login.** `open main --app app1` already answers `signed-in`: the test identity provider
  approves every authorization at once and shows no page, so the first navigation to an app
  completes a login. `login main --app app1` answers the same line with `as=alice`.
- **The page.** `marker` shows the fixture page's text.
- **A session read.** `session` answers `http=200`: the gateway holds a session for the profile.
- **An API call.** `api main orders/whoami` answers `path=/api/orders/whoami http=200`: the call
  went through the gateway, which attached the session's token, to the mock API.
- **A second user.** `login two --app app2 --as bob` logs `bob` in to `app2` in a second profile,
  beside the first.
- **The end.** `stop` ends the process and its browser, and the `start` terminal prints its
  `stopped` line. Nothing persists across a stop: the next `start` begins with no profile, no
  session and a new cell. The bundle keeps the run's files, its logs among them, and can be removed with
  `rm -r "$E"`.
