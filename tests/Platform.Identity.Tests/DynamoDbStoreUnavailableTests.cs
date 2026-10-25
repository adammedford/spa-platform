using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// F17 (#332): the session table unreachable. The identity chain runs behind a real Kestrel host
/// (<see cref="StageHost"/>) over the DynamoDB store, so what the request ends on is what the server itself
/// answers when the chain throws: the gateway's generic 500, with no cookie set or cleared, and one Error
/// line, the server's, naming the store's operation. A <c>503 session_store_unavailable</c> row is a
/// next-gate contract item, so this pins today's 500.
/// </summary>
public sealed class DynamoDbStoreUnavailableTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);

    [Fact]
    public async Task An_unreachable_table_ends_a_session_request_on_the_generic_500_with_one_Error_line_and_the_cookie_intact()
    {
        // Mutation: read the store's failure as no session (GetAsync answering null), and the request is
        // answered as anonymous rather than 500; log the failure in the store as well, and there are two
        // Error lines. The store logs into the same capture as the gateway, so a line of its own is seen.
        await using FakeDynamoDbServer fake = await FakeDynamoDbServer.StartAsync(Ct);
        CapturedLog identityLog = new();
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1],
            services => services
                .AddSingleton(identityLog.Provider)
                .AddSingleton<ISessionStore>(new DynamoDbSessionStore(
                    DynamoDbTables.Over(fake, TimeProvider.System, identityLog.CreateLogger<DynamoDbSessionStore>()))));
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        Assert.Equal(200, (await gateway.SendAsync(App1, IdentityGateway.ApiPath, login.Jar, headers: IdentityGateway.Fetch)).Status);
        string pk = Assert.Single(fake.Items.Keys);

        await using StageHost server = await StageHost.StartAsync(
            new StageHostOptions { Pipeline = app => app.Run(context => gateway.ServeAsync(context, App1)) },
            Ct);
        await fake.StopAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, IdentityGateway.ApiPath);
        request.Headers.Add("Cookie", string.Join("; ", login.Jar.Select(c => $"{c.Key}={c.Value}")));
        foreach ((string name, string value) in IdentityGateway.Fetch)
        {
            request.Headers.Add(name, value);
        }

        using HttpResponseMessage answer = await server.Client.SendAsync(request, Ct);

        Assert.Equal(500, (int)answer.StatusCode);
        Assert.False(answer.Headers.Contains("Set-Cookie"));
        CapturedLine error = Assert.Single([.. server.Log.Lines, .. identityLog.Lines], line => line.Level >= LogLevel.Error);
        Assert.Contains("read (GetItem)", error.Text, StringComparison.Ordinal);
        Assert.True(fake.Items.ContainsKey(pk));

        // N-16: the line names the operation and not the session it was for.
        string cookie = login.Jar.Values.Single();
        Assert.DoesNotContain(cookie, error.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(pk, error.Text, StringComparison.Ordinal);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
