#!/usr/bin/env python3
"""Exercise the shared path classifier through its pipefail caller (PR #50)."""

import os
import pathlib
import subprocess
import tempfile
import textwrap
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent


class ClassifierTests(unittest.TestCase):
    def classify(self, half, paths):
        # Both the hook and CI pipe their path list into the executable under pipefail.
        return subprocess.run(
            ["bash", "-o", "pipefail", "-c",
             'changed=$(cat); printf "%s\\n" "$changed" | "$1" "$2"',
             "classifier-test", str(ROOT / ".github/touches.sh"), half],
            input="\n".join(paths) + "\n", text=True, capture_output=True,
        ).returncode

    def test_contract_inputs_and_restore_configuration_trigger_the_build(self):
        for path in ("contracts/registry.schema.json", "contracts/app-definition.schema.json",
                     "contracts/request-gate.md", "nuget.config"):
            with self.subTest(path=path):
                self.assertEqual(0, self.classify("build", [path]))

    def test_the_analyzer_configs_trigger_the_build(self):
        # The build enforces every analyzer severity, so a change to the files that set them is proved
        # by building: a ramp batch's promotion deletes pending lines from .globalconfig alone (#606).
        for path in (".globalconfig", ".editorconfig", "build/strict.globalconfig"):
            with self.subTest(path=path):
                self.assertEqual(0, self.classify("build", [path]))
                self.assertEqual(1, self.classify("terraform", [path]))
        for path in ("docs/.editorconfig", ".globalconfig.bak", ".editorconfig.orig"):
            with self.subTest(path=path):
                self.assertEqual(1, self.classify("build", [path]))

    def test_the_banned_symbol_lists_trigger_the_build(self):
        # A list's entry is an RS0030 error the build enforces, and an id that names no symbol bans
        # nothing without a word, so only a build proves a change to one (#608).
        for path in ("BannedSymbols.Shipping.txt", "BannedSymbols.NoToken.txt"):
            with self.subTest(path=path):
                self.assertEqual(0, self.classify("build", [path]))
                self.assertEqual(1, self.classify("terraform", [path]))
        for path in ("docs/BannedSymbols.Shipping.txt", "BannedSymbols.Shipping.txt.orig"):
            with self.subTest(path=path):
                self.assertEqual(1, self.classify("build", [path]))

    def test_what_the_gateway_image_is_built_from_triggers_the_build(self):
        # The build half builds the gateway's image too, and .dockerignore decides its context (#210).
        self.assertEqual(0, self.classify("build", [".dockerignore"]))
        self.assertEqual(1, self.classify("terraform", [".dockerignore"]))
        self.assertEqual(1, self.classify("build", ["docs/.dockerignore"]))

    def test_documentation_alone_triggers_neither_half(self):
        for half in ("build", "terraform"):
            with self.subTest(half=half):
                self.assertEqual(1, self.classify(half, ["docs/adr/0020-decision.md"]))

    def test_what_decides_which_terraform_runs_triggers_the_terraform_half(self):
        # Raising the floor changes what every root is checked with, so it has to be checked (#69).
        for path in (".terraform-version", "tools/terraform-floor.sh"):
            with self.subTest(path=path):
                self.assertEqual(0, self.classify("terraform", [path]))
                self.assertEqual(1, self.classify("build", [path]))

    def test_neighbours_of_those_paths_trigger_neither_half(self):
        for path in ("docs/.terraform-version", ".terraform-version.bak", "tools/terraform-floor.sh.orig",
                     "tools/scripts/terraform-floor.sh"):
            with self.subTest(path=path):
                self.assertEqual(1, self.classify("terraform", [path]))

    def test_anything_under_the_workflows_folder_triggers_the_workflows_half(self):
        # A script, a companion or a fixture: the companions share the fixtures (#276).
        for path in (".claude/workflows/overnight.js", ".claude/workflows/overnight_tests.mjs",
                     ".claude/workflows/fixtures/2026-10-20-tickets.json"):
            with self.subTest(path=path):
                self.assertEqual(0, self.classify("workflows", [path]))
                self.assertEqual(1, self.classify("build", [path]))
                self.assertEqual(1, self.classify("terraform", [path]))

    def test_neighbours_of_the_workflows_folder_do_not_trigger_it(self):
        for path in (".claude/settings.json", ".claude/skills/wiz/SKILL.md", ".github/workflows/ci.yml",
                     "docs/.claude/workflows/x.js", "tools/workflow_parse_tests.py"):
            with self.subTest(path=path):
                self.assertEqual(1, self.classify("workflows", [path]))

    def test_an_early_match_survives_a_path_list_larger_than_the_pipe_buffer(self):
        trailing = [f"docs/{i:05d}-{'a' * 100}.md" for i in range(3000)]
        for half, first in (("build", "Directory.Build.props"),
                            ("terraform", "infra/main.tf"),
                            ("workflows", ".claude/workflows/overnight.js")):
            with self.subTest(half=half):
                self.assertEqual(0, self.classify(half, [first, *trailing]))


class WorkflowDecisionTests(unittest.TestCase):
    def decide(self, paths, *, event="push", missing_base=False):
        """Which expensive jobs run: the decision step's outputs, less the base it resolved and the
        workflow companions' answer, which WorkflowCompanionDecisionTests reads."""
        outputs = self.outputs(paths, event=event, missing_base=missing_base)
        outputs.pop("base")
        outputs.pop("workflows")
        return outputs

    def outputs(self, paths, *, event="push", missing_base=False):
        # Execute the workflow's real run block, rather than a second copy of its decision logic.
        workflow = (ROOT / ".github/workflows/ci.yml").read_text()
        step = workflow.split("        id: touches\n", 1)[1].split("\n      - name:", 1)[0]
        script = textwrap.dedent(step.split("        run: |\n", 1)[1])
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)

            def git(*args):
                return subprocess.run(
                    ["git", "-c", "core.hooksPath=/dev/null", "-c", "maintenance.auto=false",
                     "-c", "user.name=CI decision test", "-c", "user.email=test@example.test", *args],
                    cwd=root, check=True, text=True, capture_output=True,
                ).stdout.strip()

            classifier = root / ".github/touches.sh"
            classifier.parent.mkdir()
            classifier.write_bytes((ROOT / ".github/touches.sh").read_bytes())
            classifier.chmod(0o755)
            git("init", "-q", "-b", "main")
            git("add", ".")
            git("commit", "-qm", "baseline")
            base = git("rev-parse", "HEAD")
            git("update-ref", "refs/remotes/origin/main", base)
            for path in paths:
                target = root / path
                target.parent.mkdir(parents=True, exist_ok=True)
                # Keep the real classifier executable when it is itself the changed input.
                with target.open("a") as output:
                    output.write("\n")
            git("add", ".")
            git("commit", "-qm", "changed input")
            output = root / "job-outputs"
            result = subprocess.run(
                ["bash", "-c", script], cwd=root, text=True, capture_output=True, timeout=30,
                env={**os.environ, "EVENT": event, "BASE_REF": "main",
                     "BEFORE": "0" * 40 if missing_base else base, "GITHUB_OUTPUT": str(output)},
            )
            self.assertEqual(0, result.returncode, result.stderr)
            return dict(line.split("=", 1) for line in output.read_text().splitlines())

    def test_a_documentation_pull_request_skips_both_expensive_jobs(self):
        self.assertEqual({"build": "false", "terraform": "false"},
                         self.decide(["docs/note.md"], event="pull_request"))

    def test_a_schema_only_push_runs_build_and_tests(self):
        self.assertEqual({"build": "true", "terraform": "false"},
                         self.decide(["contracts/registry.schema.json"]))

    def test_raising_the_floor_runs_the_terraform_job(self):
        self.assertEqual({"build": "false", "terraform": "true"},
                         self.decide([".terraform-version"], event="pull_request"))

    def test_a_change_in_both_halves_runs_both_jobs(self):
        self.assertEqual({"build": "true", "terraform": "true"},
                         self.decide(["src/Example.cs", "infra/main.tf"]))

    def test_a_change_to_the_image_check_runs_both_jobs(self):
        # Only the build job runs tools/image-check.sh, so no half's paths can vouch for it (#210).
        self.assertEqual({"build": "true", "terraform": "true"},
                         self.decide(["tools/image-check.sh"], event="pull_request"))

    def test_an_unresolvable_base_runs_both_jobs(self):
        self.assertEqual({"build": "true", "terraform": "true"},
                         self.decide(["docs/note.md"], missing_base=True))

    def test_large_source_diffs_run_build_and_tests(self):
        trailing = [f"docs/{i:05d}-{'a' * 100}.md" for i in range(3000)]
        self.assertEqual({"build": "true", "terraform": "false"},
                         self.decide(["Directory.Build.props", *trailing]))

    def test_the_base_it_resolved_is_an_output_for_the_steering_check(self):
        # The steering check's authority rule judges the commits from this base to HEAD (#102), so
        # the step names the base it diffed against, and nothing when it had none to diff against.
        self.assertEqual("origin/main", self.outputs(["docs/note.md"], event="pull_request")["base"])
        self.assertRegex(self.outputs(["docs/note.md"])["base"], r"^[0-9a-f]{40}$")
        self.assertEqual("", self.outputs(["docs/note.md"], missing_base=True)["base"])

    def test_the_workflows_overrides_survive_large_diffs(self):
        trailing = [f"z-docs/{i:05d}-{'a' * 100}.md" for i in range(3000)]
        for path in (".github/workflows/ci.yml", ".github/touches.sh", "tools/smoke.sh"):
            with self.subTest(path=path):
                self.assertEqual({"build": "true", "terraform": "true"},
                                 self.decide([path, *trailing]))


class WorkflowCompanionDecisionTests(unittest.TestCase):
    """#276: the Documents job's step that runs the workflow scripts' test companions runs when a
    file under .claude/workflows/ changed, and whenever every job runs; otherwise it is skipped."""

    # The same run of the decision step's real block, without inheriting its cases.
    outputs = WorkflowDecisionTests.outputs

    def workflows(self, paths, **kwargs):
        return self.outputs(paths, **kwargs)["workflows"]

    def test_a_workflow_change_runs_the_companions(self):
        for path in (".claude/workflows/overnight.js", ".claude/workflows/fixtures/tickets.json"):
            with self.subTest(path=path):
                self.assertEqual("true", self.workflows([path], event="pull_request"))

    def test_a_change_elsewhere_skips_them(self):
        for path in ("docs/note.md", "src/Example.cs", "infra/main.tf", ".claude/settings.json"):
            with self.subTest(path=path):
                self.assertEqual("false", self.workflows([path], event="pull_request"))

    def test_they_run_whenever_every_job_runs(self):
        self.assertEqual("true", self.workflows(["docs/note.md"], missing_base=True))
        for path in (".github/workflows/ci.yml", ".github/touches.sh"):
            with self.subTest(path=path):
                self.assertEqual("true", self.workflows([path]))


if __name__ == "__main__":
    unittest.main()
