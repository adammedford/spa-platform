using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// The client credential style of an app's issuer, as that issuer's profile states it (ADR-0017
/// decision 2). One answer for the refresh of task 3.8 and the revocation at logout, so the two cannot
/// disagree; the form body when no profile is registered under the issuer's profile name.
/// </summary>
internal sealed class CredentialStyles(IssuerDirectory issuers, IEnumerable<IIdpProfile> profiles)
{
    public ClientCredentialStyle Of(AppDefinition app)
    {
        string profile = issuers.Get(app.OAuth.Issuer).Profile;
        return profiles.FirstOrDefault(p => string.Equals(p.Name, profile, StringComparison.Ordinal))?.ClientCredentialStyle
            ?? ClientCredentialStyle.PostBody;
    }
}
