// The pins-sweep script's test companion. Owner: integrator. Tracked beside the script (#269),
// named for it as truth-audit_tests.mjs is for its own.
//
//   node .claude/workflows/pins-sweep_tests.mjs [script]
//
// Runs pins-sweep.js end to end with agent, parallel, log and phase stubbed, and with gh stubbed
// where the script reaches it: through its agents, each of which the stub plays from a served
// fixture as its prompt asks. The Inventory agent serves the families, pins, SDK row and issue
// listings tools/pins_inventory.py and gh would give; each Check agent serves a recommendation for
// the family its prompt carries; each Trial agent writes the log its prompt names, HEAD= first and
// the verdict last, green unless the fixture says red; the File agent writes each would-be issue and
// comment its prompt carries, and "runs" each gh command its prompt lists by recording it. No agent,
// gh, git, dotnet or network call is made. repo is a throwaway git repository with this
// repository's .gitignore, so a run that writes outside its log shows in that repository's status.
// Every stub's return is validated against the schema the script passed. Exits 0 when every check
// holds, 1 when one fails (each failure printed), 2 when the script cannot run. Its last line is its
// verdict.
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { execFileSync, spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const scriptPath = process.argv[2] || path.join(here, 'pins-sweep.js')
const top = path.resolve(here, '..', '..')
const BASE = 'b'.repeat(40)
const DATE = '2026-10-22'
const LOG_REL = '.scratch/pins-sweep/log'
const REPORT_TITLE = 'Pins sweep: the state of every pin'
// A fixture's issue number, built so the steering check's citation search (#NNN must name an issue
// here) does not read the made-up issues of these fixtures as citations.
const cite = (n) => '#' + n
const NET = ['aspnetcore', 'duende', 'yarp', 'aws-sdk', 'opentelemetry', 'xunit', 'playwright', 'schema-validator', 'fake-clock', 'mvc-testing', 'analyzers', 'sdk']

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

// ---------------------------------------------------------------- the inventory the stubs serve
// The tree as #82's proof 1 draws it: the framework family behind a security patch and tied to
// ADR-0017's evidence line, the token library behind a minor and tied, the AWS SDK behind a patch
// with no tie, the schema validator held by D-1, xunit and Playwright current.
const pin = (id, pinned, newest, extra = {}) => ({
  id, pinned, newest, newestInBound: '', hold: '', comment: '', status: newest === pinned ? 'current' : (pinned.split('.').slice(0, 2).join('.') === newest.split('.').slice(0, 2).join('.') ? 'behind-patch' : 'behind-minor'),
  referencedBy: [`src/${id}.Consumer/${id}.Consumer.csproj`], evidence: [], documents: [], advisories: [], ...extra,
})
const ADR17 = { path: 'docs/adr/0017-g0-findings-fold-into-the-identity-contracts.md', line: 169 }
const FIXTURE = {
  aspnetcore: { trialScope: ['solution'], smoke: true, pins: [
    pin('Microsoft.AspNetCore.Authentication.OpenIdConnect', '10.0.12', '10.0.13', { status: 'blocked-by-evidence', evidence: [ADR17], documents: [ADR17] }),
    pin('Microsoft.AspNetCore.Authentication.JwtBearer', '10.0.12', '10.0.13'),
    pin('Microsoft.IdentityModel.JsonWebTokens', '8.19.2', '8.23.0')] },
  duende: { trialScope: ['tests/Platform.Identity.Tests/Platform.Identity.Tests.csproj'], smoke: false, pins: [
    pin('Duende.AccessTokenManagement.OpenIdConnect', '4.2.0', '4.3.0', { status: 'blocked-by-evidence', evidence: [{ path: ADR17.path, line: 170 }], documents: [{ path: ADR17.path, line: 170 }] })] },
  yarp: { trialScope: ['tests/Platform.Proxy.Tests/Platform.Proxy.Tests.csproj'], smoke: false, pins: [pin('Yarp.ReverseProxy', '2.3.0', '2.3.0')] },
  'aws-sdk': { trialScope: ['tests/Platform.Registry.Tests/Platform.Registry.Tests.csproj', 'tests/Spactl.Tests/Spactl.Tests.csproj'], smoke: false, pins: [
    pin('AWSSDK.S3', '4.0.101.6', '4.0.104.4', { comment: '' }), pin('AWSSDK.SimpleSystemsManagement', '4.0.104.2', '4.0.104.4')] },
  opentelemetry: { trialScope: ['tests/Platform.Telemetry.Tests/Platform.Telemetry.Tests.csproj'], smoke: false, pins: [pin('OpenTelemetry.Extensions.Hosting', '1.19.1', '1.20.0')] },
  xunit: { trialScope: ['solution'], smoke: true, pins: [pin('xunit.v3', '4.0.2', '4.0.2')] },
  playwright: { trialScope: ['fixtures/Fixtures.Tests/Fixtures.Tests.csproj'], smoke: false, pins: [pin('Microsoft.Playwright', '1.63.0', '1.63.0')] },
  'schema-validator': { trialScope: ['tests/Contracts.Tests/Contracts.Tests.csproj'], smoke: false, pins: [
    pin('JsonSchema.Net', '8.0.5', '9.4.1', { status: 'held-by-decision', hold: 'D-1', newestInBound: '8.0.9', comment: 'Decision D-1 in docs/dependencies.md.' })] },
  'fake-clock': { trialScope: ['tests/Platform.Testing.Tests/Platform.Testing.Tests.csproj'], smoke: false, pins: [pin('Microsoft.Extensions.TimeProvider.Testing', '10.10.0', '10.11.0')] },
  'mvc-testing': { trialScope: ['tests/Gateway.IntegrationTests/Gateway.IntegrationTests.csproj'], smoke: false, pins: [pin('Microsoft.AspNetCore.Mvc.Testing', '10.0.12', '10.0.13')] },
  sdk: { trialScope: [], smoke: false, pins: [] },
  terraform: { trialScope: ['infra/envs/poc', 'infra/modules/cell'], smoke: false, pins: [pin('hashicorp/aws', '6.68.0', '6.69.1')] },
}
const familyRecord = (id, served) => {
  const f = JSON.parse(JSON.stringify(FIXTURE[id]))
  const extra = (served.pins || {})[id]
  if (extra) f.pins = f.pins.map((p) => ({ ...p, ...(extra[p.id] || {}) }))
  const order = ['advisory', 'blocked-by-evidence', 'held-by-decision', 'behind-minor', 'behind-patch', 'current', 'not-checked']
  for (const p of f.pins) if (p.advisories.length) p.status = 'advisory'
  const status = f.pins.length ? f.pins.map((p) => p.status).sort((a, b) => order.indexOf(a) - order.indexOf(b))[0] : (served.sdkRow && served.sdkRow.status) || 'current'
  return { id, status, ...f }
}

const made = []
process.on('exit', () => { for (const dir of made) fs.rmSync(dir, { recursive: true, force: true }) })
const newEnv = () => {
  const repo = fs.mkdtempSync(path.join(os.tmpdir(), 'pins-sweep-tests-'))
  const wt = fs.mkdtempSync(path.join(os.tmpdir(), 'pins-sweep-tests-wt-'))
  made.push(repo, wt)
  const git = (...a) => execFileSync('git', ['-C', repo, ...a], { encoding: 'utf8' })
  git('init', '-q')
  fs.copyFileSync(path.join(top, '.gitignore'), path.join(repo, '.gitignore'))
  fs.writeFileSync(path.join(repo, 'Directory.Packages.props'), '<Project />\n')
  git('add', '-A')
  git('-c', 'user.name=t', '-c', 'user.email=t@t', 'commit', '-q', '-m', 'start')
  return { repo, wt, thrown: [], schemaFailures: [], peak: 0, gh: [], trialLogs: {} }
}
const argsFor = (env, a = {}) => ({ repo: env.repo, wt: env.wt, date: DATE, ...a })

const dataLine = (prompt, marker) => {
  const m = new RegExp(`^${marker}: (.*)$`, 'm').exec(prompt)
  return m ? JSON.parse(m[1]) : null
}
const block = (prompt, name) => {
  const m = new RegExp(`^BEGIN ${name}\\n([\\s\\S]*?)\\nEND ${name}$`, 'm').exec(prompt)
  return m ? m[1] : null
}
const write = (file, text) => { fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, text) }
// The commands a prompt asks an agent to run: its backtick spans, outside the data it carries (a
// BEGIN/END block, or a NAME: [json] line), which quote the issues and are never run.
const commands = (prompt) => {
  const text = prompt.replace(/^BEGIN (\S+(?: \S+)?)\n[\s\S]*?\nEND \1$/gm, '').replace(/^[A-Z][A-Z ]*(?: \S+)?: [[{"].*$/gm, '')
  return [...text.matchAll(/`([^`\n]+)`/g)].map((m) => m[1])
}
// -X and --method are flags only after a space: a temporary directory's random name can hold "-X".
const GH_WRITE = /\bgh\s+(issue\s+(create|edit|comment|close|reopen|delete|lock|transfer|pin)|label|api\b[^`]*\s(-X|--method)\b|pr\s+(create|edit|comment|merge|close))/

// served: { halt, listFails, pins: {family: {pinId: fields}}, sdkRow, openIssues, closedIssues,
//           report: {number, statuses}, checks: {family: fields}, trials: {family: 'red'|'not-run'}, ghDown }
function stub(env, served) {
  const calls = []
  let inFlight = 0
  globalThis.phase = () => {}
  globalThis.log = () => {}
  globalThis.parallel = (fns) => Promise.all(fns.map((f) => f().catch((e) => { env.thrown.push(e); return null })))
  const LOG = `${env.repo}/${LOG_REL}/${DATE}`
  globalThis.agent = async (prompt, opts) => {
    const label = opts.label
    inFlight++
    env.peak = Math.max(env.peak, inFlight)
    try {
      await new Promise((r) => setTimeout(r, 3))
      calls.push({ label, prompt, schema: opts.schema })
      let out
      if (label === 'inventory') {
        const scope = dataLine(prompt, 'FAMILIES IN SCOPE') || []
        if (served.halt) {
          out = { outcome: 'halt', reason: 'python3 -I tools/pins_inventory.py inventory exited 3: pins-inventory: halt: nuget.org unreachable', base: '', sdk: '', inventoryFile: '', listed: false, families: [], sdkRow: { floor: '', latestSdk: '', latestRuntime: '', frameworkRuntime: '', securityRelease: '', status: 'not-checked' }, openIssues: [], closedIssues: [], report: { number: 0, statuses: [] } }
        } else {
          write(`${LOG}/inventory.json`, '{}\n')
          out = {
            outcome: 'ok', reason: served.listFails ? 'gh: error connecting to api.github.com' : '', base: BASE, sdk: '10.0.401', inventoryFile: `${LOG}/inventory.json`, listed: !served.listFails,
            families: scope.map((id) => familyRecord(id, served)),
            sdkRow: { floor: '10.0.100', latestSdk: '10.0.402', latestRuntime: '10.0.13', frameworkRuntime: '10.0.12', securityRelease: '', status: 'behind-patch', ...(served.sdkRow || {}) },
            openIssues: served.listFails ? [] : served.openIssues || [], closedIssues: served.listFails ? [] : served.closedIssues || [],
            report: served.report || { number: 0, statuses: [] },
          }
        }
      } else if (label.startsWith('check-')) {
        const fam = dataLine(prompt, 'FAMILY')
        const behind = fam.pins.filter((p) => p.newest !== p.pinned).map((p) => ({ id: p.id, from: p.pinned, to: p.hold ? p.newestInBound || p.pinned : p.newest }))
        const moves = behind.filter((m) => m.to !== m.from)
        const base = {
          family: fam.id, recommendation: fam.id === 'sdk' ? 'no-move' : moves.length ? 'move' : 'no-move', reason: moves.length ? 'newer stable release' : 'current',
          candidate: fam.id === 'sdk' ? [] : moves, advisories: [], licenceBefore: 'MIT', licenceAfter: 'MIT', releaseNotes: `https://example.invalid/${fam.id}/releases`,
          breakingChanges: [], evidenceTouched: fam.pins.flatMap((p) => p.evidence.map((e) => ({ ...e, why: 'records the G0 run at this version' }))), evidenceRefuted: [], newestConfirmed: true, behaviour: '',
        }
        out = { ...base, ...((served.checks || {})[fam.id] || {}) }
      } else if (label.startsWith('trial-')) {
        const t = dataLine(prompt, 'TRIAL')
        env.trials = env.trials || []
        env.trials.push(t)
        const verdict = (served.trials || {})[t.family]
        const logf = `${LOG}/trial-${t.family}.log`
        const lines = [`HEAD=${BASE}`, 'SDK=10.0.401', `MOVE=${t.moves.map((m) => `${m.id} ${m.from} -> ${m.to}`).join('; ')}`]
        if (verdict === 'red') lines.push('error NU1102: Unable to find package AWSSDK.S3 with version (= 9.9.9)', 'TRIAL: RED')
        else lines.push('Test run summary: Passed!', 'TRIAL: GREEN')
        write(logf, lines.join('\n') + '\n')
        env.trialLogs[t.family] = logf
        out = {
          family: t.family, base: t.base, sdk: '10.0.401', scope: t.scope, outcome: verdict === 'red' ? 'red' : 'green', logPath: logf,
          // 'liar': the agent reports green and the log's last line says otherwise.
          verdictLine: verdict === 'liar' ? 'TRIAL: RED' : lines[lines.length - 1], failingLines: verdict === 'red' ? [lines[3]] : [], browserRevision: '', worktreeRemoved: true,
        }
      } else if (label === 'file') {
        const filings = dataLine(prompt, 'FILINGS') || []
        const writesList = dataLine(prompt, 'WRITES') || []
        const report = dataLine(prompt, 'REPORT ISSUE')
        env.filings = filings
        for (const f of filings) write(f.bodyFile, block(prompt, `ISSUE ${f.family}`) || '')
        for (const w of writesList) if (w.kind !== 'create') write(`${LOG}/comment-${w.key}.md`, block(prompt, `COMMENT ${w.key}`) || '')
        const table = '| Pin | Pinned | Newest | Status | Referencing projects | Why |\n| --- | --- | --- | --- | --- | --- |\n'
        write(report.bodyFile, (block(prompt, 'REPORT') || '').replace('TABLE: pending', table))
        write(report.commentFile, block(prompt, 'REPORT COMMENT') || '')
        // The gh commands the prompt lists, each recorded as run, in order.
        let n = 900
        const done = []
        for (const c of commands(prompt).filter((x) => /\bgh\s/.test(x))) {
          if (served.ghDown) break
          env.gh.push(c)
        }
        for (const w of writesList) {
          const ok = !served.ghDown && commands(prompt).some((c) => GH_WRITE.test(c) && (w.kind === 'create' ? c.includes('gh issue create') && c.includes(`issue-${w.key}.md`) : c.includes(` ${w.issue} `)))
          done.push({ key: w.key, done: ok, number: ok && w.kind === 'create' ? n++ : ok ? w.issue : 0, url: ok ? `https://github.com/adammedford/spa-platform/issues/${n}` : '' })
        }
        out = {
          writes: done, table, report: { number: report.mode === 'edit' ? report.issue : report.mode === 'create' && !served.ghDown ? 950 : 0, url: '' },
          checkout: { trackedChanged: [], newUntracked: [], headMoved: false, refsChanged: [], worktreesLeft: [] }, reason: served.ghDown ? 'gh: error connecting to api.github.com' : '',
        }
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
  const calls = stub(env, served)
  globalThis.args = scriptArgs
  const src = fs.readFileSync(scriptPath, 'utf8').replace(/^export const meta/m, 'const meta')
  const result = await new Function('return (async () => {' + src + '\n})()')()
  for (const c of calls) allCalls.push({ ...c, dry: scriptArgs.dryRun === true, env })
  for (const f of env.schemaFailures.splice(0)) driftAll.push(f)
  for (const t of env.thrown.splice(0)) driftAll.push(`a thunk threw: ${t}`)
  return { result, calls, labels: calls.map((c) => c.label), prompt: (l) => (calls.find((c) => c.label === l) || {}).prompt || '' }
}
const loadMeta = () => {
  const src = fs.readFileSync(scriptPath, 'utf8')
  const m = /^export const meta = ([\s\S]*?\n})\n/m.exec(src)
  return m ? new Function(`return (${m[1]})`)() : null
}
const recordOf = (result, family) => (result.files || []).find((f) => f.family === family)
const bodyOf = (env, family) => {
  const f = `${env.repo}/${LOG_REL}/${DATE}/issue-${family}.md`
  return fs.existsSync(f) ? fs.readFileSync(f, 'utf8') : ''
}

const failures = []
const fail = (msg) => failures.push(msg)
let checks = 0
const FRAMEWORK = ['aspnetcore', 'mvc-testing', 'fake-clock', 'sdk']
const REST = ['aws-sdk', 'opentelemetry', 'xunit', 'schema-validator']

try {
  // 1. meta names pins-sweep and its phases; the script accepts exactly #82's arguments, and anything
  //    else stops it before any agent.
  checks++
  {
    const meta = loadMeta()
    if (!meta || meta.name !== 'pins-sweep') fail(`meta.name is ${meta && meta.name}, expected pins-sweep`)
    const titles = ((meta && meta.phases) || []).map((p) => p.title)
    if (titles.join() !== 'Inventory,Check,Trial,File,Report') fail(`meta phases ${titles.join(', ')}`)
    const src = fs.readFileSync(scriptPath, 'utf8')
    const known = (/^const KNOWN_ARGS = (\[.*\])$/m.exec(src) || [])[1]
    if (known !== "['repo', 'wt', 'date', 'families', 'maxParallel', 'maxIssues', 'maxAgents', 'dryRun', 'terraform', 'moves']") fail(`KNOWN_ARGS is ${known}`)
    for (const [label, a] of [
      ['an unknown argument', (e) => argsFor(e, { scope: 'all' })],
      ['no repo', (e) => ({ wt: e.wt, date: DATE })],
      ['no wt', (e) => ({ repo: e.repo, date: DATE })],
      ['no date', (e) => ({ repo: e.repo, wt: e.wt })],
      ['a wt inside repo', (e) => ({ repo: e.repo, wt: `${e.repo}/wt`, date: DATE })],
      ['an unknown family', (e) => argsFor(e, { families: ['nodejs'] })],
      ['terraform named as a family', (e) => argsFor(e, { families: ['terraform'] })],
      ['maxIssues negative', (e) => argsFor(e, { maxIssues: -1 })],
      ['maxParallel zero', (e) => argsFor(e, { maxParallel: 0 })],
      ['dryRun as a string', (e) => argsFor(e, { dryRun: 'true' })],
      ['terraform as a string', (e) => argsFor(e, { terraform: 'yes' })],
      ['moves not an object', (e) => argsFor(e, { families: ['yarp'], moves: ['yarp'] })],
      ['moves naming a family out of scope', (e) => argsFor(e, { families: ['yarp'], moves: { 'aws-sdk': '4.0.104.4' } })],
      ['moves naming the report-only SDK', (e) => argsFor(e, { families: ['sdk'], moves: { sdk: '10.0.402' } })],
    ]) {
      const env = newEnv()
      const { result, calls } = await execute(env, a(env))
      if (!result || !/bad launch/.test(result.halted || '') || calls.length) fail(`${label}: ${calls.length} agent(s) ran; returned ${JSON.stringify(result)}`)
    }
    const env = newEnv()
    const { result } = await execute(env, argsFor(env, { families: ['yarp'], maxParallel: 2, maxIssues: 1, maxAgents: 4, dryRun: true, terraform: false, moves: { yarp: '2.4.0' } }))
    if (result.halted) fail(`every argument at once was refused: ${result.halted}`)
  }

  // 2. Agent budget: every .NET family in scope halts before any dispatch with 18 against 10 (17 before #607's
  //    analyzers family); a named
  //    group within budget proceeds.
  checks++
  {
    const env = newEnv()
    const { result, calls } = await execute(env, argsFor(env))
    if (calls.length || result.agentCount !== 18 || result.maxAgents !== 10 || !/18 agents[\s\S]*maxAgents 10/.test(result.halted || '')) fail(`the full run: ${calls.length} agent(s), returned ${JSON.stringify(result)}`)
    const tf = newEnv()
    const t = await execute(tf, argsFor(tf, { families: FRAMEWORK, terraform: true }))
    if (t.calls.length || t.result.agentCount !== 11) fail(`the framework group with terraform counts ${t.result.agentCount}, expected 11, and dispatched ${t.calls.length}`)
    const ok = newEnv()
    const g = await execute(ok, argsFor(ok, { families: FRAMEWORK }))
    if (g.result.halted || g.labels[0] !== 'inventory' || g.result.agentCount !== 10) fail(`the framework group (10 agents) did not proceed: ${JSON.stringify(g.result.halted)} ${g.labels.join(', ')}`)
    if (g.calls.length > 10) fail(`the framework group ran ${g.calls.length} agents`)
  }

  // 3. Inventory halt: nuget.org unreachable ends the run with no Check or Trial agent; so does a
  //    failed issue listing, dry or not.
  checks++
  {
    const env = newEnv()
    const { result, labels } = await execute(env, argsFor(env, { families: REST }), { halt: true })
    if (labels.join() !== 'inventory') fail(`a halted Inventory dispatched ${labels.join(', ')}`)
    if (!/Inventory halted[\s\S]*nuget\.org unreachable[\s\S]*no Check or Trial agent/.test(result.halted || '')) fail(`a halted Inventory returned ${JSON.stringify(result)}`)
    for (const dry of [false, true]) {
      const down = newEnv()
      const d = await execute(down, argsFor(down, { families: REST, dryRun: dry }), { listFails: true })
      if (d.labels.join() !== 'inventory' || !/listings could not be read/.test(d.result.halted || '')) fail(`a failed listing (dryRun ${dry}) dispatched ${d.labels.join(', ')}: ${JSON.stringify(d.result.halted)}`)
    }
    const e3 = newEnv()
    const p = (await execute(e3, argsFor(e3, { families: REST }))).prompt('inventory')
    if (!/nuget\.org[\s\S]*halt/.test(p) || !/worktree add --detach/.test(p) || !/rev-parse origin\/main/.test(p)) fail('the Inventory prompt does not detach a base at origin/main or say nuget.org halts')
  }

  // 4. Trial order: at most maxIssues trials, advisory-bearing families first; a candidate cut by the
  //    cap is reported as behind and not trialled.
  checks++
  {
    const env = newEnv()
    const served = { pins: { 'aws-sdk': { 'AWSSDK.S3': { advisories: [{ id: 'GHSA-xxxx-yyyy-zzzz', severity: 'High', url: 'https://github.com/advisories/GHSA-xxxx-yyyy-zzzz', fixed: '4.0.104.4', via: '' }] } } } }
    const { result, labels } = await execute(env, argsFor(env, { families: ['opentelemetry', 'fake-clock', 'aws-sdk', 'mvc-testing'], maxIssues: 2 }), served)
    const trials = labels.filter((l) => l.startsWith('trial-'))
    if (trials.join() !== 'trial-aws-sdk,trial-fake-clock' && trials.join() !== 'trial-aws-sdk,trial-opentelemetry') fail(`trials dispatched ${trials.join(', ')}; expected the advisory-bearing aws-sdk first, then a minor step`)
    if (trials.length !== 2) fail(`${trials.length} trials ran under maxIssues 2`)
    const cut = result.leftovers.filter((l) => /behind, not trialled/.test(l.why)).map((l) => l.family).sort()
    if (cut.length !== 2 || !cut.includes('mvc-testing')) fail(`the cut candidates are ${JSON.stringify(result.leftovers)}`)
    for (const f of cut) if (recordOf(result, f)) fail(`${f} was cut by the cap and still filed`)
    // Without the advisory, the minor steps go first and the AWS patch is cut.
    const plain = newEnv()
    const p = await execute(plain, argsFor(plain, { families: ['opentelemetry', 'fake-clock', 'aws-sdk', 'mvc-testing'], maxIssues: 2 }))
    if (p.labels.filter((l) => l.startsWith('trial-')).join() !== 'trial-fake-clock,trial-opentelemetry') fail(`without an advisory the trials are ${p.labels.filter((l) => l.startsWith('trial-')).join(', ')}`)
  }

  // 5. moves replaces the Check phase's candidate for the named family.
  checks++
  {
    const env = newEnv()
    await execute(env, argsFor(env, { families: ['aws-sdk', 'yarp'], moves: { 'aws-sdk': '4.0.200.1', yarp: { 'Yarp.ReverseProxy': '2.4.0' } } }))
    const aws = (env.trials || []).find((t) => t.family === 'aws-sdk')
    if (!aws || aws.moves.map((m) => `${m.id}=${m.to}`).sort().join() !== 'AWSSDK.S3=4.0.200.1,AWSSDK.SimpleSystemsManagement=4.0.200.1') fail(`the aws-sdk trial moved ${JSON.stringify(aws && aws.moves)}`)
    const yarp = (env.trials || []).find((t) => t.family === 'yarp')
    if (!yarp || yarp.moves.length !== 1 || yarp.moves[0].to !== '2.4.0') fail(`the yarp override, over a no-move Check, trialled ${JSON.stringify(yarp && yarp.moves)}`)
    const without = newEnv()
    await execute(without, argsFor(without, { families: ['aws-sdk', 'yarp'] }))
    const plain = (without.trials || []).find((t) => t.family === 'aws-sdk')
    if (!plain || plain.moves.some((m) => m.to !== '4.0.104.4') || (without.trials || []).some((t) => t.family === 'yarp')) fail(`without moves the trials are ${JSON.stringify(without.trials)}`)
  }

  // 6. Labels: each would-be issue carries exactly one of the two; a red trial, an evidence tie, a
  //    licence change or a decision hold never carries ready-for-agent.
  checks++
  {
    const env = newEnv()
    const served = {
      checks: { opentelemetry: { licenceAfter: 'Apache-2.0 AND LicenseRef-fee' }, 'schema-validator': { recommendation: 'move', candidate: [{ id: 'JsonSchema.Net', from: '8.0.5', to: '8.0.9' }] } },
      trials: { 'fake-clock': 'red' },
    }
    const { result } = await execute(env, argsFor(env, { families: ['aws-sdk', 'aspnetcore', 'opentelemetry', 'fake-clock', 'schema-validator'], maxIssues: 5, maxAgents: 12 }), served)
    const want = { 'aws-sdk': 'ready-for-agent', aspnetcore: 'ready-for-human', opentelemetry: 'ready-for-human', 'fake-clock': 'ready-for-human', 'schema-validator': 'ready-for-human' }
    for (const [f, label] of Object.entries(want)) {
      const r = recordOf(result, f)
      if (!r || r.label !== label) fail(`${f} is labelled ${r && r.label}, expected ${label}`)
      const body = bodyOf(env, f)
      if (label === 'ready-for-human' && body.includes('ready-for-agent')) fail(`${f}'s ready-for-human body names ready-for-agent`)
      if (!/^\*\*Pins family:\*\* /m.test(body) || /\*\*Plan row:\*\*/.test(body)) fail(`${f}'s body has no family marker line, or has a plan-row line`)
    }
    for (const c of env.gh.filter((x) => x.includes('gh issue create') && x.includes('/issue-'))) {
      const labels = [...c.matchAll(/--label (\S+)/g)].map((m) => m[1])
      if (labels.length !== 1 || !['ready-for-agent', 'ready-for-human'].includes(labels[0])) fail(`a create carries labels ${labels.join(', ')}: ${c}`)
    }
    // A ready-for-agent issue is a ticket the overnight's lanes will pick up, so it must have the
    // shape tools/ticket_shape.py holds every ticket to.
    for (const [f, label] of Object.entries(want)) {
      if (f === 'terraform') continue
      const shape = spawnSync('python3', ['-I', path.join(top, 'tools', 'ticket_shape.py'), '-'], { encoding: 'utf8', input: JSON.stringify({ number: 1, body: bodyOf(env, f), labels: [{ name: label }], subIssuesSummary: { total: 0 }, blockedBy: { nodes: [] } }) })
      if (shape.status !== 0) fail(`${f}'s would-be issue fails tools/ticket_shape.py: ${(shape.stdout + shape.stderr).trim().split('\n').join(' | ')}`)
    }
    const red = bodyOf(env, 'fake-clock')
    if (!red.includes('TRIAL: RED') || !red.includes('NU1102') || !/\*\*Label:\*\* ready-for-human, because the trial is RED/.test(red)) fail('the red trial\'s body lacks its verdict, its failing line or its reason first')
    if (!/evidence names the pinned version: docs\/adr\/0017/.test(bodyOf(env, 'aspnetcore'))) fail('the evidence-tied body does not name the ADR line')
    if (!/licence changes/.test(bodyOf(env, 'opentelemetry'))) fail('the licence change is not the stated reason')
    if (!/decision holds the pin: D-1/.test(bodyOf(env, 'schema-validator'))) fail('the decision hold is not the stated reason')
    const awsTitle = (recordOf(result, 'aws-sdk') || {}).title
    if (!/Pins: AWS SDK 4\.0\.101\.6, 4\.0\.104\.2 → 4\.0\.104\.4/.test(awsTitle || '')) fail(`the aws-sdk title is ${awsTitle}`)
    // A Check that refutes the companion's tie lifts it: the same family green and clean is ready-for-agent.
    const refuted = newEnv()
    const r2 = await execute(refuted, argsFor(refuted, { families: ['duende'] }), { checks: { duende: { evidenceTouched: [], evidenceRefuted: [{ path: 'docs/adr/0017-g0-findings-fold-into-the-identity-contracts.md', line: 170, why: 'names the run, not a constraint' }] } } })
    if ((recordOf(r2.result, 'duende') || {}).label !== 'ready-for-agent') fail(`a refuted tie still labels duende ${(recordOf(r2.result, 'duende') || {}).label}`)
    const unaddressed = newEnv()
    const r3 = await execute(unaddressed, argsFor(unaddressed, { families: ['duende'] }), { checks: { duende: { evidenceTouched: [] } } })
    if ((recordOf(r3.result, 'duende') || {}).label !== 'ready-for-human') fail('a tie the Check neither confirmed nor refuted was dropped')
  }

  // 7. Dedupe outcomes against a stubbed listing.
  checks++
  {
    const move = 'AWSSDK.S3 4.0.101.6 -> 4.0.104.4; AWSSDK.SimpleSystemsManagement 4.0.104.2 -> 4.0.104.4'
    const issue = (n, m, labels = ['ready-for-agent']) => ({ number: n, title: 'Pins: AWS SDK', marker: 'aws-sdk', move: m, labels })
    const cases = [
      ['same move', { openIssues: [issue(501, move)] }, {}, 'stands', 501],
      ['a different move', { openIssues: [issue(501, 'AWSSDK.S3 4.0.101.6 -> 4.0.103.0')] }, {}, 'updated', 501],
      ['closed wontfix on the same move', { closedIssues: [issue(502, move, ['wontfix'])] }, {}, 'declined', 502],
      ['closed otherwise', { closedIssues: [issue(502, move, ['ready-for-agent'])] }, {}, 'created', 900],
      ['the marker line removed', { openIssues: [{ ...issue(501, move), marker: '', move: '' }] }, { dryRun: true }, 'would-create', 0],
      ['a different move, dry', { openIssues: [issue(501, 'old')] }, { dryRun: true }, 'would-update', 501],
    ]
    for (const [label, served, extra, action, number] of cases) {
      const env = newEnv()
      const { result } = await execute(env, argsFor(env, { families: ['aws-sdk'], ...extra }), served)
      const r = recordOf(result, 'aws-sdk')
      if (!r || r.action !== action || r.number !== number) fail(`${label}: ${JSON.stringify(r)}, expected ${action} ${cite(number)}`)
      const created = env.gh.filter((c) => c.includes('gh issue create') && c.includes('issue-aws-sdk.md'))
      if (action !== 'created' && created.length) fail(`${label}: an issue was created`)
      if (action === 'stands' && (env.gh.some((c) => c.startsWith(`cd ${env.repo} && gh issue edit 501`)) || !env.gh.some((c) => c.includes('gh issue comment 501')))) fail('stands: the body was edited, or no comment was made')
      if (action === 'updated' && !(env.gh.some((c) => c.includes('gh issue edit 501 --body-file') && c.includes('issue-aws-sdk.md')) && env.gh.some((c) => c.includes('gh issue comment 501')))) fail('updated: the body was not replaced with a comment beside it')
    }
  }

  // 8. Cap counting: creations and body updates count toward maxIssues; stands comments do not.
  checks++
  {
    const adv = { 'Yarp.ReverseProxy': { advisories: [{ id: 'GHSA-jrjw-qgr2-wfcg', severity: 'High', url: 'https://github.com/advisories/GHSA-jrjw-qgr2-wfcg', fixed: '', via: '' }] } }
    const yarpIssue = (m) => ({ number: 610, title: 'Pins: YARP: no move', marker: 'yarp', move: m, labels: ['ready-for-human'] })
    const served = (m) => ({ pins: { yarp: adv }, openIssues: [yarpIssue(m), { number: 611, title: 'Pins: The fake clock', marker: 'fake-clock', move: 'other', labels: ['ready-for-agent'] }] })
    const fams = ['yarp', 'fake-clock', 'opentelemetry']
    const a = newEnv()
    const r1 = (await execute(a, argsFor(a, { families: fams, maxIssues: 1 }), served('no move'))).result
    if ((recordOf(r1, 'yarp') || {}).action !== 'stands' || (recordOf(r1, 'fake-clock') || {}).action !== 'updated') fail(`a stands beside one update under maxIssues 1: ${JSON.stringify(r1.files)}`)
    const b = newEnv()
    const r2 = (await execute(b, argsFor(b, { families: fams, maxIssues: 1 }), served('Yarp.ReverseProxy 2.3.0 -> 2.2.0'))).result
    if ((recordOf(r2, 'yarp') || {}).action !== 'updated' || (recordOf(r2, 'fake-clock') || {}).action !== 'skipped-cap') fail(`an update spends the cap: ${JSON.stringify(r2.files)}`)
    if (b.gh.some((c) => c.includes('gh issue edit 611'))) fail('a skipped-cap family was still written')
  }

  // 9. dryRun: no gh write is issued, and the would-be issues are under the log directory.
  checks++
  {
    const env = newEnv()
    const { result, calls } = await execute(env, argsFor(env, { families: ['aws-sdk', 'aspnetcore', 'mvc-testing'], dryRun: true }), { openIssues: [{ number: 620, title: 'Pins: The MVC test host', marker: 'mvc-testing', move: 'Microsoft.AspNetCore.Mvc.Testing 10.0.12 -> 10.0.13', labels: ['ready-for-agent'] }] })
    for (const c of calls) for (const cmd of commands(c.prompt)) if (GH_WRITE.test(cmd)) fail(`a dry run's ${c.label} prompt asks for ${cmd}`)
    if (env.gh.some((c) => GH_WRITE.test(c))) fail(`a dry run wrote: ${env.gh.join(' | ')}`)
    const logDir = path.join(env.repo, LOG_REL, DATE)
    for (const f of ['aws-sdk', 'aspnetcore', 'mvc-testing']) {
      const r = recordOf(result, f)
      if (!r || !r.bodyFile.startsWith(logDir + '/') || !fs.existsSync(r.bodyFile) || !fs.readFileSync(r.bodyFile, 'utf8').includes(`**Pins family:** ${f}`)) fail(`${f}'s would-be issue is not in the log: ${JSON.stringify(r)}`)
    }
    if ((recordOf(result, 'aws-sdk') || {}).action !== 'would-create' || (recordOf(result, 'mvc-testing') || {}).action !== 'stands') fail(`dry actions: ${JSON.stringify(result.files.map((f) => [f.family, f.action]))}`)
    if (!fs.existsSync(path.join(logDir, 'report.md'))) fail('the dry run left no report body in the log')
  }

  // 10. The report issue is found by title and created only when absent.
  checks++
  {
    const absent = newEnv()
    await execute(absent, argsFor(absent, { families: ['aws-sdk'] }))
    const creates = absent.gh.filter((c) => c.includes(`gh issue create --title "${REPORT_TITLE}"`))
    if (creates.length !== 1) fail(`with no report issue, ${creates.length} were created`)
    if (!absent.gh.some((c) => /gh issue comment <its number> --body-file \S+report-comment\.md/.test(c))) fail('the first run adds no comment to the report issue')
    const present = newEnv()
    const p = await execute(present, argsFor(present, { families: ['aws-sdk'] }), { report: { number: 640, statuses: [{ pin: 'AWSSDK.S3', status: 'current' }] }, openIssues: [] })
    if (present.gh.some((c) => c.includes(`--title "${REPORT_TITLE}"`))) fail('a report issue was created though one is open')
    if (!present.gh.some((c) => c.includes('gh issue edit 640 --body-file') && c.includes('report.md'))) fail('the open report issue was not refreshed')
    const comment = fs.readFileSync(path.join(present.repo, LOG_REL, DATE, 'report-comment.md'), 'utf8')
    if (!/AWSSDK\.S3: current -> behind-patch/.test(comment) || !/Families checked: aws-sdk/.test(comment)) fail(`the run comment does not name the changed row: ${comment}`)
    if (p.result.report.number !== 640) fail(`the return names report ${p.result.report.number}`)
  }

  // 11. Terraform off: no Terraform family is checked; on, it is.
  checks++
  {
    const off = newEnv()
    const r = await execute(off, argsFor(off, { families: ['aws-sdk'] }))
    if (r.labels.some((l) => /terraform/.test(l)) || !/Terraform is off/.test(r.prompt('inventory')) || /--terraform/.test(r.prompt('inventory'))) fail(`terraform false: ${r.labels.join(', ')}`)
    if (!(dataLine(r.prompt('inventory'), 'FAMILIES IN SCOPE') || []).every((f) => f !== 'terraform')) fail('terraform false still scopes the provider family')
    const on = newEnv()
    const t = await execute(on, argsFor(on, { families: ['aws-sdk'], terraform: true }))
    if (!t.labels.includes('check-terraform') || !/--terraform --provider-versions/.test(t.prompt('inventory'))) fail(`terraform true: ${t.labels.join(', ')}`)
    const tfBody = bodyOf(on, 'terraform')
    if (!/^\*\*Owner:\*\* workstream 1/m.test(tfBody) || (recordOf(t.result, 'terraform') || {}).label !== 'ready-for-human') fail('the Terraform issue does not name workstream 1 first, or is not ready-for-human')
  }

  // 12. The trial log's shape, and the verdict the script believes: HEAD= first, TRIAL: last, and a
  //     green outcome without TRIAL: GREEN as its verdict line is not green.
  checks++
  {
    const env = newEnv()
    const r = await execute(env, argsFor(env, { families: ['aws-sdk'] }))
    const p = r.prompt('trial-aws-sdk')
    if (!(p.indexOf('HEAD=') > 0 && p.indexOf('HEAD=') < p.indexOf('SDK=') && p.indexOf('SDK=') < p.indexOf('MOVE=') && p.indexOf('MOVE=') < p.indexOf('TRIAL: GREEN'))) fail('the trial prompt does not write HEAD=, SDK= and the move before the verdict')
    if (!/worktree remove --force/.test(p) || !p.includes(path.join(env.wt, 'pins-sweep-'))) fail('the trial worktree is not under wt, or is not removed')
    const lines = fs.readFileSync(env.trialLogs['aws-sdk'], 'utf8').trim().split('\n')
    if (!lines[0].startsWith('HEAD=') || lines[lines.length - 1] !== 'TRIAL: GREEN') fail(`the trial log reads ${lines[0]} ... ${lines[lines.length - 1]}`)
    if ((recordOf(r.result, 'aws-sdk') || {}).label !== 'ready-for-agent') fail('the green control is not ready-for-agent')
    const liar = newEnv()
    const l = await execute(liar, argsFor(liar, { families: ['aws-sdk'] }), { trials: { 'aws-sdk': 'liar' } })
    if ((recordOf(l.result, 'aws-sdk') || {}).label !== 'ready-for-human') fail('a trial reported green over a TRIAL: RED verdict line is ready-for-agent')
    const smoke = newEnv()
    const s = await execute(smoke, argsFor(smoke, { families: ['aspnetcore'] }))
    if (!/tools\/smoke\.sh Debug 64\d\d/.test(s.prompt('trial-aspnetcore')) || !/--solution SpaPlatform\.slnx/.test(s.prompt('trial-aspnetcore'))) fail('a framework-wide trial does not run the solution and the smoke test on an assigned port')
  }

  // 13. Forbidden commands: across every mocked run, no commit, push, checkout or reset of the main
  //     checkout, no write to a tracked path, no install and no cache clear; and no run changed the
  //     throwaway checkout or wrote outside its log.
  checks++
  {
    const FORBIDDEN = [
      [/\bgit\b(?:\s+-C\s+\S+)?\s+(commit|push|checkout|switch|reset|stash|merge|rebase|tag)\b/, 'a commit, push, checkout or reset'],
      [/\b(brew|npm|pip3?|apt(-get)?)\s+install\b|\bdotnet\s+(tool|workload)\s+install\b|dotnet-install|\binstall\.sh\b/, 'an install'],
      [/\bnuget\s+locals\b|--clear\b|\brm\s+-r|\bbuild-server\s+shutdown\b/, 'a cache clear or a delete'],
      [/\bterraform\s+(apply|plan|destroy|import|state)\b|\bterraform\s+init\b(?![^`]*-backend=false)|(?:^|&&\s*|;\s*|\|\s*)aws\s/, 'a Terraform backend or AWS step'],
    ]
    let scanned = 0
    for (const c of allCalls) {
      const repo = c.env.repo
      for (const cmd of commands(c.prompt)) {
        scanned++
        for (const [re, what] of FORBIDDEN) if (re.test(cmd)) fail(`${c.label} asks for ${what}: ${cmd}`)
        for (const m of cmd.matchAll(/>>?\s*(\S+)/g)) if (m[1].startsWith(repo + '/') && !m[1].startsWith(`${repo}/${LOG_REL}/`)) fail(`${c.label} writes ${m[1]} in the main checkout`)
        if (/\bsed -i\b/.test(cmd) && !cmd.startsWith(`cd ${c.env.wt}/`)) fail(`${c.label} edits a file outside a trial worktree: ${cmd}`)
        if (/^cd \S+ && dotnet (build|restore|test)/.test(cmd) && cmd.startsWith(`cd ${repo} `)) fail(`${c.label} builds in the main checkout: ${cmd}`)
      }
    }
    if (scanned < 100) fail(`only ${scanned} commands were scanned, so the search read almost nothing`)
    const dirs = [...new Set(allCalls.map((c) => c.env.repo))]
    let wrote = 0
    for (const dir of dirs) {
      const status = execFileSync('git', ['-C', dir, 'status', '--porcelain', '--untracked-files=all'], { encoding: 'utf8' })
      if (status) fail(`a run changed ${dir}: ${status}`)
      const ignored = execFileSync('git', ['-C', dir, 'status', '--porcelain', '--ignored', '--untracked-files=all'], { encoding: 'utf8' }).split('\n').filter((l) => l.startsWith('!! ')).map((l) => l.slice(3))
      for (const f of ignored) if (!f.startsWith(LOG_REL + '/')) fail(`a run wrote ${f} outside the log`)
      if (ignored.length) wrote++
    }
    if (wrote < 10) fail(`only ${wrote} runs wrote under the log, so the status search read almost nothing`)
  }

  // 14. The script's families are the companion's (tools/pins_inventory.py families).
  checks++
  {
    const out = spawnSync('python3', ['-I', path.join(top, 'tools', 'pins_inventory.py'), 'families'], { encoding: 'utf8' })
    if (out.status !== 0) fail(`the companion's families command failed: ${out.stderr}`)
    else {
      const ids = JSON.parse(out.stdout).map((f) => f.id)
      const src = fs.readFileSync(scriptPath, 'utf8')
      const mine = JSON.parse(((/^const FAMILIES = (\[.*\])$/m.exec(src) || [])[1] || '[]').replace(/'/g, '"'))
      if (mine.join() !== ids.filter((i) => i !== 'terraform').join() || mine.join() !== NET.join()) fail(`the script's families ${mine.join(', ')} are not the companion's ${ids.join(', ')}`)
    }
  }

  // 15. .gitignore tracks this companion by its own name and nothing broader.
  checks++
  {
    const gi = fs.readFileSync(path.join(top, '.gitignore'), 'utf8').split('\n')
    if (!gi.includes('!.claude/workflows/pins-sweep_tests.mjs')) fail('.gitignore has no line !.claude/workflows/pins-sweep_tests.mjs')
    const env = newEnv()
    fs.mkdirSync(path.join(env.repo, '.claude/workflows'), { recursive: true })
    for (const f of ['pins-sweep_tests.mjs', 'stray_tests.mjs', 'pins-sweep.js', 'pins-sweep.fixture.json']) fs.writeFileSync(path.join(env.repo, '.claude/workflows', f), '\n')
    const ignored = (f) => spawnSync('git', ['-C', env.repo, 'check-ignore', '-q', `.claude/workflows/${f}`]).status === 0
    if (ignored('pins-sweep_tests.mjs') || ignored('pins-sweep.js')) fail('.gitignore ignores pins-sweep_tests.mjs or pins-sweep.js')
    if (!ignored('stray_tests.mjs') || !ignored('pins-sweep.fixture.json')) fail('.gitignore opens more than pins-sweep_tests.mjs')
  }

  // 16. Every agent call passes a schema and every stub's return satisfies it; no more than
  //     maxParallel agents were ever in flight.
  checks++
  {
    for (const c of allCalls) if (!c.schema || c.schema.type !== 'object') fail(`${c.label}: called without a schema`)
    if (driftAll.length) fail(`returns not matching the script's schemas: ${driftAll.slice(0, 5).join(' | ')}`)
    const env = newEnv()
    await execute(env, argsFor(env, { families: ['aws-sdk', 'opentelemetry', 'fake-clock', 'mvc-testing', 'duende'], maxParallel: 2, maxIssues: 3, maxAgents: 10 }))
    if (env.peak > 2) fail(`maxParallel 2, and ${env.peak} agents were in flight`)
  }
  // The edit rule (#109, Q164 of #286): every prompt that carries the house rules carries the amended
  // line verbatim, Edit and Write both, and no prompt still carries the Edit-only line it replaced.
  checks++
  {
    const EDIT_RULE = '- To change an existing file with Edit or Write, open it with the Read tool first in this session, not with `cat`, `head` or `sed -n`: for a file you will change, this rule overrides the auto-mode advice to read with Bash. Open it with Read again after anything else wrote it (a script, the check, a merge, `git`). Otherwise make the change with a short script written to a file under your scratchpad and run by its path. Edit and Write refuse a file not read since its last write: 16 calls went to that refusal on 2026-10-18 and 16 over the four launches from 2026-10-19 to 2026-10-21 (#109).'
    const housed = allCalls.filter((c) => /(House|Read-only) rules \(/.test(c.prompt))
    if (!housed.length) fail('no prompt carries the house rules, so the edit-rule search read nothing')
    for (const c of housed) if (!c.prompt.includes(EDIT_RULE)) fail(`the ${c.label} prompt's house rules lack the edit rule of #109`)
    for (const c of allCalls) if (/Change a file with Edit only after/.test(c.prompt)) fail(`the ${c.label} prompt still carries the Edit-only line #109 replaced`)
  }
} catch (e) {
  console.error(`pins-sweep tests: the script did not run: ${e && e.stack ? e.stack : e}`)
  process.exit(2)
}

if (failures.length) {
  console.log(`pins-sweep tests: FAILED, ${failures.length} failure(s) in ${checks} checks, on ${path.basename(scriptPath)}:`)
  failures.forEach((f) => console.log(`  - ${f}`))
  process.exit(1)
}
console.log(`pins-sweep tests: pass: ${checks} checks, on ${path.basename(scriptPath)}`)
