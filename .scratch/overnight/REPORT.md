# Overnight report — 2026-10-16

**12 integrated, 8 partial, 0 failed, 2 need you, 10 blocked.**

Base `bf36a81` (tracker commit on `integrator/tracker-2026-10-16`). Integration branch
`overnight/2026-10-16` at `04d41c2`, worktree `/Users/adammedford/.worktrees/overnight-2026-10-16/overnight`.
The run did not halt. It is 56 commits and 170 files (+12526 / −491) over the base. The last gate
on the branch (6.1a) was green at 1502 of 1502 tests. Questions are in
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/questions.md` (30, Q1..Q30).

Highlights:

- **Identity components integrated (3.1 to 3.9 and 3.5a); 3.8 and 3.9 remain partial.** The gateway now has
  per-app OIDC and cookie schemes, a session store, refresh and `/bff`. The shell gate and the
  token-source binding are still switched off in the shipping gateway (see Partial and Q24).
- **The composed suites now sign in.** 6.2, 6.3 and 6.5 run against the gateway with a session
  required. 6.5 is 34 headless-Chromium tests.
- **The CI flake is fixed.** 6.1a reproduced it and fixed it in the harness. It also fixed the
  same race for `Rejected` in six more asserts.

## Integrated

| Task | Merge | What landed |
| --- | --- | --- |
| 2.7a | `facaca5` | Hosting and the registry name their stages by entry point (comments only; 1209 tests) |
| 3.1 | `992fd8c` | Host-scoped `RegistrySchemeProvider` replaces the stock scheme provider; R-B6 proven through the framework |
| 3.2 | `d5a6f0c` | OIDC options per app over one shared backchannel and metadata manager per issuer |
| 4.7a | `09be9fc` | The method allowlist writes `gate.method`; security and proxy name their stages |
| 3.3 | `dc14faa` | `__Host-spa-session` cookie options, an app- and client-bound ticket store, a callback-blind cookie manager |
| 3.4 | `a208c85` | In-memory session store: bounded, swept every minute, registered by `AddPlatformIdentity` |
| 3.5 | `f14f90f` | OIDC handler events: 401 for a fetch, sub/name/email tickets, one failure page (`auth.callback`) |
| 3.6 | `baefed3` | Options invalidator: swap, then evict; purge sessions on removal or a new client; no cached failed build |
| 3.7 | `6d30d1f` | `standard` and `cognito` IdP profiles; the issuer's profile owns the sign-out redirect |
| 3.5a | `afb888f` | Identity held to the shared rule ids by a source scan; N-16 follows a session key through every log line |
| 6.3b | `a5296b3` | N-20's composed 405 carries `gate.method` on the line and in the counts; telemetry names its stages |
| 6.1a | `04d41c2` | The harness reports the outcome of its own registry write, not of one poll (the CI flake) |

Full log: `git -C /Users/adammedford/Projects/spa-platform log --oneline bf36a81..overnight/2026-10-16`.

## Partial (Outstanding)

- **6.3:** the signed-in negative rows are implemented and tested hermetically. Plan row 6.3
  requires green at G4; that gate is not closed. Outstanding: deployed negative evidence and G4.

- **3.8**, `6919def`: `IAccessTokenSource` with gateway-owned refresh locking over the token
  library. *Outstanding:* the shipping gateway does not bind it yet. Binding it turned 18
  sessionless ws6 composed tests red. The binding is the internal `AddPlatformAccessTokenSource`,
  and moving it into `AddPlatformIdentity` takes one line (Q24). A provider outage throws
  `IdentityProviderUnavailableException`, which reaches the client as a 500, until change request
  4.2 is decided (Q25).
- **3.9**, `67f0115`: `/bff/session`, `/bff/login` and `/bff/logout` are live, and the shell gate
  is written. *Outstanding:* the shell gate is off in the shipping gateway behind the internal
  `AddPlatformShellGate` (turning it on made 15 composed tests red), and so is the token-source
  binding. Both wait until every composed row signs in (Q24).
- **4.2**, `69d11a7`: the token step runs ahead of the forwarder, with the upstream header rules
  (N-11, N-12, the `/api` half of N-19, T-C3, T-C7). *Outstanding:* the change request
  `contracts/changes/4.2-access-token-result-provider-unavailable.md` is open (Q25). With no
  `IAccessTokenSource` registered, `/api` is forwarded without `Authorization` and a startup
  warning is logged.
- **6.2**, `aca1320`: functional rows F-01 to F-05, F-08 to F-10 and F-07's hermetic half, against
  the session-required gateway, and F-06 and F-11 now sign in first. *Outstanding:* F-07's deployed
  half (task id, start time, task definition, plans) waits for G5. `tests/Gateway.SecurityTests`
  still makes sessionless requests in its shared fixtures (Q24).
- **6.5**, `0d27c7d`: the browser suite, 34 tests in headless Chromium against a local stand-in for
  the ALB, covering every D-tier row. *Outstanding (task 6.9):* the deployed binding (the tunnel,
  Cognito's real login page, CloudWatch logs, saved trace and dumps), and the AWS-only clauses of
  F-01 and F-07.
- **1.5**, `d03f7f4`: the teardown role in bootstrap, its lookup in the platform root, and one
  cell-name rule enforced in three places with drift tests. *Outstanding:* P-04 needs the applied
  bucket (G3 / 6.9). **Integrator:** update `contracts/resource-tags.md` (three exempt bootstrap
  resources) and `contracts/s3-layout.md` (it still says the teardown role "awaits workstream 1
  implementation"). Change request 1.5a is undecided (Q1–Q12).
- **1.1**, `dd57db3` (already on main before tonight; **nothing was done this run**): ticket 02 was
  stale in the queue. *Outstanding (human):* apply `infra/envs/poc/idp` and load
  `<issuer>/.well-known/openid-configuration`. If bootstrap is already applied, re-apply it so the
  CI plan role gets `cognito-idp:AdminGetUser`. Drop ticket 02 from the queue.

## Failed

None.

## Needs you

- **1.2** (ticket 03) was grilled on paper: 11 questions (Q13–Q23), no ADR candidates. The ticket
  is stale. Its code reached main in #5 (`5e2cbcc`). What remains is an exact-rule assertion,
  stripping the default security group, a zone filter, the human apply and P-06. The ticket's
  Status still reads `claimed`.
- **CR-1.5a** (ticket 65) was grilled on paper: 12 questions (Q1–Q12), and a new ADR candidate if
  you choose the regional namespace. Main recommendation: put the platform bucket in the S3 account
  regional namespace (`spa-platform-<cell>-<account id>-<region>-an`). No bucket exists yet, and
  only that namespace stops another account from taking the name between teardown and redeploy.
  The Object Lock row in ADR-0009 should lead with "Object Lock protects versions, not keys".
  Decide this before the first platform-root apply.

The 2026-10-15 questions file records that you pre-accepted every recommendation tonight's grills
raise. The Answer lines are still empty so that you can confirm.

## Blocked

| Task | Waits on | Why |
| --- | --- | --- |
| 1.3 | 1.2 | Terraform landed (`b2c0f70`); "applies cleanly" is the human platform-root apply, together with 1.2 |
| 1.4 | 1.3, 1.5 | Repo half landed as 1.4a; "service steady, target healthy" needs the applied root |
| 1.6b | 1.4 | `verify-tunnel.sh` exits 0 only against a deployed service |
| 1.7 | 1.4 | Each script must run unattended against a deployed gateway |
| 1.9 | 1.2, 1.3, 1.4, 1.5 | None of them is landed; plan generation stays fail-closed until a reviewed 1.9 producer and consumer exist |
| 6.7 | 6.9 | Needs G3, which 6.9 carries |
| 6.9 | G2, 1.6b, 1.7, 6.5 | G2 is not tagged, and 1.6b and 1.7 are blocked; 6.5 has now landed as partial |
| 2.1b | MR-1 | ADR-0015 decision 4 defers the `timeoutSeconds` maximum to MR-1, which has no plan row (Q26) |
| 6.3a | 6.3 | Hermetic session halves integrated; parent 6.3 remains partial until G4 |
| CR-6.x | 6.1a | The symptom report for 6.1a. 6.1a landed tonight, so it can close (Q29) |

## Claimed branches to merge

None. The run claimed no branch outside the integration branch. The five lane branches still
checked out in worktrees (`task/1.5-bucket`, `task/2.7a-hosting-names-its-stages`,
`task/3.5a-identity-writes-its-rule-ids-from-the-shared-constants`,
`task/4.7a-allowlist-writes-gate-method-and-security-names-its-stages`,
`task/6.1a-harness-update-registry-race`) are all ancestors of `overnight/2026-10-16`. They need no
PR of their own.

## Main checkout

- `git rev-parse HEAD` is `bf36a8184fdcf0dd1fb33ee196ffa27b380100af`, **as expected** (branch
  `integrator/tracker-2026-10-16`).
- `git status --porcelain` is **not empty**:
  - 20 tickets the run updated, all under `.scratch/overnight/issues/`: 03, 06, 18–27, 36, 37, 39,
    56, 68-6.1a (61-6.1a when this ran), 62, 63 and 64.
  - The new, untracked ticket `65-CR-1.5a-bucket-name-and-object-lock.md`.
  - This report.
  - `questions.md`.
  - Last run's files, renamed to keep them: `REPORT.md` became `REPORT-2026-10-15.md`, and
    `questions.md` became `questions-2026-10-15-answered.md`.

  Everything is integrator-owned tracker state, and none of it is on `overnight/2026-10-16`.
  No source file in the main checkout changed.

## Morning steps

1. Read `/Users/adammedford/Projects/spa-platform/.scratch/overnight/questions.md` and answer it.
   Start with Q1–Q12 (bucket name, before any platform-root apply) and Q24–Q25 (turning login on in
   the shipping gateway).
2. Put the tracker changes on the branch so the PR carries them. They are in the main checkout and
   not on `overnight/2026-10-16`. For example, copy `.scratch/overnight/` into the overnight
   worktree and commit it there as one integrator commit.
3. `git -C /Users/adammedford/Projects/spa-platform push -u origin overnight/2026-10-16 --no-verify`.
   `--no-verify` is needed because A-05 allows one owner per diff, and this branch is multi-owner
   by design. The overnight gate built, tested and smoke-tested each merge.
4. Open one PR from `overnight/2026-10-16` to `main`.
5. Merge the claimed branches in their own PRs. Tonight there are none.
6. Remove each worktree under `/Users/adammedford/.worktrees/overnight-2026-10-16`, then prune. All
   six are clean.
   ```
   git -C /Users/adammedford/Projects/spa-platform worktree remove /Users/adammedford/.worktrees/overnight-2026-10-16/overnight
   git -C /Users/adammedford/Projects/spa-platform worktree remove /Users/adammedford/.worktrees/overnight-2026-10-16/ws1-infra
   git -C /Users/adammedford/Projects/spa-platform worktree remove /Users/adammedford/.worktrees/overnight-2026-10-16/ws2-hosting
   git -C /Users/adammedford/Projects/spa-platform worktree remove /Users/adammedford/.worktrees/overnight-2026-10-16/ws3-identity
   git -C /Users/adammedford/Projects/spa-platform worktree remove /Users/adammedford/.worktrees/overnight-2026-10-16/ws4-proxy
   git -C /Users/adammedford/Projects/spa-platform worktree remove /Users/adammedford/.worktrees/overnight-2026-10-16/ws6-verification
   git -C /Users/adammedford/Projects/spa-platform worktree prune
   ```
7. After merging, refresh the roadmap's generated block (`tools/roadmap.py`) in the landing
   checkout.

## Leftovers

- `lsof -nP -iTCP -sTCP:LISTEN | grep -E 'dotnet|Gateway|MockApi'`: no output. Nothing is listening.
- `pgrep -fl /Users/adammedford/.worktrees/overnight-2026-10-16`: no output. No process is running
  from the run's worktrees.
- Nothing was killed. Six run worktrees are still in place, all clean (step 6).

## PR41 review correction

The original 6.3 merge subject claimed the whole row, but its done-when is green at G4.
The reviewed branch corrects that subject to part-landed and regenerates the roadmap. The
historical implementation and review records above do not establish a deployed gate.

Refresh-failure regressions reproduced a token echoed through a provider error in an exception
and in both vendor and platform loggers. A callback error description also reached vendor logs.
The review fixes bound identity outcomes and suppress credential-bearing vendor categories at
both filters and the formatter, while retaining platform request telemetry.
