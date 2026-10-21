export const meta = {
  name: 'retro',
  description: 'Read one overnight run\'s record, find what cost time or went wrong, and file evidence-bearing follow-up tickets under one Retro <date> issue',
  whenToUse: 'The morning after an overnight run, launched by scriptPath with the night\'s date or its Overnight <date> issue; dryRun first to see what it would file',
  phases: [
    { title: 'Gather', detail: 'one agent builds the record index on every launch: the report, the lane tickets, the check logs, the integration branch, the overnight script read, the earlier nights; a read of the durable state and the open Retro issue comes first' },
    { title: 'Lanes', detail: 'one agent per owner lane that ran (beyond maxLaneAgents grouped, smallest together), one for integration and report, one for the workflow\'s own prompts; each returns typed findings or "nothing found" with a reason' },
    { title: 'Rank', detail: 'one agent merges findings by noun, ranks by cost times recurrence, drops what an open issue or origin/main already covers, and writes the coverage block; it writes nothing to GitHub' },
    { title: 'File', detail: 'one agent, the only writer: the Retro <date> parent and up to maxTickets sub-issues in rank order, or, with dryRun, the same as files in the scratch directory' },
  ],
}

// ---------------------------------------------------------------- what this is
// The retro spec (#77), built under #260; its proofs on real runs are #262. It reads one overnight
// run's record, the way the human retrospectives #8, #27, #31 and #46 did, and files what it finds
// as tickets in the overnight collect step's shape under one `Retro <date>` issue. The deterministic
// reading (check logs, side logs, earlier rounds, report headings, the noun merge, cost and
// recurrence, coverage, the ticket body's shape) is tools/retro_record.py, which every agent calls;
// the judgement stays in the agents. Test companion: retro_tests.mjs beside this file.
//
// Launch (Workflow tool), by scriptPath so the landed file runs and not a session's snapshot:
//   scriptPath: /Users/adammedford/Projects/spa-platform/.claude/workflows/retro.js
//   args: { "repo": "/Users/adammedford/Projects/spa-platform", "date": "2026-10-17", "dryRun": true,
//           "knownIssues": [68, 69, 70, 71] }
// Arguments (#77 -> "Arguments"), and no others: an unknown one is refused, so a misspelt dryRun can
// never file for real.
//   date            YYYY-MM-DD, the night (the date its Overnight issue and report carry). One of date
//   issue           and issue is required; the other is derived by the first read below.
//                   issue is the Overnight <date> issue's number.
//   repo            required  the main checkout, an absolute path. Read-only, except the scratch directory.
//   wt              optional  that night's worktree root, when its worktrees still exist; read, never touched.
//   maxParallel     optional  positive integer, default 4: agents in flight at once. #77 puts the session
//                             guideline at "about ten agents per run"; a five-lane night runs #77's ten
//                             (Gather, five lanes, two more, Rank, File), and eleven with the read.
//   maxLaneAgents   optional  positive integer, default 5: owner-lane agents. Lanes beyond it are grouped,
//                             the smallest together, so fan-out is bounded by this and not by the night.
//   maxTickets      optional  positive integer, default 6: sub-issues filed. #77: "no human retrospective
//                             here filed more than six".
//   dryRun          optional  boolean, default false. The parent and the sub-issues are written as files to
//                             the scratch directory and GitHub is only read.
//   overnightScript optional  absolute path of the overnight workflow whose prompts are read; default
//                             <repo>/.claude/workflows/overnight.js.
//   knownIssues     optional  issue numbers of a retrospective already filed for this night; Rank writes a
//                             coverage block naming which a finding matches and which it missed.
//
// Durable state (#77 -> "Durable state"). Every phase agent writes `{"inputs": <key>, "result": <its
// return>}` to its phase file under the scratch directory before the next phase starts. The script has
// no filesystem, so its first agent, the read, lists those files (tools/retro_record.py state) and the
// open `Retro <date>` issue; a phase whose file carries the key this run computes for it is not
// dispatched again. Gather is the exception: its inputs are the record itself (the issues and their
// comments, the logs, the branches, the lane worktrees, the earlier nights), which only a read can
// compare, so it runs on every launch, and its index carries versions (every file's digest and every
// issue's updatedAt) so that a changed record changes the index. A key is a hash of the phase's
// inputs: the index for a lane, and with it the findings and knownIssues for Rank, the ranking,
// maxTickets and dryRun for File, both of which read the index too. An unchanged record gives an
// unchanged index, and every phase after Gather is reused.
const A = args || {}
const KNOWN_ARGS = ['date', 'issue', 'repo', 'wt', 'maxParallel', 'maxLaneAgents', 'maxTickets', 'dryRun', 'overnightScript', 'knownIssues']
const badArguments = (a) => {
  const bad = []
  const abs = (p) => typeof p === 'string' && p.startsWith('/') && p.length > 1
  const count = (v) => Number.isInteger(v) && v > 0
  for (const k of Object.keys(a)) if (!KNOWN_ARGS.includes(k)) bad.push(`${k}: not an argument of this workflow; it takes ${KNOWN_ARGS.join(', ')}`)
  if (a.date === undefined && a.issue === undefined) bad.push('date or issue: one is required, e.g. "date": "2026-10-17" or "issue": 226 (the Overnight <date> issue)')
  if (a.date !== undefined && (typeof a.date !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(a.date))) bad.push('date: YYYY-MM-DD, the night the record is of')
  if (a.issue !== undefined && !count(a.issue)) bad.push('issue: the Overnight <date> issue\'s number, a positive integer')
  if (!abs(a.repo)) bad.push('repo: the main checkout as an absolute path, e.g. /Users/adammedford/Projects/spa-platform')
  if (a.wt !== undefined && !abs(a.wt)) bad.push('wt: when given, that night\'s worktree root as an absolute path')
  for (const k of ['maxParallel', 'maxLaneAgents', 'maxTickets']) if (a[k] !== undefined && !count(a[k])) bad.push(`${k}: a positive integer`)
  if (a.dryRun !== undefined && typeof a.dryRun !== 'boolean') bad.push('dryRun: true or false, a boolean (the string "true" is refused)')
  if (a.overnightScript !== undefined && !abs(a.overnightScript)) bad.push('overnightScript: an absolute path to the overnight workflow file')
  if (a.knownIssues !== undefined && !(Array.isArray(a.knownIssues) && a.knownIssues.every(count))) bad.push('knownIssues: an array of issue numbers, e.g. [68, 69, 70, 71]')
  return bad
}
const BAD_LAUNCH = badArguments(A)
if (BAD_LAUNCH.length) {
  // Stops here: no agent is started, nothing is read or written.
  return { halted: 'bad launch; no agent started. Fix each argument and relaunch with all of them:\n- ' + BAD_LAUNCH.join('\n- ') }
}
const REPO = A.repo.replace(/\/+$/, '')
const WTROOT = A.wt ? A.wt.replace(/\/+$/, '') : ''
const MAX_PAR = A.maxParallel || 4
const MAX_LANES = A.maxLaneAgents || 5
const MAX_TICKETS = A.maxTickets || 6
const DRY = A.dryRun === true
const OVERNIGHT = A.overnightScript || `${REPO}/.claude/workflows/overnight.js`
const KNOWN = [...new Set(A.knownIssues || [])]
// The night's git-ignored log directory (.gitignore: .scratch/*/log/). The retro writes only under its
// own dated subdirectory of it, which is fixed once the night's date is known.
const NIGHT_LOG = `${REPO}/.scratch/overnight/log`
const scratchFor = (date) => `${NIGHT_LOG}/retro-${date}`
const READER = `python3 -I ${REPO}/tools/retro_record.py`

// ---------------------------------------------------------------- keys, agents and slots
// A stable key for a phase's inputs: FNV-1a over JSON with sorted keys. No clock and no randomness,
// which a Workflow script may not use, so a resumed run computes the same key from the same inputs.
const stable = (v) => (Array.isArray(v) ? `[${v.map(stable).join(',')}]`
  : v && typeof v === 'object' ? `{${Object.keys(v).sort().map((k) => JSON.stringify(k) + ':' + stable(v[k])).join(',')}}`
    : JSON.stringify(v === undefined ? null : v))
const key = (v) => {
  let h = 0x811c9dc5
  const s = stable(v)
  for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 0x01000193) >>> 0 }
  return h.toString(16).padStart(8, '0')
}

let agentsRun = 0
let running = 0
const waiting = []
const slot = async (fn) => {
  if (running >= MAX_PAR) await new Promise((resolve) => waiting.push(resolve))
  running++
  try { return await fn() } finally {
    running--
    const next = waiting.shift()
    if (next) next()
  }
}
const run = (prompt, opts) => slot(() => { agentsRun++; return agent(prompt, opts) })

// ---------------------------------------------------------------- what every prompt carries
// Agents are fresh contexts that know only their prompt (#77 -> "One workflow script"), so the rules,
// the record's shape, the taxonomy and the evidence rules are in each one in full.
const HOUSE = (scratch) => `Read-only rules (a retro; you change nothing you read):
- The main checkout ${REPO} is Adam's. Never commit, checkout, switch, stash, reset, build or test in it, and run nothing from it but the record reader named below, which reads files and prints JSON. git is read-only: log, show, diff, rev-parse, reflog, status, ls-files.
- GitHub is read until the File phase, and only the File phase writes to it. Before then use only \`gh issue view\`, \`gh issue list\`, \`gh search issues\` and GET \`gh api\` calls. Run every \`gh\` command with \`cd ${REPO} &&\` in front.
- The only place you may write is ${scratch}/, the retro's scratch directory under the night's git-ignored log directory. Write each OUTPUT FILE and PHASE FILE line's path exactly as given, and no other file.
- The archived ticket directories (.scratch/<slug>/issues/) are read-only (ADR-0020 decision 5). Never write there.
- Make no worktree. A lane's leftover worktree${WTROOT ? ` under ${WTROOT}` : ''} is read with \`git -C <path> status\` and \`git -C <path> reflog\`, never touched.
- Never push, merge, tag, build, apply, or edit, close, relabel or assign an issue this run did not create.
- No credential, token, cookie, authorization code or state value is ever quoted into a finding, a ticket or a file, even when a record line carries one: cite the line, do not quote it.
- The record reader is \`${READER}\`. Use it for what it decides (a log's sha, verdict and counts, a side log, an earlier round, a report's date and headings, the noun merge, cost and recurrence, coverage, a ticket body's shape) rather than reading those by eye; its docstring states each rule.`

const RECORD = `The record of an overnight run (#77 -> "Problem Statement"), in one of two shapes:
- The Issues shape (from 2026-10-18 on): the \`Overnight <date>\` issue's body, question comments and answer comments; each lane ticket's comments in order by heading (## Routing, ## Decisions taken, ## Answer, ## Review, ## Status note, ## Grilling record, ## Questions for Adam); the pending-writes file ${NIGHT_LOG}/pending-<date>.jsonl and the untracked-at-start listing when the run wrote them.
- The archive shape (the nights up to 2026-10-17): the report file under ${REPO}/.scratch/overnight/ (REPORT-<date>.md, or REPORT.md, resolved by its first line), the questions file, and each lane ticket's archived file with its ## Comments section, where the same headings appear as sections.
- Both: the check logs in ${NIGHT_LOG}/, one per task and stage, first line HEAD=<sha>, last line the verdict (GATE: GREEN|RED, or check: pass:|check: fail:), with the test runner's total/failed/succeeded/skipped/duration lines; a date-suffixed log is an earlier round of its task; a log without HEAD= first or a verdict last is a side log and never a verdict. The integration branch's first-parent log and merge bodies, its \`Review:\` commits, and any \`attempt, not green\` or \`partial, question for Adam\` subject in the night's branches. The workflow's return JSON when one was saved. The record's only clock is file modification times and comment timestamps.
- It holds no agent transcripts and no tool-call counts. Never estimate either; say the record cannot show it.`

const TAXONOMY = `Each finding has one type (#77 -> "The finding schema"):
- time-sink: a stage whose span between log or comment timestamps is long against its output, or a step the report says was slow.
- repeated-failure: a RED verdict line; a date-suffixed earlier log; a reopened ## Answer; an \`attempt, not green\` subject; a reflog that resets and retries. A RED verdict line always yields at least one repeated-failure finding citing the log's last lines.
- misleading-instruction: a lane record that repairs, works around or reads past an instruction from a prompt or a steering document, or a question that asks what an instruction means. Quote the instruction and the record of working around it.
- reversed-decision: a ## Review record's "Decisions reversed", or a decision taken in one lane and reversed in integration or the morning.
- late-found-unfailable-assertion: a \`Review:\` commit or review record that makes an assertion able to fail, or unticks a box the implementer ticked. Cite that commit or record.
- guardrail-gap: a defect that passed the lane's check and was caught by review, integration or the morning, and that a check could have decided.
- tracker-hygiene: a record item that disagrees with itself: a closed issue with an unticked box, a status the collect step re-derived, an undated report name, a leftover branch or worktree, a question answered one night and asked again the next, a pending-writes file not yet replayed.
Fields: type; title; noun, the identifier the finding cannot paraphrase away (a flag, a command, a file name, a heading, a rule id), which dedupe keys on; evidence; cost {red, attempts, nights, lanes} counted from the record; fix {kind: check | prompt | doc | decision, owner: an OWNERSHIP.md role (ws1-infra, ws2-hosting, ws3-identity, ws4-proxy, ws5-fixtures, ws6-verification, integrator), summary, diff: a unified diff only when the fix is an edit to the overnight workflow, else ""}; confidence: verified when you checked the evidence line yourself, else suspected.`

const EVIDENCE = `Evidence rules: every finding carries at least one evidence entry {kind: check-log | comment | commit | ticket-file, ref, lines, quote}. ref is a path and line (path:line), a comment's URL, or a commit sha; quote is the record line itself, copied, never paraphrased, and never a secret. A finding you cannot cite is not a finding. Cite an issue as #NNN only after reading it with \`gh issue view\` in this run.`

// ---------------------------------------------------------------- schemas
const PHASE_FILE = { type: 'object', properties: { name: { type: 'string' }, inputs: { type: 'string' }, result: { type: 'object' } }, required: ['name', 'inputs', 'result'] }
const STATE = {
  type: 'object',
  properties: {
    date: { type: 'string' }, issue: { type: 'integer' }, issueUrl: { type: 'string' },
    openRetro: { type: 'array', items: { type: 'object', properties: { number: { type: 'integer' }, title: { type: 'string' }, url: { type: 'string' }, text: { type: 'string' } }, required: ['number', 'title', 'url', 'text'] } },
    phaseFiles: { type: 'array', items: PHASE_FILE },
    error: { type: 'string' },
  },
  required: ['date', 'issue', 'issueUrl', 'openRetro', 'phaseFiles', 'error'],
}
// The record index (#77 user story 22): the one input every later phase reads. Its shape, for a
// sibling workflow that reads the same record:
//   night          {date, issue (0 when the archive shape has none), shape: issues|archive, base, integrationBranch}
//   report         {source, date, kind, sections[]}: the report, as `retro_record.py report` resolves it
//   questions      [{source, date}]
//   lanes          [{owner, tasks: [{id, issue, ticket}], size}]: the owner lanes that ran; size is the tasks' count
//   logs, sideLogs, notLogs, rounds   `retro_record.py logs` for the night's logs, verbatim
//   integration    {branch, merges: [{sha, subject, body}], reviewCommits: [{sha, subject}], attempts: [{sha, subject, branch}]}
//   worktrees      [{path, status, reflog}]: only when wt was given and the worktree exists
//   returnJson     the path of the run's saved return JSON, or ""
//   pending, untracked   the pending-writes file and the untracked-at-start listing, or ""
//   overnightScript {path, sha256, mtime}: `retro_record.py script` on the script read
//   history        [{date, report, questions}]: earlier nights' report and questions paths, for recurrence
//   missing        [string]: every record item the night lacks; the parent issue repeats it verbatim
//   versions       {files, issues: [{number, updatedAt}]}: files is `retro_record.py fingerprint` over every
//                  file the index names and the log directory; issues the Overnight issue and each lane
//                  ticket with its updatedAt, which a new comment moves. A changed record changes these,
//                  so the phases after Gather, keyed on the index, run again.
const LOG_ENTRY = {
  type: 'object',
  properties: { path: { type: 'string' }, task: { type: 'string' }, stage: { type: 'string' }, round: { type: 'string' }, sha: { type: 'string' }, verdict: { type: 'string' }, verdictLine: { type: 'string' }, lastLines: { type: 'array', items: { type: 'string' } }, lines: { type: 'integer' }, mtime: { type: 'string' }, side: { type: 'boolean' } },
  required: ['path', 'task', 'verdict', 'lastLines', 'lines', 'side'],
}
const INDEX = {
  type: 'object',
  properties: {
    night: { type: 'object', properties: { date: { type: 'string' }, issue: { type: 'integer' }, shape: { type: 'string', enum: ['issues', 'archive'] }, base: { type: 'string' }, integrationBranch: { type: 'string' } }, required: ['date', 'issue', 'shape', 'base', 'integrationBranch'] },
    report: { type: 'object', properties: { source: { type: 'string' }, date: { type: 'string' }, kind: { type: 'string' }, sections: { type: 'array', items: { type: 'string' } } }, required: ['source', 'date', 'kind', 'sections'] },
    questions: { type: 'array', items: { type: 'object', properties: { source: { type: 'string' }, date: { type: 'string' } }, required: ['source', 'date'] } },
    lanes: { type: 'array', items: { type: 'object', properties: { owner: { type: 'string' }, tasks: { type: 'array', items: { type: 'object', properties: { id: { type: 'string' }, issue: { type: 'integer' }, ticket: { type: 'string' } }, required: ['id', 'issue', 'ticket'] } }, size: { type: 'integer' } }, required: ['owner', 'tasks', 'size'] } },
    logs: { type: 'array', items: LOG_ENTRY },
    sideLogs: { type: 'array', items: { type: 'string' } },
    notLogs: { type: 'array', items: { type: 'string' } },
    rounds: { type: 'array', items: { type: 'object', properties: { task: { type: 'string' }, stage: { type: 'string' }, current: { type: 'string' }, earlier: { type: 'array', items: { type: 'string' } } }, required: ['task', 'stage', 'current', 'earlier'] } },
    integration: { type: 'object', properties: { branch: { type: 'string' }, merges: { type: 'array', items: { type: 'object' } }, reviewCommits: { type: 'array', items: { type: 'object' } }, attempts: { type: 'array', items: { type: 'object' } } }, required: ['branch', 'merges', 'reviewCommits', 'attempts'] },
    worktrees: { type: 'array', items: { type: 'object', properties: { path: { type: 'string' }, status: { type: 'string' }, reflog: { type: 'string' } }, required: ['path', 'status', 'reflog'] } },
    returnJson: { type: 'string' }, pending: { type: 'string' }, untracked: { type: 'string' },
    overnightScript: { type: 'object', properties: { path: { type: 'string' }, sha256: { type: 'string' }, mtime: { type: 'string' } }, required: ['path', 'sha256', 'mtime'] },
    history: { type: 'array', items: { type: 'object', properties: { date: { type: 'string' }, report: { type: 'string' }, questions: { type: 'string' } }, required: ['date', 'report', 'questions'] } },
    missing: { type: 'array', items: { type: 'string' } },
    versions: { type: 'object', properties: { files: { type: 'string' }, issues: { type: 'array', items: { type: 'object', properties: { number: { type: 'integer' }, updatedAt: { type: 'string' } }, required: ['number', 'updatedAt'] } } }, required: ['files', 'issues'] },
  },
  required: ['night', 'report', 'questions', 'lanes', 'logs', 'sideLogs', 'notLogs', 'rounds', 'integration', 'worktrees', 'returnJson', 'pending', 'untracked', 'overnightScript', 'history', 'missing', 'versions'],
}
const TYPES = ['time-sink', 'repeated-failure', 'misleading-instruction', 'reversed-decision', 'late-found-unfailable-assertion', 'guardrail-gap', 'tracker-hygiene']
const OWNERS = ['ws1-infra', 'ws2-hosting', 'ws3-identity', 'ws4-proxy', 'ws5-fixtures', 'ws6-verification', 'integrator']
const FINDING = {
  type: 'object',
  properties: {
    type: { type: 'string', enum: TYPES },
    title: { type: 'string' },
    noun: { type: 'string' },
    evidence: { type: 'array', items: { type: 'object', properties: { kind: { type: 'string', enum: ['check-log', 'comment', 'commit', 'ticket-file'] }, ref: { type: 'string' }, lines: { type: 'string' }, quote: { type: 'string' } }, required: ['kind', 'ref', 'lines', 'quote'] } },
    cost: { type: 'object', properties: { red: { type: 'integer' }, attempts: { type: 'integer' }, nights: { type: 'integer' }, lanes: { type: 'integer' } }, required: ['red', 'attempts', 'nights', 'lanes'] },
    fix: { type: 'object', properties: { kind: { type: 'string', enum: ['check', 'prompt', 'doc', 'decision'] }, owner: { type: 'string', enum: OWNERS }, summary: { type: 'string' }, diff: { type: 'string' } }, required: ['kind', 'owner', 'summary', 'diff'] },
    confidence: { type: 'string', enum: ['verified', 'suspected'] },
  },
  required: ['type', 'title', 'noun', 'evidence', 'cost', 'fix', 'confidence'],
}
const LANE = {
  type: 'object',
  properties: { lane: { type: 'string' }, findings: { type: 'array', items: FINDING }, nothingFound: { type: 'string' } },
  required: ['lane', 'findings', 'nothingFound'],
}
const RANKED = { ...FINDING, properties: { ...FINDING.properties, recurrence: { type: 'integer' }, score: { type: 'integer' }, blockedBy: { type: 'array', items: { type: 'string' } } }, required: [...FINDING.required, 'recurrence', 'score', 'blockedBy'] }
const RANK = {
  type: 'object',
  properties: {
    ranked: { type: 'array', items: RANKED },
    dropped: { type: 'array', items: { type: 'object', properties: { noun: { type: 'string' }, issue: { type: 'string' }, reason: { type: 'string' } }, required: ['noun', 'issue', 'reason'] } },
    coverage: { type: 'object', properties: { matched: { type: 'array', items: { type: 'object' } }, missed: { type: 'array', items: { type: 'object' } } }, required: ['matched', 'missed'] },
  },
  required: ['ranked', 'dropped', 'coverage'],
}
const FILED = {
  type: 'object',
  properties: {
    parent: { type: 'object', properties: { number: { type: 'integer' }, url: { type: 'string' }, title: { type: 'string' }, file: { type: 'string' } }, required: ['number', 'url', 'title', 'file'] },
    tickets: { type: 'array', items: { type: 'object', properties: { number: { type: 'integer' }, url: { type: 'string' }, title: { type: 'string' }, label: { type: 'string', enum: ['ready-for-agent', 'ready-for-human'] }, noun: { type: 'string' }, file: { type: 'string' }, blockedBy: { type: 'array', items: { type: 'integer' } } }, required: ['number', 'url', 'title', 'label', 'noun', 'file', 'blockedBy'] } },
    dryRun: { type: 'boolean' },
    refused: { type: 'string' },
    unreachable: { type: 'boolean' },
  },
  required: ['parent', 'tickets', 'dryRun', 'refused', 'unreachable'],
}

// ---------------------------------------------------------------- prompts
const statePrompt = () => `Read the retro's durable state before anything runs. Write no file.

${HOUSE(`${NIGHT_LOG}/retro-<date>`)}

1. The night. ${A.date ? `date is ${A.date}.` : 'date is not given.'} ${A.issue ? `issue is #${A.issue}.` : 'issue is not given.'}
   ${A.issue ? `Read it: \`cd ${REPO} && gh issue view ${A.issue} --json number,title,url\`. Its title must be "Overnight <date>"; the date is that one.${A.date ? ` It must equal ${A.date}; if not, set error to say both.` : ''}` : `Find it: \`cd ${REPO} && gh issue list --state all --search "Overnight ${A.date} in:title" --json number,title,url\`, the issue titled exactly "Overnight ${A.date}". A night of the archive shape has none: issue 0, issueUrl "".`}
   If neither resolves, set error to say which.
2. The phase files: \`${READER} state ${NIGHT_LOG}/retro-<date>\` with the date from step 1. phaseFiles is its output, verbatim (an empty list when the directory does not exist).
3. An open retro of this night: \`cd ${REPO} && gh issue list --state open --search "Retro <date> in:title" --json number,title,url,body\`. openRetro is every issue whose title is exactly "Retro <date>", with text its body followed by the bodies of its sub-issues (\`cd ${REPO} && gh api repos/{owner}/{repo}/issues/<n>/sub_issues\`). An empty list when there is none.
error is "" when nothing stopped you.`

const gatherPrompt = (scratch, date, issue, phaseKey) => `Gather: build the record index of the overnight run of ${date}${issue ? ` (Overnight issue #${issue})` : ''}. You judge nothing; you index.

${HOUSE(scratch)}

${RECORD}

PHASE FILE: ${scratch}/gather.json INPUTS: ${phaseKey}
Write {"inputs": "${phaseKey}", "result": <your return value>} to the PHASE FILE as JSON before you return.

1. Resolve the shape. ${issue ? `The Issues shape: \`cd ${REPO} && gh issue view ${issue} --json number,title,body,comments\`; report.source is its URL and \`${READER} report <a copy of its body under ${scratch}/> --title "Overnight ${date}"\` gives its sections.` : `No Overnight issue: the archive shape. Resolve the report and questions files under ${REPO}/.scratch/overnight/ by their first lines with \`${READER} report <file>\`, since REPORT.md and questions.md carry no date in their names.`}
2. lanes: the owner lanes that ran that night, from the report's task lists and the lane tickets (issue comments, or archived ticket files): one entry per OWNERSHIP.md role with its tasks and size = the number of tasks.
3. logs, sideLogs, notLogs, rounds: \`${READER} logs ${NIGHT_LOG}\`, keeping the entries of that night's tasks (by task id and by modification time against the night's comments) and their earlier rounds.
4. integration: the night's integration branch (overnight/${date}): \`git -C ${REPO} log --first-parent --format='%H%x09%s' <base>..overnight/${date}\` and each merge's body with \`git -C ${REPO} show -s --format=%B <sha>\`; its \`Review:\` commits; every \`attempt, not green\` or \`partial, question for Adam\` subject in \`git -C ${REPO} log --all --format='%H%x09%D%x09%s'\` for the night's task branches.
5. worktrees: ${WTROOT ? `each lane worktree under ${WTROOT} that exists: \`git -C <path> status --short\` and \`git -C <path> reflog -20\`.` : 'wt was not given: an empty list, and "worktrees: not given" in missing.'}
6. returnJson, pending, untracked: the paths when they exist (the return JSON in ${scratch}/ or ${NIGHT_LOG}/, ${NIGHT_LOG}/pending-${date}.jsonl, the untracked-at-start listing), else "".
7. overnightScript: \`${READER} script ${OVERNIGHT}\`.
8. history: every earlier night's report and questions, resolved by first line as in step 1, and each earlier Overnight <date> issue (\`cd ${REPO} && gh issue list --state all --search "Overnight in:title" --json number,title\`), newest first.
9. missing: one line for every record item the night lacks (no return JSON, no worktrees left, no transcripts and no tool-call counts, which the record never holds, a pending-writes file absent or present-and-unreplayed, a lane with no check log).
10. versions, which decide whether the phases after you are reused, so copy each value exactly: files is the digest from \`${READER} fingerprint ${NIGHT_LOG} ${REPO}/.scratch/overnight ${OVERNIGHT} <every other file path the index names>\`; issues is the Overnight issue and every lane ticket that is an issue, each {number, updatedAt} from \`cd ${REPO} && gh issue view <n> --json number,updatedAt\`, in number order ([] when there are none).
Return the index.`

const lanePrompt = (unit, scratch, date, index, phaseKey) => `Lanes: find what cost time or went wrong in ${unit.title} on the overnight run of ${date}.

${HOUSE(scratch)}

${RECORD}

${TAXONOMY}

${EVIDENCE}

PHASE FILE: ${scratch}/lanes/${unit.key}.json INPUTS: ${phaseKey}
Write {"inputs": "${phaseKey}", "result": <your return value>} to the PHASE FILE as JSON before you return.

Read ${scratch}/gather.json, the record index, and the files and comments it points at, and nothing else. ${unit.read}
${unit.kind === 'lane' ? `Your tasks: ${unit.tasks.map((t) => `${t.id}${t.issue ? ` (#${t.issue})` : ''}`).join(', ') || '(none listed)'}.${unit.red.length ? ` These logs end RED, and each must yield at least one repeated-failure finding citing its last lines: ${unit.red.join(', ')}.` : ''}` : ''}
Return lane "${unit.key}", findings, and nothingFound: "" when you found something, else the reason the record shows nothing (a clean lane gives an empty list and a reason, never a vague one).`

const rankPrompt = (scratch, date, phaseKey, added) => `Rank the retro's findings for the overnight run of ${date}. You write nothing to GitHub.

${HOUSE(scratch)}

${TAXONOMY}

${EVIDENCE}

PHASE FILE: ${scratch}/rank.json INPUTS: ${phaseKey}
Write {"inputs": "${phaseKey}", "result": <your return value>} to the PHASE FILE as JSON before you return.

Read ${scratch}/gather.json and every file under ${scratch}/lanes/: the lanes' findings.
The script adds these findings, one for each RED verdict line no lane cited; rank them with the rest: ${JSON.stringify(added)}
1. Merging and ranking are the reader's: pipe {"findings": [...every finding...], "history": [{"date", "text"}: the text of each earlier night's report and questions in the index's history], "drop": [...]} to \`${READER} rank\` and return its ranked list in its order, adding blockedBy (the nouns of other ranked findings whose fix this one's needs first, else []).
2. drop: a finding an open issue already covers: search open issues for the noun (\`cd ${REPO} && gh search issues --repo adammedford/spa-platform --state open "<noun>" --json number,title\`), read each candidate with \`gh issue view\`, and drop it as "#NNN" only when that issue's body asks for the same fix. A finding whose fix has already landed: \`git -C ${REPO} log --first-parent --format='%H %s' origin/main\` carries the noun in a subject or body after the night; drop it with the commit's sha as its issue.
3. coverage: ${KNOWN.length ? `the known issues are ${KNOWN.map((n) => '#' + n).join(', ')}. Read each with \`gh issue view\`, then pipe {"known": [{"number", "text": title and body}], "findings": [...the ranked and dropped findings...], "missing": <the index's missing list>, "why": {"<number>": "<the missing entry that explains the miss>"}} to \`${READER} coverage\` and return its output.` : 'no knownIssues were given: {"matched": [], "missed": []}.'}`

const filePrompt = (scratch, date, issue, index, rank, toFile, phaseKey) => {
  const bodies = `${scratch}/${DRY ? 'dry-run' : 'bodies'}`
  const ticketFile = (i) => `${bodies}/ticket-${String(i + 1).padStart(2, '0')}.md`
  return `File the retro of the overnight run of ${date}.${DRY ? ' This is a dry run: write every issue as a file and write nothing to GitHub.' : ' You are the only phase that writes to GitHub.'}

${HOUSE(scratch)}

${EVIDENCE}

PHASE FILE: ${scratch}/file.json INPUTS: ${phaseKey}
Write {"inputs": "${phaseKey}", "result": <your return value>} to the PHASE FILE as JSON before you return.
OUTPUT FILE: ${bodies}/parent.md
${toFile.map((_, i) => `OUTPUT FILE: ${ticketFile(i)}`).join('\n')}

THE TICKETS, in rank order, at most ${MAX_TICKETS}; file exactly these, in this order:
${JSON.stringify(toFile.map((f) => ({ type: f.type, title: f.title, noun: f.noun, cost: f.cost, score: f.score, recurrence: f.recurrence, fix: f.fix, confidence: f.confidence, evidence: f.evidence, blockedBy: f.blockedBy || [] })), null, 1)}
Dropped: ${JSON.stringify(rank.dropped)}. Coverage: ${JSON.stringify(rank.coverage)}. The index's missing list: ${JSON.stringify(index.missing)}.

1. ${DRY ? 'A dry run reads GitHub only for the citations below.' : `Look again for an open "Retro ${date}" issue (\`cd ${REPO} && gh issue list --state open --search "Retro ${date} in:title" --json number,title,url\`). If one is titled exactly that, file nothing: return it as parent, tickets [], refused "an open Retro ${date} exists".`}
2. Every #NNN a body cites is one you read with \`cd ${REPO} && gh issue view <n>\` in this run; cite nothing from memory.
3. The parent's body, written to ${bodies}/parent.md: the night's one-line summary and base (${index.night.base || 'from the index'}${issue ? `, Overnight #${issue}` : ''}); a table of the findings with type, cost, confidence and ticket; the dropped list with its #NNN citations; the coverage block${KNOWN.length ? '' : ' (none: no knownIssues)'}; the index's missing list verbatim; and one paragraph saying what a record-only retrospective cannot see (transcripts, tool-call counts, anything the missing list names). Its title is "Retro ${date}". It carries no triage label.
4. Each ticket's body, written to its OUTPUT FILE in order, in the overnight collect step's shape: \`**Owner:** <fix.owner>\`, \`**Plan row:** none\`, \`**What to build:**\` with the finding's evidence lines (ref and quote) and its cost, and \`## Acceptance\` with \`- [ ]\` boxes each naming an observable in a later run's record ("no lane's log shows the repair"). A fix with a diff carries it in a \`\`\`diff block for the human to apply. Check each with \`${READER} body-check < <file>\` and fix it until it exits 0. The label: ready-for-agent when the fix is provable on this Mac by a build, a test, a check run or terraform validate, writes only its owner's paths and needs no decision; ready-for-human when fix.kind is decision, or it needs a contract or ADR change, an AWS action, or applying a diff to the local workflow. A ticket pairing a tracked change with a workflow diff is ready-for-agent and carries the diff (#68's precedent). Its title is "<type>: <title>".
${DRY ? `5. Write nothing to GitHub. Return parent {number: 0, url: "", title: "Retro ${date}", file: "${bodies}/parent.md"}, and each ticket with number 0, url "", its label and its file, dryRun true, refused "", unreachable false.` : `5. Create the parent: \`cd ${REPO} && gh issue create --title "Retro ${date}" --body-file ${bodies}/parent.md\`.
6. Create each ticket in order: \`cd ${REPO} && gh issue create --title "<type>: <title>" --label <label> --parent <parent number> --body-file <its file>\`.
7. Each blocked-by edge, after both exist: \`cd ${REPO} && gh issue edit <ticket> --add-blocked-by <the ticket it needs>\`.
8. If GitHub cannot be reached, stop writing, set unreachable true, and return what was created; the bodies stay in ${bodies}/. Return parent and tickets with their numbers and URLs, dryRun false, refused "".`}`
}

// ---------------------------------------------------------------- lanes and groups
// Owner lanes beyond maxLaneAgents are grouped, the smallest together (#77 -> "Arguments"): the
// largest maxLaneAgents - 1 keep an agent each and the rest share the last one.
const laneUnits = (index) => {
  const lanes = [...index.lanes].sort((a, b) => b.size - a.size || (a.owner < b.owner ? -1 : 1))
  const groups = lanes.length <= MAX_LANES ? lanes.map((l) => [l]) : [...lanes.slice(0, MAX_LANES - 1).map((l) => [l]), lanes.slice(MAX_LANES - 1)]
  const redFor = (tasks) => index.logs.filter((l) => !l.side && l.verdict === 'RED' && tasks.some((t) => t.id === l.task)).map((l) => l.path)
  const units = groups.map((g) => {
    const tasks = g.flatMap((l) => l.tasks)
    return {
      kind: 'lane', key: g.map((l) => l.owner).join('+'), owners: g.map((l) => l.owner), tasks, red: redFor(tasks),
      title: `the ${g.map((l) => l.owner).join(' and ')} lane${g.length > 1 ? 's' : ''}`,
      read: `Read only these lanes' tasks, tickets, check logs (and their earlier rounds), merges and review records.`,
    }
  })
  units.push({ kind: 'integration', key: 'integration', title: 'integration and report', tasks: [], red: [], read: 'Read the serialized merges and the integration verdicts (the -integrate logs), the report\'s Failed, Partial and Needs-you sections, the main-checkout findings and the leftovers (branches, worktrees, the pending-writes file).' })
  units.push({ kind: 'prompts', key: 'prompts', title: 'the overnight workflow\'s own prompts', tasks: [], red: [], read: `Read the overnight script the index names (overnightScript: path, sha256, mtime) against the lanes' records of repairing, working around or asking about one of its instructions. A fix here is fix.kind prompt with a unified diff against that file. Say in a finding when the script's hash shows it changed since the night.` })
  return units
}

// A RED verdict line always yields at least one repeated-failure finding (#77): a RED log no finding
// cites gets one here, so the rule does not rest on an agent remembering it. A lane may cite the log
// by any path to it (EVIDENCE: "a path and line"), so a citation is matched on the file's name. The
// quote is the verdict line alone and the rest of the last lines are cited by range: a record line
// can carry a secret, and this finding is the one no agent reads before it reaches a ticket.
const fileOf = (ref) => String(ref).replace(/:[\d-]+$/, '').split('/').pop()
const redFindings = (index, findings) => {
  const cited = (path) => findings.some((f) => f.type === 'repeated-failure' && f.evidence.some((e) => fileOf(e.ref) === fileOf(path)))
  const ownerOf = (task) => (index.lanes.find((l) => l.tasks.some((t) => t.id === task)) || { owner: 'integrator' }).owner
  return index.logs.filter((l) => !l.side && l.verdict === 'RED' && !cited(l.path)).map((l) => ({
    type: 'repeated-failure',
    title: `${l.task} ${l.stage || 'check'} ended RED`,
    noun: l.path.split('/').pop(),
    evidence: [{ kind: 'check-log', ref: `${l.path}:${l.lines}`, lines: `${Math.max(1, l.lines - l.lastLines.length + 1)}-${l.lines}`, quote: l.verdictLine || 'RED' }],
    cost: { red: 1, attempts: 1, nights: 1, lanes: 1 },
    fix: { kind: 'check', owner: OWNERS.includes(ownerOf(l.task)) ? ownerOf(l.task) : 'integrator', summary: 'Read the failure in the log\'s last lines and make the check pass, or record why it cannot.', diff: '' },
    confidence: 'verified',
  }))
}

// ---------------------------------------------------------------- run
phase('Gather')
const state = await run(statePrompt(), { label: 'state', phase: 'Gather', schema: STATE })
if (!state || state.error) return { halted: `the durable-state read stopped: ${state ? state.error : 'no result'}; no phase ran` }
const DATE = A.date || state.date
const ISSUE = A.issue || state.issue || 0
if (!/^\d{4}-\d{2}-\d{2}$/.test(DATE || '')) return { halted: `the night's date did not resolve (${DATE}); no phase ran` }
const SCRATCH = scratchFor(DATE)
const files = new Map((state.phaseFiles || []).map((f) => [f.name, f]))
const reused = []
// done: whether a phase file's result is a finished phase. File's is not when GitHub could not be
// reached or the run refused, so a relaunch dispatches File again rather than replaying the stop.
const reuse = (name, inputs, done = () => true) => {
  const f = files.get(name)
  if (f && f.inputs === inputs && f.result && done(f.result)) { reused.push(name); return f.result }
  return null
}
log(`retro ${DATE}${ISSUE ? ` (#${ISSUE})` : ''}: scratch ${SCRATCH}${DRY ? '; dry run' : ''}`)

// Gather runs on every launch: its inputs are the record, which only a read can compare (see above).
const gatherKey = key({ date: DATE, issue: ISSUE, repo: REPO, wt: WTROOT, overnightScript: OVERNIGHT })
const index = await run(gatherPrompt(SCRATCH, DATE, ISSUE, gatherKey), { label: 'gather', phase: 'Gather', schema: INDEX })
if (!index) return { halted: 'Gather returned nothing; no later phase ran', scratch: SCRATCH }
const indexKey = key(index)

phase('Lanes')
const units = laneUnits(index)
log(`${index.lanes.length} owner lane(s) ran; ${units.length} lane agent(s), at most ${MAX_PAR} at once`)
const laneResults = await parallel(units.map((u) => async () => {
  const k = key({ index: indexKey, unit: u.key, owners: u.owners || [] })
  return reuse(`lanes/${u.key}`, k) || await run(lanePrompt(u, SCRATCH, DATE, index, k), { label: `lane:${u.key}`, phase: 'Lanes', schema: LANE })
}))
const laneFindings = laneResults.filter(Boolean).flatMap((r) => r.findings)
const added = redFindings(index, laneFindings)
if (added.length) log(`${added.length} RED log(s) no lane cited, each given a repeated-failure finding`)

phase('Rank')
// Rank and File read the index too (its history, missing list and base), so their keys carry it.
const rankKey = key({ index: indexKey, lanes: laneResults.map((r) => (r ? key(r) : null)), added, knownIssues: KNOWN })
const rank = reuse('rank', rankKey) || await run(rankPrompt(SCRATCH, DATE, rankKey, added), { label: 'rank', phase: 'Rank', schema: RANK })
if (!rank) return { halted: 'Rank returned nothing; nothing was filed', scratch: SCRATCH, overnightScript: index.overnightScript }

phase('File')
const toFile = rank.ranked.slice(0, MAX_TICKETS)
if (rank.ranked.length > toFile.length) log(`${rank.ranked.length} findings ranked; the ${rank.ranked.length - toFile.length} below the top ${MAX_TICKETS} are not filed`)
const result = {
  date: DATE, issue: ISSUE, scratch: SCRATCH, dryRun: DRY, filed: false,
  parent: null, tickets: [], dropped: rank.dropped, coverage: rank.coverage,
  delta: [], unfiled: rank.ranked.slice(MAX_TICKETS).map((f) => f.noun),
  missing: index.missing, overnightScript: index.overnightScript,
  agents: agentsRun, reused,
}
const open = (state.openRetro || []).filter((r) => r.title === `Retro ${DATE}`)
if (open.length) {
  // A second run on the same night files nothing and returns what it would have filed (#77).
  const existing = open[0]
  result.parent = { number: existing.number, url: existing.url, title: existing.title, file: '' }
  result.delta = toFile.filter((f) => !existing.text.includes(f.noun)).map((f) => ({ type: f.type, title: f.title, noun: f.noun, score: f.score }))
  result.agents = agentsRun
  log(`Retro ${DATE} is already open (#${existing.number}): nothing filed; ${result.delta.length} finding(s) it does not carry`)
  return result
}
const fileKey = key({ index: indexKey, rank: key(rank), maxTickets: MAX_TICKETS, dryRun: DRY })
const filed = reuse('file', fileKey, (r) => r.dryRun || (!r.refused && !r.unreachable && r.parent && r.parent.number > 0)) || await run(filePrompt(SCRATCH, DATE, ISSUE, index, rank, toFile, fileKey), { label: 'file', phase: 'File', schema: FILED })
if (!filed) return { ...result, agents: agentsRun, halted: 'File returned nothing; check GitHub for a partial Retro issue before relaunching' }
result.filed = !DRY && !filed.refused && !filed.unreachable && filed.parent.number > 0
result.parent = filed.parent
result.tickets = filed.tickets.slice(0, MAX_TICKETS)
if (filed.tickets.length > MAX_TICKETS) log(`File reported ${filed.tickets.length} tickets; only ${MAX_TICKETS} are named`)
result.refused = filed.refused
result.unreachable = filed.unreachable
result.agents = agentsRun
log(`${DRY ? 'dry run: would file' : 'filed'} ${result.tickets.length} ticket(s) under ${filed.parent.url || filed.parent.file}`)
return result
