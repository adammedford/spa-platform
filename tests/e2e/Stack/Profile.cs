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
        await Page.GotoAsync(app.Url("/bff/login").ToString());
        await Stack.CompleteLoginAsync(this, app);
        await WaitSignedInAsync(app);
    }

    /// <summary>Waits until the page is the app's shell and the shell says it is signed in.</summary>
    public async Task WaitSignedInAsync(HarnessApp app)
    {
        await Page.WaitForURLAsync(app.Url("/").ToString());
        await Assertions.Expect(Page.Locator("#session-state")).ToHaveTextAsync(SignedInState);
    }

    /// <summary>
    /// Puts the page on the app's origin without starting a login: <c>/healthz</c> answers on any host,
    /// sets nothing and needs no session (contracts/pipeline.md), where the shell would challenge.
    /// </summary>
    public async Task OnOriginOfAsync(HarnessApp app)
    {
        IResponse? response = await Page.GotoAsync(app.Url("/healthz").ToString());
        Assert.Equal(200, response?.Status);
    }

    /// <summary>
    /// Starts a login to the app and stops it at the callback: the provider approves and redirects the
    /// browser to <c>/signin-oidc</c> with a code and state, and the load balancer holds that one
    /// request instead of passing it on. The browser keeps the login-state cookies the gateway set; the
    /// code is unredeemed. Returns the callback URL.
    /// </summary>
    public async Task<Uri> StopAtCallbackAsync(HarnessApp app)
    {
        string callback = app.Url("/signin-oidc").ToString();
        int before = Local.LoadBalancer.Held.Count;
        Local.LoadBalancer.Hold(url => url.ToString().StartsWith(callback, StringComparison.Ordinal));
        try
        {
            await Page.GotoAsync(app.Url("/bff/login").ToString());
        }
        finally
        {
            Local.LoadBalancer.Release();
        }

        return Local.LoadBalancer.Held.Skip(before).SingleOrDefault()
            ?? throw new InvalidOperationException($"the login to {app} never reached its callback; the page is at {Page.Url}.");
    }

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

    /// <summary>
    /// Opens a page some other party controls at <paramref name="url"/> (<see cref="TlsLoadBalancer.ServeForeign"/>):
    /// a hostile sibling under the app domain, or <see cref="LocalStack.ForeignSite"/>. Every request the
    /// page makes leaves the browser as usual.
    /// </summary>
    public async Task OpenForeignPageAsync(Uri url, string html = "<!doctype html><title>foreign</title>")
    {
        Local.LoadBalancer.ServeForeign(url, html);
        await Page.GotoAsync(url.ToString());
    }

    /// <summary>The stack this profile is on.</summary>
    public IBrowserStack Stack { get; } = stack;

    /// <summary>The stack, for what only the local stand-in load balancer can do: hold a request, serve a foreign page.</summary>
    private LocalStack Local => Stack as LocalStack
        ?? throw new NotSupportedException("this step needs the local stack's stand-in load balancer, which the deployed cell has no counterpart of.");

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
