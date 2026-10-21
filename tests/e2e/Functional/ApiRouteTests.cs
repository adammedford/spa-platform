using System.Text.Json;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Functional;

/// <summary>
/// F-08 in a real browser (docs/plan/04-tests.md, tier D): each mock API route called through the
/// gateway by the app's own page script, from both apps. The mock API is the only observer that can
/// tell one app's token from another's, so what it reports is what the row asserts.
/// </summary>
public sealed class ApiRouteTests
{
    /// <summary>Each app and the listener that admits its client, as the cell sets them up.</summary>
    public static TheoryData<string, string> OwnListener => new()
    {
        { "app1", "orders" },
        { "app2", "reports" },
    };

    [Theory]
    [MemberData(nameof(OwnListener))]
    public async Task F08_whoami_reports_the_apps_own_client_scopes_and_bound_audience_and_admin_is_403(string appId, string api)
    {
        // What the app asked for, from its definition: the read scope of the API, and the resource it
        // binds its token to, which app 1 names and app 2 does not.
        System.Text.Json.Nodes.JsonObject oauth = FixtureFiles.AppDefinition(appId)["oauth"]!.AsObject();
        string scope = Assert.Single(oauth["scopes"]!.AsArray().Select(node => node!.GetValue<string>()),
            candidate => candidate.EndsWith("/read", StringComparison.Ordinal) && candidate.Contains(api, StringComparison.Ordinal));
        string? audience = oauth["resource"]?.GetValue<string>();

        await using IBrowserStack stack = await Stacks.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        await profile.SignInAsync(stack.App(appId));

        FetchResult whoami = await profile.FetchAsync($"/api/{api}/whoami");
        FetchResult admin = await profile.FetchAsync($"/api/{api}/admin");

        Assert.Equal(200, whoami.Status);
        using (JsonDocument body = JsonDocument.Parse(whoami.Body))
        {
            JsonElement root = body.RootElement;
            Assert.Equal(api, root.GetProperty("api").GetString());
            Assert.Equal(stack.App(appId).ClientId, root.GetProperty("clientId").GetString());
            Assert.Contains(scope, root.GetProperty("scopes").EnumerateArray().Select(element => element.GetString()));
            Assert.Equal(audience, root.GetProperty("audience").GetString());
        }

        // The listener admits this client, so the 403 is for want of the admin scope no app asks for.
        Assert.Equal(403, admin.Status); // Not an Errors row: the mock API's own answer, passed back.
        using (JsonDocument body = JsonDocument.Parse(admin.Body))
        {
            Assert.Equal("insufficient_scope", body.RootElement.GetProperty("error").GetString());
        }
    }
}
