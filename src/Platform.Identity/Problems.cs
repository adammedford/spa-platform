using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Platform.Identity;

/// <summary>
/// The error bodies in <c>contracts/http-contract.md</c> -> Errors: <c>application/problem+json</c>
/// carrying a stable <c>code</c> and nothing that describes the request. Workstream 3's own writer,
/// because a workstream project references no other workstream's (A-03); it writes the same two
/// fields the security and proxy writers do.
/// </summary>
internal static class Problems
{
    /// <summary>401: no session (contracts/http-contract.md -> Errors).</summary>
    internal const string Unauthenticated = "unauthenticated";

    internal static Task WriteAsync(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsync(
            $"{{\"status\":{status},\"code\":\"{JsonEncodedText.Encode(code)}\"}}", context.RequestAborted);
    }
}
