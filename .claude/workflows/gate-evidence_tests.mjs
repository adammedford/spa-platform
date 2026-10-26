// The gate-evidence script's test companion. Owner: integrator. Tracked beside the script (#265),
// named for it as truth-audit_tests.mjs and pins-sweep_tests.mjs are for theirs.
//
//   node .claude/workflows/gate-evidence_tests.mjs [script]
//
// Runs gate-evidence.js end to end with agent, parallel, log and phase stubbed, and with git and gh
// stubbed where the script reaches them: through its agents, each of which the stub plays from a
// served fixture as its prompt asks. The Scope agent serves the ladder, the tag listings, origin's
// main, the gate's row of the gates table, the roadmap script's row states, the change requests,
// the open issues, CI, the merges since the previous tag with their review records, and the durable
// results it reads from disk under the run's log for the base it resolved; the Locate agents serve
// the tests citing the ids their prompt names; the Prove agents play each item their prompt lists
// and write its result file; the Row-audit agents serve each row's merges; the Report agent writes
// the report its prompt carries and creates or edits the issue its prompt names. No agent, gh, git
// or network call is made. The script's repo argument is a throwaway git repository with this
// repository's .gitignore, so a write outside the log shows in that repository's status. Every
// stub's return is validated against the schema the script passed. Exits 0 when every check holds,
// 1 when one fails (each failure printed), 2 when the script cannot run. Its last line is its verdict.
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const scriptPath = process.argv[2] || path.join(here, 'gate-evidence.js')
const top = path.resolve(here, '..', '..')
const SHA = 'a'.repeat(40)
const OTHER = 'c'.repeat(40)
const DATE = '2026-10-21'
const LOG_REL = '.scratch/gate-evidence/log'
// A fixture's issue number, built so the steering check's citation search does not read the made-up
// issues of these fixtures as citations.
const cite = (n) => '#' + n

// ---------------------------------------------------------------- a minimal JSON Schema check
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

// ---------------------------------------------------------------- the trunk the stubs serve
const LADDER = [
  ['S1', 'G1a', 'skeleton-v1'], ['S2', 'G0', 'g0-mechanism'], ['S3', 'G1b', 'contracts-v1'], ['S4', 'G2', 'g2-local-slice'],
  ['S5', 'G3', 'g3-deployed-slice'], ['S6', 'G4', 'g4-negative-green'], ['S7', 'G5', 'g5-phase-1-accepted'],
  ['S8', 'G6', 'g6-continuity'], ['S9', 'G7', 'g7-pingfederate'], ['S10', 'G8', 'g8-enterprise-ready'], ['SX', 'none', '—'],
].map(([stage, gate, tag]) => ({ stage, gate, tag }))
const TAGGED = ['skeleton-v1', 'g0-mechanism', 'contracts-v1', 'g2-local-slice']
const range = (p, a, b) => Array.from({ length: b - a + 1 }, (_, i) => `${p}-${String(a + i).padStart(2, '0')}`)
const ids = (list, tiers) => list.map((id) => ({ id, tiers }))
const row = (r, owner = 'ws1-aws', idsNamed = []) => ({ row: r, owner, doneWhen: `the tests of ${r} pass`, ids: idsNamed })
// The gates the stub's Scope agent reads. G3: deployed F- clauses and P- checks; G4: 26 hermetic N-
// ids and N-26 deployed; G6 has no exit test (Phase 2). READY is a gate with nothing withheld.
const GATES = {
  G3: { exitTest: 'F-01 to F-06 and F-08, deployed. P-01 to P-06.', ids: [...ids([...range('F', 1, 6), 'F-08'], ['D']), ...ids(range('P', 1, 3), ['H']), ...ids(range('P', 4, 6), ['D'])], rows: [row('1.5', 'ws1-aws', ['P-04']), row('1.7'), row('6.9', 'ws6-verification')] },
  G4: { exitTest: 'N-01 to N-25 and N-27, hermetic. The deployed subset, which adds N-26.', ids: [...ids([...range('N', 1, 25), 'N-27'], ['H']), ...ids(['N-26'], ['D'])], rows: [row('6.3', 'ws6-verification')] },
  G5: { exitTest: 'F-07 in both tiers, and all seven acceptance criteria signed off', ids: ids(['F-07'], ['H', 'D']), rows: [row('6.7', 'ws6-verification')], criteria: [{ criterion: '6 Third SPA onboarded', ids: ['F-07'] }] },
}
const READY = { exitTest: 'N-01 to N-03, hermetic.', ids: ids(range('N', 1, 3), ['H']), rows: [row('4.1', 'ws4-api', ['N-01']), row('4.2', 'ws4-api')] }

const made = []
process.on('exit', () => { for (const dir of made) fs.rmSync(dir, { recursive: true, force: true }) })
const newRepo = () => {
  const repo = fs.mkdtempSync(path.join(os.tmpdir(), 'gate-evidence-tests-'))
  const wt = fs.mkdtempSync(path.join(os.tmpdir(), 'gate-evidence-wt-'))
  made.push(repo, wt)
  const git = (...a) => execFileSync('git', ['-C', repo, ...a], { encoding: 'utf8' })
  git('init', '-q')
  fs.copyFileSync(path.join(top, '.gitignore'), path.join(repo, '.gitignore'))
  fs.writeFileSync(path.join(repo, 'README.md'), 'tracked\n')
  git('add', '-A')
  git('-c', 'user.name=t', '-c', 'user.email=t@t', 'commit', '-q', '-m', 'start')
  return { repo, wt, git, thrown: [], schemaFailures: [], peak: 0, provePeak: 0, gh: [], items: [] }
}

const dataLine = (prompt, marker) => {
  const m = new RegExp(`^${marker}: (.*)$`, 'm').exec(prompt)
  return m ? JSON.parse(m[1]) : null
}
const block = (prompt, name) => {
  const m = new RegExp(`^BEGIN ${name}\\n([\\s\\S]*?)\\nEND ${name}$`, 'm').exec(prompt)
  return m ? m[1] : null
}
const write = (file, text) => { fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, text) }

// The tests citing an id: served.tests[id], else a composed and an owner test for a hermetic id and a
// deployed browser test beside them for an id the gate requires deployed (its hermetic twins too).
const testsFor = (served, id) => {
  if (served.tests && served.tests[id]) return served.tests[id]
  const t = (name, project, role, tier, extra = {}) => ({ test: name, project, runner: 'dotnet', owner: role === 'composed' ? 'ws6-verification' : 'ws4-api', role, tier, inCi: true, notInCiWhy: '', guards: `what ${id} names`, ...extra })
  const slugId = id.replace('-', '')
  const out = [
    t(`Gateway.ComposedTests.${slugId}Composed`, 'tests/Gateway.ComposedTests', 'composed', 'H'),
    t(`Platform.Security.Tests.${slugId}Owner`, 'tests/Platform.Security.Tests', 'owner', 'H'),
  ]
  if (/^P-/.test(id)) return [t(`plan_${slugId}`, 'infra/envs/poc', 'owner', 'H', { runner: 'terraform', owner: 'ws1-aws' })]
  if ((served.deployedIds || []).includes(id)) out.push(t(`e2e.${slugId}Deployed`, 'tests/e2e', 'other', 'D'))
  return out
}

// served: { ladder, tagsReachable, tagsAll, originMain, baseResolved, gates, rowStates, changeRequests,
//           openIssues, ci, merges, tests: {id: [test]}, outcome: {test: 'proved'|'did-not-fail'|
//           'other-failed'|'red'|'one-mutation'}, audit: {row: [merge]}, deployedIds,
//           nothingFrom: [label], the agents that return nothing, as a failed agent does }
function stub(env, scriptArgs, served) {
  const calls = []
  let inFlight = 0
  let proveInFlight = 0
  globalThis.args = { repo: env.repo, wt: env.wt, ...scriptArgs }
  globalThis.phase = () => {}
  globalThis.log = (m) => { env.logs = env.logs || []; env.logs.push(String(m)) }
  globalThis.parallel = (fns) => Promise.all(fns.map((f) => f().catch((e) => { env.thrown.push(e); return null })))
  const LOG = path.join(env.repo, LOG_REL)
  globalThis.agent = async (prompt, opts) => {
    const label = opts.label
    inFlight++
    env.peak = Math.max(env.peak, inFlight)
    if (label.startsWith('prove')) { proveInFlight++; env.provePeak = Math.max(env.provePeak, proveInFlight) }
    try {
      await new Promise((r) => setTimeout(r, 3))
      calls.push({ label, prompt, schema: opts.schema })
      if ((served.nothingFrom || []).includes(label)) return null
      let out
      if (label === 'scope') {
        const want = dataLine(prompt, 'GATE')
        const baseArg = dataLine(prompt, 'BASE ARGUMENT')
        const originMain = served.originMain || SHA
        const base = baseArg ? (served.baseResolved || baseArg) : originMain
        const ladder = served.ladder || LADDER
        const reach = served.tagsReachable || TAGGED
        const gname = want === 'first untagged' ? (ladder.find((l) => /^G/.test(l.gate) && !reach.includes(l.tag)) || {}).gate || '' : want
        const gates = { ...GATES, ...(served.gates || {}) }
        const gd = gates[gname]
        const prevIdx = ladder.findIndex((l) => l.gate === gname)
        const prev = ladder.slice(0, Math.max(0, prevIdx)).reverse().find((l) => reach.includes(l.tag))
        const dir = path.join(LOG, 'results', base)
        const durable = fs.existsSync(dir) ? fs.readdirSync(dir).filter((f) => f.endsWith('.json')).sort().map((f) => JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8'))) : []
        env.durableServed = durable.length
        write(path.join(LOG, `${DATE}-${base.slice(0, 12)}`, 'checkout-at-start.txt'), '')
        out = {
          fetched: true, reason: '', date: DATE, base, originMain,
          tagsReachable: reach, tagsAll: served.tagsAll || reach, ladder,
          gate: gd
            ? { gate: gname, phase: 1, defined: true, exitTest: gd.exitTest, previousTag: prev ? prev.tag : '', ids: gd.ids, criteria: gd.criteria || [], rows: gd.rows }
            : { gate: gname, phase: 2, defined: false, exitTest: '', previousTag: prev ? prev.tag : '', ids: [], criteria: [], rows: [] },
          rowStates: served.rowStates || { '1.5': 'part-landed', '1.7': 'open', '6.9': 'open', '6.3': 'part-landed', '6.7': 'open', '4.1': 'landed', '4.2': 'landed' },
          changeRequests: served.changeRequests || [], openIssues: served.openIssues || [],
          ci: served.ci || { url: 'https://github.com/adammedford/spa-platform/actions/runs/1', sha: base, jobs: [{ name: 'Documents', conclusion: 'success' }, { name: 'Build and test', conclusion: 'skipped' }] },
          lastFull: { sha: 'd'.repeat(40), url: 'https://github.com/adammedford/spa-platform/actions/runs/0' },
          merges: served.merges || [{ sha: 'e'.repeat(40), subject: 'Merge task 6.2: the composed suite', pr: 47, head: 'f'.repeat(40), cleared: true, record: 'https://github.com/adammedford/spa-platform/pull/47#issuecomment-1' }],
          durable,
        }
      } else if (label.startsWith('locate')) {
        const want = dataLine(prompt, 'IDS') || []
        env.locateIds = env.locateIds || []
        env.locateIds.push(want)
        out = { ids: want.map((id) => ({ id, tests: testsFor(served, id), ownerProof: true, composedProof: true, missing: '', wouldRun: 'the deployed gate runs of row 6.9 over the tunnel of row 1.6b' })) }
      } else if (label.startsWith('prove')) {
        const items = dataLine(prompt, 'ITEMS') || []
        const base = (/worktree add --detach \S+ ([0-9a-f]{40})/.exec(prompt) || [])[1]
        env.items.push(...items)
        out = {
          items: items.map((it) => {
            const o = (served.outcome || {})[it.test] || 'proved'
            const m = (broken, failed, citingFailed) => ({ broken, failed, total: 50, failingNames: citingFailed ? [it.test] : failed ? ['Some.OtherTest'] : [], citingFailed })
            const rec = {
              key: it.key, baseline: o === 'red' ? 'red' : 'green', timedOut: false,
              mutations: o === 'proved' ? [m(`inverted the guard of ${it.id}`, 3, true)]
                : o === 'did-not-fail' ? [m('inverted the guard', 0, false), m('deleted the attribute', 0, false)]
                  : o === 'other-failed' ? [m('swapped two rule ids', 2, false)]
                    : o === 'one-mutation' ? [m('inverted the guard', 0, false)] : [],
              why: '',
            }
            write(it.resultFile, JSON.stringify({ base, id: it.id, test: it.test, project: it.project, ...rec }))
            return rec
          }),
          cleanup: { worktreeRemoved: true, listening: [], checkoutChanged: [] },
        }
      } else if (label.startsWith('audit')) {
        const rows = dataLine(prompt, 'ROWS') || []
        env.auditRows = env.auditRows || []
        env.auditRows.push(rows)
        out = {
          rows: rows.map((r) => ({ row: r.row, merges: (served.audit || {})[r.row] || [{ sha: '1'.repeat(40), subject: `Merge task ${r.row}: the work`, form: 'landed', outstanding: [], ownership: 'pass', ownershipDetail: '' }], why: '' })),
          cleanup: { worktreeRemoved: true, checkoutChanged: [] },
        }
      } else if (label === 'report') {
        const text = block(prompt, 'REPORT')
        const outFile = (/^OUTPUT FILE: (\S+)$/m.exec(prompt) || [])[1]
        const fmt = dataLine(prompt, 'CHECKOUT FORMAT')
        write(outFile, text.replace(/^CHECKOUT: pending$/m, fmt.clean))
        const target = dataLine(prompt, 'REPORT ISSUE')
        let issue = 0
        if (target.mode === 'edit') { issue = target.issue; env.gh.push({ action: 'edit', issue, title: target.title }) }
        if (target.mode === 'create') { issue = 950; env.gh.push({ action: 'create', title: target.title }) }
        out = { checkout: served.reportCheckout || { trackedChanged: [], newUntracked: [] }, worktreesLeft: [], issue, url: `https://github.com/adammedford/spa-platform/issues/${issue}`, posted: true, reason: '' }
      } else throw new Error(`unexpected agent label ${label}`)
      const bad = validate(opts.schema, out)
      if (bad.length) env.schemaFailures.push(`${label}: ${bad.slice(0, 3).join('; ')}`)
      for (const x of prompt.matchAll(/^PHASE FILE: (\S+)$/gm)) write(x[1], JSON.stringify(out))
      return out
    } finally {
      inFlight--
      if (label.startsWith('prove')) proveInFlight--
    }
  }
  return calls
}

const allCalls = []
const driftAll = []
async function execute(env, scriptArgs = {}, served = {}) {
  const calls = stub(env, scriptArgs, served)
  const src = fs.readFileSync(scriptPath, 'utf8').replace(/^export const meta/m, 'const meta')
  const result = await new Function('return (async () => {' + src + '\n})()')()
  for (const c of calls) allCalls.push({ ...c, repo: env.repo, wt: env.wt })
  for (const f of env.schemaFailures.splice(0)) driftAll.push(f)
  for (const t of env.thrown.splice(0)) driftAll.push(`a thunk threw: ${t}`)
  const labels = calls.map((c) => c.label)
  return { result, calls, labels, n: (p) => labels.filter((l) => l.startsWith(p)).length, prompt: (l) => (calls.find((c) => c.label === l) || {}).prompt || '' }
}
const loadMeta = () => {
  const src = fs.readFileSync(scriptPath, 'utf8')
  const m = /^export const meta = ([\s\S]*?\n})\n/m.exec(src)
  return m ? new Function(`return (${m[1]})`)() : null
}
// The commands a prompt asks an agent to run: its backtick spans, outside the data it carries (a
// BEGIN/END block, or a NAME: [json] line), which are never run.
const commands = (prompt) => {
  const text = prompt.replace(/^BEGIN (\S+)\n[\s\S]*?\nEND \1$/gm, '').replace(/^[A-Z][A-Z ]*: [[{"].*$/gm, '')
  return [...text.matchAll(/`([^`\n]+)`/g)].map((m) => m[1])
}
const section = (text, heading) => {
  const m = new RegExp(`^## ${heading}\\n([\\s\\S]*?)(?=^## |$(?![\\s\\S]))`, 'm').exec(text || '')
  return m ? m[1] : null
}
const tableRow = (text, id) => ((section(text, 'Evidence') || '').split('\n').find((l) => l.startsWith(`| ${id} |`)) || '')

const failures = []
const fail = (msg) => failures.push(msg)
let checks = 0

try {
  // 1. Box 2: meta names gate-evidence and the five phases; exactly the arguments of #80 ->
  //    "Arguments" are accepted, and anything else stops the run before any agent.
  checks++
  {
    const meta = loadMeta()
    if (!meta || meta.name !== 'gate-evidence') fail(`meta.name is ${meta && meta.name}, expected gate-evidence`)
    const titles = ((meta && meta.phases) || []).map((p) => p.title)
    if (titles.join() !== 'Scope,Locate,Prove,Row audit,Report') fail(`meta phases ${titles.join(', ')}; expected Scope, Locate, Prove, Row audit, Report`)
    for (const [label, a] of [
      ['an unknown argument', { files: ['x'] }],
      ['dryRun, which this workflow does not take', { dryRun: true }],
      ['maxParallel zero', { maxParallel: 0 }],
      ['mutationCap zero', { mutationCap: 0 }],
      ['auditRows as a string', { auditRows: 'false' }],
      ['a relative repo', { repo: 'spa-platform' }],
      ['wt inside the repository', (env) => ({ wt: `${env.repo}/wt` })],
      ['a bad date', { date: '21-10-2026' }],
      ['only not ids', { only: ['N16'] }],
      ['skipIds empty', { skipIds: [] }],
      ['a gate that is not one', { gate: 'S5' }],
    ]) {
      const env = newRepo()
      const { result, calls } = await execute(env, typeof a === 'function' ? a(env) : a)
      if (!result || !/bad launch/.test(result.halted || '') || calls.length) fail(`${label}: ${calls.length} agent(s) ran; returned ${JSON.stringify(result).slice(0, 200)}`)
    }
    const env = newRepo()
    const all = { gate: 'G4', maxParallel: 2, mutationCap: 2, auditRows: true, base: SHA, date: DATE, only: ['N-01', 'N-02'], skipIds: ['N-02'] }
    const { result } = await execute(env, all)
    if (result.halted) fail(`the ten arguments were refused: ${result.halted}`)
    if (result.gate !== 'G4') fail(`gate G4 ran ${result.gate}`)
  }

  // 2. Box 4: with no gate, the run picks the first ladder gate whose tag the stubbed listing does not
  //    reach.
  checks++
  {
    const a = await execute(newRepo())
    if (a.result.gate !== 'G3' || a.result.tag !== 'g3-deployed-slice') fail(`with G2 the last tag reached, the default gate is ${a.result.gate} (${a.result.tag}), expected G3`)
    if (dataLine(a.prompt('scope'), 'GATE') !== 'first untagged') fail('the Scope prompt was not asked for the first untagged gate')
    const b = await execute(newRepo(), {}, { tagsReachable: [...TAGGED, 'g3-deployed-slice'] })
    if (b.result.gate !== 'G4') fail(`with g3-deployed-slice reached, the default gate is ${b.result.gate}, expected G4`)
    // Out of order: the ladder's order decides, not the listing's.
    const c = await execute(newRepo(), {}, { tagsReachable: ['g2-local-slice', 'skeleton-v1', 'contracts-v1', 'g0-mechanism', 'g4-negative-green'] })
    if (c.result.gate !== 'G3') fail(`with g4 reached but not g3, the default gate is ${c.result.gate}, expected G3`)
    const named = await execute(newRepo(), { gate: 'G5' })
    if (named.result.gate !== 'G5' || dataLine(named.prompt('scope'), 'GATE') !== 'G5') fail(`gate G5 ran ${named.result.gate}`)
  }

  // 3. Box 5: each refusal ends the run with a report saying why, and no Prove agent is dispatched.
  checks++
  {
    for (const [label, a, served, why] of [
      ['base differs from origin', { base: 'feature' }, { baseResolved: OTHER }, /trunk differs from origin/],
      ['the gate\'s tag exists', { gate: 'G3' }, { tagsAll: [...TAGGED, 'g3-deployed-slice'] }, /tag g3-deployed-slice already exists/],
      ['a Phase 2 gate', { gate: 'G6' }, {}, /G6 is a Phase 2 gate/],
      ['a Phase 2 gate by default', {}, { tagsReachable: [...TAGGED, 'g3-deployed-slice', 'g4-negative-green', 'g5-phase-1-accepted'] }, /G6 is a Phase 2 gate/],
    ]) {
      const env = newRepo()
      const { result, labels, n } = await execute(env, a, served)
      if (n('prove') || n('locate') || n('audit')) fail(`${label}: dispatched ${labels.join(', ')}`)
      if (labels.join() !== 'scope,report') fail(`${label}: dispatched ${labels.join(', ')}, expected scope then report`)
      if (result.verdict !== 'tag withheld' || !result.reasons.some((r) => why.test(r))) fail(`${label}: verdict ${result.verdict}, reasons ${JSON.stringify(result.reasons)}`)
      if (!why.test(result.report.text) || !/stopped after Scope/.test(result.report.text)) fail(`${label}: the report does not say why: ${(result.report.text || '').slice(0, 300)}`)
      if (env.gh.length !== 1) fail(`${label}: ${env.gh.length} report writes`)
    }
    // The base matching origin is not refused, so the base check above can fail.
    const ok = await execute(newRepo(), { base: 'main' }, { baseResolved: SHA })
    if (ok.result.refused) fail(`a base equal to origin's main was refused: ${JSON.stringify(ok.result.reasons)}`)
  }

  // 4. Box 6: fan-out bounds.
  checks++
  {
    // G4: 27 ids, 9 landed rows, a cap of 20 over many projects.
    const rows = Array.from({ length: 9 }, (_, i) => row(`4.${i + 1}`, 'ws4-api'))
    const states = Object.fromEntries(rows.map((r) => [r.row, 'landed']))
    const tests = {}
    for (const id of [...range('N', 1, 25), 'N-27']) tests[id] = [{ test: `T.${id}`, project: `tests/Project${id}`, runner: 'dotnet', owner: 'ws4-api', role: 'owner', tier: 'H', inCi: true, notInCiWhy: '', guards: 'g' }]
    for (const mp of [3, 2, 1]) {
      const env = newRepo()
      const { result, n } = await execute(env, { gate: 'G4', mutationCap: 20, maxParallel: mp }, { gates: { G4: { ...GATES.G4, rows } }, rowStates: states, tests })
      if (n('scope') !== 1) fail(`maxParallel ${mp}: ${n('scope')} Scope agents`)
      if (n('locate') < 1 || n('locate') > 3) fail(`maxParallel ${mp}: ${n('locate')} Locate agents for 27 ids, expected 1 to 3`)
      const located = (env.locateIds || []).flat().sort()
      if (located.length !== 27 || new Set(located).size !== 27) fail(`maxParallel ${mp}: the Locate agents were given ${located.length} ids`)
      if (n('prove') > mp || n('prove') < 1) fail(`maxParallel ${mp}: ${n('prove')} Prove agents`)
      if (env.provePeak > mp) fail(`maxParallel ${mp}: ${env.provePeak} Prove agents in flight`)
      if (env.peak > mp) fail(`maxParallel ${mp}: ${env.peak} agents in flight`)
      if (env.items.length !== 20) fail(`maxParallel ${mp}: ${env.items.length} items dealt under a cap of 20`)
      if (n('audit') !== 3) fail(`maxParallel ${mp}: ${n('audit')} Row-audit agents for 9 rows, expected 3`)
      if ((env.auditRows || []).some((b) => b.length > 4)) fail(`maxParallel ${mp}: a Row-audit agent holds more than four rows`)
      if (n('report') !== 1) fail(`maxParallel ${mp}: ${n('report')} Report agents`)
      if (result.halted) fail(`maxParallel ${mp}: halted ${result.halted}`)
    }
    const env = newRepo()
    await execute(env, { gate: 'G4', mutationCap: 20 }, { gates: { G4: { ...GATES.G4, rows } }, rowStates: states, tests })
    if (env.peak !== 3) fail(`the default maxParallel peaked at ${env.peak}, expected exactly 3`)
    // Four rows are one agent, five are two.
    for (const [k, want] of [[4, 1], [5, 2], [1, 1]]) {
      const rs = rows.slice(0, k)
      const r = await execute(newRepo(), { gate: 'G4' }, { gates: { G4: { ...GATES.G4, rows: rs } }, rowStates: states, tests })
      if (r.n('audit') !== want) fail(`${k} landed rows made ${r.n('audit')} Row-audit agents, expected ${want}`)
    }
  }

  // 5. Box 7: items are dealt round-robin across ids and cut at the cap; the untried appear as
  //    "not mutated this run".
  checks++
  {
    const three = (id) => ['composed', 'owner', 'other'].map((role, i) => ({ test: `${id}.${role}`, project: `tests/P${i}`, runner: 'dotnet', owner: 'ws4-api', role, tier: 'H', inCi: true, notInCiWhy: '', guards: 'g' }))
    const env = newRepo()
    const { result } = await execute(env, { gate: 'G4', mutationCap: 4 }, {
      gates: { G4: { exitTest: 'N-01 to N-03, hermetic.', ids: ids(range('N', 1, 3), ['H']), rows: [] } },
      tests: { 'N-01': three('N-01'), 'N-02': three('N-02'), 'N-03': three('N-03') },
    })
    const dealt = env.items.map((i) => i.test).sort()
    const want = ['N-01.composed', 'N-02.composed', 'N-03.composed', 'N-01.owner'].sort()
    if (dealt.join() !== want.join()) fail(`a cap of 4 over three ids of three tests dealt ${dealt.join(', ')}, expected ${want.join(', ')}`)
    if ((result.notMutatedThisRun || []).length !== 5) fail(`the return lists ${(result.notMutatedThisRun || []).length} untried items, expected 5`)
    if (!/N-03\.owner \(tests\/P1, ws4-api\): not mutated this run/.test(tableRow(result.report.text, 'N-03'))) fail(`N-03's untried owner test is not "not mutated this run": ${tableRow(result.report.text, 'N-03')}`)
    if (!/N-01\.owner[^;]*: proved/.test(tableRow(result.report.text, 'N-01'))) fail(`N-01's second test was dealt but not shown proved: ${tableRow(result.report.text, 'N-01')}`)
    if (result.counts.notMutated !== 5 || result.counts.proved !== 4) fail(`counts ${JSON.stringify(result.counts)}, expected 4 proved and 5 not mutated`)
    // A deployed-tier test, and a test CI does not run, are never dealt.
    const d = newRepo()
    await execute(d, { gate: 'G3', mutationCap: 30 }, { deployedIds: [...range('F', 1, 6), 'F-08'] })
    if (d.items.some((i) => /Deployed$/.test(i.test))) fail('a deployed-tier test was dealt to a Prove agent')
  }

  // 6. Box 8: a second run on the same base reuses the first run's durable files, dispatches no Prove
  //    agent and produces an identical evidence table; a run on a different base ignores them.
  checks++
  {
    const env = newRepo()
    const served = { gates: { G4: { ...GATES.G4, rows: [] } } }
    const a = await execute(env, { gate: 'G4', mutationCap: 60 }, served)
    if (!a.n('prove')) fail('the first run dispatched no Prove agent')
    const files = fs.readdirSync(path.join(env.repo, LOG_REL, 'results', SHA))
    if (files.length !== env.items.length) fail(`the first run wrote ${files.length} durable files for ${env.items.length} items`)
    const b = await execute(env, { gate: 'G4', mutationCap: 60 }, served)
    if (b.n('prove')) fail(`the second run on the same base dispatched ${b.n('prove')} Prove agent(s)`)
    if (!env.durableServed) fail('the second run\'s Scope agent read no durable file')
    if (section(a.result.report.text, 'Evidence') !== section(b.result.report.text, 'Evidence') || !section(a.result.report.text, 'Evidence')) fail('the second run\'s evidence table differs from the first\'s')
    if (a.result.report.text !== b.result.report.text) fail('the second run\'s report differs from the first\'s')
    if (b.result.reused !== files.length) fail(`the second run reused ${b.result.reused} results, expected ${files.length}`)
    // A third run, wider: only the tests the first did not reach are dealt.
    const before = env.items.length
    const capped = newRepo()
    await execute(capped, { gate: 'G4', mutationCap: 4 }, served)
    const firstFour = capped.items.map((i) => i.key)
    await execute(capped, { gate: 'G4', mutationCap: 4 }, served)
    const second = capped.items.slice(4).map((i) => i.key)
    if (second.length !== 4 || second.some((k) => firstFour.includes(k))) fail(`a capped rerun dealt again what the first had proved: ${second.join(', ')}`)
    // A different base starts clean.
    const c = await execute(env, { gate: 'G4', mutationCap: 60 }, { ...served, originMain: OTHER })
    if (!c.n('prove') || c.result.base !== OTHER) fail(`a run on a new base dispatched ${c.n('prove')} Prove agents at ${c.result.base}`)
    if (env.items.length - before !== before) fail(`a run on a new base dealt ${env.items.length - before} items, expected all ${before}`)
  }

  // 7. Box 9: the verdict rules.
  checks++
  {
    const ready = await execute(newRepo(), { gate: 'G3' }, { gates: { G3: READY } })
    if (ready.result.verdict !== 'tag ready' || ready.result.reasons.length) fail(`with nothing withheld the verdict is ${ready.result.verdict}: ${JSON.stringify(ready.result.reasons)}`)
    if (!/\*\*Verdict:\*\* tag ready/.test(ready.result.report.text)) fail('the ready report does not say tag ready')
    const base = { gates: { G3: READY } }
    for (const [label, served, args, why] of [
      ['a required id is not proved at its tier', { ...base, tests: { 'N-02': [{ test: 'T.N02', project: 'tests/X', runner: 'dotnet', owner: 'ws4-api', role: 'owner', tier: 'H', inCi: false, notInCiWhy: 'the project is not in the solution', guards: 'g' }] } }, {}, /N-02 is not proved at the hermetic tier: not in CI/],
      ['a required id with no citing test', { ...base, tests: { 'N-03': [] } }, {}, /N-03 is not proved at the hermetic tier: no citing test/],
      ['a citing test did not fail', { ...base, outcome: { 'Gateway.ComposedTests.N02Composed': 'did-not-fail' } }, {}, /Gateway\.ComposedTests\.N02Composed, citing N-02, did not fail/],
      ['a mutation that failed other tests only', { ...base, outcome: { 'Gateway.ComposedTests.N01Composed': 'other-failed' } }, {}, /N01Composed, citing N-01, did not fail/],
      ['a baseline that is red', { ...base, outcome: { 'Gateway.ComposedTests.N01Composed': 'red' } }, {}, /N-01 is not proved at the hermetic tier: baseline red/],
      ['a red baseline says trunk is not green', { ...base, outcome: { 'Gateway.ComposedTests.N01Composed': 'red' } }, {}, /tests\/Gateway\.ComposedTests failed unmutated at base, so trunk is not green here/],
      ['an id the Scope agent returned with no tier', { gates: { G3: { ...READY, ids: [...READY.ids, { id: 'N-04', tiers: [] }] } } }, {}, /"N-04" .*was not judged/],
      ['an id the Scope agent returned malformed', { gates: { G3: { ...READY, ids: [...READY.ids, { id: 'N-4', tiers: ['H'] }] } } }, {}, /"N-4" .*was not judged/],
      ['a row is not done', { ...base, audit: { '4.1': [{ sha: '2'.repeat(40), subject: 'Merge task 4.1: x', form: 'landed', outstanding: ['Outstanding test ids: N-01 (4.1)'], ownership: 'pass', ownershipDetail: '' }] } }, {}, /row 4\.1 is not done/],
      ['a row whose merge crosses owners', { ...base, audit: { '4.2': [{ sha: '3'.repeat(40), subject: 'Merge task 4.2: x', form: 'landed', outstanding: [], ownership: 'fail', ownershipDetail: 'src/Platform.Identity/X.cs is ws3-oauth' }] } }, {}, /row 4\.2 is not done/],
      ['a row with an open change request', { ...base, changeRequests: [{ file: 'contracts/changes/4.2-x.md', rows: ['4.2'], status: '**Status:** proposed' }] }, {}, /row 4\.2 is not done/],
      ['trunk differs from origin', base, { base: 'feature' }, /trunk differs from origin/],
      ['a merge since the previous tag has an open review', { ...base, merges: [{ sha: '9'.repeat(40), subject: 'Merge task 4.2: x', pr: 70, head: '8'.repeat(40), cleared: false, record: 'no record names the head' }] }, {}, /merge 999999999999 since g2-local-slice has an open review/],
      ['a part-landed row', { ...base, rowStates: { '4.1': 'landed', '4.2': 'part-landed' } }, {}, /row 4\.2 is not done/],
    ]) {
      const r = await execute(newRepo(), { gate: 'G3', ...args }, { baseResolved: OTHER, ...served })
      if (r.result.verdict !== 'tag withheld' || !r.result.reasons.some((x) => why.test(x))) fail(`${label}: verdict ${r.result.verdict}, reasons ${JSON.stringify(r.result.reasons)}`)
      if (!why.test(r.result.report.text)) fail(`${label}: the report does not name the reason`)
    }
    // A deployed clause is never counted as proved, even when a test claims the deployed tier and a
    // durable record claims it proved.
    const env = newRepo()
    const dTest = { test: 'e2e.F01Deployed', project: 'tests/e2e', runner: 'dotnet', owner: 'ws6-verification', role: 'composed', tier: 'D', inCi: true, notInCiWhy: '', guards: 'g' }
    const forged = path.join(env.repo, LOG_REL, 'results', SHA, 'forged.json')
    write(forged, JSON.stringify({ base: SHA, key: 'F-01|e2e.F01Deployed|tests/e2e', id: 'F-01', test: 'e2e.F01Deployed', project: 'tests/e2e', baseline: 'green', timedOut: false, mutations: [{ broken: 'x', failed: 1, total: 1, failingNames: ['e2e.F01Deployed'], citingFailed: true }], why: '' }))
    const dep = await execute(env, { gate: 'G3' }, { gates: { G3: { exitTest: 'F-01, deployed.', ids: ids(['F-01'], ['D']), rows: [] } }, tests: { 'F-01': [dTest] } })
    if (dep.result.verdict !== 'tag withheld' || !dep.result.reasons.some((x) => /F-01 is not proved at the deployed tier: deployed, not run here/.test(x))) fail(`a deployed clause: ${dep.result.verdict} ${JSON.stringify(dep.result.reasons)}`)
    if (dep.result.counts.notRunHere !== 1 || dep.result.counts.proved !== 0) fail(`a deployed clause counted ${JSON.stringify(dep.result.counts)}`)
    if (!/deployed, not run here \(would run it: the deployed gate runs/.test(tableRow(dep.result.report.text, 'F-01'))) fail(`the deployed row does not name what would run it: ${tableRow(dep.result.report.text, 'F-01')}`)
    // The open reviews are the range's merges (docs/agents/landing-work.md -> "Gates"), so a direct
    // commit on trunk, such as a generated roadmap refresh, is not read as a merge with no review.
    if (!commands(ready.prompt('scope')).some((c) => /\blog --first-parent --merges\b.*<previousTag>\.\.<base>/.test(c))) fail('the Scope prompt does not list the merges since the previous tag with --first-parent --merges')
    // The report's closing section claims no clean checkout the Report agent did not find.
    const dirty = await execute(newRepo(), { gate: 'G3' }, { gates: { G3: READY }, reportCheckout: { trackedChanged: ['README.md'], newUntracked: [] } })
    const closing = section(dirty.result.report.text, 'What this run did not do') || ''
    if (!/tracked files changed: README\.md/.test(closing) || /wrote no tracked file/.test(closing)) fail(`with README.md changed, the closing section says: ${closing.trim()}`)
    // Both tiers: hermetic proved, deployed not run here, still withheld.
    const g5 = await execute(newRepo(), { gate: 'G5' })
    if (g5.result.verdict !== 'tag withheld' || !g5.result.reasons.some((x) => /F-07 is not proved at the deployed tier/.test(x)) || g5.result.reasons.some((x) => /F-07 is not proved at the hermetic/.test(x))) fail(`F-07 in both tiers: ${JSON.stringify(g5.result.reasons)}`)
    if (!/Acceptance criteria/.test(g5.result.report.text)) fail('G5\'s report does not list its acceptance criteria')
    // A single mutation that failed nothing is not did-not-fail: a second, different one is owed.
    const one = await execute(newRepo(), { gate: 'G3' }, { ...base, outcome: { 'Gateway.ComposedTests.N01Composed': 'one-mutation' } })
    if (one.result.reasons.some((x) => /N01Composed, citing N-01, did not fail/.test(x)) || !/N01Composed[^;]*: not proved: could not run/.test(tableRow(one.result.report.text, 'N-01'))) fail(`one mutation that failed nothing: ${tableRow(one.result.report.text, 'N-01')}`)
    // The report body follows #80's order.
    const order = ['**Run:**', '**Verdict:**', '## Evidence', '## Ids with no failing-capable proof', '## Rows not done', '## Open reviews', '## DRAFT tag message', '## What this run did not do']
    const t = g5.result.report.text
    const at = order.map((h) => t.indexOf(h))
    if (at.some((i) => i < 0) || at.some((i, k) => k && i < at[k - 1])) fail(`the report's sections are out of order or missing: ${JSON.stringify(at)}`)
    if (!/```text\nG5: F-07 in both tiers, and all seven acceptance criteria signed off, proved by https:\/\/github\.com\/adammedford\/spa-platform\/actions\/runs\/1/.test(t)) fail('the DRAFT tag message is not in the landing guide\'s form')
    if (!/Deciding CI: https:\/\/github\.com\/adammedford\/spa-platform\/actions\/runs\/0/.test(t) || !/Build and test skipped/.test(t)) fail('the draft does not name the deciding run and the skipped job')
    for (const k of ['gate', 'base', 'verdict', 'counts', 'rowsNotDone', 'report']) if (!(k in g5.result)) fail(`the return has no ${k}`)
    if (!(g5.result.report.issue === 950 && /issues\/950$/.test(g5.result.report.url))) fail(`the return names report ${JSON.stringify([g5.result.report.issue, g5.result.report.url])}`)
    for (const k of ['proved', 'didNotFail', 'notRunHere', 'notMutated']) if (!Number.isInteger(g5.result.counts[k])) fail(`counts.${k} is not an integer`)
  }

  // 8. Box 10: auditRows false dispatches no Row-audit agent.
  checks++
  {
    const off = await execute(newRepo(), { gate: 'G3', auditRows: false }, { gates: { G3: READY } })
    if (off.n('audit')) fail(`auditRows false dispatched ${off.n('audit')} Row-audit agent(s)`)
    if (!off.result.reasons.some((r) => /row 4\.1 is not done/.test(r)) || !/not audited: auditRows is false/.test(off.result.report.text)) fail('a run that audited no row reports its landed rows as done')
    const on = await execute(newRepo(), { gate: 'G3' }, { gates: { G3: READY } })
    if (!on.n('audit')) fail('auditRows true dispatched no Row-audit agent, so the check above proves nothing')
  }

  // 9. Box 11: with an open "<gate> evidence, <date>" issue in the stubbed listing, the report edits
  //    it and creates no second.
  checks++
  {
    const env = newRepo()
    await execute(env, { gate: 'G3' }, { openIssues: [{ number: 812, title: `G3 evidence, ${DATE}`, rows: [], label: '', assignee: '' }, { number: 813, title: `G4 evidence, ${DATE}`, rows: [], label: '', assignee: '' }] })
    if (env.gh.length !== 1 || env.gh[0].action !== 'edit' || env.gh[0].issue !== 812) fail(`with ${cite(812)} open, the report wrote ${JSON.stringify(env.gh)}`)
    const fresh = newRepo()
    const f = await execute(fresh, { gate: 'G3' }, { openIssues: [{ number: 813, title: `G3 evidence, 2026-10-20`, rows: [], label: '', assignee: '' }] })
    if (fresh.gh.length !== 1 || fresh.gh[0].action !== 'create' || fresh.gh[0].title !== `G3 evidence, ${DATE}`) fail(`with only yesterday's open, the report wrote ${JSON.stringify(fresh.gh)}`)
    if (!/gh issue create --title "G3 evidence, 2026-10-21" --body-file/.test(f.prompt('report')) || /--label/.test(commands(f.prompt('report')).join('\n'))) fail('the create carries a label or the wrong title')
  }

  // 10. Box 12: across every mocked run, no command pushes, merges or tags, and none writes to a
  //     tracked path in the main checkout.
  checks++
  {
    const FORBIDDEN = [/\bgit\b[^`]*\bpush\b/, /\bgit\b(\s+-C\s+\S+)?\s+merge\b(?!-)/, /\bgh\s+pr\s+merge\b/, /\bgit\b(\s+-C\s+\S+)?\s+tag\b/, /\bgh\s+release\b/, /\baws\s/]
    let seen = 0
    for (const c of allCalls) {
      for (const cmd of commands(c.prompt)) {
        seen++
        for (const re of FORBIDDEN) if (re.test(cmd)) fail(`${c.label} asks for a forbidden command: ${cmd}`)
        const main = new RegExp(`git -C ${c.repo.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\s+(checkout|switch|stash|reset|commit|add|rm|restore|clean|rebase|cherry-pick|apply|am)\\b`)
        if (main.test(cmd)) fail(`${c.label} changes the main checkout: ${cmd}`)
        for (const m of cmd.matchAll(/(?:(?:^|\s)>>?|\btee\s+(?:-a\s+)?)\s+(\S+)/g)) {
          const target = m[1]
          if (target === '/dev/null') continue
          if (!(target.startsWith(path.join(c.repo, LOG_REL) + '/') || target.startsWith(c.wt + '/'))) fail(`${c.label} writes ${target}, outside the run's log and worktrees: ${cmd}`)
        }
        for (const m of cmd.matchAll(/worktree add --detach (\S+)/g)) if (!m[1].startsWith(c.wt + '/')) fail(`${c.label} adds a worktree outside wt: ${cmd}`)
      }
    }
    if (seen < 50) fail(`only ${seen} commands were read across the runs, so the search read almost nothing`)
    // The search can fail: a planted tag and a planted push are caught.
    for (const planted of ['git -C /x tag -a g3-deployed-slice', 'git -C /x push origin main', 'git -C /x merge --no-ff y']) if (!FORBIDDEN.some((re) => re.test(planted))) fail(`the forbidden search misses ${planted}`)
    if (FORBIDDEN.some((re) => re.test('git -C /x for-each-ref --merged abc refs/tags'))) fail('the forbidden search refuses a read of the tags')
    // And nothing outside the log appeared in the throwaway repository's status.
    const env = newRepo()
    await execute(env, { gate: 'G4', mutationCap: 6 })
    const status = env.git('status', '--porcelain', '--untracked-files=all').trim()
    if (status) fail(`a run left the throwaway checkout dirty: ${status}`)
  }

  // 11. Box 13: the companion is tracked by its named line, and nothing broader.
  checks++
  {
    const gi = fs.readFileSync(path.join(top, '.gitignore'), 'utf8').split('\n')
    if (!gi.includes('!.claude/workflows/gate-evidence_tests.mjs')) fail('.gitignore has no line !.claude/workflows/gate-evidence_tests.mjs')
    if (gi.some((l) => /^!\.claude\/workflows\/\*?_?tests|^!\.claude\/workflows\/\*\.mjs/.test(l))) fail('.gitignore opens the workflows folder wider than the named companion')
  }

  // 12. A Locate or Row-audit agent that returns nothing is named as failed in the reasons and the
  //     report (#277), never read as ids no test cites or rows nobody audited. Paired with the same
  //     run with every agent answering, which names no agent.
  checks++
  {
    const served = { gates: { G3: READY } }
    const named = (r, re) => r.result.reasons.some((x) => re.test(x)) && re.test(r.result.report.text)
    const LOCATE = /locate-1 returned nothing: N-01 was not located/
    const AUDIT = /audit-1 returned nothing: rows? 4\.1, 4\.2 (was|were) not audited/
    const all = await execute(newRepo(), { gate: 'G3' }, served)
    if (named(all, LOCATE) || named(all, AUDIT)) fail(`with every agent answering, a Locate or Row-audit agent is named as failed: ${JSON.stringify(all.result.reasons)}`)
    const locate = await execute(newRepo(), { gate: 'G3' }, { ...served, nothingFrom: ['locate-1'] })
    if (locate.result.verdict !== 'tag withheld' || !named(locate, LOCATE)) fail(`a null locate-1 is not named in the reasons and the report: ${JSON.stringify(locate.result.reasons)}`)
    const audit = await execute(newRepo(), { gate: 'G3' }, { ...served, nothingFrom: ['audit-1'] })
    if (audit.result.verdict !== 'tag withheld' || !named(audit, AUDIT)) fail(`a null audit-1 is not named in the reasons and the report: ${JSON.stringify(audit.result.reasons)}`)
  }

  if (driftAll.length) fail(`schema drift or thrown thunks:\n  ${driftAll.join('\n  ')}`)
  // The edit rule (#109, Q164 of #286): every prompt that carries the house rules carries the amended
  // line verbatim, Edit and Write both, and no prompt still carries the Edit-only line it replaced.
  checks++
  {
    const EDIT_RULE = '- To change an existing file with Edit or Write, open it with the Read tool first in this session, not with `cat`, `head` or `sed -n`: for a file you will change, this rule overrides the auto-mode advice to read with Bash. Open it with Read again after anything else wrote it (a script, the check, a merge, `git`). Otherwise make the change with a short script written to a file under your scratchpad and run by its path. Edit and Write refuse a file not read since its last write: 16 calls went to that refusal on 2026-10-18 and 16 over the four launches from 2026-10-19 to 2026-10-21 (#109).'
    const housed = allCalls.filter((c) => /(House|Read-only) rules \(/.test(c.prompt))
    if (!housed.length) fail('no prompt carries the house rules, so the edit-rule search read nothing')
    for (const c of housed) if (!c.prompt.includes(EDIT_RULE)) fail(`the ${c.label} prompt's house rules lack the edit rule of #109`)
    for (const c of allCalls) if (/Change a file with Edit only after/.test(c.prompt)) fail(`the ${c.label} prompt still carries the Edit-only line #109 replaced`)
  }
  // The plan policy's place in CI (#367, Q43 of #349): the Locate prompt says what ci.yml says, that
  // P-01 to P-03 and P-07 are mock-provider assertions the Terraform job's test step runs, so a
  // plan-policy test is in CI by the Terraform discovery leg and has no deployed clause to run.
  checks++
  {
    const located = allCalls.filter((c) => /^locate-/.test(c.label))
    if (!located.length) fail('no Locate prompt was dispatched, so the plan-policy search read nothing')
    for (const c of located) {
      const p = c.prompt
      if (/plan-policy check for P-01 to P-03 is not in CI while it is a notice/.test(p)) fail(`the ${c.label} prompt still calls the plan policy a notice outside CI`)
      if (/for P-01 to P-03, the plan policy of row 1\.9\)/.test(p)) fail(`the ${c.label} prompt still lists P-01 to P-03 as a deployed clause's wouldRun`)
      const inCi = (p.match(/^inCi, leg by leg[^\n]*/m) || [''])[0]
      if (!/P-01 to P-03 and P-07/.test(inCi) || !/Terraform job's test step/.test(inCi)) fail(`the ${c.label} prompt's inCi legs do not place P-01 to P-03 and P-07 under the Terraform job's test step: ${inCi.slice(-300)}`)
      // Any wording of "not in CI" in the legs, not only the sentence #367 removed (its brief's second box).
      if (/not in CI/i.test(inCi)) fail(`the ${c.label} prompt's inCi legs say something is not in CI: ${inCi.slice(-300)}`)
      const would = (p.match(/wouldRun:[^\n]*/) || [''])[0]
      if (!/"" for P-01 to P-03 and P-07/.test(would)) fail(`the ${c.label} prompt's wouldRun does not leave P-01 to P-03 and P-07 empty: ${would}`)
      // The deployed clause, up to its '"" otherwise', names what runs a deployed clause; no plan-policy id belongs in it.
      const deployed = would.split('"" otherwise')[0]
      if (/P-0[1237]\b/.test(deployed)) fail(`the ${c.label} prompt's wouldRun lists a plan-policy id as a deployed clause: ${deployed}`)
    }
  }
  // The Scope prompt's tiers (#392, from #367's review): an id named after the export, P-07 among
  // them, has its row in contracts/test-ids.md and not in docs/plan/04-tests.md, so Scope reads a tier
  // there too, and the fallback for a P- id with no tier counts P-07 with the hermetic plan checks.
  checks++
  {
    const scoped = allCalls.filter((c) => c.label === 'scope')
    if (!scoped.length) fail('no Scope prompt was dispatched, so the tier search read nothing')
    for (const c of scoped) {
      const step = (c.prompt.match(/^4\. gate:[^\n]*/m) || [''])[0]
      if (!step) { fail('the Scope prompt has no step 4, so its tier rules were not read'); continue }
      const tierRule = (step.match(/an id the cell gives no tier[^;]*?(?=;|,? and a P- id)/) || [''])[0]
      if (!/contracts\/test-ids\.md/.test(tierRule)) fail(`the Scope prompt reads an untiered id's tier only from the export: ${tierRule}`)
      const subset = (step.match(/"the deployed subset" is[^;]*/) || [''])[0]
      if (!/contracts\/test-ids\.md/.test(subset)) fail(`the Scope prompt reads the deployed subset only from the export: ${subset}`)
      const fallback = (step.match(/a P- id with none is[^)]*/) || [''])[0]
      if (!/\bH for [^;]*P-01 to P-03 and P-07\b/.test(fallback)) fail(`the Scope prompt's fallback does not tier P-07 hermetic with P-01 to P-03: ${fallback}`)
      if (/\bD for (?:(?! and H for)[^,;])*P-07\b/.test(fallback)) fail(`the Scope prompt's fallback tiers P-07 deployed: ${fallback}`)
    }
  }
} catch (e) {
  console.error(`gate-evidence_tests: the script could not run: ${e && e.stack ? e.stack : e}`)
  console.log('gate-evidence_tests: ERROR')
  process.exit(2)
}

for (const f of failures) console.error(`FAIL: ${f}`)
console.log(failures.length ? `gate-evidence_tests: ${failures.length} failure(s) across ${checks} checks` : `gate-evidence_tests: ${checks} checks, all held`)
process.exit(failures.length ? 1 : 0)
