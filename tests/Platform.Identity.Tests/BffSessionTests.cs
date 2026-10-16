using System.Text.Json;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// <c>GET /bff/session</c> (task 3.9, contracts/http-contract.md -> Endpoints): who is signed in, until
/// when, and the antiforgery request token, and never a token of the provider's. N-19's
/// <c>/bff/session</c> half, and N-23 for this endpoint.
/// </summary>
public sealed class BffSessionTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);

    [Fact]
    public async Task Without_a_session_it_answers_401_with_a_problem_body_and_never_a_redirect()
    {
        // N-19.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);

        IdentityGateway.Answer session = await gateway.SendAsync(App1, "/bff/session", headers: IdentityGateway.Fetch);

        Assert.Equal(401, session.Status);
        Assert.Equal("", session.Location);
        Assert.Equal("application/problem+json", session.ContentType);
        Assert.Equal("unauthenticated", JsonDocument.Parse(session.Body).RootElement.GetProperty("code").GetString());
        Assert.Empty(gateway.Idp.Authorizations);
        Assert.Null(session.Rule);
    }

    [Fact]
    public async Task Without_a_session_a_navigation_to_it_is_not_redirected_either()
    {
        // contracts/http-contract.md -> Errors: "/api and /bff/session never answer with a redirect."
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);

        IdentityGateway.Answer session = await gateway.SendAsync(App1, "/bff/session", headers: IdentityGateway.Navigation);

        Assert.Equal(401, session.Status);
        Assert.Equal("", session.Location);
        Assert.Equal("application/problem+json", session.ContentType);
    }

    [Fact]
    public async Task With_a_session_it_answers_the_user_the_session_expiry_and_the_csrf_token()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        (_, SessionRecord record) = await gateway.Sessions.SingleAsync();

        IdentityGateway.Answer session = await gateway.SendAsync(App1, "/bff/session", login.Jar, headers: IdentityGateway.Fetch);

        Assert.Equal(200, session.Status);
        Assert.StartsWith("application/json", session.ContentType, StringComparison.Ordinal);
        JsonElement body = JsonDocument.Parse(session.Body).RootElement;
        Assert.Equal(
            ["csrfToken", "email", "expiresAt", "name", "sub"],
            body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(gateway.Idp.DefaultSubject, body.GetProperty("sub").GetString());
        Assert.Equal(gateway.Idp.DefaultSubject + " (display)", body.GetProperty("name").GetString());
        Assert.Equal(gateway.Idp.DefaultSubject + "@spa-poc.test", body.GetProperty("email").GetString());

        // The session's expiry, from the record, not the access token's (contracts/http-contract.md).
        Assert.Equal(record.ExpiresAt, body.GetProperty("expiresAt").GetDateTimeOffset());

        // The request token the antiforgery service issued for this app, with its cookie set beside it.
        Assert.Equal("csrf-for-app1", body.GetProperty("csrfToken").GetString());
        Assert.Contains(session.Cookies, c => c.Name == FakeCsrfTokens.CookieName && c.Value == "csrf-for-app1");
        Assert.Equal("no-store", session.CacheControl);
    }

    [Fact]
    public async Task Its_body_holds_no_token_the_provider_issued_and_nothing_token_shaped()
    {
        // N-23 for /bff/session.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        IdentityGateway.Login login = await gateway.LoginAsync(App1);

        IdentityGateway.Answer session = await gateway.SendAsync(App1, "/bff/session", login.Jar, headers: IdentityGateway.Fetch);

        Assert.Equal(200, session.Status);
        TokenSearch.AssertNoTokens(gateway.Idp, session.Body);
    }
}
