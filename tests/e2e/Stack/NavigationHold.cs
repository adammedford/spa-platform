using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// Stops the browser at one request, inside the browser, for the deployed cell (#211), where there is
/// no stand-in load balancer to hold it (<see cref="TlsLoadBalancer.Hold"/>): the page's own Chrome
/// DevTools Protocol session pauses the first request whose URL starts with a prefix and answers it with
/// a page that says it was held, so the request never leaves the browser.
///
/// <para>Why the protocol's Fetch domain and not a Playwright route. A route sees only the first
/// request of a redirect chain (https://playwright.dev/dotnet/docs/api/class-browsercontext#browser-context-route),
/// and a login's callback is the last hop of one. Fetch pauses every hop, redirects included, and only
/// those matching its pattern (https://chromedevtools.github.io/devtools-protocol/tot/Fetch/#method-enable),
/// so every other request is the browser's own, with the Fetch Metadata a route's continued request
/// lost (<see cref="TlsLoadBalancer"/>).</para>
/// </summary>
public static class NavigationHold
{
    /// <summary>The title of the page a held request is answered with.</summary>
    public const string HeldTitle = TlsLoadBalancer.HeldTitle;

    /// <summary>How long a hold waits for its request by default: a provider's login form and its redirect.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs <paramref name="action"/> and holds the first request of <paramref name="page"/> whose URL
    /// starts with <paramref name="prefix"/>, which must come within <paramref name="wait"/>. Returns
    /// that request's URL. The hold ends when this returns, whether or not the request came.
    /// </summary>
    public static async Task<Uri> HoldAsync(IPage page, string prefix, Func<Task> action, TimeSpan? wait = null)
    {
        TaskCompletionSource<Uri> held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ICDPSession session = await page.Context.NewCDPSessionAsync(page);
        session.Event("Fetch.requestPaused").OnEvent += async (_, paused) =>
        {
            if (paused is not { } request)
            {
                return;
            }

            try
            {
                string url = request.GetProperty("request").GetProperty("url").GetString()!;
                await session.SendAsync("Fetch.fulfillRequest", new Dictionary<string, object>
                {
                    ["requestId"] = request.GetProperty("requestId").GetString()!,
                    ["responseCode"] = 200,
                    ["responseHeaders"] = new[] { new Dictionary<string, object> { ["name"] = "Content-Type", ["value"] = "text/html" } },
                    ["body"] = Convert.ToBase64String(Encoding.UTF8.GetBytes($"<!doctype html><title>{HeldTitle}</title>")),
                });
                held.TrySetResult(new Uri(url));
            }
            catch (Exception failed) when (failed is PlaywrightException or JsonException or KeyNotFoundException)
            {
                held.TrySetException(failed);
            }
        };

        try
        {
            // The pattern's only wildcard is the trailing one: the prefix is matched as written.
            await session.SendAsync("Fetch.enable", new Dictionary<string, object>
            {
                ["patterns"] = new[] { new Dictionary<string, object> { ["urlPattern"] = Escape(prefix) + "*", ["requestStage"] = "Request" } },
            });
            await action();
            try
            {
                return await held.Task.WaitAsync(wait ?? DefaultWait, TestContext.Current.CancellationToken);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"no request to {prefix} came within {wait ?? DefaultWait}; the page is at {page.Url}.");
            }
        }
        finally
        {
            await session.SendAsync("Fetch.disable");
            await session.DetachAsync();
        }
    }

    /// <summary>The protocol's pattern treats <c>*</c> and <c>?</c> as wildcards and <c>\</c> as their escape.</summary>
    private static string Escape(string literal) =>
        literal.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal).Replace("?", "\\?", StringComparison.Ordinal);
}
