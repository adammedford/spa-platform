// The review-queue script's test companion. Owner: integrator. Tracked beside the script (#83), as
// #213 tracks the overnight script's companion.
//
//   node .claude/workflows/review-queue_tests.mjs [script]
//
// Runs review-queue.js end to end with agent, parallel, log and phase stubbed, and with gh stubbed
// where the script reaches it: through the inventory agent, which serves canned pull requests as gh
// would list them, and the post agent, which records the comment it was asked to post instead of
// posting it. A check that needs more of GitHub gives the run ghStub(): a store of posted comments
// that outlives one run, a head that moves before posting, and a GitHub that cannot be reached at
// posting time (#85). A stubbed range agent reports the delta range a check gives it. A stubbed mutation agent reports, per test, the result a check tells it to, or dies by
// throwing or returning nothing, and records the worktree its prompt names. No agent, gh or network
// call is made. The main checkout the script is given is a
// throwaway git repository with this repository's .gitignore, and a stubbed agent writes every file
// a prompt names as an OUTPUT FILE into it, so the checkout's state after the run is evidence.
// Exits 0 when every check holds, 1 when one fails (each failure printed), 2 when the script cannot
// run. Its last line is its verdict.
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const scriptPath = process.argv[2] || path.join(here, 'review-queue.js')
const top = path.resolve(here, '..', '..')

const sha = (c) => c.repeat(40)
const FORK = sha('b')

// One open pull request into main, as the inventory agent reports it from gh and git.
const pr = (number, changedFiles, extra = {}) => ({
  number, title: `PR ${number}`, author: 'adammedford', branch: `task/pr-${number}`, baseRef: 'main',
  head: sha(String(number % 10)), baseTip: sha('c'), forkPoint: FORK,
  commits: [sha(String(number % 10))], changedFiles,
  ownership: { owners: ['integrator'], ok: true, output: 'path-ownership: one owner' },
  ci: [{ name: 'Documents', state: 'success' }, { name: 'Build and test', state: 'skipped' }],
  cites: { planRow: '', issues: [], mergeSubject: '' }, bodyClaims: [],
  candidates: [], statedMutations: [], reviewCommits: [], statedHeads: [],
  description: '', commitMessages: [],
  comments: [], reviews: [], error: '', ...extra,
})

// PR #76's head and fork point, and the review that cleared it, as GitHub serves them: the fixed line
// sits on a line of its own inside a COMMENTED review, between prose.
const H76 = '3d7d3c8070cbe4d374427aa662da3c7dce75c5c8'
const B76 = '247b3031f2bd1a15a00a346956d0f919ef8fde44'
const CLEARED_76 = {
  state: 'COMMENTED', author_association: 'OWNER', submitted_at: '2026-10-17T18:02:11Z',
  html_url: 'https://github.com/adammedford/spa-platform/pull/76#pullrequestreview-5559002050',
  body: `Final independent fix review\n\nReviewed ${H76} against ${B76}: merge\n\nThe source head is unchanged.\n`,
}
// Lines that name the head and are not records: the free prose of PR #76's first review, the line in
// backticks, an abbreviated sha, the line from an account outside the repository, the line in an
// approval rather than a COMMENTED review, and another verdict word.
const NOT_RECORDS = [
  { author_association: 'OWNER', created_at: '2026-10-17T17:00:00Z', html_url: 'u/1', body: `Reviewed head: ${H76}\nHold.` },
  { author_association: 'OWNER', created_at: '2026-10-17T17:01:00Z', html_url: 'u/2', body: `\`Reviewed ${H76} against ${B76}: merge\`` },
  { author_association: 'OWNER', created_at: '2026-10-17T17:02:00Z', html_url: 'u/3', body: `Reviewed ${H76.slice(0, 7)} against ${B76}: merge` },
  { author_association: 'NONE', created_at: '2026-10-17T17:03:00Z', html_url: 'u/4', body: `Reviewed ${H76} against ${B76}: merge` },
  { author_association: 'OWNER', created_at: '2026-10-17T17:04:00Z', html_url: 'u/5', body: `Reviewed ${H76} against ${B76}: approve` },
]
const APPROVAL = { ...CLEARED_76, state: 'APPROVED', html_url: 'u/6' }

const PRS = {
  101: pr(101, ['docs/agents/review.md']),                        // docs-only
  102: pr(102, ['.scratch/review-queue/issues/01-skeleton.md']),  // tracker-only
  76: pr(76, ['.scratch/x/issues/01.md'], { head: H76, forkPoint: B76, commits: [H76], reviews: [CLEARED_76] }),
  77: pr(77, ['docs/x.md'], { head: H76, forkPoint: B76, commits: [H76], comments: NOT_RECORDS, reviews: [APPROVAL] }),
  // A record of an earlier head is not a record of this one.
  78: pr(78, ['docs/y.md'], { reviews: [CLEARED_76] }),
}

const made = []
process.on('exit', () => { for (const dir of made) fs.rmSync(dir, { recursive: true, force: true }) })

// A candidate invariant test as the inventory reports it, and a mutation the body states.
const cand = (test, project = 'tests/Platform.Registry.Tests') => ({ test, project, file: `${project}/${test}.cs`, guards: `what ${test} guards`, breaks: `src/${test}.cs` })
const stated = (test, failed, total, project = 'tests/Platform.Registry.Tests') => ({ test, project, mutation: `break what ${test} guards`, failed, total, quote: `breaking it failed ${failed} of ${total}` })
// What a stubbed mutation agent reports: reproduced, did-not-fail or could-not-run, with counts.
const row = (result, failed = result === 'reproduced' ? 1 : 0, total = 10) => ({ result, failed, total })

// behave: { nominations: { [n]: [...] }, mutate: { [test]: row | 'throw' | 'null' }, verdict: 'merge' | 'hold',
//           range: { [n]: what the range agent reports }, earlier: { [n]: the verdict's earlierFindings },
//           followUps: { [n]: the verdict's followUps, each carried finding with the ticket it names },
//           gh: a stubbed GitHub (ghStub below) }
// Without behave.gh the post agent posts and reports the head unchanged; with it, the stubbed gh
// decides, and its store of posted comments outlives the run, so a rerun reads what a run posted.
const allPrompts = []   // {label, prompt}, every agent's, across every run
function run(scriptArgs, served, behave = {}) {
  const calls = []
  const posted = []
  const mutations = []
  const prompts = {}
  const repo = fs.mkdtempSync(path.join(os.tmpdir(), 'review-queue-tests-'))
  made.push(repo)
  const git = (...a) => execFileSync('git', ['-C', repo, ...a], { encoding: 'utf8' })
  git('init', '-q')
  fs.copyFileSync(path.join(top, '.gitignore'), path.join(repo, '.gitignore'))
  fs.writeFileSync(path.join(repo, 'README.md'), 'tracked\n')
  git('add', '-A')
  git('-c', 'user.name=t', '-c', 'user.email=t@t', 'commit', '-q', '-m', 'start')
  const args = { repo, wt: path.join(os.tmpdir(), 'review-queue-tests-wt'), date: '2026-10-21', ...scriptArgs }
  // A prs of undefined is a launch with no list: the key is left out, as an operator would.
  if (args.prs === undefined) delete args.prs

  globalThis.args = args
  globalThis.phase = () => {}
  globalThis.log = () => {}
  // A rejected branch rejects the whole call. This is stricter than the Workflow tool, whose parallel()
  // resolves a thrower to null and never rejects, and whose agent() returns null for an agent that
  // dies (the workflow-authoring reference, "Script body hooks"). The stub is strict on purpose, so
  // the script's own guard turns a dying mutation agent into a row and no check rests on that promise.
  globalThis.parallel = (fns) => Promise.all(fns.map((f) => f()))
  // Agents in flight now and at most, across the run: what the slot count bounds.
  // events: every agent's start and end, in order, as `start <label>` and `end <label>`.
  const flight = { now: 0, max: 0 }
  const events = []
  globalThis.agent = async (prompt, opts) => {
    flight.now++
    flight.max = Math.max(flight.max, flight.now)
    events.push(`start ${opts.label}`)
    try { return await answer(prompt, opts) } finally { flight.now--; events.push(`end ${opts.label}`) }
  }
  const answer = async (prompt, opts) => {
    const [kind, n] = opts.label.split(':')
    calls.push(opts.label)
    prompts[opts.label] = prompt
    allPrompts.push({ label: opts.label, prompt })
    // behave.delay: how many milliseconds an agent takes, by label, so agents overlap in time.
    const ms = behave.delay ? behave.delay(opts.label) : 0
    if (ms) await new Promise((resolve) => setTimeout(resolve, ms))
    // behave.hold: a promise, by label, that the agent waits on, so a check decides when it ends.
    if (behave.hold) await behave.hold(opts.label)
    // behave.kill: { [label]: 'throw' | 'null' }, an agent killed mid-run, by throwing as the strict
    // parallel() above surfaces it, or by returning nothing as the Workflow tool's agent() does.
    const killed = (behave.kill || {})[opts.label]
    if (killed === 'throw') throw new Error(`the agent ${opts.label} was killed`)
    if (killed === 'null') return null
    for (const m of prompt.matchAll(/^OUTPUT FILE: (\S+)$/gm)) {
      fs.mkdirSync(path.dirname(m[1]), { recursive: true })
      fs.writeFileSync(m[1], `${opts.label}\n`)
    }
    if (kind === 'queue') {
      // gh pr list as the queue agent reports it: behave.queue, or no open pull request.
      return behave.queue === 'null' ? null : (behave.queue || { baseTip: sha('c'), prs: [], error: '' })
    }
    if (kind === 'inventory') {
      const inv = served[n]
      const gh = behave.gh
      // The store is read when the inventory runs, as gh would serve it; serveStore false is a resumed
      // run whose inventory was replayed from before the post.
      return gh && gh.serveStore !== false ? { ...inv, comments: [...inv.comments, ...gh.store.filter((c) => c.n === Number(n)).map((c) => c.entry)] } : inv
    }
    if (kind === 'range') {
      const r = (behave.range || {})[n]
      return r === 'null' ? null : (r || { from: '', ancestor: false, commits: [], merges: [], changedFiles: [], error: '' })
    }
    if (kind === 'standards' || kind === 'spec') {
      const nominations = kind === 'standards' ? ((behave.nominations || {})[n] || []) : []
      return { axis: kind, sourcesRead: ['CODING_STANDARDS.md'], findings: [], claims: [], nominations }
    }
    if (kind === 'mutate') {
      const test = (/^TEST: (.+)$/m.exec(prompt) || [])[1]
      const worktree = (/^WORKTREE: (\S+)$/m.exec(prompt) || [])[1]
      mutations.push({ n: Number(n), test, worktree, prompt })
      const b = (behave.mutate || {})[test] || row('reproduced')
      if (b === 'throw') throw new Error('the mutation agent died')
      if (b === 'null') return null
      return { test, broken: `broke what ${test} guards`, failingNames: [], why: b.result === 'could-not-run' ? 'the project did not build' : '', ...b }
    }
    if (kind === 'verdict') return { verdict: behave.verdict || 'merge', findings: [], claims: [], outstanding: ['nothing'], summary: 's', earlierFindings: (behave.earlier || {})[n] || [], caughtAccepted: (behave.caught || {})[n] || [], followUps: (behave.followUps || {})[n] || [], changeRequests: (behave.changeRequests || {})[n] || [] }
    if (kind === 'post') {
      const m = /^BEGIN COMMENT\n([\s\S]*)\nEND COMMENT$/m.exec(prompt)
      const body = m ? m[1] : null
      const file = (/^OUTPUT FILE: (\S+)$/m.exec(prompt) || [])[1]
      // Step 1 of the post prompt: the body goes to the log before gh is reached.
      if (file && body !== null) fs.writeFileSync(file, body)
      const gh = behave.gh
      if (!gh) {
        posted.push({ n: Number(n), body })
        return { posted: true, url: `https://github.com/o/r/pull/${n}#issuecomment-1`, headNow: served[n].head, unreachable: false, bodyFile: file }
      }
      return gh.post(Number(n), prompt, body, file, posted, served[n])
    }
    throw new Error(`unexpected agent label ${opts.label}`)
  }
  return { repo, wt: args.wt, calls, posted, mutations, prompts, git, flight, events }
}

// A stubbed gh shared across runs. failPost: GitHub cannot be reached at posting time. headNow: the
// head gh serves when the post agent re-reads it. The post agent follows its prompt's step 3 as
// written: a comment carrying the record line, among every comment gh serves, counts as this run's
// only when its URL is not among those the prompt lists as read by the inventory.
function ghStub(opts = {}) {
  const gh = { store: [], failPost: false, headNow: null, serveStore: true, ...opts }
  let next = 0
  gh.post = (n, prompt, body, file, posted, pull) => {
    const headNow = gh.headNow || pull.head
    if (gh.failPost) return { posted: false, url: '', headNow: '', unreachable: true, bodyFile: file }
    const record = (body || '').split('\n').pop()
    const read = new Set([...prompt.matchAll(/^READ BY THE INVENTORY: (\S+)$/gm)].map((x) => x[1]))
    // Every comment gh serves now: those the pull request came with, then those posted since.
    const all = [...pull.comments, ...gh.store.filter((c) => c.n === n).map((c) => c.entry)]
    const mine = all.find((c) => !read.has(c.html_url) && c.body.split('\n').includes(record))
    if (mine) return { posted: false, url: mine.html_url, headNow, unreachable: false, bodyFile: file }
    next++
    const entry = { author_association: 'OWNER', created_at: `2026-10-21T10:${String(next).padStart(2, '0')}:00Z`, html_url: `https://github.com/o/r/pull/${n}#issuecomment-${100 + next}`, body }
    gh.store.push({ n, entry })
    posted.push({ n, body })
    return { posted: true, url: entry.html_url, headNow, unreachable: false, bodyFile: file }
  }
  return gh
}

async function execute() {
  const src = fs.readFileSync(scriptPath, 'utf8').replace(/^export const meta/m, 'const meta')
  return await new Function('return (async () => {' + src + '\n})()')()
}
// The script's meta, evaluated alone, as the Workflow tool reads it before any run.
const loadMeta = () => {
  const src = fs.readFileSync(scriptPath, 'utf8')
  const m = /^export const meta = ([\s\S]*?\n})\n/m.exec(src)
  return m ? new Function(`return (${m[1]})`)() : null
}
// What whenToUse lacks of the working launch (#522): scriptPath, the script's path, and the three
// required arguments, each a whole word. Empty when it names them all.
const LAUNCH_WORDS = ['scriptPath', '.claude/workflows/review-queue.js', 'repo', 'date', 'wt']
const launchGaps = (whenToUse) => LAUNCH_WORDS.filter((w) =>
  !new RegExp(`(^|[^\\w.])${w.replace(/[.*+?^${}()|[\]\\/]/g, '\\$&')}(?![\\w])`).test(whenToUse || ''))

const failures = []
const fail = (msg) => failures.push(msg)
let checks = 0

try {
  // 1. A docs-only and a tracker-only pull request each get both axes and a verdict, and one comment
  //    whose last line is the record of the head and fork point the inventory read.
  checks++
  let env = run({ prs: [101, 102] }, PRS)
  let result = await execute(env)
  for (const n of [101, 102]) {
    for (const kind of ['inventory', 'standards', 'spec', 'verdict', 'post']) {
      if (!env.calls.includes(`${kind}:${n}`)) fail(`PR ${n}: no ${kind} agent ran`)
    }
    const r = (result.prs || []).find((x) => x.number === n)
    if (!r) { fail(`PR ${n}: no result returned`); continue }
    if (r.outcome !== 'recorded') fail(`PR ${n}: outcome ${r.outcome}, expected recorded`)
    if (r.verdict !== 'merge') fail(`PR ${n}: verdict ${r.verdict}, expected merge`)
    const comments = env.posted.filter((p) => p.n === n)
    if (comments.length !== 1) { fail(`PR ${n}: ${comments.length} comments posted, expected one`); continue }
    const lines = (comments[0].body || '').split('\n')
    const want = `Reviewed ${sha(String(n % 10))} against ${FORK}: merge`
    if (lines[lines.length - 1] !== want) fail(`PR ${n}: the comment's last line is ${JSON.stringify(lines[lines.length - 1])}, expected ${JSON.stringify(want)}`)
    if (!lines.some((l) => /^No mutations run/.test(l))) fail(`PR ${n}: with no candidate and no stated mutation, the comment does not say no mutations run`)
    if (env.calls.some((c) => c.startsWith(`mutate:${n}:`))) fail(`PR ${n}: a mutation agent ran with nothing to mutate`)
  }

  // 2. After that run the main checkout's tracked files are unchanged, nothing new is visible to git,
  //    and every file the run wrote is under its log directory.
  checks++
  const logRel = '.scratch/review-queue/log/2026-10-21/'
  if (env.git('status', '--porcelain') !== '') fail(`the main checkout changed: ${env.git('status', '--porcelain').trim()}`)
  const ignored = env.git('status', '--porcelain', '--ignored', '--untracked-files=all').split('\n').filter(Boolean)
  if (!ignored.length) fail('the run wrote no file at all, so the log directory was never exercised')
  for (const line of ignored) {
    const name = line.slice(3)
    if (!name.startsWith(logRel)) fail(`the run wrote ${name}, outside ${logRel}`)
  }

  // 3. A hand-written record on the current head, PR #76's fixed line in a COMMENTED review, skips the
  //    head: no review agent runs, nothing is posted, and the record's URL is returned.
  checks++
  env = run({ prs: [76] }, PRS)
  result = await execute(env)
  const r76 = (result.prs || []).find((x) => x.number === 76) || {}
  if (r76.skippedBecauseRecorded !== true) fail('PR 76: a record on the current head did not skip it')
  if (r76.outcome !== 'skipped') fail(`PR 76: outcome ${r76.outcome}, expected skipped`)
  if (r76.recordUrl !== CLEARED_76.html_url) fail(`PR 76: recordUrl ${r76.recordUrl}, expected the record's URL`)
  const ran76 = env.calls.filter((c) => !c.startsWith('inventory:'))
  if (ran76.length) fail(`PR 76: agents ran after the inventory of a recorded head: ${ran76.join(', ')}`)
  if (env.posted.length) fail(`PR 76: ${env.posted.length} comment(s) posted on a recorded head`)

  // 4. Lines that are not records do not skip the head, and a record of another head does not either.
  checks++
  env = run({ prs: [77, 78] }, PRS)
  result = await execute(env)
  for (const n of [77, 78]) {
    const r = (result.prs || []).find((x) => x.number === n) || {}
    if (r.skippedBecauseRecorded !== false || r.outcome !== 'recorded') fail(`PR ${n}: outcome ${r.outcome}, skipped ${r.skippedBecauseRecorded}; a line that is not a record of this head skipped it`)
    if (env.posted.filter((p) => p.n === n).length !== 1) fail(`PR ${n}: expected one comment`)
  }
  // The lines that are not records are not read as records of another head either, and the record
  // of an earlier head is reported as one.
  const earlier = (n) => JSON.stringify(((result.prs || []).find((x) => x.number === n) || {}).earlierRecords)
  if (earlier(77) !== '[]') fail(`PR 77: earlierRecords ${earlier(77)}, expected none: a line that is not a record was read as one`)
  if (earlier(78) !== JSON.stringify([CLEARED_76.html_url])) fail(`PR 78: earlierRecords ${earlier(78)}, expected PR #76's review`)

  // 5. With skipRecorded false, a recorded head is reviewed again and gets a new record.
  checks++
  env = run({ prs: [76], skipRecorded: false }, PRS)
  result = await execute(env)
  const again = (result.prs || []).find((x) => x.number === 76) || {}
  if (again.outcome !== 'recorded' || env.posted.length !== 1) fail(`PR 76 with skipRecorded false: outcome ${again.outcome}, ${env.posted.length} comment(s)`)

  // 6. A bad launch starts no agent.
  checks++
  env = run({ prs: [] }, PRS)
  result = await execute(env)
  if (!result || !result.halted || env.calls.length) fail(`a launch with no pull request started ${env.calls.length} agent(s) and returned ${JSON.stringify(result)}`)

  // 7. The producer's comment is a record to the recogniser: served back as the owner's comment, the
  //    comment a first run posted makes a second run on the same head post nothing.
  checks++
  env = run({ prs: [101] }, PRS)
  await execute(env)
  const first = env.posted[0] ? env.posted[0].body : null
  if (first === null) fail('PR 101: the first run posted no comment to serve back')
  else {
    const echo = { author_association: 'OWNER', created_at: '2026-10-21T09:00:00Z', html_url: 'https://github.com/o/r/pull/101#issuecomment-1', body: first }
    env = run({ prs: [101] }, { 101: { ...PRS[101], comments: [echo] } })
    result = await execute(env)
    const r = (result.prs || []).find((x) => x.number === 101) || {}
    if (!r.skippedBecauseRecorded || env.posted.length) fail(`PR 101's second run: outcome ${r.outcome}, ${env.posted.length} comment(s); its own record did not skip the head`)
  }

  // 8. The recogniser agrees with the reader land.sh runs. Each body below holds a merge record of
  //    PR #76's head, separated or padded by a character on which JavaScript's and Python's notions
  //    of a line or of whitespace differ; tools/review_record.py is run on each, and the head must be
  //    skipped exactly when the reader clears it. Only merge records: the reader exits 1 on a hold.
  checks++
  const L = `Reviewed ${H76} against ${B76}: merge`
  const BODIES = [L, `  ${L}\t`, `\ufeff${L}`, `\x1f${L}`, `\xa0${L}`, `\u3000${L}`, `x\u200b${L}`,
    `prose\r${L}`, `prose\v${L}`, `prose\x1d${L}`, `${L}\x85`, `prose\u2028${L}`, `prose\u2029${L}`]
  for (const [i, body] of BODIES.entries()) {
    const entry = { author_association: 'OWNER', created_at: '2026-10-17T17:00:00Z', html_url: `u/r${i}`, body }
    let cleared
    try {
      execFileSync('python3', ['-I', path.join(top, 'tools', 'review_record.py'), H76],
        { input: JSON.stringify({ comments: [[entry]], reviews: [[]] }), stdio: ['pipe', 'ignore', 'ignore'] })
      cleared = true
    } catch (e) {
      if (e.code === 'ENOENT') throw new Error('python3 is required to run tools/review_record.py, the reader this check holds the script to')
      if (e.status !== 1) throw new Error(`tools/review_record.py exited ${e.status} on body ${i}`)
      cleared = false
    }
    env = run({ prs: [76] }, { 76: { ...PRS[76], reviews: [], comments: [entry] } })
    const res = await execute(env)
    const skipped = !!(((res.prs || [])[0]) || {}).skippedBecauseRecorded
    if (skipped !== cleared) fail(`body ${i} ${JSON.stringify(body)}: the reader ${cleared ? 'clears' : 'refuses'} it, the script ${skipped ? 'skips' : 'reviews'} the head`)
  }

  // 9. Two records of the head at the same instant, one comment carrying both verdicts: a hold among
  //    them holds, as in the reader.
  checks++
  const both = { author_association: 'OWNER', created_at: '2026-10-17T17:00:00Z', html_url: 'u/both', body: `Reviewed ${H76} against ${B76}: hold\nReviewed ${H76} against ${B76}: merge` }
  env = run({ prs: [76] }, { 76: { ...PRS[76], reviews: [], comments: [both] } })
  result = await execute(env)
  const tie = (result.prs || [])[0] || {}
  if (tie.verdict !== 'hold') fail(`PR 76 with a hold and a merge at one instant: verdict ${tie.verdict}, expected hold`)

  // The mutations a run posted for one pull request: its comment's table rows, as cells.
  const TABLE_HEAD = '| Test | What was broken | Failed of total | Result | Log file |'
  const tableOf = (body) => {
    const lines = (body || '').split('\n')
    const at = lines.indexOf(TABLE_HEAD)
    if (at < 0) return null
    const rows = []
    for (const l of lines.slice(at + 2)) {
      if (!l.startsWith('|')) break
      rows.push(l.slice(1, -1).split(' | ').map((c) => c.trim()))
    }
    return rows
  }
  const resultOf = (n, result) => (result.prs || []).find((x) => x.number === n) || {}
  const commentOf = (env, n) => (env.posted.find((p) => p.n === n) || {}).body || ''

  // 10. A mutation agent that dies, by throwing or by returning nothing, becomes a could-not-run row
  //     and the verdict still runs. On a candidate nobody suspected it is reported and does not hold;
  //     on a nominated test it holds.
  checks++
  const DIES = { 201: pr(201, ['tests/Platform.Registry.Tests/A.cs'], { candidates: [cand('ThrowsTest'), cand('NullTest'), cand('FineTest')] }) }
  env = run({ prs: [201] }, DIES, { mutate: { ThrowsTest: 'throw', NullTest: 'null' } })
  result = await execute(env)
  {
    const r = resultOf(201, result)
    if (!env.calls.includes('verdict:201')) fail('PR 201: a dying mutation agent stopped the verdict')
    if (r.outcome !== 'recorded') fail(`PR 201: outcome ${r.outcome}, expected recorded`)
    for (const t of ['ThrowsTest', 'NullTest']) {
      const m = (r.mutations || []).find((x) => x.test === t)
      if (!m) fail(`PR 201: no mutation row for ${t}, whose agent died`)
      else if (m.result !== 'could-not-run' || !m.why) fail(`PR 201: ${t}'s row is ${JSON.stringify(m)}, expected could-not-run with why`)
      const cells = (tableOf(commentOf(env, 201)) || []).find((c) => c[0] === t)
      if (!cells || !/^could-not-run: \S/.test(cells[3])) fail(`PR 201: the comment's table has no could-not-run row with why for ${t}`)
    }
    if (r.verdict !== 'merge') fail(`PR 201: verdict ${r.verdict}; a could-not-run on candidates nobody suspected must not hold`)
  }
  env = run({ prs: [201] }, DIES, { mutate: { ThrowsTest: 'throw', NullTest: 'null' }, nominations: { 201: [{ test: 'NullTest', project: 'tests/Platform.Registry.Tests', why: 'asserts a constant' }] } })
  result = await execute(env)
  if (resultOf(201, result).verdict !== 'hold') fail(`PR 201 with NullTest nominated: verdict ${resultOf(201, result).verdict}; a could-not-run on a nominated test must hold`)

  // 11. Every mutation agent is given its own throwaway worktree under the worktree root, none under
  //     the repository, and is told to remove and prune it after its run.
  checks++
  const WIDE = { 202: pr(202, ['tests/x.cs'], { candidates: [cand('T1'), cand('T2'), cand('T3')] }), 203: pr(203, ['tests/y.cs'], { candidates: [cand('T4')] }) }
  env = run({ prs: [202, 203] }, WIDE)
  await execute(env)
  if (env.mutations.length !== 4) fail(`PRs 202 and 203: ${env.mutations.length} mutation agents, expected 4`)
  const seen = new Set()
  for (const m of env.mutations) {
    const w = m.worktree || ''
    if (!w.startsWith(env.wt + '/')) fail(`mutation ${m.test}: worktree ${JSON.stringify(w)} is not under the worktree root ${env.wt}`)
    if (w === env.repo || w.startsWith(env.repo + '/')) fail(`mutation ${m.test}: worktree ${w} is under the repository`)
    if (seen.has(w)) fail(`mutation ${m.test}: worktree ${w} is shared with another mutation`)
    seen.add(w)
    if (!m.prompt.includes(`git -C ${env.repo} worktree add --detach ${w} `)) fail(`mutation ${m.test}: not told to add its worktree detached at the head`)
    // Removed and pruned after the run: the instruction comes after the worktree is added.
    const added = m.prompt.indexOf(`git -C ${env.repo} worktree add --detach ${w} `)
    if (m.prompt.lastIndexOf(`git -C ${env.repo} worktree remove --force ${w}`) < added) fail(`mutation ${m.test}: not told to remove its worktree after its run`)
    if (m.prompt.lastIndexOf(`git -C ${env.repo} worktree prune`) < added) fail(`mutation ${m.test}: not told to prune worktrees after its run`)
  }

  // 12. With more candidates than the cap, the cap is honoured and what it cut is named in the comment
  //     body handed to gh. The list is the stated mutations, then the nominations, then the remaining
  //     candidates, deduplicated by test.
  checks++
  const CAPPED = { 204: pr(204, ['tests/z.cs'], {
    candidates: [cand('C1'), cand('C2'), cand('Nom'), cand('C3'), cand('Said')],
    statedMutations: [stated('Said', 1, 10)],
  }) }
  env = run({ prs: [204], maxMutationsPerPr: 3 }, CAPPED, { nominations: { 204: [{ test: 'Nom', project: 'tests/Platform.Registry.Tests', why: 'asserts a constant' }] } })
  result = await execute(env)
  {
    const ran = env.mutations.map((m) => m.test)
    if (JSON.stringify(ran) !== JSON.stringify(['Said', 'Nom', 'C1'])) fail(`PR 204 with a cap of 3: mutated ${JSON.stringify(ran)}, expected ["Said","Nom","C1"]`)
    const body = commentOf(env, 204)
    const cutLine = body.split('\n').find((l) => /^Not mutated/.test(l)) || ''
    for (const t of ['C2', 'C3']) if (!cutLine.includes(t)) fail(`PR 204: the comment's not-mutated line ${JSON.stringify(cutLine)} does not name ${t}, which the cap cut`)
    for (const t of ['Said', 'Nom', 'C1']) if (cutLine.includes(t)) fail(`PR 204: the not-mutated line names ${t}, which was mutated`)
    if (JSON.stringify(resultOf(204, result).notMutated) !== JSON.stringify(['C2', 'C3'])) fail(`PR 204: notMutated ${JSON.stringify(resultOf(204, result).notMutated)}, expected ["C2","C3"]`)
    const rows = tableOf(body)
    if (!rows || rows.length !== 3) fail(`PR 204: the comment's mutations table has ${rows ? rows.length : 'no'} rows, expected 3`)
  }
  // The default cap is 3.
  env = run({ prs: [204] }, { 204: { ...CAPPED[204], statedMutations: [] } })
  await execute(env)
  if (env.mutations.length !== 3) fail(`PR 204 with the default cap: ${env.mutations.length} mutation agents, expected 3`)

  // 13. The hold rules. A did-not-fail row holds, a stated mutation that does not reproduce holds, and
  //     a stated mutation that reproduces with its counts merges, its row saying the counts matched.
  checks++
  const HOLDS = { 205: pr(205, ['tests/h.cs'], { candidates: [cand('Weak')] }), 206: pr(206, ['tests/i.cs'], { statedMutations: [stated('Said', 4, 153)] }) }
  const cases = [
    [205, { Weak: row('did-not-fail') }, 'hold', 'a did-not-fail row'],
    [206, { Said: row('did-not-fail', 0, 153) }, 'hold', 'a stated mutation that did not reproduce'],
    [206, { Said: row('could-not-run', 0, 0) }, 'hold', 'a stated mutation that could not run'],
    [206, { Said: row('reproduced', 4, 153) }, 'merge', 'a stated mutation reproduced with its counts'],
    [206, { Said: { result: 'went-red-ish', failed: 4, total: 153 } }, 'hold', 'a result that is none of the three', 'could-not-run'],
    [206, { Said: row('removed', 0, 0) }, 'hold', 'removed on a stated mutation, which only a held mutation may report', 'could-not-run'],
  ]
  for (const [n, mutate, want, what, rowWant] of cases) {
    env = run({ prs: [n] }, HOLDS, { mutate })
    result = await execute(env)
    const r = resultOf(n, result)
    if (r.verdict !== want) fail(`PR ${n} with ${what}: verdict ${r.verdict}, expected ${want}`)
    const got = ((r.mutations || [])[0] || {}).result
    if (got !== (rowWant || Object.values(mutate)[0].result)) fail(`PR ${n} with ${what}: the row's result is ${got}, expected ${rowWant || Object.values(mutate)[0].result}`)
    const body = commentOf(env, n)
    if (body.split('\n').pop() !== `Reviewed ${HOLDS[n].head} against ${FORK}: ${want}`) fail(`PR ${n} with ${what}: the record does not say ${want}`)
    const rows = tableOf(body)
    if (!rows || rows.length !== 1 || rows[0].length !== 5) fail(`PR ${n} with ${what}: the comment's mutations table is ${JSON.stringify(rows)}, expected one row of five cells under ${TABLE_HEAD}`)
    else if (!/^mutation-1\.txt$|\/mutation-1\.txt$/.test(rows[0][4])) fail(`PR ${n} with ${what}: log file cell ${rows[0][4]}`)
  }
  {
    env = run({ prs: [206] }, HOLDS, { mutate: { Said: row('reproduced', 4, 153) } })
    result = await execute(env)
    const m = (resultOf(206, result).mutations || [])[0] || {}
    if (m.countsMatched !== true) fail(`PR 206: a stated 4 of 153 reproduced as 4 of 153 has countsMatched ${m.countsMatched}`)
    const cells = (tableOf(commentOf(env, 206)) || [])[0] || []
    if (cells[2] !== '4 of 153' || !/counts match/.test(cells[3] || '')) fail(`PR 206: the row reads ${JSON.stringify(cells)}, expected 4 of 153 and a result saying the counts match`)
    env = run({ prs: [206] }, HOLDS, { mutate: { Said: row('reproduced', 3, 153) } })
    result = await execute(env)
    const off = (resultOf(206, result).mutations || [])[0] || {}
    if (off.countsMatched !== false) fail(`PR 206: a stated 4 of 153 reproduced as 3 of 153 has countsMatched ${off.countsMatched}`)
    env = run({ prs: [206] }, HOLDS, { mutate: { Said: row('reproduced', 4, 150) } })
    result = await execute(env)
    const offTotal = (resultOf(206, result).mutations || [])[0] || {}
    if (offTotal.countsMatched !== false) fail(`PR 206: a stated 4 of 153 reproduced as 4 of 150 has countsMatched ${offTotal.countsMatched}`)
    // A body that gives the failed count and no total (-1) is compared on what it gives: a reproduced
    // 4 matches a stated "failed 4", whatever the total, and the prompt never quotes "of -1".
    const NO_TOTAL = { 206: { ...HOLDS[206], statedMutations: [stated('Said', 4, -1)] } }
    env = run({ prs: [206] }, NO_TOTAL, { mutate: { Said: row('reproduced', 4, 153) } })
    result = await execute(env)
    const noTotal = (resultOf(206, result).mutations || [])[0] || {}
    if (noTotal.countsMatched !== true) fail(`PR 206: a stated "failed 4" with no total reproduced as 4 of 153 has countsMatched ${noTotal.countsMatched}`)
    const said = (env.mutations[0] || {}).prompt || ''
    if (!said.includes('(failed 4).')) fail('PR 206: the mutation prompt does not give the stated "failed 4" alone, with no total')
  }

  // 14. The moved head (#85). gh serves another head when the post agent re-reads it: the comment is
  //     still posted, its record names the head the inventory read, and the return flags the move,
  //     per pull request and at run level, so the operator runs again.
  checks++
  {
    const gh = ghStub({ headNow: sha('e') })
    env = run({ prs: [101] }, PRS, { gh })
    result = await execute(env)
    const r = resultOf(101, result)
    if (r.outcome !== 'recorded' || gh.store.length !== 1) fail(`PR 101 with a moved head: outcome ${r.outcome}, ${gh.store.length} comment(s); a moved head must still be posted`)
    const last = commentOf(env, 101).split('\n').pop()
    if (last !== `Reviewed ${sha('1')} against ${FORK}: merge`) fail(`PR 101 with a moved head: the record is ${JSON.stringify(last)}, expected the head the inventory read`)
    if (r.headMoved !== true) fail(`PR 101 with a moved head: headMoved ${r.headMoved}, expected true`)
    if (JSON.stringify(result.headMoved) !== '[101]') fail(`a moved head is not flagged at run level: headMoved ${JSON.stringify(result.headMoved)}, expected [101]`)
    env = run({ prs: [101] }, PRS, { gh: ghStub() })
    result = await execute(env)
    if (resultOf(101, result).headMoved !== false || JSON.stringify(result.headMoved) !== '[]') fail(`PR 101 with the head unchanged: headMoved ${resultOf(101, result).headMoved}, run level ${JSON.stringify(result.headMoved)}`)
  }

  // 15. GitHub unreachable at posting time (#85): the outcome is unposted naming the file, the file
  //     holds the comment body, and nothing was posted. A rerun with gh reachable posts it once; a
  //     third run finds the record and posts nothing.
  checks++
  {
    const gh = ghStub({ failPost: true })
    env = run({ prs: [101] }, PRS, { gh })
    result = await execute(env)
    const r = resultOf(101, result)
    const file = `${env.repo}/.scratch/review-queue/log/2026-10-21/pr-101/comment.md`
    if (r.outcome !== 'unposted') fail(`PR 101 with GitHub unreachable: outcome ${r.outcome}, expected unposted`)
    if (r.commentFile !== file || !(r.stage || '').includes(file)) fail(`PR 101 unposted: commentFile ${r.commentFile}, stage ${JSON.stringify(r.stage)}; neither names ${file}`)
    const kept = fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : ''
    if (!kept.startsWith('## Review') || kept.split('\n').pop() !== `Reviewed ${sha('1')} against ${FORK}: merge`) fail(`PR 101 unposted: ${file} does not hold the comment body: ${JSON.stringify(kept.slice(0, 60))}`)
    if (gh.store.length) fail(`PR 101 unposted: ${gh.store.length} comment(s) reached GitHub`)
    // Nothing is retried: the post agent ran once, and the stub's failure stood.
    const postRuns = env.calls.filter((l) => l === 'post:101').length
    if (postRuns !== 1) fail(`PR 101 unposted: the post agent ran ${postRuns} time(s); an unreachable GitHub is never retried`)
    // An unreachable gh served no head, so no move is reported on it.
    if (r.headMoved !== false || JSON.stringify(result.headMoved) !== '[]') fail(`PR 101 unposted: headMoved ${r.headMoved}, run level ${JSON.stringify(result.headMoved)}; an unread head is not a moved one`)
    gh.failPost = false
    env = run({ prs: [101] }, PRS, { gh })
    result = await execute(env)
    if (resultOf(101, result).outcome !== 'recorded' || gh.store.length !== 1) fail(`PR 101's rerun with GitHub reachable: outcome ${resultOf(101, result).outcome}, ${gh.store.length} comment(s), expected recorded and one`)
    env = run({ prs: [101] }, PRS, { gh })
    result = await execute(env)
    if (resultOf(101, result).outcome !== 'skipped' || gh.store.length !== 1) fail(`PR 101's third run: outcome ${resultOf(101, result).outcome}, ${gh.store.length} comment(s), expected skipped and one`)
    // A resumed run: its inventory was replayed from before the post, so it does not see the record,
    // and its post agent runs again. It finds this run's record and posts nothing.
    gh.serveStore = false
    env = run({ prs: [101] }, PRS, { gh })
    result = await execute(env)
    const resumed = resultOf(101, result)
    if (gh.store.length !== 1 || resumed.outcome !== 'recorded' || resumed.recordUrl !== gh.store[0].entry.html_url) fail(`PR 101 resumed after its post: ${gh.store.length} comment(s), outcome ${resumed.outcome}, recordUrl ${resumed.recordUrl}; a resumed run posted twice or lost the record`)
  }
  // A post agent that posts nothing and names no comment has not recorded anything.
  {
    const gh = ghStub()
    gh.post = () => ({ posted: false, url: '', headNow: sha('1'), unreachable: false, bodyFile: '' })
    env = run({ prs: [101] }, PRS, { gh })
    result = await execute(env)
    if (resultOf(101, result).outcome !== 'failed') fail(`PR 101 with nothing posted and no URL: outcome ${resultOf(101, result).outcome}, expected failed`)
  }

  // 16. A held head re-run with skipRecorded off (#85): a new record is posted on it even when the
  //     verdict is hold again, the line the earlier record wrote, and the latest line decides.
  checks++
  {
    const HELD = { author_association: 'OWNER', created_at: '2026-10-20T09:00:00Z', html_url: 'https://github.com/o/r/pull/101#issuecomment-50', body: `## Review\n\nReviewed ${sha('1')} against ${FORK}: hold` }
    const served = { 101: { ...PRS[101], comments: [HELD] } }
    let gh = ghStub()
    env = run({ prs: [101], skipRecorded: false }, served, { gh, verdict: 'hold' })
    result = await execute(env)
    if (resultOf(101, result).outcome !== 'recorded' || gh.store.length !== 1) fail(`held PR 101 re-run with skipRecorded off and held again: outcome ${resultOf(101, result).outcome}, ${gh.store.length} new comment(s), expected one new record`)
    gh = ghStub()
    env = run({ prs: [101], skipRecorded: false }, served, { gh })
    result = await execute(env)
    if (gh.store.length !== 1 || gh.store[0].entry.body.split('\n').pop() !== `Reviewed ${sha('1')} against ${FORK}: merge`) fail(`held PR 101 re-run with skipRecorded off: ${gh.store.length} new comment(s), expected one merge record`)
    env = run({ prs: [101] }, served, { gh })
    result = await execute(env)
    const r = resultOf(101, result)
    if (r.outcome !== 'skipped' || r.verdict !== 'merge' || r.recordUrl !== (gh.store[0] || { entry: {} }).entry.html_url) fail(`PR 101 with a hold and a later merge on its head: ${r.outcome} ${r.verdict} ${r.recordUrl}; the latest line must decide`)
    // And the other way round: a merge then a later hold holds.
    const MERGED = { ...HELD, body: HELD.body.replace(': hold', ': merge') }
    const LATER = { ...HELD, created_at: '2026-10-21T09:00:00Z', html_url: 'u/later' }
    env = run({ prs: [101] }, { 101: { ...PRS[101], comments: [MERGED, LATER] } })
    result = await execute(env)
    if (resultOf(101, result).verdict !== 'hold' || resultOf(101, result).recordUrl !== 'u/later') fail(`PR 101 with a merge and a later hold: ${resultOf(101, result).verdict} ${resultOf(101, result).recordUrl}; the latest line must decide`)
  }

  // 17. The delta review (#85). The latest record names an ancestor of the head: both axes are given
  //     the range and the whole diff for context, with each merge in it to audit on its own; only the
  //     candidates the range touches are mutated, the rest named; the comment names the earlier record
  //     and the range after the head line; the new record names the current head.
  checks++
  const H_OLD = sha('a')
  const H_NEW = sha('d')
  const MERGE_SHA = sha('f')
  const OLD_RECORD = (verdict) => ({ author_association: 'OWNER', created_at: '2026-10-20T09:00:00Z', html_url: 'https://github.com/o/r/pull/207#issuecomment-70', body: `## Review\n\nReviewed ${H_OLD} against ${FORK}: ${verdict}` })
  const DELTA = (verdict) => ({ 207: pr(207, ['tests/Platform.Registry.Tests/Touched.cs', 'tests/Platform.Registry.Tests/Untouched.cs', 'src/Fixed.cs'], {
    head: H_NEW, commits: [H_NEW, MERGE_SHA, H_OLD], comments: [OLD_RECORD(verdict)],
    candidates: [cand('Touched'), cand('Untouched'), { ...cand('ViaSource'), breaks: 'src/Fixed.cs' }],
  }) })
  const RANGE = { 207: { from: H_OLD, ancestor: true, commits: [H_NEW, MERGE_SHA], merges: [MERGE_SHA], changedFiles: ['tests/Platform.Registry.Tests/Touched.cs', 'src/Fixed.cs'], error: '' } }
  {
    const gh = ghStub()
    env = run({ prs: [207] }, DELTA('merge'), { gh, range: RANGE })
    result = await execute(env)
    const r = resultOf(207, result)
    if (!env.calls.includes('range:207')) fail('PR 207: no range agent ran for a record of an earlier head')
    if (r.outcome !== 'recorded') fail(`PR 207 delta: outcome ${r.outcome}`)
    const ran = env.mutations.map((m) => m.test).sort()
    if (JSON.stringify(ran) !== '["Touched","ViaSource"]') fail(`PR 207 delta: mutated ${JSON.stringify(ran)}, expected only the candidates the range touches, ["Touched","ViaSource"]`)
    const body = commentOf(env, 207)
    const lines = body.split('\n')
    if (lines.pop() !== `Reviewed ${H_NEW} against ${FORK}: merge`) fail('PR 207 delta: the new record does not name the current head')
    const headAt = lines.findIndex((l) => l.startsWith(`Head ${H_NEW}`))
    const deltaAt = lines.findIndex((l) => l.startsWith('Delta review:'))
    if (deltaAt < 0 || deltaAt !== headAt + 1) fail(`PR 207 delta: no "Delta review:" line straight after the head line (head at ${headAt}, delta at ${deltaAt})`)
    else {
      const d = lines[deltaAt]
      for (const want of [OLD_RECORD('merge').html_url, `${H_OLD}..${H_NEW}`, MERGE_SHA]) if (!d.includes(want)) fail(`PR 207 delta: the delta line ${JSON.stringify(d)} does not name ${want}`)
    }
    const outside = lines.find((l) => /^Not mutated, outside the range/.test(l)) || ''
    if (!outside.includes('Untouched') || outside.includes('Touched,') || /\bTouched\b/.test(outside.replace('Untouched', ''))) fail(`PR 207 delta: the outside-the-range line ${JSON.stringify(outside)} does not name Untouched alone`)
    if (JSON.stringify(r.outsideRange) !== '["Untouched"]') fail(`PR 207 delta: outsideRange ${JSON.stringify(r.outsideRange)}`)
    if (!r.earlierRecord || r.earlierRecord.url !== OLD_RECORD('merge').html_url || r.earlierRecord.head !== H_OLD) fail(`PR 207 delta: earlierRecord ${JSON.stringify(r.earlierRecord)}`)
    if (!r.range || r.range.from !== H_OLD || r.range.to !== H_NEW) fail(`PR 207 delta: range ${JSON.stringify(r.range)}`)
    for (const axis of ['standards', 'spec']) {
      const p = env.prompts[`${axis}:207`] || ''
      for (const want of [`${H_OLD}..${H_NEW}`, `${FORK}...${H_NEW}`, MERGE_SHA, OLD_RECORD('merge').html_url]) if (!p.includes(want)) fail(`PR 207 delta: the ${axis} prompt does not name ${want}`)
    }
  }
  // A held earlier record: its findings are checked for an answer in the range; one left unanswered holds.
  {
    env = run({ prs: [207] }, DELTA('hold'), { range: RANGE, earlier: { 207: [{ finding: 'the cap is off by one', answered: true, how: 'fixed in ' + H_NEW }] } })
    result = await execute(env)
    const p = env.prompts['verdict:207'] || ''
    if (!/held/.test(p) || !p.includes(OLD_RECORD('hold').html_url)) fail('PR 207 after a hold: the verdict is not told to check the held record\'s findings for an answer')
    if (resultOf(207, result).verdict !== 'merge') fail(`PR 207 with the earlier hold answered: verdict ${resultOf(207, result).verdict}, expected merge`)
    if (!/the cap is off by one/.test(commentOf(env, 207))) fail('PR 207 after a hold: the comment does not carry the earlier finding and its answer')
    env = run({ prs: [207] }, DELTA('hold'), { range: RANGE, earlier: { 207: [{ finding: 'the cap is off by one', answered: false, how: 'nothing in the range touches it' }] } })
    result = await execute(env)
    if (resultOf(207, result).verdict !== 'hold') fail(`PR 207 with the earlier hold unanswered: verdict ${resultOf(207, result).verdict}, expected hold`)
    // A follow-up the held record carried without holding on it does not hold when the range leaves it.
    env = run({ prs: [207] }, DELTA('hold'), { range: RANGE, earlier: { 207: [{ finding: 'the cap is off by one', held: true, answered: true, how: 'fixed' }, { finding: 'an ADR index row', held: false, answered: false, how: 'the range touches no ADR' }] } })
    result = await execute(env)
    if (resultOf(207, result).verdict !== 'merge') fail(`PR 207 with only a carried follow-up unanswered: verdict ${resultOf(207, result).verdict}, expected merge`)
    if (!/held true when that record held on it/.test(env.prompts['verdict:207'] || '')) fail('PR 207 after a hold: the verdict is not told to mark which earlier findings held')
    env = run({ prs: [207] }, DELTA('hold'), { range: RANGE, earlier: { 207: [{ finding: 'the cap is off by one', held: true, answered: false, how: 'untouched' }] } })
    result = await execute(env)
    if (resultOf(207, result).verdict !== 'hold') fail(`PR 207 with a held finding unanswered: verdict ${resultOf(207, result).verdict}, expected hold`)
    // A verdict that lists none of the held record's findings has checked none of them: it holds.
    env = run({ prs: [207] }, DELTA('hold'), { range: RANGE, earlier: { 207: [] } })
    result = await execute(env)
    if (resultOf(207, result).verdict !== 'hold') fail(`PR 207 after a hold with no earlier finding listed: verdict ${resultOf(207, result).verdict}, expected hold`)
    if (!/listed none, so the hold stands/.test(commentOf(env, 207))) fail('PR 207 after a hold with no earlier finding listed: the comment does not say the hold stands')
  }
  // Not an ancestor (a force-push), or no range at all: a full review, and the comment says why.
  for (const [what, range] of [['not an ancestor', { 207: { ...RANGE[207], ancestor: false } }], ['the range agent returned nothing', { 207: 'null' }]]) {
    env = run({ prs: [207] }, DELTA('merge'), { range })
    result = await execute(env)
    const r = resultOf(207, result)
    if (env.mutations.length !== 3) fail(`PR 207 with ${what}: ${env.mutations.length} mutations, expected the full review's 3`)
    if (r.range !== null || r.earlierRecord !== null) fail(`PR 207 with ${what}: range ${JSON.stringify(r.range)}, earlierRecord ${JSON.stringify(r.earlierRecord)}, expected null`)
    const full = commentOf(env, 207).split('\n').find((l) => l.startsWith('Full review:')) || ''
    if (!full.includes(OLD_RECORD('merge').html_url)) fail(`PR 207 with ${what}: no "Full review:" line naming the earlier record`)
  }

  // 17a. A rebuilt head (#293). The latest record names a head that is no ancestor of this one, as on
  //      #287 where each rebuild of the run branch cost a full review. The range agent maps the old
  //      commits onto the new with git range-diff and reports, per new commit, the old one whose tree
  //      it equals. Two of the three new commits map with equal trees: the review is a delta over the
  //      one that does not, the comment says it was a rebuild and names the mapping, and only the
  //      candidates the content diff from the recorded head touches are mutated. A rebuild that maps
  //      no commit, or whose range nobody could read, is a full review, as before.
  checks++
  {
    const OLD_FIRST = sha('4')
    const NEW_FIRST = sha('5'); const NEW_MID = sha('6'); const NEW_HEAD = sha('8')
    // The recorded head forked from an older main than the rebuild did, so range-diff is asked for
    // the record's own fork point, not the head's.
    const OLD_BASE = sha('9')
    const REBUILT = (verdict) => ({ 207: { ...DELTA(verdict)[207], head: NEW_HEAD, commits: [NEW_HEAD, NEW_MID, NEW_FIRST],
      comments: [{ ...OLD_RECORD(verdict), body: `## Review\n\nReviewed ${H_OLD} against ${OLD_BASE}: ${verdict}` }] } })
    const mapping = [{ commit: NEW_HEAD, equals: '' }, { commit: NEW_MID, equals: H_OLD }, { commit: NEW_FIRST, equals: OLD_FIRST }]
    // Two of the head's commits are merges: the unmapped one is audited on its own, the mapped one not.
    const REB = { 207: { from: H_OLD, ancestor: false, commits: [], merges: [NEW_HEAD, NEW_MID], since: [], error: '',
      changedFiles: ['tests/Platform.Registry.Tests/Touched.cs', 'src/Fixed.cs'], mapping } }
    env = run({ prs: [207] }, REBUILT('merge'), { range: REB })
    result = await execute(env)
    const r = resultOf(207, result)
    const rp = env.prompts['range:207'] || ''
    // range-diff prints abbreviated shas and rebuildOf takes only full ones, so the prompt must ask for them.
    if (!/full 40-hex sha[^.]*rev-parse <old>/.test(rp)) fail('a rebuild: the range agent is not asked for the full sha of each old commit, so range-diff\'s abbreviated ones fail every mapping')
    if (!rp.includes(`range-diff ${OLD_BASE}..${H_OLD} ${FORK}..${NEW_HEAD}`)) fail('a rebuild: the range agent is not asked to map the old commits onto the new with git range-diff from the record\'s fork point')
    if (!/\^\{tree\}/.test(rp)) fail('a rebuild: the range agent is not asked to compare trees, only patches')
    if (r.outcome !== 'recorded') fail(`a rebuild: outcome ${r.outcome}`)
    const ran = env.mutations.map((m) => m.test).sort()
    if (JSON.stringify(ran) !== '["Touched","ViaSource"]') fail(`a rebuild: mutated ${JSON.stringify(ran)}, expected the delta's ["Touched","ViaSource"]`)
    if (JSON.stringify(r.outsideRange) !== '["Untouched"]') fail(`a rebuild: outsideRange ${JSON.stringify(r.outsideRange)}`)
    if (!r.range || !r.range.rebuilt || JSON.stringify(r.range.commits) !== JSON.stringify([NEW_HEAD])) fail(`a rebuild: range ${JSON.stringify(r.range)}, expected a rebuilt range over ${NEW_HEAD} alone`)
    if (!r.earlierRecord || r.earlierRecord.head !== H_OLD) fail(`a rebuild: earlierRecord ${JSON.stringify(r.earlierRecord)}`)
    if (!r.range || JSON.stringify(r.range.merges) !== JSON.stringify([NEW_HEAD])) fail(`a rebuild: merges ${JSON.stringify(r.range && r.range.merges)}, expected the unmapped merge ${NEW_HEAD} alone`)
    const lines = commentOf(env, 207).split('\n')
    const headAt = lines.findIndex((l) => l.startsWith(`Head ${NEW_HEAD}`))
    const deltaAt = lines.findIndex((l) => l.startsWith('Delta review:'))
    if (deltaAt < 0 || deltaAt !== headAt + 1) fail(`a rebuild: no "Delta review:" line straight after the head line (head at ${headAt}, delta at ${deltaAt})`)
    const scope = lines.slice(deltaAt, deltaAt + 2).join('\n')
    for (const want of ['rebuil', OLD_RECORD('merge').html_url, `${NEW_MID} = ${H_OLD}`, `${NEW_FIRST} = ${OLD_FIRST}`, NEW_HEAD]) if (!scope.includes(want)) fail(`a rebuild: the scope lines ${JSON.stringify(scope)} do not name ${want}`)
    if (lines.some((l) => l.startsWith('Full review:'))) fail('a rebuild that maps two of three commits: the comment still says full review')
    for (const axis of ['standards', 'spec']) {
      const p = env.prompts[`${axis}:207`] || ''
      if (!p.includes(NEW_HEAD) || !p.includes(`diff ${H_OLD} ${NEW_HEAD}`)) fail(`a rebuild: the ${axis} prompt does not name the unmapped commit and the content diff from the recorded head`)
      // from..to over a head that is no descendant lists the whole branch, so the rebuild never says it.
      if (p.includes(`${H_OLD}..${NEW_HEAD}`)) fail(`a rebuild: the ${axis} prompt reviews the range ${H_OLD}..${NEW_HEAD}, which is the whole branch when the recorded head is no ancestor`)
      if (p.includes(NEW_MID + ' ') && !p.includes(`${NEW_MID} = ${H_OLD}`)) fail(`a rebuild: the ${axis} prompt asks for review of the mapped ${NEW_MID}`)
    }
    // A held record's findings are still checked in a rebuild; one left unanswered holds.
    env = run({ prs: [207] }, REBUILT('hold'), { range: REB, earlier: { 207: [{ finding: 'the cap is off by one', answered: false, how: 'the unmapped commit leaves it' }] } })
    result = await execute(env)
    if (resultOf(207, result).verdict !== 'hold') fail(`a rebuild after a hold left unanswered: verdict ${resultOf(207, result).verdict}, expected hold`)
    // Every commit maps: a delta over nothing new, which still names the mapping.
    const all = { 207: { ...REB[207], changedFiles: [], mapping: mapping.map((m) => ({ ...m, equals: m.equals || H_OLD })) } }
    env = run({ prs: [207] }, REBUILT('merge'), { range: all })
    result = await execute(env)
    if (!resultOf(207, result).range || resultOf(207, result).range.commits.length !== 0 || env.mutations.length) fail(`a rebuild whose every commit maps: range ${JSON.stringify(resultOf(207, result).range)}, ${env.mutations.length} mutation(s); expected a delta over no commit and none`)
    // Full reviews: no commit maps; the range could not be read; the mapping misses a commit of the head.
    for (const [what, rg] of [
      ['maps no commit', { ...REB[207], mapping: mapping.map((m) => ({ ...m, equals: '' })) }],
      ['an unreadable range', { ...REB[207], error: 'range-diff: bad revision' }],
      ['a mapping that misses a commit of the head', { ...REB[207], mapping: mapping.slice(1) }],
      ['a mapping that names no sha', { ...REB[207], mapping: [{ commit: NEW_HEAD, equals: '' }, { commit: NEW_MID, equals: 'yes' }, { commit: NEW_FIRST, equals: '' }] }],
    ]) {
      env = run({ prs: [207] }, REBUILT('merge'), { range: { 207: rg } })
      result = await execute(env)
      const f = resultOf(207, result)
      if (env.mutations.length !== 3) fail(`a rebuild with ${what}: ${env.mutations.length} mutations, expected the full review's 3`)
      if (f.range !== null) fail(`a rebuild with ${what}: range ${JSON.stringify(f.range)}, expected null`)
      const full = commentOf(env, 207).split('\n').find((l) => l.startsWith('Full review:')) || ''
      if (!full.includes(OLD_RECORD('merge').html_url)) fail(`a rebuild with ${what}: no "Full review:" line naming the earlier record`)
    }
  }

  // 17b. A full review after a hold (#289), in #287's shape: the latest record held on a head that is
  //      no ancestor of this one, its Mutations table naming two LibraryTests cases in one did-not-fail
  //      cell; two `Review:` commits since the fork point; a Dockerfile candidate that record reproduced
  //      and nothing since touches, and a WebStorage candidate an older record reproduced, likewise
  //      untouched. The held tests are mutated first, under the stated rule, by the break the record
  //      named; the candidate a `Review:` commit touches comes next, ahead of one listed before it; the
  //      two reproduced candidates are skipped and named as cleared by their records; the cap's cut is
  //      still named.
  checks++
  {
    const H1 = sha('1'); const H2 = sha('2'); const H3 = sha('3')
    const LIB1 = 'LibraryTests.test_a_name_inside_a_longer_name_is_not_a_use'
    const LIB2 = 'LibraryTests.test_a_name_that_ends_a_sentence_is_a_use'
    const DOCKER = 'MockApiDockerfileTests.The_build_stage_opens_the_published_tree_after_publish_and_before_the_final_stage'
    const STORAGE = 'WebStorageTests.Empty_storage_passes_the_check'
    const LIB_BREAK = 'tools/check.sh users_of(): added `[ "$name" = lib.sh ] \\| return 2`, so the run ends early'
    const table = (rows) => ['## Mutations', '', TABLE_HEAD, '| --- | --- | --- | --- | --- |',
      ...rows.map(([t, broken, counts, result]) => `| ${t} | ${broken} | ${counts} | ${result} | /log/mutation.txt |`)].join('\n')
    const record = (url, at, head, verdict, rows) => ({ author_association: 'OWNER', created_at: at, html_url: url,
      body: `## Review\n\nHead ${head}.\n\n${table(rows)}\n\nNot mutated, cut by the cap of 3: Other.\n\n## Outstanding\n\n- Nothing.\n\nReviewed ${head} against ${FORK}: ${verdict}` })
    const OLDER = record('https://github.com/o/r/pull/287#issuecomment-1', '2026-10-22T08:00:00Z', H1, 'hold', [
      [STORAGE, 'replaced the early return in AssertEmpty', '2 of 111', 'reproduced: it went red'],
    ])
    const HELD = record('https://github.com/o/r/pull/287#issuecomment-2', '2026-10-22T09:00:00Z', H2, 'hold', [
      [`tools/check_tests.py: ${LIB1} (first half) and ${LIB2} (second half)`, LIB_BREAK, '3 of 71', 'did-not-fail: both named tests stayed green'],
      [DOCKER, 'deleted the chmod line from fixtures/MockApi/Dockerfile', '2 of 199', 'reproduced: it failed at line 31'],
    ])
    const lib = (test) => ({ test, project: 'tools/check_tests.py', file: 'tools/check_tests.py', guards: 'a library use', breaks: 'tools/check.sh' })
    const SHAPE = { 287: pr(287, ['tools/check_tests.py', 'tools/check.sh', 'fixtures/MockApi/Dockerfile', 'tests/Platform.Registry.Tests/ReviewFixed.cs'], {
      head: H3, commits: [H3, sha('e'), sha('f')], comments: [OLDER, HELD],
      reviewCommits: [
        { sha: sha('e'), subject: 'Review: assert the status in the lib.sh halves', files: ['tools/check_tests.py'] },
        { sha: sha('f'), subject: 'Review: tighten ReviewFixed', files: ['tests/Platform.Registry.Tests/ReviewFixed.cs'] },
      ],
      candidates: [
        { test: DOCKER, project: 'fixtures/Fixtures.Tests', file: 'fixtures/Fixtures.Tests/MockApiDockerfileTests.cs', guards: 'the chmod step', breaks: 'fixtures/MockApi/Dockerfile' },
        { test: STORAGE, project: 'tests/e2e', file: 'tests/e2e/WebStorageTests.cs', guards: 'empty storage passes', breaks: 'tests/e2e/Stack/WebStorage.cs' },
        cand('Filler'), lib(LIB1), lib(LIB2), cand('ReviewFixed'),
      ],
    }) }
    // The range since the held head: not an ancestor (the run branch was rebuilt); what changed since
    // each recorded head is read all the same, and neither reproduced candidate's files are in it.
    const range = { 287: { from: H2, ancestor: false, commits: [], merges: [], error: '',
      changedFiles: ['tools/check_tests.py', 'tools/check.sh', 'tests/Platform.Registry.Tests/ReviewFixed.cs'],
      since: [{ head: H1, changedFiles: ['tools/check_tests.py', 'fixtures/MockApi/Dockerfile'], error: '' }] } }
    env = run({ prs: [287] }, SHAPE, { range })
    result = await execute(env)
    const r = resultOf(287, result)
    const ran = env.mutations.map((m) => m.test)
    if (JSON.stringify(ran) !== JSON.stringify([LIB1, LIB2, 'ReviewFixed'])) fail(`PR 287's shape: mutated ${JSON.stringify(ran)}, expected the held tests first, then the candidate a Review: commit touches: ${JSON.stringify([LIB1, LIB2, 'ReviewFixed'])}`)
    for (const m of env.mutations.filter((x) => x.test === LIB1 || x.test === LIB2)) {
      if (!m.prompt.includes(LIB_BREAK.replace('\\|', '|'))) fail(`PR 287's shape: the held mutation of ${m.test} is not told the break the held record named`)
      if (!m.prompt.includes(HELD.html_url)) fail(`PR 287's shape: the held mutation of ${m.test} does not name the held record`)
    }
    if (!(env.prompts['range:287'] || '').includes(H1)) fail('PR 287\'s shape: the range agent is not asked what changed since the older record\'s head')
    const body = commentOf(env, 287)
    const lines = body.split('\n')
    const cleared = lines.find((l) => /^Not mutated, cleared by/.test(l)) || ''
    if (!cleared.includes(DOCKER) || !cleared.includes(HELD.html_url)) fail(`PR 287's shape: the cleared line ${JSON.stringify(cleared)} does not name the Dockerfile candidate and the record that reproduced it`)
    if (!cleared.includes(STORAGE) || !cleared.includes(OLDER.html_url)) fail(`PR 287's shape: the cleared line ${JSON.stringify(cleared)} does not name the WebStorage candidate and the older record that reproduced it`)
    if (JSON.stringify((r.clearedByEarlier || []).map((c) => c.test).sort()) !== JSON.stringify([DOCKER, STORAGE].sort())) fail(`PR 287's shape: clearedByEarlier ${JSON.stringify(r.clearedByEarlier)}`)
    const cutLine = lines.find((l) => /^Not mutated, cut by the cap/.test(l)) || ''
    if (!cutLine.includes('Filler') || cutLine.includes(DOCKER)) fail(`PR 287's shape: the cap's line ${JSON.stringify(cutLine)} does not name Filler alone`)
    if (JSON.stringify(r.notMutated) !== '["Filler"]') fail(`PR 287's shape: notMutated ${JSON.stringify(r.notMutated)}, expected ["Filler"]`)
    if (r.verdict !== 'merge') fail(`PR 287's shape with every held test reproduced: verdict ${r.verdict}, expected merge`)
    // A held test that does not reproduce holds, under the stated rule: did-not-fail and could-not-run both.
    for (const res of ['did-not-fail', 'could-not-run']) {
      env = run({ prs: [287] }, SHAPE, { range, mutate: { [LIB2]: row(res) } })
      result = await execute(env)
      if (resultOf(287, result).verdict !== 'hold') fail(`PR 287's shape with the held ${LIB2} ${res}: verdict ${resultOf(287, result).verdict}, expected hold`)
    }
    // removed (#334): a held test whose break the branch deleted holds nothing in the script, so the
    // verdict is the agent's, and the agent is told to hold unless the deletion is shown sound.
    env = run({ prs: [287] }, SHAPE, { range, mutate: { [LIB2]: { ...row('removed', 0, 0), why: 'commit abc1234 removed the unreachable fail branch' } } })
    result = await execute(env)
    {
      const c = resultOf(287, result)
      const held = (c.mutations || []).find((m) => m.test === LIB2) || {}
      if (held.result !== 'removed') fail(`PR 287's shape with the held ${LIB2} removed: its row is ${JSON.stringify(held.result)}, expected removed`)
      if (c.verdict !== 'merge') fail(`PR 287's shape with the held ${LIB2} removed: verdict ${c.verdict}; the script must not hold on a removed row`)
      const verdictPrompt = env.prompts['verdict:287'] || ''
      if (!/A removed row is a held break whose target the range deleted/.test(verdictPrompt) || !/otherwise, or if you cannot show either, the held test is unanswered and the verdict is hold/.test(verdictPrompt)) fail('the verdict prompt does not tell the agent to hold on a removed row whose deletion is not shown sound')
      if (!new RegExp(`${LIB2}[^\\n]*removed`).test(commentOf(env, 287))) fail('the comment\'s table does not show the removed row')
    }
    // caught (#684): the held break turns the project red on another test while the held one stays
    // green. Accepted by the verdict, the row is reproduced and its cell names both tests, so a later
    // review's held list drops it; not accepted, it holds and reads did-not-fail.
    const CATCHER = 'LibraryTests.test_a_name_inside_a_longer_name_is_still_read'
    const caught = { [LIB2]: { ...row('reproduced', 1, 71), failingNames: [`tools.check_tests.${CATCHER}`], why: 'the break is caught by the catcher, not by LIB2' } }
    env = run({ prs: [287] }, SHAPE, { range, mutate: caught, caught: { 287: [LIB2] } })
    result = await execute(env)
    {
      const c = resultOf(287, result)
      const held = (c.mutations || []).find((m) => m.test === LIB2) || {}
      if (held.result !== 'reproduced' || JSON.stringify(held.caughtBy) !== JSON.stringify([`tools.check_tests.${CATCHER}`])) fail(`PR 287's shape with the held ${LIB2} caught and accepted: its row is ${JSON.stringify(held)}`)
      if (c.verdict !== 'merge') fail(`PR 287's shape with the held ${LIB2} caught and accepted: verdict ${c.verdict}, expected merge`)
      if (!/A caught row is a held break the project caught through other tests/.test(env.prompts['verdict:287'] || '')) fail('the verdict prompt does not tell the agent how to judge a caught row')
      if (!/stayed green, the result is reproduced all the same/.test((env.mutations.find((m) => m.test === LIB2) || {}).prompt || '')) fail('the held mutation prompt does not say a break caught by another test is reproduced')
      const cellLine = commentOf(env, 287).split('\n').find((l) => l.startsWith(`| ${LIB2} (caught by `)) || ''
      if (!cellLine.includes(CATCHER)) fail(`PR 287's shape with the held ${LIB2} caught: no table row reads "${LIB2} (caught by ...)"`)
      // A later review on a new head, the caught record now the latest: LIB2 is answered and not mutated
      // again. Its row is the cell the script wrote above, so the round trip reads what a record carries.
      const LATER = { author_association: 'OWNER', created_at: '2026-10-22T10:00:00Z', html_url: 'https://github.com/o/r/pull/287#issuecomment-3',
        body: `## Review\n\nHead ${H3}.\n\n${table([[LIB1, LIB_BREAK, '3 of 71', 'did-not-fail: it stayed green']])}\n${cellLine}\n\n## Outstanding\n\n- Nothing.\n\nReviewed ${H3} against ${FORK}: hold` }
      const NEXT = { 287: { ...SHAPE[287], head: sha('4'), commits: [sha('4')], comments: [OLDER, HELD, LATER] } }
      env = run({ prs: [287] }, NEXT, { range: { 287: { ...range[287], from: H3 } } })
      result = await execute(env)
      const ranNext = env.mutations.filter((m) => m.prompt.includes('held because')).map((m) => m.test)
      if (ranNext.includes(LIB2) || !ranNext.includes(LIB1)) fail(`PR 287 after a caught record: held mutations ${JSON.stringify(ranNext)}, expected ${LIB1} and not ${LIB2}`)
    }
    env = run({ prs: [287] }, SHAPE, { range, mutate: caught })
    result = await execute(env)
    {
      const c = resultOf(287, result)
      const held = (c.mutations || []).find((m) => m.test === LIB2) || {}
      if (c.verdict !== 'hold' || held.result !== 'did-not-fail') fail(`PR 287's shape with the held ${LIB2} caught and not accepted: verdict ${c.verdict}, row ${held.result}, expected hold and did-not-fail`)
      if (commentOf(env, 287).includes('(caught by ')) fail('a caught row the verdict did not accept still reads "caught by" in the table')
    }
    // A held test that went red itself is an ordinary reproduction, whatever else failed beside it: it
    // is no catch, needs no acceptance and merges. A same-named method of another class is a catch,
    // which, unaccepted, reads did-not-fail and holds.
    for (const [names, want, verdict] of [
      [[`tools.check_tests.${LIB2}`, `tools.check_tests.${CATCHER}`], 'reproduced', 'merge'],
      [[`tools.check_tests.${LIB2}(case 1)`], 'reproduced', 'merge'],
      [[`tools.check_tests.OtherTests.${LIB2.split('.').pop()}`], 'did-not-fail', 'hold'],
    ]) {
      env = run({ prs: [287] }, SHAPE, { range, mutate: { [LIB2]: { ...row('reproduced', names.length, 71), failingNames: names } } })
      result = await execute(env)
      const c = resultOf(287, result)
      const held = (c.mutations || []).find((m) => m.test === LIB2) || {}
      if (held.result !== want || c.verdict !== verdict) fail(`PR 287's shape with the held ${LIB2} failing as ${JSON.stringify(names)}: row ${held.result}, verdict ${c.verdict}, expected ${want} and ${verdict}`)
    }
    // A held test the cap cuts stays unanswered, so it holds, and the cap's line still names it: with a
    // cap of 1 only the first held test runs and reproduces, and the second may not let the review merge.
    env = run({ prs: [287], maxMutationsPerPr: 1 }, SHAPE, { range })
    result = await execute(env)
    {
      const c = resultOf(287, result)
      if (JSON.stringify(env.mutations.map((m) => m.test)) !== JSON.stringify([LIB1])) fail(`PR 287's shape with a cap of 1: mutated ${JSON.stringify(env.mutations.map((m) => m.test))}, expected ${LIB1} alone`)
      if (!(c.notMutated || []).includes(LIB2)) fail(`PR 287's shape with a cap of 1: notMutated ${JSON.stringify(c.notMutated)} does not name the cut held test`)
      if (c.verdict !== 'hold') fail(`PR 287's shape with a cap of 1: verdict ${c.verdict} with the held ${LIB2} cut by the cap, expected hold`)
    }
    // A reproduced candidate whose source changed since the record is mutated again, not cleared.
    const moved = { 287: { ...range[287], changedFiles: [...range[287].changedFiles, 'fixtures/MockApi/Dockerfile'] } }
    env = run({ prs: [287], maxMutationsPerPr: 9 }, SHAPE, { range: moved })
    result = await execute(env)
    if (!env.mutations.some((m) => m.test === DOCKER)) fail('PR 287\'s shape with the Dockerfile changed since the held record: the Dockerfile candidate was not mutated')
    if ((resultOf(287, result).clearedByEarlier || []).some((c) => c.test === DOCKER)) fail('PR 287\'s shape with the Dockerfile changed since the held record: it is still listed as cleared')
    // No range read: nothing is cleared, since what changed is unknown.
    env = run({ prs: [287], maxMutationsPerPr: 9 }, SHAPE, { range: { 287: 'null' } })
    result = await execute(env)
    if ((resultOf(287, result).clearedByEarlier || []).length) fail(`PR 287's shape with no range read: cleared ${JSON.stringify(resultOf(287, result).clearedByEarlier)}, expected nothing`)
    if (JSON.stringify(env.mutations.slice(0, 2).map((m) => m.test)) !== JSON.stringify([LIB1, LIB2])) fail('PR 287\'s shape with no range read: the held tests are not first')
    // A held cell that names no candidate is still mutated first, by its own name.
    const LONE = { 287: { ...SHAPE[287], candidates: SHAPE[287].candidates.filter((c) => !c.test.startsWith('LibraryTests')) } }
    env = run({ prs: [287] }, LONE, { range })
    await execute(env)
    if (!(env.mutations[0] || { test: '' }).test.includes(LIB1)) fail(`PR 287's shape with no LibraryTests candidate: the first mutation is ${JSON.stringify((env.mutations[0] || {}).test)}, expected the held cell itself`)
    // A delta review after the same hold: the held tests are not "outside the range" when the range
    // leaves their files alone; they are mutated first all the same.
    const delta = { 287: { ...range[287], ancestor: true, commits: [H3], changedFiles: ['tests/Platform.Registry.Tests/ReviewFixed.cs'] } }
    env = run({ prs: [287] }, SHAPE, { range: delta, earlier: { 287: [{ finding: 'the lib.sh halves cannot fail', answered: true, how: 'asserted' }] } })
    result = await execute(env)
    if (JSON.stringify(env.mutations.slice(0, 2).map((m) => m.test)) !== JSON.stringify([LIB1, LIB2])) fail(`PR 287's shape as a delta: mutated ${JSON.stringify(env.mutations.map((m) => m.test))}, expected the held tests first`)
    if ((resultOf(287, result).outsideRange || []).some((t) => t === LIB1 || t === LIB2)) fail('PR 287\'s shape as a delta: a held test is listed as outside the range, cleared by the record that held on it')
    // #287 as it happened: the first record held on the LibraryTests cases, the second held on
    // something else and mutated neither, and the third review is the one that must reach them. The
    // did-not-fail rows of the older held record are carried forward, since no later record reproduced them.
    const FIRST = record('https://github.com/o/r/pull/287#issuecomment-6340588449', '2026-10-22T08:00:00Z', H1, 'hold', [
      [`tools/check_tests.py: ${LIB1} (first half) and ${LIB2} (second half)`, LIB_BREAK, '3 of 71', 'did-not-fail: both named tests stayed green'],
      [STORAGE, 'replaced the early return in AssertEmpty', '2 of 111', 'reproduced: it went red'],
    ])
    const SECOND = record('https://github.com/o/r/pull/287#issuecomment-6342205384', '2026-10-22T09:00:00Z', H2, 'hold', [
      [DOCKER, 'deleted the chmod line from fixtures/MockApi/Dockerfile', '2 of 199', 'reproduced: it failed at line 31'],
    ])
    const CHAIN = { 287: { ...SHAPE[287], comments: [FIRST, SECOND] } }
    env = run({ prs: [287] }, CHAIN, { range })
    result = await execute(env)
    if (JSON.stringify(env.mutations.map((m) => m.test)) !== JSON.stringify([LIB1, LIB2, 'ReviewFixed'])) fail(`#287 as it happened: mutated ${JSON.stringify(env.mutations.map((m) => m.test))}, expected the first record's held tests first`)
    if (!(env.mutations[0] || { prompt: '' }).prompt.includes(FIRST.html_url)) fail('#287 as it happened: the held mutation does not name the record that held on it')
    // A later record that reproduced a held test answers it: it is not carried forward.
    const ANSWERED = { 287: { ...SHAPE[287], comments: [FIRST, record('https://github.com/o/r/pull/287#issuecomment-3', '2026-10-22T09:00:00Z', H2, 'hold', [
      [LIB1, 'the same break', '1 of 71', 'reproduced: it went red'],
    ])] } }
    env = run({ prs: [287], maxMutationsPerPr: 9 }, ANSWERED, { range })
    await execute(env)
    const kinds = Object.fromEntries(env.mutations.map((m) => [m.test, /The review record \S+ held because/.test(m.prompt) ? 'held' : 'other']))
    if (kinds[LIB1] !== 'other' || kinds[LIB2] !== 'held') fail(`a held test a later record reproduced: kinds ${JSON.stringify(kinds)}, expected ${LIB2} held and ${LIB1} not`)
    // A later record's removed row answers the held test as a reproduced one does (#334).
    const REMOVED = { 287: { ...SHAPE[287], comments: [FIRST, record('https://github.com/o/r/pull/287#issuecomment-4', '2026-10-22T09:00:00Z', H2, 'hold', [
      [LIB1, 'the same break', '0 of 0', 'removed: commit abc1234 deleted the unreachable branch'],
    ])] } }
    env = run({ prs: [287], maxMutationsPerPr: 9 }, REMOVED, { range })
    await execute(env)
    const removedKinds = Object.fromEntries(env.mutations.map((m) => [m.test, /The review record \S+ held because/.test(m.prompt) ? 'held' : 'other']))
    if (removedKinds[LIB1] !== 'other') fail(`a held test a later record found removed: kinds ${JSON.stringify(removedKinds)}, expected ${LIB1} answered`)
    // A range that read no changed file at all clears nothing: an empty list is no evidence.
    env = run({ prs: [287], maxMutationsPerPr: 9 }, SHAPE, { range: { 287: { ...range[287], changedFiles: [], since: [{ head: H1, changedFiles: [], error: '' }] } } })
    result = await execute(env)
    if ((resultOf(287, result).clearedByEarlier || []).length) fail(`PR 287's shape with an empty diff read: cleared ${JSON.stringify(resultOf(287, result).clearedByEarlier)}, expected nothing`)
  }

  // 18. The open queue (#86), empty: launched with no list, the queue agent runs, the stubbed gh lists
  //     no open pull request into main, and the run returns an empty list, posts nothing and starts
  //     no other agent. The return still says what the run saw: date, base tip, agents, log directory.
  checks++
  {
    env = run({ prs: undefined }, PRS, { queue: { baseTip: sha('c'), prs: [], error: '' } })
    result = await execute(env)
    if (!result || result.halted) fail(`an empty queue: the run halted or returned nothing: ${JSON.stringify(result)}`)
    else {
      if (JSON.stringify(result.prs) !== '[]') fail(`an empty queue: prs ${JSON.stringify(result.prs)}, expected []`)
      if (env.posted.length) fail(`an empty queue: ${env.posted.length} comment(s) posted`)
      if (JSON.stringify(env.calls) !== '["queue"]') fail(`an empty queue: agents ${JSON.stringify(env.calls)}, expected the queue agent alone`)
      if (result.agents !== 1) fail(`an empty queue: agents ${result.agents}, expected 1, the queue agent`)
      if (result.date !== '2026-10-21' || result.baseTip !== sha('c') || result.logDir !== `${env.repo}/.scratch/review-queue/log/2026-10-21`) fail(`an empty queue: date ${result.date}, baseTip ${result.baseTip}, logDir ${result.logDir}`)
    }
  }

  // 19. The open queue, with pull requests in it: each one into main is reviewed and posted, a row of
  //     another base is not, and the base tip reported is the one the queue read. With a named list
  //     the base tip is the inventory's. A queue agent that returns nothing is not an empty queue: the
  //     run halts saying so and posts nothing.
  checks++
  {
    const TIP = sha('9')
    const queue = { baseTip: TIP, prs: [{ number: 101, head: sha('1'), baseRef: 'main' }, { number: 102, head: sha('2'), baseRef: 'main' }, { number: 999, head: sha('3'), baseRef: 'develop' }], error: '' }
    env = run({ prs: undefined }, PRS, { queue })
    result = await execute(env)
    const got = (result.prs || []).map((r) => `${r.number} ${r.outcome}`).sort()
    if (JSON.stringify(got) !== '["101 recorded","102 recorded"]') fail(`a queue of 101, 102 and a develop-based 999: results ${JSON.stringify(got)}, expected 101 and 102 recorded`)
    if (env.calls.some((c) => c.endsWith(':999'))) fail('a queue row whose base is not main was reviewed')
    if (env.posted.map((p) => p.n).sort().join(',') !== '101,102') fail(`a queue of 101 and 102: comments on ${env.posted.map((p) => p.n).join(',')}`)
    if (result.baseTip !== TIP) fail(`a queue: baseTip ${result.baseTip}, expected the queue's ${TIP}`)
    env = run({ prs: [101] }, PRS)
    result = await execute(env)
    if (result.baseTip !== sha('c')) fail(`a named list: baseTip ${result.baseTip}, expected the inventory's ${sha('c')}`)
    if (env.calls.includes('queue')) fail('a named list ran the queue agent')
    env = run({ prs: undefined }, PRS, { queue: 'null' })
    result = await execute(env)
    if (!result || !result.halted || JSON.stringify(result.prs) !== '[]' || env.posted.length || env.calls.length !== 1) fail(`a queue agent that returned nothing: ${JSON.stringify(result)}, ${env.calls.length} agent(s), ${env.posted.length} comment(s); expected a halt naming the queue, no review and no post`)
  }

  // 20. The agent budget (#86). Three pull requests with four candidates each, a mutation cap of 2 and
  //     agents that take different times, so the pipelines overlap. Per pull request that is one
  //     inventory, two axes, two mutations, one verdict and one post: 7, so 21 named and 22 with the
  //     queue. The return reports exactly the agents that ran, and no more than the slot count are ever
  //     in flight at once. One pull request's mutations start while another is still under review.
  checks++
  {
    const four = (n) => pr(n, ['tests/q.cs'], { candidates: [cand(`Q${n}a`), cand(`Q${n}b`), cand(`Q${n}c`), cand(`Q${n}d`)] })
    const WIDE3 = { 301: four(301), 302: four(302), 303: four(303) }
    // 301 reviews fast, 302 slowly, 303 in between; every other agent takes a few milliseconds.
    const delay = (label) => {
      const [kind, n] = label.split(':')
      if (kind === 'standards' || kind === 'spec') return { 301: 2, 302: 40, 303: 15 }[n] || 1
      return kind === 'mutate' ? 5 + Number(label.split(':')[2]) : 3
    }
    for (const [slots, queued, want] of [[2, false, 21], [1, false, 21], [3, true, 22], [5, false, 21]]) {
      env = run({ prs: queued ? undefined : [301, 302, 303], maxParallel: slots, maxMutationsPerPr: 2 }, WIDE3,
        { delay, queue: { baseTip: sha('c'), prs: [301, 302, 303].map((n) => ({ number: n, head: WIDE3[n].head, baseRef: 'main' })), error: '' } })
      result = await execute(env)
      const what = `three pull requests, ${slots} slot(s)${queued ? ', from the queue' : ''}`
      if (result.agents !== env.calls.length) fail(`${what}: the return reports ${result.agents} agents, ${env.calls.length} ran`)
      if (env.calls.length !== want) fail(`${what}: ${env.calls.length} agents ran, expected ${want}`)
      if (env.flight.max > slots) fail(`${what}: ${env.flight.max} agents in flight at once, over the slot count`)
      if ((result.prs || []).some((r) => r.outcome !== 'recorded')) fail(`${what}: outcomes ${(result.prs || []).map((r) => r.outcome).join(', ')}`)
      if (slots >= 3) {
        // Overlap: 301's first mutation starts while 302's Standards axis is still running.
        const at = (e) => env.events.indexOf(e)
        if (!(at('start mutate:301:1') >= 0 && at('start standards:302') >= 0 && at('start standards:302') < at('start mutate:301:1') && at('start mutate:301:1') < at('end standards:302'))) fail(`${what}: PR 301's mutations did not start while PR 302 was under review: ${env.events.join(', ')}`)
      }
    }
  }

  // 21. A killed agent (#86). Two pull requests, 401 and 402, reviewed together; one of 401's agents is
  //     killed mid-run, at each stage that fails a pull request, by throwing and by returning nothing.
  //     401 alone fails, naming its stage; no comment for it reaches the stubbed gh; and 402's result is
  //     what 402 gets reviewed on its own. A killed mutation agent is the could-not-run row of check 10.
  checks++
  {
    const PAIR = { 401: pr(401, ['tests/k.cs'], { candidates: [cand('K1')] }), 402: pr(402, ['tests/l.cs'], { candidates: [cand('L1')] }) }
    // A result with the throwaway checkout's path taken out, so two runs' results compare.
    const plain = (env, r) => JSON.stringify(r).split(env.repo).join('<repo>')
    const solo = run({ prs: [402] }, PAIR, { gh: ghStub() })
    const alone = plain(solo, resultOf(402, await execute(solo)))
    for (const stage of ['inventory', 'standards', 'spec', 'verdict', 'post']) {
      for (const how of ['throw', 'null']) {
        const gh = ghStub()
        env = run({ prs: [401, 402] }, PAIR, { gh, kill: { [`${stage}:401`]: how }, delay: (l) => (l.endsWith(':401') ? 1 : 2) })
        const what = `PR 401's ${stage} agent killed (${how})`
        try { result = await execute(env) } catch (e) { fail(`${what}: the run itself failed: ${e && e.message}`); continue }
        const r = resultOf(401, result)
        if (r.outcome !== 'failed') fail(`${what}: outcome ${r.outcome}, expected failed`)
        const where = stage === 'standards' || stage === 'spec' ? 'review' : stage
        if (!(r.stage || '').startsWith(where)) fail(`${what}: stage ${JSON.stringify(r.stage)}, expected it to name ${where}`)
        if (r.verdict || r.recordUrl) fail(`${what}: verdict ${JSON.stringify(r.verdict)}, recordUrl ${JSON.stringify(r.recordUrl)}; a failed pull request records nothing`)
        if (gh.store.some((c) => c.n === 401) || env.posted.some((p) => p.n === 401)) fail(`${what}: a comment for 401 reached the stubbed gh`)
        if (stage !== 'post' && env.calls.includes('post:401')) fail(`${what}: a post agent ran for 401`)
        const other = plain(env, resultOf(402, result))
        if (other !== alone) fail(`${what}: PR 402's result changed: ${other}, alone it is ${alone}`)
      }
    }
  }

  // 22. The slot's hand-off (#86). Two agents that end in one macrotask, a few microtasks apart, while
  //     a third pull request waits for a slot: a waiter that takes its own slot on waking leaves a gap
  //     between the free and the take, and the first pull request's axes, asked for in that gap, take
  //     the freed slot too. Two slots, three pull requests; the inventories of 501 and 502 hold the
  //     slots and end k microtasks apart, for each k up to 24, and no more than two agents are ever in
  //     flight. The agent stub's timers end one agent per macrotask, which is why check 20 cannot see it.
  checks++
  {
    const TRIO = { 501: pr(501, ['docs/a.md']), 502: pr(502, ['docs/b.md']), 503: pr(503, ['docs/c.md']) }
    const over = []
    for (let k = 0; k <= 24; k++) {
      const gate = {}
      const hold = (label) => (label === 'inventory:501' || label === 'inventory:502' ? new Promise((resolve) => { gate[label] = resolve }) : undefined)
      env = run({ prs: [501, 502, 503], maxParallel: 2, maxMutationsPerPr: 1 }, TRIO, { hold })
      const done = execute(env)
      for (let i = 0; i < 50 && !(gate['inventory:501'] && gate['inventory:502']); i++) await new Promise((resolve) => setTimeout(resolve, 0))
      if (!(gate['inventory:501'] && gate['inventory:502'])) { fail(`the hand-off, k ${k}: the two inventories never both held a slot`); break }
      gate['inventory:501']()
      for (let i = 0; i < k; i++) await null
      gate['inventory:502']()
      result = await done
      if (env.flight.max > 2) over.push(`k ${k}: ${env.flight.max} in flight`)
      if ((result.prs || []).some((r) => r.outcome !== 'recorded')) fail(`the hand-off, k ${k}: outcomes ${(result.prs || []).map((r) => r.outcome).join(', ')}`)
    }
    if (over.length) fail(`the hand-off: more agents in flight than the 2 slots, at ${over.length} of 25 offsets: ${over.join('; ')}`)
  }
  // 23. Follow-ups (#292). The verdict returns each finding it carries with the open ticket that owns
  //     it, or none. #287's third record carried three findings: deploy.sh builds without --pull
  //     (owned by #288 by then), logs_tests.py and cell_doubles_tests.py run in no CI job, and the
  //     provisional #244 Q9 answer disagrees with end-app-sessions.md (#244 names it but owns no fix).
  //     The run returns the two that name no open ticket as followUps, each with its axis, where, and
  //     the earliest record on the pull request that carried it; the comment ends its findings with
  //     them as a table under "## Follow-ups", before "## Mutations", and the record stays last.
  checks++
  {
    const H287 = '76704c53c36ea51a9a4a81a206b76de7cf46e98c'
    const F287 = 'ae6e2e19be974ca96e75ac0a0aef1e588a9ba9a5'
    const R1 = 'https://github.com/adammedford/spa-platform/pull/287#issuecomment-6340588449'
    const R2 = 'https://github.com/adammedford/spa-platform/pull/287#issuecomment-6342205384'
    const rec = (url, head, at, extra = '') => ({ author_association: 'OWNER', created_at: at, html_url: url, body: `## Review\n\n${extra}Reviewed ${head} against ${F287}: hold` })
    const CARRIED = [
      { axis: 'Spec', title: 'deploy.sh builds the gateway image without --pull', where: 'tools/scripts/deploy.sh', ticket: '#288', firstCarried: R1 },
      { axis: 'Standards', title: 'logs_tests.py and cell_doubles_tests.py run in no CI job', where: '.github/workflows/ci.yml lines 321-326 | tools/scripts', ticket: '', firstCarried: R1 },
      { axis: 'Spec', title: 'The provisional #244 Q9 answer disagrees with end-app-sessions.md', where: '#244 Q9; docs/runbooks/end-app-sessions.md', ticket: '', firstCarried: R2 },
    ]
    const served = (comments) => ({ 287: pr(287, ['.github/workflows/ci.yml'], { head: H287, forkPoint: F287, commits: [H287], comments }) })
    const tableOf = (body) => {
      const lines = body.split('\n')
      const at = lines.indexOf('| Finding | Axis | Where | First carried by |')
      if (at < 0) return null
      const rows = []
      for (const l of lines.slice(at + 2)) {
        if (!l.startsWith('|')) break
        rows.push(l.slice(1, -1).split(/(?<!\\)\|/).map((c) => c.trim().replace(/\\\|/g, '|')))
      }
      return { at, rows }
    }
    const EARLIER = [rec(R1, 'ed8d0633e9c1360fb7c2e425f63237676f4db648', '2026-10-22T01:00:00Z'), rec(R2, '701ba9465d3d90575cecf19c09afee80346ada39', '2026-10-22T02:00:00Z')]
    env = run({ prs: [287] }, served(EARLIER), { followUps: { 287: CARRIED } })
    result = await execute(env)
    const r = resultOf(287, result)
    const want = [
      { axis: 'Standards', title: CARRIED[1].title, where: CARRIED[1].where, firstCarried: R1 },
      { axis: 'Spec', title: CARRIED[2].title, where: CARRIED[2].where, firstCarried: R2 },
    ]
    if (JSON.stringify(r.followUps) !== JSON.stringify(want)) fail(`PR 287: followUps ${JSON.stringify(r.followUps)}, expected the two carried findings that name no open ticket, ${JSON.stringify(want)}`)
    const body = commentOf(env, 287)
    const lines = body.split('\n')
    const t = tableOf(body)
    if (!t) fail('PR 287: the comment carries no follow-ups table')
    else {
      const head = lines.indexOf('## Follow-ups')
      if (!(lines.indexOf('## Spec') < head && head < t.at && t.at < lines.indexOf('## Mutations'))) fail('PR 287: the follow-ups table does not end the findings: it must sit under "## Follow-ups", after "## Spec" and before "## Mutations"')
      const rows = JSON.stringify(t.rows)
      const wantRows = JSON.stringify(want.map((f) => [f.title, f.axis, f.where, f.firstCarried]))
      if (rows !== wantRows) fail(`PR 287: the follow-ups table reads back as ${rows}, expected ${wantRows}`)
    }
    if (lines[lines.length - 1] !== `Reviewed ${H287} against ${F287}: merge`) fail(`PR 287: the comment's last line is ${JSON.stringify(lines[lines.length - 1])}, not the record`)

    // An earlier record whose own table carried the finding decides the earliest record, whatever the
    // verdict says; "this record" in that table is that record. A first-carried URL that is no record
    // on this pull request is read as this record, the first to carry it.
    const R2_TABLE = rec(R2, '701ba9465d3d90575cecf19c09afee80346ada39', '2026-10-22T02:00:00Z', `## Follow-ups\n\n| Finding | Axis | Where | First carried by |\n| --- | --- | --- | --- |\n| ${CARRIED[2].title} | Spec | there | this record |\n\n`)
    const later = [{ ...CARRIED[1], firstCarried: 'https://example.invalid/not-a-record' }, { ...CARRIED[2], firstCarried: '' }]
    env = run({ prs: [287] }, served([EARLIER[0], R2_TABLE]), { followUps: { 287: later } })
    result = await execute(env)
    const got = (resultOf(287, result).followUps || []).map((f) => f.firstCarried)
    if (JSON.stringify(got) !== JSON.stringify(['', R2])) fail(`PR 287: first carried by ${JSON.stringify(got)}, expected ["", "${R2}"]: an unknown URL is this record, and the earlier record's own table names it`)
    const rows = (tableOf(commentOf(env, 287)) || { rows: [] }).rows
    if (!rows[0] || rows[0][3] !== 'this record') fail(`PR 287: a finding this record carries first is written ${JSON.stringify(rows[0] && rows[0][3])}, expected "this record"`)

    // No carried finding without a ticket: the section says so, and nothing is returned.
    env = run({ prs: [287] }, served(EARLIER), { followUps: { 287: [CARRIED[0]] } })
    result = await execute(env)
    const none = commentOf(env, 287).split('\n')
    const h = none.indexOf('## Follow-ups')
    if (h < 0 || !/^- None/.test(none[h + 2] || '') || (resultOf(287, result).followUps || [1]).length) fail(`PR 287 with every carried finding owned: followUps ${JSON.stringify(resultOf(287, result).followUps)}, section line ${JSON.stringify(none[h + 2])}`)
    // A ticket that is not "#<n>" names no ticket: a verdict writing "none" or "-" for one loses no finding.
    env = run({ prs: [287] }, served(EARLIER), { followUps: { 287: [{ ...CARRIED[1], ticket: 'none' }, { ...CARRIED[2], ticket: '-' }, CARRIED[0]] } })
    result = await execute(env)
    const kept = (resultOf(287, result).followUps || []).map((f) => f.title)
    if (JSON.stringify(kept) !== JSON.stringify([CARRIED[1].title, CARRIED[2].title])) fail(`PR 287 with tickets "none" and "-": followUps ${JSON.stringify(kept)}, expected both findings kept and the one #288 owns dropped`)
    if (!/followUps/.test(env.prompts['verdict:287'] || '') || !/no open ticket/.test(env.prompts['verdict:287'] || '')) fail('PR 287: the verdict prompt does not ask for followUps naming no open ticket')
  }
  // A description whose stated head is not the head (#294): #287's description still named ed8d063
  // after a rebuild. The inventory reports each sha the description states as the head; the script
  // compares it with the head it read, and each one that differs is a verified Spec finding naming
  // both shas. A full sha or an abbreviation of the head is current, and so is a description that
  // states none; a prefix under seven digits names no commit, so it is stale.
  checks++
  {
    const HEAD = '76704c5e1d2c3b4a5968778695a4b3c2d1e0f9a8'
    const quote = (s) => `Base \`ae6e2e1\`, head \`${s}\`.`
    const described = (n, stated) => pr(n, ['docs/z.md'], { head: HEAD, commits: [HEAD], statedHeads: stated.map((s) => ({ sha: s, quote: quote(s) })) })
    const SERVED = {
      301: described(301, ['ed8d063']),                 // stale: #287's second review
      302: described(302, [HEAD]),                      // current, full
      303: described(303, ['76704c5']),                 // current, abbreviated as #287's body does
      304: described(304, []),                          // states no head
      305: described(305, ['76704C5', 'ed8d0633e9c1360fb7c2e425f63237676f4db648']), // one current, one stale
      307: described(307, ['76704c']),                  // a prefix of six digits, under the seven a stated head needs
    }
    const env = run({ prs: [301, 302, 303, 304, 305, 307] }, SERVED)
    const result = await execute(env)
    const specOf = (n) => {
      const lines = commentOf(env, n).split('\n')
      return lines.slice(lines.indexOf('## Spec') + 1, lines.indexOf("The author's claims:")).filter((l) => l.startsWith('- '))
    }
    const stale = { 301: ['ed8d063'], 302: [], 303: [], 304: [], 305: ['ed8d0633e9c1360fb7c2e425f63237676f4db648'], 307: ['76704c'] }
    for (const [n, want] of Object.entries(stale).map(([k, v]) => [Number(k), v])) {
      const verified = (resultOf(n, result).findings || { verified: [] }).verified
      if (verified.length !== want.length) fail(`PR ${n}: ${verified.length} verified finding(s), expected ${want.length} from the description's stated head(s)`)
      for (const [i, s] of want.entries()) {
        const f = verified[i] || {}
        if (f.axis !== 'Spec' || f.status !== 'verified') fail(`PR ${n}: the stale-head finding is ${f.axis} ${f.status}, expected a verified Spec finding`)
        if (!String(f.title).includes(s) || !String(f.title).includes(HEAD)) fail(`PR ${n}: the stale-head finding ${JSON.stringify(f.title)} does not name both ${s} and ${HEAD}`)
        if (!String(f.checked).includes(quote(s))) fail(`PR ${n}: the stale-head finding does not quote the description's sentence`)
      }
      const spec = specOf(n)
      if (want.length ? !(spec.length === want.length && want.every((s, i) => spec[i].includes(s) && spec[i].includes(HEAD) && spec[i].includes('(verified,'))) : !(spec.length === 1 && spec[0] === '- None.')) fail(`PR ${n}: the comment's Spec section reads ${JSON.stringify(spec)}`)
    }
    if (!/statedHeads/.test(env.prompts['inventory:301'] || '')) fail('the inventory prompt does not ask for statedHeads')
    if (!(env.prompts['verdict:301'] || '').includes('ed8d063')) fail('PR 301: the verdict prompt does not carry the stale-head finding it is to weigh')
  }

  // A date later than the run's (#525): #507's description said "#44's was re-checked on 2026-10-29"
  // on a review run dated 2026-10-28, and neither axis noticed. The inventory hands over the
  // description and each commit message since the fork point as written; the script finds every
  // YYYY-MM-DD date in them later than the run's date (2026-10-21 here) and makes each a verified low
  // Spec finding quoting its sentence, which the comment carries and the verdict does not hold on. A
  // date on or before the run's date, or none, yields nothing. The review of #525 found the rules the
  // search states untested: the quote is the sentence, not the line; the same date in one sentence
  // counts once; a month past 12 or a day past 31 is no date; and a date inside a longer run of
  // digits is none. PR 515 breaks each, and the quote is matched whole.
  checks++
  {
    const C1 = sha('e')
    const LATER = '#44\'s was re-checked on 2026-10-22, a day after the run.'
    const SERVED = {
      511: pr(511, ['docs/z.md'], { description: `Lands the fix.\n\nThe record holds. ${LATER} Nothing else changed.` }),
      512: pr(512, ['docs/z.md'], { description: 'Checked on 2026-10-21, the run\'s own date. Filed 2026-10-20.', commitMessages: [{ sha: C1, message: 'Task x: done on 2026-10-19\n\nCo-Authored-By: t' }] }),
      513: pr(513, ['docs/z.md'], { commitMessages: [{ sha: C1, message: 'Task x: lands it\n\nVerified green on 2027-01-02 by the check.\n' }] }),
      514: pr(514, ['docs/z.md'], { description: 'No date at all.' }),
      515: pr(515, ['docs/z.md'], { description: 'Ran it on 2026-10-25 and again on 2026-10-25. Not dates: 2026-13-01, 2026-11-32, 12026-10-30 and 2026-10-301.' }),
    }
    const env = run({ prs: [511, 512, 513, 514, 515] }, SERVED)
    const result = await execute(env)
    const want = { 511: [['2026-10-22', LATER, 'description']], 512: [], 513: [['2027-01-02', 'Verified green on 2027-01-02 by the check.', C1]], 514: [], 515: [['2026-10-25', 'Ran it on 2026-10-25 and again on 2026-10-25.', 'description']] }
    for (const [n, list] of Object.entries(want).map(([k, v]) => [Number(k), v])) {
      const r = resultOf(n, result)
      const verified = (r.findings || { verified: [] }).verified
      if (verified.length !== list.length) fail(`PR ${n}: ${verified.length} verified finding(s), expected ${list.length} from dates later than the run's`)
      for (const [i, [date, quote, where]] of list.entries()) {
        const f = verified[i] || {}
        if (f.axis !== 'Spec' || f.status !== 'verified' || f.severity !== 'low') fail(`PR ${n}: the later-date finding is ${f.axis} ${f.status} ${f.severity}, expected a verified low Spec finding`)
        if (!String(f.title).includes(date) || !String(f.title).includes('2026-10-21')) fail(`PR ${n}: the later-date finding ${JSON.stringify(f.title)} does not name both ${date} and the run's date`)
        if (!String(f.checked).endsWith(JSON.stringify(quote))) fail(`PR ${n}: the later-date finding does not quote its sentence ${JSON.stringify(quote)}: ${JSON.stringify(f.checked)}`)
        if (!String(f.where).includes(where)) fail(`PR ${n}: the later-date finding is at ${JSON.stringify(f.where)}, expected it to name ${where}`)
      }
      if (r.verdict !== 'merge') fail(`PR ${n}: verdict ${r.verdict}; a later date is carried, not held on`)
      const lines = commentOf(env, n).split('\n')
      const spec = lines.slice(lines.indexOf('## Spec') + 1, lines.indexOf("The author's claims:")).filter((l) => l.startsWith('- '))
      if (list.length ? !(spec.length === list.length && list.every(([date, quote], i) => spec[i].includes(date) && spec[i].includes('(verified, low)') && spec[i].includes(quote))) : !(spec.length === 1 && spec[0] === '- None.')) fail(`PR ${n}: the comment's Spec section reads ${JSON.stringify(spec)}`)
    }
    const inv = env.prompts['inventory:511'] || ''
    if (!/description/.test(inv) || !/commitMessages/.test(inv)) fail('the inventory prompt does not ask for the description and the commit messages')
    if (!(env.prompts['verdict:511'] || '').includes('2026-10-22')) fail('PR 511: the verdict prompt does not carry the later-date finding it is to weigh')
  }

  // The return leads with one line per request (#294): the completion notice cuts the return short,
  // and on #287's runs the verdict and the mutation rows were past the cut. summary is the first
  // field, one line per pull request with its number, head, verdict, record URL, verified finding and
  // mutation counts, and the did-not-fail rows named; a halted run's summary is empty.
  checks++
  {
    const HEAD = '76704c5e1d2c3b4a5968778695a4b3c2d1e0f9a8'
    const SERVED = {
      301: pr(301, ['docs/z.md'], { head: HEAD, commits: [HEAD], statedHeads: [{ sha: 'ed8d063', quote: 'head `ed8d063`.' }] }),
      306: pr(306, ['tests/Platform.Registry.Tests/GuardsX.cs'], { candidates: [cand('GuardsX'), cand('GuardsY')] }),
      76: PRS[76],
    }
    const env = run({ prs: [301, 306, 76] }, SERVED, { mutate: { GuardsX: row('did-not-fail') } })
    const result = await execute(env)
    if (Object.keys(result)[0] !== 'summary') fail(`the return's first field is ${JSON.stringify(Object.keys(result)[0])}, expected summary`)
    const want = [
      `#301 ${HEAD} merge (recorded) https://github.com/o/r/pull/301#issuecomment-1: 1 verified finding(s), 0 mutation(s), did-not-fail: none`,
      `#306 ${sha('6')} hold (recorded) https://github.com/o/r/pull/306#issuecomment-1: 0 verified finding(s), 2 mutation(s), did-not-fail: GuardsX`,
      `#76 ${H76} merge (skipped) ${CLEARED_76.html_url}: 0 verified finding(s), 0 mutation(s), did-not-fail: none`,
    ]
    if (JSON.stringify(result.summary) !== JSON.stringify(want)) fail(`summary is ${JSON.stringify(result.summary, null, 1)}, expected ${JSON.stringify(want, null, 1)}`)
    // A pull request that fails names its stage where the verdict would be, and the head it read.
    const dead = run({ prs: [306] }, SERVED, { kill: { 'verdict:306': 'null' } })
    const r = await execute(dead)
    const line = (r.summary || [])[0] || ''
    if (!/^#306 6{40} - \(failed: verdict: the verdict agent returned nothing\) \(no record\): 0 verified finding\(s\), 2 mutation\(s\), did-not-fail: none$/.test(line)) fail(`a failed pull request's summary line is ${JSON.stringify(line)}`)
    // A halted run, a bad launch or a queue nobody could read, leads with an empty summary.
    for (const [why, a, b] of [['a bad launch', { prs: [] }, {}], ['an unread queue', {}, { queue: 'null' }]]) {
      const h = await execute(run(a, SERVED, b))
      if (Object.keys(h || {})[0] !== 'summary' || !Array.isArray(h.summary) || h.summary.length) fail(`${why} returns ${JSON.stringify(h)}, expected summary [] first`)
    }
  }

  // A ticket's change requests are spec sources (#434, #421 Q12): the Spec axis reads, for each issue
  // the pull request cites, the issues it is or was blocked by, and holds the diff to each one
  // labelled change-request, its body and its decision comment, open or closed, so the diff is held
  // to what was decided. The Standards axis is not sent there.
  checks++
  {
    const SERVED = { 434: pr(434, ['.claude/workflows/review-queue.js'], { cites: { planRow: '', issues: [434, 381], mergeSubject: '' } }) }
    const env = run({ prs: [434] }, SERVED)
    await execute(env)
    const spec = env.prompts['spec:434'] || ''
    for (const i of [434, 381]) if (!spec.includes(`cd ${env.repo} && gh issue view ${i} --json blockedBy`)) fail(`PR 434: the Spec prompt does not read the issues #${i} is blocked by`)
    if (!/labelled `change-request`/.test(spec)) fail('PR 434: the Spec prompt does not keep the blocking issues labelled change-request')
    if (!/open or closed/.test(spec)) fail('PR 434: the Spec prompt does not read a closed change request as well as an open one')
    if (!/its body and its decision comment/.test(spec) || !/`Decision:`/.test(spec)) fail('PR 434: the Spec prompt does not name each request\'s body and its Decision: comment as spec sources')
    if (!/what was decided/.test(spec)) fail('PR 434: the Spec prompt does not hold the diff to what the request\'s decision decided')
    if (/blockedBy/.test(env.prompts['standards:434'] || '')) fail('PR 434: the Standards prompt reads change requests, which are the Spec axis\'s sources')
  }

  // A held steering change is filed, not lost (#434, #411 Q5, #421 Q12). The Spec axis judges whether
  // the authority a steering change cites decides it; the citation itself is the steering check's,
  // so no prompt asks for a trailer to be read. A change it holds is a verified finding the author
  // takes out of the branch, and the verdict files it as a change-request issue blocking nothing,
  // after searching the open requests for its title; a request it cannot file is reported, since a
  // review keeps no pending-writes file (#421 Q11). The comment and the return name each one.
  checks++
  {
    const SERVED = { 435: pr(435, ['GLOSSARY.md'], { cites: { planRow: '', issues: [434], mergeSubject: '' } }) }
    const filed = [
      { title: 'Glossary: Held change', number: 501, found: false, error: '' },
      { title: 'Review guide: change requests as spec sources', number: 490, found: true, error: '' },
      { title: 'Agent guide: the done rule', number: 0, found: false, error: 'GitHub could not be reached' },
    ]
    const env = run({ prs: [435] }, SERVED, { verdict: 'hold', changeRequests: { 435: filed } })
    const result = await execute(env)
    const spec = env.prompts['spec:435'] || ''
    if (!/steering document/.test(spec) || !/whether the authority it cites decides this change/.test(spec)) fail('PR 435: the Spec prompt does not judge whether a steering change\'s cited authority decides it')
    const v = env.prompts['verdict:435'] || ''
    const search = v.indexOf(`cd ${env.repo} && gh issue list --state open --label change-request --search "<title> in:title"`)
    const create = v.indexOf(`cd ${env.repo} && gh issue create`)
    if (search < 0) fail('PR 435: the verdict prompt does not search the open change requests by title')
    if (create < 0) fail('PR 435: the verdict prompt does not file a held steering change as an issue')
    if (search >= 0 && create >= 0 && search > create) fail('PR 435: the verdict prompt files the request before it searches for one')
    const createLine = create >= 0 ? v.slice(create, v.indexOf('\n', create) < 0 ? undefined : v.indexOf('\n', create)) : ''
    if (!/--label change-request --label ready-for-human/.test(createLine)) fail('PR 435: the request is not filed labelled change-request and ready-for-human')
    if (/--blocking|--blocked-by/.test(v)) fail('PR 435: the held change\'s request blocks something, but it blocks nothing')
    if (!/--body-file -/.test(createLine)) fail('PR 435: the request body is not passed on stdin, so the review would write a file outside its OUTPUT FILE lines')
    if (!v.includes('**Raised by:** #435')) fail('PR 435: the request body does not say the review of #435 raised it')
    if (!/out of the branch/.test(v)) fail('PR 435: the verdict prompt does not say the held change comes out of the branch')
    if (!/file nothing/.test(v)) fail('PR 435: the verdict prompt files a second request when one with that title is open')
    for (const c of allPrompts) if (/trailer/i.test(c.prompt)) fail(`the ${c.label} prompt mentions a trailer; reading the citation is the steering check's (#411 Q5)`)
    const body = commentOf(env, 435)
    // The numbers are built, not written: a literal hash-number here is a citation the steering check
    // reads, and these name no issue of this repository.
    for (const want of ['## Change requests', `- #${filed[0].number} Glossary: Held change: filed, blocking nothing.`, `- #${filed[1].number} Review guide: change requests as spec sources: already open, nothing filed.`, '- Agent guide: the done rule: not filed: GitHub could not be reached.']) if (!body.includes(want)) fail(`PR 435: the comment lacks ${JSON.stringify(want)}`)
    if (JSON.stringify(resultOf(435, result).changeRequests) !== JSON.stringify(filed)) fail(`PR 435: the return's changeRequests are ${JSON.stringify(resultOf(435, result).changeRequests)}`)
    // A review that holds no steering change names no section for it.
    const quiet = run({ prs: [101] }, PRS)
    await execute(quiet)
    if (commentOf(quiet, 101).includes('## Change requests')) fail('PR 101: a review that filed no request carries a Change requests section')
  }

  // The edit rule (#109, Q164 of #286): every prompt that carries the house rules carries the amended
  // line verbatim, Edit and Write both, and no prompt still carries the Edit-only line it replaced.
  checks++
  {
    const EDIT_RULE = '- To change an existing file with Edit or Write, open it with the Read tool first in this session, not with `cat`, `head` or `sed -n`: for a file you will change, this rule overrides the auto-mode advice to read with Bash. Open it with Read again after anything else wrote it (a script, the check, a merge, `git`). Otherwise make the change with a short script written to a file under your scratchpad and run by its path. Edit and Write refuse a file not read since its last write: 16 calls went to that refusal on 2026-10-18 and 16 over the four launches from 2026-10-19 to 2026-10-21 (#109).'
    const housed = allPrompts.filter((c) => /(House|Read-only) rules \(/.test(c.prompt))
    if (!housed.length) fail('no prompt carries the house rules, so the edit-rule search read nothing')
    for (const c of housed) if (!c.prompt.includes(EDIT_RULE)) fail(`the ${c.label} prompt's house rules lack the edit rule of #109`)
    for (const c of allPrompts) if (/Change a file with Edit only after/.test(c.prompt)) fail(`the ${c.label} prompt still carries the Edit-only line #109 replaced`)
  }

  // The launch in whenToUse (#522): /review-queue shows meta and then a launch by name, which runs a
  // session's snapshot with no arguments and halts as a bad launch. whenToUse names the working one:
  // by scriptPath, with the required repo, date and wt. The check is held to itself: a whenToUse with
  // any one of those words taken out is reported as lacking it.
  checks++
  {
    const meta = loadMeta()
    if (!meta) fail('meta could not be read from the script')
    else {
      const gaps = launchGaps(meta.whenToUse)
      if (gaps.length) fail(`meta.whenToUse does not name ${gaps.join(', ')} of the scriptPath launch`)
      for (const w of LAUNCH_WORDS) {
        const cut = String(meta.whenToUse || '').split(w).join('')
        if (!launchGaps(cut).includes(w)) fail(`the whenToUse check misses a whenToUse with ${w} taken out`)
      }
    }
  }
} catch (e) {
  console.error(`review-queue tests: the script did not run: ${e && e.stack ? e.stack : e}`)
  process.exit(2)
}

if (failures.length) {
  console.log(`review-queue tests: FAILED, ${failures.length} failure(s) in ${checks} checks, on ${path.basename(scriptPath)}:`)
  failures.forEach((f) => console.log(`  - ${f}`))
  process.exit(1)
}
console.log(`review-queue tests: pass: ${checks} checks, on ${path.basename(scriptPath)}`)
