using System.Collections.Immutable;
using Gateway.TestHarness;
using Platform.Abstractions;
using Platform.Testing;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-09, whole: "Send <c>X-Forwarded-Proto</c> and <c>X-Forwarded-For</c> from an address outside
/// the known networks -> Ignored." (docs/plan/04-tests.md.) Threat T6, and ADR-0010's reason for
/// taking the scheme and the client address from the ALB subnets only.
///
/// <para><b>"Ignored" has to be read as a difference, not as an absence.</b> A request whose
/// forwarded headers were ignored looks exactly like a request that sent none, so each row here runs
/// the <i>same</i> request against two cells that differ only in
/// <see cref="GatewayHarnessOptions.TrustedNetworks"/>: one that trusts the sender and one that does
/// not. If the headers were honoured in both, or ignored in both, the pair fails — which a
/// one-sided test could not tell apart from a gateway that ignored every forwarded header, or
/// trusted every sender.</para>
///
/// <para><b>What makes the difference visible from outside.</b> Two things, and neither of them
/// reaches into a stage:</para>
/// <list type="bullet">
/// <item><b>The scheme</b>, through the request gate. The app origin an <c>Origin</c> header is
/// compared against is built from the scheme UsePlatformForwardedHeaders settled
/// (<c>contracts/request-gate.md</c>: "<c>Origin</c>, when sent, equals the app origin"), so a
/// browser claiming <c>https</c> is admitted by a cell that believes <c>X-Forwarded-Proto</c> and
/// refused with <see cref="RuleIds.GateOrigin"/> by one that does not. Asserted both ways round, so
/// the pair cannot pass by the gate refusing or admitting everything.</item>
/// <item><b>The client address</b>, through the upstream. The last entry of the
/// <c>X-Forwarded-For</c> an upstream receives is the gateway's own view of the caller, so a cell
/// that trusts the sender passes on the address the caller claimed and a cell that does not passes
/// on the address the socket actually came from.</item>
/// </list>
///
/// <para><b>Why the raw socket.</b> The sender's address is the input under test, so the request has
/// to come from a real connection — which is also why the harness runs the gateway on Kestrel
/// rather than on a test server. The stand-in load balancer the test browser uses would overwrite
/// <c>X-Forwarded-Proto</c> and append to <c>X-Forwarded-For</c>, as the ALB does, so it cannot send
/// the row's request either.</para>
/// </summary>
public sealed class ForwardedHeaderTests
{
    /// <summary>A client address no test machine has, so a cell that repeats it has been told it.</summary>
    private const string ClaimedAddress = "203.0.113.9";

    /// <summary>The address a loopback socket really comes from, which is what an untrusted cell must use.</summary>
    private const string SocketAddress = "127.0.0.1";

    /// <summary>
    /// A network the harness is not in, so the same request arrives from an untrusted sender.
    /// <see cref="GatewayHarnessOptions.TrustedNetworks"/> names N-09 as its reason for existing.
    /// </summary>
    private static readonly ImmutableArray<string> SomewhereElse = ["10.0.0.0/8"];

    [Fact]
    public async Task A_trusted_sender_has_its_forwarded_proto_believed()
    {
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);

        // 127.0.0.1/32 is the harness default, and a raw socket does come from there.
        Assert.Contains(SocketAddress + "/32", harness.Options.TrustedNetworks);

        // Claiming https, and sending the https origin that only a cell believing the claim will
        // accept. Admitted: so the header was read.
        RawResponse admitted = await Call(harness, "https");
        Assert.Equal(200, admitted.StatusCode);

        // And the other way round: the http origin, which is now the wrong one.
        int before = await Observed.SettledCountAsync(harness, Suite.Ct);
        RawResponse refused = await Call(harness, "https", origin: "http");
        Assert.Equal(403, refused.StatusCode);
        Assert.Equal(RuleIds.GateOrigin, await Observed.RuleOfOneRequestAsync(harness, before, Suite.Ct));
    }

    [Fact]
    public async Task An_untrusted_senders_forwarded_proto_is_ignored()
    {
        await using GatewayHarness harness = await Suite.StartAsync(
            new GatewayHarnessOptions { TrustedNetworks = SomewhereElse }, Suite.Ct);

        // The identical request the trusted cell admitted. Refused here, because the scheme stayed
        // http and the app origin with it, so the claimed header changed nothing.
        int before = await Observed.SettledCountAsync(harness, Suite.Ct);
        RawResponse refused = await Call(harness, "https");

        Contract.ErrorRow expected = Contract.Error("The request gate refused the request");
        Assert.Equal(expected.Status, refused.StatusCode);
        Assert.Contains($"\"code\":\"{expected.Code}\"", RawBody.Text(refused), StringComparison.Ordinal);
        Assert.Equal(RuleIds.GateOrigin, await Observed.RuleOfOneRequestAsync(harness, before, Suite.Ct));

        // And the http origin is admitted, which is what "ignored" means rather than "refused":
        // the gateway judged the request against the scheme of the connection it really had.
        RawResponse admitted = await Call(harness, "https", origin: "http");
        Assert.Equal(200, admitted.StatusCode);
    }

    [Fact]
    public async Task A_trusted_sender_has_its_forwarded_for_believed_and_passed_on()
    {
        await using GatewayHarness harness = await Suite.StartAsync(ct: Suite.Ct);

        EchoedRequest received = await CallAndReadUpstream(harness, "https");

        Assert.Equal(ClaimedAddress, LastForwardedFor(received));
    }

    [Fact]
    public async Task An_untrusted_senders_forwarded_for_is_ignored_and_the_socket_address_is_passed_on()
    {
        await using GatewayHarness harness = await Suite.StartAsync(
            new GatewayHarnessOptions { TrustedNetworks = SomewhereElse }, Suite.Ct);

        // The same claim, from a sender the cell does not trust. The upstream is told where the
        // request really came from, so nothing downstream can be made to believe the claim.
        EchoedRequest received = await CallAndReadUpstream(harness, origin: "http");

        Assert.Equal(SocketAddress, LastForwardedFor(received));
        Assert.NotEqual(ClaimedAddress, LastForwardedFor(received));
    }

    [Fact]
    public async Task A_cell_with_no_trusted_network_reads_no_forwarded_header_at_all()
    {
        // The configuration an operator is most likely to get wrong, and the one where the
        // framework's own default is "no restriction" rather than "trust nobody": with both known
        // lists empty the middleware would trust every sender, so the platform switches the headers
        // off instead (src/Platform.Security/PlatformForwardedHeaders.cs). Proven here as a client
        // sees it, because the failure mode is a cell that silently trusts the internet.
        await using GatewayHarness harness = await Suite.StartAsync(
            new GatewayHarnessOptions { TrustedNetworks = [] }, Suite.Ct);

        int before = await Observed.SettledCountAsync(harness, Suite.Ct);
        RawResponse refused = await Call(harness, "https");

        Assert.Equal(403, refused.StatusCode);
        Assert.Equal(RuleIds.GateOrigin, await Observed.RuleOfOneRequestAsync(harness, before, Suite.Ct));
    }

    /// <summary>
    /// One <c>/api</c> call over a bare socket, claiming <paramref name="forwardedProto"/> and
    /// carrying the app origin under scheme <paramref name="origin"/>.
    /// </summary>
    private static Task<RawResponse> Call(GatewayHarness harness, string forwardedProto, string origin = "https") =>
        RawHttp.SendAsync(
            harness.GatewayAddress, "GET", "/api/echo/thing", harness.App("app1").Host,
            [
                ("Sec-Fetch-Site", FetchMetadata.SameOrigin),
                ("Origin", origin + "://" + harness.App("app1").Host),
                ("X-Forwarded-Proto", forwardedProto),
                ("X-Forwarded-For", ClaimedAddress),
            ],
            Suite.Ct);

    /// <summary>The same call, and what the echo upstream received because of it.</summary>
    private static async Task<EchoedRequest> CallAndReadUpstream(
        GatewayHarness harness, string forwardedProto = "https", string origin = "https")
    {
        int before = harness.Echo.Requests.Count;
        RawResponse response = await Call(harness, forwardedProto, origin);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(before + 1, harness.Echo.Requests.Count);
        return harness.Echo.Requests[^1];
    }

    /// <summary>
    /// The last entry of the <c>X-Forwarded-For</c> the upstream received, which is the gateway's
    /// own view of the caller whether the proxy replaces the header or appends to it.
    /// </summary>
    private static string LastForwardedFor(EchoedRequest received)
    {
        Assert.True(
            received.Headers.TryGetValue("X-Forwarded-For", out ImmutableArray<string> values),
            "the upstream received no X-Forwarded-For, so this row has nothing to read. "
            + "contracts/upstream-contract.md has the gateway set one.");

        return string.Join(", ", values).Split(',')[^1].Trim();
    }
}
