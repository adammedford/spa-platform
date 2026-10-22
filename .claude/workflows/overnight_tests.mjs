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
// owner, so the integrator lane is the slow one, as it is on a real night, except that two tickets nobody
// waits on meet a merge conflict at integrate (#136): one resolves on the retry, one does not. It also
// checks that each ticket has its own worktree, that a lane implements while its last ticket is in review
// or integrating, and that no two merges run at once. No agent, gh or git call is made.
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const scriptPath = process.argv[2] || path.join(here, 'overnight.js')
const fixturePath = process.argv[3] || path.join(here, 'fixtures', '2026-10-20-tickets.json')
const fixture = JSON.parse(fs.readFileSync(fixturePath, 'utf8'))
const tickets = fixture.tickets
const byId = Object.fromEntries(tickets.map((t) => [t.id, t]))
// Two tickets meet a content conflict at integrate (#136), chosen from those the fixture expects to integrate and
// no ticket waits on: CONFLICT_ONCE conflicts on its first merge only, so it integrates after the back-merge;
// CONFLICT_ALWAYS conflicts on its retry too, so it is reported, not merged, and fails.
const blocking = new Set(tickets.flatMap((t) => t.blockedBy))
const free = fixture.expect.integrated.filter((id) => !blocking.has(id))
const CONFLICT_ONCE = free[0]
const CONFLICT_ALWAYS = free[free.length - 1]
const CONFLICT_FILES = ['.github/workflows/ci.yml', 'docs/dependencies.md']
const merges = {}          // id -> integrate calls so far
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
  if (kind === 'collect') return { outcome: 'ok', reason: 'companion', tickets, landed: [], base: 'a'.repeat(40), shell: '3.2', shellCommand: '/bin/bash' }
  if (kind === 'grill') return { questions: [], glossaryProposals: [], adrCandidates: [], summary: 'companion' }
  if (kind === 'route') return { flow: 'agent', reason: 'companion' }
  if (kind === 'report') return { questions: 0, mainCheckoutClean: true, leftovers: '', issue: 0, url: '' }
  const n = ++calls
  events.push({ at: tick, kind, id, edge: 'start', n })
  await sleep(kind === 'implement' ? cost[byId[id].owner] * 3 : 30)
  events.push({ at: tick, kind, id, edge: 'end', n })
  if (kind === 'implement') return { outcome: 'resolved', branch: 'b', forkSha: 'f', headSha: 'h', complete: true, outstanding: '', summary: 's', question: '', delegatedDecisions: [], proposedSteering: [] }
  if (kind === 'review') return { outcome: 'committed', headSha: 'h', findingsFixed: [], findingsSkipped: [], summary: 's', question: '', proposedSteering: [] }
  if (kind === 'resolve') return { outcome: 'resolved', headSha: 'h2', notes: 'companion' }
  if (kind === 'integrate') {
    const k = merges[id] = (merges[id] || 0) + 1
    if (id === CONFLICT_ALWAYS || (id === CONFLICT_ONCE && k === 1)) return { ok: false, mergeSha: '', notes: 'conflict', conflictFiles: CONFLICT_FILES }
    return { ok: true, mergeSha: 'm', notes: '', conflictFiles: [] }
  }
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
const want = new Set(fixture.expect.integrated.filter((id) => id !== CONFLICT_ALWAYS))
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

// 8. Integrate merges one ticket at a time (#136): no two integrate or resolve agents run at once.
{
  const spans = []
  for (const s of events.filter((x) => (x.kind === 'integrate' || x.kind === 'resolve') && x.edge === 'start')) {
    const e = events.find((x) => x.kind === s.kind && x.id === s.id && x.edge === 'end' && x.at >= s.at && x.n === s.n)
    spans.push({ label: `${s.kind}:${s.id}#${s.n}`, from: s.at, to: e ? e.at : Infinity })
  }
  spans.sort((p, q) => p.from - q.from)
  for (let i = 1; i < spans.length; i++) if (spans[i].from < spans[i - 1].to) fail(`${spans[i].label} started at ${spans[i].from} while ${spans[i - 1].label} ran until ${spans[i - 1].to}`)
}

// 9. A conflict at integrate (#136): the ticket's branch back-merges the run branch in its own worktree, resolves
//    and re-runs the check (the resolve agent), and integrate retries once; a ticket still in conflict is reported
//    with its files, not merged.
{
  if (!CONFLICT_ONCE || CONFLICT_ONCE === CONFLICT_ALWAYS) fail('the fixture has fewer than two tickets nobody waits on, so the conflict checks read nothing')
  const resolves = (id) => allPrompts.filter((c) => c.label === `resolve:${id}`)
  for (const id of [CONFLICT_ONCE, CONFLICT_ALWAYS]) {
    if (resolves(id).length !== 1) fail(`${id} met a conflict and ran ${resolves(id).length} resolve agents, not 1`)
    else {
      const r = resolves(id)[0].prompt
      if (!r.includes(`merge --no-ff overnight/${fixture.args.date}`)) fail(`the resolve of ${id} does not back-merge overnight/${fixture.args.date}`)
      for (const f of CONFLICT_FILES) if (!r.includes(f)) fail(`the resolve of ${id} does not name the conflicting file ${f}`)
      if (!/THE CHECK/.test(r)) fail(`the resolve of ${id} does not re-run the check`)
    }
    if (merges[id] !== 2) fail(`${id} was merged ${merges[id] || 0} times; a conflict retries once, so 2`)
  }
  for (const id of Object.keys(merges)) if (![CONFLICT_ONCE, CONFLICT_ALWAYS].includes(id) && resolves(id).length) fail(`${id} ran a resolve agent without a conflict`)
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

if (failures.length) {
  console.log(`overnight_tests: FAILED, ${failures.length} check(s), on ${path.basename(scriptPath)}:`)
  failures.forEach((f) => console.log(`  - ${f}`))
  process.exit(1)
}
console.log(`overnight_tests: pass: ${got.size} integrated, ${fixture.expect.skippedForBlocker.length} skipped for a blocker, ${fixture.expect.startsBefore.length} ordering checks, on ${path.basename(scriptPath)}`)
