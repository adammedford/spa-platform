#!/usr/bin/env python3
"""The JSON that seed.sh, deploy.sh and rollback.sh read and write. Owner: workstream 1.

Task 1.7 (#58). The scripts are bash 3.2, which has no JSON reader, and jq is in neither
docs/dependencies.md nor tools/preflight.sh; python3 is in both. So each document the scripts build
or read is built or read here, and nothing else is: the scripts pass files in and read one line or
one file back.

    cell_documents.py outputs <region> <idp.json> <apps.json> <platform.json> <out>
        infra/envs/poc/outputs.json, contracts/infra-outputs.schema.json's document, from the three
        roots' `terraform output -json`. Written whole or not at all. The one optional field,
        ecsBastionService, is copied when the platform root publishes it and left out when not.
    cell_documents.py field <outputs.json> <field> <region>
        One field of that document, for a script that reads it: the contract's own description says
        the scripts read it and none of them reads Terraform state, teardown.sh excepted (change
        request 1.5a, decision 2; Q49 on #129). Refused when the document is for another Region.
    cell_documents.py clients <outputs.json> <region>
        Each app's client id from that document, one `<appId><tab><clientId>` line per app, and each
        fragment's nested under it, one `<appId>:<fragmentId><tab><clientId>` line per fragment
        (#999), for the redaction in cell-common.sh, which elides them. Refused as field is.
    cell_documents.py fragments <definition.json>
        Each fragment of an app definition, in its order, one `<appId><tab><fragmentId><tab><entry>`
        line per fragment, the entry entry.js when the fragment names none; nothing for a definition
        without fragments. For seed.sh's fragment step (#999). Each id and entry is checked against
        contracts/registry.schema.json, since the seed makes paths of them.
    cell_documents.py fragment-release <directory> <entry>
        A release name for a fixture fragment directory, derived from its content as release's is;
        refused when the directory has no <entry> at its root.
    cell_documents.py config <environment.tsv> <platform-config.schema.json>
        The platform configuration the gateway container runs with, in the JSON form of
        contracts/platform-config.schema.json, from its task definition's environment.
    cell_documents.py apps <registry.json>
        The number of apps in a registry document.
    cell_documents.py revision <registry.json>
        A registry document's revision, the number /readyz reports once it is live.
    cell_documents.py digests <manifest> <digest> <os/architecture>
        The digests a running container of the image <digest> can report, one a line, from the
        manifest ECR's BatchGetImage answered for it: <digest> alone for an image manifest, and for an
        image index <digest> and then the one image in it for <os/architecture>, an attestation
        manifest never among them (#783).
    cell_documents.py release <directory>
        A release name for a fixture directory, derived from its content.
    cell_documents.py requests
        A filter for logs.sh --requests: of the `aws logs tail --format detailed` lines on standard
        input, each gateway request record becomes one line on standard output, and every other
        line, a health check's record among them, becomes nothing. Each line is written as it is read.

Exit status: 0 done; 1 the input is not what the document needs, with the reason on standard error;
2 a command line it does not take.
No value is printed in a reason: the inputs carry client ids and the account id.

Run with python3 -I, as the scripts do. Kept 3.9-compatible (docs/dependencies.md).
"""

from __future__ import annotations

import hashlib
import json
import os
import pathlib
import re
import sys
import tempfile


class Refused(Exception):
    pass


# --- outputs ---------------------------------------------------------------------------------------

# contracts/infra-outputs.schema.json's fields, each from the root and output that answers it. The
# roots' outputs.tf name each output for its field; the contract is additionalProperties: false, so
# this is the whole document and nothing else is copied.
FIELDS = (
    ("region", "platform", "region"),
    ("cell", "platform", "cell"),
    ("bucket", "platform", "bucket"),
    ("albDnsName", "platform", "alb_dns_name"),
    ("ecsCluster", "platform", "ecs_cluster"),
    ("ecsService", "platform", "ecs_service"),
    ("ecrGateway", "platform", "ecr_gateway"),
    ("ecrMockApi", "platform", "ecr_mock_api"),
    ("gatewayImage", "platform", "gateway_image"),
    ("issuer", "idp", "issuer"),
    ("cognitoDomain", "idp", "cognito_domain"),
    ("clients", "apps", "clients"),
    ("testUsers", "idp", "test_users"),
    ("logGroup", "platform", "log_group"),
)

# The contract's optional fields, which the document carries only when the root publishes them
# (#1167). The platform root's ecs_bastion_service is null with its bastion input false, and
# Terraform leaves a null output out of `terraform output -json`, so its absence, or a null, means
# the cell declares no bastion and the document has no such key: tunnel.sh reads that before it
# asks AWS anything.
OPTIONAL_FIELDS = (
    ("ecsBastionService", "platform", "ecs_bastion_service"),
)


def read_root(path: str, root: str) -> dict:
    try:
        document = json.loads(pathlib.Path(path).read_text())
    except (OSError, ValueError):
        raise Refused(f"the {root} root's terraform output is not JSON")
    if not isinstance(document, dict):
        raise Refused(f"the {root} root's terraform output is not an object of outputs")
    return document


def outputs(region: str, idp: str, apps: str, platform: str, out: str) -> int:
    roots = {"idp": read_root(idp, "idp"), "apps": read_root(apps, "apps"),
             "platform": read_root(platform, "platform")}
    document = {}
    missing = []
    for field, root, name in FIELDS:
        entry = roots[root].get(name)
        if not isinstance(entry, dict) or "value" not in entry:
            missing.append(f"{root}.{name}")
            continue
        document[field] = entry["value"]
    if missing:
        raise Refused("these outputs are missing, so a root is not applied or not initialised: " + ", ".join(missing))
    for field, root, name in OPTIONAL_FIELDS:
        entry = roots[root].get(name)
        if isinstance(entry, dict) and entry.get("value") is not None:
            document[field] = entry["value"]
    # The idp root's Region and cell against the platform root's, which the document carries; the
    # apps root has no region or cell output, so there is nothing of it to compare. And then the
    # platform root's Region against the one every AWS call of the scripts names: a root applied
    # elsewhere would put a bucket in the document the calls cannot reach.
    for name in ("region", "cell"):
        other = roots["idp"].get(name, {}).get("value")
        if other is not None and other != document[name]:
            raise Refused(f"the idp root's {name} differs from the platform root's")
    if document["region"] != region:
        raise Refused("the platform root's region is not AWS_REGION: source tools/aws-project.env, or check the root's terraform.tfvars")
    target = pathlib.Path(out)
    target.parent.mkdir(parents=True, exist_ok=True)
    # Whole or not at all: spactl and the deployed tests read this file, and half of one is worse
    # than the previous one.
    descriptor, temporary = tempfile.mkstemp(dir=str(target.parent), prefix=".outputs.", suffix=".tmp")
    try:
        with os.fdopen(descriptor, "w") as handle:
            json.dump(document, handle, indent=2)
            handle.write("\n")
        os.replace(temporary, target)
    except BaseException:
        if os.path.exists(temporary):
            os.unlink(temporary)
        raise
    return 0


def read_outputs(path: str, region: str) -> dict:
    try:
        document = json.loads(pathlib.Path(path).read_text())
    except (OSError, ValueError):
        raise Refused("the outputs document is not JSON")
    if not isinstance(document, dict):
        raise Refused("the outputs document is not an object")
    # The document is rewritten after every apply, so a stale one names this checkout's last cell;
    # one for another Region would aim every call of the script where they cannot reach.
    if document.get("region") != region:
        raise Refused("the outputs document is not for AWS_REGION: source tools/aws-project.env, or run seed.sh again")
    return document


def field(path: str, name: str, region: str) -> int:
    value = read_outputs(path, region).get(name)
    if not isinstance(value, str) or not value:
        raise Refused(f"the outputs document has no {name}")
    print(value)
    return 0


def clients(path: str, region: str) -> int:
    entries = read_outputs(path, region).get("clients")
    if not isinstance(entries, dict):
        raise Refused("the outputs document has no clients")
    lines = []

    def listed(label: str, entry) -> None:
        client = entry.get("clientId") if isinstance(entry, dict) else None
        # contracts/registry.schema.json: printable ASCII with no spaces, so no tab or newline can
        # break the line the redaction reads.
        if not isinstance(client, str) or not client or any(not "!" <= c <= "~" for c in client + label):
            raise Refused("an entry of the outputs document's clients has no printable clientId")
        lines.append((label, client))

    for app, entry in entries.items():
        listed(app, entry)
        # contracts/infra-outputs.schema.json: clients.{appId}.fragments.{fragmentId}, absent for an
        # app with none. A fragment's client id is as secret-free and as elided as a shell's, so it
        # is listed under <appId>:<fragmentId>, the two ids as the fragment's scheme name joins them.
        fragments = entry.get("fragments", {}) if isinstance(entry, dict) else {}
        if not isinstance(fragments, dict):
            raise Refused("an entry of the outputs document's clients has fragments that are not an object")
        for fragment, nested in fragments.items():
            listed(f"{app}:{fragment}", nested)
    # The longest first: the redaction replaces them in this order, and one id inside another
    # would otherwise leave the longer one's remainder in the line.
    for app, client in sorted(lines, key=lambda line: (-len(line[1]), line[0])):
        print(f"{app}\t{client}")
    return 0


# --- config ----------------------------------------------------------------------------------------

def schema_at(schema: dict, path: list[str]) -> dict | None:
    """The subschema a configuration path names: a property by name, an issuer through
    additionalProperties, an array element through items."""
    node = schema
    for segment in path:
        if not isinstance(node, dict):
            return None
        if node.get("type") == "array":
            node = node.get("items")
        elif segment in node.get("properties", {}):
            node = node["properties"][segment]
        elif isinstance(node.get("additionalProperties"), dict):
            node = node["additionalProperties"]
        else:
            return None
    return node if isinstance(node, dict) else None


def typed(value: str, schema: dict | None, key: str):
    """An environment value is a string the binder converts (contracts/platform-config.schema.json's
    description of the deployed form); the JSON form carries the schema's type."""
    kind = (schema or {}).get("type")
    if kind == "integer":
        try:
            return int(value)
        except ValueError:
            raise Refused(f"{key} is not an integer")
    if kind == "boolean":
        if value.lower() not in ("true", "false"):
            raise Refused(f"{key} is not true or false")
        return value.lower() == "true"
    return value


def config(environment: str, schema_path: str) -> int:
    schema = json.loads(pathlib.Path(schema_path).read_text())
    sections = set(schema.get("properties", {}))
    tree: dict = {}
    for line in pathlib.Path(environment).read_text().splitlines():
        if not line.strip():
            continue
        key, _, value = line.partition("\t")
        # The deployed form: "a colon becomes a double underscore" (the schema's description). A
        # variable that is not the platform's (ASPNETCORE_*, Logging, OTEL_*) is not in the schema's
        # sections and is left out, as the schema's $comment says the environment carries such.
        path = key.split("__")
        if path[0] not in sections or len(path) < 2:
            continue
        node = tree
        for segment in path[:-1]:
            node = node.setdefault(segment, {})
            if not isinstance(node, dict):
                raise Refused(f"{key} is both a value and a section")
        node[path[-1]] = typed(value, schema_at(schema, path), key)

    def arrays(node, path):
        # Policy__AllowedUpstreams__0 and __1 are an array's elements, in index order.
        if not isinstance(node, dict):
            return node
        target = schema_at(schema, path) or {}
        if target.get("type") == "array":
            if not all(index.isdigit() for index in node):
                raise Refused("__".join(path) + " is an array with a key that is not an index")
            return [arrays(node[index], path + [index]) for index in sorted(node, key=int)]
        return {name: arrays(child, path + [name]) for name, child in node.items()}

    tree = arrays(tree, [])
    missing = [name for name in schema.get("required", []) if name not in tree]
    if missing:
        raise Refused("the gateway container's environment has no " + ", ".join(missing) + " section")
    json.dump(tree, sys.stdout, indent=2)
    sys.stdout.write("\n")
    return 0


# --- apps ------------------------------------------------------------------------------------------

def apps(path: str) -> int:
    try:
        document = json.loads(pathlib.Path(path).read_text())
    except (OSError, ValueError):
        raise Refused("the live registry is not JSON")
    entries = document.get("apps") if isinstance(document, dict) else None
    if not isinstance(entries, list):
        raise Refused("the live registry has no apps array")
    print(len(entries))
    return 0


def revision(path: str) -> int:
    """contracts/registry.schema.json's revision: an integer of at least 1. A boolean is not one,
    though Python counts it as an int."""
    try:
        document = json.loads(pathlib.Path(path).read_text())
    except (OSError, ValueError):
        raise Refused("the live registry is not JSON")
    value = document.get("revision") if isinstance(document, dict) else None
    if isinstance(value, bool) or not isinstance(value, int) or value < 1:
        raise Refused("the live registry has no revision of at least 1")
    print(value)
    return 0


# --- digests --------------------------------------------------------------------------------------

# The two forms of each kind, OCI's and Docker's: an index lists images, an image manifest is one.
# https://github.com/opencontainers/image-spec/blob/main/image-index.md
INDEX_TYPES = ("application/vnd.oci.image.index.v1+json", "application/vnd.docker.distribution.manifest.list.v2+json")
IMAGE_TYPES = ("application/vnd.oci.image.manifest.v1+json", "application/vnd.docker.distribution.manifest.v2+json")
# buildx lists an attestation manifest in the index it pushes under the platform unknown/unknown,
# so a platform's image is told from it by its platform alone:
# https://docs.docker.com/build/metadata/attestations/attestation-storage/


def digests(path: str, digest: str, platform: str) -> int:
    """BatchGetImage answers a digest it does not hold under failures, so the query's text is None."""
    text = pathlib.Path(path).read_text().strip()
    if text in ("", "None"):
        raise Refused("the registry holds no manifest for the image")
    try:
        manifest = json.loads(text)
    except ValueError:
        raise Refused("the image's manifest is not JSON")
    kind = manifest.get("mediaType") if isinstance(manifest, dict) else None
    if kind in IMAGE_TYPES:
        print(digest)
        return 0
    if kind not in INDEX_TYPES or not isinstance(manifest.get("manifests"), list):
        raise Refused("the image's manifest is neither an image manifest nor an image index")
    wanted_os, _, wanted_architecture = platform.partition("/")
    images = [entry.get("digest") for entry in manifest["manifests"]
              if isinstance(entry, dict)
              and (entry.get("platform") or {}).get("os") == wanted_os
              and (entry.get("platform") or {}).get("architecture") == wanted_architecture]
    if len(images) != 1:
        raise Refused(f"the image index holds {len(images) or 'no'} {platform} image{'' if len(images) == 1 else 's'}")
    if not isinstance(images[0], str) or not images[0].startswith("sha256:"):
        raise Refused(f"the image index names its {platform} image by no digest")
    print(digest)
    print(images[0])
    return 0


# --- release ---------------------------------------------------------------------------------------

def release(directory: str, complete: str = "index.html") -> int:
    """fixture-<12 hex>: the SHA-256 of every file's relative path and bytes, in path order. A
    release prefix is written once (ADR-0009), and spactl's publish leaves identical bytes alone, so
    a name that follows the content makes a second seed of the same fixtures a no-op and a changed
    fixture a new release, never a refused overwrite. `complete` is the file that makes the release
    one: index.html for an app's, the entry module for a fragment's."""
    root = pathlib.Path(directory)
    if not (root / complete).is_file():
        raise Refused(f"{directory} has no {complete}")
    digest = hashlib.sha256()
    for file in sorted(p for p in root.rglob("*") if p.is_file()):
        relative = file.relative_to(root).as_posix().encode()
        digest.update(len(relative).to_bytes(4, "big") + relative)
        content = file.read_bytes()
        digest.update(len(content).to_bytes(8, "big") + content)
    print("fixture-" + digest.hexdigest()[:12])
    return 0


def fragment_release(directory: str, entry: str) -> int:
    return release(directory, entry)


# --- fragments -------------------------------------------------------------------------------------

# The registry contract, beside this script in the tree, for the id and entry rules and the entry's
# default: read, never copied, so a changed rule there is the rule here.
REGISTRY_SCHEMA = pathlib.Path(__file__).resolve().parent.parent.parent / "contracts" / "registry.schema.json"


def fragments(path: str) -> int:
    try:
        definition = json.loads(pathlib.Path(path).read_text())
    except (OSError, ValueError):
        raise Refused("the app definition is not JSON")
    if not isinstance(definition, dict):
        raise Refused("the app definition is not an object")
    rules = json.loads(REGISTRY_SCHEMA.read_text())["$defs"]

    def checked(value, rule: str, what: str) -> str:
        schema = rules[rule]
        if (not isinstance(value, str) or not re.fullmatch(schema["pattern"], value)
                or not schema.get("minLength", 0) <= len(value) <= schema.get("maxLength", len(value))):
            raise Refused(f"the app definition has {what} that contracts/registry.schema.json's {rule} refuses")
        return value

    app = checked(definition.get("appId"), "appId", "an appId")
    listed = definition.get("fragments", [])
    if not isinstance(listed, list):
        raise Refused("the app definition's fragments is not an array")
    for fragment in listed:
        if not isinstance(fragment, dict):
            raise Refused("a fragment of the app definition is not an object")
        fragment_id = checked(fragment.get("fragmentId"), "fragmentId", "a fragmentId")
        entry = checked(fragment.get("entry", rules["entry"]["default"]), "entry", "an entry")
        print(f"{app}\t{fragment_id}\t{entry}")
    return 0


# --- requests --------------------------------------------------------------------------------------

# The category the gateway's request line is written under: RequestTelemetry.Category in
# src/Platform.Telemetry/RequestTelemetry.cs. contracts/telemetry.md gives the line's fields, and a
# request line carries no path, so a health check is told by its route: the contract's `health`,
# which the telemetry stage gives /healthz and /readyz and nothing else.
REQUEST_CATEGORY = "Platform.Telemetry.Request"
HEALTH_ROUTE = "health"


def request_line(line: str) -> str | None:
    """`<time> <task id's first eight> <app> <route> <rule> <status>` for one tail line that holds a
    gateway request record, else None. The tail's detailed format is the event's timestamp, its
    stream and its message (https://awscli.amazonaws.com/v2/documentation/api/latest/reference/logs/tail.html,
    `--format`); the stream is <container>/<container>/<task id> (infra/modules/cell/ecs.tf).
    A null app (a request refused before its app was resolved) or rule prints as -."""
    parts = line.rstrip("\n").split(" ", 2)
    if len(parts) < 3:
        return None
    _, stream, message = parts
    try:
        record = json.loads(message)
    except ValueError:
        return None
    if not isinstance(record, dict) or record.get("category") != REQUEST_CATEGORY:
        return None
    if record.get("route") == HEALTH_ROUTE:
        return None
    task = stream.rsplit("/", 1)[-1][:8]
    fields = [record.get("ts"), task, record.get("app_id"), record.get("route"), record.get("rule"), record.get("status")]
    return " ".join("-" if value is None else str(value) for value in fields)


def requests() -> int:
    """Reads line by line and flushes each answer, so that logs.sh --requests --follow prints a
    request as it arrives. Control-C ends logs.sh's read, and this filter with it, quietly."""
    try:
        for line in iter(sys.stdin.readline, ""):
            shown = request_line(line)
            if shown is not None:
                print(shown, flush=True)
    except KeyboardInterrupt:
        return 130
    except BrokenPipeError:
        return 0
    return 0


COMMANDS = {"outputs": (outputs, 5), "field": (field, 3), "clients": (clients, 2), "config": (config, 2), "apps": (apps, 1), "revision": (revision, 1), "digests": (digests, 3), "release": (release, 1), "requests": (requests, 0),
            "fragments": (fragments, 1), "fragment-release": (fragment_release, 2)}


def main(argv: list[str]) -> int:
    if not argv or argv[0] not in COMMANDS or len(argv) - 1 != COMMANDS[argv[0]][1]:
        print(__doc__.split("\n\n")[1], file=sys.stderr)
        return 2
    command, _ = COMMANDS[argv[0]]
    try:
        return command(*argv[1:])
    except Refused as refused:
        print(f"cell_documents: {refused}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
