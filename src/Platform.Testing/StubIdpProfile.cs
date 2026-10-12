using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Platform.Abstractions;

namespace Platform.Testing;

/// <summary>
/// An <see cref="IIdpProfile"/> that records what it was asked to do and, by default, does nothing.
/// Arrange <see cref="OnConfigure"/> or <see cref="OnSignOutRedirect"/> when a test needs the
/// profile to have an effect.
///
/// Workstream 3 writes the real profiles in task 3.7. This one exists so a test can assert the
/// profile was consulted, for the right app and issuer, without depending on either of them.
/// </summary>
public sealed class StubIdpProfile(string name = "standard") : IIdpProfile
{
    private readonly List<(AppDefinition App, IssuerDefinition Issuer)> configured = [];
    private readonly List<(AppDefinition App, IssuerDefinition Issuer)> signOutRedirects = [];

    public string Name { get; } = name;

    /// <summary>Every <see cref="Configure"/> call, in order.</summary>
    public IReadOnlyList<(AppDefinition App, IssuerDefinition Issuer)> Configured => configured;

    /// <summary>Every <see cref="RedirectToSignOutAsync"/> call, in order.</summary>
    public IReadOnlyList<(AppDefinition App, IssuerDefinition Issuer)> SignOutRedirects => signOutRedirects;

    /// <summary>Applied to the options after the call is recorded.</summary>
    public Action<OpenIdConnectOptions, AppDefinition, IssuerDefinition>? OnConfigure { get; set; }

    /// <summary>Runs after the call is recorded, to redirect or handle the response.</summary>
    public Func<RedirectContext, Task>? OnSignOutRedirect { get; set; }

    public void Configure(OpenIdConnectOptions options, AppDefinition app, IssuerDefinition issuer)
    {
        configured.Add((app, issuer));
        OnConfigure?.Invoke(options, app, issuer);
    }

    public Task RedirectToSignOutAsync(RedirectContext context, AppDefinition app, IssuerDefinition issuer)
    {
        signOutRedirects.Add((app, issuer));
        return OnSignOutRedirect?.Invoke(context) ?? Task.CompletedTask;
    }
}
