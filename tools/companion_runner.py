"""The runner the script test companions use: unittest's own, with the cases run at the same time.

Owner: integrator. Built in #107, whose profile is in its Answer.

**Why.** tools/land_tests.py, tools/check_tests.py and tools/prune_branches_tests.py build a bare
origin and a clone for nearly every case and run a real script against them, so a run spends 96 to
98% of its time waiting on child processes. On 18 October 2026 the night's runs of *_tests.py files
took 59.5 minutes, more than its full checks together, mostly these three and mostly mutation
proofs, each a whole run. Run one at a time, their cases leave every core but one idle. On threads,
a run that took 32 to 35 seconds takes 6 to 8 on the 12-core Mac it was measured on.

**What stays unittest's.** A companion calls main() where it called unittest.main(), so its
command line, its exit status, the names it is asked to run and its output are unittest's: Runner is
a unittest.TextTestRunner whose suite runs its cases on a pool of threads. Each case reports to a
Recorder of its own, and the main thread replays each recorder into the real result in the suite's
order, so the output reads as a serial run's would, and two runs can be compared line for line.

**What a companion keeps to.** Its cases share one process, so none may change what the process
shares: the working directory, os.environ, the umask, a signal handler. Each case keeps to its own
throwaway directory and passes its own environment to what it runs, which is how the three
companions were already written. A class whose cases read what every other case left behind, as
tools/land_tests.py's completeness case reads every refusal the others produced, sets
`runs_after_the_others = True`: its cases run one at a time, once every other case has finished.

A module's setUpModule and tearDownModule run once, around every case, as unittest's own suite runs
them. A class with its own setUpClass or tearDownClass is an error and its cases do not run, since
no companion has one and running its cases without it would test something else. Failfast (-f)
starts no case after the first failure, though cases already running finish.

**How many at once.** One per CPU, up to MOST_JOBS, unless SCRIPT_TESTS_JOBS names another whole
number: SCRIPT_TESTS_JOBS=1 runs them one at a time, through the same replay. MOST_JOBS is 8 because
on that Mac 6 and 8 threads took the same time, and 10 and 12 were slower: much of the work is git
writing small files, which stopped scaling at about four threads.

**The tools in a case's PATH.** link_tool() makes a case's real tools links to one wrapper per tool
for the whole run, because there a newly written file costs about 45 ms more the first time it runs
than a file that has run before, and a fresh link to one that has run costs nothing extra.

Run its companion: python3 -I tools/companion_runner_tests.py
"""

from __future__ import annotations

import atexit
import concurrent.futures
import os
import pathlib
import re
import shlex
import sys
import tempfile
import threading
import unittest

# A test class that reads what the other cases left behind sets this attribute true, and its cases
# run one at a time once every other case has finished.
AFTER_THE_OTHERS = "runs_after_the_others"

# How many cases run at once: the variable's number when it names one, else one per CPU up to
# MOST_JOBS.
JOBS = "SCRIPT_TESTS_JOBS"
MOST_JOBS = 8


def jobs_for(environ, cpus: int | None) -> int:
    """How many cases run at once, given the environment and the number of CPUs."""
    value = environ.get(JOBS, "")
    if value == "":
        return min(cpus or 1, MOST_JOBS)
    if not re.fullmatch(r"[0-9]+", value) or int(value) < 1:
        raise ValueError(f"{JOBS}={value!r} is not a whole number of at least 1")
    return int(value)


class Recorder(unittest.TestResult):
    """What one case reported, kept on the thread that ran it, to be replayed in order later.

    Under failfast, a failure sets the run's stop event on the spot, so no case starts after it: the
    thread that ran it may otherwise take the next case before the main thread has replayed it."""

    def __init__(self, stop: threading.Event, failfast: bool):
        super().__init__()
        self.events: list[tuple[str, tuple]] = []
        self.stop_all = stop
        self.failfast = failfast

    def failed(self):
        if self.failfast:
            self.stop_all.set()

    def addSuccess(self, test):
        self.events.append(("addSuccess", (test,)))

    def addError(self, test, err):
        self.events.append(("addError", (test, err)))
        self.failed()

    def addFailure(self, test, err):
        self.events.append(("addFailure", (test, err)))
        self.failed()

    def addSubTest(self, test, subtest, err):
        self.events.append(("addSubTest", (test, subtest, err)))
        if err is not None:
            self.failed()

    def addSkip(self, test, reason):
        self.events.append(("addSkip", (test, reason)))

    def addExpectedFailure(self, test, err):
        self.events.append(("addExpectedFailure", (test, err)))

    def addUnexpectedSuccess(self, test):
        self.events.append(("addUnexpectedSuccess", (test,)))
        self.failed()

    def replay(self, test: unittest.TestCase, result: unittest.TestResult):
        result.startTest(test)
        for name, args in self.events:
            getattr(result, name)(*args)
        result.stopTest(test)


class Fixture:
    """A fixture that raised or was refused, in a result: named as unittest's own suite names one,
    "setUpModule (<module>)" or "setUpClass (<module>.<class>)"."""

    failureException = None

    def __init__(self, description: str):
        self.description = description

    def id(self) -> str:
        return self.description

    def shortDescription(self) -> None:
        return None

    def __str__(self) -> str:
        return self.description


def leaves(suite):
    """Every test case in `suite`, in order, however deeply its suites nest."""
    for test in suite:
        if isinstance(test, unittest.TestSuite):
            yield from leaves(test)
        else:
            yield test


def call_fixture(module: str, name: str, result: unittest.TestResult) -> bool:
    """Calls the module's setUpModule or tearDownModule, if it has one, as unittest's own suite
    would; one that raises is reported as an error, and the answer is False."""
    function = getattr(sys.modules.get(module), name, None)
    if function is None:
        return True
    try:
        function()
    except Exception:
        result.addError(Fixture(f"{name} ({module})"), sys.exc_info())
        return False
    return True


def has_class_fixture(cls: type) -> bool:
    """Whether a test class defines its own setUpClass or tearDownClass."""
    return any(getattr(cls, name).__func__ is not getattr(unittest.TestCase, name).__func__
               for name in ("setUpClass", "tearDownClass"))


def refuse_class_fixture(cls: type, result: unittest.TestResult):
    """Reports a class with a class fixture as an error under unittest's name for one."""
    error = TypeError(f"{cls.__qualname__} has setUpClass or tearDownClass, and "
                      "tools/companion_runner.py does not run class fixtures; set up in setUpModule "
                      "or in each case instead")
    result.addError(Fixture(f"setUpClass ({cls.__module__}.{cls.__qualname__})"),
                    (TypeError, error, None))


def run_one(test: unittest.TestCase, stop: threading.Event, failfast: bool) -> Recorder | None:
    """Runs `test` into a recorder of its own, unless the run has stopped. What escapes the case's
    own run is that case's error, so the other cases still run and the run still reports."""
    if stop.is_set():
        return None
    recorder = Recorder(stop, failfast)
    try:
        test(recorder)
    except BaseException:
        recorder.addError(test, sys.exc_info())
    return recorder


class Concurrently:
    """A suite's cases, run on `jobs` threads when TextTestRunner.run calls this as `test(result)`,
    and reported to `result` in the suite's order."""

    def __init__(self, suite: unittest.TestSuite, jobs: int, failfast: bool):
        self.suite = suite
        self.jobs = jobs
        self.failfast = failfast

    def __call__(self, result: unittest.TestResult):
        tests = list(leaves(self.suite))
        # A module whose setUpModule raised runs none of its cases, nor its tearDownModule.
        modules = [module for module in dict.fromkeys(type(test).__module__ for test in tests)
                   if call_fixture(module, "setUpModule", result)]
        tests = [test for test in tests if type(test).__module__ in modules]
        # Nor does a class whose class fixture this runner would not run.
        refused = [cls for cls in dict.fromkeys(type(test) for test in tests) if has_class_fixture(cls)]
        for cls in refused:
            refuse_class_fixture(cls, result)
        tests = [test for test in tests if type(test) not in refused]
        later = [test for test in tests if getattr(type(test), AFTER_THE_OTHERS, False)]
        now = [test for test in tests if not getattr(type(test), AFTER_THE_OTHERS, False)]
        stop = threading.Event()
        pool = concurrent.futures.ThreadPoolExecutor(max_workers=self.jobs)
        try:
            futures = [pool.submit(run_one, test, stop, self.failfast) for test in now]
            for test, future in zip(now, futures):
                recorder = future.result()
                if recorder is not None:
                    recorder.replay(test, result)
                # Set by failfast through the replay, or by unittest's -c handler on Ctrl-C.
                if result.shouldStop:
                    break
        finally:
            # Whatever ended the loop, the cases not yet started return at once.
            stop.set()
            pool.shutdown(wait=True)
        for test in later:
            if result.shouldStop:
                break
            run_one(test, threading.Event(), self.failfast).replay(test, result)
        for module in modules:
            call_fixture(module, "tearDownModule", result)


class Runner(unittest.TextTestRunner):
    """unittest's text runner, but its cases run `jobs` at a time, by default as many as
    jobs_for() gives for this environment and machine."""

    def __init__(self, *args, jobs: int | None = None, **kwargs):
        super().__init__(*args, **kwargs)
        self.jobs = jobs_for(os.environ, os.cpu_count()) if jobs is None else jobs

    def run(self, test):
        return super().run(Concurrently(test, self.jobs, self.failfast))


class Wrappers:
    """One wrapper script per binary, written once for the whole run and linked from every case's
    bin directory, for the cost the module's docstring gives. Each is read-only, so writing through
    a case's link fails instead of rewriting the tool for every case."""

    def __init__(self):
        self.lock = threading.Lock()
        self.made: dict[str, pathlib.Path] = {}
        self.directory: tempfile.TemporaryDirectory | None = None

    def wrapper(self, binary: str) -> pathlib.Path:
        with self.lock:
            if binary not in self.made:
                if self.directory is None:
                    self.directory = tempfile.TemporaryDirectory(prefix="companion-tools-")
                    atexit.register(self.directory.cleanup)
                # A folder each, so two binaries of one name cannot collide.
                folder = pathlib.Path(self.directory.name) / str(len(self.made))
                folder.mkdir()
                wrapper = folder / os.path.basename(binary)
                wrapper.write_text(f'#!/bin/sh\nexec {shlex.quote(binary)} "$@"\n')
                wrapper.chmod(0o555)
                self.made[binary] = wrapper
            return self.made[binary]


WRAPPERS = Wrappers()


def link_tool(path: pathlib.Path, binary: str):
    """Makes `path` a symbolic link to the run's one wrapper that execs `binary`."""
    path.symlink_to(WRAPPERS.wrapper(binary))


def main():
    """What a companion calls in place of unittest.main(), with the same command line."""
    try:
        jobs_for(os.environ, os.cpu_count())
    except ValueError as error:
        print(f"companion_runner: {error}.", file=sys.stderr)
        raise SystemExit(2)
    unittest.main(module="__main__", testRunner=Runner)
