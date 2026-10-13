using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Platform.Proxy;

/// <summary>
/// The error bodies in <c>contracts/http-contract.md</c>: <c>application/problem+json</c> carrying a
/// stable <c>code</c> and nothing that describes the request. Which rule fired is in the log line.
///
/// Task 4.6 owns problem-details errors for the gateway as a whole. This is the proxy's own writer
/// until then, and it writes the same two fields.
/// </summary>
internal static class Problems
{
    internal static Task WriteAsync(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";

        // Written by hand rather than serialized: the body is two fields, one of them a literal from
        // the contract's table, and a reflection-based serializer in the gateway image is a cost
        // with nothing to buy.
        return context.Response.WriteAsync(
            $"{{\"status\":{status},\"code\":\"{JsonEncodedText.Encode(code)}\"}}", context.RequestAborted);
    }
}
