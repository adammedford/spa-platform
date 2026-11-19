#!/usr/bin/env python3
"""A build without the analyzer configs stops on the target that names the missing one (#1369).
Owner: integrator.

Directory.Build.props runs the analysis wherever it is read (ADR-0035 decisions 5 and 14). Without
.editorconfig and .globalconfig beside it, every rule would run at error with none of the
exceptions, and whether that build failed would rest on the source of the day tripping a rule.
Q19 of Overnight 2026-11-01 (#706) asked for an explicit check instead, proved with each file
removed in turn; #1369's decision took it. The props' `_SpaRequireAnalyzerConfigs` target runs
before the compile and raises an MSBuild error naming the file that is not there.

Each case copies the build files below into a throwaway tree outside the checkout, so no
`.editorconfig` or `Directory.Build.props` above it is read, adds one strict-tier project, and runs
`dotnet build` on it. With both configs the build passes; with one removed it stops on the target's
error naming that file, and on no other error, because the target runs before the compile and
nothing the analyzers say is reached. The restore reads the analyzer packages Directory.Build.props
references from the machine's package cache, which a build of the solution has filled; CI runs this
companion in its Build and test job, after that job's build.

Every case reads the checkout and none reads a script of its own. The line below names the files
BUILD_FILES copies, for tools/check.sh, whose full mode runs this companion when a change touches
one (#354).

Checkout inputs: Directory.Build.props Directory.Packages.props global.json nuget.config .editorconfig .globalconfig BannedSymbols.*.txt

Run: python3 -I tools/analyzer_configs_build_tests.py
"""

from __future__ import annotations

import fnmatch
import os
import pathlib
import re
import shutil
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent

CONFIGS = (".editorconfig", ".globalconfig")

# What a build of one strict-tier project reads from the top level of the checkout.
BUILD_FILES = ("Directory.Build.props", "Directory.Packages.props", "global.json", "nuget.config",
               *CONFIGS, *sorted(p.name for p in ROOT.glob("BannedSymbols.*.txt")))

# A strict-tier project (ADR-0035 decision 3; Directory.Build.props's SpaRelaxedTier says which folders
# are not), so the banned-symbol lists apply too, as they do to the shipped projects.
PROJECT = "src/Probe/Probe.csproj"
PROJECT_TEXT = '<Project Sdk="Microsoft.NET.Sdk">\n</Project>\n'
SOURCE = "src/Probe/Marker.cs"
SOURCE_TEXT = "namespace Probe;\n\ninternal static class Marker\n{\n}\n"

# An MSBuild error line: `<origin>: error <code>: <text> [<project>]`, the code optional. An Error
# task given no File logs the project file it runs in, which for a task in an imported file is that
# file, at the task's line and column; the control and the removal cases measured it on #1369's
# first run. https://learn.microsoft.com/visualstudio/msbuild/error-task
ERROR = re.compile(r":\s*error\b")


def build(tree: pathlib.Path) -> tuple[int, list[str], str]:
    """`dotnet build` of the probe project: its exit status, its distinct error lines, its output."""
    env = {**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1",
           "MSBUILDDISABLENODEREUSE": "1", "DOTNET_CLI_UI_LANGUAGE": "en"}
    result = subprocess.run(["dotnet", "build", str(tree / PROJECT), "-tl:off", "-nodeReuse:false",
                             "-p:UseSharedCompilation=false"],
                            cwd=tree, env=env, capture_output=True, text=True, check=False)
    output = result.stdout + result.stderr
    errors = sorted({line.strip() for line in output.splitlines() if ERROR.search(line)})
    return result.returncode, errors, output


class ABuildWithoutAnAnalyzerConfigStops(unittest.TestCase):

    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        # Resolved: on macOS the temporary folder is a link, and the compiler read each source at its
        # real path, which an .editorconfig at the link's path did not cover; the control failed on
        # IDE0160 with both configs present until the tree was resolved (#1369's first run).
        self.tree = pathlib.Path(self.tmp.name).resolve()
        for name in BUILD_FILES:
            shutil.copyfile(ROOT / name, self.tree / name)
        for path, text in ((PROJECT, PROJECT_TEXT), (SOURCE, SOURCE_TEXT)):
            (self.tree / path).parent.mkdir(parents=True, exist_ok=True)
            (self.tree / path).write_text(text, encoding="utf-8")

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def test_with_both_configs_the_build_passes(self):
        # The control: a red case below means the target fired, not that the tree cannot restore or
        # build at all.
        status, errors, output = build(self.tree)
        self.assertEqual([], errors, output)
        self.assertEqual(0, status, output)

    def test_each_config_removed_in_turn_stops_the_build_on_the_target_naming_it(self):
        for missing in CONFIGS:
            with self.subTest(missing=missing):
                (self.tree / missing).unlink()
                try:
                    status, errors, output = build(self.tree)
                finally:
                    shutil.copyfile(ROOT / missing, self.tree / missing)
                self.assertNotEqual(0, status, output)
                self.assertTrue(errors, output)
                present = next(name for name in CONFIGS if name != missing)
                for line in errors:
                    self.assertIn(f"{self.tree / 'Directory.Build.props'}(", line, output)
                    self.assertIn(str(self.tree / missing), line, output)
                    self.assertNotIn(present, line, output)


class TheCheckoutInputsLineCoversWhatItCopies(unittest.TestCase):
    """tools/check.sh runs this companion on a change to a file its Checkout inputs line names, so a
    file BUILD_FILES copies and the line leaves out would change with this companion unrun."""

    def test_every_copied_file_is_declared(self):
        text = pathlib.Path(__file__).read_text(encoding="utf-8")
        lines = [line for line in text.splitlines() if line.startswith("Checkout inputs:")]
        self.assertEqual(1, len(lines), "the docstring has no single Checkout inputs line")
        patterns = lines[0].removeprefix("Checkout inputs:").split()
        self.assertEqual([], [name for name in BUILD_FILES
                              if not any(fnmatch.fnmatchcase(name, p) for p in patterns)])


if __name__ == "__main__":
    unittest.main()
