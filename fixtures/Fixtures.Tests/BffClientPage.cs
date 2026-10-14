using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Playwright;

namespace Fixtures.Tests;

/// <summary>What a stub answers: a status and, for a JSON answer, the body.</summary>
internal sealed record StubAnswer(int Status, object? Json = null)
{
    public static StubAnswer Ok(object json) => new(200, json);

    /// <summary>An error as contracts/http-contract.md -> "Errors" shapes it: problem+json with a code.</summary>
    public static StubAnswer Problem(int status, string code) => new(status, new { status, code });
}

/// <summary>
/// A page on a made-up origin whose every request is answered by <c>page.RouteAsync</c>, so nothing
/// reaches DNS or a port. The shell loads <c>fixtures/bff-client.js</c> exactly as an SPA would, with a
/// <c>&lt;script src&gt;</c> from its own origin, and the <c>/bff</c> and <c>/api</c> endpoints are stubs
/// in the shapes contracts/http-contract.md gives them. Every request the page makes is recorded, so a
/// test observes the client at its one real boundary: what it puts on the wire and where it navigates.
/// </summary>
internal sealed class BffClientPage : IAsyncDisposable
{
    internal const string Origin = "https://app1.test";

    private readonly IBrowserContext context;

    private readonly ConcurrentQueue<IRequest> requests = new();

    private BffClientPage(IBrowserContext context, IPage page)
    {
        this.context = context;
        Page = page;
    }

    public IPage Page { get; }

    /// <summary>What <c>GET /bff/session</c> answers.</summary>
    public StubAnswer Session { get; set; } = StubAnswer.Problem(401, "unauthenticated");

    /// <summary>What any request under <c>/api/</c> answers.</summary>
    public Func<IRequest, StubAnswer> Api { get; set; } = _ => StubAnswer.Ok(new { ok = true });

    public IReadOnlyList<IRequest> Requests => [.. requests];

    /// <summary>Every request made to one path, whatever its query.</summary>
    public IReadOnlyList<IRequest> RequestsTo(string path) =>
        [.. requests.Where(request => new Uri(request.Url).AbsolutePath == path)];

    public static async Task<BffClientPage> OpenAsync(IBrowser browser, string path = "/orders/42?tab=lines")
    {
        IBrowserContext context = await browser.NewContextAsync();
        IPage page = await context.NewPageAsync();
        BffClientPage harness = new(context, page);
        page.Request += (_, request) => harness.requests.Enqueue(request);
        await page.RouteAsync("**/*", harness.AnswerAsync);
        await page.GotoAsync(Origin + path);
        await page.WaitForFunctionAsync("() => typeof window.BffClient === 'object'", null, new() { Timeout = 5000 });
        return harness;
    }

    /// <summary>Calls one of the client's functions and returns what its promise resolves to, as JSON.</summary>
    public Task<JsonElement> CallAsync(string expression) =>
        Page.EvaluateAsync<JsonElement>($"async () => JSON.parse(JSON.stringify(await ({expression})) ?? 'null')");

    /// <summary>
    /// Starts a call that is expected to navigate, without awaiting it in the page (the navigation
    /// destroys the context the promise lives in), and returns the navigation request it caused.
    /// </summary>
    public async Task<IRequest> NavigationFromAsync(string expression)
    {
        Task<IRequest> navigation = Page.WaitForRequestAsync(request => request.IsNavigationRequest && request.Frame == Page.MainFrame && new Uri(request.Url).AbsolutePath.StartsWith("/bff/", StringComparison.Ordinal));
        await Page.EvaluateAsync($"() => {{ void ({expression}); }}");
        IRequest request = await navigation;
        await Page.WaitForLoadStateAsync(LoadState.Load);
        return request;
    }

    public async ValueTask DisposeAsync() => await context.DisposeAsync();

    private Task AnswerAsync(IRoute route)
    {
        Uri url = new(route.Request.Url);
        return url.AbsolutePath switch
        {
            "/bff-client.js" => route.FulfillAsync(new()
            {
                Status = 200,
                ContentType = "text/javascript",
                Path = FixtureFiles.Path("fixtures", "bff-client.js"),
            }),
            "/bff/session" => Fulfil(route, Session),
            "/bff/login" or "/bff/logout" => route.FulfillAsync(new()
            {
                Status = 200,
                ContentType = "text/html",
                Body = "<!doctype html><title>provider</title>",
            }),
            string path when path.StartsWith("/api/", StringComparison.Ordinal) => Fulfil(route, Api(route.Request)),
            _ => route.FulfillAsync(new()
            {
                Status = 200,
                ContentType = "text/html",
                Body = "<!doctype html><title>shell</title><script src=\"/bff-client.js\"></script><body></body>",
            }),
        };
    }

    private static Task Fulfil(IRoute route, StubAnswer answer) =>
        route.FulfillAsync(new()
        {
            Status = answer.Status,
            ContentType = answer.Status < 400 ? "application/json" : "application/problem+json",
            Body = answer.Json is null ? "" : JsonSerializer.Serialize(answer.Json),
        });
}
