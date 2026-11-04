using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// Workstream 3's half of the key-ring proof (change request P2-2, decision 7; ADR-0025; #342): the
/// session cookie and the login state one identity gateway issues are read by another exactly when the
/// two keep their key ring in one repository. The second gateway is started beside the first, so it
/// asks the first's provider and reads the first's session store, as two tasks of one cell share the
/// provider and the DynamoDB table; the repository is the only thing a test chooses to share or not.
/// </summary>
public sealed class KeyRingContinuityTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);

    [Fact]
    public async Task A_gateway_keeps_its_key_ring_in_a_repository_of_its_own()
    {
        // Mutation that turns it red: drop the KeyManagementOptions line from IdentityGateway.StartAsync.
        // The framework then keeps the ring in its default repository, a directory under the user's
        // profile, and the options name no repository of this gateway's.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);

        await gateway.LoginAsync(App1);

        Assert.Same(gateway.Keys, gateway.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);
        Assert.Single(gateway.Keys.GetAllElements());
    }

    [Fact]
    public async Task A_session_cookie_issued_by_one_gateway_is_accepted_by_another_over_the_same_repository()
    {
        // Mutation that turns it red: start the second gateway with keys: new ListXmlRepository(), or
        // have StartAsync ignore its keys argument. The second then cannot unprotect the cookie and
        // answers 401 unauthenticated, which is what the two-repository control below asserts.
        ListXmlRepository ring = new();
        await using IdentityGateway first = await IdentityGateway.StartAsync([App1], keys: ring);
        IdentityGateway.Login login = await first.LoginAsync(App1);
        await using IdentityGateway second = await IdentityGateway.StartAsync([App1], keys: ring, beside: first);

        IdentityGateway.Answer session = await second.SendAsync(App1, "/bff/session", login.Jar, headers: IdentityGateway.Fetch);

        Assert.Equal(200, session.Status);
        Assert.Equal(first.Idp.DefaultSubject, session.Subject);
        Assert.Single(ring.GetAllElements());
    }

    [Fact]
    public async Task A_login_started_on_one_gateway_completes_on_another_over_the_same_repository()
    {
        // Mutation that turns it red: start the second gateway with keys: new ListXmlRepository(), or
        // have StartAsync ignore its keys argument. The second then cannot read the state or the
        // correlation cookie and answers the failure page, as the two-repository control asserts.
        ListXmlRepository ring = new();
        await using IdentityGateway first = await IdentityGateway.StartAsync([App1], keys: ring);
        IdentityGateway.Authorized started = await first.AuthorizeAsync(App1);
        await using IdentityGateway second = await IdentityGateway.StartAsync([App1], keys: ring, beside: first);

        IdentityGateway.Answer callback = await second.SendAsync(App1, started.Callback.PathAndQuery, started.LoginState);

        Assert.Equal(302, callback.Status);
        Assert.Single(first.Sessions.Created);
        IdentityGateway.Answer session = await second.SendAsync(
            App1, "/bff/session", new IdentityGateway.Login(started.Challenge, callback).Jar, headers: IdentityGateway.Fetch);
        Assert.Equal(200, session.Status);
    }

    [Fact]
    public async Task Over_two_repositories_both_the_cookie_and_the_login_state_are_refused()
    {
        // The control, and the case that lets the two positive assertions above fail: everything else
        // the second gateway shares with the first is the same as there, so what refuses here is the
        // key ring alone. Mutation that turns it red: start the second gateway with keys: ring, the
        // first's repository, or let both gateways fall back to the framework's default ring, which two
        // hosts in one process share under the user's profile (change request P2-2, section 4).
        ListXmlRepository ring = new();
        await using IdentityGateway first = await IdentityGateway.StartAsync([App1], keys: ring);
        IdentityGateway.Login login = await first.LoginAsync(App1);
        IdentityGateway.Authorized started = await first.AuthorizeAsync(App1);
        await using IdentityGateway second = await IdentityGateway.StartAsync(
            [App1], RemoteFailureRecorder.Install(out RemoteFailureRecorder recorder), keys: new ListXmlRepository(), beside: first);

        // The cookie: what the contract gives a request with no session (contracts/http-contract.md -> Errors).
        IdentityGateway.Answer session = await second.SendAsync(App1, "/bff/session", login.Jar, headers: IdentityGateway.Fetch);
        Assert.Equal(401, session.Status);
        Assert.Equal("", session.Location);
        Assert.Equal("application/problem+json", session.ContentType);
        Assert.Equal("unauthenticated", System.Text.Json.JsonDocument.Parse(session.Body).RootElement.GetProperty("code").GetString());

        // The login state: the one static failure page a refused callback gets, and no session.
        int sessionsBefore = first.Sessions.Created.Count;
        IdentityGateway.Answer callback = await second.SendAsync(App1, started.Callback.PathAndQuery, started.LoginState);
        Assert.Equal((400, AuthenticationEvents.FailurePage), (callback.Status, callback.Body));
        Assert.Contains("state", Assert.Single(recorder.Failures).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sessionsBefore, first.Sessions.Created.Count);
    }
}
