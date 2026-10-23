#!/usr/bin/env python3
"""The retro's count of Edit and Write refusals, read from a run's subagent transcripts.
Owner: integrator. Built under #315, from #109's Q166 and Q167.

    python3 -I tools/retro_refusals.py RUN_ID... [--root GLOB]     one line per workflow run
    python3 -I tools/retro_refusals.py --date YYYY-MM-DD [--root GLOB]   every overnight run of a date

#109's box 2 is zero "File has not been read yet" and zero "modified since read" refusals across two
consecutive overnight runs, and the retro's record (check logs, comments, branches) holds no
transcript, so until this script the count was a human's read each morning. It prints, for each run:

    retro-refusals: <run>: <n> Edit and Write calls (<m> tool_use blocks, replays included),
      <r> "File has not been read yet" (<e> Edit, <w> Write), <s> "modified since read"; agents: <labels>

on one line, or, when the run's transcripts are not under the root,

    retro-refusals: <run>: missing: no subagent transcripts for this run under <root>

which is never a zero: a retro that cannot read the transcripts says so (#315).

**A run** is one launch of a workflow script, named by its id (`wf_3b77ab2e-214`); a second launch
on the same date is a run of its own (Q167). Its transcripts are every `agent-*.jsonl` in each
directory named for it under the root, a glob that defaults to
`~/.claude/projects/-Users-adammedford-Projects-spa-platform/*/subagents/workflows/`. With `--date`,
the runs are the records `<session>/workflows/wf_*.json` beside each matched `subagents/` whose
workflowName is `overnight` and whose args.date is that date, in the order their timestamps give.

**A call** is a `tool_use` block named Edit or Write, and is counted once per `tool_use_id`. A
transcript's `api-request-blob` lines replay earlier messages, blocks and all, so the same id appears
more than once; the line also gives the raw block count, replays included, because that is the figure
#109's grilling record called calls (210 on 2026-10-18, against 110 distinct ids).

**A refusal** is a `tool_result` block whose `tool_use_id` names an Edit or Write call of the run and
whose text holds "File has not been read yet" or "modified since read", counted once per id. The join
on the call is the rule that keeps a Bash result out: a session that reads #109 or a retro ticket
prints those words in a result that refused nothing. A refusal's agent is the label its
`agent-<id>.meta.json` gives (`implement:issue-102`), else its id.

Exits 0 when every run was counted, 1 when one was missing, 2 on a usage error. Standard library
only, under `python3 -I`. Test companion: tools/retro_refusals_tests.py.
"""

from __future__ import annotations

import glob
import json
import os
import re
import sys

DEFAULT_ROOT = os.path.expanduser("~/.claude/projects/-Users-adammedford-Projects-spa-platform/*/subagents/workflows/")
KINDS = ("File has not been read yet", "modified since read")
RUN_ID = re.compile(r"^wf_[0-9a-f]{8}-[0-9a-f]{3}$")
DATE = re.compile(r"^\d{4}-\d{2}-\d{2}$")
USAGE = "usage: retro_refusals.py RUN_ID... [--root GLOB] | --date YYYY-MM-DD [--root GLOB]"


def blocks(line: dict):
    """Every content block of a transcript line's message, whatever the line's type."""
    message = line.get("message")
    for item in message if isinstance(message, list) else [message]:
        if isinstance(item, dict) and isinstance(item.get("content"), list):
            for block in item["content"]:
                if isinstance(block, dict):
                    yield block


def text_of(content: object) -> str:
    if isinstance(content, list):
        return " ".join(str(part.get("text", "")) for part in content if isinstance(part, dict))
    return str(content)


def label_of(transcript: str, agent: str) -> str:
    try:
        with open(transcript[: -len(".jsonl")] + ".meta.json", encoding="utf-8") as handle:
            return json.load(handle).get("description") or agent
    except (OSError, ValueError, AttributeError):
        return agent


def count(run: str, root: str) -> tuple[int, str]:
    directories = sorted(d for d in glob.glob(os.path.join(root, run)) if os.path.isdir(d))
    transcripts = [t for d in directories for t in sorted(glob.glob(os.path.join(d, "agent-*.jsonl")))]
    if not transcripts:
        return 1, f"retro-refusals: {run}: missing: no subagent transcripts for this run under {root}"
    calls: dict[str, str] = {}
    raw = 0
    results: dict[str, tuple[str, str]] = {}
    for transcript in transcripts:
        agent = os.path.basename(transcript)[len("agent-"): -len(".jsonl")]
        label = label_of(transcript, agent)
        with open(transcript, encoding="utf-8") as handle:
            for text in handle:
                try:
                    line = json.loads(text)
                except ValueError:
                    continue
                if not isinstance(line, dict):
                    continue
                for block in blocks(line):
                    if block.get("type") == "tool_use" and block.get("name") in ("Edit", "Write"):
                        raw += 1
                        calls[block.get("id", "")] = block["name"]
                    elif block.get("type") == "tool_result":
                        body = text_of(block.get("content"))
                        for kind in KINDS:
                            if kind in body:
                                results.setdefault(block.get("tool_use_id", ""), (kind, label))
    refusals = {i: r for i, r in results.items() if i in calls}
    not_read = [i for i, (kind, _) in refusals.items() if kind == KINDS[0]]
    modified = [i for i, (kind, _) in refusals.items() if kind == KINDS[1]]
    edits = sum(1 for i in not_read if calls[i] == "Edit")
    agents = sorted({label for _, label in refusals.values()})
    return 0, (f"retro-refusals: {run}: {len(calls)} Edit and Write calls ({raw} tool_use blocks, replays included), "
               f'{len(not_read)} "{KINDS[0]}" ({edits} Edit, {len(not_read) - edits} Write), '
               f'{len(modified)} "{KINDS[1]}"; agents: {", ".join(agents) or "none"}')


def runs_on(date: str, root: str) -> list:
    """The overnight runs recorded for a date, oldest first."""
    found = {}
    for directory in glob.glob(root):
        records = os.path.join(os.path.dirname(os.path.dirname(os.path.normpath(directory))), "workflows")
        for path in glob.glob(os.path.join(records, "wf_*.json")):
            try:
                with open(path, encoding="utf-8") as handle:
                    record = json.load(handle)
            except (OSError, ValueError):
                continue
            if (isinstance(record, dict) and record.get("workflowName") == "overnight"
                    and isinstance(record.get("args"), dict) and record["args"].get("date") == date):
                run = record.get("runId") or os.path.basename(path)[: -len(".json")]
                found[run] = str(record.get("timestamp", ""))
    return sorted(found, key=lambda r: (found[r], r))


def main(argv: list) -> int:
    root, date, run_ids = DEFAULT_ROOT, None, []
    args = list(argv)
    while args:
        arg = args.pop(0)
        if arg in ("--root", "--date") and args:
            value = args.pop(0)
            if arg == "--root":
                root = value
            else:
                date = value
        elif RUN_ID.match(arg):
            run_ids.append(arg)
        else:
            print(f"{USAGE}\nretro-refusals: not an argument: {arg}", file=sys.stderr)
            return 2
    if (date is None) == (not run_ids) or (date is not None and not DATE.match(date)):
        print(USAGE, file=sys.stderr)
        return 2
    if date is not None:
        run_ids = runs_on(date, root)
        if not run_ids:
            print(f"retro-refusals: {date}: missing: no overnight run recorded for this date under {root}")
            return 1
    status = 0
    for run in run_ids:
        code, line = count(run, root)
        print(line)
        status = max(status, code)
    return status


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
