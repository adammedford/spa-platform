#!/usr/bin/env python3
"""Test companion for tools/pins_inventory.py, the pins sweep's inventory. Owner: integrator.

Specified in #82 -> Testing Decisions -> "Companion tests"; built under #269.

**The seam is the command the Inventory agent runs.** Every case starts the real file as
`python3 -I tools/pins_inventory.py ...` against a throwaway git repository this companion builds
from copies of this checkout's pins file, SDK pin file, solution, build properties and project
files, in the shape of the roadmap tests' temp copies, and asserts on the JSON or table it prints.
Every network input is a file the case writes, so no case reaches nuget.org; the one case that
needs it unreachable points the proxy at a closed port.

Each rule has a case that would pass if the rule were gone: a planted newest beside an unplanted
one, a swapped pair of version lists, an advisory and its removal, a document naming the pinned
version beside one naming another, the decision comment and its removal.

The copies are of the checkout, so a change to one of them can turn a case red with no change
here: on #336 a new pin did. The line below names them for tools/check.sh, whose full mode runs
this companion when a change touches one (#354), as CI's Documents job runs it on every change. A
file Repo starts copying joins the line; tools/check_tests.py fails while one is missing.

Checkout inputs: Directory.Packages.props global.json SpaPlatform.slnx Directory.Build.props *.csproj

Run: python3 -I tools/pins_inventory_tests.py
"""

from __future__ import annotations

import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
COMPANION = ROOT / "tools" / "pins_inventory.py"
GHSA = "GHSA-jrjw-qgr2-wfcg"
PROPS = "Directory.Packages.props"


def git(repo, *argv):
    subprocess.run(["git", "-C", str(repo), *argv], check=True, capture_output=True)


def solution_projects():
    text = (ROOT / "SpaPlatform.slnx").read_text(encoding="utf-8")
    return re.findall(r'<Project Path="([^"]+)"', text)


class Repo:
    """A throwaway repository holding copies of this checkout's inventory inputs, committed."""

    def __init__(self, case: unittest.TestCase):
        self.dir = pathlib.Path(tempfile.mkdtemp(prefix="pins-inventory-tests-"))
        case.addCleanup(shutil.rmtree, self.dir, True)
        git(self.dir, "init", "-q")
        for rel in [PROPS, "global.json", "SpaPlatform.slnx", "Directory.Build.props", *solution_projects()]:
            self.copy(rel)

    def copy(self, rel):
        target = self.dir / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(ROOT / rel, target)

    def write(self, rel, text):
        target = self.dir / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")
        return target

    def read(self, rel):
        return (self.dir / rel).read_text(encoding="utf-8")

    def remove(self, rel):
        (self.dir / rel).unlink()

    def commit(self):
        git(self.dir, "add", "-A")
        git(self.dir, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "fixture")

    def input(self, name, data):
        path = self.dir.parent / f"{self.dir.name}-{name}.json"
        path.write_text(json.dumps(data), encoding="utf-8")
        self.inputs = getattr(self, "inputs", []) + [path]
        return str(path)


def run(*argv, env=None):
    done = subprocess.run([sys.executable, "-I", str(COMPANION), *argv], capture_output=True, text=True, env=env)
    return done.returncode, done.stdout, done.stderr


class Case(unittest.TestCase):
    def repo(self) -> Repo:
        r = Repo(self)
        self.addCleanup(lambda: [p.unlink() for p in getattr(r, "inputs", []) if p.exists()])
        return r

    def inventory(self, repo: Repo, versions=None, **inputs):
        repo.commit()
        argv = ["inventory", "--repo", str(repo.dir), "--versions", repo.input("versions", versions or {})]
        for name, data in inputs.items():
            if name == "terraform":
                argv.append("--terraform")
                continue
            argv += ["--" + name.replace("_", "-"), repo.input(name, data)]
        code, out, err = run(*argv)
        self.assertEqual(0, code, err)
        return json.loads(out)

    @staticmethod
    def pin(data, pid):
        return next(p for p in data["pins"] if p["id"] == pid)


def drop_pin(text: str, pid: str) -> str:
    """The pins file without one PackageVersion, and without the comment that belongs to it."""
    pattern = re.compile(r"(?:[ \t]*<!--(?:(?!-->).)*-->\n)?[ \t]*<PackageVersion Include=\"" + re.escape(pid) + r"\"[^>]*/>\n", re.S)
    out, n = pattern.subn("", text)
    assert n == 1, pid
    return out


class Parsing(Case):
    def test_the_pins_file_as_the_spec_counted_it_yields_20_pins_in_the_labelled_groups(self):
        # #82 counted 20 on 17 October 2026; #206 added AWSSDK.Signin and #336 the DynamoDB and
        # DataProtection.SSM pins after it. The copy without them is
        # the file #82 counted; a pin added later joins this drop list, and the live copy is held to
        # the family table below.
        r = self.repo()
        text = r.read(PROPS)
        for later in ("AWSSDK.Signin", "AWSSDK.DynamoDBv2", "Amazon.AspNetCore.DataProtection.SSM"):
            text = drop_pin(text, later)
        r.write(PROPS, text)
        data = self.inventory(r)
        self.assertEqual(20, len(data["pins"]))
        groups = {}
        for p in data["pins"]:
            groups[p["group"]] = groups.get(p["group"], 0) + 1
        self.assertEqual({"Identity": 3, "Proxy": 1, "AWS": 2, "Telemetry": 5, "Fixtures": 2, "Tests": 7}, groups)

    def test_the_live_pins_file_maps_every_pin_to_a_family(self):
        r = self.repo()
        data = self.inventory(r)
        # Counted from the file itself, so a pin added after this was written is held to the family
        # table rather than breaking a literal. 21 when #269 landed, 23 when #336 did.
        self.assertEqual(len(re.findall(r"<PackageVersion\s", r.read(PROPS))), len(data["pins"]))
        self.assertEqual([], [p["id"] for p in data["pins"] if not p["family"]])
        self.assertEqual(["aspnetcore", "duende", "yarp", "aws-sdk", "opentelemetry", "xunit", "playwright",
                          "schema-validator", "fake-clock", "mvc-testing", "sdk"], [f["id"] for f in data["families"]])

    def test_deleting_one_pin_drops_the_count_and_its_family(self):
        r = self.repo()
        r.write(PROPS, drop_pin(r.read(PROPS), "Yarp.ReverseProxy"))
        data = self.inventory(r)
        self.assertEqual(22, len(data["pins"]))
        self.assertNotIn("yarp", [f["id"] for f in data["families"]])
        self.assertNotIn("Yarp.ReverseProxy", [p["id"] for p in data["pins"]])

    def test_the_reference_map_names_each_referencing_project(self):
        data = self.inventory(self.repo())
        self.assertEqual(["src/Platform.Proxy/Platform.Proxy.csproj"], self.pin(data, "Yarp.ReverseProxy")["referencedBy"])
        self.assertEqual(["src/Platform.Registry/Platform.Registry.csproj", "tools/spactl/spactl.csproj"],
                         self.pin(data, "AWSSDK.S3")["referencedBy"])
        # Directory.Build.props's conditional reference reaches every *Tests project and no other.
        xunit = self.pin(data, "xunit.v3")["referencedBy"]
        tests = [p for p in solution_projects() if pathlib.PurePosixPath(p).stem.endswith("Tests")]
        self.assertEqual(sorted(tests), xunit)
        self.assertNotIn("tests/Gateway.TestHarness/Gateway.TestHarness.csproj", xunit)
        # The trial scope is the test projects that reach a member, through the project graph.
        yarp = next(f for f in data["families"] if f["id"] == "yarp")
        self.assertIn("tests/Platform.Proxy.Tests/Platform.Proxy.Tests.csproj", yarp["trialScope"])
        self.assertIn("tests/Gateway.IntegrationTests/Gateway.IntegrationTests.csproj", yarp["trialScope"])
        self.assertNotIn("tests/Platform.Registry.Tests/Platform.Registry.Tests.csproj", yarp["trialScope"])

    def test_a_versioned_reference_inside_the_solution_is_a_finding_and_a_spike_one_is_not(self):
        r = self.repo()
        clean = self.inventory(r)
        self.assertEqual([], [f for f in clean["findings"] if f["kind"] == "versioned-reference"])
        proj = "src/Platform.Proxy/Platform.Proxy.csproj"
        r.write(proj, r.read(proj).replace('<PackageReference Include="Yarp.ReverseProxy" />',
                                           '<PackageReference Include="Yarp.ReverseProxy" Version="2.2.0" />'))
        r.write("spikes/g0/x/X.csproj", '<Project><ItemGroup><PackageReference Include="Yarp.ReverseProxy" Version="2.3.0" /></ItemGroup></Project>\n')
        data = self.inventory(r)
        self.assertEqual([{"kind": "versioned-reference", "project": proj, "package": "Yarp.ReverseProxy", "version": "2.2.0"}],
                         [f for f in data["findings"] if f["kind"] == "versioned-reference"])
        self.assertIn({"project": "spikes/g0/x/X.csproj", "package": "Yarp.ReverseProxy", "version": "2.3.0"}, data["spikes"])


class Classification(Case):
    LISTS = {
        "AWSSDK.S3": ["4.0.100.1", "4.0.101.6", "4.0.104.4"],
        "OpenTelemetry.Extensions.Hosting": ["1.19.1", "1.20.0"],
        "Yarp.ReverseProxy": ["2.2.0", "2.3.0", "2.4.0-preview.1"],
        "OpenTelemetry.Instrumentation.AspNetCore": ["1.19.0", "1.19.3"],
        "OpenTelemetry.Instrumentation.Http": ["1.19.0", "1.20.0"],
    }

    def test_each_planted_newest_gives_its_status_and_prereleases_are_ignored(self):
        data = self.inventory(self.repo(), self.LISTS)
        self.assertEqual("behind-patch", self.pin(data, "AWSSDK.S3")["status"])
        self.assertEqual("4.0.104.4", self.pin(data, "AWSSDK.S3")["newest"])
        self.assertEqual("behind-minor", self.pin(data, "OpenTelemetry.Extensions.Hosting")["status"])
        self.assertEqual("current", self.pin(data, "Yarp.ReverseProxy")["status"])
        self.assertEqual("2.3.0", self.pin(data, "Yarp.ReverseProxy")["newest"])
        self.assertEqual("not-checked", self.pin(data, "Microsoft.Playwright")["status"])

    def test_swapping_two_lists_flips_two_statuses(self):
        a, b = "OpenTelemetry.Instrumentation.AspNetCore", "OpenTelemetry.Instrumentation.Http"
        before = self.inventory(self.repo(), self.LISTS)
        swapped = dict(self.LISTS, **{a: self.LISTS[b], b: self.LISTS[a]})
        after = self.inventory(self.repo(), swapped)
        self.assertEqual(("behind-patch", "behind-minor"), (self.pin(before, a)["status"], self.pin(before, b)["status"]))
        self.assertEqual(("behind-minor", "behind-patch"), (self.pin(after, a)["status"], self.pin(after, b)["status"]))


class Advisories(Case):
    @staticmethod
    def listing(_=None, with_entry=True):
        def vuln(pid, ver):
            return {"id": pid, "resolvedVersion": ver,
                    "vulnerabilities": [{"severity": "High", "advisoryurl": f"https://github.com/advisories/{GHSA}"}] if with_entry else []}
        return {"version": 1, "projects": [
            {"path": "/w/src/Platform.Proxy/Platform.Proxy.csproj",
             "frameworks": [{"framework": "net10.0", "topLevelPackages": [dict(vuln("Yarp.ReverseProxy", "2.3.0"), requestedVersion="2.3.0")]}]},
            {"path": "/w/src/Platform.Telemetry/Platform.Telemetry.csproj",
             "frameworks": [{"framework": "net10.0", "topLevelPackages": [],
                             "transitivePackages": [dict(vuln("Planted.Transitive", "1.0.0"), vulnerabilities=[
                                 {"severity": "Moderate", "advisoryurl": "https://github.com/advisories/GHSA-aaaa-bbbb-cccc"}] if with_entry else [])]}]},
        ]}

    def test_the_planted_advisory_lands_on_the_right_pin_and_the_right_transitive(self):
        data = self.inventory(self.repo(), {"Yarp.ReverseProxy": ["2.3.0"]}, vulnerable=self.listing(None))
        yarp = self.pin(data, "Yarp.ReverseProxy")
        self.assertEqual("advisory", yarp["status"])
        self.assertEqual([(GHSA, "High", "")], [(a["id"], a["severity"], a["via"]) for a in yarp["advisories"]])
        self.assertEqual(["Planted.Transitive"], [t["id"] for t in data["transitives"]])
        self.assertEqual("GHSA-aaaa-bbbb-cccc", data["transitives"][0]["advisories"][0]["id"])
        # The transitive is reported on the pins its listing project references, and on no other.
        hosting = self.pin(data, "OpenTelemetry.Extensions.Hosting")
        self.assertEqual([("GHSA-aaaa-bbbb-cccc", "Moderate", "Planted.Transitive")], [(a["id"], a["severity"], a["via"]) for a in hosting["advisories"]])
        self.assertEqual([], self.pin(data, "OpenTelemetry.Exporter.InMemory")["advisories"])
        self.assertEqual([], self.pin(data, "AWSSDK.S3")["advisories"])

    def test_removing_the_entry_removes_the_status(self):
        data = self.inventory(self.repo(), {"Yarp.ReverseProxy": ["2.3.0"]}, vulnerable=self.listing(None, False))
        self.assertEqual("current", self.pin(data, "Yarp.ReverseProxy")["status"])
        self.assertEqual([], data["transitives"])

    def test_a_github_advisory_applies_only_inside_its_range(self):
        adv = [{"ghsa_id": GHSA, "severity": "high", "html_url": f"https://github.com/advisories/{GHSA}",
                "vulnerabilities": [{"package": {"ecosystem": "nuget", "name": "Yarp.ReverseProxy"},
                                     "vulnerable_version_range": ">= 2.0.0, < 2.0.1", "first_patched_version": "2.0.1"}]}]
        r = self.repo()
        self.assertEqual("current", self.pin(self.inventory(r, {"Yarp.ReverseProxy": ["2.3.0"]}, advisories=adv), "Yarp.ReverseProxy")["status"])
        r.write(PROPS, r.read(PROPS).replace('"Yarp.ReverseProxy" Version="2.3.0"', '"Yarp.ReverseProxy" Version="2.0.0"'))
        yarp = self.pin(self.inventory(r, {"Yarp.ReverseProxy": ["2.0.0", "2.3.0"]}, advisories=adv), "Yarp.ReverseProxy")
        self.assertEqual("advisory", yarp["status"])
        self.assertEqual("2.0.1", yarp["advisories"][0]["fixed"])

    def test_a_prerelease_bound_orders_below_its_release_and_an_unread_range_holds_the_pin(self):
        # GitHub writes .NET ranges with prerelease bounds; read as unparseable, 2.0.0 inside
        # ">= 2.0.0-preview.1, < 2.0.1" was reported clean (#269's review).
        def adv(spec):
            return [{"ghsa_id": GHSA, "severity": "high", "html_url": f"https://github.com/advisories/{GHSA}",
                     "vulnerabilities": [{"package": {"ecosystem": "nuget", "name": "Yarp.ReverseProxy"},
                                          "vulnerable_version_range": spec, "first_patched_version": "2.0.1"}]}]
        lists = {"Yarp.ReverseProxy": ["2.0.0", "2.3.0"]}
        r = self.repo()
        self.assertEqual("current", self.pin(self.inventory(r, lists, advisories=adv("< 2.0.1-rc.1")), "Yarp.ReverseProxy")["status"])
        self.assertEqual("advisory", self.pin(self.inventory(r, lists, advisories=adv("< 2.3.1-rc.1")), "Yarp.ReverseProxy")["status"])
        self.assertEqual("current", self.pin(self.inventory(r, lists, advisories=adv("<= 2.3.0-rc.1")), "Yarp.ReverseProxy")["status"])
        self.assertEqual("advisory", self.pin(self.inventory(r, lists, advisories=adv("unreadable 2.x")), "Yarp.ReverseProxy")["status"])
        r.write(PROPS, r.read(PROPS).replace('"Yarp.ReverseProxy" Version="2.3.0"', '"Yarp.ReverseProxy" Version="2.0.0"'))
        self.assertEqual("advisory", self.pin(self.inventory(r, lists, advisories=adv(">= 2.0.0-preview.1, < 2.0.1")), "Yarp.ReverseProxy")["status"])


class EvidenceTies(Case):
    def test_a_document_naming_the_pinned_version_is_reported_with_its_line(self):
        r = self.repo()
        r.write("docs/adr/0099-planted.md", "# Planted\n\nG0 item 5 ran Yarp.ReverseProxy\n2.3.0 on this runtime.\n")
        r.write("docs/notes.md", "Line one.\nThe proxy package Yarp.ReverseProxy 2.3.0 is pinned.\n")
        yarp = self.pin(self.inventory(r, {"Yarp.ReverseProxy": ["2.3.0", "2.4.0"]}), "Yarp.ReverseProxy")
        self.assertEqual([("docs/adr/0099-planted.md", 4)], [(d["path"], d["line"]) for d in yarp["evidence"]])
        self.assertIn(("docs/notes.md", 2), [(d["path"], d["line"]) for d in yarp["documents"]])
        self.assertEqual("blocked-by-evidence", yarp["status"])

    def test_a_document_naming_a_different_version_is_not(self):
        r = self.repo()
        r.write("docs/adr/0099-planted.md", "# Planted\n\nG0 item 5 ran Yarp.ReverseProxy 2.3.1 and 12.3.0 on this runtime.\n")
        yarp = self.pin(self.inventory(r, {"Yarp.ReverseProxy": ["2.3.0", "2.4.0"]}), "Yarp.ReverseProxy")
        self.assertEqual([], yarp["documents"])
        self.assertEqual("behind-minor", yarp["status"])


class DecisionHold(Case):
    LISTS = {"JsonSchema.Net": ["8.0.5", "8.0.9", "9.4.1", "10.0.0-beta"]}

    def test_a_comment_naming_a_decision_holds_the_pin(self):
        schema = self.pin(self.inventory(self.repo(), self.LISTS), "JsonSchema.Net")
        self.assertEqual(("held-by-decision", "D-1", "9.4.1", "8.0.9"),
                         (schema["status"], schema["hold"], schema["newest"], schema["newestInBound"]))

    def test_removing_the_comment_gives_behind_minor(self):
        r = self.repo()
        text = r.read(PROPS)
        start = text.index("<!-- Decision D-1")
        end = text.index("-->", start) + len("-->\n")
        r.write(PROPS, text[:start] + text[end:].lstrip(" "))
        schema = self.pin(self.inventory(r, self.LISTS), "JsonSchema.Net")
        self.assertEqual(("behind-minor", ""), (schema["status"], schema["hold"]))


class Rendering(Case):
    def canned(self):
        r = self.repo()
        r.write("docs/adr/0099-planted.md", "Yarp.ReverseProxy 2.3.0 and JsonSchema.Net 8.0.5 ran.\n")
        vul = Advisories.listing(None, True)
        releases = {"latest-sdk": "10.0.402", "latest-runtime": "10.0.13", "latest-release-date": "2026-10-13",
                    "releases": [{"release-version": "10.0.13", "security": True}, {"release-version": "10.0.12", "security": True}]}
        lists = {"Yarp.ReverseProxy": ["2.3.0", "2.4.0"], "JsonSchema.Net": ["8.0.5", "9.4.1"], "AWSSDK.S3": ["4.0.101.6", "4.0.104.4"]}
        data = self.inventory(r, lists, vulnerable=vul, releases=releases)
        return r, r.input("inventory", data), data

    def table(self, path, *extra):
        code, out, err = run("render", "--input", path, *extra)
        self.assertEqual(0, code, err)
        return out

    def test_one_row_per_pin_with_the_precedence_applied(self):
        _, path, data = self.canned()
        out = self.table(path)
        rows = [l for l in out.splitlines() if l.startswith("| ") and not l.startswith("| Pin ") and not l.startswith("| ---")]
        self.assertEqual(len(data["pins"]) + 1, len(rows))
        status = {l.split(" | ")[0][2:]: l.split(" | ")[3] for l in rows}
        # Yarp has an advisory and an evidence tie: the advisory outranks. JsonSchema.Net has a tie and
        # a hold: the tie outranks. AWSSDK.S3 is only behind.
        self.assertEqual("advisory", status["Yarp.ReverseProxy"])
        self.assertEqual("blocked-by-evidence", status["JsonSchema.Net"])
        self.assertEqual("behind-patch", status["AWSSDK.S3"])
        self.assertEqual("advisory", status["SDK (global.json)"])
        self.assertIn("security release 10.0.13", out)
        self.assertIn(GHSA, out)

    def test_a_family_out_of_scope_is_not_checked(self):
        _, path, _ = self.canned()
        out = self.table(path, "--families", "aws-sdk")
        self.assertRegex(out, r"\| AWSSDK\.S3 \| [^|]+ \| [^|]+ \| behind-patch \|")
        self.assertRegex(out, r"\| Yarp\.ReverseProxy \| [^|]+ \| [^|]+ \| not-checked \|")

    def test_the_table_is_byte_stable_across_two_renders(self):
        _, path, _ = self.canned()
        self.assertEqual(self.table(path).encode(), self.table(path).encode())


class Inputs(Case):
    def test_an_unreachable_nuget_halts(self):
        r = self.repo()
        r.commit()
        env = dict(os.environ, HTTPS_PROXY="http://127.0.0.1:9", https_proxy="http://127.0.0.1:9", NO_PROXY="", no_proxy="")
        code, out, err = run("inventory", "--repo", str(r.dir), env=env)
        self.assertEqual(3, code, err)
        self.assertIn("halt: nuget.org unreachable", err)
        self.assertEqual("", out)

    def test_the_sdk_row_reports_a_newer_security_release(self):
        rel = {"latest-sdk": "10.0.402", "latest-runtime": "10.0.13", "latest-release-date": "2026-10-13",
               "releases": [{"release-version": "10.0.13", "security": True}]}
        sdk = self.inventory(self.repo(), releases=rel)["sdk"]
        self.assertEqual(("10.0.100", "latestFeature", "advisory", "10.0.13", "10.0.12"),
                         (sdk["floor"], sdk["rollForward"], sdk["status"], sdk["securityRelease"], sdk["frameworkRuntime"]))
        rel["releases"][0]["security"] = False
        self.assertEqual("behind-patch", self.inventory(self.repo(), releases=rel)["sdk"]["status"])

    def test_terraform_lock_files_are_rows_only_when_asked(self):
        r = self.repo()
        r.write("infra/envs/a/.terraform.lock.hcl", 'provider "registry.terraform.io/hashicorp/aws" {\n  version     = "6.68.0"\n  constraints = "~> 6.0"\n  hashes = []\n}\n')
        self.assertNotIn("terraform", self.inventory(r))
        data = self.inventory(r, provider_versions={"hashicorp/aws": ["6.68.0", "6.69.1"]}, terraform=True)
        rows = data["terraform"]
        self.assertEqual(["infra/envs/a"], next(f for f in data["families"] if f["id"] == "terraform")["trialScope"])
        self.assertEqual([("infra/envs/a/.terraform.lock.hcl", "hashicorp/aws", "6.68.0", "~> 6.0", "6.69.1", "behind-minor")],
                         [(t["lockFile"], t["provider"], t["pinned"], t["constraints"], t["newest"], t["status"]) for t in rows])

    def test_the_families_command_lists_the_inventory_families(self):
        code, out, _ = run("families")
        self.assertEqual(0, code)
        ids = [f["id"] for f in json.loads(out)]
        self.assertEqual(12, len(ids))
        self.assertEqual("terraform", ids[-1])


if __name__ == "__main__":
    unittest.main()
