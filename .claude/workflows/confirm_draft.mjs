// The morning's draft check from the command line (#491). Owner: integrator.
//
//   node .claude/workflows/confirm_draft.mjs <draft.md> <comments.json> [--json]
//
// <draft.md> is a draft Confirm comment for an Overnight <date> issue; <comments.json> is that
// issue's comments as `gh issue view <n> --json comments` prints them (an object with a comments
// array, or the bare array). It prints the questions the draft covers, those it overrides, those
// answered no, those left open and any number no questions comment yields, then one verdict line:
// `confirm-draft: pass: ...` or `confirm-draft: fail: ...`. With --json it prints the sort as JSON
// instead. It writes nothing and calls nothing.
//
// It runs morning.js's own code: it reads the section "the questions, read here" and the ADAM line
// out of the morning.js beside it and calls draftCheck, the function the confirmFile mode calls, so
// a change to the morning's parser is the change this command runs. A workflow script cannot be
// imported (it returns at top level), which is why the section is read as text; the companion
// morning_tests.mjs holds this command's output to draftCheck's direct result.
//
// Exit 0: every question the comments yield is confirmed, none is left open, no Q<n>: line opens
// "no", and every number the draft names is found. Exit 1: any of those fails, the draft does not
// begin "Confirm", or a "## Questions:" comment could not be read. Exit 2: it could not judge: a
// missing or unreadable file, comments that are not JSON, an issue that already has a Confirm
// comment, comments with no questions comment among them, or a morning.js it cannot read the parser
// from.
// The confirmFile mode of the morning workflow is the full check with an agent reading the issue;
// its verdict fails only on a number no questions comment yields, where this one, the step a session
// runs before handing a draft to Adam, also fails on what the draft leaves undecided.
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const stop = (msg) => { process.stderr.write(`confirm-draft: error: ${msg}\n`); process.exit(2) }
const argv = process.argv.slice(2)
const json = argv.includes('--json')
const files = argv.filter((a) => a !== '--json')
if (files.length !== 2) stop('usage: node .claude/workflows/confirm_draft.mjs <draft.md> <comments.json> [--json]')
const [draftPath, commentsPath] = files.map((f) => path.resolve(f))

const scriptPath = path.join(path.dirname(fileURLToPath(import.meta.url)), 'morning.js')
let src
try { src = fs.readFileSync(scriptPath, 'utf8') } catch (e) { stop(`cannot read ${scriptPath}: ${e.message}`) }
const section = (/^\/\/ -+ the questions, read here\n([\s\S]*?)\n\/\/ -+ /m.exec(src) || [])[1]
const adam = (/^const ADAM = .*$/m.exec(src) || [])[0]
if (!section || !adam) stop(`${scriptPath} has no "the questions, read here" section or no ADAM line`)
let parser
try { parser = new Function(`${adam}\n${section}\nreturn { draftCheck, draftLists }`)() } catch (e) { stop(`the parser in ${scriptPath} did not load: ${e.message}`) }

let draft
try { draft = fs.readFileSync(draftPath, 'utf8') } catch (e) { stop(`cannot read the draft ${draftPath}: ${e.message}`) }
if (!draft.trim()) stop(`the draft ${draftPath} is empty`)
let listed
try { listed = JSON.parse(fs.readFileSync(commentsPath, 'utf8')) } catch (e) { stop(`cannot read the comments ${commentsPath} as JSON: ${e.message}`) }
const raw = Array.isArray(listed) ? listed : listed && Array.isArray(listed.comments) ? listed.comments : null
if (!raw) stop(`${commentsPath} holds no comments array; write it with gh issue view <n> --json comments`)
// gh prints the author as {login}; the morning's agents return the login itself.
const comments = raw.map((c) => ({ author: c.author && typeof c.author === 'object' ? c.author.login : c.author, createdAt: c.createdAt, url: c.url, body: c.body }))

const r = parser.draftCheck(comments, draft, draftPath)
if (r.halted) stop(`the issue ${r.halted}`)
// No questions comment at all is no input, not a pass: the wrong issue's comments, or a file written
// before the night's questions were posted, would otherwise pass "0 of 0" (CODING_STANDARDS.md, "A
// check fails closed").
if (!r.total && !r.refusals.length) stop(`${commentsPath} holds no "## Questions:" comment, so there is nothing to judge the draft against`)
if (json) {
  process.stdout.write(JSON.stringify(r) + '\n')
} else {
  const L = [`## Draft Confirm, ${draftPath}`, '']
  L.push(`The first sentence covers ${r.covered} of ${r.total} questions.`)
  L.push(`${r.overrides} Q<n>: line(s) override; ${r.byOwnLines} confirmed by their own Q<n>: lines alone, named by no first sentence.`)
  L.push(`${r.confirmed} confirmed of ${r.total}: ${r.applied} applied, ${r.no.length} answered no, ${r.open.length} left open.`)
  if (r.notConfirm) L.push('The draft does not begin with "Confirm", so the apply step would read it as no Confirm comment at all.')
  L.push(...parser.draftLists(r))
  const why = []
  if (r.notConfirm) why.push('the draft does not begin "Confirm"')
  if (r.open.length) why.push(`${r.open.length} left open (${r.open.map((o) => 'Q' + o.q).join(', ')})`)
  if (r.noOverrides.length) why.push(`override(s) opening "no" (${r.noOverrides.map((q) => 'Q' + q).join(', ')})`)
  if (r.unknown.length) why.push(`named by the draft, yielded by no questions comment (${r.unknown.map((q) => 'Q' + q).join(', ')})`)
  if (r.refusals.length) why.push(`${r.refusals.length} questions comment(s) not read`)
  L.push('', why.length ? `confirm-draft: fail: ${why.join('; ')}` : `confirm-draft: pass: ${r.confirmed} of ${r.total} confirmed, ${r.applied} applied, ${r.no.length} answered no`)
  process.stdout.write(L.join('\n') + '\n')
}
const failed = r.notConfirm || r.open.length || r.noOverrides.length || r.unknown.length || r.refusals.length
process.exit(failed ? 1 : 0)
