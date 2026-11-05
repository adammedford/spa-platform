#!/usr/bin/env python3
"""Test companion for tools/bench/fargate.py (#305). Owner: the owner OWNERSHIP.md names for
tools/bench/.

The run itself needs the applied cell and about half an hour, and is #178's. What runs here is everything the
script decides without AWS: the order of its calls, which task it measures, which hop it takes,
which clocks it reads and how it labels them, when it stops and what it writes, and that nothing it
writes holds a secret.

**The real script, a constructed environment.** Each case copies fargate.py into a repository tree
of its own, with infra/envs/poc/outputs.json in contracts/infra-outputs.schema.json's shape, and runs
it there with python3 -I. PATH is the case's bin directory alone: the doubles `aws` and
`session-manager-plugin`, and git, the one real tool the script uses. The environment holds a Region,
a HOME of the case's own and no credential, so no case can reach AWS.

**The doubles are this ticket's own**, in tools/bench/fargate_doubles.py beside this file, in the
shape of tools/scripts/cell_doubles.py and importing nothing from it (Q3 on #305): workstream 1's companion
runs every companion that loads that file, and its doubles answer none of the calls made here. One
world per case in world.json: the service with its deployments and their ids, the tasks with their
startedBy, each restart's target-health samples in order, each task's log stream and each exec
session's answer. Every call is recorded in calls.jsonl in order, and a call shape the double does
not answer is recorded and exits 97, so a new call in the script turns a case red instead of being
answered. As the real CLI does, `ssm start-session` and `ecs execute-command` hand the session to
session-manager-plugin: for a port forward the plugin double listens on the local port with TLS
under a test authority and answers every request the OpenID Connect challenge, a 302 whose headers
carry a state and a cookie canary; for an exec it prints what the case's world says the session
printed.

**The world's names are read from the Terraform that makes them** (CODING_STANDARDS.md, "A guard
reads the thing it guards"): the cluster, the service, the gateway container and the log group are
the locals in infra/modules/cell/ecs.tf.

**Canaries.** The account id is in every ARN the doubles hand out (task, task definition, target
group, the stop reason's target group) and in the gateway image's repository URI; the 302 carries a
`state` and a cookie. No output may hold any of them, nor the repository URI with its account
label redacted, nor "arn:aws".

Run: python3 -I tools/bench/fargate_tests.py
"""

from __future__ import annotations

import http.server
import importlib.util
import json
import pathlib
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent
SCRIPT = ROOT / "tools" / "bench" / "fargate.py"
OUTPUTS = pathlib.Path("infra") / "envs" / "poc" / "outputs.json"
ALB_TF = ROOT / "infra" / "modules" / "cell" / "alb.tf"


def deregistration_delay() -> str:
    """The target group's drain, read from the Terraform that sets it (CODING_STANDARDS.md, "A guard
    reads the thing it guards"), so the line the script prints turns red when alb.tf changes."""
    match = re.search(r"^\s*deregistration_delay\s*=\s*(\d+)\s*$", ALB_TF.read_text(), re.MULTILINE)
    if match is None:
        raise AssertionError("alb.tf sets no deregistration_delay")
    return match[1]

spec = importlib.util.spec_from_file_location("fargate", SCRIPT)
fargate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fargate)
DOUBLES = ROOT / "tools" / "bench" / "fargate_doubles.py"
spec = importlib.util.spec_from_file_location("fargate_doubles", DOUBLES)
doubles = importlib.util.module_from_spec(spec)
spec.loader.exec_module(doubles)
ACCOUNT, REGION, CLUSTER, SERVICE, GATEWAY = doubles.ACCOUNT, doubles.REGION, doubles.CLUSTER, doubles.SERVICE, doubles.GATEWAY
LOG_GROUP, DIGEST, IMAGE, TG_ARN, ALB = doubles.LOG_GROUP, doubles.DIGEST, doubles.IMAGE, doubles.TG_ARN, doubles.ALB
SECRETS, APP_HOST, REPOSITORY_TAIL = doubles.SECRETS, doubles.APP_HOST, doubles.REPOSITORY_TAIL
plan, world, task_id, save = doubles.plan, doubles.world, doubles.task_id, doubles.save


# --- the cases ------------------------------------------------------------------------------------

AUTHORITY = None


def setUpModule():
    """A test authority and a wildcard leaf for *.spa-poc.test, as certs.sh issues the real ones."""
    global AUTHORITY
    openssl = shutil.which("openssl")
    if openssl is None:
        raise AssertionError("openssl is required: the plugin double serves TLS under a test authority")
    AUTHORITY = tempfile.mkdtemp(prefix="fargate-ca-")
    work = pathlib.Path(AUTHORITY)

    def run(*args):
        subprocess.run([openssl, *args], cwd=work, check=True, capture_output=True)

    (work / "leaf.cnf").write_text("[req]\ndistinguished_name=dn\n[dn]\n[ext]\nsubjectAltName=DNS:*.spa-poc.test\n"
                                   "basicConstraints=CA:FALSE\nkeyUsage=digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\n")
    (work / "ca.cnf").write_text("[req]\ndistinguished_name=dn\n[dn]\n[ext]\nbasicConstraints=critical,CA:TRUE\n"
                                 "keyUsage=critical,keyCertSign,cRLSign\nsubjectKeyIdentifier=hash\n")
    run("req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", "ca.key", "-out", "ca.pem", "-days", "2",
        "-subj", "/CN=Fargate Test CA", "-config", "ca.cnf", "-extensions", "ext")
    run("req", "-newkey", "rsa:2048", "-nodes", "-keyout", "leaf.key", "-out", "leaf.csr", "-subj", "/CN=*.spa-poc.test",
        "-config", "leaf.cnf")
    run("x509", "-req", "-in", "leaf.csr", "-CA", "ca.pem", "-CAkey", "ca.key", "-CAcreateserial", "-out", "leaf.pem",
        "-days", "2", "-extfile", "leaf.cnf", "-extensions", "ext")
    # A second authority that issued nothing the double serves: the probe must refuse the forward under it.
    run("req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", "other.key", "-out", "other.pem", "-days", "2",
        "-subj", "/CN=Fargate Other CA", "-config", "ca.cnf", "-extensions", "ext")


def tearDownModule():
    if AUTHORITY:
        shutil.rmtree(AUTHORITY, ignore_errors=True)


def free_port() -> int:
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


class Run:
    def __init__(self, case: pathlib.Path, done: subprocess.CompletedProcess):
        self.case = case
        self.code = done.returncode
        self.stdout = done.stdout
        self.stderr = done.stderr
        self.txt_path = case / "out.txt"
        self.json_path = case / "out.json"
        self.txt = self.txt_path.read_text() if self.txt_path.exists() else None
        self.json = json.loads(self.json_path.read_text()) if self.json_path.exists() else None
        lines = (case / "calls.jsonl").read_text().splitlines() if (case / "calls.jsonl").exists() else []
        self.calls = [json.loads(line) for line in lines]
        probes = (case / "probes.jsonl").read_text().splitlines() if (case / "probes.jsonl").exists() else []
        self.probes = [json.loads(line) for line in probes]

    def aws(self, *words) -> list:
        """The aws calls whose words, after --region, start with these."""
        return [call["argv"] for call in self.calls if call["tool"] == "aws" and call["argv"][2:2 + len(words)] == list(words)]

    def shapes(self) -> list:
        return [" ".join(word for word in call["argv"][2:4]) for call in self.calls if call["tool"] == "aws"]


def run_case(document: dict, outputs: dict = None, region: str = REGION, extra=(), env_changes=None, ca: str = "ca.pem") -> Run:
    case = pathlib.Path(tempfile.mkdtemp(prefix="fargate-case-"))
    repo = case / "repo"
    (repo / "tools" / "bench").mkdir(parents=True)
    shutil.copy(SCRIPT, repo / "tools" / "bench" / "fargate.py")
    if outputs is not False:
        (repo / OUTPUTS).parent.mkdir(parents=True)
        written = {"region": REGION, "cell": "poc", "bucket": f"spa-platform-poc-{ACCOUNT}-{REGION}-an", "albDnsName": ALB,
                   "ecsCluster": CLUSTER, "ecsService": SERVICE, "ecrGateway": IMAGE.split("@")[0],
                   "ecrMockApi": f"{ACCOUNT}.dkr.ecr.{REGION}.amazonaws.com/spa-poc/mock-api", "gatewayImage": IMAGE,
                   "issuer": f"https://cognito-idp.{REGION}.amazonaws.com/{REGION}_EXAMPLE",
                   "cognitoDomain": f"spa-platform-poc-{ACCOUNT}.auth.{REGION}.amazoncognito.com",
                   "clients": {}, "testUsers": {}, "logGroup": LOG_GROUP}
        written.update(outputs or {})
        (repo / OUTPUTS).write_text(json.dumps(written))
    for name in ("leaf.pem", "leaf.key", "ca.pem", "other.pem"):
        shutil.copy(pathlib.Path(AUTHORITY) / name, case / name)
    bin_dir = case / "bin"
    bin_dir.mkdir()
    for name in ("aws", "session-manager-plugin"):
        wrapper = bin_dir / name
        wrapper.write_text(f"#!/bin/sh\nexec '{sys.executable}' -I '{DOUBLES}' {name} '{case}' \"$@\"\n")
        wrapper.chmod(0o755)
    git = shutil.which("git")
    if git:
        (bin_dir / "git").symlink_to(git)
    (case / "home").mkdir()
    save(case, document)
    env = {"PATH": str(bin_dir), "HOME": str(case / "home"), "AWS_REGION": region}
    if region is None:
        env.pop("AWS_REGION")
    env.update(env_changes or {})
    argv = [sys.executable, "-I", str(repo / "tools" / "bench" / "fargate.py"), "--txt", str(case / "out.txt"),
            "--json", str(case / "out.json"), "--ca", str(case / ca), "--local-port", str(free_port()),
            "--poll-seconds", "0", "--probe-interval", "0", "--task-timeout", "20", "--health-timeout", "20",
            "--log-timeout", "1", "--probe-attempts", "5", "--forward-timeout", "20", *extra]
    done = subprocess.run(argv, env=env, capture_output=True, text=True, timeout=600)
    return Run(case, done)


class CaseBase(unittest.TestCase):
    def assertNoSecrets(self, run: Run):
        for name, text in (("txt", run.txt), ("json", run.json_path.read_text() if run.json_path.exists() else None),
                           ("stderr", run.stderr), ("stdout", run.stdout)):
            if text is None:
                continue
            for secret in SECRETS:
                self.assertNotIn(secret, text, f"{secret!r} reached the {name}")

    def assertNoUnexpectedCalls(self, run: Run):
        self.assertEqual([], [call for call in run.calls if "unexpected" in call])


class TenCleanRestarts(CaseBase):
    """One clean run, read by every test here."""

    @classmethod
    def setUpClass(cls):
        plans = [plan() for _ in range(10)]
        plans[1] = plan(probe_lands=["old", "old", "new"])
        cls.result = run_case(world(plans))

    def test_exits_0_and_writes_both_files_complete(self):
        self.assertEqual(0, self.result.code, self.result.stderr)
        self.assertNoUnexpectedCalls(self.result)
        self.assertTrue(self.result.json["complete"])
        self.assertEqual("clean", self.result.json["ending"])
        self.assertEqual(10, len(self.result.json["restarts"]))
        self.assertIn("ending: 10 clean restarts", self.result.txt)

    def test_ten_restarts_in_sequence_each_waiting_for_a_stable_service_with_its_drain(self):
        # Each update-service follows the previous restart's services-stable wait, and the service
        # is described as settled (one deployment, COMPLETED) before the next.
        order = [shape for shape in self.result.shapes() if shape in ("ecs update-service", "ecs wait")]
        self.assertEqual(["ecs wait"] + ["ecs update-service", "ecs wait"] * 10, order)
        updates = self.result.aws("ecs", "update-service")
        self.assertEqual(10, len(updates))
        for argv in updates:
            # The running revision, forced; no other setting changed (#286 Q135).
            self.assertEqual(["ecs", "update-service", "--cluster", CLUSTER, "--service", SERVICE, "--force-new-deployment",
                              "--output", "json"], argv[2:])

    def test_prints_the_expected_length_before_restart_1(self):
        first_update = self.result.stdout.index("restart 1/10")
        self.assertIn("ten restarts, about half an hour", self.result.stdout[:first_update])
        self.assertIn(f"drains for {deregistration_delay()} s and stops", self.result.stdout[:first_update])

    def test_the_new_task_is_the_primary_deployments_while_two_deployments_overlap(self):
        # The world lists a decoy task of another deployment first, and the old task beside the new
        # one; the new task appears only on the second poll.
        for index, restart in enumerate(self.result.json["restarts"]):
            self.assertEqual(f"ecs-svc/{2000000000000000000 + index}", restart["deployment"])
            self.assertEqual(task_id(100 + 2 * index), restart["task_id"])
        streams = {argv[argv.index("--log-stream-name") + 1] for argv in self.result.aws("logs", "get-log-events")}
        self.assertEqual({f"{GATEWAY}/{GATEWAY}/{task_id(100 + 2 * i)}" for i in range(10)}, streams)

    def test_the_three_log_clocks_carry_their_labels_and_the_premise(self):
        for label in ("listening (log)", "registry live (log)", "first passing check (log)"):
            self.assertIn(label, self.result.txt)
        self.assertIn("premise: nothing on the cell calls /healthz during the run", self.result.txt)
        table = self.result.txt.split("\n\n")[1]
        for row in table.splitlines():
            self.assertNotIn("ready", row.lower())
        # From the metadata StartedAt: the world logs listening at 900 ms after it, registry live at
        # 1500 and the first 200 health line at 3000 (after a 503 at 2000).
        self.assertRegex(self.result.txt, r"listening \(log\)\s+900\s+900\n")
        self.assertRegex(self.result.txt, r"registry live \(log\)\s+1500\s+1500\n")
        self.assertRegex(self.result.txt, r"first passing check \(log\)\s+3000\s+3000\n")
        # Each log line keeps its own ts and CloudWatch's event timestamp beside it.
        line = self.result.json["restarts"][0]["marks"]["listening"]
        self.assertEqual(line["ts_ns"] // 1_000_000 + 200, line["event_timestamp_ms"])
        self.assertRegex(line["ts"], r"\.\d{7}\+00:00$")

    def test_the_zero_is_the_metadata_started_at_with_its_clock_drift(self):
        for restart in self.result.json["restarts"]:
            self.assertEqual("metadata StartedAt", restart["zero"]["source"])
            self.assertEqual(0.4, restart["zero"]["clock_drift"]["ClockErrorBound"])
        self.assertIn("ClockDrift bound 0.4 ms, SYNCHRONIZED", self.result.txt)
        # The world's task started 500 ms after its container and pulled until 1500 ms before it.
        self.assertRegex(self.result.txt, r"task startedAt from zero \(cross-clock\)\s+500\s+500\n")
        self.assertRegex(self.result.txt, r"pullStoppedAt from zero \(cross-clock\)\s+-1500\s+-1500\n")
        self.assertRegex(self.result.txt, r"pull-and-place \(createdAt to startedAt, control plane\)\s+22000\s+22000\n")
        self.assertRegex(self.result.txt, r"of which pull \(pullStartedAt to pullStoppedAt\)\s+15000\s+15000\n")

    def test_the_exec_session_runs_the_one_liner_on_the_new_task_once_stable(self):
        execs = self.result.aws("ecs", "execute-command")
        self.assertEqual(10, len(execs))
        for index, argv in enumerate(execs):
            self.assertEqual(task_id(100 + 2 * index), argv[argv.index("--task") + 1])
            self.assertEqual(fargate.EXEC_COMMAND, argv[argv.index("--command") + 1])
            self.assertIn("--interactive", argv)
        shapes = self.result.shapes()
        for position, shape in enumerate(shapes):
            if shape == "ecs execute-command":
                self.assertEqual("ecs wait", next(s for s in reversed(shapes[:position]) if s in ("ecs wait", "ecs update-service")))

    def test_t0_and_target_healthy_are_poll_windows_with_their_reason_codes(self):
        restart = self.result.json["restarts"][0]
        reasons = [sample["reason"] for sample in restart["samples"]]
        self.assertEqual([None, "Elb.RegistrationInProgress", "Elb.InitialHealthChecking", None], reasons)
        self.assertEqual([None, "initial", "initial", "healthy"], [sample["state"] for sample in restart["samples"]])
        self.assertEqual(restart["samples"][2]["at_ns"], restart["t0"]["at_ns"])
        self.assertEqual(restart["samples"][1]["at_ns"], restart["t0"]["after_ns"])
        self.assertEqual(restart["samples"][3]["at_ns"], restart["healthy"]["at_ns"])
        self.assertEqual(1, restart["k"])
        # Two health lines up to the first 200 (a 503, then the 200), at most two per check.
        self.assertIn("log consistent: 2 health line(s) to the first 200", self.result.txt)
        self.assertIn("S - T0", self.result.txt)
        self.assertIn("cross-clock", self.result.txt)

    def test_each_hop_is_its_own_port_forward_through_the_outgoing_task(self):
        sessions = self.result.aws("ssm", "start-session")
        self.assertEqual(10, len(sessions))
        hops = [task_id(1)] + [task_id(100 + 2 * i) for i in range(9)]
        for argv, hop in zip(sessions, hops):
            self.assertEqual(f"ecs:{CLUSTER}_{hop}_{hop}-2222", argv[argv.index("--target") + 1])
            parameters = json.loads(argv[argv.index("--parameters") + 1])
            self.assertEqual([ALB], parameters["host"])
            self.assertEqual(["443"], parameters["portNumber"])
        # Each forward opens before its restart's update-service.
        shapes = self.result.shapes()
        self.assertEqual(["ssm start-session", "ecs update-service"] * 10,
                         [s for s in shapes if s in ("ssm start-session", "ecs update-service")])

    def test_the_302_probe_is_a_cookieless_navigation_retried_until_the_new_stream_holds_a_302(self):
        # Restart 2's first two probes land on the old task; the third reaches the new one.
        by_hop = {}
        for probe in self.result.probes:
            by_hop.setdefault(probe["hop"], []).append(probe)
        second = by_hop[100]
        self.assertEqual([100, 100, 102], [probe["task"] for probe in second])
        self.assertEqual(3, len(self.result.json["restarts"][1]["probes"]))
        for probe in self.result.probes:
            self.assertEqual("GET / HTTP/1.1", probe["request"])
            self.assertEqual(APP_HOST, probe["headers"]["Host"])
            self.assertEqual("navigate", probe["headers"]["Sec-Fetch-Mode"])
            self.assertEqual("document", probe["headers"]["Sec-Fetch-Dest"])
            self.assertNotIn("Cookie", probe["headers"])
        # The clock is the new task's 302 line's duration_ms.
        self.assertRegex(self.result.txt, r"302 duration_ms \(log\)\s+640\s+640\n")
        self.assertIn("302 mark from zero (bounded by target healthy)", self.result.txt)

    def test_the_facts_name_no_account(self):
        facts = self.result.txt.splitlines()[1]
        self.assertIn(f"image {DIGEST}", facts)
        self.assertIn("task definition spa-platform-poc:7", facts)
        self.assertIn("platform version 1.4.0", facts)
        self.assertIn(f"Region {REGION}", facts)
        self.assertIn("clocks: task", self.result.txt)
        # The facts, on the .txt's line and in the .json, name the image by digest alone: no
        # account id, raw or redacted, and no repository URI, raw, account-redacted or replaced.
        for where, text in (("the facts line", facts), ("the .json's facts", json.dumps(self.result.json["facts"]))):
            for absent in (ACCOUNT, "<account id>", REPOSITORY_TAIL, "<repository>", "arn:aws", "<arn>"):
                self.assertNotIn(absent, text, f"{absent!r} is in {where}")
            self.assertNotRegex(text, r"(?<![0-9])[0-9]{12}(?![0-9])", f"a twelve-digit run is in {where}")

    def test_nothing_written_holds_a_secret(self):
        self.assertNoSecrets(self.result)

    def test_no_call_ran_with_a_credential_in_its_environment(self):
        self.assertTrue(self.result.calls)
        self.assertEqual([], [call for call in self.result.calls if call["credential_env"]])


class Endings(CaseBase):
    def test_a_breach_stops_the_run_writes_the_restarts_so_far_and_exits_3(self):
        plans = [plan() for _ in range(10)]
        plans[1] = plan(health=[None, ["initial", "Elb.InitialHealthChecking"], ["unhealthy", "Target.ResponseCodeMismatch"]],
                        stop_at_sample=3)
        run = run_case(world(plans))
        self.assertEqual(3, run.code, run.stderr)
        self.assertNoUnexpectedCalls(run)
        # No further restart, and nothing done to the service after the stop.
        self.assertEqual(2, len(run.aws("ecs", "update-service")))
        last_update = max(i for i, s in enumerate(run.shapes()) if s == "ecs update-service")
        self.assertNotIn("ecs wait", run.shapes()[last_update:])
        self.assertEqual("breach", run.json["ending"])
        self.assertFalse(run.json["complete"])
        restarts = run.json["restarts"]
        self.assertEqual([True, False], [r["complete"] for r in restarts])
        self.assertEqual("ServiceSchedulerInitiated", restarts[1]["stop"]["code"])
        self.assertIn("<arn>", restarts[1]["stop"]["reason"])
        self.assertTrue(restarts[1]["lines"], "the breach row keeps the task's log lines")
        # The breach is the scheduler's stop (#286 Q148), placed against the hard bound as observed on
        # T0's clock, never asserted past it (#919): the doubles stop the task milliseconds after T0.
        self.assertRegex(run.txt, r"ending: BREACH on restart 2: the scheduler stopped task \w+ for failing the load "
                                  r"balancer's health checks \(ServiceSchedulerInitiated\), seen at T0 \+ -?\d+\.\.-?\d+ s, "
                                  r"before the T0 \+ 60 s hard bound\n")
        self.assertNotIn("past the T0", run.txt)
        seen = restarts[1]["stop"]["seen"]
        # From the describe-tasks before the one that saw the stop, which came after the T0 sample.
        self.assertGreater(seen["after_ns"], restarts[1]["t0"]["at_ns"])
        self.assertLess(seen["after_ns"], seen["at_ns"])
        self.assertRegex(run.txt, r"#2 task \w+ breach: .*S - T0 -?\d+\.\.-?\d+ cross-clock.*; stopped "
                                  r"ServiceSchedulerInitiated: [^\n]*; seen at T0 \+ -?\d+\.\.-?\d+ s, before the T0 \+ 60 s hard bound")
        self.assertNoSecrets(run)

    def test_a_breach_before_any_t0_sample_is_reported_unplaced_and_exits_3(self):
        plans = [plan() for _ in range(10)]
        plans[0] = plan(health=[None, ["initial", "Elb.RegistrationInProgress"]], stop_at_sample=2)
        run = run_case(world(plans))
        self.assertEqual(3, run.code, run.stderr)
        self.assertNoUnexpectedCalls(run)
        self.assertEqual("breach", run.json["ending"])
        self.assertIsNone(run.json["restarts"][0]["t0"])
        self.assertIn("ending: BREACH on restart 1: the scheduler stopped task ", run.txt)
        self.assertIn("(ServiceSchedulerInitiated), with no T0 (no sample saw Elb.InitialHealthChecking), "
                      "not placed against the T0 + 60 s hard bound\n", run.txt)
        self.assertNotIn("past the T0", run.txt)
        self.assertNoSecrets(run)

    def test_a_deployment_not_stable_within_the_waiters_bound_is_incomplete_and_exits_4(self):
        plans = [plan() for _ in range(10)]
        plans[2] = plan(stable=False)
        run = run_case(world(plans))
        self.assertEqual(4, run.code, run.stderr)
        self.assertEqual(3, len(run.aws("ecs", "update-service")))
        self.assertEqual("incomplete", run.json["ending"])
        self.assertIn("services-stable waiter's bound", run.json["reason"])
        self.assertEqual([True, True, False], [r["complete"] for r in run.json["restarts"]])
        self.assertIn("ending: INCOMPLETE after 2 complete restart(s)", run.txt)
        self.assertNoSecrets(run)

    def test_an_expected_log_line_absent_after_the_bounded_wait_is_incomplete(self):
        plans = [plan() for _ in range(10)]
        plans[0] = plan(log=[["Now listening on: http://[::]:8080", 900], ["health", 200, 3000]])
        run = run_case(world(plans))
        self.assertEqual(4, run.code, run.stderr)
        self.assertIn("has no registry_live line", run.json["reason"])
        self.assertEqual(1, len(run.aws("ecs", "update-service")))

    def test_a_probe_that_never_reaches_the_new_task_is_incomplete(self):
        plans = [plan(probe_lands=["old"]) for _ in range(10)]
        run = run_case(world(plans))
        self.assertEqual(4, run.code, run.stderr)
        self.assertIn("no 302 reached task", run.json["reason"])
        self.assertEqual(5, len(run.probes))
        self.assertNoSecrets(run)

    def test_a_forward_whose_certificate_the_authority_did_not_issue_is_never_probed_through(self):
        # TLS is verified against --ca (Q10): under an authority that issued nothing the forward
        # serves, no request reaches it, so no 302 lands and the run is incomplete.
        run = run_case(world(), ca="other.pem")
        self.assertEqual(4, run.code, run.stderr)
        self.assertIn("no 302 reached task", run.json["reason"])
        self.assertEqual([], run.probes)
        self.assertNoSecrets(run)

    def test_a_refusal_after_restart_1s_update_service_still_writes_the_restart_and_exits_4(self):
        # A restart was issued, so this is no refusal before restart 1 (Q8): the service vanishing
        # after it is incomplete, and restart 1's marks are kept.
        run = run_case(world(service_gone_after=1))
        self.assertEqual(4, run.code, run.stderr)
        self.assertEqual(1, len(run.aws("ecs", "update-service")))
        self.assertEqual("incomplete", run.json["ending"])
        self.assertIn(f"has no service {SERVICE}", run.json["reason"])
        self.assertEqual([False], [r["complete"] for r in run.json["restarts"]])
        self.assertNoSecrets(run)

    def test_any_other_failure_after_restart_1_began_writes_the_partial_files_and_exits_4(self):
        plans = [plan() for _ in range(10)]
        plans[1] = plan(bad_created_at=True)
        run = run_case(world(plans))
        self.assertEqual(4, run.code, run.stderr)
        self.assertEqual("incomplete", run.json["ending"])
        self.assertEqual("an unexpected ValueError", run.json["reason"])
        self.assertEqual([True, False], [r["complete"] for r in run.json["restarts"]])
        self.assertNotIn("Traceback", run.stderr)
        self.assertNoSecrets(run)

    def test_any_other_failure_once_restart_1s_update_service_is_out_writes_the_restart_and_exits_4(self):
        # The new task's createdAt is read after update-service was sent: the service may have
        # changed, so this is no failure before restart 1 (Endings: 4), and restart 1 is kept.
        plans = [plan() for _ in range(10)]
        plans[0] = plan(bad_created_at=True)
        run = run_case(world(plans))
        self.assertEqual(4, run.code, run.stderr)
        self.assertEqual(1, len(run.aws("ecs", "update-service")))
        self.assertEqual("incomplete", run.json["ending"])
        self.assertEqual("an unexpected ValueError", run.json["reason"])
        self.assertEqual([False], [r["complete"] for r in run.json["restarts"]])
        self.assertIsNotNone(run.txt)
        self.assertNotIn("Traceback", run.stderr)
        self.assertNoSecrets(run)

    def test_a_failed_update_service_on_restart_1_writes_the_restart_and_exits_4(self):
        # The call was sent, so the service may have changed (Endings: 4): the same predicate as
        # any other failure past that point, not the exit 2 of a failure before it.
        run = run_case(world(update_service_fail_at=1))
        self.assertEqual(4, run.code, run.stderr)
        self.assertEqual(1, len(run.aws("ecs", "update-service")))
        self.assertEqual("incomplete", run.json["ending"])
        self.assertEqual("an AWS call failed (ThrottlingException)", run.json["reason"])
        self.assertEqual([False], [r["complete"] for r in run.json["restarts"]])
        self.assertIsNotNone(run.txt)
        self.assertNotIn("Traceback", run.stderr)
        self.assertNoSecrets(run)

    def test_an_aws_failure_on_restart_1_before_its_update_service_exits_2_and_writes_nothing(self):
        # Preflight's list-tasks is the first; restart 1's, before its update-service, the second.
        run = run_case(world(list_tasks_fail_at_poll=2))
        self.assertEqual(2, run.code, run.stderr)
        self.assertIsNone(run.txt)
        self.assertIsNone(run.json)
        self.assertEqual([], run.aws("ecs", "update-service"))
        self.assertEqual(2, len(run.aws("ecs", "list-tasks")))
        self.assertIn("an AWS call failed before restart 1 (ThrottlingException)", run.stderr)
        self.assertNoSecrets(run)

    def test_any_other_failure_on_restart_1_before_its_update_service_exits_2_and_writes_nothing(self):
        # Preflight's list-tasks is the first; restart 1's, before its update-service, the second.
        run = run_case(world(list_tasks_garbage_at_poll=2))
        self.assertEqual(2, run.code, run.stderr)
        self.assertIsNone(run.txt)
        self.assertIsNone(run.json)
        self.assertEqual([], run.aws("ecs", "update-service"))
        self.assertEqual(2, len(run.aws("ecs", "list-tasks")))
        self.assertIn("an unexpected JSONDecodeError before restart 1.", run.stderr)
        self.assertNotIn("Traceback", run.stderr)
        self.assertNoSecrets(run)

    def test_any_other_failure_in_preflight_exits_2_and_writes_nothing(self):
        run = run_case(world(list_tasks_garbage_at_poll=1))
        self.assertEqual(2, run.code, run.stderr)
        self.assertIsNone(run.txt)
        self.assertIsNone(run.json)
        self.assertEqual([], run.aws("ecs", "update-service"))
        self.assertEqual(1, len(run.aws("ecs", "list-tasks")))
        self.assertIn("an unexpected JSONDecodeError before restart 1.", run.stderr)
        self.assertNotIn("Traceback", run.stderr)
        self.assertNoSecrets(run)

    def test_refusals_before_restart_1_exit_2_and_write_nothing(self):
        cases = {
            "no Region": dict(region=None),
            "no outputs document": dict(outputs=False),
            "an outputs document for another Region": dict(outputs={"region": "eu-west-1"}),
            "no credentials": dict(document=world(credentials=False)),
            "not on gatewayImage": dict(document=world(td_image=IMAGE.replace("a1" * 32, "ff" * 32))),
            "not stable": dict(document=world(initially_stable=False)),
            "a usage error": dict(extra=("--no-such-option",)),
        }
        for name, arguments in cases.items():
            with self.subTest(name):
                document = arguments.pop("document", world())
                run = run_case(document, **arguments)
                self.assertEqual(2, run.code, run.stderr)
                self.assertIsNone(run.txt)
                self.assertIsNone(run.json)
                self.assertEqual([], run.aws("ecs", "update-service"))
                self.assertNoSecrets(run)


class TheZero(CaseBase):
    def test_every_failure_of_the_exec_read_falls_back_to_pull_stopped_at_with_its_reason(self):
        plans = [plan(exec="no-session"), plan(exec="no-endpoint"), plan(exec="no-parse")] + [plan() for _ in range(7)]
        run = run_case(world(plans))
        self.assertEqual(0, run.code, run.stderr)
        zeros = [restart["zero"] for restart in run.json["restarts"][:4]]
        self.assertEqual(["pullStoppedAt"] * 3 + ["metadata StartedAt"], [zero["source"] for zero in zeros])
        self.assertIn("no session", zeros[0]["fallback"])
        self.assertIn("TargetNotConnectedException", zeros[0]["fallback"])
        self.assertIn("no endpoint variable", zeros[1]["fallback"])
        self.assertIn("no parse", zeros[2]["fallback"])
        self.assertIn("#1 pullStoppedAt (fallback: no session", run.txt)
        self.assertIn("#2 pullStoppedAt (fallback: no endpoint variable", run.txt)
        self.assertIn("#3 pullStoppedAt (fallback: no parse", run.txt)
        # The fallback zero is pullStoppedAt itself, so listening is 900 + 1500 ms after it.
        self.assertEqual(2400.0, fargate.clocks(run.json["restarts"][1])["listening"])
        self.assertNoSecrets(run)


class TheOneLiner(unittest.TestCase):
    """The exec command itself, run by the real bash against a metadata endpoint stand-in."""

    def serve(self, documents):
        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_GET(self):
                body = documents.get(self.path)
                self.send_response(200 if body is not None else 404)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(json.dumps(body or {}).encode())

        server = http.server.HTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        return server.server_address[1]

    def bash(self, env):
        done = subprocess.run(["/bin/bash", "-c", fargate.ONE_LINER], env=env, capture_output=True, text=True, timeout=30)
        self.assertEqual(0, done.returncode, done.stderr)
        return done.stdout

    def test_reads_started_at_and_clock_drift_between_the_markers(self):
        port = self.serve({"/v4/abc": {"Name": GATEWAY, "StartedAt": "2026-10-21T04:41:06.349093412Z"},
                           "/v4/abc/task": {"ClockDrift": {"ClockErrorBound": 0.5, "ClockSynchronizationStatus": "SYNCHRONIZED"}}})
        out = self.bash({"PATH": "/usr/bin:/bin", "ECS_CONTAINER_METADATA_URI_V4": f"http://127.0.0.1:{port}/v4/abc"})
        found, reason = fargate.parse_metadata("Starting session with SessionId: x\r\n" + out.replace("\n", "\r\n"))
        self.assertEqual("", reason)
        self.assertEqual(1792557666_349093412, found["started_ns"])
        self.assertEqual(0.5, found["clock_drift"]["ClockErrorBound"])

    def test_without_the_variable_it_says_so(self):
        out = self.bash({"PATH": "/usr/bin:/bin"})
        found, reason = fargate.parse_metadata(out)
        self.assertIsNone(found)
        self.assertTrue(reason.startswith("no endpoint variable"))

    def test_a_refused_endpoint_is_no_parse(self):
        port = free_port()
        out = subprocess.run(["/bin/bash", "-c", fargate.ONE_LINER], capture_output=True, text=True, timeout=30,
                             env={"PATH": "/usr/bin:/bin", "ECS_CONTAINER_METADATA_URI_V4": f"http://127.0.0.1:{port}/v4/abc"}).stdout
        found, reason = fargate.parse_metadata(out)
        self.assertIsNone(found)
        self.assertTrue(reason.startswith("no parse"))

    def test_holds_no_single_quote_so_it_survives_bash_c_quoting(self):
        self.assertNotIn("'", fargate.ONE_LINER)


class Arithmetic(unittest.TestCase):
    S = 1_000_000_000

    def window(self, after_s, at_s):
        return {"after_ns": int(after_s * self.S), "at_ns": int(at_s * self.S)}

    def test_k_counts_checks_after_t0_at_the_30_s_interval(self):
        t0 = self.window(99, 100)
        self.assertEqual(1, fargate.check_count(t0, self.window(100, 101)))
        self.assertEqual(2, fargate.check_count(t0, self.window(129, 130)))
        self.assertEqual(3, fargate.check_count(t0, self.window(159.5, 160.5)))
        self.assertIsNone(fargate.check_count(None, self.window(1, 2)))

    def test_a_stop_is_placed_against_the_hard_bound_on_t0s_clock(self):
        t0 = self.window(99, 100)
        self.assertEqual("seen at T0 + 61..63 s, past the T0 + 60 s hard bound",
                         fargate.stop_after_t0(t0, self.window(161, 162)))
        self.assertEqual("seen at T0 + 30..32 s, before the T0 + 60 s hard bound",
                         fargate.stop_after_t0(t0, self.window(130, 131)))
        self.assertEqual("seen at T0 + 59..62 s, across the T0 + 60 s hard bound: the poll cannot place it",
                         fargate.stop_after_t0(t0, self.window(159, 161)))
        # A stop on the first describe has no describe before it: the window is its one instant.
        self.assertEqual("seen at T0 + 70..71 s, past the T0 + 60 s hard bound",
                         fargate.stop_after_t0(t0, {"after_ns": None, "at_ns": 170 * self.S}))
        # A T0 the first sample saw may have begun any time before it, so the stop is never "before".
        first = {"after_ns": None, "at_ns": 100 * self.S}
        self.assertEqual("seen at T0 + 30..? s, across the T0 + 60 s hard bound: the poll cannot place it",
                         fargate.stop_after_t0(first, self.window(130, 131)))
        self.assertEqual("seen at T0 + 61..? s, past the T0 + 60 s hard bound",
                         fargate.stop_after_t0(first, self.window(161, 162)))
        self.assertEqual("with no T0 (no sample saw Elb.InitialHealthChecking), not placed against the T0 + 60 s hard bound",
                         fargate.stop_after_t0(None, self.window(1, 2)))

    def test_the_log_cross_check_allows_one_line_per_node_per_check(self):
        self.assertTrue(fargate.log_check(2, 4).startswith("consistent"))
        self.assertTrue(fargate.log_check(1, 3).startswith("INCONSISTENT"))
        self.assertTrue(fargate.log_check(1, 0).startswith("INCONSISTENT"))

    def test_windows_take_the_sample_before_and_the_sample_that_saw_it(self):
        samples = [{"at_ns": 1, "state": None, "reason": None},
                   {"at_ns": 2, "state": "initial", "reason": "Elb.RegistrationInProgress"},
                   {"at_ns": 3, "state": "initial", "reason": "Elb.InitialHealthChecking"},
                   {"at_ns": 4, "state": "initial", "reason": "Elb.InitialHealthChecking"},
                   {"at_ns": 5, "state": "healthy", "reason": None}]
        self.assertEqual({"t0": {"after_ns": 2, "at_ns": 3}, "healthy": {"after_ns": 4, "at_ns": 5}}, fargate.windows(samples))

    def test_reads_the_three_timestamp_forms(self):
        self.assertEqual(1792557666_349093412, fargate.time_ns("2026-10-21T04:41:06.349093412Z"))
        self.assertEqual(1792557666_349093000, fargate.time_ns("2026-10-21T04:41:06.349093+00:00"))
        self.assertEqual(1792557666_349093400, fargate.time_ns("2026-10-21T04:41:06.3490934+00:00"))
        self.assertEqual(1792557666_000000000, fargate.time_ns("2026-10-21T06:41:06+02:00"))

    def test_redact_removes_arns_and_account_ids(self):
        text = fargate.redact(f"failed in (target-group {TG_ARN}) for {ACCOUNT}", ACCOUNT)
        self.assertNotIn(ACCOUNT, text)
        self.assertNotIn("arn:aws", text)
        # A deployment id's nineteen digits are not an account id.
        self.assertEqual("ecs-svc/2000000000000000001", fargate.redact("ecs-svc/2000000000000000001"))

    def test_redact_masks_any_twelve_digit_run_without_the_known_account(self):
        # The fallback: with no account known (main()'s first err() calls) or another account's id
        # in the text, every twelve-digit run standing alone still becomes <account id>. One that
        # touches a letter or a digit is kept (test_redact_keeps_a_twelve_digit_run_inside_a_hex_id).
        other = "999988887777"
        for account in (None, ACCOUNT):
            with self.subTest(account=account):
                text = fargate.redact(f"role in {other}, trusted by {other}.", account)
                self.assertEqual("role in <account id>, trusted by <account id>.", text)
        self.assertEqual("ecs-svc/2000000000000000001 <account id>",
                         fargate.redact(f"ecs-svc/2000000000000000001 {other}"))

    def test_redact_keeps_a_twelve_digit_run_inside_a_hex_id(self):
        # A sha256 digest (and a task id) can hold twelve digits in a row; that is no account id.
        digest = "sha256:" + "f" + "123456789012" + "e" * 51
        other = "999988887777"
        for account in (None, ACCOUNT):
            with self.subTest(account=account):
                self.assertEqual(f"image @{digest} in task 9ab123456789012cd",
                                 fargate.redact(f"image @{digest} in task 9ab123456789012cd", account))
                self.assertEqual(f"@{digest} in <account id>", fargate.redact(f"@{digest} in {other}", account))

    def test_redact_replaces_a_repository_uri_and_keeps_the_digest(self):
        for account in (None, ACCOUNT):
            with self.subTest(account=account):
                text = fargate.redact(f"image {IMAGE} pulled", account)
                self.assertEqual(f"image <repository>@{DIGEST} pulled", text)
                self.assertNotIn(REPOSITORY_TAIL, text)


class TheDoubles(unittest.TestCase):
    def test_an_unknown_call_shape_is_recorded_and_exits_97(self):
        case = pathlib.Path(tempfile.mkdtemp(prefix="fargate-double-"))
        save(case, world())
        done = subprocess.run([sys.executable, "-I", str(DOUBLES), "aws", str(case),
                               "--region", REGION, "ecs", "delete-service", "--cluster", CLUSTER, "--output", "json"],
                              capture_output=True, text=True, env={"PATH": "/usr/bin:/bin"})
        self.assertEqual(97, done.returncode)
        calls = [json.loads(line) for line in (case / "calls.jsonl").read_text().splitlines()]
        self.assertEqual("ecs delete-service", calls[-1]["unexpected"])

    def test_the_tests_and_the_script_import_nothing_from_tools_scripts(self):
        for path in (SCRIPT, DOUBLES, pathlib.Path(__file__).resolve()):
            source = path.read_text()
            self.assertNotRegex(source, r"(?m)^\s*(from|import)\s+\S*cell_")
            self.assertNotRegex(source, r"spec_from_file_location\([^)]*scripts")

    def test_the_double_sees_a_credential_when_one_is_there(self):
        # The other half of TenCleanRestarts' no-credential assertion: the record can say yes.
        run = run_case(world(), env_changes={"AWS_ACCESS_KEY_ID": "AKIAEXAMPLE"})
        self.assertTrue(any(call["credential_env"] for call in run.calls))


if __name__ == "__main__":
    unittest.main()
