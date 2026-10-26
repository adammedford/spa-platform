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

    [Fact]
    public async Task A_code_exchange_slower_than_the_provider_deadline_fails_the_login_at_the_deadline()
    {
        // The provider answers every token request after 3 s; the deadline is 500 ms. The handler's
        // BackchannelTimeout reads the deadline, and the shared backchannel's own timeout is what applies
        // it, so the callback fails on the failure page well before the provider answers, and no session
        // is made. Mutation: leave the shared backchannel's 60 s timeout (set only BackchannelTimeout,
        // which the handler ignores for a backchannel it did not build); the exchange waits the 3 s, the
        // login succeeds, and this fails.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1],
            services: s => s.AddSingleton(new AccessTokenSourceOptions { ProviderDeadline = TimeSpan.FromMilliseconds(500) }));
        IdentityGateway.Authorized authorized = await gateway.AuthorizeAsync(App1);
        gateway.Idp.Faults.TokenDelay = TimeSpan.FromSeconds(3);

        long started = TimeProvider.System.GetTimestamp();
        IdentityGateway.Answer callback = await gateway.SendAsync(App1, authorized.Callback.PathAndQuery, authorized.LoginState);
        TimeSpan took = TimeProvider.System.GetElapsedTime(started);

        Assert.Equal((400, AuthenticationEvents.FailurePage), (callback.Status, callback.Body));
        Assert.True(took < TimeSpan.FromSeconds(2.5), $"the callback took {took}");
        Assert.Empty(gateway.Sessions.Created);
        gateway.Pin(App1);
        OpenIdConnectOptions options = gateway.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(SchemeNames.Oidc(App1.AppId));
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.BackchannelTimeout);
        Assert.Equal(options.BackchannelTimeout, options.Backchannel.Timeout);
    }
}
