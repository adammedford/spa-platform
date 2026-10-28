#!/usr/bin/env python3
"""Test companion for tools/bench/e2e_evidence.py (#469). Owner: the owner OWNERSHIP.md names for
tools/bench/.

Every case builds its junit report in the test, in the shape `dotnet test --report-xunit-junit`
writes (testsuites > testsuite > testcase with name, time and classname, and a failure or skipped
child), and runs the script as a person does, by its path. The id register is the checkout's own
docs/plan/04-tests.md and contracts/test-ids.md wherever the case is about the real ids, so a change
to either file that breaks the reading fails here; the cases about an id no register defines use a
register of their own under --root, so they do not move when the integrator numbers a new id.

Checkout inputs: docs/plan/04-tests.md contracts/test-ids.md

Run: python3 -I tools/bench/e2e_evidence_tests.py
"""

from __future__ import annotations

import hashlib
import pathlib
import subprocess
import sys
import tempfile
import unittest
from xml.sax.saxutils import quoteattr

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent
SCRIPT = ROOT / "tools" / "bench" / "e2e_evidence.py"

FUNCTIONAL = "Gateway.E2ETests.Functional."
NEGATIVE = "Gateway.E2ETests.Negative."
DEPLOYED = "Gateway.E2ETests.Deployed."


def case(classname: str, method: str, time: str = "1.000000", outcome: str = "passed", message: str = "") -> str:
    attrs = f"name={quoteattr(classname + '.' + method)} time={quoteattr(time)} classname={quoteattr(classname)}"
    if outcome == "passed":
        return f"<testcase {attrs} />"
    if outcome == "failed":
        return f"<testcase {attrs}><failure message={quoteattr(message)}>{message}</failure></testcase>"
    if outcome == "skipped":
        return f"<testcase {attrs}><skipped /></testcase>"
    raise ValueError(outcome)


def report(cases: list) -> str:
    body = "".join(cases)
    n = len(cases)
    return (
        f'<testsuites name="Test results" tests="{n}" failures="0" errors="0" disabled="0" time="1.0" '
        f'timestamp="2026-10-24T21:49:05"><testsuite name="Gateway.E2ETests.dll" tests="{n}" failures="0" '
        f'errors="0" disabled="0" skipped="0" time="1.0" timestamp="2026-10-24T21:49:05" hostname="localhost">'
        f"{body}</testsuite></testsuites>"
    )


# The 2026-10-24 shape (#63's correction of 2026-10-27): N-07 has three gate tests, two of them one
# theory's rows, and DeployedSubsetTests carries "N-28" and "N-07" in its data. The ad hoc tally gave
# N-07 five; the gate tests are three.
OCT_24 = [
    case(NEGATIVE + "OriginTests", "N07_no_fetch_site_header_or_a_foreign_origin_is_403_origin_rejected_with_no_cors_header", "195.948000"),
    case(NEGATIVE + "OriginTests", 'N07_a_foreign_pages_calls_are_403_origin_rejected_with_no_cors_header_preflight_included(initiator: "sibling", fetchSite: "same-site")', "553.698000"),
    case(NEGATIVE + "OriginTests", 'N07_a_foreign_pages_calls_are_403_origin_rejected_with_no_cors_header_preflight_included(initiator: "elsewhere", fetchSite: "cross-site")', "129.734000"),
    case(NEGATIVE + "HostTests", "N08_an_unknown_host_and_an_app_host_on_the_wrong_port_are_404", "70.573000"),
    case(NEGATIVE + "HostTests", "N08_a_spoofed_forwarded_host_changes_nothing", "233.498000"),
    case(FUNCTIONAL + "ShellTests", "F06_an_extensionless_path_gets_the_shell_and_a_missing_asset_gets_404", "562.734000", "failed",
         "Assert.Equal() Failure: Values differ\nExpected: 404\nActual:   500"),
    case(FUNCTIONAL + "ShellTests", "F06_each_app_gets_its_own_policy_and_the_browser_enforces_it", "121.683000"),
    case(DEPLOYED + "DeployedSubsetTests", 'The_register_rows_are_read(row: "| N-28 | Forge one | 401 | H | #322 Q8 |")', "0.001000"),
    case(DEPLOYED + "DeployedSubsetTests", 'N07_shaped_meta_test_names_its_row(row: "| N-07 | Plant | D |")', "0.001000"),
    case(DEPLOYED + "DeployedSubsetTests", "Every_functional_test_starts_the_chosen_stack_f07_included", "0.001000"),
    case(DEPLOYED + "TokenShapeTests", "Text_with_no_jwt_passes_the_search", "0.005000"),
]

CONTEXT = "This is the C2 sitting of 2026-10-24, on one fresh `deploy.sh` of `0c0ffee`.\n"


class Run:
    def __init__(self, cases: list, context: str = CONTEXT, root: pathlib.Path = ROOT, extra: list = ()):
        self.dir = tempfile.TemporaryDirectory()
        base = pathlib.Path(self.dir.name)
        self.report = base / "adammedford_Adams-Mac-mini_2026-10-24_21_49_05.556590.junit.xml"
        self.report.write_text(report(cases), encoding="utf-8")
        self.context = base / "context.md"
        self.context.write_text(context, encoding="utf-8")
        self.out = base / "drafts"
        argv = [sys.executable, "-I", str(SCRIPT), str(self.report), "--out", str(self.out),
                "--commit", "9d8a001", "--context", str(self.context), "--root", str(root), *extra]
        done = subprocess.run(argv, capture_output=True, text=True, timeout=60)
        self.code, self.stdout, self.stderr = done.returncode, done.stdout, done.stderr

    def draft(self, test_id: str) -> str:
        return (self.out / f"{test_id}.md").read_text(encoding="utf-8")

    def drafts(self) -> list:
        return sorted(p.name for p in self.out.glob("*.md")) if self.out.exists() else []

    def close(self):
        self.dir.cleanup()


class TestCase(unittest.TestCase):
    def run_script(self, *args, **kwargs) -> Run:
        run = Run(*args, **kwargs)
        self.addCleanup(run.close)
        return run


def register(root: pathlib.Path, plan_rows: str, contract_rows: str = "") -> pathlib.Path:
    (root / "docs" / "plan").mkdir(parents=True)
    (root / "contracts").mkdir()
    (root / "docs" / "plan" / "04-tests.md").write_text(
        "| ID | Test | Expect | Tier | Gate |\n| --- | --- | --- | --- | --- |\n" + plan_rows, encoding="utf-8")
    (root / "contracts" / "test-ids.md").write_text(
        "**Numbering.** A new id continues its prefix's sequence: F-12, N-28, P-08 and A-06 are next.\n\n"
        "| ID | Attempt | Expect | Tier | Recorded by |\n| --- | --- | --- | --- | --- |\n" + contract_rows,
        encoding="utf-8")
    return root


class OnlyTheGateClassesCount(TestCase):
    def test_the_2026_10_24_shape_counts_n07_as_three_and_n08_as_two(self):
        run = self.run_script(OCT_24)
        self.assertEqual(1, run.code, run.stderr)
        self.assertIn("N-07: 3 tests, 3 passed, 0 failed, 0 skipped: passed", run.stdout)
        self.assertIn("N-08: 2 tests, 2 passed, 0 failed, 0 skipped: passed", run.stdout)
        self.assertIn("F-06: 2 tests, 1 passed, 1 failed, 0 skipped: FAILED", run.stdout)

    def test_a_meta_test_whose_data_names_n28_adds_no_row(self):
        run = self.run_script(OCT_24)
        self.assertEqual(["F-06.md", "N-07.md", "N-08.md"], run.drafts())
        self.assertNotIn("N-28", run.stdout)
        self.assertNotIn("DeployedSubsetTests", run.draft("N-07"))

    def test_a_meta_test_class_named_like_a_gate_class_is_not_one(self):
        # A Deployed test whose name says "functional" or "Negative" is still a meta-test: the
        # class decides, by its namespace.
        run = self.run_script([
            case(NEGATIVE + "HostTests", "N08_a_spoofed_forwarded_host_changes_nothing"),
            case("Gateway.E2ETests.Deployed.NegativeTests", "N07_looks_like_a_row"),
            case(DEPLOYED + "FunctionalLookalike", "F06_looks_like_a_row"),
        ])
        self.assertEqual(0, run.code, run.stderr)
        self.assertEqual(["N-08.md"], run.drafts())

    def test_n28_is_not_defined_by_the_register_prose_that_names_it_as_next(self):
        # contracts/test-ids.md names N-28 as the next number in a sentence. Only a table row
        # defines an id, so a gate test claiming N-28 is refused today.
        run = self.run_script([case(NEGATIVE + "CookiePlantingTests", "N28_plant_a_d_cookie")])
        self.assertEqual(2, run.code, run.stdout)
        self.assertIn("N-28", run.stderr)
        self.assertIn("CookiePlantingTests.N28_plant_a_d_cookie", run.stderr)
        self.assertEqual([], run.drafts())


class Refusals(TestCase):
    def test_a_gate_test_with_no_id_prefix_fails_the_run_naming_it(self):
        run = self.run_script(OCT_24 + [case(NEGATIVE + "HostTests", "an_unknown_host_is_404")])
        self.assertEqual(2, run.code, run.stdout)
        self.assertIn("Gateway.E2ETests.Negative.HostTests.an_unknown_host_is_404", run.stderr)
        self.assertEqual([], run.drafts())

    def test_a_prefix_of_the_other_class_is_no_id_prefix(self):
        run = self.run_script([case(NEGATIVE + "HostTests", "F06_in_the_negative_class")])
        self.assertEqual(2, run.code, run.stdout)
        self.assertIn("HostTests.F06_in_the_negative_class", run.stderr)

    def test_an_id_neither_register_defines_fails_naming_the_test(self):
        with tempfile.TemporaryDirectory() as d:
            root = register(pathlib.Path(d), "| N-07 | Foreign origin | 403 | H, D | 4 |\n")
            run = self.run_script([
                case(NEGATIVE + "OriginTests", "N07_a_foreign_origin_is_403"),
                case(NEGATIVE + "HostTests", "N08_an_unknown_host_is_404"),
            ], root=root)
        self.assertEqual(2, run.code, run.stdout)
        self.assertIn("N-08", run.stderr)
        self.assertIn("HostTests.N08_an_unknown_host_is_404", run.stderr)
        self.assertEqual([], run.drafts())

    def test_an_id_the_contract_register_defines_counts(self):
        with tempfile.TemporaryDirectory() as d:
            root = register(pathlib.Path(d), "| N-07 | Foreign origin | 403 | H, D | 4 |\n",
                            "| N-28 | Plant a D cookie | Refused | D | #322 Q8 |\n")
            run = self.run_script([case(NEGATIVE + "CookiePlantingTests", "N28_plant_a_d_cookie")], root=root)
        self.assertEqual(0, run.code, run.stderr)
        self.assertEqual(["N-28.md"], run.drafts())
        self.assertIn("N-28: 1 test, 1 passed, 0 failed, 0 skipped: passed", run.stdout)

    def test_a_register_file_that_is_missing_fails_the_run(self):
        with tempfile.TemporaryDirectory() as d:
            root = register(pathlib.Path(d), "| N-07 | Foreign origin | 403 | H, D | 4 |\n")
            (root / "contracts" / "test-ids.md").unlink()
            run = self.run_script([case(NEGATIVE + "OriginTests", "N07_a_foreign_origin_is_403")], root=root)
        self.assertEqual(2, run.code, run.stdout)
        self.assertIn("test-ids.md", run.stderr)

    def test_a_report_with_no_gate_test_fails_the_run(self):
        run = self.run_script([case(DEPLOYED + "TokenShapeTests", "Text_with_no_jwt_passes_the_search")])
        self.assertEqual(2, run.code, run.stdout)
        self.assertIn("no Functional or Negative test", run.stderr)

    def test_a_report_that_does_not_parse_fails_the_run(self):
        run = self.run_script([case(NEGATIVE + "HostTests", "N08_x")])
        run.report.write_text("<testsuites><testsuite>", encoding="utf-8")
        again = subprocess.run([sys.executable, "-I", str(SCRIPT), str(run.report), "--out", str(run.out),
                                "--commit", "9d8a001", "--context", str(run.context)],
                               capture_output=True, text=True, timeout=60)
        self.assertEqual(2, again.returncode, again.stdout)
        self.assertIn(run.report.name, again.stderr)

    def test_a_context_naming_an_account_an_arn_or_a_client_id_is_refused_without_printing_it(self):
        for line, kind in [
            ("Deployed into 123456789012 today.", "account id"),
            ("The role is arn:aws:iam::210987654321:role/spa-platform-poc-task.", "ARN"),
            ("app1's client is 1example23456789app1client.", "client id"),
            ("Called with client_id=app1-client.", "client id"),
        ]:
            with self.subTest(kind=kind):
                run = self.run_script(OCT_24, context="First line.\n" + line + "\n")
                self.assertEqual(2, run.code, run.stdout)
                self.assertIn("line 2", run.stderr)
                self.assertIn(kind, run.stderr)
                for value in ("123456789012", "210987654321", "1example23456789app1client", "app1-client"):
                    self.assertNotIn(value, run.stderr + run.stdout)
                self.assertEqual([], run.drafts())

    def test_a_gate_test_name_carrying_an_account_id_is_refused_naming_the_test_not_the_value(self):
        run = self.run_script([case(NEGATIVE + "HostTests", 'N08_a_host_is_404(account: "123456789012")')])
        self.assertEqual(2, run.code, run.stdout)
        self.assertIn("HostTests.N08_a_host_is_404", run.stderr)
        self.assertNotIn("123456789012", run.stderr + run.stdout)


class Drafts(TestCase):
    def test_a_draft_carries_heading_totals_tests_report_and_context(self):
        run = self.run_script(OCT_24)
        n07 = run.draft("N-07")
        self.assertTrue(n07.startswith("## N-07, deployed, at `9d8a001`: passed\n"), n07)
        self.assertIn(CONTEXT.strip(), n07)
        # 11 tests: 7 gate tests (6 passed, 1 failed) and 4 meta-tests (all passed).
        self.assertIn("11 tests in the report: 10 passed, 1 failed, 0 skipped.", n07)
        self.assertIn("7 are Functional and Negative gate tests (6 passed, 1 failed, 0 skipped)", n07)
        self.assertIn("4 are meta-tests (4 passed, 0 failed, 0 skipped)", n07)
        self.assertIn("**N-07's tests** (3 of 3 passed):", n07)
        self.assertIn(
            '- `OriginTests.N07_a_foreign_pages_calls_are_403_origin_rejected_with_no_cors_header_preflight_included'
            '(initiator: "sibling", fetchSite: "same-site")`: passed, 553.7s', n07)
        digest = hashlib.sha256(run.report.read_bytes()).hexdigest()
        self.assertIn(f"`{run.report.name}`, SHA-256 `{digest}`", n07)
        self.assertNotIn(str(run.report.parent), n07)

    def test_a_failed_id_is_headed_failed_and_its_failure_message_is_not_copied(self):
        run = self.run_script(OCT_24)
        f06 = run.draft("F-06")
        self.assertTrue(f06.startswith("## F-06, deployed, at `9d8a001`: FAILED\n"), f06)
        self.assertIn("**F-06's tests** (1 of 2 passed):", f06)
        self.assertIn("- `ShellTests.F06_an_extensionless_path_gets_the_shell_and_a_missing_asset_gets_404`: FAILED, 562.7s", f06)
        self.assertNotIn("Actual:", f06)

    def test_a_skipped_gate_test_is_not_a_pass(self):
        run = self.run_script([
            case(NEGATIVE + "HostTests", "N08_an_unknown_host_is_404"),
            case(NEGATIVE + "HostTests", "N08_a_spoofed_forwarded_host_changes_nothing", outcome="skipped"),
        ])
        self.assertEqual(1, run.code, run.stderr)
        self.assertIn("N-08: 2 tests, 1 passed, 0 failed, 1 skipped: FAILED", run.stdout)
        self.assertIn("`HostTests.N08_a_spoofed_forwarded_host_changes_nothing`: skipped", run.draft("N-08"))

    def test_every_counted_test_passing_exits_zero_with_one_line_per_id(self):
        run = self.run_script([c for c in OCT_24 if "F06_" not in c])
        self.assertEqual(0, run.code, run.stderr)
        lines = [line for line in run.stdout.splitlines() if line[:2] in ("F-", "N-")]
        self.assertEqual(["N-07: 3 tests, 3 passed, 0 failed, 0 skipped: passed",
                          "N-08: 2 tests, 2 passed, 0 failed, 0 skipped: passed"], lines)

    def test_ids_sort_by_number_not_by_text(self):
        with tempfile.TemporaryDirectory() as d:
            root = register(pathlib.Path(d), "| N-07 | a | b | D | 4 |\n| N-100 | a | b | D | 4 |\n")
            run = self.run_script([case(NEGATIVE + "A", "N100_x"), case(NEGATIVE + "A", "N07_x")], root=root)
        self.assertEqual(0, run.code, run.stderr)
        ids = [line.split(":")[0] for line in run.stdout.splitlines() if line.startswith("N-")]
        self.assertEqual(["N-07", "N-100"], ids)


class Usage(TestCase):
    def test_a_commit_that_is_not_a_hex_sha_is_refused(self):
        run = self.run_script(OCT_24, extra=["--commit", "main"])
        self.assertEqual(2, run.code, run.stdout)
        self.assertIn("--commit", run.stderr)


if __name__ == "__main__":
    unittest.main()
