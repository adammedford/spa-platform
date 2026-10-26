using System.Text.Json.Nodes;

namespace Spactl.Tests;

/// <summary>
/// A read the store refused (<see cref="Platform.Abstractions.ObjectReadRefusedException"/>) stops
/// spactl with exit 1 and a diagnostic naming the key, and is never read as "missing" or "different
/// bytes" (change request contracts/changes/issue-381-object-read-refusal.md, item 1, the
/// <c>tools/spactl</c> row). Each of the three reads it names is a row here: the registry read, the
/// release-prefix check's read of <c>index.html</c>, and a resumed publish's byte comparison. In each,
/// no write follows the refused read.
///
/// Mutation: remove the <c>ObjectReadRefusedException</c> handler from <c>SpactlApp.RunAsync</c>, and
/// all three fail with the exception escaping, no exit code and no diagnostic (3 of 3).
/// </summary>
public sealed class RefusedReadTests : IDisposable
{
    private readonly Harness spactl = new();

    public void Dispose() => spactl.Dispose();

    private static string ExampleRelease => Harness.ExampleJson("registry.example.json")["apps"]![0]!["release"]!.GetValue<string>();

    private string RegistryFile()
    {
        JsonObject document = Harness.ExampleJson("registry.example.json");
        return spactl.WriteFile("registry.json", document.ToJsonString());
    }

    [Fact]
    public async Task Registry_validate_over_a_refused_registry_read_exits_1_and_names_the_key()
    {
        // Read as "no registry", the refusal would let validate pass a revision the live one may
        // exceed. Mutation: read the refusal as absence in ReadLiveAsync (catch it, return no live
        // document), and validate exits 0 here, the prefix being published.
        await spactl.Store.SeedAsync($"apps/app1/releases/{ExampleRelease}/index.html", "<!doctype html>");
        int written = spactl.Store.Written.Count;
        spactl.StoreOverride = new RefusingStore(spactl.Store, Harness.RegistryKey);

        Assert.Equal(1, await spactl.Run("registry", "validate", RegistryFile()));

        AssertRefusedDiagnostic(Harness.RegistryKey);
        Assert.Equal(written, spactl.Store.Written.Count);
    }

    [Fact]
    public async Task Registry_apply_whose_release_prefix_check_is_refused_exits_1_and_writes_nothing()
    {
        // The prefix check reads index.html (contracts/README.md gives spactl the rule). A refusal is
        // not "has no index.html, publish it first": the release may well be published and the
        // credentials unable to read it. Mutation: catch the refusal in ExistsAsync and return false,
        // and this fails on the diagnostic, which then says "has no index.html".
        string shell = $"apps/app1/releases/{ExampleRelease}/index.html";
        await spactl.Store.SeedAsync(shell, "<!doctype html>");
        int written = spactl.Store.Written.Count;
        spactl.StoreOverride = new RefusingStore(spactl.Store, shell);

        Assert.Equal(1, await spactl.Run("registry", "apply", RegistryFile()));

        AssertRefusedDiagnostic(shell);
        Assert.DoesNotContain("has no index.html", spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal(written, spactl.Store.Written.Count);
        Assert.Null(await spactl.Store.TextAsync(Harness.RegistryKey));
    }

    [Fact]
    public async Task A_resumed_publish_whose_byte_comparison_is_refused_exits_1_and_writes_nothing_after_it()
    {
        // A resumed publish meets an object already there and compares its bytes. A refused read is
        // neither "same bytes" nor "other content". Mutation: catch the refusal in SameBytesAsync and
        // return false, and this fails on the diagnostic, which then says the prefix is written once.
        string spa = Harness.Example("spa", "app1");
        string first = Directory.EnumerateFiles(spa, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(spa, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(file => file != "index.html")
            .Order(StringComparer.Ordinal)
            .First();
        string key = "apps/app1/releases/r1/" + first;
        await spactl.Store.SeedAsync(key, await File.ReadAllTextAsync(Path.Combine(spa, first), TestContext.Current.CancellationToken));
        int written = spactl.Store.Written.Count;
        spactl.StoreOverride = new RefusingStore(spactl.Store, key);

        Assert.Equal(1, await spactl.Run("release", "publish", "app1", spa, "r1"));

        AssertRefusedDiagnostic(key);
        Assert.DoesNotContain("written once", spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal(written, spactl.Store.Written.Count);
        Assert.Null(await spactl.Store.TextAsync("apps/app1/releases/r1/index.html"));
    }

    private void AssertRefusedDiagnostic(string key)
    {
        string error = spactl.Error.ToString();
        Assert.Contains(key, error, StringComparison.Ordinal);
        Assert.Contains("refused", error, StringComparison.Ordinal);
        Assert.Contains("missing or not readable", error, StringComparison.Ordinal);
    }
}
