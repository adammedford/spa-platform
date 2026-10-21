export const meta = {
  name: 'sequence',
  description: 'Run the 2026-10-21 workflow-work sequence: per-ticket worktrees, same-file tickets in order, other files in parallel, onto sequence/<date>',
  whenToUse: 'One-off orchestration of the workflow-work tickets (#137 #231 #202 #203 #136 #228 #248 #227 #247 #213 #183 #200 #230 #229, review-queue #83-#86 and the split spec builds). Relaunchable: each launch runs what its gates allow.',
  phases: [
    { title: 'Collect', detail: 'facts per planned issue, discovered spec builds and #213 follow-ups; sequence/<date> worktree' },
    { title: 'Implement', detail: 'one worktree per ticket; tickets sharing a file run in table order' },
    { title: 'Review', detail: 'code-review against the fork point, mutations re-run' },
    { title: 'Integrate', detail: 'serialized merge into sequence/<date>, the check, worktree removed on green' },
    { title: 'Grill', detail: 'ungrilled ready-for-human planned tickets, on paper' },
    { title: 'Land prep', detail: 'push sequence/<date>, verify cited shas and partial lines, open the landing PR' },
    { title: 'Report', detail: 'one issue, Sequence <date>' },
  ],
}

// ---------------------------------------------------------------- what this is
// A one-off driver for the workflow-work sequence planned on 2026-10-21, a sibling of overnight.js whose house rules
// and prompts it copies (from overnight.js at e0bf0eb). It is orchestration nothing downstream consumes, so by Adam's
// decision of 2026-10-21 it is launched from the branch that carries it, without a landing first (ADR-0026 decision 3
// waived for this script), it may push its own run branch and open the landing PR, and partial tickets are allowed.
// What CI and the landing enforce stays: each ticket's tools/check.sh --full, Authority: trailers on steering edits,
// per-commit ownership, and a review record before tools/land.sh merges into main. Nothing here merges into main.
//
// Launch (Workflow tool), from a worktree of the branch that carries this file:
//   scriptPath: <that worktree>/.claude/workflows/sequence.js
//   args: { "repo": "/Users/adammedford/Projects/spa-platform", "date": "2026-10-22",
//           "wt": "/Users/adammedford/.worktrees/sequence-2026-10-22", "standingAnswer": true }
//   repo            required  the main checkout, absolute; must be clean and at origin/main (b313c14 or later).
//   date            required  YYYY-MM-DD; names sequence/<date> and the "Sequence <date>" issue. A relaunch after a
//                             landing takes a new date; a resume passes the same arguments again.
//   wt              required  worktree root, absolute, outside repo: <wt>/sequence integrates, <wt>/<id> per ticket.
//   standingAnswer  required  boolean; true lets a lane take its own recommendation on a human-owned decision.
//   base            optional  40-hex sha of origin/main; default the main checkout's HEAD, checked against origin.
//   maxParallel     optional  implement/review slots, default 4.   maxFailures  optional, default 6.
//   only, skip      optional  arrays of ticket ids (issue-<n>).
// Never run it alongside an overnight launch: both claim from the same tracker.
const A = args || {}
const badArguments = (a) => {
  const bad = []
  const abs = (p) => typeof p === 'string' && p.startsWith('/') && p.length > 1
  const ids = (v) => Array.isArray(v) && v.every((x) => typeof x === 'string' && /^issue-\d+$/.test(x))
  const count = (v) => Number.isInteger(v) && v > 0
  if (!abs(a.repo)) bad.push('repo: the main checkout as an absolute path')
  if (typeof a.date !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(a.date)) bad.push('date: YYYY-MM-DD')
  if (!abs(a.wt)) bad.push('wt: the worktree root as an absolute path outside repo')
  else if (abs(a.repo) && (a.wt === a.repo || a.wt.startsWith(a.repo.replace(/\/+$/, '') + '/'))) bad.push('wt: must lie outside repo')
  if (typeof a.standingAnswer !== 'boolean') bad.push('standingAnswer: true or false, a boolean')
  if (a.base !== undefined && !/^[0-9a-f]{40}$/.test(String(a.base))) bad.push('base: the full 40-hex sha of origin/main')
  if (a.maxParallel !== undefined && !count(a.maxParallel)) bad.push('maxParallel: a positive integer')
  if (a.maxFailures !== undefined && !count(a.maxFailures)) bad.push('maxFailures: a positive integer')
  if (a.only !== undefined && a.only !== null && !ids(a.only)) bad.push('only: an array of ids like "issue-230"')
  if (a.skip !== undefined && !ids(a.skip)) bad.push('skip: an array of ids like "issue-230"')
  return bad
}
const BAD_LAUNCH = badArguments(A)
if (BAD_LAUNCH.length) return { halted: 'bad launch; no agent started:\n- ' + BAD_LAUNCH.join('\n- ') }

const REPO = A.repo
let BASE = A.base || ''
const DATE = A.date
const WTROOT = A.wt
const MAX_PAR = A.maxParallel || 4
const MAX_FAIL = A.maxFailures || 6
const ONLY = A.only || null
const SKIP = new Set(A.skip || [])
const STANDING = A.standingAnswer === true
const GH = 'adammedford/spa-platform'
const SEQ = `${REPO}/.scratch/sequence`
const LOG = `${SEQ}/log`                  // git-ignored (.scratch/*/log/)
const PENDING = `${LOG}/pending-${DATE}.jsonl`
const INT_BRANCH = `sequence/${DATE}`
const INT_WT = `${WTROOT}/sequence`
const WT = (id) => `${WTROOT}/${id}`
const PORT = (n) => 6100 + 10 * n        // overnight uses 5100 + 10n
const TRAILER = 'Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>'
const SKILLS = '/Users/adammedford/.agents/skills'
const skill = (name) => `call the Skill tool with "${name}" (if the Skill tool is unavailable or refuses, read ${SKILLS}/${name}/SKILL.md and follow it)`

// Confirmed answers the steering edits cite as Authority:.
const URL_ = (n, c) => `https://github.com/${GH}/issues/${n}#issuecomment-${c}`
const CONFIRM_226 = URL_(226, '6319841927')   // #200 Q15-Q25, #213 Q26-Q44, #203 Q45-Q57
const CONFIRM_185 = URL_(185, '6305259207')   // #183 Q1-Q8
const CONFIRM_227 = URL_(227, '6323543713')   // #227 rules 1-5
const CONFIRM_229 = URL_(229, '6323544258')   // #229 wording
const CONFIRM_232 = URL_(232, '6323542689')   // #232 Q9 (in scope), Q11, Q14, Q15

// ---------------------------------------------------------------- the plan
// Order inside a group is table order; tickets in different groups run in parallel. A group is a set of files two
// tickets would both edit. needs: ids that must integrate in this run (or be closed) first; every open native blocker
// is added by resolvePlan(). Review-queue's proofs (#87, #255-#258) and every spec's proof ticket are the human's.
const PLAN = [
  { id: 'issue-213', issue: 213, slug: 'track-companion-and-skills', groups: ['ci', 'gitignore', 'adr', 'glossary', 'skill:wiz', 'wf:companion'],
    wf: true, notes: `The branch task/issue-213-track-companion-and-skills already exists on origin: Adam's unblock.sh pushed the live-bytes first commit (Q42). Continue on it; never rewrite that commit. Authority for the ADR (it amends ADR-0026 decision 1, Q43 override), the glossary entry *Project skill* (Q44) and every other steering edit: ${CONFIRM_226}. The three wiz corrections are separate commits (Q40). Run the companion 20 times in a loop and keep the output in ${LOG}/issue-213-loop.txt (Q30); show it exiting 1 on a throwaway copy with the ready-first pick reverted (Q31).` },
  { id: 'issue-183', issue: 183, slug: 'restart-rule', groups: ['doc:landing', 'glossary'],
    notes: `Confirmed on #185 (Q1-Q8): ${CONFIRM_185}. Boxes 1 and 4 are struck (Q4, Q5), box 2 is the kept project entry (Q3), box 3 is Q6's sentence in landing-work.md, plus Q7's glossary term *Claude Code process*. Authority: that URL. The landing's Closes line closes it (Q8).` },
  { id: 'issue-200', issue: 200, slug: 'guard-files-and-index', groups: ['doc:landing', 'glossary', 'tool:guard'], needs: ['issue-183'],
    notes: `Decided by #226 Q15-Q25: ${CONFIRM_226}; that URL is the Authority for the landing-work.md and GLOSSARY.md edits (Q23).` },
  { id: 'issue-230', issue: 230, slug: 'check-logs-shell-umask', groups: ['tool:check'], notes: 'tools/check.sh changed in PR #250 (#205, #210); build on main as it is now.' },
  { id: 'issue-229', issue: 229, slug: 'fail-closed-standard', groups: ['doc:standards'], notes: `Authority: ${CONFIRM_229}.` },
  { id: 'issue-137', issue: 137, slug: 'drop-foreground-rule', groups: ['wf:overnight', 'tool:collect'], wf: true,
    notes: `Drop the **Session:** foreground rule and the collect prompt's stale reason from overnight.js and tools/overnight_collect.py (#135 Q12); remove from FOREGROUND_ONLY every spec that now has sub-issues (#232 Q14: ${CONFIRM_232}).` },
  { id: 'issue-231', issue: 231, slug: 'merge-message-file', groups: ['wf:overnight'], needs: ['issue-213'], wf: true,
    notes: 'The merge-message file goes under the run log, not the session scratchpad (#202 box 2 wins over #231\'s prose).' },
  { id: 'issue-202', issue: 202, slug: 'review-reruns-mutations', groups: ['wf:overnight'], needs: ['issue-213'], wf: true },
  { id: 'issue-203', issue: 203, slug: 'quote-whole-paragraphs', groups: ['wf:overnight', 'doc:tracker'], wf: true,
    notes: 'Authority: #203 (Adam approved its body). Change nothing but the wrapping; if a quoted paragraph changed on main since the approval, stop and report (Q52).' },
  { id: 'issue-136', issue: 136, slug: 'worktree-per-ticket', groups: ['wf:overnight'], needs: ['issue-213'], wf: true,
    notes: 'This script (.claude/workflows/sequence.js on the launch branch) already runs one worktree per ticket; reuse its approach. The first night with it is #245, not a box here.' },
  { id: 'issue-227', issue: 227, slug: 'ticket-shape-rules', groups: ['doc:tracker'], notes: `Authority: ${CONFIRM_227}. Quote paragraphs whole (#203's rule).` },
  { id: 'issue-228', issue: 228, slug: 'collect-flags-partial', groups: ['wf:overnight', 'tool:collect'], wf: true, notes: `Patterns follow #227's confirmed rules: ${CONFIRM_227}.` },
  { id: 'issue-247', issue: 247, slug: 'ticket-shape-check', groups: ['doc:tracker', 'tool:shape'], needs: ['issue-227'], notes: `Authority for the tracker-guide edit: ${CONFIRM_227}.` },
  { id: 'issue-248', issue: 248, slug: 'import-ticket-shape', groups: ['wf:overnight', 'tool:collect', 'tool:shape'], needs: ['issue-228', 'issue-247'], wf: true,
    notes: 'overnight.js has no retro prompt; if box 3 cannot be met as written, land the rest and say so in Outstanding.' },
  { id: 'issue-83', issue: 83, slug: 'review-queue-skeleton', groups: ['wf:review-queue'], wf: true },
  { id: 'issue-84', issue: 84, slug: 'review-queue-mutate', groups: ['wf:review-queue'], needs: ['issue-83'], wf: true },
  { id: 'issue-85', issue: 85, slug: 'review-queue-delta', groups: ['wf:review-queue'], needs: ['issue-84'], wf: true },
  { id: 'issue-86', issue: 86, slug: 'review-queue-queue', groups: ['wf:review-queue'], needs: ['issue-85'], wf: true },
]
// Specs whose build sub-issues collect discovers (#232 Q14). Their tickets run one per workflow file, in parallel;
// gate-evidence follows review-queue and truth-audit when they run in the same launch.
const SPECS = [
  { issue: 77, name: 'retro' }, { issue: 78, name: 'morning' }, { issue: 81, name: 'truth-audit' },
  { issue: 82, name: 'pins-sweep' }, { issue: 80, name: 'gate-evidence', after: ['wf:review-queue', 'wf:truth-audit'] },
]
const PLANNED = PLAN.map((p) => p.issue)
// A discovered ticket shares a lock with a planned one when its Touches line names one of these files.
const SHARED = [['.github/workflows/ci.yml', 'ci'], ['.gitignore', 'gitignore'], ['GLOSSARY.md', 'glossary'],
  ['docs/agents/issue-tracker.md', 'doc:tracker'], ['tools/check.sh', 'tool:check'], ['.claude/skills/wiz/', 'skill:wiz']]
const sharedGroups = (f) => SHARED.filter(([path]) => (f.touches || []).some((t) => t.startsWith(path))).map(([, g]) => g)

// ---------------------------------------------------------------- shared rules
const HOUSE = `House rules (unattended; nobody will answer questions during the run):
- The main checkout ${REPO} is Adam's. Never commit, checkout, switch, stash, reset or build there. The only files you may write there are under ${LOG}/. Every other file you write goes in your own worktree; the launching session's scratchpad is off limits.
- Work under \`umask 022\`: begin each Bash call that writes files with \`umask 022 &&\`. The check and the pre-push hook refuse a tracked file only its owner can read (#205).
- The tracker is GitHub Issues. Run every \`gh\` command with \`cd ${REPO} &&\` in front. A multi-line comment or body goes through a file: write it under ${LOG}/, then \`--body-file <that file>\`; never an unquoted heredoc (one ran backtick spans as commands and damaged #213).
- If \`gh\` fails because GitHub is unreachable, append the write as one JSON line to ${PENDING} ({"issue": <n>, "action": "comment"|"edit"|"close", "body": "..."}) and continue.
- Never write a comment that begins "Confirm" or "Approved": those are Adam's. Close an issue only in a step that says to.
- Each ticket's task merge on ${INT_BRANCH} is that ticket's merge body. Cite work by that merge's sha, never by a task branch: task branches are not pushed.
- Begin every Bash command that touches your worktree with \`cd <worktree> &&\`, and use \`git -C <path>\` for every git command. To delete a file under tests/, run \`cd <worktree> && git rm tests/<path>\` as ONE Bash call. A bare \`git commit\` lands on Adam's branch.
- Change a file with Edit only after opening it with Read in this session, and open it again after anything else wrote it; otherwise make the change with a short script.
- Worktrees live under ${WTROOT}. Never create one under the repo.
- AGENTS.md's rules hold: owner paths only (OWNERSHIP.md); never log a secret, token, cookie, code or state value.
- A steering-document change (GLOSSARY.md, AGENTS.md, CLAUDE.md, OWNERSHIP.md, CODING_STANDARDS.md, docs/agents/, docs/adr/, contracts/) is committed only with the Authority: trailer your ticket's notes name, on a line of its own in the commit message's last paragraph. A change your notes do not authorise goes under "## Proposed steering changes" in your Answer instead. ADRs about this orchestration layer may be committed when the notes say so.
- Never run \`dotnet build-server shutdown\`, \`dotnet nuget locals --clear\`, or anything that deletes outside your worktree. Bind only your own port, and kill any process you start before you return.
- Commit messages end with the attribution line "${TRAILER}". Never push, unless your step says to.`

const GATE = ({ dir, log, kind, fork, port }) => `THE CHECK (${kind}). Run it as ONE Bash call with a 600000 ms timeout, output to ${log}:
  cd ${dir} && { echo "HEAD=$(git -C ${dir} rev-parse HEAD)"; tools/check.sh --full --base ${fork} --port ${port}; } > ${log} 2>&1; tail -25 ${log}
Green means the last line of ${log} starts "check: pass:". Run it exactly as written; if the check itself fails to run, report that, do not repair it. To learn what it does, read its header only (\`sed -n 1,60p tools/check.sh\`).`

// tools/check.sh never parses workflow scripts, so a ticket that edits one proves it parses and still schedules.
const WFPROOF = ({ dir, log }) => `THE WORKFLOW PROOF (this ticket edits .claude/workflows/). Run as ONE Bash call, output to its own log so the check's verdict stays the check log's last line:
  cd ${dir} && { python3 -I .github/workflow_parse_tests.py && python3 -I .github/workflow-parse.py && { [ ! -f .claude/workflows/overnight_tests.mjs ] || node .claude/workflows/overnight_tests.mjs; }; echo "wfproof: exit $?"; } > ${log} 2>&1; tail -15 ${log}
Green means the last line reads "wfproof: exit 0".`

const TICK = (t, verb) => `To ${verb} acceptance boxes, edit the issue body and nothing else in it: \`cd ${REPO} && gh issue view ${t.issue} --json body --jq .body > ${LOG}/${t.id}-body.md\`, change only the \`- [ ]\` / \`- [x]\` markers of the lines concerned, then \`cd ${REPO} && gh issue edit ${t.issue} --body-file ${LOG}/${t.id}-body.md\`.`

// ---------------------------------------------------------------- schemas
const FACT = {
  type: 'object',
  properties: {
    issue: { type: 'integer' }, title: { type: 'string' }, url: { type: 'string' },
    state: { type: 'string', enum: ['OPEN', 'CLOSED'] }, labels: { type: 'array', items: { type: 'string' } },
    assignees: { type: 'array', items: { type: 'string' } }, openBlockers: { type: 'array', items: { type: 'integer' } },
    openBoxes: { type: 'integer' }, owner: { type: 'string' }, grilled: { type: 'boolean' },
    taskBranch: { type: 'string' }, parent: { type: 'integer' }, slug: { type: 'string' },
    touches: { type: 'array', items: { type: 'string' } },
  },
  required: ['issue', 'title', 'url', 'state', 'labels', 'assignees', 'openBlockers', 'openBoxes', 'owner', 'grilled', 'taskBranch', 'parent', 'slug', 'touches'],
}
const COLLECT = {
  type: 'object',
  properties: {
    outcome: { type: 'string', enum: ['ok', 'dirty-start'] }, reason: { type: 'string' },
    base: { type: 'string' }, shell: { type: 'string' }, shellCommand: { type: 'string' },
    planned: { type: 'array', items: FACT }, discovered: { type: 'array', items: FACT }, followups: { type: 'array', items: FACT },
  },
  required: ['outcome', 'reason', 'base', 'shell', 'shellCommand', 'planned', 'discovered', 'followups'],
}
const IMPL = {
  type: 'object',
  properties: {
    outcome: { type: 'string', enum: ['resolved', 'needs-human', 'failed', 'skipped'] },
    branch: { type: 'string' }, forkSha: { type: 'string' }, headSha: { type: 'string' },
    complete: { type: 'boolean' }, outstanding: { type: 'string' }, summary: { type: 'string' }, question: { type: 'string' },
    delegatedDecisions: { type: 'array', items: { type: 'string' } }, proposedSteering: { type: 'array', items: { type: 'string' } },
  },
  required: ['outcome', 'branch', 'forkSha', 'headSha', 'complete', 'outstanding', 'summary', 'question', 'delegatedDecisions', 'proposedSteering'],
}
const REVIEW = {
  type: 'object',
  properties: {
    outcome: { type: 'string', enum: ['committed', 'needs-human', 'failed'] }, headSha: { type: 'string' },
    findingsFixed: { type: 'array', items: { type: 'string' } }, findingsSkipped: { type: 'array', items: { type: 'string' } },
    summary: { type: 'string' }, question: { type: 'string' }, proposedSteering: { type: 'array', items: { type: 'string' } },
  },
  required: ['outcome', 'headSha', 'findingsFixed', 'findingsSkipped', 'summary', 'question', 'proposedSteering'],
}
const INT = {
  type: 'object',
  properties: { ok: { type: 'boolean' }, mergeSha: { type: 'string' }, conflict: { type: 'boolean' }, files: { type: 'array', items: { type: 'string' } }, worktreeRemoved: { type: 'boolean' }, notes: { type: 'string' } },
  required: ['ok', 'mergeSha', 'conflict', 'files', 'worktreeRemoved', 'notes'],
}
const GRILL = {
  type: 'object',
  properties: {
    questions: { type: 'array', items: { type: 'object', properties: { q: { type: 'string' }, recommendation: { type: 'string' } }, required: ['q', 'recommendation'] } },
    summary: { type: 'string' },
  },
  required: ['questions', 'summary'],
}
const LANDPREP = {
  type: 'object',
  properties: {
    pushed: { type: 'boolean' }, head: { type: 'string' }, pr: { type: 'integer' }, prUrl: { type: 'string' },
    closes: { type: 'array', items: { type: 'integer' } }, tickets: { type: 'array', items: { type: 'integer' } },
    fixedLinks: { type: 'array', items: { type: 'string' } }, notes: { type: 'string' },
  },
  required: ['pushed', 'head', 'pr', 'prUrl', 'closes', 'tickets', 'fixedLinks', 'notes'],
}
const REPORT = {
  type: 'object',
  properties: { issue: { type: 'integer' }, url: { type: 'string' }, mainCheckoutClean: { type: 'boolean' }, leftovers: { type: 'string' } },
  required: ['issue', 'url', 'mainCheckoutClean', 'leftovers'],
}

// ---------------------------------------------------------------- prompts
const factRule = `For each issue n, one FACT:
- issue, title, url, state, labels (names), assignees (logins): from \`cd ${REPO} && gh issue view <n> --json number,title,url,state,labels,assignees,body,comments\`.
- openBlockers: the numbers of OPEN issues that block it: GitHub's blocked-by relationship (\`cd ${REPO} && gh api repos/${GH}/issues/<n>/dependencies/blocked_by --jq '.[] | select(.state=="open") | .number'\`), plus any \`Blocked by: #<m>\` line in the body whose #m is open. Closed blockers do not count.
- openBoxes: the number of body lines that are an unticked task-list item (a line whose first non-space text is "- [ ]"), or -1 when the body has no task-list item at all. Count it with python3, do not estimate.
- owner: the \`**Owner:**\` line's first role as OWNERSHIP.md names it (integrator, ws1-infra, ...); "integrator" when absent.
- grilled: true when a comment headed "## Questions for Adam" or "## Grilling record" exists.
- taskBranch: the name of a branch \`task/issue-<n>-*\` on origin or local (\`git -C ${REPO} ls-remote --heads origin 'refs/heads/task/issue-<n>-*'\`, \`git -C ${REPO} branch --list 'task/issue-<n>-*'\`), else "".
- parent: the parent issue's number, else 0. slug: a short kebab-case slug of the title.
- touches: the paths the body's \`**Touches:**\` line names (each backticked path, optional ones included), else [].`

const collectPrompt = `Collect the facts for the workflow-work sequence. Repo (main checkout): ${REPO}. Base: ${BASE || 'none given; SETUP 1 resolves it'}. Integration branch: ${INT_BRANCH}.
${HOUSE}

SETUP, in order:
0. The shell: run \`echo "BASH_VERSION=$BASH_VERSION"; ps -p $$ -o comm=\` as one Bash call; "shell" is the value after BASH_VERSION= ("" when empty), "shellCommand" the ps line. Return both even on "dirty-start".
1. \`git -C ${REPO} status --porcelain --untracked-files=no\` must print nothing. ${BASE ? `\`git -C ${REPO} rev-parse HEAD\` must equal ${BASE}.` : `The base is \`git -C ${REPO} rev-parse HEAD\`, and it must equal what \`git -C ${REPO} ls-remote origin refs/heads/main\` prints.`} It must also contain b313c14 (PR #250): \`git -C ${REPO} merge-base --is-ancestor b313c14 HEAD\`. Otherwise return "dirty-start" with the reason and do nothing else.
2. If ${INT_BRANCH} exists it must be the base or a descendant (\`git -C ${REPO} merge-base --is-ancestor <base> ${INT_BRANCH}\`), else "dirty-start". If it does not exist: \`git -C ${REPO} worktree add -b ${INT_BRANCH} ${INT_WT} <base>\`; if it exists but ${INT_WT} is not a worktree: \`git -C ${REPO} worktree add ${INT_WT} ${INT_BRANCH}\`.
3. \`mkdir -p ${LOG}\`, then \`git -C ${REPO} status --porcelain --untracked-files=all > ${LOG}/untracked-at-start.txt\`.

FACTS.
- planned: one FACT for each of ${PLANNED.map((n) => '#' + n).join(', ')}.
- discovered: one FACT for each sub-issue of ${SPECS.map((s) => '#' + s.issue).join(', ')} (\`cd ${REPO} && gh api repos/${GH}/issues/<spec>/sub_issues --jq '.[].number'\`), whatever its state; parent is the spec.
- followups: one FACT for each open issue whose body has a \`**Raised by:** #213\` line (\`cd ${REPO} && gh issue list --state open --limit 200 --search '"Raised by:** #213" in:body' --json number\`), excluding planned ones.
${factRule}

Write nothing else. Return outcome "ok", reason (anything odd you saw, one sentence each), base (full sha), shell, shellCommand, planned, discovered, followups.`

const implPrompt = (t) => `Implement ticket ${t.n} (${t.id}, owner ${t.owner}): issue #${t.issue}, ${t.url}. Unattended.
${HOUSE}

Worktree: ${WT(t.id)}. Branch: task/${t.id}-${t.slug}. Your port: ${PORT(t.n)}.
${t.notes ? 'NOTES FOR THIS TICKET (they carry its authority): ' + t.notes + '\n' : ''}
STEP 0 — durable state (this may be a resumed run):
- \`cd ${REPO} && gh issue view ${t.issue} --json state --jq .state\` printing CLOSED: return outcome "skipped", summary "closed before the lane started", empty strings elsewhere, complete false, and write nothing.
- If \`git -C ${REPO} merge-base --is-ancestor task/${t.id}-${t.slug} ${INT_BRANCH}\` succeeds and the branch has a commit whose subject starts "Task ${t.id}:", the work is integrated: return its facts (forkSha = the merge-base of ${INT_BRANCH} and the branch) and do nothing else.
- If the branch exists, locally or on origin (\`git -C ${REPO} fetch origin task/${t.id}-${t.slug}\` brings it), continue on it: if ${WT(t.id)} is missing, \`git -C ${REPO} worktree add ${WT(t.id)} task/${t.id}-${t.slug}\` (for a branch only on origin, \`git -C ${REPO} worktree add -b task/${t.id}-${t.slug} ${WT(t.id)} origin/task/${t.id}-${t.slug}\`); if present, \`git -C ${WT(t.id)} rev-parse --abbrev-ref HEAD\` must print the branch, else return "failed". forkSha = \`git -C ${REPO} merge-base ${INT_BRANCH} task/${t.id}-${t.slug}\`. Read the issue's comments once for what earlier work recorded.
- Otherwise fork at the integration tip: forkSha = \`git -C ${REPO} rev-parse ${INT_BRANCH}\`; ${WT(t.id)} must not exist (if it does, return "failed" naming it); \`git -C ${REPO} worktree add -b task/${t.id}-${t.slug} ${WT(t.id)} <forkSha>\`.
Claim the issue: \`cd ${REPO} && gh issue edit ${t.issue} --add-assignee @me\`.

STEP 1 — build it, following ${SKILLS}/implement/SKILL.md (read it; it is not callable) without its code-review step. The ticket is issue #${t.issue}'s body: its acceptance boxes are the spec. Read GLOSSARY.md, docs/agents/domain.md, CODING_STANDARDS.md and the files the ticket names. Build test-first: ${skill('tdd')}. Launch any sub-agent with \`run_in_background: false\` and wait for its report.
- Every mutation you run to prove a test can fail: name it (what you broke, which test went red, its count) in the task commit's body and in your Answer. A review re-runs each one; an unnamed or unreproducible claim is a finding.
- At the end, run once, fixing and re-running until green, at most three runs:
${GATE({ dir: WT(t.id), log: `${LOG}/${t.id}-implement.txt`, kind: 'task', fork: '<forkSha>', port: PORT(t.n) })}${t.wf ? '\n' + WFPROOF({ dir: WT(t.id), log: `${LOG}/${t.id}-implement-workflow.txt` }) : ''}

STEP 2 — outcomes, each a comment on issue #${t.issue} with the heading as its first line, body through a file under ${LOG}/:
- Human-owned decision: ${STANDING ? `the standing answer for this launch (${DATE}) accepts your recommendation: take it after weighing real alternatives, and record each in a comment headed "## Decisions taken" (question, alternatives with costs, choice and why, how it is reversed). It covers code on your branch only.` : 'no standing answer: write a "## Status note" with the question, options and recommendation, swap ready-for-agent for ready-for-human, commit what exists with subject "Task ' + t.id + ': partial, question for Adam", and return "needs-human".'} List each in delegatedDecisions prefixed "Human-owned:".
- Cannot get green: "## Status note" with what blocks it; remove your assignee; commit what exists, subject "Task ${t.id}: attempt, not green"; return "failed".
- Green: tick the boxes you can point at (${TICK(t, 'tick')}), commit with subject "Task ${t.id}: <what landed>", then a comment headed "## Answer": what landed, the task commit's sha, the check's last line, every mutation you ran, and an "Outstanding:" line if any box is left. Say the issue closes when ${INT_BRANCH} lands. Never close the issue.
- No commit (HEAD equals forkSha): remove your assignee before returning.

Return outcome, branch, forkSha, headSha, complete, outstanding, summary (2-4 sentences), question, delegatedDecisions, proposedSteering.`

const reviewPrompt = (t, impl) => `Review ticket ${t.n} (${t.id}, issue #${t.issue}, ${t.url}) before it integrates. You did not write it. Unattended.
${HOUSE}

Worktree ${WT(t.id)}, branch ${impl.branch}, fixed point ${impl.forkSha}. Implementer's summary: ${impl.summary}
${t.notes ? 'NOTES FOR THIS TICKET: ' + t.notes + '\n' : ''}
STEP 0: if \`git -C ${WT(t.id)} log -1 --format=%s\` starts with "Review:" or the issue has a comment headed "## Review", the review is done: return "committed" with the current HEAD and the findings recorded there.

STEP 1 — the check, once. If the first line of ${LOG}/${t.id}-implement.txt is "HEAD=" the current HEAD and its last line starts "check: pass:", copy it to ${LOG}/${t.id}-review.txt; otherwise run:
${GATE({ dir: WT(t.id), log: `${LOG}/${t.id}-review.txt`, kind: 'task', fork: impl.forkSha, port: PORT(t.n) })}${t.wf ? '\n' + WFPROOF({ dir: WT(t.id), log: `${LOG}/${t.id}-review-workflow.txt` }) : ''}

STEP 2 — ${skill('code-review')}, fixed point ${impl.forkSha}, in ${WT(t.id)}. The spec is issue #${t.issue}'s body; standards include CODING_STANDARDS.md. Also:
- Re-run every mutation the task commit's body or the "## Answer" claims, in a throwaway copy of the worktree under ${WTROOT}, and record failed-of-total for each. A claim that does not reproduce, or a "never prints X" promise no test holds with a canary of X, is a finding.
- Steering edits: every changed path under GLOSSARY.md, AGENTS.md, CLAUDE.md, OWNERSHIP.md, CODING_STANDARDS.md, docs/agents/, docs/adr/ or contracts/ (contracts/changes/ aside) needs the Authority: trailer the notes name. Take out any change that lacks it and put its text under "## Proposed steering changes" in your Review.
- Re-weigh each "## Decisions taken" entry; reverse in code any that loses.

STEP 3 — fix every finding you verify, re-run the check (at most twice) and keep it green; untick any box not actually met (${TICK(t, 'untick')}). Post one comment headed "## Review" (findings, mutations re-run with their counts, fixed, skipped and why). Commit with subject "Review: <what changed>" (none if no change). Cannot get green: "## Status note", remove the assignee, return failed.

Return outcome, headSha, findingsFixed, findingsSkipped, summary, question, proposedSteering.`

const integratePrompt = (t, impl, rev) => `Integrate ticket ${t.n} (${t.id}, issue #${t.issue}) into ${INT_BRANCH}. You are the only integrator running.
${HOUSE}

Integration worktree ${INT_WT}. Task branch ${impl.branch}. Partial: ${impl.complete ? 'no' : 'yes — outstanding: ' + impl.outstanding}.
Implementer: ${impl.summary}
Reviewer: ${rev.summary}; fixed: ${(rev.findingsFixed || []).join(' | ') || 'none'}.

STEP 0: if \`git -C ${INT_WT} rev-parse -q --verify MERGE_HEAD\` succeeds, \`git -C ${INT_WT} merge --abort\`. If \`git -C ${INT_WT} merge-base --is-ancestor ${impl.branch} HEAD\` succeeds, the merge is in: if ${LOG}/${t.id}-integrate.txt ends "check: pass:" for the current HEAD, go to STEP 3; else to STEP 2.

STEP 1 — merge. old=\`git -C ${INT_WT} rev-parse HEAD\`. Write the merge message to ${LOG}/${t.id}-merge-message.txt (subject "${impl.complete ? `Merge task ${t.id}: <what landed>` : `Merge part of task ${t.id}: <what landed>`}"; body: what was verified and how, the review's fixes, the mutations re-run, the line "Ticket: #${t.issue}", ${impl.complete ? '' : 'an "Outstanding:" line, '}then "${TRAILER}"), then \`git -C ${INT_WT} merge --no-ff -F ${LOG}/${t.id}-merge-message.txt ${impl.branch}\`. A conflict: \`git -C ${INT_WT} merge --abort\` and return ok=false, conflict=true, files (the conflicting paths). Then \`python3 -I tools/roadmap.py\` in ${INT_WT}; if it changed README.md or docs/roadmap.md, commit those two alone, subject "Refresh the generated roadmap blocks after merging ${t.id}".

STEP 2 — the integration check:
${GATE({ dir: INT_WT, log: `${LOG}/${t.id}-integrate.txt`, kind: 'integration', fork: BASE, port: PORT(t.n) })}${t.wf ? '\n' + WFPROOF({ dir: INT_WT, log: `${LOG}/${t.id}-integrate-workflow.txt` }) : ''}
Red: \`git -C ${INT_WT} reset --hard <old>\` and return ok=false, conflict=false with the failing lines. Do not fix code here.

STEP 3 — green: \`git -C ${REPO} worktree remove ${WT(t.id)}\` (no --force; if it refuses, leave it and say why). Keep the branch.

Return ok, mergeSha, conflict, files, worktreeRemoved, notes.`

const grillPrompt = (t) => `Grill ticket ${t.id} on paper for Adam: issue #${t.issue}, ${t.url}.
STEP 0: if a comment headed "## Questions for Adam" exists, return its questions and summary "carried, not re-grilled", and write nothing.
Follow ${SKILLS}/grill-with-docs/SKILL.md (read it): ${skill('grilling')}, then ${skill('domain-modeling')}. Run the design tree on paper: numbered questions with options and recommendations, ${STANDING ? `each answered "Answer: yes, pre-accepted under the standing answer granted ${DATE}"` : 'each "Answer: open"'}, until the frontier is empty. Facts are yours to read from ${REPO}; decisions are Adam's.
Write only to issue #${t.issue}, two comments through files under ${LOG}/: "## Grilling record" and "## Questions for Adam" (numbered, "yes" accepts the recommendation). Edit nothing in the repo, and not the body, labels or assignees.
Return questions (q, recommendation) and a 2-sentence summary.`

const landPrepPrompt = (s) => `Prepare the landing of ${INT_BRANCH}. Repo ${REPO}, integration worktree ${INT_WT}, base ${BASE}.
${HOUSE}
In this step only, you may push ${INT_BRANCH} and nothing else.
Results (JSON): ${JSON.stringify(s)}

1. \`python3 -I tools/roadmap.py\` in ${INT_WT}; commit README.md and docs/roadmap.md alone if it changed them, subject "Refresh the generated roadmap blocks for the ${DATE} sequence".
2. Push with the pre-push hook running, never --no-verify: \`git -C ${INT_WT} push -u origin ${INT_BRANCH}\`. If the hook refuses, stop here: return pushed false with its last lines in notes.
3. Dead links (#250's review held on them): for each integrated ticket, read its "## Answer" comment; every sha it cites must be reachable from origin/${INT_BRANCH} (\`git -C ${REPO} fetch origin ${INT_BRANCH}\`, then \`git -C ${REPO} merge-base --is-ancestor <sha> origin/${INT_BRANCH}\`). For each that is not, post a comment headed "## Answer, corrected" citing the task merge's sha on ${INT_BRANCH} instead, and list it in fixedLinks.
4. Stale partials (#204 box 4 went stale when #210 merged): for each partial ticket, re-read its open boxes against ${INT_BRANCH}'s tip; tick any a later merge met (only the "- [ ]" markers, through a body file under ${LOG}/), say which merge met it in a comment, and count the ticket complete when nothing is left.
5. Open the landing PR: \`cd ${REPO} && gh pr create --base main --head ${INT_BRANCH} --title "Sequence ${DATE}: <N> tickets integrated, <M> part-landed" --body-file ${LOG}/landing-pr.md\`, whose body is a one-paragraph summary, then one line "Closes #<n>" per complete ticket and one "Ticket: #<n>" per partial one, then "Each ticket's task merge on ${INT_BRANCH} is its merge body." If a PR from ${INT_BRANCH} is already open, edit its body instead.
Return pushed, head (the pushed sha), pr, prUrl, closes, tickets, fixedLinks, notes.`

const reportPrompt = (s) => `Write the report of this sequence launch as one GitHub issue. Repo ${REPO}, base ${BASE}, integration branch ${INT_BRANCH}, logs ${LOG}.
Results (JSON): ${JSON.stringify(s)}
\`cd ${REPO} && gh issue create --title "Sequence ${DATE}" --body-file ${LOG}/report.md\` (no label, no parent; if an open issue with that title exists, edit its body instead). The body:
- "Standing answer: ${STANDING ? 'granted for this launch (' + DATE + ')' : 'not granted'}." Then one line of counts.
- Integrated (ticket, #issue, merge sha), Partial (Outstanding), Conflicts (files), Failed (last lines of its log and its Status note), Skipped, Out of this launch (each with what it waits on, as #issue).
- Decisions taken: every delegatedDecisions entry, Human-owned first, and whether its review upheld it.
- Proposed steering changes: each entry, quoted whole, as a question whose "yes" commits it.
- Landing: the PR from the results (or, if land prep did not run, the description to open it with).
- Needs you, in this order: dispatch a fresh top-level session to review the PR's exact head and post its record, expecting one fix round (#250 needed nine follow-up commits); \`tools/land.sh <pr> --subject "..." --body-file <path>\`; if #213 landed, the cmp of each live file against its live-bytes commit before \`git merge --ff-only origin/main\` (Q42), then remove the stale overnight_harness.mjs; delete ${INT_BRANCH} by hand after landing (prune_branches.py reads only overnight/<date>); remove leftover worktrees under ${WTROOT} and \`git -C ${REPO} worktree prune\`; the proof tickets (ready-for-human, they push throwaway PRs: #87, #255-#258 and each spec's proof ticket); #245's night once #136 lands; relaunch with a new date for anything Out of this launch. If #213 landed, name the GLOSSARY collision between #252 (a new *Harness* sense) and #213's Q44 (Harness unchanged).
- Grill questions, if any: as comments on the report issue, numbered Q1..Qn, each with "Recommended:" and its Answer line.
Before creating: \`git -C ${REPO} status --porcelain --untracked-files=no\` must be empty and HEAD still ${BASE}; record it in the body. List anything new untracked against ${LOG}/untracked-at-start.txt.
Return issue, url, mainCheckoutClean, leftovers.`

// ---------------------------------------------------------------- plan resolution (plain code)
const issueOf = (id) => Number(id.replace('issue-', ''))
const resolvePlan = (col) => {
  const facts = {}
  ;[...col.planned, ...col.discovered, ...col.followups].forEach((f) => { facts[f.issue] = f })
  const nodes = []
  PLAN.forEach((p) => nodes.push({ ...p, needs: [...(p.needs || [])], after: [], planned: true }))
  // A spec's build tickets are its ready-for-agent sub-issues; its proof tickets (ready-for-human) are reported only.
  SPECS.forEach((s) => col.discovered.filter((f) => f.parent === s.issue && (f.labels || []).includes('ready-for-agent'))
    .sort((a, b) => a.issue - b.issue).forEach((f, i, all) => {
      nodes.push({ id: `issue-${f.issue}`, issue: f.issue, slug: f.slug, groups: [`wf:${s.name}`, ...sharedGroups(f)], wf: true, after: [],
        needs: i > 0 ? [`issue-${all[i - 1].issue}`] : [], afterGroups: s.after || [],
        notes: `A build ticket split from spec #${s.issue} (${s.name}); read the spec for the design. Proof boxes live in its proof ticket.` })
    }))
  col.followups.forEach((f) => nodes.push({ id: `issue-${f.issue}`, issue: f.issue, slug: f.slug, groups: ['skill:wiz', ...sharedGroups(f).filter((g) => g !== 'skill:wiz')], needs: ['issue-213'], after: [],
    notes: `A follow-up of #213 (Q40/Q41 confirmed on #226: ${CONFIRM_226}).` }))
  const status = {}   // id -> 'run' | 'met' | 'grill' | 'out'
  const why = {}
  nodes.forEach((t, i) => {
    const f = facts[t.issue]
    t.n = i + 1
    if (!f) { status[t.id] = 'out'; why[t.id] = 'collect returned no facts'; return }
    Object.assign(t, { title: f.title, url: f.url, owner: f.owner || 'integrator', slug: t.slug || f.slug })
    if (SKIP.has(t.id) || (ONLY && !ONLY.includes(t.id))) { status[t.id] = 'out'; why[t.id] = 'not in this launch (only/skip)'; return }
    if (f.state === 'CLOSED') { status[t.id] = 'met'; return }
    const labels = f.labels || []
    if (labels.includes('ready-for-human')) {
      if (t.planned && !f.grilled) status[t.id] = 'grill'
      else { status[t.id] = 'out'; why[t.id] = 'ready-for-human: waits on Adam' }
      return
    }
    if (!labels.includes('ready-for-agent')) { status[t.id] = 'out'; why[t.id] = `labelled ${labels.join(', ') || 'nothing'}`; return }
    if (f.openBoxes === 0) { status[t.id] = 'met'; why[t.id] = 'every box ticked; it closes at the landing'; return }
    if ((f.assignees || []).length && !f.taskBranch) { status[t.id] = 'out'; why[t.id] = `claimed by ${f.assignees.join(', ')}`; return }
    status[t.id] = 'run'
    ;(f.openBlockers || []).forEach((b) => { const bid = `issue-${b}`; if (!t.needs.includes(bid)) t.needs.push(bid) })
  })
  // A need on anything that is neither closed nor running this launch keeps the ticket out; repeat until stable.
  let changed = true
  while (changed) {
    changed = false
    nodes.filter((t) => status[t.id] === 'run').forEach((t) => {
      const ok = (b) => status[b] === 'met' || status[b] === 'run' || (facts[issueOf(b)] && facts[issueOf(b)].state === 'CLOSED')
      const bad = t.needs.find((b) => !ok(b))
      if (bad) { status[t.id] = 'out'; why[t.id] = `waits on #${issueOf(bad)}${why[bad] ? ' (' + why[bad] + ')' : ''}`; changed = true }
    })
  }
  const run = nodes.filter((t) => status[t.id] === 'run')
  const inRun = new Set(run.map((t) => t.id))
  run.forEach((t) => {
    t.needs = t.needs.filter((b) => inRun.has(b))
    ;(t.afterGroups || []).forEach((g) => run.filter((o) => o.groups.includes(g)).forEach((o) => t.after.push(o.id)))
  })
  return { nodes, status, why, run, grillT: nodes.filter((t) => status[t.id] === 'grill') }
}
// A need or soft order that loops would hang the run; refuse it before any work starts.
const findCycle = (run) => {
  const edges = Object.fromEntries(run.map((t) => [t.id, [...t.needs, ...t.after]]))
  const mark = {}
  const visit = (id, path) => {
    if (mark[id] === 1) return [...path, id]
    if (mark[id] === 2) return null
    mark[id] = 1
    for (const b of edges[id] || []) { const c = visit(b, [...path, id]); if (c) return c }
    mark[id] = 2
    return null
  }
  for (const t of run) { const c = visit(t.id, []); if (c) return c }
  return null
}

// ---------------------------------------------------------------- concurrency helpers (from overnight.js)
let slots = MAX_PAR
const waiters = []
const acquire = () => new Promise((res) => { if (slots > 0) { slots--; res() } else waiters.push(res) })
const release = () => { const w = waiters.shift(); if (w) w(); else slots++ }
const withSlot = async (fn) => { await acquire(); try { return await fn() } finally { release() } }
let intLock = Promise.resolve()
const serial = (fn) => { const p = intLock.then(fn, fn); intLock = p.catch(() => {}); return p }

// ---------------------------------------------------------------- run
phase('Collect')
const col = await agent(collectPrompt, { label: 'collect', phase: 'Collect', schema: COLLECT })
if (!col) return { halted: 'collect agent died' }
if (col.outcome !== 'ok') return { halted: `dirty-start: ${col.reason}` }
if (!col.shell) return { halted: `the Bash tool runs ${col.shellCommand || 'zsh'}, not bash; restart Claude Code, then relaunch` }
BASE = col.base || BASE

const plan = resolvePlan(col)
const cycle = findCycle(plan.run)
if (cycle) return { halted: `the plan loops: ${cycle.join(' -> ')}; no ticket started` }
log(`runnable ${plan.run.length}: ${plan.run.map((t) => t.id).join(' ')}; grill ${plan.grillT.length}; out ${plan.nodes.filter((t) => plan.status[t.id] === 'out').length}`)

const results = {}
const grillQueue = []
let failures = 0
let halted = null
const fail = (t, stage, info) => {
  results[t.id] = { stage, outcome: 'failed', ...info }
  if (++failures >= MAX_FAIL && !halted) { halted = `${failures} failures; no new tickets started`; log(halted) }
  return false
}
plan.grillT.forEach((t) => grillQueue.push(agent(grillPrompt(t), { label: `grill:${t.id}`, phase: 'Grill', schema: GRILL })
  .then((g) => { results[t.id] = { stage: 'grill', outcome: 'needs-human', grill: g || 'grill agent died' } })))

const work = async (t) => {
  // A ticket started before the halt still waits for a slot; it checks again once it has one.
  const impl = await withSlot(() => halted ? Promise.resolve({ halt: true }) : agent(implPrompt(t), { label: `implement:${t.id}`, phase: 'Implement', schema: IMPL }))
  if (impl && impl.halt) { results[t.id] = { stage: 'implement', outcome: 'skipped', why: halted }; return false }
  if (!impl) return fail(t, 'implement', { why: 'agent died; worktree may be mid-change' })
  if (impl.outcome === 'skipped') { results[t.id] = { stage: 'implement', outcome: 'skipped', why: impl.summary }; return true }
  if (impl.outcome === 'needs-human') { results[t.id] = { stage: 'implement', outcome: 'needs-human', ...impl }; return false }
  if (impl.outcome !== 'resolved') return fail(t, 'implement', impl)
  if (impl.headSha === impl.forkSha) { results[t.id] = { stage: 'implement', outcome: 'skipped', why: 'the lane changed nothing', impl }; return false }
  const rev = await withSlot(() => agent(reviewPrompt(t, impl), { label: `review:${t.id}`, phase: 'Review', schema: REVIEW }))
  if (!rev) return fail(t, 'review', { why: 'agent died', impl })
  if (rev.outcome === 'needs-human') { results[t.id] = { stage: 'review', outcome: 'needs-human', ...rev, impl }; return false }
  if (rev.outcome !== 'committed') return fail(t, 'review', { ...rev, impl })
  // The integrate lock is the outer one: a slot held while queueing for it would deadlock once every slot waits.
  const integ = await serial(() => withSlot(() => agent(integratePrompt(t, impl, rev), { label: `integrate:${t.id}`, phase: 'Integrate', schema: INT })))
  if (!integ || !integ.ok) return fail(t, 'integrate', { why: integ ? integ.notes : 'agent died', conflict: !!(integ && integ.conflict), files: integ ? integ.files : [], impl, rev })
  results[t.id] = { stage: 'integrated', outcome: impl.complete ? 'integrated' : 'partial', mergeSha: integ.mergeSha, outstanding: impl.outstanding,
    summary: impl.summary, findingsFixed: rev.findingsFixed, delegatedDecisions: impl.delegatedDecisions,
    proposedSteering: { implement: impl.proposedSteering || [], review: rev.proposedSteering || [] }, worktreeRemoved: integ.worktreeRemoved }
  log(`${t.id}: integrated ${integ.mergeSha}`)
  return true
}

// The scheduler: same-file tickets in table order, everything else as soon as its needs integrate. Locks are taken
// all at once, synchronously, so there is no hold-and-wait; a ready ticket that cannot start reserves its groups so a
// later one cannot barge past it; a ticket still waiting on a need holds and reserves nothing.
const queue = [...plan.run]
const settled = {}
const held = new Set()
let running = 0
let pumping = false
let again = false
let finish
const allDone = new Promise((res) => { finish = res })
const skip = (t, why) => { results[t.id] = results[t.id] || { stage: 'implement', outcome: 'skipped', why }; settled[t.id] = false; queue.splice(queue.indexOf(t), 1) }
const ready = (t) => t.needs.every((b) => settled[b] === true) && t.after.every((b) => b in settled)
const earlierInGroup = (t) => queue.some((o) => queue.indexOf(o) < queue.indexOf(t) && o.groups.some((g) => t.groups.includes(g)))
const pump = () => {
  if (pumping) { again = true; return }
  pumping = true
  do {
    again = false
    const reserved = new Set()
    for (const t of [...queue]) {
      const dead = t.needs.find((b) => settled[b] === false)
      if (halted || dead) { skip(t, halted || `needs ${dead}, which did not integrate`); again = true; continue }
      if (!ready(t)) continue
      if (earlierInGroup(t) || t.groups.some((g) => held.has(g) || reserved.has(g))) { t.groups.forEach((g) => reserved.add(g)); continue }
      t.groups.forEach((g) => held.add(g))
      queue.splice(queue.indexOf(t), 1)
      running++
      work(t).catch((e) => fail(t, 'implement', { why: `lane threw: ${e && e.message ? e.message : e}` }))
        .then((ok) => { t.groups.forEach((g) => held.delete(g)); running--; settled[t.id] = ok === true; pump() })
    }
  } while (again)
  pumping = false
  if (!running) {
    // Nothing runs and nothing can start: what is left can never become ready, so say so instead of hanging.
    ;[...queue].forEach((t) => skip(t, 'never became ready'))
    finish()
  }
}
phase('Implement')
pump()
await allDone
await Promise.all(grillQueue)

const ids = (pred) => Object.entries(results).filter(([, r]) => pred(r)).map(([id]) => id)
const outOfLaunch = plan.nodes.filter((t) => plan.status[t.id] === 'out').map((t) => ({ id: t.id, issue: t.issue, waits: plan.why[t.id] }))
const summary = {
  base: BASE, integrationBranch: INT_BRANCH, halted, results, outOfLaunch,
  met: plan.nodes.filter((t) => plan.status[t.id] === 'met').map((t) => ({ id: t.id, issue: t.issue, why: plan.why[t.id] || 'closed' })),
  tickets: plan.nodes.map((t) => ({ id: t.id, issue: t.issue, title: t.title, url: t.url })),
}

let land = null
if (ids((r) => r.stage === 'integrated').length) {
  phase('Land prep')
  land = await agent(landPrepPrompt(summary), { label: 'land-prep', phase: 'Land prep', schema: LANDPREP })
}
phase('Report')
const rep = await agent(reportPrompt({ ...summary, land }), { label: 'report', phase: 'Report', schema: REPORT })

return {
  halted,
  integrated: ids((r) => r.outcome === 'integrated'),
  partial: ids((r) => r.outcome === 'partial'),
  failed: ids((r) => r.outcome === 'failed'),
  skipped: ids((r) => r.outcome === 'skipped'),
  needsHuman: ids((r) => r.outcome === 'needs-human'),
  outOfLaunch: outOfLaunch.map((o) => `${o.id}: ${o.waits}`),
  landingPr: land ? land.prUrl : null,
  reportUrl: rep ? rep.url : null,
}
