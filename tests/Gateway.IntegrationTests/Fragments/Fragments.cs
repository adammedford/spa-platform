using System.Text;
using System.Text.Json;
using Gateway.TestHarness;
using Platform.Testing;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>What the composed fragment suites share: the login, the session read and the token an upstream saw.</summary>
internal static class Fragments
{
    /// <summary>A navigation to the shell's <c>/bff/login</c> with <paramref name="returnUrl"/>, followed to its end.</summary>
    public static Task<BrowserResponse> LoginAsync(TestBrowser browser, HarnessApp shell, string returnUrl, CancellationToken ct) =>
        browser.NavigateAsync(shell.Url("/bff/login?returnUrl=" + Uri.EscapeDataString(returnUrl)), ct: ct);

    /// <summary><c>GET /bff/session</c> on the shell's host, as the reference client reads it.</summary>
    public static async Task<JsonElement> SessionAsync(TestBrowser browser, HarnessApp shell, CancellationToken ct)
    {
        BrowserResponse response = await browser.GetAsync(shell.Url("/bff/session").ToString(), ct: ct);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"/bff/session answered {response}: {response.Body}");
        }

        using JsonDocument document = JsonDocument.Parse(response.Body);
        return document.RootElement.Clone();
    }

    /// <summary>Each fragment's state in a <c>/bff/session</c> body, by fragment id.</summary>
    public static Dictionary<string, string> States(JsonElement session) =>
        session.TryGetProperty("fragments", out JsonElement fragments) && fragments.ValueKind == JsonValueKind.Object
            ? fragments.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()!
                    : property.Value.GetProperty("state").GetString()!,
                StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Calls the echo API through the shell's route (<paramref name="fragmentId"/> null) or a
    /// fragment's, and returns the <c>client_id</c> of the bearer token the echo upstream received.
    /// </summary>
    public static async Task<string> ClientIdAtEchoAsync(
        GatewayHarness harness, TestBrowser browser, HarnessApp shell, string? fragmentId, CancellationToken ct)
    {
        string marker = "/items/" + Guid.NewGuid().ToString("N");
        string route = fragmentId is null ? "/api/echo" : $"/fragments/{fragmentId}/api/echo";
        BrowserResponse response = await browser.GetAsync(shell.Url(route + marker).ToString(), ct: ct);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"the echo call through {route} answered {response}: {response.Body}");
        }

        EchoedRequest received = Assert.Single(harness.Echo.Requests, request => request.Path == marker);
        string bearer = received.Headers["Authorization"].Single();
        return ClaimOf(bearer["Bearer ".Length..], "client_id");
    }

    /// <summary>One string claim of a JWT's payload, read without validating it: the upstream validated it.</summary>
    public static string ClaimOf(string jwt, string claim)
    {
        string payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        return document.RootElement.GetProperty(claim).GetString()!;
    }
}
