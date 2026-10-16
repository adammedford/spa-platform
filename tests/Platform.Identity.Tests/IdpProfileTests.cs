using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// The two profiles of task 3.7 as <c>AddPlatformIdentity</c> registers them, at the
/// <see cref="IIdpProfile"/> interface: their names, their credential styles (ADR-0017) and where each
/// sends the browser at logout, given an issuer whose browser origin is not its authority.
/// </summary>
public sealed class IdpProfileTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: "pool");

    /// <summary>A Cognito-shaped issuer: tokens from cognito-idp, the browser at the hosted UI's domain.</summary>
    private static readonly IssuerDefinition Pool = new(
        "https://cognito-idp.eu-west-2.amazonaws.com/eu-west-2_Example",
        "cognito",
        new Uri("https://example-pool.auth.eu-west-2.amazoncognito.com"));

    [Fact]
    public void AddPlatformIdentity_registers_one_profile_for_each_name_platform_configuration_permits()
    {
        // Read from the contract, not repeated here: an issuer may name any value of the Profile enum,
        // and a name with no profile would get no logout of its own.
        IReadOnlyDictionary<string, IIdpProfile> profiles = Profiles();

        Assert.Equal(PermittedProfileNames().Order(StringComparer.Ordinal), profiles.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_standard_profile_sends_the_secret_in_the_form_body_and_the_cognito_profile_in_a_basic_header()
    {
        // ADR-0017, C-2.19 and C-2.21: Cognito's revocation endpoint refuses a form-body secret.
        IReadOnlyDictionary<string, IIdpProfile> profiles = Profiles();

        Assert.Equal(ClientCredentialStyle.PostBody, profiles["standard"].ClientCredentialStyle);
        Assert.Equal(ClientCredentialStyle.AuthorizationHeader, profiles["cognito"].ClientCredentialStyle);
    }

    [Fact]
    public async Task The_cognito_profile_redirects_to_the_logout_endpoint_on_the_browser_origin_not_the_authority()
    {
        RedirectContext context = SignOutContext();

        await Profiles()["cognito"].RedirectToSignOutAsync(context, App1, Pool);

        Assert.True(context.Handled);
        Assert.Equal(302, context.Response.StatusCode);
        Assert.Equal(
            "https://example-pool.auth.eu-west-2.amazoncognito.com/logout?client_id=" + Uri.EscapeDataString(App1.OAuth.ClientId)
                + "&logout_uri=" + Uri.EscapeDataString($"https://{App1.Host}/"),
            context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task The_standard_profile_leaves_the_handlers_rp_initiated_request_as_built()
    {
        // The handler builds the request from discovery before the event and adds its state after it;
        // the standard profile keeps it, so the handler redirects (R-C6).
        RedirectContext context = SignOutContext();

        await Profiles()["standard"].RedirectToSignOutAsync(context, App1, Pool with { Profile = "standard" });

        Assert.False(context.Handled);
        Assert.Equal("https://idp.example.test/endsession", context.ProtocolMessage.IssuerAddress);
        Assert.Equal("hint", context.ProtocolMessage.IdTokenHint);
        Assert.Equal($"https://{App1.Host}/signout-callback-oidc", context.ProtocolMessage.PostLogoutRedirectUri);
        Assert.False(context.Response.Headers.ContainsKey("Location"));
    }

    /// <summary><c>Issuers:{name}:Profile</c>'s enum in <c>contracts/platform-config.schema.json</c>.</summary>
    private static string[] PermittedProfileNames([CallerFilePath] string thisFile = "")
    {
        string schema = Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "contracts", "platform-config.schema.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(schema));
        JsonElement issuer = document.RootElement.GetProperty("properties").GetProperty("Issuers").GetProperty("additionalProperties");
        return [.. issuer.GetProperty("properties").GetProperty("Profile").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!)];
    }

    private static IReadOnlyDictionary<string, IIdpProfile> Profiles()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection();
        builder.AddPlatformIdentity();
        using ServiceProvider services = builder.Services.BuildServiceProvider();
        return services.GetServices<IIdpProfile>().ToDictionary(p => p.Name, StringComparer.Ordinal);
    }

    /// <summary>What the handler hands the sign-out event: the RP-initiated request it built from discovery.</summary>
    private static RedirectContext SignOutContext()
    {
        DefaultHttpContext http = new();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString(App1.Host);
        return new RedirectContext(
            http,
            new AuthenticationScheme(SchemeNames.Oidc(App1.AppId), null, typeof(OpenIdConnectHandler)),
            new OpenIdConnectOptions(),
            new AuthenticationProperties { RedirectUri = "/" })
        {
            ProtocolMessage = new OpenIdConnectMessage
            {
                IssuerAddress = "https://idp.example.test/endsession",
                IdTokenHint = "hint",
                PostLogoutRedirectUri = $"https://{App1.Host}/signout-callback-oidc",
            },
        };
    }
}
