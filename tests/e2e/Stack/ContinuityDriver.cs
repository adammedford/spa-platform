using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Gateway.TestHarness;
using Microsoft.Playwright;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// The driver of the deployed continuity rows (#358 Q136): it sends the requests and pulls the levers,
/// on the two-task cell and nowhere else. Every request carries a fresh sampled <c>traceparent</c>
/// (Q137): the raw client's per request (<see cref="TracedClient"/>), Chromium's per step through the
/// context's extra HTTP headers. What it records is <see cref="Step"/>s, which the observer
/// (<see cref="ContinuityObserver"/>) places and judges; it holds the session's cookies only in
/// <see cref="SessionCookies"/>, which prints as nothing.
/// </summary>
public sealed class ContinuityDriver : IAsyncDisposable
{
    private readonly DeployedStack stack;
    private readonly TracedClient raw;

    private ContinuityDriver(DeployedStack stack, ICommandRunner runner)
    {
        this.stack = stack;
        Ecs = new ContinuityEcs(stack.Cell, runner);
        raw = new TracedClient((request, _) => stack.SendRawAsync(request));
        string directory = Environment.GetEnvironmentVariable(ContinuityEvidence.Variable) is { Length: > 0 } named
            ? named
            : throw new InvalidOperationException($"{ContinuityEvidence.Variable} names no directory: a continuity row writes its evidence there, under .scratch/sitting/log/<UTC date>/.");
        Run = new ContinuityRun(new ContinuityEvidence(directory, runner), new ContinuityLog(stack.Cell, runner));
    }

    public ContinuityEcs Ecs { get; }

    public ContinuityRun Run { get; }

    public HarnessApp App1 => stack.App("app1");

    /// <summary>The two-task stack (Q139): skipped, naming the switch, unless <c>SPA_E2E_STACK</c> is <c>deployed</c>.</summary>
    public static async Task<ContinuityDriver> StartAsync()
    {
        ContinuityStack.SkipUnlessDeployed();
        return new ContinuityDriver(await DeployedStack.StartAsync(), new ProcessCommandRunner());
    }

    /// <summary>The control (Q143): one raw request to <c>/healthz</c>, which answers on any app host and needs no session.</summary>
    public Task<Step> ControlAsync(CancellationToken ct) =>
        raw.SendAsync("control", HttpMethod.Get, App1.Url("/healthz"), null, 200, ct, "health");

    /// <summary>A raw request with the session's cookies.</summary>
    public Task<Step> SendAsync(string name, HttpMethod method, string path, SessionCookies? cookies, int? expect, CancellationToken ct, HarnessApp? app = null) =>
        raw.SendAsync(name, method, (app ?? App1).Url(path), cookies, expect, ct);

    /// <summary>A fresh browser profile signed in to the app, and its cookies for the raw client.</summary>
    public async Task<(Profile Profile, SessionCookies Cookies)> SignInAsync(HarnessApp app)
    {
        Profile profile = await stack.OpenAsync();
        await profile.SignInAsync(app);
        return (profile, await CookiesAsync(profile, app));
    }

    /// <summary>Row 8's start: a session whose access token has expired (<see cref="DeployedStack.SignInWithExpiredAccessTokenAsync"/>).</summary>
    public async Task<(Profile Profile, SessionCookies Cookies)> SignInWithDueTokenAsync(HarnessApp app)
    {
        Profile profile = await stack.OpenAsync();
        await stack.SignInWithExpiredAccessTokenAsync(profile, app);
        return (profile, await CookiesAsync(profile, app));
    }

    public static async Task<SessionCookies> CookiesAsync(Profile profile, HarnessApp app) =>
        new(string.Join("; ", (await profile.CookiesOfAsync(app)).Select(cookie => $"{cookie.Name}={cookie.Value}")));

    /// <summary>
    /// One browser step: the context's extra headers carry a fresh <c>traceparent</c> while
    /// <paramref name="action"/> runs, and the step's status is the first response the page received
    /// for <paramref name="path"/> on the app's host. The headers go to every request of the step, so
    /// <paramref name="route"/> names the request line that is the step's own.
    /// </summary>
    public static async Task<Step> BrowserStepAsync(Profile profile, HarnessApp app, string name, string path, string route, int? expect, Func<Task> action)
    {
        ClientTrace trace = ClientTrace.Fresh();
        DateTimeOffset at = DateTimeOffset.UtcNow;
        TaskCompletionSource<int> status = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Seen(object? sender, IResponse response)
        {
            Uri url = new(response.Url);
            if (url.Host == app.Origin.Host && url.AbsolutePath == path)
            {
                status.TrySetResult(response.Status);
            }
        }

        await profile.Context.SetExtraHTTPHeadersAsync([new(ClientTrace.Header, trace.Traceparent)]);
        profile.Page.Response += Seen;
        try
        {
            await action();
        }
        finally
        {
            profile.Page.Response -= Seen;
            await profile.Context.SetExtraHTTPHeadersAsync([]);
        }

        return new Step(name, trace.TraceId, at, status.Task.IsCompleted ? status.Task.Result : null) { Expect = expect, Route = route };
    }

    /// <summary>
    /// A login in two steps (rows 5 and 7): the start, a navigation to <c>/bff/login</c> held at the
    /// callback once the provider's form is filled, and the callback, which redirects to the shell.
    /// </summary>
    public async Task<(Profile Profile, Step Start, Step Callback)> LoginAsync(HarnessApp app)
    {
        Profile profile = await stack.OpenAsync();
        Uri? callback = null;
        Step start = await BrowserStepAsync(profile, app, "login-start", "/bff/login", "bff", 302,
            async () => callback = await profile.StopAtCallbackAsync(app));
        Step finish = await BrowserStepAsync(profile, app, "callback", "/signin-oidc", "callback", 302, async () =>
        {
            await profile.GotoAsync(callback!.ToString());
            await profile.WaitSignedInAsync(app);
        });
        return (profile, start, finish);
    }

    /// <summary>Row 5's antiforgery pair: <c>GET /bff/session</c> for the token, then a POST to the orders API carrying it.</summary>
    public static async Task<(Step Session, Step Post)> AntiforgeryAsync(Profile profile, HarnessApp app)
    {
        string? token = null;
        Step session = await BrowserStepAsync(profile, app, "session", "/bff/session", "bff", 200, async () =>
        {
            FetchResult answer = await profile.FetchAsync("/bff/session");
            token = answer.Status == 200 ? JsonDocument.Parse(answer.Body).RootElement.GetProperty("csrfToken").GetString() : null;
        });
        Step post = await BrowserStepAsync(profile, app, "post", "/api/orders/echo", "api", 200, () =>
            profile.FetchAsync("/api/orders/echo", "POST", new Dictionary<string, string> { ["X-CSRF-TOKEN"] = token ?? "", ["Content-Type"] = "application/json" }, "{}"));
        return (session, post);
    }

    /// <summary>Row 9's logout through the shell's own form, stopped at the provider's login.</summary>
    public Task<Step> LogoutAsync(Profile profile, HarnessApp app) =>
        BrowserStepAsync(profile, app, "logout", "/bff/logout", "bff", 302,
            () => stack.StopAtProviderLoginAsync(profile, () => profile.Page.ClickAsync("form[action='/bff/logout'] button")));

    /// <summary>Row 7: #212's F-07 onboarding of app 3 (Q31), and its offboarding.</summary>
    public Task<HarnessApp> OnboardAsync(string appId) => stack.OnboardAsync(appId);

    public Task OffboardAsync(string appId) => stack.OffboardAsync(appId);

    /// <summary>
    /// Row 6 (Q145, Q146, Q149, Q150). The operator runs <c>deploy.sh</c> on the same commit and restarts
    /// <c>tunnel.sh</c> once the new tasks run, while this drives: a new session signed in every
    /// <see cref="ContinuityBounds.Row6SignInEvery"/> from the start until the interval ends, each
    /// calling the orders API once a second for <see cref="ContinuityBounds.Row6SessionCalls"/>; and a
    /// login started when ECS first lists a new task and none is RUNNING, held at the provider's form,
    /// and submitted once every old task's desired status is STOPPED and the tunnel answers again.
    /// </summary>
    public async Task<ContinuityObserver.RollingDeploy> RollingDeployAsync(TimeSpan bound, CancellationToken ct)
    {
        IReadOnlyList<EcsTaskState> old = [.. (await Ecs.ListedAsync(ct)).Where(task => task.LastStatus == "RUNNING")];
        HashSet<string> oldIds = [.. old.Select(task => task.Id)];
        List<Task<IReadOnlyList<Step>>> sessions = [];
        Stopwatch elapsed = Stopwatch.StartNew();
        TimeSpan nextSignIn = TimeSpan.Zero;
        Step? start = null;
        Step? callback = null;
        Profile? straddling = null;
        bool ended = false;
        while (!ended && elapsed.Elapsed < bound)
        {
            if (elapsed.Elapsed >= nextSignIn)
            {
                (_, SessionCookies cookies) = await SignInAsync(App1);
                sessions.Add(CallEverySecondAsync(sessions.Count + 1, cookies, ct));
                nextSignIn += ContinuityBounds.Row6SignInEvery;
            }

            IReadOnlyList<EcsTaskState> listed = await Ecs.ListedAsync(ct);
            EcsTaskState[] fresh = [.. listed.Where(task => !oldIds.Contains(task.Id))];
            if (start is null && fresh.Length > 0 && fresh.All(task => task.LastStatus != "RUNNING"))
            {
                straddling = await stack.OpenAsync();
                start = await BrowserStepAsync(straddling, App1, "login-start", "/bff/login", "bff", 302, () => stack.StopAtProviderLoginAsync(straddling, () => straddling.GotoAsync(App1.Url("/bff/login").ToString())));
            }

            IReadOnlyList<EcsTaskState> before = await Ecs.DescribeAsync([.. oldIds], ct);
            if (straddling is not null && callback is null && before.All(task => task.DesiredStatus == "STOPPED") && await TunnelAnswersAsync(ct))
            {
                Profile held = straddling;
                callback = await BrowserStepAsync(held, App1, "callback", "/signin-oidc", "callback", 302, async () =>
                {
                    await stack.CompleteLoginAsync(held, App1);
                    await held.WaitSignedInAsync(App1);
                });
            }

            ended = callback is not null && before.All(task => task.StoppedAt is not null) && fresh.Length > 0 && fresh.All(task => task.LastStatus == "RUNNING");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        IReadOnlyList<IReadOnlyList<Step>> calls = await Task.WhenAll(sessions);
        IReadOnlyList<EcsTaskState> stopped = await Ecs.DescribeAsync([.. oldIds], ct);
        IReadOnlyList<EcsTaskState> replacements = [.. (await Ecs.ListedAsync(ct)).Where(task => !oldIds.Contains(task.Id))];
        return new ContinuityObserver.RollingDeploy([.. stopped.Select(task => task.Task)], [.. replacements.Select(task => task.Task)], calls, start, callback);
    }

    private async Task<IReadOnlyList<Step>> CallEverySecondAsync(int session, SessionCookies cookies, CancellationToken ct)
    {
        List<Step> calls = [];
        Stopwatch running = Stopwatch.StartNew();
        while (running.Elapsed < ContinuityBounds.Row6SessionCalls)
        {
            calls.Add(await SendAsync($"session-{session}-call-{calls.Count + 1}", HttpMethod.Get, "/api/orders/whoami", cookies, null, ct));
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return calls;
    }

    private async Task<bool> TunnelAnswersAsync(CancellationToken ct) =>
        (await raw.SendAsync("tunnel-probe", HttpMethod.Get, App1.Url("/healthz"), null, null, ct)).Status == 200;

    public ValueTask DisposeAsync() => stack.DisposeAsync();
}

/// <summary>Which stack the continuity rows have (Q139): only the deployed cell drives them.</summary>
public static class ContinuityStack
{
    /// <summary>A test's own environment, in place of the process's (the class's skip test).</summary>
    internal static readonly AsyncLocal<Func<string, string?>?> Environment = new();

    /// <summary>The reason a row is skipped on the local stack, naming the switch.</summary>
    public static string SkipReason =>
        $"{StackSwitch.Variable} is unset or 'local': the continuity rows drive the two-task cell only, with {StackSwitch.Variable}=deployed";

    /// <summary>Skips the calling row test (Assert.Skip) unless the switch names the deployed cell, before anything reaches AWS.</summary>
    public static void SkipUnlessDeployed()
    {
        if (StackSwitch.Choose(Environment.Value ?? System.Environment.GetEnvironmentVariable) != StackKind.Deployed)
        {
            Assert.Skip(SkipReason);
        }
    }
}
