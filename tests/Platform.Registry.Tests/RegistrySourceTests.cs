using System.Collections.Frozen;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Registry.Tests;

/// <summary>
/// Plan row 2.2: conditional GET, secret preload, validate, swap, then notify. Any failure keeps the
/// last good snapshot; at a cold start an app whose secret cannot be read is left out and reported;
/// nothing is ready until the first valid snapshot. Driven one poll at a time through
/// <see cref="RegistrySource.RefreshAsync"/>, against the Platform.Testing doubles.
/// </summary>
public sealed class RegistrySourceTests
{
    private const string Key = "registry/registry.json";
    private const string App1Ref = "ssm:/spa-platform/poc/apps/app1/client-secret:1";
    private const string App2Ref = "ssm:/spa-platform/poc/apps/app2/client-secret:1";

    // Canaries: a secret value must never reach a log line (N-16).
    private const string App1Secret = "canary-app1-secret-value";
    private const string App2Secret = "canary-app2-secret-value";

    private readonly RecordingObjectStore store = new(new InMemoryObjectStore());
    private readonly FakeClientSecretSource secrets = new() { [App1Ref] = App1Secret };
    private readonly ListLoggerProvider logs = new();
    private readonly LiveRegistry registry;
    private readonly RegistrySource source;

    public RegistrySourceTests()
    {
        registry = new LiveRegistry(logs.CreateLogger<LiveRegistry>());
        source = new RegistrySource(store, secrets, Examples.Policy(), registry, Key, logs.CreateLogger<RegistrySource>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Before_any_document_is_read_nothing_is_live()
    {
        // contracts/pipeline.md, Health endpoints: /readyz is 503 before a snapshot. Revision 0 is that
        // state: a document's revision is at least 1 (registry.schema.json).
        Assert.Equal(0, registry.Current.Revision);
        Assert.Empty(registry.Current.ByHost);
        Assert.Empty(registry.Current.ByAppId);
        Assert.Same(LiveRegistry.NoSnapshot, registry.Current);
    }

    [Fact]
    public async Task A_valid_document_at_cold_start_goes_live_and_notifies_after_the_swap()
    {
        await Publish(Examples.Registry());
        List<(RegistrySnapshot Old, RegistrySnapshot New, RegistrySnapshot CurrentDuringCallback)> seen = [];
        using IDisposable _ = registry.OnSwapped((old, next) => seen.Add((old, next, registry.Current)));

        Assert.Equal(RefreshOutcome.Swapped, await source.RefreshAsync(Ct));

        Assert.Equal(42, registry.Current.Revision);
        Assert.Equal(store.LastETag, registry.Current.ETag);
        Assert.Equal("app1", registry.Current.ByHost["app1.spa-poc.test:8443"].AppId);
        var (old, next, during) = Assert.Single(seen);
        Assert.Same(LiveRegistry.NoSnapshot, old);
        Assert.Same(registry.Current, next);
        // IRegistry.OnSwapped: raised after the swap, never before.
        Assert.Same(next, during);
    }

    [Fact]
    public async Task Secrets_are_preloaded_before_the_swap()
    {
        await Publish(Examples.RegistryWithSecondEntry());
        secrets[App2Ref] = App2Secret;

        await source.RefreshAsync(Ct);

        Assert.Equal([App1Ref, App2Ref], secrets.Preloaded.Order(StringComparer.Ordinal));
        Assert.Equal(App2Secret, secrets.Get(registry.Current.ByAppId["app2"].OAuth.ClientSecretRef));
    }

    [Fact]
    public async Task A_missing_registry_object_leaves_the_gateway_not_ready()
    {
        Assert.Equal(RefreshOutcome.Missing, await source.RefreshAsync(Ct));

        Assert.Same(LiveRegistry.NoSnapshot, registry.Current);
        Assert.Contains(logs.Lines, line => line.Contains(Key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_bad_first_document_leaves_the_gateway_not_ready()
    {
        await Publish(Examples.RegistryEntry(entry => entry["host"] = "app1.elsewhere.test"));

        Assert.Equal(RefreshOutcome.Rejected, await source.RefreshAsync(Ct));

        Assert.Same(LiveRegistry.NoSnapshot, registry.Current);
    }

    [Fact]
    public async Task A_document_that_is_not_json_changes_nothing()
    {
        RegistrySnapshot live = await GoLive(Examples.Registry());
        CountSwaps();
        await Replace("{\"schemaVersion\": 1, \"revision\": 43, \"apps\": [");

        Assert.Equal(RefreshOutcome.Rejected, await source.RefreshAsync(Ct));

        Assert.Same(live, registry.Current);
        Assert.Equal(0, swaps);
        Assert.Contains(logs.Lines, line => line.Contains(RegistryRules.NotJson, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_document_that_breaks_a_rule_changes_nothing()
    {
        RegistrySnapshot live = await GoLive(Examples.Registry());
        CountSwaps();
        await Replace(Examples.RegistryWithSecondEntry(second => second["host"] = "app1.spa-poc.test:8443")
            .WithRevision(43));

        Assert.Equal(RefreshOutcome.Rejected, await source.RefreshAsync(Ct));

        Assert.Same(live, registry.Current);
        Assert.Equal(0, swaps);
        Assert.Contains(logs.Lines, line => line.Contains(RegistryRules.DuplicateHost, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_document_whose_revision_does_not_rise_changes_nothing()
    {
        RegistrySnapshot live = await GoLive(Examples.Registry());
        CountSwaps();
        await Replace(Examples.RegistryWithSecondEntry());   // still revision 42

        Assert.Equal(RefreshOutcome.Rejected, await source.RefreshAsync(Ct));

        Assert.Same(live, registry.Current);
        Assert.Equal(0, swaps);
    }

    [Fact]
    public async Task A_newer_valid_document_replaces_the_live_one()
    {
        RegistrySnapshot live = await GoLive(Examples.Registry());
        secrets[App2Ref] = App2Secret;
        await Replace(Examples.RegistryWithSecondEntry().WithRevision(43));
        List<(long Old, long New)> seen = [];
        using IDisposable _ = registry.OnSwapped((old, next) => seen.Add((old.Revision, next.Revision)));

        Assert.Equal(RefreshOutcome.Swapped, await source.RefreshAsync(Ct));

        Assert.Equal(43, registry.Current.Revision);
        Assert.Equal(2, registry.Current.ByAppId.Count);
        Assert.Equal([(42L, 43L)], seen);
        Assert.NotSame(live, registry.Current);
    }

    [Fact]
    public async Task An_unchanged_document_costs_no_body_no_validation_and_no_preload()
    {
        await GoLive(Examples.Registry());
        int preloads = secrets.Preloaded.Count;

        Assert.Equal(RefreshOutcome.NotModified, await source.RefreshAsync(Ct));

        Assert.Equal(registry.Current.ETag, store.Reads[^1].IfNoneMatch);
        Assert.True(store.Reads[^1].NotModified);
        Assert.Equal(preloads, secrets.Preloaded.Count);
    }

    [Fact]
    public async Task A_rejected_document_is_not_read_again_until_it_changes()
    {
        await GoLive(Examples.Registry());
        await Replace("not json");
        await source.RefreshAsync(Ct);
        string rejected = store.LastETag;

        Assert.Equal(RefreshOutcome.NotModified, await source.RefreshAsync(Ct));

        Assert.Equal(rejected, store.Reads[^1].IfNoneMatch);
        Assert.Equal(42, registry.Current.Revision);
    }

    [Fact]
    public async Task A_store_failure_keeps_the_last_good_snapshot()
    {
        RegistrySnapshot live = await GoLive(Examples.Registry());
        store.FailNextRead = new IOException("the network went away");

        Assert.Equal(RefreshOutcome.Failed, await source.RefreshAsync(Ct));

        Assert.Same(live, registry.Current);
    }

    [Fact]
    public async Task One_unreadable_secret_at_cold_start_costs_one_app_not_all()
    {
        // app2's secret is never set, so it cannot be read.
        await Publish(Examples.RegistryWithSecondEntry());

        Assert.Equal(RefreshOutcome.Swapped, await source.RefreshAsync(Ct));

        Assert.Equal(42, registry.Current.Revision);
        Assert.Equal(["app1"], registry.Current.ByAppId.Keys);
        Assert.Equal(["app1.spa-poc.test:8443"], registry.Current.ByHost.Keys);
        Assert.Equal(["app2"], source.LeftOut);
        // Reported by app and by reference, never by value.
        Assert.Contains(logs.Lines, line => line.Contains("app2", StringComparison.Ordinal) && line.Contains(App2Ref, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, line => line.Contains(App1Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_app_left_out_at_cold_start_joins_once_its_secret_can_be_read()
    {
        await Publish(Examples.RegistryWithSecondEntry());
        await source.RefreshAsync(Ct);
        RegistrySnapshot partial = registry.Current;
        secrets[App2Ref] = App2Secret;
        List<(RegistrySnapshot Old, RegistrySnapshot New)> seen = [];
        using IDisposable _ = registry.OnSwapped((old, next) => seen.Add((old, next)));

        // The document has not changed, so the read is a 304; the retry is of the secret alone.
        Assert.Equal(RefreshOutcome.Swapped, await source.RefreshAsync(Ct));

        Assert.True(store.Reads[^1].NotModified);
        Assert.Equal(["app1", "app2"], registry.Current.ByAppId.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(42, registry.Current.Revision);
        Assert.Empty(source.LeftOut);
        Assert.Same(partial, Assert.Single(seen).Old);

        // Complete now, so the next unchanged poll is a plain 304 again.
        Assert.Equal(RefreshOutcome.NotModified, await source.RefreshAsync(Ct));
    }

    [Fact]
    public async Task An_app_still_unreadable_on_retry_stays_out_without_a_swap()
    {
        await Publish(Examples.RegistryWithSecondEntry());
        await source.RefreshAsync(Ct);
        RegistrySnapshot partial = registry.Current;
        CountSwaps();

        Assert.Equal(RefreshOutcome.NotModified, await source.RefreshAsync(Ct));

        Assert.Same(partial, registry.Current);
        Assert.Equal(0, swaps);
        Assert.Equal(["app2"], source.LeftOut);
    }

    [Fact]
    public async Task Every_secret_unreadable_at_cold_start_leaves_the_gateway_not_ready()
    {
        // Decided by implementer: a document that names apps, none of which can go live, is not a
        // snapshot worth serving. Staying not ready keeps the ALB on the tasks that still work.
        FakeClientSecretSource none = new();
        RegistrySource coldSource = new(store, none, Examples.Policy(), registry, Key, logs.CreateLogger<RegistrySource>());
        await Publish(Examples.RegistryWithSecondEntry());

        Assert.Equal(RefreshOutcome.SecretsUnreadable, await coldSource.RefreshAsync(Ct));

        Assert.Same(LiveRegistry.NoSnapshot, registry.Current);
    }

    [Fact]
    public async Task A_document_with_no_apps_is_a_valid_snapshot()
    {
        // contracts/README.md: an empty apps array is a valid document. Refusing to deploy against
        // one is task 1.7's rule, not the gateway's.
        await Publish(Examples.Registry(document => document["apps"] = new JsonArray()));

        Assert.Equal(RefreshOutcome.Swapped, await source.RefreshAsync(Ct));

        Assert.Equal(42, registry.Current.Revision);
        Assert.Empty(registry.Current.ByAppId);
    }

    [Fact]
    public async Task An_unreadable_secret_in_a_new_document_keeps_the_live_snapshot_whole()
    {
        // contracts/README.md, clientSecretRef: a snapshot that cannot read one is rejected and the
        // live snapshot stays (D3: a snapshot with a missing secret never replaces a working one).
        RegistrySnapshot live = await GoLive(Examples.Registry());
        CountSwaps();
        await Replace(Examples.RegistryWithSecondEntry().WithRevision(43));

        Assert.Equal(RefreshOutcome.SecretsUnreadable, await source.RefreshAsync(Ct));

        Assert.Same(live, registry.Current);
        Assert.Equal(0, swaps);
        Assert.Contains(logs.Lines, line => line.Contains(App2Ref, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_document_rejected_for_its_secrets_is_tried_again_on_the_next_poll()
    {
        await GoLive(Examples.Registry());
        await Replace(Examples.RegistryWithSecondEntry().WithRevision(43));
        await source.RefreshAsync(Ct);
        secrets[App2Ref] = App2Secret;

        Assert.Equal(RefreshOutcome.Swapped, await source.RefreshAsync(Ct));

        Assert.Equal(43, registry.Current.Revision);
        Assert.Equal(2, registry.Current.ByAppId.Count);
    }

    [Fact]
    public async Task A_subscriber_that_throws_stops_neither_the_swap_nor_the_others()
    {
        await Publish(Examples.Registry());
        bool second = false;
        using IDisposable a = registry.OnSwapped((_, _) => throw new InvalidOperationException("evictor failed"));
        using IDisposable b = registry.OnSwapped((_, _) => second = true);

        Assert.Equal(RefreshOutcome.Swapped, await source.RefreshAsync(Ct));

        Assert.True(second);
        Assert.Equal(42, registry.Current.Revision);
        Assert.Contains(logs.Lines, line => line.Contains("evictor failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unsubscribed_callback_is_not_called()
    {
        await Publish(Examples.Registry());
        bool called = false;
        registry.OnSwapped((_, _) => called = true).Dispose();

        await source.RefreshAsync(Ct);

        Assert.False(called);
    }

    [Fact]
    public async Task Concurrent_readers_never_see_a_partial_snapshot()
    {
        // Revision r names r apps, app1 to app{r}, each under its own host. A reader that ever saw a
        // snapshot whose two indexes disagree with each other or with its revision saw a partial one.
        const int Revisions = 40;
        for (int r = 1; r <= Revisions; r++)
        {
            secrets[$"ssm:/spa-platform/poc/apps/app{r}/client-secret:1"] = $"value-{r}";
        }

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        List<string> faults = [];
        int reads = 0;
        Task[] readers = [.. Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                RegistrySnapshot snapshot = registry.Current;
                Interlocked.Increment(ref reads);
                string? fault = Inconsistency(snapshot);
                if (fault is not null)
                {
                    lock (faults)
                    {
                        faults.Add(fault);
                    }
                }
            }
        }))];

        for (int r = 1; r <= Revisions; r++)
        {
            await Replace(Numbered(r));
            Assert.Equal(RefreshOutcome.Swapped, await source.RefreshAsync(Ct));
        }

        await stop.CancelAsync();
        await Task.WhenAll(readers);

        Assert.Empty(faults);
        Assert.True(reads > Revisions, $"the readers ran only {reads} times");
        Assert.Equal(Revisions, registry.Current.Revision);
    }

    [Fact]
    public void The_consistency_check_above_can_fail()
    {
        // An assertion must be able to fail (CODING_STANDARDS.md): a torn snapshot, whose indexes
        // disagree, is reported by the same check the concurrency test applies.
        AppDefinition app1 = TestApps.Entry("app1");
        AppDefinition app2 = TestApps.Entry("app2");
        RegistrySnapshot torn = new(2, "\"x\"",
            new Dictionary<string, AppDefinition> { [app1.Host] = app1 }.ToFrozenDictionary(),
            new Dictionary<string, AppDefinition> { [app1.AppId] = app1, [app2.AppId] = app2 }.ToFrozenDictionary());
        RegistrySnapshot whole = new(2, "\"x\"",
            new Dictionary<string, AppDefinition> { [app1.Host] = app1, [app2.Host] = app2 }.ToFrozenDictionary(),
            new Dictionary<string, AppDefinition> { [app1.AppId] = app1, [app2.AppId] = app2 }.ToFrozenDictionary());

        Assert.NotNull(Inconsistency(torn));
        Assert.Null(Inconsistency(whole));
    }

    private static string? Inconsistency(RegistrySnapshot snapshot)
    {
        long apps = snapshot.Revision;
        if (snapshot.ByAppId.Count != apps || snapshot.ByHost.Count != apps)
        {
            return $"revision {snapshot.Revision} with {snapshot.ByAppId.Count} appIds and {snapshot.ByHost.Count} hosts";
        }

        foreach (AppDefinition app in snapshot.ByAppId.Values)
        {
            if (!snapshot.ByHost.TryGetValue(app.Host, out AppDefinition? byHost) || !ReferenceEquals(byHost, app))
            {
                return $"revision {snapshot.Revision}: {app.AppId} is not under its own host";
            }
        }

        return null;
    }

    private int swaps;

    private void CountSwaps()
    {
        swaps = 0;
        registry.OnSwapped((_, _) => swaps++);
    }

    private static JsonObject Numbered(int revision) => Examples.Registry(document =>
    {
        JsonObject template = Examples.Entry(document, 0);
        JsonArray apps = [];
        for (int i = 1; i <= revision; i++)
        {
            JsonObject entry = template.DeepClone().AsObject();
            entry["appId"] = $"app{i}";
            entry["host"] = $"app{i}.spa-poc.test:8443";
            entry["oauth"]!["clientId"] = $"{i}example23456789";
            entry["oauth"]!["clientSecretRef"] = $"ssm:/spa-platform/poc/apps/app{i}/client-secret:1";
            apps.Add(entry);
        }

        document["apps"] = apps;
        document["revision"] = revision;
    });

    private async Task<RegistrySnapshot> GoLive(JsonObject document)
    {
        await Publish(document);
        Assert.Equal(RefreshOutcome.Swapped, await source.RefreshAsync(Ct));
        return registry.Current;
    }

    private Task Publish(JsonObject document) => Replace(document.ToJsonString());

    private Task Replace(JsonObject document) => Replace(document.ToJsonString());

    private async Task Replace(string text)
    {
        StoredObject? current = await store.Inner.GetAsync(Key, null, Ct);
        current?.Body?.Dispose();
        using MemoryStream body = new(Encoding.UTF8.GetBytes(text));
        Assert.True(await store.Inner.PutAsync(Key, body, current?.ETag, Ct));
    }
}

internal static class RegistryDocumentExtensions
{
    public static JsonObject WithRevision(this JsonObject document, long revision)
    {
        document["revision"] = revision;
        return document;
    }
}
