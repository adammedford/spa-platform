using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Platform.Abstractions;

namespace Gateway.E2ETests.Stack;

/// <summary>The hop that refused one padding probe (#1530, "Which hop refused").</summary>
public enum PadHop
{
    /// <summary>The load balancer answered a 502 of its own.</summary>
    Alb,

    /// <summary>The gateway answered its 502 <c>upstream_unavailable</c>: its outbound client refused the upstream's headers.</summary>
    Gateway,

    /// <summary>No answer reached the caller: the driver's handler or the engine's <c>fetch</c> refused it, or the connection failed.</summary>
    Client,

    /// <summary>Any other answer: a refusal that says nothing about a header limit (a 403 from the upstream, a 401, a 400).</summary>
    Other,
}

/// <summary>
/// What one answer to <c>/pad</c> or to the shell showed, as the driver's client or an engine's
/// <c>fetch</c> read it: the status, the <c>Server</c> and <c>Content-Type</c> headers, a problem
/// body's <c>code</c> and nothing else of a body, each padding header's length, and the
/// <c>Content-Security-Policy</c> header's byte length. No cookie and no token is in it.
/// </summary>
public sealed record PadResponse(int? Status, string? Server, string? ContentType, string? ProblemCode, IReadOnlyList<int?> PadLengths, string? Error)
{
    public int? CspBytes { get; init; }

    /// <summary>What <see cref="HeaderLimitDrive.FetchScript"/> answered in an engine.</summary>
    public static PadResponse FromScript(JsonElement answer)
    {
        static string? Text(JsonElement answer, string name) =>
            answer.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        int? status = answer.TryGetProperty("status", out JsonElement code) && code.ValueKind == JsonValueKind.Number ? code.GetInt32() : null;
        int?[] lengths = answer.TryGetProperty("lengths", out JsonElement each) && each.ValueKind == JsonValueKind.Array
            ? [.. each.EnumerateArray().Select(length => length.ValueKind == JsonValueKind.Number ? length.GetInt32() : (int?)null)]
            : [];
        return new PadResponse(status, Text(answer, "server"), Text(answer, "contentType"), Text(answer, "code"), lengths, Text(answer, "error"));
    }
}

/// <summary>One probe's verdict: accepted, or refused and by which hop, with a short printable account of the answer.</summary>
public sealed record PadAnswer(bool Accepted, PadHop? RefusedBy, string Seen)
{
    /// <summary>
    /// The answer to <c>/pad?bytes=N&amp;count=M</c>. Accepted is a 200 carrying all M padding headers of
    /// exactly N bytes (fixtures/MockApi/Routes.cs, #1545). The gateway's refusal is its problem body
    /// with <c>upstream_unavailable</c> (src/Platform.Proxy/UpstreamFailure.cs: YARP's 502 once its
    /// client refuses the upstream's response). The ALB's is told by itself and never as "not the
    /// gateway's": a 502 whose <c>Server</c> is the load balancer's, <c>awselb/2.0</c>, with no problem
    /// body. The ALB answers 502 when "the target response is malformed or contains HTTP headers that are
    /// not valid" (https://docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-troubleshooting.html#http-502-issues)
    /// and names itself <c>awselb/2.0</c> on the responses it generates
    /// (https://docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-access-logs.html,
    /// "-" for target_status_code when the load balancer answered). Any other answer is
    /// <see cref="PadHop.Other"/>, so a figure is never the ALB's on an answer it did not give.
    /// </summary>
    public static PadAnswer Of(PadResponse response, int bytes, int count)
    {
        if (response.Error is { } error)
        {
            return new PadAnswer(false, PadHop.Client, $"no answer ({error})");
        }

        string status = response.Status?.ToString(CultureInfo.InvariantCulture) ?? "no status";
        if (response.Status == 200 && response.PadLengths.Count == count && response.PadLengths.All(length => length == bytes))
        {
            return new PadAnswer(true, null, "200");
        }

        if (response.Status == 502
            && response.ContentType?.StartsWith("application/problem+json", StringComparison.OrdinalIgnoreCase) == true
            && response.ProblemCode == Problems.UpstreamUnavailable)
        {
            return new PadAnswer(false, PadHop.Gateway, $"502 {Problems.UpstreamUnavailable} from the gateway");
        }

        if (response.Status == 502 && response.ProblemCode is null
            && response.Server?.StartsWith("awselb/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new PadAnswer(false, PadHop.Alb, $"502 from the ALB ({response.Server})");
        }

        string padding = response.Status == 200
            ? $" with {response.PadLengths.Count(length => length == bytes)} of {count.ToString(CultureInfo.InvariantCulture)} padding headers at {bytes.ToString(CultureInfo.InvariantCulture)} bytes"
            : "";
        return new PadAnswer(false, PadHop.Other, $"{status} {response.ContentType ?? "with no content type"}{(response.ProblemCode is { } code ? " " + code : "")}{padding}");
    }
}

/// <summary>
/// A bisection's result: the largest accepted N and the smallest refused one with its answer; Largest is
/// null when the smallest probe was refused, and Refusal null when the bound itself was accepted.
/// </summary>
public sealed record Bisection(int? Largest, int? RefusedAt, PadAnswer? Refusal, int Probes);

/// <summary>The search for the largest N a probe accepts (#1530, "found by bisection over N").</summary>
public static class HeaderBisection
{
    /// <summary>
    /// The largest N in [<paramref name="low"/>, <paramref name="high"/>] the probe accepts, to within one:
    /// the low end and the bound are probed first, then the interval between the largest accepted and the
    /// smallest refused N is halved until they are adjacent. It assumes a refusal at N holds above N,
    /// which a header limit does; any refusal narrows the interval, so a refusal that is not a limit's
    /// becomes the boundary's and its hop says so (<see cref="PadHop.Other"/>).
    /// </summary>
    public static async Task<Bisection> LargestAcceptedAsync(Func<int, CancellationToken, Task<PadAnswer>> probe, int low, int high, CancellationToken ct)
    {
        PadAnswer first = await probe(low, ct);
        if (!first.Accepted)
        {
            return new Bisection(null, low, first, 1);
        }

        PadAnswer last = await probe(high, ct);
        if (last.Accepted)
        {
            return new Bisection(null, null, null, 2);
        }

        int accepted = low;
        int refused = high;
        PadAnswer refusal = last;
        int probes = 2;
        while (refused - accepted > 1)
        {
            int middle = accepted + ((refused - accepted) / 2);
            PadAnswer answer = await probe(middle, ct);
            probes++;
            if (answer.Accepted)
            {
                accepted = middle;
            }
            else
            {
                refused = middle;
                refusal = answer;
            }
        }

        return new Bisection(accepted, refused, refusal, probes);
    }
}

/// <summary>What O9's drive reads and sends on the cell: reads and GETs alone, so no step of it writes to the live registry (#1411: no test write).</summary>
public interface IHeaderLimitCell
{
    /// <summary>The cell's <c>Policy:FragmentsPerShell</c>.</summary>
    int Cap { get; }

    /// <summary>The shell's live entry's fragments, read from the registry object.</summary>
    Task<IReadOnlyList<LiveFragment>> LiveFragmentsAsync(CancellationToken ct);

    /// <summary>A sign-in on the shell, which signs in its fragments too, and the session's cookies, or null.</summary>
    Task<(Step Step, SessionCookies? Cookies)> SignInAsync(string name, CancellationToken ct);

    /// <summary>One GET on the shell's host with the session, as the driver's own HTTP client answers it.</summary>
    Task<(Step Step, PadResponse Response)> GetAsync(string name, string pathAndQuery, SessionCookies cookies, int count, CancellationToken ct);
}

/// <summary>
/// G9's O9 drive (#1530; #961 O9): the merged CSP's size at the cap, and the largest padding header the
/// mock API's <c>/pad</c> route (#1545) can send through a fragment's API route to the driver's client
/// and to a <c>fetch</c> in each engine, each found by bisection, with the hop that refused each one
/// past it. Every response the cell answers crosses the ALB, so an engine's figure is what it accepts
/// behind the ALB; Kestrel's is left empty (<see cref="KestrelPath"/>).
/// </summary>
public sealed class HeaderLimitDrive(IHeaderLimitCell cell, IEngines engines, HarnessAppUrl shell)
{
    /// <summary>
    /// The bound of the single-header search: the largest value <c>/pad</c> answers, 1 MiB
    /// (fixtures/MockApi/Routes.cs, <c>PadMaxTotalBytes</c>), past the ALB's 16 K single header and the
    /// gateway client's 64 KiB, so a limit below it is found and one above it is reported as not found.
    /// </summary>
    public const int SingleBound = 1_048_576;

    /// <summary>
    /// The whole-block search's value size: under the ALB's 16 K single-header quota
    /// (https://docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-limits.html,
    /// "HTTP header limits"), so the block's refusal is the block's and not one header's.
    /// </summary>
    public const int BlockValueBytes = 1_000;

    /// <summary>The whole-block search's bound: <c>/pad</c>'s most headers, 1,024 (<c>PadMaxCount</c>), 1,024,000 bytes of values under its 1 MiB.</summary>
    public const int BlockBound = 1_024;

    /// <summary>
    /// Why Kestrel's figure stays empty: the ticket asks for it over a request that does not cross the
    /// ALB, and the cell offers none.
    /// </summary>
    public const string KestrelPath =
        "Kestrel's limit is not measured: the cell offers no request to the gateway task that does not cross the ALB. "
        + "Exec is off on the gateway's service (infra/modules/cell/ecs.tf, #787), and the task security group admits the gateway port "
        + "from the ALB's group alone (infra/modules/cell/network.tf, task_from_alb)";

    /// <summary>
    /// The page's own same-origin <c>fetch</c> of a padding path, answering what <see cref="PadResponse.FromScript"/>
    /// reads. A response an engine refuses is a rejected promise, a <c>TypeError</c>
    /// (https://fetch.spec.whatwg.org/#concept-network-error), answered by its name alone.
    /// </summary>
    public const string FetchScript = """
        async ([path, count]) => {
          try {
            const response = await fetch(path, { credentials: "same-origin" });
            const lengths = [];
            for (let k = 1; k <= count; k++) {
              const value = response.headers.get(`x-pad-${k}`);
              lengths.push(value === null ? null : value.length);
            }
            const type = response.headers.get("content-type");
            let code = null;
            if (type !== null && type.toLowerCase().startsWith("application/problem+json")) {
              try { const body = await response.json(); code = typeof body.code === "string" ? body.code : null; } catch { }
            }
            return { status: response.status, server: response.headers.get("server"), contentType: type, code, lengths, error: null };
          } catch (failed) {
            return { status: null, server: null, contentType: null, code: null, lengths: [], error: failed.name };
          }
        }
        """;

    /// <summary>The path every sign-in returns to (<see cref="FragmentsBrowserRows.ReturnUrl"/>).</summary>
    private const string ReturnUrl = FragmentsBrowserRows.ReturnUrl;

    /// <summary>The cap from the platform configuration the gateway runs with: <c>Policy.FragmentsPerShell</c>, or its placeholder when absent.</summary>
    public static int CapOf(string platformConfig)
    {
        JsonNode? cap = JsonNode.Parse(platformConfig)?["Policy"]?["FragmentsPerShell"];
        return cap is null
            ? new PlatformPolicy([], [], System.Collections.Immutable.ImmutableDictionary<string, IssuerDefinition>.Empty).FragmentsPerShell
            : cap.GetValueKind() == JsonValueKind.String
                ? int.Parse(cap.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture)
                : cap.GetValue<int>();
    }

    public async Task<(HeaderLimitRecord Record, IReadOnlyList<Step> Steps, IReadOnlyList<string> Agents)> RunAsync(CancellationToken ct)
    {
        List<string> lines = [];
        List<Step> steps = [];
        IReadOnlyList<LiveFragment> live = await cell.LiveFragmentsAsync(ct);
        LiveFragment? fragment = live.FirstOrDefault(each => each.Apis.Count > 0);
        if (fragment is null)
        {
            lines.Add("the live entry names no fragment with an API, so no fragment's API route reaches /pad");
        }

        int? csp = null;
        int? alb = null;
        (Step signIn, SessionCookies? cookies) = await cell.SignInAsync("o9-sign-in", ct);
        steps.Add(signIn);
        if (cookies is null)
        {
            lines.Add("the driver's sign-in did not complete, so neither the shell's CSP nor the driver's figures were read");
        }
        else
        {
            (Step shellStep, PadResponse shellAnswer) = await cell.GetAsync("o9-shell", ReturnUrl, cookies, 0, ct);
            steps.Add(shellStep);
            csp = MergedCsp(live.Count, shellAnswer, lines);
            if (fragment is not null)
            {
                alb = await DriverFiguresAsync(fragment, cookies, steps, lines, ct);
            }
        }

        (IReadOnlyList<BrowserHeaderLimit> browsers, IReadOnlyList<string> agents) = await EnginesAsync(fragment, lines, ct);
        lines.Add(KestrelPath);
        return (new HeaderLimitRecord(cell.Cap, csp, alb, null, browsers) { Lines = lines }, steps, agents);
    }

    /// <summary>The CSP's byte length, only while the live entry holds the cap's fragments: O9 measures the merged CSP at the cap, as O1's run at the cap leaves it.</summary>
    private int? MergedCsp(int fragments, PadResponse shellAnswer, List<string> lines)
    {
        if (fragments != cell.Cap)
        {
            lines.Add($"the live entry lists {fragments.ToString(CultureInfo.InvariantCulture)} fragments, not the cap's {cell.Cap.ToString(CultureInfo.InvariantCulture)}: "
                + "the merged CSP is measured at the cap only, as O1's run at the cap leaves the registry, and this drive writes no entry");
            return null;
        }

        if (shellAnswer.CspBytes is null)
        {
            lines.Add($"the shell's {ReturnUrl} answered {shellAnswer.Status?.ToString(CultureInfo.InvariantCulture) ?? "nothing"} with no single Content-Security-Policy header");
        }

        return shellAnswer.CspBytes;
    }

    /// <summary>The driver's own client: one header bisected over N, recorded as the ALB's only when the ALB refused N + 1, then the whole block over M.</summary>
    private async Task<int?> DriverFiguresAsync(LiveFragment fragment, SessionCookies cookies, List<Step> steps, List<string> lines, CancellationToken ct)
    {
        Bisection single = await HeaderBisection.LargestAcceptedAsync(
            (n, token) => ProbeAsync($"o9-single-{n.ToString(CultureInfo.InvariantCulture)}", PadPath(fragment, n, 1), n, 1, cookies, steps, token), 1, SingleBound, ct);
        lines.Add(Describe("the driver's client, one padding header", single, "bytes of value"));
        int? alb = single.Refusal?.RefusedBy == PadHop.Alb ? single.Largest : null;
        if (single.Largest is not null && alb is null)
        {
            lines.Add("the driver's figure is not recorded as the ALB's: the ALB did not refuse the next byte");
        }

        Bisection block = await HeaderBisection.LargestAcceptedAsync(
            (m, token) => ProbeAsync($"o9-block-{m.ToString(CultureInfo.InvariantCulture)}", PadPath(fragment, BlockValueBytes, m), BlockValueBytes, m, cookies, steps, token), 1, BlockBound, ct);
        lines.Add(Describe($"the driver's client, the whole block of {BlockValueBytes.ToString(CultureInfo.InvariantCulture)}-byte padding headers", block, "headers")
            + (block.Largest is { } count ? $", {BlockBytes(count).ToString("N0", CultureInfo.InvariantCulture)} bytes of padding header lines (name, \": \", value and CRLF)" : ""));
        return alb;
    }

    private async Task<PadAnswer> ProbeAsync(string name, string path, int bytes, int count, SessionCookies cookies, List<Step> steps, CancellationToken ct)
    {
        (Step step, PadResponse response) = await cell.GetAsync(name, path, cookies, count, ct);
        steps.Add(step);
        return PadAnswer.Of(response, bytes, count);
    }

    /// <summary>Each of <see cref="FragmentsObserver.Engines"/>: signed in on the shell, then the single-header bisection by the page's own <c>fetch</c>. An engine that does not launch, or a figure not bounded by a header refusal, has no figure, so the observer names it.</summary>
    private async Task<(IReadOnlyList<BrowserHeaderLimit> Browsers, IReadOnlyList<string> Agents)> EnginesAsync(LiveFragment? fragment, List<string> lines, CancellationToken ct)
    {
        (IReadOnlyList<IEngine> launched, IReadOnlyList<string> refused) = await engines.LaunchAsync(FragmentsObserver.Engines, ct);
        lines.AddRange(refused);
        List<BrowserHeaderLimit> browsers = [];
        foreach (string name in FragmentsObserver.Engines)
        {
            if (launched.FirstOrDefault(engine => engine.Name == name) is not { } engine)
            {
                lines.Add($"{name} did not launch, so it has no figure");
                browsers.Add(new BrowserHeaderLimit(name, null));
                continue;
            }

            string agent = FragmentsBrowserRows.AgentOf(engine);
            browsers.Add(new BrowserHeaderLimit(agent, fragment is null ? null : await EngineFigureAsync(engine, agent, fragment, lines, ct)));
        }

        return (browsers, [.. browsers.Select(each => each.UserAgent)]);
    }

    private async Task<int?> EngineFigureAsync(IEngine engine, string agent, LiveFragment fragment, List<string> lines, CancellationToken ct)
    {
        await using IEnginePage page = await engine.OpenAsync(ct);
        Navigation signedIn = await page.NavigateAsync(shell.Url("/bff/login?returnUrl=%2F"), fill: true, ReturnUrl, ct);
        if (signedIn.Landed != ReturnUrl)
        {
            lines.Add($"{agent}: the sign-in did not land on {ReturnUrl}, so it has no figure");
            return null;
        }

        Bisection single = await HeaderBisection.LargestAcceptedAsync(
            async (n, token) => PadAnswer.Of(PadResponse.FromScript(await page.EvaluateAsync(FetchScript, new object[] { PadPath(fragment, n, 1), 1 }, token)), n, 1),
            1, SingleBound, ct);
        lines.Add(Describe($"{agent}'s fetch, one padding header", single, "bytes of value"));

        // A limit is a refusal of a header: the ALB's, the gateway's or the engine's own. Any other boundary is no figure.
        return single.Refusal?.RefusedBy is PadHop.Alb or PadHop.Gateway or PadHop.Client ? single.Largest : null;
    }

    private static string PadPath(LiveFragment fragment, int bytes, int count) =>
        $"/fragments/{fragment.FragmentId}/api/{fragment.Apis[0]}/pad?bytes={bytes.ToString(CultureInfo.InvariantCulture)}&count={count.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The bytes the first <paramref name="count"/> padding header lines take: <c>X-Pad-k: </c>, the value and CRLF each.</summary>
    public static long BlockBytes(int count) =>
        Enumerable.Range(1, count).Sum(k => (long)("X-Pad-".Length + k.ToString(CultureInfo.InvariantCulture).Length + 2 + BlockValueBytes + 2));

    private static string Describe(string what, Bisection found, string unit) =>
        found switch
        {
            { Largest: { } largest, RefusedAt: { } at, Refusal: { } refusal } =>
                $"{what}: largest accepted {largest.ToString(CultureInfo.InvariantCulture)} {unit}, refused at {at.ToString(CultureInfo.InvariantCulture)} by {refusal.Seen}, in {found.Probes.ToString(CultureInfo.InvariantCulture)} probes",
            { Refusal: { } refusal, RefusedAt: { } at } =>
                $"{what}: no figure, the smallest probe ({at.ToString(CultureInfo.InvariantCulture)}) was refused by {refusal.Seen}",
            _ => $"{what}: no figure, the search's bound was accepted",
        };
}

/// <summary>
/// The cell's side of O9's drive: the registry read through <see cref="FragmentsReaders"/>, a sign-in by
/// the driver's sessions, and GETs through the driver's raw client with the session's cookies and the
/// Fetch Metadata a same-origin <c>fetch</c> sends (<see cref="TracedClient.FetchMetadata"/>).
/// </summary>
public sealed class CellHeaderLimits(
    FragmentsReaders readers,
    string shellId,
    HarnessAppUrl shell,
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
    Func<string, CancellationToken, Task<(Step Step, SessionCookies? Cookies)>> signIn,
    int cap) : IHeaderLimitCell
{
    public int Cap => cap;

    public Task<IReadOnlyList<LiveFragment>> LiveFragmentsAsync(CancellationToken ct) => readers.LiveFragmentsAsync(shellId, ct);

    public Task<(Step Step, SessionCookies? Cookies)> SignInAsync(string name, CancellationToken ct) => signIn(name, ct);

    public async Task<(Step Step, PadResponse Response)> GetAsync(string name, string pathAndQuery, SessionCookies cookies, int count, CancellationToken ct)
    {
        ClientTrace trace = ClientTrace.Fresh();
        DateTimeOffset at = DateTimeOffset.UtcNow;
        using HttpRequestMessage request = new(HttpMethod.Get, shell.Url(pathAndQuery));
        request.Headers.TryAddWithoutValidation(ClientTrace.Header, trace.Traceparent);
        foreach ((string header, string value) in TracedClient.FetchMetadata)
        {
            request.Headers.TryAddWithoutValidation(header, value);
        }

        request.Headers.TryAddWithoutValidation("Cookie", cookies.Reveal());
        try
        {
            using HttpResponseMessage response = await send(request, ct);
            string? type = response.Content.Headers.ContentType?.MediaType;
            string? code = null;
            if (type?.Equals("application/problem+json", StringComparison.OrdinalIgnoreCase) == true)
            {
                code = ProblemCode(await response.Content.ReadAsStringAsync(ct));
            }

            int?[] lengths = [.. Enumerable.Range(1, count).Select(k => One(response, $"X-Pad-{k.ToString(CultureInfo.InvariantCulture)}")?.Length)];
            string? server = response.Headers.TryGetValues("Server", out IEnumerable<string>? products) ? string.Join(" ", products) : null;
            PadResponse answer = new((int)response.StatusCode, server, type, code, lengths, null)
            {
                CspBytes = One(response, "Content-Security-Policy") is { } csp ? Encoding.UTF8.GetByteCount(csp) : null,
            };
            return (new Step(name, trace.TraceId, at, (int)response.StatusCode) { Route = "api" }, answer);
        }
        catch (Exception failed) when (failed is HttpRequestException or IOException or SocketException
                                       || (failed is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return (Step.Failed(name, trace, at, failed) with { Route = "api" }, new PadResponse(null, null, null, null, [], failed.GetType().Name));
        }
    }

    /// <summary>A header's one value, or null when it is absent or repeated.</summary>
    private static string? One(HttpResponseMessage response, string name) =>
        (response.Headers.TryGetValues(name, out IEnumerable<string>? values) || response.Content.Headers.TryGetValues(name, out values))
        && values.ToArray() is [string value] ? value : null;

    private static string? ProblemCode(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["code"] is JsonValue code && code.GetValueKind() == JsonValueKind.String ? code.GetValue<string>() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
