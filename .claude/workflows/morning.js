export const meta = {
  name: 'morning',
  description: 'Land the mechanical half of an overnight run: replay its pending tracker writes, verify and push its integration branch, open its one pull request, apply the confirmed answers, and hand over what only Adam runs',
  whenToUse: 'The morning after an overnight run, in a foreground session, launched by scriptPath with the night\'s date or its Overnight <date> issue; once Adam has landed the pull request, again with after: true',
  phases: [
    { title: 'Replay', detail: 'one agent: finds the one open Overnight <date> issue, then applies the pending-writes file in order, recording each line, and renames it when every line is applied' },
    { title: 'Verify', detail: 'one agent, eight steps each a stop: the main checkout, the base, the lane worktrees, the check, the push with the hook, one pull request, CI on the exact head, a review record naming it' },
    { title: 'Apply', detail: 'one classifier, then at most maxParallel appliers: commit appliers one at a time on integrator/morning-<date>, tracker-only appliers in parallel; dry with no Confirm comment' },
    { title: 'Hand-over', detail: 'one agent, always: posts the hand-over the script wrote as ## Morning <date> on the Overnight issue' },
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
// Launch (Workflow tool), by scriptPath so the landed file runs and not a session's snapshot:
//   scriptPath: /Users/adammedford/Projects/spa-platform/.claude/workflows/morning.js
//   args: { "repo": "/Users/adammedford/Projects/spa-platform", "date": "2026-10-21",
//           "wt": "/Users/adammedford/.worktrees/overnight-2026-10-21" }
// then, once tools/land.sh has merged the pull request, the same arguments with "after": true.
// Arguments (#78 -> "Arguments"), and no others: an unknown one is refused, so a misspelt `after`
// can never run the wrong mode.
//   date         YYYY-MM-DD, the night's date as its Overnight issue's title carries it. One of date
//   issue        and issue is required; issue is the Overnight <date> issue's number, and the date is
//                read off its title. Given both, they must agree.
//   repo         required  the main checkout, an absolute path. Written only under its run log.
//   wt           required  the worktree root the run used, as the overnight workflow names it:
//                          <wt>/overnight is the integration worktree, <wt>/<owner> each lane's.
//   after        optional  boolean, default false: the post-landing mode (#78 grill Q10).
//   maxParallel  optional  positive integer, default 3: agents in flight at once, and the most
//                          appliers Apply dispatches (#78 -> "Agent budget").
//
// Agent budget (#78 -> "Agent budget"): Replay 1, Verify 1, Apply 1 classifier and at most
// maxParallel appliers, Hand-over 1: seven at the default. The issue lookup is the Replay agent's
// first step, not an agent of its own, so the budget holds; `after` is one agent.
const A = args || {}
const KNOWN_ARGS = ['date', 'issue', 'repo', 'wt', 'after', 'maxParallel']
const badArguments = (a) => {
  const bad = []
  const abs = (p) => typeof p === 'string' && p.startsWith('/') && p.length > 1
  const count = (v) => Number.isInteger(v) && v > 0
  for (const k of Object.keys(a)) if (!KNOWN_ARGS.includes(k)) bad.push(`${k}: not an argument of this workflow; it takes ${KNOWN_ARGS.join(', ')}`)
  if (a.date === undefined && a.issue === undefined) bad.push('date or issue: one is required, e.g. "date": "2026-10-21" or "issue": 226 (the Overnight <date> issue)')
  if (a.date !== undefined && (typeof a.date !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(a.date))) bad.push('date: YYYY-MM-DD, the night the Overnight issue is titled for')
  if (a.issue !== undefined && !count(a.issue)) bad.push('issue: the Overnight <date> issue\'s number, a positive integer')
  if (!abs(a.repo)) bad.push('repo: the main checkout as an absolute path, e.g. /Users/adammedford/Projects/spa-platform')
  if (!abs(a.wt)) bad.push('wt: the worktree root the run used, an absolute path, e.g. /Users/adammedford/.worktrees/overnight-2026-10-21')
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
const WTROOT = A.wt.replace(/\/+$/, '')
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
- Never write a comment that begins "Confirm" or "Approved" on any issue: a confirmation is Adam's act (docs/agents/issue-tracker.md; #78 grill Q7).
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
const LISTED = { type: 'array', items: { type: 'object', properties: { number: { type: 'integer' }, title: { type: 'string' }, state: { type: 'string' } }, required: ['number', 'title', 'state'] } }
const COMMENT = { type: 'object', properties: { author: { type: 'string' }, createdAt: { type: 'string' }, url: { type: 'string' }, body: { type: 'string' } }, required: ['author', 'createdAt', 'url', 'body'] }
const REPLAY = {
  type: 'object',
  properties: {
    listing: LISTED,
    issue: { type: 'object', properties: { number: { type: 'integer' }, title: { type: 'string' }, url: { type: 'string' }, state: { type: 'string' }, body: { type: 'string' }, comments: { type: 'array', items: COMMENT } }, required: ['number', 'title', 'url', 'state', 'body', 'comments'] },
    phaseFiles: { type: 'array', items: { type: 'object', properties: { name: { type: 'string' }, inputs: { type: 'string' }, result: { type: 'object' } }, required: ['name', 'inputs', 'result'] } },
    counts: { type: 'object', properties: { comment: COUNTS, edit: COUNTS, close: COUNTS }, required: ['comment', 'edit', 'close'] },
    pending: { type: 'string' }, renamed: { type: 'string' },
    stoppedAt: { type: 'integer' }, reason: { type: 'string' },
  },
  required: ['listing', 'issue', 'phaseFiles', 'counts', 'pending', 'renamed', 'stoppedAt', 'reason'],
}
const VERIFY = {
  type: 'object',
  properties: {
    outcome: { type: 'string', enum: ['ok', 'stop'] }, step: { type: 'integer' }, reason: { type: 'string' },
    base: { type: 'string' }, head: { type: 'string' }, branch: { type: 'string' },
    pr: { type: 'object', properties: { number: { type: 'integer' }, url: { type: 'string' } }, required: ['number', 'url'] },
    prBodyFile: { type: 'string' }, mergeBodyFile: { type: 'string' }, mergeSubject: { type: 'string' },
    ci: { type: 'string', enum: ['green', 'red', 'pending', 'none'] }, ciUrl: { type: 'string' }, failingJobs: { type: 'array', items: { type: 'string' } },
    pushed: { type: 'boolean' }, mainMoved: { type: 'boolean' }, findings: { type: 'array', items: { type: 'string' } },
    worktrees: { type: 'array', items: { type: 'object', properties: { path: { type: 'string' }, branch: { type: 'string' }, tip: { type: 'string' }, clean: { type: 'boolean' }, ancestor: { type: 'boolean' } }, required: ['path', 'branch', 'tip', 'clean', 'ancestor'] } },
    lanes: { type: 'array', items: { type: 'object', properties: { issue: { type: 'integer' }, task: { type: 'string' }, outcome: { type: 'string', enum: ['integrated', 'partial', 'failed'] }, complete: { type: 'boolean' }, mergeSha: { type: 'string' }, outstanding: { type: 'string' } }, required: ['issue', 'task', 'outcome', 'complete', 'mergeSha', 'outstanding'] } },
    claimed: { type: 'array', items: { type: 'string' } },
    record: { type: 'object', properties: { verdict: { type: 'string', enum: ['merge', 'hold', 'none'] }, head: { type: 'string' }, url: { type: 'string' }, reader: { type: 'string' } }, required: ['verdict', 'head', 'url', 'reader'] },
  },
  required: ['outcome', 'step', 'reason', 'base', 'head', 'branch', 'pr', 'prBodyFile', 'mergeBodyFile', 'mergeSubject', 'ci', 'ciUrl', 'failingJobs', 'pushed', 'mainMoved', 'findings', 'worktrees', 'lanes', 'claimed', 'record'],
}
const KINDS = ['no-repo-change', 'integrator-edit', 'workstream-ticket', 'decision-to-record']
const CLASSIFY = {
  type: 'object',
  properties: {
    questions: { type: 'array', items: { type: 'object', properties: { q: { type: 'integer' }, issue: { type: 'integer' }, task: { type: 'string' }, answer: { type: 'string' }, kind: { type: 'string', enum: KINDS }, summary: { type: 'string' } }, required: ['q', 'issue', 'task', 'answer', 'kind', 'summary'] } },
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
    skipped: { type: 'array', items: { type: 'object', properties: { q: { type: 'integer' }, why: { type: 'string' } }, required: ['q', 'why'] } },
    pushed: { type: 'object', properties: { branch: { type: 'string' }, tip: { type: 'string' }, ok: { type: 'boolean' }, reason: { type: 'string' } }, required: ['branch', 'tip', 'ok', 'reason'] },
    refused: { type: 'string' }, unreachable: { type: 'boolean' },
  },
  required: ['unit', 'commits', 'issues', 'candidates', 'rewrites', 'skipped', 'pushed', 'refused', 'unreachable'],
}
const HANDED = { type: 'object', properties: { url: { type: 'string' }, posted: { type: 'boolean' }, reason: { type: 'string' } }, required: ['url', 'posted', 'reason'] }
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
const readQuestions = (comments) => {
  const groups = new Map()
  for (const c of comments) {
    const body = String(c.body || '')
    const nl = body.indexOf('\n')
    const first = (nl < 0 ? body : body.slice(0, nl)).trim()
    const m = HEADING.exec(first)
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
  const confirms = comments.filter((c) => c.author === ADAM && /^Confirm\b/.test(String(c.body || '').trimStart()))
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

// ---------------------------------------------------------------- the record, judged here
// tools/review_record.py, run by the Verify agent, decides which record clears a head; the script
// still holds the agent's report to the head it verified, so a record of an older head, however it
// is reported, never clears this one (#78 -> "Verify" step 8: a review covers the head it read).
const FULL = /^[0-9a-f]{40}$/
const judgeVerify = (v, subject) => {
  if (!v) return { stop: 'Verify returned nothing', kind: 'stop' }
  if (v.outcome === 'stop') return { stop: `Verify stopped at step ${v.step}: ${v.reason}`, kind: 'stop' }
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

// ---------------------------------------------------------------- applier units
// One applier per grilled ticket with work to do (#78 -> "Apply"), at most maxParallel in all
// (#78 -> "Agent budget"). Past that, every committing ticket shares one applier, since they run on
// one branch one at a time anyway, and the tracker-only tickets share the rest round-robin.
const unitsFor = (byTicket) => {
  const commits = byTicket.filter((t) => t.questions.some((q) => q.kind === 'integrator-edit'))
  const trackers = byTicket.filter((t) => !commits.includes(t) && (t.rewrite || t.questions.some((q) => q.kind === 'workstream-ticket' || q.kind === 'decision-to-record')))
  let cGroups = commits.map((t) => [t])
  let tGroups = trackers.map((t) => [t])
  if (cGroups.length + tGroups.length > MAX_PAR) {
    cGroups = commits.length ? [commits] : []
    const room = MAX_PAR - cGroups.length
    if (room <= 0) {
      cGroups = [[...commits, ...trackers]]
      tGroups = []
    } else if (trackers.length > room) {
      tGroups = Array.from({ length: room }, () => [])
      trackers.forEach((t, i) => tGroups[i % room].push(t))
    }
  }
  const unit = (kind, g) => ({ kind, key: `${kind}-${g.map((t) => t.issue).join('-')}`, tickets: g })
  return { commit: cGroups.map((g) => unit('commit', g)), tracker: tGroups.map((g) => unit('tracker', g)) }
}

// ---------------------------------------------------------------- prompts
const replayPrompt = () => `Replay: find the night's Overnight issue, then replay the tracker writes GitHub refused overnight (#78 -> "Replay").

${HOUSE(paths(A.date || '<date>').mdir)}

${LOOKUP(`The date${A.date ? ` is ${A.date}` : ' is the one in that title'}; call it <date> below.`)}
2. The pending-writes file is ${LOG}/pending-<date>.jsonl: JSON lines {"issue", "action": "comment"|"edit"|"close", "body"}. If ${LOG}/pending-<date>.replayed.jsonl exists and the pending file does not, an earlier morning replayed it: replay nothing. If neither exists, there is nothing to replay. Otherwise apply its lines in file order, skipping each line already listed in the progress file ${LOG}/pending-<date>.progress (one line number per applied line), and append each line's number to the progress file the moment it is applied:
   - a comment: write its body to ${LOG}/morning-<date>/replay-<line>.md and post it, \`cd ${REPO} && gh issue comment <issue> --body-file ${LOG}/morning-<date>/replay-<line>.md\`;
   - an edit that changes labels or assignees: \`cd ${REPO} && gh issue edit <issue> <its flags>\`;
   - an edit that replaces a body (--body-file): only if the issue's body is still the one the lane read, which the lane's saved body file in ${LOG}/ shows (\`cd ${REPO} && gh issue view <issue> --json body --jq .body\`, compared byte for byte); otherwise skip it and count it skipped, because a stale body would clobber a newer spec;
   - a close: \`cd ${REPO} && gh issue close <issue>\`.
   The first line that fails stops the replay: nothing after it is applied, stoppedAt is its line number and reason the error. When every line is applied, rename the file, \`mv ${LOG}/pending-<date>.jsonl ${LOG}/pending-<date>.replayed.jsonl\`, and never delete it. pending is the pending file's path or "", renamed the new path or "".
3. The issue, read after the replay so the comments you return are complete: \`cd ${REPO} && gh issue view <number> --json number,title,url,state,body,comments\`. issue carries its number, title, url, state and body verbatim, and comments as {author: the login, createdAt, url, body}, every one, in order, each body verbatim.
4. The morning's durable state: every JSON file under ${LOG}/morning-<date>/ (apply/ included), each as {name: its path under that directory without .json, inputs, result} from its contents; an empty list when there is none.
Return listing, issue, phaseFiles, counts per action (applied, skipped, failed), pending, renamed, stoppedAt (0 when nothing stopped) and reason ("" when nothing stopped).`

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
2. \`git -C ${REPO} fetch origin\`. The integration branch exists and the base the issue names, resolved to its full sha, is an ancestor of it: \`git -C ${REPO} merge-base --is-ancestor <base> ${p.intBranch}\`. mainMoved is whether origin/main has moved past the base; that is reported, never merged in or rebased onto.
3. Every worktree under ${WTROOT} other than ${p.intWt} and ${p.morningWt}: \`git -C <path> status --porcelain\` prints nothing (a dirty one is a stop naming the worktree and its files), and its tip is an ancestor of the integration tip, \`git -C ${REPO} merge-base --is-ancestor <tip> ${p.intBranch}\`. A tip that is not is a stop when the issue lists its lane as integrated or partial, because the report and git disagree; for a failed lane it is expected, and the worktree is kept and listed. Worktrees left from earlier runs are findings.
4. The check, as the hook runs it, from the base to the head, judging each commit of a history with merges: \`cd ${p.intWt} && tools/check.sh --base <base>\`. Its last line is its verdict; anything but "check: pass:" is a stop with the output's last lines.
5. The push, with the hook running: compare \`git -C ${REPO} ls-remote origin refs/heads/${p.intBranch}\` with the local tip as the house rules say, then \`git -C ${p.intWt} push -u origin ${p.intBranch}\`. pushed is whether the remote now holds the local tip.
6. One pull request from ${p.intBranch} to main: \`cd ${REPO} && gh pr list --head ${p.intBranch} --state open --json number,url,headRefOid\` reuses an open one. Draft its body to ${p.mdir}/pr-body.md from the issue and the lane merge bodies: the integrated, partial and failed rows with their merge shas; one line "Closes #<n>" for each integrated row whose lane reported complete and one line "Ticket: #<n>" for each partial row (#100); the aggregated "Outstanding:" and "Outstanding test ids:" lines; the evidence line (how many gate logs, the last integration gate's head and its test count); and the merge subject above. Draft the merge body to ${p.mdir}/merge-body.md: what was verified and how, citing each task merge by sha, with the same Outstanding lines and no Closes line. Open the pull request when none is open: \`cd ${REPO} && gh pr create --base main --head ${p.intBranch} --title ${JSON.stringify(subjectFor(date))} --body-file ${p.mdir}/pr-body.md\`. mergeSubject is the merge subject above, verbatim.
7. CI on the exact head: the pull request's headRefOid must equal the local tip, else stop. Wait for its required checks in one Bash call with a 600000 ms timeout, \`cd ${REPO} && gh pr checks <number> --watch --interval 30\`, then read \`cd ${REPO} && gh pr checks <number> --json name,state,bucket,link\`. A skipped job counts as passing. ci is green, red (failingJobs naming each failing job), pending (the wait ran out) or none (no checks reported); ciUrl is a run's link on that head.
8. The review record naming the head. Read the pull request's records exactly as tools/land.sh does: \`cd ${REPO} && comments=$(gh api --paginate --slurp repos/{owner}/{repo}/issues/<number>/comments) && reviews=$(gh api --paginate --slurp repos/{owner}/{repo}/pulls/<number>/reviews) && printf '{"comments": %s, "reviews": %s}\\n' "$comments" "$reviews" | ${READER} <head>\`. Exit 0: record {verdict "merge", head: the head you passed, url: the URL it printed}. Exit 1 with "says hold: <url>": record {verdict "hold", head, url}. Exit 1 otherwise: record {verdict "none", head "", url ""}. Exit 2: a stop with its message. reader is the exit status and its first line. A hand-written record in the fixed form counts; nothing else does, and you never write one.
Return every field: lanes from the issue's rows (issue, task, outcome, complete, mergeSha, outstanding), claimed the claimed branches the issue names, worktrees as step 3 read them, findings as plain lines. Fields a stop left unread are empty.`
}

const classifyPrompt = (date, issue, questions, dry, phaseKey) => {
  const p = paths(date)
  return `Classify the ${dry ? 'provisional (pre-accepted, not yet confirmed)' : 'confirmed'} answers of the overnight run of ${date} (Overnight #${issue}) for Apply (#78 -> "Apply"). You write nothing but your phase file: you classify, and the appliers act.

${HOUSE(p.mdir)}

PHASE FILE: ${p.mdir}/classify.json INPUTS: ${phaseKey}
Write {"inputs": "${phaseKey}", "result": <your return value>} to the PHASE FILE as JSON before you return.
QUESTIONS: ${JSON.stringify(questions)}

For each question above, read its ticket's "## Questions for Adam", "## Grilling record" and "Proposed glossary / ADR" comments (\`cd ${REPO} && gh issue view <issue> --json title,body,labels,comments\`) and assign it exactly one kind:
- no-repo-change: the decision is already recorded by the grilling, or applying it is code, a test, a script or anything that needs a build or a run. That work belongs to the ticket's implementation, never to the morning.
- integrator-edit: a document a session steers by, on an integrator path: a glossary term, a sentence in an agent guide, a change request's status line (#78 grill Q8). Never a contract sentence or an ADR, its status or its index row: those are decision-to-record.
- workstream-ticket: an edit inside a workstream's paths (OWNERSHIP.md), which becomes a ticket rather than an edit.
- decision-to-record: an ADR candidate, drafted as a comment and never as a file.
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

Skip what an earlier morning already did, and list it in skipped with why: a commit on ${p.morningBranch} whose body cites #${issue} and the question (\`git -C ${REPO} log origin/main..${p.morningBranch} --format=%B\`), an issue whose body cites them (\`cd ${REPO} && gh issue list --state all --search "#${issue} Q<n> in:body" --json number,url,title\`), a candidate or a preserved body already among the ticket's comments.
${commits ? `Integrator edits, one commit each, on ${p.morningBranch} in the morning worktree ${p.morningWt}, forked from origin/main: reuse it when it exists, else \`git -C ${REPO} fetch origin && git -C ${REPO} worktree add -b ${p.morningBranch} ${p.morningWt} origin/main\` (when the branch exists without the worktree, \`git -C ${REPO} worktree add ${p.morningWt} ${p.morningBranch}\`). Each commit's subject says what changed; its body cites the Overnight issue as #${issue}, the question as Q<n> and its answer line verbatim; its last paragraph carries the attribution line and, on a line of its own, "Authority: <the question's authority URL>", because every such edit is to a steering document (docs/agents/landing-work.md). Commit with \`git -C ${p.morningWt} commit -F <a message file under ${p.mdir}/>\`. When the branch has commits, push it with the hook running, as the house rules say: \`git -C ${p.morningWt} push -u origin ${p.morningBranch}\`. A hook failure leaves it local: pushed.ok false and reason the hook's last lines.
` : `You make no commit. pushed is {branch: "", tip: "", ok: true, reason: "no commits"}.
`}Workstream tickets: file each as \`cd ${REPO} && gh issue create --title "<what to build>" --label needs-triage --parent ${PARENT_ISSUE} --body-file <a file under ${p.mdir}/>\` (#78 grill Q9: the night's Route phase writes the brief), its body opening with the owner line (**Owner:** and the OWNERSHIP.md role) and the plan row where there is one, citing #${issue} and Q<n>, and add the blocked-by edges the recommendation names with \`cd ${REPO} && gh issue edit <new> --add-blocked-by <n>\`.
Decisions to record: draft each ADR candidate as a comment on its ticket, \`cd ${REPO} && gh issue comment <ticket> --body-file <a file under ${p.mdir}/>\`: the decision, the rejected alternatives, and any sentence that contradicts an existing ADR flagged as docs/agents/domain.md asks. Never an ADR file.
Rewrites (a ticket with "rewrite": true): first preserve the current body verbatim in a comment headed "## Body before ${date}" (\`cd ${REPO} && gh issue comment <ticket> --body-file <file>\`), then rewrite it as current decision, what to build and acceptance, naming every confirmed question number, \`cd ${REPO} && gh issue edit <ticket> --body-file <file>\`. A spec's body is never rewritten (#78 grill Q13): list that one in skipped.
A no-repo-change question needs nothing. If GitHub cannot be reached, stop writing, set unreachable true and return what was made. refused is "" unless you refused the whole unit, with why.`
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
  L.push(`Overnight #${s.issue}; integration branch ${p.intBranch}${v && v.head ? ` at ${v.head}` : ''}${v && v.base ? `, base ${v.base}` : ''}. Apply: ${s.apply.ran ? (s.apply.dry ? 'dry, no Confirm comment' : 'applied') : 'not run'}.`)
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
  const lanes = (v && v.lanes) || []
  list('Issues to close', lanes.filter((l) => l.outcome === 'integrated' && l.complete).map((l) => `#${l.issue} (${l.task}, merge ${l.mergeSha}): its Closes line closes it at the landing; after: true closes it if still open`))
  list('Issues to unassign, once merged', lanes.filter((l) => l.outcome === 'partial').map((l) => `#${l.issue} (${l.task}): Outstanding: ${l.outstanding || 'see its Answer'}`))
  list('Worktrees to remove after the landing, then git worktree prune', ((v && v.worktrees) || []).map((w) => `${w.path} (${w.branch} at ${w.tip}: ${w.clean ? 'clean' : 'dirty'}, ${w.ancestor ? 'on the integration branch' : 'not on the integration branch'})`))
  list('Claimed branches needing their own pull requests', (v && v.claimed) || [])
  list('Failed lanes\' branches, kept', lanes.filter((l) => l.outcome === 'failed').map((l) => `#${l.issue} (${l.task})`))
  const mb = s.apply.morning
  list('Morning branch', mb.commits ? [`${p.morningBranch}: ${mb.commits} commit(s), ${mb.pushed ? 'pushed' : `not pushed (${mb.reason})`}. Its pull request is yours to open: gh pr create --base main --head ${p.morningBranch}`] : [], 'no commits')
  list('Applied', s.apply.done, 'nothing')
  list('Would do, once confirmed', s.apply.wouldDo, 'nothing')
  list('Open questions', s.apply.open.length ? [s.apply.open.map((q) => 'Q' + q).join(', ')] : [])
  list('Answered no, untouched', s.apply.no.length ? [s.apply.no.map((q) => 'Q' + q).join(', ')] : [])
  const c = s.replay.counts || {}
  list('Replay', [['comment', 'edit', 'close'].map((a) => `${a} ${(c[a] || {}).applied || 0} applied, ${(c[a] || {}).skipped || 0} skipped, ${(c[a] || {}).failed || 0} failed`).join('; ') + (s.replay.renamed ? `; renamed to ${s.replay.renamed}` : '') + (s.replay.reason ? `; stopped at line ${s.replay.stoppedAt}: ${s.replay.reason}` : '')])
  list('main', [v && v.mainMoved ? 'origin/main has moved past the base; nothing was merged in or rebased' : 'origin/main has not moved past the base'])
  if (v && v.findings && v.findings.length) list('Findings', v.findings)
  return L.join('\n').replace(/`/g, "'")
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
if (rep.reason) refusals.push(`Replay stopped at line ${rep.stoppedAt} of ${rep.pending || P.pending}: ${rep.reason}`)
log(`morning ${DATE} (#${ISSUE}): run log ${P.mdir}`)

phase('Verify')
const verifyKey = key({ date: DATE, issue: ISSUE, repo: REPO, wt: WTROOT })
const v = await run(verifyPrompt(DATE, ISSUE, verifyKey), { label: 'verify', phase: 'Verify', schema: VERIFY })
const verdict = judgeVerify(v, SUBJECT)
if (!verdict.ok) refusals.push(verdict.stop)

phase('Apply')
const comments = rep.issue.comments || []
const read = readQuestions(comments)
refusals.push(...read.refusals)
const conf = readConfirmations(comments)
const dry = conf.confirms.length === 0
// A question a Confirm comment names or overrides that no questions comment yielded is a read that
// missed something: nothing is applied over it, and the hand-over says which.
const readQs = new Set(read.questions.map((q) => q.q))
const unread = [...new Set([...conf.covered.keys(), ...conf.overrides.keys()])].filter((q) => !readQs.has(q)).sort((a, b) => a - b)
if (unread.length) refusals.push(`the Confirm comments name ${unread.map((q) => 'Q' + q).join(', ')}, which no "## Questions:" comment yielded; nothing is applied until every confirmed question is read`)
const apply = { ran: false, dry, done: [], wouldDo: [], open: [], no: [], answers: {}, morning: { commits: 0, pushed: false, reason: '' } }
// Without a Confirm comment the pre-accepted answers are classified for the would-do list and acted
// on by nobody (#78 grill Q1); with one, only what it confirms is classified and applied.
const toClassify = []
for (const q of read.questions) {
  const o = conf.overrides.get(q.q)
  const confirmed = !dry && (conf.covered.has(q.q) || !!o)
  const answer = o ? o.answer : q.answer
  const vd = verdictOf(answer)
  apply.answers[q.q] = { answer, confirmed, verdict: vd }
  if (dry ? vd !== 'yes' : (!confirmed || vd === 'open')) { apply.open.push(q.q); continue }
  if (vd === 'no') { apply.no.push(q.q); continue }
  toClassify.push({ q: q.q, issue: q.issue, task: q.task, title: q.title, text: q.text, recommendation: q.recommendation, answer, authority: dry ? '' : (o ? o.url : conf.covered.get(q.q)) })
}
if (!verdict.ok) {
  // Any Verify stop goes to the hand-over: nothing is applied over a head no record clears (#78 ->
  // "Verify" step 8; the morning resumes once a record exists).
  log('Apply skipped: Verify did not clear the head')
} else if (unread.length) {
  log('Apply skipped: a confirmed question was not read')
} else if (toClassify.length) {
  apply.ran = true
  const classifyKey = key({ questions: toClassify, dry })
  const cls = reuse('classify', classifyKey) || await run(classifyPrompt(DATE, ISSUE, toClassify, dry, classifyKey), { label: 'classify', phase: 'Apply', schema: CLASSIFY })
  const kinds = new Map(((cls && cls.questions) || []).map((c) => [c.q, c]))
  const rewrite = new Set(((cls && cls.rewrites) || []).map((r) => r.issue))
  const byTicket = []
  for (const q of toClassify) {
    const c = kinds.get(q.q)
    if (!c || !KINDS.includes(c.kind)) { refusals.push(`the classifier returned no kind for Q${q.q}; it is not applied`); continue }
    let t = byTicket.find((x) => x.issue === q.issue)
    if (!t) { t = { issue: q.issue, task: q.task, title: q.title, rewrite: rewrite.has(q.issue), questions: [] }; byTicket.push(t) }
    t.questions.push({ ...q, kind: c.kind, summary: c.summary })
  }
  if (dry) {
    for (const t of byTicket) {
      for (const q of t.questions) if (q.kind !== 'no-repo-change') apply.wouldDo.push(`Q${q.q} (#${t.issue}): ${q.kind}: ${q.summary}`)
      if (t.rewrite) apply.wouldDo.push(`#${t.issue}: rewrite its body, the old one preserved in a dated comment`)
    }
  } else {
    const units = unitsFor(byTicket)
    const runUnit = async (u) => {
      const k = key({ unit: u, date: DATE, issue: ISSUE })
      return reuse(`apply/${u.key}`, k, (r) => !r.refused && !r.unreachable) || await run(applyPrompt(DATE, ISSUE, u, k), { label: `apply:${u.kind}:${u.key}`, phase: 'Apply', schema: APPLIED })
    }
    // Commit appliers share one branch and run one at a time; tracker-only appliers run beside
    // them, bounded by the slots (#78 -> "Apply").
    const commitChain = async () => {
      const out = []
      for (const u of units.commit) out.push(await runUnit(u))
      return out
    }
    const results = await parallel([commitChain, ...units.tracker.map((u) => () => runUnit(u))])
    for (const r of results.flat().filter(Boolean)) {
      for (const c of r.commits) apply.done.push(`commit ${c.sha} on ${P.morningBranch}: ${c.subject} (${c.questions.map((q) => 'Q' + q).join(', ')})`)
      for (const i of r.issues) apply.done.push(`filed #${i.number} ${i.title} (${i.questions.map((q) => 'Q' + q).join(', ')}): ${i.url}`)
      for (const c of r.candidates) apply.done.push(`ADR candidate (${c.questions.map((q) => 'Q' + q).join(', ')}): ${c.url}`)
      for (const w of r.rewrites) apply.done.push(`rewrote #${w.issue}; the old body is at ${w.preserved}`)
      for (const s of r.skipped) apply.done.push(`skipped Q${s.q}: ${s.why}`)
      if (r.refused) refusals.push(`applier ${r.unit} refused: ${r.refused}`)
      if (r.unreachable) refusals.push(`applier ${r.unit}: GitHub could not be reached; relaunch to finish it`)
      if (r.commits.length) { apply.morning.commits += r.commits.length; apply.morning.pushed = r.pushed.ok; apply.morning.reason = r.pushed.reason }
    }
  }
}

phase('Hand-over')
const text = handoverText({ date: DATE, issue: ISSUE, verify: v, refusals, apply, replay: rep })
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
  agents: agentsRun, reused,
}
