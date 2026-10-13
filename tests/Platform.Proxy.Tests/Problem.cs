using System.Net.Http.Json;

namespace Platform.Proxy.Tests;

/// <summary>
/// An error body as <c>contracts/http-contract.md</c> gives it: <c>application/problem+json</c> with
/// a status and a stable code, and nothing else to read.
/// </summary>
internal sealed record Problem(int Status, string Code)
{
    /// <summary>The code in a response's problem body, or null when it carries none.</summary>
    internal static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Problem>())?.Code;
}
