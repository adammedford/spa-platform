#!/usr/bin/env python3
"""Test companion for tools/image-scan.sh and tools/image_scan.py, the images' vulnerability scan (#785).

Owner: integrator. The shape is D-3's in docs/dependencies.md: the real scripts, run as
subprocesses, against inputs built to trip each rule.

**The policy, tools/image_scan.py**, is run on Trivy reports written here in Trivy's own JSON shape
(SchemaVersion 2, the fields the 2026-11-11 scan of the deployed gateway image carried), so every
boundary of #779's answer 2 is a case: a HIGH and a CRITICAL finding with a fix fail, one with no
fix and a MEDIUM with a fix pass, an accepted finding passes until its expiry and fails the day
after, and an acceptance without an issue, over 30 days, dated ahead of today or naming a finding
twice fails. A report that read no package list fails as unjudged, because a scanner that reads
nothing passes every image (#779, answer 1).

**The scan, tools/image-scan.sh**, is run with a docker double on PATH, so no case builds an image,
pulls Trivy or reaches the network. The double records each call, answers `info`, and writes the
report a case gives it where Trivy's `--output` would have put it. The policy it hands that report
to is the real one.

**The workflow** is read as it stands: the scan step sits in the Build job, under that job's `if`,
and the workflow has no `paths` filter that would leave a required check pending.

Run: python3 -I tools/image_scan_tests.py
"""

from __future__ import annotations

import json
import os
import pathlib
import re
import shlex
import shutil
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
POLICY = ROOT / "tools" / "image_scan.py"
SCAN = ROOT / "tools" / "image-scan.sh"
ACCEPTED = ROOT / ".github" / "image-scan-accepted.json"
WORKFLOW = ROOT / ".github" / "workflows" / "ci.yml"
DEPENDENCIES = ROOT / "docs" / "dependencies.md"
BASH = "/bin/bash"

TODAY = "2026-11-11"


def package(name: str, version: str = "1.0") -> dict:
    return {"ID": f"{name}@{version}", "Name": name, "Version": version}


def finding(cve: str = "CVE-2026-0001", pkg: str = "libssl3t64", severity: str = "HIGH",
            fixed: str = "3.0.13-0ubuntu3.7") -> dict:
    """One entry of a result's Vulnerabilities, as Trivy writes it. An empty `fixed` is a finding
    Trivy reports with no FixedVersion, which is how it says no fix has shipped."""
    entry = {"VulnerabilityID": cve, "PkgName": pkg, "InstalledVersion": "3.0.13-0ubuntu3.5",
             "Severity": severity, "Status": "fixed" if fixed else "affected"}
    if fixed:
        entry["FixedVersion"] = fixed
    return entry


def report(*, os_packages: int = 10, dotnet_packages: int = 52, os_findings=(), dotnet_findings=()) -> dict:
    """A Trivy image report: an Ubuntu result read from the chiseled image's package list and a
    .NET result read from the app's deps.json, each with its packages (--list-all-pkgs) and the
    findings a case gives it. A count of None leaves that result out altogether."""
    results = []
    if os_packages is not None:
        results.append({"Target": "/scan/gateway.tar (ubuntu 24.04)", "Class": "os-pkgs", "Type": "ubuntu",
                        "Packages": [package(f"os-{n}") for n in range(os_packages)],
                        "Vulnerabilities": list(os_findings)})
    if dotnet_packages is not None:
        results.append({"Target": "app/Gateway.deps.json", "Class": "lang-pkgs", "Type": "dotnet-core",
                        "Packages": [package(f"Dotnet.{n}") for n in range(dotnet_packages)],
                        "Vulnerabilities": list(dotnet_findings)})
    return {"SchemaVersion": 2, "Trivy": {"Version": "0.76.1"}, "ArtifactName": "/scan/gateway.tar",
            "ArtifactType": "container_image", "Results": results}


def acceptance(cve: str = "CVE-2026-0001", pkg: str = "libssl3t64", issue=812, accepted: str = "2026-11-01",
               expires: str = "2026-12-01", **extra) -> dict:
    entry = {"id": cve, "package": pkg, "issue": issue, "accepted": accepted, "expires": expires,
             "reason": "No patched base image yet; the pins sweep moves the digest when one ships."}
    entry.update(extra)
    return entry


class Scratch:
    """A throwaway directory for one case's reports and accepted file."""

    def __init__(self, test: unittest.TestCase):
        scratch = tempfile.TemporaryDirectory()
        test.addCleanup(scratch.cleanup)
        self.dir = pathlib.Path(scratch.name).resolve()

    def write(self, name: str, value) -> pathlib.Path:
        path = self.dir / name
        path.write_text(value if isinstance(value, str) else json.dumps(value))
        return path

    def accepted(self, *entries) -> pathlib.Path:
        return self.write("accepted.json", {"about": "accepted findings", "accepted": list(entries)})


def policy(*args: str) -> subprocess.CompletedProcess:
    return subprocess.run(["python3", "-I", str(POLICY), *args], capture_output=True, text=True, timeout=60)


class PolicyTests(unittest.TestCase):
    """#779's answer 2, as tools/image_scan.py applies it to Trivy's report."""

    def judge(self, scratch: Scratch, accepted: pathlib.Path, *reports: tuple[str, dict], today: str = TODAY):
        paths = [f"{name}={scratch.write(f'{name}.json', value)}" for name, value in reports]
        return policy("--accepted", str(accepted), "--today", today, *paths)

    def assertVerdict(self, completed: subprocess.CompletedProcess, status: int, start: str):
        self.assertEqual(status, completed.returncode, completed.stdout + completed.stderr)
        stream = completed.stdout if status == 0 else completed.stderr
        lines = stream.splitlines()
        self.assertTrue(lines, completed.stdout + completed.stderr)
        self.assertTrue(lines[-1].startswith(start), lines[-1])

    def test_a_clean_report_passes_and_says_what_it_read(self):
        scratch = Scratch(self)
        completed = self.judge(scratch, scratch.accepted(), ("gateway", report()), ("mock-api", report(dotnet_packages=10)))
        self.assertVerdict(completed, 0, "image-scan: pass:")
        self.assertIn("image-scan: gateway: read 10 OS packages and 52 .NET packages; 0 findings at the threshold.",
                      completed.stdout.splitlines())
        self.assertIn("image-scan: mock-api: read 10 OS packages and 10 .NET packages; 0 findings at the threshold.",
                      completed.stdout.splitlines())

    def test_a_high_or_critical_finding_with_a_fix_fails_naming_it(self):
        for severity in ("HIGH", "CRITICAL"):
            with self.subTest(severity=severity):
                scratch = Scratch(self)
                completed = self.judge(scratch, scratch.accepted(),
                                       ("gateway", report(os_findings=[finding(severity=severity)])))
                self.assertVerdict(completed, 1, "image-scan: fail:")
                self.assertIn(f"image-scan: gateway: CVE-2026-0001 in libssl3t64 3.0.13-0ubuntu3.5, {severity}, "
                              "fixed in 3.0.13-0ubuntu3.7, is not accepted.", completed.stderr.splitlines())

    def test_a_finding_in_a_dotnet_package_fails_too(self):
        scratch = Scratch(self)
        planted = finding(cve="CVE-2026-0002", pkg="Microsoft.NETCore.App.Runtime.linux-x64", fixed="10.0.15")
        completed = self.judge(scratch, scratch.accepted(), ("gateway", report(dotnet_findings=[planted])))
        self.assertVerdict(completed, 1, "image-scan: fail:")

    def test_a_finding_in_either_image_fails_the_scan(self):
        scratch = Scratch(self)
        completed = self.judge(scratch, scratch.accepted(), ("gateway", report()),
                               ("mock-api", report(os_findings=[finding()])))
        self.assertVerdict(completed, 1, "image-scan: fail:")
        self.assertIn("image-scan: mock-api: CVE-2026-0001", completed.stderr)

    def test_below_the_threshold_or_without_a_fix_passes(self):
        for name, planted in (("MEDIUM with a fix", finding(severity="MEDIUM")),
                              ("LOW with a fix", finding(severity="LOW")),
                              ("UNKNOWN with a fix", finding(severity="UNKNOWN")),
                              ("HIGH with no fix", finding(fixed="")),
                              ("CRITICAL with no fix", finding(severity="CRITICAL", fixed=""))):
            with self.subTest(name):
                scratch = Scratch(self)
                completed = self.judge(scratch, scratch.accepted(), ("gateway", report(os_findings=[planted])))
                self.assertVerdict(completed, 0, "image-scan: pass:")

    def test_an_accepted_finding_passes_until_its_expiry_and_fails_after_it(self):
        planted = report(os_findings=[finding()])
        for today, status in (("2026-11-01", 0), ("2026-11-20", 0), ("2026-12-01", 0), ("2026-12-02", 1)):
            with self.subTest(today=today):
                scratch = Scratch(self)
                completed = self.judge(scratch, scratch.accepted(acceptance()), ("gateway", planted), today=today)
                if status == 0:
                    self.assertVerdict(completed, 0, "image-scan: pass:")
                    self.assertIn("image-scan: gateway: CVE-2026-0001 in libssl3t64 is accepted on #812 until 2026-12-01.",
                                  completed.stdout.splitlines())
                else:
                    self.assertVerdict(completed, 1, "image-scan: fail:")
                    self.assertIn("image-scan: gateway: CVE-2026-0001 in libssl3t64 3.0.13-0ubuntu3.5, HIGH, fixed in "
                                  "3.0.13-0ubuntu3.7, was accepted on #812 until 2026-12-01, which has passed.",
                                  completed.stderr.splitlines())

    def test_an_acceptance_covers_its_own_package_only(self):
        scratch = Scratch(self)
        completed = self.judge(scratch, scratch.accepted(acceptance(pkg="openssl")),
                               ("gateway", report(os_findings=[finding()])))
        self.assertVerdict(completed, 1, "image-scan: fail:")

    def test_an_acceptance_no_finding_needs_is_named_and_fails_nothing(self):
        scratch = Scratch(self)
        completed = self.judge(scratch, scratch.accepted(acceptance(cve="CVE-2026-9999")), ("gateway", report()))
        self.assertVerdict(completed, 0, "image-scan: pass:")
        self.assertIn("image-scan: the acceptance of CVE-2026-9999 in libssl3t64 (#812) matched no finding; "
                      "remove it once nothing needs it.", completed.stdout.splitlines())

    def test_an_acceptance_that_breaks_the_rules_fails_the_scan(self):
        for name, entry, message in (
            ("no issue", acceptance(issue=None), "names no issue"),
            ("issue zero", acceptance(issue=0), "names no issue"),
            ("issue as text", acceptance(issue="#812"), "names no issue"),
            ("over 30 days", acceptance(accepted="2026-11-01", expires="2026-12-02"), "more than 30 days"),
            ("expiry before acceptance", acceptance(accepted="2026-11-10", expires="2026-11-09"), "before it was accepted"),
            ("accepted ahead of today", acceptance(accepted="2026-11-12", expires="2026-12-12"), "ahead of today"),
            ("not a date", acceptance(expires="next month"), "not a date"),
            ("no id", acceptance(cve=""), "no finding id"),
            ("no package", acceptance(pkg=""), "no package"),
            ("unknown key", acceptance(image="gateway"), "unknown key"),
        ):
            with self.subTest(name):
                scratch = Scratch(self)
                completed = self.judge(scratch, scratch.accepted(acceptance(cve="CVE-2026-0100"), entry),
                                       ("gateway", report()))
                self.assertVerdict(completed, 1, "image-scan: fail:")
                self.assertIn(message, completed.stderr)

    def test_an_acceptance_of_exactly_30_days_is_within_the_rule(self):
        scratch = Scratch(self)
        completed = self.judge(scratch, scratch.accepted(acceptance(accepted="2026-11-01", expires="2026-12-01")),
                               ("gateway", report(os_findings=[finding()])))
        self.assertVerdict(completed, 0, "image-scan: pass:")

    def test_the_same_finding_accepted_twice_fails(self):
        scratch = Scratch(self)
        completed = self.judge(scratch, scratch.accepted(acceptance(), acceptance(issue=813)), ("gateway", report()))
        self.assertVerdict(completed, 1, "image-scan: fail:")
        self.assertIn("twice", completed.stderr)

    def test_a_report_that_read_no_package_list_is_not_judged(self):
        # #779 answer 1: a scanner that reads nothing passes every image. A report with no OS
        # result, an empty one, no .NET result, or results without --list-all-pkgs' packages is
        # exactly that report, findings or none.
        for name, value in (("no OS result", report(os_packages=None)),
                            ("no OS packages", report(os_packages=0)),
                            ("no .NET result", report(dotnet_packages=None)),
                            ("no .NET packages", report(dotnet_packages=0)),
                            ("no results", {"SchemaVersion": 2, "Results": []}),
                            ("results without packages",
                             {"SchemaVersion": 2, "Results": [{k: v for k, v in result.items() if k != "Packages"}
                                                              for result in report()["Results"]]})):
            with self.subTest(name):
                scratch = Scratch(self)
                completed = self.judge(scratch, scratch.accepted(), ("gateway", value))
                self.assertVerdict(completed, 2, "image-scan: fail: not judged:")
                self.assertIn("a scanner that reads nothing passes every image", completed.stderr)

    def test_an_unreadable_input_is_not_judged(self):
        scratch = Scratch(self)
        good = scratch.write("good.json", report())
        for name, args in (
            ("a report that is not JSON", ["--accepted", str(scratch.accepted()), f"gateway={scratch.write('bad.json', '{')}"]),
            ("a missing report", ["--accepted", str(scratch.accepted()), f"gateway={scratch.dir / 'none.json'}"]),
            ("another schema version", ["--accepted", str(scratch.accepted()),
                                        f"gateway={scratch.write('v1.json', dict(report(), SchemaVersion=1))}"]),
            ("a missing accepted file", ["--accepted", str(scratch.dir / "none.json"), f"gateway={good}"]),
            ("an accepted file that is not JSON", ["--accepted", str(scratch.write("a.json", "[")), f"gateway={good}"]),
            ("an accepted file without its list", ["--accepted", str(scratch.write("b.json", {"about": "x"})), f"gateway={good}"]),
            ("no report", ["--accepted", str(scratch.accepted())]),
            ("a report not named", ["--accepted", str(scratch.accepted()), str(good)]),
            ("today not a date", ["--accepted", str(scratch.accepted()), "--today", "tomorrow", f"gateway={good}"]),
        ):
            with self.subTest(name):
                completed = policy(*args)
                self.assertEqual(2, completed.returncode, completed.stdout + completed.stderr)
                self.assertTrue(completed.stderr.splitlines()[-1].startswith("image-scan: fail: not judged:"),
                                completed.stderr)

    def test_the_tracked_accepted_file_keeps_the_rules(self):
        # The file CI reads, judged against an empty report on today's date: every acceptance it
        # holds names an issue, spans 30 days or fewer and is not dated ahead of today.
        scratch = Scratch(self)
        completed = policy("--accepted", str(ACCEPTED), f"gateway={scratch.write('r.json', report())}")
        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)


# The docker double for tools/image-scan.sh: records each call, NUL-separated, in its own file, and
# answers as docker would for what the script asks. `run` of the Trivy image writes the report the
# case left in $REPORTS/<name>.json to the host side of --output. Any call whose arguments contain
# $FAIL_ON fails.
DOCKER = r"""#!/bin/bash
n=0
while [ -e "$LOG/call-$n" ]; do n=$((n + 1)); done
printf '%s\0' "$@" > "$LOG/call-$n"
if [ -n "${FAIL_ON:-}" ]; then
  case " $* " in *" $FAIL_ON "*|*"$FAIL_ON"*) echo "docker double failed: $*" >&2; exit 1 ;; esac
fi
case "$1" in
  info) exit 0 ;;
  save)
    shift
    while [ "$#" -gt 0 ]; do case "$1" in -o) : > "$2"; shift 2 ;; *) shift ;; esac; done
    exit 0
    ;;
  run)
    host="" output=""
    while [ "$#" -gt 0 ]; do
      case "$1" in
        -v) case "$2" in *:/scan) host="${2%:/scan}" ;; esac; shift 2 ;;
        --output) output="$2"; shift 2 ;;
        *) shift ;;
      esac
    done
    if [ -n "$output" ]; then
      name="${output##*/}"
      cp "$REPORTS/$name" "$host/$name"
    fi
    exit 0
    ;;
esac
exit 0
"""


class ScanCase:
    """A throwaway checkout holding the scan's files, and a bin directory with the docker double."""

    def __init__(self, test: unittest.TestCase, reports: dict[str, dict] | None = None, fail_on: str = "",
                 docker: bool = True, accepted: dict | None = None):
        scratch = tempfile.TemporaryDirectory()
        test.addCleanup(scratch.cleanup)
        self.dir = pathlib.Path(scratch.name).resolve()
        self.work, self.bin, self.log, self.reports, self.home = (
            self.dir / "work", self.dir / "bin", self.dir / "log", self.dir / "reports", self.dir / "home")
        for directory in (self.work / "tools", self.work / ".github", self.bin, self.log, self.reports, self.home):
            directory.mkdir(parents=True)
        shutil.copy(SCAN, self.work / "tools" / "image-scan.sh")
        shutil.copy(POLICY, self.work / "tools" / "image_scan.py")
        (self.work / ".github" / "image-scan-accepted.json").write_text(
            json.dumps(accepted if accepted is not None else {"about": "none", "accepted": []}))
        subprocess.run(["git", "init", "-q", str(self.work)], check=True, capture_output=True)
        for tool in ("git", "python3", "date", "mktemp", "rm", "mkdir", "cat", "dirname", "cp"):
            found = shutil.which(tool)
            test.assertIsNotNone(found, f"{tool} is required to run this companion")
            (self.bin / tool).symlink_to(found)
        (self.bin / "bash").symlink_to(BASH)
        if docker:
            (self.bin / "docker").write_text(DOCKER)
            (self.bin / "docker").chmod(0o755)
        for name, value in (reports or {"gateway": report(), "mock-api": report(dotnet_packages=10)}).items():
            (self.reports / f"{name}.json").write_text(json.dumps(value))
        self.fail_on = fail_on

    def run(self, *args: str) -> subprocess.CompletedProcess:
        env = {"PATH": str(self.bin), "HOME": str(self.home), "LOG": str(self.log), "REPORTS": str(self.reports),
               "FAIL_ON": self.fail_on, "LC_ALL": "C"}
        return subprocess.run([BASH, str(self.work / "tools" / "image-scan.sh"), *args], cwd=self.work, env=env,
                              capture_output=True, text=True, timeout=60)

    def calls(self) -> list[list[str]]:
        names = sorted(self.log.glob("call-*"), key=lambda path: int(path.name.split("-")[1]))
        return [path.read_text().split("\0")[:-1] for path in names]


def pinned(name: str, text: str) -> str:
    """The image reference a script pins in its `<name>=` line."""
    match = re.search(rf'^{name}="([^"]+)"$', text, re.M)
    assert match, f"no {name}= line"
    return match[1]


TRIVY = pinned("TRIVY_IMAGE", SCAN.read_text())


class ScanTests(unittest.TestCase):
    """tools/image-scan.sh builds both images, saves each, scans each with the pinned Trivy and hands
    the reports to the policy, whose verdict is its own."""

    def assertLast(self, completed: subprocess.CompletedProcess, status: int, start: str):
        self.assertEqual(status, completed.returncode, completed.stdout + completed.stderr)
        lines = (completed.stdout if status == 0 else completed.stderr).splitlines()
        self.assertTrue(lines and lines[-1].startswith(start), completed.stdout + completed.stderr)

    def test_it_builds_saves_and_scans_both_images_with_the_pinned_trivy(self):
        case = ScanCase(self)
        completed = case.run()
        self.assertLast(completed, 0, "image-scan: pass:")
        calls = case.calls()
        self.assertEqual(["info"], calls[0])
        builds = [call for call in calls if call[0] == "build"]
        self.assertEqual([["src/Gateway/Dockerfile"], ["fixtures/MockApi/Dockerfile"]],
                         [[call[call.index("-f") + 1]] for call in builds])
        saves = [call for call in calls if call[0] == "save"]
        self.assertEqual(2, len(saves))
        scans = [call for call in calls if call[0] == "run"]
        self.assertEqual(2, len(scans))
        for scan, name in zip(scans, ("gateway", "mock-api")):
            self.assertIn(TRIVY, scan)
            after = scan[scan.index(TRIVY) + 1:]
            self.assertEqual("image", after[0])
            # Every package listed, so the policy can see the package list was read, and the image
            # read from the saved archive, not from a daemon socket.
            for flag in ("--list-all-pkgs", "--scanners", "vuln", "--format", "json", "--input",
                         f"/scan/{name}.tar", "--output", f"/scan/{name}.json"):
                self.assertIn(flag, after)
        self.assertIn("image-scan: gateway: read 10 OS packages and 52 .NET packages; 0 findings at the threshold.",
                      completed.stdout.splitlines())

    def test_a_planted_finding_fails_the_scan(self):
        case = ScanCase(self, reports={"gateway": report(os_findings=[finding()]), "mock-api": report()})
        completed = case.run()
        self.assertLast(completed, 1, "image-scan: fail:")
        self.assertIn("image-scan: gateway: CVE-2026-0001 in libssl3t64", completed.stderr)

    def test_a_report_with_no_package_list_fails_the_scan(self):
        case = ScanCase(self, reports={"gateway": report(os_packages=0), "mock-api": report()})
        self.assertLast(case.run(), 2, "image-scan: fail: not judged:")

    def test_the_tracked_accepted_file_is_the_one_read(self):
        accepted = {"about": "x", "accepted": [acceptance(accepted="2026-11-01", expires="2026-12-01")]}
        case = ScanCase(self, reports={"gateway": report(os_findings=[finding()]), "mock-api": report()},
                        accepted=accepted)
        completed = case.run()
        # The acceptance holds or has expired depending on today, but either way it was read: an
        # unread file would leave the finding "not accepted".
        self.assertIn("accepted on #812 until 2026-12-01", completed.stdout + completed.stderr)

    def test_a_failed_step_fails_with_a_verdict(self):
        for fail_on, message in (("build", "did not build"), ("save", "could not be saved"),
                                 (TRIVY, "Trivy did not scan"), ("info", "does not answer")):
            with self.subTest(fail_on=fail_on):
                case = ScanCase(self, fail_on=fail_on)
                completed = case.run()
                self.assertLast(completed, 1, "image-scan: fail:")
                self.assertIn(message, completed.stderr.splitlines()[-1])

    def test_without_docker_it_fails(self):
        case = ScanCase(self, docker=False)
        completed = case.run()
        self.assertLast(completed, 1, "image-scan: fail: docker is not on PATH")

    def test_named_images_are_scanned_without_a_build(self):
        # The form a planted-finding run uses: an image already built, under the name it reports.
        case = ScanCase(self, reports={"old": report()})
        completed = case.run("old=example/old:1")
        self.assertLast(completed, 0, "image-scan: pass:")
        self.assertEqual([], [call for call in case.calls() if call[0] == "build"])
        self.assertIn(["save", "-o"], [call[:2] for call in case.calls()])
        self.assertIn("example/old:1", [arg for call in case.calls() if call[0] == "save" for arg in call])

    def test_a_named_image_that_is_not_name_equals_ref_is_refused(self):
        case = ScanCase(self)
        completed = case.run("example/old:1")
        self.assertLast(completed, 2, "image-scan: fail:")
        self.assertEqual([], case.calls())


class PinTests(unittest.TestCase):
    """The pins: an exact version beside its digest, and the same pair in docs/dependencies.md."""

    def test_trivy_is_pinned_by_version_and_digest_and_listed(self):
        self.assertRegex(TRIVY, r"^aquasec/trivy:\d+\.\d+\.\d+@sha256:[0-9a-f]{64}$")
        self.assertIn(f"`{TRIVY}`", DEPENDENCIES.read_text())


class WorkflowTests(unittest.TestCase):
    """The scan runs in CI where the ticket puts it: in the Build job, under its `if`."""

    def jobs(self) -> dict[str, str]:
        text = WORKFLOW.read_text()
        body = text.split("\njobs:\n", 1)[1]
        parts = re.split(r"^  ([a-z0-9-]+):\n", body, flags=re.M)
        return dict(zip(parts[1::2], parts[2::2]))

    def test_the_scan_step_runs_in_the_build_job_under_its_if(self):
        jobs = self.jobs()
        build = jobs["build-and-test"]
        self.assertIn("    if: ${{ !cancelled() && needs.documents.outputs.build != 'false' }}\n", build)
        self.assertIn("        run: tools/image-scan.sh\n", build)
        steps = re.split(r"^      - ", build, flags=re.M)
        scan = next(step for step in steps if "run: tools/image-scan.sh" in step)
        # A step-level `if` would let the scan skip while the job runs.
        self.assertNotRegex(scan, r"(?m)^        if:", "the scan step has an if of its own")
        for name, job in jobs.items():
            if name != "build-and-test":
                self.assertNotIn("tools/image-scan.sh\n", job, f"the scan also runs in {name}")

    def test_the_workflow_has_no_paths_filter(self):
        on = WORKFLOW.read_text().split("\non:\n", 1)[1].split("\npermissions:", 1)[0]
        self.assertNotIn("paths", on)

    def test_the_companion_runs_in_the_documents_job(self):
        self.assertIn("run: python3 -I tools/image_scan_tests.py\n", self.jobs()["documents"])


if __name__ == "__main__":
    unittest.main()
