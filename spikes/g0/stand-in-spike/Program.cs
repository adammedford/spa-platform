using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Spike;

// Feasibility spike: runtime, per-host OAuth clients on ASP.NET Core 10 without restart.
// Everything runs in one process: a fake authorization server, the gateway, and a cookie-carrying test client.

var results = new List<(string Id, string Name, bool Pass, string Detail)>();
void Check(string id, string name, bool pass, string detail = "")
{
    results.Add((id, name, pass, detail));
    Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {id,-4} {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
}
static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
static string Url(string host, string path = "/") => $"https://{host}{path}";

const string H1 = "app1.spa.test", H2 = "app2.spa.test", H3 = "app3.spa.test", SessionCookie = "__Host-spa-session";

var idp = new FakeIdp();
await idp.StartAsync();
void RegisterClient(string clientId, string secret, string host)
    => idp.Clients[clientId] = new FakeIdp.Client(secret, [$"https://{host}/signin-oidc"]);

RegisterClient("client-1", "secret-1", H1);
RegisterClient("client-2", "secret-2", H2);
var app1 = new AppEntry("app1", H1, "client-1", "secret-1", "openid api1/read");
var app2 = new AppEntry("app2", H2, "client-2", "secret-2", "openid api2/read");
var registry = new Registry();
registry.Publish([app1, app2]);

var gw = await Gateway.StartAsync(registry, idp.BaseUrl, Mode.HostScoped);
var pid = Environment.ProcessId;
var gatewayInstance = gw.App;
var cookieOptions = gw.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
Console.WriteLine($"runtime {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; gateway {gw.BaseUrl}; idp {idp.BaseUrl}\n");

// ---------------------------------------------------------------- A. host-based selection, challenge, callback
Console.WriteLine("A. Host-based scheme selection, challenge and callback dispatch");
{
    var probe = new Browser(gw.BaseUrl, idp.BaseUrl);
    var r1 = await probe.Get(Url(H1));
    var loc1 = r1.Headers.Location?.ToString() ?? "";
    Check("A1", "app1 host challenges with client-1 and its own redirect_uri",
        r1.StatusCode == HttpStatusCode.Redirect && loc1.StartsWith(idp.BaseUrl) && loc1.Contains("client_id=client-1") && loc1.Contains(Uri.EscapeDataString($"https://{H1}/signin-oidc")));
    var r2 = await probe.Get(Url(H2));
    var loc2 = r2.Headers.Location?.ToString() ?? "";
    Check("A2", "app2 host challenges with client-2 and its own redirect_uri",
        r2.StatusCode == HttpStatusCode.Redirect && loc2.Contains("client_id=client-2") && loc2.Contains(Uri.EscapeDataString($"https://{H2}/signin-oidc")));
    var unknown = new[] { "/", "/signin-oidc?code=x&state=y", "/bff/user" };
    var codes = new List<HttpStatusCode>();
    foreach (var p in unknown) codes.Add((await probe.Get(Url("evil.spa.test", p))).StatusCode);
    Check("A3", "unknown host gets 404 on every path, including the callback path", codes.All(c => c == HttpStatusCode.NotFound), string.Join(",", codes.Select(c => (int)c)));
}

var alice = new Browser(gw.BaseUrl, idp.BaseUrl);
{
    var (final1, hops1) = await alice.Navigate(Url(H1));
    var body1 = await final1.Content.ReadAsStringAsync();
    Check("A4", "full login on app1 ends on app1's shell", final1.StatusCode == HttpStatusCode.OK && body1 == "shell app=app1 sub=alice", $"{hops1.Count} hops; {(int)final1.StatusCode} '{body1}' via {string.Join(" > ", hops1.Select(h => h.Split('?')[0]))}");
    var (final2, _) = await alice.Navigate(Url(H2));
    var body2 = await final2.Content.ReadAsStringAsync();
    Check("A5", "full login on app2 (same callback path) ends on app2's shell", final2.StatusCode == HttpStatusCode.OK && body2 == "shell app=app2 sub=alice");

    var u1 = await Json(await alice.Get(Url(H1, "/bff/user")));
    var u2 = await Json(await alice.Get(Url(H2, "/bff/user")));
    Check("A6", "each host authenticates through its own default scheme and client",
        u1.GetProperty("clientId").GetString() == "client-1" && u2.GetProperty("clientId").GetString() == "client-2"
        && u1.GetProperty("middlewareSetUser").GetBoolean() && u2.GetProperty("middlewareSetUser").GetBoolean());

    var before = Interlocked.Read(ref StandInOidcHandler.Initializations);
    for (var i = 0; i < 50; i++) await alice.Get(Url(H1, "/bff/user"));
    var perRequest = (Interlocked.Read(ref StandInOidcHandler.Initializations) - before) / 50.0;
    Check("A7", "exactly one remote handler is created per request", perRequest == 1.0, $"{perRequest:0.##} per request");
}

// ---------------------------------------------------------------- B. independent cookies and server-side sessions
Console.WriteLine("\nB. Cookies, server-side sessions and cross-app substitution");
var app1Cookie = alice.CookiesFor(H1)[SessionCookie];
{
    var setCookie = alice.SetCookies.Last(c => c.Host == H1 && c.SetCookie.StartsWith(SessionCookie + "=")).SetCookie.ToLowerInvariant();
    Check("B1", "session cookie is __Host- prefixed, Secure, HttpOnly, SameSite=Lax, Path=/, no Domain",
        setCookie.Contains("; secure") && setCookie.Contains("; httponly") && setCookie.Contains("samesite=lax") && setCookie.Contains("path=/") && !setCookie.Contains("domain="));
    Check("B2", "after login the browser holds one cookie per host: the session cookie",
        alice.CookiesFor(H1).Keys.SequenceEqual([SessionCookie]) && alice.CookiesFor(H2).Keys.SequenceEqual([SessionCookie]),
        string.Join(",", alice.CookiesFor(H1).Keys));

    var o1 = cookieOptions.Get(SchemeNames.Cookie("app1"));
    var o2 = cookieOptions.Get(SchemeNames.Cookie("app2"));
    var inCookie = o1.TicketDataFormat.Unprotect(app1Cookie);
    Check("B3", "the cookie ticket carries a session id and nothing else (no tokens, no identity claims)",
        inCookie is not null && inCookie.Principal.Claims.Count() == 1
        && inCookie.Principal.Claims.Single().Type == "Microsoft.AspNetCore.Authentication.Cookies-SessionId"
        && !inCookie.Properties.GetTokens().Any(), $"cookie length {app1Cookie.Length}");

    var stored = TicketSerializer.Default.Deserialize(gw.Sessions.All.Single(r => r.AppId == "app1").Ticket)!;
    Check("B4", "tokens live in the server-side session, bound to app and client",
        gw.Sessions.Count == 2 && stored.Properties.GetTokenValue("access_token")!.StartsWith("at~client-1~")
        && stored.Properties.GetTokenValue("refresh_token")!.StartsWith("rt~")
        && gw.Sessions.All.Single(r => r.AppId == "app1").ClientId == "client-1");

    Check("B5", "app2's scheme cannot decrypt app1's cookie (per-scheme protector purpose)", o2.TicketDataFormat.Unprotect(app1Cookie) is null);

    var replay = await alice.Get(Url(H2, "/bff/user"), new Dictionary<string, string> { [SessionCookie] = app1Cookie });
    var replayNav = await alice.Get(Url(H2), new Dictionary<string, string> { [SessionCookie] = app1Cookie });
    Check("B6", "app1's cookie presented to app2 is anonymous (401 on API, fresh challenge on navigation)",
        replay.StatusCode == HttpStatusCode.Unauthorized && replayNav.StatusCode == HttpStatusCode.Redirect
        && replayNav.Headers.Location!.ToString().Contains("client_id=client-2"));

    // Suppose the crypto boundary failed: forge a cookie that app2 CAN decrypt but that names app1's session key.
    var app1Key = gw.Sessions.KeysFor("app1").Single();
    var forgedIdentity = new ClaimsIdentity([new Claim("Microsoft.AspNetCore.Authentication.Cookies-SessionId", app1Key)], SchemeNames.Cookie("app2"));
    var forged = o2.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(forgedIdentity), SchemeNames.Cookie("app2")));
    var forgedResp = await alice.Get(Url(H2, "/bff/user"), new Dictionary<string, string> { [SessionCookie] = forged });
    Check("B7", "a validly encrypted app2 cookie naming app1's session key is still anonymous (store is app-bound)", forgedResp.StatusCode == HttpStatusCode.Unauthorized);

    var other = await (await alice.Get(Url(H1, "/_probe?scheme=cookie:app2"))).Content.ReadAsStringAsync();
    var own = await (await alice.Get(Url(H1, "/_probe?scheme=cookie:app1"))).Content.ReadAsStringAsync();
    Check("B8", "on app1's host, app2's scheme names do not resolve at all", other == "absent" && own == "present");
}

// ---------------------------------------------------------------- C. callback substitution
Console.WriteLine("\nC. Callback, state and code substitution");
{
    var sessionsBefore = gw.Sessions.Count;

    var mallory = new Browser(gw.BaseUrl, idp.BaseUrl);
    var challenge = await mallory.Get(Url(H1));
    var callback = (await mallory.Get(challenge.Headers.Location!.ToString())).Headers.Location!;      // https://app1/signin-oidc?code&state
    var onApp2 = await mallory.Get(Url(H2, callback.PathAndQuery), new Dictionary<string, string>(mallory.CookiesFor(H1)));
    var onApp2Body = await onApp2.Content.ReadAsStringAsync();
    Check("C1", "app1's callback (code, state, correlation cookie) replayed on app2 is rejected",
        onApp2.StatusCode == HttpStatusCode.BadRequest && onApp2Body.Contains("state invalid"), onApp2Body);

    var eve = new Browser(gw.BaseUrl, idp.BaseUrl);
    var eveChallenge = await eve.Get(Url(H2));
    var state2 = System.Web.HttpUtility.ParseQueryString(eveChallenge.Headers.Location!.Query)["state"]!;
    var codeForClient1 = idp.IssueCodeFor("client-1", $"https://{H1}/signin-oidc");
    var injected = await eve.Get(Url(H2, $"/signin-oidc?code={codeForClient1}&state={Uri.EscapeDataString(state2)}"));
    var injectedBody = await injected.Content.ReadAsStringAsync();
    Check("C2", "a code issued to client-1 injected into app2's own login is refused by the authorization server",
        injected.StatusCode == HttpStatusCode.BadRequest && injectedBody.Contains("token endpoint rejected"), injectedBody);

    var carol = new Browser(gw.BaseUrl, idp.BaseUrl);
    var carolChallenge = await carol.Get(Url(H1));
    var carolCallback = (await carol.Get(carolChallenge.Headers.Location!.ToString())).Headers.Location!;
    var noCorrelation = await carol.Get(Url(H1, carolCallback.PathAndQuery), new Dictionary<string, string>());
    var noCorrelationBody = await noCorrelation.Content.ReadAsStringAsync();
    Check("C3", "a valid callback without the browser's correlation cookie is rejected",
        noCorrelation.StatusCode == HttpStatusCode.BadRequest && noCorrelationBody.Contains("Correlation failed"), noCorrelationBody);

    Check("C4", "none of the rejected callbacks created a session", gw.Sessions.Count == sessionsBefore, $"{gw.Sessions.Count} sessions");
}

// ---------------------------------------------------------------- D. runtime onboarding, rotation, removal
Console.WriteLine("\nD. Onboarding, rotation and removal with no restart");
var bob = new Browser(gw.BaseUrl, idp.BaseUrl);
{
    var app1OptionsBefore = cookieOptions.Get(SchemeNames.Cookie("app1"));
    var pre = await bob.Get(Url(H3));
    RegisterClient("client-3", "secret-3", H3);
    var app3 = new AppEntry("app3", H3, "client-3", "secret-3", "openid api3/read");
    registry.Publish([app1, app2, app3]);
    var (final3, _) = await bob.Navigate(Url(H3));
    var still1 = await alice.Get(Url(H1, "/bff/user"));
    Check("D1", "third app onboarded at runtime: 404 before, full login after, same process and server instance",
        pre.StatusCode == HttpStatusCode.NotFound && await final3.Content.ReadAsStringAsync() == "shell app=app3 sub=alice"
        && Environment.ProcessId == pid && ReferenceEquals(gatewayInstance, gw.App) && still1.StatusCode == HttpStatusCode.OK);
    Check("D2", "apps that did not change keep their cached options instance", ReferenceEquals(app1OptionsBefore, cookieOptions.Get(SchemeNames.Cookie("app1"))));

    // Secret rotation: the IdP rotates first, so the gateway's stale secret must fail, then the registry catches up.
    RegisterClient("client-1", "secret-1b", H1);
    var dave = new Browser(gw.BaseUrl, idp.BaseUrl);
    var (stale, _) = await dave.Navigate(Url(H1));
    app1 = app1 with { ClientSecret = "secret-1b" };
    registry.Publish([app1, app2, app3]);
    var (rotated, _) = await dave.Navigate(Url(H1));
    var aliceAfterRotation = await alice.Get(Url(H1, "/bff/user"));
    Check("D3", "client secret rotated at runtime: stale secret fails, new secret is picked up, existing sessions survive",
        stale.StatusCode == HttpStatusCode.BadRequest && rotated.StatusCode == HttpStatusCode.OK
        && idp.LastSecretSeenFor("client-1") == "secret-1b" && aliceAfterRotation.StatusCode == HttpStatusCode.OK);

    // A login that is in flight while the registry reloads (unrelated app added, this app's secret rotated).
    var erin = new Browser(gw.BaseUrl, idp.BaseUrl);
    var erinChallenge = await erin.Get(Url(H2));
    RegisterClient("client-2", "secret-2b", H2);
    RegisterClient("client-4", "secret-4", "app4.spa.test");
    app2 = app2 with { ClientSecret = "secret-2b" };
    var app4 = new AppEntry("app4", "app4.spa.test", "client-4", "secret-4", "openid");
    registry.Publish([app1, app2, app3, app4]);
    var erinCallback = (await erin.Get(erinChallenge.Headers.Location!.ToString())).Headers.Location!.ToString();
    var (erinFinal, _) = await erin.Navigate(erinCallback);
    Check("D4", "a login started before a reload completes after it (state and correlation survive)",
        erinFinal.StatusCode == HttpStatusCode.OK && await erinFinal.Content.ReadAsStringAsync() == "shell app=app2 sub=alice");

    // The app is re-pointed at a different OAuth client while a login is in flight.
    var frank = new Browser(gw.BaseUrl, idp.BaseUrl);
    var frankChallenge = await frank.Get(Url(H3));
    RegisterClient("client-3b", "secret-3b", H3);                     // client-3 stays registered at the IdP
    app3 = app3 with { ClientId = "client-3b", ClientSecret = "secret-3b" };
    registry.Publish([app1, app2, app3, app4]);
    var frankCallback = (await frank.Get(frankChallenge.Headers.Location!.ToString())).Headers.Location!.ToString();
    var frankFail = await frank.Get(frankCallback);
    var frankFailBody = await frankFail.Content.ReadAsStringAsync();
    var bobOldSession = await bob.Get(Url(H3, "/bff/user"));
    var (frankRetry, _) = await frank.Navigate(Url(H3));
    var frankUser = await Json(await frank.Get(Url(H3, "/bff/user")));
    Check("D5", "client replaced mid-login: the in-flight callback fails closed, a fresh login uses the new client",
        frankFail.StatusCode == HttpStatusCode.BadRequest && frankFailBody.Contains("client changed during login")
        && frankRetry.StatusCode == HttpStatusCode.OK && frankUser.GetProperty("clientId").GetString() == "client-3b", frankFailBody);
    Check("D6", "sessions issued under the replaced client stop authenticating", bobOldSession.StatusCode == HttpStatusCode.Unauthorized);

    // Removal.
    var app2Cookie = alice.CookiesFor(H2)[SessionCookie];
    var app2SessionsBefore = gw.Sessions.CountFor("app2");
    registry.Publish([app1, app3, app4]);
    var gone = await alice.Get(Url(H2));
    var goneCallback = await alice.Get(Url(H2, "/signin-oidc?code=x&state=y"));
    var replayElsewhere = await alice.Get(Url(H1, "/bff/user"), new Dictionary<string, string> { [SessionCookie] = app2Cookie });
    var app1Alive = await alice.Get(Url(H1, "/bff/user"));
    Check("D7", "app removed at runtime: host 404s, its sessions are purged, its cookie is useless elsewhere, others unaffected",
        gone.StatusCode == HttpStatusCode.NotFound && goneCallback.StatusCode == HttpStatusCode.NotFound
        && app2SessionsBefore > 0 && gw.Sessions.CountFor("app2") == 0
        && replayElsewhere.StatusCode == HttpStatusCode.Unauthorized && app1Alive.StatusCode == HttpStatusCode.OK,
        $"{app2SessionsBefore} app2 sessions purged");

    // An invalid registry never goes live.
    var versionBefore = registry.Current.Version;
    var threw = false;
    try { registry.Publish([app1, app3, app4, new AppEntry("hijack", H1, "client-x", "s", "openid")]); } catch (InvalidOperationException) { threw = true; }
    var afterBad = await alice.Get(Url(H1, "/bff/user"));
    Check("D8", "a registry that claims an existing host twice is rejected whole; last-known-good stays live",
        threw && registry.Current.Version == versionBefore && afterBad.StatusCode == HttpStatusCode.OK);
}

// ---------------------------------------------------------------- E. token refresh and logout
Console.WriteLine("\nE. Server-side tokens: refresh under concurrency, logout");
{
    idp.AccessTokenLifetimeMs = 500;
    idp.RefreshDelayMs = 150;
    var grace = new Browser(gw.BaseUrl, idp.BaseUrl);
    await grace.Navigate(Url(H1));
    var first = await Json(await grace.Get(Url(H1, "/api/whoami")));
    var originalCookie = grace.CookiesFor(H1)[SessionCookie];
    await Task.Delay(700);                                            // access token is now expired

    int grantsBefore = idp.RefreshGrants, rejectedBefore = idp.RejectedTokenRequests;
    var cookies = new Dictionary<string, string>(grace.CookiesFor(H1));
    var calls = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => grace.Get(Url(H1, "/api/whoami"), cookies)));
    var seqs = new List<int>();
    foreach (var c in calls.Where(c => c.StatusCode == HttpStatusCode.OK)) seqs.Add((await Json(c)).GetProperty("tokenSeq").GetInt32());
    Check("E1", "25 concurrent calls on an expired token cause exactly one refresh grant and all succeed with the new token",
        calls.All(c => c.StatusCode == HttpStatusCode.OK) && idp.RefreshGrants - grantsBefore == 1
        && idp.RejectedTokenRequests == rejectedBefore && seqs.Distinct().Count() == 1 && seqs[0] > first.GetProperty("tokenSeq").GetInt32(),
        $"{idp.RefreshGrants - grantsBefore} refresh grant(s), {idp.RejectedTokenRequests - rejectedBefore} rejected, token #{first.GetProperty("tokenSeq").GetInt32()} -> #{seqs.FirstOrDefault()}");

    idp.AccessTokenLifetimeMs = 3_600_000;
    idp.RefreshDelayMs = 0;
    await Task.Delay(700);
    await grace.Get(Url(H1, "/api/whoami"));                          // refresh once more, now with a long-lived token
    var keyInOriginalCookie = cookieOptions.Get(SchemeNames.Cookie("app1")).TicketDataFormat.Unprotect(originalCookie)!.Principal.Claims.Single().Value;
    var storedAfterRefresh = TicketSerializer.Default.Deserialize(gw.Sessions.TryGet(keyInOriginalCookie)!.Ticket)!;
    var storedSeq = int.Parse(storedAfterRefresh.Properties.GetTokenValue("access_token")!.Split('~')[3]);
    var withOriginalCookie = await grace.Get(Url(H1, "/api/whoami"), new Dictionary<string, string> { [SessionCookie] = originalCookie });
    Check("E2", "refresh rewrites the same server-side session; the cookie issued at login still identifies it",
        withOriginalCookie.StatusCode == HttpStatusCode.OK && storedSeq > seqs[0]
        && (await Json(withOriginalCookie)).GetProperty("tokenSeq").GetInt32() == storedSeq, $"stored token #{storedSeq}");

    var henry = new Browser(gw.BaseUrl, idp.BaseUrl);
    await henry.Navigate(Url(H3));
    var api3 = await Json(await henry.Get(Url(H3, "/api/whoami")));
    var api1 = await Json(await grace.Get(Url(H1, "/api/whoami")));
    Check("E3", "the token attached for each app was issued to that app's client",
        api1.GetProperty("tokenClient").GetString() == "client-1" && api3.GetProperty("tokenClient").GetString() == "client-3b");

    var cookieBeforeLogout = grace.CookiesFor(H1)[SessionCookie];
    var sessionsBeforeLogout = gw.Sessions.CountFor("app1");
    var logout = await grace.Send(HttpMethod.Post, Url(H1, "/bff/logout"));
    var replayAfterLogout = await grace.Get(Url(H1, "/bff/user"), new Dictionary<string, string> { [SessionCookie] = cookieBeforeLogout });
    Check("E4", "logout deletes the server-side session; replaying the old (still decryptable) cookie is anonymous",
        logout.StatusCode == HttpStatusCode.NoContent && !grace.CookiesFor(H1).ContainsKey(SessionCookie)
        && gw.Sessions.CountFor("app1") == sessionsBeforeLogout - 1 && replayAfterLogout.StatusCode == HttpStatusCode.Unauthorized);
}

// ---------------------------------------------------------------- F. scale: host-scoped vs the stock provider
Console.WriteLine("\nF. 300 apps: host-scoped provider vs stock provider with every scheme registered");
{
    const int n = 300;
    var many = Enumerable.Range(0, n).Select(i => new AppEntry($"app-{i:000}", $"app-{i:000}.spa.test", $"client-m{i:000}", $"secret-m{i:000}", "openid")).ToList();
    foreach (var a in many) RegisterClient(a.ClientId, a.ClientSecret, a.Host);
    registry.Publish([.. registry.Current.ByAppId.Values, .. many]);
    var total = registry.Current.ByAppId.Count;

    async Task<(double InitsPerRequest, double MeanMicros)> Measure(Gateway g, string host)
    {
        var b = new Browser(g.BaseUrl, idp.BaseUrl);
        for (var i = 0; i < 300; i++) await b.Get(Url(host, "/bff/user"));
        var before = Interlocked.Read(ref StandInOidcHandler.Initializations);
        const int requests = 3000;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < requests; i++) await b.Get(Url(host, "/bff/user"));
        sw.Stop();
        return ((Interlocked.Read(ref StandInOidcHandler.Initializations) - before) / (double)requests, sw.Elapsed.TotalMicroseconds / requests);
    }

    var scoped = await Measure(gw, "app-150.spa.test");
    var lateLogin = new Browser(gw.BaseUrl, idp.BaseUrl);
    var (lateFinal, _) = await lateLogin.Navigate(Url("app-299.spa.test"));
    Check("F1", $"host-scoped provider with {total} apps: one handler per request, any app can log in",
        scoped.InitsPerRequest == 1.0 && lateFinal.StatusCode == HttpStatusCode.OK, $"{scoped.InitsPerRequest:0.#} handler/request, {scoped.MeanMicros:0} µs mean");

    var stock = await Gateway.StartAsync(registry, idp.BaseUrl, Mode.StockProviderAllSchemes);
    var stockResult = await Measure(stock, "app-150.spa.test");
    Check("F2", $"control, stock provider with {total} apps: every request instantiates every remote handler",
        stockResult.InitsPerRequest == total, $"{stockResult.InitsPerRequest:0.#} handlers/request, {stockResult.MeanMicros:0} µs mean ({stockResult.MeanMicros / scoped.MeanMicros:0.0}x)");

    var stockBrowser = new Browser(stock.BaseUrl, idp.BaseUrl);
    var (firstRegistered, _) = await stockBrowser.Navigate(Url("app-000.spa.test"));
    var (lastRegistered, _) = await stockBrowser.Navigate(Url("app-299.spa.test"));
    var lastBody = await lastRegistered.Content.ReadAsStringAsync();
    Check("F3", "control, stock provider with a shared callback path: only the first-registered app can complete login",
        firstRegistered.StatusCode == HttpStatusCode.OK && lastRegistered.StatusCode == HttpStatusCode.BadRequest && lastBody.Contains("state invalid"),
        $"app-000: {(int)firstRegistered.StatusCode}; app-299: {(int)lastRegistered.StatusCode} {lastBody}");
    await stock.App.StopAsync();
}

await gw.App.StopAsync();
await idp.App.StopAsync();

var failed = results.Count(r => !r.Pass);
Console.WriteLine($"\n{results.Count - failed}/{results.Count} checks passed");
File.WriteAllText("spike-results.json", JsonSerializer.Serialize(new
{
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    checks = results.Select(r => new { r.Id, r.Name, r.Pass, r.Detail }),
}, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
return failed == 0 ? 0 : 1;
