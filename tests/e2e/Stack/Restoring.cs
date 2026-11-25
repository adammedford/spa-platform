namespace Gateway.E2ETests.Stack;

/// <summary>
/// A lever on the cell pulled for one drive and put back after it (#1411's decision of 2026-11-24): the
/// drive's work, then the restore under a bound of its own, whether the work answered or threw. A restore
/// that fails is the row's failure; with a failed work too, both are thrown together.
/// </summary>
public static class Restoring
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> work, Func<CancellationToken, Task> restore, TimeSpan bound)
    {
        T result;
        try
        {
            result = await work();
        }
        catch (Exception failed)
        {
            try
            {
                await RestoreAsync(restore, bound);
            }
            catch (Exception unrestored)
            {
                throw new AggregateException(failed, unrestored);
            }

            throw;
        }

        await RestoreAsync(restore, bound);
        return result;
    }

    private static async Task RestoreAsync(Func<CancellationToken, Task> restore, TimeSpan bound)
    {
        using CancellationTokenSource limit = new(bound);
        await restore(limit.Token);
    }
}
