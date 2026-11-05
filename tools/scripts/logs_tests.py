#!/usr/bin/env python3
"""Test companion for tools/scripts/logs.sh. Owner: workstream 1.

#208, raised by the sitting of #58 (stage C1, 2026-10-21): the diagnosis tailed the gateway's log
group with the CLI directly, and the mock API's start line put both apps' client ids on the terminal
and into the sitting log. logs.sh is the safe path, and these cases hold it to the ticket's list:
the group the outputs document names, the window and the container the operator gives, and every
line through the shared redaction, which elides the account id and each client id the outputs
document lists.

The doubles, the world they share and the universal every run holds (no output names the account
id) are tools/scripts/cell_doubles.py's. The client ids are not in that universal's SECRETS, which
every cell script's run is held to and some of them may print, so the cases here search for them
themselves.

#1014, raised by the G6 continuity sitting of 2026-11-04: to tell a sign-out from a page served
normally, the sitting summarised 259 gateway lines by hand. --requests prints one line per gateway
request record, drops the health checks, and still passes every line through the redaction. The
record's category and the health route are read from the gateway's source and the telemetry
contract, so a change to either runs these cases.

Run: python3 -I tools/scripts/logs_tests.py
Checkout inputs: src/Platform.Telemetry/RequestTelemetry.cs contracts/telemetry.md
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

SCRIPT = "logs.sh"
REPO = pathlib.Path(__file__).resolve().parents[2]


def request_category() -> str:
    """RequestTelemetry.Category, the category the gateway writes its request line under."""
    source = (REPO / "src/Platform.Telemetry/RequestTelemetry.cs").read_text()
    return re.search(r'const string Category = "([^"]+)";', source).group(1)


def health_route() -> str:
    """The route value contracts/telemetry.md lists that is a health check's, by its name there."""
    row = next(line for line in (REPO / "contracts/telemetry.md").read_text().splitlines()
               if line.startswith("| `route` values |"))
    values = re.findall(r"`([a-z]+)`", row.split("|")[2])
    return next(value for value in values if value == "health")


def client_ids(world: dict) -> dict[str, str]:
    """appId -> clientId, from the apps root's outputs that the outputs document is written from."""
    return {app: entry["clientId"] for app, entry in world["roots"]["apps"]["clients"].items()}


def logged_world() -> dict:
    """The cell with one gateway task and one mock API task logging. The mock API's start line names
    both apps' clients, as fixtures/MockApi/Program.cs's configuration does (MockApi__App1Client and
    MockApi__App2Client in infra/modules/cell/ecs.tf); a gateway line carries an ARN, which carries
    the account id, and one client id in JSON."""
    world = cell.default_world()
    ids = client_ids(world)
    task = world["tasks"][0]["id"]
    world["log_events"] = [
        [f"{cell.MOCK_API}/{cell.MOCK_API}/{task}",
         f"mock api listening; app1 client {ids['app1']}, app2 client {ids['app2']}"],
        [f"{cell.GATEWAY}/{cell.GATEWAY}/{task}",
         json.dumps({"level": "Warning", "message": "registry read refused",
                     "resource": f"arn:aws:s3:::spa-platform-poc-{cell.ACCOUNT}-{cell.REGION}-an/registry/registry.json",
                     "clientId": ids["app1"]})],
        [f"{cell.GATEWAY}/{cell.GATEWAY}/{task}", "GET /readyz 200"],
    ]
    return world


def setUpModule():
    cell.bash_banner("logs_tests")


class LogsReadsTheGroupThroughTheRedaction(cell.CompanionTestCase):

    def read(self, world: dict, *args: str):
        case = cell.Case(self, outputs=True, world=world)
        result = case.run(SCRIPT, *args)
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        for app, client in client_ids(world).items():
            self.assertNotIn(client, result.output, f"{app}'s client id was printed")
        tails = case.aws("logs tail")
        self.assertEqual(1, len(tails))
        return result, tails[0]

    def test_a_line_carrying_a_client_id_and_an_account_id_comes_out_redacted(self):
        result, _ = self.read(logged_world())
        # The account universal is Case.run's and the client ids are read() above; these say what
        # took their place, so that a script that dropped the lines would not pass.
        self.assertIn("mock api listening; app1 client <client-id:app1>, app2 client <client-id:app2>", result.stdout)
        self.assertIn(f"arn:aws:s3:::spa-platform-poc-<account>-{cell.REGION}-an/registry/registry.json", result.stdout)
        self.assertIn('"clientId": "<client-id:app1>"', result.stdout)
        self.assertIn("GET /readyz 200", result.stdout)

    def test_the_group_is_the_one_the_outputs_document_names(self):
        world = logged_world()
        # Not the module's name for it: the outputs document is what a script reads
        # (contracts/infra-outputs.schema.json), so a group renamed there is the one read.
        world["roots"]["platform"]["log_group"] = "/spa-platform/poc/renamed"
        _, call = self.read(world)
        self.assertEqual(["/spa-platform/poc/renamed"], call["positional"])

    def test_the_window_defaults_to_ten_minutes_and_follows_since(self):
        _, call = self.read(logged_world())
        self.assertEqual("10m", call["options"]["since"])
        self.assertNotIn("follow", call["options"])
        _, call = self.read(logged_world(), "--since", "2h")
        self.assertEqual("2h", call["options"]["since"])
        _, call = self.read(logged_world(), "--since", "2026-10-21T02:00:00Z")
        self.assertEqual("2026-10-21T02:00:00Z", call["options"]["since"])

    def test_a_container_limits_the_streams_read(self):
        result, call = self.read(logged_world(), "--container", cell.MOCK_API)
        self.assertEqual(f"{cell.MOCK_API}/", call["options"]["log-stream-name-prefix"])
        self.assertIn("<client-id:app2>", result.stdout)
        self.assertNotIn("/readyz", result.stdout)
        result, call = self.read(logged_world(), "--container", cell.GATEWAY)
        self.assertEqual(f"{cell.GATEWAY}/", call["options"]["log-stream-name-prefix"])
        self.assertNotIn("mock api listening", result.stdout)
        self.assertIn("GET /readyz 200", result.stdout)

    def test_follow_is_passed_on(self):
        _, call = self.read(logged_world(), "--follow")
        self.assertIs(True, call["options"]["follow"])

    def test_a_client_id_is_redacted_as_text_and_never_as_a_pattern(self):
        # contracts/registry.schema.json allows any printable ASCII in a clientId, so a pattern
        # built from one would match other text, or fail to match itself.
        world = logged_world()
        world["roots"]["apps"]["clients"]["app1"]["clientId"] = "a.b*c[1]/&\\x"
        world["log_events"] = [[f"{cell.GATEWAY}/{cell.GATEWAY}/t", "client a.b*c[1]/&\\x and aXbbbc1 stay apart"]]
        result, _ = self.read(world)
        self.assertIn("client <client-id:app1> and aXbbbc1 stay apart", result.stdout)

    def test_a_client_id_inside_another_leaves_no_remainder_of_the_longer(self):
        # cell_documents.py clients lists the longest first: replaced in the document's order, app1's
        # id inside app2's would leave app2's ends in the line.
        world = logged_world()
        world["roots"]["apps"]["clients"]["app1"]["clientId"] = "Qz7vQ"
        world["roots"]["apps"]["clients"]["app2"]["clientId"] = "KkQz7vQKk"
        world["log_events"] = [[f"{cell.MOCK_API}/{cell.MOCK_API}/t", "app1 Qz7vQ app2 KkQz7vQKk"]]
        result, _ = self.read(world)
        self.assertIn("app1 <client-id:app1> app2 <client-id:app2>", result.stdout)
        self.assertNotIn("Kk", result.stdout)


def request(route: str, status: int, app: str | None, rule: str | None = None) -> str:
    """A request line as the gateway's PlatformJsonFormatter writes it, its fields in
    contracts/telemetry.md's order."""
    return json.dumps({"ts": f"2026-11-04T21:13:0{status % 10}.1234567+00:00", "level": "Information",
                       "msg": "request", "category": request_category(),
                       "trace_id": "4bf92f3577b34da6a3ce929d0e0e4736", "span_id": "00f067aa0ba902b7",
                       "cell": "poc", "app_id": app, "registry_rev": 7, "route": route, "rule": rule,
                       "session_ref": "a1b2c3d4e5f6", "status": status, "duration_ms": 3.25})


def requests_world() -> dict:
    """logged_world's lines, which are no request's, and a sitting's worth of request records: two
    served, a refusal, a sign-out, a request refused before its app was known, and the two health
    checks, which the telemetry stage gives the route `health` (a request line carries no path)."""
    world = logged_world()
    stream = f"{cell.GATEWAY}/{cell.GATEWAY}/{world['tasks'][0]['id']}"
    world["log_events"] += [
        [stream, request("asset", 200, "app1")],
        [stream, request(health_route(), 200, None)],
        [stream, request("api", 401, "app2", "auth.session")],
        [stream, request(health_route(), 503, None)],
        [stream, request("bff", 302, "app1")],
        [stream, request("asset", 404, None, "gate.host")],
        [stream, json.dumps({"ts": "2026-11-04T21:13:09.0000000+00:00", "level": "Information",
                             "msg": "registry swapped", "category": "Platform.Registry.Swap", "route": "asset",
                             "status": 200})],
        [stream, "{not json"],
        [stream, "[1, 2]"],
    ]
    return world


class LogsRequestsSummarisesTheGatewayRequests(cell.CompanionTestCase):

    def read(self, world: dict, *args: str):
        case = cell.Case(self, outputs=True, world=world)
        result = case.run(SCRIPT, "--requests", *args)
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        for app, client in client_ids(world).items():
            self.assertNotIn(client, result.output, f"{app}'s client id was printed")
        self.assertNotIn(cell.ACCOUNT, result.output)
        tails = case.aws("logs tail")
        self.assertEqual(1, len(tails))
        # note() writes the read's first line to standard output; every other line is a request's.
        lines = [line for line in result.stdout.splitlines() if not line.startswith("logs: ")]
        return lines, tails[0]

    def test_one_line_per_request_record_and_nothing_for_any_other_line(self):
        lines, _ = self.read(requests_world())
        self.assertEqual([
            "2026-11-04T21:13:00.1234567+00:00 01234567 app1 asset - 200",
            "2026-11-04T21:13:01.1234567+00:00 01234567 app2 api auth.session 401",
            "2026-11-04T21:13:02.1234567+00:00 01234567 app1 bff - 302",
            "2026-11-04T21:13:04.1234567+00:00 01234567 - asset gate.host 404",
        ], lines)

    def test_it_reads_the_gateway_streams_and_passes_since_and_follow_on(self):
        _, call = self.read(requests_world())
        self.assertEqual(f"{cell.GATEWAY}/", call["options"]["log-stream-name-prefix"])
        self.assertEqual("10m", call["options"]["since"])
        self.assertNotIn("follow", call["options"])
        _, call = self.read(requests_world(), "--since", "2h", "--follow")
        self.assertEqual("2h", call["options"]["since"])
        self.assertIs(True, call["options"]["follow"])
        _, call = self.read(requests_world(), "--container", cell.GATEWAY)
        self.assertEqual(f"{cell.GATEWAY}/", call["options"]["log-stream-name-prefix"])

    def test_a_request_line_passes_through_the_redaction(self):
        # A request line names no client id or account; one whose app or rule held one would still
        # be printed without it, because the summary passes through the same redaction as plain mode.
        world = requests_world()
        world["roots"]["apps"]["clients"]["app1"]["clientId"] = "Qz7vQclient"
        stream = f"{cell.GATEWAY}/{cell.GATEWAY}/{world['tasks'][0]['id']}"
        world["log_events"] = [[stream, request("api", 403, "Qz7vQclient", f"rule-{cell.ACCOUNT}")]]
        lines, _ = self.read(world)
        self.assertEqual(["2026-11-04T21:13:03.1234567+00:00 01234567 <client-id:app1> api rule-<account> 403"], lines)


class LogsRefuses(cell.CompanionTestCase):

    def refused(self, fragment: str, *args: str, status: int = 2, world: dict | None = None, outputs: bool = True):
        case = cell.Case(self, outputs=outputs, world=world or logged_world())
        result = case.run(SCRIPT, *args)
        self.assertEqual(status, result.status, result.output)
        self.assertIn(fragment, result.stderr)
        self.assertNoUnexpectedCall(case)
        return case, result

    def test_a_container_the_task_does_not_run(self):
        case, _ = self.refused("--container must be", "--container", "sidecar")
        self.assertEqual([], case.calls("aws"))

    def test_requests_from_the_mock_api(self):
        case, _ = self.refused("--requests reads the", "--requests", "--container", cell.MOCK_API)
        self.assertEqual([], case.calls("aws"))

    def test_a_window_the_cli_does_not_take(self):
        for since in ("5h30m", "ten", "10", "-5m", "0m"):
            with self.subTest(since=since):
                case, _ = self.refused("--since must be", "--since", since)
                self.assertEqual([], case.calls("aws"))

    def test_no_outputs_document(self):
        case, _ = self.refused("seed.sh", outputs=False)
        self.assertEqual([], case.aws("logs tail"))

    def test_a_read_aws_refuses_names_its_code_and_no_message(self):
        world = logged_world()
        world["faults"]["logs tail"] = ["AccessDeniedException", "FilterLogEvents"]
        # The message carries the caller's ARN, and so the account id: Case.run's universal fails
        # the case if the script printed it.
        self.refused("AccessDeniedException", status=1, world=world)


if __name__ == "__main__":
    sys.exit(cell.main("logs_tests"))
