#!/usr/bin/env python3
"""Test companion for tools/scripts/poc/mock-api.sh. Owner: workstream 1.

#1349: the proof cell's mock API gets a script of its own, so that deploy.sh builds the gateway and
nothing else. Given the outputs document, the script builds fixtures/MockApi/Dockerfile for ARM64,
pushes it to the document's ecrMockApi repository and writes its digest as mock_api_image to an
*.auto.tfvars file in the proof-cell platform root, named to sort after images.auto.tfvars, so the
next apply loads it and an older images.auto.tfvars's value loses. It applies nothing. The doubles,
the world they share and the universal every run holds (no output names the account id) are
tools/scripts/cell_doubles.py's.

Checkout inputs: infra/envs/poc/cell.env infra/envs/poc/outputs-fields.json

Run: python3 -I tools/scripts/poc/mock_api_tests.py
"""

from __future__ import annotations

import importlib.util
import json
import pathlib
import sys

_spec = importlib.util.spec_from_file_location("cell_doubles", pathlib.Path(__file__).resolve().parent.parent / "cell_doubles.py")
cell = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(cell)

import unittest  # noqa: E402

SCRIPT = "poc/mock-api.sh"
PLATFORM = "infra/envs/poc/platform"


def setUpModule():
    cell.bash_banner("mock_api_tests")


def builds(case) -> list[dict]:
    return [call for call in case.calls("docker") if call["argv"][:2] == ["buildx", "build"]]


class TheMockApiIsBuiltPushedAndRecorded(cell.CompanionTestCase):

    def pushed(self, world: dict | None = None):
        case = cell.Case(self, outputs=True, world=world)
        result = case.run(SCRIPT)
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        return case, result

    def recorded(self, case) -> pathlib.Path:
        return case.repo / PLATFORM / cell.mock_api_tfvars_name()

    def test_it_builds_the_mock_api_s_dockerfile_for_arm64_with_attestations_and_pushes_it(self):
        case, _ = self.pushed()
        found = builds(case)
        self.assertEqual(1, len(found), found)
        argv = found[0]["argv"]
        self.assertEqual("linux/arm64", argv[argv.index("--platform") + 1])
        self.assertEqual(str(case.repo / "fixtures/MockApi/Dockerfile"), argv[argv.index("--file") + 1])
        self.assertEqual(str(case.repo), argv[-1])
        for option in ("--push", "--pull", "--sbom=true", "--provenance=mode=max"):
            self.assertIn(option, argv)
        # Into the outputs document's ecrMockApi repository.
        self.assertTrue(found[0]["tag"].startswith(cell.ECR_MOCK_API + ":"), found[0]["tag"])

    def test_it_writes_mock_api_image_by_the_digest_ecr_holds(self):
        case, _ = self.pushed()
        world = case.load()
        tag = builds(case)[0]["tag"].rpartition(":")[2]
        digest = world["repositories"][cell.MOCK_API_REPOSITORY][tag]
        text = self.recorded(case).read_text()
        self.assertEqual({"mock_api_image": f"{cell.ECR_MOCK_API}@{digest}"},
                         cell.auto_tfvars(case.repo / PLATFORM))
        self.assertRegex(text, r'(?m)^mock_api_image = "[^"]+@sha256:[0-9a-f]{64}"$')
        # The digest is ECR's, read back by the run's tag, not one the script made up.
        reads = case.aws("ecr describe-images")
        self.assertEqual([cell.MOCK_API_REPOSITORY], [r["options"]["repository-name"] for r in reads])
        self.assertEqual([f"imageTag={tag}"], [r["options"]["image-ids"] for r in reads])

    def test_its_file_is_an_auto_tfvars_that_sorts_after_images_auto_tfvars(self):
        name = cell.mock_api_tfvars_name()
        self.assertTrue(name.endswith(".auto.tfvars"), name)
        self.assertEqual(["images.auto.tfvars", name], sorted(["images.auto.tfvars", name]))
        # Git ignores it with every *.tfvars: the digest's repository carries the account id.
        self.assertTrue(name.endswith(".tfvars"))

    def test_an_older_images_auto_tfvars_s_mock_api_image_loses_to_it(self):
        # An images.auto.tfvars written before #1349 still sets mock_api_image until the next
        # deploy.sh rewrites it; Terraform loads the two in lexical order and the later wins.
        case = cell.Case(self, outputs=True)
        stale = f"{cell.ECR_MOCK_API}@{cell.digest_of('mock-stale')}"
        images = case.repo / PLATFORM / "images.auto.tfvars"
        before = f'gateway_image     = "{cell.ECR_GATEWAY}@{cell.digest_of("g")}"\nmock_api_image    = "{stale}"\n'
        images.write_text(before)
        result = case.run(SCRIPT)
        self.assertEqual(0, result.status, result.output)
        loaded = cell.auto_tfvars(case.repo / PLATFORM)
        self.assertNotEqual(stale, loaded["mock_api_image"])
        self.assertTrue(loaded["mock_api_image"].startswith(cell.ECR_MOCK_API + "@sha256:"))
        # deploy.sh's file is deploy.sh's: this script leaves it as it was.
        self.assertEqual(before, images.read_text())

    def test_it_runs_no_terraform_at_all(self):
        # It applies nothing: the next apply, deploy.sh's on the proof cell, loads the file.
        case, _ = self.pushed()
        self.assertEqual([], case.calls("terraform"))
        self.assertNotIn("terraform", (cell.SCRIPTS / SCRIPT).read_text().split("\n# Owner:")[1])

    def test_the_login_password_goes_to_docker_on_standard_input(self):
        case, _ = self.pushed()
        login = next(c for c in case.calls("docker") if c["argv"][:1] == ["login"])
        self.assertTrue(login["password_on_stdin"])
        self.assertEqual(["login", "--username", "AWS", "--password-stdin", cell.REGISTRY_HOST], login["argv"])

    def test_two_runs_push_two_tags(self):
        case = cell.Case(self, outputs=True)
        self.assertEqual(0, case.run(SCRIPT).status)
        second = case.run(SCRIPT)
        self.assertEqual(0, second.status, second.output)
        self.assertEqual(2, len({call["tag"] for call in builds(case)}))


class TheMockApiScriptStopsShort(cell.CompanionTestCase):

    def test_a_failed_build_writes_no_digest(self):
        world = cell.default_world()
        world["faults"]["build"] = True
        case = cell.Case(self, outputs=True, world=world)
        result = case.run(SCRIPT)
        self.assertEqual(1, result.status, result.output)
        self.assertIn("building fixtures/MockApi/Dockerfile failed", result.stderr)
        self.assertFalse((case.repo / PLATFORM / cell.mock_api_tfvars_name()).exists())

    def test_a_refused_login_builds_nothing(self):
        world = cell.default_world()
        world["faults"]["login"] = True
        case = cell.Case(self, outputs=True, world=world)
        result = case.run(SCRIPT)
        self.assertEqual(1, result.status, result.output)
        self.assertEqual([], builds(case))
        self.assertFalse((case.repo / PLATFORM / cell.mock_api_tfvars_name()).exists())

    def test_without_the_outputs_document_it_refuses_before_any_build(self):
        case = cell.Case(self)
        result = case.run(SCRIPT)
        self.assertEqual(2, result.status, result.output)
        self.assertIn("there is no infra/envs/poc/outputs.json", result.stderr)
        self.assertEqual([], builds(case))
        self.assertEqual([], [c for c in case.calls("docker") if c["argv"][:1] == ["login"]])

    def test_a_document_with_no_ecr_mock_api_is_refused_naming_it(self):
        case = cell.Case(self, outputs=True)
        document = json.loads(case.outputs_path().read_text())
        del document["ecrMockApi"]
        case.outputs_path().write_text(json.dumps(document))
        result = case.run(SCRIPT)
        self.assertEqual(2, result.status, result.output)
        self.assertIn("gives no ecrMockApi", result.stderr)
        self.assertEqual([], builds(case))

    def test_each_entry_value_it_reads_unset_exits_2_naming_it_before_any_call(self):
        for name in ("SPA_PLATFORM_ROOT", "SPA_OUTPUTS_FILE"):
            for value in (None, ""):
                with self.subTest(name=name, value=value):
                    case = cell.Case(self, outputs=True)
                    result = case.run(SCRIPT, env=case.env(**{name: value}))
                    self.assertEqual(2, result.status, result.output)
                    self.assertIn(f"{name} is not set", result.stderr)
                    self.assertEqual([], case.calls())

    def test_without_docker(self):
        case = cell.Case(self, outputs=True)
        case.remove("docker")
        result = case.run(SCRIPT)
        self.assertEqual(2, result.status, result.output)
        self.assertIn("brew install --cask docker-desktop", result.stderr)

    def test_an_unknown_argument_is_refused_and_help_prints_the_manual(self):
        case = cell.Case(self)
        result = case.run(SCRIPT, "--apply")
        self.assertEqual(2, result.status, result.output)
        self.assertEqual([], case.calls())
        shown = case.run(SCRIPT, "--help", env=case.env(**{name: None for name in cell.ENTRY_NAMES}))
        self.assertEqual(0, shown.status, shown.output)
        self.assertIn("mock_api_image", shown.stdout)
        self.assertIn(cell.mock_api_tfvars_name(), shown.stdout)


if __name__ == "__main__":
    sys.exit(cell.main("mock_api_tests"))
