#!/usr/bin/env python3
"""PROTOTYPE: the startup benchmark harness (#173, on the map #169). Owner: integrator, until the
request in contracts/changes/issue-172-tools-bench-ownership.md gives tools/bench to workstream 6.

Throwaway code that answers one question: how is one cold start of the gateway timed, against the
startup floor, and what does a run print. The method choices it makes are recorded in #173's
Answer; the accepted ADR the map ends in decides which survive.

    tools/bench/startup.py                     N=10 cold starts each of the floor and the gateway
    tools/bench/startup.py --runs 3 --json out.json --unconstrained
    tools/bench/startup.py --no-build          reuse the spa-bench/*:<commit> images already built

Needs Docker and git on the host and nothing else but this python3 (3.9, as macOS ships it). It
binds no host port: everything it reaches is on a bench network of its own.

**One cold start** is a fresh container from an image already on the machine, under
`--cpus 0.25 --memory 1g` (the map's constraint, the ECS task's size). The floor is
tools/bench/floor, an empty WebApplication on the gateway's image chain. The two kinds alternate,
floor then gateway, so drift on the machine lands on both.

**One clock.** Clock zero is the container's `State.StartedAt`, which dockerd stamps once the
process has been started. Every mark is `time.time_ns()` in a probe container on the same Docker
VM, so zero and the marks are read from the same kernel's CLOCK_REALTIME. A mark taken on the host
would be on the Mac's clock, which Docker Desktop only keeps near the VM's. `docker start` is timed
on the host as well, but only as a duration, printed beside the table and never mixed into it.

**What is detected, and how:**
  listening               the first TCP connect to :8080 that succeeds (a new socket every poll)
  listening (log line)    dockerd's timestamp on the "Now listening on" line Kestrel logs
  /healthz 200            the first 200, a new connection per attempt
  /readyz 200             gateway only: the first 200, which needs a live registry snapshot
  first app-host 401      gateway only: GET / on the registry's host with Sec-Fetch-Mode: cors and
                          no cookie, which UsePlatformShellGate answers 401 (ADR-0008)
Each clock is polled only after the one before it is reached. A mark is the end of the attempt that
succeeded; the attempt before it failed, so the event lies between that attempt's start and the
mark, and the widest such window of the run is printed as the precision.

**The stand-in.** moto in server mode, at a fixed address on the bench network, reached through
AWS_ENDPOINT_URL on an IP literal so the SDK uses path style (docs/research/local-aws-stand-in.md
on branch research/local-aws-stand-in, from #170). It is seeded once, from contracts/ and fixtures/
read in place: the bucket, versioned, the client secret each app's reference names, each app's
release files, and contracts/registry.example.json as the registry object.

**The discovery stand-in.** The 401 is written by the OpenID Connect handler's
OnRedirectToIdentityProvider event (src/Platform.Identity/AuthenticationEvents.cs), and the handler
reads the provider's discovery document before it raises that event
(OpenIdConnectHandler.HandleChallengeAsyncInternal,
https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/OpenIdConnect/src/OpenIdConnectHandler.cs).
With the example's Cognito authority the first app-host fetch is a 500. So each run's network
namespace is held by a small container that serves a static discovery document on 127.0.0.1:9000,
the one plain-HTTP authority SharedOidc admits (src/Platform.Identity/IssuerDirectory.cs), and the
measured container joins that namespace with `--network container:`. That is the shape of a Fargate
task, whose containers share one namespace under awsvpc. Both kinds run that way, so the floor and
the gateway see the same network.

The same file runs inside the containers, mounted read-only at /bench, as the subcommands `probe`,
`seed` and `stand-in`; their interpreter is the moto image's python, so nothing else is pulled.
"""

from __future__ import annotations

import argparse
import calendar
import http.client
import http.server
import ipaddress
import json
import os
import pathlib
import platform
import re
import shutil
import socket
import statistics
import subprocess
import sys
import time
from typing import Dict, List, NamedTuple, Optional

MOTO_IMAGE = "motoserver/moto:5.2.4"   # the research's pin (#170)
DISCOVERY_PORT = 9000
APP_PORT = 8080                        # ASPNETCORE_HTTP_PORTS in both Dockerfiles
ROOT = pathlib.Path(__file__).resolve().parent.parent.parent

# --- timestamps ------------------------------------------------------------------------------------

_DOCKER_TIME = re.compile(r"^(\d{4})-(\d\d)-(\d\d)T(\d\d):(\d\d):(\d\d)(?:\.(\d{1,9}))?Z$")


def docker_time_ns(text: str) -> int:
    """A dockerd UTC timestamp (State.StartedAt, `docker logs --timestamps`) in ns since the epoch.
    Parsed by hand: datetime keeps microseconds, and dockerd prints nanoseconds."""
    match = _DOCKER_TIME.match(text.strip())
    if not match:
        raise ValueError(f"not a dockerd UTC timestamp: {text!r}")
    whole = calendar.timegm(tuple(int(part) for part in match.groups()[:6]))
    fraction = (match.group(7) or "").ljust(9, "0")
    return whole * 1_000_000_000 + int(fraction)


# --- the probe, which runs inside the bench network -----------------------------------------------


class Step(NamedTuple):
    name: str
    path: str
    expect: int
    host: str = "bench.invalid"
    headers: Dict[str, str] = {}


def _connects(address: str, port: int, timeout_s: float) -> bool:
    try:
        with socket.create_connection((address, port), timeout=timeout_s):
            return True
    except OSError:
        return False


def _status(address: str, port: int, step: Step, timeout_s: float) -> Optional[int]:
    # The Host header is sent as written: everything here routes by host (AGENTS.md).
    connection = http.client.HTTPConnection(address, port, timeout=timeout_s)
    try:
        connection.putrequest("GET", step.path, skip_host=True, skip_accept_encoding=True)
        connection.putheader("Host", step.host)
        for name, value in step.headers.items():
            connection.putheader(name, value)
        connection.endheaders()
        response = connection.getresponse()
        response.read()
        return response.status
    except (OSError, http.client.HTTPException):
        return None
    finally:
        connection.close()


def probe(address: str, port: int, steps: List[Step], interval_s: float, deadline_s: float,
          clock=time.time_ns) -> Dict[str, dict]:
    """Polls `listening` and then each step in turn, and returns each one's mark: `at_ns` when it was
    reached (None if the deadline came first), `after_ns` the start of the failed attempt before it,
    the attempts made and the last status seen."""
    end = time.monotonic() + deadline_s
    marks: Dict[str, dict] = {}

    def watch(name, attempt):
        after = clock()
        tries = 0
        last = None
        while time.monotonic() < end:
            begun = clock()
            tries += 1
            ok, last = attempt()
            if ok:
                marks[name] = {"at_ns": clock(), "after_ns": after, "attempts": tries, "status": last}
                return True
            after = begun
            time.sleep(interval_s)
        marks[name] = {"at_ns": None, "after_ns": after, "attempts": tries, "status": last}
        return False

    reached = watch("listening", lambda: (_connects(address, port, 0.05), None))
    for step in steps:
        if not reached:
            marks[step.name] = {"at_ns": None, "after_ns": None, "attempts": 0, "status": None}
            continue

        def attempt(step=step):
            status = _status(address, port, step, 5.0)
            return status == step.expect, status

        reached = watch(step.name, attempt)
    return marks


def steps_for(kind: str, app_host: str) -> List[Step]:
    healthz = Step("healthz", "/healthz", 200)
    if kind == "floor":
        # The floor has no registry and no app: it stops at /healthz (#173).
        return [healthz]
    return [
        healthz,
        Step("readyz", "/readyz", 200),
        # An unauthenticated fetch, as a SPA's script sends one: not a navigation, so a 401 and never
        # a redirect (ADR-0008). Same-origin, so nothing in UsePlatformRequestGate refuses it first.
        Step("app_host", "/", 401, host=app_host,
             headers={"Sec-Fetch-Mode": "cors", "Sec-Fetch-Dest": "empty", "Sec-Fetch-Site": "same-origin"}),
    ]


# --- the summary -----------------------------------------------------------------------------------

CLOCKS = [
    ("listening", "listening"),
    ("listening_log", "listening (log line)"),
    ("healthz", "/healthz 200"),
    ("readyz", "/readyz 200"),
    ("app_host", "first app-host 401"),
]


def clocks(run: dict) -> Dict[str, Optional[float]]:
    """A run's clocks in milliseconds from its StartedAt."""
    started = run["started_ns"]
    return {name: None if mark["at_ns"] is None else (mark["at_ns"] - started) / 1e6
            for name, mark in run["marks"].items()}


def summarise(runs: List[dict]) -> Dict[str, dict]:
    """Median and max per clock over the runs that reached it; `reached` of `runs` says how many."""
    per_run = [clocks(one) for one in runs]
    summary = {}
    for name, _ in CLOCKS:
        values = [one[name] for one in per_run if name in one]
        if not values:
            continue
        reached = [value for value in values if value is not None]
        summary[name] = {
            "median": statistics.median(reached) if reached else None,
            "max": max(reached) if reached else None,
            "reached": len(reached),
            "runs": len(values),
        }
    return summary


def gaps(floor: Dict[str, dict], gateway: Dict[str, dict]) -> Dict[str, dict]:
    """The gateway over the floor, clock by clock. The floor has no registry, so it is ready when
    /healthz answers: a clock it lacks is measured against its /healthz, and `against` says so."""
    result = {}
    for name, entry in gateway.items():
        against = name if name in floor else "healthz"
        base = floor.get(against)
        if base is None or entry["median"] is None or base["median"] is None:
            continue
        result[name] = {"median": entry["median"] - base["median"], "max": entry["max"] - base["max"],
                        "against": against}
    return result


def render_table(floor: Dict[str, dict], gateway: Dict[str, dict]) -> str:
    gap = gaps(floor, gateway)

    def cell(entry, key):
        return "-" if not entry or entry.get(key) is None else f"{entry[key]:.0f}"

    rows = [("clock (ms from StartedAt)", "floor med", "floor max", "gw med", "gw max", "gap med", "gap max")]
    for name, label in CLOCKS:
        if name not in floor and name not in gateway:
            continue
        short = [f"{kind} {entry['reached']}/{entry['runs']}"
                 for kind, entry in (("floor", floor.get(name)), ("gw", gateway.get(name)))
                 if entry and entry["reached"] < entry["runs"]]
        if short:
            label = f"{label} (reached: {', '.join(short)})"
        if name in gap and gap[name]["against"] != name:
            label = f"{label} *"
        rows.append((label, cell(floor.get(name), "median"), cell(floor.get(name), "max"),
                     cell(gateway.get(name), "median"), cell(gateway.get(name), "max"),
                     cell(gap.get(name), "median"), cell(gap.get(name), "max")))
    widths = [max(len(row[i]) for row in rows) for i in range(len(rows[0]))]
    lines = ["  ".join(value.ljust(widths[0]) if i == 0 else value.rjust(widths[i]) for i, value in enumerate(row))
             for row in rows]
    if any(entry["against"] != name for name, entry in gap.items()):
        lines.append("* the gap is over the floor's /healthz: the floor has no registry, so that is when it is ready")
    return "\n".join(lines)


def windows_ms(runs: List[dict]) -> Dict[str, float]:
    """Per polled clock, the widest window over the runs between the start of the failed attempt
    before the mark and the mark. The event lies inside it, so it bounds the precision. A clock
    reached on its first attempt has no failed attempt: its window is that attempt's own round trip,
    and the mark is still when the answer arrived, which is what a health checker sees."""
    widest: Dict[str, float] = {}
    for one in runs:
        for name, mark in one["marks"].items():
            if mark.get("at_ns") is None or mark.get("after_ns") is None:
                continue
            widest[name] = max(widest.get(name, 0.0), (mark["at_ns"] - mark["after_ns"]) / 1e6)
    return widest


# --- the gateway's environment ---------------------------------------------------------------------


def gateway_environment(config: dict, endpoint: str, discovery: str) -> Dict[str, str]:
    """The task environment infra/modules/cell/ecs.tf builds, flattened from a platform
    configuration file the way the configuration binder reads it back (Policy:AllowedUpstreams:0 is
    Policy__AllowedUpstreams__0, every value a string), with every issuer pointed at the discovery
    stand-in and the SDK chain resolved from the environment alone: the endpoint, AWS_REGION (the
    .NET chain reads no other) and a static key pair (docs/research/local-aws-stand-in.md, section
    1)."""
    env: Dict[str, str] = {}

    def flatten(prefix, value):
        if isinstance(value, dict):
            for key, inner in value.items():
                flatten(f"{prefix}__{key}" if prefix else key, inner)
        elif isinstance(value, list):
            for index, inner in enumerate(value):
                flatten(f"{prefix}__{index}", inner)
        else:
            env[prefix] = value if isinstance(value, str) else json.dumps(value)

    flatten("", config)
    for issuer in config.get("Issuers", {}):
        env[f"Issuers__{issuer}__Authority"] = discovery
    env.update({"AWS_ENDPOINT_URL": endpoint, "AWS_REGION": "us-east-1",
                "AWS_ACCESS_KEY_ID": "bench", "AWS_SECRET_ACCESS_KEY": "bench"})
    return env


# --- in-container subcommands ----------------------------------------------------------------------


def serve_discovery() -> None:
    """The namespace holder: a static discovery document and an empty key set on loopback. Enough
    for the handler to build a challenge, which the gateway's event then turns into the 401; no
    login can complete against it, and none is attempted."""
    authority = f"http://127.0.0.1:{DISCOVERY_PORT}"
    documents = {
        "/.well-known/openid-configuration": {
            "issuer": authority,
            "authorization_endpoint": authority + "/authorize",
            "token_endpoint": authority + "/token",
            "jwks_uri": authority + "/jwks",
            "end_session_endpoint": authority + "/logout",
            "response_types_supported": ["code"],
            "subject_types_supported": ["public"],
            "id_token_signing_alg_values_supported": ["RS256"],
        },
        "/jwks": {"keys": []},
    }

    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def do_GET(self):
            document = documents.get(self.path)
            body = json.dumps(document).encode() if document is not None else b""
            self.send_response(200 if document is not None else 404)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    server = http.server.ThreadingHTTPServer(("127.0.0.1", DISCOVERY_PORT), Handler)
    print("ready", flush=True)
    server.serve_forever()


def seed(endpoint: str, repo: pathlib.Path) -> None:
    """Seeds the stand-in from the files in place: docs/research/local-aws-stand-in.md section 5, with
    boto3 from the moto image in place of the AWS CLI and spactl. The gateway checks neither the
    release prefix nor the write condition, so plain puts serve."""
    import boto3  # in the moto image, which is where this runs

    config = json.loads((repo / "contracts" / "platform-config.example.json").read_text())
    registry_path = repo / "contracts" / "registry.example.json"
    registry = json.loads(registry_path.read_text())
    s3 = boto3.client("s3", endpoint_url=endpoint, region_name="us-east-1")
    ssm = boto3.client("ssm", endpoint_url=endpoint, region_name="us-east-1")

    bucket = config["Registry"]["Bucket"]
    s3.create_bucket(Bucket=bucket)
    # contracts/s3-layout.md: the registry object keeps every version.
    s3.put_bucket_versioning(Bucket=bucket, VersioningConfiguration={"Status": "Enabled"})
    for app in registry["apps"]:
        # ssm:<name>:<version>; a first put is version 1, which is what the example references.
        name, version = app["oauth"]["clientSecretRef"][len("ssm:"):].rsplit(":", 1)
        put = ssm.put_parameter(Name=name, Type="SecureString", Value="bench-not-a-secret")
        if str(put["Version"]) != version:
            raise SystemExit(f"seed: {name} is at version {put['Version']}, the registry names {version}")
        release_root = repo / "fixtures" / "spa" / app["appId"]
        for path in sorted(p for p in release_root.rglob("*") if p.is_file()):
            key = f"apps/{app['appId']}/releases/{app['release']}/{path.relative_to(release_root).as_posix()}"
            s3.put_object(Bucket=bucket, Key=key, Body=path.read_bytes())
    s3.put_object(Bucket=bucket, Key=config["Registry"]["Key"], Body=registry_path.read_bytes())
    print(f"seed: {bucket}, {len(registry['apps'])} app(s), registry revision {registry['revision']}", flush=True)


# --- the host side ---------------------------------------------------------------------------------


class Docker:
    def __init__(self):
        found = shutil.which("docker")
        desktop = pathlib.Path.home() / ".docker" / "bin" / "docker"
        if not found and desktop.exists():
            found = str(desktop)
        if not found:
            raise SystemExit("startup.py: docker is not on PATH (Docker Desktop puts it in ~/.docker/bin).")
        self.binary = found

    def run(self, *args, input: Optional[bytes] = None) -> subprocess.CompletedProcess:
        return subprocess.run([self.binary, *args], capture_output=True, input=input, check=False)

    def __call__(self, *args, input: Optional[bytes] = None) -> str:
        done = self.run(*args, input=input)
        if done.returncode != 0:
            raise RuntimeError(f"docker {' '.join(args[:3])} failed: {done.stderr.decode().strip()[-600:]}")
        return done.stdout.decode().strip()

    def ok(self, *args) -> bool:
        return self.run(*args).returncode == 0

    def popen(self, *args) -> subprocess.Popen:
        return subprocess.Popen([self.binary, *args], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)


def git(*args) -> str:
    return subprocess.run(["git", "-C", str(ROOT), *args], capture_output=True, text=True, check=True).stdout.strip()


class Bench:
    def __init__(self, options):
        self.options = options
        self.docker = Docker()
        self.prefix = f"spa-bench-{os.getpid()}"
        subnet = ipaddress.ip_network(options.subnet)
        hosts = list(subnet.hosts())
        self.subnet = str(subnet)
        self.moto_ip = str(hosts[9])
        self.target_ip = str(hosts[19])
        self.created: List[str] = []
        self.network: Optional[str] = None
        registry = json.loads((ROOT / "contracts" / "registry.example.json").read_text())
        self.app_host = registry["apps"][0]["host"]
        self.environment = gateway_environment(
            json.loads((ROOT / "contracts" / "platform-config.example.json").read_text()),
            endpoint=f"http://{self.moto_ip}:5000", discovery=f"http://127.0.0.1:{DISCOVERY_PORT}")

    def images(self) -> Dict[str, str]:
        """Builds both images from the commit, not the working tree, so the commit measured is exact
        and no host bin/ or obj/ reaches the context: src/Gateway/Dockerfile copies src/ whole, and a
        host obj/project.assets.json without the linux target fails its publish with NETSDK1047."""
        commit = git("rev-parse", "--short=12", "HEAD")
        tags = {"floor": f"spa-bench/floor:{commit}", "gateway": f"spa-bench/gateway:{commit}"}
        dockerfiles = {"floor": "tools/bench/floor/Dockerfile", "gateway": "src/Gateway/Dockerfile"}
        for kind, tag in tags.items():
            if self.options.no_build:
                self.docker("image", "inspect", tag)
                continue
            print(f"bench: building {tag} from {dockerfiles[kind]} at {commit}", flush=True)
            archive = subprocess.run(["git", "-C", str(ROOT), "archive", "--format=tar", "HEAD"],
                                     capture_output=True, check=True).stdout
            self.docker("build", "-q", "-f", dockerfiles[kind], "-t", tag, "-", input=archive)
        if not self.docker.ok("image", "inspect", MOTO_IMAGE):
            self.docker("pull", MOTO_IMAGE)
        return tags

    def mounts(self, repo: bool = False) -> List[str]:
        mounts = ["-v", f"{ROOT / 'tools' / 'bench'}:/bench:ro"]
        if repo:
            mounts += ["-v", f"{ROOT / 'contracts'}:/repo/contracts:ro", "-v", f"{ROOT / 'fixtures'}:/repo/fixtures:ro"]
        return mounts

    def up(self) -> None:
        self.docker("network", "create", "--subnet", self.subnet, self.prefix)
        self.network = self.prefix
        self.track(self.docker("run", "-d", "--name", f"{self.prefix}-moto", "--network", self.network,
                               "--ip", self.moto_ip, MOTO_IMAGE))
        # The seed is also moto's warm-up, so moto's own first requests are not inside a gateway
        # clock (research section 6, item 9). It retries while moto starts.
        last = b""
        for _ in range(30):
            done = self.docker.run("run", "--rm", "--network", self.network,
                                   "-e", "AWS_ACCESS_KEY_ID=bench", "-e", "AWS_SECRET_ACCESS_KEY=bench",
                                   *self.mounts(repo=True), "--entrypoint", "python3", MOTO_IMAGE,
                                   "/bench/startup.py", "seed", "--endpoint", f"http://{self.moto_ip}:5000")
            if done.returncode == 0:
                print(done.stdout.decode().strip(), flush=True)
                return
            last = done.stderr
            time.sleep(1)
        raise RuntimeError(f"seeding the stand-in failed: {last.decode().strip()[-600:]}")

    def track(self, container: str) -> str:
        self.created.append(container)
        return container

    def remove(self, *containers: str) -> None:
        for container in containers:
            self.docker.run("rm", "-f", container)
            if container in self.created:
                self.created.remove(container)

    def down(self) -> None:
        self.remove(*list(self.created))
        if self.network:
            self.docker.run("network", "rm", self.network)

    def wait_for_line(self, container: str, text: str, timeout_s: float = 30) -> None:
        end = time.monotonic() + timeout_s
        while time.monotonic() < end:
            if text in self.docker.run("logs", container).stdout.decode():
                return
            time.sleep(0.05)
        raise RuntimeError(f"{container} never printed {text!r}")

    def log_line_ns(self, container: str, text: str) -> Optional[int]:
        done = self.docker.run("logs", "--timestamps", container)
        for line in (done.stdout + done.stderr).decode().splitlines():
            if text in line:
                return docker_time_ns(line.split(" ", 1)[0])
        return None

    def one(self, kind: str, image: str, index: int, constrained: bool) -> dict:
        name = f"{self.prefix}-{kind}-{'c' if constrained else 'u'}{index}"
        holder = self.track(self.docker(
            "run", "-d", "--name", f"{name}-netns", "--network", self.network, "--ip", self.target_ip,
            *self.mounts(), "--entrypoint", "python3", MOTO_IMAGE, "-u", "/bench/startup.py", "stand-in"))
        target = None
        prober = None
        try:
            self.wait_for_line(holder, "ready")
            limits = ["--cpus", self.options.cpus, "--memory", self.options.memory] if constrained else []
            env = [arg for key, value in (self.environment.items() if kind == "gateway" else [])
                   for arg in ("-e", f"{key}={value}")]
            target = self.track(self.docker("create", "--name", name, "--network", f"container:{holder}",
                                            *limits, *env, image))
            prober = self.docker.popen(
                "run", "--rm", "--name", f"{name}-probe", "--network", self.network, *self.mounts(),
                "--entrypoint", "python3", MOTO_IMAGE, "-u", "/bench/startup.py", "probe",
                "--address", self.target_ip, "--kind", kind, "--app-host", self.app_host,
                "--interval-ms", str(self.options.interval_ms), "--deadline-s", str(self.options.timeout))
            first = prober.stdout.readline().strip()
            if first != "ready":
                raise RuntimeError(f"the probe did not start: {first!r} {prober.stderr.read()[-600:]}")

            begun = time.monotonic()
            self.docker("start", target)
            start_cli_ms = (time.monotonic() - begun) * 1e3

            out, err = prober.communicate(timeout=self.options.timeout + 30)
            if prober.returncode != 0:
                raise RuntimeError(f"the probe failed: {err.strip()[-600:]}")
            marks = json.loads(out)
            started_ns = docker_time_ns(self.docker("inspect", "-f", "{{.State.StartedAt}}", target))
            marks["listening_log"] = {"at_ns": self.log_line_ns(target, "Now listening on"),
                                      "after_ns": None, "attempts": 0, "status": None}
            return {"kind": kind, "constrained": constrained, "index": index, "started_ns": started_ns,
                    "start_cli_ms": start_cli_ms, "marks": marks}
        finally:
            if prober and prober.poll() is None:
                prober.kill()
            self.remove(*[c for c in (target, holder) if c])

    def facts(self, tags: Dict[str, str]) -> dict:
        info = json.loads(self.docker("info", "--format", "{{json .}}"))
        return {
            "commit": git("rev-parse", "HEAD"),
            "uncommitted_changes_not_measured": bool(git("status", "--porcelain")),
            "images": {kind: {"tag": tag, "id": self.docker("image", "inspect", "-f", "{{.Id}}", tag)}
                       for kind, tag in {**tags, "stand-in": MOTO_IMAGE}.items()},
            "docker": {"server": info.get("ServerVersion"), "os": info.get("OperatingSystem"),
                       "arch": info.get("Architecture"), "cpus": info.get("NCPU"), "memory": info.get("MemTotal")},
            "host": {"machine": platform.machine(), "system": platform.platform(), "python": platform.python_version()},
            "runs": self.options.runs, "cpus": self.options.cpus, "memory": self.options.memory,
            "interval_ms": self.options.interval_ms,
        }


def report(facts: dict, runs: List[dict]) -> str:
    lines = [
        "Startup benchmark (PROTOTYPE, #173)",
        f"commit {facts['commit']}" + (" (the working tree has changes that were not measured)"
                                        if facts["uncommitted_changes_not_measured"] else ""),
        "images: " + ", ".join(f"{kind} {image['id'][7:19]}" for kind, image in facts["images"].items()),
        f"docker {facts['docker']['server']} on {facts['docker']['os']}, {facts['docker']['arch']}, "
        f"{facts['docker']['cpus']} CPUs, {facts['docker']['memory'] / 2**30:.1f} GiB; host {facts['host']['machine']}",
        "clock: zero is the container's State.StartedAt (dockerd); marks are time.time_ns() in a probe container "
        "on the same Docker VM, one CLOCK_REALTIME; the log-line clock is dockerd's own timestamp on that line",
        f"polling: a new connection every {facts['interval_ms']:g} ms after a failed attempt, each clock only "
        "after the one before it",
    ]
    for constrained in (True, False):
        chosen = [one for one in runs if one["constrained"] == constrained]
        if not chosen:
            continue
        floor = [one for one in chosen if one["kind"] == "floor"]
        gateway = [one for one in chosen if one["kind"] == "gateway"]
        widths = windows_ms(chosen)
        cli = statistics.median(one["start_cli_ms"] for one in chosen)
        heading = (f"--cpus {facts['cpus']} --memory {facts['memory']}" if constrained
                   else "unconstrained (reported, not the floor)")
        lines += ["", f"{heading}: {len(floor)} floor and {len(gateway)} gateway cold starts, alternating",
                  render_table(summarise(floor), summarise(gateway)),
                  "precision, the widest window between the attempt before a mark and the mark (a clock "
                  "reached on its first attempt has only that request's round trip): "
                  + ", ".join(f"{label} {widths[name]:.0f} ms" for name, label in CLOCKS if name in widths),
                  f"`docker start` took {cli:.0f} ms median, timed on the host's clock and not part of the table"]
    return "\n".join(lines)


def run_bench(options) -> int:
    bench = Bench(options)
    runs: List[dict] = []
    try:
        tags = bench.images()
        bench.up()
        for constrained in [True] + ([False] if options.unconstrained else []):
            for index in range(options.runs):
                for kind in ("floor", "gateway"):
                    one = bench.one(kind, tags[kind], index, constrained)
                    runs.append(one)
                    shown = {name: "unreached" if ms is None else round(ms) for name, ms in clocks(one).items()}
                    print(f"bench: {kind} {'constrained' if constrained else 'unconstrained'} #{index + 1}: {shown}",
                          flush=True)
        facts = bench.facts(tags)
    finally:
        bench.down()
    print()
    print(report(facts, runs))
    if options.json:
        pathlib.Path(options.json).write_text(json.dumps({"facts": facts, "runs": runs}, indent=2) + "\n")
    return 0


def main(argv: List[str]) -> int:
    parser = argparse.ArgumentParser(description="PROTOTYPE startup benchmark (#173): the floor and the gateway.")
    parser.add_argument("--runs", type=int, default=10)
    parser.add_argument("--cpus", default="0.25")
    parser.add_argument("--memory", default="1g")
    parser.add_argument("--unconstrained", action="store_true",
                        help="also run each kind without limits; printed apart, and never the floor")
    parser.add_argument("--interval-ms", type=float, default=10)
    parser.add_argument("--timeout", type=float, default=120, help="seconds one run may take to reach every clock")
    parser.add_argument("--subnet", default="10.90.0.0/24")
    parser.add_argument("--no-build", action="store_true")
    parser.add_argument("--json", help="also write every run's marks and the facts to this file")
    sub = parser.add_subparsers(dest="command")

    probe_cmd = sub.add_parser("probe", help="(inside the bench network) poll one container's clocks")
    probe_cmd.add_argument("--address", required=True)
    probe_cmd.add_argument("--port", type=int, default=APP_PORT)
    probe_cmd.add_argument("--kind", choices=("floor", "gateway"), required=True)
    probe_cmd.add_argument("--app-host", required=True)
    probe_cmd.add_argument("--interval-ms", type=float, default=10)
    probe_cmd.add_argument("--deadline-s", type=float, default=120)

    seed_cmd = sub.add_parser("seed", help="(inside the bench network) seed the stand-in")
    seed_cmd.add_argument("--endpoint", required=True)
    seed_cmd.add_argument("--repo", default="/repo")

    sub.add_parser("stand-in", help="(inside the bench network) hold the namespace and serve discovery")

    options = parser.parse_args(argv)
    if options.command == "probe":
        # The harness starts the container on this line, so the probe is polling before it starts.
        print("ready", flush=True)
        marks = probe(options.address, options.port, steps_for(options.kind, options.app_host),
                      interval_s=options.interval_ms / 1000, deadline_s=options.deadline_s)
        print(json.dumps(marks), flush=True)
        return 0
    if options.command == "seed":
        seed(options.endpoint, pathlib.Path(options.repo))
        return 0
    if options.command == "stand-in":
        serve_discovery()
        return 0
    return run_bench(options)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
