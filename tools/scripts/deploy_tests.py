#!/usr/bin/env python3
"""Test companion for tools/scripts/deploy.sh. Owner: workstream 1.

Task 1.7 (#58): "Deployment builds and pushes by digest, refuses to start a gateway against an empty
registry, applies, waits for steady state, then asserts the running digest." The doubles, the world
they share and the universal every run holds (no output names the account id) are
tools/scripts/cell_doubles.py's; its docstring says what a case can and cannot show.

Run: python3 -I tools/scripts/deploy_tests.py
"""

from __future__ import annotations

import importlib.util
import json
import pathlib
import sys
import tempfile

_spec = importlib.util.spec_from_file_location("cell_doubles", pathlib.Path(__file__).resolve().parent / "cell_doubles.py")
cell = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(cell)

import unittest  # noqa: E402

SCRIPT = "deploy.sh"


def seeded_world() -> dict:
    """After seed.sh: the two fixture apps in the live registry."""
    world = cell.default_world()
    world["registry"] = {"schemaVersion": 1, "revision": 2,
                         "apps": [{"appId": "app1", "release": "r1"}, {"appId": "app2", "release": "r1"}]}
    world["objects"] = [{"Key": "registry/registry.json", "VersionId": "r2"}]
    return world


def setUpModule():
    cell.bash_banner("deploy_tests")


class DeployBuildsPushesAppliesAndAsserts(cell.CompanionTestCase):
    """docs/plan/05-infrastructure-and-cost.md, First deployment, step 5, and Deployment and rollback:
    "deploy.sh builds, pushes, applies the new digest and checks that the running task has it"."""

    def deployed(self, world: dict | None = None):
        case = cell.Case(self, outputs=True, world=world or seeded_world())
        result = case.run(SCRIPT)
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        return case, result

    def test_both_images_are_built_for_arm64_from_the_repository_root_and_pushed(self):
        case, _ = self.deployed()
        builds = [call for call in case.calls("docker") if call["argv"][:2] == ["buildx", "build"]]
        self.assertEqual(2, len(builds))
        for call, dockerfile, repository in ((builds[0], "src/Gateway/Dockerfile", cell.ECR_GATEWAY),
                                             (builds[1], "fixtures/MockApi/Dockerfile", cell.ECR_MOCK_API)):
            argv = call["argv"]
            self.assertEqual("linux/arm64", argv[argv.index("--platform") + 1])
            self.assertEqual(str(case.repo / dockerfile), argv[argv.index("--file") + 1])
            self.assertIn("--push", argv)
            self.assertEqual(str(case.repo), argv[-1])
            self.assertTrue(call["tag"].startswith(repository + ":"), call["tag"])
            # One manifest, so the digest pushed is the digest a task reports.
            self.assertIn("--provenance=false", argv)
            self.assertIn("--sbom=false", argv)

    def test_each_build_pulls_its_base_image_afresh(self):
        # #288: without --pull, a workstation whose cache holds an older
        # mcr.microsoft.com/dotnet/aspnet:10.0-noble builds on it, and could ship a runtime that
        # predates the advisories the floating tag's current image fixes (#273).
        case, _ = self.deployed()
        builds = [call for call in case.calls("docker") if call["argv"][:2] == ["buildx", "build"]]
        self.assertEqual(2, len(builds))
        for call in builds:
            self.assertIn("--pull", call["argv"], call["argv"])

    def test_the_apply_names_each_image_by_the_digest_ecr_holds_and_the_health_path_is_readyz(self):
        case, _ = self.deployed()
        world = case.load()
        gateway_tag = next(c["tag"] for c in case.calls("docker") if c.get("tag", "").startswith(cell.ECR_GATEWAY))
        mock_tag = next(c["tag"] for c in case.calls("docker") if c.get("tag", "").startswith(cell.ECR_MOCK_API))
        # The digest is ECR's, read back by tag, not one the script made up or took from Docker.
        gateway = f"{cell.ECR_GATEWAY}@{world['repositories'][cell.GATEWAY_REPOSITORY][gateway_tag.rpartition(':')[2]]}"
        mock = f"{cell.ECR_MOCK_API}@{world['repositories'][cell.MOCK_API_REPOSITORY][mock_tag.rpartition(':')[2]]}"
        applies = [c for c in case.calls("terraform") if c.get("command") == "apply"]
        self.assertEqual(1, len(applies))
        self.assertEqual({"gateway_image": gateway, "mock_api_image": mock, "health_check_path": "/readyz"},
                         applies[0]["vars"])
        self.assertEqual("platform", applies[0]["root"])

    def test_the_saved_plan_is_what_is_applied(self):
        case, _ = self.deployed()
        terraform = [c for c in case.calls("terraform") if c.get("command") in ("plan", "apply")]
        self.assertEqual(["plan", "apply"], [c["command"] for c in terraform])
        plan_file = next(t for t in terraform[0]["argv"] if t.startswith("-out="))[len("-out="):]
        self.assertEqual(plan_file, terraform[1]["argv"][-1])
        # Outside the tree: a plan holds the values its apply writes (change request 1.1).
        self.assertFalse(pathlib.Path(plan_file).resolve().is_relative_to(case.repo)
                         if hasattr(pathlib.Path, "is_relative_to") else str(plan_file).startswith(str(case.repo)))

    def test_every_later_apply_passes_the_same_digests_back(self):
        case, _ = self.deployed()
        applied = next(c for c in case.calls("terraform") if c.get("command") == "apply")["vars"]
        tfvars = (case.repo / "infra/envs/poc/platform/images.auto.tfvars").read_text()
        self.assertIn(f'gateway_image     = "{applied["gateway_image"]}"', tfvars)
        self.assertIn(f'mock_api_image    = "{applied["mock_api_image"]}"', tfvars)
        self.assertIn('health_check_path = "/readyz"', tfvars)

    def test_it_waits_for_a_steady_service_after_the_apply(self):
        case, result = self.deployed()
        order = [c.get("command") for c in case.calls() if c.get("command") in ("apply", "ecs wait services-stable")]
        self.assertEqual(["apply", "ecs wait services-stable"], order)
        wait = case.aws("ecs wait services-stable")[0]["options"]
        self.assertEqual(cell.CLUSTER, wait["cluster"])
        self.assertEqual([cell.SERVICE], wait["services"])
        self.assertIn("pass:", result.stdout)

    def test_it_reads_no_terraform_state_but_the_outputs_it_writes_after_the_apply(self):
        # contracts/infra-outputs.schema.json: the scripts read outputs.json, and none of them reads
        # Terraform state; Q49 on #129 excepts teardown.sh alone of these four. Review of #58.
        case, _ = self.deployed()
        terraform = case.calls("terraform")
        outputs = [c for c in terraform if c.get("command") == "output"]
        self.assertEqual([], [c for c in outputs if "-raw" in c["argv"]])
        apply = next(index for index, c in enumerate(terraform) if c.get("command") == "apply")
        self.assertEqual(3, len(outputs))
        self.assertTrue(all(terraform.index(c) > apply for c in outputs), "an output was read before the apply")

    def test_it_writes_the_outputs_document_with_the_running_image(self):
        case, _ = self.deployed()
        document = json.loads((case.repo / "infra/envs/poc/outputs.json").read_text())
        schema = json.loads((cell.ROOT / "contracts/infra-outputs.schema.json").read_text())
        self.assertEqual(sorted(schema["required"]), sorted(document))
        applied = next(c for c in case.calls("terraform") if c.get("command") == "apply")["vars"]
        self.assertEqual(applied["gateway_image"], document["gatewayImage"])

    def test_the_login_password_goes_to_docker_on_standard_input(self):
        case, _ = self.deployed()
        login = next(c for c in case.calls("docker") if c["argv"][:1] == ["login"])
        self.assertTrue(login["password_on_stdin"])
        self.assertEqual(["login", "--username", "AWS", "--password-stdin", cell.REGISTRY_HOST], login["argv"])
        for call in case.calls():
            self.assertNotIn(cell.PASSWORD, json.dumps(call["argv"]))

    def test_the_previous_tasks_target_draining_beside_the_new_tasks_healthy_one_is_steady(self):
        # The sitting of #58, stage C1 (2026-10-21): the service stable, the new task's target
        # healthy on /readyz and the previous task's draining for the deregistration delay, and
        # deploy.sh exited 1 with "the target group's targets are: healthy draining" (#207). The
        # doubles' apply leaves the group so on every deployment; this case names it.
        case, result = self.deployed()
        world = case.load()
        new_task = world["tasks"][0]
        self.assertEqual(sorted([[cell.task_ip(new_task), "healthy"], [cell.task_ip(cell.default_world()["tasks"][0]), "draining"]]),
                         sorted([t["id"], t["state"]] for t in world["target_group"]["targets"]))
        self.assertIn("pass:", result.stdout)

    def test_the_plan_prints_no_client_id_from_the_outputs_document(self):
        # #251: the plan shows the mock API's environment, which carries both apps' client ids. The
        # client ids are not in cell_doubles.SECRETS (logs_tests.py says why), so this case searches
        # for them itself, and names what took their place so a script that dropped the plan's
        # lines would not pass.
        world = seeded_world()
        _, result = self.deployed(world)
        for app, entry in world["roots"]["apps"]["clients"].items():
            self.assertNotIn(entry["clientId"], result.output, f"{app}'s client id was printed")
        self.assertIn('name = "MockApi__App1Client", value = "<client-id:app1>"', result.stdout)
        self.assertIn('name = "MockApi__App2Client", value = "<client-id:app2>"', result.stdout)

    def test_it_waits_until_every_running_task_reports_the_registry_revision_step_1_read(self):
        # Change request P2-5, decision 6 (#309): after the service is steady, spactl wait covers
        # each running task by its id, the last segment of its ARN and never the ARN, which carries
        # the account id, at the revision of the registry document step 1 read.
        world = seeded_world()
        world["registry"]["revision"] = 7
        case, result = self.deployed(world)
        waits = [c for c in case.calls("dotnet") if c["spactl"][:1] == ["wait"]]
        self.assertEqual(1, len(waits))
        args = waits[0]["spactl"]
        running = [task["id"] for task in case.load()["tasks"]]
        self.assertEqual(",".join(running), args[args.index("--instances") + 1])
        self.assertEqual("7", args[args.index("--revision") + 1])
        # The tunnel's origin by default: the ALB is internal (tools/scripts/tunnel.sh).
        self.assertEqual("https://app1.spa-poc.test:8443", args[args.index("--url") + 1])
        order = [c.get("command") or c["spactl"][0] for c in case.calls()
                 if c.get("command") == "ecs wait services-stable" or c.get("spactl", [""])[:1] == ["wait"]]
        self.assertEqual(["ecs wait services-stable", "wait"], order)
        self.assertIn("pass:", result.stdout)

    def test_the_wait_takes_the_origin_given_with_url(self):
        case = cell.Case(self, outputs=True, world=seeded_world())
        result = case.run(SCRIPT, "--url", "https://app2.spa-poc.test:9443")
        self.assertEqual(0, result.status, result.output)
        wait = next(c["spactl"] for c in case.calls("dotnet") if c["spactl"][:1] == ["wait"])
        self.assertEqual("https://app2.spa-poc.test:9443", wait[wait.index("--url") + 1])

    def test_two_deployments_push_two_tags(self):
        case = cell.Case(self, outputs=True, world=seeded_world())
        self.assertEqual(0, case.run(SCRIPT).status)
        second = case.run(SCRIPT)
        self.assertEqual(0, second.status, second.output)
        tags = [c["tag"] for c in case.calls("docker") if c.get("tag", "").startswith(cell.ECR_GATEWAY)]
        self.assertEqual(2, len(set(tags)))


class DeployRefusesAnEmptyRegistry(cell.CompanionTestCase):
    """contracts/README.md: "Deployment refuses to start a gateway against an empty registry (task
    1.7)". Before anything is built: nothing reaches Docker or Terraform's plan."""

    def refused(self, world: dict, fragment: str):
        case = cell.Case(self, outputs=True, world=world)
        result = case.run(SCRIPT)
        self.assertEqual(1, result.status, result.output)
        self.assertIn(fragment, result.stderr)
        self.assertEqual([], [c for c in case.calls("docker") if c["argv"][:2] == ["buildx", "build"]])
        self.assertEqual([], [c for c in case.calls("terraform") if c.get("command") in ("plan", "apply")])
        self.assertNoUnexpectedCall(case)
        return case

    def test_a_registry_with_no_app(self):
        world = seeded_world()
        world["registry"]["apps"] = []
        self.refused(world, "holds no app")

    def test_no_registry_at_all(self):
        world = cell.default_world()
        self.refused(world, "Run tools/scripts/seed.sh first")

    def test_a_registry_with_no_revision_to_wait_for(self):
        # Step 5 waits for the revision step 1 read (#309), so a document without one stops before
        # anything is built. contracts/registry.schema.json: an integer of at least 1.
        for revision in (None, 0, True, "2"):
            with self.subTest(revision=revision):
                world = seeded_world()
                world["registry"]["revision"] = revision
                self.refused(world, "has no revision")

    def test_it_reads_the_bucket_and_key_the_gateway_reads(self):
        world = seeded_world()
        case = self.refused_or_passed(world)
        get = case.aws("s3api get-object")[0]["options"]
        environment = dict(world["task_definitions"][f"{cell.FAMILY}:1"]["containers"][cell.GATEWAY]["environment"])
        self.assertEqual(environment["Registry__Bucket"], get["bucket"])
        self.assertEqual(environment["Registry__Key"], get["key"])

    def refused_or_passed(self, world: dict):
        case = cell.Case(self, outputs=True, world=world)
        result = case.run(SCRIPT)
        self.assertEqual(0, result.status, result.output)
        return case


class DeployFailsUnlessTheAppliedGatewayRuns(cell.CompanionTestCase):
    """Stable is not deployed. Each way the running service can differ from the apply is exit 1."""

    def failed(self, world: dict, fragment: str):
        case = cell.Case(self, outputs=True, world=world)
        result = case.run(SCRIPT)
        self.assertEqual(1, result.status, result.output)
        self.assertIn(fragment, result.stderr)
        self.assertNoUnexpectedCall(case)
        self.assertNotIn("pass:", result.stdout)
        return case

    def test_the_circuit_breaker_rolled_it_back(self):
        world = seeded_world()
        world["faults"]["circuit_breaker"] = True
        self.failed(world, "rolled it back")

    def test_a_running_task_with_another_digest(self):
        world = seeded_world()
        world["faults"]["running_digest"] = "sha256:" + "e" * 64
        self.failed(world, "is not the applied gateway")

    def test_the_service_never_stable(self):
        world = seeded_world()
        world["faults"]["wait"] = True
        self.failed(world, "did not become stable")

    def test_a_rollout_that_did_not_complete(self):
        world = seeded_world()
        world["faults"]["rollout"] = "FAILED"
        self.failed(world, "rollout is FAILED")

    def test_an_unhealthy_target(self):
        world = seeded_world()
        world["faults"]["new_target"] = "unhealthy"
        self.failed(world, "targets are: draining unhealthy")

    def test_a_target_in_a_state_other_than_healthy(self):
        # unhealthy.draining is in TargetHealth's State enum and was in no list of bad states, so a
        # deploy whose target was in it exited 0 (the review of PR #186, S2). One healthy target
        # beside it does not make the group healthy, and only draining itself is tolerated (#207).
        # https://docs.aws.amazon.com/elasticloadbalancing/latest/APIReference/API_TargetHealth.html
        world = seeded_world()
        world["faults"]["extra_targets"] = [["10.0.9.9", "unhealthy.draining"]]
        self.failed(world, "targets are: draining healthy unhealthy.draining")

    def test_a_target_in_any_state_but_healthy_or_draining_fails_beside_a_healthy_new_task(self):
        # Every other value of TargetHealth's State, each as a second target beside the new task's
        # healthy one and the previous task's draining one (#207, the second box).
        for state in ("initial", "unhealthy", "unhealthy.draining", "unused", "unavailable"):
            with self.subTest(state=state):
                world = seeded_world()
                world["faults"]["extra_targets"] = [["10.0.9.9", state]]
                self.failed(world, f"targets are: draining healthy {state}.")

    def test_the_new_tasks_target_draining_is_not_tolerated(self):
        # Draining is tolerated as the previous task's state, never as the new one's: the new task
        # has to be healthy on /readyz (#207, the first box).
        world = seeded_world()
        world["faults"]["new_target"] = "draining"
        self.failed(world, "is not healthy on /readyz: draining")

    def test_the_new_task_not_registered_while_another_target_is_healthy(self):
        # A healthy target that is no running task's does not stand in for the new task's.
        world = seeded_world()
        world["faults"]["new_target"] = None
        world["faults"]["extra_targets"] = [["10.0.9.9", "healthy"]]
        self.failed(world, "is not healthy on /readyz: not registered")

    def test_only_draining_targets(self):
        world = seeded_world()
        world["faults"]["new_target"] = None
        self.failed(world, "is not healthy on /readyz: not registered")

    # The four cases below each turn one check of cell-common.sh's wait_and_assert red when that
    # check is removed, and no other check catches what they set up (#192, from the review of #186,
    # P1): the merge body names each mutation.

    def test_a_previous_deployment_not_drained(self):
        world = seeded_world()
        world["faults"]["deployments"] = 2
        self.failed(world, "has 2 deployments; the previous one has not drained")

    def test_a_running_task_of_another_revision_with_the_applied_digest(self):
        # The digest matches, so only the comparison of each running task's definition catches it:
        # the task is named with the revision it runs, the cell's first.
        world = seeded_world()
        world["faults"]["running_definition"] = True
        case = cell.Case(self, outputs=True, world=world)
        result = case.run(SCRIPT)
        self.assertEqual(1, result.status, result.output)
        self.assertIn("is not the applied gateway", result.stderr)
        self.assertIn(f": RUNNING, {cell.FAMILY}:1, gateway digest", result.stderr)
        self.assertNoUnexpectedCall(case)

    def test_a_target_group_that_does_not_check_readyz(self):
        world = seeded_world()
        world["faults"]["health_path_stale"] = True
        self.failed(world, "the target group checks /, not /readyz.")

    def test_an_outputs_document_that_names_another_gateway_image(self):
        world = seeded_world()
        world["faults"]["output_image_stale"] = True
        self.failed(world, "the outputs document's gatewayImage is ")

    def test_a_failed_apply_records_no_images_and_waits_for_nothing(self):
        world = seeded_world()
        world["faults"]["apply"] = True
        case = self.failed(world, "apply failed")
        self.assertFalse((case.repo / "infra/envs/poc/platform/images.auto.tfvars").exists())
        self.assertEqual([], case.aws("ecs wait services-stable"))

    def test_a_refused_docker_login_builds_nothing_and_names_no_account(self):
        # Docker names the registry it could not log in to, and the registry's host is the account id
        # (#191). Case.run holds the account-id universal, so this is the case where docker login's
        # stream can fail it.
        world = seeded_world()
        world["faults"]["login"] = True
        case = self.failed(world, "could not log Docker in to the cell's registry.")
        self.assertEqual([], [c for c in case.calls("docker") if c["argv"][:2] == ["buildx", "build"]])
        self.assertEqual([], [c for c in case.calls("terraform") if c.get("command") in ("plan", "apply")])

    def test_a_running_task_never_sampled_fails_the_wait(self):
        # #309's second box. The load balancer hands each read to the task it picks, so a task it
        # never picks has never said which revision it serves: the wait times out naming it by its
        # id, and deploy.sh fails rather than passing on the other task's answer.
        world = seeded_world()
        world["faults"]["extra_tasks"] = 1
        world["faults"]["unsampled"] = 1
        case = cell.Case(self, outputs=True, world=world)
        result = case.run(SCRIPT)
        self.assertEqual(1, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        self.assertNotIn("pass:", result.stdout)
        sampled, unsampled = case.load()["tasks"]
        self.assertIn(f"instance {unsampled['id']} never answered", result.stderr)
        self.assertNotIn(f"instance {sampled['id']} never answered", result.stderr)
        self.assertIn("did not report revision 2", result.stderr)
        wait = next(c["spactl"] for c in case.calls("dotnet") if c["spactl"][:1] == ["wait"])
        self.assertEqual(f"{sampled['id']},{unsampled['id']}", wait[wait.index("--instances") + 1])

    def test_a_task_behind_the_revision_step_1_read_fails_the_wait(self):
        world = seeded_world()
        world["faults"]["behind"] = 1
        self.failed(world, "last answered revision 1")

    def test_a_failed_build_applies_nothing(self):
        world = seeded_world()
        world["faults"]["build"] = True
        case = self.failed(world, "building src/Gateway/Dockerfile failed")
        self.assertEqual([], [c for c in case.calls("terraform") if c.get("command") in ("plan", "apply")])


class DeployRefusesToStart(cell.CompanionTestCase):
    def refused(self, case, fragment: str, env=None):
        result = case.run(SCRIPT, env=env)
        self.assertEqual(2, result.status, result.output)
        self.assertIn(fragment, result.stderr)
        self.assertEqual([], [c for c in case.calls("docker") if c["argv"][:2] == ["buildx", "build"]])

    def test_without_docker(self):
        case = cell.Case(self, outputs=True, world=seeded_world())
        case.remove("docker")
        self.refused(case, "brew install --cask docker-desktop")

    def test_without_a_region(self):
        case = cell.Case(self, outputs=True, world=seeded_world())
        self.refused(case, "no Region", env=case.env(AWS_REGION=None, AWS_DEFAULT_REGION=None))
        self.assertEqual([], case.calls("aws"))

    def test_as_the_teardown_role(self):
        world = seeded_world()
        world["caller"] = f"arn:aws:sts::{cell.ACCOUNT}:assumed-role/{cell.TEARDOWN_ROLE}/someone"
        self.refused(cell.Case(self, outputs=True, world=world), "teardown role")

    def test_without_the_outputs_document(self):
        # contracts/infra-outputs.schema.json: the scripts read outputs.json and none of them reads
        # Terraform state, teardown.sh excepted (Q49 on #129). Review of #58.
        case = cell.Case(self, world=seeded_world())
        self.refused(case, "Run tools/scripts/seed.sh first")
        self.assertEqual([], case.calls("terraform"))

    def test_an_outputs_document_for_another_region(self):
        case = cell.Case(self, outputs=True, world=seeded_world())
        path = case.repo / "infra/envs/poc/outputs.json"
        document = json.loads(path.read_text())
        document["region"] = "us-west-2"
        path.write_text(json.dumps(document))
        self.refused(case, f"gives no ecrGateway for {cell.REGION}")
        self.assertEqual([], case.calls("terraform"))


class TheDoublesWorldIsReplacedWholeByRename(unittest.TestCase):
    """deploy.sh pipes the aws double into the docker double for the ECR login, so two doubles hold
    world.json at once, and cell_doubles.save_world writes it by rename so that a reader sees the old
    world or the new one, never half of one (c1f4c2a). A rename is checked here directly rather than
    by racing the pipe, which loses about 11 runs in 1000 written in place (the review of #186, P9):
    a reader that opened the file before a save goes on reading the world it opened, whole, which a
    write in place, truncating that same file, does not leave it."""

    def test_a_reader_open_across_a_save_reads_the_world_it_opened(self):
        with tempfile.TemporaryDirectory() as scratch:
            case = pathlib.Path(scratch)
            old = cell.default_world()
            cell.save_world(case, old)
            new = seeded_world()
            with open(case / "world.json") as reader:
                cell.save_world(case, new)
                self.assertEqual(old, json.loads(reader.read()))
            self.assertEqual(new, json.loads((case / "world.json").read_text()))
            self.assertEqual(["world.json"], sorted(path.name for path in case.iterdir()),
                             "a staged world was left beside world.json")


if __name__ == "__main__":
    sys.exit(cell.main("deploy_tests"))
