using System.Diagnostics;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// What one attempt of a fragments row drove: its context, its requests, and its recording; or, where
/// the driver cannot yet drive a part of the row, <see cref="Missing"/> naming it, which the run reports
/// as not proved without judging.
/// </summary>
public sealed record Driven<T>(FragmentsContext Context, IReadOnlyList<Step> Steps, T Record)
{
    public string? Missing { get; init; }

    /// <summary>
    /// When the drive stopped watching the cell, for a row whose lines come from something other than its
    /// own requests (F-31's rolling deploy): the read keeps lines delivered up to
    /// <see cref="ContinuityBounds.LogDelivery"/> after the later of this and the last request.
    /// </summary>
    public DateTimeOffset? Until { get; init; }
}

/// <summary>
/// One fragments row's run (#1005), in #358's form: the row's bound read back from its earlier evidence,
/// nothing driven once it is spent, each attempt followed by the log-delivery wait and a read of the
/// gateway's lines cut at it, the control checked on every read, and the evidence written whatever the
/// verdict. The verdict's message passes <see cref="FragmentsEvidence.Unsafe"/> before anything prints it.
/// </summary>
public sealed class FragmentsRun(FragmentsEvidence evidence, ContinuityLog log)
{
    /// <summary>How long the run waits before each read; the read always runs to <see cref="ContinuityBounds.LogDelivery"/> after the last request.</summary>
    public TimeSpan Delivery { get; init; } = ContinuityBounds.LogDelivery;

    private static readonly TimeSpan ClockMargin = TimeSpan.FromSeconds(5);

    public async Task<RowResult> RowAsync<T>(
        string row,
        Step control,
        Func<int, CancellationToken, Task<Driven<T>>> attempt,
        Func<Driven<T>, GatewayCapture, RowResult> judge,
        CancellationToken ct)
    {
        RowBound budget = await evidence.BudgetAsync(row, ct);
        RowBound bound = FragmentsBounds.Of(row);
        if (budget.Spent)
        {
            RowResult spent = RowResult.NotProved(
                $"the row's bounds are spent ({bound.Attempts} attempts, {bound.Wall.TotalMinutes} minutes, reruns included); a fresh budget needs a fresh {FragmentsEvidence.Variable} directory", []);
            await evidence.WriteAsync(row, spent, [], 0, TimeSpan.Zero, ct);
            return spent;
        }

        Stopwatch wall = Stopwatch.StartNew();
        List<FragmentsAttempt> attempts = [];
        RowResult result = RowResult.NotProved("no attempt was made", []);
        try
        {
            while (attempts.Count < budget.Attempts && wall.Elapsed < budget.Wall)
            {
                Driven<T> driven = await attempt(attempts.Count + 1, ct);
                if (driven.Missing is { } missing)
                {
                    attempts.Add(Recorded(attempts.Count + 1, driven, control, null));
                    result = RowResult.NotProved($"the driver cannot yet {missing}", []);
                    break;
                }

                GatewayCapture capture = await WaitAndReadAsync(control, driven.Steps, driven.Until, ct);
                attempts.Add(Recorded(attempts.Count + 1, driven, control, capture));
                result = ContinuityObserver.Control(control, capture) ?? Printable(judge(driven, capture));
                if (result.Verdict != Verdict.NotProved)
                {
                    break;
                }
            }
        }
        catch (Exception stopped)
        {
            if (stopped is not OperationCanceledException)
            {
                result = RowResult.NotProved($"the driver stopped with {stopped.GetType().Name}", result.Observations);
            }

            await WriteBesideAsync(stopped, () => WriteAsync(row, result, attempts, wall.Elapsed));
            throw;
        }

        await WriteAsync(row, result, attempts, wall.Elapsed);
        return result;
    }

    /// <summary>
    /// The row's file; one whose attempts held a credential's shape is written again with none of them
    /// and the refusal as its reason, so the row still records its spent bounds and says why (N-16).
    /// </summary>
    private async Task WriteAsync(string row, RowResult result, IReadOnlyList<FragmentsAttempt> attempts, TimeSpan wall)
    {
        try
        {
            await evidence.WriteAsync(row, result, attempts, attempts.Count, wall, CancellationToken.None);
        }
        catch (UnsafeEvidenceException unsafeEvidence)
        {
            RowResult refused = RowResult.NotProved($"the row's attempts held {unsafeEvidence.Kind}, so none of them was written", []);
            await evidence.WriteAsync(row, refused, [], attempts.Count, wall, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// A verdict whose message would print a credential's shape keeps its verdict and loses its reason and
    /// observations: a failure message is output too (AGENTS.md).
    /// </summary>
    public static RowResult Printable(RowResult result) =>
        FragmentsEvidence.Unsafe(result.Message) is { } kind
            ? new RowResult(result.Verdict, $"the observations held {kind}, so none is printed", [])
            : result;

    /// <summary>The attempt with the task ids its steps and the control were found on added to the driver's.</summary>
    private static FragmentsAttempt Recorded<T>(int number, Driven<T> driven, Step control, GatewayCapture? capture)
    {
        string[] located = capture is null
            ? []
            : [.. driven.Steps.Append(control).Select(capture.Locate).Where(place => place.Kind == PlacementKind.OnTask).Select(place => place.TaskId!)];
        FragmentsContext context = driven.Context with
        {
            TaskIds = [.. driven.Context.TaskIds.Concat(located).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        };
        return new FragmentsAttempt(number, context, driven.Steps, FragmentsObserver.Recorded(driven.Record));
    }

    private static async Task WriteBesideAsync(Exception failure, Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (Exception failed)
        {
            throw new AggregateException(failure, failed);
        }
    }

    private async Task<GatewayCapture> WaitAndReadAsync(Step control, IEnumerable<Step> steps, DateTimeOffset? until, CancellationToken ct)
    {
        await Task.Delay(Delivery, ct);
        DateTimeOffset last = steps.Select(step => step.At).Append(control.At).Append(until ?? control.At).Max();
        return await log.ReadAsync(control.At - ClockMargin, last + ContinuityBounds.LogDelivery, ct);
    }
}
