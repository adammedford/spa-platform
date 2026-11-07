#!/usr/bin/env python3
"""The startup benchmark's Fargate half (#305, for #178's run). Owner: the owner OWNERSHIP.md names
for tools/bench/.

    tools/bench/fargate.py --txt out.txt --json out.json

Ten service restarts of the deployed cell, each timed from the new task's own clocks, the control
plane's and this workstation's. tools/bench/startup.py drives Docker; this reads ECS, the load
balancer and CloudWatch Logs instead. Its method is #178's confirmed answers (#286 Q134 to Q148) and
#305's (Q1 to Q14, the grilling record on #305); each step below names the one it follows.

Run it as the operator, with the spa-platform profile and tools/aws-project.env sourced, after
seed.sh or deploy.sh has written infra/envs/poc/outputs.json and certs.sh has issued the cell's
authority. It needs the AWS CLI v2, the Session Manager plugin and this python3 (3.9, as macOS ships
it, D-3 in docs/dependencies.md), and binds one workstation port, --local-port.

**Names.** Every name is read from infra/envs/poc/outputs.json (contracts/infra-outputs.schema.json:
"The scripts and the deployed tests read it"): ecsCluster, ecsService, logGroup, albDnsName,
gatewayImage. Nothing is looked up by name and nothing is read from Terraform state, as the cell
scripts do (#198). The document has no target group, so it is read from the service's
loadBalancers in describe-services, with the container the load balancer sends to; that
container's log stream prefix is read from its task definition (grilling record, facts 5 and 7).

**A restart** (#286 Q135; Q9, Q12) is `update-service --force-new-deployment` of the running
revision, and nothing else changes. Before restart 1 the service must be stable on gatewayImage:
the ECS services-stable waiter, then one deployment, rolloutState COMPLETED, and the running task's
gateway image digest the document's. The new task is the one whose startedBy is the PRIMARY
deployment's id from update-service's answer, polled with describe-tasks while the old deployment's
task still runs beside it. A restart ends stable again, the old task's drain and stop included:
the target group's deregistration delay, 30 s (infra/modules/cell/alb.tf, #347), then SIGTERM and
up to the container's stopTimeout, 30 s (ecs.tf).

**Clocks.** Three, never mixed without a label:
  task           the gateway's own log lines' `ts` and the metadata endpoint's StartedAt, both
                 stamped in the task's microVM (#286 Q136, Q6)
  control plane  describe-tasks' createdAt, pullStartedAt, pullStoppedAt and startedAt
  workstation    the describe-target-health poll at about 1 s, which gives T0 (the first sample
                 whose reason is Elb.InitialHealthChecking) and target healthy, each as the window
                 between the sample before and the sample that saw it (Q13)
Clock zero is the gateway container's StartedAt from the task metadata endpoint v4, read once the
service is stable by one ECS Exec session running a bash one-liner (Q1, Q11): the image has bash with
/dev/tcp and no curl (the grilling record's fact 1). Any failure falls back to pullStoppedAt, and
the output says so and why. S is the task's startedAt, so S - T0 crosses clocks and is printed as
a range.

**Log clocks** (#286 Q137; Q4, Q5) come from the new task's own stream: listening (log) is
Kestrel's "Now listening on", registry live (log) the "Registry revision ... is live" line, and
first passing check (log) the first request line with route health and status 200. The request
line carries no path (contracts/telemetry.md), so that last one rests on a premise the output
prints: nothing on the cell calls /healthz during the run. "ready" labels no deployed row.

**The hop and the 302** (#286 Q139; Q7, Q10). Before each restart the script opens its own
AWS-StartPortForwardingSessionToRemoteHost session to albDnsName:443 through the task about to be
replaced, which outlives the new target's first checks: that task is deregistered once the new
target is healthy and stops after the 30 s drain and up to the 30 s stopTimeout, so the forward
lasts about 30 to 60 s past target healthy: 30 s when the gateway exits soon after SIGTERM, 60 s
only when it takes the whole stopTimeout. The default probe loop (--probe-attempts 30 x
--probe-interval 2.0 s) sleeps 60 s, the upper end only, and each round's probe and log read add
to it, so a 302 that needs more than about 30 s of probing can find the forward gone and end the
restart incomplete. #347 leaves the forward's continuity through a roll unproved.
tools/scripts/tunnel.sh is not used, since its hop can be the task being measured. Once the new
target is healthy it sends a
cookieless navigation to app1's registry host through that forward, TLS verified against the
authority certs.sh issued, until a 302 lands in the new task's stream. It reads the status line and
nothing else: the 302 is the OpenID Connect challenge, whose Location holds state and whose
Set-Cookie holds the correlation cookie. The clock is that log line's duration_ms.

**Endings** (#286 Q148; Q8):
  0  ten clean restarts
  2  a usage error, a refusal, a failed AWS call or any other failure before restart 1's
     update-service is sent (no Region, no outputs document for it, no credentials, a missing tool,
     the service not stable on gatewayImage, a malformed image digest, caller account or hop task
     id); nothing is written
  3  a breach: the scheduler stopped the new task for failing the load balancer's health checks
     (#286 Q148), the first such stop ending the run wherever it falls. ecs.tf's reading of the
     target group's defaults (#171) puts that stop past the T0 + 60 s hard bound, three failures 30 s
     apart; the script does not take that on trust but places the stop it saw against the bound on
     T0's clock, the workstation's: from the describe-tasks before the one that saw it to that
     one's answer. The report says past, before or across the bound, or that no T0 was seen. No
     further restart and nothing done to the service (the circuit breaker rolls back on its own);
     the files hold the restarts so far and the breach row
  4  incomplete: a deployment not stable within the waiter's bound, an expected log line absent
     after a bounded wait, the probe never reaching the new task, another stop, a malformed task
     id, deployment id, control-plane instant or 302 duration, or any other failure once restart
     1's update-service was sent, a failure of that call itself included, since the service may
     have changed; the files hold the restarts so far, marked incomplete with the reason

**Secrecy** (Q7, amended by #976's confirmed answers). The .txt, .json, stdout and stderr use the
same field-aware publication policy. Arbitrary text has recognized ARNs and ECR repository URIs
replaced with placeholders, and every contiguous sequence of twelve or more ASCII digits replaced in
full, including sequences adjacent to letters. Repository URIs are recognized before replacing the
known caller account. Only validated values from the designated Git commit, deployed image digest,
ECS task-id and ECS deployment-id sources retain their identifier digits; the known caller account
is still masked within those strings. Validated numerical measurements retain their numerical
values, even if their digits incidentally contain an account-number sequence. These explicit
exceptions mean the output does not promise the absence of every possible account-number substring.
An identifier-shaped token in arbitrary text is not an exception. Required malformed identity or
measurement evidence refuses the run before its first restart or makes it incomplete afterward;
malformed optional diagnostic values are omitted with a fixed marker. Redaction operates on
structured values before rendering or serialization, and detects literal digit sequences rather
than separated, spelled-out or encoded numbers.
"""

from __future__ import annotations

import argparse
import calendar
import datetime
import json
import math
import os
import pathlib
import re
import shutil
import socket
import ssl
import statistics
import subprocess
import sys
import time
from typing import Dict, List, Optional, Tuple

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent
OUTPUTS_FILE = ROOT / "infra" / "envs" / "poc" / "outputs.json"
PROGRAM = "fargate"

RESTARTS = 10                 # #286 Q135
CHECK_INTERVAL_S = 30         # the target group sets none, so the provider's default (ecs.tf's comment)
ALB_NODES = 2                 # one checker stream per node, one node per subnet (grilling record, Q13)
HARD_BOUND_S = 60             # T0 + 60 s, the third check (#175 Q4, ecs.tf's comment)

EXIT_CLEAN, EXIT_USAGE, EXIT_BREACH, EXIT_INCOMPLETE = 0, 2, 3, 4


class Stop(Exception):
    """The message keeps its type here, since str() of an exception would lose Published (#976)."""

    def __init__(self, message: str):
        super().__init__(message)
        self.message = message


class Refused(Stop):
    """Before restart 1: exit 2 and write nothing."""


class Incomplete(Stop):
    """After restart 1 began: the files are written, marked incomplete, and exit 4."""


class Breach(Exception):
    def __init__(self, row: dict):
        super().__init__("breach")
        self.row = row


class AwsError(Exception):
    def __init__(self, code: str):
        super().__init__(code)
        self.code = code


# --- secrecy ---------------------------------------------------------------------------------------

# The division the Secrecy paragraph states (#976 Q219-Q226). redact() is for arbitrary text; it never
# looks at what a token resembles. Everything written goes through publish_document(), by field path,
# or for a line of stdout or stderr through compose(), before it is rendered, so no pass over the
# rendered text exists to undo a kept identifier or corrupt a number.

MALFORMED = "<malformed>"     # the fixed marker: a rejected value is never echoed (#976 contract 5)

_ARN = re.compile(r"arn:aws[a-zA-Z-]*:[^\s\"',)]*")
_REPOSITORY = re.compile(r"(?<![0-9])[0-9]{12}\.dkr\.ecr(?:-fips)?\.[a-z0-9-]+\.amazonaws\.com(?:\.cn)?/[^\s\"',)@:]*")
_DIGIT_RUN = re.compile(r"[0-9]{12,}")

# The designated sources' grammars, the only values that keep their digits: `git rev-parse HEAD` in
# a SHA-1 repository, the digest gatewayImage pins, an ECS task ARN's last segment and the PRIMARY
# deployment's id in update-service's answer (the shapes of #976's grilling record, fact 5).
COMMIT = re.compile(r"[0-9a-f]{40}")
IMAGE_DIGEST = re.compile(r"sha256:[0-9a-f]{64}")
TASK_ID = re.compile(r"[0-9a-f]{32}")
DEPLOYMENT_ID = re.compile(r"ecs-svc/[0-9]{19}")
# An AWS account id is twelve digits, which is what lets redact()'s digit rule cover the caller's.
# https://docs.aws.amazon.com/accounts/latest/reference/manage-acct-identifiers.html
CALLER_ACCOUNT = re.compile(r"[0-9]{12}")

LATEST_EPOCH_S = 253402300799     # 9999-12-31T23:59:59Z, the last instant the ISO form can name


class Published(str):
    """Text this policy has already passed. A subclass, so that an f-string or a str() loses the
    type, and what lost it is arbitrary text again: the failure masks an id and leaks nothing."""


def redact(text: str) -> str:
    """Arbitrary text only: every ECR repository URI becomes <repository> (an image keeps what
    follows it), every ARN <arn>, and every contiguous run of twelve or more ASCII digits
    <account id>, whole, whatever touches it. The repository goes first, while its host still starts
    with the account. The caller's own id is one such run (preflight refuses any other shape), so
    this replaces it too. It does not read a token's shape and keeps nothing for resembling an
    identifier: a designated value keeps its digits through publish_identifier() instead, and a
    measurement stays a number by never being text."""
    return _DIGIT_RUN.sub("<account id>", _ARN.sub("<arn>", _REPOSITORY.sub("<repository>", text)))


def publish_text(value) -> Optional[str]:
    """Arbitrary text: a task-definition name, a hostname, a log message, a stop reason. What is
    Published stays; what is not a string is no text this script reads, so it is the marker."""
    if value is None or isinstance(value, Published):
        return value
    return redact(value) if isinstance(value, str) else MALFORMED


def publish_identifier(value, grammar: "re.Pattern[str]", account: Optional[str]) -> Published:
    """A value from a designated source: kept only if it fits that source's grammar, and then with
    the known caller's account masked inside it, the exact correlation being lost in that case
    (#976 contract 2). The operational value the AWS calls use is not touched."""
    if not isinstance(value, str) or not grammar.fullmatch(value):
        return Published(MALFORMED)
    return Published(value.replace(account, "<account id>") if account else value)


def compose(template: str, **fields) -> Published:
    """One line of stdout or stderr, or a reason, from a literal template: a Published field (an
    identifier) and a number stand, and every other field is arbitrary text."""
    return Published(template.format(**{name: value if isinstance(value, (Published, int, float)) else publish_text(value)
                                        for name, value in fields.items()}))


def is_measurement(value) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) or isinstance(value, float) and math.isfinite(value)


def valid_status(value):
    """An HTTP status, as the log's `status` and the probe's status line give it."""
    return value if isinstance(value, int) and not isinstance(value, bool) and 100 <= value <= 599 else MALFORMED


def valid_duration(value):
    """A stopwatch reading, so never negative, unlike the differences of clocks ms() derives. The
    upper bound is a thousand years in ms: float() below it cannot overflow."""
    return value if is_measurement(value) and 0 <= value <= LATEST_EPOCH_S * 1000 else MALFORMED


def valid_event_ms(value):
    """CloudWatch's event timestamp, whole ms since the epoch.
    https://docs.aws.amazon.com/AmazonCloudWatchLogs/latest/APIReference/API_OutputLogEvent.html"""
    return value if isinstance(value, int) and not isinstance(value, bool) and 0 <= value <= LATEST_EPOCH_S * 1000 else MALFORMED


def _text(value, _account):
    return publish_text(value)


def _measured(value, _account):
    return value if value is None or is_measurement(value) else MALFORMED


def _optional(valid):
    return lambda value, _account: None if value is None else valid(value)


def _flag(value, _account):
    return value if isinstance(value, bool) or value is None else MALFORMED


def _identifier(grammar):
    return lambda value, account: None if value is None else publish_identifier(value, grammar, account)


def _commit(value, account):
    # "unknown" is the fallback commit() takes when git gives nothing, an absence and no value.
    return value if value == "unknown" else publish_identifier(value, COMMIT, account)


def _instant(value, _account):
    """An ISO instant the script read, kept as given. No form of it holds twelve digits in a row."""
    if value is None:
        return None
    try:
        time_ns(value)
    except ValueError:
        return MALFORMED
    return value if isinstance(value, str) else MALFORMED


def _unlisted(value, _account):
    """A field no table names gains nothing from its name or its type (#976 boundaries)."""
    if value is None:
        return None
    return redact(value if isinstance(value, str) else json.dumps(value, default=str))


def _fields(table: dict):
    def publish(value, account):
        if value is None:
            return None
        if not isinstance(value, dict):
            return MALFORMED
        return {key: table.get(key, _unlisted)(item, account) for key, item in value.items()}
    return publish


def _each(kind):
    def publish(value, account):
        if value is None:
            return None
        return [kind(item, account) for item in value] if isinstance(value, list) else MALFORMED
    return publish


# The field paths. Every field of the document is here, by what writes it: the facts from preflight,
# a restart from Bench.restart(), a line from read_lines(). A number nothing validated is the marker.
_FACTS = _fields({"commit": _commit, "image_digest": _identifier(IMAGE_DIGEST), "task_definition": _text,
                  "platform_version": _text, "region": _text, "restarts": _measured, "poll_seconds": _measured,
                  "check_interval_s": _measured, "app_host": _text})
_WINDOW = _fields({"after_ns": _measured, "at_ns": _measured})
_SAMPLE = _fields({"at_ns": _measured, "state": _text, "reason": _text})
_PROBE = _fields({"at_ns": _measured, "status": _optional(valid_status)})
_LINE = _fields({"ts": _instant, "ts_ns": _measured, "event_timestamp_ms": _optional(valid_event_ms), "route": _text,
                 "status": _optional(valid_status), "duration_ms": _optional(valid_duration), "msg": _text})
_MARKS = _fields({"listening": _LINE, "registry_live": _LINE, "first_passing_check": _LINE, "redirect": _LINE,
                  "health_lines": _measured})
_DRIFT = _fields({"ClockErrorBound": _optional(valid_duration), "ReferenceTimestamp": _instant,
                  "ClockSynchronizationStatus": _text})
_ZERO = _fields({"source": _text, "ns": _measured, "clock_drift": _DRIFT, "fallback": _text})
_STOP = _fields({"code": _text, "reason": _text, "seen": _WINDOW, "after_t0": _text})
_RESTART = _fields({"index": _measured, "complete": _flag, "hop_task": _identifier(TASK_ID), "task_id": _identifier(TASK_ID),
                    "deployment": _identifier(DEPLOYMENT_ID), "task_arn": _text, "created_ns": _measured,
                    "pull_started_ns": _measured, "pull_stopped_ns": _measured, "started_ns": _measured,
                    "platform_version": _text, "samples": _each(_SAMPLE), "t0": _WINDOW, "healthy": _WINDOW, "k": _measured,
                    "probes": _each(_PROBE), "zero": _ZERO, "stop": _STOP, "lines": _each(_LINE), "marks": _MARKS})
_DOCUMENT = _fields({"facts": _FACTS, "complete": _flag, "ending": _text, "reason": _text, "premise": _text,
                     "restarts": _each(_RESTART)})


def publish_document(document: dict, account: Optional[str]) -> dict:
    """The sanitized copy the .json is serialized from and the .txt rendered from; the records
    themselves stay as the AWS calls and the calculations need them."""
    return _DOCUMENT(document, account)


# --- timestamps ------------------------------------------------------------------------------------

_TIME = re.compile(r"(\d{4})-(\d\d)-(\d\d)T(\d\d):(\d\d):(\d\d)(?:\.(\d{1,9}))?(Z|[+-]\d\d:\d\d)", re.ASCII)


def time_ns(value) -> int:
    """A UTC instant in ns since the epoch, from the three forms read here: the AWS CLI's ISO 8601
    (microseconds and an offset), the metadata endpoint's (nanoseconds and Z) and the log line's
    `ts` (.NET's "O": seven digits and an offset). A number is epoch seconds, the CLI's other
    timestamp format. Parsed by hand, because datetime keeps only microseconds. Anything else is a
    ValueError, before a calculation uses it: a bool, an infinity, a number no instant reaches, a
    string with anything around the form, a date or a time that does not exist (#976 contract 3)."""
    if isinstance(value, (int, float)):
        if not is_measurement(value) or abs(value) > LATEST_EPOCH_S:
            raise ValueError("not an epoch instant")
        return int(round(value * 1e9))
    match = _TIME.fullmatch(value) if isinstance(value, str) else None
    if not match:
        raise ValueError("not an ISO 8601 instant")
    year, month, day, hour, minute, second = (int(part) for part in match.groups()[:6])
    datetime.date(year, month, day)
    if hour > 23 or minute > 59 or second > 60:      # 60: a leap second, which ISO 8601 can name
        raise ValueError("not an ISO 8601 instant")
    whole = calendar.timegm((year, month, day, hour, minute, second))
    fraction = int((match.group(7) or "").ljust(9, "0"))
    zone = match.group(8)
    offset = 0
    if zone != "Z":
        if int(zone[1:3]) > 23 or int(zone[4:6]) > 59:
            raise ValueError("not an ISO 8601 instant")
        sign = 1 if zone[0] == "+" else -1
        offset = sign * (int(zone[1:3]) * 3600 + int(zone[4:6]) * 60)
    return (whole - offset) * 1_000_000_000 + fraction


def ms(later: Optional[int], earlier: Optional[int]) -> Optional[float]:
    return None if later is None or earlier is None else (later - earlier) / 1e6


# --- the zero: the metadata endpoint through ECS Exec -----------------------------------------------

BEGIN, END, NO_ENDPOINT = "SPA-BENCH-BEGIN-", "SPA-BENCH-END-", "SPA-BENCH-NO-ENDPOINT"

# One bash one-liner (Q11). The endpoint from the session's environment, else from the gateway
# process's (/proc/1/environ, NUL-separated), since an exec session need not inherit the task's
# environment. Each body is fetched over bash's /dev/tcp, the image having no curl (the grilling
# record's fact 1), with HTTP/1.0 so the server closes and `cat` ends. No single quote inside: the
# whole is passed as bash -c '...'.
# https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-metadata-endpoint-v4-fargate.html
ONE_LINER = (
    'U="${ECS_CONTAINER_METADATA_URI_V4:-}"; '
    'if [ -z "$U" ] && [ -r /proc/1/environ ]; then '
    'while IFS= read -r -d "" kv; do case "$kv" in ECS_CONTAINER_METADATA_URI_V4=*) U="${kv#*=}";; esac; '
    'done < /proc/1/environ; fi; '
    'if [ -z "$U" ]; then echo ' + NO_ENDPOINT + '; exit 0; fi; '
    'get() { h="${1#http://}"; a="${h%%/*}"; p="/${h#*/}"; n="${a%%:*}"; o="${a##*:}"; '
    '[ "$o" = "$a" ] && o=80; exec 3<>"/dev/tcp/$n/$o" || return 1; '
    'printf "GET %s HTTP/1.0\\r\\nHost: %s\\r\\n\\r\\n" "$p" "$a" >&3; cat <&3; exec 3<&-; }; '
    'echo ' + BEGIN + 'CONTAINER; get "$U"; echo; echo ' + END + 'CONTAINER; '
    'echo ' + BEGIN + 'TASK; get "$U/task"; echo; echo ' + END + 'TASK'
)
EXEC_COMMAND = "bash -c '" + ONE_LINER + "'"


def _between(lines: List[str], name: str) -> Optional[str]:
    try:
        first = lines.index(BEGIN + name)
        last = lines.index(END + name, first + 1)
    except ValueError:
        return None
    return "\n".join(lines[first + 1:last])


def _body(response: str) -> dict:
    head, separator, body = response.partition("\n\n")
    status = head.split("\n", 1)[0].split()
    if not separator or len(status) < 2 or status[1] != "200":
        raise ValueError("not a 200")
    document = json.loads(body.strip())
    if not isinstance(document, dict):
        raise ValueError("not an object")
    return document


def parse_metadata(output: str) -> Tuple[Optional[dict], str]:
    """What the session printed, read only between the markers: ({started_ns, clock_drift}, "") or
    (None, the reason for the fallback). The banner the plugin prints around it is ignored."""
    lines = [line.rstrip("\r") for line in output.replace("\r\n", "\n").split("\n")]
    if NO_ENDPOINT in lines:
        return None, "no endpoint variable: ECS_CONTAINER_METADATA_URI_V4 is in neither the session's environment nor the gateway process's"
    container, task = _between(lines, "CONTAINER"), _between(lines, "TASK")
    if container is None or task is None:
        return None, "no parse: the session printed no metadata between the markers"
    try:
        started = time_ns(_body(container)["StartedAt"])
        drift = _body(task).get("ClockDrift")
    except (ValueError, KeyError, TypeError):
        return None, "no parse: the metadata bodies held no container StartedAt"
    keep = {key: drift.get(key) for key in ("ClockErrorBound", "ReferenceTimestamp", "ClockSynchronizationStatus")} \
        if isinstance(drift, dict) else None
    return {"started_ns": started, "clock_drift": keep}, ""


# --- the poll: T0, target healthy and k ------------------------------------------------------------

def windows(samples: List[dict]) -> Dict[str, Optional[dict]]:
    """T0 is the first sample whose reason is Elb.InitialHealthChecking, target healthy the first
    whose state is healthy; each as {after_ns, at_ns}: the sample before it and the one that saw it.
    https://docs.aws.amazon.com/elasticloadbalancing/latest/APIReference/API_TargetHealth.html"""
    found: Dict[str, Optional[dict]] = {"t0": None, "healthy": None}
    previous = None
    for sample in samples:
        if found["t0"] is None and sample.get("reason") == "Elb.InitialHealthChecking":
            found["t0"] = {"after_ns": previous, "at_ns": sample["at_ns"]}
        if found["healthy"] is None and sample.get("state") == "healthy":
            found["healthy"] = {"after_ns": previous, "at_ns": sample["at_ns"]}
        previous = sample["at_ns"]
    return found


def _mid(window: dict) -> float:
    after = window["after_ns"] if window["after_ns"] is not None else window["at_ns"]
    return (after + window["at_ns"]) / 2


def check_count(t0: Optional[dict], healthy: Optional[dict], interval_s: float = CHECK_INTERVAL_S) -> Optional[int]:
    """Which check after T0 passed: check 1 is T0's own, check 2 T0 + 30 s, check 3 the hard bound
    (#175 Q4). From the two windows' midpoints, so a poll period well under 30 s cannot move it."""
    if t0 is None or healthy is None:
        return None
    return 1 + max(0, round((_mid(healthy) - _mid(t0)) / (interval_s * 1e9)))


def stop_after_t0(t0: Optional[dict], seen: dict, bound_s: float = HARD_BOUND_S) -> str:
    """Where a stop the poll saw falls against T0 + bound_s, both on the workstation's clock (#919).
    The stop is after the T0 window's earliest instant and the stop window's earliest, before both
    windows' latest, so the low end pairs the stop's earliest with T0's latest and the high end the
    reverse. A stop window with no describe before it is its one instant (unreachable with a T0:
    a sample follows a describe). A T0 seen by the first sample has no earliest instant, so the
    high end stays open and the stop is never placed before the bound on it (#919 review)."""
    if t0 is None:
        return f"with no T0 (no sample saw Elb.InitialHealthChecking), not placed against the T0 + {bound_s:g} s hard bound"
    seen_after = seen["after_ns"] if seen["after_ns"] is not None else seen["at_ns"]
    low = (seen_after - t0["at_ns"]) / 1e9
    high = None if t0["after_ns"] is None else (seen["at_ns"] - t0["after_ns"]) / 1e9
    if low > bound_s:
        where = "past"
    elif high is not None and high < bound_s:
        where = "before"
    else:
        where = "across"
    shown = "?" if high is None else f"{high:.0f}"
    text = f"seen at T0 + {low:.0f}..{shown} s, {where} the T0 + {bound_s:g} s hard bound"
    return text + (": the poll cannot place it" if where == "across" else "")


def log_check(k: Optional[int], health_lines: int) -> str:
    """The cross-check of k against the log: every check that reached a listening gateway left one
    `health` line per load balancer node, and a check before it listened left none, so the lines up
    to the first 200 are at least one and at most ALB_NODES per check."""
    if k is None:
        return "not checked: no k"
    verdict = "consistent" if 1 <= health_lines <= ALB_NODES * k else "INCONSISTENT"
    return f"{verdict}: {health_lines} health line(s) to the first 200, at most {ALB_NODES} per check"


# --- the log ----------------------------------------------------------------------------------------

_REGISTRY_LIVE = re.compile(r"^Registry revision .* is live")


def read_lines(events: List[dict]) -> List[dict]:
    """Each event of the stream as the fields read here: its own `ts` and CloudWatch's event
    timestamp beside it (Q6), and route, status, duration_ms or the message. Nothing else of a line is
    kept, and a line that is not the gateway's JSON is skipped. A value invalid for its field is the
    marker before any calculation or mark can read it (#976 contract 3): its name earns it nothing. A
    line of the gateway's with a malformed ts is kept as the marker and nothing else, since no clock
    can stand on it."""
    lines = []
    for event in events:
        try:
            line = json.loads(event.get("message", ""))
            stamp = line["ts"]
        except (ValueError, KeyError, TypeError):
            continue
        if not isinstance(line, dict):
            continue
        received = event.get("timestamp")
        received = None if received is None else valid_event_ms(received)
        try:
            # The gateway writes its ts as a string; a number is the CLI's form, not the log's.
            ts_ns = time_ns(stamp if isinstance(stamp, str) else None)
        except ValueError:
            lines.append({"ts": MALFORMED, "event_timestamp_ms": received})
            continue
        kept = {"ts": stamp, "ts_ns": ts_ns, "event_timestamp_ms": received}
        if "route" in line:
            kept["route"] = line["route"] if isinstance(line["route"], str) else MALFORMED
        if "status" in line:
            kept["status"] = valid_status(line["status"])
        if "duration_ms" in line:
            kept["duration_ms"] = valid_duration(line["duration_ms"])
        msg = line.get("msg", "")
        if "route" not in line and isinstance(msg, str) and (msg.startswith("Now listening on") or _REGISTRY_LIVE.match(msg)):
            kept["msg"] = msg
        lines.append(kept)
    return lines


def log_marks(lines: List[dict]) -> dict:
    """The three log clocks, the 302 and the health lines up to the first passing check."""
    def first(test):
        return next((line for line in lines if test(line)), None)

    listening = first(lambda line: line.get("msg", "").startswith("Now listening on"))
    registry = first(lambda line: bool(_REGISTRY_LIVE.match(line.get("msg", ""))))
    passing = first(lambda line: line.get("route") == "health" and line.get("status") == 200)
    found = first(lambda line: line.get("status") == 302 and line.get("route") != "health")
    health = [line for line in lines if line.get("route") == "health"
              and (passing is None or line["ts_ns"] <= passing["ts_ns"])]
    return {"listening": listening, "registry_live": registry, "first_passing_check": passing,
            "redirect": found, "health_lines": len(health)}


# --- the AWS side -----------------------------------------------------------------------------------

_CODE = re.compile(r"An error occurred \(([^)]+)\)")


class Aws:
    def __init__(self, region: str):
        self.region = region

    def _argv(self, args) -> List[str]:
        return ["aws", "--region", self.region, *args]

    def __call__(self, *args) -> dict:
        done = subprocess.run(self._argv([*args, "--output", "json"]), capture_output=True, text=True, check=False)
        if done.returncode != 0:
            match = _CODE.search(done.stderr)
            # The message can name an ARN, so only the code is kept (cell-common.sh's rule).
            raise AwsError(match[1] if match else f"exit {done.returncode}")
        return json.loads(done.stdout) if done.stdout.strip() else {}

    def status(self, *args, timeout: Optional[float] = None) -> int:
        done = subprocess.run(self._argv(args), capture_output=True, text=True, check=False, timeout=timeout)
        return done.returncode

    def output(self, *args, timeout: float) -> Tuple[int, str, str]:
        done = subprocess.run(self._argv(args), capture_output=True, text=True, check=False, timeout=timeout,
                              stdin=subprocess.DEVNULL)
        match = _CODE.search(done.stderr)
        return done.returncode, done.stdout, (match[1] if match else f"exit {done.returncode}")

    def popen(self, *args, stderr) -> subprocess.Popen:
        return subprocess.Popen(self._argv(args), stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=stderr)


def read_outputs(region: str) -> dict:
    if not OUTPUTS_FILE.is_file():
        raise Refused("there is no infra/envs/poc/outputs.json. Run tools/scripts/seed.sh first: it writes it from the three roots.")
    try:
        document = json.loads(OUTPUTS_FILE.read_text())
    except (OSError, ValueError):
        raise Refused("infra/envs/poc/outputs.json is not JSON. Run tools/scripts/seed.sh again.")
    if not isinstance(document, dict) or document.get("region") != region:
        raise Refused(f"infra/envs/poc/outputs.json is not for {region}. Run tools/scripts/seed.sh again to rewrite it from the three roots.")
    names = {}
    for field in ("ecsCluster", "ecsService", "logGroup", "albDnsName", "gatewayImage"):
        value = document.get(field)
        if not isinstance(value, str) or not value:
            raise Refused(f"infra/envs/poc/outputs.json gives no {field} for {region}. Run tools/scripts/seed.sh again.")
        names[field] = value
    if not IMAGE_DIGEST.fullmatch(names["gatewayImage"].partition("@")[2]):
        raise Refused("infra/envs/poc/outputs.json's gatewayImage is not pinned by digest.")
    return names


def _last_segment(arn: str) -> str:
    return arn.rsplit("/", 1)[-1]


def task_id_of(arn) -> Optional[str]:
    """The id an ECS task ARN ends in, or None when it does not fit TASK_ID.
    https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs-account-settings.html"""
    if not isinstance(arn, str):
        return None
    candidate = _last_segment(arn)
    return candidate if TASK_ID.fullmatch(candidate) else None


def _ip(task: dict) -> Optional[str]:
    for attachment in task.get("attachments", []):
        if attachment.get("type") == "ElasticNetworkInterface":
            for detail in attachment.get("details", []):
                if detail.get("name") == "privateIPv4Address":
                    return detail.get("value")
    return None


def _container(task: dict, name: str) -> dict:
    return next((c for c in task.get("containers", []) if c.get("name") == name), {})


def _when(task: dict, key: str) -> Optional[int]:
    value = task.get(key)
    return None if value is None else time_ns(value)


# --- the run ----------------------------------------------------------------------------------------

class Bench:
    def __init__(self, options, region: str, names: dict, say):
        self.options = options
        self.region = region
        self.names = names
        self.aws = Aws(region)
        self.say = say
        self.account: Optional[str] = None
        self.cluster = names["ecsCluster"]
        self.service = names["ecsService"]
        self.digest = names["gatewayImage"].split("@", 1)[1]
        self.restarts: List[dict] = []
        self.updates_issued = 0
        self.facts: dict = {}
        self.forward: Optional[subprocess.Popen] = None
        self.forward_errors = None
        self.app_host = options.host
        self.server_name = options.host.rsplit(":", 1)[0]

    # --- before restart 1 ---

    def preflight(self) -> None:
        try:
            identity = self.aws("sts", "get-caller-identity")
        except AwsError as error:
            raise Refused(f"no AWS credentials answered get-caller-identity ({error.code}). Sign in with the spa-platform profile.")
        account = identity.get("Account")
        if not isinstance(account, str) or not CALLER_ACCOUNT.fullmatch(account):
            raise Refused("get-caller-identity named no well-formed account id.")
        self.account = account
        service = self.describe_service()
        balancers = service.get("loadBalancers") or []
        if not balancers or not balancers[0].get("targetGroupArn") or not balancers[0].get("containerName"):
            raise Refused(f"{self.service} has no load balancer target group.")
        self.target_group = balancers[0]["targetGroupArn"]
        self.container = balancers[0]["containerName"]
        definition = self.aws("ecs", "describe-task-definition", "--task-definition", service["taskDefinition"])
        container = next((c for c in definition.get("taskDefinition", {}).get("containerDefinitions", [])
                          if c.get("name") == self.container), None)
        if container is None:
            raise Refused(f"the service's task definition has no {self.container} container.")
        if container.get("image") != self.names["gatewayImage"]:
            raise Refused(f"{self.service} does not run gatewayImage: deploy it first (tools/scripts/deploy.sh).")
        options = (container.get("logConfiguration") or {}).get("options") or {}
        self.stream_prefix = options.get("awslogs-stream-prefix") or self.container
        if self.aws.status("ecs", "wait", "services-stable", "--cluster", self.cluster, "--services", self.service) != 0:
            raise Refused(f"{self.service} is not stable: the services-stable waiter gave up.")
        self.require_settled()
        running = self.running_tasks()
        if len(running) != 1 or _container(running[0], self.container).get("imageDigest") != self.digest:
            raise Refused(f"{self.service} is not stable on gatewayImage: its running task does not carry that digest.")
        family_revision = _last_segment(service["taskDefinition"])
        self.facts = {
            "commit": self.commit(), "image_digest": self.digest, "task_definition": family_revision,
            "platform_version": running[0].get("platformVersion") or service.get("platformVersion"),
            "region": self.region, "restarts": RESTARTS, "poll_seconds": self.options.poll_seconds,
            "check_interval_s": CHECK_INTERVAL_S, "app_host": self.app_host,
        }

    def commit(self) -> str:
        try:
            done = subprocess.run(["git", "-C", str(ROOT), "rev-parse", "HEAD"], capture_output=True, text=True, check=False)
        except OSError:
            return "unknown"
        # Optional, since it has this fallback (#305): publication makes what git printed the marker
        # when it does not fit COMMIT, and the run goes on.
        return done.stdout.strip() if done.returncode == 0 and done.stdout.strip() else "unknown"

    def task(self, task_id: Optional[str]) -> Published:
        return publish_identifier(task_id, TASK_ID, self.account)

    def describe_service(self) -> dict:
        services = self.aws("ecs", "describe-services", "--cluster", self.cluster, "--services", self.service).get("services") or []
        if not services:
            raise Refused(f"{self.cluster} has no service {self.service}.")
        return services[0]

    def require_settled(self, deployment: Optional[str] = None) -> None:
        """Stable is the waiter and then one deployment, COMPLETED (Q12): the old task has drained."""
        deployments = self.describe_service().get("deployments") or []
        settled = len(deployments) == 1 and deployments[0].get("rolloutState") == "COMPLETED" \
            and (deployment is None or deployments[0].get("id") == deployment)
        if not settled:
            message = f"{self.service} is not settled: its deployment has not completed."
            raise Refused(message) if deployment is None else Incomplete(message)

    def running_tasks(self) -> List[dict]:
        arns = self.aws("ecs", "list-tasks", "--cluster", self.cluster, "--service-name", self.service,
                        "--desired-status", "RUNNING").get("taskArns") or []
        return self.describe_tasks(arns) if arns else []

    def describe_tasks(self, arns: List[str]) -> List[dict]:
        return self.aws("ecs", "describe-tasks", "--cluster", self.cluster, "--tasks", *arns).get("tasks") or []

    # --- the hop ---

    def open_forward(self, through: dict, refuse: bool) -> str:
        """Q10: a port forward of our own, through the task about to be replaced."""
        container = _container(through, self.container)
        agent = next((a for a in container.get("managedAgents", []) if a.get("name") == "ExecuteCommandAgent"), {})
        task_id = task_id_of(through["taskArn"])
        problem = Refused if refuse else Incomplete
        if task_id is None:
            raise problem("the outgoing task's ARN holds no well-formed task id.")
        # A container takes a session once its ExecuteCommandAgent's lastStatus is RUNNING.
        # https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs-exec.html#ecs-exec-enabling
        if through.get("lastStatus") != "RUNNING" or not container.get("runtimeId") or agent.get("lastStatus") != "RUNNING":
            raise problem(compose("task {task} cannot carry a session: its {container} container's ExecuteCommandAgent is not running.",
                                  task=self.task(task_id), container=self.container))
        # ecs:<cluster>_<task id>_<container runtime id> names an ECS container to Session Manager.
        # https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager-working-with-sessions-start.html#sessions-remote-port-forwarding
        target = f"ecs:{self.cluster}_{task_id}_{container['runtimeId']}"
        parameters = json.dumps({"host": [self.names["albDnsName"]], "portNumber": ["443"],
                                 "localPortNumber": [str(self.options.local_port)]})
        self.forward_errors = open(os.devnull, "w")
        self.forward = self.aws.popen("ssm", "start-session", "--target", target,
                                      "--document-name", "AWS-StartPortForwardingSessionToRemoteHost",
                                      "--parameters", parameters, stderr=self.forward_errors)
        end = time.monotonic() + self.options.forward_timeout
        while time.monotonic() < end:
            if self.forward.poll() is not None:
                break
            if _listening(self.options.local_port):
                return task_id
            time.sleep(0.05)
        self.close_forward()
        raise problem(compose("the port forward through task {task} did not open within {timeout:g} s.",
                              task=self.task(task_id), timeout=self.options.forward_timeout))

    def close_forward(self) -> None:
        if self.forward is not None:
            if self.forward.poll() is None:
                self.forward.terminate()
                try:
                    self.forward.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    self.forward.kill()
                    self.forward.wait()
            self.forward = None
        if self.forward_errors is not None:
            self.forward_errors.close()
            self.forward_errors = None

    # --- one restart ---

    def restart(self, index: int) -> dict:
        record: dict = {"index": index, "complete": False}
        self.restarts.append(record)
        outgoing = self.running_tasks()
        if len(outgoing) != 1:
            raise (Refused if index == 1 else Incomplete)(f"{self.service} runs {len(outgoing)} tasks before restart {index}, not one.")
        record["hop_task"] = self.open_forward(outgoing[0], refuse=index == 1)
        try:
            # Counted before the call: once it is sent, the service may have changed (Endings: 4).
            self.updates_issued += 1
            answer = self.aws("ecs", "update-service", "--cluster", self.cluster, "--service", self.service,
                              "--force-new-deployment")
            primary = next((d for d in answer.get("service", {}).get("deployments", []) if d.get("status") == "PRIMARY"), None)
            if primary is None or not primary.get("id"):
                raise Incomplete(f"update-service named no PRIMARY deployment on restart {index}.")
            if not isinstance(primary["id"], str) or not DEPLOYMENT_ID.fullmatch(primary["id"]):
                raise Incomplete(f"update-service named a malformed PRIMARY deployment id on restart {index}.")
            record["deployment"] = primary["id"]
            self.say(compose("restart {index}/{restarts}: deployment {deployment}, hop through task {hop}", index=index,
                             restarts=RESTARTS, deployment=publish_identifier(primary["id"], DEPLOYMENT_ID, self.account),
                             hop=self.task(record["hop_task"])))
            task = self.new_task(primary["id"], record)
            self.watch_health(task, record)
            self.probe_until_redirect(record)
        finally:
            self.close_forward()
        if self.aws.status("ecs", "wait", "services-stable", "--cluster", self.cluster, "--services", self.service) != 0:
            raise Incomplete(f"restart {index}: the deployment was not stable within the services-stable waiter's bound.")
        self.require_settled(record["deployment"])
        final = self.describe_tasks([record["task_arn"]])
        if final:
            self.task_times(final[0], record)
        self.read_zero(record)
        self.wait_for_log(record)
        record["complete"] = True
        return record

    def new_task(self, deployment: str, record: dict) -> dict:
        """Q9: the task whose startedBy is the PRIMARY deployment's id, while the old one still runs."""
        end = time.monotonic() + self.options.task_timeout
        while True:
            arns = self.aws("ecs", "list-tasks", "--cluster", self.cluster, "--service-name", self.service).get("taskArns") or []
            for task in self.describe_tasks(arns) if arns else []:
                if task.get("startedBy") == deployment:
                    found = task_id_of(task["taskArn"])
                    if found is None:
                        raise Incomplete(f"restart {record['index']}: the new task's ARN holds no well-formed task id.")
                    record["task_arn"] = task["taskArn"]
                    record["task_id"] = found
                    record["created_ns"] = _when(task, "createdAt")
                    return task
            if time.monotonic() >= end:
                raise Incomplete(compose("restart {index}: no task of deployment {deployment} within {timeout:g} s.", index=record["index"],
                                         deployment=publish_identifier(deployment, DEPLOYMENT_ID, self.account),
                                         timeout=self.options.task_timeout))
            time.sleep(self.options.poll_seconds)

    def task_times(self, task: dict, record: dict) -> None:
        for key, name in (("createdAt", "created_ns"), ("pullStartedAt", "pull_started_ns"),
                          ("pullStoppedAt", "pull_stopped_ns"), ("startedAt", "started_ns")):
            if task.get(key) is not None:
                record[name] = _when(task, key)
        record["platform_version"] = task.get("platformVersion")

    def watch_health(self, task: dict, record: dict) -> None:
        """Q13: describe-target-health at about 1 s from the new task's createdAt, the new task
        described beside each sample so a stop for health ends the run (#286 Q148)."""
        samples: List[dict] = []
        record["samples"] = samples
        end = time.monotonic() + self.options.health_timeout
        # When the last describe-tasks that still saw the task running was sent: a stop the next one
        # sees came after it (#919).
        running_since: Optional[int] = None
        while True:
            asked = time.time_ns()
            current = self.describe_tasks([record["task_arn"]])
            task = current[0] if current else task
            self.task_times(task, record)
            if task.get("lastStatus") == "STOPPED" or task.get("desiredStatus") == "STOPPED":
                self.stopped(task, record, {"after_ns": running_since, "at_ns": time.time_ns()})
            running_since = asked
            address = _ip(task)
            described = self.aws("elbv2", "describe-target-health", "--target-group-arn", self.target_group)
            at = time.time_ns()
            health = next((d.get("TargetHealth", {}) for d in described.get("TargetHealthDescriptions", [])
                           if address and d.get("Target", {}).get("Id") == address), None)
            samples.append({"at_ns": at, "state": health.get("State") if health else None,
                            "reason": health.get("Reason") if health else None})
            if health and health.get("State") == "healthy":
                break
            if time.monotonic() >= end:
                raise Incomplete(compose("restart {index}: task {task}'s target was not healthy within {timeout:g} s.", index=record["index"],
                                         task=self.task(record["task_id"]), timeout=self.options.health_timeout))
            time.sleep(self.options.poll_seconds)
        found = windows(samples)
        record["t0"], record["healthy"] = found["t0"], found["healthy"]
        record["k"] = check_count(found["t0"], found["healthy"])

    def stopped(self, task: dict, record: dict, seen: dict) -> None:
        code = publish_text(task.get("stopCode") or "unknown")
        reason = publish_text(task.get("stoppedReason") or "")
        found = windows(record.get("samples", []))
        record["t0"] = found["t0"]
        record["stop"] = {"code": code, "reason": reason, "seen": seen, "after_t0": stop_after_t0(found["t0"], seen)}
        record["lines"] = self.read_log(record["task_id"])
        record["marks"] = log_marks(record["lines"])
        # A task the scheduler stops for failing its load balancer checks is the breach (#286 Q148),
        # wherever the stop falls against T0 + 60 s: the report places it (Endings: 3, #919). The task
        # definition declares no container health check (ecs.tf), so "health check" in a scheduler
        # stop's reason is the load balancer's.
        # https://docs.aws.amazon.com/AmazonECS/latest/developerguide/stopped-task-error-codes.html
        if code == "ServiceSchedulerInitiated" and "health check" in reason.lower():
            raise Breach(record)
        raise Incomplete(compose("restart {index}: task {task} stopped ({code}), not for health.", index=record["index"],
                                 task=self.task(record["task_id"]), code=code))

    # --- the 302 ---

    def probe_until_redirect(self, record: dict) -> None:
        probes: List[dict] = []
        record["probes"] = probes
        for _ in range(self.options.probe_attempts):
            probes.append({"at_ns": time.time_ns(), "status": probe(self.options.local_port, self.app_host,
                                                                      self.server_name, self.options.ca)})
            time.sleep(self.options.probe_interval)
            if log_marks(self.read_log(record["task_id"]))["redirect"] is not None:
                return
        raise Incomplete(compose("restart {index}: no 302 reached task {task}'s stream in {attempts} probes.", index=record["index"],
                                 task=self.task(record["task_id"]), attempts=self.options.probe_attempts))

    # --- the zero ---

    def read_zero(self, record: dict) -> None:
        """Q1, Q11: one interactive exec session on the new task, once stable; any failure takes
        pullStoppedAt, and the reason is kept."""
        try:
            status, out, code = self.aws.output("ecs", "execute-command", "--cluster", self.cluster,
                                                "--task", record["task_id"], "--container", self.container,
                                                "--interactive", "--command", EXEC_COMMAND,
                                                timeout=self.options.exec_timeout)
        except subprocess.TimeoutExpired:
            status, out, code = 1, "", "timeout"
        if status != 0:
            found, reason = None, f"no session: execute-command failed ({code})"
        else:
            found, reason = parse_metadata(out)
        if found is not None:
            record["zero"] = {"source": "metadata StartedAt", "ns": found["started_ns"], "clock_drift": found["clock_drift"]}
        else:
            record["zero"] = {"source": "pullStoppedAt", "ns": record.get("pull_stopped_ns"), "fallback": reason}

    # --- the log ---

    def read_log(self, task_id: str) -> List[dict]:
        stream = f"{self.stream_prefix}/{self.container}/{task_id}"
        events: List[dict] = []
        token = None
        while True:
            args = ["logs", "get-log-events", "--log-group-name", self.names["logGroup"], "--log-stream-name", stream,
                    "--start-from-head"]
            if token:
                args += ["--next-token", token]
            try:
                page = self.aws(*args)
            except AwsError as error:
                if error.code == "ResourceNotFoundException":
                    return []
                raise
            events += page.get("events") or []
            # The forward token repeats at the end of the stream.
            # https://docs.aws.amazon.com/AmazonCloudWatchLogs/latest/APIReference/API_GetLogEvents.html
            following = page.get("nextForwardToken")
            if not following or following == token:
                return read_lines(events)
            token = following

    def wait_for_log(self, record: dict) -> None:
        end = time.monotonic() + self.options.log_timeout
        while True:
            lines = self.read_log(record["task_id"])
            marks = log_marks(lines)
            missing = [name for name in ("listening", "registry_live", "first_passing_check", "redirect") if marks[name] is None]
            if not missing:
                record["lines"] = lines
                record["marks"] = marks
                # The 302's clock is this value, so a malformed one is no result; none at all stays
                # the row's "-", as a line without the field always was (#976 contract 5).
                if marks["redirect"].get("duration_ms") == MALFORMED:
                    raise Incomplete(compose("restart {index}: task {task}'s 302 line holds a malformed duration_ms.",
                                             index=record["index"], task=self.task(record["task_id"])))
                return
            if time.monotonic() >= end:
                record["lines"] = lines
                raise Incomplete(compose("restart {index}: task {task}'s stream has no {missing} line after {timeout:g} s.", index=record["index"],
                                         task=self.task(record["task_id"]), missing=", ".join(missing), timeout=self.options.log_timeout))
            time.sleep(self.options.poll_seconds)


def _listening(port: int) -> bool:
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=0.2):
            return True
    except OSError:
        return False


def probe(port: int, host: str, server_name: str, ca: str, timeout_s: float = 10.0) -> Optional[int]:
    """A cookieless navigation to the app's host through the forward: Host and SNI the app's, TLS
    verified against the cell's authority. Only the status line is read; the headers, which carry
    the challenge's state and its cookies, and the body are never read (Q7)."""
    context = ssl.create_default_context(cafile=ca)
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=timeout_s) as raw:
            with context.wrap_socket(raw, server_hostname=server_name) as tls:
                tls.sendall((f"GET / HTTP/1.1\r\nHost: {host}\r\nSec-Fetch-Mode: navigate\r\n"
                             "Sec-Fetch-Dest: document\r\nConnection: close\r\n\r\n").encode())
                line = b""
                while not line.endswith(b"\r\n") and len(line) < 256:
                    chunk = tls.recv(1)
                    if not chunk:
                        break
                    line += chunk
    except (OSError, ssl.SSLError):
        return None
    parts = line.split()
    if len(parts) < 2:
        return None
    # Publication checks the number against valid_status; this reads it, or says it was no number.
    return int(parts[1]) if parts[1].isdigit() else MALFORMED


# --- the report -------------------------------------------------------------------------------------

def clocks(record: dict) -> Dict[str, Optional[float]]:
    """One restart's values in ms. Container clocks from the zero; pull-and-place on the control
    plane; target healthy after T0 on the workstation; S - T0 as its two ends."""
    zero = (record.get("zero") or {}).get("ns")
    marks = record.get("marks") or {}

    def mark(name):
        line = marks.get(name)
        return ms(line["ts_ns"], zero) if line else None

    t0, healthy = record.get("t0"), record.get("healthy")
    redirect = marks.get("redirect")
    values = {
        "pull_and_place": ms(record.get("started_ns"), record.get("created_ns")),
        "pull": ms(record.get("pull_stopped_ns"), record.get("pull_started_ns")),
        "task_started": ms(record.get("started_ns"), zero),
        "pull_stopped": ms(record.get("pull_stopped_ns"), zero),
        "listening": mark("listening"),
        "registry_live": mark("registry_live"),
        "first_passing_check": mark("first_passing_check"),
        "target_healthy": None, "k": record.get("k"), "s_t0_low": None, "s_t0_high": None,
        "redirect_ms": float(redirect["duration_ms"]) if redirect and is_measurement(redirect.get("duration_ms")) else None,
        "redirect_mark": mark("redirect"),
    }
    if t0 and healthy:
        values["target_healthy"] = (_mid(healthy) - _mid(t0)) / 1e6
    if t0 and record.get("started_ns") is not None:
        after = t0["after_ns"] if t0["after_ns"] is not None else t0["at_ns"]
        values["s_t0_low"] = ms(record["started_ns"], t0["at_ns"])
        values["s_t0_high"] = ms(record["started_ns"], after)
    return values


ROWS = [
    ("pull_and_place", "pull-and-place (createdAt to startedAt, control plane)"),
    ("pull", "  of which pull (pullStartedAt to pullStoppedAt)"),
    ("task_started", "task startedAt from zero (cross-clock)"),
    ("pull_stopped", "pullStoppedAt from zero (cross-clock)"),
    ("listening", "listening (log)"),
    ("registry_live", "registry live (log)"),
    ("first_passing_check", "first passing check (log)"),
    ("target_healthy", "target healthy after T0 (window midpoints, workstation)"),
    ("k", "target healthy at check k after T0"),
    ("s_t0_low", "S - T0, low end (cross-clock)"),
    ("s_t0_high", "S - T0, high end (cross-clock)"),
    ("redirect_ms", "302 duration_ms (log)"),
    ("redirect_mark", "302 mark from zero (bounded by target healthy)"),
]


def _cell(value: Optional[float]) -> str:
    return "-" if value is None else f"{value:.0f}"


def report(facts: dict, records: List[dict], ending: str, reason: str) -> str:
    """The .txt, rendered from publish_document()'s copy with nothing passed over the result."""
    poll = facts.get("poll_seconds", 1)
    done = [record for record in records if record.get("complete")]
    per = [clocks(record) for record in records]
    lines = [
        "Startup benchmark, Fargate half (#305 for #178)",
        f"commit {facts.get('commit')}, image {facts.get('image_digest')}, task definition {facts.get('task_definition')}, "
        f"platform version {facts.get('platform_version')}, Region {facts.get('region')}",
        "clocks: task (the gateway's log line ts and the metadata endpoint's StartedAt, one microVM), control plane "
        "(describe-tasks createdAt, pullStartedAt, pullStoppedAt, startedAt = S), workstation (describe-target-health "
        f"polled every {f'{poll:g}' if is_measurement(poll) else poll} s: T0 and target healthy). A log clock is the line's own ts; "
        "CloudWatch's event timestamp is in the .json beside it (locally startup.py used dockerd's line timestamp).",
        "zero per restart: " + ", ".join(
            f"#{record['index']} " + ((record.get("zero") or {}).get("source") or "none")
            + (f" (fallback: {record['zero']['fallback']})" if (record.get("zero") or {}).get("fallback") else "")
            for record in records),
        "premise: nothing on the cell calls /healthz during the run, so the first route=health status=200 line is the "
        "load balancer's first passing /readyz check (the request line carries no path, contracts/telemetry.md).",
    ]
    if ending == "clean":
        lines.append(f"ending: {len(done)} clean restarts")
    elif ending == "breach":
        lines.append(f"ending: BREACH on restart {len(records)}: {reason}")
    else:
        lines.append(f"ending: INCOMPLETE after {len(done)} complete restart(s): {reason}")
    lines += ["", f"over {len(done)} complete restart(s), ms (k a count)"]
    table = [("clock", "median", "max")]
    for name, label in ROWS:
        values = [one[name] for one, record in zip(per, records) if record.get("complete") and one[name] is not None]
        table.append((label, _cell(statistics.median(values)) if values else "-", _cell(max(values)) if values else "-"))
    width = max(len(row[0]) for row in table)
    lines += [f"{row[0].ljust(width)}  {row[1].rjust(7)}  {row[2].rjust(7)}" for row in table]
    lines += ["", "per restart, ms from zero unless named"]
    for record, one in zip(records, per):
        state = "complete" if record.get("complete") else ("breach" if record.get("stop") and ending == "breach" else "incomplete")
        t0, healthy = record.get("t0"), record.get("healthy")
        windows_text = []
        for name, window in (("T0", t0), ("healthy", healthy)):
            if window:
                width_ms = ms(window["at_ns"], window["after_ns"])
                windows_text.append(f"{name} window {_cell(width_ms)} ms")
        marks = record.get("marks") or {}
        check = log_check(record.get("k"), marks.get("health_lines", 0)) if marks else "not checked: no log"
        row = (f"#{record['index']} task {record.get('task_id', '-')} {state}: pull-and-place {_cell(one['pull_and_place'])} "
               f"(pull {_cell(one['pull'])}), startedAt {_cell(one['task_started'])}, pullStoppedAt {_cell(one['pull_stopped'])}, "
               f"listening {_cell(one['listening'])}, registry live {_cell(one['registry_live'])}, "
               f"first passing check {_cell(one['first_passing_check'])}, target healthy {_cell(one['target_healthy'])} after T0 "
               f"(k {record.get('k') if record.get('k') is not None else '-'}; log {check}), "
               f"S - T0 {_cell(one['s_t0_low'])}..{_cell(one['s_t0_high'])} cross-clock, "
               f"302 {_cell(one['redirect_ms'])} ms at {_cell(one['redirect_mark'])}; {', '.join(windows_text) or 'no poll windows'}")
        drift = (record.get("zero") or {}).get("clock_drift")
        if drift:
            row += f"; ClockDrift bound {drift.get('ClockErrorBound')} ms, {drift.get('ClockSynchronizationStatus')}"
        if record.get("stop"):
            row += f"; stopped {record['stop']['code']}: {record['stop']['reason']}"
            if record["stop"].get("after_t0"):
                row += f"; {record['stop']['after_t0']}"
        lines.append(row)
    widest = {}
    for record in records:
        for name in ("t0", "healthy"):
            window = record.get(name)
            if window and window["after_ns"] is not None:
                widest[name] = max(widest.get(name, 0.0), ms(window["at_ns"], window["after_ns"]))
    lines += ["", "precision: the widest poll window, T0 " + (_cell(widest.get("t0")) if "t0" in widest else "-")
              + " ms, target healthy " + (_cell(widest.get("healthy")) if "healthy" in widest else "-")
              + f" ms; the check interval quantizes target healthy to {CHECK_INTERVAL_S} s; log clocks are the process's own ts"]
    return "\n".join(lines) + "\n"


def write(options, facts: dict, records: List[dict], ending: str, reason: str, account: Optional[str]) -> None:
    document = publish_document({"facts": facts, "complete": ending == "clean", "ending": ending, "reason": reason,
                                 "premise": "nothing on the cell calls /healthz during the run", "restarts": records}, account)
    pathlib.Path(options.txt).write_text(report(document["facts"], document["restarts"], ending, document["reason"]))
    # allow_nan=False: publication leaves no NaN, and a document no JSON parser reads is no report.
    pathlib.Path(options.json).write_text(json.dumps(document, indent=2, allow_nan=False) + "\n")


# --- the command line -------------------------------------------------------------------------------

def main(argv: List[str]) -> int:
    domain = os.environ.get("SPA_CERT_DOMAIN") or "spa-poc.test"
    parser = argparse.ArgumentParser(prog="fargate.py", description="The startup benchmark on the Fargate cell (#305).")
    parser.add_argument("--txt", required=True, help="the report, for #178 to commit under tools/bench/results/")
    parser.add_argument("--json", required=True, help="every restart's raw marks, samples and log lines")
    parser.add_argument("--host", default=f"app1.{domain}:8443", help="app1's registry host (default: %(default)s)")
    parser.add_argument("--ca", default=str(ROOT / ".certs" / "spa-platform-poc-ca.pem"),
                        help="the authority certs.sh issued (default: %(default)s)")
    parser.add_argument("--local-port", type=int, default=9443, help="the port forward's workstation port")
    parser.add_argument("--poll-seconds", type=float, default=1.0)
    parser.add_argument("--task-timeout", type=float, default=300)
    parser.add_argument("--health-timeout", type=float, default=900)
    parser.add_argument("--forward-timeout", type=float, default=60)
    parser.add_argument("--probe-attempts", type=int, default=30)
    parser.add_argument("--probe-interval", type=float, default=2.0)
    parser.add_argument("--exec-timeout", type=float, default=120)
    parser.add_argument("--log-timeout", type=float, default=180)

    def err(message: str) -> None:
        print(f"{PROGRAM}: {publish_text(message)}", file=sys.stderr, flush=True)

    try:
        options = parser.parse_args(argv)
    except SystemExit as stop:
        return EXIT_USAGE if stop.code else EXIT_CLEAN

    try:
        for tool, hint in (("aws", "brew install awscli"),
                           ("session-manager-plugin", "brew install --cask session-manager-plugin")):
            if shutil.which(tool) is None:
                raise Refused(f"{tool} is not on PATH. {hint}")
        region = os.environ.get("AWS_REGION") or os.environ.get("AWS_DEFAULT_REGION")
        if not region:
            raise Refused("no Region in AWS_REGION or AWS_DEFAULT_REGION. set -a; . tools/aws-project.env; set +a")
        names = read_outputs(region)
        if not pathlib.Path(options.ca).is_file():
            raise Refused("no authority at --ca. Run tools/scripts/certs.sh issue, or fetch-ca on a second workstation.")
        if _listening(options.local_port):
            raise Refused(f"something already listens on 127.0.0.1:{options.local_port}. Stop it, or pass --local-port.")
    except Refused as refusal:
        err(refusal.message)
        return EXIT_USAGE

    bench = Bench(options, region, names, say=lambda message: print(f"{PROGRAM}: {publish_text(message)}", flush=True))
    try:
        bench.preflight()
        # Q12's "about an hour" rested on the provider's 300 s drain; alb.tf sets 30 s (#347). A
        # restart now is the new task's pull and start (about 30 s), up to the T0 + 60 s third check,
        # the probe, the 30 s drain, up to the 30 s stopTimeout, the waiter's 15 s polls and the exec
        # read: about three minutes, so about half an hour for ten.
        bench.say(f"ten restarts, about half an hour: each runs two tasks side by side, and the old one drains for "
                  f"30 s and stops before the service is stable (#305 Q12, alb.tf's deregistration delay)")
    except Refused as refusal:
        err(refusal.message)
        return EXIT_USAGE
    except AwsError as error:
        err(f"an AWS call failed before restart 1 ({error.code}).")
        return EXIT_USAGE
    except Exception as error:
        # Endings: 2, nothing written. Only the type is kept: its message can carry what the AWS
        # side answered (Q7).
        err(f"an unexpected {type(error).__name__} before restart 1.")
        return EXIT_USAGE

    ending, reason, code = "clean", "", EXIT_CLEAN
    try:
        for index in range(1, RESTARTS + 1):
            try:
                bench.restart(index)
            except Refused as refusal:
                if index == 1 and bench.updates_issued == 0:
                    bench.restarts.pop()
                    err(refusal.message)
                    return EXIT_USAGE
                # update-service was sent, so this is no refusal before restart 1 (Q8): what was
                # measured is written, marked incomplete.
                raise Incomplete(refusal.message) from refusal
            except AwsError as error:
                # Before restart 1's update-service is sent nothing has changed on the service, so
                # this is preflight's ending (Endings: 2, nothing written), not an incomplete run.
                # A failure of that call itself is past this point (Endings: 4): updates_issued
                # counts the call before it is sent, and one predicate decides all three cases.
                if index == 1 and bench.updates_issued == 0:
                    bench.restarts.pop()
                    err(f"an AWS call failed before restart 1 ({error.code}).")
                    return EXIT_USAGE
                raise
            except (Breach, Incomplete):
                raise
            except Exception as error:
                # Any other failure before restart 1's update-service was sent: nothing has changed
                # on the service either, so the same ending (Endings: 2, nothing written).
                if index == 1 and bench.updates_issued == 0:
                    bench.restarts.pop()
                    err(f"an unexpected {type(error).__name__} before restart 1.")
                    return EXIT_USAGE
                raise
    except Breach as breach:
        row = breach.row
        ending, code = "breach", EXIT_BREACH
        reason = compose("the scheduler stopped task {task} for failing the load balancer's health checks ({code}), {after}",
                         task=bench.task(row.get("task_id")), code=row["stop"]["code"], after=row["stop"]["after_t0"])
    except Incomplete as stop:
        ending, reason, code = "incomplete", stop.message, EXIT_INCOMPLETE
    except AwsError as error:
        ending, reason, code = "incomplete", f"an AWS call failed ({error.code})", EXIT_INCOMPLETE
    except Exception as error:
        # Any other failure once restart 1 began still writes what was measured (Q8). Only the
        # exception's type is kept: its message can carry what the AWS side answered (Q7).
        ending, reason, code = "incomplete", f"an unexpected {type(error).__name__}", EXIT_INCOMPLETE
    finally:
        bench.close_forward()
    write(options, bench.facts, bench.restarts, ending, reason, bench.account)
    if code != EXIT_CLEAN:
        err(compose("{ending}: {reason}", ending=ending, reason=reason))
    bench.say(f"wrote {options.txt} and {options.json}")
    return code


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
