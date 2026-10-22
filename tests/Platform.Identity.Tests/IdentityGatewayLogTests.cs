using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// The capture every never-logged search (N-16) in this project reads: a <see cref="CapturedLog"/>
/// attached to <see cref="IdentityGateway"/> through its <c>Provider</c> (#151). The gateway's floor is
/// Trace, as the stage host's is (#140), because the OpenID Connect handler writes the callback URL,
/// code and state included, at Trace: a search at the framework's default floor, Information, would
/// pass without having seen that line.
/// </summary>
public sealed class IdentityGatewayLogTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);

    [Fact]
    public async Task A_capture_attached_to_the_gateway_sees_the_handlers_trace_lines_from_a_login()
    {
        // Mutation: leave the gateway's floor at Information, and no Trace line is captured.
        CapturedLog log = new();
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1], services => services.AddSingleton(log.Provider));

        await gateway.LoginAsync(App1);

        Assert.Contains(log.Lines, line =>
            line.Level == LogLevel.Trace && line.Category == typeof(OpenIdConnectHandler).FullName);
    }
}
