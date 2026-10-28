#!/usr/bin/env python3
"""Redact a post of the `wiz` skill: text in on stdin, redacted text out on stdout. Owner: integrator.

    python3 -I tools/wiz_redact.py [--keep-last-four] < draft > post
    <an AWS read> 2>&1 | tee -a <record> | python3 -I tools/wiz_redact.py --stream

The `wiz` skill posts each stage's evidence to its ticket from captured output, and every post goes
through this script first (#259). It used to follow a paragraph of prose, which can fail quietly;
tools/wiz_redact_tests.py holds each rule to a case. It applies eight rules, in this order:

1. A twelve-digit number, or one written 1234-5678-9012, becomes `<account>`. With
   --keep-last-four it becomes `<account-9012>`, for a ticket whose grill asks for the last four
   digits, as #125 does. Without the option no twelve-digit number survives.
2. An ARN, `arn:aws:<service>:<region>:<account>:<resource>`, becomes its resource field, the part
   after the account: `arn:aws:iam::<account>:role/x` becomes `role/x`.
3. The value after `code=`, `state=`, `client_id=`, `client_secret=`, `code_verifier=`,
   `access_token=`, `refresh_token=`, `id_token=`, `X-Amz-Security-Token=`, `X-Amz-Signature=`,
   `ClientId`, `ClientSecret`, `SessionToken` or a parameter's `Value` becomes `<elided>`. A `Value`
   beside a `Key`, which is a tag's, is kept.
4. A JSON member named `clientId`, `secret`, `SecretString`, `AccessKeyId`, `SecretAccessKey`,
   `access_token`, `refresh_token`, `id_token`, `client_secret`, `code` or `state` becomes
   `"<elided>"`, whatever its value; so does a header held as a JSON member, `Cookie`,
   `Set-Cookie`, `Authorization` or one of rule 7's, in any case.
5. In a `Cookie:` or `Set-Cookie:` line, the value of every cookie becomes `<elided>`; its name and
   its attributes (`Path`, `Expires`, `HttpOnly` and the rest) are kept.
6. The credential after `Authorization:` becomes `<elided>`, its scheme (`Bearer`, `Basic`) kept; a
   `Bearer` token of sixteen characters or more elsewhere is elided the same way.
7. The value of an `X-XSRF-TOKEN:`, `X-CSRF-TOKEN:`, `RequestVerificationToken:` or
   `X-Amz-Security-Token:` header line becomes `<elided>`.
8. The `[id=...]` Terraform prints on a line naming an `aws_cognito_user_pool_client` resource
   (`Creation complete`, `Refreshing state...`, `Destruction complete`) becomes `[id=<elided>]`: that
   id is the client id. Other resources keep theirs (#468).

It catches shapes, not meaning, so the skill still reads the result once as a reviewer would.

A post that carries `AccessDenied` or `Access Denied` is not redacted at all: AWS answering that
where it did not before is a stop of the skill's (ask about the spend limit, per
docs/agents/aws-rules.md), so the script writes nothing to stdout, one line to stderr, and exits 3.
One shape is let through (#468): a whole line of P-04's passing cases,
`pass  <case>: 403 AccessDenied (<the kind of policy>)`, as verify-release-immutability.sh prints
it, since there the refusal is the evidence. Any other line naming the refusal still stops.

--stream is the terminal's mode (#468). Each line goes through the same eight rules as it arrives
and is flushed at once, so a long-running command reads live; `AccessDenied` passes through, since
the human has to see it to stop (the skill's rule 7), and the mode always exits 0. A rule that spans
lines, a JSON value broken over several, is applied to each line alone.

A usage error exits 2. Input that is not UTF-8 passes through byte for byte where no rule matches.

Standard library only, kept 3.9-compatible (D-3 in docs/dependencies.md).
"""
from __future__ import annotations

import argparse
import os
import re
import sys
import typing

ACCESS_DENIED_EXIT = 3

ACCESS_DENIED = re.compile(r"AccessDenied|Access Denied")

# A passing case line of P-04 (tools/scripts/verify-release-immutability.sh), whole: the refusal is
# the case's subject, and its parentheses hold only the kind of policy S3 named, in one of the two
# forms the script's denial() prints.
P04_CASE = re.compile(
    r"^pass  (?:(?!AccessDenied|Access Denied)[^\n])+: 403 AccessDenied "
    r"\((?:explicit deny in an? [A-Za-z -]+ policy|no [A-Za-z -]+ policy allows it)\)$",
    re.MULTILINE,
)

# The plain form goes wherever no digit touches it, a hyphen included, because a CDK bootstrap
# bucket (cdk-hnb659fds-assets-123456789012-us-east-1) carries the account between hyphens. Only the
# console's dashed form needs the hyphen guard, so 1234-5678-9012-3456 is not read as an account.
ACCOUNT = re.compile(r"(?<!\d)(\d{12})(?!\d)|(?<![\d-])(\d{4}-\d{4}-\d{4})(?![\d-])")

# arn:<partition>:<service>:<region>:<account>:<resource>. The resource runs to whitespace, a quote,
# a comma or a closing bracket, so an ARN inside JSON or a sentence keeps what follows it.
ARN = re.compile(r"\barn:aws[\w-]*:[\w-]*:[\w-]*:[^:\s\"']*:([^\s\"',;)\]}>]+)")

# `code=` and `state=` and the like are query parameters: the value runs to the next parameter.
QUERY_KEY = re.compile(
    r"(?<![\w-])(code|state|client_id|client_secret|code_verifier|access_token|refresh_token|id_token"
    r"|X-Amz-Security-Token|X-Amz-Signature)="
    r"([^&\s\"'#`<>]+)"
)

# The AWS names, as a JSON member ("ClientId": "x"), in YAML (ClientId: x) or in the CLI's
# shorthand (ClientId=x).
AWS_KEY = re.compile(
    r"(?<![\w-])(\"?)(ClientId|ClientSecret|SessionToken|Value)\1(\s*[:=]\s*)"
    r"(\"(?:[^\"\\]|\\.)*\"|[^\s,\"'}\]]+)"
)

JSON_MEMBER = re.compile(
    r"\"(clientId|secret|SecretString|AccessKeyId|SecretAccessKey"
    r"|access_token|refresh_token|id_token|client_secret|code|state)\"\s*:\s*"
)

# The headers a captured request or response can carry as JSON members, in whatever case the
# client wrote them; the whole value goes, a list of cookies included.
TOKEN_HEADERS = r"X-XSRF-TOKEN|X-CSRF-TOKEN|RequestVerificationToken|X-Amz-Security-Token"
JSON_HEADER = re.compile(rf"\"(Set-Cookie|Cookie|Authorization|{TOKEN_HEADERS})\"\s*:\s*", re.IGNORECASE)

# A cookie header line, request or response, with or without a curl -v prefix. The attributes a
# Set-Cookie line carries are not cookies, so their names keep their values.
COOKIE_LINE = re.compile(r"(?<![\w-])((?:Set-)?Cookie\s*:[ \t]*)([^\r\n]*)", re.IGNORECASE)
COOKIE_ATTRIBUTES = frozenset(
    ("path", "domain", "expires", "max-age", "samesite", "secure", "httponly", "partitioned", "priority")
)
COOKIE_PAIR = re.compile(r"([^;=\s][^;=]*)=([^;]*)")

# `Authorization: Bearer abc` keeps its scheme and elides the credential; a bare `Bearer <token>` of
# sixteen characters or more goes the same way, which leaves the words "Bearer token" alone.
AUTHORIZATION = re.compile(r"(?<![\w-])(Authorization\s*:[ \t]*)(?:([A-Za-z]+)[ \t]+)?([^\s]+)", re.IGNORECASE)
BEARER = re.compile(r"(?<![\w-])(Bearer[ \t]+)[A-Za-z0-9._~+/=-]{16,}")

TOKEN_HEADER = re.compile(rf"(?<![\w-])((?:{TOKEN_HEADERS})\s*:[ \t]*)[^\s]+", re.IGNORECASE)

# Terraform's progress line for a user pool client, whose resource id is the client id.
TERRAFORM_CLIENT_ID = re.compile(r"(aws_cognito_user_pool_client[^\n]*\[id=)[^\]\n]+(\])")


def account(keep_last_four: bool) -> typing.Callable[[re.Match], str]:
    def replace(match: re.Match) -> str:
        if keep_last_four:
            return f"<account-{match.group(0)[-4:]}>"
        return "<account>"
    return replace


def in_a_tag(text: str, start: int) -> bool:
    """Whether the `Value` at start shares its object, or its shorthand list, with a `Key`.

    The window stops at any bracket, so a `Key` inside an object nested beside the `Value` (a
    parameter carrying "Tags": [{"Key": ...}]) does not count as the `Value`'s own.
    """
    opened = max(text.rfind(c, 0, start) for c in "{}[]\n")
    closes = [i for i in (text.find(c, start) for c in "{}[]\n") if i != -1]
    closed = min(closes) if closes else len(text)
    return re.search(r"(?<![\w-])\"?Key\"?\s*[:=]", text[opened + 1:closed]) is not None


def aws_key(text: str) -> str:
    def replace(match: re.Match) -> str:
        if match.group(2) == "Value" and in_a_tag(text, match.start()):
            return match.group(0)
        value = match.group(4)
        elided = '"<elided>"' if value.startswith('"') else "<elided>"
        return match.group(0)[: match.start(4) - match.start()] + elided
    return AWS_KEY.sub(replace, text)


def cookie_line(match: re.Match) -> str:
    def pair(found: re.Match) -> str:
        if found.group(1).strip().lower() in COOKIE_ATTRIBUTES:
            return found.group(0)
        return f"{found.group(1)}=<elided>"
    return match.group(1) + COOKIE_PAIR.sub(pair, match.group(2))


def authorization(match: re.Match) -> str:
    scheme = f"{match.group(2)} " if match.group(2) else ""
    return f"{match.group(1)}{scheme}<elided>"


def json_value_end(text: str, start: int) -> int:
    """The index just past the JSON value at start: a string, an object or array, or a scalar."""
    if start >= len(text):
        return start
    if text[start] == '"':
        i = start + 1
        while i < len(text):
            if text[i] == "\\":
                i += 2
                continue
            if text[i] == '"':
                return i + 1
            i += 1
        return len(text)
    if text[start] in "{[":
        depth, i = 0, start
        while i < len(text):
            c = text[i]
            if c == '"':
                i = json_value_end(text, i)
                continue
            if c in "{[":
                depth += 1
            elif c in "}]":
                depth -= 1
                if depth == 0:
                    return i + 1
            i += 1
        return len(text)
    match = re.compile(r"[^\s,}\]]*").match(text, start)
    return match.end()


def json_members(text: str, member: re.Pattern) -> str:
    out, pos = [], 0
    for match in member.finditer(text):
        if match.start() < pos:
            continue
        end = json_value_end(text, match.end())
        out.append(text[pos:match.end()])
        out.append('"<elided>"')
        pos = end
    out.append(text[pos:])
    return "".join(out)


def redact(text: str, keep_last_four: bool = False) -> str:
    text = ACCOUNT.sub(account(keep_last_four), text)
    text = ARN.sub(lambda m: m.group(1), text)
    text = QUERY_KEY.sub(lambda m: f"{m.group(1)}=<elided>", text)
    text = aws_key(text)
    text = json_members(text, JSON_MEMBER)
    text = json_members(text, JSON_HEADER)
    text = COOKIE_LINE.sub(cookie_line, text)
    text = AUTHORIZATION.sub(authorization, text)
    text = BEARER.sub(lambda m: f"{m.group(1)}<elided>", text)
    text = TOKEN_HEADER.sub(lambda m: f"{m.group(1)}<elided>", text)
    text = TERRAFORM_CLIENT_ID.sub(r"\1<elided>\2", text)
    return text


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(
        prog="wiz_redact.py",
        description="Redact a wiz post: text on stdin, redacted text on stdout.",
    )
    parser.add_argument(
        "--keep-last-four", action="store_true",
        help="keep the last four digits of a twelve-digit number, as <account-1234>",
    )
    parser.add_argument(
        "--stream", action="store_true",
        help="redact line by line for the terminal, flushing each line, and pass AccessDenied through",
    )
    args = parser.parse_args(argv)

    if args.stream:
        try:
            for line in sys.stdin.buffer:
                text = line.decode("utf-8", "surrogateescape")
                sys.stdout.buffer.write(redact(text, args.keep_last_four).encode("utf-8", "surrogateescape"))
                sys.stdout.buffer.flush()
        except BrokenPipeError:
            # A reader such as `head` went away; the record already holds the whole output. Point
            # stdout at /dev/null so the flush at exit does not raise again.
            os.dup2(os.open(os.devnull, os.O_WRONLY), sys.stdout.fileno())
        return 0

    text = sys.stdin.buffer.read().decode("utf-8", "surrogateescape")
    if ACCESS_DENIED.search(P04_CASE.sub("", text)):
        sys.stderr.write(
            "wiz_redact: the input carries an AWS AccessDenied answer; nothing was written. "
            "Stop the sitting and ask about the spend limit (docs/agents/aws-rules.md).\n"
        )
        return ACCESS_DENIED_EXIT
    sys.stdout.buffer.write(redact(text, args.keep_last_four).encode("utf-8", "surrogateescape"))
    sys.stdout.buffer.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
