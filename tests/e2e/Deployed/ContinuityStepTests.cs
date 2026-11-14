using System.Text.Json;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The continuity driver's browser step on the local composed stack (#1193): its <c>traceparent</c>
/// goes to the app's origin and to no other. On the cell the header went on every request of the
/// context, the provider's pages' own among them, and rows 5 and 7 stopped at Cognito's form (#348);
/// the rows themselves run on the two-task cell only.
/// </summary>
public sealed class ContinuityStepTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_steps_traceparent_reaches_the_apps_origin_with_the_browsers_own_headers_and_no_other_origin()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        await profile.SignInAsync(app);

        // One step with a request of each kind: the page's own call to its app, then a navigation to a
        // page on another site, as a login leaves for the provider.
        Uri elsewhere = new(ForeignSites.Elsewhere, "/login");
        Step step = await ContinuityDriver.BrowserStepAsync(profile, app, "session", ContinuityDriver.SessionPath, "bff", 200, async () =>
        {
            await profile.FetchAsync(ContinuityDriver.SessionPath);
            await profile.OpenForeignPageAsync(elsewhere);
        });

        Assert.Equal(200, step.Status);
        Exchange own = Assert.Single(stack.LoadBalancer.Exchanges, exchange => exchange.Sent(ClientTrace.Header)?.Contains(step.TraceId, StringComparison.Ordinal) == true);
        Assert.Equal(ContinuityDriver.SessionPath, own.Url.AbsolutePath);

        // The request was the browser's own but for the header: its Fetch Metadata and its cookies went with it.
        Assert.Equal(("same-origin", "cors", "empty"), (own.Sent("Sec-Fetch-Site"), own.Sent("Sec-Fetch-Mode"), own.Sent("Sec-Fetch-Dest")));
        Assert.NotNull(own.Sent("Cookie"));

        Exchange foreign = Assert.Single(stack.LoadBalancer.ForeignRequests, request => request.Url.AbsolutePath == elsewhere.AbsolutePath);
        Assert.Null(foreign.Sent(ClientTrace.Header));

        // And the gateway took the step's trace id for the request's line, as the observer reads it.
        JsonElement[] Lines() => [.. stack.RequestLines.Where(line => line.TryGetProperty("trace_id", out JsonElement id) && id.GetString() == step.TraceId)];
        for (int attempt = 0; attempt < 250 && Lines().Length == 0; attempt++)
        {
            await Task.Delay(20, Ct);
        }

        Assert.Equal(200, Assert.Single(Lines()).GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task A_request_made_after_the_step_carries_no_traceparent()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        await profile.SignInAsync(app);
        await ContinuityDriver.BrowserStepAsync(profile, app, "session", ContinuityDriver.SessionPath, "bff", 200, () => profile.FetchAsync(ContinuityDriver.SessionPath));
        int before = stack.LoadBalancer.Exchanges.Count;

        FetchResult after = await profile.FetchAsync(ContinuityDriver.SessionPath);

        Assert.Equal(200, after.Status);
        Assert.All(stack.LoadBalancer.Exchanges.Skip(before), exchange => Assert.Null(exchange.Sent(ClientTrace.Header)));
    }
}
