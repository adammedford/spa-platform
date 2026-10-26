export const meta = {
  name: 'gate-evidence',
  description: 'Before a gate is tagged: read what the gate requires at trunk, find every test citing each exit-test id, prove each hermetic one by mutation in a throwaway worktree, audit the ladder stage\'s rows for done, and report the evidence with a DRAFT tag message as one <gate> evidence, <date> issue',
  whenToUse: 'In a foreground session, launched by scriptPath, by the integrator before tagging a gate; it never tags, merges or pushes, and a second run on the same base reuses the first run\'s mutation results',
  phases: [
    { title: 'Scope', detail: 'one agent reads the gates table, the gate register, the ladder, the stage\'s rows, the tags, origin\'s main, CI, the merges since the previous tag with their review records, and the durable results for this base' },
    { title: 'Locate', detail: 'at most three agents, ids batched: every test citing each id, its project, owner, tier and whether CI runs it, both proofs, and what would run a deployed clause' },
    { title: 'Prove', detail: 'at most maxParallel agents, items dealt round-robin across ids and cut at mutationCap, each in its own detached worktree at base: a baseline, then a mutation of what the test guards per item' },
    { title: 'Row audit', detail: 'one agent per four landed or part-landed rows: merges, Outstanding lines and the ownership check between each merge and its first parent; skipped when auditRows is false' },
    { title: 'Report', detail: 'one agent checks the checkout and creates, or edits, the <gate> evidence, <date> issue the script composed' },
  ],
}

// ---------------------------------------------------------------- what this is
// The gate-evidence spec (#80), built under #265; its proofs on real runs are #266. A gate tag is the
// record that a gate's exit test passed (docs/agents/landing-work.md -> "Gates"), and nothing
// assembled that proof: a citation of a test id is not a proof that the test can fail, CI's green
// says less than it sounds like, landed is not done, and a review can still be open (#80 -> "Problem
// Statement"). This script decides in code what can be decided in code, so the companion can hold
// it: the default gate, the refusals, each id's tier from its row in the export or the test id
// register, read from their table lines, and the ids neither tiers (#328), whether the gates table
// or the gate register defines the gate, and a register gate's ids, tiers and criteria, read from its
// table lines (#403), the id list after only and skipIds, the Locate batches, the ranking, the round-robin dealing under the cap, which results are reused, each test's and each
// id's verdict, each row's done, the verdict and its reasons, the report body and its dedupe. The
// agents read git and gh, run tests and mutations, and write the issue.
// Test companion: gate-evidence_tests.mjs beside this file.
//
// What it reuses, carried here so the script stays self-contained (#80 -> "What it reuses"):
// - the Mutate agent of review-queue.js (#84): its result shape (broken, failed, total, failingNames,
//   why), its worktree discipline (detached at the commit, removed whatever happens) and its runner
//   per kind (.NET per project, Terraform per directory, Python per companion), extended with the
//   per-project baseline, the second mutation and the cleanup block #80 -> "Phase Prove" asks for;
// - the proves-type verifier of truth-audit.js (#81), "proves: leg by leg": the id is attributed in
//   the named project, the project is in the solution and runs under CI's test step, and the test
//   does not skip on a machine condition; the Locate prompt carries those legs as its in-CI rule.
//
// Launch (Workflow tool), by scriptPath so the landed file runs and not a session's snapshot:
//   scriptPath: /Users/adammedford/Projects/spa-platform/.claude/workflows/gate-evidence.js
//   args: { "wt": "/Users/adammedford/.worktrees/gate-evidence-2026-10-22" }
// Arguments (#80 -> "Arguments"), and no others: an unknown one is refused before any agent runs.
//   gate         optional  a gate of the ladder table, e.g. "G3". Default: the first gate in the ladder
//                          whose tag is not reachable from base.
//   maxParallel  optional  positive integer, default 3: agents in flight at once, and the most Prove
//                          agents a run starts.
//   mutationCap  optional  positive integer, default 4: test-level mutation items one run performs.
//   auditRows    optional  boolean, default true: false dispatches no Row-audit agent.
//   repo         optional  the main checkout, an absolute path; default REPO below. Read at base
//                          through git, never from its working tree; written only under its log.
//   base         optional  the commit to prove, a sha or ref. Default: the commit origin reports for
//                          its main branch. A base that differs from origin's main is refused.
//   wt           optional  the root for throwaway worktrees, an absolute path outside repo; default
//                          WT below.
//   date         optional  YYYY-MM-DD, the report's date. Default: the Scope agent's `date +%F`; a
//                          Workflow script has no clock of its own.
//   only         optional  test ids: prove these of the gate's ids and no others.
//   skipIds      optional  test ids: leave these out. Either one makes the verdict withheld, since
//                          the run did not judge the whole exit test.
const A = args || {}
const KNOWN_ARGS = ['gate', 'maxParallel', 'mutationCap', 'auditRows', 'repo', 'base', 'wt', 'date', 'only', 'skipIds']
const REPO_DEFAULT = '/Users/adammedford/Projects/spa-platform'
const WT_DEFAULT = '/Users/adammedford/.worktrees/gate-evidence'
const ID_RE = /^[FNPA]-\d{2}$/
const badArguments = (a) => {
  const bad = []
  for (const k of Object.keys(a)) if (!KNOWN_ARGS.includes(k)) bad.push(`${k}: not an argument of this workflow; it takes ${KNOWN_ARGS.join(', ')}`)
  const pos = (v) => Number.isInteger(v) && v > 0
  const abs = (v) => typeof v === 'string' && v.startsWith('/') && !/(^|\/)\.\.(\/|$)/.test(v)
  if (a.gate !== undefined && !(typeof a.gate === 'string' && /^G\d+[a-z]?$/.test(a.gate))) bad.push('gate: a gate of the ladder table, e.g. "G3"')
  if (a.maxParallel !== undefined && !pos(a.maxParallel)) bad.push('maxParallel: a positive integer (default 3)')
  if (a.mutationCap !== undefined && !pos(a.mutationCap)) bad.push('mutationCap: a positive integer (default 4)')
  if (a.auditRows !== undefined && typeof a.auditRows !== 'boolean') bad.push('auditRows: true or false, a boolean (the string "false" is refused)')
  if (a.repo !== undefined && !abs(a.repo)) bad.push('repo: an absolute path to the main checkout')
  if (a.wt !== undefined && !abs(a.wt)) bad.push('wt: an absolute path outside the repository')
  const repo = a.repo || REPO_DEFAULT
  const wt = a.wt || WT_DEFAULT
  if (wt === repo || wt.startsWith(repo + '/')) bad.push(`wt: ${wt} is inside the repository; throwaway worktrees live outside it`)
  if (a.base !== undefined && !(typeof a.base === 'string' && /^[A-Za-z0-9][A-Za-z0-9._/-]*$/.test(a.base))) bad.push('base: a sha or a ref such as origin/main')
  if (a.date !== undefined && !(typeof a.date === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(a.date))) bad.push('date: YYYY-MM-DD')
  for (const k of ['only', 'skipIds']) {
    if (a[k] !== undefined && !(Array.isArray(a[k]) && a[k].length && a[k].every((x) => typeof x === 'string' && ID_RE.test(x)))) bad.push(`${k}: a non-empty array of test ids such as ["N-16"]`)
  }
  return bad
}
const BAD_LAUNCH = badArguments(A)
if (BAD_LAUNCH.length) {
  // Stops here: no agent is started, nothing is read or written.
  return { halted: 'bad launch; no agent started. Fix each argument and relaunch:\n- ' + BAD_LAUNCH.join('\n- ') }
}
const REPO = A.repo || REPO_DEFAULT
const WT = A.wt || WT_DEFAULT
const MAX_PAR = A.maxParallel || 3
const CAP = A.mutationCap || 4
const AUDIT_ROWS = A.auditRows !== false
const LOG = `${REPO}/.scratch/gate-evidence/log` // git-ignored (.gitignore: .scratch/*/log/)
const LOCATE_MAX = 3
const ROWS_PER_AUDIT = 4
const RUNNERS = ['dotnet', 'terraform', 'python']
const RESULTS = ['proved', 'did-not-fail', 'baseline-red', 'timed-out', 'could-not-run']
// What a reused result may be: a result that settles the test. A timed-out or could-not-run item is
// tried again on the next run on the same base, since "does only what is missing" includes it.
const SETTLED = ['proved', 'did-not-fail', 'baseline-red']
const ITEM_MINUTES = 20

// ---------------------------------------------------------------- slots
// The overnight workflow's slot and serial helpers, as truth-audit.js carries them: a freed slot goes
// straight to a waiter, so no more than maxParallel agents are ever in flight.
let slots = MAX_PAR
const waiters = []
const acquire = () => new Promise((res) => { if (slots > 0) { slots--; res() } else waiters.push(res) })
const release = () => { const w = waiters.shift(); if (w) w(); else slots++ }
const withSlot = async (fn) => { await acquire(); try { return await fn() } finally { release() } }
let agentsRun = 0
const run = (prompt, opts) => withSlot(() => { agentsRun++; return agent(prompt, opts) })

// ---------------------------------------------------------------- what every prompt carries
// #80 -> "House rules, carried in every prompt". Every command an agent is told to run is a backtick
// span, which is what the companion searches; the data a prompt carries is on a NAME: [json] line or
// between BEGIN and END, and is never run. No prompt names a command that pushes, merges or tags,
// even to forbid it: tags are read with for-each-ref and parents with ^1, so the companion can forbid
// those commands outright.
const HOUSE = `House rules (gate evidence; it proves and reports, and changes nothing on any branch):
- The main checkout ${REPO} is read at the base commit through git, never from its working tree: a file is \`git -C ${REPO} show <base>:<path>\`. Never build, check out, switch, stash, reset, commit or add there, and never write a file there except under ${LOG}/. Git's own bookkeeping for a fetch or a worktree is the only other change.
- Throwaway worktrees go under ${WT}/ only, detached at the commit your prompt names, and are removed before you return: \`git -C ${REPO} worktree remove --force <path>\`, then \`git -C ${REPO} worktree prune\`.
- The tracker is GitHub Issues. Run every \`gh\` command with \`cd ${REPO} &&\` in front. A multi-line body goes through a file you write under the run log and \`--body-file <that file>\`.
- Nothing is pushed, merged or tagged; no branch is created or moved; no AWS command is run, and no deployed stack, tunnel or provider is reached.
- Never log a secret, token, cookie, authorization code or state value (AGENTS.md); a runner's output you copy to a log is copied as printed and never gains one.
- A process you start binds only a port your prompt gives you (the suites bind ephemeral ports), and is stopped before you return.
- To change an existing file with Edit or Write, open it with the Read tool first in this session, not with \`cat\`, \`head\` or \`sed -n\`: for a file you will change, this rule overrides the auto-mode advice to read with Bash. Open it with Read again after anything else wrote it (a script, the check, a merge, \`git\`). Otherwise make the change with a short script written to a file under your scratchpad and run by its path. Edit and Write refuse a file not read since its last write: 16 calls went to that refusal on 2026-10-18 and 16 over the four launches from 2026-10-19 to 2026-10-21 (#109).
- Write each PHASE FILE, RESULT FILE and OUTPUT FILE line's path exactly as given.
The vocabulary is the glossary's: this run performs checks, and the only gates are the ladder's.`

// ---------------------------------------------------------------- schemas
const S = (properties, extra = {}) => ({ type: 'object', properties, required: Object.keys(properties), ...extra })
const str = { type: 'string' }
const int = { type: 'integer' }
const bool = { type: 'boolean' }
const strs = { type: 'array', items: str }
const TIER = { type: 'string', enum: ['H', 'D'] }
const SCOPE = S({
  fetched: bool, reason: str, date: str, base: str, originMain: str,
  tagsReachable: strs, tagsAll: strs,
  ladder: { type: 'array', items: S({ stage: str, gate: str, tag: str }) },
  gate: S({
    gate: str, phase: { type: 'integer', enum: [1, 2] }, defined: bool, exitTest: str, previousTag: str,
    ids: { type: 'array', items: S({ id: str, tiers: { type: 'array', items: TIER } }) },
    criteria: { type: 'array', items: S({ criterion: str, ids: strs }) },
    rows: { type: 'array', items: S({ row: str, owner: str, doneWhen: str, ids: strs }) },
  }),
  idTables: { type: 'array', items: S({ document: str, lines: strs }) },
  gateRegister: S({ exists: bool, lines: strs }),
  rowStates: { type: 'object' },
  changeRequests: { type: 'array', items: S({ file: str, rows: strs, status: str }) },
  openIssues: { type: 'array', items: S({ number: int, title: str, rows: strs, label: str, assignee: str }) },
  ci: S({ url: str, sha: str, jobs: { type: 'array', items: S({ name: str, conclusion: str }) } }),
  lastFull: S({ sha: str, url: str }),
  merges: { type: 'array', items: S({ sha: str, subject: str, pr: int, head: str, cleared: bool, record: str }) },
  durable: { type: 'array', items: { type: 'object' } },
})
const TEST = S({
  test: str, project: str, runner: { type: 'string', enum: [...RUNNERS, 'none'] }, owner: str,
  role: { type: 'string', enum: ['composed', 'owner', 'other'] }, tier: TIER, inCi: bool, notInCiWhy: str, guards: str,
})
const LOCATE = S({
  ids: { type: 'array', items: S({ id: str, tests: { type: 'array', items: TEST }, ownerProof: bool, composedProof: bool, missing: str, wouldRun: str }) },
})
const MUTATION = S({ broken: str, failed: int, total: int, failingNames: strs, citingFailed: bool })
const PROVE = S({
  items: { type: 'array', items: S({ key: str, baseline: { type: 'string', enum: ['green', 'red', 'timed-out', 'not-run'] }, timedOut: bool, mutations: { type: 'array', items: MUTATION }, why: str }) },
  cleanup: S({ worktreeRemoved: bool, listening: strs, checkoutChanged: strs }),
})
const AUDIT = S({
  rows: {
    type: 'array',
    items: S({
      row: str,
      merges: { type: 'array', items: S({ sha: str, subject: str, form: { type: 'string', enum: ['landed', 'part', 'other'] }, outstanding: strs, ownership: { type: 'string', enum: ['pass', 'fail', 'could-not-run'] }, ownershipDetail: str }) },
      why: str,
    }),
  },
  cleanup: S({ worktreeRemoved: bool, checkoutChanged: strs }),
})
const REPORTED = S({
  checkout: S({ trackedChanged: strs, newUntracked: strs }), worktreesLeft: strs,
  issue: int, url: str, posted: bool, reason: str,
})

// ---------------------------------------------------------------- the gate, in code
// The default gate (#80 -> "Arguments"): the first ladder row with a tag whose tag base does not
// reach. A ladder row whose tag is a dash (SX) names no gate.
const defaultGate = (ladder, reachable) => {
  const r = new Set(reachable)
  const row = ladder.find((l) => /^G/.test(l.gate) && l.tag && l.tag !== '—' && !r.has(l.tag))
  return row ? row.gate : ''
}
// The refusals (#80 -> "Phase Scope" and Testing Decisions 7): each ends the run with a report and
// no Locate, Prove or Row-audit agent. A gate is admitted exactly when the export's gates table or
// the gate register defines it (#403, Q3 of #364); its phase is a label and admits nothing, so a Phase 2
// gate such as G6 is admitted by its register row, and a gate with no row in either is refused. A register
// problem refuses the gate it names, and a problem naming no gate refuses a gate only the register
// could define; a gate both define is refused, since which exit test closes it would be a guess.
const refusalsOf = (scope, gate, ladderRow, register) => {
  const out = []
  if (!/^[0-9a-f]{40}$/.test(scope.base)) out.push(`base ${JSON.stringify(scope.base)} did not resolve to a commit`)
  else if (scope.base !== scope.originMain) out.push(`trunk differs from origin: base ${scope.base} is not ${scope.originMain || 'what origin reports'}, the commit origin reports for main`)
  if (!gate) out.push('every gate in the ladder is tagged at base, so there is no gate to prove')
  else if (!ladderRow) out.push(`${gate} is not a gate of the ladder table at base`)
  else {
    if ((scope.tagsAll || []).includes(ladderRow.tag)) out.push(`the tag ${ladderRow.tag} already exists, so ${gate} is closed and a tag is never corrected`)
    if (scope.gate.gate !== gate) out.push(`the Scope agent read ${scope.gate.gate || 'no gate'}, not ${gate}`)
    else {
      const inTable = !!scope.gate.defined
      const problems = register.problems.filter((p) => p.gate === gate || (!p.gate && !inTable))
      for (const p of problems) out.push(`the gate register ${GATES_DOC} could not be read for ${gate}: ${p.text}`)
      if (inTable && register.gates.has(gate)) out.push(`${gate} is defined twice: by the gates table of ${PLAN_DOC} and by the gate register ${GATES_DOC}`)
      else if (!inTable && !register.gates.has(gate) && !problems.length) out.push(`${gate} has no exit test: neither the gates table nor the gate register defines one`)
    }
  }
  return out
}

// ---------------------------------------------------------------- tiers, in code
// An id's tier comes from a row (#328): the export, docs/plan/04-tests.md, and the test id register,
// contracts/test-ids.md, which lists the ids named after the export and tiers the export's rows that
// carry none. Q37 of #349 asks that each row be checked against its source document before a tier is
// taken from it; a Workflow script cannot open a file, so the Scope agent copies the two documents'
// table lines as `grep` prints them, and the rows are read from those lines here, never from an
// agent's reading of the tables. A line the agent drops leaves its ids unknown, never tiered. The
// tables, the ranges, the
// tiers, an id in neither, a retired id and an id both documents define are decided here, so the
// companion can hold them. The exit test cell's own tier comes first, since a gate can require fewer
// tiers than a row lists (G4 requires N-01 hermetic only).
const ID_DOCS = ['docs/plan/04-tests.md', 'contracts/test-ids.md']
const REGISTER_DOC = ID_DOCS[1]
// A table's columns that only index or tier a row. A table with no other column, such as the
// register's "Tiers of the export's untiered rows", defines no check, so a row in it is supplemental
// and never a second home for its id (Q40 of #349).
const INDEX_COLUMNS = ['ID', 'Id', 'Tier', 'Recorded by']
const SEPARATOR_RE = /^\|(\s*:?-{3,}:?\s*\|)+\s*$/
const cellsOf = (line) => String(line).trim().split(/(?<!\\)\|/).slice(1, -1).map((c) => c.trim())
// The rows of one document's table lines, and what could not be read. A header is the line before a
// separator; a row with a test id first and a cell count other than its header's is named, not read.
const rowsOfLines = (document, lines) => {
  const rows = []
  const problems = []
  let header = null
  lines.forEach((line, i) => {
    if (SEPARATOR_RE.test(String(line).trim())) return
    if (SEPARATOR_RE.test(String(lines[i + 1] || '').trim())) { header = cellsOf(line); return }
    const c = cellsOf(line)
    if (!c.length || !idsOfCell(c[0]).length) return
    if (!header || c.length !== header.length) {
      problems.push(`a row of ${document} for ${c[0].replace(/`/g, '')} has ${c.length} cells where its table's header has ${header ? header.length : 'none'}, so it was not read`)
      return
    }
    const t = header.indexOf('Tier')
    const rb = header.indexOf('Recorded by')
    rows.push({
      document, id: c[0].replace(/`/g, ''), tier: t < 0 ? '' : c[t], recordedBy: rb < 0 ? '' : c[rb],
      defines: header.some((h) => !INDEX_COLUMNS.includes(h)),
      retired: document === REGISTER_DOC && c.some((x) => /\bretired\b/i.test(x)),
    })
  })
  return { rows, problems }
}
// Every row of both documents, from the Scope agent's idTables. A document missing, empty or given
// twice, and table lines of any other document, are named: a check that cannot read its input fails
// closed (CODING_STANDARDS.md -> "A check fails closed").
const idRowsOf = (tables) => {
  const rows = []
  const problems = []
  for (const t of tables) {
    if (!ID_DOCS.includes(t.document)) problems.push(`the Scope agent returned table lines of ${JSON.stringify(t.document)}, which is neither ${ID_DOCS.join(' nor ')}, so they were not read`)
  }
  for (const d of ID_DOCS) {
    const given = tables.filter((t) => t.document === d)
    if (given.length !== 1 || !given[0].lines.length) { problems.push(`the Scope agent returned ${given.length > 1 ? 'more than one copy of' : 'no table line of'} ${d}, so no id it lists could be tiered from it`); continue }
    const r = rowsOfLines(d, given[0].lines)
    rows.push(...r.rows)
    problems.push(...r.problems)
  }
  return { rows, problems }
}
const ID_CELL_RE = /^([FNPA])-(\d{2})(?: to \1-(\d{2}))?$/
const idsOfCell = (cell) => {
  const m = ID_CELL_RE.exec(String(cell).replace(/`/g, '').trim())
  if (!m) return []
  const a = Number(m[2])
  const b = m[3] ? Number(m[3]) : a
  return b < a ? [] : Array.from({ length: b - a + 1 }, (_, i) => `${m[1]}-${String(a + i).padStart(2, '0')}`)
}
// "H", "D" or "H, D"; anything else is null, an unreadable cell, and never a guess.
const tiersOfCell = (cell) => {
  const t = String(cell).split(/[\s,]+/).filter(Boolean)
  return t.length && t.every((x) => x === 'H' || x === 'D') ? ['H', 'D'].filter((x) => t.includes(x)) : null
}
// id -> its rows in either document, each with its Tier cell read; a row with no Tier cell gives no tier.
const tierIndex = (rows) => {
  const idx = new Map()
  for (const r of rows) {
    for (const id of idsOfCell(r.id)) {
      if (!idx.has(id)) idx.set(id, [])
      idx.get(id).push({ document: r.document, cell: r.tier, tiers: String(r.tier).trim() ? tiersOfCell(r.tier) : [], defines: r.defines, retired: r.retired, recordedBy: r.recordedBy })
    }
  }
  return idx
}
// One exit-test id: { tiers } when it can be judged, else { why }, a reason the verdict names. An id
// in neither document is unknown whatever tier the cell gives it: no row defines what it checks. A
// retired id, and an id both documents define, are steering errors to surface whatever the cell says
// (Q40 of #349). kind marks the two a gate register's definition is refused for (Q9 of #364).
const tiersFor = (x, idx) => {
  const all = idx.get(x.id)
  if (!all) return { kind: 'unknown', why: `${x.id} is in neither ${ID_DOCS.join(' nor ')}, so it is unknown: it was not tiered or judged` }
  const retired = all.find((gv) => gv.retired)
  if (retired) return { kind: 'retired', why: `${x.id} is retired in ${REGISTER_DOC}, so it was not judged${retired.recordedBy ? ` (recorded by ${retired.recordedBy})` : ''}` }
  if (ID_DOCS.every((d) => all.some((gv) => gv.document === d && gv.defines))) return { why: `${x.id} is listed by both ${ID_DOCS.join(' and ')}, so it was not judged` }
  if (x.tiers.length) return { tiers: ['H', 'D'].filter((t) => x.tiers.includes(t)) }
  const given = all.filter((gv) => !gv.tiers || gv.tiers.length)
  const bad = given.find((gv) => !gv.tiers)
  if (bad) return { why: `${x.id}'s Tier cell ${JSON.stringify(bad.cell)} in ${bad.document} is not H, D or both, so it was not judged` }
  const sets = [...new Set(given.map((gv) => gv.tiers.join(', ')))]
  if (!sets.length) return { why: `the exit test gives ${x.id} no tier and no row of ${ID_DOCS.join(' or ')} gives it one, so it was not judged` }
  if (sets.length > 1) return { why: `the exit test gives ${x.id} no tier and its rows disagree (${given.map((gv) => `${gv.document}: ${gv.cell}`).join('; ')}), so it was not judged` }
  return { tiers: sets[0].split(', ') }
}

// ---------------------------------------------------------------- the gate register, in code
// A gate defined after the frozen export lives in the gate register, contracts/gates.md (#403; Q1 of
// #364), read beside docs/plan/01-plan.md's gates table. Its ids are data, not prose (Q6), so the
// missing, unknown and retired cases are decided here from the table lines the Scope agent copies,
// as #328's tiers are. The shape this reader takes, which the register's rows must have:
//
//   | Gate | Proves | Exit test | Unblocks | Recorded by |     a gates table of one row: the Exit
//   | --- | --- | --- | --- | --- |                            test cell is the sentence the tag
//   | G6 | ... | ... | ... | ... |                              message quotes
//
//   | Id | Tiers required |                                     beneath it, one test id a row, its
//   | --- | --- |                                              tiers H, D or "H, D"
//   | N-28 | H |
//
//   | Criterion | Ids |                                        optional, beneath that: each criterion
//   | --- | --- |                                              with the ids of the table above that
//   | P2-1 ... | N-28, F-07 |                                   prove it, "—" for none (Q7)
//
// Each gate takes a section of its own with those tables. An ids or criteria table belongs to the
// one-row gates table above it; anything this cannot attribute, or cannot read, is a problem, which
// refuses the gate it names, or, naming none, a gate only the register could define (refusalsOf).
// An absent file is no register, not a problem: the export's gates need none, and a gate only the
// register could define is then refused as having no exit test; a file that exists and comes back
// with no table line is a problem, since the Scope agent
// dropped it (CODING_STANDARDS.md -> "A check fails closed").
const GATES_DOC = 'contracts/gates.md'
const PLAN_DOC = 'docs/plan/01-plan.md'
const GATE_RE = /^G\d+[a-z]?$/
const REGISTER_TABLES = { gates: ['Gate', 'Proves', 'Exit test', 'Unblocks', 'Recorded by'], ids: ['Id', 'Tiers required'], criteria: ['Criterion', 'Ids'] }
const NONE_CELL = /^(—|-|none)?$/i
const registerOfLines = (reg) => {
  const gates = new Map()
  const problems = []
  const problem = (gate, text) => problems.push({ gate, text })
  if (!reg || !reg.exists) return { gates, problems }
  const lines = reg.lines || []
  if (!lines.length) { problem('', `the gate register ${GATES_DOC} exists at base, but the Scope agent returned none of its table lines`); return { gates, problems } }
  let kind = null // the kind of the table the line is in
  let header = []
  let gateRows = [] // the gates of the last gates table
  let owner = null // the gate the current ids or criteria table belongs to
  lines.forEach((line, i) => {
    const t = String(line).trim()
    if (SEPARATOR_RE.test(t)) return
    if (SEPARATOR_RE.test(String(lines[i + 1] || '').trim())) {
      header = cellsOf(t)
      kind = Object.keys(REGISTER_TABLES).find((k) => REGISTER_TABLES[k].join('|') === header.join('|')) || 'other'
      if (kind === 'other') problem('', `a table of ${GATES_DOC} with the header ${JSON.stringify(header.join(' | '))} is none of the register's tables (${Object.values(REGISTER_TABLES).map((h) => h.join(' | ')).join('; ')}), so it was not read`)
      if (kind === 'gates') { gateRows = []; owner = null }
      if (kind === 'ids' || kind === 'criteria') {
        owner = gateRows.length === 1 ? gateRows[0] : null
        if (!owner) {
          const what = `${kind === 'ids' ? 'an' : 'a'} ${REGISTER_TABLES[kind].join(' | ')} table follows a gates table of ${gateRows.length} rows, so whose ${kind} it lists is unclear`
          if (gateRows.length) for (const gname of gateRows) problem(gname, what); else problem('', what)
        } else if (kind === 'ids' && gates.get(owner).idTables++) problem(owner, `${owner} has more than one Id | Tiers required table`)
      }
      return
    }
    const c = cellsOf(t)
    if (!c.length || kind === 'other') return
    if (!kind) { problem('', `a table line of ${GATES_DOC} comes before any table header, so it was not read: ${t}`); return }
    const first = c[0].replace(/`/g, '')
    if (c.length !== header.length) { problem(kind === 'gates' && GATE_RE.test(first) ? first : (owner || ''), `a row of ${GATES_DOC}'s ${header.join(' | ')} table has ${c.length} cells where its header has ${header.length}, so it was not read: ${t}`); return }
    if (kind === 'gates') {
      if (!GATE_RE.test(first)) { problem('', `the gate register's Gate cell ${JSON.stringify(c[0])} is not a gate such as G6, so its row was not read`); return }
      gateRows.push(first)
      if (gates.has(first)) { problem(first, `${first} has more than one row in the gate register`); return }
      if (!c[2]) problem(first, `${first}'s Exit test cell is empty`)
      gates.set(first, { gate: first, proves: c[1], exitTest: c[2], unblocks: c[3], recordedBy: c[4], ids: [], criteria: [], idTables: 0 })
      return
    }
    if (!owner) return // named once, at its header
    const gd = gates.get(owner)
    if (kind === 'ids') {
      if (!ID_RE.test(first)) { problem(owner, `${owner}'s ids table names ${JSON.stringify(c[0])}, which is not one test id`); return }
      const tiers = tiersOfCell(c[1])
      if (!tiers) { problem(owner, `${owner}'s Tiers required cell for ${first} reads ${JSON.stringify(c[1])}, which is not H, D or both`); return }
      if (gd.ids.some((x) => x.id === first)) { problem(owner, `${owner}'s ids table lists ${first} twice`); return }
      gd.ids.push({ id: first, tiers })
      return
    }
    const named = NONE_CELL.test(c[1].trim()) ? [] : c[1].split(',').map((x) => x.replace(/`/g, '').trim())
    for (const id of named) if (!ID_RE.test(id)) problem(owner, `${owner}'s criterion ${JSON.stringify(c[0])} names ${JSON.stringify(id)}, which is not a test id`)
    gd.criteria.push({ criterion: c[0], ids: named.filter((id) => ID_RE.test(id)) })
  })
  for (const gd of gates.values()) {
    if (!gd.ids.length) problem(gd.gate, `${gd.gate}'s row names no id: its Id | Tiers required table is missing or empty`)
    for (const cr of gd.criteria) for (const id of cr.ids) if (!gd.ids.some((x) => x.id === id)) problem(gd.gate, `${gd.gate}'s criterion ${JSON.stringify(cr.criterion)} names ${id}, which its ids table does not`)
  }
  return { gates, problems }
}
// What would run a deployed clause, by the gate (Q10 of #364): an export gate's deployed runs are
// Phase 1's rows; G6's are #348's sitting, whose rows run through #358's deployed class. A register
// gate with no entry here names none, rather than pointing at Phase 1's runs.
const WOULD_RUN = { G6: "#348's sitting, which runs each deployed row through #358's deployed class" }
const PHASE_1_RUNS = 'the deployed gate runs of row 6.9 over the tunnel of row 1.6b with the deploy scripts of row 1.7'
const wouldRunOf = (gate, fromRegister) => (!fromRegister ? PHASE_1_RUNS : WOULD_RUN[gate] || `nothing is named for ${gate}'s deployed clauses yet, so say so`)

// ---------------------------------------------------------------- ids, tests and items, in code
const chunks = (xs, n) => {
  const k = Math.max(1, Math.min(n, xs.length))
  const size = Math.ceil(xs.length / k)
  const out = []
  for (let i = 0; i < xs.length; i += size) out.push(xs.slice(i, i + size))
  return out
}
const slug = (s) => String(s).replace(/[^A-Za-z0-9._-]+/g, '_').slice(0, 80)
// A short, stable hash, so two keys that slug alike still name different files.
const hash = (s) => { let h = 5381; for (const c of String(s)) h = ((h * 33) ^ c.charCodeAt(0)) >>> 0; return h.toString(16).padStart(8, '0') }
const keyOf = (id, test, project) => `${id}|${test}|${project}`
const resultFile = (base, id, test, project) => `${LOG}/results/${base}/${slug(id)}__${slug(project.split('/').pop())}__${slug(test.split('.').pop())}__${hash(keyOf(id, test, project))}.json`
const ROLE_RANK = { composed: 0, owner: 1, other: 2 }
// The ranking (#80 -> "Phase Locate"): the composed-suite test first, then the owner's component
// test, then the rest; ties by name, so a rerun deals the same list.
const ranked = (tests) => [...tests].sort((a, b) => ROLE_RANK[a.role] - ROLE_RANK[b.role] || (a.test < b.test ? -1 : a.test > b.test ? 1 : 0))
const mutable = (t) => t.tier === 'H' && t.inCi && RUNNERS.includes(t.runner)
// The items (#80 -> "Phase Prove"): the ranked hermetic tests of every id required hermetically, then
// the hermetic twins of the deployed clauses, taken round-robin across ids so a capped run covers
// breadth before depth, and cut at the cap. A test with a settled result for this base is not dealt.
const dealItems = (order, testsById, settled, cap) => {
  const lists = order.map((id) => ranked((testsById.get(id) || []).filter(mutable)).filter((t) => !settled.has(keyOf(id, t.test, t.project))).map((t) => ({ ...t, id })))
  const all = []
  for (let r = 0; lists.some((l) => l.length > r); r++) for (const l of lists) if (l.length > r) all.push(l[r])
  return { dealt: all.slice(0, cap), untried: all.slice(cap) }
}
// Dealt to at most maxParallel agents by project: whole projects, the largest first into the
// lightest agent, so a project is built and baselined in one worktree once.
const dealAgents = (items, n) => {
  const byProject = new Map()
  for (const it of items) { if (!byProject.has(it.project)) byProject.set(it.project, []); byProject.get(it.project).push(it) }
  const groups = [...byProject.values()].sort((a, b) => b.length - a.length || (a[0].project < b[0].project ? -1 : 1))
  const bins = Array.from({ length: Math.max(0, Math.min(n, groups.length)) }, () => [])
  for (const g of groups) bins.reduce((m, b) => (b.length < m.length ? b : m), bins[0]).push(...g)
  return bins.filter((b) => b.length)
}
// One item's result from what its agent reported (#80 -> "Phase Prove"): a red baseline is baseline
// red; over time is timed out; the citing test among the failures of any mutation is proved; else
// did-not-fail, which needs a second, different mutation when the first failed nothing; a mutation
// that failed other tests but not the citing one is did-not-fail at once, since what failed was not
// the claimed guard. Anything else could not be judged.
const resultOf = (got) => {
  if (!got) return { result: 'could-not-run', why: 'the Prove agent returned nothing for this item' }
  if (got.baseline === 'red') return { result: 'baseline-red', why: got.why || 'the project failed unmutated at base' }
  if (got.timedOut || got.baseline === 'timed-out') return { result: 'timed-out', why: got.why || 'over the time budget' }
  const ms = got.mutations || []
  if (ms.some((m) => m.citingFailed)) return { result: 'proved', why: got.why || '' }
  if (ms.length >= 2 || ms.some((m) => m.failed > 0)) return { result: 'did-not-fail', why: got.why || 'the citing test passed under every mutation tried' }
  return { result: 'could-not-run', why: ms.length ? 'one mutation failed nothing and no second, different mutation was tried' : (got.why || 'no mutation was made') }
}

// ---------------------------------------------------------------- verdicts, in code
const TEST_VERDICT = {
  proved: 'proved', 'did-not-fail': 'did-not-fail', 'baseline-red': 'baseline red', 'timed-out': 'not proved: timed out',
  'could-not-run': 'not proved: could not run', deployed: 'deployed, not run here', 'not-in-ci': 'not in CI', 'not-mutated': 'not mutated this run',
}
// A deployed-tier test is deployed whatever record names it: a record claiming it proved is not
// evidence this run could have made.
const testStatus = (t, rec) => {
  if (t.tier === 'D') return 'deployed'
  if (rec) return rec.result
  if (!t.inCi) return 'not-in-ci'
  return 'not-mutated'
}
// An id's verdict at one tier. A deployed clause is never proved here: the run does not reach the
// deployed tier, whatever a test claims.
const idVerdict = (tier, tests) => {
  if (tier === 'D') return 'deployed, not run here'
  const h = tests.filter((t) => t.tier === 'H')
  if (!h.length) return 'no citing test'
  const st = h.map((t) => t.status)
  if (st.includes('baseline-red')) return 'baseline red'
  if (st.includes('did-not-fail')) return 'did-not-fail'
  if (st.includes('proved')) return 'proved'
  if (st.every((s) => s === 'not-in-ci')) return 'not in CI'
  if (st.includes('timed-out')) return 'not proved: timed out'
  if (st.includes('could-not-run')) return 'not proved: could not run'
  return 'not mutated this run'
}
// A row is done when its merges name it as landed, nothing it merged is Outstanding, its diffs stayed
// in its owner's set, no change request naming it is open, and the ids its Done-when names are proved
// at the tier the gate requires (#80 -> "Phase Row audit").
const rowDone = (row, state, audit, crs, idState) => {
  const why = []
  if (state !== 'landed') why.push(state === 'part-landed' ? 'part-landed: no merge names it as landed' : `not landed (${state || 'unknown'})`)
  if (audit) {
    const out = audit.merges.flatMap((m) => m.outstanding)
    if (out.length) why.push(`outstanding: ${out.join('; ')}`)
    for (const m of audit.merges) if (m.ownership !== 'pass') why.push(`the ownership check ${m.ownership === 'fail' ? 'fails' : 'could not run'} on ${m.sha.slice(0, 7)}${m.ownershipDetail ? `: ${m.ownershipDetail}` : ''}`)
  } else if (state === 'landed' || state === 'part-landed') why.push('not audited this run')
  for (const c of crs) why.push(`change request ${c.file} is open (${c.status})`)
  for (const id of row.ids) {
    const v = idState.get(id)
    if (!v) why.push(`${id} has no verdict in this run`)
    else if (!v.proved) why.push(`${id} is not proved at its tier (${v.verdict})`)
  }
  return why
}

// ---------------------------------------------------------------- the report, in code
const cell = (s) => String(s).replace(/\|/g, '\\|').replace(/\n/g, ' ')
const mutationWords = (rec) => {
  if (!rec || !rec.mutations || !rec.mutations.length) return '—'
  return rec.mutations.map((m) => `${m.broken}: ${m.failed} of ${m.total} failed${m.citingFailed ? ', the citing test among them' : ', not the citing test'}`).join('; ')
}
const reportText = (s) => {
  const L = []
  const list = (title, items, none = 'none') => {
    L.push('', `## ${title}`, '')
    if (items.length) items.forEach((i) => L.push(`- ${i}`)); else L.push(none)
  }
  L.push(`# ${s.title}`, '')
  L.push(`**Run:** gate ${s.gate || '(none)'}, tag ${s.tag || '(none)'}, base ${s.base || '(unresolved)'}, ${s.date}. Arguments: ${JSON.stringify(s.args)}. Run log: ${s.runLog}.`)
  L.push('', `**Verdict:** ${s.verdict}`)
  if (s.reasons.length) { L.push(''); s.reasons.forEach((r) => L.push(`- ${r}`)) }
  if (s.refused) {
    L.push('', `## Refused`, '', 'The run stopped after Scope: no test was located or mutated and no row was audited.')
  } else {
    L.push('', '## Evidence', '', '| Id | Tests | Tier required | Tier exercised | In CI | Mutation | Verdict |', '| --- | --- | --- | --- | --- | --- | --- |')
    for (const r of s.table) L.push(`| ${r.id} | ${cell(r.tests)} | ${r.required} | ${r.exercised} | ${cell(r.inCi)} | ${cell(r.mutation)} | ${cell(r.verdict)} |`)
    if (s.criteria.length) list('Acceptance criteria (sign-off is the integrator\'s)', s.criteria.map((c) => `${c.criterion}: ${c.ids.join(', ') || 'no proving ids'}`))
    list('Ids with no failing-capable proof', s.noProof)
    list('Rows not done', s.rowsNotDone.map((r) => `${r.row} (${r.owner}): ${r.why.join('; ')}`))
  }
  L.push('', '## Open reviews', '', `No gate tag is cut while any review of a change it covers is open (docs/agents/landing-work.md -> "Gates"). Merges since ${s.previousTag || 'the previous gate tag'}:`, '')
  if (s.merges.length) for (const m of s.merges) L.push(`- ${m.sha.slice(0, 12)} ${m.subject}: ${m.pr ? `pull request ${'#' + m.pr}, ` : 'no pull request, '}${m.cleared ? `cleared by ${m.record}` : `open${m.record ? ` (${m.record})` : ''}`}`)
  else L.push('none')
  L.push('', '## DRAFT tag message', '', 'A draft for the tagger, not a record: the tag is cut by hand, and only once nothing above is withheld.', '', '```text', ...s.draft, '```')
  // The tracked-file half is not asserted here: the Report agent's checkout line below is its evidence,
  // and it can say a file changed.
  L.push('', '## What this run did not do', '', 'It did not tag, merge or push, applied nothing in AWS and ran no deployed suite: no prompt of this run asks for any of them. Its writes are meant to be its log and its throwaway worktrees only; the checkout line below says what the Report agent found.', '', 'CHECKOUT: pending')
  return L.join('\n')
}
const draftOf = (s) => {
  const proved = s.table.filter((r) => r.verdict === 'proved').map((r) => r.id)
  const deployed = s.table.filter((r) => /deployed/.test(r.verdict)).map((r) => r.id)
  const skipped = (s.ci.jobs || []).filter((j) => j.conclusion !== 'success').map((j) => `${j.name} ${j.conclusion}`)
  return [
    `${s.gate}: ${s.exitTest}, proved by ${s.ci.url || '<CI run URL of base>'}`,
    '',
    `Exit evidence: hermetically proved by mutation at ${s.base}: ${proved.join(', ') || 'none'}.${deployed.length ? ` Deployed: ${deployed.join(', ')}, from <the deployed run's saved trace, cookie and storage dump, gateway log and plans>.` : ''}`,
    '',
    `CI of base: ${s.ci.url || 'not found'}${skipped.length ? `; it skipped or did not pass: ${skipped.join(', ')}` : ''}.`,
    `Deciding CI: ${s.lastFull.url || '<the latest first-parent run where Build and test and Terraform ran and passed>'}${s.lastFull.sha ? ` (${s.lastFull.sha.slice(0, 12)})` : ''}`,
    '',
    deployed.length ? 'No AWS apply, deployed evidence, or live provider authentication is claimed beyond the deployed run named above.' : 'No AWS apply, deployed evidence, or live provider authentication is claimed.',
  ]
}

// ---------------------------------------------------------------- prompts
const scopePrompt = (wantGate) => `Scope: read what the gate requires, at base, and what stands around it (#80 -> "Phase Scope"). You prove nothing and write nothing outside the run log and one throwaway worktree.

${HOUSE}

GATE: ${JSON.stringify(wantGate)}
BASE ARGUMENT: ${JSON.stringify(A.base || '')}
DATE ARGUMENT: ${JSON.stringify(A.date || '')}

1. \`git -C ${REPO} fetch origin --tags\`. If it fails, stop: fetched false, reason its error, every other field empty. originMain is \`git -C ${REPO} ls-remote origin refs/heads/main\`, its sha. base is the BASE ARGUMENT resolved with \`git -C ${REPO} rev-parse --verify <it>^{commit}\`, or originMain when it is "". date is the DATE ARGUMENT, or \`date +%F\` when it is "". Record the checkout as it is now, \`mkdir -p ${LOG}/<date>-<base's first 12>\` and \`git -C ${REPO} status --porcelain --untracked-files=all > ${LOG}/<date>-<base's first 12>/checkout-at-start.txt\`.
2. tagsReachable: \`git -C ${REPO} for-each-ref --merged <base> --format=%(refname:short) refs/tags\`. tagsAll: \`git -C ${REPO} for-each-ref --format=%(refname:short) refs/tags\` together with the names \`git -C ${REPO} ls-remote --tags origin\` prints.
3. ladder: docs/roadmap.md at base, its ladder table, one {stage, gate, tag} per row in order, tag without backticks ("—" where it has none).
4. gate: the GATE, or, when it is "first untagged", the first ladder row whose tag is not in tagsReachable. From docs/plan/01-plan.md at base, the gates table's row for it: defined true, and exitTest, the exit test cell verbatim; when that table has no row for it, defined false, exitTest "", ids [] and criteria [], since the script reads a gate defined after the export from gateRegister below and never from your reading. phase is a label only, 1 for a gate of that table and 2 for G6 to G8, which Phase 2 adds; it admits or refuses nothing. ids: the exit test split into test ids, each with the tiers the cell itself requires of it ("H" hermetic, "D" deployed; "in both tiers" is both; a range "X to Y" names every id between; "the deployed subset" is every N- id whose row in docs/plan/04-tests.md or contracts/test-ids.md has a Tier cell that includes D, plus any it adds), and tiers [] for an id the cell gives no tier: never tier one yourself, since the script tiers it from idTables. idTables: two entries, one per document that lists test ids, each {document, lines}: document "docs/plan/04-tests.md" (the export) with lines every line \`git -C ${REPO} show <base>:docs/plan/04-tests.md | grep -E '^[|]'\` prints, and document "contracts/test-ids.md" (the test id register) with lines every line \`git -C ${REPO} show <base>:contracts/test-ids.md | grep -E '^[|]'\` prints, each in the order printed, verbatim. Copy the lines as printed and never drop, add, reorder or edit one: the script reads the tables, the ranges and the tiers from them. gateRegister: the gate register, ${GATES_DOC}, which defines the gates added after the export: exists, whether \`git -C ${REPO} cat-file -e <base>:${GATES_DOC}\` succeeds; lines, when it does, every line \`git -C ${REPO} show <base>:${GATES_DOC} | grep -E '^[|]'\` prints, in the order printed, verbatim, under the same rule as idTables, else []. The script reads each gate's row, its ids and tiers and its criteria from those lines, so copy them whatever gate you were asked for. criteria: for a gate whose exit test names the acceptance criteria, each criterion of docs/plan/01-plan.md's acceptance table with its proving ids, else []. previousTag: the tag of the nearest earlier ladder row that is in tagsReachable. rows: the ladder stage's **Plan rows.** in docs/roadmap.md, each with its owner and Done-when clause from docs/plan/03-workstreams-and-tasks.md and the test ids that clause names.
5. rowStates: in a throwaway worktree at base, \`git -C ${REPO} worktree add --detach ${WT}/scope-<base's first 12> <base>\`, run \`python3 -I tools/roadmap.py --json\` there and return its "rows" object as it prints it; then remove the worktree. The roadmap script is the derivation: never derive a row's state yourself.
6. changeRequests: each contracts/changes/*.md at base whose **Status:** line does not begin decided, rejected or withdrawn, with the plan rows it names and that status line. openIssues: \`cd ${REPO} && gh issue list --state open --limit 500 --json number,title,body,labels,assignees\`, each issue whose title or body names a row of rows or ends " evidence, <date>", with the rows it names, its first label and its first assignee ("" for none).
7. ci: the GitHub Actions run of base, \`cd ${REPO} && gh run list --commit <base> --json url,headSha,databaseId\` and \`cd ${REPO} && gh run view <id> --json jobs\`, with each job's name and conclusion. lastFull: walking \`git -C ${REPO} log --first-parent --format=%H <base>\`, the most recent commit whose run's Build and test and Terraform jobs both ran and succeeded: {sha, url}.
8. merges: every merge on \`git -C ${REPO} log --first-parent --merges --format=%H%x09%s <previousTag>..<base>\` (docs/agents/landing-work.md -> "Gates"; a direct commit, such as a generated roadmap refresh, is not one) with its subject, the pull request it merged (0 when none), and head, the merged head: the merge's second parent, \`git -C ${REPO} rev-parse <merge>^2\`. For each with a pull request: \`cd ${REPO} && gh api --paginate --slurp repos/{owner}/{repo}/issues/<pr>/comments > ${LOG}/<date>-<base's first 12>/pr-<pr>-comments.json\` and the same of \`repos/{owner}/{repo}/pulls/<pr>/reviews\`, then pipe \`{"comments": <comments>, "reviews": <reviews>}\` to \`python3 -I tools/review_record.py <head>\`, run from a throwaway worktree at base: cleared is true when it exits 0, and record is the URL it prints, or its refusal line when it does not.
9. durable: every JSON file in ${LOG}/results/<base>/, each parsed, \`cat ${LOG}/results/<base>/*.json\` ([] when the directory is absent). Never read another base's directory.
Return every field; reason is "" when the fetch succeeded.`

const locatePrompt = (ctx, ids, k, phaseFile) => `Locate ${k}: find every test that cites each id below, and say what proves it (#80 -> "Phase Locate"). You read at base and run nothing.

${HOUSE}

PHASE FILE: ${phaseFile}
Write your return value to the PHASE FILE as JSON before you return.
IDS: ${JSON.stringify(ids)}

The base is ${ctx.base}. For each id, \`git -C ${REPO} grep -n -F -e <id> ${ctx.base}\`: every xUnit fact or theory whose name, documentation or class documentation names it; every Terraform test run or assertion naming it; every Python companion naming it; the browser suite's tests. For each test: test, its full name; project, its test project, Terraform directory or companion path; runner, dotnet, terraform or python, by how CI runs it ("none" when no runner of the three runs it); owner, from OWNERSHIP.md at base; role, composed for a composed suite under tests/Gateway.*, owner for the component test of the workstream that owns the id's behaviour, other for the rest; tier, H for in-process composed and component tests and the browser suite against its local stand-in, D for a clause only the deployed binding can show; guards, the production thing it guards, in one line.
inCi, leg by leg (truth-audit's proves rule): the project is in SpaPlatform.slnx and runs under CI's test step in .github/workflows/ci.yml; it is not an empty project the suppressed zero-tests exit code hides (a count of its facts); nothing filters it; nothing skips it on the machine (an attribute, an environment probe, or an early return on a missing tool); a Terraform test sits where CI's discovery step finds it, which is how the plan policy is in CI: P-01 to P-03 and P-07 are mock-provider assertions the Terraform job's test step runs with no AWS credential (the header of .github/workflows/ci.yml; ADR-0016; contracts/test-ids.md), so a plan-policy test passes this leg when that step finds it, and plan generation staying disabled is not a leg that fails. notInCiWhy names the leg that fails ("" when inCi).
For an F- or N- id: ownerProof and composedProof, whether each exists, and missing, the one that does not ("" when both do). wouldRun: for a deployed clause, what would run it (${wouldRunOf(ctx.gate, ctx.fromRegister)}); "" otherwise, and "" for P-01 to P-03 and P-07, which are hermetic and run in CI's Terraform test step (the plan policy of row 1.9 for P-01 to P-03, contracts/test-ids.md's row for P-07), so no deployed clause of theirs waits on a run.`

const provePrompt = (ctx, items, k, phaseFile) => {
  const wt = `${WT}/prove-${ctx.base.slice(0, 12)}-${k}`
  return `Prove ${k}: break what each test below guards, in your own throwaway worktree, and count what fails (#80 -> "Phase Prove"; the Mutate agent of review-queue.js, #84).

${HOUSE}

PHASE FILE: ${phaseFile}
WORKTREE: ${wt}
ITEMS: ${JSON.stringify(items.map((it) => ({ key: it.key, id: it.id, test: it.test, project: it.project, runner: it.runner, guards: it.guards, resultFile: it.resultFile })))}

1. Record \`git -C ${REPO} status --porcelain --untracked-files=no\`. If ${wt} exists, an earlier run left it: \`git -C ${REPO} worktree remove --force ${wt}\` and \`git -C ${REPO} worktree prune\`. Then \`git -C ${REPO} worktree add --detach ${wt} ${ctx.base}\`.
2. Per project, once: build it (a .NET project) or \`terraform init -backend=false\` in it (a Terraform directory), then run it unmutated: \`dotnet test --project <it>\` in ${wt}, \`terraform test\` in the directory, \`python3 -I <it>\` for a companion. Never the solution. It must pass: baseline green. If it fails, every item of that project is baseline red and you mutate nothing in it. Over ${ITEM_MINUTES} minutes is baseline timed-out.
3. Per item: read the test, then the production thing it guards (source or infrastructure definition, never the test). Make one minimal mutation of it (an inverted condition, a deleted attribute, an entry point returning its builder unchanged, two rule ids swapped). Run only that project. From the runner's summary: failed, total and failingNames; citingFailed, whether the item's test is among the failures. Then revert, \`git -C ${wt} reset --hard ${ctx.base}\` and \`git -C ${wt} clean -fd\`, and confirm \`git -C ${wt} status --porcelain\` prints nothing. When the mutation failed nothing, make a second, different mutation the same way before you give up on the item. A run over ${ITEM_MINUTES} minutes is timedOut true. broken: the mutation in words, one line.
4. Write each item's record as JSON to its resultFile, \`mkdir -p\` its directory first: {"base": "${ctx.base}", "key", "id", "test", "project", "baseline", "timedOut", "mutations", "why"}.
5. Always, whatever happened above: stop anything you started; \`git -C ${REPO} worktree remove --force ${wt}\` and \`git -C ${REPO} worktree prune\`; listening, every listener \`lsof -nP -iTCP -sTCP:LISTEN\` shows that you started; checkoutChanged, each tracked path whose \`git -C ${REPO} status --porcelain --untracked-files=no\` line differs from step 1.
Return items (one per ITEM, key as given), and cleanup; write the same JSON to the PHASE FILE.`
}

const auditPrompt = (ctx, rows, k, phaseFile) => {
  const wt = `${WT}/audit-${ctx.base.slice(0, 12)}-${k}`
  return `Row audit ${k}: audit each landed or part-landed row below for done (#80 -> "Phase Row audit"). You read at base and judge nothing the ownership check does not.

${HOUSE}

PHASE FILE: ${phaseFile}
WORKTREE: ${wt}
ROWS: ${JSON.stringify(rows)}

1. Record \`git -C ${REPO} status --porcelain --untracked-files=no\`.
2. Per row: its merges, each commit of \`git -C ${REPO} log --first-parent --format=%H%x09%s ${ctx.base}\` whose subject names the row's task; form, landed for "Merge task <id>" or GitHub's subject naming the task branch, part for "Merge part of task <id>", other for anything else; outstanding, every line of \`git -C ${REPO} log -1 --format=%B <merge>\` that begins "Outstanding:" or "Outstanding test ids:", verbatim.
3. Per merge, the ownership check between its first parent and it, read-only, in a throwaway worktree: \`git -C ${REPO} worktree add --detach ${wt} <merge>\`, then in ${wt} \`python3 -I .github/path-ownership.py <merge>^1 <merge>\`. ownership is pass when it exits 0, fail otherwise, could-not-run when it cannot be run, with ownershipDetail its last line. Remove the worktree after each merge: \`git -C ${REPO} worktree remove --force ${wt}\` and \`git -C ${REPO} worktree prune\`.
4. why: one sentence on anything you could not read.
5. cleanup: worktreeRemoved, and checkoutChanged, each tracked path whose status line differs from step 1.
Return rows (one per ROW) and cleanup; write the same JSON to the PHASE FILE.`
}

const CHECKOUT_FORMAT = { clean: '- Checkout: no tracked file changed, no new untracked file outside the run log, no worktree of this run left.', dirty: '- Checkout: tracked files changed: <tracked>. New untracked files outside the run log: <untracked>. Worktrees of this run left: <worktrees>.' }
const reportPrompt = (ctx, text, target, outFile) => `Report: check the checkout, then write the gate's evidence report and post it (#80 -> "Phase Report").

${HOUSE}

OUTPUT FILE: ${outFile}
CHECKOUT FORMAT: ${JSON.stringify(CHECKOUT_FORMAT)}
REPORT ISSUE: ${JSON.stringify(target)}

1. The checkout: \`git -C ${REPO} status --porcelain --untracked-files=all\` against ${ctx.runLog}/checkout-at-start.txt. trackedChanged, each tracked path whose status differs; newUntracked, each untracked path not in the start file and not under ${LOG}/. worktreesLeft, each path under ${WT}/ that \`git -C ${REPO} worktree list --porcelain\` still lists.
2. Write the text between BEGIN REPORT and END REPORT, without the markers, to ${outFile}, with its one line "CHECKOUT: pending" replaced by the CHECKOUT FORMAT's clean line when all three lists are empty, else by its dirty line with each list comma-separated or "none". Change nothing else in it.
${target.mode === 'edit'
    ? `3. An open issue of this title exists: \`cd ${REPO} && gh issue edit ${target.issue} --body-file ${outFile}\`. issue is ${target.issue}. Create no other.`
    : `3. \`cd ${REPO} && gh issue create --title "${target.title}" --body-file ${outFile}\`, with no label and no parent. issue is its number.`}
If GitHub cannot be reached, append the write to ${ctx.runLog}/pending.jsonl, \`jq -cn --rawfile b ${outFile} '{issue: ${target.mode === 'edit' ? target.issue : 0}, action: "${target.mode === 'edit' ? 'edit' : 'create'}", title: "${target.title}", body: $b}' >> ${ctx.runLog}/pending.jsonl\`, and say so in reason.
Return checkout, worktreesLeft, issue, url, posted and reason ("" when nothing failed).
BEGIN REPORT
${text}
END REPORT`

// ---------------------------------------------------------------- run
phase('Scope')
const wantGate = A.gate || 'first untagged'
const scope = await run(scopePrompt(wantGate), { label: 'scope', phase: 'Scope', schema: SCOPE })
if (!scope || !scope.fetched) return { halted: `the fetch of origin failed${scope && scope.reason ? `: ${scope.reason}` : ''}; progress is unverified, and nothing was proved or reported`, agents: agentsRun }
const date = A.date || scope.date
if (!/^\d{4}-\d{2}-\d{2}$/.test(date || '')) return { halted: `the Scope agent returned date ${JSON.stringify(scope.date)}; nothing was proved or reported`, agents: agentsRun }
const base = scope.base
const runLog = `${LOG}/${date}-${String(base).slice(0, 12)}`
// The script chooses the gate; the Scope agent's reading of the gates table is used only when it read
// the same one.
const gate = A.gate || defaultGate(scope.ladder || [], scope.tagsReachable || [])
const ladderRow = (scope.ladder || []).find((l) => l.gate === gate)
const tag = ladderRow ? ladderRow.tag : ''
const register = registerOfLines(scope.gateRegister)
const scopeRefusals = refusalsOf(scope, gate, ladderRow, register)
const g = scope.gate
// The gate's definition: the register's row when the register defines it (#403), its ids and tiers
// read from the register's table lines, else the Scope agent's reading of the export's gates table.
const regGate = scopeRefusals.length ? undefined : register.gates.get(gate)
const def = regGate
  ? { exitTest: regGate.exitTest, ids: regGate.ids, criteria: regGate.criteria, fromRegister: true }
  : { exitTest: (g && g.exitTest) || '', ids: (g && g.ids) || [], criteria: (g && g.criteria) || [], fromRegister: false }
const ctx = { base, date, runLog, gate, fromRegister: def.fromRegister }
// The ids this run proves: the gate's, each tiered by the exit test cell or its row, narrowed by only
// and skipIds. An id the Scope agent returned malformed, an id in neither document, a retired id, an
// id both documents define and an id no cell or row tiers are named in the verdict, and so is a
// document whose table lines could not be read: never dropped unseen, since a check that skips what
// it cannot read fails open (CODING_STANDARDS.md -> "A check fails closed"). A register definition
// naming an unknown or retired id is refused before Locate instead (Q9 of #364): proving the rest of
// a malformed exit test would read as the gate's evidence. The export's frozen exit tests keep #328's
// verdict reason, since an unknown id there is as likely the Scope agent's expansion of a prose cell.
const idRead = idRowsOf(scope.idTables || [])
const idx = tierIndex(idRead.rows)
const resolved = def.ids.map((x) => (ID_RE.test(x.id)
  ? { id: x.id, ...tiersFor(x, idx) }
  : { id: x.id, why: `the Scope agent returned ${JSON.stringify(x.id)} from the exit test as no test id, so it was not judged` }))
const refusals = [...scopeRefusals, ...(def.fromRegister ? resolved.filter((x) => x.kind).map((x) => `the gate register's definition of ${gate} names ${x.id}: ${x.why}`) : [])]
const title = `${gate || 'No gate'} evidence, ${date}`
const reportTarget = () => {
  const open = (scope.openIssues || []).find((i) => i.title === title)
  return open ? { mode: 'edit', issue: open.number, title } : { mode: 'create', issue: 0, title }
}
const writeReport = async (s) => {
  phase('Report')
  const text = reportText(s)
  const target = reportTarget()
  const outFile = `${runLog}/report.md`
  const rep = await run(reportPrompt(ctx, text, target, outFile), { label: 'report', phase: 'Report', schema: REPORTED })
  const ck = (rep && rep.checkout) || { trackedChanged: ['the Report agent returned nothing'], newUntracked: [] }
  const left = (rep && rep.worktreesLeft) || [] // empty-ok: a null Report is named in ck and in reason, so the checkout line reads dirty
  const dirty = ck.trackedChanged.length || ck.newUntracked.length || left.length
  const line = dirty
    ? CHECKOUT_FORMAT.dirty.replace('<tracked>', ck.trackedChanged.join(', ') || 'none').replace('<untracked>', ck.newUntracked.join(', ') || 'none').replace('<worktrees>', left.join(', ') || 'none')
    : CHECKOUT_FORMAT.clean
  return { issue: rep ? rep.issue : 0, url: rep ? rep.url : '', posted: rep ? rep.posted : false, reason: rep ? rep.reason : 'the Report agent returned nothing', mode: target.mode, file: outFile, text: text.replace(/^CHECKOUT: pending$/m, line), checkoutClean: !!rep && !dirty }
}
const ci = scope.ci || { url: '', sha: '', jobs: [] }
const lastFull = scope.lastFull || { sha: '', url: '' }
const merges = scope.merges || []
const openReviews = merges.filter((m) => !m.cleared)
const argsLine = Object.fromEntries(KNOWN_ARGS.filter((k) => A[k] !== undefined).map((k) => [k, A[k]]))

if (refusals.length) {
  const reasons = [...refusals, ...openReviews.map((m) => `merge ${m.sha.slice(0, 12)} since the previous tag has an open review`)]
  const report = await writeReport({
    title, gate, tag, base, date, args: argsLine, runLog, verdict: 'tag withheld', reasons, refused: true,
    table: [], criteria: [], noProof: [], rowsNotDone: [], merges, previousTag: g && g.previousTag,
    draft: draftOf({ gate, exitTest: def.exitTest, base, ci, lastFull, table: [] }),
  })
  return { gate, tag, base, date, verdict: 'tag withheld', reasons, refused: true, counts: { proved: 0, didNotFail: 0, notRunHere: 0, notMutated: 0 }, rowsNotDone: [], report, runLog, agents: agentsRun }
}

const gateIds = resolved.filter((x) => x.tiers)
// A register problem that names no gate cannot rule out that the register also defines this export
// gate, so it withholds the tag without refusing the run (refusalsOf refuses only a register gate).
const registerDoubts = register.problems.filter((p) => !p.gate).map((p) => `the gate register ${GATES_DOC} could not be read (${p.text}), so whether it also defines ${gate} is unknown`)
const unjudged = [...idRead.problems, ...resolved.filter((x) => x.why).map((x) => x.why), ...registerDoubts]
const leftOut = gateIds.filter((x) => (A.only && !A.only.includes(x.id)) || (A.skipIds && A.skipIds.includes(x.id))).map((x) => x.id)
const ids = gateIds.filter((x) => !leftOut.includes(x.id))
log(`gate-evidence ${gate} at ${base}: ${ids.length} ids; run log ${runLog}`)

phase('Locate')
const batches = chunks(ids.map((x) => x.id), LOCATE_MAX)
const located = await parallel(batches.map((b, i) => () => run(locatePrompt(ctx, b, i + 1, `${runLog}/locate-${i + 1}.json`), { label: `locate-${i + 1}`, phase: 'Locate', schema: LOCATE })))
const loc = new Map()
// An agent that returns nothing is named among the reasons, never read as an empty result
// (CODING_STANDARDS.md -> "A check fails closed"; #277): a null Locate is not ids no test cites.
const failedAgents = []
const were = (xs) => (xs.length === 1 ? 'was' : 'were')
located.forEach((r, i) => {
  if (!r || !Array.isArray(r.ids)) { failedAgents.push(`locate-${i + 1} returned nothing: ${batches[i].join(', ')} ${were(batches[i])} not located, so no test citing ${batches[i].length === 1 ? 'it' : 'them'} was proved`); return }
  for (const x of r.ids) if (batches[i].includes(x.id) && !loc.has(x.id)) loc.set(x.id, x)
})
const testsById = new Map(ids.map((x) => [x.id, ((loc.get(x.id) || {}).tests) || []]))

phase('Prove')
// Durable reuse: only this base's records, and only a settled result.
const durable = new Map()
for (const r of scope.durable || []) if (r && r.base === base && SETTLED.includes(resultOf(r).result) && typeof r.key === 'string') durable.set(r.key, { ...r, ...resultOf(r) })
const order = [...ids.filter((x) => x.tiers.includes('H')), ...ids.filter((x) => !x.tiers.includes('H'))].map((x) => x.id)
const { dealt, untried } = dealItems(order, testsById, durable, CAP)
const items = dealt.map((t) => ({ ...t, key: keyOf(t.id, t.test, t.project), resultFile: resultFile(base, t.id, t.test, t.project) }))
const agentsItems = dealAgents(items, MAX_PAR)
const proved = await parallel(agentsItems.map((its, i) => () => run(provePrompt(ctx, its, i + 1, `${runLog}/prove-${i + 1}.json`), { label: `prove-${i + 1}`, phase: 'Prove', schema: PROVE })))
const fresh = new Map()
const cleanups = []
agentsItems.forEach((its, i) => {
  const r = proved[i]
  if (r && r.cleanup) cleanups.push({ agent: `prove-${i + 1}`, ...r.cleanup })
  for (const it of its) {
    const got = ((r && r.items) || []).find((x) => x.key === it.key) // empty-ok: a null Prove leaves got undefined, and resultOf names the Prove agent on each item, could-not-run
    fresh.set(it.key, { ...(got || {}), key: it.key, ...resultOf(got) })
  }
})
const recOf = (id, t) => fresh.get(keyOf(id, t.test, t.project)) || durable.get(keyOf(id, t.test, t.project)) || null

// The evidence table, one row per id, a function of the LOCATE results and the PROVE records.
const table = []
const idState = new Map()
for (const x of ids) {
  const l = loc.get(x.id) || { tests: [], ownerProof: false, composedProof: false, missing: 'not located', wouldRun: '' }
  const tests = ranked(l.tests).map((t) => { const rec = recOf(x.id, t); return { ...t, rec, status: testStatus(t, rec) } })
  const per = x.tiers.map((tier) => ({ tier, verdict: idVerdict(tier, tests) }))
  const verdict = per.map((p) => (x.tiers.length > 1 ? `${p.tier}: ${p.verdict}` : p.verdict)).join('; ') + (per.some((p) => p.tier === 'D') && l.wouldRun ? ` (would run it: ${l.wouldRun})` : '')
  idState.set(x.id, { proved: per.every((p) => p.verdict === 'proved'), verdict })
  const proofNote = /^[FN]-/.test(x.id) && l.missing ? ` Missing proof: ${l.missing}.` : ''
  table.push({
    id: x.id, tiers: x.tiers, per, testsList: tests,
    tests: tests.length ? tests.map((t) => `${t.test} (${t.project}, ${t.owner}): ${TEST_VERDICT[t.status]}`).join('; ') + proofNote : `none${proofNote}`,
    required: x.tiers.join(', '), exercised: [...new Set(tests.map((t) => t.tier))].join(', ') || '—',
    inCi: tests.length ? tests.map((t) => (t.inCi ? 'yes' : `no: ${t.notInCiWhy}`)).join('; ') : '—',
    mutation: tests.filter((t) => t.rec && t.tier === 'H').map((t) => mutationWords(t.rec)).join('; ') || '—',
    verdict,
  })
}
const allTests = table.flatMap((r) => r.testsList.map((t) => ({ ...t, id: r.id })))
const counts = {
  proved: allTests.filter((t) => t.status === 'proved').length,
  didNotFail: allTests.filter((t) => t.status === 'did-not-fail').length,
  notRunHere: table.reduce((n, r) => n + r.per.filter((p) => p.tier === 'D').length, 0),
  notMutated: allTests.filter((t) => t.status === 'not-mutated').length,
}
const noProof = table.filter((r) => r.per.some((p) => p.tier === 'H' && p.verdict !== 'proved')).map((r) => `${r.id}: ${r.testsList.filter((t) => t.tier === 'H').map((t) => `${t.test} ${TEST_VERDICT[t.status]}`).join('; ') || 'no hermetic test cites it'}`)

phase('Row audit')
const states = scope.rowStates || {}
const auditable = g.rows.filter((r) => ['landed', 'part-landed'].includes(states[r.row]))
const auditBatches = AUDIT_ROWS ? chunks(auditable, Math.ceil(auditable.length / ROWS_PER_AUDIT)) : []
const audited = auditBatches.length && auditable.length
  ? await parallel(auditBatches.map((b, i) => () => run(auditPrompt(ctx, b.map((r) => ({ row: r.row, owner: r.owner })), i + 1, `${runLog}/audit-${i + 1}.json`), { label: `audit-${i + 1}`, phase: 'Row audit', schema: AUDIT })))
  : []
const auditByRow = new Map()
audited.forEach((r, i) => {
  const rows = auditBatches[i].map((x) => x.row)
  if (!r || !Array.isArray(r.rows)) { failedAgents.push(`audit-${i + 1} returned nothing: row${rows.length === 1 ? '' : 's'} ${rows.join(', ')} ${were(rows)} not audited`); return }
  for (const a of r.rows) if (rows.includes(a.row)) auditByRow.set(a.row, a)
  if (r.cleanup) cleanups.push({ agent: `audit-${i + 1}`, ...r.cleanup })
})
const rowsNotDone = []
for (const r of g.rows) {
  const crs = (scope.changeRequests || []).filter((c) => c.rows.includes(r.row))
  const why = AUDIT_ROWS || !['landed', 'part-landed'].includes(states[r.row])
    ? rowDone(r, states[r.row], auditByRow.get(r.row), crs, idState)
    : ['not audited: auditRows is false']
  const issues = (scope.openIssues || []).filter((i) => i.rows.includes(r.row))
  if (why.length) rowsNotDone.push({ row: r.row, owner: r.owner, why: [...why, ...issues.map((i) => `open issue ${'#' + i.number} carries it (${i.label || 'no label'}, ${i.assignee || 'unassigned'})`)] })
}

// The verdict (#80 -> "Phase Report"): withheld whenever any one of the five holds, each named.
const reasons = [...failedAgents]
for (const r of table) for (const p of r.per) if (p.verdict !== 'proved') reasons.push(`${r.id} is not proved at ${p.tier === 'H' ? 'the hermetic' : 'the deployed'} tier: ${p.verdict}`)
for (const t of allTests) if (t.status === 'did-not-fail') reasons.push(`${t.test}, citing ${t.id}, did not fail under mutation`)
for (const r of rowsNotDone) reasons.push(`row ${r.row} is not done`)
for (const m of openReviews) reasons.push(`merge ${m.sha.slice(0, 12)} since ${g.previousTag || 'the previous tag'} has an open review`)
for (const x of unjudged) reasons.push(x)
// #80 -> "Phase Prove": a red baseline means the report says trunk is not green here, per project.
for (const p of [...new Set(allTests.filter((t) => t.status === 'baseline-red').map((t) => t.project))]) reasons.push(`${p} failed unmutated at base, so trunk is not green here`)
if (!gateIds.length) reasons.push(def.ids.length ? `no test id the exit test of ${gate} names could be tiered, so nothing here proves it` : `the exit test of ${gate} names no test id, so nothing here proves it`)
if (leftOut.length) reasons.push(`only or skipIds left out ${leftOut.join(', ')}, so the run did not judge the whole exit test`)
for (const c of cleanups) {
  if (c.listening && c.listening.length) reasons.push(`${c.agent} left a listener: ${c.listening.join(', ')}`)
  if (c.checkoutChanged && c.checkoutChanged.length) reasons.push(`${c.agent} changed the checkout: ${c.checkoutChanged.join(', ')}`)
}
const verdict = reasons.length ? 'tag withheld' : 'tag ready'
const report = await writeReport({
  title, gate, tag, base, date, args: argsLine, runLog, verdict, reasons, refused: false,
  table, criteria: def.criteria, noProof, rowsNotDone, merges, previousTag: g.previousTag,
  draft: draftOf({ gate, exitTest: def.exitTest, base, ci, lastFull, table }),
})
return {
  gate, tag, base, date, verdict, reasons, refused: false, counts,
  rowsNotDone: rowsNotDone.map((r) => ({ row: r.row, why: r.why })),
  notMutatedThisRun: untried.map((t) => `${t.id}: ${t.test}`),
  reused: [...durable.keys()].filter((k) => allTests.some((t) => keyOf(t.id, t.test, t.project) === k)).length,
  report, runLog, agents: agentsRun,
}
