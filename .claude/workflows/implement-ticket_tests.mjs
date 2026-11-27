// The implement-ticket script's test companion. Owner: integrator. Tracked beside the script (#269),
// named for it as pins-sweep_tests.mjs is for its own.
//
//   node .claude/workflows/implement-ticket_tests.mjs [script]
//
// Runs implement-ticket.js whole, as pins-sweep_tests.mjs runs its own, with agent, parallel, log and
// phase stubbed. Every agent is answered by its label: the Plan agent with a proposal, the build agent
// with overnight.js's implement result, and each courier (start, check:<n>, release) with a record
// the tool wrote, served from fixtures/implement-ticket.json, which tools/implement_ticket_tests.py
// holds to the tool's records' keys, so a record's shape cannot change in one layer alone. Every
// stub's return is checked against the schema the script passed. No agent, git, gh or tool runs.
// Exits 0 when every check holds, 1 when one fails (each failure printed), 2 when the script cannot
// run. Its last line is its verdict.
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const scriptPath = process.argv[2] || path.join(here, 'implement-ticket.js')
const FIXTURE = JSON.parse(fs.readFileSync(path.join(here, 'fixtures', 'implement-ticket.json'), 'utf8'))
const RUN = FIXTURE.run
// The first launch the tool printed for the fixture's ticket: { repo, issue, wt, port }.
const FIRST = RUN.read.launch.args
const ISSUE = FIRST.issue
const CONFIRMED = {
  seams: [{ name: 'the command line', interface: 'thing.py run as a subprocess: its stdout and exit code', boxes: [1] }],
  noTest: [{ box: 2, why: 'the companion is itself the test' }],
}

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

// ---------------------------------------------------------------- what the stubs serve
const PROPOSAL = {
  seams: [{ name: 'the command line', interface: 'thing.py run as a subprocess', catches: 'a wrong name printed', misses: 'a slow start', boxes: [1] }],
  tests: [{ box: 1, seam: 'the command line', test: 'thing.py prints its name and exits 0' }],
  noTest: [{ box: 2, why: 'the companion is itself the test' }],
  integratorPaths: [], questions: [], summary: 'One seam, the command line. Box 2 is the companion.',
}
const courierReturn = (record, extra = {}) => ({
  record, verdictLine: `implement-ticket: ${record.step}: ${record.verdict}: ${record.why}`, exit: record.exit, ...extra,
})
const BUILT = {
  outcome: 'resolved', branch: RUN.startNew.branch, forkSha: RUN.startNew.fork, headSha: RUN.checkPass.head,
  complete: true, outstanding: '', summary: 'Built the thing at the confirmed seam.', question: '',
  delegatedDecisions: [], changeRequests: [], changedFiles: ['tools/thing.py', 'tools/thing_tests.py', 'docs/thing.md'], lastLine: '',
  tests: [{ box: 1, test: 'test_thing_prints_its_name', project: 'tools/thing_tests.py' }], seams: [],
}

// served: { plan, start, build, checks: [record names or returns], release }
function stub(served) {
  const calls = []
  const schemaFailures = []
  let checks = 0
  globalThis.phase = () => {}
  globalThis.log = () => {}
  globalThis.parallel = (fns) => Promise.all(fns.map((f) => f().catch(() => null)))
  globalThis.agent = async (prompt, opts) => {
    const label = opts.label
    calls.push({ label, prompt, opts })
    let out
    if (label === 'plan') out = 'plan' in served ? served.plan : PROPOSAL
    else if (label === 'start') out = 'start' in served ? served.start : courierReturn(RUN.startNew)
    else if (label === 'build') out = 'build' in served ? served.build : BUILT
    else if (label.startsWith('check:')) {
      const list = served.checks || ['checkPass']
      const item = list[Math.min(checks++, list.length - 1)]
      out = typeof item === 'string' ? courierReturn(RUN[item]) : item
    } else if (label === 'release') out = 'release' in served ? served.release : courierReturn(RUN.release)
    else throw new Error(`no stub for the agent labelled ${label}`)
    if (out !== null) for (const f of validate(opts.schema, out)) schemaFailures.push(`${label}: ${f}`)
    return out
  }
  return { calls, schemaFailures }
}

const returns = []
async function execute(scriptArgs, served = {}) {
  const { calls, schemaFailures } = stub(served)
  globalThis.args = scriptArgs
  const src = fs.readFileSync(scriptPath, 'utf8').replace(/^export const meta/m, 'const meta')
  const result = await new Function('return (async () => {' + src + '\n})()')()
  returns.push({ args: scriptArgs, result })
  for (const f of schemaFailures) fail(`a stub's return does not match the script's schema: ${f}`)
  return { result, calls, labels: calls.map((c) => c.label), prompt: (l) => (calls.find((c) => c.label === l) || {}).prompt || '' }
}
const loadMeta = () => {
  const src = fs.readFileSync(scriptPath, 'utf8')
  const m = /^export const meta = ([\s\S]*?\n})\n/m.exec(src)
  return m ? new Function(`return (${m[1]})`)() : null
}
const dataLine = (prompt, marker) => {
  const m = new RegExp(`^${marker}: (.*)$`, 'm').exec(prompt)
  return m ? JSON.parse(m[1]) : undefined
}
// The commands a prompt asks an agent to run: its backtick spans.
const commands = (prompt) => [...prompt.replace(/^[A-Z]+: .*$/gm, '').matchAll(/`([^`\n]+)`/g)].map((m) => m[1])
const GH_WRITE = /\bgh\s+(issue\s+(create|edit|comment|close|reopen|delete)|label|api\b[^`]*\s(-X|--method)\b|pr\s+(create|edit|comment|merge|close))|\bgit\b.*\b(commit|push|add|checkout|switch|worktree add)\b/
const second = (seams = CONFIRMED) => ({ ...FIRST, seams })
const same = (a, b) => JSON.stringify(a) === JSON.stringify(b)

const failures = []
const fail = (msg) => failures.push(msg)
let checks = 0

try {
  // 1. meta names implement-ticket and its four phases; the known arguments are #1565's; a bad launch
  //    is refused with next and no agent started.
  checks++
  {
    const meta = loadMeta()
    if (!meta || meta.name !== 'implement-ticket') fail(`meta.name is ${meta && meta.name}, expected implement-ticket`)
    const titles = ((meta && meta.phases) || []).map((p) => p.title)
    if (titles.join() !== 'Plan,Start,Build,Check') fail(`meta phases ${titles.join(', ')}`)
    const src = fs.readFileSync(scriptPath, 'utf8')
    const known = (/^const KNOWN_ARGS = (\[.*\])$/m.exec(src) || [])[1]
    if (known !== "['repo', 'issue', 'wt', 'port', 'seams', 'skills']") fail(`KNOWN_ARGS is ${known}`)
    if (!src.includes('Test companion: `implement-ticket_tests.mjs` beside this file')) fail('the header does not name its test companion')
    for (const [label, a] of [
      ['an unknown argument', { ...FIRST, dryRun: true }],
      ['no issue', { repo: FIRST.repo, wt: FIRST.wt }],
      ['an issue as a string', { ...FIRST, issue: String(ISSUE) }],
      ['an issue of zero', { ...FIRST, issue: 0 }],
      ['no wt', { repo: FIRST.repo, issue: ISSUE }],
      ['a relative wt', { ...FIRST, wt: 'worktrees/issue-7' }],
      ['a wt inside the repository', { ...FIRST, wt: `${FIRST.repo}/wt` }],
      ['a relative repo', { ...FIRST, repo: 'checkout' }],
      ['a port below 1024', { ...FIRST, port: 80 }],
      ['a port as a string', { ...FIRST, port: '5180' }],
      ['a relative skills', { ...FIRST, skills: 'skills' }],
      ['seams as another word', second('everything')],
      ['seams as a bare list', second(CONFIRMED.seams)],
      ['a seam with no interface', second({ seams: [{ name: 'x', boxes: [1] }], noTest: [] })],
      ['a seam with a box as a string', second({ seams: [{ name: 'x', interface: 'y', boxes: ['1'] }] })],
      ['a noTest box with no why', second({ seams: [], noTest: [{ box: 1 }] })],
      ['an empty seams object', second({ seams: [], noTest: [] })],
    ]) {
      const { result, calls } = await execute(a)
      if (!result || !/^bad launch; no agent started/.test(result.halted || '') || calls.length) fail(`${label}: ${calls.length} agent(s) ran; returned ${JSON.stringify(result).slice(0, 200)}`)
    }
    const named = await execute({ ...FIRST, dryRun: true, port: 80 })
    if (!/dryRun: not an argument/.test(named.result.halted) || !/port: an integer/.test(named.result.halted)) fail(`a bad launch does not name each bad argument: ${named.result.halted}`)
    for (const a of [FIRST, second(), second('implementer'), { ...FIRST, skills: '/skills', port: 5191 }, { issue: ISSUE, wt: '/elsewhere/wt' }]) {
      const { result } = await execute(a)
      if (result && result.halted) fail(`a good launch was refused: ${JSON.stringify(a)}: ${result.halted}`)
    }
  }

  // 2. With no seams, the Plan agent runs alone and the proposal comes back with next naming the tool's
  //    launch; a proposal naming an integrator path returns the change-request stop.
  checks++
  {
    const x = await execute(FIRST)
    if (x.labels.join() !== 'plan') fail(`the first launch ran ${x.labels.join(', ')}, expected the Plan agent alone`)
    if (x.result.stop !== 'seams' || !same(x.result.proposal, PROPOSAL)) fail(`the first launch's return: ${JSON.stringify(x.result).slice(0, 300)}`)
    if (!x.result.next.includes(`python3 -I tools/implement_ticket.py launch ${ISSUE} --seams `)) fail(`the first launch's next names no launch command: ${x.result.next}`)
    if (!x.prompt('plan').includes(`${FIRST.repo}/.scratch/implement/log/${ISSUE}/read.json`) || !/number them from 1/.test(x.prompt('plan'))) fail('the Plan prompt does not take the boxes, numbered from 1, from read\'s record')
    for (const c of commands(x.prompt('plan'))) if (GH_WRITE.test(c)) fail(`the Plan prompt asks for a write: ${c}`)
    const asked = await execute(FIRST, { plan: { ...PROPOSAL, questions: ['Which clock does the thing read?'] } })
    if (!asked.result.next.includes('Which clock does the thing read?')) fail(`a human-owned question is not in next: ${asked.result.next}`)
    const cr = await execute(FIRST, { plan: { ...PROPOSAL, integratorPaths: [{ path: 'contracts/thing.md', why: 'a new field' }] } })
    if (cr.labels.join() !== 'plan' || cr.result.stop !== 'change-request') fail(`an integrator path: ran ${cr.labels.join(', ')}, returned ${cr.result.stop}`)
    if (!cr.result.next.includes('contracts/thing.md') || !cr.result.next.includes('"Change requests"') || /implement_ticket\.py launch/.test(cr.result.next)) fail(`the change-request stop's next: ${cr.result.next}`)
    const dead = await execute(FIRST, { plan: null })
    if (dead.result.stop !== 'plan-failed' || dead.labels.join() !== 'plan') fail(`a Plan agent that returned nothing: ${JSON.stringify(dead.result)}`)
  }

  // 3. With seams, no Plan agent runs; the confirmed seams reach the build prompt unchanged; under
  //    implementer the build agent is told to choose and list its own.
  checks++
  {
    const x = await execute(second())
    if (x.labels.join() !== 'start,build,check:1') fail(`the second launch ran ${x.labels.join(', ')}, expected start, build, check:1`)
    const build = x.prompt('build')
    if (!same(dataLine(build, 'SEAMS'), CONFIRMED)) fail(`the build prompt's seams are not the confirmed ones: ${dataLine(build, 'SEAMS')}`)
    if (!same(dataLine(build, 'BOXES'), RUN.startNew.boxes)) fail('the build prompt does not carry the open boxes from start\'s record')
    if (/choose them yourself/.test(build)) fail('a build at confirmed seams is told to choose its own')
    if (!/at exactly the seams on the SEAMS line/.test(build)) fail('the build prompt does not hold the build to the confirmed seams')
    if (!build.includes(`Ticket: #${ISSUE}`) || !build.includes(`Worktree: ${RUN.startNew.worktree}`) || !build.includes('implement/SKILL.md') || !/Do not run its code-review step/.test(build)) fail('the build prompt lacks the Ticket line, the worktree, the skill or the no-review change')
    if (!/Never edit, commit, switch, stash or check out in the main checkout/.test(build)) fail('the build prompt does not keep the agent out of the main checkout')
    const skills = await execute({ ...second(), skills: '/opt/skills/' })
    if (!skills.prompt('build').includes('/opt/skills/implement/SKILL.md')) fail('the skills argument does not reach the build prompt')
    const own = await execute(second('implementer'))
    const ownBuild = own.prompt('build')
    if (dataLine(ownBuild, 'SEAMS') !== 'implementer' || !/choose them yourself/.test(ownBuild) || !/list each in your return's seams/.test(ownBuild)) fail('under implementer the build agent is not told to choose and list its own seams')
    if (own.labels.includes('plan')) fail('under implementer a Plan agent ran')
  }

  // 4. Couriers: the 600000 ms timeout and low effort; finished skips Build; running is asked again; a
  //    pass on the build's head is done; a pass on another head is not.
  checks++
  {
    const x = await execute(second(), { checks: ['checkRunning', 'checkRunning', 'checkPass'] })
    if (x.labels.join() !== 'start,build,check:1,check:2,check:3') fail(`a running check: ran ${x.labels.join(', ')}`)
    if (x.result.done !== true || x.result.head !== RUN.checkPass.head) fail(`a pass on the build's head after two running records is not done: ${JSON.stringify(x.result).slice(0, 300)}`)
    if (!/review-queue/.test(x.result.next) || !x.result.next.includes(`land ${ISSUE}`)) fail(`done's next: ${x.result.next}`)
    const red = await execute(second(), { checks: ['checkFail'] })
    for (const c of [...x.calls, ...red.calls].filter((c) => ['start', 'release'].includes(c.label) || c.label.startsWith('check:'))) {
      if (!c.prompt.includes('timeout of 600000 ms')) fail(`the ${c.label} courier's prompt does not name the 600000 ms timeout`)
      if (c.opts.effort !== 'low') fail(`the ${c.label} courier runs at ${c.opts.effort} effort, not low`)
      if (!/Return the record exactly as printed/.test(c.prompt)) fail(`the ${c.label} courier is not told to return the record unchanged`)
    }
    const fin = await execute(second(), { start: courierReturn(RUN.startFinished) })
    if (fin.labels.join() !== 'start,check:1' || fin.result.done !== true) fail(`a finished start: ran ${fin.labels.join(', ')}, returned ${JSON.stringify(fin.result).slice(0, 200)}`)
    const other = await execute(second(), { build: { ...BUILT, headSha: 'f'.repeat(40) } })
    if (other.result.done || other.result.stop !== 'failed' || !other.labels.includes('release')) fail(`a pass on another head: ${JSON.stringify(other.result).slice(0, 300)}`)
    const finOther = await execute(second(), { start: courierReturn({ ...RUN.startFinished, head: 'e'.repeat(40) }) })
    if (finOther.result.done) fail('a finished start whose head is not the check\'s ended as done')
    const unexited = await execute(second(), { checks: [courierReturn(RUN.checkPass, { exit: 2 })] })
    if (unexited.result.done) fail('a pass record with a non-zero exit ended as done')
    const stuck = await execute(second(), { checks: ['checkRunning'] })
    if (stuck.result.stop !== 'check-running' || stuck.labels.filter((l) => l.startsWith('check:')).length !== 8 || stuck.labels.includes('release')) fail(`a check running at every ask: ${stuck.labels.join(', ')}; ${stuck.result.stop}`)
  }

  // 5. A red check runs release and returns the log's path and last lines; the other stops.
  checks++
  {
    const red = await execute(second(), { checks: ['checkFail'] })
    if (red.labels.join() !== 'start,build,check:1,release' || red.result.stop !== 'red') fail(`a red check: ran ${red.labels.join(', ')}, returned ${red.result.stop}`)
    if (red.result.log !== RUN.checkFail.log || !red.result.next.includes(RUN.checkFail.log)) fail(`a red check's return lacks the log's path: ${JSON.stringify(red.result).slice(0, 300)}`)
    const last = RUN.checkFail.tail[RUN.checkFail.tail.length - 1]
    if (!red.result.tail.includes(last) || !red.result.next.includes(last)) fail('a red check\'s return lacks the log\'s last lines')
    if (!red.result.release || red.result.release.step !== 'release') fail('a red check\'s return lacks the release record')
    const refused = await execute(second(), { start: courierReturn(RUN.startRefused) })
    if (refused.labels.join() !== 'start' || refused.result.stop !== 'start-refused' || !refused.result.next.includes('ready-for-agent')) fail(`a refused start: ran ${refused.labels.join(', ')}; ${JSON.stringify(refused.result).slice(0, 200)}`)
    const garbled = await execute(second(), { start: courierReturn(RUN.checkPass) })
    if (garbled.labels.join() !== 'start' || garbled.result.stop !== 'courier') fail(`a start courier that brought the check's record: ran ${garbled.labels.join(', ')}; ${garbled.result.stop}`)
    const otherIssue = await execute(second(), { checks: [courierReturn({ ...RUN.checkPass, issue: ISSUE + 1 })] })
    if (otherIssue.result.done || otherIssue.result.stop !== 'courier') fail(`a check record of another issue: ${otherIssue.result.stop}`)
    const question = await execute(second(), { build: { ...BUILT, outcome: 'needs-human', question: 'Which clock?' } })
    if (question.labels.join() !== 'start,build' || question.result.stop !== 'question' || !question.result.next.includes('Which clock?')) fail(`a build with a question: ran ${question.labels.join(', ')}; ${question.result.stop}`)
    for (const [label, build] of [['failed', { ...BUILT, outcome: 'failed', summary: 'could not build' }], ['nothing', null]]) {
      const f = await execute(second(), { build })
      if (f.labels.join() !== 'start,build,release' || f.result.stop !== 'failed') fail(`a build that returned ${label}: ran ${f.labels.join(', ')}; ${f.result.stop}`)
    }
    const deadCourier = await execute(second(), { start: null })
    if (deadCourier.result.stop !== 'courier') fail(`a start courier that returned nothing: ${deadCourier.result.stop}`)
  }

  // 6. Every return, early or final, has a non-empty next.
  checks++
  {
    if (returns.length < 30) fail(`only ${returns.length} runs were made`)
    for (const { args: a, result } of returns) {
      if (!result || typeof result.next !== 'string' || !result.next.trim()) fail(`a return with no next, for ${JSON.stringify(a).slice(0, 120)}: ${JSON.stringify(result).slice(0, 200)}`)
    }
  }
} catch (e) {
  console.error(`implement-ticket tests: the script did not run: ${e && e.stack ? e.stack : e}`)
  process.exit(2)
}

if (failures.length) {
  console.log(`implement-ticket tests: FAILED, ${failures.length} failure(s) in ${checks} checks, on ${path.basename(scriptPath)}:`)
  failures.forEach((f) => console.log(`  - ${f}`))
  process.exit(1)
}
console.log(`implement-ticket tests: pass: ${checks} checks, on ${path.basename(scriptPath)}`)
