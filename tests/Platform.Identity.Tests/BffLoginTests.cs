using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// <c>GET /bff/login?returnUrl=/path</c> (task 3.9, contracts/http-contract.md -> Endpoints): discards
/// any session, then redirects to the provider; a return path that is not local becomes <c>/</c>.
/// F-02 and N-15, and N-23 for this endpoint.
/// </summary>
public sealed class BffLoginTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);
    private static readonly AppDefinition App2 = TestApps.Entry("app2", issuer: IdentityGateway.IssuerName);

    [Theory]
    [InlineData("app1")]
    [InlineData("app2")]
    public async Task Each_app_redirects_to_the_provider_with_its_own_client_and_returns_to_its_own_callback(string appId)
    {
        // F-02.
        AppDefinition app = appId == "app1" ? App1 : App2;
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);

        IdentityGateway.Login login = await gateway.LoginAsync(app, start: "/bff/login?returnUrl=/reports");

        Assert.Equal(302, login.Challenge.Status);
        Assert.Equal(gateway.Idp.AuthorizeEndpoint, new Uri(login.Challenge.Location).GetLeftPart(UriPartial.Path));
        Assert.Equal(app.OAuth.ClientId, login.Challenge.RedirectQuery["client_id"]);
        Assert.Equal(IdentityGateway.RedirectUriOf(app), login.Challenge.RedirectQuery["redirect_uri"]);

        Assert.Equal(302, login.Callback.Status);
        Assert.Equal("/reports", login.Callback.Location);
        (_, SessionRecord record) = await gateway.Sessions.SingleAsync();
        Assert.Equal(app.AppId, record.AppId);
        Assert.Equal(app.OAuth.ClientId, record.ClientId);
    }

    [Theory]
    [InlineData("//evil.test")]
    [InlineData("https://evil.test")]
    [InlineData("/\\evil.test")]
    [InlineData("\\\\evil.test")]
    [InlineData("%2F%2Fevil.test")]
    [InlineData("evil.test")]
    [InlineData("")]
    public async Task A_return_url_that_is_not_a_local_path_becomes_the_root(string returnUrl)
    {
        // N-15. Each value goes on the query encoded, so what the endpoint reads is the value itself;
        // %2F%2Fevil.test is what a doubly encoded //evil.test reads as, which is not a path at all.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);

        IdentityGateway.Login login = await gateway.LoginAsync(App1, start: "/bff/login?returnUrl=" + Uri.EscapeDataString(returnUrl));

        Assert.Equal(302, login.Callback.Status);
        Assert.Equal("/", login.Callback.Location);
    }

    [Fact]
    public async Task An_encoded_backslash_stays_encoded_and_names_a_path_on_the_apps_own_host()
    {
        // A browser does not decode %5C in a Location header before it reads the authority, so this is
        // a path, not //evil.test.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);

        IdentityGateway.Login login = await gateway.LoginAsync(App1, start: "/bff/login?returnUrl=" + Uri.EscapeDataString("/%5Cevil.test"));

        Assert.Equal("/%5Cevil.test", login.Callback.Location);
        Assert.Equal(App1.Host, new Uri(new Uri($"https://{App1.Host}/"), login.Callback.Location).Host);
    }

    [Fact]
    public async Task Two_return_urls_are_not_a_local_path()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);

        IdentityGateway.Login login = await gateway.LoginAsync(App1, start: "/bff/login?returnUrl=/a&returnUrl=//evil.test");

        Assert.Equal("/", login.Callback.Location);
    }

    [Fact]
    public async Task Without_a_return_url_the_login_returns_to_the_root()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);

        IdentityGateway.Login login = await gateway.LoginAsync(App1, start: "/bff/login");

        Assert.Equal("/", login.Callback.Location);
    }

    [Fact]
    public async Task A_login_discards_the_session_the_browser_already_has()
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        IdentityGateway.Login first = await gateway.LoginAsync(App1);
        (string key, _) = await gateway.Sessions.SingleAsync();

        IdentityGateway.Answer login = await gateway.SendAsync(App1, "/bff/login?returnUrl=/", first.Jar, headers: IdentityGateway.Navigation);

        Assert.Equal(302, login.Status);
        Assert.StartsWith(gateway.Idp.AuthorizeEndpoint, login.Location, StringComparison.Ordinal);
        Assert.Null(await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));
        Assert.Contains(login.Cookies, c =>
            c.Name == first.Callback.SessionCookie.Name && c.Expires is { } expires && expires < DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Its_response_bodies_hold_no_token()
    {
        // N-23 for /bff/login: the challenge and the callback it leads to.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);

        IdentityGateway.Login login = await gateway.LoginAsync(App1, start: "/bff/login?returnUrl=/");

        TokenSearch.AssertNoTokens(gateway.Idp, login.Challenge.Body + login.Callback.Body);
    }
}
