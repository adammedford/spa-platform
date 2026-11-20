using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Hosting.Tests;

/// <summary>
/// <c>UsePlatformHostResolution</c>'s fragment pin (#992; contracts/pipeline.md, stage
/// <c>UsePlatformHostResolution</c> and "Which token set a request uses"): a request under
/// <c>/fragments/{fragmentId}/</c>, where <c>{fragmentId}</c> names a fragment of the host's shell in
/// the pinned snapshot, carries <see cref="IFragmentFeature"/> beside <see cref="IAppFeature"/>, from
/// the same snapshot; every other request carries none, and the stage answers nothing for an unknown
/// <c>fragmentId</c>.
/// </summary>
public sealed class FragmentResolutionTests
{
    private static readonly FragmentDefinition Reports = TestApps.Fragment("reports", release: "2026-11-01-aaaa111");
    private static readonly FragmentDefinition Audit = TestApps.Fragment("audit", release: "2026-11-02-bbbb222");

    private static readonly AppDefinition Shell = TestApps.Entry("app1", fragments: [Reports, Audit]);

    // A host whose shell has no fragments, and one whose shell has a fragment app1's does not.
    private static readonly AppDefinition Plain = TestApps.Entry("app2");
    private static readonly AppDefinition Other = TestApps.Entry("app3", fragments: [TestApps.Fragment("ledger", appId: "app3")]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HostingHost> StartAsync() => HostingHost.StartAsync(Snapshots.With(7, Shell, Plain, Other), Ct);

    [Theory]
    [InlineData("/fragments/reports/entry.js", "reports")]
    [InlineData("/fragments/reports/assets/chunk-1a2b.js", "reports")]
    [InlineData("/fragments/reports/api/reports/42", "reports")]
    [InlineData("/fragments/reports/", "reports")]
    [InlineData("/fragments/audit/entry.js", "audit")]
    [InlineData("/FRAGMENTS/audit/entry.js", "audit")]
    public async Task A_request_under_a_known_fragments_namespace_has_it_pinned_beside_the_app(string path, string fragmentId)
    {
        await using HostingHost gateway = await StartAsync();

        RawResponse response = await gateway.GetAsync(Shell.Host, path, Ct);

        Assert.Equal(200, response.StatusCode);
        (FragmentDefinition fragment, AppDefinition app) = Assert.NotNull(gateway.LastFragment);
        Assert.Equal(fragmentId, fragment.FragmentId);

        // The fragment is the pinned entry's own, the same instance, not a copy found elsewhere.
        Assert.Contains(app.Fragments, listed => ReferenceEquals(listed, fragment));
        Assert.Equal(("app1", Shell.Host, 7L), gateway.LastSeen);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/assets/app.js")]
    [InlineData("/reports/entry.js")]
    [InlineData("/fragmentsx/reports/entry.js")]
    [InlineData("/fragments")]
    [InlineData("/fragments/")]
    [InlineData("/fragments/unknown/entry.js")]
    [InlineData("/fragments/Reports/entry.js")]
    [InlineData("/fragments/ledger/entry.js")]
    [InlineData("/signin-oidc/reports")]
    [InlineData("/api/reports/42")]
    public async Task A_shell_path_or_an_unknown_fragmentId_carries_no_fragment_and_passes(string path)
    {
        await using HostingHost gateway = await StartAsync();

        RawResponse response = await gateway.GetAsync(Shell.Host, path, Ct);

        // This stage answers nothing for a fragmentId it does not know: the later stages answer it.
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(("app1", Shell.Host, 7L), gateway.LastSeen);
        Assert.Null(gateway.LastFragment);
    }

    [Fact]
    public async Task A_host_whose_shell_has_no_fragments_pins_none()
    {
        await using HostingHost gateway = await StartAsync();

        RawResponse response = await gateway.GetAsync(Plain.Host, "/fragments/reports/entry.js", Ct);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(("app2", Plain.Host, 7L), gateway.LastSeen);
        Assert.Null(gateway.LastFragment);
    }

    /// <summary>
    /// The pin comes from the snapshot the request resolved against: a swap mid-request that changes
    /// the fragment's release, or removes the fragment, does not reach the request already resolved.
    /// </summary>
    [Fact]
    public async Task The_fragment_comes_from_the_same_snapshot_as_the_app()
    {
        await using HostingHost gateway = await StartAsync();
        FragmentDefinition activated = Reports with { Release = "2026-11-09-cccc333" };
        gateway.DuringRequest = () => gateway.Registry.Swap(
            Snapshots.With(8, Shell with { Fragments = [activated, Audit], Fingerprint = "sha256:next" }, Plain, Other));

        await gateway.GetAsync(Shell.Host, "/fragments/reports/entry.js", Ct);

        (FragmentDefinition fragment, AppDefinition app) = Assert.NotNull(gateway.LastFragment);
        Assert.Equal(7L, gateway.LastSeen?.Revision);
        Assert.Equal(Reports.Release, fragment.Release);
        Assert.Same(app.Fragments[0], fragment);

        // The next request resolves against the new snapshot.
        gateway.DuringRequest = null;
        await gateway.GetAsync(Shell.Host, "/fragments/reports/entry.js", Ct);
        Assert.Equal(8L, gateway.LastSeen?.Revision);
        Assert.Equal(activated.Release, gateway.LastFragment?.Fragment.Release);

        // And a fragment the new snapshot removes is no longer pinned.
        gateway.Registry.Swap(Snapshots.With(9, Shell with { Fragments = [Audit], Fingerprint = "sha256:removed" }, Plain, Other));
        await gateway.GetAsync(Shell.Host, "/fragments/reports/entry.js", Ct);
        Assert.Equal(9L, gateway.LastSeen?.Revision);
        Assert.Null(gateway.LastFragment);
    }

    /// <summary>
    /// Host resolution reads <c>Current</c> once and pins the fragment from that read: under a registry
    /// that moves on at every read, a second read for the fragment would pin the next snapshot's.
    /// </summary>
    [Fact]
    public async Task The_fragment_is_pinned_from_the_one_read_of_the_registry()
    {
        FragmentDefinition activated = Reports with { Release = "2026-11-09-cccc333" };
        RegistrySnapshot next = Snapshots.With(8, Shell with { Fragments = [activated, Audit], Fingerprint = "sha256:next" }, Plain, Other);
        await using HostingHost gateway = await HostingHost.StartAsync(
            Snapshots.With(7, Shell, Plain, Other), Ct, registryAs: inner => new MovingRegistry(inner, next));

        await gateway.GetAsync(Shell.Host, "/fragments/reports/entry.js", Ct);

        Assert.Equal(7L, gateway.LastSeen?.Revision);
        Assert.Equal(Reports.Release, gateway.LastFragment?.Fragment.Release);
    }

    /// <summary>Answers each read of <c>Current</c>, then swaps to <paramref name="next"/>.</summary>
    private sealed class MovingRegistry(InMemoryRegistry inner, RegistrySnapshot next) : IRegistry
    {
        public RegistrySnapshot Current
        {
            get
            {
                RegistrySnapshot current = inner.Current;
                if (!ReferenceEquals(current, next))
                {
                    inner.Swap(next);
                }

                return current;
            }
        }

        public IDisposable OnSwapped(Action<RegistrySnapshot, RegistrySnapshot> oldThenNew) => inner.OnSwapped(oldThenNew);
    }
}
