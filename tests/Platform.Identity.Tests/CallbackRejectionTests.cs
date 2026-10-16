using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// Login callbacks the gateway must refuse, through the real OpenID Connect handler and the test
/// identity provider, and the one thing every refusal has in common: no session (S-C4, R-B5).
///
/// A refused callback ends on task 3.5's failure page, which says nothing of the reason, so these
/// tests read the handler's reason from <see cref="RemoteFailureRecorder"/>.
/// </summary>
public sealed class CallbackRejectionTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);
    private static readonly AppDefinition App2 = TestApps.Entry("app2", issuer: IdentityGateway.IssuerName);

    [Fact]
    public async Task App1s_callback_replayed_on_app2_is_rejected_because_app2_cannot_read_the_state()
    {
        // S-C1, R-B1 and N-03: its code, its state and its login cookies, all presented to app 2.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1, App2], RemoteFailureRecorder.Install(out RemoteFailureRecorder recorder));
        IdentityGateway.Authorized app1 = await gateway.AuthorizeAsync(App1);

        Exception reason = await RefusedAsync(gateway, recorder, App2, app1.Callback.PathAndQuery, app1.LoginState);

        Assert.Contains("state", reason.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(gateway.Idp.Grants, g => g.GrantType == "authorization_code");
        Assert.Empty(gateway.Sessions.Created);
    }

    [Fact]
    public async Task A_code_issued_to_client1_injected_into_app2s_login_is_refused_at_the_token_endpoint()
    {
        // S-C2, R-B2 and N-04: app 2's own state and login cookies, app 1's code.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1, App2], RemoteFailureRecorder.Install(out RemoteFailureRecorder recorder));
        IdentityGateway.Authorized app1 = await gateway.AuthorizeAsync(App1);
        IdentityGateway.Authorized app2 = await gateway.AuthorizeAsync(App2);

        string injected = With(app2.Callback, "code", QueryHelpers.ParseQuery(app1.Callback.Query)["code"]!);
        Exception reason = await RefusedAsync(gateway, recorder, App2, injected, app2.LoginState);

        Assert.IsType<OpenIdConnectProtocolException>(reason);
        var grant = Assert.Single(gateway.Idp.Grants);
        Assert.Equal(("client-app2", "invalid_grant"), (grant.ClientId, grant.Outcome));
        Assert.Empty(gateway.Sessions.Created);
    }

    [Fact]
    public async Task A_callback_without_the_correlation_cookie_is_rejected()
    {
        // S-C3, R-B3 and N-05.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1, App2], RemoteFailureRecorder.Install(out RemoteFailureRecorder recorder));
        IdentityGateway.Authorized app1 = await gateway.AuthorizeAsync(App1);

        Exception reason = await RefusedAsync(gateway, recorder, App1, app1.Callback.PathAndQuery, Without(app1.LoginState, "__Host-spa-corr."));

        Assert.Contains("Correlation failed", reason.Message, StringComparison.Ordinal);
        Assert.Empty(gateway.Sessions.Created);
    }

    [Fact]
    public async Task A_callback_without_the_nonce_cookie_is_rejected_by_the_protocol_validator()
    {
        // R-B4 and N-05. The code is exchanged; the ID token's nonce then matches no cookie.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1, App2], RemoteFailureRecorder.Install(out RemoteFailureRecorder recorder));
        IdentityGateway.Authorized app1 = await gateway.AuthorizeAsync(App1);

        Exception reason = await RefusedAsync(gateway, recorder, App1, app1.Callback.PathAndQuery, Without(app1.LoginState, "__Host-spa-nonce."));

        Assert.IsType<OpenIdConnectProtocolInvalidNonceException>(reason);
        Assert.Empty(gateway.Sessions.Created);
    }

    [Fact]
    public async Task A_callback_with_tampered_state_is_rejected()
    {
        // N-05.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1, App2], RemoteFailureRecorder.Install(out RemoteFailureRecorder recorder));
        IdentityGateway.Authorized app1 = await gateway.AuthorizeAsync(App1);
        string state = QueryHelpers.ParseQuery(app1.Callback.Query)["state"]!;
        string tampered = state[..^4] + (state.EndsWith("AAAA", StringComparison.Ordinal) ? "BBBB" : "AAAA");

        Exception reason = await RefusedAsync(gateway, recorder, App1, With(app1.Callback, "state", tampered), app1.LoginState);

        Assert.Contains("state", reason.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(gateway.Idp.Grants, g => g.GrantType == "authorization_code");
        Assert.Empty(gateway.Sessions.Created);
    }

    [Fact]
    public async Task The_same_callback_with_everything_intact_creates_the_session()
    {
        // The control for the five refusals above: what they removed or swapped is what refused them.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1, App2], RemoteFailureRecorder.Install(out RemoteFailureRecorder recorder));
        IdentityGateway.Authorized app1 = await gateway.AuthorizeAsync(App1);

        IdentityGateway.Answer callback = await gateway.SendAsync(App1, app1.Callback.PathAndQuery, app1.LoginState);

        Assert.Equal(302, callback.Status);
        Assert.Single(gateway.Sessions.Created);
    }

    /// <summary>Sends the callback and returns the handler's reason for refusing it.</summary>
    private static async Task<Exception> RefusedAsync(
        IdentityGateway gateway,
        RemoteFailureRecorder recorder,
        AppDefinition app,
        string pathAndQuery,
        IReadOnlyDictionary<string, string> cookies)
    {
        IdentityGateway.Answer answer = await gateway.SendAsync(app, pathAndQuery, cookies);
        Assert.Equal((400, AuthenticationEvents.FailurePage), (answer.Status, answer.Body));
        return Assert.Single(recorder.Failures);
    }

    private static Dictionary<string, string> Without(IReadOnlyDictionary<string, string> cookies, string prefix) =>
        cookies.Where(c => !c.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);

    private static string With(Uri callback, string name, string value)
    {
        Dictionary<string, string?> query = QueryHelpers.ParseQuery(callback.Query)
            .ToDictionary(p => p.Key, p => (string?)p.Value.ToString(), StringComparer.Ordinal);
        query[name] = value;
        return QueryHelpers.AddQueryString(callback.AbsolutePath, query);
    }
}
