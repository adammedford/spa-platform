#!/usr/bin/env python3
"""Tests for tools/wiz_redact.py. Owner: integrator.

Run: python3 -I tools/wiz_redact_tests.py

The `wiz` skill pipes every post through tools/wiz_redact.py (#259), so a rule that stops matching
leaks an account id, an authorization code or a secret into a public comment and says nothing. Each
case starts the real script as a subprocess under `python3 -I`, in D-3's shape, feeds it text on
stdin and compares stdout, stderr and the exit code with values written out by hand. Each of the
four rules has at least one case that turns red when that rule is removed from the script; #259's
merge body names the mutation and the case.

The ids, codes and secrets below are made up for the cases; none is a real account or credential.
"""
from __future__ import annotations

import pathlib
import subprocess
import sys
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "tools" / "wiz_redact.py"

ACCESS_DENIED_EXIT = 3


def redact(text: str, *args: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, "-I", str(SCRIPT), *args],
        input=text.encode("utf-8"),
        capture_output=True,
        timeout=60,
        check=False,
    )


class Redacts(unittest.TestCase):
    def assertRedacts(self, text: str, expected: str, *args: str) -> None:
        done = redact(text, *args)
        self.assertEqual(done.returncode, 0, done.stderr.decode())
        self.assertEqual(done.stderr.decode(), "")
        self.assertEqual(done.stdout.decode("utf-8"), expected)


class NothingToRedact(Redacts):
    def test_text_with_nothing_to_redact_passes_through_unchanged(self) -> None:
        text = (
            "## A4, read-back\n"
            "\n"
            "exit=0\n"
            '{"desired": 1, "running": 1, "pending": 0, "cpu": "256", "memory": "1024"}\n'
            "the redirect carried `?code=` -- response_type=code&scope=openid\n"
            "a build number 12345678901 (eleven digits) and 1234567890123 (thirteen)\n"
            "Values=spa-platform, ClientIds counted: 2, café\n"
        )
        self.assertRedacts(text, text)

    def test_empty_input_gives_empty_output(self) -> None:
        self.assertRedacts("", "")

    def test_bytes_that_are_not_utf8_pass_through_unchanged(self) -> None:
        raw = b"line one\n\xff\xfe not utf-8\n"
        done = subprocess.run([sys.executable, "-I", str(SCRIPT)], input=raw,
                              capture_output=True, timeout=60, check=False)
        self.assertEqual(done.returncode, 0, done.stderr.decode())
        self.assertEqual(done.stdout, raw)


class RuleOneAccountIds(Redacts):
    def test_a_twelve_digit_number_becomes_account(self) -> None:
        self.assertRedacts("account 123456789012 is the pin\n", "account <account> is the pin\n")

    def test_a_dashed_twelve_digit_number_becomes_account(self) -> None:
        self.assertRedacts("Account ID: 1234-5678-9012\n", "Account ID: <account>\n")

    def test_every_twelve_digit_number_goes_without_the_option(self) -> None:
        self.assertRedacts('{"Account": "210987654321", "pin": 123456789012}\n',
                           '{"Account": "<account>", "pin": <account>}\n')

    def test_keep_last_four_keeps_the_last_four_digits(self) -> None:
        self.assertRedacts("account 123456789012 and 1234-5678-4321\n",
                           "account <account-9012> and <account-4321>\n",
                           "--keep-last-four")

    def test_keep_last_four_still_reduces_an_arn_to_its_resource(self) -> None:
        self.assertRedacts("arn:aws:iam::123456789012:role/spa-platform-poc-task\n",
                           "role/spa-platform-poc-task\n", "--keep-last-four")


class RuleTwoArns(Redacts):
    def test_a_twelve_digit_id_inside_an_arn_leaves_only_the_resource(self) -> None:
        self.assertRedacts(
            "role: arn:aws:iam::123456789012:role/spa-platform-poc-task\n",
            "role: role/spa-platform-poc-task\n",
        )

    def test_an_arn_with_a_region_inside_json(self) -> None:
        self.assertRedacts(
            '["arn:aws:ecs:us-east-1:123456789012:service/spa-poc/gateway", '
            '"arn:aws:logs:us-east-1:123456789012:log-group:/spa-platform/poc/gateway:*"]\n',
            '["service/spa-poc/gateway", "log-group:/spa-platform/poc/gateway:*"]\n',
        )

    def test_an_arn_without_an_account(self) -> None:
        self.assertRedacts("bucket arn:aws:s3:::spa-poc-releases/app1\n",
                           "bucket spa-poc-releases/app1\n")


class RuleThreeValuesAfterAKey(Redacts):
    def test_a_code_in_a_url_is_elided(self) -> None:
        self.assertRedacts(
            "https://app1.spa-poc.test:8443/signin-oidc?code=4f1c2e9a-77aa-4c1b&state=abc\n",
            "https://app1.spa-poc.test:8443/signin-oidc?code=<elided>&state=abc\n",
        )

    def test_a_client_id_in_a_url_is_elided(self) -> None:
        self.assertRedacts(
            "authorize?response_type=code&client_id=3n4b5c6d7e8f9g0h&scope=openid\n",
            "authorize?response_type=code&client_id=<elided>&scope=openid\n",
        )

    def test_aws_clientid_clientsecret_and_sessiontoken_are_elided(self) -> None:
        self.assertRedacts(
            '{"UserPoolClient": {"ClientId": "3n4b5c6d7e8f9g0h", "ClientSecret": "s3cr3t-v4lu3"},\n'
            ' "Credentials": {"SessionToken": "IQoJb3JpZ2luX2VjE"}}\n',
            '{"UserPoolClient": {"ClientId": "<elided>", "ClientSecret": "<elided>"},\n'
            ' "Credentials": {"SessionToken": "<elided>"}}\n',
        )

    def test_a_key_in_cli_shorthand_is_elided(self) -> None:
        self.assertRedacts("--client ClientId=3n4b5c6d7e8f9g0h\n", "--client ClientId=<elided>\n")

    def test_a_parameters_value_is_elided(self) -> None:
        self.assertRedacts(
            '{"Parameter": {"Name": "/spa-platform/poc/app1/client-secret", "Type": "SecureString",'
            ' "Value": "s3cr3t-v4lu3", "Version": 1}}\n',
            '{"Parameter": {"Name": "/spa-platform/poc/app1/client-secret", "Type": "SecureString",'
            ' "Value": "<elided>", "Version": 1}}\n',
        )

    def test_a_tags_value_is_kept(self) -> None:
        text = '{"Tags": [{"Key": "Platform", "Value": "spa-platform"}]}\n'
        self.assertRedacts(text, text)


class RuleFourJsonMembers(Redacts):
    def test_a_client_id_in_json_is_elided(self) -> None:
        self.assertRedacts(
            '{"app1": {"clientId": "3n4b5c6d7e8f9g0h", "host": "app1.spa-poc.test"}}\n',
            '{"app1": {"clientId": "<elided>", "host": "app1.spa-poc.test"}}\n',
        )

    def test_each_named_member_is_elided_whatever_its_value(self) -> None:
        self.assertRedacts(
            '{"secret": "abc\\"def", "SecretString": "{\\"clientSecret\\":\\"x\\"}",\n'
            ' "AccessKeyId": "ASIAEXAMPLEEXAMPLE", "SecretAccessKey": {"nested": [1, "}"]},'
            ' "kept": true}\n',
            '{"secret": "<elided>", "SecretString": "<elided>",\n'
            ' "AccessKeyId": "<elided>", "SecretAccessKey": "<elided>",'
            ' "kept": true}\n',
        )


class AccessDenied(unittest.TestCase):
    def assertStops(self, text: str) -> None:
        done = redact(text)
        self.assertEqual(done.returncode, ACCESS_DENIED_EXIT)
        self.assertEqual(done.stdout, b"")
        err = done.stderr.decode()
        self.assertEqual(err.count("\n"), 1, err)
        self.assertTrue(err.endswith("\n"))
        self.assertIn("AccessDenied", err)
        self.assertIn("spend limit", err)
        self.assertNotIn("123456789012", err)
        self.assertNotIn("is not authorized", err)

    def test_an_access_denied_exception_stops_without_writing_the_body(self) -> None:
        self.assertStops(
            "An error occurred (AccessDeniedException) when calling the DescribeServices "
            "operation: User: arn:aws:sts::123456789012:assumed-role/x/y is not authorized\n"
        )

    def test_an_s3_access_denied_body_stops_without_writing_the_body(self) -> None:
        self.assertStops(
            '<?xml version="1.0"?><Error><Code>AccessDenied</Code><Message>Access Denied'
            "</Message><RequestId>123456789012</RequestId></Error>\nis not authorized\n"
        )

    def test_the_words_access_denied_alone_stop(self) -> None:
        self.assertStops("upload failed: Access Denied (account 123456789012 is not authorized)\n")


class Usage(unittest.TestCase):
    def test_an_unknown_option_fails_and_writes_nothing(self) -> None:
        done = redact("account 123456789012\n", "--keep-last-five")
        self.assertNotEqual(done.returncode, 0)
        self.assertNotEqual(done.returncode, ACCESS_DENIED_EXIT)
        self.assertEqual(done.stdout, b"")


if __name__ == "__main__":
    unittest.main()
