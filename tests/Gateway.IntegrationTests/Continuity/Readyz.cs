using System.Net;
using System.Text.Json;
using Gateway.TestHarness;

namespace Gateway.IntegrationTests.Continuity;

/// <summary>The two members of a <c>/readyz</c> 200 body the continuity rows read (contracts/pipeline.md, Health endpoints).</summary>
internal static class Readyz
{
    public static string? Instance(BrowserResponse response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(response.Body);
        return body.RootElement.GetProperty("instance").GetString();
    }
}
