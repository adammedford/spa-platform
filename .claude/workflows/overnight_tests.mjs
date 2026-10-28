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
// owner, so the integrator lane is the slow one, as it is on a real night, except that two tickets meet a
// merge conflict at integrate (#136, #279): one that others wait on, which goes back to its lane, resolves,
// passes a second review and integrates on the retry, and one nobody waits on, whose retry conflicts again.
// Each mocked review reports the files its branch changed, and every integrated ticket but the second shares
// one, so the run must integrate those one after the other (#279). One more ticket nobody waits on stops at its
// implement on a question for Adam, and the run's return value must give every failed, skipped and needs-human
// ticket its stage and a one-line reason (#280). Eight more meet a red integration gate (#375): one on test
// projects its change cannot reach, which pass alone, so the merge stays and the flake is recorded; one whose change
// reaches a failed project, one whose re-run fails again, one red on something other than a test project, and one
// whose reach line does not parse, all reset; and three whose re-run agent answers ok with a project not passed,
// answers ok on another head, or dies, which keep no merge and reset the run branch (#397). It also checks that
// each ticket has its own worktree, that a lane implements while its last ticket is in review or integrating, that
// no two merges run at once, and that every ticket the run files passes tools/ticket_shape.py first (#248). No
// agent, gh or git call is made.
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const scriptPath = process.argv[2] || path.join(here, 'overnight.js')
const fixturePath = process.argv[3] || path.join(here, 'fixtures', '2026-10-20-tickets.json')
const fixture = JSON.parse(fs.readFileSync(fixturePath, 'utf8'))
const tickets = fixture.tickets
const byId = Object.fromEntries(tickets.map((t) => [t.id, t]))
// Two tickets meet a content conflict at integrate (#136), chosen from those the fixture expects to integrate:
// CONFLICT_ONCE conflicts on its first merge only, so it goes back to its lane and integrates after the
// back-merge and a second review; it is the first that another integrating ticket waits on, so its dependants
// must wait for it rather than be skipped (#279). CONFLICT_ALWAYS, which nobody waits on, conflicts on its retry
// too, so it is reported, not merged, and fails.
const blocking = new Set(tickets.flatMap((t) => t.blockedBy))
const free = fixture.expect.integrated.filter((id) => !blocking.has(id))
const dependantsOf = (id) => fixture.expect.integrated.filter((d) => byId[d].blockedBy.includes(id))
const CONFLICT_ONCE = fixture.expect.integrated.find((id) => dependantsOf(id).length)
const CONFLICT_ALWAYS = free[free.length - 1]
const CONFLICT_FILES = ['.github/workflows/ci.yml', 'docs/dependencies.md']
// One ticket nobody waits on, other than CONFLICT_ALWAYS, stops at its implement on a question for Adam (#280), so the
// run's reasons carry a needs-human ticket from a lane, whose reason is its question.
const NEEDS_HUMAN = free.find((id) => id !== CONFLICT_ALWAYS)
const QUESTION = 'Which store holds the rotated keys: the parameter store or the secrets manager?'
// The files each mocked review says its branch changed (#279): a file of its own, and SHARED_FILE for every ticket
// the fixture integrates except CONFLICT_ALWAYS, so those tickets share a file with no planned group in common,
// and CONFLICT_ALWAYS shares none, so other tickets may integrate while it is back in its lane.
// Five more tickets nobody waits on meet a red integration gate (#375), one per outcome the re-run rule has:
// FLAKE_KEPT failed only on projects its change cannot reach, which pass alone, so its merge stays; FLAKE_REACHED
// failed on a project its change reaches, so it resets with no re-run; FLAKE_AGAIN's unreachable project fails again
// alone, so it resets and says so; FLAKE_OTHER's gate failed on the smoke test, not a test project, so it resets.
// FLAKE_GARBLED's reach line does not parse, so it resets too: a rule that cannot read its input does not re-run.
// Three more are re-run, and the re-run agent's answer is not enough to keep the merge (#397): FLAKE_CLAIMED's agent
// answers ok though a project it re-ran did not pass, FLAKE_ELSEWHERE's answers ok on a head that is not the gate's,
// and FLAKE_DIED's agent dies. The script keeps no merge on any of them, and puts the run branch back to its old head.
const [FLAKE_KEPT, FLAKE_REACHED, FLAKE_AGAIN, FLAKE_OTHER, FLAKE_GARBLED, FLAKE_CLAIMED, FLAKE_ELSEWHERE, FLAKE_DIED] = free.filter((id) => id !== CONFLICT_ALWAYS && id !== NEEDS_HUMAN)
const FLAKE_UNKEPT = [FLAKE_CLAIMED, FLAKE_ELSEWHERE, FLAKE_DIED]
const FLAKE_FAILED = [FLAKE_REACHED, FLAKE_AGAIN, FLAKE_OTHER, FLAKE_GARBLED, ...FLAKE_UNKEPT]
// Four tickets the 2026-10-20 fixture lacks, added after every ticket above is chosen from it so none of those
// choices moves (#379). CLOSE has every acceptance box ticked, so collect gives it the status close-in-morning: no
// lane, listed with the issues to close, and met for AFTER_CLOSE, which waits on it. HELD is needs-triage with an open
// blocker labelled ready-for-human, so it routes to a human whatever its route agent says, and its routing names the
// blocker; TRIAGED is needs-triage with a ready-for-agent blocker, so it routes as before and integrates after it.
const synthetic = (n, id, status, blockedBy, openBoxes) => ({ n, id, slug: id, issue: Number(id.slice(6)), url: `https://example.invalid/${id}`,
  title: `Synthetic ${id}`, status, owner: 'integrator', blockedBy, why: `companion ${status}`, openBoxes, willEndPartial: [] })
const HELD_BY = 'issue-135', TRIAGED_AFTER = 'issue-144'
if (byId[HELD_BY].status !== 'ready-for-human' || byId[TRIAGED_AFTER].status !== 'ready-for-agent') throw new Error('the fixture no longer has issue-135 ready-for-human and issue-144 ready-for-agent')
const [CLOSE, AFTER_CLOSE, HELD, TRIAGED] = [
  synthetic(901, 'issue-901', 'close-in-morning', [], 0), synthetic(902, 'issue-902', 'ready-for-agent', ['issue-901'], 2),
  synthetic(903, 'issue-903', 'needs-triage', [HELD_BY], 3), synthetic(904, 'issue-904', 'needs-triage', [TRIAGED_AFTER], 3),
]
for (const t of [CLOSE, AFTER_CLOSE, HELD, TRIAGED]) { tickets.push(t); byId[t.id] = t }
const CLOSE_WHY = 'every acceptance box is ticked; close it in the morning'
const UNREACHED = ['tests/Spactl.Tests', 'tests/e2e']
const OLD = 'b'.repeat(40), RED = 'c'.repeat(40)
const TESTS_FAILED = 'check: fail: the tests failed.'
const reachLine = {
  [FLAKE_KEPT]: JSON.stringify({ failedAt: 'tests', verdict: TESTS_FAILED, projects: UNREACHED.map((project) => ({ project, reachedBy: [] })), changed: ['tools/bench/report.py'] }),
  [FLAKE_REACHED]: JSON.stringify({ failedAt: 'tests', verdict: TESTS_FAILED, projects: [{ project: 'tests/Spactl.Tests', reachedBy: [] }, { project: 'tests/Platform.Registry.Tests', reachedBy: ['src/Platform.Registry/RegistrySchemas.cs (project)'] }], changed: ['src/Platform.Registry/RegistrySchemas.cs'] }),
  [FLAKE_AGAIN]: JSON.stringify({ failedAt: 'tests', verdict: TESTS_FAILED, projects: [{ project: 'tests/Spactl.Tests', reachedBy: [] }], changed: ['tools/bench/report.py'] }),
  // The tool names no project for a failure that is not the tests; this one does, so the rule's own test of
  // failedAt is what holds it back.
  [FLAKE_OTHER]: JSON.stringify({ failedAt: 'other', verdict: 'check: fail: the smoke test failed.', projects: [{ project: 'tests/Spactl.Tests', reachedBy: [] }], changed: ['tools/bench/report.py'] }),
  [FLAKE_GARBLED]: 'test-reach: fail: cannot read the log',
  [FLAKE_CLAIMED]: JSON.stringify({ failedAt: 'tests', verdict: TESTS_FAILED, projects: UNREACHED.map((project) => ({ project, reachedBy: [] })), changed: ['tools/bench/report.py'] }),
  [FLAKE_ELSEWHERE]: JSON.stringify({ failedAt: 'tests', verdict: TESTS_FAILED, projects: [{ project: 'tests/Spactl.Tests', reachedBy: [] }], changed: ['tools/bench/report.py'] }),
  [FLAKE_DIED]: JSON.stringify({ failedAt: 'tests', verdict: TESTS_FAILED, projects: [{ project: 'tests/Spactl.Tests', reachedBy: [] }], changed: ['tools/bench/report.py'] }),
}
const ELSEWHERE = 'e'.repeat(40)   // the head FLAKE_ELSEWHERE's re-run agent answers ok on, not the gate's RED
const AGAIN_NOTE = 're-run failed: tests/Spactl.Tests failed again alone'
const SHARED_FILE = '.gitignore'
const changedFiles = (id) => (id === CONFLICT_ALWAYS || id === NEEDS_HUMAN || !fixture.expect.integrated.includes(id)) ? [`files/${id}.txt`] : [`files/${id}.txt`, SHARED_FILE]
const merges = {}          // id -> integrate calls so far
const greens = []          // {at, head}, each green mocked integrate, in order: the verified tips (#279)
const RESOLVE_TIP = 't'.repeat(40)   // the run branch's tip the mocked resolve back-merged
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
const allPrompts = []      // {label, prompt}, every agent's
let calls = 0              // each mocked agent call's ordinal, so an agent's start and end pair up
globalThis.agent = async (prompt, opts) => {
  allPrompts.push({ label: opts.label, prompt })
  const [kind, id] = opts.label.split(':')
  // Copies: the script sets a routed ticket's status, and the checks read the statuses collect gave.
  if (kind === 'collect') return { outcome: 'ok', reason: 'companion', tickets: tickets.map((t) => ({ ...t })), landed: [], base: 'a'.repeat(40), shell: '3.2', shellCommand: '/bin/bash' }
  if (kind === 'grill') return { questions: [], glossaryProposals: [], adrCandidates: [], summary: 'companion' }
  if (kind === 'route') return { flow: 'agent', reason: 'companion' }
  if (kind === 'report') return { questions: 0, mainCheckoutClean: true, leftovers: '', issue: 0, url: '' }
  const n = ++calls
  events.push({ at: tick, kind, id, edge: 'start', n })
  await sleep(kind === 'implement' ? cost[byId[id].owner] * 3 : 30)
  events.push({ at: tick, kind, id, edge: 'end', n })
  if (kind === 'implement' && id === NEEDS_HUMAN) return { outcome: 'needs-human', branch: 'b', forkSha: 'f', headSha: 'h', complete: false, outstanding: '', summary: 'stopped on a storage question', question: QUESTION, delegatedDecisions: [], proposedSteering: [] }
  if (kind === 'implement') return { outcome: 'resolved', branch: 'b', forkSha: 'f', headSha: 'h', complete: true, outstanding: '', summary: 's', question: '', delegatedDecisions: [], proposedSteering: [] }
  if (kind === 'review') return { outcome: 'committed', headSha: 'h', findingsFixed: [], findingsSkipped: [], summary: 's', question: '', proposedSteering: [], changedFiles: changedFiles(id) }
  if (kind === 'resolve') return { outcome: 'resolved', headSha: 'h2', tip: RESOLVE_TIP, notes: 'companion' }
  if (kind === 'integrate') {
    const k = merges[id] = (merges[id] || 0) + 1
    if (id === CONFLICT_ALWAYS || (id === CONFLICT_ONCE && k === 1)) return { ok: false, mergeSha: '', notes: 'conflict\n  CONFLICT (content): Merge conflict in .github/workflows/ci.yml', conflictFiles: CONFLICT_FILES, headSha: '', old: '', redHead: '', reach: '' }
    if (id in reachLine) return { ok: false, mergeSha: '', notes: 'check: fail: the tests failed.', conflictFiles: [], headSha: '', old: OLD, redHead: RED, reach: reachLine[id] }
    greens.push({ at: tick, head: `verified-${id}` })
    return { ok: true, mergeSha: 'm', notes: '', conflictFiles: [], headSha: `verified-${id}`, old: '', redHead: '', reach: '' }
  }
  if (kind === 'rerun') {
    const projects = JSON.parse(reachLine[id]).projects.map((p) => ({ project: p.project, log: `log-${p.project}`, passed: id === FLAKE_KEPT }))
    if (id === FLAKE_KEPT) { greens.push({ at: tick, head: RED }); return { ok: true, mergeSha: 'm-kept', headSha: RED, notes: 'passed alone; check green with --skip-tests', projects } }
    // An answer of ok the script must not keep (#397): tests/e2e did not pass, or every project passed on another head.
    if (id === FLAKE_CLAIMED) return { ok: true, mergeSha: 'm-claimed', headSha: RED, notes: 'passed alone; check green with --skip-tests', projects: projects.map((p) => ({ ...p, passed: p.project !== 'tests/e2e' })) }
    if (id === FLAKE_ELSEWHERE) return { ok: true, mergeSha: 'm-elsewhere', headSha: ELSEWHERE, notes: 'passed alone; check green with --skip-tests', projects: projects.map((p) => ({ ...p, passed: true })) }
    if (id === FLAKE_DIED) return null
    return { ok: false, mergeSha: '', headSha: '', notes: AGAIN_NOTE, projects }
  }
  if (kind === 'reset') return { ok: true, head: OLD, notes: `${id}: the run branch is at ${OLD}` }
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
const integratedAt = (id) => (events.filter((e) => e.kind === 'integrate' && e.id === id && e.edge === 'end').pop() || {}).at

// 1. What integrates and what skips.
const want = new Set([...fixture.expect.integrated.filter((id) => id !== CONFLICT_ALWAYS && id !== NEEDS_HUMAN && !FLAKE_FAILED.includes(id)), AFTER_CLOSE.id, TRIAGED.id])
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

// The run's log directory, as overnight.js derives it from the launch's repo: every file an agent writes outside
// its worktree goes there (#202).
const LOG = `${fixture.args.repo}/.scratch/overnight/log`

// 5. The edit rule (#109, Q164 of #286): every prompt that carries the house rules carries the amended
//    line verbatim, Edit and Write both, and no prompt still carries the Edit-only line it replaced. #202
//    moved its script from the session scratchpad to the run's log directory.
{
  const EDIT_RULE = `- To change an existing file with Edit or Write, open it with the Read tool first in this session, not with \`cat\`, \`head\` or \`sed -n\`: for a file you will change, this rule overrides the auto-mode advice to read with Bash. Open it with Read again after anything else wrote it (a script, the check, a merge, \`git\`). Otherwise make the change with a short script written to a file under ${LOG}/ and run by its path. Edit and Write refuse a file not read since its last write: 16 calls went to that refusal on 2026-10-18 and 16 over the four launches from 2026-10-19 to 2026-10-21 (#109).`
  const housed = allPrompts.filter((c) => /(House|Read-only) rules \(/.test(c.prompt))
  if (!housed.length) fail('no prompt carries the house rules, so the edit-rule search read nothing')
  for (const c of housed) if (!c.prompt.includes(EDIT_RULE)) fail(`the ${c.label} prompt's house rules lack the edit rule of #109`)
  for (const c of allPrompts) if (/Change a file with Edit only after/.test(c.prompt)) fail(`the ${c.label} prompt still carries the Edit-only line #109 replaced`)
}

// 6. One worktree per ticket (#136): every implement prompt names its own worktree, so no two tickets share one.
{
  const wtOf = {}
  for (const c of allPrompts.filter((x) => x.label.startsWith('implement:'))) {
    const m = /^Worktree: (\S+?)\.? Branch:/m.exec(c.prompt)
    if (!m) { fail(`the ${c.label} prompt names no worktree`); continue }
    wtOf[c.label.split(':')[1]] = m[1]
  }
  const owners = {}
  for (const [id, wt] of Object.entries(wtOf)) (owners[wt] = owners[wt] || []).push(id)
  for (const [wt, ids] of Object.entries(owners)) if (ids.length > 1) fail(`tickets ${ids.join(', ')} share the worktree ${wt}`)
  for (const c of allPrompts.filter((x) => x.label.startsWith('review:'))) {
    const id = c.label.split(':')[1]
    if (wtOf[id] && !c.prompt.includes(`Worktree ${wtOf[id]},`)) fail(`the review of ${id} does not run in its ticket's worktree ${wtOf[id]}`)
  }
}

// 7. A lane implements while its last ticket is in review or integrating (#136): in every lane with two or more
//    implements, the second starts before the first's integrate ends, unless the second waits on the first.
{
  const lanes = {}
  for (const e of events.filter((x) => x.kind === 'implement' && x.edge === 'start')) (lanes[byId[e.id].owner] = lanes[byId[e.id].owner] || []).push(e)
  let pairs = 0
  for (const [owner, starts] of Object.entries(lanes)) {
    if (starts.length < 2) continue
    const [a, b] = starts
    if (byId[b.id].blockedBy.includes(a.id)) continue
    const end = integratedAt(a.id)
    if (end === undefined) continue
    pairs++
    if (b.at >= end) fail(`${owner}: ${b.id} started its implement at ${b.at}, only after ${a.id} integrated at ${end}; the lane waited on review and integrate`)
  }
  if (!pairs) fail('no lane had two implements to compare, so the overlap check read nothing')
}

// 8. Integrate merges one ticket at a time (#136): no two integrate agents run at once. A resolve runs outside the
//    lock since #279, in the ticket's own lane, so it is not counted here; check 13 holds what it may overlap.
{
  const spans = []
  // A re-run holds the lock its red integrate held (#375), and so does the reset of a merge it did not keep (#397),
  // so both are counted with the integrates.
  for (const s of events.filter((x) => ['integrate', 'rerun', 'reset'].includes(x.kind) && x.edge === 'start')) {
    const e = events.find((x) => x.kind === s.kind && x.id === s.id && x.edge === 'end' && x.at >= s.at && x.n === s.n)
    spans.push({ label: `${s.kind}:${s.id}#${s.n}`, from: s.at, to: e ? e.at : Infinity })
  }
  spans.sort((p, q) => p.from - q.from)
  for (let i = 1; i < spans.length; i++) if (spans[i].from < spans[i - 1].to) fail(`${spans[i].label} started at ${spans[i].from} while ${spans[i - 1].label} ran until ${spans[i - 1].to}`)
}

// 9. A conflict at integrate (#136, #279): the ticket goes back to its lane, where its branch back-merges the run
//    branch in its own worktree, resolves and re-runs the check (the resolve agent), passes a second review against
//    the tip it merged in, and integrate retries once; a ticket still in conflict is reported with its files, not
//    merged.
{
  if (!CONFLICT_ONCE || !CONFLICT_ALWAYS || CONFLICT_ONCE === CONFLICT_ALWAYS) fail('the fixture lacks a waited-on ticket and a separate ticket nobody waits on, so the conflict checks read nothing')
  const resolves = (id) => allPrompts.filter((c) => c.label === `resolve:${id}`)
  const reviews = (id) => allPrompts.filter((c) => c.label === `review:${id}`)
  const spansOf = (kind, id) => events.filter((e) => e.kind === kind && e.id === id && e.edge === 'start')
    .map((s) => ({ from: s.at, to: (events.find((e) => e.edge === 'end' && e.n === s.n) || { at: Infinity }).at }))
  for (const id of [CONFLICT_ONCE, CONFLICT_ALWAYS]) {
    if (resolves(id).length !== 1) fail(`${id} met a conflict and ran ${resolves(id).length} resolve agents, not 1`)
    else {
      const r = resolves(id)[0].prompt
      // It back-merges the last verified tip, not the branch, which may hold a merge still under its gate (#279).
      const at = events.find((e) => e.kind === 'resolve' && e.id === id && e.edge === 'start').at
      const tip = (greens.filter((g) => g.at <= at).pop() || {}).head
      if (!tip) fail(`no integrate was green before the resolve of ${id}, so its verified tip is unknown`)
      else if (!r.includes(`merge --no-ff ${tip}\``)) fail(`the resolve of ${id} does not back-merge ${tip}, the last tip whose integration gate passed`)
      for (const f of CONFLICT_FILES) if (!r.includes(f)) fail(`the resolve of ${id} does not name the conflicting file ${f}`)
      if (!/THE CHECK/.test(r)) fail(`the resolve of ${id} does not re-run the check`)
    }
    if (merges[id] !== 2) fail(`${id} was merged ${merges[id] || 0} times; a conflict retries once, so 2`)
    // The second review (#279): one more review prompt, judged against the tip the resolve merged in, naming the
    // conflicting files, and run after the resolve and before the retry.
    if (reviews(id).length !== 2) fail(`${id} went back to its lane and passed ${reviews(id).length} reviews, not 2`)
    else {
      const again = reviews(id)[1].prompt
      if (!again.includes(`fixed point ${RESOLVE_TIP}.`)) fail(`the second review of ${id} is not judged against the tip its resolve merged in`)
      for (const f of CONFLICT_FILES) if (!again.includes(f)) fail(`the second review of ${id} does not name the conflicting file ${f}`)
      const [res] = spansOf('resolve', id), rv = spansOf('review', id), ig = spansOf('integrate', id)
      if (res && rv[1] && ig[1] && ig[0] && !(ig[0].to <= res.from && res.to <= rv[1].from && rv[1].to <= ig[1].from)) {
        fail(`${id}: the first merge, the resolve, the second review and the retry did not run in that order (${JSON.stringify({ ig, res, rv })})`)
      }
    }
  }
  for (const id of Object.keys(merges)) if (![CONFLICT_ONCE, CONFLICT_ALWAYS].includes(id) && resolves(id).length) fail(`${id} ran a resolve agent without a conflict`)
  for (const id of Object.keys(merges)) if (![CONFLICT_ONCE, CONFLICT_ALWAYS].includes(id) && reviews(id).length !== 1) fail(`${id} met no conflict and passed ${reviews(id).length} reviews, not 1`)
  // The tickets waiting on a sent-back ticket wait for it rather than being skipped (#279).
  const deps = dependantsOf(CONFLICT_ONCE)
  if (!deps.length) fail(`no integrating ticket waits on ${CONFLICT_ONCE}, so the waiting check read nothing`)
  for (const d of deps) {
    if ((result.skipped || []).includes(d)) fail(`${d} was skipped while its blocker ${CONFLICT_ONCE} was back in its lane`)
    if (!(result.integrated || []).includes(d)) fail(`${d} did not integrate after its blocker ${CONFLICT_ONCE} went back to its lane and integrated`)
    const s = started(d), end = integratedAt(CONFLICT_ONCE)
    if (s && end !== undefined && s.at < end) fail(`${d} started at ${s.at}, before its blocker ${CONFLICT_ONCE} integrated on its retry at ${end}`)
  }
  if (!(result.integrated || []).includes(CONFLICT_ONCE)) fail(`${CONFLICT_ONCE} did not integrate after its conflict was resolved`)
  if ((result.integrated || []).includes(CONFLICT_ALWAYS)) fail(`${CONFLICT_ALWAYS} integrated although its retry still conflicted`)
  if (!(result.failed || []).includes(CONFLICT_ALWAYS)) fail(`${CONFLICT_ALWAYS} is not reported failed after its retry still conflicted`)
  const c = Object.fromEntries((result.conflicts || []).map((x) => [x.id, x]))
  for (const [id, resolved] of [[CONFLICT_ONCE, true], [CONFLICT_ALWAYS, false]]) {
    if (!c[id]) { fail(`the run's result names no conflict for ${id}`); continue }
    if (c[id].resolved !== resolved) fail(`the run's result says ${id}'s conflict resolved=${c[id].resolved}, not ${resolved}`)
    if (CONFLICT_FILES.some((f) => !(c[id].files || []).includes(f))) fail(`the run's result does not name ${id}'s conflicting files`)
  }
  if ((result.conflicts || []).length !== 2) fail(`the run's result names ${(result.conflicts || []).length} conflicts, not 2`)
  const logged = logs.filter((l) => /^conflict: /.test(l.text)).map((l) => l.text)
  if (!logged.some((l) => l.includes(CONFLICT_ONCE) && l.endsWith('-> resolved'))) fail(`no "conflict: ... -> resolved" log line for ${CONFLICT_ONCE}`)
  if (!logged.some((l) => l.includes(CONFLICT_ALWAYS) && l.endsWith('-> unresolved'))) fail(`no "conflict: ... -> unresolved" log line for ${CONFLICT_ALWAYS}`)
}

// 10. Every file an agent writes goes to the run's log directory (#202): every prompt that carries the house rules
//     carries the log rule verbatim, which names the session scratchpad off limits, and no prompt still sends a file
//     to the scratchpad. On 2026-10-20 the collect agent's listings overwrote the pre-launch audit's snapshot there.
{
  const LOG_RULE = `- Every file you write outside your worktree goes under ${LOG}/ (${LOG}/research/ for research): a listing, a snapshot, a script, a body file, a draft. A file these instructions do not name by path begins with your task id (collect's with \`collect-\`, the report's with \`report-\`), so no two agents write the same file. The session scratchpad your system prompt names is off limits: every agent of the run is handed the launching session's, and on 2026-10-20 the collect agent's listings overwrote the pre-launch audit's snapshot of the same name there (#202).`
  const housed = allPrompts.filter((c) => /House rules \(/.test(c.prompt))
  if (!housed.length) fail('no prompt carries the house rules, so the log-rule search read nothing')
  for (const c of housed) if (!c.prompt.includes(LOG_RULE)) fail(`the ${c.label} prompt's house rules lack the log rule of #202`)
  for (const c of allPrompts) if (/under your scratchpad|in your scratchpad|to your scratchpad/.test(c.prompt)) fail(`the ${c.label} prompt still sends a file to the session scratchpad (#202)`)
  // The report prompt carries no house rules, yet it may write a body or a measurement: it carries its own log rule.
  const REPORT_RULE = `Any file you write (a body, a measurement, a script) goes under ${LOG}/ and begins with \`report-\`; the session scratchpad your system prompt names is off limits, as it is to every agent of the run (#202).`
  const reports = allPrompts.filter((c) => c.label === 'report')
  if (!reports.length) fail('no report prompt was rendered, so the report log-rule search read nothing')
  for (const c of reports) if (!c.prompt.includes(REPORT_RULE)) fail('the report prompt lacks its log rule of #202')
}

// 11. The review re-runs what a branch claims (#202): every review prompt tells the reviewer to re-run each mutation a
//     commit message or merge body claims, and to hold a "never prints X" promise unproven until a test plants a canary
//     of X. On 2026-10-20 five guards a merge body called broken on purpose could be removed with every companion green.
{
  const reviews = allPrompts.filter((c) => c.label.startsWith('review:'))
  if (!reviews.length) fail('no review prompt was rendered, so the claim checks read nothing')
  for (const c of reviews) {
    const wt = (/^Worktree (\S+?),/m.exec(c.prompt) || [])[1]
    const fork = (/fixed point (\S+?)\./.exec(c.prompt) || [])[1]
    if (!wt || !fork) { fail(`the ${c.label} prompt names no worktree or fixed point`); continue }
    const wants = [
      `Claims are re-run, not read (#202): read every commit message and merge body on the branch, \`git -C ${wt} log --format='%h%n%B' ${fork}..HEAD\`, and the implementer's "## Answer" comment, and re-run every mutation they claim`,
      `run only the test project or companion the claim names, see it fail, and restore the file with \`git -C ${wt} checkout -- <path>\` before the next. A claimed failure you cannot reproduce is a finding on the Spec axis`,
      'A promise that something "never prints X" (a token, a secret, a cookie, a path) is unproven until a test plants a canary value of X and asserts that the output lacks it',
      `\`git -C ${wt} status --porcelain\` must print nothing before STEP 3`,
    ]
    for (const w of wants) if (!c.prompt.includes(w)) fail(`the ${c.label} prompt lacks: ${w.slice(0, 80)}...`)
  }
}

// 12. The merge message goes through a file (#231): git merge reads no message from standard input (`-F -` fails
//     with "could not read file '-'"), and on 2026-10-21 the integrate agents of #155 and #194 each spent a retry on
//     that. Every integrate prompt, first merge and retry, names the message file under the run's log directory,
//     the ticket's own, and the merge command that reads it. The retry writes the same file the first merge left,
//     so the prompt asks for a heredoc, which overwrites it, where the Write tool refuses an unread file (#109).
{
  const integrates = allPrompts.filter((c) => c.label.startsWith('integrate:'))
  if (!integrates.length) fail('no integrate prompt was rendered, so the merge-message check read nothing')
  for (const c of integrates) {
    const id = c.label.split(':')[1]
    const file = `${LOG}/${id}-merge-message.txt`
    const wants = [
      `Write the message to ${file} first`,
      `merge --no-ff --no-edit -F ${file} `,
      'never on standard input',
      "with a quoted heredoc (`cat > <that file> <<'EOF'`)",
    ]
    for (const w of wants) if (!c.prompt.includes(w)) fail(`the ${c.label} prompt lacks: ${w}`)
    const branch = (/Task branch (\S+?)\. /.exec(c.prompt) || [])[1]
    if (!branch) fail(`the ${c.label} prompt names no task branch`)
    else if (c.prompt.includes(`merge --no-ff ${branch}`)) fail(`the ${c.label} prompt still merges ${branch} without the message file`)
  }
}

// 13. Tickets that changed the same file integrate one after the other (#279): the scheduler locks the files each
//     ticket's branch changed, as its review read them from its diff against its fork point, from its first merge
//     to its last, its time back in its lane included; so no ticket sharing a file with a sent-back ticket merges
//     while that one is out, though a ticket sharing none does: the run does not wait on a ticket back in its lane.
{
  const window = (id) => {
    const ig = events.filter((e) => e.kind === 'integrate' && e.id === id)
    if (!ig.length) return null
    return { from: Math.min(...ig.filter((e) => e.edge === 'start').map((e) => e.at)), to: Math.max(...ig.filter((e) => e.edge === 'end').map((e) => e.at)) }
  }
  const sharing = Object.keys(merges).filter((id) => changedFiles(id).includes(SHARED_FILE))
  let pairs = 0
  for (let i = 0; i < sharing.length; i++) for (let j = i + 1; j < sharing.length; j++) {
    const a = window(sharing[i]), b = window(sharing[j])
    if (!a || !b) continue
    pairs++
    if (a.from < b.to && b.from < a.to) fail(`${sharing[i]} (${a.from}..${a.to}) and ${sharing[j]} (${b.from}..${b.to}) both changed ${SHARED_FILE} and integrated at the same time`)
  }
  if (pairs < 2) fail(`fewer than two pairs of tickets shared ${SHARED_FILE}, so the file-lock check read nothing`)
  const waited = logs.filter((l) => new RegExp(`^\\S+: waiting to integrate behind .*${SHARED_FILE.replace('.', '\\.')}`).test(l.text))
  if (!waited.length) fail(`no ticket logged waiting to integrate behind another on ${SHARED_FILE}, so the file locks were never exercised`)
  const out = window(CONFLICT_ALWAYS), back = events.filter((e) => e.kind === 'integrate' && e.id === CONFLICT_ALWAYS)
  if (out && back.length === 4) {
    const from = back[1].at, to = back[2].at   // the first merge's end, the retry's start
    const during = events.filter((e) => e.kind === 'integrate' && e.id !== CONFLICT_ALWAYS && e.edge === 'start' && e.at >= from && e.at < to)
    if (!during.length) fail(`no ticket integrated while ${CONFLICT_ALWAYS}, which shares no file, was back in its lane (${from}..${to}): the run waited on it`)
  } else fail(`${CONFLICT_ALWAYS} did not run two integrates, so the send-back window check read nothing`)
  // git diff --name-only lists only a rename's new path, so a ticket that renamed a shared file would not lock the
  // old one: every review asks for its changed files with --no-renames, which lists both sides.
  const reviewPrompts = allPrompts.filter((c) => c.label.startsWith('review:'))
  if (!reviewPrompts.length) fail('no review prompt was captured, so the changed-files listing check read nothing')
  const blind = reviewPrompts.filter((c) => !c.prompt.includes('diff --no-renames --name-only '))
  if (blind.length) fail(`${blind.length} review prompt(s) ask for the changed files without --no-renames, so a renamed file locks only its new path`)
}

// 14. The run's return value says why (#280): every failed, skipped and needs-human ticket has one entry in its
//     "reasons", with the stage it stopped at and a one-line reason, so a launching session reads why without
//     rebuilding it from the run's journal. The failed ticket's reason names its conflicting files.
{
  const reasons = result.reasons || []
  const of = (id) => reasons.filter((r) => r.id === id)
  for (const [outcome, list] of [['failed', result.failed], ['skipped', result.skipped], ['needs-human', result.needsHuman]]) {
    for (const id of list || []) {
      const rs = of(id)
      if (rs.length !== 1) { fail(`${id} (${outcome}) has ${rs.length} entries in the run's reasons, not 1`); continue }
      const [r] = rs
      if (r.outcome !== outcome) fail(`${id}'s reason says outcome ${r.outcome}, not ${outcome}`)
      if (!r.stage) fail(`${id}'s reason names no stage`)
      if (typeof r.reason !== 'string' || !r.reason.trim() || /\n/.test(r.reason)) fail(`${id}'s reason is not one non-empty line: ${JSON.stringify(r.reason)}`)
    }
  }
  const failed = of(CONFLICT_ALWAYS)[0]
  if (!failed) fail(`the run's reasons hold no entry for ${CONFLICT_ALWAYS}, which failed`)
  else {
    if (failed.stage !== 'integrate') fail(`${CONFLICT_ALWAYS} failed at integrate; its reason says stage ${failed.stage}`)
    for (const f of CONFLICT_FILES) if (!failed.reason.includes(f)) fail(`${CONFLICT_ALWAYS}'s reason does not name its conflicting file ${f}: ${failed.reason}`)
  }
  // A ticket skipped because a need did not integrate names that need, and not a need that did integrate: the
  // skipped ticket the fixture blocks on two tickets, one integrated tonight and one that did not.
  const twoNeeds = fixture.expect.skippedForBlocker.map((id) => byId[id])
    .find((t) => t.blockedBy.some((b) => got.has(b)) && t.blockedBy.some((b) => !got.has(b)))
  if (!twoNeeds) fail('no ticket skipped for a blocker also waits on one that integrated, so the need-naming check read nothing')
  else {
    const r = of(twoNeeds.id)[0]
    const unmet = twoNeeds.blockedBy.filter((b) => !got.has(b)), met = twoNeeds.blockedBy.filter((b) => got.has(b))
    if (!r) fail(`the run's reasons hold no entry for ${twoNeeds.id}, which was skipped`)
    else {
      if (r.stage !== 'implement') fail(`${twoNeeds.id} was skipped before its implement; its reason says stage ${r.stage}`)
      for (const b of unmet) if (!r.reason.includes(b)) fail(`${twoNeeds.id}'s reason does not name ${b}, the need that did not integrate: ${r.reason}`)
      for (const b of met) if (r.reason.includes(b)) fail(`${twoNeeds.id}'s reason names ${b}, a need that integrated: ${r.reason}`)
    }
  }
  // A needs-human ticket from a lane gives its stage and its question; one grilled from collect says it went to a human.
  if (!NEEDS_HUMAN) fail('the fixture has no free ticket to stop on a question, so the needs-human check read nothing')
  else {
    if (!(result.needsHuman || []).includes(NEEDS_HUMAN)) fail(`${NEEDS_HUMAN} stopped on a question and is not reported needs-human`)
    const r = of(NEEDS_HUMAN)[0]
    if (!r) fail(`the run's reasons hold no entry for ${NEEDS_HUMAN}, which needs a human`)
    else {
      if (r.stage !== 'implement') fail(`${NEEDS_HUMAN} stopped at its implement; its reason says stage ${r.stage}`)
      if (!r.reason.includes(QUESTION)) fail(`${NEEDS_HUMAN}'s reason is not its question: ${r.reason}`)
    }
  }
  const grilled = tickets.filter((t) => t.status === 'ready-for-human' && (result.needsHuman || []).includes(t.id))
  if (!grilled.length) fail('no ready-for-human ticket was reported needs-human, so the grilled-ticket check read nothing')
  for (const t of grilled) {
    const r = of(t.id)[0]
    if (r && (r.stage !== 'collect' || !r.reason.includes(t.why))) fail(`${t.id}, ready-for-human from collect, has reason ${JSON.stringify(r)}, not stage collect with its collect why`)
  }
}

// 15. A Confirm names the questions it covers, and each question carries both its numbers (#301). The morning reads
//     only a Confirm comment's first sentence for the questions it covers (ADR-0021 decision 2), so a bare "Confirm"
//     covers none: on 2026-10-22 a draft opening with one would have confirmed 21 of 263 answers, the ones its
//     override lines named. The report's first line says so, with a range example. And each questions comment numbers
//     a question night-wide, then by the number its ticket's own comment gave it, so an answer citing "#244 Q9" turns
//     into the number a Confirm names without counting down a comment.
{
  const FIRST_LINE = 'Adam confirms pre-accepted answers with one comment on this issue beginning "Confirm", whose first sentence names the questions it covers, as in "Confirm Q1 to Q<last>, with the overrides below." where Q<last> is the last question\'s night-wide number; a bare "Confirm" covers no question, because the morning reads only that first sentence for them (ADR-0021 decision 2)'
  const BOTH_NUMBERS = 'Each question opens with its night-wide number, then, in parentheses, the number it carries in its ticket\'s own comment, taken unchanged from that comment, as in "**Q94.** (#244 Q9)"; a question its source does not number (a status note\'s question, a proposed steering change) opens with the night-wide number alone, as in "**Q95.**"'
  const reports = allPrompts.filter((c) => c.label === 'report')
  if (!reports.length) fail('no report prompt was rendered, so the Confirm and numbering checks read nothing')
  for (const c of reports) {
    if (!c.prompt.includes(FIRST_LINE)) fail('the report prompt\'s first line does not say that a Confirm\'s first sentence names the questions it covers and that a bare "Confirm" covers none (#301)')
    if (!c.prompt.includes(BOTH_NUMBERS)) fail('the report prompt does not number each question night-wide and then by its ticket-local number (#301)')
  }
}

// 16. The status the collect tool gives is the ticket's (#316): only tools/overnight_collect.py reads who applied
//     ready-for-agent, so the collect prompt copies its status and why, and a tool that did not run starts no lane.
{
  const COPIED = 'The status the tool gives is the ticket\'s (#316): give each ticket in "tickets" the "status" and "why" of the tool\'s ticket with the same issue number, copied as printed, in place of yours'
  const UNREAD = 'give every ticket whose status would be "ready-for-agent" the status "blocked"'
  const collects = allPrompts.filter((c) => c.label === 'collect')
  if (!collects.length) fail('no collect prompt was rendered, so the status check reads nothing')
  for (const c of collects) {
    if (!c.prompt.includes(COPIED)) fail('the collect prompt does not take each ticket\'s status from tools/overnight_collect.py (#316)')
    if (!c.prompt.includes(UNREAD)) fail('the collect prompt lets a ticket stay ready-for-agent when tools/overnight_collect.py did not run (#316)')
  }
}

// 17. A red integration gate on test projects the change cannot reach is re-run once, alone (#375). On 2026-10-24
//     #305 changed Python under tools/bench/ alone and was reset because 30 Spactl.Tests tests failed (#373).
{
  if (!FLAKE_DIED) fail('the fixture has too few free tickets for the eight re-run cases, so the flake checks read nothing')
  const reruns = (id) => allPrompts.filter((c) => c.label === `rerun:${id}`)
  const flakes = result.flakes || []
  const flakeOf = (id) => flakes.filter((f) => f.id === id)
  const reasonOf = (id) => ((result.reasons || []).find((r) => r.id === id) || {}).reason || ''
  // Outcome 1: every failed project lies outside the closure. One re-run, of exactly those projects, each once and
  // alone, then the rest of the check with --skip-tests; it passed, so the merge stays and the flake is recorded.
  {
    const id = FLAKE_KEPT, rs = reruns(id)
    if (rs.length !== 1) fail(`${id}'s gate failed only on unreachable projects and ran ${rs.length} re-runs, not 1`)
    else {
      const p = rs[0].prompt
      for (const project of UNREACHED) {
        const runs = p.split(`dotnet test --project ${project} `).length - 1
        if (runs !== 1) fail(`the re-run of ${id} runs ${project} ${runs} times, not once`)
      }
      if (/dotnet test --solution/.test(p)) fail(`the re-run of ${id} runs the whole suite, not the failed projects alone`)
      if (/dotnet test --project tests\/(?!Spactl\.Tests |e2e |<X>\.Tests`)/.test(p)) fail(`the re-run of ${id} runs a project that did not fail`)
      if (!p.includes(`tools/check.sh --full --base a${'a'.repeat(39)} --port `) || !p.includes(' --skip-tests; }')) fail(`the re-run of ${id} does not run the rest of the check with --skip-tests, against the run's base`)
      if (!p.includes(`reset --hard ${RED}`) || !p.includes(`reset --hard ${OLD}`)) fail(`the re-run of ${id} does not move the run branch to the gate's head and back to the old head on red`)
      if (!p.includes('"## Flake at integrate"')) fail(`the re-run of ${id} records no "## Flake at integrate" comment on its ticket`)
    }
    if (!(result.integrated || []).includes(id)) fail(`${id}'s projects passed alone and the check passed, yet it did not integrate`)
    const [f] = flakeOf(id)
    if (flakeOf(id).length !== 1) fail(`the run's result holds ${flakeOf(id).length} flakes for ${id}, not 1`)
    else {
      if (f.kept !== true) fail(`${id}'s flake does not say the merge was kept`)
      if (JSON.stringify(f.projects.map((x) => x.project)) !== JSON.stringify(UNREACHED)) fail(`${id}'s flake names ${JSON.stringify(f.projects)}, not each failed project`)
      for (const x of f.projects) if (!x.log || !x.log.includes(x.project.replace('/', '_')) || x.passed !== true) fail(`${id}'s flake does not name ${x.project}'s re-run log and its pass: ${JSON.stringify(x)}`)
    }
    const report = allPrompts.find((x) => x.label === 'report')
    const summary = report ? JSON.parse((/^Run results \(JSON\): (.*)$/m.exec(report.prompt) || [, 'null'])[1]) : null
    const kept = summary && summary.results[id]
    if (!kept || !kept.flake || kept.flake.kept !== true) fail(`${id}'s integrate record does not name its flake`)
  }
  // Outcome 2: a failed project inside the closure resets at once, with no re-run.
  // Outcome 4: a gate red on something other than a test project resets as before, and so does a reach line the
  // rule cannot read.
  for (const id of [FLAKE_REACHED, FLAKE_OTHER, FLAKE_GARBLED]) {
    if (reruns(id).length) fail(`${id} re-ran its tests although the rule forbids it`)
    if (flakeOf(id).length) fail(`${id} recorded a flake although nothing was re-run`)
    if (!(result.failed || []).includes(id)) fail(`${id}'s gate was red with no re-run, yet it is not reported failed`)
    if (merges[id] !== 1) fail(`${id} was merged ${merges[id] || 0} times; a red gate with no re-run merges once`)
  }
  if (!logs.some((l) => l.text.startsWith(`${FLAKE_REACHED}: integration gate red; no re-run: the change reaches tests/Platform.Registry.Tests`))) fail(`no log line says ${FLAKE_REACHED} was not re-run because its change reaches the failed project`)
  if (!logs.some((l) => l.text.startsWith(`${FLAKE_OTHER}: integration gate red; no re-run: the gate failed on something other than the tests`))) fail(`no log line says ${FLAKE_OTHER} was not re-run because its gate failed on something other than the tests`)
  // Outcome 3: the re-run fails again: reset, and the record says the re-run failed.
  {
    const id = FLAKE_AGAIN
    if (reruns(id).length !== 1) fail(`${id} ran ${reruns(id).length} re-runs, not 1`)
    if (!(result.failed || []).includes(id)) fail(`${id}'s re-run failed, yet it is not reported failed`)
    if (!reasonOf(id).startsWith('re-run failed: ') || !reasonOf(id).includes('tests/Spactl.Tests failed again')) fail(`${id}'s reason does not say the re-run failed: ${reasonOf(id)}`)
    const [f] = flakeOf(id)
    if (!f || f.kept !== false || f.projects.some((x) => x.passed)) fail(`${id}'s flake record does not say the re-run failed: ${JSON.stringify(f)}`)
  }
  // Outcome 6 (#397): the merge is kept on the script's own reading of the re-run, every failed project recorded as
  // passed alone on the gate's head, never on the agent's ok alone. An ok with a project not passed, an ok on another
  // head, and a dead agent keep nothing: the ticket fails, its reason says why, and a reset agent puts the run branch
  // back to the old head inside the same lock, since the re-run agent that answered ok left its merge in.
  {
    const resets = (id) => allPrompts.filter((c) => c.label === `reset:${id}`)
    const why = {
      [FLAKE_CLAIMED]: ['tests/e2e did not pass alone'],
      [FLAKE_ELSEWHERE]: [ELSEWHERE, RED],
      [FLAKE_DIED]: ['the re-run agent died'],
    }
    for (const id of FLAKE_UNKEPT) {
      if (reruns(id).length !== 1) fail(`${id} ran ${reruns(id).length} re-runs, not 1`)
      if ((result.integrated || []).includes(id)) fail(`${id}'s re-run agent's answer does not show every project passed on the gate's head, yet its merge was kept`)
      if (!(result.failed || []).includes(id)) fail(`${id}'s merge was not kept, yet it is not reported failed`)
      const r = reasonOf(id)
      if (!r.startsWith('re-run failed: ') || why[id].some((w) => !r.includes(w))) fail(`${id}'s reason does not say why the script kept no merge (${why[id].join(', ')}): ${r}`)
      const [f] = flakeOf(id)
      if (flakeOf(id).length !== 1 || f.kept !== false) fail(`${id}'s flake record does not say the merge was not kept: ${JSON.stringify(f)}`)
      if (!logs.some((l) => l.text.startsWith(`flake: ${id} `) && l.text.includes('-> not kept: '))) fail(`no "flake: ${id} ... -> not kept: ..." log line`)
      const rs = resets(id)
      if (rs.length !== 1) fail(`${id}'s merge was not kept and ran ${rs.length} reset agents, not 1`)
      else {
        const p = rs[0].prompt
        if (!p.includes(`reset --hard ${OLD}`) || !p.includes(`merge-base --is-ancestor ${OLD} HEAD`)) fail(`the reset of ${id} does not put the run branch back to ${OLD} only when ${OLD} is an ancestor of its head`)
        if (!p.includes('"## Flake at integrate: merge not kept"')) fail(`the reset of ${id} records no "## Flake at integrate: merge not kept" comment on its ticket`)
      }
    }
    const [c] = flakeOf(FLAKE_CLAIMED)
    if (c && JSON.stringify(c.projects.map((x) => [x.project, x.passed])) !== JSON.stringify([['tests/Spactl.Tests', true], ['tests/e2e', false]])) fail(`${FLAKE_CLAIMED}'s flake does not record each project's pass as its re-run gave it: ${JSON.stringify(c.projects)}`)
    // The agent that answered red reset the branch itself, as its prompt says; a kept merge needs no reset.
    for (const id of [FLAKE_KEPT, FLAKE_AGAIN]) if (resets(id).length) fail(`${id} ran a reset agent, though its re-run agent's answer and the script's verdict agree`)
  }
  if (flakes.length !== 5) fail(`the run's result holds ${flakes.length} re-runs, not 5`)
  if (!logs.some((l) => l.text === `flake: ${FLAKE_KEPT} ${UNREACHED.join(', ')} -> passed alone, merge kept`)) fail(`no "flake: ... -> passed alone, merge kept" log line for ${FLAKE_KEPT}`)
  // Outcome 5: the report lists each flake under a heading of its own, the project's path, the same every night.
  const HEADING = 'Give each project its own heading, `### Flake: <project>`'
  for (const c of allPrompts.filter((x) => x.label === 'report')) {
    if (!c.prompt.includes(HEADING)) fail('the report prompt does not give each flake a heading of its own (#375)')
    if (!c.prompt.includes('"flakes":[')) fail('the report prompt\'s results carry no flakes')
  }
  // The red path records the facts the rule reads and decides nothing itself.
  for (const c of allPrompts.filter((x) => x.label.startsWith('integrate:'))) {
    if (!c.prompt.includes('python3 -I tools/test_reach.py --log ')) fail(`the ${c.label} prompt does not run tools/test_reach.py on a red gate`)
    if (!c.prompt.includes("whether the failed projects are re-run is the script's decision")) fail(`the ${c.label} prompt lets the agent decide the re-run`)
  }
}

// 18. A ticket with nothing to do gets no lane (#379). On 2026-10-24 lanes were forecast empty for 1.7 (#58), every
//     box ticked, and #329, whose one open blocker #322 was labelled ready-for-human; each returned skipped.
{
  const reasonOf = (id) => (result.reasons || []).find((r) => r.id === id)
  const report = allPrompts.find((x) => x.label === 'report')
  const summary = report ? JSON.parse((/^Run results \(JSON\): (.*)$/m.exec(report.prompt) || [, 'null'])[1]) : null
  // close-in-morning, collect's status for it, and a ready-for-agent ticket with openBoxes 0 (the fixture's 1.4),
  // should collect's status not say so: no lane, listed with the issues to close, met for its dependants.
  const zero = tickets.filter((t) => t.status === 'ready-for-agent' && t.openBoxes === 0 && !fixture.args.skip.includes(t.id))
  if (!zero.length) fail('the fixture has no ready-for-agent ticket with every box ticked, so the backstop check read nothing')
  for (const t of [CLOSE, ...zero]) {
    if (started(t.id)) fail(`${t.id} has every box ticked, yet a lane started its implement`)
    if (!(result.closeInMorning || []).includes(t.id)) fail(`${t.id} is not in the run's closeInMorning`)
    const r = reasonOf(t.id)
    if (!r || r.stage !== 'collect' || r.reason !== CLOSE_WHY) fail(`${t.id}'s reason is ${JSON.stringify(r)}, not stage collect with "${CLOSE_WHY}"`)
    if (!summary || !(summary.closeInMorning || []).some((c) => c.id === t.id && c.issue === t.issue)) fail(`the report's results do not list ${t.id} (#${t.issue}) to close in the morning`)
  }
  if (!report || !report.prompt.includes('every entry of the run results\' "closeInMorning"') || !report.prompt.includes('list them with the issues to close')) fail('the report prompt does not list the closeInMorning tickets with the issues to close')
  if (!started(AFTER_CLOSE.id)) fail(`${AFTER_CLOSE.id} waits only on ${CLOSE.id}, which has nothing left to build, yet it never started`)
  const COLLECT_RULE = 'label ready-for-agent with openBoxes 0 (every acceptance box ticked), whatever it waits on -> "close-in-morning"'
  for (const c of allPrompts.filter((x) => x.label === 'collect')) if (!c.prompt.includes(COLLECT_RULE)) fail('the collect prompt does not give a ticket with every box ticked the status close-in-morning')
  // Route: a needs-triage ticket with an open ready-for-human blocker goes to a human, naming the blocker, though the
  // mocked route agent answers "agent"; one whose blockers are ready-for-agent tickets routes as before.
  const routeOf = (t) => (allPrompts.find((x) => x.label === `route:${t.id}`) || {}).prompt || ''
  const blocker = byId[HELD_BY]
  const heldPrompt = routeOf(HELD)
  if (!heldPrompt) fail(`${HELD.id} is needs-triage and was not routed`)
  else {
    if (!heldPrompt.includes(`#${blocker.issue} (${blocker.id})`) || !heldPrompt.includes('"## Routing"')) fail(`${HELD.id}'s routing comment does not name its ready-for-human blocker #${blocker.issue}`)
    if (!heldPrompt.includes(`gh issue edit ${HELD.issue} --remove-label needs-triage --add-label ready-for-human`) || heldPrompt.includes('--add-label ready-for-agent')) fail(`${HELD.id}'s route prompt does not swap its label to ready-for-human alone`)
  }
  if (started(HELD.id)) fail(`${HELD.id} waits on the ready-for-human ${HELD_BY}, yet a lane started its implement`)
  if (!(result.needsHuman || []).includes(HELD.id)) fail(`${HELD.id} is not reported needs-human`)
  const hr = reasonOf(HELD.id)
  if (!hr || hr.stage !== 'route' || !hr.reason.includes(`#${blocker.issue}`)) fail(`${HELD.id}'s reason is ${JSON.stringify(hr)}, not stage route naming #${blocker.issue}`)
  const triaged = routeOf(TRIAGED)
  if (!triaged.includes('Decide the flow') || triaged.includes('Its flow is already decided')) fail(`${TRIAGED.id}, whose blocker is ready-for-agent, did not route as before`)
  if ((result.needsHuman || []).includes(TRIAGED.id)) fail(`${TRIAGED.id} went to a human although its blocker is ready-for-agent`)
}

// 19. One grammar where a ticket is filed (#248): the ticket-shape check at creation, the collect tool's forecast,
//     and the run's own filing site. NEW TICKETS, the one place the run files a ticket, pipes each body through
//     tools/ticket_shape.py under the label and blockers it is filed with, before its `gh issue create`, and files
//     nothing when the check fails. Every other `gh issue create` a prompt carries is the report's own issue,
//     "Overnight <date>", which is no ticket; the grill writes comments and files none. Retro's filing site is
//     retro.js's, proved by its own companion.
//     The recipe is taken from the prompt and run for real, as retro_tests.mjs check 19 runs retro's, so a recipe
//     that cannot judge, or never passes the label it is given, fails here and not on the night. Neither body
//     cites an issue number, so ticket_shape.py makes no gh call.
{
  const RECIPE = /`cd (\S+) && (python3 -I -c '[^']*' (\S+\/new-ticket-<id>\.md) <label> <each --blocked-by number> \| python3 -I tools\/ticket_shape\.py -)`/
  const TICKET_CREATE = 'gh issue create --title "<id> — <short title>"'
  const REPORT_CREATE = 'gh issue create --title "Overnight '
  const top = path.resolve(here, '..', '..')
  const bodyDir = fs.mkdtempSync(path.join(os.tmpdir(), 'overnight-tests-shape-'))
  const body = (extra) => `**Owner:** integrator\n**Plan row:** I-9\n\n**What to build:** the row's What, citing \`contracts/pipeline.md\`, and its Done when.\n## Acceptance\n- [ ] the companion passes\n${extra}`
  fs.writeFileSync(path.join(bodyDir, 'good.md'), body(''))
  fs.writeFileSync(path.join(bodyDir, 'bad.md'), body('**Session:** foreground\n'))
  const shapeOf = (cmd, file, blockers) => {
    const sh = cmd.replace(/\S+\/new-ticket-<id>\.md/, file).replace('<label>', 'ready-for-agent').replace(' <each --blocked-by number>', blockers.map((n) => ` ${n}`).join(''))
    return spawnSync('sh', ['-c', sh], { cwd: top, encoding: 'utf8' })
  }
  const collects = allPrompts.filter((x) => x.label === 'collect')
  if (!collects.length) fail('no collect prompt was rendered, so the NEW TICKETS check read nothing')
  const creates = new Map()   // the collect prompt -> the index of its one checked gh issue create
  for (const c of collects) {
    const m = RECIPE.exec(c.prompt)
    const create = c.prompt.indexOf(TICKET_CREATE)
    if (!m) { fail('#248: NEW TICKETS does not pipe each body through tools/ticket_shape.py under its label and blockers'); continue }
    if (create < 0) { fail('#248: NEW TICKETS files no ticket with gh issue create'); continue }
    creates.set(c, create)
    if (m[1] !== fixture.args.repo) fail(`#248: NEW TICKETS runs the ticket-shape check in ${m[1]}, not the main checkout ${fixture.args.repo}`)
    if (!m[3].startsWith(`${LOG}/`)) fail(`#248: NEW TICKETS checks ${m[3]}, not a body file under ${LOG}/`)
    if (m.index > create) fail('#248: NEW TICKETS runs the ticket-shape check after its gh issue create, not before')
    const filing = c.prompt.slice(create, c.prompt.indexOf('\n', create))
    if (!filing.includes(`--body-file ${m[3]}`)) fail(`#248: NEW TICKETS files a body other than the one it checked (${m[3]}): ${filing}`)
    const step = c.prompt.slice(m.index, c.prompt.indexOf('\n', create))
    for (const rule of ['Only when that exits 0', 'On exit 1 file nothing for that row', 'on any other exit file nothing either', 'Never reword a body to get past the check'])
      if (!step.includes(rule)) fail(`#248: NEW TICKETS lacks "${rule}", so a body the check fails may still be filed`)
    const good = shapeOf(m[2], path.join(bodyDir, 'good.md'), [900])
    if (good.status !== 0 || !/^ticket-shape: pass:/m.test(good.stdout)) fail(`#248: the NEW TICKETS check did not pass a well-shaped body: exit ${good.status}, ${(good.stdout + good.stderr).trim()}`)
    const bad = shapeOf(m[2], path.join(bodyDir, 'bad.md'), [])
    if (bad.status !== 1 || !/^ticket-shape: foreground:/m.test(bad.stdout)) fail(`#248: the NEW TICKETS check did not fail a ready-for-agent foreground body on its rule: exit ${bad.status}, ${(bad.stdout + bad.stderr).trim()}`)
  }
  fs.rmSync(bodyDir, { recursive: true, force: true })
  // Every gh issue create in every prompt the run sends is the one checked NEW TICKETS create or the report's issue.
  for (const c of allPrompts) {
    for (const x of c.prompt.matchAll(/gh issue create\b/g)) {
      const rest = c.prompt.slice(x.index)
      const ok = (c.label === 'collect' && x.index === creates.get(c)) || (c.label === 'report' && rest.startsWith(REPORT_CREATE))
      if (!ok) fail(`#248: the ${c.label} prompt carries a gh issue create the ticket-shape check does not guard: ${rest.slice(0, 80)}`)
    }
  }
  const grills = allPrompts.filter((x) => x.label.startsWith('grill:'))
  if (!grills.length) fail('no grill prompt was rendered, so the check that the grill files no issue read nothing')
}

// 20. A complete lane's ticket is not its row (#388, #457). On 2026-10-24 the integrate step wrote "Merge task P2-2"
//     from the lane's completeness alone while #338 and #342, parts of P2-2, were open. Every integrate prompt of a
//     complete lane asks tools/landing_subjects.py for the subject's head, in the integration worktree, before the
//     merge, and writes the subject from its answer, never from completeness. The rule itself is proved in
//     tools/landing_subjects_tests.py; here only that the prompt asks it, and asks it first.
{
  const INT_WT = `${fixture.args.wt}/overnight`
  const integrates = allPrompts.filter((x) => x.label.startsWith('integrate:'))
  if (!integrates.length) fail('#388: no integrate prompt was rendered, so the subject check read nothing')
  for (const c of integrates) {
    const id = c.label.split(':')[1]
    const ask = `python3 -I tools/landing_subjects.py --subject-for ${id} --ticket ${byId[id].issue}`
    const at = c.prompt.indexOf(ask)
    const merge = c.prompt.indexOf(' merge --no-ff --no-edit -F ')
    if (at < 0) { fail(`#388: the ${c.label} prompt does not ask "${ask}" for the subject`); continue }
    if (!c.prompt.slice(0, at).endsWith(`cd ${INT_WT} && `)) fail(`#388: the ${c.label} prompt asks the subject outside the integration worktree ${INT_WT}`)
    if (merge < 0 || merge < at) fail(`#388: the ${c.label} prompt merges before it asks the subject`)
    if (!c.prompt.includes('- subject "<head>: <what landed>"')) fail(`#388: the ${c.label} prompt does not write the subject from the answer's head`)
    if (c.prompt.includes(`- subject "Merge task ${id}: `)) fail(`#388: the ${c.label} prompt still writes "Merge task ${id}" from the lane's completeness`)
    if (!c.prompt.includes('naming each open part ticket')) fail(`#388: the ${c.label} prompt does not name the open part tickets on a part merge's Outstanding line`)
  }
}

if (failures.length) {
  console.log(`overnight_tests: FAILED, ${failures.length} check(s), on ${path.basename(scriptPath)}:`)
  failures.forEach((f) => console.log(`  - ${f}`))
  process.exit(1)
}
console.log(`overnight_tests: pass: ${got.size} integrated, ${fixture.expect.skippedForBlocker.length} skipped for a blocker, ${fixture.expect.startsBefore.length} ordering checks, on ${path.basename(scriptPath)}`)
