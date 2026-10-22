# Glossary

The vocabulary of the Enterprise SPA Platform. Use these terms in issue titles, test names, commit
messages and code. Where a term has a tempting synonym, the entry says which word to avoid.

Definitions are drawn from `docs/plan/` and the decisions in `docs/adr/`. The plan is the frozen
export; a term that changes meaning should be changed here and in the ADR that moved it.

## The unit of deployment and the unit of tenancy

**Cell** — the whole stack: ALB, service, bucket, registry, secrets prefix, session store and key
ring. The deployment unit. Every module takes a cell name (`Platform:Cell`) from day one, and the
name is part of every resource name. Every task of a cell shares one key ring, and no two cells
share one. Phase 1 runs one cell. See ADR-0012 and ADR-0025.

**Key ring** — the set of keys under which a cell's gateway protects what it hands to browsers and
stores: session cookies, login state, antiforgery tokens and, from Phase 2, stored session tickets.
Every task of a cell shares one ring and no two cells share one; a value protected under one ring is
unreadable under another. In Phase 1 each task has a ring of its own, the "key durability" gap P2-2
closes. See ADR-0025.
_Avoid_: "keys" alone, which also means client secrets and KMS keys.

**Instance** — one running copy of the gateway with its own live snapshot: in a cell, one ECS task of
the service; in the hermetic tier, one harness host. `/readyz` names the instance that answered, and
a wait for a revision covers every instance a cell runs (change request P2-5).
_Avoid_: "instance" for an EC2 instance or for a copy of a Terraform module; "process", which is
not the unit when two hosts share one test process.

**Platform project** — the AWS project the platform is built in: the one whose account
`tools/preflight.sh` checks against the git-ignored pin. A profile name does not identify it; the
account does. Not "the account", "the real project", "the right project" or "the project made for
this work".

**Sample project** — the project the new AWS experience created beside the platform project.
Nothing of the platform belongs in it. Not "the wrong account", "the other project" or "the
default project".

**App** — one SPA. An app exists only as a registry entry, a release prefix in S3 and an OAuth
client. It has no runtime resource of its own. Not "tenant": tenancy here is the cell, and an app is
a logical boundary inside one.

**appId** — an app's identifier: 3 to 40 lowercase letters, digits or hyphens, unique in the
registry. It names the app's scheme pair, its S3 prefix and its log field.

**Host** — the exact authority the browser sends, lowercase, with the port when one is used. A
request's authority must equal a registry `host` exactly or it gets 404 before routing runs. Not
"domain" and not "hostname" where the port matters.

**Logical boundary** — what an app gets: its host, OAuth client, scheme names, encryption purposes,
session records, S3 prefix, API list and content security policy. Deliberately weaker than
isolation: the process, memory, task role, key ring and registry are shared. **It bounds the
gateway's sessions, not the provider's.** One browser holds one session at the identity provider
across every app, so a second app's login can complete with no password prompt, and signing out of
one app ends the provider's session for all of them. The gateway's own logout stays per app: the
other app's session still answers and still refreshes (ADR-0017, evidence C-2.6, C-2.14, C-2.15).

## The registry

**Registry** — one JSON document at `registry/registry.json`, holding every app's routing, OAuth and
policy configuration. Accepted or rejected whole. See ADR-0002.

**Snapshot** — a validated registry document that is live in the gateway, with its secrets resolved.
The gateway pins one snapshot per request. "The registry" is the document; "the snapshot" is what a
request sees.

**Revision** — the registry's monotonic version (`revision`). Must exceed the live one. A rollback is
the old content published under a new revision, never a decrement.

**Fingerprint** — a per-entry hash of an app's canonical JSON. When it changes, that app's cached
options are evicted.

**Swap** — replacing the live snapshot with a new one, whole. The order is fixed: swap, then evict
cached options.

**Last known good** — the live snapshot that keeps serving when a new document fails validation or
its secrets cannot be read. At a cold start there is none, so affected apps are left out and
reported and the rest go live.

**App definition** — a registry entry without `clientId`, `clientSecretRef` and `release`. Terraform
reads it to create the OAuth client; `spactl app add` completes it from the infrastructure outputs.
So each app is described once.

**Platform configuration** — settings from the task environment (`Policy:*`, `Issuers:*`,
`Registry:*`) that bound what any registry entry may say. A registry writer cannot widen them.

**Release** — an S3 prefix, `apps/{appId}/releases/{release}/`, in which each key is written once. A
release can gain a key, but outside teardown it never changes or loses one. The registry names the
live release, so activation and rollback are registry changes. See ADR-0009.
_Avoid_: "immutable prefix". The prefix can grow; only its keys are fixed.

**Release key** — the key of one object under a release. It is the unit the platform bucket keeps
write-once, and the unit P-04 tests.
_Avoid_: "release" on its own when one object is meant.

## The request path

**Request gate** — the stage that accepts same-origin browser requests only on `/api` and `/bff`,
judged by `Sec-Fetch-Site` and `Origin`, and refuses a cookie name that imitates a platform cookie.
See ADR-0007.

**Shell gate** — the stage that requires a session before any asset is served: an unauthenticated
navigation is challenged, an unauthenticated fetch gets 401. See ADR-0008. Distinct from the request
gate; don't use "gate" alone for either.

**Scheme pair** — an app's two derived scheme names, `oidc:{appId}` and `cookie:{appId}`. The
framework keys protector purposes on the scheme name, so the pair is what makes apps
cryptographically separate.

**Scheme provider** — `RegistrySchemeProvider`, the replacement `IAuthenticationSchemeProvider` that
resolves the app from the request host and returns only that app's scheme pair. See ADR-0001.

**Options invalidator** — the component that evicts cached named options after a swap, for entries
whose fingerprint changed or disappeared.

**Sentinel scheme** — the fixed scheme registered so the token-management library's options hook can
be built outside a request. Every call still passes both `ChallengeScheme` and `SignInScheme`. See
ADR-0005.

**IdP profile** — `IIdpProfile`, the seam holding provider differences (notably Cognito's
non-standard logout). Chosen per issuer in platform configuration, never per app. See ADR-0011.

**Upstream** — an origin behind the gateway that an app's `/api/{name}/*` calls reach, named in the
registry and bounded by `Policy:AllowedUpstreams`. Not "backend" and not "service".

**API name** — the `{name}` segment in `/api/{name}/*`, 1 to 32 lowercase letters, digits or hyphens,
unique within an app.

**Resource binding** — passing `resource` so the provider sets the access token's `aud` to the API's
identifier. One audience per app in Phase 1.

**Client credential style** — how the gateway, as the confidential client, authenticates to the
provider's backchannel endpoints: a form-body secret, a Basic header, or a client assertion. One per
IdP profile, governing every backchannel call. See ADR-0017 and change request P2-6.
_Avoid_: "auth method", "token endpoint auth".

**Client assertion** — the signed JWT the gateway presents in place of a client secret under
`private_key_jwt`, signed with the cell's key for that issuer.
_Avoid_: "client JWT", "key assertion".

**Pushed authorization** — the login's authorize parameters sent to the provider over the backchannel
before the browser is redirected, so the browser carries a reference and not the parameters.
_Avoid_: "PAR" alone in prose.

**Login-state cookies** — the correlation and nonce cookies a login leaves until its callback,
`__Host-spa-corr.*` and `__Host-spa-nonce.*`, minutes long and deleted by the callback. See ADR-0007.
_Avoid_: "OIDC cookies", "auth cookies".

**Stage** — one entry point in the middleware table of `contracts/pipeline.md`, in the position that
table gives it. The number is the position and nothing more: it is normative only in that table, and
it moved at G1b when ADR-0018 put telemetry first. Everywhere else a stage is named by its entry
point, `UsePlatformRequestGate`, never "stage 6" (`CODING_STANDARDS.md` → A stage is named, not
numbered).

**Rejection** — a response a stage ends the request with **under a rule id**: a 400, 403, 404, 405 or
a 401 that `contracts/request-gate.md` has an id for. The rule id is what makes it one, not the
status: a 404 for an asset that is simply absent carries no id and is an answer, not a rejection.
Every rejection reaches the request line under its rule id and is counted under that id in
`security.rejections`. What Kestrel refuses before any stage runs, the 431 for oversized headers, is
the server's answer and not a rejection. The code's log messages say "refused"; the contracts and
the metric say "rejection", and that is the word.

**Rule id** — the identifier of the rule a rejection fired under, from the table in
`contracts/request-gate.md` → Rule ids, held once as `Platform.Abstractions.RuleIds` (ADR-0015). It
is the `rule` field of the request line and the tag of `security.rejections`; the `code` in a
problem body is deliberately less specific.

**Request line** — the one JSON log line `UsePlatformTelemetry` writes per request, after the rest of
the pipeline has returned, with exactly the fields the "Log fields" row of `contracts/telemetry.md`
names. It carries no value forwarded headers settle, which is what lets the first stage write it.

## Sessions

**Session record** — the serialized ticket and saved tokens held server-side behind `ITicketStore`,
bound to app and client. The browser cookie carries a session id and nothing else. See ADR-0004.

**Session key** — the identifier in the cookie's session-id claim. Every sign-in creates a new one,
because the session cookie is not read on the callback path.

**session_ref** — the first 12 hex characters of a SHA-256 of the session key, used in logs. It
correlates a session's requests without being usable as a credential. Log this, never the session
key.

**Session gate** — the in-process, per-session-key serialisation of refreshes: one holder reads,
decides, refreshes and writes, and the callers queued behind it take its outcome. In process only;
two tasks share no gate. See ADR-0017 and ADR-0024.
_Avoid_: "lock", and "lease" for this.

**Refresh lease** — the cross-task claim on one session's refresh, held by one task for a bounded
time and given back when its refresh is written. The **holder** is the task that took it; a
**loser** is a task that could not, and re-reads the record instead. See ADR-0024.
_Avoid_: "lease" alone where the session gate could be meant, and "distributed lock".

## Work, gates and evidence

**Workstream** — one of the six parallel streams of work (1 AWS and IaC, 2 Hosting and registry,
3 OAuth and sessions, 4 API and security, 5 Fixtures, 6 Verification and operations), each with its
own paths in `OWNERSHIP.md`. Not "team", and not "lane".

**Integrator** — the owner of the shared skeleton: contracts, abstractions, test doubles, the
composition root, `docs/adr/`, `docs/plan/`, CI and package versions. A role that agent sessions
play, not a person.

**Agent session** — one agent conversation working in the repository, and the unit in which review
independence is counted. A sub-agent belongs to the session that dispatched it. One workstream, or
the integrator, may be many agent sessions. _Avoid_: bare "session" wherever the gateway's sessions
(session record, session key) could be meant.

**Claude Code process** — one launch of Claude Code, from a terminal or an editor, with every agent
session it hosts: the foreground conversation, its subagents and the agents a workflow starts. It
reads the checkout's settings when it starts, so a change to them reaches only a process launched
afterwards. _Avoid_: "fresh session" or "new session" for this; a new agent session can run in an
old process.

**Change request** — a file under `contracts/changes/<task-id>-<slug>.md`. The only way a workstream
asks for a change to an integrator path. Anyone may add one; the integrator decides and edits.

**Ticket** — one unit of work with its acceptance criteria, held as an issue on this repository and
cited as `#NNN`, and closed when its work lands. The tickets of one feature are sub-issues of its
spec.

**Spec** — the statement of a feature's problem, solution and acceptance that its tickets are cut
from, held the same way and closed when the feature lands. Not "the plan": `docs/plan/` is the
frozen export, and a spec is one feature's working statement.

**Standing answer** — a launch-time instruction that a night's lanes and grills take each
recommendation as decided instead of stopping for the human. Given per run, and recorded in that
run's `Overnight <date>` issue. Not "approval".

**Workflow script** — a script the Workflow tool runs, under `.claude/workflows/` (the overnight run
is one), which launches scripted sessions and tells each what to do. It is tracked and changes by a
branch, a review and a landing like any other integrator change (ADR-0026). It is not a CI workflow
under `.github/workflows/`. _Avoid_: bare "workflow" where CI's could be meant.

**Lane** — one owner's tickets in one overnight run, worked in that owner's worktree, each on a
branch of its own. The integrator has a lane and no workstream. _Avoid_: "lane" for a workstream.

**Foreground session** — an agent session the human starts and watches, at the workstation, for
work only the human can steer or approve as it happens. _Avoid_: "interactive session", and
"foreground" for where a file lives.

**Scripted session** — an agent session a workflow script launches: a lane's implementer, a
reviewer, a griller, the report writer. It drafts and never commits an ADR or a frozen contract's
sentence (ADR-0021 decision 4). _Avoid_: "background agent", "subagent", for this.

**Pre-accepted answer** — an answer written under a standing answer: the recommendation, taken on
the night. Provisional until confirmed: a lane's code may rest on it until the human lands, but no
steering document, ticket body or new ticket does. Not "accepted".

**Confirmed answer** — an answer the human wrote, or a pre-accepted answer the human confirmed on
the run's `Overnight <date>` issue. The only answer a session applies to a steering document, a
ticket body or a new ticket.

**Steering document** — a document a session steers by: `GLOSSARY.md`, `AGENTS.md`, `CLAUDE.md`,
`OWNERSHIP.md`, `CODING_STANDARDS.md`, the agent guides in `docs/agents/`, the ADRs and `contracts/`.
A change to one cites its authority: an acceptance criterion of the ticket it serves, where the
human wrote or approved that ticket, or a confirmed answer. Adding a change request is not a change
to one.

**Main checkout** — the clone's own working tree: git's main worktree, whose git directory is the
common directory. Concurrent sessions share it, so a session's branch work happens in a linked
worktree of its own (`docs/agents/landing-work.md`, "Take a task"); the one change a session makes
there is a fast-forward of a clean `main` to what has landed, `git merge --ff-only origin/main`
(#200).

**Review** — a two-axis `/code-review` (Standards and Spec) of one pull request head, by an agent
session that wrote none of the commits it covers. Not a GitHub approval, which one account cannot
give its own pull request. _Avoid_: approval, sign-off.

**Review record** — the line a review leaves on its pull request, naming the exact head it read and
its verdict: `Reviewed <40-hex head> against <40-hex base>: merge`, or `: hold`, on a line of its
own in a comment or a review submitted as a comment (`docs/agents/review.md` → "The review record").
It covers that head and nothing pushed after it, and the latest record naming the current head
decides. Written only by an agent session that wrote none of the commits it covers. _Avoid_:
review-evidence line, review disposition, approval.

**Open review** — the state of a pull request whose head no review record yet clears: a review
running or holding, commits pushed after the last reviewed head, or a merge made without a review.
It closes when a record names that head with a merge verdict and the findings it raised have landed.

**Hand-over** — the morning's closing statement, on the `Overnight <date>` issue and the console:
what the human runs next (the landing command, the issues to close, the worktrees to remove) and
every refusal with its reason.

**Landed** — the state of a plan row whose merge subject names it, in the form
`docs/agents/landing-work.md` fixes. A row is **part-landed** when the subject says "part of", or
when a split of the row merged. Landed is not done: a row is done when its tests pass in CI, it
touched only its owner's paths and it leaves no change request open. _Avoid_: "merged" for landed,
and "landed" for done.

**Gate** — a named checkpoint with an exit test: G1a skeleton frozen, G0 mechanism confirmed, G1b
identity contracts frozen, G2 local vertical slice, G3 deployed slice, G4 negative suite green, G5
Phase 1 accepted. Phase 2 adds G6 to G8.

**Gate tag** — the annotated tag that closes a gate, its message naming the CI run that proved the
exit test. It is permanent, so what its commit says stays said there. Other tags are not gates.
_Avoid_: release tag, and "the tag" where another tag could be meant.

**Check** — a mechanical guard the pre-push hook, CI or `tools/land.sh` runs when applicable:
ownership, steering, the roadmap, the build, Terraform's format and validate, the review record at
the merge. A check holds a push or a merge to the rules; a gate is the ladder's checkpoint. Never
"gate" for a check, including the one a workflow runs before it integrates a branch.

**Local check** — `tools/check.sh`: the checks a branch must pass on this machine, in one file that
the pre-push hook, a person before a push and every overnight lane run as written. Its full mode,
`--full`, adds the test suite, the smoke test and `terraform test`, which the hook leaves to CI, and
ends on a one-line verdict. _Avoid_: gate, and "the hook" for the checks themselves.

**Terraform floor** — the oldest Terraform the repository supports, named in `.terraform-version`.
CI installs exactly it, every root's `required_version` asks for at least it, and the local check
runs it when the machine has it, naming the version it ran either way. Raising it is a one-line
change to that file plus each root's constraint. _Avoid_: the pinned version, the minimum, for this.

**Test companion** — the tests that sit beside a check or a script, in a file named for it, and
prove what it does by running the real thing on throwaway inputs built to trip each rule; never a
copy of its parts. _Avoid_: harness, suite, for these.

**Harness** — one of two things, and never a script's tests. Either the gateway test harness, task
6.1's in-process host of the composed gateway that the hermetic suites drive, or a G0 feasibility
harness under `spikes/g0/`, which probes one mechanism and records its evidence ids. Say which when
the sentence does not. _Avoid_: harness, for a test companion.

**Hermetic (H)** — the test tier that runs in process on every change, against the test identity
provider, an in-memory object store, the real mock API and an echo upstream.

**Deployed (D)** — the test tier that runs through the tunnel at G3 and after, against the ALB, the
task, S3, Parameter Store and Cognito, with a real browser plus a raw HTTP client.

**Test ids** — `F-` functional, `N-` negative security, `P-` infrastructure and architecture
properties, `A-` architecture reference rules. A test id in a task names a behaviour: the owning
workstream proves it in its own project, and workstream 6 proves it again in the composed suites.

**Evidence ids** — `S-` the stand-in spike run, `R-` the real-handler run, `T-` the token-and-proxy
harness (G0 items 4 and 5), `C-` the Cognito run (G0 items 2 and 3). `S-`, `R-` and `T-` are listed in `docs/plan/07-evidence.md`; the `C-` ids are the Cognito report's own, in `spikes/g0/cognito/report-cognito.md`. Separate from test ids.

**Stand-in run** — the first feasibility run, using a stand-in for `OpenIdConnectHandler` because the
sandbox could not reach NuGet. 34 of 34 checks passed.

**Real-handler run** — the run against `OpenIdConnectHandler` compiled from unmodified `release/10.0`
source, then repeated on the shipped package with the overrides on. 32 of 32 expectations held.

**Reserve design** — the gateway-owned locking over the library's refresh call, proven in the harness
and adopted for task 3.8 after the Cognito inside-window successor failure (C-2.17).
The late-replay control C-2.18 is a separate observation. See ADR-0005 and ADR-0017.
