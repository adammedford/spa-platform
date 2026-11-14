using System.Text.Json;
using Microsoft.Playwright;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// One request header the page sends to one origin and to no other, until disposed (#1193).
///
/// <para>Why not the context's extra HTTP headers. Those go on every request of the context, the
/// provider's pages' own among them, and a header CORS does not safelist turns a script's request to
/// another origin into one that needs a preflight naming it
/// (https://fetch.spec.whatwg.org/#cors-safelisted-request-header). A provider's endpoint allows no
/// <c>traceparent</c>, so the browser refused the request its login form's script sent and the login
/// stayed at the form, which is how rows 5 and 7 stopped on the cell (#348).</para>
///
/// <para>The page's own DevTools Protocol session pauses the requests whose URL starts with the origin
/// and continues each with the header added, as <see cref="NavigationHold"/> pauses a callback. A
/// continued request's overrides do not reach a redirect's next hop
/// (https://chromedevtools.github.io/devtools-protocol/tot/Fetch/#method-continueRequest), and a hop
/// to another origin matches no pattern, so the header stays behind when a login leaves for the
/// provider. The network stack still adds its own headers after the pause, Fetch Metadata and cookies
/// among them: <c>ContinuityStepTests</c> holds it to that.</para>
/// </summary>
public sealed class OriginHeader : IAsyncDisposable
{
    private readonly ICDPSession session;
    private readonly TaskCompletionSource<Exception> failed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private OriginHeader(ICDPSession session) => this.session = session;

    /// <summary>Starts sending <paramref name="name"/> on the page's requests to <paramref name="origin"/>.</summary>
    public static async Task<OriginHeader> SendAsync(IPage page, Uri origin, string name, string value)
    {
        OriginHeader header = new(await page.Context.NewCDPSessionAsync(page));
        header.session.Event("Fetch.requestPaused").OnEvent += async (_, paused) =>
        {
            if (paused is not { } request)
            {
                return;
            }

            try
            {
                List<Dictionary<string, object>> headers = [.. request.GetProperty("request").GetProperty("headers").EnumerateObject()
                    .Where(sent => !sent.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .Select(sent => new Dictionary<string, object> { ["name"] = sent.Name, ["value"] = sent.Value.GetString()! })];
                headers.Add(new Dictionary<string, object> { ["name"] = name, ["value"] = value });
                await header.session.SendAsync("Fetch.continueRequest", new Dictionary<string, object>
                {
                    ["requestId"] = request.GetProperty("requestId").GetString()!,
                    ["headers"] = headers,
                });
            }
            catch (Exception met)
            {
                // Nothing thrown on the session's event reaches anyone (#1029): kept for the step to report.
                header.failed.TrySetResult(met);
            }
        };

        // The pattern's only wildcard is the trailing one, and the origin ends in its slash, so a host
        // that merely starts with the app's does not match.
        await header.session.SendAsync("Fetch.enable", new Dictionary<string, object>
        {
            ["patterns"] = new[] { new Dictionary<string, object> { ["urlPattern"] = NavigationHold.Escape(origin.GetLeftPart(UriPartial.Authority) + "/") + "*", ["requestStage"] = "Request" } },
        });
        return header;
    }

    /// <summary>Fails the step when the handler could not continue a request, naming the failure's kind and no URL.</summary>
    public void ThrowIfFailed()
    {
        if (failed.Task.IsCompleted)
        {
            throw new InvalidOperationException($"a request to the app's origin could not be continued with the step's header: {failed.Task.Result.GetType().Name}.");
        }
    }

    /// <summary>Stops sending the header. It throws nothing of its own, so what the step's action met is what the step reports.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await session.SendAsync("Fetch.disable");
            await session.DetachAsync();
        }
        catch (PlaywrightException)
        {
            // The page closed under the step: there is no session left to end.
        }
    }
}
