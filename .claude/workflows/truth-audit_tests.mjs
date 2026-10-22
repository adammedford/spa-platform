// The truth-audit script's test companion. Owner: integrator. Tracked beside the script (#267),
// named for it as morning_tests.mjs and retro_tests.mjs are for theirs.
//
//   node .claude/workflows/truth-audit_tests.mjs [script]
//
// Runs truth-audit.js end to end with agent, parallel, log and phase stubbed, and with gh and git
// stubbed where the script reaches them: through its agents, each of which the stub plays from a
// served fixture as its prompt asks. The Setup agent serves the fetch, the trunk sha, the trunk
// file list with word counts, OWNERSHIP.md at trunk, the open-issue listing and the candidate-rule
// target's **Rule:** lines; the Shard agents serve claims for the files their prompt names; the
// Verify agents serve verdicts for the claims their prompt carries; the Cluster agent groups what it
// is given by noun; the File agent creates what its prompt lists, or, when the stubbed network is
// down, appends the pending line its prompt gives; the Report agent writes the report its prompt
// carries and creates or edits the issue its prompt names. No agent, gh, git or network call is
// made. The script's one REPO line is pointed at a throwaway git repository with this repository's
// .gitignore, and every file a stub writes lands in it, so a run that writes outside its log shows
// in that repository's status. Every stub's return is validated against the schema the script
// passed. Exits 0 when every check holds, 1 when one fails (each failure printed), 2 when the
// script cannot run. Its last line is its verdict.
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { execFileSync, spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const scriptPath = process.argv[2] || path.join(here, 'truth-audit.js')
const top = path.resolve(here, '..', '..')
const SHA = 'a'.repeat(40)
const DATE = '2026-10-21'
const LOG_REL = '.scratch/truth-audit/log'
const REPO_LINE = /^const REPO = '\/Users\/adammedford\/Projects\/spa-platform'$/m
// A fixture's issue number, built so the steering check's citation search (#NNN must name an issue
// here) does not read the made-up issues of these fixtures as citations.
const cite = (n) => '#' + n

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

// ---------------------------------------------------------------- the trunk the stubs serve
// The default corpus as the spec draws it (40 files at 5e0d4e0, 46 with today's decision records):
// the contracts' top level, the decision records, the agent guides, and five root and docs files.
// Words are chosen so the corpus is 59,200 words, which is eight shards at 8,000 a shard;
// CLAUDE.md, contracts/changes, src and the plan export are in the tree and never in the corpus.
const CONTRACTS = ['README', 'http-contract', 'pipeline', 'request-gate', 'resource-tags', 's3-layout', 'telemetry', 'upstream-contract'].map((n) => `contracts/${n}.md`)
const ADRS = Array.from({ length: 27 }, (_, i) => `docs/adr/${String(i + 1).padStart(4, '0')}-record.md`)
const GUIDES = ['aws-rules', 'domain', 'issue-tracker', 'landing-work', 'review', 'triage-labels'].map((n) => `docs/agents/${n}.md`)
const ROOTS = ['GLOSSARY.md', 'CODING_STANDARDS.md', 'docs/roadmap.md', 'README.md', 'AGENTS.md']
const CORPUS = [...CONTRACTS, ...ADRS, ...GUIDES, ...ROOTS]
const TREE = [
  ...CONTRACTS.map((p) => ({ path: p, words: 3000 })),
  ...ADRS.map((p) => ({ path: p, words: 600 })),
  ...GUIDES.map((p) => ({ path: p, words: 1500 })),
  ...ROOTS.map((p) => ({ path: p, words: 2000 })),
  { path: 'CLAUDE.md', words: 200 },
  { path: 'contracts/changes/6.4-request-telemetry-feature.md', words: 900 },
  { path: 'contracts/telemetry.schema.json', words: 300 },
  { path: 'docs/plan/02-contracts.md', words: 9000 },
  { path: 'src/Platform.Telemetry/Meters.cs', words: 400 },
  { path: 'src/Platform.Registry/Registry.cs', words: 400 },
  { path: 'OWNERSHIP.md', words: 300 },
]
const OWNERSHIP = '# Path ownership\n\n```text\n*                                     integrator\n/src/Platform.Registry/               ws2-hosting\n/src/Platform.Telemetry/              ws6-verification\n/docs/runbooks/                       ws6-verification\n```\n'
const TYPES = ['identifier-exists', 'writes-or-reads', 'proves', 'path-exists', 'count', 'freeze-or-ownership-cell', 'universal', 'vendor']

const made = []
process.on('exit', () => { for (const dir of made) fs.rmSync(dir, { recursive: true, force: true }) })
const newRepo = () => {
  const repo = fs.mkdtempSync(path.join(os.tmpdir(), 'truth-audit-tests-'))
  made.push(repo)
  const git = (...a) => execFileSync('git', ['-C', repo, ...a], { encoding: 'utf8' })
  git('init', '-q')
  fs.copyFileSync(path.join(top, '.gitignore'), path.join(repo, '.gitignore'))
  fs.writeFileSync(path.join(repo, 'README.md'), 'tracked\n')
  fs.mkdirSync(path.join(repo, 'contracts'))
  fs.writeFileSync(path.join(repo, 'contracts', 'telemetry.md'), 'the working tree copy, never read\n')
  git('add', '-A')
  git('-c', 'user.name=t', '-c', 'user.email=t@t', 'commit', '-q', '-m', 'start')
  return { repo, git, thrown: [], schemaFailures: [], peak: 0, gh: [], pendingWrites: [] }
}

const dataLine = (prompt, marker) => {
  const m = new RegExp(`^${marker}: (.*)$`, 'm').exec(prompt)
  return m ? JSON.parse(m[1]) : null
}
const block = (prompt, name) => {
  const m = new RegExp(`^BEGIN ${name}\\n([\\s\\S]*?)\\nEND ${name}$`, 'm').exec(prompt)
  return m ? m[1] : null
}
const write = (file, text) => { fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, text) }

// The claims a Shard agent finds in a file: served.claims[file], else one true identifier claim.
const claimsIn = (served, file) => {
  if (served.claims && served.claims[file]) return served.claims[file]
  return [{ line: 3, type: 'identifier-exists', noun: `noun-${path.basename(file, '.md')}`, sentence: `The ${path.basename(file)} names a thing.` }]
}
const claimRecord = (file, c, ordinal) => ({
  id: `${file}:${c.line}:${ordinal}`, file, line: c.line, ordinal, type: c.type, sentence: c.sentence || `A sentence about ${c.noun}.`,
  noun: c.noun, scope: c.scope || 'the gateway', exceptions: c.exceptions || [], anchor: c.anchor || '', vendor: c.vendor || '', document: c.document || '',
})

// served: { fetchFails, tree, absolute: [{path, words}], openIssues, ruleLines, rowsKnown, unlanded,
//           claims: {file: [{line, type, noun, ...}]}, verdicts: {noun: verdict}, rules: {noun: rule},
//           rows: {noun: row}, changeRequest: [noun], ghDown: true, checkout: {trackedChanged, newUntracked},
//           nothingFrom: [label], the agents that return nothing, as a failed agent does }
function stub(env, scriptArgs, served) {
  const calls = []
  let inFlight = 0
  globalThis.args = scriptArgs
  globalThis.phase = () => {}
  globalThis.log = (m) => { env.logs = env.logs || []; env.logs.push(String(m)) }
  globalThis.parallel = (fns) => Promise.all(fns.map((f) => f().catch((e) => { env.thrown.push(e); return null })))
  const LOG = path.join(env.repo, LOG_REL)
  globalThis.agent = async (prompt, opts) => {
    const label = opts.label
    inFlight++
    env.peak = Math.max(env.peak, inFlight)
    try {
      await new Promise((r) => setTimeout(r, 3))
      calls.push({ label, prompt, schema: opts.schema })
      if ((served.nothingFrom || []).includes(label)) return null
      let out
      const run = `${LOG}/${DATE}-${SHA.slice(0, 12)}`
      if (label === 'setup') {
        if (served.fetchFails) {
          out = { fetched: false, listed: false, reason: 'fatal: unable to access github.com', sha: '', date: '', run: '', tree: [], absolute: [], ownership: '', openIssues: [], ruleLines: [], rows: { known: false, unlanded: [], reason: '' } }
        } else {
          const prefixes = dataLine(prompt, 'TREE PREFIXES') || []
          const tree = (served.tree || TREE).filter((f) => prefixes.some((p) => p === '' || f.path === p || f.path.startsWith(p + '/')))
          const absolute = (dataLine(prompt, 'ABSOLUTE FILES') || []).map((p) => {
            const a = (served.absolute || []).find((x) => x.path === p)
            return { path: p, words: a ? a.words : 0, exists: !!a }
          })
          write(`${run}/run.log`, `${SHA}\n`)
          out = {
            fetched: true, listed: !served.listFails, reason: served.listFails ? 'gh: error connecting to api.github.com' : '', sha: SHA, date: DATE, run, tree, absolute, ownership: OWNERSHIP,
            openIssues: served.listFails ? [] : served.openIssues || [], ruleLines: served.listFails ? [] : served.ruleLines || [],
            rows: { known: served.rowsKnown !== false, unlanded: served.unlanded || [], reason: served.rowsKnown === false ? 'the main checkout is not at trunk' : '' },
          }
        }
      } else if (label.startsWith('shard')) {
        const files = dataLine(prompt, 'SHARD') || []
        env.shards = env.shards || []
        env.shards.push(files)
        const claims = []
        for (const f of files) claimsIn(served, f.path).forEach((c, i) => claims.push(claimRecord(f.path, c, i + 1)))
        out = { claims }
      } else if (label.startsWith('verify')) {
        const claims = dataLine(prompt, 'CLAIMS') || []
        env.batches = env.batches || []
        env.batches.push(claims)
        out = {
          verdicts: claims.map((c) => {
            const v = (served.verdicts || {})[c.noun] || 'true'
            return {
              id: c.id, verdict: v, why: v === 'unverifiable' ? 'needs the deployed stack' : `the ${v} reason`,
              evidence: [{ where: v === 'false' ? 'src/Platform.Registry/Registry.cs:1' : `${c.file}:${c.line}`, quote: `evidence for ${c.noun}` }],
              correction: v === 'false' ? `${c.noun} is not created anywhere` : '',
            }
          }),
        }
      } else if (label === 'cluster') {
        const given = dataLine(prompt, 'VERDICTS') || []
        env.clustered = given
        const byNoun = new Map()
        for (const g of given) {
          if (!byNoun.has(g.claim.noun)) byNoun.set(g.claim.noun, [])
          byNoun.get(g.claim.noun).push(g)
        }
        out = {
          findings: [...byNoun.entries()].map(([noun, gs]) => ({
            noun, claim: `${noun} is named but never created`, verdict: gs[0].verdict.verdict,
            sites: gs.map((g) => ({ file: g.claim.file, line: g.claim.line, excerpt: g.claim.sentence })),
            evidence: gs[0].verdict.evidence, correction: gs[0].verdict.correction,
            changeRequest: (served.changeRequest || []).includes(noun), row: (served.rows || {})[noun] || '',
            rule: (served.rules || {})[noun] || { key: '', pattern: '', examples: [] },
          })),
        }
      } else if (label === 'file') {
        const pending = dataLine(prompt, 'PENDING FILE')
        const out1 = { created: [], commented: [], pending: [], reason: '' }
        for (const m of prompt.matchAll(/^WRITE (\S+): (.*)$/gm)) {
          const key = m[1]
          const w = JSON.parse(m[2])
          const body = block(prompt, `BODY ${key}`)
          write(w.bodyFile, body)
          if (served.ghDown) {
            const line = w.action === 'create'
              ? { issue: 0, action: 'create', title: w.title, label: w.label, body }
              : { issue: w.issue, action: 'comment', body }
            fs.appendFileSync(pending, JSON.stringify(line) + '\n')
            out1.pending.push({ key, action: w.action })
            continue
          }
          env.gh.push({ key, ...w, body })
          if (w.action === 'create') out1.created.push({ key, number: 900 + out1.created.length, url: `https://github.com/adammedford/spa-platform/issues/${900 + out1.created.length}` })
          else out1.commented.push({ key, url: `https://github.com/adammedford/spa-platform/issues/${w.issue}#issuecomment-${out1.commented.length + 1}` })
        }
        out = out1
      } else if (label === 'report') {
        const text = block(prompt, 'REPORT')
        const outFile = (/^OUTPUT FILE: (\S+)$/m.exec(prompt) || [])[1]
        const ck = served.checkout || { trackedChanged: [], newUntracked: [] }
        const line = (dataLine(prompt, 'CHECKOUT FORMAT') || { clean: '', dirty: '' })
        const filled = text.replace(/^CHECKOUT: pending$/m, ck.trackedChanged.length || ck.newUntracked.length
          ? line.dirty.replace('<tracked>', ck.trackedChanged.join(', ') || 'none').replace('<untracked>', ck.newUntracked.join(', ') || 'none')
          : line.clean)
        write(outFile, filled)
        const target = dataLine(prompt, 'REPORT ISSUE')
        let issue = 0
        if (target && target.mode === 'edit') { issue = target.issue; env.gh.push({ action: 'edit', issue, body: filled }) }
        if (target && target.mode === 'create') { issue = 950; env.gh.push({ action: 'create', title: target.title, body: filled }) }
        out = { checkout: ck, issue, url: issue ? `https://github.com/adammedford/spa-platform/issues/${issue}` : '', posted: !!issue, reason: '' }
      } else throw new Error(`unexpected agent label ${label}`)
      const bad = validate(opts.schema, out)
      if (bad.length) env.schemaFailures.push(`${label}: ${bad.slice(0, 3).join('; ')}`)
      for (const x of prompt.matchAll(/^PHASE FILE: (\S+)$/gm)) write(x[1], JSON.stringify(out))
      return out
    } finally { inFlight-- }
  }
  return calls
}

const allCalls = []
const driftAll = []
async function execute(env, scriptArgs = {}, served = {}) {
  const calls = stub(env, scriptArgs, served)
  const raw = fs.readFileSync(scriptPath, 'utf8')
  if (!REPO_LINE.test(raw)) throw new Error('the script has no line `const REPO = \'/Users/adammedford/Projects/spa-platform\'` to point at a throwaway repository')
  const src = raw.replace(REPO_LINE, `const REPO = ${JSON.stringify(env.repo)}`).replace(/^export const meta/m, 'const meta')
  const result = await new Function('return (async () => {' + src + '\n})()')()
  for (const c of calls) allCalls.push({ ...c, dry: scriptArgs.dryRun === true })
  for (const f of env.schemaFailures.splice(0)) driftAll.push(f)
  for (const t of env.thrown.splice(0)) driftAll.push(`a thunk threw: ${t}`)
  return { result, calls, labels: calls.map((c) => c.label), prompt: (l) => (calls.find((c) => c.label === l) || {}).prompt || '' }
}
const loadMeta = () => {
  const src = fs.readFileSync(scriptPath, 'utf8')
  const m = /^export const meta = ([\s\S]*?\n})\n/m.exec(src)
  return m ? new Function(`return (${m[1]})`)() : null
}
// The commands a prompt asks an agent to run: its backtick spans, outside the data it carries (a
// BEGIN/END block, or a NAME: [json] line), which quote the corpus and are never run.
const commands = (prompt) => {
  const text = prompt.replace(/^BEGIN (\S+(?: \S+)?)\n[\s\S]*?\nEND \1$/gm, '').replace(/^[A-Z][A-Z ]*(?: \S+)?: [[{].*$/gm, '')
  return [...text.matchAll(/`([^`\n]+)`/g)].map((m) => m[1])
}
const GH_WRITE = /\bgh\s+(issue\s+(create|edit|comment|close|reopen|delete|lock|transfer|pin)|label|api\b[^`]*(-X|--method)|pr\s+(create|edit|comment|merge|close))/

const failures = []
const fail = (msg) => failures.push(msg)
let checks = 0
// Two false findings for the duplicate, cap and shape checks: one with three sites, one with one.
const TWO = {
  claims: {
    'contracts/telemetry.md': [{ line: 15, type: 'identifier-exists', noun: 'registry.revision' }, { line: 61, type: 'identifier-exists', noun: 'registry.revision' }],
    'docs/plan/02-contracts.md': [{ line: 377, type: 'identifier-exists', noun: 'registry.revision' }],
    'contracts/pipeline.md': [{ line: 40, type: 'writes-or-reads', noun: 'X-Planted-Header' }],
  },
  verdicts: { 'registry.revision': 'false', 'X-Planted-Header': 'false' },
}
const withPlan = (a = {}) => ({ files: [...CORPUS, 'docs/plan/02-contracts.md'], ...a })

try {
  // 1. Box 2: meta names truth-audit and the five phases; exactly files, maxParallel, maxIssues and
  //    dryRun are accepted, and anything else stops the run before any agent.
  checks++
  {
    const meta = loadMeta()
    if (!meta || meta.name !== 'truth-audit') fail(`meta.name is ${meta && meta.name}, expected truth-audit`)
    const titles = ((meta && meta.phases) || []).map((p) => p.title)
    if (titles.join() !== 'Shard,Verify,Cluster,File,Report') fail(`meta phases ${titles.join(', ')}; expected Shard, Verify, Cluster, File, Report`)
    for (const [label, a] of [
      ['an unknown argument', { repo: '/tmp/x' }],
      ['maxParallel zero', { maxParallel: 0 }],
      ['maxIssues negative', { maxIssues: -1 }],
      ['dryRun as a string', { dryRun: 'true' }],
      ['files not strings', { files: [3] }],
      ['an absolute path inside the repository', (repo) => ({ files: [`${repo}/contracts/telemetry.md`] })],
    ]) {
      const env = newRepo()
      const { result, calls } = await execute(env, typeof a === 'function' ? a(env.repo) : a)
      if (!result || !/bad launch/.test(result.halted || '') || calls.length) fail(`${label}: ${calls.length} agent(s) ran; returned ${JSON.stringify(result)}`)
    }
    const { result } = await execute(newRepo(), { files: ['contracts/telemetry.md'], maxParallel: 2, maxIssues: 1, dryRun: true })
    if (result.halted) fail(`the four arguments were refused: ${result.halted}`)
  }

  // 2. Box 4: a failing fetch stops the run before any other agent, and says progress is unverified.
  checks++
  {
    const env = newRepo()
    const { result, labels } = await execute(env, {}, { fetchFails: true })
    if (labels.join() !== 'setup') fail(`a failed fetch dispatched ${labels.join(', ')}, expected the setup agent alone`)
    if (!/progress is unverified/.test(result.halted || '')) fail(`a failed fetch returned ${JSON.stringify(result)}`)
    const ok = await execute(newRepo())
    if (ok.result.halted || !ok.labels.some((l) => l.startsWith('shard'))) fail(`a good fetch: ${ok.labels.join(', ')} -> ${ok.result.halted}`)
    const p = ok.prompt('setup')
    if (!(p.indexOf('fetch origin --tags') > 0 && p.indexOf('fetch origin --tags') < p.indexOf('run.log'))) fail('the setup prompt does not fetch before it writes the run log')
    if (ok.result.sha !== SHA) fail(`the return names sha ${ok.result.sha}`)
    const runLog = fs.readFileSync(path.join(ok.result.runLog, 'run.log'), 'utf8')
    if (runLog.split('\n')[0] !== SHA) fail(`the run log's first line is ${JSON.stringify(runLog.split('\n')[0])}, not the sha`)
    // A failed open-issue or rule-target listing halts too, dry or not: read as empty, it would pass
    // the duplicate, rule and same-day report checks with nothing judged.
    for (const dry of [false, true]) {
      const down = newRepo()
      const d = await execute(down, withPlan({ dryRun: dry }), { ...TWO, listFails: true })
      if (d.labels.join() !== 'setup' || down.gh.length) fail(`a failed listing (dryRun ${dry}) dispatched ${d.labels.join(', ')} and wrote ${down.gh.length} time(s)`)
      if (!/could not be read[\s\S]*nothing was filed/.test(d.result.halted || '') || !/api\.github\.com/.test(d.result.halted || '')) fail(`a failed listing (dryRun ${dry}) returned ${JSON.stringify(d.result)}`)
    }
    if (!/listed false/.test(p)) fail('the setup prompt does not say what a failed listing returns')
  }

  // 3. Box 5: files resolution. A relative path is read at the trunk sha through git; a glob
  //    expands against the stubbed trunk file list; an absolute path is read from disk; the default
  //    is the corpus.
  checks++
  {
    const def = await execute(newRepo())
    const read = (def.result.corpus || []).slice().sort()
    if (read.join() !== CORPUS.slice().sort().join()) fail(`the default corpus is ${read.length} files (${read.filter((f) => !CORPUS.includes(f)).join(', ')} extra; ${CORPUS.filter((f) => !read.includes(f)).join(', ')} missing), expected the ${CORPUS.length}`)
    const sharded = (def.calls.filter((c) => c.label.startsWith('shard')).flatMap((c) => dataLine(c.prompt, 'SHARD') || [])).map((f) => f.path).sort()
    if (sharded.join() !== CORPUS.slice().sort().join()) fail('the shard agents were not given exactly the default corpus')
    const rel = await execute(newRepo(), { files: ['contracts/telemetry.md'] })
    const f = (dataLine(rel.prompt('shard-1'), 'SHARD') || [])[0] || {}
    if (f.path !== 'contracts/telemetry.md' || f.read !== `git -C ${rel.result.runLog.split('/.scratch/')[0]} show ${SHA}:contracts/telemetry.md`) fail(`a relative path is read with ${JSON.stringify(f.read)}`)
    if (!rel.prompt('shard-1').includes(`\`${f.read}\``) && !/read each file with the command its entry names/i.test(rel.prompt('shard-1'))) fail('the shard prompt does not say to read with the given command')
    const glob = await execute(newRepo(), { files: ['contracts/*.md'] })
    const globbed = (glob.result.corpus || []).slice().sort()
    if (globbed.join() !== CONTRACTS.slice().sort().join()) fail(`contracts/*.md expanded to ${globbed.join(', ')}`)
    if (!(dataLine(glob.prompt('setup'), 'TREE PREFIXES') || []).includes('contracts')) fail('the glob\'s directory was not asked of the trunk listing')
    const deep = await execute(newRepo(), { files: ['contracts/**/*.md'] })
    if (!(deep.result.corpus || []).includes('contracts/changes/6.4-request-telemetry-feature.md') || (deep.result.corpus || []).includes('contracts/telemetry.schema.json')) fail(`contracts/**/*.md expanded to ${(deep.result.corpus || []).join(', ')}`)
    const abs = await execute(newRepo(), { files: ['/tmp/copy/browser.md'] }, { absolute: [{ path: '/tmp/copy/browser.md', words: 1200 }] })
    const af = (dataLine(abs.prompt('shard-1'), 'SHARD') || [])[0] || {}
    if (af.path !== '/tmp/copy/browser.md' || af.read !== 'cat /tmp/copy/browser.md') fail(`an absolute path is read with ${JSON.stringify(af.read)}`)
    if (!(dataLine(abs.prompt('setup'), 'ABSOLUTE FILES') || []).includes('/tmp/copy/browser.md')) fail('the absolute file was not measured on disk by setup')
    const none = await execute(newRepo(), { files: ['contracts/nothing-*.md', 'docs/missing.md'] })
    if (!none.result.halted || none.labels.some((l) => l.startsWith('shard'))) fail(`files that resolve to nothing: ${none.labels.join(', ')} -> ${JSON.stringify(none.result.halted)}`)
    if (!/docs\/missing\.md/.test(none.result.halted || '')) fail('the unresolved path is not named')
  }

  // 4. Box 6: sharding near the word constant, one Shard agent per shard; Verify batched by file at
  //    the batch constant; never more than maxParallel agents in flight.
  checks++
  {
    const env = newRepo()
    const many = {}
    // contracts/telemetry.md carries 130 claims, the other 45 files one each: 175 claims.
    many['contracts/telemetry.md'] = Array.from({ length: 130 }, (_, i) => ({ line: i + 1, type: 'count', noun: `n${i}` }))
    const { result, labels } = await execute(env, {}, { claims: many })
    const shards = env.shards || []
    if (shards.length !== 8 || labels.filter((l) => l.startsWith('shard')).length !== 8) fail(`59,200 words made ${shards.length} shards, expected 8 at 8,000 words`)
    const words = (s) => s.reduce((n, f) => n + (TREE.find((t) => t.path === f.path) || {}).words, 0)
    for (const s of shards) if (words(s) > 9000) fail(`a shard of ${words(s)} words is not near the 8,000 constant`)
    const batches = env.batches || []
    if (batches.some((b) => b.length > 60)) fail(`a verify batch holds ${Math.max(...batches.map((b) => b.length))} claims, over 60`)
    if (batches.length !== 4) fail(`175 claims made ${batches.length} verify batches, expected 4 (130 in three of one file, 45 in one)`)
    const tele = batches.filter((b) => b.some((c) => c.file === 'contracts/telemetry.md'))
    if (tele.length !== 3 || tele.some((b) => b.some((c) => c.file !== 'contracts/telemetry.md'))) fail('the file with 130 claims was not batched on its own at 60 a batch')
    if (result.claims !== 175) fail(`the return counts ${result.claims} claims, expected 175`)
    if (env.peak !== 3) fail(`the default maxParallel let ${env.peak} agents run at once, expected exactly 3`)
    const two = newRepo()
    await execute(two, { maxParallel: 2 })
    if (two.peak !== 2) fail(`maxParallel 2 let ${two.peak} agents run at once`)
    const one = newRepo()
    await execute(one, { maxParallel: 1 })
    if (one.peak !== 1) fail(`maxParallel 1 let ${one.peak} agents run at once`)
  }

  // 5. Box 7: dryRun issues no gh write of any kind; the report is in the return value and a log file.
  checks++
  {
    const env = newRepo()
    const { result, calls, labels } = await execute(env, withPlan({ dryRun: true }), TWO)
    if (env.gh.length) fail(`a dry run wrote to GitHub: ${JSON.stringify(env.gh.map((g) => g.action))}`)
    if (labels.includes('file')) fail('a dry run dispatched the File agent')
    for (const c of calls) for (const cmd of commands(c.prompt)) if (GH_WRITE.test(cmd)) fail(`the dry run's ${c.label} prompt asks for a gh write: ${cmd}`)
    if (!result.report || !/^## Truth audit 2026-10-21/.test(result.report.text || '')) fail(`a dry run's return carries no report: ${JSON.stringify(result.report)}`)
    const onDisk = result.report && result.report.file && fs.existsSync(result.report.file) ? fs.readFileSync(result.report.file, 'utf8') : ''
    if (!onDisk || onDisk !== result.report.text) fail('the dry run\'s report file is missing or differs from the returned report')
    if (!result.report.file.startsWith(path.join(env.repo, LOG_REL) + '/')) fail(`the report file ${result.report.file} is not under the run log`)
    if (!/Would file/.test(result.report.text) || !/registry\.revision/.test(result.report.text)) fail('the dry run\'s report does not say what it would file')
    // The same run, not dry, does write: the search above can fail.
    const wet = newRepo()
    const w = await execute(wet, withPlan(), TWO)
    if (!wet.gh.length || !w.calls.some((c) => commands(c.prompt).some((cmd) => GH_WRITE.test(cmd)))) fail('a run that is not dry wrote nothing, so the dry run\'s silence proves nothing')
  }

  // 6. Box 8a: an open issue whose **Noun:** line matches a finding skips it as a duplicate of its #NNN.
  checks++
  {
    const env = newRepo()
    const open = [{ number: 412, title: 'something else entirely', noun: '**Noun:** `registry.revision`' }]
    const { result } = await execute(env, withPlan(), { ...TWO, openIssues: open })
    const dup = (result.skipped || []).find((s) => s.noun === 'registry.revision')
    if (!dup || dup.reason !== `duplicate of ${cite(412)}`) fail(`a matching Noun line: skipped ${JSON.stringify(result.skipped)}`)
    if (env.gh.some((g) => /registry\.revision/.test(g.title || ''))) fail('the duplicate was filed anyway')
    if (!env.gh.some((g) => /^X-Planted-Header: /.test(g.title || ''))) fail('the other finding was not filed beside the duplicate')
    if (!new RegExp(`registry\\.revision[^\\n]*duplicate of ${cite(412)}`).test(result.report.text)) fail('the report does not name the duplicate and its issue')
    const byTitle = await execute(newRepo(), withPlan(), { ...TWO, openIssues: [{ number: 413, title: 'X-Planted-Header: X-Planted-Header is named but never created', noun: '' }] })
    if (!(byTitle.result.skipped || []).some((s) => s.noun === 'X-Planted-Header' && s.reason === `duplicate of ${cite(413)}`)) fail(`a matching title: skipped ${JSON.stringify(byTitle.result.skipped)}`)
  }

  // 7. Box 8b: maxIssues 1 with two findings files one, the one with more sites, and reports the
  //    other as over the cap.
  checks++
  {
    const env = newRepo()
    const { result } = await execute(env, withPlan({ maxIssues: 1 }), TWO)
    const creates = env.gh.filter((g) => g.action === 'create' && !/^Truth audit/.test(g.title))
    if (creates.length !== 1 || !/^registry\.revision: /.test(creates[0].title)) fail(`maxIssues 1 filed ${JSON.stringify(creates.map((c) => c.title))}`)
    const over = (result.skipped || []).find((s) => s.noun === 'X-Planted-Header')
    if (!over || !/over the cap/.test(over.reason)) fail(`the second finding: ${JSON.stringify(result.skipped)}`)
    if ((result.filed || []).length !== 1) fail(`the return names ${(result.filed || []).length} filed`)
    const both = newRepo()
    await execute(both, withPlan({ maxIssues: 2 }), TWO)
    if (both.gh.filter((g) => g.action === 'create' && !/^Truth audit/.test(g.title)).length !== 2) fail('maxIssues 2 did not file both findings')
  }

  // 8. Box 9: stale, unverifiable and owed-by-an-unlanded-row findings are reported and never filed.
  checks++
  {
    const env = newRepo()
    const served = {
      claims: {
        'docs/adr/0018-record.md': [{ line: 9, type: 'path-exists', noun: 'Old.Path', anchor: '2026-10-12' }],
        'contracts/request-gate.md': [{ line: 20, type: 'writes-or-reads', noun: 'bucket.refuses' }],
        'contracts/telemetry.md': [{ line: 15, type: 'identifier-exists', noun: 'auth.logins' }],
        'contracts/pipeline.md': [{ line: 40, type: 'writes-or-reads', noun: 'X-Planted-Header' }],
      },
      verdicts: { 'Old.Path': 'stale', 'bucket.refuses': 'unverifiable', 'auth.logins': 'false', 'X-Planted-Header': 'false' },
      rows: { 'auth.logins': '3.4' }, unlanded: ['3.4', '6.8'],
    }
    const { result } = await execute(env, {}, served)
    const titles = env.gh.filter((g) => g.action === 'create').map((g) => g.title)
    for (const n of ['Old.Path', 'bucket.refuses', 'auth.logins']) if (titles.some((t) => t.startsWith(n + ':'))) fail(`${n} was filed`)
    if (!titles.some((t) => t.startsWith('X-Planted-Header:'))) fail('the false finding beside them was not filed, so the check proves nothing')
    const t = result.report.text
    if (!/### Stale claims[\s\S]*Old\.Path/.test(t)) fail('the stale claim is not reported')
    if (!/### Unverifiable claims[\s\S]*bucket\.refuses[^\n]*needs the deployed stack/.test(t)) fail('the unverifiable claim is not reported with its reason')
    if (!(result.skipped || []).some((s) => s.noun === 'auth.logins' && s.reason === 'owed by row 3.4')) fail(`the owed finding: ${JSON.stringify(result.skipped)}`)
    if (!(env.clustered || []).every((g) => ['false', 'stale'].includes(g.verdict.verdict))) fail('the cluster agent was given a verdict other than false or stale')
    // Unknown row state: a finding that names a row is reported, not filed, since its row may be unlanded.
    const unk = newRepo()
    const u = await execute(unk, {}, { ...served, rowsKnown: false })
    if (unk.gh.some((g) => (g.title || '').startsWith('auth.logins:')) || !(u.result.skipped || []).some((s) => s.noun === 'auth.logins' && /row 3\.4/.test(s.reason))) fail(`with the rows unknown, the row's finding: ${JSON.stringify(u.result.skipped)}`)
  }

  // 9. Box 10: every would-be issue has the <noun>: title, exactly one label, needs-triage, no
  //    parent, an owner line, **Plan row:** none and a **Noun:** line; the plan export's site is a
  //    re-export and a workstream path's owner is its owner.
  checks++
  {
    const env = newRepo()
    const served = { ...TWO, claims: { ...TWO.claims, 'docs/runbooks/x.md': [{ line: 2, type: 'identifier-exists', noun: 'registry.revision' }] } }
    const { calls } = await execute(env, { files: [...CORPUS, 'docs/plan/02-contracts.md', 'docs/runbooks/x.md'] }, { ...served, tree: [...TREE, { path: 'docs/runbooks/x.md', words: 100 }] })
    const creates = env.gh.filter((g) => g.action === 'create' && !/^Truth audit/.test(g.title))
    if (creates.length !== 2) fail(`expected two creates, got ${creates.length}`)
    for (const c of creates) {
      if (!/^(registry\.revision|X-Planted-Header): \S/.test(c.title)) fail(`title ${JSON.stringify(c.title)}`)
      if (/[`$"\\]/.test(c.title)) fail(`title carries a shell metacharacter: ${c.title}`)
      if (c.label !== 'needs-triage') fail(`label ${JSON.stringify(c.label)}`)
      const lines = c.body.split('\n')
      if (!/^\*\*Owner:\*\* \S/.test(lines[0]) || lines[1] !== '**Plan row:** none' || !/^\*\*Noun:\*\* /.test(lines[2])) fail(`body opens ${JSON.stringify(lines.slice(0, 3))}`)
      if (!/a contract, a decision record or the glossary/.test(c.body)) fail('the body does not say where a correction that becomes a decision goes')
      if (!c.body.includes(SHA)) fail('the body does not name the sha audited')
    }
    const reg = creates.find((c) => c.title.startsWith('registry.revision:')) || { body: '' }
    if (!/\| docs\/plan\/02-contracts\.md \| 377 \| integrator \| plan re-export \|/.test(reg.body)) fail('the plan export\'s site is not a re-export')
    if (!/\| docs\/runbooks\/x\.md \| 2 \| ws6-verification \| workstream edit \|/.test(reg.body)) fail('the runbook\'s site does not carry its owner from OWNERSHIP.md')
    if (!/\| contracts\/telemetry\.md \| 15 \| integrator \| integrator edit \|/.test(reg.body)) fail('the contract\'s site is not an integrator edit')
    const fp = (calls.find((c) => c.label === 'file') || {}).prompt || ''
    for (const cmd of commands(fp)) {
      if (/gh issue create/.test(cmd) && (/--parent\b/.test(cmd) || (cmd.match(/--label\b/g) || []).length !== 1 || !/--label needs-triage\b/.test(cmd))) fail(`a create command: ${cmd}`)
      if (/gh issue edit/.test(cmd) && /--(add|remove)-label/.test(cmd)) fail(`the file prompt changes a label: ${cmd}`)
    }
    if (!commands(fp).some((c) => /gh issue create/.test(c))) fail('the file prompt carries no create command, so the label search read nothing')
    const cr = newRepo()
    await execute(cr, withPlan(), { ...TWO, changeRequest: ['registry.revision'] })
    const crBody = (cr.gh.find((g) => (g.title || '').startsWith('registry.revision:')) || { body: '' }).body
    if (!/\| contracts\/telemetry\.md \| 15 \| integrator \| change request \|/.test(crBody)) fail('a contract site whose correction changes a listed name is not a change request')
  }

  // 10. Box 11: a candidate-rule comment is posted only when a finding carries a rule, and not again
  //     when the target's comments already hold it.
  checks++
  {
    const rule = { key: 'telemetry-metric-has-a-creation-site', pattern: 'every name in the Metrics row is created in src/', examples: ['registry.revision'] }
    const none = newRepo()
    await execute(none, withPlan(), TWO)
    if (none.gh.some((g) => g.action === 'comment')) fail('a comment was posted with no candidate rule')
    const env = newRepo()
    const { result } = await execute(env, withPlan(), { ...TWO, rules: { 'registry.revision': rule } })
    const posted = env.gh.filter((g) => g.action === 'comment')
    if (posted.length !== 1 || !posted[0].body.includes(`**Rule:** ${rule.key}`) || !posted[0].body.includes('registry.revision')) fail(`a rule: posted ${JSON.stringify(posted)}`)
    if (posted.length && posted[0].issue !== result.ruleIssue) fail(`the rule went to ${posted[0].issue}, not the named target ${result.ruleIssue}`)
    const again = newRepo()
    const a = await execute(again, withPlan(), { ...TWO, rules: { 'registry.revision': rule }, ruleLines: [`**Rule:** ${rule.key}`] })
    if (again.gh.some((g) => g.action === 'comment')) fail('the rule was posted again although the target already holds it')
    if (!/already posted/.test(a.result.report.text)) fail('the report does not say the rule was already posted')
    if (!a.prompt('setup').includes(`gh issue view ${result.ruleIssue} --json comments`)) fail('setup does not read the target\'s comments')
  }

  // 11. Box 12: a gh write that fails on the network appends one line to the run log's pending file,
  //     and the report names it.
  checks++
  {
    const env = newRepo()
    const { result } = await execute(env, withPlan({ maxIssues: 1 }), { ...TWO, ghDown: true })
    const pendingFile = path.join(result.runLog, 'pending.jsonl')
    const lines = fs.existsSync(pendingFile) ? fs.readFileSync(pendingFile, 'utf8').split('\n').filter(Boolean) : []
    if (lines.length !== 1) fail(`the pending file holds ${lines.length} lines, expected 1`)
    else {
      const l = JSON.parse(lines[0])
      if (l.action !== 'create' || !/^registry\.revision: /.test(l.title) || !/^\*\*Owner:\*\*/.test(l.body)) fail(`the pending line: ${lines[0].slice(0, 200)}`)
    }
    if (!/### Not written: GitHub unreachable[\s\S]*registry\.revision/.test(result.report.text) || !result.report.text.includes(pendingFile)) fail('the report does not name the pending write and its file')
    if ((result.filed || []).length) fail('a pending write was reported as filed')
  }

  // 12. Box 13: an open Truth audit <date> issue from the same day is edited, and no second created.
  checks++
  {
    const env = newRepo()
    const { result, prompt } = await execute(env, withPlan(), { ...TWO, openIssues: [{ number: 420, title: `Truth audit ${DATE}`, noun: '' }, { number: 419, title: 'Truth audit 2026-10-20', noun: '' }] })
    const p = prompt('report')
    if (!p.includes('`cd ' + env.repo + ' && gh issue edit 420 --body-file') || /gh issue create/.test(commands(p).join('\n'))) fail('the report prompt does not edit the day\'s issue alone')
    if (env.gh.some((g) => g.action === 'create' && /^Truth audit/.test(g.title)) || !env.gh.some((g) => g.action === 'edit' && g.issue === 420)) fail(`the report: ${JSON.stringify(env.gh.map((g) => [g.action, g.issue || g.title]))}`)
    if (result.report.issue !== 420) fail(`the return names report issue ${result.report.issue}`)
    const fresh = newRepo()
    const f = await execute(fresh, withPlan(), { ...TWO, openIssues: [{ number: 419, title: 'Truth audit 2026-10-20', noun: '' }] })
    const fc = commands(f.prompt('report')).find((c) => /gh issue create/.test(c)) || ''
    if (!fc.includes('--title "Truth audit 2026-10-21"') || /--label|--parent/.test(fc)) fail(`no same-day issue: the create is ${JSON.stringify(fc)}`)
  }

  // 13. Box 14: across every mocked run, no command writes to a tracked path in the main checkout:
  //     every redirect, tee and mkdir lands under the log, git only reads, and the throwaway
  //     repository the runs wrote into shows no change outside its log.
  checks++
  {
    const READS = /^(fetch|rev-parse|show|grep|ls-tree|status|log|cat-file|ls-files|worktree list)\b/
    let spans = 0
    for (const c of allCalls) {
      const repo = (/^\s*- The main checkout (\S+) is Adam's/m.exec(c.prompt) || [])[1] || ''
      for (const cmd of commands(c.prompt)) {
        spans++
        for (const m of cmd.matchAll(/(?:(?:^|\s)\d?>>?|\btee(?: -a)?|\bmkdir -p)\s*(\S+)/g)) {
          if (!m[1].startsWith(`${repo}/${LOG_REL}/`) && !m[1].startsWith('<')) fail(`the ${c.label} prompt writes to ${m[1]}: ${cmd}`)
        }
        for (const m of cmd.matchAll(/\bgit -C (\S+) (\S+(?: \S+)?)/g)) if (!READS.test(m[2])) fail(`the ${c.label} prompt runs git ${m[2]}: ${cmd}`)
        if (/\bgit (commit|checkout|switch|stash|reset|add|rm|apply|merge|rebase|push|tag|worktree add)\b/.test(cmd)) fail(`the ${c.label} prompt runs ${cmd}`)
      }
    }
    if (spans < 100) fail(`the command search read ${spans} spans; it cannot have judged the prompts`)
    for (const dir of made) {
      if (!fs.existsSync(path.join(dir, '.git'))) continue
      const tracked = execFileSync('git', ['-C', dir, 'status', '--porcelain', '--untracked-files=no'], { encoding: 'utf8' })
      const untracked = execFileSync('git', ['-C', dir, 'status', '--porcelain', '--untracked-files=all'], { encoding: 'utf8' })
      const ignored = execFileSync('git', ['-C', dir, 'status', '--porcelain', '--ignored', '--untracked-files=all'], { encoding: 'utf8' }).split('\n').filter((l) => l.startsWith('!! ')).map((l) => l.slice(3))
      if (tracked || untracked) fail(`a run changed ${dir}: ${tracked}${untracked}`)
      for (const f of ignored) if (!f.startsWith(LOG_REL + '/')) fail(`a run wrote ${f} outside the log`)
    }
    const wrote = made.filter((d) => fs.existsSync(path.join(d, LOG_REL))).length
    if (wrote < 10) fail(`only ${wrote} runs wrote under the log, so the status search read almost nothing`)
  }

  // 14. Box 15: .gitignore tracks this companion by its own name and nothing broader.
  checks++
  {
    const gi = fs.readFileSync(path.join(top, '.gitignore'), 'utf8').split('\n')
    if (!gi.includes('!.claude/workflows/truth-audit_tests.mjs')) fail('.gitignore has no line !.claude/workflows/truth-audit_tests.mjs')
    const env = newRepo()
    fs.mkdirSync(path.join(env.repo, '.claude/workflows'), { recursive: true })
    for (const f of ['truth-audit_tests.mjs', 'stray_tests.mjs', 'truth-audit.js', 'truth-audit.fixture.json']) fs.writeFileSync(path.join(env.repo, '.claude/workflows', f), '\n')
    const ignored = (f) => spawnSync('git', ['-C', env.repo, 'check-ignore', '-q', `.claude/workflows/${f}`]).status === 0
    if (ignored('truth-audit_tests.mjs') || ignored('truth-audit.js')) fail('.gitignore ignores truth-audit_tests.mjs or truth-audit.js')
    if (!ignored('stray_tests.mjs') || !ignored('truth-audit.fixture.json')) fail('.gitignore opens more than truth-audit_tests.mjs')
  }

  // 15. The report carries what the spec lists, the precision sample is ten true claims across
  //     types, the checkout line is filled, and the return names the facts.
  checks++
  {
    const served = { claims: {} }
    CORPUS.slice(0, 12).forEach((f, i) => { served.claims[f] = [{ line: 5, type: TYPES[i % TYPES.length], noun: `sample-${i}` }] })
    const { result } = await execute(newRepo(), {}, served)
    const t = result.report.text
    for (const h of ['### Counts by type and verdict', '### Filed', '### Skipped', '### Stale claims', '### Unverifiable claims', '### Precision sample', '### Candidate rules', '### Checkout']) if (!t.includes(h)) fail(`the report has no ${h}`)
    if (!t.includes(SHA)) fail('the report does not name the sha')
    if (!/sample: N of 10 right/.test(t)) fail('the report does not ask for the sample judgement')
    const sample = (/### Precision sample\n\n([\s\S]*?)\n\n/.exec(t) || [])[1] || ''
    const items = sample.split('\n').filter((l) => /^\d+\. /.test(l))
    if (items.length !== 10) fail(`the precision sample lists ${items.length} claims`)
    const typesIn = new Set(TYPES.filter((ty) => items.some((i) => i.includes(`(${ty})`))))
    if (typesIn.size < TYPES.length) fail(`the precision sample covers ${typesIn.size} of ${TYPES.length} types`)
    if (/CHECKOUT: pending/.test(t) || !/Tracked files changed: none/.test(t)) fail('the checkout line was not filled')
    if (result.checkoutClean !== true) fail(`checkoutClean ${result.checkoutClean}`)
    const dirty = await execute(newRepo(), {}, { checkout: { trackedChanged: ['README.md'], newUntracked: [] } })
    if (dirty.result.checkoutClean !== false || !/Tracked files changed: README\.md/.test(dirty.result.report.text)) fail('a changed tracked file is not reported')
    for (const k of ['sha', 'claims', 'verdicts', 'findings', 'filed', 'skipped', 'report', 'checkoutClean']) if (!(k in result)) fail(`the return has no ${k}`)
    if (result.verdicts.true !== CORPUS.length) fail(`the verdict counts ${JSON.stringify(result.verdicts)}`)
  }

  // 17. An agent that returns nothing is a failure the run reports, never an empty result
  //     (CODING_STANDARDS.md -> "A check fails closed"): a Shard, a Verify batch and the Cluster each
  //     named in the return and the report, which says it is incomplete; the same runs with every
  //     agent answering name no failure, so each assertion can fail.
  checks++
  {
    const one = { files: ['contracts/telemetry.md'] }
    const incomplete = (t) => /^\*\*Incomplete:\*\*/m.test(t)
    const named = (r, re) => (r.failed || []).some((f) => re.test(f)) && new RegExp(`### Agents that returned nothing[\\s\\S]*${re.source}`).test(r.report.text)
    const ok = await execute(newRepo(), one)
    if (!Array.isArray(ok.result.failed) || ok.result.failed.length || incomplete(ok.result.report.text) || ok.result.claims !== 1) fail(`a run with every agent answering: failed ${JSON.stringify(ok.result.failed)}, claims ${ok.result.claims}`)
    const sh = await execute(newRepo(), one, { nothingFrom: ['shard-1'] })
    if (!named(sh.result, /shard-1 \(contracts\/telemetry\.md\) returned nothing/) || !incomplete(sh.result.report.text)) fail(`a Shard that returned nothing: failed ${JSON.stringify(sh.result.failed)}; the report ${incomplete(sh.result.report.text) ? 'says' : 'does not say'} it is incomplete`)
    const ve = await execute(newRepo(), one, { nothingFrom: ['verify-1'] })
    if (!named(ve.result, /verify-1 \(contracts\/telemetry\.md\) returned nothing: 1 claim/) || !incomplete(ve.result.report.text)) fail(`a Verify batch that returned nothing: failed ${JSON.stringify(ve.result.failed)}`)
    const good = newRepo()
    const g = await execute(good, withPlan(), TWO)
    if ((g.result.failed || []).length || g.result.findings !== 2) fail(`TWO with every agent answering: failed ${JSON.stringify(g.result.failed)}, findings ${g.result.findings}`)
    const cl = newRepo()
    const c = await execute(cl, withPlan(), { ...TWO, nothingFrom: ['cluster'] })
    if (c.result.verdicts.false !== 4 || c.result.findings !== 0) fail(`a Cluster that returned nothing: verdicts ${JSON.stringify(c.result.verdicts)}, findings ${c.result.findings}`)
    if (!named(c.result, /the Cluster agent returned nothing: 4 false or stale verdicts/) || !incomplete(c.result.report.text)) fail(`a Cluster that returned nothing: failed ${JSON.stringify(c.result.failed)}`)
    if (cl.gh.some((x) => x.action === 'create' && !/^Truth audit/.test(x.title))) fail('a Cluster that returned nothing still filed a finding')
  }

  // 16. Every agent call passes a schema and every stub's return satisfies it.
  checks++
  for (const c of allCalls) if (!c.schema || c.schema.type !== 'object') fail(`${c.label}: called without a schema`)
  if (driftAll.length) fail(`returns not matching the script's schemas: ${driftAll.slice(0, 5).join(' | ')}`)
} catch (e) {
  console.error(`truth-audit tests: the script did not run: ${e && e.stack ? e.stack : e}`)
  process.exit(2)
}

if (failures.length) {
  console.log(`truth-audit tests: FAILED, ${failures.length} failure(s) in ${checks} checks, on ${path.basename(scriptPath)}:`)
  failures.forEach((f) => console.log(`  - ${f}`))
  process.exit(1)
}
console.log(`truth-audit tests: pass: ${checks} checks, on ${path.basename(scriptPath)}`)
