export const meta = {
  name: 'morning',
  description: 'Land the mechanical half of an overnight run: replay its pending tracker writes, verify and push its integration branch, open its one pull request, apply the confirmed answers, and hand over what only Adam runs',
  whenToUse: 'The morning after an overnight run, in a foreground session, launched by scriptPath; copy the launch line, with every argument, from docs/agents/landing-work.md -> "The morning\'s launches": the morning pass, the draft Confirm check, and the pass once Adam has landed the pull request',
  phases: [
    { title: 'Replay', detail: 'one agent: finds the one open Overnight <date> issue, checks for the pending-writes file first, then applies it in order, recording each line, and renames it when every line is applied; it returns no comment' },
    { title: 'Verify', detail: 'one agent, eight steps each a stop: the main checkout, the base, the lane worktrees, the check, the push with the hook, one pull request, CI on the exact head, a review record naming it; then, when the open pull request\'s description names an older head, one agent writes the description the script refreshed' },
    { title: 'Apply', detail: 'one index of the Overnight issue\'s comments, one body reader per batch of the questions and Confirm comments, one classifier, then at most maxParallel appliers: the chain (commit appliers on integrator/morning-<date>, and those that edit a ticket body another may write) one at a time, tracker-only appliers in parallel; follow-ups an answer asks for are filed, edits to other tickets applied; dry with no Confirm comment' },
    { title: 'Follow-ups', detail: 'one agent reads every open pull request into main, the follow-ups already filed and the open Apply tickets of every night; the script reads the follow-ups table of each one\'s latest review record, and one filer files the rows no issue holds' },
    { title: 'Hand-over', detail: 'one agent, always: posts the hand-over the script wrote as ## Morning <date> on the Overnight issue, every open Apply ticket a step of its own, oldest first' },
    { title: 'Check', detail: 'confirmFile only, one read agent: returns the issue\'s comments and the draft Confirm; the script sorts the questions with Apply\'s own code, prints the counts and lists, and stops (#302)' },
    { title: 'After', detail: 'after: true only, one agent: refuses until origin/main contains the integration tip, then closes, unassigns and removes worktrees whose tips are on origin/main, the integration worktree last, and prunes' },
  ],
}

// ---------------------------------------------------------------- what this is
// The morning spec (#78), built under #263; its proofs on real runs are #264. It does the landing's
// mechanical half and stops exactly where a human decision or an unreviewed change begins (#78 ->
// "Solution"): it never merges, because Adam runs the landing script (#78 grill Q2), and it reviews
// nothing, because a review is review-queue's or a /code-review session's (Q4). What it can decide
// in code it decides here, so its companion can hold it: which issue the run is, which questions
// exist and which a Confirm comment confirmed, whether a review record clears the head, how many
// appliers run and which run together, and the hand-over's text. The judgement and every tool call
// stay in the agents. Test companion: morning_tests.mjs beside this file.
//
// Launch (Workflow tool), by scriptPath so the landed file runs and not a session's snapshot: the
// three launch lines, each complete, are in docs/agents/landing-work.md -> "The morning's launches"
// (#489), and morning_tests.mjs holds them to badArguments below, so they are not copied here.
// Arguments (#78 -> "Arguments"), and no others: an unknown one is refused, so a misspelt `after`
// can never run the wrong mode.
//   date         YYYY-MM-DD, the night's date as its Overnight issue's title carries it. One of date
//   issue        and issue is required; issue is the Overnight <date> issue's number, and the date is
//                read off its title. Given both, they must agree.
//   repo         required  the main checkout, an absolute path. Written only under its run log.
//   wt           required  the worktree root the run used, as the overnight workflow names it:
//                          <wt>/overnight is the integration worktree, <wt>/<owner>-<task id> each
//                          ticket's (#136; <wt>/<owner> on runs before it).
//   after        optional  boolean, default false: the post-landing mode (#78 grill Q10).
//   maxParallel  optional  positive integer, default 3: agents in flight at once, and the most
//                          appliers Apply dispatches (#78 -> "Agent budget").
//   confirmFile  optional  an absolute path to a draft Confirm comment (#302). The run is then the
//                          draft check alone: it reads the issue and the file, sorts the questions
//                          the way Apply would, prints the result and stops. wt is not needed for it,
//                          and after is refused beside it.
//
// The draft check (#302). A Confirm is read by its first sentence and by the first word of each
// "Q<n>:" line, and nothing showed the human what a draft would do until it was posted: on 2026-10-22
// the draft for #286 opened with a bare "Confirm", which confirms only its override lines, and two of
// those opened "No". With confirmFile, one read agent returns the issue's comments and the file's
// text; the script sorts the questions through sortAnswers, the code Apply runs, with the draft as the
// one Confirm comment, and returns the counts and lists. It classifies nothing, runs no applier or
// verify agent and writes nothing. It fails only on a question number no questions comment yields; a
// leading "no" is a normal override, listed and never a failure.
//
// Agent budget (#78 -> "Agent budget"): Replay 1, Verify 1, Apply 1 comments index, one body reader
// per batch of the questions and Confirm comments, 1 classifier and at most maxParallel appliers,
// Follow-ups 1 reader and at most 1 filer (#292), Hand-over 1: ten at the default, besides the body
// readers. The issue lookup is the Replay agent's first step, not an agent of its own; `after` is
// one agent. A re-run whose open pull request's description names an older head than the branch's
// adds one, the description writer (#300), so such a run may take eleven.
//
// The comments are read by the phase that uses them (#319). Replay once returned the issue's body and
// every comment verbatim, and on 2026-10-22, with no pending-writes file, it took 7.4 minutes and 108k
// tokens, most of it carrying sheets of 263 questions through its return. Replay now checks for the
// pending-writes file before any replay step and returns no comment; Apply's readers return them, for
// the questions, the Confirm comments and the hand-over's open lists. On 2026-10-31 the one reader could
// not hand back #659's comments, on two runs: both hand-overs on #659 (6543854407, 6545142123) say a
// safety classifier stopped the verbatim return, and neither names a size or a return limit. #659's
// eleven comments are about 75 KB, its Confirm comment 18 KB. Whether size is what trips the classifier is
// not established; the change keeps any one return small and puts no body in the index. An index agent
// returns each comment's author, URL, size and the head of its body, the script picks the questions
// and Confirm comments with the readers' own predicates, and one reader per batch of at most
// BATCH_BYTES returns those bodies.
//
// Follow-ups (#292). A finding a review carries rather than holds on gets filed as a ticket. The
// review-queue workflow ends a record's findings with a follow-ups table of the carried findings that
// name no open ticket; the morning reads the table of the latest record on each open pull request
// into main, and files each row no issue already holds, with a "**Raised by:** #<pr>" line and a link
// to the record, as neither a sub-issue nor a blocker (docs/agents/tracker-policy.md). Neither Verify's
// verdict nor a Confirm comment gates it: the review decided the finding, and the night did not. Each
// carries the "**Owner:**" and "**Touches:**" lines its place derives, or is filed ready-for-human
// saying why it has none, and is filed only when tools/ticket_shape.py passes its body (#479).
//
// Open Apply tickets (#481). The same reader lists the open "Apply Q<n> of Overnight <date> to #<m>"
// tickets of every night, and the hand-over lists each, with this morning's, oldest first, as a step
// of its own: its target and its Confirm URL. See "open Apply tickets" below.
const A = args || {}
const KNOWN_ARGS = ['date', 'issue', 'repo', 'wt', 'after', 'maxParallel', 'confirmFile']
const badArguments = (a) => {
  const bad = []
  const abs = (p) => typeof p === 'string' && p.startsWith('/') && p.length > 1
  const count = (v) => Number.isInteger(v) && v > 0
  for (const k of Object.keys(a)) if (!KNOWN_ARGS.includes(k)) bad.push(`${k}: not an argument of this workflow; it takes ${KNOWN_ARGS.join(', ')}`)
  if (a.date === undefined && a.issue === undefined) bad.push('date or issue: one is required, e.g. "date": "2026-10-21" or "issue": 226 (the Overnight <date> issue)')
  if (a.date !== undefined && (typeof a.date !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(a.date))) bad.push('date: YYYY-MM-DD, the night the Overnight issue is titled for')
  if (a.issue !== undefined && !count(a.issue)) bad.push('issue: the Overnight <date> issue\'s number, a positive integer')
  if (!abs(a.repo)) bad.push('repo: the main checkout as an absolute path, e.g. /Users/adammedford/Projects/spa-platform')
  if (a.confirmFile !== undefined && !abs(a.confirmFile)) bad.push('confirmFile: the draft Confirm comment\'s file as an absolute path')
  if (a.confirmFile !== undefined && a.after === true) bad.push('confirmFile: the draft check runs alone; drop after, or drop confirmFile')
  if (a.confirmFile !== undefined && a.wt === undefined) { /* the draft check makes and reads no worktree */ } else if (!abs(a.wt)) bad.push('wt: the worktree root the run used, an absolute path, e.g. /Users/adammedford/.worktrees/overnight-2026-10-21')
  else if (abs(a.repo) && (a.wt === a.repo || a.wt.startsWith(a.repo.replace(/\/+$/, '') + '/'))) bad.push('wt: must lie outside repo; no worktree is made under the repository')
  if (a.after !== undefined && typeof a.after !== 'boolean') bad.push('after: true or false, a boolean (the string "true" is refused)')
  if (a.maxParallel !== undefined && !count(a.maxParallel)) bad.push('maxParallel: a positive integer (default 3)')
  return bad
}
const BAD_LAUNCH = badArguments(A)
if (BAD_LAUNCH.length) {
  // Stops here: no agent is started, nothing is read or written.
  return { halted: 'bad launch; no agent started. Fix each argument and relaunch with all of them:\n- ' + BAD_LAUNCH.join('\n- ') }
}
const REPO = A.repo.replace(/\/+$/, '')
const WTROOT = (A.wt || '').replace(/\/+$/, '')
const CHECK = A.confirmFile !== undefined
const AFTER = A.after === true
const MAX_PAR = A.maxParallel || 3
// The night's git-ignored log directory (.gitignore: .scratch/*/log/), where the overnight run left
// its check logs and its pending-writes file. The morning writes only under morning-<date>/ there,
// apart from the pending file's progress file and its rename, which #78 -> "Replay" puts beside it.
const LOG = `${REPO}/.scratch/overnight/log`
// Adam's account, the one whose Confirm comment confirms a night (#78 -> "Apply": "Adam's comment on
// the Overnight issue beginning Confirm"; ADR-0021). The Replay agent returns each comment's author as
// its login, so a Confirm from any other account confirms and overrides nothing.
const ADAM = 'adammedford'
const PARENT_ISSUE = 51 // the parent the overnight workflow files plan-row tickets under (#78 -> "Apply")
const READER = `python3 -I ${REPO}/tools/review_record.py` // the repository's one record reader (#67)
const TRAILER = 'Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>'
const paths = (date) => ({
  mdir: `${LOG}/morning-${date}`,
  pending: `${LOG}/pending-${date}.jsonl`,
  intBranch: `overnight/${date}`,
  intWt: `${WTROOT}/overnight`,
  morningBranch: `integrator/morning-${date}`,
  morningWt: `${WTROOT}/morning`,
  landWt: `${WTROOT}/land`,
})
// The integration pull request's merge subject describes the run and takes neither of the landing
// guide's row-landing forms, "Merge task <id>: ..." and "Merge part of task <id>: ...", because
// tools/roadmap.py reads every merge subject on main and the lane merges inside the branch already
// carry the rows (#78 -> "Verify" step 6; docs/agents/landing-work.md -> "Land it").
const ROW_FORM = /^Merge (part of )?task\b/i
const subjectFor = (date) => `Land the overnight run of ${date}`

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
// The overnight workflow's house rules by copy, with #78's three differences only: the integration
// branch and the morning branch may be pushed, with the hook running; the landing script is never
// run and no Confirm comment is written; and the pending-writes file is replayed, never written.
// A prohibition is prose and every command an agent is told to run is a backtick span, which is
// what the companion searches for forbidden commands.
const HOUSE = (mdir) => `House rules (a morning session; Adam reads the hand-over, and nobody answers questions while you run):
- The main checkout ${REPO} is Adam's. Never commit, checkout, switch, stash, reset or build there. The only files you may write there are under ${mdir}/, and the pending-writes file's progress file and its rename in ${LOG}/; git's own bookkeeping for fetches and worktrees is the only other change.
- The tracker is GitHub Issues. Run every \`gh\` command with \`cd ${REPO} &&\` in front so it resolves this repository. A multi-line comment or body goes through a file you write under ${mdir}/ and \`--body-file <that file>\`.
- If \`gh\` fails because GitHub is unreachable, stop and report it as your reason. The morning replays the pending-writes file and never writes one: a tracker write you cannot make is a failure you report, never a line you queue.
- Never write a comment that begins "Confirm" or "Approved" on any issue: a confirmation is Adam's act (docs/agents/tracker-policy.md; #78 grill Q7).
- Never run the landing script, tools/land.sh: Adam runs it (#78 grill Q2). Never merge, rebase or cherry-pick into any branch, never tag, never pass --admin or --no-verify, never force-push, never delete a branch (#71 owns deletion), and never remove a worktree by force.
- Only two branches may be pushed, each with the pre-push hook running: the integration branch and the morning branch. Before a push, compare the remote tip with the local one: equal, skip the push; not an ancestor of the local tip, stop, because someone else pushed and the morning never force-pushes.
- Begin every Bash command that touches a worktree with \`cd <worktree> &&\`, and use \`git -C <path>\` for every git command. A bare \`git commit\` lands on Adam's branch.
- To change an existing file with Edit or Write, open it with the Read tool first in this session, not with \`cat\`, \`head\` or \`sed -n\`: for a file you will change, this rule overrides the auto-mode advice to read with Bash. Open it with Read again after anything else wrote it (a script, the check, a merge, \`git\`). Otherwise make the change with a short script written to a file under your scratchpad and run by its path. Edit and Write refuse a file not read since its last write: 16 calls went to that refusal on 2026-10-18 and 16 over the four launches from 2026-10-19 to 2026-10-21 (#109).
- Worktrees live under ${WTROOT}. Never create one under the repo.
- AGENTS.md's rules hold: owner paths only (OWNERSHIP.md); never log a secret, token, cookie, code or state value.
- Commit messages end with the attribution line "${TRAILER}" (or the one this session's own attribution reminder names, if it differs).
- Write each OUTPUT FILE and PHASE FILE line's path exactly as given, and no other file outside a worktree under ${WTROOT} but a script the edit rule above has you write under your scratchpad.`

// The lookup every first agent does before anything else (#78 user story 38): the run is one open
// issue titled exactly "Overnight <date>", or nothing happens. The script judges the listing the
// agent returns again, so the refusal does not rest on the agent alone.
const LOOKUP = (then) => (A.issue
  ? `1. The issue. Run \`cd ${REPO} && gh issue view ${A.issue} --json number,title,state,url\`. listing is a one-element list of {number, title, state} from it. Unless its title is exactly "Overnight YYYY-MM-DD"${A.date ? ` with the date ${A.date}` : ''}, stop here: return the listing with everything else empty or zero, and read, write and replay nothing more. ${then}`
  : `1. The issue. Run \`cd ${REPO} && gh issue list --state open --search "Overnight ${A.date} in:title" --json number,title,state --limit 100\`. listing is its output, verbatim. Unless exactly one issue in it is titled exactly "Overnight ${A.date}", stop here: return the listing with everything else empty or zero, and read, write and replay nothing more. ${then}`)

// ---------------------------------------------------------------- schemas
const COUNTS = { type: 'object', properties: { applied: { type: 'integer' }, skipped: { type: 'integer' }, failed: { type: 'integer' } }, required: ['applied', 'skipped', 'failed'] }
// The pending-writes file's verbs, each counted. create is a change request the overnight run could
// not file (#433; #421 Q2, Q3): only the overnight run queues one, and a create line counts as replayed.
const VERBS = ['comment', 'edit', 'close', 'create']
// What the Replay agent did with each create line it ran: the request it found or filed, and per
// ticket the request blocks, the edge and the comment naming its number (judgeCreates reads it).
const CREATED = {
  type: 'object',
  properties: {
    line: { type: 'integer' }, title: { type: 'string' }, number: { type: 'integer' }, found: { type: 'boolean' }, filed: { type: 'boolean' },
    tickets: { type: 'array', items: { type: 'object', properties: { number: { type: 'integer' }, edge: { type: 'string', enum: ['added', 'present', 'none'] }, comment: { type: 'string', enum: ['posted', 'present', 'none'] } }, required: ['number', 'edge', 'comment'] } },
  },
  required: ['line', 'title', 'number', 'found', 'filed', 'tickets'],
}
const LISTED = { type: 'array', items: { type: 'object', properties: { number: { type: 'integer' }, title: { type: 'string' }, state: { type: 'string' } }, required: ['number', 'title', 'state'] } }
const COMMENT = { type: 'object', properties: { author: { type: 'string' }, createdAt: { type: 'string' }, url: { type: 'string' }, body: { type: 'string' } }, required: ['author', 'createdAt', 'url', 'body'] }
const REPLAY = {
  type: 'object',
  properties: {
    listing: LISTED,
    issue: { type: 'object', properties: { number: { type: 'integer' }, title: { type: 'string' }, url: { type: 'string' }, state: { type: 'string' } }, required: ['number', 'title', 'url', 'state'] },
    phaseFiles: { type: 'array', items: { type: 'object', properties: { name: { type: 'string' }, inputs: { type: 'string' }, result: { type: 'object' } }, required: ['name', 'inputs', 'result'] } },
    counts: { type: 'object', properties: Object.fromEntries(VERBS.map((v) => [v, COUNTS])), required: VERBS },
    creates: { type: 'array', items: CREATED },
    pending: { type: 'string' }, renamed: { type: 'string' },
    stoppedAt: { type: 'integer' }, reason: { type: 'string' },
  },
  required: ['listing', 'issue', 'phaseFiles', 'counts', 'creates', 'pending', 'renamed', 'stoppedAt', 'reason'],
}
const COMMENT_INDEXED = { type: 'object', properties: { author: { type: 'string' }, createdAt: { type: 'string' }, url: { type: 'string' }, bytes: { type: 'integer' }, head: { type: 'string' } }, required: ['author', 'createdAt', 'url', 'bytes', 'head'] }
const COMMENTS_INDEX = {
  type: 'object',
  properties: {
    issue: { type: 'object', properties: { number: { type: 'integer' }, comments: { type: 'array', items: COMMENT_INDEXED } }, required: ['number', 'comments'] },
    error: { type: 'string' },
  },
  required: ['issue', 'error'],
}
const COMMENTS_BATCH = {
  type: 'object',
  properties: { comments: { type: 'array', items: COMMENT }, error: { type: 'string' } },
  required: ['comments', 'error'],
}
const VERIFY = {
  type: 'object',
  properties: {
    outcome: { type: 'string', enum: ['ok', 'stop'] }, step: { type: 'integer' }, reason: { type: 'string' },
    base: { type: 'string' }, forkPoint: { type: 'string' }, head: { type: 'string' }, branch: { type: 'string' },
    pr: { type: 'object', properties: { number: { type: 'integer' }, url: { type: 'string' } }, required: ['number', 'url'] },
    prBodyFile: { type: 'string' }, mergeBodyFile: { type: 'string' }, mergeSubject: { type: 'string' },
    prReused: { type: 'boolean' }, prBody: { type: 'string' },
    merges: { type: 'array', items: { type: 'object', properties: { sha: { type: 'string' }, subject: { type: 'string' } }, required: ['sha', 'subject'] } },
    ci: { type: 'string', enum: ['green', 'red', 'pending', 'none'] }, ciUrl: { type: 'string' }, failingJobs: { type: 'array', items: { type: 'string' } },
    pushed: { type: 'boolean' }, mainMoved: { type: 'boolean' }, findings: { type: 'array', items: { type: 'string' } },
    worktrees: { type: 'array', items: { type: 'object', properties: { path: { type: 'string' }, branch: { type: 'string' }, tip: { type: 'string' }, clean: { type: 'boolean' }, ancestor: { type: 'boolean' }, lane: { type: 'string' }, partner: { type: 'string' } }, required: ['path', 'branch', 'tip', 'clean', 'ancestor', 'lane', 'partner'] } },
    oldTip: { type: 'string' },
    lanes: { type: 'array', items: { type: 'object', properties: { issue: { type: 'integer' }, task: { type: 'string' }, outcome: { type: 'string', enum: ['integrated', 'partial', 'failed'] }, complete: { type: 'boolean' }, mergeSha: { type: 'string' }, outstanding: { type: 'string' } }, required: ['issue', 'task', 'outcome', 'complete', 'mergeSha', 'outstanding'] } },
    claimed: { type: 'array', items: { type: 'string' } },
    record: { type: 'object', properties: { verdict: { type: 'string', enum: ['merge', 'hold', 'none'] }, head: { type: 'string' }, url: { type: 'string' }, reader: { type: 'string' } }, required: ['verdict', 'head', 'url', 'reader'] },
  },
  required: ['outcome', 'step', 'reason', 'base', 'forkPoint', 'head', 'branch', 'pr', 'prBodyFile', 'mergeBodyFile', 'mergeSubject', 'prReused', 'prBody', 'merges', 'ci', 'ciUrl', 'failingJobs', 'pushed', 'mainMoved', 'findings', 'worktrees', 'oldTip', 'lanes', 'claimed', 'record'],
}
const KINDS = ['no-repo-change', 'integrator-edit', 'workstream-ticket', 'decision-to-record', 'follow-up', 'ticket-edit']
// Every question carries the follow-up and ticket-edit fields, empty when its kind is another (#320),
// so a classifier that leaves one out fails the schema rather than reading as one that stated nothing.
const FOLLOW_UP = { type: 'object', properties: { title: { type: 'string' }, touches: { type: 'array', items: { type: 'string' } }, blockedBy: { type: 'array', items: { type: 'integer' } }, what: { type: 'string' } }, required: ['title', 'touches', 'blockedBy', 'what'] }
const CLASSIFY = {
  type: 'object',
  properties: {
    questions: { type: 'array', items: { type: 'object', properties: { q: { type: 'integer' }, issue: { type: 'integer' }, task: { type: 'string' }, answer: { type: 'string' }, kind: { type: 'string', enum: KINDS }, summary: { type: 'string' }, target: { type: 'integer' }, removeBoxes: { type: 'array', items: { type: 'string' } }, addBlockedBy: { type: 'array', items: { type: 'integer' } }, unstated: { type: 'string' }, followUp: FOLLOW_UP }, required: ['q', 'issue', 'task', 'answer', 'kind', 'summary', 'target', 'removeBoxes', 'addBlockedBy', 'unstated', 'followUp'] } },
    rewrites: { type: 'array', items: { type: 'object', properties: { issue: { type: 'integer' }, questions: { type: 'array', items: { type: 'integer' } }, why: { type: 'string' } }, required: ['issue', 'questions', 'why'] } },
  },
  required: ['questions', 'rewrites'],
}
const MADE = (extra) => ({ type: 'array', items: { type: 'object', properties: { ...extra, questions: { type: 'array', items: { type: 'integer' } } }, required: [...Object.keys(extra), 'questions'] } })
const APPLIED = {
  type: 'object',
  properties: {
    unit: { type: 'string' },
    commits: MADE({ sha: { type: 'string' }, subject: { type: 'string' } }),
    issues: MADE({ number: { type: 'integer' }, url: { type: 'string' }, title: { type: 'string' } }),
    candidates: MADE({ url: { type: 'string' } }),
    rewrites: { type: 'array', items: { type: 'object', properties: { issue: { type: 'integer' }, preserved: { type: 'string' } }, required: ['issue', 'preserved'] } },
    edits: MADE({ issue: { type: 'integer' }, preserved: { type: 'string' }, removed: { type: 'array', items: { type: 'string' } } }),
    edges: MADE({ issue: { type: 'integer' }, blockedBy: { type: 'array', items: { type: 'integer' } } }),
    skipped: { type: 'array', items: { type: 'object', properties: { q: { type: 'integer' }, why: { type: 'string' } }, required: ['q', 'why'] } },
    pushed: { type: 'object', properties: { branch: { type: 'string' }, tip: { type: 'string' }, ok: { type: 'boolean' }, reason: { type: 'string' } }, required: ['branch', 'tip', 'ok', 'reason'] },
    refused: { type: 'string' }, unreachable: { type: 'boolean' },
  },
  required: ['unit', 'commits', 'issues', 'candidates', 'rewrites', 'edits', 'edges', 'skipped', 'pushed', 'refused', 'unreachable'],
}
const ENTRY = {
  type: 'object',
  properties: {
    body: { type: 'string' }, author_association: { type: 'string' }, html_url: { type: 'string' },
    created_at: { type: 'string' }, submitted_at: { type: 'string' }, state: { type: 'string' },
  },
  required: ['body', 'author_association', 'html_url'],
}
const FOLLOWUPS = {
  type: 'object',
  properties: {
    prs: { type: 'array', items: { type: 'object', properties: { number: { type: 'integer' }, comments: { type: 'array', items: ENTRY }, reviews: { type: 'array', items: ENTRY } }, required: ['number', 'comments', 'reviews'] } },
    filed: { type: 'array', items: { type: 'object', properties: { number: { type: 'integer' }, title: { type: 'string' }, body: { type: 'string' }, url: { type: 'string' } }, required: ['number', 'title', 'body', 'url'] } },
    applyTickets: { type: 'array', items: { type: 'object', properties: { number: { type: 'integer' }, title: { type: 'string' }, body: { type: 'string' }, url: { type: 'string' }, state: { type: 'string' }, author: { type: 'object', properties: { login: { type: 'string' } }, required: ['login'] } }, required: ['number', 'title', 'body', 'url', 'state', 'author'] } },
    ownership: { type: 'string' },
    error: { type: 'string' },
  },
  required: ['prs', 'filed', 'applyTickets', 'ownership', 'error'],
}
const ISSUED = { type: 'array', items: { type: 'object', properties: { pr: { type: 'integer' }, title: { type: 'string' }, number: { type: 'integer' }, url: { type: 'string' } }, required: ['pr', 'title', 'number', 'url'] } }
const SHAPE_REFUSED = { type: 'array', items: { type: 'object', properties: { pr: { type: 'integer' }, title: { type: 'string' }, why: { type: 'string' } }, required: ['pr', 'title', 'why'] } }
const FILED = { type: 'object', properties: { filed: ISSUED, skipped: ISSUED, refused: SHAPE_REFUSED, unreachable: { type: 'boolean' }, reason: { type: 'string' } }, required: ['filed', 'skipped', 'refused', 'unreachable', 'reason'] }
const HANDED = { type: 'object', properties: { url: { type: 'string' }, posted: { type: 'boolean' }, reason: { type: 'string' } }, required: ['url', 'posted', 'reason'] }
const REFRESHED = { type: 'object', properties: { edited: { type: 'boolean' }, reason: { type: 'string' } }, required: ['edited', 'reason'] }
const AFTERED = {
  type: 'object',
  properties: {
    listing: LISTED,
    issue: { type: 'object', properties: { number: { type: 'integer' }, title: { type: 'string' }, url: { type: 'string' } }, required: ['number', 'title', 'url'] },
    tip: { type: 'string' }, originMain: { type: 'string' }, contained: { type: 'boolean' },
    closed: { type: 'array', items: { type: 'integer' } }, unassigned: { type: 'array', items: { type: 'integer' } },
    removed: { type: 'array', items: { type: 'string' } }, kept: { type: 'array', items: { type: 'string' } },
    pruned: { type: 'boolean' }, reason: { type: 'string' },
  },
  required: ['listing', 'issue', 'tip', 'originMain', 'contained', 'closed', 'unassigned', 'removed', 'kept', 'pruned', 'reason'],
}

// ---------------------------------------------------------------- the lookup, judged here
const TITLE = /^Overnight (\d{4}-\d{2}-\d{2})$/
const judgeLookup = (listing) => {
  const list = Array.isArray(listing) ? listing : []
  if (A.issue) {
    const it = list.find((i) => i.number === A.issue)
    const m = it && TITLE.exec(it.title)
    if (!m) return { refused: `issue #${A.issue} is not titled "Overnight YYYY-MM-DD" (${it ? JSON.stringify(it.title) : 'not found'}); nothing was replayed, verified or applied` }
    if (A.date && A.date !== m[1]) return { refused: `issue #${A.issue} is "${it.title}", not "Overnight ${A.date}"; nothing was replayed, verified or applied` }
    return { issue: A.issue, date: m[1] }
  }
  const hits = list.filter((i) => i.title === `Overnight ${A.date}` && String(i.state).toUpperCase() === 'OPEN')
  if (hits.length !== 1) return { refused: `found ${hits.length} open issues titled "Overnight ${A.date}"${hits.length ? ` (${hits.map((i) => '#' + i.number).join(', ')})` : ''}, and the morning runs against exactly one; nothing was replayed, verified or applied` }
  return { issue: hits[0].number, date: A.date }
}
const replayedAny = (counts) => Object.values(counts || {}).some((c) => c && (c.applied || c.failed))
// Where Replay stopped, or "" when no line failed (#319). A stop is a failed line: stoppedAt above zero
// names it, and a failed count with stoppedAt 0 is still one, whose number the agent did not give, so
// it is refused rather than read as no stop (CODING_STANDARDS.md -> "A check fails closed").
const replayStop = (r) => (r.stoppedAt > 0 ? `line ${r.stoppedAt}`
  : Object.values(r.counts || {}).some((c) => c && c.failed > 0) ? 'a line it did not number' : '')
// A Replay string field holding only a pair of quotes, or only whitespace, is empty (#299). On
// 2026-10-22 no pending-writes file existed and the Replay agent returned pending, renamed and reason
// as the two-character string "", which the run read as a stop and refused beside the real one.
const blankField = (s) => (typeof s === 'string' && /^\s*(?:""\s*)?$/.test(s) ? '' : s)
// The create lines Replay ran, judged against the rule its prompt states (#433; #421 Q3): a request
// found by its title files nothing, one not found is filed once, and either way every ticket it blocks
// carries its edge and a comment naming its number, since the lane's merge body names it by title
// alone. What the agent reports against that rule is a refusal naming the line, never read as done
// (CODING_STANDARDS.md -> "A check fails closed"), and so is a create line counted applied that it
// does not report.
const EDGE_SAID = { added: 'edge added', present: 'edge already there', none: 'no edge' }
const COMMENT_SAID = { posted: 'number commented', present: 'number already commented', none: 'no comment' }
const createsOf = (r) => (Array.isArray(r && r.creates) ? r.creates : [])
const judgeCreates = (r) => {
  const out = []
  const creates = createsOf(r)
  for (const c of creates) {
    const at = `Replay's create line ${c.line} ("${c.title}")`
    if (c.found && c.filed) out.push(`${at} was found open and filed again, so the request may be filed twice: check the open change-request issues for its title`)
    if ((c.found || c.filed) && !(c.number > 0)) out.push(`${at} was reported found or filed with no number, so no edge or comment can name it`)
    if (!c.found && !c.filed) continue
    for (const t of c.tickets || []) {
      if (!['added', 'present'].includes(t.edge)) out.push(`${at} blocks #${t.number}, which carries no edge from it`)
      if (!['posted', 'present'].includes(t.comment)) out.push(`${at} blocks #${t.number}, which carries no comment naming its number`)
    }
  }
  const applied = ((r && r.counts && r.counts.create) || {}).applied || 0
  const done = creates.filter((c) => c.found || c.filed).length
  if (applied > done) out.push(`Replay counts ${applied} create line(s) applied and reports ${done} found or filed, so the rest cannot be checked for a duplicate, an edge or a comment`)
  return out
}
const createLines = (r) => createsOf(r).map((c) => `create line ${c.line}: #${c.number} ${c.title}, ${c.found && !c.filed ? 'found open, filed nothing' : c.filed && !c.found ? 'filed' : c.found ? 'found open and filed again' : 'neither found nor filed'}; ${(c.tickets || []).length ? c.tickets.map((t) => `#${t.number}: ${EDGE_SAID[t.edge] || t.edge}, ${COMMENT_SAID[t.comment] || t.comment}`).join('; ') : 'blocks nothing'}`)

// ---------------------------------------------------------------- follow-ups, read here
// review-queue.js's record recogniser and follow-ups table, by copy: a workflow script imports
// nothing, and morning_tests.mjs holds each line below equal to its twin there, so a change to the
// grammar is an edit to both scripts together (and to tools/review_record.py for the record line).
const PY_LINE_BREAK = /\r\n|[\n\v\f\r\x1c-\x1e\x85\u2028\u2029]/
const PY_SPACE = '[\\t\\n\\v\\f\\r\\x1c-\\x1f \\x85\\xa0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000]'
const PY_STRIP = new RegExp(`^${PY_SPACE}+|${PY_SPACE}+$`, 'g')
const RECORD = /^Reviewed ([0-9a-f]{40}) against ([0-9a-f]{40}): (merge|hold)$/
const REPOSITORY_ACCOUNTS = ['OWNER', 'MEMBER', 'COLLABORATOR']
const FOLLOW_UP_HEAD = '| Finding | Axis | Where | First carried by |'
const THIS_RECORD = 'this record'
const followUpsOf = (body, url = '') => {
  const lines = String(body || '').split(PY_LINE_BREAK).map((l) => l.trim())
  const at = lines.indexOf(FOLLOW_UP_HEAD)
  if (at < 0) return []
  const rows = []
  for (const line of lines.slice(at + 2)) {
    if (!line.startsWith('|') || !line.endsWith('|')) break
    const cells = line.slice(1, -1).split(/(?<!\\)\|/).map((c) => c.trim().replace(/\\\|/g, '|'))
    if (cells.length === 4 && cells[0]) rows.push({ title: cells[0], axis: cells[1], where: cells[2], firstCarried: cells[3] === THIS_RECORD ? url : cells[3] })
  }
  return rows
}
const sameFinding = (a, b) => String(a).toLowerCase().replace(/\s+/g, ' ').trim() === String(b).toLowerCase().replace(/\s+/g, ' ').trim()
// The latest record on a pull request, as review-queue.js's recordsIn orders them: every record line
// in its comments and its COMMENTED reviews from an account the repository let in, by time.
const latestRecordOf = (pr) => {
  const found = []
  const read = (entry, at) => {
    if (!entry || typeof entry.body !== 'string' || !REPOSITORY_ACCOUNTS.includes(entry.author_association)) return
    for (const raw of entry.body.split(PY_LINE_BREAK)) if (RECORD.test(raw.replace(PY_STRIP, ''))) found.push({ url: entry.html_url || '', at: entry[at] || '', body: entry.body })
  }
  for (const c of pr.comments || []) read(c, 'created_at')
  for (const r of pr.reviews || []) if (r && r.state === 'COMMENTED') read(r, 'submitted_at')
  found.sort((x, y) => (x.at < y.at ? -1 : x.at > y.at ? 1 : 0))
  return found[found.length - 1] || null
}
// A follow-up is already filed when an issue carries its finding as its title and names the pull
// request on its Raised-by line, the shape this script files and a hand-filed one can take.
const filedAs = (filed, pr, title) => (filed || []).find((i) => i && sameFinding(i.title, title)
  && new RegExp(`^\\*\\*Raised by:\\*\\*[^\\n]*#${pr}(?!\\d)`, 'm').test(String(i.body || ''))) || null

// ---------------------------------------------------------------- follow-ups, owners and places
// #479. A follow-up filed with a Raised-by line alone failed tools/ticket_shape.py on owner-line the
// moment it was labelled ready-for-agent (#458 to #461; #386 to #401), though its place was in hand.
// So its Touches line is the paths the finding's Where cell names, and its Owner line the role
// OWNERSHIP.md derives from them. OWNERSHIP.md's block is read as .github/path-ownership.py's rules()
// and owner_of() read it: the first fenced block, "#" opening a comment, the last matching line
// winning, "*" every path, a pattern ending "/" a directory, any other an fnmatch pattern. A path is
// read as tools/ticket_shape.py's owner_of() reads a Touches path: a directory written without its
// slash (`tools/scripts`) is that directory. morning_tests.mjs evaluates this section and holds its
// owners equal to ticket_shape's on every path OWNERSHIP.md names, so it uses nothing defined outside it.
const ownershipRules = (text) => {
  const rules = []
  let inside = false
  for (const line of String(text || '').split(/\r\n|\n|\r/)) {
    if (line.startsWith('```')) {
      if (inside) break
      inside = true
      continue
    }
    if (!inside) continue
    const fields = line.split('#')[0].trim().split(/\s+/).filter(Boolean)
    if (fields.length >= 2) rules.push([fields[0], fields[1]])
  }
  return rules
}
// fnmatch's "*" and "?", which match a "/" too; no line of OWNERSHIP.md uses a "[...]" set.
const fnmatchOf = (pattern) => new RegExp(`^${pattern.split('').map((c) => (c === '*' ? '[\\s\\S]*' : c === '?' ? '[\\s\\S]' : c.replace(/[.+^${}()|[\]\\]/g, '\\$&'))).join('')}$`)
const ruleMatches = (pattern, p) => {
  if (pattern === '*') return true
  const anchored = pattern.replace(/^\/+/, '')
  if (anchored.endsWith('/')) return p.startsWith(anchored)
  return p === anchored || fnmatchOf(anchored).test(p)
}
const ownerOfPath = (rules, p) => {
  const bare = String(p).trim().replace(/^\/+/, '').replace(/\/+$/, '')
  const read = rules.some(([pattern]) => pattern.replace(/^\/+/, '') === bare + '/') ? bare + '/' : bare
  let found = null
  for (const [pattern, owner] of rules) if (ruleMatches(pattern, read)) found = owner
  return found
}
// The paths a Where cell names, in its order, once each. The cell is prose a reviewer wrote
// (".github/workflows/ci.yml:657-658", "a.cs; b.cs", "76704c5:ci.yml lines 321-326 | tools/scripts",
// "#244 Q9; docs/runbooks/x.md", "fixtures/TestIdp/SigningKeyRing.cs NewKey", "the pull request
// description"), so a token is a path by tools/ticket_shape.py's BARE_PATH, once its code-span ticks,
// a commit prefix ("76704c5:") and a ":line" or ":line-line" suffix are taken off; a token with no "/"
// counts only as a dotfile or a name ending in a lower-case extension, so "SigningKeyRing.NewKey" is
// not read as a file.
const PLACE_PATH = /^(?=[^/]*\/|\.?[\w-]*[\w-]\.[\w-]|\.[\w-])[\w./*-]+$/
const NOT_PLACES = ['e.g', 'i.e', 'and/or']
const pathsOf = (where) => {
  const out = []
  for (const raw of String(where || '').split(/[\s,;|]+/)) {
    const t = raw.replace(/^[`'"([]+|[`'")\]]+$/g, '').replace(/[.,:;]+$/, '')
      .replace(/^[0-9a-f]{7,40}:(?=\S)/, '').replace(/(:\d+(-\d+)?)+$/, '').replace(/[.:]+$/, '')
    if (!t || NOT_PLACES.includes(t) || !PLACE_PATH.test(t)) continue
    if (!t.includes('/') && !/^\.[\w-]+$/.test(t) && !/\.[a-z0-9]{1,8}$/.test(t)) continue
    if (!out.includes(t)) out.push(t)
  }
  return out
}
// A follow-up's owner, Touches paths and label. One owner: its role, filed needs-triage as before. No
// path, a path no line owns, or paths of two owners: no Touches line, since ticket_shape fails two
// owners' paths under any label (one-owner); the integrator, who triages it, as its Owner; filed
// ready-for-human with why, which names the paths and their roles.
const placeOf = (rules, where) => {
  const touches = pathsOf(where)
  if (!touches.length) return { owner: 'integrator', touches, label: 'ready-for-human', why: `the finding names no repository path (its place: ${where || 'none named'}), so no owner is derived from it; give it a path and an owner, then relabel it` }
  const byOwner = {}
  for (const p of touches) {
    const owner = ownerOfPath(rules, p) || 'no owner'
    ;(byOwner[owner] = byOwner[owner] || []).push(p)
  }
  const owners = Object.keys(byOwner).sort()
  const listed = owners.map((o) => `${o}: ${byOwner[o].join(', ')}`).join('; ')
  if (owners.length > 1) return { owner: 'integrator', touches, label: 'ready-for-human', why: `the finding names paths of two owners or more (${listed}), and a ticket has one owner (docs/agents/tracker-policy.md); split it, one ticket per owner, then relabel each` }
  if (owners[0] === 'no owner') return { owner: 'integrator', touches, label: 'ready-for-human', why: `no line of OWNERSHIP.md owns ${byOwner['no owner'].join(', ')}, so no owner is derived from it` }
  return { owner: owners[0], touches, label: 'needs-triage', why: '' }
}

// ---------------------------------------------------------------- follow-ups, their bodies
const followUpBody = (pr, record, f, place) => [
  `**Raised by:** #${pr}, from its review record ${record}`,
  `**Owner:** ${place.owner}`,
  place.label === 'needs-triage' ? `**Touches:** ${place.touches.map((p) => '`' + p + '`').join(', ')}` : `**Filed ready-for-human:** ${place.why}`,
  '',
  '## What to build',
  '',
  `The review of #${pr} carried this finding rather than holding on it (${f.axis || 'no'} axis), first carried by ${f.firstCarried || record}: ${f.title}, at ${f.where || 'no place named'}. The record lists it under "## Follow-ups", which names no open ticket for it; this issue is that ticket.`,
  '',
  '## Acceptance criteria',
  '',
  `- [ ] ${f.title}: fixed, or declined with the reason in this issue's Answer.`,
].join('\n')
// The rows to file and those already filed, from what the reader returned: per open pull request, the
// latest record's table, each row checked against the issues already filed, and its owner, Touches
// and label derived from its place under the OWNERSHIP.md the reader returned (#479).
const followUpsToFile = (read, mdir) => {
  const toFile = []
  const already = []
  const rules = ownershipRules(read.ownership)
  for (const pr of read.prs || []) {
    const latest = pr && Number.isInteger(pr.number) ? latestRecordOf(pr) : null
    if (!latest) continue
    followUpsOf(latest.body, latest.url).forEach((f) => {
      const hit = filedAs(read.filed, pr.number, f.title)
      if (hit) { already.push({ pr: pr.number, title: f.title, number: hit.number, url: hit.url || '' }); return }
      const k = toFile.filter((x) => x.pr === pr.number).length + 1
      const place = placeOf(rules, f.where)
      toFile.push({ pr: pr.number, title: f.title, record: latest.url, label: place.label, why: place.why, file: `${mdir}/followups/pr-${pr.number}-${k}.md`, body: followUpBody(pr.number, latest.url, f, place) })
    })
  }
  return { toFile, already }
}

// ---------------------------------------------------------------- open Apply tickets (#481)
// An answer the morning cannot state as an edit is filed as "Apply Q<n> of Overnight <date> to #<m>",
// and the part an edit leaves unstated as "The rest of Q<n> of Overnight <date> on #<m>" (planOf).
// Each waited for a launch prep to find it: on 2026-10-28 #476's found nine open, three of them three
// days old. So the hand-over lists every open one, from any night, as a step of its own, oldest first
// (by number, which GitHub gives in filing order), each with its target and the Confirm comment its
// Raised-by line names (followUpTicket). Two sources, one parser: the follow-ups reader's listing of
// the open issues, and the tickets this morning's appliers filed, which a listing taken minutes later
// may not show yet. A ticket the listing names as closed is not listed. A listed ticket counts only when
// Adam's account opened it, as the morning's appliers do: a title and a Raised-by line are all it takes
// to read as a step, so an issue anyone else opened in that shape is never handed over as one
// (CODING_STANDARDS.md -> "An authorisation is read from its author").
const APPLY_TITLE = /^(?:Apply Q(\d+) of Overnight (\d{4}-\d{2}-\d{2}) to|The rest of Q(\d+) of Overnight (\d{4}-\d{2}-\d{2}) on) #(\d+)$/
const applyTicketOf = (i) => {
  const m = i && Number.isInteger(i.number) ? APPLY_TITLE.exec(String(i.title || '').trim()) : null
  if (!m) return null
  const c = /^\*\*Raised by:\*\*[^\n]*, confirmed at (https:\/\/\S+)/m.exec(String(i.body || ''))
  return { number: i.number, title: String(i.title).trim(), url: i.url || '', target: Number(m[5]), confirm: c ? c[1] : '' }
}
const openApplyTickets = (listed, filedToday) => {
  const closed = new Set((listed || []).filter((i) => i && String(i.state).toUpperCase() !== 'OPEN').map((i) => i.number))
  const byNumber = new Map()
  for (const i of [...(listed || []).filter((x) => x && x.author && x.author.login === ADAM), ...filedToday]) {
    const t = applyTicketOf(i)
    if (t && !closed.has(t.number) && !(byNumber.get(t.number) || {}).confirm) byNumber.set(t.number, t)
  }
  return [...byNumber.values()].sort((a, b) => a.number - b.number)
}

// ---------------------------------------------------------------- the questions, read here
// The night's questions are comments on the Overnight issue, never its body (#104): each headed
// "## Questions: #<issue>, <task id>, <title>", numbered Q1..Qn continuously across them, each
// with its "Recommended:" and "Answer:" lines. The overnight report does not pin the markup, and
// the nights have written "**Q1.** text" (2026-10-21), "Q1. **text**" with the lines under it
// indented, some as "- Answer:" list items (2026-10-20), and "**Q1** (#125 Q1). **text**"
// (2026-10-19). So a question opens at a line that begins with any of the three, "Q1 to Q15, from
// ..." opens none, and a Recommended: or Answer: line may be indented or a list item. A comment over 60,000
// characters is split into parts whose headings end "(part k of m)"; the parts are joined in k order
// before reading, so a question cut at a part boundary is read whole. A ticket whose parts are not
// all present, a questions comment with no question in it, or a number that appears twice, is a
// refusal, and those questions stay unread: nothing is applied from a partial read.
const HEADING = /^## Questions: #(\d+), ([^,]+), (.*)$/
const PART = /\s*\(part (\d+) of (\d+)\)\s*$/
const Q_MARK = /^[ \t]*(?:\*\*Q(\d+)\.?\*\*|Q(\d+)\.(?=[ \t]))[ \t]*/gm
// What makes a comment one the readers read, said once: batchesOf picks the comments to fetch with
// these two functions, on the head of each body, so it cannot pick a narrower set than the readers do.
const headingOf = (body) => HEADING.exec(String(body || '').split('\n')[0].trim())
const isConfirm = (author, body) => author === ADAM && /^Confirm\b/.test(String(body || '').trimStart())
const readQuestions = (comments) => {
  const groups = new Map()
  for (const c of comments) {
    const body = String(c.body || '')
    const nl = body.indexOf('\n')
    const m = headingOf(body)
    if (!m) continue
    const pm = PART.exec(m[3])
    const title = m[3].replace(PART, '')
    const k = `${m[1]}|${m[2]}|${title}`
    if (!groups.has(k)) groups.set(k, { issue: Number(m[1]), task: m[2].trim(), title, of: pm ? Number(pm[2]) : 1, parts: [] })
    groups.get(k).parts.push({ k: pm ? Number(pm[1]) : 1, text: nl < 0 ? '' : body.slice(nl + 1) })
  }
  const found = []
  const refusals = []
  for (const g of groups.values()) {
    const ks = new Set(g.parts.map((p) => p.k))
    if (ks.size !== g.of || g.parts.length !== g.of) {
      refusals.push(`the questions of #${g.issue} are split into ${g.of} parts and ${ks.size} distinct part(s) were found; none of them is read`)
      continue
    }
    const text = [...g.parts].sort((a, b) => a.k - b.k).map((p) => p.text).join('\n')
    const marks = [...text.matchAll(Q_MARK)]
    if (!marks.length) {
      refusals.push(`the questions comment of #${g.issue} carries no question the morning can read (none opens "**Q<n>.**", "**Q<n>**" or "Q<n>."); none of it is read`)
      continue
    }
    marks.forEach((mk, i) => {
      const block = text.slice(mk.index + mk[0].length, i + 1 < marks.length ? marks[i + 1].index : text.length)
      const rec = /^[ \t]*(?:[-*][ \t]+)?Recommended:[ \t]*(.*)$/m.exec(block)
      const answers = [...block.matchAll(/^[ \t]*(?:[-*][ \t]+)?Answer:[ \t]*(.*)$/gm)]
      found.push({
        q: Number(mk[1] || mk[2]), issue: g.issue, task: g.task, title: g.title,
        text: block.split(/^[ \t]*(?:[-*][ \t]+)?Recommended:/m)[0].trim(),
        recommendation: rec ? rec[1].trim() : '',
        answer: answers.length ? answers[answers.length - 1][1].trim() : '',
      })
    })
  }
  const twice = new Set(found.filter((e, i) => found.findIndex((x) => x.q === e.q) !== i).map((e) => e.q))
  for (const q of twice) refusals.push(`Q${q} is numbered more than once (${found.filter((e) => e.q === q).map((e) => '#' + e.issue).join(', ')}); none of them is applied`)
  return { questions: found.filter((e) => !twice.has(e.q)).sort((a, b) => a.q - b.q), refusals }
}

// A confirmation is Adam's comment on the Overnight issue beginning "Confirm" (ADR-0021; #78 grill
// Q1, Q7). Its first sentence names the questions it covers ("Q1 to Q57", "Q1-Q3", "Q58"), and only
// its first sentence, because a later one may name questions it leaves open ("Confirm D1, R1, R4.
// Q1–Q48, R2 and R3 are not covered by this comment and stay open.", on 2026-10-18's issue); its
// "Q<n>: <answer>" lines override, the last line per question winning across every Confirm comment
// in order. A question no Confirm comment names or overrides stays as the night left it: provisional.
const RANGE = /Q(\d+)\s*(?:to|through|–|—|-)\s*Q?(\d+)/g
const readConfirmations = (comments) => {
  const confirms = comments.filter((c) => isConfirm(c.author, c.body))
    .sort((a, b) => (a.createdAt < b.createdAt ? -1 : a.createdAt > b.createdAt ? 1 : 0))
  const covered = new Map() // q -> the URL of the latest Confirm comment that names it
  const overrides = new Map() // q -> { answer, url }
  for (const c of confirms) {
    const body = String(c.body).trimStart()
    const firstLine = body.split('\n')[0].split(/\.(?:\s|$)/)[0]
    let rest = firstLine
    for (const r of firstLine.matchAll(RANGE)) {
      for (let q = Number(r[1]); q <= Number(r[2]); q++) covered.set(q, c.url)
      rest = rest.replace(r[0], ' ')
    }
    for (const s of rest.matchAll(/Q(\d+)/g)) covered.set(Number(s[1]), c.url)
    for (const line of body.split('\n')) {
      const o = /^Q(\d+):[ \t]*(.+)$/.exec(line.trim())
      if (o) overrides.set(Number(o[1]), { answer: o[2].trim(), url: c.url })
    }
  }
  return { confirms, covered, overrides }
}
const verdictOf = (answer) => (/^yes\b/i.test(answer) ? 'yes' : /^no\b/i.test(answer) ? 'no' : (!answer || /^open\b/i.test(answer)) ? 'open' : 'answered')
// The sort Apply runs and the draft check prints (#302): each question's answer (its override, or the
// night's), whether a Confirm confirmed it, and where it goes. Without a Confirm comment (dry) only a
// "yes" goes on, to the would-do list; with one, a question no Confirm names or overrides, or answered
// open, stays open, one answered no is recorded and applies nothing, and any other opening is applied
// as written.
const sortAnswers = (questions, conf, dry) => {
  const out = { answers: {}, open: [], no: [], toClassify: [] }
  for (const q of questions) {
    const o = conf.overrides.get(q.q)
    const confirmed = !dry && (conf.covered.has(q.q) || !!o)
    const answer = o ? o.answer : q.answer
    const vd = verdictOf(answer)
    out.answers[q.q] = { answer, confirmed, verdict: vd }
    if (dry ? vd !== 'yes' : (!confirmed || vd === 'open')) { out.open.push(q.q); continue }
    if (vd === 'no') { out.no.push(q.q); continue }
    out.toClassify.push({ q: q.q, issue: q.issue, task: q.task, title: q.title, text: q.text, recommendation: q.recommendation, answer, authority: dry ? '' : (o ? o.url : conf.covered.get(q.q)) })
  }
  return out
}
// The questions a Confirm names or overrides that no questions comment yielded.
const unreadOf = (questions, conf) => {
  const readQs = new Set(questions.map((q) => q.q))
  return [...new Set([...conf.covered.keys(), ...conf.overrides.keys()])].filter((q) => !readQs.has(q)).sort((a, b) => a - b)
}
// The draft check (#302), run by the confirmFile mode and by confirm_draft.mjs from the command line
// (#491), which reads this section out of this file so both run the same code: the issue's comments,
// as {author: the login, createdAt, url, body}, and a draft Confirm, as the one Confirm comment Adam
// would post, sorted by Apply's own code. An issue that already has a Confirm comment is refused
// (halted), since a draft is then no longer the whole confirmation. Answers are cut to sixty
// characters. noOverrides are the Q<n>: lines opening "no", which apply nothing.
const first60 = (s) => String(s).slice(0, 60)
const draftCheck = (comments, draft, url) => {
  if (readConfirmations(comments).confirms.length) return { halted: 'already has a Confirm comment, so a draft is no longer the whole confirmation; nothing was sorted' }
  const read = readQuestions(comments)
  const conf = readConfirmations([{ author: ADAM, createdAt: 'draft', url, body: draft }])
  const s = sortAnswers(read.questions, conf, false)
  const qs = read.questions.map((q) => q.q)
  const overridden = [...conf.overrides.entries()].sort((a, b) => a[0] - b[0])
  return {
    total: qs.length,
    covered: qs.filter((q) => conf.covered.has(q)).length,
    coveredList: qs.filter((q) => conf.covered.has(q)),
    overrides: conf.overrides.size,
    overridden: overridden.map(([q, o]) => ({ q, answer: first60(o.answer) })),
    noOverrides: overridden.filter(([, o]) => verdictOf(o.answer) === 'no').map(([q]) => q),
    byOwnLines: qs.filter((q) => conf.overrides.has(q) && !conf.covered.has(q)).length,
    confirmed: qs.filter((q) => s.answers[q].confirmed).length,
    applied: s.toClassify.length,
    no: s.no.map((q) => ({ q, answer: first60(s.answers[q].answer) })),
    open: s.open.map((q) => ({ q, answer: first60(s.answers[q].answer), confirmed: s.answers[q].confirmed })),
    unknown: unreadOf(read.questions, conf),
    notConfirm: conf.confirms.length === 0,
    refusals: read.refusals,
  }
}
// The lists both print: covered as runs ("Q1 to Q263"), then each question by its line.
const qRuns = (qs) => {
  const runs = []
  for (const q of qs) { const last = runs[runs.length - 1]; if (last && q === last[1] + 1) last[1] = q; else runs.push([q, q]) }
  return runs.map(([a, b]) => (a === b ? `Q${a}` : `Q${a} to Q${b}`))
}
const draftLists = (r) => {
  const L = []
  const list = (title, items) => { L.push('', `### ${title}`, ''); if (items.length) items.forEach((i) => L.push(`- ${i}`)); else L.push('none') }
  list('Covered', qRuns(r.coveredList))
  list('Overridden', r.overridden.map((n) => `Q${n.q}: ${n.answer}`))
  list('Answered no', r.no.map((n) => `Q${n.q}: ${n.answer}`))
  list('Left open', r.open.map((n) => `Q${n.q}: ${n.answer}${n.confirmed ? '' : ' (not confirmed)'}`))
  list('Not found', r.unknown.map((q) => `Q${q}: no "## Questions:" comment yields it`))
  if (r.refusals.length) list('Questions not read', r.refusals)
  return L
}

// ---------------------------------------------------------------- the record, judged here
// tools/review_record.py, run by the Verify agent, decides which record clears a head; the script
// still holds the agent's report to the head it verified, so a record of an older head, however it
// is reported, never clears this one (#78 -> "Verify" step 8: a review covers the head it read).
const FULL = /^[0-9a-f]{40}$/
// Step 3's stop names its remedy (#321). On 2026-10-22 Verify stopped there with the symptoms only,
// lane tips off the branch, and the reader edited the Overnight issue's body, which step 3 never
// reads: the gate is each lane worktree's tip, judged by git. So the script, not the agent's prose,
// builds the stop from the worktrees it reports: each one whose lane the issue lists as integrated
// or partial and whose tip is off the branch, with its tip and the commit on the branch that has the
// same tree, which marks a rebuild rather than a lane that never merged. When every such tip has one,
// the stop gives tools/remap_rebuild.py's command (#318) with both tips; a missing tip withholds it
// rather than print a half-filled one (CODING_STANDARDS.md -> "A check fails closed"). A failed
// lane's tip is off the branch by design (step 3), so it neither stops the run nor withholds the
// command.
const offBranch = (v) => {
  const outcomes = new Map((v.lanes || []).map((l) => [l.task, l.outcome]))
  return (v.worktrees || []).filter((w) => w && w.ancestor === false && ['integrated', 'partial'].includes(outcomes.get(w.lane)))
}
const laneTipsRemedy = (v, off, issue, intBranch) => {
  const lines = [`The gate is the lane worktree tips, not the issue body: step 3 runs \`git -C ${REPO} merge-base --is-ancestor <tip> ${intBranch}\` on each lane worktree under ${WTROOT}, so editing the shas in Overnight #${issue} clears nothing. Off the branch:`]
  for (const w of off) {
    lines.push(FULL.test(w.partner || '')
      ? `- ${w.path} (${w.branch}) at ${w.tip}: rebuilt as ${w.partner}, the commit on ${intBranch} with the same tree`
      : `- ${w.path} (${w.branch}) at ${w.tip}: not merged; no commit on ${intBranch} has its tree, so this tip is not on the branch, as merged or as rebuilt (part of its lane's work may be: compare the tip with the lane's merge)`)
  }
  if (off.some((w) => !FULL.test(w.partner || ''))) lines.push('Not every tip has a tree-equal partner, so this is not only a rebuild and no remap clears it: the report and git disagree on each lane named not merged.')
  else if (!FULL.test(v.oldTip || '') || !FULL.test(v.head || '')) lines.push(`Every tip has a tree-equal partner, so ${intBranch} was rebuilt, but ${!FULL.test(v.oldTip || '') ? 'the old tip, the one the lanes merged into, was not found' : 'the new tip was not read'}; find it and run tools/remap_rebuild.py (#318) with both.`)
  else lines.push(`Every tip has a tree-equal partner, so ${intBranch} was rebuilt (#318). Run the remap, read its dry run, then run it again with --apply: \`cd ${REPO} && python3 -I tools/remap_rebuild.py --old ${v.oldTip} --new ${v.head} --issue ${issue} --worktree-root ${WTROOT} --session "<who rebuilt it, and why>"\``)
  return lines.join('\n')
}
const judgeVerify = (v, subject, issue, intBranch) => {
  if (!v) return { stop: 'Verify returned nothing', kind: 'stop' }
  const off = offBranch(v)
  if (v.outcome === 'stop' && v.step === 3 && off.length) return { stop: `Verify stopped at step 3: ${v.reason}\n${laneTipsRemedy(v, off, issue, intBranch)}`, kind: 'stop' }
  if (v.outcome === 'stop') return { stop: `Verify stopped at step ${v.step}: ${v.reason}`, kind: 'stop' }
  if (off.length) return { stop: `Verify stopped at step 3: it reported ok over lane tips off ${intBranch}\n${laneTipsRemedy(v, off, issue, intBranch)}`, kind: 'stop' }
  if (!FULL.test(v.head || '')) return { stop: `Verify returned no full head sha (${JSON.stringify(v.head)})`, kind: 'stop' }
  if (v.mergeSubject !== subject || ROW_FORM.test(v.mergeSubject)) return { stop: `the drafted merge subject is ${JSON.stringify(v.mergeSubject)}, not ${JSON.stringify(subject)}`, kind: 'stop' }
  if (v.ci === 'red') return { stop: `CI is red on ${v.head}${v.failingJobs.length ? `: ${v.failingJobs.join(', ')}` : ''} (${v.ciUrl || 'no run URL'})`, kind: 'ci' }
  if (v.ci !== 'green') return { stop: `CI is ${v.ci} on ${v.head} after the bounded wait (${v.ciUrl || 'no run URL'})`, kind: 'ci' }
  const r = v.record || {}
  if (r.head === v.head && r.verdict === 'hold') return { stop: `the review record of ${v.head} says hold: ${r.url}. Its findings are there; a fix is a new head that needs its own record`, kind: 'hold' }
  if (r.head === v.head && r.verdict === 'merge' && r.url) return { ok: true }
  const other = r.head && r.head !== v.head ? ` (the record reported names ${r.head}, which is not this head)` : ''
  return { stop: `no review record names ${v.head}, the pull request's head${other}`, kind: 'record' }
}

// ---------------------------------------------------------------- the description, refreshed here
// #300. On 2026-10-22 the run branch gained review-fix commits and was rebuilt to correct row 6.7's
// merge subject; the description the morning had written still named the first head, the old
// task-merge shas and the old counts, and #287's second review held on it until it was rewritten
// outside the workflow. So a re-run that finds the pull request open compares the head its
// description names with the branch's head and, when they differ, rewrites the lines it owns from
// the branch's first-parent merges: the head in the head line, the integrated and partial counts,
// and the rows of the Integrated and Partial tables, whose merge shas are replaced and which move
// between the two tables when a merge's subject did. Every other line is kept byte for byte (#300's
// acceptance), so a cell's annotation after its sha is kept too and the hand-over shows it. A
// description it cannot read in that shape is refused whole rather than half-refreshed
// (CODING_STANDARDS.md -> "A check fails closed"). A "Closes #<n>" line whose row is now partial is
// kept as well, since the rewrite does not own it, and named as a refusal: a Closes line closes its
// issue at the landing, which a partial row must not (#100).
const LANE_MERGE = /^Merge (part of )?task ([^\s:]+):/
const HEAD_IN = /\bhead `([0-9a-f]{7,40})`/
const COUNTS_IN = /\b(\d+) integrated, (\d+) partial\b/
const TABLES = ['Integrated', 'Partial']
const cellsOf = (line) => (line.startsWith('|') && line.trimEnd().endsWith('|') ? line.trimEnd().slice(1, -1).split('|').map((c) => c.trim()) : null)
const tableIn = (lines, name) => {
  const at = lines.findIndex((l) => l.trim() === `## ${name}`)
  if (at < 0) return null
  let i = at + 1
  while (i < lines.length && !lines[i].trim()) i++
  const header = cellsOf(lines[i] || '')
  if (!header || header.length !== 3) return null
  const start = Math.min(i + 2, lines.length) // past the header and its separator row
  let end = start
  while (end < lines.length && cellsOf(lines[end])) end++
  return { name, start, end, rows: lines.slice(start, end).map((line) => ({ line, cells: cellsOf(line) })) }
}
const refreshDescription = (body, head, merges, lanes) => {
  const lines = String(body).split('\n')
  const h = lines.findIndex((l) => HEAD_IN.test(l))
  if (h < 0) return { state: 'refused', reason: 'the open pull request\'s description names no head in the form head `<sha>`, so it was not compared or refreshed' }
  const named = HEAD_IN.exec(lines[h])[1]
  if (head.startsWith(named)) return { state: 'untouched', named }
  const refuse = (why) => ({ state: 'refused', named, reason: `the open pull request's description names the head ${named}, not ${head}, and was not refreshed: ${why}` })
  // Each task's last first-parent merge decides its table and its sha.
  const tasks = new Map()
  for (const m of merges || []) {
    const x = LANE_MERGE.exec(String(m.subject || ''))
    if (!x) continue
    tasks.delete(x[2])
    tasks.set(x[2], { sha: m.sha, partial: !!x[1] })
  }
  if (!tasks.size) return refuse('the branch reports no "Merge task" or "Merge part of task" merge to read rows from')
  const c = lines.findIndex((l) => COUNTS_IN.test(l))
  if (c < 0) return refuse('it carries no "<n> integrated, <m> partial" line')
  const tables = Object.fromEntries(TABLES.map((n) => [n, tableIn(lines, n)]))
  if (!tables.Integrated) return refuse('it has no "## Integrated" table with the columns Task, Issue and Task merge')
  const rowOf = new Map()
  for (const t of TABLES.map((n) => tables[n]).filter(Boolean)) {
    for (const r of t.rows) {
      if (!tasks.has(r.cells[0])) return refuse(`its ${t.name} row for ${r.cells[0]} names no task merge on the branch`)
      if (!/`[0-9a-f]{7,40}`/.test(r.cells[2])) return refuse(`its ${t.name} row for ${r.cells[0]} names no merge sha`)
      rowOf.set(r.cells[0], { table: t.name, ...r })
    }
  }
  const rewrote = []
  const out = { Integrated: [], Partial: [] }
  for (const t of TABLES) for (const r of (tables[t] || { rows: [] }).rows) out[t].push(r.cells[0])
  const rowText = new Map()
  for (const [task, m] of tasks) {
    const to = m.partial ? 'Partial' : 'Integrated'
    if (!tables[to]) return refuse(`${task}'s merge belongs in a "## ${to}" table it does not have`)
    const r = rowOf.get(task)
    if (!r) {
      const lane = (lanes || []).find((l) => l.task === task)
      if (!lane || !Number.isInteger(lane.issue)) return refuse(`${task} has a merge on the branch, no row, and no lane naming its issue`)
      const line = `| ${task} | #${lane.issue} | \`${m.sha.slice(0, 7)}\` |`
      out[to].push(task)
      rowText.set(task, line)
      rewrote.push(`added to ${to}: ${line}`)
      continue
    }
    const old = /`([0-9a-f]{7,40})`/.exec(r.cells[2])[1]
    const line = m.sha.startsWith(old) ? r.line : r.line.replace(`\`${old}\``, `\`${m.sha.slice(0, old.length)}\``)
    rowText.set(task, line)
    if (r.table !== to) {
      out[r.table] = out[r.table].filter((x) => x !== task)
      out[to].push(task)
      rewrote.push(`moved from ${r.table} to ${to}: ${r.line} became ${line}`)
    } else if (line !== r.line) rewrote.push(`${r.line} became ${line}`)
  }
  const counts = [...tasks.values()].reduce((n, m) => (m.partial ? { ...n, p: n.p + 1 } : { ...n, i: n.i + 1 }), { i: 0, p: 0 })
  const lineAt = new Map()
  lineAt.set(h, lines[h].replace(`head \`${named}\``, `head \`${head.slice(0, named.length)}\``))
  lineAt.set(c, (lineAt.get(c) || lines[c]).replace(COUNTS_IN, `${counts.i} integrated, ${counts.p} partial`))
  const result = []
  const headLines = []
  const at = Object.fromEntries(TABLES.filter((t) => tables[t]).map((t) => [tables[t].start, t]))
  for (let i = 0; i <= lines.length; i++) {
    const t = at[i]
    if (t) {
      // A table's rows, written where its old rows began; an empty table's go before the line there.
      for (const task of out[t]) result.push(rowText.get(task))
      if (tables[t].end > i) { i = tables[t].end - 1; continue }
    }
    if (i === lines.length) break
    if (lineAt.has(i) && lineAt.get(i) !== lines[i]) headLines.push(`${lines[i]} became ${lineAt.get(i)}`)
    result.push(lineAt.has(i) ? lineAt.get(i) : lines[i])
  }
  rewrote.unshift(...headLines)
  // The Closes lines the rewrite keeps but a partial row must not carry.
  const closing = [...tasks].filter(([, m]) => m.partial).map(([task]) => {
    const n = (/#(\d+)/.exec((rowOf.get(task) || { cells: ['', ''] }).cells[1]) || [])[1]
    return n && lines.some((l) => l.trim() === `Closes #${n}`) ? `the refreshed description still carries "Closes #${n}" for ${task}, whose merge on the branch is now "Merge part of task": change it to "Ticket: #${n}" before landing, or the landing closes #${n} (#100)` : null
  }).filter(Boolean)
  return { state: 'rewritten', named, body: result.join('\n'), rewrote, closing }
}

// ---------------------------------------------------------------- follow-ups and ticket edits
// #320. On 2026-10-22 Apply classed Q71 and Q174 of #286 as needing no repository change and skipped
// them, though each answer asked for an integrator follow-up ticket, and skipped Q91 and Q92 because
// their edit (two boxes off #62's body; blocked-by edges on #244) lands on a ticket outside the
// rewrite list. So an answer asking for a follow-up is filed as one, under the Overnight issue's
// rules: its body is written here, the applier derives its owner from its Touches paths, and
// tools/ticket_shape.py checks its shape before it is filed (overnight.js -> NEW TICKETS). An answer
// whose change is an edit to a ticket's body boxes or blocked-by edges is applied to that ticket; the
// part of it the answer alone cannot state (a ticket not yet filed, a condition) is filed as a
// follow-up naming the ticket, and so is an edit stated nowhere. A follow-up names its origin on a
// Raised-by line and is neither a sub-issue nor a blocker of it (docs/agents/tracker-policy.md).
const BOX_LINE = /^- \[[ x]\] \S/
const followUpTicket = (date, issue, q, f) => ({
  q: q.q, title: f.title, blockedBy: f.blockedBy, file: `${paths(date).mdir}/apply/followup-q${q.q}.md`,
  body: [
    `**Raised by:** #${q.issue}, Q${q.q} of Overnight ${date} (#${issue}), confirmed at ${q.authority || 'no Confirm comment'}`,
    `**Owner:** ${f.owner || '<role>'}`,
    `**Touches:** ${f.touches.length ? f.touches.map((p) => '`' + p + '`').join(', ') : 'none: a tracker edit, no repository path'}`,
    '',
    '## What to build',
    '',
    f.what,
    '',
    `The question, Q${q.q} on #${q.issue}: ${q.text}`,
    '',
    `Its confirmed answer: ${q.answer}`,
    '',
    '## Acceptance criteria',
    '',
    `- [ ] ${f.title}: done, or declined with the reason in this issue's Answer.`,
  ].join('\n'),
})
// What applying one classified question asks of an applier: the edits, edges and follow-ups, and the
// refusals for what it cannot be asked to do.
const planOf = (date, issue, q, c) => {
  const out = { edits: [], edges: [], followUps: [], refusals: [] }
  if (c.kind === 'follow-up') {
    const f = c.followUp
    const touches = f.touches.map((p) => String(p).trim()).filter(Boolean)
    const title = f.title.trim() || c.summary
    if (!touches.length) out.refusals.push(`Q${q.q} asks for a follow-up that names no path it writes, so no owner can be derived from it; it is not filed`)
    else out.followUps.push(followUpTicket(date, issue, q, { title, touches, blockedBy: f.blockedBy, what: f.what || c.summary }))
  }
  if (c.kind === 'ticket-edit') {
    const t = c.target
    if (!Number.isInteger(t) || t <= 0) { out.refusals.push(`Q${q.q} is an edit to a ticket the classifier did not name; it is not applied`); return out }
    const boxes = c.removeBoxes.filter((b) => BOX_LINE.test(b))
    const notBoxes = c.removeBoxes.filter((b) => !BOX_LINE.test(b))
    const edges = [...new Set(c.addBlockedBy.filter((n) => Number.isInteger(n) && n > 0 && n !== t))]
    if (boxes.length) out.edits.push({ q: q.q, target: t, removeBoxes: boxes })
    if (edges.length) out.edges.push({ q: q.q, target: t, addBlockedBy: edges })
    const rest = [c.unstated.trim(), notBoxes.length ? `remove ${notBoxes.map((b) => JSON.stringify(b)).join(', ')}, which ${notBoxes.length === 1 ? 'is' : 'are'} not a box line` : ''].filter(Boolean)
    if (!boxes.length && !edges.length && !rest.length) rest.push(c.summary)
    if (rest.length) {
      const title = boxes.length || edges.length ? `The rest of Q${q.q} of Overnight ${date} on #${t}` : `Apply Q${q.q} of Overnight ${date} to #${t}`
      out.followUps.push(followUpTicket(date, issue, q, { title, touches: [], blockedBy: [], owner: 'integrator', what: `On #${t}: ${rest.join('; ')}. The morning could not state this as an edit from the answer alone, so it is filed here for the integrator to apply to #${t}.` }))
    }
  }
  return out
}

// ---------------------------------------------------------------- applier units
// One applier per grilled ticket with work to do (#78 -> "Apply"), at most maxParallel in all
// (#78 -> "Agent budget"). Past that, every chained ticket shares one applier, since they run one at
// a time anyway, and the tracker-only tickets share the rest round-robin. The chain is the tickets
// that commit, on one branch, and those that write a ticket body another applier may write too: an
// edit of a ticket's boxes, or the rewrite of a ticket such an edit targets (#320), since two
// appliers writing one body at once would each clobber the other's. A follow-up or ticket-edit
// question's work is the followUps, edits and edges planOf made of it, so a question planOf refused
// sends its ticket no applier with nothing to do.
const TRACKER_KINDS = ['workstream-ticket', 'decision-to-record']
const unitsFor = (byTicket) => {
  const committing = (t) => t.questions.some((q) => q.kind === 'integrator-edit')
  const targets = new Set(byTicket.flatMap((t) => t.edits.map((e) => e.target)))
  const chained = (t) => committing(t) || t.edits.length > 0 || (t.rewrite && targets.has(t.issue))
  const chain = byTicket.filter(chained)
  const trackers = byTicket.filter((t) => !chain.includes(t) && (t.rewrite || t.followUps.length || t.edges.length || t.questions.some((q) => TRACKER_KINDS.includes(q.kind))))
  let cGroups = chain.map((t) => [t])
  let tGroups = trackers.map((t) => [t])
  if (cGroups.length + tGroups.length > MAX_PAR) {
    cGroups = chain.length ? [chain] : []
    const room = MAX_PAR - cGroups.length
    if (room <= 0) {
      cGroups = [[...chain, ...trackers]]
      tGroups = []
    } else if (trackers.length > room) {
      tGroups = Array.from({ length: room }, () => [])
      trackers.forEach((t, i) => tGroups[i % room].push(t))
    }
  }
  const unit = (g) => {
    const kind = g.some(committing) ? 'commit' : 'tracker'
    return { kind, key: `${kind}-${g.map((t) => t.issue).join('-')}`, tickets: g, followUps: g.flatMap((t) => t.followUps), edits: g.flatMap((t) => t.edits), edges: g.flatMap((t) => t.edges) }
  }
  return { chain: cGroups.map(unit), tracker: tGroups.map(unit) }
}

// ---------------------------------------------------------------- prompts
const replayPrompt = () => `Replay: find the night's Overnight issue, then replay the tracker writes GitHub refused overnight (#78 -> "Replay").

${HOUSE(paths(A.date || '<date>').mdir)}

${LOOKUP(`The date${A.date ? ` is ${A.date}` : ' is the one in that title'}; call it <date> below.`)}
2. The pending-writes file, checked before any replay step (#319): run \`test -e ${LOG}/pending-<date>.jsonl && echo pending; test -e ${LOG}/pending-<date>.replayed.jsonl && echo replayed\` and nothing else first. If neither exists, there is nothing to replay: pending and renamed are "", every count is zero, creates is [], stoppedAt is 0 and reason is "", and skip to step 3 at once, reading no other file and running no other command for this step. If only the replayed file exists, an earlier morning replayed it: replay nothing and skip to step 3. Otherwise the pending file holds JSON lines {"issue", "action": "comment"|"edit"|"close"|"create", "body"}, a create line carrying "title", "label" and "blocking" too: apply its lines in file order, skipping each line already listed in the progress file ${LOG}/pending-<date>.progress (one line number per applied line), and append each line's number to the progress file the moment it is applied (a create line's once its last step is done, as below):
   - a comment: write its body to ${LOG}/morning-<date>/replay-<line>.md and post it, \`cd ${REPO} && gh issue comment <issue> --body-file ${LOG}/morning-<date>/replay-<line>.md\`;
   - an edit that changes labels or assignees: \`cd ${REPO} && gh issue edit <issue> <its flags>\`;
   - an edit that replaces a body (--body-file): only if the issue's body is still the one the lane read, which the lane's saved body file in ${LOG}/ shows (\`cd ${REPO} && gh issue view <issue> --json body --jq .body\`, compared byte for byte); otherwise skip it and count it skipped, because a stale body would clobber a newer spec;
   - a close: \`cd ${REPO} && gh issue close <issue>\`;
   - a create, {"issue": 0, "action": "create", "title", "label": "change-request,ready-for-human", "blocking", "body"}: a change request a lane could not file overnight, which only the overnight run queues (#433; #421 Q2, Q3). A create line whose issue is not 0, whose label is not exactly "change-request,ready-for-human" or whose title is empty is a failed line. blocking is the ticket the request blocks, or a list of them; 0 or [] blocks nothing. Its steps run in this order, each skipped when already done, so a line run twice files one request:
     1. Search the open change-request issues for its title first: \`cd ${REPO} && gh issue list --state open --label change-request --limit 100 --search "<title> in:title" --json number,title\`. An issue there whose title equals the line's title exactly is the request, found already filed: its number is the request's number, and the line files nothing.
     2. Only when none is found, file it: write its body to ${LOG}/morning-<date>/replay-<line>.md, then \`cd ${REPO} && gh issue create --title "<title>" --label change-request,ready-for-human --body-file ${LOG}/morning-<date>/replay-<line>.md\`, adding --blocking <ticket> for each ticket it blocks. The new issue's number is the request's number.
     3. Each ticket it blocks, its edge: \`cd ${REPO} && gh issue view <ticket> --json blockedBy\`, and when the request's number is not among them, \`cd ${REPO} && gh issue edit <ticket> --add-blocked-by <number>\`.
     4. Each ticket it blocks, the request's number, because the lane's merge body names the request only by its title and cannot be edited: \`cd ${REPO} && gh issue view <ticket> --json comments --jq '.comments[].body'\`, and unless a comment there already names #<number>, write "Change request #<number>, <title>, is the request this ticket's Outstanding line names by title; the morning of <date> replayed it from line <line> of the pending-writes file." to ${LOG}/morning-<date>/replay-<line>-<ticket>.md and post it, \`cd ${REPO} && gh issue comment <ticket> --body-file ${LOG}/morning-<date>/replay-<line>-<ticket>.md\`.
     A create line goes to the progress file only once its last step is done, never at the create, so a morning that crashed midway runs the line again and step 1 finds the request it filed. It counts as applied whether step 2 filed the request or step 1 found it. Return each create line you ran in creates as {line, title, number: the request's number (0 when it was neither found nor filed), found: step 1 found it, filed: step 2 filed it, tickets: one {number, edge: "added"|"present"|"none", comment: "posted"|"present"|"none"} per ticket it blocks}.
   The first line that fails stops the replay: nothing after it is applied, stoppedAt is its line number and reason the error. When every line is applied, rename the file, \`mv ${LOG}/pending-<date>.jsonl ${LOG}/pending-<date>.replayed.jsonl\`, and never delete it. pending is the pending file's path or "", renamed the new path or "".
3. The issue: \`cd ${REPO} && gh issue view <number> --json number,title,url,state\`. issue carries its number, title, url and state. Return none of its body or comments: the Apply phase reads the comments itself (#319).
4. The morning's durable state: every JSON file under ${LOG}/morning-<date>/ (apply/ included), each as {name: its path under that directory without .json, inputs, result} from its contents; an empty list when there is none.
Return listing, issue, phaseFiles, counts per action (applied, skipped, failed), creates, pending, renamed, stoppedAt (the number of the line that failed, 0 when no line failed) and reason (that line's error, "" when no line failed).`

const verifyPrompt = (date, issue, phaseKey) => {
  const p = paths(date)
  return `Verify: check the overnight run of ${date} the way its report writer would, push it, open its one pull request, and read CI and the review record on its exact head (#78 -> "Verify"). You review nothing and fix nothing: a finding that needs a fix is a stop.

${HOUSE(p.mdir)}

PHASE FILE: ${p.mdir}/verify.json INPUTS: ${phaseKey}
OUTPUT FILE: ${p.mdir}/pr-body.md
OUTPUT FILE: ${p.mdir}/merge-body.md
Write {"inputs": "${phaseKey}", "result": <your return value>} to the PHASE FILE as JSON before you return.
MERGE SUBJECT: ${JSON.stringify(subjectFor(date))}

Read the Overnight issue first: \`cd ${REPO} && gh issue view ${issue} --json body,comments\`. It names the base, the integration branch ${p.intBranch} and its worktree ${p.intWt}, and the integrated, partial and failed rows; the lane merge bodies on the branch carry the rest. Each step below reads durable state first and skips what is already done. Each step is a stop: on the first one that fails, set outcome "stop", step its number and reason what failed, push nothing more, and return.
1. The main checkout's tracked files are unchanged: \`git -C ${REPO} status --porcelain --untracked-files=no\` prints nothing. Untracked files are listed in findings against what the run recorded at its start, never a stop.
2. \`git -C ${REPO} fetch origin\`. The integration branch exists and the base the issue names, resolved to its full sha, is an ancestor of it: \`git -C ${REPO} merge-base --is-ancestor <base> ${p.intBranch}\`. mainMoved is whether origin/main has moved past the base; that is reported, never merged in or rebased onto. forkPoint is the integration branch's merge base with origin/main, the commit the landing merges onto, as a full sha: \`git -C ${REPO} merge-base origin/main ${p.intBranch}\`. It is the base unless main was merged into the branch (#326). Return it even on a stop at this step; "" only when the branch does not exist.
3. Every worktree under ${WTROOT} other than ${p.intWt} and ${p.morningWt}: \`git -C <path> status --porcelain\` prints nothing (a dirty one is a stop naming the worktree and its files), and its tip is an ancestor of the integration tip, \`git -C ${REPO} merge-base --is-ancestor <tip> ${p.intBranch}\`. A tip that is not is a stop when the issue lists its lane as integrated or partial, because the report and git disagree; for a failed lane it is expected, and the worktree is kept and listed. Worktrees left from earlier runs are findings. Read every worktree before you stop, and return each as {path, branch, tip: its full sha, clean, ancestor, lane, partner}: lane is the task id of the issue's row whose branch it holds, "" for a worktree no row names; partner, for a tip that is not an ancestor, is the commit on the integration branch with the same tree, which marks a rebuild rather than a lane that never merged: compare \`git -C ${REPO} rev-parse <tip>^{tree}\` with \`git -C ${REPO} log --format=%H%x09%T%x09%P <base>..${p.intBranch}\` and take the matching commit with one parent over a merge, whose tree a --no-ff merge of a lane forked from the branch's own tip shares (tools/remap_rebuild.py); "" when none matches or the tip is an ancestor. oldTip, when any integrated or partial lane's tip is not an ancestor, is the integration tip those lanes were merged into: the tip the issue's Run: paragraph names, resolved to its full sha, when every such lane tip is an ancestor of it, else the newest entry of \`git -C ${REPO} reflog --format=%H refs/heads/${p.intBranch}\` that every such lane tip is an ancestor of; "" when neither is, or when every lane tip is an ancestor. head is the integration tip, \`git -C ${REPO} rev-parse ${p.intBranch}\`, and lanes are the issue's rows, both returned even on a stop here. The script builds this stop's remedy from those fields (#321): your reason states the symptoms only.
4. The check, as the hook runs it, from the base to the head, judging each commit of a history with merges: \`cd ${p.intWt} && tools/check.sh --base <base>\`. Its last line is its verdict; anything but "check: pass:" is a stop with the output's last lines.
5. The push, with the hook running: compare \`git -C ${REPO} ls-remote origin refs/heads/${p.intBranch}\` with the local tip as the house rules say, then \`git -C ${p.intWt} push -u origin ${p.intBranch}\`. pushed is whether the remote now holds the local tip.
6. One pull request from ${p.intBranch} to main: \`cd ${REPO} && gh pr list --head ${p.intBranch} --state open --json number,url,headRefOid\` reuses an open one. merges is the branch's first-parent merges from the base, oldest first, each {sha: the full sha, subject}: \`git -C ${REPO} log --first-parent --merges --reverse --format=%H%x09%s <base>..${p.intBranch}\`. prReused is whether you reused an open pull request rather than opening one. When you reuse one, prBody is its description exactly as served, \`cd ${REPO} && gh pr view <number> --json body --jq .body\`, and you never edit it: the script compares the head it names with this head and refreshes it (#300). Otherwise prBody is "". Draft its body to ${p.mdir}/pr-body.md from the issue and the lane merge bodies, in the shape a later morning's refresh reads: a line "Overnight issue: #${issue}. Base <base>, head <head>. <n> integrated, <m> partial, <k> failed." with both shas in full and each in backticks; then tables headed "## Integrated", "## Partial" and "## Failed", each with the header "| Task | Issue | Task merge |" and one row per lane, "| <task id> | #<n> | <the merge's short sha, in backticks> |" for an integrated or partial lane and "| <task id> | #<n> | none; <what is kept, such as its branch> |" for a failed one, even a table with no rows; one line "Closes #<n>" for each integrated row whose lane reported complete and one line "Ticket: #<n>" for each partial row (#100); the aggregated "Outstanding:" and "Outstanding test ids:" lines; the evidence line (how many gate logs, the last integration gate's head and its test count); and the merge subject above. Draft the merge body to ${p.mdir}/merge-body.md: what was verified and how, citing each task merge by sha, with the same Outstanding lines and no Closes line. Open the pull request when none is open: \`cd ${REPO} && gh pr create --base main --head ${p.intBranch} --title ${JSON.stringify(subjectFor(date))} --body-file ${p.mdir}/pr-body.md\`. mergeSubject is the merge subject above, verbatim.
7. CI on the exact head: the pull request's headRefOid must equal the local tip, else stop. Wait for its required checks in one Bash call with a 600000 ms timeout, \`cd ${REPO} && gh pr checks <number> --watch --interval 30\`, then read \`cd ${REPO} && gh pr checks <number> --json name,state,bucket,link\`. A skipped job counts as passing. ci is green, red (failingJobs naming each failing job), pending (the wait ran out) or none (no checks reported); ciUrl is a run's link on that head.
8. The review record naming the head. Read the pull request's records exactly as tools/land.sh does: \`cd ${REPO} && comments=$(gh api --paginate --slurp repos/{owner}/{repo}/issues/<number>/comments) && reviews=$(gh api --paginate --slurp repos/{owner}/{repo}/pulls/<number>/reviews) && printf '{"comments": %s, "reviews": %s}\\n' "$comments" "$reviews" | ${READER} <head>\`. Exit 0: record {verdict "merge", head: the head you passed, url: the URL it printed}. Exit 1 with "says hold: <url>": record {verdict "hold", head, url}. Exit 1 otherwise: record {verdict "none", head "", url ""}. Exit 2: a stop with its message. reader is the exit status and its first line. A hand-written record in the fixed form counts; nothing else does, and you never write one.
Return every field: lanes from the issue's rows (issue, task, outcome, complete, mergeSha, outstanding), claimed the claimed branches the issue names, worktrees as step 3 read them, findings as plain lines. Fields a stop left unread are empty.`
}

// Its agent writes nothing anywhere and keeps no phase file, since a rerun must read the comments as
// they are then, so it carries these rules rather than the house rules (as the draft check's does).
// The head of each comment's body the index carries: enough to run the readers' predicates on.
const HEAD_CHARS = 200
const COMMENTS_RULES = `Rules (a comments read; you write nothing anywhere):
- Write no file, anywhere, and post, edit, file or close nothing on GitHub: use only \`gh issue view\`, with \`cd ${REPO} &&\` in front so it resolves this repository.
- Never commit, checkout, switch, stash, reset or build in ${REPO}, and make no worktree.`
const commentsPrompt = (issue) => `Comments: index the Overnight issue's comments for Apply, with no body (#319). You read; the script picks the comments it needs and has them read in batches.

${COMMENTS_RULES}
- Never quote a secret, token, cookie, code or state value in anything you return.

1. \`cd ${REPO} && gh issue view ${issue} --json number,comments --jq '{number, comments: [.comments[] | {author: .author.login, createdAt, url, bytes: (.body|utf8bytelength), head: .body[0:${HEAD_CHARS}]}]}'\`. issue is its output as printed: its number, and every comment, in order, as {author, createdAt, url, bytes, head}. head is the first ${HEAD_CHARS} characters of the comment, whatever they are; return nothing more of any comment.
2. If GitHub cannot be reached or the read fails, error says why and comments is []. error is "" when nothing stopped you.
Return issue and error.`
const batchPrompt = (issue, at) => `Comments: return ${at.length === 1 ? 'one comment' : `${at.length} comments`} of the Overnight issue verbatim for Apply (#319). You read; the script reads the questions and the Confirm comments from them.

${COMMENTS_RULES}
- Never quote a secret, token, cookie, code or state value in anything you return but the comments, verbatim.

1. \`cd ${REPO} && gh issue view ${issue} --json comments --jq '[.comments[${at.join(',')}] | {author: .author.login, createdAt, url, body}]'\`. comments is its output: ${at.length} comment(s), in that order, each as {author, createdAt, url, body}, each body verbatim, byte for byte.
2. If GitHub cannot be reached or the read fails, error says why and comments is []. error is "" when nothing stopped you.
Return comments and error.`
// The bodies a batch reader carries back are kept to this many bytes, so no one return is large:
// #659's comments were about 75 KB, its Confirm comment 18 KB. A comment larger than this is a batch alone.
// This is a precaution, not a known limit: what stopped the one reader on 2026-10-31 was a safety
// classifier (see the header), and nothing on #659 shows size is what trips it.
const BATCH_BYTES = 24000
// The comments Apply reads (readQuestions, readConfirmations), picked from the index in order by
// the readers' own predicates (headingOf, isConfirm) run on each comment's head, and packed into
// batches of at most BATCH_BYTES. A head the readers' pattern cannot be judged on is not skipped: the
// comment is read whole and the readers decide, so a comment they would read is never dropped unread.
// Two heads cannot be judged. One is full length with less than MIN_VISIBLE characters of it after
// its leading whitespace: the cut may have fallen before the pattern could show. The other is full
// length, its first line cut (no newline in it), and what is visible is only the start of a heading,
// short of its second comma (a long padded or long second field): HEADING needs the whole line to
// match, so a head cut there says nothing either way.
const MIN_VISIBLE = 40
const HEADING_CUT = /^## Questions: #\d*(?:, [^,]*)?$/
const undecidableHead = (head) => head.length >= HEAD_CHARS &&
  (head.trim().length < MIN_VISIBLE || (!head.includes('\n') && HEADING_CUT.test(head.trim())))
const batchesOf = (indexed) => {
  const out = []
  let cur = null
  indexed.forEach((c, i) => {
    const head = String(c.head || '')
    if (!undecidableHead(head) && !headingOf(head) && !isConfirm(c.author, head)) return
    if (!cur || cur.bytes + (c.bytes || 0) > BATCH_BYTES) { cur = { at: [], urls: [], bytes: 0 }; out.push(cur) }
    cur.at.push(i); cur.urls.push(c.url); cur.bytes += c.bytes || 0
  })
  return out
}

const classifyPrompt = (date, issue, questions, dry, phaseKey) => {
  const p = paths(date)
  return `Classify the ${dry ? 'provisional (pre-accepted, not yet confirmed)' : 'confirmed'} answers of the overnight run of ${date} (Overnight #${issue}) for Apply (#78 -> "Apply"). You write nothing but your phase file: you classify, and the appliers act.

${HOUSE(p.mdir)}

PHASE FILE: ${p.mdir}/classify.json INPUTS: ${phaseKey}
Write {"inputs": "${phaseKey}", "result": <your return value>} to the PHASE FILE as JSON before you return.
QUESTIONS: ${JSON.stringify(questions)}

For each question above, read its ticket's "## Questions for Adam" and "## Grilling record" comments, its labels, and its "## Proposed glossary / ADR" comment only for its ADR candidates, which are decision-to-record: a glossary or steering proposal there is never an answer, since proposals are filed as change requests (#421 Q9) (\`cd ${REPO} && gh issue view <issue> --json title,body,labels,comments\`), and assign it exactly one kind:
- no-repo-change: the answer needs nothing from the morning: the grilling already records the decision, and applying it is the ticket's own implementation (code, a test, a script, anything that needs a build or a run). An answer that asks for a follow-up or another ticket to be filed is never this kind, even when that ticket's work is code: it is follow-up. Nor is one whose change is to a ticket's boxes or blocked-by edges: it is ticket-edit. summary says why nothing is needed.
- follow-up: the answer asks for a ticket to be filed, an integrator or workstream follow-up, rather than for an edit the morning makes itself (#320). followUp carries title (what to build, as an issue title), touches (the repository paths its work writes, from which its owner is derived; at least one), blockedBy (the open issues it waits for, as numbers) and what (what to build, in a sentence or two, from the answer and its recommendation).
- ticket-edit: the answer's change is to a ticket's body boxes or blocked-by edges, this ticket's or another's, short of a rewrite (#320). target is that ticket's number; removeBoxes the box lines to take off its body, each copied whole and exactly from its current body (\`cd ${REPO} && gh issue view <target> --json body\`); addBlockedBy the issue numbers to add as its blockers; unstated whatever part of the change the answer alone does not state as those (a ticket not filed yet, a condition still open, a box reworded rather than removed), "" when none. A change you cannot state at all goes whole in unstated: the morning files it as a follow-up naming the target.
- integrator-edit: an edit to a document a session steers by, on an integrator path, such as a glossary term or a sentence in an agent guide, that a Confirmed answer to a question in a "## Questions for Adam" comment decides, on a ticket not labelled change-request (#78 grill Q8; #421 Q9). A question on a ticket labelled change-request is never this kind, whatever it asks: a Confirm on the Overnight issue never authorises a change request's edit, which only Adam's Decision: comment on the request does (#421 Q5), so it is no-repo-change, its summary saying so. Never a contract sentence or an ADR, its status or its index row: those are decision-to-record.
- workstream-ticket: an edit inside a workstream's paths (OWNERSHIP.md), which becomes a ticket rather than an edit.
- decision-to-record: an ADR candidate, drafted as a comment and never as a file.
Every question carries target, removeBoxes, addBlockedBy, unstated and followUp; give 0, [], [], "" and {"title": "", "touches": [], "blockedBy": [], "what": ""} where its kind uses none of them.
summary says in one line what applying it changes. rewrites lists each ticket whose answers say its body is rewritten before an agent takes it (#78 grill Q3), with the questions that say so; a spec (GLOSSARY.md -> Spec) is never rewritten by the morning (Q13). Return the questions in the order given, each with q, issue, task and answer as given.`
}

const applyPrompt = (date, issue, unit, phaseKey) => {
  const p = paths(date)
  const commits = unit.kind === 'commit'
  return `Apply the confirmed answers of the overnight run of ${date} (Overnight #${issue}) for ${unit.tickets.map((t) => '#' + t.issue).join(', ')} (#78 -> "Apply"). Every answer below was confirmed by the Confirm comment named as its authority; act on nothing else.

${HOUSE(p.mdir)}

PHASE FILE: ${p.mdir}/apply/${unit.key}.json INPUTS: ${phaseKey}
Write {"inputs": "${phaseKey}", "result": <your return value>} to the PHASE FILE as JSON before you return.
UNIT: ${JSON.stringify(unit)}

Skip what an earlier morning already did, and list it in skipped with why: a commit on ${p.morningBranch} whose body cites #${issue} and the question (\`git -C ${REPO} log origin/main..${p.morningBranch} --format=%B\`), an issue whose body cites them (\`cd ${REPO} && gh issue list --state all --search "#${issue} Q<n> in:body" --json number,url,title\`), a candidate or a preserved body already among the ticket's comments, a box already gone from its target's body, an edge already among its target's blockers.
${commits ? `Integrator edits, one commit each, on ${p.morningBranch} in the morning worktree ${p.morningWt}, forked from origin/main: reuse it when it exists, else \`git -C ${REPO} fetch origin && git -C ${REPO} worktree add -b ${p.morningBranch} ${p.morningWt} origin/main\` (when the branch exists without the worktree, \`git -C ${REPO} worktree add ${p.morningWt} ${p.morningBranch}\`). Each commit's subject says what changed; its body cites the Overnight issue as #${issue}, the question as Q<n> and its answer line verbatim; its last paragraph carries the attribution line and, on a line of its own, "Authority: <the question's authority URL>", because every such edit is to a steering document (docs/agents/landing-work.md). Commit with \`git -C ${p.morningWt} commit -F <a message file under ${p.mdir}/>\`. When the branch has commits, push it with the hook running, as the house rules say: \`git -C ${p.morningWt} push -u origin ${p.morningBranch}\`. A hook failure leaves it local: pushed.ok false and reason the hook's last lines.
` : `You make no commit. pushed is {branch: "", tip: "", ok: true, reason: "no commits"}.
`}Workstream tickets: file each as \`cd ${REPO} && gh issue create --title "<what to build>" --label needs-triage --parent ${PARENT_ISSUE} --body-file <a file under ${p.mdir}/>\` (#78 grill Q9: the night's Route phase writes the brief), its body opening with the owner line (**Owner:** and the OWNERSHIP.md role) and the plan row where there is one, citing #${issue} and Q<n>, and add the blocked-by edges the recommendation names with \`cd ${REPO} && gh issue edit <new> --add-blocked-by <n>\`.
Decisions to record: draft each ADR candidate as a comment on its ticket, \`cd ${REPO} && gh issue comment <ticket> --body-file <a file under ${p.mdir}/>\`: the decision, the rejected alternatives, and any sentence that contradicts an existing ADR flagged as docs/agents/domain.md asks. Never an ADR file.
Rewrites (a ticket with "rewrite": true): first preserve the current body verbatim in a comment headed "## Body before ${date}" (\`cd ${REPO} && gh issue comment <ticket> --body-file <file>\`), then rewrite it as current decision, what to build and acceptance, naming every confirmed question number, \`cd ${REPO} && gh issue edit <ticket> --body-file <file>\`. A spec's body is never rewritten (#78 grill Q13): list that one in skipped.
FOLLOW-UP TICKETS: the unit's followUps, each with its q, title, blockedBy, file and body, written by the script (#320). For each, in order:
1. Write its body to its file, exactly, but for an Owner line reading <role>: replace <role> with the one OWNERSHIP.md role its Touches paths derive (the last matching line wins, as git's rule). Paths that derive more than one role are filed by nobody: list it in skipped with the roles.
2. Check its shape as the overnight run checks a new ticket, under the label and blockers it will be filed with: \`cd ${REPO} && python3 -I -c 'import json,sys; print(json.dumps({"number": 0, "body": open(sys.argv[1]).read(), "labels": [{"name": sys.argv[2]}], "subIssuesSummary": {"total": 0}, "blockedBy": {"nodes": [{"number": int(n), "state": "OPEN"} for n in sys.argv[3:]]}}))' <its file> needs-triage <each blockedBy number> | python3 -I tools/ticket_shape.py -\` (bare numbers; with none, nothing after the label).
3. Only when that exits 0, its last line starting "ticket-shape: pass:", file it: \`cd ${REPO} && gh issue create --title "<its title>" --label needs-triage --body-file <its file>\`, adding --blocked-by <n> for each blockedBy number. It is neither a sub-issue nor a blocker of the ticket that raised it (docs/agents/tracker-policy.md): no --parent. On exit 1 file nothing and list it in skipped with each "ticket-shape: <rule>:" line; on any other exit file nothing and say in skipped that the check did not judge it, quoting its last line. Never reword a body to get past the check: a failure is reported, not repaired. List each one filed in issues with its number, url, title and questions [its q].
Ticket edits: the unit's edits, each {q, target, removeBoxes} (#320). Read the target, \`cd ${REPO} && gh issue view <target> --json body,subIssuesSummary\`. A spec, an issue with sub-issues, is never edited (#78 grill Q13): list it in skipped. Each line of removeBoxes must be a whole line of the body, byte for byte; when one is not, edit nothing for that q and list it in skipped with the line not found. Otherwise, as a rewrite does, first preserve the current body verbatim in a comment headed "## Body before ${date}" whose next line names Q<n> of #${issue} (\`cd ${REPO} && gh issue comment <target> --body-file <file>\`), then write the body with exactly those lines removed and no other byte changed, \`cd ${REPO} && gh issue edit <target> --body-file <file>\`. List each in edits {issue: the target, preserved: that comment's URL, removed: the lines removed, questions: [its q]}. The unit's edits run in order, each reading the body the last one left.
Ticket edges: the unit's edges, each {q, target, addBlockedBy}. Read the target's blockers, \`cd ${REPO} && gh issue view <target> --json blockedBy\`, and add each one missing, \`cd ${REPO} && gh issue edit <target> --add-blocked-by <n>\`, one call per number. List each in edges {issue: the target, blockedBy: the numbers it now carries from addBlockedBy, questions: [its q]}.
A no-repo-change question needs nothing. If GitHub cannot be reached, stop writing, set unreachable true and return what was made. refused is "" unless you refused the whole unit, with why.`
}

const followupsPrompt = (date) => {
  const p = paths(date)
  return `Follow-ups: read every open pull request into main with its review records, and the follow-ups already filed (#292). Read and report; you file nothing, and the script decides what is filed.

${HOUSE(p.mdir)}

OUTPUT FILE: ${p.mdir}/followups-read.txt
1. \`cd ${REPO} && gh pr list --state open --base main --limit 500 --json number\`.
2. For each pull request it lists: comments = \`cd ${REPO} && gh api --paginate --slurp repos/{owner}/{repo}/issues/<number>/comments\`, flattened, each entry's body, author_association, html_url and created_at exactly as served; reviews = the same for \`cd ${REPO} && gh api --paginate --slurp repos/{owner}/{repo}/pulls/<number>/reviews\`, with body, state, author_association, html_url and submitted_at. Copy bodies byte for byte: the script reads the records and their follow-ups tables from them.
3. filed: for each pull request, \`cd ${REPO} && gh issue list --state all --limit 500 --search "Raised by #<number> in:body" --json number,title,body,url\`, every issue of every listing once, each body verbatim.
4. applyTickets, the open Apply tickets of every night (#481): \`cd ${REPO} && gh issue list --state open --limit 500 --json number,title,body,url,state,author --jq '[.[] | select(.title | test("^(Apply|The rest of) Q[0-9]+ of Overnight "))]'\`, each issue as served, its body verbatim and its author as {login}. It lists without --search, whose index can miss an issue filed minutes ago.
5. ownership: OWNERSHIP.md as the main checkout holds it, \`cat ${REPO}/OWNERSHIP.md\`, verbatim (#479): the script derives each follow-up's owner from it, and tools/ticket_shape.py checks the result against the same file. "" when it cannot be read, which files nothing.
6. If GitHub cannot be reached, error says so and prs, filed and applyTickets are []. error is "" when nothing stopped you.
Write the whole result as JSON to the OUTPUT FILE, then return it. It is named .txt so that a later morning's Replay, which reads every .json file there as a phase file, passes over it.`
}

const fileFollowUpsPrompt = (date, items) => {
  const p = paths(date)
  return `Follow-ups: file each carried review finding below as an issue of its own (#292). The script chose them: each is a row of the follow-ups table of the latest review record on its pull request, and no issue it was shown holds it.

${HOUSE(p.mdir)}

FOLLOW-UPS: ${JSON.stringify(items)}
${items.map((f) => `OUTPUT FILE: ${f.file}`).join('\n')}

For each follow-up, in order:
1. Write its body, exactly, to its file.
2. Look again before filing, since a rerun or a person may have filed it since the read: \`cd ${REPO} && gh issue list --state all --limit 100 --search "<its title> in:title" --json number,title,body,url\`. An issue whose title is the follow-up's title and whose body's Raised-by line names #<its pr> holds it: file nothing, and list it in skipped.
3. Check its shape as the overnight run checks a new ticket, under the label it will be filed with (#479): \`cd ${REPO} && python3 -I -c 'import json,sys; print(json.dumps({"number": 0, "body": open(sys.argv[1]).read(), "labels": [{"name": sys.argv[2]}], "subIssuesSummary": {"total": 0}, "blockedBy": {"nodes": []}}))' <its file> <its label> | python3 -I tools/ticket_shape.py -\`. Only when that exits 0 with its last line starting "ticket-shape: pass:" go on to step 4. On exit 1 file nothing, and list it in refused with its pr, title and why, each "ticket-shape: <rule>:" line it printed joined by "; "; on any other exit file nothing, and list it in refused with why "the check did not judge it: " and its last line. Never reword a body to get past the check: a failure is reported, not repaired.
4. File it: \`cd ${REPO} && gh issue create --title "<its title>" --label <its label> --body-file <its file>\`, with its label exactly as given (needs-triage, or ready-for-human for one whose place names no path or two owners' paths). A follow-up is filed as neither a sub-issue nor a blocker of anything (docs/agents/tracker-policy.md -> "An issue is held open by its own scope"): no parent, and no blocked-by edge either way. List it in filed with its pr, title, the new number and URL.
If GitHub cannot be reached, stop filing: unreachable true, reason the error, and return what was filed. reason is "" when nothing stopped you; refused is [] when the check refused none.`
}

const handoverPrompt = (date, issue, text) => {
  const p = paths(date)
  return `Hand-over: post the morning's hand-over on the Overnight issue (#78 -> "Hand-over"). The text between the markers is the hand-over, written by the script; post it exactly, adding and removing nothing.

${HOUSE(p.mdir)}

OUTPUT FILE: ${p.mdir}/handover.md
1. Write the text between the markers, without the markers, to ${p.mdir}/handover.md.
2. Read the issue's comments, \`cd ${REPO} && gh issue view ${issue} --json comments\`. If a comment headed "## Morning ${date}" already has exactly this text, post nothing: url is that comment's URL and posted false.
3. Otherwise post it, \`cd ${REPO} && gh issue comment ${issue} --body-file ${p.mdir}/handover.md\`; url is the new comment's URL and posted true.
reason is "" unless the post failed, and then the error.
BEGIN HAND-OVER
${text}
END HAND-OVER`
}

const refreshPrompt = (date, pr, head, body) => {
  const p = paths(date)
  return `Refresh: replace the description of pull request #${pr} with the one the script refreshed to the branch's head ${head} (#300). The text between the markers is the new description; write it exactly, adding and removing nothing.

${HOUSE(p.mdir)}

OUTPUT FILE: ${p.mdir}/pr-body-refreshed.md
1. \`cd ${REPO} && gh pr view ${pr} --json state,headRefOid\`. Unless state is OPEN and headRefOid is ${head}, stop: edited false, reason says which, and edit nothing.
2. Write the text between the markers, without the markers, to ${p.mdir}/pr-body-refreshed.md.
3. \`cd ${REPO} && gh pr edit ${pr} --body-file ${p.mdir}/pr-body-refreshed.md\`. edited is true when it succeeded.
reason is "" unless you stopped or the edit failed, and then why.
BEGIN DESCRIPTION
${body}
END DESCRIPTION`
}

const afterPrompt = () => `After: the post-landing mode (#78 grill Q10). It closes, unassigns and removes worktrees only once origin/main contains the integration tip, and otherwise does nothing.

${HOUSE(paths(A.date || '<date>').mdir)}

${LOOKUP(`The date${A.date ? ` is ${A.date}` : ' is the one in that title'}; call it <date> below. issue is {number, title, url} of that issue.`)}
2. The integration tip: the head of the pull request from overnight/<date>, \`cd ${REPO} && gh pr list --head overnight/<date> --state all --json number,headRefOid,state,mergeCommit\`, which must equal the head in ${LOG}/morning-<date>/verify.json when that file exists. \`git -C ${REPO} fetch origin\`; originMain is \`git -C ${REPO} rev-parse origin/main\`; contained is whether \`git -C ${REPO} merge-base --is-ancestor <tip> origin/main\` exits 0. If it is not contained, stop here: reason says so with both shas, and close, unassign and remove nothing.
3. Read the Overnight issue (\`cd ${REPO} && gh issue view <number> --json body,comments\`) and its last "## Morning <date>" comment. Close each landed row's issue that is still open, with a comment citing the merge sha: \`cd ${REPO} && gh issue close <n> --comment "Landed in <merge sha> on main."\`. Unassign each partial row's issue: \`cd ${REPO} && gh issue edit <n> --remove-assignee @me\`.
4. Each lane worktree under ${WTROOT}, then the integration worktree overnight last: remove it only when it is clean and \`git -C ${REPO} merge-base --is-ancestor <its tip> origin/main\` exits 0, with \`git -C ${REPO} worktree remove <path>\`; every other is kept and listed in kept with why. Then \`git -C ${REPO} worktree prune\`.
Return listing, issue, tip, originMain, contained, closed and unassigned (issue numbers), removed and kept (paths), pruned and reason.`

// ---------------------------------------------------------------- the hand-over's text
// Written here so that every item #78 -> "Hand-over" lists is always present and every refusal
// reaches it with its reason. It carries no backtick span, because a span is what a prompt asks an
// agent to run, and the landing command is Adam's to run, never an agent's.
const handoverText = (s) => {
  const p = paths(s.date)
  const v = s.verify
  const L = []
  const list = (title, items, none = 'none') => {
    L.push('', `### ${title}`, '')
    if (items.length) items.forEach((i) => L.push(`- ${i}`)); else L.push(none)
  }
  L.push(`## Morning ${s.date}`, '')
  // The fork point, not the night's base, is what the landing merges onto: on 2026-10-23 main was
  // merged into the branch, and the hand-over named the night's start as its base (#326).
  const same = v && v.forkPoint && v.forkPoint === v.base
  const fork = !v ? ''
    : `, fork point ${v.forkPoint ? `${v.forkPoint} (its merge base with origin/main${same ? ', the night\'s base' : ''})` : 'not read'}${v.base && !same ? `; the night's base ${v.base}` : ''}`
  L.push(`Overnight #${s.issue}; integration branch ${p.intBranch}${v && v.head ? ` at ${v.head}` : ''}${fork}. Apply: ${s.apply.ran ? (s.apply.dry ? 'dry, no Confirm comment' : 'applied') : 'not run'}.`)
  list('Refusals', s.refusals)
  const prN = v && v.pr && v.pr.number
  const land = []
  if (prN) {
    land.push(`Pull request #${prN}: ${v.pr.url}`)
    land.push(v.record && v.record.verdict !== 'none' && v.record.head === v.head
      ? `Review record (${v.record.verdict}): ${v.record.url}`
      : `Review record: none names ${v.head || 'the head'}. Review-queue command for #${prN}: the Workflow tool with scriptPath ${REPO}/.claude/workflows/review-queue.js and args {"repo": "${REPO}", "date": "${s.date}", "wt": "${WTROOT}-review-queue", "prs": [${prN}]}; then relaunch the morning.`)
    land.push(`CI on ${v.head || 'the head'}: ${v.ci}${v.ciUrl ? ` (${v.ciUrl})` : ''}`)
    land.push(`Landing command, yours to run once a record names this head with merge and CI is green: git -C ${REPO} worktree add --detach ${p.landWt} origin/main, then cd ${p.landWt} && tools/land.sh ${prN} --subject "${subjectFor(s.date)}" --body-file ${v.mergeBodyFile || p.mdir + '/merge-body.md'}`)
  }
  list('Landing', land, 'no pull request: Verify stopped before step 6')
  const rf = s.refresh || { state: 'none' }
  const described = rf.state === 'rewritten'
    ? [`#${prN}'s description named ${rf.named}, not ${v.head}; ${rf.edited ? 'rewritten' : `not rewritten (${rf.reason || 'the writer returned nothing'})`}. The lines ${rf.edited ? 'it rewrote' : 'it would have rewritten'}, everything else kept:`, ...rf.rewrote]
    : rf.state === 'untouched' ? [`#${prN}'s description names this head (${rf.named}); untouched`]
      : rf.state === 'refused' ? [rf.reason]
        : []
  list('Pull request description', described, prN ? 'drafted by this run, or no open pull request was read' : 'none')
  const lanes = (v && v.lanes) || [] // empty-ok: a null Verify is named in Refusals by judgeVerify; these lists are only reported
  list('Issues to close', lanes.filter((l) => l.outcome === 'integrated' && l.complete).map((l) => `#${l.issue} (${l.task}, merge ${l.mergeSha}): its Closes line closes it at the landing; after: true closes it if still open`))
  list('Issues to unassign, once merged', lanes.filter((l) => l.outcome === 'partial').map((l) => `#${l.issue} (${l.task}): Outstanding: ${l.outstanding || 'see its Answer'}`))
  list('Worktrees to remove after the landing, then git worktree prune', ((v && v.worktrees) || []).map((w) => `${w.path} (${w.branch} at ${w.tip}: ${w.clean ? 'clean' : 'dirty'}, ${w.ancestor ? 'on the integration branch' : 'not on the integration branch'})`)) // empty-ok: a null Verify is named in Refusals by judgeVerify; these lists are only reported
  list('Claimed branches needing their own pull requests', (v && v.claimed) || []) // empty-ok: a null Verify is named in Refusals by judgeVerify; these lists are only reported
  list('Failed lanes\' branches, kept', lanes.filter((l) => l.outcome === 'failed').map((l) => `#${l.issue} (${l.task})`))
  const mb = s.apply.morning
  list('Morning branch', mb.commits ? [`${p.morningBranch}: ${mb.commits} commit(s), ${mb.pushed ? 'pushed' : `not pushed (${mb.reason})`}. Its pull request is yours to open: gh pr create --base main --head ${p.morningBranch}`] : [], 'no commits')
  list('Applied', s.apply.done, 'nothing')
  // #481: each open Apply ticket, from any night, a step of its own for the session that holds the context.
  const at = s.applyTickets || { unread: 'not passed to the hand-over', open: [] }
  list('Open Apply tickets, oldest first: apply each, then close it', [
    ...(at.unread ? [`earlier nights' Apply tickets not read: ${at.unread}; only this morning's are listed`] : []),
    ...at.open.map((t) => `#${t.number}: ${t.title}: apply it to #${t.target}, as the answer confirmed at ${t.confirm || 'no Confirm URL on its Raised-by line'} says (${t.url})`),
  ])
  list('Would do, once confirmed', s.apply.wouldDo, 'nothing')
  list('Open questions', s.apply.open.length ? [s.apply.open.map((q) => 'Q' + q).join(', ')] : [])
  list('Answered no, untouched', s.apply.no.length ? [s.apply.no.map((q) => 'Q' + q).join(', ')] : [])
  const c = s.replay.counts || {}
  // A stop is a failed line (#319): only a failed line (replayStop) says "stopped"; a reason the agent
  // gave with no line failed is kept as its note, never read as a stop.
  const stopped = replayStop(s.replay)
  const none = !stopped && !s.replay.renamed && VERBS.every((a) => !((c[a] || {}).applied || (c[a] || {}).skipped || (c[a] || {}).failed))
  list('Replay', [VERBS.map((a) => `${a} ${(c[a] || {}).applied || 0} applied, ${(c[a] || {}).skipped || 0} skipped, ${(c[a] || {}).failed || 0} failed`).join('; ') + (s.replay.renamed ? `; renamed to ${s.replay.renamed}` : '') + (stopped ? `; stopped at ${stopped}: ${s.replay.reason || 'no reason given'}` : '') + (!stopped && s.replay.reason ? `; the agent noted: ${s.replay.reason}` : '') + (none ? '; nothing was replayed' : ''), ...createLines(s.replay)])
  list('main', [v && v.mainMoved ? 'origin/main has moved past the base; nothing was merged in or rebased' : 'origin/main has not moved past the base'])
  if (v && v.findings && v.findings.length) list('Findings', v.findings)
  const fu = s.followUps || { filed: [], already: [], refused: [], forHuman: [] }
  list('Follow-ups filed', fu.filed.map((f) => `#${f.number} from #${f.pr}: ${f.title} (${f.url})`), 'none')
  if ((fu.forHuman || []).length) list('Follow-ups filed ready-for-human', fu.forHuman.map((f) => `#${f.number} from #${f.pr}: ${f.title}: ${f.why}`))
  if ((fu.refused || []).length) list('Follow-ups not filed: tools/ticket_shape.py failed them', fu.refused.map((f) => `from #${f.pr}: ${f.title}: ${f.why}`))
  if (fu.already.length) list('Follow-ups already filed', fu.already.map((f) => `#${f.number} from #${f.pr}: ${f.title}`))
  return L.join('\n').replace(/`/g, "'")
}

// ---------------------------------------------------------------- run: the draft check (#302)
const DRAFTED = {
  type: 'object',
  properties: {
    listing: LISTED,
    issue: { type: 'object', properties: { number: { type: 'integer' }, title: { type: 'string' }, url: { type: 'string' }, state: { type: 'string' }, comments: { type: 'array', items: COMMENT } }, required: ['number', 'title', 'url', 'state', 'comments'] },
    draft: { type: 'string' }, error: { type: 'string' },
  },
  required: ['listing', 'issue', 'draft', 'error'],
}
// Its agent writes nothing anywhere, so it carries these rules rather than the house rules, which
// let an agent write under the run log.
const draftPrompt = () => `Draft check: read the Overnight issue's comments and a draft Confirm comment, and return both verbatim (#302). You read; the script sorts.

Rules (a draft check; you write nothing anywhere):
- Write no file, anywhere, and post, edit, file or close nothing on GitHub: use only \`gh issue view\` and \`gh issue list\`, each with \`cd ${REPO} &&\` in front so it resolves this repository.
- Never commit, checkout, switch, stash, reset or build in ${REPO}, and make no worktree.
- Never quote a secret, token, cookie, code or state value in anything you return but the two texts below, verbatim.

${LOOKUP('')}
2. The issue: \`cd ${REPO} && gh issue view <number> --json number,title,url,state,comments\`. issue carries its number, title, url and state, and comments as {author: the login, createdAt, url, body}, every one, in order, each body verbatim.
3. The draft: open ${A.confirmFile} with the Read tool. draft is its whole text, byte for byte, without the Read tool's line numbers. If it cannot be read, draft is "" and error says why.
Return listing, issue, draft and error ("" when nothing stopped you).`

const checkText = (r) => {
  const L = [`## Draft Confirm, Overnight ${r.date} (#${r.issue})`, '']
  L.push(`The first sentence covers ${r.covered} of ${r.total} questions.`)
  L.push(`${r.overrides} Q<n>: line(s) override; ${r.byOwnLines} confirmed by their own Q<n>: lines alone, named by no first sentence.`)
  L.push(`${r.confirmed} confirmed of ${r.total}: ${r.applied} applied, ${r.no.length} answered no, ${r.open.length} left open.`)
  if (r.notConfirm) L.push('The draft does not begin with "Confirm", so the apply step would read it as no Confirm comment at all.')
  L.push(...draftLists(r))
  L.push('', r.ok ? `check: pass: ${r.confirmed} of ${r.total} confirmed, every question number in the draft found` : `check: fail: ${r.unknown.map((q) => 'Q' + q).join(', ')} named by the draft, yielded by no questions comment`)
  return L.join('\n')
}

if (CHECK) {
  phase('Check')
  const d = await run(draftPrompt(), { label: 'draft-read', phase: 'Check', schema: DRAFTED })
  const dl = judgeLookup(d && d.listing)
  if (dl.refused) return { mode: 'check', halted: dl.refused, agents: agentsRun }
  if (d.issue.number !== dl.issue) return { mode: 'check', halted: `the read agent read #${d.issue.number}, not the run's #${dl.issue}; nothing was sorted`, agents: agentsRun }
  if (d.error || !d.draft.trim()) return { mode: 'check', halted: `the draft ${A.confirmFile} was not read${d.error ? `: ${d.error}` : ': it came back empty'}; nothing was sorted`, agents: agentsRun }
  // The draft, as the one Confirm comment Adam would post, read and sorted by Apply's own code.
  const sorted = draftCheck(d.issue.comments || [], d.draft, A.confirmFile)
  if (sorted.halted) return { mode: 'check', halted: `#${dl.issue} ${sorted.halted}`, agents: agentsRun }
  const r = { mode: 'check', date: dl.date, issue: dl.issue, ...sorted }
  r.ok = r.unknown.length === 0
  r.text = checkText(r)
  log(r.text)
  return { ...r, agents: agentsRun }
}

// ---------------------------------------------------------------- run: after
if (AFTER) {
  phase('After')
  const a = await run(afterPrompt(), { label: 'after', phase: 'After', schema: AFTERED })
  const look = judgeLookup(a && a.listing)
  if (look.refused) return { mode: 'after', halted: look.refused, agents: agentsRun }
  if (!a.contained) {
    const breach = a.closed.length || a.unassigned.length || a.removed.length ? `; the agent acted anyway (closed ${a.closed.length}, unassigned ${a.unassigned.length}, removed ${a.removed.length}), which its prompt forbids` : ''
    return { mode: 'after', date: look.date, issue: look.issue, refused: `origin/main (${a.originMain}) does not contain the integration tip ${a.tip}: land the pull request first${breach}`, closed: [], unassigned: [], removed: [], kept: a.kept, agents: agentsRun }
  }
  log(`after: closed ${a.closed.length}, unassigned ${a.unassigned.length}, removed ${a.removed.length}, kept ${a.kept.length}`)
  return { mode: 'after', date: look.date, issue: look.issue, refused: '', tip: a.tip, originMain: a.originMain, closed: a.closed, unassigned: a.unassigned, removed: a.removed, kept: a.kept, pruned: a.pruned, agents: agentsRun }
}

// ---------------------------------------------------------------- run: the morning
phase('Replay')
const rep = await run(replayPrompt(), { label: 'replay', phase: 'Replay', schema: REPLAY })
if (rep) for (const f of ['pending', 'renamed', 'reason']) rep[f] = blankField(rep[f])
const look = judgeLookup(rep && rep.listing)
if (look.refused) {
  const breach = rep && replayedAny(rep.counts) ? '; the Replay agent applied lines despite the refusal, which its prompt forbids' : ''
  return { mode: 'morning', halted: look.refused + breach, agents: agentsRun }
}
if (rep.issue.number !== look.issue) return { mode: 'morning', halted: `the Replay agent read #${rep.issue.number}, not the run's #${look.issue}; nothing was verified or applied`, agents: agentsRun }
const DATE = look.date
const ISSUE = look.issue
const P = paths(DATE)
const SUBJECT = subjectFor(DATE)
const files = new Map((rep.phaseFiles || []).map((f) => [f.name, f]))
const reused = []
// done: whether a phase file's result is a finished phase. An applier GitHub could not reach, or one
// that refused, is not, so a relaunch dispatches it again rather than replaying the stop.
const reuse = (name, inputs, done = () => true) => {
  const f = files.get(name)
  if (f && f.inputs === inputs && f.result && done(f.result)) { reused.push(name); return f.result }
  return null
}
const refusals = []
// A stop is a failed line, so the refusal keys on a failed line (replayStop), never on reason alone
// (#319): on 2026-10-22 a reason of "" with stoppedAt 0 was refused as "Replay stopped at line 0". A
// stop with no reason is still a stop (CODING_STANDARDS.md -> "A check fails closed").
if (replayStop(rep)) refusals.push(`Replay stopped at ${replayStop(rep)} of ${rep.pending || P.pending}: ${rep.reason || 'no reason given'}`)
refusals.push(...judgeCreates(rep))
log(`morning ${DATE} (#${ISSUE}): run log ${P.mdir}`)

phase('Verify')
const verifyKey = key({ date: DATE, issue: ISSUE, repo: REPO, wt: WTROOT })
const v = await run(verifyPrompt(DATE, ISSUE, verifyKey), { label: 'verify', phase: 'Verify', schema: VERIFY })
const verdict = judgeVerify(v, SUBJECT, ISSUE, P.intBranch)
if (!verdict.ok) refusals.push(verdict.stop)
// The description of a pull request an earlier morning opened, refreshed to this head (#300). Read
// whatever the record says: on 2026-10-22 it was the stale description a review held on.
let refresh = { state: 'none' }
if (v && v.outcome === 'ok' && v.prReused && !v.prBody) {
  // A reused pull request returned with no description is a read that failed, never one with
  // nothing to refresh (CODING_STANDARDS.md -> "A check fails closed").
  refresh = { state: 'refused', reason: `Verify reused #${(v.pr || {}).number} but returned no description, so it was not compared with ${v.head} or refreshed; relaunch to refresh it` }
  refusals.push(refresh.reason)
} else if (v && v.outcome === 'ok' && v.prReused && FULL.test(v.head || '') && v.pr && v.pr.number && v.prBody) {
  refresh = refreshDescription(v.prBody, v.head, v.merges, v.lanes)
  if (refresh.state === 'refused') refusals.push(refresh.reason)
  if (refresh.state === 'rewritten') {
    const w = await run(refreshPrompt(DATE, v.pr.number, v.head, refresh.body), { label: 'pr-refresh', phase: 'Verify', schema: REFRESHED })
    refresh.edited = !!(w && w.edited)
    refresh.reason = w ? w.reason : 'the description writer returned nothing'
    if (!refresh.edited) refusals.push(`the description of #${v.pr.number} names ${refresh.named}, not ${v.head}, and was not rewritten: ${refresh.reason || 'the writer said it did not edit it'}; relaunch to refresh it`)
    refusals.push(...refresh.closing)
  }
}

phase('Apply')
// The comments, read here by the phase that uses them (#319), whatever Verify said: the hand-over's open
// lists come from them too. A read that failed is named and nothing is sorted or applied, never read
// as an issue with no questions and no Confirm comment (CODING_STANDARDS.md -> "A check fails closed").
const cr = await run(commentsPrompt(ISSUE), { label: 'comments', phase: 'Apply', schema: COMMENTS_INDEX })
let commentsRefusal = !cr ? `the comments reader returned nothing: no question or Confirm comment of #${ISSUE} was read, so nothing was sorted or applied; relaunch to apply`
  : cr.error ? `the comments reader could not read the comments of #${ISSUE}: ${cr.error}; nothing was sorted or applied`
    : cr.issue.number !== ISSUE ? `the comments reader read the comments of #${cr.issue.number}, not #${ISSUE}; nothing was sorted or applied`
      : ''
// Every batch is read whole or the read fails: a body missing, or one from another comment than the
// index named there, is named, and nothing is sorted from a partial read.
let comments = []
if (!commentsRefusal) {
  const batches = batchesOf(cr.issue.comments || [])
  const got = await parallel(batches.map((b, k) => () => run(batchPrompt(ISSUE, b.at), { label: `comments-batch:${k + 1}`, phase: 'Apply', schema: COMMENTS_BATCH })))
  const bad = []
  batches.forEach((b, k) => {
    const g = got[k]
    const urls = g && !g.error ? (g.comments || []).map((c) => c.url) : []
    if (!g) bad.push(`batch ${k + 1} (${b.urls.join(', ')}) returned nothing`)
    else if (g.error) bad.push(`batch ${k + 1} (${b.urls.join(', ')}): ${g.error}`)
    else if (urls.join(' ') !== b.urls.join(' ')) bad.push(`batch ${k + 1} returned ${urls.join(', ') || 'no comment'}, not ${b.urls.join(', ')}`)
    else comments.push(...g.comments)
  })
  if (bad.length) {
    commentsRefusal = `the comments of #${ISSUE} were not all read: ${bad.join('; ')}; nothing was sorted or applied; relaunch to apply`
    comments = []
  }
}
if (commentsRefusal) refusals.push(commentsRefusal)
const read = readQuestions(comments)
refusals.push(...read.refusals)
const conf = readConfirmations(comments)
const dry = conf.confirms.length === 0
// A question a Confirm comment names or overrides that no questions comment yielded is a read that
// missed something: nothing is applied over it, and the hand-over says which.
const unread = unreadOf(read.questions, conf)
if (unread.length) refusals.push(`the Confirm comments name ${unread.map((q) => 'Q' + q).join(', ')}, which no "## Questions:" comment yielded; nothing is applied until every confirmed question is read`)
const apply = { ran: false, dry, done: [], wouldDo: [], open: [], no: [], answers: {}, filed: [], morning: { commits: 0, pushed: false, reason: '' } }
// Without a Confirm comment the pre-accepted answers are classified for the would-do list and acted
// on by nobody (#78 grill Q1); with one, only what it confirms is classified and applied.
const sorted = sortAnswers(read.questions, conf, dry)
Object.assign(apply, { answers: sorted.answers, open: sorted.open, no: sorted.no })
const toClassify = sorted.toClassify
if (commentsRefusal) {
  log('Apply skipped: the comments were not read')
} else if (!verdict.ok) {
  // Any Verify stop goes to the hand-over: nothing is applied over a head no record clears (#78 ->
  // "Verify" step 8; the morning resumes once a record exists).
  log('Apply skipped: Verify did not clear the head')
} else if (unread.length) {
  log('Apply skipped: a confirmed question was not read')
} else if (toClassify.length) {
  apply.ran = true
  const classifyKey = key({ questions: toClassify, dry })
  const cls = reuse('classify', classifyKey) || await run(classifyPrompt(DATE, ISSUE, toClassify, dry, classifyKey), { label: 'classify', phase: 'Apply', schema: CLASSIFY })
  // A classifier that returned nothing is named, never read as one that classified nothing
  // (CODING_STANDARDS.md -> "A check fails closed"; #277), and no question is applied.
  if (!cls) refusals.push(`the Classify agent returned nothing: ${toClassify.map((q) => 'Q' + q.q).join(', ')} ${toClassify.length === 1 ? 'was' : 'were'} not classified, so none is applied`)
  const kinds = new Map(cls ? cls.questions.map((c) => [c.q, c]) : [])
  const rewrite = new Set(cls ? cls.rewrites.map((r) => r.issue) : [])
  const byTicket = []
  for (const q of cls ? toClassify : []) {
    const c = kinds.get(q.q)
    if (!c || !KINDS.includes(c.kind)) { refusals.push(`the classifier returned no kind for Q${q.q}; it is not applied`); continue }
    let t = byTicket.find((x) => x.issue === q.issue)
    if (!t) { t = { issue: q.issue, task: q.task, title: q.title, rewrite: rewrite.has(q.issue), questions: [], followUps: [], edits: [], edges: [] }; byTicket.push(t) }
    const plan = planOf(DATE, ISSUE, q, c)
    refusals.push(...plan.refusals)
    t.followUps.push(...plan.followUps)
    t.edits.push(...plan.edits)
    t.edges.push(...plan.edges)
    t.questions.push({ ...q, kind: c.kind, summary: c.summary })
  }
  if (dry) {
    for (const t of byTicket) {
      for (const q of t.questions) if (q.kind !== 'no-repo-change') apply.wouldDo.push(`Q${q.q} (#${t.issue}): ${q.kind}: ${q.summary}`)
      for (const f of t.followUps) apply.wouldDo.push(`Q${f.q} (#${t.issue}): file the follow-up "${f.title}"`)
      if (t.rewrite) apply.wouldDo.push(`#${t.issue}: rewrite its body, the old one preserved in a dated comment`)
    }
  } else {
    const units = unitsFor(byTicket)
    const runUnit = async (u) => {
      const k = key({ unit: u, date: DATE, issue: ISSUE })
      return reuse(`apply/${u.key}`, k, (r) => !r.refused && !r.unreachable) || await run(applyPrompt(DATE, ISSUE, u, k), { label: `apply:${u.kind}:${u.key}`, phase: 'Apply', schema: APPLIED })
    }
    // Chained appliers, those that commit or write a ticket body another may write, run one at a
    // time; tracker-only appliers run beside them, bounded by the slots (#78 -> "Apply"; #320).
    const runChain = async () => {
      const out = []
      for (const u of units.chain) out.push(await runUnit(u))
      return out
    }
    const results = await parallel([runChain,...units.tracker.map((u) => () => runUnit(u))])
    // The body each follow-up was filed with, by title, so the hand-over reads a ticket filed this
    // morning by the same parser as one the follow-ups reader lists (#481).
    const bodies = new Map([...units.chain, ...units.tracker].flatMap((u) => u.followUps).map((f) => [f.title, f.body]))
    for (const r of results.flat().filter(Boolean)) {
      for (const c of r.commits) apply.done.push(`commit ${c.sha} on ${P.morningBranch}: ${c.subject} (${c.questions.map((q) => 'Q' + q).join(', ')})`)
      for (const i of r.issues) apply.done.push(`filed #${i.number} ${i.title} (${i.questions.map((q) => 'Q' + q).join(', ')}): ${i.url}`)
      for (const i of r.issues) apply.filed.push({ number: i.number, title: i.title, url: i.url, body: bodies.get(i.title) || '' })
      for (const c of r.candidates) apply.done.push(`ADR candidate (${c.questions.map((q) => 'Q' + q).join(', ')}): ${c.url}`)
      for (const w of r.rewrites) apply.done.push(`rewrote #${w.issue}; the old body is at ${w.preserved}`)
      for (const e of r.edits) apply.done.push(`edited #${e.issue}: removed ${e.removed.length} box(es); the old body is at ${e.preserved} (${e.questions.map((q) => 'Q' + q).join(', ')})`)
      for (const e of r.edges) apply.done.push(`blocked #${e.issue} by ${e.blockedBy.map((n) => '#' + n).join(', ')} (${e.questions.map((q) => 'Q' + q).join(', ')})`)
      for (const s of r.skipped) apply.done.push(`skipped Q${s.q}: ${s.why}`)
      if (r.refused) refusals.push(`applier ${r.unit} refused: ${r.refused}`)
      if (r.unreachable) refusals.push(`applier ${r.unit}: GitHub could not be reached; relaunch to finish it`)
      if (r.commits.length) { apply.morning.commits += r.commits.length; apply.morning.pushed = r.pushed.ok; apply.morning.reason = r.pushed.reason }
    }
    // An answer needing nothing is named with its reason, never dropped from the hand-over (#320).
    for (const t of byTicket) for (const q of t.questions) if (q.kind === 'no-repo-change') apply.done.push(`skipped Q${q.q} (#${t.issue}): needs nothing: ${q.summary}`)
  }
}

phase('Follow-ups')
// Read whatever Verify said: a review's carried finding is filed whether or not tonight's head clears.
const followUps = { filed: [], already: [], refused: [], forHuman: [] }
const fr = await run(followupsPrompt(DATE), { label: 'followups', phase: 'Follow-ups', schema: FOLLOWUPS })
// The open Apply tickets (#481), read whatever Verify or Apply did: a reader that failed leaves the
// earlier nights' unread, which the hand-over says, never "none" (CODING_STANDARDS.md -> "A check fails closed").
const applyTickets = {
  unread: !fr ? 'the follow-ups agent returned nothing' : fr.error ? `the follow-ups agent could not read them: ${fr.error}` : '',
  open: openApplyTickets(fr && !fr.error ? fr.applyTickets : [], apply.filed),
}
if (!fr) refusals.push('the follow-ups agent returned nothing: no review record\'s follow-ups were read, so none was filed')
else if (fr.error) refusals.push(`the follow-ups agent could not read the open pull requests: ${fr.error}; none was filed`)
else {
  const { toFile, already } = followUpsToFile(fr, P.mdir)
  followUps.already.push(...already)
  // No rules, no owner: a follow-up is filed only with the owner its place derives (#479), so an
  // OWNERSHIP.md the reader could not return, or one with no block, files none (CODING_STANDARDS.md
  // -> "A check fails closed"), as .github/path-ownership.py refuses one.
  if (toFile.length && !ownershipRules(fr.ownership).length) refusals.push(`the follow-ups agent returned no OWNERSHIP.md block, so no follow-up's owner could be derived and none was filed: ${toFile.map((f) => `#${f.pr} ${JSON.stringify(f.title)}`).join(', ')}; relaunch to file them`)
  else if (toFile.length) {
    const fd = await run(fileFollowUpsPrompt(DATE, toFile), { label: 'followups-file', phase: 'Follow-ups', schema: FILED })
    if (!fd) refusals.push(`the follow-ups filer returned nothing: ${toFile.map((f) => `#${f.pr} ${JSON.stringify(f.title)}`).join(', ')} may be unfiled; relaunch to file them`)
    else {
      followUps.filed.push(...fd.filed)
      followUps.already.push(...fd.skipped)
      followUps.refused.push(...fd.refused)
      // Filed ready-for-human, each with why, so the hand-over names what a person must place (#479).
      for (const f of fd.filed) {
        const sent = toFile.find((t) => t.pr === f.pr && sameFinding(t.title, f.title))
        if (sent && sent.label === 'ready-for-human') followUps.forHuman.push({ ...f, why: sent.why })
      }
      // A body tools/ticket_shape.py failed was filed by nobody (#479): named, so it is fixed and filed by hand.
      for (const r of fd.refused) refusals.push(`the follow-up #${r.pr} ${JSON.stringify(r.title)} failed tools/ticket_shape.py and was not filed: ${r.why}`)
      // A follow-up the filer accounts for in no list is named, never read as filed: the hand-over
      // would otherwise say "none" over a finding nobody filed (CODING_STANDARDS.md -> "A check fails closed").
      const told = [...fd.filed, ...fd.skipped, ...fd.refused]
      const lost = toFile.filter((f) => !told.some((t) => t && t.pr === f.pr && sameFinding(t.title, f.title)))
      if (lost.length && !fd.unreachable && !fd.reason) refusals.push(`the follow-ups filer returned ${lost.map((f) => `#${f.pr} ${JSON.stringify(f.title)}`).join(', ')} neither filed nor skipped; relaunch to file them`)
      if (fd.unreachable || fd.reason) refusals.push(`the follow-ups filer stopped: ${fd.reason || 'GitHub could not be reached'}; relaunch to file the rest`)
    }
  }
}

phase('Hand-over')
const text = handoverText({ date: DATE, issue: ISSUE, verify: v, refusals, apply, replay: rep, followUps, refresh, applyTickets })
log(text)
const handed = await run(handoverPrompt(DATE, ISSUE, text), { label: 'handover', phase: 'Hand-over', schema: HANDED })
if (handed && handed.reason) refusals.push(`the hand-over was not posted: ${handed.reason}`)
return {
  mode: 'morning', date: DATE, issue: ISSUE, runLog: P.mdir,
  ok: verdict.ok === true, refusals, stop: verdict.ok ? '' : verdict.stop, stopKind: verdict.ok ? '' : verdict.kind,
  replay: { counts: rep.counts, renamed: rep.renamed, stoppedAt: rep.stoppedAt, reason: rep.reason },
  pr: v ? v.pr : null, head: v ? v.head : '', ci: v ? v.ci : 'none', record: v ? v.record : null,
  apply: { ran: apply.ran, dry: apply.dry, answers: apply.answers, open: apply.open, no: apply.no, wouldDo: apply.wouldDo, done: apply.done, morning: apply.morning },
  handover: { url: handed ? handed.url : '', posted: handed ? handed.posted : false, text },
  followUps, applyTickets,
  refresh: { state: refresh.state, named: refresh.named || '', edited: !!refresh.edited, rewrote: refresh.rewrote || [], reason: refresh.reason || '', body: refresh.body || '' },
  agents: agentsRun, reused,
}
