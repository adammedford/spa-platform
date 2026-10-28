#!/usr/bin/env python3
"""Test companion for tools/scripts/cell_doubles.py, the doubles of the cell-script companions.
Owner: workstream 1.

#201. The 1.7 doubles handed the teardown role's secret and session token to teardown.sh as bare
values that no search list held, so "never printed" went untested and three reviews missed it (#191
added the two). This companion makes that omission one a run catches: it reads every credential the
doubles hand out and holds each companion's search list to it.

**Which companions.** Every `*_tests.py` beside this file that loads cell_doubles.py, read from the
files rather than listed here, so one added later is held too. Each loads its own copy of cell_doubles,
and Case.run searches every run's output for that copy's SECRETS: that tuple is the companion's
search list.

**What a double hands out is observed, not read from its source.** Before #191 the secret and the
token were string literals in aws_answer's return, named by no constant, so a reading of the
doubles' names would have missed them as the reviews did. Instead each companion's whole suite runs
here once, through tools/companion_runner.py as it runs on its own, with its bin/aws replaced by a
recorder: this file in `--record` mode, which runs the real double with the same arguments,
environment and standard input, passes its standard error and exit status through unchanged, and
gives the script its standard output byte for byte, after copying it to the companion's harvest.

**What is credential-shaped is read from the call, not guessed from the value.** A canary has no
shape of its own ("teardownsecret/abc+def"), so a value counts by the AWS operation that answered
it: the operations in CREDENTIAL_CALLS answer with credentials, as their API references document.
The scripts read every answer with --output text, so every field such a call prints is a
credential; a JSON answer counts its CREDENTIAL_FIELDS. An operation missing from the table is this
companion's gap, and the anchor case keeps it from being silent for the calls the scripts make
today: each table operation a cell script calls must appear in the harvest.

**Only the aws double.** The scripts' credentials come from AWS: the registry password from ECR's
get-login-password, the teardown role's three from STS's AssumeRole. The terraform double hands out
the roots' outputs, which name parameters and never their values, docker's hands out a login result,
and dotnet's (spactl) a publish result.

Run: python3 -I tools/scripts/cell_doubles_tests.py
"""

from __future__ import annotations

import json
import os
import pathlib
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent


def record(double: str, harvest: str, args: list[str]) -> int:
    """The recorder that stands in a case's bin/aws: the real double, its standard output copied."""
    completed = subprocess.run([double, *args], stdout=subprocess.PIPE)
    sys.stdout.buffer.write(completed.stdout)
    sys.stdout.buffer.flush()
    with open(harvest, "a") as handle:
        handle.write(json.dumps({"argv": args, "stdout": completed.stdout.decode(errors="replace")}) + "\n")
    return completed.returncode


if __name__ == "__main__" and sys.argv[1:2] == ["--record"]:
    sys.exit(record(sys.argv[2], sys.argv[3], sys.argv[4:]))

import importlib.util  # noqa: E402 -- below the recorder, which starts once per call
import io  # noqa: E402
import re  # noqa: E402
import shlex  # noqa: E402
import tempfile  # noqa: E402
import threading  # noqa: E402
import unittest  # noqa: E402

ROOT = HERE.parent.parent

# The AWS CLI operations whose answers are credentials, `<service> <operation>` as the CLI spells
# them, from each service's API reference:
#   STS: AssumeRole, AssumeRoleWithSAML, AssumeRoleWithWebIdentity, AssumeRoot, GetSessionToken and
#     GetFederationToken answer Credentials (AccessKeyId, SecretAccessKey, SessionToken),
#     https://docs.aws.amazon.com/STS/latest/APIReference/API_Credentials.html
#   ECR: get-login-password prints the registry password, and GetAuthorizationToken answers the
#     token it is made from, https://docs.aws.amazon.com/cli/latest/reference/ecr/get-login-password.html
#     and https://docs.aws.amazon.com/AmazonECR/latest/APIReference/API_GetAuthorizationToken.html
#   Secrets Manager: GetSecretValue answers SecretString or SecretBinary,
#     https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_GetSecretValue.html
#   Systems Manager: GetParameter, GetParameters and GetParametersByPath answer Value, decrypted on
#     --with-decryption: counted either way, since a script that reads one wants its value,
#     https://docs.aws.amazon.com/systems-manager/latest/APIReference/API_GetParameter.html
#   IAM: CreateAccessKey answers AccessKey's SecretAccessKey,
#     https://docs.aws.amazon.com/IAM/latest/APIReference/API_CreateAccessKey.html
#   Cognito: InitiateAuth and AdminInitiateAuth answer AuthenticationResult's tokens,
#     https://docs.aws.amazon.com/cognito-user-identity-pools/latest/APIReference/API_InitiateAuth.html
CREDENTIAL_CALLS = frozenset({
    "sts assume-role", "sts assume-role-with-saml", "sts assume-role-with-web-identity", "sts assume-root",
    "sts get-session-token", "sts get-federation-token",
    "ecr get-login-password", "ecr get-authorization-token", "ecr-public get-login-password",
    "ecr-public get-authorization-token",
    "secretsmanager get-secret-value",
    "ssm get-parameter", "ssm get-parameters", "ssm get-parameters-by-path",
    "iam create-access-key",
    "cognito-idp initiate-auth", "cognito-idp admin-initiate-auth",
})
# The fields of those answers that hold a credential, when one comes back as JSON.
CREDENTIAL_FIELDS = frozenset({
    "AccessKeyId", "SecretAccessKey", "SessionToken", "authorizationToken", "SecretString", "SecretBinary",
    "Value", "AccessToken", "IdToken", "RefreshToken",
})
# The CLI's global options that take no value (`aws help`, "GLOBAL OPTIONS"); every other option
# before the command is taken to carry one.
FLAGS = frozenset({"--debug", "--no-verify-ssl", "--no-paginate", "--no-sign-request", "--no-cli-pager",
                   "--cli-auto-prompt", "--no-cli-auto-prompt"})


def operation(argv: list[str]) -> str:
    """`<service> <operation>` of an aws command line: its first two words that are not options."""
    words = []
    index = 0
    while index < len(argv) and len(words) < 2:
        token = argv[index]
        if token.startswith("--"):
            index += 1 if token in FLAGS or "=" in token else 2
            continue
        words.append(token)
        index += 1
    return " ".join(words)


def credentials_in(stdout: str) -> list[str]:
    """The credentials one credential call's answer holds: every field it printed, as text, or the
    CREDENTIAL_FIELDS of a JSON answer."""
    try:
        document = json.loads(stdout)
    except ValueError:
        return [field for field in re.split(r"[\t\n]", stdout) if field.strip()]
    found = []

    def walk(value, key=None):
        if isinstance(value, dict):
            for name, inner in value.items():
                walk(inner, name)
        elif isinstance(value, list):
            for inner in value:
                walk(inner, key)
        elif key in CREDENTIAL_FIELDS and value not in (None, ""):
            found.append(str(value))

    walk(document)
    if not isinstance(document, (dict, list)) and str(document).strip():
        found.append(str(document))
    return found


def companions() -> list[pathlib.Path]:
    """The companions that load cell_doubles.py, from their own text."""
    return sorted(path for path in HERE.glob("*_tests.py")
                  if path.resolve() != pathlib.Path(__file__).resolve() and '"cell_doubles.py"' in path.read_text())


class Harvest:
    """One companion's suite, run once with every case's bin/aws recorded."""

    def __init__(self, path: pathlib.Path, directory: pathlib.Path):
        self.name = path.name
        self.file = directory / f"{path.stem}.jsonl"
        self.file.touch()
        spec = importlib.util.spec_from_file_location(f"held_{path.stem}", path)
        self.module = importlib.util.module_from_spec(spec)
        # unittest finds a module's setUpModule through sys.modules.
        sys.modules[spec.name] = self.module
        spec.loader.exec_module(self.module)
        cell = self.module.cell
        self.secrets = tuple(cell.SECRETS)
        harvest = self.file
        constructed = cell.Case.__init__
        self.installed = 0

        def recorded(case, *args, **kwargs):
            constructed(case, *args, **kwargs)
            self.installed += 1
            double = case.dir / "aws-double"
            (case.bin / "aws").rename(double)
            case.write_executable("aws", (
                f"#!/bin/sh\nexec {shlex.quote(os.path.realpath(sys.executable))} -I -S "
                f"{shlex.quote(str(pathlib.Path(__file__).resolve()))} --record {shlex.quote(str(double))} "
                f"{shlex.quote(str(harvest))} \"$@\"\n"))

        cell.Case.__init__ = recorded
        runner = load_runner()
        suite = unittest.defaultTestLoader.loadTestsFromModule(self.module)
        self.report = io.StringIO()
        self.result = runner.Runner(stream=self.report, verbosity=1).run(suite)
        self.calls = [json.loads(line) for line in self.file.read_text().splitlines()]

    def handed_out(self) -> list[tuple[str, str]]:
        """(operation, credential) for every credential a credential call answered, once each."""
        seen = []
        for call in self.calls:
            name = operation(call["argv"])
            if name in CREDENTIAL_CALLS:
                for value in credentials_in(call["stdout"]):
                    if (name, value) not in seen:
                        seen.append((name, value))
        return seen


def load_runner():
    spec = importlib.util.spec_from_file_location("companion_runner", ROOT / "tools" / "companion_runner.py")
    runner = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(runner)
    return runner


_HARVESTS: dict[str, Harvest] = {}
_LOCK = threading.Lock()
_SCRATCH = tempfile.TemporaryDirectory(prefix="cell-doubles-harvest-")


def harvest(path: pathlib.Path) -> Harvest:
    with _LOCK:
        if path.name not in _HARVESTS:
            _HARVESTS[path.name] = Harvest(path, pathlib.Path(_SCRATCH.name))
        return _HARVESTS[path.name]


def scripts_calling(name: str) -> list[str]:
    """The cell scripts whose code, outside comments, makes the call `name`."""
    pattern = re.compile(rf"(?<![\w-]){re.escape(name)}(?![\w-])")
    found = []
    for script in sorted(HERE.glob("*.sh")):
        code = "\n".join(line.split("#", 1)[0] for line in script.read_text().splitlines())
        if pattern.search(code):
            found.append(script.name)
    return found


class EveryCredentialTheDoublesHandOutIsSearchedFor(unittest.TestCase):
    def test_each_companion_s_search_list_holds_every_credential_its_doubles_hand_out(self):
        found = companions()
        self.assertTrue(found, "no companion beside this file loads cell_doubles.py")
        for path in found:
            with self.subTest(companion=path.name):
                held = harvest(path)
                self.assertTrue(held.installed, f"{path.name}: no case was constructed, so the recorder was never in place")
                # A companion whose script makes no aws call records none, and says so with NO_AWS_CALLS
                # (platform_tfvars_tests.py, #470, whose cases take the aws double off PATH, so a call
                # fails them); every other one must record one, or the recorder missed them.
                if not getattr(held.module, "NO_AWS_CALLS", False):
                    self.assertTrue(held.calls, f"{path.name}: no aws call was recorded, so the recorder was not in place")
                missing = [f"  {value!r}, from {name}" for name, value in held.handed_out() if value not in held.secrets]
                if missing:
                    self.fail(f"{path.name}: its doubles hand out credentials that its search list, "
                              f"cell_doubles.SECRETS, lacks, so a script printing one passes:\n" + "\n".join(missing))
                # A suite that failed may have stopped before the call that hands one out.
                self.assertTrue(held.result.wasSuccessful(),
                                f"{path.name} did not pass under the recorder, so its harvest may be short:\n"
                                + held.report.getvalue()[-4000:])

    def test_every_credential_call_a_cell_script_makes_is_harvested(self):
        # The anchor: a call the scripts make that the harvest never saw means the recorder missed it,
        # and the case above would pass on an empty harvest.
        made = {name: scripts_calling(name) for name in sorted(CREDENTIAL_CALLS) if scripts_calling(name)}
        self.assertTrue(made, "no cell script makes a credential call, which teardown.sh and deploy.sh do")
        seen = {operation(call["argv"]) for path in companions() for call in harvest(path).calls}
        for name, scripts in made.items():
            with self.subTest(call=name):
                self.assertIn(name, seen, f"{', '.join(scripts)} call aws {name}, and no companion's run recorded it")

    def test_a_text_answer_counts_every_field_and_a_json_one_its_credential_fields(self):
        # Worked from the CLI's own output: --output text prints a query's list tab-separated on one
        # line; JSON prints the whole Credentials object, Expiration among the fields.
        self.assertEqual(["AKIAEXAMPLE", "secret/x+y", "token=="], credentials_in("AKIAEXAMPLE\tsecret/x+y\ttoken==\n"))
        self.assertEqual(["pass-word"], credentials_in("pass-word"))
        answer = {"Credentials": {"AccessKeyId": "AKIAEXAMPLE", "SecretAccessKey": "s", "SessionToken": "t",
                                  "Expiration": "2026-10-21T00:00:00Z"}, "AssumedRoleUser": {"Arn": "arn:aws:sts::1:x"}}
        self.assertEqual(["AKIAEXAMPLE", "s", "t"], credentials_in(json.dumps(answer)))
        self.assertEqual("sts assume-role",
                         operation(["--region", "us-east-2", "--output", "text", "sts", "assume-role", "--role-arn", "a"]))
        self.assertEqual("ecr get-login-password", operation(["--debug", "--region=us-east-2", "ecr", "get-login-password"]))


if __name__ == "__main__":
    unittest.main()
