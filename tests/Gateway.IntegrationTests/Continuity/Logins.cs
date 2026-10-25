using System.Net;
using System.Text.Json;
using Gateway.TestHarness;
using Spactl;

namespace Gateway.IntegrationTests.Continuity;

/// <summary>What the continuity rows share: a login stopped at its callback, the shell it lands on, and <c>spactl wait</c> in process.</summary>
internal static class Logins
{
    public static readonly RequestOptions Manual = new() { Redirect = RedirectMode.Manual };

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Starts a login to the app and stops at the callback: <c>/bff/login</c>, then the provider's
    /// authorize endpoint, which redirects to <c>/signin-oidc</c> with the code and state. The browser holds
    /// the login-state cookies; the callback has not been requested.
    /// </summary>
    public static async Task<Uri> UpToCallbackAsync(GatewayHarness harness, TestBrowser browser, HarnessApp app)
    {
        BrowserResponse login = await browser.NavigateAsync(app.Url("/bff/login"), Manual, Ct);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Uri authorize = new(login.Header("Location")!);
        Assert.Equal(harness.Idp.AuthorizeEndpoint, authorize.GetLeftPart(UriPartial.Path));

        BrowserResponse approved = await browser.ClickAsync(authorize.ToString(), Manual, Ct);
        Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
        Uri callback = new(approved.Header("Location")!);
        Assert.Equal(app.Url("/signin-oidc"), new Uri(callback.GetLeftPart(UriPartial.Path)));
        return callback;
    }

    /// <summary>The navigation ended on the app's own shell, with a 200.</summary>
    public static void AssertOnShell(HarnessApp app, BrowserResponse landed)
    {
        Assert.Equal(HttpStatusCode.OK, landed.StatusCode);
        Assert.True(FetchMetadata.IsSameOrigin(app.Origin, landed.Url), $"the navigation ended at {landed}");
        Assert.Equal(FixtureFiles.Spa(app.AppId)["index.html"], landed.Bytes);
    }

    /// <summary>A problem body's <c>code</c>.</summary>
    public static string? Code(BrowserResponse response)
    {
        using JsonDocument body = JsonDocument.Parse(response.Body);
        return body.RootElement.TryGetProperty("code", out JsonElement code) ? code.GetString() : null;
    }

    /// <summary>
    /// <c>spactl wait</c>, run in process through <paramref name="http"/>, which is the harness's stand-in
    /// load balancer: every read goes to whichever host it picks, as through the ALB (change request P2-5).
    /// </summary>
    public static Task<int> WaitAsync(HttpMessageHandler http, StringWriter output, StringWriter error, CancellationToken ct, params string[] arguments) =>
        SpactlApp.RunAsync(
            ["wait", "--url", "https://cell.spa-poc.test:8443/", .. arguments],
            new SpactlEnvironment
            {
                Store = _ => throw new InvalidOperationException("spactl wait reads no bucket."),
                History = _ => throw new InvalidOperationException("spactl wait reads no history."),
                Http = http,
                Out = output,
                Error = error,
                PollInterval = TimeSpan.FromMilliseconds(20),
            },
            ct);
}

/// <summary>Counts the requests that went through it.</summary>
internal sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private int sent;

    public int Sent => Volatile.Read(ref sent);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref sent);
        return base.SendAsync(request, cancellationToken);
    }
}
