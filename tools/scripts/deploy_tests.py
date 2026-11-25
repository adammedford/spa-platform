#!/usr/bin/env python3
"""Test companion for tools/scripts/deploy.sh. Owner: workstream 1.

Task 1.7 (#58): "Deployment builds and pushes by digest, refuses to start a gateway against an empty
registry, applies, waits for steady state, then asserts the running digest." The doubles, the world
they share and the universal every run holds (no output names the account id) are
tools/scripts/cell_doubles.py's; its docstring says what a case can and cannot show.

The proof cell's entry file sets every case's entry values (#1348), so a change to it runs this
companion.

Checkout inputs: infra/envs/poc/cell.env infra/envs/poc/outputs-fields.json tools/scripts/poc/mock-api.sh

Run: python3 -I tools/scripts/deploy_tests.py
"""

from __future__ import annotations

import contextlib
import importlib.util
import json
import os
import pathlib
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time

_spec = importlib.util.spec_from_file_location("cell_doubles", pathlib.Path(__file__).resolve().parent / "cell_doubles.py")
cell = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(cell)
# The fields the scripts write, from the field map they write them from (the proof cell's, which
# its entry file names; #1348), not from the schema's root required: the contract may require fewer
# of every cell than this cell's scripts write (#1367). The optional ones are left out.
WRITTEN_FIELDS = sorted(entry["field"] for entry in cell.field_map()["fields"] if not entry.get("optional"))

import unittest  # noqa: E402

SCRIPT = "deploy.sh"


def seeded_world(bastion: bool = True) -> dict:
    """After seed.sh: the two fixture apps in the live registry, on a cell with the bastion on (#1167),
    which the readiness wait's own tunnel goes through unless --url names an origin."""
    world = cell.with_bastion(cell.default_world()) if bastion else cell.default_world()
    world["registry"] = {"schemaVersion": 1, "revision": 2,
                         "apps": [{"appId": "app1", "host": "app1.spa-poc.test:8443", "release": "r1"},
                                  {"appId": "app2", "host": "app2.spa-poc.test:8443", "release": "r1"}]}
    world["objects"] = [{"Key": "registry/registry.json", "VersionId": "r2"}]
    return world


def write_mock_api_tfvars(case, image: str):
    """The mock API's tfvars in the case's platform root, under the name tools/scripts/poc/mock-api.sh
    writes, as that script leaves it after a push (#1349)."""
    (case.repo / "infra/envs/poc/platform" / cell.mock_api_tfvars_name()).write_text(f'mock_api_image = "{image}"\n')


def tfvars_names(text: str) -> list[str]:
    """The variables a tfvars file assigns, sorted."""
    return sorted(line.split("=", 1)[0].strip() for line in text.splitlines()
                  if "=" in line and not line.lstrip().startswith("#"))


@contextlib.contextmanager
def listening():
    """A listener on a free port of 127.0.0.1 that accepts and closes each connection: an origin the
    caller already reaches, or an operator's own tunnel."""
    server = socket.socket()
    server.bind(("127.0.0.1", 0))
    server.listen(8)
    server.settimeout(0.2)
    stop = threading.Event()

    def serve():
        while not stop.is_set():
            try:
                connection, _ = server.accept()
            except OSError:
                continue
            connection.close()

    thread = threading.Thread(target=serve, daemon=True)
    thread.start()
    try:
        yield server.getsockname()[1]
    finally:
        stop.set()
        thread.join()
        server.close()


def accepts(port: int) -> bool:
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=1):
            return True
    except OSError:
        return False


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

    def test_the_gateway_image_alone_is_built_for_arm64_from_the_repository_root_and_pushed(self):
        # #1349: the deploy an adopter's operator copies builds the product and no fixture. The
        # proof cell's mock API has a script of its own, tools/scripts/poc/mock-api.sh.
        case, _ = self.deployed()
        builds = [call for call in case.calls("docker") if call["argv"][:2] == ["buildx", "build"]]
        self.assertEqual(1, len(builds), builds)
        for call, dockerfile, repository in ((builds[0], "src/Gateway/Dockerfile", cell.ECR_GATEWAY),):
            argv = call["argv"]
            self.assertEqual("linux/arm64", argv[argv.index("--platform") + 1])
            self.assertEqual(str(case.repo / dockerfile), argv[argv.index("--file") + 1])
            self.assertIn("--push", argv)
            self.assertEqual(str(case.repo), argv[-1])
            self.assertTrue(call["tag"].startswith(repository + ":"), call["tag"])
            # BuildKit's SBOM and provenance attestations, pushed with the image in one image index
            # (#779, answer 3). Each option is asserted whole, so turning either off fails here.
            self.assertIn("--sbom=true", argv)
            self.assertIn("--provenance=mode=max", argv)
            self.assertEqual([], [a for a in argv if a in ("--sbom=false", "--provenance=false")], argv)

    def test_the_index_is_read_from_ecr_by_the_digest_applied(self):
        case, _ = self.deployed()
        applied = next(c for c in case.calls("terraform") if c.get("command") == "apply")["vars"]["gateway_image"]
        reads = case.aws("ecr batch-get-image")
        self.assertEqual([f"imageDigest={applied.rpartition('@')[2]}"], [r["options"]["image-ids"] for r in reads])
        self.assertEqual([cell.GATEWAY_REPOSITORY], [r["options"]["repository-name"] for r in reads])

    def test_the_manual_says_why_the_build_attests_citing_779_and_each_option_s_vendor_page(self):
        # #783's third box. As prose, whatever the line breaks.
        manual = (cell.SCRIPTS / SCRIPT).read_text().split("\n# Owner:")[0]
        text = " ".join(" ".join(line.lstrip("#").split()) for line in manual.splitlines())
        self.assertIn("#779, answer 3", text)
        self.assertIn("https://github.com/adammedford/spa-platform/issues/779#issuecomment-6767167834", text)
        self.assertIn("--sbom=true attaches BuildKit's SBOM attestation, an SPDX document", text)
        self.assertIn("(https://docs.docker.com/build/metadata/attestations/sbom/)", text)
        self.assertIn("--provenance=mode=max attaches its SLSA provenance attestation", text)
        self.assertIn("(https://docs.docker.com/build/metadata/attestations/slsa-provenance/)", text)
        self.assertNotIn("No provenance or SBOM attestation", (cell.SCRIPTS / SCRIPT).read_text())

    def test_each_build_pulls_its_base_image_afresh(self):
        # #288: both Dockerfiles pin their bases by digest (#781, #782), so this holds only for a
        # FROM line that names a tag alone: without --pull, a workstation whose cache holds an older
        # image of that tag builds on it, and could ship a runtime that predates the advisories the
        # tag's current image fixes (#273).
        case, _ = self.deployed()
        builds = [call for call in case.calls("docker") if call["argv"][:2] == ["buildx", "build"]]
        self.assertEqual(1, len(builds))
        for call in builds:
            self.assertIn("--pull", call["argv"], call["argv"])

    def test_the_apply_names_the_gateway_by_the_digest_ecr_holds_and_the_health_path_is_readyz(self):
        case, _ = self.deployed()
        world = case.load()
        gateway_tag = next(c["tag"] for c in case.calls("docker") if c.get("tag", "").startswith(cell.ECR_GATEWAY))
        # The digest is ECR's, read back by tag, not one the script made up or took from Docker.
        gateway = f"{cell.ECR_GATEWAY}@{world['repositories'][cell.GATEWAY_REPOSITORY][gateway_tag.rpartition(':')[2]]}"
        plans = [c for c in case.calls("terraform") if c.get("command") == "plan"]
        applies = [c for c in case.calls("terraform") if c.get("command") == "apply"]
        self.assertEqual(1, len(applies))
        # #1349: the gateway's image and health path, and no mock_api_image, which stays at what the
        # root's tfvars holds. gateway_writable_root is the sample's (#1380): the gateway is applied on
        # its read-only root, whatever terraform.tfvars still says from the sample's first apply.
        for call in plans + applies:
            self.assertEqual({"gateway_image": gateway, "health_check_path": "/readyz", "gateway_writable_root": "false"},
                             call["vars"])
            self.assertEqual([], [t for t in call["argv"] if "mock_api_image" in t], call["argv"])
        self.assertEqual("platform", applies[0]["root"])

    def test_it_reads_no_ecr_mock_api_and_names_no_fixtures_path(self):
        # #1349: deploying the product builds no fixture. An outputs document with no ecrMockApi, as
        # an adopter's cell writes it, deploys; and the script's text names neither.
        case = cell.Case(self, outputs=True, world=seeded_world())
        path = case.outputs_path()
        document = json.loads(path.read_text())
        del document["ecrMockApi"]
        path.write_text(json.dumps(document))
        result = case.run(SCRIPT)
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        text = (cell.SCRIPTS / SCRIPT).read_text()
        self.assertNotIn("ecrMockApi", text)
        self.assertNotIn("fixtures/", text)

    def test_the_mock_api_container_stays_at_the_digest_its_tfvars_holds(self):
        # The proof cell's mock API moves by its own script's file, which sorts after
        # images.auto.tfvars, so a stale mock_api_image an older deploy.sh left there loses (#1349).
        world = seeded_world()
        case = cell.Case(self, outputs=True, world=world)
        platform = case.repo / "infra/envs/poc/platform"
        stale = f"{cell.ECR_MOCK_API}@{cell.digest_of('mock-stale')}"
        current = f"{cell.ECR_MOCK_API}@{cell.digest_of('mock-current')}"
        (platform / "images.auto.tfvars").write_text(f'gateway_image = "x"\nmock_api_image    = "{stale}"\n')
        write_mock_api_tfvars(case, current)
        result = case.run(SCRIPT)
        self.assertEqual(0, result.status, result.output)
        world = case.load()
        running = world["task_definitions"][world["service"]["taskDefinition"]]["containers"]
        self.assertEqual(current, running[cell.MOCK_API]["image"])
        # And deploy.sh's rewrite of its own file drops the stale value.
        self.assertNotIn("mock_api_image", (platform / "images.auto.tfvars").read_text())

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
        self.assertIn('health_check_path = "/readyz"', tfvars)
        # Loaded after terraform.tfvars, so a later plain apply keeps the read-only root too (#1380).
        self.assertIn("\ngateway_writable_root = false\n", tfvars)
        # #1349: the gateway's values and nothing else; the mock API's digest is its own file's.
        self.assertEqual(["gateway_image", "gateway_writable_root", "health_check_path"], tfvars_names(tfvars))

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
        # Every field the scripts write, and the bastion's optional one, which this cell publishes (#1167).
        self.assertEqual(sorted(WRITTEN_FIELDS + ["ecsBastionService"]), sorted(document))
        self.assertEqual(cell.BASTION_SERVICE, document["ecsBastionService"])
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
        # The mock API is in the task from its own script's tfvars (#1349), and a replaced task
        # definition's plan shows it whichever image moved.
        world = seeded_world()
        case = cell.Case(self, outputs=True, world=world)
        write_mock_api_tfvars(case, f"{cell.ECR_MOCK_API}@{cell.digest_of('mock-a')}")
        result = case.run(SCRIPT)
        self.assertEqual(0, result.status, result.output)
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
        # Through the wait's own tunnel by default: the ALB is internal (#1008).
        port = case.aws("ssm start-session")[0]["port"]
        self.assertEqual(f"https://app1.spa-poc.test:{port}", args[args.index("--url") + 1])
        order = [c.get("command") or c["spactl"][0] for c in case.calls()
                 if c.get("command") == "ecs wait services-stable" or c.get("spactl", [""])[:1] == ["wait"]]
        self.assertEqual(["ecs wait services-stable", "wait"], order)
        self.assertIn("pass:", result.stdout)

    def test_two_running_tasks_both_healthy_and_both_sampled_pass(self):
        # The two-task cell (#347, P2-4): a roll under it leaves two running tasks of the applied
        # deployment, each with its own healthy target, and the wait covers both by id. Nothing in
        # deploy.sh counts tasks, which is what this case holds: the second task is one more target
        # to find healthy and one more id to wait on, not a different shape.
        world = seeded_world()
        world["faults"]["extra_tasks"] = 1
        case, result = self.deployed(world)
        self.assertIn("pass:", result.stdout)
        running = [task["id"] for task in case.load()["tasks"]]
        self.assertEqual(2, len(running))
        wait = next(c["spactl"] for c in case.calls("dotnet") if c["spactl"][:1] == ["wait"])
        self.assertEqual(",".join(running), wait[wait.index("--instances") + 1])
        world = case.load()
        self.assertEqual(sorted([[cell.task_ip(task), "healthy"] for task in world["tasks"]]
                                + [[cell.task_ip(cell.default_world()["tasks"][0]), "draining"]]),
                         sorted([t["id"], t["state"]] for t in world["target_group"]["targets"]))

    def test_the_wait_takes_the_origin_given_with_url_and_opens_no_tunnel(self):
        # --url names an origin the caller already reaches, so the wait reads through it and starts
        # no session (#1008). The listener stands in for that origin.
        with listening() as port:
            case = cell.Case(self, outputs=True, world=seeded_world())
            result = case.run(SCRIPT, "--url", f"https://app2.spa-poc.test:{port}")
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        wait = next(c["spactl"] for c in case.calls("dotnet") if c["spactl"][:1] == ["wait"])
        self.assertEqual(f"https://app2.spa-poc.test:{port}", wait[wait.index("--url") + 1])
        self.assertEqual([], case.aws("ssm start-session"))

    def test_two_deployments_push_two_tags(self):
        case = cell.Case(self, outputs=True, world=seeded_world())
        self.assertEqual(0, case.run(SCRIPT).status)
        second = case.run(SCRIPT)
        self.assertEqual(0, second.status, second.output)
        tags = [c["tag"] for c in case.calls("docker") if c.get("tag", "").startswith(cell.ECR_GATEWAY)]
        self.assertEqual(2, len(set(tags)))


class TheReadinessWaitOpensAndClosesItsOwnTunnel(cell.CompanionTestCase):
    """#1008. The load balancer is internal, so the wait reads /readyz through a Session Manager port
    forward. It used to read through an operator's tunnel, started by hand in the window between the
    steady line and the end of the wait, which sitting D missed: deploy.sh exited 1 after a healthy,
    steady deployment (#242). The wait now opens its own through tools/scripts/tunnel.sh, through a
    running task of the new deployment, on a port of its own, and closes it when it ends, on a pass, a
    fail or an interrupt. The session manager is cell_doubles.py's: a plugin that really listens, as
    the CLI's child, which spactl's wait double reads through."""

    def session(self, case, listened: bool = True) -> tuple[dict, int]:
        sessions = case.aws("ssm start-session")
        self.assertEqual(1, len(sessions), "the wait opens one tunnel")
        self.assertEqual("session", sessions[0]["outcome"], sessions[0])
        listening = [c for c in case.calls("session-manager-plugin") if c["event"] == "listening"]
        self.assertEqual([sessions[0]["port"]] if listened else [], [c["port"] for c in listening])
        return sessions[0], sessions[0]["port"]

    def assertClosed(self, port: int):
        """Nothing listens on the port, within a few seconds of the script's return: the plugin is
        killed with the CLI and gone once its process is reaped. One that outlived the script listens
        for cell_doubles.PLUGIN_LIFETIME seconds."""
        deadline = time.monotonic() + 5
        while accepts(port):
            self.assertLess(time.monotonic(), deadline, f"something still listens on the wait's port {port}")
            time.sleep(0.1)

    def test_the_wait_opens_a_tunnel_on_a_port_other_than_8443_and_reads_through_it(self):
        case = cell.Case(self, outputs=True, world=seeded_world())
        result = case.run(SCRIPT, env=case.env(SPA_TUNNEL_PORT=None))
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        session, port = self.session(case)
        self.assertNotEqual(8443, port)
        # Through the bastion's task, never a task of the deployment it waits on (#1167): the double
        # answers only a target that is a running bastion task's container.
        world = case.load()
        self.assertIn(session["task"], [task["id"] for task in world["bastion_tasks"]])
        self.assertNotIn(session["task"], [task["id"] for task in world["tasks"]])
        # The bastion service is listed once, by the tunnel; every other listing is the gateway's.
        lists = [c["options"]["service-name"] for c in case.aws("ecs list-tasks")]
        self.assertEqual(cell.BASTION_SERVICE, lists[-1])
        self.assertEqual({cell.SERVICE}, set(lists[:-1]))
        wait = next(c["spactl"] for c in case.calls("dotnet") if c["spactl"][:1] == ["wait"])
        self.assertEqual(f"https://app1.spa-poc.test:{port}", wait[wait.index("--url") + 1])
        reads = [c["received"] for c in case.calls("session-manager-plugin") if c["event"] == "connection"]
        self.assertIn("spactl wait", reads, "the wait read through the tunnel's port")
        # Opened after the service is steady, and before the wait reads.
        order = [c.get("command") or c["spactl"][0] for c in case.calls()
                 if c.get("command") in ("ecs wait services-stable", "ssm start-session") or c.get("spactl", [""])[:1] == ["wait"]]
        self.assertEqual(["ecs wait services-stable", "ssm start-session", "wait"], order)
        self.assertClosed(port)
        # Closed when the wait ends, before the pass line, and not only when the script exits.
        closed = f"closed the wait's tunnel on local port {port}"
        self.assertEqual(1, result.stdout.count(closed), result.stdout)
        self.assertLess(result.stdout.index(closed), result.stdout.index("pass:"))

    def test_an_operator_s_tunnel_is_left_alone(self):
        # The operator's tunnel holds $SPA_TUNNEL_PORT (tools/scripts/tunnel.sh); the wait's takes
        # another port, and the operator's still answers after the run.
        with listening() as operator:
            case = cell.Case(self, outputs=True, world=seeded_world())
            result = case.run(SCRIPT, env=case.env(SPA_TUNNEL_PORT=str(operator)))
            self.assertEqual(0, result.status, result.output)
            _, port = self.session(case)
            self.assertNotIn(port, (operator, 8443))
            self.assertTrue(accepts(operator), "the operator's tunnel stopped answering")
        self.assertClosed(port)

    def test_a_failed_wait_closes_the_tunnel(self):
        world = seeded_world()
        world["faults"]["behind"] = 1
        case = cell.Case(self, outputs=True, world=world)
        result = case.run(SCRIPT)
        self.assertEqual(1, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        self.assertIn("last answered revision 1", result.stderr)
        _, port = self.session(case)
        self.assertClosed(port)
        self.assertIn(f"closed the wait's tunnel on local port {port}", result.stdout)

    def test_an_interrupt_during_the_wait_closes_the_tunnel(self):
        # Ctrl-C in a terminal reaches the foreground process group, the script and the spactl it
        # waits on, and not the tunnel, which runs in a group of its own: so the script itself has to
        # close it. Hence the whole group here, and not the script's process alone.
        world = seeded_world()
        world["faults"]["hang"] = True
        case = cell.Case(self, outputs=True, world=world)
        process = subprocess.Popen([cell.BASH, str(case.script(SCRIPT))], cwd=case.dir, env=case.env(),
                                   stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   text=True, start_new_session=True)
        self.addCleanup(lambda: process.poll() is None and os.killpg(process.pid, signal.SIGKILL))
        deadline = time.monotonic() + 90
        while not (case.dir / "waiting").exists():
            self.assertIsNone(process.poll(), "deploy.sh ended before its wait read")
            self.assertLess(time.monotonic(), deadline, "the wait never started reading")
            time.sleep(0.1)
        _, port = self.session(case)
        self.assertTrue(accepts(port), "the tunnel is open while the wait reads")
        os.killpg(process.pid, signal.SIGINT)
        stdout, stderr = process.communicate(timeout=60)
        self.assertEqual(130, process.returncode, stdout + stderr)
        for secret in cell.SECRETS:
            self.assertNotIn(secret, stdout + stderr)
        self.assertNotIn("pass:", stdout)
        # Closed by the port, not by the closing line: bash runs the trap once spactl has returned,
        # inside the function's redirection of its output (cell-common.sh, close_wait_tunnel).
        self.assertClosed(port)

    def test_a_tunnel_that_ends_before_it_opens_fails_the_wait_by_its_error_code(self):
        # StartSession refused: tunnel.sh exits 254, the CLI's status for it (`aws help return-codes`),
        # and its line names the code and never the caller's ARN. The wait reads nothing.
        world = seeded_world()
        world["faults"]["session_refused"] = True
        case = cell.Case(self, outputs=True, world=world)
        result = case.run(SCRIPT, env=case.env(SPA_WAIT_TUNNEL_SECONDS="60"))
        self.assertEqual(1, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        self.assertIn("AccessDeniedException", result.stderr)
        self.assertIn("the wait's tunnel ended before it opened: tools/scripts/tunnel.sh exited 254", result.stderr)
        self.assertEqual([], [c for c in case.calls("dotnet") if c["spactl"][:1] == ["wait"]])
        self.assertNotIn("pass:", result.stdout)

    def test_a_tunnel_that_never_listens_fails_the_wait_at_its_bound_and_is_closed(self):
        world = seeded_world()
        world["faults"]["session_silent"] = True
        case = cell.Case(self, outputs=True, world=world)
        started = time.monotonic()
        result = case.run(SCRIPT, env=case.env(SPA_WAIT_TUNNEL_SECONDS="2"))
        self.assertLess(time.monotonic() - started, 60, "the CLI's session was not ended at the bound")
        self.assertEqual(1, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        _, port = self.session(case, listened=False)
        self.assertIn(f"the wait's tunnel did not open on local port {port} within 2 seconds", result.stderr)
        self.assertIn(f"closed the wait's tunnel on local port {port}", result.stdout)
        self.assertEqual([], [c for c in case.calls("dotnet") if c["spactl"][:1] == ["wait"]])

    def test_with_no_bastion_and_no_url_it_refuses_before_it_builds_naming_both(self):
        # #1168: the wait's tunnel needs the bastion, and deploy.sh never turns it on. The outputs
        # document names the last apply's cell, so a document with no ecsBastionService is refused
        # before anything is built or pushed, and the last line says what to do.
        case = cell.Case(self, outputs=True, world=seeded_world(bastion=False))
        result = case.run(SCRIPT)
        self.assertEqual(2, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        last = result.stderr.strip().splitlines()[-1]
        self.assertIn("bastion = true", last)
        self.assertIn("--url", last)
        self.assertEqual([], [c for c in case.calls("docker") if c["argv"][:2] == ["buildx", "build"]])
        self.assertEqual([], [c for c in case.calls("terraform") if c.get("command") in ("plan", "apply")])
        self.assertEqual([], case.aws("ssm start-session"))

    def test_with_no_bastion_and_a_url_it_passes(self):
        # --url names an origin the caller already reaches, so no tunnel is needed and no bastion.
        with listening() as port:
            case = cell.Case(self, outputs=True, world=seeded_world(bastion=False))
            result = case.run(SCRIPT, "--url", f"https://app1.spa-poc.test:{port}")
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        self.assertIn("pass:", result.stdout)
        self.assertEqual([], case.aws("ssm start-session"))
        self.assertNotIn(cell.BASTION_SERVICE, [c["options"]["service-name"] for c in case.aws("ecs list-tasks")])

    def test_an_apply_that_leaves_no_bastion_fails_the_wait_before_a_tunnel_naming_both(self):
        # The document named a bastion when the run started, but terraform.tfvars had since set it
        # false, so the deployment's own apply destroyed it: the wait fails, exit 1, before it opens
        # a tunnel, on the same line.
        world = seeded_world()
        world["faults"]["bastion_dropped"] = True
        case = cell.Case(self, outputs=True, world=world)
        result = case.run(SCRIPT)
        self.assertEqual(1, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        last = result.stderr.strip().splitlines()[-1]
        self.assertIn("bastion = true", last)
        self.assertIn("--url", last)
        self.assertEqual([], case.aws("ssm start-session"))
        self.assertEqual(1, len([c for c in case.calls("terraform") if c.get("command") == "apply"]))
        self.assertNotIn("pass:", result.stdout)

    def test_without_the_session_manager_plugin_it_refuses_before_it_builds(self):
        case = cell.Case(self, outputs=True, world=seeded_world())
        case.remove("session-manager-plugin")
        result = case.run(SCRIPT)
        self.assertEqual(2, result.status, result.output)
        self.assertIn("brew install --cask session-manager-plugin", result.stderr)
        self.assertEqual([], [c for c in case.calls("docker") if c["argv"][:2] == ["buildx", "build"]])

    def test_the_wait_s_header_names_the_tunnel_it_opens_and_closes(self):
        # #1008's second box: the comment above wait_for_revision, and the step of deploy.sh's
        # manual that runs it, name the tunnel the wait opens and closes, and neither says an
        # operator's tunnel must be running.
        common = (cell.SCRIPTS / "cell-common.sh").read_text()
        header = common[common.index("# --- convergence"):common.index("\nwait_for_revision() {")]
        manual = (cell.SCRIPTS / SCRIPT).read_text().split("\n# Owner:")[0]
        for comment in (header, manual):
            # As prose, whatever the line breaks: each line without its comment mark, joined.
            text = " ".join(" ".join(line.lstrip("#").split()) for line in comment.splitlines())
            self.assertIn("tools/scripts/tunnel.sh", text)
            self.assertIn("opens a tunnel of its own", text)
            self.assertIn("closes it when the wait ends", text)
            self.assertNotIn("has to be", text)
            self.assertNotIn("in another terminal", text)
        self.assertNotIn("restart tools/scripts/tunnel.sh", common)


class DeployRefusesAnEmptyRegistry(cell.CompanionTestCase):
    """contracts/README.md: "Deployment refuses to start a gateway against an empty registry (task
    1.7)". Before anything is built: nothing reaches Docker or Terraform's plan."""

    def refused(self, world: dict, fragment: str):
        case = cell.Case(self, outputs=True, world=world)
        result = case.run(SCRIPT)
        case.last = result
        self.assertEqual(1, result.status, result.output)
        self.assertIn(fragment, result.stderr)
        self.assertEqual([], [c for c in case.calls("docker") if c["argv"][:2] == ["buildx", "build"]])
        self.assertEqual([], [c for c in case.calls("terraform") if c.get("command") in ("plan", "apply")])
        self.assertNoUnexpectedCall(case)
        return case

    # #1349: an adopter's equivalent of the proof cell's seed.sh is these two commands, and no message
    # of a reference script names seed.sh.
    SPACTL = "publish an app with spactl: spactl release publish, then spactl app add"

    def test_a_registry_with_no_app(self):
        world = seeded_world()
        world["registry"]["apps"] = []
        case = self.refused(world, "holds no app")
        self.names_spactl_not_seed(case)

    def test_no_registry_at_all(self):
        world = cell.with_bastion(cell.default_world())
        case = self.refused(world, "there is no live registry")
        self.names_spactl_not_seed(case)

    def names_spactl_not_seed(self, case):
        stderr = case.last.stderr
        self.assertIn(self.SPACTL, stderr)
        self.assertNotIn("seed.sh", stderr)

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

    def test_a_rollout_in_progress_after_the_waiter_that_then_completes_passes(self):
        # #467: services-stable returned while the primary deployment was still IN_PROGRESS, and the
        # one read that followed failed a deployment that completed a minute later. While it is in
        # progress the double lists two deployments (a test device, see cell_doubles.py), so the
        # deployment count is shown to be read after the rollout settles.
        world = seeded_world()
        world["faults"]["rollout_in_progress"] = 3
        case = cell.Case(self, outputs=True, world=world)
        case.stub_sleep()
        result = case.run(SCRIPT)
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        self.assertEqual(4, len(self.rollout_reads(case)))
        # The rollout's three, and after them only the one-second polls of the wait's tunnel opening
        # (cell-common.sh, open_wait_tunnel), which the stub makes return at once.
        sleeps = case.sleeps()
        self.assertEqual([["15"]] * 3, sleeps[:3])
        self.assertEqual([], [s for s in sleeps[3:] if s != ["1"]])

    def test_a_rollout_in_progress_that_then_fails_fails_naming_failed(self):
        world = seeded_world()
        world["faults"]["rollout_in_progress"] = 2
        world["faults"]["rollout"] = "FAILED"
        case = self.failed(world, "rollout is FAILED, not COMPLETED", stub_sleep=True)
        self.assertEqual(3, len(self.rollout_reads(case)))
        self.assertEqual([["15"]] * 2, case.sleeps())

    def test_a_rollout_in_progress_past_the_window_fails_naming_in_progress(self):
        # The window is the waiter's own cadence: 40 reads 15 seconds apart, within its ten minutes.
        # https://awscli.amazonaws.com/v2/documentation/api/latest/reference/ecs/wait/services-stable.html
        world = seeded_world()
        world["faults"]["rollout_in_progress"] = 100
        case = self.failed(world, "rollout is IN_PROGRESS, not COMPLETED, after 40 reads 15 seconds apart",
                           stub_sleep=True)
        self.assertEqual(40, len(self.rollout_reads(case)))
        self.assertEqual([["15"]] * 39, case.sleeps())
        self.assertEqual([], case.aws("ecs list-tasks"), "nothing after the rollout is read once it fails")

    def rollout_reads(self, case) -> list[dict]:
        return [c for c in case.aws("ecs describe-services") if "rolloutState" in c["options"].get("query", "")]

    def failed(self, world: dict, fragment: str, stub_sleep: bool = False):
        case = cell.Case(self, outputs=True, world=world)
        if stub_sleep:
            case.stub_sleep()
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

    # #783: with the attestations on, the run's tag names an image index, which holds the linux/arm64
    # image and an attestation manifest for it. Whether ECS reports the index's digest for a running
    # container or the image's is what #788 observes on a cell, so the assertion takes either, both
    # read from the index in ECR, and nothing else.

    def test_a_running_task_that_reports_the_index_s_arm64_image_passes(self):
        world = seeded_world()
        world["faults"]["running_platform"] = True
        case = cell.Case(self, outputs=True, world=world)
        result = case.run(SCRIPT)
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        world = case.load()
        applied = next(c for c in case.calls("terraform") if c.get("command") == "apply")["vars"]["gateway_image"]
        index = world["manifests"][applied.rpartition("@")[2]]
        arm64 = [m["digest"] for m in index["manifests"] if m.get("platform") == {"os": "linux", "architecture": "arm64"}]
        self.assertEqual([arm64[0]], [task["gatewayDigest"] for task in world["tasks"]])
        self.assertNotEqual(applied.rpartition("@")[2], arm64[0])
        self.assertIn("pass:", result.stdout)

    def test_a_running_task_that_reports_the_attestation_manifest_fails(self):
        # The attestation manifest is in the index too, and is not an image a container runs.
        world = seeded_world()
        world["faults"]["running_attestation"] = True
        self.failed(world, "is not the applied gateway")

    def test_an_index_with_no_linux_arm64_image_fails(self):
        # Each half of the platform on its own: another architecture, and arm64 for another os.
        for platform in ({"os": "linux", "architecture": "amd64"}, {"os": "windows", "architecture": "arm64"}):
            with self.subTest(platform=platform):
                world = seeded_world()
                world["faults"]["index_platform"] = platform
                case = self.failed(world, "holds no linux/arm64 image")
                self.assertEqual([], case.aws("ecs list-tasks"), "the running tasks were read after the index read failed")

    def test_an_index_with_two_linux_arm64_images_fails(self):
        world = seeded_world()
        world["faults"]["index_twice"] = True
        self.failed(world, "holds 2 linux/arm64 images")

    def test_an_image_ecr_no_longer_holds_fails(self):
        # BatchGetImage answers a digest it does not hold under failures, not as an error:
        # https://docs.aws.amazon.com/AmazonECR/latest/APIReference/API_BatchGetImage.html
        world = seeded_world()
        world["faults"]["manifest_missing"] = True
        case = self.failed(world, "the registry holds no manifest")
        self.assertEqual([], case.aws("ecs list-tasks"), "the running tasks were read after the index read failed")

    def test_a_manifest_that_is_not_an_image_or_an_index_of_digests_fails(self):
        nameless = {"schemaVersion": 2, "mediaType": "application/vnd.oci.image.index.v1+json",
                    "manifests": [{"mediaType": "application/vnd.oci.image.manifest.v1+json",
                                   "platform": {"os": "linux", "architecture": "arm64"}}]}
        for answer, fragment in (("{not json", "the image's manifest is not JSON"),
                                 (json.dumps({"schemaVersion": 2, "mediaType": "application/vnd.oci.empty.v1+json"}),
                                  "neither an image manifest nor an image index"),
                                 (json.dumps(nameless), "names its linux/arm64 image by no digest")):
            with self.subTest(fragment=fragment):
                world = seeded_world()
                world["faults"]["manifest_text"] = answer
                self.failed(world, fragment)

    def test_the_service_never_stable(self):
        world = seeded_world()
        world["faults"]["wait"] = True
        self.failed(world, "did not become stable")

    def test_a_rollout_that_did_not_complete(self):
        world = seeded_world()
        world["faults"]["rollout"] = "FAILED"
        case = self.failed(world, "rollout is FAILED", stub_sleep=True)
        # FAILED on the first read is final: nothing is read again (#467).
        self.assertEqual(1, len(self.rollout_reads(case)))
        self.assertEqual([], case.sleeps())

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
        return result

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
        result = self.refused(case, "written from the cell's roots' outputs after an apply")
        self.assertNotIn("seed.sh", result.stderr)
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


class DeployTakesTheCellFromTheEntryValues(cell.CompanionTestCase):
    """#1348: the platform root, the roots, the field map and the outputs document are entry values
    with no default, each refused by name before anything is built. #1351: the wait's host is the
    live registry's, so the certificate's domain is not one deploy.sh reads."""

    READS = ("SPA_PLATFORM_ROOT", "SPA_ROOTS", "SPA_FIELD_MAP", "SPA_OUTPUTS_FILE")

    def test_each_entry_value_it_reads_unset_exits_2_naming_it_before_any_call(self):
        for name in self.READS:
            for value in (None, ""):
                with self.subTest(name=name, value=value):
                    case = cell.Case(self, outputs=True, world=seeded_world())
                    result = case.run(SCRIPT, env=case.env(**{name: value}))
                    self.assertEqual(2, result.status, result.output)
                    self.assertIn(f"{name} is not set", result.stderr)
                    self.assertEqual([], case.calls())

    def test_neither_the_domain_nor_the_app_host_is_read(self):
        case = cell.Case(self, outputs=True, world=seeded_world())
        result = case.run(SCRIPT, env=case.env(SPA_CERT_DOMAIN=None, SPA_APP_HOST=None))
        self.assertEqual(0, result.status, result.output)

    def test_help_reads_no_entry_value_and_names_no_profile(self):
        case = cell.Case(self)
        result = case.run(SCRIPT, "--help", env=case.env(**{name: None for name in cell.ENTRY_NAMES}))
        self.assertEqual(0, result.status, result.output)
        self.assertNotIn("the spa-platform profile", result.stdout)
        self.assertNotIn("tools/aws-project.env", result.stdout)
        self.assertNotIn("infra/envs/", result.stdout)
        for name in self.READS:
            self.assertIn(name, result.stdout)


class DeployWaitsThroughTheLiveRegistrysHost(cell.CompanionTestCase):
    """#1351: with no --url, the wait reads through an app's host from the live registry step 1
    read, without the port the registry carries, on the wait tunnel's own port. No app of the
    fixtures' is a default, so a registry whose apps are another cell's is read through its own."""

    def wait_url(self, apps: list[dict]):
        world = seeded_world()
        world["registry"]["apps"] = apps
        case = cell.Case(self, outputs=True, world=world)
        result = case.run(SCRIPT)
        self.assertNoUnexpectedCall(case)
        waits = [c["spactl"] for c in case.calls("dotnet") if c["spactl"][:1] == ["wait"]]
        return case, result, [wait[wait.index("--url") + 1] for wait in waits]

    def test_the_first_app_s_host_without_its_port_on_the_tunnel_s_port(self):
        for host in ("shell.cells.example.test:8443", "shell.cells.example.test"):
            with self.subTest(host=host):
                case, result, urls = self.wait_url([
                    {"appId": "shell", "host": host, "release": "r1"},
                    {"appId": "orders", "host": "orders.cells.example.test:8443", "release": "r1"}])
                self.assertEqual(0, result.status, result.output)
                port = case.aws("ssm start-session")[0]["port"]
                self.assertEqual([f"https://shell.cells.example.test:{port}"], urls)
                self.assertNotIn("app1", result.stdout)

    def test_a_registry_whose_first_app_names_no_host_fails_before_anything_is_built(self):
        for app in ({"appId": "shell", "release": "r1"}, {"appId": "shell", "host": "", "release": "r1"},
                    {"appId": "shell", "host": "https://shell.cells.example.test", "release": "r1"},
                    # The schema's pattern holds a 256-character name; its maxLength of 255 does not.
                    {"appId": "shell", "host": "a" * 256, "release": "r1"}):
            with self.subTest(app=app):
                case, result, urls = self.wait_url([app])
                self.assertEqual(1, result.status, result.output)
                self.assertIn("names no host", result.stderr)
                self.assertEqual([], urls)
                # Only the preflight's `docker buildx version`: no build and no login.
                self.assertEqual([["buildx", "version"]], [c["argv"] for c in case.calls("docker")])

    def test_with_url_the_registry_s_host_is_not_read(self):
        world = seeded_world()
        world["registry"]["apps"] = [{"appId": "shell", "release": "r1"}]
        with listening() as port:
            case = cell.Case(self, outputs=True, world=world)
            result = case.run(SCRIPT, "--url", f"https://gateway.cells.example.test:{port}")
        self.assertEqual(0, result.status, result.output)


if __name__ == "__main__":
    sys.exit(cell.main("deploy_tests"))
