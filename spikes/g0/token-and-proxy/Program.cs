using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Spike;
using Yarp.ReverseProxy;

// Gate G0, items 4 and 5. The mechanism of the real-handler harness (host-scoped scheme provider, named options,
// cache eviction, server-side sessions, the four overrides) with two packages added:
//   A  Duende.AccessTokenManagement.OpenIdConnect, configured as plan task 3.8 says             (item 4)
//   B  the same library's defaults and edges
//   C  Yarp.ReverseProxy doing what the upstream contract and plan task 4.2 ask                 (item 5)
//   D  the library's refresh and revoke calls alone, under orchestration the gateway owns        (item 4, the alternative)
// "OK"/"BAD" marks whether an expectation held. "FINDING" marks a probe that shows something the plan must allow
// for; "not-rep" is a probe that did not reproduce. Only "BAD" fails the run.
// Run one section with: dotnet run -c Release -- A

var only = args.Length > 0 ? args[0] : null;
if (only is not (null or "A" or "B" or "C" or "D"))
{
    Console.Error.WriteLine("usage: dotnet run -c Release [-- A|B|C|D]");
    return 2;
}

// Everything here talks to loopback. A proxy configured for the machine must not be asked to carry it,
// and C19 needs HttpClient to propagate trace context whatever the machine's environment says.
HttpClient.DefaultProxy = new WebProxy();
AppContext.SetSwitch("System.Net.Http.EnableActivityPropagation", true);

var results = new List<(string Id, string Kind, string Name, string Detail)>();
void Report(string id, string kind, string name, string detail = "")
{
    results.Add((id, kind, name, detail));
    Console.WriteLine($"{kind,-8}{id,-5} {name}{(detail.Length > 0 ? "\n              [" + detail + "]" : "")}");
}
void Check(string id, string name, bool pass, string detail = "") => Report(id, pass ? "OK" : "BAD", name, detail);
void Finding(string id, string name, bool confirmed, string detail = "") => Report(id, confirmed ? "FINDING" : "not-rep", name, detail);

var sectionPrinted = false;
async Task Section(string id, string title, Func<Task> body)
{
    if (only is not null && only != id) return;
    Console.WriteLine($"{(sectionPrinted ? "\n" : "")}{id}. {title}");
    sectionPrinted = true;
    try { await body(); }
    catch (Exception ex)
    {
        Check(id + "!", "this section stopped on an exception, so its remaining checks did not run", false, OneLine($"{ex.GetType().Name}: {ex.Message}"));
    }
}

static async Task<JsonElement> Json(HttpResponseMessage r)
{
    var text = await r.Content.ReadAsStringAsync();
    try { return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement; }
    catch (JsonException) { return default; }
}
static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
static string OneLine(string? s) => s is null ? "" : string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
static string Url(string host, string path = "/") => $"https://{host}{path}";
static string Describe(RotatingIdp.Grant g)
    => $"#{g.Seq} {g.Type} by {(g.ClientId.Length > 0 ? g.ClientId : "?")} ({g.Auth}){(g.RefreshTokenUsed is null ? "" : " with " + RotatingIdp.Label(g.RefreshTokenUsed))} -> {g.Outcome}{(g.Ok ? $": {RotatingIdp.Label(g.AccessToken)}, {RotatingIdp.Label(g.RefreshTokenIssued)}" : "")}";
static string VersionOf(Assembly a) => a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? a.GetName().Version?.ToString() ?? "?";
static string TargetOf(Assembly a) => a.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "?";
static string About(Assembly a) => $"{a.GetName().Name} {VersionOf(a)}, built for {TargetOf(a)}";
static string BearerOf(IReadOnlyDictionary<string, string> headers) => headers.TryGetValue("Authorization", out var v) && v.StartsWith("Bearer ") ? v["Bearer ".Length..] : "";
// The value of one query parameter of a URL: null when the parameter is absent, "" when it is present and empty.
static string? QueryValue(string url, string name)
    => url.Length > 0 && QueryHelpers.ParseQuery(new Uri(url).Query).TryGetValue(name, out var v) ? v.ToString() : null;
static async Task<bool> Until(Func<bool> condition, int milliseconds = 3000)
{
    for (var waited = 0; waited < milliseconds && !condition(); waited += 20) await Task.Delay(20);
    return condition();
}

const string H1 = "app1.spa.test", H2 = "app2.spa.test", H3 = "app3.spa.test", H3Port = "app3.spa.test:8443", SessionCookie = "__Host-spa-session";

var app1 = new AppEntry("app1", H1, "client-1", "secret-1", "openid profile api1/read", "https://orders.spa.test");
var app2 = new AppEntry("app2", H2, "client-2", "secret-2", "openid api2/read");

async Task<RotatingIdp> NewIdp(Action<RotatingIdp>? configure = null)
{
    var idp = new RotatingIdp();
    configure?.Invoke(idp);
    await idp.StartAsync();
    Register(idp, "client-1", "secret-1", H1);
    Register(idp, "client-2", "secret-2", H2);
    return idp;
}
static void Register(RotatingIdp idp, string clientId, string secret, string host)
    => idp.Clients[clientId] = new RotatingIdp.Client(secret, [$"https://{host}/signin-oidc"], [$"https://{host}/signout-callback-oidc"]);

async Task<(ProxyGateway Gw, Registry Reg)> NewGateway(RotatingIdp idp, TokenSwitches sw, ApiCatalog? catalog, params AppEntry[] apps)
{
    var reg = new Registry();
    reg.Publish(apps);
    return (await ProxyGateway.StartAsync(reg, idp.BaseUrl, sw, catalog ?? new ApiCatalog()), reg);
}

// Signs the user in on one host. The access token this login returns lives for <lifetime> seconds. A token with
// under a minute left is due for refresh, so 30 means "the next call must refresh".
async Task<Browser> Login(RotatingIdp idp, ProxyGateway gw, string host, Browser? browser = null, int lifetime = 300)
{
    browser ??= new Browser(gw.BaseUrl, idp.BaseUrl);
    idp.AccessTokenLifetime = lifetime;
    var (final, hops) = await browser.Navigate(Url(host));
    idp.AccessTokenLifetime = 300;
    if (final.StatusCode != HttpStatusCode.OK)
        throw new InvalidOperationException($"login on {host} failed with {(int)final.StatusCode} after {string.Join(" > ", hops.Select(h => h.Split('?')[0]))}: {await final.Content.ReadAsStringAsync()}");
    return browser;
}

async Task<(HttpStatusCode Status, JsonElement Body)> Token(Browser b, string host, string query = "", IDictionary<string, string>? cookies = null, IDictionary<string, string>? headers = null)
{
    var r = await b.Get(Url(host, "/bff/token" + query), cookies, headers);
    return (r.StatusCode, await Json(r));
}

// Leaves the session with an access token that is due for refresh, without waiting for one to age.
async Task MakeDue(RotatingIdp idp, Browser b, string host)
{
    idp.AccessTokenLifetime = 30;
    var (status, body) = await Token(b, host, "?force=1");
    idp.AccessTokenLifetime = 300;
    if (status != HttpStatusCode.OK) throw new InvalidOperationException($"could not prepare a token due for refresh on {host}: {(int)status} {body}");
}

static AuthenticationProperties Stored(ProxyGateway gw, string appId)
    => TicketSerializer.Default.Deserialize(gw.Sessions.All.Single(r => r.AppId == appId).Ticket)!.Properties;

// A request that authenticated, and so read the session, before another request's refresh finished, and asks for
// its token only afterwards. The session's token must be due when this is called.
async Task<(HttpStatusCode Status, JsonElement Body, List<RotatingIdp.Grant> Grants)> LateArrival(RotatingIdp idp, ProxyGateway gw, Browser browser, string lateQuery = "")
{
    var before = idp.RefreshGrants.Count;
    var cookies = browser.CookiesFor(H1);
    gw.Flow.ResetHold();
    var late = browser.Get(Url(H1, "/bff/token" + lateQuery), cookies, new Dictionary<string, string> { ["X-Test-Hold"] = "1" });
    await gw.Flow.Parked.WaitAsync(TimeSpan.FromSeconds(10));   // it has authenticated and is waiting
    await Token(browser, H1, "", cookies);                       // another request refreshes, which rotates the refresh token
    gw.Flow.Release();
    var response = await late;
    return (response.StatusCode, await Json(response), idp.RefreshGrants.Skip(before).ToList());
}

// A browser that gives up: it sends the request and drops the connection after <afterMs> milliseconds.
static async Task Abandon(ProxyGateway gw, string host, string path, IDictionary<string, string> cookies, int afterMs)
{
    using var handler = new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, UseProxy = false };
    using var client = new HttpClient(handler);
    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(gw.BaseUrl), path));
    request.Headers.Host = host;
    request.Headers.Add("X-Forwarded-Proto", "https");
    request.Headers.Add("Cookie", string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}")));
    using var cts = new CancellationTokenSource(afterMs);
    try { await client.SendAsync(request, cts.Token); }
    catch (OperationCanceledException) { }
}

// One GET with the request target exactly as written. HttpClient would tidy the path before sending it.
static async Task<int> RawGet(ProxyGateway gw, string host, string target, IDictionary<string, string> cookies)
{
    var address = new Uri(gw.BaseUrl);
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(address.Host, address.Port);
    await using var stream = tcp.GetStream();
    var request = $"GET {target} HTTP/1.1\r\nHost: {host}\r\nX-Forwarded-Proto: https\r\nCookie: {string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}"))}\r\nConnection: close\r\n\r\n";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
    using var reader = new StreamReader(stream, Encoding.ASCII);
    var statusLine = await reader.ReadLineAsync() ?? "";
    await reader.ReadToEndAsync();
    return statusLine.Split(' ') is { Length: >= 2 } parts && int.TryParse(parts[1], out var status) ? status : 0;
}

// Rounds of fifty parallel API calls on app 1, each round starting with a token due for refresh.
async Task<BurstResult> Bursts(RotatingIdp idp, ProxyGateway gw, EchoApi orders, Browser browser, int rounds, int providerDelayMs)
{
    int second = 0, none = 0, refused = 0, failed = 0, stale = 0, ended = 0;
    for (var round = 0; round < rounds; round++)
    {
        await MakeDue(idp, browser, H1);
        idp.RefreshDelayMs = providerDelayMs;
        int before = idp.RefreshGrants.Count, seenBefore = orders.Count;
        var jar = browser.CookiesFor(H1);
        var calls = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => browser.Get(Url(H1, $"/api/orders/whoami?i={i}"), jar)));
        idp.RefreshDelayMs = 0;
        var grants = idp.RefreshGrants.Skip(before).ToList();
        var fresh = grants.FirstOrDefault(g => g.Ok)?.AccessToken;
        if (grants.Count > 1) second++;
        if (grants.Count == 0) none++;
        refused += grants.Count(g => !g.Ok);
        failed += calls.Count(r => r.StatusCode != HttpStatusCode.OK);
        stale += orders.Requests.Skip(seenBefore).Count(seen => BearerOf(seen.Headers) != fresh);
        if (gw.Sessions.CountFor("app1") == 0)
        {
            ended++;
            await Login(idp, gw, H1, browser);
        }
    }
    return new BurstResult(rounds, second, none, refused, failed, stale, ended);
}

Assembly tokenLibrary = typeof(UserTokenManagementOptions).Assembly, yarp = typeof(IProxyStateLookup).Assembly;
Console.WriteLine($"runtime {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
foreach (var assembly in new[] { typeof(OpenIdConnectHandler).Assembly, tokenLibrary, typeof(Scheme).Assembly, typeof(Duende.IdentityModel.OidcConstants).Assembly, yarp })
    Console.WriteLine($"  {About(assembly)}");
Console.WriteLine();

// =====================================================================================================
// A. Token management as the plan configures it: a sentinel scheme, and both scheme names on every call
// =====================================================================================================
await Section("A", "Duende.AccessTokenManagement under derived schemes (G0 item 4)", async () =>
{
    Check("A0", "the token-management assemblies in this process are the shipped 4.2.0 builds for .NET 10",
        VersionOf(tokenLibrary).StartsWith("4.2.0") && VersionOf(typeof(Scheme).Assembly).StartsWith("4.2.0") && TargetOf(tokenLibrary).StartsWith(".NETCoreApp,Version=v10."),
        About(tokenLibrary));

    var idp = await NewIdp();
    var (gw, registry) = await NewGateway(idp, new TokenSwitches(), null, app1, app2);
    var alice = new Browser(gw.BaseUrl, idp.BaseUrl);

    // The first request this process ever serves is for app 2.
    await Login(idp, gw, H2, alice, lifetime: 30);
    var first2 = await Token(alice, H2);

    await Login(idp, gw, H1, alice, lifetime: 30);
    var loginCookie = alice.CookiesFor(H1)[SessionCookie];
    var loginKey = gw.Sessions.KeysFor("app1").Single();
    var loginExpires = Stored(gw, "app1").ExpiresUtc;
    await Task.Delay(1100);                             // the ticket's expiry is kept to the second: let one pass, so A6 can tell a reset from none
    idp.AccessTokenLifetime = 30;                       // the token this refresh returns is due again at once, for A4
    var first1 = await Token(alice, H1);
    idp.AccessTokenLifetime = 300;

    var codes = idp.Grants.Where(g => g.Type == "authorization_code").ToList();
    var refreshes = idp.RefreshGrants;
    Check("A1", "the gate's pinning test holds: the first request is on app 2, then a refresh on app 1 uses client 1 and app 1's refresh token. B3 shows it holds without the sentinel as well",
        refreshes.Count == 2
        && refreshes[0] is { ClientId: "client-2", Ok: true } && refreshes[0].RefreshTokenUsed == codes.Single(g => g.ClientId == "client-2").RefreshTokenIssued
        && refreshes[1] is { ClientId: "client-1", Ok: true } && refreshes[1].RefreshTokenUsed == codes.Single(g => g.ClientId == "client-1").RefreshTokenIssued
        && Str(first2.Body, "accessToken") == refreshes[0].AccessToken && Str(first1.Body, "accessToken") == refreshes[1].AccessToken,
        string.Join(" | ", refreshes.Select(Describe)));

    var oidc = gw.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();
    OpenIdConnectOptions o1 = oidc.Get(SchemeNames.Oidc("app1")), o2 = oidc.Get(SchemeNames.Oidc("app2"));
    Check("A2", "with the sentinel, the library's options hook attaches to no app, and its refresh uses the one shared backchannel and metadata manager",
        o1.BackchannelHttpHandler is null && o2.BackchannelHttpHandler is null
        && idp.Grants.Count == 4 && idp.Grants.All(g => g.UserAgent == ProxyGateway.BackchannelAgent) && idp.DiscoveryFetches == 1,
        $"all {idp.Grants.Count} token requests came from the shared backchannel; discovery was fetched {idp.DiscoveryFetches} time(s)");

    // The other half of B1: the same resolution that throws without the sentinel, on a gateway that has served nothing.
    var idle = new Registry();
    idle.Publish([app1, app2]);
    var unstarted = ProxyGateway.Build(idle, idp.BaseUrl, new TokenSwitches(), new ApiCatalog());
    string? buildError = null;
    try { unstarted.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(SchemeNames.Oidc("app1")); }
    catch (Exception ex) { buildError = $"{ex.GetType().Name}: {ex.Message}"; }
    await unstarted.App.DisposeAsync();
    Check("A3", "with the sentinel, the options hook and an app's options build outside any request", buildError is null, OneLine(buildError));

    var third = await Token(alice, H1);
    var g3 = idp.RefreshGrants[^1];
    Check("A4", "the session keeps the rotated refresh token: the next refresh presents it and is granted (F-03)",
        idp.RefreshGrants.Count == 3 && g3.Ok && g3.RefreshTokenUsed == refreshes[1].RefreshTokenIssued
        && Stored(gw, "app1").GetTokenValue("refresh_token") == g3.RefreshTokenIssued && Str(third.Body, "accessToken") == g3.AccessToken,
        Describe(g3));

    var grantsSoFar = idp.Grants.Count;
    var again = await Token(alice, H1);
    var again2 = await Token(alice, H1);
    Check("A5", "no grant while the token has more than a minute left: two more calls return the same token",
        idp.Grants.Count == grantsSoFar && Str(again.Body, "accessToken") == g3.AccessToken && Str(again2.Body, "accessToken") == g3.AccessToken);

    var replay = await alice.Get(Url(H1, "/bff/session"), new Dictionary<string, string> { [SessionCookie] = loginCookie });
    Check("A6", "refresh rewrites the same server-side session: same key, same absolute expiry, and the cookie issued at login still identifies it (S-E2)",
        gw.Sessions.KeysFor("app1").SequenceEqual([loginKey]) && Stored(gw, "app1").ExpiresUtc == loginExpires && replay.StatusCode == HttpStatusCode.OK,
        $"expires {loginExpires:u} at login and {Stored(gw, "app1").ExpiresUtc:u} after two refreshes made more than a second later");

    var freshBare = await Token(alice, H1, "?schemes=none");
    await MakeDue(idp, alice, H1);
    var grantsBeforeBare = idp.Grants.Count;
    var dueBare = await Token(alice, H1, "?schemes=none");
    Finding("A7", "the cost of the sentinel: a call that leaves the scheme names off works only until a refresh is due. It is served while the token is fresh, then it throws, and nothing reaches the provider",
        freshBare.Status == HttpStatusCode.OK && dueBare.Status == HttpStatusCode.InternalServerError && Str(dueBare.Body, "code") == "exception" && idp.Grants.Count == grantsBeforeBare,
        OneLine(Str(dueBare.Body, "error")));

    await MakeDue(idp, alice, H1);
    idp.RevokeAllRefreshTokens("client-1");
    var refused = await Token(alice, H1, "?end=0");
    var sessionsAfterRefusal = gw.Sessions.CountFor("app1");
    var ended = await Token(alice, H1);
    var afterEnd = await alice.Get(Url(H1, "/bff/session"));
    Check("A8", "a refresh the provider refuses comes back as a failed result whose error is invalid_grant; the token step then ends the session, and the next request is anonymous",
        refused.Status == HttpStatusCode.OK && Str(refused.Body, "error") == "invalid_grant" && sessionsAfterRefusal == 1
        && ended.Status == HttpStatusCode.Unauthorized && Str(ended.Body, "code") == "session_ended" && gw.Sessions.CountFor("app1") == 0
        && afterEnd.StatusCode == HttpStatusCode.Unauthorized,
        $"the library itself left {sessionsAfterRefusal} session in place after the refusal; ending it is the gateway's step");

    await MakeDue(idp, alice, H2);
    idp.RefreshOutageStatus = 503;
    var outage = await Token(alice, H2);
    var sessionsDuringOutage = gw.Sessions.CountFor("app2");
    idp.RefreshOutageStatus = null;
    var recovered = await Token(alice, H2);
    Check("A9", "a provider that answers 503 has not refused anything: the call fails, the session stays, and the next call refreshes as usual. The library reports both cases the same way, and only the error text tells them apart",
        outage.Status == HttpStatusCode.ServiceUnavailable && Str(outage.Body, "code") == "idp_unavailable" && sessionsDuringOutage == 1
        && recovered.Status == HttpStatusCode.OK && idp.RefreshGrants[^1].Ok,
        $"the library's error for the 503 was \"{OneLine(Str(outage.Body, "error"))}\"");

    var refresh2 = Stored(gw, "app2").GetTokenValue("refresh_token");
    var logout = await alice.Send(HttpMethod.Post, Url(H2, "/bff/logout"));
    var logoutTo = logout.Headers.Location?.ToString() ?? "";
    var revocations = idp.Revocations;
    Check("A10", "logout revokes the session's current refresh token with app 2's own client, removes the session, and redirects to the provider with an ID token hint (F-04)",
        logout.StatusCode == HttpStatusCode.Redirect && logoutTo.StartsWith(idp.BaseUrl + "/endsession") && QueryValue(logoutTo, "id_token_hint") is { Length: > 0 }
        && revocations.Count == 1 && revocations[0] is { ClientId: "client-2", Known: true, Hint: "refresh_token" } && revocations[0].Token == refresh2
        && gw.Sessions.CountFor("app2") == 0,
        revocations.Count == 1 ? $"revocation by {revocations[0].ClientId} ({revocations[0].Auth}), hint {revocations[0].Hint}" : $"{revocations.Count} revocations");

    await Login(idp, gw, H1, alice, lifetime: 30);
    idp.Clients["client-1"] = idp.Clients["client-1"] with { Secret = "secret-1b" };
    var app1Rotated = app1 with { ClientSecret = "secret-1b" };
    registry.Publish([app1Rotated, app2]);
    var afterRotation = await Token(alice, H1);
    var g11 = idp.RefreshGrants[^1];
    Check("A11", "a client secret rotated at runtime is what the next refresh sends, on a session that began before the rotation (S-D3, for refresh)",
        afterRotation.Status == HttpStatusCode.OK && g11 is { ClientId: "client-1", Ok: true } && !idp.Grants.Any(g => g.Outcome == "invalid_client"),
        Describe(g11));

    Register(idp, "client-3", "secret-3", H3);
    registry.Publish([app1Rotated, app2, new AppEntry("app3", H3, "client-3", "secret-3", "openid api3/read")]);
    await Login(idp, gw, H3, alice, lifetime: 30);
    var onboarded = await Token(alice, H3);
    var g12 = idp.RefreshGrants[^1];
    Check("A12", "an app onboarded at runtime refreshes with its own client, with no restart: the library holds nothing per app",
        onboarded.Status == HttpStatusCode.OK && g12 is { ClientId: "client-3", Ok: true } && Str(onboarded.Body, "accessToken") == g12.AccessToken,
        Describe(g12));

    // A provider that does not rotate answers a refresh without a refresh token.
    await MakeDue(idp, alice, H3);
    var kept = Stored(gw, "app3").GetTokenValue("refresh_token");
    idp.RotateRefreshTokens = false;
    idp.AccessTokenLifetime = 30;
    await Token(alice, H3);
    idp.AccessTokenLifetime = 300;
    var keptAfter = Stored(gw, "app3").GetTokenValue("refresh_token");
    var reused = await Token(alice, H3);
    idp.RotateRefreshTokens = true;
    var lastTwo = idp.RefreshGrants.TakeLast(2).ToList();
    Check("A13", "against a provider that does not rotate, the session keeps its refresh token and the next refresh presents it again",
        reused.Status == HttpStatusCode.OK && keptAfter == kept && lastTwo.All(g => g.Ok && g.RefreshTokenUsed == kept && g.RefreshTokenIssued is null),
        string.Join(" | ", lastTwo.Select(Describe)));

    var longest = new string('a', 40);
    Check("A14", "the longest app id the registry contract allows, 40 characters, fits the library's limit on a scheme name",
        Scheme.TryParse(SchemeNames.Cookie(longest), out _, out _) && Scheme.TryParse(SchemeNames.Oidc(longest), out _, out _)
        && !Scheme.TryParse(SchemeNames.Cookie(new string('a', Scheme.MaxLength - "cookie:".Length + 1)), out _, out _),
        $"the limit is {Scheme.MaxLength}, so \"cookie:\" leaves {Scheme.MaxLength - "cookie:".Length} characters for the app id");

    await MakeDue(idp, alice, H3);
    await idp.App.StopAsync();
    var down = await Token(alice, H3);
    Check("A15", "with the provider unreachable, a refresh that is due fails with 503 and the session stays",
        down.Status == HttpStatusCode.ServiceUnavailable && Str(down.Body, "code") == "idp_unavailable" && gw.Sessions.CountFor("app3") == 1,
        $"the library's error was \"{OneLine(Str(down.Body, "error"))}\"");

    await gw.App.StopAsync();
});

// =====================================================================================================
// B. What the library does by default, and at the edges
// =====================================================================================================
await Section("B", "Probes: the library's defaults and edges", async () =>
{
    // B1 to B4: no sentinel and no scheme names, which is how the library is used with one fixed scheme.
    {
        var idp = await NewIdp();
        var registry = new Registry();
        registry.Publish([app1, app2]);
        var built = ProxyGateway.Build(registry, idp.BaseUrl, new TokenSwitches { Sentinel = false, ExplicitSchemes = false }, new ApiCatalog());

        string? thrown = null;
        try { built.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>(); }
        catch (Exception ex) { thrown = (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message; }
        Finding("B1", "without the sentinel the library's options hook cannot be built outside a request: it asks for the default challenge scheme, and the host-scoped provider has none to give",
            thrown is not null && thrown.Contains("No OpenID Connect authentication scheme configured"), OneLine(thrown) is { Length: > 0 } message ? message : "no exception");

        var gw = await built.StartAsync();
        var alice = new Browser(gw.BaseUrl, idp.BaseUrl);
        await Login(idp, gw, H2, alice, lifetime: 30);      // first request: app 2
        await Login(idp, gw, H1, alice, lifetime: 30);
        var oidc = gw.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();
        bool hook2 = oidc.Get(SchemeNames.Oidc("app2")).BackchannelHttpHandler is not null, hook1 = oidc.Get(SchemeNames.Oidc("app1")).BackchannelHttpHandler is not null;
        Finding("B2", "built inside a request instead, the hook binds to the app that request was for: app 2 gets the library's DPoP and client-assertion wiring, and app 1 never does",
            hook2 && !hook1, $"hook on oidc:app2: {hook2}; on oidc:app1: {hook1}");

        await Token(alice, H1);
        await Token(alice, H2);
        var r = idp.RefreshGrants;
        Finding("B3", "the pinning stops there: with no sentinel and no scheme names, a refresh on each app still used that app's own client, because the client is looked up through the default challenge scheme at call time",
            r.Count == 2 && r[0] is { ClientId: "client-1", Ok: true } && r[1] is { ClientId: "client-2", Ok: true }, string.Join(" | ", r.Select(Describe)));

        var styles = idp.Grants.GroupBy(g => g.Type).ToDictionary(g => g.Key, g => string.Join("/", g.Select(x => x.Auth).Distinct()));
        Finding("B4", "the two callers of the token endpoint authenticate differently: the handler's code exchange puts the secret in the form body, the library's refresh in a Basic header",
            styles.GetValueOrDefault("authorization_code") == "post" && styles.GetValueOrDefault("refresh_token") == "basic",
            string.Join("; ", styles.Select(kv => $"{kv.Key}: {kv.Value}")));
        await gw.App.StopAsync();
        await idp.App.StopAsync();
    }

    {
        var idp = await NewIdp();
        var (gw, _) = await NewGateway(idp, new TokenSwitches { PostBodyClientAuth = true }, null, app1, app2);
        var alice = await Login(idp, gw, H1, lifetime: 30);
        var t = await Token(alice, H1);
        var g = idp.RefreshGrants.SingleOrDefault();
        Check("B5", "ClientCredentialStyle.PostBody makes the refresh authenticate the way the code exchange does",
            t.Status == HttpStatusCode.OK && g is { Auth: "post", Ok: true }, g is null ? "no grant" : Describe(g));
        await gw.App.StopAsync();
        await idp.App.StopAsync();
    }

    {
        var idp = await NewIdp();
        var (gw, _) = await NewGateway(idp, new TokenSwitches(), null, app1, app2);
        var alice = await Login(idp, gw, H1, lifetime: 30);

        idp.RefreshGraceSeconds = 0;
        var strict = await LateArrival(idp, gw, alice);
        Finding("B6", "the library's single-flight does not cover a request that read the session before another request's refresh finished: it presents the rotated-out refresh token, a provider with no grace window refuses it, and the plan's rule then ends a session whose refresh had just succeeded",
            strict.Grants.Count == 2 && strict.Grants[0].Ok && strict.Grants[1].Outcome == "invalid_grant" && strict.Grants[1].RefreshTokenUsed == strict.Grants[0].RefreshTokenUsed
            && strict.Status == HttpStatusCode.Unauthorized && Str(strict.Body, "code") == "session_ended" && gw.Sessions.CountFor("app1") == 0,
            $"{string.Join(" | ", strict.Grants.Select(Describe))}; sessions left for app1: {gw.Sessions.CountFor("app1")}");

        await Login(idp, gw, H1, alice, lifetime: 30);
        idp.RefreshGraceSeconds = 10;
        var graced = await LateArrival(idp, gw, alice);
        Finding("B7", "a provider that still honours a rotated-out refresh token inside a grace window, the way this test provider does, grants the late request and the session survives. Whether Cognito's 10-second window works that way is for G0 item 2 to show",
            graced.Grants.Count == 2 && graced.Grants.All(g => g.Ok) && graced.Grants[1].RefreshTokenUsed == graced.Grants[0].RefreshTokenUsed
            && Str(graced.Body, "code") == "ok" && gw.Sessions.CountFor("app1") == 1,
            string.Join(" | ", graced.Grants.Select(Describe)));
        await gw.App.StopAsync();
        await idp.App.StopAsync();
    }

    {
        var idp = await NewIdp();
        var (gw, _) = await NewGateway(idp, new TokenSwitches { RecheckOnRefusal = true }, null, app1, app2);
        var alice = await Login(idp, gw, H1, lifetime: 30);
        idp.RefreshGraceSeconds = 0;
        var guarded = await LateArrival(idp, gw, alice);
        Check("B8", "a guard keeps the session with no grace window: on a refusal the token step re-reads the session, finds a newer refresh token there, and uses that session's access token",
            guarded.Grants.Count == 2 && guarded.Grants[1].Outcome == "invalid_grant" && Str(guarded.Body, "code") == "ok-after-recheck"
            && Str(guarded.Body, "accessToken") == guarded.Grants[0].AccessToken && gw.Sessions.CountFor("app1") == 1,
            $"{string.Join(" | ", guarded.Grants.Select(Describe))}. The provider still saw a rotated-out token replayed; one that revokes the whole family on replay would end the session at its next refresh");

        var quiet = await alice.Get(Url(H1, "/bff/token"));
        var refreshing = await alice.Get(Url(H1, "/bff/token?force=1"));
        var reissued = refreshing.Headers.TryGetValues("Set-Cookie", out var setCookies) && setCookies.Any(c => c.StartsWith(SessionCookie + "="));
        var cacheControl = refreshing.Headers.CacheControl?.ToString() ?? "";
        Finding("B9", "every refresh re-issues the session cookie and marks the response no-store: the library's default store saves the new tokens by signing the user in again. A call that does not refresh sets neither",
            reissued && cacheControl.Contains("no-store") && !quiet.Headers.Contains("Set-Cookie") && quiet.Headers.CacheControl is null,
            $"on the refreshing call: Set-Cookie: {SessionCookie}=...; Cache-Control: {cacheControl}");
        await gw.App.StopAsync();
        await idp.App.StopAsync();
    }

    {
        var idp = await NewIdp();
        var clock = new ShiftedClock();
        var (gw, _) = await NewGateway(idp, new TokenSwitches { Clock = clock }, null, app1, app2);
        var alice = await Login(idp, gw, H1);
        await Token(alice, H1);
        var atRest = idp.RefreshGrants.Count;
        clock.Shift = TimeSpan.FromMinutes(10);          // by the injected clock, the five-minute token expired five minutes ago
        await Token(alice, H1);
        var afterShift = idp.RefreshGrants.Count;
        await Token(alice, H1);                          // the token from that refresh expires five minutes from the SYSTEM clock's now
        var afterNext = idp.RefreshGrants.Count;
        Finding("B10", "the decision to refresh follows the injected TimeProvider, but the new token's expiry is computed from the system clock: once a test clock is moved past the token lifetime, every call refreshes",
            atRest == 0 && afterShift == 1 && afterNext == 2, $"refresh grants after each of three calls: {atRest}, {afterShift}, {afterNext}");
        await gw.App.StopAsync();
        await idp.App.StopAsync();
    }

    {
        var idp = await NewIdp();
        var (gw, _) = await NewGateway(idp, new TokenSwitches(), null, app1, app2);
        idp.RefreshTokenPadding = RefreshToken.MaxLength;            // this login's refresh token is longer than the library accepts
        var alice = await Login(idp, gw, H1);
        idp.RefreshTokenPadding = 0;
        var t = await Token(alice, H1);
        var atLimit = RefreshToken.TryParse(new string('r', RefreshToken.MaxLength), out _, out _);

        // The same limit when it is a refresh that returns the long token: the provider has rotated by the time the library reads the answer.
        var bob = await Login(idp, gw, H2, lifetime: 30);
        idp.RefreshGraceSeconds = 0;
        idp.RefreshTokenPadding = RefreshToken.MaxLength;
        var lost = await Token(bob, H2);
        idp.RefreshTokenPadding = 0;
        var rotatedAway = idp.RefreshGrants.LastOrDefault(g => g.ClientId == "client-2");
        var afterLoss = await Token(bob, H2);
        Finding("B11", "the library refuses a refresh token longer than 4,096 characters. With one from login in the session, every token call throws, even while the access token is fresh. One returned by a refresh is thrown away after the provider has rotated, and the next call ends the session. G0 item 2 must measure Cognito's, from login and from refresh",
            atLimit && t.Status == HttpStatusCode.InternalServerError && Str(t.Body, "code") == "exception"
            && lost.Status == HttpStatusCode.InternalServerError && rotatedAway is { Ok: true } && afterLoss.Status == HttpStatusCode.Unauthorized && Str(afterLoss.Body, "code") == "session_ended",
            $"limits: refresh token {RefreshToken.MaxLength}, access token {AccessToken.MaxLength}, ID token {IdentityToken.MaxLength}. The call failed with {OneLine(Str(t.Body, "error"))}");
        await gw.App.StopAsync();
        await idp.App.StopAsync();
    }

    {
        var idp = await NewIdp();
        var (gw, _) = await NewGateway(idp, new TokenSwitches { PassResource = true }, null, app1, app2);
        var alice = await Login(idp, gw, H1);            // a fresh token, bound to app 1's resource at login
        var first = await Token(alice, H1);
        var second = await Token(alice, H1);
        var r = idp.RefreshGrants;
        var held = Stored(gw, "app1").Items.Keys.Count(k => k.StartsWith(".Token.access_token"));
        Finding("B12", "naming the resource on the token call makes the library pass over the token from login: the first call after each login costs a refresh grant, which carries the resource parameter, and the session then holds two access tokens",
            r.Count == 1 && r[0] is { SentResource: true, Ok: true } && Str(first.Body, "accessToken") == r[0].AccessToken && Str(second.Body, "accessToken") == r[0].AccessToken && held == 2,
            $"{string.Join(" | ", r.Select(Describe))}; access tokens in the session: {held}. Left off, as in section A, the refresh request carries no resource");
        await gw.App.StopAsync();
        await idp.App.StopAsync();
    }

    {
        var idp = await NewIdp(i => i.IdTokenOnRefresh = false);
        var (gw, _) = await NewGateway(idp, new TokenSwitches(), null, app1, app2);
        var alice = await Login(idp, gw, H1, lifetime: 30);
        var atLogin = Stored(gw, "app1").GetTokenValue("id_token") is { Length: > 0 };
        await Token(alice, H1);
        var afterRefresh = Stored(gw, "app1").GetTokenValue("id_token");
        var logout = await alice.Send(HttpMethod.Post, Url(H1, "/bff/logout"));
        var to = logout.Headers.Location?.ToString() ?? "";
        var hint = QueryValue(to, "id_token_hint");
        Finding("B13", "a refresh response that carries no ID token blanks the one stored at login, so the logout that follows gives the provider an empty id_token_hint",
            atLogin && string.IsNullOrEmpty(afterRefresh) && to.StartsWith(idp.BaseUrl + "/endsession") && string.IsNullOrEmpty(hint),
            $"ID token in the session at login: {(atLogin ? "present" : "absent")}; after the refresh: {(string.IsNullOrEmpty(afterRefresh) ? "blank" : "present")}; id_token_hint on the logout redirect: {(hint is null ? "absent" : hint.Length == 0 ? "present and empty" : "present")}. Cognito returns an ID token on refresh");
        await gw.App.StopAsync();
        await idp.App.StopAsync();
    }

    {
        var idp = await NewIdp(i => i.AdvertiseRevocation = false);
        var (gw, _) = await NewGateway(idp, new TokenSwitches(), null, app1, app2);
        var alice = await Login(idp, gw, H1);
        var logout = await alice.Send(HttpMethod.Post, Url(H1, "/bff/logout"));
        var body = await Json(logout);
        Finding("B14", "when discovery lists no revocation endpoint the library's revoke call throws: a logout that revokes first fails before it has removed the session",
            logout.StatusCode == HttpStatusCode.InternalServerError && Str(body, "code") == "revocation_failed" && gw.Sessions.CountFor("app1") == 1,
            $"{OneLine(Str(body, "detail"))}; sessions left for app1: {gw.Sessions.CountFor("app1")}");
        await gw.App.StopAsync();
        await idp.App.StopAsync();
    }

    // B15 to B17: the browser walks away while its request's refresh is at the provider.
    async Task<(RotatingIdp.Grant? Grant, string? StoredRefresh, HttpStatusCode Next, string? NextCode, int Sessions)> WalkAway(TokenSwitches switches)
    {
        var idp = await NewIdp();
        var (gw, _) = await NewGateway(idp, switches, null, app1, app2);
        var alice = await Login(idp, gw, H1, lifetime: 30);
        idp.RefreshGraceSeconds = 0;
        idp.RefreshDelayMs = 300;
        await Abandon(gw, H1, "/bff/token", alice.CookiesFor(H1), afterMs: 100);
        await Until(() => idp.RefreshGrants.Count == 1);              // the provider finishes the grant regardless
        idp.RefreshDelayMs = 0;
        var grant = idp.RefreshGrants.FirstOrDefault();
        await Until(() => Stored(gw, "app1").GetTokenValue("refresh_token") == grant?.RefreshTokenIssued, 1000);
        var stored = Stored(gw, "app1").GetTokenValue("refresh_token");
        var next = await Token(alice, H1);
        var outcome = (grant, stored, next.Status, Str(next.Body, "code"), gw.Sessions.CountFor("app1"));
        await gw.App.StopAsync();
        await idp.App.StopAsync();
        return outcome;
    }

    var tied = await WalkAway(new TokenSwitches { PassRequestAborted = true });
    Finding("B15", "handing the request's cancellation token to the token call ties a refresh to one browser: if it disconnects while the provider is answering, the provider has rotated the refresh token and the gateway has stored nothing. At a provider with no grace window the next call is refused and the session ends",
        tied.Grant is { Ok: true } && tied.StoredRefresh == tied.Grant.RefreshTokenUsed && tied.Next == HttpStatusCode.Unauthorized && tied.NextCode == "session_ended" && tied.Sessions == 0,
        $"the provider issued {RotatingIdp.Label(tied.Grant?.RefreshTokenIssued)}; the session still held {RotatingIdp.Label(tied.StoredRefresh)}");

    var untied = await WalkAway(new TokenSwitches());
    Check("B16", "without that token, and with a ticket store that ignores cancellation as this in-memory one does, the refresh finishes after the browser has gone: the rotated refresh token is stored and the next call is served",
        untied.Grant is { Ok: true } && untied.StoredRefresh == untied.Grant.RefreshTokenIssued && untied.Next == HttpStatusCode.OK && untied.Sessions == 1,
        $"the provider issued {RotatingIdp.Label(untied.Grant?.RefreshTokenIssued)}; the session holds {RotatingIdp.Label(untied.StoredRefresh)}");

    var honoured = await WalkAway(new TokenSwitches { StoreHonoursCancellation = true });
    Finding("B17", "leaving that token off the call is not enough on the library's path: it saves by signing in again, and the cookie handler hands the request's cancellation token to the ticket store. A store that honours it drops the save once the browser has gone",
        honoured.Grant is { Ok: true } && honoured.StoredRefresh == honoured.Grant.RefreshTokenUsed && honoured.Next == HttpStatusCode.Unauthorized && honoured.Sessions == 0,
        $"the provider issued {RotatingIdp.Label(honoured.Grant?.RefreshTokenIssued)}; the session still held {RotatingIdp.Label(honoured.StoredRefresh)}");

    {
        var idp = await NewIdp();
        var (gw, _) = await NewGateway(idp, new TokenSwitches(), null, app1, app2);
        var alice = await Login(idp, gw, H1, lifetime: 30);
        var cookies = alice.CookiesFor(H1);
        idp.RefreshAnswerDelayMs = 300;                               // the provider rotates at once and answers 300 ms later
        var inFlight = Token(alice, H1, "", cookies);
        await Until(() => idp.RefreshGrants.Count == 1);
        var logout = await alice.Send(HttpMethod.Post, Url(H1, "/bff/logout"), cookies);
        await inFlight;
        idp.RefreshAnswerDelayMs = 0;
        var grant = idp.RefreshGrants.FirstOrDefault();
        var revoked = idp.Revocations;
        Finding("B18", "a logout that arrives while a refresh's answer is on its way revokes the refresh token in its own ticket, which the provider has just rotated out. Nobody revokes the successor, and this provider would still redeem it. Whether Cognito revokes a successor with its predecessor is for G0 item 2 to show",
            grant is { Ok: true } && logout.StatusCode == HttpStatusCode.Redirect && revoked.Count == 1 && revoked[0].Token == grant.RefreshTokenUsed
            && idp.IsRedeemable(grant.RefreshTokenIssued) && gw.Sessions.CountFor("app1") == 0,
            $"revoked: {(revoked.Count == 1 ? RotatingIdp.Label(revoked[0].Token) : revoked.Count + " tokens")}; issued by the refresh and still redeemable: {RotatingIdp.Label(grant?.RefreshTokenIssued)}");
        await gw.App.StopAsync();
        await idp.App.StopAsync();
    }
});

// =====================================================================================================
// C. YARP on .NET 10, doing what the upstream contract and task 4.2 ask of it
// =====================================================================================================
await Section("C", "Yarp.ReverseProxy on the .NET 10 runtime (G0 item 5)", async () =>
{
    Check("C0", "the YARP assembly in this process is the shipped 2.3.0 build, whose newest target is .NET 8, loaded on a .NET 10 runtime",
        VersionOf(yarp).StartsWith("2.3.0") && TargetOf(yarp).StartsWith(".NETCoreApp,Version=v8.") && Environment.Version.Major == 10,
        $"{About(yarp)}; runtime {Environment.Version}");

    var idp = await NewIdp();
    var orders = new EchoApi("orders");
    var reports = new EchoApi("reports");
    await orders.StartAsync();
    await reports.StartAsync();

    // A port that is bound and not listening refuses connections for as long as the socket lives.
    using var nobodyHome = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    nobodyHome.Bind(new IPEndPoint(IPAddress.Loopback, 0));

    var catalog = new ApiCatalog();
    catalog.Set("app1", new ApiDef("orders", orders.BaseUrl), new ApiDef("slow", orders.BaseUrl, TimeoutSeconds: 1), new ApiDef("dead", $"http://127.0.0.1:{((IPEndPoint)nobodyHome.LocalEndPoint!).Port}"));
    catalog.Set("app2", new ApiDef("reports", reports.BaseUrl));
    var (gw, registry) = await NewGateway(idp, new TokenSwitches(), catalog, app1, app2);
    var lookup = gw.Services.GetRequiredService<IProxyStateLookup>();

    var alice = new Browser(gw.BaseUrl, idp.BaseUrl);
    await Login(idp, gw, H1, alice);
    await Login(idp, gw, H2, alice);

    var get = await alice.Get(Url(H1, "/api/orders/whoami?x=1&y=two"));
    var getBody = await Json(get);
    var seenGet = orders.Requests[^1];
    Check("C1", "a GET goes through: host and /api/{name} select the route, the prefix is removed, the query is kept, and the upstream's status and body come back",
        get.StatusCode == HttpStatusCode.OK && Str(getBody, "api") == "orders" && seenGet is { Method: "GET", Path: "/whoami", Query: "?x=1&y=two" },
        $"upstream saw {seenGet.Method} {seenGet.Path}{seenGet.Query}");

    var csrf = Str(await Json(await alice.Get(Url(H1, "/bff/session"))), "csrfToken")!;
    var payload = "{\"order\":42,\"note\":\"" + new string('x', 5000) + "\"}";
    var post = await alice.Send(HttpMethod.Post, Url(H1, "/api/orders/echo"), null, new StringContent(payload, Encoding.UTF8, "application/json"), new Dictionary<string, string> { ["X-CSRF-TOKEN"] = csrf });
    var seenPost = orders.Requests[^1];
    var countAfterPost = orders.Count;
    var postNoToken = await alice.Send(HttpMethod.Post, Url(H1, "/api/orders/echo"), null, new StringContent(payload, Encoding.UTF8, "application/json"));
    Check("C2", "a POST with the antiforgery header is checked in the proxy pipeline and its body is forwarded whole; without the header it is refused and nothing is forwarded",
        post.StatusCode == HttpStatusCode.OK && seenPost.Method == "POST" && seenPost.Body == payload && seenPost.Headers.GetValueOrDefault("Content-Type", "").StartsWith("application/json")
        && postNoToken.StatusCode == HttpStatusCode.Forbidden && Str(await Json(postNoToken), "code") == "csrf_failed" && orders.Count == countAfterPost,
        $"{seenPost.Body.Length} of {payload.Length} characters arrived");

    // What a request looks like when the browser tries to speak for the load balancer. The load balancer appends the
    // address it saw to X-Forwarded-For, so its own entry is the last one; everything else here is the browser's.
    Dictionary<string, string> Spoofed() => new()
    {
        ["Authorization"] = "Bearer sent-by-the-browser",
        ["X-CSRF-TOKEN"] = csrf,
        ["X-Forwarded-For"] = "203.0.113.9, 198.51.100.7",
        ["X-Forwarded-Host"] = "evil.test",
        ["X-Forwarded-Port"] = "1",
        ["X-Forwarded-Server"] = "evil.test",
        ["X-Forwarded-Scheme"] = "gopher",
        ["X-Forwarded-Prefix"] = "/evil",
        ["Forwarded"] = "for=203.0.113.9;host=evil.test",
        ["X-Original-URL"] = "/admin",
        ["X-Original-Host"] = "evil.test",
        ["X-Real-IP"] = "203.0.113.9",
        ["Via"] = "1.1 evil.test",
    };
    // The three headers the upstream contract says the gateway sets. Any other name in Spoofed() must not arrive at all.
    string[] gatewayOwn = ["X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto"];

    var sentC3 = Spoofed();
    var probe = await alice.Get(Url(H1, "/api/orders/whoami?probe=c3"), null, sentC3);
    var seenProbe = orders.Requests[^1];
    var up = seenProbe.Headers;
    var arrived = sentC3.Keys.Where(k => k != "Authorization" && !gatewayOwn.Contains(k) && up.ContainsKey(k)).Order(StringComparer.OrdinalIgnoreCase).ToList();
    var otherForwarding = sentC3.Keys.Count(k => k is not ("Authorization" or "X-CSRF-TOKEN") && !gatewayOwn.Contains(k));
    Check("C3", $"the upstream gets the session's token and none of what this request tried to pass it: no Cookie, no CSRF header, no browser Authorization, and none of the {otherForwarding} other forwarding headers the browser sent. X-Forwarded-For, -Host and -Proto are the gateway's own, and nothing names the hop in front of it (N-11)",
        probe.StatusCode == HttpStatusCode.OK && seenProbe.Query == "?probe=c3"
        && BearerOf(up).Length > 0 && BearerOf(up) == Stored(gw, "app1").GetTokenValue("access_token")
        && !up.ContainsKey("Cookie") && arrived.Count == 0 && !up.ContainsKey("X-Original-For") && !up.ContainsKey("X-Original-Proto")
        && up.GetValueOrDefault("X-Forwarded-For") == "198.51.100.7" && up.GetValueOrDefault("X-Forwarded-Host") == H1 && up.GetValueOrDefault("X-Forwarded-Proto") == "https",
        $"headers the upstream received: {string.Join(", ", up.Keys.Order(StringComparer.OrdinalIgnoreCase))}; X-Forwarded-For: {up.GetValueOrDefault("X-Forwarded-For")}");

    // Legal header values in unusual spellings. A proxy that parses and re-serialises headers changes them.
    var verbatim = new Dictionary<string, string>
    {
        ["Cache-Control"] = "max-age=0 , no-cache",
        ["Accept"] = "application/json;q=0.9 ,  */*;q=0.1",
        ["If-None-Match"] = "W/\"abc\" ,\"def\"",
        ["Referer"] = "not a uri",
    };
    var untouched = await alice.Get(Url(H1, "/api/orders/whoami?probe=c4"), null, verbatim);
    var seenUntouched = orders.Requests[^1];
    var changed = verbatim.Where(kv => seenUntouched.Headers.GetValueOrDefault(kv.Key) != kv.Value).Select(kv => $"{kv.Key}: \"{seenUntouched.Headers.GetValueOrDefault(kv.Key)}\"").ToList();
    Check("C4", "request headers the gateway has no business with reach the upstream exactly as sent. A transform that enumerates the outgoing request's typed headers would rewrite some of them, so the one that removes forwarding headers reads them unvalidated",
        untouched.StatusCode == HttpStatusCode.OK && seenUntouched.Query == "?probe=c4" && changed.Count == 0,
        changed.Count == 0 ? $"{verbatim.Count} headers unchanged" : "changed: " + string.Join("; ", changed));

    var beyond = new Dictionary<string, string> { ["True-Client-IP"] = "203.0.113.9", ["X-Client-IP"] = "203.0.113.9", ["X-HTTP-Method-Override"] = "DELETE" };
    await alice.Get(Url(H1, "/api/orders/whoami?probe=c5"), null, beyond);
    var seenBeyond = orders.Requests[^1];
    Finding("C5", "removing forwarding headers by name is only as good as the list: headers of the same kind that are not on it go upstream as the browser sent them. Which names upstream APIs may trust, and which the gateway removes, is for the contract to say",
        seenBeyond.Query == "?probe=c5" && beyond.All(kv => seenBeyond.Headers.GetValueOrDefault(kv.Key) == kv.Value),
        $"sent and received unchanged: {string.Join(", ", beyond.Keys)}");

    var toReports = await alice.Get(Url(H2, "/api/reports/whoami?probe=c6"));
    string token1 = BearerOf(up), token2 = BearerOf(reports.Requests[^1].Headers);
    Check("C6", "each app's call carries a token issued to that app's own client, with app 1's bound audience (S-E3)",
        toReports.StatusCode == HttpStatusCode.OK && reports.Requests[^1].Query == "?probe=c6"
        && RotatingIdp.ClientOf(token1) == "client-1" && RotatingIdp.ClientOf(token2) == "client-2" && RotatingIdp.AudienceOf(token1) == app1.Resource && RotatingIdp.AudienceOf(token2) == "-",
        $"orders saw {RotatingIdp.Label(token1)} for audience {RotatingIdp.AudienceOf(token1)}; reports saw {RotatingIdp.Label(token2)}");

    // The requests to /headers carry a copy of the cookies and leave the browser's own jar alone, so a cookie that got through could not upset later checks.
    static bool Clean(HttpResponseMessage r) => !r.Headers.Contains("Set-Cookie") && !r.Headers.Any(h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    static string Csp(HttpResponseMessage r) => r.Headers.TryGetValues("Content-Security-Policy", out var v) ? string.Join(" | ", v) : "";
    var dirty = await alice.Get(Url(H1, "/api/orders/headers"), alice.CookiesFor(H1));
    var dirty500 = await alice.Get(Url(H1, "/api/orders/headers?status=500"), alice.CookiesFor(H1));
    Check("C7", "an upstream's Set-Cookie and Access-Control-* headers never reach the browser, on success or on an upstream error, and the gateway's CSP replaces the upstream's (N-12)",
        dirty.StatusCode == HttpStatusCode.OK && Clean(dirty) && Csp(dirty) == ProxyGateway.GatewayCsp && dirty.Headers.Contains("X-Upstream")
        && dirty500.StatusCode == HttpStatusCode.InternalServerError && Clean(dirty500) && Csp(dirty500) == ProxyGateway.GatewayCsp && dirty500.Headers.Contains("X-Upstream"),
        $"Content-Security-Policy: {Csp(dirty)}");

    var countBeforeAnonymous = orders.Count;
    var anon = await new Browser(gw.BaseUrl, idp.BaseUrl).Get(Url(H1, "/api/orders/whoami"));
    Check("C8", "an API call with no session gets 401 and a problem body from the pipeline step, never a redirect, and nothing reaches the upstream (N-19)",
        anon.StatusCode == HttpStatusCode.Unauthorized && anon.Headers.Location is null && anon.Content.Headers.ContentType?.MediaType == "application/problem+json"
        && Str(await Json(anon), "code") == "unauthenticated" && orders.Count == countBeforeAnonymous);

    int ordersBefore = orders.Count, reportsBefore = reports.Count;
    var foreign = await alice.Get(Url(H1, "/api/reports/whoami"));       // app 2 lists "reports"; app 1 does not
    var missing = await alice.Get(Url(H1, "/api/nothing/here"));
    Check("C9", "an API name the app does not list matches no route, even when another app lists it: 404 unknown_api, and no upstream is called (the first half of N-10)",
        foreign.StatusCode == HttpStatusCode.NotFound && Str(await Json(foreign), "code") == "unknown_api"
        && missing.StatusCode == HttpStatusCode.NotFound && Str(await Json(missing), "code") == "unknown_api"
        && orders.Count == ordersBefore && reports.Count == reportsBefore);

    // C10: paths built to leave the prefix, sent exactly as written.
    string[] resolved = ["/api/orders/../reports/whoami", "/api/orders/%2e%2e/reports/whoami"];
    string[] forwarded = ["/api/orders/..%2freports/whoami", "/api/orders/a%5c..%5c..%5cb", "/api/orders/a/..;/b", "/api/orders//whoami"];
    var escapes = new List<(string Target, int Status, string Reached)>();
    foreach (var target in resolved.Concat(forwarded))
    {
        int o = orders.Count, r = reports.Count;
        var status = await RawGet(gw, H1, target, alice.CookiesFor(H1));
        escapes.Add((target, status, reports.Count > r ? "reports" : orders.Count > o ? "orders as " + orders.Requests[^1].Path : "no upstream"));
    }
    Finding("C10", "YARP forwards the rest of a path as it arrives: with an encoded slash or backslash, a \"..;\" segment or a doubled slash, the request reaches the app's own upstream. Dot segments, plain or written %2e, are resolved before routing, and none of these reaches another app's upstream. Refusing such paths is the request gate's work, ahead of the proxy (the second half of N-10)",
        escapes.Take(2).All(e => e is { Status: 404, Reached: "no upstream" }) && escapes.Skip(2).All(e => e.Status == 200 && e.Reached.StartsWith("orders")),
        string.Join(" | ", escapes.Select(e => $"{e.Target} -> {e.Status}, {e.Reached}")));

    // C11: the route table follows the registry.
    var beforeAdd = await alice.Get(Url(H1, "/api/extra/whoami"));
    catalog.Set("app1", [.. catalog.For("app1"), new ApiDef("extra", reports.BaseUrl)]);
    registry.Publish([app1, app2]);
    var extraAdded = await Until(() => lookup.TryGetRoute(ProxyConfig.RouteId("app1", "extra"), out _));
    var viaExtra = await alice.Get(Url(H1, "/api/extra/whoami?probe=c11"));
    var extraSeen = reports.Requests.Count > 0 && reports.Requests[^1].Query == "?probe=c11" && RotatingIdp.ClientOf(BearerOf(reports.Requests[^1].Headers)) == "client-1";
    catalog.Set("app1", catalog.For("app1").Where(a => a.Name != "extra").ToArray());
    var app3 = new AppEntry("app3", H3Port, "client-3", "secret-3", "openid api3/read");
    Register(idp, "client-3", "secret-3", H3Port);
    catalog.Set("app3", new ApiDef("reports", reports.BaseUrl));
    registry.Publish([app1, app2, app3]);
    var extraRemoved = await Until(() => !lookup.TryGetRoute(ProxyConfig.RouteId("app1", "extra"), out _) && lookup.TryGetRoute(ProxyConfig.RouteId("app3", "reports"), out _));
    var reportsCount = reports.Count;
    var afterRemove = await alice.Get(Url(H1, "/api/extra/whoami"));
    var extraGone = reports.Count == reportsCount;
    await Login(idp, gw, H3Port, alice);
    var viaPort = await alice.Get(Url(H3Port, "/api/reports/whoami?probe=c11-port"));
    var seenPort = reports.Requests[^1];
    var routeIds = lookup.GetRoutes().Select(r => r.Config.RouteId).Order(StringComparer.Ordinal).ToList();
    Check("C11", "the route table follows the registry with no restart: an API added to an app is proxied, the same API removed again is 404 unknown_api, and a new app on a host with a port is proxied with its own client's token (task 4.1)",
        beforeAdd.StatusCode == HttpStatusCode.NotFound && Str(await Json(beforeAdd), "code") == "unknown_api"
        && extraAdded && viaExtra.StatusCode == HttpStatusCode.OK && extraSeen
        && extraRemoved && afterRemove.StatusCode == HttpStatusCode.NotFound && Str(await Json(afterRemove), "code") == "unknown_api" && extraGone
        && viaPort.StatusCode == HttpStatusCode.OK && seenPort.Query == "?probe=c11-port" && RotatingIdp.ClientOf(BearerOf(seenPort.Headers)) == "client-3"
        && seenPort.Headers.GetValueOrDefault("X-Forwarded-Host") == H3Port
        && routeIds.SequenceEqual(["app1/dead", "app1/orders", "app1/slow", "app2/reports", "app3/reports"]),
        $"routes now: {string.Join(", ", routeIds)}");

    // C12: F-10 and S-E1 through the proxy, against a provider that takes as long as a real one.
    await MakeDue(idp, alice, H1);
    idp.RefreshGraceSeconds = 0;
    idp.RefreshDelayMs = 200;
    int grantsBefore = idp.RefreshGrants.Count, seenBefore = orders.Count;
    var jar = alice.CookiesFor(H1);
    var calls = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => alice.Get(Url(H1, $"/api/orders/whoami?i={i}"), jar)));
    idp.RefreshDelayMs = 0;
    var burst = idp.RefreshGrants.Skip(grantsBefore).ToList();
    var tokensUpstream = orders.Requests.Skip(seenBefore).Select(s => BearerOf(s.Headers)).Distinct().ToList();
    var sessionStillValid = (await alice.Get(Url(H1, "/bff/session"))).StatusCode == HttpStatusCode.OK;
    Check("C12", "fifty parallel API calls on a token due for refresh, at a provider that takes 200 ms to answer: one refresh grant, none refused, and all fifty are forwarded with the new token (F-10, S-E1)",
        calls.All(r => r.StatusCode == HttpStatusCode.OK) && burst.Count == 1 && burst[0].Ok && orders.Count == seenBefore + 50
        && tokensUpstream.Count == 1 && tokensUpstream[0] == burst[0].AccessToken,
        $"{burst.Count} grant(s) at a provider with no grace window; {tokensUpstream.Count} distinct token(s) reached the upstream");

    await MakeDue(idp, alice, H1);
    var refreshingCall = await alice.Get(Url(H1, "/api/orders/headers"), alice.CookiesFor(H1));
    var mergedCache = refreshingCall.Headers.TryGetValues("Cache-Control", out var cc) ? string.Join(", ", cc) : "";
    Finding("C13", "on a proxied call that refreshes, the session cookie the library re-issues is removed together with the upstream's cookies, and its no-store is merged with the upstream's caching header. The cookie the browser already holds keeps working, because it carries only the session key",
        calls.All(r => !r.Headers.Contains("Set-Cookie")) && !refreshingCall.Headers.Contains("Set-Cookie") && sessionStillValid && mergedCache.Contains("no-store") && mergedCache.Contains("max-age=60"),
        $"Cache-Control on the refreshing call: {mergedCache}");

    // C14: the same burst at a provider that answers at once, as an in-process test provider does. This one is a rate, not a certainty.
    const int Rounds = 100;
    var instant = await Bursts(idp, gw, orders, alice, Rounds, providerDelayMs: 0);
    Finding("C14", "the same fifty calls at a provider that answers at once: in some rounds a call that read the session before the refresh was stored asks for its token after the flight has ended, and presents the rotated-out refresh token. With no grace window that grant is refused and the plan's rule ends the session. This is B6 happening unprompted",
        instant.WithSecondGrant > 0 && instant.RefusedGrants > 0 && instant.SessionsEnded > 0, instant.ToString());

    var redirect = await alice.Get(Url(H1, "/api/orders/redirect"));
    Check("C15", "an upstream redirect is passed to the browser as it is, not followed",
        redirect.StatusCode == HttpStatusCode.Redirect && redirect.Headers.Location?.ToString() == "https://elsewhere.test/landing");

    var dead = await alice.Get(Url(H1, "/api/dead/anything"));
    var deadBody = await Json(dead);
    Check("C16", "an unreachable upstream gives 502, and the pipeline step can still write the contract's problem body",
        dead.StatusCode == HttpStatusCode.BadGateway && Str(deadBody, "code") == "upstream_unavailable", $"YARP reported {Str(deadBody, "detail")}");

    var timer = Stopwatch.StartNew();
    var slow = await alice.Get(Url(H1, "/api/slow/slow?ms=6000"));
    timer.Stop();
    var slowBody = await Json(slow);
    Check("C17", "an upstream that sends nothing for longer than the API's timeout gives 504 when the timeout expires",
        slow.StatusCode == HttpStatusCode.GatewayTimeout && Str(slowBody, "code") == "upstream_timeout" && timer.Elapsed < TimeSpan.FromSeconds(4),
        $"{timer.ElapsedMilliseconds} ms with a 1-second ActivityTimeout; YARP reported {Str(slowBody, "detail")}");

    var stalled = await alice.Get(Url(H1, "/api/slow/stall?ms=6000"));
    var stalledBody = await Json(stalled);
    timer.Restart();
    var dripped = await alice.Get(Url(H1, "/api/slow/drip?count=6&gap=400"));
    var drippedText = await dripped.Content.ReadAsStringAsync();
    timer.Stop();
    Finding("C18", "that timeout limits silence, not the whole exchange, and 504 covers only silence before the upstream's headers: an upstream that sends headers and then stalls gets 502, and one that sends a byte every 400 ms runs well past the limit and is answered 200",
        stalled.StatusCode == HttpStatusCode.BadGateway && Str(stalledBody, "code") == "upstream_unavailable"
        && dripped.StatusCode == HttpStatusCode.OK && drippedText == "......" && timer.Elapsed > TimeSpan.FromSeconds(2),
        $"headers then silence: {(int)stalled.StatusCode}, YARP reported {Str(stalledBody, "detail")}; six bytes over {timer.ElapsedMilliseconds} ms with a 1-second limit: {(int)dripped.StatusCode}");

    // C19: W3C trace context. The browser's own traceparent is replaced by one in the same trace, from the gateway's outgoing span.
    using (var listener = new ActivityListener { ShouldListenTo = _ => true, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded })
    {
        ActivitySource.AddActivityListener(listener);
        const string traceId = "0af7651916cd43dd8448eb211c80319c", browserSpan = "b7ad6b7169203331";
        var traced = await alice.Get(Url(H1, "/api/orders/whoami?probe=c19"), null, new Dictionary<string, string> { ["traceparent"] = $"00-{traceId}-{browserSpan}-01" });
        var seenTraced = orders.Requests[^1];
        var parts = seenTraced.Headers.GetValueOrDefault("traceparent", "").Split('-');
        var gatewayTrace = traced.Headers.TryGetValues("X-Test-Trace-Id", out var ids) ? ids.First() : "";
        Check("C19", "with tracing on, the upstream receives a W3C traceparent from the gateway's own span, in the gateway request's trace (groundwork for F-11). That trace continues the trace id the browser sent",
            seenTraced.Query == "?probe=c19" && parts.Length == 4 && parts[1] == traceId && parts[2] != browserSpan && gatewayTrace == traceId,
            parts.Length == 4 ? $"trace id {(parts[1] == traceId ? "kept" : "changed")}; span {(parts[2] != browserSpan ? "replaced" : "unchanged")}" : "no traceparent upstream");
    }
    await gw.App.StopAsync();

    // C20: YARP's own default for removing a response header.
    {
        var (gwDefault, _) = await NewGateway(idp, new TokenSwitches { StripOnSuccessOnly = true }, catalog, app1, app2);
        var bob = await Login(idp, gwDefault, H1);
        var ok = await bob.Get(Url(H1, "/api/orders/headers"), bob.CookiesFor(H1));
        var error = await bob.Get(Url(H1, "/api/orders/headers?status=500"), bob.CookiesFor(H1));
        Finding("C20", "YARP's header-removal transform acts on successful responses only unless told otherwise: left at its default, an upstream's 500 delivers its Set-Cookie to the browser",
            ok.StatusCode == HttpStatusCode.OK && !ok.Headers.Contains("Set-Cookie") && error.StatusCode == HttpStatusCode.InternalServerError && error.Headers.Contains("Set-Cookie"),
            "task 4.2 needs ResponseCondition.Always, which the gateway in C7 uses");
        await gwDefault.App.StopAsync();
    }

    // C21: the alternative the plan names for the token step.
    {
        var (gwTransform, _) = await NewGateway(idp, new TokenSwitches { TokenStep = TokenStep.Transform }, catalog, app1, app2);
        var carol = await Login(idp, gwTransform, H1);
        var before = orders.Count;
        var noSession = await new Browser(gwTransform.BaseUrl, idp.BaseUrl).Get(Url(H1, "/api/orders/whoami"));
        var refusedBeforeSend = orders.Count == before;
        var withSession = await carol.Get(Url(H1, "/api/orders/whoami?probe=c21"));
        Check("C21", "the alternative works too: a YARP request transform can fetch the token itself and stop the request with a 401 before anything is sent",
            noSession.StatusCode == HttpStatusCode.Unauthorized && Str(await Json(noSession), "code") == "unauthenticated" && refusedBeforeSend
            && withSession.StatusCode == HttpStatusCode.OK && orders.Requests[^1].Query == "?probe=c21" && RotatingIdp.ClientOf(BearerOf(orders.Requests[^1].Headers)) == "client-1",
            "either placement meets the contract; the pipeline step answers before YARP has picked a destination or built a request");
        await gwTransform.App.StopAsync();
    }

    // C22: what YARP's defaults do with forwarding headers, when the gateway removes none itself.
    {
        var (gwPlain, _) = await NewGateway(idp, new TokenSwitches { YarpDefaultHeadersOnly = true }, catalog, app1, app2);
        var dave = await Login(idp, gwPlain, H1);
        var sent = Spoofed();
        var plain = await dave.Get(Url(H1, "/api/orders/whoami?probe=c22"), null, sent);
        var seenPlain = orders.Requests[^1];
        var asSent = sent.Keys.Where(k => k != "Authorization" && seenPlain.Headers.GetValueOrDefault(k) == sent[k]).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var added = seenPlain.Headers.Keys.Where(k => k.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase) && !asSent.Contains(k, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        Finding("C22", "left to YARP's defaults, only X-Forwarded-For, -Host, -Proto and -Prefix and Forwarded are replaced or dropped. Every other forwarding header goes upstream exactly as the browser sent it, and the forwarded-headers middleware adds X-Original-For and X-Original-Proto, which name the hop in front of the gateway",
            plain.StatusCode == HttpStatusCode.OK && seenPlain.Query == "?probe=c22"
            && asSent.SequenceEqual(["Via", "X-Forwarded-Port", "X-Forwarded-Scheme", "X-Forwarded-Server", "X-Original-Host", "X-Original-URL", "X-Real-IP"], StringComparer.OrdinalIgnoreCase)
            && added.SequenceEqual(["X-Original-For", "X-Original-Proto"], StringComparer.OrdinalIgnoreCase),
            $"as the browser sent them: {string.Join(", ", asSent)}; added on the way: {string.Join(", ", added)}");
        await gwPlain.App.StopAsync();
    }

    await orders.App.StopAsync();
    await reports.App.StopAsync();
    await idp.App.StopAsync();
});

// =====================================================================================================
// D. The alternative for task 3.8: the gateway owns the orchestration, the library makes the provider calls
// =====================================================================================================
await Section("D", "The library's refresh and revoke calls under orchestration the gateway owns (G0 item 4, the alternative)", async () =>
{
    // This provider leaves the ID token out of a refresh response, as OpenID Connect allows.
    var idp = await NewIdp(i => i.IdTokenOnRefresh = false);
    var orders = new EchoApi("orders");
    await orders.StartAsync();
    var catalog = new ApiCatalog();
    catalog.Set("app1", new ApiDef("orders", orders.BaseUrl));
    var (gw, _) = await NewGateway(idp, new TokenSwitches { OwnOrchestration = true }, catalog, app1, app2);
    var alice = new Browser(gw.BaseUrl, idp.BaseUrl);

    await Login(idp, gw, H2, alice, lifetime: 30);       // the first request is on app 2, as in A1
    var first2 = await Token(alice, H2);
    await Login(idp, gw, H1, alice, lifetime: 30);
    var keyAtLogin = gw.Sessions.KeysFor("app1").Single();
    var idTokenAtLogin = Stored(gw, "app1").GetTokenValue("id_token");
    var refreshing = await alice.Get(Url(H1, "/bff/token"));
    var first1 = await Json(refreshing);
    var codes = idp.Grants.Where(g => g.Type == "authorization_code").ToList();
    var r = idp.RefreshGrants;
    Check("D1", "the library's refresh call, given the challenge scheme, uses each app's own client through the shared backchannel. The gateway writes the new tokens into the same session record, so the response carries no cookie and no caching header",
        r.Count == 2
        && r[0] is { ClientId: "client-2", Ok: true } && r[0].RefreshTokenUsed == codes.Single(g => g.ClientId == "client-2").RefreshTokenIssued
        && r[1] is { ClientId: "client-1", Ok: true } && r[1].RefreshTokenUsed == codes.Single(g => g.ClientId == "client-1").RefreshTokenIssued
        && Str(first2.Body, "accessToken") == r[0].AccessToken && Str(first1, "accessToken") == r[1].AccessToken
        && idp.Grants.All(g => g.UserAgent == ProxyGateway.BackchannelAgent)
        && !refreshing.Headers.Contains("Set-Cookie") && refreshing.Headers.CacheControl is null
        && gw.Sessions.KeysFor("app1").SequenceEqual([keyAtLogin]) && Stored(gw, "app1").GetTokenValue("refresh_token") == r[1].RefreshTokenIssued,
        string.Join(" | ", r.Select(Describe)));

    // Every token issued during this check is due at once, so the held request has to refresh as well.
    await MakeDue(idp, alice, H1);
    idp.RefreshGraceSeconds = 0;
    idp.AccessTokenLifetime = 30;
    var late = await LateArrival(idp, gw, alice);
    idp.AccessTokenLifetime = 300;
    Check("D2", "a request that read the session before another request's refresh (B6) refreshes with what the store holds now, not with what its own ticket held: its grant presents the refresh token the first refresh returned, and a provider with no grace window grants it",
        late.Grants.Count == 2 && late.Grants.All(g => g.Ok) && late.Grants[1].RefreshTokenUsed == late.Grants[0].RefreshTokenIssued
        && late.Status == HttpStatusCode.OK && Str(late.Body, "accessToken") == late.Grants[1].AccessToken && gw.Sessions.CountFor("app1") == 1,
        string.Join(" | ", late.Grants.Select(Describe)));

    const int Rounds = 100;
    var instant = await Bursts(idp, gw, orders, alice, Rounds, providerDelayMs: 0);
    Check("D3", "fifty parallel API calls at a provider that answers at once and allows no grace (C14): exactly one refresh grant in every round, none refused, and every call forwarded with the new token",
        instant.WithSecondGrant == 0 && instant.WithoutGrant == 0 && instant.RefusedGrants == 0 && instant.FailedCalls == 0 && instant.ForwardedWithAnotherToken == 0 && instant.SessionsEnded == 0,
        instant.ToString());

    await MakeDue(idp, alice, H1);
    var grantsBefore = idp.RefreshGrants.Count;
    idp.RefreshDelayMs = 300;
    await Abandon(gw, H1, "/bff/token", alice.CookiesFor(H1), afterMs: 100);
    await Until(() => idp.RefreshGrants.Count == grantsBefore + 1);
    idp.RefreshDelayMs = 0;
    var abandoned = idp.RefreshGrants[^1];
    var storedInTime = await Until(() => Stored(gw, "app1").GetTokenValue("refresh_token") == abandoned.RefreshTokenIssued, 1000);
    var next = await Token(alice, H1);
    Check("D4", "a browser that disconnects while its refresh is at the provider (B15, B17) loses nothing: the rotated refresh token is stored, and the next call is served without another grant",
        abandoned.Ok && storedInTime && next.Status == HttpStatusCode.OK && Str(next.Body, "accessToken") == abandoned.AccessToken && idp.RefreshGrants.Count == grantsBefore + 1,
        Describe(abandoned));

    await MakeDue(idp, alice, H1);
    idp.RefreshOutageStatus = 503;
    var outage = await Token(alice, H1);
    var sessionsDuringOutage = gw.Sessions.CountFor("app1");
    idp.RefreshOutageStatus = null;
    await Task.Delay(400);                               // past the moment a failure is remembered for the calls queued behind it
    var recovered = await Token(alice, H1);
    Check("D5", "a provider that answers 503 has not refused anything here either: the call fails with 503, the session stays, and a later call refreshes as usual",
        outage.Status == HttpStatusCode.ServiceUnavailable && Str(outage.Body, "code") == "idp_unavailable" && sessionsDuringOutage == 1
        && recovered.Status == HttpStatusCode.OK && idp.RefreshGrants[^1].Ok);

    await MakeDue(idp, alice, H1);
    idp.RefreshOutageStatus = 503;
    idp.RefreshDelayMs = 200;
    int askedBefore = idp.RefreshGrants.Count, forwardedBefore = orders.Count;
    var jar = alice.CookiesFor(H1);
    var timer = Stopwatch.StartNew();
    var failing = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => alice.Get(Url(H1, $"/api/orders/whoami?i={i}"), jar)));
    timer.Stop();
    idp.RefreshOutageStatus = null;
    idp.RefreshDelayMs = 0;
    var asked = idp.RefreshGrants.Count - askedBefore;
    Check("D6", "an outage is shared by the calls already waiting: fifty parallel API calls on a due token, at a provider that answers 503 after 200 ms, ask the provider once and fail together. Nothing is forwarded and the session stays",
        failing.All(f => f.StatusCode == HttpStatusCode.ServiceUnavailable) && asked == 1 && timer.Elapsed < TimeSpan.FromSeconds(2)
        && orders.Count == forwardedBefore && gw.Sessions.CountFor("app1") == 1,
        $"{asked} refresh request(s) reached the provider; the fifty calls took {timer.ElapsedMilliseconds} ms");

    await Task.Delay(400);
    await MakeDue(idp, alice, H1);
    var cookies = alice.CookiesFor(H1);
    var grantsBeforeLogout = idp.RefreshGrants.Count;
    idp.RefreshAnswerDelayMs = 300;                      // the provider rotates at once and answers 300 ms later
    var inFlight = Token(alice, H1, "", cookies);
    await Until(() => idp.RefreshGrants.Count == grantsBeforeLogout + 1);
    var logout = await alice.Send(HttpMethod.Post, Url(H1, "/bff/logout"), cookies);
    var refreshed = await inFlight;
    idp.RefreshAnswerDelayMs = 0;
    var lastGrant = idp.RefreshGrants[^1];
    var to = logout.Headers.Location?.ToString() ?? "";
    var replay = await alice.Get(Url(H1, "/bff/session"), cookies);
    Check("D7", "a logout that arrives while a refresh's answer is on its way (B18) waits for it, revokes the refresh token the provider has just issued, and removes the session. The ID token from login, which no refresh response replaced (B13), is still there for the hint",
        refreshed.Status == HttpStatusCode.OK && lastGrant.Ok && logout.StatusCode == HttpStatusCode.Redirect
        && idp.Revocations.Count == 1 && idp.Revocations[0].Token == lastGrant.RefreshTokenIssued && !idp.IsRedeemable(lastGrant.RefreshTokenIssued)
        && gw.Sessions.CountFor("app1") == 0 && replay.StatusCode == HttpStatusCode.Unauthorized
        && idTokenAtLogin is { Length: > 0 } && QueryValue(to, "id_token_hint") == idTokenAtLogin,
        $"revoked: {(idp.Revocations.Count == 1 ? RotatingIdp.Label(idp.Revocations[0].Token) : idp.Revocations.Count + " tokens")}, the one the in-flight refresh returned; id_token_hint: {(QueryValue(to, "id_token_hint") is { Length: > 0 } ? "the login's ID token" : "absent or empty")}");

    await Login(idp, gw, H1, alice, lifetime: 30);
    idp.RevokeAllRefreshTokens("client-1");
    var refusedCall = await alice.Get(Url(H1, "/bff/token"));
    var refusedBody = await Json(refusedCall);
    var cookieCleared = refusedCall.Headers.TryGetValues("Set-Cookie", out var cleared) && cleared.Any(c => c.StartsWith(SessionCookie + "=;"));
    var afterRefusal = await alice.Get(Url(H1, "/bff/session"));
    Check("D8", "a refresh the provider refuses ends the session here as in A8: 401 session_ended, the record removed, the cookie cleared, and the next request anonymous",
        refusedCall.StatusCode == HttpStatusCode.Unauthorized && Str(refusedBody, "code") == "session_ended" && Str(refusedBody, "error") == "invalid_grant"
        && gw.Sessions.CountFor("app1") == 0 && cookieCleared && afterRefusal.StatusCode == HttpStatusCode.Unauthorized);

    await gw.App.StopAsync();
    await orders.App.StopAsync();
    await idp.App.StopAsync();

    // D9 and D10: the refresh call's own deadline, at a provider that takes longer than that.
    async Task<(HttpStatusCode Cut, string? CutCode, long CutMs, int SessionsAfterCut, List<RotatingIdp.Grant> Grants, HttpStatusCode Retry, string? RetryCode, int SessionsAfterRetry)> CutOff(int graceSeconds)
    {
        var slowIdp = await NewIdp();
        var (gwDeadline, _) = await NewGateway(slowIdp, new TokenSwitches { OwnOrchestration = true, ProviderDeadlineMs = 500 }, null, app1, app2);
        var erin = await Login(slowIdp, gwDeadline, H1, lifetime: 30);
        slowIdp.RefreshGraceSeconds = graceSeconds;
        slowIdp.RefreshDelayMs = 1200;
        var clock = Stopwatch.StartNew();
        var cut = await Token(erin, H1);
        clock.Stop();
        var sessionsAfterCut = gwDeadline.Sessions.CountFor("app1");
        await Until(() => slowIdp.RefreshGrants.Count == 1);          // the provider carries out the grant it was asked for
        slowIdp.RefreshDelayMs = 0;
        var retry = await Token(erin, H1);
        var outcome = (cut.Status, Str(cut.Body, "code"), clock.ElapsedMilliseconds, sessionsAfterCut, slowIdp.RefreshGrants.ToList(), retry.Status, Str(retry.Body, "code"), gwDeadline.Sessions.CountFor("app1"));
        await gwDeadline.App.StopAsync();
        await slowIdp.App.StopAsync();
        return outcome;
    }

    var graced = await CutOff(graceSeconds: 10);
    Check("D9", "the refresh call has a deadline of its own: a provider that takes longer is cut off, the call fails with 503 and the session stays",
        graced.Cut == HttpStatusCode.ServiceUnavailable && graced.CutCode == "idp_unavailable" && graced.CutMs < 1100 && graced.SessionsAfterCut == 1,
        $"cut off after {graced.CutMs} ms with a 500 ms deadline, at a provider that takes 1,200 ms. The retry, inside this test provider's 10-second grace window: {(int)graced.Retry}");

    var strict = await CutOff(graceSeconds: 0);
    Finding("D10", "a refresh the gateway gave up on may still have been carried out: the provider rotated the refresh token and the gateway never saw the answer. The retry presents the rotated-out token, and a provider with no grace window refuses it, which ends the session. A deadline is only safe with a grace window that covers the retry",
        strict.Cut == HttpStatusCode.ServiceUnavailable && strict.Grants.Count == 2 && strict.Grants[0].Ok && strict.Grants[1].Outcome == "invalid_grant"
        && strict.Grants[1].RefreshTokenUsed == strict.Grants[0].RefreshTokenUsed && strict.Retry == HttpStatusCode.Unauthorized && strict.RetryCode == "session_ended" && strict.SessionsAfterRetry == 0,
        string.Join(" | ", strict.Grants.Select(Describe)));
});

var bad = results.Count(r => r.Kind == "BAD");
Console.WriteLine($"\n{results.Count(r => r.Kind == "OK")} expectations held, {bad} did not, {results.Count(r => r.Kind == "FINDING")} findings confirmed, {results.Count(r => r.Kind == "not-rep")} probes not reproduced");
return bad == 0 ? 0 : 1;

/// <summary>What <c>Bursts</c> saw over its rounds.</summary>
sealed record BurstResult(int Rounds, int WithSecondGrant, int WithoutGrant, int RefusedGrants, int FailedCalls, int ForwardedWithAnotherToken, int SessionsEnded)
{
    public override string ToString()
        => $"{Rounds} rounds: {WithSecondGrant} with a second grant, {WithoutGrant} with none, {RefusedGrants} grant(s) refused, {FailedCalls} call(s) not answered 200, {ForwardedWithAnotherToken} forwarded with a token other than the new one, {SessionsEnded} session(s) ended";
}

/// <summary>A clock a test can move forward. Only "now" is shifted; timers run in real time.</summary>
sealed class ShiftedClock : TimeProvider
{
    public TimeSpan Shift;
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Shift;
}
