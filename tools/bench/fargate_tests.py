#!/usr/bin/env python3
"""Test companion for tools/bench/fargate.py (#305). Owner: the owner OWNERSHIP.md names for
tools/bench/.

The run itself needs the applied cell and about half an hour, and is #178's. What runs here is everything the
script decides without AWS: the order of its calls, which task it measures, which hop it takes,
which clocks it reads and how it labels them, when it stops and what it writes, and that what it
writes keeps to the Secrecy paragraph: no secret that comes in as text or an unvalidated field shows,
and only the four designated identifiers and validated measurements keep their digits.

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
`state` and a cookie; the session the CLI hands session-manager-plugin for an exec or a port
forward carries a session token; another account's id, against letters, in hex-like free text and
inside longer digit runs, is in the stop reason and the log's listening line (#976). No output may
hold any of them as text, nor the repository URI with its account label redacted, nor "arn:aws".
The one exception is Q223's: a validated measurement keeps its digits, so the caller's number can sit
inside one, which TheSecrecyMatrix proves while checking that no string holds it. Absence
alone proves nothing of a canary that never came in, so TheSecrecyMatrix asserts where each one's
placeholder lands, and names each source and outcome no path connects.

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
FOREIGN, FOREIGN_FORMS = doubles.FOREIGN, doubles.FOREIGN_FORMS
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
        self.txt_path = next(iter(sorted(case.glob("*.txt"))), case / "out.txt")
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


def run_case(document: dict, outputs: dict = None, region: str = REGION, extra=(), env_changes=None, ca: str = "ca.pem",
             git_stdout: str = None, txt_name: str = "out.txt") -> Run:
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
    if git_stdout is not None:
        # What `git rev-parse HEAD` prints here is the case's, so a commit can hold what a case needs.
        (bin_dir / "git").write_text(f"#!/bin/sh\nprintf '%s\\n' '{git_stdout}'\n")
        (bin_dir / "git").chmod(0o755)
    elif git:
        (bin_dir / "git").symlink_to(git)
    (case / "home").mkdir()
    save(case, document)
    env = {"PATH": str(bin_dir), "HOME": str(case / "home"), "AWS_REGION": region}
    if region is None:
        env.pop("AWS_REGION")
    env.update(env_changes or {})
    argv = [sys.executable, "-I", str(repo / "tools" / "bench" / "fargate.py"), "--txt", str(case / txt_name),
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


ACCOUNT_IN_A_NUMBER_NS = 1111222233330000000     # 2005-03-19T08:50:33.33Z: its digits hold ACCOUNT
ACCOUNT_IN_A_NUMBER_MS = 1111222233330


def strings_in(value):
    """Every string a parsed document holds, keys included; the numbers are left out on purpose."""
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for key, item in value.items():
            yield key
            yield from strings_in(item)
    elif isinstance(value, list):
        for item in value:
            yield from strings_in(item)


class TheTextRule(unittest.TestCase):
    """redact(): what happens to arbitrary text (Secrecy paragraph, #976 contract 4)."""

    def test_every_run_of_twelve_or_more_digits_goes_whole_whatever_touches_it(self):
        worked = {
            f"in {FOREIGN}.": "in <account id>.",
            f"account{FOREIGN}": "account<account id>",
            f"x{FOREIGN}y": "x<account id>y",
            f"1{FOREIGN}": "<account id>",
            f"{FOREIGN}{FOREIGN}": "<account id>",
            f"{ACCOUNT}4444": "<account id>",
            "ecs-svc/2000000000000000001": "ecs-svc/<account id>",
        }
        for text, expected in worked.items():
            with self.subTest(text):
                self.assertEqual(expected, fargate.redact(text))

    def test_an_identifier_shape_earns_arbitrary_text_nothing(self):
        for length in (32, 40, 64):
            with self.subTest(length):
                token = ("ab" + FOREIGN).ljust(length, "c")
                self.assertEqual(len(token), length)
                self.assertEqual("ab<account id>" + "c" * (length - 14), fargate.redact(token))
        self.assertEqual("sha256:<account id>" + "c" * 52, fargate.redact(f"sha256:{FOREIGN}" + "c" * 52))

    def test_it_promises_literal_ascii_runs_of_twelve_and_nothing_more(self):
        for kept in ("12345678901", "1234-5678-9012", "١" * 12):
            with self.subTest(kept):
                self.assertEqual(kept, fargate.redact(kept))

    def test_a_repository_uri_and_an_arn_are_recognized_first(self):
        # Were the digits replaced first, the URI would no longer be recognized and its path would stay.
        self.assertEqual(f"image <repository>@{DIGEST} pulled", fargate.redact(f"image {IMAGE} pulled"))
        self.assertEqual(f"x<repository>@{DIGEST}", fargate.redact(f"x{IMAGE}"))
        self.assertEqual("in (target-group <arn>) for <account id>", fargate.redact(f"in (target-group {TG_ARN}) for {ACCOUNT}"))

    def test_redacting_serialized_numbers_would_corrupt_them(self):
        # The regression behind "before rendering or serialization": the broad pass this script used
        # to run over its json.dumps makes a number a bare word and the document no JSON at all.
        serialized = json.dumps({"event_timestamp_ms": ACCOUNT_IN_A_NUMBER_MS})
        self.assertEqual(ACCOUNT_IN_A_NUMBER_MS, json.loads(serialized)["event_timestamp_ms"])
        with self.assertRaises(ValueError):
            json.loads(fargate.redact(serialized))


class ThePublication(unittest.TestCase):
    """publish_document(): the field paths, without a run. The real paths are in TheSecrecyMatrix."""

    COMMIT = ("ab" + FOREIGN).ljust(40, "c")
    TASK = ("ab" + FOREIGN).ljust(32, "c")
    PICTURE = "sha256:" + ("ab" + FOREIGN).ljust(64, "c")
    DEPLOYMENT = "ecs-svc/1999988887777000000"

    def published(self, account=ACCOUNT):
        return fargate.publish_document({
            "facts": {"commit": self.COMMIT, "image_digest": self.PICTURE, "task_definition": f"{self.TASK}:7",
                      "platform_version": self.COMMIT},
            "reason": f"task {self.TASK}", "ending": "incomplete",
            "restarts": [{"index": 1, "task_id": self.TASK, "hop_task": self.TASK, "deployment": self.DEPLOYMENT,
                          "task_arn": TG_ARN, "created_ns": ACCOUNT_IN_A_NUMBER_NS,
                          "stop": {"code": self.DEPLOYMENT, "reason": self.TASK},
                          "samples": [{"at_ns": ACCOUNT_IN_A_NUMBER_NS, "state": self.TASK, "reason": self.DEPLOYMENT}],
                          "lines": [{"ts": "2026-10-21T04:41:06.3490934+00:00", "ts_ns": ACCOUNT_IN_A_NUMBER_NS,
                                     "route": self.TASK, "status": 200, "msg": self.COMMIT,
                                     "duration_ms": 1.5, "event_timestamp_ms": ACCOUNT_IN_A_NUMBER_MS}]}],
        }, account)

    def test_the_same_string_stays_whole_in_its_designated_field_only(self):
        document = self.published()
        restart = document["restarts"][0]
        self.assertEqual(self.COMMIT, document["facts"]["commit"])
        self.assertEqual(self.PICTURE, document["facts"]["image_digest"])
        self.assertEqual(self.TASK, restart["task_id"])
        self.assertEqual(self.TASK, restart["hop_task"])
        self.assertEqual(self.DEPLOYMENT, restart["deployment"])
        masked_task = "ab<account id>" + "c" * 18
        masked_commit = "ab<account id>" + "c" * 26
        self.assertEqual(f"{masked_task}:7", document["facts"]["task_definition"])
        self.assertEqual(masked_commit, document["facts"]["platform_version"])
        self.assertEqual(f"task {masked_task}", document["reason"])
        self.assertEqual("ecs-svc/<account id>", restart["stop"]["code"])
        self.assertEqual(masked_task, restart["stop"]["reason"])
        self.assertEqual(masked_task, restart["samples"][0]["state"])
        self.assertEqual("ecs-svc/<account id>", restart["samples"][0]["reason"])
        self.assertEqual(masked_task, restart["lines"][0]["route"])
        self.assertEqual(masked_commit, restart["lines"][0]["msg"])

    def test_a_designated_field_still_refuses_what_does_not_fit_its_grammar(self):
        document = fargate.publish_document({"facts": {"commit": self.TASK, "image_digest": "sha256:" + "A" * 64},
                                             "restarts": [{"task_id": self.COMMIT, "deployment": "ecs-svc/" + FOREIGN}]}, ACCOUNT)
        self.assertEqual(fargate.MALFORMED, document["facts"]["commit"])
        self.assertEqual(fargate.MALFORMED, document["facts"]["image_digest"])
        self.assertEqual(fargate.MALFORMED, document["restarts"][0]["task_id"])
        self.assertEqual(fargate.MALFORMED, document["restarts"][0]["deployment"])

    def test_the_known_account_is_masked_inside_an_identifier_after_it_validates(self):
        task = ("ab" + ACCOUNT).ljust(32, "c")
        document = fargate.publish_document({"restarts": [{"task_id": task}]}, ACCOUNT)
        self.assertEqual("ab<account id>" + "c" * 18, document["restarts"][0]["task_id"])

    def test_a_validated_measurement_stays_a_number_holding_the_callers_digits(self):
        document = self.published()
        restart = document["restarts"][0]
        self.assertEqual(ACCOUNT_IN_A_NUMBER_NS, restart["created_ns"])
        self.assertEqual(ACCOUNT_IN_A_NUMBER_NS, restart["samples"][0]["at_ns"])
        self.assertEqual(ACCOUNT_IN_A_NUMBER_NS, restart["lines"][0]["ts_ns"])
        self.assertEqual(ACCOUNT_IN_A_NUMBER_MS, restart["lines"][0]["event_timestamp_ms"])
        self.assertEqual(-1500.5, fargate.publish_document({"restarts": [{"created_ns": -1500.5}]}, ACCOUNT)["restarts"][0]["created_ns"])

    def test_a_value_a_measurement_field_cannot_hold_is_the_marker(self):
        for bad in (f"{FOREIGN}", True, float("nan"), float("inf"), [FOREIGN], {"n": FOREIGN}):
            with self.subTest(repr(bad)):
                document = fargate.publish_document({"restarts": [{"created_ns": bad}]}, ACCOUNT)
                self.assertEqual(fargate.MALFORMED, document["restarts"][0]["created_ns"])

    def test_a_field_this_policy_does_not_list_is_text_whatever_it_holds_or_is_called(self):
        document = fargate.publish_document({"restarts": [{
            "status": int(FOREIGN), "duration_ms": f"{FOREIGN}", "someday": {"task_id": FOREIGN, "n": int(FOREIGN)}}],
            "elsewhere": int(FOREIGN)}, ACCOUNT)
        restart = document["restarts"][0]
        self.assertEqual("<account id>", restart["status"])
        self.assertEqual("<account id>", restart["duration_ms"])
        self.assertEqual('{"task_id": "<account id>", "n": <account id>}', restart["someday"])
        self.assertEqual("<account id>", document["elsewhere"])

    def test_a_text_field_holding_an_object_is_the_marker(self):
        document = fargate.publish_document({"restarts": [{"stop": {"reason": {"x": FOREIGN}}}]}, ACCOUNT)
        self.assertEqual(fargate.MALFORMED, document["restarts"][0]["stop"]["reason"])

    def test_the_task_arn_stays_for_the_aws_calls_and_is_published_as_a_placeholder(self):
        record = {"task_arn": TG_ARN, "task_id": self.TASK}
        document = fargate.publish_document({"restarts": [record]}, ACCOUNT)
        self.assertEqual("<arn>", document["restarts"][0]["task_arn"])
        self.assertEqual(TG_ARN, record["task_arn"])

    def test_a_message_keeps_a_published_identifier_and_redacts_the_rest(self):
        task = fargate.publish_identifier(self.TASK, fargate.TASK_ID, ACCOUNT)
        line = fargate.compose("task {task} on {host} after {n}", task=task, host=f"h{FOREIGN}", n=3)
        self.assertEqual(f"task {self.TASK} on h<account id> after 3", line)
        self.assertEqual(line, fargate.publish_text(line))
        # Lost by an f-string, the type goes and the text is arbitrary again: it fails safe.
        self.assertEqual(f"task {'ab<account id>' + 'c' * 18}", fargate.publish_text(f"task {self.TASK}"))


class TheValidators(unittest.TestCase):
    def test_an_instant_is_one_of_the_read_forms_and_nothing_else(self):
        for bad in (True, None, [], "not an instant", "2026-10-21T04:41:06Z\n", " 2026-10-21T04:41:06Z",
                    "2026-13-21T04:41:06Z", "2026-02-30T04:41:06Z", "2026-10-21T24:41:06Z", "2026-10-21T04:60:06Z",
                    "2026-10-21T04:41:61Z", "2026-10-21T04:41:06+24:00", "٢026-10-21T04:41:06Z",
                    float("nan"), float("inf"), 10 ** 400, -(10 ** 12)):
            with self.subTest(repr(bad)[:40]):
                with self.assertRaises(ValueError):
                    fargate.time_ns(bad)
        # A number stays the CLI's other timestamp format (the worked instant's seconds).
        self.assertEqual(1792557666_000000000, fargate.time_ns(1792557666))
        self.assertEqual(1792557666_500000000, fargate.time_ns(1792557666.5))

    def test_a_log_line_keeps_only_values_valid_for_their_field(self):
        ts = "2026-10-21T04:41:06.3490934+00:00"

        def line(**fields):
            return fargate.read_lines([{"timestamp": 1, "message": json.dumps({"ts": ts, **fields})}])[0]

        for good in (100, 200, 302, 599):
            self.assertEqual(good, line(status=good)["status"])
        for bad in (99, 600, 200.0, True, "200", None, int(FOREIGN)):
            with self.subTest(status=bad):
                self.assertEqual(fargate.MALFORMED, line(status=bad)["status"])
        for good in (0, 1.5, 640, 10 ** 11 - 1):
            self.assertEqual(good, line(duration_ms=good)["duration_ms"])
        for label, bad in (("-1", -1), ("'1.5'", "1.5"), ("True", True), ("None", None), ("inf", float("inf")),
                           ("nan", float("nan")), ("10**400", 10 ** 400), ("10**11", 10 ** 11),
                           ("10**11 as a float", 1e11), ("a twelve-digit number", int(FOREIGN))):
            with self.subTest(duration_ms=label):
                self.assertEqual(fargate.MALFORMED, line(duration_ms=bad)["duration_ms"])
        self.assertEqual(fargate.MALFORMED, line(route={"x": FOREIGN})["route"])
        self.assertNotIn("status", line())
        self.assertNotIn("duration_ms", line())

    def test_a_malformed_status_never_reads_as_the_passing_check_or_the_302(self):
        ts = "2026-10-21T04:41:06.3490934+00:00"
        marks = fargate.log_marks(fargate.read_lines([
            {"message": json.dumps({"ts": ts, "route": "health", "status": 200.0})},
            {"message": json.dumps({"ts": ts, "route": "asset", "status": 302.0})}]))
        self.assertIsNone(marks["first_passing_check"])
        self.assertIsNone(marks["redirect"])

    def test_a_log_ts_is_a_string_a_malformed_one_marks_its_line_and_a_line_with_none_is_skipped(self):
        lines = fargate.read_lines([
            {"timestamp": 7, "message": json.dumps({"ts": f"BADVALUE{FOREIGN}", "route": "health", "status": 200, "msg": "m"})},
            {"timestamp": "BADVALUE", "message": json.dumps({"ts": 1792557666, "route": "health", "status": 200})},
            {"message": "not json"}, {"message": json.dumps(["a list"])}, {"message": json.dumps({"no": "ts"})}])
        self.assertEqual([{"ts": fargate.MALFORMED, "event_timestamp_ms": 7},
                          {"ts": fargate.MALFORMED, "event_timestamp_ms": fargate.MALFORMED}], lines)
        # Neither counts as a health line nor a mark.
        marks = fargate.log_marks(lines)
        self.assertEqual(0, marks["health_lines"])
        self.assertIsNone(marks["first_passing_check"])

    def test_an_event_timestamp_is_a_whole_ms_instant(self):
        ts = "2026-10-21T04:41:06.3490934+00:00"

        def stamp(value):
            return fargate.read_lines([{"timestamp": value, "message": json.dumps({"ts": ts})}])[0]["event_timestamp_ms"]

        self.assertEqual(ACCOUNT_IN_A_NUMBER_MS, stamp(ACCOUNT_IN_A_NUMBER_MS))
        self.assertIsNone(fargate.read_lines([{"message": json.dumps({"ts": ts})}])[0]["event_timestamp_ms"])
        for bad in (1.5, -1, True, "1", 10 ** 20):
            with self.subTest(repr(bad)):
                self.assertEqual(fargate.MALFORMED, stamp(bad))

    def test_a_task_id_is_the_arns_last_segment_in_its_grammar(self):
        good = "0" * 31 + "a"
        self.assertEqual(good, fargate.task_id_of(f"arn:aws:ecs:{REGION}:{ACCOUNT}:task/c/{good}"))
        for bad in (f"arn:aws:ecs:{REGION}:{ACCOUNT}:task/c/{good.upper()}", f"arn:aws:ecs:{REGION}:{ACCOUNT}:task/c/{good}0", None, 7):
            with self.subTest(repr(bad)):
                self.assertIsNone(fargate.task_id_of(bad))


class TheSecrecyMatrix(CaseBase):
    """Real input-to-output paths, source by outcome and by sink. Each entry asserts the placeholder
    where its canary lands, not only the canary's absence. An entry `unreachable` is a pair no code
    path connects, named with the reason rather than counted as coverage."""

    # The combinations no path connects:
    #   stoppedReason, clean or refusal: a stop ends the run as breach or incomplete, and a refusal
    #     is before any new task exists.
    #   stoppedReason or a log line, stderr or stdout: the breach reason carries the stop's code and
    #     placement, and the file holds the rest.
    #   a log line, refusal: nothing is read from a stream before restart 1 and nothing is written.
    #   the --txt path, refusal: the "wrote" line follows the files, which a refusal does not write.
    #   any free text, breach's stderr: its reason is built from the task id, the code and placement,
    #     so test_breach pins that line whole.
    #   stderr, clean: a clean run says nothing on it, pinned empty by test_clean.
    #   a log line, the .txt: report() renders no msg or route, so a log canary's only sink is the
    #     .json.

    @classmethod
    def setUpClass(cls):
        cls.path_name = f"report{FOREIGN}.txt"
        plans = [plan(exec="no-session") for _ in range(10)]
        cls.clean = run_case(world(plans, platform_version=f"LATEST-{FOREIGN}", exec_error_code=f"Target{FOREIGN}Exception"),
                             txt_name=cls.path_name)

    def test_clean(self):
        run = self.clean
        self.assertEqual(0, run.code, run.stderr)
        self.assertNoSecrets(run)
        self.assertEqual("", run.stderr)
        # .txt: the platform version and the exec's error code are free text.
        self.assertEqual("LATEST-<account id>", run.json["facts"]["platform_version"])
        self.assertIn("platform version LATEST-<account id>", run.txt)
        self.assertIn("no session: execute-command failed (Target<account id>Exception)", run.txt)
        self.assertEqual("no session: execute-command failed (Target<account id>Exception)", run.json["restarts"][0]["zero"]["fallback"])
        # .json: the log's listening line, in every form.
        listening = run.json["restarts"][0]["marks"]["listening"]["msg"]
        self.assertIn("account<account id> x<account id>y a<account id> ", listening)
        self.assertIn("ab<account id>" + "c" * 18, listening)
        self.assertIn("ab<account id>" + "c" * 50, listening)
        self.assertTrue(listening.endswith(" <account id> <account id>"), listening)
        # stdout: the --txt path is the operator's text.
        self.assertIn("wrote ", run.stdout)
        self.assertIn("report<account id>.txt", run.stdout)
        # Identifiers, in each sink that carries them: their zeros are a twelve-digit run and more.
        first = task_id(100)
        self.assertIn("000000000000", first)
        self.assertIn(f"#1 task {first} complete", run.txt)
        self.assertEqual(first, run.json["restarts"][0]["task_id"])
        self.assertIn(f"restart 2/10: deployment ecs-svc/2000000000000000001, hop through task {first}", run.stdout)
        self.assertEqual("ecs-svc/2000000000000000000", run.json["restarts"][0]["deployment"])
        self.assertEqual(task_id(1), run.json["restarts"][0]["hop_task"])

    def test_the_commit_keeps_a_long_digit_run_where_arbitrary_text_would_lose_it(self):
        # Digits of its own: the designated source keeps its run, so a canary in SECRETS cannot stand in it.
        commit = ("ab" + "123456789012345").ljust(40, "c")
        run = run_case(world([plan(stable=False)]), git_stdout=commit)
        self.assertEqual(4, run.code, run.stderr)
        self.assertEqual(commit, run.json["facts"]["commit"])
        self.assertIn(f"commit {commit},", run.txt)

    def test_breach(self):
        plans = [plan(), plan(health=[None, ["initial", f"Elb.{FOREIGN}"], ["initial", "Elb.InitialHealthChecking"],
                                      ["unhealthy", "Target.ResponseCodeMismatch"]],
                              stop_at_sample=4)] + [plan() for _ in range(8)]
        run = run_case(world(plans), txt_name=self.path_name)
        self.assertEqual(3, run.code, run.stderr)
        self.assertNoSecrets(run)
        row = run.json["restarts"][1]
        self.assertIn("(target-group <arn>) account<account id> x<account id>y", row["stop"]["reason"])
        self.assertIn("ab<account id>" + "c" * 26, row["stop"]["reason"])
        self.assertIn("stopped ServiceSchedulerInitiated: Task failed ELB health checks in (target-group <arn>) account<account id>", run.txt)
        self.assertIn("account<account id>", row["marks"]["listening"]["msg"])
        self.assertEqual("Elb.<account id>", row["samples"][1]["reason"])
        # The reason the task's id stays whole in, on stderr and in both files.
        self.assertRegex(run.stderr, rf"^fargate: breach: the scheduler stopped task {task_id(102)} for failing the load balancer's "
                                     r"health checks \(ServiceSchedulerInitiated\), seen at T0 \+ -?\d+\.\.-?\d+ s, before the T0 \+ 60 s hard bound\n$")
        self.assertIn(f"ending: BREACH on restart 2: the scheduler stopped task {task_id(102)} ", run.txt)
        self.assertIn(f"stopped task {task_id(102)} ", run.json["reason"])
        self.assertIn("report<account id>.txt", run.stdout)

    def test_incomplete_a_stop_not_for_health_carries_its_code_and_reason(self):
        plans = [plan(stop_at_sample=1, stop=[f"Code{FOREIGN}", f"Essential container of task {task_id(100)} exited {FOREIGN_FORMS}"])]
        run = run_case(world(plans), txt_name=self.path_name)
        self.assertEqual(4, run.code, run.stderr)
        self.assertNoSecrets(run)
        self.assertEqual(f"restart 1: task {task_id(100)} stopped (Code<account id>), not for health.", run.json["reason"])
        self.assertIn(f"fargate: incomplete: restart 1: task {task_id(100)} stopped (Code<account id>), not for health.\n", run.stderr)
        self.assertIn(f"INCOMPLETE after 0 complete restart(s): restart 1: task {task_id(100)} stopped (Code<account id>)", run.txt)
        # The task's own id, as arbitrary text in the reason and whole in its designated field.
        self.assertEqual(task_id(100), run.json["restarts"][0]["task_id"])
        self.assertIn("Essential container of task <account id> exited account<account id> x<account id>y", run.json["restarts"][0]["stop"]["reason"])
        self.assertIn("Essential container of task <account id> exited account<account id> x<account id>y", run.txt)
        self.assertIn("report<account id>.txt", run.stdout)

    def test_incomplete_an_aws_error_code_is_text(self):
        run = run_case(world([plan()], update_service_fail_at=1, error_code=f"Thing{FOREIGN}Exception"))
        self.assertEqual(4, run.code, run.stderr)
        self.assertNoSecrets(run)
        self.assertEqual("an AWS call failed (Thing<account id>Exception)", run.json["reason"])
        self.assertIn("fargate: incomplete: an AWS call failed (Thing<account id>Exception)\n", run.stderr)
        self.assertIn("INCOMPLETE after 0 complete restart(s): an AWS call failed (Thing<account id>Exception)", run.txt)

    def test_refusal_says_the_code_as_text_and_writes_no_report(self):
        run = run_case(world(list_tasks_fail_at_poll=2, error_code=f"Thing{FOREIGN}Exception"))
        self.assertEqual(2, run.code, run.stderr)
        self.assertIsNone(run.txt)
        self.assertIsNone(run.json)
        self.assertEqual([], list(run.case.glob("out.*")))
        self.assertNoSecrets(run)
        self.assertEqual("fargate: an AWS call failed before restart 1 (Thing<account id>Exception).\n", run.stderr)
        # stdout before the first update-service is the one fixed line of expected length.
        self.assertTrue(run.stdout.startswith("fargate: ten restarts, about half an hour"), run.stdout)
        self.assertEqual(1, len(run.stdout.splitlines()))

    def test_incidental_digits_a_measurement_keeps_and_a_string_never_shows(self):
        # Contract 3's exception, on the real path: a createdAt and a log event's timestamp whose
        # digits hold the caller's number, and calculations from them unchanged.
        created = doubles.iso(ACCOUNT_IN_A_NUMBER_NS)
        log = [["Now listening on: http://[::]:8080", 900], ["Registry revision 42 (\"etag\") is live with 1 app(s).", 1500],
               ["health", 200, 3000],
               ["raw", {"msg": "Request", "route": "health", "status": 200, "duration_ms": 1.2,
                        "_event_timestamp": ACCOUNT_IN_A_NUMBER_MS}, 3100]]
        finished = run_case(world([plan(created_at=created, log=log), plan(stable=False)]))
        self.assertEqual(4, finished.code, finished.stderr)
        first = finished.json["restarts"][0]
        self.assertEqual(ACCOUNT_IN_A_NUMBER_NS, first["created_ns"])
        stamps = [line["event_timestamp_ms"] for line in first["lines"]]
        self.assertIn(ACCOUNT_IN_A_NUMBER_MS, stamps)
        self.assertIs(type(stamps[-1]), int)
        for text in strings_in(finished.json):
            self.assertNotIn(ACCOUNT, text)
        self.assertIn(ACCOUNT, finished.json_path.read_text(), "the number's digits are the exception this case proves")
        # startedAt, BASE_S + 100 000 s + 22 s, less the createdAt above: a value a pass over the
        # rendered report would mask, and the calculation the valid input always gave.
        self.assertRegex(finished.txt, r"#1 task \w+ complete: pull-and-place 681435454670 ")
        # The others are as the worked world gives them.
        self.assertRegex(finished.txt, r"listening \(log\)\s+900\s+900\n")

    def test_a_known_account_inside_identifiers_is_masked_and_the_aws_calls_keep_the_real_one(self):
        number = int(ACCOUNT, 16)
        collided = f"{number:032x}"
        self.assertIn(ACCOUNT, collided)
        digest = "sha256:" + ACCOUNT + "a" * 52
        image = f"{ACCOUNT}.dkr.ecr.{REGION}.amazonaws.com/spa-poc/gateway@{digest}"
        document = world([plan(stable=False, deployment_id=f"ecs-svc/{ACCOUNT}0000000", decoy=False)],
                         td_image=image, next_task=number)
        document["tasks"]["1"]["digest"] = digest
        run = run_case(document, outputs={"gatewayImage": image}, git_stdout=(ACCOUNT + "a" * 28))
        self.assertEqual(4, run.code, run.stderr)
        self.assertNoUnexpectedCalls(run)
        self.assertNoSecrets(run)
        self.assertEqual("<account id>" + "a" * 28, run.json["facts"]["commit"])
        self.assertEqual("sha256:<account id>" + "a" * 52, run.json["facts"]["image_digest"])
        restart = run.json["restarts"][0]
        self.assertEqual("ecs-svc/<account id>0000000", restart["deployment"])
        self.assertEqual("0" * 20 + "<account id>", restart["task_id"])
        self.assertIn("deployment ecs-svc/<account id>0000000,", run.stdout)
        self.assertIn(f"#1 task {'0' * 20}<account id> ", run.txt)
        # The stream the script read is the task's real one: a masked id would name no stream.
        streams = {argv[argv.index("--log-stream-name") + 1] for argv in run.aws("logs", "get-log-events")}
        self.assertEqual({f"{GATEWAY}/{GATEWAY}/{collided}"}, streams)


class TheMalformedValues(CaseBase):
    """A malformed value supplied, against one legitimately absent (#976 contract 5). Each test hands
    assertNoEcho the values it rejected, which it looks for whole in every output, so an echo of any
    of them turns its case red. BAD holds BADVALUE and the foreign canary, and every case also checks
    that BADVALUE shows nowhere."""

    BAD = f"BADVALUE{FOREIGN}"

    def assertNoEcho(self, run, *rejected):
        self.assertNoSecrets(run)
        for name, text in (("txt", run.txt), ("json", run.json_path.read_text() if run.json_path.exists() else None),
                           ("stderr", run.stderr), ("stdout", run.stdout)):
            # assertFalse, not assertNotIn, so that a failure names the output and never prints the value.
            for value in ("BADVALUE", *(str(value) for value in rejected if value is not None)):
                self.assertFalse(value in (text or ""), f"a rejected value reached the {name}")

    def assertRefused(self, run, says, *rejected):
        self.assertEqual(2, run.code, run.stderr)
        self.assertIsNone(run.txt)
        self.assertIsNone(run.json)
        self.assertEqual([], run.aws("ecs", "update-service"))
        self.assertIn(says, run.stderr)
        self.assertNoEcho(run, *rejected)

    def test_a_malformed_image_digest_is_refused_before_restart_1(self):
        for digest in (f"sha256:{self.BAD}", "sha256:" + "A" * 64, "sha256:" + "a" * 63):
            with self.subTest(digest):
                image = f"{ACCOUNT}.dkr.ecr.{REGION}.amazonaws.com/spa-poc/gateway@{digest}"
                self.assertRefused(run_case(world(td_image=image), outputs={"gatewayImage": image}), "not pinned by digest", digest)

    def test_a_malformed_caller_account_is_refused_before_restart_1(self):
        for answer in (self.BAD, "12345678901", None, 111122223333):
            with self.subTest(repr(answer)):
                self.assertRefused(run_case(world(account_answer=answer)), "no well-formed account id", answer)

    def test_a_malformed_task_id_on_the_hop_of_restart_1_is_refused(self):
        run = run_case(world(arn_ids={"1": self.BAD}))
        self.assertRefused(run, "holds no well-formed task id", self.BAD)

    def test_a_malformed_task_id_on_the_new_task_is_incomplete(self):
        run = run_case(world(arn_ids={"100": self.BAD}))
        self.assertEqual(4, run.code, run.stderr)
        self.assertEqual("incomplete", run.json["ending"])
        self.assertEqual(1, len(run.aws("ecs", "update-service")))
        self.assertIn("holds no well-formed task id", run.json["reason"])
        self.assertNotIn("task_id", run.json["restarts"][0])
        self.assertNoEcho(run, self.BAD)

    def test_a_malformed_deployment_id_is_incomplete_since_update_service_was_sent(self):
        for deployment in (self.BAD, "ecs-svc/123", "ecs-svc/" + "1" * 20):
            with self.subTest(deployment):
                run = run_case(world([plan(deployment_id=deployment)]))
                self.assertEqual(4, run.code, run.stderr)
                self.assertEqual(1, len(run.aws("ecs", "update-service")))
                self.assertEqual("update-service named a malformed PRIMARY deployment id on restart 1.", run.json["reason"])
                self.assertNotIn("deployment", run.json["restarts"][0])
                self.assertNoEcho(run, deployment)

    def test_a_malformed_required_instant_is_incomplete(self):
        run = run_case(world([plan(created_at=self.BAD)]))
        self.assertEqual(4, run.code, run.stderr)
        self.assertEqual("an unexpected ValueError", run.json["reason"])
        self.assertNoEcho(run, self.BAD)

    def test_a_malformed_302_duration_is_incomplete_where_an_absent_one_is_not(self):
        def world_with(fields):
            log = plan()["log"] + [["raw", {"msg": "Request", "route": "asset", "status": 302, **fields}, 30000]]
            return world([plan(log=log), plan(stable=False)])

        # A string, and a number no request takes: a twelve-digit id in a numeric field is no
        # measurement for being a number (#976 contract 3, valid_duration's bound).
        for label, bad in (("a string", self.BAD), ("a twelve-digit number", int(FOREIGN))):
            with self.subTest(label):
                run = run_case(world_with({"duration_ms": bad}))
                self.assertEqual(4, run.code, run.stderr)
                self.assertEqual(1, len(run.aws("ecs", "update-service")))
                self.assertEqual(f"restart 1: task {task_id(100)}'s 302 line holds a malformed duration_ms.", run.json["reason"])
                self.assertEqual(fargate.MALFORMED, run.json["restarts"][0]["marks"]["redirect"]["duration_ms"])
                self.assertFalse(run.json["restarts"][0]["complete"])
                self.assertNoEcho(run, bad)
        # The same line with none: the benchmark's existing absence, a row with no value.
        absent = run_case(world_with({}))
        self.assertEqual(2, len(absent.aws("ecs", "update-service")))
        self.assertTrue(absent.json["restarts"][0]["complete"])
        self.assertNotIn("duration_ms", absent.json["restarts"][0]["marks"]["redirect"])
        self.assertRegex(absent.txt, r"#1 task \w+ complete: .*, 302 - ms at")

    def test_malformed_optional_diagnostics_are_the_marker_and_the_run_is_valid(self):
        log = plan()["log"] + [
            ["raw", {"msg": "Request", "route": {"x": self.BAD}, "status": self.BAD, "duration_ms": self.BAD,
                     "_event_timestamp": self.BAD}, 3200],
            ["raw", {"ts": self.BAD, "msg": "Request", "route": "health", "status": 200}, 3300]]
        plans = [plan(log=log, exec="bad-drift", probe_status_line=f"HTTP/1.1 {FOREIGN} Found"),
                 plan(stable=False, probe_status_line="HTTP/1.1 BADVALUE Found")]
        run = run_case(world(plans, health_reason=[self.BAD]), git_stdout=self.BAD)
        self.assertEqual(4, run.code, run.stderr)
        self.assertEqual(2, len(run.aws("ecs", "update-service")))
        first = run.json["restarts"][0]
        self.assertTrue(first["complete"])
        marker = fargate.MALFORMED
        self.assertEqual(marker, run.json["facts"]["commit"])
        self.assertIn(f"commit {marker},", run.txt)
        self.assertEqual({"ClockErrorBound": marker, "ReferenceTimestamp": marker, "ClockSynchronizationStatus": marker},
                         first["zero"]["clock_drift"])
        self.assertIn(f"ClockDrift bound {marker} ms, {marker}", run.txt)
        self.assertEqual(marker, first["probes"][0]["status"])
        self.assertEqual(marker, run.json["restarts"][1]["probes"][0]["status"])
        self.assertEqual(marker, first["samples"][1]["reason"])
        odd = next(line for line in first["lines"] if line.get("route") == marker)
        self.assertEqual((marker, marker, marker), (odd["status"], odd["duration_ms"], odd["event_timestamp_ms"]))
        self.assertTrue(any(line["ts"] == marker for line in first["lines"]))
        # The calculations stand on the valid lines alone.
        self.assertRegex(run.txt, r"first passing check \(log\)\s+3000\s+3000\n")
        self.assertNoEcho(run, self.BAD)

    def test_a_stopped_reason_that_is_no_string_is_the_marker(self):
        run = run_case(world([plan(stop_at_sample=1)], stopped_reason={"x": self.BAD}))
        self.assertEqual(4, run.code, run.stderr)
        self.assertEqual(fargate.MALFORMED, run.json["restarts"][0]["stop"]["reason"])
        self.assertNoEcho(run, self.BAD)

    def test_a_stop_code_that_is_a_number_gains_nothing_by_it(self):
        run = run_case(world([plan(stop_at_sample=1, stop=[int(FOREIGN), "exited"])]))
        self.assertEqual(4, run.code, run.stderr)
        self.assertEqual(fargate.MALFORMED, run.json["restarts"][0]["stop"]["code"])
        self.assertIn(f"stopped ({fargate.MALFORMED}), not for health.", run.stderr)
        self.assertNoEcho(run, int(FOREIGN))

    def test_legitimate_absence_stays_absent(self):
        # git's own failure is the commit's fallback, not a malformed value. The other legitimate
        # absences are asserted where they live: the StartedAt fallback in TheZero, a null CloudWatch
        # timestamp in TheValidators.
        run = run_case(world([plan(stable=False)]))
        self.assertEqual("unknown", run.json["facts"]["commit"])
        self.assertIn("commit unknown,", run.txt)
        self.assertEqual(4, run.code, run.stderr)
        self.assertNoEcho(run)


class TheUnwritableReport(CaseBase):
    def test_a_report_path_that_cannot_be_written_ends_on_a_fixed_line_with_no_traceback(self):
        # write() raises before either file exists: the .txt's directory is missing. The path holds
        # the foreign canary, so a traceback quoting it would also fail assertNoSecrets.
        run = run_case(world([plan(stable=False)]), txt_name=f"missing{FOREIGN}/out.txt")
        self.assertEqual(4, run.code, run.stderr)
        self.assertIsNone(run.json)
        self.assertFalse("Traceback" in run.stderr, "a traceback reached stderr")
        self.assertIn("fargate: the report files could not be written (FileNotFoundError).\n", run.stderr)
        self.assertIn("fargate: incomplete: ", run.stderr)
        self.assertNotIn("wrote ", run.stdout)
        self.assertNoSecrets(run)


class TheProse(unittest.TestCase):
    def test_the_docstrings_state_the_division_and_drop_the_old_claims(self):
        module = " ".join(fargate.__doc__.split())
        self.assertIn("**Secrecy** (Q7, amended by #976's confirmed answers).", module)
        self.assertIn("An identifier-shaped token in arbitrary text is not an exception.", module)
        for doc in (module, " ".join(fargate.redact.__doc__.split())):
            for old in ("One exception", "Nothing this script reads", "tunnel.sh's session filter", "every line written passed through"):
                self.assertNotIn(old, doc)


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
