namespace Gateway.E2ETests.Stack;

/// <summary>
/// The driver of the deployed fragments rows (#1005, T14 of #961): it sends the requests, reads the AWS
/// CLI and records, on the deployed cell and nowhere else. Each row's drive returns a
/// <see cref="Driven{T}"/>; a part of a row this driver cannot yet drive is returned as
/// <see cref="Driven{T}.Missing"/>, so the row reports <c>not proved:</c> naming it and never passes on a
/// recording nobody made. The observer (<see cref="FragmentsObserver"/>) judges what it records.
/// </summary>
public sealed class FragmentsDriver : IAsyncDisposable
{
    /// <summary>The shell whose live entry names the row's fragments; the sitting sets N by the entry it activates.</summary>
    public const string ShellVariable = "SPA_G9_SHELL";

    /// <summary>F-31: the pre-fragments image's trunk commit and digest, and the fragments image's digest, as the sitting deployed them in #961's recorded order.</summary>
    public const string PreImageCommitVariable = "SPA_G9_PRE_IMAGE_COMMIT";

    public const string PreImageDigestVariable = "SPA_G9_PRE_IMAGE_DIGEST";

    public const string FragmentsImageDigestVariable = "SPA_G9_FRAGMENTS_IMAGE_DIGEST";

    /// <summary>O4's isolated table (#1005 O4: "an isolated test cell and table").</summary>
    public const string TableVariable = "SPA_G9_O4_TABLE";

    /// <summary>How often F-31 reads the service's tasks while the rolling deploy runs.</summary>
    public static readonly TimeSpan TaskPoll = TimeSpan.FromSeconds(15);

    private readonly DeployedStack stack;
    private readonly TracedClient raw;
    private readonly Func<string, string?> environment;

    private FragmentsDriver(DeployedStack stack, ICommandRunner runner, Func<string, string?> environment)
    {
        this.stack = stack;
        this.environment = environment;
        raw = ContinuityDriver.RawOf(stack);
        Readers = new FragmentsReaders(stack.Cell, runner);
        string directory = environment(FragmentsEvidence.Variable) is { Length: > 0 } named
            ? named
            : throw new InvalidOperationException($"{FragmentsEvidence.Variable} names no directory: a fragments row writes its evidence there, under .scratch/sitting/log/<UTC date>/.");
        Run = new FragmentsRun(new FragmentsEvidence(directory, runner), new ContinuityLog(stack.Cell, runner));
    }

    public FragmentsRun Run { get; }

    public FragmentsReaders Readers { get; }

    public string Shell => environment(ShellVariable) is { Length: > 0 } shell ? shell : "app1";

    /// <summary>Skipped, naming the switch, unless <c>SPA_E2E_STACK</c> is <c>deployed</c>; then the stack and the runner by <see cref="FragmentsStack.Deployed"/>'s path.</summary>
    public static async Task<FragmentsDriver> StartAsync()
    {
        FragmentsStack.SkipUnlessDeployed();
        DeployedPath path = FragmentsStack.Deployed.Value ?? new DeployedPath(DeployedStack.StartAsync, new ProcessCommandRunner());
        Func<string, string?> environment = FragmentsStack.Environment.Value ?? Environment.GetEnvironmentVariable;
        return new FragmentsDriver(await path.Start(), path.Runner, environment);
    }

    /// <summary>The control (#358 Q143): one raw request to the shell's <c>/healthz</c>, which must be found in exactly one gateway stream.</summary>
    public Task<Step> ControlAsync(CancellationToken ct) =>
        raw.SendAsync("control", HttpMethod.Get, stack.App(Shell).Url("/healthz"), null, 200, ct, "health");

    /// <summary>The row's context: N read from the live registry, the user agents it ran in, and the tasks it read.</summary>
    public async Task<FragmentsContext> ContextAsync(IReadOnlyList<string> userAgents, IReadOnlyList<string> tasks, CancellationToken ct) =>
        new(await Readers.FragmentsOfAsync(Shell, ct), userAgents, tasks);

    /// <summary>A part of a row this driver does not drive: the row records its context and reports not proved, naming the part.</summary>
    public async Task<Driven<T>> NotDrivenAsync<T>(T empty, string part, CancellationToken ct) =>
        new(await ContextAsync([], [], ct), [], empty) { Missing = part };

    /// <summary>
    /// F-31's drive: the images' provenance as the sitting names it, the document's revision from the
    /// live registry, and every gateway task seen with its digest while the rolling deploy runs, read
    /// every <see cref="TaskPoll"/> until no task runs the pre-fragments image or the deadline passes.
    /// </summary>
    public async Task<Driven<RollingRefusalRecord>> RollingRefusalAsync(TimeSpan deadline, CancellationToken ct)
    {
        string? pre = environment(PreImageDigestVariable);
        Dictionary<string, TaskImage> seen = new(StringComparer.Ordinal);
        DateTimeOffset until = DateTimeOffset.UtcNow + deadline;
        do
        {
            IReadOnlyList<TaskImage> now = await Readers.TaskImagesAsync(ct);
            foreach (TaskImage task in now)
            {
                seen[task.TaskId] = task;
            }

            if (seen.Values.Any(task => task.Digest == pre) && now.All(task => task.Digest != pre))
            {
                break;
            }

            await Task.Delay(TaskPoll, ct);
        }
        while (DateTimeOffset.UtcNow < until);

        long revision = (await new CellAws(stack.Cell, Readers.Runner).LiveRegistryAsync(ct)).Revision;
        RollingRefusalRecord record = new(
            new ImageProvenance(environment(PreImageCommitVariable), pre),
            new ImageProvenance(null, environment(FragmentsImageDigestVariable)),
            revision,
            [.. seen.Values.OrderBy(task => task.TaskId, StringComparer.Ordinal)]);
        return new Driven<RollingRefusalRecord>(await ContextAsync([], [.. seen.Keys], ct), [], record);
    }

    /// <summary>O4's size half from the isolated table; the capacity half needs the named workload, which this driver does not run.</summary>
    public async Task<Driven<RecordSize>> RecordSizeAsync(CancellationToken ct)
    {
        string table = environment(TableVariable) is { Length: > 0 } named
            ? named
            : throw new InvalidOperationException($"{TableVariable} names no table: O4 runs on an isolated test cell and table.");
        ItemSize size = await Readers.ItemSizeAsync(table, "ticket", ct);
        return new Driven<RecordSize>(await ContextAsync([], [], ct), [], new RecordSize(size, null))
        {
            Missing = "run O4's named session-write workload with its quiet control periods and isolation check, so its capacity sample is unrecorded",
        };
    }

    public ValueTask DisposeAsync() => stack.DisposeAsync();
}

/// <summary>
/// The fragments class's switch (#1005 "The local stack"): every row is skipped through
/// <c>Assert.Skip</c>, naming <c>SPA_E2E_STACK</c>, unless it names the deployed cell, before the stack
/// starts or any command runs.
/// </summary>
public static class FragmentsStack
{
    /// <summary>A test's own environment, in place of the process's (the class's skip test).</summary>
    internal static readonly AsyncLocal<Func<string, string?>?> Environment = new();

    /// <summary>A test's own way to AWS, which the skip test watches; unset everywhere else.</summary>
    internal static readonly AsyncLocal<DeployedPath?> Deployed = new();

    public static string SkipReason =>
        $"{StackSwitch.Variable} is unset or 'local': the fragments rows drive the deployed cell only, with {StackSwitch.Variable}=deployed";

    public static void SkipUnlessDeployed()
    {
        if (StackSwitch.Choose(Environment.Value ?? System.Environment.GetEnvironmentVariable) != StackKind.Deployed)
        {
            Assert.Skip(SkipReason);
        }
    }
}
