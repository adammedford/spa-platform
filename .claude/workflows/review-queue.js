export const meta = {
  name: 'review-queue',
  description: 'Review every open pull request into main, or a named list, on the Standards and Spec axes, and post one comment per pull request ending in its review record',
  whenToUse: 'An integrator session, in the foreground, wants the open pull requests into main, or the ones it names, to carry a review record before tools/land.sh merges them',
  phases: [
    { title: 'Inventory', detail: 'with no list, one queue agent lists the open pull requests into main; then one agent per pull request reads head, fork point, files, owner set, CI, citations and the records already posted; it judges nothing. When the latest record names another head, one range agent says whether it is an ancestor and what the commits since it touch, or, for a rebuilt head, which new commits have the tree of an old one' },
    { title: 'Review', detail: 'two fresh agents per pull request, Standards and Spec, read the branch through origin and rebuild nothing' },
    { title: 'Mutate', detail: 'one agent per mutation, up to the cap, breaks what a test guards in a throwaway worktree, runs that test\'s project, and removes the worktree' },
    { title: 'Verdict', detail: 'one agent per pull request re-takes the severity of every finding the axes report and says merge or hold, held to the mutation rows; one comment is posted, its last line the record' },
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
// Leave prs out to review the open queue: every open pull request into main (#86).
// Arguments. Every required one is required because the script can derive none of them: it has no
// clock, environment or filesystem.
//   repo          required  the main checkout, an absolute path. Read-only apart from the run's log.
//   date          required  YYYY-MM-DD. Names the run's log directory.
//   wt            required  the root for throwaway worktrees, an absolute path outside repo.
//   prs           optional  a non-empty array of pull request numbers into main, reviewed exactly. Left
//                           out, one queue agent lists every open pull request whose base is main and
//                           the run reviews those (#86); a queue of zero returns an empty list and
//                           posts nothing. An empty array is refused as a bad launch, since it names
//                           nothing and is not the absent list that asks for the queue.
//   maxParallel   optional  positive integer, default 3: agents running at once.
//   maxMutationsPerPr optional positive integer, default 3: mutation agents per pull request. The
//                           list is cut here, and the comment names what the cap left unmutated.
//   skipRecorded  optional  boolean, default true. A head that already carries a record of either
//                           verdict is skipped and reported; false reviews it again and posts a new
//                           record, which is how a hold answered without a new commit becomes merge.
//                           It is the one way a head gets a second record (#85).
//
// The agent budget (#86). Every agent runs through one set of maxParallel slots, so the pull
// requests' pipelines (inventory, axes, mutations, verdict, post) overlap and one pull request's
// mutations start while another is still under review. A run starts at most one queue agent (none
// with a list), then per pull request one inventory, one range agent when an earlier head carries a
// record, two axes, up to maxMutationsPerPr mutations, one verdict and one post: 6 + the cap each,
// 9 with the defaults. The return's agents is the count that ran, beside the date, the base tip the
// run saw and the log directory. A wide queue is kept under a session's guideline by naming pull
// requests or lowering the cap.
//
// A delta review (#85): when the latest record names an ancestor of the head, the axes review the
// commits since that head with the whole diff for context, each merge in the range audited on its
// own; only the candidates the range touches are mutated; a held record's findings are checked for
// an answer in the range; the comment names the earlier record and the range, and the new record
// names the current head.
//
// A rebuilt head (#293): when the latest record's head is no ancestor, the range agent maps the
// recorded head's commits onto the new ones with `git range-diff` and reports, for each new commit,
// the old one whose tree it equals, or none. range-diff's own `=` means an equal patch, which a
// rebase onto a moved main keeps while every tree changes, so the pairing is range-diff's and the
// equality is the trees' (https://git-scm.com/docs/git-range-diff: `=` marks commits whose patches
// are the same; range-diff also prints abbreviated shas, so the agent expands each pair with
// rev-parse, and rebuildOf takes only a full sha). When at least one new commit maps, the review
// is a delta over the ones that do not: the axes read each of them, and the content diff
// `git diff <recorded> <head>`, which needs no ancestry; the comment says it was a rebuild and
// names the mapping. On #287 each rebuild
// of the run branch cost a full review, about 700k to 860k subagent tokens and 17 to 18 minutes,
// though most trees were unchanged. A rebuild that maps no commit, a mapping that leaves out a
// commit of the head or names no sha, a force-push, or a range nobody could read is a full review,
// and the comment says which.
//
// A review after a hold (#289), full or delta: the tests the earlier records' Mutations tables list
// as did-not-fail, and no later record reproduced, are mutated first, with the break the record named
// and under the stated mutation's hold rule, since a rebuilt branch is no descendant of the held
// head and a full review would otherwise put them back in inventory order. A held test the cap cuts
// is still unanswered, so it holds the verdict as well as being named in the cap's line. The candidates a `Review: ...` commit touches come
// next among the candidates. A candidate an earlier record reproduced is not mutated again while
// neither its test file nor the source it breaks has changed since that record's head; the comment
// names it as cleared by that record, beside the cap's cut.
//
// A held test whose break the branch has since deleted (#334: a dead `fail` branch removed instead of
// tested). There is nothing left to break, so the row is could-not-run, which holds, and a fix that
// deletes unreachable code could never be answered. The mutation agent may report removed for a held
// mutation only, proving it with the commit `git log -S` names; the script then holds nothing on the
// row, and the verdict agent holds unless it shows the removed code could never run or is enforced
// elsewhere. A removed row answers the held test in later reviews as a reproduced one does; on a
// stated, nominated or candidate mutation it is a could-not-run.
//
// Follow-ups (#292). A finding the review carries rather than holds on lived only in records: #287's
// three records each carried three with no ticket. The verdict agent names, for every carried finding,
// the open ticket that owns it or none, and the earliest record on the pull request that carried it;
// the ones with none are returned as followUps and end the comment's findings as the table under
// "## Follow-ups", which morning.js reads from the latest record and files. The review still writes
// nothing outside its log and its one comment.
//
// Reading a review at a glance (#294). A description whose stated head is not the head is a verified
// Spec finding reached by comparison, not by an axis's reading: the inventory reports each sha the
// description gives as the head, and the script compares it with the head it read (#287's second
// review found the description still naming ed8d063 after a rebuild). The return leads with summary,
// one line per pull request with its number, head, verdict, record URL, verified finding and mutation
// counts and the did-not-fail rows named, because the completion notice cuts a return short and on
// #287's runs the verdict and the mutation rows were past the cut. A halted run's summary is [].
//
// Posting (#85). The post agent re-reads the head first; a head that moved is still posted, because
// the record names the head the inventory read, and the return flags it (per pull request and in
// the run-level headMoved list) so the operator runs again. GitHub unreachable at posting time is
// outcome unposted: the body is in the pull request's comment.md under the log, the return names
// that file, and nothing is retried. Before posting, the agent looks for this run's record among the
// comments the inventory did not read, so a resumed run never posts twice and an earlier run's
// identical line never stops a skipRecorded-off re-run.
const A = args || {}
const badArguments = (a) => {
  const bad = []
  const abs = (p) => typeof p === 'string' && p.startsWith('/') && p.length > 1
  const count = (v) => Number.isInteger(v) && v > 0
  if (!abs(a.repo)) bad.push('repo: the main checkout as an absolute path, e.g. /Users/adammedford/Projects/spa-platform')
  if (typeof a.date !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(a.date)) bad.push('date: YYYY-MM-DD, which names the run\'s log directory')
  if (!abs(a.wt)) bad.push('wt: the root for throwaway worktrees, an absolute path outside repo')
  else if (abs(a.repo) && (a.wt === a.repo || a.wt.startsWith(a.repo.replace(/\/+$/, '') + '/'))) bad.push('wt: must lie outside repo; no worktree is made under the repository')
  if (a.prs !== undefined && (!Array.isArray(a.prs) || a.prs.length === 0 || !a.prs.every(count))) bad.push('prs: a non-empty array of pull request numbers, e.g. [255], or left out for every open pull request into main')
  if (a.maxParallel !== undefined && !count(a.maxParallel)) bad.push('maxParallel: a positive integer (default 3)')
  if (a.maxMutationsPerPr !== undefined && !count(a.maxMutationsPerPr)) bad.push('maxMutationsPerPr: a positive integer (default 3)')
  if (a.skipRecorded !== undefined && typeof a.skipRecorded !== 'boolean') bad.push('skipRecorded: true or false, a boolean (default true)')
  return bad
}
const BAD_LAUNCH = badArguments(A)
if (BAD_LAUNCH.length) {
  // Stops here: no agent is started, nothing is read or written.
  return { summary: [], halted: 'bad launch; no agent started. Fix each argument and relaunch with all of them:\n- ' + BAD_LAUNCH.join('\n- ') }
}
const REPO = A.repo.replace(/\/+$/, '')
const DATE = A.date
const WTROOT = A.wt.replace(/\/+$/, '')
// The named list, or null when the open queue decides (#86).
const NAMED = A.prs === undefined ? null : [...new Set(A.prs)]
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
      if (m) found.push({ head: m[1], base: m[2], verdict: m[3], url: entry.html_url || '', at: entry[at] || '', body: entry.body })
    }
  }
  for (const c of inv.comments || []) read(c, 'created_at')
  for (const r of inv.reviews || []) if (r && r.state === 'COMMENTED') read(r, 'submitted_at')
  return found.sort((x, y) => (x.at < y.at ? -1 : x.at > y.at ? 1 : 0))
}
// The rows of a review comment's Mutations table, as mutationLines below writes it (#289): one
// { test, broken, result } per row, each cell unescaped as cell() escaped it, result the leading word
// of the Result cell. A comment with no such table, a human's or an older shape's, yields none.
const MUTATION_HEAD = '| Test | What was broken | Failed of total | Result | Log file |'
const mutationTableOf = (body) => {
  const lines = String(body || '').split(PY_LINE_BREAK).map((l) => l.trim())
  const at = lines.indexOf(MUTATION_HEAD)
  if (at < 0) return []
  const rows = []
  for (const line of lines.slice(at + 2)) {
    if (!line.startsWith('|') || !line.endsWith('|')) break
    const cells = line.slice(1, -1).split(/(?<!\\)\|/).map((c) => c.trim().replace(/\\\|/g, '|'))
    const result = cells.length === 5 && /^(reproduced|did-not-fail|could-not-run|removed)\b/.exec(cells[3])
    if (result && cells[0]) rows.push({ test: cells[0], broken: cells[1], result: result[1] })
  }
  return rows
}
// Whether a row's Test cell names a test: the cell is the name, or holds it whole, as #287's first
// record held two LibraryTests cases in one cell.
const names = (cellText, test) => !!test && new RegExp(`(?<![\\w.])${test.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?!\\w)`).test(cellText)

// The follow-ups table (#292): the carried findings that name no open ticket, which the comment ends
// its findings with and the morning run files. One row per finding, its First carried by cell the
// earliest record on the pull request that carried it, or THIS_RECORD for the record the comment
// ends in. morning.js reads the same table with its own copy of FOLLOW_UP_HEAD, THIS_RECORD and this
// parser, since a workflow script imports nothing; morning_tests.mjs holds the two copies equal, so
// a change to the table is an edit to both scripts together. followUpsOf(body, url) reads a comment's
// table back, THIS_RECORD resolved to url, the comment's own address; a comment with no table yields
// none.
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
// The verdict's carried findings that name no open ticket, each with the earliest record on the pull
// request that carried it: the earliest of the record the verdict names and every earlier record whose
// own follow-ups table lists the finding by title. A URL that is no record here is not trusted, and
// a finding no record carried before is this record's, "" until it is posted.
const followUpsFrom = (carried, records) => {
  const urls = records.map((r) => r.url).filter(Boolean)
  // Only a "#<n>" names a ticket: "none" or "-" in its place loses no finding (a check fails closed).
  return (carried || []).filter((f) => f && f.title && !/^#\d+$/.test(String(f.ticket || '').trim())).map((f) => {
    const named = records.flatMap((r) => followUpsOf(r.body, r.url)).filter((x) => sameFinding(x.title, f.title)).map((x) => x.firstCarried)
    const first = [...named, f.firstCarried].filter((u) => urls.includes(u)).sort((a, b) => urls.indexOf(a) - urls.indexOf(b))[0] || ''
    return { axis: f.axis, title: f.title, where: f.where, firstCarried: first }
  })
}

// ---------------------------------------------------------------- agents and slots
let agentsRun = 0
let running = 0
const waiting = []
// A freed slot passes straight to the first waiter, and running drops only when nobody waits. A
// waiter that took its own slot on waking would leave a gap between the free and the take, in which
// another caller sees a slot open and takes it too: one over maxParallel (#86, review check 22).
const slot = async (fn) => {
  if (running >= MAX_PAR) await new Promise((resolve) => waiting.push(resolve))
  else running++
  try { return await fn() } finally {
    const next = waiting.shift()
    if (next) next()
    else running--
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
- To change an existing file with Edit or Write, open it with the Read tool first in this session, not with \`cat\`, \`head\` or \`sed -n\`: for a file you will change, this rule overrides the auto-mode advice to read with Bash. Open it with Read again after anything else wrote it (a script, the check, a merge, \`git\`). Otherwise make the change with a short script written to a file under your scratchpad and run by its path. Edit and Write refuse a file not read since its last write: 16 calls went to that refusal on 2026-10-18 and 16 over the four launches from 2026-10-19 to 2026-10-21 (#109).
- Write each OUTPUT FILE line's path exactly as given, and no other file outside a throwaway worktree under ${WTROOT} but a script the edit rule above has you write under your scratchpad.`

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
// Terraform test directory, or the companion's path. file is the file the test is written in, and
// breaks the source it would take to break it: a delta review mutates a candidate only when its
// range touches one of the two.
const CANDIDATE = {
  type: 'object',
  properties: { test: { type: 'string' }, project: { type: 'string' }, file: { type: 'string' }, guards: { type: 'string' }, breaks: { type: 'string' } },
  required: ['test', 'project', 'file', 'guards', 'breaks'],
}
// What the range agent reads for a delta review: whether the head the latest record names is an
// ancestor of the current head, and the commits, merges and files since it. changedFiles is read
// whether or not it is an ancestor, and since holds the files changed since each other recorded head
// the script asks about: a candidate an earlier record reproduced is cleared by them (#289). mapping
// is read when the head is no ancestor (#293): one entry per commit of the head since its fork point,
// equals the old commit range-diff pairs it with when their trees are equal, "" otherwise; merges
// then lists the merges among the head's commits, so an unmapped one is audited on its own.
const RANGE = {
  type: 'object',
  properties: {
    from: { type: 'string' }, ancestor: { type: 'boolean' },
    commits: { type: 'array', items: { type: 'string' } }, merges: { type: 'array', items: { type: 'string' } },
    changedFiles: { type: 'array', items: { type: 'string' } },
    since: { type: 'array', items: { type: 'object', properties: { head: { type: 'string' }, changedFiles: { type: 'array', items: { type: 'string' } }, error: { type: 'string' } }, required: ['head', 'changedFiles', 'error'] } },
    mapping: { type: 'array', items: { type: 'object', properties: { commit: { type: 'string' }, equals: { type: 'string' } }, required: ['commit', 'equals'] } },
    error: { type: 'string' },
  },
  required: ['from', 'ancestor', 'commits', 'merges', 'changedFiles', 'since', 'mapping', 'error'],
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
    // The commits since the fork point titled `Review: ...`, each with the files it touches (#289).
    reviewCommits: { type: 'array', items: { type: 'object', properties: { sha: { type: 'string' }, subject: { type: 'string' }, files: { type: 'array', items: { type: 'string' } } }, required: ['sha', 'subject', 'files'] } },
    ownership: { type: 'object', properties: { owners: { type: 'array', items: { type: 'string' } }, ok: { type: 'boolean' }, output: { type: 'string' } }, required: ['owners', 'ok', 'output'] },
    ci: { type: 'array', items: { type: 'object', properties: { name: { type: 'string' }, state: { type: 'string' } }, required: ['name', 'state'] } },
    cites: { type: 'object', properties: { planRow: { type: 'string' }, issues: { type: 'array', items: { type: 'integer' } }, mergeSubject: { type: 'string' } }, required: ['planRow', 'issues', 'mergeSubject'] },
    bodyClaims: { type: 'array', items: { type: 'string' } },
    // Every sha the description states as this pull request's head, as written, with its sentence (#294).
    statedHeads: { type: 'array', items: { type: 'object', properties: { sha: { type: 'string' }, quote: { type: 'string' } }, required: ['sha', 'quote'] } },
    statedMutations: { type: 'array', items: STATED },
    candidates: { type: 'array', items: CANDIDATE },
    comments: { type: 'array', items: ENTRY },
    reviews: { type: 'array', items: ENTRY },
    error: { type: 'string' },
  },
  required: ['number', 'title', 'author', 'branch', 'baseRef', 'head', 'baseTip', 'forkPoint', 'commits', 'changedFiles', 'reviewCommits', 'ownership', 'ci', 'cites', 'bodyClaims', 'statedHeads', 'statedMutations', 'candidates', 'comments', 'reviews', 'error'],
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
    // A delta review after a hold: each finding of the held record, whether that record held on it or
    // carried it as a follow-up, and whether the range answers it.
    earlierFindings: { type: 'array', items: { type: 'object', properties: { finding: { type: 'string' }, held: { type: 'boolean' }, answered: { type: 'boolean' }, how: { type: 'string' } }, required: ['finding', 'held', 'answered', 'how'] } },
    // Every finding the review carries rather than holds on (#292), with the open ticket that owns
    // it ("" for none) and the earliest record on this pull request that carried it ("" for none).
    followUps: { type: 'array', items: { type: 'object', properties: { axis: { type: 'string', enum: ['Standards', 'Spec'] }, title: { type: 'string' }, where: { type: 'string' }, ticket: { type: 'string' }, firstCarried: { type: 'string' } }, required: ['axis', 'title', 'where', 'ticket', 'firstCarried'] } },
  },
  required: ['verdict', 'findings', 'claims', 'outstanding', 'summary', 'earlierFindings', 'followUps'],
}
const RESULTS = ['reproduced', 'did-not-fail', 'could-not-run', 'removed']
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
// The open queue, as the queue agent lists it from gh: every open pull request whose base is main.
const QUEUE = {
  type: 'object',
  properties: {
    baseTip: { type: 'string' },
    prs: { type: 'array', items: { type: 'object', properties: { number: { type: 'integer' }, head: { type: 'string' }, baseRef: { type: 'string' } }, required: ['number', 'head', 'baseRef'] } },
    error: { type: 'string' },
  },
  required: ['baseTip', 'prs', 'error'],
}

// ---------------------------------------------------------------- prompts
const queuePrompt = () => `List the open queue for a review run: every open pull request whose base is main. Read and report; judge nothing.

${HOUSE}

OUTPUT FILE: ${LOG}/queue.json

1. \`git -C ${REPO} fetch origin --tags\`. baseTip = \`git -C ${REPO} rev-parse origin/main\`.
2. \`cd ${REPO} && gh pr list --state open --base main --limit 500 --json number,headRefOid,baseRefName\`. prs: one entry per pull request it lists, number, head = headRefOid (40 hex) and baseRef = baseRefName, in the order gh lists them. An empty list is an answer: prs is [].
3. If gh or git cannot be reached, set error to say which and leave prs []. error is "" when nothing stopped you.
4. Write the whole result as JSON to the OUTPUT FILE, then return it.`

const inventoryPrompt = (n) => `Inventory pull request #${n} for a review. Read and report; judge nothing.

${HOUSE}

OUTPUT FILE: ${DIR(n)}/inventory.json

1. \`git -C ${REPO} fetch origin --tags\`. Then \`cd ${REPO} && gh pr view ${n} --json number,title,author,headRefName,baseRefName,headRefOid,state,body,statusCheckRollup\`.
2. If the pull request is not OPEN or its base is not main, set error to say which, fill what you read, and stop.
3. head = headRefOid (40 hex). baseTip = \`git -C ${REPO} rev-parse origin/main\`. forkPoint = \`git -C ${REPO} merge-base origin/main <head>\`. commits = \`git -C ${REPO} log --format=%H <forkPoint>..<head>\`. changedFiles = \`git -C ${REPO} diff --name-only <forkPoint>...<head>\`.
3b. reviewCommits: from \`git -C ${REPO} log --format='%H%x09%s' <forkPoint>..<head>\`, one entry per commit whose subject starts \`Review:\`: sha, subject, and files = \`git -C ${REPO} diff-tree --no-commit-id --name-only -r <sha>\` (for a merge, \`git -C ${REPO} diff --name-only <sha>^1 <sha>\`). [] when none.
4. ownership: run \`cd ${REPO} && python3 -I .github/path-ownership.py <forkPoint> <head>\`; owners = the owner sets it names, ok = whether it exited 0, output = its last lines.
5. ci: one row per check of statusCheckRollup, state one of success, failure, pending, skipped.
6. cites: the plan row id, the issue numbers and the merge subject the body proposes (empty when absent). bodyClaims: every claim the body makes about what it did or proved, quoted, mutation statements with their counts included. statedMutations: one entry per mutation the body states: the test it names, the project that runs it, the mutation as stated, failed and total as the body gives them (-1 for a count it does not give), and the sentence quoted.
6a. statedHeads: one entry per sha the body gives as this pull request's head, the head it says it describes ("head \`76704c5\`"): sha exactly as written, full or abbreviated, and quote, the sentence that gives it. Leave out the fork point, the base, a listed commit, and a sha the body names as an earlier head or as history ("the earlier head \`ed8d063\`"). [] when the body gives none. Do not compare it with the head: the script does.
6b. candidates: every candidate invariant test the diff adds or changes: each xUnit fact or theory (test = its class and method name, project = its test project's directory), each Terraform test file (project = its directory), each Python test companion (project = its path). file: the repository path of the file the test is written in. guards: one sentence on what it guards. breaks: the source file it would take to break that, never the test itself.
7. comments: \`cd ${REPO} && gh api --paginate --slurp repos/{owner}/{repo}/issues/${n}/comments\`, flattened, each entry's body, author_association, html_url and created_at exactly as served. reviews: the same for \`repos/{owner}/{repo}/pulls/${n}/reviews\`, with body, state, author_association, html_url and submitted_at. Copy bodies byte for byte: the script reads review records from them.
8. Write the whole result as JSON to the OUTPUT FILE, then return it. error is "" when nothing stopped you.`

const rangePrompt = (n, inv, from, oldFork, sinceHeads = []) => `Read the range for a delta review of pull request #${n}: the commits since ${from}, the head its latest review record names. Read and report; judge nothing.

${HOUSE}

OUTPUT FILE: ${DIR(n)}/range.json

1. \`git -C ${REPO} fetch origin --tags\`. from = ${from}; the current head is ${inv.head}.
2. ancestor: whether \`git -C ${REPO} merge-base --is-ancestor ${from} ${inv.head}\` exits 0. If it exits 1, ancestor is false (a force-push or a rebuilt branch dropped the recorded head); leave commits empty. If ${from} cannot be read at all, ancestor is false and error says why.
3. When ancestor is true: commits = \`git -C ${REPO} log --format=%H ${from}..${inv.head}\`, merges = \`git -C ${REPO} log --merges --format=%H ${from}..${inv.head}\`, and mapping is [].
3b. When ancestor is false and ${from} can be read, map the recorded head's commits onto the new ones: \`git -C ${REPO} range-diff ${oldFork}..${from} ${inv.forkPoint}..${inv.head}\` (${oldFork} is the fork point the record names). mapping: one entry for each commit of \`git -C ${REPO} log --format=%H ${inv.forkPoint}..${inv.head}\`, merges included: commit is its sha, and equals is the full 40-hex sha (\`git -C ${REPO} rev-parse <old>\`, since range-diff prints abbreviated ones) of the old commit range-diff pairs it with when \`git -C ${REPO} rev-parse <old>^{tree}\` prints the same tree as \`git -C ${REPO} rev-parse <commit>^{tree}\`, else "". range-diff's \`=\` is an equal patch, not an equal tree, so compare the trees for every pair. A commit range-diff pairs with none, a merge among them, maps to the commit of ${oldFork}..${from} whose tree equals its own, if one does. merges = \`git -C ${REPO} log --merges --format=%H ${inv.forkPoint}..${inv.head}\`. If range-diff cannot run, error says why. Either way, when ${from} can be read: changedFiles = \`git -C ${REPO} diff --name-only ${from} ${inv.head}\`, which compares the two trees and needs no ancestry.
4. since: ${sinceHeads.length ? `one entry for each of these earlier recorded heads: ${sinceHeads.join(', ')}. head is the sha; changedFiles = \`git -C ${REPO} diff --name-only <head> ${inv.head}\`; error is "" or why that head cannot be read, with changedFiles [].` : '[].'}
5. Write the whole result as JSON to the OUTPUT FILE, then return it. error is "" when nothing stopped you.`

// What the axes and the verdict are told about a delta review, and what the comment says of it. A
// rebuild's delta is no range: from..to over a head that does not descend from the recorded one
// lists the whole branch, so it is named by its unmapped commits and read through the content diff.
const deltaRange = (d) => `${d.from}..${d.to}`
const deltaScope = (d) => (d.rebuilt ? `the unmapped commit(s) ${d.commits.join(', ') || '(none)'}` : `the range ${deltaRange(d)}`)
const mappingText = (d) => d.mapping.filter((m) => m.equals).map((m) => `${m.commit} = ${m.equals}`).join(', ')
const deltaBrief = (d) => `This is a delta review. The latest review record on this pull request, ${d.url}, names ${d.from} (${d.verdict}), ${d.rebuilt
    ? `which is no ancestor of the head: the branch was rebuilt. These new commits have the tree of an old one (new = old): ${mappingText(d)}; what the earlier record cleared there is not re-reviewed. Review the commit(s) that map onto none, ${d.commits.join(', ') || '(none: every commit maps)'}, each with \`git -C ${REPO} show <sha>\`, and the content diff from the recorded head, \`git -C ${REPO} diff ${d.from} ${d.to}\` (files: ${d.changedFiles.join(', ') || '(none)'}).`
    : `an ancestor of the head. Review the range ${deltaRange(d)}: \`git -C ${REPO} diff ${deltaRange(d)}\` and \`git -C ${REPO} log ${deltaRange(d)} --oneline\` (${d.commits.length} commit(s); files: ${d.changedFiles.join(', ') || '(none)'}).`} Read the whole diff, \`git -C ${REPO} diff ${d.fork}...${d.to}\`, for context only: what the earlier record cleared is not re-reviewed.${d.merges.length ? ` The range holds merge commit(s) ${d.merges.join(', ')}: audit each one's resolution on its own, with \`git -C ${REPO} show --remerge-diff <merge>\`, as the review guide requires.` : ''}${d.verdict === 'hold' ? ` The earlier record held: read ${d.url} and check each of its findings for an answer in ${d.rebuilt ? 'the content diff' : 'the range'}.` : ''}`

const axisPrompt = (axis, n, inv, d) => `You are the ${axis} axis of a review of pull request #${n}: ${inv.title}. You are a fresh agent and wrote none of its commits.

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

${d ? deltaBrief(d) : `Read the diff yourself with the review guide's commands (\`git -C ${REPO} diff ${inv.forkPoint}...${inv.head}\`).`} A diff with no code in it, docs-only or tracker-only, is reviewed all the same: no pull request is exempt by what it touches.
${axis === 'Standards'
    ? 'Hold the diff to CODING_STANDARDS.md, AGENTS.md, CLAUDE.md and OWNERSHIP.md; a path-ownership refusal or a diff outside one owner\'s set is a finding of its own. Name in nominations every assertion you suspect cannot fail, by file and test name.'
    : 'Hold the diff to the spec sources the review guide\'s table names: the plan row, the issue\'s body and comments, the contracts and pipeline table, the test and evidence ids the row names, change requests and ADRs. The pull request body is the author\'s claim, not the spec: put every claim in claims, verified or not, and how.'}
${axis === 'Standards' ? 'Each nomination: test (by the name the inventory\'s candidates use, if it is one), project (what runs it alone), and why you suspect it cannot fail. ' : ''}Each finding: axis "${axis}", what and where, what you read or ran to check it, verified or suspected, and an advisory severity the verdict re-takes. List every source you read in sourcesRead. Write the result as JSON to the OUTPUT FILE, then return it.`

const verdictPrompt = (n, inv, mut, d, stale = []) => `Decide the review of pull request #${n}: ${inv.title}. You wrote none of its commits.

${HOUSE}

OUTPUT FILE: ${DIR(n)}/verdict.json

${AXIS_GUIDE} Its "The bar for a finding" and "Landing or holding" decide.

Read ${DIR(n)}/inventory.json, ${DIR(n)}/standards.json and ${DIR(n)}/spec.json.
Take every finding's severity for yourself: a sub-agent's "looks wrong" is sometimes the contract's own design. You may run one further targeted check in a throwaway worktree, never the solution. Mark each finding verified or suspected and say how it was checked. Carry the author's claims with whether each was verified and how.
${stale.length ? `The script compared every head the description states with the head the inventory read, and these differ; each is a verified Spec finding the comment already carries, so weigh it in verdict and outstanding and do not list it again in findings:\n${stale.map((f) => `- ${f.title}. ${f.checked}`).join('\n')}\n` : ''}The mutation rows, each output file under ${DIR(n)}/ (first line the head, last line the result):
${mut.rows.length ? mut.rows.map((r) => `- ${r.test} (${r.kind}): ${r.result}, ${r.failed} of ${r.total} failed. ${r.why}`).join('\n') : '- None ran.'}${mut.rows.some((r) => r.kind === 'held') ? '\nA held row is a test an earlier record held on as did-not-fail and no later record reproduced, mutated again with the break that record named.' : ''}${mut.cut.length ? `\nThe cap (${MUTATION_CAP}) left unmutated: ${mut.cut.join(', ')}.` : ''}${mut.cleared.length ? `\nNot mutated, cleared by an earlier record that reproduced it, its test file and the source it breaks unchanged since that record's head: ${clearedText(mut.cleared)}.` : ''}
${mut.holds.length ? `The rows already hold the verdict: ${mut.holds.join('; ')}. Say so in summary.` : ''}
${d ? deltaBrief(d) + (d.verdict === 'hold' ? ' Put each of the held record\'s findings in earlierFindings: held true when that record held on it, false when it carried it as a follow-up that did not hold; answered true when the range answers it, false when it does not, and how. An unanswered finding the record held on holds, and so does an empty list; an unanswered follow-up does not, and goes in followUps.' : ' earlierFindings is [].') : 'earlierFindings is [].'}
verdict is hold when a verified finding shows the change does not do what it says, or a test that proves it cannot fail: a did-not-fail row, or a stated or held mutation that did not reproduce. A could-not-run on a stated, held or nominated mutation holds too; one on a candidate nobody suspected is reported and does not. A removed row is a held break whose target the range deleted, so it holds nothing by itself, but it answers the held test only if the deletion is sound: read the commit its why names, and it answers when the removed code could never run (say why) or the same check is still enforced elsewhere (say where), each as a verified finding; otherwise, or if you cannot show either, the held test is unanswered and the verdict is hold. Otherwise merge. outstanding lists what stays for the merge body's Outstanding: line.
followUps: every finding this review carries rather than holds on, whatever the verdict: a finding left standing for after the merge, and each outstanding entry that is a defect still to fix. Each: axis, title (one line; when an earlier record's "## Follow-ups" table lists the finding, its Finding cell verbatim), where, ticket and firstCarried. ticket is the open issue that owns the fix, as "#<n>", checked with \`cd ${REPO} && gh issue view <n> --json state,title\` (it must be OPEN and its title or body must be about this fix; an issue the finding merely mentions does not own it), or "" when there is no open ticket for it, which you search for with \`cd ${REPO} && gh issue list --state open --search "<key words> in:title,body" --json number,title\`. firstCarried is the earliest of the records on this pull request that carried the same finding, by its URL: ${(() => { const urls = recordsIn(inv).map((r) => r.url).filter(Boolean); return urls.length ? `read ${urls.join(', ')} (oldest first)` : 'there is none, so it is ""' })()}; "" when none did. The script keeps those with ticket "" as the comment's follow-ups, which the morning run files.
Write the result as JSON to the OUTPUT FILE, then return it.`

const mutationWorktree = (n, i) => `${WTROOT}/pr-${n}-mutation-${i}`
const mutationLog = (n, i) => `${DIR(n)}/mutation-${i}.txt`
const mutatePrompt = (n, inv, m, i) => {
  const wt = mutationWorktree(n, i)
  const project = m.project || 'the project that runs it alone (find it from its name)'
  return `Run one mutation for the review of pull request #${n}: break what one test guards, and see whether the test goes red. You wrote none of its commits.

${HOUSE}

OUTPUT FILE: ${mutationLog(n, i)}
WORKTREE: ${wt}
TEST: ${m.test}

The test: ${m.test}, run by ${project}. ${m.kind === 'stated'
    ? `The author states this mutation; apply it exactly as stated: ${m.mutation}. The body says: ${JSON.stringify(m.quote)}${m.failed >= 0 ? ` (failed ${m.failed}${m.total >= 0 ? ` of ${m.total}` : ''})` : ''}.`
    : m.kind === 'held'
    ? `The review record ${m.heldBy} held because this test did not fail with this broken: ${m.mutation}. Apply that break again, exactly as it says, never in the test${m.cell !== m.test ? ` (the record's row named ${JSON.stringify(m.cell)}; judge ${m.test} alone)` : ''}, and see whether the test goes red now.`
    : `${m.kind === 'nominated' ? `The Standards axis suspects it cannot fail: ${m.why}. ` : ''}It guards: ${m.guards || '(say what it guards, from reading it)'}. Break that in the source it guards (${m.breaks || 'find it'}), never in the test, with the smallest change that should turn it red.`}

1. Write \`HEAD=${inv.head}\` as the OUTPUT FILE's first line. If ${wt} already exists, an earlier run left it: \`git -C ${REPO} worktree remove --force ${wt}\`, then \`git -C ${REPO} worktree prune\`.
2. \`git -C ${REPO} worktree add --detach ${wt} ${inv.head}\`. If the worktree cannot be made, the result is could-not-run with why.
3. Make the mutation in ${wt} only. Run only ${project} there: a .NET test project with \`dotnet test --project <it>\`, a Terraform test directory with \`terraform test\` in it, a Python companion with \`python3 -I <it>\`. Never the solution. Append the runner's output to the OUTPUT FILE as it prints it; never add a credential, token, cookie, code or state value to it.
4. failed and total: the counts the runner printed; failingNames: the tests it named as failing. result: reproduced when ${m.test} went red, did-not-fail when the project passed with the mutation in place, could-not-run when it could not be judged (the project does not build at the head, a tool is absent, the test was not found, the run timed out, the worktree could not be made), with why.${m.kind === 'held' ? ` removed, for this held mutation only: the code the held break names no longer exists at the head, so there is nothing to break. Prove it before you say so: search the head for the quoted code, and \`git -C ${wt} log -S'<a distinctive string of it>' --oneline ${inv.forkPoint}..${inv.head}\` must name the commit that removed it; why names that commit and quotes what it removed. Code that moved or was renamed, a break you could not locate, or a test that fails to build is not removed: it is could-not-run.` : ''} broken: what you broke, in one line.
5. Always, whatever happened above: \`git -C ${REPO} worktree remove --force ${wt}\` and \`git -C ${REPO} worktree prune\`.
6. Write \`RESULT: <result>\` as the OUTPUT FILE's last line, then return the result.`
}

// What the earlier records say about this review's mutation list (#289). held: when the latest record
// held on another head, the did-not-fail rows of the Mutations tables of every record on another head,
// each with the record's url and the Test cells later records reproduced, which answer it. On #287 the
// latest record held for other reasons and its table had no did-not-fail row; the record before it
// held on two LibraryTests cases nobody mutated again, so the rows are carried forward until a later
// record reproduces them. clearers: newest first, each earlier record whose head the range agent
// diffed against this one, with its url, the files changed since its head, and the Test cells of the
// rows it reproduced. With no range read, or an empty diff read, nothing is known to be unchanged, so
// nothing clears: an agent that left a list empty must not clear what changed.
const priorOf = (records, inv, rg) => {
  const latest = records[records.length - 1]
  const other = records.filter((r) => r.head !== inv.head)
  const held = latest && latest.head !== inv.head && latest.verdict === 'hold'
    ? { url: latest.url, rows: other.flatMap((r, i) => mutationTableOf(r.body).filter((x) => x.result === 'did-not-fail').map((x) => ({
      ...x, url: r.url,
      later: other.slice(i + 1).flatMap((l) => mutationTableOf(l.body).filter((y) => y.result === 'reproduced' || y.result === 'removed').map((y) => y.test)),
    }))) }
    : null
  const changed = new Map()
  const known = (files) => Array.isArray(files) && files.length > 0
  if (rg && !rg.error && latest) {
    if (known(rg.changedFiles)) changed.set(latest.head, rg.changedFiles)
    for (const s of rg.since || []) if (s && !s.error && known(s.changedFiles) && !changed.has(s.head)) changed.set(s.head, s.changedFiles)
  }
  const clearers = records.filter((r) => r.head !== inv.head && changed.has(r.head)).reverse()
    .map((r) => ({ url: r.url, files: changed.get(r.head), reproduced: mutationTableOf(r.body).filter((x) => x.result === 'reproduced').map((x) => x.test) }))
  return { held, clearers }
}
// The earlier recorded heads, other than the latest's, whose records reproduced a test the inventory
// lists as a candidate: the heads the range agent is asked to diff for prior.clearers.
const sinceHeadsOf = (records, inv) => {
  const latest = records[records.length - 1]
  const heads = records.filter((r) => r.head !== inv.head && (!latest || r.head !== latest.head)
    && mutationTableOf(r.body).some((x) => x.result === 'reproduced' && (inv.candidates || []).some((c) => names(x.test, c.test))))
    .map((r) => r.head)
  return [...new Set(heads)]
}

// The mutation list: when the latest record held, first the tests the records list as did-not-fail
// and no later record reproduced (#289); then the body's stated mutations, then the Standards axis's
// nominations, then the candidates whose file or broken source a commit titled `Review: ...` since
// the fork point touches, then the
// inventory's remaining candidates; deduplicated by test and cut at the cap. A held row whose Test
// cell names candidates becomes one held mutation per candidate it names; one naming none is mutated
// by the cell itself, its project left for the mutation agent to find. kind decides the hold rule a
// row falls under; a held mutation falls under the stated one. In a delta review a stated mutation or
// a candidate whose file and broken source the range leaves alone was cleared by the earlier record
// and is listed as outside the range; a nomination comes from the range's own review and always runs,
// as does a held test and a stated mutation naming no file the inventory knows. A candidate an
// earlier record reproduced is cleared by that record, and not mutated, when neither its file nor its
// broken source changed since that record's head.
const mutationList = (inv, standards, d, prior = { held: null, clearers: [] }) => {
  const candidates = inv.candidates || []
  const byTest = new Map(candidates.map((c) => [c.test, c]))
  // Newest first, so a test two records held on is mutated by the latest one's break; one a later
  // record reproduced is answered and drops out.
  const held = prior.held ? [...prior.held.rows].reverse().flatMap((h) => {
    const base = { mutation: h.broken, quote: h.broken, failed: -1, total: -1, kind: 'held', heldBy: h.url, cell: h.test }
    const named = candidates.filter((c) => names(h.test, c.test))
    const answered = (test) => h.later.some((t) => t === test || names(t, test))
    return named.length ? named.filter((c) => !answered(c.test)).map((c) => ({ ...c, ...base }))
      : answered(h.test) ? [] : [{ test: h.test, project: '', file: '', guards: '', breaks: '', ...base }]
  }) : []
  const reviewed = new Set((inv.reviewCommits || []).filter((c) => c && /^Review:/.test(c.subject || '')).flatMap((c) => c.files || []))
  const touched = (c) => [c.file, c.breaks].some((f) => f && reviewed.has(f))
  const all = [
    ...held,
    ...(inv.statedMutations || []).map((s) => ({ ...byTest.get(s.test), ...s, kind: 'stated' })),
    ...((standards && standards.nominations) || []).map((s) => ({ ...byTest.get(s.test), ...s, kind: 'nominated' })), // empty-ok: a null Standards axis fails the pull request before mutationList runs, so standards is never null here
    ...candidates.filter(touched).map((c) => ({ ...c, kind: 'candidate' })),
    ...candidates.filter((c) => !touched(c)).map((c) => ({ ...c, kind: 'candidate' })),
  ]
  const seen = new Set()
  const deduped = all.filter((m) => m.test && !seen.has(m.test) && seen.add(m.test))
  const inRange = (m) => {
    if (!d || m.kind === 'nominated' || m.kind === 'held') return true
    const files = [m.file, m.breaks].filter(Boolean)
    return !files.length || files.some((f) => d.changedFiles.includes(f))
  }
  const clearedBy = (m) => {
    const files = [m.file, m.breaks].filter(Boolean)
    if (m.kind !== 'candidate' || !files.length) return null
    return prior.clearers.find((c) => c.reproduced.some((t) => names(t, m.test)) && !files.some((f) => c.files.includes(f))) || null
  }
  const cleared = []
  const list = deduped.filter(inRange).filter((m) => {
    const by = clearedBy(m)
    if (by) cleared.push({ test: m.test, url: by.url })
    return !by
  })
  const cutHeld = list.slice(MUTATION_CAP).filter((m) => m.kind === 'held').map((m) => m.test)
  return { list: list.slice(0, MUTATION_CAP), cut: list.slice(MUTATION_CAP).map((m) => m.test), cutHeld, outside: deduped.filter((m) => !inRange(m)).map((m) => m.test), cleared }
}
const clearedText = (cleared) => cleared.map((c) => `${c.test} (cleared by ${c.url})`).join(', ')

// One row per mutation. An agent that dies, returns nothing or returns a result that is none of the
// three is a could-not-run row, so a dead agent never reads as a pass and the verdict still runs.
const mutationRow = (n, m, i, got) => {
  const ok = got && RESULTS.includes(got.result)
  const row = {
    test: m.test, kind: m.kind, guards: m.guards || m.why || '', mutation: m.mutation || '',
    broken: (ok && got.broken) || m.mutation || '', failed: ok && Number.isInteger(got.failed) ? got.failed : 0,
    total: ok && Number.isInteger(got.total) ? got.total : 0, failingNames: (ok && got.failingNames) || [],
    result: ok ? (got.result === 'removed' && m.kind !== 'held' ? 'could-not-run' : got.result) : 'could-not-run',
    why: ok ? (got.result === 'removed' && m.kind !== 'held' ? `removed is only for a held mutation: ${got.why || ''}` : (got.why || '')) : (got ? `the mutation agent returned the result ${JSON.stringify(got.result)}, which is none of ${RESULTS.join(', ')}` : 'the mutation agent died or returned nothing'),
    countsMatched: null, logFile: mutationLog(n, i),
  }
  // -1 is a count the body does not give (STATED): compare only the counts the author stated.
  if (m.kind === 'stated' && row.result === 'reproduced' && m.failed >= 0) row.countsMatched = row.failed === m.failed && (m.total < 0 || row.total === m.total)
  return row
}
// What in the rows holds the verdict, each named. A held test the cap cut was never mutated, so the
// hold it answers stays open (#289): merging past it is #287's third review again.
const mutationHolds = (rows, cutHeld = []) => [...rows.flatMap((r) => {
  if (r.result === 'did-not-fail') return [`${r.test} did not fail with ${r.broken || 'what it guards'} broken`]
  if (r.kind === 'stated' && r.result !== 'reproduced') return [`the stated mutation of ${r.test} did not reproduce (${r.result})`]
  if (r.kind === 'held' && r.result !== 'reproduced' && r.result !== 'removed') return [`the held test ${r.test} did not reproduce with the break the held record named (${r.result})`]
  if (r.kind === 'nominated' && r.result === 'could-not-run') return [`the nominated mutation of ${r.test} could not run: ${r.why}`]
  return []
}), ...cutHeld.map((t) => `the held test ${t} was cut by the cap of ${MUTATION_CAP} and stays unanswered; a run with a higher maxMutationsPerPr reaches it`)]

// The comments the inventory read, by URL: a record line in one of them is an earlier run's, and a
// record line in any other comment on this pull request is this run's, posted before a resume.
const postPrompt = (n, record, body, inv) => `Post one review comment on pull request #${n}, at most once.

${HOUSE}

OUTPUT FILE: ${DIR(n)}/comment.md
${(inv.comments || []).map((c) => c && c.html_url).filter(Boolean).map((u) => `READ BY THE INVENTORY: ${u}`).join('\n')}

1. Write the text between the line BEGIN COMMENT and the line END COMMENT below, exactly and without those two lines, to the OUTPUT FILE. bodyFile is that path.
2. headNow = \`cd ${REPO} && gh pr view ${n} --json headRefOid --jq .headRefOid\`.
3. Read the pull request's comments (\`cd ${REPO} && gh api --paginate repos/{owner}/{repo}/issues/${n}/comments\`). Leave out every comment whose html_url is on a READ BY THE INVENTORY line above: those were there before this run, and a record in one of them never stops this post. If a remaining comment carries the line \`${record}\` on a line of its own, this run posted it before a resume: post nothing, set posted false and url to that comment's html_url.
4. Otherwise post it: \`cd ${REPO} && gh pr comment ${n} --body-file ${DIR(n)}/comment.md\`; posted true, url the comment's URL it prints. Post it even if headNow differs from the head the record names: the record names the head that was read.
5. If GitHub cannot be reached at any of steps 2 to 4, post nothing and retry nothing: unreachable true, posted false, url "", headNow as read or "". The OUTPUT FILE keeps the body.

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
    ? [MUTATION_HEAD, '| --- | --- | --- | --- | --- |',
      ...mut.rows.map((r) => `| ${[r.test, r.broken || '-', `${r.failed} of ${r.total}`,
        r.result + (r.countsMatched === true ? ', counts match the stated claim' : r.countsMatched === false ? ', counts differ from the stated claim' : '') + (r.why ? `: ${r.why}` : ''),
        r.logFile].map(cell).join(' | ')} |`)]
    : [mut.outside.length ? 'No mutations run: the range touches no candidate invariant test and no stated mutation.'
      : mut.cleared.length ? 'No mutations run: every candidate invariant test was cleared by an earlier record, and the body states no mutation.'
      : 'No mutations run: the diff adds or changes no candidate invariant test and the body states no mutation.']),
  '',
  mut.cut.length ? `Not mutated, cut by the cap of ${MUTATION_CAP}: ${mut.cut.join(', ')}.` : `Not mutated: nothing; the cap of ${MUTATION_CAP} cut nothing.`,
  ...(mut.outside.length ? [`Not mutated, outside the range, cleared by the earlier record: ${mut.outside.join(', ')}.`] : []),
  ...(mut.cleared.length ? [`Not mutated, cleared by an earlier record that reproduced it, neither its test file nor the source it breaks changed since that record's head: ${clearedText(mut.cleared)}.`] : []),
]
// The line after the head line: the delta's earlier record and range, or why a pull request with an
// earlier record got a full review.
const scopeLines = (d, full) => (d
  ? d.rebuilt
    ? [`Delta review: a rebuild, since the record ${d.url} (${d.verdict}) names ${d.from}, no ancestor of the head; ${d.mapping.filter((m) => m.equals).length} of ${d.mapping.length} new commit(s) map onto an old one with an equal tree, and ${deltaScope(d)}${d.merges.length ? `, merge(s) ${d.merges.join(', ')} audited on their own` : ''} and the content diff ${d.from} ${d.to} were reviewed, with the whole diff from the fork point for context.`,
      `Rebuild mapping (new = old, equal trees): ${mappingText(d)}.`]
    : [`Delta review: since the record ${d.url} (${d.verdict}) on ${d.from}, the range ${deltaRange(d)}, ${d.commits.length} commit(s)${d.merges.length ? `, merge(s) ${d.merges.join(', ')} audited on their own` : ''}; the whole diff from the fork point was read for context.`]
  : full ? [`Full review: the latest record, ${full.url}, names ${full.head}, ${full.why}.`] : [])
const earlierLines = (d, v) => (d && d.verdict === 'hold'
  ? ['## The earlier hold', '', `The findings of ${d.url}, checked for an answer in ${d.rebuilt ? `${deltaScope(d)} and the content diff ${d.from} ${d.to}` : deltaRange(d)}:`,
    ...(v.earlierFindings.length ? v.earlierFindings.map((f) => `- ${f.finding}: ${f.answered ? 'answered' : 'not answered'}. ${f.how}`) : ['- The verdict listed none, so the hold stands: a held record\'s findings are never dropped unchecked.']), '']
  : [])
const followUpLines = (list) => ['## Follow-ups', '',
  ...(list.length
    ? ['Carried findings that name no open ticket; the morning run files each one (#292).', '', FOLLOW_UP_HEAD, '| --- | --- | --- | --- |',
      ...list.map((f) => `| ${[f.title, f.axis, f.where || '-', f.firstCarried || THIS_RECORD].map(cell).join(' | ')} |`)]
    : ['- None: the review carries no finding that names no open ticket.']),
  '']
const commentBody = (inv, v, record, mut, d, full, followUps = []) => [
  '## Review',
  '',
  `Head ${inv.head}, read against the fork point ${inv.forkPoint} (origin/main at ${inv.baseTip}).`,
  ...scopeLines(d, full),
  `CI on the head: ${inv.ci.map((c) => `${c.name} ${c.state}`).join(', ') || 'none reported'}.`,
  `Owner set: ${inv.ownership.owners.join(', ') || 'none'} (path-ownership ${inv.ownership.ok ? 'passed' : 'refused'}).`,
  'Axes: Standards and Spec reported; the verdict re-took every severity but those of the script\'s head comparison (#294). Run by the review-queue workflow (#83).',
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
  ...followUpLines(followUps),
  ...earlierLines(d, v),
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

// ---------------------------------------------------------------- the description's head
// A description whose stated head is not the head (#294). #287's second review found its description
// still naming ed8d063 after a rebuild, and only an axis's reading caught it. The inventory reports
// each sha the description states as the head; this compares each with the head it read, a full sha
// or an abbreviation of at least seven hex digits matching as its prefix, case aside, and makes each
// one that differs, or is no sha at all, a verified Spec finding naming both. A description that
// states no head yields none. The verdict cannot re-take a finding it did not write, so the severity
// is set here: low, because the fix is an edit to the description, not to the branch.
const staleHeadFindings = (inv) => (inv.statedHeads || []).filter((s) => s && typeof s.sha === 'string')
  .filter((s) => { const h = s.sha.trim().toLowerCase(); return !(/^[0-9a-f]{7,40}$/.test(h) && inv.head.startsWith(h)) })
  .map((s) => ({
    axis: 'Spec', status: 'verified', severity: 'low', where: 'the pull request description',
    title: `The description names the head ${s.sha.trim()}, but the head is ${inv.head}`,
    checked: `Compared by the script with the head gh serves; the description says: ${JSON.stringify(s.quote || '')}`,
  }))

// ---------------------------------------------------------------- a rebuilt head
// The range agent's mapping judged against the inventory's commits (#293): null when it cannot be
// trusted, so the review is full, as a range nobody could read is. It must name every commit of the
// head and no other, each once, with equals a full sha or "". mapped is whether any commit maps;
// unmapped is the named set the delta reviews, in the mapping's order.
const SHA = /^[0-9a-f]{40}$/
const rebuildOf = (inv, rg) => {
  const mapping = Array.isArray(rg.mapping) ? rg.mapping : []
  const heads = new Set(inv.commits || [])
  const seen = new Set()
  for (const m of mapping) {
    if (!m || !heads.has(m.commit) || seen.has(m.commit) || !(m.equals === '' || SHA.test(m.equals))) return null
    seen.add(m.commit)
  }
  if (!heads.size || seen.size !== heads.size) return null
  const unmapped = mapping.filter((m) => !m.equals).map((m) => m.commit)
  return { mapping: mapping.map((m) => ({ commit: m.commit, equals: m.equals })), unmapped, mapped: unmapped.length < mapping.length }
}

// ---------------------------------------------------------------- one pull request
// A stage that fails fails its pull request alone (#86): the result is outcome failed with the stage,
// no verdict and no record, and nothing is posted for it, since the post is the last stage. An agent
// that returns nothing, or a stage that throws, fails that stage, named with the message; the other pull requests run on in their own
// pipelines. Two stages are built not to fail: a range agent that dies makes a full review, and a
// mutation agent that dies makes a could-not-run row.
const blank = (n) => ({
  number: n, head: '', base: '', baseTip: '', verdict: '', outcome: 'failed', stage: '', recordUrl: '',
  findings: { verified: [], suspected: [] }, mutations: [], notMutated: [], skippedBecauseRecorded: false,
  headMoved: false, earlierRecords: [], earlierRecord: null, range: null, outsideRange: [], clearedByEarlier: [], commentFile: '',
  followUps: [],
})
const failed = (res, stage) => ({ ...res, outcome: 'failed', stage, verdict: '', recordUrl: '' })
const review = async (n) => {
  const res = blank(n)
  const at = { stage: 'inventory' }
  try { return await reviewOne(n, res, at) } catch (e) {
    const why = `${at.stage}: ${String((e && e.message) || e).split('\n')[0]}`
    log(`#${n}: failed at ${why}`)
    return failed(res, why)
  }
}
const reviewOne = async (n, res, at) => {
  const inv = await run(inventoryPrompt(n), { label: `inventory:${n}`, phase: 'Inventory', schema: INVENTORY })
  if (!inv || inv.error) return failed(res, 'inventory' + (inv ? ': ' + inv.error : ': the inventory agent returned nothing'))
  if (!/^[0-9a-f]{40}$/.test(inv.head) || !/^[0-9a-f]{40}$/.test(inv.forkPoint)) return failed(res, 'inventory: head or fork point is not a full sha')
  res.head = inv.head
  res.base = inv.forkPoint
  res.baseTip = /^[0-9a-f]{40}$/.test(inv.baseTip || '') ? inv.baseTip : ''
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

  // A delta review when the latest record names another head and that head is an ancestor of this
  // one, or when this head rebuilt it and some of its commits keep an old tree (#293). A range agent
  // that dies, or a head that is neither, is a full review, said so.
  let d = null
  let full = null
  const latestRecord = records[records.length - 1]
  let rg = null
  if (latestRecord && latestRecord.head !== inv.head) {
    at.stage = 'range'
    try { rg = await run(rangePrompt(n, inv, latestRecord.head, latestRecord.base, sinceHeadsOf(records, inv)), { label: `range:${n}`, phase: 'Inventory', schema: RANGE }) } catch (e) { rg = null }
    const rebuild = rg && !rg.ancestor && !rg.error ? rebuildOf(inv, rg) : null
    if (rg && rg.ancestor && !rg.error) {
      d = { from: latestRecord.head, to: inv.head, fork: inv.forkPoint, url: latestRecord.url, verdict: latestRecord.verdict, commits: rg.commits || [], merges: rg.merges || [], changedFiles: rg.changedFiles || [], rebuilt: false, mapping: [] }
    } else if (rebuild && rebuild.mapped) {
      d = { from: latestRecord.head, to: inv.head, fork: inv.forkPoint, url: latestRecord.url, verdict: latestRecord.verdict, commits: rebuild.unmapped, merges: (rg.merges || []).filter((m) => rebuild.unmapped.includes(m)), changedFiles: rg.changedFiles || [], rebuilt: true, mapping: rebuild.mapping }
    }
    if (d) {
      res.earlierRecord = { url: d.url, head: d.from, verdict: d.verdict }
      res.range = { from: d.from, to: d.to, commits: d.commits, merges: d.merges, rebuilt: d.rebuilt, mapping: d.mapping }
    } else {
      full = { url: latestRecord.url, head: latestRecord.head, why: !rg ? 'and the range agent returned nothing, so its ancestry is unread' : rg.error ? `and its range could not be read: ${rg.error}` : `which is not an ancestor of the head, ${rebuild ? 'and no commit of the rebuild maps onto an old one with an equal tree' : 'and the rebuild\'s mapping does not cover every commit of the head with a sha or ""'}` }
      log(`#${n}: full review; the latest record ${latestRecord.url} names ${latestRecord.head}, ${full.why}`)
    }
  }

  at.stage = 'review'
  const [standards, spec] = await parallel([
    () => run(axisPrompt('Standards', n, inv, d), { label: `standards:${n}`, phase: 'Review', schema: AXIS }),
    () => run(axisPrompt('Spec', n, inv, d), { label: `spec:${n}`, phase: 'Review', schema: AXIS }),
  ])
  // An axis that returned nothing fails the pull request here, with no comment: a hold posted for it
  // would be a record, and a skipRecorded run would then skip the head no axis reviewed.
  const missing = [standards ? '' : 'Standards', spec ? '' : 'Spec'].filter(Boolean)
  if (missing.length) return failed(res, `review: the ${missing.join(' and ')} axis returned nothing`)

  at.stage = 'mutate'
  const { list, cut, cutHeld, outside, cleared } = mutationList(inv, standards, d, priorOf(records, inv, rg))
  const got = await parallel(list.map((m, i) => async () => {
    try { return await run(mutatePrompt(n, inv, m, i + 1), { label: `mutate:${n}:${i + 1}`, phase: 'Mutate', schema: MUTATION }) } catch (e) { return null }
  }))
  const rows = list.map((m, i) => mutationRow(n, m, i + 1, got && got[i]))
  const mut = { rows, cut, outside, cleared, holds: mutationHolds(rows, cutHeld) }
  res.mutations = rows
  res.notMutated = cut
  res.outsideRange = outside
  res.clearedByEarlier = cleared

  at.stage = 'verdict'
  const stale = staleHeadFindings(inv)
  const v = await run(verdictPrompt(n, inv, mut, d, stale), { label: `verdict:${n}`, phase: 'Verdict', schema: VERDICT })
  if (!v) return failed(res, 'verdict: the verdict agent returned nothing')
  v.earlierFindings = v.earlierFindings || []
  // The comparison's findings lead the verdict's own: the verdict was told of them and not to repeat them.
  v.findings = [...stale, ...(v.findings || [])]
  // The script holds on what it can judge itself: a mutation row under the hold rules, or a held
  // record's finding the range leaves unanswered (an axis that returned nothing failed the pull
  // request above). The verdict agent can hold on more, never on less. An empty earlierFindings after
  // a hold holds too: a hold always had a reason, and a list with none in it is that reason left
  // unchecked (A check fails closed). A follow-up the held record carried without holding on it does
  // not hold here either (#331's third record held on one no commit could answer); a finding that does
  // not say held: false counts as held.
  const unanswered = d && d.verdict === 'hold' && (!v.earlierFindings.length || v.earlierFindings.some((f) => f.answered === false && f.held !== false))
  const verdict = mut.holds.length || unanswered ? 'hold' : v.verdict
  // The record names the head the inventory read, whatever the post agent finds later.
  const record = recordLine(inv.head, inv.forkPoint, verdict)
  const followUps = followUpsFrom(v.followUps, records)
  const body = commentBody(inv, v, record, mut, d, full, followUps)
  res.verdict = verdict
  res.followUps = followUps
  res.findings = {
    verified: v.findings.filter((f) => f.status === 'verified'),
    suspected: v.findings.filter((f) => f.status === 'suspected'),
  }

  at.stage = 'post'
  const post = await run(postPrompt(n, record, body, inv), { label: `post:${n}`, phase: 'Verdict', schema: POST })
  res.commentFile = `${DIR(n)}/comment.md`
  if (!post) return failed(res, 'post: the post agent returned nothing, so whether it posted is unknown; it was handed the body to write to ' + res.commentFile)
  // Moved only on a head gh served that is not the one read; an unreachable gh served none.
  res.headMoved = /^[0-9a-f]{40}$/.test(post.headNow || '') && post.headNow !== inv.head
  if (res.headMoved) log(`#${n}: the head moved to ${post.headNow} during the run; the record names ${inv.head}. Run again for the new head.`)
  if (post.unreachable) {
    log(`#${n}: GitHub unreachable at posting time; nothing posted and nothing retried. The comment is in ${res.commentFile}`)
    return { ...res, outcome: 'unposted', stage: 'post: GitHub unreachable; the body is in ' + res.commentFile }
  }
  if (!post.url) return failed(res, 'post: the post agent posted nothing and named no comment')
  return { ...res, outcome: 'recorded', stage: '', recordUrl: post.url }
}

// ---------------------------------------------------------------- the return's summary
// One line per pull request, the return's first field (#294): the completion notice cuts a return
// short, and on #287's runs the verdict and the mutation rows were past the cut. The line names the
// number, the head read, the verdict ("-" when none was reached) with the outcome and any stage, the
// record's URL, the verified findings and mutations counted, and the tests whose rows did not fail.
const summaryLine = (r) => {
  const didNotFail = (r.mutations || []).filter((m) => m.result === 'did-not-fail').map((m) => m.test)
  return `#${r.number} ${r.head || '(unread)'} ${r.verdict || '-'} (${r.outcome}${r.stage ? ': ' + r.stage : ''}) ${r.recordUrl || '(no record)'}: ${((r.findings || {}).verified || []).length} verified finding(s), ${(r.mutations || []).length} mutation(s), did-not-fail: ${didNotFail.join(', ') || 'none'}`
}

// ---------------------------------------------------------------- run
phase('Inventory')
// The base tip the run saw: the queue agent's reading, or with a named list the first inventory's.
let baseTip = ''
let PRS = NAMED
if (!PRS) {
  const q = await run(queuePrompt(), { label: 'queue', phase: 'Inventory', schema: QUEUE })
  if (!q || q.error) {
    const why = `queue: ${q ? q.error : 'the queue agent returned nothing'}; no pull request was reviewed and nothing was posted`
    log(`review-queue ${DATE}: ${why}`)
    return { summary: [], date: DATE, baseTip: '', logDir: LOG, agents: agentsRun, halted: why, headMoved: [], prs: [] }
  }
  baseTip = /^[0-9a-f]{40}$/.test(q.baseTip || '') ? q.baseTip : ''
  // gh was asked for base main; a row of another base, or a number that is not one, is dropped.
  PRS = [...new Set((q.prs || []).filter((p) => p && p.baseRef === 'main' && Number.isInteger(p.number) && p.number > 0).map((p) => p.number))]
}
log(`review-queue ${DATE}: ${PRS.length} pull request(s)${NAMED ? '' : ' in the open queue'}: ${PRS.map((n) => '#' + n).join(', ') || '(none)'}; log ${LOG}`)
const results = await parallel(PRS.map((n) => () => review(n)))
const prs = results.map((r, i) => r || failed(blank(PRS[i]), 'unknown'))
for (const r of prs) log(`#${r.number}: ${r.outcome}${r.verdict ? ' ' + r.verdict : ''}${r.stage ? ' (' + r.stage + ')' : ''}${r.recordUrl ? ' ' + r.recordUrl : ''}`)
// headMoved: the pull requests whose head moved during the run, each to be run again.
if (!baseTip) baseTip = (prs.find((r) => r.baseTip) || {}).baseTip || ''
log(`review-queue ${DATE}: ${agentsRun} agent(s) ran; origin/main at ${baseTip || '(unread)'}; log ${LOG}`)
return { summary: prs.map(summaryLine), date: DATE, baseTip, logDir: LOG, agents: agentsRun, headMoved: prs.filter((r) => r.headMoved).map((r) => r.number), prs }
