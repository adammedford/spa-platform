using System.Net;
using System.Text;
using System.Text.Json;
using Gateway.SecurityTests.Negative;
using Gateway.TestHarness;
using Platform.Abstractions;
using Platform.Testing;

namespace Gateway.SecurityTests.Fragments;

/// <summary>
/// What the composed fragment rows of this suite share: a login to a shell, the step a chain stops at,
/// the session record's version and the token an upstream saw. A copy of the integration suite's
/// helpers (tests/Gateway.IntegrationTests/Fragments/Fragments.cs), since a test project references
/// no other.
/// </summary>
internal static class FragmentLogins
{
    /// <summary>A navigation to the shell's <c>/bff/login</c> with <paramref name="returnUrl"/>, followed to its end.</summary>
    public static Task<BrowserResponse> LoginAsync(TestBrowser browser, HarnessApp shell, string returnUrl = "/") =>
        browser.NavigateAsync(shell.Url("/bff/login?returnUrl=" + Uri.EscapeDataString(returnUrl)), ct: Suite.Ct);

    /// <summary>
    /// Signs <paramref name="browser"/> in to the shell and stops at the first <c>/bff/chain</c> step,
    /// unrequested: <c>/bff/login</c>, the provider's authorize endpoint and the shell's callback,
    /// each followed by hand. The browser then holds the shell's session, no fragment signed in yet,
    /// and the returned URL carries the continuation <c>c</c> the callback minted.
    /// </summary>
    public static async Task<Uri> UpToFirstStepAsync(GatewayHarness harness, TestBrowser browser, HarnessApp shell)
    {
        Uri callback = await SignedIn.UpToCallbackAsync(harness, browser, shell);
        BrowserResponse called = await browser.NavigateAsync(callback, SignedIn.Manual, Suite.Ct);
        Assert.Equal(HttpStatusCode.Redirect, called.StatusCode);
        Uri step = new(shell.Origin, called.Header("Location")!);
        Assert.Equal(shell.Url("/bff/chain"), new Uri(step.GetLeftPart(UriPartial.Path)));
        return step;
    }

    /// <summary>The session record <paramref name="browser"/>'s session cookie names on the shell.</summary>
    public static async Task<SessionRecord> RecordAsync(GatewayHarness harness, TestBrowser browser, HarnessApp shell)
    {
        string key = SignedIn.KeyOf(harness, shell.AppId, SignedIn.SessionCookie(browser, shell));
        return Assert.IsType<SessionRecord>(await SignedIn.Store(harness).GetAsync(key, Suite.Ct));
    }

    /// <summary>
    /// Calls the echo API through the shell's route (<paramref name="fragmentId"/> null) or a
    /// fragment's, and returns the bearer token the echo upstream received.
    /// </summary>
    public static async Task<string> TokenAtEchoAsync(GatewayHarness harness, TestBrowser browser, HarnessApp shell, string? fragmentId)
    {
        string marker = "/items/" + Guid.NewGuid().ToString("N");
        string route = fragmentId is null ? "/api/echo" : $"/fragments/{fragmentId}/api/echo";
        BrowserResponse response = await browser.GetAsync(shell.Url(route + marker).ToString(), ct: Suite.Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"the echo call through {route} answered {response}: {response.Body}");

        EchoedRequest received = Assert.Single(harness.Echo.Requests, request => request.Path == marker);
        string bearer = received.Headers["Authorization"].Single();
        return bearer["Bearer ".Length..];
    }

    /// <summary>One string claim of a JWT's payload, read without validating it: the upstream validated it.</summary>
    public static string ClaimOf(string jwt, string claim)
    {
        string payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        return document.RootElement.GetProperty(claim).GetString()!;
    }

    /// <summary>A string property of a JSON response body, or null when it has none.</summary>
    public static string? Property(BrowserResponse response, string name)
    {
        using JsonDocument document = JsonDocument.Parse(response.Body);
        return document.RootElement.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;
    }
}
