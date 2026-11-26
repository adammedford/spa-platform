using System.Collections.Immutable;
using System.Net;
using MockApi;

namespace Fixtures.Tests;

/// <summary>
/// The mock API's <c>/pad</c> route (#1545): padding response headers of a size the caller chooses,
/// so G9's O9 can find the largest single response header and the largest whole response header
/// the ALB and each browser accept (#1530, #961 O9). Called directly, with no gateway, as the rest
/// of the mock API's tests are; the gateway reaches it under a fragment's API route, which removes
/// its prefix (contracts/upstream-contract.md -> "Fragment routes").
/// </summary>
public sealed class MockApiPaddingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(8_192)]
    [InlineData(65_536)]
    [InlineData(1_048_576)]
    public async Task One_padding_header_is_exactly_the_requested_size(int bytes)
    {
        // 1 MiB is the route's upper bound, so the bound is inclusive.
        await using MockApiHarness harness = await MockApiHarness.StartAsync();

        HttpResponseMessage response = await harness.CallOwnAppAsync("orders", $"/pad?bytes={bytes}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["X-Pad-1"], PaddingNames(response));
        Assert.Equal(new string('x', bytes), Assert.Single(response.Headers.GetValues("X-Pad-1")));
    }

    [Theory]
    [InlineData(1_024, 16)]
    [InlineData(1_024, 1_024)]
    public async Task Count_answers_that_many_padding_headers_with_distinct_names(int bytes, int count)
    {
        // O9 needs many smaller headers as well as one large one: the ALB limits a single header
        // apart from the whole response header. 1,024 x 1,024 is both bounds at once.
        await using MockApiHarness harness = await MockApiHarness.StartAsync();

        HttpResponseMessage response = await harness.CallOwnAppAsync("reports", $"/pad?bytes={bytes}&count={count}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string[] names = PaddingNames(response);
        Assert.Equal(count, names.Length);
        Assert.Equal(count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, name => Assert.Equal(bytes, Assert.Single(response.Headers.GetValues(name)).Length));
        Assert.All(names, name => Assert.Equal(new string('x', bytes), response.Headers.GetValues(name).Single()));
    }

    [Theory]
    [InlineData("/pad")]
    [InlineData("/pad?count=2")]
    [InlineData("/pad?bytes=")]
    [InlineData("/pad?bytes=abc")]
    [InlineData("/pad?bytes=1.5")]
    [InlineData("/pad?bytes=-1")]
    [InlineData("/pad?bytes=+1")]
    [InlineData("/pad?bytes=%201")]
    [InlineData("/pad?bytes=0")]
    [InlineData("/pad?bytes=1048577")]
    [InlineData("/pad?bytes=99999999999")]
    [InlineData("/pad?bytes=1&bytes=2")]
    [InlineData("/pad?bytes=1&count=")]
    [InlineData("/pad?bytes=1&count=abc")]
    [InlineData("/pad?bytes=1&count=0")]
    [InlineData("/pad?bytes=1&count=1025")]
    [InlineData("/pad?bytes=1&count=1&count=2")]
    [InlineData("/pad?bytes=1025&count=1024")]
    [InlineData("/pad?bytes=1048576&count=2")]
    [InlineData("/pad?bytes=2097152&count=1024")]
    public async Task A_missing_malformed_or_out_of_range_size_or_count_answers_400(string path)
    {
        // N is 1 to 1 MiB, M is 1 to 1,024 and N x M is at most 1 MiB. The last three rows break
        // only the total; the last is 2^31, which an int product would wrap to a negative total.
        await using MockApiHarness harness = await MockApiHarness.StartAsync();

        HttpResponseMessage response = await harness.CallOwnAppAsync("orders", path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(PaddingNames(response));
    }

    [Fact]
    public async Task An_unauthenticated_call_answers_401_before_its_query_is_read()
    {
        // As on /whoami: an unauthenticated caller learns nothing, not even that its query is bad.
        await using MockApiHarness harness = await MockApiHarness.StartAsync();

        HttpResponseMessage good = await harness.CallAsync("orders", "/pad?bytes=8", token: null);
        HttpResponseMessage bad = await harness.CallAsync("orders", "/pad?bytes=abc", token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, good.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Empty(PaddingNames(good));
    }

    [Fact]
    public async Task An_unadmitted_clients_call_answers_403()
    {
        // As on /whoami (N-17's half one): app 1's token at the reports API.
        await using MockApiHarness harness = await MockApiHarness.StartAsync();

        HttpResponseMessage response = await harness.CallAsync("reports", "/pad?bytes=8", await harness.App1TokenAsync());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("client_not_admitted", (await MockApiHarness.BodyOf(response)).GetProperty("error").GetString());
        Assert.Empty(PaddingNames(response));
    }

    [Fact]
    public async Task A_call_without_the_read_scope_answers_403()
    {
        await using MockApiHarness harness = await MockApiHarness.StartAsync();

        HttpResponseMessage response = await harness.CallAsync(
            "orders", "/pad?bytes=8", await harness.App1TokenAsync(scope: "openid profile"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("insufficient_scope", (await MockApiHarness.BodyOf(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Admission_is_judged_on_the_pad_route()
    {
        // Admission by route (#998): a listener admitting client 1 on /pad alone serves it there and
        // refuses it on /whoami, so a fragment's client can be admitted on /pad and nowhere else.
        await using MockApiHarness harness = await MockApiHarness.StartAsync(
        [
            new ApiListenerOptions("orders", Port: 0, ScopeIdentifier: MockApiHarness.Orders,
                Audience: MockApiHarness.Orders, AdmittedClients: [])
            {
                RouteAdmissions = ImmutableDictionary<string, ImmutableArray<string>>.Empty
                    .Add("/pad", [IdpHarness.Client1]),
            },
        ]);
        string token = await harness.App1TokenAsync();

        HttpResponseMessage pad = await harness.CallAsync("orders", "/pad?bytes=8", token);
        HttpResponseMessage whoami = await harness.CallAsync("orders", "/whoami", token);

        Assert.Equal(HttpStatusCode.OK, pad.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, whoami.StatusCode);
    }

    /// <summary>The padding headers' names, in the order they arrived.</summary>
    private static string[] PaddingNames(HttpResponseMessage response) =>
        [.. response.Headers
            .Select(header => header.Key)
            .Where(name => name.StartsWith("X-Pad-", StringComparison.OrdinalIgnoreCase))];
}
