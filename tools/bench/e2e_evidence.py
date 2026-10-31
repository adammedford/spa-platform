#!/usr/bin/env python3
"""Draft one evidence comment per test id from a deployed e2e run's junit report (#469). Owner: the
owner OWNERSHIP.md names for tools/bench/.

    python3 -I tools/bench/e2e_evidence.py <junit.xml> --out <dir> --commit <sha> --context <file>

#63's Q75 asks for one comment per test id, drawn from the junit report of
`SPA_E2E_STACK=deployed dotnet test --project tests/e2e --report-xunit-junit`. That report holds the
Functional and Negative gate tests and, beside them, the `Deployed/*` meta-tests, some of which carry
ids in their data. Twice a session tallied ids by hand and counted meta-tests: on 2026-10-24 N-07 was
given 5 where its gate tests were 3, and a draft of 2026-10-27 took an "N-28", which is no row, from
DeployedSubsetTests' InlineData. So:

**What counts.** Only a testcase whose `classname` is in `Gateway.E2ETests.Functional.` or
`Gateway.E2ETests.Negative.`, the namespaces of tests/e2e/Functional and tests/e2e/Negative, or the
one registered Deployed class below. The class decides, never the test's name or data. In
Functional and Negative the id is the method name's prefix, `F06_` meaning F-06, and its letter must
be its class's: F in Functional, N in Negative. Every other testcase is a meta-test, counted in the
totals and never toward an id.

**The one Deployed class that counts.** `Gateway.E2ETests.Deployed.ContinuityTests` (tests/e2e/Deployed/
ContinuityTests.cs) holds the deployed continuity Facts, which contracts/test-ids.md registers as the
test of F-19 to F-23 and N-32. A testcase of exactly that class counts toward the id whose register
row names its method as `ContinuityTests.<method>` beside that file's path, and toward no other. The
method is read from the register, never from its name's prefix (`Row3_` is no id), and a method of the
class that no row names (Row7_) stays a meta-test. Two rows naming one method are refused.

**What an id is.** A row of a table in docs/plan/04-tests.md or contracts/test-ids.md, read from the
row's first cell only: test-ids.md names "N-33" in prose as the next number, which defines nothing.

**Outcome.** A testcase with a `failure` or `error` child failed, one with `skipped` was skipped, and
any other passed. An id passed only when every one of its tests passed, so a skipped gate test leaves
its id FAILED: a skip proves nothing about the row (CODING_STANDARDS.md, a test that skips).

**What a draft holds.** `<out>/<id>.md`: the heading `## <id>, deployed, at `<commit>`: passed` or
`FAILED`, the --context file's text (which names the deploy), the run's totals split into gate tests
and meta-tests, each of the id's tests with its class, name, outcome and time, and the report's file
name and SHA-256. Not a failure's message: it is free text from the system under test, and the
ticket's draft does not carry it. The report's directory is left out too, since it is a path on one
machine.

**What it refuses (exit 2, naming the test or line, writing no draft).** A gate test with no id
prefix, or with an id neither register defines; a report that does not parse, or holds no gate test;
a register file it cannot read; and a --context line or gate-test name carrying an AWS account id, an
ARN or an OAuth client id. The draft is posted to a ticket, so these are refused rather than
rewritten, and the refusal names where, never the value. A client id is matched by its key
(`client_id=`, `ClientId`) or by the bare shape of a Cognito app client id, 26 lowercase letters and
digits, as tools/scripts/cell_doubles.py's stand-ins have it.

**What it prints.** One line per id, ids in number order: `N-07: 3 tests, 3 passed, 0 failed,
0 skipped: passed`. It exits 0 when every counted test passed and 1 when any did not.

Standard library only, kept 3.9-compatible (D-3 in docs/dependencies.md).
"""

from __future__ import annotations

import argparse
import hashlib
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent
REGISTERS = ("docs/plan/04-tests.md", "contracts/test-ids.md")

GATE_CLASSES = {"Gateway.E2ETests.Functional.": "F", "Gateway.E2ETests.Negative.": "N"}
PREFIX = re.compile(r"([FN])(\d{2,})_")
ROW = re.compile(r"^\|\s*([FNPA]-\d{2,})\s*\|", re.MULTILINE)
CONTINUITY_CLASS = "Gateway.E2ETests.Deployed.ContinuityTests"
CONTINUITY_FILE = "`tests/e2e/Deployed/ContinuityTests.cs`"
CONTINUITY_REFERENCE = re.compile(r"`ContinuityTests\.([A-Za-z_][A-Za-z0-9_]*)`")
COMMIT = re.compile(r"[0-9a-f]{7,40}")

# Shapes that must not reach a ticket, the ARN before the account id inside it. The account id
# and ARN shapes are tools/wiz_redact.py's.
SENSITIVE = (
    ("an ARN", re.compile(r"\barn:aws[\w-]*:")),
    ("an account id", re.compile(r"(?<!\d)\d{12}(?!\d)|(?<![\d-])\d{4}-\d{4}-\d{4}(?![\d-])")),
    ("a client id", re.compile(r"client_?id\s*[=:\"]", re.IGNORECASE)),
    ("a client id", re.compile(r"(?<![A-Za-z0-9])(?=[a-z]*\d)(?=\d*[a-z])[a-z0-9]{26}(?![A-Za-z0-9])")),
)


class Refusal(Exception):
    pass


def sensitive(text: str) -> str | None:
    for kind, pattern in SENSITIVE:
        if pattern.search(text):
            return kind
    return None


def read_register(root: pathlib.Path) -> tuple:
    """The defined ids, and the continuity Fact each row names: {method: id}."""
    defined, continuity = set(), {}
    for name in REGISTERS:
        try:
            text = (root / name).read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError) as error:
            raise Refusal(f"cannot read the id register {name}: {error}") from None
        defined.update(ROW.findall(text))
        for line in text.splitlines():
            row = ROW.match(line)
            if row is None or CONTINUITY_FILE not in line:
                continue
            reference = CONTINUITY_REFERENCE.search(line)
            if reference is None:
                continue
            method, test_id = reference.group(1), row.group(1)
            if continuity.setdefault(method, test_id) != test_id:
                raise Refusal(f"{continuity[method]} and {test_id} both name ContinuityTests.{method}")
    return defined, continuity


class Test:
    def __init__(self, classname: str, method: str, outcome: str, time: float):
        self.classname, self.method, self.outcome, self.time = classname, method, outcome, time

    @property
    def full(self) -> str:
        return f"{self.classname}.{self.method}"

    @property
    def short(self) -> str:
        return f"{self.classname.rsplit('.', 1)[-1]}.{self.method}"


def read_report(path: pathlib.Path) -> list:
    try:
        tree = ET.parse(path)
    except (OSError, ET.ParseError) as error:
        raise Refusal(f"cannot read the report {path.name}: {error}") from None
    tests = []
    for element in tree.iter("testcase"):
        classname = element.get("classname", "")
        name = element.get("name", "")
        # The name is the class's full name, a dot, and the method with any theory row's arguments,
        # which may hold dots themselves, so the class is cut off the front rather than split on.
        method = name[len(classname) + 1:] if classname and name.startswith(classname + ".") else name
        if element.find("failure") is not None or element.find("error") is not None:
            outcome = "FAILED"
        elif element.find("skipped") is not None:
            outcome = "skipped"
        else:
            outcome = "passed"
        try:
            time = float(element.get("time", "0"))
        except ValueError:
            # The name only up to a theory row's arguments: the sensitive scan in count() has not
            # run yet, and an argument may carry an account id.
            raise Refusal(f"{classname}.{method.split('(')[0]}: its time is not a number") from None
        tests.append(Test(classname, method, outcome, time))
    return tests


def gate_letter(test: Test) -> str | None:
    for namespace, letter in GATE_CLASSES.items():
        if test.classname.startswith(namespace):
            return letter
    return None


def count(tests: list, defined: set, continuity: dict) -> tuple:
    by_id, meta = {}, []
    for test in tests:
        if test.classname == CONTINUITY_CLASS and test.method in continuity:
            by_id.setdefault(continuity[test.method], []).append(test)
            continue
        letter = gate_letter(test)
        if letter is None:
            meta.append(test)
            continue
        # First, so no refusal below prints a name that carries one.
        kind = sensitive(test.method)
        if kind:
            raise Refusal(f"{test.full.split('(')[0]}: its name carries what looks like {kind}")
        match = PREFIX.match(test.method)
        if match is None or match.group(1) != letter:
            raise Refusal(f"{test.full}: a {'Functional' if letter == 'F' else 'Negative'} test with no {letter}NN_ id prefix")
        test_id = f"{match.group(1)}-{match.group(2)}"
        if test_id not in defined:
            raise Refusal(f"{test.full}: {test_id} is not a row of {' or '.join(REGISTERS)}")
        by_id.setdefault(test_id, []).append(test)
    if not by_id:
        raise Refusal("the report holds no Functional, Negative or registered continuity test")
    return by_id, meta


def tally(tests: list) -> str:
    passed = sum(t.outcome == "passed" for t in tests)
    failed = sum(t.outcome == "FAILED" for t in tests)
    skipped = sum(t.outcome == "skipped" for t in tests)
    return f"{passed} passed, {failed} failed, {skipped} skipped"


def verdict(tests: list) -> str:
    return "passed" if all(t.outcome == "passed" for t in tests) else "FAILED"


def draft(test_id: str, tests: list, gate: list, meta: list, report: pathlib.Path, digest: str,
          commit: str, context: str) -> str:
    everyone = gate + meta
    passed = sum(t.outcome == "passed" for t in tests)
    lines = [
        f"## {test_id}, deployed, at `{commit}`: {verdict(tests)}",
        "",
        context.strip(),
        "",
        f"**Run.** {len(everyone)} tests in the report: {tally(everyone)}. Of those, {len(gate)} are "
        f"gate tests, Functional, Negative and registered continuity ({tally(gate)}), and {len(meta)} are meta-tests "
        f"({tally(meta)}), which count toward no id.",
        "",
        f"**{test_id}'s tests** ({passed} of {len(tests)} passed):",
        "",
    ]
    lines += [f"- `{t.short}`: {t.outcome}, {t.time:.1f}s" for t in tests]
    lines += ["", f"**Evidence.** `{report.name}`, SHA-256 `{digest}`.", ""]
    return "\n".join(lines)


def id_order(test_id: str) -> tuple:
    letter, number = test_id.split("-")
    return ("FNPA".index(letter), int(number))


def run(args: argparse.Namespace) -> int:
    if not COMMIT.fullmatch(args.commit):
        raise Refusal(f"--commit {args.commit!r} is not a commit sha (7 to 40 lowercase hex digits)")
    try:
        context = args.context.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as error:
        raise Refusal(f"cannot read the --context file {args.context.name}: {error}") from None
    if not context.strip():
        raise Refusal(f"the --context file {args.context.name} is empty; it names the deploy")
    for number, line in enumerate(context.splitlines(), 1):
        kind = sensitive(line)
        if kind:
            raise Refusal(f"the --context file {args.context.name}, line {number}: carries what looks like {kind}")

    defined, continuity = read_register(args.root)
    tests = read_report(args.report)
    by_id, meta = count(tests, defined, continuity)
    gate = [t for group in by_id.values() for t in group]
    try:
        digest = hashlib.sha256(args.report.read_bytes()).hexdigest()
    except OSError as error:
        raise Refusal(f"cannot read the report {args.report.name}: {error.strerror}") from None

    drafts = {test_id: draft(test_id, group, gate, meta, args.report, digest, args.commit, context)
              for test_id, group in by_id.items()}
    # A write that fails is a refusal (exit 2), never a traceback's exit 1, which reads as a gate
    # test that failed (CODING_STANDARDS.md, a check fails closed).
    try:
        args.out.mkdir(parents=True, exist_ok=True)
        for test_id in drafts:
            (args.out / f"{test_id}.md").write_text(drafts[test_id], encoding="utf-8")
    except OSError as error:
        raise Refusal(f"cannot write the drafts under --out {args.out.name}: {error.strerror}") from None
    for test_id in sorted(drafts, key=id_order):
        group = by_id[test_id]
        print(f"{test_id}: {len(group)} test{'' if len(group) == 1 else 's'}, {tally(group)}: {verdict(group)}")
    print(f"e2e-evidence: {len(gate)} gate tests ({tally(gate)}) under {len(by_id)} ids, "
          f"{len(meta)} meta-tests; drafts in {args.out}")
    return 0 if all(t.outcome == "passed" for t in gate) else 1


def main(argv: list) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("report", type=pathlib.Path, help="the junit report of the deployed e2e run")
    parser.add_argument("--out", type=pathlib.Path, required=True, help="the directory the drafts go in")
    parser.add_argument("--commit", required=True, help="the commit the run tested")
    parser.add_argument("--context", type=pathlib.Path, required=True, help="a file whose text names the deploy")
    parser.add_argument("--root", type=pathlib.Path, default=ROOT, help="the checkout the id registers are read from")
    args = parser.parse_args(argv)
    try:
        return run(args)
    except Refusal as refusal:
        print(f"e2e-evidence: refused: {refusal}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
