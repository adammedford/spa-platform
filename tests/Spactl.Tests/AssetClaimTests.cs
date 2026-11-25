using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Platform.Abstractions;

namespace Spactl.Tests;

/// <summary>
/// The write-once claim per <c>assets/</c> key (contracts/s3-layout.md, "An assets/ URL names the same
/// bytes in its namespace"), F-T1 to F-T4 of the fragments spec's traces (#961; ticket #993), for a
/// shell's namespace and a fragment's.
/// </summary>
public sealed class AssetClaimTests : IDisposable
{
    private const string A = "export const x = 'A';";
    private const string B = "export const x = 'B';";

    private readonly FragmentWorld world = new();

    private Harness Spactl => world.Spactl;

    private static string Shell => $"apps/{FragmentWorld.AppId}/";

    private static string Fragment => FragmentWorld.FragmentNamespace;

    public void Dispose() => world.Dispose();

    private static Dictionary<string, string> X(string bytes) => new() { ["assets/x.js"] = bytes };

    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private async Task<JsonObject> Claim(string ns) =>
        JsonNode.Parse(await Spactl.Store.TextAsync(ns + "claims/assets/x.js") ?? throw new InvalidOperationException("No claim."))!.AsObject();

    [Fact]
    public async Task Each_assets_key_is_claimed_before_it_is_written_and_no_other_key_is_claimed()
    {
        Assert.Equal(0, await world.PublishShell("r1", more: new Dictionary<string, string> { ["assets/x.js"] = A, ["robots.txt"] = "" }));

        List<string> written = Spactl.Store.Written;
        Assert.True(written.IndexOf($"{Shell}claims/assets/x.js") < written.IndexOf($"{Shell}releases/r1/assets/x.js"));
        Assert.Equal([$"{Shell}claims/assets/x.js"], written.Where(key => key.Contains("/claims/", StringComparison.Ordinal)));
        JsonObject claim = await Claim(Shell);
        Assert.Equal(Digest(A), claim["sha256"]!.GetValue<string>());
        Assert.Equal("r1", claim["release"]!.GetValue<string>());
    }

    [Fact]
    public async Task F_T1_a_shell_release_with_other_bytes_behind_a_claimed_url_is_refused_naming_the_claim()
    {
        Assert.Equal(0, await world.PublishShell("r1", more: X(A)));
        Assert.Equal(0, await world.PublishFragment("f1"));
        Assert.Equal(0, await Spactl.Run("app", "add", world.Definition(), "--release", "r1", "--fragment-release", $"{FragmentWorld.FragmentId}=f1"));
        Assert.Equal(0, await world.PublishShell("r2"));
        Assert.Equal(0, await Spactl.Run("release", "activate", FragmentWorld.AppId, "r2"));

        Assert.Equal(1, await world.PublishShell("r3", more: X(B)));

        Assert.Contains("claimed by release r1", Spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Null(await Spactl.Store.TextAsync($"{Shell}releases/r3/assets/x.js"));
        Assert.Null(await Spactl.Store.TextAsync($"{Shell}releases/r3/index.html"));

        Assert.Equal(0, await world.PublishShell("r4", more: X(A)));
        Assert.Equal(A, await Spactl.Store.TextAsync($"{Shell}releases/r4/assets/x.js"));
        Assert.Equal("r1", (await Claim(Shell))["release"]!.GetValue<string>());
    }

    [Fact]
    public async Task F_T1_a_fragment_release_with_other_bytes_behind_a_claimed_url_is_refused_naming_the_claim()
    {
        Assert.Equal(0, await world.PublishFragment("f1", more: X(A)));
        Assert.Equal(0, await world.PublishFragment("f2"));

        Assert.Equal(1, await world.PublishFragment("f3", more: X(B)));

        Assert.Contains("claimed by release f1", Spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Null(await Spactl.Store.TextAsync($"{Fragment}releases/f3/assets/x.js"));
        Assert.Null(await Spactl.Store.TextAsync($"{Fragment}releases/f3/entry.js"));

        Assert.Equal(0, await world.PublishFragment("f4", more: X(A)));
        Assert.Equal(A, await Spactl.Store.TextAsync($"{Fragment}releases/f4/assets/x.js"));
    }

    [Fact]
    public async Task A_claim_binds_a_url_in_its_own_namespace_only()
    {
        Assert.Equal(0, await world.PublishShell("r1", more: X(A)));

        Assert.Equal(0, await world.PublishFragment("f1", more: X(B)));

        Assert.Equal(Digest(B), (await Claim(Fragment))["sha256"]!.GetValue<string>());
        Assert.Equal(Digest(A), (await Claim(Shell))["sha256"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(A, B, 1)]
    [InlineData(A, A, 2)]
    public async Task F_T2_of_two_publishes_held_at_the_claim_exactly_one_wins_unless_the_bytes_agree(string first, string second, int completing)
    {
        using Harness other = new();
        ClaimBarrierStore barrier = new(Spactl.Store, parties: 2);
        Spactl.StoreOverride = barrier;
        other.StoreOverride = barrier;
        string one = world.Directory("one", new Dictionary<string, string> { ["entry.js"] = "export {};", ["assets/x.js"] = first });
        string two = world.Directory("two", new Dictionary<string, string> { ["entry.js"] = "export {};", ["assets/x.js"] = second });

        int[] exits = await Task.WhenAll(
            Task.Run(() => Spactl.Run("fragment", "publish", FragmentWorld.AppId, FragmentWorld.FragmentId, one, "f1"), TestContext.Current.CancellationToken),
            Task.Run(() => other.Run("fragment", "publish", FragmentWorld.AppId, FragmentWorld.FragmentId, two, "f2"), TestContext.Current.CancellationToken));

        Assert.Equal(2, barrier.Arrived);
        Assert.Equal(completing, exits.Count(exit => exit == 0));
        JsonObject claim = await Claim(Fragment);
        string winner = claim["release"]!.GetValue<string>();
        string winnerBytes = winner == "f1" ? first : second;
        Assert.Equal(Digest(winnerBytes), claim["sha256"]!.GetValue<string>());
        Assert.Equal(winnerBytes, await Spactl.Store.TextAsync($"{Fragment}releases/{winner}/assets/x.js"));
        if (completing == 1)
        {
            string loser = winner == "f1" ? "f2" : "f1";
            Assert.Equal(1, exits[loser == "f1" ? 0 : 1]);
            Assert.Null(await Spactl.Store.TextAsync($"{Fragment}releases/{loser}/assets/x.js"));
            Assert.Null(await Spactl.Store.TextAsync($"{Fragment}releases/{loser}/entry.js"));
        }
    }

    [Fact]
    public async Task F_T3_a_claim_without_its_object_completes_on_a_rerun_with_the_same_bytes()
    {
        await Spactl.Store.SeedAsync($"{Fragment}claims/assets/x.js", "{\"sha256\":\"" + Digest(A) + "\",\"release\":\"f1\"}");

        Assert.Equal(0, await world.PublishFragment("f1", more: X(A)));

        Assert.Equal(A, await Spactl.Store.TextAsync($"{Fragment}releases/f1/assets/x.js"));
        Assert.NotNull(await Spactl.Store.TextAsync($"{Fragment}releases/f1/entry.js"));
    }

    [Fact]
    public async Task F_T4_an_unclaimed_legacy_object_binds_nothing()
    {
        // A key published before claims existed has none, and there is no backfill (#961, H-F2).
        await Spactl.Store.SeedAsync($"{Shell}releases/r0/assets/x.js", A);

        Assert.Equal(0, await world.PublishShell("r1", more: X(B)));

        Assert.Equal(B, await Spactl.Store.TextAsync($"{Shell}releases/r1/assets/x.js"));
        Assert.Equal(Digest(B), (await Claim(Shell))["sha256"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_claim_that_cannot_be_read_back_after_a_refused_create_answers_run_the_command_again()
    {
        Spactl.StoreOverride = new LostClaimStore(Spactl.Store);

        Assert.Equal(1, await world.PublishFragment("f1", more: X(A)));

        Assert.Contains("run the command again", Spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Null(await Spactl.Store.TextAsync($"{Fragment}releases/f1/assets/x.js"));
    }

    [Fact]
    public async Task A_claim_that_does_not_parse_refuses_the_release()
    {
        await Spactl.Store.SeedAsync($"{Fragment}claims/assets/x.js", "not a claim");

        Assert.Equal(1, await world.PublishFragment("f1", more: X(A)));

        Assert.Contains("unreadable claim", Spactl.Error.ToString(), StringComparison.Ordinal);
        Assert.Null(await Spactl.Store.TextAsync($"{Fragment}releases/f1/assets/x.js"));
    }
}

/// <summary>
/// Holds every claim write until <paramref name="parties"/> publishes have reached one, then lets them
/// race: F-T2's barrier before the claim.
/// </summary>
internal sealed class ClaimBarrierStore(IObjectStore inner, int parties) : IObjectStore
{
    private readonly TaskCompletionSource all = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int arrived;

    public int Arrived => Volatile.Read(ref arrived);

    public Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct) => inner.GetAsync(key, ifNoneMatch, ct);

    public async Task<bool> PutAsync(string key, Stream body, string? ifMatch, CancellationToken ct)
    {
        if (key.Contains("/claims/", StringComparison.Ordinal))
        {
            if (Interlocked.Increment(ref arrived) >= parties)
            {
                all.TrySetResult();
            }

            await all.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }

        return await inner.PutAsync(key, body, ifMatch, ct);
    }
}

/// <summary>
/// A store where a claim's create is refused and the claim then reads as absent: the create was lost
/// between the request and the answer, or the claim cannot be seen yet.
/// </summary>
internal sealed class LostClaimStore(IObjectStore inner) : IObjectStore
{
    public Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct) =>
        key.Contains("/claims/", StringComparison.Ordinal) ? Task.FromResult<StoredObject?>(null) : inner.GetAsync(key, ifNoneMatch, ct);

    public Task<bool> PutAsync(string key, Stream body, string? ifMatch, CancellationToken ct) =>
        key.Contains("/claims/", StringComparison.Ordinal) ? Task.FromResult(false) : inner.PutAsync(key, body, ifMatch, ct);
}
