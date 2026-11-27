using Gateway.TestHarness;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>
/// N-41 (#1007, part of #961 -> "Fragment routes at the proxy"): <c>/fragments/{fragmentId}/api/**</c> is
/// the fragment's API route, so <c>spactl fragment publish</c> refuses a release with a top-level
/// <c>api/</c>, whose files that route would shadow, before it writes anything of the release.
/// </summary>
public sealed class PublishTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task G9_fragment_publish_refuses_a_release_with_a_top_level_api_directory()
    {
        // Mutation that turns it red: a publish that checks api/ only on the shell's releases (or after
        // writing the claims and objects) exits 0 here, or leaves the release's objects behind.
        await using GatewayHarness harness = await GatewayHarness.StartAsync(GatewayHarnessOptions.Shell(1), Ct);
        WriteRecorder bucket = new(harness.Store);
        const string Release = "2026-11-03.1";

        (int exit, _, string error) = await SpactlRuns.RunOverAsync(harness, bucket, Ct,
            "fragment", "publish", "shell", "orders",
            SpactlRuns.Directory(SpactlRuns.Files(
                ("entry.js", "export {};\n"), ("assets/a.0a0a0a0a.js", "export {};\n"), ("api/data.json", "{}"))),
            Release);

        Assert.NotEqual(0, exit);
        Assert.Contains("api/", error, StringComparison.Ordinal);
        Assert.Empty(bucket.Written);
        foreach (string path in (string[])["entry.js", "assets/a.0a0a0a0a.js", "api/data.json"])
        {
            Assert.Null(await SpactlRuns.TextAsync(harness, $"apps/shell/fragments/orders/releases/{Release}/{path}", Ct));
        }

        // The control: the same release without api/ publishes, and api/ deeper down is a file like any other.
        (int ok, _, string okError) = await SpactlRuns.RunAsync(harness, Ct,
            "fragment", "publish", "shell", "orders",
            SpactlRuns.Directory(SpactlRuns.Files(("entry.js", "export {};\n"), ("lib/api/data.json", "{}"))),
            Release);
        Assert.True(ok == 0, okError);
        Assert.Equal("{}", await SpactlRuns.TextAsync(harness, $"apps/shell/fragments/orders/releases/{Release}/lib/api/data.json", Ct));
    }
}
