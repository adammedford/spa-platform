using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Abstractions;

namespace Platform.Identity.Tests;

/// <summary>
/// #332 F1: the OpenID Connect handler's backchannel is bounded by the token source's provider deadline,
/// so a login's code exchange is bounded by the budget a refresh is, inside the shutdown timeout.
/// </summary>
public sealed class BackchannelTimeoutTests
{
    private static readonly AppDefinition App1 = RefreshLeaseTests.App1;

    /// <summary>
    /// The deadline under test. The backchannel's timeout is this figure from the first request it sends,
    /// so it also bounds the discovery and key-set reads of the login before the exchange (#461): it is
    /// sized for a cold read on a slow runner, not for speed. The test takes about this long.
    /// </summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task A_code_exchange_slower_than_the_provider_deadline_fails_the_login_at_the_deadline()
    {
        // The provider holds the token request until the test releases it, or for its 30 s backstop;
        // the deadline is 2 s. The handler's BackchannelTimeout reads the deadline, and the shared
        // backchannel's own timeout is what applies it, so the callback fails on the failure page while
        // the provider still holds the exchange, and no session is made. Mutation: leave the shared
        // backchannel's 60 s timeout (set only BackchannelTimeout, which the handler ignores for a
        // backchannel it did not build); the exchange waits out the backstop, the login succeeds, and
        // this fails.
        HeldClock held = new();
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1],
            services: s => s.AddSingleton(new AccessTokenSourceOptions { ProviderDeadline = Deadline }),
            idpClock: held);
        IdentityGateway.Authorized authorized = await gateway.AuthorizeAsync(App1);
        gateway.Idp.Faults.TokenDelay = TimeSpan.FromSeconds(1);

        long started = TimeProvider.System.GetTimestamp();
        IdentityGateway.Answer callback = await gateway.SendAsync(App1, authorized.Callback.PathAndQuery, authorized.LoginState);
        TimeSpan took = TimeProvider.System.GetElapsedTime(started);
        bool exchangeHeld = held.Holding.IsCompleted;
        held.Release();

        Assert.True(exchangeHeld, "the provider never held the exchange");
        Assert.Equal((400, AuthenticationEvents.FailurePage), (callback.Status, callback.Body));
        Assert.True(took < TimeSpan.FromSeconds(15), $"the callback took {took}");
        Assert.Empty(gateway.Sessions.Created);
        gateway.Pin(App1);
        OpenIdConnectOptions options = gateway.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(SchemeNames.Oidc(App1.AppId));
        Assert.Equal(Deadline, options.BackchannelTimeout);
        Assert.Equal(options.BackchannelTimeout, options.Backchannel.Timeout);
    }
}
