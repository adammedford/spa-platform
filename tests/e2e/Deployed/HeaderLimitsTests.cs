using System.Net;
using System.Text.Json;
using Gateway.E2ETests.Stack;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// O9's drive without AWS (#1530): the bisection against fakes that accept up to a fixed size, each
/// refusal attributed to its hop, and the drive's figures and lines from scripted answers.
/// </summary>
public sealed class HeaderLimitsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PadAnswer Accepted => new(true, null, "200");

    private static PadAnswer RefusedBy(PadHop hop) => new(false, hop, hop.ToString());

    /// <summary>A probe that accepts up to <paramref name="limit"/> and refuses past it, counting its calls.</summary>
    private static Func<int, CancellationToken, Task<PadAnswer>> UpTo(int limit, List<int> asked, PadHop hop = PadHop.Alb) =>
        (n, _) =>
        {
            asked.Add(n);
            return Task.FromResult(n <= limit ? Accepted : RefusedBy(hop));
        };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(16_350)]
    [InlineData(16_351)]
    [InlineData(32_767)]
    [InlineData(1_048_575)]
    public async Task The_bisection_finds_the_largest_accepted_n_to_within_one_byte(int limit)
    {
        List<int> asked = [];

        Bisection found = await HeaderBisection.LargestAcceptedAsync(UpTo(limit, asked), 1, 1_048_576, Ct);

        Assert.Equal(limit, found.Largest);
        Assert.Equal(limit + 1, found.RefusedAt);
        Assert.Equal(PadHop.Alb, found.Refusal?.RefusedBy);
        // 1 MiB in 2^20 steps: the low and high probes and at most 20 halvings.
        Assert.InRange(asked.Count, 2, 22);
    }

    [Fact]
    public async Task A_refused_smallest_probe_or_an_accepted_bound_finds_no_figure()
    {
        List<int> asked = [];

        Bisection none = await HeaderBisection.LargestAcceptedAsync(UpTo(0, asked, PadHop.Other), 1, 1_048_576, Ct);
        Bisection unbounded = await HeaderBisection.LargestAcceptedAsync(UpTo(1_048_576, asked), 1, 1_048_576, Ct);

        Assert.Null(none.Largest);
        Assert.Equal(1, none.RefusedAt);
        Assert.Equal(PadHop.Other, none.Refusal?.RefusedBy);
        Assert.Null(unbounded.Largest);
        Assert.Null(unbounded.Refusal);
    }

    // ---- the hop that refused ----

    private static PadResponse Padded(int bytes, int count) => new(200, "Kestrel", "application/json", null, [.. Enumerable.Repeat<int?>(bytes, count)], null);

    private static readonly PadResponse GatewayRefusal = new(502, "Kestrel", "application/problem+json", "upstream_unavailable", [], null);

    private static readonly PadResponse AlbRefusal = new(502, "awselb/2.0", "text/html", null, [], null);

    [Fact]
    public void Each_answer_is_attributed_to_its_hop_and_the_alb_only_by_its_own_502()
    {
        // Mutations: the gateway's check removed (its 502 falls to the ALB's test, which wants no problem code, so
        // it is Other), the ALB's Server test removed, and the length check dropped, each fail a case here.
        Assert.True(PadAnswer.Of(Padded(16_000, 1), 16_000, 1).Accepted);
        Assert.Equal(PadHop.Gateway, PadAnswer.Of(GatewayRefusal, 16_000, 1).RefusedBy);
        Assert.Equal(PadHop.Alb, PadAnswer.Of(AlbRefusal, 16_000, 1).RefusedBy);
        Assert.Equal(PadHop.Client, PadAnswer.Of(new PadResponse(null, null, null, null, [], "TypeError"), 16_000, 1).RefusedBy);

        // A 502 with no load balancer's Server, a 403 from the upstream, and a 200 with a short header are none of them.
        Assert.Equal(PadHop.Other, PadAnswer.Of(AlbRefusal with { Server = "Kestrel" }, 16_000, 1).RefusedBy);
        Assert.Equal(PadHop.Other, PadAnswer.Of(new PadResponse(403, "Kestrel", "application/json", null, [], null), 1, 1).RefusedBy);
        Assert.Equal(PadHop.Other, PadAnswer.Of(Padded(15_999, 1), 16_000, 1).RefusedBy);
        Assert.Equal(PadHop.Other, PadAnswer.Of(Padded(1_000, 2), 1_000, 3).RefusedBy);
    }

    /// <summary>A cell whose /pad answers up to a single-header limit and a block limit, each refused by the hop given.</summary>
    private sealed class FakeCell(int single, PadHop singleHop, int block = 30, int cap = 2, int fragments = 2) : IHeaderLimitCell
    {
        public int Cap => cap;

        public List<string> Asked { get; } = [];

        public bool SignedIn { get; init; } = true;

        public PadResponse? Smallest { get; init; }

        public Task<IReadOnlyList<LiveFragment>> LiveFragmentsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LiveFragment>>([.. Enumerable.Range(1, fragments).Select(i => new LiveFragment($"f{i}", "r1", "entry.js", ["orders"]))]);

        public Task<(Step Step, SessionCookies? Cookies)> SignInAsync(string name, CancellationToken ct) =>
            Task.FromResult<(Step, SessionCookies?)>((new Step(name, "t", DateTimeOffset.UnixEpoch, 302), SignedIn ? new SessionCookies("a=b") : null));

        public Task<(Step Step, PadResponse Response)> GetAsync(string name, string pathAndQuery, SessionCookies cookies, int count, CancellationToken ct)
        {
            Asked.Add(pathAndQuery);
            Step step = new(name, "t", DateTimeOffset.UnixEpoch, 200);
            return Task.FromResult((step, pathAndQuery == "/" ? new PadResponse(200, "Kestrel", "text/html", null, [], null) { CspBytes = 3_100 } : Answer(pathAndQuery)));
        }

        public PadResponse Answer(string pathAndQuery)
        {
            Assert.StartsWith("/fragments/f1/api/orders/pad?", pathAndQuery, StringComparison.Ordinal);
            System.Collections.Specialized.NameValueCollection query = System.Web.HttpUtility.ParseQueryString(pathAndQuery[(pathAndQuery.IndexOf('?', StringComparison.Ordinal) + 1)..]);
            int bytes = int.Parse(query["bytes"]!, System.Globalization.CultureInfo.InvariantCulture);
            int count = int.Parse(query["count"]!, System.Globalization.CultureInfo.InvariantCulture);
            if (Smallest is { } smallest)
            {
                return smallest;
            }

            if (count == 1)
            {
                return bytes <= single ? Padded(bytes, 1) : Refusal(singleHop);
            }

            return count <= block ? Padded(bytes, count) : AlbRefusal;
        }

        private static PadResponse Refusal(PadHop hop) => hop switch
        {
            PadHop.Alb => AlbRefusal,
            PadHop.Gateway => GatewayRefusal,
            _ => new PadResponse(403, "Kestrel", "application/json", null, [], null),
        };
    }

    /// <summary>Engines whose fetch answers as the fake cell does, or none launched.</summary>
    private sealed class FakeEngines(FakeCell cell, params string[] launched) : IEngines
    {
        public Task<(IReadOnlyList<IEngine> Launched, IReadOnlyList<string> Refused)> LaunchAsync(IReadOnlyList<string> names, CancellationToken ct) =>
            Task.FromResult<(IReadOnlyList<IEngine>, IReadOnlyList<string>)>((
                [.. launched.Where(names.Contains).Select(name => (IEngine)new Engine(name, cell))],
                [.. names.Where(name => !launched.Contains(name)).Select(name => $"{name}: did not launch in the test")]));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Engine(string name, FakeCell cell) : IEngine, IEnginePage
        {
            public string Name => name;

            public string Version => "1.0";

            public Task<IEnginePage> OpenAsync(CancellationToken ct) => Task.FromResult<IEnginePage>(this);

            public Task<Navigation> NavigateAsync(Uri url, bool fill, string landed, CancellationToken ct) =>
                Task.FromResult(new Navigation([], landed, true));

            public Task<JsonElement> EvaluateAsync(string script, object? argument, CancellationToken ct)
            {
                Assert.Equal(HeaderLimitDrive.FetchScript, script);
                object[] arguments = Assert.IsType<object[]>(argument);
                PadResponse answer = cell.Answer((string)arguments[0]);
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    status = answer.Status,
                    server = answer.Server,
                    contentType = answer.ContentType,
                    code = answer.ProblemCode,
                    lengths = answer.PadLengths,
                    error = answer.Error,
                }));
            }

            public Task ForgetGatewayAsync(CancellationToken ct) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static readonly HarnessAppUrl Shell = new(new Uri("https://app1.example.test"));

    private static async Task<(HeaderLimitRecord Record, IReadOnlyList<Step> Steps, IReadOnlyList<string> Agents)> DriveAsync(FakeCell cell, params string[] launched) =>
        await new HeaderLimitDrive(cell, new FakeEngines(cell, launched), Shell).RunAsync(Ct);

    [Fact]
    public async Task The_alb_s_refusal_past_a_size_records_that_size_as_the_alb_s_limit_and_the_row_passes()
    {
        (HeaderLimitRecord record, IReadOnlyList<Step> steps, IReadOnlyList<string> agents) = await DriveAsync(new FakeCell(16_350, PadHop.Alb), [.. FragmentsObserver.Engines]);

        Assert.Equal(16_350, record.AlbLargestAcceptedBytes);
        Assert.Equal(2, record.Cap);
        Assert.Equal(3_100, record.MergedCspBytes);
        Assert.Null(record.KestrelLargestAcceptedBytes);
        Assert.All(record.Browsers, browser => Assert.Equal(16_350, browser.LargestAcceptedBytes));
        Assert.Equal(["chromium 1.0", "firefox 1.0", "webkit 1.0"], agents);
        Assert.Contains(record.Lines, line => line.StartsWith("the driver's client, the whole block of 1000-byte padding headers: largest accepted 30 headers", StringComparison.Ordinal));
        Assert.Contains(HeaderLimitDrive.KestrelPath, record.Lines);
        Assert.All(steps.Skip(2), step => Assert.StartsWith("o9-", step.Name, StringComparison.Ordinal));

        // The figures but Kestrel's are complete, so the row reports Kestrel's alone missing, with the path in its seen lines.
        RowResult result = FragmentsObserver.HeaderLimits(record, agents);
        Assert.Equal(Verdict.NotProved, result.Verdict);
        Assert.Equal("O9's header record is incomplete, missing Kestrel's limit", result.Reason);
        Assert.Contains(HeaderLimitDrive.KestrelPath, result.Observations);
        Assert.Equal(Verdict.Passed, FragmentsObserver.HeaderLimits(record with { KestrelLargestAcceptedBytes = 65_536 }, agents).Verdict);
    }

    [Fact]
    public async Task A_gateway_refusal_is_never_recorded_as_the_alb_s_limit()
    {
        // Mutation: recording single.Largest as the ALB's whatever hop refused (no RefusedBy == Alb test) fails this test.
        (HeaderLimitRecord record, _, IReadOnlyList<string> agents) = await DriveAsync(new FakeCell(16_350, PadHop.Gateway), [.. FragmentsObserver.Engines]);

        Assert.Null(record.AlbLargestAcceptedBytes);
        Assert.Contains(record.Lines, line => line.Contains("refused at 16351 by 502 upstream_unavailable from the gateway", StringComparison.Ordinal));
        Assert.Contains("the driver's figure is not recorded as the ALB's: the ALB did not refuse the next byte", record.Lines);

        // The engines' figure is what they accept behind the ALB, whichever header refusal bounds it.
        Assert.All(record.Browsers, browser => Assert.Equal(16_350, browser.LargestAcceptedBytes));
        Assert.Contains("missing the ALB's limit", FragmentsObserver.HeaderLimits(record, agents).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_smallest_probe_gives_no_figure_anywhere_and_names_the_answer()
    {
        // A mock API that does not admit the driver's client on /pad answers 403 to the smallest probe.
        PadResponse notAdmitted = new(403, "Kestrel", "application/json", null, [], null);
        (HeaderLimitRecord record, _, IReadOnlyList<string> agents) = await DriveAsync(new FakeCell(16_350, PadHop.Alb) { Smallest = notAdmitted }, "chromium");

        Assert.Null(record.AlbLargestAcceptedBytes);
        Assert.All(record.Browsers, browser => Assert.Null(browser.LargestAcceptedBytes));
        Assert.Contains("the driver's client, one padding header: no figure, the smallest probe (1) was refused by 403 application/json", record.Lines);
        string message = FragmentsObserver.HeaderLimits(record, agents).Message;
        Assert.Contains("missing the ALB's limit", message, StringComparison.Ordinal);
        Assert.Contains("chromium 1.0's limit", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_engine_that_does_not_launch_is_named_missing_and_never_passes_on_the_others()
    {
        // Mutation: leaving an unlaunched engine out of Browsers and the agents passes the row on chromium alone.
        (HeaderLimitRecord record, _, IReadOnlyList<string> agents) = await DriveAsync(new FakeCell(16_350, PadHop.Alb), "chromium");

        Assert.Equal(["chromium 1.0", "firefox", "webkit"], agents);
        RowResult result = FragmentsObserver.HeaderLimits(record with { KestrelLargestAcceptedBytes = 65_536 }, agents);
        Assert.Equal(Verdict.NotProved, result.Verdict);
        Assert.Contains("firefox's limit; webkit's limit", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_boundary_that_no_header_refusal_explains_gives_no_figure()
    {
        // Past 16,350 the upstream answers 403, which says nothing of a header limit: no hop's figure is recorded.
        // Mutation: taking an engine's largest accepted N whatever refused the next one records 16,350 and fails this test.
        (HeaderLimitRecord record, _, _) = await DriveAsync(new FakeCell(16_350, PadHop.Other), [.. FragmentsObserver.Engines]);

        Assert.Null(record.AlbLargestAcceptedBytes);
        Assert.All(record.Browsers, browser => Assert.Null(browser.LargestAcceptedBytes));
        Assert.Contains(record.Lines, line => line.StartsWith("chromium 1.0's fetch, one padding header: largest accepted 16350 bytes of value, refused at 16351 by 403", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_merged_csp_is_recorded_only_while_the_live_entry_holds_the_cap_s_fragments()
    {
        // Mutation: dropping the fragments != cap test in MergedCsp records 3,100 at N = 1 and fails this test.
        (HeaderLimitRecord record, _, _) = await DriveAsync(new FakeCell(16_350, PadHop.Alb, cap: 2, fragments: 1), [.. FragmentsObserver.Engines]);

        Assert.Null(record.MergedCspBytes);
        Assert.Contains(record.Lines, line => line.StartsWith("the live entry lists 1 fragments, not the cap's 2", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "Policy": { "AllowedHostSuffixes": ["x"], "AllowedUpstreams": ["https://x"] } }""", 5)]
    [InlineData("""{ "Policy": { "FragmentsPerShell": 6 } }""", 6)]
    [InlineData("""{ "Policy": { "FragmentsPerShell": "4" } }""", 4)]
    public void The_cap_is_the_platform_configuration_s_or_its_placeholder(string config, int cap) =>
        Assert.Equal(cap, HeaderLimitDrive.CapOf(config));

    [Fact]
    public async Task No_drive_step_writes_to_the_live_registry()
    {
        // Through the cell's own adapter: the AWS CLI is asked only to stream the registry object to standard output,
        // and every request is a GET. Mutation: an s3 cp whose destination is the registry key fails the first assertion.
        string registry = """{ "revision": 3, "apps": [ { "appId": "app1", "fragments": [ { "fragmentId": "f1", "release": "r1", "apis": [ { "name": "orders" } ] } ] } ] }""";
        RecordedRunner aws = new(arguments => registry);
        List<HttpMethod> methods = [];
        FakeCell answers = new(16_350, PadHop.Alb, cap: 1, fragments: 1);
        Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            methods.Add(request.Method);
            PadResponse answer = request.RequestUri!.PathAndQuery == "/" ? new PadResponse(200, "Kestrel", "text/html", null, [], null) : answers.Answer(request.RequestUri.PathAndQuery);
            HttpResponseMessage response = new((HttpStatusCode)answer.Status!.Value) { Content = new StringContent(answer.ProblemCode is { } code ? $$"""{ "code": "{{code}}" }""" : "") };
            response.Content.Headers.ContentType = answer.ContentType is { } type ? new System.Net.Http.Headers.MediaTypeHeaderValue(type) : null;
            response.Headers.TryAddWithoutValidation("Server", answer.Server);
            for (int k = 0; k < answer.PadLengths.Count; k++)
            {
                response.Headers.TryAddWithoutValidation($"X-Pad-{k + 1}", new string('x', answer.PadLengths[k]!.Value));
            }

            if (request.RequestUri.PathAndQuery == "/")
            {
                response.Headers.TryAddWithoutValidation("Content-Security-Policy", "default-src 'self'");
            }

            return Task.FromResult(response);
        }

        CellHeaderLimits cell = new(new FragmentsReaders(FragmentsObserverTests.Cell, aws), "app1", Shell, Send,
            (name, _) => Task.FromResult<(Step, SessionCookies?)>((new Step(name, "t", DateTimeOffset.UnixEpoch, 302), new SessionCookies("a=b"))), cap: 1);

        (HeaderLimitRecord record, _, _) = await new HeaderLimitDrive(cell, new FakeEngines(answers), Shell).RunAsync(Ct);

        Assert.All(aws.Calls, call => Assert.Equal(["s3", "cp", $"s3://{FragmentsObserverTests.Cell.Bucket}/{CellAws.RegistryKey}", "-"], call.Arguments[..4]));
        Assert.NotEmpty(aws.Calls);
        Assert.All(methods, method => Assert.Equal(HttpMethod.Get, method));
        Assert.Equal(16_350, record.AlbLargestAcceptedBytes);
        Assert.Equal("default-src 'self'".Length, record.MergedCspBytes);
    }

    [Fact]
    public async Task The_session_cookie_reaches_each_probe_and_nothing_the_drive_records()
    {
        // PadResponse's promise, "no cookie and no token is in it", proved as N-16 proves a log line: a canary cookie is
        // planted, every probe carries it, and the record, its steps and the row's message and seen lines lack it.
        // Mutation: appending cookies.Reveal() to the driver's single-header seen line fails the last assertion.
        const string Canary = "__Host-spa=canary-7f3a91c2e4";
        List<string?> sent = [];
        FakeCell answers = new(16_350, PadHop.Alb, cap: 1, fragments: 1);
        Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            sent.Add(request.Headers.TryGetValues("Cookie", out IEnumerable<string>? values) ? string.Join("; ", values) : null);
            PadResponse answer = request.RequestUri!.PathAndQuery == "/" ? new PadResponse(200, "Kestrel", "text/html", null, [], null) : answers.Answer(request.RequestUri.PathAndQuery);
            HttpResponseMessage response = new((HttpStatusCode)answer.Status!.Value) { Content = new StringContent(answer.ProblemCode is { } code ? $$"""{ "code": "{{code}}" }""" : "") };
            response.Content.Headers.ContentType = answer.ContentType is { } type ? new System.Net.Http.Headers.MediaTypeHeaderValue(type) : null;
            response.Headers.TryAddWithoutValidation("Server", answer.Server);
            for (int k = 0; k < answer.PadLengths.Count; k++)
            {
                response.Headers.TryAddWithoutValidation($"X-Pad-{k + 1}", new string('x', answer.PadLengths[k]!.Value));
            }

            return Task.FromResult(response);
        }

        string registry = """{ "revision": 3, "apps": [ { "appId": "app1", "fragments": [ { "fragmentId": "f1", "release": "r1", "apis": [ { "name": "orders" } ] } ] } ] }""";
        CellHeaderLimits cell = new(new FragmentsReaders(FragmentsObserverTests.Cell, new RecordedRunner(_ => registry)), "app1", Shell, Send,
            (name, _) => Task.FromResult<(Step, SessionCookies?)>((new Step(name, "t", DateTimeOffset.UnixEpoch, 302), new SessionCookies(Canary))), cap: 1);

        (HeaderLimitRecord record, IReadOnlyList<Step> steps, IReadOnlyList<string> agents) = await new HeaderLimitDrive(cell, new FakeEngines(answers), Shell).RunAsync(Ct);

        Assert.NotEmpty(sent);
        Assert.All(sent, cookie => Assert.Equal(Canary, cookie));
        RowResult row = FragmentsObserver.HeaderLimits(record, agents);
        string recorded = JsonSerializer.Serialize(new { record, steps, row.Message, row.Observations });
        Assert.DoesNotContain("canary-7f3a91c2e4", recorded, StringComparison.Ordinal);
    }
}
