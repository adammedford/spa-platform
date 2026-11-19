#!/usr/bin/env python3
"""Test companion for tools/scripts/seed.sh and tools/scripts/cell_documents.py. Owner: workstream 1.

Task 1.7 (#58): "Seeding publishes the fixture releases and writes the first registry through
spactl." The doubles, the world they share and the universal every run holds (no output names the
account id) are tools/scripts/cell_doubles.py's; spactl is reached as `dotnet run --project
tools/spactl --`, and the dotnet double answers as spactl does for publish and app add.

Run: python3 -I tools/scripts/seed_tests.py
"""

from __future__ import annotations

import importlib.util
import json
import pathlib
import re
import sys

_spec = importlib.util.spec_from_file_location("cell_doubles", pathlib.Path(__file__).resolve().parent / "cell_doubles.py")
cell = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(cell)

import unittest  # noqa: E402

SCRIPT = "seed.sh"
# Read from the fixtures, never typed here: the seed is every definition in fixtures/apps/.
APPS = sorted(path.stem for path in (cell.ROOT / "fixtures" / "apps").glob("*.json"))


def setUpModule():
    cell.bash_banner("seed_tests")


def spactl_calls(case) -> list[list[str]]:
    return [call["spactl"] for call in case.calls("dotnet")]


# The contracts a document is checked against, by the $id each declares, so the outputs schema's
# references to registry.schema.json resolve to the file beside it.
_CONTRACTS = {json.loads(path.read_text())["$id"]: json.loads(path.read_text())
              for path in (cell.ROOT / "contracts").glob("*.schema.json")}


def schema_errors(value, schema: dict, root: dict, where: str = "$") -> list[str]:
    """Where value breaks schema: the JSON Schema keywords contracts/infra-outputs.schema.json and the
    definitions it references use, and no others (no jsonschema package is on the machine, and D-3 is
    the standard library). A keyword this does not know fails, so a schema that grows one cannot pass
    unread."""
    known = {"$ref", "type", "pattern", "minLength", "maxLength", "minimum", "required", "properties",
             "additionalProperties", "propertyNames", "description", "$schema", "$id", "title", "x-freeze",
             "$defs", "$comment", "examples"}
    unknown = set(schema) - known
    if unknown:
        return [f"{where}: the checker does not know {sorted(unknown)}"]
    if "$ref" in schema:
        base, _, pointer = schema["$ref"].partition("#")
        target = _CONTRACTS[base] if base else root
        node = target
        for part in pointer.strip("/").split("/"):
            node = node[part]
        return schema_errors(value, node, target, where)
    errors = []
    kind = schema.get("type")
    types = {"string": str, "object": dict, "integer": int}
    if kind and (not isinstance(value, types[kind]) or (kind == "integer" and isinstance(value, bool))):
        return [f"{where}: not a {kind}"]
    if isinstance(value, str):
        if "pattern" in schema and not re.search(schema["pattern"], value):
            errors.append(f"{where}: does not match {schema['pattern']}")
        if len(value) < schema.get("minLength", 0) or len(value) > schema.get("maxLength", len(value)):
            errors.append(f"{where}: length out of bounds")
    if isinstance(value, int) and "minimum" in schema and value < schema["minimum"]:
        errors.append(f"{where}: below {schema['minimum']}")
    if isinstance(value, dict):
        for name in schema.get("required", []):
            if name not in value:
                errors.append(f"{where}: {name} is required")
        properties = schema.get("properties", {})
        for name, member in value.items():
            if "propertyNames" in schema:
                errors += schema_errors(name, schema["propertyNames"], root, f"{where} key {name}")
            if name in properties:
                errors += schema_errors(member, properties[name], root, f"{where}.{name}")
            elif schema.get("additionalProperties") is False:
                errors.append(f"{where}: {name} is not a property")
            elif isinstance(schema.get("additionalProperties"), dict):
                errors += schema_errors(member, schema["additionalProperties"], root, f"{where}.{name}")
    return errors


def outputs_errors(document: dict) -> list[str]:
    schema = json.loads((cell.ROOT / "contracts" / "infra-outputs.schema.json").read_text())
    return schema_errors(document, schema, schema)


class SeedPublishesTheFixturesAndWritesTheFirstRegistry(cell.CompanionTestCase):
    """docs/plan/05-infrastructure-and-cost.md, First deployment, step 4: "seed.sh publishes the two
    fixture releases and writes the first registry"."""

    def seeded(self, *args: str, case=None):
        case = case or cell.Case(self)
        result = case.run(SCRIPT, *args)
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        return case, result

    def test_each_app_definition_is_published_then_added_through_spactl(self):
        self.assertEqual(["app1", "app2"], APPS, "the plan seeds two fixture apps; fixtures/apps/ changed")
        case, _ = self.seeded()
        calls = spactl_calls(case)
        self.assertEqual(APPS, [c[2] for c in calls if c[:2] == ["release", "publish"]])
        for app in APPS:
            publish = next(c for c in calls if c[:3] == ["release", "publish", app])
            add = next(c for c in calls if c[:2] == ["app", "add"] and c[2].endswith(f"fixtures/apps/{app}.json"))
            self.assertEqual(str(case.repo / "fixtures" / "spa" / app), publish[3])
            # The entry names the release just published.
            self.assertEqual(publish[4], add[add.index("--release") + 1])
            self.assertLess(calls.index(publish), calls.index(add))
        registry = case.load()["registry"]
        self.assertEqual(APPS, sorted(entry["appId"] for entry in registry["apps"]))

    def test_spactl_is_dotnet_run_of_the_project_in_the_tree(self):
        case, _ = self.seeded()
        for call in case.calls("dotnet"):
            self.assertEqual(str(case.repo / "tools" / "spactl"), call["project"])

    def test_spactl_reads_the_outputs_document_it_wrote(self):
        case, _ = self.seeded()
        outputs = case.repo / "infra" / "envs" / "poc" / "outputs.json"
        for call in spactl_calls(case):
            self.assertEqual(str(outputs), call[call.index("--outputs") + 1])
        document = json.loads(outputs.read_text())
        schema = json.loads((cell.ROOT / "contracts" / "infra-outputs.schema.json").read_text())
        self.assertEqual(sorted(schema["required"]), sorted(document))
        world = cell.default_world()
        self.assertEqual(world["roots"]["apps"]["clients"], document["clients"])
        self.assertEqual(world["roots"]["platform"]["bucket"], document["bucket"])
        self.assertEqual(world["roots"]["idp"]["issuer"], document["issuer"])

    def test_it_writes_the_outputs_document_from_the_roots_and_then_reads_only_it(self):
        # contracts/infra-outputs.schema.json: the scripts read outputs.json, and none of them reads
        # Terraform state; Q49 on #129 excepts teardown.sh alone of these four. Writing the document
        # is the one terraform output seed.sh runs. Review of #58.
        case, _ = self.seeded()
        outputs = [c for c in case.calls("terraform") if c.get("command") == "output"]
        self.assertEqual(["apps", "idp", "platform"], sorted(c["root"] for c in outputs))
        self.assertTrue(all("-json" in c["argv"] for c in outputs), outputs)

    def test_the_registry_is_bounded_by_the_configuration_the_gateway_runs_with(self):
        case, _ = self.seeded()
        config = next(c for c in case.calls("dotnet") if c["spactl"][:2] == ["app", "add"])["config"]
        environment = dict(cell.environment())
        self.assertEqual({"cognito-poc": {
            "Authority": environment["Issuers__cognito-poc__Authority"],
            "BrowserOrigin": environment["Issuers__cognito-poc__BrowserOrigin"],
            "Profile": environment["Issuers__cognito-poc__Profile"]}}, config["Issuers"])
        self.assertEqual({"AllowedHostSuffixes": [".spa-poc.test:8443"],
                          "AllowedUpstreams": ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]}, config["Policy"])
        # Typed as the schema types them: the environment carries strings.
        self.assertEqual(15, config["Registry"]["PollSeconds"])
        self.assertEqual(10000, config["Session"]["MaxSessions"])
        self.assertEqual(["10.20.0.0/24", "10.20.1.0/24"], config["ForwardedHeaders"]["TrustedNetworks"])
        self.assertNotIn("ASPNETCORE_HTTP_PORTS", config)
        schema = json.loads((cell.ROOT / "contracts" / "platform-config.schema.json").read_text())
        self.assertEqual(sorted(schema["required"]), sorted(config))

    def test_the_release_is_named_for_its_content_and_a_second_seed_writes_no_release_object(self):
        case, _ = self.seeded()
        first = [c[4] for c in spactl_calls(case) if c[:2] == ["release", "publish"]]
        self.assertTrue(all(name.startswith("fixture-") and len(name) == len("fixture-") + 12 for name in first), first)
        self.assertEqual(len(APPS), len(set(first)), "two apps with different content share a release name")
        releases = [o for o in case.load()["objects"] if o["Key"].startswith("apps/")]
        self.seeded(case=case)
        second = [c[4] for c in spactl_calls(case) if c[:2] == ["release", "publish"]][len(APPS):]
        self.assertEqual(first, second)
        self.assertEqual(releases, [o for o in case.load()["objects"] if o["Key"].startswith("apps/")])

    def test_a_changed_fixture_is_a_new_release(self):
        case = cell.Case(self)
        self.seeded(case=case)
        (case.repo / "fixtures" / "spa" / "app1" / "index.html").write_text("<!doctype html><title>changed</title>\n")
        self.seeded(case=case)
        names = [c[4] for c in spactl_calls(case) if c[:3] == ["release", "publish", "app1"]]
        self.assertEqual(2, len(set(names)))

    def test_a_release_name_given_is_used_for_every_app(self):
        case, _ = self.seeded("--release", "2026-10-20.1")
        self.assertEqual(["2026-10-20.1"] * len(APPS),
                         [c[4] for c in spactl_calls(case) if c[:2] == ["release", "publish"]])

    def test_spactl_named_by_the_environment_is_run_instead(self):
        case = cell.Case(self)
        case.write_executable("spactl", f'#!/bin/sh\nexec "{case.bin}/dotnet" run --project /given -- "$@"\n')
        result = case.run(SCRIPT, env=case.env(SPACTL=str(case.bin / "spactl")))
        self.assertEqual(0, result.status, result.output)
        self.assertEqual({"/given"}, {call["project"] for call in case.calls("dotnet")})


class SeedWritesTheBastionServiceOnlyWhenTheRootPublishesIt(cell.CompanionTestCase):
    """#1168: the outputs document's one optional field, ecsBastionService, is copied when the
    platform root publishes ecs_bastion_service (bastion = true) and absent when it does not
    (bastion = false, whose null output terraform output -json leaves out), and both documents are
    contracts/infra-outputs.schema.json's."""

    def document(self, world: dict) -> dict:
        case = cell.Case(self, world=world)
        result = case.run(SCRIPT)
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        return json.loads((case.repo / "infra" / "envs" / "poc" / "outputs.json").read_text())

    def test_with_the_bastion_the_document_names_its_service(self):
        document = self.document(cell.with_bastion(cell.default_world()))
        self.assertEqual(cell.BASTION_SERVICE, document["ecsBastionService"])
        self.assertEqual([], outputs_errors(document))

    def test_without_the_bastion_the_document_has_no_such_key(self):
        document = self.document(cell.default_world())
        self.assertNotIn("ecsBastionService", document)
        self.assertEqual([], outputs_errors(document))

    def test_a_null_output_is_no_bastion_either(self):
        # Terraform 1.11.0 leaves a null output out of -json (#1167), and should one ever print it
        # as null, it is still no bastion.
        world = cell.default_world()
        world["roots"]["platform"]["ecs_bastion_service"] = None
        document = self.document(world)
        self.assertNotIn("ecsBastionService", document)
        self.assertEqual([], outputs_errors(document))

    def test_the_checker_refuses_what_the_schema_refuses(self):
        # The check above can fail: a document with an unknown key, without a required one, or with
        # a bastion service name the contract's awsName refuses.
        document = self.document(cell.with_bastion(cell.default_world()))
        for broken in ({**document, "ecsBastion": "bastion"},
                       {key: value for key, value in document.items() if key != "ecsService"},
                       {**document, "ecsBastionService": "-bastion"}):
            with self.subTest(keys=sorted(set(broken) ^ set(document)), value=broken.get("ecsBastionService")):
                self.assertNotEqual([], outputs_errors(broken))


class SeedRefuses(cell.CompanionTestCase):
    def refused(self, world: dict, status: int, fragment: str, *args: str):
        case = cell.Case(self, world=world)
        result = case.run(SCRIPT, *args)
        self.assertEqual(status, result.status, result.output)
        self.assertIn(fragment, result.stderr)
        self.assertEqual([], case.calls("dotnet"), "spactl ran where it must not")
        self.assertFalse((case.repo / "infra" / "envs" / "poc" / "outputs.json").exists())
        return case

    def test_an_apps_root_not_applied(self):
        world = cell.default_world()
        world["roots"]["apps"] = {}
        self.refused(world, 1, "apps.clients")

    def test_a_root_not_initialised(self):
        world = cell.default_world()
        world["faults"]["output idp"] = True
        self.refused(world, 2, "could not read the idp root's outputs")

    def test_a_platform_root_in_another_region(self):
        world = cell.default_world()
        world["roots"]["platform"]["region"] = "us-east-1"
        world["roots"]["idp"]["region"] = "us-east-1"
        self.refused(world, 1, "not AWS_REGION")

    def test_an_idp_root_in_another_region_from_the_platform_root(self):
        # The platform root is in AWS_REGION, so only the check across roots catches the idp root
        # elsewhere (#192, from the review of #186, S5).
        world = cell.default_world()
        world["roots"]["idp"]["region"] = "us-east-1"
        self.refused(world, 1, "the idp root's region differs from the platform root's")

    def test_an_idp_root_of_another_cell_from_the_platform_root(self):
        world = cell.default_world()
        world["roots"]["idp"]["cell"] = "poc2"
        self.refused(world, 1, "the idp root's cell differs from the platform root's")

    def test_no_app_definition_publishes_nothing_and_fails(self):
        # The loop over fixtures/apps/*.json runs no time, and the seed must not then say it passed
        # (#192). Not refused(): the outputs document is written before the loop.
        case = cell.Case(self)
        for definition in (case.repo / "fixtures" / "apps").glob("*.json"):
            definition.unlink()
        result = case.run(SCRIPT)
        self.assertEqual(1, result.status, result.output)
        self.assertIn("fixtures/apps/ holds no app definition.", result.stderr)
        self.assertNotIn("pass:", result.stdout)
        self.assertEqual([], case.calls("dotnet"))
        self.assertNoUnexpectedCall(case)

    def test_a_release_name_that_is_not_one(self):
        self.refused(cell.default_world(), 2, "--release must be", "--release", "a/b")


if __name__ == "__main__":
    sys.exit(cell.main("seed_tests"))
