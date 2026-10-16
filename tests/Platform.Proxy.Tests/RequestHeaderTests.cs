using System.Collections.Immutable;
using System.Net;
using Platform.Testing;

namespace Platform.Proxy.Tests;

/// <summary>
/// N-11 and T-C3: what an upstream receives. None of the browser's <c>Cookie</c>, <c>Authorization</c>,
/// <c>X-CSRF-TOKEN</c> or forwarding headers; the session's token; and exactly the three forwarding
/// headers the gateway sets (<c>contracts/upstream-contract.md</c>).
///
/// T-C3 (with T-C5 and T-C22) is why a list of names is not enough: YARP's defaults replace
/// <c>X-Forwarded-For</c>, <c>-Host</c>, <c>-Proto</c> and <c>-Prefix</c> and drop <c>Forwarded</c>,
/// and every other forwarding header goes upstream as the browser sent it. So the names below include
/// some no list would name.
///
/// Over a real loopback listener, because a header is only wrong once it is on a wire.
/// </summary>
public sealed class RequestHeaderTests
{
    private const string Host = "app1.spa-poc.test";

    /// <summary>
    /// Every header the browser may not send upstream, with a value that would be recognisable if it
    /// arrived. The last few are names no fixed list carries: only the prefix rule removes them.
    /// </summary>
    public static TheoryData<string> Forbidden =>
    [
        "Cookie",
        "Authorization",
        "X-CSRF-TOKEN",
        "Forwarded",
        "Via",
        "X-Real-IP",
        "X-Forwarded-For",
        "X-Forwarded-Proto",
        "X-Forwarded-Host",
        "X-Forwarded-Prefix",
        "X-Forwarded-Port",
        "X-Forwarded-Server",
        "x-forwarded-ssl",
        "X-Forwarded-Client-Cert",
        "X-Original-For",
        "X-Original-Proto",
        "X-Original-Host",
        "X-Original-URL",
        "x-original-method",
    ];

    private static readonly ImmutableArray<(string Name, string Value)> Planted =
    [
        ("Cookie", "__Host-spa-session=planted-cookie"),
        ("Authorization", "Bearer planted-browser-token"),
        ("X-CSRF-TOKEN", "planted-csrf"),
        ("Forwarded", "for=203.0.113.9;host=evil.test;proto=https"),
        ("Via", "1.1 planted-via"),
        ("X-Real-IP", "203.0.113.9"),
        ("X-Forwarded-For", "203.0.113.9"),
        ("X-Forwarded-Proto", "https"),
        ("X-Forwarded-Host", "evil.test"),
        ("X-Forwarded-Prefix", "/planted"),
        ("X-Forwarded-Port", "8443"),
        ("X-Forwarded-Server", "planted-server"),
        ("x-forwarded-ssl", "on"),
        ("X-Forwarded-Client-Cert", "planted-cert"),
        ("X-Original-For", "203.0.113.9"),
        ("X-Original-Proto", "https"),
        ("X-Original-Host", "evil.test"),
        ("X-Original-URL", "/planted"),
        ("x-original-method", "DELETE"),
        ("X-Request-Id", "control-header"),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Forbidden))]
    public async Task The_browsers_header_does_not_reach_the_upstream(string name)
    {
        EchoedRequest received = await SendPlanted();

        string planted = Planted.Single(header => header.Name == name).Value;
        string[] values = received.Headers.TryGetValue(name, out ImmutableArray<string> arrived) ? [.. arrived] : [];
        Assert.DoesNotContain(values, value => value.Contains(planted, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_upstream_sees_the_sessions_token_and_the_three_forwarding_headers_the_gateway_set()
    {
        EchoedRequest received = await SendPlanted();

        Assert.Equal(["Bearer " + ProxyHost.DefaultToken], received.Headers["Authorization"]);
        Assert.Equal(["127.0.0.1"], received.Headers["X-Forwarded-For"]);
        Assert.Equal(["http"], received.Headers["X-Forwarded-Proto"]);
        Assert.Equal([Host], received.Headers["X-Forwarded-Host"]);

        // "An API trusts only the three headers the gateway sets": no fourth arrives, by any name.
        string[] forwarding = [.. received.Headers.Keys
            .Where(name => name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Via", StringComparison.OrdinalIgnoreCase)
                || name.Equals("X-Real-IP", StringComparison.OrdinalIgnoreCase))
            .Select(name => name.ToUpperInvariant())
            .Order(StringComparer.Ordinal)];
        Assert.Equal(["X-FORWARDED-FOR", "X-FORWARDED-HOST", "X-FORWARDED-PROTO"], forwarding);
    }

    [Fact]
    public async Task A_header_the_contract_does_not_name_still_reaches_the_upstream()
    {
        // The control: a stripping rule that removed everything would pass every test above.
        EchoedRequest received = await SendPlanted();

        Assert.Equal(["control-header"], received.Headers["X-Request-Id"]);
    }

    private static async Task<EchoedRequest> SendPlanted()
    {
        EchoUpstream orders = new();
        await using EchoUpstreamListener listener = await orders.ListenAsync(Ct);
        await using ProxyHost gateway = await ProxyHost.StartAsync(
            Ct, TestApps.Entry("app1", apis: [TestApps.Api("orders", listener.BaseAddress.AbsoluteUri)]));
        using HttpClient client = new(new HttpClientHandler { UseCookies = false });
        using HttpRequestMessage request = gateway.Request(HttpMethod.Get, Host, "/api/orders/items");
        foreach ((string name, string value) in Planted)
        {
            Assert.True(request.Headers.TryAddWithoutValidation(name, value), name);
        }

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(orders.Requests);
    }
}
