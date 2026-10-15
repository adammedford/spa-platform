using Gateway.SecurityTests.Negative;

// N-16's claim is about every line the suite produced, so the last word on it has to be spoken after
// the last test class has finished. An assembly fixture is the only place that exists: xUnit creates
// it before the first test and disposes it after the last, so the sweep below reads the whole run's
// log, including the lines of classes that never look at one.
//
// The named tests in CanaryTests cover the mechanism and the kinds of credential; this covers the
// scope. If it finds a canary, the run fails as a cleanup error with the same message a test would
// have given, which is enough to say which canary and how many lines were searched.
[assembly: AssemblyFixture(typeof(SuiteSweep))]

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// The final canary sweep of the whole run (N-16). See the comment above the assembly attribute for
/// why it lives here rather than in a test.
/// </summary>
public sealed class SuiteSweep : IDisposable
{
    private bool disposed;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        // Nothing to sweep means no harness was started, which happens when a run is filtered down
        // to tests that need none. That is not a leak and must not be reported as one; the named
        // tests are what hold the mechanism to working.
        if (CanarySweep.Harnesses > 0)
        {
            CanarySweep.Assert("at the end of the run");
        }
    }
}
