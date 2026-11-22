#!/usr/bin/env python3
"""Test companion for tools/scripts/seed.sh and tools/scripts/cell_documents.py. Owner: workstream 1.

Task 1.7 (#58): "Seeding publishes the fixture releases and writes the first registry through
spactl." The doubles, the world they share and the universal every run holds (no output names the
account id) are tools/scripts/cell_doubles.py's; spactl is reached as `dotnet run --project
tools/spactl --`, and the dotnet double answers as spactl does for publish and app add.

Since #1348 it also holds the entry point the reference scripts share: the proof cell's entry
file and field map, cell-common.sh's write_outputs run through a driver of the case's own, and what
the reference scripts' sources may not name. Those cases read the files below, so a change to one
runs this companion.

Checkout inputs: tools/scripts/*.sh tools/scripts/cell_documents.py infra/envs/poc/cell.env infra/envs/poc/outputs-fields.json

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
# The fields the scripts write, from the field map they write them from (the proof cell's, which
# its entry file names; #1348), not from the schema's root required: the contract may require fewer
# of every cell than this cell's scripts write (#1367). The optional ones are left out.
WRITTEN_FIELDS = sorted(entry["field"] for entry in cell.field_map()["fields"] if not entry.get("optional"))

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
        self.assertEqual(WRITTEN_FIELDS, sorted(document))
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
        # a bastion service name the contract's awsName refuses. The required one dropped is bucket,
        # required under the schema as it stands and under #1336's.
        document = self.document(cell.with_bastion(cell.default_world()))
        for broken in ({**document, "ecsBastion": "bastion"},
                       {key: value for key, value in document.items() if key != "bucket"},
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


# --- #999: the fragment step -------------------------------------------------------------------------

# A shell with two fragments, this companion's own (the fixture fragment definitions are another
# ticket's): orders on the default entry, reports on a named one.
SHELL = "shell"
SHELL_DEFINITION = {
    "appId": SHELL, "host": "shell.spa-poc.test:8443",
    "oauth": {"issuer": "cognito-poc", "scopes": ["openid"], "resource": "https://orders.spa-poc.test"},
    "session": {"lifetimeMinutes": 480},
    "fragments": [
        {"fragmentId": "orders", "oauth": {"issuer": "cognito-poc", "scopes": ["openid"], "resource": "https://orders.spa-poc.test"}},
        {"fragmentId": "reports", "oauth": {"issuer": "cognito-poc", "scopes": ["openid"], "resource": "https://reports.spa-poc.test"},
         "entry": "main.js"},
    ],
}
DEFAULT_ENTRY = json.loads((cell.ROOT / "contracts" / "registry.schema.json").read_text())["$defs"]["entry"]["default"]


def fragment_world() -> dict:
    """The default cell after an apps-root apply that read the shell's definition too: the shell's
    client and, nested under it, each fragment's, as the root's clients output gives them."""
    world = cell.default_world()
    world["roots"]["apps"]["clients"][SHELL] = {
        "clientId": "1example23456789shellclient", "secretParameter": f"/spa-platform/poc/apps/{SHELL}/client-secret",
        "secretVersion": 1,
        "fragments": {f["fragmentId"]: {
            "clientId": f"1example23456789{f['fragmentId']}fragmentclient",
            "secretParameter": f"/spa-platform/poc/apps/{SHELL}/fragments/{f['fragmentId']}/client-secret",
            "secretVersion": 1} for f in SHELL_DEFINITION["fragments"]}}
    return world


def fragment_case(test, world: dict | None = None, definition: dict | None = None) -> tuple:
    """A case whose tree holds the shell's definition under fixtures/fragments/, each fragment's
    release directory with its entry at the root and a chunk under assets/, and the shell's release
    under fixtures/spa/. Returns the case and the definition's path."""
    case = cell.Case(test, world=world or fragment_world())
    definition = definition or SHELL_DEFINITION
    path = case.repo / "fixtures" / "fragments" / f"{definition['appId']}.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(definition, indent=2))
    for fragment in definition.get("fragments", []):
        root = case.repo / "fixtures" / "fragments" / definition["appId"] / fragment["fragmentId"]
        (root / "assets").mkdir(parents=True)
        (root / fragment.get("entry", DEFAULT_ENTRY)).write_text(f"import './assets/{fragment['fragmentId']}.js';\n")
        (root / "assets" / f"{fragment['fragmentId']}.js").write_text(f"export const name = '{fragment['fragmentId']}';\n")
    site = case.repo / "fixtures" / "spa" / definition["appId"]
    site.mkdir(parents=True, exist_ok=True)
    (site / "index.html").write_text("<!doctype html><title>shell</title>\n")
    return case, path


def documents(*args: str) -> tuple[int, str, str]:
    """cell_documents.py in this tree, as the scripts run it."""
    import subprocess
    completed = subprocess.run([sys.executable, "-I", str(cell.SCRIPTS / "cell_documents.py"), *args],
                               capture_output=True, text=True)
    return completed.returncode, completed.stdout, completed.stderr


class SeedPublishesAShellsFragmentsOnlyWhenNamed(cell.CompanionTestCase):
    """#999: for each definition --shell names, each fragment's `fragment publish` with the
    definition's --entry, then the shell's publish, then exactly one `app add` with the
    --fragment-release comma list; a run naming none writes no fragment-bearing document (#961, "The
    deployment prerequisite")."""

    def seeded(self, case, *args: str):
        result = case.run(SCRIPT, *args)
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        return result

    def test_each_fragment_then_the_shell_then_one_app_add_naming_every_fragment_release(self):
        case, definition = fragment_case(self)
        self.seeded(case, "--shell", str(definition))
        calls = spactl_calls(case)
        fragment_publishes = [c for c in calls if c[:2] == ["fragment", "publish"]]
        # Each fragment in the definition's order, with its directory and the definition's entry: the
        # default for orders, main.js for reports.
        self.assertEqual([[SHELL, "orders"], [SHELL, "reports"]], [c[2:4] for c in fragment_publishes])
        for call, fragment in zip(fragment_publishes, SHELL_DEFINITION["fragments"]):
            self.assertEqual(str(case.repo / "fixtures" / "fragments" / SHELL / fragment["fragmentId"]), call[4])
            self.assertEqual(fragment.get("entry", DEFAULT_ENTRY), call[call.index("--entry") + 1])
        self.assertEqual([DEFAULT_ENTRY, "main.js"], [c[c.index("--entry") + 1] for c in fragment_publishes])
        shell_publish = [c for c in calls if c[:3] == ["release", "publish", SHELL]]
        adds = [c for c in calls if c[:2] == ["app", "add"] and c[2] == str(definition)]
        self.assertEqual(1, len(shell_publish))
        self.assertEqual(1, len(adds), "exactly one app add for the shell")
        self.assertEqual(str(case.repo / "fixtures" / "spa" / SHELL), shell_publish[0][3])
        # Order: every fragment publish, then the shell's publish, then its one app add.
        self.assertLess(max(calls.index(c) for c in fragment_publishes), calls.index(shell_publish[0]))
        self.assertLess(calls.index(shell_publish[0]), calls.index(adds[0]))
        add = adds[0]
        self.assertEqual(shell_publish[0][4], add[add.index("--release") + 1])
        self.assertEqual(",".join(f"{c[3]}={c[5]}" for c in fragment_publishes), add[add.index("--fragment-release") + 1])
        self.assertEqual(1, sum("--fragment-release" in c for c in calls))
        entry = next(e for e in case.load()["registry"]["apps"] if e["appId"] == SHELL)
        self.assertEqual([{"fragmentId": c[3], "release": c[5]} for c in fragment_publishes], entry["fragments"])
        # The fixture apps are seeded as without --shell.
        self.assertEqual(APPS + [SHELL], sorted(e["appId"] for e in case.load()["registry"]["apps"]))

    def test_a_publish_writes_the_entry_last(self):
        case, definition = fragment_case(self)
        self.seeded(case, "--shell", str(definition))
        for fragment in SHELL_DEFINITION["fragments"]:
            keys = [o["Key"] for o in case.load()["objects"] if f"/fragments/{fragment['fragmentId']}/" in o["Key"]]
            self.assertEqual(2, len(keys))
            self.assertTrue(keys[-1].endswith("/" + fragment.get("entry", DEFAULT_ENTRY)), keys)

    def test_release_names_follow_each_fragments_content_and_a_second_seed_writes_no_release_object(self):
        case, definition = fragment_case(self)
        self.seeded(case, "--shell", str(definition))
        first = [c[5] for c in spactl_calls(case) if c[:2] == ["fragment", "publish"]]
        self.assertTrue(all(n.startswith("fixture-") and len(n) == len("fixture-") + 12 for n in first), first)
        self.assertEqual(2, len(set(first)), "two fragments with different content share a release name")
        objects = [o for o in case.load()["objects"] if o["Key"].startswith("apps/")]
        self.seeded(case, "--shell", str(definition))
        self.assertEqual(first, [c[5] for c in spactl_calls(case) if c[:2] == ["fragment", "publish"]][2:])
        self.assertEqual(objects, [o for o in case.load()["objects"] if o["Key"].startswith("apps/")])

    def test_a_release_name_given_is_used_for_every_fragment_too(self):
        case, definition = fragment_case(self)
        self.seeded(case, "--release", "2026-11-19.1", "--shell", str(definition))
        add = next(c for c in spactl_calls(case) if "--fragment-release" in c)
        self.assertEqual("orders=2026-11-19.1,reports=2026-11-19.1", add[add.index("--fragment-release") + 1])

    def test_the_outputs_document_carries_the_nested_clients_and_is_the_contracts(self):
        case, definition = fragment_case(self)
        self.seeded(case, "--shell", str(definition))
        document = json.loads((case.repo / "infra" / "envs" / "poc" / "outputs.json").read_text())
        self.assertEqual(fragment_world()["roots"]["apps"]["clients"][SHELL], document["clients"][SHELL])
        self.assertEqual([], outputs_errors(document))

    def test_a_failed_fragment_publish_stops_before_any_app_add_of_the_shell(self):
        world = fragment_world()
        world["faults"]["fragment publish reports"] = True
        case, definition = fragment_case(self, world=world)
        result = case.run(SCRIPT, "--shell", str(definition))
        self.assertEqual(1, result.status, result.output)
        self.assertIn("could not publish fragment reports", result.stderr)
        self.assertNotIn("pass:", result.stdout)
        calls = spactl_calls(case)
        self.assertEqual(["orders", "reports"], [c[3] for c in calls if c[:2] == ["fragment", "publish"]])
        self.assertEqual([], [c for c in calls if c[:2] == ["app", "add"] and c[2] == str(definition)])
        self.assertEqual([], [c for c in calls if "--fragment-release" in c])
        self.assertEqual([], [c for c in calls if c[:3] == ["release", "publish", SHELL]])
        self.assertNotIn(SHELL, [e["appId"] for e in case.load()["registry"]["apps"]])

    def test_a_run_naming_no_shell_publishes_no_fragment_and_names_no_fragment_release(self):
        # The shell's definition, its fragments and its nested clients are all there; unnamed, none is
        # seeded.
        case, _ = fragment_case(self)
        self.seeded(case)
        calls = spactl_calls(case)
        self.assertEqual([], [c for c in calls if c[:2] == ["fragment", "publish"]])
        self.assertEqual([], [c for c in calls if "--fragment-release" in c])
        self.assertEqual(APPS, sorted(c[2] for c in calls if c[:2] == ["release", "publish"]))
        registry = case.load()["registry"]
        self.assertTrue(all("fragments" not in e for e in registry["apps"]), registry)

    def test_a_fragment_bearing_definition_among_the_default_ones_is_refused_before_any_publish(self):
        case, _ = fragment_case(self)
        (case.repo / "fixtures" / "apps" / f"{SHELL}.json").write_text(json.dumps(SHELL_DEFINITION))
        result = case.run(SCRIPT)
        self.assertEqual(1, result.status, result.output)
        self.assertIn(f"fixtures/apps/{SHELL}.json has fragments", result.stderr)
        self.assertEqual([], case.calls("dotnet"))

    def test_a_named_definition_without_fragments_is_refused_before_any_publish(self):
        case, _ = fragment_case(self)
        plain = case.repo / "fixtures" / "fragments" / "plain.json"
        plain.write_text(json.dumps({key: value for key, value in SHELL_DEFINITION.items() if key != "fragments"}))
        result = case.run(SCRIPT, "--shell", str(plain))
        self.assertEqual(2, result.status, result.output)
        self.assertIn("has no fragments", result.stderr)
        self.assertEqual([], case.calls("dotnet"))

    def test_a_fragment_without_its_entry_file_is_refused_before_any_publish(self):
        case, definition = fragment_case(self)
        (case.repo / "fixtures" / "fragments" / SHELL / "reports" / "main.js").unlink()
        result = case.run(SCRIPT, "--shell", str(definition))
        self.assertEqual(1, result.status, result.output)
        self.assertIn("fragment reports of shell has no main.js", result.stderr)
        self.assertEqual([], case.calls("dotnet"))

    def test_shell_needs_a_file(self):
        case, _ = fragment_case(self)
        for args in (["--shell"], ["--shell", str(case.repo / "fixtures" / "fragments" / "absent.json")]):
            with self.subTest(args=args):
                result = case.run(SCRIPT, *args)
                self.assertEqual(2, result.status, result.output)
        self.assertEqual([], case.calls("dotnet"))

    def test_the_double_refuses_an_app_add_whose_entry_the_publish_did_not_write(self):
        # The seed's agreement between --entry and the definition's entry is what the double's app add
        # checks: a release published under another entry name is refused, as spactl refuses it
        # (#993, "Completion"), so the case above passing means the two agreed.
        case, definition = fragment_case(self)
        case.write_outputs()
        site = str(case.repo / "fixtures" / "fragments" / SHELL / "reports")
        outputs = str(case.repo / "infra" / "envs" / "poc" / "outputs.json")
        (pathlib.Path(site) / "main.js").rename(pathlib.Path(site) / "entry.js")
        spactl = [str(case.bin / "dotnet"), "run", "--project", "x", "--"]
        import subprocess
        for fragment, entry in (("orders", DEFAULT_ENTRY), ("reports", "entry.js")):
            subprocess.run([*spactl, "fragment", "publish", SHELL, fragment, site if fragment == "reports" else
                            str(case.repo / "fixtures" / "fragments" / SHELL / "orders"), "r1", "--entry", entry,
                            "--outputs", outputs], env=case.env(), capture_output=True, check=True)
        config = case.dir / "config.json"
        config.write_text("{}")
        refused = subprocess.run([*spactl, "app", "add", str(definition), "--release", "r1", "--fragment-release",
                                  "orders=r1,reports=r1", "--config", str(config), "--outputs", outputs],
                                 env=case.env(), capture_output=True, text=True)
        self.assertEqual(1, refused.returncode, refused.stderr)
        self.assertIn("no entry file", refused.stderr)


class CellDocumentsReadsFragments(cell.CompanionTestCase):
    """#999: cell_documents.py clients lists each nested fragment client id, and its fragments reader
    gives each fragment with its entry, entry.js when unnamed."""

    def outputs(self, world: dict) -> pathlib.Path:
        case = cell.Case(self, world=world, outputs=True)
        return case.repo / "infra" / "envs" / "poc" / "outputs.json"

    def test_clients_lists_each_fragment_client_id_under_app_and_fragment(self):
        status, out, err = documents("clients", str(self.outputs(fragment_world())), cell.REGION)
        self.assertEqual(0, status, err)
        lines = dict(line.split("\t") for line in out.splitlines())
        clients = fragment_world()["roots"]["apps"]["clients"]
        for f in SHELL_DEFINITION["fragments"]:
            self.assertEqual(clients[SHELL]["fragments"][f["fragmentId"]]["clientId"], lines[f"{SHELL}:{f['fragmentId']}"])
        self.assertEqual(clients[SHELL]["clientId"], lines[SHELL])
        self.assertEqual(len(APPS) + 1 + len(SHELL_DEFINITION["fragments"]), len(lines))
        # Longest first, fragments among them, so one id inside another leaves no remainder.
        ids = [line.split("\t")[1] for line in out.splitlines()]
        self.assertEqual(sorted(ids, key=lambda i: -len(i)), ids)

    def test_clients_refuses_a_fragment_without_a_printable_client_id(self):
        for broken in ("", "has space", None):
            world = fragment_world()
            world["roots"]["apps"]["clients"][SHELL]["fragments"]["orders"]["clientId"] = broken
            with self.subTest(clientId=broken):
                status, _, err = documents("clients", str(self.outputs(world)), cell.REGION)
                self.assertEqual(1, status)
                self.assertIn("no printable clientId", err)
        world = fragment_world()
        world["roots"]["apps"]["clients"][SHELL]["fragments"] = ["orders"]
        status, _, err = documents("clients", str(self.outputs(world)), cell.REGION)
        self.assertEqual(1, status)
        self.assertIn("fragments that are not an object", err)

    def definition(self, value: dict) -> str:
        case = cell.Case(self)
        path = case.dir / "definition.json"
        path.write_text(json.dumps(value))
        return str(path)

    def test_fragments_gives_each_fragment_in_order_with_its_entry(self):
        status, out, err = documents("fragments", self.definition(SHELL_DEFINITION))
        self.assertEqual(0, status, err)
        self.assertEqual([f"{SHELL}\torders\t{DEFAULT_ENTRY}", f"{SHELL}\treports\tmain.js"], out.splitlines())
        self.assertEqual("entry.js", DEFAULT_ENTRY)

    def test_fragments_gives_nothing_for_a_definition_without_fragments(self):
        for app in cell.ROOT.joinpath("fixtures", "apps").glob("*.json"):
            with self.subTest(app=app.name):
                status, out, err = documents("fragments", str(app))
                self.assertEqual((0, ""), (status, out), err)

    def test_fragments_refuses_an_id_or_entry_the_registry_contract_refuses(self):
        # The seed makes paths of these, so a '/' or '..' must never reach it.
        for where, value in (("fragmentId", "../x"), ("fragmentId", "ab"), ("entry", "a/main.js"), ("entry", "main.mjs"),
                             ("entry", "x" * 62 + ".js"), ("appId", "Shell")):
            broken = json.loads(json.dumps(SHELL_DEFINITION))
            if where == "appId":
                broken["appId"] = value
            else:
                broken["fragments"][1][where] = value
            with self.subTest(where=where, value=value):
                status, out, err = documents("fragments", self.definition(broken))
                self.assertEqual(1, status, out)
                self.assertIn(f"{where} refuses", err.replace("'s ", " "))

    def test_fragment_release_needs_the_entry_and_follows_the_content(self):
        case = cell.Case(self)
        root = case.dir / "fragment"
        root.mkdir()
        status, _, err = documents("fragment-release", str(root), "main.js")
        self.assertEqual(1, status)
        self.assertIn("has no main.js", err)
        (root / "main.js").write_text("export {};\n")
        status, first, err = documents("fragment-release", str(root), "main.js")
        self.assertEqual(0, status, err)
        self.assertRegex(first.strip(), r"^fixture-[0-9a-f]{12}$")
        (root / "main.js").write_text("export const changed = 1;\n")
        self.assertNotEqual(first, documents("fragment-release", str(root), "main.js")[1])


# --- the entry point (#1348) -----------------------------------------------------------------------

# The field list cell_documents.py held at c3fa0e0, written out here from that commit as the record
# of the document write_outputs wrote then: (field, root, output), and the one optional field after.
C3FA0E0_FIELDS = (
    ("region", "platform", "region"), ("cell", "platform", "cell"), ("bucket", "platform", "bucket"),
    ("albDnsName", "platform", "alb_dns_name"), ("ecsCluster", "platform", "ecs_cluster"),
    ("ecsService", "platform", "ecs_service"), ("ecrGateway", "platform", "ecr_gateway"),
    ("ecrMockApi", "platform", "ecr_mock_api"), ("gatewayImage", "platform", "gateway_image"),
    ("issuer", "idp", "issuer"), ("cognitoDomain", "idp", "cognito_domain"), ("clients", "apps", "clients"),
    ("testUsers", "idp", "test_users"), ("logGroup", "platform", "log_group"),
)
# The optional field trunk added after c3fa0e0 (#1167), written only when the platform root publishes
# it: a cell with no bastion gets c3fa0e0's document exactly, and one with a bastion the fork's.
C3FA0E0_OPTIONAL = (("ecsBastionService", "platform", "ecs_bastion_service"),)

# write_outputs as seed.sh, deploy.sh and rollback.sh call it, after need_entry and cell_start.
DRIVER = """set -euo pipefail
PROGRAM="write-outputs"
. "$(dirname -- "$0")/cell-common.sh"
need_entry SPA_ROOTS SPA_FIELD_MAP SPA_OUTPUTS_FILE
trap cell_cleanup EXIT
cell_start
write_outputs
"""
# The operator's way in: the proof cell's entry file sourced with set -a, then the script named.
SOURCING = """set -a
. "$(dirname -- "$0")/../../infra/envs/poc/cell.env"
set +a
exec /bin/bash "$(dirname -- "$0")/$1"
"""

# The reference scripts: every file of tools/scripts/ but the proof cell's three, the doubles and
# the companions, which follow their scripts (the #1335 spec's table).
EXEMPT = ("seed.sh", "certs.sh", "platform-tfvars.sh", "cell_doubles.py")
REFERENCE = sorted(path for path in cell.SCRIPTS.iterdir()
                   if path.is_file() and path.name not in EXEMPT and not path.name.endswith("_tests.py"))


def driven(test, world: dict | None = None, **env: str | None):
    case = cell.Case(test, world=world)
    (case.repo / "tools" / "scripts" / "write-outputs.sh").write_text(DRIVER)
    return case, case.run("write-outputs.sh", env=case.env(**env))


class TheProofCellsEntryFile(cell.CompanionTestCase):
    """infra/envs/poc/cell.env sets the five entry values to the proof cell's own and names the
    field map; sourced, it makes write_outputs write what it wrote at c3fa0e0."""

    def test_it_sets_the_five_values_and_names_the_field_map(self):
        values = cell.entry_values()
        self.assertEqual(sorted(cell.ENTRY_NAMES), sorted(values))
        self.assertEqual(cell.CELL, values["SPA_CELL"])
        # The domain certs.sh issues for, read from it rather than typed here.
        certs = re.search(r'^DOMAIN="\$\{SPA_CERT_DOMAIN:-([^}]*)\}"', (cell.SCRIPTS / "certs.sh").read_text(), re.M)
        self.assertEqual(certs[1], values["SPA_CERT_DOMAIN"])
        roots = values["SPA_ROOTS"].split(":")
        self.assertEqual(["idp", "apps", "platform"], [root.rsplit("/", 1)[-1] for root in roots])
        for directory in roots:
            self.assertTrue((cell.ROOT / directory / "main.tf").is_file(), directory)
        self.assertEqual(roots[-1], values["SPA_PLATFORM_ROOT"])
        self.assertTrue((cell.ROOT / values["SPA_FIELD_MAP"]).is_file())
        self.assertEqual("infra/envs/poc/outputs.json", values["SPA_OUTPUTS_FILE"])

    def test_sourced_it_writes_from_the_three_roots_the_document_write_outputs_wrote_at_c3fa0e0(self):
        for world in (cell.default_world(), cell.with_bastion(cell.default_world())):
            with self.subTest(bastion="ecs_bastion_service" in world["roots"]["platform"]):
                case = cell.Case(self, world=world)
                scripts = case.repo / "tools" / "scripts"
                (scripts / "write-outputs.sh").write_text(DRIVER)
                (scripts / "with-entry.sh").write_text(SOURCING)
                # No entry value from the case: only what the entry file sets.
                result = case.run("with-entry.sh", "write-outputs.sh",
                                  env=case.env(**{name: None for name in cell.ENTRY_NAMES}))
                self.assertEqual(0, result.status, result.output)
                roots = world["roots"]
                expected = {field: roots[root][output] for field, root, output in C3FA0E0_FIELDS}
                expected.update({field: roots[root][output] for field, root, output in C3FA0E0_OPTIONAL
                                 if roots[root].get(output) is not None})
                self.assertEqual(json.dumps(expected, indent=2) + "\n", case.outputs_path().read_text())
                self.assertEqual(["idp", "apps", "platform"],
                                 [c["root"] for c in case.calls("terraform") if c.get("command") == "output"])


class WriteOutputsWritesWhatTheMapLists(cell.CompanionTestCase):
    """write_outputs writes exactly the fields a field map lists, from the roots SPA_ROOTS names, in
    its order, and refuses roots that disagree on region or cell, whichever two they are."""

    def other_map(self, case, fields: list[dict]) -> pathlib.Path:
        path = case.dir / "another-cell" / "fields.json"
        path.parent.mkdir()
        path.write_text(json.dumps({"fields": fields}))
        return path

    def test_a_map_other_than_the_proof_cells_from_the_roots_it_names(self):
        world = cell.default_world()
        case = cell.Case(self, world=world)
        (case.repo / "tools" / "scripts" / "write-outputs.sh").write_text(DRIVER)
        fields = self.other_map(case, [
            {"field": "region", "root": "platform", "output": "region"},
            {"field": "where", "root": "idp", "output": "issuer"},
            {"field": "bucketName", "root": "platform", "output": "bucket"},
            {"field": "absent", "root": "idp", "output": "no_such_output", "optional": True},
        ])
        out = case.dir / "another-cell" / "outputs.json"
        roots = "infra/envs/poc/platform:infra/envs/poc/idp"
        result = case.run("write-outputs.sh", env=case.env(SPA_ROOTS=roots, SPA_FIELD_MAP=str(fields),
                                                            SPA_OUTPUTS_FILE=str(out)))
        self.assertEqual(0, result.status, result.output)
        expected = {"region": world["roots"]["platform"]["region"], "where": world["roots"]["idp"]["issuer"],
                    "bucketName": world["roots"]["platform"]["bucket"]}
        self.assertEqual(json.dumps(expected, indent=2) + "\n", out.read_text())
        # The two roots on the list, in its order, and not the third.
        self.assertEqual(["platform", "idp"], [c["root"] for c in case.calls("terraform") if c.get("command") == "output"])
        self.assertFalse(case.outputs_path().exists())

    def test_a_map_naming_a_root_that_is_not_on_the_list_is_refused(self):
        case = cell.Case(self)
        (case.repo / "tools" / "scripts" / "write-outputs.sh").write_text(DRIVER)
        fields = self.other_map(case, [{"field": "region", "root": "platform", "output": "region"},
                                       {"field": "clients", "root": "apps", "output": "clients"}])
        result = case.run("write-outputs.sh", env=case.env(SPA_ROOTS="infra/envs/poc/platform", SPA_FIELD_MAP=str(fields)))
        self.assertEqual(1, result.status, result.output)
        self.assertIn("not on the roots list: apps", result.stderr)
        self.assertFalse(case.outputs_path().exists())

    def test_a_missing_output_is_refused_by_root_and_name_and_an_optional_one_is_left_out(self):
        case = cell.Case(self)
        (case.repo / "tools" / "scripts" / "write-outputs.sh").write_text(DRIVER)
        fields = self.other_map(case, [{"field": "region", "root": "platform", "output": "region"},
                                       {"field": "gone", "root": "idp", "output": "gone"}])
        result = case.run("write-outputs.sh", env=case.env(SPA_FIELD_MAP=str(fields)))
        self.assertEqual(1, result.status, result.output)
        self.assertIn("idp.gone", result.stderr)

    def test_roots_that_output_different_regions_or_cells_are_refused_whichever_two_they_are(self):
        names = ("idp", "apps", "platform")
        for first in names:
            for second in names:
                if first == second:
                    continue
                for output, other in (("region", "eu-west-1"), ("cell", "poc2")):
                    world = cell.default_world()
                    # The apps root has no region or cell output in the proof cell; given both, it
                    # is a root like the others.
                    world["roots"]["apps"].update(region=cell.REGION, cell=cell.CELL)
                    world["roots"][second][output] = other
                    case = cell.Case(self, world=world)
                    (case.repo / "tools" / "scripts" / "write-outputs.sh").write_text(DRIVER)
                    fields = self.other_map(case, [{"field": "region", "root": first, "output": "region"}])
                    roots = f"infra/envs/poc/{first}:infra/envs/poc/{second}"
                    with self.subTest(first=first, second=second, output=output):
                        result = case.run("write-outputs.sh", env=case.env(SPA_ROOTS=roots, SPA_FIELD_MAP=str(fields)))
                        self.assertEqual(1, result.status, result.output)
                        self.assertIn(f"the {first} root's {output} differs from the {second} root's", result.stderr)
                        self.assertFalse(case.outputs_path().exists())
                    # The same two roots agreeing are written: the refusal above is the difference's.
                    world["roots"][second][output] = world["roots"][first][output]
                    case = cell.Case(self, world=world)
                    (case.repo / "tools" / "scripts" / "write-outputs.sh").write_text(DRIVER)
                    fields = self.other_map(case, [{"field": "region", "root": first, "output": "region"}])
                    with self.subTest(first=first, second=second, output=output, agreeing=True):
                        result = case.run("write-outputs.sh", env=case.env(SPA_ROOTS=roots, SPA_FIELD_MAP=str(fields)))
                        self.assertEqual(0, result.status, result.output)

    def test_roots_that_agree_on_another_region_than_aws_region_are_refused(self):
        world = cell.default_world()
        for name in ("idp", "platform"):
            world["roots"][name]["region"] = "eu-west-1"
        case, result = driven(self, world)
        self.assertEqual(1, result.status, result.output)
        self.assertIn("the document's region is not AWS_REGION", result.stderr)
        self.assertFalse(case.outputs_path().exists())

    def test_a_map_with_no_region_field_is_refused(self):
        case = cell.Case(self)
        (case.repo / "tools" / "scripts" / "write-outputs.sh").write_text(DRIVER)
        fields = self.other_map(case, [{"field": "bucket", "root": "platform", "output": "bucket"}])
        result = case.run("write-outputs.sh", env=case.env(SPA_FIELD_MAP=str(fields)))
        self.assertEqual(1, result.status, result.output)
        self.assertIn("names no region field", result.stderr)

    def test_each_entry_value_write_outputs_reads_unset_exits_2_naming_it_before_any_call(self):
        for name in ("SPA_ROOTS", "SPA_FIELD_MAP", "SPA_OUTPUTS_FILE"):
            for value in (None, ""):
                with self.subTest(name=name, value=value):
                    case, result = driven(self, **{name: value})
                    self.assertEqual(2, result.status, result.output)
                    self.assertIn(f"{name} is not set", result.stderr)
                    self.assertEqual([], case.calls())

    def test_seed_refuses_each_entry_value_it_reads_unset_before_any_call(self):
        for name in ("SPA_ROOTS", "SPA_FIELD_MAP", "SPA_OUTPUTS_FILE"):
            with self.subTest(name=name):
                case = cell.Case(self)
                result = case.run(SCRIPT, env=case.env(**{name: None}))
                self.assertEqual(2, result.status, result.output)
                self.assertIn(f"{name} is not set", result.stderr)
                self.assertEqual([], case.calls())


class TheReferenceScriptsNameNoCell(unittest.TestCase):
    """What the reference scripts' sources may not hold (#1348): the proof cell's paths, the
    operator profile's name and its file, the mock API's container in the shared file, and in
    cell_documents.py any field list, root name or Cognito name."""

    def test_the_reference_scripts_are_the_ones_the_spec_names(self):
        self.assertEqual(["cell-common.sh", "cell_documents.py", "deploy.sh", "logs.sh", "rollback.sh",
                          "teardown.sh", "tunnel.sh", "verify-release-immutability.sh", "verify-teardown.sh",
                          "verify-tunnel.sh"], [path.name for path in REFERENCE])

    def test_no_reference_script_names_an_infra_envs_path(self):
        for path in REFERENCE:
            with self.subTest(path.name):
                self.assertNotIn("infra/envs/", path.read_text())

    def test_no_reference_script_names_the_profile_or_its_file(self):
        for path in REFERENCE:
            with self.subTest(path.name):
                text = path.read_text()
                self.assertNotRegex(text, r"spa-platform\s+profile")
                self.assertNotIn("aws-project.env", text)

    def test_cell_common_defines_no_mock_api_container(self):
        text = (cell.SCRIPTS / "cell-common.sh").read_text()
        self.assertNotRegex(text, r"(?m)^\s*MOCK_API_CONTAINER=")
        self.assertNotIn("mock-api", text)

    def test_cell_documents_holds_no_field_list_no_root_name_and_no_cognito(self):
        text = (cell.SCRIPTS / "cell_documents.py").read_text()
        self.assertNotRegex(text, r"(?i)cognito")
        entries = cell.field_map()["fields"]
        # A field list holds its names as string literals. The outputs every root may carry and the
        # fields other commands read are the contract's own words, not a cell's list: region and cell
        # (the cross-check), clients (the clients command).
        shared = {"region", "cell", "clients"}
        for name in sorted({entry["field"] for entry in entries} | {entry["output"] for entry in entries}):
            if name not in shared:
                with self.subTest(name=name):
                    self.assertNotRegex(text, rf"[\"']{re.escape(name)}[\"']")
        for root in sorted({entry["root"] for entry in entries}):
            with self.subTest(root=root):
                self.assertNotRegex(text, rf"\b{root} root\b")
                self.assertNotRegex(text, rf"\[\s*[\"']{root}[\"']\s*\]")
        self.assertNotRegex(text, r"\bidp\b")


if __name__ == "__main__":
    sys.exit(cell.main("seed_tests"))
