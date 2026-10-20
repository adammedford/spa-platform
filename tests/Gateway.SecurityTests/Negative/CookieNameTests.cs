using System.Collections.Immutable;
using Gateway.TestHarness;
using Platform.Abstractions;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// N-25, whole for the clause a hermetic tier can hold: "From a sibling host, set each platform
/// cookie for the parent domain, under its exact name and in other letter cases -> The browser
/// refuses the exact name. The gateway rejects a request that carries a case variant."
/// (docs/plan/04-tests.md.)
///
/// <para><b>The gateway's half is here; the browser's half is the deployed suite's.</b> "The browser
/// refuses the exact name" is a <c>__Host-</c>-prefix rule that only a real browser enforces, and
/// the test browser deliberately does not: it "enforces no cookie prefix", so "a planted cookie with
/// an exact <c>__Host-</c> name is accepted here, as finding R-D3 records". Asserting that clause
/// against this client would prove the client, not the platform, so it belongs to row 6.5 and is not
/// attempted here. What is proven is the server's own check, which exists exactly because the
/// prefix rule is the browser's: <c>contracts/request-gate.md</c> -> "The rule that covers every
/// path" says a name beginning <c>__Host-spa-</c> in any other letter case is refused with 400, "and
/// so is a platform cookie sent twice", because ASP.NET Core matches request cookie names without
/// regard to case.</para>
///
/// <para><b>Why the raw socket.</b> The <c>Cookie</c> header's bytes are the input: two names equal
/// but for case, and one name twice. A client with a cookie jar normalises both away.</para>
///
/// <para><b>No request here carries a session</b>, so one the gate admits goes on to the shell gate,
/// which answers an asset request without a session 401 (ADR-0008). That is what the controls read:
/// the gate's 400 under <see cref="RuleIds.GateCookieName"/> against the shell gate's 401, which
/// carries <see cref="RuleIds.AuthSession"/> only when a session cookie was presented and named no
/// session.</para>
///
/// <para>These requests isolate the HTTP contract named above. Session establishment and
/// preservation require separate acceptance evidence; a successful response here proves only the
/// asserted routing, policy or telemetry behaviour.</para>
/// </summary>
public sealed class CookieNameTests(CookieNameTests.Cell cell) : IClassFixture<CookieNameTests.Cell>
{
    /// <summary>One harness for this class: every row here sends the same registry a different header.</summary>
    public sealed class Cell : IAsyncLifetime
    {
        public GatewayHarness Harness { get; private set; } = null!;

        public async ValueTask InitializeAsync() =>
            Harness = await Suite.StartAsync(ct: TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync() => await Harness.DisposeAsync();
    }

    private GatewayHarness Harness => cell.Harness;

    /// <summary>
    /// The platform cookie names, from the Cookies table of the browser contract, with a suffix
    /// substituted for the wildcard the login-state cookies carry. Read from the contract, so a
    /// cookie the platform adds later is covered without this file changing.
    /// </summary>
    private static readonly ImmutableArray<string> Names =
        [.. Contract.PlatformCookieNames.Select(name => name.Replace(".*", ".abc123", StringComparison.Ordinal))];

    public static TheoryData<string> PlatformCookieNames => new(Names);

    [Theory]
    [MemberData(nameof(PlatformCookieNames))]
    public async Task A_platform_cookie_name_in_another_letter_case_is_400_under_gate_cookie_name(string name)
    {
        // The whole prefix upper-cased, which is what a sibling host would plant to have the
        // framework read it as the real cookie.
        string variant = Variant(name);
        Assert.NotEqual(name, variant);
        Assert.Equal(name, variant, ignoreCase: true);

        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);
        RawResponse response = await Send(variant + "=whatever");

        await AssertRefusedAsync(response, before);
    }

    [Fact]
    public async Task A_platform_cookie_sent_twice_is_400_under_gate_cookie_name()
    {
        string name = Names[0];
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        // Equal but for case is the same cookie to the framework, so this is one cookie sent twice
        // (contracts/request-gate.md). Both halves of the rule, in one header.
        RawResponse response = await Send($"{name}=one; {name}=two");

        await AssertRefusedAsync(response, before);
    }

    [Theory]
    [MemberData(nameof(PlatformCookieNames))]
    public async Task The_exact_name_sent_once_is_not_what_this_rule_refuses(string name)
    {
        // The control. Without it the rows above would pass against a gateway that refused every
        // request carrying any cookie at all, which is a different and much blunter rule
        // (CODING_STANDARDS.md -> "An assertion must be able to fail").
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        RawResponse response = await Send(name + "=whatever");

        // Past the gate, to the shell gate. Only the session cookie's name is read as a session, and
        // "whatever" decrypts to none, so that one is refused under auth.session; the others name no
        // session at all.
        await AssertRefusedForNoSessionAsync(
            response, before, name == SignedIn.SessionCookieName ? RuleIds.AuthSession : null);
    }

    [Fact]
    public async Task An_unrelated_cookie_name_is_not_what_this_rule_refuses_however_it_is_cased()
    {
        // A cookie that is not the platform's, in mixed case, passes the gate: the rule is about names
        // that imitate a platform cookie, not about letter case in general.
        int before = await Observed.SettledCountAsync(Harness, Suite.Ct);

        RawResponse response = await Send("Some-App-Cookie=whatever; ANOTHER=two");

        await AssertRefusedForNoSessionAsync(response, before, rule: null);
    }

    /// <summary>One request to the app's root carrying exactly this <c>Cookie</c> header.</summary>
    private Task<RawResponse> Send(string cookieHeader) => RawHttp.SendAsync(
        Harness.GatewayAddress, "GET", "/", Harness.App("app1").Host,
        [("Cookie", cookieHeader)], Suite.Ct);

    /// <summary>
    /// The same name with the rule's prefix upper-cased: <c>__Host-spa-session</c> becomes
    /// <c>__HOST-SPA-session</c>, which is the name a case-insensitive cookie lookup still finds.
    /// The prefix comes from <c>contracts/request-gate.md</c>, not from this file.
    /// </summary>
    private static string Variant(string name)
    {
        string prefix = Contract.PlatformCookiePrefix;
        Assert.StartsWith(prefix, name, StringComparison.Ordinal);
        return prefix.ToUpperInvariant() + name[prefix.Length..];
    }

    /// <summary>The shell gate's answer to a request the gate admitted with no session: 401 <c>unauthenticated</c>, under <paramref name="rule"/>.</summary>
    private async Task AssertRefusedForNoSessionAsync(RawResponse response, int before, string? rule)
    {
        Contract.ErrorRow expected = Contract.Error("unauthenticated");
        Assert.Equal(expected.Status, response.StatusCode);
        Assert.Contains($"\"code\":\"{expected.Code}\"", RawBody.Text(response), StringComparison.Ordinal);
        Assert.Equal(rule, await Observed.RuleOfOneRequestAsync(Harness, before, Suite.Ct));
    }

    private async Task AssertRefusedAsync(RawResponse response, int before)
    {
        Contract.ErrorRow expected = Contract.Error("invalid_request");
        Assert.Equal(expected.Status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Header("Content-Type"));
        Assert.Contains($"\"code\":\"{expected.Code}\"", RawBody.Text(response), StringComparison.Ordinal);

        // Nothing the client sent reaches the body or the line: the cookie name is the client's to
        // choose (contracts/telemetry.md -> Never logged).
        Assert.DoesNotContain("__Host", RawBody.Text(response), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(RuleIds.GateCookieName, await Observed.RuleOfOneRequestAsync(Harness, before, Suite.Ct));
    }
}
