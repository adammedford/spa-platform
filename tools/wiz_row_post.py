#!/usr/bin/env python3
"""Draft a continuity row's post of the `wiz` skill's sitting D from its bundle. Owner: integrator.

    python3 -I tools/wiz_row_post.py <n> --bundle "$E" [--evidence <file>] [--junit <file>] [--test-output <file>]

Built in #1204. Each of rows 3 to 9 posts `## D<n>, row n` on #348 from the row's bundle, and in
sitting D of 2026-11-14 every one was an inline Python read of the evidence file, a `shasum` and a
thirty-line heredoc, with the session ids taken out by hand. This prints the draft on stdout:

- the heading `## D<n>, row <n>: <verdict>`, where the verdict is `passed` only for the evidence
  file's `Passed`; `Failed` gives `failed` and `NotProved` gives `not proved`. A verdict that is
  none of the three is refused (exit 2), so a draft never calls a row passed on a guess;
- the evidence file's verdict and reason, its attempts and its wall time, and each of its
  observations;
- the SHA-256 of the evidence file, the junit report and the test's output, each named by its path
  inside the bundle, or by its file name alone when it lies outside it.

The files are found in the bundle as sitting D leaves them: `continuity/row<n>.json`, the one
`*.junit.xml` under `results-row<n>/`, and `row<n>-test.out`. --evidence, --junit and --test-output
name another file instead; a directory holding no junit report, or more than one, needs --junit.
The evidence file's shape is `RowEvidence` in tests/e2e/Stack/ContinuityEvidence.cs.

The draft is the part of the post the files hold. The command, the lever's output lines (D4, D6 and
D7) and the task ids `deploy.sh` named are the sitting's to add, and the sitting reads the whole
draft once before it posts.

It prints no account id, ARN or session id. A session id here is a `session_ref`, the twelve hex
characters contracts/telemetry.md defines, which the observer writes after the word `session` in a
reason or an observation (`session a1b2c3d4e5f6 refreshed once`, `session <ref>,<ref> with 1
Refreshed lines`): the ids are left out and the word kept (`session refreshed once`). Everything the
evidence file gives then goes through tools/wiz_redact.py's rules, so an account id or an ARN in an
exception's message is redacted as every other post is. A task id, thirty-two hex characters, is
kept whole.

Exit status: 0 with the draft on stdout; 2, with nothing on stdout and the reason on stderr, for a
usage error, a row outside 3 to 9, a missing or unreadable file, an evidence file of another row or
one that is not JSON, or a verdict it does not know.

Run its companion: python3 -I tools/wiz_row_post_tests.py. Standard library only, kept
3.9-compatible (D-3 in docs/dependencies.md).
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import pathlib
import re
import sys

USAGE_EXIT = 2

HEADINGS = {"Passed": "passed", "Failed": "failed", "NotProved": "not proved"}

# `session ` and one or more session_refs, comma separated: the twelve hex characters of
# contracts/telemetry.md, as a whole word, so a 32-character task id never matches.
SESSION_REFS = re.compile(r"\bsession (?:[0-9a-f]{12}(?![0-9A-Za-z]))(?:,[0-9a-f]{12}(?![0-9A-Za-z]))*")


class Refused(Exception):
    pass


def load_redact():
    """tools/wiz_redact.py's redact(), loaded by path: under -I the script's directory is not on sys.path."""
    path = pathlib.Path(__file__).resolve().parent / "wiz_redact.py"
    spec = importlib.util.spec_from_file_location("wiz_redact", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.redact


def clean(text: str, redact) -> str:
    return redact(SESSION_REFS.sub("session", text))


def shown(path: pathlib.Path, bundle: pathlib.Path) -> str:
    try:
        return path.resolve().relative_to(bundle.resolve()).as_posix()
    except ValueError:
        return path.name


def sha256(path: pathlib.Path) -> str:
    try:
        return hashlib.sha256(path.read_bytes()).hexdigest()
    except OSError as error:
        raise Refused(f"{path} cannot be read: {error.strerror}") from error


def junit_in(bundle: pathlib.Path, n: int) -> pathlib.Path:
    results = bundle / f"results-row{n}"
    found = sorted(results.glob("*.junit.xml")) if results.is_dir() else []
    if len(found) != 1:
        raise Refused(
            f"{results} holds {len(found)} junit reports (*.junit.xml), not one; name the row's with --junit")
    return found[0]


def draft(n: int, bundle: pathlib.Path, evidence: pathlib.Path, junit: pathlib.Path, output: pathlib.Path) -> str:
    try:
        doc = json.loads(evidence.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, ValueError) as error:
        raise Refused(f"{evidence} is not a JSON evidence file: {error}") from error
    if not isinstance(doc, dict):
        raise Refused(f"{evidence} is not a JSON evidence file: it holds no object")
    if doc.get("Row") != f"row{n}":
        raise Refused(f"{evidence} is the evidence of {doc.get('Row')!r}, not row{n}")
    verdict = doc.get("Verdict")
    if verdict not in HEADINGS:
        raise Refused(f"{evidence} holds the verdict {verdict!r}, which is none of {', '.join(HEADINGS)}")
    attempts, wall = doc.get("Attempts"), doc.get("WallSeconds")
    observations = doc.get("Observations") or []
    if not isinstance(attempts, int) or not isinstance(wall, (int, float)) or not isinstance(observations, list):
        raise Refused(f"{evidence} lacks the Attempts, WallSeconds or Observations of a RowEvidence")

    redact = load_redact()
    lines = [
        f"## D{n}, row {n}: {HEADINGS[verdict]}",
        "",
        f"- Evidence file verdict: `{verdict}`, \"{clean(str(doc.get('Reason', '')), redact)}\".",
        f"- Attempts spent: {attempts}. Wall time: {wall:.1f} s.",
        "",
    ]
    if observations:
        lines += ["Observations:", ""]
        lines += [f"- \"{clean(str(each), redact)}\"" for each in observations]
    else:
        lines.append("Observations: none.")
    lines += ["", "Bundle (not uploaded; SHA-256):", ""]
    lines += [f"- `{clean(shown(path, bundle), redact)}` {sha256(path)}" for path in (evidence, junit, output)]
    return "\n".join(lines) + "\n"


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="wiz_row_post.py",
                                     description="Draft a continuity row's post from its bundle.")
    parser.add_argument("row", type=int, help="the row, 3 to 9")
    parser.add_argument("--bundle", required=True, type=pathlib.Path, help="the sitting's bundle directory, $E")
    parser.add_argument("--evidence", type=pathlib.Path, help="the evidence file, if not continuity/row<n>.json")
    parser.add_argument("--junit", type=pathlib.Path, help="the junit report, if not the one under results-row<n>/")
    parser.add_argument("--test-output", type=pathlib.Path, help="the test's output, if not row<n>-test.out")
    args = parser.parse_args(argv)
    n, bundle = args.row, args.bundle
    try:
        if not 3 <= n <= 9:
            raise Refused(f"row {n} is not a continuity row with a post of this shape: rows 3 to 9 are")
        evidence = args.evidence or bundle / "continuity" / f"row{n}.json"
        junit = args.junit or junit_in(bundle, n)
        output = args.test_output or bundle / f"row{n}-test.out"
        text = draft(n, bundle, evidence, junit, output)
    except Refused as refused:
        sys.stderr.write(f"wiz_row_post: {refused}; nothing was drafted\n")
        return USAGE_EXIT
    sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
