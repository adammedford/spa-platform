// PROTOTYPE for "Scheme names and callback dispatch for a shell and its fragments" (#771, map #765). Throwaway.
// The prototype skill's HTML shape does not fit: the question is what the real OpenIdConnectHandler does with N clients
// on one host, so this is a g0-style console harness, one line per check, non-zero exit on a miss.
// Against fixtures/TestIdp, which has no provider session and ignores prompt: a silent hop here is trivially silent,
// not proven silent. Tokens sit in ticket properties, not Duende ATM: per-set refresh is not exercised.
// Printed URLs are paths only; no code, state, cookie or token value is printed.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Spike;
using TestIdp;

var failures = 0;
void Check(string id, bool ok, string what)
{
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "ok  " : "MISS")} {id} {what}");
}
void Note(string id, string what) => Console.WriteLine($"note {id} {what}");

const string Host = "shop.test";
const string Origin = "https://" + Host;

await using var idp = new TestIdentityProvider();
await idp.StartAsync();

TestIdpClient Client(string id, string secret, params string[] callbacks)
{
    var c = new TestIdpClient(id, secret, new HashSet<string>(callbacks.Select(p => Origin + p), StringComparer.Ordinal),
        new HashSet<string>([Origin + "/signout-callback-oidc", Origin + "/"], StringComparer.Ordinal));
    idp.AddClient(c);
    return c;
}

ShellEntry Shell(params FragmentEntry[] fragments) => new("shop", Host, "c-shop", "s-shop", fragments);
FragmentEntry Frag(string id, string? secret = null) => new(id, "c-" + id, secret ?? "s-" + id);

Client("c-shop", "s-shop", "/signin-oidc");
foreach (var id in new[] { "orders", "billing", "reviews", "stall" }) Client("c-" + id, "s-" + id, "/signin-oidc/" + id, "/signin-oidc");
for (var k = 0; k < 8; k++) Client($"c-f{k}", $"s-f{k}", $"/signin-oidc/f{k}", "/signin-oidc");
// "stall"'s client registers only the shared path, so a per-path hop is refused at the provider and never comes back.
idp.AddClient(new TestIdpClient("c-stall", "s-stall", new HashSet<string>([Origin + "/signin-oidc"]), new HashSet<string>([Origin + "/"])));

Console.WriteLine($"PROTOTYPE fragments: handler {typeof(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler).Assembly.GetName().Version}, runtime {Environment.Version}");

async Task<(HttpResponseMessage Final, List<(string Path, int Status, double Ms)> Hops)> Go(Browser b, string url, int max = 80, Func<string, bool>? stopBefore = null)
{
    var hops = new List<(string, int, double)>();
    var current = url;
    for (var i = 0; i < max; i++)
    {
        var sw = Stopwatch.StartNew();
        var resp = await b.Get(current);
        var u = new Uri(current);
        hops.Add(((u.Authority == Host ? "" : "idp") + u.AbsolutePath, (int)resp.StatusCode, sw.Elapsed.TotalMilliseconds));
        if ((int)resp.StatusCode is not (301 or 302 or 303 or 307 or 308)) return (resp, hops);
        var loc = resp.Headers.Location!;
        current = loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri(current), loc).ToString();
        if (stopBefore?.Invoke(current) == true) { hops.Add((current, 0, 0)); return (resp, hops); }
    }
    throw new InvalidOperationException("redirect loop");
}

static int Redirects(List<(string Path, int Status, double Ms)> hops) => hops.Count(h => h.Status is >= 300 and < 400);

async Task<JsonElement> SessionOf(Browser b)
{
    var r = await b.Get(Origin + "/bff/session");
    return r.StatusCode == HttpStatusCode.OK ? JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement : default;
}
static string Frags(JsonElement s) => s.ValueKind == JsonValueKind.Object
    ? string.Join(", ", s.GetProperty("fragments").EnumerateObject().Select(p => $"{p.Name}={p.Value.GetString()}")) : "(401)";
static string? FragState(JsonElement s, string id) => s.ValueKind == JsonValueKind.Object && s.GetProperty("fragments").TryGetProperty(id, out var v) ? v.GetString() : null;

async Task RunMode(bool perFragmentPath)
{
    var tag = perFragmentPath ? "A" : "B";
    Console.WriteLine();
    Console.WriteLine(perFragmentPath
        ? "== A: per-fragment callback paths /signin-oidc/{fragmentId}, exact dispatch, SkipUnrecognizedRequests off"
        : "== B (control): every scheme on /signin-oidc, SkipUnrecognizedRequests on, dispatch by state");

    var registry = new Registry();
    registry.Publish(Shell(Frag("orders"), Frag("billing")));
    var sw = new Switches { PerFragmentPath = perFragmentPath };
    await using var gw = await Gateway.StartAsync(registry, idp.BaseUrl, sw);
    var b = new Browser(gw.BaseUrl, idp.BaseUrl);
    var authBefore = idp.Authorizations.Count;

    // 1. The eager chain.
    var (final, hops) = await Go(b, Origin + "/bff/login?returnUrl=/app");
    var body = await final.Content.ReadAsStringAsync();
    Check(tag + "1", final.StatusCode == HttpStatusCode.OK && body == "shell document" && gw.Sessions.Count == 1,
        $"shell login then two silent fragment hops land on /app with one record: {(int)final.StatusCode}, records={gw.Sessions.Count}");
    Console.WriteLine("     path: " + string.Join(" > ", hops.Select(h => $"{h.Path}[{h.Status}]")));
    var auths = idp.Authorizations.Skip(authBefore).ToList();
    Check(tag + "2", auths.Select(a => a.ClientId).SequenceEqual(["c-shop", "c-orders", "c-billing"]) && auths.All(a => a.Outcome == "ok"),
        "one authorization per client, in registry order: " + string.Join(", ", auths.Select(a => $"{a.ClientId}->{new Uri(a.RedirectUri).AbsolutePath}")));
    var s = await SessionOf(b);
    Check(tag + "3", FragState(s, "orders") == "signed-in" && FragState(s, "billing") == "signed-in", "/bff/session: " + Frags(s));
    var key = gw.Sessions.Keys.Single();
    var items = gw.Sessions.Ticket(key)!.Properties.Items;
    var tokenKeys = items.Keys.Where(k => k.StartsWith(".Token.", StringComparison.Ordinal)).Order().ToList();
    Check(tag + "4", tokenKeys.Contains(".Token.access_token") && tokenKeys.Contains(".Token.access_token:orders") && tokenKeys.Contains(".Token.refresh_token:billing")
        && items[".Token.access_token"] != items[".Token.access_token:orders"], "three token sets in one record under suffixed names: " + string.Join(" ", tokenKeys));
    var grants = idp.Grants.Where(g => g.GrantType == "authorization_code").TakeLast(3).Select(g => g.ClientId).ToList();
    Check(tag + "5", grants.SequenceEqual(["c-shop", "c-orders", "c-billing"]), "each code redeemed by its own client: " + string.Join(", ", grants));
    var r30x = Redirects(hops);
    var steps = gw.Log.ChainSteps.ToArray();
    var perHop = steps.Zip(steps.Skip(1), (x, y) => Stopwatch.GetElapsedTime(x.Ticks, y.Ticks).TotalMilliseconds).ToList();
    Note(tag + "6", $"{r30x} redirects from /bff/login to /app with 2 fragments; per fragment hop on loopback (handler cost, not a latency budget): "
        + string.Join(", ", perHop.Select(m => $"{m:F1} ms")));

    // 2. Cross-dispatch: orders' state presented at billing's callback.
    var preTicket = gw.Sessions.Ticket(key)!.Properties.Items[".Token.access_token:orders"];
    var (_, lazy) = await Go(b, Origin + "/bff/login?fragment=orders&returnUrl=/app", stopBefore: u => u.StartsWith(Origin + "/signin-oidc", StringComparison.Ordinal));
    var callback = new Uri(lazy[^1].Path);
    var target = perFragmentPath ? Origin + "/signin-oidc/billing" + callback.Query : null;
    if (target is not null)
    {
        var cross = await b.Get(target);
        Check(tag + "7", cross.StatusCode == HttpStatusCode.BadRequest && gw.Log.Merges.Last() is ("billing", var o) && o.StartsWith("failed, no state", StringComparison.Ordinal)
            && gw.Sessions.Count == 1 && FragState(await SessionOf(b), "billing") == "signed-in",
            $"orders' state at /signin-oidc/billing: {(int)cross.StatusCode}, billing's handler cannot unprotect it ({gw.Log.Merges.Last().Outcome}); record unchanged");
    }
    // Then the real callback completes the lazy fallback into the same record.
    var (done, _) = await Go(b, callback.ToString());
    Check(tag + "8", done.StatusCode == HttpStatusCode.OK && gw.Sessions.Keys.Single() == key
        && gw.Sessions.Ticket(key)!.Properties.Items[".Token.access_token:orders"] != preTicket
        && gw.Sessions.Ticket(key)!.Properties.Items.ContainsKey(".Token.access_token"),
        "/bff/login?fragment=orders re-acquires orders' set into the same record key; the shell's set stays");

    // 3. Garbage state at a callback.
    var garbage = await b.Get(Origin + (perFragmentPath ? "/signin-oidc/orders" : "/signin-oidc") + "?code=x&state=not-a-state");
    var gbody = await garbage.Content.ReadAsStringAsync();
    if (perFragmentPath)
        Check(tag + "9", garbage.StatusCode == HttpStatusCode.BadRequest && gbody == "login failed", $"unrecognised state at /signin-oidc/orders reaches the failure path: {(int)garbage.StatusCode} {gbody}");
    else
        Check(tag + "9", garbage.StatusCode == HttpStatusCode.NotFound && gbody == "path.reserved",
            $"unrecognised state at the shared /signin-oidc: every handler skips it, it falls through to the asset route: {(int)garbage.StatusCode} {gbody}");

    // 4. Logout.
    var revBefore = idp.Revocations.Count;
    var logoutsBefore = idp.Logouts.Count;
    var lo = await b.Send(HttpMethod.Post, Origin + "/bff/logout");
    var revs = idp.Revocations.Skip(revBefore).ToList();
    var outs = idp.Logouts.Skip(logoutsBefore).ToList();
    Check(tag + "10", revs.Select(r => r.ClientId).SequenceEqual(["c-shop", "c-orders", "c-billing"]) && revs.All(r => r.Known)
        && gw.Sessions.Count == 0 && (int)lo.StatusCode == 302 && new Uri(lo.Headers.Location!.ToString()).AbsolutePath == "/endsession",
        $"logout: {revs.Count} revocations under {string.Join(", ", revs.Select(r => r.ClientId))}, each known to its client; record removed; one redirect to the provider's end-session");
    var (_, outHops) = await Go(b, lo.Headers.Location!.ToString());
    outs = idp.Logouts.Skip(logoutsBefore).ToList();
    Check(tag + "11", outs.Count == 1 && outs[0].ClientId == "c-shop" && outs[0].Outcome == "ok",
        $"one provider logout, by the shell's client: {string.Join(", ", outs.Select(x => $"{x.ClientId} {x.Outcome}"))}; then " + string.Join(" > ", outHops.Select(h => $"{h.Path}[{h.Status}]")));

    if (!perFragmentPath) return;

    // 5. Failing hops. (a) The gateway holds a wrong secret for billing: the token endpoint refuses it.
    registry.Publish(Shell(Frag("orders"), Frag("billing", "wrong")));
    var evicted = gw.Log.Evicted.ToArray();
    Check("A12", evicted.SequenceEqual(["oidc:shop:billing"]), "changing billing's secret evicts only its scheme: " + string.Join(", ", evicted));
    (final, hops) = await Go(b, Origin + "/bff/login?returnUrl=/app");
    s = await SessionOf(b);
    Check("A13", final.StatusCode == HttpStatusCode.OK && FragState(s, "orders") == "signed-in" && FragState(s, "billing") == "unavailable" && gw.Sessions.Count == 1,
        $"billing's code exchange refused ({gw.Log.Merges.Last().Outcome}): chain continues, shell lands on /app, {Frags(s)}");

    // (b) The provider refuses the silent hop (Cognito managed login after its hour, prompt=none): error=login_required.
    while (gw.Log.Evicted.TryDequeue(out _)) { }
    registry.Publish(Shell(Frag("orders"), Frag("billing")));
    var (_, toOrders) = await Go(b, Origin + "/bff/login?returnUrl=/app", stopBefore: u => u.Contains("/authorize?", StringComparison.Ordinal) && u.Contains("client_id=c-orders", StringComparison.Ordinal));
    var authorize = new Uri(toOrders[^1].Path);
    var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorize.Query);
    Check("A14", q["prompt"] == "none", $"a chain hop sends prompt=none: prompt={q["prompt"]}");
    var (afterRefusal, _) = await Go(b, Origin + "/signin-oidc/orders?error=login_required&state=" + Uri.EscapeDataString(q["state"]!));
    s = await SessionOf(b);
    Check("A15", afterRefusal.StatusCode == HttpStatusCode.OK && FragState(s, "orders") == "unavailable" && FragState(s, "billing") == "signed-in",
        $"error=login_required with readable state is a failed hop, not a failed login ({gw.Log.Merges.Reverse().Skip(1).First().Outcome}): {Frags(s)}");

    // (c) A fragment whose client lacks the redirect URI: the provider answers 400 and never comes back.
    registry.Publish(Shell(Frag("orders"), Frag("stall"), Frag("billing")));
    (final, hops) = await Go(b, Origin + "/bff/login?returnUrl=/app");
    s = await SessionOf(b);
    Check("A16", final.StatusCode == HttpStatusCode.BadRequest && hops[^1].Path == "idp/authorize" && FragState(s, "stall") == "missing" && FragState(s, "billing") == "missing"
        && s.GetProperty("sub").GetString() == "alice",
        $"unregistered redirect URI: the chain stalls at the provider's 400 and billing behind it is never tried; the shell is signed in: {Frags(s)}");

    // 6. Onboarding after login, then the fallback; and a subject mismatch.
    while (gw.Log.Evicted.TryDequeue(out _)) { }
    registry.Publish(Shell(Frag("orders"), Frag("billing"), Frag("reviews")));
    (final, _) = await Go(b, Origin + "/bff/login?returnUrl=/app");
    while (gw.Log.Evicted.TryDequeue(out _)) { }
    registry.Publish(Shell(Frag("orders"), Frag("billing"), Frag("reviews"), Frag("f0")));
    evicted = gw.Log.Evicted.ToArray();
    s = await SessionOf(b);
    Check("A17", FragState(s, "f0") == "missing" && evicted.SequenceEqual(["oidc:shop:f0"]),
        $"a fragment onboarded after login is missing, and onboarding evicts only its name ({string.Join(", ", evicted)}): {Frags(s)}");
    (final, _) = await Go(b, Origin + "/bff/login?fragment=f0&returnUrl=/app");
    s = await SessionOf(b);
    Check("A18", final.StatusCode == HttpStatusCode.OK && FragState(s, "f0") == "signed-in" && gw.Sessions.Count == 1, "the fallback signs f0 in: " + Frags(s));
    idp.DefaultSubject = "bob";   // Go() does not add login_hint; the provider's default subject is who "types in"
    (final, _) = await Go(b, Origin + "/bff/login?fragment=reviews&returnUrl=/app");
    idp.DefaultSubject = "alice";
    s = await SessionOf(b);
    Check("A19", gw.Log.Merges.Last() == ("reviews", "refused: subject mismatch") && s.GetProperty("sub").GetString() == "alice" && FragState(s, "reviews") == "unavailable",
        "a fragment set for another subject is refused, not merged: " + Frags(s));
    await b.Send(HttpMethod.Post, Origin + "/bff/logout");

    // 7. The redirect count against the Fetch standard's limit of 20.
    Console.WriteLine();
    foreach (var n in new[] { 0, 1, 2, 4, 5, 6, 8 })
    {
        registry.Publish(Shell([.. Enumerable.Range(0, n).Select(k => Frag($"f{k}"))]));
        (final, hops) = await Go(b, Origin + "/bff/login?returnUrl=/app");
        Note("A20", $"N={n}: {Redirects(hops)} redirects from /bff/login to /app ({(Redirects(hops) <= 20 ? "within" : "OVER")} 20), final {(int)final.StatusCode}");
        await b.Send(HttpMethod.Post, Origin + "/bff/logout");
    }
}

await RunMode(perFragmentPath: true);
await RunMode(perFragmentPath: false);

Console.WriteLine();
Console.WriteLine(failures == 0 ? "all checks held" : $"{failures} checks missed");
return failures == 0 ? 0 : 1;
