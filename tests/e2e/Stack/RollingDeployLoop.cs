using Microsoft.Playwright;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// What row 6's loop reads and pulls (#1203): the cell's tasks on ECS, the tunnel, a fresh session's
/// sign-in and its calls, the straddling login's two halves, and the pause between turns.
/// <see cref="ContinuityDriver.RollingDeployAsync"/> gives the cell's own; a test gives a double.
/// </summary>
public interface IRollingDeployCell
{
    /// <summary>The service's tasks whose desired status is RUNNING (<see cref="ContinuityEcs.ListedAsync"/>).</summary>
    Task<IReadOnlyList<EcsTaskState>> ListedAsync(CancellationToken ct);

    /// <summary>The tasks by id (<see cref="ContinuityEcs.DescribeAsync"/>).</summary>
    Task<IReadOnlyList<EcsTaskState>> DescribeAsync(IReadOnlyList<string> ids, CancellationToken ct);

    /// <summary>Whether <c>/healthz</c> answers 200 through the tunnel now.</summary>
    Task<bool> TunnelAnswersAsync(CancellationToken ct);

    /// <summary>
    /// Whether <c>/healthz</c> answered 200 through the tunnel at every probe taken since
    /// <paramref name="since"/>: on the cell, those a <see cref="TunnelWatch"/> took while a sign-in ran
    /// and the one after it ended (#1211).
    /// </summary>
    Task<bool> TunnelAnsweredSinceAsync(DateTimeOffset since, CancellationToken ct);

    /// <summary>A fresh profile signed in to app 1; a <see cref="PlaywrightException"/> or <see cref="TimeoutException"/> when it could not be.</summary>
    Task<SessionCookies> SignInAsync(CancellationToken ct);

    /// <summary>The session's orders API calls, once a second for <see cref="ContinuityBounds.Row6SessionCalls"/>.</summary>
    Task<IReadOnlyList<Step>> CallEverySecondAsync(int session, SessionCookies cookies, CancellationToken ct);

    /// <summary>The straddling login's start, held at the provider's form.</summary>
    Task<Step> StartLoginAsync(CancellationToken ct);

    /// <summary>The held login submitted, through its callback to the shell.</summary>
    Task<Step> CompleteLoginAsync(CancellationToken ct);

    Task PauseAsync(TimeSpan delay, CancellationToken ct);
}

/// <summary>
/// Row 6's loop, as <see cref="ContinuityDriver.RollingDeployAsync"/> states it: its decisions are
/// proved against a double for ECS and the tunnel (<c>RollingDeployLoopTests</c>, #1203), and only the
/// levers it pulls are the cell's.
/// </summary>
public sealed class RollingDeployLoop(IRollingDeployCell cell, TimeProvider time, TextWriter output)
{
    /// <summary>How long the loop waits between turns.</summary>
    public static readonly TimeSpan Turn = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The start of the one line the loop writes to <c>output</c>, standard output on the cell, when a
    /// deployment launched then puts a session's refresh inside the interval (<see cref="ContinuityBounds.Row6Launch"/>).
    /// </summary>
    public const string LaunchLine = "row6: launch deploy.sh now";

    public async Task<ContinuityObserver.RollingDeploy> RunAsync(TimeSpan bound, CancellationToken ct)
    {
        IReadOnlyList<EcsTaskState> old = [.. (await cell.ListedAsync(ct)).Where(task => task.LastStatus == "RUNNING")];
        HashSet<string> oldIds = [.. old.Select(task => task.Id)];
        List<Task<IReadOnlyList<Step>>> sessions = [];
        long started = time.GetTimestamp();
        TimeSpan nextSignIn = TimeSpan.Zero;
        TimeSpan? firstSignedIn = null;
        bool launchSaid = false;
        Step? start = null;
        Step? callback = null;
        bool ended = false;
        while (!ended && time.GetElapsedTime(started) < bound)
        {
            if (time.GetElapsedTime(started) >= nextSignIn)
            {
                DateTimeOffset signingIn = time.GetUtcNow();
                try
                {
                    SessionCookies cookies = await cell.SignInAsync(ct);
                    firstSignedIn ??= time.GetElapsedTime(started);
                    sessions.Add(cell.CallEverySecondAsync(sessions.Count + 1, cookies, ct));
                    nextSignIn += ContinuityBounds.Row6SignInEvery;
                }
                catch (Exception failed) when (failed is PlaywrightException or TimeoutException)
                {
                    // The tunnel's restart gap, when it did not answer at some moment of the sign-in, though it
                    // answers again by the failure (#1211): tried again on the next turn. A failure while it
                    // answered throughout is the row's.
                    if (await cell.TunnelAnsweredSinceAsync(signingIn, ct))
                    {
                        throw;
                    }
                }
            }

            if (!launchSaid && firstSignedIn is { } signedIn && time.GetElapsedTime(started) >= signedIn + ContinuityBounds.Row6LaunchAfterFirstSignIn)
            {
                await output.WriteLineAsync(
                    $"{LaunchLine} ({time.GetUtcNow():O}): the first session refreshes {DeployedStack.AccessTokenLifetime - ContinuityBounds.Row6RefreshBeforeExpiry} after its sign-in and a new one signs in every {ContinuityBounds.Row6SignInEvery}, so a replacement interval at least {ContinuityBounds.Row6SignInEvery} long that starts from now holds a refresh");
                await output.FlushAsync(ct);
                launchSaid = true;
            }

            IReadOnlyList<EcsTaskState> listed = await cell.ListedAsync(ct);
            EcsTaskState[] fresh = [.. listed.Where(task => !oldIds.Contains(task.Id))];
            if (start is null && fresh.Length > 0 && fresh.All(task => task.LastStatus != "RUNNING"))
            {
                start = await cell.StartLoginAsync(ct);
            }

            IReadOnlyList<EcsTaskState> before = await cell.DescribeAsync([.. oldIds], ct);
            if (start is not null && callback is null && before.All(task => task.StoppedAt is not null) && await cell.TunnelAnswersAsync(ct))
            {
                callback = await cell.CompleteLoginAsync(ct);
            }

            ended = callback is not null && before.All(task => task.StoppedAt is not null) && fresh.Length > 0 && fresh.All(task => task.LastStatus == "RUNNING");
            await cell.PauseAsync(Turn, ct);
        }

        IReadOnlyList<IReadOnlyList<Step>> calls = await Task.WhenAll(sessions);
        IReadOnlyList<EcsTaskState> stopped = await cell.DescribeAsync([.. oldIds], ct);
        IReadOnlyList<EcsTaskState> replacements = [.. (await cell.ListedAsync(ct)).Where(task => !oldIds.Contains(task.Id))];
        return new ContinuityObserver.RollingDeploy([.. stopped.Select(task => task.Task)], [.. replacements.Select(task => task.Task)], calls, start, callback);
    }
}

/// <summary>
/// The tunnel probed while a piece of work runs (#1211): as it starts, every <see cref="Every"/> while
/// it runs, and once after it ends. A sign-in in flight when the tunnel's old hop stops can fail only
/// once its Playwright wait runs out, after the restart answers again, so a probe taken at the failure
/// alone reads the gap as the tunnel up; the gaps #348 records ran 8 to 16 s.
/// </summary>
public sealed class TunnelWatch(Func<CancellationToken, Task<bool>> probe, Func<TimeSpan, CancellationToken, Task> pause, TimeProvider time)
{
    /// <summary>How often the tunnel is probed while the work runs.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

    private readonly List<DateTimeOffset> unanswered = [];

    /// <summary>Runs <paramref name="work"/> with the tunnel probed alongside, and returns or throws what it does.</summary>
    public async Task<T> WhileAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        Task<T> running = work();
        while (true)
        {
            bool ended = running.IsCompleted;
            DateTimeOffset at = time.GetUtcNow();
            if (!await probe(ct))
            {
                unanswered.Add(at);
            }

            if (ended)
            {
                return await running;
            }

            await Task.WhenAny(running, pause(Every, ct));
        }
    }

    /// <summary>Whether every probe taken since <paramref name="since"/> answered.</summary>
    public bool AnsweredSince(DateTimeOffset since) => !unanswered.Exists(at => at >= since);
}
