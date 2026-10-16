using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// Plan row 3.5: the OpenID Connect handler's events. A challenge on a request that is not a
/// navigation is answered 401 rather than redirected (ADR-0008), the ticket keeps only the claims the
/// browser contract exposes, and every refused callback ends on one fixed page.
/// </summary>
public sealed class AuthenticationEventsTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);
    private static readonly AppDefinition App2 = TestApps.Entry("app2", issuer: IdentityGateway.IssuerName);

    [Fact]
    public async Task A_fetch_gets_401_where_a_navigation_gets_a_redirect()
    {
        // ADR-0008 and N-19: an unauthenticated fetch gets 401, never a redirect. The control is the
        // same path on the same host, made as a navigation.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);

        IdentityGateway.Answer navigation = await gateway.SendAsync(App1, "/", headers: IdentityGateway.Navigation);
        IdentityGateway.Answer fetch = await gateway.SendAsync(App1, "/", headers: IdentityGateway.Fetch);

        Assert.Equal(302, navigation.Status);
        Assert.StartsWith(gateway.Idp.AuthorizeEndpoint, navigation.Location, StringComparison.Ordinal);

        Assert.Equal(401, fetch.Status);
        Assert.Equal("", fetch.Location);
        Assert.Equal("application/problem+json", fetch.ContentType);
        using JsonDocument problem = JsonDocument.Parse(fetch.Body);
        Assert.Equal(401, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("unauthenticated", problem.RootElement.GetProperty("code").GetString());

        // No login was started, so the fetch is handed no login-state cookies and no rule id: a 401
        // that reports the session's state is not a rejection (contracts/request-gate.md).
        Assert.NotEmpty(navigation.Cookies);
        Assert.Empty(fetch.Cookies);
        Assert.Null(fetch.Rule);
    }

    [Fact]
    public async Task A_request_without_fetch_metadata_is_not_treated_as_a_navigation()
    {
        // The gateway redirects only what the browser says is a navigation (ADR-0007).
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);

        IdentityGateway.Answer bare = await gateway.SendAsync(App1, "/");

        Assert.Equal(401, bare.Status);
        Assert.Equal("", bare.Location);
    }

    [Fact]
    public async Task A_frame_loading_the_shell_is_not_treated_as_a_navigation()
    {
        // contracts/request-gate.md reads a navigation as Sec-Fetch-Mode: navigate with
        // Sec-Fetch-Dest: document (gate.navigation); an iframe's load has the mode and not the dest.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);

        IdentityGateway.Answer framed = await gateway.SendAsync(App1, "/", headers: new Dictionary<string, string>
        {
            ["Sec-Fetch-Site"] = "same-origin",
            ["Sec-Fetch-Mode"] = "navigate",
            ["Sec-Fetch-Dest"] = "iframe",
        });

        Assert.Equal(401, framed.Status);
        Assert.Equal("", framed.Location);
    }

    [Fact]
    public async Task A_profiles_redirect_event_still_runs_on_a_navigation_and_never_on_a_fetch()
    {
        // ADR-0011: the profile configures last, so the gateway's events wrap what it set rather than
        // being replaced by it.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);
        int profileRedirects = 0;
        gateway.Profile.OnConfigure = (options, _, _) => options.Events.OnRedirectToIdentityProvider = context =>
        {
            profileRedirects++;
            context.ProtocolMessage.SetParameter("profile_marker", "yes");
            return Task.CompletedTask;
        };

        IdentityGateway.Answer fetch = await gateway.SendAsync(App1, "/", headers: IdentityGateway.Fetch);
        IdentityGateway.Answer navigation = await gateway.SendAsync(App1, "/", headers: IdentityGateway.Navigation);

        Assert.Equal(401, fetch.Status);
        Assert.Equal(302, navigation.Status);
        Assert.Equal("yes", navigation.RedirectQuery["profile_marker"]);
        Assert.Equal(1, profileRedirects);
    }

    [Fact]
    public async Task The_stored_ticket_holds_sub_name_and_email_only()
    {
        // The provider's ID token carries sub, name and email beside token_use, auth_time, jti, sid
        // and the like (fixtures/TestIdp). Only the three the browser contract's /bff/session exposes
        // reach the session; the tokens themselves stay in the ticket's properties (ADR-0004).
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);

        await gateway.LoginAsync(App1);

        (_, SessionRecord record) = await gateway.Sessions.SingleAsync();
        AuthenticationTicket ticket = Assert.IsType<AuthenticationTicket>(TicketSerializer.Default.Deserialize(record.Ticket.ToArray()));
        Claim[] claims = [.. ticket.Principal.Claims];
        Assert.Equal(
            [("email", "alice@spa-poc.test"), ("name", "alice (display)"), ("sub", "alice")],
            claims.Select(c => (c.Type, c.Value)).OrderBy(c => c.Type, StringComparer.Ordinal));
        Assert.Single(ticket.Principal.Identities);
        Assert.True(ticket.Principal.Identity!.IsAuthenticated);
        Assert.False(string.IsNullOrEmpty(ticket.Properties.GetTokenValue("refresh_token")));
        Assert.Equal("alice", (await gateway.SendAsync(App1, IdentityGateway.ApiPath, (await gateway.LoginAsync(App1)).Jar)).Subject);
    }

    [Fact]
    public async Task Every_refused_callback_shows_the_same_page_and_it_reflects_nothing_from_the_request()
    {
        // Five refusals for five different reasons (S-C1, S-C3, R-B4, N-05 and the provider's
        // access_denied), each carrying a marker in the query. Every one ends on identical bytes, under
        // auth.callback (contracts/request-gate.md), with no session created (S-C4, R-B5).
        const string Marker = "reflected-marker-7f3a";
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1, App2], RemoteFailureRecorder.Install(out RemoteFailureRecorder recorder));

        IdentityGateway.Authorized replayed = await gateway.AuthorizeAsync(App1);
        IdentityGateway.Authorized noCorrelation = await gateway.AuthorizeAsync(App1);
        IdentityGateway.Authorized noNonce = await gateway.AuthorizeAsync(App1);
        IdentityGateway.Authorized tampered = await gateway.AuthorizeAsync(App1);
        IdentityGateway.Authorized denied = await gateway.AuthorizeAsync(App1);
        string tamperedState = Query(tampered.Callback)["state"] + "AAAA";

        IdentityGateway.Answer[] refusals =
        [
            await gateway.SendAsync(App2, Marked(replayed.Callback, Marker), replayed.LoginState),
            await gateway.SendAsync(App1, Marked(noCorrelation.Callback, Marker), Without(noCorrelation.LoginState, "__Host-spa-corr.")),
            await gateway.SendAsync(App1, Marked(noNonce.Callback, Marker), Without(noNonce.LoginState, "__Host-spa-nonce.")),
            await gateway.SendAsync(App1, Marked(tampered.Callback, Marker, ("state", tamperedState)), tampered.LoginState),
            await gateway.SendAsync(App1, Denied(denied.Callback, Marker), denied.LoginState),
        ];

        Assert.Equal(5, recorder.Failures.Count);
        IdentityGateway.Answer first = refusals[0];
        Assert.Equal(400, first.Status);
        Assert.StartsWith("text/html", first.ContentType, StringComparison.Ordinal);
        Assert.Contains("<html", first.Body, StringComparison.Ordinal);
        Assert.All(refusals, refusal =>
        {
            Assert.Equal((first.Status, first.ContentType, first.Body, ""), (refusal.Status, refusal.ContentType, refusal.Body, refusal.Location));
            Assert.Equal("auth.callback", refusal.Rule);
            Assert.DoesNotContain(Marker, refusal.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(refusal.Cookies, c => c.Name.Value == CookieOptionsSetup.CookieName && !string.IsNullOrEmpty(c.Value.Value));
        });
        Assert.DoesNotContain(tamperedState, first.Body, StringComparison.Ordinal);
        Assert.Empty(gateway.Sessions.Created);
    }

    private static Dictionary<string, string> Query(Uri callback) =>
        QueryHelpers.ParseQuery(callback.Query).ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);

    /// <summary>The callback with an extra parameter carrying <paramref name="marker"/>, and any replacements.</summary>
    private static string Marked(Uri callback, string marker, params (string Name, string Value)[] replace)
    {
        Dictionary<string, string?> query = Query(callback).ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.Ordinal);
        foreach ((string name, string value) in replace)
        {
            query[name] = value;
        }

        query["error_description"] = marker;
        return QueryHelpers.AddQueryString(callback.AbsolutePath, query);
    }

    /// <summary>What the provider sends back when the user declines: its error, no code, the login's own state.</summary>
    private static string Denied(Uri callback, string marker) =>
        QueryHelpers.AddQueryString(callback.AbsolutePath, new Dictionary<string, string?>
        {
            ["error"] = "access_denied",
            ["error_description"] = marker,
            ["state"] = Query(callback)["state"],
        });

    private static Dictionary<string, string> Without(IReadOnlyDictionary<string, string> cookies, string prefix) =>
        cookies.Where(c => !c.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
}
