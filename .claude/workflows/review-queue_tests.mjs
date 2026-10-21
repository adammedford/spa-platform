// The review-queue script's test companion. Owner: integrator. Tracked beside the script (#83), as
// #213 tracks the overnight script's companion.
//
//   node .claude/workflows/review-queue_tests.mjs [script]
//
// Runs review-queue.js end to end with agent, parallel, log and phase stubbed, and with gh stubbed
// where the script reaches it: through the inventory agent, which serves canned pull requests as gh
// would list them, and the post agent, which records the comment it was asked to post instead of
// posting it. A stubbed mutation agent reports, per test, the result a check tells it to, or dies by
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
  candidates: [], statedMutations: [],
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
const cand = (test, project = 'tests/Platform.Registry.Tests') => ({ test, project, guards: `what ${test} guards`, breaks: `src/${test}.cs` })
const stated = (test, failed, total, project = 'tests/Platform.Registry.Tests') => ({ test, project, mutation: `break what ${test} guards`, failed, total, quote: `breaking it failed ${failed} of ${total}` })
// What a stubbed mutation agent reports: reproduced, did-not-fail or could-not-run, with counts.
const row = (result, failed = result === 'reproduced' ? 1 : 0, total = 10) => ({ result, failed, total })

// behave: { nominations: { [n]: [...] }, mutate: { [test]: row | 'throw' | 'null' }, verdict: 'merge' | 'hold' }
function run(scriptArgs, served, behave = {}) {
  const calls = []
  const posted = []
  const mutations = []
  const repo = fs.mkdtempSync(path.join(os.tmpdir(), 'review-queue-tests-'))
  made.push(repo)
  const git = (...a) => execFileSync('git', ['-C', repo, ...a], { encoding: 'utf8' })
  git('init', '-q')
  fs.copyFileSync(path.join(top, '.gitignore'), path.join(repo, '.gitignore'))
  fs.writeFileSync(path.join(repo, 'README.md'), 'tracked\n')
  git('add', '-A')
  git('-c', 'user.name=t', '-c', 'user.email=t@t', 'commit', '-q', '-m', 'start')
  const args = { repo, wt: path.join(os.tmpdir(), 'review-queue-tests-wt'), date: '2026-10-21', ...scriptArgs }

  globalThis.args = args
  globalThis.phase = () => {}
  globalThis.log = () => {}
  // A rejected branch rejects the whole call: the script, not this stub, must turn a dying agent into
  // a row, since nothing promises the Workflow tool's parallel swallows a throw.
  globalThis.parallel = (fns) => Promise.all(fns.map((f) => f()))
  globalThis.agent = async (prompt, opts) => {
    const [kind, n] = opts.label.split(':')
    calls.push(opts.label)
    for (const m of prompt.matchAll(/^OUTPUT FILE: (\S+)$/gm)) {
      fs.mkdirSync(path.dirname(m[1]), { recursive: true })
      fs.writeFileSync(m[1], `${opts.label}\n`)
    }
    if (kind === 'inventory') return served[n]
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
    if (kind === 'verdict') return { verdict: behave.verdict || 'merge', findings: [], claims: [], outstanding: ['nothing'], summary: 's' }
    if (kind === 'post') {
      const body = /^BEGIN COMMENT\n([\s\S]*)\nEND COMMENT$/m.exec(prompt)
      posted.push({ n: Number(n), body: body ? body[1] : null })
      return { posted: true, url: `https://github.com/o/r/pull/${n}#issuecomment-1`, headNow: served[n].head, unreachable: false, bodyFile: '' }
    }
    throw new Error(`unexpected agent label ${opts.label}`)
  }
  return { repo, wt: args.wt, calls, posted, mutations, git }
}

async function execute() {
  const src = fs.readFileSync(scriptPath, 'utf8').replace(/^export const meta/m, 'const meta')
  return await new Function('return (async () => {' + src + '\n})()')()
}

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
