// The morning script's test companion. Owner: integrator. Tracked beside the script (#263), named
// for it as retro_tests.mjs and review-queue_tests.mjs are for theirs.
//
//   node .claude/workflows/morning_tests.mjs [script]
//
// Runs morning.js end to end with agent, parallel, log and phase stubbed, and with gh stubbed where
// the script reaches it: through its agents, each of which the stub plays faithfully from a served
// fixture. The Replay agent serves the `Overnight <date>` listing and the issue's body and comments
// as gh would, and the phase files as they lie on disk; the Verify agent serves the pull request's
// comments to the real tools/review_record.py and reports what it said; the appliers and the
// hand-over report what they were asked to do instead of doing it. No agent, gh or network call is
// made. The main checkout the script is given is a throwaway git repository with this repository's
// .gitignore, and a stubbed agent writes every PHASE FILE and OUTPUT FILE its prompt names into it,
// so a rerun reads back what the first run left. Every stub's return is validated against the
// schema the script passed, so a fixture that drifts from the script's schemas fails here rather
// than at a launch. Exits 0 when every check holds, 1 when one fails (each failure printed), 2 when
// the script cannot run. Its last line is its verdict.
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { execFileSync, spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const scriptPath = process.argv[2] || path.join(here, 'morning.js')
const top = path.resolve(here, '..', '..')
const READER = path.join(top, 'tools', 'review_record.py')
const DATE = '2026-10-21'
const MORNING_REL = `.scratch/overnight/log/morning-${DATE}`
const WT = '/tmp/morning-tests-wt/overnight-2026-10-21'

// ---------------------------------------------------------------- a minimal JSON Schema check
// The subset the script's schemas use: type, properties, required, items, enum.
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

// ---------------------------------------------------------------- the record the stubs serve
const sha = (c) => c.repeat(40)
// A fixture's issue number, built so the steering check's citation search (#NNN must name an issue
// here) does not read the made-up tickets of these fixtures as citations.
const cite = (n) => '#' + n
const HEAD = sha('c')
const OLDER = sha('d')
const BASE = sha('b')
const ISSUE = 226
const URL = (n) => `https://github.com/adammedford/spa-platform/issues/${n}`
const comment = (n, body, at) => ({ author: 'adammedford', createdAt: at, url: `${URL(ISSUE)}#issuecomment-${n}`, body })
const PRE = `Answer: yes, pre-accepted under the standing answer granted ${DATE}`
// Q1 and Q2 on ticket 300; ticket 301's comment is split into two parts with Q3's Answer line in the second, and
// Q4 open; the body carries a decoy Q99 that must never be read as a question.
const QUESTIONS = [
  comment(1, `## Questions: ${cite(300)}, issue-300, First ticket\n\nThe grilling record is on ${cite(300)}.\n\n**Q1.** Add the glossary term Morning branch?\nRecommended: Yes.\n${PRE}\n\n**Q2.** Rewrite the landing guide sentence?\nRecommended: Yes.\n${PRE}\n`, '2026-10-21T06:00:00Z'),
  comment(2, `## Questions: ${cite(301)}, issue-301, Second ticket (part 1 of 2)\n\n**Q3.** File the workstream ticket for the proxy?\nRecommended: Yes, under #51.\n`, '2026-10-21T06:00:01Z'),
  comment(3, `## Questions: ${cite(301)}, issue-301, Second ticket (part 2 of 2)\n\n${PRE}\n\n**Q4.** Change the port?\nRecommended: none given.\nAnswer: open\n`, '2026-10-21T06:00:02Z'),
  comment(4, '## Carried, already answered\n\n- #56 (1.4), ECS cluster\n', '2026-10-21T06:00:03Z'),
]
const BODY = `Standing answer: granted for this run (${DATE}).\n\n**3 integrated, 0 partial.**\n\nBase \`bbbbbbb\`.\n\n**Q99.** A decoy the body must never contribute.\n${PRE}\n`
const CONFIRM = comment(9, 'Confirm Q1 to Q3 as pre-accepted, with the overrides below.\n\nQ2: yes, the first override\nQ2: no, the second override wins\n', '2026-10-21T08:00:00Z')
const issueWith = (comments, body = BODY) => ({ number: ISSUE, title: `Overnight ${DATE}`, url: URL(ISSUE), state: 'OPEN', body, comments })
const LISTING_ONE = [{ number: ISSUE, title: `Overnight ${DATE}`, state: 'OPEN' }, { number: 78, title: 'morning: land an overnight run', state: 'OPEN' }]
// A pull request comment in GitHub's REST shape, as `gh api --paginate --slurp` pages it.
const prComment = (id, body, at) => ({ body, created_at: at, html_url: `https://github.com/adammedford/spa-platform/pull/400#issuecomment-${id}`, author_association: 'OWNER', user: { login: 'adammedford' } })
const RECORD_MERGE = [prComment(11, `Findings: none.\n\nReviewed ${HEAD} against ${BASE}: merge`, '2026-10-21T07:00:00Z')]

// Follow-ups (#292): pull request #287's three review records, as GitHub serves them, the latest
// ending its findings with the follow-ups table review-queue.js writes. Of #287's three carried
// findings, deploy.sh building without --pull is owned by #288 by the third record, so the table
// lists the other two: logs_tests.py and cell_doubles_tests.py running in no CI job, and the
// provisional #244 Q9 answer disagreeing with end-app-sessions.md. #288 is served as filed, in its
// real shape, whose Raised-by line names #287 too.
const PR287 = 'https://github.com/adammedford/spa-platform/pull/287'
const R287 = [`${PR287}#issuecomment-6340588449`, `${PR287}#issuecomment-6342205384`, `${PR287}#issuecomment-6343000001`]
const H287 = ['ed8d0633e9c1360fb7c2e425f63237676f4db648', '701ba9465d3d90575cecf19c09afee80346ada39', '76704c53c36ea51a9a4a81a206b76de7cf46e98c']
const F287 = 'ae6e2e19be974ca96e75ac0a0aef1e588a9ba9a5'
const FINDING_CI = 'logs_tests.py and cell_doubles_tests.py run in no CI job'
const FINDING_Q9 = 'The provisional #244 Q9 answer disagrees with end-app-sessions.md'
const FOLLOW_UPS_287 = `## Follow-ups\n\nCarried findings that name no open ticket; the morning run files each one.\n\n| Finding | Axis | Where | First carried by |\n| --- | --- | --- | --- |\n| ${FINDING_CI} | Spec | 76704c5:.github/workflows/ci.yml lines 321-326 \\| tools/scripts | ${R287[0]} |\n| ${FINDING_Q9} | Spec | #244 Q9; docs/runbooks/end-app-sessions.md | this record |\n`
const rec287 = (i, verdict, extra = '') => ({ body: `## Review\n\nHead ${H287[i]}.\n\n## Spec\n\n- a finding\n\n${extra}\n## Mutations\n\n- none\n\nReviewed ${H287[i]} against ${F287}: ${verdict}`, author_association: 'OWNER', html_url: R287[i], created_at: `2026-10-22T0${i + 1}:00:00Z` })
const ISSUE_288 = { number: 288, title: 'deploy.sh pulls the base image when it builds the gateway', body: `**Raised by:** #273, from the reviews of #287 (${R287[1]})\n**Owner:** ws1-infra\n`, url: 'https://github.com/adammedford/spa-platform/issues/288' }
const FU287 = { prs: [{ number: 287, comments: [rec287(0, 'hold'), rec287(1, 'hold'), rec287(2, 'merge', FOLLOW_UPS_287)], reviews: [] }], filed: [ISSUE_288] }

// #300: #287's description as the morning wrote it at the first head, served verbatim from its edit
// history, and the first-parent merges of the branch it was rebuilt to (76704c5), oldest first.
const BODY_287_FIRST = [
  "Land the overnight run of 2026-10-22",
  "",
  "Overnight issue: #286. Base `ae6e2e19be974ca96e75ac0a0aef1e588a9ba9a5`, head `ed8d0633e9c1360fb7c2e425f63237676f4db648`. 15 integrated, 1 partial, 1 failed, 7 skipped (blocked by the failed one).",
  "",
  "## Integrated",
  "",
  "| Task | Issue | Task merge |",
  "| --- | --- | --- |",
  "| issue-233 | #233 | `6964163` |",
  "| issue-235 | #235 | `58f0285` |",
  "| issue-239 | #239 | `192f2a6` |",
  "| 6.7 | #62 | `032eb43` (+ `eab8420`, roadmap refresh) |",
  "| issue-158 | #158 | `9b46ca8` |",
  "| issue-193 | #193 | `246c1a0` |",
  "| issue-234 | #234 | `a30b942` |",
  "| issue-251 | #251 | `9916904` |",
  "| issue-253 | #253 | `53f5d7a` |",
  "| issue-198 | #198 | `fff1181` |",
  "| issue-254 | #254 | `c32e8f3` |",
  "| issue-237 | #237 | `2946115` |",
  "| issue-240 | #240 | `9765c54` |",
  "| issue-243 | #243 | `10828ec` |",
  "| issue-273 | #273 | `ed8d063` |",
  "",
  "## Partial",
  "",
  "| Task | Issue | Task merge |",
  "| --- | --- | --- |",
  "| issue-241 | #241 | `2eebae1` (\"Merge part of task issue-241\") |",
  "",
  "## Failed",
  "",
  "| Task | Issue | Merge |",
  "| --- | --- | --- |",
  "| issue-145 | #145 | none; branch `task/issue-145-stage-host-2-10-stagehost-capturedlog-and-rawhtt` at `2e14356` is kept for #146 to fork from (CS0104 collision only #146's commit removes) |",
  "| skipped: #146, #147, #148, #149, #150, #151, #152 | | none; \"blocker not integrated: issue-145\" |",
  "",
  "#239 and #62 carry `Ticket:` rather than `Closes`: #239's lane reported box 2 as Outstanding (its task merge `192f2a6` names the count, so tick box 2 and change the line to `Closes #239` if you agree), and #62 still holds boxes 2 and 3, moved to #244 under #232 Q9, whose removal from #62's body is a morning act (#244's Q6).",
  "",
  "Closes #233",
  "Closes #235",
  "Closes #158",
  "Closes #193",
  "Closes #234",
  "Closes #251",
  "Closes #253",
  "Closes #198",
  "Closes #254",
  "Closes #237",
  "Closes #240",
  "Closes #243",
  "Closes #273",
  "Ticket: #239",
  "Ticket: #62",
  "Ticket: #241",
  "",
  "Outstanding: #241: CI does not run tools/scripts/certs_tests.py yet; that waits on change request contracts/changes/issue-241-ci-runs-the-certs-companion.md (two ci.yml steps and a D-3 sentence). Until then only tools/check.sh --full runs it. #239: box 2 is met by task merge 192f2a6 but not ticked. #62: boxes 2 and 3 moved to #244 and are still in #62's body.",
  "Outstanding test ids: none",
  "",
  "Evidence: 16 integration gate logs in .scratch/overnight/log (one per merged lane); the last, issue-273-integrate.txt, ran tools/check.sh --full at HEAD=ed8d0633e9c1360fb7c2e425f63237676f4db648 and ended \"check: pass:\", with 1715 .NET tests passed, 0 failed. The morning re-ran tools/check.sh --base ae6e2e1 at that head (pass) and pushed with the pre-push hook running.",
  "",
  "Merge subject: Land the overnight run of 2026-10-22",
  "",
  "🤖 Generated with [Claude Code](https://claude.com/claude-code)",
  "",
].join('\n')
const MERGES_287 = [
  { sha: "993b0801081fbddf0c7ad9afbfba7a702399e6a4", subject: "Merge task issue-233: the mock API image makes /app readable by the runtime user" },
  { sha: "d93ed8acae9df4deb71c3817fc3d1af59846bcd4", subject: "Merge task issue-235: spactl references AWSSDK.Signin, and a test loads a login_session profile" },
  { sha: "2ece748e3a476f64e2e92b9980b95247545f92a5", subject: "Merge task issue-239: the trust and execution documents merge no other policy document" },
  { sha: "340fd0758f9dcda06328b4a8c605fcde524264c3", subject: "Merge part of task 6.7: operating notes for deploy, rollbacks, secret rotation, ending an app's sessions and teardown" },
  { sha: "118293a4bab2f07d4fd4daa51e9319a160267e1b", subject: "Merge part of task issue-241: certs.sh resolves its Region the way preflight.sh does and refuses without one" },
  { sha: "4bd8ea7e319642564bdc176beab3e19a11f70d0e", subject: "Merge task issue-158: the rule-id-literal rule takes the eleven problem codes" },
  { sha: "4564c13d30896939a56ff0d5e74010ab58192564", subject: "Merge task issue-193: change requests 1.6b, 1.7 and issue-122 decided, and the decision rule holds disagreeing Status lines" },
  { sha: "4028026334330e80a3ac8df841dd95d4c08a040e", subject: "Merge task issue-234: image_build_tests.py holds the mock API's Dockerfile to the runtime-read case" },
  { sha: "8cf855383ab8f522a70e5c0a838ed57764225a79", subject: "Merge task issue-251: deploy.sh and rollback.sh redact the apps' client ids in the platform root's plan" },
  { sha: "b3122dab203cca3f4e52feb9e46f05a0d2aa23a3", subject: "Merge task issue-253: Playwright failures in the deployed e2e rows print no URL query, and F-06's switch-back runs under its own bound" },
  { sha: "559b8911b46ade3242418a580f60422df2aad3b5", subject: "Merge task issue-198: tunnel.sh reads the cluster, service and ALB from the outputs document" },
  { sha: "9e32b7532af8cb8c66ce0ec06322ad14c069d975", subject: "Merge task issue-254: F-05's web storage check names nothing on failure" },
  { sha: "cefbd4a08ed7eb10975d167f0e34988dc7dc71c3", subject: "Merge task issue-237: the full check runs the companions of a library a change touches" },
  { sha: "5f63d6ac61f9859bee7eb4bd9f05523198a4b3a0", subject: "Merge task issue-240: GLOSSARY.md carries Problem code and OAuth error value" },
  { sha: "dec982d9923aca156731cf3bfa0a8c64542cc071", subject: "Merge task issue-243: CI runs the cell scripts' six companions in both jobs, and D-3 names them" },
  { sha: "27ad35d7264e45029ced64b58fc63b3f610f1be7", subject: "Merge task issue-273: the gateway image runs .NET runtime 10.0.13, and the three runtimes are read one way" },
]

const made = []
process.on('exit', () => { for (const dir of made) fs.rmSync(dir, { recursive: true, force: true }) })

const newRepo = () => {
  const repo = fs.mkdtempSync(path.join(os.tmpdir(), 'morning-tests-'))
  made.push(repo)
  const git = (...a) => execFileSync('git', ['-C', repo, ...a], { encoding: 'utf8' })
  git('init', '-q')
  fs.copyFileSync(path.join(top, '.gitignore'), path.join(repo, '.gitignore'))
  fs.writeFileSync(path.join(repo, 'README.md'), 'tracked\n')
  git('add', '-A')
  git('-c', 'user.name=t', '-c', 'user.email=t@t', 'commit', '-q', '-m', 'start')
  return { repo, git, thrown: [], schemaFailures: [], peaks: {} }
}

const phaseFilesOn = (dir) => {
  const out = []
  const walk = (d) => {
    if (!fs.existsSync(d)) return
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const p = path.join(d, e.name)
      if (e.isDirectory()) walk(p)
      else if (e.name.endsWith('.json')) {
        const j = JSON.parse(fs.readFileSync(p, 'utf8'))
        out.push({ name: path.relative(dir, p).replace(/\.json$/, ''), inputs: j.inputs, result: j.result })
      }
    }
  }
  walk(dir)
  return out
}

// The Verify agent's record step, played as the prompt asks: the two listings piped to the real
// reader with the head, exit 0 a merge record at the URL it prints, "says hold: <url>" a hold.
const readRecord = (head, comments, reviews = []) => {
  const r = spawnSync('python3', ['-I', READER, head], { input: JSON.stringify({ comments: [comments], reviews: [reviews] }), encoding: 'utf8' })
  if (r.status === 0) return { verdict: 'merge', head, url: r.stdout.trim(), reader: 'exit 0' }
  const hold = /says hold: (\S+)/.exec(r.stderr)
  if (r.status === 1 && hold) return { verdict: 'hold', head, url: hold[1], reader: 'exit 1: hold' }
  return { verdict: 'none', head: '', url: '', reader: `exit ${r.status}: ${r.stderr.split('\n')[0]}` }
}

const jsonAfter = (prompt, marker) => {
  const m = new RegExp(`^${marker}: (.*)$`, 'm').exec(prompt)
  return m ? JSON.parse(m[1]) : null
}

// served: { listing, issue, verify: {...overrides}, recordComments, agentRecord, kinds: {q: kind},
//           followUps: { prs: [{number, comments, reviews}], filed: [issues already filed] },
//           filerDrops: [title],
//           replay: {pending, renamed, stoppedAt, reason}, the Replay agent's own fields as it returned them,
//           rewrite: [issue], after: {...}, refresh: the description writer's return (#300), nothingFrom: [label], the agents that return nothing,
//           as a failed agent does }
function stub(env, scriptArgs, served) {
  const calls = []
  const inFlight = {}
  globalThis.args = { repo: env.repo, wt: WT, ...scriptArgs }
  globalThis.phase = () => {}
  globalThis.log = (m) => { env.logs = env.logs || []; env.logs.push(String(m)) }
  globalThis.parallel = (fns) => Promise.all(fns.map((f) => f().catch((e) => { env.thrown.push(e); return null })))
  globalThis.agent = async (prompt, opts) => {
    const label = opts.label
    const cls = label.startsWith('apply:commit') ? 'commit' : label.startsWith('apply:tracker') ? 'tracker' : 'other'
    inFlight[cls] = (inFlight[cls] || 0) + 1
    inFlight.all = (inFlight.all || 0) + 1
    env.peaks[cls] = Math.max(env.peaks[cls] || 0, inFlight[cls])
    env.peaks.all = Math.max(env.peaks.all || 0, inFlight.all)
    try {
      await new Promise((r) => setTimeout(r, 5))
      calls.push({ label, prompt, schema: opts.schema })
      if ((served.nothingFrom || []).includes(label)) return null
      let out
      if (label === 'replay') {
        const issue = served.issue === undefined ? issueWith([...QUESTIONS, CONFIRM]) : served.issue
        out = {
          listing: served.listing || LISTING_ONE,
          issue: issue ? { number: issue.number, title: issue.title, url: issue.url, state: issue.state, body: issue.body, comments: issue.comments } : { number: 0, title: '', url: '', state: '', body: '', comments: [] },
          phaseFiles: phaseFilesOn(path.join(env.repo, MORNING_REL)),
          counts: served.replayCounts || { comment: { applied: 0, skipped: 0, failed: 0 }, edit: { applied: 0, skipped: 0, failed: 0 }, close: { applied: 0, skipped: 0, failed: 0 } },
          pending: '', renamed: '', stoppedAt: 0, reason: '',
          ...(served.replay || {}),
        }
      } else if (label === 'verify') {
        const v = served.verify || {}
        const head = v.head || HEAD
        const record = served.agentRecord || readRecord(head, served.recordComments || RECORD_MERGE)
        out = {
          outcome: v.outcome || 'ok', step: v.step || 0, reason: v.reason || '',
          base: BASE, head, branch: `overnight/${DATE}`,
          pr: { number: 400, url: 'https://github.com/adammedford/spa-platform/pull/400' },
          prBodyFile: `${env.repo}/${MORNING_REL}/pr-body.md`, mergeBodyFile: `${env.repo}/${MORNING_REL}/merge-body.md`,
          mergeSubject: jsonAfter(prompt, 'MERGE SUBJECT') || '',
          prReused: !!v.prReused, prBody: v.prBody || '', merges: v.merges || [],
          ci: v.ci || 'green', ciUrl: 'https://github.com/adammedford/spa-platform/actions/runs/1', failingJobs: v.failingJobs || [],
          pushed: true, mainMoved: false, findings: [],
          worktrees: [{ path: `${WT}/ws1-infra`, branch: 'task/x', tip: sha('e'), clean: true, ancestor: true }],
          lanes: [
            { issue: 300, task: 'issue-300', outcome: 'integrated', complete: true, mergeSha: sha('1'), outstanding: '' },
            { issue: 301, task: 'issue-301', outcome: 'partial', complete: false, mergeSha: sha('2'), outstanding: 'box 3' },
            { issue: 302, task: 'issue-302', outcome: 'failed', complete: false, mergeSha: '', outstanding: '' },
          ],
          claimed: ['claim/ws2-hosting/2.9'],
          record,
        }
      } else if (label === 'classify') {
        const qs = jsonAfter(prompt, 'QUESTIONS') || []
        const kinds = served.kinds || {}
        out = {
          questions: qs.map((q) => ({ q: q.q, issue: q.issue, task: q.task, answer: q.answer, kind: kinds[q.q] || 'integrator-edit', summary: `apply Q${q.q}` })),
          rewrites: (served.rewrite || []).map((n) => ({ issue: n, questions: qs.filter((q) => q.issue === n).map((q) => q.q), why: 'the grill said so' })),
        }
      } else if (label.startsWith('apply:')) {
        const unit = jsonAfter(prompt, 'UNIT') || { tickets: [] }
        const qs = unit.tickets.flatMap((t) => t.questions)
        out = {
          unit: unit.key || '',
          commits: qs.filter((q) => q.kind === 'integrator-edit').map((q) => ({ sha: sha('f'), subject: q.summary, questions: [q.q] })),
          issues: qs.filter((q) => q.kind === 'workstream-ticket').map((q, i) => ({ number: 500 + i, url: URL(500 + i), title: q.summary, questions: [q.q] })),
          candidates: qs.filter((q) => q.kind === 'decision-to-record').map((q) => ({ url: `${URL(q.issue)}#issuecomment-7`, questions: [q.q] })),
          rewrites: unit.tickets.filter((t) => t.rewrite).map((t) => ({ issue: t.issue, preserved: `${URL(t.issue)}#issuecomment-8` })),
          skipped: [], pushed: { branch: `integrator/morning-${DATE}`, tip: sha('f'), ok: true, reason: '' }, refused: '', unreachable: false,
        }
      } else if (label === 'pr-refresh') {
        out = served.refresh || { edited: true, reason: '' }
      } else if (label === 'handover') {
        out = { url: `${URL(ISSUE)}#issuecomment-99`, posted: true, reason: '' }
      } else if (label === 'followups') {
        // The open pull requests into main with their comments and reviews as gh serves them, and the
        // issues already filed: those the fixture serves, then those an earlier run's filer filed.
        const f = served.followUps || { prs: [], filed: [] }
        out = { prs: f.prs, filed: [...f.filed, ...(env.filed || [])], error: f.error || '' }
      } else if (label === 'followups-file') {
        // The filer files each follow-up it is given, as gh issue create would, and remembers it in
        // env, so a rerun on the same checkout reads it back as filed.
        // served.filerDrops: the titles the filer returns neither filed nor skipped, as a filer that
        // lost one would.
        const items = (jsonAfter(prompt, 'FOLLOW-UPS') || []).filter((it) => !(served.filerDrops || []).includes(it.title))
        env.filed = env.filed || []
        const filed = items.map((it) => {
          const number = 900 + env.filed.length
          const issue = { number, title: it.title, body: it.body, url: URL(number) }
          env.filed.push(issue)
          return { pr: it.pr, title: it.title, number, url: issue.url }
        })
        out = { filed, skipped: [], unreachable: false, reason: '' }
      } else if (label === 'draft-read') {
        // The draft check's one agent (#302) reads the issue and the draft file and returns them verbatim.
        const issue = served.issue === undefined ? issueWith([...QUESTIONS, CONFIRM]) : served.issue
        out = {
          listing: served.listing || LISTING_ONE,
          issue: issue ? { number: issue.number, title: issue.title, url: issue.url, state: issue.state, comments: issue.comments } : { number: 0, title: '', url: '', state: '', comments: [] },
          draft: served.draft === undefined ? '' : served.draft, error: served.draftError || '',
        }
      } else if (label === 'after') {
        const a = served.after || {}
        const contained = a.contained !== false
        out = {
          listing: served.listing || LISTING_ONE,
          issue: { number: ISSUE, title: `Overnight ${DATE}`, url: URL(ISSUE) },
          tip: HEAD, originMain: sha('9'), contained,
          closed: contained ? [300] : [], unassigned: contained ? [301] : [], removed: contained ? [`${WT}/ws1-infra`, `${WT}/overnight`] : [],
          kept: [], pruned: contained, reason: contained ? '' : `origin/main does not contain ${HEAD}`,
        }
      } else throw new Error(`unexpected agent label ${label}`)
      const bad = validate(opts.schema, out)
      if (bad.length) env.schemaFailures.push(`${label}: ${bad.slice(0, 3).join('; ')}`)
      for (const x of prompt.matchAll(/^OUTPUT FILE: (\S+)$/gm)) {
        fs.mkdirSync(path.dirname(x[1]), { recursive: true })
        fs.writeFileSync(x[1], `${label}\n`)
      }
      for (const x of prompt.matchAll(/^PHASE FILE: (\S+) INPUTS: (\S+)$/gm)) {
        fs.mkdirSync(path.dirname(x[1]), { recursive: true })
        fs.writeFileSync(x[1], JSON.stringify({ inputs: x[2], result: out }))
      }
      return out
    } finally { inFlight[cls]--; inFlight.all-- }
  }
  return calls
}

const allPrompts = []
const driftAll = []
async function execute(env, scriptArgs, served = {}, source = null) {
  const calls = stub(env, scriptArgs, served)
  const src = (source === null ? fs.readFileSync(scriptPath, 'utf8') : source).replace(/^export const meta/m, 'const meta')
  const result = await new Function('return (async () => {' + src + '\n})()')()
  for (const c of calls) allPrompts.push(c)
  for (const f of env.schemaFailures.splice(0)) driftAll.push(f)
  for (const t of env.thrown.splice(0)) driftAll.push(`a thunk threw: ${t}`)
  return { result, calls, labels: calls.map((c) => c.label), prompt: (l) => (calls.find((c) => c.label === l) || {}).prompt || '' }
}
const loadMeta = () => {
  const src = fs.readFileSync(scriptPath, 'utf8')
  const m = /^export const meta = ([\s\S]*?\n})\n/m.exec(src)
  return m ? new Function(`return (${m[1]})`)() : null
}

const failures = []
const fail = (msg) => failures.push(msg)
let checks = 0
const APPLIERS = (labels) => labels.filter((l) => l.startsWith('apply:'))

try {
  // 1. meta names morning and its phases in order; a bad launch stops before any agent.
  checks++
  const meta = loadMeta()
  if (!meta || meta.name !== 'morning') fail(`meta.name is ${meta && meta.name}, expected morning`)
  const titles = ((meta && meta.phases) || []).map((p) => p.title)
  if (titles.slice(0, 5).join() !== 'Replay,Verify,Apply,Follow-ups,Hand-over') fail(`meta phases ${titles.join(', ')}; expected Replay, Verify, Apply, Follow-ups, Hand-over first`)
  if (!titles.includes('After')) fail('meta declares no After phase for the after mode')
  for (const [label, a] of [
    ['neither date nor issue', {}],
    ['an unknown argument', { date: DATE, dryRun: true }],
    ['after as a string', { date: DATE, after: 'true' }],
    ['maxParallel zero', { date: DATE, maxParallel: 0 }],
    ['no wt', { date: DATE, wt: undefined }],
  ]) {
    const env = newRepo()
    const { result, calls } = await execute(env, a)
    if (!result || !result.halted || calls.length) fail(`${label}: ${calls.length} agent(s) ran; returned ${JSON.stringify(result)}`)
  }
  // ...and exactly date/issue, repo, wt, after and maxParallel are accepted.
  {
    const env = newRepo()
    const { result } = await execute(env, { date: DATE, after: false, maxParallel: 3 })
    if (result.halted && /bad launch/.test(result.halted)) fail(`the five arguments were refused: ${result.halted}`)
  }

  // 2. Issue lookup: zero or two open Overnight <date> issues refuse before anything but the first
  //    agent; exactly one proceeds; an issue number reads its date off the title.
  checks++
  for (const [label, listing] of [
    ['zero', [{ number: 78, title: 'morning: land an overnight run', state: 'OPEN' }, { number: 185, title: 'Overnight 2026-10-20', state: 'OPEN' }]],
    ['two', [{ number: ISSUE, title: `Overnight ${DATE}`, state: 'OPEN' }, { number: 227, title: `Overnight ${DATE}`, state: 'OPEN' }]],
  ]) {
    const env = newRepo()
    const { result, labels } = await execute(env, { date: DATE }, { listing, issue: null })
    if (!result.halted || !/Overnight 2026-10-21/.test(result.halted)) fail(`${label} issues: returned ${JSON.stringify(result.halted)}, expected a refusal naming the title`)
    if (labels.join() !== 'replay') fail(`${label} issues: dispatched ${labels.join(', ')}, expected the lookup alone`)
  }
  {
    const env = newRepo()
    const { result, labels, prompt } = await execute(env, { date: DATE })
    if (result.halted) fail(`one issue: halted ${result.halted}`)
    if (result.issue !== ISSUE) fail(`one issue: issue ${result.issue}, expected ${ISSUE}`)
    if (!labels.includes('verify') || !labels.includes('handover')) fail(`one issue: dispatched ${labels.join(', ')}`)
    // The lookup stops the agent before a write: the prompt orders the refusal ahead of the replay.
    const p = prompt('replay')
    const stopAt = p.search(/Unless exactly one issue[^\n]*stop here/)
    if (!(stopAt > 0 && stopAt < p.search(/pending-\S*\.jsonl/))) fail('the Replay prompt does not stop on the lookup before it reads the pending-writes file')
  }
  {
    const env = newRepo()
    const { result } = await execute(env, { issue: ISSUE }, { listing: [{ number: ISSUE, title: `Overnight ${DATE}`, state: 'OPEN' }] })
    if (result.date !== DATE || result.issue !== ISSUE) fail(`issue alone: date ${result.date}, issue ${result.issue}`)
    const bad = await execute(newRepo(), { issue: 98 }, { listing: [{ number: 98, title: 'Not a run', state: 'OPEN' }] })
    if (!bad.result.halted) fail('an issue whose title is not Overnight <date> was accepted')
  }

  // 3. Hand-over always runs, last, once, after every way Verify can end, and the refusal reason
  //    reaches its text; Apply runs only after a clean pass.
  checks++
  const HOLD = [prComment(12, `Findings: the merge body overstates a count.\n\nReviewed ${HEAD} against ${BASE}: hold`, '2026-10-21T07:00:00Z')]
  const ending = [
    ['a Verify stop', { verify: { outcome: 'stop', step: 3, reason: 'the worktree ws1-infra is dirty: stray.txt' } }, /step 3: the worktree ws1-infra is dirty: stray\.txt/],
    ['red CI', { verify: { ci: 'red', failingJobs: ['.NET build and test'] } }, /CI is red on c{40}: \.NET build and test/],
    ['pending CI', { verify: { ci: 'pending' } }, /CI is pending on c{40}/],
    ['no record', { recordComments: [] }, /no review record names c{40}/],
    ['a hold', { recordComments: HOLD }, /says hold: https:\S+issuecomment-12/],
    ['a clean pass', {}, null],
  ]
  for (const [label, served, reason] of ending) {
    const env = newRepo()
    const { result, labels, prompt } = await execute(env, { date: DATE }, served)
    if (labels.filter((l) => l === 'handover').length !== 1 || labels[labels.length - 1] !== 'handover') fail(`${label}: the hand-over did not run last and once: ${labels.join(', ')}`)
    const text = (/BEGIN HAND-OVER\n([\s\S]*)\nEND HAND-OVER/.exec(prompt('handover')) || [])[1] || ''
    if (!text.startsWith(`## Morning ${DATE}`)) fail(`${label}: the hand-over text does not open with its heading`)
    if (text !== result.handover.text) fail(`${label}: the posted hand-over is not the text the run returns`)
    if (reason) {
      if (!reason.test(text) || !reason.test(result.stop)) fail(`${label}: the reason ${reason} is not in the hand-over (${JSON.stringify(result.stop)})`)
      if (result.ok) fail(`${label}: returned ok`)
      if (labels.some((l) => l === 'classify' || l.startsWith('apply:'))) fail(`${label}: Apply ran: ${labels.join(', ')}`)
    } else {
      if (!result.ok || result.refusals.length) fail(`a clean pass: ok ${result.ok}, refusals ${JSON.stringify(result.refusals)}`)
      if (!labels.includes('classify') || !APPLIERS(labels).length) fail(`a clean pass with a Confirm comment dispatched ${labels.join(', ')}`)
    }
  }

  // 4. No record: the hand-over carries the review-queue command for the pull request's number,
  //    and no Apply commit step is dispatched.
  checks++
  {
    const env = newRepo()
    const { result, labels } = await execute(env, { date: DATE }, { recordComments: [] })
    const t = result.handover.text
    if (!t.includes(`${env.repo}/.claude/workflows/review-queue.js`) || !t.includes('"prs": [400]')) fail(`no record: the hand-over carries no review-queue command for ${cite(400)}:\n${t}`)
    if (labels.some((l) => l.startsWith('apply:commit'))) fail(`no record: a commit applier ran: ${labels.join(', ')}`)
    if (result.stopKind !== 'record') fail(`no record: stopKind ${result.stopKind}`)
  }

  // 5. Record checks: read through tools/review_record.py; an older head's record does not satisfy,
  //    even when an agent reports it as a merge; the current head's does, hand-written in the fixed
  //    form; a hold ends at the hand-over naming the record.
  checks++
  {
    const older = await execute(newRepo(), { date: DATE }, { recordComments: [prComment(13, `Reviewed ${OLDER} against ${BASE}: merge`, '2026-10-21T07:00:00Z')] })
    if (older.result.ok || older.result.stopKind !== 'record') fail(`a record of an older head satisfied Verify: ${JSON.stringify(older.result.stop)}`)
    const sloppy = await execute(newRepo(), { date: DATE }, { agentRecord: { verdict: 'merge', head: OLDER, url: 'https://github.com/adammedford/spa-platform/pull/400#issuecomment-13', reader: 'exit 0' } })
    if (sloppy.result.ok || !/names d{40}, which is not this head/.test(sloppy.result.stop)) fail(`an agent's report of an older head's merge cleared this head: ${JSON.stringify(sloppy.result.stop)}`)
    const written = await execute(newRepo(), { date: DATE }, { recordComments: [prComment(14, `Reviewed ${HEAD} against ${BASE}: merge`, '2026-10-21T07:00:00Z')] })
    if (!written.result.ok || !/issuecomment-14$/.test(written.result.record.url)) fail(`a hand-written record of the head did not satisfy Verify: ${JSON.stringify(written.result.stop)}`)
    const vp = written.prompt('verify')
    if (!vp.includes(`python3 -I ${written.result.runLog.split('/.scratch/')[0]}/tools/review_record.py <head>`)) fail('the Verify prompt does not read the record through tools/review_record.py')
    const held = await execute(newRepo(), { date: DATE }, { recordComments: HOLD })
    if (held.result.stopKind !== 'hold' || !held.result.handover.text.includes('pull/400#issuecomment-12')) fail(`a hold: stopKind ${held.result.stopKind}`)
  }

  // 6. Confirmation: no Confirm comment runs Apply dry, with no applier and the would-do list in the
  //    hand-over; one dispatches the classifier and the appliers; a later Q<n>: line wins.
  checks++
  {
    const dry = await execute(newRepo(), { date: DATE }, { issue: issueWith(QUESTIONS) })
    if (APPLIERS(dry.labels).length || !dry.labels.includes('classify')) fail(`no Confirm: dispatched ${dry.labels.join(', ')}`)
    if (!dry.result.apply.dry || !dry.result.apply.wouldDo.length) fail(`no Confirm: dry ${dry.result.apply.dry}, would-do ${JSON.stringify(dry.result.apply.wouldDo)}`)
    if (!new RegExp(`### Would do, once confirmed\\n\\n- Q1 \\(${cite(300)}\\)`).test(dry.result.handover.text)) fail('no Confirm: the would-do list is not in the hand-over')
    if (!/provisional/.test(dry.prompt('classify'))) fail('no Confirm: the classifier was not told the answers are provisional')
    const wet = await execute(newRepo(), { date: DATE })
    if (wet.result.apply.dry || !wet.labels.includes('classify') || !APPLIERS(wet.labels).length) fail(`a Confirm comment: dry ${wet.result.apply.dry}, dispatched ${wet.labels.join(', ')}`)
    const sent = (jsonAfter(wet.prompt('classify'), 'QUESTIONS') || []).map((q) => q.q)
    if (sent.includes(2) || wet.result.apply.answers[2].answer !== 'no, the second override wins') fail(`the later Q2 override did not win: sent ${sent}, answer ${JSON.stringify(wet.result.apply.answers[2])}`)
    // ...and the other way round, across two Confirm comments: a later yes beats an earlier no.
    const flip = await execute(newRepo(), { date: DATE }, { issue: issueWith([...QUESTIONS, CONFIRM, comment(10, 'Confirm Q2.\n\nQ2: yes, on reflection\n', '2026-10-21T09:00:00Z')]) })
    const flipped = (jsonAfter(flip.prompt('classify'), 'QUESTIONS') || []).find((q) => q.q === 2)
    if (!flipped || flipped.answer !== 'yes, on reflection' || flipped.authority !== `${URL(ISSUE)}#issuecomment-10`) fail(`a later Confirm comment's Q2 line did not win: ${JSON.stringify(flipped)}`)
  }

  // 6a. A Classify agent that returns nothing is named as failed in the refusals and the hand-over
  //     (#277), dry or confirmed, and nothing is applied; never read as a classifier that returned
  //     no questions. Paired with the same runs with every agent answering, which name no failure.
  checks++
  {
    const NAMED = /the Classify agent returned nothing: Q\d+(, Q\d+)* (was|were) not classified, so none is applied/
    for (const [label, served] of [['confirmed', {}], ['dry', { issue: issueWith(QUESTIONS) }]]) {
      const all = await execute(newRepo(), { date: DATE }, served)
      if (all.result.refusals.some((r) => NAMED.test(r))) fail(`${label}, every agent answering: the Classify agent is named as failed: ${JSON.stringify(all.result.refusals)}`)
      const none = await execute(newRepo(), { date: DATE }, { ...served, nothingFrom: ['classify'] })
      if (!none.labels.includes('classify')) fail(`${label}: no classifier ran, so the null proves nothing`)
      if (!none.result.refusals.some((r) => NAMED.test(r)) || !NAMED.test(none.result.handover.text)) fail(`${label}: a null Classify agent is not named in the refusals and the hand-over: ${JSON.stringify(none.result.refusals)}`)
      if (APPLIERS(none.labels).length || none.result.apply.done.length || none.result.apply.wouldDo.length) fail(`${label}: after a null Classify agent, applied ${JSON.stringify(none.result.apply.done)} and would do ${JSON.stringify(none.result.apply.wouldDo)}`)
    }
  }

  // 7. Questions come from the comments, numbered across them and across a split comment's parts in
  //    either order; the body's decoy is never read; a missing part is refused, not half-read.
  checks++
  {
    const { result, prompt } = await execute(newRepo(), { date: DATE })
    const sent = jsonAfter(prompt('classify'), 'QUESTIONS') || []
    if (sent.map((q) => q.q).join() !== '1,3') fail(`confirmed questions sent to the classifier: ${sent.map((q) => q.q)}; expected 1,3`)
    const q3 = sent.find((q) => q.q === 3)
    if (!q3 || q3.answer !== PRE.slice('Answer: '.length) || q3.issue !== 301 || q3.task !== 'issue-301') fail(`Q3, cut across two parts, read as ${JSON.stringify(q3)}`)
    if (Object.keys(result.apply.answers).join() !== '1,2,3,4') fail(`questions read: ${Object.keys(result.apply.answers)}; expected 1-4, and never the body's Q99`)
    if (result.apply.open.join() !== '4') fail(`open questions ${result.apply.open}; expected Q4`)
    if (!/### Open questions\n\n- Q4\n/.test(result.handover.text) || !/### Answered no, untouched\n\n- Q2\n/.test(result.handover.text)) fail('the hand-over does not list the open question Q4 and the question answered no, Q2')
    const reversed = await execute(newRepo(), { date: DATE }, { issue: issueWith([QUESTIONS[0], QUESTIONS[2], QUESTIONS[1], QUESTIONS[3], CONFIRM]) })
    if (((jsonAfter(reversed.prompt('classify'), 'QUESTIONS') || []).map((q) => q.q)).join() !== '1,3') fail('parts posted out of order were not joined in k order')
    const missing = await execute(newRepo(), { date: DATE }, { issue: issueWith([QUESTIONS[0], QUESTIONS[1], QUESTIONS[3], CONFIRM]) })
    if (3 in missing.result.apply.answers || !missing.result.refusals.some((r) => new RegExp(`${cite(301)} are split into 2 parts and 1`).test(r))) fail(`a missing part: answers ${Object.keys(missing.result.apply.answers)}, refusals ${JSON.stringify(missing.result.refusals)}`)
  }

  // A night of many tickets: one question each, on ticket 300 upwards, all confirmed.
  const night = (n) => {
    const cs = Array.from({ length: n }, (_, i) => comment(20 + i, `## Questions: #${300 + i}, issue-${300 + i}, Ticket ${i}\n\n**Q${i + 1}.** Do thing ${i}?\nRecommended: Yes.\n${PRE}\n`, `2026-10-21T06:0${i}:00Z`))
    return issueWith([...cs, comment(90, `Confirm Q1 to Q${n} as pre-accepted.`, '2026-10-21T08:00:00Z')])
  }

  // 8. Applier concurrency: commit appliers never overlap; tracker-only appliers and every agent in
  //    flight stay within maxParallel.
  checks++
  {
    const env = newRepo()
    const kinds = { 1: 'integrator-edit', 2: 'integrator-edit', 3: 'integrator-edit', 4: 'workstream-ticket', 5: 'decision-to-record' }
    const { labels } = await execute(env, { date: DATE, maxParallel: 5 }, { issue: night(5), kinds })
    const commitsRun = labels.filter((l) => l.startsWith('apply:commit')).length
    const trackersRun = labels.filter((l) => l.startsWith('apply:tracker')).length
    if (commitsRun !== 3 || trackersRun !== 2) fail(`concurrency: ${commitsRun} commit and ${trackersRun} tracker appliers, expected 3 and 2`)
    if (env.peaks.commit !== 1) fail(`commit appliers overlapped: ${env.peaks.commit} in flight at once`)
    if (env.peaks.tracker < 2) fail(`tracker-only appliers ran one at a time (${env.peaks.tracker}), so the bound below was never tested`)
    const tight = newRepo()
    await execute(tight, { date: DATE, maxParallel: 2 }, { issue: night(6), kinds: { 1: 'workstream-ticket', 2: 'workstream-ticket', 3: 'workstream-ticket', 4: 'decision-to-record', 5: 'workstream-ticket', 6: 'decision-to-record' } })
    if ((tight.peaks.tracker || 0) > 2 || tight.peaks.all > 2) fail(`maxParallel 2: ${tight.peaks.tracker} tracker appliers and ${tight.peaks.all} agents in flight at once`)
  }

  // 9. Budget: at the default maxParallel a night of eight tickets of every kind, with follow-ups to
  //    file, dispatches at most nine agents, and the return counts what ran.
  checks++
  {
    const kinds = { 1: 'integrator-edit', 2: 'integrator-edit', 3: 'workstream-ticket', 4: 'workstream-ticket', 5: 'decision-to-record', 6: 'decision-to-record', 7: 'integrator-edit', 8: 'workstream-ticket' }
    const { result, labels } = await execute(newRepo(), { date: DATE }, { issue: night(8), kinds, rewrite: [303], followUps: FU287 })
    if (!labels.includes('followups-file')) fail('budget: no follow-up was filed, so the two follow-up agents were not counted')
    if (labels.length > 9 || APPLIERS(labels).length > 3) fail(`budget: ${labels.length} agents (${APPLIERS(labels).length} appliers): ${labels.join(', ')}`)
    if (result.agents !== labels.length) fail(`budget: the return counts ${result.agents} agents, ${labels.length} ran`)
    const unitsSent = labels.filter((l) => l.startsWith('apply:')).length
    const done = result.apply.done.join('\n')
    for (const q of [1, 2, 3, 4, 5, 6, 7, 8]) if (!new RegExp(`Q${q}\\b`).test(done)) fail(`budget: grouping ${unitsSent} appliers dropped Q${q}`)
    if (!new RegExp(`rewrote ${cite(303)}`).test(done)) fail('budget: the rewrite of ticket 303 was dropped by the grouping')
  }

  // 10. after: true refuses, with one agent and nothing done, until origin/main contains the
  //     integration tip; once it does, one agent does the work.
  checks++
  {
    const no = await execute(newRepo(), { date: DATE, after: true }, { after: { contained: false } })
    if (no.labels.join() !== 'after' || !/does not contain the integration tip c{40}/.test(no.result.refused || '') || no.result.closed.length || no.result.removed.length || no.result.unassigned.length) fail(`after, not contained: ${no.labels.join(', ')} -> ${JSON.stringify(no.result)}`)
    const yes = await execute(newRepo(), { date: DATE, after: true }, { after: { contained: true } })
    if (yes.labels.join() !== 'after' || yes.result.refused !== '' || yes.result.closed.join() !== '300') fail(`after, contained: ${yes.labels.join(', ')} -> ${JSON.stringify(yes.result)}`)
    const p = yes.prompt('after')
    if (!(p.indexOf('merge-base --is-ancestor <tip> origin/main') > 0 && p.indexOf('merge-base --is-ancestor <tip> origin/main') < p.indexOf('gh issue close'))) fail('the after prompt does not check origin/main before it closes anything')
    const none = await execute(newRepo(), { date: DATE, after: true }, { listing: [] })
    if (!none.result.halted) fail('after with no Overnight issue was not refused')
  }

  // 11. Resumption: a rerun whose phase files show the classifier and the appliers done dispatches
  //     neither again.
  checks++
  {
    const env = newRepo()
    const first = await execute(env, { date: DATE })
    const second = await execute(env, { date: DATE })
    if (!first.labels.includes('classify')) fail('resumption: the first run did not classify')
    if (second.labels.includes('classify') || APPLIERS(second.labels).length) fail(`resumption: the rerun dispatched ${second.labels.join(', ')}`)
    if (!second.result.reused.includes('classify') || !second.result.reused.some((r) => r.startsWith('apply/'))) fail(`resumption: reused ${second.result.reused}`)
    if (second.result.apply.done.join() !== first.result.apply.done.join()) fail('resumption: the rerun\'s hand-over lost what the first run applied')
  }

  // 12. Forbidden commands. Every command a prompt asks an agent to run is a backtick span; across
  //     every mocked run above none is the landing script, --admin, --no-verify, a force-push, a tag
  //     or a branch deletion. The span count shows the search read the commands it judges.
  checks++
  {
    const FORBIDDEN = [
      ['the landing script', /tools\/land\.sh/],
      ['--admin', /--admin\b/],
      ['--no-verify', /--no-verify\b/],
      ['a force-push', /\bpush\b[^`]*(--force|\s-f\b|\s\+\S)/],
      ['a tag', /\bgit\b[^`]*\btag\b/],
      ['a branch deletion', /\bbranch\b[^`]*\s(-d|-D|--delete)\b|\bpush\b[^`]*(--delete|\s:\S)/],
      ['a forced worktree removal', /worktree remove[^`]*--force/],
    ]
    let spans = 0
    let pushes = 0
    for (const c of allPrompts) {
      for (const m of c.prompt.matchAll(/`([^`\n]+)`/g)) {
        spans++
        if (/\bpush -u origin\b/.test(m[1])) pushes++
        for (const [what, re] of FORBIDDEN) if (re.test(m[1])) fail(`the ${c.label} prompt asks for ${what}: ${m[1]}`)
      }
      const text = /BEGIN HAND-OVER\n([\s\S]*)\nEND HAND-OVER/.exec(c.prompt)
      if (text && text[1].includes('`')) fail('a hand-over text carries a backtick span, which reads as a command to run')
    }
    if (spans < 100 || pushes < 2) fail(`the command search read ${spans} spans and ${pushes} pushes; it cannot have judged the prompts`)
  }

  // 13. .gitignore tracks this companion by its own name and nothing broader.
  checks++
  {
    const gi = fs.readFileSync(path.join(top, '.gitignore'), 'utf8').split('\n')
    if (!gi.includes('!.claude/workflows/morning_tests.mjs')) fail('.gitignore has no line !.claude/workflows/morning_tests.mjs')
    const env = newRepo()
    for (const f of ['morning_tests.mjs', 'stray_tests.mjs', 'morning.js']) {
      fs.mkdirSync(path.join(env.repo, '.claude/workflows'), { recursive: true })
      fs.writeFileSync(path.join(env.repo, '.claude/workflows', f), '\n')
    }
    const ignored = (f) => spawnSync('git', ['-C', env.repo, 'check-ignore', '-q', `.claude/workflows/${f}`]).status === 0
    if (ignored('morning_tests.mjs') || ignored('morning.js')) fail('.gitignore ignores morning_tests.mjs or morning.js')
    if (!ignored('stray_tests.mjs')) fail('.gitignore opens more than morning_tests.mjs: another companion is not ignored')
  }

  // 14. The question markup the nights have written: "**Q<n>.**", "Q<n>. **...**" with indented and
  //     list-item lines under it, and "**Q<n>** (...)." all read, and a "Q1 to Q2, from ..." preamble
  //     opens no question. A Confirm comment naming a question no comment yielded, or a questions
  //     comment with no question in it, is a refusal and nothing is applied; a Confirm comment's
  //     later sentence that names questions it leaves open covers none of them.
  checks++
  {
    const forms = [
      comment(30, `## Questions: ${cite(310)}, issue-310, Form two\n\nQ1 to Q2, from the grill's questions.\n\nQ1. **Add the term?**\n   Recommended: yes.\n   ${PRE}\n\nQ2. **Rewrite the sentence?**\n   - Recommended: yes.\n   - Answer: open\n`, '2026-10-21T06:00:00Z'),
      comment(31, `## Questions: ${cite(311)}, issue-311, Form three\n\n**Q3** (${cite(311)} Q1). **File the ticket?**\nRecommended: Yes.\n${PRE}\n`, '2026-10-21T06:00:01Z'),
    ]
    const r = await execute(newRepo(), { date: DATE }, { issue: issueWith([...forms, comment(32, 'Confirm Q1 to Q3 as pre-accepted.', '2026-10-21T08:00:00Z')]) })
    const a = r.result.apply.answers
    if (Object.keys(a).join() !== '1,2,3' || a[1].verdict !== 'yes' || a[2].verdict !== 'open' || a[3].verdict !== 'yes' || r.result.refusals.length) fail(`the nights' markup: answers ${JSON.stringify(a)}, refusals ${JSON.stringify(r.result.refusals)}`)
    const sent = (jsonAfter(r.prompt('classify'), 'QUESTIONS') || []).map((q) => q.q)
    if (sent.join() !== '1,3') fail(`the nights' markup: the classifier was sent ${sent}; expected 1,3`)
    const unread = await execute(newRepo(), { date: DATE }, { issue: issueWith([...QUESTIONS, comment(33, 'Confirm Q1 to Q5 as pre-accepted.', '2026-10-21T08:00:00Z')]) })
    if (unread.labels.some((l) => l === 'classify' || l.startsWith('apply:')) || !/Q5, which no "## Questions:" comment yielded/.test(unread.result.handover.text)) fail(`a Confirm comment naming an unread Q5: dispatched ${unread.labels.join(', ')}; refusals ${JSON.stringify(unread.result.refusals)}`)
    const blank = await execute(newRepo(), { date: DATE }, { issue: issueWith([...QUESTIONS, comment(34, `## Questions: ${cite(312)}, issue-312, No markup\n\nQuestion one: add the term?\nAnswer: yes\n`, '2026-10-21T06:00:04Z'), CONFIRM]) })
    if (!blank.result.refusals.some((x) => new RegExp(`${cite(312)} carries no question`).test(x))) fail(`a questions comment with no question in it was not refused: ${JSON.stringify(blank.result.refusals)}`)
    const later = await execute(newRepo(), { date: DATE }, { issue: issueWith([...QUESTIONS, comment(35, 'Confirm Q1. Q3 is not covered by this comment and stays open.', '2026-10-21T08:00:00Z')]) })
    if (later.result.apply.answers[3].confirmed || !later.result.apply.answers[1].confirmed) fail(`a Confirm comment's later sentence: answers ${JSON.stringify(later.result.apply.answers)}`)
  }

  // 15. A Confirm comment counts only from Adam's account (#78 -> "Apply": "Adam's comment on the
  //     Overnight issue beginning Confirm"): another account's "Confirm Q1", override line included,
  //     confirms nothing, so Apply stays dry and no commit applier runs; Adam's own still confirms.
  checks++
  {
    const outsider = (n, body, at) => ({ ...comment(n, body, at), author: 'someone-else' })
    const out = await execute(newRepo(), { date: DATE }, { issue: issueWith([...QUESTIONS, outsider(40, 'Confirm Q1.\n\nQ1: yes, an outsider says so\n', '2026-10-21T08:00:00Z')]) })
    const a1 = out.result.apply.answers[1] || {}
    if (a1.confirmed || a1.answer !== `yes, pre-accepted under the standing answer granted ${DATE}`) fail(`an outsider's Confirm Q1 confirmed or overrode Q1: ${JSON.stringify(a1)}`)
    if (!out.result.apply.dry) fail('an outsider\'s Confirm comment took Apply out of its dry run')
    if (out.labels.some((l) => l.startsWith('apply:commit'))) fail(`an outsider's Confirm comment dispatched a commit applier: ${out.labels.join(', ')}`)
    const own = await execute(newRepo(), { date: DATE }, { issue: issueWith([...QUESTIONS, outsider(40, 'Confirm Q2.', '2026-10-21T07:59:00Z'), comment(41, 'Confirm Q1.', '2026-10-21T08:00:00Z')]) })
    const o = own.result.apply.answers
    if (own.result.apply.dry || !o[1].confirmed || o[2].confirmed || !own.labels.some((l) => l.startsWith('apply:commit'))) fail(`Adam's Confirm Q1 beside an outsider's Confirm Q2: dry ${own.result.apply.dry}, answers ${JSON.stringify(o)}, dispatched ${own.labels.join(', ')}`)
  }

  // 17. Follow-ups (#292). The morning reads the follow-ups table of the latest review record on each
  //     open pull request into main and files each row no issue already holds, with a Raised-by line
  //     naming the pull request and a link to the record, as neither a sub-issue nor a blocker. #287's
  //     latest record lists two; #288, filed by hand for the third finding, names #287 on its
  //     Raised-by line and holds neither of them. A rerun files nothing; Verify's verdict gates none of it.
  checks++
  {
    const env = newRepo()
    const first = await execute(env, { date: DATE }, { followUps: FU287 })
    const sent = jsonAfter(first.prompt('followups-file'), 'FOLLOW-UPS') || []
    if (JSON.stringify(sent.map((f) => [f.pr, f.title])) !== JSON.stringify([[287, FINDING_CI], [287, FINDING_Q9]])) fail(`follow-ups: the filer was given ${JSON.stringify(sent.map((f) => [f.pr, f.title]))}, expected #287's two rows`)
    for (const f of sent) {
      if (!f.body.startsWith('**Raised by:** #287')) fail(`follow-ups: the body of ${JSON.stringify(f.title)} does not open with the Raised-by line: ${JSON.stringify(f.body.split('\n')[0])}`)
      if (!f.body.includes(R287[2])) fail(`follow-ups: the body of ${JSON.stringify(f.title)} does not link the record ${R287[2]}`)
    }
    if (sent[0] && !sent[0].body.includes(R287[0])) fail('follow-ups: the first row\'s body does not name the earliest record that carried it')
    if (sent[0] && !sent[0].body.includes('76704c5:.github/workflows/ci.yml lines 321-326 | tools/scripts')) fail('follow-ups: the escaped bar in the Where cell was not read back')
    if (sent[1] && !sent[1].body.includes(`first carried by ${R287[2]}`)) fail('follow-ups: "this record" was not read as the record the table is in')
    const fp = first.prompt('followups-file')
    const spans = [...fp.matchAll(/`([^`\n]+)`/g)].map((m) => m[1])
    if (!spans.some((x) => /gh issue create\b/.test(x) && /--label needs-triage\b/.test(x))) fail('follow-ups: the filer prompt asks for no gh issue create with the needs-triage label')
    if (spans.some((x) => /--parent\b|blocked-by/.test(x))) fail(`follow-ups: the filer prompt files a sub-issue or a blocker: ${spans.filter((x) => /--parent\b|blocked-by/.test(x)).join(' | ')}`)
    const ho = first.result.handover.text
    if (!/### Follow-ups filed/.test(ho) || !ho.includes(FINDING_CI) || !ho.includes(FINDING_Q9)) fail(`follow-ups: the hand-over does not list what was filed:\n${ho}`)
    if (JSON.stringify((first.result.followUps || {}).filed || []) === '[]') fail('follow-ups: the return lists nothing filed')
    const again = await execute(env, { date: DATE }, { followUps: FU287 })
    if (again.labels.includes('followups-file')) fail('follow-ups: a rerun filed again what the first run filed')
    if (((again.result.followUps || {}).already || []).length !== 2) fail(`follow-ups: a rerun reports ${JSON.stringify((again.result.followUps || {}).already)} as already filed, expected both rows`)
    // Filed by hand under the finding's title, with a Raised-by line naming the pull request, counts.
    const byHand = await execute(newRepo(), { date: DATE }, { followUps: { ...FU287, filed: [ISSUE_288, { number: 777, title: FINDING_Q9, body: '**Raised by:** #287\n', url: URL(777) }] } })
    const sent2 = (jsonAfter(byHand.prompt('followups-file'), 'FOLLOW-UPS') || []).map((f) => f.title)
    if (JSON.stringify(sent2) !== JSON.stringify([FINDING_CI])) fail(`follow-ups: with ${FINDING_Q9} filed by hand, the filer was given ${JSON.stringify(sent2)}`)
    // Only the latest record decides: an earlier record's table, a newer record without one, files nothing.
    const older = await execute(newRepo(), { date: DATE }, { followUps: { prs: [{ number: 287, comments: [rec287(1, 'hold', FOLLOW_UPS_287), rec287(2, 'merge')], reviews: [] }], filed: [] } })
    if (older.labels.includes('followups-file')) fail('follow-ups: an earlier record\'s table was filed though the latest record lists none')
    // A table under a line that is no record, or posted from outside the repository, files nothing.
    const outsider = { ...rec287(2, 'merge', FOLLOW_UPS_287), author_association: 'NONE' }
    const out = await execute(newRepo(), { date: DATE }, { followUps: { prs: [{ number: 287, comments: [outsider], reviews: [] }], filed: [] } })
    if (out.labels.includes('followups-file')) fail('follow-ups: a table in a comment from outside the repository was filed')
    // Verify's stop does not hold the follow-ups back: they are decided by the review, not the night.
    const stopped = await execute(newRepo(), { date: DATE }, { followUps: FU287, verify: { outcome: 'stop', step: 3, reason: 'dirty' } })
    if (!stopped.labels.includes('followups-file')) fail('follow-ups: a Verify stop kept the follow-ups from being filed')
    // A read agent that returns nothing is named, never read as a queue with nothing to file.
    const dead = await execute(newRepo(), { date: DATE }, { followUps: FU287, nothingFrom: ['followups'] })
    if (!dead.result.refusals.some((r) => /follow-ups agent returned nothing/.test(r))) fail(`follow-ups: a dead read agent is not named: ${JSON.stringify(dead.result.refusals)}`)
    // A filer that returns a follow-up neither filed nor skipped has not accounted for it: the run
    // names it, never reads the silence as done (CODING_STANDARDS.md -> "A check fails closed").
    const lost = await execute(newRepo(), { date: DATE }, { followUps: FU287, filerDrops: [FINDING_Q9] })
    if (!lost.result.refusals.some((r) => /neither filed nor skipped/.test(r) && r.includes(FINDING_Q9))) fail(`follow-ups: a follow-up the filer dropped is not named: ${JSON.stringify(lost.result.refusals)}`)
  }

  // 18. The follow-ups table has one grammar: morning.js's copy of its header, its "this record" cell,
  //     the record line and its parser are review-queue.js's, character for character.
  checks++
  {
    const mine = fs.readFileSync(scriptPath, 'utf8')
    const theirs = fs.readFileSync(path.join(here, 'review-queue.js'), 'utf8')
    for (const name of ['FOLLOW_UP_HEAD', 'THIS_RECORD', 'PY_LINE_BREAK', 'PY_SPACE', 'PY_STRIP', 'RECORD', 'REPOSITORY_ACCOUNTS', 'sameFinding']) {
      const line = (src) => (new RegExp(`^const ${name} = .*$`, 'm').exec(src) || [''])[0]
      if (!line(mine) || line(mine) !== line(theirs)) fail(`the grammar: morning.js's ${name} is ${JSON.stringify(line(mine))}, review-queue.js's ${JSON.stringify(line(theirs))}`)
    }
    const fn = (src) => (/^const followUpsOf = [\s\S]*?\n}\n/m.exec(src) || [''])[0]
    if (!fn(mine) || fn(mine) !== fn(theirs)) fail('the grammar: morning.js\'s followUpsOf is not review-queue.js\'s')
  }

  // 19. A Replay field holding only a pair of quotes, or only whitespace, is empty (#299). On
  //     2026-10-22 no pending-writes file existed, and the Replay agent returned pending, renamed and
  //     reason as the two-character string "" (its result in that morning's workflow record, the
  //     counts all zero), which the run read as a stop: "Replay stopped at line 0 of \"\": \"\"".
  //     The first case is that result; the second, a real stop, must still be refused by its line.
  checks++
  {
    const ZERO = { applied: 0, skipped: 0, failed: 0 }
    const OCT22 = { counts: { comment: ZERO, edit: ZERO, close: ZERO }, pending: '""', renamed: '""', stoppedAt: 0, reason: '""' }
    const replayLine = (t) => (/### Replay\n\n- (.*)\n/.exec(t) || [])[1] || ''
    const NOTHING = 'comment 0 applied, 0 skipped, 0 failed; edit 0 applied, 0 skipped, 0 failed; close 0 applied, 0 skipped, 0 failed; nothing was replayed'
    for (const [label, replay] of [
      ['2026-10-22\'s Replay result', OCT22],
      ['a reason of whitespace', { ...OCT22, pending: '', renamed: ' ', reason: ' \n ' }],
      ['a quoted empty reason padded with whitespace', { ...OCT22, reason: ' "" ' }],
    ]) {
      const { result } = await execute(newRepo(), { date: DATE }, { replay })
      if (result.refusals.length) fail(`${label}: refused ${JSON.stringify(result.refusals)}, expected none`)
      if (result.replay.reason !== '' || result.replay.renamed !== '') fail(`${label}: the run returns reason ${JSON.stringify(result.replay.reason)} and renamed ${JSON.stringify(result.replay.renamed)}, expected both empty`)
      if (replayLine(result.handover.text) !== NOTHING) fail(`${label}: the hand-over's Replay line is ${JSON.stringify(replayLine(result.handover.text))}, expected ${JSON.stringify(NOTHING)}`)
    }
    const PENDING = `/tmp/morning-tests-repo/.scratch/overnight/log/pending-${DATE}.jsonl`
    const real = await execute(newRepo(), { date: DATE }, { replay: { counts: { comment: { applied: 2, skipped: 0, failed: 1 }, edit: ZERO, close: ZERO }, pending: PENDING, renamed: '', stoppedAt: 3, reason: 'HTTP 502: Bad Gateway (https://api.github.com/graphql)' } })
    const want = `Replay stopped at line 3 of ${PENDING}: HTTP 502: Bad Gateway (https://api.github.com/graphql)`
    if (!real.result.refusals.includes(want)) fail(`a real stop: refusals ${JSON.stringify(real.result.refusals)}, expected ${JSON.stringify(want)}`)
    if (!/; stopped at line 3: HTTP 502/.test(replayLine(real.result.handover.text)) || /nothing was replayed/.test(real.result.handover.text)) fail(`a real stop: the hand-over's Replay line is ${JSON.stringify(replayLine(real.result.handover.text))}`)
  }

  // 20. A re-run refreshes the description of the pull request an earlier morning opened (#300). The
  //     fixture is 2026-10-22's: #287's first description, as the morning wrote it at ed8d063, and the
  //     first-parent merges of the branch rebuilt to 76704c5, where "Merge task 6.7" became "Merge part
  //     of task 6.7". The refresh rewrites the head, the task-merge shas and the counts, moves 6.7 to
  //     Partial, keeps every other line byte for byte, and the hand-over names each line it rewrote.
  checks++
  {
    const NOW = '76704c53c36ea51a9a4a81a206b76de7cf46e98c'
    const reran = { verify: { head: NOW, prReused: true, prBody: BODY_287_FIRST, merges: MERGES_287 }, agentRecord: { verdict: 'hold', head: NOW, url: R287[1], reader: 'exit 1: hold' } }
    const r = await execute(newRepo(), { date: DATE }, reran)
    const wrote = (/BEGIN DESCRIPTION\n([\s\S]*)\nEND DESCRIPTION/.exec(r.prompt('pr-refresh')) || [])[1]
    if (r.labels.filter((l) => l === 'pr-refresh').length !== 1 || wrote === undefined) fail(`2026-10-22's re-run: dispatched ${r.labels.join(', ')}, expected one description writer`)
    const body = wrote || ''
    if (body !== (r.result.refresh || {}).body) fail('2026-10-22\'s re-run: the writer was given a description other than the one the run returns')
    const before = BODY_287_FIRST.split('\n')
    const after = body.split('\n')
    const headLine = (ls) => ls.find((l) => l.startsWith('Overnight issue: '))
    if (headLine(after) !== `Overnight issue: ${cite(286)}. Base \`${F287}\`, head \`${NOW}\`. 14 integrated, 2 partial, 1 failed, 7 skipped (blocked by the failed one).`) fail(`2026-10-22's re-run: the head line reads ${JSON.stringify(headLine(after))}`)
    // The task-merge shas, each read from the merge the branch reports for its task.
    const section = (ls, name) => { const at = ls.indexOf(`## ${name}`); const rows = []; for (let i = at + 4; i < ls.length && ls[i].startsWith('|'); i++) rows.push(ls[i]); return rows }
    const rowsOf = (ls, name) => section(ls, name).map((l) => l.split('|').map((c) => c.trim())).map((c) => [c[1], (/`([0-9a-f]+)`/.exec(c[3]) || [])[1]])
    const lastMerge = new Map(MERGES_287.map((m) => [/^Merge (?:part of )?task ([^\s:]+):/.exec(m.subject)[1], m]))
    for (const [name, partial] of [['Integrated', false], ['Partial', true]]) {
      for (const [task, short] of rowsOf(after, name)) {
        const m = lastMerge.get(task)
        if (!m || !m.sha.startsWith(short) || /^Merge part of/.test(m.subject) !== partial) fail(`2026-10-22's re-run: the ${name} row ${task} names ${short}, but its merge on the branch is ${m && m.sha} "${m && m.subject}"`)
      }
    }
    if (rowsOf(after, 'Integrated').length !== 14 || rowsOf(after, 'Partial').length !== 2) fail(`2026-10-22's re-run: ${rowsOf(after, 'Integrated').length} Integrated and ${rowsOf(after, 'Partial').length} Partial rows, expected 14 and 2`)
    if (rowsOf(after, 'Integrated').some(([t]) => t === '6.7') || !section(after, 'Partial').includes('| 6.7 | #62 | `340fd07` (+ `eab8420`, roadmap refresh) |')) fail(`2026-10-22's re-run: 6.7 did not move to Partial with its new merge: ${JSON.stringify(section(after, 'Partial'))}`)
    // Every line the rewrite does not own is kept, byte for byte and in order: the Outstanding and
    // Evidence lines, the Closes and Ticket lines, the Failed table, the prose.
    const owned = (ls) => { const skip = new Set([...section(ls, 'Integrated'), ...section(ls, 'Partial'), headLine(ls)]); return ls.filter((l) => !skip.has(l)) }
    if (JSON.stringify(owned(after)) !== JSON.stringify(owned(before))) fail('2026-10-22\'s re-run: a line the rewrite does not own changed')
    for (const kept of before.filter((l) => /^(Outstanding( test ids)?|Evidence):/.test(l))) if (!after.includes(kept)) fail(`2026-10-22's re-run: the line ${JSON.stringify(kept.slice(0, 60))} was not kept verbatim`)
    if (before.filter((l) => /^(Outstanding( test ids)?|Evidence):/.test(l)).length !== 3) fail('2026-10-22\'s fixture lost its Outstanding and Evidence lines, so the check above read nothing')
    // The hand-over names every line that changed, as it read before and after.
    const ho = r.result.handover.text
    const changed = before.filter((l) => !after.includes(l))
    if (changed.length < 15) fail(`2026-10-22's re-run: only ${changed.length} lines changed, so the hand-over check reads too little`)
    for (const l of [...changed, ...after.filter((x) => !before.includes(x))]) if (!ho.includes(l.replace(/`/g, "'"))) fail(`2026-10-22's re-run: the hand-over does not name the rewritten line ${JSON.stringify(l)}`)
    if (!ho.includes(`### Pull request description\n\n- ${cite(400)}'s description named ed8d0633e9c1360fb7c2e425f63237676f4db648, not ${NOW}; rewritten`)) fail(`2026-10-22's re-run: the hand-over's description section reads ${JSON.stringify((/### Pull request description\n\n[^\n]*/.exec(ho) || [''])[0])}`)
    if (r.result.refusals.some((x) => /description/.test(x))) fail(`2026-10-22's re-run: refused ${JSON.stringify(r.result.refusals)}`)

    // Heads that match leave the description untouched: the description as it was rewritten by hand,
    // naming 76704c5 short, dispatches no writer.
    const BODY_287_NOW = BODY_287_FIRST.replace(/head `ed8d0633e9c1360fb7c2e425f63237676f4db648`\. 15 integrated, 1 partial/, 'head `76704c5`. 14 integrated, 2 partial')
    if (BODY_287_NOW === BODY_287_FIRST) fail('the matching-head fixture is the stale one')
    const same = await execute(newRepo(), { date: DATE }, { ...reran, verify: { ...reran.verify, prBody: BODY_287_NOW } })
    if (same.labels.includes('pr-refresh') || (same.result.refresh || {}).state !== 'untouched' || !/description names this head \(76704c5\); untouched/.test(same.result.handover.text)) fail(`heads that match: dispatched ${same.labels.join(', ')}, refresh ${JSON.stringify((same.result.refresh || {}).state)}`)
    // A pull request this run opened has no description to compare.
    const fresh = await execute(newRepo(), { date: DATE }, { verify: { merges: MERGES_287 } })
    if (fresh.labels.includes('pr-refresh') || (fresh.result.refresh || {}).state !== 'none') fail(`a pull request opened this run: dispatched ${fresh.labels.join(', ')}, refresh ${(fresh.result.refresh || {}).state}`)
    // Even when Verify hands back a description, one this run opened is never compared or rewritten.
    const opened = await execute(newRepo(), { date: DATE }, { ...reran, verify: { ...reran.verify, prReused: false } })
    if (opened.labels.includes('pr-refresh') || (opened.result.refresh || {}).state !== 'none') fail(`a pull request opened this run, returned with a description: dispatched ${opened.labels.join(', ')}, refresh ${(opened.result.refresh || {}).state}`)

    // A Closes line the rewrite keeps for a row now partial is named, never rewritten and never silent.
    const closing = BODY_287_FIRST.replace('Ticket: #62', 'Closes #62')
    const cl = await execute(newRepo(), { date: DATE }, { ...reran, verify: { ...reran.verify, prBody: closing } })
    if (!String((cl.result.refresh || {}).body).split('\n').includes('Closes #62')) fail('a Closes line for a row now partial was not kept verbatim')
    if (!cl.result.refusals.some((x) => /still carries "Closes #62" for 6\.7/.test(x))) fail(`a Closes line for a row now partial is not named: ${JSON.stringify(cl.result.refusals)}`)

    // Fails closed: a description with no head in the drafted form, or a row whose task has no merge on
    // the branch, is refused whole and nothing is written; a writer that did not edit is named.
    const headless = await execute(newRepo(), { date: DATE }, { ...reran, verify: { ...reran.verify, prBody: BODY_287_FIRST.replace(/, head `[0-9a-f]+`/, '') } })
    if (headless.labels.includes('pr-refresh') || !headless.result.refusals.some((x) => /names no head/.test(x))) fail(`a description naming no head: dispatched ${headless.labels.join(', ')}, refusals ${JSON.stringify(headless.result.refusals)}`)
    const stray = await execute(newRepo(), { date: DATE }, { ...reran, verify: { ...reran.verify, merges: MERGES_287.filter((m) => !/issue-198:/.test(m.subject)) } })
    if (stray.labels.includes('pr-refresh') || !stray.result.refusals.some((x) => /row for issue-198 names no task merge on the branch/.test(x))) fail(`a row with no merge on the branch: dispatched ${stray.labels.join(', ')}, refusals ${JSON.stringify(stray.result.refusals)}`)
    const blankRead = await execute(newRepo(), { date: DATE }, { ...reran, verify: { ...reran.verify, prBody: '' } })
    if (blankRead.labels.includes('pr-refresh') || !blankRead.result.refusals.some((x) => x.includes(`reused ${cite(400)} but returned no description`))) fail(`a reused pull request read with no description: dispatched ${blankRead.labels.join(', ')}, refusals ${JSON.stringify(blankRead.result.refusals)}`)
    const unwritten = await execute(newRepo(), { date: DATE }, { ...reran, refresh: { edited: false, reason: 'headRefOid is 1111111' } })
    if (!unwritten.result.refusals.some((x) => /was not rewritten: headRefOid is 1111111/.test(x)) || !/not rewritten \(headRefOid is 1111111\)/.test(unwritten.result.handover.text)) fail(`a writer that did not edit: refusals ${JSON.stringify(unwritten.result.refusals)}`)
    // The Verify prompt reads the open description and the first-parent merges, and edits neither.
    const vp = r.prompt('verify')
    if (!vp.includes('log --first-parent --merges --reverse') || !vp.includes('gh pr view <number> --json body --jq .body')) fail('the Verify prompt does not read the open description and the first-parent merges')
  }

  // 21. Both numbers on a question (#301). The overnight report now opens each question with its night-wide number
  //     and then its ticket-local one, "**Q2.** (#310 Q2)", and one its source does not number (a proposed steering
  //     change) with the night-wide number alone. The same comments in the old form and the new yield the same
  //     question numbers, recommendations and answers, and a ticket-local "#310 Q1" cited in a question or a
  //     recommendation opens no question of its own. The new form's marks are built from the two examples
  //     overnight.js's report prompt gives, read from that file, so a change to the markup the report is told to
  //     write is read here too (CODING_STANDARDS.md, "A guard reads the thing it guards").
  checks++
  {
    const overnightSrc = fs.readFileSync(path.join(here, 'overnight.js'), 'utf8')
    const both = /, as in "(\*\*Q\d+\.\*\*[^"]+)"/.exec(overnightSrc)
    const alone = /opens with the night-wide number alone, as in "([^"]+)"/.exec(overnightSrc)
    if (!both || !alone) fail(`both numbers: overnight.js's report prompt gives no ${!both ? 'two-number' : 'night-wide-only'} example to build the new form from`)
    // Each example's digits, in order, become this fixture's night-wide number, issue and ticket-local number.
    const mark = (ex, ...nums) => { let k = 0; return ex.replace(/\d+/g, () => String(nums[k++])) }
    const BOTH = (q, local) => (both ? mark(both[1], q, 310, local) : `**Q${q}.**`)
    const ALONE = (q) => (alone ? mark(alone[1], q) : `**Q${q}.**`)
    const ticket = (n, qs) => comment(n, `## Questions: ${cite(310)}, issue-310, Both numbers\n\n${qs}`, '2026-10-21T06:00:00Z')
    const OLD = ticket(50, `**Q1.** Add the term Morning branch?\nRecommended: Yes.\n${PRE}\n\n**Q2.** Rewrite the landing sentence, as #310 Q1 assumes?\nRecommended: Yes, after ${cite(310)} Q1.\n${PRE}\n\n**Q3.** Commit this guide sentence?\nRecommended: Yes.\nAnswer: open\n`)
    const NEW = ticket(50, `${BOTH(1, 1)} Add the term Morning branch?\nRecommended: Yes.\n${PRE}\n\n${BOTH(2, 2)} Rewrite the landing sentence, as #310 Q1 assumes?\nRecommended: Yes, after ${cite(310)} Q1.\n${PRE}\n\n${ALONE(3)} Commit this guide sentence?\nRecommended: Yes.\nAnswer: open\n`)
    if (both && !BOTH(2, 2).includes('310')) fail(`both numbers: the example ${both[1]} yielded ${BOTH(2, 2)}, which carries no ticket number`)
    const confirm = comment(51, 'Confirm Q1 to Q3, with the overrides below.\n\nQ2: no, keep the sentence\n', '2026-10-21T08:00:00Z')
    const old = await execute(newRepo(), { date: DATE }, { issue: issueWith([OLD, confirm]) })
    const now = await execute(newRepo(), { date: DATE }, { issue: issueWith([NEW, confirm]) })
    const sent = (r) => (jsonAfter(r.prompt('classify'), 'QUESTIONS') || []).map((q) => ({ q: q.q, recommendation: q.recommendation, answer: q.answer }))
    if (old.result.refusals.length || now.result.refusals.length) fail(`both numbers: refusals old ${JSON.stringify(old.result.refusals)}, new ${JSON.stringify(now.result.refusals)}`)
    if (Object.keys(now.result.apply.answers).join() !== '1,2,3') fail(`both numbers: the new form yielded questions ${Object.keys(now.result.apply.answers)}, expected 1,2,3`)
    if (JSON.stringify(now.result.apply.answers) !== JSON.stringify(old.result.apply.answers)) fail(`both numbers: answers differ, old ${JSON.stringify(old.result.apply.answers)}, new ${JSON.stringify(now.result.apply.answers)}`)
    if (!sent(old).length) fail('both numbers: the old form sent the classifier nothing, so the comparison read nothing')
    if (JSON.stringify(sent(now)) !== JSON.stringify(sent(old))) fail(`both numbers: the classifier was sent ${JSON.stringify(sent(now))} from the new form, ${JSON.stringify(sent(old))} from the old`)
  }

  // 22. A draft Confirm is sorted the way the apply step would sort it, before it is posted (#302).
  //     Launched with confirmFile on an issue with no Confirm comment, the morning takes the file's
  //     text as the Confirm, sorts the issue's questions through the apply step's own code, prints the
  //     result and stops: one read agent, no classifier, applier or verify agent, nothing written. The
  //     fixture is 2026-10-22's shape: 263 questions, a draft of 21 override lines, two opening "No".
  checks++
  {
    const TOTAL = 263
    const perTicket = 53
    const big = []
    for (let t = 0; t * perTicket < TOTAL; t++) {
      const qs = []
      for (let q = t * perTicket + 1; q <= Math.min(TOTAL, (t + 1) * perTicket); q++) qs.push(`**Q${q}.** Do thing ${q}?\nRecommended: Yes.\n${PRE}\n`)
      big.push(comment(60 + t, `## Questions: #${320 + t}, issue-${320 + t}, Ticket ${t}\n\n${qs.join('\n')}`, `2026-10-22T06:0${t}:00Z`))
    }
    const NO19 = 'No, the follow-through this line states is a separate ticket and it is filed by hand later'
    const NO20 = 'no'
    const LINES = [...Array.from({ length: 18 }, (_, i) => `Q${i + 1}: yes, as recommended`), `Q19: ${NO19}`, `Q20: ${NO20}`, 'Q94: open']
    const BARE = `Confirm\n\n${LINES.join('\n')}\n`
    const HEADED = `Confirm Q1 to Q263, with the overrides below.\n\n${LINES.join('\n')}\n`
    const FILE = '/tmp/morning-tests-draft/confirm-286.md'
    const draftRun = (draft, extra = {}, source = null) => execute(newRepo(), { date: DATE, confirmFile: FILE }, { issue: issueWith(big), draft, ...extra }, source)

    // The bare Confirm: its first sentence covers none, its own lines confirm 21.
    const bare = await draftRun(BARE)
    const b = bare.result
    if (bare.labels.join() !== 'draft-read') fail(`draft check: dispatched ${bare.labels.join(', ')}, expected the one read agent`)
    if (b.mode !== 'check' || b.total !== TOTAL || b.covered !== 0 || b.overrides !== 21 || b.confirmed !== 21 || b.byOwnLines !== 21) fail(`draft check, bare Confirm: ${JSON.stringify({ mode: b.mode, total: b.total, covered: b.covered, overrides: b.overrides, confirmed: b.confirmed, byOwnLines: b.byOwnLines })}`)
    if (!/first sentence covers 0 of 263/.test(b.text || '') || !/21 confirmed by their own Q<n>: lines/.test(b.text || '')) fail(`draft check, bare Confirm: the printed result does not say 0 of 263 and 21 by their own lines:\n${b.text}`)
    if (b.applied !== 18 || (b.no || []).length !== 2 || (b.open || []).length !== TOTAL - 20) fail(`draft check, bare Confirm: applied ${b.applied}, no ${b.no && b.no.length}, open ${b.open && b.open.length}; expected 18, 2, ${TOTAL - 20}`)
    if (!b.ok) fail(`draft check, bare Confirm: failed though every number is found: ${JSON.stringify(b.unknown)}`)

    // The headed Confirm: all 263 confirmed; the two No lines and Q94 sorted with their first sixty characters.
    const headed = await draftRun(HEADED)
    const h = headed.result
    if (h.covered !== TOTAL || h.confirmed !== TOTAL || h.overrides !== 21 || !/263 confirmed/.test(h.text || '')) fail(`draft check, headed Confirm: covered ${h.covered}, confirmed ${h.confirmed}, overrides ${h.overrides}`)
    if (h.applied !== TOTAL - 3) fail(`draft check, headed Confirm: applied ${h.applied}, expected ${TOTAL - 3}`)
    if (JSON.stringify(h.no) !== JSON.stringify([{ q: 19, answer: NO19.slice(0, 60) }, { q: 20, answer: NO20 }])) fail(`draft check: answered no lists ${JSON.stringify(h.no)}`)
    if (JSON.stringify((h.open || []).map((o) => [o.q, o.answer])) !== JSON.stringify([[94, 'open']])) fail(`draft check: left open lists ${JSON.stringify(h.open)}`)
    if (!String(h.text).includes(`- Q19: ${NO19.slice(0, 60)}`) || !h.text.includes('- Q94: open') || NO19.length <= 60) fail(`draft check: the printed result does not list Q19's first sixty characters and Q94:\n${h.text}`)
    if (!h.ok || (h.unknown || []).length) fail(`draft check: a leading No failed the check: ${JSON.stringify(h.unknown)}`)

    // A number no questions comment yields fails the check, by name.
    const lost = await draftRun(HEADED.replace('Q94: open', 'Q94: open\nQ300: yes'))
    if (lost.result.ok || JSON.stringify(lost.result.unknown) !== '[300]' || !/Q300/.test(lost.result.text || '') || !/^check: fail/m.test(lost.result.text || '')) fail(`draft check: Q300 did not fail the check by name: ok ${lost.result.ok}, unknown ${JSON.stringify(lost.result.unknown)}`)
    const ranged = await draftRun('Confirm Q1 to Q264.\n')
    if (ranged.result.ok || JSON.stringify(ranged.result.unknown) !== '[264]') fail(`draft check: a first sentence naming Q264 did not fail by name: ${JSON.stringify(ranged.result.unknown)}`)

    // It runs one read agent and writes nothing: no command it is given posts, edits, files, closes,
    // commits or pushes, and it names no OUTPUT or PHASE FILE.
    const rp = bare.prompt('draft-read')
    const spans = [...rp.matchAll(/`([^`\n]+)`/g)].map((m) => m[1])
    if (!spans.some((x) => /gh issue view\b/.test(x))) fail('draft check: the read prompt runs no gh issue view, so the search below read nothing')
    const WRITES = /gh (issue|pr) (comment|edit|create|close|reopen)\b|gh api\b[^`]*-X|\bgit\b[^`]*\b(commit|push|worktree add)\b|\bmv\b|\brm\b/
    for (const x of spans) if (WRITES.test(x)) fail(`draft check: the read prompt asks for a write: ${x}`)
    if (/^(OUTPUT|PHASE) FILE:/m.test(rp)) fail('draft check: the read prompt names a file to write')
    if (!rp.includes(FILE)) fail('draft check: the read prompt does not name the draft file')

    // An issue that already has a Confirm comment, a draft the agent could not read, and a bad launch are refused.
    const has = await execute(newRepo(), { date: DATE, confirmFile: FILE }, { draft: BARE })
    if (!has.result.halted || !/already has a Confirm comment/.test(has.result.halted) || has.labels.join() !== 'draft-read') fail(`draft check over a confirmed issue: ${JSON.stringify(has.result.halted)}`)
    const unreadable = await draftRun('', { draftError: 'no such file' })
    if (!unreadable.result.halted || !/no such file/.test(unreadable.result.halted)) fail(`draft check, unreadable draft: ${JSON.stringify(unreadable.result.halted)}`)
    for (const [label, a] of [['a relative confirmFile', { date: DATE, confirmFile: 'draft.md' }], ['confirmFile with after', { date: DATE, confirmFile: FILE, after: true }]]) {
      const r = await execute(newRepo(), a)
      if (!r.result || !r.result.halted || r.calls.length) fail(`draft check, ${label}: ${r.calls.length} agent(s) ran; returned ${JSON.stringify(r.result)}`)
    }
    // wt is optional beside confirmFile, since the draft check makes and reads no worktree; without
    // confirmFile a missing wt is still refused. stub supplies wt, so each case sets it undefined.
    const noWt = await execute(newRepo(), { date: DATE, confirmFile: FILE, wt: undefined }, { issue: issueWith(big), draft: HEADED })
    if (noWt.result.halted || noWt.result.mode !== 'check' || !noWt.result.ok || noWt.labels.join() !== 'draft-read') fail(`draft check without wt: ${JSON.stringify(noWt.result.halted || { mode: noWt.result.mode, ok: noWt.result.ok, labels: noWt.labels })}`)
    const noWtMorning = await execute(newRepo(), { date: DATE, wt: undefined })
    if (!noWtMorning.result || !/wt: /.test(noWtMorning.result.halted || '') || noWtMorning.calls.length) fail(`a morning without wt or confirmFile was not refused: ${JSON.stringify(noWtMorning.result)}`)

    // The sort is the apply step's own code (#302): the first-word rule mutated in the script turns
    // both the apply case (Q2 overridden "no, ...") and the check case (Q19 opening "No") red.
    const applyCase = (r) => (r.result.apply && r.result.apply.no.includes(2) && !(jsonAfter(r.prompt('classify'), 'QUESTIONS') || []).some((q) => q.q === 2) ? [] : ['apply: Q2 answered no was not held back'])
    const checkCase = (r) => (r.result.no && r.result.no.some((n) => n.q === 19) ? [] : ['check: Q19 opening No is not listed under answered no'])
    const real = fs.readFileSync(scriptPath, 'utf8')
    const rule = (/^const verdictOf = .*$/m.exec(real) || [''])[0]
    const mutant = real.replace(rule, rule.replace('/^no\\b/i', '/^nope\\b/i'))
    if (!rule || mutant === real) fail('draft check: the first-word rule (const verdictOf, /^no\\b/i) was not found, so the mutation changed nothing')
    const realRed = [...applyCase(await execute(newRepo(), { date: DATE })), ...checkCase(await draftRun(HEADED))]
    if (realRed.length) fail(`draft check: the cases fail on the real script: ${realRed.join('; ')}`)
    const applyRed = applyCase(await execute(newRepo(), { date: DATE }, {}, mutant))
    const checkRed = checkCase(await draftRun(HEADED, {}, mutant))
    if (!applyRed.length || !checkRed.length) fail(`draft check: the first-word mutation turned ${!applyRed.length ? 'the apply case' : 'the check case'} not red, so the two do not share the sort`)
  }

  // 16. Every agent call passes a schema and every stub's return satisfies it.
  checks++
  for (const c of allPrompts) if (!c.schema || c.schema.type !== 'object') fail(`${c.label}: called without a schema`)
  if (driftAll.length) fail(`returns not matching the script's schemas: ${driftAll.slice(0, 5).join(' | ')}`)
  // The edit rule (#109, Q164 of #286): every prompt that carries the house rules carries the amended
  // line verbatim, Edit and Write both, and no prompt still carries the Edit-only line it replaced.
  checks++
  {
    const EDIT_RULE = '- To change an existing file with Edit or Write, open it with the Read tool first in this session, not with `cat`, `head` or `sed -n`: for a file you will change, this rule overrides the auto-mode advice to read with Bash. Open it with Read again after anything else wrote it (a script, the check, a merge, `git`). Otherwise make the change with a short script written to a file under your scratchpad and run by its path. Edit and Write refuse a file not read since its last write: 16 calls went to that refusal on 2026-10-18 and 16 over the four launches from 2026-10-19 to 2026-10-21 (#109).'
    const housed = allPrompts.filter((c) => /(House|Read-only) rules \(/.test(c.prompt))
    if (!housed.length) fail('no prompt carries the house rules, so the edit-rule search read nothing')
    for (const c of housed) if (!c.prompt.includes(EDIT_RULE)) fail(`the ${c.label} prompt's house rules lack the edit rule of #109`)
    for (const c of allPrompts) if (/Change a file with Edit only after/.test(c.prompt)) fail(`the ${c.label} prompt still carries the Edit-only line #109 replaced`)
  }
} catch (e) {
  console.error(`morning tests: the script did not run: ${e && e.stack ? e.stack : e}`)
  process.exit(2)
}

if (failures.length) {
  console.log(`morning tests: FAILED, ${failures.length} failure(s) in ${checks} checks, on ${path.basename(scriptPath)}:`)
  failures.forEach((f) => console.log(`  - ${f}`))
  process.exit(1)
}
console.log(`morning tests: pass: ${checks} checks, on ${path.basename(scriptPath)}`)
