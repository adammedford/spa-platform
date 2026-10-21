using System.Text.Json;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;
using Platform.Registry;

namespace Gateway.E2ETests.Functional;

/// <summary>
/// F-07 in a real browser (docs/plan/04-tests.md, tier D), the third-SPA scenario: onboard app 3 while
/// apps 1 and 2 are in use. The clauses only AWS can show (the task id, start time and task definition
/// unchanged, and the onboarding and platform plans) are not asserted: a local stack has no ECS task and
/// no Terraform plan to read them from.
/// </summary>
public sealed class OnboardingTests
{
    [Fact]
    public async Task F07_app3_is_served_and_logs_in_with_its_own_client_while_apps_1_and_2_stay_signed_in_and_the_api_refuses_it()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile user = await stack.OpenAsync();
        HarnessApp app1 = stack.App("app1");
        HarnessApp app2 = stack.App("app2");
        IServiceProvider services = stack.Harness.Services;

        // Apps 1 and 2 in use in one browser.
        await user.SignInAsync(app1);
        await user.SignInAsync(app2);

        (HarnessApp app3, RefreshOutcome outcome) = await stack.Harness.OnboardAsync("app3", TestContext.Current.CancellationToken);
        Assert.Equal(RefreshOutcome.Swapped, outcome);

        List<Uri> navigations = user.RecordNavigations();
        await user.Page.GotoAsync(app3.Url("/").ToString());
        await user.WaitSignedInAsync(app3);

        // Served: its own shell, after a login with its own client to its own callback.
        Assert.Equal("app3", await user.Page.Locator("#app").GetAttributeAsync("data-app"));
        Uri authorize = Assert.Single(navigations, url => url.GetLeftPart(UriPartial.Path) == stack.Harness.Idp.AuthorizeEndpoint);
        Assert.Equal(app3.ClientId, QueryHelpers.ParseQuery(authorize.Query)["client_id"].ToString());
        Assert.Equal(app3.Url("/signin-oidc").ToString(), QueryHelpers.ParseQuery(authorize.Query)["redirect_uri"].ToString());

        // The mock API refuses app 3's call: its owner has not admitted that client.
        FetchResult refused = await user.FetchAsync("/api/reports/whoami");
        Assert.Equal(403, refused.Status); // Not an Errors row: the mock API's own answer, passed back.
        using (JsonDocument body = JsonDocument.Parse(refused.Body))
        {
            Assert.Equal("client_not_admitted", body.RootElement.GetProperty("error").GetString());
        }

        // No restart: the same gateway, and apps 1 and 2 kept their sessions throughout.
        Assert.Same(services, stack.Harness.Services);
        foreach (HarnessApp app in new[] { app1, app2 })
        {
            await user.Page.GotoAsync(app.Url("/").ToString());
            await user.WaitSignedInAsync(app);
        }

        Assert.Equal(3, stack.Harness.Idp.Authorizations.Count);
    }
}
