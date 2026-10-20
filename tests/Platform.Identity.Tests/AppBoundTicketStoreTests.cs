using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// <see cref="AppBoundTicketStore"/>, the cookie handler's <c>ITicketStore</c> (ADR-0004), called the way
/// the handler calls it, over <c>Platform.Testing</c>'s <see cref="InMemorySessionStore"/> behind the one
/// <see cref="AppSessions"/> the request's services hold.
/// </summary>
public sealed class AppBoundTicketStoreTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1");

    [Fact]
    public async Task The_renew_that_carries_the_request_refuses_and_writes_nothing()
    {
        // #141, Q11: nothing reaches this overload (the callback-blind cookie manager makes every sign-in
        // store a new key, and sliding expiry is off), and if anything did, it would write the ticket the
        // request started with under a version read fresh: an ungated stale write over a concurrent
        // refresh. Mutation: restore the renew's old body (read the owned record, replace it one version
        // on); no exception is thrown, the record is rewritten, and this fails.
        InMemorySessionStore store = new();
        HttpContext request = Request(store);
        AppBoundTicketStore tickets = new(App1);
        string key = await tickets.StoreAsync(Ticket("alice"), request, Ct);
        SessionRecord before = Assert.IsType<SessionRecord>(await store.GetAsync(key, Ct));

        await Assert.ThrowsAsync<NotSupportedException>(() => tickets.RenewAsync(key, Ticket("mallory"), Request(store), Ct));

        Assert.Equal(before, await store.GetAsync(key, Ct));
    }

    private static HttpContext Request(ISessionStore store)
    {
        ServiceCollection services = new();
        services.AddSingleton(new AppSessions(store));
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    /// <summary>A ticket as the cookie handler signs in with: the provider's <c>sub</c>, an issue time and an expiry.</summary>
    private static AuthenticationTicket Ticket(string subject) => new(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], SchemeNames.Cookie(App1.AppId))),
        new AuthenticationProperties { IssuedUtc = DateTimeOffset.UnixEpoch, ExpiresUtc = DateTimeOffset.UnixEpoch.AddHours(8) },
        SchemeNames.Cookie(App1.AppId));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
