#!/usr/bin/env python3
"""The analysis guard: what ADR-0035 asks of the analyzer configs and of every suppression, that no
build can see. Owner: integrator. Built in #606; tools/check.sh runs it in both modes.

    python3 -I tools/analysis_guard.py

It reads the tracked files of the repository it runs in, from the top level, and fails (exit 1),
naming each finding on its own line before a count, when:

- the relaxed tier's section of the root `.editorconfig`, the one headed
  `[{tests,fixtures,tools/spactl,tools/bench/floor,src/Platform.Testing}/**.cs]`, is missing, names a
  security rule, has an entry with no reason comment, or is not the last section that matches a C#
  file in the relaxed tier (#589-Q8; ADR-0035 decisions 5 and 13). A later section that matches one
  would override it, and the build would stay green while a relaxation, or a strict rule, silently
  changed;
- any analyzer config, an `.editorconfig`, `.globalconfig` or `*.globalconfig` at any depth, has a
  `dotnet_analyzer_diagnostic.category-*` line (X4; decision 5): under `AnalysisMode=All` it adds
  nothing, and its reach into a package's rules that share the category name is undocumented;
- a `[SuppressMessage]` has no `Justification`, or one that cites no id (decision 12);
- a `#pragma warning disable` cites no id on its own line (decision 12);
- any `GlobalSuppressions.cs` exists (#594-Q7; decision 4);
- the root `.globalconfig` is not tracked: `Directory.Build.props` switches analysis on only where
  it is (so that an image build copying the props without it publishes under the SDK's defaults),
  and a tree without it would build green with no analysis at all.

An id is an evidence id (`S-`, `R-`), a test id (`F-`, `N-`, `P-`, `A-`), an issue (`#N`) or an ADR
(`ADR-NNNN`). Whether the citation justifies the suppression is review's to judge, not this file's
(CODING_STANDARDS.md, "A suppression answers to its citation").

**A reason comment** is a comment on the entry's own line, after the value, or the comment line
directly above it, with text in it. Two entries sharing one comment fail on the second: each entry
carries its own reason, so deleting one never orphans another's.

**A security rule** is one in an analyzer's `Security` category, never a prefix (decision 4):
SECURITY_IDS below is the SDK's 94 at 10.0.401, read from the SDK's own
`analysislevelsecurity_10_all.globalconfig`, plus ASP.NET Core's ASP0026 and Meziantou's MA0009 and
MA0039. Moving the `10-all` pin to a new .NET major is a new ADR (Status, route 2), and that change
refreshes this list; an admitted package's `Security` ids join it with the package.

Only tracked files are read, as the push carries them, and nothing under `spikes/`, which is outside
the solution and outside ADR-0035 (decision 1). A git that cannot list the files, or a file that
cannot be read, exits 2: a check fails closed (CODING_STANDARDS.md).
"""
from __future__ import annotations

import pathlib
import re
import subprocess
import sys

ME = "analysis guard"

RELAXED_HEADER = "[{tests,fixtures,tools/spactl,tools/bench/floor,src/Platform.Testing}/**.cs]"

# A file in each relaxed folder, at two depths: a section that matches one comes after the relaxed
# section only to override it.
RELAXED_PROBES = [
    f"{folder}/{rest}"
    for folder in ("tests", "fixtures", "tools/spactl", "tools/bench/floor", "src/Platform.Testing")
    for rest in ("A.cs", "Project/A.cs", "Project/Folder/A.cs")
]

SECURITY_IDS = frozenset("""
CA2100 CA2119 CA2153 CA2300 CA2301 CA2302 CA2305 CA2310 CA2311 CA2312 CA2315 CA2321 CA2322 CA2326
CA2327 CA2328 CA2329 CA2330 CA2350 CA2351 CA2352 CA2353 CA2354 CA2355 CA2356 CA2361 CA2362 CA3001
CA3002 CA3003 CA3004 CA3005 CA3006 CA3007 CA3008 CA3009 CA3010 CA3011 CA3012 CA3061 CA3075 CA3076
CA3077 CA3147 CA5350 CA5351 CA5358 CA5359 CA5360 CA5361 CA5362 CA5363 CA5364 CA5365 CA5366 CA5367
CA5368 CA5369 CA5370 CA5371 CA5372 CA5373 CA5374 CA5375 CA5376 CA5377 CA5378 CA5379 CA5380 CA5381
CA5382 CA5383 CA5384 CA5385 CA5386 CA5387 CA5388 CA5389 CA5390 CA5391 CA5392 CA5393 CA5394 CA5395
CA5396 CA5397 CA5398 CA5399 CA5400 CA5401 CA5402 CA5403 CA5404 CA5405
ASP0026
MA0009 MA0039
""".split())

CITATION = re.compile(r"(?<![A-Za-z0-9_-])(?:[SR]-[A-Z0-9]+|[FNPA]-[0-9]+|ADR-[0-9]+)(?![A-Za-z0-9_])"
                      r"|(?<![A-Za-z0-9_&])#[0-9]+\b")
SEVERITY = re.compile(r"^\s*dotnet_diagnostic\.([A-Za-z0-9_]+)\.severity\s*=", re.IGNORECASE)
CATEGORY = re.compile(r"^\s*dotnet_analyzer_diagnostic\.category-", re.IGNORECASE)
SECTION = re.compile(r"^\s*(\[.*\])\s*(?:[#;].*)?$")
COMMENT = re.compile(r"^\s*[#;]")
TRAILING_REASON = re.compile(r"\s[#;]\s*\S")
ATTRIBUTE = re.compile(r"\b(?:Unconditional)?SuppressMessage(?:Attribute)?\s*\(")
JUSTIFICATION = re.compile(r"\bJustification\s*=\s*")
PRAGMA = re.compile(r"^\s*#\s*pragma\s+warning\s+disable\b")


class NotJudged(Exception):
    pass


def tracked_files(top: pathlib.Path) -> list[str]:
    try:
        listed = subprocess.run(["git", "-C", str(top), "ls-files", "-z"], capture_output=True, check=True)
    except (OSError, subprocess.CalledProcessError) as error:
        raise NotJudged(f"git ls-files failed: {error}") from error
    paths = [p for p in listed.stdout.decode("utf-8", "surrogateescape").split("\0") if p]
    return [p for p in paths if not p.startswith("spikes/")]


def read(top: pathlib.Path, path: str) -> str:
    try:
        return (top / path).read_text(encoding="utf-8-sig", errors="surrogateescape")
    except OSError as error:
        raise NotJudged(f"could not read {path}: {error}") from error


# --- editorconfig globs ----------------------------------------------------------------------

def glob_to_regex(glob: str) -> re.Pattern[str]:
    """An editorconfig section glob as a regular expression over a path relative to the file's
    directory: `*` stays within a segment, `**` crosses them, `{a,b}` is a choice, and a glob with
    no `/` matches a file name at any depth (https://spec.editorconfig.org/#glob-expressions)."""
    out: list[str] = []
    depth = 0
    i = 0
    while i < len(glob):
        c = glob[i]
        if c == "*":
            if glob.startswith("**", i):
                out.append(".*")
                i += 2
                continue
            out.append("[^/]*")
        elif c == "?":
            out.append("[^/]")
        elif c == "[":
            end = glob.find("]", i + 1)
            if end == -1:
                out.append(re.escape(c))
            else:
                body = glob[i + 1:end]
                if body.startswith("!"):
                    body = "^" + body[1:]
                out.append(f"[{body}]")
                i = end
        elif c == "{":
            depth += 1
            out.append("(?:")
        elif c == "}" and depth:
            depth -= 1
            out.append(")")
        elif c == "," and depth:
            out.append("|")
        elif c == "\\" and i + 1 < len(glob):
            i += 1
            out.append(re.escape(glob[i]))
        else:
            out.append(re.escape(c))
        i += 1
    pattern = "".join(out)
    if "/" in glob:
        pattern = pattern[2:] if pattern.startswith("\\/") else pattern
        return re.compile(f"^{pattern}$")
    return re.compile(f"^(?:.*/)?{pattern}$")


def sections(text: str) -> list[tuple[str, int, list[tuple[int, str]]]]:
    """Each section of an .editorconfig: its header, its line number, and its lines."""
    found: list[tuple[str, int, list[tuple[int, str]]]] = []
    for number, line in enumerate(text.splitlines(), start=1):
        header = SECTION.match(line)
        if header and not COMMENT.match(line):
            found.append((header.group(1), number, []))
        elif found:
            found[-1][2].append((number, line))
    return found


def judge_relaxed_section(path: str, text: str, findings: list[str]) -> None:
    every = sections(text)
    at = [n for n, (header, _, _) in enumerate(every) if header == RELAXED_HEADER]
    if not at:
        findings.append(f"{path}: no relaxed-tier section {RELAXED_HEADER}; the relaxations live there "
                        "and nowhere else (ADR-0035 decision 5).")
        return
    header, header_line, lines = every[at[-1]]
    for later, line, _ in every[at[-1] + 1:]:
        matcher = glob_to_regex(later[1:-1])
        if any(matcher.match(probe) for probe in RELAXED_PROBES):
            findings.append(f"{path}:{header_line}: the relaxed-tier section is not the last section matching "
                            f"*.cs: {later} at line {line} matches the relaxed tier's files and overrides it "
                            "(ADR-0035 decision 5).")
    previous = ""
    for number, line in lines:
        entry = SEVERITY.match(line)
        if entry:
            diagnostic = entry.group(1).upper()
            if diagnostic in SECURITY_IDS:
                findings.append(f"{path}:{number}: {diagnostic} is a security rule, which is never relaxed for a "
                                "tier; fix it or suppress it at its site (ADR-0035 decision 4).")
            reason_above = COMMENT.match(previous) and previous.strip().lstrip("#;").strip()
            if not reason_above and not TRAILING_REASON.search(line):
                findings.append(f"{path}:{number}: {diagnostic} has no reason comment, on its line or the line "
                                "directly above (ADR-0035 decision 13).")
        previous = line


def judge_category_lines(path: str, text: str, findings: list[str]) -> None:
    for number, line in enumerate(text.splitlines(), start=1):
        if CATEGORY.match(line):
            findings.append(f"{path}:{number}: a dotnet_analyzer_diagnostic.category-* line; severities are "
                            "per id only (ADR-0035 decision 5).")


def is_analyzer_config(path: str) -> bool:
    name = path.rsplit("/", 1)[-1]
    return name in (".editorconfig", ".globalconfig") or name.endswith(".globalconfig")


# --- C# --------------------------------------------------------------------------------------

def mask(source: str) -> str:
    """The source with every comment and the inside of every string and character literal blanked
    to spaces, newlines kept, so that offsets and line numbers match the original."""
    out = list(source)
    i = 0
    n = len(source)

    def blank(start: int, end: int) -> None:
        for k in range(start, min(end, n)):
            if out[k] != "\n":
                out[k] = " "

    while i < n:
        c = source[i]
        if source.startswith("//", i):
            end = source.find("\n", i)
            end = n if end == -1 else end
            blank(i, end)
            i = end
        elif source.startswith("/*", i):
            end = source.find("*/", i + 2)
            end = n if end == -1 else end + 2
            blank(i, end)
            i = end
        elif c == '"' or (c in "@$" and source.startswith('"', i + 1)) or source.startswith(('$@"', '@$"'), i):
            start = i
            while source[i] in "@$":
                i += 1
            verbatim = "@" in source[start:i]
            quotes = 0
            while i + quotes < n and source[i + quotes] == '"':
                quotes += 1
            if quotes >= 3:
                close = source.find('"' * quotes, i + quotes)
                end = n if close == -1 else close + quotes
                blank(i + quotes, end - quotes)
                i = end
                continue
            i += 1
            body = i
            while i < n:
                if verbatim and source.startswith('""', i):
                    i += 2
                elif not verbatim and source[i] == "\\":
                    i += 2
                elif source[i] == '"' or (not verbatim and source[i] == "\n"):
                    break
                else:
                    i += 1
            blank(body, i)
            i += 1
        elif c == "'":
            end = i + 1
            while end < n and source[end] not in "'\n":
                end += 2 if source[end] == "\\" else 1
            blank(i + 1, end)
            i = end + 1
        else:
            i += 1
    return "".join(out)


def string_at(source: str, masked: str, start: int) -> str | None:
    """The contents of the string literal that starts at `start`, or None when none starts there."""
    i = start
    while i < len(source) and source[i] in "@$":
        i += 1
    if i >= len(source) or source[i] != '"':
        return None
    j = i
    while j < len(source) and source[j] == '"':
        j += 1
    quotes = j - i if j - i >= 3 else 1
    body = i + quotes
    end = body
    while end < len(masked) and not (masked[end] == '"' and source[end] == '"'):
        end += 1
    return source[body:end]


def closing_paren(masked: str, opening: int) -> int:
    depth = 0
    for k in range(opening, len(masked)):
        if masked[k] == "(":
            depth += 1
        elif masked[k] == ")":
            depth -= 1
            if depth == 0:
                return k
    return len(masked)


def judge_csharp(path: str, source: str, findings: list[str]) -> None:
    masked = mask(source)
    for found in ATTRIBUTE.finditer(masked):
        line = masked.count("\n", 0, found.start()) + 1
        opening = found.end() - 1
        arguments = slice(opening, closing_paren(masked, opening) + 1)
        justification = JUSTIFICATION.search(masked[arguments])
        if not justification:
            findings.append(f"{path}:{line}: a [SuppressMessage] with no Justification; it cites the id that "
                            "makes the code right (ADR-0035 decision 12).")
            continue
        text = string_at(source, masked, opening + justification.end())
        if text is None or not CITATION.search(text):
            findings.append(f"{path}:{line}: a [SuppressMessage] whose Justification cites no id (S-, R-, F-, N-, "
                            "P-, A-, #N or ADR-) (ADR-0035 decision 12).")
    original = source.splitlines()
    for number, line in enumerate(masked.splitlines(), start=1):
        if PRAGMA.match(line) and not CITATION.search(original[number - 1].split("disable", 1)[1]):
            findings.append(f"{path}:{number}: a #pragma warning disable with no id cited on its line (S-, R-, F-, "
                            "N-, P-, A-, #N or ADR-) (ADR-0035 decision 12).")


def judge(top: pathlib.Path) -> list[str]:
    findings: list[str] = []
    paths = tracked_files(top)
    for path in paths:
        name = path.rsplit("/", 1)[-1]
        if name == "GlobalSuppressions.cs":
            findings.append(f"{path}: a GlobalSuppressions.cs; a suppression is per site, never assembly-wide "
                            "(ADR-0035 decisions 4 and 12).")
        if is_analyzer_config(path):
            text = read(top, path)
            judge_category_lines(path, text, findings)
            if path == ".editorconfig":
                judge_relaxed_section(path, text, findings)
        elif name.endswith(".cs"):
            judge_csharp(path, read(top, path), findings)
    if ".editorconfig" not in paths:
        findings.append(f".editorconfig: no root .editorconfig, so no relaxed-tier section {RELAXED_HEADER}.")
    if ".globalconfig" not in paths:
        findings.append(".globalconfig: no root .globalconfig; Directory.Build.props switches analysis on only "
                        "where it is, so the build would run without analysis (ADR-0035 decision 5).")
    return findings


def main() -> int:
    try:
        top = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True)
        findings = judge(pathlib.Path(top.stdout.strip()))
    except (OSError, subprocess.CalledProcessError, NotJudged) as error:
        print(f"{ME}: not judged: {error}", file=sys.stderr)
        return 2
    for finding in findings:
        print(f"{ME}: {finding}", file=sys.stderr)
    if findings:
        count = len(findings)
        print(f"{ME}: {count} finding{'' if count == 1 else 's'}.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
