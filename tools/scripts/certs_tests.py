#!/usr/bin/env python3
"""Test companion for tools/scripts/certs.sh. Owner: workstream 1.

#241, from #118's Q8, accepted as #129's Q33: tools/preflight.sh passes on the profile's Region
alone, and in that shell certs.sh used to import the wildcard certificate into us-east-1, its own
fallback, where the platform root never looks and only teardown's proof would find it afterwards.
certs.sh now resolves its Region the way preflight.sh resolves the Region a shell will build in
(AWS_REGION, then AWS_DEFAULT_REGION, then the profile's configured Region), with --region over all
three, and refuses with a message when none of them names one.

These cases hold it to that, in D-3's shape (docs/dependencies.md): Python's unittest, running the
real script under /bin/bash, the bash 3.2 macOS ships. SCRIPT_TESTS_REQUIRE_BASH fails the run when
/bin/bash is another version.

**A constructed environment, inheriting nothing.** PATH is the case's bin directory alone: a
one-line exec of each real tool the script uses, and a double for aws. openssl is the real one,
named by OPENSSL, because the import checks the key and the certificate it issued; the authority and
certificate are issued once, by the script, and copied into each case. No case can reach AWS.

**The aws double** answers the calls the import, the fetch and the status make, records every call
in order with the Region it named, and exits 97 on a call shape it does not answer, so a new call in
the script turns a case red instead of being answered. `aws configure get region` prints the case's
profile Region, or exits 1 with nothing on stdout, as the real CLI does when the profile names none.

**A universal held on every run:** nothing the script prints holds private key material.

Run: python3 -I tools/scripts/certs_tests.py
"""

from __future__ import annotations

import json
import os
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent.parent
SCRIPT = ROOT / "tools" / "scripts" / "certs.sh"
BASH = "/bin/bash"
REQUIRE_BASH = "SCRIPT_TESTS_REQUIRE_BASH"

CELL = "poc"
DOMAIN = "spa-poc.test"
WILDCARD = f"*.{DOMAIN}"
ACCOUNT = "111122223333"
# The fallback the script used to take. No case's world names it, so a call that carries it is the
# fallback back.
FALLBACK = "us-east-1"

# The real tools the script uses. Everything else is absent, git included, so the case's --dir is
# what names the certificates directory.
TOOLS = ("basename", "dirname", "sed", "tr", "cat", "cp", "grep", "mkdir", "chmod", "mktemp", "rm",
         "uname", "sleep")


def arn(region: str, number: int = 1) -> str:
    return f"arn:aws:acm:{region}:{ACCOUNT}:certificate/0000000{number}-1111-2222-3333-444455556666"


# --- the aws double --------------------------------------------------------------------------------

class Unexpected(Exception):
    pass


def parse(argv: list[str]) -> tuple[str, str, dict]:
    """service, command, and the options as name -> value (a list for --tags)."""
    if len(argv) < 2:
        raise Unexpected(f"no service and command in {argv}")
    service, command, rest = argv[0], argv[1], argv[2:]
    options: dict = {}
    position = []
    i = 0
    while i < len(rest):
        token = rest[i]
        if token.startswith("--"):
            name = token[2:]
            values = []
            while i + 1 < len(rest) and not rest[i + 1].startswith("--"):
                values.append(rest[i + 1])
                i += 1
            options[name] = values if name == "tags" else (values[0] if values else True)
        else:
            position.append(token)
        i += 1
    if position:
        options["_"] = position
    return service, command, options


def answer(world: dict, service: str, command: str, options: dict) -> str:
    region = options.get("region")
    certificates = world["certificates"]
    if service == "configure" and command == "get":
        if options.get("_") != ["region"]:
            raise Unexpected(f"configure get {options.get('_')}")
        if not world.get("profile_region"):
            raise SystemExit(1)
        return world["profile_region"] + "\n"
    if service == "sts" and command == "get-caller-identity":
        return json.dumps({"Account": ACCOUNT, "UserId": "AIDAEXAMPLE", "Arn": "arn:aws:iam::x"}) + "\n"
    if service != "acm":
        raise Unexpected(f"{service} {command}")
    if not region:
        raise Unexpected(f"acm {command} without --region")
    here = [entry for entry in certificates if entry["region"] == region]
    if command == "list-certificates":
        return "\t".join(entry["arn"] for entry in here) + "\n"
    if command == "list-tags-for-certificate":
        entry = next((e for e in here if e["arn"] == options.get("certificate-arn")), None)
        return (CELL if entry and entry.get("cell") == CELL else "None") + "\n"
    if command == "import-certificate":
        existing = options.get("certificate-arn")
        if existing:
            return existing + "\n"
        new = arn(region, len(certificates) + 1)
        certificates.append({"region": region, "arn": new, "cell": None})
        return new + "\n"
    if command == "add-tags-to-certificate":
        for entry in here:
            if entry["arn"] == options.get("certificate-arn"):
                entry["cell"] = CELL
        return ""
    if command == "get-certificate":
        field = {"CertificateChain": "chain", "Certificate": "leaf"}.get(options.get("query"))
        if field is None:
            raise Unexpected(f"acm get-certificate --query {options.get('query')}")
        return world[field]
    if command == "describe-certificate":
        return "IMPORTED\tISSUED\t2027-11-21T00:00:00+00:00\tNone\n"
    raise Unexpected(f"acm {command}")


def aws_double(argv: list[str], case: pathlib.Path) -> int:
    world = json.loads((case / "world.json").read_text())
    record = {"argv": argv, "env_region": os.environ.get("AWS_REGION"),
              "env_default_region": os.environ.get("AWS_DEFAULT_REGION")}
    status = 0
    try:
        service, command, options = parse(argv)
        record.update(service=service, command=command, region=options.get("region"))
        sys.stdout.write(answer(world, service, command, options))
        record["outcome"] = "success"
    except SystemExit as exit_:
        record["outcome"] = f"exit {exit_.code}"
        status = int(exit_.code)
    except Unexpected as error:
        record["outcome"] = "unexpected"
        record["unexpected"] = str(error)
        sys.stderr.write(f"aws double: unexpected call: {error}\n")
        status = 97
    (case / "world.json").write_text(json.dumps(world, indent=1))
    with open(case / "aws-calls.jsonl", "a") as log:
        log.write(json.dumps(record) + "\n")
    return status


if __name__ == "__main__" and sys.argv[1:2] == ["--double"]:
    sys.exit(aws_double(sys.argv[4:], pathlib.Path(sys.argv[3])))

import shlex  # noqa: E402 -- below the double, which starts once per call
import shutil  # noqa: E402
import subprocess  # noqa: E402
import tempfile  # noqa: E402
import unittest  # noqa: E402


def real_openssl() -> str | None:
    found = shutil.which("openssl")
    return os.path.realpath(found) if found else None


# --- a case ----------------------------------------------------------------------------------------

class Result:
    def __init__(self, completed: subprocess.CompletedProcess):
        self.status = completed.returncode
        self.stdout = completed.stdout
        self.stderr = completed.stderr
        self.output = completed.stdout + completed.stderr


ISSUED: pathlib.Path | None = None
_ISSUED_DIR: tempfile.TemporaryDirectory | None = None


class Case:
    """One throwaway directory: a bin holding the aws double and a way to each real tool, which is
    the case's whole PATH, the case's world beside it, and a certificates directory."""

    def __init__(self, test: unittest.TestCase, *, profile_region: str | None = None,
                 certificates: list[dict] | None = None, issued: bool = True):
        self.test = test
        scratch = tempfile.TemporaryDirectory()
        test.addCleanup(scratch.cleanup)
        self.dir = pathlib.Path(scratch.name).resolve()
        self.bin = self.dir / "bin"
        self.home = self.dir / "home"
        self.certs = self.dir / "certs"
        for directory in (self.bin, self.home):
            directory.mkdir()
        for tool in TOOLS:
            found = shutil.which(tool)
            test.assertIsNotNone(found, f"{tool} is required to run this companion and is not on PATH")
            self.write_executable(tool, f'#!/bin/sh\nexec {shlex.quote(os.path.realpath(found))} "$@"\n')
        self.write_executable("aws", (
            f"#!/bin/sh\nexec {shlex.quote(os.path.realpath(sys.executable))} -I -S "
            f"{shlex.quote(str(pathlib.Path(__file__).resolve()))} --double aws "
            f"{shlex.quote(str(self.dir))} \"$@\"\n"))
        if issued:
            shutil.copytree(str(ISSUED), str(self.certs))
        self.world = {"profile_region": profile_region, "certificates": certificates or [],
                      "chain": (ISSUED / f"spa-platform-{CELL}-ca.pem").read_text(),
                      "leaf": (ISSUED / f"spa-platform-{CELL}-wildcard.pem").read_text()}
        (self.dir / "world.json").write_text(json.dumps(self.world, indent=1))

    def write_executable(self, name: str, content: str):
        path = self.bin / name
        path.write_text(content)
        path.chmod(0o755)

    def env(self, **extra: str | None) -> dict[str, str]:
        """No AWS_REGION and no AWS_DEFAULT_REGION unless the case names one."""
        env = {"PATH": str(self.bin), "HOME": str(self.home), "LC_ALL": "C", "TMPDIR": str(self.dir),
               "AWS_PROFILE": "spa-platform", "OPENSSL": real_openssl()}
        env.update(extra)
        return {name: value for name, value in env.items() if value is not None}

    def run(self, *args: str, env: dict[str, str] | None = None) -> Result:
        completed = subprocess.run([BASH, str(SCRIPT), *args, "--dir", str(self.certs)], cwd=self.dir,
                                   env=env or self.env(), stdin=subprocess.DEVNULL, capture_output=True,
                                   text=True, timeout=120)
        result = Result(completed)
        self.test.assertNotIn("PRIVATE KEY", result.output, "the script printed private key material")
        return result

    def aws_calls(self) -> list[dict]:
        path = self.dir / "aws-calls.jsonl"
        return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []

    def acm_calls(self) -> list[dict]:
        return [call for call in self.aws_calls() if call.get("service") == "acm"]

    def acm_regions(self) -> set:
        return {call.get("region") for call in self.acm_calls()}


def setUpModule():
    global ISSUED, _ISSUED_DIR
    full = subprocess.run([BASH, "-c", 'printf "%s" "$BASH_VERSION"'], env={"PATH": "/usr/bin:/bin"},
                          capture_output=True, text=True, check=True).stdout
    print(f"certs_tests: the script runs under {BASH} {full}", file=sys.stderr)
    openssl = real_openssl()
    if openssl is None:
        raise RuntimeError("openssl is required to run this companion and is not on PATH")
    # The authority and the certificate, issued once by the script itself: `issue` touches no AWS
    # and needs no Region.
    _ISSUED_DIR = tempfile.TemporaryDirectory()
    ISSUED = pathlib.Path(_ISSUED_DIR.name).resolve() / "certs"
    bin_dir = pathlib.Path(_ISSUED_DIR.name).resolve() / "bin"
    bin_dir.mkdir()
    for tool in TOOLS:
        found = shutil.which(tool)
        if found is None:
            raise RuntimeError(f"{tool} is required to run this companion and is not on PATH")
        (bin_dir / tool).write_text(f'#!/bin/sh\nexec {shlex.quote(os.path.realpath(found))} "$@"\n')
        (bin_dir / tool).chmod(0o755)
    completed = subprocess.run(
        [BASH, str(SCRIPT), "issue", "--dir", str(ISSUED)],
        env={"PATH": str(bin_dir), "HOME": _ISSUED_DIR.name, "LC_ALL": "C", "TMPDIR": _ISSUED_DIR.name,
             "OPENSSL": openssl},
        stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=300)
    if completed.returncode != 0:
        raise RuntimeError(f"certs.sh issue failed:\n{completed.stdout}{completed.stderr}")


def tearDownModule():
    if _ISSUED_DIR is not None:
        _ISSUED_DIR.cleanup()


class CompanionTestCase(unittest.TestCase):
    def assertNoUnexpectedCall(self, case: Case):
        unexpected = [call for call in case.aws_calls() if call.get("outcome") == "unexpected"]
        self.assertEqual([], unexpected, "the script made an AWS call the double does not answer")

    def assertRefusedForNoRegion(self, case: Case, result: Result):
        self.assertEqual(1, result.status, result.output)
        self.assertIn("no Region", result.stderr)
        # The message names where a Region comes from, so the operator can give one.
        for source in ("AWS_REGION", "AWS_DEFAULT_REGION", "--region", "tools/aws-project.env"):
            self.assertIn(source, result.stderr)
        self.assertEqual([], case.acm_calls(), "the script reached ACM with no Region")
        self.assertNoUnexpectedCall(case)


# --- the refusal -----------------------------------------------------------------------------------

class RefusesRatherThanFallBackToUsEast1(CompanionTestCase):
    """The case that fails when the fallback returns: no Region anywhere, and nothing reaches ACM."""

    def test_import_with_no_region_anywhere_refuses_and_reaches_no_acm(self):
        case = Case(self)
        result = case.run("import")
        self.assertRefusedForNoRegion(case, result)
        self.assertNotIn(FALLBACK, {call.get("region") for call in case.aws_calls()})
        self.assertFalse((case.certs / f"spa-platform-{CELL}-wildcard.acm-arn").exists(),
                         "the script recorded an import it should have refused")

    def test_fetch_ca_with_no_region_anywhere_refuses_and_reaches_no_acm(self):
        case = Case(self, issued=False)
        result = case.run("fetch-ca")
        self.assertRefusedForNoRegion(case, result)

    def test_all_refuses_before_it_issues_anything(self):
        # `all` issues and trusts before it imports. Refusing at the import would leave a new
        # authority on disk, and a trust prompt answered, for nothing.
        case = Case(self, issued=False)
        result = case.run("all")
        self.assertRefusedForNoRegion(case, result)
        self.assertFalse(case.certs.exists() and any(case.certs.iterdir()),
                         "the script issued before it found it had no Region")

    def test_empty_region_variables_count_as_unset(self):
        case = Case(self)
        result = case.run("import", env=case.env(AWS_REGION="", AWS_DEFAULT_REGION=""))
        self.assertRefusedForNoRegion(case, result)

    def test_an_empty_region_flag_is_refused(self):
        case = Case(self, profile_region="us-east-2")
        result = case.run("import", "--region", "")
        self.assertEqual(1, result.status, result.output)
        self.assertIn("--region", result.stderr)
        self.assertEqual([], case.acm_calls())

    def test_help_names_no_fallback(self):
        case = Case(self, issued=False)
        result = case.run("--help")
        self.assertEqual(0, result.status, result.output)
        self.assertNotIn(FALLBACK, result.output)
        self.assertIn("AWS_DEFAULT_REGION", result.output)


# --- the resolution, as tools/preflight.sh's ---------------------------------------------------------

class ResolvesTheRegionAsPreflightDoes(CompanionTestCase):
    """AWS_REGION, then AWS_DEFAULT_REGION, then the profile's configured Region: what preflight.sh
    calls the effective Region, which the AWS CLI and the Terraform provider both use. --region,
    given, is over all three."""

    def imported_into(self, case: Case, result: Result) -> set:
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        self.assertIn("import-certificate", [call["command"] for call in case.acm_calls()])
        return case.acm_regions()

    def test_the_profile_region_alone_is_used(self):
        # The shell #118 found: preflight passes on the profile's Region, and nothing else names one.
        case = Case(self, profile_region="us-east-2")
        result = case.run("import")
        self.assertEqual({"us-east-2"}, self.imported_into(case, result))
        self.assertIn("into ACM in us-east-2", result.stdout)
        arn_file = case.certs / f"spa-platform-{CELL}-wildcard.acm-arn"
        self.assertIn(":us-east-2:", arn_file.read_text())

    def test_aws_default_region_is_over_the_profile(self):
        case = Case(self, profile_region="us-west-2")
        result = case.run("import", env=case.env(AWS_DEFAULT_REGION="eu-west-1"))
        self.assertEqual({"eu-west-1"}, self.imported_into(case, result))

    def test_aws_region_is_over_aws_default_region(self):
        case = Case(self, profile_region="us-west-2")
        result = case.run("import", env=case.env(AWS_REGION="us-east-2", AWS_DEFAULT_REGION="eu-west-1"))
        self.assertEqual({"us-east-2"}, self.imported_into(case, result))

    def test_the_region_flag_is_over_everything(self):
        case = Case(self, profile_region="us-west-2")
        result = case.run("import", "--region", "ca-central-1",
                          env=case.env(AWS_REGION="us-east-2", AWS_DEFAULT_REGION="eu-west-1"))
        self.assertEqual({"ca-central-1"}, self.imported_into(case, result))

    def test_the_profile_is_not_asked_when_the_environment_names_a_region(self):
        case = Case(self, profile_region="us-west-2")
        result = case.run("import", env=case.env(AWS_REGION="us-east-2"))
        self.imported_into(case, result)
        self.assertNotIn("configure", [call["service"] for call in case.aws_calls()])

    def test_fetch_ca_reads_the_profile_region(self):
        # A certificate in the profile's Region, and one in the old fallback: the fetch must find
        # the first and never ask the second.
        case = Case(self, issued=False, profile_region="us-east-2",
                    certificates=[{"region": "us-east-2", "arn": arn("us-east-2"), "cell": CELL}])
        result = case.run("fetch-ca")
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        self.assertIn("from ACM in us-east-2", result.stdout)
        self.assertEqual({"us-east-2"}, case.acm_regions())


# --- status, which reads ACM but creates nothing ------------------------------------------------------

class StatusChecksAcmOnlyInAResolvedRegion(CompanionTestCase):
    def test_status_with_no_region_skips_acm_and_says_so(self):
        case = Case(self)
        result = case.run("status")
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        self.assertEqual([], case.acm_calls(), "status asked ACM in a Region nothing named")
        self.assertIn("ACM: not checked", result.stdout)
        self.assertIn("no Region", result.stdout)
        self.assertNotIn(FALLBACK, result.output)

    def test_status_reads_acm_in_the_profile_region(self):
        case = Case(self, profile_region="us-east-2",
                    certificates=[{"region": "us-east-2", "arn": arn("us-east-2"), "cell": CELL}])
        result = case.run("status")
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        self.assertEqual({"us-east-2"}, case.acm_regions())
        self.assertIn(f"ACM: {arn('us-east-2')}", result.stdout)
        self.assertIn("region us-east-2", result.stdout)


def main() -> int:
    required = os.environ.get(REQUIRE_BASH)
    if required:
        version = subprocess.run([BASH, "-c", 'printf "%s.%s" "${BASH_VERSINFO[0]}" "${BASH_VERSINFO[1]}"'],
                                 env={"PATH": "/usr/bin:/bin"}, capture_output=True, text=True, check=True).stdout
        if version != required:
            print(f"certs_tests: {REQUIRE_BASH}={required}, but {BASH} is {version}", file=sys.stderr)
            return 1
    program = unittest.main(exit=False)
    return 0 if program.result.wasSuccessful() else 1


if __name__ == "__main__":
    sys.exit(main())
