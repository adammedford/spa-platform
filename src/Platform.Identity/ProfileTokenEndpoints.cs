using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Abstractions;
using LibraryCredentialStyle = Duende.IdentityModel.Client.ClientCredentialStyle;

namespace Platform.Identity;

/// <summary>
/// The token library's refresh and revoke calls, one instance per <see cref="ClientCredentialStyle"/>,
/// so that each issuer's profile decides how its client presents the secret (ADR-0017 decision 2).
///
/// The library takes the style from <see cref="UserTokenManagementOptions.ClientCredentialStyle"/>, one
/// value for the whole process, which its endpoint reads from the <see cref="IOptions{TOptions}"/> it is
/// built with. So each style gets an endpoint of the library's own type, built by the container with
/// options that name that style; the protocol step is still entirely the library's. The type is the one
/// <c>AddOpenIdConnectAccessTokenManagement</c> registered for <see cref="IOpenIdConnectUserTokenEndpoint"/>,
/// found from that registration rather than named, because it is internal to the package (4.2.0).
/// </summary>
internal sealed class ProfileTokenEndpoints
{
    /// <summary>
    /// The default challenge scheme the library's options hook is given: a name the scheme provider
    /// never resolves, since every scheme it derives starts <c>oidc:</c> or <c>cookie:</c>. The hook
    /// reads the default challenge scheme once; under a host-scoped provider it cannot be built outside
    /// a request (T-B1) and inside one it binds to the first caller's app (T-B2). With the sentinel it
    /// binds to no app (T-A2, T-A3), and every call names its schemes instead (T-A7).
    /// </summary>
    public const string SentinelScheme = "token-management:none";

    private readonly IOpenIdConnectUserTokenEndpoint postBody;
    private readonly IOpenIdConnectUserTokenEndpoint authorizationHeader;

    public ProfileTokenEndpoints(IServiceProvider services, Type endpointType)
    {
        postBody = Build(services, endpointType, LibraryCredentialStyle.PostBody);
        authorizationHeader = Build(services, endpointType, LibraryCredentialStyle.AuthorizationHeader);
    }

    /// <summary>The endpoint whose calls present the secret the way <paramref name="style"/> says.</summary>
    public IOpenIdConnectUserTokenEndpoint For(ClientCredentialStyle style) => style switch
    {
        // By name, never by cast: the two enums order these members the other way round, so a cast
        // would compile and invert the style (see ClientCredentialStyle in Platform.Abstractions).
        ClientCredentialStyle.PostBody => postBody,
        ClientCredentialStyle.AuthorizationHeader => authorizationHeader,
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "No such client credential style."),
    };

    /// <summary>The library's name for <paramref name="style"/>: by name, never by cast (see <see cref="For"/>).</summary>
    public static LibraryCredentialStyle LibraryStyle(ClientCredentialStyle style) => style switch
    {
        ClientCredentialStyle.PostBody => LibraryCredentialStyle.PostBody,
        ClientCredentialStyle.AuthorizationHeader => LibraryCredentialStyle.AuthorizationHeader,
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "No such client credential style."),
    };

    /// <summary>
    /// The implementation type the library registered for its endpoint. Throws when the registration is
    /// not a plain type, which would mean the pinned package changed how it registers it.
    /// </summary>
    public static Type RegisteredEndpointType(IServiceCollection services) =>
        services.LastOrDefault(d => d.ServiceType == typeof(IOpenIdConnectUserTokenEndpoint))?.ImplementationType
        ?? throw new InvalidOperationException(
            "AddOpenIdConnectAccessTokenManagement registered no implementation type for IOpenIdConnectUserTokenEndpoint.");

    private static IOpenIdConnectUserTokenEndpoint Build(IServiceProvider services, Type endpointType, LibraryCredentialStyle style) =>
        (IOpenIdConnectUserTokenEndpoint)ActivatorUtilities.CreateInstance(services, endpointType, Options.Create(new UserTokenManagementOptions
        {
            ChallengeScheme = Scheme.Parse(SentinelScheme),
            ClientCredentialStyle = style,
        }));
}
