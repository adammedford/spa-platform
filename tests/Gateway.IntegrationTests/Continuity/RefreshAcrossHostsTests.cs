using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Gateway.TestHarness;
using Microsoft.AspNetCore.Authentication;
using Platform.Abstractions;
using Platform.Testing;
using TestIdp;
using static Gateway.IntegrationTests.Continuity.Logins;

namespace Gateway.IntegrationTests.Continuity;

/// <summary>
/// Plan row P2-3 composed (#346; #332 T8, rows F5 and F3; change request P2-3, Decision item 7): one
/// refresh per session across two gateway hosts that share one session store and one refresh-lease
/// table, with the production <c>AccessTokenSource</c> and the test provider counting grants.
///
/// <para>The fifty calls run at grace zero, where a replay cannot be forgiven, and at the 60 s grace the
/// cell configures (#332 D1). The one-host F-10 (<c>Functional/RefreshTests.cs</c>) keeps its replay
/// tolerance and is not changed by this row.</para>
/// </summary>
public sealed class RefreshAcrossHostsTests
{
    /// <summary>
    /// The grace window the cell's app clients carry, read from the one place the cell sets it:
    /// <c>retry_grace_period_seconds</c> in <c>infra/envs/poc/apps/main.tf</c>, 60 s since #332 D1. Read,
    /// not copied, so the composed rows run at whatever the cell is configured with (CODING_STANDARDS.md,
    /// "A guard reads the thing it guards").
    /// </summary>
    private static readonly TimeSpan CellGrace = ReadCellGrace();

    /// <summary>The echo upstream's API, which validates nothing, so a token the provider issued on the stepped clock is forwarded as it is and recorded.</summary>
    private const string EchoPath = "/api/echo/items";

    [Theory]
    [InlineData("zero")]
    [InlineData("cell")]
    public async Task P2_3_F5_fifty_calls_split_across_two_hosts_sharing_one_lease_table_make_one_grant_and_no_refusal(string grace)
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.TwoHosts, Ct);
        (BrowserResponse[] calls, int[] perHost) = await FiftyAcrossBothHostsAsync(harness, grace == "zero" ? TimeSpan.Zero : CellGrace);

        // Mutation that turns it red: one refresh-lease table per host (RefreshLeasesPerHost, the control
        // below). Each host's session gate then takes its own lease and both reach the provider: at grace
        // zero the second grant is refused, at grace 60 it is replayed inside the window, and either way
        // there are two grants where this asserts one.
        Assert.Equal([25, 25], perHost);
        TokenGrant grant = Assert.Single(harness.Idp.RefreshGrants);
        Assert.True(grant is { Ok: true, ReplayedInsideGrace: false }, grant.ToString());
        Assert.All(calls, call => Assert.Equal(HttpStatusCode.OK, call.StatusCode));
        Assert.All(calls, call => Assert.Equal(harness.App("app1").ClientId, ClientOf(call)));

        // The record holds the one rotated refresh token, so the session's next refresh presents it.
        string? stored = await StoredTokenAsync(harness, "refresh_token");
        Assert.True(string.Equals(grant.RefreshTokenIssued, stored, StringComparison.Ordinal),
            $"the record holds {TestIdentityProvider.Label(stored)}, not {TestIdentityProvider.Label(grant.RefreshTokenIssued)}, which the one grant issued.");
    }

    /// <summary>
    /// The control that makes the row above able to fail (CODING_STANDARDS.md): the same fifty calls with
    /// one refresh-lease table per host, which is two tasks sharing no lease, as before P2-3. Only the
    /// grants are asserted: the refused host's re-read (T-B8) races the other host's write, so whether
    /// its calls end on that host's token or end the session is timing, not the row.
    /// </summary>
    [Fact]
    public async Task P2_3_F5_control_with_a_lease_table_per_host_the_provider_counts_two_grants_and_refuses_one()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(
            GatewayHarnessOptions.TwoHosts with { RefreshLeasesPerHost = true }, Ct);
        (_, int[] perHost) = await FiftyAcrossBothHostsAsync(harness, TimeSpan.Zero);

        // Mutation that turns it red: GatewayHarnessOptions.TwoHosts without the `with`, one table for
        // both hosts: one grant, and both assertions on the grants fail.
        Assert.Equal([25, 25], perHost);
        Assert.Equal(2, harness.Idp.RefreshGrants.Count);
        Assert.Single(harness.Idp.RefreshGrants, grant => grant.Outcome == "invalid_grant");
    }

    /// <summary>
    /// #332 F3 composed, on both sides of its bound (TTL &lt; t &lt; r + G, at the cell's 60 s grace). Host A's
    /// refresh is carried out by the provider, which then holds its answer (<see cref="TestIdpFaults.RefreshAnswerDelay"/>),
    /// and in that gap A is killed: its write never lands and its refresh lease is never given back
    /// (<see cref="SwitchableSessionStore.StallNextReplace"/>). The provider has rotated; the record still
    /// holds the superseded refresh token.
    ///
    /// <list type="bullet">
    /// <item><c>inside</c>: the user's next call reaches host B while A's lease is held, waits there as a
    /// loser, and when the lease lapses B takes it over and refreshes with the superseded token, about 22 s
    /// after the rotation: inside the grace window, so the provider honours it and the session continues.</item>
    /// <item><c>after</c>: the user is idle past r + G, which D1 says still ends the session. B's call
    /// takes the lapsed lease at once, the provider refuses the superseded token, the re-read before a
    /// refusal (T-B8) finds the same token, and the session ends once: 401 <c>session_ended</c>, one
    /// logged reason, and a challenge on the next navigation.</item>
    /// </list>
    ///
    /// Then A's frozen write is let go, and it changes nothing: it expects the version it read and meets
    /// B's outcome, and A's rotated token never reaches an upstream.
    /// </summary>
    [Theory]
    [InlineData("inside", null)]
    [InlineData("after", "session_ended")]
    public async Task P2_3_F3_a_host_killed_after_the_rotation_and_before_its_write_is_recovered_by_the_other_inside_rotation_plus_grace_and_ended_once_after(
        string retry, string? refusal)
    {
        int expected = refusal is null ? (int)HttpStatusCode.OK : Contract.Error(refusal).Status;
        SteppedClock clock = new();
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.TwoHosts with { Clock = clock }, Ct);
        GatewayHost a = harness.Hosts[0], b = harness.Hosts[1];
        HarnessApp app = harness.App("app1");
        GatewayHost target = a;
        using TestBrowser browser = harness.CreateBrowser(() => target);

        harness.Idp.Faults.RefreshRotationGrace = CellGrace;
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(-10);
        await harness.SignInAsync(browser, "app1", Ct);
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(5);
        string? superseded = Assert.Single(harness.Idp.Grants, grant => grant.GrantType == "authorization_code").RefreshTokenIssued;

        // A's refresh: the provider rotates, then holds the answer two seconds, and A dies in that gap.
        harness.Idp.Faults.RefreshAnswerDelay = TimeSpan.FromSeconds(2);
        Task<BrowserResponse> onA = browser.GetAsync(EchoPath, ct: Ct);
        await Functional.CapturedTelemetry.UntilAsync(() => harness.Idp.RefreshGrants.Count == 1, "host A's refresh grant", Ct);
        harness.SessionStore!.StallNextReplace();
        harness.Idp.Faults.RefreshAnswerDelay = TimeSpan.Zero;
        await harness.SessionStore.Stalled.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        TokenGrant rotated = harness.Idp.RefreshGrants[0];
        Assert.True(rotated.Ok, rotated.ToString());
        IReadOnlyList<RefreshLease> taken = harness.RefreshLeases!.Acquired;
        Assert.True(taken.Count == 1, $"{taken.Count} refresh leases were taken, not A's one.");
        RefreshLease dead = taken[0];

        target = b;
        BrowserResponse answer;
        if (retry == "inside")
        {
            int refused = harness.RefreshLeases.Refused;
            Task<BrowserResponse> onB = browser.GetAsync(EchoPath, ct: Ct);
            await Functional.CapturedTelemetry.UntilAsync(() => harness.RefreshLeases.Refused > refused, "host B finding A's refresh lease held", Ct);
            clock.Advance(dead.ExpiresAt - clock.GetUtcNow() + TimeSpan.FromSeconds(1));
            answer = await onB.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }
        else
        {
            clock.Advance(CellGrace + TimeSpan.FromSeconds(1));
            answer = await browser.GetAsync(EchoPath, ct: Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }

        // Mutation that turns both rows red: a lease table that never lets a lapsed lease be taken over
        // (InMemoryRefreshLeases answering null for an expired lease): B waits on the dead holder's lease
        // and never refreshes. Mutation that turns `after` red: a refused refresh that keeps the session
        // (AccessTokenSource answering invalid_grant as it answers an outage): 503 and the session lives.
        Assert.Equal(expected, (int)answer.StatusCode);
        Assert.Equal(2, harness.Idp.RefreshGrants.Count);
        TokenGrant second = harness.Idp.RefreshGrants[1];
        Assert.True(string.Equals(superseded, second.RefreshTokenUsed, StringComparison.Ordinal),
            $"B's refresh presented {TestIdentityProvider.Label(second.RefreshTokenUsed)}, not the superseded {TestIdentityProvider.Label(superseded)}.");
        if (retry == "inside")
        {
            Assert.True(second is { Ok: true, ReplayedInsideGrace: true }, second.ToString());
            Assert.True(CarriedBearer(harness.Echo.Requests[^1], second.AccessToken),
                $"B's call did not carry {TestIdentityProvider.Label(second.AccessToken)}, the token B's refresh issued.");
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/bff/session", ct: Ct)).StatusCode);
        }
        else
        {
            Assert.Equal("invalid_grant", second.Outcome);
            Assert.Equal(Contract.Error("session_ended").Status, (int)answer.StatusCode);
            Assert.Equal("session_ended", Code(answer));
            Assert.Single(harness.Log.Lines, line => line.Category.EndsWith(".AccessTokenSource", StringComparison.Ordinal)
                && line.Message.Contains("refused the refresh token", StringComparison.Ordinal));
            BrowserResponse next = await browser.NavigateAsync(app.Url("/"), Manual, Ct);
            Assert.Equal(HttpStatusCode.Redirect, next.StatusCode);
            Assert.StartsWith(harness.Idp.AuthorizeEndpoint + "?", next.Header("Location"), StringComparison.Ordinal);
        }

        string? before = await StoredTokenAsync(harness, "refresh_token");
        Assert.True(retry == "inside" ? string.Equals(second.RefreshTokenIssued, before, StringComparison.Ordinal) : before is null,
            $"the record holds {TestIdentityProvider.Label(before)} after B's call.");

        // The dead host's write, let go at last, meets B's outcome and changes nothing.
        harness.SessionStore.Resume();
        await onA.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(2, harness.Idp.RefreshGrants.Count);
        Assert.True(string.Equals(before, await StoredTokenAsync(harness, "refresh_token"), StringComparison.Ordinal),
            "the dead host's late write replaced the record B left.");
        Assert.DoesNotContain(harness.Echo.Requests, request => CarriedBearer(request, rotated.AccessToken));
    }

    /// <summary>
    /// Signs a browser in through host A on a token the provider issued already expired, then sends fifty
    /// API calls at once, alternating between the hosts, with the provider's token answers slowed so all
    /// fifty are waiting while the refresh is in flight. Returns the calls and how many went to each host.
    /// </summary>
    private static async Task<(BrowserResponse[] Calls, int[] PerHost)> FiftyAcrossBothHostsAsync(GatewayHarness harness, TimeSpan grace)
    {
        int[] perHost = new int[harness.Hosts.Count];
        int sent = -1;
        bool split = false;
        using TestBrowser browser = harness.CreateBrowser(() =>
        {
            if (!Volatile.Read(ref split))
            {
                return harness.Hosts[0];
            }

            int index = (int)((uint)Interlocked.Increment(ref sent) % (uint)harness.Hosts.Count);
            Interlocked.Increment(ref perHost[index]);
            return harness.Hosts[index];
        });

        harness.Idp.Faults.RefreshRotationGrace = grace;
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(-10);
        await harness.SignInAsync(browser, "app1", Ct);
        harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(5);
        harness.Idp.Faults.TokenDelay = TimeSpan.FromMilliseconds(200);
        harness.Idp.Faults.RefreshAnswerDelay = TimeSpan.FromMilliseconds(200);

        Volatile.Write(ref split, true);
        BrowserResponse[] calls = await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            browser.GetAsync("/api/orders/whoami?call=" + i, ct: Ct)));
        return (calls, perHost);
    }

    /// <summary>The one session's stored token of this name, read from the cell's one store.</summary>
    internal static async Task<string?> StoredTokenAsync(GatewayHarness harness, string name)
    {
        // Counted, then indexed: Assert.Single would print the keys on a failure, and a key is a bearer
        // credential (N-16).
        IReadOnlyList<string> created = harness.SessionStore!.Created;
        Assert.True(created.Count == 1, $"the cell's store created {created.Count} sessions, not the one.");
        string key = created[0];
        if (await harness.SessionStore.GetAsync(key, Ct) is not { } record)
        {
            return null;
        }

        AuthenticationTicket ticket = Assert.IsType<AuthenticationTicket>(TicketSerializer.Default.Deserialize(record.Ticket.ToArray()));
        return ticket.Properties.GetTokenValue(name);
    }

    private static TimeSpan ReadCellGrace()
    {
        string main = File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "infra", "envs", "poc", "apps", "main.tf"));
        System.Text.RegularExpressions.Match setting = Assert.Single(
            System.Text.RegularExpressions.Regex.Matches(main, @"^\s*retry_grace_period_seconds\s*=\s*(\d+)\s*$",
                System.Text.RegularExpressions.RegexOptions.Multiline, RegexTimeout.Span));
        return TimeSpan.FromSeconds(int.Parse(setting.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Whether a request that reached the echo upstream carried exactly this bearer token. Compared, never printed (N-16).</summary>
    private static bool CarriedBearer(EchoedRequest request, string? accessToken) =>
        accessToken is not null
        && request.Headers.TryGetValue("Authorization", out ImmutableArray<string> sent)
        && sent.SequenceEqual(["Bearer " + accessToken]);

    private static string? ClientOf(BrowserResponse response)
    {
        using JsonDocument body = JsonDocument.Parse(response.Body);
        return body.RootElement.GetProperty("clientId").GetString();
    }
}
