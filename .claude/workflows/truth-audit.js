export const meta = {
  name: 'truth-audit',
  description: 'Read the prose corpus at origin/main, extract every checkable claim, verify each against the tree, cluster the false ones by noun, file each finding as one needs-triage issue, and report the run as a Truth audit <date> issue',
  whenToUse: 'In a foreground session, launched by scriptPath: over one contract after it changed, over the contracts before a gate is tagged, or over the whole corpus on a quiet day; dryRun: true reports without filing',
  phases: [
    { title: 'Shard', detail: 'one setup agent (fetch with tags, the trunk sha, the trunk listing, the open issues), then one agent per shard of about 8,000 words, returning claim records; it never verifies' },
    { title: 'Verify', detail: 'claims batched by file, about 60 to an agent: grep the noun once over trunk, return true, false, stale or unverifiable with trunk path:line evidence' },
    { title: 'Cluster', detail: 'one agent groups the false and stale verdicts by noun and correction; the script names each site\'s owner from OWNERSHIP.md and its fix kind' },
    { title: 'File', detail: 'one serialised agent creates the findings the script chose (needs-triage only, no parent) and the candidate-rule comments not yet posted; skipped on a dry run' },
    { title: 'Report', detail: 'one agent checks the checkout and writes the Truth audit <date> issue the script composed, editing a same-day one; on a dry run it writes the log file alone' },
  ],
}

// ---------------------------------------------------------------- what this is
// The truth-audit spec (#81), built under #267; its proofs on real runs are #268. The prose makes
// claims about the tree that nothing checks between pull requests (#81 -> "Problem Statement"). This
// script reads the corpus as origin/main has it, and its agents extract and verify the claims; what
// can be decided in code is decided here, so the companion can hold it: which files the corpus is,
// how they shard and batch, which verdicts reach the cluster, each site's owner and fix kind, which
// findings are filed, skipped as duplicates, over the cap or owed by a row, which candidate rules
// are posted, the issue bodies and the report. The agents fetch, read, grep, judge and run gh.
// Test companion: truth-audit_tests.mjs beside this file.
//
// Launch (Workflow tool), by scriptPath so the landed file runs and not a session's snapshot:
//   scriptPath: /Users/adammedford/Projects/spa-platform/.claude/workflows/truth-audit.js
//   args: { "files": ["contracts/telemetry.md"], "dryRun": true }
// Arguments (#81 -> "Arguments, and no others"), and no others: an unknown one is refused.
//   files        optional  paths or globs. A relative path is read at origin/main through git; a glob
//                          (* within a segment, ** across them, ?) is expanded against trunk's file
//                          list; an absolute path is a file on disk outside the repository, for a
//                          throwaway copy during a proof. Default: the corpus, CORPUS below.
//   maxParallel  optional  positive integer, default 3: agents in flight at once.
//   maxIssues    optional  integer >= 0, default 5: findings filed per run; the rest are reported.
//   dryRun       optional  boolean, default false: no GitHub write of any kind; the report is the
//                          return value and a file in the run's log.
// There is no repo argument: the main checkout is REPO, one line, which the companion points at a
// throwaway repository. There is no date argument: an argless new Date() throws in a Workflow
// script, so the setup agent reads the date.
const A = args || {}
const KNOWN_ARGS = ['files', 'maxParallel', 'maxIssues', 'dryRun']
const REPO = '/Users/adammedford/Projects/spa-platform'
const badArguments = (a) => {
  const bad = []
  for (const k of Object.keys(a)) if (!KNOWN_ARGS.includes(k)) bad.push(`${k}: not an argument of this workflow; it takes ${KNOWN_ARGS.join(', ')}`)
  if (a.files !== undefined && !(Array.isArray(a.files) && a.files.length && a.files.every((f) => typeof f === 'string' && f.trim().length))) bad.push('files: a non-empty array of paths or globs, e.g. ["contracts/telemetry.md"] or ["contracts/*.md"]')
  else if (a.files) {
    for (const f of a.files) {
      if (f.startsWith('/') && (f === REPO || f.startsWith(REPO + '/'))) bad.push(`files: ${f} is inside the main checkout, whose working tree is never read; name it relative to the repository to read it at origin/main`)
      if (!f.startsWith('/') && /(^|\/)\.\.(\/|$)/.test(f)) bad.push(`files: ${f} climbs out of the repository; a file outside it is named by its absolute path`)
    }
  }
  if (a.maxParallel !== undefined && !(Number.isInteger(a.maxParallel) && a.maxParallel > 0)) bad.push('maxParallel: a positive integer (default 3)')
  if (a.maxIssues !== undefined && !(Number.isInteger(a.maxIssues) && a.maxIssues >= 0)) bad.push('maxIssues: an integer, 0 or more (default 5)')
  if (a.dryRun !== undefined && typeof a.dryRun !== 'boolean') bad.push('dryRun: true or false, a boolean (the string "true" is refused)')
  return bad
}
const BAD_LAUNCH = badArguments(A)
if (BAD_LAUNCH.length) {
  // Stops here: no agent is started, nothing is read or written.
  return { halted: 'bad launch; no agent started. Fix each argument and relaunch:\n- ' + BAD_LAUNCH.join('\n- ') }
}
const MAX_PAR = A.maxParallel || 3
const MAX_ISSUES = A.maxIssues === undefined ? 5 : A.maxIssues
const DRY = A.dryRun === true
// The corpus (#81 -> "Arguments, and no others"): the contracts, the decision records, the agent
// guides, the glossary, the coding standards, the roadmap, the README and the agent guide; 40 files
// and 35,986 words at 5e0d4e0. contracts/changes/ is records and the plan export is frozen, so
// neither is extracted from, though the noun grep reaches both as sites.
const CORPUS = ['contracts/*.md', 'docs/adr/*.md', 'docs/agents/*.md', 'GLOSSARY.md', 'CODING_STANDARDS.md', 'docs/roadmap.md', 'README.md', 'AGENTS.md']
const FILES = A.files || CORPUS
// Tuned from the counts the report prints, not arguments (#81): about five shards for the default
// corpus, and sixty claims to a verify agent so its greps share sites.
const SHARD_WORDS = 8000
const BATCH_CLAIMS = 60
// Where candidate rules are proposed. #81 names #65, which is closed (#81's audit comment of
// 2026-10-21); where this should point is raised on #81 and not decided here (#267).
const RULE_ISSUE = 65
const LOG = `${REPO}/.scratch/truth-audit/log` // git-ignored (.gitignore: .scratch/*/log/)
const TYPES = ['identifier-exists', 'writes-or-reads', 'proves', 'path-exists', 'count', 'freeze-or-ownership-cell', 'universal', 'vendor']
const VERDICTS = ['true', 'false', 'stale', 'unverifiable']
const FIX_KINDS = { 'integrator-edit': 'integrator edit', 'change-request': 'change request', 'workstream-edit': 'workstream edit', 'plan-re-export': 'plan re-export' }

// ---------------------------------------------------------------- slots
// The overnight workflow's slot and serial helpers: a freed slot goes straight to a waiter, so no
// more than maxParallel agents are ever in flight, and serial runs the one phase that writes.
let slots = MAX_PAR
const waiters = []
const acquire = () => new Promise((res) => { if (slots > 0) { slots--; res() } else waiters.push(res) })
const release = () => { const w = waiters.shift(); if (w) w(); else slots++ }
const withSlot = async (fn) => { await acquire(); try { return await fn() } finally { release() } }
let writeLock = Promise.resolve()
const serial = (fn) => { const p = writeLock.then(fn, fn); writeLock = p.catch(() => {}); return p }
let agentsRun = 0
const run = (prompt, opts) => withSlot(() => { agentsRun++; return agent(prompt, opts) })

// ---------------------------------------------------------------- what every prompt carries
// The overnight workflow's house rules where they apply (#81 -> "The script"), with this run's
// difference: it writes no file outside its run log and changes nothing on any branch. Every
// command an agent is told to run is a backtick span, which is what the companion searches; the
// data a prompt carries is on a NAME: [json] line or between BEGIN and END, and is never run.
const HOUSE = `House rules (a truth audit; it modifies nothing in the checkout and decides no fix):
- The main checkout ${REPO} is Adam's. Never commit, checkout, switch, stash, reset or build there, and never write a file there except under ${LOG}/. Git's own bookkeeping for a fetch is the only other change.
- Every read of the repository is of the trunk sha this run names, through git: a file is \`git -C ${REPO} show <sha>:<path>\`, a grep is \`git -C ${REPO} grep -n -F -e <noun> <sha>\`. Never read the working tree, which may be dirty or trail origin/main.
- The tracker is GitHub Issues. Run every \`gh\` command with \`cd ${REPO} &&\` in front. A multi-line body goes through a file you write under the run log and \`--body-file <that file>\`.
- Never write a comment that begins "Confirm" or "Approved": a confirmation is Adam's act. Never change a label, a body or a state on an existing issue, except the edit of this day's report issue when your prompt names it.
- Never log a secret, token, cookie, authorization code or state value (AGENTS.md).
- Write each PHASE FILE and OUTPUT FILE line's path exactly as given.`

// The rules the verifier carries by name (#81 -> "Phase Verify: verdicts"), and the glossary's words.
const RULES = `The three rules, by name:
- Grep the noun, then check the scope (docs/agents/review.md): grep the token the sentence cannot paraphrase away, never the sentence; read the scope at every site.
- A universal names its exceptions by the writer (CODING_STANDARDS.md): enumerate the writers or instances of the noun and ask of each whether it has the property or is among the exceptions the sentence names. An exception scoped to an identifier that has a second writer is false: that is the 405 lesson, where a qualifier on a rule id that two stages write excepted only one.
- Code that rests on vendor behaviour cites the vendor (CODING_STANDARDS.md): a vendor claim is checked only against the vendor's document fetched in this run, with WebFetch; when it cannot be fetched the verdict is unverifiable, never a memory.
The glossary's words (GLOSSARY.md): a rejection is a response a stage ends the request with under a rule id; a request line is the one JSON log line UsePlatformTelemetry writes per request; a stage is named by its entry point, never by its number.`

// ---------------------------------------------------------------- schemas
const SETUP = {
  type: 'object',
  properties: {
    fetched: { type: 'boolean' }, listed: { type: 'boolean' }, reason: { type: 'string' },
    sha: { type: 'string' }, date: { type: 'string' }, run: { type: 'string' },
    tree: { type: 'array', items: { type: 'object', properties: { path: { type: 'string' }, words: { type: 'integer' } }, required: ['path', 'words'] } },
    absolute: { type: 'array', items: { type: 'object', properties: { path: { type: 'string' }, words: { type: 'integer' }, exists: { type: 'boolean' } }, required: ['path', 'words', 'exists'] } },
    ownership: { type: 'string' },
    openIssues: { type: 'array', items: { type: 'object', properties: { number: { type: 'integer' }, title: { type: 'string' }, noun: { type: 'string' } }, required: ['number', 'title', 'noun'] } },
    ruleLines: { type: 'array', items: { type: 'string' } },
    rows: { type: 'object', properties: { known: { type: 'boolean' }, unlanded: { type: 'array', items: { type: 'string' } }, reason: { type: 'string' } }, required: ['known', 'unlanded', 'reason'] },
  },
  required: ['fetched', 'listed', 'reason', 'sha', 'date', 'run', 'tree', 'absolute', 'ownership', 'openIssues', 'ruleLines', 'rows'],
}
const CLAIM = {
  type: 'object',
  properties: {
    id: { type: 'string' }, file: { type: 'string' }, line: { type: 'integer' }, ordinal: { type: 'integer' },
    type: { type: 'string', enum: TYPES }, sentence: { type: 'string' }, noun: { type: 'string' }, scope: { type: 'string' },
    exceptions: { type: 'array', items: { type: 'string' } }, anchor: { type: 'string' }, vendor: { type: 'string' }, document: { type: 'string' },
  },
  required: ['id', 'file', 'line', 'ordinal', 'type', 'sentence', 'noun', 'scope', 'exceptions', 'anchor', 'vendor', 'document'],
}
const SHARDED = { type: 'object', properties: { claims: { type: 'array', items: CLAIM } }, required: ['claims'] }
const EVIDENCE = { type: 'array', items: { type: 'object', properties: { where: { type: 'string' }, quote: { type: 'string' } }, required: ['where', 'quote'] } }
const VERIFIED = {
  type: 'object',
  properties: {
    verdicts: { type: 'array', items: { type: 'object', properties: { id: { type: 'string' }, verdict: { type: 'string', enum: VERDICTS }, evidence: EVIDENCE, correction: { type: 'string' }, why: { type: 'string' } }, required: ['id', 'verdict', 'evidence', 'correction', 'why'] } },
  },
  required: ['verdicts'],
}
const CLUSTERED = {
  type: 'object',
  properties: {
    findings: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          noun: { type: 'string' }, claim: { type: 'string' }, verdict: { type: 'string', enum: ['false', 'stale'] },
          sites: { type: 'array', items: { type: 'object', properties: { file: { type: 'string' }, line: { type: 'integer' }, excerpt: { type: 'string' } }, required: ['file', 'line', 'excerpt'] } },
          evidence: EVIDENCE, correction: { type: 'string' }, changeRequest: { type: 'boolean' }, row: { type: 'string' },
          rule: { type: 'object', properties: { key: { type: 'string' }, pattern: { type: 'string' }, examples: { type: 'array', items: { type: 'string' } } }, required: ['key', 'pattern', 'examples'] },
        },
        required: ['noun', 'claim', 'verdict', 'sites', 'evidence', 'correction', 'changeRequest', 'row', 'rule'],
      },
    },
  },
  required: ['findings'],
}
const FILED = {
  type: 'object',
  properties: {
    created: { type: 'array', items: { type: 'object', properties: { key: { type: 'string' }, number: { type: 'integer' }, url: { type: 'string' } }, required: ['key', 'number', 'url'] } },
    commented: { type: 'array', items: { type: 'object', properties: { key: { type: 'string' }, url: { type: 'string' } }, required: ['key', 'url'] } },
    pending: { type: 'array', items: { type: 'object', properties: { key: { type: 'string' }, action: { type: 'string' } }, required: ['key', 'action'] } },
    reason: { type: 'string' },
  },
  required: ['created', 'commented', 'pending', 'reason'],
}
const REPORTED = {
  type: 'object',
  properties: {
    checkout: { type: 'object', properties: { trackedChanged: { type: 'array', items: { type: 'string' } }, newUntracked: { type: 'array', items: { type: 'string' } } }, required: ['trackedChanged', 'newUntracked'] },
    issue: { type: 'integer' }, url: { type: 'string' }, posted: { type: 'boolean' }, reason: { type: 'string' },
  },
  required: ['checkout', 'issue', 'url', 'posted', 'reason'],
}

// ---------------------------------------------------------------- files, in code
// A glob's segments: * any run within a segment, ? one character, ** any number of segments.
const isGlob = (p) => /[*?]/.test(p)
const globRe = (g) => new RegExp('^' + g.split('/').map((seg) => (seg === '**' ? '\u0000' : seg.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '[^/]*').replace(/\?/g, '[^/]'))).join('/').replace(/\u0000\//g, '(?:[^/]+/)*').replace(/\/\u0000/g, '(?:/[^/]+)*').replace(/\u0000/g, '.*') + '$')
// What setup lists from trunk: each pattern's literal directory, the part before its first wildcard.
const prefixOf = (p) => {
  if (!isGlob(p)) return p
  const segs = p.split('/')
  const lit = []
  for (const s of segs) { if (/[*?]/.test(s)) break; lit.push(s) }
  return lit.join('/')
}
const resolveFiles = (patterns, tree, absolute) => {
  const files = []
  const missing = []
  const add = (f) => { if (!files.some((x) => x.path === f.path)) files.push(f) }
  for (const p of patterns) {
    if (p.startsWith('/')) {
      const a = absolute.find((x) => x.path === p)
      if (a && a.exists) add({ path: p, words: a.words, read: `cat ${p}` })
      else missing.push(`${p} (not on disk)`)
    } else if (isGlob(p)) {
      const re = globRe(p)
      const hit = tree.filter((t) => re.test(t.path))
      if (!hit.length) missing.push(`${p} (matches nothing at trunk)`)
      for (const t of hit) add({ path: t.path, words: t.words, read: null })
    } else {
      const t = tree.find((x) => x.path === p)
      if (t) add({ path: t.path, words: t.words, read: null })
      else missing.push(`${p} (not at trunk)`)
    }
  }
  return { files, missing }
}
// Shards: as many as the word constant needs, filled largest file first into the lightest shard,
// so each lands near the constant; a file is never split, so one larger than it is a shard alone.
const shardsOf = (files) => {
  const total = files.reduce((n, f) => n + f.words, 0)
  const n = Math.max(1, Math.min(files.length, Math.ceil(total / SHARD_WORDS)))
  const bins = Array.from({ length: n }, () => ({ words: 0, files: [] }))
  for (const f of [...files].sort((a, b) => b.words - a.words || (a.path < b.path ? -1 : 1))) {
    const bin = bins.reduce((m, b) => (b.words < m.words ? b : m), bins[0])
    bin.files.push(f)
    bin.words += f.words
  }
  return bins.filter((b) => b.files.length)
}
// Verify batches: claims grouped by file, whole files packed in order up to the batch constant; a
// file with more claims than that is cut into batches of its own.
const batchesOf = (claims) => {
  const byFile = new Map()
  for (const c of claims) { if (!byFile.has(c.file)) byFile.set(c.file, []); byFile.get(c.file).push(c) }
  const out = []
  let cur = []
  for (const cs of byFile.values()) {
    if (cs.length > BATCH_CLAIMS) {
      for (let i = 0; i < cs.length; i += BATCH_CLAIMS) out.push(cs.slice(i, i + BATCH_CLAIMS))
      continue
    }
    if (cur.length + cs.length > BATCH_CLAIMS) { out.push(cur); cur = [] }
    cur = cur.concat(cs)
  }
  if (cur.length) out.push(cur)
  return out
}

// ---------------------------------------------------------------- owners and fix kinds, in code
// OWNERSHIP.md's block at trunk, read as .github/path-ownership.py reads it: the last matching line
// wins; "*" is every path, "/dir/" a directory, "/file" one file.
const ownersOf = (text) => {
  const m = /```text\n([\s\S]*?)```/.exec(text || '')
  const rules = []
  for (const line of (m ? m[1] : '').split('\n')) {
    const t = line.trim()
    if (!t || t.startsWith('#')) continue
    const [pat, owner] = t.split(/\s+/)
    if (pat && owner) rules.push({ pat, owner })
  }
  return (p) => {
    let owner = 'integrator'
    for (const r of rules) {
      const rel = r.pat.replace(/^\//, '')
      if (r.pat === '*' || (rel.endsWith('/') ? p.startsWith(rel) : p === rel)) owner = r.owner
    }
    return owner
  }
}
// #81 -> "Phase Cluster: findings": a plan export site is a re-export; a workstream path is the
// owner's own edit; a frozen contract whose correction changes a stage, an owner, a signature, a
// schema, a rule id, a field name or a listed name waits for a change request; the rest is an
// integrator edit. A path outside the repository (a throwaway copy) is the integrator's.
const fixKindOf = (file, owner, changeRequest) => {
  if (file.startsWith('docs/plan/')) return 'plan-re-export'
  if (owner !== 'integrator') return 'workstream-edit'
  if (changeRequest && /^contracts\/[^/]+$/.test(file)) return 'change-request'
  return 'integrator-edit'
}

// ---------------------------------------------------------------- the issue surface, in code
const norm = (s) => String(s || '').replace(/^\s*\*\*Noun:\*\*/, '').replace(/`/g, '').trim().toLowerCase()
// A title is passed to gh inside double quotes, so it carries no character a shell expands there.
const titleOf = (f) => `${f.noun}: ${f.claim}`.replace(/[`$"\\]/g, '').replace(/\s+/g, ' ').trim().slice(0, 120)
const cell = (s) => String(s).replace(/\|/g, '\\|').replace(/\n/g, ' ')
const issueBody = (f, ctx) => {
  const L = []
  L.push(`**Owner:** ${f.sites[0].owner}`, '**Plan row:** none', `**Noun:** \`${f.noun}\``, '')
  L.push(`**Claim:** ${f.claim}`, '', `**Verdict:** ${f.verdict}, at origin/main ${ctx.sha}.`, '')
  L.push('## Sites', '', '| File | Line | Owner | Fix kind | Excerpt |', '| --- | --- | --- | --- | --- |')
  for (const s of f.sites) L.push(`| ${s.file} | ${s.line} | ${s.owner} | ${FIX_KINDS[s.fixKind]} | ${cell(s.excerpt)} |`)
  L.push('', '## Evidence', '')
  for (const e of f.evidence) L.push(`- ${e.where}: ${e.quote}`)
  L.push('', '## Proposed correction', '', f.correction || 'Not determinable from the tree; the owner decides.', '')
  if (f.sites.some((s) => s.fixKind === 'change-request')) L.push('A site is a frozen contract and the correction changes what it lists, so it waits for a change request under contracts/changes/, written by the owner beside the decision; the audit writes none.', '')
  if (f.sites.some((s) => s.fixKind === 'plan-re-export')) L.push('A site is under the plan export, which is never edited in place: its fix is a fresh export (docs/agents/domain.md).', '')
  L.push(`Audited at ${ctx.sha}; reported on the Truth audit ${ctx.date} issue.`, '')
  L.push('A correction that becomes a decision goes into a contract, a decision record or the glossary, not only into this issue (ADR-0020).', '')
  L.push('_Filed by the truth-audit workflow (#81)._')
  return L.join('\n')
}
const ruleBody = (r, f, ctx) => [
  `## Candidate rule (truth audit ${ctx.date})`, '',
  `**Rule:** ${r.key}`, '',
  `**Pattern:** ${r.pattern}`, '',
  '**Examples:**', ...r.examples.map((e) => `- ${e}`), '',
  `From the finding on \`${f.noun}\` at origin/main ${ctx.sha}. A rule for the steering check is its own ticket; this comment proposes and decides nothing.`,
].join('\n')

// ---------------------------------------------------------------- prompts
const setupPrompt = () => `Setup: fetch trunk and read what the run needs from it (#81 -> "Setup"). You write nothing outside the run log.

${HOUSE}

TREE PREFIXES: ${JSON.stringify([...new Set(FILES.filter((f) => !f.startsWith('/')).map(prefixOf))])}
ABSOLUTE FILES: ${JSON.stringify(FILES.filter((f) => f.startsWith('/')))}
RULE ISSUE: ${RULE_ISSUE}

1. Run \`git -C ${REPO} fetch origin --tags\`. If it fails, stop here: fetched false, listed false, reason its error, every other field empty, and write nothing anywhere. Progress is unverified without it (AGENTS.md).
2. sha is \`git -C ${REPO} rev-parse origin/main\`, the full 40 hex; date is \`date +%F\`. run is ${LOG}/<date>-<the sha's first 12 characters>. Create it with \`mkdir -p ${LOG}/<date>-<sha12>\`, then write the sha alone as the first line of the run log, \`git -C ${REPO} rev-parse origin/main > ${LOG}/<date>-<sha12>/run.log\`, and record the checkout as it is now, \`git -C ${REPO} status --porcelain --untracked-files=all > ${LOG}/<date>-<sha12>/checkout-at-start.txt\`.
3. tree: every file tracked at the sha under each TREE PREFIX ("" is the whole tree; a prefix that is a file is that file), with its word count: \`git -C ${REPO} ls-tree -r --name-only <sha> -- <prefix>\`, then for each path \`git -C ${REPO} show <sha>:<path> | wc -w\`. One entry per path, {path, words}.
4. absolute: for each ABSOLUTE FILE, {path, words: \`wc -w < <path>\`, exists}; a file that is not there is exists false and words 0.
5. ownership: OWNERSHIP.md at the sha verbatim, \`git -C ${REPO} show <sha>:OWNERSHIP.md\`.
6. openIssues: \`cd ${REPO} && gh issue list --state open --limit 500 --json number,title,body --jq '.[] | {number, title, noun: ((.body // "") | split("\\n") | map(select(startswith("**Noun:**"))) | first // "")}'\`, one {number, title, noun} per issue, noun the line verbatim or "".
7. ruleLines: \`cd ${REPO} && gh issue view ${RULE_ISSUE} --json comments --jq '.comments[].body | split("\\n")[] | select(startswith("**Rule:**"))'\`, each line verbatim.
listed is true only when both commands in 6 and 7 exited 0. If either fails, stop there: listed false, reason its error, openIssues and ruleLines []. An empty listing from a failed command is not an empty tracker: the duplicate, rule and same-day report checks would pass on it.
8. rows: the plan rows not yet landed, from tools/roadmap.py, which reads the working tree and its HEAD. Only when \`git -C ${REPO} rev-parse HEAD\` is the sha and \`git -C ${REPO} status --porcelain --untracked-files=no\` prints nothing does the working tree equal trunk: then run \`cd ${REPO} && python3 -I tools/roadmap.py --json\` (it writes nothing) and return known true and unlanded as every key of its "rows" whose value is not "landed". Otherwise known false, unlanded [], and reason which of the two failed.
Return every field; reason is "" when the fetch and both listings succeeded.`

const shardPrompt = (ctx, shard, k, phaseFile) => `Shard ${k}: extract the claims these files make about the tree (#81 -> "Phase Shard: claims"). You read only these files and you never verify: a later agent does.

${HOUSE}

PHASE FILE: ${phaseFile}
Write your return value to the PHASE FILE as JSON before you return.
SHARD: ${JSON.stringify(shard.files.map((f) => ({ path: f.path, words: f.words, read: f.read })))}

Read each file with the command its entry names, run as written; the trunk sha is ${ctx.sha}. A claim is a sentence that says something checkable about the code, the tests, the infrastructure or a vendor, and carries a noun: the token the sentence cannot paraphrase away, which the verifier will grep. A sentence with no noun (rationale, intention, opinion) is not a claim: record nothing for it. Code comments and test names are not this run's corpus.
For each claim return: id "<file>:<line>:<ordinal>" (ordinal counts claims on that line from 1); file as its entry names it; line; type, one of:
- identifier-exists: a type, member, rule id, meter, header, cookie name, environment variable or flag exists;
- writes-or-reads: a stage or entry point writes a field, counts a metric, reads a header;
- proves: a test id is proved by a project in a tier (H or D) and runs in CI;
- path-exists: a file or directory exists;
- count: a number of things ("nine of the ten", "48 cases");
- freeze-or-ownership-cell: a freeze table's cell, or a row of an ownership table;
- universal: every, never, only, the only, with the exceptions the sentence names;
- vendor: a documented AWS, ASP.NET Core, YARP or Duende behaviour;
the sentence as written; the noun; the scope, what the writer says has the property, in the writer's terms; exceptions, those a universal names ([] otherwise); anchor, a date, tag, gate, plan row or evidence id the sentence ties itself to ("" if none); vendor and document, for a vendor claim the vendor and the document named or implied ("" otherwise).`

const verifyPrompt = (ctx, batch, k, phaseFile) => `Verify ${k}: give each claim below a verdict against the tree at trunk (#81 -> "Phase Verify: verdicts").

${HOUSE}

PHASE FILE: ${phaseFile}
Write your return value to the PHASE FILE as JSON before you return.
CLAIMS: ${JSON.stringify(batch)}

The trunk sha is ${ctx.sha}. For each claim grep its noun once over the whole tracked tree at trunk, \`git -C ${REPO} grep -n -F -e <noun> ${ctx.sha}\`, and reuse the result for every claim that shares the noun. src/, tests/, fixtures/, infra/, contracts/, tools/ and .github/ decide the verdict; the prose and docs/plan/ supply the other sites. A file outside the repository (a claim whose file starts with /) is read with \`cat <file>\`, and its claims are still judged against trunk.

${RULES}

Per type:
- identifier-exists and path-exists: true when the thing is defined or present where the claim puts it.
- writes-or-reads: true when the named writer does what is said and no unnamed writer breaks the sentence's scope.
- proves: leg by leg. The id is attributed in the named project; the project is in SpaPlatform.slnx and runs under CI's test step (.github/workflows/ci.yml), or is the deployed suite for tier D; the test does not skip on a machine condition. Name the leg that fails.
- count: recompute the number from the tree.
- freeze-or-ownership-cell: a freeze cell is the steering check's, so on a green trunk report it true without re-deriving it. An ownership cell is compared line by line with OWNERSHIP.md's block at trunk, the last matching line winning.
- universal: enumerate the writers or instances, as the rule above says.
- vendor: only against the vendor's document fetched now, its address and the sentence that settles it as evidence.
Verdicts: true; false; stale, when false now and true at its anchor (the tree at the tag, \`git -C ${REPO} show <tag>:<path>\`, or the record's date for a decision record's context and decision sections); unverifiable, when it needs AWS, a deployed stack or a run you cannot make, with the reason in why. Each verdict: id as given; verdict; evidence as {where: "<trunk path>:<line>" or the document's address, quote: the line or sentence}; correction, the correct statement where it can be stated, else ""; why, one sentence. Return one verdict for every claim.`

const clusterPrompt = (ctx, given, phaseFile) => `Cluster: group the false and stale verdicts below into findings (#81 -> "Phase Cluster: findings"). You read; you write nothing but your phase file.

${HOUSE}

PHASE FILE: ${phaseFile}
Write your return value to the PHASE FILE as JSON before you return.
VERDICTS: ${JSON.stringify(given)}

The unit of a finding is the correction, not the sentence: group by noun across files, and merge what one correction would fix together, so three unbacked names in one table row are one finding when one edit retires them. Keep a false claim and a stale one apart. For each finding return: noun; claim, the false claim in a few words; verdict, false or stale; sites, every file and line it appears at, each with its excerpt, adding the sites \`git -C ${REPO} grep -n -F -e <noun> ${ctx.sha}\` finds in prose, in the plan export, in contracts/changes/ and in code comments; evidence, the lines that settle it; correction, where determinable, else ""; changeRequest, true when a site is a frozen contract (contracts/*.md) and the correction changes a stage, an owner, a signature, a schema, a rule id, a field name or a listed name; row, the plan row (an id from docs/plan/03-workstreams-and-tasks.md) whose code the claim waits for, when the claim sits in a contract and a row owes the code, else ""; rule, when the class is mechanical enough for the steering check, {key: a short kebab-case name, pattern: what the check would match, examples: the sites}, else {key: "", pattern: "", examples: []}. The code is the authority on what the code does. The script names each site's owner and fix kind from OWNERSHIP.md; you do not.`

const filePrompt = (ctx, writes, pendingFile) => `File: make exactly the GitHub writes below, in order, and nothing else (#81 -> "Phase File: issues"). The script chose them: duplicates, the cap and the rows that owe their code are already out.

${HOUSE}

PENDING FILE: ${JSON.stringify(pendingFile)}
${writes.map((w) => `WRITE ${w.key}: ${JSON.stringify({ action: w.action, title: w.title, label: w.label, issue: w.issue, bodyFile: w.bodyFile })}`).join('\n')}

For each WRITE, first write the text between BEGIN BODY <key> and END BODY <key>, without the markers, to its bodyFile. Then:
${writes.map((w) => (w.action === 'create'
    ? `- ${w.key}: \`cd ${REPO} && gh issue create --title "${w.title}" --label needs-triage --body-file ${w.bodyFile}\`. created gets {key: "${w.key}", number, url}.`
    : `- ${w.key}: \`cd ${REPO} && gh issue comment ${w.issue} --body-file ${w.bodyFile}\`. commented gets {key: "${w.key}", url}.`)).join('\n')}
A write that fails because GitHub cannot be reached is not retried: append it as one JSON line to the PENDING FILE, \`jq -cn --arg t "<its title>" --rawfile b <its bodyFile> '{issue: 0, action: "create", title: $t, label: "needs-triage", body: $b}' >> ${pendingFile}\` for a create, \`jq -cn --rawfile b <its bodyFile> '{issue: ${RULE_ISSUE}, action: "comment", body: $b}' >> ${pendingFile}\` for a comment, list it in pending as {key, action}, and go on to the next. Any other failure stops: reason is its error. Add no label, parent, assignee or milestone, and change no existing issue.
${writes.map((w) => `BEGIN BODY ${w.key}\n${w.body}\nEND BODY ${w.key}`).join('\n')}`

const CHECKOUT_FORMAT = { clean: '- Tracked files changed: none. New untracked files outside the run log: none.', dirty: '- Tracked files changed: <tracked>. New untracked files outside the run log: <untracked>.' }
const checkoutLine = (c) => (c.trackedChanged.length || c.newUntracked.length
  ? CHECKOUT_FORMAT.dirty.replace('<tracked>', c.trackedChanged.join(', ') || 'none').replace('<untracked>', c.newUntracked.join(', ') || 'none')
  : CHECKOUT_FORMAT.clean)

const reportPrompt = (ctx, text, target, outFile) => `Report: check the checkout, then write the run's report${DRY ? ' to the log alone: this is a dry run, and you make no GitHub call of any kind' : ' and post it'} (#81 -> "Phase Report").

${HOUSE}

OUTPUT FILE: ${outFile}
CHECKOUT FORMAT: ${JSON.stringify(CHECKOUT_FORMAT)}
${DRY ? '' : `REPORT ISSUE: ${JSON.stringify(target)}\n`}
1. The checkout check: \`git -C ${REPO} status --porcelain --untracked-files=all\` against ${ctx.run}/checkout-at-start.txt. trackedChanged lists each tracked file whose status differs (\`git -C ${REPO} status --porcelain --untracked-files=no\` shows them); newUntracked lists each untracked path not in the start file and not under ${LOG}/.
2. Write the text between BEGIN REPORT and END REPORT, without the markers, to ${outFile}, with its one line "CHECKOUT: pending" replaced by the CHECKOUT FORMAT's clean line when both lists are empty, else by its dirty line with <tracked> and <untracked> each the comma-separated list or "none". Change nothing else in it.
${DRY ? '3. Post nothing. issue is 0, url "" and posted false.' : target.mode === 'edit'
    ? `3. The day's report issue exists: \`cd ${REPO} && gh issue edit ${target.issue} --body-file ${outFile}\`. issue is ${target.issue}.`
    : `3. \`cd ${REPO} && gh issue create --title "${target.title}" --body-file ${outFile}\`, with no label and no parent. issue is its number.`}
${DRY ? '' : `If GitHub cannot be reached, append the write to ${ctx.run}/pending.jsonl, \`jq -cn --rawfile b ${outFile} '{issue: ${target.mode === 'edit' ? target.issue : 0}, action: "${target.mode === 'edit' ? 'edit' : 'create'}", title: "${target.title}", body: $b}' >> ${ctx.run}/pending.jsonl\`, and say so in reason.`}
Finally append one line to the run log, \`echo "truth-audit: report written" >> ${ctx.run}/run.log\`. Return checkout, issue, url, posted and reason ("" when nothing failed).
BEGIN REPORT
${text}
END REPORT`

// ---------------------------------------------------------------- the report, in code
const sampleOf = (claims, verdictById) => {
  const trues = claims.filter((c) => (verdictById.get(c.id) || {}).verdict === 'true')
  // Ten true claims across types and files: one per type in turn, each type's next from a file
  // not yet sampled where it can, so the integrator's spot-check reads the verifier widely.
  const byType = TYPES.map((t) => trues.filter((c) => c.type === t))
  const out = []
  const seenFiles = new Set()
  while (out.length < 10 && byType.some((l) => l.length)) {
    for (const l of byType) {
      if (out.length >= 10 || !l.length) continue
      const i = Math.max(0, l.findIndex((c) => !seenFiles.has(c.file)))
      const [c] = l.splice(i, 1)
      seenFiles.add(c.file)
      out.push(c)
    }
  }
  return out
}
const reportText = (s) => {
  const L = []
  const list = (title, items, none = 'none') => {
    L.push('', `### ${title}`, '')
    if (items.length) items.forEach((i) => L.push(`- ${i}`)); else L.push(none)
  }
  L.push(`## Truth audit ${s.date}`, '')
  L.push(`Trunk: origin/main ${s.sha}. Corpus: ${s.files.length} files, ${s.words} words, in ${s.shards} shards; ${s.claims.length} claims in ${s.batches} verify batches. ${DRY ? 'Dry run: nothing was filed or posted.' : `Cap: ${MAX_ISSUES} issues.`}`)
  if (s.failed.length) L.push('', `**Incomplete:** ${s.failed.length} agent(s) returned nothing, listed under "Agents that returned nothing"; what they held is missing from every count and list below.`)
  L.push('', '### Counts by type and verdict', '', `| Type | ${VERDICTS.join(' | ')} | no verdict |`, `| --- | ${VERDICTS.map(() => '---').join(' | ')} | --- |`)
  for (const t of TYPES) {
    const cs = s.claims.filter((c) => c.type === t)
    L.push(`| ${t} | ${VERDICTS.map((v) => cs.filter((c) => (s.verdictById.get(c.id) || {}).verdict === v).length).join(' | ')} | ${cs.filter((c) => !s.verdictById.has(c.id)).length} |`)
  }
  list(DRY ? 'Filed (Would file, on a run that is not dry)' : 'Filed', s.filed.map((f) => (DRY ? `Would file: ${f.title} (${f.sites} sites)` : `#${f.number} ${f.title} (${f.sites} sites)`)))
  if (s.failed.length) list('Agents that returned nothing', s.failed)
  list('Skipped', s.skipped.map((k) => `${k.noun}: ${k.reason}`))
  if (s.pending.length) list('Not written: GitHub unreachable', s.pending.map((p) => `${p.what}: appended to ${s.pendingFile}`))
  list('Stale claims', s.stale.map((c) => `${c.file}:${c.line} (${c.type}) ${c.noun}: ${(s.verdictById.get(c.id) || {}).why || ''}`), 'none, not filed')
  list('Unverifiable claims', s.unverifiable.map((c) => `${c.file}:${c.line} (${c.type}) ${c.noun}: ${(s.verdictById.get(c.id) || {}).why || ''}`))
  L.push('', '### Precision sample', '')
  if (s.sample.length) {
    s.sample.forEach((c, i) => {
      const v = s.verdictById.get(c.id) || { evidence: [] }
      L.push(`${i + 1}. ${c.file}:${c.line} (${c.type}) ${c.noun}: ${c.sentence.replace(/\n/g, ' ')} Evidence: ${v.evidence.map((e) => `${e.where} "${e.quote}"`).join('; ') || 'none'}`)
    })
  } else L.push('no true claims')
  L.push('', 'Judge these ten and answer "sample: N of 10 right" as a comment on this issue.')
  list('Candidate rules', s.rules.map((r) => `${r.key}: ${r.state}`))
  if (s.missing.length) list('Files not read', s.missing)
  if (!s.rows.known) list('Plan rows', [`the landed state is unknown (${s.rows.reason}), so a finding that names a row was reported and not filed`])
  L.push('', '### Checkout', '', 'CHECKOUT: pending')
  return L.join('\n')
}

// ---------------------------------------------------------------- run
phase('Shard')
const setup = await run(setupPrompt(), { label: 'setup', phase: 'Shard', schema: SETUP })
if (!setup || !setup.fetched) return { halted: `the fetch of origin failed${setup && setup.reason ? `: ${setup.reason}` : ''}; progress is unverified, and no claim was read`, agents: agentsRun }
if (!/^[0-9a-f]{40}$/.test(setup.sha) || !/^\d{4}-\d{2}-\d{2}$/.test(setup.date)) return { halted: `setup returned sha ${JSON.stringify(setup.sha)} and date ${JSON.stringify(setup.date)}; nothing was read`, agents: agentsRun }
// The listing feeds the duplicate check, the already-posted rule check and the same-day report
// dedupe; read as empty after a failure, all three pass with nothing judged, so the run stops here,
// dry or not (CODING_STANDARDS.md -> "A check fails closed").
if (!setup.listed) return { halted: `the open issues or #${RULE_ISSUE}'s comments could not be read${setup.reason ? `: ${setup.reason}` : ''}; the duplicate check cannot run, so no claim was read and nothing was filed`, sha: setup.sha, agents: agentsRun }
const RUN = `${LOG}/${setup.date}-${setup.sha.slice(0, 12)}`
const ctx = { sha: setup.sha, date: setup.date, run: RUN }
const { files, missing } = resolveFiles(FILES, setup.tree, setup.absolute)
for (const f of files) if (!f.read) f.read = `git -C ${REPO} show ${ctx.sha}:${f.path}`
if (!files.length) return { halted: `files resolved to nothing at ${ctx.sha}: ${missing.join('; ')}`, sha: ctx.sha, agents: agentsRun }
log(`truth audit at ${ctx.sha}: ${files.length} files; run log ${RUN}`)

const shards = shardsOf(files)
const sharded = await parallel(shards.map((s, i) => () => run(shardPrompt(ctx, s, i + 1, `${RUN}/shard-${i + 1}.json`), { label: `shard-${i + 1}`, phase: 'Shard', schema: SHARDED })))
// An agent that returns nothing is named here and in the report, never read as an empty result
// (CODING_STANDARDS.md -> "A check fails closed"); the run goes on with what the others returned.
const failed = []
const filesOf = (paths) => [...new Set(paths)].join(', ')
// The script holds the shard agents to their files and to the noun rule, and numbers the ids itself.
const claims = []
sharded.forEach((r, i) => {
  const mine = new Set(shards[i].files.map((f) => f.path))
  if (!r || !Array.isArray(r.claims)) { failed.push(`shard-${i + 1} (${filesOf(mine)}) returned nothing: its files' claims were not read`); return }
  for (const c of r.claims) {
    if (!mine.has(c.file) || !String(c.noun || '').trim()) continue
    const id = `${c.file}:${c.line}:${c.ordinal}`
    if (claims.some((x) => x.id === id)) continue
    claims.push({ ...c, id })
  }
})

phase('Verify')
const batches = batchesOf(claims)
const verified = await parallel(batches.map((b, i) => () => run(verifyPrompt(ctx, b, i + 1, `${RUN}/verify-${i + 1}.json`), { label: `verify-${i + 1}`, phase: 'Verify', schema: VERIFIED })))
const verdictById = new Map()
verified.forEach((r, i) => {
  const ids = new Set(batches[i].map((c) => c.id))
  if (!r || !Array.isArray(r.verdicts)) { failed.push(`verify-${i + 1} (${filesOf(batches[i].map((c) => c.file))}) returned nothing: ${ids.size} claim${ids.size === 1 ? '' : 's'} have no verdict`); return }
  for (const v of r.verdicts) if (ids.has(v.id) && !verdictById.has(v.id)) verdictById.set(v.id, v)
})
const verdictOf = (c) => (verdictById.get(c.id) || {}).verdict
const counts = Object.fromEntries(VERDICTS.map((v) => [v, claims.filter((c) => verdictOf(c) === v).length]))
counts.none = claims.filter((c) => !verdictById.has(c.id)).length

phase('Cluster')
// Only false and stale reach the cluster; an unverifiable claim is counted and never filed.
const given = claims.filter((c) => ['false', 'stale'].includes(verdictOf(c))).map((c) => ({ claim: c, verdict: verdictById.get(c.id) }))
const clustered = given.length ? await run(clusterPrompt(ctx, given, `${RUN}/cluster.json`), { label: 'cluster', phase: 'Cluster', schema: CLUSTERED }) : { findings: [] }
if (!clustered || !Array.isArray(clustered.findings)) failed.push(`the Cluster agent returned nothing: ${given.length} false or stale verdicts were not grouped, and no finding was filed from them`)
const owner = ownersOf(setup.ownership)
const unlanded = new Set(setup.rows.unlanded)
const findings = ((clustered && Array.isArray(clustered.findings) && clustered.findings) || []).filter((f) => f.sites.length).map((f) => ({
  ...f,
  sites: f.sites.map((s) => {
    const o = s.file.startsWith('/') ? 'integrator' : owner(s.file)
    return { ...s, owner: o, fixKind: fixKindOf(s.file, o, f.changeRequest) }
  }),
}))

phase('File')
// The order #81 sets: descending site count, then noun, so a rerun walks the same list.
const ordered = [...findings].sort((a, b) => b.sites.length - a.sites.length || (a.noun < b.noun ? -1 : a.noun > b.noun ? 1 : 0))
const open = setup.openIssues
const skipped = []
const toFile = []
for (const f of ordered) {
  const title = titleOf(f)
  if (f.verdict === 'stale') { skipped.push({ noun: f.noun, reason: 'stale: reported, never filed' }); continue }
  if (f.row && !setup.rows.known) { skipped.push({ noun: f.noun, reason: `names row ${f.row}, whose landed state is unknown: reported, not filed` }); continue }
  if (f.row && unlanded.has(f.row)) { skipped.push({ noun: f.noun, reason: `owed by row ${f.row}` }); continue }
  const dup = open.find((i) => i.title === title || (norm(i.noun) && norm(i.noun) === norm(f.noun)))
  if (dup) { skipped.push({ noun: f.noun, reason: `duplicate of #${dup.number}` }); continue }
  if (toFile.length >= MAX_ISSUES) { skipped.push({ noun: f.noun, reason: `over the cap of ${MAX_ISSUES} issues this run` }); continue }
  toFile.push({ f, title })
}
// A candidate rule is posted once across runs: not when the target's comments already hold it, and
// once per run whatever number of findings carry it.
const posted = new Set(setup.ruleLines.map((l) => l.replace(/^\*\*Rule:\*\*/, '').trim()))
const rules = []
for (const f of ordered) {
  const r = f.rule
  if (!r || !r.key || rules.some((x) => x.key === r.key)) continue
  rules.push({ key: r.key, rule: r, f, state: posted.has(r.key) ? `already posted on #${RULE_ISSUE}` : DRY ? `would be posted on #${RULE_ISSUE}` : 'to post' })
}
const writes = [
  ...toFile.map(({ f, title }, i) => ({ key: `finding-${i + 1}`, action: 'create', title, label: 'needs-triage', issue: 0, bodyFile: `${RUN}/finding-${i + 1}.md`, body: issueBody(f, ctx), noun: f.noun, sites: f.sites.length })),
  ...rules.filter((r) => r.state === 'to post').map((r, i) => ({ key: `rule-${i + 1}`, action: 'comment', title: '', label: '', issue: RULE_ISSUE, bodyFile: `${RUN}/rule-${i + 1}.md`, body: ruleBody(r.rule, r.f, ctx), rule: r })),
]
const pendingFile = `${RUN}/pending.jsonl`
const filed = []
const pending = []
let fileReason = ''
if (DRY) {
  for (const w of writes.filter((x) => x.action === 'create')) filed.push({ number: 0, url: '', title: w.title, noun: w.noun, sites: w.sites })
} else if (writes.length) {
  const done = await serial(() => run(filePrompt(ctx, writes, pendingFile), { label: 'file', phase: 'File', schema: FILED }))
  const byKey = new Map(writes.map((w) => [w.key, w]))
  for (const c of (done && done.created) || []) { const w = byKey.get(c.key); if (w && w.action === 'create') filed.push({ number: c.number, url: c.url, title: w.title, noun: w.noun, sites: w.sites }) }
  for (const c of (done && done.commented) || []) { const w = byKey.get(c.key); if (w && w.rule) w.rule.state = `posted on #${RULE_ISSUE}: ${c.url}` }
  for (const p of (done && done.pending) || []) {
    const w = byKey.get(p.key)
    if (!w) continue
    pending.push({ key: p.key, what: w.action === 'create' ? `the issue ${w.title}` : `the candidate rule ${w.rule.key} on #${RULE_ISSUE}` })
    if (w.rule) w.rule.state = `not posted: GitHub unreachable, pending in ${pendingFile}`
  }
  fileReason = (done && done.reason) || (done ? '' : 'the File agent returned nothing')
  if (!done) failed.push(`the File agent returned nothing: ${writes.length} write(s) unaccounted for, each under Skipped`)
  for (const w of writes) {
    const accounted = filed.some((f) => f.title === w.title && w.action === 'create') || pending.some((p) => p.key === w.key) || (w.rule && /^posted/.test(w.rule.state))
    if (!accounted) skipped.push({ noun: w.noun || w.rule.key, reason: `not written: ${fileReason || 'the File agent did not report it'}` })
  }
}

phase('Report')
const stale = claims.filter((c) => verdictOf(c) === 'stale')
const unverifiable = claims.filter((c) => verdictOf(c) === 'unverifiable')
const sample = sampleOf(claims, verdictById)
const text = reportText({
  date: ctx.date, sha: ctx.sha, files, words: files.reduce((n, f) => n + f.words, 0), shards: shards.length, batches: batches.length,
  claims, verdictById, filed, skipped, pending, pendingFile, stale, unverifiable, sample, rules, missing, rows: setup.rows, failed,
})
const day = open.find((i) => i.title === `Truth audit ${ctx.date}`)
const target = day ? { mode: 'edit', issue: day.number, title: `Truth audit ${ctx.date}` } : { mode: 'create', issue: 0, title: `Truth audit ${ctx.date}` }
const reportFile = `${RUN}/report.md`
const rep = await run(reportPrompt(ctx, text, target, reportFile), { label: 'report', phase: 'Report', schema: REPORTED })
const checkout = (rep && rep.checkout) || { trackedChanged: ['the report agent returned nothing'], newUntracked: [] }
// The report text is composed before its agent runs, so a Report that returned nothing is named in
// the return alone.
if (!rep) failed.push('the Report agent returned nothing: the report was not checked against the checkout, written or posted')
const finalText = text.replace(/^CHECKOUT: pending$/m, checkoutLine(checkout))
return {
  sha: ctx.sha, date: ctx.date, runLog: RUN, dryRun: DRY, corpus: files.map((f) => f.path), missing,
  claims: claims.length, verdicts: counts, findings: findings.length, failed,
  filed, skipped, pending: pending.map((p) => p.what), pendingFile: pending.length ? pendingFile : '',
  rules: rules.map((r) => ({ key: r.key, state: r.state })), ruleIssue: RULE_ISSUE,
  report: { issue: rep ? rep.issue : 0, url: rep ? rep.url : '', posted: rep ? rep.posted : false, reason: rep ? rep.reason : 'the report agent returned nothing', file: reportFile, text: finalText },
  checkoutClean: !!rep && checkout.trackedChanged.length === 0 && checkout.newUntracked.length === 0,
  agents: agentsRun,
}
