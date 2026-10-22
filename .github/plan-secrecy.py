#!/usr/bin/env python3
"""Keep plan generation disabled: no decision admits a plan pipeline (ADR-0016 decision 4).

CR 1.1: Terraform plans contain plaintext credentials even when marked sensitive:
https://developer.hashicorp.com/terraform/cli/commands/plan#out-filename
https://developer.hashicorp.com/terraform/cli/commands/show#json-output

This conservative source check is a regression guard for accidental CI edits, not a
shell interpreter or a sandbox against deliberately obfuscated code. Plan policy needs
no plan file: P-01 to P-03 are mock-provider assertions in terraform test (ADR-0016).
Do not enable plan/show by relaxing a path regex: review the full producer, consumer,
logging and artifact flow when a new decision admits a plan pipeline.
"""

from __future__ import annotations

import pathlib
import re

# Include helper scripts and local composite actions, not only the placeholder path.
SOURCE_SUFFIXES = {".sh", ".bash", ".yml", ".yaml"}
CACHE = re.compile(r"actions/cache(?:/save|/restore)?\s*@", re.IGNORECASE)
UPLOAD = re.compile(r"actions/upload-artifact\s*@", re.IGNORECASE)
TERRAFORM = re.compile(r"\b(?:terraform|tofu)\b[^\n;|&]*\b(?:plan|show)\b")


def targets(root: pathlib.Path) -> list[pathlib.Path]:
    return sorted(
        path for directory in (root / ".github", root / "tools")
        for path in directory.rglob("*")
        if path.is_file() and path.suffix in SOURCE_SUFFIXES
    )


def check(root: pathlib.Path) -> list[str]:
    problems: list[str] = []
    for path in targets(root):
        rel = path.relative_to(root).as_posix()
        # Join shell continuations so splitting a command cannot bypass the rule.
        source = re.sub(r"\\\r?\n", " ", path.read_text())
        lines = source.splitlines()
        for n, line in enumerate(lines, 1):
            if line.lstrip().startswith("#"):
                continue
            # Quotes around action values and commands are legal YAML/shell syntax.
            normalized = line.replace('"', "").replace("'", "")
            if UPLOAD.search(normalized):
                problems.append(f"{rel}:{n}: artifact uploads require a reviewed plan-secrecy policy")
            if CACHE.search(normalized):
                indent = len(line) - len(line.lstrip())
                block = []
                for following in lines[n:]:
                    if following.strip() and len(following) - len(following.lstrip()) <= indent:
                        break
                    block.append(following)
                # The sole current cache stores dependencies, not the workspace or runner temp.
                # Multiline/dynamic paths and any new cache need an explicit review (CR 1.1).
                paths = [entry.strip() for entry in block if entry.strip().startswith("path:")]
                if rel != ".github/workflows/ci.yml" or paths != ["path: ~/.nuget/packages"]:
                    problems.append(f"{rel}:{n}: only the reviewed NuGet dependency cache is allowed")
            if TERRAFORM.search(normalized):
                problems.append(
                    f"{rel}:{n}: plan/show is disabled: a credentialed plan pipeline needs a new "
                    "decision (ADR-0016 decision 4) and a review of its producer, policy consumer "
                    "and logging/artifact controls (CR 1.1)"
                )
    return problems


def main() -> int:
    root = pathlib.Path(__file__).resolve().parent.parent
    problems = check(root)
    for problem in problems:
        print(problem)
    if problems:
        print("PLAN SECRECY FAILED: review the complete plan pipeline before enabling it.")
        return 1
    print("plan secrecy: no direct plan/show commands or artifact uploads in checked CI sources")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
