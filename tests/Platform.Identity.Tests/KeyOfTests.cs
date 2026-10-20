using Microsoft.AspNetCore.Authentication;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// <see cref="AppSessions.KeyOf"/>, the one read of the request's session key (#141, Q7), through the
/// framework's cookie handler: the condition it guards against, a ticket the handler read through the
/// ticket store and then refused, is one only the handler produces.
/// </summary>
public sealed class KeyOfTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);

    [Fact]
    public async Task A_ticket_the_cookie_handler_read_and_refused_as_expired_resolves_to_no_key()
    {
        // The cookie handler retrieves the ticket through the ticket store, which puts the key on the
        // request, and only then finds it expired, removes the session and fails authentication
        // (CookieAuthenticationHandler.ReadCookieTicket). The shell gate passes a request on only when
        // KeyOf answers a key. Mutation: have KeyOf read the channel without regard to the authenticate
        // result; it hands back the expired session's key, the shell gate serves the asset with 200, and
        // this fails.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        (string key, SessionRecord record) = await gateway.Sessions.SingleAsync();
        await ExpireTicketAsync(gateway, key, record);

        IdentityGateway.Answer asset = await gateway.SendAsync(App1, "/", login.Jar, headers: IdentityGateway.Navigation);

        Assert.Equal(302, asset.Status);
        Assert.StartsWith(gateway.Idp.AuthorizeEndpoint, asset.Location, StringComparison.Ordinal);
        Assert.Null(asset.Subject);
        // contracts/request-gate.md: auth.session, still correlated with the session the cookie named.
        Assert.Equal("auth.session", asset.Rule);
        Assert.Equal(SessionRef.Of(key), asset.SessionRef);
        // The handler's own removal of the expired ticket, through the ticket store's ungated remove.
        Assert.Null(await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Rewrites the stored ticket with an expiry in the past, leaving the record's own expiry alone, so
    /// the store still returns it and the cookie handler is the one that refuses it.
    /// </summary>
    private static async Task ExpireTicketAsync(IdentityGateway gateway, string key, SessionRecord record)
    {
        AuthenticationTicket ticket = TicketSerializer.Default.Deserialize(record.Ticket.ToArray())!;
        ticket.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        SessionRecord expired = record with { Version = record.Version + 1, Ticket = TicketSerializer.Default.Serialize(ticket) };
        Assert.True(await gateway.Sessions.ReplaceAsync(key, expired, record.Version, TestContext.Current.CancellationToken));
    }
}
