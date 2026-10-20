#!/usr/bin/env python3
"""Test companion for tools/scripts/rollback.sh. Owner: workstream 1.

Task 1.7 (#58): "rollback.sh applies the digest of the previous task definition"
(docs/plan/05-infrastructure-and-cost.md, Deployment and rollback). The doubles, the world they
share and the universal every run holds (no output names the account id) are
tools/scripts/cell_doubles.py's.

Run: python3 -I tools/scripts/rollback_tests.py
"""

from __future__ import annotations

import importlib.util
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


def setUpModule():
    cell.bash_banner("rollback_tests")


class RollbackAppliesThePreviousTaskDefinitionsImages(cell.CompanionTestCase):

    def rolled_back(self, world: dict, *args: str):
        case = cell.Case(self, world=world)
        result = case.run(SCRIPT, *args)
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        return case, result

    def applied(self, case) -> dict:
        applies = [c for c in case.calls("terraform") if c.get("command") == "apply"]
        self.assertEqual(1, len(applies))
        return applies[0]["vars"]

    def test_the_previous_revision_s_gateway_and_mock_api_are_applied_and_asserted(self):
        case, result = self.rolled_back(deployed_twice())
        self.assertEqual({"gateway_image": A, "mock_api_image": MOCK_A, "health_check_path": "/readyz"},
                         self.applied(case))
        self.assertEqual(1, len(case.aws("ecs wait services-stable")))
        self.assertIn("pass:", result.stdout)
        tfvars = (case.repo / "infra/envs/poc/platform/images.auto.tfvars").read_text()
        self.assertIn(f'gateway_image     = "{A}"', tfvars)

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
        case = cell.Case(self, world=world)
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
        case = cell.Case(self, world=world)
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


if __name__ == "__main__":
    sys.exit(cell.main("rollback_tests"))
