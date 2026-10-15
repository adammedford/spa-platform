# Overnight report — 2026-10-15

## PR review amendment — 2026-10-15

This file preserves the overnight snapshot and its original questions. The final ADR-0014 and
change requests supersede these review errors in Q49, Q56, Q63, Q69 and their ticket-57 sources:

- Current Terraform at `070e3fae` defaults to `spa-platform-<cell>`, with a bucket-name override.
  The account-qualified name is proposed, not implemented; `spa-platform-<cell>-*` misses the former
  but matches the latter. Teardown grants must use the actual bucket ARN, including overrides.
- `Allow` plus `BoolIfExists: true` accepts long-term keys when the MFA key is absent. `Deny` plus
  `BoolIfExists: false` also denies federation when the key is absent. ADR-0014 now separates
  IAM-user trust (`Bool: true`) from explicitly allowlisted federated human roles whose MFA is
  enforced upstream; input validation rejects root and STS-session ARNs.
- Object Lock can be enabled on existing versioned buckets; its irreversible enablement and
  retention/bypass trade-offs, not mandatory bucket replacement, are the relevant constraints.
- Role separation protects against the ordinary writer, not a trusted human who can switch
  between teardown credentials and writer credentials. No AWS apply or live role is claimed.
- The plan-ignore patterns land here; the apply-recipe and CI artifact follow-ups remain open.

Primary sources checked on 2026-10-15:
[IAM MFA semantics](https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_policies_condition-keys.html#condition-keys-multifactorauthpresent),
[S3 Object Lock configuration](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock-configure.html),
[S3 conditional writes](https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes.html).


**2 integrated, 1 partial, 0 failed, 8 need you, 21 blocked.** 143 questions, 0 claimed branches to
merge.

Base `070e3fae5ab47a210097f2245f0e12aa28605679` — which **is** `origin/main`. Integration branch
`overnight/2026-10-15`, **13 commits, tip `7efaeff`**, worktree
`/Users/adammedford/.worktrees/overnight-2026-10-15/overnight`. Tickets in
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues`, gate logs in
`/Users/adammedford/Projects/spa-platform/.scratch/overnight/log`.

**The branch carries five lanes, not three.** `2.3b` and `4.3b` landed in the run's first half and
were reported at 08:35; `1.4a`, `6.2a` and `6.3a` ran after that. This file supersedes the 08:35
interim report and carries everything from it that is still true. One PR takes all five.

**Every gate was GREEN on its first run** — five implement gates, five review gates, five integration
gates. No lane went red, nothing was rolled back, no change request was opened and no integrator path
was touched by any lane.

**Your checkout is untouched.** `HEAD` is still `8323750`, `git status --porcelain` shows exactly the
five expected paths and nothing else, and there are no leftover listeners or processes. Verified
again at the end of the run; details at the bottom.

Yesterday's files are preserved, because yesterday's carried your 145 answers:
`REPORT-2026-10-14.md` and `questions-2026-10-14-answered.md`.

---

## Integrated (2)

`git -C /Users/adammedford/Projects/spa-platform log --oneline 070e3fae5ab47a210097f2245f0e12aa28605679..overnight/2026-10-15`

```
7efaeff Merge part of task 6.3a: the negative rows that need no session, proven in the composed gateway
7e07b20 Review: registry row carries its benign change, two assertions made able to fail
a6fc905 Task 6.3a: the negative rows that need no session, proven in the composed gateway
04c7489 Merge task 1.4a: the cell's ECS resources and its two IAM roles, proven by plan
3ae9531 Review: close three widenings the task 1.4a assertions could not see
6eb424f Merge task 6.2a: F-06 and F-11 proven in the composed gateway
a150ed9 Review: prove the clauses that could not fail, and read the asset path
9910a26 Task 1.4a: the cell's ECS resources, written and planned
fd2b124 Task 6.2a: F-06 and F-11 proven in the composed gateway
a09a79d Merge task 4.3b: the security and proxy stages read the shared rule ids
be1f27b Merge task 2.3b: hosting reads the shared rule ids and deletes its own copy
4267e3b Task 4.3b: the security and proxy stages read the shared rule ids
950bb8d Task 2.3b: hosting reads the shared rule ids and deletes its own copy
```

| Task | Merge sha | What landed |
| --- | --- | --- |
| **1.4a** (ticket 58) | `04c7489` | The cell's ECS half, inside the existing module: `infra/modules/cell/ecs.tf` (cluster, log group, task environment, two container definitions, task definition, service) and `iam.tf` (a separate execution role **and** task role), with `variables.tf`, `outputs.tf`, `README.md` and the platform root (`main.tf`, `outputs.tf`, `variables.tf`) wired to them and publishing the four outputs the infrastructure-outputs contract already had fields for — `ecsCluster`, `ecsService`, `logGroup`, `gatewayImage`, so **no change request**. Ten files, all `infra/`, all owner 1's. 28 new plan-time assertions against a mock provider (25 in `infra/modules/cell/tests/module.tftest.hcl`, 3 in the root's), each reading its expected value out of the contracts and fixtures rather than restating it. **30 mutations, all caught** — including a bucket prefix widened by one segment, a parameter path widened to every cell, a fifth `ssmmessages` action and an unmentioned fourth statement on the task role. Gate: 1116 tests 0 failed, `fmt` clean, all six roots validate, and `terraform test` run separately over CI's own discovery expression (54 passing, 28 of them new). A-05: 10 files, all ws1-infra. |
| **6.2a** (ticket 59) | `6eb424f` | F-06's hermetic tier and F-11 proven in the composed gateway: seven new files in `tests/Gateway.IntegrationTests/Functional/` (`PerAppHeadersTests`, `AssetFallbackTests`, `ReleaseSwitchTests`, `ApiTelemetryTests`, `ConcurrentApiCallsTests`, plus `CapturedTelemetry` and `ContractText`), **20 `[Fact]` methods** — the ticket's prose says 16 and is wrong; the review corrected the record (Q133). The task 6.1 `GatewayHarness` is the project's only seam; `src/`, `SpaPlatform.slnx`, `Directory.Packages.props` and every contract are untouched. Two guards read the thing they guard: the baseline CSP policy and fixed header set are parsed out of `contracts/http-contract.md`, the request line's field list and order out of `contracts/telemetry.md`. Spans and metrics go through in-memory exporters wired in via `GatewayHarnessOptions.ConfigureServices`, so **no collector runs**. Every clause was green on arrival, so every one was watched failing by mutation (2 of 35, 2 of 35, 5 of 40, 4 of 44, 7 of 51). Gate: 1115 on the lane, 1116 on the merge, 0 failed, 0 skipped. A-05: 7 files, all ws6-verification. |

Both of these are whole. The third lane merged but is recorded **partial** — next section.

### Carried from the 08:35 half of the same run

| Task | Merge sha | What landed |
| --- | --- | --- |
| 2.3b | `be1f27b` | Hosting reads the shared rule ids. One deletion: `src/Platform.Hosting/RuleIds.cs` (23 lines) is gone, and the three references at `HostResolution.cs:35`, `Assets.cs:73` and `Assets.cs:86` bind to `Platform.Abstractions.RuleIds` through the `using` both files already carried — so no call site, project reference or string changed. 1091 tests, 0 failed. Two mutations prove the suite holds the swap. A-05: one file, all ws2-hosting. |
| 4.3b | `a09a79d` | The security and proxy stages read the shared rule ids: all ten places `src/Platform.Security` and `src/Platform.Proxy` spelled an id now read `RuleIds`. Driven test-first by a new per-project guard (`tests/*/SharedRuleIdsTests.cs`) that scans its own sources against `RuleIds.All` and was red on all ten literals. 1095 tests, 0 failed. A-05: 6 files, all ws4-proxy. |

ADR-0015's three adoption tickets are now two-thirds done; only **3.5a** (ticket 56) remains, and it
waits for `contracts-v1`.

### Branch names are dated, and you need the dated ones

Both 08:35 lane branches were still checked out at yesterday's archived worktrees, so git could not
reuse the assigned names:

- `task/2.3b-hosting-adopts-the-shared-rule-ids-1015` (`950bb8d`)
- `task/4.3b-security-and-proxy-adopt-the-shared-rule-ids-2026-10-15` (`4267e3b`)

The three later lanes kept their plain names: `task/1.4a-cell-ecs-terraform` (`3ae9531`),
`task/6.2a-functional-rows-no-session` (`a150ed9`), `task/6.3a-negative-rows-no-session` (`7e07b20`).
**All five are already merged into `overnight/2026-10-15`**, so none needs a PR of its own.

One thing to know before you delete anything: yesterday's `task/4.3b-...` tip (`b802f1c`) is **not**
reachable from tag `archive/overnight-2026-10-14`. Deleting that branch orphans the commit. Its only
content is the change request ADR-0015 made moot, so losing it costs nothing — but if you want it
kept, tag it first.

## Partial (1)

### 6.3a — ticket 60 — merged as `7efaeff`, subject "Merge **part of** task 6.3a"

**What landed.** `tests/Gateway.SecurityTests`, empty until now and already commented "Negative
suite, N-01 to N-27", holds **thirteen** of plan row 6.3's rows driven through the task 6.1 harness —
N-08, N-09, N-13, N-14, N-16, N-20, N-24, N-25 and N-27 whole, and N-07, N-10 and N-12 partial with
the unproven clause named in the test. 20 new files, 1205 tests green, 0 failed. Rows are asserted
only through what a client outside the gateway can observe, and every rule id comes from
`Platform.Abstractions.RuleIds` (ADR-0015) rather than a literal. Ten `src/` mutations were measured
and reverted (67, 2, 2, 3, 1, 2, 1, 4, 5 and 1 failures of 89), plus a test-side mutation proving the
end-of-run canary sweep really fails a run. A-05: 20 files, all ws6-verification.

**Outstanding.** **N-11 whole, and N-12's `Set-Cookie` clause — both absent rather than red.**
Measured on this branch, not assumed: an `/api` call's `Cookie`, `Authorization`, `X-CSRF-TOKEN`,
`Via`, `X-Real-IP` and `X-Original-URL` all reach the upstream, and an upstream's `Set-Cookie` reaches
the browser's cookie jar. That stripping is plan row **4.2**, whose needs are "4.1, G1b", so it has
not landed — workstream 6 owns the proof, not the stage. **The carve's own spec was wrong** that these
two rows rest on landed work. No change request was filed: nothing of yours is needed and no contract
is contradicted. They join `tests/Gateway.SecurityTests`, against the same harness and the same
`HostileUpstream`, when 4.2 lands. **Two acceptance boxes on ticket 60 are therefore unticked**, and
Q136 asks you to put a line on ticket 27 and row 4.2's done-when so the proof is not forgotten.

**This sub-row cannot close row 6.3**, whose done-when is G4. Neither can 6.2a close 6.2 (G2), nor
1.4a close 1.4 (needs AWS). Per `tools/roadmap.py`, a split id never lands its parent, and all three
merge subjects name only the sub-id.

## Failed (0)

No gate ended red. Every one of the fifteen gate logs ends `GATE: GREEN`. For the record, the three
late lanes' final lines:

- `.scratch/overnight/log/1.4a-integrate.txt` — `total: 1116 / failed: 0 / skipped: 0`, six roots validate, `GATE: GREEN`
- `.scratch/overnight/log/6.2a-integrate.txt` — `total: 1116 / failed: 0 / skipped: 0`, smoke on 5690, `GATE: GREEN`
- `.scratch/overnight/log/6.3a-integrate.txt` — `total: 1205 / failed: 0 / skipped: 0`, smoke on 5700, `GATE: GREEN`

### Six review findings worth your eye, all fixed on their branches before the merge

These are the ones where a test **could not fail** and now can — the failure mode this run exists to
catch:

1. **1.4a, the ticket's headline claim.** The task role's "no unrecognised statement" run did not fail
   on widening. Every assertion read only `data.aws_iam_policy_document.task`, which is a closed
   account of the role only while that document is the whole of what the role holds. Three
   declarations widen it without touching that document and each left the suite green: a second
   `aws_iam_role_policy` granting `s3:DeleteObject` on `"*"` — the very delete `contracts/s3-layout.md`
   reserves for the teardown role — plus `managed_policy_arns = [AdministratorAccess]` and an
   `inline_policy` block. Now three assertions cover it; each mutation fails 1 of 26.
2. **1.4a.** `var.gateway_port` could diverge from the port the gateway actually listens on:
   `src/Gateway/Dockerfile` fixes `ASPNETCORE_HTTP_PORTS=8080` and nothing overrode it, so default
   8080 -> 9090 produced a task whose load balancer sends to a dead port — row 1.4's own done-when,
   unprovable. Fixed by setting `ASPNETCORE_HTTP_PORTS` from the variable (Q124).
3. **1.4a, Spec axis.** `ForwardedHeaders:TrustedNetworks` was the whole VPC range where the config
   schema says "the ALB subnets, in CIDR form", so stage 1 trusted `X-Forwarded-For` from every
   unallocated address in the cell. The assertion could not catch it because it compared the
   environment against `var.vpc_cidr` — the guard restated the value it protected (Q123).
4. **6.2a, Spec axis.** The spec's story "an entry that tries to add a directive outside the sanctioned
   set is refused rather than partially honoured" was claimed by a test name and not proven: the test
   read app2's served policy, and app2's fixture attempts no out-of-set directive, so the failing input
   was never supplied. Now a second test publishes an entry naming `worker-src` through
   `UpdateRegistryAsync` and asserts `RefreshOutcome.Rejected` with the policy unchanged.
5. **6.2a.** The platform-span half of an `ApiTelemetryTests` assertion was vacuous — nothing in the
   tree creates an `ActivitySource`, so the filtered set was always empty and `Assert.All` passed
   regardless. The vacuity is now named in the comment and the mechanism is asserted against an input
   that breaks it; the `Assert.All` line was kept so it bites when a platform `ActivitySource` lands.
6. **6.3a.** `RegistryRejectionTests`' forbidden-CSP row reassigned the same property its benign change
   wrote, destroying it — so the row published **one** change, not the two the class's whole stated
   design rests on, and its `DoesNotContain` was vacuous because the value had never been there.

## Needs you (8)

All eight are `ready-for-human` plan rows or change requests — unchanged from 08:35, since no lane
touched any of them. Where a ticket had been grilled before, this run's pass re-derived it on trunk,
and **three of those passes changed their own earlier answers** — so do not assume last night's `yes`
still holds where this says otherwise.

| Ticket | What it is | Questions |
| --- | --- | --- |
| 17 — 3.0 (G0) | Third pass. **The head of the critical path**: G0 -> I-6 -> 15 of the 21 blocked rows. Rounds 6–8 cover the tree the sitting runs in, bootstrap's own apply inputs and its single copy of local state, and — decisively — that `tools/roadmap.py` says a split id never lands its parent row, so only a merge subject reading "Merge task 3.0: …" can ever close G0's last row. | Q1–Q13 |
| 01 — I-6 | Re-grilled on trunk, which **killed four existing questions outright**. Sharpest new finds: the landed harness quietly narrows the plan's item 2 by demoting resource binding to a finding, and its C-2.9 route contradicts the frozen evidence tab's T-B12. | Q14–Q27 |
| 02 — 1.1 | **First grilling, and it found the worst thing in the report.** The documented apply recipe writes every generated test-user password into an un-gitignored `idp.tfplan` *inside the repo*, and the apps root's equivalent writes the client secrets. Also: three cell-name patterns disagree and all three admit a two-character cell that violates two G1a-frozen contracts. | Q28–Q45 |
| 57 — CR-1.5 | The only change request still reading `open`, and your working tree holds the answer. The rebase reversed one recommendation and found four new defects in your drafted ADR-0014 — including that committing your `.gitignore` verbatim would **re-ignore 72 tracked issue-tracker files** that trunk deliberately un-ignored. | Q46–Q62 |
| 06 — 1.5 | Rounds 5–7. Three defects in ADR-0014 as drafted: the role's S3 scope `spa-platform-<cell>-*` matches no bucket; nothing validates the derived ARN because it sits in a condition rather than a `Principal`; and the claim that `If-None-Match: *` would refuse a write after a delete is contradicted by S3's documented delete-marker behaviour. | Q63–Q73 |
| 03 — 1.2 | Re-grilled on trunk, which moved the picture: the branch is fully merged, 1.3 landed on top, CI now runs `terraform test`, so last night's request for a CI step is moot. New finds include two uncited vendor claims in `network.tf`. | Q74–Q88 |
| 04 — 1.3 | **First grilling.** Stale twice over. The sharpest find is that the landed file leaves `idle_timeout`, `deregistration_delay` and all four health-check timings at defaults nobody chose — and change request 2.1 had explicitly deferred the ALB idle timeout, the platform's real deployed request ceiling, to this very task. | Q89–Q104 |
| 53 — CR-6.4a | Re-run. The mechanism verdict survives but the facts under it did not. **A fifth option appeared and is cheaper than all four the ticket lists**: swap stages 1 and 2 so telemetry is outermost. The first run's option 1 is defective — `Health.TryAnswerAsync` answers `/healthz` on any method, so it would make `TRACE /healthz` a 200 and break N-20. Ticket 60's Q141 now waits on this. | Q105–Q116 |

Two questions have no grilled ticket of their own: **Q117** decides gap MR-1, the real head of ticket
49's chain, and **Q118** is the `src/Gateway/Dockerfile` comment whose change request lives only on
the archived branch. **Q119–Q143** are the three late lanes' own questions and recommendations;
Q119, Q120, Q135 and Q136 are the four of those worth reading tonight.

### One defect in this run's own output

Ticket 02's grilling record claims "CI never runs the 11 Terraform test runs". **That is wrong on this
base.** `.github/workflows/ci.yml` carries the step "Test every root and module with tests under
`infra/`", which discovers every `*.tftest.hcl` under `infra/` and runs each on the pinned Terraform
1.11.0 — verified against `origin/main` directly, and task 1.4a's 28 new runs went through exactly
that expression tonight. Ticket 03's re-grill has it right and ticket 02's does not; Q40 asks you to
strike that question and correct the record.

## Blocked (21)

Nothing here was agent work tonight. Grouped by what actually holds it. **Three rows moved**: 6.2a,
6.3a and 1.4a were carved out of 6.2, 6.3 and 1.4 and landed — the parent rows stay open, and the
carves cannot close them.

**Behind `contracts-v1` / G1b (14).** AGENTS.md is explicit that nothing in `src/Platform.Identity`
starts before I-6 tags it, and `git tag` shows only `skeleton-v1` and `archive/overnight-2026-10-14`.

| Row | Waits on |
| --- | --- |
| 3.1 | I-6 |
| 3.2 | I-6, 3.1 |
| 3.3 | I-6, 3.2 — the widest row in ws3; five later rows name it, and it carries the session-fixation fix |
| 3.4 | I-6 — the store contract suite asserts on a shape marked `[Freeze("G1b")]` |
| 3.5 | I-6, 3.3 |
| 3.6 | I-6, 3.3 — proves S-D1–S-D7, R-C1–R-C5 and that R-D10 no longer reproduces |
| 3.7 | I-6, 3.3 — `IIdpProfile` is one of the interfaces G1b freezes |
| 3.8 | I-6, 3.3 — A-04 also restricts the token package to `Platform.Identity` |
| 3.9 | I-6, 3.3, 3.7, 3.8 — **owns stage 8**, the absence tickets 59 and 60 had to record |
| 3.5a | I-6, 3.5, 3.9 — its constants are on trunk, waiting for an `auth.*` refusal to raise |
| 4.2 | I-6 — **and now the named blocker of 6.3a's two outstanding rows** |
| 6.2 | I-6 — nine of eleven rows start signed in; F-06 and F-11 carved out and landed |
| 6.3 | I-6 — thirteen rows need a session; fourteen carved out and landed, done-when is G4 |
| 6.5 | No headless browser on this Mac — chromium, google-chrome and playwright all missing — so N-26 and the `__Host-` prefix rows cannot be decided here at all. Installing Playwright is a human step, and the rest needs the deployed stack at G3. |

**Behind an AWS apply (4).** 1.4 waits on 1.3 and 1.5, each landed only in part with an apply
outstanding — **ticket 58 carved out everything in it that needs no AWS and landed it**, but the row's
done-when is a steady service on a healthy target. 1.6b and 1.7 wait on 1.4: a Session Manager port
forward needs a running task, and each deploy script must run unattended against a real cell. 1.9's
P-01–P-03 police an allowlist of resource types and counts plus the two security groups and the task
role; **1.4a gives it the consistent ECS plan it has been missing**, but the policy still waits on the
cell's rows being applied and stable. 1.7's `teardown.sh` also waits on the CR-1.5 answer.

**Behind a later gate (2).** 6.7 opens at G3, which 6.9 carries; 6.9 opens at G2 and needs 1.6b, 1.7
and 6.5, all blocked. 6.9 is the single row carrying G3, G4 **and** G5, and every part of it runs
against real AWS.

**Behind an integrator decision (1).** 2.1b's original blocker is discharged — ticket 48 landed and
`ProxyTimeout.MaximumTimeoutSeconds = 2,147,483` is in the tree — but ADR-0015 decision 4 defers the
fix until gap **MR-1** says which registry documents a given gateway build accepts. That is Q117.

### Nothing left to carve

The 08:35 report named three rows worth carving — 6.3a, 6.2a and 1.4a. All three were carved,
dispatched, reviewed and merged in the second half. On this base there is no remaining blocked row
with a slice that is both provable without AWS and unblocked by G1b.

## Claimed branches to merge (0)

None. All five lane branches are already merged into `overnight/2026-10-15`, so the one PR carries
everything. The branches behind the part-landed rows —
`origin/task/1.2-and-1.5-network-and-bucket`, `task/1.3-alb`, `origin/task/3.0a-cognito-harness` — are
all already ancestors of the base, so nothing is waiting there either. **`task/1.3-alb` can simply be
deleted**: it is an ancestor of both `origin/main` and the integration branch.

Tickets 34 (5.3) and 40 (6.6) still read `claimed` in the tracker; both landed (`2a80154`,
`943654c`). Stale Status lines, not work.

---

## Morning steps

1. **Read `questions.md` first** —
   `/Users/adammedford/Projects/spa-platform/.scratch/overnight/questions.md`, 143 questions, each
   with a recommendation and an empty `Answer:` line. Q1–Q13 and Q28–Q45 set up the AWS sitting, which
   is the one thing that moves the critical path. Three cross-ticket conflicts are flagged there so you
   decide each once: the legal cell name (Q53 governs; Q33 and Q64 follow it), the ADR number for G0's
   findings (Q10, Q19, Q100 and Q110 all reach for 0016), and ticket 53's stage swap against 4.3b's
   landed work (Q111 sequences them). Of the new block, **Q119, Q120, Q135 and Q136** are the four
   worth reading before anything else.
2. **Push the integration branch.**
   ```bash
   git -C /Users/adammedford/Projects/spa-platform push -u origin overnight/2026-10-15 --no-verify
   ```
   `--no-verify` because A-05 is one owner per diff and **this branch is multi-owner by design**
   (ws1-infra, ws2-hosting, ws4-proxy, ws6-verification). The overnight gate built it, ran every suite
   and smoke-tested it on each merge commit; each lane's own diff was single-owner and A-05 confirmed
   that per lane (10, 1, 6, 7 and 20 files).
3. **Open one PR** for `overnight/2026-10-15` -> `main`. It carries all five tasks — 2.3b, 4.3b, 6.2a,
   1.4a, 6.3a. Nothing else needs a PR of its own.
4. **No claimed branches to merge.** Delete `task/1.3-alb` if you like — it is already upstream. Keep
   `b802f1c` in mind before deleting yesterday's `task/4.3b-...` (see above).
5. **Remove the worktrees.**
   ```bash
   git -C /Users/adammedford/Projects/spa-platform worktree remove /Users/adammedford/.worktrees/overnight-2026-10-15/overnight
   git -C /Users/adammedford/Projects/spa-platform worktree remove /Users/adammedford/.worktrees/overnight-2026-10-15/ws1-infra
   git -C /Users/adammedford/Projects/spa-platform worktree remove /Users/adammedford/.worktrees/overnight-2026-10-15/ws2-hosting
   git -C /Users/adammedford/Projects/spa-platform worktree remove /Users/adammedford/.worktrees/overnight-2026-10-15/ws4-proxy
   git -C /Users/adammedford/Projects/spa-platform worktree remove /Users/adammedford/.worktrees/overnight-2026-10-15/ws6-verification
   git -C /Users/adammedford/Projects/spa-platform worktree prune
   ```
   Yesterday's worktrees under `/Users/adammedford/.worktrees/overnight/` are still there too, and are
   now only history; remove them when you have decided about `b802f1c`.

**One thing to expect when you fast-forward.** Your checkout's `.gitignore` (both at `8323750` and in
your working tree) ignores `.scratch/` wholesale, which is why the 60 ticket files do not show in
`git status`. Trunk narrowed that to `.scratch/*/log/` so the issue tracker is tracked. The moment you
fast-forward, all 60 tickets appear as untracked or modified — that is expected, and it is what Q2 is
about. Do not let it surprise you into a `git clean`.

---

## Your checkout — verified clean

Re-checked at the end of the run, after all five merges:

```
$ git -C /Users/adammedford/Projects/spa-platform rev-parse HEAD
8323750e54ed65b94ff3e4c321e3f3573d7f852f          # as expected; lags the base by design

$ git -C /Users/adammedford/Projects/spa-platform status --porcelain
 M .gitignore
 M contracts/changes/1.5-teardown-role-and-ignored-tfvars.md
 M contracts/s3-layout.md
 M docs/adr/README.md
?? docs/adr/0014-teardown-role-in-bootstrap.md
```

**`HEAD` is exactly `8323750e54ed65b94ff3e4c321e3f3573d7f852f`, and the five paths are exactly the
five from SETUP step 1 — no sixth path, and none of the five changed.** `8323750` lagging the base is
by design and correct, not something to fix. Nothing was added, removed or changed in this checkout by
any part of the run: every lane worked in its own worktree under
`/Users/adammedford/.worktrees/overnight-2026-10-15/`. Tickets 57 and 06 reason about those five paths
from the diff only, and both say so; ticket 58 reads the base version of change request 1.5 and
records that your ADR-0014 is not in the base (Q122).

## Leftovers — listed, not killed

```
$ lsof -nP -iTCP -sTCP:LISTEN | grep -E 'dotnet|Gateway|MockApi'
(no matches)

$ pgrep -fl /Users/adammedford/.worktrees/overnight-2026-10-15
(no matches)
```

**Nothing is listening and nothing is running.** The harnesses' in-process listeners and the gates'
smoke-test ports (5690 for 6.2a, 5700 for 6.3a) all closed with their runs — 6.3a's suite alone starts
37 harnesses per run, each with an identity provider, two mock API listeners and an echo upstream, and
every one of them is gone.

Worktrees still on disk, for step 5:

```
/Users/adammedford/.worktrees/overnight-2026-10-15/overnight          7efaeff [overnight/2026-10-15]
/Users/adammedford/.worktrees/overnight-2026-10-15/ws1-infra          3ae9531 [task/1.4a-cell-ecs-terraform]
/Users/adammedford/.worktrees/overnight-2026-10-15/ws2-hosting        950bb8d [task/2.3b-...-1015]
/Users/adammedford/.worktrees/overnight-2026-10-15/ws4-proxy          4267e3b [task/4.3b-...-2026-10-15]
/Users/adammedford/.worktrees/overnight-2026-10-15/ws6-verification   7e07b20 [task/6.3a-negative-rows-no-session]
/Users/adammedford/.worktrees/overnight/overnight                     2030ab9 [overnight/2026-10-14]
/Users/adammedford/.worktrees/overnight/ws2-hosting                   f34560f [task/2.3b-...]
/Users/adammedford/.worktrees/overnight/ws4-proxy                     b802f1c [task/4.3b-...]
/Users/adammedford/.worktrees/overnight/ws6-verification              3cb9367 [task/6.4a-...]
```

`task/6.2a-functional-rows-no-session` (`a150ed9`) has no worktree — its lane finished and the
worktree was reused by 6.3a.

## One small inconsistency, for the record

The attribution trailer is spelled two ways on the branch you are about to push: the five leaf and
review commits say `Co-Authored-By: Claude Opus 5`, matching each session's own attribution
instruction and the model actually running, while all five merge commits say `Claude Opus 5.5`, the
run's house rule. Two lanes flagged it themselves and offered to have it normalised. Harmless, and
Q143 asks whether you want one spelling — the recommendation is to leave the history and fix the house
rule instead.
