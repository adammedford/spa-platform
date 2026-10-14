using System.Text.Json.Nodes;

namespace Spactl.Tests;

/// <summary>
/// <c>spactl release publish</c> and <c>release activate</c> (plan row 2.6; contracts/s3-layout.md).
/// Publish writes every object once, under <c>apps/{appId}/releases/{release}/</c>; activate is a
/// registry change that names a release whose prefix exists.
/// </summary>
public sealed class ReleaseCommandTests : IDisposable
{
    private readonly Harness spactl = new();

    public void Dispose() => spactl.Dispose();

    private static string App1Spa => Harness.Example("spa", "app1");

    /// <summary>The fixture SPA's files, relative to its directory: what a publish must upload.</summary>
    private static List<string> App1Files =>
    [
        .. Directory.EnumerateFiles(App1Spa, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(App1Spa, file).Replace(Path.DirectorySeparatorChar, '/')),
    ];

    [Fact]
    public async Task Publish_uploads_every_file_under_the_release_prefix_with_index_html_last()
    {
        Assert.Equal(0, await spactl.Run("release", "publish", "app1", App1Spa, "r1"));

        Assert.Equal(
            App1Files.Select(file => "apps/app1/releases/r1/" + file).Order(StringComparer.Ordinal),
            spactl.Store.Written.Order(StringComparer.Ordinal));
        Assert.Equal("apps/app1/releases/r1/index.html", spactl.Store.Written[^1]);
        Assert.Contains(App1Files, file => file.StartsWith("assets/", StringComparison.Ordinal));
        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(App1Spa, "index.html"), TestContext.Current.CancellationToken),
            await spactl.Store.TextAsync("apps/app1/releases/r1/index.html"));
    }

    [Fact]
    public async Task Publish_never_replaces_an_object_with_other_content()
    {
        await spactl.Store.SeedAsync("apps/app1/releases/r1/index.html", "an earlier, different index");

        Assert.Equal(1, await spactl.Run("release", "publish", "app1", App1Spa, "r1"));

        Assert.Contains("written once", spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal("an earlier, different index", await spactl.Store.TextAsync("apps/app1/releases/r1/index.html"));
    }

    [Fact]
    public async Task Publish_run_again_with_the_same_files_writes_nothing_and_succeeds()
    {
        await spactl.Run("release", "publish", "app1", App1Spa, "r1");
        int written = spactl.Store.Written.Count;

        Assert.Equal(0, await spactl.Run("release", "publish", "app1", App1Spa, "r1"));

        Assert.Equal(written, spactl.Store.Written.Count);
    }

    [Fact]
    public async Task Publish_refuses_a_directory_without_index_html_and_writes_nothing()
    {
        string directory = spactl.ScratchDirectory("no-index");
        spactl.WriteFile("no-index/assets/app.js", "console.log(1)");

        Assert.Equal(1, await spactl.Run("release", "publish", "app1", directory, "r1"));
        Assert.Empty(spactl.Store.Written);
    }

    [Theory]
    [InlineData("../app2", "r1")]
    [InlineData("App1", "r1")]
    [InlineData("app1", "r1/../../registry")]
    [InlineData("app1", "has space")]
    public async Task Publish_refuses_an_app_id_or_release_the_registry_schema_refuses(string appId, string release)
    {
        // contracts/s3-layout.md: the key is built from checked values, never an unchecked segment.
        Assert.Equal(2, await spactl.Run("release", "publish", appId, App1Spa, release));
        Assert.Empty(spactl.Store.Written);
    }

    [Fact]
    public async Task Activate_points_the_entry_at_a_published_release_under_the_next_revision()
    {
        await spactl.Run("release", "publish", "app1", App1Spa, "r1");
        await spactl.Run("app", "add", Harness.Example("apps", "app1.json"), "--release", "r1");
        long before = (await spactl.LiveRegistry())["revision"]!.GetValue<long>();
        await spactl.Run("release", "publish", "app1", App1Spa, "r2");

        Assert.Equal(0, await spactl.Run("release", "activate", "app1", "r2"));

        JsonObject live = await spactl.LiveRegistry();
        Assert.Equal(before + 1, live["revision"]!.GetValue<long>());
        Assert.Equal("r2", live["apps"]![0]!["release"]!.GetValue<string>());
    }

    [Fact]
    public async Task Activate_refuses_a_release_that_was_never_published()
    {
        await spactl.Run("release", "publish", "app1", App1Spa, "r1");
        await spactl.Run("app", "add", Harness.Example("apps", "app1.json"), "--release", "r1");
        int versions = spactl.Store.VersionCount(Harness.RegistryKey);

        Assert.Equal(1, await spactl.Run("release", "activate", "app1", "never-published"));

        Assert.Contains("apps/app1/releases/never-published/", spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal(versions, spactl.Store.VersionCount(Harness.RegistryKey));
    }

    [Fact]
    public async Task Activate_refuses_an_app_the_registry_does_not_hold()
    {
        await spactl.Run("release", "publish", "app1", App1Spa, "r1");
        await spactl.Run("app", "add", Harness.Example("apps", "app1.json"), "--release", "r1");

        Assert.Equal(1, await spactl.Run("release", "activate", "app9", "r1"));
        Assert.Contains("app9", spactl.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Activate_with_no_live_registry_is_refused()
    {
        Assert.Equal(1, await spactl.Run("release", "activate", "app1", "r1"));
        Assert.Null(await spactl.Store.TextAsync(Harness.RegistryKey));
    }
}
