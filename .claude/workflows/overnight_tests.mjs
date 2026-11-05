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
// no two merges run at once, and that every ticket the run files passes tools/ticket_shape.py first (#248). Three
// change requests (#436), two ready-for-human and one ready-for-agent, prove that a request is grilled into a draft
// decision and built only on Adam's decision comment, and the mocked lanes' changeRequests fill the report's
// Change requests lists. Each integrate's check is judged against its merge's first parent, and one closing check,
// which answers red, judges the whole run against the run base once every lane has settled; three short runs after the
// main one cover a run that integrated nothing, a closing agent that died, and an ok contradicting its last line
// (#629). Every implement must fork from the last verified tip, or the run base before any gate passed, never from a
// merge still under its gate; and a run of its own, on tickets whose Touches paths overlap, proves the path waits:
// equal paths and directory containment wait, a textual prefix does not, waits point backwards by n, a failed
// predecessor releases its waiter, no readable path waits on its lane, a cycle with a blocked-by edge is dropped, and
// each wait is reported with its paths and clocked minutes (#665); one more, where the lane's downstream-first order is
// not n's, proves that a ticket with no readable path waits on the tickets ahead of it in that order, and that a wait
// whose clock dies is reported unmeasured, never zero (#706 Q1 (b)). One more has a ticket both blocked by
// one ticket and path-waiting on another, and proves that its wait is clocked and logged only once its blocker has
// settled, and that it starts with that wait logged and reported (#665). Two more close a cycle through a path wait
// that points forwards in n, and prove that the cycle guard drops the closing wait whether it points forwards or
// backwards, following path waits as well as blocked-by edges (#916). Five more short runs on synthetic tickets check that
// a lane runs first the ready ticket with the most of tonight's tickets downstream, counted transitively, across lanes,
// once per ticket and only over tickets a lane runs, ties by n (#662). One more short run has an agent die on the
// account's usage limit, and checks that the run halts naming the reset, counts that death as no failure, lists the
// tickets it stopped apart from the failed ones, and starts no agent after it (#710); one more checks that a re-run
// dying on the limit says the run branch may hold its merge, and that a grill the limit stopped does not read as died.
// One more short run has the resume probe find tickets already on the run branch (#712), #677's case with its gate on
// record and #634's with no pass line, and checks that each skips implement and review, runs the gate once and
// integrates. A last short run has the live read find tickets relabelled since collect (#721), #544's case relabelled
// ready-for-human among them, and checks that none is implemented, that each takes its live status, and that a read
// that died fails closed. Every house-rules prompt sends docker to ~/.docker/bin before an agent may call it
// absent (#742). No agent, gh or git call is made.
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
const synthetic = (n, id, status, blockedBy, openBoxes, changeRequest = false) => ({ n, id, slug: id, issue: Number(id.slice(6)), url: `https://example.invalid/${id}`,
  title: `Synthetic ${id}`, status, owner: 'integrator', blockedBy, why: `companion ${status}`, openBoxes, willEndPartial: [], changeRequest,
  touches: [`files/${id}.txt`] })
const HELD_BY = 'issue-135', TRIAGED_AFTER = 'issue-144'
if (byId[HELD_BY].status !== 'ready-for-human' || byId[TRIAGED_AFTER].status !== 'ready-for-agent') throw new Error('the fixture no longer has issue-135 ready-for-human and issue-144 ready-for-agent')
const [CLOSE, AFTER_CLOSE, HELD, TRIAGED] = [
  synthetic(901, 'issue-901', 'close-in-morning', [], 0), synthetic(902, 'issue-902', 'ready-for-agent', ['issue-901'], 2),
  synthetic(903, 'issue-903', 'needs-triage', [HELD_BY], 3), synthetic(904, 'issue-904', 'needs-triage', [TRIAGED_AFTER], 3),
]
for (const t of [CLOSE, AFTER_CLOSE, HELD, TRIAGED]) { tickets.push(t); byId[t.id] = t }
// Three change requests (#436 Q12, after #379's four): a change-request issue is a ticket labelled change-request,
// which collect flags as changeRequest. REQUEST_HUMAN is ready-for-human, so it is grilled into a draft decision;
// REQUEST_AGENT is ready-for-agent, so the integrator lane builds it on Adam's decision comment. REQUEST_DECIDED is
// ready-for-human and already carries Adam's decision comment, so its grill carries the decision and the report
// lists it as decided, not yet relabelled. Every other ticket, the fixture's included, carries no changeRequest
// field, which reads as false.
const [REQUEST_HUMAN, REQUEST_AGENT, REQUEST_DECIDED] = [
  synthetic(905, 'issue-905', 'ready-for-human', [], 2, true), synthetic(906, 'issue-906', 'ready-for-agent', [], 2, true),
  synthetic(907, 'issue-907', 'ready-for-human', [], 2, true),
]
const REQUESTS_GRILLED = [REQUEST_HUMAN.id, REQUEST_DECIDED.id]
for (const t of [REQUEST_HUMAN, REQUEST_AGENT, REQUEST_DECIDED]) { tickets.push(t); byId[t.id] = t }
const CLOSE_WHY = 'every acceptance box is ticked; close it in the morning'
// What the mocked lanes say they did with change requests (#436 Q10), so the report's "Change requests" lists have
// entries from every place a result is kept: AFTER_CLOSE's implement filed request 950 blocking its ticket, TRIAGED's
// review found request 951 open and added an edge, and HELD_BY's grill could not reach GitHub and queued pending line
// 1. REQUEST_HUMAN's grill posted a draft decision and found no decision comment, so it awaits Adam's decision;
// REQUEST_DECIDED's grill found his decision comment, so it is decided.
const FILED = { number: 950, title: 'Name the rotated-key store in contracts/pipeline.md', blocking: AFTER_CLOSE.issue, queued: 0, filed: true }
const EDGE = { number: 951, title: 'Add a package for the synthetic proof', blocking: TRIAGED.issue, queued: 0, filed: false }
const QUEUED = { number: 0, title: 'Define the term Synthetic in GLOSSARY.md', blocking: 0, queued: 1, filed: false }
const DRAFT_URL = `https://example.invalid/issue-905#issuecomment-1`
const DECIDED_LINE = 'Decision: accept'
// AFTER_CLOSE's reviewer reports its implementer's request again, and one it found open but did not wire, which the
// route says to report not at all: neither may add a line to the report's lists.
const UNWIRED = { number: 952, title: 'An open request this ticket does not need', blocking: 0, queued: 0, filed: false }
// CONFLICT_ALWAYS fails at integrate, so its result keeps the implementer's and both reviewers' results whole as impl,
// rev and rev1, not a changeRequests field (#436 Q11): its implement filed request 953, its first review added an edge to
// request 954, and its second review, after the resolve, an edge to request 955. Each list must still reach the report.
const KEPT_IMPL = { number: 953, title: 'Name the conflict owner in contracts/pipeline.md', blocking: byId[CONFLICT_ALWAYS].issue, queued: 0, filed: true }
const KEPT_REV1 = { number: 954, title: 'Add a package for the conflict proof', blocking: byId[CONFLICT_ALWAYS].issue, queued: 0, filed: false }
const KEPT_REV = { number: 955, title: 'Pin the merge driver in OWNERSHIP.md', blocking: byId[CONFLICT_ALWAYS].issue, queued: 0, filed: false }
const changeRequestsOf = (kind, id, k) => (kind === 'implement' && id === AFTER_CLOSE.id) ? [FILED] : (kind === 'review' && id === AFTER_CLOSE.id) ? [FILED, UNWIRED]
  : (kind === 'review' && id === TRIAGED.id) ? [EDGE]
  : (kind === 'implement' && id === CONFLICT_ALWAYS) ? [KEPT_IMPL] : (kind === 'review' && id === CONFLICT_ALWAYS) ? [k === 1 ? KEPT_REV1 : KEPT_REV] : []
const grillResult = (id) => ({ questions: [], changeRequests: id === HELD_BY ? [QUEUED] : [], adrCandidates: [],
  draftDecision: id === REQUEST_HUMAN.id ? DRAFT_URL : '', decided: id === REQUEST_DECIDED.id ? DECIDED_LINE : '', summary: 'companion' })
// The resume probe's answer for a ticket whose work is not yet on the run branch (#712), the case of every run but 36.
const NOT_MERGED = { merged: false, mergeSha: '', subject: '', notes: 'companion' }
// The live read's answer for a ticket still labelled as collect read it (#721), the case of every run but 38.
const LIVE_READY = { state: 'OPEN', labels: ['ready-for-agent', 'kind:ticket'], assignees: [], notes: '' }
const reviewCalls = {}     // id -> review calls so far, so a ticket's first and second review can report apart
// One more, added after the tickets above so none of their choices moves (#480): a tracker edit whose Touches line says
// none, which collect gives the status foreground: no lane, no Route, no grill, and the report's Foreground section.
const FOREGROUND_T = { ...synthetic(908, 'issue-908', 'foreground', [], 1), title: 'Apply Q4 of Overnight 2026-10-28 to #248' }
tickets.push(FOREGROUND_T); byId[FOREGROUND_T.id] = FOREGROUND_T
// Every ticket above names a Touches path of its own (#665), so none waits to start behind another and the checks
// above keep their meaning: a ticket with no readable path would wait on every ticket ahead of it in its lane. Checks
// 43 to 46 run ticket sets of their own whose paths overlap.
for (const t of tickets) if (!('touches' in t)) t.touches = [`files/${t.id}.txt`]
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
// One ready-for-human ticket's grill was asked on an earlier night (#487): its grill returns the questions it carried
// and the summary the grill prompt names for that case, so its reason says carried and already answered; every other
// grill asks its questions tonight, and its reason says grilled on paper. A second carried grill words its summary
// loosely, capitalised and without "not re-grilled", which the reason line still reads as carried, as the report does.
const [CARRIED, CARRIED_LOOSE] = tickets.filter((t) => t.status === 'ready-for-human').map((t) => t.id)
const CARRIED_SUMMARY = 'carried from an earlier night, not re-grilled'
const CARRIED_LOOSE_SUMMARY = 'Carried from an earlier night.'
const grillQuestions = (n) => Array.from({ length: n }, (_, i) => ({ q: `Q${i + 1}?`, recommendation: 'yes' }))
const ELSEWHERE = 'e'.repeat(40)   // the head FLAKE_ELSEWHERE's re-run agent answers ok on, not the gate's RED
const AGAIN_NOTE = 're-run failed: tests/Spactl.Tests failed again alone'
// The closing check's answer in the main run (#629 Q15, Q18): red on a companion that no single merge's check earned.
const CLOSING_TIP = 'd'.repeat(40)
const CLOSING_FAIL_LINE = 'check: fail: the test companion tools/landing_subjects_tests.py failed.'
const CLOSING_RED = { ok: false, headSha: CLOSING_TIP, lastLine: CLOSING_FAIL_LINE, notes: 'companion' }
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

// The run launches with a skills directory other than the script's default (#501), so every skill path a prompt names
// must come from the launch, not from one machine's home.
const OTHER_SKILLS = '/opt/companion/agent-skills'
globalThis.args = { ...fixture.args, skills: OTHER_SKILLS }
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
  if (kind === 'resume') return NOT_MERGED   // the resume probe (#712): nothing of this run is on the run branch yet
  if (kind === 'live') return LIVE_READY   // the live read (#721): still ready-for-agent
  if (kind === 'grill') {
    const asked = id === CARRIED ? { questions: grillQuestions(13), summary: CARRIED_SUMMARY }
      : id === CARRIED_LOOSE ? { questions: grillQuestions(5), summary: CARRIED_LOOSE_SUMMARY }
      : { questions: grillQuestions(2) }
    return byId[id].changeRequest ? grillResult(id) : { ...grillResult(id), ...asked }
  }
  if (kind === 'route') return { flow: 'agent', reason: 'companion' }
  if (kind === 'report') return { questions: 0, mainCheckoutClean: true, leftovers: '', issue: 0, url: '' }
  const n = ++calls
  events.push({ at: tick, kind, id, edge: 'start', n })
  await sleep(kind === 'implement' ? cost[byId[id].owner] * 3 : 30)
  events.push({ at: tick, kind, id, edge: 'end', n })
  if (kind === 'implement' && id === NEEDS_HUMAN) return { outcome: 'needs-human', branch: 'b', forkSha: 'f', headSha: 'h', complete: false, outstanding: '', summary: 'stopped on a storage question', question: QUESTION, delegatedDecisions: [], changeRequests: changeRequestsOf(kind, id) }
  if (kind === 'implement') return { outcome: 'resolved', branch: 'b', forkSha: 'f', headSha: 'h', complete: true, outstanding: '', summary: 's', question: '', delegatedDecisions: [], changeRequests: changeRequestsOf(kind, id) }
  if (kind === 'review') return { outcome: 'committed', headSha: 'h', findingsFixed: [], findingsSkipped: [], summary: 's', question: '', changeRequests: changeRequestsOf(kind, id, reviewCalls[id] = (reviewCalls[id] || 0) + 1), changedFiles: changedFiles(id) }
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
  // The closing check (#629) answers red, the case the report has to carry without resetting any merge.
  if (kind === 'closing') return CLOSING_RED
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
const want = new Set([...fixture.expect.integrated.filter((id) => id !== CONFLICT_ALWAYS && id !== NEEDS_HUMAN && !FLAKE_FAILED.includes(id)), AFTER_CLOSE.id, TRIAGED.id, REQUEST_AGENT.id])
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
// The change-request route (#436): its search, its create, and the house rule that names the create.
const REQUEST_SEARCH = `cd ${fixture.args.repo} && gh issue list --state open --label change-request --limit 500 --json number,title,body`
const REQUEST_CREATE = 'gh issue create --title "<title>" --label change-request,ready-for-human --blocking '
const HOUSE_CREATE = 'gh issue create` only for a change request, after the search'

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
  // A grill carried from an earlier night asked nothing tonight (#487): its reason says carried and already answered,
  // and never "grilled on paper", which read 39 carried grills as hundreds of new questions on 2026-10-28 (#476). A
  // grill that asked tonight keeps "grilled on paper". The summary the mock returns must be the one the grill prompt
  // tells a carried grill to return, so the script and its prompt cannot drift apart.
  const grillPrompt = allPrompts.find((x) => x.label === `grill:${CARRIED}`)
  if (!grillPrompt) fail(`no grill ran for ${CARRIED}, so the carried-grill reason check read nothing`)
  else if (!grillPrompt.prompt.includes(`summary "${CARRIED_SUMMARY}"`)) fail(`the grill prompt no longer tells a carried grill to return the summary "${CARRIED_SUMMARY}"`)
  const carried = of(CARRIED)[0]
  if (!carried) fail(`the run's reasons hold no entry for ${CARRIED}, whose grill was carried`)
  else if (!carried.reason.includes(`carried, 13 question(s) already answered on #${byId[CARRIED].issue}`) || carried.reason.includes('grilled on paper')) {
    fail(`${CARRIED}'s grill was carried, yet its reason does not say carried and already answered: ${carried.reason}`)
  }
  const loose = of(CARRIED_LOOSE)[0]
  if (!loose) fail(`the run's reasons hold no entry for ${CARRIED_LOOSE}, whose grill was carried with a loosely worded summary`)
  else if (!loose.reason.includes(`carried, 5 question(s) already answered on #${byId[CARRIED_LOOSE].issue}`) || loose.reason.includes('grilled on paper')) {
    fail(`${CARRIED_LOOSE}'s grill was carried with the summary "${CARRIED_LOOSE_SUMMARY}", yet its reason does not say carried and already answered: ${loose.reason}`)
  }
  // A change request's grill drafts a decision and asks no numbered question (#436 Q9), so it is not one of these.
  const fresh = grilled.filter((t) => t.id !== CARRIED && t.id !== CARRIED_LOOSE && !t.changeRequest)
  if (!fresh.length) fail('every grilled ticket was carried, so the fresh-grill reason check read nothing')
  for (const t of fresh) {
    const r = of(t.id)[0]
    if (r && (!r.reason.includes(`grilled on paper, 2 question(s) on #${t.issue}`) || r.reason.includes('carried'))) fail(`${t.id}'s grill asked tonight, yet its reason does not say grilled on paper: ${r.reason}`)
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
  const BOTH_NUMBERS = 'Each question opens with its night-wide number, then, in parentheses, the number it carries in its ticket\'s own comment, taken unchanged from that comment, as in "**Q94.** (#244 Q9)"; a question its source does not number (a status note\'s question) opens with the night-wide number alone, as in "**Q95.**"'
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
      // #629 Q19: the re-run judges the same merge against the same old commit the red check was judged against.
      if (!p.includes(`tools/check.sh --full --base ${OLD} --port `) || !p.includes(' --skip-tests; }')) fail(`the re-run of ${id} does not run the rest of the check with --skip-tests, against the merge's first parent ${OLD}`)
      if (p.includes(`--base a${'a'.repeat(39)}`)) fail(`the re-run of ${id} still judges against the run base, not the merge's first parent (#629)`)
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

// 18b. A Touches: none ticket is foreground work (#480): #402, #404 and #405, each "Apply Qn of Overnight <date> to
//      #m", stayed open from 2026-10-25 and #404 and #405 were grilled again on 2026-10-28, though no lane can make a
//      tracker edit and the answer was already confirmed. Collect gives it the status foreground; the run gives it no
//      lane, no Route agent and no grill agent, and the report lists it under its own Foreground section.
{
  const id = FOREGROUND_T.id
  const report = allPrompts.find((x) => x.label === 'report')
  const summary = report ? JSON.parse((/^Run results \(JSON\): (.*)$/m.exec(report.prompt) || [, 'null'])[1]) : null
  if (started(id)) fail(`${id} is foreground, yet a lane started its implement`)
  for (const kind of ['route', 'grill', 'review', 'integrate']) if (allPrompts.some((x) => x.label === `${kind}:${id}`)) fail(`${id} is foreground, yet a ${kind} agent ran for it`)
  if (!(result.foreground || []).includes(id)) fail(`${id} is not in the run's foreground`)
  if ((result.needsHuman || []).includes(id) || (result.skipped || []).includes(id)) fail(`${id} is foreground, yet the run reports it needs-human or skipped`)
  if (!summary || !(summary.foreground || []).some((f) => f.id === id && f.issue === FOREGROUND_T.issue && f.title === FOREGROUND_T.title)) fail(`the report's results do not list ${id} (#${FOREGROUND_T.issue}) with its title as foreground`)
  if (!report || !report.prompt.includes('- Foreground (#480): every entry of the run results\' "foreground"') || !report.prompt.includes('under a "## Foreground" heading')) fail('the report prompt does not name the Foreground section')
  if (!report || !report.prompt.includes('the issue it edits')) fail('the report prompt does not give each foreground ticket its target issue')
  const FG_RULE = '`**Touches:** none` line, whatever its label -> "foreground"'
  for (const c of allPrompts.filter((x) => x.label === 'collect')) if (!c.prompt.includes(FG_RULE)) fail('the collect prompt does not give a Touches: none ticket the status foreground')
}

// 19. One grammar where a ticket is filed (#248): the ticket-shape check at creation, the collect tool's forecast,
//     and the run's own filing site. NEW TICKETS, the one place the run files a ticket, pipes each body through
//     tools/ticket_shape.py under the label and blockers it is filed with, before its `gh issue create`, and files
//     nothing when the check fails. Every other `gh issue create` a prompt carries is the report's own issue,
//     "Overnight <date>", which is no ticket, or a change request filed by the change-request route after its
//     search (#436), whose body shape check 22 runs through the same check. Retro's filing site is retro.js's,
//     proved by its own companion.
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
  // Every gh issue create in every prompt the run sends is the one checked NEW TICKETS create, the report's issue, the
  // house rule that names the change-request create, or the change-request route's create after its search (#436).
  for (const c of allPrompts) {
    for (const x of c.prompt.matchAll(/gh issue create\b/g)) {
      const rest = c.prompt.slice(x.index)
      const searched = c.prompt.lastIndexOf(REQUEST_SEARCH, x.index) >= 0
      const ok = (c.label === 'collect' && x.index === creates.get(c)) || (c.label === 'report' && rest.startsWith(REPORT_CREATE))
        || rest.startsWith(HOUSE_CREATE) || (rest.startsWith(REQUEST_CREATE) && searched)
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

// 21. Change requests are issues (#436; #421 Q6, Q8): one changeRequests field replaces proposedSteering and
//     glossaryProposals, and no prompt or schema sends a session to the file route under contracts/changes/, which is
//     an archive (#424). The script's own source is read, so a schema naming a retired field fails here though no
//     mock agent reads a schema.
const reportPrompt = (allPrompts.find((x) => x.label === 'report') || {}).prompt || ''
const reportSummary = reportPrompt ? JSON.parse((/^Run results \(JSON\): (.*)$/m.exec(reportPrompt) || [, 'null'])[1]) : null
{
  const src = fs.readFileSync(scriptPath, 'utf8')
  for (const retired of ['proposedSteering', 'glossaryProposals', '## Proposed steering changes', 'contracts/changes/'])
    if (src.includes(retired)) fail(`#436: ${path.basename(scriptPath)} still names ${retired}`)
  for (const c of allPrompts) if (c.prompt.includes('contracts/changes/')) fail(`#436: the ${c.label} prompt names contracts/changes/`)
  if (!reportSummary) fail('#436: no report prompt carried the run results, so the changeRequests checks read nothing')
  else {
    const r = reportSummary.results
    const implOf = (id) => r[id] && r[id].changeRequests && r[id].changeRequests.implement
    const revOf = (id) => r[id] && r[id].changeRequests && r[id].changeRequests.review
    if (JSON.stringify(implOf(AFTER_CLOSE.id)) !== JSON.stringify([FILED])) fail(`#436: ${AFTER_CLOSE.id}'s integrated result does not keep its implementer's changeRequests: ${JSON.stringify(r[AFTER_CLOSE.id])}`)
    if (JSON.stringify(revOf(TRIAGED.id)) !== JSON.stringify([EDGE])) fail(`#436: ${TRIAGED.id}'s integrated result does not keep its reviewer's changeRequests: ${JSON.stringify(r[TRIAGED.id])}`)
    const g = r[HELD_BY] && r[HELD_BY].grill
    if (!g || JSON.stringify(g.changeRequests) !== JSON.stringify([QUEUED])) fail(`#436: ${HELD_BY}'s grill result does not keep its changeRequests: ${JSON.stringify(g)}`)
    const d = r[REQUEST_HUMAN.id] && r[REQUEST_HUMAN.id].grill
    if (!d || d.draftDecision !== DRAFT_URL || d.decided !== '') fail(`#436: ${REQUEST_HUMAN.id}'s grill result does not keep its draftDecision and decided: ${JSON.stringify(d)}`)
    const e = r[REQUEST_DECIDED.id] && r[REQUEST_DECIDED.id].grill
    if (!e || e.decided !== DECIDED_LINE) fail(`#436: ${REQUEST_DECIDED.id}'s grill result does not keep the decision it carried: ${JSON.stringify(e)}`)
    // The failed result keeps each stage's result whole; the report's lists are checked from them in check 24.
    const c = r[CONFLICT_ALWAYS] || {}
    const keptOf = (x) => JSON.stringify((x && x.changeRequests) || [])
    if (c.stage !== 'integrate' || keptOf(c.impl) !== JSON.stringify([KEPT_IMPL]) || keptOf(c.rev1) !== JSON.stringify([KEPT_REV1]) || keptOf(c.rev) !== JSON.stringify([KEPT_REV]))
      fail(`#436: ${CONFLICT_ALWAYS}'s failed result does not keep its implementer's and two reviewers' changeRequests as impl, rev1 and rev: ${JSON.stringify(c)}`)
  }
}

// 22. A lane, a review and a grill file a change request by one route (#409; #421 Q2, Q8; #436): the open
//     change-request issues are searched before anything is filed; a request is filed ready-for-human, blocking its
//     ticket; and when GitHub is unreachable a create line is queued in the shape morning.js replays (#433). The
//     route's body shape is run through tools/ticket_shape.py under ready-for-human, as collect's forecast judges
//     every listed ticket: without its Owner line it would fail owner-line (#442 Q68). Just before the create, the
//     route runs tools/ticket_shape.py --since from the time its search began, files only on its pass, folds a
//     duplicate filed since, and queues the line, carrying that time, when nothing was searched (#678).
{
  const PENDING = `${LOG}/pending-${fixture.args.date}.jsonl`
  const routed = allPrompts.filter((c) => /^(implement|review|grill):/.test(c.label))
  if (!routed.some((c) => c.label.startsWith('implement:')) || !routed.some((c) => c.label.startsWith('review:')) || !routed.some((c) => c.label.startsWith('grill:'))) fail('#436: no implement, review or grill prompt was rendered, so the route checks read nothing')
  let shape = null, sinceRecipe = null
  const SEARCHED_NOTE = 'Run `date -u +%FT%TZ` just before it and keep what it prints as <searched>'
  const SINCE_CHECK = `cd ${fixture.args.repo} && python3 -I tools/ticket_shape.py --since <searched> --title "<title>"`
  for (const c of routed) {
    const id = c.label.split(':')[1], issue = byId[id].issue
    const search = c.prompt.indexOf(REQUEST_SEARCH)
    const create = c.prompt.indexOf(`${REQUEST_CREATE}${issue} --body-file ${LOG}/${id}-cr.md`)
    if (search < 0) { fail(`#436: the ${c.label} prompt does not search the open change-request issues`); continue }
    if (create < 0) { fail(`#436: the ${c.label} prompt does not file a change request ready-for-human, blocking #${issue}`); continue }
    if (search > create) fail(`#436: the ${c.label} prompt files a change request before it searches`)
    if (!c.prompt.includes(`gh issue edit ${issue} --add-blocked-by <its number>\`; otherwise change nothing and report nothing for it.`)) fail(`#436: the ${c.label} prompt adds no edge to a request it finds open, or reports one it did not wire`)
    const queue = `jq -cn --arg t "<title>" --arg s "<searched>" --argjson k <${issue}, or 0 when it blocks nothing> --rawfile b ${LOG}/${id}-cr.md '{issue: 0, action: "create", title: $t, label: "change-request,ready-for-human", blocking: $k, searched: $s, body: $b}' >> ${PENDING}`
    if (!c.prompt.includes(queue)) fail(`#436: the ${c.label} prompt does not queue the morning's create line, with the time its search began, when GitHub is unreachable`)
    else if (c.prompt.indexOf(queue) < create) fail(`#436: the ${c.label} prompt queues the create line before it tries to file`)
    // #678: the time is noted before the search, and the --since check runs from it after the search and before the
    // create, with a rule for each of its exits; exit 2 files nothing.
    const route = c.prompt.lastIndexOf('THE CHANGE-REQUEST ROUTE', search)
    const noted = c.prompt.indexOf(SEARCHED_NOTE, route)
    const since = c.prompt.indexOf(SINCE_CHECK, search)
    if (route < 0 || noted < 0 || noted > search) fail(`#678: the ${c.label} prompt does not note the time its change-request search began, as <searched>, before the search`)
    if (since < 0 || since > create) fail(`#678: the ${c.label} prompt does not run ${SINCE_CHECK} after its search and before its create`)
    else {
      const step = c.prompt.slice(since, create)
      for (const rule of ['On exit 0, its last line starting "ticket-shape: pass:", file it', 'On exit 1, read each issue a "ticket-shape: possible-duplicate:" line names', 'gh issue view <n> --json title,body,labels,state`', 'name each that does not with `--not-duplicate-of', 'On any other exit nothing was searched, so file nothing'])
        if (!c.prompt.slice(since).includes(rule)) fail(`#678: the ${c.label} prompt's --since step lacks "${rule}"`)
      if (!step.includes('On exit 0')) fail(`#678: the ${c.label} prompt files before it says the --since check must pass`)
      if (!sinceRecipe) sinceRecipe = (/`cd \S+ && (python3 -I tools\/ticket_shape\.py --since <searched> --title "<title>")`/.exec(c.prompt.slice(since - 1)) || [])[1]
    }
    if (!shape) shape = (/in this shape:\n([\s\S]*?)\n   Its title/.exec(c.prompt) || [])[1]
  }
  // #678: the route's --since recipe is run for real against a fake gh on PATH, so a recipe the script refuses (an
  // argument it does not take, a time it does not read) fails here and not on the night. An empty listing passes; a
  // listing holding an issue whose title shares three content words with the title fails it on exit 1.
  if (!sinceRecipe) fail('#678: no prompt gave the --since recipe, so the live run read nothing')
  else {
    const bin = fs.mkdtempSync(path.join(os.tmpdir(), 'overnight-tests-since-'))
    const gh = path.join(bin, 'gh')
    fs.writeFileSync(gh, '#!/bin/sh\ncat "$LISTING"\n', { mode: 0o755 })
    const run = (listing) => {
      fs.writeFileSync(path.join(bin, 'listing.json'), JSON.stringify(listing))
      const sh = sinceRecipe.replace('<searched>', '2026-10-31T22:00:00Z').replace('<title>', 'The proxy needs a header contract')
      return spawnSync('sh', ['-c', sh], { cwd: path.resolve(here, '..', '..'), encoding: 'utf8', env: { PATH: `${bin}:/usr/bin:/bin`, LISTING: path.join(bin, 'listing.json') } })
    }
    const clear = run([])
    if (clear.status !== 0 || !/^ticket-shape: pass:/m.test(clear.stdout)) fail(`#678: the route's --since recipe does not pass an empty listing: exit ${clear.status}, ${(clear.stdout + clear.stderr).trim()}`)
    // The twin's number is built, not written after a hash, so the steering check reads no citation of an issue here.
    const TWIN = 901
    const twin = run([{ number: TWIN, title: 'A header contract for the proxy', createdAt: '2026-10-31T23:10:00Z', state: 'OPEN' }])
    if (twin.status !== 1 || !new RegExp(`^ticket-shape: possible-duplicate: #${TWIN} `, 'm').test(twin.stdout)) fail(`#678: the route's --since recipe does not fail a possible duplicate filed since the search: exit ${twin.status}, ${(twin.stdout + twin.stderr).trim()}`)
    fs.rmSync(bin, { recursive: true, force: true })
  }
  // morning.js runs its --since check from the line's searched field (#678).
  if (!fs.readFileSync(path.join(here, 'morning.js'), 'utf8').includes('the line\'s "searched"')) fail('#678: morning.js does not read the create line\'s searched field, so the morning searches from its own time alone')
  for (const c of allPrompts.filter((x) => x.label.startsWith('implement:'))) {
    if (!c.prompt.includes('cite the request\'s number (#<n>)')) fail(`#436: the ${c.label} prompt does not cite the request's number on a partial lane's Outstanding line`)
  }
  // morning.js replays exactly this label on a create line, and refuses any other (#433).
  const morning = fs.readFileSync(path.join(here, 'morning.js'), 'utf8')
  if (!morning.includes('"label": "change-request,ready-for-human"')) fail('#436: morning.js no longer replays a create line labelled change-request,ready-for-human, so the queued line would fail')
  if (!shape) fail('#436: no prompt gave the change request\'s body shape, so the ticket-shape run read nothing')
  else {
    const body = shape.split('\n').map((l) => l.replace(/^   /, '')).filter((l) => !/^\*\*(Rows|Raised by):\*\*/.test(l))
      .map((l) => l.replace('<each integrator path it changes>', '`contracts/pipeline.md`').replace(/^<.*>$/, 'The companion fills this paragraph.')).join('\n') + '\n'
    const json = JSON.stringify({ number: 0, body, labels: [{ name: 'change-request' }, { name: 'ready-for-human' }], subIssuesSummary: { total: 0 }, blockedBy: { nodes: [] } })
    const r = spawnSync('python3', ['-I', 'tools/ticket_shape.py', '-'], { cwd: path.resolve(here, '..', '..'), input: json, encoding: 'utf8' })
    if (r.status !== 0 || !/^ticket-shape: pass:/m.test(r.stdout)) fail(`#436: a change request in the route's shape fails tools/ticket_shape.py: exit ${r.status}, ${(r.stdout + r.stderr).trim()}\n${body}`)
  }
}

// 23. The review's trailer read goes (#411 Q5, #436): the steering check reads the Authority trailers, and the
//     reviewer judges whether the cited authority decides the change, filing a held change as a request.
for (const c of allPrompts.filter((x) => x.label.startsWith('review:'))) {
  if (/trailers:key=Authority/.test(c.prompt)) fail(`#436: the ${c.label} prompt still reads the Authority trailers`)
  if (!c.prompt.includes('judge whether the authority the change cites decides it')) fail(`#436: the ${c.label} prompt does not judge whether the cited authority decides a steering change`)
  if (!c.prompt.includes('file it as a change request by the route below')) fail(`#436: the ${c.label} prompt does not file a held steering change as a change request`)
}

// 24. The report's "Change requests" section (#421 Q7, #436 Q11): five lists from the run results, each "none" when
//     empty, after Decisions taken overnight and before Pending tracker writes; a request adds no numbered question.
{
  if (!reportPrompt) fail('#436: no report prompt was rendered, so the Change requests checks read nothing')
  else {
    const at = reportPrompt.indexOf('   - Change requests (')
    if (at < 0) fail('#436: the report prompt names no Change requests section')
    else {
      if (!(reportPrompt.indexOf('   - Decisions taken overnight:') < at && at < reportPrompt.indexOf('   - Pending tracker writes:'))) fail('#436: the Change requests section is not between Decisions taken overnight and Pending tracker writes')
      for (const list of ['"Filed tonight"', '"Edges added to an existing request"', '"Queued for the morning"', '"Awaiting a decision"', '"Decided, not yet labelled ready-for-agent or closed"', 'each reading "none" when empty'])
        if (!reportPrompt.includes(list)) fail(`#436: the report's Change requests section does not give ${list}`)
    }
  }
  const cr = reportSummary && reportSummary.changeRequests
  // CONFLICT_ALWAYS's three entries come only from the impl, rev1 and rev its failed result keeps, and
  // REQUEST_DECIDED's only from a grill that carried a decision, so a list that drops either kind fails here.
  const conflict = { id: CONFLICT_ALWAYS, issue: byId[CONFLICT_ALWAYS].issue }
  const want = {
    filed: [{ id: AFTER_CLOSE.id, issue: AFTER_CLOSE.issue, ...FILED }, { ...conflict, ...KEPT_IMPL }],
    edges: [{ id: TRIAGED.id, issue: TRIAGED.issue, ...EDGE }, { ...conflict, ...KEPT_REV }, { ...conflict, ...KEPT_REV1 }],
    queued: [{ id: HELD_BY, issue: byId[HELD_BY].issue, ...QUEUED }],
    awaiting: [{ id: REQUEST_HUMAN.id, issue: REQUEST_HUMAN.issue, title: REQUEST_HUMAN.title, draftDecision: DRAFT_URL }],
    decided: [{ id: REQUEST_DECIDED.id, issue: REQUEST_DECIDED.issue, title: REQUEST_DECIDED.title, decided: DECIDED_LINE }],
  }
  // The results settle in the order the lanes finish, which the clock decides, so a list is compared as a set.
  const sorted = (xs) => Array.isArray(xs) ? JSON.stringify(xs.map((x) => JSON.stringify(x)).sort()) : JSON.stringify(xs)
  for (const [list, entries] of Object.entries(want)) if (!cr || sorted(cr[list]) !== sorted(entries)) fail(`#436: the run results' changeRequests.${list} is ${JSON.stringify(cr && cr[list])}, not ${JSON.stringify(entries)}`)
}

// 25. The integrator lane on a change request (#436 Q7, Q8; #421 Q4): it reads only the confirming account's comments
//     whose first line begins "Decision:", latest first, and takes #436 Q7's three outcomes (#442 Q20): accept builds and cites that
//     comment's URL; accept as drafted builds the draft, cites the accepting comment, and stops on a draft edited
//     after it; decline, none, or a line it cannot read stops with a status note and ready-for-human, never closing.
//     ADR: yes stops before building. An ordinary ticket's prompt carries none of it.
{
  const req = (allPrompts.find((x) => x.label === `implement:${REQUEST_AGENT.id}`) || {}).prompt
  if (!req) fail(`#436: ${REQUEST_AGENT.id}, a ready-for-agent change request, got no implement prompt`)
  else {
    const wants = [
      'A decision comment is one written by the account docs/agents/tracker-policy.md names (the repository\'s owner while it names none) whose first line begins "Decision:". Read those latest first, and act on the latest alone',
      '- "Decision: accept" or "Decision: accept with changes": build the change as that comment states it, steering paragraphs as quoted there, and commit it with the trailer "Authority: <that comment\'s html_url>".',
      '- "Decision: accept as drafted <link>": build the draft the link names, as it stands, with the trailer "Authority: <the accepting comment\'s html_url>", never the draft\'s own URL. If the draft\'s updated_at is later than the accepting comment\'s created_at, it was edited after Adam accepted it, so stop',
      '- "Decision: decline", no decision comment, or a first line you cannot read as one of those: stop, as below.',
      '"ADR: yes" stops before you build anything',
      `gh issue edit ${REQUEST_AGENT.issue} --remove-label ready-for-agent --add-label ready-for-human --remove-assignee @me`,
      'return outcome "needs-human"',
      'Never close a change request, declined or not',
    ]
    for (const w of wants) if (!req.includes(w)) fail(`#436: ${REQUEST_AGENT.id}'s implement prompt lacks: ${w.slice(0, 100)}`)
    if (req.indexOf('THE DECISION (#436)') > req.indexOf('STEP 1 —')) fail(`#436: ${REQUEST_AGENT.id}'s implement prompt reads the decision after it starts building`)
    if (req.includes(`"Authority: #${REQUEST_AGENT.issue}"`)) fail(`#436: ${REQUEST_AGENT.id}'s implement prompt commits with its own number as the Authority, which the steering check refuses`)
  }
  for (const c of allPrompts.filter((x) => /^(implement|review|integrate|resolve):/.test(x.label) && x.label.split(':')[1] !== REQUEST_AGENT.id))
    for (const w of ['THE DECISION (#436)', 'accept as drafted']) if (c.prompt.includes(w)) fail(`#436: the ${c.label} prompt, not a change request's, carries the request branch (${w})`)
}

// 26. A grill on a change request drafts a decision and asks no numbered questions (#436 Q1, Q9; #421 Q5): it carries
//     an existing draft or decision, ignores the standing answer, posts "## Grilling record" and "## Draft decision"
//     alone, and the draft opens with its heading, never "Decision:", then tracker-policy.md's "Proposed decision:" and
//     "Proposed ADR:" lines (#442 Q43), so no line of a scripted draft reads as Adam's decision or his ADR call. An
//     ordinary grill keeps its questions.
{
  const grills = allPrompts.filter((x) => x.label.startsWith('grill:'))
  for (const id of REQUESTS_GRILLED) {
    const req = grills.filter((x) => x.label === `grill:${id}`)
    if (req.length !== 1) fail(`#436: ${id}, a ready-for-human change request, got ${req.length} grill prompts, not 1`)
    for (const c of req) {
      for (const w of ['"## Draft decision": its first line is that heading, never "Decision:". The two lines after it are the draft\'s, as docs/agents/tracker-policy.md gives them (#442 Q43): "Proposed decision: accept|accept with changes|decline", then "Proposed ADR: yes|no"',
        'Then "Not a decision: Adam accepts it with `Decision: accept as drafted <this comment\'s URL>`.", then the change as it would land',
        'If a draft or a decision comment already exists, carry it and write nothing', 'you never edit a posted draft',
        'The standing answer does not apply to a change request: write no "Answer:" line and no numbered question.'])
        if (!c.prompt.includes(w)) fail(`#436: ${id}'s grill prompt lacks: ${w.slice(0, 100)}`)
      for (const w of ['## Questions for Adam', '## Proposed glossary / ADR', 'pre-accepted under the standing answer']) if (c.prompt.includes(w)) fail(`#436: ${id}'s grill prompt names ${w}`)
      for (const re of [/"Decision: accept\|/, /"ADR: yes\|no"/]) if (re.test(c.prompt)) fail(`#436: ${id}'s grill prompt still drafts a bare ${re.source} line, not tracker-policy.md's Proposed form`)
    }
  }
  const ordinary = grills.filter((x) => !REQUESTS_GRILLED.includes(x.label.split(':')[1]))
  if (!ordinary.length) fail('#436: no ordinary grill prompt was rendered, so the contrast check read nothing')
  for (const c of ordinary) {
    if (!c.prompt.includes('"## Questions for Adam": numbered')) fail(`#436: the ${c.label} prompt, an ordinary grill, lost its numbered questions`)
    if (c.prompt.includes('## Draft decision')) fail(`#436: the ${c.label} prompt, an ordinary grill, carries the request branch`)
  }
}

// 27. No session writes a comment that begins "Decision:" (#436 Q1): every house-rules prompt forbids it beside
//     "Confirm", since every session posts under the account whose "Decision:" comment authorises a request.
{
  const RULE = '- Never write a comment that begins "Confirm" or "Decision:" on any issue: a confirmation, and a change request\'s decision, are the human\'s act (docs/agents/tracker-policy.md). A draft decision opens with its heading, never with "Decision:" (#436).'
  const housed = allPrompts.filter((c) => /House rules \(/.test(c.prompt))
  if (!housed.length) fail('#436: no prompt carries the house rules, so the Decision: rule search read nothing')
  for (const c of housed) if (!c.prompt.includes(RULE)) fail(`#436: the ${c.label} prompt's house rules do not forbid a comment beginning "Decision:"`)
  for (const c of allPrompts.filter((x) => x.label.startsWith('grill:'))) if (!c.prompt.includes('Never write a comment that begins "Confirm" or "Decision:"')) fail(`#436: the ${c.label} prompt, which carries no house rules, does not forbid a comment beginning "Decision:"`)
}

// 28. The skills directory is a launch argument (#501). The run above launched with OTHER_SKILLS: every SKILL.md path
//     a prompt names lies under it, and no prompt names one under a home directory. A launch whose skills is not an
//     absolute path halts naming the argument, and no hint of that halt names a home directory. Outside comments, the
//     one /Users/<name> path the script holds is SKILLS_DEFAULT, the documented default of the skills argument.
{
  const named = allPrompts.flatMap((c) => [...c.prompt.matchAll(/(\/\S*?)\/[\w-]+\/SKILL\.md/g)].map((m) => ({ label: c.label, dir: m[1] })))
  if (!named.some((x) => x.dir === OTHER_SKILLS)) fail(`#501: no prompt names a SKILL.md under the launch's skills directory ${OTHER_SKILLS}`)
  for (const k of new Set(named.filter((x) => x.dir !== OTHER_SKILLS).map((x) => `${x.label} prompt reads a skill from ${x.dir}`))) fail(`#501: the ${k}, not the launch's ${OTHER_SKILLS}`)
  for (const c of allPrompts) if (/\/Users\/[^/\s]+\/\.agents\b/.test(c.prompt)) fail(`#501: the ${c.label} prompt names a home directory's .agents path`)
  const src = fs.readFileSync(scriptPath, 'utf8').replace(/^export const meta/m, 'const meta')
  globalThis.args = { ...fixture.args, repo: 'spa-platform', wt: 'worktrees', skills: 'agent-skills' }
  const halted = await new Function('return (async () => {' + src + '\n})()')()
  const why = (halted && halted.halted) || ''
  if (!/^- skills: /m.test(why)) fail(`#501: a launch with a relative skills path did not halt naming skills: ${why || JSON.stringify(halted)}`)
  if (/\/Users\//.test(why)) fail(`#501: a bad launch's hints name a home directory: ${why}`)
  const homes = src.split('\n').map((line, i) => ({ line, n: i + 1 })).filter((x) => x.line.includes('/Users/') && !/^\s*\/\//.test(x.line) && !/^const SKILLS_DEFAULT = '\/Users\/[^']+'/.test(x.line))
  for (const x of homes) fail(`#501: ${path.basename(scriptPath)}:${x.n} reads a /Users/ path outside the skills argument's default: ${x.line.trim().slice(0, 100)}`)
}

// 29. The report checks each claimed branch's open pull request against the run branch the night before (#628): on
//     2026-10-29 PR #492, listed under "Claimed branches to merge", landed first and changed the files the run's lanes
//     changed, so the landing PR #529 was CONFLICTING with main, ran no CI, and the morning stopped at step 7 until the
//     back-merge 55d0be0. The report's "Claimed branches to merge" item runs `git merge-tree --write-tree` of the run
//     branch against each open pull request's head and lists the conflicting files, and the Morning steps take their
//     landing order from that item rather than always landing the run's pull request first.
{
  const MERGE_TREE = `\`git -C ${fixture.args.repo} merge-tree --write-tree overnight/${fixture.args.date} <its head>\``
  const ORDER = 'merge the claimed branches in their own PRs, in the order "Claimed branches to merge" gives'
  const reports = allPrompts.filter((c) => c.label === 'report')
  if (!reports.length) fail('#628: no report prompt was rendered, so the claimed-branches check read nothing')
  for (const c of reports) {
    const item = c.prompt.split('\n').find((l) => l.includes('Claimed branches to merge (')) || ''
    if (!item.includes(MERGE_TREE)) fail(`#628: the report's "Claimed branches to merge" item does not run ${MERGE_TREE} against each open pull request it names`)
    if (!item.includes('list every file that conflicts') || !item.includes('back-merges main before its review')) fail('#628: the report\'s "Claimed branches to merge" item does not list the conflicting files and say which pull request lands first and which back-merges main')
    // A merge-tree that could not run (first line not a tree id; a missing head exits 1, as a conflict does) must not
    // read as a clean merge (CODING_STANDARDS.md, "A check fails closed").
    if (!item.includes('first line is not a tree id') || !item.includes('never "none conflict"')) fail('#628: the report\'s "Claimed branches to merge" item lets a merge-tree that failed to run read as "none conflict"')
    const morning = c.prompt.split('\n').find((l) => l.includes('- Morning steps: ')) || ''
    if (!morning.includes(ORDER)) fail('#628: the report\'s Morning steps do not take the landing order from "Claimed branches to merge"')
  }
}

// 30. A partial's choice for Adam is a numbered question (#632). On 2026-10-29 the report said of #482's remaining box
//     "You decide between two routes", yet no numbered question carried it, so the Confirm left it outside its range
//     and #482 stayed ready-for-agent. The report prompt's Partial (Outstanding) item asks for a numbered question in
//     step 1b for such a partial, and step 1b lists that choice among the ticket's questions.
{
  const PARTIAL_RULE = 'Partial (Outstanding) (a partial whose outstanding work is a choice for Adam also gets a numbered question in step 1b, so a Confirm can cover it'
  const STEP_RULE = 'and, for a partial whose outstanding work is a choice for Adam, that choice as one question'
  if (!reportPrompt) fail('#632: no report prompt was rendered, so the partial-choice check read nothing')
  else {
    const item = (/^ {3}- Partial \(Outstanding\).*$/m.exec(reportPrompt) || [''])[0]
    if (!item.includes(PARTIAL_RULE)) fail('#632: the report prompt\'s Partial (Outstanding) item does not give a partial whose outstanding work is a choice for Adam a numbered question')
    const step = (/^1b\. .*$/m.exec(reportPrompt) || [''])[0]
    if (!step.includes(STEP_RULE)) fail('#632: the report prompt\'s step 1b does not list a partial\'s choice for Adam among its ticket\'s questions')
  }
}

// 31. Each integrate step's local check is judged against the merge's first parent (#629 Q14). On 2026-10-29 every
//     integrate ran `--base <run base>`, so the n-th merge's check judged all n merges: the span from merge message to
//     integrate log grew from 2m31s (#506, the first) to 10m27s (#513, the last) while the suite stayed near 2 minutes,
//     as the companions earlier merges had earned ran again on each later one. <old> is the run branch's tip before
//     this merge, which STEP 1 records and STEP 0 recovers on a resumed run, so the diff judged is this merge and its
//     roadmap refresh.
{
  const RUN_BASE = 'a'.repeat(40)
  const integrates = allPrompts.filter((c) => c.label.startsWith('integrate:'))
  if (!integrates.length) fail('#629: no integrate prompt was rendered, so the base check read nothing')
  for (const c of integrates) {
    if (!c.prompt.includes('tools/check.sh --full --base <old> --port ')) fail(`#629: the ${c.label} prompt's local check is not judged against <old>, the merge's first parent`)
    if (c.prompt.includes(`--base ${RUN_BASE}`)) fail(`#629: the ${c.label} prompt still judges a check against the run base`)
    if (!c.prompt.includes('Record old=') || !c.prompt.includes('judged against <old>, the merge\'s first parent')) fail(`#629: the ${c.label} prompt does not define <old> on both the first and the resumed path`)
    if (!c.prompt.includes('STEP 2 — the integrate step\'s local check, judged against the merge\'s first parent <old>')) fail(`#629: the ${c.label} prompt's STEP 2 does not name the check in the glossary's words (Q13, Q17)`)
    // The check's command is run exactly as written, and a bare `<old>` in it is a bash redirect from a file named old,
    // so the step says to write the sha in its place (#629's review).
    if (!c.prompt.includes('write <old>\'s 40-character sha where it says <old>')) fail(`#629: the ${c.label} prompt does not say to put <old>'s sha in the check's command, which bash would read as a redirect`)
  }
}

// 32. One closing check judges the whole run against the run base (#629 Q15, Q18): what 31 gives up is the proof that
//     a later merge did not break a companion an earlier merge earned, and no one knows in advance which merge is the
//     last, since the lanes run in parallel. So once every lane has settled, and before the report, one agent runs
//     `tools/check.sh --full --base <run base>` on the run branch's tip, on a port no lane has, and changes nothing.
//     A red one is reported under its own heading with its log and last line; every merge stays, and the morning adds
//     no hold. The main run's closing check answers red; three short runs below cover a run that integrated nothing,
//     a closing agent that died, and an answer of ok whose last line is a failure.
{
  const RUN_BASE = 'a'.repeat(40)
  const INT_WT = `${fixture.args.wt}/overnight`
  const CLOSING_LOG = `${LOG}/closing-check.txt`
  const closings = allPrompts.filter((c) => c.label === 'closing')
  if (closings.length !== 1) fail(`#629: the run rendered ${closings.length} closing-check prompts, not 1`)
  const p = (closings[0] || {}).prompt || ''
  const cmd = (/^ {2}cd (\S+) && \{ echo "HEAD=\$\(git -C \S+ rev-parse HEAD\)"; tools\/check\.sh --full --base (\S+) --port (\d+); \} > (\S+) 2>&1; tail -25 \S+$/m.exec(p) || [])
  if (!cmd.length) fail('#629: the closing-check prompt does not run tools/check.sh --full as the one check command')
  else {
    if (cmd[1] !== INT_WT) fail(`#629: the closing check runs in ${cmd[1]}, not the integration worktree ${INT_WT}`)
    if (cmd[2] !== RUN_BASE) fail(`#629: the closing check is judged against ${cmd[2]}, not the run base ${RUN_BASE}`)
    if (cmd[4] !== CLOSING_LOG) fail(`#629: the closing check logs to ${cmd[4]}, not ${CLOSING_LOG}`)
    // No ticket's port, whether or not its lane ran tonight: a lane's port is 5100 + 10n, with n from 1.
    const lanePorts = new Set([...allPrompts.filter((c) => c.label.startsWith('implement:')).map((c) => (/Your port: (\d+)\./.exec(c.prompt) || [])[1]),
      ...tickets.map((t) => String(5100 + 10 * t.n))])
    if (lanePorts.has(cmd[3])) fail(`#629: the closing check's port ${cmd[3]} is a lane's port`)
  }
  for (const rule of ['Change nothing in it', 'every merge stays', 'STEP 0 — durable state']) if (!p.includes(rule)) fail(`#629: the closing-check prompt lacks "${rule}"`)
  if (/reset --hard|git -C \S+ (merge|commit)\b/.test(p)) fail('#629: the closing-check prompt moves the run branch')
  // After every lane has settled: no integrate ends after the closing check starts; and before the report.
  const closingStart = (events.find((e) => e.kind === 'closing' && e.edge === 'start') || {}).at
  const lastIntegrate = Math.max(...events.filter((e) => e.kind === 'integrate' && e.edge === 'end').map((e) => e.at))
  if (closingStart === undefined) fail('#629: the closing check never started')
  else if (closingStart < lastIntegrate) fail(`#629: the closing check started at ${closingStart}, before the last integrate ended at ${lastIntegrate}`)
  const at = (label) => allPrompts.findIndex((c) => c.label === label)
  if (at('closing') < 0 || at('report') < at('closing')) fail('#629: the closing check does not run before the report')
  // A red closing check is reported, and nothing moves: the integrated set is check 1's, and no reset agent ran for it.
  const want629 = { status: 'fail', log: CLOSING_LOG, base: RUN_BASE, headSha: CLOSING_TIP, lastLine: CLOSING_FAIL_LINE }
  for (const [where, got629] of [['the run\'s result', result.closingCheck], ['the report\'s results', reportSummary && reportSummary.closingCheck]]) {
    for (const [k, v] of Object.entries(want629)) if (!got629 || got629[k] !== v) fail(`#629: ${where} closingCheck.${k} is ${got629 ? JSON.stringify(got629[k]) : 'missing'}, not ${JSON.stringify(v)}`)
  }
  if (allPrompts.some((c) => c.label === 'reset:closing' || c.label === 'reset:undefined')) fail('#629: a red closing check reset the run branch')
  const item = reportPrompt.split('\n').find((l) => l.includes('- Closing check (#629)')) || ''
  for (const rule of ['`### Closing check`', 'its log', 'its last line', 'every merge was kept', 'no morning hold']) if (!item.includes(rule)) fail(`#629: the report prompt's Closing check item lacks "${rule}"`)

  // The short runs: one ticket, or none, and every agent answering at once.
  const SOLO = [...want].find((id) => byId[id].status === 'ready-for-agent' && !byId[id].blockedBy.length && !byId[id].changeRequest)
  const shortRun = async (only, closingAnswer) => {
    const prompts = []
    globalThis.args = { ...fixture.args, skills: OTHER_SKILLS, only }
    globalThis.log = () => {}
    globalThis.agent = async (prompt, opts) => {
      prompts.push({ label: opts.label, prompt })
      const [kind, id] = opts.label.split(':')
      if (kind === 'collect') return { outcome: 'ok', reason: 'companion', tickets: tickets.map((t) => ({ ...t })), landed: [], base: RUN_BASE, shell: '3.2', shellCommand: '/bin/bash' }
      if (kind === 'resume') return NOT_MERGED   // the resume probe (#712): nothing of this run is on the run branch yet
      if (kind === 'live') return LIVE_READY   // the live read (#721): still ready-for-agent
      if (kind === 'implement') return { outcome: 'resolved', branch: 'b', forkSha: 'f', headSha: 'h', complete: true, outstanding: '', summary: 's', question: '', delegatedDecisions: [], changeRequests: [] }
      if (kind === 'review') return { outcome: 'committed', headSha: 'h', findingsFixed: [], findingsSkipped: [], summary: 's', question: '', changeRequests: [], changedFiles: [`files/${id}.txt`] }
      if (kind === 'integrate') return { ok: true, mergeSha: 'm', notes: '', conflictFiles: [], headSha: `verified-${id}`, old: '', redHead: '', reach: '' }
      if (kind === 'closing') return closingAnswer
      if (kind === 'report') return { questions: 0, mainCheckoutClean: true, leftovers: '', issue: 0, url: '' }
      throw new Error(`unexpected agent label ${opts.label} in a closing-check run`)
    }
    const src = fs.readFileSync(scriptPath, 'utf8').replace(/^export const meta/m, 'const meta')
    return { result: await new Function('return (async () => {' + src + '\n})()')(), prompts }
  }
  if (!SOLO) fail('#629: the fixture has no unblocked ticket to integrate alone, so the short closing-check runs read nothing')
  else {
    const none = await shortRun([], null)
    if (none.prompts.some((c) => c.label === 'closing')) fail('#629: a run that integrated nothing still ran a closing check')
    if (!none.result.closingCheck || none.result.closingCheck.status !== 'not-run' || !/nothing integrated/.test(none.result.closingCheck.why || '')) fail(`#629: a run that integrated nothing does not say its closing check did not run because nothing integrated: ${JSON.stringify(none.result.closingCheck)}`)
    const died = await shortRun([SOLO], null)
    if (JSON.stringify(died.result.integrated) !== JSON.stringify([SOLO])) fail(`#629: the short run integrated ${JSON.stringify(died.result.integrated)}, not [${SOLO}]`)
    if (!died.result.closingCheck || died.result.closingCheck.status !== 'not-run' || !/died/.test(died.result.closingCheck.why || '') || died.result.closingCheck.log !== CLOSING_LOG) fail(`#629: a closing agent that died does not read as not run, naming its log: ${JSON.stringify(died.result.closingCheck)}`)
    // The script reads the verdict from the last line, not from the agent's ok, as the keep rule reads a re-run (#397).
    const claimed = await shortRun([SOLO], { ok: true, headSha: CLOSING_TIP, lastLine: CLOSING_FAIL_LINE, notes: '' })
    if (!claimed.result.closingCheck || claimed.result.closingCheck.status !== 'fail') fail(`#629: an ok whose last line is a failure reads as ${JSON.stringify(claimed.result.closingCheck)}, not fail`)
    const green = await shortRun([SOLO], { ok: true, headSha: CLOSING_TIP, lastLine: 'check: pass: A-05, the roadmap.', notes: '' })
    if (!green.result.closingCheck || green.result.closingCheck.status !== 'pass') fail(`#629: a green closing check reads as ${JSON.stringify(green.result.closingCheck)}, not pass`)
  }
}

// 33. A lane runs first the ready ticket with the most of tonight's tickets downstream (#662). On 2026-10-31 the
//     static-analysis head #605 had 18 of the night's tickets downstream, the five workstream lanes' only work among
//     them, and sat at n=70 behind 19 ready integrator tickets with nothing downstream. Five short runs on synthetic
//     tickets, every agent answering at once; implement starts are counted by ordinal, since the tick does not move.
const DOWNSTREAM_CASES = 5
{
  const orderRun = async (set) => {
    const starts = []
    globalThis.args = { ...fixture.args, skills: OTHER_SKILLS }
    globalThis.log = () => {}
    globalThis.agent = async (prompt, opts) => {
      const [kind, id] = opts.label.split(':')
      if (kind === 'collect') return { outcome: 'ok', reason: 'companion', tickets: set.map((t) => ({ ...t })), landed: [], base: 'a'.repeat(40), shell: '3.2', shellCommand: '/bin/bash' }
      if (kind === 'resume') return NOT_MERGED   // the resume probe (#712): nothing of this run is on the run branch yet
      if (kind === 'live') return LIVE_READY   // the live read (#721): still ready-for-agent
      if (kind === 'implement') { starts.push(id); return { outcome: 'resolved', branch: 'b', forkSha: 'f', headSha: 'h', complete: true, outstanding: '', summary: 's', question: '', delegatedDecisions: [], changeRequests: [] } }
      if (kind === 'review') return { outcome: 'committed', headSha: 'h', findingsFixed: [], findingsSkipped: [], summary: 's', question: '', changeRequests: [], changedFiles: [`files/${id}.txt`] }
      if (kind === 'integrate') return { ok: true, mergeSha: 'm', notes: '', conflictFiles: [], headSha: `verified-${id}`, old: '', redHead: '', reach: '' }
      if (kind === 'closing') return { ok: true, headSha: CLOSING_TIP, lastLine: 'check: pass: companion.', notes: '' }
      if (kind === 'report') return { questions: 0, mainCheckoutClean: true, leftovers: '', issue: 0, url: '' }
      throw new Error(`unexpected agent label ${opts.label} in a downstream-order run`)
    }
    const src = fs.readFileSync(scriptPath, 'utf8').replace(/^export const meta/m, 'const meta')
    const res = await new Function('return (async () => {' + src + '\n})()')()
    return { res, starts }
  }
  const lane = (n, owner, blockedBy) => ({ ...synthetic(n, `issue-${n}`, 'ready-for-agent', blockedBy, 1), owner })
  const before = (starts, a, b, why) => {
    const i = starts.indexOf(a), j = starts.indexOf(b)
    if (i < 0 || j < 0) fail(`#662: ${why}: ${i < 0 ? a : b} never started (${JSON.stringify(starts)})`)
    else if (i > j) fail(`#662: ${why}: ${b} started before ${a} (${JSON.stringify(starts)})`)
  }
  // Case 1: FREE (n=961) has nothing downstream; HEAD (n=962) has three in other lanes, one of them only through
  // another (HEAD <- ws1's 971 <- ws2's 972, and HEAD <- ws3's 973), so the count is transitive and crosses lanes.
  {
    const set = [lane(961, 'integrator', []), lane(962, 'integrator', []),
      lane(971, 'ws1-infra', ['issue-962']), lane(972, 'ws2-hosting', ['issue-971']), lane(973, 'ws3-identity', ['issue-962'])]
    const { res, starts } = await orderRun(set)
    if ((res.integrated || []).length !== set.length) fail(`#662: the first downstream-order run integrated ${JSON.stringify(res.integrated)}, not all ${set.length}`)
    before(starts, 'issue-962', 'issue-961', 'a ready ticket with three tickets downstream does not run before a lower n with none')
  }
  // Case 2: two ready tickets with one downstream each (a tie above zero) run in n order, as before #662.
  {
    const set = [lane(964, 'integrator', []), lane(963, 'integrator', []),
      lane(974, 'ws1-infra', ['issue-963']), lane(975, 'ws2-hosting', ['issue-964'])]
    const { res, starts } = await orderRun(set)
    if ((res.integrated || []).length !== set.length) fail(`#662: the second downstream-order run integrated ${JSON.stringify(res.integrated)}, not all ${set.length}`)
    before(starts, 'issue-963', 'issue-964', 'two ready tickets with equal downstream counts do not run in n order')
  }
  // Case 3: the count is transitive. WIDE (n=965) has two directly downstream; DEEP (n=966) has a chain of three
  // (DEEP <- 978 <- 979 <- 980), one of them directly, so only a transitive count puts DEEP first.
  {
    const set = [lane(965, 'integrator', []), lane(966, 'integrator', []),
      lane(976, 'ws1-infra', ['issue-965']), lane(977, 'ws2-hosting', ['issue-965']),
      lane(978, 'ws3-identity', ['issue-966']), lane(979, 'ws4-proxy', ['issue-978']), lane(980, 'ws5-fixtures', ['issue-979'])]
    const { res, starts } = await orderRun(set)
    if ((res.integrated || []).length !== set.length) fail(`#662: the third downstream-order run integrated ${JSON.stringify(res.integrated)}, not all ${set.length}`)
    before(starts, 'issue-966', 'issue-965', 'a chain of three downstream does not outrank two direct ones, so the count is not transitive')
  }
  // Case 4: a ticket downstream by two paths counts once. FLAT (n=967) has three direct; DIAMOND (n=968) has 984 and
  // 985 directly and 986 behind both, three tickets by four paths, so the tie goes to FLAT's lower n.
  {
    const set = [lane(967, 'integrator', []), lane(968, 'integrator', []),
      lane(981, 'ws1-infra', ['issue-967']), lane(982, 'ws2-hosting', ['issue-967']), lane(983, 'ws3-identity', ['issue-967']),
      lane(984, 'ws4-proxy', ['issue-968']), lane(985, 'ws5-fixtures', ['issue-968']), lane(986, 'ws6-verification', ['issue-984', 'issue-985'])]
    const { res, starts } = await orderRun(set)
    if ((res.integrated || []).length !== set.length) fail(`#662: the fourth downstream-order run integrated ${JSON.stringify(res.integrated)}, not all ${set.length}`)
    before(starts, 'issue-967', 'issue-968', 'a ticket reached by two paths counts twice downstream')
  }
  // Case 5: only tickets a lane runs count (#662's Answer). IDLE (n=987) has two downstream that no lane runs, 990
  // blocked on an issue outside tonight's set and 991 close-in-morning; BUSY (n=988) has one lane ticket, 989. A count
  // over every ticket would put IDLE first; a count over lane tickets puts BUSY first.
  {
    const lanes5 = [lane(987, 'integrator', []), lane(988, 'integrator', []), lane(989, 'ws1-infra', ['issue-988'])]
    const set = [...lanes5,
      { ...synthetic(990, 'issue-990', 'blocked', ['issue-987', 'issue-999'], 1), owner: 'ws2-hosting' },
      { ...synthetic(991, 'issue-991', 'close-in-morning', ['issue-987'], 0), owner: 'ws3-identity' }]
    const { res, starts } = await orderRun(set)
    if ((res.integrated || []).length !== lanes5.length) fail(`#662: the fifth downstream-order run integrated ${JSON.stringify(res.integrated)}, not all ${lanes5.length} lane tickets`)
    before(starts, 'issue-988', 'issue-987', 'a ticket no lane runs counts downstream')
  }
}

// 34. An agent that dies on the account's usage limit halts the run with the reset the message names, and counts as no
//     failure (#710). On 2026-11-01 each such death went through fail() toward maxFailures, so the run halted at "8
//     failures" with 4 real ones, and a resume before the reset lost 18 more agents the same way. One short run on
//     synthetic tickets, with the agents timed by sleeps: in the integrator lane DIES's implement throws the
//     message, which the 2026-11-01 run quoted, at 40 ms, and AFTER waits behind it in the same lane; in ws1 FIRST's
//     implement runs from 0 to 30 ms and its review from 30 to 60 ms, both started before the death, and SECOND's
//     implement from 30 to 60 ms; in ws2 BROKEN's review fails at about 10 ms, a real failure. After the death no agent
//     may start: not FIRST's integrate, not SECOND's review, not AFTER's implement, not the closing check or the report.
const LIMIT_MESSAGE = 'You\'ve hit your weekly limit · resets 10pm (America/Chicago)'
const LIMIT_HALTED = 'usage limit; resume after 10pm (America/Chicago)'
{
  const started710 = [], late = []
  let died = false
  globalThis.args = { ...fixture.args, skills: OTHER_SKILLS }
  globalThis.log = () => {}
  globalThis.agent = async (prompt, opts) => {
    const [kind, id] = opts.label.split(':')
    if (died) late.push(opts.label)
    started710.push(opts.label)
    if (kind === 'collect') return { outcome: 'ok', reason: 'companion', tickets: set710.map((t) => ({ ...t })), landed: [], base: 'a'.repeat(40), shell: '3.2', shellCommand: '/bin/bash' }
    if (kind === 'resume') return NOT_MERGED   // the resume probe (#712): nothing of this run is on the run branch yet
    if (kind === 'live') return LIVE_READY   // the live read (#721): still ready-for-agent
    if (kind === 'report') return { questions: 0, mainCheckoutClean: true, leftovers: '', issue: 0, url: '' }
    if (kind === 'closing') return { ok: true, headSha: CLOSING_TIP, lastLine: 'check: pass: companion.', notes: '' }
    await sleep(id === 'issue-1001' ? 40 : id === 'issue-1003' || id === 'issue-1004' ? 30 : 5)
    if (kind === 'implement' && id === 'issue-1001') { died = true; throw new Error(LIMIT_MESSAGE) }
    if (kind === 'implement') return { outcome: 'resolved', branch: 'b', forkSha: 'f', headSha: 'h', complete: true, outstanding: '', summary: 's', question: '', delegatedDecisions: [], changeRequests: [] }
    if (kind === 'review' && id === 'issue-1005') return { outcome: 'failed', headSha: 'h', findingsFixed: [], findingsSkipped: [], summary: 'a real failure', question: '', changeRequests: [], changedFiles: [] }
    if (kind === 'review') return { outcome: 'committed', headSha: 'h', findingsFixed: [], findingsSkipped: [], summary: 's', question: '', changeRequests: [], changedFiles: [`files/${id}.txt`] }
    if (kind === 'integrate') return { ok: true, mergeSha: 'm', notes: '', conflictFiles: [], headSha: `verified-${id}`, old: '', redHead: '', reach: '' }
    throw new Error(`unexpected agent label ${opts.label} in the usage-limit run`)
  }
  const lane710 = (n, owner) => ({ ...synthetic(n, `issue-${n}`, 'ready-for-agent', [], 1), owner })
  const set710 = [lane710(1001, 'integrator'), lane710(1002, 'integrator'), lane710(1003, 'ws1-infra'), lane710(1004, 'ws1-infra'), lane710(1005, 'ws2-hosting')]
  const src = fs.readFileSync(scriptPath, 'utf8').replace(/^export const meta/m, 'const meta')
  let res = null
  try { res = await new Function('return (async () => {' + src + '\n})()')() } catch (e) { fail(`#710: the usage-limit run threw: ${e && e.message ? e.message : e}`) }
  if (res) {
    if (res.halted !== LIMIT_HALTED) fail(`#710: a usage-limit death leaves halted ${JSON.stringify(res.halted)}, not ${JSON.stringify(LIMIT_HALTED)}`)
    if (res.failures !== 1) fail(`#710: the usage-limit run counts ${JSON.stringify(res.failures)} failures, not 1 (issue-1005's review alone; the death is no failure)`)
    if (JSON.stringify(res.failed) !== JSON.stringify(['issue-1005'])) fail(`#710: the usage-limit run's failed list is ${JSON.stringify(res.failed)}, not ["issue-1005"]`)
    // The tickets the limit stopped, each with the stage its agent died at or was refused at, apart from the failed.
    const stopped = Object.fromEntries((res.stopped || []).map((s) => [s.id, s.stage]))
    const wantStopped = { 'issue-1001': 'implement', 'issue-1003': 'integrate', 'issue-1004': 'review' }
    if (JSON.stringify(stopped) !== JSON.stringify(wantStopped)) fail(`#710: the usage-limit run's stopped list is ${JSON.stringify(res.stopped)}, not ${JSON.stringify(wantStopped)}`)
    if (!(res.skipped || []).includes('issue-1002')) fail(`#710: issue-1002, queued behind the death, is not skipped: ${JSON.stringify(res.skipped)}`)
    if (!res.reasons || !res.reasons.some((r) => r.id === 'issue-1001' && r.outcome === 'stopped' && r.reason.includes(LIMIT_MESSAGE))) fail(`#710: the reasons do not give issue-1001 as stopped, quoting the message: ${JSON.stringify(res.reasons)}`)
    if (!res.closingCheck || res.closingCheck.status !== 'not-run' || !res.closingCheck.why.includes(LIMIT_HALTED)) fail(`#710: the closing check after a usage-limit death is ${JSON.stringify(res.closingCheck)}, not not-run naming the limit`)
  }
  if (!started710.includes('implement:issue-1001')) fail('#710: issue-1001\'s implement never started, so the usage-limit run proved nothing')
  if (late.length) fail(`#710: agents started after the usage-limit death: ${late.join(', ')}`)
  // The report prompt lists the stopped tickets apart from the failed ones, for a run whose report can run.
  const item710 = (/^ {3}- Partial \(Outstanding\).*$/m.exec(reportPrompt) || [''])[0]
  if (!item710.includes('Stopped by the usage limit (#710') || !item710.includes('"stopped"')) fail('#710: the report prompt does not list the tickets the usage limit stopped apart from the failed ones')
}

// 35. A death that may leave a merge on the run branch says so, and a stopped grill does not read as died (#710's
//     review). After the limit no reset agent can start, so a re-run agent that dies on the limit, having put the
//     ticket's red merge back on the run branch (its STEP 1), leaves it there with no record unless the stop names it.
//     One short run: issue-1101's integrate is red on a project its change cannot reach, at about 15 ms, and its
//     re-run throws the usage-limit message at about 25 ms; issue-1102, ready-for-human, has its grill die on the limit
//     at 60 ms.
{
  globalThis.args = { ...fixture.args, skills: OTHER_SKILLS }
  globalThis.log = () => {}
  const reach = JSON.stringify({ failedAt: 'tests', verdict: TESTS_FAILED, projects: [{ project: 'tests/Spactl.Tests', reachedBy: [] }], changed: ['tools/bench/report.py'] })
  const set = [{ ...synthetic(1101, 'issue-1101', 'ready-for-agent', [], 1), owner: 'integrator' }, synthetic(1102, 'issue-1102', 'ready-for-human', [], 1)]
  globalThis.agent = async (prompt, opts) => {
    const [kind] = opts.label.split(':')
    if (kind === 'collect') return { outcome: 'ok', reason: 'companion', tickets: set.map((t) => ({ ...t })), landed: [], base: 'a'.repeat(40), shell: '3.2', shellCommand: '/bin/bash' }
    if (kind === 'resume') return NOT_MERGED   // the resume probe (#712): nothing of this run is on the run branch yet
    if (kind === 'live') return LIVE_READY   // the live read (#721): still ready-for-agent
    if (kind === 'report') return { questions: 0, mainCheckoutClean: true, leftovers: '', issue: 0, url: '' }
    if (kind === 'closing') return { ok: true, headSha: CLOSING_TIP, lastLine: 'check: pass: companion.', notes: '' }
    await sleep(kind === 'grill' ? 60 : 5)
    if (kind === 'grill' || kind === 'rerun') throw new Error(LIMIT_MESSAGE)
    if (kind === 'implement') return { outcome: 'resolved', branch: 'b', forkSha: 'f', headSha: 'h', complete: true, outstanding: '', summary: 's', question: '', delegatedDecisions: [], changeRequests: [] }
    if (kind === 'review') return { outcome: 'committed', headSha: 'h', findingsFixed: [], findingsSkipped: [], summary: 's', question: '', changeRequests: [], changedFiles: ['tools/bench/report.py'] }
    if (kind === 'integrate') return { ok: false, mergeSha: '', notes: TESTS_FAILED, conflictFiles: [], headSha: '', old: OLD, redHead: RED, reach }
    throw new Error(`unexpected agent label ${opts.label} in the usage-limit merge run`)
  }
  const src = fs.readFileSync(scriptPath, 'utf8').replace(/^export const meta/m, 'const meta')
  let res = null
  try { res = await new Function('return (async () => {' + src + '\n})()')() } catch (e) { fail(`#710: the usage-limit merge run threw: ${e && e.message ? e.message : e}`) }
  if (res) {
    const r1 = (res.reasons || []).find((r) => r.id === 'issue-1101'), r2 = (res.reasons || []).find((r) => r.id === 'issue-1102')
    if (!r1 || r1.outcome !== 'stopped' || r1.stage !== 'integrate' || !r1.reason.includes(`may hold issue-1101's unverified merge`) || !r1.reason.includes(OLD)) fail(`#710: a re-run that died on the limit does not say the run branch may hold the merge: ${JSON.stringify(r1)}`)
    if (!r2 || !r2.reason.includes('the usage limit stopped its grill')) fail(`#710: a grill the limit stopped does not say so: ${JSON.stringify(r2)}`)
  }
}

// 36. A resumed lane whose work is already on the run branch is integrated, not failed or skipped (#712). On
//     2026-11-01, after two resumes, #677's merge 02d755d was on the branch while its result read "skipped" (its lane
//     re-forked from a tip holding its own merge and saw head equal to fork), and #634's merge fd8c797 was on the
//     branch while its result read "failed" (its integrate agent had died before the gate finished). Before a lane
//     implements a ticket, the resume probe asks whether its branch is already on the run branch; a ticket whose merge
//     is there skips implement and review and goes to the gate alone, which passes on the record or runs once. One
//     short run on synthetic tickets, every agent answering at once: AS_677 is on the branch with its gate recorded as
//     passing, AS_634 is on the branch with no pass line, so its gate runs; PART is on the branch as "Merge part of
//     task"; GATE_RED's gate runs red with later merges on it; FRESH is not on the branch and is built as before; and
//     PROBE_DIED's probe dies. A mocked implement answers as the resumed lane of #677 did, head equal to fork, so a
//     script that skips the probe reads AS_677 as skipped.
{
  const AS_677 = 'issue-1201', AS_634 = 'issue-1202', PART = 'issue-1203', GATE_RED = 'issue-1204', FRESH = 'issue-1205', PROBE_DIED = 'issue-1206'
  const ON_BRANCH = [AS_677, AS_634, PART, GATE_RED]
  const BASE_712 = 'a'.repeat(40)
  const mergeOf = (id) => `${id.slice(6)}`.padEnd(40, '0')
  const RED_LINE = 'check: fail: the workflow test companion .claude/workflows/review-queue_tests.mjs failed.'
  const calls712 = []
  const prompts712 = {}
  globalThis.args = { ...fixture.args, skills: OTHER_SKILLS }
  globalThis.log = () => {}
  globalThis.agent = async (prompt, opts) => {
    const [kind, id] = opts.label.split(':')
    calls712.push(opts.label)
    prompts712[opts.label] = prompt
    if (kind === 'collect') return { outcome: 'ok', reason: 'companion', tickets: set712.map((t) => ({ ...t })), landed: [], base: BASE_712, shell: '3.2', shellCommand: '/bin/bash' }
    if (kind === 'report') return { questions: 0, mainCheckoutClean: true, leftovers: '', issue: 0, url: '' }
    if (kind === 'closing') return { ok: true, headSha: CLOSING_TIP, lastLine: 'check: pass: companion.', notes: '' }
    if (kind === 'live') return LIVE_READY
    if (kind === 'resume') {
      if (id === PROBE_DIED) return null
      if (!ON_BRANCH.includes(id)) return { merged: false, mergeSha: '', subject: '', notes: 'no commit of its own on the run branch' }
      return { merged: true, mergeSha: mergeOf(id), subject: `${id === PART ? 'Merge part of task' : 'Merge task'} ${id}: companion`, notes: '' }
    }
    // The resumed lane of #677: its STEP 0 found no "Task" commit outside the run branch, re-forked, and changed nothing.
    if (kind === 'implement') return ON_BRANCH.includes(id)
      ? { outcome: 'resolved', branch: `task/${id}`, forkSha: mergeOf(id), headSha: mergeOf(id), complete: true, outstanding: '', summary: 'changed nothing', question: '', delegatedDecisions: [], changeRequests: [] }
      : { outcome: 'resolved', branch: `task/${id}`, forkSha: 'f', headSha: 'h', complete: true, outstanding: '', summary: 's', question: '', delegatedDecisions: [], changeRequests: [] }
    if (kind === 'review') return { outcome: 'committed', headSha: 'h', findingsFixed: [], findingsSkipped: [], summary: 's', question: '', changeRequests: [], changedFiles: [`files/${id}.txt`] }
    if (kind === 'integrate') return { ok: true, mergeSha: 'm', notes: '', conflictFiles: [], headSha: `verified-${id}`, old: '', redHead: '', reach: '' }
    if (kind === 'gate') {
      if (id === GATE_RED) return { ok: false, ran: true, mergeSha: mergeOf(id), headSha: '', notes: `the merge stays on the run branch, unverified: later merges sit on it. ${RED_LINE}` }
      return { ok: true, ran: id !== AS_677, mergeSha: mergeOf(id), headSha: id === AS_677 ? mergeOf(id) : `verified-${id}`, notes: '' }
    }
    throw new Error(`unexpected agent label ${opts.label} in the resumed-lane run`)
  }
  const lane712 = (n) => ({ ...synthetic(n, `issue-${n}`, 'ready-for-agent', [], 1), owner: 'integrator' })
  const set712 = [1201, 1202, 1203, 1204, 1205, 1206].map(lane712)
  const src = fs.readFileSync(scriptPath, 'utf8').replace(/^export const meta/m, 'const meta')
  let res = null
  try { res = await new Function('return (async () => {' + src + '\n})()')() } catch (e) { fail(`#712: the resumed-lane run threw: ${e && e.message ? e.message : e}`) }
  const count = (label) => calls712.filter((l) => l === label).length
  if (res) {
    for (const id of [AS_677, AS_634]) if (!(res.integrated || []).includes(id)) fail(`#712: ${id}, whose merge was on the run branch at resume (${id === AS_677 ? "#677's case" : "#634's case"}), is not integrated: integrated ${JSON.stringify(res.integrated)}, skipped ${JSON.stringify(res.skipped)}, failed ${JSON.stringify(res.failed)}`)
    if (!(res.partial || []).includes(PART)) fail(`#712: ${PART}, on the run branch as "Merge part of task", is not partial: ${JSON.stringify(res.partial)}`)
    if (!(res.integrated || []).includes(FRESH)) fail(`#712: ${FRESH}, not on the run branch, is not built and integrated as before: ${JSON.stringify(res.integrated)}`)
    if (!(res.failed || []).includes(GATE_RED)) fail(`#712: ${GATE_RED}, whose gate went red, is not failed: ${JSON.stringify(res.failed)}`)
    const red = (res.reasons || []).find((r) => r.id === GATE_RED)
    if (!red || red.stage !== 'integrate' || !red.reason.includes(RED_LINE)) fail(`#712: the red gate's reason does not quote its last line at integrate: ${JSON.stringify(red)}`)
    // A probe that dies fails closed: the ticket is not implemented on a guess (CODING_STANDARDS.md, "A check fails closed").
    const died = (res.reasons || []).find((r) => r.id === PROBE_DIED)
    if (!(res.failed || []).includes(PROBE_DIED) || !died || !/resume probe/.test(died.reason)) fail(`#712: a resume probe that died does not fail its ticket, naming the probe: ${JSON.stringify(died)}`)
  }
  for (const id of [AS_677, AS_634, PART, GATE_RED]) {
    for (const kind of ['implement', 'review', 'integrate']) if (count(`${kind}:${id}`)) fail(`#712: ${id}'s merge was on the run branch, yet its ${kind} agent ran ${count(`${kind}:${id}`)} time(s)`)
    if (count(`gate:${id}`) !== 1) fail(`#712: ${id}'s gate ran ${count(`gate:${id}`)} time(s), not once`)
    if (count(`resume:${id}`) !== 1) fail(`#712: ${id}'s resume probe ran ${count(`resume:${id}`)} time(s), not once`)
  }
  if (count(`implement:${PROBE_DIED}`)) fail(`#712: ${PROBE_DIED}'s probe died, yet its implement ran`)
  for (const kind of ['implement', 'review', 'integrate']) if (count(`${kind}:${FRESH}`) !== 1) fail(`#712: ${FRESH}'s ${kind} ran ${count(`${kind}:${FRESH}`)} time(s), not once`)
  if (count(`gate:${FRESH}`)) fail(`#712: ${FRESH} is not on the run branch, yet the gate alone ran for it`)
  if (calls712.some((l) => l.startsWith('reset:'))) fail('#712: a red gate on a merge already on the run branch launched a reset agent')
  // The probe asks git, not the issue: the branch, the run branch, the run base and the "Task <id>:" commit it must own.
  const probe = prompts712[`resume:${AS_677}`] || ''
  // The ancestor check is named whole, branch against run branch: MERGE_OF's own "merge-base --is-ancestor" against
  // a merge sha would otherwise keep the probe's prompt passing with that check deleted (#712's review).
  for (const part of [`task/${AS_677}-${AS_677}`, 'overnight/', BASE_712, `"Task ${AS_677}:"`, `merge-base --is-ancestor task/${AS_677}-${AS_677} overnight/`]) if (!probe.includes(part)) fail(`#712: the resume probe's prompt lacks ${JSON.stringify(part)}`)
  // The gate alone judges the ticket's own merge against its first parent, and resets only a merge nothing sits on.
  // A recorded pass stands only on a head that holds the merge and that the run branch still holds (#712's review).
  const gate = prompts712[`gate:${AS_634}`] || ''
  for (const part of [mergeOf(AS_634), `${LOG}/${AS_634}-integrate.txt`, '--base <old>', '^1', 'reset nothing',
    `merge-base --is-ancestor ${mergeOf(AS_634)} <h>`, 'merge-base --is-ancestor <h> HEAD']) if (!gate.includes(part)) fail(`#712: the gate-alone prompt lacks ${JSON.stringify(part)}`)
  // The integrate step's own durable state, for an integrate agent replayed after it died mid-gate, finds the ticket's
  // own merge, not the run branch's latest (#634: its gate re-ran against #553's 1435e21).
  const integ = prompts712[`integrate:${FRESH}`] || ''
  if (integ.includes('rev-list --merges --first-parent -n 1 HEAD)^1')) fail('#712: the integrate step\'s STEP 0 still takes the run branch\'s latest merge as the ticket\'s own')
  if (!integ.includes('THE GATE ALONE')) fail('#712: the integrate step\'s STEP 0 does not hand a merge already in to the gate alone')
  // MERGE_OF reads the oldest first-parent merge since the base, `--reverse` then the first sha, never the
  // latest (#634's fix). The mocked probe returns mergeSha directly, so only the prompts' text can show the
  // flag: with `--reverse` dropped, both prompts read the newest merge and this companion stayed green (review of #737).
  for (const [label, p] of [['resume probe', probe], ['integrate STEP 0', integ]]) {
    if (!p.includes(`rev-list --first-parent --merges --reverse ${BASE_712}..`)) fail(`#712: the ${label} prompt does not read the oldest merge, rev-list --first-parent --merges --reverse <base>.., for the ticket's own merge`)
    if (!/the first sha/.test(p)) fail(`#712: the ${label} prompt does not take the first sha that rev-list prints`)
  }
}

// 37. The report's main-checkout step tells a foreground session's scratch from a file the run left (#714). On
//     2026-11-01 it listed 331 untracked files absent from untracked-at-start.txt as a finding, all under
//     .scratch/ready-human-advance-2026-11-01/ and .scratch/ready-human-recheck-2026-11-01/, which foreground
//     sessions wrote during the run, where no lane writes. The step reads a new untracked path under .scratch/ but
//     outside .scratch/overnight/ as a note naming its directories, and any other new untracked path as a finding.
{
  const NOTE_RULE = 'a new untracked path under .scratch/ but outside .scratch/overnight/ is a note, not a finding'
  const DIRS_RULE = 'list it as a note naming its directories'
  const FINDING_RULE = 'any other untracked path untracked-at-start.txt does not list is a finding: list it'
  if (!reportPrompt) fail('#714: no report prompt was rendered, so the untracked-files check read nothing')
  else {
    const step = (/^2\. Check .*$/m.exec(reportPrompt) || [''])[0]
    if (!step) fail('#714: the report prompt has no main-checkout step 2')
    for (const [rule, what] of [[NOTE_RULE, 'read a foreground session\'s scratch under .scratch/, outside .scratch/overnight/, as a note'],
      [DIRS_RULE, 'name the directories of the noted scratch files'], [FINDING_RULE, 'keep every other new untracked path a finding']]) {
      if (step && !step.includes(rule)) fail(`#714: the report prompt's main-checkout step does not ${what}`)
    }
  }
}

// 38. A resumed run reads each ticket's live labels before it dispatches implement (#721). On 2026-11-01 #544 and
//     #562 were each dispatched for implement three times, because each resume replayed collect's cached answer,
//     ready-for-agent, after an earlier launch's lane had relabelled them ready-for-human. One short run on synthetic
//     tickets, collect reading every one ready-for-agent: AS_544 is live ready-for-human and assigned, as #544 was
//     after its first lane's status note; TRIAGED_LIVE is live needs-triage; UNLABELLED carries no triage label;
//     CLOSED_LIVE closed since collect; READY is still ready-for-agent; LIVE_DIED's read dies and LIVE_UNREAD's gh
//     failed; and MERGED_LIVE's merge is already on the run branch while its labels read ready-for-human. AFTER_544
//     waits on AS_544 and AFTER_CLOSED on CLOSED_LIVE. HUMAN_OVER_AGENT still carries ready-for-agent beside
//     ready-for-human, and TRIAGE_OVER_AGENT beside needs-triage, so the precedence collect reads is held live too.
{
  const AS_544 = 'issue-1301', TRIAGED_LIVE = 'issue-1302', UNLABELLED = 'issue-1303', CLOSED_LIVE = 'issue-1304', READY = 'issue-1305'
  const LIVE_DIED = 'issue-1306', LIVE_UNREAD = 'issue-1307', MERGED_LIVE = 'issue-1308', AFTER_544 = 'issue-1309', AFTER_CLOSED = 'issue-1310'
  const HUMAN_OVER_AGENT = 'issue-1311', TRIAGE_OVER_AGENT = 'issue-1312'
  const RELABELLED = [AS_544, TRIAGED_LIVE, UNLABELLED, HUMAN_OVER_AGENT, TRIAGE_OVER_AGENT]
  const liveOf = {
    [AS_544]: { state: 'OPEN', labels: ['ready-for-human', 'change-request'], assignees: ['adammedford'], notes: '' },
    [TRIAGED_LIVE]: { state: 'OPEN', labels: ['needs-triage'], assignees: [], notes: '' },
    [UNLABELLED]: { state: 'OPEN', labels: ['kind:ticket'], assignees: [], notes: '' },
    [CLOSED_LIVE]: { state: 'CLOSED', labels: ['ready-for-agent'], assignees: [], notes: '' },
    [LIVE_DIED]: null,
    [LIVE_UNREAD]: { state: 'UNREAD', labels: [], assignees: [], notes: 'error connecting to api.github.com' },
    [MERGED_LIVE]: { state: 'OPEN', labels: ['ready-for-human'], assignees: [], notes: '' },
    [HUMAN_OVER_AGENT]: { state: 'OPEN', labels: ['ready-for-agent', 'ready-for-human'], assignees: [], notes: '' },
    [TRIAGE_OVER_AGENT]: { state: 'OPEN', labels: ['ready-for-agent', 'needs-triage'], assignees: [], notes: '' },
  }
  const calls721 = []
  const prompts721 = {}
  let report721 = ''
  globalThis.args = { ...fixture.args, skills: OTHER_SKILLS }
  globalThis.log = () => {}
  globalThis.agent = async (prompt, opts) => {
    const [kind, id] = opts.label.split(':')
    calls721.push(opts.label)
    prompts721[opts.label] = prompt
    if (kind === 'collect') return { outcome: 'ok', reason: 'companion', tickets: set721.map((t) => ({ ...t })), landed: [], base: 'a'.repeat(40), shell: '3.2', shellCommand: '/bin/bash' }
    if (kind === 'report') { report721 = prompt; return { questions: 0, mainCheckoutClean: true, leftovers: '', issue: 0, url: '' } }
    if (kind === 'closing') return { ok: true, headSha: CLOSING_TIP, lastLine: 'check: pass: companion.', notes: '' }
    if (kind === 'resume') return id === MERGED_LIVE ? { merged: true, mergeSha: '8'.repeat(40), subject: `Merge task ${id}: companion`, notes: '' } : NOT_MERGED
    if (kind === 'live') return id in liveOf ? liveOf[id] : LIVE_READY
    if (kind === 'grill') return { questions: [], changeRequests: [], adrCandidates: [], draftDecision: '', decided: '', summary: CARRIED_GRILL_SUMMARY }
    if (kind === 'gate') return { ok: true, ran: false, mergeSha: '8'.repeat(40), headSha: '8'.repeat(40), notes: '' }
    if (kind === 'implement') return { outcome: 'resolved', branch: `task/${id}`, forkSha: 'f', headSha: 'h', complete: true, outstanding: '', summary: 's', question: '', delegatedDecisions: [], changeRequests: [] }
    if (kind === 'review') return { outcome: 'committed', headSha: 'h', findingsFixed: [], findingsSkipped: [], summary: 's', question: '', changeRequests: [], changedFiles: [`files/${id}.txt`] }
    if (kind === 'integrate') return { ok: true, mergeSha: 'm', notes: '', conflictFiles: [], headSha: `verified-${id}`, old: '', redHead: '', reach: '' }
    throw new Error(`unexpected agent label ${opts.label} in the live-label run`)
  }
  const CARRIED_GRILL_SUMMARY = 'carried from an earlier night, not re-grilled'
  const lane721 = (n, blockedBy = []) => ({ ...synthetic(n, `issue-${n}`, 'ready-for-agent', blockedBy, 1), owner: 'integrator' })
  const set721 = [lane721(1301), lane721(1302), lane721(1303), lane721(1304), lane721(1305), lane721(1306), lane721(1307), lane721(1308),
    lane721(1309, [AS_544]), lane721(1310, [CLOSED_LIVE]), lane721(1311), lane721(1312)]
  const src = fs.readFileSync(scriptPath, 'utf8').replace(/^export const meta/m, 'const meta')
  let res = null
  try { res = await new Function('return (async () => {' + src + '\n})()')() } catch (e) { fail(`#721: the live-label run threw: ${e && e.message ? e.message : e}`) }
  const count = (label) => calls721.filter((l) => l === label).length
  const reasonOf = (id) => ((res && res.reasons) || []).find((r) => r.id === id)
  for (const id of [...RELABELLED, CLOSED_LIVE, LIVE_DIED, LIVE_UNREAD, AFTER_544]) {
    if (count(`implement:${id}`)) fail(`#721: ${id}, whose live read did not say ready-for-agent, was implemented ${count(`implement:${id}`)} time(s)`)
  }
  for (const id of [READY, AFTER_CLOSED]) {
    for (const kind of ['live', 'implement', 'review', 'integrate']) if (count(`${kind}:${id}`) !== 1) fail(`#721: ${id}, still ready-for-agent, ran its ${kind} ${count(`${kind}:${id}`)} time(s), not once`)
    if (calls721.indexOf(`live:${id}`) > calls721.indexOf(`implement:${id}`)) fail(`#721: ${id}'s live read ran after its implement`)
  }
  // The probe comes first: a merge already on the run branch keeps its gate, whatever its labels say now.
  if (count(`live:${MERGED_LIVE}`)) fail(`#721: ${MERGED_LIVE}'s merge was on the run branch, yet its live read ran`)
  if (count(`gate:${MERGED_LIVE}`) !== 1) fail(`#721: ${MERGED_LIVE}'s merge was on the run branch, yet its gate ran ${count(`gate:${MERGED_LIVE}`)} time(s), not once`)
  if (res) {
    for (const id of [READY, AFTER_CLOSED, MERGED_LIVE]) if (!(res.integrated || []).includes(id)) fail(`#721: ${id} is not integrated: ${JSON.stringify(res.integrated)}`)
    // #544's case: not built, reported as the human ticket it now is, its reason naming the live label, and its grill
    // run with no context, so its STEP 0 carries the record an earlier launch wrote.
    if (!(res.needsHuman || []).includes(AS_544)) fail(`#721: ${AS_544}, live ready-for-human, is not among needsHuman: ${JSON.stringify(res.needsHuman)}`)
    const h = reasonOf(AS_544)
    if (!h || h.outcome !== 'needs-human' || h.stage !== 'implement' || !h.reason.includes('ready-for-human') || !h.reason.includes('relabelled since collect')) fail(`#721: ${AS_544}'s reason does not say it was relabelled ready-for-human at implement: ${JSON.stringify(h)}`)
    if (count(`grill:${AS_544}`) !== 1) fail(`#721: ${AS_544}'s grill ran ${count(`grill:${AS_544}`)} time(s), not once`)
    if (/Context from tonight/.test(prompts721[`grill:${AS_544}`] || '')) fail(`#721: ${AS_544}'s grill was given context, so its STEP 0 would grill again rather than carry the earlier record`)
    // ready-for-human outranks a ready-for-agent label left beside it, as in collect's precedence.
    const ho = reasonOf(HUMAN_OVER_AGENT)
    if (!(res.needsHuman || []).includes(HUMAN_OVER_AGENT) || !ho || ho.outcome !== 'needs-human' || ho.stage !== 'implement') fail(`#721: ${HUMAN_OVER_AGENT}, live ready-for-human beside ready-for-agent, is not a human ticket at implement: ${JSON.stringify(ho)}`)
    for (const [id, label] of [[TRIAGED_LIVE, 'needs-triage'], [UNLABELLED, 'kind:ticket'], [TRIAGE_OVER_AGENT, 'needs-triage']]) {
      const r = reasonOf(id)
      if (!(res.skipped || []).includes(id) || !r || r.stage !== 'implement' || !r.reason.includes(label)) fail(`#721: ${id}, live ${label}, is not skipped at implement naming its live labels: ${JSON.stringify(r)}`)
      if (count(`grill:${id}`)) fail(`#721: ${id}, live ${label}, was grilled`)
    }
    // Nothing of a relabelled ticket landed, so a ticket waiting on it is not built; a closed one counts as met (#108).
    if (!(res.skipped || []).includes(AFTER_544) || !/blocker not integrated/.test((reasonOf(AFTER_544) || {}).reason || '')) fail(`#721: ${AFTER_544}, waiting on the relabelled ${AS_544}, is not skipped for its blocker: ${JSON.stringify(reasonOf(AFTER_544))}`)
    const c = reasonOf(CLOSED_LIVE)
    if (!(res.skipped || []).includes(CLOSED_LIVE) || !c || !c.reason.includes('closed before the lane started')) fail(`#721: ${CLOSED_LIVE}, closed since collect, is not skipped as closed: ${JSON.stringify(c)}`)
    // A read that died or gave no state fails closed, naming the read.
    for (const id of [LIVE_DIED, LIVE_UNREAD]) {
      const r = reasonOf(id)
      if (!(res.failed || []).includes(id) || !r || !/live read of its labels/.test(r.reason)) fail(`#721: ${id}, whose live read ${id === LIVE_DIED ? 'died' : 'gave no state'}, does not fail naming the read: ${JSON.stringify(r)}`)
    }
    // The report reads each ticket's reconciled status, not the cached one.
    const sum = JSON.parse((/^Run results \(JSON\): (.*)$/m.exec(report721) || [, 'null'])[1])
    const st = (id) => ((sum && sum.tickets) || []).find((t) => t.id === id)
    for (const [id, status] of [[AS_544, 'ready-for-human'], [TRIAGED_LIVE, 'needs-triage'], [UNLABELLED, 'unlabelled'],
      [HUMAN_OVER_AGENT, 'ready-for-human'], [TRIAGE_OVER_AGENT, 'needs-triage']]) {
      if (!st(id) || st(id).status !== status) fail(`#721: the report reads ${id}'s status as ${JSON.stringify(st(id) && st(id).status)}, not its live ${status}`)
    }
  }
  // The read is one gh read of the issue, and writes nothing.
  const lp = prompts721[`live:${READY}`] || ''
  for (const part of [`gh issue view ${READY.slice(6)} --json state,labels,assignees`, 'no gh write', 'write no file']) if (!lp.includes(part)) fail(`#721: the live read's prompt lacks ${JSON.stringify(part)}`)
  if (/gh issue (edit|comment|create|close)/.test(lp)) fail('#721: the live read\'s prompt names a gh write')
}

// 39. The implementer breaks what it adds (#633). On 2026-10-29 reviewers made nine guards across eight tickets able
//     to fail after each lane's check had passed them, because only the review prompt asked for a mutation. Every
//     implement prompt asks, in STEP 1 before its gate, for each guard, filter and assertion the lane adds to be broken
//     one at a time against its own test, restored from a copy taken before the break (never by `git checkout --`,
//     which resets the file to forkSha while nothing is committed and discards the guard; #797's review), and named
//     with its failure count in the Task commit's body. The position bounds fail closed when either anchor is missing.
{
  const impls = allPrompts.filter((x) => x.label.startsWith('implement:'))
  if (!impls.length) fail('#633: no implement prompt was rendered, so the mutation check read nothing')
  for (const c of impls) {
    const wt = (/^Worktree: (\S+?)\.? Branch:/m.exec(c.prompt) || [])[1]
    if (!wt) { fail(`#633: the ${c.label} prompt names no worktree`); continue }
    const wants = [
      '- Every guard, filter and assertion you add must be able to fail (CODING_STANDARDS.md, "An assertion must be able to fail"): break each one on purpose, one at a time, run only its test or companion, see it fail,',
      `see it fail, and restore it before the next: copy the file aside before you break it (\`cp ${wt}/<path> ${LOG}/${c.label.slice('implement:'.length)}-mutation.bak\`) and copy it back after (\`cp ${LOG}/${c.label.slice('implement:'.length)}-mutation.bak ${wt}/<path>\`), which returns exactly what you wrote.`,
      'Name each mutation and its failure count in the Task commit\'s body ("A new invariant test names the mutation that made it fail").',
    ]
    for (const w of wants) if (!c.prompt.includes(w)) fail(`#633: the ${c.label} prompt lacks: ${w.slice(0, 80)}...`)
    if (/checkout -- /.test(c.prompt.replace('Never restore with `git checkout -- <path>`', ''))) fail(`#633: the ${c.label} prompt restores a mutation with \`checkout --\`, which resets the file to the last commit and discards the lane's uncommitted guard`)
    const at = c.prompt.indexOf(wants[0])
    const lo = c.prompt.indexOf('- Build test-first: ')
    const hi = c.prompt.indexOf('- At the end, run the gate once:')
    if (lo < 0 || hi < 0) fail(`#633: the ${c.label} prompt lacks the '- Build test-first: ' or the '- At the end, run the gate once:' line, so the mutation bullet's place in STEP 1 cannot be checked`)
    else if (at >= 0 && !(at > lo && at < hi)) fail(`#633: the ${c.label} prompt's mutation bullet is not in STEP 1 before its gate`)
  }
}

// 40. No lane calls docker absent before it has looked in ~/.docker/bin (#742). On 2026-11-02 the lanes for #731 and
//     #732 were skipped as needing an absent docker, while Docker Desktop 29.9.1 sat in ~/.docker/bin, which an agent's
//     non-login bash PATH leaves out. Every house-rules prompt carries the rule verbatim: put that directory on PATH in
//     the same Bash call as the docker command, and report docker absent only when neither place has it.
{
  const RULE = '- Docker Desktop installs `docker` in `~/.docker/bin`, which your non-login Bash PATH leaves out: begin every Bash call that runs docker with `export PATH="$HOME/.docker/bin:$PATH" &&`, since the shell\'s state resets after every call, and call docker absent only when neither `command -v docker` nor `~/.docker/bin/docker` finds it, as tools/image-check.sh looks. On 2026-11-02 the lanes for #731 and #732 were skipped for a docker that was installed there (#742).'
  const housed = allPrompts.filter((c) => /House rules \(/.test(c.prompt))
  if (!housed.length) fail('#742: no prompt carries the house rules, so the docker-path rule search read nothing')
  for (const c of housed) if (!c.prompt.includes(RULE)) fail(`#742: the ${c.label} prompt's house rules do not send docker to ~/.docker/bin before calling it absent`)
}

// 41. Gap decisions are read from the Answer (#631): the implement prompt records a gap decision only in the Answer's
//     "Decided by implementer" list, and a "## Decisions taken" comment only for a human-owned one. So every review
//     prompt's decisions step reads that list and finds no fault in the missing comment, and the report takes a gap
//     decision's alternatives from the Answer, which is among the comments it reads past the results JSON. The list's
//     label is read from each implement prompt, never copied here, so a label renamed there alone fails this check
//     (#914; CODING_STANDARDS.md "A guard reads the thing it guards").
{
  const impls = allPrompts.filter((x) => x.label.startsWith('implement:'))
  if (!impls.length) fail('#914: no implement prompt was rendered, so the gap-decision label was read from nothing')
  const labels = new Set()
  for (const c of impls) {
    const m = /^Gap decisions that are not human-owned you may take; list each in delegatedDecisions and in the Answer as "([^"]+)"\.$/m.exec(c.prompt)
    if (!m) fail(`#914: the ${c.label} prompt does not name the Answer's list a gap decision goes in`)
    else labels.add(m[1])
  }
  if (labels.size > 1) fail(`#914: the implement prompts name ${labels.size} labels for the Answer's gap-decision list: ${[...labels].join(', ')}`)
  const LABEL = labels.size === 1 ? [...labels][0] : ''
  if (LABEL) {
    const REVIEW = `Then read the "## Decisions taken" comment, if any, and the Answer's "${LABEL}" list, where a gap decision is recorded (no gap decision gets a "## Decisions taken" comment, so its absence is no finding).`
    const REPORT = `(a "Human-owned:" one from the issue's "## Decisions taken" comment, a gap decision from the "${LABEL}" list in its "## Answer" comment)`
    const READS = `the "## Decisions taken" alternatives, the "${LABEL}" list in an "## Answer" comment`
    const reviews = allPrompts.filter((x) => x.label.startsWith('review:'))
    if (!reviews.length) fail('#631: no review prompt was captured, so the gap-decision check read nothing')
    for (const c of reviews) if (!c.prompt.includes(REVIEW)) fail(`#631: the ${c.label} prompt's decisions step does not read the Answer's "${LABEL}" list`)
    if (!reportPrompt) fail('#631: no report prompt was rendered, so the gap-decision check read nothing')
    else {
      const item = (/^   - Decisions taken overnight:.*$/m.exec(reportPrompt) || [''])[0]
      if (!item.includes(REPORT)) fail(`#631: the report's "Decisions taken overnight" item does not take a gap decision's alternatives from the Answer's "${LABEL}" list`)
      if (!reportPrompt.includes(READS)) fail(`#631: the report does not read the Answer's "${LABEL}" list among the comments the results JSON lacks`)
    }
  }
}

// 42. Every implement forks from the run branch's last verified tip, or the run base before any gate has passed (#665;
//     #659 Q2): never the branch's tip as read, which may be a merge still under its gate, which a red gate resets
//     away. The main run's mocked integrates are in progress between their start and end, and only an integrate that
//     answered green has a verified head, so each implement prompt's fork is the last green head before it started.
const RUN_BASE_665 = 'a'.repeat(40)
const forkOf = (prompt) => (/Otherwise fork from [^:]+: forkSha = (\S+?)\. Take that sha, never/.exec(prompt) || [])[1]
{
  const implStarts = events.filter((e) => e.kind === 'implement' && e.edge === 'start')
  let duringMerge = 0, beforeGreen = 0
  for (const s of implStarts) {
    const c = allPrompts.find((x) => x.label === `implement:${s.id}`)
    const fork = c && forkOf(c.prompt)
    if (!fork) { fail(`#665: the implement prompt of ${s.id} names no fork sha`); continue }
    if (c.prompt.includes(`forkSha = \`git -C ${fixture.args.repo} rev-parse overnight/`)) fail(`#665: the implement prompt of ${s.id} still forks from the branch's tip as read`)
    const before = greens.filter((g) => g.at < s.at), same = greens.filter((g) => g.at === s.at).map((g) => g.head)
    const want = before.length ? before[before.length - 1].head : RUN_BASE_665
    if (fork !== want && !same.includes(fork)) fail(`#665: ${s.id} forks from ${fork}, not ${want}, the last verified tip when it started`)
    if (!before.length) beforeGreen++
    // An integrate running when this implement started: its head is not yet verified, so it is never the fork.
    const running = events.filter((e) => e.kind === 'integrate' && e.edge === 'start' && e.at < s.at
      && !events.some((x) => x.kind === 'integrate' && x.edge === 'end' && x.n === e.n && x.at <= s.at))
    if (running.length) duringMerge++
    for (const r of running) if (fork === `verified-${r.id}`) fail(`#665: ${s.id} forks from ${fork}, the head of a merge still under its gate`)
  }
  if (!duringMerge) fail('#665: no implement started while a merge was under its gate, so the in-progress check read nothing')
  if (!beforeGreen) fail('#665: no implement started before any gate passed, so the run-base fallback check read nothing')
}

// 43. Path waits (#665; #659 Q1, Q4 to Q10): a run of its own, on tickets whose Touches paths overlap. A ticket waits
//     to start its implement until every earlier ticket by n, in any lane, whose paths are equal to or a directory
//     holding its own has settled with any outcome; a textual prefix alone is no overlap; a ticket with no readable
//     path waits on every ticket ahead of it in its lane, saying why (here the lane's order is n's; check 44 has
//     one where it is not); a wait that would close a cycle with a blocked-by edge
//     is dropped; each wait is logged in the confirmed form and reported with its paths and elapsed minutes; and a
//     conflict before any gate has passed back-merges the run base.
{
  const T = (n, owner, touches, blockedBy = []) => ({ n, id: `issue-${1000 + n}`, slug: `p${n}`, issue: 1000 + n, url: `https://example.invalid/${1000 + n}`,
    title: `Path ${n}`, status: 'ready-for-agent', owner, blockedBy, why: 'companion', openBoxes: 1, changeRequest: false, willEndPartial: [], touches })
  const id = (n) => `issue-${1000 + n}`
  const set = [
    T(1, 'integrator', ['tools/shared.py']),                    // A: fails at implement
    T(2, 'ws6-verification', ['tools/shared.py']),              // B: equal path, another lane
    T(3, 'ws2-hosting', ['tools/']),                            // C: a directory holding A's and B's path
    T(5, 'ws5-fixtures', ['fixtures/Mock']),                    // E
    T(6, 'ws3-identity', ['fixtures/MockApi/Program.cs']),      // D: a textual prefix of E's, no overlap
    T(7, 'ws4-proxy', ['fixtures/Mock*']),                      // G: a glob segment holding E's and D's
    T(8, 'integrator', ['docs/x.md'], [id(10)]),                // H: starts late, behind its blocker J
    T(9, 'ws6-verification', ['docs']),                         // I: later by n, so it waits on H, though H starts after it could
    T(10, 'ws1-infra', ['infra/a.tf']),                         // J: slow
    T(11, 'integrator', []),                                    // M: no readable path
    T(12, 'integrator', ['other/n.txt']),                       // N: readable, behind M in its lane
    T(13, 'ws2-hosting', ['cyc/a'], [id(14)]),                  // K1: needs K2
    // K2 has K1 downstream, so a lane would take it first (#662): it sits in J's lane, behind J by n, rather than ahead
    // of B in B's lane, where it would hold B until A had settled and B would have nothing to wait on.
    T(14, 'ws1-infra', ['cyc/a']),                              // K2: a path wait on K1 would close a cycle
    T(15, 'ws6-verification', ['zz/z'], [id(1)]),               // Z: a real blocker on A, which fails
  ]
  const pById = Object.fromEntries(set.map((t) => [t.id, t]))
  const ev = [], logged = [], prompts = [], clocks = {}, clockSeq = {}
  const pGreens = []
  let k = 0, seq = 0   // seq orders the agent calls, where two in one tick tie
  globalThis.args = { ...fixture.args, skills: OTHER_SKILLS, only: null, skip: [], maxParallel: 6, maxFailures: 8 }
  globalThis.log = (text) => logged.push({ at: tick, text })
  globalThis.agent = async (prompt, opts) => {
    prompts.push({ label: opts.label, prompt })
    const [kind, tid, edge] = opts.label.split(':')
    if (kind === 'collect') return { outcome: 'ok', reason: 'companion', tickets: set.map((t) => ({ ...t })), landed: [], base: RUN_BASE_665, shell: '3.2', shellCommand: '/bin/bash' }
    if (kind === 'resume') return NOT_MERGED   // the resume probe (#712): nothing of this run is on the run branch yet
    if (kind === 'live') return LIVE_READY   // the live read (#721): still ready-for-agent
    if (kind === 'clock') { clocks[`${tid}:${edge}`] = tick; clockSeq[`${tid}:${edge}`] = ++seq; return { epoch: tick * 6 } }   // one tick reads as a tenth of a minute
    if (kind === 'report') return { questions: 0, mainCheckoutClean: true, leftovers: '', issue: 0, url: '' }
    if (kind === 'closing') return { ok: true, headSha: 'd'.repeat(40), lastLine: 'check: pass: companion', notes: '' }
    const n = ++k
    ev.push({ at: tick, kind, id: tid, edge: 'start', n, seq: ++seq })
    await sleep(kind === 'implement' ? (tid === id(10) ? 150 : 25) : 15)
    ev.push({ at: tick, kind, id: tid, edge: 'end', n, seq: ++seq })
    if (kind === 'implement' && tid === id(1)) return { outcome: 'failed', branch: 'b', forkSha: 'f', headSha: 'f', complete: false, outstanding: '', summary: 'failed on purpose', question: '', delegatedDecisions: [], changeRequests: [] }
    if (kind === 'implement') return { outcome: 'resolved', branch: 'b', forkSha: 'f', headSha: 'h', complete: true, outstanding: '', summary: 's', question: '', delegatedDecisions: [], changeRequests: [] }
    if (kind === 'review') return { outcome: 'committed', headSha: 'h', findingsFixed: [], findingsSkipped: [], summary: 's', question: '', changeRequests: [], changedFiles: [`files/${tid}.txt`] }
    if (kind === 'integrate') { pGreens.push({ at: tick, head: `verified-${tid}` }); return { ok: true, mergeSha: 'm', notes: '', conflictFiles: [], headSha: `verified-${tid}`, old: '', redHead: '', reach: '' } }
    throw new Error(`unexpected agent label ${opts.label} in the path-wait run`)
  }
  const src = fs.readFileSync(scriptPath, 'utf8').replace(/^export const meta/m, 'const meta')
  // The main run's tick stopped with it; this run keeps time of its own.
  const pathTick = setInterval(() => { tick++ }, 1)
  let stop
  const r = await Promise.race([new Function('return (async () => {' + src + '\n})()')(), new Promise((res) => { stop = setTimeout(() => res(STALLED), LIMIT_MS) })])
  clearTimeout(stop)
  clearInterval(pathTick)
  if (r === STALLED) fail('#665: the path-wait run did not finish: a wait never released, or two waits formed a cycle')
  else {
    const startOf = (x) => (ev.find((e) => e.kind === 'implement' && e.id === x && e.edge === 'start') || {}).at
    const settledAt = (x) => Math.max(...ev.filter((e) => e.id === x && e.edge === 'end').map((e) => e.at))
    const waitLine = (w, b, paths) => `${id(w)}: waiting to start behind ${id(b)} (${paths})`
    const hasLine = (s) => logged.some((l) => l.text === s)
    const waitsBehind = (w, b) => logged.some((l) => l.text.startsWith(`${id(w)}: waiting to start behind ${id(b)} (`))
    const after = (w, b, why) => { const s = startOf(id(w)); if (s === undefined || s < settledAt(id(b))) fail(`#665: ${id(w)} started at ${s}, before ${id(b)} settled at ${settledAt(id(b))} (${why})`) }
    // Equal paths, across lanes, behind a predecessor that fails: the waiter is released and integrates.
    if (!hasLine(waitLine(2, 1, 'tools/shared.py'))) fail(`#665: no "${waitLine(2, 1, 'tools/shared.py')}" line: equal paths did not wait`)
    after(2, 1, 'equal paths')
    // Directory containment on whole segments.
    if (!hasLine(waitLine(3, 1, 'tools, tools/shared.py')) || !hasLine(waitLine(3, 2, 'tools, tools/shared.py'))) fail('#665: a directory holding an earlier ticket\'s path did not wait on it: ' + JSON.stringify(logged.filter((l) => l.text.startsWith(id(3))).map((l) => l.text)))
    after(3, 2, 'directory containment')
    // A textual prefix alone is no overlap.
    if (waitsBehind(6, 5)) fail(`#665: ${id(6)} (fixtures/MockApi/Program.cs) waited behind ${id(5)} (fixtures/Mock), a textual prefix only`)
    if ((r.pathWaits || []).some((w) => w.id === id(6))) fail(`#665: the run reports a path wait for ${id(6)}, whose paths overlap no earlier ticket's`)
    // A glob segment holds what it matches.
    if (!waitsBehind(7, 5) || !waitsBehind(7, 6)) fail(`#665: ${id(7)}'s fixtures/Mock* did not wait on the paths it matches`)
    // Strictly backwards by n: I waits on H, which has the lower n though it starts later; H never waits on I.
    if (!hasLine(waitLine(9, 8, 'docs, docs/x.md'))) fail(`#665: ${id(9)} did not wait on ${id(8)}, the earlier ticket by n`)
    if (waitsBehind(8, 9)) fail(`#665: ${id(8)} waited on ${id(9)}, a later ticket by n`)
    after(9, 8, 'backwards by n'); after(8, 10, 'its blocker')
    // No readable path: every ticket ahead of it in its lane (by n here), and none of another lane's, with the reason.
    const reason = `every path: ${id(11)} has no readable Touches path (no Touches line, or one naming no path)`
    // Not behind issue-1001: it failed and settled before the lane was free to take issue-1011, so that wait cost nothing.
    if (!hasLine(waitLine(11, 8, reason)) || waitsBehind(11, 1)) fail(`#665: ${id(11)}, with no readable path, did not wait on every earlier ticket of its lane naming why: ` + JSON.stringify(logged.filter((l) => l.text.startsWith(id(11))).map((l) => l.text)))
    if (waitsBehind(11, 2) || waitsBehind(11, 3)) fail(`#665: ${id(11)}, with no readable path, waited on a ticket of another lane`)
    if (!hasLine(waitLine(12, 11, reason))) fail(`#665: ${id(12)} did not wait on ${id(11)}, the earlier ticket of its lane with no readable path`)
    after(11, 8, 'no readable path'); after(12, 11, 'behind no readable path')
    // A path wait that would close a cycle with a blocked-by edge is dropped and said so.
    if (waitsBehind(14, 13)) fail(`#665: ${id(14)} waits on ${id(13)}, which needs it by a blocked-by edge`)
    if (!logged.some((l) => l.text.startsWith(`${id(14)}: no path wait behind ${id(13)} (cyc/a): `))) fail(`#665: the dropped wait of ${id(14)} behind ${id(13)} is not logged`)
    // The wait orders two tickets, the edge means one needs the other: a failed predecessor releases its waiters,
    // and a real blocker on it still skips.
    const want = [2, 3, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14].map(id)
    for (const x of want) if (!(r.integrated || []).includes(x)) fail(`#665: ${x} did not integrate: ${JSON.stringify((r.reasons || []).find((y) => y.id === x))}`)
    if (!(r.failed || []).includes(id(1))) fail(`#665: ${id(1)} is not reported failed`)
    const z = (r.reasons || []).find((y) => y.id === id(15))
    if (!(r.skipped || []).includes(id(15)) || !z || !z.reason.includes(`blocker not integrated: ${id(1)}`)) fail(`#665: ${id(15)}, blocked by the failed ${id(1)}, is not skipped for it: ${JSON.stringify(z)}`)
    if (startOf(id(15)) !== undefined) fail(`#665: ${id(15)} started although its blocker failed`)
    // Each wait is reported with its paths and its elapsed minutes, read from the clock agents at its start and end.
    const waits = Object.fromEntries((r.pathWaits || []).map((w) => [w.id, w]))
    for (const x of [2, 3, 7, 9, 11, 12].map(id)) {
      const w = waits[x]
      if (!w) { fail(`#665: the run's pathWaits hold no entry for ${x}`); continue }
      const s = clocks[`${x}:start`], e = clocks[`${x}:end`]
      if (s === undefined || e === undefined) { fail(`#665: ${x}'s wait was not clocked at its start and end`); continue }
      if (w.minutes !== (e - s) / 10) fail(`#665: ${x}'s wait reads ${w.minutes} minutes, not ${(e - s) / 10}, the clocks' difference`)
      if (!(e > s)) fail(`#665: ${x}'s wait was clocked to end at ${e}, not after its start at ${s}`)
      if (e < Math.max(...w.behind.map((b) => settledAt(b.id)))) fail(`#665: ${x}'s wait was clocked to end at ${e}, before its last predecessor settled`)
      if (startOf(x) !== undefined && startOf(x) < e) fail(`#665: ${x} started at ${startOf(x)}, before its wait ended at ${e}`)
      if (w.behind.some((b) => !Array.isArray(b.paths) || !b.paths.length || !b.issue)) fail(`#665: ${x}'s wait does not give each predecessor's issue and paths: ${JSON.stringify(w.behind)}`)
      // The minutes are time the path wait cost (#659 Q10, Q11), so a wait starts when its lane is free to take the
      // ticket, never while the lane is still implementing another of its tickets, which it would wait out anyway.
      const q = clockSeq[`${x}:start`]
      const busy = ev.filter((b) => b.kind === 'implement' && b.edge === 'start' && b.id !== x && pById[b.id] && pById[b.id].owner === pById[x].owner)
        .find((b) => { const end = ev.find((y) => y.kind === 'implement' && y.edge === 'end' && y.n === b.n); return b.seq < q && (!end || q < end.seq) })
      if (busy) fail(`#665: ${x}'s wait was clocked to start at ${s}, while its lane was still implementing ${busy.id}, so its minutes hold time it could not have started in`)
      // An earlier ticket of its lane with no blocker is taken first in any case, so the wait starts after its implement.
      for (const y of set.filter((o) => o.owner === pById[x].owner && o.n < pById[x].n && !o.blockedBy.length)) {
        const end = ev.find((e2) => e2.kind === 'implement' && e2.edge === 'end' && e2.id === y.id)
        if (end && q < end.seq) fail(`#665: ${x}'s wait was clocked to start before ${y.id}, earlier in its lane, finished its implement, so its minutes hold time it could not have started in`)
      }
    }
    const rep = prompts.find((x) => x.label === 'report')
    const sum = rep ? JSON.parse((/^Run results \(JSON\): (.*)$/m.exec(rep.prompt) || [, 'null'])[1]) : null
    if (!sum || JSON.stringify(sum.pathWaits) !== JSON.stringify(r.pathWaits)) fail('#665: the report\'s results do not carry the run\'s pathWaits')
    const item = rep ? (rep.prompt.split('\n').find((l) => l.includes('- Path waits (#665)')) || '') : ''
    for (const rule of ['`### Path waits`', 'the paths the entry gives', 'elapsed minutes', '"not measured"', 'It claims no conflict saved']) if (!item.includes(rule)) fail(`#665: the report prompt's Path waits item lacks "${rule}"`)
    // Forks in this run too: the last green head before each implement started, or the run base.
    for (const s of ev.filter((e) => e.kind === 'implement' && e.edge === 'start')) {
      const fork = forkOf((prompts.find((x) => x.label === `implement:${s.id}`) || {}).prompt || '')
      const before = pGreens.filter((g) => g.at < s.at), same = pGreens.filter((g) => g.at === s.at).map((g) => g.head)
      const want2 = before.length ? before[before.length - 1].head : RUN_BASE_665
      if (fork !== want2 && !same.includes(fork)) fail(`#665: in the path-wait run ${s.id} forks from ${fork}, not ${want2}`)
    }
  }
  // Collect copies the tool's paths and reads no Touches line itself.
  for (const c of allPrompts.filter((x) => x.label === 'collect')) {
    if (!c.prompt.includes('Give each ticket in "tickets" the "touches" list of the tool\'s ticket with the same issue number, copied as printed (#665)')) fail('#665: the collect prompt does not copy each ticket\'s touches from tools/overnight_collect.py')
    if (!c.prompt.includes('never read a Touches line yourself')) fail('#665: the collect prompt lets the agent read a Touches line')
  }
  // A conflict before any integration gate has passed back-merges the run base, never the branch's tip as read.
  {
    const solo = T(1, 'integrator', ['solo/a'])
    const seen = []
    let merged = 0
    globalThis.args = { ...fixture.args, skills: OTHER_SKILLS, only: null, skip: [] }
    globalThis.log = () => {}
    globalThis.agent = async (prompt, opts) => {
      seen.push({ label: opts.label, prompt })
      const [kind] = opts.label.split(':')
      if (kind === 'collect') return { outcome: 'ok', reason: 'companion', tickets: [{ ...solo }], landed: [], base: RUN_BASE_665, shell: '3.2', shellCommand: '/bin/bash' }
      if (kind === 'resume') return NOT_MERGED
      if (kind === 'live') return LIVE_READY
      if (kind === 'implement') return { outcome: 'resolved', branch: 'b', forkSha: RUN_BASE_665, headSha: 'h', complete: true, outstanding: '', summary: 's', question: '', delegatedDecisions: [], changeRequests: [] }
      if (kind === 'review') return { outcome: 'committed', headSha: 'h', findingsFixed: [], findingsSkipped: [], summary: 's', question: '', changeRequests: [], changedFiles: ['solo/a'] }
      if (kind === 'integrate') return ++merged === 1 ? { ok: false, mergeSha: '', notes: 'conflict', conflictFiles: ['solo/a'], headSha: '', old: '', redHead: '', reach: '' }
        : { ok: true, mergeSha: 'm', notes: '', conflictFiles: [], headSha: 'v'.repeat(40), old: '', redHead: '', reach: '' }
      if (kind === 'resolve') return { outcome: 'resolved', headSha: 'h2', tip: RUN_BASE_665, notes: 'companion' }
      if (kind === 'closing') return { ok: true, headSha: 'v'.repeat(40), lastLine: 'check: pass: companion', notes: '' }
      if (kind === 'report') return { questions: 0, mainCheckoutClean: true, leftovers: '', issue: 0, url: '' }
      throw new Error(`unexpected agent label ${opts.label} in the resolve-fallback run`)
    }
    await new Function('return (async () => {' + src + '\n})()')()
    const res = (seen.find((x) => x.label === `resolve:${solo.id}`) || {}).prompt || ''
    if (!res) fail('#665: the resolve-fallback run sent no ticket back to its lane, so the fallback check read nothing')
    else {
      if (!res.includes(`tip=${RUN_BASE_665}, the run base`) || !res.includes(`merge --no-ff ${RUN_BASE_665}\``)) fail('#665: a conflict before any gate passed does not back-merge the run base')
      if (res.includes(`tip=\`git -C ${fixture.args.repo} rev-parse`)) fail('#665: a conflict before any gate passed still reads the branch\'s tip as its back-merge')
    }
  }
}

// 44. A ticket with no readable Touches path waits on the tickets ahead of it in its lane's run order, downstream first
//     and then by n, not on every lower-n ticket of its lane (#706 Q1 (b)). One lane, the integrator's: P (n 1) has
//     a path and nothing downstream; M (n 2) has no readable path and D downstream, so the lane takes M first; X (n 4)
//     has no path and nothing downstream, so it comes after P by n; Q (n 5) has a path and comes last. D sits in another
//     lane. M waits on nothing; P waits on M while M is in review and integrating (the predecessor held in flight); X
//     waits on M and P, and its logged wait names P alone, since M has settled by then and a settled one costs no
//     wait; Q waits on X. P's end clock dies, so its wait is reported unmeasured: minutes null, never zero (#659 Q10).
{
  const T = (n, owner, touches, blockedBy = []) => ({ n, id: `issue-${2000 + n}`, slug: `q${n}`, issue: 2000 + n, url: `https://example.invalid/${2000 + n}`,
    title: `Lane order ${n}`, status: 'ready-for-agent', owner, blockedBy, why: 'companion', openBoxes: 1, changeRequest: false, willEndPartial: [], touches })
  const id = (n) => `issue-${2000 + n}`
  const set = [
    T(1, 'integrator', ['p/a.txt']),            // P: a path, nothing downstream
    T(2, 'integrator', []),                     // M: no readable path, D downstream
    T(3, 'ws6-verification', ['d/d.txt'], [id(2)]),   // D: needs M
    T(4, 'integrator', []),                     // X: no readable path, nothing downstream
    T(5, 'integrator', ['q/q.txt']),            // Q: a path, after X
  ]
  const ev = [], logged = [], prompts = []
  globalThis.args = { ...fixture.args, skills: OTHER_SKILLS, only: null, skip: [], maxParallel: 6, maxFailures: 8 }
  globalThis.log = (text) => logged.push({ at: tick, text })
  globalThis.agent = async (prompt, opts) => {
    prompts.push({ label: opts.label, prompt })
    const [kind, tid, edge] = opts.label.split(':')
    if (kind === 'collect') return { outcome: 'ok', reason: 'companion', tickets: set.map((t) => ({ ...t })), landed: [], base: RUN_BASE_665, shell: '3.2', shellCommand: '/bin/bash' }
    if (kind === 'resume') return NOT_MERGED
    if (kind === 'live') return LIVE_READY
    // P's end clock dies, as an agent that returns nothing does.
    if (kind === 'clock') return tid === id(1) && edge === 'end' ? null : { epoch: tick * 6 }
    if (kind === 'report') return { questions: 0, mainCheckoutClean: true, leftovers: '', issue: 0, url: '' }
    if (kind === 'closing') return { ok: true, headSha: 'd'.repeat(40), lastLine: 'check: pass: companion', notes: '' }
    ev.push({ at: tick, kind, id: tid, edge: 'start' })
    await sleep(kind === 'implement' ? 20 : 40)   // review and integrate outlast an implement, so a ticket stays in flight
    ev.push({ at: tick, kind, id: tid, edge: 'end' })
    if (kind === 'implement') return { outcome: 'resolved', branch: 'b', forkSha: 'f', headSha: 'h', complete: true, outstanding: '', summary: 's', question: '', delegatedDecisions: [], changeRequests: [] }
    if (kind === 'review') return { outcome: 'committed', headSha: 'h', findingsFixed: [], findingsSkipped: [], summary: 's', question: '', changeRequests: [], changedFiles: [`files/${tid}.txt`] }
    if (kind === 'integrate') return { ok: true, mergeSha: 'm', notes: '', conflictFiles: [], headSha: `verified-${tid}`, old: '', redHead: '', reach: '' }
    throw new Error(`unexpected agent label ${opts.label} in the lane-order path-wait run`)
  }
  const src = fs.readFileSync(scriptPath, 'utf8').replace(/^export const meta/m, 'const meta')
  const orderTick = setInterval(() => { tick++ }, 1)
  let stop
  const r = await Promise.race([new Function('return (async () => {' + src + '\n})()')(), new Promise((res) => { stop = setTimeout(() => res(STALLED), LIMIT_MS) })])
  clearTimeout(stop)
  clearInterval(orderTick)
  if (r === STALLED) fail('#706 Q1 (b): the lane-order path-wait run did not finish: a wait never released, or two waits formed a cycle')
  else {
    const startOf = (x) => (ev.find((e) => e.kind === 'implement' && e.id === x && e.edge === 'start') || {}).at
    const settledAt = (x) => Math.max(...ev.filter((e) => e.id === x && e.edge === 'end').map((e) => e.at))
    const waitsBehind = (w, b) => logged.some((l) => l.text.startsWith(`${id(w)}: waiting to start behind ${id(b)} (`))
    const reason = (x) => `every path: ${id(x)} has no readable Touches path (no Touches line, or one naming no path)`
    const waitLine = (w, b, why) => `${id(w)}: waiting to start behind ${id(b)} (${why})`
    const hasLine = (s) => logged.some((l) => l.text === s)
    const waits = Object.fromEntries((r.pathWaits || []).map((w) => [w.id, w]))
    for (const x of [1, 2, 3, 4, 5].map(id)) if (!(r.integrated || []).includes(x)) fail(`#706 Q1 (b): ${x} did not integrate: ${JSON.stringify((r.reasons || []).find((y) => y.id === x))}`)
    // M runs first: with no readable path it waits on nothing ahead of it in its lane's order, P included.
    if (waitsBehind(2, 1) || waits[id(2)]) fail(`#706 Q1 (b): ${id(2)}, with no readable path and the most downstream, waited behind ${id(1)}, a lower-n ticket behind it in its lane's order`)
    if (!(startOf(id(2)) < startOf(id(1)))) fail(`#706 Q1 (b): ${id(2)} started at ${startOf(id(2))}, not before ${id(1)} at ${startOf(id(1))}: the lane did not take its downstream-first ticket first`)
    // P waits on M, held in flight: logged with M's reason, and P starts only once M has settled.
    if (!hasLine(waitLine(1, 2, reason(2))) || !waits[id(1)]) fail(`#706 Q1 (b): ${id(1)} did not wait behind ${id(2)}, the ticket ahead of it in its lane with no readable path: ` + JSON.stringify(logged.filter((l) => l.text.startsWith(id(1))).map((l) => l.text)))
    if (!(startOf(id(1)) >= settledAt(id(2)))) fail(`#706 Q1 (b): ${id(1)} started at ${startOf(id(1))}, before ${id(2)} settled at ${settledAt(id(2))}`)
    // Then by n: X waits on P, which has the lower n and no more downstream, and the settled M costs it no logged wait.
    if (!hasLine(waitLine(4, 1, reason(4)))) fail(`#706 Q1 (b): ${id(4)}, with no readable path, did not wait behind ${id(1)}, ahead of it in its lane by n`)
    if (waits[id(4)] && waits[id(4)].behind.some((b) => b.id === id(2))) fail(`#706 Q1 (b): ${id(4)}'s wait names ${id(2)}, which had settled before its lane was free to take ${id(4)}`)
    if (!(startOf(id(4)) >= settledAt(id(1))) || !(startOf(id(4)) >= settledAt(id(2)))) fail(`#706 Q1 (b): ${id(4)} started at ${startOf(id(4))}, before a ticket ahead of it in its lane settled`)
    if (!(startOf(id(5)) >= settledAt(id(4)))) fail(`#706 Q1 (b): ${id(5)} started at ${startOf(id(5))}, before ${id(4)}, ahead of it with no readable path, settled at ${settledAt(id(4))}`)
    // A clock that dies leaves the wait unmeasured, never zero, and the log says "not measured".
    const p = waits[id(1)]
    if (p && (p.minutes !== null || p.endEpoch !== null)) fail(`#659 Q10: ${id(1)}'s end clock died, but its wait reads ${p.minutes} minutes (endEpoch ${p.endEpoch}), not null`)
    if (p && !logged.some((l) => l.text.startsWith('path waits: ') && l.text.includes(`\n  ${id(1)} behind ${id(2)}: not measured`))) fail(`#659 Q10: the path waits log line does not say ${id(1)}'s wait is not measured: ` + JSON.stringify((logged.find((l) => l.text.startsWith('path waits: ')) || {}).text))
    const rep = prompts.find((x) => x.label === 'report')
    const sum = rep ? JSON.parse((/^Run results \(JSON\): (.*)$/m.exec(rep.prompt) || [, 'null'])[1]) : null
    const sw = sum && (sum.pathWaits || []).find((w) => w.id === id(1))
    if (p && (!sw || sw.minutes !== null)) fail(`#659 Q10: the report's results do not carry ${id(1)}'s unmeasured wait as minutes null: ${JSON.stringify(sw)}`)
  }
}

// 45. A path wait starts only once the ticket's blocked-by edges have settled (#665; #659 Q10, Q11). The lane is not
//     free to take a ticket that a blocker still holds, so a wait clocked then would hold time the blocker cost, not
//     the path. Three lanes: B (n 1) is quick; P (n 2) is slow and touches w/a.txt; W (n 3) needs B and touches
//     w/a.txt, so it path-waits on P, the earlier ticket by n. W sits alone in its lane, so it is its lane's first
//     ticket from the start. Its wait must be logged and clocked to start after B has settled, while P still holds it,
//     and W must start after P has settled, with its wait reported behind P alone.
{
  const T = (n, owner, touches, blockedBy = []) => ({ n, id: `issue-${3000 + n}`, slug: `w${n}`, issue: 3000 + n, url: `https://example.invalid/${3000 + n}`,
    title: `Blocked wait ${n}`, status: 'ready-for-agent', owner, blockedBy, why: 'companion', openBoxes: 1, changeRequest: false, willEndPartial: [], touches })
  const id = (n) => `issue-${3000 + n}`
  const set = [
    T(1, 'ws1-infra', ['b/b.txt']),                    // B: quick, W's blocker
    T(2, 'ws2-hosting', ['w/a.txt']),                  // P: slow, earlier than W by n, the same path
    T(3, 'integrator', ['w/a.txt'], [id(1)]),          // W: needs B, path-waits on P
  ]
  const ev = [], logged = [], prompts = [], clockSeq = {}
  let seq = 0   // orders the agent calls and log lines, where two in one tick tie
  globalThis.args = { ...fixture.args, skills: OTHER_SKILLS, only: null, skip: [], maxParallel: 6, maxFailures: 8 }
  globalThis.log = (text) => logged.push({ at: tick, seq: ++seq, text })
  globalThis.agent = async (prompt, opts) => {
    prompts.push({ label: opts.label, prompt })
    const [kind, tid, edge] = opts.label.split(':')
    if (kind === 'collect') return { outcome: 'ok', reason: 'companion', tickets: set.map((t) => ({ ...t })), landed: [], base: RUN_BASE_665, shell: '3.2', shellCommand: '/bin/bash' }
    if (kind === 'resume') return NOT_MERGED
    if (kind === 'live') return LIVE_READY
    if (kind === 'clock') { clockSeq[`${tid}:${edge}`] = ++seq; return { epoch: tick * 6 } }
    if (kind === 'report') return { questions: 0, mainCheckoutClean: true, leftovers: '', issue: 0, url: '' }
    if (kind === 'closing') return { ok: true, headSha: 'd'.repeat(40), lastLine: 'check: pass: companion', notes: '' }
    ev.push({ kind, id: tid, edge: 'start', seq: ++seq })
    await sleep(kind === 'implement' && tid === id(2) ? 150 : 15)
    ev.push({ kind, id: tid, edge: 'end', seq: ++seq })
    if (kind === 'implement') return { outcome: 'resolved', branch: 'b', forkSha: 'f', headSha: 'h', complete: true, outstanding: '', summary: 's', question: '', delegatedDecisions: [], changeRequests: [] }
    if (kind === 'review') return { outcome: 'committed', headSha: 'h', findingsFixed: [], findingsSkipped: [], summary: 's', question: '', changeRequests: [], changedFiles: [`files/${tid}.txt`] }
    if (kind === 'integrate') return { ok: true, mergeSha: 'm', notes: '', conflictFiles: [], headSha: `verified-${tid}`, old: '', redHead: '', reach: '' }
    throw new Error(`unexpected agent label ${opts.label} in the blocked path-wait run`)
  }
  const src = fs.readFileSync(scriptPath, 'utf8').replace(/^export const meta/m, 'const meta')
  const blockedTick = setInterval(() => { tick++ }, 1)
  let stop
  const r = await Promise.race([new Function('return (async () => {' + src + '\n})()')(), new Promise((res) => { stop = setTimeout(() => res(STALLED), LIMIT_MS) })])
  clearTimeout(stop)
  clearInterval(blockedTick)
  if (r === STALLED) fail('#665: the blocked path-wait run did not finish: a wait never released')
  else {
    const settledSeq = (x) => Math.max(...ev.filter((e) => e.id === x && e.edge === 'end').map((e) => e.seq))
    const startSeq = (x) => (ev.find((e) => e.kind === 'implement' && e.id === x && e.edge === 'start') || {}).seq
    for (const x of [1, 2, 3].map(id)) if (!(r.integrated || []).includes(x)) fail(`#665: ${x} did not integrate in the blocked path-wait run: ${JSON.stringify((r.reasons || []).find((y) => y.id === x))}`)
    const line = `${id(3)}: waiting to start behind ${id(2)} (w/a.txt)`
    const said = logged.filter((l) => l.text.startsWith(`${id(3)}: waiting to start behind `))
    if (!said.some((l) => l.text === line)) fail(`#665: ${id(3)} started with no "${line}" line: ` + JSON.stringify(said.map((l) => l.text)))
    for (const l of said) if (l.seq < settledSeq(id(1))) fail(`#665: ${id(3)}'s path wait was logged ("${l.text}") while its blocker ${id(1)} still held it`)
    const s = clockSeq[`${id(3)}:start`], e = clockSeq[`${id(3)}:end`]
    if (s === undefined || e === undefined) fail(`#665: ${id(3)}'s path wait was not clocked at its start and end`)
    else {
      if (s < settledSeq(id(1))) fail(`#665: ${id(3)}'s path wait was clocked to start while its blocker ${id(1)} still held it, so its minutes hold time the blocker cost`)
      if (s > settledSeq(id(2))) fail(`#665: ${id(3)}'s path wait was clocked to start after ${id(2)}, the predecessor it waited on, had settled`)
      if (e < settledSeq(id(2))) fail(`#665: ${id(3)}'s path wait was clocked to end before ${id(2)} settled`)
    }
    if (!(startSeq(id(3)) > settledSeq(id(2)))) fail(`#665: ${id(3)} started its implement before ${id(2)}, the predecessor its paths overlap, settled`)
    const w = (r.pathWaits || []).find((x) => x.id === id(3))
    if (!w || w.behind.map((b) => b.id).join(',') !== id(2) || typeof w.minutes !== 'number') fail(`#665: the run does not report ${id(3)}'s wait behind ${id(2)} alone, with its minutes: ${JSON.stringify(w)}`)
  }
}

// 46. The cycle guard on a path wait that points forwards in n (#916; #706 Q1 (b)). A ticket with no readable Touches
//     path is ahead by its lane's run order, so its waits may point forwards in n, and the guard must follow them, and
//     path waits as well as blocked-by edges, whichever way the closing wait points. Two runs, each with one cycle the
//     guard must break, or the run stalls. Run A, one lane: X (n 1) and Y (n 3) touch w/x.txt; A (n 2) has no
//     readable path; Y has two tickets downstream and A one, so the lane runs Y, A, X. X waits on A and A on Y, both
//     forwards in n, so Y's wait on X, earlier by n with the same path, would close a cycle of path waits alone and is
//     dropped. Run B: P (n 3) has a path and M (n 4) none in one lane, M ahead with two downstream to P's one; M needs
//     A (n 2), A path-waits on B (n 1) by x/a.txt, and B needs P. P's wait on M, forwards in n, would close the cycle
//     M, A, B, P, through a path wait, and is dropped. Each run asserts the dropped wait and that every ticket
//     integrates; that the other waits hold their tickets is checks 43 and 44's to prove.
{
  const run = async (set, label) => {
    const ev = [], logged = []
    globalThis.args = { ...fixture.args, skills: OTHER_SKILLS, only: null, skip: [], maxParallel: 6, maxFailures: 8 }
    globalThis.log = (text) => logged.push({ at: tick, text })
    globalThis.agent = async (prompt, opts) => {
      const [kind, tid] = opts.label.split(':')
      if (kind === 'collect') return { outcome: 'ok', reason: 'companion', tickets: set.map((t) => ({ ...t })), landed: [], base: RUN_BASE_665, shell: '3.2', shellCommand: '/bin/bash' }
      if (kind === 'resume') return NOT_MERGED
      if (kind === 'live') return LIVE_READY
      if (kind === 'clock') return { epoch: tick * 6 }
      if (kind === 'report') return { questions: 0, mainCheckoutClean: true, leftovers: '', issue: 0, url: '' }
      if (kind === 'closing') return { ok: true, headSha: 'd'.repeat(40), lastLine: 'check: pass: companion', notes: '' }
      ev.push({ at: tick, kind, id: tid, edge: 'start' })
      await sleep(15)
      ev.push({ at: tick, kind, id: tid, edge: 'end' })
      if (kind === 'implement') return { outcome: 'resolved', branch: 'b', forkSha: 'f', headSha: 'h', complete: true, outstanding: '', summary: 's', question: '', delegatedDecisions: [], changeRequests: [] }
      if (kind === 'review') return { outcome: 'committed', headSha: 'h', findingsFixed: [], findingsSkipped: [], summary: 's', question: '', changeRequests: [], changedFiles: [`files/${tid}.txt`] }
      if (kind === 'integrate') return { ok: true, mergeSha: 'm', notes: '', conflictFiles: [], headSha: `verified-${tid}`, old: '', redHead: '', reach: '' }
      throw new Error(`unexpected agent label ${opts.label} in the forward cycle run ${label}`)
    }
    const src = fs.readFileSync(scriptPath, 'utf8').replace(/^export const meta/m, 'const meta')
    const cycleTick = setInterval(() => { tick++ }, 1)
    let stop
    const r = await Promise.race([new Function('return (async () => {' + src + '\n})()')(), new Promise((res) => { stop = setTimeout(() => res(STALLED), LIMIT_MS) })])
    clearTimeout(stop)
    clearInterval(cycleTick)
    return { r, ev, logged }
  }
  const T = (base, n, owner, touches, blockedBy = []) => ({ n, id: `issue-${base + n}`, slug: `c${base + n}`, issue: base + n, url: `https://example.invalid/${base + n}`,
    title: `Forward cycle ${base + n}`, status: 'ready-for-agent', owner, blockedBy, why: 'companion', openBoxes: 1, changeRequest: false, willEndPartial: [], touches })
  const noPath = (x) => `every path: ${x} has no readable Touches path (no Touches line, or one naming no path)`
  const dropLine = (w, b, paths) => `${w}: no path wait behind ${b} (${paths}): ${b} already waits on ${w}, so the wait would close a cycle`
  const cases = [
    { label: 'A', base: 4000, set: (id) => [
      T(4000, 1, 'integrator', ['w/x.txt']),                       // X: earlier by n, last in its lane
      T(4000, 2, 'integrator', []),                                // A: no readable path, one downstream
      T(4000, 3, 'integrator', ['w/x.txt']),                       // Y: two downstream, first in its lane
      T(4000, 4, 'ws6-verification', ['e/e.txt'], [id(2)]),        // E: needs A
      T(4000, 5, 'ws1-infra', ['d/1.txt'], [id(3)]),               // D1: needs Y
      T(4000, 6, 'ws1-infra', ['d/2.txt'], [id(3)]),               // D2: needs Y
    ], drop: (id) => dropLine(id(3), id(1), 'w/x.txt') },
    { label: 'B', base: 5000, set: (id) => [
      T(5000, 1, 'ws1-infra', ['x/a.txt'], [id(3)]),               // B: needs P
      T(5000, 2, 'ws2-hosting', ['x/a.txt']),                      // A: path-waits on B
      T(5000, 3, 'integrator', ['p/p.txt']),                       // P: a path, one downstream (B)
      T(5000, 4, 'integrator', [], [id(2)]),                       // M: no readable path, needs A, two downstream
      T(5000, 5, 'ws6-verification', ['d/1.txt'], [id(4)]),        // D1: needs M
      T(5000, 6, 'ws6-verification', ['d/2.txt'], [id(4)]),        // D2: needs M
    ], drop: (id) => dropLine(id(3), id(4), noPath(id(4))) },
  ]
  for (const c of cases) {
    const id = (n) => `issue-${c.base + n}`
    const { r, logged } = await run(c.set(id), c.label)
    if (r === STALLED) { fail(`#916: forward cycle run ${c.label} did not finish: the cycle guard let a path wait close a cycle through a wait that points forwards in n`); continue }
    for (const x of [1, 2, 3, 4, 5, 6].map(id)) if (!(r.integrated || []).includes(x)) fail(`#916: ${x} did not integrate in forward cycle run ${c.label}: ${JSON.stringify((r.reasons || []).find((y) => y.id === x))}`)
    if (!logged.some((l) => l.text === c.drop(id))) fail(`#916: forward cycle run ${c.label} did not log "${c.drop(id)}": ` + JSON.stringify(logged.filter((l) => l.text.includes('no path wait behind')).map((l) => l.text)))
  }
}

if (failures.length) {
  console.log(`overnight_tests: FAILED, ${failures.length} check(s), on ${path.basename(scriptPath)}:`)
  failures.forEach((f) => console.log(`  - ${f}`))
  process.exit(1)
}
console.log(`overnight_tests: pass: ${got.size} integrated, ${fixture.expect.skippedForBlocker.length} skipped for a blocker, ${fixture.expect.startsBefore.length} ordering checks, ${DOWNSTREAM_CASES} downstream-order runs, on ${path.basename(scriptPath)}`)
