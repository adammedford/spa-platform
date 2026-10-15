using System.Collections.Immutable;

namespace Platform.Abstractions;

/// <summary>
/// The rule ids a refusal is logged and counted under. Source: contracts/request-gate.md -> Rule
/// ids, which is the normative list; ADR-0015 put them here. Freezes at G1a with that contract.
///
/// Four workstreams need the same strings and no two of them may disagree: workstream 2 refuses a
/// host and a path, workstream 4 the gate, antiforgery and an unknown upstream, workstream 3 a
/// login callback and a session after G1b, and workstream 6 counts them in stage 2 and asserts on
/// them in the negative suite. A-03 stops a workstream project from reading another's constants, so
/// before this type each owner kept a copy and the contract was agreed by hand. The matching test
/// in tests/Platform.Abstractions.Tests reads the contract's own table and fails if the two drift.
///
/// A rule id reaches a log line and <c>security.rejections</c>, never a response body: the
/// <c>code</c> in a problem response says less on purpose. See contracts/telemetry.md -> "How
/// <c>rule</c> and <c>session_ref</c> reach the request line".
/// </summary>
public static class RuleIds
{
    /// <summary>The request authority matches no registry <c>host</c>.</summary>
    public const string HostUnknown = "host.unknown";

    /// <summary>The path failed normalisation.</summary>
    public const string PathInvalid = "path.invalid";

    /// <summary>A reserved path that is switched off.</summary>
    public const string PathReserved = "path.reserved";

    /// <summary>A cookie name imitates a platform cookie, in another letter case or sent twice.</summary>
    public const string GateCookieName = "gate.cookie-name";

    /// <summary>
    /// <c>Sec-Fetch-Site</c> is absent, <c>same-site</c> or <c>cross-site</c> where
    /// <c>same-origin</c> is required.
    /// </summary>
    public const string GateFetchSite = "gate.fetch-site";

    /// <summary><c>Origin</c> is present and is not the app origin.</summary>
    public const string GateOrigin = "gate.origin";

    /// <summary>The path does not accept the method.</summary>
    public const string GateMethod = "gate.method";

    /// <summary><c>/bff/login</c> without <c>Sec-Fetch-Mode: navigate</c> and <c>Sec-Fetch-Dest: document</c>.</summary>
    public const string GateNavigation = "gate.navigation";

    /// <summary>The antiforgery token was missing or wrong.</summary>
    public const string CsrfToken = "csrf.token";

    /// <summary>Any refused login callback.</summary>
    public const string AuthCallback = "auth.callback";

    /// <summary>A cookie that resolves to no session for this app.</summary>
    public const string AuthSession = "auth.session";

    /// <summary>The app lists no upstream under that name.</summary>
    public const string ApiUnknown = "api.unknown";

    /// <summary>
    /// Every id above, for a test or a suite that has to cover the whole set rather than name one.
    /// Workstream 6's negative suite can read this instead of keeping a thirteenth copy.
    /// </summary>
    public static readonly ImmutableArray<string> All =
    [
        HostUnknown,
        PathInvalid,
        PathReserved,
        GateCookieName,
        GateFetchSite,
        GateOrigin,
        GateMethod,
        GateNavigation,
        CsrfToken,
        AuthCallback,
        AuthSession,
        ApiUnknown,
    ];
}
