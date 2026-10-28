using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// One continuity row's run (#358): the row's bounds read back from its earlier evidence (Q148), the
/// lever pulled only when something is left of them, each attempt followed by the log-delivery wait
/// and a read of CloudWatch cut at that wait (Q144), the control checked on every read (Q143), and
/// the evidence file written whatever the verdict (Q142).
/// </summary>
public sealed class ContinuityRun(ContinuityEvidence evidence, ContinuityLog log)
{
    /// <summary>How long the run waits before each read; the cut is always <see cref="ContinuityBounds.LogDelivery"/> after the last request.</summary>
    public TimeSpan Delivery { get; init; } = ContinuityBounds.LogDelivery;

    /// <summary>How far before the control the read starts: the workstation's clock and the cell's may differ by this much.</summary>
    private static readonly TimeSpan ClockMargin = TimeSpan.FromSeconds(5);

    /// <summary>A two-task row (3, 4, 5, 7, 9): attempts until one verdict is not "not proved" or the bounds are spent.</summary>
    public async Task<RowResult> TwoTaskRowAsync(
        string row,
        Step control,
        Func<CancellationToken, Task>? lever,
        Func<int, CancellationToken, Task<Attempt>> attempt,
        Func<IReadOnlyList<Attempt>, GatewayCapture, RowResult> judge,
        CancellationToken ct)
    {
        RowBudget budget = await evidence.BudgetAsync(row, ct);
        if (budget.Spent)
        {
            RowResult spent = RowResult.NotProved(
                $"the row's bounds are spent ({ContinuityBounds.MaxAttempts} attempts, {ContinuityBounds.MaxWall.TotalMinutes} minutes, reruns included); a fresh budget needs a fresh {ContinuityEvidence.Variable} directory", []);
            await evidence.WriteAsync(row, spent, [], TimeSpan.Zero, ct);
            return spent;
        }

        Stopwatch wall = Stopwatch.StartNew();
        List<Attempt> attempts = [];
        RowResult result = RowResult.NotProved("no attempt was made", []);
        try
        {
            if (lever is not null)
            {
                await lever(ct);
            }

            while (attempts.Count < budget.Attempts && wall.Elapsed < budget.Wall)
            {
                attempts.Add(await attempt(attempts.Count + 1, ct));
                GatewayCapture capture = await WaitAndReadAsync(control, attempts.SelectMany(each => each.Steps), ct);
                if (ContinuityObserver.Control(control, capture) is { } uncontrolled)
                {
                    result = uncontrolled;
                    break;
                }

                result = judge(attempts, capture);
                if (result.Verdict != Verdict.NotProved)
                {
                    break;
                }
            }
        }
        catch (Exception stopped) when (stopped is not OperationCanceledException)
        {
            result = RowResult.NotProved($"the driver stopped with {stopped.GetType().Name}", result.Observations);
            throw;
        }
        finally
        {
            await evidence.WriteAsync(row, result, attempts, wall.Elapsed, CancellationToken.None);
        }

        return result;
    }

    /// <summary>Rows 6 and 8: one drive, one wait, one read, one verdict, written as one attempt.</summary>
    public async Task<RowResult> OnceAsync<T>(
        string row,
        Step control,
        Func<CancellationToken, Task<T>> drive,
        Func<T, IEnumerable<Step>> steps,
        Func<T, GatewayCapture, RowResult> judge,
        CancellationToken ct)
    {
        Stopwatch wall = Stopwatch.StartNew();
        RowResult result = RowResult.NotProved("the row did not finish", []);
        Attempt? recorded = null;
        try
        {
            T driven = await drive(ct);
            recorded = new Attempt(1, [.. steps(driven)]);
            GatewayCapture capture = await WaitAndReadAsync(control, recorded.Steps, ct);
            result = ContinuityObserver.Control(control, capture) ?? judge(driven, capture);
        }
        catch (Exception stopped) when (stopped is not OperationCanceledException)
        {
            result = RowResult.NotProved($"the driver stopped with {stopped.GetType().Name}", []);
            throw;
        }
        finally
        {
            await evidence.WriteAsync(row, result, recorded is null ? [] : [recorded], wall.Elapsed, CancellationToken.None);
        }

        return result;
    }

    private async Task<GatewayCapture> WaitAndReadAsync(Step control, IEnumerable<Step> steps, CancellationToken ct)
    {
        await Task.Delay(Delivery, ct);
        DateTimeOffset last = steps.Select(step => step.At).Append(control.At).Max();
        return await log.ReadAsync(control.At - ClockMargin, last + ContinuityBounds.LogDelivery, ct);
    }
}

/// <summary>
/// The raw client's requests as a continuity row sends them (Q137): each with a fresh sampled
/// <c>traceparent</c>, the session's cookies when it has one, and nothing kept of the answer but its
/// status. A connection error becomes missing coverage (<see cref="Step.Failed"/>).
/// </summary>
public sealed class TracedClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
{
    public async Task<Step> SendAsync(string name, HttpMethod method, Uri url, SessionCookies? cookies, int? expect, CancellationToken ct, string? route = null)
    {
        ClientTrace trace = ClientTrace.Fresh();
        DateTimeOffset at = DateTimeOffset.UtcNow;
        using HttpRequestMessage request = new(method, url);
        request.Headers.TryAddWithoutValidation(ClientTrace.Header, trace.Traceparent);
        if (cookies is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookies.Reveal());
        }

        try
        {
            using HttpResponseMessage response = await send(request, ct);
            return new Step(name, trace.TraceId, at, (int)response.StatusCode) { Expect = expect, Route = route };
        }
        catch (Exception failed) when (failed is HttpRequestException or IOException or SocketException
                                       || (failed is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return Step.Failed(name, trace, at, failed) with { Expect = expect, Route = route };
        }
    }
}

/// <summary>
/// A session's cookies as a <c>Cookie</c> header value. Like <see cref="Password"/> it prints, formats
/// and serializes as nothing, so no output or evidence file can carry a cookie value (N-16).
/// </summary>
public sealed class SessionCookies
{
    private readonly string value;

    public SessionCookies(string value) => this.value = value;

    /// <summary>The header value, for the one call that puts it on a request.</summary>
    public string Reveal() => value;

    public override string ToString() => "[cookies]";
}

/// <summary>A gateway task as one <c>describe-tasks</c> read gives it.</summary>
public sealed record EcsTaskState(string Id, string LastStatus, string DesiredStatus, DateTimeOffset? StartedAt, DateTimeOffset? StoppedAt, string? Digest)
{
    public EcsTask Task => new(Id, StartedAt, StoppedAt, Digest);
}

/// <summary>
/// What the continuity rows read and pull on ECS, through the AWS CLI: the gateway service's tasks
/// with their states, timestamps and image digests (row 6, Q146), and row 4's <c>stop-task</c>
/// (Q140, Q145), which drains (#348 Q21).
/// </summary>
public sealed class ContinuityEcs(Cell cell, ICommandRunner runner)
{
    /// <summary>The variable naming the task the tunnel's hop runs on, which row 4 never stops (Q145).</summary>
    public const string TunnelTaskVariable = "SPA_TUNNEL_TASK";

    /// <summary>The gateway's container in the task definition (infra/modules/cell/ecs.tf, <c>gateway_container_name</c>).</summary>
    public const string GatewayContainer = "gateway";

    /// <summary>The service's tasks whose desired status is RUNNING, pending ones included (https://docs.aws.amazon.com/cli/latest/reference/ecs/list-tasks.html).</summary>
    public async Task<IReadOnlyList<EcsTaskState>> ListedAsync(CancellationToken ct)
    {
        string listed = await runner.RunAsync("aws",
            ["ecs", "list-tasks", "--cluster", cell.Cluster, "--service-name", cell.Service, "--desired-status", "RUNNING", "--output", "json", "--region", cell.Region], ct);
        using JsonDocument list = JsonDocument.Parse(listed);
        string[] ids = [.. list.RootElement.GetProperty("taskArns").EnumerateArray().Select(arn => AfterLast(arn.GetString()!))];
        return ids.Length == 0 ? [] : await DescribeAsync(ids, ct);
    }

    /// <summary>The tasks by id, as <c>describe-tasks</c> gives them; the CLI v2 prints timestamps in ISO 8601.</summary>
    public async Task<IReadOnlyList<EcsTaskState>> DescribeAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        string described = await runner.RunAsync("aws",
            ["ecs", "describe-tasks", "--cluster", cell.Cluster, "--tasks", .. ids, "--output", "json", "--region", cell.Region], ct);
        using JsonDocument tasks = JsonDocument.Parse(described);
        return [.. tasks.RootElement.GetProperty("tasks").EnumerateArray().Select(task => new EcsTaskState(
            AfterLast(task.GetProperty("taskArn").GetString()!),
            task.GetProperty("lastStatus").GetString() ?? "",
            task.GetProperty("desiredStatus").GetString() ?? "",
            Time(task, "startedAt"),
            Time(task, "stoppedAt"),
            task.TryGetProperty("containers", out JsonElement containers)
                ? containers.EnumerateArray().Where(c => c.GetProperty("name").GetString() == GatewayContainer)
                    .Select(c => c.TryGetProperty("imageDigest", out JsonElement digest) ? digest.GetString() : null).FirstOrDefault()
                : null))];
    }

    /// <summary>
    /// Row 4's lever (Q145): refuses unless the tunnel's task is exactly one of exactly two RUNNING
    /// tasks, then stops the other and returns its id.
    /// </summary>
    public async Task<string> StopTheOtherAsync(string? tunnelTask, CancellationToken ct)
    {
        string[] running = [.. (await ListedAsync(ct)).Where(task => task.LastStatus == "RUNNING").Select(task => task.Id)];
        if (string.IsNullOrEmpty(tunnelTask) || running.Length != 2 || running.Count(id => id == tunnelTask) != 1)
        {
            throw new InvalidOperationException(
                $"row 4 stops nothing: {TunnelTaskVariable} must name exactly one of the service's two RUNNING tasks, and it is '{tunnelTask}' with {running.Length} running ({string.Join(", ", running)}).");
        }

        string other = running.Single(id => id != tunnelTask);
        // https://docs.aws.amazon.com/cli/latest/reference/ecs/stop-task.html: SIGTERM, then stopTimeout, as a drain.
        await runner.RunAsync("aws",
            ["ecs", "stop-task", "--cluster", cell.Cluster, "--task", other, "--reason", "continuity row 4 (#348)", "--output", "json", "--region", cell.Region], ct);
        return other;
    }

    private static DateTimeOffset? Time(JsonElement task, string name) =>
        task.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture)
            : null;

    private static string AfterLast(string arn) => arn[(arn.LastIndexOf('/') + 1)..];
}
