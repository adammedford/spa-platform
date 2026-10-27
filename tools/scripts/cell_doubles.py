#!/usr/bin/env python3
"""The doubles and the case harness of the cell scripts' companions (seed_tests.py, deploy_tests.py
and the others beside it that load it by path). Owner: workstream 1. Not a companion itself. Its own
companion, cell_doubles_tests.py, finds every companion that loads it, runs each, and holds SECRETS to
every credential the aws double hands out (#201).

Task 1.7 (#58). The row's done-when, each script running unattended against a gateway build that
answers /readyz, needs the applied cell and Docker, and is sitting C's (C1 and C2 in
.scratch/sitting/spec.md). What a lane can prove without AWS is everything the scripts decide: the
order of their calls, what they pass to Terraform, Docker and spactl, what they refuse before any of
it, and what they read back afterwards. The companions prove those in D-3's shape
(docs/dependencies.md): Python's unittest, running the real scripts under /bin/bash.

**A constructed environment, inheriting nothing.** PATH is the case's bin directory alone: a
one-line exec of each real tool the scripts use (python3 among them, for cell_documents.py), and
doubles for aws, terraform, docker and dotnet. No case can reach AWS, Docker or NuGet.

**One world.** The doubles share the case's world.json: the three roots' outputs, the bucket's
object versions and its live registry, the image repositories, the task definitions, the service,
its tasks, its target group and the resources the tagging API would list. Terraform's apply and
destroy change the world as AWS would, so a script's reads after its writes see the effect of its
writes. Every call is recorded in calls.jsonl in order; a call shape the double does not answer is
recorded and exits 97, so a new call in a script turns a case red instead of being answered.

**Names are read from the Terraform that makes them.** The cluster, the service, the containers, the
task definition family and the repositories are the locals in infra/modules/cell, read out of those
files (CODING_STANDARDS.md, "A guard reads the thing it guards").

**A universal held on every run:** nothing a script prints names the account id. The repositories'
URIs, the bucket's name and every ARN carry it, and the doubles print them where the real tools do:
a plan, an apply, a destroy, a refused docker login. Nor does it name the registry password or the
teardown role's credentials, which no file the script leaves holds either (SECRETS, CREDENTIALS).
"""

from __future__ import annotations

import hashlib
import json
import os
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent
SCRIPTS = ROOT / "tools" / "scripts"
MODULE = ROOT / "infra" / "modules" / "cell"
BASH = "/bin/bash"
REQUIRE_BASH = "SCRIPT_TESTS_REQUIRE_BASH"

ACCOUNT = "111122223333"
REGION = "us-east-2"
CELL = "poc"
PASSWORD = "ecr-login-password-0123456789"

# The real tools the scripts use. Everything else is absent.
TOOLS = ("awk", "sed", "tr", "cat", "mktemp", "rm", "dirname", "basename", "python3", "sleep", "date")


def terraform_local(path: pathlib.Path, name: str, cell: str = CELL) -> str:
    """The value of `name = "..."` in a locals block, with the cell put in."""
    match = re.search(rf'^\s*{re.escape(name)}\s*=\s*"([^"]*)"', path.read_text(), re.MULTILINE)
    if match is None:
        raise AssertionError(f"{path.name} has no local {name}")
    return match[1].replace("${var.cell}", cell).replace("${local.name_prefix}", f"spa-platform-{cell}")


CLUSTER = terraform_local(MODULE / "ecs.tf", "cluster_name")
SERVICE = terraform_local(MODULE / "ecs.tf", "service_name")
GATEWAY = terraform_local(MODULE / "ecs.tf", "gateway_container_name")
MOCK_API = terraform_local(MODULE / "ecs.tf", "mock_api_container_name")
LOG_GROUP = terraform_local(MODULE / "ecs.tf", "log_group_name")
FAMILY = terraform_local(MODULE / "network.tf", "name_prefix")
REGISTRY_HOST = f"{ACCOUNT}.dkr.ecr.{REGION}.amazonaws.com"
# infra/modules/cell/storage.tf: spa-${var.cell}/${each.key}, over the set ["gateway", "mock-api"].
_REPO = re.search(r'name = "(spa-\$\{var\.cell\}/)\$\{each\.key\}"', (MODULE / "storage.tf").read_text())
assert _REPO, "storage.tf no longer names the repositories spa-${var.cell}/${each.key}"
GATEWAY_REPOSITORY = _REPO[1].replace("${var.cell}", CELL) + "gateway"
MOCK_API_REPOSITORY = _REPO[1].replace("${var.cell}", CELL) + "mock-api"
ECR_GATEWAY = f"{REGISTRY_HOST}/{GATEWAY_REPOSITORY}"
ECR_MOCK_API = f"{REGISTRY_HOST}/{MOCK_API_REPOSITORY}"
BUCKET = f"spa-platform-{CELL}-{ACCOUNT}-{REGION}-an"
SAMPLE_IMAGE = "public.ecr.aws/nginx/nginx-unprivileged@sha256:" + "5" * 64
TEARDOWN_ROLE = f"spa-platform-{CELL}-teardown"
TEARDOWN_ROLE_ARN = f"arn:aws:iam::{ACCOUNT}:role/{TEARDOWN_ROLE}"
OPERATOR_ARN = f"arn:aws:sts::{ACCOUNT}:assumed-role/AWSReservedSSO_AdministratorAccess_0123/operator"
TARGET_GROUP_ARN = f"arn:aws:elasticloadbalancing:{REGION}:{ACCOUNT}:targetgroup/spa-{CELL}/0123456789abcdef"
TEARDOWN_KEY = "ASIATEARDOWNKEY0000"
TEARDOWN_SECRET = "teardownsecret/abc+def"
TEARDOWN_TOKEN = "teardownsessiontoken/xyz=="
# What no output may hold: the account id, the registry password, and the teardown role's three
# credentials, as verify_release_immutability_tests.py lists them. teardown.sh and cell-common.sh say
# the credentials go only to one call's environment, never into a file or the output (#191).
CREDENTIALS = (PASSWORD, TEARDOWN_KEY, TEARDOWN_SECRET, TEARDOWN_TOKEN)
# The key ring's parameter names under the cell's prefix, /spa-platform/{cell}/data-protection/,
# which teardown.sh lists and deletes (#338). A name is not a key, but GetParametersByPath is a call
# that can answer with values, so cell_doubles_tests.py counts whatever it prints as handed out, and
# the scripts print none of them: teardown.sh says how many it deleted, never which.
RING_KEYS = tuple(f"/spa-platform/{CELL}/data-protection/key-{n:08d}-aaaa-bbbb-cccc-ddddeeeeffff" for n in range(1, 24))
SECRETS = (ACCOUNT, *CREDENTIALS, *RING_KEYS)


def digest_of(text: str) -> str:
    return "sha256:" + hashlib.sha256(text.encode()).hexdigest()


def td_arn(name: str) -> str:
    return f"arn:aws:ecs:{REGION}:{ACCOUNT}:task-definition/{name}"


def environment(bucket: str = BUCKET) -> list[list[str]]:
    """The gateway container's environment as infra/modules/cell/ecs.tf builds it, sorted by name."""
    pairs = {
        "ASPNETCORE_HTTP_PORTS": "8080",
        "Assets__Bucket": bucket,
        "ForwardedHeaders__TrustedNetworks__0": "10.20.0.0/24",
        "ForwardedHeaders__TrustedNetworks__1": "10.20.1.0/24",
        "Issuers__cognito-poc__Authority": f"https://cognito-idp.{REGION}.amazonaws.com/{REGION}_EXAMPLE",
        "Issuers__cognito-poc__BrowserOrigin": f"https://spa-platform-poc-{ACCOUNT}.auth.{REGION}.amazoncognito.com",
        "Issuers__cognito-poc__Profile": "cognito",
        "Platform__Cell": CELL,
        "Policy__AllowedHostSuffixes__0": ".spa-poc.test:8443",
        "Policy__AllowedUpstreams__0": "http://127.0.0.1:5080",
        "Policy__AllowedUpstreams__1": "http://127.0.0.1:5081",
        "Registry__Bucket": bucket,
        "Registry__Key": "registry/registry.json",
        "Registry__PollSeconds": "15",
        "Session__MaxSessions": "10000",
        "Session__Store": "memory",
    }
    return [[name, pairs[name]] for name in sorted(pairs)]


def platform_outputs(gateway_image: str = SAMPLE_IMAGE, bucket: str = BUCKET) -> dict:
    return {
        "region": REGION, "cell": CELL, "bucket": bucket,
        "alb_dns_name": f"internal-spa-{CELL}-1234567890.{REGION}.elb.amazonaws.com",
        "ecr_gateway": ECR_GATEWAY, "ecr_mock_api": ECR_MOCK_API,
        "ecs_cluster": CLUSTER, "ecs_service": SERVICE, "log_group": LOG_GROUP,
        "gateway_image": gateway_image,
    }


def task_ip(task: dict) -> str:
    """The private IPv4 address of a task's elastic network interface, made from its id. The task
    runs in awsvpc mode and the target group's target type is ip (infra/modules/cell/ecs.tf,
    alb.tf), so this is also the id of the task's target."""
    low = int(task["id"][-4:], 16)
    return f"10.0.{low >> 8}.{low & 0xff}"


def default_world() -> dict:
    """The cell after the first deployment's step 3: the three roots applied, the service on the
    sample image with / as its health path and its one task's target healthy, an empty bucket and
    empty repositories."""
    first = f"{FAMILY}:1"
    task = {"id": "0123456789abcdef0123456789abcdef", "lastStatus": "RUNNING", "taskDefinition": first,
            "gatewayDigest": SAMPLE_IMAGE.split("@")[1]}
    return {
        "region": REGION,
        "caller": OPERATOR_ARN,
        "roots": {
            "idp": {"region": REGION, "cell": CELL,
                    "issuer": f"https://cognito-idp.{REGION}.amazonaws.com/{REGION}_EXAMPLE",
                    "cognito_domain": f"spa-platform-poc-{ACCOUNT}.auth.{REGION}.amazoncognito.com",
                    "test_users": {"alice": {"passwordParameter": "/spa-platform/poc/test-users/alice"}}},
            "apps": {"clients": {
                "app1": {"clientId": "1example23456789app1client", "secretParameter": "/spa-platform/poc/apps/app1/client-secret", "secretVersion": 1},
                "app2": {"clientId": "1example23456789app2client", "secretParameter": "/spa-platform/poc/apps/app2/client-secret", "secretVersion": 1}}},
            "platform": platform_outputs(),
        },
        "objects": [],
        "registry": None,
        "repositories": {GATEWAY_REPOSITORY: {}, MOCK_API_REPOSITORY: {}},
        "task_definitions": {first: {"status": "ACTIVE", "containers": {
            GATEWAY: {"image": SAMPLE_IMAGE, "environment": environment()}}}},
        "service": {"taskDefinition": first, "rolloutState": "COMPLETED", "deployments": 1},
        "tasks": [task],
        "target_group": {"path": "/", "targets": [{"id": task_ip(task), "state": "healthy"}]},
        "tagged": [],
        # IAM, which the tagging API cannot list (#118): every role in the project with its tags, and
        # the customer managed policies. Bootstrap's two roles carry its own value (ADR-0013), the
        # cell's two the platform tag and go with the platform root (infra/modules/cell/iam.tf), and
        # the service-linked role ECS creates carries none and outlives every teardown.
        "roles": {
            f"spa-platform-{CELL}-ci-plan": {"tags": {"Platform": "spa-platform-bootstrap", "Cell": CELL}},
            TEARDOWN_ROLE: {"tags": {"Platform": "spa-platform-bootstrap", "Cell": CELL}},
            f"{FAMILY}-task-execution": {"tags": {"Platform": "spa-platform", "Cell": CELL}, "root": "platform"},
            f"{FAMILY}-task": {"tags": {"Platform": "spa-platform", "Cell": CELL}, "root": "platform"},
            "AWSServiceRoleForECS": {"tags": {}},
        },
        "policies": {},
        # Parameter Store names that no root's state holds: the key ring the gateway writes under
        # /spa-platform/{cell}/data-protection/ (P2-2 decision 17), which teardown.sh deletes.
        "parameters": [],
        # The log group's events, as [stream, message] pairs, for logs.sh. A stream is named
        # <awslogs-stream-prefix>/<container>/<task id>, and ecs.tf sets each container's prefix to
        # its own name: https://docs.aws.amazon.com/AmazonECS/latest/developerguide/using_awslogs.html
        "log_events": [],
        "faults": {},
        "next_task": 1,
    }


# --- the doubles -----------------------------------------------------------------------------------

class Unexpected(Exception):
    """A call the double does not answer: recorded, and exit 97."""


class ServiceError(Exception):
    def __init__(self, code: str, operation: str, message: str = "error"):
        super().__init__(code)
        self.code, self.operation, self.message = code, operation, message


# The EC2 describes verify-teardown.sh confirms a listed id with: its option, the field its answer
# lists the id under, and the error code EC2 answers for an id it does not know.
# https://docs.aws.amazon.com/AWSEC2/latest/APIReference/errors-overview.html
EC2_DESCRIBES = {
    "describe-security-groups": ("group-ids", "security-group", "InvalidGroup.NotFound", "SecurityGroups[].GroupId"),
    "describe-security-group-rules": ("security-group-rule-ids", "security-group-rule",
                                      "InvalidSecurityGroupRuleId.NotFound", "SecurityGroupRules[].SecurityGroupRuleId"),
    "describe-vpcs": ("vpc-ids", "vpc", "InvalidVpcID.NotFound", "Vpcs[].VpcId"),
    "describe-subnets": ("subnet-ids", "subnet", "InvalidSubnetID.NotFound", "Subnets[].SubnetId"),
    "describe-internet-gateways": ("internet-gateway-ids", "internet-gateway", "InvalidInternetGatewayID.NotFound",
                                   "InternetGateways[].InternetGatewayId"),
    "describe-route-tables": ("route-table-ids", "route-table", "InvalidRouteTableID.NotFound", "RouteTables[].RouteTableId"),
}


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


MULTI_VALUED = {"tasks", "services", "tag-filters", "task-definitions", "target-group-arns", "resource-type-filters",
                "clusters", "names", *(entry[0] for entry in EC2_DESCRIBES.values())}


def parse_aws(argv: list[str]) -> tuple[list[str], dict]:
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
            options[name] = values if name in MULTI_VALUED else (values[0] if values else True)
        else:
            words.append(token)
            index += 1
    return words, options


def index_entry(world: dict, arn: str, kind: str) -> dict | None:
    """The tagging index's entry for an ECS resource, by ARN, when describe would still know it."""
    return next((entry for entry in world["tagged"] if entry["arn"] == arn and entry["type"] == kind), None)


def ecs_answer(entry: dict | None) -> str:
    """What DescribeClusters or DescribeServices answers for one resource: its status, or the
    failure's reason, MISSING for one ECS does not know. An entry's describe_failure sets the reason;
    an empty one is an answer that carries neither."""
    if entry is None:
        return "MISSING"
    if "describe_failure" in entry:
        return entry["describe_failure"]
    return entry.get("status", "ACTIVE")


def task_definition_name(reference: str) -> str:
    return reference.split("task-definition/", 1)[-1]


def objects_listing(world: dict, prefix: str) -> list:
    return [entry for entry in world["objects"] if entry["Key"].startswith(prefix)]


def command_words(words: list[str]) -> list[str]:
    """The service and operation, without the one positional argument two calls take after them:
    get-object's file it writes, and logs tail's group name."""
    return words[:2] if words[:2] in (["s3api", "get-object"], ["logs", "tail"]) else words


def aws_answer(world: dict, words: list[str], options: dict, case: pathlib.Path, stdout):
    outfile = words[2:] if words[:2] == ["s3api", "get-object"] else []
    positional = words[2:]
    words = command_words(words)
    command = " ".join(words)
    query = options.get("query")
    fault = world["faults"].get(command)
    if fault:
        raise ServiceError(fault[0], fault[1])
    if command == "sts get-caller-identity":
        return [ACCOUNT, world["caller"]]
    if command == "ecr get-login-password":
        stdout.write(PASSWORD)
        return None
    if command == "logs tail":
        # https://awscli.amazonaws.com/v2/documentation/api/latest/reference/logs/tail.html: one
        # positional group name; the detailed format prints each event as its timestamp, its stream
        # and its message.
        if positional != [world["roots"]["platform"]["log_group"]]:
            raise ServiceError("ResourceNotFoundException", "FilterLogEvents", "The specified log group does not exist.")
        if query is not None or options.get("format") != "detailed" or options.get("color") != "off":
            raise Unexpected(f"logs tail {options}")
        prefix = options.get("log-stream-name-prefix", "")
        for stream, message in world["log_events"]:
            if stream.startswith(prefix):
                stdout.write(f"2026-10-21T02:14:07.123000+00:00 {stream} {message}\n")
        return None
    if command == "ecr describe-images":
        repository = world["repositories"].get(options["repository-name"])
        if repository is None:
            raise ServiceError("RepositoryNotFoundException", "DescribeImages")
        wanted = options["image-ids"]
        if wanted.startswith("imageTag="):
            found = repository.get(wanted[len("imageTag="):])
        elif wanted.startswith("imageDigest="):
            found = wanted[len("imageDigest="):] if wanted[len("imageDigest="):] in repository.values() else None
        else:
            raise Unexpected(f"--image-ids {wanted}")
        if found is None:
            raise ServiceError("ImageNotFoundException", "DescribeImages")
        if query != "imageDetails[0].imageDigest":
            raise Unexpected(f"describe-images --query {query!r}")
        return found
    if command == "s3api get-object":
        if options.get("bucket") != world["roots"]["platform"].get("bucket", BUCKET) and options.get("bucket") != BUCKET:
            raise ServiceError("NoSuchBucket", "GetObject")
        if options.get("key") != "registry/registry.json" or world["registry"] is None:
            raise ServiceError("NoSuchKey", "GetObject")
        if len(outfile) != 1 or not os.path.isabs(outfile[0]) or query is not None:
            raise Unexpected("get-object takes one absolute outfile and no --query here")
        pathlib.Path(outfile[0]).write_text(json.dumps(world["registry"]))
        # The CLI prints the object's metadata, as text with --output text.
        return [len(json.dumps(world["registry"])), "application/json", '"etag"']
    if command == "ecs describe-clusters":
        # A cluster ECS does not know is a failure with reason MISSING, not an error:
        # https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_DescribeClusters.html
        if query != "[clusters[].status, failures[].reason][]":
            raise Unexpected(f"describe-clusters --query {query!r}")
        return [ecs_answer(index_entry(world, arn, "ecs:cluster")) for arn in options["clusters"]]
    if command == "ecs describe-services" and query == "[services[].status, failures[].reason][]":
        return [ecs_answer(index_entry(world, arn, "ecs:service")) for arn in options["services"]]
    if words[:2] == ["ecs", "describe-services"]:
        service = world["service"]
        if service is None:
            raise ServiceError("ServiceNotFoundException", "DescribeServices")
        if query == "services[0].[taskDefinition, loadBalancers[0].targetGroupArn]":
            return [td_arn(service["taskDefinition"]), TARGET_GROUP_ARN]
        if query == ("services[0].[taskDefinition, deployments[?status==`PRIMARY`] | [0].rolloutState, "
                     "length(deployments), loadBalancers[0].targetGroupArn]"):
            return [td_arn(service["taskDefinition"]), service["rolloutState"], service["deployments"], TARGET_GROUP_ARN]
        raise Unexpected(f"describe-services --query {query!r}")
    if command == "ecs describe-task-definition" and query == "taskDefinition.status":
        # A deleted revision answers DELETE_IN_PROGRESS until ECS removes it, and then is unknown.
        # https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_DeleteTaskDefinitions.html
        definition = world["task_definitions"].get(task_definition_name(options["task-definition"]))
        if definition is None:
            raise ServiceError("ClientException", "DescribeTaskDefinition", "Unable to describe task definition.")
        return definition["status"]
    if command == "ecs describe-task-definition":
        name = task_definition_name(options["task-definition"])
        definition = world["task_definitions"].get(name)
        if definition is None or definition["status"] == "DELETE_IN_PROGRESS":
            raise ServiceError("ClientException", "DescribeTaskDefinition", "Unable to describe task definition.")
        match = re.fullmatch(r"taskDefinition\.containerDefinitions\[\?name=='([a-z-]+)'\] \| \[0\]\.(image|environment\[\]\.\[name, value\])", query or "")
        if not match:
            raise Unexpected(f"describe-task-definition --query {query!r}")
        container = definition["containers"].get(match[1])
        if match[2] == "image":
            return container["image"] if container else None
        return [list(pair) for pair in container["environment"]] if container else None
    if command == "ecs list-tasks":
        if query != "taskArns" or options.get("desired-status") != "RUNNING":
            raise Unexpected(f"list-tasks {options}")
        return [f"arn:aws:ecs:{REGION}:{ACCOUNT}:task/{CLUSTER}/{task['id']}" for task in world["tasks"]]
    if command == "ecs describe-tasks":
        expected = (f"tasks[].[taskArn, lastStatus, taskDefinitionArn, "
                    f"containers[?name=='{GATEWAY}'] | [0].imageDigest, "
                    f"containers[?name=='{GATEWAY}'] | [0].networkInterfaces[0].privateIpv4Address]")
        if query != expected:
            raise Unexpected(f"describe-tasks --query {query!r}")
        known = {f"arn:aws:ecs:{REGION}:{ACCOUNT}:task/{CLUSTER}/{task['id']}": task for task in world["tasks"]}
        return [[arn, known[arn]["lastStatus"], td_arn(known[arn]["taskDefinition"]), known[arn]["gatewayDigest"],
                 task_ip(known[arn])]
                for arn in options["tasks"] if arn in known]
    if command == "ecs wait services-stable":
        if world["faults"].get("wait"):
            stdout.flush()
            sys.stderr.write("\nWaiter ServicesStable failed: Max attempts exceeded\n")
            raise SystemExit(255)
        return None
    if command == "elbv2 describe-target-groups":
        if query != "TargetGroups[0].HealthCheckPath":
            raise Unexpected(f"describe-target-groups --query {query!r}")
        return world["target_group"]["path"]
    if command == "elbv2 describe-target-health":
        if query != "TargetHealthDescriptions[].[Target.Id, TargetHealth.State]":
            raise Unexpected(f"describe-target-health --query {query!r}")
        return [[target["id"], target["state"]] for target in world["target_group"]["targets"]]
    if command == "s3api list-object-versions":
        prefix = options.get("prefix", "")
        listed = objects_listing(world, prefix)
        versions = [entry for entry in listed if not entry.get("marker")]
        markers = [entry for entry in listed if entry.get("marker")]
        if query == "[Versions, DeleteMarkers][].[Key, VersionId]":
            listing = [[entry["Key"], entry["VersionId"]] for entry in versions + markers]
            # "written_after_listing": one version, the first, is written after this listing
            # answered (a spactl still publishing, say), so the listing leaves it out and a delete of
            # every listed version leaves the bucket holding it.
            if world["faults"].get("written_after_listing") and listing:
                listing = listing[1:]
            return listing
        if query == "[length(Versions || `[]`), length(DeleteMarkers || `[]`)]":
            return [len(versions), len(markers)]
        raise Unexpected(f"list-object-versions --query {query!r}")
    if command == "s3api delete-object":
        role = "teardown" if os.environ.get("AWS_ACCESS_KEY_ID") == TEARDOWN_KEY else "operator"
        key, version = options["key"], options.get("version-id")
        # contracts/s3-layout.md and storage.tf: only the teardown role deletes under apps/.
        if key.startswith("apps/") and role != "teardown":
            raise ServiceError("AccessDenied", "DeleteObject")
        if not key.startswith("apps/") and role == "teardown":
            raise ServiceError("AccessDenied", "DeleteObject")
        before = len(world["objects"])
        world["objects"] = [entry for entry in world["objects"]
                            if not (entry["Key"] == key and entry["VersionId"] == version)]
        if len(world["objects"]) == before:
            raise Unexpected(f"delete-object of a version that is not there: {key} {version}")
        return None
    if command == "iam get-role":
        name = options.get("role-name")
        if name != TEARDOWN_ROLE and name not in world.get("roles", {}):
            raise ServiceError("NoSuchEntity", "GetRole")
        if query != "Role.Arn":
            raise Unexpected(f"get-role --query {query!r}")
        return f"arn:aws:iam::{ACCOUNT}:role/{name}"
    # IAM's own listings, which verify-teardown.sh reads the platform tag through: the tagging API's
    # GetResources does not support IAM roles (#118).
    # https://docs.aws.amazon.com/IAM/latest/APIReference/API_ListRoleTags.html
    if command == "iam list-roles":
        if query != "Roles[].RoleName":
            raise Unexpected(f"list-roles --query {query!r}")
        return sorted(world.get("roles", {}))
    if command == "iam list-role-tags":
        role = world.get("roles", {}).get(options.get("role-name"))
        if role is None:
            raise ServiceError("NoSuchEntity", "ListRoleTags")
        match = re.fullmatch(r"Tags\[\?Key=='([A-Za-z]+)'\]\.Value", query or "")
        if not match:
            raise Unexpected(f"list-role-tags --query {query!r}")
        return [value for key, value in role["tags"].items() if key == match[1]]
    if command == "iam list-policies":
        if options.get("scope") != "Local" or query != "Policies[].Arn":
            raise Unexpected(f"list-policies {options}")
        return sorted(world.get("policies", {}))
    if command == "iam list-policy-tags":
        policy = world.get("policies", {}).get(options.get("policy-arn"))
        if policy is None:
            raise ServiceError("NoSuchEntity", "ListPolicyTags")
        match = re.fullmatch(r"Tags\[\?Key=='([A-Za-z]+)'\]\.Value", query or "")
        if not match:
            raise Unexpected(f"list-policy-tags --query {query!r}")
        return [value for key, value in policy["tags"].items() if key == match[1]]
    if command == "sts assume-role":
        if options.get("role-arn") != TEARDOWN_ROLE_ARN:
            raise ServiceError("AccessDenied", "AssumeRole")
        return [TEARDOWN_KEY, TEARDOWN_SECRET, TEARDOWN_TOKEN]
    if command == "resourcegroupstaggingapi get-resources":
        filters = options.get("tag-filters", [])
        if query != "ResourceTagMappingList[].ResourceARN":
            raise Unexpected(f"get-resources --query {query!r}")
        wanted = {}
        for item in filters:
            match = re.fullmatch(r"Key=([^,]+),Values=([^,]+)", item)
            if not match:
                raise Unexpected(f"--tag-filters {item}")
            wanted[match[1]] = match[2]
        types = options.get("resource-type-filters")
        # The tagging API matches a tag value exactly: https://docs.aws.amazon.com/resourcegroupstagging/latest/APIReference/API_TagFilter.html
        found = [entry["arn"] for entry in world["tagged"]
                 if all(entry["tags"].get(key) == value for key, value in wanted.items())
                 and (not types or entry["type"] in types)]
        countdown = world.get("tag_lag", 0)
        # The tagging API lags a deletion (docs/plan/05-infrastructure-and-cost.md, Teardown): a
        # deleted log group, which no describe confirms, answers the platform query for a few more calls.
        if countdown and not types:
            world["tag_lag"] = countdown - 1
            found = found + [f"arn:aws:logs:{REGION}:{ACCOUNT}:log-group:{LOG_GROUP}"]
        return found
    if words[:1] == ["ec2"] and words[1:2] and words[1] in EC2_DESCRIBES:
        option, kind, missing, expected = EC2_DESCRIBES[words[1]]
        if query != expected or len(options.get(option, [])) != 1:
            raise Unexpected(f"ec2 {words[1]} {options}")
        ident = options[option][0]
        entry = next((entry for entry in world["tagged"] if entry["arn"].endswith(f":{kind}/{ident}")), None)
        # "describe_knows": how many more describes still find a resource the destroy removed, as EC2
        # does for a while after a deletion; absent, the resource stands.
        if entry is None or entry.get("describe_knows", 1) <= 0:
            raise ServiceError(missing, words[1])
        if "describe_knows" in entry:
            entry["describe_knows"] -= 1
        return [ident]
    if command == "acm delete-certificate":
        arn = options["certificate-arn"]
        # ACM counts a certificate in use for a while after the listener that held it is deleted:
        # the AWS provider retries this error on its own deletes (terraform-provider-aws v6.68.0,
        # internal/service/acm/certificate.go).
        in_use = world["faults"].get("certificate_in_use", 0)
        if in_use:
            world["faults"]["certificate_in_use"] = in_use - 1
            raise ServiceError("ResourceInUseException", "DeleteCertificate")
        if not any(entry["arn"] == arn for entry in world["tagged"]) or world["faults"].get("certificate_gone"):
            world["tagged"] = [entry for entry in world["tagged"] if entry["arn"] != arn]
            raise ServiceError("ResourceNotFoundException", "DeleteCertificate")
        world["tagged"] = [entry for entry in world["tagged"] if entry["arn"] != arn]
        return None
    if command == "ecs list-task-definitions":
        if query != "taskDefinitionArns" or options.get("status") != "INACTIVE":
            raise Unexpected(f"list-task-definitions {options}")
        prefix = options["family-prefix"]
        return [td_arn(name) for name, definition in sorted(world["task_definitions"].items())
                if name.startswith(prefix) and definition["status"] == "INACTIVE"]
    if command == "ecs delete-task-definitions":
        names = [task_definition_name(reference) for reference in options["task-definitions"]]
        if len(names) > 10:
            # https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_DeleteTaskDefinitions.html
            raise ServiceError("InvalidParameterException", "DeleteTaskDefinitions")
        for name in names:
            world["task_definitions"][name]["status"] = "DELETE_IN_PROGRESS"
            world["tagged"] = [entry for entry in world["tagged"] if entry["arn"] != td_arn(name)]
        return None
    if command == "dynamodb describe-table":
        # A table DynamoDB does not know is ResourceNotFoundException; one being deleted answers
        # DELETING until it goes. "describe_knows" counts the describes that still find it, as for EC2.
        # https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_DescribeTable.html
        if query != "Table.TableStatus":
            raise Unexpected(f"describe-table --query {query!r}")
        name = options.get("table-name", "")
        entry = next((entry for entry in world["tagged"]
                      if entry["type"] == "dynamodb:table" and entry["arn"].endswith(f":table/{name}")), None)
        if entry is None or entry.get("describe_knows", 1) <= 0:
            raise ServiceError("ResourceNotFoundException", "DescribeTable")
        if "describe_knows" in entry:
            entry["describe_knows"] -= 1
        return entry.get("status", "ACTIVE")
    if command == "cognito-idp describe-user-pool":
        # A pool Cognito does not know is ResourceNotFoundException (#466). "describe_knows" counts the
        # describes that still find it, as for EC2.
        # https://docs.aws.amazon.com/cognito-user-identity-pools/latest/APIReference/API_DescribeUserPool.html
        if query != "UserPool.Id":
            raise Unexpected(f"describe-user-pool --query {query!r}")
        ident = options.get("user-pool-id", "")
        entry = next((entry for entry in world["tagged"]
                      if entry["type"] == "cognito-idp:userpool" and entry["arn"].endswith(f":userpool/{ident}")), None)
        if entry is None or entry.get("describe_knows", 1) <= 0:
            raise ServiceError("ResourceNotFoundException", "DescribeUserPool")
        if "describe_knows" in entry:
            entry["describe_knows"] -= 1
        return ident
    if command == "ssm get-parameters-by-path":
        # The path is a hierarchy and takes no trailing slash; --recursive reads below its first
        # level. The CLI pages on its own and --query applies to the whole answer.
        # https://docs.aws.amazon.com/systems-manager/latest/APIReference/API_GetParametersByPath.html
        path = options.get("path", "")
        if query != "Parameters[].Name" or options.get("recursive") is not True or path.endswith("/"):
            raise Unexpected(f"get-parameters-by-path {options}")
        return [name for name in world["parameters"] if name.startswith(path + "/")]
    if command == "ssm delete-parameters":
        # At most ten names a call; a name that is not there comes back under InvalidParameters, not
        # as an error. https://docs.aws.amazon.com/systems-manager/latest/APIReference/API_DeleteParameters.html
        names = options.get("names", [])
        if query != "length(DeletedParameters)" or not names:
            raise Unexpected(f"delete-parameters {options}")
        if len(names) > 10:
            raise ServiceError("ValidationException", "DeleteParameters")
        deleted = [name for name in names if name in world["parameters"]]
        world["parameters"] = [name for name in world["parameters"] if name not in names]
        world["tagged"] = [entry for entry in world["tagged"]
                           if not (entry["type"] == "ssm:parameter" and entry["arn"].split(":parameter", 1)[-1] in names)]
        return len(deleted)
    raise Unexpected(f"aws {command} is not a call these scripts make")


def aws_double(argv: list[str], case: pathlib.Path) -> int:
    world = json.loads((case / "world.json").read_text())
    record = {"tool": "aws", "argv": argv,
              "as": "teardown" if os.environ.get("AWS_ACCESS_KEY_ID") == TEARDOWN_KEY else "operator"}
    status = 0
    try:
        words, options = parse_aws(argv)
        record.update(command=" ".join(command_words(words)), positional=words[len(command_words(words)):],
                      options=options)
        if options.get("region") != world["region"]:
            raise Unexpected(f"--region {options.get('region')!r}, not the case's {world['region']}")
        if words != ["ecr", "get-login-password"] and options.get("output") != "text":
            raise Unexpected("--output text is what the scripts read, whatever the profile says")
        value = aws_answer(world, words, options, case, sys.stdout)
        if isinstance(value, Unexpected):
            raise value
        record["outcome"] = "success"
        if value is not None:
            sys.stdout.write(text(value))
    except ServiceError as error:
        record["outcome"] = error.code
        # What the CLI prints, with the caller's ARN in the message as AWS writes it, so that a
        # script that printed the message would fail the account-id universal.
        sys.stderr.write(f"\nAn error occurred ({error.code}) when calling the {error.operation} operation: "
                         f"User: {OPERATOR_ARN} is not authorized: {error.message}\n")
        status = 254
    except Unexpected as error:
        record["outcome"] = "unexpected"
        record["unexpected"] = str(error)
        sys.stderr.write(f"aws double: unexpected call: {error}\n")
        status = 97
    except SystemExit as stop:
        record["outcome"] = "waiter failed"
        status = int(stop.code)
    log(case, record)
    save_world(case, world)
    return status


def save_world(case: pathlib.Path, world: dict):
    """Writes world.json whole, by rename. Two doubles can run at once: deploy.sh pipes the aws double
    into the docker double for the ECR login, and each reads world.json as it starts and writes it as
    it ends. Written in place, a reader could open it between the truncation and the write and fail
    to parse it; a rename replaces the file in one step, so a reader sees the old world or the new one
    (os.replace: https://docs.python.org/3/library/os.html#os.replace). Neither double changes the
    world on those two calls, so the later rename loses no write. The staged name is the writer's own,
    so two writers never share one."""
    staged = case / f"world.json.{os.getpid()}"
    staged.write_text(json.dumps(world, indent=1))
    os.replace(staged, case / "world.json")


def log(case: pathlib.Path, record: dict):
    with open(case / "calls.jsonl", "a") as handle:
        handle.write(json.dumps(record) + "\n")


ROOT_NAMES = {"infra/envs/poc/idp": "idp", "infra/envs/poc/apps": "apps", "infra/envs/poc/platform": "platform"}


def terraform_double(argv: list[str], case: pathlib.Path) -> int:
    world = json.loads((case / "world.json").read_text())
    record = {"tool": "terraform", "argv": argv}
    status = 0
    try:
        if not argv or not argv[0].startswith("-chdir="):
            raise Unexpected("every terraform call names its root with -chdir")
        directory = pathlib.Path(argv[0][len("-chdir="):]).resolve()
        relative = directory.relative_to(world_root(case)).as_posix()
        root = ROOT_NAMES.get(relative)
        if root is None:
            raise Unexpected(f"-chdir={relative} is not one of the three roots")
        command, rest = argv[1], argv[2:]
        record.update(root=root, command=command)
        variables = {}
        for index, token in enumerate(rest):
            if token == "-var":
                name, _, value = rest[index + 1].partition("=")
                variables[name] = value
        record["vars"] = variables
        outputs = world["roots"][root]
        if command == "output":
            if world["faults"].get(f"output {root}"):
                sys.stderr.write("Error: No outputs found\n")
                status = 1
            elif "-json" in rest:
                if "-no-color" not in rest:
                    raise Unexpected("terraform output without -no-color")
                sys.stdout.write(json.dumps({name: {"sensitive": False, "type": "string", "value": value}
                                             for name, value in (outputs or {}).items()}, indent=2) + "\n")
            elif "-raw" in rest:
                name = rest[-1]
                if not outputs or name not in outputs:
                    sys.stderr.write(f'Error: Output "{name}" not found\n')
                    status = 1
                else:
                    sys.stdout.write(str(outputs[name]))
            else:
                raise Unexpected(f"terraform output {rest}")
        elif command == "plan":
            if root != "platform" or "-input=false" not in rest or not any(t.startswith("-out=") for t in rest):
                raise Unexpected(f"terraform plan {rest}")
            plan = next(t for t in rest if t.startswith("-out="))[len("-out="):]
            pathlib.Path(plan).write_text(json.dumps(variables))
            # The task definition's container_definitions as a real plan shows them: the mock API's
            # environment carries both apps' client ids (MockApi__App1Client and MockApi__App2Client
            # in infra/modules/cell/ecs.tf), which the platform root reads from the apps root's state
            # and the scripts never pass, and the module's variable for them is not sensitive (#251).
            clients = (world["roots"]["apps"] or {}).get("clients", {})
            sys.stdout.write(f"  # module.cell.aws_ecs_task_definition.gateway must be replaced\n"
                             f"      ~ image = \"{variables.get('gateway_image')}\"\n")
            if variables.get("mock_api_image"):
                sys.stdout.write(f"      ~ image = \"{variables['mock_api_image']}\"\n")
                for app, name in (("app1", "MockApi__App1Client"), ("app2", "MockApi__App2Client")):
                    if app in clients:
                        sys.stdout.write(f"          + {{ name = \"{name}\", value = \"{clients[app]['clientId']}\" }},\n")
            sys.stdout.write("Plan: 1 to add, 1 to change, 1 to destroy.\n")
            if world["faults"].get("plan"):
                status = 1
        elif command == "apply":
            plan = rest[-1]
            if root != "platform" or "-input=false" not in rest or "-auto-approve" in rest:
                raise Unexpected(f"terraform apply {rest}: the saved plan is what is applied")
            variables = json.loads(pathlib.Path(plan).read_text())
            record["vars"] = variables
            if world["faults"].get("apply"):
                sys.stderr.write("Error: creating ECS Task Definition: ClientException\n")
                status = 1
            else:
                apply_platform(world, variables)
                sys.stdout.write(f"Apply complete! Resources: 1 added, 1 changed, 1 destroyed.\n\nOutputs:\n\n"
                                 f"bucket = \"{outputs['bucket']}\"\ngateway_image = \"{variables['gateway_image']}\"\n")
        elif command == "destroy":
            if "-auto-approve" not in rest or "-input=false" not in rest:
                raise Unexpected(f"terraform destroy {rest}")
            destroy_root(world, root, sys.stdout)
            if world["faults"].get(f"destroy {root}"):
                status = 1
        else:
            raise Unexpected(f"terraform {command}")
    except Unexpected as error:
        record["unexpected"] = str(error)
        sys.stderr.write(f"terraform double: unexpected call: {error}\n")
        status = 97
    record["status"] = status
    log(case, record)
    save_world(case, world)
    return status


def world_root(case: pathlib.Path) -> pathlib.Path:
    return pathlib.Path((case / "repo-root").read_text()).resolve()


def apply_platform(world: dict, variables: dict):
    """What an apply of the platform root does to the cell when the images change: a new task
    definition revision, the service moved to it and the old one deregistered, a new task, and the
    health check on the path given. A circuit-breaker fault leaves the service on the old one.

    The target group then holds what the sitting saw on #58 (2026-10-21, #207) once the service is
    stable: the new task's target healthy, and the previous task's target draining for the group's
    deregistration delay. A target that was already draining has finished deregistering and is gone."""
    revisions = [int(name.split(":")[1]) for name in world["task_definitions"]]
    name = f"{FAMILY}:{max(revisions) + 1}"
    containers = {GATEWAY: {"image": variables["gateway_image"], "environment": environment(world["roots"]["platform"]["bucket"])}}
    if variables.get("mock_api_image"):
        containers[MOCK_API] = {"image": variables["mock_api_image"], "environment": []}
    previous = world["service"]["taskDefinition"]
    world["task_definitions"][name] = {"status": "ACTIVE", "containers": containers}
    world["task_definitions"][previous]["status"] = "INACTIVE"
    world["tagged"].append({"arn": td_arn(name), "type": "ecs:task-definition", "root": "platform",
                            "tags": {"Platform": "spa-platform", "Cell": CELL}})
    # Each fault below leaves one thing the apply should have moved where it was, so that a case can
    # show the check in cell-common.sh's wait_and_assert (which deploy.sh and rollback.sh run) that
    # reads it is the one that catches it.
    if not world["faults"].get("output_image_stale"):
        world["roots"]["platform"]["gateway_image"] = variables["gateway_image"]
    if not world["faults"].get("health_path_stale"):
        world["target_group"]["path"] = variables.get("health_check_path", "/")
    if world["faults"].get("circuit_breaker"):
        # The new tasks failed their health checks and ECS put the previous task definition back:
        # stable, on the old one.
        world["service"] = {"taskDefinition": previous, "rolloutState": "COMPLETED", "deployments": 1}
        return
    # "deployments": the previous deployment not yet drained, so the service lists two.
    world["service"] = {"taskDefinition": name, "rolloutState": world["faults"].get("rollout", "COMPLETED"),
                        "deployments": world["faults"].get("deployments", 1)}
    world["next_task"] += 1
    digest = variables["gateway_image"].split("@")[1] if "@" in variables["gateway_image"] else "sha256:none"
    # "running_definition": a task running the applied digest under another revision, as one started
    # from a task definition another apply registered with the same image would.
    world["tasks"] = [{"id": f"{world['next_task']:032x}", "lastStatus": "RUNNING",
                       "taskDefinition": previous if world["faults"].get("running_definition") else name,
                       "gatewayDigest": world["faults"].get("running_digest", digest)}]
    # "extra_tasks": more tasks of the applied deployment, each with its own healthy target, as a
    # cell that runs more than one task has (P2-4).
    for _ in range(world["faults"].get("extra_tasks", 0)):
        world["next_task"] += 1
        world["tasks"].append({"id": f"{world['next_task']:032x}", "lastStatus": "RUNNING", "taskDefinition": name,
                               "gatewayDigest": digest})
    # "new_target": the new task's target in another state than healthy, or None for not registered.
    # "extra_targets": more targets in the group, as [id, state] pairs.
    group = world["target_group"]
    group["targets"] = [{"id": target["id"], "state": "draining"}
                        for target in group["targets"] if target["state"] != "draining"]
    new_state = world["faults"].get("new_target", "healthy")
    if new_state is not None:
        group["targets"].append({"id": task_ip(world["tasks"][0]), "state": new_state})
    group["targets"] += [{"id": task_ip(task), "state": "healthy"} for task in world["tasks"][1:]]
    group["targets"] += [{"id": ident, "state": state} for ident, state in world["faults"].get("extra_targets", [])]


# One resource of each root, as `terraform destroy -auto-approve -no-color` names it: first in the
# plan it prints, every attribute going to null, ARNs among them, and then on the line that destroys
# it, by its id. The account id is in each, as it is in the real output, so a destroy whose output
# skipped the redaction fails the account-id universal (#191). The addresses are sample text, written
# out by hand: no case reads one, so nothing guards on them.
# https://developer.hashicorp.com/terraform/cli/commands/destroy (destroy shows its plan, as plan -destroy)
DESTROYED = {
    "idp": ("module.idp.aws_cognito_user_pool.this",
            f"arn:aws:cognito-idp:{REGION}:{ACCOUNT}:userpool/{REGION}_EXAMPLE", f"{REGION}_EXAMPLE"),
    "apps": ('aws_ssm_parameter.client_secret["app1"]',
             f"arn:aws:ssm:{REGION}:{ACCOUNT}:parameter/spa-platform/{CELL}/apps/app1/client-secret",
             f"/spa-platform/{CELL}/apps/app1/client-secret"),
    "platform": ("module.cell.aws_s3_bucket.platform", f"arn:aws:s3:::{BUCKET}", BUCKET),
}


def destroy_root(world: dict, root: str, stdout):
    if world["roots"][root] is None:
        # A root with no state plans nothing, so names nothing.
        stdout.write("No changes. No objects need to be destroyed.\n\n")
        address = None
    else:
        address, arn, ident = DESTROYED[root]
        stdout.write(f"Terraform will perform the following actions:\n\n"
                     f"  # {address} will be destroyed\n"
                     f"  - resource {{\n      - arn = \"{arn}\" -> null\n      - id  = \"{ident}\" -> null\n    }}\n\n"
                     f"Plan: 0 to add, 0 to change, 30 to destroy.\n"
                     f"{address}: Destroying... [id={ident}]\n")
    if root == "platform":
        if world["objects"]:
            # S3 refuses to delete a bucket that holds any version or delete marker.
            # https://docs.aws.amazon.com/AmazonS3/latest/API/API_DeleteBucket.html
            stdout.write(f"Error: deleting S3 Bucket ({(world['roots']['platform'] or {}).get('bucket', BUCKET)}): BucketNotEmpty\n")
            world["faults"]["destroy platform"] = True
            return
        for definition in world["task_definitions"].values():
            if definition["status"] == "ACTIVE":
                definition["status"] = "INACTIVE"
        world["service"] = None
        world["tasks"] = []
    world["roots"][root] = None
    # What #118 step 2 read after the platform root's destroy: the tagging index went on listing
    # ECS's INACTIVE cluster and service and EC2's deleted security groups and rules. An entry with
    # "after_destroy" stays listed, changed as it says ({"status": "INACTIVE"}, {"describe_knows": 0}).
    kept = []
    for entry in world["tagged"]:
        if entry.get("root") != root or entry["type"] == "ecs:task-definition":
            kept.append(entry)
        elif "after_destroy" in entry:
            entry.update(entry.pop("after_destroy"))
            kept.append(entry)
    world["tagged"] = kept
    world["roles"] = {name: role for name, role in world.get("roles", {}).items() if role.get("root") != root}
    if address:
        stdout.write(f"{address}: Destruction complete after 1s\n\nDestroy complete! Resources: 30 destroyed.\n")
    else:
        stdout.write("Destroy complete! Resources: 0 destroyed.\n")


def docker_double(argv: list[str], case: pathlib.Path) -> int:
    world = json.loads((case / "world.json").read_text())
    record = {"tool": "docker", "argv": argv}
    status = 0
    if argv == ["buildx", "version"]:
        sys.stdout.write("github.com/docker/buildx v0.29.1 a1b2c3d\n")
    elif argv[:1] == ["login"]:
        given = sys.stdin.read()
        record["password_on_stdin"] = given == PASSWORD
        record["host"] = argv[-1]
        if world["faults"].get("login"):
            # What Docker prints when the registry refuses the login: the registry's URL, whose host
            # carries the account id. The daemon's "login attempt to %s failed with status: %d %s",
            # https://github.com/moby/moby/blob/master/daemon/pkg/registry/auth.go
            sys.stderr.write(f"Error response from daemon: login attempt to https://{argv[-1]}/v2/ "
                             "failed with status: 400 Bad Request\n")
            status = 1
        else:
            sys.stdout.write("Login Succeeded\n")
    elif argv[:2] == ["buildx", "build"]:
        tag = argv[argv.index("--tag") + 1]
        record["tag"] = tag
        repository, _, image_tag = tag.rpartition(":")
        name = repository.split("/", 1)[1]
        if world["faults"].get("build"):
            sys.stderr.write("ERROR: failed to build: exit code 1\n")
            status = 1
        elif image_tag in world["repositories"][name]:
            sys.stderr.write(f"ERROR: tag invalid: The image tag '{image_tag}' already exists in the '{name}' repository and cannot be overwritten because the tag is immutable.\n")
            status = 1
        else:
            world["repositories"][name][image_tag] = digest_of(tag)
            sys.stdout.write(f"#12 pushing {tag} with docker\n#12 DONE 4.2s\n")
    else:
        record["unexpected"] = True
        sys.stderr.write(f"docker double: unexpected call: {argv}\n")
        status = 97
    record["status"] = status
    log(case, record)
    save_world(case, world)
    return status


def spactl_wait(world: dict, args: list[str], stdout, stderr) -> int:
    """`spactl wait --url <origin> --revision <N> --instances <id,...>` (tools/spactl/Commands.cs,
    WaitAsync), with each running task answering /readyz through the load balancer. Every task serves
    the registry's live revision, but "behind": n leaves the last n tasks one revision short, and
    "unsampled": n means the load balancer never hands a read to the last n. It answers as spactl
    does: 0 and a Ready line when every listed id answered at N or later, else 1 and the timeout that
    names each id behind or never answered (InstanceWait.Behind). A listed id that is no task's, an
    empty element or a missing option is not a call the scripts make."""
    options = {}
    for index in range(1, len(args), 2):
        if not args[index].startswith("--") or index + 1 >= len(args) or args[index] in options:
            raise Unexpected(f"spactl {args}")
        options[args[index]] = args[index + 1]
    if set(options) - {"--url", "--revision", "--instances", "--timeout"} or not {"--url", "--revision", "--instances"} <= set(options):
        raise Unexpected(f"spactl {args}")
    if not re.fullmatch(r"https?://[^/]+", options["--url"]) or not re.fullmatch(r"[1-9][0-9]*", options["--revision"]):
        raise Unexpected(f"spactl wait {options}")
    listed = options["--instances"].split(",")
    tasks = [task["id"] for task in world["tasks"]]
    if any(not re.fullmatch(r"[0-9a-f]{32}", ident) or ident not in tasks for ident in listed):
        raise Unexpected(f"spactl wait --instances {options['--instances']}: not the running tasks' ids")
    live = (world["registry"] or {}).get("revision", 0)
    behind = tasks[len(tasks) - world["faults"].get("behind", 0):]
    unsampled = tasks[len(tasks) - world["faults"].get("unsampled", 0):]
    revision = int(options["--revision"])
    readyz = options["--url"] + "/readyz"
    answered = {ident: (None if ident in unsampled else live - 1 if ident in behind else live) for ident in listed}
    short = [(ident, at) for ident, at in answered.items() if at is None or at < revision]
    if not short:
        summary = ", ".join(f"instance {ident} at revision {at}" for ident, at in answered.items())
        stdout.write(f"Ready: {readyz} reports revision {revision} or later from {summary}.\n")
        return 0
    named = "; ".join(f"instance {ident} never answered" if at is None else f"instance {ident} last answered revision {at}"
                      for ident, at in short)
    stderr.write(f"{readyz} did not report revision {revision} or later from every listed instance within "
                 f"{options.get('--timeout', '300')} seconds: {named}.\n")
    return 1


def spactl_change(world: dict, args: list[str], record: dict) -> int:
    """`spactl release publish` and `spactl app add`, the two commands seed.sh runs."""
    status = 0
    options = {args[i]: args[i + 1] for i in range(len(args)) if args[i].startswith("--") and i + 1 < len(args)}
    outputs = json.loads(pathlib.Path(options["--outputs"]).read_text())
    bucket = outputs["bucket"]
    if args[:2] == ["release", "publish"]:
        app, directory, release = args[2:5]
        root = pathlib.Path(directory)
        for file in sorted(p for p in root.rglob("*") if p.is_file()):
            key = f"apps/{app}/releases/{release}/{file.relative_to(root).as_posix()}"
            if not any(entry["Key"] == key for entry in world["objects"]):
                world["objects"].append({"Key": key, "VersionId": f"v{len(world['objects'])}"})
        sys.stdout.write(f"Published apps/{app}/releases/{release}/ to {bucket}.\n")
    elif args[:2] == ["app", "add"]:
        definition = json.loads(pathlib.Path(args[2]).read_text())
        config = json.loads(pathlib.Path(options["--config"]).read_text())
        record["config"] = config
        app = definition["appId"]
        if app not in outputs["clients"]:
            sys.stderr.write(f"The infrastructure outputs name no client for '{app}'.\n")
            status = 1
        else:
            registry = world["registry"] or {"schemaVersion": 1, "revision": 0, "apps": []}
            registry["apps"] = [entry for entry in registry["apps"] if entry["appId"] != app]
            registry["apps"].append({"appId": app, "release": options["--release"]})
            registry["revision"] += 1
            world["registry"] = registry
            world["objects"].append({"Key": "registry/registry.json", "VersionId": f"r{registry['revision']}"})
            sys.stdout.write(f"Added {app} at release {options['--release']}. Revision {registry['revision']}.\n")
    else:
        raise Unexpected(f"spactl {args}")
    return status


def spactl_double(argv: list[str], case: pathlib.Path) -> int:
    """`dotnet run --project <tools/spactl> -- <spactl arguments>`, answering as spactl does for the
    two commands seed.sh runs, with the bucket's objects and registry in the world, and for the wait
    deploy.sh runs (spactl_wait)."""
    world = json.loads((case / "world.json").read_text())
    record = {"tool": "dotnet", "argv": argv}
    status = 0
    try:
        if argv[:2] != ["run", "--project"] or argv[3] != "--":
            raise Unexpected(f"dotnet {argv}")
        record["project"] = argv[2]
        args = argv[4:]
        record["spactl"] = args
        if args[:1] == ["wait"]:
            status = spactl_wait(world, args, sys.stdout, sys.stderr)
        else:
            status = spactl_change(world, args, record)
    except Unexpected as error:
        record["unexpected"] = str(error)
        sys.stderr.write(f"dotnet double: unexpected call: {error}\n")
        status = 97
    record["status"] = status
    log(case, record)
    save_world(case, world)
    return status


DOUBLES = {"aws": aws_double, "terraform": terraform_double, "docker": docker_double, "dotnet": spactl_double}

if __name__ == "__main__" and sys.argv[1:2] == ["--double"]:
    sys.exit(DOUBLES[sys.argv[2]](sys.argv[4:], pathlib.Path(sys.argv[3])))

import importlib.util  # noqa: E402
import shlex  # noqa: E402 -- below the doubles, which start once per call
import shutil  # noqa: E402
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


class Case:
    """One throwaway directory: a copy of the scripts and the fixtures they read in a repository of
    its own, a bin holding the doubles and a way to each real tool, which is the case's whole PATH,
    and the world beside them."""

    def __init__(self, test: unittest.TestCase, *, world: dict | None = None, outputs: bool = False):
        self.test = test
        scratch = tempfile.TemporaryDirectory()
        test.addCleanup(scratch.cleanup)
        self.dir = pathlib.Path(scratch.name).resolve()
        self.bin = self.dir / "bin"
        self.home = self.dir / "home"
        self.repo = self.dir / "repo"
        for directory in (self.bin, self.home):
            directory.mkdir()
        # The scripts find the repository from their own path, so each case runs a copy in a tree of
        # its own: what they write (outputs.json, images.auto.tfvars) lands in the case, never here.
        for relative in ("tools/scripts", "fixtures/spa", "fixtures/apps", "contracts"):
            shutil.copytree(ROOT / relative, self.repo / relative,
                            ignore=shutil.ignore_patterns("__pycache__", "*_tests.py", "cell_doubles.py"))
        for relative in ROOT_NAMES:
            (self.repo / relative).mkdir(parents=True, exist_ok=True)
        (self.dir / "repo-root").write_text(str(self.repo))
        for tool in TOOLS:
            found = shutil.which(tool)
            test.assertIsNotNone(found, f"{tool} is required to run this companion and is not on PATH")
            self.write_executable(tool, f'#!/bin/sh\nexec {shlex.quote(os.path.realpath(found))} "$@"\n')
        for name in DOUBLES:
            self.write_executable(name, (
                f"#!/bin/sh\nexec {shlex.quote(os.path.realpath(sys.executable))} -I -S "
                f"{shlex.quote(str(pathlib.Path(__file__).resolve()))} --double {name} "
                f"{shlex.quote(str(self.dir))} \"$@\"\n"))
        self.world = world if world is not None else default_world()
        self.save()
        if outputs:
            self.write_outputs()

    def write_outputs(self):
        """infra/envs/poc/outputs.json as seed.sh leaves it, from the world's roots, by the real
        assembler in cell_documents.py: deploy.sh and rollback.sh read the cell's names from it and
        never from state (contracts/infra-outputs.schema.json). Nothing is written while a root has
        no state, as seed.sh could not have written it then."""
        roots = self.world["roots"]
        if not all(roots.get(name) for name in ("idp", "apps", "platform")):
            return
        spec = importlib.util.spec_from_file_location("cell_documents", SCRIPTS / "cell_documents.py")
        documents = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(documents)
        paths = []
        for name in ("idp", "apps", "platform"):
            path = self.dir / f"{name}-outputs.json"
            path.write_text(json.dumps({key: {"value": value} for key, value in roots[name].items()}))
            paths.append(str(path))
        documents.outputs(self.world["region"], *paths, str(self.repo / "infra/envs/poc/outputs.json"))

    def write_executable(self, name: str, content: str):
        path = self.bin / name
        path.write_text(content)
        path.chmod(0o755)

    def remove(self, name: str):
        (self.bin / name).unlink()

    def save(self):
        save_world(self.dir, self.world)

    def load(self) -> dict:
        return json.loads((self.dir / "world.json").read_text())

    def script(self, name: str) -> pathlib.Path:
        return self.repo / "tools" / "scripts" / name

    def env(self, **extra: str | None) -> dict[str, str]:
        env = {"PATH": str(self.bin), "HOME": str(self.home), "LC_ALL": "C",
               "AWS_PROFILE": "spa-platform", "AWS_REGION": REGION, "TMPDIR": str(self.dir)}
        env.update(extra)
        return {name: value for name, value in env.items() if value is not None}

    def run(self, name: str, *args: str, env: dict[str, str] | None = None) -> Result:
        completed = subprocess.run([BASH, str(self.script(name)), *args], cwd=self.dir, env=env or self.env(),
                                   stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=120)
        result = Result(completed)
        for secret in SECRETS:
            self.test.assertNotIn(secret, result.output, "the script printed the account id, a password or a credential")
        # Nor into a file the script leaves in the repository or the home directory. The account id
        # is not searched for here: outputs.json carries the bucket's name, which holds it.
        for tree in (self.repo, self.home):
            for path in tree.rglob("*"):
                if path.is_file():
                    content = path.read_bytes()
                    for secret in CREDENTIALS:
                        self.test.assertNotIn(secret.encode(), content,
                                              f"the script wrote a password or a credential to {path.relative_to(self.dir)}")
        return result

    def calls(self, tool: str | None = None) -> list[dict]:
        path = self.dir / "calls.jsonl"
        records = [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []
        return [record for record in records if tool is None or record["tool"] == tool]

    def aws(self, command: str) -> list[dict]:
        return [call for call in self.calls("aws") if call.get("command") == command]

    def unexpected(self) -> list[dict]:
        return [call for call in self.calls() if call.get("outcome") == "unexpected" or call.get("unexpected")]


class CompanionTestCase(unittest.TestCase):
    def assertNoUnexpectedCall(self, case: Case):
        self.assertEqual([], case.unexpected(), "a script made a call the doubles do not answer")


def bash_banner(name: str):
    full = subprocess.run([BASH, "-c", 'printf "%s" "$BASH_VERSION"'], env={"PATH": "/usr/bin:/bin"},
                          capture_output=True, text=True, check=True).stdout
    print(f"{name}: the scripts run under {BASH} {full}", file=sys.stderr)


def main(name: str) -> int:
    required = os.environ.get(REQUIRE_BASH)
    if required:
        version = subprocess.run([BASH, "-c", 'printf "%s.%s" "${BASH_VERSINFO[0]}" "${BASH_VERSINFO[1]}"'],
                                 env={"PATH": "/usr/bin:/bin"}, capture_output=True, text=True, check=True).stdout
        if version != required:
            print(f"{name}: {REQUIRE_BASH}={required}, but {BASH} is {version}", file=sys.stderr)
            return 1
    # tools/companion_runner.py's runner: the cases at the same time, reported as a serial run. Each
    # case keeps to its own directory and passes its own environment, which is what it asks of one.
    import importlib.util
    spec = importlib.util.spec_from_file_location("companion_runner", ROOT / "tools" / "companion_runner.py")
    runner = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(runner)
    try:
        runner.jobs_for(os.environ, os.cpu_count())
    except ValueError as error:
        print(f"companion_runner: {error}.", file=sys.stderr)
        return 2
    program = unittest.main(module="__main__", testRunner=runner.Runner, exit=False)
    return 0 if program.result.wasSuccessful() else 1
