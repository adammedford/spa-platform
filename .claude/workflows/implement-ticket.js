export const meta = {
  name: 'implement-ticket',
  description: 'One ticket in the foreground: propose seams and stop; then, at the seams Adam confirmed, start the worktree, build test-first and run the full check, routing on the records tools/implement_ticket.py writes',
  whenToUse: 'In a foreground session, launched by scriptPath with the args object tools/implement_ticket.py prints: read prints the first launch, launch prints the second; it never pushes, merges, reviews or writes a review record',
  phases: [
    { title: 'Plan', detail: 'the first launch only: one agent reads the ticket, its plan row and the contracts and proposes seams, a test per open box, any integrator path and any human-owned question; nothing is written' },
    { title: 'Start', detail: 'a courier runs the tool\'s start and returns its record: the worktree and branch, new, continued or finished' },
    { title: 'Build', detail: 'one agent builds in the worktree, following the /implement skill at the confirmed seams, with no review step' },
    { title: 'Check', detail: 'a courier runs the tool\'s check, again while its record says running; a red check runs the tool\'s release, and a pass on another head stops with the issue still assigned' },
  ],
}

// ---------------------------------------------------------------- what this is
// The second ticket of the foreground implement harness (#1563, built under #1565): the workflow half,
// as far as a built and checked branch. tools/implement_ticket.py decides what code can decide and
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
//      seams added. That launch skips Plan and runs Start, Build and Check.
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
//   failed, or red          release run, branch kept             the check log's path and last lines
//   a pass on another head  assigned, branch kept                compare the two heads; the same line
//   check unjudged, or      assigned, branch kept                what the tool names; the same line
//     still running
//   done                    assigned, branch at the checked head hand-over, review, land
// A red check is a stop in this ticket; the repair loop is #1566's.
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
// overnight.js's IMPL, with the tests per box and the seams the build used added.
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
  },
  required: ['outcome', 'branch', 'forkSha', 'headSha', 'complete', 'outstanding', 'summary', 'question', 'delegatedDecisions', 'changeRequests', 'changedFiles', 'lastLine', 'tests', 'seams'],
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
Work only in the worktree: run every command as \`cd ${WT} && ...\` or \`git -C ${WT} ...\`. Never edit, commit, switch, stash or check out in the main checkout ${REPO}; read it, never write it.
BOXES: ${JSON.stringify(BOXES)}
SEAMS: ${JSON.stringify(SEAMS)}

Follow ${SKILLS}/implement/SKILL.md (read it; it is not callable) with these changes:
- The ticket is issue #${ISSUE}: \`cd ${REPO} && gh issue view ${ISSUE} --json title,body\`; the body is the spec. The open boxes are on the BOXES line, numbered from 1 in that order.
- Seams: ${seamRule}
- Do not run its code-review step: a later step of this run reviews.
- Every commit's message carries the line \`Ticket: #${ISSUE}\` on a line of its own.
- Run single test projects and companions while you work. Do not run \`tools/check.sh --full\`: this run's check runs it once you return.
- Write nothing to the tracker (no comment, label, tick, assignee or close) and push nothing.
- Commit everything before you return: \`git -C ${WT} status --porcelain\` prints nothing.
- Needs a path the owner does not hold, or a human-owned decision: commit what exists and return outcome "needs-human" with the question in question.
- Cannot finish: commit what exists and return outcome "failed", saying what blocks it in summary.

Return outcome, branch, forkSha (${begun.fork}), headSha (\`git -C ${WT} rev-parse HEAD\` after your last commit), complete, outstanding ("" if none), summary, question ("" if none), delegatedDecisions, changeRequests ([] if none), changedFiles (\`git -C ${WT} diff --no-renames --name-only ${begun.fork} HEAD\`), lastLine ("": the check is this run's), tests (for each box with a test: the box number, the test's name, and its test project or companion), and seams.`
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

phase('Check')
const checkCommand = `${TOOL} check ${ISSUE} --port ${PORT}`
let checked = null
let checkGot = null
for (let ask = 1; ask <= CHECK_ASKS; ask++) {
  checkGot = await courier('check', checkCommand, `check:${ask}`, 'Check')
  checked = recordOf(checkGot, 'check')
  if (!checked) return garbled('check', checkCommand, checkGot, `assigned, branch ${begun.branch} kept in ${WT}`)
  if (checked.state !== 'running') break
  log(`check: still running after ask ${ask} of ${CHECK_ASKS}`)
}
const tail = (checked.tail || []).slice(-10)
if (checked.state === 'running') {
  return { stop: 'check-running', build, check: checked, left: `assigned, branch ${begun.branch} kept in ${WT}; the check is still running`, next: `The check is still running after ${CHECK_ASKS} asks. Run \`${checkCommand}\` until it gives a verdict, then relaunch ${LAUNCH_AGAIN}.` }
}
if (checked.state === 'pass' && checked.verdict === 'pass' && checkGot.exit === 0 && text(checked.head) && checked.head === expectedHead) {
  return {
    done: true, branch: begun.branch, worktree: WT, head: checked.head, fork: checked.fork, seams: SEAMS, build, check: checked,
    left: `assigned, branch ${begun.branch} at the checked head ${checked.head} in ${WT}; nothing pushed`,
    next: `The branch is checked on ${checked.head}; nothing is pushed. The hand-over command is #1567's and not yet built, so stop here and tell Adam: pushing the branch and opening the pull request wait on his word. Then the review-queue launch, from a fresh top-level session Adam starts (docs/agents/landing-work.md -> "Launching the review"); then \`land ${ISSUE}\`.`,
  }
}
if (checked.state === 'pass' && checked.verdict === 'pass' && checkGot.exit === 0 && text(checked.head)) {
  // Not a pass, and not a failure either: most likely the build misreported its head on a branch the
  // check passed, so the issue stays assigned and the session compares the two.
  return {
    stop: 'head-mismatch', build, check: checked, left: `assigned, branch ${begun.branch} kept in ${WT}`,
    next: `The check passed on ${checked.head}, and the ${build ? 'build returned' : 'start record named'} ${expectedHead}: a pass on another head is not a pass. Compare \`git -C ${WT} log --oneline ${expectedHead}...${checked.head}\`, then relaunch ${LAUNCH_AGAIN}.`,
  }
}
if (checked.state === 'fail' && text(checked.log)) {
  const r = await release('release run, branch kept')
  return {
    stop: 'red', build, check: checked, release: r.record, left: r.left, log: checked.log, tail,
    next: `The check is red: ${checked.why}. Read ${checked.log}; its last lines: ${tail.join(' | ')}. Fix it on ${begun.branch} in ${WT}, then relaunch ${LAUNCH_AGAIN}.`,
  }
}
return { stop: 'check-unjudged', build, check: checked, left: `assigned, branch ${begun.branch} kept in ${WT}`, next: `The check did not judge: ${checked.why || checkGot.verdictLine}. Fix what it names, then relaunch ${LAUNCH_AGAIN}.` }
