#!/usr/bin/env python3
"""Test companion for tools/scripts/verify-teardown.sh. Owner: workstream 1.

#308. P-05, "a query for the platform tag returns no resources", proved by every reader that can see
the platform tag (#118, #232 Q3): IAM's own listings of roles and customer managed policies, read by
their tags; the roles the cell declares, confirmed absent by name; and the tagging query in the
selected Region, which drops ECS records whose describe says INACTIVE and confirms each EC2 id with a
describe. The cases are #118 step 2's records, read on the platform project after the platform root's
destroy (2026-10-21): the cluster, the service and the task definition INACTIVE, a security group and
four rules the index still listed and EC2 no longer knew, and the cell's two roles gone by name.

The doubles, the world they share and the universal every run holds (no output names the account id)
are tools/scripts/cell_doubles.py's.

Run: python3 -I tools/scripts/verify_teardown_tests.py
"""

from __future__ import annotations

import importlib.util
import pathlib
import re
import sys

_spec = importlib.util.spec_from_file_location("cell_doubles", pathlib.Path(__file__).resolve().parent / "cell_doubles.py")
cell = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(cell)

import unittest  # noqa: E402

SCRIPT = "verify-teardown.sh"
ACCOUNT, REGION, CELL = cell.ACCOUNT, cell.REGION, cell.CELL

# contracts/resource-tags.md, read rather than typed: the platform tag and the bootstrap's value.
_TAGS = (cell.ROOT / "contracts" / "resource-tags.md").read_text()
PLATFORM_VALUE = re.search(r"\| `Platform` \| `([^`]+)` \| Every resource", _TAGS)[1]
BOOTSTRAP_VALUE = re.search(r"\| `Platform` \| `([^`]+)` \|\n", _TAGS)[1]

# The roles the cell declares, read out of infra/modules/cell/iam.tf (CODING_STANDARDS.md, "A guard
# reads the thing it guards"): every aws_iam_role's name, with the cell put in.
_IAM = (cell.MODULE / "iam.tf").read_text()
CELL_ROLES = sorted(name.replace("${local.name_prefix}", cell.FAMILY) for name in re.findall(
    r'resource "aws_iam_role" "[a-z_]+" \{\n\s*name\s*=\s*"([^"]+)"', _IAM))
assert CELL_ROLES, "iam.tf declares no aws_iam_role with a name"

ECS = f"arn:aws:ecs:{REGION}:{ACCOUNT}"
EC2 = f"arn:aws:ec2:{REGION}:{ACCOUNT}"
CLUSTER_ARN = f"{ECS}:cluster/{cell.CLUSTER}"
SERVICE_ARN = f"{ECS}:service/{cell.CLUSTER}/{cell.SERVICE}"
GROUP = "sg-0123456789abcdef0"
# The cell's session table under session_store = "dynamodb", by the name infra/modules/cell/iam.tf
# builds: ${local.name_prefix}-sessions, read from the module rather than typed.
TABLE = re.search(r'session_table_name\s*=\s*"([^"]+)"', _IAM)[1].replace("${local.name_prefix}", cell.FAMILY)
TABLE_ARN = f"arn:aws:dynamodb:{REGION}:{ACCOUNT}:table/{TABLE}"
RULES = [f"sgr-0123456789abcde{n}" for n in "0123"]


def tagged(arn: str, kind: str, value: str = PLATFORM_VALUE, **state) -> dict:
    return {"arn": arn, "type": kind, "root": "platform", "tags": {"Platform": value, "Cell": CELL}, **state}


def torn_down_world() -> dict:
    """The platform project after a correct teardown, as #118 step 2 read it: no root's state, the
    tagging index still listing ECS's INACTIVE records and EC2 ids EC2 no longer knows, bootstrap's
    bucket and two roles under their own value, and ECS's service-linked role, untagged."""
    world = cell.default_world()
    world["roots"] = {"idp": None, "apps": None, "platform": None}
    world["service"] = None
    world["tasks"] = []
    first = f"{cell.FAMILY}:1"
    world["task_definitions"] = {first: {"status": "INACTIVE", "containers": {}}}
    world["roles"] = {name: role for name, role in world["roles"].items() if not role.get("root")}
    world["tagged"] = [
        tagged(f"arn:aws:s3:::spa-platform-{CELL}-tfstate-{ACCOUNT}", "s3:bucket", BOOTSTRAP_VALUE),
        tagged(CLUSTER_ARN, "ecs:cluster", status="INACTIVE"),
        tagged(SERVICE_ARN, "ecs:service", status="INACTIVE"),
        tagged(cell.td_arn(first), "ecs:task-definition"),
        tagged(f"{EC2}:security-group/{GROUP}", "ec2:security-group", describe_knows=0),
    ] + [tagged(f"{EC2}:security-group-rule/{rule}", "ec2:security-group-rule", describe_knows=0) for rule in RULES]
    return world


def setUpModule():
    cell.bash_banner("verify_teardown_tests")


def fast(case, wait: str = "2"):
    return case.env(SPA_TEARDOWN_TAG_WAIT=wait, SPA_TEARDOWN_TAG_POLL="0")


class Verify(cell.CompanionTestCase):

    def verify(self, world: dict, *args: str, wait: str = "2"):
        case = cell.Case(self, world=world)
        result = case.run(SCRIPT, *args, env=fast(case, wait))
        self.assertNoUnexpectedCall(case)
        return case, result

    def index_queries(self, case) -> list[dict]:
        return case.aws("resourcegroupstaggingapi get-resources")


class ATornDownCellPasses(Verify):
    """#118 step 2's records are what a correct teardown leaves, and pass."""

    def test_step_2_s_records_pass_with_one_line_per_reader(self):
        case, result = self.verify(torn_down_world())
        self.assertEqual(0, result.status, result.output)
        lines = [line for line in result.stdout.splitlines() if line.startswith("verify-teardown: ")]
        for reader in ("IAM roles by tag", "IAM customer managed policies by tag", "the cell's roles by name",
                       f"the tag query in {REGION}"):
            self.assertEqual(1, sum(f": {reader}: " in line for line in lines), f"{reader}\n{result.output}")
        self.assertIn("pass:", lines[-1])
        self.assertIn("P-05", lines[-1])
        # What the index listed and why each was dropped is named, so a reader of the sitting's log
        # sees step 2's records accounted for.
        self.assertIn("3 ECS records INACTIVE", result.stdout)
        self.assertIn("5 EC2 ids describe does not know", result.stdout)

    def test_the_tag_query_is_the_contract_s_exact_value_in_the_selected_region(self):
        case, _ = self.verify(torn_down_world())
        query = self.index_queries(case)[-1]
        self.assertEqual([f"Key=Platform,Values={PLATFORM_VALUE}"], query["options"]["tag-filters"])
        self.assertNotIn("resource-type-filters", query["options"])
        self.assertEqual(REGION, query["options"]["region"])

    def test_every_role_s_tags_are_read_and_bootstrap_s_value_does_not_answer(self):
        world = torn_down_world()
        case, result = self.verify(world)
        self.assertEqual(0, result.status, result.output)
        read = sorted(call["options"]["role-name"] for call in case.aws("iam list-role-tags"))
        self.assertEqual(sorted(world["roles"]), read)
        self.assertIn(cell.TEARDOWN_ROLE, read)

    def test_the_names_checked_are_the_roles_iam_tf_declares(self):
        case, _ = self.verify(torn_down_world())
        self.assertEqual(CELL_ROLES, sorted(call["options"]["role-name"] for call in case.aws("iam get-role")))
        self.assertEqual(2, len(CELL_ROLES))

    def test_the_cell_option_names_another_cell_s_roles(self):
        case, _ = self.verify(torn_down_world(), "--cell", "b")
        self.assertEqual(sorted(name.replace(cell.FAMILY, "spa-platform-b") for name in CELL_ROLES),
                         sorted(call["options"]["role-name"] for call in case.aws("iam get-role")))

    def test_a_task_definition_teardown_deleted_passes(self):
        world = torn_down_world()
        world["task_definitions"][f"{cell.FAMILY}:1"]["status"] = "DELETE_IN_PROGRESS"
        _, result = self.verify(world)
        self.assertEqual(0, result.status, result.output)

    def test_a_task_definition_ecs_has_removed_passes(self):
        # The index still lists a revision DescribeTaskDefinition no longer knows: "Unable to describe
        # task definition", a ClientException.
        world = torn_down_world()
        world["task_definitions"] = {}
        _, result = self.verify(world)
        self.assertEqual(0, result.status, result.output)
        self.assertIn("1 ECS records deleted", result.stdout)

    def test_a_cluster_and_a_service_ecs_answers_missing_for_pass(self):
        # The index still lists a cluster and a service ECS no longer knows: a failure with reason
        # MISSING, which is the one answer that says they are gone.
        world = torn_down_world()
        for entry in world["tagged"][1:3]:
            entry["describe_failure"] = "MISSING"
        _, result = self.verify(world)
        self.assertEqual(0, result.status, result.output)
        self.assertIn("2 ECS records deleted", result.stdout)

    def test_an_ec2_id_describe_still_knows_passes_once_it_lets_go(self):
        # The index lags a deletion, and so does EC2's describe for a moment: retried within the
        # plan's few minutes (docs/plan/05-infrastructure-and-cost.md, Teardown).
        world = torn_down_world()
        world["tagged"][4]["describe_knows"] = 2
        case, result = self.verify(world)
        self.assertEqual(0, result.status, result.output)
        self.assertEqual(3, len(self.index_queries(case)))
        self.assertEqual(3, len(case.aws("ec2 describe-security-groups")))

    def test_a_reader_that_passed_is_not_asked_again(self):
        world = torn_down_world()
        world["tagged"][4]["describe_knows"] = 2
        case, _ = self.verify(world)
        self.assertEqual(1, len(case.aws("iam list-roles")))

    def test_a_table_dynamodb_no_longer_knows_passes_and_is_counted(self):
        # The index lags a DeleteTable as it lags every deletion: a table DescribeTable answers
        # ResourceNotFoundException for is gone, and the summary says so.
        world = torn_down_world()
        world["tagged"].append(tagged(TABLE_ARN, "dynamodb:table", describe_knows=0))
        case, result = self.verify(world)
        self.assertEqual(0, result.status, result.output)
        self.assertEqual([TABLE], [call["options"]["table-name"] for call in case.aws("dynamodb describe-table")])
        self.assertIn("1 DynamoDB tables DynamoDB does not know", result.stdout)

    def test_a_deleting_table_passes_once_it_is_gone(self):
        world = torn_down_world()
        world["tagged"].append(tagged(TABLE_ARN, "dynamodb:table", status="DELETING", describe_knows=2))
        case, result = self.verify(world)
        self.assertEqual(0, result.status, result.output)
        self.assertEqual(3, len(case.aws("dynamodb describe-table")))

    def test_the_tagging_index_s_own_lag_is_retried(self):
        world = torn_down_world()
        world["tag_lag"] = 2
        case, result = self.verify(world)
        self.assertEqual(0, result.status, result.output)
        self.assertEqual(3, len(self.index_queries(case)))


class AStandingResourceFails(Verify):
    """Each case fails through exactly one reader, and names what stands without the account."""

    def failed(self, world: dict, reader: str):
        case, result = self.verify(world)
        self.assertEqual(1, result.status, result.output)
        self.assertNotIn("pass: nothing", result.stdout)
        failed = [line for line in result.stderr.splitlines() if line.startswith("verify-teardown: ") and ": fail:" in line]
        self.assertTrue(any(f": {reader}: fail:" in line for line in failed), result.output)
        self.assertEqual(1, sum(": fail:" in line and not line.startswith("verify-teardown: fail:") for line in failed),
                         result.output)
        return case, result

    def test_a_live_ecs_service(self):
        world = torn_down_world()
        world["tagged"][2]["status"] = "ACTIVE"
        _, result = self.failed(world, f"the tag query in {REGION}")
        self.assertIn(f"arn:aws:ecs:{REGION}:<account>:service/{cell.CLUSTER}/{cell.SERVICE}", result.stderr)

    def test_a_live_ecs_cluster(self):
        world = torn_down_world()
        world["tagged"][1]["status"] = "ACTIVE"
        self.failed(world, f"the tag query in {REGION}")

    def test_an_ecs_failure_other_than_missing(self):
        world = torn_down_world()
        world["tagged"][1]["describe_failure"] = "ACCESS_DENIED"
        _, result = self.failed(world, f"the tag query in {REGION}")
        self.assertIn("ECS says ACCESS_DENIED", result.stderr)

    def test_an_ecs_describe_with_neither_a_status_nor_a_failure(self):
        world = torn_down_world()
        world["tagged"][2]["describe_failure"] = ""
        _, result = self.failed(world, f"the tag query in {REGION}")
        self.assertIn("neither a status nor a failure reason", result.stderr)

    def test_an_active_task_definition(self):
        world = torn_down_world()
        world["task_definitions"][f"{cell.FAMILY}:1"]["status"] = "ACTIVE"
        self.failed(world, f"the tag query in {REGION}")

    def test_a_security_group_describe_still_knows(self):
        world = torn_down_world()
        del world["tagged"][4]["describe_knows"]
        case, result = self.failed(world, f"the tag query in {REGION}")
        self.assertIn(f"security-group/{GROUP}", result.stderr)
        # Retried until the wait ran out, not judged on the first answer.
        self.assertGreater(len(self.index_queries(case)), 1)

    def test_a_resource_type_no_describe_confirms(self):
        world = torn_down_world()
        world["tagged"].append(tagged(f"arn:aws:logs:{REGION}:{ACCOUNT}:log-group:/spa-platform/poc/stray", "logs:log-group"))
        _, result = self.failed(world, f"the tag query in {REGION}")
        self.assertIn(f"arn:aws:logs:{REGION}:<account>:log-group:/spa-platform/poc/stray", result.stderr)

    def test_a_surviving_session_table(self):
        # #338: the table goes with the platform root, and one that survives it fails the verdict by
        # name, whatever DynamoDB says of its status.
        world = torn_down_world()
        world["tagged"].append(tagged(TABLE_ARN, "dynamodb:table", status="ACTIVE"))
        case, result = self.failed(world, f"the tag query in {REGION}")
        self.assertIn(f"arn:aws:dynamodb:{REGION}:<account>:table/{TABLE} (DynamoDB says ACTIVE)", result.stderr)
        self.assertGreater(len(self.index_queries(case)), 1)

    def test_a_table_describe_refused(self):
        world = torn_down_world()
        world["tagged"].append(tagged(TABLE_ARN, "dynamodb:table", status="ACTIVE"))
        world["faults"]["dynamodb describe-table"] = ["AccessDeniedException", "DescribeTable"]
        _, result = self.verify(world)
        self.assertEqual(1, result.status, result.output)
        self.assertIn("AccessDeniedException", result.stderr)
        self.assertNotIn("pass:", result.stdout)

    def test_a_role_carrying_the_platform_tag_under_a_name_the_cell_does_not_declare(self):
        # An orphan from an interrupted apply, or a role added later: the name list cannot see it.
        world = torn_down_world()
        world["roles"]["spa-platform-poc-orphan"] = {"tags": {"Platform": PLATFORM_VALUE}}
        _, result = self.failed(world, "IAM roles by tag")
        self.assertIn("spa-platform-poc-orphan", result.stderr)

    def test_a_standing_cell_role_found_by_name(self):
        # Standing without its tags, so the tag reader cannot see it and the name reader does.
        world = torn_down_world()
        world["roles"][CELL_ROLES[0]] = {"tags": {}}
        _, result = self.failed(world, "the cell's roles by name")
        self.assertIn(CELL_ROLES[0], result.stderr)

    def test_a_customer_managed_policy_carrying_the_platform_tag(self):
        world = torn_down_world()
        arn = f"arn:aws:iam::{ACCOUNT}:policy/spa-platform-poc-shared"
        world["policies"] = {arn: {"tags": {"Platform": PLATFORM_VALUE}},
                             f"arn:aws:iam::{ACCOUNT}:policy/spa-platform-poc-ci-plan": {"tags": {"Platform": BOOTSTRAP_VALUE}}}
        _, result = self.failed(world, "IAM customer managed policies by tag")
        self.assertIn("arn:aws:iam::<account>:policy/spa-platform-poc-shared", result.stderr)


class AReaderThatCannotJudgeFails(Verify):
    """A check fails closed (CODING_STANDARDS.md): an answer that is neither found nor not-found is
    a failure, never a pass."""

    def test_a_task_definition_client_exception_that_is_not_a_removed_revision(self):
        # ECS answers ClientException for a caller without permission as well as for a revision it
        # removed (API_DescribeTaskDefinition, Errors): only the removed revision's message passes.
        world = torn_down_world()
        world["faults"]["ecs describe-task-definition"] = ["ClientException", "DescribeTaskDefinition"]
        _, result = self.verify(world)
        self.assertEqual(1, result.status, result.output)
        self.assertIn("ClientException", result.stderr)
        self.assertNotIn("pass:", result.stdout)

    def test_a_describe_refused(self):
        world = torn_down_world()
        world["faults"]["ec2 describe-security-groups"] = ["UnauthorizedOperation", "DescribeSecurityGroups"]
        _, result = self.verify(world)
        self.assertEqual(1, result.status, result.output)
        self.assertIn("UnauthorizedOperation", result.stderr)
        self.assertNotIn("pass:", result.stdout)

    def test_a_listing_refused(self):
        world = torn_down_world()
        world["faults"]["iam list-roles"] = ["AccessDenied", "ListRoles"]
        _, result = self.verify(world)
        self.assertEqual(1, result.status, result.output)
        self.assertIn("AccessDenied", result.stderr)
        self.assertNotIn("pass:", result.stdout)


class VerifyRefusesToStart(Verify):

    def test_an_unknown_argument(self):
        case, result = self.verify(torn_down_world(), "--yes")
        self.assertEqual(2, result.status, result.output)
        self.assertEqual([], case.calls())

    def test_a_cell_name_that_is_not_one(self):
        case, result = self.verify(torn_down_world(), "--cell", "Poc")
        self.assertEqual(2, result.status, result.output)
        self.assertEqual([], case.calls())

    def test_a_wait_that_is_not_whole_seconds(self):
        case = cell.Case(self, world=torn_down_world())
        result = case.run(SCRIPT, env=case.env(SPA_TEARDOWN_TAG_WAIT="5m"))
        self.assertEqual(2, result.status, result.output)
        self.assertEqual([], case.calls())


if __name__ == "__main__":
    sys.exit(cell.main("verify_teardown_tests"))
