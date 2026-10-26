# Change-request readers, rules the agent docs state twice, and the steering ceremony

Research for #407, a question on the map #406. Every entry below comes from reading the source at
origin/main `b5cc48c2644123dcde03191a9895794b93f82938`, on 2026-10-25/26. Nothing was executed.
Line numbers are for that sha. Part 1 and part 3 were read here directly. Part 2 was read by a
sub-agent over all eleven files in scope, and D2, D5 and D7 were then re-checked against the source.

`contracts/changes/` holds 32 requests (plus `.gitkeep`). Six are open: `P2-5-continuity-rows-in-the-register.md`,
`issue-148-captured-line-event-id.md`, `issue-381-object-read-refusal.md`,
`mr-2-shut-down-sequence-in-the-pipeline-contract.md`, `mr-4-upstream-bounds-named-in-the-contract.md` and
`mr-5-dependency-down-catalogue.md`. The other 26 say `decided`. This matches the map's count.
`P2-5-continuity-rows-in-the-register.md` and `p2-5-readiness-reports-registry-revision.md` differ only in
case, so on a case-insensitive file system both of their names start `p2-5-`.

## Summary

- **About 260 places use the directory or a Status line.** They fall into four groups:
  - 15 mechanisms: code or workflows that read, write, check or exempt it;
  - 22 places where a document states the change-request rule;
  - about 30 citations in ADRs, contracts and docs;
  - 206 comment or message lines in 115 code, infra and test files that cite a request by its id or
    path.
- **Six readers break when requests become issues.**
  - `.github/steering.py`, with 18 tests in `.github/steering_tests.py`;
  - `.github/path-ownership.py`;
  - `.claude/workflows/gate-evidence.js`;
  - `.claude/workflows/overnight.js`;
  - `.claude/workflows/morning.js`;
  - `.claude/workflows/truth-audit.js`.

  The citations keep working if the 26 decided files stay as an archive. Only one of them is
  asserted by a test: `tests/Contracts.Tests/ContractIndexTests.cs:44`.
- **Two readers already disagree about what "open" means.**
  - `steering.py:354-360` treats every state except `open` as a decision.
  - `gate-evidence.js:371` treats every state except `decided`, `rejected` or `withdrawn` as open.

  So a Status of `accepted` or `proposed` counts as decided at the steering check and as open at the
  gate.
- **`contracts/README.md:10-13` keeps the decision beside the request.** That rule conflicts with the
  map's settled point that the decision lands in the contract or ADR it changes.
- **The 11 agent docs state 58 rules more than once.** Fifteen of them disagree or have drifted
  apart. Five of the fifteen involve change requests or the steering ceremony:
  - D7, the route;
  - D8, which files are steering documents;
  - D5 and D10, "done" versus "landed";
  - D11, the human's account.

---

## 1. Every reader of `contracts/changes/` and of a request's Status line

### 1a. Mechanisms: code that reads, writes, checks or exempts it

| Site | What it does | What it relies on |
| --- | --- | --- |
| `.github/steering.py:83`, `:346-352` (`CHANGE_REQUESTS`), `:975-990` (`steering_changes`) | A commit whose only steering change is *adding* a file under `contracts/changes/` needs no `Authority:` trailer. Editing or removing a request is a steering change. | Requests are files under that prefix, and an added file has git status `A` (no rename detection). It cites ADR-0023 decision 2. |
| `.github/steering.py:354-375` (`STATUS`, `STATUS_LABEL`, `UNREADABLE`) | Reads a request's state from `- **Status:** <word>`. Any other label shape is treated as unreadable. | The Status line's exact markdown shape, with the first word as the state. Any word except `open` is a decision (`:356`). Two Status lines naming different states are unreadable (#188's review). |
| `.github/steering.py:377-384` (`DECIDED_SINCE`), `:993-1028` (`state_at`, `request_changes`), `:1031-1067` (`held_commits`) | Holds a commit that adds or edits a request to a non-open state unless its trailer is a confirmed-answer URL. A `#n` is not enough. | `git show <rev>:<path>` of the request at the commit and at its parent. It cites ADR-0021 decision 4 and the commits 9f56729 and 37af894 (the night of 2026-10-19, #187). |
| `.github/steering.py:1070-1077`, `:1156-1165`, `:1269` | Reports a Status it cannot read. | As above. |
| `.github/steering.py:1294-1297`, `:1364`, `:1368-1383` | Failure messages telling the reader to "correct that side through a change request under contracts/changes/", that "Adding an open change request ... needs none", and how to write a Status line. | The file route and the Status line. |
| `.github/steering_tests.py:88-92` (`OPEN_REQUEST`, `DECIDED_REQUEST` fixtures), `:1760-2030` (18 tests, from `test_a_change_request_added_passes_without_a_trailer` to `test_an_open_status_in_another_shape_is_held_and_told_the_shape_to_write`) | Pins every rule above. | The file shape, Status shapes and git statuses. |
| `.github/path-ownership.py:29-32` (docstring), `:47-49` (`ALWAYS_ALLOWED = ("contracts/changes/",)`) | Any owner may write under the prefix, beside its own paths. | Anyone files a request as a file, so the ownership check exempts the directory. |
| `.github/path_ownership_tests.py:277-280` | `test_a_change_request_is_allowed_beside_another_owner`. | As above. |
| `.claude/workflows/gate-evidence.js:148` (schema `changeRequests: {file, rows, status}`), `:288-298` (`rowDone`), `:371` (scope step 6), `:585` | A plan row is not done while a request naming it is open. Step 6 lists "each contracts/changes/*.md at base whose **Status:** line does not begin decided, rejected or withdrawn, with the plan rows it names". | The Status line's first word and the request naming plan rows in its prose. Its open/decided rule is not steering.py's (see Summary). |
| `.claude/workflows/gate-evidence_tests.mjs:9`, `:111`, `:157`, `:430` | Case "a row with an open change request" (`status: '**Status:** proposed'`). | The same. |
| `.claude/workflows/overnight.js:297` (collect) | "An open change request (contracts/changes/*.md with Status open) is not a ticket of its own tonight: name it in "reason" and in the "why" of every ticket it holds up." | The collect agent reads the directory for the Status line. |
| `.claude/workflows/overnight.js:348` (implement) | A lane that needs an integrator path writes `contracts/changes/${t.id}-<slug>.md` "in the shape of the existing ones (Status: open)", builds what it can, and lands partial. | The lane *writes* the file on its own branch, and the exemption in path-ownership/steering lets it through. |
| `.claude/workflows/overnight.js:107`, `:350`, `:382` | Restate the route. Implement (`:350`) and review (`:382`) except `contracts/changes/` from the steering-document trailer audit. | The exemption from ADR-0023 decision 2. |
| `.claude/workflows/morning.js:825` | Classes "a change request's status line" as an `integrator-edit` the morning may commit on a confirmed answer (#78 grill Q8). | ADR-0021 decision 4. The decision is written as an edit to the file. |
| `.claude/workflows/truth-audit.js:64-70` (corpus excludes `contracts/changes/` as "records"), `:81`, `:160-163` (`changeRequest` field), `:277-284` (`fixKindOf` → `'change-request'`), `:300`, `:537` | A frozen contract whose correction changes a listed name "waits for a change request under contracts/changes/, written by the owner beside the decision; the audit writes none." | That the archive is records (not extracted from), and that a correction is routed as a file. |
| `.claude/workflows/truth-audit_tests.mjs:57`, `:69`, `:117`, `:188`, `:346`, `:493-495` | Pins the corpus exclusion and the `change request` fix kind. | `contracts/changes/6.4-request-telemetry-feature.md` exists at that path. |
| `.claude/workflows/review-queue.js:494` | Names "change requests and ADRs" among the review's spec sources. | `docs/agents/review.md:44`'s table. |

Places that do **not** read the directory: `tools/land.sh`, `tools/check.sh`, `.githooks/pre-push`
(which reaches it only through steering.py and path-ownership.py), `tools/roadmap.py` (`:294` is only a comment
about a subject naming "change request 1.6a"), `tools/landing_subjects.py`, `CLAUDE.md` and
`.claude/skills/wiz/SKILL.md`.
No tool checks the "leaves no change request open" half of *done* (GLOSSARY:331) except
gate-evidence.js.

### 1b. Places that state the change-request rule

| Site | Wording relied on |
| --- | --- |
| `AGENTS.md:27` | "The integrator's paths are off limits ... The one exception is adding a change request." |
| `AGENTS.md:28` | "Add `contracts/changes/<task-id>-<slug>.md` describing the change, and stop. The integrator decides and edits." |
| `AGENTS.md:33` | Done = "... it left no change request open." |
| `OWNERSHIP.md:13-15` | "...wants a change request under `contracts/changes/`. Anyone may add one of those, which is why the block exempts it." |
| `GLOSSARY.md:243-244` | **Change request**: "a file under `contracts/changes/<task-id>-<slug>.md`. The only way a workstream asks for a change to an integrator path." |
| `GLOSSARY.md:300-301` | *Steering document*: "Adding a change request is not a change to one." |
| `GLOSSARY.md:330-331` | *Landed*: "a row is done when ... it leaves no change request open." |
| `docs/agents/landing-work.md:45-50` | The route, plus "except a request added or edited to say its Status is decided, which needs a confirmed answer as its authority (ADR-0021 decision 4). Then merge what you have, say in the merge body that the request is open, and stop the task." |
| `docs/agents/landing-work.md:78-82` | "neither does a commit that only adds an open change request under `contracts/changes/`; editing or removing one does, and a commit that sets a request's Status to decided needs ... the URL of a confirmed answer". |
| `docs/agents/domain.md:41` | "A workstream that needs a change there files `contracts/changes/<task-id>-<slug>.md` and stops." |
| `docs/agents/review.md:44` | Spec-source table: "A decision the task was handed: `contracts/changes/`, `docs/adr/`". |
| `contracts/README.md:3-5` | "After that, a change is a written request to the integrator, never an edit: add `contracts/changes/<task-id>-<slug>.md` ... Anyone may add one. The integrator decides and edits." |
| `contracts/README.md:10-13` | "The decision is still written up **beside the request**, because the first question anyone asks of a frozen file is why it moved." It cites `changes/i-7-...` and `changes/6.4-...` as the two cases. This conflicts with the map's settled point that the decision lands in the contract or ADR. |
| `contracts/README.md:56` | "After the tag, these parts need a change request to alter". |
| `contracts/test-ids.md:17` | "The integrator adds it by deciding a request under `contracts/changes/`". |
| `contracts/test-ids.md:28` | The register's *Recorded by* column names "the ADR, change request or confirmed answer that named the id". |
| `contracts/resource-tags.md:97`, `:107` | Workstream 1 "files a change request" for a gap. After the first apply, a change is "a change request". |
| `docs/adr/0021-pre-accepted-answers-are-provisional.md:38-40` | Decision 4: a scripted session may commit "a change request's status line" on a confirmed answer. |
| `docs/adr/0023-lane-code-may-rest-on-a-pre-accepted-answer.md:39-40` | Decision 2: "Adding a change request is not a change to one." |
| `docs/roadmap.md:46`, `:125`, `:304`, `:313-314`, `:374` | Done includes "no change request open". "Files a change request instead of moving it". Nothing is committed work "until it is an ADR, a row in a fresh export, or a change request under `contracts/changes/`". |
| `tests/Platform.Abstractions.Tests/PublicSurfaceTests.cs:14-18` | Doc comment: "a workstream asks for it with contracts/changes/&lt;task-id&gt;-&lt;slug&gt;.md." |
| `tests/Platform.Proxy.Tests/ProxyTimeoutCeilingTests.cs:75` | Assertion message: "the fix is a change request under contracts/changes/". |
| `tools/overnight_collect.py:136`, `tools/overnight_collect_tests.py:295`, `:329` | An Owner line "integrator (anyone may write a change request)" parses to the integrator. It relies only on the words. |
| `tools/overnight_collect_tests.py:86`, `:90`; `tools/ticket_shape_tests.py:67`, `:71`, `:339`, `:557`; `tools/check_tests.py:806`; `.claude/workflows/fixtures/2026-10-20-tickets.json:381`, `:615`; `.claude/workflows/morning_tests.mjs:153`, `:170` | Fixture ticket bodies and merge subjects that name the route. They are frozen copies, so their meaning does not change, but a new fixture would word the route differently. |

### 1c. Citations of a particular request in ADRs, contracts and docs

These rely only on the archived file still existing at its path. If the 26 decided files stay in
place, they keep resolving.

- ADRs:
  - `0005:40`; `0007:3-4`, `:31`; `0009:3`; `0011:35`; `0013:10-11`, `:22`, `:47`, `:61`, `:63`;
  - `0014:3`, `:9-10` (a markdown link), `:60`, `:107`, `:119`, `:126-127`, `:136`; `0015:9`; `0016:4`;
  - `0017:159`; `0018:7`, `:14`, `:81`, `:89`; `0024:3`, `:9-10`, `:74`; `0025:8`, `:57`, `:67`;
  - `0028:7-8`, `:82`, `:89`; `0029:9-10`, `:71`.
  - ADR-0014, 0016, 0024 and 0009 carry "Amended ... by change request X" in their **Status** line.
- Contracts:
  - `README.md:42-43`, `:75`, `:124`, `:142`; `resource-tags.md:5-6`; `s3-layout.md:9`;
  - `pipeline.md:78`; `http-contract.md:36`; `test-ids.md:81-82` (the *Recorded by* cells name
    `changes/p2-6-...` item 7).
- Docs: `GLOSSARY.md:25`, `:142`; `docs/dependencies.md:63`, `:141`, `:169-170`.
- CI and git:
  - `.github/workflows/ci.yml:227`, `:409`, `:418`, `:425`, `:430`, `:435`, `:440`, `:447`, `:454`,
    `:510`, `:516`, `:522`, `:528`, `:534`, `:540`, `:547`, `:554` (each step's comment names the
    request that added it);
  - `.gitignore:19`, `:26`, `:33`, `:50`.
- **The one test that asserts a citation:**
  - `tests/Contracts.Tests/ContractIndexTests.cs:38-45` asserts that `contracts/resource-tags.md`
    contains `1.6a-platform-tag-convention.md` and `ADR-0013`.
  - It fails if that archive file is renamed or the citation is reworded to an issue number.

### 1d. Comment citations in code, infra and tests

There are 206 lines in 115 files, listed in the appendix. Almost all of them are in workstream paths,
not the integrator's. They name a request by id ("change request 1.7") or by path. They rely on the
archive alone, so with the archive kept none needs editing.

---

## 2. Rules the agent docs state more than once

Scope: `AGENTS.md`, `CLAUDE.md`, `OWNERSHIP.md`, `CODING_STANDARDS.md`, `GLOSSARY.md` and
`docs/agents/{aws-rules,domain,issue-tracker,landing-work,review,triage-labels}.md`.

**Count:** 58 duplicated rules, 15 of which disagree or drift (D1 to D15) and 43 of which agree (A1 to A43).

The following rules are stated only once, so they are not listed:
- no custom crypto (AGENTS:81);
- xUnit and test-project naming (AGENTS:68);
- warnings are errors (AGENTS:70);
- the raw socket for the `Host` header (AGENTS:72);
- exit code 8 (AGENTS:67);
- `spikes/` is outside the solution (AGENTS:71);
- entry points (AGENTS:31);
- A-01, A-02 and A-04 (AGENTS:82, 84);
- the contracts-v1 freeze (AGENTS:106).

### Disagree or drift

**D1. How a gate or a task is known to be met.**
- AGENTS:91 "Before choosing work, run `git fetch origin --tags` successfully and read origin/main."
- AGENTS:95 "`git tag --merged origin/main` ... an unrelated local tag is not a trunk gate."
- AGENTS:97 "`git log --oneline origin/main`".
- domain.md:55 "the annotated gate tags reachable from origin/main".
- landing-work:9 "a gate is met when `git tag` shows it, another task is met when `git log --oneline` shows a merge naming its id."
- *Drift:* landing-work uses a bare local `git tag` and `git log`, with no fetch and no origin/main.
- *Home:* AGENTS, "Where the work stands".

**D2. Who runs `tools/roadmap.py`, and when.**
- AGENTS:103 "after landing a row, run that script in the landing checkout."
- landing-work:96 "Run the script before the push and commit what it writes".
- landing-work:102 "The script merges, derives the roadmap and pushes the refresh as its own commit."
- landing-work:204: the only push to main without a pull request is land.sh's refresh, or the one made after a gate tag.
- *Disagree:* AGENTS says to run it by hand after landing. landing-work says to run it before the push, and also says land.sh does it.
- *Home:* landing-work, "Land it".

**D3. A claim, and what happens to it when part of the work lands.**
- landing-work:12 "**The branch is the claim.**"
- landing-work:179 "A part-landed row keeps its claim".
- issue-tracker:45 and triage-labels:24: the claim is the assignee.
- triage-labels:31 "The assignee stays until that part lands and is then removed, so the rest can be claimed".
- *Disagree:* a plan row and an issue have opposite rules for part-landed work. A row that is also a ticket gets both.
- *Home:* triage-labels, "Lifecycle".

**D4. Blocked state in the body.**
- triage-labels:20 "never by a label or a line in the body (ADR-0020)".
- issue-tracker:96 "So a body states no status in prose".
- These conflict with issue-tracker:10 (the `Part of #<parent>` fallback), issue-tracker:43 (the "fall back to a `Blocked by: #<n>` line"), issue-tracker:44, and issue-tracker:73 (ticket_shape fails that exact line).
- *Disagree:* the template's fallbacks are the very lines the repository's own additions forbid.
- *Home:* triage-labels, "Lifecycle".

**D5. When a claim branch is deleted.**
- landing-work:128 "deleted when the **task** is done, not when a pull request merges."
- landing-work:171: a `task/*` branch goes "once every id its name carries reads as landed".
- GLOSSARY:330 "Landed is not done".
- *Disagree inside one file:* "done" and "landed" are the two states the glossary keeps apart.
- *Home:* landing-work:163-182.

**D6. The ownership table.**
- AGENTS:45 gives workstream 6 "`tests/Gateway.*`".
- OWNERSHIP:49-52 lists four `tests/Gateway.<X>/` projects. Under OWNERSHIP, any other `tests/Gateway.X` falls to the integrator by `*`.
- AGENTS:39 ("their two test projects", for three projects) and AGENTS:47 (the `tests/<Project>.Tests` rule) both claim `tests/Gateway.Tests`, against the glob at AGENTS:45.
- *Drift.*
- *Home:* OWNERSHIP.md, which `.github/path-ownership.py` reads. AGENTS should point to it.

**D7. The change-request route.**
- AGENTS:27-28 and domain.md:41 say file it "and stop".
- landing-work:47-50 says "Then merge what you have, say in the merge body that the request is open, and stop the task", and narrows the exemption to an *open* request.
- OWNERSHIP:14 and GLOSSARY:300 state the exemption unconditionally ("Adding a change request is not a change to one").
- landing-work:47 says "the check exempts that directory" without naming which check. Two checks exempt it, path-ownership and steering, and only steering applies the decided-Status exception.
- *Drift.*
- *Home:* landing-work, "Build it". GLOSSARY keeps the definition only.

**D8. Which files are steering documents.**
- GLOSSARY:297 says "the agent guides in `docs/agents/`, the ADRs and `contracts/`".
- landing-work:57, ADR-0023:39 and `steering.py:335-344` name the whole directories `docs/agents/` and `docs/adr/`.
- *Drift:* under the code, `docs/adr/README.md` and the tool-rewritten `docs/agents/aws-rules.md` are steering documents. The AWS setup rewrites aws-rules.md with no trailer (CLAUDE:21, aws-rules:5).
- *Home:* GLOSSARY, but `steering.py` is the arbiter.

**D9. Who may review.**
- landing-work:189 and :191 say "any fresh session can review it".
- GLOSSARY:309 and :317 say "an agent session that wrote none of the commits".
- review.md:85 says "a top-level one that launched, dispatched and wrote nothing under review."
- *Drift:* review.md is stricter than the other two.
- *Home:* review.md.

**D10. When an open review closes.**
- GLOSSARY:322 "a record names that head with a merge verdict and the findings it raised have landed".
- landing-work:221 "...landed, fixed or carried as `Outstanding:` lines".
- *Disagree:* under the glossary, a carried finding still holds the gate.
- *Home:* landing-work, "Gates".

**D11. Whose account is "the human's".**
- issue-tracker:83 "**The human's account is `@adammedford`.** A `Confirm` comment ... count[s] only when this account wrote them". CODING_STANDARDS:99 agrees.
- review.md:146 says this is "the one GitHub account ... which every agent session and the human share". landing-work:200 and OWNERSHIP:6 agree.
- *Drift in meaning:* the author check (#316) shuts out outsiders only. Agents post through the same account, so "no agent session writes one" (issue-tracker:95) is held by prose alone. ADR-0021:22-24 and :32-34 say so explicitly ("self-attested ... the guard is against haste, not forgery").
- *Home:* issue-tracker:83, with the shared-account limit stated.

**D12. "Write an ADR."**
- domain.md:40 "**Recording a new or revised decision?** Write an ADR."
- domain.md:41, AGENTS:39 and GLOSSARY:286 (a scripted session "never commits an ADR") all restrict who may.
- *Drift.*
- *Home:* AGENTS ownership, plus GLOSSARY:286.

**D13. Whether a record is ever rewritten.**
- issue-tracker:99 "A comment, a merge body and an archived ticket file are records ... never rewritten."
- landing-work:135 "**A task merge's message is changed by rebuilding the run branch.**"
- *Drift:* there is no carve-out for an unlanded run branch.
- *Home:* issue-tracker, with the exception added.

**D14. How a ticket closes.**
- triage-labels:26 "Closed, after an `## Answer` comment". issue-tracker:46 and :104 agree.
- landing-work:115 and :120 close tickets by `Closes #n` on merge, which writes no `## Answer`.
- GLOSSARY:246 "closed when its work lands", which leaves out answered and declined.
- *Drift.*
- *Home:* issue-tracker:104.

**D15. Where test ids and evidence ids are listed.**
- AGENTS:17 names `04-tests.md` and `contracts/test-ids.md`.
- review.md:42 names `04-tests.md` only.
- AGENTS:19 lists `S-` and `R-`. review.md:43 lists `S-`, `R-` and `T-`. GLOSSARY:409 adds `C-`, which lives in `spikes/g0/cognito/report-cognito.md`.
- *Drift:* each list leaves out something another includes.
- *Home:* GLOSSARY, "Test ids" and "Evidence ids".

### Agree

Each row gives the occurrences, then the natural home.

| # | Rule | Occurrences | Home |
| --- | --- | --- | --- |
| A1 | Write only in your owner's paths; what to do when the check fires | AGENTS:25; landing-work:41-43; OWNERSHIP:13; review.md:27, :95 (review.md:28-31 copies OWNERSHIP's merge-history detail) | OWNERSHIP |
| A2 | A task is done when ... | AGENTS:33; landing-work:184; GLOSSARY:330 (only the wording differs: left/leaves, task/row) | GLOSSARY |
| A3 | A test id names a behaviour | AGENTS:32; GLOSSARY:397 | AGENTS |
| A4 | A-03 | AGENTS:83; landing-work:52 | AGENTS |
| A5 | Packages and projects are the integrator's | AGENTS:29, :69; landing-work:45; GLOSSARY:228 | AGENTS |
| A6 | Never log secrets | AGENTS:80; GLOSSARY:183, :199 (narrower cases) | AGENTS |
| A7 | Only a fast-forward in the main checkout | GLOSSARY:303; landing-work:22; review.md:21 | landing-work |
| A8 | The Authority trailer | GLOSSARY:299; landing-work:57-82; issue-tracker:128 (issue-tracker:85 adds the author check, which landing-work leaves out) | landing-work |
| A9 | Pre-accepted versus confirmed | GLOSSARY:289, :293; issue-tracker:89; landing-work:69 | GLOSSARY |
| A10 | A `Confirm` comment is the human's act | issue-tracker:83, :95; CODING_STANDARDS:100 | issue-tracker:95 |
| A11 | The review record's format and meaning | GLOSSARY:313; review.md:104-121; landing-work:106, :198 | review.md |
| A12 | Merge only through land.sh | landing-work:99, :105-113; review.md:143 | landing-work |
| A13 | The `--unreviewed` escape | landing-work:110; review.md:151 | landing-work |
| A14 | One account, so the check is in the merge script | landing-work:199; review.md:146; GLOSSARY:310 | landing-work |
| A15 | A merged PR's review is cleared by a record naming its merged head | landing-work:222, :276; review.md:152 | landing-work |
| A16 | The spec axis | landing-work:192; review.md:34 | review.md |
| A17 | `Outstanding:` lines | landing-work:127; CODING_STANDARDS:30; review.md:95; triage-labels:30 | landing-work |
| A18 | Report what was checked | CODING_STANDARDS:167; review.md:46, :67 | CODING_STANDARDS |
| A19 | Name the mutation that fails a test | CODING_STANDARDS:52-55; review.md:63-64 | CODING_STANDARDS |
| A20 | Name a stage, do not number it | CODING_STANDARDS:134; GLOSSARY:159 | CODING_STANDARDS |
| A21 | A reuse key is only as fresh as its inputs | CODING_STANDARDS:111; GLOSSARY:265 | CODING_STANDARDS |
| A22 | A universal names its exceptions by the writer | CODING_STANDARDS:145; review.md:69-75 | CODING_STANDARDS |
| A23 | `#NNN`, one number space | issue-tracker:27, :111; GLOSSARY:247 | issue-tracker:111 |
| A24 | `.scratch/` is a read-only archive | CLAUDE:7; issue-tracker:135; triage-labels:39 | issue-tracker |
| A25 | A feature is a spec plus its sub-issues | GLOSSARY:247; issue-tracker:53 | issue-tracker |
| A26 | When a ticket is open to an agent | issue-tracker:44, :57; triage-labels:32 | issue-tracker:57 |
| A27 | State lives in fields, not in the body | triage-labels:19; issue-tracker:96 (but see D4) | triage-labels |
| A28 | Follow-ups do not hold their origin open | triage-labels:34; issue-tracker:107; review.md:98 | issue-tracker |
| A29 | Declining an issue | triage-labels:28; issue-tracker:104 | triage-labels |
| A30 | `docs/plan` is a read-only export | CLAUDE:15; domain.md:31, :40; GLOSSARY:6, :251 | domain.md |
| A31 | `docs/plan` and `docs/adr` are the integrator's | AGENTS:39; domain.md:41; GLOSSARY:228 | OWNERSHIP |
| A32 | `tools/check.sh` is the local check | AGENTS:61; GLOSSARY:347; CODING_STANDARDS:12 | GLOSSARY |
| A33 | The check skips a half the branch did not touch | AGENTS:62; review.md:52 | AGENTS |
| A34 | A gate tag is annotated, permanent and names its CI run | GLOSSARY:338; landing-work:212, :228; AGENTS:95 | landing-work |
| A35 | Merge subject forms; part-landed | landing-work:88-92; GLOSSARY:328; AGENTS:97 | landing-work |
| A36 | AWS rules are not imported | CLAUDE:21; AGENTS:21; aws-rules:5 | CLAUDE |
| A37 | Say "project", not "account" | aws-rules:15; GLOSSARY:31 | GLOSSARY |
| A38 | The `Overnight <date>` issue | issue-tracker:117; GLOSSARY:255, :290, :294 | issue-tracker |
| A39 | A workflow script or project skill changes like any integrator change | GLOSSARY:259, :270 | GLOSSARY |
| A40 | Owners are roles, not people | GLOSSARY:229; OWNERSHIP:5 | OWNERSHIP |
| A41 | Repeated "Avoid" words | GLOSSARY:226/:278 ("lane"), :39/:234 ("session"), :358/:364 ("harness") | GLOSSARY |
| A42 | Triage label strings equal their role names | CLAUDE:11; triage-labels:5-13 (template line :15 invites drift) | triage-labels |
| A43 | The six workstreams | GLOSSARY:224; AGENTS:37-45; OWNERSHIP:20-46 | OWNERSHIP |

These are pointers whose paraphrase changes the meaning:
- review.md:95 says landing-work "requires" an `Outstanding:` line for every carried finding. landing-work:127 asks for one only for an unfinished row.
- GLOSSARY:300 drops the decided-Status exception (see D7).
- landing-work:259-277 ("Review debt ... As read on 18 October 2026") is a dated progress snapshot inside a steering document.

---

## 3. The steering ceremony's rules and the reason each cites

The table numbers the rules S1 to S17.

| # | Rule | Where stated | Reason cited |
| --- | --- | --- | --- |
| S1 | **The steering-document set**: GLOSSARY, AGENTS, CLAUDE, OWNERSHIP, CODING_STANDARDS, `docs/agents/`, `docs/adr/`, `contracts/` | ADR-0023:39-40 (decision 2); `steering.py:331-344`; GLOSSARY:297; landing-work:57 | ADR-0023, Context (`:22-30`): on the night of 2026-10-18 five commits changed such a document on no confirmed answer. |
| S2 | **Every commit changing one carries an `Authority:` trailer** (`#n` for a ticket whose acceptance criterion asks for it, or a confirmed-answer comment URL) | ADR-0023:41-45 (decision 3); `steering.py:74-98`, `:399-409`; landing-work:56-82; GLOSSARY:299; issue-tracker:128-129 | The same night: 7569b1f (#68), b5aa97d (#69), 469ea61 (#67's D1), 2b89124 (#68's review), d606340 (#65's review) (ADR-0023 Evidence, `:77-80`). "The night's reviews held none of them" is why it is a check, not a reviewer standard (`:58-60`). |
| S3 | **Pre-accepted answers, night follow-ups, archived proposals and reviewer findings are not authority** | ADR-0023:42-44; landing-work:69-70; overnight.js:382 | ADR-0021 Context (`:13-24`): 52 unconfirmed pre-accepted answers on 2026-10-17, PR #76's review calling them "accepted", and #66's body rewritten to them. |
| S4 | **The trailer must be in git's trailer group, not in the prose** | `steering.py:86`, `:393-397`; landing-work:71-72 | git-interpret-trailers' parsing rules (the URL is cited at `steering.py:398`). No incident is cited. |
| S5 | **The rule applies only from `AUTHORITY_SINCE`, by committer date** | `steering.py:386-391` | A commit made earlier "could not have known the rule". An amend or rebase keeps the author date. |
| S6 | **A merge needs no trailer** | `steering.py:87-88`; landing-work:78 | Its commits are each held on their own. |
| S7 | **Adding a change request is exempt** | ADR-0023:40; `steering.py:346-351`; GLOSSARY:300; landing-work:78-79 | "Holding it would hold every lane that followed the rules" (`steering.py:346-349`). |
| S8 | **Setting a request's Status to anything but open needs a confirmed-answer URL; a `#n` alone is held** | `steering.py:377-384`, `:1368-1374`; landing-work:47-49, :80-81 | ADR-0021 decision 4, and the commits 9f56729 and 37af894 on the night of 2026-10-19 (#187, the 2026-10-20 retro). |
| S9 | **A Status in an unreadable shape, or two Status lines naming different states, is held** | `steering.py:354-372` | #188's review and "the second #188 review". |
| S10 | **A cited comment counts only if the human's account wrote it** | `steering.py:94-97`, `:422-433`; issue-tracker:83-88; CODING_STANDARDS:97-103 | #316. CODING_STANDARDS: #271's morning script took an outsider's `Confirm Q1` (fixed in 58f5858). |
| S11 | **A number the issue listing lacks is looked up alone before it is refused** | `steering.py:91-93`, `:297-306` | #297: on 2026-10-22 one push's run refused trailers citing #185, #135 and #159, all real issues. |
| S12 | **A lane proposes a needed term or guide sentence under `## Proposed steering changes` instead of committing it** | ADR-0023:46-48 (decision 4); overnight.js:350, :356, :389, :482 | ADR-0023, the same 2026-10-18 night, and #103 ("The overnight prompts route steering changes through proposals"). |
| S13 | **The reviewer takes an unauthorised steering change out of the branch ("held"), quotes it, and lists it under "Held"** | overnight.js:382 (#103) | #103 and ADR-0023's "reviews held none of them". |
| S14 | **Quote each changed paragraph whole, as it will read, with its neighbours when one is inserted or removed; re-quote and re-ask if `main` moved since approval; an approval covers the body as it stood** | issue-tracker:119-131; overnight.js:350, :382 | #187's edits were given as old and new sentences. What landed "had an exemption that read as the wrong check's and ... an 'it' that had lost its referent" (issue-tracker:124-126). |
| S15 | **Confirmation is one `Confirm` comment by the human on the `Overnight <date>` issue** | ADR-0021:31-35 (decision 2); issue-tracker:89-95; GLOSSARY:293 | ADR-0021 Context: the drift of 2026-10-17. |
| S16 | **A scripted session commits a confirmed answer only to the glossary, the guides or a request's Status line, on its own branch; it never commits a frozen contract's sentence or an ADR** | ADR-0021:38-40 (decision 4); morning.js:825; GLOSSARY:286 | #78's grill, Q8 (the morning workflow's first draft would have committed these). |
| S17 | **Whether the cited authority decides the change is the reviewer's call, not the check's** | ADR-0023:44-45; `steering.py:80-81`; landing-work:81-82 | The rejected alternative "a reviewer standard instead of a check" (ADR-0023:58-60). |

---

## Appendix: comment citations of a change request in code, infra and tests (`file: lines`)

- `.gitignore`: 19, 26, 33, 50
- `infra/bootstrap/README.md`: 3, 40, 43, 125, 170, 196
- `infra/bootstrap/ci-role.tf`: 191
- `infra/bootstrap/main.tf`: 31
- `infra/bootstrap/outputs.tf`: 41
- `infra/bootstrap/terraform.tfvars.example`: 11
- `infra/bootstrap/tests/recipes.tftest.hcl`: 1, 41, 74, 97, 136, 159, 164
- `infra/bootstrap/tests/recipes/main.tf`: 4, 7
- `infra/bootstrap/tests/root.tftest.hcl`: 5, 22, 71, 186, 213, 225, 302, 385
- `infra/bootstrap/variables.tf`: 10, 138, 169
- `infra/envs/poc/apps/README.md`: 60
- `infra/envs/poc/apps/versions.tf`: 20
- `infra/envs/poc/idp/versions.tf`: 26
- `infra/envs/poc/platform/main.tf`: 79
- `infra/envs/poc/platform/terraform.tfvars.example`: 6, 29
- `infra/envs/poc/platform/tests/root.tftest.hcl`: 34, 137, 508, 550
- `infra/envs/poc/platform/variables.tf`: 16, 41
- `infra/envs/poc/platform/versions.tf`: 22
- `infra/modules/cell/README.md`: 39, 164, 172, 211
- `infra/modules/cell/ecs.tf`: 108
- `infra/modules/cell/iam.tf`: 36, 237
- `infra/modules/cell/storage.tf`: 19, 32, 45, 53, 64
- `infra/modules/cell/tests/module.tftest.hcl`: 268, 290, 332, 373, 446, 975, 1714, 1880
- `infra/modules/cell/variables.tf`: 60, 83
- `infra/modules/cell/versions.tf`: 11
- `spikes/g0/README.md`: 302
- `spikes/g0/cognito/Report.cs`: 60
- `src/Gateway/Dockerfile`: 31
- `src/Platform.Abstractions/IAccessTokenSource.cs`: 23
- `src/Platform.Abstractions/IRefreshLeases.cs`: 5, 41
- `src/Platform.Abstractions/IRequestTelemetryFeature.cs`: 17, 18, 26
- `src/Platform.Abstractions/Problems.cs`: 17
- `src/Platform.Abstractions/Rejection.cs`: 16, 22, 71
- `src/Platform.Hosting/Assets.cs`: 220
- `src/Platform.Hosting/GatewayInstance.cs`: 8
- `src/Platform.Hosting/Health.cs`: 41
- `src/Platform.Hosting/HostResolution.cs`: 37
- `src/Platform.Hosting/KeyRing.cs`: 13, 51
- `src/Platform.Hosting/Platform.Hosting.csproj`: 10
- `src/Platform.Hosting/PlatformHostingExtensions.cs`: 24, 66
- `src/Platform.Hosting/PlatformHostingLimits.cs`: 15
- `src/Platform.Identity/AccessTokenSource.cs`: 33, 220
- `src/Platform.Identity/AuthenticationEvents.cs`: 57
- `src/Platform.Identity/DynamoDbRefreshLeases.cs`: 9
- `src/Platform.Identity/DynamoDbSessionStore.cs`: 13, 186
- `src/Platform.Identity/DynamoDbSessionTable.cs`: 14, 45, 93
- `src/Platform.Identity/Platform.Identity.csproj`: 14
- `src/Platform.Identity/PlatformIdentityExtensions.cs`: 96
- `src/Platform.Proxy/AccessTokenStep.cs`: 64
- `src/Platform.Proxy/ProxyTimeout.cs`: 29
- `src/Platform.Registry/Platform.Registry.csproj`: 31
- `src/Platform.Testing/CapturedLog.cs`: 34
- `src/Platform.Testing/InMemoryRefreshLeases.cs`: 8
- `src/Platform.Testing/Internal/LoopbackStart.cs`: 13
- `src/Platform.Testing/Platform.Testing.csproj`: 4
- `src/Platform.Testing/RawHttp.cs`: 19
- `src/Platform.Testing/RefreshLeasesContract.cs`: 7, 9
- `src/Platform.Testing/StageHost.cs`: 31, 38, 70
- `tests/Contracts.Tests/ContractIndexTests.cs`: 40 (asserts the citation, see 1c)
- `tests/Contracts.Tests/PlatformConfigurationTests.cs`: 103, 146
- `tests/Gateway.IntegrationTests/Continuity/ConvergenceTests.cs`: 9
- `tests/Gateway.IntegrationTests/Continuity/Logins.cs`: 51
- `tests/Gateway.IntegrationTests/Functional/RefreshTests.cs`: 13, 153
- `tests/Gateway.TestHarness/GatewayHarness.cs`: 280
- `tests/Gateway.TestHarness/GatewayHost.cs`: 18
- `tests/Platform.Abstractions.Tests/ContractTables.cs`: 35, 36
- `tests/Platform.Abstractions.Tests/PublicSurfaceTests.cs`: 18 (states the route, see 1b)
- `tests/Platform.Abstractions.Tests/RefreshLeaseTests.cs`: 7
- `tests/Platform.Abstractions.Tests/RejectionTableTests.cs`: 77, 78, 82
- `tests/Platform.Abstractions.Tests/RejectionTests.cs`: 61
- `tests/Platform.Abstractions.Tests/RequestTelemetryFeatureTests.cs`: 43
- `tests/Platform.Hosting.Tests/HealthTests.cs`: 57, 78, 118, 145, 179
- `tests/Platform.Hosting.Tests/HostResolutionTests.cs`: 198, 208
- `tests/Platform.Hosting.Tests/HostingHost.cs`: 80, 108
- `tests/Platform.Hosting.Tests/KeyRingTests.cs`: 15, 58, 80, 106, 148
- `tests/Platform.Hosting.Tests/RingHost.cs`: 74
- `tests/Platform.Hosting.Tests/StubRingStore.cs`: 10
- `tests/Platform.Identity.Tests/AccessTokenSourceTests.cs`: 214
- `tests/Platform.Identity.Tests/DynamoDbProtocolTests.cs`: 14, 203
- `tests/Platform.Identity.Tests/DynamoDbSessionStoreContractTests.cs`: 28
- `tests/Platform.Identity.Tests/DynamoDbWriteOutcomeTests.cs`: 10, 98
- `tests/Platform.Identity.Tests/FakeDynamoDbServer.cs`: 16
- `tests/Platform.Identity.Tests/IdentityGateway.cs`: 43
- `tests/Platform.Identity.Tests/KeyRingContinuityTests.cs`: 10, 79
- `tests/Platform.Identity.Tests/ListXmlRepository.cs`: 8
- `tests/Platform.Identity.Tests/SessionStoreRegistrationTests.cs`: 83, 138
- `tests/Platform.Proxy.Tests/AccessTokenStepTests.cs`: 83
- `tests/Platform.Proxy.Tests/ProxyHost.cs`: 64
- `tests/Platform.Proxy.Tests/ProxyTimeoutCeilingTests.cs`: 52, 75 (`:75` states the route, see 1b)
- `tests/Platform.Proxy.Tests/RecordingLoggerProvider.cs`: 12, 14
- `tests/Platform.Proxy.Tests/RuleIdTelemetryTests.cs`: 72
- `tests/Platform.Proxy.Tests/YarpActivityTimeout.cs`: 133
- `tests/Platform.Security.Tests/AntiforgeryHost.cs`: 85
- `tests/Platform.Security.Tests/GateHost.cs`: 64
- `tests/Platform.Telemetry.Tests/TelemetryHost.cs`: 178
- `tests/Platform.Testing.Tests/InMemoryRefreshLeasesTests.cs`: 8
- `tests/Platform.Testing.Tests/SessionStoreContractTests.cs`: 10
- `tests/Platform.Testing.Tests/SettableRequestTelemetryFeatureTests.cs`: 40
- `tests/Platform.Testing.Tests/StageHostTests.cs`: 24
- `tests/Spactl.Tests/Harness.cs`: 129
- `tests/Spactl.Tests/WaitAndRollbackTests.cs`: 42, 76
- `tools/aws-project.env`: 24
- `tools/check_tests.py`: 806
- `tools/pins_inventory.py`: 31
- `tools/preflight.sh`: 258, 280
- `tools/roadmap.py`: 294
- `tools/roadmap_tests.py`: 140
- `tools/scripts/cell-common.sh`: 35, 108, 257, 347, 482
- `tools/scripts/deploy.sh`: 37
- `tools/scripts/deploy_tests.py`: 99, 172
- `tools/scripts/teardown.sh`: 17
- `tools/scripts/teardown_tests.py`: 6, 139
- `tools/scripts/verify-release-immutability.sh`: 304, 318
- `tools/scripts/verify_release_immutability_tests.py`: 42
- `tools/spactl/Commands.cs`: 290, 629

This list comes from `grep -rn -i -E 'contracts/changes|change requests?'` over `src`, `tests`,
`infra`, `tools/scripts`, `tools/spactl`, `spikes`, `.gitignore` and selected `tools/` files. A few
of the matched lines use "change request" in a general sense rather than as a citation.
