#!/usr/bin/env python3
"""The retro workflow's record reader: the deterministic reading of an overnight run's record.
Owner: integrator.

The retro (#77, built under #260) reads one overnight run's record and files follow-up tickets. Its
agents judge what a record line means; this script decides what a line *is*, so that the rules hold
without an agent in the loop and its companion, tools/retro_record_tests.py, can show each one fail.
#77 -> "The record reader" lists what lives here; the agents call it instead of re-deriving it.

    python3 -I tools/retro_record.py log FILE...           one check log each, parsed
    python3 -I tools/retro_record.py logs DIR              a log directory: logs, side logs, rounds
    python3 -I tools/retro_record.py report FILE [--title T]   a report or questions file's date and headings
    python3 -I tools/retro_record.py merge < findings.json      findings sharing a noun, merged
    python3 -I tools/retro_record.py rank < rank-input.json     merged, dropped and ranked
    python3 -I tools/retro_record.py coverage < cov-input.json  known issues matched and missed
    python3 -I tools/retro_record.py body-check < body.md       a ticket body's shape
    python3 -I tools/retro_record.py state DIR             the retro's phase files under its scratch directory
    python3 -I tools/retro_record.py script FILE           a workflow script's path, sha256 and mtime

Every command prints JSON on standard output. It exits 0 when it read its input; 1 when the input is
not what the command reads (a report with no date, a body not in the ticket shape), with the reason
on stderr or in the JSON; 2 on a usage error.

**A check log** is a `.txt` file in the night's log directory whose first line is `HEAD=<sha>` and
whose last line is a verdict. Two verdict forms are in the record: `GATE: GREEN` / `GATE: RED`, which
the overnight workflow wrote until #68 moved the check into tools/check.sh, and `check: pass: ...` /
`check: fail: ...`, which tools/check.sh --full writes as its last line. Both read as GREEN or RED. The
test runner's summary lines (`total:`, `failed:`, `succeeded:`, `skipped:`, `duration:`, indented)
are read from their last occurrence. A log without the `HEAD=` first line or without a verdict last
is a **side log**: it is listed and its verdict is "", never read as GREEN or RED (#77: three
lane-written side logs follow no shape). A file in the directory that is not `.txt` (a `-body.md`, a
`-comment.md`) is not a log at all and is listed under notLogs.

**A log's name** is `<task>-<stage>.txt`, where task is a plan row id (`1.6a`, `I-4`, `P2-7`),
`issue-<n>`, or `ticket-<n>`, the archived tickets' ids the 2026-10-16 night named its logs by
(`ticket-71-implement.txt`), and stage the rest (`implement`, `review-terraform-test`). A re-run renames the earlier
log with a date suffix, `-YYYY-MM-DD` or `.YYYY-MM-DD` before `.txt`; that log is an **earlier round**
of the same task and stage, and `logs` pairs it under rounds.

**A report** is resolved by its first line, since one night's report and questions files carry
undated names (`REPORT.md`, `questions.md`): `# Overnight report — <date>` is a report, a first line
beginning `# Questions` a questions file, and the date is the first YYYY-MM-DD in it. That is the
archive shape. The Issues shape is an `Overnight <date>` issue's body, whose date is its title's
(`--title`). Both carry the same `## ` section headings, which come back in order.

**A finding** is #77's schema: type, title, noun, evidence[], cost {red, attempts, nights, lanes},
fix {kind, owner, summary, diff} and confidence. Findings **merge** on their noun, stripped and
compared exactly ("grep the noun", docs/agents/review.md): red, attempts and lanes are summed, because
each finding counted its own lane's; nights is the largest, because every lane counted the same
nights; evidence is every distinct line; confidence is verified when any was. **Ranking** is cost
times recurrence: cost is red + attempts + lanes, at least 1, and recurrence is the number of
distinct earlier nights whose history text carries the noun, plus one. Ties keep their merged order.
Findings named in `drop` (an open issue covers them, or the fix is on origin/main) leave the ranking
for the dropped list with the issue the agent cited.

**Coverage** against known issues (a retrospective already filed for the night): a known issue is
matched by a finding whose noun occurs in the issue's text and which has at least one evidence line;
every other known issue is missed, with the entry of the index's missing list the agent gave for it,
or "unexplained" when it gave none or one the list does not hold (#77 -> proof 1).

**A ticket body** is the overnight collect step's shape: an `**Owner:**` line naming one role of
OWNERSHIP.md, a `**Plan row:**` line (a row id or none), a what-to-build part (`**What to build:**`
or `## What to build`), and an `## Acceptance` section with at least one `- [ ]` box.

**The record index** (#77 user story 22) is the Gather phase's JSON, built by an agent from these
commands and from git and gh; .claude/workflows/retro.js -> INDEX is its schema and its comment the
shape's documentation. `logs` output is its logs, sideLogs, notLogs and rounds members verbatim.
"""

from __future__ import annotations

import datetime
import hashlib
import json
import os
import re
import sys

HEAD = re.compile(r"^HEAD=([0-9a-f]{7,40})$")
VERDICTS = (
    (re.compile(r"^GATE: GREEN$"), "GREEN"),
    (re.compile(r"^GATE: RED$"), "RED"),
    (re.compile(r"^check: pass:"), "GREEN"),
    (re.compile(r"^check: fail:"), "RED"),
)
SUMMARY = re.compile(r"^\s*(total|failed|succeeded|skipped|duration):\s*(.*?)\s*$")
COUNTED = ("total", "failed", "succeeded", "skipped")
DATE = re.compile(r"\d{4}-\d{2}-\d{2}")
SUFFIX = re.compile(r"[.-](\d{4}-\d{2}-\d{2})$")
NAME = re.compile(r"^(?P<task>issue-\d+|ticket-\d+|I-\d+|P2-\d+|[^-]+)-(?P<stage>.+)$")
LAST_LINES = 5

# OWNERSHIP.md's roles, as the overnight collect step names them (tools/overnight_collect.py ROLES).
ROLES = ("ws1-infra", "ws2-hosting", "ws3-identity", "ws4-proxy", "ws5-fixtures", "ws6-verification",
         "integrator")
OWNER_LINE = re.compile(r"(?m)^\*\*Owner:\*\*[ \t]*(?P<value>[^\n]*)$")
PLAN_LINE = re.compile(r"(?m)^\*\*Plan row:\*\*[ \t]*\S")
WHAT = re.compile(r"(?m)^(\*\*What to build:\*\*|## What to build\s*$)")
ACCEPTANCE = re.compile(r"(?m)^## Acceptance[^\n]*$")
OPEN_BOX = re.compile(r"(?m)^\s*- \[ \]")


def utc(seconds: float) -> str:
    return datetime.datetime.fromtimestamp(seconds, datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def named(path: str) -> dict:
    stem = os.path.basename(path)
    if stem.endswith(".txt"):
        stem = stem[:-4]
    dated = SUFFIX.search(stem)
    round_ = dated.group(1) if dated else ""
    if dated:
        stem = stem[:dated.start()]
    m = NAME.match(stem)
    return {"task": m.group("task") if m else stem, "stage": m.group("stage") if m else "", "round": round_}


def parse_log(path: str) -> dict:
    with open(path, encoding="utf-8", errors="replace") as handle:
        text = handle.read()
    lines = text.splitlines()
    first = lines[0].strip() if lines else ""
    last = lines[-1].strip() if lines else ""
    head = HEAD.match(first)
    verdict = next((word for pattern, word in VERDICTS if pattern.match(last)), "")
    tests: dict = {}
    for line in lines:
        m = SUMMARY.match(line)
        if m:
            key, value = m.group(1), m.group(2)
            tests[key] = int(value) if key in COUNTED and value.isdigit() else value
    side = not (head and verdict)
    return {
        "path": path, **named(path),
        "sha": head.group(1) if head else "",
        "verdict": "" if side else verdict,
        "verdictLine": "" if side else last,
        "tests": tests,
        "lastLines": lines[-LAST_LINES:],
        "lines": len(lines),
        "mtime": utc(os.stat(path).st_mtime),
        "side": side,
    }


def parse_dir(directory: str) -> dict:
    names = sorted(os.listdir(directory))
    logs, not_logs = [], []
    for name in names:
        path = os.path.join(directory, name)
        if not os.path.isfile(path):
            continue
        (logs if name.endswith(".txt") else not_logs).append(path)
    parsed = [parse_log(p) for p in logs]
    rounds: dict = {}
    for entry in parsed:
        if entry["round"]:
            rounds.setdefault((entry["task"], entry["stage"]), []).append(entry)
    paired = []
    for (task, stage), earlier in sorted(rounds.items()):
        current = next((e["path"] for e in parsed
                        if (e["task"], e["stage"]) == (task, stage) and not e["round"]), "")
        paired.append({"task": task, "stage": stage, "current": current,
                       "earlier": [e["path"] for e in sorted(earlier, key=lambda e: e["round"])]})
    return {"logs": parsed, "sideLogs": [e["path"] for e in parsed if e["side"]], "notLogs": not_logs,
            "rounds": paired}


def parse_report(path: str, title: str | None) -> dict | None:
    with open(path, encoding="utf-8", errors="replace") as handle:
        text = handle.read()
    lines = text.splitlines()
    first = lines[0] if lines else ""
    sections = [line[3:].strip() for line in lines if line.startswith("## ")]
    if title is not None:
        m = DATE.search(title)
        if not m or not title.startswith("Overnight "):
            return None
        return {"path": path, "shape": "issues", "kind": "report", "date": m.group(0), "sections": sections}
    m = DATE.search(first)
    if not m or not first.startswith("# "):
        return None
    kind = "questions" if first.startswith("# Questions") else "report" if "report" in first.lower() else "other"
    return {"path": path, "shape": "archive", "kind": kind, "date": m.group(0), "sections": sections}


def merge(findings: list) -> list:
    merged: dict = {}
    for f in findings:
        noun = str(f.get("noun", "")).strip()
        cost = f.get("cost") or {}
        if noun not in merged:
            merged[noun] = {**f, "noun": noun, "evidence": [], "mergedFrom": 0,
                            "cost": {"red": 0, "attempts": 0, "lanes": 0, "nights": 0}}
        into = merged[noun]
        into["mergedFrom"] += 1
        for key in ("red", "attempts", "lanes"):
            into["cost"][key] += int(cost.get(key, 0) or 0)
        into["cost"]["nights"] = max(into["cost"]["nights"], int(cost.get("nights", 0) or 0))
        for e in f.get("evidence") or []:
            if e not in into["evidence"]:
                into["evidence"].append(e)
        if f.get("confidence") == "verified":
            into["confidence"] = "verified"
    return list(merged.values())


def rank(payload: dict) -> dict:
    findings = merge(payload.get("findings") or [])
    history = payload.get("history") or []
    drops = {str(d.get("noun", "")).strip(): d for d in payload.get("drop") or []}
    ranked, dropped = [], []
    for f in findings:
        if f["noun"] in drops:
            d = drops[f["noun"]]
            dropped.append({"noun": f["noun"], "issue": d.get("issue", ""), "reason": d.get("reason", "")})
            continue
        nights = {h.get("date") for h in history if f["noun"] and f["noun"] in str(h.get("text", ""))}
        recurrence = len(nights) + 1
        cost = f["cost"]
        weight = max(1, cost["red"] + cost["attempts"] + cost["lanes"])
        ranked.append({**f, "recurrence": recurrence, "score": weight * recurrence})
    ranked.sort(key=lambda f: -f["score"])
    return {"ranked": ranked, "dropped": dropped}


def coverage(payload: dict) -> dict:
    findings = payload.get("findings") or []
    missing = payload.get("missing") or []
    why = {str(k): v for k, v in (payload.get("why") or {}).items()}
    matched, missed = [], []
    for known in payload.get("known") or []:
        number, text = known.get("number"), str(known.get("text", ""))
        hit = next((f for f in findings
                    if str(f.get("noun", "")).strip() and str(f["noun"]).strip() in text and f.get("evidence")),
                   None)
        if hit:
            matched.append({"issue": number, "noun": str(hit["noun"]).strip(), "evidence": hit["evidence"][0]})
        else:
            reason = why.get(str(number), "")
            missed.append({"issue": number, "reason": reason if reason and reason in missing else "unexplained"})
    return {"matched": matched, "missed": missed}


def body_check(body: str) -> dict:
    problems = []
    owner = OWNER_LINE.search(body)
    if not owner:
        problems.append("no **Owner:** line: the owner line names one role of OWNERSHIP.md")
    elif owner.group("value").strip().split(" ")[0].strip(",;") not in ROLES:
        problems.append(f"the owner line names {owner.group('value').strip()!r}, not one of: {', '.join(ROLES)}")
    if not PLAN_LINE.search(body):
        problems.append("no **Plan row:** line: a plan row id, or none")
    if not WHAT.search(body):
        problems.append("no what-to-build part: **What to build:** or ## What to build")
    section = ACCEPTANCE.search(body)
    if not section or not OPEN_BOX.search(body[section.end():]):
        problems.append("no ## Acceptance section with a - [ ] box")
    return {"ok": not problems, "problems": problems}


def state(directory: str) -> list:
    files = []
    if not os.path.isdir(directory):
        return files
    for base, _dirs, names in os.walk(directory):
        for name in names:
            if not name.endswith(".json"):
                continue
            path = os.path.join(base, name)
            try:
                with open(path, encoding="utf-8") as handle:
                    data = json.load(handle)
            except (OSError, ValueError):
                continue
            if not isinstance(data, dict) or not isinstance(data.get("inputs"), str) or "result" not in data:
                continue
            rel = os.path.relpath(path, directory)[:-len(".json")]
            files.append({"name": rel.replace(os.sep, "/"), "inputs": data["inputs"], "result": data["result"]})
    return sorted(files, key=lambda f: f["name"])


def script(path: str) -> dict:
    with open(path, "rb") as handle:
        digest = hashlib.sha256(handle.read()).hexdigest()
    return {"path": path, "sha256": digest, "mtime": utc(os.stat(path).st_mtime)}


def emit(value: object) -> None:
    json.dump(value, sys.stdout, ensure_ascii=False, indent=1)
    sys.stdout.write("\n")


def main(argv: list) -> int:
    if not argv:
        print(__doc__.split("\n\n")[2], file=sys.stderr)
        return 2
    command, rest = argv[0], argv[1:]
    if command == "log" and rest:
        emit([parse_log(p) for p in rest])
        return 0
    if command == "logs" and len(rest) == 1:
        emit(parse_dir(rest[0]))
        return 0
    if command == "report" and rest:
        title = rest[rest.index("--title") + 1] if "--title" in rest[1:-1] else None
        found = parse_report(rest[0], title)
        if found is None:
            print(f"retro_record: {rest[0]} names no date in its first line or title", file=sys.stderr)
            return 1
        emit(found)
        return 0
    if command in ("merge", "rank", "coverage", "body-check") and not rest:
        data = sys.stdin.read()
        if command == "body-check":
            result = body_check(data)
            emit(result)
            return 0 if result["ok"] else 1
        payload = json.loads(data)
        emit(merge(payload) if command == "merge" else rank(payload) if command == "rank" else coverage(payload))
        return 0
    if command == "state" and len(rest) == 1:
        emit(state(rest[0]))
        return 0
    if command == "script" and len(rest) == 1:
        emit(script(rest[0]))
        return 0
    print(f"retro_record: unknown command or arguments: {' '.join(argv)}", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
