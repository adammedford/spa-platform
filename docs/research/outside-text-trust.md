# Which agents and scripts act on text an outside account writes, without checking its author

- **Ticket:** #1551, "Which agents and scripts act on text an outside account writes, without checking its author?", a child of the wayfinder map #1548
- **Date:** 2026-11-25
- **Branch:** `research/outside-text-trust`, cut from origin/main at `feafd88c`; every line number below is at that commit
- **Method:** read the code of every reader the ticket names, and the others a search for tracker reads turned up (`gh issue view`, `gh api .../comments`, `gh pr view`, `gh issue list`), against `docs/agents/review.md`, `docs/agents/tracker-policy.md`, `docs/agents/landing-work.md` and `CODING_STANDARDS.md` → "An authorisation is read from its author". Ran the author-check test suites and a few local probes of the checking functions (the closing section lists them). GitHub's permission model comes from its documentation. No workflow, overnight, morning, retro or review-queue run was launched, no comment was posted to test a reader, and nothing touched AWS or PingFederate.

## Answer in brief

1. **#1547's three claims hold, for the tokens that authorise.** Each is checked in code against its author:
   - the account that applied `ready-for-agent` (`tools/overnight_collect.py`);
   - a `Confirm` comment and a questions comment (`.claude/workflows/morning.js`, `tools/overnight_answers.py`);
   - an `Authority: <URL>` comment (`.github/steering.py`);
   - a review record (`tools/review_record.py`, `review-queue.js`, `morning.js`);
   - a `Decision:` comment (`tools/decision_citation.py`).

   The suites pass, and the probes show an outsider's `Confirm`, record and `Authority:` comment refused. What the claims leave out is the text those tokens point at, and the readers that act on text they do not check at all.
2. **The label check covers the label, not the body.** A lane reads the ticket body live, as its spec, at the time it runs. Nothing compares the issue's author, or the body's last edit, with the `ready-for-agent` event the collect step checks. So an issue an outsider opened, and Adam or a session later labelled, keeps a body its author can still change.

   Fix: a lane takes a ticket only when `@adammedford` opened it, or when the body has not changed since the authorised label. The alternative is for the label step to freeze a copy of the body.
3. **Every session posts as `@adammedford`, so a session that copies outside text into a ticket gets it past the author checks.** Two workflows apply `ready-for-agent` themselves:
   - The retro files `ready-for-agent` tickets whose evidence quotes comments by any account.
   - The pins sweep adopts any open issue that carries its marker line, whoever opened it, then rewrites the body and relabels it.

   Fix: these readers take only repository accounts' text (`author_association` `OWNER`, `MEMBER` or `COLLABORATOR`). The pins sweep adopts only issues `@adammedford` opened.
4. **The overnight workflow's resume markers accept a comment by any account.**
   - A comment headed `## Review` makes the reviewer stage return as already done, taking that comment's findings.
   - A `## Questions for Adam` comment is carried into the Overnight issue's questions.
   - A `## Draft decision` comment counts as a change request's draft.

   The Route, grill, implement-resume and report agents read every comment into their prompts. Fix: each marker matches only the account's comment, and the prompts get only repository accounts' comments.
5. **`review-queue.js` filters review records by `author_association` but nothing else about an outside pull request.** No step reads the pull request's author or `isCrossRepository`, and nothing treats an outside pull request differently:
   - A fork's head is not fetched, because origin's refspec is `refs/heads/*`. The inventory fails today by accident, not by a check.
   - Once the head is fetched, the mutation agents check it out and run its test project, the contributor's code, on the maintainer's machine.
   - A stated mutation and its container image go from the pull request body into the agent's instructions verbatim.
   - The Spec axis judges the diff against whichever issue the body cites.

   Fix: identify an outside pull request in the script. Run its code only in a container without credentials. Pass body fields as quoted data, not instructions. Require that the cited ticket was opened or labelled by the account.
6. **`Authority: #<n>` accepts any issue of the repository, whoever opened it** (`.github/steering.py` 1188 to 1209; the docstring at 1182 to 1187 leaves this to the reviewer on purpose). Also, CI on a fork pull request runs the pull request's own copy of `steering.py` and `path-ownership.py`, and an outsider never runs the pre-push hook.

   Fix: `#<n>` requires an issue the account opened or labelled. CI runs the checkers from the base commit's copy, and the reviewer and `tools/land.sh` do not take CI's green on an outside pull request as the steering verdict.
7. **`ticket-shape.yml` and `issue-labels.yml` act on outside issues with no author check, and that is safe as written.**
   - Neither interpolates text from the event: the number goes through `env`.
   - Both check out the default branch and hold `issues: write`, `pull-requests: read` and `contents: read`.
   - `ticket-shape.yml` judges only issues that already carry `ready-for-agent`, which an outsider cannot apply.

   What an outsider can do: pick their own issue's `kind:`, `area:` and `vendor:` labels through its Touches and Vendor lines, and make the daily `issue-labels` run fail on a line the table does not cover. Fix: none beyond #1547, though the daily run could skip outside issues rather than fail on them.
8. **Many listings find issues by title or marker line, and none checks who opened the issue.** These are `Overnight <date>`, `Retro <date>`, the pins report title, a follow-up's `Raised by:` line, truth-audit's `**Noun:**` and `**Rule:**` lines, and gate-evidence's open issues that name a row. An outsider's issue there halts a run or suppresses a filing; it does not start one. The morning stops on two issues with the night's title, which is the fail-closed outcome. Fix: filter each listing to repository accounts before matching.
9. **Some author checks are prompt-only, or run on a field an agent copied.**
   - Prompt-only: the overnight integrator lane's `Decision:` filter, review-queue's change-request decision, and the wiz skill's rule 5 and H1.
   - Copied by an agent: the morning's `Confirm` check and review-queue's record check run in script on an author field an agent transcribed from `gh`. Neither script makes the call itself.
   - Two mismatches with the docs: "the account" (a login) and "a repository account" (`author_association`) are two different tests, and `morning.js` hardcodes `adammedford` where the other readers parse the account line in `tracker-policy.md`.
10. **No outside account has opened an issue or a pull request yet.** A REST count over every issue and pull request found zero with an `author_association` outside `OWNER`, `MEMBER` and `COLLABORATOR`, and zero cross-repository pull requests. Every gap above is latent.

## Detail: each reader, its author check, and what outside text could cause

The check column takes one of four values:
- **login in code:** the script compares the author's login with the account.
- **association in code:** the script admits `OWNER`, `MEMBER` or `COLLABORATOR`.
- **prompt only:** an agent is told to filter, and no code checks that it did.
- **none.**

### The authorisation readers #1547 relies on

| Reader | File:line | Text read | Author check | What an outsider's text could cause | Evidence |
| --- | --- | --- | --- | --- | --- |
| Collect, who applied `ready-for-agent` | `tools/overnight_collect.py` 315-339, 537 | the actor of the last `labeled` event | login in code (the account `tracker-policy.md` names) | Nothing: another actor makes the ticket `needs-triage` | the docstring at 46-55; `overnight_collect_tests.py` passes, with outsider-label cases at 512, 803 and 997 |
| Collect, the ticket body | `tools/overnight_collect.py` 29-65 (id, owner, Touches, boxes) | the issue body | **none**: neither the issue's author nor its edit time is read | On an issue an outsider opened that the account later labelled, the author's later edit sets the spec, Touches and boxes a lane builds | no `author`, `user` or `lastEditedAt` field is read anywhere in the file; the lane reads the body live, `overnight.js` 491 |
| Morning, `Confirm` and questions comments | `.claude/workflows/morning.js` 217, 972-973, 1033, 1547 | Overnight issue comments | login in code, hardcoded `ADAM = 'adammedford'` | Nothing: an outsider's comment is never fetched (1547) or counted | `morning_tests.mjs` 1530-1538, an outsider's `Confirm` that stays dry; the suite passes |
| Morning, hand-over boundary | `morning.js` 765 | `## Morning` comments | login in code | Nothing | the code read |
| Morning, apply tickets | `morning.js` 892-899 | `Apply Q<n> ...` issues | login in code (`author.login === ADAM`) | Nothing | the code read |
| Morning, review records and follow-ups | `morning.js` 555, 575-586 | pull request comments and reviews | association in code | Nothing: the follow-up table is read only from a record that passes | the code read |
| `tools/overnight_answers.py` (run by `land.sh`) | 194-276 | Overnight questions and `Confirm` | login in code (from the guide) | Nothing: an outsider's comment is named and not read | docstring 39-53 |
| `tools/decision_citation.py` (run by `land.sh`) | 16-35 | `Decision:` comments | login in code | Nothing | docstring 16-24 |
| `.github/steering.py`, `Authority: <URL>` | 1212-1234; account read at the base, 1503-1513 | the cited comment's login | login in code, from the guide **at the base**, so a branch cannot rename the account | Nothing: an outsider's comment is `unconfirmed` | probe: `unconfirmed`, "is a comment by @someone-else"; `steering_tests.py` 1674 passes |
| `.github/steering.py`, `Authority: #<n>` | 1188-1209 | whether `#n` is an issue | **none** (by design, docstring 1182-1187) | An outsider's own issue counts as authority for a steering change; only the reviewer stands between them | probe: `judged("#9", ...)` returns `vouched` |
| `tools/review_record.py` (run by `land.sh`) | 110-116, 187-189 | record lines | association in code; no association at all is refused | Nothing: `NONE` and `CONTRIBUTOR` records are "not read" | probe: exit 1 for `NONE` and for `CONTRIBUTOR`, exit 0 for `OWNER` |

### The overnight workflow, `.claude/workflows/overnight.js`

| Reader | Line | Text read | Author check | What an outsider's text could cause | Evidence |
| --- | --- | --- | --- | --- | --- |
| Route | 438 | `title,body,comments` | none | Outside comments go into the Route agent's prompt and steer how an unclear ticket is routed | the prompt read |
| Implement, the spec | 491 | `title,body` | none (the label check covers the label, row 2 above) | The body of an outside-opened, account-labelled issue is the spec | the prompt read |
| Implement, resume | 485-486 | `comments` | none | Outside comments go into the prompt as "what an earlier attempt recorded" | the prompt read |
| Reviewer, resume marker | 531 | comments headed `## Review` | **none** | An outside comment with that heading makes the review stage return as done, taking that comment's findings. The landing pull request still needs a review-queue record (ADR-0022) | the prompt read |
| Change request, decision | 462-470 | comments with `login` | prompt only (`DECISION_COMMENT`, 464) | If the agent misapplies it, an outside `Decision:` comment authorises an integrator build | the prompt read; no script check follows |
| Change request, draft | 746 | comments starting `## Draft decision` | none | An outside draft counts as "a draft already exists", and nothing new is drafted | the prompt read |
| Grill, resume marker | 763 | comments headed `## Questions for Adam` | **none** | Outside questions and recommendations are carried into the Overnight issue. Under a standing answer they are pre-accepted, and a one-word `Confirm` confirms every pre-accepted answer | the prompt read; `landing-work.md` 446-450 on the bare `Confirm` |
| Report | 790-814 | each lane ticket's comments | none | Outside comments go into the report agent's prompt | the prompt read |
| Change-request search | 180 | `change-request` issues | label only (an outsider cannot apply labels) | Nothing directly | GitHub's roles table: applying labels needs Triage, which Read lacks |

### The retro, `.claude/workflows/retro.js`

| Reader | Line | Text read | Author check | What an outsider's text could cause | Evidence |
| --- | --- | --- | --- | --- | --- |
| Gather, the record | 145, 302 | the Overnight issue's comments and each lane ticket's comments | none | Outside comments become evidence for findings | the prompt read |
| File | 379, 382 | its own ticket bodies | n/a; filed as `@adammedford`, with `ready-for-agent` when "the fix is provable on this Mac" | A finding grounded in an outside comment becomes a `ready-for-agent` ticket labelled by the account, and the collect step's label check passes it | the prompt read |
| Drop and dedupe | 346 | open issues matching the noun | none | An outside issue that "asks for the same fix" drops a finding | the prompt read |
| An open `Retro <date>` | 290, 376 | an issue titled that | none | Any open issue with the title stops the retro filing | the prompt read |
| Finding the Overnight issue | 287 | by title | none | A duplicate title can confuse which issue the retro reads | the prompt read |

### The review queue, `.claude/workflows/review-queue.js`

| Reader | Line | Text read | Author check | What an outsider's text could cause | Evidence |
| --- | --- | --- | --- | --- | --- |
| Records | 289, 299-310 | comments and `COMMENTED` reviews | association in code, on fields the inventory agent copied (703) | Nothing, unless the inventory agent misreports a field | `review-queue_tests.mjs` passes |
| Inventory, the pull request | 693, 699-701 | `author`, `body`, commit messages, the diff | **none**: `author` is returned but gates nothing, and `isCrossRepository` is never read | The body's claims, stated mutations, heads and citations shape the review | grep: no `isCrossRepository`, `headRepository` or `refs/pull` in the file |
| A fork's head | 693, 695 | `git fetch origin --tags`, then `merge-base` on the head | none | Today the head is not among the objects, since `remote.origin.fetch` is `+refs/heads/*:refs/remotes/origin/*`, so the inventory fails. An agent that fetches the head itself reaches the rows below | `git config --get-all remote.origin.fetch` |
| Mutations | 846-853 | the head's code and the body's stated mutation | none | The contributor's test project, which MSBuild, Terraform or Python runs, executes on the maintainer's machine with its `gh` login. The stated mutation and its image go verbatim into the agent's instructions | the prompt read |
| Spec axis, the ticket | 764, 800, 811 | the issue the body cites, its body and comments | none on the ticket; prompt only (`@adammedford`) on a change request's decision | An outside pull request can cite an issue its author opened, and be judged against that issue as its spec | the prompt read |
| A held change request | 775-777 | its own body, from the finding | n/a; filed `ready-for-human` | Nothing a lane takes | the prompt read |

### Other workflows and tools

| Reader | File:line | Text read | Author check | What an outsider's text could cause | Evidence |
| --- | --- | --- | --- | --- | --- |
| Pins sweep, adoption | `.claude/workflows/pins-sweep.js` 255, 553-567, 504 | any open issue with a `**Pins family:**` marker | **none** | An outside issue with a matching marker is rewritten and relabelled by the account, `ready-for-agent` when the trial is green. A later edit by its author then reaches a lane as in row 2. A matching move instead makes the sweep only comment | the code read |
| Pins sweep, the report issue | 256 | the issue titled `Pins sweep: the state of every pin` | none | Its status table is read from whichever issue holds that title | the prompt read |
| Truth audit | `.claude/workflows/truth-audit.js` 330-331, 559-566 | `**Noun:**` lines on open issues and `**Rule:**` lines on the rule issue's comments | none | Suppresses a finding or a candidate rule | the code read |
| Gate evidence | `.claude/workflows/gate-evidence.js` 672, 1015-1016 | open issues naming a row | none (change requests: by label) | An outside issue is named as one "carrying" a row that is not done, in the evidence report | the code read |
| Morning, follow-up dedupe | `morning.js` 588-589, 1736 | issues by title and `Raised by:` line | none | An outside issue in that shape suppresses a follow-up | the code read |
| Morning, the Overnight issue | `morning.js` 297 | issues by title | none (halts unless exactly one matches) | A duplicate title halts the morning: fail-closed | the prompt read |
| `tools/land.sh` | 304 | the pull request description's closing references | none, mechanical | On merge, an outside pull request closes the issues it names, as long as its own commits name them too | the docstring at 52-60 |
| `ticket-shape.yml` and `tools/ticket_shape.py` | the workflow at 41-77; the script at 521-532 | the body of an issue labelled `ready-for-agent` | none, and none needed (it runs only after an authorised label) | The bot comment quotes up to 160 characters of a failing box. It cannot add `ready-for-agent`, only remove it | the workflow read; `NUMBER` goes through `env`, so no expression injection |
| `issue-labels.yml` and `tools/issue_labels.py` | the whole workflow | title, sub-issues, Touches and Vendor lines | none | The author picks their own issue's `kind:`, `area:` and `vendor:` labels, and an uncovered path fails the daily run | the workflow header |
| `ci.yml` on a fork pull request | 59-64, 236-243 | the pull request's tree, including `.github/steering.py` | n/a; `pull_request`, `contents: read`, no secrets | The pull request's own copy of the steering and ownership checkers judges it | the workflow read; nothing uses `pull_request_target` |
| `codeql-weekly.yml` and `startup-bench.yml` | their triggers | none | n/a: schedule and dispatch only | Nothing | the workflows read |
| The wiz skill | `.claude/skills/wiz/SKILL.md` 44-46, 209, 292-295 | fixed issues' bodies; Overnight comments for H1 | prompt only; rule 5 does not say a `Confirm` must be Adam's | A session reading H1's digest or rule 5 could count an outside `Confirm` | the skill read; the fixed issues at 209 are the account's own |
| Project skills | `.claude/skills/` | none other: wiz is the only project skill | n/a | n/a | `ls .claude/skills` |

### The stated rules against the code

- `tracker-policy.md` lines 75-84 say a `Confirm` and `ready-for-agent` count only from `@adammedford`, and that the collect step and steering read the account from that line. The code agrees. `morning.js` 217 instead hardcodes the login, so a change to the guide's line would not reach it.
- `review.md` lines 145-147 read records only from `OWNER`, `MEMBER` and `COLLABORATOR`. The code agrees, in `review_record.py`, `review-queue.js` and `morning.js`. These are two different tests: the record test admits any collaborator the repository invites, and the `Confirm` test admits only the one login.
- `CODING_STANDARDS.md` → "An authorisation is read from its author" asks whether the code checks the writer "everywhere the writing is used, in what it confirms, what it overrides and what it switches on". The resume markers (row 4 of the answer), the retro's and pins sweep's labelling (row 3) and the label-versus-body gap (row 2) are the places it does not.
- `tracker-policy.md` 80-84 already says that the shared account "cannot tell a session from the human". Row 3 is the same limit seen from the other side: a session's write carries the account's authority whatever text it copied.

## What was executed and what was only read

Executed on 2026-11-25, in the worktree at `feafd88c`:
- `python3 -I tools/overnight_collect_tests.py`, `python3 -I .github/steering_tests.py` and `python3 -I tools/review_record_tests.py`: each `OK`.
- `node .claude/workflows/morning_tests.mjs`: "pass: 59 checks". `node .claude/workflows/review-queue_tests.mjs`: "pass: 80 checks".
- A probe that imports `steering.py`. `judged("#9", ...)` on an issue returned `vouched`. `judged(<comment URL>, ...)` on a comment by another login returned `unconfirmed`.
- `tools/review_record.py <head>` on a slurped record with `author_association` `NONE`, then `CONTRIBUTOR`, then `OWNER`. The first two exit 1, "from outside the repository, so not read". The third exits 0.
- `git config --get-all remote.origin.fetch` in the main checkout: `+refs/heads/*:refs/remotes/origin/*`.
- REST counts over `issues?state=all` and `pulls?state=all`: zero items from outside `OWNER`, `MEMBER` and `COLLABORATOR`, and zero cross-repository pull requests.

Read only, on 2026-11-25:
- The code at `feafd88c`: `tools/overnight_collect.py`, `tools/overnight_answers.py`, `tools/decision_citation.py`, `tools/review_record.py`, `tools/ticket_shape.py`, `tools/land.sh`, `.github/steering.py`, `.github/workflows/{ci,ticket-shape,issue-labels,codeql-weekly,startup-bench}.yml`, `.claude/workflows/{overnight,morning,review-queue,retro,pins-sweep,truth-audit,gate-evidence}.js` and `.claude/skills/wiz/SKILL.md`.
- The rules: `docs/agents/review.md`, `docs/agents/tracker-policy.md`, `docs/agents/landing-work.md` and `CODING_STANDARDS.md`.
- The tracker: #1547, #1548 and #1551.
- GitHub's roles table, https://docs.github.com/en/organizations/managing-user-access-to-your-organizations-repositories/managing-repository-roles/repository-roles-for-an-organization. "Apply/dismiss labels" needs Triage, which Read lacks. "Edit and delete their own comments" is open to every role.

Not verified: that an issue's author who is not a collaborator can edit that issue's body. GitHub's interface allows it, but the pages read do not say so for issue bodies, only for comments. Row 2 of the answer rests on it. It was not tested live, since that would mean writing to the tracker from an outside account.
