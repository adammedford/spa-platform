#!/usr/bin/env python3
"""Test companion for tools/wiz_d6.sh, row 6's lever in the `wiz` skill's sitting D. Owner: integrator.

Run: python3 -I tools/wiz_d6_tests.py

Built in #1204. In sitting D of 2026-11-14 the agent wrote row 6's lever as a ten-line inline script
three times: start the row's test, launch `deploy.sh` while it runs, and on the pass line stop the
tunnel, note the time and start it again through a new task. One launch went to a test that had
already died, and only a long delay left time to stop `deploy.sh` before it applied. Each case here
runs the real script against doubles and needs no cell.

**What runs is the script itself**, under /bin/bash (3.2 on macOS, the Bash tool's shell, #183), from
a copy in a throwaway tree of its own: tools/wiz_d6.sh and tools/wiz_redact.py copied whole, and
doubles of tools/scripts/deploy.sh and tools/scripts/tunnel.sh beside them, which the script finds
from its own path. Nothing reaches AWS: no real deploy.sh or tunnel.sh is in the tree.

**The doubles.** Each appends its name and arguments to the case's `calls` file and reads what to do
from a file of the case's. The deploy double prints `deploy.sh`'s pass line, or a failure and exit 3,
or exits 0 with no pass line, and prints an ARN so a case can see stdout redacted. The tunnel double
prints `tunnel.sh`'s `through task` line and the Session Manager plugin's `SessionId` line, then
execs into a process whose arguments carry `AWS-StartPortForwardingSessionToRemoteHost` and the
`localPortNumber`, as `aws ssm start-session` does once tunnel.sh execs into it; or it exits 1
before naming a task. A running tunnel is such a process started by the case, on a local port of
the case's own, so a case never meets a real tunnel on 8443.

**The launch line is the driver's.** `LaunchLine` in tests/e2e/Stack/RollingDeployLoop.cs is the
text the row's driver writes (#1203); `launch_line()` reads it from there and the fixtures write it,
so a script waiting for anything else fails the passing case (CODING_STANDARDS.md, "A guard reads
the thing it guards").
"""
from __future__ import annotations

import json
import os
import pathlib
import random
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "tools" / "wiz_d6.sh"
REDACT = ROOT / "tools" / "wiz_redact.py"
DRIVER = ROOT / "tests" / "e2e" / "Stack" / "RollingDeployLoop.cs"

NEW_TASK = "efe39edbcbb94e2da35cc3df84907f8a"
STAMP = re.compile(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z")

DEPLOY_DOUBLE = r"""#!/bin/bash
echo "deploy.sh $*" >> "$WIZ_D6_CASE/calls"
mode="$(cat "$WIZ_D6_CASE/deploy.mode")"
echo "deploy: the task role arn:aws:iam::123456789012:role/spa-poc-task"
case "$mode" in
  pass)
    echo "deploy: steady: task definition spa-platform-poc:24 runs gateway sha256:95e771b1dd80ca1e82e59a291208b72833e6e64bf041cfec305a0ce4e88daf00, healthy on /readyz"
    echo "deploy: pass: the gateway sha256:95e771b1dd80ca1e82e59a291208b72833e6e64bf041cfec305a0ce4e88daf00 runs steady in spa-poc"
    exit 0 ;;
  fail)
    echo "deploy: fail: the service did not reach a steady state" >&2
    exit 3 ;;
  silent)
    exit 0 ;;
esac
"""

TUNNEL_DOUBLE = r"""#!/bin/bash
echo "tunnel.sh $*" >> "$WIZ_D6_CASE/calls"
port="$2"
if [ "$(cat "$WIZ_D6_CASE/tunnel.mode")" = "die" ]; then
  echo "tunnel: no task of bastion in spa-poc can carry a session:" >&2
  exit 1
fi
echo "$$" > "$WIZ_D6_CASE/tunnel.pid"
echo "tunnel: forwarding local port ${port} to internal-x.elb.amazonaws.com:443"
echo "tunnel: through task NEW_TASK, container bastion, of bastion in spa-poc"
echo "Starting session with SessionId: botocore-session-1763251200-0a1b2c3d4e5f60789" >&2
exec python3 -c 'import time; time.sleep(120)' ssm start-session --document-name AWS-StartPortForwardingSessionToRemoteHost \
  --parameters "{\"host\":[\"x\"],\"portNumber\":[\"443\"],\"localPortNumber\":[\"${port}\"]}"
""".replace("NEW_TASK", NEW_TASK)


def launch_line() -> str:
    found = re.search(r'public const string LaunchLine = "([^"]+)";', DRIVER.read_text(encoding="utf-8"))
    if found is None:
        raise AssertionError(f"{DRIVER} declares no LaunchLine")
    return found.group(1)


def session(port: int) -> subprocess.Popen:
    """A process shaped as a running tunnel.sh is once it has exec'd into the session."""
    return subprocess.Popen(
        [sys.executable, "-c", "import time; time.sleep(120)", "ssm", "start-session",
         "--document-name", "AWS-StartPortForwardingSessionToRemoteHost",
         "--parameters", f'{{"host":["x"],"portNumber":["443"],"localPortNumber":["{port}"]}}'],
        stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def plugin(port: int) -> subprocess.Popen:
    """A process shaped as the session-manager-plugin the CLI runs: its arguments carry the request
    as json.dumps writes it, with a space after each colon (awscli/customizations/sessionmanager.py)."""
    request = json.dumps({"Target": "ecs:spa-poc_x_y", "DocumentName": "AWS-StartPortForwardingSessionToRemoteHost",
                          "Parameters": {"host": ["x"], "portNumber": ["443"], "localPortNumber": [str(port)]}})
    return subprocess.Popen(
        [sys.executable, "-c", "import time; time.sleep(120)", "{}", "us-east-2", "StartSession", "", request,
         "https://ssm.us-east-2.amazonaws.com"],
        stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


class Case(unittest.TestCase):
    def setUp(self) -> None:
        made = tempfile.TemporaryDirectory()
        self.addCleanup(made.cleanup)
        self.dir = pathlib.Path(made.name)
        tools = self.dir / "tree" / "tools"
        (tools / "scripts").mkdir(parents=True)
        shutil.copy2(SCRIPT, tools / "wiz_d6.sh")
        shutil.copy2(REDACT, tools / "wiz_redact.py")
        for name, text in (("deploy.sh", DEPLOY_DOUBLE), ("tunnel.sh", TUNNEL_DOUBLE)):
            (tools / "scripts" / name).write_text(text, encoding="utf-8")
            (tools / "scripts" / name).chmod(0o755)
        self.script = tools / "wiz_d6.sh"
        self.port = random.randint(41000, 59000)
        self.record = self.dir / "record.log"
        self.output = self.dir / "row6-test.out"
        self.mode("deploy", "pass")
        self.mode("tunnel", "up")
        self.addCleanup(self.stop_restarted)

    def mode(self, double: str, mode: str) -> None:
        (self.dir / f"{double}.mode").write_text(mode, encoding="utf-8")

    def stop_restarted(self) -> None:
        pid_file = self.dir / "tunnel.pid"
        if pid_file.exists():
            try:
                os.kill(int(pid_file.read_text()), signal.SIGKILL)
            except (ProcessLookupError, ValueError):
                pass

    def alive_test(self) -> subprocess.Popen:
        test = subprocess.Popen(["sleep", "120"])
        self.addCleanup(self.end, test)
        return test

    def old_tunnel(self, port: int | None = None) -> subprocess.Popen:
        old = session(port or self.port)
        self.addCleanup(self.end, old)
        return old

    @staticmethod
    def end(process: subprocess.Popen) -> None:
        if process.poll() is None:
            process.kill()
        process.wait()

    def said(self, text: str = "") -> None:
        self.output.write_text(
            "Running tests...\n"
            + (f"{launch_line()} (2026-11-15T16:15:11.0000000Z): {text}\n" if text is not None else ""),
            encoding="utf-8")

    def run_d6(self, pid: int, *extra: str) -> subprocess.CompletedProcess:
        env = {"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "HOME": str(self.dir), "WIZ_D6_CASE": str(self.dir)}
        return subprocess.run(
            ["/bin/bash", str(self.script), "--test-pid", str(pid), "--test-output", str(self.output),
             "--record", str(self.record), "--local-port", str(self.port), "--tunnel-wait", "10", *extra],
            capture_output=True, text=True, env=env, timeout=90, check=False)

    def calls(self) -> list[str]:
        path = self.dir / "calls"
        return path.read_text(encoding="utf-8").splitlines() if path.exists() else []

    def record_text(self) -> str:
        return self.record.read_text(encoding="utf-8") if self.record.exists() else ""


class OnThePassLine(Case):
    def test_it_launches_deploy_on_the_launch_line_and_moves_the_tunnel_through_a_new_task(self) -> None:
        test, old = self.alive_test(), self.old_tunnel()
        self.said("the first session refreshes 00:04:00 after its sign-in")
        done = self.run_d6(test.pid, "--wait", "10")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(self.calls(), ["deploy.sh ", f"tunnel.sh --local-port {self.port}"])
        self.assertIsNotNone(old.poll(), "the running tunnel was not stopped")
        record = self.record_text()
        stopped = re.search(r"^wiz_d6: tunnel stopped at (\S+)$", record, re.M)
        started = re.search(r"^wiz_d6: tunnel started at (\S+)$", record, re.M)
        self.assertIsNotNone(stopped, record)
        self.assertIsNotNone(started, record)
        self.assertRegex(stopped.group(1), STAMP)
        self.assertRegex(started.group(1), STAMP)
        self.assertLessEqual(stopped.group(1), started.group(1))
        self.assertIn(f"\nwiz_d6: through task {NEW_TASK}\n", record)
        self.assertIn("deploy: pass: the gateway", record)
        restarted = int((self.dir / "tunnel.pid").read_text())
        os.kill(restarted, 0)  # the new tunnel outlives the script

    def test_it_prints_no_account_id_arn_or_session_id(self) -> None:
        test, _ = self.alive_test(), self.old_tunnel()
        self.said("now")
        done = self.run_d6(test.pid, "--wait", "10")
        self.assertEqual(done.returncode, 0, done.stderr)
        printed = done.stdout + done.stderr
        self.assertIn("deploy: pass: the gateway", printed)
        self.assertIn(NEW_TASK, printed)
        for leak in ("123456789012", "arn:aws", "SessionId", "botocore-session"):
            self.assertNotIn(leak, printed)

    def test_it_passes_its_arguments_after_the_double_dash_to_deploy(self) -> None:
        test = self.alive_test()
        self.said("now")
        done = self.run_d6(test.pid, "--wait", "10", "--keepalive", "300", "--", "--url", "https://app1.spa-poc.test:9443")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(self.calls(), ["deploy.sh --url https://app1.spa-poc.test:9443",
                                        f"tunnel.sh --local-port {self.port} --keepalive 300"])

    def test_with_no_tunnel_running_it_says_so_and_starts_one(self) -> None:
        test = self.alive_test()
        self.said("now")
        done = self.run_d6(test.pid, "--wait", "10")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertIn(f"wiz_d6: no tunnel was running on local port {self.port}; none was stopped\n", self.record_text())
        self.assertIn(f"wiz_d6: through task {NEW_TASK}\n", self.record_text())

    def test_the_cli_s_plugin_child_that_holds_the_port_is_stopped_too(self) -> None:
        test, old = self.alive_test(), self.old_tunnel()
        held, other = plugin(self.port), plugin(self.port + 1)
        self.addCleanup(self.end, held)
        self.addCleanup(self.end, other)
        self.said("now")
        done = self.run_d6(test.pid, "--wait", "10")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertIsNotNone(old.poll(), "the CLI was not stopped")
        self.assertIsNotNone(held.poll(), "the plugin holding the port was left running")
        self.assertIsNone(other.poll(), "a plugin on another port was stopped")

    def test_a_tunnel_on_another_local_port_is_left_running(self) -> None:
        test, other = self.alive_test(), self.old_tunnel(self.port + 1)
        self.said("now")
        done = self.run_d6(test.pid, "--wait", "10")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertIsNone(other.poll(), "a tunnel on another port was stopped")

    def test_a_restarted_tunnel_that_names_no_task_fails_the_lever(self) -> None:
        test = self.alive_test()
        self.mode("tunnel", "die")
        self.said("now")
        done = self.run_d6(test.pid, "--wait", "10")
        self.assertEqual(done.returncode, 1)
        self.assertIn("tunnel.sh exited before it named its task", done.stderr)
        self.assertNotIn("through task", self.record_text())


class LaunchesNothing(Case):
    def assertNothingLaunched(self) -> None:
        self.assertEqual(self.calls(), [])

    def test_when_the_test_is_no_longer_running(self) -> None:
        ended = subprocess.Popen(["true"])
        ended.wait()
        self.said("now")
        done = self.run_d6(ended.pid, "--wait", "10")
        self.assertEqual(done.returncode, 1)
        self.assertIn(f"the row 6 test (pid {ended.pid}) is no longer running; deploy.sh was not launched", done.stderr)
        self.assertNothingLaunched()

    def test_when_the_test_ends_while_it_waits_for_the_line(self) -> None:
        test = self.alive_test()
        self.said(None)
        ender = threading.Timer(2, test.kill)
        ender.start()
        self.addCleanup(ender.cancel)
        done = self.run_d6(test.pid, "--wait", "30")
        self.assertEqual(done.returncode, 1)
        self.assertIn("is no longer running; deploy.sh was not launched", done.stderr)
        self.assertNothingLaunched()

    def test_when_the_launch_line_never_comes_within_the_bound(self) -> None:
        test = self.alive_test()
        self.said(None)
        done = self.run_d6(test.pid, "--wait", "2")
        self.assertEqual(done.returncode, 1)
        self.assertIn(f"\"{launch_line()}\" did not come in {self.output} within 2 s (--wait); deploy.sh was not launched",
                      done.stderr)
        self.assertNothingLaunched()

    def test_when_the_output_file_never_appears(self) -> None:
        test = self.alive_test()
        done = self.run_d6(test.pid, "--wait", "2")
        self.assertEqual(done.returncode, 1)
        self.assertIn("within 2 s (--wait); deploy.sh was not launched", done.stderr)
        self.assertNothingLaunched()

    def test_on_a_usage_error(self) -> None:
        env = {"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "WIZ_D6_CASE": str(self.dir)}
        done = subprocess.run(["/bin/bash", str(self.script), "--record", str(self.record)],
                              capture_output=True, text=True, env=env, timeout=30, check=False)
        self.assertEqual(done.returncode, 2)
        self.assertIn("usage:", done.stderr)
        self.assertNothingLaunched()


class WithoutThePassLine(Case):
    def test_a_failed_deploy_starts_no_tunnel_and_exits_with_deploy_s_status(self) -> None:
        test, old = self.alive_test(), self.old_tunnel()
        self.mode("deploy", "fail")
        self.said("now")
        done = self.run_d6(test.pid, "--wait", "10")
        self.assertEqual(done.returncode, 3)
        self.assertEqual(self.calls(), ["deploy.sh "])
        self.assertIsNone(old.poll(), "the tunnel was stopped though deploy.sh did not pass")
        self.assertIn("deploy.sh exited 3 without its pass line; no tunnel was stopped or started", done.stderr)

    def test_a_deploy_that_exits_0_with_no_pass_line_starts_no_tunnel_and_exits_1(self) -> None:
        # #1284: d.md's D6 reads the lever's exit 0 as the tunnel having moved, so a deploy.sh that
        # exits 0 without its pass line (its --help, say) must not give the lever's exit 0.
        test, old = self.alive_test(), self.old_tunnel()
        self.mode("deploy", "silent")
        self.said("now")
        done = self.run_d6(test.pid, "--wait", "10")
        self.assertEqual(done.returncode, 1)
        self.assertEqual(self.calls(), ["deploy.sh "])
        self.assertIsNone(old.poll(), "the tunnel was stopped though deploy.sh did not pass")
        self.assertIn("deploy.sh exited 0 without its pass line; no tunnel was stopped or started", done.stderr)
        self.assertNotIn("wiz_d6: through task", self.record_text())


class Header(unittest.TestCase):
    def test_the_header_says_what_it_prints_none_of(self) -> None:
        header = "".join(line for line in SCRIPT.read_text(encoding="utf-8").splitlines(True) if line.startswith("#"))
        self.assertIn("prints no account id, ARN or session id", " ".join(header.replace("#", " ").split()))


if __name__ == "__main__":
    unittest.main()
