using System.Reflection;
using Platform.Abstractions;

namespace Platform.Abstractions.Tests;

/// <summary>
/// What <c>Platform.Abstractions</c> exposes, and which parts remain draft.
///
/// This is the project every workstream builds against and it freezes at G1a, so an addition is a
/// contract change and not a convenience. The schema x-freeze annotations in contracts/README.md
/// record gate provenance; <see cref="FreezeAttribute"/> marks code drafts until their gate.
/// </summary>
public sealed class PublicSurfaceTests
{
    /// <summary>
    /// Every public type the contract names, from docs/plan/02-contracts.md -> Interfaces. Adding a
    /// line here is a contract change: the integrator decides it, and a workstream asks for it with
    /// contracts/changes/&lt;task-id&gt;-&lt;slug&gt;.md.
    /// </summary>
    private static readonly string[] Contracted =
    [
        "AccessTokenResult",
        "AccessTokenResult+ProviderUnavailable",
        "AccessTokenResult+SessionEnded",
        "AccessTokenResult+Token",
        "AccessTokenResult+Unauthenticated",
        "ApiDefinition",
        "AppDefinition",
        "ClientCredentialStyle",
        "FreezeAttribute",
        "IAccessTokenSource",
        "IAppFeature",
        "IClientSecretSource",
        "ICsrfTokens",
        "IIdpProfile",
        "IObjectStore",
        "IRegistry",
        "IRequestTelemetryFeature",
        "ISessionStore",
        "IssuerDefinition",
        "OAuthDefinition",
        "PlatformPolicy",
        "RegistrySnapshot",
        "RuleIds",
        "SchemeNames",
        "SessionRecord",
        "SessionRef",
        "StoredObject",
    ];

    /// <summary>
    /// I-6 removes the G1b markers (ADR-0017). Every identity part is prepared for the freeze;
    /// the annotated contracts-v1 tag records closure before implementation can start.
    /// </summary>
    private static readonly string[] DraftUntilG1b = [];

    [Fact]
    public void The_public_surface_is_exactly_what_the_contract_names()
    {
        Assert.Equal(Contracted, PublicTypes().Select(Name).Order().ToArray());
    }

    [Fact]
    public void Everything_public_lives_in_the_one_namespace()
    {
        // A workstream reads one using directive. Nothing hides in a nested namespace.
        Assert.All(PublicTypes(), type => Assert.Equal("Platform.Abstractions", type.Namespace));
    }

    [Fact]
    public void No_part_remains_marked_draft_until_G1b()
    {
        string[] marked =
        [
            .. PublicTypes()
                .Where(type => type.GetCustomAttribute<FreezeAttribute>() is { Gate: "G1b" })
                .Select(Name)
                .Order(),
        ];

        Assert.Equal(DraftUntilG1b, marked);
    }

    [Fact]
    public void Nothing_is_marked_with_a_gate_that_is_not_a_gate()
    {
        // A typo in a marker would quietly exempt a type from the test above.
        Assert.All(
            PublicTypes().Select(type => type.GetCustomAttribute<FreezeAttribute>()).OfType<FreezeAttribute>(),
            freeze => Assert.Contains(freeze.Gate, (string[])["G1a", "G1b"]));
    }

    private static IEnumerable<Type> PublicTypes() =>
        typeof(AppDefinition).Assembly.GetExportedTypes().Where(type => !type.IsCompilerGenerated());

    private static string Name(Type type) =>
        type.DeclaringType is null ? type.Name : type.DeclaringType.Name + "+" + type.Name;
}

internal static class TypeExtensions
{
    public static bool IsCompilerGenerated(this Type type) =>
        type.GetCustomAttributes(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false).Length > 0;
}
