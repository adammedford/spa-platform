using Microsoft.AspNetCore.Authentication;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// The eager sign-in chain (#994; the fragments spec #961 -> "The eager chain"): one login on a shell
/// with fragments acquires a token set for the shell and one for each fragment, in one session record,
/// through <c>/bff/chain</c>, and lands on <c>returnUrl</c>.
/// </summary>
public sealed class FragmentChainTests
{
    internal static readonly FragmentDefinition Orders = TestApps.Fragment("orders", "shell");
    internal static readonly FragmentDefinition Billing = TestApps.Fragment("billing", "shell");

    internal static AppDefinition Shell(params FragmentDefinition[] fragments) =>
        TestApps.Entry("shell", issuer: IdentityGateway.IssuerName, fragments: fragments,
            fingerprint: "sha256:shell-" + string.Join("-", fragments.Select(f => f.FragmentId + "." + f.OAuth.ClientId)));

    [Fact]
    public async Task One_login_on_a_shell_with_N_fragments_ends_at_returnUrl_with_N_plus_1_token_sets()
    {
        AppDefinition shell = Shell(Orders, Billing);
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([shell]);
        using FragmentBrowser browser = new(gateway, shell);

        IdentityGateway.Answer end = await browser.NavigateAsync("/bff/login?returnUrl=%2Forders%2F42");

        Assert.Equal(302, end.Status);
        Assert.Equal("/orders/42", end.Location);

        // 4 + 3N redirects from /bff/login to returnUrl on TestIdp, the bound behind the cap of five
        // fragments (PlatformPolicy.FragmentsPerShell): 4 + 3 x 2 = 10.
        Assert.Equal(10, browser.Redirects.Count);

        (_, SessionRecord record) = await gateway.Sessions.SingleAsync();
        AuthenticationProperties held = Held.Properties(record);

        // The shell's set under the framework's names, and each fragment's under its suffixed names,
        // recorded to its client, with its ID token dropped.
        Assert.False(string.IsNullOrEmpty(held.GetTokenValue("access_token")));
        Assert.False(string.IsNullOrEmpty(held.GetTokenValue("id_token")));
        foreach (FragmentDefinition fragment in new[] { Orders, Billing })
        {
            Assert.Equal(FragmentSets.SignedIn, FragmentSets.StateOf(held, fragment));
            Assert.Equal(fragment.OAuth.ClientId, held.Items[".Fragment.Client:" + fragment.FragmentId]);
            string[] names = [.. FragmentSets.TokenNames(held, fragment.FragmentId).Order(StringComparer.Ordinal)];
            Assert.Contains("access_token", names);
            Assert.Contains("refresh_token", names);
            Assert.DoesNotContain("id_token", names);
        }

        // Each set is the provider's grant to that set's own client, three in all.
        string[] clients = [.. gateway.Idp.Grants.Where(g => g.GrantType == "authorization_code").Select(g => g.ClientId)];
        Assert.Equal([shell.OAuth.ClientId, Orders.OAuth.ClientId, Billing.OAuth.ClientId], clients);
        string orders = held.Items[FragmentSets.TokenItem("access_token", "orders")]!;
        Assert.Contains(orders, gateway.Idp.IssuedAccessTokens);
        Assert.NotEqual(held.GetTokenValue("access_token"), orders);

        // The fragments' names stay out of .TokenNames, so the handler's StoreTokens on the shell's set
        // leaves them alone.
        Assert.DoesNotContain(held.GetTokens(), t => t.Name.Contains(':', StringComparison.Ordinal));
    }
}
