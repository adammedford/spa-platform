using System.Collections.Immutable;
using Platform.Abstractions;

namespace Platform.Abstractions.Tests;

/// <summary>
/// Why <see cref="AppDefinition.Fingerprint"/> exists. docs/plan/02-contracts.md: "Fingerprint
/// decides eviction. Record equality cannot, because immutable collections compare by reference and
/// every reload builds new ones." Task 3.6 evicts on a fingerprint change, so this is the behaviour
/// that rules out the obvious alternative.
/// </summary>
public sealed class AppDefinitionEqualityTests
{
    [Fact]
    public void Two_entries_with_equal_values_but_separately_built_collections_are_not_equal()
    {
        // Same values, same fingerprint, different collection instances. A reload builds new
        // collections every time, so an evict-on-inequality cache would evict every entry on every
        // reload.
        Assert.NotEqual(Entry(), Entry());
    }

    [Theory]
    [InlineData("scopes")]
    [InlineData("csp")]
    [InlineData("apis")]
    public void One_rebuilt_collection_is_enough_to_make_two_entries_unequal(string rebuilt)
    {
        Collections shared = new(Scopes(), Csp(), Apis());
        Collections oneRebuilt = rebuilt switch
        {
            "scopes" => shared with { Scopes = Scopes() },
            "csp" => shared with { Csp = Csp() },
            "apis" => shared with { Apis = Apis() },
            _ => throw new ArgumentOutOfRangeException(nameof(rebuilt), rebuilt, "unknown collection"),
        };

        Assert.NotEqual(Entry(shared), Entry(oneRebuilt));
    }

    [Fact]
    public void Two_entries_sharing_every_collection_are_equal()
    {
        Collections shared = new(Scopes(), Csp(), Apis());

        Assert.Equal(Entry(shared), Entry(shared));
    }

    [Fact]
    public void An_entry_whose_fingerprint_changed_is_not_equal_to_the_old_one()
    {
        Collections shared = new(Scopes(), Csp(), Apis());

        Assert.NotEqual(Entry(shared), Entry(shared) with { Fingerprint = "sha256:other" });
    }

    /// <summary>The three collections an entry holds, so a test can share them or rebuild one.</summary>
    private sealed record Collections(
        ImmutableArray<string> Scopes,
        ImmutableDictionary<string, ImmutableArray<string>> Csp,
        ImmutableArray<ApiDefinition> Apis);

    private static ImmutableArray<string> Scopes() => ["openid", "profile", "orders.read"];

    private static ImmutableDictionary<string, ImmutableArray<string>> Csp() =>
        ImmutableDictionary<string, ImmutableArray<string>>.Empty
            .Add("connect-src", ["https://api.example"]);

    private static ImmutableArray<ApiDefinition> Apis() =>
        [new ApiDefinition("orders", new Uri("https://orders.internal"), TimeSpan.FromSeconds(30))];

    private static AppDefinition Entry() => Entry(new Collections(Scopes(), Csp(), Apis()));

    private static AppDefinition Entry(Collections collections) =>
        new(
            AppId: "app1",
            Host: "app1.spa-poc.test",
            Release: "2026-10-12-abc1234",
            OAuth: new OAuthDefinition(
                Issuer: "primary",
                ClientId: "1example23456789",
                ClientSecretRef: "ssm:/spa-platform/poc/apps/app1/client-secret:1",
                Scopes: collections.Scopes,
                Resource: "https://orders.example"),
            Apis: collections.Apis,
            Csp: collections.Csp,
            SessionLifetime: TimeSpan.FromHours(8),
            Fingerprint: "sha256:abc");
}
