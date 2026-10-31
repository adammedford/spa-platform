// A review record's follow-up body from the command line (#643). Owner: integrator.
//
//   node .claude/workflows/follow_up_body.mjs <pr> <record-url> <record.md> <row>
//
// <pr> is the pull request's number, <record-url> the record's link on it (the html_url of the
// comment or review that carries the record line), <record.md> a file holding the record's body
// verbatim ("-" reads it from stdin), and <row> the 1-based row of its "## Follow-ups" table, the
// <k> of the morning's followups/pr-<pr>-<k>.md. It prints the body the morning would file for that
// row, byte for byte with no newline added, so `> body.md` gives the file `gh issue create
// --body-file` takes; on stderr it names the row's title and the label the morning would file it
// under, and why when that label is ready-for-human. It writes nothing and calls nothing.
//
// It runs morning.js's own code: it reads the sections "follow-ups, read here", "follow-ups, owners
// and places" and "follow-ups, their bodies" out of the morning.js beside it and calls followUpsOf,
// ownershipRules, placeOf and followUpBody as followUpsToFile calls them, with the OWNERSHIP.md of
// the checkout this file sits in, read verbatim as the morning's follow-ups reader returns it. A
// workflow script cannot be imported (it returns at top level), which is why the sections are read
// as text, and morning.js and review-queue.js stay as they are; the companion morning_tests.mjs
// holds this command's output to those functions' direct result. A row Adam wants filed before the
// next morning reads it is filed from this body instead of a hand cut of morning.js (#514, #568).
//
// Exit 0: the body was printed. Exit 1: the record has no Follow-ups row, or none at <row>. Exit 2:
// a missing or malformed argument, an unreadable record file, a body with no record line, an
// OWNERSHIP.md that cannot be read or has no ownership block, or a morning.js it cannot read the
// sections from. Every nonzero exit prints its message on stderr and no body.
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const USAGE = 'usage: node .claude/workflows/follow_up_body.mjs <pr> <record-url> <record.md|-> <row>'
const stop = (msg, code = 2) => { process.stderr.write(`follow-up-body: error: ${msg}\n`); process.exit(code) }
const argv = process.argv.slice(2)
if (argv.length !== 4) stop(USAGE)
const [prArg, url, recordPath, rowArg] = argv
if (!/^[1-9]\d*$/.test(prArg)) stop(`the pull request number ${JSON.stringify(prArg)} is not a positive integer; ${USAGE}`)
const pr = Number(prArg)
// The record's link on that pull request, as GitHub serves a comment's or a review's html_url.
if (!new RegExp(`^https://github\\.com/[^/\\s]+/[^/\\s]+/pull/${pr}#\\S+$`).test(url)) stop(`the record URL ${JSON.stringify(url)} is not a comment or review link on pull request #${pr}`)
if (!/^[1-9]\d*$/.test(rowArg)) stop(`the row ${JSON.stringify(rowArg)} is not a positive integer (the 1-based row of the record's Follow-ups table)`)
const row = Number(rowArg)

const here = path.dirname(fileURLToPath(import.meta.url))
const scriptPath = path.join(here, 'morning.js')
const ownershipPath = path.resolve(here, '..', '..', 'OWNERSHIP.md')
let src
try { src = fs.readFileSync(scriptPath, 'utf8') } catch (e) { stop(`cannot read ${scriptPath}: ${e.message}`) }
const sectionOf = (name) => (new RegExp(`^// -+ ${name}\\n([\\s\\S]*?)\\n// -+ `, 'm').exec(src) || [])[1]
const names = ['follow-ups, read here', 'follow-ups, owners and places', 'follow-ups, their bodies']
const sections = names.map(sectionOf)
const lost = names.filter((n, i) => !sections[i])
if (lost.length) stop(`${scriptPath} has no ${lost.map((n) => JSON.stringify(n)).join(', ')} section`)
let port
try { port = new Function(`${sections.join('\n')}\nreturn { RECORD, PY_LINE_BREAK, PY_STRIP, followUpsOf, ownershipRules, placeOf, followUpBody }`)() } catch (e) { stop(`the follow-ups code in ${scriptPath} did not load: ${e.message}`) }

let body
try { body = fs.readFileSync(recordPath === '-' ? 0 : path.resolve(recordPath), 'utf8') } catch (e) { stop(`cannot read the record ${recordPath}: ${e.message}`) }
// A body with no record line is not a review record: the morning reads a table only from the latest
// comment or review carrying one (latestRecordOf), so anything else is the wrong input.
if (!body.split(port.PY_LINE_BREAK).some((l) => port.RECORD.test(l.replace(port.PY_STRIP, '')))) stop(`${recordPath} has no record line ("Reviewed <head> against <base>: merge|hold"), so it is not a review record's body`)
let ownership
try { ownership = fs.readFileSync(ownershipPath, 'utf8') } catch (e) { stop(`cannot read ${ownershipPath}: ${e.message}`) }
const rules = port.ownershipRules(ownership)
// No rules derive no owner: the morning files none then (CODING_STANDARDS.md, "A check fails closed").
if (!rules.length) stop(`${ownershipPath} has no ownership block, so no follow-up's owner can be derived`)

const rows = port.followUpsOf(body, url)
if (!rows.length) stop(`${recordPath} has no "## Follow-ups" row, so there is no follow-up to print`, 1)
if (row > rows.length) stop(`${recordPath}'s Follow-ups table has ${rows.length} row(s), not ${row}`, 1)
const f = rows[row - 1]
const place = port.placeOf(rules, f.where)
process.stderr.write(`follow-up-body: row ${row} of ${rows.length}: ${JSON.stringify(f.title)}, label ${place.label}${place.why ? `: ${place.why}` : ''}\n`)
process.stdout.write(port.followUpBody(pr, url, f, place))
