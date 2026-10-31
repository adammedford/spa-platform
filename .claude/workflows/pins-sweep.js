export const meta = {
  name: 'pins-sweep',
  description: 'Inventory every package pin at origin/main, check each family in scope against nuget.org, advisories and vendor notes, trial-build each candidate move in a throwaway worktree, and file one issue per family with something to decide, plus the standing report issue; it never commits',
  whenToUse: 'In a foreground session on this Mac, launched by scriptPath, over one named group of families at a time (the full set is over the agent budget); dryRun: true writes the would-be issues to the log and nothing to GitHub',
  phases: [
    { title: 'Inventory', detail: 'one agent: fetch, a baseline worktree detached at origin/main, restore, the outdated and vulnerable listings, the advisory database, the release index, tools/pins_inventory.py, and the issue listings; nuget.org unreachable halts the run' },
    { title: 'Check', detail: 'one agent per family in scope, maxParallel at once: second source for the newest, vendor notes by URL, licence before and after, each evidence tie confirmed or refuted; a move, no move, or hold' },
    { title: 'Trial', detail: 'one agent per candidate, at most maxIssues, advisory-bearing families first then by distance: edit the pins in a throwaway worktree, restore, build, run the trial scope; the log ends TRIAL: GREEN or TRIAL: RED' },
    { title: 'File', detail: 'one agent: writes each would-be issue to the log, then, unless dryRun, makes exactly the creates, updates and comments the script chose; refreshes the report issue; removes the base worktree and checks the checkout' },
    { title: 'Report', detail: 'no agent: the script returns the base, the table, each family\'s filing action, the leftovers and whether the main checkout is clean' },
  ],
}

// ---------------------------------------------------------------- what this is
// The pins-sweep spec (#82), built under #269; its proofs on real runs are in the proof ticket filed
// beside it. Nothing collects what the pins need (#82 -> "Problem Statement"): which trail nuget.org,
// which carry an advisory, which a recorded run names. This script orchestrates; what can be
// decided in code is decided here, so the companion can hold it: the agent budget, the families in
// scope, the trial order and the cap, each issue's title, body and label, the dedupe outcome, the
// report issue and its comment. tools/pins_inventory.py gathers the facts and renders the table; the
// agents read vendors, judge, build and run gh. Test companion: pins-sweep_tests.mjs beside this file.
//
// Launch (Workflow tool), by scriptPath so the landed file runs and not a session's snapshot:
//   scriptPath: /Users/adammedford/Projects/spa-platform/.claude/workflows/pins-sweep.js
//   args: { "repo": "/Users/adammedford/Projects/spa-platform", "wt": "/Users/adammedford/.worktrees/pins-sweep",
//           "date": "2026-10-22", "families": ["aspnetcore", "mvc-testing", "fake-clock", "sdk"], "dryRun": true }
// Arguments (#82 -> "Arguments"), and no others: an unknown one is refused before any agent starts.
//   repo         required  the main checkout, an absolute path. It is read through git and never written,
//                          but for its git-ignored log directory and git's own bookkeeping.
//   wt           required  the worktree root, an absolute path outside repo: the base and trial worktrees.
//   date         required  YYYY-MM-DD; an argless new Date() throws in a Workflow script.
//   families     optional  family ids from FAMILIES below; default every .NET family.
//   maxParallel  optional  positive integer, default 3: agents in flight at once.
//   maxIssues    optional  integer >= 0, default 4: trials, and family issues created or updated.
//   maxAgents    optional  positive integer, default 10: the run's agent budget.
//   dryRun       optional  boolean, default false: no GitHub write of any kind.
//   terraform    optional  boolean, default false: add the Terraform provider family.
//   moves        optional  {family: "version"} or {family: {"<package id>": "version"}}: replaces the
//                          Check phase's candidate for that family, so a named bump is trialled.
// The documented invocation names a group and rotates (#82 -> "Arguments"): the framework group
// [aspnetcore, mvc-testing, fake-clock, sdk], the identity-and-proxy group [duende, yarp, playwright],
// and the rest [aws-sdk, opentelemetry, xunit, schema-validator, analyzers]; analyzers joined under #607.
const A = args || {}
const KNOWN_ARGS = ['repo', 'wt', 'date', 'families', 'maxParallel', 'maxIssues', 'maxAgents', 'dryRun', 'terraform', 'moves']
// The families of tools/pins_inventory.py, by id; the companion holds the two lists equal.
const FAMILIES = ['aspnetcore', 'duende', 'yarp', 'aws-sdk', 'opentelemetry', 'xunit', 'playwright', 'schema-validator', 'fake-clock', 'mvc-testing', 'analyzers', 'sdk']
const TERRAFORM = 'terraform'
const badArguments = (a) => {
  const bad = []
  for (const k of Object.keys(a)) if (!KNOWN_ARGS.includes(k)) bad.push(`${k}: not an argument of this workflow; it takes ${KNOWN_ARGS.join(', ')}`)
  const abs = (v) => typeof v === 'string' && v.startsWith('/') && !/(^|\/)\.\.(\/|$)/.test(v)
  if (!abs(a.repo)) bad.push('repo: required, the main checkout as an absolute path')
  if (!abs(a.wt)) bad.push('wt: required, the worktree root as an absolute path')
  else if (abs(a.repo) && (a.wt === a.repo || a.wt.startsWith(a.repo.replace(/\/$/, '') + '/'))) bad.push('wt: must be outside repo; a worktree is never created under the repository')
  if (!(typeof a.date === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(a.date))) bad.push('date: required, YYYY-MM-DD')
  if (a.families !== undefined) {
    if (!(Array.isArray(a.families) && a.families.length && a.families.every((f) => typeof f === 'string'))) bad.push(`families: a non-empty array of family ids, from ${FAMILIES.join(', ')}`)
    else for (const f of a.families) if (!FAMILIES.includes(f)) bad.push(`families: ${f} is not a .NET family (${FAMILIES.join(', ')})${f === TERRAFORM ? '; the provider family is terraform: true' : ''}`)
  }
  for (const [k, min, def] of [['maxParallel', 1, 3], ['maxIssues', 0, 4], ['maxAgents', 1, 10]]) {
    if (a[k] !== undefined && !(Number.isInteger(a[k]) && a[k] >= min)) bad.push(`${k}: an integer, ${min} or more (default ${def})`)
  }
  for (const k of ['dryRun', 'terraform']) if (a[k] !== undefined && typeof a[k] !== 'boolean') bad.push(`${k}: true or false, a boolean (the string "true" is refused)`)
  if (a.moves !== undefined) {
    if (!(a.moves && typeof a.moves === 'object' && !Array.isArray(a.moves))) bad.push('moves: an object from family id to a version, or to {"<package id>": "<version>"}')
    else {
      for (const [f, v] of Object.entries(a.moves)) {
        if (![...FAMILIES, TERRAFORM].includes(f)) bad.push(`moves: ${f} is not a family`)
        // The SDK is report-only (#82 -> "Families"): no trial installs one, so a move for it would be
        // dropped without a word (#269's review).
        else if (f === 'sdk') bad.push('moves: sdk is report-only; no move of the SDK is trialled')
        const ok = (s) => typeof s === 'string' && /^\d+(\.\d+){1,3}(-[0-9A-Za-z.-]+)?$/.test(s)
        if (!(ok(v) || (v && typeof v === 'object' && !Array.isArray(v) && Object.keys(v).length && Object.values(v).every(ok)))) bad.push(`moves: ${f} needs a version such as "4.0.104.4", or {"<package id>": "<version>"}`)
      }
    }
  }
  return bad
}
const BAD_LAUNCH = badArguments(A)
if (BAD_LAUNCH.length) {
  // Stops here: no agent is started, nothing is read or written.
  return { halted: 'bad launch; no agent started. Fix each argument and relaunch:\n- ' + BAD_LAUNCH.join('\n- '), agents: 0 }
}
const REPO = A.repo.replace(/\/$/, '')
const WT = A.wt.replace(/\/$/, '')
const DATE = A.date
const SCOPE = [...(A.families || FAMILIES), ...(A.terraform ? [TERRAFORM] : [])]
const MAX_PAR = A.maxParallel || 3
const MAX_ISSUES = A.maxIssues === undefined ? 4 : A.maxIssues
const MAX_AGENTS = A.maxAgents || 10
const DRY = A.dryRun === true
const MOVES = A.moves || {}
for (const f of Object.keys(MOVES)) {
  if (!SCOPE.includes(f)) return { halted: `bad launch; no agent started. moves names ${f}, which is not in this run's families (${SCOPE.join(', ')})`, agents: 0 }
}

// ---------------------------------------------------------------- the agent budget
// #82 -> "Arguments": one Inventory, one Check per family, at most maxIssues trials, one File. Counted
// before anything is dispatched; a full .NET run is 1 + 11 + 4 + 1 = 17, over the session guideline.
const AGENT_COUNT = 1 + SCOPE.length + MAX_ISSUES + 1
if (AGENT_COUNT > MAX_AGENTS) {
  return {
    halted: `the run would take ${AGENT_COUNT} agents (1 Inventory + ${SCOPE.length} Check + up to ${MAX_ISSUES} Trial + 1 File), over maxAgents ${MAX_AGENTS}; no agent started. Name a group of families, e.g. ["aspnetcore", "mvc-testing", "fake-clock", "sdk"], or lower maxIssues`,
    agentCount: AGENT_COUNT, maxAgents: MAX_AGENTS, agents: 0,
  }
}

const LOG = `${REPO}/.scratch/pins-sweep/log/${DATE}` // git-ignored (.gitignore: .scratch/*/log/)
const BASE_WT = `${WT}/pins-sweep-${DATE}-base`
const trialWt = (f) => `${WT}/pins-sweep-${DATE}-trial-${f}`
const COMPANION = `${BASE_WT}/tools/pins_inventory.py`
const REPORT_TITLE = 'Pins sweep: the state of every pin'
// Trials that run the smoke test bind one port each, from here up, by trial order.
const TRIAL_PORT = 6480
const STATUSES = ['advisory', 'blocked-by-evidence', 'held-by-decision', 'behind-minor', 'behind-patch', 'current', 'not-checked']
const ACTIONS = ['created', 'updated', 'stands', 'declined', 'skipped-cap', 'would-create', 'would-update', 'not-written']

// ---------------------------------------------------------------- slots
// The overnight workflow's slot helper: a freed slot goes straight to a waiter, so no more than
// maxParallel agents are ever in flight.
let slots = MAX_PAR
const waiters = []
const acquire = () => new Promise((res) => { if (slots > 0) { slots--; res() } else waiters.push(res) })
const release = () => { const w = waiters.shift(); if (w) w(); else slots++ }
const withSlot = async (fn) => { await acquire(); try { return await fn() } finally { release() } }
let agentsRun = 0
const run = (prompt, opts) => withSlot(() => { agentsRun++; return agent(prompt, opts) })

// ---------------------------------------------------------------- what every prompt carries
// The overnight's house rules (#82 -> "What the sweep writes"), with this run's: it commits nothing,
// edits no tracked file of the main checkout, installs no SDK or tool, applies nothing, touches no
// AWS and clears no cache. Every command an agent is told to run is a backtick span, which is what
// the companion searches; the data a prompt carries is on a NAME: [json] line or between BEGIN and
// END, and is never run.
const HOUSE = `House rules (a pins sweep; it changes nothing in the main checkout and decides no move on its own):
- The main checkout ${REPO} is Adam's. Never commit, checkout, switch, stash, reset or build there, and never write a file there except under ${LOG}/. Git's own bookkeeping for a fetch or a worktree is the only other change.
- Begin every command that works in a worktree with cd <that worktree> &&, and use git -C <path> for every git command. Worktrees are created only under ${WT}, detached, and nothing is ever committed in one.
- Never install an SDK or a tool, never clear a cache (the NuGet package cache and Playwright's browser cache are only ever added to), never run a Terraform backend, apply or AWS credential step, and never push.
- The tracker is GitHub Issues. Run every gh command with cd ${REPO} && in front. A multi-line body goes through a file under ${LOG}/ and --body-file <that file>.
- Never write a comment that begins "Confirm" or "Approved". Never log a secret, token, cookie, authorization code or state value (AGENTS.md).
- To change an existing file with Edit or Write, open it with the Read tool first in this session, not with \`cat\`, \`head\` or \`sed -n\`: for a file you will change, this rule overrides the auto-mode advice to read with Bash. Open it with Read again after anything else wrote it (a script, the check, a merge, \`git\`). Otherwise make the change with a short script written to a file under your scratchpad and run by its path. Edit and Write refuse a file not read since its last write: 16 calls went to that refusal on 2026-10-18 and 16 over the four launches from 2026-10-19 to 2026-10-21 (#109).
- Write each PHASE FILE and OUTPUT FILE line's path exactly as given.`

// ---------------------------------------------------------------- schemas
const ADVISORY = { type: 'object', properties: { id: { type: 'string' }, severity: { type: 'string' }, url: { type: 'string' }, fixed: { type: 'string' }, via: { type: 'string' } }, required: ['id', 'severity', 'url', 'fixed', 'via'] }
const PLACE = { type: 'object', properties: { path: { type: 'string' }, line: { type: 'integer' } }, required: ['path', 'line'] }
const PIN = {
  type: 'object',
  properties: {
    id: { type: 'string' }, pinned: { type: 'string' }, newest: { type: 'string' }, newestInBound: { type: 'string' }, hold: { type: 'string' }, comment: { type: 'string' },
    status: { type: 'string', enum: STATUSES }, referencedBy: { type: 'array', items: { type: 'string' } },
    evidence: { type: 'array', items: PLACE }, documents: { type: 'array', items: PLACE }, advisories: { type: 'array', items: ADVISORY },
  },
  required: ['id', 'pinned', 'newest', 'newestInBound', 'hold', 'comment', 'status', 'referencedBy', 'evidence', 'documents', 'advisories'],
}
const LISTED_ISSUE = { type: 'object', properties: { number: { type: 'integer' }, title: { type: 'string' }, marker: { type: 'string' }, move: { type: 'string' }, labels: { type: 'array', items: { type: 'string' } } }, required: ['number', 'title', 'marker', 'move', 'labels'] }
const INVENTORY = {
  type: 'object',
  properties: {
    outcome: { type: 'string', enum: ['ok', 'halt'] }, reason: { type: 'string' }, base: { type: 'string' }, sdk: { type: 'string' },
    inventoryFile: { type: 'string' }, listed: { type: 'boolean' },
    families: {
      type: 'array',
      items: { type: 'object', properties: { id: { type: 'string' }, status: { type: 'string', enum: STATUSES }, trialScope: { type: 'array', items: { type: 'string' } }, smoke: { type: 'boolean' }, pins: { type: 'array', items: PIN } }, required: ['id', 'status', 'trialScope', 'smoke', 'pins'] },
    },
    sdkRow: { type: 'object', properties: { floor: { type: 'string' }, latestSdk: { type: 'string' }, latestRuntime: { type: 'string' }, frameworkRuntime: { type: 'string' }, securityRelease: { type: 'string' }, status: { type: 'string', enum: STATUSES } }, required: ['floor', 'latestSdk', 'latestRuntime', 'frameworkRuntime', 'securityRelease', 'status'] },
    openIssues: { type: 'array', items: LISTED_ISSUE },
    closedIssues: { type: 'array', items: LISTED_ISSUE },
    report: { type: 'object', properties: { number: { type: 'integer' }, statuses: { type: 'array', items: { type: 'object', properties: { pin: { type: 'string' }, status: { type: 'string' } }, required: ['pin', 'status'] } } }, required: ['number', 'statuses'] },
  },
  required: ['outcome', 'reason', 'base', 'sdk', 'inventoryFile', 'listed', 'families', 'sdkRow', 'openIssues', 'closedIssues', 'report'],
}
const MOVE = { type: 'object', properties: { id: { type: 'string' }, from: { type: 'string' }, to: { type: 'string' } }, required: ['id', 'from', 'to'] }
const TIE = { type: 'object', properties: { path: { type: 'string' }, line: { type: 'integer' }, why: { type: 'string' } }, required: ['path', 'line', 'why'] }
const CHECK = {
  type: 'object',
  properties: {
    family: { type: 'string' }, recommendation: { type: 'string', enum: ['move', 'no-move', 'hold'] }, reason: { type: 'string' },
    candidate: { type: 'array', items: MOVE }, advisories: { type: 'array', items: ADVISORY },
    licenceBefore: { type: 'string' }, licenceAfter: { type: 'string' }, releaseNotes: { type: 'string' },
    breakingChanges: { type: 'array', items: { type: 'string' } }, evidenceTouched: { type: 'array', items: TIE }, evidenceRefuted: { type: 'array', items: TIE },
    newestConfirmed: { type: 'boolean' }, behaviour: { type: 'string' },
  },
  required: ['family', 'recommendation', 'reason', 'candidate', 'advisories', 'licenceBefore', 'licenceAfter', 'releaseNotes', 'breakingChanges', 'evidenceTouched', 'evidenceRefuted', 'newestConfirmed', 'behaviour'],
}
const TRIAL = {
  type: 'object',
  properties: {
    family: { type: 'string' }, base: { type: 'string' }, sdk: { type: 'string' }, scope: { type: 'array', items: { type: 'string' } },
    outcome: { type: 'string', enum: ['green', 'red', 'not-run'] }, logPath: { type: 'string' }, verdictLine: { type: 'string' },
    failingLines: { type: 'array', items: { type: 'string' } }, browserRevision: { type: 'string' }, worktreeRemoved: { type: 'boolean' },
  },
  required: ['family', 'base', 'sdk', 'scope', 'outcome', 'logPath', 'verdictLine', 'failingLines', 'browserRevision', 'worktreeRemoved'],
}
const FILED = {
  type: 'object',
  properties: {
    writes: { type: 'array', items: { type: 'object', properties: { key: { type: 'string' }, done: { type: 'boolean' }, number: { type: 'integer' }, url: { type: 'string' } }, required: ['key', 'done', 'number', 'url'] } },
    table: { type: 'string' },
    report: { type: 'object', properties: { number: { type: 'integer' }, url: { type: 'string' } }, required: ['number', 'url'] },
    checkout: { type: 'object', properties: { trackedChanged: { type: 'array', items: { type: 'string' } }, newUntracked: { type: 'array', items: { type: 'string' } }, headMoved: { type: 'boolean' }, refsChanged: { type: 'array', items: { type: 'string' } }, worktreesLeft: { type: 'array', items: { type: 'string' } } }, required: ['trackedChanged', 'newUntracked', 'headMoved', 'refsChanged', 'worktreesLeft'] },
    reason: { type: 'string' },
  },
  required: ['writes', 'table', 'report', 'checkout', 'reason'],
}

// ---------------------------------------------------------------- versions, in code
const vparts = (v) => String(v || '').split('-')[0].split('.').map((x) => parseInt(x, 10) || 0)
const vcmp = (a, b) => { const x = vparts(a), y = vparts(b); for (let i = 0; i < Math.max(x.length, y.length); i++) { const d = (x[i] || 0) - (y[i] || 0); if (d) return d } return 0 }
// How far a move goes: the first part that differs and by how much, so a minor step outranks any
// number of patches, as #82's "then by distance behind" reads.
const distanceOf = (moves) => moves.reduce((best, m) => {
  const x = vparts(m.from), y = vparts(m.to)
  let d = [0, 0, 0, 0]
  for (let i = 0; i < 4; i++) if ((y[i] || 0) !== (x[i] || 0)) { d[i] = (y[i] || 0) - (x[i] || 0); break }
  for (let i = 0; i < 4; i++) if (d[i] !== best[i]) return d[i] > best[i] ? d : best
  return best
}, [0, 0, 0, 0])
const cmpDistance = (a, b) => { for (let i = 0; i < 4; i++) if (a[i] !== b[i]) return b[i] - a[i]; return 0 }

// ---------------------------------------------------------------- prompts
const inventoryPrompt = () => `Inventory: read every pin at origin/main and the tracker's view of the sweep (#82 -> "Phases" -> Inventory). You write only under ${LOG}/ and the base worktree.

${HOUSE}

PHASE FILE: ${LOG}/inventory-record.json
Write your return value to the PHASE FILE as JSON before you return.
FAMILIES IN SCOPE: ${JSON.stringify(SCOPE)}
TERRAFORM: ${JSON.stringify(!!A.terraform)}

1. \`git -C ${REPO} fetch origin\`. If it fails, stop: outcome halt, reason its error, every other field empty, listed false.
2. base is \`git -C ${REPO} rev-parse origin/main\`, the full 40 hex. \`mkdir -p ${LOG}\`, then record the checkout as it is now: \`git -C ${REPO} status --porcelain --untracked-files=all > ${LOG}/checkout-at-start.txt\`, \`git -C ${REPO} rev-parse HEAD > ${LOG}/head-at-start.txt\`, \`git -C ${REPO} for-each-ref --format='%(refname) %(objectname)' > ${LOG}/refs-at-start.txt\`.
3. The baseline worktree: \`git -C ${REPO} worktree add --detach ${BASE_WT} <base>\`, then \`cd ${BASE_WT} && dotnet restore\`. sdk is \`cd ${BASE_WT} && dotnet --version\`.
4. The listings, in JSON: \`cd ${BASE_WT} && dotnet list package --outdated --format json > ${LOG}/outdated.json\` and \`cd ${BASE_WT} && dotnet list package --vulnerable --include-transitive --format json > ${LOG}/vulnerable.json\`. A restore or listing that fails because nuget.org cannot be reached is a halt (step 9).
5. GitHub's advisory database, one query per PackageVersion in ${BASE_WT}/Directory.Packages.props: \`cd ${REPO} && gh api "/advisories?ecosystem=nuget&affects=<id>@<version>" > ${LOG}/advisories-<id>.json\`, then \`jq -s add ${LOG}/advisories-*.json > ${LOG}/advisories.json\`.
6. Microsoft's release index for the SDK's channel (the major.minor of global.json's sdk.version): \`curl -fsS https://builds.dotnet.microsoft.com/dotnet/release-metadata/<channel>/releases.json -o ${LOG}/releases.json\`.
${A.terraform ? `7. The provider versions, for each provider a .terraform.lock.hcl names: \`curl -fsS https://registry.terraform.io/v1/providers/<namespace>/<name>/versions | jq '{"<namespace>/<name>": [.versions[].version]}' > ${LOG}/provider-<name>.json\`, then \`jq -s add ${LOG}/provider-*.json > ${LOG}/provider-versions.json\`.` : '7. Terraform is off: read no lock file and fetch no provider version.'}
8. The companion: \`python3 -I ${COMPANION} inventory --repo ${BASE_WT} --commit <base> --vulnerable ${LOG}/vulnerable.json --advisories ${LOG}/advisories.json --releases ${LOG}/releases.json${A.terraform ? ` --terraform --provider-versions ${LOG}/provider-versions.json` : ''} > ${LOG}/inventory.json\`. It fetches each version list from nuget.org; exit 3 means nuget.org is unreachable.
9. A halt (nuget.org unreachable at step 3, 4 or 8): \`git -C ${REPO} worktree remove --force ${BASE_WT}\`, then return outcome halt, reason the failing command and its error, and nothing else. The run ends there: a quiet run must never read as a clean one.
10. The tracker. Open issues: \`cd ${REPO} && gh issue list --state open --limit 500 --json number,title,labels,body > ${LOG}/open-issues.json\`; closed ones whose title starts "Pins:": \`cd ${REPO} && gh issue list --state closed --limit 500 --search "Pins: in:title" --json number,title,labels,body > ${LOG}/closed-issues.json\`. For each issue return {number, title, marker: the text after "**Pins family:**" on its body's line starting so, else "", move: the text after "**Pins move:**" likewise, else "", labels: their names}. openIssues lists the open issues that have a marker or whose title is "${REPORT_TITLE}"; closedIssues the closed ones with a marker. listed is true only when both commands exited 0; if either fails, listed false: an empty listing from a failed command is not an empty tracker, and the run stops on it.
11. report: the open issue titled exactly "${REPORT_TITLE}": number, and statuses, one {pin, status} per row of the table in its body (the first and fourth cells). None open: number 0, statuses [].
Return outcome ok, base, sdk, inventoryFile ${LOG}/inventory.json, listed, and from that file: families, those in scope only, each {id, status, trialScope, smoke, pins: its pins with id, pinned, newest, newestInBound, hold, comment, status, referencedBy, evidence and documents as {path, line}, advisories}; and sdkRow from its "sdk". reason is "" when nothing failed.`

const checkPrompt = (ctx, fam, phaseFile) => `Check ${fam.id}: decide this family's recommendation from the vendor's own sources (#82 -> "Phases" -> Check). You read; you write only your phase file.

${HOUSE}

PHASE FILE: ${phaseFile}
Write your return value to the PHASE FILE as JSON before you return.
BASE: ${JSON.stringify(ctx.base)}
FAMILY: ${JSON.stringify(fam)}
${fam.id === 'sdk' ? `SDK ROW: ${JSON.stringify(ctx.sdkRow)}\n` : ''}
The base worktree is ${BASE_WT}, at the base commit; read every document there or with \`git -C ${REPO} show <base>:<path>\`, never in the main checkout's working tree.
1. newestConfirmed: the companion's newest for each pin, checked against a second source, \`cd ${BASE_WT} && dotnet package search <id> --exact-match --format json\`.
2. releaseNotes: the vendor's release notes between the pinned and the proposed version, cited by URL, and breakingChanges, each one sentence with its URL. Code that rests on vendor behaviour cites the vendor (CODING_STANDARDS.md): a note you cannot fetch is said so, never remembered.
3. licenceBefore and licenceAfter: the licence expression each version's own package declares, read from \`curl -fsS https://api.nuget.org/v3-flatcontainer/<id-lowercase>/<version>/<id-lowercase>.nuspec\`. A licence you cannot read is "" and blocks the move.
4. Each evidence tie the companion found (each pin's evidence): read the line and its paragraph, and put it in evidenceTouched when it records a run, a decision or a contracted constant at the pinned version, or in evidenceRefuted with why when it does not. A document the companion did not flag that you find naming the version as evidence goes in evidenceTouched too. behaviour: from the vendor's notes, whether the proposed version changes what that evidence recorded (the handler's claim and scheme handling, the token library's refresh under derived schemes, YARP's timeout ceiling constant), or "" when nothing is tied.
5. advisories: each with id, severity, url, the fixed version ("" if none) and via (the transitive package, or "").
6. recommendation: move (candidate: every pin of the family to one target set, {id, from, to}), no-move with its reason, or hold. A pin held by a decision (its hold) moves only inside the decision's bound: its newestInBound; the newest outside it is reported with its licence and never proposed. ${fam.id === 'sdk' ? 'The SDK is report-only: never a move. Recommend hold when the release index names a newer security release than the runtime the framework pins track, else no-move.' : ''}${fam.id === TERRAFORM ? 'The providers move within their lock files\' constraints only.' : ''}`

const editLines = (moves, wt) => moves.map((m) => `\`cd ${wt} && sed -i '' 's|Include="${m.id}" Version="${m.from}"|Include="${m.id}" Version="${m.to}"|' Directory.Packages.props\``).join(', ')
const trialPrompt = (ctx, t, port, phaseFile) => {
  const wt = trialWt(t.family)
  const logf = `${LOG}/trial-${t.family}.log`
  if (t.family === TERRAFORM) return terraformTrialPrompt(ctx, t, wt, logf, phaseFile)
  const tests = t.scope.includes('solution')
    ? [`\`cd ${wt} && dotnet test --solution SpaPlatform.slnx --ignore-exit-code 8 >> ${logf} 2>&1\``]
    : t.scope.map((p) => `\`cd ${wt} && dotnet test --project ${p} >> ${logf} 2>&1\``)
  if (t.smoke) tests.push(`\`cd ${wt} && tools/smoke.sh Debug ${port} >> ${logf} 2>&1\``)
  const moveText = t.moves.map((m) => `${m.id} ${m.from} -> ${m.to}`).join('; ')
  return `Trial ${t.family}: build and test this move in a throwaway worktree (#82 -> "Phases" -> Trial). Nothing is ever committed in it, and it is removed at the end.

${HOUSE}

PHASE FILE: ${phaseFile}
Write your return value to the PHASE FILE as JSON before you return.
TRIAL: ${JSON.stringify({ family: t.family, base: ctx.base, moves: t.moves, scope: t.scope, smoke: t.smoke, port: t.smoke ? port : 0 })}

1. \`git -C ${REPO} worktree add --detach ${wt} ${ctx.base}\`.
2. The log's first lines: \`echo "HEAD=$(git -C ${wt} rev-parse HEAD)" > ${logf}\`, \`echo "SDK=$(cd ${wt} && dotnet --version)" >> ${logf}\`, \`echo "MOVE=${moveText}" >> ${logf}\`.
3. Edit the pins, one line each: ${editLines(t.moves, wt)}. Then \`git -C ${wt} diff --stat\` must name Directory.Packages.props alone; anything else is RED.
4. \`cd ${wt} && dotnet restore >> ${logf} 2>&1\`, then \`cd ${wt} && dotnet build >> ${logf} 2>&1\` (Directory.Build.props makes warnings errors, with the package-advisory warnings its stated exception).${t.family === 'playwright' ? ' A newer Playwright wants a newer browser build: install it with the package\'s own script into Playwright\'s own cache, deleting nothing, and name its revision in browserRevision.' : ''}
5. The trial scope, in order, stopping at the first failure: ${tests.join(', ')}.
6. The verdict, as the log's last line: \`echo "TRIAL: GREEN" >> ${logf}\` when every step passed, else \`echo "TRIAL: RED" >> ${logf}\`. A step that could not run (nuget.org unreachable, the port taken) is RED with that reason.
7. \`git -C ${REPO} worktree remove --force ${wt}\`; worktreeRemoved is whether it succeeded.
Return family, base, sdk, scope, outcome (green, red, or not-run when step 1 failed), logPath ${logf}, verdictLine (the log's last line, exactly), failingLines (red: the exact failing lines from the log, at most 40, each with no secret), browserRevision ("" unless installed).`
}

// #82 -> "Families" -> Terraform providers: the provider upgraded within each lock file's constraint,
// then format, validate and terraform test where tests exist, with no backend and no credentials.
const terraformTrialPrompt = (ctx, t, wt, logf, phaseFile) => `Trial terraform: upgrade the providers within their constraints in a throwaway worktree (#82 -> "Families"). Nothing is ever committed in it, and it is removed at the end. No backend, no credentials, no plan and no apply.

${HOUSE}

PHASE FILE: ${phaseFile}
Write your return value to the PHASE FILE as JSON before you return.
TRIAL: ${JSON.stringify({ family: t.family, base: ctx.base, moves: t.moves, scope: t.scope, smoke: false, port: 0 })}

1. \`git -C ${REPO} worktree add --detach ${wt} ${ctx.base}\`.
2. The log's first lines: \`echo "HEAD=$(git -C ${wt} rev-parse HEAD)" > ${logf}\`, \`echo "SDK=$(terraform version | head -1)" >> ${logf}\`, \`echo "MOVE=${t.moves.map((m) => `${m.id} ${m.from} -> ${m.to}`).join('; ')}" >> ${logf}\`.
3. For each directory of the scope, in order, stopping at the first failure: ${t.scope.map((d) => `\`cd ${wt}/${d} && terraform init -backend=false -upgrade -input=false >> ${logf} 2>&1\`, \`cd ${wt}/${d} && terraform validate >> ${logf} 2>&1\``).join(', ') || 'none'}; then \`cd ${wt} && terraform fmt -check -recursive infra >> ${logf} 2>&1\`, and \`terraform test\` in each scope directory that has a tests directory, from that directory, appending to the log. \`git -C ${wt} diff --stat\` must name .terraform.lock.hcl files alone.
4. The verdict, as the log's last line: \`echo "TRIAL: GREEN" >> ${logf}\` when every step passed, else \`echo "TRIAL: RED" >> ${logf}\`.
5. \`git -C ${REPO} worktree remove --force ${wt}\`; worktreeRemoved is whether it succeeded.
Return family, base, sdk (the Terraform version line), scope, outcome, logPath ${logf}, verdictLine (the log's last line, exactly), failingLines (red: the exact failing lines, at most 40), browserRevision "".`

// ---------------------------------------------------------------- the issue surface, in code
const FAMILY_NAMES = {
  aspnetcore: 'ASP.NET Core and IdentityModel', duende: 'Token management (Duende)', yarp: 'YARP', 'aws-sdk': 'AWS SDK',
  opentelemetry: 'OpenTelemetry', xunit: 'The xunit.v3 set', playwright: 'Playwright', 'schema-validator': 'The schema validator',
  'fake-clock': 'The fake clock', 'mvc-testing': 'The MVC test host', analyzers: 'The analyzer packages', sdk: 'The SDK',
  terraform: 'Terraform providers',
}
const versionsText = (vs) => [...new Set(vs)].sort(vcmp).join(', ')
const moveLine = (moves) => (moves.length ? [...moves].sort((a, b) => (a.id < b.id ? -1 : 1)).map((m) => `${m.id} ${m.from} -> ${m.to}`).join('; ') : 'no move')
const norm = (s) => String(s || '').replace(/`/g, '').replace(/→/g, '->').replace(/\s+/g, ' ').trim()
const titleOf = (d) => (d.moves.length
  ? `Pins: ${FAMILY_NAMES[d.family]} ${versionsText(d.moves.map((m) => m.from))} → ${versionsText(d.moves.map((m) => m.to))}`
  : `Pins: ${FAMILY_NAMES[d.family]}: no move`).replace(/[`$"\\]/g, '')
const projectName = (p) => p.split('/').pop().replace(/\.csproj$/, '')

// #82 -> "What gets filed, and with which label": ready-for-agent only when the trial is green, no
// evidence tie holds, the licence is unchanged and no decision holds the pin; every reason against it
// is named, first, in the body. A tie the companion found counts until the Check agent refutes it.
const reasonsAgainst = (d) => {
  const r = []
  if (d.family === 'sdk') r.push('the SDK is report-only: the release index names a newer security release than the runtime the framework pins track, and installing an SDK is the integrator\'s')
  else if (!d.trial) r.push('no trial ran')
  else if (!d.green) r.push(`the trial is ${d.trial.outcome === 'not-run' ? 'not run' : 'RED'}: ${d.trial.verdictLine || 'no verdict line'}`)
  if (d.ties.length) r.push(`evidence names the pinned version: ${d.ties.map((t) => `${t.path}:${t.line}`).join(', ')}; whether it still applies is the integrator's (plan row I-7)`)
  if (d.moves.length && d.check && (!d.check.licenceBefore || !d.check.licenceAfter || d.check.licenceBefore !== d.check.licenceAfter)) r.push(`the licence changes or could not be read: ${d.check.licenceBefore || 'unknown'} -> ${d.check.licenceAfter || 'unknown'}`)
  if (d.holds.length) r.push(`a decision holds the pin: ${d.holds.join(', ')}`)
  if (d.family === TERRAFORM) r.push('the lock files are workstream 1\'s and the table rows the integrator\'s: two owners')
  if (!d.moves.length && d.advisories.length) r.push(`an advisory with no move: ${d.advisories.map((a) => a.id).join(', ')}`)
  return r
}

const issueBody = (d, ctx) => {
  const L = []
  const ws1 = d.family === TERRAFORM
  L.push(ws1 ? '**Owner:** workstream 1 (the lock files), then integrator (the dependencies table rows)' : '**Owner:** integrator')
  L.push(ws1 ? '**Touches:** `infra/`, `docs/dependencies.md`' : '**Touches:** `Directory.Packages.props`, `docs/dependencies.md`')
  L.push(`**Pins family:** ${d.family}`, `**Pins move:** ${moveLine(d.moves)}`, '')
  L.push(d.label === 'ready-for-agent' ? '**Label:** ready-for-agent: the trial is green, no evidence names the old version, the licence is unchanged and no decision holds the pin.' : `**Label:** ready-for-human, because ${d.reasons.join('; ')}.`, '')
  L.push(`Swept on ${ctx.date} at origin/main ${ctx.base}, reported on the "${REPORT_TITLE}" issue.`, '')
  L.push('## Pins', '', '| Pin | Current | Proposed | Newest | Status |', '| --- | --- | --- | --- | --- |')
  for (const p of d.pins) {
    const m = d.moves.find((x) => x.id === p.id)
    L.push(`| ${p.id} | ${p.pinned} | ${m ? m.to : 'unchanged'} | ${p.newest || '?'} | ${p.status} |`)
  }
  L.push('', '## Advisories', '')
  if (d.advisories.length) for (const a of d.advisories) L.push(`- ${a.id} (${a.severity}) ${a.url}${a.via ? `, via ${a.via}` : ''}${a.fixed ? `, fixed in ${a.fixed}` : ', no fixed version'}`)
  else L.push('none')
  const c = d.check || {}
  L.push('', '## Vendor', '', `- Licence: ${c.licenceBefore || 'unknown'} before, ${c.licenceAfter || 'unknown'} after.`, `- Release notes: ${c.releaseNotes || 'none cited'}`)
  for (const b of c.breakingChanges || []) L.push(`- Breaking: ${b}`)
  if (c.behaviour) L.push(`- What the evidence recorded: ${c.behaviour}`)
  if (c.reason) L.push(`- Check: ${c.recommendation}, ${c.reason}`)
  L.push('', '## Trial', '')
  if (d.trial) {
    L.push(`- Scope: ${d.trial.scope.join(', ')}`, `- SDK: ${d.trial.sdk}`, `- Log: ${d.trial.logPath}`, `- Verdict: \`${d.trial.verdictLine || 'none'}\``)
    if (d.trial.browserRevision) L.push(`- Browser build installed into Playwright's cache: ${d.trial.browserRevision}`)
    if (!d.green && d.trial.failingLines.length) L.push('', 'Failing lines:', '', '```text', ...d.trial.failingLines, '```')
  } else L.push(d.family === 'sdk' ? 'Report-only: no trial installs anything.' : 'Not trialled.')
  L.push('', '## Documents that name the old version', '')
  const docs = d.pins.flatMap((p) => (p.documents || []).map((x) => `${x.path}:${x.line} (${p.id} ${p.pinned})`))
  if (docs.length) for (const x of [...new Set(docs)]) L.push(`- ${x}${d.ties.some((t) => x.startsWith(`${t.path}:${t.line} `)) ? ', evidence' : ''}`)
  else L.push('none')
  if (d.moves.length) {
    // A fenced block, since the row's own cells hold code spans an inline span cannot nest.
    L.push('', '## The dependencies table rows to write', '', '```markdown')
    for (const m of d.moves) {
      const p = d.pins.find((x) => x.id === m.id) || { referencedBy: [] }
      L.push(`| ${m.id} | ${m.to} | ${c.licenceAfter || '?'} | ${p.referencedBy.map((r) => '`' + projectName(r) + '`').join(', ')} | <keep the row's note>; checked on ${ctx.date} |`)
    }
    L.push('```')
    const comments = d.pins.filter((p) => p.comment && d.moves.some((m) => m.id === p.id))
    L.push('', '## Pins file comments', '')
    if (comments.length) for (const p of comments) L.push(`- Above ${p.id}: "${p.comment}". A comment that carries a date or a reason speaks for its own row: update it with the move (change request 1.1); the file header's date is not moved to claim a recheck of every row.`)
    else L.push('No comment above a moved pin carries a date or a reason. The file header\'s date stays as it is (change request 1.1).')
    L.push('', '## Acceptance', '')
    for (const m of d.moves) L.push(ws1 ? `- [ ] Each .terraform.lock.hcl under ${d.fam.trialScope.join(', ')} pins ${m.id} at ${m.to}.` : `- [ ] Directory.Packages.props pins ${m.id} at ${m.to}.`)
    for (const m of d.moves) L.push(`- [ ] docs/dependencies.md's row for ${m.id} names ${m.to} and its licence.`)
    if (d.trial) L.push(`- [ ] The trial scope passes: ${d.trial.scope.join(', ')}.`)
    // A box names no landing word (tools/ticket_shape.py's SITTING_OR_LANDING), so the citation the
    // landing owes is prose below the list.
    L.push('- [ ] `tools/check.sh --full` passes.', '', 'The change that lands this cites this issue and the trial\'s verdict line, so the evidence for the move is one hop away.')
  }
  L.push('', '_Filed by the pins-sweep workflow (#82)._')
  return L.join('\n')
}
const standsBody = (d, ctx) => `The pins sweep of ${ctx.date} at origin/main ${ctx.base} makes the same recommendation: ${moveLine(d.moves)}. The body is unchanged.`
const changedBody = (d, prev, ctx) => `The pins sweep of ${ctx.date} at origin/main ${ctx.base} replaced the body: the move was "${prev}" and is now "${moveLine(d.moves)}", labelled ${d.label}.`

// ---------------------------------------------------------------- run
phase('Inventory')
const inv = await run(inventoryPrompt(), { label: 'inventory', phase: 'Inventory', schema: INVENTORY })
if (!inv || inv.outcome !== 'ok') {
  return { halted: `Inventory halted${inv && inv.reason ? `: ${inv.reason}` : ': the agent returned nothing'}; no Check or Trial agent was dispatched`, base: (inv && inv.base) || '', date: DATE, agents: agentsRun }
}
if (!/^[0-9a-f]{40}$/.test(inv.base)) return { halted: `Inventory returned base ${JSON.stringify(inv.base)}; nothing was checked`, date: DATE, agents: agentsRun }
// The listing feeds dedupe and the report issue's lookup; read as empty after a failure, every
// family would be created again, so the run stops here, dry or not (CODING_STANDARDS.md -> "A check
// fails closed").
if (!inv.listed) return { halted: `the issue listings could not be read${inv.reason ? `: ${inv.reason}` : ''}; dedupe cannot run, so nothing was checked or filed`, base: inv.base, date: DATE, agents: agentsRun }
const ctx = { base: inv.base, date: DATE, sdk: inv.sdk, sdkRow: inv.sdkRow }
const famById = new Map(inv.families.map((f) => [f.id, f]))
const scoped = SCOPE.filter((f) => famById.has(f))
const leftovers = SCOPE.filter((f) => !famById.has(f)).map((f) => ({ family: f, why: 'not in the inventory at the base commit' }))
log(`pins sweep at ${ctx.base}: ${scoped.length} families; log ${LOG}`)

phase('Check')
const checks = new Map()
const checked = await parallel(scoped.map((id) => () => run(checkPrompt(ctx, famById.get(id), `${LOG}/check-${id}.json`), { label: `check-${id}`, phase: 'Check', schema: CHECK })))
scoped.forEach((id, i) => {
  if (checked[i] && checked[i].family === id) checks.set(id, checked[i])
  else leftovers.push({ family: id, why: 'its Check agent returned nothing' })
})

// The candidates: a Check move, or the moves override, which replaces the candidate for its family.
const candidates = []
for (const id of scoped) {
  const fam = famById.get(id)
  const c = checks.get(id)
  if (!c || id === 'sdk') continue
  let moves = c.recommendation === 'move' ? c.candidate.filter((m) => m.to && m.from !== m.to) : []
  if (MOVES[id]) {
    const want = MOVES[id]
    moves = fam.pins.map((p) => ({ id: p.id, from: p.pinned, to: typeof want === 'string' ? want : want[p.id] })).filter((m) => m.to && m.to !== m.from)
  }
  if (!moves.length) continue
  const advisories = [...fam.pins.flatMap((p) => p.advisories), ...c.advisories]
  candidates.push({ family: id, moves, scope: fam.trialScope, smoke: fam.smoke, advisory: advisories.length > 0, distance: distanceOf(moves), override: !!MOVES[id] })
}
// #82 -> "Trial": advisory-bearing families first, then by distance behind, then by id for a stable order.
candidates.sort((a, b) => (a.advisory === b.advisory ? 0 : a.advisory ? -1 : 1) || cmpDistance(a.distance, b.distance) || (a.family < b.family ? -1 : 1))
const toTrial = candidates.slice(0, MAX_ISSUES)
for (const c of candidates.slice(MAX_ISSUES)) leftovers.push({ family: c.family, why: `behind, not trialled: over the cap of ${MAX_ISSUES} trials`, moves: moveLine(c.moves) })

phase('Trial')
const trials = new Map()
let smokeIndex = 0
const ports = new Map(toTrial.map((t) => [t.family, t.smoke ? TRIAL_PORT + smokeIndex++ : 0]))
const tried = await parallel(toTrial.map((t) => () => run(trialPrompt(ctx, t, ports.get(t.family), `${LOG}/trial-${t.family}.json`), { label: `trial-${t.family}`, phase: 'Trial', schema: TRIAL })))
toTrial.forEach((t, i) => {
  const r = tried[i]
  trials.set(t.family, r && r.family === t.family ? r : { family: t.family, base: ctx.base, sdk: '', scope: t.scope, outcome: 'not-run', logPath: '', verdictLine: '', failingLines: ['the Trial agent returned nothing'], browserRevision: '', worktreeRemoved: false })
})

phase('File')
// One decision per family: what to file, its label and its dedupe outcome, all in code.
const decisions = []
for (const id of scoped) {
  const fam = famById.get(id)
  const c = checks.get(id)
  if (!c) continue
  const cand = candidates.find((x) => x.family === id)
  const trial = trials.get(id)
  const advisories = [...fam.pins.flatMap((p) => p.advisories), ...c.advisories].filter((a, i, all) => all.findIndex((b) => b.id === a.id && b.via === a.via) === i)
  const refuted = new Set(c.evidenceRefuted.map((t) => `${t.path}:${t.line}`))
  const ties = [...c.evidenceTouched, ...fam.pins.flatMap((p) => p.evidence).filter((t) => !refuted.has(`${t.path}:${t.line}`))]
    .filter((t, i, all) => all.findIndex((x) => x.path === t.path && x.line === t.line) === i)
  const holds = [...new Set(fam.pins.map((p) => p.hold).filter(Boolean))]
  let file = false
  let moves = []
  if (id === 'sdk') file = !!ctx.sdkRow.securityRelease
  else if (cand && trial) { file = true; moves = cand.moves }
  else if (!cand && advisories.length) file = true
  if (!file) continue
  const d = { family: id, fam, check: c, trial: trial || null, green: !!trial && trial.outcome === 'green' && trial.verdictLine === 'TRIAL: GREEN', moves, advisories, ties, holds, pins: fam.pins }
  d.reasons = reasonsAgainst(d)
  d.label = d.reasons.length ? 'ready-for-human' : 'ready-for-agent'
  // The title goes inside a double-quoted gh argument, so it loses what titleOf strips too.
  d.title = id === 'sdk' ? `Pins: The SDK: runtime ${ctx.sdkRow.frameworkRuntime} → ${ctx.sdkRow.securityRelease} security release`.replace(/[`$"\\]/g, '') : titleOf(d)
  d.move = norm(id === 'sdk' ? `runtime ${ctx.sdkRow.frameworkRuntime} -> ${ctx.sdkRow.securityRelease}` : moveLine(d.moves))
  d.advisory = advisories.length > 0
  d.distance = distanceOf(moves)
  decisions.push(d)
}
decisions.sort((a, b) => (a.advisory === b.advisory ? 0 : a.advisory ? -1 : 1) || cmpDistance(a.distance, b.distance) || (a.family < b.family ? -1 : 1))

// #82 -> "Dedupe": the marker line, matched locally against the listing, never GitHub's search
// index. Creations and body updates count toward maxIssues; a stands comment does not.
let counted = 0
const writes = []
for (const d of decisions) {
  const open = inv.openIssues.filter((i) => norm(i.marker) === d.family).sort((a, b) => b.number - a.number)[0]
  const closed = inv.closedIssues.filter((i) => norm(i.marker) === d.family).sort((a, b) => b.number - a.number)[0]
  d.body = issueBody(d, ctx)
  d.bodyFile = `${LOG}/issue-${d.family}.md`
  if (open && norm(open.move) === d.move) { d.action = 'stands'; d.issue = open.number }
  else if (!open && closed && closed.labels.includes('wontfix') && norm(closed.move) === d.move) { d.action = 'declined'; d.issue = closed.number }
  else if (counted >= MAX_ISSUES) { d.action = 'skipped-cap'; d.issue = open ? open.number : 0 }
  else if (open) { counted++; d.action = DRY ? 'would-update' : 'updated'; d.issue = open.number; d.previous = open.move; d.oldLabels = open.labels }
  else { counted++; d.action = DRY ? 'would-create' : 'created'; d.issue = 0 }
  if (d.action === 'stands') writes.push({ key: d.family, kind: 'comment', issue: d.issue, bodyFile: `${LOG}/comment-${d.family}.md`, body: standsBody(d, ctx) })
  if (d.action === 'updated') {
    const drop = (d.oldLabels || []).filter((l) => ['ready-for-agent', 'ready-for-human'].includes(l) && l !== d.label)
    writes.push({ key: d.family, kind: 'update', issue: d.issue, bodyFile: d.bodyFile, label: d.label, drop, commentFile: `${LOG}/comment-${d.family}.md`, comment: changedBody(d, d.previous, ctx) })
  }
  if (d.action === 'created') writes.push({ key: d.family, kind: 'create', title: d.title, bodyFile: d.bodyFile, label: d.label })
}

// The standing report issue (#82): found by title in the listing, created only when absent; each run
// adds one comment naming the families checked, the rows whose status changed and each filing.
const prevStatus = new Map(inv.report.statuses.map((s) => [s.pin, s.status]))
const nowStatus = []
for (const id of scoped) for (const p of famById.get(id).pins) nowStatus.push({ pin: p.id, status: p.status })
if (scoped.includes('sdk')) nowStatus.push({ pin: 'SDK (global.json)', status: ctx.sdkRow.status })
const changed = inv.report.number ? nowStatus.filter((s) => prevStatus.has(s.pin) && prevStatus.get(s.pin) !== s.status).map((s) => `${s.pin}: ${prevStatus.get(s.pin)} -> ${s.status}`) : []
const actionText = (d) => `${FAMILY_NAMES[d.family]}: ${d.action}${d.issue ? ` #${d.issue}` : d.action === 'created' ? ` NUMBER(${d.family})` : ''}, ${d.label}`
const reportBody = [
  `## ${REPORT_TITLE}`, '',
  `Swept on ${ctx.date} at origin/main ${ctx.base}. Families checked: ${scoped.join(', ')}. A row outside them reads not-checked. Precedence: advisory, blocked-by-evidence, held-by-decision, behind-minor, behind-patch, current.`, '',
  'TABLE: pending', '',
  '_Refreshed by the pins-sweep workflow (#82); each run adds one comment._',
].join('\n')
const reportComment = [
  `Pins sweep ${ctx.date} at origin/main ${ctx.base}.`, '',
  `- Families checked: ${scoped.join(', ')}.`,
  `- Rows whose status changed: ${changed.length ? changed.join('; ') : inv.report.number ? 'none' : 'the first run, nothing to compare'}.`,
  `- Filed: ${decisions.length ? decisions.map(actionText).join('; ') : 'nothing to decide'}.`,
  `- Not trialled or not checked: ${leftovers.length ? leftovers.map((l) => `${l.family} (${l.why})`).join('; ') : 'none'}.`,
].join('\n')
const report = { mode: DRY ? 'dry' : inv.report.number ? 'edit' : 'create', issue: inv.report.number, bodyFile: `${LOG}/report.md`, commentFile: `${LOG}/report-comment.md` }

const ghLines = (w) => {
  if (w.kind === 'create') return [`\`cd ${REPO} && gh issue create --title "${w.title}" --label ${w.label} --body-file ${w.bodyFile}\``]
  if (w.kind === 'comment') return [`\`cd ${REPO} && gh issue comment ${w.issue} --body-file ${w.bodyFile}\``]
  return [`\`cd ${REPO} && gh issue edit ${w.issue} --body-file ${w.bodyFile} --add-label ${w.label}${w.drop.length ? ` --remove-label ${w.drop.join(',')}` : ''}\``, `\`cd ${REPO} && gh issue comment ${w.issue} --body-file ${w.commentFile}\``]
}
const filePrompt = () => `File: write the would-be issues to the log, then ${DRY ? 'stop: this is a dry run, and you make no GitHub write of any kind' : 'make exactly the GitHub writes below, in order, and nothing else'}; refresh the report issue; clean up and check the checkout (#82 -> "Phases" -> File). The script chose every write: dedupe and the cap are already applied.

${HOUSE}

PHASE FILE: ${LOG}/file-record.json
Write your return value to the PHASE FILE as JSON before you return.
FILINGS: ${JSON.stringify(decisions.map((d) => ({ family: d.family, action: d.action, issue: d.issue, title: d.title, label: d.label, bodyFile: d.bodyFile })))}
WRITES: ${JSON.stringify(writes.map((w) => ({ key: w.key, kind: w.kind, issue: w.issue || 0 })))}
REPORT ISSUE: ${JSON.stringify(report)}

1. Write each would-be issue first: the text between BEGIN ISSUE <family> and END ISSUE <family>, without the markers, to its bodyFile; then each comment's text between BEGIN COMMENT <family> and END COMMENT <family> to ${LOG}/comment-<family>.md. Then \`jq -n --argjson f '${JSON.stringify(decisions.map((d) => ({ family: d.family, action: d.action, issue: d.issue, title: d.title, label: d.label })))}' '$f' > ${LOG}/would-file.json\`.
2. The table: \`python3 -I ${COMPANION} render --input ${LOG}/inventory.json --families ${scoped.join(',')} > ${LOG}/table.md\`. Write the text between BEGIN REPORT and END REPORT to ${report.bodyFile} with its line "TABLE: pending" replaced by the contents of ${LOG}/table.md, and the text between BEGIN REPORT COMMENT and END REPORT COMMENT to ${report.commentFile}. table is the table's text.
${DRY ? '3. Post nothing. Every write is done false, number 0, url "".' : `3. The writes, in order. For a create, number and url are the new issue's; then replace NUMBER(<family>) in ${report.commentFile} with #<number>.
${writes.map((w) => `- ${w.key}: ${ghLines(w).join(', then ')}`).join('\n') || '- none'}
4. The report issue: ${report.mode === 'edit' ? `\`cd ${REPO} && gh issue edit ${report.issue} --body-file ${report.bodyFile}\`` : `\`cd ${REPO} && gh issue create --title "${REPORT_TITLE}" --body-file ${report.bodyFile}\``}, then \`cd ${REPO} && gh issue comment <its number> --body-file ${report.commentFile}\`. report is {number, url}.
If GitHub cannot be reached, stop writing: the would-be issues stay in the log, each write not made is done false, and reason says so. There is no replay file; the next run re-derives everything.`}
5. Clean up: \`git -C ${REPO} worktree remove --force ${BASE_WT}\`, then \`git -C ${REPO} worktree prune\`. worktreesLeft: each path \`git -C ${REPO} worktree list --porcelain\` names under ${WT}/pins-sweep-${DATE}-.
6. The checkout: trackedChanged, each tracked file \`git -C ${REPO} status --porcelain --untracked-files=no\` shows that ${LOG}/checkout-at-start.txt does not; newUntracked, each untracked path \`git -C ${REPO} status --porcelain --untracked-files=all\` shows that is not in that file and not under ${LOG}/; headMoved, whether \`git -C ${REPO} rev-parse HEAD\` differs from ${LOG}/head-at-start.txt; refsChanged, each line of \`git -C ${REPO} for-each-ref --format='%(refname) %(objectname)'\` not in ${LOG}/refs-at-start.txt, outside refs/remotes/.
Return writes (one per WRITE, {key, done, number, url}), table, report, checkout and reason ("" when nothing failed).
${decisions.map((d) => `BEGIN ISSUE ${d.family}\n${d.body}\nEND ISSUE ${d.family}`).join('\n')}
${writes.filter((w) => w.kind !== 'create').map((w) => `BEGIN COMMENT ${w.key}\n${w.kind === 'comment' ? w.body : w.comment}\nEND COMMENT ${w.key}`).join('\n')}
BEGIN REPORT
${reportBody}
END REPORT
BEGIN REPORT COMMENT
${reportComment}
END REPORT COMMENT`
const filed = await run(filePrompt(), { label: 'file', phase: 'File', schema: FILED })

phase('Report')
const done = new Map(((filed && filed.writes) || []).map((w) => [w.key, w])) // empty-ok: a null File is named in reason below, and each real write then reads not-written
const records = decisions.map((d) => {
  const w = writes.find((x) => x.key === d.family)
  const r = w ? done.get(w.key) : null
  let action = d.action
  // A dry run makes no write, so its action is the one the script chose; a real run's write that the
  // File agent did not report done was not made.
  if (w && !DRY && (!r || !r.done)) action = 'not-written'
  const number = w && w.kind === 'create' ? (r && r.done ? r.number : 0) : d.issue
  return { family: d.family, action, number, url: (r && r.url) || '', label: d.label, title: d.title, bodyFile: d.bodyFile }
})
const checkout = (filed && filed.checkout) || { trackedChanged: ['the File agent returned nothing'], newUntracked: [], headMoved: false, refsChanged: [], worktreesLeft: [] }
const trialLeft = [...trials.values()].filter((t) => !t.worktreeRemoved && t.outcome !== 'not-run').map((t) => trialWt(t.family))
return {
  halted: false, base: ctx.base, date: DATE, dryRun: DRY, families: scoped, log: LOG,
  table: (filed && filed.table) || '', files: records,
  report: { number: filed && filed.report ? filed.report.number : 0, url: filed && filed.report ? filed.report.url : '', action: report.mode },
  trials: [...trials.values()].map((t) => ({ family: t.family, outcome: t.outcome, verdictLine: t.verdictLine, logPath: t.logPath })),
  leftovers, worktreesLeft: [...new Set([...checkout.worktreesLeft, ...trialLeft])],
  mainCheckoutClean: !!filed && !checkout.trackedChanged.length && !checkout.newUntracked.length && !checkout.headMoved && !checkout.refsChanged.length,
  reason: (filed && filed.reason) || (filed ? '' : 'the File agent returned nothing'),
  agentCount: AGENT_COUNT, maxAgents: MAX_AGENTS, agents: agentsRun,
}
