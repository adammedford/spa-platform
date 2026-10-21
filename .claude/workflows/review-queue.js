export const meta = {
  name: 'review-queue',
  description: 'Review each named open pull request into main on the Standards and Spec axes, and post one comment ending in its review record',
  whenToUse: 'An integrator session, in the foreground, names open pull requests into main that need a review record before tools/land.sh merges them',
  phases: [
    { title: 'Inventory', detail: 'one agent per pull request reads head, fork point, files, owner set, CI, citations and the records already posted; it judges nothing' },
    { title: 'Review', detail: 'two fresh agents per pull request, Standards and Spec, read the branch through origin and rebuild nothing' },
    { title: 'Verdict', detail: 'one agent per pull request re-takes every severity and says merge or hold; one comment is posted, its last line the record' },
  ],
}

// ---------------------------------------------------------------- what this is
// The walking skeleton of the review-queue spec (#79, ticket 1 of 5: #83). For each pull request it is
// given it inventories, reviews on both axes, decides, and posts exactly one comment whose last line is
// the review record docs/agents/review.md -> "The review record" defines. It never merges, pushes,
// tags, labels, assigns or edits a branch; mutations, delta review and the open queue are later
// tickets, and its comment says "No mutations run". Test companion: review-queue_tests.mjs beside it.
//
// Launch (Workflow tool), by scriptPath so the landed file runs and not a session's snapshot:
//   scriptPath: /Users/adammedford/Projects/spa-platform/.claude/workflows/review-queue.js
//   args: { "repo": "/Users/adammedford/Projects/spa-platform", "date": "2026-10-22",
//           "wt": "/Users/adammedford/.worktrees/review-queue-2026-10-22", "prs": [255] }
// Arguments. Every required one is required because the script can derive none of them: it has no
// clock, environment or filesystem.
//   repo          required  the main checkout, an absolute path. Read-only apart from the run's log.
//   date          required  YYYY-MM-DD. Names the run's log directory.
//   wt            required  the root for throwaway worktrees, an absolute path outside repo.
//   prs           required  a non-empty array of pull request numbers into main. The open queue, read
//                           when no list is given, is a later ticket.
//   maxParallel   optional  positive integer, default 3: agents running at once.
//   skipRecorded  optional  boolean, default true. A head that already carries a record of either
//                           verdict is skipped and reported; false reviews it again and posts a new
//                           record, which is how a hold answered without a new commit becomes merge.
const A = args || {}
const badArguments = (a) => {
  const bad = []
  const abs = (p) => typeof p === 'string' && p.startsWith('/') && p.length > 1
  const count = (v) => Number.isInteger(v) && v > 0
  if (!abs(a.repo)) bad.push('repo: the main checkout as an absolute path, e.g. /Users/adammedford/Projects/spa-platform')
  if (typeof a.date !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(a.date)) bad.push('date: YYYY-MM-DD, which names the run\'s log directory')
  if (!abs(a.wt)) bad.push('wt: the root for throwaway worktrees, an absolute path outside repo')
  else if (abs(a.repo) && (a.wt === a.repo || a.wt.startsWith(a.repo.replace(/\/+$/, '') + '/'))) bad.push('wt: must lie outside repo; no worktree is made under the repository')
  if (!Array.isArray(a.prs) || a.prs.length === 0 || !a.prs.every(count)) bad.push('prs: a non-empty array of pull request numbers, e.g. [255]')
  if (a.maxParallel !== undefined && !count(a.maxParallel)) bad.push('maxParallel: a positive integer (default 3)')
  if (a.skipRecorded !== undefined && typeof a.skipRecorded !== 'boolean') bad.push('skipRecorded: true or false, a boolean (default true)')
  return bad
}
const BAD_LAUNCH = badArguments(A)
if (BAD_LAUNCH.length) {
  // Stops here: no agent is started, nothing is read or written.
  return { halted: 'bad launch; no agent started. Fix each argument and relaunch with all of them:\n- ' + BAD_LAUNCH.join('\n- ') }
}
const REPO = A.repo.replace(/\/+$/, '')
const DATE = A.date
const WTROOT = A.wt.replace(/\/+$/, '')
const PRS = [...new Set(A.prs)]
const MAX_PAR = A.maxParallel || 3
const SKIP_RECORDED = A.skipRecorded !== false
// The run's directory under the git-ignored scratch log (.gitignore: .scratch/*/log/). It is the only
// place the run writes in the main checkout, apart from git's own bookkeeping for fetches and worktrees.
const LOG = `${REPO}/.scratch/review-queue/log/${DATE}`
const DIR = (n) => `${LOG}/pr-${n}`

// ---------------------------------------------------------------- the review record
// The one producer and the one recogniser of the record line. The grammar is fixed by #67: the
// reader tools/land.sh runs is tools/review_record.py, and docs/agents/review.md -> "The review
// record" states it. This copy agrees with that reader on what counts: one stripped line in the
// exact form with both shas full and lowercase, in an issue comment or in a review whose state is
// COMMENTED, posted by an account the repository let in (OWNER, MEMBER, COLLABORATOR). A change to
// the grammar is an edit to these lines and to the reader, together.
const RECORD = /^Reviewed ([0-9a-f]{40}) against ([0-9a-f]{40}): (merge|hold)$/
const REPOSITORY_ACCOUNTS = ['OWNER', 'MEMBER', 'COLLABORATOR']
const recordLine = (head, base, verdict) => {
  const line = `Reviewed ${head} against ${base}: ${verdict}`
  if (!RECORD.test(line)) throw new Error(`refusing to write a record not in the form: ${line}`)
  return line
}
// Every record in an inventory's comments and reviews: { head, base, verdict, url, at }.
const recordsIn = (inv) => {
  const found = []
  const read = (entry, at) => {
    if (!entry || typeof entry.body !== 'string') return
    if (!REPOSITORY_ACCOUNTS.includes(entry.author_association)) return
    for (const raw of entry.body.split(/\r?\n/)) {
      const m = RECORD.exec(raw.trim())
      if (m) found.push({ head: m[1], base: m[2], verdict: m[3], url: entry.html_url || '', at: entry[at] || '' })
    }
  }
  for (const c of inv.comments || []) read(c, 'created_at')
  for (const r of inv.reviews || []) if (r && r.state === 'COMMENTED') read(r, 'submitted_at')
  return found.sort((x, y) => (x.at < y.at ? -1 : x.at > y.at ? 1 : 0))
}

// ---------------------------------------------------------------- agents and slots
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

// ---------------------------------------------------------------- shared rules
const HOUSE = `House rules (a review run; you write nothing under review):
- The main checkout ${REPO} is read-only. Never commit, checkout, switch, stash, reset, build or edit there. The only files you may write there are under ${LOG}/, and git's own bookkeeping for \`git fetch\` and \`git worktree\` is the only other change allowed.
- Run every \`gh\` command with \`cd ${REPO} &&\` in front so it resolves this repository.
- Read the branch through origin, with docs/agents/review.md -> "Read the branch, never the checkout": \`git -C ${REPO} fetch origin --tags\`, \`git -C ${REPO} diff <fork>...<head>\`, \`git -C ${REPO} log <fork>..<head> --oneline\`, \`git -C ${REPO} show <head>:<path>\`, \`git -C ${REPO} show origin/main:<path>\`. Never check the branch out.
- CI has already run. Rebuild nothing and never run the solution. Where a runtime claim genuinely must be run, make a throwaway worktree under ${WTROOT}, detached at the head, run the one project there, and remove it with \`git -C ${REPO} worktree remove --force <path>\` and \`git -C ${REPO} worktree prune\` before you return.
- Never push, merge, tag, label, assign, or edit a pull request's body or branch. Never start a process on a port.
- Never log or quote a credential, token, cookie, authorization code or state value.
- Write each OUTPUT FILE line's path exactly as given, and no other file.`

const AXIS_GUIDE = `Read docs/agents/review.md (at origin/main) before anything else: it is the review guide, with the sources each axis holds the diff to and the bar for a finding. CODING_STANDARDS.md is the standard. Read both yourself; this prompt does not transcribe them.`

// ---------------------------------------------------------------- schemas
const ENTRY = {
  type: 'object',
  properties: {
    body: { type: 'string' }, author_association: { type: 'string' }, html_url: { type: 'string' },
    created_at: { type: 'string' }, submitted_at: { type: 'string' }, state: { type: 'string' },
  },
  required: ['body', 'author_association', 'html_url'],
}
const INVENTORY = {
  type: 'object',
  properties: {
    number: { type: 'integer' }, title: { type: 'string' }, author: { type: 'string' },
    branch: { type: 'string' }, baseRef: { type: 'string' },
    head: { type: 'string' }, baseTip: { type: 'string' }, forkPoint: { type: 'string' },
    commits: { type: 'array', items: { type: 'string' } },
    changedFiles: { type: 'array', items: { type: 'string' } },
    ownership: { type: 'object', properties: { owners: { type: 'array', items: { type: 'string' } }, ok: { type: 'boolean' }, output: { type: 'string' } }, required: ['owners', 'ok', 'output'] },
    ci: { type: 'array', items: { type: 'object', properties: { name: { type: 'string' }, state: { type: 'string' } }, required: ['name', 'state'] } },
    cites: { type: 'object', properties: { planRow: { type: 'string' }, issues: { type: 'array', items: { type: 'integer' } }, mergeSubject: { type: 'string' } }, required: ['planRow', 'issues', 'mergeSubject'] },
    bodyClaims: { type: 'array', items: { type: 'string' } },
    comments: { type: 'array', items: ENTRY },
    reviews: { type: 'array', items: ENTRY },
    error: { type: 'string' },
  },
  required: ['number', 'title', 'author', 'branch', 'baseRef', 'head', 'baseTip', 'forkPoint', 'commits', 'changedFiles', 'ownership', 'ci', 'cites', 'bodyClaims', 'comments', 'reviews', 'error'],
}
const FINDING = {
  type: 'object',
  properties: {
    axis: { type: 'string', enum: ['Standards', 'Spec'] }, title: { type: 'string' }, where: { type: 'string' },
    checked: { type: 'string' }, status: { type: 'string', enum: ['verified', 'suspected'] }, severity: { type: 'string' },
  },
  required: ['axis', 'title', 'where', 'checked', 'status', 'severity'],
}
const CLAIM = {
  type: 'object',
  properties: { claim: { type: 'string' }, verified: { type: 'boolean' }, how: { type: 'string' } },
  required: ['claim', 'verified', 'how'],
}
const AXIS = {
  type: 'object',
  properties: {
    axis: { type: 'string', enum: ['Standards', 'Spec'] },
    sourcesRead: { type: 'array', items: { type: 'string' } },
    findings: { type: 'array', items: FINDING },
    claims: { type: 'array', items: CLAIM },
    nominations: { type: 'array', items: { type: 'string' } },
  },
  required: ['axis', 'sourcesRead', 'findings', 'claims', 'nominations'],
}
const VERDICT = {
  type: 'object',
  properties: {
    verdict: { type: 'string', enum: ['merge', 'hold'] },
    findings: { type: 'array', items: FINDING },
    claims: { type: 'array', items: CLAIM },
    outstanding: { type: 'array', items: { type: 'string' } },
    summary: { type: 'string' },
  },
  required: ['verdict', 'findings', 'claims', 'outstanding', 'summary'],
}
const POST = {
  type: 'object',
  properties: {
    posted: { type: 'boolean' }, url: { type: 'string' }, headNow: { type: 'string' },
    unreachable: { type: 'boolean' }, bodyFile: { type: 'string' },
  },
  required: ['posted', 'url', 'headNow', 'unreachable', 'bodyFile'],
}

// ---------------------------------------------------------------- prompts
const inventoryPrompt = (n) => `Inventory pull request #${n} for a review. Read and report; judge nothing.

${HOUSE}

OUTPUT FILE: ${DIR(n)}/inventory.json

1. \`git -C ${REPO} fetch origin --tags\`. Then \`cd ${REPO} && gh pr view ${n} --json number,title,author,headRefName,baseRefName,headRefOid,state,body,statusCheckRollup\`.
2. If the pull request is not OPEN or its base is not main, set error to say which, fill what you read, and stop.
3. head = headRefOid (40 hex). baseTip = \`git -C ${REPO} rev-parse origin/main\`. forkPoint = \`git -C ${REPO} merge-base origin/main <head>\`. commits = \`git -C ${REPO} log --format=%H <forkPoint>..<head>\`. changedFiles = \`git -C ${REPO} diff --name-only <forkPoint>...<head>\`.
4. ownership: run \`cd ${REPO} && python3 -I .github/path-ownership.py <forkPoint> <head>\`; owners = the owner sets it names, ok = whether it exited 0, output = its last lines.
5. ci: one row per check of statusCheckRollup, state one of success, failure, pending, skipped.
6. cites: the plan row id, the issue numbers and the merge subject the body proposes (empty when absent). bodyClaims: every claim the body makes about what it did or proved, quoted, mutation statements with their counts included.
7. comments: \`cd ${REPO} && gh api --paginate --slurp repos/{owner}/{repo}/issues/${n}/comments\`, flattened, each entry's body, author_association, html_url and created_at exactly as served. reviews: the same for \`repos/{owner}/{repo}/pulls/${n}/reviews\`, with body, state, author_association, html_url and submitted_at. Copy bodies byte for byte: the script reads review records from them.
8. Write the whole result as JSON to the OUTPUT FILE, then return it. error is "" when nothing stopped you.`

const axisPrompt = (axis, n, inv) => `You are the ${axis} axis of a review of pull request #${n}: ${inv.title}. You are a fresh agent and wrote none of its commits.

${HOUSE}

OUTPUT FILE: ${DIR(n)}/${axis.toLowerCase()}.json

${AXIS_GUIDE}

The pull request, as the inventory read it (${DIR(n)}/inventory.json holds the whole of it):
- head ${inv.head}, fork point ${inv.forkPoint}, origin/main ${inv.baseTip}, branch ${inv.branch}
- changed files: ${inv.changedFiles.join(', ') || '(none)'}
- owner set: ${inv.ownership.owners.join(', ') || '(none)'}; path-ownership ${inv.ownership.ok ? 'passed' : 'refused'}: ${inv.ownership.output}
- CI on the head: ${inv.ci.map((c) => `${c.name} ${c.state}`).join(', ') || '(none reported)'}. CI has already run.
- cites: plan row ${inv.cites.planRow || '(none)'}, issues ${inv.cites.issues.map((i) => '#' + i).join(', ') || '(none)'}
- the author's claims: ${inv.bodyClaims.length ? inv.bodyClaims.map((c) => JSON.stringify(c)).join('; ') : '(none)'}

Read the diff yourself with the review guide's commands (\`git -C ${REPO} diff ${inv.forkPoint}...${inv.head}\`). A diff with no code in it, docs-only or tracker-only, is reviewed all the same: no pull request is exempt by what it touches.
${axis === 'Standards'
    ? 'Hold the diff to CODING_STANDARDS.md, AGENTS.md, CLAUDE.md and OWNERSHIP.md; a path-ownership refusal or a diff outside one owner\'s set is a finding of its own. Name in nominations every assertion you suspect cannot fail, by file and test name.'
    : 'Hold the diff to the spec sources the review guide\'s table names: the plan row, the issue\'s body and comments, the contracts and pipeline table, the test and evidence ids the row names, change requests and ADRs. The pull request body is the author\'s claim, not the spec: put every claim in claims, verified or not, and how.'}
Each finding: axis "${axis}", what and where, what you read or ran to check it, verified or suspected, and an advisory severity the verdict re-takes. List every source you read in sourcesRead. Write the result as JSON to the OUTPUT FILE, then return it.`

const verdictPrompt = (n, inv, axes) => `Decide the review of pull request #${n}: ${inv.title}. You wrote none of its commits.

${HOUSE}

OUTPUT FILE: ${DIR(n)}/verdict.json

${AXIS_GUIDE} Its "The bar for a finding" and "Landing or holding" decide.

Read ${DIR(n)}/inventory.json, ${DIR(n)}/standards.json and ${DIR(n)}/spec.json. ${axes.missing.length ? `The ${axes.missing.join(' and ')} axis returned nothing: say so in summary, and hold.` : ''}
Take every finding's severity for yourself: a sub-agent's "looks wrong" is sometimes the contract's own design. You may run one further targeted check in a throwaway worktree, never the solution. Mark each finding verified or suspected and say how it was checked. Carry the author's claims with whether each was verified and how.
verdict is hold when a verified finding shows the change does not do what it says, or a test that proves it cannot fail; otherwise merge. No mutations run in this version of the review, so none is claimed. outstanding lists what stays for the merge body's Outstanding: line. Write the result as JSON to the OUTPUT FILE, then return it.`

const postPrompt = (n, record, body) => `Post one review comment on pull request #${n}, at most once.

${HOUSE}

OUTPUT FILE: ${DIR(n)}/comment.md

1. Write the text between the line BEGIN COMMENT and the line END COMMENT below, exactly and without those two lines, to the OUTPUT FILE. bodyFile is that path.
2. headNow = \`cd ${REPO} && gh pr view ${n} --json headRefOid --jq .headRefOid\`.
3. Read the pull request's comments (\`cd ${REPO} && gh api --paginate repos/{owner}/{repo}/issues/${n}/comments\`). If one already carries the line \`${record}\` on a line of its own, this run posted it before a resume: post nothing, set posted false and url to that comment's html_url.
4. Otherwise post it: \`cd ${REPO} && gh pr comment ${n} --body-file ${DIR(n)}/comment.md\`; posted true, url the comment's URL it prints. Post it even if headNow differs from the head the record names: the record names the head that was read.
5. If GitHub cannot be reached, post nothing and retry nothing: unreachable true, posted false, url "".

BEGIN COMMENT
${body}
END COMMENT`

// ---------------------------------------------------------------- the comment
const findingLines = (list) => (list.length
  ? list.map((f) => `- **${f.title}** (${f.status}, ${f.severity}) at ${f.where}. Checked: ${f.checked}`)
  : ['- None.'])
const commentBody = (inv, v, axes, record) => [
  '## Review',
  '',
  `Head ${inv.head}, read against the fork point ${inv.forkPoint} (origin/main at ${inv.baseTip}).`,
  `CI on the head: ${inv.ci.map((c) => `${c.name} ${c.state}`).join(', ') || 'none reported'}.`,
  `Owner set: ${inv.ownership.owners.join(', ') || 'none'} (path-ownership ${inv.ownership.ok ? 'passed' : 'refused'}).`,
  `Axes: Standards ${axes.standards ? 'reported' : 'returned nothing'}, Spec ${axes.spec ? 'reported' : 'returned nothing'}; the verdict re-took every severity. Run by the review-queue workflow (#83).`,
  '',
  '## Standards',
  '',
  ...findingLines(v.findings.filter((f) => f.axis === 'Standards')),
  '',
  '## Spec',
  '',
  ...findingLines(v.findings.filter((f) => f.axis === 'Spec')),
  '',
  "The author's claims:",
  ...(v.claims.length ? v.claims.map((c) => `- ${c.claim}: ${c.verified ? 'verified' : 'not verified'}. ${c.how}`) : ['- None stated.']),
  '',
  '## Mutations',
  '',
  'No mutations run: this version of the review runs none.',
  '',
  '## Outstanding',
  '',
  ...(v.outstanding.length ? v.outstanding.map((o) => `- ${o}`) : ['- Nothing.']),
  '',
  record,
].join('\n')

// ---------------------------------------------------------------- one pull request
const review = async (n) => {
  const res = {
    number: n, head: '', base: '', verdict: '', outcome: 'failed', stage: '', recordUrl: '',
    findings: { verified: [], suspected: [] }, mutations: [], skippedBecauseRecorded: false,
    headMoved: false, earlierRecords: [], commentFile: '',
  }
  const inv = await run(inventoryPrompt(n), { label: `inventory:${n}`, phase: 'Inventory', schema: INVENTORY })
  if (!inv || inv.error) { res.stage = 'inventory' + (inv ? ': ' + inv.error : ''); return res }
  if (!/^[0-9a-f]{40}$/.test(inv.head) || !/^[0-9a-f]{40}$/.test(inv.forkPoint)) { res.stage = 'inventory: head or fork point is not a full sha'; return res }
  res.head = inv.head
  res.base = inv.forkPoint
  const records = recordsIn(inv)
  const onHead = records.filter((r) => r.head === inv.head)
  res.earlierRecords = records.filter((r) => r.head !== inv.head).map((r) => r.url)
  if (SKIP_RECORDED && onHead.length) {
    const latest = onHead[onHead.length - 1]
    log(`#${n}: ${inv.head} already carries a record (${latest.verdict}): ${latest.url}; skipped`)
    return { ...res, outcome: 'skipped', stage: '', verdict: latest.verdict, recordUrl: latest.url, skippedBecauseRecorded: true }
  }

  const [standards, spec] = await parallel([
    () => run(axisPrompt('Standards', n, inv), { label: `standards:${n}`, phase: 'Review', schema: AXIS }),
    () => run(axisPrompt('Spec', n, inv), { label: `spec:${n}`, phase: 'Review', schema: AXIS }),
  ])
  const axes = { standards, spec, missing: [standards ? '' : 'Standards', spec ? '' : 'Spec'].filter(Boolean) }

  const v = await run(verdictPrompt(n, inv, axes), { label: `verdict:${n}`, phase: 'Verdict', schema: VERDICT })
  if (!v) { res.stage = 'verdict'; return res }
  const verdict = axes.missing.length ? 'hold' : v.verdict
  const record = recordLine(inv.head, inv.forkPoint, verdict)
  const body = commentBody(inv, v, axes, record)
  res.verdict = verdict
  res.findings = {
    verified: v.findings.filter((f) => f.status === 'verified'),
    suspected: v.findings.filter((f) => f.status === 'suspected'),
  }

  const post = await run(postPrompt(n, record, body), { label: `post:${n}`, phase: 'Verdict', schema: POST })
  res.commentFile = `${DIR(n)}/comment.md`
  if (!post) { res.stage = 'post'; return res }
  res.headMoved = post.headNow !== inv.head
  if (post.unreachable) return { ...res, outcome: 'unposted', stage: 'post: GitHub unreachable; the body is in ' + res.commentFile }
  return { ...res, outcome: 'recorded', stage: '', recordUrl: post.url }
}

// ---------------------------------------------------------------- run
phase('Inventory')
log(`review-queue ${DATE}: ${PRS.length} pull request(s): ${PRS.map((n) => '#' + n).join(', ')}; log ${LOG}`)
const results = await parallel(PRS.map((n) => () => review(n)))
const prs = results.map((r, i) => r || { number: PRS[i], head: '', base: '', verdict: '', outcome: 'failed', stage: 'unknown', recordUrl: '', findings: { verified: [], suspected: [] }, mutations: [], skippedBecauseRecorded: false, headMoved: false, earlierRecords: [], commentFile: '' })
for (const r of prs) log(`#${r.number}: ${r.outcome}${r.verdict ? ' ' + r.verdict : ''}${r.stage ? ' (' + r.stage + ')' : ''}${r.recordUrl ? ' ' + r.recordUrl : ''}`)
return { date: DATE, logDir: LOG, agents: agentsRun, prs }
