#!/usr/bin/env python3
"""Tests for tools/analysis_guard.py. Owner: integrator. Built in #606.

Run: python3 -I tools/analysis_guard_tests.py

**What runs is the script itself**, as `python3 -I tools/analysis_guard.py`, in a throwaway
repository whose tracked files are the analyzer configs and C# sources a case writes. Each case
names one failure the guard reports (ADR-0035 decisions 5, 12 and 13; #589-Q8, #594-Q7, X4), and
the first case runs the guard on this checkout, which must pass as it stands.
"""
from __future__ import annotations

import os
import pathlib
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "tools" / "analysis_guard.py"

RELAXED_HEADER = "[{tests,fixtures,tools/spactl,tools/bench/floor,src/Platform.Testing}/**.cs]"

# An .editorconfig the guard passes: the style section, then the relaxed tier's section last.
EDITORCONFIG = f"""root = true

[*]
indent_size = 4

[*.cs]
csharp_style_namespace_declarations = file_scoped

{RELAXED_HEADER}
# Test names use underscores as sentences.
dotnet_diagnostic.CA1707.severity = none
dotnet_diagnostic.CA1062.severity = none # xUnit supplies public test methods' arguments.
"""

GLOBALCONFIG = """is_global = true
dotnet_diagnostic.CA2007.severity = none
dotnet_diagnostic.CA5394.severity = suggestion # pending #611
"""


class Guard(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.repo = pathlib.Path(self.tmp.name)
        self.git("init", "-q")
        self.files: dict[str, str] = {".editorconfig": EDITORCONFIG, ".globalconfig": GLOBALCONFIG,
                                      "src/App/App.cs": "namespace App;\npublic sealed class App { }\n"}

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def git(self, *args: str) -> None:
        subprocess.run(["git", "-C", str(self.repo), *args], check=True, capture_output=True)

    def run_guard(self, cwd: pathlib.Path | None = None) -> subprocess.CompletedProcess[str]:
        for path, text in self.files.items():
            target = self.repo / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(text)
        self.git("add", "-A")
        env = {"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "HOME": str(self.repo)}
        return subprocess.run([sys.executable, "-I", str(SCRIPT)], cwd=cwd or self.repo, env=env,
                              capture_output=True, text=True)

    def assertPasses(self, result: subprocess.CompletedProcess[str]) -> None:
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def assertFails(self, result: subprocess.CompletedProcess[str], *fragments: str) -> None:
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        for fragment in fragments:
            self.assertIn(fragment, result.stderr)

    # --- the tree as it stands, and a clean one ------------------------------------------------

    def test_this_checkout_passes(self) -> None:
        result = subprocess.run([sys.executable, "-I", str(SCRIPT)], cwd=ROOT, capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_a_clean_tree_passes(self) -> None:
        self.assertPasses(self.run_guard())

    def test_it_runs_from_a_subdirectory_against_the_top_level(self) -> None:
        self.files["src/App/GlobalSuppressions.cs"] = "// nothing\n"
        (self.repo / "src").mkdir()
        self.assertFails(self.run_guard(cwd=self.repo / "src"), "src/App/GlobalSuppressions.cs")

    # --- the relaxed tier's section (#589-Q8, ADR-0035 decision 13) ----------------------------

    def test_the_relaxed_section_missing_fails(self) -> None:
        self.files[".editorconfig"] = "root = true\n\n[*.cs]\ncsharp_style_namespace_declarations = file_scoped\n"
        self.assertFails(self.run_guard(), "no relaxed-tier section", RELAXED_HEADER)

    def test_a_security_id_in_the_relaxed_section_fails(self) -> None:
        self.files[".editorconfig"] = EDITORCONFIG + "# The fake S3 server's ETag is MD5.\ndotnet_diagnostic.CA5351.severity = none\n"
        self.assertFails(self.run_guard(), ".editorconfig:14", "CA5351", "security rule")

    def test_a_package_security_id_in_the_relaxed_section_fails(self) -> None:
        self.files[".editorconfig"] = EDITORCONFIG + "# Regexes in tests are trusted.\ndotnet_diagnostic.MA0009.severity = none\n"
        self.assertFails(self.run_guard(), "MA0009", "security rule")

    def test_the_aspnet_security_id_in_the_relaxed_section_fails(self) -> None:
        self.files[".editorconfig"] = EDITORCONFIG + "# A reason.\ndotnet_diagnostic.ASP0026.severity = none\n"
        self.assertFails(self.run_guard(), "ASP0026", "security rule")

    def test_a_security_id_in_lower_case_still_fails(self) -> None:
        self.files[".editorconfig"] = EDITORCONFIG + "# A reason.\ndotnet_diagnostic.ca5394.severity = none\n"
        self.assertFails(self.run_guard(), "CA5394", "security rule")

    def test_a_usage_id_with_the_security_prefix_shape_passes(self) -> None:
        # Decision 4: a security rule is one in the Security category, not a CA5xxx-looking prefix.
        self.files[".editorconfig"] = EDITORCONFIG + "# Tests address relative paths as strings.\ndotnet_diagnostic.CA2234.severity = none\n"
        self.assertPasses(self.run_guard())

    def test_an_entry_without_a_reason_fails(self) -> None:
        self.files[".editorconfig"] = EDITORCONFIG + "\ndotnet_diagnostic.CA2234.severity = none\n"
        self.assertFails(self.run_guard(), ".editorconfig:14", "CA2234", "no reason comment")

    def test_a_reason_above_a_blank_line_is_not_its_reason(self) -> None:
        self.files[".editorconfig"] = EDITORCONFIG + "# A reason for something else.\n\ndotnet_diagnostic.CA2234.severity = none\n"
        self.assertFails(self.run_guard(), "CA2234", "no reason comment")

    def test_an_empty_comment_is_no_reason(self) -> None:
        self.files[".editorconfig"] = EDITORCONFIG + "dotnet_diagnostic.CA2234.severity = none #\n"
        self.assertFails(self.run_guard(), "CA2234", "no reason comment")

    def test_a_later_cs_section_fails(self) -> None:
        self.files[".editorconfig"] = EDITORCONFIG + "\n[*.cs]\ncsharp_style_var_elsewhere = false\n"
        self.assertFails(self.run_guard(), "not the last section", "[*.cs]")

    def test_a_later_catch_all_section_fails(self) -> None:
        self.files[".editorconfig"] = EDITORCONFIG + "\n[*]\nindent_size = 2\n"
        self.assertFails(self.run_guard(), "not the last section", "[*]")

    def test_a_later_section_for_one_relaxed_folder_fails(self) -> None:
        self.files[".editorconfig"] = EDITORCONFIG + "\n[tests/**/*.{cs,csx}]\ndotnet_diagnostic.CA1000.severity = none\n"
        self.assertFails(self.run_guard(), "not the last section", "[tests/**/*.{cs,csx}]")

    def test_a_later_section_that_matches_no_cs_file_passes(self) -> None:
        self.files[".editorconfig"] = EDITORCONFIG + "\n[*.{md,json}]\nindent_size = 2\n"
        self.assertPasses(self.run_guard())

    # --- category lines (X4, ADR-0035 decision 5) ----------------------------------------------

    def test_a_category_line_in_the_globalconfig_fails(self) -> None:
        self.files[".globalconfig"] = GLOBALCONFIG + "dotnet_analyzer_diagnostic.category-Security.severity = error\n"
        self.assertFails(self.run_guard(), ".globalconfig:4", "category")

    def test_a_category_line_in_the_editorconfig_fails(self) -> None:
        self.files[".editorconfig"] = EDITORCONFIG.replace(
            "[*.cs]\n", "[*.cs]\ndotnet_analyzer_diagnostic.category-Style.severity = warning\n")
        self.assertFails(self.run_guard(), ".editorconfig:7", "category")

    def test_a_category_line_in_a_nested_editorconfig_fails(self) -> None:
        self.files["src/App/.editorconfig"] = "[*.cs]\ndotnet_analyzer_diagnostic.category-Design.severity = none\n"
        self.assertFails(self.run_guard(), "src/App/.editorconfig:2", "category")

    def test_a_category_line_in_a_named_globalconfig_fails(self) -> None:
        self.files["build/strict.globalconfig"] = "is_global = true\ndotnet_analyzer_diagnostic.category-Naming.severity = none\n"
        self.assertFails(self.run_guard(), "build/strict.globalconfig:2", "category")

    def test_a_category_line_that_is_commented_out_passes(self) -> None:
        self.files[".globalconfig"] = GLOBALCONFIG + "# dotnet_analyzer_diagnostic.category-Security.severity = error\n"
        self.assertPasses(self.run_guard())

    # --- [SuppressMessage] (ADR-0035 decision 12) -----------------------------------------------

    def test_a_suppress_message_without_a_justification_fails(self) -> None:
        self.files["src/App/Jitter.cs"] = ('namespace App;\n'
                                          '[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394")]\n'
                                          'public sealed class Jitter { }\n')
        self.assertFails(self.run_guard(), "src/App/Jitter.cs:2", "Justification")

    def test_a_justification_citing_no_id_fails(self) -> None:
        self.files["src/App/Jitter.cs"] = ('namespace App;\n'
                                          '[SuppressMessage("Security", "CA5394", Justification = "Jitter needs no CSPRNG.")]\n'
                                          'public sealed class Jitter { }\n')
        self.assertFails(self.run_guard(), "src/App/Jitter.cs:2", "cites no id")

    def test_a_justification_citing_each_kind_of_id_passes(self) -> None:
        for citation in ("#611", "ADR-0035", "S-12", "R-A1", "F-03", "N-16", "P-04", "A-04"):
            with self.subTest(citation=citation):
                self.files["src/App/Jitter.cs"] = (
                    'namespace App;\n'
                    f'[SuppressMessage("Security", "CA5394", Justification = "Jitter, not a secret: {citation}.")]\n'
                    'public sealed class Jitter { }\n')
                self.assertPasses(self.run_guard())

    def test_a_multi_line_suppress_message_is_read_whole(self) -> None:
        self.files["src/App/Jitter.cs"] = ('namespace App;\n'
                                          '[SuppressMessage(\n'
                                          '    "Security",\n'
                                          '    "CA5394:Do not use insecure randomness",\n'
                                          '    Justification = "Retry jitter, no secret (#611).")]\n'
                                          'public sealed class Jitter { }\n')
        self.assertPasses(self.run_guard())

    def test_a_multi_line_suppress_message_without_a_citation_names_its_first_line(self) -> None:
        self.files["src/App/Jitter.cs"] = ('namespace App;\n'
                                          'public sealed class Jitter\n'
                                          '{\n'
                                          '    [SuppressMessageAttribute(\n'
                                          '        "Security", "CA5394",\n'
                                          '        Justification = "Retry jitter.")]\n'
                                          '    public int Next() => 4;\n'
                                          '}\n')
        self.assertFails(self.run_guard(), "src/App/Jitter.cs:4", "cites no id")

    def test_an_id_in_the_check_id_is_not_a_citation(self) -> None:
        # "CA5394" names the finding; the citation is what makes the code right, in the Justification.
        self.files["src/App/Jitter.cs"] = ('namespace App;\n'
                                          '[SuppressMessage("Security", "CA5394 #611", Justification = "Jitter.")]\n'
                                          'public sealed class Jitter { }\n')
        self.assertFails(self.run_guard(), "cites no id")

    def test_the_word_in_a_comment_or_string_is_not_an_attribute(self) -> None:
        self.files["src/App/Notes.cs"] = ('namespace App;\n'
                                         '// A [SuppressMessage] needs a Justification.\n'
                                         'public static class Notes { public const string Text = "SuppressMessage(x)"; }\n')
        self.assertPasses(self.run_guard())

    # --- #pragma warning disable (ADR-0035 decision 12) -----------------------------------------

    def test_a_pragma_without_a_citation_fails(self) -> None:
        self.files["src/App/Hash.cs"] = ('namespace App;\n'
                                        '#pragma warning disable CA5351 // S3 needs it\n'
                                        'public sealed class Hash { }\n'
                                        '#pragma warning restore CA5351\n')
        self.assertFails(self.run_guard(), "src/App/Hash.cs:2", "#pragma warning disable")

    def test_a_pragma_citing_an_id_on_its_line_passes(self) -> None:
        self.files["src/App/Hash.cs"] = ('namespace App;\n'
                                        '#pragma warning disable CA5351 // S3\'s ETag is MD5 (#611)\n'
                                        'public sealed class Hash { }\n'
                                        '#pragma warning restore CA5351\n')
        self.assertPasses(self.run_guard())

    def test_a_citation_on_the_line_above_a_pragma_is_not_on_its_line(self) -> None:
        self.files["src/App/Hash.cs"] = ('namespace App;\n'
                                        '// #611\n'
                                        '  #pragma   warning disable CA5351\n'
                                        'public sealed class Hash { }\n')
        self.assertFails(self.run_guard(), "src/App/Hash.cs:3")

    # --- GlobalSuppressions.cs (#594-Q7) --------------------------------------------------------

    def test_a_global_suppressions_file_fails(self) -> None:
        self.files["tests/App.Tests/GlobalSuppressions.cs"] = (
            '[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707", Justification = "#606")]\n')
        self.assertFails(self.run_guard(), "tests/App.Tests/GlobalSuppressions.cs", "GlobalSuppressions.cs")

    # --- what the guard does not read -----------------------------------------------------------

    def test_spikes_are_outside_the_solution_and_not_read(self) -> None:
        self.files["spikes/g0/GlobalSuppressions.cs"] = "// a spike\n"
        self.files["spikes/g0/Program.cs"] = "#pragma warning disable CS0168\n"
        self.files["spikes/g0/.editorconfig"] = "[*.cs]\ndotnet_analyzer_diagnostic.category-Style.severity = none\n"
        self.assertPasses(self.run_guard())

    def test_an_untracked_file_is_not_read(self) -> None:
        result = self.run_guard()
        (self.repo / "src" / "App" / "GlobalSuppressions.cs").write_text("// untracked\n")
        env = {"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "HOME": str(self.repo)}
        again = subprocess.run([sys.executable, "-I", str(SCRIPT)], cwd=self.repo, env=env,
                               capture_output=True, text=True)
        self.assertPasses(result)
        self.assertPasses(again)

    def test_every_failure_is_named_before_the_verdict(self) -> None:
        self.files["src/App/GlobalSuppressions.cs"] = "// one\n"
        self.files["src/App/Hash.cs"] = "#pragma warning disable CA5351\n"
        result = self.run_guard()
        self.assertFails(result, "src/App/GlobalSuppressions.cs", "src/App/Hash.cs:1")
        self.assertIn("2 findings", result.stderr.splitlines()[-1])

    def test_outside_a_repository_it_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as bare:
            env = {"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "HOME": bare,
                   "GIT_CEILING_DIRECTORIES": str(pathlib.Path(bare).parent)}
            result = subprocess.run([sys.executable, "-I", str(SCRIPT)], cwd=bare, env=env,
                                    capture_output=True, text=True)
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertIn("not judged", result.stderr)


if __name__ == "__main__":
    unittest.main()
