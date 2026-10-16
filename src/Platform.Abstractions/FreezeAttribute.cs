namespace Platform.Abstractions;

/// <summary>
/// Marks a draft part with its freeze gate. Schemas use <c>x-freeze</c> annotations for the
/// same draft set, retaining their frozen status after code draft markers are removed.
///
/// A marked type is draft until its gate is tagged; an unmarked type is frozen. At I-6 the
/// G1b markers are removed under the freeze decision (ADR-0017). The annotated contracts-v1
/// tag on main records that freeze; identity work stays blocked until it is reachable from main.
/// </summary>
[AttributeUsage(AttributeTargets.All, AllowMultiple = false)]
public sealed class FreezeAttribute(string gate) : Attribute
{
    /// <summary>"G1a" or "G1b".</summary>
    public string Gate { get; } = gate;
}
