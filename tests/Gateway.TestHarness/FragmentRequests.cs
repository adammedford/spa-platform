using System.Net;
using System.Text;
using System.Text.Json;
using Platform.Testing;

namespace Gateway.TestHarness;

/// <summary>
/// The requests both composed suites' fragment rows make the same way (#1007, part of #961): a login to a
/// shell, a call through a shell's or a fragment's echo route and the token that reached the upstream, and
/// one claim read from it. Each suite wraps these with its own cancellation and assertions.
/// </summary>
public static class FragmentRequests
{
    /// <summary>A navigation to the shell's <c>/bff/login</c> with <paramref name="returnUrl"/>, followed to its end.</summary>
    public static Task<BrowserResponse> LoginAsync(TestBrowser browser, HarnessApp shell, string returnUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(shell);
        return browser.NavigateAsync(shell.Url("/bff/login?returnUrl=" + Uri.EscapeDataString(returnUrl)), ct: ct);
    }

    /// <summary>
    /// Calls the echo API through the shell's route (<paramref name="fragmentId"/> null) or a fragment's, and
    /// returns the bearer token the echo upstream received for that one call.
    /// </summary>
    public static async Task<string> BearerAtEchoAsync(
        GatewayHarness harness, TestBrowser browser, HarnessApp shell, string? fragmentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(shell);
        string marker = "/items/" + Guid.NewGuid().ToString("N");
        string route = fragmentId is null ? "/api/echo" : $"/fragments/{fragmentId}/api/echo";
        BrowserResponse response = await browser.GetAsync(shell.Url(route + marker).ToString(), ct: ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"the echo call through {route} answered {response}: {response.Body}");
        }

        EchoedRequest[] received = [.. harness.Echo.Requests.Where(request => request.Path == marker)];
        if (received.Length != 1)
        {
            throw new InvalidOperationException($"the echo upstream saw {received.Length} requests for one call through {route}.");
        }

        string bearer = received[0].Headers["Authorization"].Single();
        return bearer["Bearer ".Length..];
    }

    /// <summary>One string claim of a JWT's payload, read without validating it: the upstream validated it.</summary>
    public static string ClaimOf(string jwt, string claim)
    {
        ArgumentNullException.ThrowIfNull(jwt);
        string payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        return document.RootElement.GetProperty(claim).GetString()!;
    }
}
