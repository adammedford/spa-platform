#!/usr/bin/env python3
"""The doubles of tools/bench/fargate_tests.py (#305): `aws` and `session-manager-plugin`, and the
world they answer from. Owner: the owner OWNERSHIP.md names for tools/bench/. Not a companion
itself: fargate_tests.py loads it by path for the world, and each case's bin/aws and
bin/session-manager-plugin run it as

    python3 -I tools/bench/fargate_doubles.py <aws|session-manager-plugin> <case dir> <args>

A file of its own so that each call, of about 270 in a ten-restart case, starts in a few tens of
milliseconds: it imports only what a double needs. fargate_tests.py's docstring says what the
world holds and why the doubles take this shape.
"""

from __future__ import annotations

import json
import os
import pathlib
import re
import signal
import socket
import ssl
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent
ECS_TF = ROOT / "infra" / "modules" / "cell" / "ecs.tf"

def terraform_local(name: str, cell: str = "poc") -> str:
    match = re.search(rf'^\s*{re.escape(name)}\s*=\s*"([^"]*)"', ECS_TF.read_text(), re.MULTILINE)
    if match is None:
        raise AssertionError(f"ecs.tf has no local {name}")
    return match[1].replace("${var.cell}", cell)


ACCOUNT = "111122223333"
REGION = "us-east-2"
CLUSTER = terraform_local("cluster_name")
SERVICE = terraform_local("service_name")
GATEWAY = terraform_local("gateway_container_name")
LOG_GROUP = terraform_local("log_group_name")
DIGEST = "sha256:" + "a1" * 32
IMAGE = f"{ACCOUNT}.dkr.ecr.{REGION}.amazonaws.com/spa-poc/gateway@{DIGEST}"
TD_ARN = f"arn:aws:ecs:{REGION}:{ACCOUNT}:task-definition/spa-platform-poc:7"
TG_ARN = f"arn:aws:elasticloadbalancing:{REGION}:{ACCOUNT}:targetgroup/spa-poc/0123456789abcdef"
ALB = f"internal-spa-poc-1234567890.{REGION}.elb.amazonaws.com"
STATE_CANARY = "STATECANARY0123456789"
COOKIE_CANARY = "COOKIECANARY9876543210"
# The repository canary is the URI without its account label, so it fails on its own once the
# account has been redacted out of a URI that was not.
REPOSITORY_TAIL = IMAGE.split("@")[0].split(".", 1)[1]
SECRETS = (ACCOUNT, STATE_CANARY, COOKIE_CANARY, REPOSITORY_TAIL, "arn:aws")
APP_HOST = "app1.spa-poc.test:8443"
BASE_S = 1792557666           # 2026-10-21T04:41:06Z, startup_tests.py's worked instant

# --- the world ------------------------------------------------------------------------------------


def iso(ns: int, digits: int = 6, zone: str = "+00:00") -> str:
    """An instant as the CLI (6 digits, +00:00), the metadata endpoint (9, Z) or .NET's "O" (7) prints it."""
    whole, fraction = divmod(ns, 1_000_000_000)
    stamp = time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(whole))
    return f"{stamp}.{str(fraction).rjust(9, '0')[:digits]}{zone}"


def task_id(n: int) -> str:
    return f"{n:032x}"


def task_arn(n: int) -> str:
    return f"arn:aws:ecs:{REGION}:{ACCOUNT}:task/{CLUSTER}/{task_id(n)}"


def plan(**changes) -> dict:
    """One restart's script: what the new task does. Times in ms after its createdAt."""
    entry = {
        "hidden_polls": 1,                       # list-tasks calls before the new task is listed
        "decoy": True,                           # a task of no deployment of ours, listed first
        "health": [None, ["initial", "Elb.RegistrationInProgress"], ["initial", "Elb.InitialHealthChecking"],
                   ["healthy", None]],
        "stop_at_sample": None,                  # a stop for health after this many samples
        "stop": ["ServiceSchedulerInitiated", f"Task failed ELB health checks in (target-group {TG_ARN})"],
        "probe_lands": ["new"],                  # each probe in turn, the last repeated
        "stable": True,
        "exec": "ok",
        "pull_started": 5000, "pull_stopped": 20000, "container_started": 21500, "started": 22000,
        "log": [["Now listening on: http://[::]:8080", 900], ["Registry revision 42 (\"etag\") is live with 1 app(s).", 1500],
                ["health", 503, 2000], ["health", 200, 3000], ["health", 200, 3100]],
        "redirect_ms": 640.5,
        "bad_created_at": False,                 # the new task's createdAt is no instant the script reads
    }
    entry.update(changes)
    return entry


def world(plans=None, **changes) -> dict:
    plans = plans if plans is not None else [plan() for _ in range(10)]
    first = 1
    document = {
        "credentials": True,
        "td_image": IMAGE,
        "deployments": [{"id": "ecs-svc/1000000000000000001", "status": "PRIMARY", "rolloutState": "COMPLETED",
                         "taskDefinition": TD_ARN}],
        "tasks": {str(first): {"n": first, "startedBy": "ecs-svc/1000000000000000001", "desired": "RUNNING",
                               "last": "RUNNING", "created_ms": 0, "plan": None, "digest": DIGEST}},
        "plans": plans,
        "restart": 0,
        "current": str(first),
        "new": None,
        "samples": 0,
        "list_polls": 0,
        "next_task": 100,
        "initially_stable": True,
        "service_gone_after": None,              # describe-services names no service from this restart on
        "list_tasks_fail_at_poll": None,         # this list-tasks call (1-based) fails ThrottlingException
        "list_tasks_garbage_at_poll": None,      # this list-tasks call (1-based) exits 0 with no JSON
    }
    document.update(changes)
    return document


def load(case: pathlib.Path) -> dict:
    return json.loads((case / "world.json").read_text())


def save(case: pathlib.Path, document: dict) -> None:
    temporary = case / "world.json.tmp"
    temporary.write_text(json.dumps(document))
    temporary.replace(case / "world.json")


def record(case: pathlib.Path, entry: dict) -> None:
    with open(case / "calls.jsonl", "a") as calls:
        calls.write(json.dumps(entry) + "\n")


class Unexpected(Exception):
    pass


def options_of(argv):
    words, options, index = [], {}, 0
    multi = {"tasks", "services"}
    while index < len(argv):
        token = argv[index]
        if token.startswith("--"):
            name = token[2:]
            values = []
            index += 1
            while index < len(argv) and not argv[index].startswith("--"):
                values.append(argv[index])
                index += 1
                if name not in multi:
                    break
            options[name] = values if name in multi else (values[0] if values else True)
        else:
            words.append(token)
            index += 1
    return words, options


def created_ns(task: dict) -> int:
    return (BASE_S + task["n"] * 1000) * 1_000_000_000


def task_json(document: dict, task: dict) -> dict:
    base = created_ns(task)
    entry = document["plans"][task["plan"]] if task.get("plan") is not None else plan()
    out = {
        "taskArn": task_arn(task["n"]), "lastStatus": task["last"], "desiredStatus": task["desired"],
        "startedBy": task["startedBy"], "platformVersion": "1.4.0",
        "taskDefinitionArn": TD_ARN,
        "createdAt": iso(base),
        "pullStartedAt": iso(base + entry["pull_started"] * 1_000_000),
        "pullStoppedAt": iso(base + entry["pull_stopped"] * 1_000_000),
        "startedAt": iso(base + entry["started"] * 1_000_000),
        "attachments": [{"type": "ElasticNetworkInterface", "details": [
            {"name": "subnetId", "value": "subnet-0abc"}, {"name": "privateIPv4Address", "value": ip_of(task["n"])}]}],
        "containers": [
            {"name": "mock-api", "runtimeId": f"{task_id(task['n'])}-1111", "imageDigest": "sha256:" + "b2" * 32,
             "managedAgents": [{"name": "ExecuteCommandAgent", "lastStatus": "RUNNING"}]},
            {"name": GATEWAY, "runtimeId": f"{task_id(task['n'])}-2222", "imageDigest": task.get("digest", DIGEST),
             "managedAgents": [{"name": "ExecuteCommandAgent", "lastStatus": "RUNNING"}]},
        ],
    }
    if task.get("stop"):
        out["stopCode"], out["stoppedReason"] = task["stop"]
    if task.get("plan") is not None and entry["bad_created_at"] and not task.get("decoy"):
        out["createdAt"] = "not an instant"
    return out


def ip_of(n: int) -> str:
    return f"10.20.{n // 250}.{n % 250 + 2}"


def log_events(case: pathlib.Path, document: dict, task: dict) -> list:
    entry = document["plans"][task["plan"]] if task.get("plan") is not None else plan()
    zero = created_ns(task) + entry["container_started"] * 1_000_000
    lines = []
    for item in entry["log"]:
        if item[0] == "health":
            lines.append(({"ts": None, "level": "info", "msg": "Request", "route": "health", "status": item[1],
                           "duration_ms": 1.2}, item[2]))
        else:
            lines.append(({"ts": None, "level": "info", "msg": item[0]}, item[1]))
    probes = case / "probes.jsonl"
    if probes.exists():
        landed = [json.loads(line) for line in probes.read_text().splitlines()]
        for index, probe in enumerate(probe for probe in landed if probe["task"] == task["n"]):
            lines.append(({"ts": None, "level": "info", "msg": "Request", "route": "asset", "status": 302,
                           "duration_ms": entry["redirect_ms"]}, 40000 + index * 1000))
    events = []
    for line, at in sorted(lines, key=lambda pair: pair[1]):
        stamp = zero + at * 1_000_000
        line["ts"] = iso(stamp, 7)
        events.append({"timestamp": stamp // 1_000_000 + 200, "message": json.dumps(line), "ingestionTime": stamp // 1_000_000 + 900})
    return events


def answer_aws(case: pathlib.Path, argv: list) -> int:
    document = load(case)
    if argv[:2] != ["--region", REGION]:
        raise Unexpected("no --region first")
    words, options = options_of(argv[2:])
    shape = " ".join(words)
    out = None

    def fail(code, status=254):
        sys.stderr.write(f"\nAn error occurred ({code}) when calling the operation: User: arn:aws:sts::{ACCOUNT}:assumed-role/x is not authorized\n")
        return status

    def tasks_named(arns):
        return [document["tasks"][str(int(arn.rsplit("/", 1)[1], 16))] for arn in arns]

    if shape == "sts get-caller-identity":
        if not document["credentials"]:
            sys.stderr.write("\nUnable to locate credentials. You can configure credentials by running \"aws configure\".\n")
            return 253
        out = {"UserId": "AROAEXAMPLE:operator", "Account": ACCOUNT, "Arn": f"arn:aws:sts::{ACCOUNT}:assumed-role/operator/x"}
    elif shape == "ecs describe-services" and options.get("cluster") == CLUSTER and options.get("services") == [SERVICE] \
            and document["service_gone_after"] is not None and document["restart"] >= document["service_gone_after"]:
        out = {"services": [], "failures": [{"arn": f"arn:aws:ecs:{REGION}:{ACCOUNT}:service/{CLUSTER}/{SERVICE}", "reason": "MISSING"}]}
    elif shape == "ecs describe-services" and options.get("cluster") == CLUSTER and options.get("services") == [SERVICE]:
        out = {"services": [{"serviceName": SERVICE, "taskDefinition": TD_ARN, "platformVersion": "LATEST",
                             "loadBalancers": [{"targetGroupArn": TG_ARN, "containerName": GATEWAY, "containerPort": 8080}],
                             "deployments": document["deployments"]}]}
    elif shape == "ecs describe-task-definition" and options.get("task-definition") == TD_ARN:
        out = {"taskDefinition": {"taskDefinitionArn": TD_ARN, "containerDefinitions": [
            {"name": GATEWAY, "image": document["td_image"],
             "logConfiguration": {"logDriver": "awslogs", "options": {"awslogs-group": LOG_GROUP, "awslogs-stream-prefix": GATEWAY}}},
            {"name": "mock-api", "image": "public.ecr.aws/x/mock@sha256:" + "c3" * 32}]}}
    elif shape == "ecs wait services-stable" and options.get("cluster") == CLUSTER and options.get("services") == [SERVICE]:
        if document["restart"] == 0:
            if not document["initially_stable"]:
                sys.stderr.write("\nWaiter ServicesStable failed: Max attempts exceeded\n")
                return 255
            return 0
        entry = document["plans"][document["restart"] - 1]
        if not entry["stable"]:
            sys.stderr.write("\nWaiter ServicesStable failed: Max attempts exceeded\n")
            return 255
        # The rollout completes: the old task has drained and stopped, one deployment is left.
        old = document["tasks"][document["current"]]
        old["desired"], old["last"] = "STOPPED", "STOPPED"
        document["current"] = document["new"]
        document["deployments"] = [dict(document["deployments"][0], rolloutState="COMPLETED")]
        for task in document["tasks"].values():
            if task.get("decoy"):
                task["desired"], task["last"] = "STOPPED", "STOPPED"
        save(case, document)
        return 0
    elif shape == "ecs list-tasks" and options.get("cluster") == CLUSTER and options.get("service-name") == SERVICE \
            and options.get("desired-status") in (None, "RUNNING") and set(options) <= {"cluster", "service-name", "desired-status", "output"}:
        document["list_polls"] += 1
        if document["list_tasks_fail_at_poll"] == document["list_polls"]:
            save(case, document)
            return fail("ThrottlingException")
        if document["list_tasks_garbage_at_poll"] == document["list_polls"]:
            save(case, document)
            sys.stdout.write("<html>not json</html>")
            return 0
        listed = []
        for task in document["tasks"].values():
            if task["desired"] != "RUNNING":
                continue
            if task.get("hidden_until", 0) > document["list_polls"]:
                continue
            listed.append(task)
        # A decoy first, then the rest: the script must choose by startedBy, not by position.
        listed.sort(key=lambda task: (not task.get("decoy"), task["n"]))
        out = {"taskArns": [task_arn(task["n"]) for task in listed]}
        save(case, document)
    elif shape == "ecs describe-tasks" and options.get("cluster") == CLUSTER and options.get("tasks"):
        named = tasks_named(options["tasks"])
        for task in named:
            entry = document["plans"][task["plan"]] if task.get("plan") is not None else None
            if entry and entry["stop_at_sample"] is not None and document["samples"] >= entry["stop_at_sample"] and task["n"] == int(document["new"]):
                task["last"], task["desired"], task["stop"] = "STOPPED", "STOPPED", entry["stop"]
        save(case, document)
        out = {"tasks": [task_json(document, task) for task in named], "failures": []}
    elif shape == "ecs update-service" and options.get("cluster") == CLUSTER and options.get("service") == SERVICE \
            and options.get("force-new-deployment") is True and set(options) == {"cluster", "service", "force-new-deployment", "output"}:
        if document["restart"] >= len(document["plans"]):
            raise Unexpected("a restart the world has no plan for")
        index = document["restart"]
        entry = document["plans"][index]
        document["restart"] += 1
        deployment = f"ecs-svc/{2000000000000000000 + index}"
        n = document["next_task"]
        document["next_task"] += 2
        document["tasks"][str(n)] = {"n": n, "startedBy": deployment, "desired": "RUNNING", "last": "PROVISIONING",
                                     "plan": index, "hidden_until": document["list_polls"] + 1 + entry["hidden_polls"]}
        if entry["decoy"]:
            # A task the scheduler started for another reason, overlapping: same service, no deployment of ours.
            document["tasks"][str(n + 1)] = {"n": n + 1, "startedBy": "ecs-svc/9999999999999999999", "desired": "RUNNING",
                                             "last": "RUNNING", "plan": index, "decoy": True}
        document["new"] = str(n)
        document["samples"] = 0
        for deployment_entry in document["deployments"]:
            deployment_entry["status"] = "ACTIVE"
        document["deployments"] = [{"id": deployment, "status": "PRIMARY", "rolloutState": "IN_PROGRESS", "taskDefinition": TD_ARN}] + \
            document["deployments"]
        save(case, document)
        out = {"service": {"serviceName": SERVICE, "deployments": document["deployments"]}}
    elif shape == "elbv2 describe-target-health" and options.get("target-group-arn") == TG_ARN:
        descriptions = [{"Target": {"Id": ip_of(int(document["current"])), "Port": 8080},
                         "TargetHealth": {"State": "healthy"}}]
        if document["new"] is not None:
            new = document["tasks"][document["new"]]
            new["last"] = "RUNNING" if new["last"] == "PROVISIONING" else new["last"]
            health = document["plans"][new["plan"]]["health"]
            sample = health[min(document["samples"], len(health) - 1)]
            document["samples"] += 1
            if sample is not None:
                state = {"State": sample[0]}
                if sample[1]:
                    state["Reason"] = sample[1]
                    state["Description"] = f"Target registration is in progress for {TG_ARN}"
                descriptions.append({"Target": {"Id": ip_of(new["n"]), "Port": 8080}, "TargetHealth": state})
        save(case, document)
        out = {"TargetHealthDescriptions": descriptions}
    elif shape == "logs get-log-events" and options.get("log-group-name") == LOG_GROUP and options.get("start-from-head") is True:
        stream = options.get("log-stream-name", "")
        match = re.fullmatch(rf"{re.escape(GATEWAY)}/{re.escape(GATEWAY)}/([0-9a-f]{{32}})", stream)
        task = document["tasks"].get(str(int(match[1], 16))) if match else None
        if task is None:
            return fail("ResourceNotFoundException")
        if options.get("next-token") == "f/end":
            out = {"events": [], "nextForwardToken": "f/end", "nextBackwardToken": "b/0"}
        else:
            out = {"events": log_events(case, document, task), "nextForwardToken": "f/end", "nextBackwardToken": "b/0"}
    elif shape == "ecs execute-command" and options.get("cluster") == CLUSTER and options.get("container") == GATEWAY \
            and options.get("interactive") is True and options.get("command", "").startswith("bash -c '"):
        task = document["tasks"].get(str(int(options["task"], 16)))
        if task is None or task["n"] != int(document["current"]):
            raise Unexpected("an exec on a task that is not the service's running one")
        kind = document["plans"][task["plan"]]["exec"]
        if kind == "no-session":
            return fail("TargetNotConnectedException")
        session = {"sessionId": "ecs-execute-command-0abc", "streamUrl": "wss://example", "tokenValue": "TOKENCANARY"}
        os.execvp("session-manager-plugin", ["session-manager-plugin", json.dumps(session), REGION, "StartSession", "",
                                             json.dumps({"kind": "exec", "task": task["n"]}), "https://ecs"])
    elif shape == "ssm start-session" and options.get("document-name") == "AWS-StartPortForwardingSessionToRemoteHost":
        parameters = json.loads(options["parameters"])
        session = {"SessionId": "operator-0abc", "StreamUrl": "wss://example", "TokenValue": "TOKENCANARY"}
        os.execvp("session-manager-plugin", ["session-manager-plugin", json.dumps(session), REGION, "StartSession", "",
                                             json.dumps({"kind": "forward", "Target": options["target"], "Parameters": parameters}),
                                             "https://ssm"])
    else:
        raise Unexpected(shape)
    if options.get("output") != "json":
        raise Unexpected("not --output json")
    sys.stdout.write(json.dumps(out))
    return 0


def answer_plugin(case: pathlib.Path, argv: list) -> int:
    request = json.loads(argv[4])
    if request["kind"] == "exec":
        document = load(case)
        task = document["tasks"][str(request["task"])]
        entry = document["plans"][task["plan"]]
        zero = created_ns(task) + entry["container_started"] * 1_000_000
        sys.stdout.write("\r\nStarting session with SessionId: ecs-execute-command-0abc\r\n")
        if entry["exec"] == "ok":
            container = {"DockerId": "x", "Name": GATEWAY, "StartedAt": iso(zero, 9, "Z")}
            whole = {"Cluster": f"arn:aws:ecs:{REGION}:{ACCOUNT}:cluster/{CLUSTER}", "ClockDrift": {
                "ClockErrorBound": 0.4, "ReferenceTimestamp": iso(zero, 9, "Z"), "ClockSynchronizationStatus": "SYNCHRONIZED"}}
            for name, body in (("CONTAINER", container), ("TASK", whole)):
                sys.stdout.write(f"SPA-BENCH-BEGIN-{name}\r\nHTTP/1.0 200 OK\r\nContent-Type: application/json\r\n\r\n"
                                 f"{json.dumps(body)}\r\nSPA-BENCH-END-{name}\r\n")
        elif entry["exec"] == "no-endpoint":
            sys.stdout.write("SPA-BENCH-NO-ENDPOINT\r\n")
        elif entry["exec"] == "no-parse":
            sys.stdout.write("SPA-BENCH-BEGIN-CONTAINER\r\nbash: connect: Connection refused\r\nSPA-BENCH-END-CONTAINER\r\n"
                             "SPA-BENCH-BEGIN-TASK\r\n\r\nSPA-BENCH-END-TASK\r\n")
        sys.stdout.write("\r\n\r\nExiting session with sessionId: ecs-execute-command-0abc.\r\n\r\n")
        return 0
    if request["kind"] == "forward":
        return serve_forward(case, request)
    raise Unexpected("plugin request")


def serve_forward(case: pathlib.Path, request: dict) -> int:
    """The load balancer through the hop: TLS under the test authority, every request the challenge."""
    port = int(request["Parameters"]["localPortNumber"][0])
    hop = int(request["Target"].split("_")[1], 16)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(str(case / "leaf.pem"), str(case / "leaf.key"))
    listener = socket.socket()
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", port))
    listener.listen(8)
    signal.signal(signal.SIGTERM, lambda *_: os._exit(0))
    sys.stdout.write(f"\nStarting session with SessionId: operator-0abc.\nPort {port} opened for sessionId operator-0abc.\nWaiting for connections...\n")
    sys.stdout.flush()
    while True:
        connection, _ = listener.accept()
        try:
            with context.wrap_socket(connection, server_side=True) as tls:
                data = b""
                while b"\r\n\r\n" not in data:
                    chunk = tls.recv(4096)
                    if not chunk:
                        break
                    data += chunk
                head = data.split(b"\r\n\r\n", 1)[0].decode(errors="replace").split("\r\n")
                headers = dict(line.split(": ", 1) for line in head[1:] if ": " in line)
                document = load(case)
                probes = case / "probes.jsonl"
                earlier = [json.loads(line) for line in probes.read_text().splitlines()] if probes.exists() else []
                count = len([probe for probe in earlier if probe["restart"] == document["restart"]])
                lands = document["plans"][document["restart"] - 1]["probe_lands"]
                where = lands[min(count, len(lands) - 1)]
                with open(probes, "a") as out:
                    out.write(json.dumps({"request": head[0], "headers": headers, "hop": hop, "restart": document["restart"],
                                          "task": int(document["new"]) if where == "new" else int(document["current"])}) + "\n")
                tls.sendall((f"HTTP/1.1 302 Found\r\nLocation: https://idp.example/authorize?client_id=x&state={STATE_CANARY}\r\n"
                             f"Set-Cookie: .AspNetCore.Correlation.{STATE_CANARY}={COOKIE_CANARY}; path=/; secure; httponly\r\n"
                             "Content-Length: 0\r\n\r\n").encode())
        except (OSError, ssl.SSLError):
            pass


def double_main(argv: list) -> int:
    name, case, rest = argv[0], pathlib.Path(argv[1]), argv[2:]
    record(case, {"tool": name, "argv": rest,
                  "credential_env": sorted(key for key in os.environ if key in CREDENTIAL_VARIABLES)})
    try:
        return answer_aws(case, rest) if name == "aws" else answer_plugin(case, rest)
    except Unexpected as unexpected:
        record(case, {"tool": name, "unexpected": str(unexpected)})
        sys.stderr.write(f"double: unexpected call: {unexpected}\n")
        return 97


# What the CLI would sign with, if any were in the environment.
# https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-envvars.html
CREDENTIAL_VARIABLES = ("AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN", "AWS_PROFILE",
                        "AWS_CONTAINER_CREDENTIALS_FULL_URI", "AWS_WEB_IDENTITY_TOKEN_FILE")


if __name__ == "__main__":
    sys.exit(double_main(sys.argv[1:]))
