export const meta = {
  name: 'implement-ticket',
  description: 'One ticket in the foreground: propose seams and stop; then, at the seams Adam confirmed, start the worktree, build test-first, run the full check, re-run every saved mutation, pre-review on two axes with skeptics, and repair at most twice, routing on the records tools/implement_ticket.py writes',
  whenToUse: 'In a foreground session, launched by scriptPath with the args object tools/implement_ticket.py prints: read prints the first launch, launch prints the second; it never pushes or merges, and its pre-review is not a review record',
  phases: [
    { title: 'Plan', detail: 'the first launch only: one agent reads the ticket, its plan row and the contracts and proposes seams, a test per open box, any integrator path and any human-owned question; nothing is written' },
    { title: 'Start', detail: 'a courier runs the tool\'s start and returns its record: the worktree and branch, new, continued or finished' },
    { title: 'Build', detail: 'one agent builds in the worktree, following the /implement skill at the confirmed seams, with no review step' },
    { title: 'Check', detail: 'a courier runs the tool\'s check, again while its record says running; a red check goes to a repair, a pass on another head stops with the issue still assigned' },
    { title: 'Prove', detail: 'after a passing check, a courier runs the tool\'s prove: each saved mutation patch re-run in a throwaway worktree at the checked head' },
    { title: 'Review', detail: 'a Standards and a Spec agent pre-review the diff by sha in fresh contexts; each suspected finding goes to a skeptic told to refute it; a box prove did not prove is a finding of the script\'s own; findings already seen are dropped' },
    { title: 'Repair', detail: 'one agent fixes the surviving findings or a red check and saves again each patch its change stops applying; its new head goes back to Check; two rounds at most, and a red last check runs release' },
  ],
}

// ---------------------------------------------------------------- what this is
// The second and third tickets of the foreground implement harness (#1563, built under #1565 and
// #1566): the workflow half, as far as a built, checked, proved and pre-reviewed branch.
// tools/implement_ticket.py decides what code can decide and
// writes each step's record; this script starts the agents that judge (the planner, the builder) and
// the couriers that run one tool command each, and routes on the records they return. A Workflow
// script has no filesystem and no network, so every read of git, the issue or a log is the tool's.
// A courier that garbles a record cannot cause a bad hand-over: each later tool step reads the records
// and git from disk, not from what this script was told.
// Test companion: `implement-ticket_tests.mjs` beside this file.
//
// The two-launch protocol a session follows (a script cannot ask a question while it runs):
//   1. `cd <repo> && python3 -I tools/implement_ticket.py read <n> --wt <worktree>` gates the ticket.
//      Its record, when it passes, ends on "launch": { scriptPath, args }. Launch that (Workflow tool,
//      by scriptPath, args as an object). With no seams argument this script runs the Plan agent alone
//      and returns its proposal, writing nothing anywhere.
//   2. Show the proposed seams to Adam. Write the ones he confirms to a file, as
//      {"seams": [{"name", "interface", "boxes"}], "noTest": [{"box", "why"}]}, boxes numbered from 1
//      in read's order, and run `python3 -I tools/implement_ticket.py launch <n> --seams <file>` (or
//      `--seams implementer`, to let the builder choose). It fails naming any open box that is in no
//      seam, or in two; when it passes its record ends on the second launch: the same args with
//      seams added. That launch skips Plan and runs Start, Build and Check, then the loop below.
// The loop (#1566). A red check goes to Repair. A passing check goes to Prove, the tool's re-run of
// every saved mutation patch, and Review: a Standards and a Spec agent read the diff by sha, each
// suspected finding goes to a skeptic told to refute it, and survives only when not refuted; a box
// prove left did-not-fail, did-not-apply, baseline-red or unproved is a verified finding of the
// script's own, which no skeptic sees. Findings are deduplicated by key (FINDING below). Surviving
// findings go to Repair:
// one agent, which returns a new head, and any new head goes back to Check, then Prove and the
// pre-review of the new commits. Two repair rounds at most (MAX_REPAIRS): after the second, or a round
// that changes nothing, the run ends, done with what is left as findings.outstanding when the last
// check passed, and failed with release run when it was red. So the head a run ends done on always has
// both a passing check and a prove record, which the hand-over reads (#1563's fourth ticket). The
// pre-review is not a review record (docs/agents/review.md -> "Landing or holding"): nothing here
// writes one, and the record still comes from a fresh top-level session Adam starts.
// Every return, early or final, carries next: the literal command or question that comes next.
//
// Arguments, and no others: an unknown one is refused before any agent starts.
//   repo    optional  the main checkout, an absolute path; default REPO_DEFAULT below. The tool runs
//                     there; nothing is built or committed there.
//   issue   required  the ticket's issue number, a positive integer.
//   wt      required  the worktree path read recorded, absolute and outside repo. The build works in
//                     the worktree start's record names, which is this one unless start found the task
//                     branch already checked out elsewhere.
//   port    optional  the check's port, an integer from 1024 to 65535; default 5180.
//   seams   optional  absent on the first launch. On the second, what the tool's launch printed: the
//                     confirmed {"seams": [...], "noTest": [...]}, or the word "implementer".
//   skills  optional  the skills directory, an absolute path holding implement/SKILL.md; default
//                     SKILLS_DEFAULT below, overnight.js's (#501).
//
// What each stop leaves (#1563 -> "What each stop leaves"):
//   bad launch              nothing                              which argument to fix
//   after Plan              nothing                              show the seams, then the tool's launch
//   an integrator path      nothing                              file a change request with Adam, stop
//   start refused           nothing                              the gate that failed
//   a human-owned question  assigned, branch kept                the question; the same line continues
//   failed, or red at the   release run, branch kept             the check log's path and last lines
//     end of the repairs
//   a pass on another head  assigned, branch kept                compare the two heads; the same line
//   check unjudged, or      assigned, branch kept                what the tool names; the same line
//     still running
//   prove unjudged, or its  assigned, branch at the checked head run prove by hand; the same line
//     record not this head's
//   done                    assigned, branch at the checked head tell Adam; pushing waits on his word
// Every return after Start carries findings { repaired, outstanding, refuted }, prove (the last prove
// record, its patch and box rows) and rounds (the repair rounds used).
// Departures from #1563's table, named in #1565's pull request: a pass on another head
// (head-mismatch), a check unjudged and a check still running are stops that table does not have.
// #1566 adds prove-unjudged: a prove refused, unjudged or of another head is no fact a repair can
// fix, so the run stops with the issue assigned rather than spend a round on it; a prove that fails
// on a bad sidecar is a finding the repair can fix.
// None runs release: the branch may hold good work the tool could not judge, so the issue stays
// assigned and the same line continues. And done's next names no hand-over command, where that table
// names one: #1565 leaves the hand-over out of its scope, so pushing the branch and opening the pull
// request wait on Adam's word.
const A = args || {}
const KNOWN_ARGS = ['repo', 'issue', 'wt', 'port', 'seams', 'skills']
const REPO_DEFAULT = '/Users/adammedford/Projects/spa-platform'
const SKILLS_DEFAULT = '/Users/adammedford/.agents/skills'
const PORT_DEFAULT = 5180
// A courier's one Bash call: check waits up to 540 s for a verdict and the Bash tool allows 600000 ms.
const COURIER_TIMEOUT_MS = 600000
// A check still running after this many asks is handed back to the session rather than waited on.
const CHECK_ASKS = 8

const abs = (v) => typeof v === 'string' && v.startsWith('/') && !/(^|\/)\.\.(\/|$)/.test(v)
const text = (v) => typeof v === 'string' && v.trim() !== ''
const box = (v) => Number.isInteger(v) && v > 0
const seamsProblems = (s) => {
  if (s === 'implementer') return []
  if (!s || typeof s !== 'object' || Array.isArray(s) || !Array.isArray(s.seams)) return ['seams: the confirmed {"seams": [...], "noTest": [...]} the tool\'s launch printed, or the word "implementer"']
  const bad = []
  for (const k of Object.keys(s)) if (!['seams', 'noTest'].includes(k)) bad.push(`seams.${k}: not a field; a seams object has seams and noTest`)
  s.seams.forEach((x, i) => {
    if (!x || !text(x.name) || !text(x.interface) || !Array.isArray(x.boxes) || !x.boxes.length || !x.boxes.every(box)) bad.push(`seams.seams[${i}]: {name, interface, boxes}, boxes a non-empty list of 1-based box numbers`)
  })
  if (s.noTest !== undefined && !(Array.isArray(s.noTest) && s.noTest.every((x) => x && box(x.box) && text(x.why)))) bad.push('seams.noTest: a list of {box, why}')
  return bad
}
const badArguments = (a) => {
  const bad = []
  for (const k of Object.keys(a)) if (!KNOWN_ARGS.includes(k)) bad.push(`${k}: not an argument of this workflow; it takes ${KNOWN_ARGS.join(', ')}`)
  if (a.repo !== undefined && !abs(a.repo)) bad.push('repo: an absolute path to the main checkout')
  if (!(Number.isInteger(a.issue) && a.issue > 0)) bad.push('issue: required, the issue number as a positive integer (not a string)')
  if (!abs(a.wt)) bad.push('wt: required, the worktree read recorded, an absolute path')
  const repo = a.repo || REPO_DEFAULT
  if (abs(a.wt) && (a.wt === repo || a.wt.startsWith(repo + '/'))) bad.push(`wt: ${a.wt} is inside the repository; a worktree lives outside the main checkout`)
  if (a.port !== undefined && !(Number.isInteger(a.port) && a.port >= 1024 && a.port <= 65535)) bad.push('port: an integer from 1024 to 65535 (default 5180)')
  if (a.seams !== undefined) bad.push(...seamsProblems(a.seams))
  if (a.skills !== undefined && !abs(a.skills)) bad.push('skills: when given, the skills directory as an absolute path, the one holding implement/SKILL.md')
  return bad
}
const BAD_LAUNCH = badArguments(A)
if (BAD_LAUNCH.length) {
  // Stops here: no agent is started, nothing is read or written.
  return {
    halted: 'bad launch; no agent started. Fix each argument and relaunch:\n- ' + BAD_LAUNCH.join('\n- '),
    next: 'Relaunch with the args object the tool printed, unchanged: the "launch" that ends the record of `python3 -I tools/implement_ticket.py read <n> --wt <worktree>` (the first launch) or of `python3 -I tools/implement_ticket.py launch <n> --seams <file>` (the second).',
  }
}

const REPO = A.repo || REPO_DEFAULT
const ISSUE = A.issue
const PORT = A.port || PORT_DEFAULT
const SKILLS = (A.skills || SKILLS_DEFAULT).replace(/\/+$/, '')
const SEAMS = A.seams
const TOOL = `cd ${REPO} && python3 -I tools/implement_ticket.py`
const LOG = `${REPO}/.scratch/implement/log/${ISSUE}`
const SEAMS_FILE = `${LOG}/seams.json`
const LAUNCH_AGAIN = 'the same launch (scriptPath and args unchanged), which start continues on the branch'

// ---------------------------------------------------------------- schemas
const PLAN = {
  type: 'object',
  properties: {
    seams: { type: 'array', items: { type: 'object', properties: {
      name: { type: 'string' }, interface: { type: 'string' }, catches: { type: 'string' }, misses: { type: 'string' },
      boxes: { type: 'array', items: { type: 'integer' } },
    }, required: ['name', 'interface', 'catches', 'misses', 'boxes'] } },
    tests: { type: 'array', items: { type: 'object', properties: {
      box: { type: 'integer' }, seam: { type: 'string' }, test: { type: 'string' },
    }, required: ['box', 'seam', 'test'] } },
    noTest: { type: 'array', items: { type: 'object', properties: { box: { type: 'integer' }, why: { type: 'string' } }, required: ['box', 'why'] } },
    integratorPaths: { type: 'array', items: { type: 'object', properties: { path: { type: 'string' }, why: { type: 'string' } }, required: ['path', 'why'] } },
    questions: { type: 'array', items: { type: 'string' } },
    summary: { type: 'string' },
  },
  required: ['seams', 'tests', 'noTest', 'integratorPaths', 'questions', 'summary'],
}
// A courier hands back what the tool printed: its record as one object ({} when it printed none), its
// last line, and its exit code. The record's fields are the tool's, so the schema does not name them:
// the script reads step, issue and state, and treats a record without them as garbled.
const COURIER = {
  type: 'object',
  properties: { record: { type: 'object' }, verdictLine: { type: 'string' }, exit: { type: 'integer' } },
  required: ['record', 'verdictLine', 'exit'],
}
// overnight.js's IMPL, with the tests per box, the seams the build used and its saved mutations added.
const CHANGE_REQUESTS = { type: 'array', items: { type: 'object', properties: {
  number: { type: 'integer' }, title: { type: 'string' }, blocking: { type: 'integer' }, queued: { type: 'integer' }, filed: { type: 'boolean' },
}, required: ['number', 'title', 'blocking', 'queued', 'filed'] } }
const BUILD = {
  type: 'object',
  properties: {
    outcome: { type: 'string', enum: ['resolved', 'needs-human', 'failed', 'skipped'] },
    branch: { type: 'string' }, forkSha: { type: 'string' }, headSha: { type: 'string' },
    complete: { type: 'boolean' }, outstanding: { type: 'string' },
    summary: { type: 'string' }, question: { type: 'string' },
    delegatedDecisions: { type: 'array', items: { type: 'string' } },
    changeRequests: CHANGE_REQUESTS,
    changedFiles: { type: 'array', items: { type: 'string' } },
    lastLine: { type: 'string' },
    tests: { type: 'array', items: { type: 'object', properties: {
      box: { type: 'integer' }, test: { type: 'string' }, project: { type: 'string' },
    }, required: ['box', 'test', 'project'] } },
    seams: { type: 'array', items: { type: 'object', properties: {
      name: { type: 'string' }, interface: { type: 'string' }, boxes: { type: 'array', items: { type: 'integer' } },
    }, required: ['name', 'interface', 'boxes'] } },
    mutations: { type: 'array', items: { type: 'object', properties: {
      name: { type: 'string' }, box: { type: 'integer' }, project: { type: 'string' },
    }, required: ['name', 'box', 'project'] } },
  },
  required: ['outcome', 'branch', 'forkSha', 'headSha', 'complete', 'outstanding', 'summary', 'question', 'delegatedDecisions', 'changeRequests', 'changedFiles', 'lastLine', 'tests', 'seams', 'mutations'],
}

// ---------------------------------------------------------------- couriers
// One tool command, its printed record returned unchanged. Every command is a backtick span.
const courierPrompt = (step, command) => `You are a courier. Run exactly one Bash command, giving that Bash call a timeout of ${COURIER_TIMEOUT_MS} ms (the tool may wait 540 s before it prints), and nothing else:

\`${command}\`

It prints a JSON record on one line, then a verdict line beginning "implement-ticket: ${step}:". Return the record exactly as printed, parsed but unchanged, as record ({} when it printed none), the last line unchanged as verdictLine, and the command's exit code as exit. Do not run it a second time, do not run any other command, do not edit any file, and do not interpret or repair the record: the script routes on it.`
const courier = (step, command, label, phaseName) => agent(courierPrompt(step, command), { label, phase: phaseName, schema: COURIER, effort: 'low' })
// The record a courier brought back, when it is the record of this step for this issue; else null.
const recordOf = (got, step) => {
  if (!got || !got.record || typeof got.record !== 'object') return null
  const r = got.record
  return r.step === step && r.issue === ISSUE ? r : null
}
const garbled = (step, command, got, left) => ({
  stop: 'courier', step, left,
  returned: got,
  next: `The ${step} courier returned ${got ? 'a record that is not the tool\'s ' + step + ' record for #' + ISSUE : 'nothing'}. Run \`${command}\` yourself and read its verdict line; then relaunch ${LAUNCH_AGAIN}.`,
})

// ---------------------------------------------------------------- launch 1: Plan alone
if (SEAMS === undefined) {
  phase('Plan')
  const planPrompt = `Propose the test seams for ticket #${ISSUE}, before any code is written. A foreground run: Adam confirms the seams you propose, then a later launch builds at exactly those.
Write nothing: no file in ${REPO} or anywhere else, no tracker write, no branch, no commit. Read only.

Read:
- the ticket's record from the tool, ${LOG}/read.json: its "boxes" are the open acceptance boxes in order, and you number them from 1 in that order (box 1 is the first), as the tool's launch checks them; its "owner", "touches" and "kinds";
- the issue, \`cd ${REPO} && gh issue view ${ISSUE} --json title,body,comments\`: the body is the spec;
- the plan row or spec it cites (docs/plan/03-workstreams-and-tasks.md, or the parent issue it names), and the contracts/ files, ADRs and GLOSSARY.md terms in its area;
- ${SKILLS}/tdd/SKILL.md, "Seams: where tests go", for what a seam is.

Return:
- seams: each a public interface the tests observe behaviour through, with name, interface (what is called and what is observed), catches (what a test there catches), misses (what it cannot see), and boxes (the box numbers it covers). Few seams, at the ticket's public boundaries, never a helper.
- tests: one entry per open box that gets a test: its box, the seam's name, and the test in a sentence.
- noTest: each open box that needs no test, with why (a box that is itself a test run, a file that must be tracked). Every open box is in exactly one seam or in noTest.
- integratorPaths: each path the work needs that the ticket's owner does not own by OWNERSHIP.md (a contract, a package version, a new project, src/Platform.Abstractions), with why. When the owner is the integrator, its own paths are no such need. [] when there is none.
- questions: each human-owned question the ticket leaves open (the plan, contracts and ADRs are silent or contradict, and the choice is hard to reverse). [] when there is none.
- summary: two sentences.`
  const plan = await agent(planPrompt, { label: 'plan', phase: 'Plan', schema: PLAN })
  if (!plan) {
    return { stop: 'plan-failed', left: 'nothing: no tracker write, no file', next: 'The Plan agent returned nothing. Relaunch the first launch unchanged: the "launch" in ' + LOG + '/read.json.' }
  }
  if (plan.integratorPaths.length) {
    return {
      stop: 'change-request', proposal: plan, left: 'nothing: no tracker write, no file',
      next: `The ticket needs path(s) its owner does not hold: ${plan.integratorPaths.map((p) => p.path).join(', ')}. File a change request with Adam and stop: docs/agents/tracker-policy.md -> "Change requests". Do not launch the build.`,
    }
  }
  const asks = plan.questions.length ? ` Put each human-owned question to Adam first: ${plan.questions.join(' | ')}.` : ''
  return {
    stop: 'seams', proposal: plan, left: 'nothing: no tracker write, no file',
    next: `Show the proposed seams and tests to Adam.${asks} Write the seams he confirms to ${SEAMS_FILE} as {"seams": [{"name", "interface", "boxes"}], "noTest": [{"box", "why"}]}, then run \`${TOOL} launch ${ISSUE} --seams ${SEAMS_FILE}\` (or \`${TOOL} launch ${ISSUE} --seams implementer\` if he leaves the seams to the builder), and launch the second launch its record ends on.`,
  }
}

// ---------------------------------------------------------------- launch 2: Start, Build, Check
const release = async (left) => {
  const command = `${TOOL} release ${ISSUE}`
  const got = await courier('release', command, 'release', 'Check')
  const record = recordOf(got, 'release')
  return { record, command, left: record && record.verdict === 'pass' ? left : `${left}; but release did not report a pass, so run \`${command}\` yourself` }
}

phase('Start')
const startCommand = `${TOOL} start ${ISSUE}`
const startGot = await courier('start', startCommand, 'start', 'Start')
const begun = recordOf(startGot, 'start')
if (!begun) return garbled('start', startCommand, startGot, 'unknown: start may have assigned the issue and added the worktree')
if (begun.state === 'refused') {
  return { stop: 'start-refused', record: begun, left: 'nothing: start refused before any write', next: `start refused #${ISSUE}: ${begun.why}. Once the gate holds, run \`${TOOL} read ${ISSUE} --wt ${A.wt}\` again and launch what it prints.` }
}
if (!['new', 'continued', 'finished'].includes(begun.state) || startGot.exit !== 0 || !text(begun.worktree) || !text(begun.branch) || !text(begun.head) || !Array.isArray(begun.boxes)) {
  return { stop: 'start-unjudged', record: begun, left: 'unknown: see the start record', next: `start did not judge #${ISSUE}: ${begun.why || startGot.verdictLine}. Fix what it names, then relaunch ${LAUNCH_AGAIN}.` }
}
const WT = begun.worktree
const BOXES = begun.boxes

let build = null
let expectedHead = begun.head
if (begun.state !== 'finished') {
  phase('Build')
  const confirmed = SEAMS !== 'implementer'
  const seamRule = confirmed
    ? `Build test-first at exactly the seams on the SEAMS line, which Adam confirmed: write tests at those seams and at no other, add no seam, and give each box the seam that lists it. A box on the noTest list gets no test; its why says so. Return seams as [] (they are the confirmed ones).`
    : `Nobody confirmed seams for this ticket (seams "implementer"): choose them yourself at the ticket's public interfaces, as an overnight lane does, and list each in your return's seams with its name, its interface and the boxes it covers.`
  const buildPrompt = `Build ticket #${ISSUE} (task ${begun.id}, owner ${begun.owner}): ${begun.title}. A foreground run.
Worktree: ${WT}. Branch: ${begun.branch}, ${begun.state === 'continued' ? 'continued from an earlier run: read its commits first' : 'new'}, forked at ${begun.fork}. Your port: ${PORT}.
Work only in the worktree: run every command as \`cd ${WT} && ...\` or \`git -C ${WT} ...\`. Never edit, commit, switch, stash or check out in the main checkout ${REPO}; read it, never write it, but for one directory: ${LOG}/mutations, where the mutation patches go.
BOXES: ${JSON.stringify(BOXES)}
SEAMS: ${JSON.stringify(SEAMS)}

Follow ${SKILLS}/implement/SKILL.md (read it; it is not callable) with these changes:
- The ticket is issue #${ISSUE}: \`cd ${REPO} && gh issue view ${ISSUE} --json title,body\`; the body is the spec. The open boxes are on the BOXES line, numbered from 1 in that order.
- Seams: ${seamRule}
- Do not run its code-review step: a later step of this run reviews.
- Every commit's message carries the line \`Ticket: #${ISSUE}\` on a line of its own.
- A mutation is a patch. Break each guard you write once, on purpose: make the break, run its test project and see it fail, save \`git -C ${WT} diff\` as ${LOG}/mutations/<k>.patch, beside it write ${LOG}/mutations/<k>.json as {"box": <the open box it proves, numbered from 1>, "project": "<the test project or companion that should fail, a path in the worktree>"}, and restore the file (\`git -C ${WT} checkout -- <file>\`) before the next. <k> is letters, digits, '.', '_' and '-'. A later change to a file a saved patch touches saves that patch again by the same rule, so it applies to the new head and still breaks the same guard. This run re-runs every patch itself.
- Run single test projects and companions while you work. Do not run \`tools/check.sh --full\`: this run's check runs it once you return.
- Write nothing to the tracker (no comment, label, tick, assignee or close) and push nothing.
- Commit everything before you return: \`git -C ${WT} status --porcelain\` prints nothing.
- Needs a path the owner does not hold, or a human-owned decision: commit what exists and return outcome "needs-human" with the question in question.
- Cannot finish: commit what exists and return outcome "failed", saying what blocks it in summary.

Return outcome, branch, forkSha (${begun.fork}), headSha (\`git -C ${WT} rev-parse HEAD\` after your last commit), complete, outstanding ("" if none), summary, question ("" if none), delegatedDecisions, changeRequests ([] if none), changedFiles (\`git -C ${WT} diff --no-renames --name-only ${begun.fork} HEAD\`), lastLine ("": the check is this run's), tests (for each box with a test: the box number, the test's name, and its test project or companion), seams, and mutations (each patch you saved: its name <k>, its box and its project).`
  build = await agent(buildPrompt, { label: 'build', phase: 'Build', schema: BUILD })
  if (!build || build.outcome === 'failed' || build.outcome === 'skipped') {
    const r = await release('release run, branch kept')
    return {
      stop: 'failed', build, release: r.record, left: r.left,
      next: `The build ${build ? 'returned ' + build.outcome + ': ' + build.summary : 'agent returned nothing'}. The branch ${begun.branch} is kept in ${WT}; read its commits, then relaunch ${LAUNCH_AGAIN}.`,
    }
  }
  if (build.outcome === 'needs-human') {
    return { stop: 'question', build, question: build.question, left: `assigned, branch ${begun.branch} kept in ${WT}`, next: `Put this to Adam: ${build.question} Then relaunch ${LAUNCH_AGAIN}.` }
  }
  expectedHead = build.headSha
}

// ---------------------------------------------------------------- the check, the proof, the pre-review
// A check courier asked until its record has a verdict. Its kind routes the run: pass on the head
// this run expects, red, or one of the stops a repair cannot settle.
let round = 0
const checkLabel = (ask) => (round ? `check:r${round}:${ask}` : `check:${ask}`)
const checkCommand = `${TOOL} check ${ISSUE} --port ${PORT}`
const runCheck = async (expected) => {
  phase('Check')
  let checked = null
  let got = null
  for (let ask = 1; ask <= CHECK_ASKS; ask++) {
    got = await courier('check', checkCommand, checkLabel(ask), 'Check')
    checked = recordOf(got, 'check')
    if (!checked) return { kind: 'garbled', got }
    if (checked.state !== 'running') break
    log(`check: still running after ask ${ask} of ${CHECK_ASKS}`)
  }
  const passed = checked.state === 'pass' && checked.verdict === 'pass' && got.exit === 0 && text(checked.head)
  if (checked.state === 'running') return { kind: 'running', checked, got }
  if (passed) return { kind: checked.head === expected ? 'pass' : 'mismatch', checked, got }
  if (checked.state === 'fail' && text(checked.log)) return { kind: 'red', checked, got }
  return { kind: 'unjudged', checked, got }
}

// Findings: review-queue.js's FINDING shape. A finding's key is its axis, place and title; the run
// keeps every key it has seen, a refuted finding's included, and drops a finding whose key it has
// seen. A finding the script makes from prove's record names, in its place, the head prove ran on:
// it is a fact about that head, so a box still unproved on a later head is a new finding there and is
// never dropped as the earlier head's (#1566, named in its Answer).
const FINDING = {
  type: 'object',
  properties: {
    axis: { type: 'string', enum: ['Standards', 'Spec'] }, title: { type: 'string' }, where: { type: 'string' },
    checked: { type: 'string' }, status: { type: 'string', enum: ['verified', 'suspected'] }, severity: { type: 'string' },
  },
  required: ['axis', 'title', 'where', 'checked', 'status', 'severity'],
}
const AXIS = {
  type: 'object',
  properties: { axis: { type: 'string', enum: ['Standards', 'Spec'] }, sourcesRead: { type: 'array', items: { type: 'string' } }, findings: { type: 'array', items: FINDING } },
  required: ['axis', 'sourcesRead', 'findings'],
}
const SKEPTIC = { type: 'object', properties: { refuted: { type: 'boolean' }, why: { type: 'string' } }, required: ['refuted', 'why'] }
const REPAIR = {
  type: 'object',
  properties: {
    headSha: { type: 'string' }, summary: { type: 'string' },
    fixed: { type: 'array', items: { type: 'string' } },
    notFixed: { type: 'array', items: { type: 'object', properties: { title: { type: 'string' }, why: { type: 'string' } }, required: ['title', 'why'] } },
    resaved: { type: 'array', items: { type: 'string' } },
  },
  required: ['headSha', 'summary', 'fixed', 'notFixed', 'resaved'],
}
const norm = (s) => String(s).toLowerCase().replace(/\s+/g, ' ').trim()
const keyOf = (f) => `${norm(f.axis)}|${norm(f.where)}|${norm(f.title)}`
const seen = new Set()
const firstSeen = (f) => {
  const k = keyOf(f)
  if (seen.has(k)) return false
  seen.add(k)
  return true
}
const findings = { repaired: [], outstanding: [], refuted: [] }
let lastProve = null
// Every box state but proved and no-test: a baseline-red box is a test red at the checked head, which
// no patch can prove, so it is a finding too rather than a run done with nothing outstanding.
const PROVE_GAPS = ['did-not-fail', 'did-not-apply', 'baseline-red', 'unproved']
const proveFindings = (rec) => (rec.boxes || []).filter((b) => PROVE_GAPS.includes(b.state)).map((b) => ({
  axis: 'Spec', title: `Box ${b.box}'s mutation proof is ${b.state}`, where: `box ${b.box} at ${rec.head}`,
  checked: `tools/implement_ticket.py prove on ${rec.head}: ${b.patches.length ? 'patch(es) ' + b.patches.join(', ') : 'no saved patch'}${b.text ? ', for "' + b.text + '"' : ''}`,
  status: 'verified', severity: 'high',
}))

const MUTATIONS_DIR = `${LOG}/mutations`
const REVIEW_GUIDE = `Read docs/agents/review.md first: its "The two axes' sources" names what your axis holds the diff to, and "The bar for a finding" is the bar. This is a pre-review inside an implement run, not a review record: write nothing to the tracker, no file, no branch and no commit, and return your findings.`
const axisPrompt = (axis, fork, head, from) => `${axis} pre-review of ticket #${ISSUE}'s branch ${begun.branch}, in the worktree ${WT}, by sha: fork ${fork}, head ${head}.
${REVIEW_GUIDE}
Read the diff with \`git -C ${WT} diff ${fork}...${head}\`${from !== fork ? `, and review the new commits since the last pre-review, \`git -C ${WT} diff ${from}..${head}\`; the rest is context` : ''}. No remote branch exists: read it from the worktree, by those shas.
${axis === 'Standards'
    ? 'Hold the diff to CODING_STANDARDS.md, AGENTS.md and OWNERSHIP.md.'
    : `Hold the diff to the issue's body (\`cd ${REPO} && gh issue view ${ISSUE} --json title,body\`), the plan row or spec it cites, and the contracts/ files in its area. The open boxes, numbered from 1: ${JSON.stringify(BOXES)}.`}
Return axis "${axis}", sourcesRead (every source you read), and findings: each in review-queue.js's finding shape, axis "${axis}", title (one line), where (path:line), checked (what you read or ran to check it), status "verified" when you showed it by a command or a reading that settles it, "suspected" otherwise, and an advisory severity. [] when there is none.`
const skepticPrompt = (f) => `Refute this ${f.axis} finding on ticket #${ISSUE}'s branch, in the worktree ${WT}. Read only: no file, commit or tracker write.
FINDING: ${JSON.stringify(f)}
Read the code and the source it is held to (docs/agents/review.md -> "The bar for a finding"). Return refuted true when the finding is wrong, not a defect by that bar, or when you are unsure; refuted false only when you confirm it is a defect. why says what you read.`

const repairPrompt = (list, red, head) => `Repair ticket #${ISSUE} (task ${begun.id}, owner ${begun.owner}) on branch ${begun.branch} in the worktree ${WT}, at head ${head}. A fresh context: read the branch's commits first, \`git -C ${WT} log --oneline ${begun.fork}..${head}\`.
Work only in the worktree: run every command as \`cd ${WT} && ...\` or \`git -C ${WT} ...\`. The one place outside it you may write is ${MUTATIONS_DIR}, the saved mutation patches. Never edit, commit, switch, stash or check out in the main checkout ${REPO}.
${list.length ? `FINDINGS: ${JSON.stringify(list)}\nFix each finding, or say in notFixed why it is not a defect or cannot be fixed here.` : ''}${red ? `\nThe full check is red on ${head}: ${red.why}. Its log is ${red.log}; its last lines: ${(red.tail || []).slice(-10).join(' | ')}${red.testReach ? '. tools/test_reach.py says: ' + JSON.stringify(red.testReach) : ''}. Make it pass.` : ''}
- Saved patches: a change to a file a saved patch in ${MUTATIONS_DIR} touches saves that patch again, by the build's rule, so that it applies to the new head and still breaks the same guard: make the break again, save \`git -C ${WT} diff\` over <k>.patch, keep its <k>.json, and restore the file. A box whose proof is did-not-fail needs a test that fails on its patch, or a patch that breaks the guard; one that is unproved needs a patch and its sidecar. List each patch you saved again in resaved.
- Every commit's message carries the line \`Ticket: #${ISSUE}\` on a line of its own. Run single test projects and companions; do not run \`tools/check.sh --full\`: this run's check runs it once you return.
- Write nothing to the tracker and push nothing. Commit everything before you return: \`git -C ${WT} status --porcelain\` prints nothing.
Return headSha (\`git -C ${WT} rev-parse HEAD\` after your last commit; ${head} when you changed nothing), summary, fixed (the titles fixed), notFixed and resaved.`

const ends = (extra) => ({ ...extra, findings, prove: lastProve, rounds: round })
const stopAfterCheck = (c) => {
  if (c.kind === 'garbled') return ends(garbled('check', checkCommand, c.got, `assigned, branch ${begun.branch} kept in ${WT}`))
  const checked = c.checked
  if (c.kind === 'running') return ends({ stop: 'check-running', build, check: checked, left: `assigned, branch ${begun.branch} kept in ${WT}; the check is still running`, next: `The check is still running after ${CHECK_ASKS} asks. Run \`${checkCommand}\` until it gives a verdict, then relaunch ${LAUNCH_AGAIN}.` })
  // Not a pass, and not a failure either: most likely an agent misreported its head on a branch the
  // check passed, so the issue stays assigned and the session compares the two.
  if (c.kind === 'mismatch') return ends({ stop: 'head-mismatch', build, check: checked, left: `assigned, branch ${begun.branch} kept in ${WT}`, next: `The check passed on ${checked.head}, and the ${c.whose} ${c.expected}: a pass on another head is not a pass. Compare \`git -C ${WT} log --oneline ${c.expected}...${checked.head}\`, then relaunch ${LAUNCH_AGAIN}.` })
  return ends({ stop: 'check-unjudged', build, check: checked, left: `assigned, branch ${begun.branch} kept in ${WT}`, next: `The check did not judge: ${checked.why || c.got.verdictLine}. Fix what it names, then relaunch ${LAUNCH_AGAIN}.` })
}

const MAX_REPAIRS = 2
// A run whose last check is red ends failed: release runs, and the branch is kept.
const failedRed = async (red, why) => {
  const r = await release('release run, branch kept')
  const tail = (red.tail || []).slice(-10)
  return ends({
    stop: 'red', build, check: red, release: r.record, left: r.left, log: red.log, tail,
    next: `The check is red after the run's repairs (${why}): ${red.why}. Read ${red.log}; its last lines: ${tail.join(' | ')}. Fix it on ${begun.branch} in ${WT}, then relaunch ${LAUNCH_AGAIN}.`,
  })
}

let expected = expectedHead
let whose = build ? 'build returned' : 'start record named'
let reviewedTo = null
let lastPass = null
for (;;) {
  const c = await runCheck(expected)
  if (!['pass', 'red'].includes(c.kind)) return stopAfterCheck({ ...c, expected, whose })
  const checked = c.checked
  const head = checked.head
  if (c.kind === 'pass') lastPass = checked
  let surviving = []
  if (c.kind === 'pass') {
    phase('Prove')
    const proveCommand = `${TOOL} prove ${ISSUE}`
    const pGot = await courier('prove', proveCommand, `prove:${round}`, 'Prove')
    const proved = recordOf(pGot, 'prove')
    if (!proved) return ends(garbled('prove', proveCommand, pGot, `assigned, branch ${begun.branch} at the checked head ${head} in ${WT}`))
    const ran = ['proved', 'gaps'].includes(proved.state) && proved.head === head && Array.isArray(proved.boxes)
    const badMutations = proved.state === 'fail' && proved.head === head
    if (!ran && !badMutations) {
      return ends({ stop: 'prove-unjudged', build, check: checked, proveRecord: proved, left: `assigned, branch ${begun.branch} at the checked head ${head} in ${WT}`, next: `prove did not run on ${head}: ${proved.why || pGot.verdictLine}. Run \`${proveCommand}\` yourself and read its verdict line; then relaunch ${LAUNCH_AGAIN}.` })
    }
    lastProve = proved
    const made = ran ? proveFindings(proved) : [{ axis: 'Spec', title: 'The saved mutations cannot be proved', where: `${MUTATIONS_DIR} at ${head}`, checked: `tools/implement_ticket.py prove on ${head}: ${proved.why}`, status: 'verified', severity: 'high' }]

    phase('Review')
    const fork = checked.fork
    const from = reviewedTo || fork
    const [std, spec] = await parallel(['Standards', 'Spec'].map((axis) => () => agent(axisPrompt(axis, fork, head, from), { label: `${axis.toLowerCase()}:${round}`, phase: 'Review', schema: AXIS })))
    reviewedTo = head
    const reviewed = [...((std && std.findings) || []), ...((spec && spec.findings) || [])]
    for (const [axis, got] of [['Standards', std], ['Spec', spec]]) {
      if (!got) findings.outstanding.push({ axis, title: `The ${axis} pre-review returned nothing on ${head}`, where: head, checked: 'the agent returned no result', status: 'verified', severity: 'unknown' })
    }
    // The script's own findings come from code, so no skeptic sees them; a verified one needs none.
    const fresh = [...made, ...reviewed].filter(firstSeen)
    const own = fresh.filter((f) => made.includes(f) || f.status === 'verified')
    const doubted = fresh.filter((f) => !own.includes(f))
    const verdicts = await parallel(doubted.map((f, i) => () => agent(skepticPrompt(f), { label: `skeptic:${round}:${i + 1}`, phase: 'Review', schema: SKEPTIC })))
    doubted.forEach((f, i) => {
      if (verdicts[i] && verdicts[i].refuted === true) findings.refuted.push({ ...f, refutedBecause: verdicts[i].why })
      else surviving.push(f)
    })
    surviving = [...own, ...surviving]
    if (!surviving.length) break
  }
  const red = c.kind === 'red' ? checked : null
  if (round >= MAX_REPAIRS) {
    if (red) return await failedRed(red, `${MAX_REPAIRS} repair rounds, the most a run takes, were used`)
    findings.outstanding.push(...surviving)
    break
  }
  round++
  phase('Repair')
  const repair = await agent(repairPrompt(surviving, red, head), { label: `repair:${round}`, phase: 'Repair', schema: REPAIR })
  if (!repair || !text(repair.headSha) || repair.headSha === head) {
    if (red) return await failedRed(red, `repair round ${round} changed nothing`)
    findings.outstanding.push(...surviving)
    break
  }
  // A finding the repair names in notFixed stays outstanding: its key is seen, so no later round
  // would report it again, and the repair's word is the only record that it still stands.
  const declined = new Map((repair.notFixed || []).map((x) => [norm(x.title), x.why]))
  for (const f of surviving) {
    if (declined.has(norm(f.title))) findings.outstanding.push({ ...f, notFixedBecause: declined.get(norm(f.title)) })
    else findings.repaired.push(f)
  }
  expected = repair.headSha
  whose = `repair:${round} returned`
}

// The run ends on a check that passed, with prove and the pre-review run on its head.
const passedOn = lastPass.head
return ends({
  done: true, branch: begun.branch, worktree: WT, head: passedOn, fork: lastPass.fork, seams: SEAMS, build, check: lastPass,
  left: `assigned, branch ${begun.branch} at the checked head ${passedOn} in ${WT}; nothing pushed`,
  next: `The branch is checked and pre-reviewed on ${passedOn}${findings.outstanding.length ? `, with ${findings.outstanding.length} finding(s) outstanding (findings.outstanding)` : ''}; nothing is pushed. Stop here and tell Adam: pushing the branch and opening the pull request wait on his word. Then the review-queue launch, from a fresh top-level session Adam starts (docs/agents/landing-work.md -> "Launching the review"); then \`land ${ISSUE}\`.`,
})
