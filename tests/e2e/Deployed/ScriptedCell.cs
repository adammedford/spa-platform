using Gateway.E2ETests.Stack;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// A double for what row 6's loop reads and pulls (#1203): ECS's old and new tasks and the tunnel, each
/// a function of the time since the row started, on a clock that moves only when the loop pauses or a
/// sign-in takes time, so no test sleeps. A sign-in fails, by default, while the tunnel does not answer.
/// </summary>
internal sealed class ScriptedCell : IRollingDeployCell
{
    private static readonly DateTimeOffset Epoch = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    public ManualClock Clock { get; } = new(Epoch);

    private TimeSpan Now => Clock.GetUtcNow() - Epoch;

    public Func<TimeSpan, EcsTaskState> Old { get; init; } = _ => Running("old");

    public Func<TimeSpan, EcsTaskState[]> New { get; init; } = _ => [];

    public Func<TimeSpan, bool> Tunnel { get; init; } = _ => true;

    public Func<TimeSpan, bool>? SignInFails { get; init; }

    public Func<TimeSpan, TimeSpan> SignInTakes { get; init; } = _ => TimeSpan.Zero;

    /// <summary>Called at the end of each pause with the time then.</summary>
    public Action<TimeSpan>? Turned { get; set; }

    public List<TimeSpan> SignedIn { get; } = [];

    public List<TimeSpan> SignInsFailed { get; } = [];

    public TimeSpan? LoginStartedAt { get; private set; }

    public TimeSpan? LoginSubmittedAt { get; private set; }

    public static EcsTaskState Running(string id) => new(id, "RUNNING", "RUNNING", Epoch, null, null, null);

    public static EcsTaskState Pending(string id) => new(id, "PENDING", "RUNNING", null, null, null, null);

    /// <summary>A task ECS has asked to stop and which still drains: desired STOPPED, no stoppedAt.</summary>
    public static EcsTaskState Draining(string id) => new(id, "DEACTIVATING", "STOPPED", Epoch, null, null, null);

    public static EcsTaskState Stopped(string id, TimeSpan at) => new(id, "STOPPED", "STOPPED", Epoch, Epoch + at, null, null);

    public Task<IReadOnlyList<EcsTaskState>> ListedAsync(CancellationToken ct)
    {
        EcsTaskState old = Old(Now);
        return Task.FromResult<IReadOnlyList<EcsTaskState>>([.. old.DesiredStatus == "RUNNING" ? [old] : Array.Empty<EcsTaskState>(), .. New(Now)]);
    }

    public Task<IReadOnlyList<EcsTaskState>> DescribeAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        EcsTaskState[] known = [Old(Now), .. New(Now)];
        return Task.FromResult<IReadOnlyList<EcsTaskState>>([.. ids.Select(id => known.Single(task => task.Id == id))]);
    }

    public Task<bool> TunnelAnswersAsync(CancellationToken ct) => Task.FromResult(Tunnel(Now));

    public Task<SessionCookies> SignInAsync(CancellationToken ct)
    {
        TimeSpan at = Now;
        if (SignInFails?.Invoke(at) ?? !Tunnel(at))
        {
            SignInsFailed.Add(at);
            throw new TimeoutException("the provider's page did not load");
        }

        SignedIn.Add(at);
        Clock.Advance(SignInTakes(at));
        return Task.FromResult(new SessionCookies("[double]"));
    }

    public Task<IReadOnlyList<Step>> CallEverySecondAsync(int session, SessionCookies cookies, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Step>>([]);

    public Task<Step> StartLoginAsync(CancellationToken ct)
    {
        LoginStartedAt ??= Now;
        return Task.FromResult(new Step("login-start", ClientTrace.Fresh().TraceId, Clock.GetUtcNow(), 302));
    }

    public Task<Step> CompleteLoginAsync(CancellationToken ct)
    {
        LoginSubmittedAt ??= Now;
        return Task.FromResult(new Step("callback", ClientTrace.Fresh().TraceId, Clock.GetUtcNow(), 302));
    }

    public Task PauseAsync(TimeSpan delay, CancellationToken ct)
    {
        Clock.Advance(delay);
        Turned?.Invoke(Now);
        return Task.CompletedTask;
    }
}

/// <summary>A clock that moves only when told: its timestamps are its own ticks, so elapsed time is what was advanced.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public void Advance(TimeSpan by) => now += by;

    public override DateTimeOffset GetUtcNow() => now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => now.UtcTicks;
}
