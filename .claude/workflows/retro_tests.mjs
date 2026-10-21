// The retro script's test companion. Owner: integrator. Tracked beside the script (#260), named for
// it as review-queue_tests.mjs is for its own.
//
//   node .claude/workflows/retro_tests.mjs [script]
//
// Runs retro.js end to end with agent, parallel, log and phase stubbed, and with gh stubbed where the
// script reaches it: through the durable-state read, which serves the open `Retro <date>` listing as
// gh would and the phase files as tools/retro_record.py reads them, and through the File agent, which
// reports the issues it was asked to file instead of filing them. No agent, gh or network call is
// made. The main checkout the script is given is a throwaway git repository with this repository's
// .gitignore; a stubbed agent writes every OUTPUT FILE and PHASE FILE its prompt names into it, so
// what a run leaves there is evidence, and a resumed run reads it back through the real reader.
// Every stub's return is validated against the schema the script passed, so a fixture that drifts
// from the script's schemas fails here rather than in a launch. Exits 0 when every check holds, 1
// when one fails (each failure printed), 2 when the script cannot run. Its last line is its verdict.
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const scriptPath = process.argv[2] || path.join(here, 'retro.js')
const top = path.resolve(here, '..', '..')
const READER = path.join(top, 'tools', 'retro_record.py')
const DATE = '2026-10-17'
const SCRATCH_REL = `.scratch/overnight/log/retro-${DATE}/`

// ---------------------------------------------------------------- a minimal JSON Schema check
// The subset the script's schemas use: type, properties, required, items, enum.
const validate = (schema, value, where = '$') => {
  const bad = []
  if (!schema) return [`${where}: no schema`]
  const t = schema.type
  const kind = Array.isArray(value) ? 'array' : value === null ? 'null' : Number.isInteger(value) ? 'integer' : typeof value
  if (t && !(t === kind || (t === 'number' && kind === 'integer'))) return [`${where}: ${kind}, expected ${t}`]
  if (schema.enum && !schema.enum.includes(value)) bad.push(`${where}: ${JSON.stringify(value)} not in ${schema.enum.join('|')}`)
  if (t === 'object') {
    for (const k of schema.required || []) if (!(k in value)) bad.push(`${where}.${k}: missing`)
    for (const [k, s] of Object.entries(schema.properties || {})) if (k in value) bad.push(...validate(s, value[k], `${where}.${k}`))
  }
  if (t === 'array' && schema.items) value.forEach((v, i) => bad.push(...validate(schema.items, v, `${where}[${i}]`)))
  return bad
}

// ---------------------------------------------------------------- the record the stubs serve
const sha = (c) => c.repeat(40)
const ROLES = ['ws1-infra', 'ws2-hosting', 'ws3-identity', 'ws4-proxy', 'ws5-fixtures', 'ws6-verification', 'integrator']
const lane = (owner, size) => ({ owner, size, tasks: Array.from({ length: size }, (_, i) => ({ id: `${owner}-t${i}`, issue: 0, ticket: '' })) })
const logEntry = (p, task, verdict, extra = {}) => ({ path: p, task, stage: 'implement', round: '', sha: sha('a'), verdict, verdictLine: verdict === 'RED' ? 'GATE: RED' : 'GATE: GREEN', lastLines: ['  failed: 1', verdict === 'RED' ? 'GATE: RED' : 'GATE: GREEN'], lines: 60, mtime: '2026-10-17T03:00:00Z', side: false, ...extra })
const OVERNIGHT_READ = { path: '/x/.claude/workflows/overnight.js', sha256: 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855', mtime: '2026-10-16T21:14:03Z' }
const indexFor = (lanes, logs = []) => ({
  night: { date: DATE, issue: 0, shape: 'archive', base: sha('b'), integrationBranch: `overnight/${DATE}` },
  report: { source: '/x/.scratch/overnight/REPORT-2026-10-17.md', date: DATE, kind: 'report', sections: ['Integrated', 'Failed'] },
  questions: [{ source: '/x/.scratch/overnight/questions-2026-10-17.md', date: DATE }],
  lanes, logs, sideLogs: [], notLogs: [], rounds: [],
  integration: { branch: `overnight/${DATE}`, merges: [], reviewCommits: [], attempts: [] },
  worktrees: [], returnJson: '', pending: '', untracked: '',
  overnightScript: OVERNIGHT_READ,
  history: [{ date: '2026-10-16', report: '/x/REPORT.md', questions: '/x/questions.md' }],
  missing: ['return JSON: none saved', 'transcripts and tool-call counts: the record holds none'],
})
const finding = (noun, extra = {}) => ({
  type: 'misleading-instruction', title: `about ${noun}`, noun,
  evidence: [{ kind: 'check-log', ref: `/x/log/${noun}.txt:3`, lines: '3', quote: noun }],
  cost: { red: 0, attempts: 1, nights: 1, lanes: 1 },
  fix: { kind: 'prompt', owner: 'integrator', summary: 's', diff: '' }, confidence: 'verified', ...extra,
})
const ranked = (n) => Array.from({ length: n }, (_, i) => ({ ...finding(`noun-${i + 1}`), recurrence: 1, score: 100 - i, blockedBy: [] }))

const made = []
process.on('exit', () => { for (const dir of made) fs.rmSync(dir, { recursive: true, force: true }) })

const newRepo = () => {
  const repo = fs.mkdtempSync(path.join(os.tmpdir(), 'retro-tests-'))
  made.push(repo)
  const git = (...a) => execFileSync('git', ['-C', repo, ...a], { encoding: 'utf8' })
  git('init', '-q')
  fs.copyFileSync(path.join(top, '.gitignore'), path.join(repo, '.gitignore'))
  fs.writeFileSync(path.join(repo, 'README.md'), 'tracked\n')
  git('add', '-A')
  git('-c', 'user.name=t', '-c', 'user.email=t@t', 'commit', '-q', '-m', 'start')
  return { repo, git }
}

// served: { index, rankedCount, openRetro, laneFindings: {key: [...]}, stateDate }
function stub(env, scriptArgs, served) {
  const calls = []
  let inFlight = 0
  env.peak = 0
  globalThis.args = { repo: env.repo, ...scriptArgs }
  globalThis.phase = () => {}
  globalThis.log = () => {}
  globalThis.parallel = (fns) => Promise.all(fns.map((f) => f().catch((e) => { env.thrown.push(e); return null })))
  globalThis.agent = async (prompt, opts) => {
    inFlight++
    env.peak = Math.max(env.peak, inFlight)
    try {
      await new Promise((r) => setTimeout(r, 5))
      const label = opts.label
      calls.push({ label, prompt, schema: opts.schema })
      let out
      if (label === 'state') {
        const scratch = path.join(env.repo, SCRATCH_REL)
        const files = JSON.parse(execFileSync('python3', ['-I', READER, 'state', scratch], { encoding: 'utf8' }))
        out = { date: served.stateDate || DATE, issue: 0, issueUrl: '', openRetro: served.openRetro || [], phaseFiles: files, error: '' }
      } else if (label === 'gather') out = served.index
      else if (label.startsWith('lane:')) {
        const k = label.slice(5)
        const found = (served.laneFindings || {})[k] || []
        out = { lane: k, findings: found, nothingFound: found.length ? '' : 'every log GREEN, one round' }
      } else if (label === 'rank') out = { ranked: ranked(served.rankedCount ?? 2), dropped: [{ noun: 'old', issue: '#68', reason: 'open issue covers it' }], coverage: { matched: [], missed: [...prompt.matchAll(/the known issues are ([#\d, ]+)\./g)].flatMap((x) => x[1].split(', ')).map((n) => ({ issue: Number(n.slice(1)), reason: 'unexplained' })) } }
      else if (label === 'file') {
        const m = /^THE TICKETS[^\n]*\n([\s\S]*?)\nDropped: /m.exec(prompt)
        const list = m ? JSON.parse(m[1]) : []
        const dry = /This is a dry run/.test(prompt)
        const files = [...prompt.matchAll(/^OUTPUT FILE: (\S+)$/gm)].map((x) => x[1])
        out = served.fileUnreachable ? {
          parent: { number: 0, url: '', title: `Retro ${DATE}`, file: files[0] || '' }, tickets: [], dryRun: false, refused: '', unreachable: true,
        } : {
          parent: { number: dry ? 0 : 900, url: dry ? '' : 'https://github.com/o/r/issues/900', title: `Retro ${DATE}`, file: files[0] || '' },
          tickets: list.map((f, i) => ({ number: dry ? 0 : 901 + i, url: dry ? '' : `https://github.com/o/r/issues/${901 + i}`, title: `${f.type}: ${f.title}`, label: 'ready-for-agent', noun: f.noun, file: files[i + 1] || '', blockedBy: [] })),
          dryRun: dry, refused: '', unreachable: false,
        }
      } else throw new Error(`unexpected agent label ${label}`)
      const bad = validate(opts.schema, out)
      if (bad.length) env.schemaFailures.push(`${label}: ${bad.slice(0, 3).join('; ')}`)
      for (const x of prompt.matchAll(/^OUTPUT FILE: (\S+)$/gm)) {
        fs.mkdirSync(path.dirname(x[1]), { recursive: true })
        fs.writeFileSync(x[1], `${label}\n`)
      }
      for (const x of prompt.matchAll(/^PHASE FILE: (\S+) INPUTS: (\S+)$/gm)) {
        fs.mkdirSync(path.dirname(x[1]), { recursive: true })
        fs.writeFileSync(x[1], JSON.stringify({ inputs: x[2], result: out }))
      }
      return out
    } finally { inFlight-- }
  }
  return calls
}

async function execute(env, scriptArgs, served) {
  env.thrown = env.thrown || []
  env.schemaFailures = env.schemaFailures || []
  const calls = stub(env, scriptArgs, served)
  const src = fs.readFileSync(scriptPath, 'utf8').replace(/^export const meta/m, 'const meta')
  const result = await new Function('return (async () => {' + src + '\n})()')()
  return { result, calls, labels: calls.map((c) => c.label) }
}

const failures = []
const fail = (msg) => failures.push(msg)
let checks = 0
const count = (labels, l) => labels.filter((x) => x === l).length
const THREE = [lane('ws1-infra', 3), lane('ws3-identity', 2), lane('ws6-verification', 1)]
const GH_WRITE = /gh issue (create|edit|comment|close|delete|reopen|transfer)\b|gh api [^\n]*(-X|--method) *(POST|PATCH|PUT|DELETE)/

try {
  // 1. A launch with neither date nor issue, and one with an argument the script does not take, stop
  //    before any agent.
  checks++
  for (const [label, a] of [['neither date nor issue', {}], ['an unknown argument', { date: DATE, dryrun: true }]]) {
    const env = newRepo()
    const { result, calls } = await execute(env, a, {})
    if (!result || !result.halted || calls.length) fail(`${label}: ${calls.length} agent(s) ran; returned ${JSON.stringify(result)}`)
  }

  // 2. The fan-out: the state read, one Gather agent, one agent per owner lane plus integration and
  //    prompts, one Rank and one File; every call with a schema its stubbed return satisfies.
  checks++
  let env = newRepo()
  let { result, labels, calls } = await execute(env, { date: DATE }, { index: indexFor(THREE) })
  const want = ['state', 'gather', 'lane:ws1-infra', 'lane:ws3-identity', 'lane:ws6-verification', 'lane:integration', 'lane:prompts', 'rank', 'file']
  if (JSON.stringify([...labels].sort()) !== JSON.stringify([...want].sort())) fail(`fan-out: agents ${labels.join(', ')}; expected ${want.join(', ')}`)
  for (const l of ['state', 'gather', 'rank', 'file']) if (count(labels, l) !== 1) fail(`fan-out: ${count(labels, l)} ${l} agents, expected one`)

  // 3. Every agent call passes a schema, and every stub's return satisfies it.
  checks++
  for (const c of calls) if (!c.schema || c.schema.type !== 'object') fail(`${c.label}: called without a schema`)
  if (env.schemaFailures.length) fail(`returns not matching the script's schemas: ${env.schemaFailures.join(' | ')}`)

  // 4. The return JSON names the parent, the tickets, dropped, coverage and the scratch directory, and
  //    the overnight script read, with its hash and mtime.
  checks++
  for (const k of ['parent', 'tickets', 'dropped', 'coverage', 'scratch']) if (!(k in (result || {}))) fail(`the return has no ${k}`)
  if (result.parent && result.parent.number !== 900) fail(`parent ${JSON.stringify(result.parent)}, expected issue 900`)
  if ((result.tickets || []).map((t) => t.number).join() !== '901,902') fail(`tickets ${JSON.stringify(result.tickets)}`)
  if (JSON.stringify(result.dropped) !== JSON.stringify([{ noun: 'old', issue: '#68', reason: 'open issue covers it' }])) fail(`dropped ${JSON.stringify(result.dropped)}`)
  if (result.scratch !== path.join(env.repo, SCRATCH_REL).replace(/\/$/, '')) fail(`scratch ${result.scratch}`)
  if (JSON.stringify(result.overnightScript) !== JSON.stringify(OVERNIGHT_READ)) fail(`overnightScript ${JSON.stringify(result.overnightScript)}, expected the hash and mtime Gather read`)
  if (result.dryRun !== false || result.filed !== true) fail(`a real run returned dryRun ${result.dryRun}, filed ${result.filed}`)
  // A real run's File prompt carries the write commands, so check 8's search for them can fail.
  const fileCall = calls.find((c) => c.label === 'file')
  if (!fileCall || !GH_WRITE.test(fileCall.prompt) || !/--parent/.test(fileCall.prompt)) fail('a real run\'s File prompt carries no gh issue create --parent command')
  // ...and no other phase's prompt carries one, since File is the only writer (#77).
  for (const c of calls) if (c.label !== 'file' && GH_WRITE.test(c.prompt)) fail(`a real run: the ${c.label} prompt carries a gh write: ${GH_WRITE.exec(c.prompt)[0]}`)

  // 5. Everything the run wrote is under its scratch directory, and git sees no change.
  checks++
  if (env.git('status', '--porcelain') !== '') fail(`the main checkout changed: ${env.git('status', '--porcelain').trim()}`)
  const written = env.git('status', '--porcelain', '--ignored', '--untracked-files=all').split('\n').filter(Boolean).map((l) => l.slice(3))
  if (!written.length) fail('the run wrote nothing, so the scratch directory was never exercised')
  for (const w of written) if (!w.startsWith(SCRATCH_REL)) fail(`the run wrote ${w}, outside ${SCRATCH_REL}`)

  // 6. Lanes beyond maxLaneAgents are grouped, the smallest together.
  checks++
  const SEVEN = ROLES.map((r, i) => lane(r, 7 - i))   // ws1-infra largest, integrator smallest
  env = newRepo();
  ({ labels } = await execute(env, { date: DATE, maxLaneAgents: 3 }, { index: indexFor(SEVEN) }))
  const lanes = labels.filter((l) => l.startsWith('lane:') && l !== 'lane:integration' && l !== 'lane:prompts')
  const wantLanes = ['lane:ws1-infra', 'lane:ws2-hosting', 'lane:ws3-identity+ws4-proxy+ws5-fixtures+ws6-verification+integrator']
  if (JSON.stringify(lanes.sort()) !== JSON.stringify(wantLanes.sort())) fail(`grouping: ${lanes.join(', ')}; expected ${wantLanes.join(', ')}`)

  // 7. Never more than maxParallel agents in flight; and the probe can see concurrency at all.
  checks++
  env = newRepo()
  await execute(env, { date: DATE, maxParallel: 2 }, { index: indexFor(SEVEN) })
  if (env.peak > 2) fail(`maxParallel 2: ${env.peak} agents were in flight at once`)
  if (env.peak < 2) fail(`maxParallel 2: the peak was ${env.peak}, so the lanes never ran concurrently and the cap was not exercised`)
  env = newRepo()
  await execute(env, { date: DATE, maxParallel: 5 }, { index: indexFor(SEVEN) })
  if (env.peak !== 5) fail(`maxParallel 5 over nine lane agents: peak ${env.peak}, expected 5`)

  // 8. dryRun: no prompt carries a gh write, the would-be parent and sub-issues are files under the
  //    scratch directory, and the return says it was a dry run.
  checks++
  env = newRepo();
  ({ result, calls } = await execute(env, { date: DATE, dryRun: true }, { index: indexFor(THREE), rankedCount: 3 }))
  for (const c of calls) if (GH_WRITE.test(c.prompt)) fail(`dryRun: the ${c.label} prompt carries a gh write: ${GH_WRITE.exec(c.prompt)[0]}`)
  const dry = path.join(env.repo, SCRATCH_REL, 'dry-run')
  for (const f of ['parent.md', 'ticket-01.md', 'ticket-02.md', 'ticket-03.md']) if (!fs.existsSync(path.join(dry, f))) fail(`dryRun: ${f} was not written under ${dry}`)
  if (result.dryRun !== true || result.filed !== false) fail(`dryRun: the return says dryRun ${result.dryRun}, filed ${result.filed}`)
  if ((result.tickets || []).some((t) => !t.file.startsWith(dry))) fail(`dryRun: tickets name files outside ${dry}`)

  // 9. An open Retro <date> issue: no File agent, nothing filed, and the delta returned.
  checks++
  const OPEN = { number: 777, title: `Retro ${DATE}`, url: 'https://github.com/o/r/issues/777', text: 'body naming noun-1 already' }
  env = newRepo();
  ({ result, labels } = await execute(env, { date: DATE }, { index: indexFor(THREE), rankedCount: 3, openRetro: [OPEN] }))
  if (labels.includes('file')) fail('an open Retro issue: the File agent was still dispatched')
  if (result.filed !== false || !result.parent || result.parent.number !== 777) fail(`an open Retro issue: filed ${result.filed}, parent ${JSON.stringify(result.parent)}`)
  if ((result.delta || []).map((d) => d.noun).join() !== 'noun-2,noun-3') fail(`an open Retro issue: delta ${JSON.stringify(result.delta)}, expected noun-2 and noun-3`)

  // 10. The guard's mutation: the same listing with that title removed files again.
  checks++
  for (const listing of [[{ ...OPEN, title: 'Retro 2026-10-16' }], [{ ...OPEN, title: `Retro ${DATE} (draft)` }], []]) {
    env = newRepo();
    ({ result, labels } = await execute(env, { date: DATE }, { index: indexFor(THREE), rankedCount: 3, openRetro: listing }))
    if (!labels.includes('file') || result.filed !== true) fail(`with the listing ${JSON.stringify(listing.map((l) => l.title))}, the run did not file`)
  }

  // 11. More ranked findings than maxTickets: at most maxTickets filed, in rank order.
  checks++
  env = newRepo();
  ({ result, calls } = await execute(env, { date: DATE, maxTickets: 3 }, { index: indexFor(THREE), rankedCount: 8 }))
  const asked = /^THE TICKETS[^\n]*\n([\s\S]*?)\nDropped: /m.exec((calls.find((c) => c.label === 'file') || {}).prompt || '')
  const askedNouns = asked ? JSON.parse(asked[1]).map((f) => f.noun) : []
  if (askedNouns.join() !== 'noun-1,noun-2,noun-3') fail(`maxTickets 3: File was asked for ${askedNouns.join(', ')}`)
  if ((result.tickets || []).map((t) => t.noun).join() !== 'noun-1,noun-2,noun-3') fail(`maxTickets 3: tickets ${JSON.stringify((result.tickets || []).map((t) => t.noun))}`)
  if ((result.unfiled || []).length !== 5) fail(`maxTickets 3: unfiled ${JSON.stringify(result.unfiled)}, expected the five below the cap`)

  // 12. A resumed run whose phase files have unchanged inputs dispatches none of those phases' agents;
  //     a changed knownIssues re-dispatches Rank and File and nothing before them.
  checks++
  env = newRepo()
  const first = await execute(env, { date: DATE }, { index: indexFor(THREE) })
  const second = await execute(env, { date: DATE }, { index: indexFor(THREE) })
  if (second.labels.join() !== 'state') fail(`resumed with unchanged inputs: dispatched ${second.labels.join(', ')}`)
  if (JSON.stringify(second.result.tickets) !== JSON.stringify(first.result.tickets)) fail('resumed: the reused File result differs from the first run\'s')
  if ((second.result.reused || []).length !== 9 - 1) fail(`resumed: reused ${JSON.stringify(second.result.reused)}, expected all eight phase files`)
  const third = await execute(env, { date: DATE, knownIssues: [68] }, { index: indexFor(THREE) })
  if (third.labels.sort().join() !== 'file,rank,state') fail(`knownIssues changed: dispatched ${third.labels.join(', ')}, expected state, rank and file`)

  // 13. A RED verdict no lane cites reaches Rank as a repeated-failure finding citing the log's last
  //     lines; a RED log a lane did cite gets no second one.
  checks++
  const RED = '/x/log/ws1-infra-t0-implement.txt'
  // A record line can carry a secret (AGENTS.md: N-16's canaries); the script's own finding quotes the
  // verdict line and cites the rest by range, so a canary in the last lines never reaches a ticket.
  const CANARY = 'retro-canary-7f3a91'
  const redEntry = logEntry(RED, 'ws1-infra-t0', 'RED', { lastLines: ['  failed: 1', `Set-Cookie: __Host-spa=${CANARY}`, 'GATE: RED'] })
  env = newRepo();
  ({ calls } = await execute(env, { date: DATE }, { index: indexFor(THREE, [redEntry, logEntry('/x/log/g.txt', 'ws3-identity-t0', 'GREEN')]) }))
  const rankCall = calls.find((c) => c.label === 'rank')
  const addedM = /one for each RED verdict line no lane cited; rank them with the rest: (\[.*\])$/m.exec(rankCall ? rankCall.prompt : '')
  const addedList = addedM ? JSON.parse(addedM[1]) : []
  if (addedList.length !== 1 || addedList[0].type !== 'repeated-failure' || addedList[0].evidence[0].ref !== `${RED}:60` || !/GATE: RED/.test(addedList[0].evidence[0].quote)) fail(`a RED log no lane cited: Rank was handed ${JSON.stringify(addedList)}`)
  if (calls.some((c) => c.label !== 'state' && c.label !== 'gather' && !c.label.startsWith('lane:') && c.prompt.includes(CANARY))) fail('a canary in a RED log\'s last lines reached the Rank or File prompt')
  if (!/ws1-infra-t0-implement\.txt/.test((calls.find((c) => c.label === 'lane:ws1-infra') || {}).prompt || '')) fail('the RED log is not named in its lane\'s prompt')
  env = newRepo();
  ({ calls } = await execute(env, { date: DATE }, {
    index: indexFor(THREE, [logEntry(RED, 'ws1-infra-t0', 'RED')]),
    laneFindings: { 'ws1-infra': [finding('red', { type: 'repeated-failure', evidence: [{ kind: 'check-log', ref: `${RED}:60`, lines: '56-60', quote: 'GATE: RED' }] })] },
  }))
  const again = /rank them with the rest: (\[.*\])$/m.exec((calls.find((c) => c.label === 'rank') || {}).prompt || '')
  if (!again || again[1] !== '[]') fail(`a RED log a lane cited was given a second finding: ${again && again[1]}`)
  // The evidence rules let a ref be any path:line, so a lane citing the log by a relative path has cited it.
  env = newRepo();
  ({ calls } = await execute(env, { date: DATE }, {
    index: indexFor(THREE, [logEntry(RED, 'ws1-infra-t0', 'RED')]),
    laneFindings: { 'ws1-infra': [finding('red', { type: 'repeated-failure', evidence: [{ kind: 'check-log', ref: 'log/ws1-infra-t0-implement.txt:60', lines: '56-60', quote: 'GATE: RED' }] })] },
  }))
  const relative = /rank them with the rest: (\[.*\])$/m.exec((calls.find((c) => c.label === 'rank') || {}).prompt || '')
  if (!relative || relative[1] !== '[]') fail(`a RED log a lane cited by a relative path was given a second finding: ${relative && relative[1]}`)

  // 14. issue alone: the date comes from the state read, and names the scratch directory.
  checks++
  env = newRepo();
  ({ result, labels } = await execute(env, { issue: 226 }, { index: indexFor(THREE), stateDate: '2026-10-21' }))
  if (result.date !== '2026-10-21' || !String(result.scratch).endsWith('/retro-2026-10-21')) fail(`issue alone: date ${result.date}, scratch ${result.scratch}`)
  if (labels[0] !== 'state') fail('issue alone: the state read did not run first')

  // 15. A File run GitHub could not reach is not a finished phase: a relaunch dispatches File again.
  checks++
  env = newRepo()
  const cut = await execute(env, { date: DATE }, { index: indexFor(THREE), fileUnreachable: true })
  if (cut.result.unreachable !== true || cut.result.filed !== false) fail(`unreachable: returned unreachable ${cut.result.unreachable}, filed ${cut.result.filed}`)
  const retry = await execute(env, { date: DATE }, { index: indexFor(THREE) })
  if (!retry.labels.includes('file') || retry.result.filed !== true) fail(`relaunched after an unreachable File: dispatched ${retry.labels.join(', ')}, filed ${retry.result.filed}`)

  if (env.thrown && env.thrown.length) fail(`a lane thunk threw: ${env.thrown[0]}`)
} catch (e) {
  console.error(`retro tests: the script did not run: ${e && e.stack ? e.stack : e}`)
  process.exit(2)
}

if (failures.length) {
  console.log(`retro tests: FAILED, ${failures.length} failure(s) in ${checks} checks, on ${path.basename(scriptPath)}:`)
  failures.forEach((f) => console.log(`  - ${f}`))
  process.exit(1)
}
console.log(`retro tests: pass: ${checks} checks, on ${path.basename(scriptPath)}`)
