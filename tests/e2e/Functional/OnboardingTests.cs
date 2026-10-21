using System.Text.Json;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.AspNetCore.WebUtilities;

namespace Gateway.E2ETests.Functional;

/// <summary>
/// F-07 in a real browser (docs/plan/04-tests.md, tier D), the third-SPA scenario: onboard app 3 while
/// apps 1 and 2 are in use, on the stack <see cref="StackSwitch"/> chooses (#212). On the cell the
/// clauses only AWS can show are asserted too: the apps root's plan with the onboarding definition,
/// read before it was applied, adds app 3's own resources alone, and the gateway's one task keeps its
/// id, start and task definition revision throughout. The clauses only the harness can show (the same
/// service provider, the provider's authorization count) are asserted on the local stack alone.
/// </summary>
public sealed class OnboardingTests
{
    [Fact]
    public async Task F07_app3_is_served_and_logs_in_with_its_own_client_while_apps_1_and_2_stay_signed_in_and_the_api_refuses_it()
    {
        await using IBrowserStack stack = await Stacks.StartAsync();
        await using Profile user = await stack.OpenAsync();
        HarnessApp app1 = stack.App("app1");
        HarnessApp app2 = stack.App("app2");
        IServiceProvider? services = (stack as LocalStack)?.Harness.Services;
        DeployedStack? cell = stack as DeployedStack;
        RunningTask? before = cell is null ? null : await cell.Aws.RunningTaskAsync(TestContext.Current.CancellationToken);

        // Apps 1 and 2 in use in one browser.
        await user.SignInAsync(app1);
        await user.SignInAsync(app2);

        try
        {
            HarnessApp app3 = await stack.OnboardAsync("app3");

            List<Uri> navigations = user.RecordNavigations();
            await user.Page.GotoAsync(app3.Url("/").ToString());
            await stack.CompleteLoginAsync(user, app3);
            await user.WaitSignedInAsync(app3);

            // Served: its own shell, after a login with its own client to its own callback.
            Assert.Equal("app3", await user.Page.Locator("#app").GetAttributeAsync("data-app"));
            Uri authorize = Assert.Single(navigations, url => url.GetLeftPart(UriPartial.Path) == stack.AuthorizeEndpoint);
            Assert.Equal(app3.ClientId, QueryHelpers.ParseQuery(authorize.Query)["client_id"].ToString());
            Assert.Equal(app3.Url("/signin-oidc").ToString(), QueryHelpers.ParseQuery(authorize.Query)["redirect_uri"].ToString());

            // The mock API refuses app 3's call: its owner has not admitted that client. On the cell the
            // platform root keeps app 3's client out of its allowlists (infra/envs/poc/platform/main.tf).
            FetchResult refused = await user.FetchAsync("/api/reports/whoami");
            Assert.Equal(403, refused.Status); // Not an Errors row: the mock API's own answer, passed back.
            using (JsonDocument body = JsonDocument.Parse(refused.Body))
            {
                Assert.Equal("client_not_admitted", body.RootElement.GetProperty("error").GetString());
            }

            // No restart: apps 1 and 2 kept their sessions throughout.
            foreach (HarnessApp app in new[] { app1, app2 })
            {
                await user.Page.GotoAsync(app.Url("/").ToString());
                await user.WaitSignedInAsync(app);
            }

            if (stack is LocalStack local)
            {
                // The same gateway, and one authorization per app.
                Assert.Same(services, local.Harness.Services);
                Assert.Equal(3, local.Harness.Idp.Authorizations.Count);
            }

            if (cell is not null)
            {
                // The plan, read before the apply: one client, one branding style and one parameter, all app 3's.
                string root = await File.ReadAllTextAsync(Path.Combine(FixtureFiles.RepositoryRoot, "infra", "envs", "poc", "apps", "main.tf"), TestContext.Current.CancellationToken);
                Assert.Equal(
                    TerraformPlan.PerAppAddresses(root, "app3").Select(address => new PlannedChange(address, "create")),
                    cell.LastOnboarding!.Planned.OrderBy(change => change.Address, StringComparer.Ordinal));

                // The same task: no restart, no new task definition, no deployment.
                Assert.Equal(before, await cell.Aws.RunningTaskAsync(TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            // Taken off even when the row fails, so the cell is left serving what it served and the next
            // sitting's plan shows the onboarding again.
            await stack.OffboardAsync("app3");
        }
    }
}
