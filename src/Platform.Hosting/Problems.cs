using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Platform.Hosting;

/// <summary>
/// The error bodies in <c>contracts/http-contract.md</c> -> Errors: <c>application/problem+json</c>
/// carrying a stable <c>code</c> and nothing that describes the request.
///
/// <c>Platform.Security</c> and <c>Platform.Proxy</c> each have their own copy writing the same two
/// fields, because check A-03 keeps a workstream project from referencing another one. Every stage
/// this project owns writes through this one; task 2.4's missing asset will too.
/// </summary>
internal static class Problems
{
    public const string NotFound = "not_found";

    public static Task WriteAsync(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";

        return context.Response.WriteAsync(
            $"{{\"status\":{status},\"code\":\"{JsonEncodedText.Encode(code)}\"}}", context.RequestAborted);
    }
}
