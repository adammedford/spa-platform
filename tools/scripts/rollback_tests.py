#!/usr/bin/env python3
"""Test companion for tools/scripts/rollback.sh. Owner: workstream 1.

Task 1.7 (#58): "rollback.sh applies the digest of the previous task definition"
(docs/plan/05-infrastructure-and-cost.md, Deployment and rollback). The doubles, the world they
share and the universal every run holds (no output names the account id) are
tools/scripts/cell_doubles.py's.

The proof cell's entry file sets every case's entry values (#1348), so a change to it runs this
companion.

Checkout inputs: infra/envs/poc/cell.env infra/envs/poc/outputs-fields.json tools/scripts/poc/mock-api.sh

Run: python3 -I tools/scripts/rollback_tests.py
"""

from __future__ import annotations

import importlib.util
import json
import pathlib
import sys

_spec = importlib.util.spec_from_file_location("cell_doubles", pathlib.Path(__file__).resolve().parent / "cell_doubles.py")
cell = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(cell)

import unittest  # noqa: E402

SCRIPT = "rollback.sh"

A = f"{cell.ECR_GATEWAY}@{cell.digest_of('gateway-a')}"
B = f"{cell.ECR_GATEWAY}@{cell.digest_of('gateway-b')}"
MOCK_A = f"{cell.ECR_MOCK_API}@{cell.digest_of('mock-a')}"
MOCK_B = f"{cell.ECR_MOCK_API}@{cell.digest_of('mock-b')}"


def definition(gateway: str, mock: str | None, status: str = "INACTIVE") -> dict:
    containers = {cell.GATEWAY: {"image": gateway, "environment": cell.environment()}}
    if mock:
        containers[cell.MOCK_API] = {"image": mock, "environment": []}
    return {"status": status, "containers": containers}


def deployed_twice() -> dict:
    """Revision 1 the sample image, 2 the first gateway build A, 3 the second, B, which runs."""
    world = cell.default_world()
    world["task_definitions"] = {
        f"{cell.FAMILY}:1": definition(cell.SAMPLE_IMAGE, None),
        f"{cell.FAMILY}:2": definition(A, MOCK_A),
        f"{cell.FAMILY}:3": definition(B, MOCK_B, "ACTIVE"),
    }
    world["repositories"] = {
        cell.GATEWAY_REPOSITORY: {"a": A.split("@")[1], "b": B.split("@")[1]},
        cell.MOCK_API_REPOSITORY: {"a": MOCK_A.split("@")[1], "b": MOCK_B.split("@")[1]},
    }
    world["service"] = {"taskDefinition": f"{cell.FAMILY}:3", "rolloutState": "COMPLETED", "deployments": 1}
    world["tasks"] = [{"id": "b" * 32, "lastStatus": "RUNNING", "taskDefinition": f"{cell.FAMILY}:3",
                       "gatewayDigest": B.split("@")[1]}]
    world["roots"]["platform"]["gateway_image"] = B
    world["target_group"]["path"] = "/readyz"
    return world


def write_mock_api_tfvars(case, image: str):
    """The mock API's tfvars in the case's platform root, as tools/scripts/poc/mock-api.sh leaves it
    (#1349)."""
    (case.repo / "infra/envs/poc/platform" / cell.mock_api_tfvars_name()).write_text(f'mock_api_image = "{image}"\n')


def setUpModule():
    cell.bash_banner("rollback_tests")


class RollbackAppliesThePreviousTaskDefinitionsImages(cell.CompanionTestCase):

    def rolled_back(self, world: dict, *args: str):
        case = cell.Case(self, outputs=True, world=world)
        result = case.run(SCRIPT, *args)
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        return case, result

    def applied(self, case) -> dict:
        applies = [c for c in case.calls("terraform") if c.get("command") == "apply"]
        self.assertEqual(1, len(applies))
        return applies[0]["vars"]

    def test_the_previous_revision_s_gateway_is_applied_and_asserted(self):
        case, result = self.rolled_back(deployed_twice())
        # #1349: the gateway alone. No mock_api_image is passed, in the plan or the apply.
        for call in [c for c in case.calls("terraform") if c.get("command") in ("plan", "apply")]:
            self.assertEqual({"gateway_image": A, "health_check_path": "/readyz", "gateway_writable_root": "false"},
                             call["vars"])
            self.assertEqual([], [t for t in call["argv"] if "mock_api_image" in t], call["argv"])
        self.assertEqual(1, len(case.aws("ecs wait services-stable")))
        self.assertIn("pass:", result.stdout)
        tfvars = (case.repo / "infra/envs/poc/platform/images.auto.tfvars").read_text()
        self.assertIn(f'gateway_image     = "{A}"', tfvars)
        self.assertEqual(["gateway_image", "gateway_writable_root", "health_check_path"],
                         sorted(line.split("=", 1)[0].strip() for line in tfvars.splitlines()
                                if "=" in line and not line.startswith("#")))
        # The cell's names are the outputs document's, never state's (contracts/infra-outputs.schema.json;
        # Q49 on #129 excepts teardown.sh alone). Review of #58.
        self.assertEqual([], [c for c in case.calls("terraform") if c.get("command") == "output" and "-raw" in c["argv"]])
        self.assertEqual(A, json.loads((case.repo / "infra/envs/poc/outputs.json").read_text())["gatewayImage"])

    def test_a_previous_build_pushed_as_an_image_index_is_asserted_by_its_arm64_image(self):
        # #783: a build deploy.sh attested is an image index, and rollback.sh shares deploy.sh's
        # wait_and_assert, which takes the index's linux/arm64 image as the running digest too, and
        # its attestation manifest never.
        for fault, passes in (("running_platform", True), ("running_attestation", False)):
            with self.subTest(fault=fault):
                world = deployed_twice()
                world["manifests"] = {A.split("@")[1]: cell.attested_index(world, "gateway-a")}
                world["faults"][fault] = True
                case = cell.Case(self, outputs=True, world=world)
                result = case.run(SCRIPT)
                self.assertNoUnexpectedCall(case)
                self.assertEqual(0 if passes else 1, result.status, result.output)
                running = case.load()["tasks"][0]["gatewayDigest"]
                self.assertNotEqual(A.split("@")[1], running)
                if not passes:
                    self.assertIn("is not the applied gateway", result.stderr)

    def test_the_plan_prints_no_client_id_from_the_outputs_document(self):
        # #251, as deploy_tests.py's case of the same name: the plan shows the mock API's
        # environment, which carries both apps' client ids.
        world = deployed_twice()
        case = cell.Case(self, outputs=True, world=world)
        write_mock_api_tfvars(case, MOCK_B)
        result = case.run(SCRIPT)
        self.assertEqual(0, result.status, result.output)
        for app, entry in world["roots"]["apps"]["clients"].items():
            self.assertNotIn(entry["clientId"], result.output, f"{app}'s client id was printed")
        self.assertIn('name = "MockApi__App1Client", value = "<client-id:app1>"', result.stdout)
        self.assertIn('name = "MockApi__App2Client", value = "<client-id:app2>"', result.stdout)

    def test_it_reads_no_container_image_but_the_gateway_s_and_the_mock_api_stays_at_its_tfvars(self):
        # #1349: the target revision ran MOCK_A beside A, and the mock API's own file holds MOCK_B.
        # The rollback moves the gateway and leaves the mock API at MOCK_B.
        world = deployed_twice()
        case = cell.Case(self, outputs=True, world=world)
        write_mock_api_tfvars(case, MOCK_B)
        result = case.run(SCRIPT)
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        for call in case.aws("ecs describe-task-definition"):
            query = call["options"].get("query", "")
            if "containerDefinitions" in query and "name==" in query:
                self.assertIn(f"name=='{cell.GATEWAY}'", query, query)
        world = case.load()
        running = world["task_definitions"][world["service"]["taskDefinition"]]["containers"]
        self.assertEqual(A, running[cell.GATEWAY]["image"])
        self.assertEqual(MOCK_B, running[cell.MOCK_API]["image"])
        self.assertNotIn("MOCK_API_CONTAINER", (cell.SCRIPTS / SCRIPT).read_text())
        self.assertNotIn("mock-api", (cell.SCRIPTS / SCRIPT).read_text())

    def test_a_rollout_in_progress_after_the_waiter_that_then_completes_passes(self):
        # #467: rollback.sh shares deploy.sh's wait_and_assert, which reads the primary's
        # rolloutState again until it settles rather than failing on its first IN_PROGRESS.
        world = deployed_twice()
        world["faults"]["rollout_in_progress"] = 2
        case = cell.Case(self, outputs=True, world=world)
        case.stub_sleep()
        result = case.run(SCRIPT)
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        self.assertIn("pass:", result.stdout)
        reads = [c for c in case.aws("ecs describe-services") if "rolloutState" in c["options"].get("query", "")]
        self.assertEqual(3, len(reads))
        self.assertEqual([["15"]] * 2, case.sleeps())

    def test_a_revision_with_the_running_image_is_passed_over(self):
        world = deployed_twice()
        # Revision 4: an apply that changed something other than the image.
        world["task_definitions"][f"{cell.FAMILY}:3"]["status"] = "INACTIVE"
        world["task_definitions"][f"{cell.FAMILY}:4"] = definition(B, MOCK_B, "ACTIVE")
        world["service"]["taskDefinition"] = f"{cell.FAMILY}:4"
        world["tasks"][0]["taskDefinition"] = f"{cell.FAMILY}:4"
        case, _ = self.rolled_back(world)
        self.assertEqual(A, self.applied(case)["gateway_image"])

    def test_a_deleted_revision_is_passed_over(self):
        world = deployed_twice()
        world["task_definitions"][f"{cell.FAMILY}:2"]["status"] = "DELETE_IN_PROGRESS"
        world["task_definitions"][f"{cell.FAMILY}:0"] = definition(A, MOCK_A)  # never read: below 1
        case = cell.Case(self, outputs=True, world=world)
        result = case.run(SCRIPT)
        # Revision 1 is the sample image, so with 2 gone there is nothing to roll back to.
        self.assertEqual(1, result.status, result.output)
        self.assertIn("not a gateway build", result.stderr)
        self.assertEqual([], [c for c in case.calls("terraform") if c.get("command") in ("plan", "apply")])

    def test_a_revision_named_is_the_one_applied(self):
        world = deployed_twice()
        world["task_definitions"][f"{cell.FAMILY}:3"]["status"] = "INACTIVE"
        world["task_definitions"][f"{cell.FAMILY}:4"] = definition(f"{cell.ECR_GATEWAY}@{cell.digest_of('gateway-c')}", None, "ACTIVE")
        world["repositories"][cell.GATEWAY_REPOSITORY]["c"] = cell.digest_of("gateway-c")
        world["service"]["taskDefinition"] = f"{cell.FAMILY}:4"
        case, _ = self.rolled_back(world, "--to-revision", "2")
        self.assertEqual(A, self.applied(case)["gateway_image"])


class RollbackRefuses(cell.CompanionTestCase):
    def refused(self, world: dict, fragment: str, *args: str, status: int = 1):
        case = cell.Case(self, outputs=True, world=world)
        result = case.run(SCRIPT, *args)
        self.assertEqual(status, result.status, result.output)
        self.assertIn(fragment, result.stderr)
        self.assertEqual([], [c for c in case.calls("terraform") if c.get("command") in ("plan", "apply")])
        self.assertNoUnexpectedCall(case)

    def test_the_sample_image_is_not_a_rollback(self):
        world = deployed_twice()
        world["task_definitions"][f"{cell.FAMILY}:3"]["status"] = "INACTIVE"
        world["service"]["taskDefinition"] = f"{cell.FAMILY}:2"
        world["tasks"][0]["taskDefinition"] = f"{cell.FAMILY}:2"
        del world["task_definitions"][f"{cell.FAMILY}:3"]
        self.refused(world, 'health_check_path = "/"')

    def test_an_image_the_repository_no_longer_holds(self):
        world = deployed_twice()
        del world["repositories"][cell.GATEWAY_REPOSITORY]["a"]
        self.refused(world, "no longer holds")

    def test_nothing_older_than_the_first_revision(self):
        world = cell.default_world()
        self.refused(world, "nothing to roll back to")

    def test_the_revision_already_running(self):
        self.refused(deployed_twice(), "already runs revision 3", "--to-revision", "3")

    def test_a_revision_that_is_not_a_number(self):
        self.refused(deployed_twice(), "must be a revision number", "--to-revision", "x2", status=2)


class RollbackTakesTheCellFromTheEntryValues(cell.CompanionTestCase):
    """#1348: the four entry values it reads, each refused by name before any call."""

    READS = ("SPA_PLATFORM_ROOT", "SPA_ROOTS", "SPA_FIELD_MAP", "SPA_OUTPUTS_FILE")

    def test_each_entry_value_it_reads_unset_exits_2_naming_it_before_any_call(self):
        for name in self.READS:
            for value in (None, ""):
                with self.subTest(name=name, value=value):
                    case = cell.Case(self, outputs=True, world=deployed_twice())
                    result = case.run(SCRIPT, env=case.env(**{name: value}))
                    self.assertEqual(2, result.status, result.output)
                    self.assertIn(f"{name} is not set", result.stderr)
                    self.assertEqual([], case.calls())

    def test_it_needs_no_certificate_domain(self):
        case = cell.Case(self, outputs=True, world=deployed_twice())
        result = case.run(SCRIPT, env=case.env(SPA_CERT_DOMAIN=None, SPA_CELL=None))
        self.assertEqual(0, result.status, result.output)

    def test_help_reads_no_entry_value_and_names_no_profile(self):
        case = cell.Case(self)
        result = case.run(SCRIPT, "--help", env=case.env(**{name: None for name in cell.ENTRY_NAMES}))
        self.assertEqual(0, result.status, result.output)
        self.assertNotIn("the spa-platform profile", result.stdout)
        self.assertNotIn("tools/aws-project.env", result.stdout)
        self.assertNotIn("infra/envs/", result.stdout)


if __name__ == "__main__":
    sys.exit(cell.main("rollback_tests"))
