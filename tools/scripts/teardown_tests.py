#!/usr/bin/env python3
"""Test companion for tools/scripts/teardown.sh. Owner: workstream 1.

Task 1.7 (#58): "Teardown ends with a tag query that must return nothing" (P-05), with the exact
value contracts/resource-tags.md fixes, and it reads the bucket from the platform root's state so a
bucket_name override is honoured (change request 1.5a). Since #308 that query is one of
verify-teardown.sh's readers, which teardown.sh ends by running; verify_teardown_tests.py proves the
readers, and these cases that teardown.sh ends on them and takes their verdict. The doubles, the world they share and the
universal every run holds (no output names the account id) are tools/scripts/cell_doubles.py's.

Run: python3 -I tools/scripts/teardown_tests.py
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

SCRIPT = "teardown.sh"
ACCOUNT, REGION, CELL = cell.ACCOUNT, cell.REGION, cell.CELL

# contracts/resource-tags.md, read rather than typed: the platform tag and the bootstrap's value.
_TAGS = (cell.ROOT / "contracts" / "resource-tags.md").read_text()
PLATFORM_VALUE = re.search(r"\| `Platform` \| `([^`]+)` \| Every resource", _TAGS)[1]
BOOTSTRAP_VALUE = re.search(r"\| `Platform` \| `([^`]+)` \|\n", _TAGS)[1]
CERTIFICATE = f"arn:aws:acm:{REGION}:{ACCOUNT}:certificate/0123abcd-0000-1111-2222-333344445555"
# The cell's ring prefix, /spa-platform/{cell}/data-protection/ (P2-2 decision 8), read from the cell
# module's variable rather than typed, and the key names the package writes under it.
_VARIABLES = (cell.MODULE / "variables.tf").read_text()
RING_PREFIX = re.search(r'data_protection_parameter_prefix == "(/spa-platform/)\$\{var\.cell\}(/data-protection/)"',
                        _VARIABLES).expand(rf"\g<1>{CELL}\g<2>")
RING_KEYS = list(cell.RING_KEYS[:2])
assert all(name.startswith(RING_PREFIX) for name in cell.RING_KEYS), "cell_doubles.RING_KEYS is not under the module's prefix"
OTHER_RING_KEY = "/spa-platform/other/data-protection/key-00000009-aaaa-bbbb-cccc-ddddeeeeffff"


def tagged(arn: str, kind: str, root: str, value: str = PLATFORM_VALUE, cell_name: str = CELL) -> dict:
    return {"arn": arn, "type": kind, "root": root, "tags": {"Platform": value, "Cell": cell_name}}


def deployed_world(bucket: str = cell.BUCKET) -> dict:
    """A cell after deploy.sh: releases and registry versions in the bucket, three task definition
    revisions, and every root's resources tagged, beside the bootstrap's three, which are not."""
    world = cell.default_world()
    world["roots"]["platform"] = cell.platform_outputs(f"{cell.ECR_GATEWAY}@{cell.digest_of('gateway-a')}", bucket)
    world["objects"] = [
        {"Key": "apps/app1/releases/fixture-aaaa/index.html", "VersionId": "v1"},
        {"Key": "apps/app1/releases/fixture-aaaa/assets/app.js", "VersionId": "v2"},
        {"Key": "apps/app2/releases/fixture-bbbb/index.html", "VersionId": "v3"},
        {"Key": "apps/p04-probe/releases/20261020T000000Z/index.html", "VersionId": "v4", "marker": True},
        {"Key": "registry/registry.json", "VersionId": "r1"},
        {"Key": "registry/registry.json", "VersionId": "r2"},
    ]
    world["task_definitions"] = {
        f"{cell.FAMILY}:1": {"status": "INACTIVE", "containers": {}},
        f"{cell.FAMILY}:2": {"status": "INACTIVE", "containers": {}},
        f"{cell.FAMILY}:3": {"status": "ACTIVE", "containers": {}},
        # Another cell's family, which the prefix spa-platform-poc also matches.
        "spa-platform-poc2:1": {"status": "INACTIVE", "containers": {}},
    }
    world["service"]["taskDefinition"] = f"{cell.FAMILY}:3"
    world["tagged"] = [
        tagged(f"arn:aws:s3:::{bucket}", "s3:bucket", "platform"),
        tagged(f"arn:aws:elasticloadbalancing:{REGION}:{ACCOUNT}:loadbalancer/app/spa-{CELL}/0123", "elasticloadbalancing:loadbalancer", "platform"),
        tagged(f"arn:aws:ecs:{REGION}:{ACCOUNT}:cluster/{cell.CLUSTER}", "ecs:cluster", "platform"),
        tagged(f"arn:aws:cognito-idp:{REGION}:{ACCOUNT}:userpool/{REGION}_EXAMPLE", "cognito-idp:userpool", "idp"),
        tagged(f"arn:aws:ssm:{REGION}:{ACCOUNT}:parameter/spa-platform/poc/apps/app1/client-secret", "ssm:parameter", "apps"),
        tagged(CERTIFICATE, "acm:certificate", "certs"),
        tagged(f"arn:aws:s3:::spa-platform-{CELL}-tfstate-{ACCOUNT}", "s3:bucket", "bootstrap", BOOTSTRAP_VALUE),
    ] + [tagged(cell.td_arn(f"{cell.FAMILY}:{n}"), "ecs:task-definition", "platform") for n in (1, 2, 3)]
    # The key ring the gateway wrote under the cell's prefix, tagged on create (#274) and outside every
    # root's state, so no destroy removes it; and a parameter under another cell's prefix, which
    # teardown leaves alone. That one is untagged here: Phase 1 matches the platform tag alone, so a
    # second cell's tagged ring would answer P-05 whatever teardown did (contracts/resource-tags.md).
    for name in RING_KEYS:
        world["parameters"].append(name)
        world["tagged"].append(tagged(f"arn:aws:ssm:{REGION}:{ACCOUNT}:parameter{name}", "ssm:parameter", "gateway"))
    world["parameters"].append(OTHER_RING_KEY)
    return world


def setUpModule():
    cell.bash_banner("teardown_tests")


def fast(case, wait: str = "2"):
    return case.env(SPA_TEARDOWN_TAG_WAIT=wait, SPA_TEARDOWN_TAG_POLL="0")


class TeardownEmptiesDestroysAndProvesNothingIsTagged(cell.CompanionTestCase):
    """docs/plan/05-infrastructure-and-cost.md, Teardown, in its order, ending on P-05."""

    def torn_down(self, world: dict | None = None):
        case = cell.Case(self, world=world or deployed_world())
        (case.repo / "infra/envs/poc/platform/images.auto.tfvars").write_text('gateway_image = "x"\n')
        result = case.run(SCRIPT, "--yes", env=fast(case))
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        return case, result

    def test_it_ends_on_the_platform_tag_query_answering_nothing(self):
        case, result = self.torn_down()
        queries = [c for c in case.aws("resourcegroupstaggingapi get-resources")
                   if "resource-type-filters" not in c["options"]]
        self.assertGreaterEqual(len(queries), 1)
        # Exactly the contract's value, as the supported exact-match filter, and nothing else.
        self.assertEqual([f"Key=Platform,Values={PLATFORM_VALUE}"], queries[-1]["options"]["tag-filters"])
        self.assertEqual(queries[-1], case.calls("aws")[-1], "the tag query is not the last call")
        self.assertIn("pass:", result.stdout)
        self.assertIn("P-05", result.stdout)
        # The tag query cannot list IAM roles, so the pass line does not claim them (#118).
        self.assertIn("IAM roles", result.stdout)

    def test_the_bootstrap_s_resources_do_not_answer_and_are_left(self):
        case, _ = self.torn_down()
        left = [entry for entry in case.load()["tagged"]]
        self.assertEqual([BOOTSTRAP_VALUE], [entry["tags"]["Platform"] for entry in left])

    def test_releases_are_deleted_as_the_teardown_role_and_the_registry_as_the_operator(self):
        case, _ = self.torn_down()
        deletes = case.aws("s3api delete-object")
        self.assertEqual(6, len(deletes))
        for call in deletes:
            expected = "teardown" if call["options"]["key"].startswith("apps/") else "operator"
            self.assertEqual(expected, call["as"], call["options"]["key"])
        self.assertEqual([], case.load()["objects"])
        assume = case.aws("sts assume-role")[0]["options"]
        self.assertEqual(cell.TEARDOWN_ROLE_ARN, assume["role-arn"])

    def test_the_bucket_is_the_platform_root_s_output_so_an_override_is_honoured(self):
        # change request 1.5a: a bucket_name override, still in the account regional namespace.
        override = f"custom-name-{ACCOUNT}-{REGION}-an"
        case, _ = self.torn_down(deployed_world(override))
        buckets = {c["options"]["bucket"] for c in case.calls("aws") if c.get("command", "").startswith("s3api")}
        self.assertEqual({override}, buckets)

    def test_the_roots_are_destroyed_platform_then_apps_then_idp(self):
        case, _ = self.torn_down()
        destroys = [c for c in case.calls("terraform") if c.get("command") == "destroy"]
        self.assertEqual(["platform", "apps", "idp"], [c["root"] for c in destroys])
        # After the bucket is empty, or S3 would refuse the platform root's destroy.
        calls = case.calls()
        last_delete = max(i for i, c in enumerate(calls) if c.get("command") == "s3api delete-object")
        self.assertLess(last_delete, calls.index(destroys[0]))
        self.assertEqual(cell.digest_of("gateway-a"), destroys[0]["vars"]["gateway_image"].split("@")[1])

    def test_images_auto_tfvars_goes_with_the_platform_root(self):
        case, _ = self.torn_down()
        self.assertFalse((case.repo / "infra/envs/poc/platform/images.auto.tfvars").exists())

    def test_the_certificate_is_deleted_after_the_platform_root(self):
        case, _ = self.torn_down()
        calls = case.calls()
        delete = next(c for c in calls if c.get("command") == "acm delete-certificate")
        self.assertEqual(CERTIFICATE, delete["options"]["certificate-arn"])
        platform = next(c for c in calls if c.get("command") == "destroy" and c.get("root") == "platform")
        self.assertLess(calls.index(platform), calls.index(delete))
        lookup = next(c for c in case.aws("resourcegroupstaggingapi get-resources") if "resource-type-filters" in c["options"])
        self.assertEqual(sorted([f"Key=Platform,Values={PLATFORM_VALUE}", f"Key=Cell,Values={CELL}"]),
                         sorted(lookup["options"]["tag-filters"]))

    def test_a_certificate_acm_still_counts_in_use_is_retried_until_it_lets_go(self):
        # The listener's association outlives it for a while, and the AWS provider retries this
        # error on its own certificate deletes (terraform-provider-aws v6.68.0,
        # internal/service/acm/certificate.go). Review of #58.
        world = deployed_world()
        world["faults"]["certificate_in_use"] = 3
        case, result = self.torn_down(world)
        outcomes = [c["outcome"] for c in case.aws("acm delete-certificate")]
        self.assertEqual(["ResourceInUseException"] * 3 + ["success"], outcomes)
        self.assertIn("pass:", result.stdout)

    def test_a_certificate_already_deleted_counts_as_deleted(self):
        world = deployed_world()
        world["faults"]["certificate_gone"] = True
        case, result = self.torn_down(world)
        self.assertEqual(["ResourceNotFoundException"], [c["outcome"] for c in case.aws("acm delete-certificate")])
        self.assertIn("deleted already", result.stdout)

    def test_the_cell_s_inactive_task_definitions_are_deleted_and_no_other_cell_s(self):
        case, _ = self.torn_down()
        deleted = [name for call in case.aws("ecs delete-task-definitions")
                   for name in (ref.split("/")[-1] for ref in call["options"]["task-definitions"])]
        self.assertEqual(sorted(f"{cell.FAMILY}:{n}" for n in (1, 2, 3)), sorted(deleted))
        self.assertEqual("INACTIVE", case.load()["task_definitions"]["spa-platform-poc2:1"]["status"])

    def test_more_than_ten_inactive_task_definitions_go_ten_at_a_time(self):
        world = deployed_world()
        for n in range(4, 26):
            world["task_definitions"][f"{cell.FAMILY}:{n}"] = {"status": "INACTIVE", "containers": {}}
        case, _ = self.torn_down(world)
        sizes = [len(call["options"]["task-definitions"]) for call in case.aws("ecs delete-task-definitions")]
        self.assertEqual([10, 10, 5], sizes)

    def test_the_key_ring_is_deleted_after_the_platform_root_and_before_the_proof(self):
        # P2-2 decision 17: the gateway creates the ring at run time, outside Terraform, so no destroy
        # removes it, and a cell rebuilt under the same name would load the old ring. Deleted after
        # the platform root, so no task is left to write a new key, and before verify-teardown.sh.
        case, result = self.torn_down()
        calls = case.calls()
        listing = case.aws("ssm get-parameters-by-path")
        self.assertEqual(1, len(listing))
        self.assertEqual(RING_PREFIX.rstrip("/"), listing[0]["options"]["path"])
        self.assertIs(True, listing[0]["options"]["recursive"])
        deletes = case.aws("ssm delete-parameters")
        self.assertEqual([sorted(RING_KEYS)], [sorted(call["options"]["names"]) for call in deletes])
        platform = next(c for c in calls if c.get("command") == "destroy" and c.get("root") == "platform")
        proof = next(c for c in calls if c.get("command") == "iam list-roles")
        self.assertLess(calls.index(platform), calls.index(listing[0]))
        self.assertLess(calls.index(deletes[-1]), calls.index(proof))
        self.assertIn("deleted 2 key ring parameters under " + RING_PREFIX, result.stdout)

    def test_another_cell_s_ring_is_left(self):
        case, _ = self.torn_down()
        self.assertEqual([OTHER_RING_KEY], case.load()["parameters"])

    def test_an_empty_ring_prefix_is_not_an_error(self):
        # A memory cell with no ring, or a second teardown: nothing under the prefix, nothing deleted.
        world = deployed_world()
        world["parameters"] = [OTHER_RING_KEY]
        world["tagged"] = [entry for entry in world["tagged"] if not entry["arn"].endswith(tuple(RING_KEYS))]
        case, result = self.torn_down(world)
        self.assertEqual(1, len(case.aws("ssm get-parameters-by-path")))
        self.assertEqual([], case.aws("ssm delete-parameters"))
        self.assertIn("deleted 0 key ring parameters under " + RING_PREFIX, result.stdout)

    def test_more_than_ten_ring_parameters_go_ten_at_a_time(self):
        # DeleteParameters takes at most ten names a call.
        # https://docs.aws.amazon.com/systems-manager/latest/APIReference/API_DeleteParameters.html
        world = deployed_world()
        for name in cell.RING_KEYS[2:]:
            world["parameters"].append(name)
            world["tagged"].append(tagged(f"arn:aws:ssm:{REGION}:{ACCOUNT}:parameter{name}", "ssm:parameter", "gateway"))
        case, _ = self.torn_down(world)
        self.assertEqual([10, 10, 3], [len(call["options"]["names"]) for call in case.aws("ssm delete-parameters")])
        self.assertEqual([OTHER_RING_KEY], case.load()["parameters"])

    def test_the_query_retries_while_the_tagging_api_lags(self):
        world = deployed_world()
        world["tag_lag"] = 2
        case, _ = self.torn_down(world)
        queries = [c for c in case.aws("resourcegroupstaggingapi get-resources")
                   if "resource-type-filters" not in c["options"]]
        self.assertEqual(3, len(queries))

    def test_it_ends_on_verify_teardown_whose_readers_each_report(self):
        # #308: teardown.sh ends by running verify-teardown.sh, whose readers see what the tag query
        # cannot (#118), and its pass line is the last thing teardown prints.
        case, result = self.torn_down()
        lines = result.stdout.splitlines()
        self.assertTrue(lines[-1].startswith("verify-teardown: pass:"), result.output)
        for reader in ("IAM roles by tag", "IAM customer managed policies by tag", "the cell's roles by name",
                       f"the tag query in {REGION}"):
            self.assertTrue(any(line.startswith(f"verify-teardown: {reader}: ") for line in lines), reader)
        self.assertEqual(sorted(f"{cell.FAMILY}-{suffix}" for suffix in ("task", "task-execution")),
                         sorted(c["options"]["role-name"] for c in case.aws("iam get-role")
                                if c["options"]["role-name"] != cell.TEARDOWN_ROLE))

    def test_what_a_correct_destroy_leaves_listed_passes(self):
        # #118 step 2, after the platform root's destroy: the index went on listing the INACTIVE
        # cluster and service, and a security group and four rules EC2 no longer knew. teardown.sh on
        # main before #308 exited 1 on exactly that.
        world = deployed_world()
        ec2 = f"arn:aws:ec2:{REGION}:{ACCOUNT}"
        world["tagged"] += [
            dict(tagged(f"arn:aws:ecs:{REGION}:{ACCOUNT}:service/{cell.CLUSTER}/{cell.SERVICE}", "ecs:service", "platform"),
                 after_destroy={"status": "INACTIVE"}),
            dict(tagged(f"{ec2}:security-group/sg-0123456789abcdef0", "ec2:security-group", "platform"),
                 after_destroy={"describe_knows": 0}),
        ] + [dict(tagged(f"{ec2}:security-group-rule/sgr-0123456789abcde{n}", "ec2:security-group-rule", "platform"),
                  after_destroy={"describe_knows": 0}) for n in "0123"]
        cluster = next(entry for entry in world["tagged"] if entry["type"] == "ecs:cluster")
        cluster["after_destroy"] = {"status": "INACTIVE"}
        case, result = self.torn_down(world)
        self.assertIn("2 ECS records INACTIVE", result.stdout)
        self.assertIn("5 EC2 ids describe does not know", result.stdout)
        self.assertEqual(7, len([e for e in case.load()["tagged"] if e["tags"]["Platform"] == PLATFORM_VALUE]))

    def test_an_empty_bucket_and_destroyed_state_still_end_on_the_query(self):
        world = deployed_world()
        world["roots"]["platform"] = None
        world["objects"] = []
        case, _ = self.torn_down(world)
        self.assertEqual([], case.aws("s3api list-object-versions"))
        self.assertEqual(["platform", "apps", "idp"],
                         [c["root"] for c in case.calls("terraform") if c.get("command") == "destroy"])


class TeardownFails(cell.CompanionTestCase):

    def test_a_resource_still_tagged_after_the_wait_fails_and_is_named_without_the_account(self):
        world = deployed_world()
        stray = f"arn:aws:logs:{REGION}:{ACCOUNT}:log-group:/spa-platform/poc/stray"
        world["tagged"].append(tagged(stray, "logs:log-group", "nobody"))
        case = cell.Case(self, world=world)
        result = case.run(SCRIPT, "--yes", env=fast(case))
        self.assertEqual(1, result.status, result.output)
        self.assertIn("log-group:/spa-platform/poc/stray", result.stderr)
        self.assertIn("<account>", result.stderr)
        self.assertNotIn("pass:", result.stdout)

    def test_its_exit_status_is_verify_teardown_s(self):
        # A cell role Terraform's state never held, so the destroy leaves it standing; it has lost its
        # tags, so only the name reader sees it (#308).
        world = deployed_world()
        world["roles"][f"{cell.FAMILY}-task"] = {"tags": {}}
        case = cell.Case(self, world=world)
        result = case.run(SCRIPT, "--yes", env=fast(case))
        self.assertEqual(1, result.status, result.output)
        self.assertIn("verify-teardown: the cell's roles by name: fail:", result.stderr)
        self.assertIn(f"  {cell.FAMILY}-task", result.stderr)
        self.assertNotIn("pass:", result.stdout)
        self.assertNoUnexpectedCall(case)

    def test_a_certificate_in_use_past_the_wait_fails_and_says_so(self):
        world = deployed_world()
        world["faults"]["certificate_in_use"] = 1000
        case = cell.Case(self, world=world)
        result = case.run(SCRIPT, "--yes", env=case.env(SPA_TEARDOWN_TAG_WAIT="2", SPA_TEARDOWN_TAG_POLL="0",
                                                        SPA_TEARDOWN_CERT_WAIT="3"))
        self.assertEqual(1, result.status, result.output)
        self.assertIn("still in use after 3 seconds", result.stderr)
        self.assertEqual(4, len(case.aws("acm delete-certificate")))
        self.assertNotIn("pass:", result.stdout)
        self.assertNoUnexpectedCall(case)

    def test_a_ring_deletion_refused_fails_before_the_proof(self):
        world = deployed_world()
        world["faults"]["ssm delete-parameters"] = ["AccessDeniedException", "DeleteParameters"]
        case = cell.Case(self, world=world)
        result = case.run(SCRIPT, "--yes", env=fast(case))
        self.assertEqual(1, result.status, result.output)
        self.assertIn("could not delete the key ring parameters", result.stderr)
        self.assertEqual([], case.aws("iam list-roles"))
        self.assertNoUnexpectedCall(case)

    def test_a_version_left_in_the_bucket_stops_before_any_destroy(self):
        # A version written after the listing is not deleted, and the count after the deletes says
        # so: the platform root is never destroyed with the bucket still holding it (#192). Without
        # that check the destroy runs and S3 refuses the bucket's deletion, which this case tells
        # apart by the message and by the destroy it ran.
        world = deployed_world()
        world["faults"]["written_after_listing"] = True
        case = cell.Case(self, world=world)
        result = case.run(SCRIPT, "--yes", env=fast(case))
        self.assertEqual(1, result.status, result.output)
        self.assertIn("the bucket still holds 1 versions and 0 delete markers.", result.stderr)
        self.assertEqual([], [c for c in case.calls("terraform") if c.get("command") == "destroy"])
        self.assertNoUnexpectedCall(case)

    def test_a_failed_destroy_stops_before_the_next_root(self):
        world = deployed_world()
        world["faults"]["destroy platform"] = True
        case = cell.Case(self, world=world)
        result = case.run(SCRIPT, "--yes", env=fast(case))
        self.assertEqual(1, result.status, result.output)
        self.assertEqual(["platform"], [c["root"] for c in case.calls("terraform") if c.get("command") == "destroy"])
        self.assertEqual([], case.aws("resourcegroupstaggingapi get-resources"))

    def test_a_release_the_operator_deletes_is_refused_by_the_bucket(self):
        # The double's bucket policy is storage.tf's: a delete under apps/ by anyone but the teardown
        # role is AccessDenied. A teardown that skipped the role would stop here.
        world = deployed_world()
        world["faults"]["sts assume-role"] = ["AccessDenied", "AssumeRole"]
        case = cell.Case(self, world=world)
        result = case.run(SCRIPT, "--yes", env=fast(case))
        self.assertEqual(1, result.status, result.output)
        self.assertIn("could not assume the teardown role", result.stderr)
        self.assertEqual([], [c for c in case.calls("terraform") if c.get("command") == "destroy"])


class TeardownRefusesToStart(cell.CompanionTestCase):

    def test_without_yes(self):
        case = cell.Case(self, world=deployed_world())
        result = case.run(SCRIPT)
        self.assertEqual(2, result.status, result.output)
        self.assertIn("--yes", result.stderr)
        self.assertEqual([], case.calls())

    def test_as_the_teardown_role(self):
        world = deployed_world()
        world["caller"] = f"arn:aws:sts::{ACCOUNT}:assumed-role/{cell.TEARDOWN_ROLE}/someone"
        case = cell.Case(self, world=world)
        result = case.run(SCRIPT, "--yes", env=fast(case))
        self.assertEqual(2, result.status, result.output)
        self.assertEqual([], case.calls("terraform"))

    def test_a_cell_name_that_is_not_one(self):
        case = cell.Case(self, world=deployed_world())
        result = case.run(SCRIPT, "--yes", "--cell", "Poc")
        self.assertEqual(2, result.status, result.output)
        self.assertEqual([], case.calls())


if __name__ == "__main__":
    sys.exit(cell.main("teardown_tests"))
