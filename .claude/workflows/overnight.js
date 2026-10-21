export const meta = {
  name: 'overnight',
  description: 'Collect open tickets from GitHub Issues, implement agent-ready ones in owner lanes onto overnight/<date>, grill human ones on paper, report as an Overnight <date> issue',
  whenToUse: 'Unattended overnight run over the open rows of docs/plan/03-workstreams-and-tasks.md',
  phases: [
    { title: 'Collect', detail: 'open issues labelled ready-for-agent / ready-for-human become tonight\'s tickets; plan rows with no issue yet get one' },
    { title: 'Route', detail: 'needs-triage tickets routed with ask-matt; routing is a comment and a label swap' },
    { title: 'Implement', detail: 'one lane per owner, implement skill text with tdd, one check; the issue is claimed, commented and ticked, and stays open until the landing closes it (#100)' },
    { title: 'Review', detail: 'code-review against the fork point, reusing the implement gate; findings as a comment' },
    { title: 'Integrate', detail: 'serialized merge into overnight/<date>, integration gate with smoke; the merge body cites #<n>' },
    { title: 'Grill', detail: 'grilling + domain-modeling on paper, record and questions as comments on the issue' },
    { title: 'Report', detail: 'one issue, Overnight <date>: the report, then the questions Q1..Qn' },
  ],
}

// ---------------------------------------------------------------- args and paths
// Launch (Workflow tool). Pass scriptPath, so the file as edited runs and not a session's snapshot:
//   scriptPath: /Users/adammedford/Projects/spa-platform/.claude/workflows/overnight.js
//   args: { "repo": "/Users/adammedford/Projects/spa-platform", "date": "2026-10-19",
//           "wt": "/Users/adammedford/.worktrees/overnight-2026-10-19", "standingAnswer": true, "maxParallel": 3 }
// Arguments (#106). Every required one is required because the script can derive none of them: an argless
// `new Date()` throws in a Workflow script, and it has no environment or filesystem access. A resume
// (resumeFromRunId) passes no arguments on its own: pass the original ones again.
//   repo            required  the main checkout, an absolute path. Collect refuses to start unless it sits at
//                             origin/main with no tracked change.
//   date            required  YYYY-MM-DD, the morning the run ends: an evening launch on the 18th uses
//                             2026-10-19. Names overnight/<date> and the "Overnight <date>" issue.
//   wt              required  the worktree root, an absolute path outside repo (and outside iCloud Drive):
//                             <wt>/overnight is the integration worktree, <wt>/<owner> each lane's.
//   standingAnswer  required  a boolean. true grants Adam's standing answer for this run (ADR-0021 decision 3:
//                             per launch, default off). The string "true" is refused, not read as false.
//   base            optional  the full 40-hex sha of origin/main; absent, collect takes the main checkout's
//                             HEAD and checks it against origin.
//   maxParallel     optional  positive integer, default 3.      maxFailures  optional  positive integer, default 4.
//   only            optional  array of task ids to run.         skip         optional  array of task ids to
//                             leave out, added to FOREGROUND_ONLY below.
// Shell (#129): every agent's Bash must run /bin/bash. settings.json sets CLAUDE_CODE_SHELL=/bin/bash, but only a
// Claude Code process started after that setting merged honours it; collect reports the shell, and the run halts
// before Route, with no lane started, when it is not bash. Restart Claude Code before a launch if in doubt.
const A = args || {}
const badArguments = (a) => {
  const bad = []
  const abs = (p) => typeof p === 'string' && p.startsWith('/') && p.length > 1
  const ids = (v) => Array.isArray(v) && v.every((x) => typeof x === 'string' && x.length > 0)
  const count = (v) => Number.isInteger(v) && v > 0
  if (!abs(a.repo)) bad.push('repo: the main checkout as an absolute path, e.g. /Users/adammedford/Projects/spa-platform')
  if (typeof a.date !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(a.date)) bad.push('date: YYYY-MM-DD, the morning the run ends (an evening launch on the 18th uses 2026-10-19)')
  if (!abs(a.wt)) bad.push('wt: the worktree root as an absolute path outside repo, e.g. /Users/adammedford/.worktrees/overnight-<date>')
  else if (abs(a.repo) && (a.wt === a.repo || a.wt.startsWith(a.repo.replace(/\/+$/, '') + '/'))) bad.push('wt: must lie outside repo; the house rules forbid a worktree under the repository')
  if (typeof a.standingAnswer !== 'boolean') bad.push('standingAnswer: true or false, a boolean (the string "true" is refused); it grants the standing answer for this run (ADR-0021 decision 3)')
  if (a.base !== undefined && !/^[0-9a-f]{40}$/.test(String(a.base))) bad.push('base: when given, the full 40-hex sha of origin/main')
  if (a.maxParallel !== undefined && !count(a.maxParallel)) bad.push('maxParallel: a positive integer (default 3)')
  if (a.maxFailures !== undefined && !count(a.maxFailures)) bad.push('maxFailures: a positive integer (default 4)')
  if (a.only !== undefined && a.only !== null && !ids(a.only)) bad.push('only: an array of task ids, e.g. ["2.1b", "issue-96"]')
  if (a.skip !== undefined && !ids(a.skip)) bad.push('skip: an array of task ids to leave out')
  return bad
}
const BAD_LAUNCH = badArguments(A)
if (BAD_LAUNCH.length) {
  // Stops here, before Collect: no agent is started, nothing is read or written (#106).
  return { halted: 'bad launch; no agent started. Fix each argument and relaunch with all of them:\n- ' + BAD_LAUNCH.join('\n- ') }
}
const REPO = A.repo
let BASE = A.base || ''          // '' = the collect step takes the main checkout's HEAD, which must be origin's main
const DATE = A.date
const WTROOT = A.wt
const MAX_PAR = A.maxParallel || 3
const MAX_FAIL = A.maxFailures || 4
const ONLY = A.only || null          // task ids to implement/grill; null = all (applied after collect)
// Foreground-only work: the six workflow specs (#77 to #82) build git-ignored files in the main checkout, which
// no lane may write, so they stay out of every run until /to-tickets splits them into tickets a lane can land.
// Remove an id here when its tickets exist; A.skip extends the list for one run. Tickets filed from these specs carry a
// `**Session:** foreground` line instead, which the collect prompt reads, so they need no entry here.
const FOREGROUND_ONLY = ['issue-77', 'issue-78', 'issue-79', 'issue-80', 'issue-81', 'issue-82']
const SKIP = new Set([...FOREGROUND_ONLY, ...(A.skip || [])])
// The standing answer is granted per launch (#78 grill, Q5/Q12), default off. Granted, grills and lanes take each
// recommendation as decided and write pre-accepted answers; not granted, answers stay open and human-owned
// decisions stop the lane. A pre-accepted answer is provisional until Adam confirms it on the Overnight issue.
const STANDING = A.standingAnswer === true
const STANDING_LINE = STANDING ? 'granted for this run (' + DATE + ')' : 'not granted; nothing is pre-accepted'

const OVR = `${REPO}/.scratch/overnight`
const LOG = `${OVR}/log`                 // git-ignored (.scratch/*/log/)
const RESEARCH = `${LOG}/research`    // under log/, which git ignores; the archive's research/ is not
const PENDING = `${LOG}/pending-${DATE}.jsonl`
const PARENT_ISSUE = 51                  // "Plan rows and integrator tickets open at the switch to Issues": new plan-row tickets are its sub-issues
const INT_BRANCH = `overnight/${DATE}`
const INT_WT = `${WTROOT}/overnight`
const WT = (owner) => `${WTROOT}/${owner}`
const PORT = (n) => 5100 + 10 * n
const TRAILER = 'Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>'
const SKILLS = '/Users/adammedford/.agents/skills'
const skill = (name) => `call the Skill tool with "${name}" (if the Skill tool is unavailable or refuses, read ${SKILLS}/${name}/SKILL.md and follow it)`

// ---------------------------------------------------------------- shared rules
const HOUSE = `House rules (unattended; nobody will answer questions tonight):
- The main checkout ${REPO} is Adam's. Never commit, checkout, switch, stash, reset or build there. The only files you may write there are under ${LOG}/ and ${RESEARCH}/.
- The tracker is GitHub Issues: read with \`gh issue view\`, write with \`gh issue comment\` / \`gh issue edit\`; never write a ticket file, the directories under .scratch/<slug>/issues/ are an archive. Run every \`gh\` command with \`cd ${REPO} &&\` in front so it resolves this repository. A multi-line comment body goes through stdin: \`gh issue comment <n> --body-file - <<'EOF' ... EOF\`.
- If \`gh\` fails because GitHub is unreachable, append the comment or label change you meant to make as one JSON line to ${PENDING} ({"issue": <n>, "action": "comment"|"edit"|"close", "body": "<the comment text, or the gh issue edit flags>"}) and continue; the morning session replays it.
- Never write a comment that begins "Confirm" on any issue: a confirmation is the human's act (docs/agents/issue-tracker.md).
- An ADR you write is a draft: put it under ${LOG}/, name its path in your "## Answer" or "## Review" comment, and leave it uncommitted, because a scripted session drafts an ADR and never commits one (ADR-0021 decision 4). Title it \`ADR draft: <title>\`, cite it by that title, and leave its number to the session that commits it, which takes the next free one then (docs/adr/README.md).
- Begin every Bash command that touches your worktree with \`cd <worktree> &&\`, and use \`git -C <path>\` for every git command. To delete a file under tests/, run \`cd <worktree> && git rm tests/<path>\` as ONE Bash call: the shell's directory resets after every call, so a \`git rm\` in a call of its own runs in Adam's main checkout. A bare \`git commit\` lands on Adam's branch.
- Change a file with Edit only after opening it with Read in this session, and open it again with Read after anything else wrote it (a script, the check, a merge); otherwise make the change with a short script. Edit refuses a file it has not seen read, and 16 calls went to that refusal on 2026-10-18 (#109).
- Worktrees live under ${WTROOT}, outside iCloud Drive. Never create one under the repo.
- AGENTS.md's rules hold: owner paths only (OWNERSHIP.md); an integrator need is a change request under contracts/changes/, never an edit; never log a secret, token, cookie, code or state value.
- Read contracts/pipeline.md before touching the composed gateway. Its stage order changed at G1b (ADR-0018: UsePlatformTelemetry first); outside that table a stage is named by its entry point, never numbered (CODING_STANDARDS.md).
- Never run \`dotnet build-server shutdown\`, \`dotnet nuget locals --clear\`, or anything that deletes outside your worktree.
- If you start a process by hand (a gateway, the mock API), bind only the port you are given (ASPNETCORE_HTTP_PORTS), and kill it before you return.
- Commit messages end with the attribution line "${TRAILER}" (or the one this session's own attribution reminder names, if it differs). Never push.`

// The check is tools/check.sh, run as written (#68): the same file the pre-push hook runs, so a lane
// never repairs a pasted command again. --base is the fork point a lane is judged against; --port is
// the lane's own, for the smoke test. Its last line is its verdict.
const GATE = ({ dir, log, kind, fork, port }) => `THE CHECK (${kind}). Run it as ONE Bash call with a 600000 ms timeout, output to ${log}:
  cd ${dir} && { echo "HEAD=$(git -C ${dir} rev-parse HEAD)"; tools/check.sh --full --base ${fork} --port ${port}; } > ${log} 2>&1; tail -25 ${log}
Green means the last line of ${log} starts "check: pass:". Run the command exactly as written; if the check itself fails to run, report that, do not repair it. This is the only full build-and-suite run: do not run \`dotnet build\` or the full suite any other way. Per-project runs (\`dotnet test --project tests/<X>.Tests\`) are fine while working. To learn what the check does, read its header only, \`sed -n 1,60p tools/check.sh\`, not the whole file.`

// How a lane ticks or unticks acceptance boxes: the body is the spec and must otherwise stay byte-identical.
const TICK = (t, verb) => `To ${verb} acceptance boxes, edit the issue body and nothing else in it: \`cd ${REPO} && gh issue view ${t.issue} --json body --jq .body > ${LOG}/${t.id}-body.md\`, change only the \`- [ ]\` / \`- [x]\` markers of the lines concerned (no other byte of the file), then \`cd ${REPO} && gh issue edit ${t.issue} --body-file ${LOG}/${t.id}-body.md\`.`

// ---------------------------------------------------------------- schemas
const TICKET = {
  type: 'object',
  properties: {
    n: { type: 'integer' }, id: { type: 'string' }, slug: { type: 'string' },
    issue: { type: 'integer' }, url: { type: 'string' },
    title: { type: 'string' },
    status: { type: 'string', enum: ['ready-for-agent', 'ready-for-human', 'needs-triage', 'blocked', 'claimed'] },
    owner: { type: 'string' }, blockedBy: { type: 'array', items: { type: 'string' } }, why: { type: 'string' },
    openBoxes: { type: 'integer' },
  },
  required: ['n', 'id', 'slug', 'issue', 'url', 'title', 'status', 'owner', 'blockedBy', 'why', 'openBoxes'],
}
const COLLECT = {
  type: 'object',
  properties: {
    outcome: { type: 'string', enum: ['ok', 'dirty-start'] },
    reason: { type: 'string' },
    tickets: { type: 'array', items: TICKET },
    landed: { type: 'array', items: { type: 'string' } },
    base: { type: 'string' },
    shell: { type: 'string' }, shellCommand: { type: 'string' },
  },
  required: ['outcome', 'reason', 'tickets', 'landed', 'base', 'shell', 'shellCommand'],
}
const ROUTE = {
  type: 'object',
  properties: { flow: { type: 'string', enum: ['agent', 'human', 'research-then-human'] }, reason: { type: 'string' } },
  required: ['flow', 'reason'],
}
const IMPL = {
  type: 'object',
  properties: {
    outcome: { type: 'string', enum: ['resolved', 'needs-human', 'failed', 'skipped'] },
    branch: { type: 'string' }, forkSha: { type: 'string' }, headSha: { type: 'string' },
    complete: { type: 'boolean' }, outstanding: { type: 'string' },
    summary: { type: 'string' }, question: { type: 'string' },
    delegatedDecisions: { type: 'array', items: { type: 'string' } },
    proposedSteering: { type: 'array', items: { type: 'string' } },
  },
  required: ['outcome', 'branch', 'forkSha', 'headSha', 'complete', 'outstanding', 'summary', 'question', 'delegatedDecisions', 'proposedSteering'],
}
const REVIEW = {
  type: 'object',
  properties: {
    outcome: { type: 'string', enum: ['committed', 'needs-human', 'failed'] },
    headSha: { type: 'string' },
    findingsFixed: { type: 'array', items: { type: 'string' } },
    findingsSkipped: { type: 'array', items: { type: 'string' } },
    summary: { type: 'string' }, question: { type: 'string' },
    proposedSteering: { type: 'array', items: { type: 'string' } },
  },
  required: ['outcome', 'headSha', 'findingsFixed', 'findingsSkipped', 'summary', 'question', 'proposedSteering'],
}
const INT = {
  type: 'object',
  properties: { ok: { type: 'boolean' }, mergeSha: { type: 'string' }, notes: { type: 'string' } },
  required: ['ok', 'mergeSha', 'notes'],
}
const GRILL = {
  type: 'object',
  properties: {
    questions: { type: 'array', items: { type: 'object', properties: { q: { type: 'string' }, recommendation: { type: 'string' } }, required: ['q', 'recommendation'] } },
    glossaryProposals: { type: 'array', items: { type: 'string' } },
    adrCandidates: { type: 'array', items: { type: 'string' } },
    summary: { type: 'string' },
  },
  required: ['questions', 'glossaryProposals', 'adrCandidates', 'summary'],
}
const REPORT = {
  type: 'object',
  properties: {
    questions: { type: 'integer' }, mainCheckoutClean: { type: 'boolean' }, leftovers: { type: 'string' },
    issue: { type: 'integer' }, url: { type: 'string' },
  },
  required: ['questions', 'mainCheckoutClean', 'leftovers', 'issue', 'url'],
}

// ---------------------------------------------------------------- prompts
const BASE_REF = BASE || '<base>'  // what the collect prompt calls the base before its SETUP 1 resolves it
const collectPrompt = `Collect tonight's tickets for the SPA platform. Repo (main checkout): ${REPO}. Base commit: ${BASE || 'none given; SETUP 1 resolves it'}. Integration branch: ${INT_BRANCH}. Date: ${DATE}.
${HOUSE}

SETUP, in order:
0. The shell, before anything else and whatever happens after: run \`echo "BASH_VERSION=$BASH_VERSION"; ps -p $$ -o comm=\` as one Bash call and return what it prints: "shell" is the value after BASH_VERSION= ("" when it is empty), "shellCommand" the line ps prints (e.g. /bin/zsh). Return both on "dirty-start" too. The script halts the run when "shell" is empty (#129); do not try to fix the shell yourself.
1. \`git -C ${REPO} status --porcelain --untracked-files=no\` must print nothing: no tracked file may differ. Untracked and ignored files do not count, because every build runs in a worktree. ${BASE ? `\`git -C ${REPO} rev-parse HEAD\` must equal ${BASE}.` : `No base was given, so the base is \`git -C ${REPO} rev-parse HEAD\`, and it must equal the sha \`git -C ${REPO} ls-remote origin refs/heads/main\` prints (read-only, so nothing in the main checkout's .git changes). Below, <base> is that full sha.`} Otherwise return outcome "dirty-start" with the reason, and do nothing else.
2. If branch ${INT_BRANCH} exists it must be ${BASE_REF} or a descendant of it (\`git -C ${REPO} merge-base --is-ancestor ${BASE_REF} ${INT_BRANCH}\`); otherwise "dirty-start". If it does not exist, \`git -C ${REPO} worktree add -b ${INT_BRANCH} ${INT_WT} ${BASE_REF}\`. If it exists but ${INT_WT} is not a worktree, \`git -C ${REPO} worktree add ${INT_WT} ${INT_BRANCH}\`.
3. \`mkdir -p ${LOG} ${RESEARCH}\`, then record what is untracked before the run starts: \`git -C ${REPO} status --porcelain --untracked-files=all > ${LOG}/untracked-at-start.txt\`. The report compares against it. That file is the only thing this step writes to disk.

COLLECT. Read docs/plan/03-workstreams-and-tasks.md in full (every task table and the "Dispatch order" table), docs/plan/01-plan.md's gates table, docs/agents/landing-work.md, docs/agents/issue-tracker.md, OWNERSHIP.md, AGENTS.md, contracts/README.md, and every contracts/changes/*.md.
- LANDED: run \`cd ${REPO} && python3 -I tools/roadmap.py --json\` and read its "rows": a task id is landed when its state is "landed", which is how docs/roadmap.md counts it, merges inside merges included. "part-landed" and "open" are not landed, and "ongoing" (I-7, 6.8) is SKIP. Gates: its "closed_gates". If it exits non-zero, say so in "reason" and stop rather than guess (#70).
- CLAIMED: a task with a branch \`task/<id>-...\` (local or origin) that is not landed. It is Adam's to merge in the morning; status "claimed", no ticket work.
- SKIP: rows whose Needs is "Ongoing" (I-7, 6.8) and landed rows. List landed ids in "landed".

TICKETS come from GitHub Issues, one list per triage label (\`--label a,b\` is an AND filter, so three calls):
  cd ${REPO} && gh issue list --state open --label ready-for-agent --limit 500 --json number,title,body,labels,assignees,url,blockedBy
  cd ${REPO} && gh issue list --state open --label ready-for-human --limit 500 --json number,title,body,labels,assignees,url,blockedBy
  cd ${REPO} && gh issue list --state open --label needs-triage --limit 500 --json number,title,body,labels,assignees,url,blockedBy
An open issue carrying none of the three (a parent such as #${PARENT_ISSUE}, an Overnight report) is not a ticket. Nor is an issue whose body carries a \`**Session:** foreground\` line: its work happens in a foreground session in the main checkout (a git-ignored workflow file no lane may write); leave it out of tickets and name it in "reason" as foreground-only. Each listed issue is ONE ticket:
- issue: its number; url: its url; title: its title.
- id: the X of a \`**Plan row:** X\` line in the body, else \`issue-<number>\`; a value of \`none\` (or an empty value) counts as absent, so a spec issue filed with \`**Plan row:** none\` gets \`issue-<number>\`, never the id \`none\`. slug: a short kebab-case slug of the title.
- owner: the \`**Owner:**\` line mapped to the OWNERSHIP.md role: ws1-infra (workstream 1, AWS and IaC, infra/), ws2-hosting (workstream 2, Registry/Hosting/spactl), ws3-identity (workstream 3, Identity), ws4-proxy (workstream 4, Proxy/Security), ws5-fixtures (workstream 5, fixtures/), ws6-verification (workstream 6, Telemetry, tests/Gateway.*, e2e, runbooks), integrator (everything else, and every I-* row). A body that says "workstream 2 (...)" maps the same way. When it names more than one owner, the ticket's owner is the first named and "why" says the others land in their own PRs.
- blockedBy: the ids (by the rule above; \`issue-<number>\` for a blocker that is not in tonight's set) of the issues in \`blockedBy.nodes\` whose state is OPEN. Closed blockers do not count.
- status (exactly one, in this order of precedence):
  * label ready-for-human -> "ready-for-human" (whether or not it has an assignee);
  * label needs-triage -> "needs-triage";
  * label ready-for-agent with an assignee, or whose plan row is CLAIMED by a branch -> "claimed";
  * label ready-for-agent whose every open blocker is itself a ticket in tonight's set that resolves to "ready-for-agent" by these rules -> "ready-for-agent", with blockedBy naming those blockers' ids: the lane runs it after they integrate, which is how the G2 chain landed on 2026-10-17;
  * label ready-for-agent with any other open blocker (a human row, a claimed row, a blocked row, an issue outside tonight's set) -> "blocked", blockedBy naming them;
  * label ready-for-agent otherwise -> "ready-for-agent".
  Do not re-classify an issue by reading its plan row: the labels are the tracker's state. "why" is one sentence saying which rule fired and, for blocked, what it waits on.
- openBoxes: the number of lines in the issue body matching \`^\\s*- \\[ \\]\` (open acceptance boxes), or -1 when the body has no checkbox at all (no \`- [ ]\` and no \`- [x]\` line). Count it, do not estimate: \`cd ${REPO} && gh issue view <n> --json body --jq .body | python3 -I -c 'import re,sys; b=sys.stdin.read(); o=len(re.findall(r"(?m)^\\s*- \\[ \\]", b)); print(o if re.search(r"(?m)^\\s*- \\[[ xX]\\]", b) else -1)'\`. A ready-for-agent ticket with 0 gets no lane tonight (every box is ticked; Adam closes it in the morning).
- n: the ticket's ordinal, 1.. in plan order (rows in the plan's order first, then \`issue-<number>\` tickets by number). It picks the lane's port, so every ticket needs a distinct one.
Then, for every id in "landed", say in "reason" if an open issue still carries that plan row (Adam closes it; do not close it yourself).

NEW TICKETS. Every open row of docs/plan/03 that is not landed, not SKIP, and not present as an issue (\`cd ${REPO} && gh issue list --state all --limit 500 --json number,body --jq '.[] | select(.body | test("\\\\*\\\\*Plan row:\\\\*\\\\* ")) | .number'\` and read their Plan row lines; any state counts) gets one, as a sub-issue of #${PARENT_ISSUE}:
  cd ${REPO} && gh issue create --title "<id> — <short title>" --label <ready-for-agent|ready-for-human> --parent ${PARENT_ISSUE} --body-file - <<'EOF'
  **Owner:** <role>
  **Plan row:** <id>

  **What to build:** the row's What, the contracts/ rows and test ids that name it (cite paths), and its Done when.
  ## Acceptance
  - [ ] one box per Done-when clause and per test id
  EOF
Choose the label by this rule: ready-for-agent when the repo part is provable on this Mac by build, tests, or terraform validate; it writes only its owner's paths; and every Need is landed or is itself an issue (then \`--blocked-by <that issue's number>\`, which also makes tonight's status "blocked"). Terraform rows count: written and validated, not applied, is how 1.0 and 1.6a landed. A row needing tooling not installed here is not ready-for-agent. The headless-browser test is NOT \`which chromium\`: Playwright keeps its browsers under ~/Library/Caches/ms-playwright. Read the Microsoft.Playwright version from Directory.Packages.props and the chromium revision from ~/.nuget/packages/microsoft.playwright/<version>/.playwright/package/browsers.json; the browser is present when ~/Library/Caches/ms-playwright/chromium-<revision> exists. ready-for-human when the next step is a human's: an AWS apply or credentials, a gate decision (a gate is closed when the JSON's "closed_gates" lists it, and open otherwise), an integrator decision, an open change request, a decision the plan leaves open. The owner is the workstream's OWNERSHIP.md role; integrator for I-* rows. Add each created issue to "tickets" with the status its label and blockers give it, and name each one in "reason" ("created #<n> for <id>"). An open change request (contracts/changes/*.md with Status open) is not a ticket of its own tonight: name it in "reason" and in the "why" of every ticket it holds up.

Return outcome "ok", tickets (sorted by n), landed, reason, base: the base's full sha (on "dirty-start", whatever HEAD is), and shell and shellCommand from SETUP 0.`

const routePrompt = (t) => `Route one unclear ticket: issue #${t.issue} (${t.url}; task ${t.id}). Read ${SKILLS}/ask-matt/SKILL.md, and before relying on any skill it names, that skill's SKILL.md. Read the issue (\`cd ${REPO} && gh issue view ${t.issue} --json title,body,comments\`: the body is the spec, the comments its record), its plan row in ${REPO}/docs/plan/03-workstreams-and-tasks.md, and anything it cites.
Decide the flow: "agent" (an AFK agent can build and prove it tonight in its owner's paths), "human" (a decision or an action only Adam can take), or "research-then-human" (a primary-source question must be answered before Adam can decide). When in doubt, "human".
For research-then-human: ${skill('research')}, writing the findings to ${RESEARCH}/${t.id}.md and nowhere else; that is the only file this step writes.
Then record the routing on the issue: one comment headed "## Routing" with your reason (and, for research, the line "Research: ${RESEARCH}/${t.id}.md"), then swap the label: \`cd ${REPO} && gh issue edit ${t.issue} --remove-label needs-triage --add-label ready-for-agent\` for "agent", \`--add-label ready-for-human\` otherwise. Write nothing else. Do not build.`

const OWNER_NOTES = {
  'ws3-identity': `
WORKSTREAM 3, read before designing anything:
- src/Platform.Identity holds the whole chain (3.1 to 3.9). Extend it; do not re-port spikes/g0, and never reference spikes/.
- ADR-0005 as amended 2026-10-16 is binding: gateway-owned refresh locking; ClientCredentialStyle governs refresh and revocation, not the code exchange; a provider outage is AccessTokenResult.ProviderUnavailable (change request 4.2, decided), never an exception.
- session_ref and the auth.* rule ids are written through IRequestTelemetryFeature; Platform.Abstractions.RuleIds is the only source of a rule id (ADR-0015).
`,
}

const implPrompt = (t) => `Implement ticket ${t.n} (task ${t.id}, owner ${t.owner}): issue #${t.issue}, ${t.url}. Unattended overnight.
${HOUSE}

Worktree: ${WT(t.owner)}. Branch: task/${t.id}-${t.slug}. Your port: ${PORT(t.n)}.
${OWNER_NOTES[t.owner] || ''}
STEP 0 — durable state (this may be a resumed run):
- First, the issue's state now: \`cd ${REPO} && gh issue view ${t.issue} --json state --jq .state\`. Tonight's tickets are a snapshot taken at collect, so a ticket can close before its lane starts (#108). If it prints CLOSED, return outcome "skipped" with summary "closed before the lane started", branch "", forkSha "", headSha "", complete false, outstanding "", question "" and no decisions; do nothing else and write nothing.
- If branch task/${t.id}-${t.slug} exists and its log (\`git -C ${REPO} log ${INT_BRANCH}..task/${t.id}-${t.slug} --format=%s\`) has a commit whose subject starts "Task ${t.id}:", the implementation is done: make sure the worktree has it checked out (\`git -C ${REPO} worktree add ${WT(t.owner)} task/${t.id}-${t.slug}\` if ${WT(t.owner)} is missing, else \`git -C ${WT(t.owner)} checkout task/${t.id}-${t.slug}\`), read the issue's state and its "## Answer" comment (\`cd ${REPO} && gh issue view ${t.issue} --json state,labels,comments\`), and return those facts (forkSha = \`git -C ${REPO} merge-base ${INT_BRANCH} task/${t.id}-${t.slug}\`). Do no other work.
- If the branch exists without that commit, check it out as above and continue the work on it; read the issue's comments once for what an earlier attempt recorded.
- Otherwise fork from the integration tip: forkSha = \`git -C ${REPO} rev-parse ${INT_BRANCH}\`. If ${WT(t.owner)} is not a worktree: \`git -C ${REPO} worktree add -b task/${t.id}-${t.slug} ${WT(t.owner)} <forkSha>\`. If it is, it must be clean (\`git -C ${WT(t.owner)} status --porcelain\` empty; if not, return "failed" saying so), then \`git -C ${WT(t.owner)} checkout -b task/${t.id}-${t.slug} <forkSha>\`.
Claim the issue: \`cd ${REPO} && gh issue edit ${t.issue} --add-assignee @me\`. The branch remains the claim git sees.

STEP 1 — build it, following ${SKILLS}/implement/SKILL.md (read it; it is not callable) with two changes: do NOT run its code-review step (a separate reviewer does that next), and the ticket is GitHub issue #${t.issue}: read its body (\`cd ${REPO} && gh issue view ${t.issue} --json title,body\`), which is the spec and the acceptance list; the comments are history and you need them only in the resumed cases of STEP 0.
- Read the ticket, its plan row (${REPO}/docs/plan/03-workstreams-and-tasks.md), the contracts/ files it names, GLOSSARY.md, docs/agents/domain.md, the ADRs in the area, CODING_STANDARDS.md.
- Launch every sub-agent with \`run_in_background: false\` and wait for each report: a background sub-agent lets you hand back before it reports.
- Build test-first: ${skill('tdd')}. Nobody can confirm seams tonight: choose the seams yourself at the contract's public interfaces, and list them in the Answer as "Seams (chosen by implementer)". Prove each test id the row names in your owner's test project, against the Platform.Testing doubles.
- While working, run single test projects (\`cd ${WT(t.owner)} && dotnet test --project tests/<X>.Tests\`).
- At the end, run the gate once:
${GATE({ dir: WT(t.owner), log: `${LOG}/${t.id}-implement.txt`, kind: 'task', fork: '<forkSha>', port: PORT(t.n) })}
  Fix and re-run until green, at most three runs in total.

STEP 2 — outcomes. Every record below is a comment on issue #${t.issue} (\`cd ${REPO} && gh issue comment ${t.issue} --body-file - <<'EOF' ... EOF\`), with the heading named as the comment's first line; nothing is written to a ticket file.
- Needs an integrator path (a contract, a package, a new project, anything in src/Platform.Abstractions or Platform.Testing): write contracts/changes/${t.id}-<slug>.md in the worktree in the shape of the existing ones (Status: open), build what you can without it, and land partial: complete=false, outstanding says what waits on the request.
- Human-owned decision (the plan, contracts and ADRs are silent or contradict, and the choice is hard to reverse): ${STANDING ? "Adam's standing answer, granted for this run (" + DATE + "), is that he accepts the implementer's recommendation, so take it and keep going, on one condition: the recommendation is weighed, not asserted. It covers the code on your own branch and nothing else (ADR-0023 decision 1): on it no steering document changes, no ticket body is rewritten beyond its boxes, no ticket is filed and nothing merges." : "No standing answer was granted for this run, so every human-owned decision goes the \"## Status note\" way described at the end of this bullet, and the \"## Decisions taken\" route below is closed tonight."} In a comment headed "## Decisions taken" write, for each: the question; at least two real alternatives, each with what it costs and what it breaks, citing the plan row, contract or ADR that bears on it; the one you took and why it beats the others on those terms; what evidence would show it was wrong, and how it is reversed. A decision whose alternatives are strawmen, or whose choice contradicts a frozen contract or an accepted ADR, is not yours to take: that one goes in a comment headed "## Status note" with the question, the options and your recommendation, then \`cd ${REPO} && gh issue edit ${t.issue} --remove-label ready-for-agent --add-label ready-for-human\`, a commit with subject "Task ${t.id}: partial, question for Adam", and outcome "needs-human". List every decision taken in delegatedDecisions prefixed "Human-owned:", to tell it from a gap decision.
- Steering documents (GLOSSARY.md, AGENTS.md, CLAUDE.md, OWNERSHIP.md, CODING_STANDARDS.md, docs/agents/, docs/adr/, contracts/; adding a change request under contracts/changes/ is not a change to one): a new term or guide sentence you need is a proposal, not an edit. Put its exact text under a heading "## Proposed steering changes" in your "## Answer" comment and change no steering file for it; the report puts each proposal to Adam, and once he confirms it a session commits it on its own branch (ADR-0023 decision 4). The one steering change you commit is one this ticket's acceptance criteria ask for, in a commit whose message ends with the trailer "Authority: #${t.issue}" (ADR-0023 decision 3); the reviewer holds any other.
- Cannot get green (or it needs AWS, network the machine refuses, or another owner's path): a comment headed "## Status note" saying what blocks it and what you tried; \`cd ${REPO} && gh issue edit ${t.issue} --remove-assignee @me\` (the label stays ready-for-agent); commit what exists with subject "Task ${t.id}: attempt, not green"; return "failed".
- Green: tick the boxes you can point at (${TICK(t, 'tick')}), then a comment headed "## Answer" (what landed, where, seams, the check's last line, and an "Outstanding:" line if partial), and commit everything in the worktree with subject "Task ${t.id}: <what landed>" and a short body. The Answer says where each part went, the branch and the sha, and that the issue closes when that lands on main. Never close the issue: complete or partial, it stays open and assigned until the landing pull request's \`Closes #${t.issue}\` line closes it on the merge into main (#100; docs/agents/issue-tracker.md). A partial row's Answer carries its Outstanding line.
- No commit: if the lane makes no commit (\`git -C ${WT(t.owner)} rev-parse HEAD\` equals forkSha), you claimed an issue you did no work on, so remove the assignee before returning: \`cd ${REPO} && gh issue edit ${t.issue} --remove-assignee @me\`.
Gap decisions that are not human-owned you may take; list each in delegatedDecisions and in the Answer as "Decided by implementer".

Return outcome, branch, forkSha, headSha (\`git -C ${WT(t.owner)} rev-parse HEAD\`), complete, outstanding ("" if none), summary (2-4 sentences), question ("" if none), delegatedDecisions, proposedSteering (the exact text of each entry under "## Proposed steering changes" in your "## Answer" comment; [] if none).`

const reviewPrompt = (t, impl) => `Review ticket ${t.n} (task ${t.id}, issue #${t.issue}, ${t.url}) before it integrates. You did not write it. Unattended overnight.
${HOUSE}

Worktree ${WT(t.owner)}, branch ${impl.branch}, fixed point ${impl.forkSha}. Implementer's summary (orientation only): ${impl.summary}

STEP 0 — durable state: read the issue once, \`cd ${REPO} && gh issue view ${t.issue} --json title,body,comments\` (the body is the spec; the comments hold the implementer's "## Decisions taken" and "## Answer"). If \`git -C ${WT(t.owner)} log -1 --format=%s\` starts with "Review:" or a comment headed "## Review" already exists, the review is done: return "committed" with the current HEAD and the findings recorded there.

STEP 1 — the gate, once. If the first line of ${LOG}/${t.id}-implement.txt is "HEAD=<sha>" equal to \`git -C ${WT(t.owner)} rev-parse HEAD\` and its last line starts "check: pass:", copy it to ${LOG}/${t.id}-review.txt and do NOT rebuild. Otherwise run:
${GATE({ dir: WT(t.owner), log: `${LOG}/${t.id}-review.txt`, kind: 'task', fork: impl.forkSha, port: PORT(t.n) })}

STEP 2 — ${skill('code-review')}, fixed point ${impl.forkSha}, in ${WT(t.owner)}. The spec is issue #${t.issue}'s body plus its plan row and the contracts/ rows naming the task (landing-work.md "Review": there is no per-task spec file). Standards sources include CODING_STANDARDS.md. Tell both axes: the tests have been run and their output is ${LOG}/${t.id}-review.txt; read it rather than running dotnet. CODING_STANDARDS.md asks a runtime claim to be verified by a run — that file is the run; a claim it does not cover may run one test project, never the solution.
Launch every sub-agent with \`run_in_background: false\` and wait until each axis has reported before you write anything: a background sub-agent lets you hand back before it reports. If sub-agents are unavailable, do both axes yourself in turn and keep the two reports separate.
Steering documents (#103): for every path in the diff under GLOSSARY.md, AGENTS.md, CLAUDE.md, OWNERSHIP.md, CODING_STANDARDS.md, docs/agents/, docs/adr/ or contracts/ (contracts/changes/ excepted), read the trailer of the commit that changed it, \`git -C ${WT(t.owner)} log --format='%h %(trailers:key=Authority,valueonly)' ${impl.forkSha}..HEAD -- <path>\`, and the item it cites. Authority is an acceptance criterion of issue #${t.issue} that asks for that change, or a confirmed answer; a pre-accepted answer, a proposal, a follow-up a night raised and a reviewer's finding are none (ADR-0023 decision 3). A change with no Authority trailer, or one whose cited item does not decide it, is held: take it out of the branch (restore the file to ${impl.forkSha} when the whole change lacks authority, else edit the sentence out, and commit), put its exact text under "## Proposed steering changes" in your "## Review" comment, and list it there under "Held". Make no steering change of your own; propose it the same way.
Then read the "## Decisions taken" comment, if any. Each human-owned decision the implementer took is a finding on the Spec axis unless its alternatives were real (not strawmen), its costs were stated against the plan row, contract or ADR that bears on it, and the choice holds against every frozen contract and accepted ADR. Re-weigh each one yourself; where the implementer's choice loses on those terms, reverse it in code and say so in your "## Review" comment under "Decisions reversed".

STEP 3 — fix every finding you verify as real; for each one you reject, say why. If you changed files, re-run the gate above into ${LOG}/${t.id}-review.txt (at most twice) and keep it green. Untick any acceptance box that is not actually met (${TICK(t, 'untick')}). Post one comment headed "## Review" on issue #${t.issue} (both axes' findings, what you fixed, what you skipped and why, decisions upheld or reversed). Commit with subject "Review: <what changed>" (a review with no code change commits nothing; say so). The issue stays open whatever you tick or untick: the landing closes it (#100).
A human-owned decision of your own: same rule as the implementer, weigh it and record it in a comment headed "## Decisions taken", or hand it over in a comment headed "## Status note" plus \`cd ${REPO} && gh issue edit ${t.issue} --remove-label ready-for-agent --add-label ready-for-human\` and outcome needs-human when it is not yours to take. Cannot get green: a "## Status note" comment, \`cd ${REPO} && gh issue edit ${t.issue} --remove-assignee @me\`, commit what exists, return failed.

Return outcome, headSha, findingsFixed, findingsSkipped (with reasons), summary, question ("" if none), proposedSteering (the exact text of each entry under "## Proposed steering changes" in your "## Review" comment, held ones included; [] if none).`

const integratePrompt = (t, impl, rev) => `Integrate ticket ${t.n} (task ${t.id}, issue #${t.issue}) into ${INT_BRANCH}. You are the only integrator running.
${HOUSE}

Integration worktree ${INT_WT} (branch ${INT_BRANCH}). Task branch ${impl.branch}. Partial row: ${impl.complete ? 'no' : 'yes — outstanding: ' + impl.outstanding}.
The implementer's summary: ${impl.summary}
The reviewer's summary: ${rev.summary}
Findings the review fixed: ${(rev.findingsFixed || []).join(' | ') || 'none'}.
These, the task branch's own commit messages and the gate logs under ${LOG} are the merge body's sources.${impl.complete ? '' : ` For the "Outstanding test ids:" line, read the issue body's acceptance list alone: \`cd ${REPO} && gh issue view ${t.issue} --json body --jq .body | awk '/^## /{p=($0=="## Acceptance")} p'\`; an unticked box naming a test id is outstanding.`}

STEP 0 — durable state: if \`git -C ${INT_WT} rev-parse -q --verify MERGE_HEAD\` succeeds, \`git -C ${INT_WT} merge --abort\`. If \`git -C ${INT_WT} merge-base --is-ancestor ${impl.branch} HEAD\` succeeds, the merge is already in: if the last line of ${LOG}/${t.id}-integrate.txt starts "check: pass:" and its first line is "HEAD=" the current HEAD, return ok with that merge's sha; otherwise skip to the gate.

STEP 1 — merge. Record old=\`git -C ${INT_WT} rev-parse HEAD\`. \`git -C ${INT_WT} merge --no-ff ${impl.branch}\` with the message from docs/agents/landing-work.md "Land it":
- subject "${impl.complete ? `Merge task ${t.id}: <what landed>` : `Merge part of task ${t.id}: <what landed>`}"
- body: what was verified and how (the gate, the review's findings fixed), the line "Ticket: #${t.issue}" (ADR-0020 decision 4: the merge body cites the issue it came from, as #<n>, never by file path), ${impl.complete ? '' : 'an "Outstanding:" line and an "Outstanding test ids:" line, '}then the attribution line the house rules name.
A conflict: \`git -C ${INT_WT} merge --abort\` and return ok=false naming the files.
Then run \`python3 -I tools/roadmap.py\` in ${INT_WT}; if it changed README.md or docs/roadmap.md, commit those two alone there, subject "Refresh the generated roadmap blocks after merging ${t.id}". The check below runs the roadmap check, and a lane forked from this tip cannot fix docs/roadmap.md from another owner's paths.

STEP 2 — the integration gate:
${GATE({ dir: INT_WT, log: `${LOG}/${t.id}-integrate.txt`, kind: 'integration', fork: BASE, port: PORT(t.n) })}
Red: \`git -C ${INT_WT} reset --hard <old>\` and return ok=false with the failing lines. Do not fix code here.

Return ok, mergeSha (the merge commit, or "" on failure), notes.`

const grillPrompt = (t, context) => `Grill ticket ${t.n} (task ${t.id}) on paper for Adam: issue #${t.issue}, ${t.url}. Adam is asleep and will answer in bulk tomorrow.
${context ? `Context from tonight: ${context}\n` : ''}
STEP 0, do not grill twice. Read the issue once: \`cd ${REPO} && gh issue view ${t.issue} --json title,body,comments\` (the body is the spec; the comments are the record). If a comment headed "## Questions for Adam" already exists and no context from tonight is given above, it was grilled on an earlier night: return its existing questions (q, recommendation), glossaryProposals and adrCandidates read from the "## Proposed glossary / ADR" comment, summary "carried from an earlier night, not re-grilled", and write nothing. Only an issue with no such comment, or one with context from tonight, is grilled below.

${STANDING ? "ADAM'S STANDING ANSWER, granted for this run (" + DATE + "): he accepts the recommendation on every question. Under each question write the line \"Answer: yes, pre-accepted under the standing answer granted " + DATE + "\", and in later rounds treat the recommendation as decided rather than provisional. A pre-accepted answer is provisional until Adam confirms it on the Overnight issue (docs/agents/issue-tracker.md), so still write the question and the recommendation in full." : "NO STANDING ANSWER was granted for this run. Under each question write the line \"Answer: open\". To reach the next round, assume your recommendation provisionally and say so; nothing is decided until Adam answers."}

Follow ${SKILLS}/grill-with-docs/SKILL.md (read it; it is not callable): ${skill('grilling')}, then ${skill('domain-modeling')}.
Run the design tree ON PAPER, because nobody can answer: write round 1 in the grilling format (numbered ❓ question with its options, ➡️ your recommendation), ADOPT each recommendation as decided when a standing answer was granted, and as provisional when not (the rule above), recompute the frontier, write the next round; stop when the frontier is empty and nothing is silently assumed. Facts are your job: read the repo (${REPO}: the plan, contracts/, docs/adr/, GLOSSARY.md, the code, \`git log\`), never guess and never ask Adam a fact. Decisions are Adam's: every one goes in the questions.
Domain modelling: challenge the ticket's terms against GLOSSARY.md, sharpen fuzzy ones, test them on concrete scenarios, cross-check the code.

Write ONLY to issue #${t.issue}, as three comments in this order (\`cd ${REPO} && gh issue comment ${t.issue} --body-file - <<'EOF' ... EOF\`, the heading as the first line of each), and nothing to disk:
- "## Grilling record": every round, in order.
- "## Questions for Adam": numbered, each worded so "yes" accepts your recommendation, each with the recommendation, a one-line why, and its "Answer:" line as the standing-answer rule above dictates.
- "## Proposed glossary / ADR": proposed GLOSSARY.md terms; ADR candidates only where all three of domain-modeling's criteria hold. Title each candidate \`ADR draft: <title>\`, cite it by that title, and leave its number to the session that commits it, which takes the next free one then, even though domain-modeling's ADR format numbers one: a scripted session drafts an ADR and never commits one (ADR-0021 decision 4; docs/adr/README.md).
Edit nothing in the repo, GLOSSARY.md and docs/adr/ included, even though domain-modeling says to update inline: a glossary term or an ADR is a steering change, and a steering change rests on a confirmed answer (ADR-0023 decisions 3 and 4). The route is the "## Proposed glossary / ADR" comment above: the report puts each proposal to Adam, and once he confirms it a session commits it on its own branch with an Authority: trailer naming the confirmation. Do not run dotnet. Do not edit the body, the labels (ready-for-human stays) or the assignees.

Return the questions (q, recommendation), glossaryProposals, adrCandidates, and a 2-sentence summary.`

const reportPrompt = (summary) => `Write the overnight report as one GitHub issue. Repo ${REPO}, base ${BASE}, integration branch ${INT_BRANCH} (worktree ${INT_WT}), gate logs in ${LOG}. The tickets are GitHub issues; "tickets" below lists each one's id, issue number and status, and every record a lane wrote tonight is a comment on its issue (\`cd ${REPO} && gh issue view <n> --json title,body,comments\`).
Run results (JSON): ${JSON.stringify(summary)}
The results JSON already holds what the lanes returned (#129): each grill's full questions (q, recommendation), glossaryProposals and adrCandidates under results.<id>.grill, and each implementer's and reviewer's proposedSteering (the exact text of every "## Proposed steering changes" entry) under results.<id>.proposedSteering (an integrated ticket keeps it as {implement, review}; any other keeps the stopping stage's list there) and results.<id>.impl.proposedSteering wherever the implementer's result is kept. Take them from there. Read an issue's comments only for what the JSON lacks: a "## Status note" question, the "## Decisions taken" alternatives and the review's verdict on them, or the records of a resumed run.

0. Run \`python3 -I tools/roadmap.py\` in ${INT_WT}; if it changed README.md or docs/roadmap.md, commit those alone there, subject "Refresh the generated roadmap blocks for the ${DATE} run's merges". That commit is integrator-only, so the per-commit ownership check holds. Nothing else is committed, and nothing is written into ${REPO} outside ${LOG}.
1. The report issue, created once at the end of this step with \`cd ${REPO} && gh issue create --title "Overnight ${DATE}" --body-file - <<'EOF' ... EOF\` (no label, no parent; if an open issue titled "Overnight ${DATE}" already exists from a resumed run, edit its body with \`gh issue edit <n> --body-file -\` instead of creating a second). Its body holds no question text: the questions are comments, step 1b below (#104). The body, for Adam first thing in the morning:
   - first line: "Standing answer: ${STANDING_LINE}." Adam confirms pre-accepted answers with one comment on this issue beginning "Confirm" (overrides as "Q<n>: <answer>" lines); until then they are provisional;
   - one line: N integrated, N partial, N skipped, N failed, N need you, N blocked;
   - Integrated: task, #issue, merge sha, what landed (from \`git -C ${REPO} log --oneline ${BASE}..${INT_BRANCH}\`);
   - Close in the morning: every ticket whose result's "why" is "every acceptance box is ticked; close it in the morning": no lane ran, because collect found no open acceptance box on its issue (#129); cite each as #<issue> so Adam closes it, or reopens a box if one is not met;
   - Partial (Outstanding), Skipped (its "why", Close in the morning tickets excepted: closed before its lane started, or a lane that changed nothing, #108), Failed (the last lines of its check log, the issue's "## Status note" comment), Needs you, Blocked (what each waits on, as #issue), Claimed branches to merge; every ticket cited as #<issue>;
   - Decisions taken overnight: every delegatedDecisions entry, grouped by task, the "Human-owned:" ones first, each with the alternatives it was weighed against (from the issue's "## Decisions taken" comment) and whether the review upheld or reversed it (its "## Review" comment);
   - Pending tracker writes: if ${PENDING} exists, how many lines it holds and that the morning session replays them (a comment, label change or close that GitHub refused tonight); otherwise "none";
   - Landing pull request (#100): the description Adam opens the pull request with, as a fenced block ready to paste: a title naming the run, then one line \`Closes #<issue>\` per ticket whose outcome is integrated and whose lane reported complete, and one line \`Ticket: #<issue>\` per partial one. GitHub closes the \`Closes\` issues when \`tools/land.sh\` merges the pull request into main; nothing closes a partial ticket.
   - Morning steps: answer the questions (the comments below) as comments on this issue; \`git -C ${INT_WT} push -u origin ${INT_BRANCH}\` with the pre-push hook running (it reads a branch that merges single-owner lanes commit by commit; never --no-verify); open one PR with the description above; a fresh top-level agent session, never a sub-agent of the session that launched this run, reviews its exact head and posts the review record, and \`tools/land.sh <pr> --subject "..." --body-file <path>\` merges it (ADR-0022; docs/agents/landing-work.md). Each ticket's task merge on ${INT_BRANCH} is that ticket's merge body, so a box asking for something "in the merge body" is met by the task merge: the landing body cites those merges by sha and copies none of their lists; merge the claimed branches in their own PRs; close the issue of every landed row that is still open, the fallback when a \`Closes\` line was missed, and unassign the partial rows' issues once their branches are merged; \`git worktree remove\` each worktree under ${WTROOT}, then \`git -C ${REPO} worktree prune\`.
   - Leftovers: \`lsof -nP -iTCP -sTCP:LISTEN | grep -E 'dotnet|Gateway|MockApi'\` and \`pgrep -fl ${WTROOT}\`; kill nothing, list it.
1b. The questions, as comments on the report issue (#104), one comment per ticket that has any, \`cd ${REPO} && gh issue comment <report issue> --body-file - <<'EOF' ... EOF\`, posted in the order that puts the questions unblocking the most work first. Each comment is headed "## Questions: #<issue>, <task id>, <title>" and lists, numbered Q1..Qn continuously across all the comments so that one "Confirm" comment names them as before (ADR-0021 decision 2): that ticket's questions from its "## Questions for Adam" and "## Status note" comments tonight, each as the question, "Recommended:", and the "Answer:" line as the comment records it ("Answer: yes, pre-accepted under the standing answer granted <date>" when one was granted; "Answer: open" when none was, and for a status note's question); then every entry under "## Proposed steering changes" in its "## Answer" and "## Review" comments, each as a question whose "yes" commits that exact text, with "Answer: open", because a steering change rests on a confirmed answer alone (ADR-0023; #103). A grill whose summary says "carried from an earlier night" contributes none: name those tickets in one closing comment headed "## Carried, already answered". Before filing anything, measure the body and every comment: GitHub refuses a body or comment over 65,536 characters, so split any that would pass 60,000 into consecutive parts headed "(part k of m)", dropping nothing. "questions" in your return is the count across every comment.
2. Check \`git -C ${REPO} status --porcelain --untracked-files=no\` is empty (the lanes write no tracked file in the main checkout any more; a modified tracked file is a finding: list it), that \`git -C ${REPO} status --porcelain --untracked-files=all\` lists nothing ${LOG}/untracked-at-start.txt does not (an untracked file the run left is a finding: list it, and delete nothing; ${LOG} and ${RESEARCH} are not findings when git ignores them), and that \`git -C ${REPO} rev-parse HEAD\` is ${BASE}; record the result in the report body before you create the issue.
Return questions (the count), mainCheckoutClean, leftovers, issue (the report issue's number) and url.`

// ---------------------------------------------------------------- concurrency helpers
let slots = MAX_PAR
const waiters = []
const acquire = () => new Promise((res) => { if (slots > 0) { slots--; res() } else waiters.push(res) })
const release = () => { const w = waiters.shift(); if (w) w(); else slots++ }
const withSlot = async (fn) => { await acquire(); try { return await fn() } finally { release() } }
let intLock = Promise.resolve()
const serial = (fn) => { const p = intLock.then(fn, fn); intLock = p.catch(() => {}); return p }

// ---------------------------------------------------------------- run
phase('Collect')
const col = await agent(collectPrompt, { label: 'collect', phase: 'Collect', schema: COLLECT })
if (!col) return { halted: 'collect agent died' }
if (col.outcome !== 'ok') return { halted: `dirty-start: ${col.reason}` }
// The Bash tool must be bash (#129): on 2026-10-19 every agent ran /bin/zsh. Halt before Route, as the bad-launch
// refusal does, so no lane starts in the wrong shell.
if (!col.shell) return { halted: `the Bash tool runs ${col.shellCommand || 'zsh'}, not bash; restart Claude Code so CLAUDE_CODE_SHELL=/bin/bash takes effect, then relaunch`, shellCommand: col.shellCommand || '' }
BASE = col.base || BASE

const inScope = (t) => !SKIP.has(t.id) && (!ONLY || ONLY.includes(t.id))
const tickets = col.tickets
log(`collected ${tickets.length}: ` + ['ready-for-agent', 'ready-for-human', 'needs-triage', 'blocked', 'claimed']
  .map((s) => `${tickets.filter((t) => t.status === s).length} ${s}`).join(', '))

phase('Route')
const triage = tickets.filter((t) => t.status === 'needs-triage' && inScope(t))
const routed = await parallel(triage.map((t) => () =>
  agent(routePrompt(t), { label: `route:${t.id}`, phase: 'Route', schema: ROUTE })))
triage.forEach((t, i) => { const r = routed[i]; t.status = r && r.flow === 'agent' ? 'ready-for-agent' : 'ready-for-human' })

const agentT = tickets.filter((t) => t.status === 'ready-for-agent' && inScope(t))
const humanT = tickets.filter((t) => t.status === 'ready-for-human' && inScope(t))
const byId = Object.fromEntries(tickets.map((t) => [t.id, t]))

const results = {}      // id -> {stage, outcome, ...}
const grillQueue = []   // grill promises
let failures = 0
let halted = null

const grill = (t, context) => {
  const p = agent(grillPrompt(t, context), { label: `grill:${t.id}`, phase: 'Grill', schema: GRILL })
    .then((g) => { results[t.id] = { ...(results[t.id] || {}), grill: g ? { questions: g.questions, glossaryProposals: g.glossaryProposals, adrCandidates: g.adrCandidates, summary: g.summary } : 'grill agent died' } })
  grillQueue.push(p)
  return p
}
const fail = (t, stage, info) => {
  results[t.id] = { stage, outcome: 'failed', ...info }
  if (++failures >= MAX_FAIL && !halted) { halted = `${failures} failures; no new tickets started`; log(halted) }
  return false
}

// Human tickets grill from the start, alongside the build lanes.
humanT.forEach((t) => grill(t, ''))

// Lanes: one per owner, serial inside, parallel across. Cross-lane blockers via promises.
// A lane runs its first READY ticket, not its next one in plan order (2026-10-20): with the lanes chained in n
// order, a ticket blocked on another lane at the head of its lane idled the lane's unblocked work behind it
// (ws3's free #164 chain behind #151, which waited on the integrator's #145). A ticket is ready when every
// blocker that is itself one of tonight's agent tickets has settled, integrated or not; a lane with nothing
// ready waits for the first of its queue's blockers to settle, then looks again. Order among the ready: by n.
const done = {}      // id -> Promise<boolean integrated>, one per agent ticket, created before any lane starts
const settled = {}   // id -> boolean, once its promise settled
const resolveDone = {}
agentT.forEach((t) => { done[t.id] = new Promise((res) => { resolveDone[t.id] = res }) })
agentT.forEach((t) => { done[t.id].then((ok) => { settled[t.id] = ok }, () => { settled[t.id] = false }) })
const lanes = {}
;[...agentT].sort((a, b) => a.n - b.n).forEach((t) => { (lanes[t.owner] = lanes[t.owner] || []).push(t) })
const laneBlockers = (t) => t.blockedBy.filter((b) => byId[b] && done[b])   // blockers that are tonight's agent tickets
const ready = (t) => laneBlockers(t).every((b) => b in settled)

// Every acceptance box is already ticked (#129): no lane, and it counts as met for the tickets it blocks, settled
// before any lane starts so a dependant in another lane sees it at once. -1 (no checkbox in the body) still gets a lane.
agentT.filter((t) => t.openBoxes === 0).forEach((t) => {
  results[t.id] = { stage: 'implement', outcome: 'skipped', why: 'every acceptance box is ticked; close it in the morning' }
  log(`${t.id}: skipped, every acceptance box is ticked`)
  lanes[t.owner].splice(lanes[t.owner].indexOf(t), 1)
  settled[t.id] = true
  resolveDone[t.id](true)
})

const work = async (t) => {
    const deps = await Promise.all(t.blockedBy.map((b) => (byId[b] && done[b]) ? done[b] : Promise.resolve(!byId[b])))
    // a blocker not in tonight's agent set counts as met only if collect did not list it as a ticket
    if (deps.some((ok) => !ok)) { results[t.id] = { stage: 'implement', outcome: 'skipped', why: `blocker not integrated: ${t.blockedBy.join(', ')}` }; return false }
    if (halted) { results[t.id] = { stage: 'implement', outcome: 'skipped', why: halted }; return false }

    const impl = await withSlot(() => agent(implPrompt(t), { label: `implement:${t.id}`, phase: 'Implement', schema: IMPL }))
    if (!impl) return fail(t, 'implement', { why: 'agent died; worktree may be mid-change' })
    // A ticket closed since collect has no work tonight; it counts as met for the tickets it blocked, as GitHub's
    // blocked-by count, which reads open blockers only, already says (#108).
    if (impl.outcome === 'skipped') { results[t.id] = { stage: 'implement', outcome: 'skipped', why: impl.summary }; log(`${t.id}: skipped, ${impl.summary}`); return true }
    if (impl.outcome === 'needs-human') { results[t.id] = { stage: 'implement', ...impl }; grill(t, `The implementer stopped on: ${impl.question}`); return false }
    if (impl.outcome !== 'resolved') return fail(t, 'implement', impl)
    // A lane that changed nothing has nothing to review or integrate, and its dependants have nothing to build on (#108).
    if (impl.headSha === impl.forkSha) { results[t.id] = { stage: 'implement', outcome: 'skipped', why: 'the lane changed nothing: its head is its fork point, so there was nothing to review or integrate', impl }; log(`${t.id}: skipped, nothing changed`); return false }

    const rev = await withSlot(() => agent(reviewPrompt(t, impl), { label: `review:${t.id}`, phase: 'Review', schema: REVIEW }))
    if (!rev) return fail(t, 'review', { why: 'agent died', impl })
    if (rev.outcome === 'needs-human') { results[t.id] = { stage: 'review', ...rev, impl }; grill(t, `The reviewer stopped on: ${rev.question}`); return false }
    if (rev.outcome !== 'committed') return fail(t, 'review', { ...rev, impl })

    const integ = await serial(() => withSlot(() => agent(integratePrompt(t, impl, rev), { label: `integrate:${t.id}`, phase: 'Integrate', schema: INT })))
    if (!integ || !integ.ok) return fail(t, 'integrate', { why: integ ? integ.notes : 'agent died', impl, rev })

    results[t.id] = { stage: 'integrated', outcome: impl.complete ? 'integrated' : 'partial', mergeSha: integ.mergeSha, outstanding: impl.outstanding,
      summary: impl.summary, findingsFixed: rev.findingsFixed, delegatedDecisions: impl.delegatedDecisions,
      proposedSteering: { implement: impl.proposedSteering || [], review: rev.proposedSteering || [] } }
    log(`${t.id}: integrated ${integ.mergeSha}`)
    return true
}

const runLane = async (owner, queue) => {
  while (queue.length) {
    const t = queue.find(ready)
    if (!t) {
      const waitFor = [...new Set(queue.flatMap((q) => laneBlockers(q).filter((b) => !(b in settled))))]
      log(`${owner}: nothing ready; waiting on ${waitFor.join(', ')}`)
      await Promise.race(waitFor.map((b) => done[b].then(() => true, () => false)))
      continue
    }
    queue.splice(queue.indexOf(t), 1)
    let ok = false
    try { ok = await work(t) } catch (e) { fail(t, 'implement', { why: `lane threw: ${e && e.message ? e.message : e}` }) }
    settled[t.id] = ok === true   // set here, before the promise's own handler runs, so this lane's next pick sees it
    resolveDone[t.id](ok === true)
  }
}

phase('Implement')
await Promise.all(Object.entries(lanes).map(([owner, queue]) => runLane(owner, queue)))
await Promise.all(grillQueue)

phase('Report')
const summary = {
  base: BASE, integrationBranch: INT_BRANCH, halted,
  landedBeforeTonight: col.landed,
  tickets: tickets.map((t) => ({ id: t.id, issue: t.issue, url: t.url, title: t.title, status: t.status, owner: t.owner })),
  blocked: tickets.filter((t) => t.status === 'blocked').map((t) => ({ id: t.id, issue: t.issue, waitsOn: t.blockedBy, why: t.why })),
  claimed: tickets.filter((t) => t.status === 'claimed').map((t) => t.id),
  outOfScope: tickets.filter((t) => !inScope(t)).map((t) => t.id),
  results,
}
const rep = await agent(reportPrompt(summary), { label: 'report', phase: 'Report', schema: REPORT })

const ids = (pred) => Object.entries(results).filter(([, r]) => pred(r)).map(([id]) => id)
return {
  halted,
  integrated: ids((r) => r.outcome === 'integrated'),
  partial: ids((r) => r.outcome === 'partial'),
  skipped: ids((r) => r.outcome === 'skipped'),
  failed: ids((r) => r.outcome === 'failed'),
  needsHuman: [...new Set([...humanT.map((t) => t.id), ...ids((r) => r.outcome === 'needs-human')])],
  blocked: summary.blocked.map((b) => b.id),
  questions: rep ? rep.questions : null,
  mainCheckoutClean: rep ? rep.mainCheckoutClean : null,
  reportIssue: rep ? rep.issue : null,
  reportUrl: rep ? rep.url : null,
}
