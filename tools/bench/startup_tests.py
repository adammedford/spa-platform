#!/usr/bin/env python3
"""Test companion for tools/bench/startup.py (#173). Owner: the owner OWNERSHIP.md names for
tools/bench/.

The startup benchmark's Docker half needs a Docker daemon and minutes of wall time, so it is proved by running
it (the numbers in #173's Answer), not here. What runs here is everything that decides a number
without Docker: reading dockerd's timestamps, the probe's clocks against a real socket, the summary's
medians, maxima and gaps, the verdict mode's rules and command line (ADR-0033, where a `B-` id is
proved by the benchmark's verdict and the verdict's logic here), and the gateway's environment, read
from the contract's example rather than from a copy.

Run: python3 -I tools/bench/startup_tests.py
"""

from __future__ import annotations

import http.server
import importlib.util
import json
import pathlib
import socket
import threading
import time
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent
spec = importlib.util.spec_from_file_location("startup", ROOT / "tools" / "bench" / "startup.py")
startup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(startup)


class DockerTime(unittest.TestCase):
    def test_reads_dockerds_nanoseconds(self):
        # A State.StartedAt as `docker inspect` prints it, and the same instant worked by hand:
        # 2026-10-21T04:41:06Z is 1792557666 seconds after the epoch.
        self.assertEqual(1792557666_349093412, startup.docker_time_ns("2026-10-21T04:41:06.349093412Z"))

    def test_reads_a_shorter_fraction_as_a_fraction(self):
        self.assertEqual(1792557666_300000000, startup.docker_time_ns("2026-10-21T04:41:06.3Z"))


class LateServer:
    """A gateway stand-in that starts listening late and turns ready on its third /readyz."""

    def __init__(self, delay_s: float, app_status: int = 401):
        probe = socket.socket()
        probe.bind(("127.0.0.1", 0))
        self.port = probe.getsockname()[1]
        probe.close()
        self.readyz_calls = 0
        self.app_status = app_status
        self.server = None
        self.timer = threading.Timer(delay_s, self._start)
        self.timer.start()

    def _start(self):
        outer = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_GET(self):
                if self.path == "/healthz":
                    status = 200
                elif self.path == "/readyz":
                    outer.readyz_calls += 1
                    status = 200 if outer.readyz_calls >= 3 else 503
                elif self.headers.get("Host") == "app1.spa-poc.test:8443" and self.headers.get("Sec-Fetch-Mode") == "cors":
                    status = outer.app_status
                else:
                    status = 404
                self.send_response(status)
                self.send_header("Content-Length", "0")
                self.end_headers()

        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", self.port), Handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def close(self):
        self.timer.cancel()
        if self.server:
            self.server.shutdown()
            self.server.server_close()


class Probe(unittest.TestCase):
    STEPS = [
        startup.Step("healthz", "/healthz", 200),
        startup.Step("readyz", "/readyz", 200),
        startup.Step("app_host", "/", 401, host="app1.spa-poc.test:8443",
                     headers={"Sec-Fetch-Mode": "cors", "Sec-Fetch-Dest": "empty", "Sec-Fetch-Site": "same-origin"}),
    ]

    def test_each_clock_is_its_first_expected_answer_in_order(self):
        server = LateServer(0.3)
        self.addCleanup(server.close)
        begun = time.time_ns()
        marks = startup.probe("127.0.0.1", server.port, self.STEPS, interval_s=0.005, deadline_s=10)

        names = [name for name in ("listening", "healthz", "readyz", "app_host")]
        times = [marks[name]["at_ns"] for name in names]
        self.assertTrue(all(t is not None for t in times), marks)
        self.assertEqual(sorted(times), times)
        # Nothing listens for the first 300 ms, so listening cannot be seen before then.
        self.assertGreaterEqual(times[0] - begun, 250_000_000)
        # /readyz answered 503 twice before the 200 the clock stops on.
        self.assertEqual(3, server.readyz_calls)
        self.assertEqual(200, marks["readyz"]["status"])
        # The precision bound: the failed attempt before each success started no later than it.
        for name in names:
            self.assertLessEqual(marks[name]["after_ns"], marks[name]["at_ns"])
        # ...and it is the attempt just before, not the first: polling every 5 ms, the window around
        # listening is a few ms wide, not the 300 ms nothing listened for.
        self.assertLess(marks["listening"]["at_ns"] - marks["listening"]["after_ns"], 100_000_000)

    def test_a_clock_whose_answer_never_comes_is_unreached_and_says_what_came(self):
        server = LateServer(0.0, app_status=500)
        self.addCleanup(server.close)
        marks = startup.probe("127.0.0.1", server.port, self.STEPS, interval_s=0.01, deadline_s=1.5)

        self.assertIsNotNone(marks["readyz"]["at_ns"])
        self.assertIsNone(marks["app_host"]["at_ns"])
        self.assertEqual(500, marks["app_host"]["status"])


def run(started_s: int, **after_ms):
    """A run as the startup benchmark records one: dockerd's StartedAt and the probe's marks, in ns."""
    started = started_s * 1_000_000_000
    marks = {name: {"at_ns": None if ms is None else started + ms * 1_000_000, "after_ns": started, "attempts": 1,
                    "status": None} for name, ms in after_ms.items()}
    return {"started_ns": started, "marks": marks}


FLOOR = [run(10, listening=100, healthz=130), run(20, listening=120, healthz=150), run(30, listening=110, healthz=140)]
GATEWAY = [
    run(11, listening=900, healthz=910, readyz=1500, app_host=1800),
    run(21, listening=1000, healthz=1010, readyz=1700, app_host=2000),
    run(31, listening=950, healthz=960, readyz=1600, app_host=1900),
]


class Summary(unittest.TestCase):
    def test_each_clock_is_milliseconds_from_started_at(self):
        self.assertEqual({"listening": 900.0, "healthz": 910.0, "readyz": 1500.0, "app_host": 1800.0},
                         startup.clocks(GATEWAY[0]))

    def test_median_and_max_per_clock(self):
        summary = startup.summarise(GATEWAY)
        self.assertEqual({"median": 950.0, "max": 1000.0, "reached": 3, "runs": 3}, summary["listening"])
        self.assertEqual({"median": 1900.0, "max": 2000.0, "reached": 3, "runs": 3}, summary["app_host"])

    def test_the_median_is_the_middle_run_not_the_mean(self):
        runs = [run(i, listening=ms) for i, ms in enumerate([10, 90, 20])]
        self.assertEqual(20.0, startup.summarise(runs)["listening"]["median"])

    def test_an_even_count_takes_the_mean_of_the_middle_two(self):
        runs = [run(i, listening=ms) for i, ms in enumerate([40, 10, 30, 20])]
        self.assertEqual(25.0, startup.summarise(runs)["listening"]["median"])

    def test_an_unreached_clock_is_counted_and_left_out_of_the_statistics(self):
        runs = GATEWAY[:2] + [run(41, listening=950, healthz=960, readyz=1600, app_host=None)]
        self.assertEqual({"median": 1900.0, "max": 2000.0, "reached": 2, "runs": 3},
                         startup.summarise(runs)["app_host"])

    def test_the_gap_is_the_gateway_over_the_floor_and_readiness_is_over_the_floors_healthz(self):
        gaps = startup.gaps(startup.summarise(FLOOR), startup.summarise(GATEWAY))
        self.assertEqual({"median": 840.0, "max": 880.0, "against": "listening"}, gaps["listening"])
        self.assertEqual({"median": 820.0, "max": 860.0, "against": "healthz"}, gaps["healthz"])
        # The floor has no registry, so it is ready when /healthz answers: the clocks it lacks are
        # measured against that.
        self.assertEqual({"median": 1460.0, "max": 1550.0, "against": "healthz"}, gaps["readyz"])
        self.assertEqual({"median": 1760.0, "max": 1850.0, "against": "healthz"}, gaps["app_host"])

    def test_the_table_prints_each_number_once_per_column(self):
        text = startup.render_table(startup.summarise(FLOOR), startup.summarise(GATEWAY))
        app_row = next(line for line in text.splitlines() if line.startswith("first app-host 401"))
        self.assertEqual(["1900", "2000", "1760", "1850"], app_row.split()[-4:])
        listening_row = next(line for line in text.splitlines() if line.startswith("listening"))
        self.assertEqual(["110", "120", "950", "1000", "840", "880"], listening_row.split()[-6:])


class Windows(unittest.TestCase):
    def test_each_clocks_widest_window_bounds_its_precision(self):
        first = run(1, listening=100, healthz=130)
        second = run(2, listening=100, healthz=130)
        first["marks"]["healthz"]["after_ns"] = first["marks"]["healthz"]["at_ns"] - 12_000_000
        second["marks"]["healthz"]["after_ns"] = second["marks"]["healthz"]["at_ns"] - 45_000_000
        first["marks"]["listening"]["after_ns"] = first["marks"]["listening"]["at_ns"] - 7_000_000
        second["marks"]["listening"]["after_ns"] = second["marks"]["listening"]["at_ns"] - 3_000_000
        # The log-line clock is dockerd's stamp, not a poll, and has no window.
        second["marks"]["listening_log"] = {"at_ns": 5, "after_ns": None, "attempts": 0, "status": None}
        self.assertEqual({"listening": 7.0, "healthz": 45.0}, startup.windows_ms([first, second]))


class GatewayEnvironment(unittest.TestCase):
    """The task environment infra/modules/cell/ecs.tf builds, from the contract's example file."""

    def setUp(self):
        self.example = json.loads((ROOT / "contracts" / "platform-config.example.json").read_text())
        self.env = startup.gateway_environment(self.example, endpoint="http://10.90.0.10:5000",
                                               discovery="http://127.0.0.1:9000")

    def test_sections_flatten_the_way_ecs_tf_names_them(self):
        self.assertEqual(".spa-poc.test:8443", self.env["Policy__AllowedHostSuffixes__0"])
        self.assertEqual("http://127.0.0.1:5081", self.env["Policy__AllowedUpstreams__1"])
        self.assertEqual("15", self.env["Registry__PollSeconds"])
        self.assertEqual("cognito", self.env["Issuers__cognito-poc__Profile"])
        self.assertEqual(self.example["Registry"]["Bucket"], self.env["Registry__Bucket"])

    def test_every_issuer_discovers_from_the_loopback_stand_in(self):
        self.assertEqual("http://127.0.0.1:9000", self.env["Issuers__cognito-poc__Authority"])

    def test_the_sdk_chain_resolves_from_the_environment_alone(self):
        # docs/research/local-aws-stand-in.md section 1: the endpoint, AWS_REGION (the .NET chain reads
        # no other) and a static key pair, so no profile file and no metadata endpoint is tried.
        self.assertEqual("http://10.90.0.10:5000", self.env["AWS_ENDPOINT_URL"])
        self.assertEqual("us-east-1", self.env["AWS_REGION"])
        self.assertIn("AWS_ACCESS_KEY_ID", self.env)
        self.assertIn("AWS_SECRET_ACCESS_KEY", self.env)


def starts(kind: str, count: int, **after_ms):
    """`count` constrained cold starts of one kind, each with the same clocks, as run_bench records them."""
    return [dict(run(100 * i + (1 if kind == "gateway" else 0), **after_ms), kind=kind, constrained=True, index=i,
                 start_cli_ms=50.0)
            for i in range(count)]


QUIET_FLOOR = starts("floor", 10, listening=200, healthz=450)
GOOD_GATEWAY = starts("gateway", 10, listening=800, healthz=1200, readyz=2200, app_host=2600)
BUDGETS_MS = {"healthz": 4000.0, "readyz": 6000.0, "app_host": 7000.0}


class Verdict(unittest.TestCase):
    """The verdict mode's rules (ADR-0033 Decision, the assertion; #175 Q7; #176 Q3 and Q6)."""

    def test_a_max_over_its_budget_fails(self):
        slow = GOOD_GATEWAY[:9] + starts("gateway", 1, listening=800, healthz=1200, readyz=6100, app_host=6500)
        verdict = startup.verdict(QUIET_FLOOR + slow, BUDGETS_MS)
        self.assertEqual("fail", verdict.status)
        self.assertTrue(verdict.line.startswith("bench: fail: "), verdict.line)
        self.assertIn("/readyz 200 max 6100 ms over its budget of 6000 ms", verdict.line)
        self.assertNotEqual(0, verdict.exit_code)

    def test_a_start_that_never_reached_a_budgeted_clock_fails(self):
        # Today's summary drops such a start from the max (#176 Q3); the verdict counts it.
        stalled = GOOD_GATEWAY[:9] + starts("gateway", 1, listening=800, healthz=1200, readyz=2200, app_host=None)
        verdict = startup.verdict(QUIET_FLOOR + stalled, BUDGETS_MS)
        self.assertEqual("fail", verdict.status)
        self.assertIn("first app-host 401 not reached in 1 of 10 starts", verdict.line)

    def test_n_other_than_ten_gives_no_verdict(self):
        verdict = startup.verdict(QUIET_FLOOR[:3] + GOOD_GATEWAY[:3], BUDGETS_MS)
        self.assertEqual("no verdict", verdict.status)
        self.assertTrue(verdict.line.startswith("bench: no verdict: "), verdict.line)
        self.assertIn("3 gateway starts", verdict.line)
        self.assertNotEqual(0, verdict.exit_code)

    def test_a_breach_on_a_loaded_floor_gives_no_verdict(self):
        # The floor's /healthz max above about 1.2 s shows a loaded machine (#175 Q7).
        loaded = QUIET_FLOOR[:9] + starts("floor", 1, listening=900, healthz=1300)
        slow = GOOD_GATEWAY[:9] + starts("gateway", 1, listening=800, healthz=1200, readyz=6100, app_host=6500)
        verdict = startup.verdict(loaded + slow, BUDGETS_MS)
        self.assertEqual("no verdict", verdict.status)
        self.assertIn("floor /healthz 200 max 1300 ms", verdict.line)
        self.assertIn("re-run once", verdict.line)
        self.assertNotEqual(0, verdict.exit_code)
        self.assertNotEqual(startup.verdict(QUIET_FLOOR + slow, BUDGETS_MS).exit_code, verdict.exit_code)

    def test_a_pass_counts_regardless_of_load(self):
        loaded = QUIET_FLOOR[:9] + starts("floor", 1, listening=900, healthz=1300)
        self.assertEqual("pass", startup.verdict(loaded + GOOD_GATEWAY, BUDGETS_MS).status)

    def test_a_floor_that_never_reached_healthz_counts_as_loaded(self):
        stalled = QUIET_FLOOR[:9] + starts("floor", 1, listening=900, healthz=None)
        slow = GOOD_GATEWAY[:9] + starts("gateway", 1, listening=800, healthz=1200, readyz=6100, app_host=6500)
        self.assertEqual("no verdict", startup.verdict(stalled + slow, BUDGETS_MS).status)

    def test_a_pass_names_each_max_against_its_budget_and_exits_zero(self):
        verdict = startup.verdict(QUIET_FLOOR + GOOD_GATEWAY, BUDGETS_MS)
        self.assertEqual("pass", verdict.status)
        self.assertEqual(0, verdict.exit_code)
        self.assertTrue(verdict.line.startswith("bench: pass: "), verdict.line)
        self.assertIn("/healthz 200 max 1200 ms <= 4000 ms", verdict.line)
        self.assertIn("/readyz 200 max 2200 ms <= 6000 ms", verdict.line)
        self.assertIn("first app-host 401 max 2600 ms <= 7000 ms", verdict.line)
        # The floor is reported only (#176 Q1), and the median is the headline, never the check.
        self.assertIn("floor /healthz 200 max 450 ms", verdict.line)
        self.assertIn("medians", verdict.line)

    def test_unconstrained_starts_are_reported_and_never_judged(self):
        # The budget holds under --cpus 0.25 --memory 1g (ADR-0033, the method).
        unconstrained = [dict(one, constrained=False) for one in
                         starts("gateway", 10, listening=800, healthz=1200, readyz=9000, app_host=9500)]
        self.assertEqual("pass", startup.verdict(QUIET_FLOOR + GOOD_GATEWAY + unconstrained, BUDGETS_MS).status)


FACTS = {"commit": "0" * 40, "uncommitted_changes_not_measured": False,
         "images": {"floor": {"tag": "f", "id": "sha256:" + "a" * 64}, "gateway": {"tag": "g", "id": "sha256:" + "b" * 64}},
         "docker": {"server": "29.9.1", "os": "Docker Desktop", "arch": "aarch64", "cpus": 12, "memory": 8 * 2**30},
         "host": {"machine": "arm64", "system": "macOS", "python": "3.9.6"},
         "runs": 10, "cpus": "0.25", "memory": "1g", "interval_ms": 10}


class VerdictMode(unittest.TestCase):
    """The command line of the verdict mode, and what a run prints and returns with and without it."""

    def test_budgets_are_named_per_clock_in_seconds(self):
        options = startup.parse_args(["--verdict", "--budget", "healthz=4", "--budget", "readyz=6", "--budget", "app_host=7"])
        self.assertEqual({"healthz": 4000.0, "readyz": 6000.0, "app_host": 7000.0}, options.budgets_ms)

    def test_a_budget_on_a_clock_that_is_not_budgeted_is_refused(self):
        with self.assertRaises(SystemExit):
            startup.parse_args(["--verdict", "--budget", "ready=6"])
        # Listening is reported only (#175 Q1), so a budget on it is refused, not held.
        with self.assertRaises(SystemExit):
            startup.parse_args(["--verdict", "--budget", "listening=1"])

    def test_a_verdict_without_a_budget_is_refused(self):
        with self.assertRaises(SystemExit):
            startup.parse_args(["--verdict"])

    def test_a_verdict_with_n_other_than_ten_is_refused_before_any_start(self):
        with self.assertRaises(SystemExit):
            startup.parse_args(["--verdict", "--budget", "healthz=4", "--runs", "3"])

    def conclude(self, argv, runs):
        import io
        import contextlib
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = startup.conclude(startup.parse_args(argv), FACTS, runs)
        return code, out.getvalue().rstrip("\n").splitlines()

    def test_without_the_verdict_mode_a_run_reports_as_before_and_returns_zero(self):
        slow = GOOD_GATEWAY[:9] + starts("gateway", 1, listening=800, healthz=1200, readyz=6100, app_host=6500)
        code, lines = self.conclude([], QUIET_FLOOR + slow)
        self.assertEqual(0, code)
        self.assertFalse(any(line.startswith("bench: ") for line in lines), lines)
        self.assertTrue(lines[-1].startswith("`docker start` took"), lines[-1])

    def test_with_the_verdict_mode_the_verdict_is_the_last_line_and_the_exit_code(self):
        slow = GOOD_GATEWAY[:9] + starts("gateway", 1, listening=800, healthz=1200, readyz=6100, app_host=6500)
        argv = ["--verdict", "--budget", "healthz=4", "--budget", "readyz=6", "--budget", "app_host=7"]
        code, lines = self.conclude(argv, QUIET_FLOOR + slow)
        self.assertEqual(1, code)
        self.assertTrue(lines[-1].startswith("bench: fail: /readyz 200 max 6100 ms"), lines[-1])
        code, lines = self.conclude(argv, QUIET_FLOOR + GOOD_GATEWAY)
        self.assertEqual(0, code)
        self.assertTrue(lines[-1].startswith("bench: pass: "), lines[-1])

    def test_the_json_file_carries_the_verdict(self):
        import tempfile
        with tempfile.TemporaryDirectory() as folder:
            path = pathlib.Path(folder) / "run.json"
            argv = ["--verdict", "--budget", "healthz=4", "--json", str(path)]
            self.conclude(argv, QUIET_FLOOR + GOOD_GATEWAY)
            written = json.loads(path.read_text())
        self.assertEqual("pass", written["verdict"]["status"])
        self.assertEqual({"healthz": 4000.0}, written["facts"]["budgets_ms"])
        self.assertEqual(20, len(written["runs"]))


if __name__ == "__main__":
    unittest.main()
