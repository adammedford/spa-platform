using System.Text;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.Playwright;

namespace Gateway.E2ETests.Functional;

/// <summary>
/// F-06 in a real browser (docs/plan/04-tests.md, tier D): each app's own content security policy, as
/// the browser enforces it; the asset fallback matrix; and a release switched and switched back with no
/// deployment.
/// </summary>
public sealed class ShellTests
{
    /// <summary>A one-pixel GIF as a <c>data:</c> URL, the source app 2 adds to <c>img-src</c> (fixtures/apps/app2.json).</summary>
    private const string DataImage = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";

    [Fact]
    public async Task F06_each_app_gets_its_own_policy_and_the_browser_enforces_it()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app1 = stack.App("app1");
        HarnessApp app2 = stack.App("app2");

        // App 2 lists data: under img-src, so its own badge loads.
        await profile.SignInAsync(app2);
        Assert.Contains("data:", ImgSrc(Shell(stack, app2)), StringComparison.Ordinal);
        Assert.True(await LoadsAsync(profile, DataImage), "app 2's policy allows data: images, so its badge should load.");

        // App 1 does not, so the same image on its shell is refused by the browser.
        await profile.SignInAsync(app1);
        Assert.DoesNotContain("data:", ImgSrc(Shell(stack, app1)), StringComparison.Ordinal);
        Assert.False(await LoadsAsync(profile, DataImage), "app 1's policy has no data: in img-src, so Chromium should refuse the image.");
    }

    [Fact]
    public async Task F06_an_extensionless_path_gets_the_shell_and_a_missing_asset_gets_404()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        await profile.SignInAsync(app);

        IResponse? deep = await profile.Page.GotoAsync(app.Url("/orders/42/edit").ToString());
        Assert.Equal(200, deep?.Status);
        Assert.Equal(Encoding.UTF8.GetString(FixtureFiles.Spa("app1")["index.html"]), await deep!.TextAsync());
        await Assertions.Expect(profile.Page.Locator("#session-state")).ToHaveTextAsync(Profile.SignedInState);

        IResponse? missing = await profile.Page.GotoAsync(app.Url("/assets/missing.0badf00d.js").ToString());
        Assert.Equal(Contract.Error("not_found").Status, missing?.Status);
    }

    [Fact]
    public async Task F06_switching_the_release_changes_the_content_with_no_deployment_and_switching_back_restores_it()
    {
        await using LocalStack stack = await LocalStack.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        string first = stack.Harness.Options.Release;
        const string Second = "2026-10-16.1";

        // A second release of app 1 in the bucket, with a heading the first does not have.
        Dictionary<string, byte[]> files = new(FixtureFiles.Spa("app1"), StringComparer.Ordinal);
        files["index.html"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files["index.html"])
            .Replace("<h1 id=\"marker\">Fixture SPA app1</h1>", "<h1 id=\"marker\">Fixture SPA app1, second release</h1>", StringComparison.Ordinal));
        await stack.Harness.PublishReleaseAsync("app1", Second, files, TestContext.Current.CancellationToken);

        await profile.SignInAsync(app);
        await Assertions.Expect(profile.Page.Locator("#marker")).ToHaveTextAsync("Fixture SPA app1");

        await SwitchAsync(stack, app, Second);
        await profile.Page.ReloadAsync();
        await Assertions.Expect(profile.Page.Locator("#marker")).ToHaveTextAsync("Fixture SPA app1, second release");

        await SwitchAsync(stack, app, first);
        await profile.Page.ReloadAsync();
        await Assertions.Expect(profile.Page.Locator("#marker")).ToHaveTextAsync("Fixture SPA app1");
        await Assertions.Expect(profile.Page.Locator("#session-state")).ToHaveTextAsync(Profile.SignedInState);
    }

    /// <summary>Points the app's entry at another release in a new registry revision, as <c>spactl</c> does.</summary>
    private static async Task SwitchAsync(LocalStack stack, HarnessApp app, string release)
    {
        Platform.Registry.RefreshOutcome outcome = await stack.Harness.UpdateRegistryAsync(document =>
        {
            foreach (System.Text.Json.Nodes.JsonNode? entry in document["apps"]!.AsArray())
            {
                if (entry!["appId"]!.GetValue<string>() == app.AppId)
                {
                    entry["release"] = release;
                }
            }
        }, TestContext.Current.CancellationToken);
        Assert.Equal(Platform.Registry.RefreshOutcome.Swapped, outcome);
    }

    /// <summary>The last shell the browser received for the app.</summary>
    private static Exchange Shell(LocalStack stack, HarnessApp app) =>
        stack.LoadBalancer.Exchanges.Last(exchange => exchange.Url == app.Url("/") && exchange.Status == 200);

    private static string ImgSrc(Exchange shell) =>
        Contract.Directives(shell.Header("Content-Security-Policy")!).Single(directive => directive.Key == "img-src").Value;

    /// <summary>Adds an image to the current page and reports whether the browser loaded it.</summary>
    private static Task<bool> LoadsAsync(Profile profile, string source) =>
        profile.Page.EvaluateAsync<bool>(
            """
            (source) => new Promise((resolve) => {
              const image = new Image();
              image.onload = () => resolve(true);
              image.onerror = () => resolve(false);
              image.src = source;
              document.body.appendChild(image);
            })
            """,
            source);
}
