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
        await using IBrowserStack stack = await Stacks.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app1 = stack.App("app1");
        HarnessApp app2 = stack.App("app2");

        // App 2 lists data: under img-src, so its own badge loads.
        await profile.SignInAsync(app2);
        Assert.Contains("data:", ImgSrc(await ShellAsync(stack, app2)), StringComparison.Ordinal);
        Assert.True(await LoadsAsync(profile, DataImage), "app 2's policy allows data: images, so its badge should load.");

        // App 1 does not, so the same image on its shell is refused by the browser.
        await profile.SignInAsync(app1);
        Assert.DoesNotContain("data:", ImgSrc(await ShellAsync(stack, app1)), StringComparison.Ordinal);
        Assert.False(await LoadsAsync(profile, DataImage), "app 1's policy has no data: in img-src, so Chromium should refuse the image.");
    }

    [Fact]
    public async Task F06_an_extensionless_path_gets_the_shell_and_a_missing_asset_gets_404()
    {
        await using IBrowserStack stack = await Stacks.StartAsync();
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
        await using IBrowserStack stack = await Stacks.StartAsync();
        await using Profile profile = await stack.OpenAsync();
        HarnessApp app = stack.App("app1");
        string first = await stack.ReleaseOfAsync("app1");

        // A second release of app 1 in the bucket, with a heading the first does not have, named for
        // its content: on the cell a release prefix is written once, so a fixed name would be refused
        // at the next sitting after the fixture changed.
        Dictionary<string, byte[]> files = new(FixtureFiles.Spa("app1"), StringComparer.Ordinal);
        files["index.html"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files["index.html"])
            .Replace("<h1 id=\"marker\">Fixture SPA app1</h1>", "<h1 id=\"marker\">Fixture SPA app1, second release</h1>", StringComparison.Ordinal));
        string second = ContentRelease.Name("f06", files);
        await stack.PublishReleaseAsync("app1", second, files);

        await profile.SignInAsync(app);
        await Assertions.Expect(profile.Page.Locator("#marker")).ToHaveTextAsync("Fixture SPA app1");

        try
        {
            await stack.SwitchReleaseAsync(app, second);
            await ReloadUntilMarkerAsync(stack, profile, "Fixture SPA app1, second release");
        }
        finally
        {
            // Switched back even when the row fails, so the cell is left serving what it served.
            await stack.SwitchReleaseAsync(app, first);
        }

        await ReloadUntilMarkerAsync(stack, profile, "Fixture SPA app1");
        await Assertions.Expect(profile.Page.Locator("#session-state")).ToHaveTextAsync(Profile.SignedInState);
    }

    /// <summary>
    /// Reloads until the shell's heading reads <paramref name="marker"/>, for up to the stack's
    /// <see cref="IBrowserStack.RegistryPropagation"/>: the cell's gateway picks a new revision up on its
    /// next poll, where the local harness polls as it writes.
    /// </summary>
    private static async Task ReloadUntilMarkerAsync(IBrowserStack stack, Profile profile, string marker)
    {
        System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            await profile.Page.ReloadAsync();
            string? text = await profile.Page.Locator("#marker").TextContentAsync();
            if (text == marker || waited.Elapsed >= stack.RegistryPropagation)
            {
                Assert.Equal(marker, text);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The last shell the browser received for the app.</summary>
    private static async Task<Exchange> ShellAsync(IBrowserStack stack, HarnessApp app) =>
        (await stack.ExchangesAsync()).Last(exchange => exchange.Url == app.Url("/") && exchange.Status == 200);

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
