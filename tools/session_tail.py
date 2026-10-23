#!/usr/bin/env python3
"""Print the last turns of a Claude Code session's transcript that carry text. Owner: integrator.

    python3 -I tools/session_tail.py <session-id> [-n N] [--project PATH] [--projects-dir DIR]

A review or a morning often starts in a fresh session from what another session left, and the only
record of what that session meant is its transcript (#303). On 2026-10-22 a session recovered that
intent by parsing a transcript by hand, twice, and the second print ran to 18,000 characters, most
of it six task-notification blocks (#286). This script prints the end of the conversation and
nothing else.

Claude Code keeps one `<session-id>.jsonl` per session in `~/.claude/projects/<folder>/` (or under
$CLAUDE_CONFIG_DIR/projects), where `<folder>` is the path the session started in with every
character other than a letter or digit made a hyphen: /Users/adammedford/Projects/spa-platform is
-Users-adammedford-Projects-spa-platform, and its .claude/worktrees/roadmap worktree is
-Users-adammedford-Projects-spa-platform--claude-worktrees-roadmap. The path defaults to the current
directory, so run it from where the session ran, or name that path with --project. This layout and
the record shapes below are Claude Code's own, read off this repository's transcripts on 2026-10-22
rather than from a Claude Code document, so a Claude Code update can move them: a moved folder shows
as an unknown id, a renamed field as a missing or extra turn.

A turn is one line whose top-level `type` is `user` or `assistant`; the other record types
(`api-request-blob`, `attachment`, `queue-operation` and the rest) are bookkeeping, and an
`api-request-blob` carries an assistant role and text without being a turn. Of the turns:

1. Its text is the `content` string, or the `text` blocks of a `content` list. A turn whose blocks
   are all tool calls, tool results or thinking carries no text and is left out.
2. A task notification is left out: a `user` turn whose origin kind is `task-notification` or whose
   text begins `<task-notification>`.
3. A turn marked `isMeta` is left out: the harness injected it (a skill's body, a command's caveat).
4. An assistant reply written across several lines, one per block, is one turn: the text of
   consecutive lines with the same message id is joined.

The last N turns left (6 by default) print oldest first, each as a `--- <role> <timestamp> ---`
line, the role `human` or `assistant`, then its text, cut at 4,000 characters with a line saying how
many were dropped. An id with no transcript in the folder exits 1 and names the folder searched; a
usage error exits 2. tools/session_tail_tests.py holds each rule to a case.

Standard library only, kept 3.9-compatible (D-3 in docs/dependencies.md).
"""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import re
import sys
import typing

LIMIT = 4000
NOT_FOUND_EXIT = 1
NOTIFICATION_TAG = "<task-notification>"


def projects_dir() -> pathlib.Path:
    config = os.environ.get("CLAUDE_CONFIG_DIR")
    base = pathlib.Path(config) if config else pathlib.Path.home() / ".claude"
    return base / "projects"


def folder_name(project: str) -> str:
    return re.sub(r"[^A-Za-z0-9]", "-", project)


def turn_text(content: typing.Any) -> str:
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return "\n".join(block.get("text", "") for block in content
                         if isinstance(block, dict) and block.get("type") == "text")
    return ""


def is_notification(record: dict, text: str) -> bool:
    origin = record.get("origin")
    kind = origin.get("kind") if isinstance(origin, dict) else None
    return kind == "task-notification" or text.lstrip().startswith(NOTIFICATION_TAG)


def read_turns(path: pathlib.Path) -> list:
    """Each turn as [role, timestamp, text, message id], oldest first."""
    turns: list = []
    with path.open(encoding="utf-8", errors="replace") as lines:
        for line in lines:
            try:
                record = json.loads(line)
            except ValueError:
                continue
            if not isinstance(record, dict) or record.get("type") not in ("user", "assistant"):
                continue
            if record.get("isMeta"):
                continue
            message = record.get("message")
            if not isinstance(message, dict):
                continue
            text = turn_text(message.get("content"))
            if not text.strip():
                continue
            if record["type"] == "user" and is_notification(record, text):
                continue
            role = "human" if record["type"] == "user" else "assistant"
            msg_id = message.get("id")
            if (role == "assistant" and msg_id and turns and turns[-1][0] == "assistant"
                    and turns[-1][3] == msg_id):
                turns[-1][2] += "\n" + text
                continue
            turns.append([role, str(record.get("timestamp", "")), text, msg_id])
    return turns


def render(turns: list) -> str:
    out = []
    for role, timestamp, text, _ in turns:
        body = text
        if len(text) > LIMIT:
            body = text[:LIMIT] + f"\n[{len(text) - LIMIT} characters dropped]"
        out.append(f"--- {role} {timestamp} ---\n{body}\n")
    return "\n".join(out)


def main(argv: typing.Sequence[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("session", help="the session id, the transcript's file name without .jsonl")
    parser.add_argument("-n", type=int, default=6, help="how many text turns to print (6)")
    parser.add_argument("--project", default=os.getcwd(),
                        help="the path the session started in (the current directory)")
    parser.add_argument("--projects-dir", type=pathlib.Path, default=None,
                        help="Claude Code's projects folder (~/.claude/projects)")
    args = parser.parse_args(argv)
    if args.n < 1:
        parser.error("-n must be at least 1")
    if not re.fullmatch(r"[A-Za-z0-9-]+", args.session):
        parser.error(f"{args.session!r} is not a session id")

    folder = (args.projects_dir or projects_dir()) / folder_name(args.project)
    path = folder / f"{args.session}.jsonl"
    if not path.is_file():
        print(f"session_tail: no transcript for session {args.session} in {folder}",
              file=sys.stderr)
        return NOT_FOUND_EXIT

    sys.stdout.write(render(read_turns(path)[-args.n:]))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
