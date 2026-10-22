// The overnight script's test companion: runs overnight.js end to end with every agent mocked, on a fixed
// ticket set, and checks how the lanes schedule. Owner: integrator. Tracked beside the script, with its
// fixtures under fixtures/, and run by CI's Documents job on every push (#213, ADR-0027).
//
//   node .claude/workflows/overnight_tests.mjs [script] [fixture]
//
// Defaults: overnight.js beside this file, and fixtures/2026-10-20-tickets.json; a later fixture sits
// beside that one and is named as the second argument. It is wall-clock timed: a 1 ms tick against
// sleeps of 30 to 180 ms. Exits 0 when every check holds, 1 when one fails (each failure printed) or when
// the script has not finished within LIMIT_MS, 2 when the script cannot run. The mock collect agent
// returns the fixture's tickets; implement, review and integrate succeed after a delay that depends on the
// owner, so the integrator lane is the slow one, as it is on a real night. No agent, gh or git call is
// made.
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const scriptPath = process.argv[2] || path.join(here, 'overnight.js')
const fixturePath = process.argv[3] || path.join(here, 'fixtures', '2026-10-20-tickets.json')
const fixture = JSON.parse(fs.readFileSync(fixturePath, 'utf8'))
const tickets = fixture.tickets
const byId = Object.fromEntries(tickets.map((t) => [t.id, t]))
const cost = { integrator: 60, 'ws1-infra': 30, 'ws2-hosting': 20, 'ws3-identity': 30, 'ws4-proxy': 20, 'ws5-fixtures': 10, 'ws6-verification': 30 }

let tick = 0
const events = []          // {at, kind, id, edge}
const logs = []            // {at, text}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const clock = setInterval(() => { tick++ }, 1)

globalThis.args = fixture.args
globalThis.phase = () => {}
globalThis.log = (text) => logs.push({ at: tick, text })
globalThis.parallel = (fns) => Promise.all(fns.map((f) => f()))
globalThis.agent = async (prompt, opts) => {
  const [kind, id] = opts.label.split(':')
  if (kind === 'collect') return { outcome: 'ok', reason: 'companion', tickets, landed: [], base: 'a'.repeat(40), shell: '3.2', shellCommand: '/bin/bash' }
  if (kind === 'grill') return { questions: [], glossaryProposals: [], adrCandidates: [], summary: 'companion' }
  if (kind === 'route') return { flow: 'agent', reason: 'companion' }
  if (kind === 'report') return { questions: 0, mainCheckoutClean: true, leftovers: '', issue: 0, url: '' }
  events.push({ at: tick, kind, id, edge: 'start' })
  await sleep(kind === 'implement' ? cost[byId[id].owner] * 3 : 30)
  events.push({ at: tick, kind, id, edge: 'end' })
  if (kind === 'implement') return { outcome: 'resolved', branch: 'b', forkSha: 'f', headSha: 'h', complete: true, outstanding: '', summary: 's', question: '', delegatedDecisions: [], proposedSteering: [] }
  if (kind === 'review') return { outcome: 'committed', headSha: 'h', findingsFixed: [], findingsSkipped: [], summary: 's', question: '', proposedSteering: [] }
  if (kind === 'integrate') return { ok: true, mergeSha: 'm', notes: '' }
  throw new Error(`unexpected agent label ${opts.label}`)
}

// A scheduler that deadlocks (a lane waiting on a ticket queued behind itself) never settles, and the tick
// alone would keep the process alive, so the run is bounded. Every mock agent of a passing run on the
// 2026-10-20 fixture, run one after another, sleeps 3.63 s (23 tickets' implement, review and integrate);
// 30 s is eight times that and over twenty times a normal run of about 1.3 s (#213).
const LIMIT_MS = 30000
const STALLED = Symbol('stalled')
let limit
let result
try {
  const src = fs.readFileSync(scriptPath, 'utf8').replace(/^export const meta/m, 'const meta')
  const run = new Function('return (async () => {' + src + '\n})()')()
  result = await Promise.race([run, new Promise((res) => { limit = setTimeout(() => res(STALLED), LIMIT_MS) })])
} catch (e) {
  clearTimeout(limit)
  clearInterval(clock)
  console.error(`overnight_tests: the script did not run: ${e && e.stack ? e.stack : e}`)
  process.exit(2)
}
clearTimeout(limit)
clearInterval(clock)

if (result === STALLED) {
  const ended = new Set(events.filter((e) => e.edge === 'end').map((e) => `${e.kind}:${e.id}`))
  const open = events.filter((e) => e.edge === 'start' && !ended.has(`${e.kind}:${e.id}`)).map((e) => `${e.kind}:${e.id}`)
  const waiting = {}
  for (const l of logs) { const m = /^(\S+): nothing ready; waiting on (.+)$/.exec(l.text); if (m) waiting[m[1]] = m[2] }
  console.log(`overnight_tests: FAILED, the script did not finish within ${LIMIT_MS / 1000} s, on ${path.basename(scriptPath)}:`)
  console.log(`  - agents started and never ended: ${open.length ? open.join(', ') : 'none'}`)
  for (const [lane, ids] of Object.entries(waiting)) console.log(`  - ${lane} last logged waiting on ${ids}`)
  const begun = new Set(events.filter((e) => e.kind === 'implement').map((e) => e.id))
  const unbegun = fixture.expect.integrated.filter((id) => !begun.has(id)).map((id) => byId[id])
  console.log(`  - ${begun.size} implements had started; of those the fixture expects to integrate, these had not:`)
  for (const t of unbegun) console.log(`      ${t.id} (${t.owner}), blocked by ${t.blockedBy.length ? t.blockedBy.join(', ') : 'nothing'}`)
  process.exit(1)
}

const failures = []
const fail = (msg) => failures.push(msg)
const started = (id) => events.find((e) => e.kind === 'implement' && e.id === id && e.edge === 'start')
const integratedAt = (id) => (events.find((e) => e.kind === 'integrate' && e.id === id && e.edge === 'end') || {}).at

// 1. What integrates and what skips.
const want = new Set(fixture.expect.integrated)
const got = new Set(result.integrated || [])
for (const id of want) if (!got.has(id)) fail(`expected ${id} to integrate; it did not`)
for (const id of got) if (!want.has(id)) fail(`${id} integrated; the fixture does not expect it`)
for (const id of fixture.expect.skippedForBlocker) {
  if (!(result.skipped || []).includes(id)) fail(`expected ${id} skipped for an unintegrated blocker; it was not`)
  if (started(id)) fail(`${id} started an implement although its blocker never integrated`)
}

// 2. No implement starts before every blocker that is one of tonight's agent tickets has integrated.
for (const e of events.filter((x) => x.kind === 'implement' && x.edge === 'start')) {
  for (const b of byId[e.id].blockedBy) {
    if (!(result.integrated || []).includes(b)) continue
    if (integratedAt(b) === undefined || integratedAt(b) > e.at) fail(`${e.id} started at ${e.at}, before its blocker ${b} integrated`)
  }
}

// 3. Ready work goes first: a lane does not idle behind a blocked head.
for (const [first, then] of fixture.expect.startsBefore) {
  const a = started(first), b = started(then)
  if (!a) fail(`${first} never started`)
  else if (b && a.at > b.at) fail(`${first} started at ${a.at}, after ${then} at ${b.at}: its lane idled behind a blocked ticket`)
}

// 4. A lane says it is waiting only on tickets that have not integrated yet.
for (const l of logs) {
  const m = /^(\S+): nothing ready; waiting on (.+)$/.exec(l.text)
  if (!m) continue
  for (const id of m[2].split(', ')) {
    const at = integratedAt(id)
    if (at !== undefined && at < l.at) fail(`${m[1]} logged waiting on ${id} at ${l.at}, after ${id} integrated at ${at}`)
  }
}

if (failures.length) {
  console.log(`overnight_tests: FAILED, ${failures.length} check(s), on ${path.basename(scriptPath)}:`)
  failures.forEach((f) => console.log(`  - ${f}`))
  process.exit(1)
}
console.log(`overnight_tests: pass: ${got.size} integrated, ${fixture.expect.skippedForBlocker.length} skipped for a blocker, ${fixture.expect.startsBefore.length} ordering checks, on ${path.basename(scriptPath)}`)
