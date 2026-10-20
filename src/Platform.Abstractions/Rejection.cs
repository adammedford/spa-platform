using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;

namespace Platform.Abstractions;

/// <summary>
/// Ends a request under a rule id: the one place the rule id, the request line's <c>rule</c> and the
/// problem body meet. contracts/request-gate.md -> Rule ids gives each id its <c>code</c>,
/// contracts/http-contract.md -> Errors gives the code its status, and contracts/telemetry.md ->
/// "How <c>rule</c> and <c>session_ref</c> reach the request line" says the rule reaches the line
/// through <see cref="IRequestTelemetryFeature"/>.
///
/// A stage that ends a rejection passes the rule id and nothing else. Before this type every such
/// stage spelled the status and code itself, in four projects that A-03 keeps apart, and the copies
/// had diverged (#142). ADR-0015 decision 1 put the ids in this project; #142 (Q1, Q5) and change
/// request contracts/changes/issue-160-rejection-table.md put the join and its writer beside them.
/// tests/Platform.Abstractions.Tests holds <see cref="Rows"/> to the <c>code</c> column that change
/// request decided for contracts/request-gate.md -> Rule ids.
///
/// What is not behind it, on purpose. The Warning line is the caller's: this never logs, so
/// <c>UsePlatformHostResolution</c> can stay silent and every pinned line keeps its template and
/// category (change request 6.4; #142, Q9). The decision that a request is a rejection is the stage's.
/// No logger, no dependency injection, no state but the two tables.
///
/// At most one <c>End*</c> per request. Nothing enforces it, for the reason
/// <see cref="IRequestTelemetryFeature.Rule"/> gives: a guard that threw would turn a mistake into a
/// 500 on whichever request tripped it.
/// </summary>
public static class Rejection
{
    /// <summary>
    /// The <c>code</c> column of contracts/request-gate.md -> Rule ids, as (rule, code): every id but
    /// <see cref="RuleIds.AuthCallback"/>, which ends on the static failure page rather than a
    /// problem body. <see cref="RuleIds.AuthSession"/> carries the fetch's <c>unauthenticated</c>.
    /// Order is not part of the interface: <see cref="RuleIds.All"/> fixes the order consumers see.
    /// </summary>
    public static readonly ImmutableArray<(string Rule, string Code)> Rows =
    [
        (RuleIds.HostUnknown, Problems.NotFound),
        (RuleIds.PathInvalid, Problems.InvalidRequest),
        (RuleIds.PathReserved, Problems.NotFound),
        (RuleIds.GateCookieName, Problems.InvalidRequest),
        (RuleIds.GateFetchSite, Problems.OriginRejected),
        (RuleIds.GateOrigin, Problems.OriginRejected),
        (RuleIds.GateMethod, Problems.MethodNotAllowed),
        (RuleIds.GateNavigation, Problems.OriginRejected),
        (RuleIds.CsrfToken, Problems.CsrfFailed),
        (RuleIds.AuthSession, Problems.Unauthenticated),
        (RuleIds.ApiUnknown, Problems.UnknownApi),
    ];

    /// <summary>
    /// Notes the rule on <see cref="IRequestTelemetryFeature"/>, null-conditionally, and writes no
    /// response. For a rejection whose response another writer ends: the shell gate's challenge, the
    /// <c>/bff/session</c> 401, and the callback failure page (#142, Q11). With no feature installed,
    /// as in a stage's own test project, it costs nothing.
    /// </summary>
    /// <exception cref="ArgumentException">The id is not in <see cref="RuleIds.All"/>.</exception>
    public static void Note(HttpContext context, string rule)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireListed(rule);

        context.Features.Get<IRequestTelemetryFeature>()?.Rule = rule;
    }

    /// <summary>
    /// Ends the request under a rule id: <see cref="Note"/>, then the problem body of the id's row.
    /// The feature is written before the body, so that a stage host recording the feature never
    /// races the body back to the client (change request issue-145, section 3).
    /// </summary>
    /// <exception cref="ArgumentException">The id is not in <see cref="RuleIds.All"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The id is <see cref="RuleIds.GateMethod"/>, which ends only through
    /// <see cref="EndMethodAsync"/>, or <see cref="RuleIds.AuthCallback"/>, whose page is the OpenID
    /// Connect remote-failure event's. Both are deterministic per call site, so the stage's own tests
    /// find the wrong call before a client does (#142, Q13).
    /// </exception>
    public static Task EndAsync(HttpContext context, string rule)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireListed(rule);

        if (rule == RuleIds.GateMethod)
        {
            throw new InvalidOperationException(
                $"'{RuleIds.GateMethod}' ends through {nameof(Rejection)}.{nameof(EndMethodAsync)}, which sets the Allow header a 405 must carry.");
        }

        if (rule == RuleIds.AuthCallback)
        {
            throw new InvalidOperationException(
                $"'{RuleIds.AuthCallback}' ends on no problem body: the OpenID Connect remote-failure event writes the static failure page. Use {nameof(Rejection)}.{nameof(Note)} there.");
        }

        return End(context, rule);
    }

    /// <summary>
    /// Ends the request under <see cref="RuleIds.GateMethod"/> with <c>Allow</c> set to the methods
    /// the path accepts, joined. <c>Allow</c> is the caller's knowledge (the request gate names the
    /// path's methods, the method allowlist all seven, the asset endpoint <c>GET, HEAD</c>), and this
    /// is the one entry through which this type ends a 405: <see cref="EndAsync"/> refuses
    /// <see cref="RuleIds.GateMethod"/>, so a 405 this type writes cannot leave without the header RFC
    /// 9110 section 15.5.6 requires. The guarantee is this type's, not the problem writer's:
    /// <see cref="Problems.WriteAsync"/> given <see cref="Problems.MethodNotAllowed"/> writes a 405 with
    /// no <c>Allow</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// No method is given, or one is empty, before anything is written.
    /// </exception>
    public static Task EndMethodAsync(HttpContext context, params string[] allowed)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(allowed);

        if (allowed.Length == 0 || allowed.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A 405 names at least one method, and no empty one, in Allow.", nameof(allowed));
        }

        context.Response.Headers.Allow = string.Join(", ", allowed);
        return End(context, RuleIds.GateMethod);
    }

    private static Task End(HttpContext context, string rule)
    {
        string code = CodeOf(rule);
        Note(context, rule);
        return Problems.WriteAsync(context, code);
    }

    private static string CodeOf(string rule)
    {
        foreach ((string row, string code) in Rows)
        {
            if (string.Equals(row, rule, StringComparison.Ordinal))
            {
                return code;
            }
        }

        // Unreachable for a listed id other than auth.callback, which EndAsync refuses first; the
        // table test proves every other id has a row.
        throw new InvalidOperationException($"'{rule}' has no row in {nameof(Rejection)}.{nameof(Rows)}.");
    }

    private static void RequireListed(string rule)
    {
        if (!RuleIds.All.Contains(rule, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"'{rule}' is not a rule id in contracts/request-gate.md -> Rule ids.", nameof(rule));
        }
    }
}
