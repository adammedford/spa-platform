export const meta = {
  name: 'review-queue',
  description: 'Review each named open pull request into main on the Standards and Spec axes, and post one comment ending in its review record',
  whenToUse: 'An integrator session, in the foreground, names open pull requests into main that need a review record before tools/land.sh merges them',
  phases: [
    { title: 'Inventory', detail: 'one agent per pull request reads head, fork point, files, owner set, CI, citations and the records already posted; it judges nothing' },
    { title: 'Review', detail: 'two fresh agents per pull request, Standards and Spec, read the branch through origin and rebuild nothing' },
    { title: 'Mutate', detail: 'one agent per mutation, up to the cap, breaks what a test guards in a throwaway worktree, runs that test\'s project, and removes the worktree' },
    { title: 'Verdict', detail: 'one agent per pull request re-takes every severity and says merge or hold, held to the mutation rows; one comment is posted, its last line the record' },
  ],
}

// ---------------------------------------------------------------- what this is
// The review-queue workflow of the spec #79. For each pull request it is given it inventories, reviews
// on both axes, mutates the tests the diff adds or changes, decides, and posts exactly one comment
// whose last line is the review record docs/agents/review.md -> "The review record" defines. It never
// merges, pushes, tags, labels, assigns or edits a branch. Every build or test run happens in a
// throwaway worktree under wt, detached at the head and removed after the run. Test companion:
// review-queue_tests.mjs beside it.
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
//   prs           required  a non-empty array of pull request numbers into main. The script reads no
//                           queue of its own: it reviews exactly the pull requests it is named (#83).
//   maxParallel   optional  positive integer, default 3: agents running at once.
//   maxMutationsPerPr optional positive integer, default 3: mutation agents per pull request. The
//                           list is cut here, and the comment names what the cap left unmutated.
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
  if (a.maxMutationsPerPr !== undefined && !count(a.maxMutationsPerPr)) bad.push('maxMutationsPerPr: a positive integer (default 3)')
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
const MUTATION_CAP = A.maxMutationsPerPr || 3
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
//
// "Line" and "stripped" are Python's, because the reader splits a body with str.splitlines() and
// strips each line with str.strip(): https://docs.python.org/3/library/stdtypes.html#str.splitlines
// and #str.strip. JavaScript's /\r?\n/ and trim() differ from both. trim() strips U+FEFF, which
// isspace() does not, so a BOM-prefixed line would skip a head the reader refuses to clear; and
// neither treats \x1c-\x1f or \x85 as Python does, nor splits at \r, \v, \f, \x1c-\x1e, \x85, U+2028
// or U+2029. The two classes below are those sets as python3 3.9 enumerates them over every code
// point, and the companion's reader check runs tools/review_record.py itself to hold them to it.
const PY_LINE_BREAK = /\r\n|[\n\v\f\r\x1c-\x1e\x85\u2028\u2029]/
const PY_SPACE = '[\\t\\n\\v\\f\\r\\x1c-\\x1f \\x85\\xa0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000]'
const PY_STRIP = new RegExp(`^${PY_SPACE}+|${PY_SPACE}+$`, 'g')
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
    for (const raw of entry.body.split(PY_LINE_BREAK)) {
      const m = RECORD.exec(raw.replace(PY_STRIP, ''))
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
- Write each OUTPUT FILE line's path exactly as given, and no other file outside a throwaway worktree under ${WTROOT}.`

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
// A candidate invariant test: an xUnit fact or theory, a Terraform test file or a Python test
// companion the diff adds or changes. project is what runs it alone: the .NET test project, the
// Terraform test directory, or the companion's path. breaks is the source it would take to break it.
const CANDIDATE = {
  type: 'object',
  properties: { test: { type: 'string' }, project: { type: 'string' }, guards: { type: 'string' }, breaks: { type: 'string' } },
  required: ['test', 'project', 'guards', 'breaks'],
}
// A mutation the pull request's body states, quoted, with its counts (-1 when the body gives none).
const STATED = {
  type: 'object',
  properties: {
    test: { type: 'string' }, project: { type: 'string' }, mutation: { type: 'string' },
    failed: { type: 'integer' }, total: { type: 'integer' }, quote: { type: 'string' },
  },
  required: ['test', 'project', 'mutation', 'failed', 'total', 'quote'],
}
const NOMINATION = {
  type: 'object',
  properties: { test: { type: 'string' }, project: { type: 'string' }, why: { type: 'string' } },
  required: ['test', 'project', 'why'],
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
    statedMutations: { type: 'array', items: STATED },
    candidates: { type: 'array', items: CANDIDATE },
    comments: { type: 'array', items: ENTRY },
    reviews: { type: 'array', items: ENTRY },
    error: { type: 'string' },
  },
  required: ['number', 'title', 'author', 'branch', 'baseRef', 'head', 'baseTip', 'forkPoint', 'commits', 'changedFiles', 'ownership', 'ci', 'cites', 'bodyClaims', 'statedMutations', 'candidates', 'comments', 'reviews', 'error'],
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
    nominations: { type: 'array', items: NOMINATION },
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
const RESULTS = ['reproduced', 'did-not-fail', 'could-not-run']
const MUTATION = {
  type: 'object',
  properties: {
    test: { type: 'string' }, broken: { type: 'string' },
    failed: { type: 'integer' }, total: { type: 'integer' },
    failingNames: { type: 'array', items: { type: 'string' } },
    result: { type: 'string', enum: RESULTS }, why: { type: 'string' },
  },
  required: ['test', 'broken', 'failed', 'total', 'failingNames', 'result', 'why'],
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
6. cites: the plan row id, the issue numbers and the merge subject the body proposes (empty when absent). bodyClaims: every claim the body makes about what it did or proved, quoted, mutation statements with their counts included. statedMutations: one entry per mutation the body states: the test it names, the project that runs it, the mutation as stated, failed and total as the body gives them (-1 for a count it does not give), and the sentence quoted.
6b. candidates: every candidate invariant test the diff adds or changes: each xUnit fact or theory (test = its class and method name, project = its test project's directory), each Terraform test file (project = its directory), each Python test companion (project = its path). guards: one sentence on what it guards. breaks: the source file it would take to break that, never the test itself.
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
${axis === 'Standards' ? 'Each nomination: test (by the name the inventory\'s candidates use, if it is one), project (what runs it alone), and why you suspect it cannot fail. ' : ''}Each finding: axis "${axis}", what and where, what you read or ran to check it, verified or suspected, and an advisory severity the verdict re-takes. List every source you read in sourcesRead. Write the result as JSON to the OUTPUT FILE, then return it.`

const verdictPrompt = (n, inv, axes, mut) => `Decide the review of pull request #${n}: ${inv.title}. You wrote none of its commits.

${HOUSE}

OUTPUT FILE: ${DIR(n)}/verdict.json

${AXIS_GUIDE} Its "The bar for a finding" and "Landing or holding" decide.

Read ${DIR(n)}/inventory.json, ${DIR(n)}/standards.json and ${DIR(n)}/spec.json. ${axes.missing.length ? `The ${axes.missing.join(' and ')} axis returned nothing: say so in summary, and hold.` : ''}
Take every finding's severity for yourself: a sub-agent's "looks wrong" is sometimes the contract's own design. You may run one further targeted check in a throwaway worktree, never the solution. Mark each finding verified or suspected and say how it was checked. Carry the author's claims with whether each was verified and how.
The mutation rows, each output file under ${DIR(n)}/ (first line the head, last line the result):
${mut.rows.length ? mut.rows.map((r) => `- ${r.test} (${r.kind}): ${r.result}, ${r.failed} of ${r.total} failed. ${r.why}`).join('\n') : '- None ran.'}${mut.cut.length ? `\nThe cap (${MUTATION_CAP}) left unmutated: ${mut.cut.join(', ')}.` : ''}
${mut.holds.length ? `The rows already hold the verdict: ${mut.holds.join('; ')}. Say so in summary.` : ''}
verdict is hold when a verified finding shows the change does not do what it says, or a test that proves it cannot fail: a did-not-fail row, or a stated mutation that did not reproduce. A could-not-run on a stated or nominated mutation holds too; one on a candidate nobody suspected is reported and does not. Otherwise merge. outstanding lists what stays for the merge body's Outstanding: line. Write the result as JSON to the OUTPUT FILE, then return it.`

const mutationWorktree = (n, i) => `${WTROOT}/pr-${n}-mutation-${i}`
const mutationLog = (n, i) => `${DIR(n)}/mutation-${i}.txt`
const mutatePrompt = (n, inv, m, i) => {
  const wt = mutationWorktree(n, i)
  return `Run one mutation for the review of pull request #${n}: break what one test guards, and see whether the test goes red. You wrote none of its commits.

${HOUSE}

OUTPUT FILE: ${mutationLog(n, i)}
WORKTREE: ${wt}
TEST: ${m.test}

The test: ${m.test}, run by ${m.project}. ${m.kind === 'stated'
    ? `The author states this mutation; apply it exactly as stated: ${m.mutation}. The body says: ${JSON.stringify(m.quote)}${m.failed >= 0 ? ` (failed ${m.failed}${m.total >= 0 ? ` of ${m.total}` : ''})` : ''}.`
    : `${m.kind === 'nominated' ? `The Standards axis suspects it cannot fail: ${m.why}. ` : ''}It guards: ${m.guards || '(say what it guards, from reading it)'}. Break that in the source it guards (${m.breaks || 'find it'}), never in the test, with the smallest change that should turn it red.`}

1. Write \`HEAD=${inv.head}\` as the OUTPUT FILE's first line. If ${wt} already exists, an earlier run left it: \`git -C ${REPO} worktree remove --force ${wt}\`, then \`git -C ${REPO} worktree prune\`.
2. \`git -C ${REPO} worktree add --detach ${wt} ${inv.head}\`. If the worktree cannot be made, the result is could-not-run with why.
3. Make the mutation in ${wt} only. Run only ${m.project} there: a .NET test project with \`dotnet test --project <it>\`, a Terraform test directory with \`terraform test\` in it, a Python companion with \`python3 -I <it>\`. Never the solution. Append the runner's output to the OUTPUT FILE as it prints it; never add a credential, token, cookie, code or state value to it.
4. failed and total: the counts the runner printed; failingNames: the tests it named as failing. result: reproduced when ${m.test} went red, did-not-fail when the project passed with the mutation in place, could-not-run when it could not be judged (the project does not build at the head, a tool is absent, the test was not found, the run timed out, the worktree could not be made), with why. broken: what you broke, in one line.
5. Always, whatever happened above: \`git -C ${REPO} worktree remove --force ${wt}\` and \`git -C ${REPO} worktree prune\`.
6. Write \`RESULT: <result>\` as the OUTPUT FILE's last line, then return the result.`
}

// The mutation list: the body's stated mutations, then the Standards axis's nominations, then the
// inventory's remaining candidates, deduplicated by test and cut at the cap. kind decides the hold
// rule a could-not-run row falls under.
const mutationList = (inv, standards) => {
  const byTest = new Map((inv.candidates || []).map((c) => [c.test, c]))
  const all = [
    ...(inv.statedMutations || []).map((s) => ({ ...byTest.get(s.test), ...s, kind: 'stated' })),
    ...((standards && standards.nominations) || []).map((s) => ({ ...byTest.get(s.test), ...s, kind: 'nominated' })),
    ...(inv.candidates || []).map((c) => ({ ...c, kind: 'candidate' })),
  ]
  const seen = new Set()
  const list = all.filter((m) => m.test && !seen.has(m.test) && seen.add(m.test))
  return { list: list.slice(0, MUTATION_CAP), cut: list.slice(MUTATION_CAP).map((m) => m.test) }
}

// One row per mutation. An agent that dies, returns nothing or returns a result that is none of the
// three is a could-not-run row, so a dead agent never reads as a pass and the verdict still runs.
const mutationRow = (n, m, i, got) => {
  const ok = got && RESULTS.includes(got.result)
  const row = {
    test: m.test, kind: m.kind, guards: m.guards || m.why || '', mutation: m.mutation || '',
    broken: (ok && got.broken) || m.mutation || '', failed: ok && Number.isInteger(got.failed) ? got.failed : 0,
    total: ok && Number.isInteger(got.total) ? got.total : 0, failingNames: (ok && got.failingNames) || [],
    result: ok ? got.result : 'could-not-run',
    why: ok ? (got.why || '') : (got ? `the mutation agent returned the result ${JSON.stringify(got.result)}, which is none of ${RESULTS.join(', ')}` : 'the mutation agent died or returned nothing'),
    countsMatched: null, logFile: mutationLog(n, i),
  }
  // -1 is a count the body does not give (STATED): compare only the counts the author stated.
  if (m.kind === 'stated' && row.result === 'reproduced' && m.failed >= 0) row.countsMatched = row.failed === m.failed && (m.total < 0 || row.total === m.total)
  return row
}
// What in the rows holds the verdict, each named.
const mutationHolds = (rows) => rows.flatMap((r) => {
  if (r.result === 'did-not-fail') return [`${r.test} did not fail with ${r.broken || 'what it guards'} broken`]
  if (r.kind === 'stated' && r.result !== 'reproduced') return [`the stated mutation of ${r.test} did not reproduce (${r.result})`]
  if (r.kind === 'nominated' && r.result === 'could-not-run') return [`the nominated mutation of ${r.test} could not run: ${r.why}`]
  return []
})

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
const cell = (s) => String(s).replace(/[\r\n]+/g, ' ').replace(/\|/g, '\\|')
const mutationLines = (mut) => [
  ...(mut.rows.length
    ? ['| Test | What was broken | Failed of total | Result | Log file |', '| --- | --- | --- | --- | --- |',
      ...mut.rows.map((r) => `| ${[r.test, r.broken || '-', `${r.failed} of ${r.total}`,
        r.result + (r.countsMatched === true ? ', counts match the stated claim' : r.countsMatched === false ? ', counts differ from the stated claim' : '') + (r.why ? `: ${r.why}` : ''),
        r.logFile].map(cell).join(' | ')} |`)]
    : ['No mutations run: the diff adds or changes no candidate invariant test and the body states no mutation.']),
  '',
  mut.cut.length ? `Not mutated, cut by the cap of ${MUTATION_CAP}: ${mut.cut.join(', ')}.` : `Not mutated: nothing; the cap of ${MUTATION_CAP} cut nothing.`,
]
const commentBody = (inv, v, axes, record, mut) => [
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
  ...mutationLines(mut),
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
    findings: { verified: [], suspected: [] }, mutations: [], notMutated: [], skippedBecauseRecorded: false,
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
    // The latest record decides, and a hold among records sharing the latest instant holds, as the
    // reader's docstring says (tools/review_record.py, "The latest record naming the head decides").
    const last = onHead[onHead.length - 1]
    const latest = onHead.find((r) => r.at === last.at && r.verdict === 'hold') || last
    log(`#${n}: ${inv.head} already carries a record (${latest.verdict}): ${latest.url}; skipped`)
    return { ...res, outcome: 'skipped', stage: '', verdict: latest.verdict, recordUrl: latest.url, skippedBecauseRecorded: true }
  }

  const [standards, spec] = await parallel([
    () => run(axisPrompt('Standards', n, inv), { label: `standards:${n}`, phase: 'Review', schema: AXIS }),
    () => run(axisPrompt('Spec', n, inv), { label: `spec:${n}`, phase: 'Review', schema: AXIS }),
  ])
  const axes = { standards, spec, missing: [standards ? '' : 'Standards', spec ? '' : 'Spec'].filter(Boolean) }

  const { list, cut } = mutationList(inv, standards)
  const got = await parallel(list.map((m, i) => async () => {
    try { return await run(mutatePrompt(n, inv, m, i + 1), { label: `mutate:${n}:${i + 1}`, phase: 'Mutate', schema: MUTATION }) } catch (e) { return null }
  }))
  const rows = list.map((m, i) => mutationRow(n, m, i + 1, got && got[i]))
  const mut = { rows, cut, holds: mutationHolds(rows) }
  res.mutations = rows
  res.notMutated = cut

  const v = await run(verdictPrompt(n, inv, axes, mut), { label: `verdict:${n}`, phase: 'Verdict', schema: VERDICT })
  if (!v) { res.stage = 'verdict'; return res }
  // The script holds on what it can judge itself: an axis that returned nothing, or a mutation row
  // under the hold rules. The verdict agent can hold on more, never on less.
  const verdict = axes.missing.length || mut.holds.length ? 'hold' : v.verdict
  const record = recordLine(inv.head, inv.forkPoint, verdict)
  const body = commentBody(inv, v, axes, record, mut)
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
const prs = results.map((r, i) => r || { number: PRS[i], head: '', base: '', verdict: '', outcome: 'failed', stage: 'unknown', recordUrl: '', findings: { verified: [], suspected: [] }, mutations: [], notMutated: [], skippedBecauseRecorded: false, headMoved: false, earlierRecords: [], commentFile: '' })
for (const r of prs) log(`#${r.number}: ${r.outcome}${r.verdict ? ' ' + r.verdict : ''}${r.stage ? ' (' + r.stage + ')' : ''}${r.recordUrl ? ' ' + r.recordUrl : ''}`)
return { date: DATE, logDir: LOG, agents: agentsRun, prs }
