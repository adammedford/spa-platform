using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Platform.Abstractions;

namespace Gateway.E2ETests.Negative;

/// <summary>
/// N-10 and N-17 in a real browser (docs/plan/04-tests.md, tier D), from the app's own page with a live
/// session, on the stack <see cref="StackSwitch"/> chooses.
/// </summary>
public sealed class ApiTests
{
    /// <summary>
    /// Paths built to leave <c>/api/orders</c> for another upstream the app lists, written with the
    /// target's name as <c>{api}</c>. Only escapes a browser sends as written: it resolves <c>..</c> and
    /// <c>%2e%2e</c> segments and turns a backslash into a slash before the request leaves
    /// (https://url.spec.whatwg.org/#double-dot-path-segment), which the hermetic suite's raw socket
    /// covers. An encoded slash survives. An encoded dot inside a segment does too, but the local load
    /// balancer forwards through <see cref="Uri"/>, which unescapes it, so that variant stays with the
    /// raw socket rather than being sent here as another path.
    /// </summary>
    public static TheoryData<string> EscapePaths => new()
    {
        "/api/orders/..%2F{api}/whoami",
        "/api/orders%2F..%2F{api}/whoami",
    };

    [Fact]
    public async Task N10_an_api_name_the_app_does_not_list_is_404_unknown_api_and_no_upstream_sees_it()
    {
        await using IBrowserStack stack = await Stacks.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        await profile.SignInAsync(app);

        LogMark mark = stack.MarkLog();
        FetchResult unlisted = await profile.FetchAsync("/api/unlisted/whoami");

        Contract.ErrorRow expected = Contract.Error("unknown_api");
        Assert.Equal(expected.Status, unlisted.Status);
        Assert.Equal(expected.Code, unlisted.Code);
        await stack.AssertRuleLoggedAsync(mark, RuleIds.ApiUnknown, app.AppId);
        if (stack is LocalStack local)
        {
            Assert.Empty(local.Harness.Echo.Requests); // In H the echo upstream records every request it gets.
        }
    }

    [Theory]
    [MemberData(nameof(EscapePaths))]
    public async Task N10_a_path_built_to_escape_the_prefix_is_400_path_invalid_and_no_upstream_sees_it(string template)
    {
        await using IBrowserStack stack = await Stacks.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        await profile.SignInAsync(app);

        // Locally the target is the harness's echo upstream, which records what reaches it. The cell has
        // none, so there the target is app 1's second API, which answers app 1's whoami 403 (N-17): a
        // path that escaped would come back 403, not 400.
        LocalStack? local = stack as LocalStack;
        string target = local?.Harness.Options.EchoApi ?? SecondApi("app1");
        string path = template.Replace("{api}", target, StringComparison.Ordinal);

        LogMark mark = stack.MarkLog();
        FetchResult escaped = await profile.FetchAsync(path);

        // Sent as written: the browser did not resolve it into something else first.
        Assert.Contains(await stack.ExchangesAsync(), exchange => exchange.Url.PathAndQuery == path);

        // Each path carries an encoded slash, which the request gate refuses before routing as
        // path.invalid (contracts/request-gate.md; the hermetic UnknownApiTests send the same through a
        // raw socket). The row allows 400 or 404 for an escape; these are the gate's 400, under its rule.
        Contract.ErrorRow expected = Contract.Error("invalid_request");
        Assert.Equal(expected.Status, escaped.Status);
        Assert.Equal(expected.Code, escaped.Code);
        await stack.AssertRuleLoggedAsync(mark, RuleIds.PathInvalid, app.AppId);

        // Control: the target is reachable by its own name, so what the escape got means something.
        FetchResult direct = await profile.FetchAsync($"/api/{target}/whoami");
        if (local is not null)
        {
            Assert.Equal(200, direct.Status);
            Assert.Single(local.Harness.Echo.Requests); // Only the direct call reached the echo upstream.
        }
        else
        {
            Assert.Equal(403, direct.Status); // Not an Errors row: the mock API's own answer, passed back.
            Assert.Contains("\"error\":\"client_not_admitted\"", direct.Body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task N17_each_apps_token_is_refused_by_the_api_it_was_not_issued_for()
    {
        await using IBrowserStack stack = await Stacks.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        string app1Second = SecondApi("app1"), app2Second = SecondApi("app2");

        await profile.SignInAsync(stack.App("app1"));
        FetchResult app1OnReports = await profile.FetchAsync($"/api/{app1Second}/whoami");
        await profile.SignInAsync(stack.App("app2"));
        FetchResult app2OnOrders = await profile.FetchAsync($"/api/{app2Second}/whoami");

        // reports binds no audience, so app 1's token validates there and is refused as the wrong client.
        Assert.Equal(403, app1OnReports.Status); // Not an Errors row: the mock API's own answer, passed back.
        Assert.Contains("\"error\":\"client_not_admitted\"", app1OnReports.Body, StringComparison.Ordinal);

        // orders binds its audience, and app 2 names no resource, so its token has no audience orders accepts.
        Assert.Null(FixtureFiles.AppDefinition("app2")["oauth"]!["resource"]);
        if (stack is LocalStack local)
        {
            Assert.Null(local.Harness.Idp.Authorizations.Last(authorization => authorization.ClientId == stack.App("app2").ClientId).Resource);
        }

        Assert.Equal(401, app2OnOrders.Status); // Not an Errors row: the mock API's own answer, passed back.
        Assert.Contains("\"error\":\"invalid_token\"", app2OnOrders.Body, StringComparison.Ordinal);

        // Controls: each token is good at its own API.
        Assert.Equal(200, (await profile.FetchAsync("/api/reports/whoami")).Status);
        await profile.GotoAsync(stack.App("app1").Url("/").ToString());
        await profile.WaitSignedInAsync(stack.App("app1"));
        Assert.Equal(200, (await profile.FetchAsync("/api/orders/whoami")).Status);
    }

    /// <summary>The second API an app lists, from its definition (fixtures/apps).</summary>
    private static string SecondApi(string appId) =>
        FixtureFiles.AppDefinition(appId)["apis"]!.AsArray()[1]!["name"]!.GetValue<string>();
}
