using System.Net;
using TestIdp;

namespace Fixtures.Tests;

/// <summary>
/// End session in both styles: standard RP-initiated, and the Cognito style that takes <c>client_id</c> and
/// <c>logout_uri</c>. The difference is what ADR-0011 puts behind <c>IIdpProfile</c>.
/// </summary>
public sealed class TestIdpEndSessionTests
{
    [Fact]
    public async Task Standard_logout_sends_the_browser_back_with_its_state()
    {
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();

        using var response = await harness.EndSessionAsync(new Dictionary<string, string?>
        {
            ["id_token_hint"] = login.IdToken,
            ["post_logout_redirect_uri"] = IdpHarness.PostLogout1,
            ["state"] = "logout-state",
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(IdpHarness.PostLogout1, response.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal("logout-state", IdpHarness.QueryValue(response.Headers.Location!, "state"));

        var logout = Assert.Single(harness.Idp.Logouts);
        Assert.Equal(EndSessionStyle.Standard, logout.Style);
        Assert.Equal(IdpHarness.Client1, logout.ClientId);
        Assert.True(logout.Ok);
        Assert.Equal(1, harness.Idp.EndSessions);
    }

    [Fact]
    public async Task Standard_logout_identifies_the_client_from_client_id_as_well()
    {
        await using var harness = await IdpHarness.StartAsync();

        using var response = await harness.EndSessionAsync(new Dictionary<string, string?>
        {
            ["client_id"] = IdpHarness.Client1,
            ["post_logout_redirect_uri"] = IdpHarness.PostLogout1,
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(IdpHarness.PostLogout1, response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Standard_logout_refuses_an_unregistered_post_logout_redirect_uri()
    {
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();

        using var response = await harness.EndSessionAsync(new Dictionary<string, string?>
        {
            ["id_token_hint"] = login.IdToken,
            ["post_logout_redirect_uri"] = "https://evil.test/",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unregistered_post_logout_redirect_uri", Assert.Single(harness.Idp.Logouts).Outcome);
    }

    [Fact]
    public async Task Standard_logout_refuses_one_clients_post_logout_uri_to_another_client()
    {
        await using var harness = await IdpHarness.StartAsync();

        using var response = await harness.EndSessionAsync(new Dictionary<string, string?>
        {
            ["client_id"] = IdpHarness.Client2,
            ["post_logout_redirect_uri"] = IdpHarness.PostLogout1,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Standard_logout_with_an_empty_id_token_hint_is_still_answered()
    {
        // A refresh answer with no ID token blanks the stored one, and logout then sends an empty hint
        // (evidence T-B13). A lenient provider still logs the browser out.
        await using var harness = await IdpHarness.StartAsync();

        using var response = await harness.EndSessionAsync(new Dictionary<string, string?>
        {
            ["id_token_hint"] = "",
            ["post_logout_redirect_uri"] = IdpHarness.PostLogout1,
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Null(Assert.Single(harness.Idp.Logouts).ClientId);
    }

    [Fact]
    public async Task Standard_logout_with_no_return_uri_answers_without_a_redirect()
    {
        await using var harness = await IdpHarness.StartAsync();
        var login = await harness.LoginAsync();

        using var response = await harness.EndSessionAsync(new Dictionary<string, string?> { ["id_token_hint"] = login.IdToken });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(Assert.Single(harness.Idp.Logouts).Ok);
    }

    [Fact]
    public async Task Standard_logout_refuses_an_unknown_client()
    {
        await using var harness = await IdpHarness.StartAsync();

        using var response = await harness.EndSessionAsync(new Dictionary<string, string?>
        {
            ["client_id"] = "nobody",
            ["post_logout_redirect_uri"] = IdpHarness.PostLogout1,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unknown_client", Assert.Single(harness.Idp.Logouts).Outcome);
    }

    [Fact]
    public async Task Cognito_style_logout_takes_client_id_and_logout_uri()
    {
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.EndSessionStyle = EndSessionStyle.Cognito;

        using var response = await harness.CognitoLogoutAsync(new Dictionary<string, string?>
        {
            ["client_id"] = IdpHarness.Client1,
            ["logout_uri"] = IdpHarness.PostLogout1,
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(IdpHarness.PostLogout1, response.Headers.Location!.ToString());
        var logout = Assert.Single(harness.Idp.Logouts);
        Assert.Equal(EndSessionStyle.Cognito, logout.Style);
        Assert.Equal(IdpHarness.Client1, logout.ClientId);
        Assert.True(logout.Ok);
    }

    [Theory]
    [InlineData("nobody", IdpHarness.PostLogout1, "unknown_client")]
    [InlineData(IdpHarness.Client1, "https://evil.test/", "unregistered_logout_uri")]
    [InlineData(IdpHarness.Client1, "", "missing_logout_uri")]
    public async Task Cognito_style_logout_refuses_a_request_that_breaks_one_rule(string clientId, string logoutUri, string reason)
    {
        await using var harness = await IdpHarness.StartAsync();

        using var response = await harness.CognitoLogoutAsync(new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["logout_uri"] = logoutUri,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(reason, Assert.Single(harness.Idp.Logouts).Outcome);
    }

    [Fact]
    public async Task A_client_registered_with_two_post_logout_uris_is_returned_to_either()
    {
        // Each identity profile returns to a different URI (ADR-0011): the standard profile to the signed-out
        // callback, the Cognito profile to the app's root. One client registered once must accept both.
        const string SignedOut = "https://app3.spa-poc.test:8443/signout-callback-oidc";
        const string Root = "https://app3.spa-poc.test:8443/";
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.AddClient("client-3", "secret-3", "https://app3.spa-poc.test:8443/signin-oidc", [SignedOut, Root]);

        using var standard = await harness.EndSessionAsync(new Dictionary<string, string?>
        {
            ["client_id"] = "client-3",
            ["post_logout_redirect_uri"] = SignedOut,
        });
        using var cognito = await harness.CognitoLogoutAsync(new Dictionary<string, string?>
        {
            ["client_id"] = "client-3",
            ["logout_uri"] = Root,
        });
        using var unregistered = await harness.EndSessionAsync(new Dictionary<string, string?>
        {
            ["client_id"] = "client-3",
            ["post_logout_redirect_uri"] = "https://app3.spa-poc.test:8443/elsewhere",
        });

        Assert.Equal(HttpStatusCode.Redirect, standard.StatusCode);
        Assert.Equal(SignedOut, standard.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Redirect, cognito.StatusCode);
        Assert.Equal(Root, cognito.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, unregistered.StatusCode);
        Assert.Equal(
            new[] { "ok", "ok", "unregistered_post_logout_redirect_uri" },
            harness.Idp.Logouts.Select(l => l.Outcome));
    }

    [Fact]
    public async Task Both_logout_endpoints_answer_whichever_style_discovery_advertises()
    {
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.EndSessionStyle = EndSessionStyle.Cognito;

        using var standard = await harness.EndSessionAsync(new Dictionary<string, string?>
        {
            ["client_id"] = IdpHarness.Client1,
            ["post_logout_redirect_uri"] = IdpHarness.PostLogout1,
        });
        using var cognito = await harness.CognitoLogoutAsync(new Dictionary<string, string?>
        {
            ["client_id"] = IdpHarness.Client1,
            ["logout_uri"] = IdpHarness.PostLogout1,
        });

        Assert.Equal(HttpStatusCode.Redirect, standard.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, cognito.StatusCode);
        Assert.Equal(2, harness.Idp.EndSessions);
    }
}
