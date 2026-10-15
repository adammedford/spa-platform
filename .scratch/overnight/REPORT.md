# Overnight report, 2026-10-13

**13 integrated, 2 partial, 0 failed, 3 need you, 21 blocked** (and 4 claimed elsewhere, waiting on their own branches).

- Base: `f8565031162d7b77548162760513b74734dfd6a6` (retro/guardrails-and-coding-standards). Integration branch: `overnight/2026-10-13` at `88ceb84`, worktree `/Users/adammedford/spa-worktrees/overnight`. Not pushed.
- Every integration gate was GREEN (gate logs in `/Users/adammedford/Documents/Projects/spa-platform/.scratch/overnight/log/`). The last one, after 2.6, ran 941 of 941 tests and the smoke check (`/healthz` 200).
- Nothing halted the run. Already landed before tonight: I-1, I-2, I-3, I-4, I-5, 1.0, 1.6a, 5.4, 4.1, 5.5.
- 55 questions are waiting in `/Users/adammedford/Documents/Projects/spa-platform/.scratch/overnight/questions.md`.

## Integrated

| Task | Merge | What landed (commits on the branch) |
| --- | --- | --- |
| 4.6 | `0c2ce20` | `3c87fac`, `1112fa1` forwarded headers from trusted networks, request limits, method allowlist; `0ad4683` review: the limits test reads its constants |
| 4.3 | `b39396b` | `7c51f7e` the request gate (stage 6), one rule id per refusal; `7c7a1f0` review: one ListLoggerProvider for both test hosts |
| 2.1 | `85558a7` | `4636ecd` registry validator, schema check and per-entry fingerprint; `836b958` review: schema-valid integers are read, not thrown on, and fingerprinted by value |
| 5.1 | `da18371` | `88c9750` three static SPAs with a marker, one hashed asset and app 2's data: image; `b6b23f5` review: /bff endpoints read from the contract, reserved-path check able to fail |
| 1.8 | `9527dcb` | `30e613a` the apps root: one Cognito client, branding style and secret parameter per app definition, written and validated, **not applied**; `ff00b47` review: P-02 reads app3.json, each definition decoded once |
| 2.5 | `a3cfe3c` | `6d6ee82` Parameter Store client secret source, pinned by version, cached by reference; `05d5963` review: one transport failure costs one reference, not the batch |
| 5.2 | `140e6a8` | `85b4a4e` bff-client.js, unit-tested in headless Chromium; `cfcd5b5` review: login's returnUrl resolved as the browser does, logout posts no fields |
| 4.4 | `77f21df` | `927fd1e` antiforgery (stage 9), header tokens only, bound to the app, cleared at logout; `57435e8` review: form-bound /bff/logout documented, full refusal checks |
| 4.5 | `deeb044` | `aac8cff` security headers and the per-app CSP builder (stage 5); `a1d5de9` review: keep 'none' when every addition is dropped |
| 2.2 | `d3cc049` | `32dee39` S3 object store and registry source, swapped whole, kept on any failure; `6f026e0` review: canary for the joined app's secret |
| 2.3 | `2cb1a11` | `19d5ada` host resolution and health (stages 3 and 4); `73a4622` review: health paths match ordinally |
| 2.4 | `bf4ffae` | `e7f34a8`, `710becd` asset endpoint (stage 10), keys from registry values and a validated path, store resolved per request; `78ad5ba` review: refusal rule ids asserted |
| 2.6 | `88ceb84` | `efda932` spactl registry, release, app and wait commands, round trip in memory; `6f6f927` review: spactl refuses an option its command does not read |

1.8 is integrated, but its proof still needs you: `terraform apply` of `infra/envs/poc/apps` after the identity root, then a manual code-flow login for apps 1 and 2.

Delegated decisions worth a glance (full lists are in each ticket's Answer):
- 4.4: an unsafe `/api` request with no session gets 403 `csrf_failed`, not 401. An SPA whose session expired sees 403 on a write, which bff-client.js (5.2) does not yet treat as "log in again".
- 4.5: the schema's `cspSource` pattern (`\S+`) admits `;` and `,`. The builder drops such sources, but tightening `registry.schema.json` was suggested, not filed.
- 4.6: plan row 4.3 does not list N-20; add it there or accept workstream 6's proof.
- 5.2: the Playwright browser download has never been tried on CI's Linux runner (this Mac had Chromium cached).
- 2.3: `InMemoryRegistry.SnapshotOf`'s ByHost index ignores case while the real registry's is ordinal. The double is an integrator path and was left alone.

## Partial (outstanding)

| Task | Merge | Outstanding |
| --- | --- | --- |
| 1.1 | `dd57db3` (`31233b3`, review `c223aa7`) | You: apply `infra/envs/poc/idp` against AWS, load `<issuer>/.well-known/openid-configuration`, and run task 1.0's lock check on this root. Integrator: change request `contracts/changes/1.1-random-provider-in-dependencies.md` (open) asks for a hashicorp/random 3.9.1 row in `docs/dependencies.md`. |
| 6.4 | `a5059b8` (`9d75616`, review `fb1dcf0`) | The request line lacks the `rule` and `session_ref` fields, and there is no naming convention for meters and activity sources. Both wait on `contracts/changes/6.4-request-telemetry-feature.md` (open), which proposes `IRequestTelemetryFeature` in Platform.Abstractions and a `Platform.<Project>` naming rule in `contracts/telemetry.md`. F-11's cross-process half belongs to 6.2. |

## Failed

None. Every task that ran reached a GREEN gate.

## Needs you

Grilled on paper overnight; the questions are in `questions.md` (ordered by what they unblock).

| Ticket | Task | Questions | Why it needs you |
| --- | --- | --- | --- |
| `issues/17-3.0-g0-cognito.md` | 3.0 G0: the Cognito items | Q1 to Q23 | Head of the critical path. Needs 1.1 and 1.8 applied, `certs.sh` run and a real Cognito run. The ticket's blockers are incomplete (1.1 applied and 1.6a's `certs.sh` run are missing). Proposes splitting into 3.0a (agent writes and dry-runs the harness) and 3.0b (you run it against Cognito). |
| `issues/01-I-6-close-g1b.md` | I-6 Close G1b, tag contracts-v1 | Q24 to Q43 | Integrator gate. Cannot start until G0 reports. "First decision record" is stale (ADR-0013 exists; G1b would be ADR-0014). The draft set is wider than the ticket says: `AccessTokenResult` and `ISessionStore` are unmarked, plus http-contract prose and three freeze tests. |
| `issues/43-CR-5.5-testapps-mirror.md` | CR-5.5 TestApps mirror | Q44 to Q55 | Integrator decision on `contracts/changes/5.5-testapps-mirrors-app1-no-longer.md`. Recommends keeping TestApps' defaults, rewording the comment, and adding Fixtures.Tests to two rows of `docs/dependencies.md`. |

Q7 (3.0) and Q24 (I-6) ask the same thing from the two sides: where G0 reports (a section in `spikes/g0/README.md`). Answer them together.

Also yours, outside the tickets:
- AWS applies: `infra/envs/poc/idp` (1.1), then `infra/envs/poc/apps` (1.8). These two unblock 3.0.
- Open change requests on the integration branch, all integrator decisions: `1.1-random-provider-in-dependencies.md`, `2.1-registry-schemas-and-jsonschema-net.md` (JsonSchema.Net row for Platform.Registry; optionally have the Dockerfile copy `contracts/*.schema.json`), `5.5-testapps-mirrors-app1-no-longer.md` (CR-5.5 above), `6.4-request-telemetry-feature.md`.
- Several logs note the gate command's `find ... -exec dirname {} ;` lost its backslash; agents ran it as `\;`, as CI does. Fix the source of that text.

## Blocked

| Task | Waits on | Why |
| --- | --- | --- |
| 1.3 | 1.2 | 1.2 is claimed and not merged. 1.6a is landed. |
| 1.4 | 1.3, 1.5, 1.8 | 1.3 blocked, 1.5 claimed and not merged. |
| 1.6b | 1.4 | 1.4 blocked; proof is a live AWS session. |
| 1.7 | 1.4, 2.2, 2.3, 2.6, 5.1, 5.3 | 1.4 blocked, 5.3 claimed and not merged; proof needs a deployed stack. |
| 1.9 | 1.2, 1.3, 1.4, 1.5, 1.8 | 1.2 and 1.5 claimed, 1.3 and 1.4 blocked behind them. |
| 3.1 | I-6 | G1b: `contracts-v1` not tagged; AGENTS.md forbids starting Platform.Identity. |
| 3.2 | I-6, 3.1 | G1b not tagged. |
| 3.3 | I-6, 3.2 | G1b not tagged. |
| 3.4 | I-6 | G1b not tagged. |
| 3.5 | I-6, 3.3 | G1b not tagged. |
| 3.6 | I-6, 3.3 | G1b not tagged. |
| 3.7 | I-6, 3.3 | G1b not tagged. |
| 3.8 | I-6, 3.3 | G1b not tagged. |
| 3.9 | I-6, 3.3, 3.7, 3.8 | G1b not tagged. |
| 4.2 | I-6 | Needs the frozen `IAccessTokenSource`. 4.1 is landed. |
| 6.1 | 5.3 | 5.3 claimed and not merged. G1a and 5.4 are met. |
| 6.2 | 6.1 | 6.1 blocked. |
| 6.3 | 6.1 | 6.1 blocked. |
| 6.5 | 6.1 | 6.1 blocked, and no headless browser on PATH. |
| 6.7 | 6.9 | G3 not tagged; carried by 6.9. |
| 6.9 | 1.6b, 1.7, 6.5 | G2 not tagged; all three blocked. |

Merging the claimed branches below unblocks 1.3, 6.1 and, behind them, most of workstreams 1 and 6. Everything in workstream 3 and 4.2 waits on 3.0, then I-6.

## Claimed branches to merge

Each is pushed to origin and not in `main` or `overnight/2026-10-13`. Merge each in its own PR.

| Task | Branch | Ahead of main | Tip |
| --- | --- | --- | --- |
| 1.2 and 1.5 | `task/1.2-and-1.5-network-and-bucket` | 2 | `8773fa2` Review: the CIDR validation was unsound, and four outputs were a trap |
| 5.3 | `task/5.3-mock-api` | 2 | `0968907` Review: the listener logged query strings, and lost the subject claim |
| 6.6 | `task/6.6-architecture-tests` | 1 | `ca90c24` Task 6.6: A-01 to A-04 as rules that are proven to be able to fail |

## Morning steps

1. Read `/Users/adammedford/Documents/Projects/spa-platform/.scratch/overnight/questions.md` and answer Q1 to Q55 (the first 23 unblock the most).
2. Push the integration branch:
   `git -C /Users/adammedford/Documents/Projects/spa-platform push -u origin overnight/2026-10-13 --no-verify`
   `--no-verify` skips the pre-push hook: A-05 allows one owner per diff, and this branch is multi-owner by design. The overnight gate built, tested and smoke-tested it after every merge.
3. Open one PR for `overnight/2026-10-13` into `main`. Note: the base `f856503` is on `retro/guardrails-and-coding-standards`, 2 commits ahead of `main` (`3cf48fe`, `f856503`), and origin's copy of that retro branch stops at `3cf48fe`. The PR will therefore also carry both retro commits unless the retro branch merges first.
4. Merge the claimed branches (1.2 and 1.5, 5.3, 6.6) in their own PRs.
5. Remove the worktrees, then prune:
   ```
   git -C /Users/adammedford/Documents/Projects/spa-platform worktree remove /Users/adammedford/spa-worktrees/overnight
   git -C /Users/adammedford/Documents/Projects/spa-platform worktree remove /Users/adammedford/spa-worktrees/ws1-infra
   git -C /Users/adammedford/Documents/Projects/spa-platform worktree remove /Users/adammedford/spa-worktrees/ws2-hosting
   git -C /Users/adammedford/Documents/Projects/spa-platform worktree remove /Users/adammedford/spa-worktrees/ws4-proxy
   git -C /Users/adammedford/Documents/Projects/spa-platform worktree remove /Users/adammedford/spa-worktrees/ws5-fixtures
   git -C /Users/adammedford/Documents/Projects/spa-platform worktree remove /Users/adammedford/spa-worktrees/ws6-verification
   git -C /Users/adammedford/Documents/Projects/spa-platform worktree prune
   ```
   All six worktrees had clean status when this report was written.
6. Then the AWS applies (1.1, 1.8) and 3.0.

## Leftovers

- `lsof -nP -iTCP -sTCP:LISTEN | grep -E 'dotnet|Gateway|MockApi'`: nothing listening.
- `pgrep -fl /Users/adammedford/spa-worktrees`: no processes.

Nothing was killed.

## Main checkout

`git status --porcelain` is empty, and `git rev-parse HEAD` is `f8565031162d7b77548162760513b74734dfd6a6`, on `retro/guardrails-and-coding-standards`. The main checkout is clean and unmoved. Only `.scratch/` (gitignored) changed: these two files, the gate logs and the grilling records appended to tickets 01, 17 and 43.
