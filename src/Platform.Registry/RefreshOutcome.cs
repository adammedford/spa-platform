namespace Platform.Registry;

/// <summary>What one poll of the registry object did. Only <see cref="Swapped"/> changed the live snapshot.</summary>
public enum RefreshOutcome
{
    /// <summary>A new snapshot is live, and subscribers have been told.</summary>
    Swapped,

    /// <summary>The object's ETag still matches what was last read: no body, no validation, no preload.</summary>
    NotModified,

    /// <summary>The registry object does not exist. The live snapshot, if any, stays.</summary>
    Missing,

    /// <summary>The document failed validation. The live snapshot stays, and this ETag is not read again.</summary>
    Rejected,

    /// <summary>
    /// The document is valid but a client secret it names could not be read: with a live snapshot the
    /// document is rejected whole, and at a cold start no app could go live. Read again on the next poll.
    /// </summary>
    SecretsUnreadable,

    /// <summary>The store or the secret source failed. The live snapshot stays.</summary>
    Failed,
}
