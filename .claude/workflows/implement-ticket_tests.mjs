// The implement-ticket script's test companion. Owner: integrator. Tracked beside the script (#269),
// named for it as pins-sweep_tests.mjs is for its own.
//
//   node .claude/workflows/implement-ticket_tests.mjs [script]
//
// Runs implement-ticket.js whole, as pins-sweep_tests.mjs runs its own, with agent, parallel, log and
// phase stubbed. Every agent is answered by its label: the Plan agent with a proposal, the build agent
// with overnight.js's implement result, the two pre-review axes (standards:<round>, spec:<round>) with
// findings, each skeptic (skeptic:<round>:<i>) with a verdict, each repair (repair:<round>) with a head,
// and each courier (start, check:<n> or check:r<round>:<n>, prove:<round>, release) with a record the
// tool wrote, served from fixtures/implement-ticket.json, which tools/implement_ticket_tests.py
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
  mutations: [{ name: 'name', box: 1, project: 'tools/thing_tests.py' }],
}
// The heads repair rounds 1 and 2 return by default, and the check and prove records on each.
const REPAIR_HEADS = ['b'.repeat(40), 'c'.repeat(40)]
const headOf = (round) => (round === 0 ? RUN.checkPass.head : REPAIR_HEADS[round - 1])
const checkOn = (head) => ({ ...RUN.checkPass, head, logHead: head })
const proveOn = (head, states) => ({
  ...RUN.prove, head,
  ...(states ? { state: states.every((x) => ['proved', 'no-test'].includes(x)) ? 'proved' : 'gaps', boxes: RUN.prove.boxes.map((b, i) => ({ ...b, state: states[i], patches: states[i] === 'unproved' ? [] : b.patches })) } : {}),
})
const finding = (title, status = 'verified', axis = 'Standards') => ({ axis, title, where: 'tools/thing.py:1', checked: 'read it', status, severity: 'medium' })
const axisReturn = (axis, list = []) => ({ axis, sourcesRead: ['docs/agents/review.md'], findings: list })
const repairReturn = (headSha) => ({ headSha, summary: 'Fixed.', fixed: [], notFixed: [], resaved: [] })

// served: { plan, start, build, checks: [record names or returns] (the first check's asks),
//   checkRounds: { <round>: [returns] }, prove: { <round>: return }, reviews: { <label>: return },
//   skeptic: (finding) => return, repair: { <round>: return }, release }
// By default every check passes on its round's head, prove proves it, the axes find nothing, a skeptic
// refutes, and repair round r returns REPAIR_HEADS[r - 1].
const allCalls = []
function stub(served) {
  const calls = []
  const schemaFailures = []
  const asked = {}
  globalThis.phase = () => {}
  globalThis.log = () => {}
  globalThis.parallel = (fns) => Promise.all(fns.map((f) => f()))
  globalThis.agent = async (prompt, opts) => {
    const label = opts.label
    calls.push({ label, prompt, opts })
    allCalls.push({ label, prompt })
    let out
    let m
    if (label === 'plan') out = 'plan' in served ? served.plan : PROPOSAL
    else if (label === 'start') out = 'start' in served ? served.start : courierReturn(RUN.startNew)
    else if (label === 'build') out = 'build' in served ? served.build : BUILT
    else if ((m = /^check:(?:r(\d+):)?\d+$/.exec(label))) {
      const round = Number(m[1] || 0)
      const list = round === 0 ? served.checks || ['checkPass'] : (served.checkRounds || {})[round] || [courierReturn(checkOn(headOf(round)))]
      asked[round] = (asked[round] || 0) + 1
      const item = list[Math.min(asked[round] - 1, list.length - 1)]
      out = typeof item === 'string' ? courierReturn(RUN[item]) : item
    } else if ((m = /^prove:(\d+)$/.exec(label))) {
      const round = Number(m[1])
      out = served.prove && round in served.prove ? served.prove[round] : courierReturn(proveOn(headOf(round)))
    } else if (/^(standards|spec):\d+$/.test(label)) {
      const axis = label.startsWith('standards') ? 'Standards' : 'Spec'
      out = served.reviews && label in served.reviews ? served.reviews[label] : axisReturn(axis)
    } else if (label.startsWith('skeptic:')) {
      const f = JSON.parse(/^FINDING: (.*)$/m.exec(prompt)[1])
      out = served.skeptic ? served.skeptic(f) : { refuted: true, why: 'not a defect' }
    } else if ((m = /^repair:(\d+)$/.exec(label))) {
      const round = Number(m[1])
      out = served.repair && round in served.repair ? served.repair[round] : repairReturn(REPAIR_HEADS[round - 1])
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
    if (titles.join() !== 'Plan,Start,Build,Check,Prove,Review,Repair') fail(`meta phases ${titles.join(', ')}`)
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
      ['a seams object with an unknown field', second({ ...CONFIRMED, extra: 1 })],
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
    if (!String(x.result.next).includes(`python3 -I tools/implement_ticket.py launch ${ISSUE} --seams `)) fail(`the first launch's next names no launch command: ${x.result.next}`)
    if (!x.prompt('plan').includes(`${FIRST.repo}/.scratch/implement/log/${ISSUE}/read.json`) || !/number them from 1/.test(x.prompt('plan'))) fail('the Plan prompt does not take the boxes, numbered from 1, from read\'s record')
    for (const c of commands(x.prompt('plan'))) if (GH_WRITE.test(c)) fail(`the Plan prompt asks for a write: ${c}`)
    const asked = await execute(FIRST, { plan: { ...PROPOSAL, questions: ['Which clock does the thing read?'] } })
    if (!String(asked.result.next).includes('Which clock does the thing read?')) fail(`a human-owned question is not in next: ${asked.result.next}`)
    const cr = await execute(FIRST, { plan: { ...PROPOSAL, integratorPaths: [{ path: 'contracts/thing.md', why: 'a new field' }] } })
    if (cr.labels.join() !== 'plan' || cr.result.stop !== 'change-request') fail(`an integrator path: ran ${cr.labels.join(', ')}, returned ${cr.result.stop}`)
    if (!String(cr.result.next).includes('contracts/thing.md') || !String(cr.result.next).includes('"Change requests"') || /implement_ticket\.py launch/.test(cr.result.next)) fail(`the change-request stop's next: ${cr.result.next}`)
    const dead = await execute(FIRST, { plan: null })
    if (dead.result.stop !== 'plan-failed' || dead.labels.join() !== 'plan') fail(`a Plan agent that returned nothing: ${JSON.stringify(dead.result)}`)
  }

  // 3. With seams, no Plan agent runs; the confirmed seams reach the build prompt unchanged; under
  //    implementer the build agent is told to choose and list its own.
  checks++
  {
    const x = await execute(second())
    if (x.labels.join() !== 'start,build,check:1,prove:0,standards:0,spec:0') fail(`the second launch ran ${x.labels.join(', ')}, expected start, build, check:1, prove:0, standards:0, spec:0`)
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
    if (x.labels.join() !== 'start,build,check:1,check:2,check:3,prove:0,standards:0,spec:0') fail(`a running check: ran ${x.labels.join(', ')}`)
    if (x.result.done !== true || x.result.head !== RUN.checkPass.head) fail(`a pass on the build's head after two running records is not done: ${JSON.stringify(x.result).slice(0, 300)}`)
    if (!/review-queue/.test(x.result.next) || !String(x.result.next).includes(`land ${ISSUE}`)) fail(`done's next: ${x.result.next}`)
    const red = await execute(second(), { checks: ['checkFail'] })
    for (const c of [...x.calls, ...red.calls].filter((c) => ['start', 'release'].includes(c.label) || c.label.startsWith('check:'))) {
      if (!c.prompt.includes('timeout of 600000 ms')) fail(`the ${c.label} courier's prompt does not name the 600000 ms timeout`)
      if (c.opts.effort !== 'low') fail(`the ${c.label} courier runs at ${c.opts.effort} effort, not low`)
      if (!/Return the record exactly as printed/.test(c.prompt)) fail(`the ${c.label} courier is not told to return the record unchanged`)
    }
    const fin = await execute(second(), { start: courierReturn(RUN.startFinished) })
    if (fin.labels.join() !== 'start,check:1,prove:0,standards:0,spec:0' || fin.result.done !== true) fail(`a finished start: ran ${fin.labels.join(', ')}, returned ${JSON.stringify(fin.result).slice(0, 200)}`)
    const other = await execute(second(), { build: { ...BUILT, headSha: 'f'.repeat(40) } })
    if (other.result.done || other.result.stop !== 'head-mismatch' || other.labels.includes('release') || !String(other.result.next).includes('f'.repeat(40))) fail(`a pass on another head: ran ${other.labels.join(', ')}; ${JSON.stringify(other.result).slice(0, 300)}`)
    const finOther = await execute(second(), { start: courierReturn({ ...RUN.startFinished, head: 'e'.repeat(40) }) })
    if (finOther.result.done || finOther.result.stop !== 'head-mismatch') fail(`a finished start whose head is not the check's: ${finOther.result.stop}`)
    const unexited = await execute(second(), { checks: [courierReturn(RUN.checkPass, { exit: 2 })] })
    if (unexited.result.done || unexited.result.stop !== 'check-unjudged') fail(`a pass record with a non-zero exit: ${unexited.result.stop}`)
    const { head: _h, ...headless } = RUN.checkPass
    const noHeads = await execute(second(), { build: { ...BUILT, headSha: '' }, checks: [courierReturn({ ...RUN.checkPass, head: '' })] })
    if (noHeads.result.done) fail('a build and a pass that both name an empty head ended as done')
    const headlessStart = await execute(second(), { start: courierReturn({ ...RUN.startFinished, head: undefined }), checks: [courierReturn(headless)] })
    if (headlessStart.result.done || headlessStart.labels.join() !== 'start') fail(`a finished start with no head: ran ${headlessStart.labels.join(', ')}; ${headlessStart.result.stop}`)
    const { boxes: _b, ...boxless } = RUN.startNew
    const noBoxes = await execute(second(), { start: courierReturn(boxless) })
    if (noBoxes.labels.join() !== 'start' || noBoxes.result.stop !== 'start-unjudged') fail(`a start record with no boxes: ran ${noBoxes.labels.join(', ')}; ${noBoxes.result.stop}`)
    // A good start record with a non-zero exit: the tool could not write the record it printed.
    const startUnexited = await execute(second(), { start: courierReturn(RUN.startNew, { exit: 2 }) })
    if (startUnexited.labels.join() !== 'start' || startUnexited.result.stop !== 'start-unjudged') fail(`a new start record with a non-zero exit: ran ${startUnexited.labels.join(', ')}; ${startUnexited.result.stop}`)
    if (!String(x.result.next).includes('wait on his word') || commands(x.result.next).some((c) => /\bgit\s+push\b|gh\s+pr\s+create/.test(c))) fail(`done's next does not leave pushing to Adam: ${x.result.next}`)
    const stuck = await execute(second(), { checks: ['checkRunning'] })
    if (stuck.result.stop !== 'check-running' || stuck.labels.filter((l) => l.startsWith('check:')).length !== 8 || stuck.labels.includes('release')) fail(`a check running at every ask: ${stuck.labels.join(', ')}; ${stuck.result.stop}`)
  }

  // 5. A red check that a repair does not change runs release and returns the log's path and last
  //    lines; the other stops.
  checks++
  {
    const red = await execute(second(), { checks: ['checkFail'], repair: { 1: repairReturn(RUN.checkFail.head) } })
    if (red.labels.join() !== 'start,build,check:1,repair:1,release' || red.result.stop !== 'red') fail(`a red check: ran ${red.labels.join(', ')}, returned ${red.result.stop}`)
    const LOG_PATH = `${FIRST.repo}/.scratch/implement/log/${ISSUE}/check.log`
    if (red.result.log !== LOG_PATH || !String(red.result.next).includes(LOG_PATH)) fail(`a red check's return lacks the log's path: ${JSON.stringify(red.result).slice(0, 300)}`)
    const last = RUN.checkFail.tail[RUN.checkFail.tail.length - 1]
    if (!(Array.isArray(red.result.tail) && red.result.tail.includes(last)) || !String(red.result.next).includes(last)) fail('a red check\'s return lacks the log\'s last lines')
    if (!red.result.release || red.result.release.step !== 'release') fail('a red check\'s return lacks the release record')
    const { log: _l, ...logless } = RUN.checkFail
    const noLog = await execute(second(), { checks: [courierReturn(logless)] })
    if (noLog.result.stop !== 'check-unjudged' || noLog.labels.includes('release')) fail(`a failed check record with no log: ${noLog.result.stop}`)
    const refused = await execute(second(), { start: courierReturn(RUN.startRefused) })
    if (refused.labels.join() !== 'start' || refused.result.stop !== 'start-refused' || !String(refused.result.next).includes('ready-for-agent')) fail(`a refused start: ran ${refused.labels.join(', ')}; ${JSON.stringify(refused.result).slice(0, 200)}`)
    const garbled = await execute(second(), { start: courierReturn(RUN.checkPass) })
    if (garbled.labels.join() !== 'start' || garbled.result.stop !== 'courier') fail(`a start courier that brought the check's record: ran ${garbled.labels.join(', ')}; ${garbled.result.stop}`)
    const otherIssue = await execute(second(), { checks: [courierReturn({ ...RUN.checkPass, issue: ISSUE + 1 })] })
    if (otherIssue.result.done || otherIssue.result.stop !== 'courier') fail(`a check record of another issue: ${otherIssue.result.stop}`)
    const question = await execute(second(), { build: { ...BUILT, outcome: 'needs-human', question: 'Which clock?' } })
    if (question.labels.join() !== 'start,build' || question.result.stop !== 'question' || !String(question.result.next).includes('Which clock?')) fail(`a build with a question: ran ${question.labels.join(', ')}; ${question.result.stop}`)
    for (const [label, build] of [['failed', { ...BUILT, outcome: 'failed', summary: 'could not build' }], ['nothing', null]]) {
      const f = await execute(second(), { build })
      if (f.labels.join() !== 'start,build,release' || f.result.stop !== 'failed') fail(`a build that returned ${label}: ran ${f.labels.join(', ')}; ${f.result.stop}`)
    }
    const deadCourier = await execute(second(), { start: null })
    if (deadCourier.result.stop !== 'courier') fail(`a start courier that returned nothing: ${deadCourier.result.stop}`)
  }

  // 6. The build prompt asks for each mutation as a patch and its sidecar; after a passing check the
  //    run starts prove, then Standards and Spec, whose prompts name the fork and head by sha; a run
  //    with nothing found is done with the fixture's prove record, no findings and no rounds.
  checks++
  {
    const x = await execute(second())
    const build = x.prompt('build')
    const dir = `${FIRST.repo}/.scratch/implement/log/${ISSUE}/mutations`
    if (!build.includes(`${dir}/<k>.patch`) || !build.includes(`${dir}/<k>.json`) || !build.includes(`\`git -C ${RUN.startNew.worktree} diff\``) || !/"box": <the open box it proves/.test(build) || !/"project": "<the test project/.test(build) || !/restore the file/.test(build)) fail('the build prompt does not ask for each break as a patch made with git diff, its sidecar naming the box and project, and the file restored')
    if (!/saves that patch again/.test(build)) fail('the build prompt does not state the rule that a later change saves a patch again')
    if (!build.includes(`but for one directory: ${dir}`)) fail('the build prompt does not let the agent write the mutations directory')
    const at = (l) => x.labels.indexOf(l)
    if (!(at('check:1') < at('prove:0') && at('prove:0') < at('standards:0') && at('prove:0') < at('spec:0'))) fail(`after a passing check: ${x.labels.join(', ')}`)
    if (!x.prompt('prove:0').includes(`\`cd ${FIRST.repo} && python3 -I tools/implement_ticket.py prove ${ISSUE}\``)) fail('the prove courier does not run the tool\'s prove')
    const { fork, head } = RUN.checkPass
    for (const axis of ['standards', 'spec']) {
      const pr = x.prompt(`${axis}:0`)
      if (!pr.includes(`fork ${fork}, head ${head}`) || !pr.includes(`diff ${fork}...${head}`) || !pr.includes('docs/agents/review.md')) fail(`the ${axis} prompt does not name the fork and head by sha and the review guide`)
    }
    if (!/CODING_STANDARDS\.md, AGENTS\.md and OWNERSHIP\.md/.test(x.prompt('standards:0'))) fail('the Standards prompt does not hold the diff to its three sources')
    if (!/the issue's body/.test(x.prompt('spec:0')) || !/contracts\//.test(x.prompt('spec:0'))) fail('the Spec prompt does not hold the diff to the issue and the contracts')
    if (x.result.done !== true || !same(x.result.prove, RUN.prove) || x.result.rounds !== 0 || !same(x.result.findings, { repaired: [], outstanding: [], refuted: [] })) fail(`a clean run's return: ${JSON.stringify(x.result).slice(0, 300)}`)
    // A pass on another head than the repair's is a stop, not a pass.
    const off = await execute(second(), { reviews: { 'standards:0': axisReturn('Standards', [finding('A bug')]) }, checkRounds: { 1: [courierReturn(checkOn('d'.repeat(40)))] } })
    if (off.result.done || off.result.stop !== 'head-mismatch' || !String(off.result.next).includes('d'.repeat(40))) fail(`a pass on another head than the repair's: ${off.result.stop}`)
    // prove refused, unjudged or of another head stops; a bad sidecar is a finding the repair gets.
    for (const [label, rec] of [['refused', { ...RUN.prove, state: 'refused', verdict: 'fail', exit: 1 }], ['of another head', { ...RUN.prove, head: 'e'.repeat(40) }], ['unjudged', { step: 'prove', issue: ISSUE, state: 'unjudged', verdict: 'fail', exit: 2, why: 'the call\'s time bound' }]]) {
      const y = await execute(second(), { prove: { 0: courierReturn(rec) } })
      if (y.result.stop !== 'prove-unjudged' || y.labels.some((l) => /^(standards|repair|release)/.test(l))) fail(`a prove ${label}: ran ${y.labels.join(', ')}; ${y.result.stop}`)
    }
    const bad = await execute(second(), { prove: { 0: courierReturn({ ...RUN.prove, state: 'fail', verdict: 'fail', exit: 1, why: 'name: its sidecar names ../x.py, a path outside the worktree', boxes: undefined }) } })
    if (!/The saved mutations cannot be proved/.test(bad.prompt('repair:1')) || !bad.result.done) fail(`a prove failed on a bad sidecar does not reach the repair: ${bad.labels.join(', ')}`)
  }

  // 7. Skeptics: a refuted suspected finding reaches no repair; one not refuted does; a verified
  //    finding gets no skeptic; a did-not-fail box is a finding with no skeptic.
  checks++
  {
    const doubted = finding('A doubtful bug', 'suspected')
    const refuted = await execute(second(), { reviews: { 'standards:0': axisReturn('Standards', [doubted]) } })
    if (!refuted.labels.includes('skeptic:0:1') || refuted.labels.some((l) => l.startsWith('repair:')) || refuted.result.findings.refuted.length !== 1 || refuted.result.findings.refuted[0].title !== doubted.title) fail(`a refuted suspected finding: ran ${refuted.labels.join(', ')}; ${JSON.stringify(refuted.result.findings)}`)
    if (!refuted.prompt('skeptic:0:1').includes('refute') || !/when you are unsure/.test(refuted.prompt('skeptic:0:1'))) fail('the skeptic is not told to refute, and to answer refuted when unsure')
    const kept = await execute(second(), { reviews: { 'standards:0': axisReturn('Standards', [doubted]) }, skeptic: () => ({ refuted: false, why: 'it is a defect' }) })
    if (!kept.labels.includes('repair:1') || !kept.prompt('repair:1').includes(doubted.title)) fail(`a suspected finding not refuted does not reach the repair: ${kept.labels.join(', ')}`)
    if (!same(kept.result.findings.repaired.map((f) => f.title), [doubted.title])) fail(`a repaired finding is not returned as repaired: ${JSON.stringify(kept.result.findings)}`)
    const dead = await execute(second(), { reviews: { 'standards:0': axisReturn('Standards', [doubted]) }, skeptic: () => null })
    if (!dead.labels.includes('repair:1')) fail('a suspected finding whose skeptic returned nothing was dropped, though nobody refuted it')
    const sure = await execute(second(), { reviews: { 'spec:0': axisReturn('Spec', [finding('A sure bug', 'verified', 'Spec')]) } })
    if (sure.labels.some((l) => l.startsWith('skeptic:')) || !sure.prompt('repair:1').includes('A sure bug')) fail(`a verified finding: ran ${sure.labels.join(', ')}`)
    const box = await execute(second(), { prove: { 0: courierReturn(proveOn(RUN.checkPass.head, ['did-not-fail', 'no-test'])) } })
    if (box.labels.some((l) => l.startsWith('skeptic:')) || !box.prompt('repair:1').includes("Box 1's mutation proof is did-not-fail")) fail(`a did-not-fail box: ran ${box.labels.join(', ')}`)
    // A box still unproved on the repair's head is a finding there: its place names that head.
    const still = await execute(second(), { prove: { 0: courierReturn(proveOn(RUN.checkPass.head, ['unproved', 'no-test'])), 1: courierReturn(proveOn(REPAIR_HEADS[0], ['unproved', 'no-test'])) } })
    if (!still.prompt('repair:2').includes("Box 1's mutation proof is unproved") || !still.prompt('repair:2').includes(`box 1 at ${REPAIR_HEADS[0]}`)) fail(`a box still unproved on the repair's head is dropped as seen: ${still.labels.join(', ')}`)
    // The whole default of the fixture: prove's gaps record served as is.
    const redBase = await execute(second(), { prove: { 0: courierReturn(proveOn(RUN.checkPass.head, ['baseline-red', 'no-test'])) } })
    if (!redBase.prompt('repair:1').includes("Box 1's mutation proof is baseline-red")) fail(`a baseline-red box is no finding: ${redBase.labels.join(', ')}`)
    const asIs = await execute(second(), { prove: { 0: courierReturn({ ...RUN.prove, state: 'gaps', boxes: [RUN.prove.boxes[0], { ...RUN.prove.boxes[1], state: 'did-not-apply' }] }) } })
    if (!asIs.prompt('repair:1').includes("Box 2's mutation proof is did-not-apply")) fail('a did-not-apply box does not reach the repair')
  }

  // 8. Dedup: a finding refuted in round one and reported again in round two is dropped by its key.
  checks++
  {
    const doubted = finding('A doubtful bug', 'suspected')
    const x = await execute(second(), {
      reviews: { 'standards:0': axisReturn('Standards', [doubted, finding('A real bug')]), 'standards:1': axisReturn('Standards', [{ ...doubted, title: '  a DOUBTFUL   bug ', checked: 'read again' }]) },
    })
    if (x.labels.filter((l) => l.startsWith('skeptic:')).join() !== 'skeptic:0:1' || x.labels.includes('repair:2')) fail(`a finding refuted in round one came back in round two: ran ${x.labels.join(', ')}`)
    if (x.result.done !== true || x.result.findings.refuted.length !== 1 || x.result.findings.outstanding.length || x.result.rounds !== 1) fail(`the dedup run's return: ${JSON.stringify(x.result.findings)}, rounds ${x.result.rounds}`)
    const elsewhere = await execute(second(), {
      reviews: { 'standards:0': axisReturn('Standards', [doubted, finding('A real bug')]), 'standards:1': axisReturn('Standards', [{ ...doubted, where: 'tools/thing.py:9' }]) },
    })
    if (!elsewhere.labels.includes('skeptic:1:1')) fail('a finding with the same title in another place was dropped as seen')
  }

  // 9. Repair: it saves patches again; a new head goes to check before any further review; never a
  //    third round; what is left after the second is outstanding; a red last check runs release.
  checks++
  {
    const every = {}
    for (const r of [0, 1, 2]) every[`standards:${r}`] = axisReturn('Standards', [finding(`Bug of round ${r}`)])
    const x = await execute(second(), { reviews: every })
    const rp = x.prompt('repair:1')
    if (!/saves that patch again/.test(rp) || !rp.includes(`${FIRST.repo}/.scratch/implement/log/${ISSUE}/mutations`) || !/List each patch you saved again in resaved/.test(rp)) fail('the repair prompt does not tell the agent to save again each patch its change stops applying')
    if (!/A fresh context/.test(rp) || !rp.includes(`Ticket: #${ISSUE}`)) fail('the repair prompt lacks the fresh context or the Ticket line')
    const after = x.labels.slice(x.labels.indexOf('repair:1') + 1)
    if (after[0] !== 'check:r1:1' || after[1] !== 'prove:1' || !after.includes('standards:1')) fail(`after repair:1: ${after.join(', ')}`)
    if (!x.prompt('standards:1').includes(`diff ${RUN.checkPass.head}..${REPAIR_HEADS[0]}`)) fail('the second pre-review does not read the new commits by sha')
    if (x.labels.includes('repair:3') || !x.labels.includes('repair:2') || !x.labels.includes('standards:2') || !x.labels.includes('prove:2')) fail(`three rounds of findings: ran ${x.labels.join(', ')}`)
    if (x.result.done !== true || x.result.rounds !== 2 || !same(x.result.findings.outstanding.map((f) => f.title), ['Bug of round 2']) || x.result.head !== REPAIR_HEADS[1]) fail(`findings left after the second round: ${JSON.stringify(x.result).slice(0, 400)}`)
    if (!same(x.result.findings.repaired.map((f) => f.title), ['Bug of round 0', 'Bug of round 1'])) fail(`the repaired findings: ${JSON.stringify(x.result.findings.repaired)}`)
    const declined = await execute(second(), { reviews: { 'standards:0': axisReturn('Standards', [finding('Bug of round 0'), finding('A matter of taste')]) }, repair: { 1: { ...repairReturn(REPAIR_HEADS[0]), notFixed: [{ title: 'a matter of TASTE', why: 'the standard allows it' }] } } })
    if (!same(declined.result.findings.repaired.map((f) => f.title), ['Bug of round 0']) || !same(declined.result.findings.outstanding.map((f) => [f.title, f.notFixedBecause]), [['A matter of taste', 'the standard allows it']])) fail(`a finding the repair did not fix: ${JSON.stringify(declined.result.findings)}`)
    const nothing = await execute(second(), { reviews: every, repair: { 1: repairReturn(RUN.checkPass.head) } })
    if (nothing.labels.includes('check:r1:1') || nothing.result.done !== true || nothing.result.rounds !== 1 || !same(nothing.result.findings.outstanding.map((f) => f.title), ['Bug of round 0'])) fail(`a repair that changed nothing: ran ${nothing.labels.join(', ')}; ${JSON.stringify(nothing.result.findings)}`)
    const red = courierReturn({ ...RUN.checkFail, head: REPAIR_HEADS[1] })
    const reds = await execute(second(), { checks: ['checkFail'], checkRounds: { 1: [courierReturn({ ...RUN.checkFail, head: REPAIR_HEADS[0] })], 2: [red] } })
    if (reds.labels.join() !== 'start,build,check:1,repair:1,check:r1:1,repair:2,check:r2:1,release' || reds.result.stop !== 'red' || !reds.result.release || reds.result.rounds !== 2) fail(`a red last check: ran ${reds.labels.join(', ')}; ${reds.result.stop}`)
    if (!reds.prompt('repair:1').includes(RUN.checkFail.log) || !reds.prompt('repair:1').includes(RUN.checkFail.why)) fail('the repair of a red check is not given its log and why')
    const mended = await execute(second(), { checks: ['checkFail'] })
    if (mended.result.done !== true || mended.labels.join() !== 'start,build,check:1,repair:1,check:r1:1,prove:1,standards:1,spec:1') fail(`a red check the first repair mends: ran ${mended.labels.join(', ')}`)
    if (!mended.prompt('standards:1').includes(`diff ${RUN.checkPass.fork}...${REPAIR_HEADS[0]}`) || mended.prompt('standards:1').includes('new commits since')) fail('a first pre-review after a red check does not read the whole diff')
  }

  // 10. No prompt and no return holds a review record's line.
  checks++
  {
    const RECORD = /Reviewed [0-9a-f]{40} against [0-9a-f]{40}/
    for (const c of allCalls) if (RECORD.test(c.prompt)) fail(`the ${c.label} prompt holds a review record's line`)
    for (const { result } of returns) if (RECORD.test(JSON.stringify(result))) fail(`a return holds a review record's line: ${JSON.stringify(result).slice(0, 200)}`)
    if (allCalls.length < 100) fail(`only ${allCalls.length} prompts were read`)
  }

  // 11. Every return, early or final, has a non-empty next.
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
