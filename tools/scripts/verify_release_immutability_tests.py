#!/usr/bin/env python3
"""Test companion for tools/scripts/verify-release-immutability.sh. Owner: workstream 1, AWS and IaC.

Built in #122, which asks for the script #92's grilling recommended and #98 confirmed (Q20, Q21,
Q25 to Q30 and Q32 there): P-04 against the applied platform bucket, one line per case and a
non-zero exit on any unexpected answer, refusing to start as the teardown role, with the
teardown-role cleanup behind a flag that is off by default. The ticket says what a lane can prove
without AWS: the case list and the exit codes against a stub, and that the role check refuses. This
is that stub, and the run against the applied bucket is the sitting's. The tool choice is D-3 in
docs/dependencies.md, and the shape is tools/preflight_tests.py's.

**What runs is the script itself**, in place: `/bin/bash tools/scripts/verify-release-immutability.sh`,
the tracked file's current bytes, through the interface any caller has -- its arguments, PATH and
the environment. /bin/bash rather than the shebang, so a Mac with Homebrew's bash first on PATH
still tests the 3.2 that macOS ships; the companion prints that bash's version first, and
SCRIPT_TESTS_REQUIRE_BASH fails the run when /bin/bash is another version, as CI's macOS job sets it.

**A constructed environment, inheriting nothing.** PATH is the case's bin directory alone: a
one-line exec of each real tool the script reads its answers with, and three doubles, for aws,
terraform and date. HOME is the case's own, so the AWS_PROFILE the cases set, spa-platform, names a
profile no file there defines, and no case can reach AWS: there is no real aws on PATH to reach it
with.

**The doubles are this file's own functions.** Each is a two-line executable in the case's bin that
runs this file as `--double aws|terraform|date <case directory> <its arguments>`, so a double is
ordinary Python rather than a program held in a string, and stops before the imports only the cases
need, since it starts once per call. The AWS double keeps the case's AWS in world.json -- the
platform bucket's versions, delete markers and open uploads, its settings and its policy, the two
repositories and the teardown role -- and answers each call as S3, STS, IAM and ECR document it, as
the identity the call's environment resolves to. It answers only the call shapes the script uses
and records every call; anything else is recorded and exits 97, so a new AWS call in the script
turns a case red instead of being answered.

**Expected answers come from the ticket and from AWS's documents, never from the script.** The
case list below is #122's, in its order. The codes are S3's: 403 AccessDenied and 412
PreconditionFailed (https://docs.aws.amazon.com/AmazonS3/latest/API/ErrorResponses.html#ErrorCodeList).
Each mutation of the world -- a policy statement missing, a setting off, a role that may write -- is
a bucket that would fail at the sitting, and its case asserts the script says so.

**A universal held on every run:** nothing the script prints names the account id, the caller's
session name or a credential. Its output goes into the landing merge's body in a public repository
(#122), change request 1.5 keeps account identifiers out of the tracked tree, and AGENTS.md forbids
logging a credential. The double's access-denied messages carry the caller's ARN, as S3's do for a
same-account request, so a script that printed S3's message would fail here.

Run: python3 -I tools/scripts/verify_release_immutability_tests.py
"""

from __future__ import annotations

import hashlib
import json
import os
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent
SCRIPT = ROOT / "tools" / "scripts" / "verify-release-immutability.sh"
PLATFORM_ROOT = ROOT / "infra" / "envs" / "poc" / "platform"
REGISTRY_SCHEMA = ROOT / "contracts" / "registry.schema.json"
BASH = "/bin/bash"
REQUIRE_BASH = "SCRIPT_TESTS_REQUIRE_BASH"
PROGRAM = "verify-release-immutability"

# The placeholder account the contracts' examples use, and a session name of the kind a federated
# console session carries. Neither may appear in anything the script prints.
ACCOUNT = "111122223333"
SESSION = "adam.medford@example.com"
REGION = "us-east-2"
CELL = "poc"
BUCKET = f"spa-platform-{CELL}-{ACCOUNT}-{REGION}-an"
REPOSITORIES = (f"spa-{CELL}/gateway", f"spa-{CELL}/mock-api")
OPERATOR_ARN = f"arn:aws:sts::{ACCOUNT}:assumed-role/AccountFullAccessRole/{SESSION}"
TEARDOWN_ROLE = f"spa-platform-{CELL}-teardown"
TEARDOWN_ROLE_ARN = f"arn:aws:iam::{ACCOUNT}:role/{TEARDOWN_ROLE}"

# What assume-role hands out. Canaries: none may appear in anything the script prints.
TEARDOWN_KEY = "ASIAQCANARYKEY000001"
TEARDOWN_SECRET = "canary/Secret+AccessKey0000000000000000"
TEARDOWN_TOKEN = "canary-session-token/IQoJb3JpZ2luX2VjEOr0000+=="
SECRETS = (ACCOUNT, SESSION, TEARDOWN_KEY, TEARDOWN_SECRET, TEARDOWN_TOKEN)

# The real tools the script reads its answers with. Everything else it could look for is absent.
TOOLS = ("awk", "cat", "dirname", "mktemp", "rm", "sed", "tr")

# The first clock reading each case's date double answers, and the next after it.
CLOCK = ("20261019T031500Z", "20261019T031501Z")

# #122's cases, in its order: a fragment of the case's line, in the ticket's words, and what S3
# answers when the bucket is as task 1.5 means it. Written out from the ticket and from S3's error
# code list, never from the script, which is the thing under test.
P04_CASES = (
    ("first PUT with If-None-Match: *", "succeeded"),
    ("second PUT with If-None-Match: *", "412 PreconditionFailed"),
    ("PUT without If-None-Match", "403 AccessDenied"),
    ("PUT with only If-Match", "403 AccessDenied"),
    ("DELETE with no version id", "403 AccessDenied"),
    ("DELETE of the version", "403 AccessDenied"),
    ("exactly one version and no delete marker", "1 version, 0 delete markers"),
    ("after the delete attempts", "412 PreconditionFailed"),
    ("CopyObject onto the key", "403 AccessDenied"),
    ("multipart upload completed without If-None-Match", "403 AccessDenied"),
    ("multipart upload is aborted", "succeeded"),
)
READ_BACKS = (
    ("versioning", "Enabled"),
    ("public access block settings", "True True True True"),
    ("policy status", "IsPublic False"),
    ("lifecycle configuration", "404 NoSuchLifecycleConfiguration"),
    ("replication configuration", "404 ReplicationConfigurationNotFoundError"),
    ("plain-HTTP request", "403 AccessDenied"),
    (REPOSITORIES[0], "IMMUTABLE"),
    (REPOSITORIES[1], "IMMUTABLE"),
)
# Behind the flag, after every case above is recorded (#98's Q29): the role is assumed in a
# separate session, cannot write, deletes the probe's one version, and leaves nothing behind.
CLEAN_UP_CASES = (
    (f"{TEARDOWN_ROLE} is assumed in a separate session", "succeeded"),
    ("teardown role cannot write", "403 AccessDenied"),
    ("teardown role deletes version", "succeeded"),
    ("nothing is left under the probe's release prefix", "0 versions, 0 delete markers"),
)
FLAG = "--clean-up-as-teardown-role"


# --- the world: the case's AWS ---------------------------------------------------------------------

def default_world() -> dict:
    """The applied cell as task 1.5 means it: infra/modules/cell/storage.tf's bucket and policy and
    its two repositories, and infra/bootstrap's teardown role, trusted by the operator's role."""
    return {
        "region": REGION,
        "caller": OPERATOR_ARN,
        "caller_writes": True,
        "bucket": BUCKET,
        # The four statements of storage.tf's policy that P-04 and the TLS read-back observe.
        "policy": {
            "require_if_none_match": True,   # DenyReleaseWriteWithoutIfNoneMatch
            "deny_deletes": True,            # DenyReleaseDeleteExceptTeardown
            "exempts_teardown_role": True,   # ...whose ArnNotLike names the teardown role's ARN
            "tls_only": True,                # DenyInsecureTransport
        },
        "versioning": "Enabled",
        "public_access_block": {"BlockPublicAcls": True, "IgnorePublicAcls": True,
                                "BlockPublicPolicy": True, "RestrictPublicBuckets": True},
        "is_public": False,
        "lifecycle": False,
        "replication": False,
        "repositories": {name: "IMMUTABLE" for name in REPOSITORIES},
        "teardown": {"exists": True, "trusts_caller": True, "writes": False},
        "objects": {},
        "uploads": {},
        "counter": 0,
    }


def teardown_session_arn(session: str = "p04-cleanup") -> str:
    return f"arn:aws:sts::{ACCOUNT}:assumed-role/{TEARDOWN_ROLE}/{session}"


# --- the AWS double ------------------------------------------------------------------------------

class Unexpected(Exception):
    """A call the double does not answer: recorded, and exit 97."""


class ServiceError(Exception):
    """A service error, printed as the CLI prints one."""

    def __init__(self, code: str, message: str):
        super().__init__(code)
        self.code = code
        self.message = message


# Options every call may carry, and the options each command takes: (required, optional). The
# shapes are the AWS CLI reference's (https://awscli.amazonaws.com/v2/documentation/api/latest/reference/index.html).
GLOBAL_OPTIONS = {"region", "output", "query", "endpoint-url"}
COMMANDS = {
    ("sts", "get-caller-identity"): (set(), set()),
    ("sts", "assume-role"): ({"role-arn", "role-session-name"}, set()),
    ("iam", "get-role"): ({"role-name"}, set()),
    ("s3api", "put-object"): ({"bucket", "key", "body"}, {"content-type", "if-none-match", "if-match"}),
    ("s3api", "delete-object"): ({"bucket", "key"}, {"version-id"}),
    ("s3api", "list-object-versions"): ({"bucket", "prefix"}, set()),
    ("s3api", "copy-object"): ({"bucket", "key", "copy-source"}, {"metadata-directive", "content-type"}),
    ("s3api", "create-multipart-upload"): ({"bucket", "key"}, {"content-type"}),
    ("s3api", "upload-part"): ({"bucket", "key", "upload-id", "part-number", "body"}, set()),
    ("s3api", "complete-multipart-upload"): ({"bucket", "key", "upload-id", "multipart-upload"}, {"if-none-match"}),
    ("s3api", "abort-multipart-upload"): ({"bucket", "key", "upload-id"}, set()),
    ("s3api", "get-bucket-versioning"): ({"bucket"}, set()),
    ("s3api", "get-public-access-block"): ({"bucket"}, set()),
    ("s3api", "get-bucket-policy-status"): ({"bucket"}, set()),
    ("s3api", "get-bucket-lifecycle-configuration"): ({"bucket"}, set()),
    ("s3api", "get-bucket-replication"): ({"bucket"}, set()),
    ("ecr", "describe-repositories"): ({"repository-names"}, set()),
}

OPERATIONS = {
    "get-caller-identity": "GetCallerIdentity", "assume-role": "AssumeRole", "get-role": "GetRole",
    "put-object": "PutObject", "delete-object": "DeleteObject", "list-object-versions": "ListObjectVersions",
    "copy-object": "CopyObject", "create-multipart-upload": "CreateMultipartUpload",
    "upload-part": "UploadPart", "complete-multipart-upload": "CompleteMultipartUpload",
    "abort-multipart-upload": "AbortMultipartUpload", "get-bucket-versioning": "GetBucketVersioning",
    "get-public-access-block": "GetPublicAccessBlock", "get-bucket-policy-status": "GetBucketPolicyStatus",
    "get-bucket-lifecycle-configuration": "GetBucketLifecycleConfiguration",
    "get-bucket-replication": "GetBucketReplication", "describe-repositories": "DescribeRepositories",
}

# The one query whose text carries a value: the probe key's versions and delete markers, counted.
COUNT_QUERY = re.compile(r"^\[length\(Versions\[\?Key=='(?P<key>[^']+)'\] \|\| `\[\]`\), "
                         r"length\(DeleteMarkers\[\?Key=='(?P=key)'\] \|\| `\[\]`\)\]$")


def parse(argv: list[str]) -> tuple[str, str, dict[str, str]]:
    """`aws [--option value]... <service> <command> [--option value]...`: every option the script
    uses takes a value."""
    words: list[str] = []
    options: dict[str, str] = {}
    index = 0
    while index < len(argv):
        token = argv[index]
        if token.startswith("--"):
            if index + 1 >= len(argv):
                raise Unexpected(f"{token} has no value")
            name = token[2:]
            if name in options:
                raise Unexpected(f"{token} given twice")
            options[name] = argv[index + 1]
            index += 2
        else:
            words.append(token)
            index += 1
    if len(words) != 2:
        raise Unexpected(f"expected a service and a command, got {words}")
    service, command = words
    if (service, command) not in COMMANDS:
        raise Unexpected(f"aws {service} {command} is not a call the script makes")
    required, optional = COMMANDS[(service, command)]
    missing = required - options.keys()
    unknown = options.keys() - required - optional - GLOBAL_OPTIONS
    if missing or unknown:
        raise Unexpected(f"aws {service} {command}: missing {sorted(missing)}, unknown {sorted(unknown)}")
    return service, command, options


def text(value) -> str:
    """--output text as awscli/text.py writes a query's result: a scalar on its own line, a list of
    scalars tab-separated, a list of lists one line each, an empty list as nothing, and Python's
    str() for True, False and None."""
    if isinstance(value, list):
        if not value:
            return ""
        if all(isinstance(element, list) for element in value):
            return "".join(text(element) for element in value)
        return "\t".join(str(element) for element in value) + "\n"
    return f"{value}\n"


def who(arn: str) -> str:
    return "teardown" if f":assumed-role/{TEARDOWN_ROLE}/" in arn else "operator"


def identity(world: dict, env: dict) -> str:
    """The ARN a call runs as. The credentials assume-role handed out, when the environment carries
    them, as the CLI's environment provider reads them; otherwise the case's caller, whom the
    profile would resolve to (https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-envvars.html)."""
    if (env.get("AWS_ACCESS_KEY_ID") == TEARDOWN_KEY and env.get("AWS_SECRET_ACCESS_KEY") == TEARDOWN_SECRET
            and env.get("AWS_SESSION_TOKEN") == TEARDOWN_TOKEN):
        return teardown_session_arn()
    return world["caller"]


def denied(arn: str, action: str, resource: str, explicit: bool, policy: str = "resource-based") -> ServiceError:
    """S3's access-denied message for a same-account request, which names the caller's ARN:
    https://docs.aws.amazon.com/AmazonS3/latest/userguide/troubleshoot-403-errors.html."""
    reason = (f"with an explicit deny in a {policy} policy" if explicit
              else f"because no identity-based policy allows the {action} action")
    return ServiceError("AccessDenied",
                        f"User: {arn} is not authorized to perform: {action} on resource: \"{resource}\" {reason}")


def etag_of(path: str) -> str:
    return '"' + hashlib.md5(pathlib.Path(path).read_bytes()).hexdigest() + '"'


def new_id(world: dict, prefix: str) -> str:
    world["counter"] += 1
    return f"{prefix}{world['counter']:04d}"


def current(world: dict, key: str) -> dict | None:
    versions = world["objects"].get(key, [])
    return versions[-1] if versions else None


def s3(world: dict, arn: str, command: str, options: dict[str, str], plain_http: bool):
    bucket = options["bucket"]
    if bucket != world["bucket"]:
        raise ServiceError("NoSuchBucket", "The specified bucket does not exist")
    bucket_arn = f"arn:aws:s3:::{bucket}"
    policy = world["policy"]
    if plain_http and policy["tls_only"]:
        raise denied(arn, "s3:GetBucketVersioning", bucket_arn, explicit=True)
    role = who(arn)
    key = options.get("key", "")
    object_arn = f"{bucket_arn}/{key}"
    release = key.startswith("apps/")

    def may_write():
        if role == "teardown" and not world["teardown"]["writes"]:
            raise denied(arn, "s3:PutObject", object_arn, explicit=False)
        if role == "operator" and not world["caller_writes"]:
            raise denied(arn, "s3:PutObject", object_arn, explicit=False)

    def conditional_write_required(header_given: bool):
        # DenyReleaseWriteWithoutIfNoneMatch: Null s3:if-none-match, and s3:ObjectCreationOperation,
        # which is true for PutObject, CopyObject and CompleteMultipartUpload and false for the
        # calls that start and fill an upload:
        # https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes-enforce.html
        if release and policy["require_if_none_match"] and not header_given:
            raise denied(arn, "s3:PutObject", object_arn, explicit=True)

    def may_delete():
        if role == "teardown":
            if not (release and policy["exempts_teardown_role"]):
                raise denied(arn, "s3:DeleteObject", object_arn, explicit=True)
        elif release and policy["deny_deletes"]:
            raise denied(arn, "s3:DeleteObject", object_arn, explicit=True)

    def write_version(etag: str) -> dict:
        version = {"id": new_id(world, "v"), "etag": etag, "marker": False}
        world["objects"].setdefault(key, []).append(version)
        return version

    if command == "put-object":
        may_write()
        conditional_write_required("if-none-match" in options)
        now = current(world, key)
        if "if-none-match" in options:
            if options["if-none-match"] != "*":
                raise Unexpected("If-None-Match takes '*' and nothing else")
            # A conditional write succeeds over a delete marker:
            # https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-requests.html
            if now is not None and not now["marker"]:
                raise ServiceError("PreconditionFailed", "At least one of the pre-conditions you specified did not hold")
        if "if-match" in options:
            if now is None or now["marker"]:
                raise ServiceError("NoSuchKey", "The specified key does not exist.")
            if options["if-match"] != now["etag"]:
                raise ServiceError("PreconditionFailed", "At least one of the pre-conditions you specified did not hold")
        version = write_version(etag_of(options["body"]))
        return {"ETag": version["etag"], "VersionId": version["id"], "ServerSideEncryption": "AES256"}

    if command == "delete-object":
        may_delete()
        versions = world["objects"].setdefault(key, [])
        if "version-id" in options:
            versions[:] = [version for version in versions if version["id"] != options["version-id"]]
            if not versions:
                del world["objects"][key]
            return {"VersionId": options["version-id"]}
        marker = {"id": new_id(world, "m"), "etag": "", "marker": True}
        versions.append(marker)
        return {"DeleteMarker": True, "VersionId": marker["id"]}

    if command == "list-object-versions":
        prefix = options["prefix"]
        response: dict = {"Name": bucket, "Prefix": prefix}
        entries = [(name, version) for name in sorted(world["objects"]) if name.startswith(prefix)
                   for version in world["objects"][name]]
        versions = [{"Key": name, "VersionId": version["id"], "ETag": version["etag"]}
                    for name, version in entries if not version["marker"]]
        markers = [{"Key": name, "VersionId": version["id"]} for name, version in entries if version["marker"]]
        if versions:
            response["Versions"] = versions
        if markers:
            response["DeleteMarkers"] = markers
        return response

    if command == "copy-object":
        may_write()
        # A bucket policy that enforces conditional writes refuses a copy without the header:
        # https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes-enforce.html
        conditional_write_required(False)
        source_bucket, _, source_key = options["copy-source"].partition("/")
        source = current(world, source_key) if source_bucket == bucket else None
        if source is None or source["marker"]:
            raise ServiceError("NoSuchKey", "The specified key does not exist.")
        if source_key == key and options.get("metadata-directive") != "REPLACE":
            raise ServiceError("InvalidRequest", "This copy request is illegal because it is trying to copy an "
                               "object to itself without changing the object's metadata, storage class, website "
                               "redirect location or encryption attributes.")
        version = write_version(source["etag"])
        return {"CopyObjectResult": {"ETag": version["etag"]}, "VersionId": version["id"]}

    if command == "create-multipart-upload":
        may_write()
        upload = new_id(world, "upload-")
        world["uploads"][upload] = {"key": key, "parts": {}}
        return {"Bucket": bucket, "Key": key, "UploadId": upload}

    if command in ("upload-part", "complete-multipart-upload", "abort-multipart-upload"):
        upload = world["uploads"].get(options["upload-id"])
        if upload is None or upload["key"] != key:
            raise ServiceError("NoSuchUpload", "The specified upload does not exist.")
        if command == "upload-part":
            may_write()
            etag = etag_of(options["body"])
            upload["parts"][options["part-number"]] = etag
            return {"ETag": etag, "ServerSideEncryption": "AES256"}
        if command == "abort-multipart-upload":
            if world.get("abort_failures", 0) > 0:
                world["abort_failures"] -= 1
                raise ServiceError("InternalError", "We encountered an internal error. Please try again.")
            del world["uploads"][options["upload-id"]]
            return {}
        may_write()
        conditional_write_required("if-none-match" in options)
        try:
            parts = json.loads(options["multipart-upload"])["Parts"]
        except (ValueError, KeyError, TypeError):
            raise Unexpected("--multipart-upload is not the JSON shape the CLI takes")
        listed = {str(part["PartNumber"]): part["ETag"].strip('"') for part in parts}
        uploaded = {number: etag.strip('"') for number, etag in upload["parts"].items()}
        if listed != uploaded:
            raise ServiceError("InvalidPart", "One or more of the specified parts could not be found.")
        del world["uploads"][options["upload-id"]]
        version = write_version('"' + hashlib.md5("".join(sorted(uploaded.values())).encode()).hexdigest() + '-1"')
        return {"Bucket": bucket, "Key": key, "ETag": version["etag"], "VersionId": version["id"]}

    if command == "get-bucket-versioning":
        return {"Status": world["versioning"]} if world["versioning"] else {}
    if command == "get-public-access-block":
        return {"PublicAccessBlockConfiguration": dict(world["public_access_block"])}
    if command == "get-bucket-policy-status":
        return {"PolicyStatus": {"IsPublic": world["is_public"]}}
    if command == "get-bucket-lifecycle-configuration":
        if not world["lifecycle"]:
            raise ServiceError("NoSuchLifecycleConfiguration", "The lifecycle configuration does not exist")
        return {"Rules": [{"ID": "expire", "Status": "Enabled", "Expiration": {"Days": 30}}]}
    if command == "get-bucket-replication":
        if not world["replication"]:
            raise ServiceError("ReplicationConfigurationNotFoundError", "The replication configuration was not found")
        return {"ReplicationConfiguration": {"Role": "arn", "Rules": []}}
    raise Unexpected(f"s3api {command}")


def answer(world: dict, arn: str, service: str, command: str, options: dict[str, str]):
    if service == "sts" and command == "get-caller-identity":
        return {"UserId": "AROAEXAMPLE:" + arn.rsplit("/", 1)[-1], "Account": ACCOUNT, "Arn": arn}
    if service == "iam":
        if options["role-name"] != TEARDOWN_ROLE or not world["teardown"]["exists"]:
            raise ServiceError("NoSuchEntity", f"The role with name {options['role-name']} cannot be found.")
        return {"Role": {"RoleName": TEARDOWN_ROLE, "Arn": TEARDOWN_ROLE_ARN, "Path": "/"}}
    if service == "sts":
        if (options["role-arn"] != TEARDOWN_ROLE_ARN or not world["teardown"]["exists"]
                or not world["teardown"]["trusts_caller"] or who(arn) != "operator"):
            raise ServiceError("AccessDenied", f"User: {arn} is not authorized to perform: sts:AssumeRole on "
                               f"resource: {options['role-arn']}")
        return {"Credentials": {"AccessKeyId": TEARDOWN_KEY, "SecretAccessKey": TEARDOWN_SECRET,
                                "SessionToken": TEARDOWN_TOKEN, "Expiration": "2026-10-19T04:15:00+00:00"},
                "AssumedRoleUser": {"Arn": teardown_session_arn(options["role-session-name"])}}
    if service == "ecr":
        name = options["repository-names"]
        if name not in world["repositories"]:
            raise ServiceError("RepositoryNotFoundException",
                               f"The repository with name '{name}' does not exist in the registry")
        return {"repositories": [{"repositoryName": name, "imageTagMutability": world["repositories"][name]}]}
    plain_http = False
    if "endpoint-url" in options:
        if (command, options["endpoint-url"]) != ("get-bucket-versioning", f"http://s3.{world['region']}.amazonaws.com"):
            raise Unexpected(f"--endpoint-url {options['endpoint-url']} on {command}")
        plain_http = True
    return s3(world, arn, command, options, plain_http)


def apply_query(command: str, query: str | None, response: dict):
    """The queries the script asks, evaluated as JMESPath would evaluate them on these responses."""
    if query is None:
        return None
    queries = {
        ("get-caller-identity", "[Account, Arn]"): lambda r: [r["Account"], r["Arn"]],
        ("get-role", "Role.Arn"): lambda r: r["Role"]["Arn"],
        ("assume-role", "Credentials.[AccessKeyId, SecretAccessKey, SessionToken]"):
            lambda r: [r["Credentials"][name] for name in ("AccessKeyId", "SecretAccessKey", "SessionToken")],
        ("put-object", "[ETag, VersionId]"): lambda r: [r["ETag"], r["VersionId"]],
        ("create-multipart-upload", "UploadId"): lambda r: r["UploadId"],
        ("upload-part", "ETag"): lambda r: r["ETag"],
        ("get-bucket-versioning", "Status"): lambda r: r.get("Status"),
        ("get-public-access-block",
         "PublicAccessBlockConfiguration.[BlockPublicAcls, IgnorePublicAcls, BlockPublicPolicy, RestrictPublicBuckets]"):
            lambda r: [r["PublicAccessBlockConfiguration"][name] for name in
                       ("BlockPublicAcls", "IgnorePublicAcls", "BlockPublicPolicy", "RestrictPublicBuckets")],
        ("get-bucket-policy-status", "PolicyStatus.IsPublic"): lambda r: r["PolicyStatus"]["IsPublic"],
        ("describe-repositories", "repositories[0].imageTagMutability"):
            lambda r: r["repositories"][0]["imageTagMutability"],
        ("list-object-versions", "[Versions, DeleteMarkers][].[Key, VersionId]"):
            lambda r: [[entry["Key"], entry["VersionId"]] for entry in r.get("Versions", []) + r.get("DeleteMarkers", [])],
        ("list-object-versions", "[length(Versions || `[]`), length(DeleteMarkers || `[]`)]"):
            lambda r: [len(r.get("Versions", [])), len(r.get("DeleteMarkers", []))],
    }
    if (command, query) in queries:
        return queries[(command, query)](response)
    counted = COUNT_QUERY.match(query)
    if command == "list-object-versions" and counted:
        key = counted["key"]
        return [sum(1 for entry in response.get("Versions", []) if entry["Key"] == key),
                sum(1 for entry in response.get("DeleteMarkers", []) if entry["Key"] == key)]
    raise Unexpected(f"--query {query!r} on {command}")


def aws_double(argv: list[str], case: pathlib.Path) -> int:
    world_path = case / "world.json"
    world = json.loads(world_path.read_text())
    arn = identity(world, os.environ)
    record = {"argv": argv, "as": who(arn), "profile": os.environ.get("AWS_PROFILE"),
              "checksums": os.environ.get("AWS_REQUEST_CHECKSUM_CALCULATION")}
    status = 0
    if world.get("no_credentials"):
        # What the CLI prints, and its exit status, when nothing resolves a credential: 253 is "the
        # system environment or configuration was invalid" (`aws help return-codes`).
        sys.stderr.write('\nUnable to locate credentials. You can configure credentials by running "aws configure".\n')
        with open(case / "aws-calls.jsonl", "a") as log:
            log.write(json.dumps(dict(record, outcome="no credentials")) + "\n")
        return 253
    try:
        service, command, options = parse(argv)
        record.update(service=service, command=command, options=options)
        if options.get("region") != world["region"]:
            raise Unexpected(f"--region {options.get('region')!r}, not the case's {world['region']}")
        if options.get("output") != "text":
            raise Unexpected("--output text is what the script reads, whatever the profile says")
        response = answer(world, arn, service, command, options)
        record["outcome"] = "success"
        value = apply_query(command, options.get("query"), response)
        if value is not None or "query" in options:
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
    world_path.write_text(json.dumps(world, indent=1))
    with open(case / "aws-calls.jsonl", "a") as log:
        log.write(json.dumps(record) + "\n")
    return status


# --- the terraform and date doubles ----------------------------------------------------------------

def terraform_double(argv: list[str], case: pathlib.Path) -> int:
    """`terraform -chdir=<platform root> output -no-color -raw <name>`, answered from the case's
    outputs as -raw prints a string: the value and no newline. A missing output fails as Terraform
    fails, which is what a storage-only targeted apply leaves for an output it did not write."""
    outputs = json.loads((case / "terraform.json").read_text())
    with open(case / "terraform-calls.jsonl", "a") as log:
        log.write(json.dumps(argv) + "\n")
    if len(argv) != 5 or argv[:4] != [f"-chdir={PLATFORM_ROOT}", "output", "-no-color", "-raw"]:
        sys.stderr.write(f"terraform double: unexpected call: {argv}\n")
        return 97
    name = argv[4]
    if name not in outputs:
        sys.stderr.write(f"\nError: Output \"{name}\" not found\n\nThe output variable requested could not be "
                         "found in the state file.\n")
        return 1
    sys.stdout.write(outputs[name])
    return 0


def date_double(argv: list[str], case: pathlib.Path) -> int:
    """The clock: `date -u +%Y%m%dT%H%M%SZ`, answering the case's readings in turn."""
    clock_path = case / "clock.json"
    readings = json.loads(clock_path.read_text())
    if argv != ["-u", "+%Y%m%dT%H%M%SZ"] or not readings:
        sys.stderr.write(f"date double: unexpected call: {argv}\n")
        return 97
    clock_path.write_text(json.dumps(readings[1:]))
    sys.stdout.write(readings[0] + "\n")
    return 0


DOUBLES = {"aws": aws_double, "terraform": terraform_double, "date": date_double}

# A double runs once for every call the script makes, so it runs this file as a program and stops
# here, before the imports only the cases need: `<this file> --double <name> <case directory> <args>`.
if __name__ == "__main__" and sys.argv[1:2] == ["--double"]:
    sys.exit(DOUBLES[sys.argv[2]](sys.argv[4:], pathlib.Path(sys.argv[3])))

import shlex  # noqa: E402 -- below the doubles, for the reason above
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
        self.lines = completed.stdout.splitlines()

    def cases(self) -> list[tuple[str, str]]:
        """Each case line, as (pass | FAIL, the rest of the line)."""
        found = []
        for line in self.lines:
            match = re.match(r"^(pass|FAIL)  (.*)$", line)
            if match:
                found.append((match[1], match[2]))
        return found

    def verdict(self) -> str:
        return self.lines[-1] if self.lines else ""


class Case:
    """One throwaway directory: a bin holding the doubles and a way to each real tool, which is the
    case's whole PATH, and the case's world, outputs and clock beside it."""

    def __init__(self, test: unittest.TestCase, *, world: dict | None = None, outputs: dict | None = None,
                 clock: tuple[str, ...] = CLOCK, tools: tuple[str, ...] = TOOLS,
                 doubles: tuple[str, ...] = tuple(DOUBLES)):
        self.test = test
        scratch = tempfile.TemporaryDirectory()
        test.addCleanup(scratch.cleanup)
        self.dir = pathlib.Path(scratch.name).resolve()
        self.bin = self.dir / "bin"
        self.home = self.dir / "home"
        for directory in (self.bin, self.home):
            directory.mkdir()
        for tool in tools:
            found = shutil.which(tool)
            test.assertIsNotNone(found, f"{tool} is required to run this companion and is not on PATH")
            self.write_executable(tool, f'#!/bin/sh\nexec {shlex.quote(os.path.realpath(found))} "$@"\n')
        for name in doubles:
            # -S as well as -I: a double needs nothing from site-packages, and it starts per call.
            self.write_executable(name, (
                f"#!/bin/sh\nexec {shlex.quote(os.path.realpath(sys.executable))} -I -S "
                f"{shlex.quote(str(pathlib.Path(__file__).resolve()))} --double {name} "
                f"{shlex.quote(str(self.dir))} \"$@\"\n"))
        self.world = world if world is not None else default_world()
        self.save_world()
        default_outputs = {"bucket": BUCKET,
                           "ecr_gateway": f"{ACCOUNT}.dkr.ecr.{REGION}.amazonaws.com/{REPOSITORIES[0]}",
                           "ecr_mock_api": f"{ACCOUNT}.dkr.ecr.{REGION}.amazonaws.com/{REPOSITORIES[1]}"}
        (self.dir / "terraform.json").write_text(json.dumps(default_outputs if outputs is None else outputs))
        (self.dir / "clock.json").write_text(json.dumps(list(clock)))

    def write_executable(self, name: str, content: str):
        path = self.bin / name
        path.write_text(content)
        path.chmod(0o755)

    def save_world(self):
        (self.dir / "world.json").write_text(json.dumps(self.world, indent=1))

    def load_world(self) -> dict:
        return json.loads((self.dir / "world.json").read_text())

    def env(self, **extra: str) -> dict[str, str]:
        env = {"PATH": str(self.bin), "HOME": str(self.home), "LC_ALL": "C",
               "AWS_PROFILE": "spa-platform", "AWS_REGION": REGION, "TMPDIR": str(self.dir)}
        env.update(extra)
        return {name: value for name, value in env.items() if value is not None}

    def run(self, *args: str, env: dict[str, str] | None = None, script: str = str(SCRIPT),
            cwd: pathlib.Path | None = None) -> Result:
        completed = subprocess.run([BASH, script, *args], cwd=cwd or self.dir, env=env or self.env(),
                                   stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=120)
        result = Result(completed)
        printed = result.stdout + result.stderr
        for secret in SECRETS:
            self.test.assertNotIn(secret, printed, "the script printed something it must never print")
        return result

    def aws_calls(self) -> list[dict]:
        path = self.dir / "aws-calls.jsonl"
        return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []

    def terraform_calls(self) -> list[list[str]]:
        path = self.dir / "terraform-calls.jsonl"
        return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []


def bash_version() -> str:
    return subprocess.run([BASH, "-c", 'printf "%s.%s" "${BASH_VERSINFO[0]}" "${BASH_VERSINFO[1]}"'],
                          env={"PATH": "/usr/bin:/bin"}, capture_output=True, text=True, check=True).stdout


def setUpModule():
    full = subprocess.run([BASH, "-c", 'printf "%s" "$BASH_VERSION"'], env={"PATH": "/usr/bin:/bin"},
                          capture_output=True, text=True, check=True).stdout
    print(f"verify_release_immutability_tests: {SCRIPT.name} runs under {BASH} {full}", file=sys.stderr)


class CompanionTestCase(unittest.TestCase):
    def assertNoUnexpectedCall(self, case: Case):
        unexpected = [call for call in case.aws_calls() if call.get("outcome") == "unexpected"]
        self.assertEqual([], unexpected, "the script made an AWS call the double does not answer")

    def line_of(self, result: Result, fragment: str) -> tuple[int, str, str]:
        """The one case line naming `fragment`: (its index among the case lines, pass | FAIL, text)."""
        found = [(index, status, line) for index, (status, line) in enumerate(result.cases()) if fragment in line]
        self.assertEqual(1, len(found), f"expected one case line naming {fragment!r}, got {found}\n{result.stdout}")
        return found[0]

    def assertCases(self, result: Result, expected: tuple[tuple[str, str], ...], failing: tuple[str, ...] = ()):
        """Every expected case has its one line, in order; each passes and shows S3's answer, except
        the `failing` ones, which say FAIL."""
        last = -1
        for fragment, answer in expected:
            index, status, line = self.line_of(result, fragment)
            self.assertGreater(index, last, f"{fragment!r} is out of the ticket's order\n{result.stdout}")
            last = index
            if fragment in failing:
                self.assertEqual("FAIL", status, line)
            else:
                self.assertEqual("pass", status, line)
                self.assertIn(answer, line)
        for fragment in failing:
            self.assertIn(fragment, [item[0] for item in expected], "a failing case the test does not list")


# --- the cases -------------------------------------------------------------------------------------

class RefusalTests(CompanionTestCase):
    """It refuses to start as the teardown role (#122; ADR-0014: P-04 never runs as it), before it
    reads the platform root's state or touches the bucket."""

    def assertRefusedBeforeAnything(self, case: Case, result: Result):
        self.assertEqual(2, result.status, result.stderr)
        self.assertIn("teardown role", result.stderr)
        self.assertEqual(["get-caller-identity"], [call["command"] for call in case.aws_calls()])
        self.assertEqual([], case.terraform_calls())

    def test_a_session_of_the_cells_teardown_role(self):
        world = default_world()
        world["caller"] = teardown_session_arn(SESSION)
        case = Case(self, world=world)
        self.assertRefusedBeforeAnything(case, case.run())

    def test_a_session_of_the_teardown_role_with_the_cleanup_flag(self):
        world = default_world()
        world["caller"] = teardown_session_arn(SESSION)
        case = Case(self, world=world)
        self.assertRefusedBeforeAnything(case, case.run("--clean-up-as-teardown-role"))

    def test_a_session_of_another_cells_teardown_role(self):
        # The name follows the cell (ADR-0014 item 2), and a --cell that names the wrong one must not
        # let a teardown session through: any spa-platform-<cell>-teardown is refused.
        world = default_world()
        world["caller"] = f"arn:aws:sts::{ACCOUNT}:assumed-role/spa-platform-dev-teardown/{SESSION}"
        case = Case(self, world=world)
        self.assertRefusedBeforeAnything(case, case.run())

    def test_the_ci_plan_role_is_not_mistaken_for_it(self):
        world = default_world()
        world["caller"] = f"arn:aws:sts::{ACCOUNT}:assumed-role/spa-platform-{CELL}-ci-plan/{SESSION}"
        case = Case(self, world=world)
        result = case.run()
        self.assertNotIn("teardown role", result.stderr)
        self.assertIn("put-object", [call["command"] for call in case.aws_calls()])


class ExpectedBucketTests(CompanionTestCase):
    """The bucket as task 1.5 means it: every case passes, one line each, and the run exits 0."""

    def test_every_case_has_one_line_in_the_tickets_order_and_the_run_exits_0(self):
        case = Case(self)
        result = case.run()
        self.assertEqual(0, result.status, result.stdout + result.stderr)
        self.assertNoUnexpectedCall(case)
        self.assertCases(result, P04_CASES + READ_BACKS)
        self.assertEqual(len(P04_CASES) + len(READ_BACKS), len(result.cases()), result.stdout)
        self.assertTrue(result.verdict().startswith(f"{PROGRAM}: pass:"), result.verdict())

    def test_the_probe_key_is_under_an_appid_no_registry_entry_names_and_matches_the_schema(self):
        case = Case(self)
        result = case.run()
        key = f"apps/p04-probe/releases/{CLOCK[0]}/index.html"
        self.assertIn(f"probe key {key}", result.stdout)
        written = {call["options"]["key"] for call in case.aws_calls() if call.get("command") == "put-object"}
        self.assertEqual({key}, written)
        # Both segments match the registry's own patterns (#92's Q11, Q28 on #98), read from the
        # schema rather than copied, so the probe stays a key a registry entry could name.
        definitions = json.loads(REGISTRY_SCHEMA.read_text())["$defs"]
        app_id, release = key.split("/")[1], key.split("/")[3]
        self.assertRegex(app_id, definitions["appId"]["pattern"])
        self.assertTrue(definitions["appId"]["minLength"] <= len(app_id) <= definitions["appId"]["maxLength"])
        self.assertRegex(release, definitions["release"]["pattern"])
        self.assertTrue(definitions["release"]["minLength"] <= len(release) <= definitions["release"]["maxLength"])

    def test_the_documented_relative_invocation_finds_the_platform_root_with_cdpath_exported(self):
        # The header's own form, from the repository root. With CDPATH set, cd resolves a relative
        # path through it and prints where it went, so the root the script captured once held a
        # second line, and the bucket output was read from nowhere (PR #130's review).
        for cdpath in (".", str(ROOT), ""):
            with self.subTest(CDPATH=cdpath):
                case = Case(self)
                result = case.run(script=str(SCRIPT.relative_to(ROOT)), cwd=ROOT, env=case.env(CDPATH=cdpath))
                self.assertEqual(0, result.status, result.stdout + result.stderr)
                self.assertEqual({f"-chdir={PLATFORM_ROOT}"}, {call[0] for call in case.terraform_calls()})
                self.assertTrue(result.verdict().startswith(f"{PROGRAM}: pass:"), result.verdict())

    def test_the_bucket_and_repositories_come_from_the_platform_roots_outputs(self):
        case = Case(self)
        case.run()
        self.assertEqual({"bucket", "ecr_gateway", "ecr_mock_api"}, {call[-1] for call in case.terraform_calls()})
        self.assertEqual({BUCKET}, {call["options"]["bucket"] for call in case.aws_calls() if "bucket" in call.get("options", {})})
        self.assertEqual(set(REPOSITORIES), {call["options"]["repository-names"] for call in case.aws_calls()
                                             if call.get("command") == "describe-repositories"})

    def test_without_the_flag_nothing_acts_as_the_teardown_role_and_the_probe_stays(self):
        # P-04 stays negative-only unless the flag is given (#98's Q32; ADR-0014).
        case = Case(self)
        self.assertEqual(0, case.run().status)
        commands = [call["command"] for call in case.aws_calls()]
        self.assertNotIn("assume-role", commands)
        self.assertNotIn("get-role", commands)
        self.assertEqual({"operator"}, {call["as"] for call in case.aws_calls()})
        world = case.load_world()
        self.assertEqual([f"apps/p04-probe/releases/{CLOCK[0]}/index.html"], list(world["objects"]))
        self.assertEqual(1, len(world["objects"][f"apps/p04-probe/releases/{CLOCK[0]}/index.html"]))
        self.assertEqual({}, world["uploads"], "the multipart upload was left open")

    def test_the_writes_carry_no_checksum_the_cli_would_add_by_default(self):
        # Since AWS CLI 2.23 upload-part and put-object send a CRC64NVME checksum unless told
        # otherwise, and a completion that lists the part without it is a request whose answer on real
        # S3 could turn on the checksum before the policy. when_required leaves the policy as the only
        # thing that decides the multipart case (#122's Q26).
        # https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-envvars.html
        case = Case(self)
        case.run()
        writes = [call for call in case.aws_calls()
                  if call.get("command") in ("put-object", "upload-part", "complete-multipart-upload")]
        self.assertTrue(writes)
        self.assertEqual({"when_required"}, {call["checksums"] for call in writes})

    def test_a_rerun_writes_a_fresh_key_beside_the_probe_an_earlier_run_left(self):
        case = Case(self)
        self.assertEqual(0, case.run().status)
        second = case.run()
        self.assertEqual(0, second.status, second.stdout + second.stderr)
        self.assertEqual({f"apps/p04-probe/releases/{reading}/index.html" for reading in CLOCK},
                         set(case.load_world()["objects"]))


class UnexpectedBucketTests(CompanionTestCase):
    """A bucket that would fail at the sitting. Each world below lacks one statement or setting task
    1.5 needs; the script must say FAIL for exactly the cases it breaks, keep going, and exit 1."""

    def run_world(self, change) -> tuple[Case, Result]:
        world = default_world()
        change(world)
        case = Case(self, world=world)
        result = case.run()
        self.assertEqual(1, result.status, result.stdout + result.stderr)
        self.assertNoUnexpectedCall(case)
        self.assertTrue(result.verdict().startswith(f"{PROGRAM}: fail:"), result.verdict())
        return case, result

    def test_without_the_conditional_write_rule_every_unconditional_write_fails_its_case(self):
        case, result = self.run_world(lambda world: world["policy"].update(require_if_none_match=False))
        self.assertCases(result, P04_CASES + READ_BACKS, failing=(
            "PUT without If-None-Match", "PUT with only If-Match", "exactly one version and no delete marker",
            "CopyObject onto the key", "multipart upload completed without If-None-Match",
            "multipart upload is aborted"))
        self.assertIn("expected 403 AccessDenied, got succeeded", self.line_of(result, "PUT without If-None-Match")[2])

    def test_without_the_delete_rule_a_delete_followed_by_a_write_fails(self):
        # The row's second half. S3 accepts a conditional write over a delete marker, which is why the
        # count and the last write are cases of their own.
        case, result = self.run_world(lambda world: world["policy"].update(deny_deletes=False))
        self.assertCases(result, P04_CASES + READ_BACKS, failing=(
            "DELETE with no version id", "DELETE of the version", "exactly one version and no delete marker",
            "after the delete attempts"))
        self.assertIn("got 0 versions, 1 delete marker",
                      self.line_of(result, "exactly one version and no delete marker")[2])
        self.assertIn("expected 412 PreconditionFailed, got succeeded", self.line_of(result, "after the delete attempts")[2])

    def test_each_read_back_fails_alone_when_its_property_is_missing(self):
        changes = {
            "versioning": lambda world: world.update(versioning="Suspended"),
            "public access block settings": lambda world: world["public_access_block"].update(BlockPublicPolicy=False),
            "policy status": lambda world: world.update(is_public=True),
            "lifecycle configuration": lambda world: world.update(lifecycle=True),
            "replication configuration": lambda world: world.update(replication=True),
            "plain-HTTP request": lambda world: world["policy"].update(tls_only=False),
            REPOSITORIES[0]: lambda world: world["repositories"].update({REPOSITORIES[0]: "MUTABLE"}),
            REPOSITORIES[1]: lambda world: world["repositories"].pop(REPOSITORIES[1]),
        }
        self.assertEqual({fragment for fragment, _ in READ_BACKS}, set(changes), "a read-back without a mutation")
        for fragment, change in changes.items():
            with self.subTest(fragment):
                case, result = self.run_world(change)
                self.assertCases(result, P04_CASES + READ_BACKS, failing=(fragment,))

    def test_a_first_write_that_fails_runs_no_other_probe_case_and_says_so(self):
        # Every refusal is read against the probe key, so with no key a 403 would prove nothing.
        case, result = self.run_world(lambda world: world.update(caller_writes=False))
        self.assertCases(result, P04_CASES + READ_BACKS, failing=tuple(fragment for fragment, _ in P04_CASES))
        for fragment, _ in P04_CASES[1:]:
            self.assertIn("not run, because", self.line_of(result, fragment)[2])
        commands = [call["command"] for call in case.aws_calls()]
        self.assertEqual(1, commands.count("put-object"))
        for command in ("delete-object", "copy-object", "create-multipart-upload", "list-object-versions"):
            self.assertNotIn(command, commands)

    def test_an_upload_whose_abort_fails_is_aborted_on_the_way_out(self):
        # The bucket may carry no lifecycle rule to expire an open upload (ADR-0014).
        case, result = self.run_world(lambda world: world.update(abort_failures=1))
        self.assertCases(result, P04_CASES + READ_BACKS, failing=("multipart upload is aborted",))
        self.assertEqual({}, case.load_world()["uploads"], "the upload was left open")


class CleanUpTests(CompanionTestCase):
    """--clean-up-as-teardown-role: after the cases, the teardown role, in a session of its own,
    cannot write and deletes this run's probe and nothing else (#98's Q29)."""

    def probe_prefix(self, reading: str = CLOCK[0]) -> str:
        return f"apps/p04-probe/releases/{reading}/"

    def assertTeardownTouchedOnlyTheProbe(self, case: Case):
        for call in case.aws_calls():
            if call["as"] == "teardown":
                self.assertIn(call["command"], ("put-object", "list-object-versions", "delete-object"))
                where = call["options"].get("key", call["options"].get("prefix"))
                self.assertTrue(where.startswith(self.probe_prefix()), call)

    def test_the_role_cannot_write_and_deletes_the_probe_leaving_nothing(self):
        case = Case(self)
        result = case.run(FLAG)
        self.assertEqual(0, result.status, result.stdout + result.stderr)
        self.assertNoUnexpectedCall(case)
        self.assertCases(result, P04_CASES + READ_BACKS + CLEAN_UP_CASES)
        self.assertTrue(result.verdict().startswith(f"{PROGRAM}: pass:"), result.verdict())
        self.assertEqual({}, case.load_world()["objects"], "the probe was left behind")
        self.assertTeardownTouchedOnlyTheProbe(case)
        # The ARN assumed is the one IAM returns for the derived name, not an assembled string.
        calls = case.aws_calls()
        assumed = [call for call in calls if call.get("command") == "assume-role"]
        self.assertEqual([TEARDOWN_ROLE_ARN], [call["options"]["role-arn"] for call in assumed])
        self.assertEqual(["operator"], [call["as"] for call in assumed])
        self.assertEqual([TEARDOWN_ROLE],
                         [call["options"]["role-name"] for call in calls if call.get("command") == "get-role"])

    def test_it_deletes_under_this_runs_release_prefix_and_nowhere_else(self):
        world = default_world()
        kept = ("apps/app1/releases/2026-10-10.1/index.html", f"{self.probe_prefix('20261018T000000Z')}index.html")
        for number, key in enumerate(kept):
            world["objects"][key] = [{"id": f"kept{number}", "etag": '"0"', "marker": False}]
        case = Case(self, world=world)
        self.assertEqual(0, case.run(FLAG).status)
        self.assertEqual(set(kept), set(case.load_world()["objects"]))
        self.assertTeardownTouchedOnlyTheProbe(case)

    def test_a_role_that_may_write_fails_and_what_it_wrote_is_removed(self):
        world = default_world()
        world["teardown"]["writes"] = True
        case = Case(self, world=world)
        result = case.run(FLAG)
        self.assertEqual(1, result.status, result.stdout)
        self.assertEqual("FAIL", self.line_of(result, "teardown role cannot write")[1])
        deleted = [line for status, line in result.cases() if "teardown role deletes version" in line]
        self.assertEqual(2, len(deleted), result.stdout)
        self.assertEqual("pass", self.line_of(result, "nothing is left under the probe's release prefix")[1])
        self.assertEqual({}, case.load_world()["objects"])

    def test_a_policy_whose_exception_names_another_principal_fails_the_clean_up(self):
        # What the clean-up proves at 1.5 rather than at teardown: the policy's exception names the
        # role the operator can assume (ADR-0014; #98's Q29).
        world = default_world()
        world["policy"]["exempts_teardown_role"] = False
        case = Case(self, world=world)
        result = case.run(FLAG)
        self.assertEqual(1, result.status, result.stdout)
        self.assertCases(result, P04_CASES + READ_BACKS + CLEAN_UP_CASES, failing=(
            "teardown role deletes version", "nothing is left under the probe's release prefix"))
        self.assertIn("got 1 version, 0 delete markers",
                      self.line_of(result, "nothing is left under the probe's release prefix")[2])
        self.assertEqual([f"{self.probe_prefix()}index.html"], list(case.load_world()["objects"]))

    def test_a_role_the_operator_cannot_assume_runs_no_clean_up_case(self):
        world = default_world()
        world["teardown"]["trusts_caller"] = False
        case = Case(self, world=world)
        result = case.run(FLAG)
        self.assertEqual(1, result.status, result.stdout)
        self.assertIn("403 AccessDenied", self.line_of(result, "is assumed in a separate session")[2])
        for fragment in ("teardown role cannot write", "teardown role deletes", "nothing is left"):
            index, status, line = self.line_of(result, fragment)
            self.assertEqual("FAIL", status)
            self.assertIn("not run, because", line)
        self.assertEqual({"operator"}, {call["as"] for call in case.aws_calls()})

    def test_the_cell_names_the_role(self):
        case = Case(self)
        result = case.run(FLAG, "--cell", "dev")
        self.assertEqual(1, result.status, result.stdout)
        self.assertEqual(["spa-platform-dev-teardown"],
                         [call["options"]["role-name"] for call in case.aws_calls() if call.get("command") == "get-role"])
        self.assertIn("404 NoSuchEntity", self.line_of(result, "spa-platform-dev-teardown is assumed")[2])

    def test_the_clean_up_runs_after_a_failed_case_too(self):
        # Without the delete rule the operator's deletes succeed and the last write lands over the
        # marker; the clean-up still leaves the bucket as the run found it.
        world = default_world()
        world["policy"]["deny_deletes"] = False
        case = Case(self, world=world)
        result = case.run(FLAG)
        self.assertEqual(1, result.status, result.stdout)
        self.assertEqual("pass", self.line_of(result, "nothing is left under the probe's release prefix")[1])
        self.assertEqual({}, case.load_world()["objects"])


class DidNotStartTests(CompanionTestCase):
    """Exit status 2, and no write to the bucket: usage errors and missing prerequisites."""

    def assertDidNotStart(self, case: Case, result: Result, fragment: str):
        self.assertEqual(2, result.status, result.stdout + result.stderr)
        self.assertIn(fragment, result.stderr)
        self.assertEqual([], [call for call in case.aws_calls() if call.get("service") == "s3api"])

    def test_an_unknown_argument(self):
        case = Case(self)
        self.assertDidNotStart(case, case.run("--cleanup"), "unknown argument '--cleanup'")
        self.assertEqual([], case.aws_calls())

    def test_a_cell_without_its_value_or_outside_the_cells_form(self):
        case = Case(self)
        self.assertDidNotStart(case, case.run("--cell"), "--cell needs a value")
        self.assertDidNotStart(case, case.run("--cell", "Dev_1"), "--cell must be lowercase letters")
        self.assertEqual([], case.aws_calls())

    def test_help_prints_the_manual_and_the_flag(self):
        case = Case(self)
        result = case.run("--help")
        self.assertEqual(0, result.status, result.stderr)
        self.assertIn(FLAG, result.stdout)
        self.assertIn("Exit status: 0 when every case is as expected", result.stdout)
        self.assertEqual([], case.aws_calls())

    def test_no_aws_cli_or_no_terraform_on_path(self):
        for missing, fragment in (("aws", "the AWS CLI is not on PATH"), ("terraform", "terraform is not on PATH")):
            with self.subTest(missing):
                case = Case(self, doubles=tuple(name for name in DOUBLES if name != missing))
                self.assertDidNotStart(case, case.run(), fragment)

    def test_no_region_names_the_file_that_holds_it(self):
        case = Case(self)
        self.assertDidNotStart(case, case.run(env=case.env(AWS_REGION=None)), "tools/aws-project.env")
        self.assertEqual([], case.aws_calls())

    def test_the_default_region_serves_when_aws_region_is_unset(self):
        # The CLI reads AWS_REGION, then AWS_DEFAULT_REGION:
        # https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-envvars.html
        case = Case(self)
        result = case.run(env=case.env(AWS_REGION=None, AWS_DEFAULT_REGION=REGION))
        self.assertEqual(0, result.status, result.stdout + result.stderr)

    def test_no_credentials(self):
        world = default_world()
        world["no_credentials"] = True
        case = Case(self, world=world)
        result = case.run()
        self.assertDidNotStart(case, result, "no AWS credentials answered get-caller-identity")
        self.assertIn("the AWS CLI exited 253", result.stderr)
        self.assertEqual([], case.terraform_calls())

    def test_no_bucket_output_reads_no_further(self):
        # What a plan that has not applied the bucket leaves: the output is not in state.
        case = Case(self, outputs={})
        result = case.run()
        self.assertDidNotStart(case, result, "could not read the platform root's bucket output")
        self.assertEqual(["get-caller-identity"], [call["command"] for call in case.aws_calls()])

    def test_missing_repository_outputs_leave_their_cases_not_run_and_p04_still_runs(self):
        case = Case(self, outputs={"bucket": BUCKET})
        result = case.run()
        self.assertEqual(1, result.status, result.stdout)
        self.assertCases(result, P04_CASES + READ_BACKS[:6])
        not_run = [line for status, line in result.cases() if "not run, because the platform root has no" in line]
        self.assertEqual(2, len(not_run), result.stdout)


class NothingSecretIsPrintedTests(CompanionTestCase):
    """Case.run holds this on every run of every case; here it is shown not to be vacuous."""

    def test_what_the_script_must_not_print_reaches_it_and_is_not_printed(self):
        case = Case(self)
        result = case.run(FLAG)
        calls = case.aws_calls()
        # It reached the script: S3 refused the operator with messages naming the caller's ARN, which
        # carries the account id and the session name, and the teardown session ran on the
        # credentials assume-role handed out.
        self.assertIn("AccessDenied", [call["outcome"] for call in calls if call["as"] == "operator"])
        self.assertIn("teardown", [call["as"] for call in calls])
        # And it was not printed: Case.run asserted that already. The bucket's line still names it,
        # with the account id masked.
        self.assertIn(f"bucket spa-platform-{CELL}-<account>-{REGION}-an", result.stdout)


class InterpreterTests(unittest.TestCase):
    def test_bash_is_the_version_this_run_requires(self):
        # Set by CI's macOS job, as in tools/preflight_tests.py.
        required = os.environ.get(REQUIRE_BASH, "")
        version = bash_version()
        self.assertRegex(version, r"^\d+\.\d+$")
        if required:
            self.assertEqual(required, version, f"{REQUIRE_BASH}={required}, but {BASH} is {version}")


if __name__ == "__main__":
    unittest.main()
