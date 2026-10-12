using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Spike;

// Review experiment. The plan's mechanism (host-scoped scheme provider + named options + cache eviction) run
// against the REAL OpenIdConnectHandler built from unmodified ASP.NET Core release/10.0 source, with a hermetic
// OpenID Connect provider (discovery, JWKS, PKCE, signed ID tokens with nonce, RP-initiated logout).
// "OK"/"BAD" marks whether the plan's expectation held; "FINDING" marks a probe that confirms a review finding.

var only = args.Length > 0 ? args[0] : null;
var results = new List<(string Id, string Kind, string Name, string Detail)>();
void Report(string id, string kind, string name, string detail = "")
{
    results.Add((id, kind, name, detail));
    Console.WriteLine($"{kind,-8}{id,-5} {name}{(detail.Length > 0 ? "\n              [" + detail + "]" : "")}");
}
void Check(string id, string name, bool pass, string detail = "") => Report(id, pass ? "OK" : "BAD", name, detail);
void Finding(string id, string name, bool confirmed, string detail = "") => Report(id, confirmed ? "FINDING" : "not-rep", name, detail);
static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
static string Url(string host, string path = "/") => $"https://{host}{path}";
static System.Collections.Specialized.NameValueCollection Query(string url) => System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);

const string H1 = "app1.spa.test", H2 = "app2.spa.test", H3 = "app3.spa.test", SessionCookie = "__Host-spa-session";

var idp = new TestIdp();
await idp.StartAsync();
void RegisterClient(string clientId, string secret, string host)
    => idp.Clients[clientId] = new TestIdp.Client(secret, [$"https://{host}/signin-oidc"], [$"https://{host}/signout-callback-oidc"]);

void ResetClients() { RegisterClient("client-1", "secret-1", H1); RegisterClient("client-2", "secret-2", H2); }   // sections must not leak provider state
ResetClients();
var app1 = new AppEntry("app1", H1, "client-1", "secret-1", "openid profile api1/read", "https://orders.spa.test");
var app2 = new AppEntry("app2", H2, "client-2", "secret-2", "openid api2/read");

async Task<(Gateway Gw, Registry Reg)> NewGateway(Switches sw, params AppEntry[] apps)
{
    var reg = new Registry();
    reg.Publish(apps);
    return (await Gateway.StartAsync(reg, idp.BaseUrl, sw), reg);
}

Console.WriteLine($"runtime {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; handler {typeof(OpenIdConnectHandler).Assembly.GetName().Name}; IdentityModel {typeof(Microsoft.IdentityModel.Tokens.SecurityKey).Assembly.GetName().Version}\n");

// =====================================================================================================
// A. The mechanism with the real handler (what Gate G0 item 1 asks for)
// =====================================================================================================
if (only is null or "A")
{
    ResetClients();
    Console.WriteLine("A. Host-scoped provider with the real OpenIdConnectHandler");
    var (gw, registry) = await NewGateway(new Switches(), app1, app2);
    var cookieOptions = gw.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();

    var probe = new Browser(gw.BaseUrl, idp.BaseUrl);
    var r1 = await probe.Get(Url(H1));
    var loc1 = r1.Headers.Location?.ToString() ?? "";
    var q1 = loc1.Length > 0 ? Query(loc1) : new();
    Check("A1", "app1 challenge: own client, own redirect_uri, code flow, PKCE S256, nonce, scope, resource; no response_mode",
        r1.StatusCode == HttpStatusCode.Redirect && loc1.StartsWith(idp.BaseUrl + "/authorize") && q1["client_id"] == "client-1"
        && q1["redirect_uri"] == $"https://{H1}/signin-oidc" && q1["response_type"] == "code" && q1["code_challenge_method"] == "S256"
        && !string.IsNullOrEmpty(q1["code_challenge"]) && !string.IsNullOrEmpty(q1["nonce"]) && q1["scope"] == "openid profile api1/read"
        && q1["resource"] == "https://orders.spa.test" && q1["response_mode"] is null,
        $"{(int)r1.StatusCode}; params: {string.Join(",", q1.AllKeys)}");
    var r2 = await probe.Get(Url(H2));
    var q2 = Query(r2.Headers.Location!.ToString());
    Check("A2", "app2 challenge: client-2 and its own redirect_uri on the same callback path",
        q2["client_id"] == "client-2" && q2["redirect_uri"] == $"https://{H2}/signin-oidc");

    var loginCookies = probe.AllCookies(H1).Select(c => $"{c.Name[..Math.Min(c.Name.Length, 34)]}… path={c.Path}").ToList();
    Report("A2b", "note", "login-state cookies as the real handler sets them", string.Join(" | ", loginCookies));

    var alice = new Browser(gw.BaseUrl, idp.BaseUrl);
    var before = idp.PkceVerified;
    var (final1, hops1) = await alice.Navigate(Url(H1));
    var body1 = await final1.Content.ReadAsStringAsync();
    Check("A3", "full login on app1 with the real handler (discovery, JWKS, PKCE, signed ID token, nonce) ends on app1's shell",
        final1.StatusCode == HttpStatusCode.OK && body1 == "shell app=app1 sub=alice" && idp.PkceVerified == before + 1,
        $"{(int)final1.StatusCode} '{body1}' via {string.Join(" > ", hops1.Select(h => h.Split('?')[0]))}");
    var (final2, _) = await alice.Navigate(Url(H2));
    var body2 = await final2.Content.ReadAsStringAsync();
    Check("A4", "full login on app2 (same callback path) ends on app2's shell", final2.StatusCode == HttpStatusCode.OK && body2 == "shell app=app2 sub=alice", body2);

    var u1 = await Json(await alice.Get(Url(H1, "/bff/user")));
    var u2 = await Json(await alice.Get(Url(H2, "/bff/user")));
    Check("A5", "each host authenticates through its own default scheme; tokens issued to its own client; resource reached the provider",
        u1.GetProperty("accessToken").GetString()!.StartsWith("at~client-1~alice~") && u1.GetProperty("accessToken").GetString()!.EndsWith("aud=https://orders.spa.test")
        && u2.GetProperty("accessToken").GetString()!.StartsWith("at~client-2~alice~") && u1.GetProperty("middlewareSetUser").GetBoolean());

    var h0 = Interlocked.Read(ref Gateway.OidcHandlersCreated);
    for (var i = 0; i < 50; i++) await alice.Get(Url(H1, "/bff/user"));
    var perRequest = (Interlocked.Read(ref Gateway.OidcHandlersCreated) - h0) / 50.0;
    Check("A6", "exactly one real OpenIdConnectHandler is created per request", perRequest == 1.0, $"{perRequest:0.##} per request");

    Check("A7", "discovery and JWKS were fetched once for both apps (one shared ConfigurationManager)", idp.DiscoveryFetches == 1 && idp.JwksFetches == 1, $"discovery {idp.DiscoveryFetches}, jwks {idp.JwksFetches}");

    var app1Cookie = alice.CookiesFor(H1)[SessionCookie];
    var o1 = cookieOptions.Get(SchemeNames.Cookie("app1"));
    var o2 = cookieOptions.Get(SchemeNames.Cookie("app2"));
    var inCookie = o1.TicketDataFormat.Unprotect(app1Cookie);
    var stored = TicketSerializer.Default.Deserialize(gw.Sessions.All.Single(r => r.AppId == "app1").Ticket)!;
    Check("A8", "SaveTokens + ITicketStore: cookie holds only the session id; access, refresh and ID token sit in the store",
        inCookie is not null && inCookie.Principal.Claims.Count() == 1 && !inCookie.Properties.GetTokens().Any()
        && stored.Properties.GetTokenValue("access_token") is not null && stored.Properties.GetTokenValue("refresh_token") is not null
        && stored.Properties.GetTokenValue("id_token") is not null && stored.Properties.GetTokenValue("expires_at") is not null,
        $"cookie length {app1Cookie.Length}; stored tokens: {string.Join(",", stored.Properties.GetTokens().Select(t => t.Name))}; claims: {string.Join(",", stored.Principal.Claims.Select(c => c.Type))}");
    Check("A9", "browser holds only the session cookie after login (correlation and nonce cookies deleted by the callback)",
        alice.AllCookies(H1).Select(c => c.Name).SequenceEqual([SessionCookie]), string.Join(",", alice.AllCookies(H1).Select(c => c.Name)));

    var replay = await alice.Get(Url(H2, "/bff/user"), new Dictionary<string, string> { [SessionCookie] = app1Cookie });
    Check("A10", "app1's cookie on app2 is anonymous; app2's scheme cannot unprotect it", replay.StatusCode == HttpStatusCode.Unauthorized && o2.TicketDataFormat.Unprotect(app1Cookie) is null);

    await gw.App.StopAsync();
}

// =====================================================================================================
// B. Callback, state, nonce and code substitution, with the real handler's own checks
// =====================================================================================================
if (only is null or "B")
{
    ResetClients();
    Console.WriteLine("\nB. Substitution attempts against the real handler");
    var (gw, registry) = await NewGateway(new Switches(), app1, app2);
    var sessionsBefore = gw.Sessions.Count;

    // B1: app1's callback, with its login cookies, replayed on app2.
    var mallory = new Browser(gw.BaseUrl, idp.BaseUrl);
    var challenge = await mallory.Get(Url(H1));
    var callback = (await mallory.Get(challenge.Headers.Location!.ToString())).Headers.Location!;
    var onApp2 = await mallory.Get(Url(H2, callback.PathAndQuery), mallory.CookiesFor(H1, "/signin-oidc"));
    var onApp2Body = await onApp2.Content.ReadAsStringAsync();
    Check("B1", "app1's callback (code, state, correlation and nonce cookies) replayed on app2 is rejected",
        onApp2.StatusCode == HttpStatusCode.BadRequest && onApp2Body.Contains("Unable to unprotect the message.State"), onApp2Body);

    // B2: a code issued to client-1 injected into app2's own login.
    var eve = new Browser(gw.BaseUrl, idp.BaseUrl);
    var eveChallenge = await eve.Get(Url(H2));
    var state2 = Query(eveChallenge.Headers.Location!.ToString())["state"]!;
    var eveOnApp1 = new Browser(gw.BaseUrl, idp.BaseUrl);
    var c1 = await eveOnApp1.Get(Url(H1));
    var codeForClient1 = Query((await eveOnApp1.Get(c1.Headers.Location!.ToString())).Headers.Location!.ToString())["code"]!;
    var injected = await eve.Get(Url(H2, $"/signin-oidc?code={codeForClient1}&state={Uri.EscapeDataString(state2)}"));
    var injectedBody = await injected.Content.ReadAsStringAsync();
    Check("B2", "a code issued to client-1 injected into app2's own login is refused at the token endpoint",
        injected.StatusCode == HttpStatusCode.BadRequest && idp.LastTokenError == "invalid_grant", $"{injectedBody} (provider said {idp.LastTokenError})");

    // B3: valid callback, no correlation cookie.
    var carol = new Browser(gw.BaseUrl, idp.BaseUrl);
    var carolChallenge = await carol.Get(Url(H1));
    var carolCallback = (await carol.Get(carolChallenge.Headers.Location!.ToString())).Headers.Location!;
    var noCookies = await carol.Get(Url(H1, carolCallback.PathAndQuery), new Dictionary<string, string>());
    var noCookiesBody = await noCookies.Content.ReadAsStringAsync();
    Check("B3", "a valid callback without the correlation cookie is rejected", noCookies.StatusCode == HttpStatusCode.BadRequest && noCookiesBody.Contains("Correlation failed"), noCookiesBody);

    // B4: valid callback, correlation cookie present, nonce cookie missing: the ID token's nonce cannot be matched.
    var dan = new Browser(gw.BaseUrl, idp.BaseUrl);
    var danChallenge = await dan.Get(Url(H1));
    var danCallback = (await dan.Get(danChallenge.Headers.Location!.ToString())).Headers.Location!;
    var onlyCorrelation = dan.CookiesFor(H1, "/").Where(kv => kv.Key.StartsWith("__Host-spa-corr.")).ToDictionary(kv => kv.Key, kv => kv.Value);   // hardened names: __Host- prefix, Path=/
    var noNonce = await dan.Get(Url(H1, danCallback.PathAndQuery), onlyCorrelation);
    var noNonceBody = await noNonce.Content.ReadAsStringAsync();
    Check("B4", "a valid callback without the nonce cookie is rejected by the protocol validator", noNonce.StatusCode == HttpStatusCode.BadRequest && noNonceBody.Contains("IDX21323"), noNonceBody.Length > 160 ? noNonceBody[..160] + "…" : noNonceBody);

    Check("B5", "none of the rejected callbacks created a session", gw.Sessions.Count == sessionsBefore, $"{gw.Sessions.Count} sessions");

    var other = await (await mallory.Get(Url(H1, "/_probe?scheme=oidc:app2"))).Content.ReadAsStringAsync();
    var own = await (await mallory.Get(Url(H1, "/_probe?scheme=oidc:app1"))).Content.ReadAsStringAsync();
    Check("B6", "on app1's host, app2's scheme name does not resolve", other == "absent" && own == "present");
    await gw.App.StopAsync();
}

// =====================================================================================================
// C. Onboarding, rotation, replacement and removal at runtime, real options cache
// =====================================================================================================
if (only is null or "C")
{
    ResetClients();
    Console.WriteLine("\nC. Runtime registry changes with the real handler's options");
    var a1 = app1; var a2 = app2;
    var (gw, registry) = await NewGateway(new Switches(), a1, a2);
    var oidcOptions = gw.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();
    var alice = new Browser(gw.BaseUrl, idp.BaseUrl);
    await alice.Navigate(Url(H1));
    var app1OptionsBefore = oidcOptions.Get(SchemeNames.Oidc("app1"));

    var bob = new Browser(gw.BaseUrl, idp.BaseUrl);
    var pre = await bob.Get(Url(H3));
    RegisterClient("client-3", "secret-3", H3);
    var a3 = new AppEntry("app3", H3, "client-3", "secret-3", "openid api3/read");
    registry.Publish([a1, a2, a3]);
    var (final3, _) = await bob.Navigate(Url(H3));
    Check("C1", "third app onboarded at runtime: 404 before, full login after; app1's cached OpenIdConnectOptions instance untouched",
        pre.StatusCode == HttpStatusCode.NotFound && await final3.Content.ReadAsStringAsync() == "shell app=app3 sub=alice"
        && ReferenceEquals(app1OptionsBefore, oidcOptions.Get(SchemeNames.Oidc("app1"))) && (await alice.Get(Url(H1, "/bff/user"))).StatusCode == HttpStatusCode.OK);

    RegisterClient("client-1", "secret-1b", H1);                       // provider rotates first
    var dave = new Browser(gw.BaseUrl, idp.BaseUrl);
    var (stale, _) = await dave.Navigate(Url(H1));
    var staleBody = await stale.Content.ReadAsStringAsync();
    a1 = a1 with { ClientSecret = "secret-1b" };
    registry.Publish([a1, a2, a3]);
    var (rotated, _) = await dave.Navigate(Url(H1));
    Check("C2", "secret rotated at runtime: stale secret fails closed, new secret picked up after eviction, existing session survives",
        stale.StatusCode == HttpStatusCode.BadRequest && rotated.StatusCode == HttpStatusCode.OK && idp.LastSecretSeenFor("client-1") == "secret-1b"
        && (await alice.Get(Url(H1, "/bff/user"))).StatusCode == HttpStatusCode.OK, staleBody.Length > 120 ? staleBody[..120] + "…" : staleBody);

    var erin = new Browser(gw.BaseUrl, idp.BaseUrl);
    var erinChallenge = await erin.Get(Url(H2));
    RegisterClient("client-2", "secret-2b", H2);
    a2 = a2 with { ClientSecret = "secret-2b" };
    RegisterClient("client-4", "secret-4", "app4.spa.test");
    var a4 = new AppEntry("app4", "app4.spa.test", "client-4", "secret-4", "openid");
    registry.Publish([a1, a2, a3, a4]);
    var erinCallback = (await erin.Get(erinChallenge.Headers.Location!.ToString())).Headers.Location!.ToString();
    var (erinFinal, _) = await erin.Navigate(erinCallback);
    Check("C3", "a login started before a reload completes after it (state, correlation, nonce and PKCE verifier survive eviction)",
        erinFinal.StatusCode == HttpStatusCode.OK && await erinFinal.Content.ReadAsStringAsync() == "shell app=app2 sub=alice");

    // Client replaced mid-login, with NO custom guard: what does the real handler do on its own?
    var frank = new Browser(gw.BaseUrl, idp.BaseUrl);
    var frankChallenge = await frank.Get(Url(H3));
    RegisterClient("client-3b", "secret-3b", H3);
    a3 = a3 with { ClientId = "client-3b", ClientSecret = "secret-3b" };
    registry.Publish([a1, a2, a3, a4]);
    var frankCallback = (await frank.Get(frankChallenge.Headers.Location!.ToString())).Headers.Location!.ToString();
    var frankFail = await frank.Get(frankCallback);
    var bobOld = await bob.Get(Url(H3, "/bff/user"));
    var (frankRetry, _) = await frank.Navigate(Url(H3));
    Check("C4", "client replaced mid-login, no custom guard: code exchange is refused by the provider; old-client sessions stop; fresh login works",
        frankFail.StatusCode == HttpStatusCode.BadRequest && idp.LastTokenError == "invalid_grant" && bobOld.StatusCode == HttpStatusCode.Unauthorized && frankRetry.StatusCode == HttpStatusCode.OK,
        $"callback {(int)frankFail.StatusCode}, provider said {idp.LastTokenError}");

    var app2Sessions = gw.Sessions.CountFor("app2");
    registry.Publish([a1, a3, a4]);
    var gone = await erin.Get(Url(H2));
    var goneCallback = await erin.Get(Url(H2, "/signin-oidc?code=x&state=y"));
    Check("C5", "app removed at runtime: host and its callback path 404, sessions purged, others unaffected",
        gone.StatusCode == HttpStatusCode.NotFound && goneCallback.StatusCode == HttpStatusCode.NotFound && app2Sessions > 0 && gw.Sessions.CountFor("app2") == 0
        && (await alice.Get(Url(H1, "/bff/user"))).StatusCode == HttpStatusCode.OK);

    // RP-initiated logout through the real handler, and the plan's "trap 1".
    var logout = await alice.Send(HttpMethod.Post, Url(H1, "/bff/logout"));
    var endSession = logout.Headers.Location?.ToString() ?? "";
    var ql = endSession.Length > 0 ? Query(endSession) : new();
    var (afterLogout, logoutHops) = endSession.Length > 0 ? await alice.Navigate(endSession, stopBefore: u => new Uri(u).AbsolutePath == "/" ) : (logout, new List<string>());
    Check("C6", "logout: session removed, redirect to end_session_endpoint with id_token_hint, state and post_logout_redirect_uri=/signout-callback-oidc, then back to /",
        logout.StatusCode == HttpStatusCode.Redirect && endSession.StartsWith(idp.BaseUrl + "/endsession") && !string.IsNullOrEmpty(ql["id_token_hint"]) && !string.IsNullOrEmpty(ql["state"])
        && ql["post_logout_redirect_uri"] == $"https://{H1}/signout-callback-oidc" && gw.Sessions.CountFor("app1") == 1 /* dave's */ && logoutHops.Last() == Url(H1),
        string.Join(" > ", logoutHops.Select(h => h.Split('?')[0])));
    var bare = await alice.Get(Url(H1, "/signout-callback-oidc"));
    Check("C7", "plan trap 1 holds: /signout-callback-oidc without state answers 200 with an empty body",
        bare.StatusCode == HttpStatusCode.OK && (await bare.Content.ReadAsStringAsync()).Length == 0, $"{(int)bare.StatusCode}, {(await bare.Content.ReadAsStringAsync()).Length} bytes");
    await gw.App.StopAsync();
}

// =====================================================================================================
// D. Probes for review findings
// =====================================================================================================
if (only is null or "D")
{
    ResetClients();
    Console.WriteLine("\nD. Probes");

    // ---- D1/D2: RemoteSignOutPath ("/signout-oidc") is live on every host by default -------------------
    {
        var (gw, _) = await NewGateway(new Switches(), app1, app2);
        var alice = new Browser(gw.BaseUrl, idp.BaseUrl);
        await alice.Navigate(Url(H1));
        var sessions = gw.Sessions.CountFor("app1");
        // What a sibling SPA's <img src>, or any cross-site top-level link, produces: a GET carrying the Lax session cookie.
        var forced = await alice.Get(Url(H1, "/signout-oidc"), alice.CookiesFor(H1, "/signout-oidc"));
        var after = await alice.Get(Url(H1, "/bff/user"));
        Finding("D1", "GET /signout-oidc (framework default RemoteSignOutPath) signs the user out: no token, no Origin check, not in the plan's reserved paths",
            forced.StatusCode == HttpStatusCode.OK && gw.Sessions.CountFor("app1") == sessions - 1 && after.StatusCode == HttpStatusCode.Unauthorized,
            $"GET /signout-oidc -> {(int)forced.StatusCode}; app1 sessions {sessions} -> {gw.Sessions.CountFor("app1")}; /bff/user -> {(int)after.StatusCode}");
        await gw.App.StopAsync();

        var (gw2, _) = await NewGateway(new Switches { DisableRemoteSignOutPath = true }, app1, app2);
        var bob = new Browser(gw2.BaseUrl, idp.BaseUrl);
        await bob.Navigate(Url(H1));
        var forced2 = await bob.Get(Url(H1, "/signout-oidc"), bob.CookiesFor(H1, "/signout-oidc"));
        Check("D2", "correction: with RemoteSignOutPath = default the path is not claimed and the session survives",
            forced2.StatusCode == HttpStatusCode.NotFound && (await bob.Get(Url(H1, "/bff/user"))).StatusCode == HttpStatusCode.OK, $"GET /signout-oidc -> {(int)forced2.StatusCode}");
        await gw2.App.StopAsync();
    }

    // ---- D3/D4: correlation and nonce cookies are not __Host- cookies -----------------------------------
    {
        var (gw, _) = await NewGateway(new Switches(), app1, app2);
        var mallory = new Browser(gw.BaseUrl, idp.BaseUrl) { LoginAs = "mallory" };
        var (_, hops) = await mallory.Navigate(Url(H1), stopBefore: u => new Uri(u).AbsolutePath == "/signin-oidc");
        var attackerCallback = hops.Last();                                     // code for mallory + state; never requested by mallory
        var loginCookies = mallory.AllCookies(H1).ToList();

        var victim = new Browser(gw.BaseUrl, idp.BaseUrl);
        foreach (var c in loginCookies) victim.Plant(H1, c.Name, c.Value);      // sibling script: document.cookie = "<name>=N; Domain=spa.test; Path=/; Secure"
        var landed = await victim.Get(attackerCallback);
        var who = await victim.Get(Url(H1, "/bff/user"));
        var sub = who.StatusCode == HttpStatusCode.OK ? (await Json(who)).GetProperty("sub").GetString() : null;
        Finding("D3", "login CSRF: a callback carrying the attacker's code and state is accepted from a browser that only holds planted correlation and nonce cookies",
            landed.StatusCode == HttpStatusCode.Redirect && sub == "mallory",
            $"cookie names: {string.Join(", ", loginCookies.Select(c => c.Name[..Math.Min(c.Name.Length, 30)] + "…"))}; callback -> {(int)landed.StatusCode}; victim's browser is now signed in as '{sub}'");
        await gw.App.StopAsync();

        var (gw2, _) = await NewGateway(new Switches { HostPrefixedLoginCookies = true }, app1, app2);
        var carol = new Browser(gw2.BaseUrl, idp.BaseUrl);
        var ch = await carol.Get(Url(H1));
        var raw = carol.SetCookies.Where(c => c.Host == H1).Select(c => c.SetCookie.ToLowerInvariant()).ToList();
        var dora = new Browser(gw2.BaseUrl, idp.BaseUrl);
        var (fin, _) = await dora.Navigate(Url(H1));
        Check("D4", "correction: correlation and nonce cookies renamed with a __Host- prefix and Path=/ still complete a login with the real handler",
            raw.Count == 2 && raw.All(c => c.StartsWith("__host-spa-") && c.Contains("; secure") && c.Contains("path=/;") && !c.Contains("domain=") && c.Contains("samesite=lax"))
            && fin.StatusCode == HttpStatusCode.OK && dora.AllCookies(H1).Select(c => c.Name).SequenceEqual([SessionCookie]),
            string.Join(" | ", raw.Select(c => c.Length > 28 ? c[..28] + "…" + c[c.IndexOf(';')..] : c)));
        await gw2.App.StopAsync();
    }

    // ---- D5/D6: the callback renews whatever valid session cookie the browser presents ------------------
    foreach (var blind in new[] { false, true })
    {
        var (gw, _) = await NewGateway(new Switches { BlindSessionCookieOnCallback = blind }, app1, app2);
        var mallory = new Browser(gw.BaseUrl, idp.BaseUrl) { LoginAs = "mallory" };
        await mallory.Navigate(Url(H1));
        var attackerCookie = mallory.CookiesFor(H1)[SessionCookie];
        var attackerKey = gw.Sessions.KeysFor("app1").Single();

        var victim = new Browser(gw.BaseUrl, idp.BaseUrl);
        await victim.Navigate(Url(H1));                                         // victim already has her own session
        var victimOldKey = gw.Sessions.KeysFor("app1").Single(k => k != attackerKey);
        var start = await victim.Get(Url(H1, "/bff/login"));                    // the plan's mitigation: sign out first, then challenge
        var oldGone = gw.Sessions.TryGet(victimOldKey) is null;
        victim.Plant(H1, SessionCookie, attackerCookie);                        // after /bff/login, before the callback (page script; __Host- stops siblings, not same-origin script)
        var (fin, _) = await victim.Navigate(start.Headers.Location!.ToString());
        var attackerNow = await Json(await mallory.Get(Url(H1, "/bff/user")));
        var keys = gw.Sessions.KeysFor("app1");
        if (!blind)
            Finding("D5", "session fixation survives the plan's mitigation: /bff/login signs out first, but the callback still renews a session key planted before it returns",
                oldGone && fin.StatusCode == HttpStatusCode.OK && keys.Count == 1 && keys[0] == attackerKey && attackerNow.GetProperty("sub").GetString() == "alice",
                $"old session discarded by /bff/login: {oldGone}; keys after callback: {keys.Count} (attacker's key reused: {keys.Contains(attackerKey)}); attacker's own cookie now reads sub='{attackerNow.GetProperty("sub").GetString()}', token '{attackerNow.GetProperty("accessToken").GetString()![..22]}…'");
        else
            Check("D6", "correction: hiding the session cookie from the cookie handler on the callback path makes sign-in mint a new key",
                fin.StatusCode == HttpStatusCode.OK && keys.Count == 2 && attackerNow.GetProperty("sub").GetString() == "mallory" && gw.Sessions.SubOf(keys.Single(k => k != attackerKey)) == "alice",
                $"keys after callback: {keys.Count}; attacker's cookie still reads sub='{attackerNow.GetProperty("sub").GetString()}'");
        await gw.App.StopAsync();
    }

    // ---- D7: stock provider, real handler, shared callback path ------------------------------------------
    {
        var many = Enumerable.Range(0, 5).Select(i => new AppEntry($"app-{i}", $"app-{i}.spa.test", $"client-s{i}", $"secret-s{i}", "openid")).ToArray();
        foreach (var a in many) RegisterClient(a.ClientId, a.ClientSecret, a.Host);
        var outcome = new List<string>();
        foreach (var skip in new[] { false, true })
        {
            var (gw, _) = await NewGateway(new Switches { Mode = Mode.StockProviderAllSchemes, SkipUnrecognizedRequests = skip }, many);
            var b0 = new Browser(gw.BaseUrl, idp.BaseUrl);
            var (first, _) = await b0.Navigate(Url("app-0.spa.test"));
            var (last, _) = await b0.Navigate(Url("app-4.spa.test"));
            var lastBody = await last.Content.ReadAsStringAsync();
            var h0 = Interlocked.Read(ref Gateway.OidcHandlersCreated);
            for (var i = 0; i < 20; i++) await b0.Get(Url("app-0.spa.test", "/bff/user"));
            var per = (Interlocked.Read(ref Gateway.OidcHandlersCreated) - h0) / 20.0;
            outcome.Add($"SkipUnrecognizedRequests={skip}: app-0 {(int)first.StatusCode}, app-4 {(int)last.StatusCode} '{(lastBody.Length > 60 ? lastBody[..60] + "…" : lastBody)}', {per:0.#} handlers/request");
            if (skip)
                Finding("D7", "stock provider + real handler: with SkipUnrecognizedRequests=true every app completes login on the shared callback path (the spike's control F3 is a default-option result, not a framework limit)",
                    first.StatusCode == HttpStatusCode.OK && last.StatusCode == HttpStatusCode.OK && lastBody == "shell app=app-4 sub=alice", string.Join(" || ", outcome));
            else if (!(first.StatusCode == HttpStatusCode.OK && last.StatusCode == HttpStatusCode.BadRequest))
                Report("D7a", "BAD", "stock provider default did not behave as the spike's F3 says", outcome.Last());
            await gw.App.StopAsync();
        }
    }

    // ---- D8: framework default response_mode with the plan's Lax login cookies ----------------------------
    {
        var (gw, _) = await NewGateway(new Switches { ResponseMode = null }, app1, app2);
        var erin = new Browser(gw.BaseUrl, idp.BaseUrl);
        var ch = await erin.Get(Url(H1));
        var mode = Query(ch.Headers.Location!.ToString())["response_mode"];
        var formPage = await (await erin.Get(ch.Headers.Location!.ToString())).Content.ReadAsStringAsync();
        string Field(string n) => System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(formPage, $"name=\"{n}\" value=\"([^\"]*)\"").Groups[1].Value);
        FormUrlEncodedContent Form() => new(new Dictionary<string, string> { ["code"] = Field("code"), ["state"] = Field("state") });
        // A cross-site POST carries no SameSite=Lax cookies. That is browser behaviour; here it is modelled by sending none.
        var crossSitePost = await erin.Send(HttpMethod.Post, Url(H1, "/signin-oidc"), new Dictionary<string, string>(), Form());
        var body = await crossSitePost.Content.ReadAsStringAsync();
        Finding("D8", "leaving ResponseMode at the framework default sends response_mode=form_post; the provider's cross-site POST arrives without the Lax correlation cookie and the login fails",
            mode == "form_post" && crossSitePost.StatusCode == HttpStatusCode.BadRequest && body.Contains("Correlation failed"), $"response_mode={mode}; POST without Lax cookies -> {(int)crossSitePost.StatusCode} {body}");
        await gw.App.StopAsync();
    }

    // ---- D9: validation and failure caching for dynamically named options ---------------------------------
    {
        var (gw, _) = await NewGateway(new Switches(), app1, app2);
        var mon = gw.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();
        var ghost = mon.Get("oidc:ghost");                                       // a name with no registry entry
        var threw = false;
        try { ghost.Validate(); } catch (ArgumentException) { threw = true; }
        Check("D9", "plan claim holds: no validation runs for a dynamically named scheme (Get succeeds with ClientId null); calling Validate() yourself throws",
            ghost.ClientId is null && ghost.ConfigurationManager is null && threw);
        await gw.App.StopAsync();

        var sc = new ServiceCollection();
        var calls = 0;
        sc.AddOptions();
        sc.AddSingleton<IConfigureOptions<DemoOptions>>(new ConfigureNamedOptions<DemoOptions>(null, _ => { if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("one-off failure while building options"); }));
        using var sp = sc.BuildServiceProvider();
        var demo = sp.GetRequiredService<IOptionsMonitor<DemoOptions>>();
        var failures = 0;
        for (var i = 0; i < 3; i++) try { demo.Get("oidc:appX"); } catch (InvalidOperationException) { failures++; }
        sp.GetRequiredService<IOptionsMonitorCache<DemoOptions>>().TryRemove("oidc:appX");
        var recovered = true;
        try { demo.Get("oidc:appX"); } catch { recovered = false; }
        Finding("D10", "a one-off exception while building named options is cached with the entry: every later Get rethrows it until the name is evicted",
            failures == 3 && calls == 2 && recovered, $"3 Gets -> {failures} failures from {calls - 1} factory run; after TryRemove -> {(recovered ? "ok" : "still failing")}");
    }
}

// =====================================================================================================
// E. ASP.NET Core antiforgery under the host-scoped provider, with __Host- cookie names
// =====================================================================================================
if (only is null or "E")
{
    ResetClients();
    Console.WriteLine("\nE. Antiforgery");
    const string Csrf = "__Host-spa-csrf";
    static StringContent Body(string s, string type) => new(s, System.Text.Encoding.UTF8, type);
    async Task<(string Token, string Cookie)> Session(Browser b, string host)
    {
        var r = await b.Get(Url(host, "/bff/session"));
        return ((await Json(r)).GetProperty("csrfToken").GetString()!, b.CookiesFor(host)[Csrf]);
    }
    Dictionary<string, string> Hdr(string token) => new() { ["X-CSRF-TOKEN"] = token };

    // E1-E4: the plan's settings (header only, Secure always), without and with app binding.
    foreach (var bindApp in new[] { false, true })
    {
        var (gw, _) = await NewGateway(new Switches { AntiforgeryBindApp = bindApp }, app1, app2);
        var alice = new Browser(gw.BaseUrl, idp.BaseUrl);
        await alice.Navigate(Url(H1));
        await alice.Navigate(Url(H2));
        var anon = await new Browser(gw.BaseUrl, idp.BaseUrl).Get(Url(H1, "/bff/session"));
        var (t1, c1) = await Session(alice, H1);
        var (t2, c2) = await Session(alice, H2);
        var setCookie = alice.SetCookies.Last(c => c.Host == H1 && c.SetCookie.StartsWith(Csrf + "=")).SetCookie.ToLowerInvariant();

        if (!bindApp)
        {
            var ok = await alice.Send(HttpMethod.Post, Url(H1, "/api/echo"), null, Body("{\"a\":1}", "application/json"), Hdr(t1));
            var okJson = await Json(ok);
            var none = await alice.Send(HttpMethod.Post, Url(H1, "/api/echo"), null, Body("{\"a\":1}", "application/json"));
            Check("E1", "antiforgery works with the host-scoped provider: token issued on /bff/session, header token accepted, body left unread, missing token refused, anonymous gets no token",
                ok.StatusCode == HttpStatusCode.OK && okJson.GetProperty("bodyLeft").GetInt32() == 7 && none.StatusCode == HttpStatusCode.Forbidden && anon.StatusCode == HttpStatusCode.Unauthorized
                && setCookie.Contains("; secure") && setCookie.Contains("path=/") && setCookie.Contains("samesite=strict") && setCookie.Contains("httponly") && !setCookie.Contains("domain="),
                setCookie[(setCookie.IndexOf(';') + 2)..]);

            var headerOnly = await alice.Send(HttpMethod.Post, Url(H2, "/api/echo"), null, Body("x", "text/plain"), Hdr(t1));
            Check("E2", "app1's request token alone on app2 is refused (it does not pair with app2's cookie token)", headerOnly.StatusCode == HttpStatusCode.Forbidden);

            // Same user re-authenticates in the same browser: the antiforgery cookie persists, the session is new.
            await alice.Send(HttpMethod.Post, Url(H1, "/bff/logout"));
            await alice.Navigate(Url(H1));
            var stale = await alice.Send(HttpMethod.Post, Url(H1, "/api/echo"), null, Body("x", "text/plain"), Hdr(t1));
            Finding("E4", "a request token issued in the user's previous session is still accepted in her next one: the token is bound to the user and the cookie token, not to the session (N-06 'another session's token' as worded)",
                stale.StatusCode == HttpStatusCode.OK, $"old-session token after logout and re-login -> {(int)stale.StatusCode}");
        }

        // Token PAIR (cookie token + request token) lifted from app1 and presented to app2 with app2's session.
        var pair = await alice.Send(HttpMethod.Post, Url(H2, "/api/echo"), new Dictionary<string, string> { [SessionCookie] = alice.CookiesFor(H2)[SessionCookie], [Csrf] = c1 }, Body("x", "text/plain"), Hdr(t1));
        if (!bindApp)
            Finding("E3", "without app binding, app1's cookie-token/request-token pair validates on app2 for the same user: the antiforgery protector purpose is process-wide, only the host-only cookie keeps apps apart",
                pair.StatusCode == HttpStatusCode.OK, $"app1 pair on app2 -> {(int)pair.StatusCode}");
        else
            Check("E3b", "plan task 4.4 closes it: with the app id in the token's additional data the same pair is refused on app2, and app2's own pair still works",
                pair.StatusCode == HttpStatusCode.Forbidden && (await alice.Send(HttpMethod.Post, Url(H2, "/api/echo"), null, Body("x", "text/plain"), Hdr(t2))).StatusCode == HttpStatusCode.OK,
                $"app1 pair on app2 -> {(int)pair.StatusCode}");
        await gw.App.StopAsync();
    }

    // E5: the framework default cookie policy with a __Host- name.
    {
        var (gw, _) = await NewGateway(new Switches { AntiforgerySecureAlways = false }, app1, app2);
        var alice = new Browser(gw.BaseUrl, idp.BaseUrl);
        await alice.Navigate(Url(H1));
        await alice.Get(Url(H1, "/bff/session"));
        var setCookie = alice.SetCookies.Last(c => c.Host == H1 && c.SetCookie.StartsWith(Csrf + "=")).SetCookie.ToLowerInvariant();
        Check("E5", "framework default (Cookie.SecurePolicy = None) emits __Host-spa-csrf WITHOUT Secure even over https, which browsers discard: SecurePolicy.Always is mandatory, as the contract says",
            !setCookie.Contains("secure"), setCookie[(setCookie.IndexOf(';') + 2)..]);
        await gw.App.StopAsync();
    }

    // E6: what SuppressReadingTokenFromFormBody actually changes.
    {
        const string form = "a=1&b=2&filler=xxxxxxxxxxxxxxxxxxxx";
        var rows = new List<string>();
        var facts = new Dictionary<string, (HttpStatusCode Status, int Left)>();
        foreach (var suppress in new[] { true, false })
        {
            var (gw, _) = await NewGateway(new Switches { AntiforgerySuppressFormBody = suppress }, app1, app2);
            var alice = new Browser(gw.BaseUrl, idp.BaseUrl);
            await alice.Navigate(Url(H1));
            var (t, _) = await Session(alice, H1);
            async Task Row(string label, string body, Dictionary<string, string>? h)
            {
                var r = await alice.Send(HttpMethod.Post, Url(H1, "/api/echo"), null, Body(body, "application/x-www-form-urlencoded"), h);
                var left = (await Json(r)).GetProperty("bodyLeft").GetInt32();
                facts[$"{suppress}/{label}"] = (r.StatusCode, left);
                rows.Add($"suppress={suppress}, {label}: {(int)r.StatusCode}, body left {left}/{body.Length}");
            }
            await Row("header token", form, Hdr(t));
            await Row("no token", form, null);
            await Row("form-field token", form + "&__RequestVerificationToken=" + Uri.EscapeDataString(t), null);
            await gw.App.StopAsync();
        }
        Check("E6", "SuppressReadingTokenFromFormBody=true: form bodies are never read and a form-field token is refused; a header token is honoured either way",
            facts["True/header token"] == (HttpStatusCode.OK, form.Length) && facts["True/no token"].Status == HttpStatusCode.Forbidden && facts["True/no token"].Left == form.Length
            && facts["True/form-field token"].Status == HttpStatusCode.Forbidden);
        Finding("E7", "with the flag off, a request that carries the header token still reaches the proxy with its body intact; the body is consumed only when the header is absent (and then a form-field token is accepted)",
            facts["False/header token"] == (HttpStatusCode.OK, form.Length) && facts["False/no token"].Left == 0 && facts["False/form-field token"].Status == HttpStatusCode.OK,
            string.Join(" | ", rows));
    }
}

var bad = results.Count(r => r.Kind == "BAD");
Console.WriteLine($"\n{results.Count(r => r.Kind == "OK")} expectations held, {bad} did not, {results.Count(r => r.Kind == "FINDING")} findings confirmed, {results.Count(r => r.Kind == "not-rep")} probes not reproduced");
await idp.App.StopAsync();
return bad == 0 ? 0 : 1;
