#!/usr/bin/env python3
"""Test companion for tools/scripts/platform-tfvars.sh. Owner: workstream 1.

#470, raised by #242: on the from-nothing run of 2026-10-27 the platform root's terraform.tfvars was
rewritten by hand from the new identity root's outputs and the sample image's current digest, ECR
Public answered the digest lookup with a 429, and the rewrite wrote
`gateway_image = "public.ecr.aws/nginx/nginx-unprivileged@"`. platform-tfvars.sh is that rewrite as
a script, and these cases hold it to the ticket's list: a 429 on the lookup leaves the file byte for
byte as it was; an empty or a short digest is refused, from the lookup or from --sample-digest; a
good run changes exactly the named keys; the other keys and the comments survive.

The doubles, the world they share and the universal every run holds (no output names the account
id, which the identity root's cognito_domain carries) are tools/scripts/cell_doubles.py's: the
terraform double answers the identity root's outputs, and the docker double answers
`docker buildx imagetools inspect` from world["public_images"], or with the status in
faults["imagetools"].

The file each case starts from is the root's own terraform.tfvars.example, read from the tree, with
what an operator has added to it after a first apply: an uncommented gateway_image and an issuers
block from an earlier identity root, each with a comment of its own.

Run: python3 -I tools/scripts/platform_tfvars_tests.py
"""

from __future__ import annotations

import importlib.util
import pathlib
import re
import sys

_spec = importlib.util.spec_from_file_location("cell_doubles", pathlib.Path(__file__).resolve().parent / "cell_doubles.py")
cell = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(cell)

import unittest  # noqa: E402

SCRIPT = "platform-tfvars.sh"
EXAMPLE = cell.ROOT / "infra/envs/poc/platform/terraform.tfvars.example"
TFVARS = "infra/envs/poc/platform/terraform.tfvars"

SAMPLE = "public.ecr.aws/nginx/nginx-unprivileged"
SAMPLE_REFERENCE = f"{SAMPLE}:stable-alpine"
CURRENT = "sha256:" + "7" * 64
GIVEN = "sha256:" + "8" * 64
OLD_IMAGE = f"{SAMPLE}@sha256:" + "5" * 64
OLD_AUTHORITY = f"https://cognito-idp.{cell.REGION}.amazonaws.com/{cell.REGION}_OLDPOOL"
OLD_ORIGIN = "https://spa-platform-poc-old.auth.us-east-2.amazoncognito.com"

# What an operator's file holds after a first apply: the example, its gateway_image filled in below
# the commented line it came from, and an issuers block from an earlier identity root.
OPERATOR_ADDITIONS = f"""
# Pinned on 2026-10-24.
gateway_image = "{OLD_IMAGE}"

# From the identity root's outputs (#242).
issuers = {{
  "cognito-poc" = {{
    # authority is the identity root's issuer output.
    authority      = "{OLD_AUTHORITY}"
    profile        = "cognito"
    browser_origin = "{OLD_ORIGIN}" # https:// plus cognito_domain
  }}
}}
"""


def operator_file() -> str:
    return EXAMPLE.read_text() + OPERATOR_ADDITIONS


def setUpModule():
    cell.bash_banner("platform_tfvars_tests")


class Tfvars(cell.CompanionTestCase):

    def case(self, content: str | None = None, *, digest: str | None = CURRENT) -> cell.Case:
        world = cell.default_world()
        if digest is not None:
            world["public_images"] = {SAMPLE_REFERENCE: digest}
        case = cell.Case(self, world=world)
        if content is not None:
            path = case.repo / TFVARS
            path.write_text(content)
        return case

    def tfvars(self, case: cell.Case) -> bytes:
        return (case.repo / TFVARS).read_bytes()

    def neighbours(self, case: cell.Case) -> list[str]:
        """Every file in the platform root's directory: a temporary file left there is a failure too."""
        return sorted(path.name for path in (case.repo / TFVARS).parent.iterdir())

    def issuer(self) -> str:
        return cell.default_world()["roots"]["idp"]["issuer"]

    def origin(self) -> str:
        return "https://" + cell.default_world()["roots"]["idp"]["cognito_domain"]

    def assertUnchanged(self, case: cell.Case, before: bytes, result: cell.Result, status: int):
        self.assertEqual(status, result.status, result.output)
        self.assertEqual(before, self.tfvars(case), "the file changed although the run failed")
        self.assertEqual(["terraform.tfvars"], self.neighbours(case), "the run left a file beside terraform.tfvars")
        self.assertNoUnexpectedCall(case)


class AFailedOrMalformedLookupLeavesTheFileAsItWas(Tfvars):

    def test_a_429_on_the_digest_lookup_leaves_the_file_byte_identical_and_names_the_lookup(self):
        case = self.case(operator_file())
        case.world["faults"]["imagetools"] = 429
        case.save()
        before = self.tfvars(case)
        result = case.run(SCRIPT)
        self.assertUnchanged(case, before, result, 1)
        self.assertIn(f"the digest lookup of {SAMPLE_REFERENCE}", result.stderr)
        self.assertIn("429", result.stderr)
        self.assertEqual([["buildx", "imagetools", "inspect", "--format", "{{json .Manifest}}", SAMPLE_REFERENCE]],
                         [call["argv"] for call in case.calls("docker")])

    def test_an_empty_digest_from_the_lookup_is_refused(self):
        case = self.case(operator_file(), digest="")
        before = self.tfvars(case)
        result = case.run(SCRIPT)
        self.assertUnchanged(case, before, result, 1)
        self.assertIn(f"the digest lookup of {SAMPLE_REFERENCE}", result.stderr)

    def test_a_short_digest_from_the_lookup_is_refused(self):
        case = self.case(operator_file(), digest="sha256:517607ae")
        before = self.tfvars(case)
        result = case.run(SCRIPT)
        self.assertUnchanged(case, before, result, 1)
        self.assertIn(f"the digest lookup of {SAMPLE_REFERENCE}", result.stderr)

    def test_an_empty_or_short_sample_digest_is_refused_before_any_lookup(self):
        for given in ("", "sha256:", "sha256:517607ae", "517607ae" * 8, "sha256:" + "A" * 64):
            with self.subTest(given=given):
                case = self.case(operator_file())
                before = self.tfvars(case)
                result = case.run(SCRIPT, "--sample-digest", given)
                self.assertUnchanged(case, before, result, 2)
                self.assertIn("--sample-digest", result.stderr)
                self.assertEqual([], case.calls(), "a refused digest still made a call")

    def test_an_identity_root_with_no_outputs_leaves_the_file_unchanged(self):
        case = self.case(operator_file())
        case.world["faults"]["output idp"] = True
        case.save()
        before = self.tfvars(case)
        result = case.run(SCRIPT)
        self.assertUnchanged(case, before, result, 1)
        self.assertIn("the identity root's issuer output", result.stderr)

    def test_a_malformed_identity_output_leaves_the_file_unchanged(self):
        for name, value in (("issuer", "http://cognito-idp.example/pool"), ("issuer", ""),
                            ("cognito_domain", "https://already-a-url.example"), ("cognito_domain", "")):
            with self.subTest(name=name, value=value):
                case = self.case(operator_file())
                case.world["roots"]["idp"][name] = value
                case.save()
                before = self.tfvars(case)
                result = case.run(SCRIPT)
                self.assertUnchanged(case, before, result, 1)
                self.assertIn(f"the identity root's {name} output", result.stderr)

    def test_a_missing_file_is_refused_and_not_created(self):
        case = self.case(None)
        result = case.run(SCRIPT)
        self.assertEqual(2, result.status, result.output)
        self.assertFalse((case.repo / TFVARS).exists())
        self.assertIn("terraform.tfvars.example", result.stderr)

    def test_an_issuers_block_the_script_cannot_read_is_left_alone(self):
        # A block whose cognito-poc entry sets authority twice: which one Terraform would keep is not
        # this script's to guess, so it refuses rather than edit one of them.
        content = operator_file().replace(
            '    profile        = "cognito"\n', f'    profile        = "cognito"\n    authority = "{OLD_AUTHORITY}"\n')
        case = self.case(content)
        before = self.tfvars(case)
        result = case.run(SCRIPT)
        self.assertUnchanged(case, before, result, 1)
        self.assertIn("authority", result.stderr)


class AGoodRunChangesExactlyTheNamedKeys(Tfvars):

    def test_the_operator_s_file_changes_in_the_named_values_and_nowhere_else(self):
        start = operator_file()
        case = self.case(start)
        result = case.run(SCRIPT)
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        expected = (start
                    .replace(f'gateway_image = "{OLD_IMAGE}"', f'gateway_image = "{SAMPLE}@{CURRENT}"')
                    .replace(f'authority      = "{OLD_AUTHORITY}"', f'authority      = "{self.issuer()}"')
                    .replace(f'browser_origin = "{OLD_ORIGIN}"', f'browser_origin = "{self.origin()}"'))
        self.assertNotEqual(start, expected)
        self.assertEqual(expected, self.tfvars(case).decode())
        self.assertEqual(["terraform.tfvars"], self.neighbours(case))
        # health_check_path is already "/" in the example, so it is named as unchanged.
        self.assertIn('changed: gateway_image, issuers["cognito-poc"].authority, issuers["cognito-poc"].browser_origin',
                      result.stdout)
        self.assertIn("unchanged: health_check_path", result.stdout)

    def test_it_prints_the_sample_digest_and_no_other_value(self):
        case = self.case(operator_file())
        result = case.run(SCRIPT)
        self.assertEqual(0, result.status, result.output)
        self.assertIn(CURRENT, result.stdout)
        # The account id in cognito_domain is Case.run's universal; these are the rest of each value.
        for value in (self.issuer(), self.origin(), cell.default_world()["roots"]["idp"]["cognito_domain"],
                      f"{cell.REGION}_EXAMPLE", OLD_AUTHORITY, OLD_ORIGIN, OLD_IMAGE):
            self.assertNotIn(value, result.output)

    def test_a_given_sample_digest_is_written_with_no_lookup(self):
        case = self.case(operator_file())
        case.remove("docker")
        result = case.run(SCRIPT, "--sample-digest", GIVEN)
        self.assertEqual(0, result.status, result.output)
        self.assertNoUnexpectedCall(case)
        self.assertIn(f'\ngateway_image = "{SAMPLE}@{GIVEN}"\n', self.tfvars(case).decode())
        self.assertEqual([], case.calls("docker"))

    def test_the_bare_example_gains_the_three_values_and_keeps_every_line_in_order(self):
        start = EXAMPLE.read_text()
        self.assertIsNone(re.search(r"(?m)^gateway_image\s*=", start), "the example now sets gateway_image")
        self.assertIsNone(re.search(r"(?m)^issuers\s*=", start), "the example now sets issuers")
        case = self.case(start.replace('health_check_path = "/"', 'health_check_path = "/readyz"'))
        result = case.run(SCRIPT)
        self.assertEqual(0, result.status, result.output)
        after = self.tfvars(case).decode()
        # Every line of the example survives, in order, with the three values among them.
        lines = after.splitlines()
        position = 0
        for line in start.splitlines():
            if line.startswith("health_check_path"):
                line = 'health_check_path = "/"'
            position = lines.index(line, position) + 1
        # The pin goes under the commented line that says where it belongs.
        commented = next(index for index, line in enumerate(lines) if line.startswith("# gateway_image"))
        self.assertEqual(f'gateway_image = "{SAMPLE}@{CURRENT}"', lines[commented + 1])
        self.assertIn(
            f'issuers = {{\n  "cognito-poc" = {{\n    authority      = "{self.issuer()}"\n'
            f'    profile        = "cognito"\n    browser_origin = "{self.origin()}"\n  }}\n}}\n', after)
        self.assertIn("changed: gateway_image, health_check_path, issuers", result.stdout)

    def test_a_second_run_changes_nothing_and_says_so(self):
        case = self.case(operator_file())
        self.assertEqual(0, case.run(SCRIPT).status)
        once = self.tfvars(case)
        result = case.run(SCRIPT)
        self.assertEqual(0, result.status, result.output)
        self.assertEqual(once, self.tfvars(case))
        self.assertNotIn("changed:", result.stdout.replace("unchanged:", ""))

    def test_the_file_keeps_its_mode(self):
        case = self.case(operator_file())
        (case.repo / TFVARS).chmod(0o640)
        self.assertEqual(0, case.run(SCRIPT).status)
        self.assertEqual(0o640, (case.repo / TFVARS).stat().st_mode & 0o777)


if __name__ == "__main__":
    sys.exit(cell.main("platform_tfvars_tests"))
