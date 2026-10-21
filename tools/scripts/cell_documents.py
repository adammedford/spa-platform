#!/usr/bin/env python3
"""The JSON that seed.sh, deploy.sh and rollback.sh read and write. Owner: workstream 1.

Task 1.7 (#58). The scripts are bash 3.2, which has no JSON reader, and jq is in neither
docs/dependencies.md nor tools/preflight.sh; python3 is in both. So each document the scripts build
or read is built or read here, and nothing else is: the scripts pass files in and read one line or
one file back.

    cell_documents.py outputs <region> <idp.json> <apps.json> <platform.json> <out>
        infra/envs/poc/outputs.json, contracts/infra-outputs.schema.json's document, from the three
        roots' `terraform output -json`. Written whole or not at all.
    cell_documents.py field <outputs.json> <field> <region>
        One field of that document, for a script that reads it: the contract's own description says
        the scripts read it and none of them reads Terraform state, teardown.sh excepted (change
        request 1.5a, decision 2; Q49 on #129). Refused when the document is for another Region.
    cell_documents.py config <environment.tsv> <platform-config.schema.json>
        The platform configuration the gateway container runs with, in the JSON form of
        contracts/platform-config.schema.json, from its task definition's environment.
    cell_documents.py apps <registry.json>
        The number of apps in a registry document.
    cell_documents.py release <directory>
        A release name for a fixture directory, derived from its content.

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


def field(path: str, name: str, region: str) -> int:
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
    value = document.get(name)
    if not isinstance(value, str) or not value:
        raise Refused(f"the outputs document has no {name}")
    print(value)
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


# --- release ---------------------------------------------------------------------------------------

def release(directory: str) -> int:
    """fixture-<12 hex>: the SHA-256 of every file's relative path and bytes, in path order. A
    release prefix is written once (ADR-0009), and spactl's publish leaves identical bytes alone, so
    a name that follows the content makes a second seed of the same fixtures a no-op and a changed
    fixture a new release, never a refused overwrite."""
    root = pathlib.Path(directory)
    if not (root / "index.html").is_file():
        raise Refused(f"{directory} has no index.html")
    digest = hashlib.sha256()
    for file in sorted(p for p in root.rglob("*") if p.is_file()):
        relative = file.relative_to(root).as_posix().encode()
        digest.update(len(relative).to_bytes(4, "big") + relative)
        content = file.read_bytes()
        digest.update(len(content).to_bytes(8, "big") + content)
    print("fixture-" + digest.hexdigest()[:12])
    return 0


COMMANDS = {"outputs": (outputs, 5), "field": (field, 3), "config": (config, 2), "apps": (apps, 1), "release": (release, 1)}


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
