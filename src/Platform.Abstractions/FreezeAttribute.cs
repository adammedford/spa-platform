namespace Platform.Abstractions;

/// <summary>
/// Which gate freezes this part of the contract. The schemas carry the same marker as an
/// <c>x-freeze</c> annotation, so that the gate travels with the thing it governs rather than only
/// with contracts/README.md.
///
/// An unmarked type freezes at G1a; a type marked <c>G1b</c> froze at G1b. The marker records
/// which gate froze the part, not whether it is still draft: G1b closed on 2026-10-16 when task
/// I-6 folded the G0 findings in (ADR-0017) and tagged <c>contracts-v1</c>. Both sets now need a
/// change request to alter.
/// </summary>
[AttributeUsage(AttributeTargets.All, AllowMultiple = false)]
public sealed class FreezeAttribute(string gate) : Attribute
{
    /// <summary>"G1a" or "G1b".</summary>
    public string Gate { get; } = gate;
}
