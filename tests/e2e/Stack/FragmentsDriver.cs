using Gateway.TestHarness;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// The driver of the deployed fragments rows (#1005, T14 of #961; the drives #1411): it sends the requests,
/// drives the engines, reads the AWS CLI, runs spactl and records, on the deployed cell and nowhere else.
/// Each row's drive returns a <see cref="Driven{T}"/>; a part of a row this driver cannot yet drive is
/// returned as <see cref="Driven{T}.Missing"/>, so the row reports <c>not proved:</c> naming it and never
/// passes on a recording nobody made. Each drive is a class with its own seams, proved without AWS:
/// <see cref="PublishRaceDrive"/>, <see cref="FragmentsWorkloads"/> and <see cref="FragmentsBrowserRows"/>.
/// The observer (<see cref="FragmentsObserver"/>) judges what it records.
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
        // The refusal and apply lines are written while the deploy runs, long after the control, so the read
        // runs to the end of the polling rather than to the control's delivery.
        return new Driven<RollingRefusalRecord>(await ContextAsync([], [.. seen.Keys], ct), [], record) { Until = DateTimeOffset.UtcNow };
    }

    /// <summary>
    /// O4 on the isolated table: the stored item's and the payload's bytes from a scan, then the named
    /// sign-in workload's capacity sample (<see cref="FragmentsWorkloads.CapacityAsync"/>). The read of the
    /// lines runs to the quiet minute after the workload, where isolation is judged.
    /// </summary>
    public async Task<Driven<RecordSize>> RecordSizeAsync(CancellationToken ct)
    {
        string table = environment(TableVariable) is { Length: > 0 } named
            ? named
            : throw new InvalidOperationException($"{TableVariable} names no table: O4 runs on an isolated test cell and table.");
        (CapacitySample sample, IReadOnlyList<Step> steps, DateTimeOffset until) = await Workloads.CapacityAsync(Readers, table, ct);
        ItemSize size = await Readers.ItemSizeAsync(table, "ticket", ct);
        return new Driven<RecordSize>(await ContextAsync([], [], ct), steps, new RecordSize(size, sample)) { Until = until };
    }

    /// <summary>O2 on the shell's live fragments (<see cref="FragmentsWorkloads.ContentionAsync"/>), the service's task count put back after it.</summary>
    public async Task<Driven<RefreshContention>> ContentionAsync(CancellationToken ct)
    {
        (RefreshContention record, IReadOnlyList<Step> steps) = await Workloads.ContentionAsync(await Readers.LiveFragmentsAsync(Shell, ct), ct);
        return new Driven<RefreshContention>(await ContextAsync([], [], ct), steps, record);
    }

    /// <summary>O2's and O4's workloads over the cell's browser and tunnel.</summary>
    public FragmentsWorkloads Workloads => new(stack.Cell, Readers.Runner, new CellSessions(this));

    /// <summary>The cell's sessions: each sign-in a fresh Chromium profile, closed once its cookies are read.</summary>
    private sealed class CellSessions(FragmentsDriver driver) : IFragmentsSessions
    {
        private HarnessApp App => driver.stack.App(driver.Shell);

        public async Task<(Step Step, SessionCookies? Cookies)> SignInAsync(string name, CancellationToken ct)
        {
            await using Profile profile = await driver.stack.OpenCancellableAsync(ct);
            DateTimeOffset at = DateTimeOffset.UtcNow;
            try
            {
                Step step = await ContinuityDriver.BrowserStepAsync(profile, App, name, "/bff/login", "bff", 302, () => profile.SignInAsync(App));
                return (step, await ContinuityDriver.CookiesAsync(profile, App));
            }
            catch (Exception failed) when (failed is Microsoft.Playwright.PlaywrightException or TimeoutException)
            {
                // Redacted.PlaywrightAsync has already taken every URL's query out of the message; the step keeps its type alone.
                return (Step.Failed(name, ClientTrace.Fresh(), at, failed), null);
            }
        }

        public Task<Step> SendAsync(string name, string path, SessionCookies cookies, CancellationToken ct) =>
            driver.raw.SendAsync(name, HttpMethod.Get, App.Url(path), cookies, null, ct, "api");

        public async Task<IReadOnlyList<Step>> OverlapAsync(string name, string fallbackFragment, CancellationToken ct)
        {
            await using Profile profile = await driver.stack.OpenCancellableAsync(ct);
            Microsoft.Playwright.IPage second = await profile.Context.NewPageAsync();
            TaskCompletionSource chained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            profile.Page.Request += (_, request) =>
            {
                if (new Uri(request.Url).AbsolutePath == FragmentsWorkloads.ChainPath)
                {
                    chained.TrySetResult();
                }
            };
            Task<Step> signIn = ContinuityDriver.BrowserStepAsync(profile, App, name + "-sign-in", "/bff/login", "bff", 302, () => profile.SignInAsync(App));
            await chained.Task.WaitAsync(TimeSpan.FromMinutes(2), ct);
            Step fallback = await PageStepAsync(second, name + "-fallback", $"/bff/login?fragment={fallbackFragment}&returnUrl=%2F");
            return [await signIn, fallback];
        }

        /// <summary>A navigation of a second page in the profile, traced as <see cref="ContinuityDriver.BrowserStepAsync"/> traces the first's, ending back on the shell.</summary>
        private async Task<Step> PageStepAsync(Microsoft.Playwright.IPage page, string name, string pathAndQuery)
        {
            ClientTrace trace = ClientTrace.Fresh();
            DateTimeOffset at = DateTimeOffset.UtcNow;
            await using OriginHeader header = await OriginHeader.SendAsync(page, App.Origin, ClientTrace.Header, trace.Traceparent);
            Microsoft.Playwright.IResponse? response = await Redacted.PlaywrightAsync(() => page.GotoAsync(App.Url(pathAndQuery).ToString()));
            await Redacted.PlaywrightAsync(() => page.WaitForURLAsync(App.Url("/").ToString()));
            header.ThrowIfFailed();
            return new Step(name, trace.TraceId, at, response?.Status) { Route = "bff" };
        }
    }

    /// <summary>
    /// F-T5's drive (<see cref="PublishRaceDrive"/>) on the shell's first live fragment: spactl in this
    /// process, over the cell's bucket through an S3 client whose statuses it reads, and the winner's URL
    /// through the tunnel.
    /// </summary>
    public async Task<Driven<PublishRaceRecord>> PublishRaceAsync(CancellationToken ct)
    {
        IReadOnlyList<LiveFragment> fragments = await Readers.LiveFragmentsAsync(Shell, ct);
        LiveFragment fragment = fragments.Count > 0
            ? fragments[0]
            : throw new InvalidOperationException($"F-T5 races one fragment's assets/ key, and {Shell}'s live entry names no fragment.");
        S3Statuses statuses = new();
        (SpactlRun spactl, Platform.Abstractions.IObjectStore store) = PublishRaceSetup.Live(stack.Cell, statuses);
        PublishRaceSetup setup = new(spactl, store, statuses, new HarnessAppUrl(stack.App(Shell).Origin), ReadAsync,
            OutputsPath, PlatformConfig("F-T5 activates the winning release with spactl fragment activate"), stack.RegistryPropagation);
        PublishRaceRecord record = await new PublishRaceDrive(setup).RunAsync(Shell, fragment, Stamp(), ct);
        return new Driven<PublishRaceRecord>(await ContextAsync([], [], ct), [], record);
    }

    /// <summary>A name no earlier attempt used: the UTC second, as spactl's release names allow.</summary>
    public static string Stamp() => DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The outputs document spactl reads the bucket from, as <see cref="DeployedStack"/> finds it.</summary>
    private string OutputsPath => environment(DeployedStack.OutputsVariable) is { Length: > 0 } named
        ? named
        : Path.Combine(Gateway.TestHarness.FixtureFiles.RepositoryRoot, "infra", "envs", "poc", "outputs.json");

    private string PlatformConfig(string why) => environment(DeployedStack.PlatformConfigVariable) is { Length: > 0 } named
        ? named
        : throw new InvalidOperationException($"{why}, which needs {DeployedStack.PlatformConfigVariable}: the platform configuration the gateway runs with.");

    /// <summary>One GET through the tunnel with a fresh <c>traceparent</c>, answering its status and body: an asset, which no gate asks fetch metadata of.</summary>
    private async Task<(int Status, byte[] Body)> ReadAsync(Uri url, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(ClientTrace.Header, ClientTrace.Fresh().Traceparent);
        using HttpResponseMessage response = await stack.SendRawCancellableAsync(request, ct);
        return ((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(ct));
    }

    private PlaywrightEngines? engines;

    /// <summary>The browser rows over Chromium, Firefox and WebKit on the cell, launched on a row's first use.</summary>
    public FragmentsBrowserRows Browsers => new(engines ??= new PlaywrightEngines(stack, stack.App(Shell)), new HarnessAppUrl(stack.App(Shell).Origin));

    /// <summary>O13's chain (F-42) in each engine that launches; an engine that does not is named on standard output at its launch, has no run in the record, and makes the row not proved (<see cref="FragmentsObserver.Chain"/>).</summary>
    public async Task<Driven<ChainRecord>> ChainAsync(CancellationToken ct)
    {
        (ChainRecord record, IReadOnlyList<string> agents, _) = await Browsers.ChainAsync(ct);
        return new Driven<ChainRecord>(await ContextAsync(agents, [], ct), [], record);
    }

    /// <summary>O12 (F-41) on the shell's live fragments, in Chromium.</summary>
    public async Task<Driven<EntryImportRecord>> EntryImportsAsync(CancellationToken ct)
    {
        (EntryImportRecord record, IReadOnlyList<string> agents) = await Browsers.EntryImportsAsync(await Readers.LiveFragmentsAsync(Shell, ct), ct);
        return new Driven<EntryImportRecord>(await ContextAsync(agents, [], ct), [], record);
    }

    /// <summary>O8 (F-40) on the shell's last live fragment, in Chromium, across the managed-login hour.</summary>
    public async Task<Driven<FallbackRecord>> FallbackAsync(CancellationToken ct)
    {
        IReadOnlyList<LiveFragment> fragments = await Readers.LiveFragmentsAsync(Shell, ct);
        if (fragments.Count == 0)
        {
            throw new InvalidOperationException($"O8 opens a fragment's fallback, and {Shell}'s live entry names no fragment.");
        }

        (FallbackRecord record, IReadOnlyList<string> agents) = await Browsers.FallbackAsync(fragments[^1], ct);
        return new Driven<FallbackRecord>(await ContextAsync(agents, [], ct), [], record);
    }

    /// <summary>
    /// O1 (<see cref="FragmentsBrowserRows.RedirectsAsync"/>): the shell's entry written at each N by spactl in this
    /// process over the cell's bucket, from the fixture definition the seed's fragment step writes, and put back
    /// at the N the live registry gave it.
    /// </summary>
    public async Task<Driven<RedirectRecord>> RedirectsAsync(CancellationToken ct)
    {
        string path = Path.Combine(FixtureFiles.RepositoryRoot, "fixtures", "fragments", Shell + ".json");
        System.Text.Json.Nodes.JsonObject definition = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as System.Text.Json.Nodes.JsonObject
            ?? throw new InvalidOperationException($"{path} is not an app definition.");
        (SpactlRun spactl, Platform.Abstractions.IObjectStore store) = PublishRaceSetup.Live(stack.Cell, new S3Statuses());
        IReadOnlyList<LiveFragment> live = await Readers.LiveFragmentsAsync(Shell, ct);
        O1Lever lever = new(spactl, store, definition, live.ToDictionary(fragment => fragment.FragmentId, fragment => fragment.Release, StringComparer.Ordinal), live.Count,
            PlatformConfig("O1 writes the shell's entry at each N with spactl app add"), OutputsPath, stack.RegistryPropagation);
        RedirectRecord record = await Browsers.RedirectsAsync(lever, ct);
        return new Driven<RedirectRecord>(await ContextAsync([.. record.Engines.Select(engine => $"{engine.Engine} {engine.Version}")], [], ct), [], record);
    }

    public async ValueTask DisposeAsync()
    {
        if (engines is not null)
        {
            await engines.DisposeAsync();
        }

        await stack.DisposeAsync();
    }
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
