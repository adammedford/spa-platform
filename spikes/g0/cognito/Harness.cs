using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Duende.IdentityModel.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using TestIdp;

namespace Spike;

public sealed class RunOptions
{
    public string Mode = "";                 // "dry-run" or "cognito"
    public string Driver = "playwright";     // "playwright", "manual", or "http" (dry run only)
    public string? OutputsPath;
    public string? CertsDir;
    public string User = "alice";
    public string SecondUser = "bob";
    public bool Headed;
    public bool SkipNaturalExpiry;
    public bool LeakTest;
    public bool DryRun => Mode == "dry-run";
}

/// <summary>Gate G0, items 2 and 3, against Cognito (task 3.0), or the same flows against fixtures/TestIdp as a dry run.</summary>
public sealed class Harness
{
    public const int DryRunPort = 5300, CognitoPort = 8443;
    public const string Usage = """
        usage:
          dotnet run -c Release -- --dry-run  [--driver playwright|http] [--leak-test]
          dotnet run -c Release -- --cognito  [--outputs PATH] [--certs-dir DIR] [--manual] [--headed]
                                              [--user alice] [--second-user bob] [--skip-natural-expiry]
          dotnet run -c Release -- --install-browser
        --dry-run runs every flow against the test identity provider in fixtures/TestIdp. It is never G0 evidence.
        --cognito runs against the user pool and OAuth clients of infra/envs/poc/idp and infra/envs/poc/apps (task 3.0b).
        """;

    private readonly RunOptions _o;
    private readonly Output _out;
    private readonly Redactor _r;
    private readonly string _root;
    private Registry _registry = default!;
    private CognitoGateway _gw = default!;
    private ILoginDriver _driver = default!;
    private IdpProfile _profile = default!;
    private ISecrets _secrets = default!;
    private IClientDescriber _describer = default!;
    private Raw _raw = default!;
    private TestIdentityProvider? _testIdp;
    private string _authority = "";
    private string _secondUser = "";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<(string Context, string AppId), string> _keys = new();
    private readonly Dictionary<(string Context, string AppId), LoginResult> _logins = new();
    private readonly Dictionary<string, LogoutResult> _logouts = new();
    private readonly List<(string AppId, string Token)> _held = [];

    private AppEntry App1 => _registry.Current.ByAppId["app1"];
    private AppEntry App2 => _registry.Current.ByAppId["app2"];

    private Harness(RunOptions o, Output output, string root) => (_o, _out, _r, _root) = (o, output, output.Redactor, root);

    public static async Task<int> MainAsync(string[] args)
    {
        var o = new RunOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--dry-run": o.Mode = o.Mode.Length == 0 ? "dry-run" : "both"; break;
                case "--cognito": o.Mode = o.Mode.Length == 0 ? "cognito" : "both"; break;
                case "--install-browser": return Microsoft.Playwright.Program.Main(["install", "chromium"]);
                case "--driver": o.Driver = Next(); break;
                case "--manual": o.Driver = "manual"; break;
                case "--outputs": o.OutputsPath = Path.GetFullPath(Next()); break;
                case "--certs-dir": o.CertsDir = Path.GetFullPath(Next()); break;
                case "--user": o.User = Next(); break;
                case "--second-user": o.SecondUser = Next(); break;
                case "--headed": o.Headed = true; break;
                case "--skip-natural-expiry": o.SkipNaturalExpiry = true; break;
                case "--leak-test": o.LeakTest = true; break;
                default: Console.Error.WriteLine($"unknown argument {args[i]}\n{Usage}"); return 2;
            }
        }
        if (o.Mode is not ("dry-run" or "cognito") || o.Driver is not ("playwright" or "manual" or "http")
            || (o.Driver == "http" && !o.DryRun) || (o.Driver == "manual" && o.DryRun) || (o.LeakTest && !o.DryRun))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var root = Config.RepoRoot();
        var output = new Output(new Redactor());
        var harness = new Harness(o, output, root);
        try { return await harness.RunAsync(); }
        finally { await harness.StopAsync(); }
    }

    private async Task<int> RunAsync()
    {
        _out.Line(_o.DryRun
            ? "MODE    DRY RUN against fixtures/TestIdp. This proves the harness, not Cognito. It is never G0 evidence."
            : "MODE    Cognito: Gate G0 items 2 and 3 (task 3.0b)");
        _out.Line($"run     {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}Z, {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        foreach (var a in new[] { typeof(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler).Assembly, typeof(Duende.AccessTokenManagement.OpenIdConnect.UserTokenRequestParameters).Assembly,
                     typeof(Duende.IdentityModel.OidcConstants).Assembly, typeof(Microsoft.Playwright.IPage).Assembly })
            _out.Line($"        {a.GetName().Name} {Version(a)}");
        _out.Line($"        Chromium for Playwright, revision {PlaywrightDriver.Revision}");
        _out.Line($"driver  {_o.Driver}");
        _out.Line();

        if (_o.DryRun) await SetUpDryRunAsync();
        else if (await SetUpCognitoAsync() is { } refused) return refused;

        _raw = new Raw(_http, (await Discovery()).TokenEndpoint!, (await Discovery()).RevocationEndpoint ?? _profile.RevocationEndpoint, _r);
        _r.Harvest = HeldTokens;

        await Section("The harness: redaction self-check (Q4)", RedactionSelfCheck);
        await Section("Item 2: discovery", ItemTwoDiscovery);
        await Section("Item 2: login, callback, refresh and logout, as tasks 3.8 and 3.9 configure them (browser A)", ItemTwoGatePath);
        if (!_o.LeakTest)
        {
            await Section("Item 2 questions: raw calls through Duende.IdentityModel (browser R)", ItemTwoQuestions);
            await Section("Item 3: the callback URL with a port, and managed login for clients Terraform made", ItemThree);
            await Cleanup();
        }
        return await Finish();
    }

    // ------------------------------------------------------------------------------------------------ set-up

    private async Task SetUpDryRunAsync()
    {
        var idp = _testIdp = new TestIdentityProvider();
        idp.EndSessionStyle = EndSessionStyle.Cognito;           // discovery lists no end_session_endpoint, as Cognito's does not
        idp.Faults.AdvertiseRevocation = false;                  // so the profile's revocation route (Q16) runs here too
        idp.Faults.AccessTokenLifetime = TimeSpan.FromSeconds(65);   // due five seconds after issue, so Q20's natural expiry is short
        await idp.StartAsync();
        _authority = idp.BaseUrl;
        _profile = new IdpProfile("cognito (dry run: TestIdp's Cognito-style /logout and /revoke)", idp.BaseUrl, "/revoke");

        var problems = new List<string>();
        var apps = Config.LoadApps(_root, problems);
        if (problems.Count > 0) throw new InvalidOperationException(string.Join("; ", problems));
        var entries = new List<AppEntry>();
        foreach (var a in apps)
        {
            // The fixture hosts, on the dry run's one fixed port. Browser A resolves *.spa-poc.test to loopback by itself.
            var host = a.Host.Split(':')[0] + ":" + DryRunPort;
            var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            _r.Add(secret);
            idp.AddClient("dry-run-" + a.AppId, secret, $"https://{host}/signin-oidc", $"https://{host}/");
            entries.Add(new AppEntry(a.AppId, host, "dry-run-" + a.AppId, secret, string.Join(' ', a.Scopes), a.Resource));
        }
        _registry = new Registry();
        _registry.Publish(entries);
        _secrets = new DryRunSecrets(entries, _r);
        _describer = new DryRunDescriber(idp);
        _secondUser = _o.SecondUser;

        _out.Line($"setup   the test identity provider on {idp.BaseUrl}, Cognito-style logout, no revocation_endpoint in discovery, 65 s access tokens");
        _out.Line($"setup   OAuth clients for app1 and app2 from fixtures/apps, hosts moved to port {DryRunPort}; outputs document synthesised, not read (its issuer is http, so it would not be contract-valid)");
        HttpClient.DefaultProxy = new WebProxy();                // everything in the dry run is loopback
        await StartGatewayAndDriver(SelfSigned(), DryRunPort, idp.BaseUrl);
    }

    private async Task<int?> SetUpCognitoAsync()
    {
        // Where certs.sh writes: SPA_CERTS_DIR, or .certs at the repository root.
        var certsDir = _o.CertsDir
            ?? (Environment.GetEnvironmentVariable("SPA_CERTS_DIR") is { Length: > 0 } env ? Path.GetFullPath(env) : Path.Combine(_root, ".certs"));
        var (items, outputs, apps) = await Prerequisites.CheckAsync(_root, _o.OutputsPath, certsDir, _o.User, _o.Driver == "manual", _r);
        _out.Line("Prerequisites (Q9)");
        foreach (var item in items) _out.Line($"{(item.Present ? "present" : "MISSING"),-8}{item.Name}\n               [{item.Detail}]");
        if (items.Any(i => !i.Present) || outputs is null)
        {
            _out.Line();
            _out.Line($"REFUSED {items.Count(i => !i.Present)} prerequisite(s) missing. Nothing was started, nothing was bound, and no output file was written.");
            return 3;
        }
        _out.Line();

        _authority = outputs.Issuer.TrimEnd('/');
        _profile = new IdpProfile("cognito", outputs.BrowserOrigin, "/oauth2/revoke");
        _secrets = new ParameterStoreSecrets(outputs, _r);
        _describer = new CognitoDescriber(outputs, _r);
        _secondUser = outputs.PasswordParameters.ContainsKey(_o.SecondUser) ? _o.SecondUser : _o.User;

        var entries = new List<AppEntry>();
        foreach (var a in apps)
        {
            var client = outputs.Clients[a.AppId];
            entries.Add(new AppEntry(a.AppId, a.Host, client.ClientId, await _secrets.ClientSecretAsync(a.AppId), string.Join(' ', a.Scopes), a.Resource));
        }
        _registry = new Registry();
        _registry.Publish(entries);
        _out.Line($"setup   issuer {_authority}, managed login at {outputs.BrowserOrigin}, outputs from {Config.Display(_root, outputs.Source)}");

        var chain = Path.Combine(certsDir, "spa-platform-poc-wildcard.fullchain.pem");
        using var pem = X509Certificate2.CreateFromPemFile(chain, Path.Combine(certsDir, "spa-platform-poc-wildcard.key"));
        await StartGatewayAndDriver(X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null), CognitoPort, outputs.BrowserOrigin);
        return null;
    }

    private async Task StartGatewayAndDriver(X509Certificate2 certificate, int port, string idpOrigin)
    {
        _gw = await CognitoGateway.StartAsync(_registry, _authority, _profile, certificate, port, _r);
        _driver = _o.Driver switch
        {
            "playwright" => await PlaywrightDriver.StartAsync(_o.DryRun, _o.Headed, _gw.BackDoor, idpOrigin, _r),
            "manual" => new ManualDriver(_gw.Log, _gw.BackDoor, idpOrigin, _out),
            _ => new HttpDriver(_gw.BackDoor, idpOrigin),
        };
        _out.Line($"setup   gateway on https://127.0.0.1:{port} for the browser; the harness's own calls reach it on loopback as the load balancer would");
        _out.Line();
    }

    /// <summary>A certificate for *.spa-poc.test made in memory for the dry run, through the framework's own API.</summary>
    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=*.spa-poc.test G0 dry run", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("*.spa-poc.test");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private async Task Section(string title, Func<Task> body)
    {
        _out.Line(title);
        try { await body(); }
        catch (Exception ex)
        {
            _out.Check("C-!", $"\"{title}\" stopped on an exception, so its remaining checks did not run", false, AwsCredentials.OneLine($"{ex.GetType().Name}: {ex.Message}"));
        }
        _out.Line();
    }

    private OpenIdConnectDiscovery? _discovery;
    private sealed record OpenIdConnectDiscovery(string? Issuer, string? AuthorizationEndpoint, string? TokenEndpoint, string? EndSessionEndpoint, string? RevocationEndpoint, string[] AuthMethods);

    private async Task<OpenIdConnectDiscovery> Discovery()
    {
        if (_discovery is not null) return _discovery;
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(_authority.TrimEnd('/') + "/.well-known/openid-configuration"));
        var r = doc.RootElement;
        string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var methods = r.TryGetProperty("token_endpoint_auth_methods_supported", out var m) && m.ValueKind == JsonValueKind.Array ? m.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
        return _discovery = new OpenIdConnectDiscovery(S("issuer"), S("authorization_endpoint"), S("token_endpoint"), S("end_session_endpoint"), S("revocation_endpoint"), methods);
    }

    /// <summary>Every token the gateway holds and every one the harness holds: the redactor reads this before each line.</summary>
    private IEnumerable<string> HeldTokens()
    {
        foreach (var record in _gw?.Sessions.All ?? [])
        {
            var p = TicketSerializer.Default.Deserialize(record.Ticket)!.Properties;
            foreach (var name in new[] { "access_token", "refresh_token", "id_token" })
                if (p.GetTokenValue(name) is { } v) yield return v;
        }
        foreach (var (_, t) in _held) yield return t;
    }

    private void Hold(string appId, string? token) { if (!string.IsNullOrEmpty(token)) { _held.Add((appId, token)); _r.Add(token); } }

    private async Task<LoginResult> Login(string context, AppEntry app, string user, string path = "/")
    {
        var before = _gw.Sessions.KeysFor(app.AppId).ToHashSet();
        var result = await _driver.LoginAsync(context, app, path, user, () => _secrets.PasswordAsync(user));
        var added = _gw.Sessions.KeysFor(app.AppId).Where(k => !before.Contains(k)).ToList();
        if (added.Count == 1) _keys[(context, app.AppId)] = added[0];
        foreach (var k in added) { _r.Add(k); foreach (var t in HeldTokens()) _r.Add(t); }
        _logins[(context, app.AppId)] = result;
        return result;
    }

    private AuthenticationProperties? Props(string context, AppEntry app)
        => _keys.TryGetValue((context, app.AppId), out var key) && _gw.Sessions.TryGet(key) is { } record
            ? TicketSerializer.Default.Deserialize(record.Ticket)!.Properties : null;

    private async Task<(HttpStatusCode Status, string? Code, string? Token)> Token(string context, AppEntry app, bool force)
    {
        var r = await _driver.BrowserFor(context).Get($"https://{app.Host}/bff/token{(force ? "?force=1" : "")}");
        var text = await r.Content.ReadAsStringAsync();
        string? code = null, token = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("code", out var c)) code = c.GetString();
            if (doc.RootElement.TryGetProperty("accessToken", out var t)) token = t.GetString();
        }
        catch (JsonException) { }
        _r.Add(token);
        return (r.StatusCode, code, token);
    }

    private async Task<HttpStatusCode> SessionStatus(string context, AppEntry app)
        => (await _driver.BrowserFor(context).Get($"https://{app.Host}/bff/session")).StatusCode;

    private static string? Q(Dictionary<string, Microsoft.Extensions.Primitives.StringValues>? query, string name)
        => query is not null && query.TryGetValue(name, out var v) ? v.ToString() : null;

    private static string Len(string? token) => token is null ? "(none)" : $"{token.Length:N0} chars";
    /// <summary>Whether a refresh rotated: it was granted and returned a refresh token other than the one sent.</summary>
    private static bool Rotated(RawResult r, string sent) => r.Ok && r.RefreshToken is { } t && t != sent;
    private static string Version(Assembly a)
    {
        var informational = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
        var file = a.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        return informational is { Length: > 0 } and not "1.0.0" ? informational : file ?? a.GetName().Version?.ToString() ?? "?";
    }
    private static string Hops(LoginResult r) => r.Hops.Count == 0 ? "(no hops seen)" : string.Join(" > ", r.Hops);
    private string LoginDetail(LoginResult r)
        => r.Completed
            ? $"asked for a password: {Yn(r.AskedForPassword)}; managed login seen: {Yn(r.SawManagedLogin)}; {Hops(r)}"
            : $"did not complete: {r.Error}";
    private static string Yn(bool? b) => b is null ? "not observed" : b.Value ? "yes" : "no";

    // ------------------------------------------------------------------------------------------------ the harness

    /// <summary>Shows the redactor working before anything it guards is printed. The probe lines are tested, never printed.</summary>
    private Task RedactionSelfCheck()
    {
        var secret = _registry.Current.ByAppId["app1"].ClientSecret;
        var jwt = "eyJhbGciOiJub25lIn0." + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24)) + ".";
        _out.Check("H-1", "a line holding app1's client secret, which the harness was given, is withheld", _r.Leak("probe " + secret) is not null);
        _out.Check("H-2", "a line holding a JWT the harness was never told about is withheld", _r.Leak("probe " + jwt) is not null);
        _out.Check("H-3", "a line holding a callback URL's code or state is withheld", _r.Leak("probe https://app1.spa-poc.test:8443/signin-oidc?code=" + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(12))) is not null);
        _out.Check("H-4", "a line holding only lengths, hashes and URLs without a query is printed", _r.Leak($"probe {Redactor.Hash(secret)} {secret.Length} chars {Redactor.Strip("https://example.test/a?state=" + secret)}") is null);
        _out.Check("H-5", "a line holding a token in a JSON member, or a code parameter at the start of a line, is withheld",
            _r.Leak("probe {\"refresh_token\": \"" + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(12)) + "\"}") is not null
            && _r.Leak("code=" + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(12))) is not null);
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------------------------------------ item 2

    private async Task ItemTwoDiscovery()
    {
        var d = await Discovery();
        var authorizeOrigin = d.AuthorizationEndpoint is { } a ? new Uri(a).GetLeftPart(UriPartial.Authority) : "(none)";
        _out.Check("C-2.1", "the issuer's discovery document loads, names the issuer the outputs give, and puts authorization at the managed-login origin",
            d.Issuer?.TrimEnd('/') == _authority.TrimEnd('/') && authorizeOrigin.Equals(_profile.BrowserOrigin, StringComparison.OrdinalIgnoreCase) && d.TokenEndpoint is not null,
            $"issuer {d.Issuer}; authorize at {authorizeOrigin}; token endpoint {Redactor.Strip(d.TokenEndpoint)}; client authentication {string.Join(", ", d.AuthMethods)}");

        _out.Finding("C-2.2", "discovery lists no end_session_endpoint (item 2: what discovery advertises for logout)",
            d.EndSessionEndpoint is null, d.EndSessionEndpoint is null ? "no end_session_endpoint" : $"end_session_endpoint {Redactor.Strip(d.EndSessionEndpoint)}",
            "the cognito profile's logout, as ADR-0011 has it: {BrowserOrigin}/logout with client_id and logout_uri, landing on / (R-C7). This harness logs out that way",
            "the standard profile's RP-initiated logout would also work; ADR-0011's cognito profile stays");

        _out.Finding("C-2.3", "discovery lists no revocation_endpoint (Q16, T-B14)",
            d.RevocationEndpoint is null, d.RevocationEndpoint is null ? "no revocation_endpoint" : $"revocation_endpoint {Redactor.Strip(d.RevocationEndpoint)}",
            "Q16: I-6 adds an IIdpProfile member, and the cognito profile supplies {BrowserOrigin}/oauth2/revoke; task 3.9 deletes the session whether or not revocation worked",
            "the library's revoke call finds the endpoint in discovery; no IIdpProfile member is needed for it");
    }

    private async Task ItemTwoGatePath()
    {
        // Alice signs in to app1, then to app2, in one browser.
        var l1 = await Login("A", App1, _o.User);
        _out.Check("C-2.4", "app1: login and callback complete through managed login, and the gateway holds app1's session",
            l1.Completed && _keys.ContainsKey(("A", "app1")) && await SessionStatus("A", App1) == HttpStatusCode.OK, LoginDetail(l1));

        if (_o.LeakTest)
        {
            // Plants a token the way a careless print would, to show the final check refuses to write the file.
            _out.Unchecked("leak test: " + Props("A", App1)?.GetTokenValue("refresh_token"));
            return;
        }

        var l2 = await Login("A", App2, _o.User);
        _out.Check("C-2.5", "app2: login and callback complete in the same browser, as a second OAuth client with its own session",
            l2.Completed && _keys.ContainsKey(("A", "app2")) && await SessionStatus("A", App2) == HttpStatusCode.OK
            && _keys[("A", "app1")] != _keys[("A", "app2")], LoginDetail(l2));

        if (l2.AskedForPassword is null) _out.Skip("C-2.6", "app2's login asked for no password (Q19)", "a person drove the login; the harness did not see the page");
        else _out.Finding("C-2.6", "app2's login asked for no password: the provider's session from app1's login made it silent (Q19)",
            l2.AskedForPassword == false, $"asked for a password: {Yn(l2.AskedForPassword)}",
            "none for the gateway: still two OAuth clients, two scheme pairs and two session records. C-2.12 and C-2.15 check that logout stays per app",
            "each app's login asks on its own");

        // Resource binding at login (Q15) and the claims of app2's token, which names no resource.
        var at1 = Props("A", App1)?.GetTokenValue("access_token");
        var at2 = Props("A", App2)?.GetTokenValue("access_token");
        var c1 = Claims.Of(at1);
        var c2 = Claims.Of(at2);
        var resource = App1.Resource ?? "";
        _out.Finding("C-2.7", "resource binding does not put app1's resource into the access token's aud (Q15)",
            !c1.Aud.Split(' ').Contains(resource),
            $"app1 sent resource at authorize: {Yn(_gw.Log.SentResource.TryGetValue("app1", out var sent) ? sent : null)}; app1 {Claims.Describe(c1)}, client_id {(c1.ClientId == App1.ClientId ? "is app1's client" : "is not app1's client")}; app2 {Claims.Describe(c2)}, client_id {(c2.ClientId == App2.ClientId ? "is app2's client" : "is not app2's client")}",
            "Q15(a): a finding, not a G0 failure. APIs accept Cognito tokens by client_id and scope, as the mock API's allowlist does; resource binding moves to Phase 2 for Cognito; I-6 records it and the glossary's \"Resource binding\" entry is revised",
            "the plan's assumption holds: aud carries the resource");

        // Refresh through the token library, configured as task 3.8 says (Q5, Q20): forced, then once by natural expiry.
        foreach (var (id, app) in new[] { ("C-2.8", App1), ("C-2.10", App2) })
        {
            var before = Props("A", app);
            string? oldAt = before?.GetTokenValue("access_token"), oldRt = before?.GetTokenValue("refresh_token");
            var (status, code, token) = await Token("A", app, force: true);
            var after = Props("A", app);
            string? newAt = after?.GetTokenValue("access_token"), newRt = after?.GetTokenValue("refresh_token");
            _out.Check(id, $"{app.AppId}: a refresh through the token library (sentinel scheme, both scheme names, PostBody) is granted, rotates the refresh token, and the session keeps the new one",
                status == HttpStatusCode.OK && token is not null && token != oldAt && token == newAt && newRt is not null && newRt != oldRt,
                $"{(int)status} {code}; access token {Redactor.Hash(oldAt)} -> {Redactor.Hash(newAt)}; refresh token {Redactor.Hash(oldRt)} ({Len(oldRt)}) -> {Redactor.Hash(newRt)} ({Len(newRt)})");

            if (app == App1)
            {
                var cAfter = Claims.Of(newAt);
                _out.Finding("C-2.9", "aud does not survive a refresh that sends no resource (Q14)",
                    cAfter.Aud != c1.Aud, $"aud at login: {c1.Aud}; after the library's refresh, which sends no resource: {cAfter.Aud}",
                    "Q14: task 3.8 sends resource on every refresh and accepts T-B12's extra grant after each login",
                    "task 3.8 leaves resource off the refresh (T-B12)");
            }
        }

        if (_o.SkipNaturalExpiry) _out.Skip("C-2.11", "app1: a call with a token due for refresh refreshes without being forced (Q20)", "--skip-natural-expiry");
        else
        {
            var props = Props("A", App1);
            var oldAt = props?.GetTokenValue("access_token");
            var expires = DateTimeOffset.TryParse(props?.GetTokenValue("expires_at"), out var e) ? e : DateTimeOffset.UtcNow;
            var wait = expires - TimeSpan.FromSeconds(60) - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
            if (wait > TimeSpan.Zero)
            {
                _out.Line($"        waiting {wait.TotalSeconds:N0} s for app1's access token to come within a minute of expiry (Q20)");
                await Task.Delay(wait);
            }
            var (status, code, token) = await Token("A", App1, force: false);
            _out.Check("C-2.11", "app1: a call with a token due for refresh refreshes without being forced, on the provider's own token lifetime (Q20)",
                status == HttpStatusCode.OK && token is not null && token != oldAt,
                $"{(int)status} {code}; the token expired at {expires:HH:mm:ss}Z; access token {Redactor.Hash(oldAt)} -> {Redactor.Hash(token)}");
        }

        // Alice logs out of app1. The provider's logout is global; the gateway's must stay per app (Q19).
        var app1Key = _keys[("A", "app1")];
        var lo = await _driver.LogoutAsync("A", App1);
        _logouts["app1"] = lo;
        var entry = _gw.Log.Logouts.LastOrDefault(x => x.AppId == "app1");
        if (entry?.RevokedToken is { } revoked) Hold("app1", revoked);
        var query = lo.ProviderLogout is { } loc && Uri.TryCreate(loc, UriKind.Absolute, out var u) ? QueryHelpers.ParseQuery(u.Query) : null;
        _out.Check("C-2.12", "app1: logout revokes, removes app1's session record, and sends the browser to the provider's logout with app1's client, landing on / (R-C7)",
            lo.Status == 302 && entry is { SessionRemoved: true } && entry.RevokeRoute != "none" && _gw.Sessions.TryGet(app1Key) is null
            && lo.ProviderLogout!.StartsWith(_profile.BrowserOrigin + "/logout?", StringComparison.OrdinalIgnoreCase)
            && Q(query, "client_id") == App1.ClientId && Q(query, "logout_uri") == $"https://{App1.Host}/" && lo.LandedOnRoot,
            $"{lo.Status}; revocation through the {entry?.RevokeRoute ?? "(none)"} route: {entry?.RevokeOutcome}; session removed: {Yn(entry?.SessionRemoved)}; provider logout {Redactor.Strip(lo.ProviderLogout)} with app1's client: {Yn(Q(query, "client_id") == App1.ClientId)}; then {string.Join(" > ", lo.Hops)}");

        if (entry?.RevokedToken is { } rt)
        {
            var again = await _raw.RefreshAsync(App1, rt, ClientCredentialStyle.PostBody);
            _out.Finding("C-2.13", "the refresh token logout revoked still refreshes",
                again.Ok, $"a refresh with it afterwards: {again.Outcome}",
                "task 3.9 records the revocation's outcome. The token lives until it expires, and only the server ever held it; the session record that held it is gone",
                "logout's revocation took effect at the provider");
        }
        else _out.Skip("C-2.13", "the refresh token logout revoked still refreshes", "the logout revoked nothing");

        if (lo.NextLoginAskedForPassword is null) _out.Skip("C-2.14", "the next login to app1 asks for a password (Q19)", "a person drove the login; the harness did not see the page");
        else _out.Finding("C-2.14", "after app1's logout the next login to app1 asks for a password: the provider's logout ended its session for every client in that browser (Q19)",
            lo.NextLoginAskedForPassword == true, $"the logout landed on /, which challenged at once; asked for a password: {Yn(lo.NextLoginAskedForPassword)}",
            "none for the gateway: its logout is per app (C-2.15), the provider's is global. I-6 records it under the glossary's logical boundary",
            "the provider's session survived app1's logout");

        var s2 = await SessionStatus("A", App2);
        var oldAt2 = Props("A", App2)?.GetTokenValue("access_token");
        var (st2, code2, tok2) = await Token("A", App2, force: true);
        _out.Check("C-2.15", "app2's session still answers /bff/session after app1's logout, and app2's refresh still works (Q19)",
            s2 == HttpStatusCode.OK && st2 == HttpStatusCode.OK && tok2 is not null && tok2 != oldAt2,
            $"/bff/session {(int)s2}; refresh {(int)st2} {code2}, access token {Redactor.Hash(oldAt2)} -> {Redactor.Hash(tok2)}");

        var app2Key = _keys[("A", "app2")];
        var lo2 = await _driver.LogoutAsync("A", App2);
        _logouts["app2"] = lo2;
        var entry2 = _gw.Log.Logouts.LastOrDefault(x => x.AppId == "app2");
        if (entry2?.RevokedToken is { } revoked2) Hold("app2", revoked2);
        var query2 = lo2.ProviderLogout is { } loc2 && Uri.TryCreate(loc2, UriKind.Absolute, out var u2) ? QueryHelpers.ParseQuery(u2.Query) : null;
        _out.Check("C-2.16", "app2: logout revokes, removes app2's session record, and sends the browser to the provider's logout with app2's client, landing on /",
            lo2.Status == 302 && entry2 is { SessionRemoved: true } && entry2.RevokeRoute != "none" && _gw.Sessions.TryGet(app2Key) is null
            && Q(query2, "client_id") == App2.ClientId && Q(query2, "logout_uri") == $"https://{App2.Host}/" && lo2.LandedOnRoot,
            $"{lo2.Status}; revocation through the {entry2?.RevokeRoute ?? "(none)"} route: {entry2?.RevokeOutcome}; session removed: {Yn(entry2?.SessionRemoved)}; then {string.Join(" > ", lo2.Hops)}");
    }

    // ------------------------------------------------------------------------------------------------ item 2 questions

    /// <summary>A new refresh-token family: a login in browser R, whose refresh token the harness then holds for raw calls.</summary>
    private async Task<(string? Rt, string? At, string? IdToken, string? Error)> NewFamily(AppEntry app)
    {
        var l = await Login("R", app, _o.User, "/bff/login");
        var p = Props("R", app);
        if (!l.Completed || p is null) return (null, null, null, l.Error ?? "no session record");
        var rt = p.GetTokenValue("refresh_token");
        Hold(app.AppId, rt);
        return (rt, p.GetTokenValue("access_token"), p.GetTokenValue("id_token"), rt is null ? "the login returned no refresh token" : null);
    }

    private async Task ItemTwoQuestions()
    {
        const ClientCredentialStyle Post = ClientCredentialStyle.PostBody;
        RawResult? q12Revoke = null;

        // Q11 (i): replay the rotated-out token inside the 10-second window, then check the successor.
        {
            var (rt1, _, _, error) = await NewFamily(App1);
            if (rt1 is null) throw new InvalidOperationException("no refresh token for Q11 (i): " + error);
            var rot = await _raw.RefreshAsync(App1, rt1, Post);
            var rotated = Stopwatch.StartNew();
            Hold("app1", rot.RefreshToken);
            await Task.Delay(TimeSpan.FromSeconds(2) - rotated.Elapsed is var d && d > TimeSpan.Zero ? d : TimeSpan.Zero);
            var at = rotated.Elapsed;
            var replay = await _raw.RefreshAsync(App1, rt1, Post);
            Hold("app1", replay.RefreshToken);
            var returned = !replay.Ok ? "an error" : replay.RefreshToken is null ? "new tokens and no refresh token" : replay.RefreshToken == rot.RefreshToken ? "new tokens and the same successor" : "new tokens and a different refresh token";
            var successor = rot.RefreshToken is null ? null : await _raw.RefreshAsync(App1, rot.RefreshToken, Post);
            Hold("app1", successor?.RefreshToken);
            if (!Rotated(rot, rt1)) _out.Check("C-2.17", "the rotation Q11 (i) needs: a refresh that returns a new refresh token", false, $"rotation {rot.Outcome}; so the window was not measured");
            else _out.Finding("C-2.17", "Cognito's grace window is insufficient, outcome (i): a replay inside the window is refused, or it leaves the successor unusable (Q11)",
                !replay.Ok || successor is not { Ok: true },
                $"rotation {rot.Outcome}, a new refresh token: {Yn(rot.RefreshToken is not null && rot.RefreshToken != rt1)}; the replay at {at.TotalSeconds:N1} s: {replay.Outcome}, returning {returned}; the successor afterwards: {successor?.Outcome ?? "(no successor)"}",
                "Q11: insufficient. ADR-0005's reserve design reopens (gateway-owned locking over the library's refresh call), and I-6 records it",
                "the window covers a replay: task 3.8 stays on the library's token manager");
        }

        // Q11 (ii): replay the rotated-out token after the window, then check the successor.
        {
            var (rt1, _, _, error) = await NewFamily(App1);
            if (rt1 is null) throw new InvalidOperationException("no refresh token for Q11 (ii): " + error);
            var rot = await _raw.RefreshAsync(App1, rt1, Post);
            var rotated = Stopwatch.StartNew();
            Hold("app1", rot.RefreshToken);
            var remaining = TimeSpan.FromSeconds(12) - rotated.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
            var at = rotated.Elapsed;
            var late = await _raw.RefreshAsync(App1, rt1, Post);
            Hold("app1", late.RefreshToken);
            var successor = rot.RefreshToken is null ? null : await _raw.RefreshAsync(App1, rot.RefreshToken, Post);
            Hold("app1", successor?.RefreshToken);
            if (!Rotated(rot, rt1)) _out.Check("C-2.18", "the rotation Q11 (ii) needs: a refresh that returns a new refresh token", false, $"rotation {rot.Outcome}; so the window was not measured");
            else _out.Finding("C-2.18", "Cognito's grace window is insufficient, outcome (ii): a replay after the window makes the successor unusable, which is token-family revocation (Q11)",
                successor is not { Ok: true },
                $"rotation {rot.Outcome}; the replay at {at.TotalSeconds:N1} s: {late.Outcome}{(late.Ok ? " (honoured after the 10-second window: recorded, not a route)" : "")}; the successor afterwards: {successor?.Outcome ?? "(no successor)"}",
                "Q11: insufficient. ADR-0005's reserve design reopens, and I-6 records it",
                late.Ok ? "the successor keeps working; the late replay was honoured, which Q11 records and does not route"
                        : "a late replay is refused and the successor keeps working: the expected behaviour, not a finding");
        }

        // Q12: revoke the rotated-out token, then try its successor (T-B18).
        {
            var (rt1, _, _, error) = await NewFamily(App1);
            if (rt1 is null) throw new InvalidOperationException("no refresh token for Q12: " + error);
            var rot = await _raw.RefreshAsync(App1, rt1, Post);
            Hold("app1", rot.RefreshToken);
            var revoke = q12Revoke = await _raw.RevokeAsync(App1, rt1);
            var successor = rot.RefreshToken is null ? null : await _raw.RefreshAsync(App1, rot.RefreshToken, Post);
            Hold("app1", successor?.RefreshToken);
            if (!Rotated(rot, rt1) || !revoke.Ok)
                _out.Check("C-2.19", "the rotation and the revocation Q12 needs: a new refresh token, and the rotated-out one revoked", false,
                    $"rotation {rot.Outcome}; revoking the rotated-out token: {revoke.Outcome}; so Q12 was not answered");
            else _out.Finding("C-2.19", "revoking a rotated-out refresh token leaves its successor alive (Q12, T-B18)",
                successor is { Ok: true },
                $"rotation {rot.Outcome}; revoking the rotated-out token at {Redactor.Strip(_raw.RevocationEndpoint)}: {revoke.Outcome}; the successor afterwards: {successor?.Outcome ?? "(no successor)"}",
                "Q12: task 3.9 revokes the refresh token it reads from the session record at logout time, not the one held at the start of the request. The T-B18 race stays a documented residual: the successor lives until it expires, held only by the server, whose session record is deleted",
                "revocation reaches the successor: recorded, and nothing changes");
        }

        // Q13 and the refresh half of Q17: lengths at login, after one refresh (Basic header) and after five (form body).
        var lengths = new List<string>();
        var over = false;
        RawResult? basicRefresh = null;
        var postRefreshes = new List<RawResult>();
        foreach (var app in new[] { App1, App2 })
        {
            var (rt, at, idt, error) = await NewFamily(app);
            if (rt is null) { lengths.Add($"{app.AppId}: no family ({error})"); continue; }
            var seen = new List<int> { rt.Length };
            var current = rt;
            for (var i = 1; i <= 5; i++)
            {
                var style = app == App1 && i == 1 ? ClientCredentialStyle.AuthorizationHeader : Post;
                var r = await _raw.RefreshAsync(app, current, style);
                if (style == ClientCredentialStyle.AuthorizationHeader) basicRefresh = r; else postRefreshes.Add(r);
                if (!r.Ok) { lengths.Add($"{app.AppId}: refresh {i} {r.Outcome}"); break; }
                Hold(app.AppId, r.RefreshToken);
                current = r.RefreshToken ?? current;
                seen.Add(current.Length);
            }
            over |= seen.Any(n => n > 4096);
            lengths.Add($"{app.AppId} refresh token {seen[0]:N0} chars at login{(seen.Count > 1 ? $", {seen[1]:N0} after 1 refresh" : "")}{(seen.Count > 5 ? $", {seen[5]:N0} after 5" : "")}; access token {Len(at)}; ID token {Len(idt)}");
        }
        _out.Finding("C-2.20", "a refresh token is longer than the token library's 4,096-character limit (Q13, T-B11)",
            over, string.Join("; ", lengths),
            "Q13(a): item 4's way out, one in-house class that calls the refresh grant through Duende.IdentityModel",
            "every refresh token fits the library's limit");

        // Q17: the code exchange in a Basic header, in a new browser, for app2.
        _gw.Log.CodeExchangeStyle = "basic";
        LoginResult basicLogin;
        try { basicLogin = await Login("C", App2, _o.User); }
        finally { _gw.Log.CodeExchangeStyle = "post"; }
        var basicExchange = _gw.Log.CodeExchanges.LastOrDefault(x => x.Style == "basic");
        var postLogins = _logins.Where(kv => kv.Key.Context != "C").Select(kv => kv.Value).ToList();
        bool codePost = postLogins.Count > 0 && postLogins.All(l => l.Completed), codeBasic = basicLogin.Completed && basicExchange?.Outcome == "ok";
        bool refreshPost = postRefreshes.Count > 0 && postRefreshes.All(r => r.Ok), refreshBasic = basicRefresh is { Ok: true };
        // Revocation retries with a Basic header when the form body is refused (Raw.cs). A revocation that needed it is
        // the same Q17 answer for task 3.9, so it confirms the finding rather than passing unseen.
        var revokeStyles = _gw.Log.Logouts.Where(l => l.RevokeRoute == "profile").Select(l => l.RevokeAcceptedStyle).ToList();
        if (q12Revoke is not null) revokeStyles.Add(q12Revoke.AcceptedStyle);
        var revokeNeededBasic = revokeStyles.Any(s => s == "Basic header");
        _out.Finding("C-2.21", "only one client credential style works at the token or revocation endpoint (Q17, T-B4, T-B5)",
            !(codePost && codeBasic && refreshPost && refreshBasic) || revokeNeededBasic,
            $"code exchange in the form body (the handler, every login): {Yn(codePost)}; in a Basic header: {Yn(codeBasic)} ({basicExchange?.Outcome ?? "not attempted"}); refresh in the form body (the library and raw): {Yn(refreshPost)}; in a Basic header: {Yn(refreshBasic)} ({basicRefresh?.Outcome ?? "not attempted"}); revocations through the profile route and Q12 accepted with: {(revokeStyles.Count == 0 ? "(none sent)" : string.Join(", ", revokeStyles.Select(s => s ?? "refused")))}",
            "Q17: the style that works becomes the cognito profile's setting, for the code exchange, the refresh and the revocation alike",
            "both styles work: task 3.8 keeps ClientCredentialStyle.PostBody, so the code exchange and the refresh agree");
    }

    // ------------------------------------------------------------------------------------------------ item 3

    private async Task ItemThree()
    {
        // Managed login in a new browser for app2, so that its page renders rather than being skipped by app1's session.
        var lb = await Login("B", App2, _secondUser);

        var described = new Dictionary<string, ClientDescription>();
        foreach (var app in new[] { App1, App2 }) described[app.AppId] = await _describer.DescribeAsync(app.AppId, app.ClientId);

        _out.Check("C-3.1", "each OAuth client, as created, lists https://{host}/signin-oidc as a callback and https://{host}/ as a sign-out URL, with the port",
            new[] { App1, App2 }.All(a => described[a.AppId].CallbackUrls.Contains($"https://{a.Host}/signin-oidc") && described[a.AppId].LogoutUrls.Contains($"https://{a.Host}/")),
            string.Join("; ", new[] { App1, App2 }.Select(a => $"{a.AppId}: callbacks {string.Join(", ", described[a.AppId].CallbackUrls)}, sign-out {string.Join(", ", described[a.AppId].LogoutUrls)}, rotation {described[a.AppId].Rotation}, grace {described[a.AppId].GraceSeconds?.ToString() ?? "(not set)"} s"))
            + $" (from {described["app1"].Source})");

        bool Returned(string appId)
        {
            var host = _registry.Current.ByAppId[appId].Host;
            var callback = $"https://{host}/signin-oidc";
            var sent = _gw.Log.RedirectUris.TryGetValue(appId, out var uri) && uri == callback;
            var logins = _logins.Where(kv => kv.Key.AppId == appId && kv.Value.Completed).Select(kv => kv.Value).ToList();
            return sent && logins.Count > 0 && (_o.Driver == "manual" || logins.All(l => l.Hops.Contains(callback, StringComparer.OrdinalIgnoreCase)));
        }
        _out.Check("C-3.2", "every authorize call carried the callback with its port, and the provider sent the browser back to it, for both apps",
            Returned("app1") && Returned("app2"),
            string.Join("; ", new[] { App1, App2 }.Select(a => $"{a.AppId}: redirect_uri {(_gw.Log.RedirectUris.TryGetValue(a.AppId, out var x) ? x : "(none)")}; the last login in each of {_logins.Count(kv => kv.Key.AppId == a.AppId && kv.Value.Completed)} browser(s) came back to it")));

        _out.Check("C-3.3", "the provider accepted each app's sign-out URL: both logouts landed on https://{host}/",
            _logouts.TryGetValue("app1", out var o1) && o1.LandedOnRoot && _logouts.TryGetValue("app2", out var o2) && o2.LandedOnRoot,
            $"app1: {Yn(_logouts.GetValueOrDefault("app1")?.LandedOnRoot)}; app2: {Yn(_logouts.GetValueOrDefault("app2")?.LandedOnRoot)} (C-2.12, C-2.16)");

        if (_o.DryRun) _out.Skip("C-3.4", "each OAuth client has a managed-login branding style", "dry run: the test identity provider has no branding styles");
        else _out.Check("C-3.4", "each OAuth client has the managed-login branding style Terraform created",
            described.Values.All(d => d.BrandingId is not null),
            string.Join("; ", described.Select(kv => $"{kv.Key}: style {(kv.Value.BrandingId is null ? "(none)" : "present")}, Cognito-provided values {Yn(kv.Value.CognitoProvidedValues)}")));

        var a1 = _logins[("A", "app1")];
        if (_o.DryRun) _out.Skip("C-3.5", "managed login renders and a login completes, for both apps",
            $"dry run: the test identity provider has no login page. The logins completed: app1 {Yn(a1.Completed)}, app2 in a new browser {Yn(lb.Completed)}");
        else if (_o.Driver == "manual") _out.Check("C-3.5", "managed login renders and a login completes, for both apps (seen by the person, not by the harness)",
            a1.Completed && lb.Completed, $"app1: {Yn(a1.Completed)}; app2 in a new browser: {Yn(lb.Completed)}");
        else _out.Check("C-3.5", "managed login renders, asks for a password, and the login completes, for both apps",
            a1 is { Completed: true, SawManagedLogin: true, AskedForPassword: true } && lb is { Completed: true, SawManagedLogin: true, AskedForPassword: true },
            $"app1: {LoginDetail(a1)}; app2 in a new browser as {_secondUser}: {LoginDetail(lb)}");
    }

    // ------------------------------------------------------------------------------------------------ the end

    /// <summary>Revokes every refresh token the run left valid, so nothing outlives the run but the pool and the clients (Q23).</summary>
    private async Task Cleanup()
    {
        var tokens = _held.Concat(_gw.Sessions.All.Select(r => (r.AppId, TicketSerializer.Default.Deserialize(r.Ticket)!.Properties.GetTokenValue("refresh_token") ?? "")))
            .Where(t => t.Item2.Length > 0).Distinct().ToList();
        var ok = 0;
        foreach (var (appId, token) in tokens)
            if ((await _raw.RevokeAsync(_registry.Current.ByAppId[appId], token)).Ok) ok++;
        _out.Line($"cleanup revoked {ok} of {tokens.Count} refresh tokens the run obtained; the harness created nothing in AWS (Q23)");
        _out.Line();
    }

    private async Task<int> Finish()
    {
        var r = _out.Results;
        int held = r.Count(x => x.Kind == "OK"), bad = r.Count(x => x.Kind == "BAD"), found = r.Count(x => x.Kind == "FINDING"),
            notRep = r.Count(x => x.Kind == "not-rep"), skipped = r.Count(x => x.Kind == "skip");
        var verdicts = Report.Verdicts(r);
        _out.Line($"{held} expectations held, {bad} did not, {found} findings confirmed, {notRep} probes not reproduced, {skipped} skipped");
        if (_o.DryRun) _out.Line("DRY RUN: the verdicts below describe the harness against the test identity provider, not Cognito.");
        if (_o.LeakTest) _out.Line("LEAK TEST: a partial run that plants a token in the output; it gives no verdict.");
        else
        {
            // Each verdict line carries the label itself, so a line found by searching for "G0 item" cannot read as evidence.
            var label = _o.DryRun ? " (DRY RUN, not evidence)" : "";
            _out.Line($"G0 item 2{label}: {verdicts.Item2}");
            _out.Line($"G0 item 3{label}: {verdicts.Item3}");
        }
        _out.Line($"redaction: {_r.Count} credential values held as canaries; {_out.Withheld} line(s) withheld");

        var dir = Path.Combine(_root, "spikes", "g0", "cognito");
        var name = _o.DryRun ? "dry-run" : "cognito";
        if (_out.FinalCheck() is { } leak)
        {
            Console.WriteLine($"REFUSED the output holds {leak}. Nothing was written: not output-{name}.txt, not report-{name}.md.");
            return 4;
        }
        await File.WriteAllTextAsync(Path.Combine(dir, $"output-{name}.txt"), _out.Text);
        var report = Report.Render(_o.DryRun, r, verdicts, $"output-{name}.txt", _o.Driver);
        if (_r.Leak(report) is { } reportLeak)
        {
            Console.WriteLine($"REFUSED the report holds {reportLeak}. report-{name}.md was not written.");
            return 4;
        }
        await File.WriteAllTextAsync(Path.Combine(dir, $"report-{name}.md"), report);
        Console.WriteLine($"wrote spikes/g0/cognito/output-{name}.txt and report-{name}.md");
        if (!_o.DryRun && Report.WriteIntoReadme(Path.Combine(_root, "spikes", "g0", "README.md"), report))
            Console.WriteLine("wrote the \"Gate G0, as run\" table into spikes/g0/README.md");
        return _out.Failed ? 1 : 0;
    }

    private async Task StopAsync()
    {
        if (_driver is not null) await _driver.DisposeAsync();
        if (_gw is not null) { await _gw.App.StopAsync(); await _gw.App.DisposeAsync(); }
        if (_testIdp is not null) await _testIdp.DisposeAsync();
    }
}

/// <summary>Dry run: the secrets are the ones the harness generated for the test identity provider.</summary>
public sealed class DryRunSecrets(IReadOnlyList<AppEntry> apps, Redactor redactor) : ISecrets
{
    private readonly string _password = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(18));
    public Task<string> ClientSecretAsync(string appId) => Task.FromResult(apps.Single(a => a.AppId == appId).ClientSecret);
    public Task<string> PasswordAsync(string user) { redactor.Add(_password); return Task.FromResult(_password); }
}

/// <summary>Dry run: what the test identity provider registered stands in for DescribeUserPoolClient.</summary>
public sealed class DryRunDescriber(TestIdentityProvider idp) : IClientDescriber
{
    public Task<ClientDescription> DescribeAsync(string appId, string clientId)
    {
        var c = idp.Clients[clientId];
        return Task.FromResult(new ClientDescription(c.RedirectUris.ToArray(), c.PostLogoutUris.ToArray(), "rotating (TestIdp)", (int)idp.Faults.RefreshRotationGrace.TotalSeconds,
            null, null, "the test identity provider's registration, dry run"));
    }
}
