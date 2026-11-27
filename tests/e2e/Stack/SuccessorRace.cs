namespace Gateway.E2ETests.Stack;

/// <summary>O3's sessions: <see cref="IFragmentsSessions"/>, and the logout a form on the shell's page posts.</summary>
public interface ISuccessorSessions : IFragmentsSessions
{
    /// <summary>One raw <c>POST /bff/logout</c> with the session's cookies, as the shell's logout form sends it; its redirect is not followed.</summary>
    Task<Step> LogoutAsync(string name, SessionCookies cookies, CancellationToken ct);
}

/// <summary>
/// O3's drive (N-38, #961 O3; #1529): one session signed in, one call on a fragment's API before its set is
/// due, whose request line gives the session's reference; then, once the set is due, that fragment's
/// refresh sent and, a lag later, <c>POST /bff/logout</c>, both in flight at once, so that on two tasks the
/// refresh's write can land after the logout's removal. Whether it did, and on which tasks, is read from the
/// gateway's lines (<see cref="FragmentsObserver.Successor"/>); a missed race is not proved and the run
/// makes another attempt, each at the next of <see cref="LogoutLags"/>.
/// </summary>
public sealed class SuccessorRace(ISuccessorSessions sessions)
{
    /// <summary>The call before the race, whose request line carries the session's reference.</summary>
    public const string BeforeStep = "o3-before";

    /// <summary>
    /// How long after the refresh is sent the logout follows, by attempt in turn. The refresh's provider call
    /// must start before the logout's revocation reaches the provider, or the presented token is refused and
    /// no successor is obtained; and its write must come after the removal. The lags spread the attempts
    /// across that window rather than betting each on one offset.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> LogoutLags = [TimeSpan.Zero, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250)];

    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public static TimeSpan LagOf(int attempt) => LogoutLags[(attempt - 1) % LogoutLags.Count];

    /// <summary>The race on <paramref name="fragment"/>'s first API; a sign-in that did not end signed in races nothing.</summary>
    public async Task<(SuccessorRecord Record, IReadOnlyList<Step> Steps)> RunAsync(LiveFragment fragment, int attempt, CancellationToken ct)
    {
        SuccessorRecord record = new("", fragment.FragmentId);
        string path = $"/fragments/{fragment.FragmentId}/api/{fragment.Apis[0]}/whoami";
        (Step signIn, SessionCookies? cookies) = await sessions.SignInAsync("o3-sign-in", ct);
        if (cookies is null)
        {
            return (record, [signIn]);
        }

        Step before = await sessions.SendAsync(BeforeStep, path, cookies, ct);
        await Delay(FragmentsWorkloads.DueAfter, ct);
        Task<Step> refresh = sessions.SendAsync("refresh", path, cookies, ct);
        await Delay(LagOf(attempt), ct);
        Step logout = await sessions.LogoutAsync("logout", cookies, ct);
        return (record, [signIn, before, await refresh, logout]);
    }

    /// <summary>The record with the session's reference, from the request line of the call before the race (<see cref="GatewayCapture.SessionRefOf"/>); empty when none carries one.</summary>
    public static SuccessorRecord WithSession(SuccessorRecord record, IReadOnlyList<Step> steps, GatewayCapture capture) =>
        record with { SessionRef = steps.FirstOrDefault(step => step.Name == BeforeStep) is { } before ? capture.SessionRefOf(before) ?? "" : "" };
}
