using System.Globalization;
using System.Text.Json;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// The sessions O2 and O4 drive through: a fresh browser profile signed in to the shell as one traced
/// step and closed after it, a raw call with a session's cookies, and O2's overlapping chain and
/// fallback callbacks on one session. The driver's are the cell's browser and tunnel; a test's answer
/// from a recording.
/// </summary>
public interface IFragmentsSessions
{
    /// <summary>The step of one sign-in to the shell, through the chain, and the session's cookies; null cookies when it did not end signed in.</summary>
    Task<(Step Step, SessionCookies? Cookies)> SignInAsync(string name, CancellationToken ct);

    /// <summary>One raw GET of the shell's host with the session's cookies, as a same-origin fetch sends it.</summary>
    Task<Step> SendAsync(string name, string path, SessionCookies cookies, CancellationToken ct);

    /// <summary>
    /// A sign-in whose chain is still running when the same browser starts <c>/bff/login?fragment=</c>
    /// for <paramref name="fallbackFragment"/>: the sign-in's step and the fallback's, both on one session.
    /// </summary>
    Task<IReadOnlyList<Step>> OverlapAsync(string name, string fallbackFragment, CancellationToken ct);
}

/// <summary>
/// O2's and O4's workloads (#1005 O2, O4), with the cell's levers they pull through the AWS CLI
/// (#1411's decision of 2026-11-24: the drive pulls, then restores). Every wait goes through
/// <see cref="Delay"/> and every time through <see cref="Clock"/>, so a test runs the hour in no time.
/// </summary>
public sealed class FragmentsWorkloads(Cell cell, ICommandRunner runner, IFragmentsSessions sessions)
{
    /// <summary>O2's sessions per topology: each is one repetition of the 1 + 5 due sets.</summary>
    public const int Repetitions = 5;

    /// <summary>O4's named workload: this many sign-ins, each creating one session record at least.</summary>
    public const int SignIns = 5;

    public const string O4Workload = "o4-sign-ins";

    /// <summary>The sign-in chain's step (contracts/http-contract.md -> Fragments).</summary>
    public const string ChainPath = "/bff/chain";

    /// <summary>How long DynamoDB's metrics may take to reach CloudWatch before O4 reads them.</summary>
    public static readonly TimeSpan MetricsArrival = TimeSpan.FromMinutes(5);

    /// <summary>A call due a refresh comes this long after the last sign-in: past the access token's life.</summary>
    public static readonly TimeSpan DueAfter = DeployedStack.AccessTokenLifetime + TimeSpan.FromSeconds(30);

    /// <summary>The gateway service's wait for a new task count to settle; restores included.</summary>
    public static readonly TimeSpan Scale = TimeSpan.FromMinutes(15);

    /// <summary>
    /// What the callback's elapsed bound counts beside its versioned replaces (#1005 O2): one outer
    /// <c>GetAsync</c>, AppSessions.EnterAsync's read of the record under the set's gate, and the rest,
    /// the code exchange under <c>ProviderDeadline</c>, 10 s (AccessTokenSourceOptions; #961's P).
    /// </summary>
    public const int OuterGets = 1;

    public const double RestSeconds = 10;

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>
    /// O2: on one task and then on two, <see cref="Repetitions"/> sessions signed in, left until every
    /// set is due, then each session's sets called at once, the shell's API and one API of each of the
    /// first five fragments, each call's latency timed; then, on two tasks, one sign-in overlapped by a
    /// fallback. The service's desired count is put back whatever happened. The writes are read from the
    /// lines afterwards (<see cref="WithWrites"/>).
    /// </summary>
    public async Task<(RefreshContention Record, IReadOnlyList<Step> Steps)> ContentionAsync(IReadOnlyList<LiveFragment> fragments, CancellationToken ct)
    {
        string[] paths = [ContinuityDriver.ApiPath, .. fragments.Where(fragment => fragment.Apis.Count > 0).Take(FragmentsObserver.DueSets - 1)
            .Select(fragment => $"/fragments/{fragment.FragmentId}/api/{fragment.Apis[0]}/whoami")];
        int desired = await DesiredCountAsync(ct);
        List<Step> steps = [];
        return await Restoring.RunAsync(async () =>
        {
            List<LatencySample> topologies = [];
            foreach (int tasks in new[] { 1, 2 })
            {
                await ScaleAsync(tasks, ct);
                topologies.Add(await SampleAsync(tasks, paths, steps, ct));
            }

            CallbackOverlap? callbacks = null;
            if (fragments.Count > 0)
            {
                steps.AddRange(await sessions.OverlapAsync("o2-overlap", fragments[^1].FragmentId, ct));
                callbacks = new CallbackOverlap(null, null, OuterGets, RestSeconds);
            }

            return (new RefreshContention(topologies, callbacks), (IReadOnlyList<Step>)steps);
        }, restore => ScaleAsync(desired, restore), Scale);
    }

    private async Task<LatencySample> SampleAsync(int tasks, string[] paths, List<Step> steps, CancellationToken ct)
    {
        List<SessionCookies> signedIn = [];
        for (int r = 1; r <= Repetitions; r++)
        {
            (Step step, SessionCookies? cookies) = await sessions.SignInAsync($"o2-t{tasks}-session-{r}", ct);
            steps.Add(step);
            if (cookies is not null)
            {
                signedIn.Add(cookies);
            }
        }

        await Delay(DueAfter, ct);
        List<double> latencies = [];
        for (int r = 0; r < signedIn.Count; r++)
        {
            SessionCookies cookies = signedIn[r];
            (Step Step, double Ms)[] calls = await Task.WhenAll(paths.Select(async (path, set) =>
            {
                long started = Clock.GetTimestamp();
                Step call = await sessions.SendAsync($"o2-t{tasks}-r{r + 1}-set{set}", path, cookies, ct);
                return (call, Clock.GetElapsedTime(started).TotalMilliseconds);
            }));
            steps.AddRange(calls.Select(call => call.Step));
            latencies.AddRange(calls.Where(call => call.Step.Status == 200).Select(call => call.Ms));
        }

        return new LatencySample(tasks, paths.Length, paths.Length, Repetitions, latencies, null);
    }

    /// <summary>
    /// O2's record with its writes read from the lines: each topology's refresh writes on the sessions its
    /// steps reached, and the overlap's fragment sign-in writes, with the tasks that wrote them.
    /// </summary>
    public static RefreshContention WithWrites(RefreshContention record, IReadOnlyList<Step> steps, GatewayCapture capture)
    {
        LatencySample[] topologies = [.. record.Topologies.Select(sample =>
        {
            IReadOnlySet<string> refs = FragmentsLines.SessionsOf(steps.Where(step => step.Name.StartsWith($"o2-t{sample.Tasks}-", StringComparison.Ordinal)), capture);
            IReadOnlyList<WriteCount> writes = FragmentsLines.RefreshWrites(refs, capture);
            return sample with { Writes = writes.Count == 0 ? null : writes };
        })];
        if (record.Callbacks is not { } callbacks)
        {
            return record with { Topologies = topologies };
        }

        IReadOnlySet<string> overlap = FragmentsLines.SessionsOf(steps.Where(step => step.Name.StartsWith("o2-overlap", StringComparison.Ordinal)), capture);
        (string TaskId, int Attempts)[] writes = [.. capture.Events
            .Where(line => line.Field("session_ref") is { } session && overlap.Contains(session))
            .Select(line => (line.TaskId, Write: FragmentsLines.FragmentWrite(line)))
            .Where(each => each.Write is not null)
            .Select(each => (each.TaskId, each.Write!.Value.Attempts))];
        return record with
        {
            Topologies = topologies,
            Callbacks = writes.Length == 0
                ? callbacks
                : callbacks with { TasksWrote = [.. writes.Select(write => write.TaskId)], AttemptsPerWrite = [.. writes.Select(write => write.Attempts)] },
        };
    }

    /// <summary>
    /// O4's capacity sample: the workload started on a minute boundary with a whole quiet minute after the
    /// drive began, <see cref="SignIns"/> sign-ins, its exact start and stop, then a wait past the quiet
    /// minute after it and <see cref="MetricsArrival"/>, and the table's periods from the control before to
    /// the control after. Isolation is read from the lines afterwards (<see cref="Isolated"/>).
    /// </summary>
    public async Task<(CapacitySample Sample, IReadOnlyList<Step> Steps, DateTimeOffset Until)> CapacityAsync(FragmentsReaders readers, string table, CancellationToken ct)
    {
        DateTimeOffset first = Minute(Clock.GetUtcNow()).AddMinutes(2);
        await Delay(first - Clock.GetUtcNow(), ct);
        DateTimeOffset start = Clock.GetUtcNow();
        List<Step> steps = [];
        int operations = 0;
        for (int i = 1; i <= SignIns; i++)
        {
            (Step step, SessionCookies? cookies) = await sessions.SignInAsync($"o4-sign-in-{i}", ct);
            steps.Add(step);
            operations += cookies is null ? 0 : 1;
        }

        DateTimeOffset stop = Clock.GetUtcNow();
        DateTimeOffset after = Minute(stop).AddMinutes(2);
        await Delay(after + MetricsArrival - Clock.GetUtcNow(), ct);
        IReadOnlyList<CapacityPeriod> periods = await readers.WriteCapacityAsync(table, first.AddMinutes(-1), after, ct);
        return (new CapacitySample(O4Workload, start, stop, operations, periods, null, null), steps, after);
    }

    /// <summary>
    /// O4's sample with its isolation read from the gateway's lines over the control minutes and the
    /// workload's: the sessions none of its sign-ins reached (other writers), and the requests none of them
    /// sent (overlapping workloads). An empty list is a check that ran and found none.
    /// </summary>
    public static CapacitySample Isolated(CapacitySample sample, IReadOnlyList<Step> steps, GatewayCapture capture)
    {
        DateTimeOffset from = Minute(sample.Start).AddMinutes(-1);
        DateTimeOffset to = Minute(sample.Stop).AddMinutes(2);
        return sample with
        {
            OtherWriters = FragmentsLines.OtherSessions(FragmentsLines.SessionsOf(steps, capture), capture, from, to),
            OverlappingWorkloads = FragmentsLines.Foreign(steps, capture, from, to),
        };
    }

    public static DateTimeOffset Minute(DateTimeOffset at) => new(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, TimeSpan.Zero);

    /// <summary>The gateway service's desired count (https://docs.aws.amazon.com/cli/latest/reference/ecs/describe-services.html).</summary>
    public async Task<int> DesiredCountAsync(CancellationToken ct)
    {
        string text = await runner.RunAsync("aws",
            ["ecs", "describe-services", "--cluster", cell.Cluster, "--services", cell.Service, "--output", "json", "--region", cell.Region], ct);
        using JsonDocument answer = JsonDocument.Parse(text);
        return answer.RootElement.GetProperty("services")[0].GetProperty("desiredCount").GetInt32();
    }

    /// <summary>
    /// The service at <paramref name="tasks"/>, and the CLI's wait until it is stable
    /// (https://docs.aws.amazon.com/cli/latest/reference/ecs/update-service.html;
    /// https://docs.aws.amazon.com/cli/latest/reference/ecs/wait/services-stable.html).
    /// </summary>
    public async Task ScaleAsync(int tasks, CancellationToken ct)
    {
        await runner.RunAsync("aws",
            ["ecs", "update-service", "--cluster", cell.Cluster, "--service", cell.Service, "--desired-count", tasks.ToString(CultureInfo.InvariantCulture), "--output", "json", "--region", cell.Region], ct);
        await runner.RunAsync("aws", ["ecs", "wait", "services-stable", "--cluster", cell.Cluster, "--services", cell.Service, "--region", cell.Region], ct);
    }
}
