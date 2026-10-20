using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Platform.Abstractions;

/// <summary>
/// The problem body: contracts/http-contract.md -> Errors, which is the normative table. An error the
/// gateway answers is <c>application/problem+json</c> carrying a status and a stable <c>code</c>, and
/// nothing that describes the request. Which rule fired is in the log line and in
/// <c>security.rejections</c>, never here.
///
/// It lives here because four workstreams write the same two fields and A-03 keeps them from sharing
/// a writer of their own: before this type each of Platform.Hosting, Platform.Security,
/// Platform.Proxy and Platform.Identity kept a copy. ADR-0015 decision 1 put a value four workstreams
/// must spell identically in this project; #142 (Q1, Q15, Q16) extends that to this writer and its
/// codes, and change request contracts/changes/issue-160-rejection-table.md records the decision.
/// tests/Platform.Abstractions.Tests reads the Errors table and fails if <see cref="Rows"/> drifts
/// from it.
///
/// A caller names the code alone. Every code in the table has exactly one status, so code to status
/// is a function and a code cannot be written with the wrong status (#142, Q7). The day the contract
/// lists one code under two statuses, the table test fails and this question reopens.
///
/// <see cref="Rejection"/> ends a request under a rule id through this writer; an answer, a response
/// under no rule id, calls <see cref="WriteAsync"/> directly.
/// </summary>
public static class Problems
{
    /// <summary>400. The path failed normalisation, or a cookie name imitates a platform cookie.</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>401. No session.</summary>
    public const string Unauthenticated = "unauthenticated";

    /// <summary>401. The refresh token was refused and the session was removed.</summary>
    public const string SessionEnded = "session_ended";

    /// <summary>403. The request gate refused the request.</summary>
    public const string OriginRejected = "origin_rejected";

    /// <summary>403. The antiforgery token was missing or wrong.</summary>
    public const string CsrfFailed = "csrf_failed";

    /// <summary>404. Unknown host, missing asset, or a reserved path that is switched off.</summary>
    public const string NotFound = "not_found";

    /// <summary>404. The app lists no upstream under that name.</summary>
    public const string UnknownApi = "unknown_api";

    /// <summary>405. The path does not accept the method.</summary>
    public const string MethodNotAllowed = "method_not_allowed";

    /// <summary>502. The upstream could not be reached.</summary>
    public const string UpstreamUnavailable = "upstream_unavailable";

    /// <summary>503. The identity provider could not be reached to refresh the token; the session is kept.</summary>
    public const string IdpUnavailable = "idp_unavailable";

    /// <summary>504. The upstream exceeded the entry's timeout.</summary>
    public const string UpstreamTimeout = "upstream_timeout";

    /// <summary>
    /// The Errors table's rows, as (status, code). A tuple array, so that no nested public type
    /// reaches the contracted surface. Order is not part of the interface: nothing walks it in order,
    /// unlike <see cref="RuleIds.All"/>, so the table test asserts it as a set.
    /// </summary>
    public static readonly ImmutableArray<(int Status, string Code)> Rows =
    [
        (StatusCodes.Status400BadRequest, InvalidRequest),
        (StatusCodes.Status401Unauthorized, Unauthenticated),
        (StatusCodes.Status401Unauthorized, SessionEnded),
        (StatusCodes.Status403Forbidden, OriginRejected),
        (StatusCodes.Status403Forbidden, CsrfFailed),
        (StatusCodes.Status404NotFound, NotFound),
        (StatusCodes.Status404NotFound, UnknownApi),
        (StatusCodes.Status405MethodNotAllowed, MethodNotAllowed),
        (StatusCodes.Status502BadGateway, UpstreamUnavailable),
        (StatusCodes.Status503ServiceUnavailable, IdpUnavailable),
        (StatusCodes.Status504GatewayTimeout, UpstreamTimeout),
    ];

    /// <summary>The status the Errors table gives a code.</summary>
    /// <exception cref="ArgumentException">The code is not in <see cref="Rows"/>.</exception>
    public static int StatusOf(string code)
    {
        foreach ((int status, string row) in Rows)
        {
            if (string.Equals(row, code, StringComparison.Ordinal))
            {
                return status;
            }
        }

        throw new ArgumentException(
            $"'{code}' is not a code in contracts/http-contract.md -> Errors.", nameof(code));
    }

    /// <summary>
    /// Writes the problem body for a code: the status from the Errors table,
    /// <c>Content-Type: application/problem+json</c>, and a body whose properties are exactly
    /// <c>status</c> and <c>code</c>. Cancelled on the request's abort token. Never logs, and nothing
    /// from the request reaches the body.
    ///
    /// The response must not have started, since setting the status would then throw. That is the
    /// caller's invariant, guarded where only it can know it: the upstream-failure writer checks it
    /// before writing.
    /// </summary>
    /// <exception cref="ArgumentException">The code is not in <see cref="Rows"/>.</exception>
    public static Task WriteAsync(HttpContext context, string code)
    {
        ArgumentNullException.ThrowIfNull(context);
        int status = StatusOf(code);

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";

        // Written by hand rather than serialized, as all four copies this replaces were: two fields,
        // one of them a code from the table, and a reflection-based serializer in the gateway image is
        // a cost with nothing to buy. The code is JSON-encoded all the same.
        return context.Response.WriteAsync(
            $"{{\"status\":{status},\"code\":\"{JsonEncodedText.Encode(code)}\"}}", context.RequestAborted);
    }
}
