using System.Reflection;
using Platform.Abstractions;

namespace Platform.Abstractions.Tests;

/// <summary>
/// What <c>Platform.Abstractions</c> exposes, and which parts of it are still draft.
///
/// This is the project every workstream builds against and it freezes at G1a, so an addition is a
/// contract change and not a convenience. contracts/README.md says of the schemas that "every draft
/// subschema carries an x-freeze annotation naming the gate, so the marker travels with the schema
/// rather than only with this page". <see cref="FreezeAttribute"/> is the same idea for the code.
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
        "AccessTokenResult+SessionEnded",
        "AccessTokenResult+Token",
        "AccessTokenResult+Unauthenticated",
        "ApiDefinition",
        "AppDefinition",
        "FreezeAttribute",
        "IAccessTokenSource",
        "IAppFeature",
        "IClientSecretSource",
        "ICsrfTokens",
        "IIdpProfile",
        "IObjectStore",
        "IRegistry",
        "ISessionStore",
        "IssuerDefinition",
        "OAuthDefinition",
        "PlatformPolicy",
        "RegistrySnapshot",
        "SchemeNames",
        "SessionRecord",
        "StoredObject",
    ];

    /// <summary>
    /// The parts that wait for G0. contracts/README.md -> "What is draft until G1b": scheme names,
    /// the session record, IAccessTokenSource and IIdpProfile. Task I-6 folds the G0 findings in,
    /// removes these markers and tags contracts-v1.
    /// </summary>
    private static readonly string[] DraftUntilG1b =
    [
        "IAccessTokenSource",
        "IIdpProfile",
        "SchemeNames",
        "SessionRecord",
    ];

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
    public void The_parts_marked_draft_are_exactly_the_ones_waiting_for_G1b()
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
