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
    ///
    /// <para>Whatever the handler that answers the paused request meets ends the hold at once, as a
    /// <see cref="HoldFailedException"/> carrying what the page showed (#1029): the handler runs on the
    /// protocol session's event, where nothing it throws reaches anyone, so a failure it let escape left
    /// the request paused, the navigation uncommitted and the hold waiting out its whole wait.</para>
    /// </summary>
    public static Task<Uri> HoldAsync(IPage page, string prefix, Func<Task> action, TimeSpan? wait = null) =>
        HoldAsync(page, prefix, action, wait, FulfilAsync);

    /// <summary>
    /// <see cref="HoldAsync(IPage, string, Func{Task}, TimeSpan?)"/> with the step that answers the paused
    /// request given: the tests' seam for a handler that fails (#1029).
    /// </summary>
    internal static async Task<Uri> HoldAsync(IPage page, string prefix, Func<Task> action, TimeSpan? wait, Func<ICDPSession, JsonElement, Task> answer)
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
                await answer(session, request);
                held.TrySetResult(new Uri(url));
            }
            catch (Exception failed)
            {
                // Every failure, not a chosen few: one let past this handler is lost (#1029).
                held.TrySetException(failed);
            }
        };

        Task acting = Task.CompletedTask;
        try
        {
            // The pattern's only wildcard is the trailing one: the prefix is matched as written.
            await session.SendAsync("Fetch.enable", new Dictionary<string, object>
            {
                ["patterns"] = new[] { new Dictionary<string, object> { ["urlPattern"] = Escape(prefix) + "*", ["requestStage"] = "Request" } },
            });

            // A navigation the handler failed to answer stays paused, so an action that waits for it
            // would wait out its own timeout first: the handler's failure does not wait for the action.
            acting = action();
            await Task.WhenAny(acting, held.Task);
            if (!held.Task.IsFaulted)
            {
                await acting;
            }

            try
            {
                return await held.Task.WaitAsync(wait ?? DefaultWait, TestContext.Current.CancellationToken);
            }
            catch (Exception) when (held.Task.IsFaulted)
            {
                // Faulted by the handler, whatever its failure's kind: a TimeoutException it met is not the wait's.
                Exception failure = held.Task.Exception!.InnerException!;
                PageShown shown = await ShownAsync(page);
                throw new HoldFailedException(
                    $"the hold on {Redacted.Url(prefix)} ended when its handler met {failure.GetType().Name}: {Redacted.Text(failure.Message, [])}; the page is at {shown.Url}.",
                    failure.GetType().Name,
                    shown);
            }
            catch (TimeoutException)
            {
                PageShown shown = await ShownAsync(page);
                throw new HoldTimeoutException($"no request to {prefix} came within {wait ?? DefaultWait}; the page is at {shown.Url}.", shown);
            }
        }
        finally
        {
            await session.SendAsync("Fetch.disable");
            await session.DetachAsync();

            // An action left running when the handler failed is not awaited: whatever it meets later is
            // observed here and dropped, because the hold has already reported the handler's failure.
            _ = acting.ContinueWith(static ended => ended.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    /// <summary>Answers the paused request with a page that says it was held, so it never leaves the browser.</summary>
    private static Task FulfilAsync(ICDPSession session, JsonElement request) =>
        session.SendAsync("Fetch.fulfillRequest", new Dictionary<string, object>
        {
            ["requestId"] = request.GetProperty("requestId").GetString()!,
            ["responseCode"] = 200,
            ["responseHeaders"] = new[] { new Dictionary<string, object> { ["name"] = "Content-Type", ["value"] = "text/html" } },
            ["body"] = Convert.ToBase64String(Encoding.UTF8.GetBytes($"<!doctype html><title>{HeldTitle}</title>")),
        });

    /// <summary>How long a timed-out hold waits for the page's text before it records that it could not read it.</summary>
    public static readonly TimeSpan TextWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// What the page showed when a hold timed out (#1003): its URL with no query and its visible text,
    /// both through <see cref="Redacted"/>, with the values of the page's own query and fragment and of
    /// the context's cookies taken out of the text as well. A page mid-navigation, or one with no body,
    /// may not give its text within <see cref="TextWait"/>; the record then says so, and the hold still
    /// fails with its own timeout.
    /// </summary>
    internal static async Task<PageShown> ShownAsync(IPage page)
    {
        string url = page.Url;
        try
        {
            // innerText is the rendered text, so a script's source and a hidden element are not in it
            // (https://playwright.dev/dotnet/docs/api/class-locator#locator-inner-text,
            // https://html.spec.whatwg.org/multipage/dom.html#the-innertext-idl-attribute); Redacted.Text
            // still takes out what the page renders.
            string text = await page.Locator("body").InnerTextAsync(new() { Timeout = (float)TextWait.TotalMilliseconds });
            IReadOnlyList<BrowserContextCookiesResult> cookies = await page.Context.CookiesAsync();
            return new PageShown(Redacted.Url(url), Redacted.Text(text, [.. Redacted.ParameterValues(url), .. cookies.Select(cookie => cookie.Value)]));
        }
        catch (Exception unread) when (unread is PlaywrightException or TimeoutException)
        {
            return new PageShown(Redacted.Url(url), $"(the page's text could not be read within {TextWait}: {unread.GetType().Name})");
        }
    }

    /// <summary>The protocol's pattern treats <c>*</c> and <c>?</c> as wildcards and <c>\</c> as their escape.</summary>
    private static string Escape(string literal) =>
        literal.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal).Replace("?", "\\?", StringComparison.Ordinal);
}

/// <summary>What a page showed, as an evidence file may hold it: <see cref="NavigationHold.ShownAsync"/> builds one.</summary>
public sealed record PageShown(string Url, string Text);

/// <summary>
/// A hold whose request never came (<see cref="NavigationHold.HoldAsync"/>), carrying what the page
/// showed at the time so a continuity row's evidence file can record it (#1003). It is a
/// <see cref="TimeoutException"/>, as the hold's failure always was, and
/// <see cref="Redacted.PlaywrightAsync(Func{Task})"/> rethrows it as one of its own kind.
/// </summary>
public sealed class HoldTimeoutException : TimeoutException
{
    public HoldTimeoutException(string message, PageShown page)
        : base(message) => Page = page;

    public PageShown Page { get; }
}

/// <summary>
/// A hold whose handler failed before it could answer the request (<see cref="NavigationHold.HoldAsync"/>,
/// #1029): the kind of what it met and what the page showed, both for a continuity row's evidence file.
/// Its message prints the failure's own message through <see cref="Redacted.Text"/>, so no URL's query
/// and no token is in it, and it carries no inner exception, whose message xUnit would print.
/// </summary>
public sealed class HoldFailedException : Exception
{
    public HoldFailedException(string message, string failure, PageShown page)
        : base(message)
    {
        Failure = failure;
        Page = page;
    }

    /// <summary>The type name of what the handler met.</summary>
    public string Failure { get; }

    public PageShown Page { get; }
}
