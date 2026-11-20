using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// A fragment's scheme and callback (#994; the fragments spec #961 -> "Schemes and callbacks"): the
/// scheme provider lists the shell's scheme, then each fragment's in registry order; each fragment's
/// options sign in to the shell's cookie scheme on its own callback; and a change to any part of the
/// entry evicts every scheme of it.
/// </summary>
public sealed class FragmentSchemeTests
{
    private static readonly FragmentDefinition Orders = FragmentChainTests.Orders;
    private static readonly FragmentDefinition Billing = FragmentChainTests.Billing;

    [Fact]
    public async Task The_shell_scheme_comes_first_then_each_fragment_scheme_in_registry_order_each_on_its_own_callback()
    {
        // Registry order is billing, then orders: not the ordinal order, so a sort would show.
        AppDefinition shell = FragmentChainTests.Shell(Billing, Orders);
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([shell]);
        gateway.Pin(shell);
        IAuthenticationSchemeProvider schemes = gateway.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        string[] handlers = [.. (await schemes.GetRequestHandlerSchemesAsync()).Select(s => s.Name)];
        Assert.Equal(["oidc:shell", "oidc:shell:billing", "oidc:shell:orders"], handlers);
        Assert.Equal(["cookie:shell", "oidc:shell", "oidc:shell:billing", "oidc:shell:orders"],
            (await schemes.GetAllSchemesAsync()).Select(s => s.Name));
        Assert.NotNull(await schemes.GetSchemeAsync("oidc:shell:orders"));
        Assert.Null(await schemes.GetSchemeAsync("oidc:shell:unknown"));
        Assert.Equal("oidc:shell", (await schemes.GetDefaultChallengeSchemeAsync())!.Name);

        foreach (FragmentDefinition fragment in new[] { Orders, Billing })
        {
            OpenIdConnectOptions options = Oidc(gateway, SchemeNames.Oidc(shell.AppId, fragment.FragmentId));
            Assert.Equal(fragment.OAuth.ClientId, options.ClientId);
            Assert.Equal(IdentityGateway.SecretOf(fragment), options.ClientSecret);
            Assert.Equal("cookie:shell", options.SignInScheme);
            Assert.Equal("/signin-oidc/" + fragment.FragmentId, options.CallbackPath.Value);
            Assert.False(options.SignedOutCallbackPath.HasValue);
            Assert.False(options.SkipUnrecognizedRequests);
        }

        // The shell's own scheme keeps the one callback every app has.
        OpenIdConnectOptions own = Oidc(gateway, SchemeNames.Oidc(shell.AppId));
        Assert.Equal(shell.OAuth.ClientId, own.ClientId);
        Assert.Equal("/signin-oidc", own.CallbackPath.Value);
        Assert.Equal("/signout-callback-oidc", own.SignedOutCallbackPath.Value);
    }

    [Fact]
    public async Task An_app_with_no_fragments_has_only_its_pair()
    {
        AppDefinition plain = TestApps.Entry("plain", issuer: IdentityGateway.IssuerName);
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([plain]);
        gateway.Pin(plain);
        IAuthenticationSchemeProvider schemes = gateway.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.Equal(["oidc:plain"], (await schemes.GetRequestHandlerSchemesAsync()).Select(s => s.Name));
        Assert.Equal(["cookie:plain", "oidc:plain"], (await schemes.GetAllSchemesAsync()).Select(s => s.Name));
    }

    [Fact]
    public async Task A_change_to_any_part_of_an_entry_evicts_all_its_schemes_and_a_new_fragment_client_is_configured_as_new()
    {
        AppDefinition shell = FragmentChainTests.Shell(Orders, Billing);
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([shell]);
        gateway.Pin(shell);
        OpenIdConnectOptions shellBefore = Oidc(gateway, "oidc:shell");
        OpenIdConnectOptions ordersBefore = Oidc(gateway, "oidc:shell:orders");
        OpenIdConnectOptions billingBefore = Oidc(gateway, "oidc:shell:billing");

        // Only the billing fragment changes, onto a new client; the entry's fingerprint changes with it.
        FragmentDefinition moved = TestApps.Fragment("billing", "shell", clientId: "client-shell-billing-2",
            clientSecretRef: "ssm:/spa-platform/poc/apps/shell/fragments/billing/client-secret:2");
        AppDefinition next = FragmentChainTests.Shell(Orders, moved);
        Assert.NotEqual(shell.Fingerprint, next.Fingerprint);
        await gateway.SwapAsync(next);
        gateway.Pin(next);

        // Every scheme of the entry is rebuilt, the fragments' included: no per-scheme eviction.
        Assert.NotSame(shellBefore, Oidc(gateway, "oidc:shell"));
        Assert.NotSame(ordersBefore, Oidc(gateway, "oidc:shell:orders"));
        OpenIdConnectOptions billingAfter = Oidc(gateway, "oidc:shell:billing");
        Assert.NotSame(billingBefore, billingAfter);
        Assert.Equal("client-shell-billing-2", billingAfter.ClientId);
        Assert.Equal(IdentityGateway.SecretOf(moved), billingAfter.ClientSecret);

        // An entry left as it was keeps its options, the fragments' included.
        OpenIdConnectOptions kept = Oidc(gateway, "oidc:shell:orders");
        await gateway.SwapAsync(next);
        gateway.Pin(next);
        Assert.Same(kept, Oidc(gateway, "oidc:shell:orders"));
    }

    [Fact]
    public async Task A_fragment_removed_from_its_entry_has_no_scheme_and_its_options_are_refused()
    {
        AppDefinition shell = FragmentChainTests.Shell(Orders, Billing);
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([shell]);
        gateway.Pin(shell);
        _ = Oidc(gateway, "oidc:shell:billing");

        AppDefinition next = FragmentChainTests.Shell(Orders);
        await gateway.SwapAsync(next);
        gateway.Pin(next);

        Assert.Null(await gateway.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync("oidc:shell:billing"));
        Assert.Throws<OptionsValidationException>(() => Oidc(gateway, "oidc:shell:billing"));
    }

    [Fact]
    public async Task A_chain_hop_sends_prompt_none_on_the_standard_profile_and_the_fallback_does_not()
    {
        AppDefinition shell = FragmentChainTests.Shell(Orders);
        await using IdentityGateway gateway = await IdentityGateway.StartAsync([shell], stubProfile: false);
        using FragmentBrowser browser = new(gateway, shell);
        List<Uri> authorizes = [];
        browser.BeforeAuthorize = uri =>
        {
            authorizes.Add(uri);
            return Task.CompletedTask;
        };

        await browser.NavigateAsync("/bff/login");
        await browser.NavigateAsync("/bff/login?fragment=orders");

        Dictionary<string, string>[] queries = [.. authorizes.Select(Query)];
        Assert.Equal([shell.OAuth.ClientId, Orders.OAuth.ClientId, Orders.OAuth.ClientId], queries.Select(q => q["client_id"]));
        Assert.False(queries[0].ContainsKey("prompt"));
        Assert.Equal("none", queries[1]["prompt"]);
        Assert.False(queries[2].ContainsKey("prompt"));
        Assert.Equal("https://shell.spa-poc.test/signin-oidc/orders", queries[1]["redirect_uri"]);
    }

    internal static Dictionary<string, string> Query(Uri uri) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query)
            .ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);

    private static OpenIdConnectOptions Oidc(IdentityGateway gateway, string name) =>
        gateway.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(name);
}
