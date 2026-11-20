using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Hosting.Tests;

/// <summary>
/// The asset endpoint, <c>MapPlatformAssets</c>, under <c>/fragments/</c> (#992; contracts/s3-layout.md;
/// contracts/http-contract.md, Endpoints and "Fragment activation"), behind the real
/// <c>UsePlatformHealth</c> and <c>UsePlatformHostResolution</c> and over a recording in-memory store.
/// <c>/fragments/</c> is reserved from the shell's release on every host: a host with no fragments
/// answers 404 <c>path.reserved</c>, an unknown <c>fragmentId</c> 404 <c>not_found</c>, and a known one
/// maps <c>/fragments/{fragmentId}/{rest}</c> to its live release with no <c>index.html</c> fallback.
/// </summary>
public sealed class FragmentAssetTests
{
    private const string ShellRelease = "2026-10-12-abc1234";
    private const string ReleaseA = "2026-11-01-aaaa111";
    private const string ReleaseB = "2026-11-02-bbbb222";

    private const string Leak = "LEAKED:";
    private const string Navigation = "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8\r\n";

    private static readonly FragmentDefinition Reports = TestApps.Fragment("reports", release: ReleaseA);
    private static readonly FragmentDefinition Audit = TestApps.Fragment("audit", release: ReleaseA, entry: "remote.js");

    private static readonly AppDefinition Shell = TestApps.Entry("app1", release: ShellRelease, fragments: [Reports, Audit]);
    private static readonly AppDefinition Plain = TestApps.Entry("app2", release: ShellRelease);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ShellPrefix(AppDefinition app) => $"apps/{app.AppId}/releases/{app.Release}/";

    private static string FragmentPrefix(string fragmentId, string release) => $"apps/app1/fragments/{fragmentId}/releases/{release}/";

    private static async Task<(HostingHost Gateway, RecordingObjectStore Store)> StartAsync(
        bool telemetry = false,
        MissingKeyAnswer missingKeys = MissingKeyAnswer.ConfirmedAbsence)
    {
        RecordingObjectStore store = new(missingKeys);
        await store.SeedAsync(ShellPrefix(Shell) + "index.html", "<p>shell</p>");
        await store.SeedAsync(ShellPrefix(Plain) + "index.html", "<p>plain</p>");

        string a = FragmentPrefix("reports", ReleaseA);
        await store.SeedAsync(a + "entry.js", "export const release = 'A';");
        await store.SeedAsync(a + "assets/chunk-only-a.js", "chunk A");
        await store.SeedAsync(a + "assets/chunk-both.js", "chunk both");
        await store.SeedAsync(a + "config", "{\"fragment\":true}");
        await store.SeedAsync(a + "api/leak.json", Leak + "a fragment release's api/");
        await store.SeedAsync(FragmentPrefix("audit", ReleaseA) + "remote.js", "export const audit = 1;");

        string b = FragmentPrefix("reports", ReleaseB);
        await store.SeedAsync(b + "entry.js", "export const release = 'B';");
        await store.SeedAsync(b + "assets/chunk-both.js", "chunk both");

        // What no request under /fragments/ may read: the shell's prefix and the claim keys.
        await store.SeedAsync(ShellPrefix(Shell) + "fragments/reports/entry.js", Leak + "the shell's prefix");
        await store.SeedAsync(ShellPrefix(Plain) + "fragments/reports/entry.js", Leak + "the plain shell's prefix");
        await store.SeedAsync("apps/app1/fragments/reports/claims/assets/chunk-only-a.js", Leak + "a fragment claim");
        await store.SeedAsync("apps/app1/claims/assets/chunk-only-a.js", Leak + "a shell claim");

        HostingHost gateway = await HostingHost.StartAsync(Snapshots.With(1, Shell, Plain), Ct, store, telemetry);
        return (gateway, store);
    }

    // ---- Reserved from the shell's release.

    [Theory]
    [InlineData("/fragments/reports/entry.js")]
    [InlineData("/fragments/reports/")]
    [InlineData("/fragments/")]
    [InlineData("/fragments")]
    [InlineData("/FRAGMENTS/reports/entry.js")]
    public async Task On_a_host_with_no_fragments_a_path_under_fragments_is_404_path_reserved_and_reads_nothing(string path)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync(telemetry: true);
        await using HostingHost _ = gateway;

        RawResponse response = await gateway.GetAsync(Plain.Host, path, Ct, Navigation);

        Assert.Equal(404, response.StatusCode);
        AssertProblem(response, "not_found");
        Assert.Equal("private, no-store", response.Header("cache-control"));
        Assert.Equal(RuleIds.PathReserved, gateway.LastTelemetry?.Rule);
        Assert.Empty(store.KeysRead);
    }

    [Theory]
    [InlineData("/fragments/unknown/entry.js")]
    [InlineData("/fragments/unknown/")]
    [InlineData("/fragments/Reports/entry.js")]
    [InlineData("/fragments/")]
    [InlineData("/fragments")]
    public async Task An_unknown_fragmentId_is_404_not_found_with_no_rule_and_reads_nothing(string path)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync(telemetry: true);
        await using HostingHost _ = gateway;

        RawResponse response = await gateway.GetAsync(Shell.Host, path, Ct, Navigation);

        Assert.Equal(404, response.StatusCode);
        AssertProblem(response, "not_found");
        Assert.Equal("private, no-store", response.Header("cache-control"));
        Assert.NotNull(gateway.LastTelemetry);
        Assert.Null(gateway.LastTelemetry.Rule);
        Assert.Empty(store.KeysRead);
    }

    // ---- URL to key.

    [Theory]
    [InlineData("/fragments/reports/entry.js", "reports", "entry.js", "export const release = 'A';", "private, no-cache", "text/javascript")]
    [InlineData("/fragments/reports/assets/chunk-only-a.js", "reports", "assets/chunk-only-a.js", "chunk A", "private, max-age=31536000, immutable", "text/javascript")]
    [InlineData("/fragments/reports/config", "reports", "config", "{\"fragment\":true}", "private, no-cache", "application/octet-stream")]
    [InlineData("/fragments/audit/remote.js", "audit", "remote.js", "export const audit = 1;", "private, no-cache", "text/javascript")]
    [InlineData("/FRAGMENTS/reports/entry.js", "reports", "entry.js", "export const release = 'A';", "private, no-cache", "text/javascript")]
    public async Task A_known_fragments_path_serves_the_key_under_its_live_release_with_the_cache_rules_by_path(
        string path, string fragmentId, string rest, string body, string cacheControl, string contentType)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync();
        await using HostingHost _ = gateway;

        RawResponse response = await gateway.GetAsync(Shell.Host, path, Ct, Navigation);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(body, response.BodyText);
        Assert.Equal(cacheControl, response.Header("cache-control"));
        Assert.StartsWith(contentType, response.Header("content-type"), StringComparison.Ordinal);
        Assert.Equal("nosniff", response.Header("x-content-type-options"));
        string key = FragmentPrefix(fragmentId, ReleaseA) + rest;
        Assert.Equal([key], store.KeysRead);
        Assert.Equal(await store.ETagOfAsync(key), response.Header("etag"));
    }

    /// <summary>
    /// No <c>index.html</c> fallback under a fragment: an extensionless path that accepts HTML is the
    /// key at that path or a 404, and an absent <c>index.html</c> is an absent asset, not a shell that
    /// failed (contracts/http-contract.md, Endpoints).
    /// </summary>
    [Theory]
    [InlineData("/fragments/reports/orders", "orders")]
    [InlineData("/fragments/reports/orders/42", "orders/42")]
    [InlineData("/fragments/reports/index.html", "index.html")]
    [InlineData("/fragments/reports/assets/missing-9c9c.js", "assets/missing-9c9c.js")]
    public async Task An_absent_key_is_404_with_no_index_html_fallback(string path, string rest)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync();
        await using HostingHost _ = gateway;

        RawResponse response = await gateway.GetAsync(Shell.Host, path, Ct, Navigation);

        Assert.Equal(404, response.StatusCode);
        AssertProblem(response, "not_found");
        Assert.Equal("private, no-store", response.Header("cache-control"));
        Assert.Equal([FragmentPrefix("reports", ReleaseA) + rest], store.KeysRead);
    }

    [Theory]
    [InlineData("/fragments/reports/")]
    [InlineData("/fragments/reports")]
    public async Task The_fragments_root_names_no_key_and_is_404(string path)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync();
        await using HostingHost _ = gateway;

        RawResponse response = await gateway.GetAsync(Shell.Host, path, Ct, Navigation);

        Assert.Equal(404, response.StatusCode);
        AssertProblem(response, "not_found");
        Assert.Equal("private, no-store", response.Header("cache-control"));
        Assert.Empty(store.KeysRead);
    }

    /// <summary>
    /// A refused read under a fragment answers as an absent asset does, 404, never the shell's 500:
    /// no key under a fragment is the app's document (contracts/http-contract.md).
    /// </summary>
    [Theory]
    [InlineData("/fragments/reports/index.html")]
    [InlineData("/fragments/reports/missing.js")]
    public async Task A_refused_read_under_a_fragment_is_404_and_marks_an_asset(string path)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync(telemetry: true, missingKeys: MissingKeyAnswer.AmbiguousRefusal);
        await using HostingHost _ = gateway;

        RawResponse response = await gateway.GetAsync(Shell.Host, path, Ct, Navigation);

        Assert.Equal(404, response.StatusCode);
        AssertProblem(response, "not_found");
        Assert.Equal("private, no-store", response.Header("cache-control"));
        Assert.Equal("asset", gateway.LastTelemetry?.ReadRefused);
        Assert.Single(store.KeysRead);
    }

    [Fact]
    public async Task A_store_failure_under_a_fragment_is_503()
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync();
        await using HostingHost _ = gateway;
        store.FailWith = new IOException("transport");

        RawResponse response = await gateway.GetAsync(Shell.Host, "/fragments/reports/entry.js", Ct);

        Assert.Equal(503, response.StatusCode);
        AssertProblem(response, "store_unavailable");
    }

    [Fact]
    public async Task HEAD_and_If_None_Match_work_under_a_fragment_as_under_the_shell()
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync();
        await using HostingHost _ = gateway;
        string key = FragmentPrefix("reports", ReleaseA) + "entry.js";
        string etag = await store.ETagOfAsync(key);

        RawResponse head = await gateway.SendAsync("HEAD", Shell.Host, "/fragments/reports/entry.js", Ct);
        Assert.Equal(200, head.StatusCode);
        Assert.Empty(head.BodyText);

        RawResponse conditional = await gateway.GetAsync(Shell.Host, "/fragments/reports/entry.js", Ct, $"If-None-Match: {etag}\r\n");
        Assert.Equal(304, conditional.StatusCode);
        Assert.Equal([key, key], store.KeysRead);
    }

    // ---- Reserved outright, and never served.

    [Theory]
    [InlineData("/fragments/reports/api/leak.json")]
    [InlineData("/fragments/reports/API/leak.json")]
    [InlineData("/fragments/reports/api")]
    [InlineData("/fragments/unknown/api/x")]
    public async Task A_fragments_api_namespace_is_never_served_from_a_release(string path)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync(telemetry: true);
        await using HostingHost _ = gateway;

        RawResponse response = await gateway.GetAsync(Shell.Host, path, Ct, Navigation);

        Assert.Equal(404, response.StatusCode);
        Assert.DoesNotContain(Leak, response.BodyText, StringComparison.Ordinal);
        Assert.Equal(RuleIds.PathReserved, gateway.LastTelemetry?.Rule);
        Assert.Empty(store.KeysRead);
    }

    /// <summary>
    /// No request path maps to a claim (contracts/s3-layout.md): a claim key sits beside
    /// <c>releases/</c>, and a fragment's path maps under <c>releases/{release}/</c> only, so the closest
    /// a request comes is a key under the release that the release does not hold.
    /// </summary>
    [Theory]
    [InlineData("/fragments/reports/claims/assets/chunk-only-a.js")]
    [InlineData("/fragments/reports/../claims/assets/chunk-only-a.js")]
    [InlineData("/fragments/reports/%2e%2e/claims/assets/chunk-only-a.js")]
    [InlineData("/fragments/reports/..%2Fclaims/assets/chunk-only-a.js")]
    [InlineData("/fragments/reports/../../claims/assets/chunk-only-a.js")]
    [InlineData("/claims/assets/chunk-only-a.js")]
    public async Task A_claim_key_is_never_served(string path)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync();
        await using HostingHost _ = gateway;

        RawResponse response = await gateway.GetAsync(Shell.Host, path, Ct, Navigation);

        Assert.NotEqual(200, response.StatusCode);
        Assert.DoesNotContain(Leak, response.BodyText, StringComparison.Ordinal);
        Assert.DoesNotContain(store.KeysRead, key => key.Contains("/claims/", StringComparison.Ordinal) && !key.Contains("/releases/", StringComparison.Ordinal));
    }

    // ---- Fragment activation is request-time.

    /// <summary>
    /// Activation A, then B, then A again (contracts/http-contract.md, "Fragment activation"): each
    /// registry change is served to the requests that follow with no restart, the shell's release
    /// answers the same bytes throughout, and a chunk A has and B lacks is 404 <c>private, no-store</c>
    /// under B, so a reload is the recovery.
    /// </summary>
    [Fact]
    public async Task Activation_A_then_B_then_A_is_served_with_no_restart_and_the_shell_unchanged()
    {
        (HostingHost gateway, RecordingObjectStore _) = await StartAsync();
        await using HostingHost __ = gateway;
        AppDefinition onB = Shell with { Fragments = [Reports with { Release = ReleaseB }, Audit], Fingerprint = "sha256:on-b" };

        await AssertOn("A", chunkOnlyA: 200);

        gateway.Registry.Swap(Snapshots.With(2, onB, Plain));
        await AssertOn("B", chunkOnlyA: 404);

        gateway.Registry.Swap(Snapshots.With(3, Shell, Plain));
        await AssertOn("A", chunkOnlyA: 200);

        async Task AssertOn(string release, int chunkOnlyA)
        {
            Assert.Equal($"export const release = '{release}';", (await gateway.GetAsync(Shell.Host, "/fragments/reports/entry.js", Ct)).BodyText);
            Assert.Equal("chunk both", (await gateway.GetAsync(Shell.Host, "/fragments/reports/assets/chunk-both.js", Ct)).BodyText);

            RawResponse chunk = await gateway.GetAsync(Shell.Host, "/fragments/reports/assets/chunk-only-a.js", Ct);
            Assert.Equal(chunkOnlyA, chunk.StatusCode);
            if (chunkOnlyA == 404)
            {
                Assert.Equal("private, no-store", chunk.Header("cache-control"));
            }

            RawResponse shell = await gateway.GetAsync(Shell.Host, "/", Ct, Navigation);
            Assert.Equal((200, "<p>shell</p>"), (shell.StatusCode, shell.BodyText));

            // The other fragment is untouched by its sibling's activation.
            Assert.Equal("export const audit = 1;", (await gateway.GetAsync(Shell.Host, "/fragments/audit/remote.js", Ct)).BodyText);
        }
    }

    /// <summary>The control: the shell's own paths are served from the shell's release as before.</summary>
    [Fact]
    public async Task A_shell_with_fragments_still_serves_its_own_release_at_its_own_paths()
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync();
        await using HostingHost _ = gateway;

        RawResponse response = await gateway.GetAsync(Shell.Host, "/reports/42", Ct, Navigation);

        Assert.Equal((200, "<p>shell</p>"), (response.StatusCode, response.BodyText));
        Assert.Equal([ShellPrefix(Shell) + "index.html"], store.KeysRead);
    }

    private static void AssertProblem(RawResponse response, string code)
    {
        Assert.Equal("application/problem+json", response.Header("content-type"));
        Assert.Contains($"\"code\":\"{code}\"", response.BodyText, StringComparison.Ordinal);
    }
}
