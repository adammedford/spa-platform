using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// F17 (#332): the session table unreachable, over the DynamoDB store. The request ends under
/// <c>auth.store</c>, 503 <c>session_store_unavailable</c>, with no cookie set or cleared and one Error
/// line naming the store's operation (contracts/http-contract.md -> Errors; #727, which replaced the
/// generic 500 T4 pinned here before).
/// </summary>
public sealed class DynamoDbStoreUnavailableTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);

    [Fact]
    public async Task An_unreachable_table_ends_a_session_request_503_session_store_unavailable_with_one_Error_line_and_the_cookie_intact()
    {
        // Mutation: let the cookie handler's read throw, as before #727, and the request ends on an
        // exception rather than an answer; read the store's failure as no session (GetAsync answering null),
        // and it is answered 401 as anonymous; log the failure in the store as well, and there are two
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
        await fake.StopAsync();

        int before = identityLog.Lines.Count;
        IdentityGateway.Answer answer = await gateway.SendAsync(App1, IdentityGateway.ApiPath, login.Jar, headers: IdentityGateway.Fetch);

        Assert.Equal((503, "auth.store"), (answer.Status, answer.Rule));
        Assert.Equal("session_store_unavailable", JsonDocument.Parse(answer.Body).RootElement.GetProperty("code").GetString());
        Assert.Empty(answer.Cookies);
        CapturedLine error = Assert.Single(identityLog.Lines.Skip(before), line => line.Level >= LogLevel.Error);
        Assert.Equal(typeof(StoreFailure).FullName, error.Category);
        Assert.Contains("read (GetItem)", error.Text, StringComparison.Ordinal);
        Assert.True(fake.Items.ContainsKey(pk));

        // N-16: the line names the operation and not the session it was for.
        string cookie = login.Jar.Values.Single();
        Assert.DoesNotContain(cookie, error.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(pk, error.Text, StringComparison.Ordinal);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
