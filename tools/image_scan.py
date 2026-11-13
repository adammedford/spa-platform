#!/usr/bin/env python3
"""The images' scan policy: Trivy's reports against #779's answer 2 (#785). Owner: integrator.

    python3 -I tools/image_scan.py --accepted .github/image-scan-accepted.json [--today YYYY-MM-DD] \
        <name>=<report.json>...

tools/image-scan.sh builds the gateway's and the mock API's images, has the pinned Trivy write a
JSON report of each, and hands them here. This decides; Trivy only reads. The policy is #779's
answers 1 and 2 (https://github.com/adammedford/spa-platform/issues/779#issuecomment-6767167834):

- **A report must show the package list was read.** Answer 1 did not check that Trivy reads the
  package list of a chiseled image, and a scanner that reads nothing passes every image. So each
  report needs an OS result (`Class` os-pkgs) and a .NET result (`Type` dotnet-core), each with at
  least one package, which Trivy lists only under `--list-all-pkgs`
  (https://trivy.dev/latest/docs/configuration/reporting/#json). On 2026-11-11 Trivy 0.76.1 read 10
  Ubuntu packages from each of the deployed gateway and mock API images, whose chiseled base ships
  both /var/lib/chisel/manifest.wall and a /var/lib/dpkg/status (which of the two Trivy read was not
  checked), and the app's and both shared frameworks' deps.json; a report without both is not judged.
- **A HIGH or CRITICAL finding that has a fix fails**, unless it is accepted. "Has a fix" is a
  non-empty `FixedVersion`, which is how Trivy's JSON says a fixed version has shipped; a finding
  with no fix passes, as answer 2 chose over failing those too. Severity is Trivy's own `Severity`.
- **An accepted finding is written in .github/image-scan-accepted.json** with the issue that tracks
  it and an expiry at most 30 days after it was accepted, the cadence of .NET's monthly patch
  releases. It covers one finding id in one package, in every image, through its expiry day, and
  fails the scan from the day after. An acceptance that names no issue, spans more than 30 days, is
  dated ahead of today, names a finding twice or carries a key this file does not read fails the
  scan whatever the reports hold, so the file cannot drift past the rule. One that matches no
  finding is named, and fails nothing.

Exit 0 is a pass, 1 a finding or an acceptance the policy fails, 2 an input it could not judge: a
report or the accepted file it could not read, or a report that read no package list. Its lines say
"image-scan:", and its last line is its verdict, on standard error when it fails (CODING_STANDARDS.md,
"A check fails closed"). Its companion is tools/image_scan_tests.py.
"""

from __future__ import annotations

import datetime
import json
import sys

THRESHOLD = ("HIGH", "CRITICAL")
MAX_DAYS = 30
KEYS = {"id", "package", "issue", "accepted", "expires", "reason"}
NOTHING_READ = "a scanner that reads nothing passes every image (#779, answer 1)"


class Unjudged(Exception):
    """An input the policy could not judge: exit 2."""


def out(line: str):
    print(f"image-scan: {line}")


def err(line: str):
    print(f"image-scan: {line}", file=sys.stderr)


def day(value, what: str) -> datetime.date:
    if not isinstance(value, str):
        raise ValueError(f"{what} {value!r} is not a date")
    try:
        return datetime.date.fromisoformat(value)
    except ValueError:
        raise ValueError(f"{what} {value!r} is not a date") from None


def load_json(path: str, what: str):
    try:
        with open(path, encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, ValueError) as error:
        raise Unjudged(f"{what} {path} could not be read: {error}") from None


def read_accepted(path: str, today: datetime.date) -> tuple[dict, list[str]]:
    """The acceptances by (id, package), and each rule an entry breaks."""
    document = load_json(path, "the accepted file")
    if not isinstance(document, dict) or not isinstance(document.get("accepted"), list):
        raise Unjudged(f"the accepted file {path} has no \"accepted\" list")
    accepted: dict[tuple[str, str], dict] = {}
    broken: list[str] = []
    for number, entry in enumerate(document["accepted"], 1):
        where = f"acceptance {number} in {path}"
        if not isinstance(entry, dict):
            broken.append(f"{where} is not an object")
            continue
        unknown = sorted(set(entry) - KEYS)
        if unknown:
            broken.append(f"{where} has an unknown key: {', '.join(unknown)}")
        cve, pkg, issue = entry.get("id"), entry.get("package"), entry.get("issue")
        if not isinstance(cve, str) or not cve:
            broken.append(f"{where} names no finding id")
        if not isinstance(pkg, str) or not pkg:
            broken.append(f"{where} names no package")
        # bool is an int to Python; true is not an issue number.
        if not isinstance(issue, int) or isinstance(issue, bool) or issue <= 0:
            broken.append(f"{where} names no issue: give the number of the issue that tracks it")
        try:
            since = day(entry.get("accepted"), "its accepted date")
            until = day(entry.get("expires"), "its expiry")
        except ValueError as error:
            broken.append(f"{where}: {error}")
            continue
        if until < since:
            broken.append(f"{where} expires on {until}, before it was accepted on {since}")
        elif (until - since).days > MAX_DAYS:
            broken.append(f"{where} runs from {since} to {until}, more than {MAX_DAYS} days (#779, answer 2)")
        if since > today:
            broken.append(f"{where} is dated {since}, ahead of today, {today}")
        if isinstance(cve, str) and isinstance(pkg, str):
            if (cve, pkg) in accepted:
                broken.append(f"{where} accepts {cve} in {pkg} twice")
            accepted[(cve, pkg)] = {"issue": issue, "expires": until}
    return accepted, broken


def read_report(name: str, path: str) -> tuple[int, int, list[dict]]:
    """The OS and .NET package counts a report read, and its findings."""
    document = load_json(path, f"{name}'s report")
    if not isinstance(document, dict) or document.get("SchemaVersion") != 2:
        raise Unjudged(f"{name}'s report {path} is not a Trivy report of schema version 2")
    results = document.get("Results")
    if not isinstance(results, list):
        raise Unjudged(f"{name}'s report {path} has no Results")
    os_packages = dotnet_packages = 0
    findings: list[dict] = []
    for result in results:
        if not isinstance(result, dict):
            raise Unjudged(f"{name}'s report {path} has a result that is not an object")
        packages = result.get("Packages") or []
        if result.get("Class") == "os-pkgs":
            os_packages += len(packages)
        if result.get("Type") == "dotnet-core":
            dotnet_packages += len(packages)
        findings += result.get("Vulnerabilities") or []
    if os_packages == 0 or dotnet_packages == 0:
        raise Unjudged(f"{name}'s report read {os_packages} OS packages and {dotnet_packages} .NET packages, "
                       f"and {NOTHING_READ}")
    return os_packages, dotnet_packages, findings


def main(argv: list[str]) -> int:
    accepted_path = None
    today = datetime.datetime.now(datetime.timezone.utc).date()
    reports: list[tuple[str, str]] = []
    args = list(argv)
    try:
        while args:
            arg = args.pop(0)
            if arg in ("--accepted", "--today"):
                if not args:
                    raise Unjudged(f"{arg} needs a value")
                value = args.pop(0)
                if arg == "--accepted":
                    accepted_path = value
                else:
                    try:
                        today = day(value, "--today")
                    except ValueError as error:
                        raise Unjudged(str(error)) from None
            elif "=" in arg and not arg.startswith("-"):
                name, path = arg.split("=", 1)
                if not name or not path:
                    raise Unjudged(f"{arg} is not <name>=<report.json>")
                reports.append((name, path))
            else:
                raise Unjudged(f"{arg} is not <name>=<report.json>")
        if accepted_path is None:
            raise Unjudged("--accepted names no file")
        if not reports:
            raise Unjudged("no report was named")

        accepted, broken = read_accepted(accepted_path, today)
        read = [(name, *read_report(name, path)) for name, path in reports]
    except Unjudged as error:
        err(f"fail: not judged: {error}.")
        return 2

    failing = list(broken)
    for line in broken:
        err(line + ".")
    used: set[tuple[str, str]] = set()
    for name, os_packages, dotnet_packages, findings in read:
        at_threshold = [f for f in findings if f.get("Severity") in THRESHOLD and f.get("FixedVersion")]
        out(f"{name}: read {os_packages} OS packages and {dotnet_packages} .NET packages; "
            f"{len(at_threshold)} findings at the threshold.")
        for f in at_threshold:
            cve, pkg = f.get("VulnerabilityID", "?"), f.get("PkgName", "?")
            described = (f"{cve} in {pkg} {f.get('InstalledVersion', '?')}, {f['Severity']}, "
                         f"fixed in {f['FixedVersion']}")
            entry = accepted.get((cve, pkg))
            if entry is None:
                failing.append(f"{name}: {described}")
                err(f"{name}: {described}, is not accepted.")
                continue
            used.add((cve, pkg))
            if today > entry["expires"]:
                failing.append(f"{name}: {described}")
                err(f"{name}: {described}, was accepted on #{entry['issue']} until {entry['expires']}, which has passed.")
            else:
                out(f"{name}: {cve} in {pkg} is accepted on #{entry['issue']} until {entry['expires']}.")
    for (cve, pkg), entry in sorted(accepted.items()):
        if (cve, pkg) not in used:
            out(f"the acceptance of {cve} in {pkg} (#{entry['issue']}) matched no finding; "
                "remove it once nothing needs it.")

    names = ", ".join(name for name, *_ in read)
    if failing:
        err(f"fail: {len(failing)} against #779's threshold, a HIGH or CRITICAL finding with a fix that is not "
            f"accepted, or an acceptance that breaks its rules, in {names}.")
        return 1
    out(f"pass: {names}: no HIGH or CRITICAL finding with a fix that is not accepted.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
