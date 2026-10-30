#!/usr/bin/env python3
"""Test companion for tools/scripts/tunnel.sh and tools/scripts/verify-tunnel.sh. Owner: workstream 1.

Task 1.6b (#57): a Session Manager port forward through the gateway task to the internal ALB, and a
check that a health path answers over it with trusted TLS. The row's done-when, verify-tunnel.sh
exiting 0 on a clean workstation, needs the applied cell and is the sitting's (B1 in
.scratch/sitting/spec.md). What a lane can prove without AWS is everything the two scripts decide:
which task and container they choose, the Session Manager call they make, the refusals before it,
and how verify-tunnel.sh reads each way a request can fail. This file proves those, in D-3's shape
(docs/dependencies.md): Python's unittest, running the real scripts under /bin/bash.

**What runs is each script itself**, in place, through its arguments, PATH and environment, under
/bin/bash rather than the shebang, so a Mac with Homebrew's bash first on PATH still tests the 3.2
macOS ships. SCRIPT_TESTS_REQUIRE_BASH fails the run when /bin/bash is another version.

**A constructed environment, inheriting nothing.** PATH is the case's bin directory alone: a
one-line exec of each real tool the scripts use, and doubles for aws, session-manager-plugin and,
in most verify-tunnel.sh cases, curl. HOME is the case's own. No case can reach AWS: there is no
real aws on PATH.

**The doubles are this file's own functions**, started as `--double <name> <case dir> <args>`. The
AWS double keeps the case's cell in world.json -- its cluster, its service's tasks and their
containers -- and answers each call the shape the AWS CLI's `--output text` prints
it, with the error codes the services document. It answers only the call shapes tunnel.sh makes and
records every call; anything else is recorded and exits 97, so a new call in the script turns a case
red instead of being answered. Each query the double answers was evaluated against the same data
with the jmespath library the AWS CLI 2.37.12 bundles, so its answers are JMESPath's, not this
file's guess.

**tunnel.sh reads its names from the outputs document, and only from it (#198).** The cluster,
the service and the load balancer's DNS name are contracts/infra-outputs.schema.json's ecsCluster,
ecsService and albDnsName in infra/envs/poc/outputs.json, which seed.sh, deploy.sh and rollback.sh
write after every apply. The script finds that file from its own path, so each case runs a copy of
the scripts in a repository tree of its own and writes the document there, in the shape of
contracts/infra-outputs.example.json; nothing is read from or written to this checkout's
infra/envs/poc. The AWS double answers no call that looks a name up (describe-load-balancers among
them), so a tunnel that went back to asking AWS for its names turns a case red, and one case gives
the document names the module's convention would never guess.

**The world's names are read from the Terraform that makes them, never typed here.** The cluster,
the service, the gateway container and the load balancer are the locals in
infra/modules/cell/ecs.tf and alb.tf, read out of those files (CODING_STANDARDS.md, "A guard reads
the thing it guards"). The load balancer's scheme is alb.tf's too: tunnel.sh no longer asks AWS
whether it is internal, so this companion holds the module to `internal = true`.

**Two verify-tunnel.sh cases use the real curl**: one against a local TLS listener whose certificate
no trust store holds, which must fail as untrusted, and one against a port nothing listens on. They
show the script reaches the workstation's trust store and does not step round it, which a double
cannot show.

**A universal held on every run:** nothing either script prints names the account id. Task ARNs
carry it, and the output of a sitting is posted to a public repository (#122's rule).

Run: python3 -I tools/scripts/tunnel_tests.py
"""

from __future__ import annotations

import json
import os
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent
TUNNEL = ROOT / "tools" / "scripts" / "tunnel.sh"
VERIFY = ROOT / "tools" / "scripts" / "verify-tunnel.sh"
ECS_TF = ROOT / "infra" / "modules" / "cell" / "ecs.tf"
EXAMPLE_OUTPUTS = ROOT / "contracts" / "infra-outputs.example.json"
OUTPUTS = pathlib.Path("infra") / "envs" / "poc" / "outputs.json"
# What a case copies into its own repository tree: tunnel.sh, what it sources and reads with, and
# verify-tunnel.sh beside it.
SCRIPT_FILES = ("tunnel.sh", "verify-tunnel.sh", "cell-common.sh", "cell_documents.py")
ALB_TF = ROOT / "infra" / "modules" / "cell" / "alb.tf"
BASH = "/bin/bash"
REQUIRE_BASH = "SCRIPT_TESTS_REQUIRE_BASH"

ACCOUNT = "111122223333"
REGION = "us-east-2"
CELL = "poc"
SECRETS = (ACCOUNT,)

# The real tools the scripts read their answers with. Everything else is absent.
TOOLS = ("awk", "sed", "tr", "cat", "mktemp", "rm", "dirname")


def terraform_local(path: pathlib.Path, name: str, cell: str = CELL) -> str:
    """The value of `name = "..."` in a locals block, with ${var.cell} put in."""
    match = re.search(rf'^\s*{re.escape(name)}\s*=\s*"([^"]*)"', path.read_text(), re.MULTILINE)
    if match is None:
        raise AssertionError(f"{path.name} has no local {name}")
    return match[1].replace("${var.cell}", cell)


CLUSTER = terraform_local(ECS_TF, "cluster_name")
SERVICE = terraform_local(ECS_TF, "service_name")
CONTAINER = terraform_local(ECS_TF, "gateway_container_name")
SIDECAR = terraform_local(ECS_TF, "mock_api_container_name")
LB_NAME = terraform_local(ALB_TF, "lb_name")
ALB_DNS = f"internal-{LB_NAME}-1234567890.{REGION}.elb.amazonaws.com"


def task(task_id: str, *, last_status: str = "RUNNING", exec_on: bool = True, agent: str | None = "RUNNING",
         gateway: bool = True) -> dict:
    """One task of the service, as describe-tasks returns it: the gateway and the mock API."""
    containers = [{"name": SIDECAR, "runtimeId": f"{task_id}-1111111111",
                   "managedAgents": [{"name": "ExecuteCommandAgent", "lastStatus": agent or "STOPPED"}]}]
    if gateway:
        gateway_container = {"name": CONTAINER, "runtimeId": f"{task_id}-2222222222"}
        if agent is not None:
            gateway_container["managedAgents"] = [{"name": "ExecuteCommandAgent", "lastStatus": agent}]
        containers.append(gateway_container)
    return {"taskArn": f"arn:aws:ecs:{REGION}:{ACCOUNT}:task/{CLUSTER}/{task_id}",
            "lastStatus": last_status, "desiredStatus": "RUNNING",
            "enableExecuteCommand": exec_on, "containers": containers}


def default_world() -> dict:
    """The cell as tasks 1.3 and 1.4 make it, on one running task, and the outputs document's names
    for it as seed.sh writes them."""
    return {
        "region": REGION,
        "outputs": {"region": REGION, "cell": CELL, "ecsCluster": CLUSTER, "ecsService": SERVICE,
                    "albDnsName": ALB_DNS},
        "clusters": {CLUSTER: {SERVICE: [task("0123456789abcdef0123456789abcdef")]}},
        "start_session_status": 0,
    }


def outputs_document(fields: dict) -> dict:
    """infra/envs/poc/outputs.json: contracts/infra-outputs.example.json with the case's fields put
    in, so the document keeps the contract's shape. A field given as None is left out."""
    document = json.loads(EXAMPLE_OUTPUTS.read_text())
    for name, value in fields.items():
        if value is None:
            document.pop(name, None)
        else:
            document[name] = value
    return document


# --- the AWS double ------------------------------------------------------------------------------

class Unexpected(Exception):
    """A call the double does not answer: recorded, and exit 97."""


class ServiceError(Exception):
    def __init__(self, code: str, message: str):
        super().__init__(code)
        self.code = code
        self.message = message


GLOBAL_OPTIONS = {"region", "output", "query"}
# (required, optional) options per command, from the AWS CLI reference:
# https://awscli.amazonaws.com/v2/documentation/api/latest/reference/index.html
COMMANDS = {
    ("ecs", "list-tasks"): ({"cluster", "service-name", "desired-status"}, set()),
    ("ecs", "describe-tasks"): ({"cluster", "tasks"}, set()),
    ("ssm", "start-session"): ({"target", "document-name", "parameters"}, set()),
}
OPERATIONS = {"list-tasks": "ListTasks",
              "describe-tasks": "DescribeTasks", "start-session": "StartSession"}
# describe-tasks takes several task ids after one --tasks.
MULTI_VALUED = {"tasks"}

# The queries tunnel.sh sends, each evaluated against this file's world with the AWS CLI's own
# jmespath before it was written down here.
TASKS_QUERY = "taskArns"
DESCRIBE_QUERY = (f"tasks[].[taskArn, lastStatus, enableExecuteCommand, "
                  f"containers[?name=='{CONTAINER}'] | [0].runtimeId, "
                  f"containers[?name=='{CONTAINER}'] | [0].managedAgents[?name=='ExecuteCommandAgent'] | [0].lastStatus]")


def parse(argv: list[str]) -> tuple[str, str, dict]:
    words: list[str] = []
    options: dict = {}
    index = 0
    while index < len(argv):
        token = argv[index]
        if token.startswith("--"):
            name = token[2:]
            if name in options:
                raise Unexpected(f"{token} given twice")
            values = []
            index += 1
            while index < len(argv) and not argv[index].startswith("--"):
                values.append(argv[index])
                index += 1
                if name not in MULTI_VALUED:
                    break
            if not values:
                raise Unexpected(f"{token} has no value")
            options[name] = values if name in MULTI_VALUED else values[0]
        else:
            words.append(token)
            index += 1
    if len(words) != 2:
        raise Unexpected(f"expected a service and a command, got {words}")
    service, command = words
    if (service, command) not in COMMANDS:
        raise Unexpected(f"aws {service} {command} is not a call tunnel.sh makes")
    required, optional = COMMANDS[(service, command)]
    missing = required - options.keys()
    unknown = options.keys() - required - optional - GLOBAL_OPTIONS
    if missing or unknown:
        raise Unexpected(f"aws {service} {command}: missing {sorted(missing)}, unknown {sorted(unknown)}")
    return service, command, options


def text(value) -> str:
    """--output text as awscli/text.py writes a query's result: a list of scalars tab-separated, a
    list of lists one line each, an empty list as nothing, Python's str() for True, False and None."""
    if isinstance(value, list):
        if not value:
            return ""
        if all(isinstance(element, list) for element in value):
            return "".join(text(element) for element in value)
        return "\t".join(str(element) for element in value) + "\n"
    return f"{value}\n"


def gateway_of(entry: dict) -> dict | None:
    return next((c for c in entry["containers"] if c["name"] == CONTAINER), None)


def answer(world: dict, service: str, command: str, options: dict):
    if command in world.get("errors", {}):
        raise ServiceError(*world["errors"][command])
    if command in ("list-tasks", "describe-tasks"):
        cluster = world["clusters"].get(options["cluster"])
        if cluster is None:
            # https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_ListTasks.html#API_ListTasks_Errors
            raise ServiceError("ClusterNotFoundException", "Cluster not found.")
    if command == "list-tasks":
        if options.get("query") != TASKS_QUERY or options["desired-status"] != "RUNNING":
            raise Unexpected(f"list-tasks {options}")
        tasks = cluster.get(options["service-name"])
        if tasks is None:
            raise ServiceError("ServiceNotFoundException", "Service not found.")
        return [entry["taskArn"] for entry in tasks if entry["desiredStatus"] == "RUNNING"]
    if command == "describe-tasks":
        if options.get("query") != DESCRIBE_QUERY:
            raise Unexpected(f"--query {options.get('query')!r}")
        known = {entry["taskArn"]: entry for tasks in cluster.values() for entry in tasks}
        rows = []
        for arn in options["tasks"]:
            entry = known.get(arn)
            if entry is None:
                continue
            gateway = gateway_of(entry)
            agent = None
            if gateway is not None:
                agent = next((a["lastStatus"] for a in gateway.get("managedAgents", [])
                              if a["name"] == "ExecuteCommandAgent"), None)
            rows.append([entry["taskArn"], entry["lastStatus"], entry["enableExecuteCommand"],
                         gateway["runtimeId"] if gateway else None, agent])
        return rows
    if command == "start-session":
        return None
    raise Unexpected(command)


def aws_double(argv: list[str], case: pathlib.Path) -> int:
    world = json.loads((case / "world.json").read_text())
    record = {"argv": argv, "profile": os.environ.get("AWS_PROFILE")}
    status = 0
    try:
        service, command, options = parse(argv)
        record.update(service=service, command=command, options=options)
        if options.get("region") != world["region"]:
            raise Unexpected(f"--region {options.get('region')!r}, not the case's {world['region']}")
        if command != "start-session" and options.get("output") != "text":
            raise Unexpected("--output text is what the script reads, whatever the profile says")
        value = answer(world, service, command, options)
        record["outcome"] = "success"
        if command == "start-session":
            # What the CLI prints once the plugin has the session, and the plugin's own lines.
            sys.stdout.write("\nStarting session with SessionId: operator-0123456789abcdef0\n"
                             f"Port {json.loads(options['parameters'])['localPortNumber'][0]} opened for sessionId "
                             "operator-0123456789abcdef0.\nWaiting for connections...\n")
            # A line on the session's stderr in no shape the CLI's errors take, for the one case that
            # holds tunnel.sh's filter to what it does with such a line.
            sys.stderr.write(world.get("start_session_stderr", ""))
            if "ctrl_c_then_stderr" in world:
                interrupted(world["ctrl_c_then_stderr"])
            status = world["start_session_status"]
        else:
            sys.stdout.write(text(value))
    except ServiceError as error:
        record["outcome"] = error.code
        sys.stderr.write(f"\nAn error occurred ({error.code}) when calling the "
                         f"{OPERATIONS.get(record.get('command', ''), 'Unknown')} operation: {error.message}\n")
        status = 254
    except Unexpected as error:
        record["outcome"] = "unexpected"
        record["unexpected"] = str(error)
        sys.stderr.write(f"aws double: unexpected call: {error}\n")
        status = 97
    with open(case / "aws-calls.jsonl", "a") as log:
        log.write(json.dumps(record) + "\n")
    return status


def interrupted(last_line: str):
    """Ctrl-C at a terminal: SIGINT to the whole foreground process group, which is the session (the
    CLI and the plugin) and tunnel.sh's stderr filter alike. The session ignores it, as the CLI does
    while the plugin holds a session, and the plugin prints its last line after it; the case runs
    the script in a process group of its own, so nothing outside the case receives the signal.
    https://www.gnu.org/software/bash/manual/html_node/Signals.html"""
    import signal
    import time
    signal.signal(signal.SIGINT, signal.SIG_IGN)
    sys.stderr.flush()
    os.killpg(os.getpgrp(), signal.SIGINT)
    # Long enough for an unprotected filter to have died of the signal before the line reaches it.
    time.sleep(0.3)
    sys.stderr.write(last_line)
    sys.stderr.flush()


def plugin_double(argv: list[str], case: pathlib.Path) -> int:
    """session-manager-plugin with no arguments prints that it is installed; the CLI starts it with
    a session's arguments, which the aws double stands in for, so nothing else reaches here."""
    if argv in ([], ["--version"]):
        sys.stdout.write("1.2.835.0\n" if argv else "\nThe Session Manager plugin was installed successfully.\n")
        return 0
    sys.stderr.write(f"session-manager-plugin double: unexpected call: {argv}\n")
    return 97


def curl_double(argv: list[str], case: pathlib.Path) -> int:
    """curl, answering from the case's curl.json: the exit status, and on success the --write-out
    fields. The exit codes are curl's own: https://curl.se/docs/manpage.html#EXIT"""
    world = json.loads((case / "curl.json").read_text())
    record = {"argv": argv, "env": {name: os.environ.get(name) for name in
                                    ("CURL_CA_BUNDLE", "SSL_CERT_FILE", "SSL_CERT_DIR", "CURL_HOME")}}
    with open(case / "curl-calls.jsonl", "a") as log:
        log.write(json.dumps(record) + "\n")
    messages = {6: "Could not resolve host", 7: "Failed to connect", 28: "Operation timed out",
                35: "SSL connect error", 52: "Empty reply from server", 56: "Recv failure: Connection reset by peer",
                60: "SSL certificate problem: unable to get local issuer certificate"}
    status = world["status"]
    if status != 0:
        sys.stderr.write(f"curl: ({status}) {messages.get(status, 'error')}\n")
        return status
    template = argv[argv.index("--write-out") + 1] if "--write-out" in argv else ""
    fields = {"%{http_code}": str(world["http_code"]), "%{remote_ip}": world["remote_ip"],
              "%{ssl_verify_result}": str(world["ssl_verify_result"])}
    for name, value in fields.items():
        template = template.replace(name, value)
    sys.stdout.write(template)
    return 0


DOUBLES = {"aws": aws_double, "session-manager-plugin": plugin_double, "curl": curl_double}

if __name__ == "__main__" and sys.argv[1:2] == ["--double"]:
    sys.exit(DOUBLES[sys.argv[2]](sys.argv[4:], pathlib.Path(sys.argv[3])))

import shlex  # noqa: E402 -- below the doubles, which start once per call
import shutil  # noqa: E402
import socket  # noqa: E402
import subprocess  # noqa: E402
import tempfile  # noqa: E402
import unittest  # noqa: E402


# --- a case ----------------------------------------------------------------------------------------

class Result:
    def __init__(self, completed: subprocess.CompletedProcess):
        self.status = completed.returncode
        self.stdout = completed.stdout
        self.stderr = completed.stderr
        self.output = completed.stdout + completed.stderr


def free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


class Case:
    """One throwaway directory: a copy of the scripts in a repository tree of its own, a bin holding
    the doubles and a way to each real tool, which is the case's whole PATH, and the case's world
    beside them."""

    def __init__(self, test: unittest.TestCase, *, world: dict | None = None,
                 doubles: tuple[str, ...] = tuple(DOUBLES), real: tuple[str, ...] = ()):
        self.test = test
        scratch = tempfile.TemporaryDirectory()
        test.addCleanup(scratch.cleanup)
        self.dir = pathlib.Path(scratch.name).resolve()
        self.bin = self.dir / "bin"
        self.home = self.dir / "home"
        self.repo = self.dir / "repo"
        for directory in (self.bin, self.home, self.repo / "tools" / "scripts", self.repo / OUTPUTS.parent):
            directory.mkdir(parents=True)
        # The scripts find the repository from their own path, so the outputs document a case writes
        # is the copy's, never this checkout's.
        for name in SCRIPT_FILES:
            shutil.copy2(ROOT / "tools" / "scripts" / name, self.repo / "tools" / "scripts" / name)
        self.write_executable("python3", f'#!/bin/sh\nexec {shlex.quote(os.path.realpath(sys.executable))} "$@"\n')
        for tool in TOOLS + real:
            found = shutil.which(tool)
            test.assertIsNotNone(found, f"{tool} is required to run this companion and is not on PATH")
            self.write_executable(tool, f'#!/bin/sh\nexec {shlex.quote(os.path.realpath(found))} "$@"\n')
        for name in doubles:
            if name in real:
                continue
            self.write_executable(name, (
                f"#!/bin/sh\nexec {shlex.quote(os.path.realpath(sys.executable))} -I -S "
                f"{shlex.quote(str(pathlib.Path(__file__).resolve()))} --double {name} "
                f"{shlex.quote(str(self.dir))} \"$@\"\n"))
        self.world = world if world is not None else default_world()
        self.save()
        self.curl({"status": 0, "http_code": 200, "remote_ip": "127.0.0.1", "ssl_verify_result": 0})

    def write_executable(self, name: str, content: str):
        path = self.bin / name
        path.write_text(content)
        path.chmod(0o755)

    def remove(self, name: str):
        (self.bin / name).unlink()

    def save(self):
        (self.dir / "world.json").write_text(json.dumps(self.world, indent=1))
        document = self.repo / OUTPUTS
        if self.world.get("outputs") is None:
            document.unlink(missing_ok=True)
        else:
            document.write_text(json.dumps(outputs_document(self.world["outputs"]), indent=2))

    def curl(self, answer: dict):
        (self.dir / "curl.json").write_text(json.dumps(answer))

    def env(self, **extra: str | None) -> dict[str, str]:
        env = {"PATH": str(self.bin), "HOME": str(self.home), "LC_ALL": "C",
               "AWS_PROFILE": "spa-platform", "AWS_REGION": REGION, "TMPDIR": str(self.dir)}
        env.update(extra)
        return {name: value for name, value in env.items() if value is not None}

    def run(self, script: pathlib.Path, *args: str, env: dict[str, str] | None = None,
            own_group: bool = False) -> Result:
        """own_group runs the script as the leader of a process group of its own, so a signal the
        case sends to the group, as a terminal's Ctrl-C does, never reaches this test runner."""
        copy = self.repo / script.relative_to(ROOT)
        completed = subprocess.run([BASH, str(copy), *args], cwd=self.dir, env=env or self.env(),
                                   stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=60,
                                   start_new_session=own_group)
        result = Result(completed)
        for secret in SECRETS:
            self.test.assertNotIn(secret, result.output, "the script printed the account id")
        return result

    def aws_calls(self) -> list[dict]:
        path = self.dir / "aws-calls.jsonl"
        return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []

    def start_sessions(self) -> list[dict]:
        return [call for call in self.aws_calls() if call.get("command") == "start-session"]

    def curl_calls(self) -> list[dict]:
        path = self.dir / "curl-calls.jsonl"
        return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []


def setUpModule():
    full = subprocess.run([BASH, "-c", 'printf "%s" "$BASH_VERSION"'], env={"PATH": "/usr/bin:/bin"},
                          capture_output=True, text=True, check=True).stdout
    print(f"tunnel_tests: the scripts run under {BASH} {full}", file=sys.stderr)


class CompanionTestCase(unittest.TestCase):
    def assertNoUnexpectedCall(self, case: Case):
        unexpected = [call for call in case.aws_calls() if call.get("outcome") == "unexpected"]
        self.assertEqual([], unexpected, "the script made an AWS call the double does not answer")


# --- tunnel.sh -------------------------------------------------------------------------------------

class TunnelForwardsThroughTheTaskToTheAlb(CompanionTestCase):
    """docs/plan/05-infrastructure-and-cost.md, Workstation access: "tunnel.sh finds the running task
    and forwards local port 8443 to the ALB on 443", with the target and document its block shows."""

    def test_it_starts_a_port_forward_to_the_alb_through_the_gateway_container(self):
        case = Case(self)
        result = case.run(TUNNEL, "--local-port", str(free_port()))
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        sessions = case.start_sessions()
        self.assertEqual(1, len(sessions), result.output)
        options = sessions[0]["options"]
        # ecs:<cluster>_<task id>_<container runtime id>: the target form for an ECS container,
        # https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager-working-with-sessions-start.html
        self.assertEqual(f"ecs:{CLUSTER}_0123456789abcdef0123456789abcdef_0123456789abcdef0123456789abcdef-2222222222",
                         options["target"])
        self.assertEqual("AWS-StartPortForwardingSessionToRemoteHost", options["document-name"])
        parameters = json.loads(options["parameters"])
        self.assertEqual([ALB_DNS], parameters["host"])
        self.assertEqual(["443"], parameters["portNumber"])
        self.assertEqual(REGION, options["region"])

    def test_the_local_port_is_8443_unless_given(self):
        case = Case(self)
        result = case.run(TUNNEL)
        # No skip when this machine holds 8443: the case fails and says why, since a skip would be
        # a pass that checked nothing (CODING_STANDARDS.md). Stop tunnel.sh and run it again.
        self.assertEqual(0, result.status, result.output)
        self.assertEqual(["8443"], json.loads(case.start_sessions()[0]["options"]["parameters"])["localPortNumber"])


    def test_with_two_running_tasks_the_first_reachable_is_the_hop_and_one_session_starts(self):
        # The two-task cell (#347, P2-4): both tasks run, both reach the agent, and the tunnel's hop is
        # a gateway task as before, the first one; a roll replaces it and the tunnel is restarted
        # (#349 Q20). No claim here that a session survives its hop's drain.
        world = default_world()
        world["clusters"][CLUSTER][SERVICE] = [
            task("aaaa0000aaaa0000aaaa0000aaaa0000"),
            task("bbbb0000bbbb0000bbbb0000bbbb0000"),
        ]
        case = Case(self, world=world)
        result = case.run(TUNNEL, "--local-port", str(free_port()))
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        sessions = case.start_sessions()
        self.assertEqual(1, len(sessions))
        self.assertEqual(f"ecs:{CLUSTER}_aaaa0000aaaa0000aaaa0000aaaa0000_aaaa0000aaaa0000aaaa0000aaaa0000-2222222222",
                         sessions[0]["options"]["target"])

    def test_it_takes_the_first_task_a_session_can_reach(self):
        world = default_world()
        world["clusters"][CLUSTER][SERVICE] = [
            task("aaaa0000aaaa0000aaaa0000aaaa0000", last_status="PENDING"),
            task("bbbb0000bbbb0000bbbb0000bbbb0000", exec_on=False),
            task("cccc0000cccc0000cccc0000cccc0000", agent="PENDING"),
            task("dddd0000dddd0000dddd0000dddd0000"),
        ]
        case = Case(self, world=world)
        result = case.run(TUNNEL, "--local-port", str(free_port()))
        self.assertEqual(0, result.status, result.output)
        self.assertEqual(f"ecs:{CLUSTER}_dddd0000dddd0000dddd0000dddd0000_dddd0000dddd0000dddd0000dddd0000-2222222222",
                         case.start_sessions()[0]["options"]["target"])
        self.assertIn("dddd0000dddd0000dddd0000dddd0000", result.stdout)

    def test_the_hop_is_the_gateway_container_never_the_mock_api(self):
        world = default_world()
        # A task whose gateway container is gone: the mock API's agent still runs, and is not used.
        world["clusters"][CLUSTER][SERVICE] = [task("eeee0000eeee0000eeee0000eeee0000", gateway=False)]
        case = Case(self, world=world)
        result = case.run(TUNNEL, "--local-port", str(free_port()))
        self.assertEqual(1, result.status, result.output)
        self.assertEqual([], case.start_sessions())
        self.assertIn(f"no running {CONTAINER} container", result.stderr)

    def test_the_session_s_own_exit_status_is_the_script_s(self):
        world = default_world()
        world["start_session_status"] = 255
        case = Case(self, world=world)
        result = case.run(TUNNEL, "--local-port", str(free_port()))
        self.assertEqual(255, result.status, result.output)

    def test_a_session_denied_names_its_code_and_never_the_principal(self):
        # StartSession's denial is the CLI's own, printed after tunnel.sh has handed over to it, and
        # its message names the caller's ARN as the list-tasks denial below does: "User:
        # arn:aws:iam::123456789012:user/mateojackson is not authorized to perform: ...",
        # https://docs.aws.amazon.com/IAM/latest/UserGuide/troubleshoot_access-denied.html
        # Case.run holds the account-id universal; this is the case where the session's stderr can
        # fail it.
        world = default_world()
        world["errors"] = {"start-session": [
            "AccessDeniedException",
            f"User: arn:aws:sts::{ACCOUNT}:assumed-role/spa-platform-operator/operator is not authorized to "
            f"perform: ssm:StartSession on resource: arn:aws:ecs:{REGION}:{ACCOUNT}:task/{CLUSTER}/"
            "0123456789abcdef0123456789abcdef because no identity-based policy allows the ssm:StartSession action"]}
        case = Case(self, world=world)
        result = case.run(TUNNEL, "--local-port", str(free_port()))
        # The CLI's status for a service error (`aws help return-codes`), passed on.
        self.assertEqual(254, result.status, result.output)
        self.assertEqual(1, len(case.start_sessions()), result.output)
        # The code alone, on a line of its own, as tunnel.sh's header promises: the account mask
        # would pass the universal by itself, and leave the operator's role and the policy text in
        # the line (#191).
        self.assertIn("tunnel: the session through task 0123456789abcdef0123456789abcdef: AccessDeniedException",
                      result.stderr.splitlines())
        for principal in ("is not authorized", "assumed-role", "spa-platform-operator", "arn:aws:"):
            self.assertNotIn(principal, result.stderr)

    def test_any_other_line_on_the_session_s_stderr_reaches_it_with_the_account_masked(self):
        # Not a line the CLI or the plugin is known to print: whatever else reaches the session's
        # stderr is passed on, so the operator still reads it, with any twelve-digit run masked.
        world = default_world()
        world["start_session_stderr"] = (
            f"plugin: a warning about arn:aws:ecs:{REGION}:{ACCOUNT}:task/{CLUSTER}/0123456789abcdef0123456789abcdef\n")
        case = Case(self, world=world)
        result = case.run(TUNNEL, "--local-port", str(free_port()))
        self.assertEqual(0, result.status, result.output)
        self.assertIn(f"plugin: a warning about arn:aws:ecs:{REGION}:", result.stderr)
        self.assertIn(f":task/{CLUSTER}/0123456789abcdef0123456789abcdef", result.stderr)
        # And the session's stdout, the plugin's progress, is not filtered at all.
        self.assertIn("Waiting for connections...", result.stdout)

    def test_ctrl_c_leaves_the_filter_to_pass_the_session_s_last_line(self):
        # #505 (#186's S9). Ctrl-C reaches the whole foreground group, the filter among it. The
        # filter ignores it, so the line the plugin prints after it still reaches the terminal, and
        # through the filter: a denial as its code, any other line with the account masked. Its
        # trap '' INT states what bash already does for a process substitution in a script, which
        # runs without job control: "asynchronous commands ignore SIGINT"
        # (https://www.gnu.org/software/bash/manual/html_node/Signals.html). So this case holds the
        # behaviour, not the trap line: it turns red if the filter is ever started in a way that
        # takes the signal, or stops passing a line that arrives after it.
        world = default_world()
        world["ctrl_c_then_stderr"] = (
            f"plugin: closing the session on arn:aws:ecs:{REGION}:{ACCOUNT}:task/{CLUSTER}/0123456789abcdef0123456789abcdef\n"
            "\nAn error occurred (TargetNotConnected) when calling the TerminateSession operation: "
            f"arn:aws:sts::{ACCOUNT}:assumed-role/spa-platform-operator/operator\n")
        case = Case(self, world=world)
        result = case.run(TUNNEL, "--local-port", str(free_port()), own_group=True)
        self.assertEqual(0, result.status, result.output)
        lines = result.stderr.splitlines()
        self.assertIn(f"plugin: closing the session on arn:aws:ecs:{REGION}:<account id>:task/{CLUSTER}/"
                      "0123456789abcdef0123456789abcdef", lines)
        # The filter's last line is the code, the last thing the session printed, and nothing after it.
        self.assertEqual("tunnel: the session through task 0123456789abcdef0123456789abcdef: TargetNotConnected",
                         lines[-1])
        for principal in ("assumed-role", "spa-platform-operator", "arn:aws:sts"):
            self.assertNotIn(principal, result.stderr)

    def test_the_header_says_the_filter_s_last_line_can_follow_the_prompt(self):
        # The session is exec'd, so nothing is left to wait for the filter: once the CLI exits, the
        # shell's prompt can print before the filter's last line. The header says so (#505).
        case = Case(self)
        result = case.run(TUNNEL, "--help")
        self.assertEqual(0, result.status, result.output)
        self.assertIn("after the shell's prompt", " ".join(result.stdout.split()))


class TunnelReadsItsNamesFromTheOutputsDocument(CompanionTestCase):
    """#198: the cluster, the service and the load balancer's DNS name come from
    infra/envs/poc/outputs.json (contracts/infra-outputs.schema.json: "The scripts and the deployed
    tests read it"), never from AWS by name and never from Terraform state."""

    def test_the_names_are_the_document_s_even_where_the_module_s_convention_would_guess_others(self):
        # Names no convention gives: spa-<cell> and gateway would find nothing here, so a tunnel that
        # went back to building or looking up its names fails this case.
        cluster, service = "documented-cluster", "documented-service"
        alb = f"internal-documented-lb-42.{REGION}.elb.amazonaws.com"
        world = default_world()
        world["outputs"].update(ecsCluster=cluster, ecsService=service, albDnsName=alb)
        world["clusters"] = {cluster: {service: [task("abab0000abab0000abab0000abab0000")]}}
        case = Case(self, world=world)
        result = case.run(TUNNEL, "--local-port", str(free_port()))
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        options = case.start_sessions()[0]["options"]
        self.assertEqual(f"ecs:{cluster}_abab0000abab0000abab0000abab0000_abab0000abab0000abab0000abab0000-2222222222",
                         options["target"])
        self.assertEqual([alb], json.loads(options["parameters"])["host"])
        for call in case.aws_calls():
            if call.get("command") in ("list-tasks", "describe-tasks"):
                self.assertEqual(cluster, call["options"]["cluster"])
        self.assertEqual([service], [c["options"]["service-name"] for c in case.aws_calls()
                                     if c.get("command") == "list-tasks"])

    def test_it_asks_aws_for_tasks_and_the_session_and_for_no_name(self):
        case = Case(self)
        result = case.run(TUNNEL, "--local-port", str(free_port()))
        self.assertEqual(0, result.status, result.output)
        self.assertEqual([("ecs", "list-tasks"), ("ecs", "describe-tasks"), ("ssm", "start-session")],
                         [(c.get("service"), c.get("command")) for c in case.aws_calls()])

    def test_the_header_says_where_the_names_come_from(self):
        case = Case(self)
        result = case.run(TUNNEL, "--help")
        self.assertEqual(0, result.status, result.output)
        self.assertIn(str(OUTPUTS), result.stdout)
        for name in ("ecsCluster", "ecsService", "albDnsName"):
            self.assertIn(name, result.stdout)
        self.assertEqual([], case.aws_calls())

    def test_the_module_s_load_balancer_is_internal(self):
        # tunnel.sh no longer reads the scheme from AWS; the document's albDnsName is the module's
        # load balancer, and the module makes it internal (task 1.3).
        self.assertRegex(ALB_TF.read_text(), r"(?m)^\s*internal\s*=\s*true\s*$")


class TunnelRefusesWithoutTheOutputsDocument(CompanionTestCase):
    """The document absent, for another Region, or short of a name: a refusal that names it, exit 2,
    and no AWS call."""

    def refused(self, case: Case, fragment: str, *args: str) -> Result:
        result = case.run(TUNNEL, "--local-port", str(free_port()), *args)
        self.assertEqual(2, result.status, result.output)
        self.assertIn(fragment, result.stderr)
        self.assertEqual([], case.aws_calls())
        return result

    def test_no_document(self):
        world = default_world()
        world["outputs"] = None
        result = self.refused(Case(self, world=world), f"there is no {OUTPUTS}")
        self.assertIn("tools/scripts/seed.sh", result.stderr)

    def test_a_document_for_another_region(self):
        world = default_world()
        world["outputs"]["region"] = "eu-west-1"
        self.refused(Case(self, world=world), "not for AWS_REGION")

    def test_a_document_without_each_name(self):
        for name in ("ecsCluster", "ecsService", "albDnsName"):
            with self.subTest(field=name):
                world = default_world()
                world["outputs"][name] = None
                self.refused(Case(self, world=world), f"gives no {name}")

    def test_no_option_steps_round_the_document(self):
        for option in ("--cell", "--cluster", "--service", "--alb"):
            with self.subTest(option=option):
                self.refused(Case(self), "unknown argument", option, "anything")


class TunnelRefusesBeforeASession(CompanionTestCase):
    """Each condition the plan names for the route, and each prerequisite, stops the script before
    it starts a session, with a reason and the status its header gives."""

    def refused(self, case: Case, status: int, fragment: str, *args: str, env: dict | None = None) -> Result:
        result = case.run(TUNNEL, "--local-port", str(free_port()), *args, env=env)
        self.assertEqual(status, result.status, result.output)
        self.assertEqual([], case.start_sessions(), "a session started where it must not")
        self.assertIn(fragment, result.stderr)
        self.assertNoUnexpectedCall(case)
        return result

    def test_no_running_task(self):
        world = default_world()
        world["clusters"][CLUSTER][SERVICE] = []
        self.refused(Case(self, world=world), 1, "has no running task")

    def test_ecs_exec_off(self):
        world = default_world()
        world["clusters"][CLUSTER][SERVICE] = [task("1111000011110000111100001111000a", exec_on=False)]
        self.refused(Case(self, world=world), 1, "ECS Exec off")

    def test_the_exec_agent_not_running(self):
        world = default_world()
        world["clusters"][CLUSTER][SERVICE] = [task("1111000011110000111100001111000b", agent="STOPPED")]
        self.refused(Case(self, world=world), 1, "ExecuteCommandAgent is STOPPED")

    def test_an_access_denial_names_its_code_and_never_the_principal(self):
        # An access denial's message names the caller's ARN, which carries the account id: "User:
        # arn:aws:iam::123456789012:user/mateojackson is not authorized to perform: ...",
        # https://docs.aws.amazon.com/IAM/latest/UserGuide/troubleshoot_access-denied.html
        # This is the case where the universal that no output names the account can fail.
        world = default_world()
        world["errors"] = {"list-tasks": [
            "AccessDeniedException",
            f"User: arn:aws:sts::{ACCOUNT}:assumed-role/spa-platform-operator/operator is not authorized to "
            f"perform: ecs:ListTasks on resource: arn:aws:ecs:{REGION}:{ACCOUNT}:container-instance/{CLUSTER}/* "
            "because no identity-based policy allows the ecs:ListTasks action"]}
        self.refused(Case(self, world=world), 1, "AccessDeniedException")

    def test_no_cluster(self):
        world = default_world()
        world["clusters"] = {}
        self.refused(Case(self, world=world), 1, "ClusterNotFoundException")

    def test_no_session_manager_plugin(self):
        case = Case(self)
        case.remove("session-manager-plugin")
        self.refused(case, 2, "brew install --cask session-manager-plugin")
        self.assertEqual([], case.aws_calls())

    def test_no_aws_cli(self):
        case = Case(self)
        case.remove("aws")
        self.refused(case, 2, "brew install awscli")

    def test_no_region(self):
        case = Case(self)
        self.refused(case, 2, "no Region", env=case.env(AWS_REGION=None, AWS_DEFAULT_REGION=None))
        self.assertEqual([], case.aws_calls())

    def test_the_local_port_already_held(self):
        case = Case(self)
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as held:
            held.bind(("127.0.0.1", 0))
            held.listen(1)
            port = held.getsockname()[1]
            result = case.run(TUNNEL, "--local-port", str(port))
        self.assertEqual(2, result.status, result.output)
        self.assertIn("already listens", result.stderr)
        self.assertEqual([], case.aws_calls())

    def test_a_port_that_is_not_a_number(self):
        case = Case(self)
        result = case.run(TUNNEL, "--local-port", "84x3")
        self.assertEqual(2, result.status, result.output)
        self.assertEqual([], case.aws_calls())


# --- verify-tunnel.sh ------------------------------------------------------------------------------

DOMAIN = "spa-poc.test"
HOST = f"app1.{DOMAIN}"


class VerifyTunnelChecksTheHealthPathOverTrustedTls(CompanionTestCase):
    """docs/plan/05-infrastructure-and-cost.md, Workstation access, step 4: verify-tunnel.sh requests
    the health path, https://app1.spa-poc.test:8443/healthz once the gateway runs, and fails unless
    TLS validates against the local authority. The path is a parameter: / on the sample image."""

    def curl_argv(self, case: Case) -> list[str]:
        calls = case.curl_calls()
        self.assertEqual(1, len(calls), "expected one request")
        return calls[0]["argv"]

    def test_the_health_path_answering_200_over_the_tunnel_passes(self):
        case = Case(self)
        result = case.run(VERIFY, "/healthz")
        self.assertEqual(0, result.status, result.output)
        self.assertEqual(f"https://{HOST}:8443/healthz", self.curl_argv(case)[-1])
        self.assertIn(f"https://{HOST}:8443/healthz", result.stdout)

    def test_the_path_is_a_parameter(self):
        case = Case(self)
        result = case.run(VERIFY, "/")
        self.assertEqual(0, result.status, result.output)
        self.assertEqual(f"https://{HOST}:8443/", self.curl_argv(case)[-1])

    def test_host_and_port_can_be_given(self):
        case = Case(self)
        result = case.run(VERIFY, "/healthz", "--host", f"app2.{DOMAIN}", "--port", "9443")
        self.assertEqual(0, result.status, result.output)
        self.assertEqual(f"https://app2.{DOMAIN}:9443/healthz", self.curl_argv(case)[-1])

    def test_trust_is_the_workstation_s_and_nothing_steps_round_it(self):
        """No -k, no CA file of its own, no curlrc and no CA bundle from the environment: the
        question is whether this workstation's trust store accepts the certificate (certs.sh verify
        asks it the same way)."""
        case = Case(self)
        env = case.env(CURL_CA_BUNDLE="/elsewhere/ca.pem", SSL_CERT_FILE="/elsewhere/ca.pem",
                       SSL_CERT_DIR="/elsewhere", CURL_HOME=str(case.home))
        result = case.run(VERIFY, "/healthz", env=env)
        self.assertEqual(0, result.status, result.output)
        argv = self.curl_argv(case)
        # -q first: curl reads ~/.curlrc unless the first argument is -q, and a curlrc may say
        # insecure. https://curl.se/docs/manpage.html#-q
        self.assertEqual("-q", argv[0])
        for flag in ("-k", "--insecure", "--cacert", "--capath", "--ca-native", "--proxy-insecure"):
            self.assertNotIn(flag, argv)
        self.assertEqual({"CURL_CA_BUNDLE": None, "SSL_CERT_FILE": None, "SSL_CERT_DIR": None, "CURL_HOME": None},
                         case.curl_calls()[0]["env"])

    def test_no_proxy_stands_between_it_and_the_tunnel(self):
        case = Case(self)
        result = case.run(VERIFY, "/healthz", env=case.env(https_proxy="http://proxy.example:3128",
                                                          HTTPS_PROXY="http://proxy.example:3128"))
        self.assertEqual(0, result.status, result.output)
        argv = self.curl_argv(case)
        self.assertEqual("*", argv[argv.index("--noproxy") + 1])

    def test_it_never_falls_back_to_plain_http(self):
        case = Case(self)
        case.run(VERIFY, "/healthz")
        argv = self.curl_argv(case)
        self.assertEqual("=https", argv[argv.index("--proto") + 1])

    def test_it_follows_no_redirect(self):
        case = Case(self)
        case.run(VERIFY, "/healthz")
        argv = self.curl_argv(case)
        self.assertNotIn("-L", argv)
        self.assertNotIn("--location", argv)


class VerifyTunnelFailsAndSaysWhy(CompanionTestCase):
    """Each way the request can fail is exit 1 with the reason a person acts on. Risk 15 in
    docs/plan/06-phase-2-and-risks.md: verify-tunnel.sh detects a dead tunnel."""

    def failed(self, answer: dict, fragment: str, *args: str) -> Result:
        case = Case(self)
        case.curl(answer)
        result = case.run(VERIFY, *(args or ("/healthz",)))
        self.assertEqual(1, result.status, result.output)
        self.assertIn(fragment, result.stderr)
        return result

    def test_nothing_listening_means_no_tunnel(self):
        self.failed({"status": 7}, "start tools/scripts/tunnel.sh")

    def test_an_untrusted_certificate(self):
        self.failed({"status": 60}, "does not trust")

    def test_a_dead_session(self):
        for status in (35, 52, 56, 28):
            with self.subTest(curl=status):
                self.failed({"status": status}, "the session has ended or the task is gone")

    def test_an_unresolved_host(self):
        self.failed({"status": 6}, f"127.0.0.1 app1.{DOMAIN} app2.{DOMAIN} app3.{DOMAIN}")

    def test_a_status_other_than_200(self):
        self.failed({"status": 0, "http_code": 404, "remote_ip": "127.0.0.1", "ssl_verify_result": 0},
                    "answered 404")

    def test_a_verification_result_other_than_0(self):
        # curl reports the TLS library's verification result beside its own exit status; a pass
        # needs both. https://curl.se/docs/manpage.html#-w
        self.failed({"status": 0, "http_code": 200, "remote_ip": "127.0.0.1", "ssl_verify_result": 20},
                    "TLS verification reported 20")

    def test_a_request_that_did_not_go_through_the_tunnel(self):
        self.failed({"status": 0, "http_code": 200, "remote_ip": "10.0.0.7", "ssl_verify_result": 0},
                    "not through the tunnel")

    def test_the_ipv6_loopback_is_the_tunnel_too(self):
        case = Case(self)
        case.curl({"status": 0, "http_code": 200, "remote_ip": "::1", "ssl_verify_result": 0})
        self.assertEqual(0, case.run(VERIFY, "/healthz").status)


class VerifyTunnelRefusesToStart(CompanionTestCase):
    def refused(self, *args: str, case: Case | None = None) -> Result:
        case = case or Case(self)
        result = case.run(VERIFY, *args)
        self.assertEqual(2, result.status, result.output)
        self.assertEqual([], case.curl_calls())
        return result

    def test_without_a_path(self):
        self.assertIn("path", self.refused().stderr)

    def test_a_path_that_is_not_absolute(self):
        self.refused("healthz")

    def test_a_path_with_a_query_or_a_space(self):
        self.refused("/healthz?x=1")
        self.refused("/a b")

    def test_without_curl(self):
        case = Case(self)
        case.remove("curl")
        self.refused("/healthz", case=case)


class VerifyTunnelAgainstTheRealCurl(CompanionTestCase):
    """The real curl, so the trust decision is the workstation's own and not the double's."""

    def real_case(self) -> Case:
        return Case(self, doubles=(), real=("curl",))

    def test_nothing_listening_on_the_port(self):
        case = self.real_case()
        result = case.run(VERIFY, "/healthz", "--host", "127.0.0.1", "--port", str(free_port()))
        self.assertEqual(1, result.status, result.output)
        self.assertIn("start tools/scripts/tunnel.sh", result.stderr)

    def test_a_certificate_no_trust_store_holds(self):
        import ssl
        import threading
        case = self.real_case()
        openssl = shutil.which("openssl")
        self.assertIsNotNone(openssl, "openssl is required to run this companion and is not on PATH")
        key, cert = case.dir / "key.pem", case.dir / "cert.pem"
        subprocess.run([openssl, "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                        "-subj", "/CN=127.0.0.1", "-keyout", str(key), "-out", str(cert)],
                       check=True, capture_output=True)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(str(cert), str(key))
        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        listener.bind(("127.0.0.1", 0))
        listener.listen(4)
        listener.settimeout(20)
        self.addCleanup(listener.close)
        handshakes = []

        def serve():
            try:
                connection, _ = listener.accept()
            except OSError:
                return
            with connection:
                try:
                    with context.wrap_socket(connection, server_side=True) as tls:
                        tls.sendall(b"HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok")
                        handshakes.append("completed")
                except (ssl.SSLError, OSError):
                    handshakes.append("refused by the client")

        server = threading.Thread(target=serve, daemon=True)
        server.start()
        result = case.run(VERIFY, "/healthz", "--host", "127.0.0.1", "--port", str(listener.getsockname()[1]))
        server.join(20)
        self.assertEqual(1, result.status, result.output)
        self.assertIn("does not trust", result.stderr)
        self.assertEqual(["refused by the client"], handshakes)


def main() -> int:
    required = os.environ.get(REQUIRE_BASH)
    if required:
        version = subprocess.run([BASH, "-c", 'printf "%s.%s" "${BASH_VERSINFO[0]}" "${BASH_VERSINFO[1]}"'],
                                 env={"PATH": "/usr/bin:/bin"}, capture_output=True, text=True, check=True).stdout
        if version != required:
            print(f"tunnel_tests: {REQUIRE_BASH}={required}, but {BASH} is {version}", file=sys.stderr)
            return 1
    program = unittest.main(exit=False)
    return 0 if program.result.wasSuccessful() else 1


if __name__ == "__main__":
    sys.exit(main())
