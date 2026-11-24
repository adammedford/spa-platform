#!/usr/bin/env python3
"""Test companion for tools/smoke.sh's refusal case (#1401, ADR-0042 decision 2).

Owner: integrator.

#1401's third box is that no line the refusal case prints carries a configured value. The script
holds it by construction: its verdicts are fixed text, a key and an exit status, and it never prints
the refused gateway's log. A promise that output never carries a value is proved by planting one and
looking for it (as N-16 does for every log line), so these cases run the real script against a fake
`dotnet` and a fake `curl` on a PATH of their own: no build, no port. The fake gateway's first start
serves until it is stopped; its second, the one started with Session__MaxSessions=0, writes its
configuration's pairs, a canary among them, to stdout and stderr, then takes one of the four paths the refusal
case judges: it aborts with a log naming the key, aborts with one that does not, exits 0, or keeps
running. Each case asserts the script's verdict line and exit status, and that neither the canary
nor any string value of contracts/platform-config.example.json, read from that file, reaches the
script's output. The still-running case also asserts the script stopped the fake before it returned.

The real gateway's side, that the process built from this tree refuses, is the smoke test's own run
after a build, which CI makes on every change to tools/smoke.sh; these cases prove the script's.

Run: python3 -I tools/smoke_tests.py
"""

from __future__ import annotations

import importlib.util
import json
import os
import pathlib
import subprocess
import tempfile
import time
import unittest
import uuid

ROOT = pathlib.Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("companion_runner", ROOT / "tools" / "companion_runner.py")
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)
SCRIPT = ROOT / "tools" / "smoke.sh"
EXAMPLE = ROOT / "contracts" / "platform-config.example.json"

# The fake gateway. Its first start (Session__MaxSessions as the example sets it) serves until the
# script stops it. Its second writes its configuration's pairs, the variables under the sections
# SMOKE_FAKE_SECTIONS names, to both streams, then takes the path SMOKE_FAKE_REFUSAL names. Only
# those pairs: the rest of the environment it inherits can hold a runner's or a session's
# credentials, which a failing case would otherwise put in the log. `kill -ABRT $$` ends it as an
# unhandled .NET exception ends the real one, so the script meets the same exit status and the same
# job notice from bash.
FAKE_DOTNET = r"""#!/usr/bin/env bash
if [ "${1:-}" = publish ]; then exit 0; fi
if [ "${Session__MaxSessions:-}" != 0 ]; then exec sleep 300; fi
echo "$$" > "$SMOKE_FAKE_PID"
pairs="$(env | grep -E "^($SMOKE_FAKE_SECTIONS)__")"
echo "$pairs"
echo "$pairs" >&2
case "$SMOKE_FAKE_REFUSAL" in
  refuses)
    echo "fail: Platform.Configuration The platform configuration fails at Session:MaxSessions: minimum."
    kill -ABRT $$
    ;;
  refuses-without-the-key)
    echo "fail: Platform.Configuration The platform configuration fails at a key: minimum."
    kill -ABRT $$
    ;;
  exits-zero)
    exit 0
    ;;
  keeps-running)
    exec sleep 300
    ;;
esac
exit 99
"""

# The fake curl answers the five probes as the serving gateway does with an empty registry.
FAKE_CURL = r"""#!/usr/bin/env bash
write=0
url=""
for arg in "$@"; do
  case "$arg" in
    %\{http_code\}) write=1 ;;
    http://*) url="$arg" ;;
  esac
done
case "$url" in
  */healthz) code=200 ;;
  */readyz) code=503 ;;
  *) code=404 ;;
esac
if [ "$write" = 1 ]; then printf '%s' "$code"; fi
exit 0
"""


def example_values() -> list[str]:
    """Every string value of the example configuration that the script's own text does not hold."""
    values: list[str] = []

    def walk(value):
        if isinstance(value, dict):
            for item in value.values():
                walk(item)
        elif isinstance(value, list):
            for item in value:
                walk(item)
        elif isinstance(value, str):
            values.append(value)

    walk(json.loads(EXAMPLE.read_text()))
    script = SCRIPT.read_text()
    return sorted({value for value in values if len(value) >= 4 and value not in script})


class RefusalCase(unittest.TestCase):
    def run_smoke(self, refusal: str) -> tuple[subprocess.CompletedProcess, str, int]:
        work = pathlib.Path(tempfile.mkdtemp(prefix="smoke-tests-"))
        self.addCleanup(subprocess.run, ["rm", "-rf", str(work)], check=False)
        bin_dir = work / "bin"
        bin_dir.mkdir()
        for name, text in (("dotnet", FAKE_DOTNET), ("curl", FAKE_CURL)):
            path = bin_dir / name
            path.write_text(text)
            path.chmod(0o755)
        canary = f"canary-{uuid.uuid4().hex}"
        pid_file = work / "refused.pid"
        environ = dict(os.environ)
        environ.update({
            "PATH": f"{bin_dir}:{environ.get('PATH', '/usr/bin:/bin')}",
            "SMOKE_FAKE_REFUSAL": refusal,
            "SMOKE_FAKE_PID": str(pid_file),
            "SMOKE_FAKE_SECTIONS": "|".join(json.loads(EXAMPLE.read_text())),
            "Session__Canary": canary,
        })
        result = subprocess.run(
            ["bash", str(SCRIPT), "Debug", "5999"],
            cwd=ROOT, env=environ, capture_output=True, text=True, timeout=120, check=False)
        pid = int(pid_file.read_text().strip()) if pid_file.exists() else 0
        return result, canary, pid

    def assert_no_value(self, result: subprocess.CompletedProcess, canary: str):
        output = result.stdout + result.stderr
        self.assertIn("app1.spa-poc.test /assets/app.js -> 404", output, "the five probes did not run")
        self.assertFalse(canary in output, "the script printed the canary configured as Session:Canary")
        values = example_values()
        self.assertTrue(values, "no value of the example to look for")
        leaked = [value for value in values if value in output]
        self.assertEqual(leaked, [], "the script printed the example's configured values")

    def test_a_refusal_naming_the_key_passes(self):
        result, canary, _ = self.run_smoke("refuses")
        self.assertEqual(result.returncode, 0, result.stdout.splitlines()[-1:])
        self.assertEqual(result.stdout.splitlines()[-1],
                         "  Session__MaxSessions=0 -> exit 134, log names Session:MaxSessions")
        self.assert_no_value(result, canary)

    def test_a_refusal_whose_log_does_not_name_the_key_fails(self):
        result, canary, _ = self.run_smoke("refuses-without-the-key")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(result.stdout.splitlines()[-1],
                         "::error::With Session__MaxSessions=0 the gateway exited with status 134 and its "
                         "log does not name Session:MaxSessions, expected it to refuse to start (ADR-0042)")
        self.assert_no_value(result, canary)

    def test_an_exit_status_of_zero_fails(self):
        result, canary, _ = self.run_smoke("exits-zero")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(result.stdout.splitlines()[-1],
                         "::error::With Session__MaxSessions=0 the gateway exited with status 0, expected "
                         "it to refuse to start (ADR-0042)")
        self.assert_no_value(result, canary)

    def test_a_gateway_still_running_after_the_wait_fails_and_is_stopped(self):
        result, canary, pid = self.run_smoke("keeps-running")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(result.stdout.splitlines()[-1],
                         "::error::With Session__MaxSessions=0 the gateway was still running after the "
                         "30-second start-up wait, expected it to refuse to start (ADR-0042)")
        self.assert_no_value(result, canary)
        self.assertNotEqual(pid, 0, "the fake gateway's second start never ran")
        # The script's EXIT trap sends TERM; give the fake a moment to go.
        for _ in range(50):
            try:
                os.kill(pid, 0)
            except ProcessLookupError:
                break
            time.sleep(0.1)
        else:
            os.kill(pid, 9)
            self.fail("the script returned with the refused gateway still running")


if __name__ == "__main__":
    runner.main()
