namespace Platform.Identity;

/// <summary>
/// The key of the session this request's cookie resolved to, left on the request by
/// <see cref="AppBoundTicketStore"/> when it hands back a session this app owns. The token source of
/// task 3.8 reads it so that it can re-read and rewrite the same record under that session's gate:
/// the cookie handler keeps the key to itself once the stored ticket has replaced the cookie's.
///
/// Internal and never logged: a session key is on contracts/telemetry.md's never-logged list, and only
/// <see cref="Abstractions.SessionRef.Of"/>'s result may be.
/// </summary>
internal sealed class SessionKeyFeature(string key)
{
    public string Key { get; } = key;

    /// <summary>Kept out of any string a log or an exception could carry.</summary>
    public override string ToString() => nameof(SessionKeyFeature);
}
