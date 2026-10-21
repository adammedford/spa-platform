using System.Net.Http;
using System.Security.Authentication;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The deployed tier's raw HTTP client (docs/plan/04-tests.md -> "How the suites run"), as the cell
/// uses it through the tunnel (#211): proven against the local stand-in load balancer, the only TLS
/// listener a test can stand up that answers as the cell does.
/// </summary>
public sealed class RawClientTests
{
    [Fact]
    public async Task A_request_goes_to_the_endpoint_whatever_its_host_and_carries_only_the_headers_given()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        HarnessApp app = stack.App("app1");
        using HttpClient client = RawClient.To(stack.LoadBalancer.Endpoint, TlsLoadBalancer.TrustAnyCertificate);

        using HttpRequestMessage request = new(HttpMethod.Get, app.Url("/bff/session"));
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        using HttpResponseMessage answer = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(Contract.Error("unauthenticated").Status, (int)answer.StatusCode);
        Exchange sent = Assert.Single(stack.LoadBalancer.Exchanges);
        Assert.Equal(app.Host, sent.Sent("Host"));
        Assert.Equal("same-origin", sent.Sent("Sec-Fetch-Site"));
        Assert.Null(sent.Sent("Cookie"));
    }

    [Fact]
    public async Task Without_a_validation_override_a_certificate_the_machine_does_not_trust_is_refused()
    {
        // The cell's client validates as the workstation does, so a run through anything but the
        // authority certs.sh installed fails rather than passing on a listener it never checked.
        await using LocalStack stack = await LocalStack.StartAsync();
        using HttpClient client = RawClient.To(stack.LoadBalancer.Endpoint);

        using HttpRequestMessage request = new(HttpMethod.Get, stack.App("app1").Url("/healthz"));
        HttpRequestException refused = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, TestContext.Current.CancellationToken));
        Assert.IsType<AuthenticationException>(refused.InnerException);
        Assert.Empty(stack.LoadBalancer.Exchanges);
    }
}
