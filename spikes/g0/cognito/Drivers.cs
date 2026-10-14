using System.Net;
using Microsoft.Playwright;

namespace Spike;

/// <summary>What a login showed. Hops are URLs with their query removed: an authorize or callback query holds state and code.</summary>
public sealed record LoginResult(bool Completed, bool? AskedForPassword, bool? SawManagedLogin, IReadOnlyList<string> Hops, string? Error);

/// <summary>What a logout showed. LandedOnRoot: the provider sent the browser back to https://{host}/ (R-C7).</summary>
public sealed record LogoutResult(int Status, string? ProviderLogout, bool LandedOnRoot, bool? NextLoginAskedForPassword, IReadOnlyList<string> Hops);

/// <summary>
/// Drives managed login (Q2). A "browser context" is one person's browser: its cookies at the provider are shared by
/// every app signed in from it, which is what Q19's scenario needs. After a login the driver copies the gateway's cookies
/// into a harness-side <see cref="Browser"/> for that context, so the harness can call the gateway itself.
/// </summary>
public interface ILoginDriver : IAsyncDisposable
{
    string Name { get; }
    Browser BrowserFor(string context);
    Task<LoginResult> LoginAsync(string context, AppEntry app, string path, string user, Func<Task<string>> password);
    Task<LogoutResult> LogoutAsync(string context, AppEntry app);
}

/// <summary>Where the selectors for managed login live, so a change in Cognito's page is one edit.</summary>
public static class ManagedLoginPage
{
    public const string Username = "input[name='username']:visible";
    public const string Password = "input[name='password']:visible, input[type='password']:visible";
    public const string Submit = "button[type='submit']:visible, input[type='submit']:visible";
}

public sealed class PlaywrightDriver : ILoginDriver
{
    private readonly IPlaywright _pw;
    private readonly IBrowser _browser;
    private readonly bool _ignoreTls;
    private readonly string _gatewayBackDoor, _idpOrigin;
    private readonly Redactor _redactor;
    private readonly Dictionary<string, (IBrowserContext Context, IPage Page, List<string> Hops)> _contexts = new();
    private readonly Dictionary<string, Browser> _browsers = new();

    public string Name => "playwright";

    /// <summary>The Chromium build this Playwright package pins, from the browsers.json it ships.</summary>
    public static string Revision
    {
        get
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, ".playwright", "package", "browsers.json")));
                var chromium = doc.RootElement.GetProperty("browsers").EnumerateArray().First(b => b.GetProperty("name").GetString() == "chromium");
                return $"{chromium.GetProperty("revision").GetString()} ({chromium.GetProperty("browserVersion").GetString()})";
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException) { return "(unknown)"; }
        }
    }

    private PlaywrightDriver(IPlaywright pw, IBrowser browser, bool ignoreTls, string backDoor, string idpOrigin, Redactor redactor)
        => (_pw, _browser, _ignoreTls, _gatewayBackDoor, _idpOrigin, _redactor) = (pw, browser, ignoreTls, backDoor, idpOrigin, redactor);

    /// <summary>Chromium as pinned by Microsoft.Playwright 1.63.0. In the dry run the app hosts resolve to loopback in the browser only.</summary>
    public static async Task<PlaywrightDriver> StartAsync(bool dryRun, bool headed, string backDoor, string idpOrigin, Redactor redactor)
    {
        var pw = await Playwright.CreateAsync();
        var args = dryRun ? new[] { "--host-resolver-rules=MAP *.spa-poc.test 127.0.0.1" } : [];
        var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = !headed, Args = args });
        return new PlaywrightDriver(pw, browser, dryRun, backDoor, idpOrigin, redactor);
    }

    /// <summary>Null when Chromium for this Playwright version launches here; otherwise why not.</summary>
    public static async Task<string?> MissingAsync()
    {
        try
        {
            using var pw = await Playwright.CreateAsync();
            await using var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            return null;
        }
        catch (PlaywrightException ex)
        {
            return AwsCredentials.OneLine(ex.Message.Split('\n')[0]);
        }
    }

    public Browser BrowserFor(string context)
    {
        if (!_browsers.TryGetValue(context, out var b)) _browsers[context] = b = new Browser(_gatewayBackDoor, _idpOrigin);
        return b;
    }

    private async Task<(IBrowserContext Context, IPage Page, List<string> Hops)> ContextFor(string name)
    {
        if (_contexts.TryGetValue(name, out var c)) return c;
        var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = _ignoreTls });
        var page = await context.NewPageAsync();
        var hops = new List<string>();
        page.Request += (_, r) => { if (r.IsNavigationRequest) lock (hops) hops.Add(Redactor.Strip(r.Url)); };
        return _contexts[name] = (context, page, hops);
    }

    public async Task<LoginResult> LoginAsync(string context, AppEntry app, string path, string user, Func<Task<string>> password)
    {
        var (ctx, page, hops) = await ContextFor(context);
        lock (hops) hops.Clear();
        bool asked = false, saw = false;
        string? error = null;
        try
        {
            await page.GotoAsync($"https://{app.Host}{path}");
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (true)
            {
                if (DateTime.UtcNow > deadline) { error = $"no login after 90 s; the page is at {Redactor.Strip(page.Url)}"; break; }
                var url = new Uri(page.Url);
                if (url.Authority.Equals(app.Host, StringComparison.OrdinalIgnoreCase))
                {
                    var body = await SafeText(page);
                    if (url.AbsolutePath == "/" && body.Contains("shell app=" + app.AppId, StringComparison.Ordinal)) break;
                    if (body.StartsWith("login failed", StringComparison.Ordinal)) { error = AwsCredentials.OneLine(body); break; }
                }
                else if (url.GetLeftPart(UriPartial.Authority).Equals(_idpOrigin, StringComparison.OrdinalIgnoreCase))
                {
                    var userField = page.Locator(ManagedLoginPage.Username);
                    var passField = page.Locator(ManagedLoginPage.Password);
                    var filled = false;
                    if (await userField.CountAsync() > 0 && (await userField.First.InputValueAsync()).Length == 0) { await userField.First.FillAsync(user); filled = true; }
                    if (await passField.CountAsync() > 0)
                    {
                        asked = true;
                        await passField.First.FillAsync(await password());
                        filled = true;
                    }
                    if (filled)
                    {
                        saw = true;
                        await page.Locator(ManagedLoginPage.Submit).First.ClickAsync();
                        await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 30_000 });
                        continue;
                    }
                }
                await Task.Delay(250);
            }
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            error = AwsCredentials.OneLine(ex.Message.Split('\n')[0]);
            if (error.Contains("ERR_CERT", StringComparison.Ordinal)) error += ". The browser does not trust the gateway's certificate: run tools/scripts/certs.sh trust";
        }

        if (error is null) await CopyCookies(ctx, context, app);
        List<string> seen;
        lock (hops) seen = [.. hops];
        return new LoginResult(error is null, asked, saw, seen, error);
    }

    public async Task<LogoutResult> LogoutAsync(string context, AppEntry app)
    {
        var (_, page, hops) = await ContextFor(context);
        // A form POST from a page on the app's own origin, as the SPA's logout button sends it. The browser then follows the
        // gateway's redirect to the provider's logout, and the provider's redirect back to "/".
        await page.GotoAsync($"https://{app.Host}/bff/session");
        int status = 0;
        string? location = null;
        void OnResponse(object? _, IResponse r)
        {
            if (r.Request.Method == "POST" && r.Url.EndsWith("/bff/logout", StringComparison.Ordinal)) { status = r.Status; r.Headers.TryGetValue("location", out location); }
        }
        page.Response += OnResponse;
        lock (hops) hops.Clear();
        try
        {
            await page.RunAndWaitForNavigationAsync(
                () => page.EvaluateAsync("() => { const f = document.createElement('form'); f.method = 'POST'; f.action = '/bff/logout'; document.body.appendChild(f); f.submit(); }"),
                new PageRunAndWaitForNavigationOptions { UrlFunc = u => !u.EndsWith("/bff/session", StringComparison.Ordinal) });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }
        finally { page.Response -= OnResponse; }

        // The logout lands on "/", which challenges at once. Where that ends shows whether the provider's session survived.
        bool? asked = null;
        var url = new Uri(page.Url);
        if (url.GetLeftPart(UriPartial.Authority).Equals(_idpOrigin, StringComparison.OrdinalIgnoreCase))
        {
            // Managed login may ask for the user name first and the password on a second page; either means it asks.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (asked != true && DateTime.UtcNow < deadline)
            {
                asked = await page.Locator(ManagedLoginPage.Username).CountAsync() + await page.Locator(ManagedLoginPage.Password).CountAsync() > 0;
                if (asked != true) await Task.Delay(250);
            }
        }
        else if (url.Authority.Equals(app.Host, StringComparison.OrdinalIgnoreCase))
            asked = false;
        List<string> seen;
        lock (hops) seen = [.. hops];
        return new LogoutResult(status, location, seen.Contains($"https://{app.Host}/", StringComparer.OrdinalIgnoreCase), asked, seen);
    }

    private async Task CopyCookies(IBrowserContext ctx, string context, AppEntry app)
    {
        var browser = BrowserFor(context);
        browser.Jar.Remove(app.Host);
        foreach (var c in await ctx.CookiesAsync([$"https://{app.Host}/"]))
        {
            _redactor.Add(c.Value);
            browser.Plant(app.Host, c.Name, c.Value, c.Path);
        }
    }

    private static async Task<string> SafeText(IPage page)
    {
        try { return await page.InnerTextAsync("body", new PageInnerTextOptions { Timeout = 2_000 }); }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { return ""; }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _contexts.Values) await c.Context.CloseAsync();
        await _browser.CloseAsync();
        _pw.Dispose();
    }
}

/// <summary>
/// The fallback for a machine where Chromium will not install (Q2): a person signs in with their own browser, and the
/// harness waits for the gateway to see the signed-in request. What only a browser can show is skipped.
/// </summary>
public sealed class ManualDriver(GatewayLog log, string backDoor, string idpOrigin, Output output) : ILoginDriver
{
    private readonly Dictionary<string, Browser> _browsers = new();
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

    public string Name => "manual";

    public Browser BrowserFor(string context)
    {
        if (!_browsers.TryGetValue(context, out var b)) _browsers[context] = b = new Browser(backDoor, idpOrigin);
        return b;
    }

    public async Task<LoginResult> LoginAsync(string context, AppEntry app, string path, string user, Func<Task<string>> password)
    {
        log.CookieTap.TryGetValue(app.Host, out var before);
        output.Line($"ACTION  In browser \"{context}\", open https://{app.Host}{path} and sign in as {user}. Use a private window for a new browser. Waiting up to 5 minutes.");
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            if (log.CookieTap.TryGetValue(app.Host, out var now) && now != before)
            {
                var browser = BrowserFor(context);
                browser.Jar.Remove(app.Host);
                foreach (var pair in now.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    output.Redactor.Add(pair[(eq + 1)..]);   // each cookie value, as the Playwright driver adds them
                    browser.Plant(app.Host, pair[..eq], pair[(eq + 1)..]);
                }
                return new LoginResult(true, null, null, [], null);
            }
            await Task.Delay(500);
        }
        return new LoginResult(false, null, null, [], "nobody signed in within 5 minutes");
    }

    public async Task<LogoutResult> LogoutAsync(string context, AppEntry app)
    {
        var browser = BrowserFor(context);
        var response = await browser.Send(HttpMethod.Post, $"https://{app.Host}/bff/logout");
        var location = response.Headers.Location?.ToString();
        var landed = false;
        var hops = new List<string>();
        if (location is not null)
        {
            // Without the person's provider cookie this ends no provider session; it shows the provider accepts the logout_uri.
            hops.Add(Redactor.Strip(location));
            var at = await Http.GetAsync(location);
            var back = at.Headers.Location?.ToString();
            if (back is not null) hops.Add(Redactor.Strip(back));
            landed = back is not null && back.Equals($"https://{app.Host}/", StringComparison.OrdinalIgnoreCase);
        }
        return new LogoutResult((int)response.StatusCode, location, landed, null, hops);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Dry run only: the test identity provider has no login page, so following redirects is a login.</summary>
public sealed class HttpDriver(string backDoor, string idpOrigin) : ILoginDriver
{
    private readonly Dictionary<string, Browser> _browsers = new();

    public string Name => "http (dry run only)";

    public Browser BrowserFor(string context)
    {
        if (!_browsers.TryGetValue(context, out var b)) _browsers[context] = b = new Browser(backDoor, idpOrigin);
        return b;
    }

    public async Task<LoginResult> LoginAsync(string context, AppEntry app, string path, string user, Func<Task<string>> password)
    {
        var (final, hops) = await BrowserFor(context).Navigate($"https://{app.Host}{path}");
        var body = await final.Content.ReadAsStringAsync();
        var ok = final.StatusCode == HttpStatusCode.OK && body.Contains("shell app=" + app.AppId, StringComparison.Ordinal);
        return new LoginResult(ok, false, false, hops.Select(Redactor.Strip).ToList(), ok ? null : $"{(int)final.StatusCode} {AwsCredentials.OneLine(body)}");
    }

    public async Task<LogoutResult> LogoutAsync(string context, AppEntry app)
    {
        var browser = BrowserFor(context);
        var response = await browser.Send(HttpMethod.Post, $"https://{app.Host}/bff/logout");
        var location = response.Headers.Location?.ToString();
        var hops = new List<string>();
        bool? asked = null;
        if (location is not null)
        {
            var (_, visited) = await browser.Navigate(location);
            hops = visited.Select(Redactor.Strip).ToList();
            asked = false;   // the test identity provider never asks
        }
        return new LogoutResult((int)response.StatusCode, location, hops.Contains($"https://{app.Host}/", StringComparer.OrdinalIgnoreCase), asked, hops);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
