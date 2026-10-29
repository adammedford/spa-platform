# Architecture decisions

One file per decision, numbered, in the format every file here follows: status, date, deciders,
plan ref, then Context, Decision, Alternatives rejected, Consequences, Evidence.

A draft carries no number until it is committed. It is titled, and cited, as `ADR draft: <title>`,
and the session that commits it takes the next free number at that moment. A scripted session
drafts an ADR and never commits one (ADR-0021 decision 4), so a number chosen while drafting is
only the one free then, and another ADR can take it before the draft lands (#116). When two
branches take the same number, the one that lands second renumbers its ADR before it merges.

ADR-0001 to ADR-0012 restate decisions D1 to D12 from the Plan tab, which is exported to
`docs/plan/01-plan.md`. The plan is a **frozen export** whose source of truth is the Claude Doc, so
it is never edited in place. These files are the **live record** of the decisions that meet the
threshold in "Where a decision lives" below. An amendment to one is a new ADR, and no decision is
an edit to `docs/plan/`.

Owner: integrator (`docs/plan/01-plan.md` → Workstreams).

| ADR | Decision | Plan |
| --- | --- | --- |
| [0001](0001-host-resolved-scheme-provider.md) | Resolve per-app schemes by host through a registry-backed scheme provider | D1 |
| [0002](0002-registry-as-versioned-s3-document.md) | Keep the registry as one versioned JSON document in S3 | D2 |
| [0003](0003-client-secrets-in-parameter-store.md) | Reference client secrets in SSM Parameter Store by name and version | D3 |
| [0004](0004-server-side-sessions-via-ticketstore.md) | Keep sessions in the cookie handler with `ITicketStore` and `SaveTokens` | D4 |
| [0005](0005-token-management-behind-iaccesstokensource.md) | Use Duende.AccessTokenManagement behind our own `IAccessTokenSource` | D5 |
| [0006](0006-yarp-routes-generated-from-registry.md) | Generate YARP routes from the registry | D6 |
| [0007](0007-host-prefix-and-fetch-metadata-over-samesite.md) | Defend across apps with `__Host-` cookies, Fetch Metadata and antiforgery, not `SameSite` | D7 |
| [0008](0008-session-required-before-any-asset.md) | Require a session before serving any asset | D8 |
| [0009](0009-immutable-release-prefixes.md) | Publish releases as immutable prefixes named by the registry | D9 |
| [0010](0010-wildcard-ingress-with-no-listener-rules.md) | Serve every host from one wildcard listener with no listener rules | D10 |
| [0011](0011-idp-differences-behind-iidpprofile.md) | Put identity-provider differences behind `IIdpProfile`, chosen per issuer | D11 |
| [0012](0012-cell-as-deployment-unit.md) | Make the cell the deployment unit | D12 |
| [0013](0013-platform-tag-and-bootstrap-exemption.md) | Name the platform tag `Platform = spa-platform`, and exempt the bootstrap root | — |
| [0014](0014-teardown-role-in-bootstrap.md) | Create the teardown role in `infra/bootstrap`, and derive its name from the cell | — |
| [0015](0015-shared-constants-across-a-workstream-boundary.md) | Keep a contract-fixed shared constant in `Platform.Abstractions`, and a dependency-measured one where it is measured | — |
| [0016](0016-plan-policy-without-aws-credentials.md) | Assert plan policy with mock providers, and keep AWS credentials out of CI | — |
| [0017](0017-g0-findings-fold-into-the-identity-contracts.md) | Fold Gate G0's Cognito findings in: adopt the reserve refresh design, and put the client credential style on the profile | — |
| [0018](0018-telemetry-is-the-outermost-stage.md) | Put `UsePlatformTelemetry` first, so every rejection the application answers is logged and counted under its rule id; name stages, never number them, outside the pipeline table | — |
| [0019](0019-registry-documents-a-build-accepts.md) | A build accepts the registry `schemaVersion` its schema names; refusing a value no build could apply is not a new version | — |
| [0020](0020-tickets-live-in-github-issues.md) | Tickets and specs live in GitHub Issues, worked through `gh`; the contracts, ADRs, glossary and merge bodies stay in the repository, and the markdown tickets filed before it are a read-only archive | — |
| [0021](0021-pre-accepted-answers-are-provisional.md) | A pre-accepted answer is provisional: a scripted session commits only to confirmed answers, to the documents a session steers by, and the human lands | — |
| [0022](0022-review-at-the-merge-path.md) | Hold review at `tools/land.sh`: merge only a head whose latest review record says merge, pinned to it, never `--admin`; no gate tag over an open review; required approving reviews rejected while one person holds the only account | — |
| [0023](0023-lane-code-may-rest-on-a-pre-accepted-answer.md) | A lane's code may rest on a pre-accepted answer; a steering document changes only on a cited authority, which the steering check requires and the reviewer weighs | — |
| [0024](0024-session-gate-in-process-and-refresh-lease-across-tasks.md) | One refresh per session across tasks: the in-process session gate stays, and a refresh lease, a contract type independent of the session record, is taken before the provider is called | — |
| [0025](0025-one-key-ring-per-cell-registered-by-hosting.md) | One data-protection key ring per cell, named `spa-platform-{cell}` and kept in Parameter Store under a per-cell prefix, registered by hosting; nothing registered when the section is absent | — |
| [0026](0026-track-the-workflow-scripts.md) | The workflow scripts under `.claude/workflows/` are tracked, as integrator code and not a steering document; every edit lands before the launch it is for, and CI parses each script the way the Workflow tool reads it | — |
| [0027](0027-track-the-project-skills-and-the-test-companion.md) | The overnight script's test companion, its fixtures and the project skills under `.claude/skills/` are tracked under the workflow scripts' rule; CI runs the companion on every push, a project skill is integrator code whose edits land before the sitting that runs them, and the user-level skills stay outside the repository | — |
| [0028](0028-back-channel-logout-outside-bff-by-subject.md) | Back-channel logout at `/signout-backchannel-oidc`, outside `/bff`, ends a subject's sessions on one app through a session index that removes by subject and lands with its endpoint at G7; a `sid`-only token is refused and cross-task `jti` replay is accepted | — |
| [0029](0029-prove-teardown-through-every-reader-of-the-platform-tag.md) | Prove teardown through every reader of the platform tag: IAM's own API for roles and customer managed policies, the cell's roles by name, and the tag query in the selected Region with INACTIVE ECS records and unknown EC2 ids dropped; amends ADR-0013's premise, not its decision | — |
| [0030](0030-workflow-test-companions-run-on-a-workflow-change.md) | The workflow test companions run when a file under `.claude/workflows/` changes, every one of them, in CI's Documents job and in `tools/check.sh --full`; a workflow script with no companion fails both | — |
| [0031](0031-contract-fixed-behaviour-lives-once-in-abstractions.md) | A contract-fixed behaviour every workstream would copy lives once in `Platform.Abstractions` as a public static type (`Rejection`, `Problems`), pinned by `PublicSurfaceTests` and held to the contract's own tables by a test; extends ADR-0015 decision 1 from a value to a behaviour | — |
| [0032](0032-change-requests-are-issues-and-each-decision-has-one-layer.md) | A change request is a `change-request` issue the human decides in a decision comment, which is a confirmed answer, and its own number is never authority; a steering change its ticket does not ask for is proposed as one; `contracts/changes/` is a closed archive; each statement about a decision has one layer, and only a new ADR amends an ADR; amends ADR-0020 decision 2, ADR-0021 decision 4 and ADR-0023 decisions 2 to 4 | — |
| [0033](0033-seven-startup-clocks-against-a-startup-floor-budgeted-at-twice-the-worst-max.md) | Time a gateway cold start by seven startup clocks against a startup floor, budget each clock at 2 × the worst max of its basis (local 4 / 6 / 7 s, the deployed column empty until #178), prove `B-` ids by the startup benchmark's verdict on tier Local (L), and open the tuning effort on a valid breach | — |
| [0034](0034-a-wrong-full-landing-on-main-stands-and-the-guard-sits-before-the-merge.md) | A wrong full landing on main stands: no trunk rebuild, no demotion form and no issue-reading pass in the roadmap derivation, the record corrected in prose; the guard sits before the merge, holding a full-landing subject while an open part ticket declares its row, and the merge of a row's last open part names the row once the carrier has landed it in part and never when the row is already landed whole (#332 D7, as #476 Q2 amended it) | — |

Those three open items closed on 2026-10-16, when Gate G0's items 2 and 3 passed against Cognito.
ADR-0017 records what the run found and amends each of them: ADR-0001's mechanism held, ADR-0005's
reserve refresh design is now **adopted** rather than held in reserve, and ADR-0011's profile gains
a client credential style. No decision carries open gate work at G0 now; the next gate work is
ADR-0011's PingFederate profile at G7.

## Where a decision lives

A decision is written as up to four statements: the rule as it holds now, its why, its provenance
and its authority. Each statement has one layer to live in, and a reader reads the layers in this
order. The plan is a snapshot and holds none of them going forward.

| Layer | For | Read |
| --- | --- | --- |
| A contract, `GLOSSARY.md` or another steering document other than an ADR (the set: `docs/agents/landing-work.md` → "A commit that changes a steering document") | The rule as it holds now | First, always |
| ADR | Why a hard-to-reverse choice was made, and what it rejected | Second, when the governing file cites it or a change would contradict it |
| Merge body | Provenance: which issue and decision moved the file, and the why of a decision with no ADR | When asking why a sentence changed |
| Issue comment (decision, grill record) | Authority and discussion; nothing is built on it directly | Last, for history |
| `docs/plan/` | The 11 October 2026 snapshot; a contract or ADR that differs wins | Background |

A change-request decision gets an ADR when any of these holds:

- (a) it amends or supersedes an ADR;
- (b) it is a change that waits for a gate under `contracts/README.md` → "What may land between
  gates" (changed stage, owner, signature, narrowed schema, rule id, renamed field);
- (c) it is hard to reverse, surprising without context, and the result of a real trade-off.

Otherwise the merge body states the why and cites the decision comment.

An ADR is amended only by a new ADR. The new ADR's Plan ref names the decisions it amends, the
older ADR's Status line gains `**Amended <date> by [ADR-NNNN](<file>):**` and what changes, and the
older body is not edited. The "amended by change request" notes on ADR-0007, ADR-0009, ADR-0014,
ADR-0016 and ADR-0024, and the one in ADR-0014's body, stay as history.
