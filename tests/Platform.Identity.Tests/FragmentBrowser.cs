using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Net.Http.Headers;
using Platform.Abstractions;

namespace Platform.Identity.Tests;

/// <summary>
/// A browser following a shell's sign-in chain (#994): the login, the provider's redirect, the shell's
/// callback, then each <c>/bff/chain</c> step, fragment authorize and fragment callback in turn, until
/// the gateway redirects anywhere else. It keeps the session cookie across the hops, as a jar would,
/// and sends each step with the fetch metadata a browser computes over the redirect chain:
/// <c>cross-site</c>, since the provider's origin is in it (ADR-0040). The provider is reached through
/// one client with a cookie container, so a provider session the fidelity switch models survives from
/// the shell's login to the fragment hops (#997).
/// </summary>
internal sealed class FragmentBrowser(IdentityGateway gateway, AppDefinition app) : IDisposable
{
    /// <summary>The fetch metadata of a navigation whose redirect chain passed through the provider.</summary>
    public static readonly IReadOnlyDictionary<string, string> CrossSiteNavigation = new Dictionary<string, string>
    {
        ["Sec-Fetch-Site"] = "cross-site",
        ["Sec-Fetch-Mode"] = "navigate",
        ["Sec-Fetch-Dest"] = "document",
    };

    private static readonly Dictionary<string, string> NoCookies = new(StringComparer.Ordinal);

    private HttpClient provider = NewProvider();

    /// <summary>
    /// Runs before each authorize leg, with its URL: for a test that changes the provider between the
    /// shell's login and a fragment's hop.
    /// </summary>
    public Func<Uri, Task>? BeforeAuthorize { get; set; }

    /// <summary>False to reach the provider with no cookies on every leg, so no provider session carries over.</summary>
    public bool KeepProviderSession { get; set; } = true;

    /// <summary>True to stop at the first <c>/bff/chain</c> step and return its redirect unsent.</summary>
    public bool StopAtChain { get; set; }

    /// <summary>The session cookie the browser holds, once the shell's callback set it.</summary>
    public Dictionary<string, string> Jar { get; } = new(StringComparer.Ordinal);

    /// <summary>Every redirect the browser followed, the gateway's and the provider's, in order.</summary>
    public List<string> Redirects { get; } = [];

    /// <summary>Every answer the gateway gave, in order.</summary>
    public List<IdentityGateway.Answer> Answers { get; } = [];

    /// <summary>
    /// Navigates to <paramref name="start"/> and follows every redirect on the gateway's host and the
    /// provider's, and returns the first answer that is not a redirect to either: the redirect to
    /// <c>returnUrl</c>, or whatever ended the chain.
    /// </summary>
    public async Task<IdentityGateway.Answer> NavigateAsync(string start, IReadOnlyDictionary<string, string>? headers = null)
    {
        IdentityGateway.Answer answer = await SendAsync(start, headers ?? IdentityGateway.Navigation, NoCookies);
        int followed = Redirects.Count;
        while (true)
        {
            // A browser gives up long before this (Fetch's network error at 20 redirects); a chain that
            // loops fails the test rather than hanging it.
            Assert.True(Redirects.Count - followed < 40, "The sign-in chain did not end within 40 redirects.");
            if (answer.Status != 302)
            {
                return answer;
            }

            if (answer.Location.StartsWith(gateway.Idp.AuthorizeEndpoint, StringComparison.Ordinal))
            {
                Dictionary<string, string> loginState = LoginState(answer);
                Uri back = await AuthorizeAsync(new Uri(answer.Location));
                if (!back.Host.Equals(app.Host, StringComparison.OrdinalIgnoreCase))
                {
                    return answer;
                }

                answer = await SendAsync(back.PathAndQuery, null, loginState);
                continue;
            }

            if (answer.Location.StartsWith(BffFragments.ChainPath + "?", StringComparison.Ordinal))
            {
                if (StopAtChain)
                {
                    return answer;
                }

                answer = await SendAsync(answer.Location, CrossSiteNavigation, NoCookies);
                continue;
            }

            return answer;
        }
    }

    /// <summary>Sends one request on the shell's host with the jar, keeping any session cookie it sets.</summary>
    public async Task<IdentityGateway.Answer> SendAsync(
        string pathAndQuery, IReadOnlyDictionary<string, string>? headers, IReadOnlyDictionary<string, string> extra)
    {
        Dictionary<string, string> cookies = new(Jar, StringComparer.Ordinal);
        foreach ((string name, string value) in extra)
        {
            cookies[name] = value;
        }

        IdentityGateway.Answer answer = await gateway.SendAsync(app, pathAndQuery, cookies, headers: headers);
        Answers.Add(answer);
        if (answer.Status == 302)
        {
            Redirects.Add(answer.Location);
        }

        foreach (SetCookieHeaderValue cookie in answer.Cookies.Where(c => c.Name.Value == CookieOptionsSetup.CookieName))
        {
            if (cookie.Expires is { } expires && expires <= DateTimeOffset.UtcNow)
            {
                Jar.Remove(cookie.Name.Value!);
            }
            else
            {
                Jar[cookie.Name.Value!] = cookie.Value.Value!;
            }
        }

        return answer;
    }

    /// <summary>The provider's authorize leg: where it redirects the browser back to.</summary>
    public async Task<Uri> AuthorizeAsync(Uri authorize)
    {
        if (BeforeAuthorize is { } hook)
        {
            await hook(authorize);
        }

        if (!KeepProviderSession)
        {
            provider.Dispose();
            provider = NewProvider();
        }

        using HttpResponseMessage response = await provider.GetAsync(authorize, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Redirects.Add(response.Headers.Location!.AbsoluteUri);
        return response.Headers.Location!;
    }

    /// <summary>The login-state cookies a challenge set, which its callback must carry.</summary>
    public static Dictionary<string, string> LoginState(IdentityGateway.Answer challenge) => challenge.Cookies
        .Where(c => c.Name.Value != CookieOptionsSetup.CookieName && (!c.Expires.HasValue || c.Expires > DateTimeOffset.UtcNow))
        .ToDictionary(c => c.Name.Value!, c => c.Value.Value ?? "", StringComparer.Ordinal);

    public void Dispose() => provider.Dispose();

    private static HttpClient NewProvider() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        CheckCertificateRevocationList = true,
        CookieContainer = new CookieContainer(),
        UseCookies = true,
    });
}

/// <summary>Reads a session record's ticket as the fragments spec stores it (#994).</summary>
internal static class Held
{
    public static AuthenticationProperties Properties(SessionRecord record) =>
        TicketSerializer.Default.Deserialize(record.Ticket.ToArray())!.Properties;

    public static string StateOf(SessionRecord record, FragmentDefinition fragment) =>
        FragmentSets.StateOf(Properties(record), fragment);
}

/// <summary>
/// A session store that runs a test's hook before each versioned replace and each read, over a real
/// store: to make another writer win a replace, remove the record, or fail as the DynamoDB store fails
/// (<see cref="SessionStoreUnavailableException"/>).
/// </summary>
internal sealed class ScriptedSessionStore(ISessionStore inner) : ISessionStore
{
    private int replaces;

    public ISessionStore Inner { get; } = inner;

    /// <summary>Runs before each replace, with its key and the attempt's number from 1.</summary>
    public Func<string, int, Task>? BeforeReplace { get; set; }

    /// <summary>Runs after each replace the inner store accepted, with its attempt's number: to fail a committed write.</summary>
    public Func<int, Task>? AfterReplace { get; set; }

    /// <summary>Runs before each read.</summary>
    public Func<string, Task>? BeforeGet { get; set; }

    /// <summary>Runs after each read, with its key: to know a request has read the session.</summary>
    public Func<string, Task>? AfterGet { get; set; }

    /// <summary>Runs before each create: to fail the login callback's.</summary>
    public Func<Task>? BeforeCreate { get; set; }

    public int Replaces => Volatile.Read(ref replaces);

    public async Task<string> CreateAsync(SessionRecord record, CancellationToken ct)
    {
        if (BeforeCreate is { } hook)
        {
            await hook();
        }

        return await Inner.CreateAsync(record, ct);
    }

    public async Task<SessionRecord?> GetAsync(string key, CancellationToken ct)
    {
        if (BeforeGet is { } hook)
        {
            await hook(key);
        }

        SessionRecord? record = await Inner.GetAsync(key, ct);
        if (AfterGet is { } after)
        {
            await after(key);
        }

        return record;
    }

    public async Task<bool> ReplaceAsync(string key, SessionRecord record, long expectedVersion, CancellationToken ct)
    {
        int attempt = Interlocked.Increment(ref replaces);
        if (BeforeReplace is { } hook)
        {
            await hook(key, attempt);
        }

        bool won = await Inner.ReplaceAsync(key, record, expectedVersion, ct);
        if (won && AfterReplace is { } after)
        {
            await after(attempt);
        }

        return won;
    }

    public Task RemoveAsync(string key, CancellationToken ct) => Inner.RemoveAsync(key, ct);

    public Task RemoveAppAsync(string appId, CancellationToken ct) => Inner.RemoveAppAsync(appId, ct);

    /// <summary>Another writer's replace of the record, bumping its version, as a concurrent refresh would.</summary>
    public async Task BumpAsync(string key)
    {
        SessionRecord current = (await Inner.GetAsync(key, CancellationToken.None))!;
        Assert.True(await Inner.ReplaceAsync(key, current with { Version = current.Version + 1 }, current.Version, CancellationToken.None));
    }

    public static SessionStoreUnavailableException Unavailable(string operation) =>
        new(operation, TimeSpan.FromSeconds(5), null);
}
