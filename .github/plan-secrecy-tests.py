#!/usr/bin/env python3
"""CR 1.1 mutation controls exercise real source files read by the CI guard."""

import importlib.util
import pathlib
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("plan_secrecy", pathlib.Path(__file__).with_name("plan-secrecy.py"))
policy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(policy)


class PlanSecrecyTests(unittest.TestCase):
    def check_source(self, source, path=".github/workflows/test.yml"):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            target = root / path
            target.parent.mkdir(parents=True)
            target.write_text(source)
            return policy.check(root)

    def test_mutations_are_rejected(self):
        for source in (
            'uses: actions/upload-artifact@v4',
            'uses: actions/cache@v4\n  with:\n    path: .',
            'uses: actions/cache/save@v4\n  with:\n    path: $RUNNER_TEMP',
            'eval \"terraform plan -out=plan\"',
            'uses: "actions/upload-artifact@v4"',
            "uses: 'actions/upload-artifact@v4'",
            'terraform plan -out=plan.tfplan',
            'terraform plan -out="$RUNNER_TEMPevil/plan"',
            'terraform plan -out="$RUNNER_TEMP/../plan"',
            'terraform plan -out="${{ runner.temp }}/plan"',
            'terraform show -json plan.tfplan',
            'terraform show -json plan.tfplan | tee plan.json',
            'terraform show -json plan.tfplan > plan.json',
            'terraform \\\n  -chdir=infra plan -out=plan',
            '"terraform" plan -out=plan',
            'tofu plan -out=plan',
        ):
            with self.subTest(source=source):
                self.assertTrue(self.check_source(source))

    def test_helpers_and_composite_actions_are_checked(self):
        for path in ("tools/scripts/plan-policy.sh", "tools/helper.sh", ".github/actions/plan/action.yaml"):
            with self.subTest(path=path):
                self.assertTrue(self.check_source("terraform show -json plan", path))

    def test_positive_controls(self):
        for source in (
            "# terraform plan -out=example\nrun: terraform validate",
            "run: terraform fmt -check -recursive infra",
            "run: terraform init -backend=false",
            "run: terraform test",
            "run: python3 -I .github/plan-secrecy.py",
        ):
            with self.subTest(source=source):
                self.assertEqual([], self.check_source(source))

    def test_nuget_cache_exception_is_narrow(self):
        safe = "- uses: actions/cache@v4\n  with:\n    path: ~/.nuget/packages"
        self.assertEqual([], self.check_source(safe, ".github/workflows/ci.yml"))
        for source in (safe.replace("~/.nuget/packages", "."),
                       safe.replace("~/.nuget/packages", "|\n      ~/.nuget/packages\n      .")):
            self.assertTrue(self.check_source(source, ".github/workflows/ci.yml"))
        self.assertTrue(self.check_source(safe))

    def test_startup_bench_upload_exception_is_narrow(self):
        # The one admitted upload: the startup benchmark's own files, from its own directory under the
        # runner's temp, in its own workflow (#576; ADR-0033 -> Decision, the reporting). The same
        # step with any other path, a second path, or in any other file is refused as before.
        safe = ("- uses: actions/upload-artifact@v4\n  if: always()\n  with:\n"
                "    name: startup-bench-${{ github.run_id }}\n    path: ${{ runner.temp }}/startup-bench/\n")
        self.assertEqual([], self.check_source(safe, ".github/workflows/startup-bench.yml"))
        for source in (safe.replace("${{ runner.temp }}/startup-bench/", "."),
                       safe.replace("${{ runner.temp }}/startup-bench/", "${{ runner.temp }}"),
                       safe.replace("${{ runner.temp }}/startup-bench/", "|\n      ${{ runner.temp }}/startup-bench/\n      infra"),
                       safe.replace("    path: ${{ runner.temp }}/startup-bench/\n", "")):
            with self.subTest(source=source):
                self.assertTrue(self.check_source(source, ".github/workflows/startup-bench.yml"))
        self.assertTrue(self.check_source(safe))
        self.assertTrue(self.check_source(safe, ".github/workflows/ci.yml"))

    def test_repository_has_sources_and_is_safe(self):
        root = pathlib.Path(__file__).resolve().parent.parent
        self.assertIn(root / ".github/workflows/ci.yml", policy.targets(root))
        self.assertEqual([], policy.check(root))


if __name__ == "__main__":
    unittest.main()
