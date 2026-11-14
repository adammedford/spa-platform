using System.Collections.Concurrent;
using System.Net;
using Gateway.E2ETests.Stack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gateway.E2ETests.SittingBrowser;

/// <summary>
/// A provider's login form in the way of the local stack's logins (#1195): a listener on loopback,
/// the test provider's host, so the sitting browser reads its page as the provider's. The test
/// provider approves every authorization at once and shows no form (fixtures/TestIdp), so
/// <see cref="Detour"/>, given to <see cref="TlsLoadBalancer.Detour"/>, sends each callback here the
/// first time it arrives. The form's submit is answered after <see cref="AnswersAfter"/>, or never,
/// with a redirect to that callback, as Cognito's managed login answers a signed-in form with one
/// (#1029). Nothing here writes a log line, and no callback is printed: each carries a code and a state.
/// </summary>
internal sealed class FormProvider : IAsyncDisposable
{
    /// <summary>
    /// The fields <c>DeployedStack</c>'s form filler and the sitting browser's prompt read: username,
    /// password, a submit button. The page's script posts the form a moment after the click, not the
    /// click itself: Playwright's click waits for a navigation the click starts
    /// (https://playwright.dev/dotnet/docs/navigations#navigation-events), so a plain form's submit
    /// would hold the filler until the answer, where the cell's filler came back with the page still at
    /// the form (#348's D0 and D2: <c>login-prompt</c> on a login that succeeded).
    /// </summary>
    private const string Form = """
        <!doctype html><title>Signin</title>
        <form method="post" action="/login">
          <input name="username"><input name="password" type="password"><button type="submit">Sign in</button>
        </form>
        <script>
          const form = document.querySelector('form');
          form.addEventListener('submit', event => { event.preventDefault(); setTimeout(() => form.submit(), 250); });
        </script>
        """;

    private readonly WebApplication app;
    private readonly CancellationTokenSource closing = new();
    private readonly ConcurrentDictionary<string, bool> answered = new(StringComparer.Ordinal);
    private volatile Uri? waiting;
    private int submitted;

    private FormProvider(WebApplication app) => this.app = app;

    /// <summary>The form's page.</summary>
    public Uri Login { get; private set; } = null!;

    /// <summary>Whether a callback is sent to the form; false leaves the test provider's own approval at once.</summary>
    public bool InTheWay { get; set; } = true;

    /// <summary>How long after a submit its answer comes; null is a submit that is never answered.</summary>
    public TimeSpan? AnswersAfter { get; set; } = TimeSpan.Zero;

    /// <summary>How many submits have arrived.</summary>
    public int Submitted => Volatile.Read(ref submitted);

    public static async Task<FormProvider> StartAsync(CancellationToken ct)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));

        WebApplication app = builder.Build();
        FormProvider provider = new(app);
        app.Run(provider.AnswerAsync);
        await app.StartAsync(ct);

        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        provider.Login = new Uri(new Uri(address), "/login");
        return provider;
    }

    /// <summary>Where the load balancer sends <paramref name="url"/>: the form, for a callback no submit has been answered with yet; otherwise null.</summary>
    public Uri? Detour(Uri url)
    {
        if (!InTheWay || url.AbsolutePath != "/signin-oidc" || answered.ContainsKey(url.AbsoluteUri))
        {
            return null;
        }

        waiting = url;
        return Login;
    }

    private async Task AnswerAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(Form, context.RequestAborted);
            return;
        }

        Interlocked.Increment(ref submitted);
        using CancellationTokenSource gone = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, closing.Token);
        try
        {
            await Task.Delay(AnswersAfter ?? Timeout.InfiniteTimeSpan, gone.Token);
        }
        catch (OperationCanceledException)
        {
            // The browser left the submit, or the test ended: there is no one to answer.
            context.Abort();
            return;
        }

        if (waiting is not { } callback)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        answered[callback.AbsoluteUri] = true;
        context.Response.StatusCode = StatusCodes.Status302Found;
        context.Response.Headers.Location = callback.AbsoluteUri;
    }

    public async ValueTask DisposeAsync()
    {
        await closing.CancelAsync();
        await app.DisposeAsync();
        closing.Dispose();
    }
}
