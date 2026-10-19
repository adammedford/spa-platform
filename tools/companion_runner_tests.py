#!/usr/bin/env python3
"""Test companion for tools/companion_runner.py, the runner the script test companions use (#107).

Owner: integrator.

The runner decides what every companion that uses it reports, so a fault in it is a fault in all of
them at once: a failure it loses turns a mutation proof green. Each case here builds a throwaway
module of test cases built to trip one rule, runs it through the runner, and reads what the runner
reported. These cases run under unittest's own runner, not the one under test.

Run: python3 -I tools/companion_runner_tests.py
"""

from __future__ import annotations

import collections
import importlib.util
import io
import itertools
import os
import pathlib
import re
import subprocess
import sys
import tempfile
import threading
import time
import types
import unittest

ROOT = pathlib.Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("companion_runner", ROOT / "tools" / "companion_runner.py")
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)

NAMES = itertools.count()


class RunnerTestCase(unittest.TestCase):
    def module(self, **members) -> types.ModuleType:
        """A throwaway module, registered for the run, holding `members`: test case classes made
        with `cases`, and setUpModule or tearDownModule when a case needs them."""
        module = types.ModuleType(f"companion_runner_tests_module_{next(NAMES)}")
        sys.modules[module.__name__] = module
        self.addCleanup(sys.modules.pop, module.__name__)
        for name, member in members.items():
            if isinstance(member, type):
                member.__module__ = module.__name__
            setattr(module, name, member)
        return module

    def run_module(self, module: types.ModuleType, jobs: int = 4, stream: io.StringIO | None = None,
                   **options):
        """Runs every case in `module` through the runner; returns the result and what it printed."""
        stream = stream or io.StringIO()
        suite = unittest.defaultTestLoader.loadTestsFromModule(module)
        result = runner.Runner(stream=stream, verbosity=2, jobs=jobs, **options).run(suite)
        return result, stream.getvalue()


class SlowStream(io.StringIO):
    """A stream that takes its time over every write, so the main thread, replaying, falls behind
    the pool's threads."""

    def write(self, text: str) -> int:
        time.sleep(0.05)
        return super().write(text)


def cases(name: str, **methods) -> type:
    """A unittest.TestCase subclass called `name`, whose test methods are `methods`."""
    return type(name, (unittest.TestCase,), methods)


class ReportingTests(RunnerTestCase):
    def test_a_passing_case_is_run_and_reported(self):
        ran = []
        module = self.module(Passing=cases("Passing", test_passes=lambda self: ran.append(1)))
        result, printed = self.run_module(module)
        self.assertEqual([1], ran)
        self.assertEqual(1, result.testsRun)
        self.assertTrue(result.wasSuccessful(), printed)
        self.assertIn("test_passes", printed)

    def test_a_failure_an_error_and_a_failing_subtest_are_each_reported(self):
        def failing_subtest(self):
            for number in (1, 2, 3):
                with self.subTest(number=number):
                    self.assertNotEqual(2, number)

        module = self.module(Broken=cases(
            "Broken",
            test_fails=lambda self: self.fail("the assertion"),
            test_raises=lambda self: {}["the error"],
            test_fails_one_subtest=failing_subtest,
            test_passes=lambda self: None))
        result, printed = self.run_module(module)
        self.assertEqual(4, result.testsRun)
        self.assertFalse(result.wasSuccessful())
        self.assertEqual(["test_fails", "test_fails_one_subtest (number=2)"],
                         sorted(test.id().split(".")[-1] for test, _ in result.failures))
        self.assertEqual(["test_raises"], [test.id().split(".")[-1] for test, _ in result.errors])
        self.assertIn("FAILED (failures=2, errors=1)", printed)

    def test_a_skip_an_expected_failure_and_an_unexpected_success_are_each_reported(self):
        # No companion has one yet, so no other case here would see one dropped. An unexpected
        # success fails the run since Python 3.4
        # (https://docs.python.org/3/library/unittest.html#unittest.TestResult.wasSuccessful).
        module = self.module(Outcomes=cases(
            "Outcomes",
            test_skipped=unittest.skip("on purpose")(lambda self: None),
            test_fails_as_expected=unittest.expectedFailure(lambda self: self.fail("expected")),
            test_passes_unexpectedly=unittest.expectedFailure(lambda self: None)))
        result, printed = self.run_module(module)
        self.assertEqual(3, result.testsRun)
        self.assertEqual(["test_skipped"], [test.id().split(".")[-1] for test, _ in result.skipped])
        self.assertEqual(["test_fails_as_expected"],
                         [test.id().split(".")[-1] for test, _ in result.expectedFailures])
        self.assertEqual(["test_passes_unexpectedly"],
                         [test.id().split(".")[-1] for test in result.unexpectedSuccesses])
        self.assertFalse(result.wasSuccessful(), printed)
        self.assertIn("FAILED (skipped=1, expected failures=1, unexpected successes=1)", printed)

    def test_what_a_case_reports_of_its_duration_reaches_the_result(self):
        # Python 3.12 and later report each case's time through addDuration, which --durations
        # prints (https://docs.python.org/3/library/unittest.html#unittest.TestResult.addDuration).
        # Reported by hand, so this holds under 3.9 too, whose cases report none.
        received = []

        class Receiving(unittest.TestResult):
            def addDuration(self, test, elapsed):
                received.append((test.id(), elapsed))

        test = cases("Timed", test_one=lambda self: None)("test_one")
        recorder = runner.Recorder(threading.Event(), failfast=False)
        recorder.addDuration(test, 1.5)
        recorder.replay(test, Receiving())
        self.assertEqual([(test.id(), 1.5)], received)

    def test_each_case_runs_once_and_is_reported_in_the_suites_order(self):
        # The first case sleeps longest, so the cases finish in the reverse of the suite's order.
        ran = collections.Counter()

        def sleeper(number: int):
            def test(self):
                time.sleep(0.03 * (8 - number))
                ran[number] += 1
            return test

        names = [f"test_{number}" for number in range(8)]
        module = self.module(Sleepers=cases("Sleepers", **{
            name: sleeper(number) for number, name in enumerate(names)}))
        result, printed = self.run_module(module, jobs=8)
        self.assertEqual({number: 1 for number in range(8)}, dict(ran))
        self.assertEqual(8, result.testsRun)
        reported = [line.split(" ", 1)[0] for line in printed.splitlines() if line.startswith("test_")]
        self.assertEqual(names, reported)


def meeting(barrier: threading.Barrier):
    """A test method that passes only if another case reaches `barrier` while it waits there."""
    def test(self):
        barrier.wait()
    return test


class ConcurrencyTests(RunnerTestCase):
    def test_cases_run_at_the_same_time(self):
        # Run one after the other, the first case would wait out the timeout alone and fail.
        barrier = threading.Barrier(2, timeout=10)
        module = self.module(Meeting=cases("Meeting", test_one=meeting(barrier),
                                           test_two=meeting(barrier)))
        result, printed = self.run_module(module, jobs=2)
        self.assertEqual(2, result.testsRun)
        self.assertTrue(result.wasSuccessful(), printed)

    def test_one_job_runs_one_case_at_a_time(self):
        # The first case waits out the timeout alone, which breaks the barrier for the second.
        barrier = threading.Barrier(2, timeout=1)
        module = self.module(Meeting=cases("Meeting", test_one=meeting(barrier),
                                           test_two=meeting(barrier)))
        result, printed = self.run_module(module, jobs=1)
        self.assertEqual(2, result.testsRun)
        self.assertEqual(2, len(result.errors), printed)
        for _, traceback in result.errors:
            self.assertIn("BrokenBarrierError", traceback)

    def test_a_case_marked_to_run_after_the_others_runs_once_every_other_has_finished(self):
        # The loader sorts classes by name, so Afterwards comes first in the suite.
        finished = []

        def worker(self):
            time.sleep(0.1)
            finished.append(1)

        def afterwards(self):
            self.assertEqual(4, len(finished))

        closing = cases("Afterwards", test_reads_what_the_others_left=afterwards)
        closing.runs_after_the_others = True
        module = self.module(Afterwards=closing, Workers=cases(
            "Workers", **{f"test_{number}": worker for number in range(4)}))
        result, printed = self.run_module(module, jobs=4)
        self.assertEqual(5, result.testsRun)
        self.assertTrue(result.wasSuccessful(), printed)

    def test_failfast_starts_no_case_after_a_failure(self):
        # One job, so the case after the failure is the next one the pool would start. The slow
        # stream keeps the main thread replaying the failure while that case could start, so only
        # the stop set by the failing case's own thread keeps it from starting.
        ran = []

        def recording(name: str, fails: bool):
            def test(self):
                ran.append(name)
                self.assertFalse(fails)
            return test

        module = self.module(Cases=cases("Cases", test_a=recording("a", True),
                                         test_b=recording("b", False), test_c=recording("c", False)))
        result, printed = self.run_module(module, jobs=1, failfast=True, stream=SlowStream())
        self.assertEqual(["a"], ran)
        self.assertEqual(1, result.testsRun, printed)
        self.assertEqual(1, len(result.failures))

    def test_a_case_whose_run_raises_is_an_error_and_the_others_still_run(self):
        # TestCase.run reports what a test raises; this case raises out of run itself, as only
        # a fault in the case's own machinery would.
        ran = []

        class Raising(unittest.TestCase):
            def run(self, result=None):
                raise RuntimeError("out of run itself")

            def test_raises(self):
                pass

        module = self.module(Raising=Raising,
                             Others=cases("Others", test_passes=lambda self: ran.append(1)))
        result, printed = self.run_module(module)
        self.assertEqual([1], ran)
        self.assertEqual(2, result.testsRun, printed)
        self.assertEqual(1, len(result.errors), printed)
        self.assertIn("out of run itself", result.errors[0][1])


class JobsTests(unittest.TestCase):
    def test_the_jobs_are_the_cpus_up_to_eight_unless_the_variable_names_a_number(self):
        for environ, cpus, expected in (({}, 12, 8), ({}, 3, 3), ({}, None, 1),
                                        ({"SCRIPT_TESTS_JOBS": ""}, 4, 4),
                                        ({"SCRIPT_TESTS_JOBS": "1"}, 12, 1),
                                        ({"SCRIPT_TESTS_JOBS": "16"}, 4, 16)):
            with self.subTest(environ=environ, cpus=cpus):
                self.assertEqual(expected, runner.jobs_for(environ, cpus))

    def test_a_variable_that_names_no_whole_number_of_at_least_one_is_refused(self):
        for value in ("0", "-2", "two", "1.5", " 3"):
            with self.subTest(value=value):
                with self.assertRaisesRegex(ValueError, re.escape(f"SCRIPT_TESTS_JOBS={value!r}")):
                    runner.jobs_for({"SCRIPT_TESTS_JOBS": value}, 8)


COMPANION = '''
import importlib.util
import threading
import unittest

spec = importlib.util.spec_from_file_location("companion_runner", {runner!r})
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)
BARRIER = threading.Barrier(2, timeout=2)


class Cases(unittest.TestCase):
    def test_passes(self):
        pass

    def test_fails(self):
        self.fail("on purpose")


class Meeting(unittest.TestCase):
    def test_one(self):
        BARRIER.wait()

    def test_two(self):
        BARRIER.wait()


if __name__ == "__main__":
    runner.main()
'''


class MainTests(unittest.TestCase):
    """main(), run the way a companion runs: as the script itself, through python3 -I."""

    def companion(self, *args: str, **environ: str) -> subprocess.CompletedProcess:
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        script = pathlib.Path(scratch.name) / "example_tests.py"
        script.write_text(COMPANION.format(runner=str(ROOT / "tools" / "companion_runner.py")))
        return subprocess.run([sys.executable, "-I", str(script), *args], capture_output=True,
                              text=True, timeout=60, env={"PATH": "/usr/bin:/bin", **environ})

    def test_a_failing_case_makes_the_exit_status_one(self):
        done = self.companion("Cases")
        self.assertEqual(1, done.returncode, done.stderr)
        self.assertEqual("FAILED (failures=1)", done.stderr.splitlines()[-1])

    def test_the_cases_named_are_the_ones_that_run(self):
        done = self.companion("Cases.test_passes")
        self.assertEqual(0, done.returncode, done.stderr)
        self.assertIn("Ran 1 test in", done.stderr)

    def test_the_variable_sets_how_many_run_at_once(self):
        for value, status in (("1", 1), ("2", 0)):
            with self.subTest(value=value):
                done = self.companion("Meeting", SCRIPT_TESTS_JOBS=value)
                self.assertEqual(status, done.returncode, done.stderr)

    def test_a_variable_that_names_no_number_stops_the_run_before_any_case(self):
        done = self.companion("Cases", SCRIPT_TESTS_JOBS="lots")
        self.assertEqual(2, done.returncode, done.stderr)
        self.assertEqual("companion_runner: SCRIPT_TESTS_JOBS='lots' is not a whole number of at least 1.",
                         done.stderr.strip())

    def test_buffering_is_refused_before_any_case(self):
        # -b swaps the process's standard streams for one test at a time, and here several run at
        # once, so a case's output would land in another case's report.
        done = self.companion("Cases", "-b")
        self.assertEqual(2, done.returncode, done.stderr)
        self.assertEqual("companion_runner: -b cannot buffer cases that run at the same time; run without it.",
                         done.stderr.strip())


class ModuleFixtureTests(RunnerTestCase):
    def test_the_modules_fixtures_run_once_around_every_case(self):
        events = []
        module = self.module(
            setUpModule=lambda: events.append("setUpModule"),
            tearDownModule=lambda: events.append("tearDownModule"),
            Recording=cases("Recording", **{
                f"test_{number}": lambda self: events.append("case") for number in range(6)}))
        result, printed = self.run_module(module, jobs=3)
        self.assertTrue(result.wasSuccessful(), printed)
        self.assertEqual(["setUpModule"] + ["case"] * 6 + ["tearDownModule"], events)

    def test_a_module_fixture_that_raises_is_an_error_and_its_cases_do_not_run(self):
        ran = []

        def broken():
            raise RuntimeError("the module cannot be set up")

        module = self.module(setUpModule=broken,
                             Cases=cases("Cases", test_one=lambda self: ran.append(1)))
        result, printed = self.run_module(module)
        self.assertEqual([], ran)
        self.assertFalse(result.wasSuccessful())
        self.assertEqual([f"setUpModule ({module.__name__})"],
                         [str(test) for test, _ in result.errors], printed)
        self.assertIn("the module cannot be set up", result.errors[0][1])

    def test_a_class_with_a_class_fixture_is_an_error_and_its_cases_do_not_run(self):
        # The runner runs no setUpClass or tearDownClass, so a class that has one must not run
        # its cases without it.
        ran = []
        with_fixture = cases("WithFixture", setUpClass=classmethod(lambda cls: ran.append("setUpClass")),
                             test_one=lambda self: ran.append("case"))
        module = self.module(WithFixture=with_fixture,
                             Others=cases("Others", test_passes=lambda self: ran.append("other")))
        result, printed = self.run_module(module)
        self.assertEqual(["other"], ran)
        self.assertFalse(result.wasSuccessful())
        self.assertEqual([f"setUpClass ({module.__name__}.WithFixture)"],
                         [str(test) for test, _ in result.errors], printed)
        self.assertIn("does not run class fixtures", result.errors[0][1])


class LinkToolTests(unittest.TestCase):
    """link_tool() gives each case's bin directory its real tools without a fresh executable each:
    on the Mac this was measured on, the first run of a newly written file costs about 45 ms more
    than a run of a file that has run before (#107)."""

    def bin(self) -> pathlib.Path:
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        return pathlib.Path(scratch.name)

    def test_a_link_runs_the_binary_and_every_link_to_it_shares_one_wrapper(self):
        first, second = self.bin() / "echo", self.bin() / "echo"
        runner.link_tool(first, "/bin/echo")
        runner.link_tool(second, "/bin/echo")
        for link in (first, second):
            done = subprocess.run([str(link), "a", "b"], capture_output=True, text=True,
                                  env={"PATH": "/usr/bin:/bin"}, check=True)
            self.assertEqual("a b\n", done.stdout)
        self.assertEqual(os.path.realpath(first), os.path.realpath(second))
        self.assertNotEqual(str(first), os.path.realpath(first))

    def test_writing_through_a_link_fails_and_leaves_the_wrapper_as_it_was(self):
        # A case that wrote a double over a linked tool would otherwise rewrite the tool for every
        # case at once. Root may write a read-only file, so as root this cannot show the refusal.
        self.assertNotEqual(0, os.geteuid(),
                            "running as root, which may write a read-only file; run as an ordinary user")
        link = self.bin() / "echo"
        runner.link_tool(link, "/bin/echo")
        with self.assertRaises(PermissionError):
            link.write_text("#!/bin/sh\nexit 1\n")
        done = subprocess.run([str(link), "still", "echo"], capture_output=True, text=True,
                              env={"PATH": "/usr/bin:/bin"}, check=True)
        self.assertEqual("still echo\n", done.stdout)


if __name__ == "__main__":
    unittest.main()
