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
The double runs in the recorder's own process, as `cell_doubles.py --double aws` runs, rather than
in a second one, and the companions' suites run three at a time (#1474). With cell_doubles.py's
shared wrappers and kept bytecode, those took this run from 216 s to about 50 s under load.

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
import sys
import threading

HERE = pathlib.Path(__file__).resolve().parent


def record(launch: str, doubles: str, case: str, harvest: str, args: list[str]) -> int:
    """The recorder that stands in a case's bin/aws: the real double, its standard output copied.

    The double is cell_doubles.py's `--double aws <case>` with the call's arguments, run in this
    process by `launch`, cell_doubles.LAUNCH, as a case's own bin/aws runs it: the same code,
    reading the same environment and standard input and writing the same standard error, without a
    second interpreter for each call. Its standard output is file descriptor 1 itself, a pipe for
    the call, so what a child of the double writes there is held too: `ssm start-session` starts
    session-manager-plugin, which inherits it. The pipe is read until every writer has closed it,
    the double and any child it left, as a recorder that ran the double as a child read its pipe;
    then the whole is given to the script and copied to the harvest, raised or not."""
    sys.stdout.flush()
    saved = os.dup(1)
    reader, writer = os.pipe()
    os.dup2(writer, 1)
    os.close(writer)
    chunks: list[bytes] = []

    def drain():
        while chunk := os.read(reader, 65536):
            chunks.append(chunk)

    # Read while the double runs, so that a full pipe never stops a writer.
    draining = threading.Thread(target=drain)
    draining.start()
    status = 0
    try:
        # LAUNCH takes the file to run after its own argument, as `python3 -c` would leave it.
        sys.argv = ["-c", doubles, "--double", "aws", case, *args]
        exec(launch, {"__builtins__": __builtins__})
    except SystemExit as stop:
        # The double ends by sys.exit with its status, as a process's would be read.
        status = 0 if stop.code is None else stop.code
    finally:
        sys.stdout.flush()
        os.dup2(saved, 1)
        os.close(saved)
        draining.join()
        os.close(reader)
        stdout = b"".join(chunks)
        sys.stdout.buffer.write(stdout)
        sys.stdout.buffer.flush()
        with open(harvest, "a") as handle:
            handle.write(json.dumps({"argv": args, "stdout": stdout.decode(errors="replace")}) + "\n")
    return status


if __name__ == "__main__" and sys.argv[1:2] == ["--record"]:
    sys.exit(record(*sys.argv[2:6], sys.argv[6:]))

import concurrent.futures  # noqa: E402 -- below the recorder, which starts once per call
import importlib.util  # noqa: E402
import io  # noqa: E402
import re  # noqa: E402
import tempfile  # noqa: E402
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
    """The companions that load cell_doubles.py, from their own text: beside it, and under poc/,
    where the proof cell's scripts and theirs sit (#1349)."""
    return sorted(path for path in HERE.rglob("*_tests.py")
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
        # One recorder for the companion's cases, which each case's bin/aws links to: a wrapper as
        # cell_doubles.py makes the doubles' own, which takes the case from the path it was run
        # by, and for their reason, cell_doubles.ToolWrappers' (#1474).
        recorder = directory / f"{path.stem}-aws"
        recorder.write_text(cell.case_wrapper(
            pathlib.Path(__file__).resolve(),
            ["--record", cell.LAUNCH, pathlib.Path(cell.__file__).resolve()], [self.file]))
        recorder.chmod(0o555)
        constructed = cell.Case.__init__
        self.installed = 0

        def recorded(case, *args, **kwargs):
            constructed(case, *args, **kwargs)
            self.installed += 1
            (case.bin / "aws").unlink()
            (case.bin / "aws").symlink_to(recorder)

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


_HARVESTS: dict[str, concurrent.futures.Future] = {}
_LOCK = threading.Lock()
_SCRATCH = tempfile.TemporaryDirectory(prefix="cell-doubles-harvest-")
# How many companions' suites run at once (#1474). Each runs its own cases on
# tools/companion_runner.py's threads, up to one per CPU and at most 8, and its cases spend their
# time in child processes, so the suites one at a time left most of the machine idle: 216 s of
# them under load on 2026-11-22. On the 12-core Mac it was measured on, two, three, four and eight
# at once all passed and took 53 to 64 s under a load average of 20 to 60, CPU-bound alike; three
# keeps the cases running together near two per CPU, so deploy_tests.py's wall-clock bounds (its
# five-second wait for a closed port) keep their margin.
AT_ONCE = 3
_POOL = concurrent.futures.ThreadPoolExecutor(max_workers=AT_ONCE)


def harvest(path: pathlib.Path) -> Harvest:
    """The companion's harvest, once it has run. The first call starts every companion's, the
    longest suites first, by their text's length, so the shorter ones fill in beside them; a harvest
    that raised raises here, in the subTest of whichever case asks for it. `path` is one of
    companions(), as every caller's is."""
    with _LOCK:
        if not _HARVESTS:
            directory = pathlib.Path(_SCRATCH.name)
            for found in sorted(companions(), key=lambda held: -held.stat().st_size):
                _HARVESTS[found.name] = _POOL.submit(Harvest, found, directory)
        future = _HARVESTS[path.name]
    return future.result()


def scripts_calling(name: str) -> list[str]:
    """The cell scripts whose code, outside comments, makes the call `name`."""
    pattern = re.compile(rf"(?<![\w-]){re.escape(name)}(?![\w-])")
    found = []
    for script in sorted(HERE.rglob("*.sh")):
        code = "\n".join(line.split("#", 1)[0] for line in script.read_text().splitlines())
        if pattern.search(code):
            found.append(script.name)
    return found


class TheProofCellsCompanionsAreFound(unittest.TestCase):
    def test_the_companions_under_poc_are_among_those_searched(self):
        # #1349 moved the proof cell's scripts and their companions to poc/; a search of this
        # directory alone would drop them from the case above without a word.
        names = {path.relative_to(HERE).as_posix() for path in companions()}
        for name in ("poc/seed_tests.py", "poc/platform_tfvars_tests.py", "poc/mock_api_tests.py", "deploy_tests.py"):
            self.assertIn(name, names)


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
