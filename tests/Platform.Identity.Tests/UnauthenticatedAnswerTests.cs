using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// The two 401s Platform.Identity writes, <c>GET /bff/session</c>'s and the OpenID Connect events' to a
/// fetch the shell gate challenged, each in its two cases (#163). A request that presented no cookie
/// gets an answer and no rule id; one whose cookie resolved to no session is a rejection under
/// <c>auth.session</c>, and the body is the same writer's. The body is pinned whole, status field
/// included, from contracts/http-contract.md -> Errors (<c>401 | unauthenticated</c>), because these
/// writers moved from a copy that took the status as an argument to the one that reads it off the
/// table.
/// </summary>
public sealed class UnauthenticatedAnswerTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);

    private const string Body = "{\"status\":401,\"code\":\"unauthenticated\"}";

    [Theory]
    [InlineData("/bff/session")]
    [InlineData("/assets/app.js")]
    public async Task Without_a_cookie_the_401_is_an_answer_with_no_rule_id(string path)
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);

        IdentityGateway.Answer answer = await gateway.SendAsync(App1, path, headers: IdentityGateway.Fetch);

        Assert.Equal((401, "application/problem+json", Body), (answer.Status, answer.ContentType, answer.Body));
        Assert.Null(answer.Rule);
    }

    [Theory]
    [InlineData("/bff/session")]
    [InlineData("/assets/app.js")]
    public async Task With_a_cookie_for_no_session_the_401_is_a_rejection_under_auth_session(string path)
    {
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        (string key, _) = await gateway.Sessions.SingleAsync();
        await gateway.Sessions.RemoveAsync(key, TestContext.Current.CancellationToken);

        IdentityGateway.Answer answer = await gateway.SendAsync(App1, path, login.Jar, headers: IdentityGateway.Fetch);

        Assert.Equal((401, "application/problem+json", Body), (answer.Status, answer.ContentType, answer.Body));
        Assert.Equal("auth.session", answer.Rule);
    }
}
