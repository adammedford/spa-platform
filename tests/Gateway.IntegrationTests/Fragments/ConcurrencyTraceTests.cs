using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Gateway.TestHarness;
using Platform.Abstractions;
using Platform.Registry;
using TestIdp;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>
/// F-35, O7 (#1007, part of #961): the concurrency traces of #961's traces comment
/// (https://github.com/adammedford/spa-platform/issues/961#issuecomment-6645349004), composed. Two gateway
/// hosts, A and B, over the cell's one session store, one lease table and one key ring, behind a shell with
/// one fragment, <c>orders</c>. Each host reaches the store through its own <see cref="HostSessionStore"/>,
/// whose hook before a versioned replace is where a trace puts the other writer's turn, so the
/// interleaving is fixed rather than raced, as workstream 3 fixes it against its doubles
/// (tests/Platform.Identity.Tests/FragmentTraceTests.cs).
///
/// What a trace states is read where the composed gateway shows it: the record's version and presence in
/// the store, the token each upstream call carried at the echo API, the grants and revocations at the test
/// provider, and each host's count of versioned replaces. The record's tokens are encrypted, and the suite
/// reads them only by what the gateway sends upstream. The figures are the decided constants: S =
/// <c>StoreDeadline</c> 5 s, P = 10 s, lease TTL 21 s, a write deadline of acquire + 20 s.
/// </summary>
public sealed class ConcurrencyTraceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Trace 1: A refreshes the shell's set and B refreshes orders', both reading v7. A wins v8
    /// <c>{S1, F0}</c>; B's replace loses, its re-read shows orders still holding F0, and it re-applies: v9
    /// <c>{S1, F1}</c>, 200 on both. Variant: B's second attempt would start with less than 2S before its
    /// acquire + 20 s, so B answers its own F1 unpersisted and v8 keeps F0.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G9_O7_trace_1(bool pastTheWriteDeadline)
    {
        // Mutation that turns it red: dropping the re-apply (a lost replace read as superseded) leaves v8
        // as the record in the first row, so its version and B's replace count fail. Mutation: no write
        // deadline on a re-apply writes F1 in the second row, so its version and replace count fail.
        await using TwoHostShell cell = await TwoHostShell.StartAsync(Ct);
        long v7 = await cell.VersionAsync(Ct);

        string? a = null;
        cell.B.SessionStore!.BeforeReplace = async attempt =>
        {
            if (attempt == 1)
            {
                a = await cell.EchoAsync(cell.A, fragmentId: null, Ct);
                if (pastTheWriteDeadline)
                {
                    cell.Clock.Advance(TimeSpan.FromSeconds(11));
                }
            }
        };

        string? b = await cell.EchoAsync(cell.B, "orders", Ct);

        TokenGrant s1 = Assert.Single(cell.Harness.Idp.RefreshGrants, g => g.ClientId == cell.Shell.ClientId);
        TokenGrant f1 = Assert.Single(cell.Harness.Idp.RefreshGrants, g => g.ClientId == cell.Orders.ClientId);
        Assert.True(s1.Ok && f1.Ok);
        Assert.Equal(s1.AccessToken, a);
        Assert.Equal(f1.AccessToken, b);
        Assert.Equal(pastTheWriteDeadline ? v7 + 1 : v7 + 2, await cell.VersionAsync(Ct));
        Assert.Equal(pastTheWriteDeadline ? 1 : 2, cell.B.SessionStore.Replaces);

        // The record serves the shell's S1 on both rows with no new grant; on the first row orders' F1 as
        // well. A call that found F0 would refresh again (it is due), so the grant count is the check.
        cell.B.SessionStore.BeforeReplace = null;
        Assert.Equal(s1.AccessToken, await cell.EchoAsync(cell.B, fragmentId: null, Ct));
        if (!pastTheWriteDeadline)
        {
            Assert.Equal(f1.AccessToken, await cell.EchoAsync(cell.A, "orders", Ct));
        }

        Assert.Equal(2, cell.Harness.Idp.RefreshGrants.Count);
    }

    /// <summary>
    /// Trace 2(i): B's fallback callback writes v8 <c>{F'}</c> first; A's refresh replace loses, its
    /// re-read shows F', not the F0 it presented, so A is superseded and drops F1. Winner: the callback;
    /// record v8; A answers 200 with F''s token; B lands on <c>returnUrl</c>.
    /// </summary>
    [Fact]
    public async Task G9_O7_trace_2i()
    {
        // Mutation that turns it red: re-applying whatever the winner holds writes F1 over F', so the
        // record's version and orders' token fail.
        await using TwoHostShell cell = await TwoHostShell.StartAsync(Ct);
        long v7 = await cell.VersionAsync(Ct);
        BrowserResponse? callback = null;
        int signIn = cell.A.SessionStore!.Replaces;
        cell.A.SessionStore.BeforeReplace = async attempt =>
        {
            if (attempt == signIn + 1)
            {
                callback = await cell.FallbackAsync(cell.B, Ct);
            }
        };

        (BrowserResponse a, string? sent) = await cell.CallAsync(cell.A, "orders", Ct);

        string fPrime = cell.LastOrdersCodeToken();
        TokenGrant f1 = Assert.Single(cell.Harness.Idp.RefreshGrants);
        Assert.NotEqual(f1.AccessToken, fPrime);
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(fPrime, sent);
        Assert.Equal("/home", callback?.Url.AbsolutePath);
        Assert.Contains(callback!.RedirectedFrom, hop => hop.AbsolutePath == "/signin-oidc/orders");
        SessionRecord record = (await cell.RecordAsync(Ct))!;
        Assert.Equal(v7 + 1, record.Version);
        Assert.Equal(fPrime, Records.FragmentToken(record, "orders", "access_token"));
    }

    /// <summary>
    /// Trace 2(ii): A's refresh writes v8 <c>{F1}</c> first; B's callback replace loses and re-applies F'
    /// unconditionally within <c>MergeBudget</c>: v9 <c>{F'}</c>. Winner: the callback; A 200 (F1), B lands on
    /// <c>returnUrl</c>; F1 orphaned.
    /// </summary>
    [Fact]
    public async Task G9_O7_trace_2ii()
    {
        // Mutation that turns it red: a callback that drops its set on a lost replace (MergeBudget 1)
        // leaves F1 in the record, so the version and orders' token fail.
        await using TwoHostShell cell = await TwoHostShell.StartAsync(Ct);
        long v7 = await cell.VersionAsync(Ct);
        (BrowserResponse Response, string? Token)? a = null;
        int before = cell.B.SessionStore!.Replaces;
        cell.B.SessionStore.BeforeReplace = async attempt =>
        {
            if (attempt == before + 1)
            {
                a = await cell.CallAsync(cell.A, "orders", Ct);
            }
        };

        BrowserResponse callback = await cell.FallbackAsync(cell.B, Ct);

        TokenGrant f1 = Assert.Single(cell.Harness.Idp.RefreshGrants);
        Assert.Equal(HttpStatusCode.OK, a?.Response.StatusCode);
        Assert.Equal(f1.AccessToken, a?.Token);
        Assert.Equal("/home", callback.Url.AbsolutePath);
        SessionRecord record = (await cell.RecordAsync(Ct))!;
        Assert.Equal(v7 + 2, record.Version);
        string fPrime = cell.LastOrdersCodeToken();
        Assert.NotEqual(f1.AccessToken, fPrime);
        Assert.Equal(fPrime, Records.FragmentToken(record, "orders", "access_token"));
    }

    /// <summary>
    /// Trace 3: A refreshes orders from v7; B's logout takes the shell's gate and then orders', revokes S0
    /// and F0 and removes the record. Winner: logout; no record; A answers 401 <c>unauthenticated</c>
    /// whether its grant came before the revocation (its write finds the record gone) or after it (F0 is
    /// refused, and the refusal's re-read finds no record).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G9_O7_trace_3(bool grantAfterRevocation)
    {
        // Mutation that turns it red: a refusal whose bounded removal finds the record gone answering
        // fragment_login_required turns the "after" row's code red.
        HoldingClock? hold = null;
        await using TwoHostShell cell = await TwoHostShell.StartAsync(
            Ct, idpClock: grantAfterRevocation ? clock => hold = new HoldingClock(clock) : null);
        SessionRecord v7 = (await cell.RecordAsync(Ct))!;
        BrowserResponse? logout = null;
        Task<(BrowserResponse Response, string? Token)> a;
        if (grantAfterRevocation)
        {
            // The provider holds A's refresh before it looks at the token; B logs out meanwhile.
            cell.Harness.Idp.Faults.TokenDelay = TimeSpan.FromSeconds(1);
            a = Task.Run(() => cell.CallAsync(cell.A, "orders", Ct), Ct);
            await hold!.Holding.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            cell.Harness.Idp.Faults.TokenDelay = TimeSpan.Zero;
            logout = await cell.LogoutAsync(cell.B, Ct);
            hold.Release();
        }
        else
        {
            int signIn = cell.A.SessionStore!.Replaces;
            cell.A.SessionStore.BeforeReplace = async attempt =>
            {
                if (attempt == signIn + 1)
                {
                    logout = await cell.LogoutAsync(cell.B, Ct);
                }
            };
            a = cell.CallAsync(cell.A, "orders", Ct);
        }

        (BrowserResponse answer, string? sent) = await a;

        Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
        Assert.Equal("unauthenticated", TwoHostShell.Code(answer));
        Assert.Null(sent);
        Assert.Equal(HttpStatusCode.Redirect, logout?.StatusCode);
        Assert.Null(await cell.RecordAsync(Ct));
        Assert.Contains(cell.Harness.Idp.Revocations, r => r.Token == Records.ShellToken(v7, "refresh_token"));
        Assert.Contains(cell.Harness.Idp.Revocations, r => r.Token == Records.FragmentToken(v7, "orders", "refresh_token"));
        Assert.Equal(grantAfterRevocation ? "invalid_grant" : "ok", Assert.Single(cell.Harness.Idp.RefreshGrants).Outcome);
    }

    /// <summary>
    /// Trace 4: A's replace loses (C wrote v8) and B's logout removes the record; A's one re-read finds
    /// nothing, and A answers 401 <c>unauthenticated</c>. Variant: the record lives and C's write removed
    /// orders' set: 401 <c>fragment_login_required</c>, and nothing is written.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G9_O7_trace_4(bool recordLives)
    {
        // Mutation that turns it red: on a re-read that finds the set gone, re-applying it (resurrecting
        // the set) gives the variant's record orders again, so its version check fails.
        await using TwoHostShell cell = await TwoHostShell.StartAsync(Ct);
        SessionRecord? written = null;
        int reads = 0;
        int signIn = cell.A.SessionStore!.Replaces;
        cell.A.SessionStore.BeforeReplace = async attempt =>
        {
            if (attempt != signIn + 1)
            {
                return;
            }

            written = await Records.WithoutFragmentAsync(cell.Harness.SessionStore!, cell.Key, "orders", Ct);
            if (!recordLives)
            {
                Assert.Equal(HttpStatusCode.Redirect, (await cell.LogoutAsync(cell.B, Ct)).StatusCode);
            }

            cell.A.SessionStore.BeforeGet = () =>
            {
                Interlocked.Increment(ref reads);
                return Task.CompletedTask;
            };
        };

        (BrowserResponse a, string? sent) = await cell.CallAsync(cell.A, "orders", Ct);

        Assert.Equal(1, reads);
        Assert.Null(sent);
        Assert.Equal(HttpStatusCode.Unauthorized, a.StatusCode);
        SessionRecord? record = await cell.RecordAsync(Ct);
        if (recordLives)
        {
            Assert.Equal("fragment_login_required", TwoHostShell.Code(a));
            Assert.Equal(written!.Version, record?.Version);
            Assert.Null(Records.FragmentToken(record!, "orders", "access_token"));
        }
        else
        {
            Assert.Equal("unauthenticated", TwoHostShell.Code(a));
            Assert.Null(record);
        }
    }

    /// <summary>
    /// Trace 5: A refreshes orders (F0 to F1) and its first replace loses to C's write, which left F0 in
    /// place. Before A re-applies, the clock passes A's acquire + 21 s: A's lease lapses, and A2 on host B
    /// takes it over, presents F0 inside the 60 s grace and gets F2. A's write deadline (acquire + 20 s)
    /// precludes its re-apply, so A answers F1 unpersisted, and A2's write of F2 lands: the record holds a
    /// live token.
    /// </summary>
    [Fact]
    public async Task G9_O7_trace_5()
    {
        // Trace 5's mutation, which must fail: re-applying after the lease lapses (no write deadline on a
        // re-apply) writes F1 first; A2 is then superseded and drops F2, the record holds F1, a token the
        // provider has rotated out, and the last three assertions fail.
        await using TwoHostShell cell = await TwoHostShell.StartAsync(Ct);
        cell.Harness.Idp.Faults.RefreshRotationGrace = TimeSpan.FromSeconds(60);
        TaskCompletionSource a2Granted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource a2Write = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<(BrowserResponse Response, string? Token)>? a2 = null;
        int onB = cell.B.SessionStore!.Replaces;
        cell.B.SessionStore.BeforeReplace = async attempt =>
        {
            if (attempt == onB + 1)
            {
                a2Granted.TrySetResult();
                await a2Write.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
        };
        int signIn = cell.A.SessionStore!.Replaces;
        cell.A.SessionStore.BeforeReplace = async attempt =>
        {
            if (attempt == signIn + 1)
            {
                await Records.BumpAsync(cell.Harness.SessionStore!, cell.Key, Ct);

                // Past the lease's time to live, 21 s (P + 2S + 1 s).
                cell.Clock.Advance(TimeSpan.FromSeconds(22));
                a2 = Task.Run(() => cell.CallAsync(cell.B, "orders", Ct), Ct);
                await a2Granted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        };

        (BrowserResponse a, string? f1Sent) = await cell.CallAsync(cell.A, "orders", Ct);
        a2Write.TrySetResult();
        (BrowserResponse second, string? f2Sent) = await a2!;

        Assert.Equal(2, cell.Harness.Idp.RefreshGrants.Count);
        TokenGrant f1 = cell.Harness.Idp.RefreshGrants[0];
        TokenGrant f2 = cell.Harness.Idp.RefreshGrants[1];
        Assert.True(f2 is { Ok: true, ReplayedInsideGrace: true }, f2.ToString());
        Assert.Equal((HttpStatusCode.OK, f1.AccessToken), (a.StatusCode, f1Sent));
        Assert.Equal((HttpStatusCode.OK, f2.AccessToken), (second.StatusCode, f2Sent));

        // A's write deadline stopped its re-apply: one replace past the one C beat.
        Assert.Equal(signIn + 1, cell.A.SessionStore.Replaces);
        SessionRecord record = (await cell.RecordAsync(Ct))!;
        Assert.Equal(f2.RefreshTokenIssued, Records.FragmentToken(record, "orders", "refresh_token"));
        Assert.True(cell.Harness.Idp.IsRedeemable(Records.FragmentToken(record, "orders", "refresh_token")));
    }

    /// <summary>
    /// A session store that fails inside a fragment handler answers 503 <c>session_store_unavailable</c>:
    /// a fragment callback's merge (the fallback's) and a <c>/bff/chain</c> step's read, as on a login callback.
    /// </summary>
    [Fact]
    public async Task G9_O7_a_store_failure_in_a_fragment_handler_answers_503()
    {
        // Mutation that turns it red: a fragment handler that lets the store's exception through answers
        // 500, not 503, on both calls.
        await using TwoHostShell cell = await TwoHostShell.StartAsync(Ct);

        // The callback: the merge's versioned replace fails with an unknown outcome.
        cell.B.SessionStore!.BeforeReplace = _ => throw HostSessionStore.Unavailable("replace (PutItem)");
        BrowserResponse callback = await cell.FallbackAsync(cell.B, Ct);
        cell.B.SessionStore.BeforeReplace = null;
        Assert.Equal("/signin-oidc/orders", callback.Url.AbsolutePath);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, callback.StatusCode);
        Assert.Equal("session_store_unavailable", TwoHostShell.Code(callback));

        // A chain step: a fresh login on B stopped at its first /bff/chain step, whose read then fails.
        using TestBrowser browser = cell.Harness.CreateBrowser(() => cell.B);
        BrowserResponse hop = await browser.NavigateAsync(
            cell.Shell.Url("/bff/login?returnUrl=%2F"), new RequestOptions { Redirect = RedirectMode.Manual }, Ct);
        Uri? step = null;
        for (int i = 0; i < 4 && step is null; i++)
        {
            Uri next = new(hop.Url, hop.Header("Location") ?? throw new InvalidOperationException($"no redirect at {hop}"));
            if (next.AbsolutePath == "/bff/chain")
            {
                step = next;
                break;
            }

            hop = await browser.NavigateAsync(next, new RequestOptions { Redirect = RedirectMode.Manual }, Ct);
        }

        cell.B.SessionStore.BeforeGet = () => throw HostSessionStore.Unavailable("read (GetItem)");
        BrowserResponse chained = await browser.NavigateAsync(step!, new RequestOptions { Redirect = RedirectMode.Manual }, Ct);
        cell.B.SessionStore.BeforeGet = null;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, chained.StatusCode);
        Assert.Equal("session_store_unavailable", TwoHostShell.Code(chained));
    }

    /// <summary>
    /// A fragment removed or rotated while its chain hop is at the provider (#961's traces comment ->
    /// "Fragment removed mid-login", and its table of writes after a lost replace, W1). Removed: its
    /// callback path names no fragment of the entry now, so it answers 404 <c>not_found</c> under
    /// <c>path.reserved</c> and the chain stops there, the session standing. Rotated: the code was issued to
    /// the old client and is redeemed with the new one's credentials, which the provider refuses as
    /// <c>invalid_grant</c>, a definitive refusal: the fragment is marked <c>unavailable</c> and the chain
    /// continues to <c>returnUrl</c>.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G9_O7_a_fragment_removed_or_rotated_mid_login_answers_as_specified(bool rotated)
    {
        // Mutation that turns it red: a callback that keeps the snapshot's fragment list from the chain's
        // start rather than the live entry's serves the removed fragment's callback (not 404), and a
        // rotated fragment's exchange made with the old client's credentials signs it in (not unavailable).
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.Shell(2), Ct);
        HarnessApp shell = harness.App("shell");
        using TestBrowser browser = harness.CreateBrowser();
        RequestOptions manual = new() { Redirect = RedirectMode.Manual };

        // Step the login by hand to the point where orders' hop sends the browser to the provider.
        BrowserResponse hop = await browser.NavigateAsync(shell.Url("/bff/login?returnUrl=%2Fdone"), manual, Ct);
        for (int i = 0; !(hop.Header("Location") is { } at && at.Contains("client_id=" + shell.Fragment("orders").ClientId + "&", StringComparison.Ordinal)); i++)
        {
            Assert.True(i < 4, $"the login never reached orders' hop: {hop}");
            hop = await browser.NavigateAsync(new Uri(hop.Url, hop.Header("Location")!), manual, Ct);
        }

        Uri authorize = new(hop.Url, hop.Header("Location")!);
        HarnessFragment? after = null;
        if (rotated)
        {
            (_, after, RefreshOutcome outcome) = await harness.RotateFragmentClientAsync("shell", "orders", Ct);
            Assert.Equal(RefreshOutcome.Swapped, outcome);
        }
        else
        {
            Assert.Equal(RefreshOutcome.Swapped, await harness.UpdateRegistryAsync(next => next["apps"]![0]!["fragments"]!.AsArray().RemoveAt(0), Ct));
        }

        BrowserResponse atIdp = await browser.NavigateAsync(authorize, manual, Ct);
        BrowserResponse callback = await browser.NavigateAsync(new Uri(atIdp.Url, atIdp.Header("Location")!), manual, Ct);
        Assert.Equal("/signin-oidc/orders", callback.Url.AbsolutePath);

        if (!rotated)
        {
            Assert.Equal(HttpStatusCode.NotFound, callback.StatusCode);
            Assert.Equal("not_found", TwoHostShell.Code(callback));
            JsonElement line = harness.Log.Json.Last(j => j.GetProperty("msg").GetString() == "request");
            Assert.Equal("path.reserved", line.GetProperty("rule").GetString());
            Assert.DoesNotContain(harness.Idp.Grants, g => g.GrantType == "authorization_code" && g.ClientId == shell.Fragment("orders").ClientId);
            Assert.Equal(new Dictionary<string, string> { ["reports"] = "loginRequired" }, Fragments.States(await Fragments.SessionAsync(browser, shell, Ct)));
            return;
        }

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/bff/chain", new Uri(callback.Url, callback.Header("Location")!).AbsolutePath);
        Assert.Equal("invalid_grant", Assert.Single(harness.Idp.Grants, g => g.GrantType == "authorization_code" && g.ClientId == after!.ClientId).Outcome);
        BrowserResponse end = await browser.NavigateAsync(new Uri(callback.Url, callback.Header("Location")!), ct: Ct);
        Assert.Equal(shell.Url("/done"), end.Url);
        Assert.Equal(
            new Dictionary<string, string> { ["orders"] = "unavailable", ["reports"] = "signedIn" },
            Fragments.States(await Fragments.SessionAsync(browser, shell, Ct)));
    }

    /// <summary>
    /// A logout started while a refresh holds a fragment's gate waits for it, then revokes the token that
    /// refresh wrote and removes the record, within the 40 s bound (<c>LogoutGateBudget</c> + S + P + S).
    /// </summary>
    [Fact]
    public async Task G9_O7_logout_waits_for_a_refresh_holding_a_fragment_gate()
    {
        // Mutation that turns it red: a logout that takes no fragment gate (the shell's alone) revokes the
        // superseded F0 before the refresh writes, so "not completed" and the revoked token fail.
        HoldingClock? hold = null;
        await using TwoHostShell cell = await TwoHostShell.StartAsync(Ct, idpClock: clock => hold = new HoldingClock(clock));
        cell.Harness.Idp.Faults.TokenDelay = TimeSpan.FromSeconds(1);

        Task<(BrowserResponse Response, string? Token)> refresh = Task.Run(() => cell.CallAsync(cell.A, "orders", Ct), Ct);
        await hold!.Holding.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        cell.Harness.Idp.Faults.TokenDelay = TimeSpan.Zero;
        Stopwatch elapsed = Stopwatch.StartNew();
        Task<BrowserResponse> logout = Task.Run(() => cell.LogoutAsync(cell.A, Ct), Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(300), Ct);
        Assert.False(logout.IsCompleted);
        Assert.Empty(cell.Harness.Idp.Revocations);
        hold.Release();

        (BrowserResponse refreshed, string? sent) = await refresh;
        BrowserResponse loggedOut = await logout;
        elapsed.Stop();

        TokenGrant grant = Assert.Single(cell.Harness.Idp.RefreshGrants);
        Assert.Equal((HttpStatusCode.OK, grant.AccessToken), (refreshed.StatusCode, sent));
        Assert.Equal(HttpStatusCode.Redirect, loggedOut.StatusCode);
        Assert.Equal(grant.RefreshTokenIssued, Assert.Single(cell.Harness.Idp.Revocations, r => r.ClientId == cell.Orders.ClientId).Token);
        Assert.Null(await cell.RecordAsync(Ct));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(40), $"logout took {elapsed.Elapsed}");
    }

    /// <summary>
    /// A holder stuck past <c>LogoutGateBudget</c> (20 s, the write deadline): orders' refresh is granted
    /// and then stalls in its versioned replace, holding orders' gate. Logout skips that gate, logs orders
    /// <c>gate_timeout</c> and revokes nothing for it, revokes the shell's set and removes the record. The
    /// stalled write then loses, re-reads nothing and stops: no record comes back, and the call answers
    /// 401 <c>unauthenticated</c>. Runs the budget's 20 s on the system's timers.
    /// </summary>
    [Fact]
    public async Task G9_O7_logout_skips_a_gate_held_past_LogoutGateBudget()
    {
        // Mutation that turns it red: waiting on every gate with no budget leaves the logout unanswered
        // while the write is stalled, so the 60 s wait below times out.
        await using TwoHostShell cell = await TwoHostShell.StartAsync(Ct);
        TaskCompletionSource stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int signIn = cell.A.SessionStore!.Replaces;
        cell.A.SessionStore.BeforeReplace = async attempt =>
        {
            if (attempt == signIn + 1)
            {
                stalled.TrySetResult();
                await resume.Task.WaitAsync(TimeSpan.FromSeconds(90));
            }
        };

        Task<(BrowserResponse Response, string? Token)> holder = Task.Run(() => cell.CallAsync(cell.A, "orders", Ct), Ct);
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Stopwatch elapsed = Stopwatch.StartNew();
        BrowserResponse logout = await cell.LogoutAsync(cell.A, Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        elapsed.Stop();

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(20) - TimeSpan.FromMilliseconds(100), $"logout returned after {elapsed.Elapsed}");
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(40), $"logout took {elapsed.Elapsed}");
        string[] lines = [.. cell.Harness.Log.Lines.Select(line => line.Text).Where(text => text.Contains("gate_timeout", StringComparison.Ordinal))];
        Assert.EndsWith("of fragment orders: gate_timeout.", Assert.Single(lines), StringComparison.Ordinal);
        Assert.DoesNotContain(cell.Harness.Idp.Revocations, r => r.ClientId == cell.Orders.ClientId);
        Assert.Contains(cell.Harness.Idp.Revocations, r => r.ClientId == cell.Shell.ClientId);
        Assert.Null(await cell.RecordAsync(Ct));

        resume.TrySetResult();
        (BrowserResponse late, string? sent) = await holder;
        Assert.Equal(HttpStatusCode.Unauthorized, late.StatusCode);
        Assert.Equal("unauthenticated", TwoHostShell.Code(late));
        Assert.Null(sent);
        Assert.Null(await cell.RecordAsync(Ct));
    }
}

/// <summary>
/// The cell every trace starts from: two hosts over one store, a shell with the <c>orders</c> fragment,
/// signed in on A with every access token already due, so the next call on either route refreshes that
/// route's own set. A browser per host shares the one session cookie.
/// </summary>
internal sealed class TwoHostShell : IAsyncDisposable
{
    private TwoHostShell(GatewayHarness harness, SteppedClock clock, TestBrowser onA, TestBrowser onB, string key)
    {
        Harness = harness;
        Clock = clock;
        OnA = onA;
        OnB = onB;
        Key = key;
    }

    public GatewayHarness Harness { get; }

    public SteppedClock Clock { get; }

    public GatewayHost A => Harness.Hosts[0];

    public GatewayHost B => Harness.Hosts[1];

    public HarnessApp Shell => Harness.App("shell");

    public HarnessFragment Orders => Shell.Fragment("orders");

    /// <summary>The browser routed to A, and the same session's browser routed to B.</summary>
    public TestBrowser OnA { get; }

    public TestBrowser OnB { get; }

    /// <summary>The session's key. A bearer credential (N-16): read the record with it, never print it.</summary>
    public string Key { get; }

    public static async Task<TwoHostShell> StartAsync(
        CancellationToken ct, Func<SteppedClock, TimeProvider>? idpClock = null, int fragments = 1)
    {
        SteppedClock clock = new();
        GatewayHarness harness = await GatewayHarness.StartAsync(
            GatewayHarnessOptions.Shell(fragments) with
            {
                Hosts = 2,
                KeyRing = KeyRingSharing.OneParameterStore,
                Clock = clock,
                IdpClock = idpClock?.Invoke(clock),
            },
            ct);
        try
        {
            TestBrowser onA = harness.CreateBrowser(() => harness.Hosts[0]);
            TestBrowser onB = harness.CreateBrowser(() => harness.Hosts[1]);
            harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(-10);
            BrowserResponse landed = await Fragments.LoginAsync(onA, harness.App("shell"), "/", ct);
            harness.Idp.Faults.AccessTokenLifetime = TimeSpan.FromMinutes(5);
            if (landed.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException($"the sign-in on A ended at {landed}.");
            }

            // B's browser holds the same cookie and has the shell open, so its calls are the page's fetches.
            onB.Cookies.Add(onA.Cookies.GetAllCookies());
            BrowserResponse page = await onB.NavigateAsync(harness.App("shell").Url("/"), ct: ct);
            if (page.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException($"the shell on B answered {page}.");
            }
            return new TwoHostShell(harness, clock, onA, onB, Assert.Single(harness.SessionStore!.Created));
        }
        catch
        {
            await harness.DisposeAsync();
            throw;
        }
    }

    public TestBrowser BrowserOn(GatewayHost host) => ReferenceEquals(host, A) ? OnA : OnB;

    /// <summary>The record's version; throws when there is no record (<see cref="RecordAsync"/> reads one that may be gone).</summary>
    public async Task<long> VersionAsync(CancellationToken ct) =>
        (await Harness.SessionStore!.GetAsync(Key, ct) ?? throw new InvalidOperationException("the session record is gone.")).Version;

    public async Task<SessionRecord?> RecordAsync(CancellationToken ct) => await Harness.SessionStore!.GetAsync(Key, ct);

    /// <summary>
    /// A call to the echo API through the shell's route or a fragment's on <paramref name="host"/>: the
    /// access token the upstream received, or null when the call did not reach it.
    /// </summary>
    public async Task<string?> EchoAsync(GatewayHost host, string? fragmentId, CancellationToken ct) =>
        (await CallAsync(host, fragmentId, ct)).Token;

    /// <summary>As <see cref="EchoAsync"/>, with the gateway's answer.</summary>
    public async Task<(BrowserResponse Response, string? Token)> CallAsync(GatewayHost host, string? fragmentId, CancellationToken ct)
    {
        string marker = "/items/" + Guid.NewGuid().ToString("N");
        string route = fragmentId is null ? "/api/echo" : $"/fragments/{fragmentId}/api/echo";
        BrowserResponse response = await BrowserOn(host).GetAsync(Shell.Url(route + marker).ToString(), ct: ct);
        Platform.Testing.EchoedRequest? received = Harness.Echo.Requests.SingleOrDefault(request => request.Path == marker);
        string? token = received?.Headers["Authorization"].Single()["Bearer ".Length..];
        return (response, token);
    }

    /// <summary>
    /// The fallback on <paramref name="host"/>: <c>GET /bff/login?fragment=orders&amp;returnUrl=/home</c>,
    /// followed through the provider and <c>/signin-oidc/orders</c>, whose callback merges a new orders set
    /// (F') into the record.
    /// </summary>
    public Task<BrowserResponse> FallbackAsync(GatewayHost host, CancellationToken ct) =>
        BrowserOn(host).NavigateAsync(Shell.Url("/bff/login?fragment=orders&returnUrl=%2Fhome"), ct: ct);

    /// <summary><c>POST /bff/logout</c> from the shell on <paramref name="host"/>, stopped at its first answer.</summary>
    public Task<BrowserResponse> LogoutAsync(GatewayHost host, CancellationToken ct) =>
        BrowserOn(host).SubmitFormAsync("/bff/logout", options: new RequestOptions { Redirect = RedirectMode.Manual }, ct: ct);

    /// <summary>The <c>code</c> of a problem body, or null when the body is not one.</summary>
    public static string? Code(BrowserResponse response)
    {
        try
        {
            using JsonDocument body = JsonDocument.Parse(response.Body);
            return body.RootElement.TryGetProperty("code", out JsonElement code) ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The access token the provider issued orders' client by its last authorization code grant: F' after a fallback.</summary>
    public string LastOrdersCodeToken() =>
        Harness.Idp.Grants.Last(g => g.GrantType == "authorization_code" && g.ClientId == Orders.ClientId && g.Ok).AccessToken!;

    public async ValueTask DisposeAsync()
    {
        OnA.Dispose();
        OnB.Dispose();
        await Harness.DisposeAsync();
    }
}
