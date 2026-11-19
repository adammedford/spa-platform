using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Stack;

// ---- the recordings: what a driver hands the observer, and what the evidence file holds ----

/// <summary>A redirect as O1 records it: its status and its path, with every query value removed (#1005 O1).</summary>
public sealed record Redirect(int Status, string Path);

/// <summary>
/// One value of N in one engine (#1005 O1), each run being the redirects the browser followed: the uninterrupted sign-in with Cognito's managed-login
/// session live, the interactive sign-in, each chain hop, each as runs of redirects split wherever a
/// page interrupted them, and whether the navigation ended on <c>returnUrl</c>.
/// </summary>
public sealed record RedirectRun(
    int N,
    IReadOnlyList<IReadOnlyList<Redirect>>? Uninterrupted,
    IReadOnlyList<IReadOnlyList<Redirect>>? Interactive,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<Redirect>>>? Hops,
    bool? ReachedReturnUrl);

/// <summary>An engine's boundary probe (#1005 O1): the most redirects it followed and the count at which it stopped.</summary>
public sealed record BoundaryProbe(int Followed, int StoppedAt);

public sealed record EngineRedirects(string Engine, string? Version, IReadOnlyList<RedirectRun> Runs, BoundaryProbe? Probe);

public sealed record RedirectRecord(IReadOnlyList<EngineRedirects> Engines);

/// <summary>One topology of O2 (#1005 O2): one task or two, the due sets, the declared sample, what each refresh took and each write counted.</summary>
public sealed record LatencySample(int Tasks, int DueSets, int DeclaredSample, int Repetitions, IReadOnlyList<double>? LatenciesMs, IReadOnlyList<WriteCount>? Writes);

public sealed record WriteCount(int Attempts, int CasConflicts, bool BudgetExhausted);

/// <summary>
/// O2's overlapping chain and fallback callbacks on two tasks: the tasks that wrote, each write's
/// attempts, and what the elapsed bound counts beside the attempts: the outer <c>GetAsync</c> calls and
/// the rest of the callback.
/// </summary>
public sealed record CallbackOverlap(IReadOnlyList<string>? TasksWrote, IReadOnlyList<int>? AttemptsPerWrite, int OuterGets, double RestSeconds);

public sealed record RefreshContention(IReadOnlyList<LatencySample> Topologies, CallbackOverlap? Callbacks);

/// <summary>
/// O4's sizes, computed by the driver from the item <c>scan</c> returned and kept as figures only: no
/// attribute value is recorded, since the item's key is the session's and its ticket a protected one.
/// </summary>
public sealed record ItemSize(long? StoredItemBytes, long? PayloadBytes);

/// <summary>One CloudWatch datapoint of <c>ConsumedWriteCapacityUnits</c>: the period's start and its Sum.</summary>
public sealed record CapacityPeriod(DateTimeOffset Start, double Sum);

/// <summary>
/// O4's capacity sample (#1005 O4): the named workload's exact start and stop and operation count, the
/// table's periods, and what isolation found: other writers and overlapping workloads, null when not checked.
/// </summary>
public sealed record CapacitySample(
    string Workload,
    DateTimeOffset Start,
    DateTimeOffset Stop,
    int Operations,
    IReadOnlyList<CapacityPeriod>? Periods,
    IReadOnlyList<string>? OtherWriters,
    IReadOnlyList<string>? OverlappingWorkloads);

public sealed record RecordSize(ItemSize? Size, CapacitySample? Capacity);

/// <summary>O5's cases (#961 O5), each by its name in <see cref="FragmentsObserver.MapCases"/>.</summary>
public sealed record MapCase(string UserAgent, string Case, bool? Loaded);

public sealed record ImportMapRecord(IReadOnlyList<MapCase> Cases);

/// <summary>One half of O8: when it was observed, and what the second client's <c>/bff/login?fragment=</c> showed.</summary>
public sealed record FallbackHalf(DateTimeOffset At, bool? ShowedPage, bool? Prompted);

/// <summary>O8 (#961 O8): the managed-login sign-in's time and the two halves, the second after the hour.</summary>
public sealed record FallbackRecord(DateTimeOffset? SignedInAt, FallbackHalf? Inside, FallbackHalf? After);

public sealed record BrowserHeaderLimit(string UserAgent, int? LargestAcceptedBytes);

/// <summary>O9 (#961 O9): the merged CSP's size at the cap, and the largest response header each layer accepted.</summary>
public sealed record HeaderLimitRecord(int? Cap, int? MergedCspBytes, int? AlbLargestAcceptedBytes, int? KestrelLargestAcceptedBytes, IReadOnlyList<BrowserHeaderLimit> Browsers);

/// <summary>One O12 case (#961 O12): the fragment, its entry's name, whether the import loaded, and the chunk's resolved path.</summary>
public sealed record EntryImport(string FragmentId, string Entry, bool? Loaded, string? ChunkPath);

public sealed record EntryImportRecord(IReadOnlyList<EntryImport> Cases);

/// <summary>One <c>/bff/chain</c> step's Fetch Metadata, as its request carried it (#961 O13).</summary>
public sealed record ChainStep(string? Site, string? Mode, string? Dest);

public sealed record ChainRun(string UserAgent, IReadOnlyList<ChainStep> Steps, bool? Completed);

public sealed record ChainRecord(IReadOnlyList<ChainRun> Runs);

/// <summary>
/// One N-46 case: where it landed and the session record's version before and after, and, for the
/// <c>expired</c> case, how long after its continuation was minted the step was sent (#1005 O13: "one
/// past <c>ChainLifetime</c>"), null when the driver did not record it.
/// </summary>
public sealed record ChainRefusal(string UserAgent, string Case, string? LandedOn, long? VersionBefore, long? VersionAfter)
{
    public TimeSpan? SinceMint { get; init; }
}

public sealed record ChainRefusalRecord(IReadOnlyList<ChainRefusal> Cases);

/// <summary>One <c>fragment publish</c> run of F-T5: its exit, S3's refusal status if it was refused, and the SHA-256 of the bytes it published.</summary>
public sealed record PublishRun(bool? Completed, int? RefusedWith, string BytesSha256);

/// <summary>
/// One variant of F-T5 (#961's traces comment, F-T2 against the cell's bucket): <c>different</c> or
/// <c>same</c> bytes, the two runs, the stored object's SHA-256, and the SHA-256 the URL served.
/// </summary>
public sealed record PublishRace(string Variant, IReadOnlyList<PublishRun> Runs, string? StoredSha256, string? ServedSha256);

public sealed record PublishRaceRecord(IReadOnlyList<PublishRace> Variants);

/// <summary>An image of F-31: the trunk commit it was built from (null for the fragments image if unknown) and its digest.</summary>
public sealed record ImageProvenance(string? TrunkCommit, string? Digest);

/// <summary>A gateway task of F-31 with its image digest, as <c>describe-tasks</c> gave it.</summary>
public sealed record TaskImage(string TaskId, string? Digest);

/// <summary>F-31 (#961 row 10): the pre-fragments image, the fragments image, the <c>schemaVersion: 2</c> document's revision, and the tasks.</summary>
public sealed record RollingRefusalRecord(ImageProvenance? PreFragments, ImageProvenance? Fragments, long? Revision, IReadOnlyList<TaskImage> Tasks);

/// <summary>
/// N-38's steps by name (#961 O3): <c>logout</c> on one task, <c>refresh</c> on the other, and the
/// provider's answer to the successor after the logout, which the driver records as a probe.
/// </summary>
public sealed record SuccessorRecord(string SessionRef, string FragmentId, int? ProbeStatus, DateTimeOffset? ProbeAt);

/// <summary>
/// The fragments class's observer (#1005): each row's recording, and for the rows that read the
/// gateway's lines the capture, turned into passed, failed or not proved. A record row (O1, O2, O4,
/// O9) passes when its figures are complete and is not proved when any is missing; a broken rule is
/// failed. Nothing here sends a request or runs a command: <see cref="FragmentsReaders"/> reads the
/// AWS CLI's answers, and the driver the browser's.
/// </summary>
public static partial class FragmentsObserver
{
    /// <summary>The engines O1 records (#1005 O1).</summary>
    public static readonly IReadOnlyList<string> Engines = ["chromium", "firefox", "webkit"];

    /// <summary>O1's N, 0 to 6 (#961 R2: six fragments for O1).</summary>
    public const int MaxN = 6;

    /// <summary>
    /// The Fetch standard's redirect limit: a request whose redirect count exceeds 20 is a network error
    /// (https://fetch.spec.whatwg.org/#http-redirect-fetch, step 6), so no navigation that completed
    /// followed a longer run.
    /// </summary>
    public const int FetchRedirectLimit = 20;

    /// <summary>O2's due token sets: the shell's and five fragments' (#1005 O2, "1 + 5").</summary>
    public const int DueSets = 6;

    /// <summary>The refresh write deadline, acquire + TTL − 1 s (#961 "The session record").</summary>
    public static readonly TimeSpan WriteDeadline = TimeSpan.FromSeconds(20);

    /// <summary><c>StoreDeadline</c>: one per <c>ReplaceAsync</c> across its put, re-read and retry, and one per outer <c>GetAsync</c> (#1005 O2).</summary>
    public static readonly TimeSpan StoreDeadline = TimeSpan.FromSeconds(5);

    /// <summary>The callback's elapsed limit: <c>P</c> + 15 s = <c>ShutdownTimeout</c>, 25 s (#961's second part, "Other rules, names and constants").</summary>
    public static readonly TimeSpan CallbackElapsedLimit = TimeSpan.FromSeconds(25);

    /// <summary>MergeBudget's floor, the placeholder #961 sets (3 attempts).</summary>
    public const int MergeBudgetFloor = 3;

    /// <summary>O5's cases and whether each must load (#961 O5).</summary>
    public static readonly IReadOnlyDictionary<string, bool> MapCases = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["strict-sha256-loads"] = true,
        ["wrong-hash-blocked"] = false,
        ["crlf-with-lf-hash-loads"] = true,
        ["sha384-admits"] = true,
    };

    /// <summary>N-46's cases (#961 O13).</summary>
    public static readonly IReadOnlyList<string> RefusalCases = ["replayed", "foreign", "expired"];

    /// <summary>O1 (record): every engine with its version, every N with its runs and hops, each engine's probe, and the cap.</summary>
    public static RowResult Redirects(RedirectRecord record)
    {
        List<string> seen = [];
        List<string> missing = [];
        foreach (string engine in Engines)
        {
            EngineRedirects? found = record.Engines.FirstOrDefault(each => each.Engine == engine);
            if (found is null || string.IsNullOrWhiteSpace(found.Version))
            {
                missing.Add($"{engine} has no {(found is null ? "run" : "version")}");
                continue;
            }

            if (found.Probe is null)
            {
                missing.Add($"{engine} has no boundary probe");
            }

            for (int n = 0; n <= MaxN; n++)
            {
                RedirectRun? run = found.Runs.FirstOrDefault(each => each.N == n);
                if (run is null)
                {
                    missing.Add($"{engine} has no run at N={n}");
                    continue;
                }

                if (run.Uninterrupted is null || run.Interactive is null || run.ReachedReturnUrl is null)
                {
                    missing.Add($"{engine} at N={n} has no {(run.Uninterrupted is null ? "uninterrupted run" : run.Interactive is null ? "interactive run" : "terminal navigation")}");
                    continue;
                }

                if (run.Hops is null || run.Hops.Count != n)
                {
                    missing.Add($"{engine} at N={n} records {run.Hops?.Count.ToString(CultureInfo.InvariantCulture) ?? "no"} hop counts of {n}");
                    continue;
                }

                IEnumerable<Redirect> every = run.Uninterrupted.Concat(run.Interactive).Concat(run.Hops.SelectMany(hop => hop)).SelectMany(each => each);
                if (every.FirstOrDefault(redirect => redirect.Path.IndexOfAny(['?', '#']) >= 0) is { } leaked)
                {
                    return RowResult.Failed($"{engine} at N={n} recorded a redirect path with its query or fragment ({leaked.Status}); a redirect is recorded by its path alone", seen);
                }

                int longest = new[] { Longest(run.Uninterrupted), Longest(run.Interactive) }.Concat(run.Hops.Select(Longest)).Max();
                // A run is the redirects the browser followed; it stops with a network error past the
                // limit, so a longer run is a record no browser made.
                if (longest > FetchRedirectLimit)
                {
                    return RowResult.Failed($"{engine} at N={n} recorded a redirect run of {longest}, past the Fetch standard's {FetchRedirectLimit}, at which a browser stops with a network error", seen);
                }

                seen.Add($"{engine} {found.Version} N={n}: uninterrupted {Longest(run.Uninterrupted)}, interactive {Longest(run.Interactive)}, hops [{string.Join(", ", run.Hops.Select(Longest))}], returnUrl {(run.ReachedReturnUrl.Value ? "reached" : "not reached")}");
            }

            if (found.Probe is { } probe)
            {
                seen.Add($"{engine} probe: followed {probe.Followed}, stopped at {probe.StoppedAt}");
            }
        }

        if (missing.Count > 0)
        {
            return RowResult.NotProved("O1's record is incomplete: " + string.Join("; ", missing), seen);
        }

        int[] completing = [.. Enumerable.Range(0, MaxN + 1).Where(n => Engines.All(engine =>
            record.Engines.First(each => each.Engine == engine).Runs.First(run => run.N == n).ReachedReturnUrl == true))];
        if (completing.Length == 0)
        {
            return RowResult.NotProved("no N from 0 to 6 completed in every engine, so O1 sets no cap", seen);
        }

        seen.Add($"cap: N={completing.Max()}, the largest N whose navigation completed in {string.Join(", ", Engines)}; margin 0, a policy choice");
        return RowResult.Passed($"O1 recorded; the cap is {completing.Max()}", seen);
    }

    private static int Longest(IReadOnlyList<IReadOnlyList<Redirect>> runs) => runs.Count == 0 ? 0 : runs.Max(run => run.Count);

    /// <summary>
    /// O2 (record): both topologies at 1 + 5 due sets with a complete sample and per-write counts, and
    /// the overlapping callbacks written on two tasks. Deadline misses, exhausted budgets and a callback
    /// bound with no room for the attempts are flagged in the record (#1005 O2): the gates and leases
    /// are confirmed only with none of the first two, and the third records a required revision to #961.
    /// </summary>
    public static RowResult Contention(RefreshContention record)
    {
        List<string> seen = [];
        List<string> flags = [];
        foreach (int tasks in new[] { 1, 2 })
        {
            LatencySample? sample = record.Topologies.FirstOrDefault(each => each.Tasks == tasks && each.DueSets == DueSets);
            if (sample is null)
            {
                return RowResult.NotProved($"O2 has no {tasks}-task topology at 1 + 5 due sets", seen);
            }

            if (sample.Writes is null || sample.Writes.Count == 0)
            {
                return RowResult.NotProved($"O2's {tasks}-task topology has no per-write counts", seen);
            }

            int declared = sample.DeclaredSample * sample.Repetitions;
            if (declared <= 0 || sample.LatenciesMs is null || sample.LatenciesMs.Count != declared)
            {
                return RowResult.NotProved($"O2's {tasks}-task latency sample holds {sample.LatenciesMs?.Count ?? 0} of the {declared} declared ({sample.DeclaredSample} x {sample.Repetitions})", seen);
            }

            int misses = sample.LatenciesMs.Count(ms => ms > WriteDeadline.TotalMilliseconds);
            int exhausted = sample.Writes.Count(write => write.BudgetExhausted);
            seen.Add($"{tasks} task(s): p95 {P95(sample.LatenciesMs).ToString("0.#", CultureInfo.InvariantCulture)} ms over {declared}; CAS conflicts {sample.Writes.Sum(write => write.CasConflicts)}; attempts per write max {sample.Writes.Max(write => write.Attempts)}; write deadline {WriteDeadline.TotalSeconds} s missed {misses}; budgets exhausted {exhausted}");
            if (misses > 0 || exhausted > 0)
            {
                flags.Add($"{tasks} task(s): {misses} refresh(es) past the write deadline, {exhausted} budget(s) exhausted");
            }
        }

        if (record.Callbacks is not { TasksWrote: { } wrote, AttemptsPerWrite: { Count: > 0 } attempts } callbacks)
        {
            return RowResult.NotProved("O2 has no overlapping callbacks with their per-write attempts", seen);
        }

        if (wrote.Distinct(StringComparer.Ordinal).Count() < 2)
        {
            return RowResult.NotProved($"O2's overlapping callbacks wrote on {wrote.Distinct(StringComparer.Ordinal).Count()} task(s), not two", seen);
        }

        int mergeBudget = Math.Max(MergeBudgetFloor, attempts.Max() + 1);
        TimeSpan elapsed = (StoreDeadline * mergeBudget) + (StoreDeadline * callbacks.OuterGets) + TimeSpan.FromSeconds(callbacks.RestSeconds);
        seen.Add($"callbacks on {string.Join(", ", wrote.Distinct(StringComparer.Ordinal))}: max attempts per write {attempts.Max()}; MergeBudget {mergeBudget}; elapsed bound {elapsed.TotalSeconds} s of {CallbackElapsedLimit.TotalSeconds} s");
        if (elapsed > CallbackElapsedLimit)
        {
            flags.Add($"the callback bound of {elapsed.TotalSeconds} s cannot hold MergeBudget {mergeBudget}: a revision to #961 is required");
        }

        seen.AddRange(flags.Select(flag => "flag: " + flag));
        return RowResult.Passed(
            flags.Count == 0
                ? $"O2 recorded; the gates and leases are confirmed; MergeBudget {mergeBudget}"
                : $"O2 recorded with {flags.Count} flag(s); the gates and leases are not confirmed by it",
            seen);
    }

    /// <summary>The nearest-rank 95th percentile.</summary>
    private static double P95(IReadOnlyList<double> values)
    {
        double[] sorted = [.. values.Order()];
        return sorted[Math.Max(0, (int)Math.Ceiling(0.95 * sorted.Length) - 1)];
    }

    /// <summary>
    /// O4 (record): the stored item's bytes beside the serialized payload's, and a capacity sample with
    /// every complete minute of the workload, quiet controls on both sides and isolation checked, else
    /// the verdict is withheld (#1005 O4).
    /// </summary>
    public static RowResult Size(RecordSize record)
    {
        List<string> seen = [];
        if (record.Size is not { StoredItemBytes: { } stored, PayloadBytes: { } payload })
        {
            return RowResult.NotProved("O4 has no stored-item or serialized-payload size", seen);
        }

        if (payload <= 0 || stored < payload)
        {
            return RowResult.Failed($"O4's stored item ({stored} bytes) is smaller than its serialized payload ({payload} bytes), which it holds", seen);
        }

        seen.Add($"stored item {stored} bytes, serialized payload {payload} bytes");
        if (record.Capacity is not { } capacity)
        {
            return RowResult.NotProved("O4 has no capacity sample", seen);
        }

        if (capacity.OtherWriters is null || capacity.OverlappingWorkloads is null)
        {
            return RowResult.NotProved("O4's isolation was not checked: other writers and overlapping workloads are unrecorded", seen);
        }

        if (capacity.OtherWriters.Count > 0 || capacity.OverlappingWorkloads.Count > 0)
        {
            return RowResult.NotProved($"O4's sample is contaminated: {capacity.OtherWriters.Count} other writer(s), {capacity.OverlappingWorkloads.Count} overlapping workload(s)", seen);
        }

        if (capacity.Periods is null || capacity.Stop <= capacity.Start || capacity.Operations <= 0)
        {
            return RowResult.NotProved("O4's capacity sample has no periods, no operations or no interval", seen);
        }

        DateTimeOffset first = Minute(capacity.Start);
        DateTimeOffset last = Minute(capacity.Stop);
        List<double> sums = [];
        for (DateTimeOffset minute = first; minute <= last; minute = minute.AddMinutes(1))
        {
            CapacityPeriod? period = capacity.Periods.FirstOrDefault(each => each.Start == minute);
            if (period is null)
            {
                return RowResult.NotProved($"O4 has no ConsumedWriteCapacityUnits period at {minute:u}", seen);
            }

            sums.Add(period.Sum);
        }

        // A table with no traffic still publishes its datapoints, with Sum 0 ("Even tables with zero traffic will
        // have the SampleCount emitted regularly, but the sample values will always be zero",
        // https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/metrics-dimensions.html#ConsumedWriteCapacityUnits),
        // so a control with no datapoint is missing period data and withholds the verdict (#1005 O4).
        foreach (DateTimeOffset control in new[] { first.AddMinutes(-1), last.AddMinutes(1) })
        {
            if (capacity.Periods.FirstOrDefault(each => each.Start == control) is not { } quiet)
            {
                return RowResult.NotProved($"O4 has no ConsumedWriteCapacityUnits period at {control:u}, a control period", seen);
            }

            if (quiet.Sum > 0)
            {
                return RowResult.NotProved($"O4's control period at {control:u} consumed {quiet.Sum} WCU, so the table was not idle", seen);
            }
        }

        // Each write consumes at least one WCU (https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/provisioned-capacity-mode.html).
        if (sums.Sum() < capacity.Operations)
        {
            return RowResult.NotProved($"O4's periods sum to {sums.Sum()} WCU for {capacity.Operations} writes, fewer than one each, so the sample is incomplete", seen);
        }

        seen.Add($"{capacity.Workload}: {capacity.Start:O} to {capacity.Stop:O}, {capacity.Operations} writes, {sums.Count} period(s), {sums.Sum()} WCU, controls quiet");
        return RowResult.Passed("O4 recorded", seen);
    }

    private static DateTimeOffset Minute(DateTimeOffset at) => new(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, TimeSpan.Zero);

    /// <summary>F-39 (O5): every case in every user agent the run names, each as #961 says it must end.</summary>
    public static RowResult ImportMap(ImportMapRecord record, IReadOnlyList<string> userAgents)
    {
        List<string> seen = [];
        if (userAgents.Count == 0)
        {
            return RowResult.NotProved("the run names no user agent", seen);
        }

        foreach (string agent in userAgents)
        {
            foreach ((string name, bool loads) in MapCases)
            {
                MapCase? found = record.Cases.FirstOrDefault(each => each.UserAgent == agent && each.Case == name);
                if (found?.Loaded is not { } loaded)
                {
                    return RowResult.NotProved($"the map case {name} is missing for {agent}", seen);
                }

                if (loaded != loads)
                {
                    return RowResult.Failed($"in {agent} the map case {name} {(loaded ? "loaded" : "was blocked")}", seen);
                }

                seen.Add($"{agent}: {name} {(loaded ? "loaded" : "blocked")}");
            }
        }

        return RowResult.Passed("every import-map case ended as #961 O5 says, in every user agent", seen);
    }

    /// <summary>F-40 (O8): no page inside the managed-login hour, a prompt after it, both observed.</summary>
    public static RowResult Fallback(FallbackRecord record)
    {
        List<string> seen = [];
        if (record.SignedInAt is not { } signedIn || record.Inside is not { ShowedPage: { } showed } inside)
        {
            return RowResult.NotProved("O8's first half, inside the managed-login hour, was not observed", seen);
        }

        if (inside.At - signedIn >= FragmentsBounds.ManagedLoginHour || inside.At < signedIn)
        {
            return RowResult.NotProved($"O8's first half came {(inside.At - signedIn).TotalMinutes:0.#} minutes after the sign-in, not inside the hour", seen);
        }

        if (showed)
        {
            return RowResult.Failed("the second client's fallback showed a page inside the managed-login hour", seen);
        }

        seen.Add($"inside the hour, at +{(inside.At - signedIn).TotalMinutes:0.#} min: no page");
        if (record.After is not { Prompted: { } prompted } after)
        {
            return RowResult.NotProved("O8's second half, after the managed-login hour, was not observed", seen);
        }

        if (after.At - signedIn < FragmentsBounds.ManagedLoginHour)
        {
            return RowResult.NotProved($"O8's second half came {(after.At - signedIn).TotalMinutes:0.#} minutes after the sign-in, inside the hour", seen);
        }

        if (!prompted)
        {
            return RowResult.Failed("the second client's fallback did not prompt after the managed-login hour", seen);
        }

        seen.Add($"after the hour, at +{(after.At - signedIn).TotalMinutes:0.#} min: prompted");
        return RowResult.Passed("the fallback was page-less inside the hour and prompted after it", seen);
    }

    /// <summary>O9 (record): the merged CSP's size at the cap, and the ALB's, Kestrel's and every named browser's largest accepted header.</summary>
    public static RowResult HeaderLimits(HeaderLimitRecord record, IReadOnlyList<string> userAgents)
    {
        List<string> seen = [];
        List<string> missing = [];
        if (record.Cap is null)
        {
            missing.Add("the cap");
        }

        if (record.MergedCspBytes is null)
        {
            missing.Add("the merged CSP's size");
        }

        if (record.AlbLargestAcceptedBytes is null)
        {
            missing.Add("the ALB's limit");
        }

        if (record.KestrelLargestAcceptedBytes is null)
        {
            missing.Add("Kestrel's limit");
        }

        if (userAgents.Count == 0)
        {
            missing.Add("a user agent");
        }

        missing.AddRange(userAgents.Where(agent => record.Browsers.FirstOrDefault(each => each.UserAgent == agent)?.LargestAcceptedBytes is null).Select(agent => $"{agent}'s limit"));
        if (missing.Count > 0)
        {
            return RowResult.NotProved("O9's header record is incomplete, missing " + string.Join("; ", missing), seen);
        }

        seen.Add($"merged CSP at the cap N={record.Cap}: {record.MergedCspBytes} bytes; ALB {record.AlbLargestAcceptedBytes}; Kestrel {record.KestrelLargestAcceptedBytes}; "
            + string.Join("; ", userAgents.Select(agent => $"{agent} {record.Browsers.First(each => each.UserAgent == agent).LargestAcceptedBytes}")));
        return RowResult.Passed("O9 recorded", seen);
    }

    /// <summary>F-41 (O12): the entry loads and its chunk resolves under the fragment's <c>assets/</c>, at the default entry and another.</summary>
    public static RowResult EntryImports(EntryImportRecord record)
    {
        List<string> seen = [];
        if (!record.Cases.Any(each => each.Entry == "entry.js") || !record.Cases.Any(each => each.Entry != "entry.js"))
        {
            return RowResult.NotProved("O12 needs a fragment at the default entry.js and one with another entry name", seen);
        }

        foreach (EntryImport import in record.Cases)
        {
            if (import.Loaded is null || import.ChunkPath is null)
            {
                return RowResult.NotProved($"{import.FragmentId}'s import of {import.Entry} was not observed", seen);
            }

            if (!import.Loaded.Value)
            {
                return RowResult.Failed($"import('/fragments/{import.FragmentId}/{import.Entry}') did not load", seen);
            }

            string assets = $"/fragments/{import.FragmentId}/assets/";
            if (!import.ChunkPath.StartsWith(assets, StringComparison.Ordinal) || import.ChunkPath.Contains("/../", StringComparison.Ordinal) || import.ChunkPath.Length == assets.Length)
            {
                return RowResult.Failed($"{import.FragmentId}'s chunk resolved to {import.ChunkPath}, outside {assets}", seen);
            }

            seen.Add($"{import.FragmentId}/{import.Entry}: loaded; chunk {import.ChunkPath}");
        }

        return RowResult.Passed("every entry imported and its chunk resolved under its fragment's assets/", seen);
    }

    /// <summary>F-42 (O13): at N = 2, in every named user agent, the chain completed with N steps, each with its Fetch Metadata recorded.</summary>
    public static RowResult Chain(ChainRecord record, FragmentsContext context)
    {
        List<string> seen = [];
        if (context.N != 2)
        {
            return RowResult.NotProved($"O13 runs at N = 2, and the shell's entry names {context.N?.ToString(CultureInfo.InvariantCulture) ?? "no"} fragments", seen);
        }

        if (context.UserAgents.Count == 0)
        {
            return RowResult.NotProved("the run names no user agent", seen);
        }

        foreach (string agent in context.UserAgents)
        {
            if (record.Runs.FirstOrDefault(each => each.UserAgent == agent) is not { Completed: { } completed } run)
            {
                return RowResult.NotProved($"no chain was observed in {agent}", seen);
            }

            if (run.Steps.Count != context.N)
            {
                return RowResult.NotProved($"{agent} recorded {run.Steps.Count} /bff/chain steps of {context.N}", seen);
            }

            if (run.Steps.FirstOrDefault(step => string.IsNullOrEmpty(step.Site) || string.IsNullOrEmpty(step.Mode) || string.IsNullOrEmpty(step.Dest)) is not null)
            {
                return RowResult.NotProved($"a /bff/chain step in {agent} has no Fetch Metadata recorded", seen);
            }

            if (!completed)
            {
                return RowResult.Failed($"the chain did not complete in {agent}", seen);
            }

            seen.Add($"{agent}: " + string.Join("; ", run.Steps.Select((step, i) => $"step {i + 1} Sec-Fetch-Site {step.Site}, -Mode {step.Mode}, -Dest {step.Dest}")));
        }

        return RowResult.Passed("the chain completed in every user agent with each step's Fetch Metadata recorded", seen);
    }

    /// <summary>N-46 (O13): each of the three cases in every named user agent lands on <c>/</c> with the record's version unchanged.</summary>
    public static RowResult ChainRefusals(ChainRefusalRecord record, IReadOnlyList<string> userAgents)
    {
        List<string> seen = [];
        if (userAgents.Count == 0)
        {
            return RowResult.NotProved("the run names no user agent", seen);
        }

        foreach (string agent in userAgents)
        {
            foreach (string name in RefusalCases)
            {
                if (record.Cases.FirstOrDefault(each => each.UserAgent == agent && each.Case == name) is not { LandedOn: { } landed, VersionBefore: { } before, VersionAfter: { } after } found)
                {
                    return RowResult.NotProved($"the {name} step in {agent} was not observed with the record's version before and after", seen);
                }

                // An "expired" step sent inside ChainLifetime is a replay, not the expiry #1005 O13 names.
                if (name == "expired" && (found.SinceMint is not { } age || age < FragmentsBounds.ChainLifetime))
                {
                    return RowResult.NotProved($"the expired step in {agent} was not sent past ChainLifetime ({FragmentsBounds.ChainLifetime.TotalMinutes} minutes) after its continuation was minted", seen);
                }

                if (after != before)
                {
                    return RowResult.Failed($"the {name} step in {agent} changed the session record's version from {before} to {after}", seen);
                }

                if (landed != "/")
                {
                    return RowResult.Failed($"the {name} step in {agent} landed on {landed}, not /", seen);
                }

                seen.Add($"{agent}: {name} landed on /, version {before} unchanged");
            }
        }

        return RowResult.Passed("each replayed, foreign and expired step landed on / with the record unchanged, in every user agent", seen);
    }

    /// <summary>
    /// F-37 (F-T5): with different bytes exactly one publish completes, the other refused by S3's 412 or
    /// 409, the stored object and the URL the winner's; with the same bytes both complete.
    /// </summary>
    public static RowResult PublishRaces(PublishRaceRecord record)
    {
        List<string> seen = [];
        PublishRace? different = record.Variants.FirstOrDefault(each => each.Variant == "different");
        PublishRace? same = record.Variants.FirstOrDefault(each => each.Variant == "same");
        if (different is not { Runs.Count: 2 } || same is not { Runs.Count: 2 })
        {
            return RowResult.NotProved("F-T5 needs two runs of both the different-bytes and the same-bytes variant", seen);
        }

        if (different.Runs.Any(run => run.Completed is null) || same.Runs.Any(run => run.Completed is null))
        {
            return RowResult.NotProved("a publish run's outcome was not recorded", seen);
        }

        if (different.Runs[0].BytesSha256 == different.Runs[1].BytesSha256)
        {
            return RowResult.NotProved("the different-bytes variant published the same bytes twice", seen);
        }

        PublishRun[] completed = [.. different.Runs.Where(run => run.Completed == true)];
        if (completed.Length == 2)
        {
            return RowResult.Failed("two racing publishes of one key both completed with different bytes", seen);
        }

        if (completed.Length == 0)
        {
            return RowResult.NotProved("neither racing publish completed", seen);
        }

        PublishRun winner = completed[0];
        PublishRun loser = different.Runs.Single(run => run != winner);
        if (loser.RefusedWith is not (412 or 409))
        {
            return RowResult.NotProved($"the losing publish stopped {(loser.RefusedWith is { } status ? $"with {status}" : "with no S3 status")}, not S3's 412 or 409", seen);
        }

        if (different.StoredSha256 is null || different.ServedSha256 is null)
        {
            return RowResult.NotProved("the stored object or the URL's bytes were not read", seen);
        }

        if (different.StoredSha256 != winner.BytesSha256)
        {
            return RowResult.Failed("the key holds bytes other than the winner's", seen);
        }

        if (different.ServedSha256 != winner.BytesSha256)
        {
            return RowResult.Failed("the URL served bytes other than the winner's", seen);
        }

        seen.Add($"different bytes: one completed, the other refused with {loser.RefusedWith}; the key and the URL hold the winner's");
        if (same.Runs.Any(run => run.Completed != true))
        {
            return RowResult.Failed("a same-bytes publish did not complete", seen);
        }

        seen.Add("same bytes: both completed");
        return RowResult.Passed("the asset invariant held on the cell's bucket", seen);
    }

    /// <summary>
    /// F-31 (#961 row 10): both images' provenance, then every pre-fragments task refused the
    /// <c>schemaVersion: 2</c> document by its version with its snapshot staying live, and every
    /// fragments task applied it, read from each task's own lines.
    /// </summary>
    public static RowResult RollingRefusal(RollingRefusalRecord record, GatewayCapture capture)
    {
        List<string> seen = [];
        if (record.PreFragments is not { TrunkCommit: { } commit, Digest: { } pre } || !Commit().IsMatch(commit) || !Digest().IsMatch(pre))
        {
            return RowResult.NotProved("the pre-fragments image's trunk commit or digest is missing", seen);
        }

        if (record.Fragments?.Digest is not { } post || !Digest().IsMatch(post))
        {
            return RowResult.NotProved("the fragments image's digest is missing", seen);
        }

        if (record.Revision is not { } revision)
        {
            return RowResult.NotProved("the schemaVersion 2 document's revision is missing", seen);
        }

        seen.Add($"pre-fragments image {pre} from {commit}; fragments image {post}; document revision {revision}");
        TaskImage[] before = [.. record.Tasks.Where(task => task.Digest == pre)];
        TaskImage[] after = [.. record.Tasks.Where(task => task.Digest == post)];
        if (before.Length == 0 || after.Length == 0)
        {
            return RowResult.NotProved($"the rolling deploy showed {before.Length} pre-fragments and {after.Length} fragments task(s); it needs both", seen);
        }

        string live = LivePrefix(revision);
        foreach (TaskImage task in before)
        {
            GatewayEvent[] lines = [.. capture.Events.Where(line => line.TaskId == task.TaskId)];
            if (lines.Any(line => line.Field("msg")?.StartsWith(live, StringComparison.Ordinal) == true))
            {
                return RowResult.Failed($"the pre-fragments task {task.TaskId} applied the schemaVersion 2 document", seen);
            }

            bool byVersion = lines.Any(line => line.Field("msg") is { } msg && msg.Contains(HasAnError, StringComparison.Ordinal) && msg.Contains(SchemaVersionIssue, StringComparison.Ordinal));
            bool stays = lines.Any(line => line.Field("msg") is { } msg && msg.Contains(IsRejected, StringComparison.Ordinal) && msg.Contains(StaysLive, StringComparison.Ordinal));
            if (!byVersion || !stays)
            {
                return RowResult.NotProved($"the pre-fragments task {task.TaskId} shows no refusal by schemaVersion with its snapshot staying live", seen);
            }

            seen.Add($"{task.TaskId} (pre-fragments): refused by schemaVersion, snapshot stayed");
        }

        foreach (TaskImage task in after)
        {
            if (!capture.Events.Any(line => line.TaskId == task.TaskId && line.Field("msg")?.StartsWith(live, StringComparison.Ordinal) == true))
            {
                return RowResult.Failed($"the fragments task {task.TaskId} did not apply revision {revision}", seen);
            }

            seen.Add($"{task.TaskId} (fragments): applied revision {revision}");
        }

        return RowResult.Passed("every pre-fragments task refused the schemaVersion 2 document by version, and every fragments task applied it", seen);
    }

    /// <summary>
    /// N-38 (O3): the logout and the refresh on two tasks; the refresh obtained the fragment set's
    /// successor while the logout ran; the logout revoked the set; and the successor was refused at the
    /// provider after it. A successor that refreshes after the logout, or a set the logout did not
    /// revoke, is a counterexample, failed naming both tasks.
    /// </summary>
    public static RowResult Successor(IReadOnlyList<Step> steps, SuccessorRecord record, GatewayCapture capture)
    {
        List<string> seen = [];
        Step? logout = steps.FirstOrDefault(step => step.Name == "logout");
        Step? refresh = steps.FirstOrDefault(step => step.Name == "refresh");
        if (logout is null || refresh is null)
        {
            return RowResult.NotProved("N-38 needs a logout step and a refresh step", seen);
        }

        Placement on = capture.Locate(logout);
        Placement other = capture.Locate(refresh);
        seen.Add($"logout: {on}; refresh: {other}");
        if (on.Kind != PlacementKind.OnTask || other.Kind != PlacementKind.OnTask || on.TaskId == other.TaskId)
        {
            return RowResult.NotProved("the logout and the refresh were not each placed on one of two different tasks", seen);
        }

        GatewayEvent[] successors = [.. capture.Refreshed(record.SessionRef).Where(line => line.TaskId == other.TaskId && FragmentOf(capture, line, record.FragmentId))];
        GatewayEvent? revocation = capture.Events.FirstOrDefault(line =>
            line.TaskId == on.TaskId && line.Field("session_ref") == record.SessionRef && line.Field("fragment_id") == record.FragmentId && line.Field("outcome") is not null);
        if (revocation is null)
        {
            return RowResult.NotProved($"no logout revocation line for fragment {record.FragmentId} on {on.TaskId}", seen);
        }

        DateTimeOffset revokedAt = revocation.Ts ?? revocation.Timestamp;
        GatewayEvent? successor = successors.FirstOrDefault(line => (line.Ts ?? line.Timestamp) >= logout.At);
        if (successor is null)
        {
            return RowResult.NotProved($"no successor was obtained on {other.TaskId} while the logout ran", seen);
        }

        if (revocation.Field("outcome") != "revoked")
        {
            return RowResult.Failed($"the logout on {on.TaskId} left fragment {record.FragmentId}'s set with outcome {revocation.Field("outcome")}, so the successor obtained on {other.TaskId} lives on", seen);
        }

        if (capture.Refreshed(record.SessionRef).FirstOrDefault(line => FragmentOf(capture, line, record.FragmentId) && (line.Ts ?? line.Timestamp) > revokedAt && line != successor) is { } late)
        {
            return RowResult.Failed($"the successor refreshed on {late.TaskId} after the logout on {on.TaskId} revoked the set (refresh on {other.TaskId})", seen);
        }

        if (record.ProbeStatus is not { } probe || record.ProbeAt is not { } probedAt || probedAt <= revokedAt)
        {
            return RowResult.NotProved("the successor was not presented to the provider after the logout", seen);
        }

        // RFC 6749 section 5.2: a revoked refresh token is invalid_grant, a 400.
        if (probe is >= 200 and < 300)
        {
            return RowResult.Failed($"the provider accepted the successor obtained on {other.TaskId} after the logout on {on.TaskId}", seen);
        }

        seen.Add($"successor refused at the provider with {probe}");
        return RowResult.Passed("the successor obtained during the logout was refused at the provider", seen);
    }

    /// <summary>
    /// The registry's own wording, from src/Platform.Registry/RegistrySource.cs's error, rejection and
    /// swap lines and RegistryIssue's <c>{path}: {message}</c>; FragmentsObserverTests reads each from
    /// those files and fails when one no longer holds it (#872's shape).
    /// </summary>
    public const string HasAnError = "has an error: ";

    public const string SchemaVersionIssue = "/schemaVersion: ";

    public const string IsRejected = "is rejected with ";

    public const string StaysLive = "stays live.";

    /// <summary>The head of the swap line, <c>Registry revision {Revision} ({ETag}) is live …</c>, for one revision.</summary>
    public static string LivePrefix(long revision) => $"Registry revision {revision.ToString(CultureInfo.InvariantCulture)} (";

    /// <summary>
    /// Whether a Refreshed line is the fragment's set's: its own <c>fragment_id</c> when it carries one, else the
    /// <c>fragment_id</c> of the request line its <c>trace_id</c> belongs to. contracts/telemetry.md (#986) writes
    /// <c>fragment_id</c> on the request line only, so a refresh line with neither is another set's, the
    /// shell's among them, and is never read as the fragment's successor.
    /// </summary>
    private static bool FragmentOf(GatewayCapture capture, GatewayEvent line, string fragmentId) =>
        line.Field("fragment_id") is { } id
            ? id == fragmentId
            : line.Field("trace_id") is { } trace && capture.Events.Any(request => request.IsRequest && request.Field("trace_id") == trace && request.Field("fragment_id") == fragmentId);

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex Commit();

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex Digest();

    /// <summary>A recording as the evidence file holds it.</summary>
    public static JsonElement Recorded<T>(T record) => JsonSerializer.SerializeToElement(record);
}
