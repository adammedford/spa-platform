using System.Net;
using TestIdp;

namespace Fixtures.Tests;

/// <summary>
/// The fidelity switch (#997): with <see cref="TestIdpFaults.ModelProviderSession"/> on, an interactive
/// sign-in leaves one provider session cookie on the provider's origin, shared by every client, and
/// <c>prompt=none</c> either issues a code for that session's subject or answers <c>login_required</c>.
/// Both logouts end the session. The harness's client keeps cookies, so it is one user agent.
/// </summary>
public sealed class TestIdpProviderSessionTests
{
    [Fact]
    public async Task An_interactive_sign_in_sets_a_session_that_a_silent_request_for_another_client_reads()
    {
        // Mutation: issuing the silent code for the login_hint (or DefaultSubject) instead of the session's
        // subject turns this red; so does setting no cookie on the interactive sign-in, and so does recording
        // the silent code as not silent.
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.ModelProviderSession = true;

        using var interactive = await harness.AuthorizeAsync(IdpHarness.AuthorizeQuery(Pkce.Challenge(Pkce.NewVerifier()), loginHint: "bob"));
        Assert.Equal(HttpStatusCode.Redirect, interactive.StatusCode);
        Assert.Contains(interactive.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith(TestIdentityProvider.SessionCookieName + "=", StringComparison.Ordinal));

        var verifier = Pkce.NewVerifier();
        var query = IdpHarness.AuthorizeQuery(Pkce.Challenge(verifier), IdpHarness.Client2, IdpHarness.Redirect2, state: "hop-2");
        query["prompt"] = "none";
        using var silent = await harness.AuthorizeAsync(query);

        Assert.Equal(HttpStatusCode.Redirect, silent.StatusCode);
        Assert.StartsWith(IdpHarness.Redirect2, silent.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal("hop-2", IdpHarness.QueryValue(silent.Headers.Location!, "state"));
        var code = IdpHarness.QueryValue(silent.Headers.Location!, "code");
        var answer = await harness.CodeGrantAsync(code, verifier, IdpHarness.Client2, IdpHarness.Secret2, IdpHarness.Redirect2);
        Assert.True(answer.Ok, answer.Error);
        Assert.Equal("bob", IdpHarness.Claim(answer.AccessToken!, "sub"));

        var hop = harness.Idp.Authorizations[^1];
        Assert.Equal(IdpHarness.Client2, hop.ClientId);
        Assert.Equal("bob", hop.Subject);
        Assert.True(hop.Ok);
        Assert.True(hop.Silent);
        Assert.False(harness.Idp.Authorizations[0].Silent);
    }

    [Fact]
    public async Task With_the_switch_off_prompt_none_is_answered_as_a_request_without_it()
    {
        // Mutation: reading prompt=none whatever the switch says (dropping the ModelProviderSession guard)
        // answers login_required here and turns this red; so does setting the cookie with the switch off.
        await using var harness = await IdpHarness.StartAsync();
        var query = IdpHarness.AuthorizeQuery(Pkce.Challenge(Pkce.NewVerifier()), IdpHarness.Client2, IdpHarness.Redirect2, loginHint: "carol");
        query["prompt"] = "none";

        using var response = await harness.AuthorizeAsync(query);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.NotEmpty(IdpHarness.QueryValue(response.Headers.Location!, "code"));
        Assert.Equal("state-1", IdpHarness.QueryValue(response.Headers.Location!, "state"));
        Assert.False(HasParameter(response.Headers.Location!, "error"));
        var hop = Assert.Single(harness.Idp.Authorizations);
        Assert.Equal(("carol", "ok", false), (hop.Subject, hop.Outcome, hop.Silent));
    }

    [Fact]
    public async Task With_no_session_prompt_none_redirects_login_required_with_the_state_and_no_code()
    {
        // Mutation: answering a session-less prompt=none with a code for the login_hint (skipping the
        // login_required branch) turns this red; so does dropping the state from the error redirect.
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.ModelProviderSession = true;
        var query = IdpHarness.AuthorizeQuery(Pkce.Challenge(Pkce.NewVerifier()), state: "opaque-state", loginHint: "bob");
        query["prompt"] = "none";

        using var response = await harness.AuthorizeAsync(query);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal(IdpHarness.Redirect1, location.GetLeftPart(UriPartial.Path));
        Assert.Equal("login_required", IdpHarness.QueryValue(location, "error"));
        Assert.Equal("opaque-state", IdpHarness.QueryValue(location, "state"));
        Assert.False(HasParameter(location, "code"));
        Assert.False(response.Headers.Contains("Set-Cookie"));

        var hop = Assert.Single(harness.Idp.Authorizations);
        Assert.Equal((IdpHarness.Client1, TestIdp.Authorization.LoginRequired, true, false), (hop.ClientId, hop.Outcome, hop.Silent, hop.Ok));
        Assert.Empty(harness.Idp.Grants);
    }

    [Theory]
    [InlineData("nobody", IdpHarness.Redirect1, "unknown_client")]
    [InlineData(IdpHarness.Client1, "https://evil.test/signin-oidc", "unregistered_redirect_uri")]
    public async Task Prompt_none_for_an_unknown_client_or_unregistered_redirect_uri_is_the_same_400(string clientId, string redirectUri, string reason)
    {
        // Mutation: moving the prompt=none branch above the client and redirect-URI checks redirects
        // login_required to an unregistered URI and turns both cases red.
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.ModelProviderSession = true;
        var query = IdpHarness.AuthorizeQuery(Pkce.Challenge(Pkce.NewVerifier()), clientId, redirectUri);
        query["prompt"] = "none";

        using var response = await harness.AuthorizeAsync(query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(reason, Assert.Single(harness.Idp.Authorizations).Outcome);
    }

    [Fact]
    public async Task An_accepted_standard_logout_ends_the_session()
    {
        // Mutation: removing EndSession from the standard logout's redirect answer turns this red.
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.ModelProviderSession = true;
        var login = await harness.LoginAsync();

        using var logout = await harness.EndSessionAsync(new Dictionary<string, string?>
        {
            ["id_token_hint"] = login.IdToken,
            ["post_logout_redirect_uri"] = IdpHarness.PostLogout1,
        });
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        Assert.Equal("login_required", await SilentOutcomeAsync(harness, IdpHarness.Client1, IdpHarness.Redirect1));
    }

    [Fact]
    public async Task A_standard_logout_with_no_return_uri_ends_the_session_too()
    {
        // Mutation: removing EndSession from the hint-only logout's "signed out" answer turns this red.
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.ModelProviderSession = true;
        var login = await harness.LoginAsync();

        using var logout = await harness.EndSessionAsync(new Dictionary<string, string?> { ["id_token_hint"] = login.IdToken });
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        Assert.Equal("login_required", await SilentOutcomeAsync(harness, IdpHarness.Client2, IdpHarness.Redirect2));
    }

    [Fact]
    public async Task A_cognito_style_logout_through_one_client_ends_the_session_for_every_client()
    {
        // Mutation: removing EndSession from the Cognito-style logout turns this red.
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.ModelProviderSession = true;
        await harness.LoginAsync();
        Assert.Equal("ok", await SilentOutcomeAsync(harness, IdpHarness.Client2, IdpHarness.Redirect2));

        using var logout = await harness.CognitoLogoutAsync(new Dictionary<string, string?>
        {
            ["client_id"] = IdpHarness.Client1,
            ["logout_uri"] = IdpHarness.PostLogout1,
        });
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        Assert.Equal("login_required", await SilentOutcomeAsync(harness, IdpHarness.Client1, IdpHarness.Redirect1));
        Assert.Equal("login_required", await SilentOutcomeAsync(harness, IdpHarness.Client2, IdpHarness.Redirect2));
    }

    [Fact]
    public async Task A_refused_logout_leaves_the_session_alone()
    {
        // Mutation: ending the session before the logout's checks (in the Refuse path) turns this red.
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.ModelProviderSession = true;
        await harness.LoginAsync();

        using var refused = await harness.CognitoLogoutAsync(new Dictionary<string, string?>
        {
            ["client_id"] = IdpHarness.Client1,
            ["logout_uri"] = "https://evil.test/",
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        Assert.Equal("ok", await SilentOutcomeAsync(harness, IdpHarness.Client2, IdpHarness.Redirect2));
    }

    [Fact]
    public async Task A_second_interactive_sign_in_as_another_subject_replaces_the_sessions_subject()
    {
        // Mutation: keeping the first session's subject when a session is already there (StartSession
        // returning early on an existing cookie) turns this red.
        await using var harness = await IdpHarness.StartAsync();
        harness.Idp.Faults.ModelProviderSession = true;
        await harness.LoginAsync(loginHint: "alice");
        await harness.LoginAsync(IdpHarness.Client2, IdpHarness.Secret2, IdpHarness.Redirect2, loginHint: "mallory");

        var verifier = Pkce.NewVerifier();
        var query = IdpHarness.AuthorizeQuery(Pkce.Challenge(verifier), loginHint: "alice");
        query["prompt"] = "none";
        using var silent = await harness.AuthorizeAsync(query);
        var answer = await harness.CodeGrantAsync(IdpHarness.QueryValue(silent.Headers.Location!, "code"), verifier);

        Assert.True(answer.Ok, answer.Error);
        Assert.Equal("mallory", IdpHarness.Claim(answer.AccessToken!, "sub"));
        Assert.Equal("mallory", IdpHarness.Claim(answer.IdToken!, "sub"));
    }

    [Fact]
    public async Task The_switch_is_settable_while_the_provider_runs()
    {
        // Mutation: reading the switch once at start (a field copied in StartAsync) turns this red.
        await using var harness = await IdpHarness.StartAsync();
        await harness.LoginAsync();
        Assert.Equal("ok", await SilentOutcomeAsync(harness, IdpHarness.Client1, IdpHarness.Redirect1));

        harness.Idp.Faults.ModelProviderSession = true;
        Assert.Equal("login_required", await SilentOutcomeAsync(harness, IdpHarness.Client1, IdpHarness.Redirect1));

        harness.Idp.Faults.ModelProviderSession = false;
        Assert.Equal("ok", await SilentOutcomeAsync(harness, IdpHarness.Client1, IdpHarness.Redirect1));
    }

    private static bool HasParameter(Uri location, string name)
        => Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(location.Query).ContainsKey(name);

    /// <summary>Sends one <c>prompt=none</c> authorization and returns its recorded outcome, after checking the
    /// answer is a redirect to the client either way.</summary>
    private static async Task<string> SilentOutcomeAsync(IdpHarness harness, string clientId, string redirectUri)
    {
        var query = IdpHarness.AuthorizeQuery(Pkce.Challenge(Pkce.NewVerifier()), clientId, redirectUri);
        query["prompt"] = "none";
        using var response = await harness.AuthorizeAsync(query);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(redirectUri, response.Headers.Location!.ToString(), StringComparison.Ordinal);
        var hop = harness.Idp.Authorizations[^1];
        Assert.Equal(clientId, hop.ClientId);
        return hop.Outcome;
    }
}
