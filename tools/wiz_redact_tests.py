#!/usr/bin/env python3
"""Tests for tools/wiz_redact.py. Owner: integrator.

Run: python3 -I tools/wiz_redact_tests.py

The `wiz` skill pipes every post through tools/wiz_redact.py (#259), so a rule that stops matching
leaks an account id, an authorization code or a secret into a public comment and says nothing. Each
case starts the real script as a subprocess under `python3 -I`, in D-3's shape, feeds it text on
stdin and compares stdout, stderr and the exit code with values written out by hand. Each of the
eight rules has at least one case that turns red when that rule is removed from the script; #259's
merge body names the mutation and the case. #468 added the terminal's --stream mode and P-04's
passing case lines, which a post carries as they are.

The ids, codes and secrets below are made up for the cases; none is a real account or credential.
"""
from __future__ import annotations

import pathlib
import select
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

    def test_a_twelve_digit_number_between_hyphens_becomes_account(self) -> None:
        # A CDK bootstrap bucket carries the account between hyphens; a card-like 16-digit dashed
        # number is not an account.
        self.assertRedacts("cdk-hnb659fds-assets-123456789012-us-east-1 x-210987654321\n"
                           "1234-5678-9012-3456\n",
                           "cdk-hnb659fds-assets-<account>-us-east-1 x-<account>\n"
                           "1234-5678-9012-3456\n")

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
            "https://app1.spa-poc.test:8443/signin-oidc?code=<elided>&state=<elided>\n",
        )

    def test_a_state_and_the_oauth_secrets_in_a_url_are_elided(self) -> None:
        self.assertRedacts(
            "cb?state=Zm9vYmFy&code_verifier=dBjftJeZ4CVP&client_secret=s3cr3t&access_token=at1&scope=openid\n",
            "cb?state=<elided>&code_verifier=<elided>&client_secret=<elided>&access_token=<elided>&scope=openid\n",
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
        text = ('{"Tags": [{"Key": "Platform", "Value": "spa-platform"}]}\n'
                "--tags Key=Platform,Value=spa-platform\n")
        self.assertRedacts(text, text)

    def test_a_key_in_a_nested_object_does_not_keep_the_parameters_value(self) -> None:
        self.assertRedacts('{"Parameter": {"Value": "s3cr3t", "Tags": [{"Key": "k"}]}}\n',
                           '{"Parameter": {"Value": "<elided>", "Tags": [{"Key": "k"}]}}\n')


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

    def test_the_oauth_token_members_are_elided(self) -> None:
        self.assertRedacts(
            '{"access_token": "eyJhbGciOi.payload.sig", "refresh_token": "r3fr3sh", "id_token": "eyJ0eXAi",'
            ' "client_secret": "s3cr3t", "token_type": "Bearer", "expires_in": 3600}\n',
            '{"access_token": "<elided>", "refresh_token": "<elided>", "id_token": "<elided>",'
            ' "client_secret": "<elided>", "token_type": "Bearer", "expires_in": 3600}\n',
        )


    def test_a_state_and_a_code_held_as_json_members_are_elided(self) -> None:
        self.assertRedacts('{"code": "4f1c2e9a", "state": "Zm9vYmFy", "error_code": "E1"}\n',
                           '{"code": "<elided>", "state": "<elided>", "error_code": "E1"}\n')

    def test_headers_held_as_json_members_are_elided_in_any_case(self) -> None:
        self.assertRedacts(
            '{"set-cookie": ["sid=abc123; path=/", "theme=dark"], "Authorization": "Bearer abc",'
            ' "x-xsrf-token": "t0k", "X-Amz-Security-Token": "IQoJb3", "content-type": "text/html"}\n',
            '{"set-cookie": "<elided>", "Authorization": "<elided>",'
            ' "x-xsrf-token": "<elided>", "X-Amz-Security-Token": "<elided>", "content-type": "text/html"}\n',
        )


class RuleFiveCookies(Redacts):
    def test_a_set_cookie_value_is_elided_and_its_attributes_are_kept(self) -> None:
        self.assertRedacts(
            "< Set-Cookie: __Host-spa.session=CfDJ8N0sess; path=/; secure; samesite=lax; httponly\n",
            "< Set-Cookie: __Host-spa.session=<elided>; path=/; secure; samesite=lax; httponly\n",
        )

    def test_every_cookie_of_a_cookie_line_is_elided(self) -> None:
        self.assertRedacts(
            "> Cookie: __Host-spa.session=CfDJ8N0sess; theme=dark; .AspNetCore.Antiforgery.x=tok\n",
            "> Cookie: __Host-spa.session=<elided>; theme=<elided>; .AspNetCore.Antiforgery.x=<elided>\n",
        )

    def test_an_expires_attribute_keeps_its_date(self) -> None:
        self.assertRedacts(
            "Set-Cookie: sid=abc123; Expires=Wed, 21 Oct 2026 07:28:00 GMT; Max-Age=60\n",
            "Set-Cookie: sid=<elided>; Expires=Wed, 21 Oct 2026 07:28:00 GMT; Max-Age=60\n",
        )


class RuleSixAuthorization(Redacts):
    def test_a_bearer_credential_is_elided_and_its_scheme_is_kept(self) -> None:
        self.assertRedacts("> Authorization: Bearer eyJhbGciOiJSUzI1NiJ9.e30.sig\n",
                           "> Authorization: Bearer <elided>\n")

    def test_a_basic_credential_is_elided(self) -> None:
        self.assertRedacts("Authorization: Basic dXNlcjpwYXNz\n", "Authorization: Basic <elided>\n")

    def test_a_bare_bearer_token_is_elided_and_the_words_are_not(self) -> None:
        self.assertRedacts("sent Bearer eyJhbGciOiJSUzI1NiJ9.e30.sig as the Bearer token\n",
                           "sent Bearer <elided> as the Bearer token\n")


class RuleSevenTokenHeaders(Redacts):
    def test_an_antiforgery_header_and_an_aws_session_token_header_are_elided(self) -> None:
        self.assertRedacts("> X-XSRF-TOKEN: CfDJ8tok\n> x-amz-security-token: IQoJb3JpZ2lu\n",
                           "> X-XSRF-TOKEN: <elided>\n> x-amz-security-token: <elided>\n")

    def test_a_presigned_urls_token_and_signature_are_elided(self) -> None:
        self.assertRedacts(
            "https://b.s3.amazonaws.com/k?X-Amz-Security-Token=IQoJb3&X-Amz-Expires=60&X-Amz-Signature=abc123\n",
            "https://b.s3.amazonaws.com/k?X-Amz-Security-Token=<elided>&X-Amz-Expires=60&X-Amz-Signature=<elided>\n",
        )


class RuleEightTerraformClientIds(Redacts):
    def test_a_user_pool_clients_terraform_id_is_elided_and_other_ids_are_kept(self) -> None:
        self.assertRedacts(
            'aws_cognito_user_pool_client.app["app3"]: Creation complete after 2s [id=3n4b5c6d7e8f9g0h1i2j3k4l5m]\n'
            'aws_cognito_user_pool_client.app["app3"]: Refreshing state... [id=3n4b5c6d7e8f9g0h1i2j3k4l5m]\n'
            'aws_ssm_parameter.client_secret["app3"]: Creation complete after 1s [id=/spa-platform/poc/app3/client-secret]\n',
            'aws_cognito_user_pool_client.app["app3"]: Creation complete after 2s [id=<elided>]\n'
            'aws_cognito_user_pool_client.app["app3"]: Refreshing state... [id=<elided>]\n'
            'aws_ssm_parameter.client_secret["app3"]: Creation complete after 1s [id=/spa-platform/poc/app3/client-secret]\n',
        )

    def test_the_stream_elides_a_user_pool_clients_terraform_id(self) -> None:
        self.assertRedacts('aws_cognito_user_pool_client.app["app3"]: Destruction complete after 1s [id=3n4b5c6d7e8f]\n',
                           'aws_cognito_user_pool_client.app["app3"]: Destruction complete after 1s [id=<elided>]\n',
                           "--stream")


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

    def test_a_p04_failure_line_stops(self) -> None:
        self.assertStops("FAIL  a PUT without If-None-Match gets 403: expected 403 AccessDenied, got succeeded\n")

    def test_a_p04_pass_line_beside_another_access_denied_stops(self) -> None:
        self.assertStops(
            "pass  a PUT without If-None-Match gets 403: 403 AccessDenied (explicit deny in a resource-based policy)\n"
            "An error occurred (AccessDenied) when calling the PutObject operation: is not authorized\n"
        )

    def test_a_p04_pass_line_that_is_not_the_whole_line_stops(self) -> None:
        self.assertStops("  pass  a PUT gets 403: 403 AccessDenied (explicit deny in a resource-based policy) "
                         "is not authorized\n")

    def test_a_p04_pass_line_whose_case_carries_access_denied_stops(self) -> None:
        self.assertStops("pass  Access Denied for account 123456789012 is not authorized: "
                         "403 AccessDenied (explicit deny in a resource-based policy)\n")

    def test_a_p04_pass_line_with_another_reason_stops(self) -> None:
        self.assertStops("pass  a PUT gets 403: 403 AccessDenied (the spend limit is reached)\n")


class P04CaseLines(Redacts):
    def test_p04s_passing_case_lines_are_posted_as_they_are(self) -> None:
        # verify-release-immutability.sh's own lines, from #63's P-04 comment of 2026-10-27; both
        # forms of the kind of policy S3 names.
        text = (
            "~~~\n"
            "pass  a second PUT with If-None-Match: * gets 412: 412 PreconditionFailed\n"
            "pass  a PUT without If-None-Match gets 403: 403 AccessDenied (explicit deny in a resource-based policy)\n"
            "pass  a plain-HTTP request is refused: 403 AccessDenied (explicit deny in a resource-based policy)\n"
            "pass  the teardown role cannot write: a PUT with If-None-Match: * to a new key gets 403: "
            "403 AccessDenied (no identity-based policy allows it)\n"
            "verify-release-immutability: pass: 23 cases as expected.\n"
            "~~~\n"
        )
        self.assertRedacts(text, text)


class Stream(Redacts):
    def test_the_stream_masks_account_ids_and_arns_and_passes_access_denied_through(self) -> None:
        self.assertRedacts(
            "taskDefinitionArn arn:aws:ecs:us-east-1:123456789012:task-definition/spa-platform-poc:7\n"
            "An error occurred (AccessDeniedException) when calling the DescribeServices operation: "
            "User: arn:aws:sts::123456789012:assumed-role/x/y is not authorized\n"
            "upload failed: Access Denied (account 123456789012)\n",
            "taskDefinitionArn task-definition/spa-platform-poc:7\n"
            "An error occurred (AccessDeniedException) when calling the DescribeServices operation: "
            "User: assumed-role/x/y is not authorized\n"
            "upload failed: Access Denied (account <account>)\n",
            "--stream",
        )

    def test_the_stream_applies_every_rule_to_each_line(self) -> None:
        self.assertRedacts('{"clientId": "3n4b5c6d7e8f9g0h"}\n> Authorization: Bearer abc\n',
                           '{"clientId": "<elided>"}\n> Authorization: Bearer <elided>\n', "--stream")

    def test_the_stream_writes_a_line_before_the_next_one_arrives(self) -> None:
        process = subprocess.Popen([sys.executable, "-I", str(SCRIPT), "--stream"],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            process.stdin.write(b"account 123456789012\n")
            process.stdin.flush()
            # A deadline, so a stream that holds the line fails here instead of hanging the run.
            ready, _, _ = select.select([process.stdout], [], [], 30)
            self.assertTrue(ready, "the stream wrote nothing within 30 seconds of a whole line")
            self.assertEqual(process.stdout.readline(), b"account <account>\n")
        finally:
            process.stdin.close()
            process.wait(timeout=60)
            process.stdout.close()
            process.stderr.close()
        self.assertEqual(process.returncode, 0)

    def test_the_stream_keeps_the_last_four_digits_on_request(self) -> None:
        self.assertRedacts("account 123456789012\n", "account <account-9012>\n",
                           "--stream", "--keep-last-four")


class Usage(unittest.TestCase):
    def test_an_unknown_option_fails_and_writes_nothing(self) -> None:
        done = redact("account 123456789012\n", "--keep-last-five")
        self.assertNotEqual(done.returncode, 0)
        self.assertNotEqual(done.returncode, ACCESS_DENIED_EXIT)
        self.assertEqual(done.stdout, b"")


if __name__ == "__main__":
    unittest.main()
