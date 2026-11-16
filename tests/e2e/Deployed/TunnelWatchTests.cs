using Gateway.E2ETests.Stack;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The tunnel probes row 6's sign-ins run beside on the cell (#1211): a probe that did not answer while
/// the work ran, or right after it ended, is kept, so a restart gap inside a sign-in is seen though the
/// tunnel answers again by its failure. A scripted probe on <see cref="ManualClock"/>; no test sleeps.
/// </summary>
public sealed class TunnelWatchTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset At(int seconds) => Epoch + TimeSpan.FromSeconds(seconds);

    /// <summary>A probe answering <paramref name="answers"/> in turn on a clock each pause moves, and work ended by <paramref name="ends"/> after the probe or pause it names.</summary>
    private sealed class Scripted(bool[] answers, Func<int, int, bool> ends)
    {
        private readonly TaskCompletionSource<string> work = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int probes;
        private int pauses;

        public ManualClock Clock { get; } = new(Epoch);

        public int Probes => probes;

        public TunnelWatch NewWatch() => new(Probe, Pause, Clock);

        public Task<string> Work() => work.Task;

        private Task<bool> Probe(CancellationToken ct)
        {
            bool answer = answers[probes++];
            if (ends(probes, pauses))
            {
                work.TrySetResult("done");
            }

            return Task.FromResult(answer);
        }

        private Task Pause(TimeSpan delay, CancellationToken ct)
        {
            Clock.Advance(delay);
            pauses++;
            if (ends(probes, pauses))
            {
                work.TrySetResult("done");
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_probe_that_did_not_answer_while_the_work_ran_is_kept_though_the_ones_after_answer()
    {
        // The second of four probes, at 1 s, falls in the gap; the work ends at the third, at 2 s.
        Scripted scripted = new([true, false, true, true], (probes, _) => probes == 3);
        TunnelWatch watch = scripted.NewWatch();

        Assert.Equal("done", await watch.WhileAsync(scripted.Work, Ct));

        Assert.Equal(4, scripted.Probes);
        Assert.False(watch.AnsweredSince(At(0)), "the probe at 1 s did not answer, while the work ran");
        Assert.True(watch.AnsweredSince(At(2)), "every probe from 2 s answered");
    }

    [Fact]
    public async Task The_tunnel_is_probed_once_more_after_the_work_ends()
    {
        // The work ends during the first pause; the probe after it, at 1 s, does not answer.
        Scripted scripted = new([true, false], (_, pauses) => pauses == 1);
        TunnelWatch watch = scripted.NewWatch();

        await watch.WhileAsync(scripted.Work, Ct);

        Assert.Equal(2, scripted.Probes);
        Assert.False(watch.AnsweredSince(At(0)), "the probe after the work ended did not answer");
    }

    [Fact]
    public async Task What_the_work_throws_the_watch_throws_once_it_has_probed_after_it()
    {
        TaskCompletionSource<string> failing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int probes = 0;
        ManualClock clock = new(Epoch);
        TunnelWatch watch = new(
            _ =>
            {
                probes++;
                return Task.FromResult(true);
            },
            (delay, _) =>
            {
                clock.Advance(delay);
                failing.TrySetException(new TimeoutException("the provider's page did not load"));
                return Task.CompletedTask;
            },
            clock);

        await Assert.ThrowsAsync<TimeoutException>(() => watch.WhileAsync(() => failing.Task, Ct));
        Assert.Equal(2, probes);
        Assert.True(watch.AnsweredSince(At(0)));
    }
}
