using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// The issuers in platform configuration (<c>Issuers:{name}</c>, <c>contracts/platform-config.schema.json</c>),
/// each with the one backchannel and the one metadata manager every app on it shares (plan row 3.2).
///
/// Sharing is the point. Left to itself, the framework's post-configuration gives every scheme its own
/// <see cref="HttpClient"/> and its own <see cref="ConfigurationManager{T}"/>, so each app would fetch
/// discovery and the signing keys for itself, and each app's options would hold a socket pool. One
/// manager per issuer fetches them once for every app (R-A7), and a key rotation at the provider is
/// picked up once for all of them. The framework builds its own only when the options carry none:
/// https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/OpenIdConnect/src/OpenIdConnectPostConfigureOptions.cs
///
/// Configuration is read once, when the directory is first resolved. An issuer is a property of the
/// environment (assumption A4), not of the registry, so it does not change while the process runs.
/// </summary>
public sealed class IssuerDirectory : IDisposable
{
    private readonly IReadOnlyDictionary<string, IssuerDefinition> issuers;
    private readonly ConcurrentDictionary<string, Lazy<SharedOidc>> shared = new(StringComparer.Ordinal);
    private readonly TimeSpan backchannelTimeout;

    /// <param name="configuration">Platform configuration, read for its <c>Issuers</c> section.</param>
    /// <param name="backchannelTimeout">
    /// The shared backchannel's timeout, which bounds the code exchange: the token source's provider
    /// deadline, as #332 F1 sets it, so a login's exchange is bounded by the budget a refresh is.
    /// </param>
    public IssuerDirectory(IConfiguration configuration, TimeSpan backchannelTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(backchannelTimeout, TimeSpan.Zero);
        this.backchannelTimeout = backchannelTimeout;

        Dictionary<string, IssuerDefinition> read = new(StringComparer.Ordinal);
        foreach (IConfigurationSection issuer in configuration.GetSection("Issuers").GetChildren())
        {
            read[issuer.Key] = new IssuerDefinition(
                Authority: Required(issuer, "Authority"),
                Profile: Required(issuer, "Profile"),
                BrowserOrigin: new Uri(Required(issuer, "BrowserOrigin"), UriKind.Absolute));
        }

        issuers = read;
    }

    /// <summary>The issuer an entry's <c>oauth.issuer</c> names. Throws when configuration has no such issuer.</summary>
    public IssuerDefinition Get(string name) => issuers.TryGetValue(name, out IssuerDefinition? issuer)
        ? issuer
        : throw new InvalidOperationException($"Platform configuration has no issuer '{name}' (Issuers:{name}).");

    /// <summary>The backchannel and metadata manager for the issuer named <paramref name="name"/>, built once.</summary>
    public SharedOidc Shared(string name)
    {
        IssuerDefinition issuer = Get(name);
        return shared.GetOrAdd(name, _ => new Lazy<SharedOidc>(() => new SharedOidc(issuer, backchannelTimeout))).Value;
    }

    public void Dispose()
    {
        foreach (Lazy<SharedOidc> built in shared.Values.Where(l => l.IsValueCreated))
        {
            built.Value.Backchannel.Dispose();
        }
    }

    private static string Required(IConfigurationSection issuer, string key) =>
        issuer[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Issuers:{issuer.Key}:{key} is missing from platform configuration.");
}

/// <summary>One issuer's backchannel and metadata manager, shared by every app on it.</summary>
public sealed class SharedOidc
{
    internal SharedOidc(IssuerDefinition issuer, TimeSpan timeout)
    {
        Uri authority = new(issuer.Authority.TrimEnd('/') + "/", UriKind.Absolute);

        // The handler's own default response limit, 10 MB, for a backchannel it builds
        // (OpenIdConnectPostConfigureOptions). Its default 60-second timeout is replaced by the provider
        // deadline (#332 F1): the handler applies OpenIdConnectOptions.BackchannelTimeout only to a
        // backchannel it builds itself, and this one is shared and set by the options setup, so the bound
        // has to be this client's own Timeout, set before its first request as HttpClient requires.
        // https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/OpenIdConnect/src/OpenIdConnectPostConfigureOptions.cs
        // A pooled connection is recycled so a provider's DNS change is seen without a restart.
        Backchannel = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = timeout,
            MaxResponseContentBufferSize = 10 * 1024 * 1024,
        };

        // Platform configuration admits only an https authority (platform-config.schema.json). The one
        // exception here is plain HTTP to a loopback address, which is where the hermetic tier's test
        // provider listens; nothing off the machine is ever fetched without TLS.
        bool loopbackHttp = authority.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(authority.Host, out IPAddress? address)
            && IPAddress.IsLoopback(address);

        Metadata = new ConfigurationManager<OpenIdConnectConfiguration>(
            new Uri(authority, ".well-known/openid-configuration").AbsoluteUri,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(Backchannel) { RequireHttps = !loopbackHttp });
    }

    public HttpClient Backchannel { get; }

    public IConfigurationManager<OpenIdConnectConfiguration> Metadata { get; }
}
