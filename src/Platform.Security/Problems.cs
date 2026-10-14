using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Platform.Security;

/// <summary>
/// The error bodies in <c>contracts/http-contract.md</c> -> Errors: <c>application/problem+json</c>
/// carrying a stable <c>code</c> and nothing that describes the request. Which rule fired is in the
/// log line.
///
/// <c>Platform.Proxy</c> has its own copy writing the same two fields, because check A-03 keeps a
/// workstream project from referencing another one. Every stage this project owns writes through
/// this one.
/// </summary>
internal static class Problems
{
    internal static Task WriteAsync(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";

        // Written by hand rather than serialized, as the proxy's copy is: two fields, one of them a
        // literal from the contract's table.
        return context.Response.WriteAsync(
            $"{{\"status\":{status},\"code\":\"{JsonEncodedText.Encode(code)}\"}}", context.RequestAborted);
    }
}
