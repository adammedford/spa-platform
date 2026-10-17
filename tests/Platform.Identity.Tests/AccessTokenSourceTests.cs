using System.Text.Json;
using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;
using Platform.Testing;
using TestIdp;

namespace Platform.Identity.Tests;

/// <summary>
/// Task 3.8: <see cref="IAccessTokenSource"/> through a login at the test identity provider, asked for
/// the token the way the proxy's token step asks (<see cref="IdentityGateway.TokenPath"/>).
///
/// A token is made due by logging in while the provider issues 30-second access tokens, inside the
/// one-minute refresh margin, rather than by moving a clock: the token library computes a new token's
/// expiry from the system clock, not from an injected one (T-B10).
/// </summary>
public sealed class AccessTokenSourceTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);
    private static readonly AppDefinition App2 = TestApps.Entry("app2", issuer: IdentityGateway.IssuerName);

    private static readonly TimeSpan Due = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task A_due_token_is_refreshed_once_and_the_session_keeps_the_rotated_refresh_token()
    {
        // F-03, at the hermetic tier: the call succeeds after one refresh, the provider counts one grant,
        // and the stored refresh token has changed.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        Dictionary<string, string> jar = await LoginDueAsync(gateway, App1);
        string loginRefresh = await StoredTokenAsync(gateway, "refresh_token");

        IdentityGateway.Answer answer = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        Assert.Equal(200, answer.Status);
        TokenGrant grant = Assert.Single(gateway.Idp.RefreshGrants);
        Assert.True(grant.Ok);
        Assert.Equal(loginRefresh, grant.RefreshTokenUsed);
        Assert.Equal(grant.AccessToken, answer.Body);
        Assert.Equal(grant.RefreshTokenIssued, await StoredTokenAsync(gateway, "refresh_token"));
        Assert.NotEqual(loginRefresh, await StoredTokenAsync(gateway, "refresh_token"));
    }

    [Fact]
    public async Task A_fresh_token_is_served_from_the_session_without_a_grant()
    {
        // The one-minute margin of plan row 3.8, from the side that must not refresh.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        IdentityGateway.Login login = await gateway.LoginAsync(App1);
        string loginAccess = await StoredTokenAsync(gateway, "access_token");

        IdentityGateway.Answer first = await gateway.SendAsync(App1, IdentityGateway.TokenPath, login.Jar);
        IdentityGateway.Answer second = await gateway.SendAsync(App1, IdentityGateway.TokenPath, login.Jar);

        Assert.Equal((200, loginAccess), (first.Status, first.Body));
        Assert.Equal((200, loginAccess), (second.Status, second.Body));
        Assert.Empty(gateway.Idp.RefreshGrants);
    }

    [Fact]
    public async Task A_refresh_rewrites_the_same_session_and_the_login_cookie_still_identifies_it()
    {
        // S-E2: same key, same absolute expiry, no cookie issued by the refresh, and the cookie from the
        // login still authenticates afterwards. The library's own store would have signed in again and
        // re-issued the cookie (T-B9); this source writes the record instead.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        Dictionary<string, string> jar = await LoginDueAsync(gateway, App1);
        (string key, SessionRecord atLogin) = await gateway.Sessions.SingleAsync();

        IdentityGateway.Answer refreshed = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        Assert.Equal(200, refreshed.Status);
        Assert.Single(gateway.Idp.RefreshGrants);
        Assert.Empty(refreshed.Cookies);
        (string keyAfter, SessionRecord afterRefresh) = await gateway.Sessions.SingleAsync();
        Assert.Equal(key, keyAfter);
        Assert.Equal(atLogin.ExpiresAt, afterRefresh.ExpiresAt);
        Assert.Equal(atLogin.Version + 1, afterRefresh.Version);
        Assert.Equal(refreshed.Body, await StoredTokenAsync(gateway, "access_token"));

        IdentityGateway.Answer shell = await gateway.SendAsync(App1, "/", jar);
        Assert.Equal(200, shell.Status);
        Assert.Equal(SessionRef.Of(key), shell.SessionRef);
    }

    [Fact]
    public async Task The_session_key_reaches_no_log_line_and_its_reference_reaches_the_request()
    {
        // Task 3.5a, contracts/telemetry.md: session_ref is this workstream's to set, on the request
        // telemetry feature, as SessionRef.Of(key). N-16: across a session's life (sign-in, a refresh,
        // a request, logout) the key itself is written to no log line; its reference is what a line
        // that names the session carries.
        CapturingLoggerProvider logs = new();
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1], services => services.AddSingleton<ILoggerProvider>(logs));
        Dictionary<string, string> jar = await LoginDueAsync(gateway, App1);
        (string key, _) = await gateway.Sessions.SingleAsync();

        IdentityGateway.Answer refreshed = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);
        IdentityGateway.Answer shell = await gateway.SendAsync(App1, "/", jar);
        await gateway.SendAsync(App1, "/bff/logout", jar, "POST");

        Assert.Equal(200, refreshed.Status);
        Assert.Equal(SessionRef.Of(key), refreshed.SessionRef);
        Assert.Equal(SessionRef.Of(key), shell.SessionRef);
        string refresh = Assert.Single(logs.Lines, l => l.Category == typeof(AccessTokenSource).FullName).Message;
        Assert.Contains(SessionRef.Of(key), refresh, StringComparison.Ordinal);
        Assert.All(logs.Lines, line => Assert.DoesNotContain(key, line.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Each_app_refreshes_with_its_own_client_and_refresh_token_whichever_app_asks_first()
    {
        // T-A1, the pinning test: the first refresh is on app 2, then app 1's uses client 1 and app 1's
        // refresh token. S-E3: the token handed back for each app is the one issued to that app's client.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);
        Dictionary<string, string> jar1 = await LoginDueAsync(gateway, App1);
        Dictionary<string, string> jar2 = await LoginDueAsync(gateway, App2);
        TokenGrant[] logins = [.. gateway.Idp.Grants];

        IdentityGateway.Answer second = await gateway.SendAsync(App2, IdentityGateway.TokenPath, jar2);
        IdentityGateway.Answer first = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar1);

        Assert.Equal(200, second.Status);
        Assert.Equal(200, first.Status);
        Assert.Collection(gateway.Idp.RefreshGrants,
            g =>
            {
                Assert.Equal((App2.OAuth.ClientId, "ok"), (g.ClientId, g.Outcome));
                Assert.Equal(logins.Single(l => l.ClientId == App2.OAuth.ClientId).RefreshTokenIssued, g.RefreshTokenUsed);
                Assert.Equal(g.AccessToken, second.Body);
            },
            g =>
            {
                Assert.Equal((App1.OAuth.ClientId, "ok"), (g.ClientId, g.Outcome));
                Assert.Equal(logins.Single(l => l.ClientId == App1.OAuth.ClientId).RefreshTokenIssued, g.RefreshTokenUsed);
                Assert.Equal(g.AccessToken, first.Body);
            });
        Assert.All(gateway.Idp.RefreshGrants, g => Assert.False(g.SentResource));
    }

    [Fact]
    public async Task Fifty_parallel_calls_on_a_due_token_share_one_refresh_and_the_session_survives()
    {
        // F-10, at the 10-second grace window task 1.8 gives Cognito. The provider holds every token
        // request for 200 ms so the fifty calls overlap. One refresh at a time per session (ADR-0017)
        // makes the grant count exactly one, where the contract allows more for a late replay.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        Dictionary<string, string> jar = await LoginDueAsync(gateway, App1);
        gateway.Idp.Faults.RefreshRotationGrace = TimeSpan.FromSeconds(10);
        gateway.Idp.Faults.TokenDelay = TimeSpan.FromMilliseconds(200);

        IdentityGateway.Answer[] answers = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => gateway.SendAsync(App1, IdentityGateway.TokenPath, jar), TestContext.Current.CancellationToken)));

        TokenGrant grant = Assert.Single(gateway.Idp.RefreshGrants);
        Assert.True(grant.Ok);
        Assert.All(answers, a => Assert.Equal((200, grant.AccessToken), (a.Status, a.Body)));
        Assert.Equal(grant.RefreshTokenIssued, await StoredTokenAsync(gateway, "refresh_token"));
        Assert.Equal(0, gateway.Services.GetRequiredService<SessionGates>().Count);
    }

    [Fact]
    public async Task A_refused_refresh_ends_the_session_and_the_next_request_is_anonymous()
    {
        // T-A8: invalid_grant is a refusal. The library ends no session itself; the source does.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        Dictionary<string, string> jar = await LoginDueAsync(gateway, App1);
        (string key, _) = await gateway.Sessions.SingleAsync();
        gateway.Idp.Faults.RefuseRefresh = true;

        IdentityGateway.Answer refused = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        Assert.Equal((401, "session_ended"), (refused.Status, refused.Body));
        Assert.Equal("invalid_grant", Assert.Single(gateway.Idp.RefreshGrants).Outcome);
        Assert.Null(await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));
        Assert.Contains(refused.Cookies, c => c.Name == CookieOptionsSetup.CookieName && c.Expires < DateTimeOffset.UtcNow);

        IdentityGateway.Answer after = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);
        Assert.Equal((401, "unauthenticated"), (after.Status, after.Body));
        Assert.Equal(302, (await gateway.SendAsync(App1, "/", jar, headers: IdentityGateway.Navigation)).Status);
    }

    [Fact]
    public async Task A_due_token_with_no_refresh_token_ends_the_session_without_a_grant()
    {
        // Decision 5 of the landing: a session whose access token is due and which holds no refresh token
        // can never yield a token again, so it ends rather than being kept to fail every call.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        Dictionary<string, string> jar = await LoginDueAsync(gateway, App1);
        (string key, _) = await gateway.Sessions.SingleAsync();
        await RemoveTokenElsewhereAsync(gateway, key, "refresh_token");

        IdentityGateway.Answer answer = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        Assert.Equal((401, "session_ended"), (answer.Status, answer.Body));
        Assert.Empty(gateway.Idp.RefreshGrants);
        Assert.Null(await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(503)]
    [InlineData(500)]
    public async Task A_provider_failure_other_than_a_refusal_fails_the_call_and_keeps_the_session(int status)
    {
        // T-A9: an outage is not a refusal. The call fails with ProviderUnavailable, which the proxy
        // answers 503 idp_unavailable (change request 4.2); the session stays, record and cookie alike,
        // and the next call refreshes.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        Dictionary<string, string> jar = await LoginDueAsync(gateway, App1);
        (string key, SessionRecord atLogin) = await gateway.Sessions.SingleAsync();
        gateway.Idp.Faults.RefreshOutageStatus = status;

        IdentityGateway.Answer failed = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        Assert.Equal((503, "idp_unavailable"), (failed.Status, failed.Body));
        Assert.Empty(failed.Cookies);
        Assert.Equal(atLogin, await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));

        gateway.Idp.Faults.RefreshOutageStatus = null;
        IdentityGateway.Answer recovered = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);
        Assert.Equal(200, recovered.Status);
        Assert.Equal(["http_" + status, "ok"], gateway.Idp.RefreshGrants.Select(g => g.Outcome));
    }

    [Fact]
    public async Task Calls_queued_behind_a_failed_refresh_share_its_failure_instead_of_asking_again()
    {
        // T-D6: fifty calls on a due token at a provider that answers 503 after 200 ms ask it once, and
        // every one of them answers ProviderUnavailable with the session kept.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        Dictionary<string, string> jar = await LoginDueAsync(gateway, App1);
        gateway.Idp.Faults.RefreshOutageStatus = 503;
        gateway.Idp.Faults.TokenDelay = TimeSpan.FromMilliseconds(200);

        IdentityGateway.Answer[] answers = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => gateway.SendAsync(App1, IdentityGateway.TokenPath, jar), TestContext.Current.CancellationToken)));

        Assert.All(answers, a => Assert.Equal((503, "idp_unavailable"), (a.Status, a.Body)));
        Assert.Single(gateway.Idp.RefreshGrants);
        Assert.Single(gateway.Sessions.Created);
        Assert.NotNull((await gateway.Sessions.SingleAsync()).Record);
    }

    [Fact]
    public async Task A_refusal_after_another_writer_refreshed_uses_the_newer_token_and_keeps_the_session()
    {
        // T-B8, at a provider with no grace window. Between this call's read of the session and its
        // grant, another writer (another gateway task sharing the store) refreshes the session: the
        // provider then refuses the rotated-out token this call presents. Re-reading the session finds
        // the successor, and its access token is the answer.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        Dictionary<string, string> jar = await LoginDueAsync(gateway, App1);
        (string key, _) = await gateway.Sessions.SingleAsync();
        gateway.Idp.Faults.RefreshRotationGrace = TimeSpan.Zero;

        // The first read is the cookie handler's, as it authenticates; the second is the source's own,
        // under the session's gate.
        int reads = 0;
        gateway.Sessions.AfterRead = async _ =>
        {
            if (++reads == 2)
            {
                gateway.Sessions.AfterRead = null;
                await RefreshElsewhereAsync(gateway, App1, key);
            }
        };

        IdentityGateway.Answer answer = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        Assert.Collection(gateway.Idp.RefreshGrants,
            g => Assert.Equal("ok", g.Outcome),
            g => Assert.Equal("invalid_grant", g.Outcome));
        TokenGrant other = gateway.Idp.RefreshGrants[0];
        Assert.Equal(gateway.Idp.RefreshGrants[1].RefreshTokenUsed, other.RefreshTokenUsed);
        Assert.Equal((200, other.AccessToken), (answer.Status, answer.Body));
        Assert.Equal(other.RefreshTokenIssued, await StoredTokenAsync(gateway, "refresh_token", key));
    }

    [Fact]
    public async Task A_successful_refresh_that_loses_the_session_version_race_uses_the_stored_winner()
    {
        // T-D2: another gateway may update the session after this gateway's read. A successful
        // provider response cannot override the versioned winner or return an unpersisted token.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        Dictionary<string, string> jar = await LoginDueAsync(gateway, App1);
        (string key, _) = await gateway.Sessions.SingleAsync();
        int reads = 0;
        gateway.Sessions.AfterRead = async _ =>
        {
            if (++reads == 2)
            {
                gateway.Sessions.AfterRead = null;
                await RefreshElsewhereAsync(gateway, App1, key);
            }
        };

        IdentityGateway.Answer answer = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        Assert.Equal(2, gateway.Idp.RefreshGrants.Count);
        Assert.All(gateway.Idp.RefreshGrants, grant => Assert.True(grant.Ok));
        Assert.Equal(await StoredTokenAsync(gateway, "access_token", key), answer.Body);
    }

    [Fact]
    public async Task A_refresh_that_loses_the_race_to_a_record_with_no_access_token_fails_the_call_and_keeps_that_record()
    {
        // T-D2's other branch: the versioned winner holds no access token, and this call's own token was
        // never persisted, so the call has no token it may hand back. It fails with ProviderUnavailable,
        // the winner stays as it was written, and the source's warning names the session by reference.
        CapturingLoggerProvider logs = new();
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1], services => services.AddSingleton<ILoggerProvider>(logs));
        Dictionary<string, string> jar = await LoginDueAsync(gateway, App1);
        (string key, _) = await gateway.Sessions.SingleAsync();
        SessionRecord? winner = null;
        int reads = 0;
        gateway.Sessions.AfterRead = async _ =>
        {
            if (++reads == 2)
            {
                gateway.Sessions.AfterRead = null;
                winner = await RemoveTokenElsewhereAsync(gateway, key, "access_token");
            }
        };

        IdentityGateway.Answer answer = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        Assert.Equal((503, "idp_unavailable"), (answer.Status, answer.Body));
        Assert.True(Assert.Single(gateway.Idp.RefreshGrants).Ok);
        Assert.Equal(winner, await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));
        string warning = Assert.Single(SourceLines(logs), line => line.Contains("no access token", StringComparison.Ordinal));
        Assert.Contains(SessionRef.Of(key), warning, StringComparison.Ordinal);
        Assert.DoesNotContain(key, warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_browser_that_disconnects_mid_refresh_does_not_strand_the_rotated_token()
    {
        // T-B16, at a provider with no grace window: the browser goes 100 ms into a refresh the provider
        // answers after 300 ms. The refresh completes and is stored, and the next call needs no grant.
        // Had the request's cancellation token reached the refresh, the provider would have rotated a
        // token the session never saw, and the next refresh would be refused (T-B15).
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1]);
        Dictionary<string, string> jar = await LoginDueAsync(gateway, App1);
        gateway.Idp.Faults.RefreshRotationGrace = TimeSpan.Zero;
        gateway.Idp.Faults.RefreshAnswerDelay = TimeSpan.FromMilliseconds(300);
        using CancellationTokenSource browser = new(TimeSpan.FromMilliseconds(100));

        IdentityGateway.Answer abandoned = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar, browser: browser);

        Assert.True(browser.IsCancellationRequested);
        TokenGrant grant = Assert.Single(gateway.Idp.RefreshGrants);
        Assert.Equal((200, grant.AccessToken), (abandoned.Status, abandoned.Body));
        Assert.Equal(grant.RefreshTokenIssued, await StoredTokenAsync(gateway, "refresh_token"));

        gateway.Idp.Faults.RefreshAnswerDelay = TimeSpan.Zero;
        IdentityGateway.Answer next = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);
        Assert.Equal((200, grant.AccessToken), (next.Status, next.Body));
        Assert.Single(gateway.Idp.RefreshGrants);
    }

    [Theory]
    [InlineData("standard", ClientAuth.Post)]
    [InlineData("cognito", ClientAuth.Basic)]
    public async Task The_refresh_presents_the_secret_the_way_the_issuers_profile_says(string profile, ClientAuth expected)
    {
        // ADR-0017 decision 2: the profile states the credential style and the refresh uses it. The
        // library's own default is the Basic header (T-B4, T-B5), so the standard row fails if the style
        // is not applied, and the cognito row fails if the mapping between the two enums inverts it.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], issuerProfile: profile, stubProfile: false);
        Dictionary<string, string> jar = await LoginDueAsync(gateway, App1);

        Assert.Equal(200, (await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar)).Status);

        TokenGrant grant = Assert.Single(gateway.Idp.RefreshGrants);
        Assert.Equal((expected, "ok"), (grant.Auth, grant.Outcome));
    }

    [Fact]
    public async Task A_refresh_past_its_deadline_fails_the_call_keeps_the_session_and_is_retried_inside_the_grace_window()
    {
        // T-D9: the refresh has a deadline of its own. The call that gives up fails with
        // ProviderUnavailable and keeps the session, and its warning names the deadline. The provider
        // carried the refresh out but answered late, so the retry presents the rotated-out token, which
        // the 10-second grace window honours.
        CapturingLoggerProvider logs = new();
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], services: services =>
        {
            Deadline(TimeSpan.FromMilliseconds(300))(services);
            services.AddSingleton<ILoggerProvider>(logs);
        });
        Dictionary<string, string> jar = await LoginDueAsync(gateway, App1);
        (string key, SessionRecord atLogin) = await gateway.Sessions.SingleAsync();
        gateway.Idp.Faults.RefreshAnswerDelay = TimeSpan.FromSeconds(1);

        IdentityGateway.Answer late = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);
        Assert.Equal((503, "idp_unavailable"), (late.Status, late.Body));
        Assert.Equal(atLogin, await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));

        gateway.Idp.Faults.RefreshAnswerDelay = TimeSpan.Zero;
        IdentityGateway.Answer retried = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        Assert.Equal(200, retried.Status);
        Assert.Contains(SourceLines(logs), line => line.Contains("(deadline_exceeded)", StringComparison.Ordinal));
        Assert.Collection(gateway.Idp.RefreshGrants,
            g => Assert.Equal("ok", g.Outcome),
            g => Assert.True(g is { Ok: true, ReplayedInsideGrace: true }));
    }

    [Fact]
    public async Task A_refresh_given_up_on_at_a_provider_with_no_grace_window_ends_the_session_on_the_retry()
    {
        // T-D10, the deadline's caveat: the refresh the gateway gave up on was carried out, so the retry
        // replays a rotated-out token. With no grace window it is refused, nothing newer is stored, and
        // the session ends. A deadline is safe only with a grace window that covers the retry.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1], services: Deadline(TimeSpan.FromMilliseconds(300)));
        Dictionary<string, string> jar = await LoginDueAsync(gateway, App1);
        gateway.Idp.Faults.RefreshRotationGrace = TimeSpan.Zero;
        gateway.Idp.Faults.RefreshAnswerDelay = TimeSpan.FromSeconds(1);

        IdentityGateway.Answer gaveUp = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);
        gateway.Idp.Faults.RefreshAnswerDelay = TimeSpan.Zero;
        IdentityGateway.Answer retried = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        Assert.Equal((503, "idp_unavailable"), (gaveUp.Status, gaveUp.Body));
        Assert.Equal((401, "session_ended"), (retried.Status, retried.Body));
        Assert.Collection(gateway.Idp.RefreshGrants,
            g => Assert.Equal("ok", g.Outcome),
            g => Assert.Equal("invalid_grant", g.Outcome));
    }

    [Fact]
    public void AddPlatformIdentity_builds_the_token_source_outside_a_request_and_leaves_the_interface_unbound()
    {
        // The library's endpoint is built once per credential style from its own registration, outside
        // any request: with the sentinel the options hook needs none (T-A3). The interface binding is
        // AddPlatformAccessTokenSource's, so a composed gateway forwards sessionless /api calls as before.
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IRegistry>(new InMemoryRegistry(InMemoryRegistry.SnapshotOf(1, "\"r1\"", [App1])));
        builder.Services.AddSingleton<IClientSecretSource>(new FakeClientSecretSource());
        builder.Services.AddSingleton<ISessionStore>(new InMemorySessionStore());
        builder.AddPlatformIdentity();
        using IHost host = builder.Build();

        Assert.NotNull(host.Services.GetRequiredService<AccessTokenSource>());
        Assert.Null(host.Services.GetService<IAccessTokenSource>());

        builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging();
        builder.AddPlatformIdentity();
        builder.Services.AddPlatformAccessTokenSource();
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(IAccessTokenSource));
    }

    [Fact]
    public async Task Provider_error_text_never_reaches_the_answer_or_the_identity_log()
    {
        // N-16: an OAuth error field is provider-controlled, even when it contains a credential. The
        // answer is the bare outcome, and the source's warning carries a fixed category in its place:
        // refresh_failed, for text it does not know.
        CapturingLoggerProvider logs = new();
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1], services => services.AddSingleton<ILoggerProvider>(logs));
        Dictionary<string, string> jar = await LoginDueAsync(gateway, App1);
        string canary = await StoredTokenAsync(gateway, "refresh_token");
        using HttpClient backchannel = new(new ErrorAnswer(canary));
        gateway.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(SchemeNames.Oidc(App1.AppId)).Backchannel = backchannel;

        IdentityGateway.Answer failure = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        Assert.Equal((503, "idp_unavailable"), (failure.Status, failure.Body));
        string warning = Assert.Single(SourceLines(logs));
        Assert.Contains("(refresh_failed)", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, warning, StringComparison.Ordinal);
    }

    private sealed class ErrorAnswer(string error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { error }), System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static Action<IServiceCollection> Deadline(TimeSpan deadline) =>
        services => services.AddSingleton(new AccessTokenSourceOptions { ProviderDeadline = deadline });

    /// <summary>The lines the token source itself wrote, oldest first.</summary>
    private static string[] SourceLines(CapturingLoggerProvider logs) =>
        [.. logs.Lines.Where(line => line.Category == typeof(AccessTokenSource).FullName).Select(line => line.Message)];

    /// <summary>
    /// Refreshes the session at the provider and writes the result into its record, as another gateway
    /// task sharing the session store would: same client, same secret, the form body.
    /// </summary>
    private static async Task RefreshElsewhereAsync(IdentityGateway gateway, AppDefinition app, string key)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SessionRecord record = Assert.IsType<SessionRecord>(await gateway.Sessions.GetAsync(key, ct));
        AuthenticationTicket ticket = Assert.IsType<AuthenticationTicket>(TicketSerializer.Default.Deserialize(record.Ticket.ToArray()));

        using HttpClient client = new();
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = ticket.Properties.GetTokenValue("refresh_token")!,
            ["client_id"] = app.OAuth.ClientId,
            ["client_secret"] = IdentityGateway.SecretOf(app),
        });
        using HttpResponseMessage response = await client.PostAsync(new Uri(gateway.Idp.TokenEndpoint), form, ct);
        response.EnsureSuccessStatusCode();
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        ticket.Properties.UpdateTokenValue("access_token", body.RootElement.GetProperty("access_token").GetString()!);
        ticket.Properties.UpdateTokenValue("refresh_token", body.RootElement.GetProperty("refresh_token").GetString()!);
        Assert.True(await gateway.Sessions.ReplaceAsync(
            key, record with { Version = record.Version + 1, Ticket = TicketSerializer.Default.Serialize(ticket) }, record.Version, ct));
    }

    /// <summary>
    /// Rewrites the session's record one version on without the named token, as another writer sharing
    /// the session store might, and returns what it wrote.
    /// </summary>
    private static async Task<SessionRecord> RemoveTokenElsewhereAsync(IdentityGateway gateway, string key, string name)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        SessionRecord record = Assert.IsType<SessionRecord>(await gateway.Sessions.GetAsync(key, ct));
        AuthenticationTicket ticket = Assert.IsType<AuthenticationTicket>(TicketSerializer.Default.Deserialize(record.Ticket.ToArray()));
        ticket.Properties.StoreTokens(ticket.Properties.GetTokens().Where(t => t.Name != name).ToList());
        SessionRecord next = record with { Version = record.Version + 1, Ticket = TicketSerializer.Default.Serialize(ticket) };
        Assert.True(await gateway.Sessions.ReplaceAsync(key, next, record.Version, ct));
        return next;
    }

    /// <summary>
    /// A login while the provider issues 30-second access tokens, so the session's token is due at once,
    /// after which the provider goes back to five-minute tokens, so one refresh makes it fresh again.
    /// </summary>
    private static async Task<Dictionary<string, string>> LoginDueAsync(IdentityGateway gateway, AppDefinition app)
    {
        gateway.Idp.Faults.AccessTokenLifetime = Due;
        IdentityGateway.Login login = await gateway.LoginAsync(app);
        gateway.Idp.Faults.AccessTokenLifetime = Fresh;
        return login.Jar;
    }

    /// <summary>A token saved in the one session the gateway has stored.</summary>
    private static async Task<string> StoredTokenAsync(IdentityGateway gateway, string name, string? key = null)
    {
        SessionRecord record = key is null
            ? (await gateway.Sessions.SingleAsync()).Record
            : Assert.IsType<SessionRecord>(await gateway.Sessions.GetAsync(key, TestContext.Current.CancellationToken));
        AuthenticationTicket ticket = Assert.IsType<AuthenticationTicket>(TicketSerializer.Default.Deserialize(record.Ticket.ToArray()));
        return Assert.IsType<string>(ticket.Properties.GetTokenValue(name));
    }
}
