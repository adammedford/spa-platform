#!/usr/bin/env python3
"""Tests for tools/issue_labels.py. Owner: integrator.

Run: python3 -I tools/issue_labels_tests.py

The derivation is proved on bodies built here, read against the table in tools/issue_labels.json as
it stands and against small tables that isolate one rule. The command line runs against a fake `gh`
on PATH that answers from a JSON file and logs each call, in an environment that inherits nothing,
so no case reaches a real repository.
"""
from __future__ import annotations

import importlib.util
import json
import pathlib
import stat
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "tools" / "issue_labels.py"
WORKFLOW = ROOT / ".github" / "workflows" / "issue-labels.yml"


def load(name: str, path: pathlib.Path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


labels = load("issue_labels", SCRIPT)
TABLE = labels.read_table()
PARSED = labels.shape().ownership().rules()


def issue(body: str = "", title: str = "A ticket", sub_issues: int = 0, have: tuple[str, ...] = (),
          number: int = 7) -> dict:
    return {"number": number, "title": title, "body": body,
            "labels": [{"name": name} for name in have], "subIssuesSummary": {"total": sub_issues}}


def derived(body: str = "", **changes) -> labels.Derived:
    return labels.derive(labels.issue_of(issue(body, **changes)), TABLE, PARSED)


def touching(*paths: str, vendor: str | None = None) -> str:
    lines = ["**Owner:** integrator", "**Touches:** " + ", ".join(f"`{path}`" for path in paths)]
    if vendor is not None:
        lines.append(f"**Vendor:** {vendor}")
    return "\n".join(lines) + "\n\n## What to build\n"


def group(found: labels.Derived, prefix: str) -> list[str]:
    return sorted(name for name in found.labels if name.startswith(prefix + ":"))


class Kind(unittest.TestCase):
    def test_a_run_record_is_a_report(self):
        for title in ("Overnight 2026-10-31", "Retro 2026-10-29", "Audit 2026-10-21: ticket shape",
                      "Sequence 2026-10-21"):
            with self.subTest(title):
                self.assertEqual(group(derived(title=title, sub_issues=3), "kind"), ["kind:report"])

    def test_an_issue_with_a_sub_issue_is_a_parent(self):
        self.assertEqual(group(derived(sub_issues=1), "kind"), ["kind:parent"])

    def test_an_issue_with_none_is_a_ticket(self):
        self.assertEqual(group(derived(), "kind"), ["kind:ticket"])

    def test_a_title_naming_a_run_without_its_date_is_no_report(self):
        self.assertEqual(group(derived(title="Overnight workflow: launch by script path"), "kind"),
                         ["kind:ticket"])


class Area(unittest.TestCase):
    def test_the_longest_prefix_wins(self):
        self.assertEqual(group(derived(touching("tests/Platform.Registry.Tests/")), "area"),
                         ["area:product"])
        self.assertEqual(group(derived(touching("tests/Gateway.IntegrationTests/")), "area"),
                         ["area:verification"])
        self.assertEqual(group(derived(touching("tools/bench/run.sh")), "area"),
                         ["area:verification"])
        self.assertEqual(group(derived(touching("tools/land.sh")), "area"), ["area:process"])

    def test_every_area_a_ticket_reaches_is_carried(self):
        found = derived(touching("Directory.Build.props", "AGENTS.md", "src/Gateway/Program.cs"))
        self.assertEqual(group(found, "area"), ["area:build", "area:process", "area:product"])

    def test_a_directory_without_its_slash_is_the_directory(self):
        self.assertEqual(group(derived("**Touches:** infra\n"), "area"), ["area:deployment"])

    def test_no_touches_line_or_none_gives_no_area(self):
        for body in ("No line here.", "**Touches:** none (runs the script)\n"):
            with self.subTest(body):
                found = derived(body)
                self.assertEqual(group(found, "area"), [])
                self.assertEqual(found.problems, ())

    def test_a_path_no_entry_covers_skips_the_issue(self):
        found = derived(touching("docs/elsewhere.md"))
        self.assertEqual(len(found.problems), 1, found)
        self.assertIn("docs/elsewhere.md", found.problems[0])

    def test_the_tables_cover_every_tracked_root_entry(self):
        # A new root file or directory the table does not cover would skip each ticket naming it.
        tracked = subprocess.run(["git", "-C", str(ROOT), "ls-files"], capture_output=True,
                                 text=True, check=True).stdout.split()
        uncovered = sorted({path for path in tracked if labels.area_of(path, TABLE) is None})
        self.assertEqual(uncovered, [])


class Vendor(unittest.TestCase):
    def test_a_path_implies_its_vendor(self):
        self.assertEqual(group(derived(touching("infra/modules/cell/ecs.tf")), "vendor"),
                         ["vendor:aws"])
        self.assertEqual(group(derived(touching("src/Platform.Identity/DynamoDbSessionStore.cs")),
                               "vendor"), ["vendor:aws"])

    def test_a_project_directory_implies_no_vendor(self):
        # The registry holds an S3 adapter, but a ticket naming the whole project says nothing
        # about whether it reaches it.
        self.assertEqual(group(derived(touching("src/Platform.Registry/")), "vendor"), [])

    def test_the_vendor_line_adds_what_no_path_can_say(self):
        found = derived(touching("src/Platform.Identity/IdpProfiles.cs", vendor="cognito, `aws`"))
        self.assertEqual(group(found, "vendor"), ["vendor:aws", "vendor:cognito"])

    def test_none_beside_an_implied_vendor_skips_the_issue(self):
        found = derived(touching("infra/main.tf", vendor="none"))
        self.assertEqual(len(found.problems), 1, found)
        self.assertIn("aws", found.problems[0])

    def test_none_on_a_portable_ticket_is_quiet(self):
        for line in ("none", "none (the gateway core)", "`none`"):
            with self.subTest(line):
                found = derived(touching("src/Gateway/Program.cs", vendor=line))
                self.assertEqual((group(found, "vendor"), found.problems, found.notes), ([], (), ()))

    def test_an_undefined_vendor_skips_the_issue(self):
        found = derived(touching("src/Gateway/Program.cs", vendor="azure"))
        self.assertEqual(len(found.problems), 1, found)
        self.assertIn("azure", found.problems[0])

    def test_a_product_ticket_with_no_vendor_line_is_a_note(self):
        found = derived(touching("src/Gateway/Program.cs"))
        self.assertEqual(found.problems, ())
        self.assertEqual(len(found.notes), 1)
        self.assertEqual(derived(touching("tools/land.sh")).notes, ())


class Changes(unittest.TestCase):
    def test_only_the_tables_groups_are_touched(self):
        raw = issue(touching("tools/land.sh"),
                    have=("ready-for-agent", "change-request", "area:product", "kind:ticket", "bug"))
        found = labels.derive(labels.issue_of(raw), TABLE, PARSED)
        add, remove = labels.changes(labels.issue_of(raw), found, TABLE)
        self.assertEqual((add, remove), (["area:process"], ["area:product"]))


class Tables(unittest.TestCase):
    def raw(self) -> dict:
        return json.loads(labels.TABLE.read_text(encoding="utf-8"))

    def test_the_table_as_it_stands_loads(self):
        self.assertIn("area:product", TABLE.labels)

    def test_an_area_with_no_label_defined_is_refused(self):
        raw = self.raw()
        raw["areas"].append(["somewhere/", "elsewhere"])
        with self.assertRaisesRegex(labels.Unreadable, "area:elsewhere"):
            labels.table_of(raw)

    def test_a_label_outside_the_groups_is_refused(self):
        raw = self.raw()
        raw["labels"].append({"name": "ready-for-agent", "color": "ffffff", "description": ""})
        with self.assertRaisesRegex(labels.Unreadable, "outside its groups"):
            labels.table_of(raw)

    def test_a_retired_label_is_never_one_the_table_defines(self):
        self.assertEqual(set(TABLE.retire) & set(TABLE.labels), set())


# The fake gh, in Python: answers from the JSON file GH_WORLD names ({"issues": [...], "labels":
# [...], "prs": [...]}) and logs each call's arguments, one JSON list per line, to GH_LOG. GH_FAIL
# makes every call fail as gh does when GitHub is unreachable; anything it does not know exits 97.
GH_DOUBLE = """#!{python}
import json, os, sys
args = sys.argv[1:]
with open(os.environ["GH_LOG"], "a") as log:
    log.write(json.dumps(args) + "\\n")
if os.environ.get("GH_FAIL"):
    print("gh: could not reach GitHub", file=sys.stderr); sys.exit(1)
world = json.load(open(os.environ["GH_WORLD"]))
def flag(name):
    return args[args.index(name) + 1] if name in args else None
if args[:2] == ["issue", "view"]:
    print(json.dumps(next(i for i in world["issues"] if str(i["number"]) == args[2])))
elif args[:2] == ["issue", "list"]:
    wanted = flag("--label")
    found = [i for i in world["issues"] if wanted is None or wanted in [l["name"] for l in i["labels"]]]
    print(json.dumps(found))
elif args[:2] == ["pr", "list"]:
    print(json.dumps([p for p in world.get("prs", []) if flag("--label") in p["labels"]]))
elif args[:2] == ["label", "list"]:
    print(json.dumps(world["labels"]))
elif args[0] == "api":
    print("{{}}")
else:
    sys.exit(97)
"""


class CommandLine(unittest.TestCase):
    """main() end to end, with the fake gh on PATH and nothing else of the caller's environment."""

    def setUp(self):
        self.directory = pathlib.Path(tempfile.mkdtemp())
        self.addCleanup(subprocess.run, ["rm", "-rf", str(self.directory)], check=False)
        bin_dir = self.directory / "bin"
        bin_dir.mkdir()
        gh = bin_dir / "gh"
        gh.write_text(GH_DOUBLE.format(python=sys.executable), encoding="utf-8")
        gh.chmod(stat.S_IRWXU)
        self.log = self.directory / "gh.log"
        self.world_file = self.directory / "world.json"
        self.env = {"PATH": f"{bin_dir}:/usr/bin:/bin", "GH_LOG": str(self.log),
                    "GH_WORLD": str(self.world_file)}
        defined = [{"name": name, **wanted} for name, wanted in TABLE.labels.items()]
        self.world({"issues": [], "labels": defined})

    def world(self, world: dict) -> None:
        self.world_file.write_text(json.dumps(world), encoding="utf-8")
        self.current = world

    def run_script(self, *args: str, stdin: str = "", **env: str) -> subprocess.CompletedProcess:
        return subprocess.run([sys.executable, "-I", str(SCRIPT), *args], input=stdin,
                              capture_output=True, text=True, env={**self.env, **env},
                              cwd=self.directory, check=False)

    def calls(self) -> list[list[str]]:
        if not self.log.exists():
            return []
        return [json.loads(line) for line in self.log.read_text().splitlines()]

    def writes(self) -> list[list[str]]:
        return [call for call in self.calls() if call[0] == "api"]

    def last_line(self, done: subprocess.CompletedProcess) -> str:
        return done.stdout.strip().splitlines()[-1]

    def test_check_on_stdin_exits_1_on_drift_and_writes_nothing(self):
        done = self.run_script("check", "-", stdin=json.dumps(issue(touching("tools/land.sh"))))
        self.assertEqual(done.returncode, 1, done.stdout + done.stderr)
        self.assertIn("issue-labels: #7: add area:process, kind:ticket", done.stdout)
        self.assertEqual(self.writes(), [])

    def test_check_in_sync_exits_0(self):
        body = touching("tools/land.sh")
        done = self.run_script("check", "-", stdin=json.dumps(
            issue(body, have=("ready-for-agent", "area:process", "kind:ticket"))))
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertIn("issue-labels: #7: in sync", done.stdout)

    def test_apply_writes_by_name_through_the_rest_routes(self):
        self.world({**self.current, "issues": [
            issue(touching("infra/main.tf"), have=("needs-triage", "area:product"), number=11),
            issue(touching("tools/land.sh"), have=("area:process", "kind:ticket"), number=12)]})
        done = self.run_script("apply", "--all")
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertEqual(self.writes(), [
            ["api", "--method", "POST", "repos/{owner}/{repo}/issues/11/labels",
             "-f", "labels[]=area:deployment", "-f", "labels[]=kind:ticket",
             "-f", "labels[]=vendor:aws"],
            ["api", "--method", "DELETE", "repos/{owner}/{repo}/issues/11/labels/area%3Aproduct"]])
        self.assertFalse(any(call[:2] == ["issue", "edit"] for call in self.calls()))
        self.assertTrue(self.last_line(done).startswith("issue-labels: pass: 2 issue(s) read, "
                                                        "1 drifted, 0 skipped, 1 written"))

    def test_a_skipped_issue_is_left_alone_and_fails_the_run(self):
        self.world({**self.current, "issues": [issue(touching("nowhere/at/all.txt"))]})
        done = self.run_script("apply", "--all")
        self.assertEqual(done.returncode, 1, done.stdout + done.stderr)
        self.assertIn("issue-labels: skip: #7:", done.stdout)
        self.assertEqual(self.writes(), [])

    def test_apply_refuses_a_label_the_repository_lacks(self):
        self.world({"issues": [issue(touching("tools/land.sh"))], "labels": []})
        done = self.run_script("apply", "7")
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
        self.assertIn("labels apply", self.last_line(done))
        self.assertEqual(self.writes(), [])

    def test_labels_apply_creates_updates_and_retires_what_is_unused(self):
        stale = {"name": "area:product", "color": "000000", "description": "old"}
        rest = [{"name": name, **wanted} for name, wanted in TABLE.labels.items()
                if name not in ("area:product", "kind:report")]
        self.world({"issues": [], "labels": [stale, *rest,
                                             {"name": "question", "color": "d876e3", "description": ""}]})
        done = self.run_script("labels", "apply")
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        routes = [(call[2], call[3]) for call in self.writes()]
        # In the table's order: kind:report is defined before area:product.
        self.assertEqual(routes, [("POST", "repos/{owner}/{repo}/labels"),
                                  ("PATCH", "repos/{owner}/{repo}/labels/area%3Aproduct"),
                                  ("DELETE", "repos/{owner}/{repo}/labels/question")])

    def test_labels_refuses_to_retire_a_label_in_use(self):
        defined = [{"name": name, **wanted} for name, wanted in TABLE.labels.items()]
        self.world({"issues": [issue(have=("duplicate",))],
                    "labels": [*defined, {"name": "duplicate", "color": "cfd3d7", "description": ""}]})
        done = self.run_script("labels", "apply")
        self.assertEqual(done.returncode, 1, done.stdout + done.stderr)
        self.assertIn("refuse to delete duplicate", done.stdout)
        self.assertEqual(self.writes(), [])

    def test_labels_check_writes_nothing(self):
        self.world({"issues": [], "labels": []})
        done = self.run_script("labels", "check")
        self.assertEqual(done.returncode, 1, done.stdout + done.stderr)
        self.assertEqual(self.writes(), [])

    def test_an_unreachable_github_is_nothing_judged(self):
        done = self.run_script("check", "--all", GH_FAIL="1")
        self.assertEqual(done.returncode, 2, done.stdout + done.stderr)
        self.assertTrue(self.last_line(done).startswith("issue-labels: fail: nothing judged:"))

    def test_bad_arguments_are_nothing_judged(self):
        for args in ((), ("apply", "-"), ("check",), ("labels", "delete")):
            with self.subTest(args):
                self.assertEqual(self.run_script(*args).returncode, 2)


class Workflow(unittest.TestCase):
    """The workflow runs only commands the script accepts."""

    def test_each_invocation_is_a_command_the_script_takes(self):
        text = WORKFLOW.read_text(encoding="utf-8")
        for needed in ('tools/issue_labels.py apply "$NUMBER"', "tools/issue_labels.py check --all",
                       "schedule) python3 -I tools/issue_labels.py apply --all",
                       "tools/issue_labels.py apply --all", "tools/issue_labels.py labels apply"):
            with self.subTest(needed):
                self.assertIn(needed, text)


if __name__ == "__main__":
    unittest.main()
