using System.Text.Json;
using Gateway.TestHarness;
using Microsoft.Playwright;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// One browser profile on a stack (<see cref="IBrowserStack"/>): a Playwright context (its own cookie jar and
/// storage) and a page in it. What a test does here is what a user, an SPA's script or a hostile page
/// does in a real browser; nothing sets a header the browser would compute itself.
/// </summary>
public sealed class Profile(IBrowserStack stack, IBrowserContext context, IPage page) : IAsyncDisposable
{
    /// <summary>The fixture shell marks itself signed in once <c>/bff/session</c> has answered 200 (fixtures/spa).</summary>
    public const string SignedInState = "signed in";

    public IBrowserContext Context { get; } = context;

    public IPage Page { get; } = page;

    /// <summary>
    /// Signs in to an app as a user does: a navigation to <c>/bff/login</c>, through the provider and the
    /// app's callback, to the shell, which then fetches its session. Returns once the shell says it is
    /// signed in.
    /// </summary>
    public async Task SignInAsync(HarnessApp app)
    {
        await GotoAsync(app.Url("/bff/login").ToString());
        await Redacted.PlaywrightAsync(() => Stack.CompleteLoginAsync(this, app));
        await WaitSignedInAsync(app);
    }

    /// <summary>Waits until the page is the app's shell and the shell says it is signed in.</summary>
    public Task WaitSignedInAsync(HarnessApp app) => Redacted.PlaywrightAsync(async () =>
    {
        await Page.WaitForURLAsync(app.Url("/").ToString());
        await Assertions.Expect(Page.Locator("#session-state")).ToHaveTextAsync(SignedInState);
    });

    /// <summary>
    /// The page's navigation to <paramref name="url"/>, whose failure prints no URL's query
    /// (<see cref="Redacted.PlaywrightAsync{T}"/>): the target may be a callback a row built with a code
    /// and state, and a navigation that redirects through a login passes one.
    /// </summary>
    public Task<IResponse?> GotoAsync(string url) => Redacted.PlaywrightAsync(() => Page.GotoAsync(url));

    /// <summary>
    /// Puts the page on the app's origin without starting a login: <c>/healthz</c> answers on any host,
    /// sets nothing and needs no session (contracts/pipeline.md), where the shell would challenge.
    /// </summary>
    public async Task OnOriginOfAsync(HarnessApp app)
    {
        IResponse? response = await GotoAsync(app.Url("/healthz").ToString());
        Assert.Equal(200, response?.Status);
    }

    /// <summary>
    /// Starts a login to the app (a navigation to <c>/bff/login</c>, unless <paramref name="start"/> says
    /// otherwise) and stops it at the callback (<see cref="IBrowserStack.StopAtCallbackAsync"/>): the
    /// browser keeps the login-state cookies the gateway set, and the code is unredeemed. Returns the
    /// callback URL.
    /// </summary>
    public Task<Uri> StopAtCallbackAsync(HarnessApp app, Func<Task>? start = null) =>
        Redacted.PlaywrightAsync(() => Stack.StopAtCallbackAsync(this, app, start ?? (() => GotoAsync(app.Url("/bff/login").ToString()))));

    /// <summary>
    /// <c>fetch()</c> from the current page, as an SPA's script makes it, and what the script can read
    /// of the answer. A request the browser refuses to complete (a failed preflight, a policy) comes back
    /// as <see cref="FetchResult.Error"/>.
    /// </summary>
    public async Task<FetchResult> FetchAsync(string url, string method = "GET", IReadOnlyDictionary<string, string>? headers = null, string? body = null, string credentials = "same-origin")
    {
        JsonElement result = await Page.EvaluateAsync<JsonElement>(
            """
            async ([url, method, headers, body, credentials]) => {
              try {
                const response = await fetch(url, { method, headers, body, credentials, redirect: "manual" });
                return { status: response.status, type: response.type, contentType: response.headers.get("content-type"),
                         location: response.headers.get("location"), body: await response.text(), error: null };
              } catch (error) {
                return { status: 0, type: "error", contentType: null, location: null, body: "", error: String(error) };
              }
            }
            """,
            new object?[] { url, method, headers ?? new Dictionary<string, string>(), body, credentials });

        return new FetchResult(
            result.GetProperty("status").GetInt32(),
            result.GetProperty("type").GetString()!,
            result.GetProperty("contentType").GetString(),
            result.GetProperty("location").GetString(),
            result.GetProperty("body").GetString()!,
            result.GetProperty("error").GetString());
    }

    /// <summary>
    /// The URL of every navigation request the page makes from now on, oldest first, redirect hops
    /// included: the list fills as the browser goes.
    /// </summary>
    public List<Uri> RecordNavigations()
    {
        List<Uri> navigations = [];
        Page.Request += (_, request) =>
        {
            if (request.IsNavigationRequest)
            {
                navigations.Add(new Uri(request.Url));
            }
        };
        return navigations;
    }

    /// <summary>The cookies the browser holds for an app's origin.</summary>
    public async Task<IReadOnlyList<BrowserContextCookiesResult>> CookiesOfAsync(HarnessApp app) =>
        await Context.CookiesAsync([app.Origin.ToString()]);

    /// <summary>Whether the browser holds a cookie of this name for the app.</summary>
    public async Task<bool> HoldsAsync(HarnessApp app, string name) =>
        (await CookiesOfAsync(app)).Any(cookie => cookie.Name == name);

    /// <summary>Opens a page some other party controls at <paramref name="url"/> (<see cref="IBrowserStack.OpenForeignPageAsync"/>).</summary>
    public Task OpenForeignPageAsync(Uri url, string html = "<!doctype html><title>foreign</title>") =>
        Stack.OpenForeignPageAsync(this, url, html);

    /// <summary>The stack this profile is on.</summary>
    public IBrowserStack Stack { get; } = stack;

    public ValueTask DisposeAsync() => Context.DisposeAsync();
}

/// <summary>What a page's script learned from one <c>fetch()</c>.</summary>
public sealed record FetchResult(int Status, string Type, string? ContentType, string? Location, string Body, string? Error)
{
    /// <summary>The <c>code</c> of a problem body, or null.</summary>
    public string? Code
    {
        get
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(Body);
                return document.RootElement.TryGetProperty("code", out JsonElement code) ? code.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    public override string ToString() => Error is null ? $"{Status} {Type} {ContentType} {Body}" : $"failed: {Error}";
}
