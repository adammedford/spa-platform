#!/usr/bin/env python3
"""The pins sweep's inventory: every pin, its family, who references it, what names it, its status.

Owner: integrator. Specified in #82 ("the pins inventory script"); built under #269. The pins-sweep
workflow (.claude/workflows/pins-sweep.js) runs it in its Inventory phase and again to render the
standing report issue's table. The split is #82's: this script gathers facts and renders tables,
the workflow's agents read vendors and judge.

    python3 -I tools/pins_inventory.py families
    python3 -I tools/pins_inventory.py inventory --repo <checkout> [--commit <rev>] [inputs...]
    python3 -I tools/pins_inventory.py render --input <inventory.json> [--families a,b]

**families** prints the family table below as JSON, so the workflow can be held to the same ids.

**inventory** reads everything at one commit through git (`git show <rev>:<path>`), never the
working tree, which may be dirty or trail origin/main (docs/agents/review.md, "read the branch,
never the checkout"). It reads:

- the central package file, Directory.Packages.props: each `PackageVersion`, its `ItemGroup`'s
  label, and the comment just above it. A comment naming a decision id (`D-1`) holds the pin to its
  major version: #82's schema validator case, where D-1 refused the 9.x licence;
- the SDK pin file, global.json: the floor and the roll-forward rule;
- the project files SpaPlatform.slnx lists, and Directory.Build.props, whose conditional
  `PackageReference` reaches every project named `*Tests`. A `PackageReference` with a version
  inside the solution is a finding: the central file forbids it. The spike projects sit outside the
  solution and pin their own versions; they are read into `spikes` as part of the evidence record
  and never proposed for a move;
- every tracked Markdown file outside `.scratch/`, for a line that names a pin (by its id or an
  alias below) and its pinned version. Each is a document a move makes stale (#82, user story 8). A
  line under docs/adr/ is also an evidence tie: the decision record citing a run or a constant at
  that version. The spike READMEs and the change requests list their own pins and the solution's
  beside them, so reading them as ties would block the AWS SDK family, which #82 says has none; the
  Check agent reads every document and may confirm a tie the companion did not flag.

Every network input can be supplied from a file, so the tests need no network:

- `--versions F`: {"<package id>": ["<version>", ...]}, nuget.org's version index. Without it each
  list is fetched from the flat container, https://api.nuget.org/v3-flatcontainer/<id>/index.json
  (https://learn.microsoft.com/en-us/nuget/api/package-base-address-resource#enumerate-package-versions);
  if nuget.org cannot be reached the command halts, exit 3, and the sweep stops there (#82).
- `--vulnerable F`: the SDK's vulnerable listing, `dotnet list package --vulnerable
  --include-transitive --format json`, whose shape (projects[].frameworks[].topLevelPackages[] and
  transitivePackages[], each with vulnerabilities[{severity, advisoryurl}]) is documented at
  https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-list-package. An advisory on a pin
  lands on the pin; one on a package no pin holds lands in `transitives`, on the pins the listing
  project references directly, which is the pin that brings it (#82: "the advisory is reported on
  the pin that brings it").
- `--advisories F`: GitHub's advisory database, a list in the shape of `gh api
  /advisories?ecosystem=nuget&affects=<id>@<version>`
  (https://docs.github.com/en/rest/security-advisories/global-advisories): each advisory's
  vulnerabilities name a package and a `vulnerable_version_range`, which is held to the pin here.
- `--releases F`: Microsoft's release index for the SDK's channel,
  https://builds.dotnet.microsoft.com/dotnet/release-metadata/<channel>/releases.json. Without it the
  SDK row is not checked.
- `--terraform` with `--provider-versions F`: {"hashicorp/aws": ["6.68.0", ...]}, the registry's
  version list; each `.terraform.lock.hcl` becomes a row per provider.

A pin's status is #82's precedence, highest first: `advisory`, then, for a pin behind its newest
stable release, `blocked-by-evidence`, `held-by-decision`, `behind-minor` (a minor or major step)
and `behind-patch` (the same major and minor); otherwise `current`. A prerelease is never a newest.
`not-checked` is a pin with no version list, or, in render, a family outside the run's scope.

**render** prints the report table from inventory JSON: one row per pin in family order, then the
SDK row and any Terraform rows. The same JSON renders to the same bytes.

Exit codes: 0 done, 2 bad arguments or unreadable input, 3 nuget.org unreachable (a halt).
"""

from __future__ import annotations

import argparse
import json
import posixpath
import re
import subprocess
import sys
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET

PRECEDENCE = ["advisory", "blocked-by-evidence", "held-by-decision", "behind-minor", "behind-patch",
              "current", "not-checked"]
EVIDENCE_PREFIXES = ("docs/adr/",)

# The families of #82 -> "Families", with each member's aliases: the words prose uses for it beside a
# version ("the handler 10.0.12"). The trial scope is not typed here: it is derived from the
# reference map, the test projects that reach a member. frameworkWide families trial the whole
# solution and the smoke test. AWSSDK.Signin joined the AWS family under #206, and AWSSDK.DynamoDBv2 and
# Amazon.AspNetCore.DataProtection.SSM under #336, after #82 was written.
FAMILIES = [
    {"id": "aspnetcore", "name": "ASP.NET Core and IdentityModel", "frameworkWide": True, "reportOnly": False,
     "members": {"Microsoft.AspNetCore.Authentication.OpenIdConnect": ["OpenIdConnect", "OpenID Connect", "ASP.NET Core", ".NET", "the handler"],
                 "Microsoft.AspNetCore.Authentication.JwtBearer": ["JwtBearer", "JWT bearer"],
                 "Microsoft.IdentityModel.JsonWebTokens": ["JsonWebTokens", "IdentityModel"]}},
    {"id": "duende", "name": "Token management (Duende)", "frameworkWide": False, "reportOnly": False,
     "members": {"Duende.AccessTokenManagement.OpenIdConnect": ["AccessTokenManagement", "token-management", "token management"],
                 "Duende.IdentityModel": ["Duende.IdentityModel"]}},
    {"id": "yarp", "name": "YARP", "frameworkWide": False, "reportOnly": False,
     "members": {"Yarp.ReverseProxy": ["YARP"]}},
    {"id": "aws-sdk", "name": "AWS SDK", "frameworkWide": False, "reportOnly": False,
     "members": {"AWSSDK.S3": [], "AWSSDK.SimpleSystemsManagement": [], "AWSSDK.Signin": [], "AWSSDK.DynamoDBv2": [],
                 "Amazon.AspNetCore.DataProtection.SSM": []}},
    {"id": "opentelemetry", "name": "OpenTelemetry", "frameworkWide": False, "reportOnly": False,
     "members": {"OpenTelemetry.Extensions.Hosting": [], "OpenTelemetry.Instrumentation.AspNetCore": [],
                 "OpenTelemetry.Instrumentation.Http": [], "OpenTelemetry.Exporter.OpenTelemetryProtocol": [],
                 "OpenTelemetry.Exporter.InMemory": []}},
    {"id": "xunit", "name": "The xunit.v3 set", "frameworkWide": True, "reportOnly": False,
     "members": {"xunit.v3": ["xUnit"], "xunit.v3.extensibility.core": [], "xunit.v3.assert": []}},
    {"id": "playwright", "name": "Playwright", "frameworkWide": False, "reportOnly": False,
     "members": {"Microsoft.Playwright": ["Playwright"]}},
    {"id": "schema-validator", "name": "The schema validator", "frameworkWide": False, "reportOnly": False,
     "members": {"JsonSchema.Net": []}},
    {"id": "fake-clock", "name": "The fake clock", "frameworkWide": False, "reportOnly": False,
     "members": {"Microsoft.Extensions.TimeProvider.Testing": ["TimeProvider.Testing", "fake clock"]}},
    {"id": "mvc-testing", "name": "The MVC test host", "frameworkWide": False, "reportOnly": False,
     "members": {"Microsoft.AspNetCore.Mvc.Testing": ["Mvc.Testing"]}},
    {"id": "sdk", "name": "The SDK", "frameworkWide": False, "reportOnly": True, "members": {}},
]
TERRAFORM_FAMILY = {"id": "terraform", "name": "Terraform providers", "frameworkWide": False, "reportOnly": False, "members": {}}
FRAMEWORK_PIN = "Microsoft.AspNetCore.Authentication.OpenIdConnect"
DECISION = re.compile(r"\b(D-\d+)\b")


class Halt(Exception):
    """nuget.org could not be reached: the sweep stops rather than guess (#82, user story 19)."""


class Bad(Exception):
    """An argument or input that cannot be read: nothing was judged."""


# ---------------------------------------------------------------- versions
def parse_version(text: str):
    """A stable version as a tuple of ints, or None for a prerelease or anything unparseable."""
    text = text.strip()
    if "-" in text or "+" in text:
        return None
    parts = text.split(".")
    if not parts or not all(p.isdigit() for p in parts):
        return None
    nums = [int(p) for p in parts]
    while len(nums) < 3:
        nums.append(0)
    return tuple(nums)


def newest_stable(versions, bound_major=None):
    best = None
    best_text = ""
    for v in versions:
        p = parse_version(v)
        if p is None or (bound_major is not None and p[0] != bound_major):
            continue
        if best is None or p > best:
            best, best_text = p, v
    return best_text


def distance(pinned: str, newest: str) -> str:
    a, b = parse_version(pinned), parse_version(newest)
    if a is None or b is None or b <= a:
        return "current"
    return "behind-patch" if a[:2] == b[:2] else "behind-minor"


def range_key(text: str):
    """A version as a range bound orders it: a prerelease sorts below its own release, so a range
    ">= 2.0.0-preview.1, < 2.0.1" holds 2.0.0 and "< 2.0.1-rc.1" does not hold 2.0.1. None when
    unparseable. Prereleases order among themselves by their parts, which a stable pin never needs."""
    core, _, pre = text.strip().split("+", 1)[0].partition("-")
    nums = parse_version(core)
    if nums is None:
        return None
    return (nums, 0, tuple(pre.split("."))) if pre else (nums, 1, ())


def in_range(version: str, spec: str) -> bool:
    """GitHub's vulnerable_version_range: comma-separated clauses such as ">= 2.0.0, < 2.0.1".

    A clause this cannot read counts as holding the pin: the cost of a false advisory is a Check
    agent reading it, the cost of a false clean is a warning nobody sees (CODING_STANDARDS.md, "A
    check fails closed"; #269's review)."""
    v = range_key(version)
    if v is None:
        return True
    for clause in [c.strip() for c in spec.split(",") if c.strip()]:
        m = re.match(r"^(>=|<=|>|<|=)?\s*(\S+)$", clause)
        if not m:
            return True
        op, other = m.group(1) or "=", range_key(m.group(2))
        if other is None:
            return True
        ok = {">=": v >= other, "<=": v <= other, ">": v > other, "<": v < other, "=": v == other}[op]
        if not ok:
            return False
    return True


# ---------------------------------------------------------------- reading at a commit
class Tree:
    def __init__(self, repo: str, commit: str):
        self.repo = repo
        done = subprocess.run(["git", "-C", repo, "rev-parse", "--verify", commit + "^{commit}"],
                              capture_output=True, text=True)
        if done.returncode != 0:
            raise Bad(f"{commit} is not a commit in {repo}: {done.stderr.strip()}")
        self.commit = done.stdout.strip()
        listing = subprocess.run(["git", "-C", repo, "ls-tree", "-r", "-z", "--name-only", self.commit],
                                 capture_output=True)
        if listing.returncode != 0:
            raise Bad(f"git ls-tree failed in {repo}")
        self.paths = [p for p in listing.stdout.decode("utf-8", "surrogateescape").split("\0") if p]
        self._set = set(self.paths)
        self._read = {}

    def has(self, path: str) -> bool:
        return path in self._set

    def read(self, path: str) -> str:
        # Kept per path: mentions() reads every tracked Markdown file once per pin, which was 21 x 209
        # git processes and 14 seconds at #269's fork; a commit's blob never changes.
        if path not in self._read:
            done = subprocess.run(["git", "-C", self.repo, "show", f"{self.commit}:{path}"], capture_output=True)
            if done.returncode != 0:
                raise Bad(f"cannot read {path} at {self.commit}")
            self._read[path] = done.stdout.decode("utf-8", "replace")
        return self._read[path]


def local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def parse_pins(text: str):
    """Each PackageVersion with its group label and the comment just above it, in file order."""
    pins = []
    # ElementTree drops comments by default; a TreeBuilder that keeps them sees the file as written.
    parser = ET.XMLParser(target=ET.TreeBuilder(insert_comments=True))
    try:
        root = ET.fromstring(text, parser=parser)
    except ET.ParseError as e:
        raise Bad(f"Directory.Packages.props does not parse: {e}")
    for group in root:
        if not isinstance(group.tag, str) or local(group.tag) != "ItemGroup":
            continue
        label = group.get("Label", "")
        comment = ""
        for el in group:
            if el.tag is ET.Comment:
                comment = (el.text or "").strip()
                continue
            if isinstance(el.tag, str) and local(el.tag) == "PackageVersion":
                pid, ver = el.get("Include", ""), el.get("Version", "")
                m = DECISION.search(comment)
                pins.append({"id": pid, "pinned": ver, "group": label, "comment": " ".join(comment.split()),
                             "hold": m.group(1) if m else ""})
            comment = ""
    return pins


def solution_projects(tree: Tree):
    if not tree.has("SpaPlatform.slnx"):
        return []
    root = ET.fromstring(tree.read("SpaPlatform.slnx"))
    return sorted({el.get("Path") for el in root.iter() if local(el.tag) == "Project" and el.get("Path")})


def references(text: str, path: str):
    """A project file's package references (id, version or "", condition) and project references."""
    root = ET.fromstring(text)
    pkgs, projs = [], []
    for group in root.iter():
        if local(group.tag) != "ItemGroup":
            continue
        cond = group.get("Condition", "")
        for el in group:
            if not isinstance(el.tag, str):
                continue
            if local(el.tag) == "PackageReference" and el.get("Include"):
                ver = el.get("Version") or ""
                for child in el:
                    if isinstance(child.tag, str) and local(child.tag) == "Version":
                        ver = (child.text or "").strip()
                pkgs.append((el.get("Include"), ver, cond))
            if local(el.tag) == "ProjectReference" and el.get("Include"):
                target = posixpath.normpath(posixpath.join(posixpath.dirname(path), el.get("Include").replace("\\", "/")))
                projs.append(target)
    return pkgs, projs


def project_name(path: str) -> str:
    return posixpath.basename(path).rsplit(".", 1)[0]


def is_test(path: str) -> bool:
    return project_name(path).endswith("Tests")


def reference_map(tree: Tree):
    """{project: [package ids]} for the solution, the project graph, and the findings."""
    projects = solution_projects(tree)
    direct, graph, findings = {}, {}, []
    props = []
    if tree.has("Directory.Build.props"):
        props = references(tree.read("Directory.Build.props"), "Directory.Build.props")[0]
    for p in projects:
        if not tree.has(p):
            findings.append({"kind": "missing-project", "project": p})
            continue
        pkgs, projs = references(tree.read(p), p)
        ids = []
        for pid, ver, _ in pkgs:
            ids.append(pid)
            if ver:
                findings.append({"kind": "versioned-reference", "project": p, "package": pid, "version": ver})
        for pid, _, cond in props:
            # Directory.Build.props's test-project reference: its condition names the *Tests rule.
            if not cond or not ("Tests" in cond or "SpaTestProject" in cond) or is_test(p):
                ids.append(pid)
        direct[p] = sorted(set(ids))
        graph[p] = projs
    return direct, graph, findings


def spikes(tree: Tree):
    out = []
    for p in tree.paths:
        if p.startswith("spikes/") and p.endswith(".csproj"):
            try:
                pkgs = references(tree.read(p), p)[0]
            except ET.ParseError:
                continue
            out += [{"project": p, "package": pid, "version": ver} for pid, ver, _ in pkgs if ver]
    return out


def reaches(graph, start, targets):
    seen, stack = set(), [start]
    while stack:
        p = stack.pop()
        if p in targets:
            return True
        if p in seen:
            continue
        seen.add(p)
        stack += graph.get(p, [])
    return False


def mentions(tree: Tree, pin, aliases):
    """Lines of tracked Markdown naming the pin, by id or alias, beside its pinned version."""
    out = []
    ver = re.compile(r"(?<![\w.])" + re.escape(pin["pinned"]) + r"(?![\w]|\.\d)")
    names = [pin["id"]] + list(aliases)
    for path in tree.paths:
        if not path.endswith(".md") or path.startswith(".scratch/"):
            continue
        text = tree.read(path)
        if pin["pinned"] not in text:
            continue
        lines = text.split("\n")
        for n, line in enumerate(lines, 1):
            # Prose wraps near column 100, so a name ends one line and its version starts the next
            # (ADR-0017: "Playwright" / "1.63.0 with Chromium 1243"): the line before counts too.
            near = (lines[n - 2] + " " + line) if n > 1 else line
            if ver.search(line) and any(a.lower() in near.lower() for a in names):
                out.append({"path": path, "line": n, "text": line.strip()[:200]})
    return out


# ---------------------------------------------------------------- inputs
def load_json(path):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError) as e:
        raise Bad(f"cannot read {path}: {e}")


def fetch_versions(pid: str):
    url = f"https://api.nuget.org/v3-flatcontainer/{pid.lower()}/index.json"
    try:
        with urllib.request.urlopen(url, timeout=30) as r:
            return json.load(r).get("versions", [])
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return []
        raise Halt(f"nuget.org answered {e.code} for {url}")
    except (urllib.error.URLError, OSError, ValueError) as e:
        raise Halt(f"nuget.org unreachable: {url}: {e}")


def advisory_id(url: str) -> str:
    return url.rstrip("/").rsplit("/", 1)[-1] if url else ""


def vulnerable(listing, pins, direct):
    """Advisories from the SDK's vulnerable listing: (per pin id, transitives)."""
    on_pin, trans = {}, {}
    ids = {p["id"].lower(): p["id"] for p in pins}
    by_name = {project_name(p): p for p in direct}
    for proj in listing.get("projects", []):
        pname = project_name(str(proj.get("path", "")).replace("\\", "/"))
        for fw in proj.get("frameworks", []):
            for kind in ("topLevelPackages", "transitivePackages"):
                for pkg in fw.get(kind, []):
                    vulns = pkg.get("vulnerabilities") or []
                    if not vulns:
                        continue
                    pid = pkg.get("id", "")
                    found = [{"id": advisory_id(v.get("advisoryurl", "")), "severity": v.get("severity", ""),
                              "url": v.get("advisoryurl", ""), "fixed": "", "via": ""} for v in vulns]
                    if pid.lower() in ids:
                        on_pin.setdefault(ids[pid.lower()], []).extend(found)
                        continue
                    t = trans.setdefault(pid, {"id": pid, "version": pkg.get("resolvedVersion", ""), "advisories": [], "pins": set()})
                    for a in found:
                        if a not in t["advisories"]:
                            t["advisories"].append(a)
                    for pin_id in direct.get(by_name.get(pname, ""), []):
                        if pin_id.lower() in ids:
                            t["pins"].add(ids[pin_id.lower()])
    for pid, t in trans.items():
        for pin_id in t["pins"]:
            for a in t["advisories"]:
                on_pin.setdefault(pin_id, []).append(dict(a, via=pid))
    out_trans = [dict(t, pins=sorted(t["pins"])) for _, t in sorted(trans.items())]
    return on_pin, out_trans


def github_advisories(listing, pins):
    on_pin = {}
    for adv in listing or []:
        for v in adv.get("vulnerabilities") or []:
            name = ((v.get("package") or {}).get("name") or "")
            for p in pins:
                if p["id"].lower() == name.lower() and in_range(p["pinned"], v.get("vulnerable_version_range") or ""):
                    on_pin.setdefault(p["id"], []).append({
                        "id": adv.get("ghsa_id", ""), "severity": adv.get("severity", ""), "url": adv.get("html_url", ""),
                        "fixed": v.get("first_patched_version") or "", "via": ""})
    return on_pin


def classify(pin):
    if pin["advisories"]:
        return "advisory"
    if not pin["newest"]:
        return "not-checked"
    d = distance(pin["pinned"], pin["newest"])
    if d == "current":
        return "current"
    if pin["evidence"]:
        return "blocked-by-evidence"
    if pin["hold"]:
        return "held-by-decision"
    return d


def lock_files(tree: Tree, provider_versions):
    rows = []
    block = re.compile(r'provider\s+"([^"]+)"\s*\{(.*?)\n\}', re.S)
    for p in tree.paths:
        if not p.endswith(".terraform.lock.hcl"):
            continue
        for m in block.finditer(tree.read(p)):
            source = m.group(1)
            short = "/".join(source.split("/")[-2:])
            ver = re.search(r'\bversion\s*=\s*"([^"]+)"', m.group(2))
            cons = re.search(r'\bconstraints\s*=\s*"([^"]+)"', m.group(2))
            pinned = ver.group(1) if ver else ""
            newest = newest_stable(provider_versions.get(short, []))
            rows.append({"lockFile": p, "provider": short, "pinned": pinned, "constraints": cons.group(1) if cons else "",
                         "newest": newest, "status": distance(pinned, newest) if newest else "not-checked"})
    return rows


def sdk_row(tree: Tree, releases, framework):
    floor, roll = "", ""
    if tree.has("global.json"):
        sdk = json.loads(tree.read("global.json")).get("sdk", {})
        floor, roll = sdk.get("version", ""), sdk.get("rollForward", "")
    p = parse_version(floor)
    row = {"floor": floor, "rollForward": roll, "channel": f"{p[0]}.{p[1]}" if p else "", "frameworkRuntime": framework,
           "latestSdk": "", "latestRuntime": "", "releaseDate": "", "securityRelease": "", "status": "not-checked"}
    if releases is None:
        return row
    row.update(latestSdk=releases.get("latest-sdk", ""), latestRuntime=releases.get("latest-runtime", ""),
               releaseDate=releases.get("latest-release-date", ""))
    security = [r.get("release-version", "") for r in releases.get("releases", [])
                if r.get("security") and parse_version(r.get("release-version", "")) and parse_version(framework)
                and parse_version(r["release-version"]) > parse_version(framework)]
    if security:
        row["securityRelease"] = max(security, key=parse_version)
        row["status"] = "advisory"
    else:
        row["status"] = distance(framework, row["latestRuntime"]) if row["latestRuntime"] else "not-checked"
    return row


def inventory(a):
    tree = Tree(a.repo, a.commit)
    if not tree.has("Directory.Packages.props"):
        raise Bad("no Directory.Packages.props at " + tree.commit)
    pins = parse_pins(tree.read("Directory.Packages.props"))
    direct, graph, findings = reference_map(tree)
    versions = load_json(a.versions) if a.versions else None
    vul = load_json(a.vulnerable) if a.vulnerable else {"projects": []}
    gh = load_json(a.advisories) if a.advisories else []
    releases = load_json(a.releases) if a.releases else None
    on_pin, transitives = vulnerable(vul, pins, direct)
    for k, v in github_advisories(gh, pins).items():
        on_pin.setdefault(k, []).extend(v)
    family_of, aliases = {}, {}
    for f in FAMILIES:
        for m, al in f["members"].items():
            family_of[m], aliases[m] = f["id"], al
    for pin in pins:
        pin["family"] = family_of.get(pin["id"], "")
        if not pin["family"]:
            findings.append({"kind": "no-family", "package": pin["id"]})
        pin["referencedBy"] = sorted(p for p, ids in direct.items() if pin["id"] in ids)
        if not pin["referencedBy"]:
            findings.append({"kind": "unreferenced", "package": pin["id"]})
        listed = versions.get(pin["id"], None) if versions is not None else fetch_versions(pin["id"])
        pin["versions"] = len(listed or [])
        pin["newest"] = newest_stable(listed or [])
        bound = parse_version(pin["pinned"])
        pin["newestInBound"] = newest_stable(listed or [], bound[0]) if pin["hold"] and bound else ""
        pin["documents"] = mentions(tree, pin, aliases.get(pin["id"], []))
        pin["evidence"] = [d for d in pin["documents"] if d["path"].startswith(EVIDENCE_PREFIXES)]
        seen, advs = set(), []
        for adv in on_pin.get(pin["id"], []):
            key = (adv["id"], adv["via"])
            if key not in seen:
                seen.add(key)
                advs.append(adv)
        pin["advisories"] = advs
        pin["status"] = classify(pin)
    families = []
    tests = [p for p in direct if is_test(p)]
    framework = next((p["pinned"] for p in pins if p["id"] == FRAMEWORK_PIN), "")
    table = FAMILIES + ([TERRAFORM_FAMILY] if a.terraform else [])
    for f in table:
        members = [p for p in pins if p["family"] == f["id"]]
        if f["members"] and not members:
            continue
        refs = {r for p in members for r in p["referencedBy"]}
        scope = ["solution"] if f["frameworkWide"] else sorted(t for t in tests if reaches(graph, t, refs))
        statuses = [p["status"] for p in members]
        families.append({"id": f["id"], "name": f["name"], "members": [p["id"] for p in members],
                         "reportOnly": f["reportOnly"], "frameworkWide": f["frameworkWide"],
                         "trialScope": scope, "smoke": f["frameworkWide"],
                         "status": min(statuses, key=PRECEDENCE.index) if statuses else "not-checked"})
    out = {"commit": tree.commit, "pins": pins, "families": families, "transitives": transitives,
           "findings": findings, "spikes": spikes(tree), "sdk": sdk_row(tree, releases, framework)}
    sdk_family = next(f for f in families if f["id"] == "sdk")
    sdk_family["status"] = out["sdk"]["status"]
    if a.terraform:
        out["terraform"] = lock_files(tree, load_json(a.provider_versions) if a.provider_versions else {})
        tf = next(f for f in families if f["id"] == "terraform")
        tf["status"] = min([r["status"] for r in out["terraform"]] or ["not-checked"], key=PRECEDENCE.index)
        # Its trial scope is the directories holding a lock file: each is upgraded, validated and tested.
        tf["trialScope"] = sorted({posixpath.dirname(r["lockFile"]) for r in out["terraform"]})
    return out


# ---------------------------------------------------------------- render
def cell(text) -> str:
    return str(text).replace("|", "\\|").replace("\n", " ")


def why(pin) -> str:
    parts = []
    for adv in pin.get("advisories", []):
        parts.append(f"{adv['id']} ({adv['severity'].lower()}){' via ' + adv['via'] if adv.get('via') else ''}"
                     f"{', fixed in ' + adv['fixed'] if adv.get('fixed') else ''}")
    ev = pin.get("evidence", [])
    if ev:
        parts.append("evidence: " + ", ".join(f"{d['path']}:{d['line']}" for d in ev[:3]) + (f" and {len(ev) - 3} more" if len(ev) > 3 else ""))
    if pin.get("hold"):
        parts.append(f"held by {pin['hold']}" + (f"; newest in bound {pin['newestInBound']}" if pin.get("newestInBound") else ""))
    return "; ".join(parts)


def render(data, scope=None) -> str:
    order = [f["id"] for f in FAMILIES] + ["terraform"]
    rows = ["| Pin | Pinned | Newest | Status | Referencing projects | Why |", "| --- | --- | --- | --- | --- | --- |"]
    pins = sorted(data.get("pins", []), key=lambda p: (order.index(p["family"]) if p.get("family") in order else len(order), p["id"].lower()))
    for p in pins:
        status = p["status"] if scope is None or p.get("family") in scope else "not-checked"
        refs = ", ".join(project_name(r) for r in p.get("referencedBy", []))
        rows.append(f"| {cell(p['id'])} | {cell(p['pinned'])} | {cell(p.get('newest') or '?')} | {status} | {cell(refs)} | {cell(why(p))} |")
    s = data.get("sdk") or {}
    if s:
        status = s.get("status", "not-checked") if scope is None or "sdk" in scope else "not-checked"
        newest = f"SDK {s.get('latestSdk') or '?'}, runtime {s.get('latestRuntime') or '?'}"
        note = f"framework pins track {s.get('frameworkRuntime')}"
        if s.get("securityRelease"):
            note = f"security release {s['securityRelease']}; " + note
        rows.append(f"| SDK (global.json) | {cell(s.get('floor'))} ({cell(s.get('rollForward'))}) | {cell(newest)} | {status} | every project | {cell(note)} |")
    for t in data.get("terraform", []) or []:
        status = t["status"] if scope is None or "terraform" in scope else "not-checked"
        rows.append(f"| {cell(t['provider'])} ({cell(t['lockFile'])}) | {cell(t['pinned'])} | {cell(t.get('newest') or '?')} | {status} | {cell(posixpath.dirname(t['lockFile']))} | {cell('constraint ' + t.get('constraints', ''))} |")
    return "\n".join(rows) + "\n"


def main(argv) -> int:
    ap = argparse.ArgumentParser(prog="pins_inventory.py")
    sub = ap.add_subparsers(dest="command", required=True)
    sub.add_parser("families")
    inv = sub.add_parser("inventory")
    inv.add_argument("--repo", required=True)
    inv.add_argument("--commit", default="HEAD")
    for name in ("versions", "vulnerable", "advisories", "releases", "provider-versions"):
        inv.add_argument("--" + name)
    inv.add_argument("--terraform", action="store_true")
    ren = sub.add_parser("render")
    ren.add_argument("--input", required=True)
    ren.add_argument("--families")
    a = ap.parse_args(argv)
    try:
        if a.command == "families":
            print(json.dumps([{k: v for k, v in f.items()} for f in FAMILIES + [TERRAFORM_FAMILY]], indent=2))
        elif a.command == "inventory":
            print(json.dumps(inventory(a), indent=2, sort_keys=True))
        else:
            scope = set(a.families.split(",")) if a.families else None
            sys.stdout.write(render(load_json(a.input), scope))
        return 0
    except Halt as e:
        print(f"pins-inventory: halt: {e}", file=sys.stderr)
        return 3
    except (Bad, ET.ParseError, ValueError) as e:
        print(f"pins-inventory: error: {e}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
