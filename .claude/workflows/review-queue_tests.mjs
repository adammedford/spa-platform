// The review-queue script's test companion. Owner: integrator. Tracked beside the script (#83), as
// #213 tracks the overnight script's companion.
//
//   node .claude/workflows/review-queue_tests.mjs [script]
//
// Runs review-queue.js end to end with agent, parallel, log and phase stubbed, and with gh stubbed
// where the script reaches it: through the inventory agent, which serves canned pull requests as gh
// would list them, and the post agent, which records the comment it was asked to post instead of
// posting it. No agent, gh or network call is made. The main checkout the script is given is a
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

function run(scriptArgs, served) {
  const calls = []
  const posted = []
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
  globalThis.parallel = (fns) => Promise.all(fns.map((f) => f().catch(() => null)))
  globalThis.agent = async (prompt, opts) => {
    const [kind, n] = opts.label.split(':')
    calls.push(opts.label)
    for (const m of prompt.matchAll(/^OUTPUT FILE: (\S+)$/gm)) {
      fs.mkdirSync(path.dirname(m[1]), { recursive: true })
      fs.writeFileSync(m[1], `${opts.label}\n`)
    }
    if (kind === 'inventory') return served[n]
    if (kind === 'standards' || kind === 'spec') return { axis: kind, sourcesRead: ['CODING_STANDARDS.md'], findings: [], claims: [], nominations: [] }
    if (kind === 'verdict') return { verdict: 'merge', findings: [], claims: [], outstanding: ['nothing'], summary: 's' }
    if (kind === 'post') {
      const body = /^BEGIN COMMENT\n([\s\S]*)\nEND COMMENT$/m.exec(prompt)
      posted.push({ n: Number(n), body: body ? body[1] : null })
      return { posted: true, url: `https://github.com/o/r/pull/${n}#issuecomment-1`, headNow: served[n].head, unreachable: false, bodyFile: '' }
    }
    throw new Error(`unexpected agent label ${opts.label}`)
  }
  return { repo, calls, posted, git }
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
    if (!lines.some((l) => /^No mutations run/.test(l))) fail(`PR ${n}: the comment does not say no mutations run`)
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
