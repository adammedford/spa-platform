using System.Reflection;
using Platform.Abstractions;

namespace Platform.Abstractions.Tests;

/// <summary>
/// <see cref="IRefreshLeases"/> and <see cref="RefreshLease"/> are frozen on landing (change request
/// P2-3 Decision items 1 and 6; ADR-0024 decision 2), so their exact shape is asserted here and not
/// only their names, which is all <see cref="PublicSurfaceTests"/> can see.
/// </summary>
public sealed class RefreshLeaseTests
{
    [Fact]
    public void A_lease_carries_a_token_and_an_expiry_and_nothing_else()
    {
        // By construction: a positional record's ToString() prints every public member, and the
        // session key is on the never-logged list (N-16), so the record must have no member that
        // could hold it. Mutation: add a SessionKey parameter to the record, and this fails.
        string[] members =
        [
            .. typeof(RefreshLease)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(["ExpiresAt", "Token"], members);
        Assert.Equal(
            ["Token", "ExpiresAt"],
            typeof(RefreshLease).GetConstructors().Single().GetParameters().Select(parameter => parameter.Name));
    }

    [Fact]
    public void A_lease_prints_its_token_and_expiry_and_no_session_key()
    {
        // The literal is the compiler's record format, written out by hand, so an override of
        // ToString or PrintMembers that added a member (or a key from anywhere) shows here.
        // Mutation: give the record a SessionKey member, and the literal no longer matches.
        RefreshLease lease = new("lease-token", new DateTimeOffset(2026, 10, 12, 9, 0, 21, TimeSpan.Zero));

        Assert.Equal(
            $"RefreshLease {{ Token = lease-token, ExpiresAt = {lease.ExpiresAt} }}",
            lease.ToString());
    }

    [Fact]
    public void The_lease_interface_has_exactly_an_acquire_and_a_release()
    {
        // No extend, no renew, no "is held" query (P2-3 Decision item 1; ADR-0024 → Alternatives
        // rejected). Mutation: add a RenewAsync member, and this fails.
        string[] signatures =
        [
            .. typeof(IRefreshLeases)
                .GetMethods()
                .Select(method =>
                    $"{Display(method.ReturnType)} {method.Name}(" +
                    string.Join(", ", method.GetParameters().Select(p => $"{Display(p.ParameterType)} {p.Name}")) +
                    ")")
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(
            [
                "Task ReleaseAsync(String sessionKey, RefreshLease lease, CancellationToken ct)",
                "Task<RefreshLease> TryAcquireAsync(String sessionKey, TimeSpan ttl, CancellationToken ct)",
            ],
            signatures);
    }

    [Fact]
    public void An_acquire_answers_a_nullable_lease()
    {
        // Null is how a held key answers; a non-nullable return would force a sentinel lease.
        MethodInfo acquire = typeof(IRefreshLeases).GetMethod(nameof(IRefreshLeases.TryAcquireAsync))!;

        NullabilityInfo result = new NullabilityInfoContext().Create(acquire.ReturnParameter);

        Assert.Equal(NullabilityState.Nullable, result.GenericTypeArguments.Single().ReadState);
    }

    private static string Display(Type type) =>
        type.IsGenericType
            ? $"{type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)]}<{string.Join(", ", type.GetGenericArguments().Select(Display))}>"
            : type.Name;
}
