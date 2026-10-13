namespace Platform.Abstractions;

/// <summary>
/// Which gate freezes this part of the contract. The schemas carry the same marker as an
/// <c>x-freeze</c> annotation, so that the gate travels with the thing it governs rather than only
/// with contracts/README.md.
///
/// An unmarked type freezes at G1a. A type marked <c>G1b</c> is draft: it may change without a
/// change request until task I-6 folds the G0 findings in and tags <c>contracts-v1</c>, and nothing
/// in <c>src/Platform.Identity</c> starts before that tag.
/// </summary>
[AttributeUsage(AttributeTargets.All, AllowMultiple = false)]
public sealed class FreezeAttribute(string gate) : Attribute
{
    /// <summary>"G1a" or "G1b".</summary>
    public string Gate { get; } = gate;
}
