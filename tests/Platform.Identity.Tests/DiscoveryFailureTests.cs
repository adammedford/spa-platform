using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// #908, #543's held row: what <c>GET /bff/login</c> and the shell gate answer when the provider's
/// discovery document cannot be fetched. Discovery is read on the first challenge, not at start-up
/// (<see cref="IssuerDirectory"/>), and the handler's challenge reads it before it writes anything
/// (OpenIdConnectHandler.HandleChallengeAsyncInternal:
/// https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/OpenIdConnect/src/OpenIdConnectHandler.cs).
/// Measured before the change, in each of the three ways: every request threw the identity model's
/// <c>InvalidOperationException</c> (IDX20803, its message holding the discovery URL) out of the
/// challenge, which a server answers <c>500</c> with no body, and the gateway logged nothing of it.
/// </summary>
public sealed class DiscoveryFailureTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);

    /// <summary>The backchannel's timeout here, so the silent provider is given up on quickly.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(1);

    /// <summary>Values a log line must not hold (N-16): a cookie's value, and a query's.</summary>
    private const string CookieCanary = "canary-cookie-value-3e9a0c";

    private const string QueryCanary = "canary-query-value-81f2d4";

    public static TheoryData<DiscoveryOutage, string, bool> Requests()
    {
        TheoryData<DiscoveryOutage, string, bool> data = [];
        foreach (DiscoveryOutage way in Enum.GetValues<DiscoveryOutage>())
        {
            data.Add(way, "/bff/login?returnUrl=/orders?q=" + QueryCanary, true);
            data.Add(way, "/bff/login?returnUrl=/orders?q=" + QueryCanary, false);
            data.Add(way, "/orders/42?q=" + QueryCanary, true);
            data.Add(way, "/assets/app.js?q=" + QueryCanary, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task A_challenge_whose_discovery_cannot_be_fetched_is_answered_503_for_a_navigation_and_401_for_a_fetch(
        DiscoveryOutage way, string pathAndQuery, bool navigation)
    {
        // A navigation gets the static failure page with 503: a person arrived, the provider is out, and
        // the page's link tries again. A fetch gets the 401 a fetch always gets, since its answer never
        // depended on the provider (ADR-0008); the SPA's navigation to /bff/login then meets the 503.
        // Neither carries a rule id or starts a login: nothing was refused, and no login state is set.
        // Mutation: remove PlatformOidcHandler's HandleChallengeAsync override, and every case throws the
        // identity model's InvalidOperationException, today's 500 with no body (12 of 12 fail).
        // Answer a fetch as a navigation, or a navigation as a fetch (6 of 12 fail each); drop the page's
        // Cache-Control (6 of 12 fail).
        await using UnreachableIssuer issuer = UnreachableIssuer.Start(way);
        CapturedLog log = new();
        await using IdentityGateway gateway = await StartAsync(issuer, log);

        IdentityGateway.Answer answer = await gateway.SendAsync(App1, pathAndQuery,
            cookies: new Dictionary<string, string> { ["unrelated"] = CookieCanary },
            headers: navigation ? IdentityGateway.Navigation : IdentityGateway.Fetch);

        if (navigation)
        {
            Assert.Equal((503, "text/html; charset=utf-8", AuthenticationEvents.FailurePage), (answer.Status, answer.ContentType, answer.Body));
            Assert.Equal("no-store", answer.CacheControl);
        }
        else
        {
            Assert.Equal((401, "unauthenticated"), (answer.Status, ProblemCode(answer)));
        }

        Assert.Equal("", answer.Location);
        Assert.Null(answer.Rule);
        // No login state: the challenge wrote nothing (the expired session cookie /bff/login's discard
        // sends is that discard's, and comes before the challenge).
        Assert.DoesNotContain(answer.Cookies, c => c.Name.Value!.StartsWith("__Host-spa-corr.", StringComparison.Ordinal)
            || c.Name.Value!.StartsWith("__Host-spa-nonce.", StringComparison.Ordinal));
        // The provider was asked, except where nothing listens.
        Assert.Equal(way == DiscoveryOutage.Refused, issuer.Requests == 0);
    }

    [Theory]
    [InlineData(DiscoveryOutage.Refused)]
    [InlineData(DiscoveryOutage.Silent)]
    [InlineData(DiscoveryOutage.Failing)]
    public async Task The_provider_error_is_logged_once_at_Warning_with_the_issuer_and_the_exception_type_and_nothing_else(DiscoveryOutage way)
    {
        // One Warning under Platform.Identity, which the telemetry floor keeps (the framework handler's
        // category is floored to None, LogFloors), naming the issuer and the type of the exception that
        // caused the failure, and nothing of the request or the provider: no URL, no query, no
        // cookie, no answer body, and no exception attached, whose message holds the discovery URL.
        // Mutations: attach the exception to the line (Exception is not null, and the URL is in its
        // text: 3 of 3 fail); log the exception's message in place of its type (the URL is in the
        // line: 3 of 3 fail); log the outer
        // InvalidOperationException's type, the same for every way (3 of 3 fail); log at Information (3 of 3 fail).
        await using UnreachableIssuer issuer = UnreachableIssuer.Start(way);
        CapturedLog log = new();
        await using IdentityGateway gateway = await StartAsync(issuer, log);

        _ = await gateway.SendAsync(App1, "/bff/login?returnUrl=/orders?q=" + QueryCanary,
            cookies: new Dictionary<string, string> { ["unrelated"] = CookieCanary },
            headers: IdentityGateway.Navigation);

        CapturedLine warning = Assert.Single(log.Lines, l => l.Level >= LogLevel.Warning);
        Assert.Equal((LogLevel.Warning, "Platform.Identity.ProviderDiscovery"), (warning.Level, warning.Category));
        Assert.Null(warning.Exception);
        Assert.Contains($"issuer {IdentityGateway.IssuerName}", warning.Message, StringComparison.Ordinal);
        // The cause the document retriever met, below the identity model's two wrappers: the 503 is the
        // retriever's own IOException, the refusal the client's HttpRequestException, the silence the
        // client's timeout.
        string expectedType = way switch
        {
            DiscoveryOutage.Failing => nameof(IOException),
            DiscoveryOutage.Refused => nameof(System.Net.Http.HttpRequestException),
            _ => nameof(TaskCanceledException),
        };
        Assert.Contains(warning.State, p => p.Key == "ExceptionType" && Equals(p.Value, expectedType));

        string authority = issuer.Authority.AbsoluteUri.TrimEnd('/');
        foreach (CapturedLine line in log.Lines)
        {
            foreach (string canary in new[] { CookieCanary, QueryCanary, UnreachableIssuer.Body, authority, "IDX" })
            {
                Assert.DoesNotContain(canary, line.Text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task A_browser_gone_before_discovery_fails_is_neither_answered_nor_logged()
    {
        // The failure is the browser's, not the provider's: the read ended because the request did, so
        // the request is not answered and no Warning reports an outage that did not happen (#543's draft
        // gives workstream 2 the same rule for the object store). Mutation: drop the RequestAborted check,
        // and the 503 page is written and the Warning logged (fails).
        await using UnreachableIssuer issuer = UnreachableIssuer.Start(DiscoveryOutage.Silent);
        CapturedLog log = new();
        await using IdentityGateway gateway = await StartAsync(issuer, log);
        using CancellationTokenSource browser = new();
        Task<IdentityGateway.Answer> sent = gateway.SendAsync(App1, "/bff/login", headers: IdentityGateway.Navigation, browser: browser);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        while (issuer.Requests == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), deadline.Token);
        }

        await browser.CancelAsync();
        IdentityGateway.Answer answer = await sent;

        Assert.NotEqual(503, answer.Status);
        Assert.Equal("", answer.Body);
        Assert.DoesNotContain(log.Lines, l => l.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_callback_whose_discovery_cannot_be_fetched_ends_on_the_failure_page_under_auth_callback()
    {
        // The callback reads discovery inside the remote handler's try, so a failure there reaches the
        // remote-failure event like every other refused callback: 400, the one page, auth.callback. It
        // is kept (decided by the implementer, #908): the login's state is spent, so a 503 inviting a
        // retry of this URL would retry nothing, and the page's link starts a fresh login, which meets
        // the 503 above while the provider is out. Measured with a second gateway on the first's key
        // ring, whose issuer's discovery is refused: the first gateway's challenge, the provider's
        // authorize, then the callback to the second.
        await using IdentityGateway first = await IdentityGateway.StartAsync([App1]);
        IdentityGateway.Authorized authorized = await first.AuthorizeAsync(App1, "/bff/login");
        await using UnreachableIssuer issuer = UnreachableIssuer.Start(DiscoveryOutage.Refused);
        CapturedLog log = new();
        await using IdentityGateway second = await IdentityGateway.StartAsync([App1],
            services: s => s.AddSingleton(log.Provider), keys: first.Keys, beside: first, authority: issuer.Authority);

        IdentityGateway.Answer callback = await second.SendAsync(App1, authorized.Callback.PathAndQuery, authorized.LoginState);

        Assert.Equal((400, AuthenticationEvents.FailurePage, RuleIds.AuthCallback), (callback.Status, callback.Body, callback.Rule));
        Assert.Empty(second.Sessions.Created);
    }

    private static Task<IdentityGateway> StartAsync(UnreachableIssuer issuer, CapturedLog log) =>
        IdentityGateway.StartAsync([App1],
            services: s =>
            {
                s.AddSingleton(log.Provider);
                s.AddSingleton(new AccessTokenSourceOptions { ProviderDeadline = Deadline });
            },
            authority: issuer.Authority);

    private static string? ProblemCode(IdentityGateway.Answer answer)
    {
        using System.Text.Json.JsonDocument problem = System.Text.Json.JsonDocument.Parse(answer.Body);
        return problem.RootElement.GetProperty("code").GetString();
    }
}
