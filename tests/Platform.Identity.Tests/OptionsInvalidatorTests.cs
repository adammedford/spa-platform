using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// Task 3.6: what a registry swap does to an app's cached options and its sessions. The seams are a
/// request through the framework's <c>UseAuthentication</c>, the framework's own
/// <see cref="IOptionsMonitor{TOptions}"/>, which is how every handler reads its options, and the
/// session store's interface. The registry is <see cref="InMemoryRegistry"/>, swapped by hand.
/// </summary>
public sealed class OptionsInvalidatorTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);
    private static readonly AppDefinition App2 = TestApps.Entry("app2", issuer: IdentityGateway.IssuerName);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_third_app_onboarded_at_runtime_is_404_before_and_logs_in_after_in_the_same_process()
    {
        // S-D1 and R-C1. Before the swap the host is unknown and a build of the app's scheme fails, as
        // it must for a name no entry has (R-D9). That failure is not cached (R-D10), so the first
        // request after the swap builds the new entry. App 1's options instance is untouched.
        AppDefinition app3 = TestApps.Entry("app3", issuer: IdentityGateway.IssuerName);
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);
        IdentityGateway.Login app1 = await gateway.LoginAsync(App1);
        OpenIdConnectOptions app1Oidc = Oidc(gateway, App1);

        Assert.Equal(404, (await gateway.SendAsync(app3, "/", headers: IdentityGateway.Navigation)).Status);
        gateway.Pin(App1);
        Assert.Throws<OptionsValidationException>(() =>
            gateway.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(SchemeNames.Oidc(app3.AppId)));

        await gateway.SwapAsync(App1, App2, app3);

        IdentityGateway.Login login = await gateway.LoginAsync(app3);
        Assert.Equal("/", login.Callback.Location);
        Assert.Equal(200, (await gateway.SendAsync(app3, IdentityGateway.ApiPath, login.Jar)).Status);
        Assert.Same(app1Oidc, Oidc(gateway, App1));
        Assert.Equal(200, (await gateway.SendAsync(App1, IdentityGateway.ApiPath, app1.Jar)).Status);
    }

    [Fact]
    public async Task A_name_whose_build_threw_is_built_again_on_the_next_read_with_no_swap()
    {
        // R-D10 no longer reproduces. The framework's cache keeps a failed build's exception and rethrows
        // it on every later read of the name until the name is evicted. Here the secret is not readable
        // for the first build, then is, and the next read builds afresh without anything evicting it.
        AppDefinition late = TestApps.Entry(
            "app3", issuer: IdentityGateway.IssuerName, clientSecretRef: "ssm:/spa-platform/poc/apps/app3/client-secret:1");
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);
        await gateway.SwapAsync(App1, App2, late);
        Assert.Equal(IdentityGateway.SecretOf(late), Oidc(gateway, late).ClientSecret);

        AppDefinition unreadable = late with
        {
            OAuth = late.OAuth with { ClientSecretRef = "ssm:/spa-platform/poc/apps/app3/client-secret:2" },
            Fingerprint = "sha256:app3-unreadable",
        };
        RegistrySnapshot previous = gateway.Registry.Current;
        gateway.Registry.Swap(InMemoryRegistry.SnapshotOf(previous.Revision + 1, "\"r-unreadable\"", [App1, App2, unreadable]));

        Assert.Throws<InvalidOperationException>(() => Oidc(gateway, unreadable));
        Assert.Throws<InvalidOperationException>(() => Oidc(gateway, unreadable));

        gateway.Secrets[unreadable.OAuth.ClientSecretRef] = "secret-two";
        await gateway.Secrets.PreloadAsync([unreadable.OAuth.ClientSecretRef], Ct);

        Assert.Equal("secret-two", Oidc(gateway, unreadable).ClientSecret);
    }

    [Fact]
    public async Task An_app_that_did_not_change_keeps_its_cached_options_and_a_changed_one_is_rebuilt()
    {
        // S-D2 and R-C1: new snapshot objects, but app 1's fingerprint is the same, so its instances are.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);
        OpenIdConnectOptions app1Oidc = Oidc(gateway, App1);
        CookieAuthenticationOptions app1Cookie = Cookie(gateway, App1);
        OpenIdConnectOptions app2Oidc = Oidc(gateway, App2);

        AppDefinition app2Moved = App2 with { Release = "2026-10-16-def5678", Fingerprint = "sha256:app2-moved" };
        await gateway.SwapAsync(App1 with { }, app2Moved);

        Assert.Same(app1Oidc, Oidc(gateway, App1));
        Assert.Same(app1Cookie, Cookie(gateway, App1));
        Assert.NotSame(app2Oidc, Oidc(gateway, app2Moved));
    }

    [Fact]
    public async Task A_swap_evicts_the_cached_options_of_a_changed_or_removed_entry_and_keeps_the_rest()
    {
        // GLOSSARY.md, options invalidator: an entry whose fingerprint changed or disappeared leaves the
        // cache at the swap, not when its name is next read. The framework's own cache interface shows
        // which names it still holds: TryAdd succeeds only for a name it does not.
        AppDefinition app3 = TestApps.Entry("app3", issuer: IdentityGateway.IssuerName);
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2, app3]);
        foreach (AppDefinition app in new[] { App1, App2, app3 })
        {
            Oidc(gateway, app);
            Cookie(gateway, app);
        }

        await gateway.SwapAsync(App1, app3 with { Fingerprint = "sha256:app3-changed" });

        var oidc = gateway.Services.GetRequiredService<IOptionsMonitorCache<OpenIdConnectOptions>>();
        var cookies = gateway.Services.GetRequiredService<IOptionsMonitorCache<CookieAuthenticationOptions>>();
        Assert.False(oidc.TryAdd(SchemeNames.Oidc("app1"), new OpenIdConnectOptions()));
        Assert.False(cookies.TryAdd(SchemeNames.Cookie("app1"), new CookieAuthenticationOptions()));
        foreach (string appId in new[] { "app2", "app3" })
        {
            Assert.True(oidc.TryAdd(SchemeNames.Oidc(appId), new OpenIdConnectOptions()));
            Assert.True(cookies.TryAdd(SchemeNames.Cookie(appId), new CookieAuthenticationOptions()));
        }
    }

    [Fact]
    public async Task A_rotated_secret_is_used_after_the_swap_and_the_existing_session_survives()
    {
        // S-D3 and R-C2. The provider rotates first, as an operator would, and until the registry moves
        // on the cached options still carry the stale secret: the login fails closed at the token
        // endpoint. After the swap the new secret is read and the earlier session is still good.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1, App2], RemoteFailureRecorder.Install(out RemoteFailureRecorder recorder));
        IdentityGateway.Login before = await gateway.LoginAsync(App1);

        AppDefinition rotated = App1 with
        {
            OAuth = App1.OAuth with { ClientSecretRef = "ssm:/spa-platform/poc/apps/app1/client-secret:2" },
            Fingerprint = "sha256:app1-rotated",
        };
        gateway.Idp.SetClientSecret(App1.OAuth.ClientId, "rotated-secret");
        gateway.Secrets[rotated.OAuth.ClientSecretRef] = "rotated-secret";

        IdentityGateway.Login stale = await gateway.LoginAsync(App1);
        Assert.NotEqual("/", stale.Callback.Location);
        Assert.Single(recorder.Failures);
        Assert.Contains(gateway.Idp.Grants, g => g.ClientId == App1.OAuth.ClientId && g.Outcome == "invalid_client");

        await gateway.SwapAsync(rotated, App2);

        IdentityGateway.Login after = await gateway.LoginAsync(rotated);
        Assert.Equal("/", after.Callback.Location);
        Assert.Equal(200, (await gateway.SendAsync(rotated, IdentityGateway.ApiPath, before.Jar)).Status);
        Assert.Equal(2, gateway.Sessions.Created.Count);
        foreach (string key in gateway.Sessions.Created)
        {
            Assert.NotNull(await gateway.Sessions.GetAsync(key, Ct));
        }
    }

    [Fact]
    public async Task A_login_started_before_a_reload_completes_after_it()
    {
        // S-D4 and R-C3: the entry changes but keeps its client, so app 1's options are rebuilt between
        // the challenge and the callback. State, correlation, nonce and the PKCE verifier all survive.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);
        OpenIdConnectOptions built = Oidc(gateway, App1);
        IdentityGateway.Authorized started = await gateway.AuthorizeAsync(App1);

        AppDefinition moved = App1 with { Release = "2026-10-16-def5678", Fingerprint = "sha256:app1-moved" };
        await gateway.SwapAsync(moved, App2);
        Assert.NotSame(built, Oidc(gateway, moved));

        IdentityGateway.Answer callback = await gateway.SendAsync(moved, started.Callback.PathAndQuery, started.LoginState);

        Assert.Equal("/", callback.Location);
        TestIdp.TokenGrant grant = Assert.Single(gateway.Idp.Grants);
        Assert.True(grant.Ok);
        Assert.Equal(200, (await gateway.SendAsync(moved, IdentityGateway.ApiPath, Jar(callback))).Status);
    }

    [Fact]
    public async Task A_client_replaced_mid_login_is_refused_its_sessions_stop_and_a_fresh_login_works()
    {
        // S-D5, R-C4, S-D6 and N-22. No guard of the gateway's own refuses the old login: the rebuilt
        // options redeem the code as the new client and the provider refuses it, because a code is bound
        // to the client it was issued to. The old client's session is purged and, purged or not, is
        // bound to the old client, so it is anonymous.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1, App2], RemoteFailureRecorder.Install(out RemoteFailureRecorder recorder));
        IdentityGateway.Login old = await gateway.LoginAsync(App1);
        IdentityGateway.Login other = await gateway.LoginAsync(App2);
        IdentityGateway.Authorized started = await gateway.AuthorizeAsync(App1);

        AppDefinition replaced = App1 with
        {
            OAuth = App1.OAuth with
            {
                ClientId = "client-app1-b",
                ClientSecretRef = "ssm:/spa-platform/poc/apps/app1/client-secret-b:1",
            },
            Fingerprint = "sha256:app1-client-b",
        };
        await gateway.SwapAsync(replaced, App2);

        IdentityGateway.Answer refused = await gateway.SendAsync(replaced, started.Callback.PathAndQuery, started.LoginState);
        Assert.NotEqual("/", refused.Location);
        Assert.IsType<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectProtocolException>(Assert.Single(recorder.Failures));
        Assert.Contains(gateway.Idp.Grants, g => (g.ClientId, g.Outcome) == ("client-app1-b", "invalid_grant"));

        Assert.Equal(401, (await gateway.SendAsync(replaced, IdentityGateway.ApiPath, old.Jar)).Status);
        Assert.Null(await gateway.Sessions.GetAsync(gateway.Sessions.Created[0], Ct));
        Assert.Equal(200, (await gateway.SendAsync(App2, IdentityGateway.ApiPath, other.Jar)).Status);

        IdentityGateway.Login fresh = await gateway.LoginAsync(replaced);
        Assert.Equal("/", fresh.Callback.Location);
        Assert.Equal(200, (await gateway.SendAsync(replaced, IdentityGateway.ApiPath, fresh.Jar)).Status);
        Assert.Equal("client-app1-b", gateway.Idp.Grants[^1].ClientId);
    }

    [Fact]
    public async Task A_removed_app_answers_404_its_sessions_are_purged_and_other_apps_are_unaffected()
    {
        // S-D7, R-C5 and N-22. The 404 for the host and its callback is host resolution's, stood in for by
        // the harness; the identity half is the purge, and that a request still pinned to the snapshot
        // the app was in finds its session gone.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);
        IdentityGateway.Login app1 = await gateway.LoginAsync(App1);
        IdentityGateway.Login app2 = await gateway.LoginAsync(App2);
        IdentityGateway.Authorized started = await gateway.AuthorizeAsync(App2);

        RegistrySnapshot before = await gateway.SwapAsync(App1);

        Assert.Equal(404, (await gateway.SendAsync(App2, "/", app2.Jar)).Status);
        Assert.Equal(404, (await gateway.SendAsync(App2, started.Callback.PathAndQuery, started.LoginState)).Status);
        Assert.Null(await gateway.Sessions.GetAsync(gateway.Sessions.Created[1], Ct));
        Assert.Equal(401, (await gateway.SendAsync(App2, IdentityGateway.ApiPath, app2.Jar, pinnedTo: before)).Status);

        Assert.Equal(200, (await gateway.SendAsync(App1, IdentityGateway.ApiPath, app1.Jar)).Status);
        Assert.NotNull(await gateway.Sessions.GetAsync(gateway.Sessions.Created[0], Ct));
    }

    [Fact]
    public async Task A_request_still_pinned_to_the_old_snapshot_does_not_leave_its_options_cached_for_the_new_one()
    {
        // Swap, then evict, leaves one window: a request host resolution pinned before the swap builds
        // its options after the eviction, from the entry it was pinned to. It gets that entry's options,
        // so it never straddles the swap, but they are not left for the requests pinned after it.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);
        AppDefinition replaced = App1 with
        {
            OAuth = App1.OAuth with { ClientId = "client-app1-b" },
            Fingerprint = "sha256:app1-client-b",
        };
        RegistrySnapshot before = await gateway.SwapAsync(replaced, App2);

        Assert.Equal(App1.OAuth.ClientId, Oidc(gateway, App1, before).ClientId);
        Assert.Equal(App1.OAuth.ClientId, Oidc(gateway, App1, before).ClientId);
        Assert.Equal("client-app1-b", Oidc(gateway, replaced).ClientId);
        Assert.Equal(App1.OAuth.ClientId, Oidc(gateway, App1, before).ClientId);

        Assert.Equal(401, (await gateway.SendAsync(App1, IdentityGateway.ApiPath, pinnedTo: before)).Status);
        IdentityGateway.Login fresh = await gateway.LoginAsync(replaced);
        Assert.Equal("/", fresh.Callback.Location);
        Assert.Equal(200, (await gateway.SendAsync(replaced, IdentityGateway.ApiPath, fresh.Jar)).Status);
    }

    [Fact]
    public async Task One_swap_that_rotates_a_secret_and_removes_an_app_does_both()
    {
        // The identity half of F-09: the new secret is used, the removed host's sessions are gone, and the
        // rotated app's session is not. That the bad document is ignored is the registry's half: it never
        // reaches a swap, so nothing here runs.
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([App1, App2]);
        IdentityGateway.Login app1 = await gateway.LoginAsync(App1);
        IdentityGateway.Login app2 = await gateway.LoginAsync(App2);

        AppDefinition rotated = App1 with
        {
            OAuth = App1.OAuth with { ClientSecretRef = "ssm:/spa-platform/poc/apps/app1/client-secret:2" },
            Fingerprint = "sha256:app1-rotated",
        };
        gateway.Idp.SetClientSecret(App1.OAuth.ClientId, "rotated-secret");
        gateway.Secrets[rotated.OAuth.ClientSecretRef] = "rotated-secret";
        await gateway.SwapAsync(rotated);

        Assert.Equal("rotated-secret", Oidc(gateway, rotated).ClientSecret);
        Assert.Equal("/", (await gateway.LoginAsync(rotated)).Callback.Location);
        Assert.Equal(200, (await gateway.SendAsync(rotated, IdentityGateway.ApiPath, app1.Jar)).Status);
        Assert.Equal(404, (await gateway.SendAsync(App2, IdentityGateway.ApiPath, app2.Jar)).Status);
        Assert.Null(await gateway.Sessions.GetAsync(gateway.Sessions.Created[1], Ct));
    }

    [Fact]
    public async Task A_purge_that_fails_leaves_the_swap_standing_and_the_other_apps_evicted_and_purged()
    {
        // Plan row 3.6: the purge runs inside the registry's synchronous swap callback, so a session store
        // that throws must not escape into the swap, nor stop the eviction or purge of the other apps.
        FailingPurgeStore store = new("app1");
        AppDefinition app3 = TestApps.Entry("app3", issuer: IdentityGateway.IssuerName);
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1, App2, app3], services => services.AddSingleton<ISessionStore>(store));
        foreach (AppDefinition app in new[] { App1, App2, app3 })
        {
            Oidc(gateway, app);
        }

        await gateway.SwapAsync(app3 with { Fingerprint = "sha256:app3-changed" });

        Assert.Equal(["app1", "app2"], store.Purged.Order(StringComparer.Ordinal));
        var oidc = gateway.Services.GetRequiredService<IOptionsMonitorCache<OpenIdConnectOptions>>();
        foreach (string appId in new[] { "app1", "app2", "app3" })
        {
            Assert.True(oidc.TryAdd(SchemeNames.Oidc(appId), new OpenIdConnectOptions()));
        }
    }

    private static OpenIdConnectOptions Oidc(IdentityGateway gateway, AppDefinition app, RegistrySnapshot? pinnedTo = null)
    {
        gateway.Pin(app, pinnedTo);
        return gateway.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(SchemeNames.Oidc(app.AppId));
    }

    private static Dictionary<string, string> Jar(IdentityGateway.Answer callback) => new(StringComparer.Ordinal)
    {
        [callback.SessionCookie.Name.Value!] = callback.SessionCookie.Value.Value!,
    };

    private static CookieAuthenticationOptions Cookie(IdentityGateway gateway, AppDefinition app)
    {
        gateway.Pin(app);
        return gateway.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(SchemeNames.Cookie(app.AppId));
    }
}

/// <summary>A session store whose purge of one app throws, and which records every purge it was asked for.</summary>
internal sealed class FailingPurgeStore(string failingAppId) : ISessionStore
{
    private readonly InMemorySessionStore inner = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> purged = new();

    public IReadOnlyList<string> Purged => [.. purged];

    public Task<string> CreateAsync(SessionRecord record, CancellationToken ct) => inner.CreateAsync(record, ct);

    public Task<SessionRecord?> GetAsync(string key, CancellationToken ct) => inner.GetAsync(key, ct);

    public Task<bool> ReplaceAsync(string key, SessionRecord record, long expectedVersion, CancellationToken ct) =>
        inner.ReplaceAsync(key, record, expectedVersion, ct);

    public Task RemoveAsync(string key, CancellationToken ct) => inner.RemoveAsync(key, ct);

    public Task RemoveAppAsync(string appId, CancellationToken ct)
    {
        purged.Enqueue(appId);
        return appId == failingAppId
            ? Task.FromException(new InvalidOperationException("The session store is unavailable."))
            : inner.RemoveAppAsync(appId, ct);
    }
}
