using Gateway.TestHarness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Abstractions;
using Platform.Registry;

namespace Gateway.IntegrationTests.Harness;

/// <summary>
/// Task 6.1a. The gateway's own poller runs beside the harness, and its tick is not ordered against a
/// test's write: on a slow runner it lands between <see cref="GatewayHarness.UpdateRegistryAsync"/>'s
/// write and that call's own poll, swaps the new revision in, and the call's conditional read then
/// answers 304. CI run 38849080050 failed two rows on exactly that. These tests force the tick into that
/// gap, so the race happens on every run rather than on a slow one.
/// </summary>
public sealed class HarnessRegistryRaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_update_a_background_poll_made_live_first_still_reports_swapped()
    {
        ReadGate gate = new();
        await using GatewayHarness harness = await GatewayHarness.StartAsync(gate.Options(), Ct);
        string before = await RegistryETagAsync(harness);

        // The "tick": a poll that has begun and is held at its read of the bucket, so it holds the
        // source's one-poll-at-a-time lock. Whichever poll reaches the bucket first is held, the
        // poller's own or this one; either way one poll is in flight and the update queues behind it.
        gate.HoldNextRead();
        Task<RefreshOutcome> tick = harness.RefreshRegistryAsync(Ct);
        await gate.Held.WaitAsync(Ct);

        Task<RefreshOutcome> update = harness.UpdateRegistryAsync(document => document["apps"]!.AsArray().RemoveAt(1), Ct);
        await WaitForWriteAsync(harness, before);

        // The held poll now reads revision 2 and swaps it in; the update's own poll comes second.
        gate.Release();
        await tick;
        RefreshOutcome outcome = await update;

        Assert.Equal(2, harness.Registry.Current.Revision);
        Assert.Equal(RefreshOutcome.Swapped, outcome);
    }

    [Fact]
    public async Task A_rejected_update_a_background_poll_read_first_is_not_reported_swapped()
    {
        ReadGate gate = new();
        await using GatewayHarness harness = await GatewayHarness.StartAsync(gate.Options(), Ct);
        string before = await RegistryETagAsync(harness);

        gate.HoldNextRead();
        Task<RefreshOutcome> tick = harness.RefreshRegistryAsync(Ct);
        await gate.Held.WaitAsync(Ct);

        Task<RefreshOutcome> update = harness.PublishRegistryTextAsync("{ not json", Ct);
        await WaitForWriteAsync(harness, before);

        gate.Release();
        await tick;
        RefreshOutcome outcome = await update;

        // The background poll rejected it and settled its ETag, so the update's poll saw it unchanged:
        // the written document is not live, and the harness must not say it is.
        Assert.Equal(1, harness.Registry.Current.Revision);
        Assert.NotEqual(RefreshOutcome.Swapped, outcome);
    }

    private static async Task<string> RegistryETagAsync(GatewayHarness harness)
    {
        StoredObject current = await harness.Store.GetAsync(GatewayHarness.RegistryKey, ifNoneMatch: null, Ct)
            ?? throw new InvalidOperationException("the registry object is missing.");
        current.Body?.Dispose();
        return current.ETag;
    }

    /// <summary>Waits until the harness's write has replaced the registry object.</summary>
    private static async Task WaitForWriteAsync(GatewayHarness harness, string before)
    {
        while (await RegistryETagAsync(harness) == before)
        {
            await Task.Delay(1, Ct);
        }
    }

    /// <summary>
    /// Wraps the store the harness registered, the way the negative suite's recorder does, and can hold
    /// the gateway's next read of the registry object until the test releases it.
    /// </summary>
    private sealed class ReadGate
    {
        private readonly TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int armed;

        /// <summary>Completes when a read has arrived and is being held.</summary>
        public Task Held => held.Task;

        public void HoldNextRead() => Volatile.Write(ref armed, 1);

        public void Release() => released.TrySetResult();

        public GatewayHarnessOptions Options() => new()
        {
            ConfigureServices = services =>
            {
                IObjectStore store = services.Last(service => service.ServiceType == typeof(IObjectStore)).ImplementationInstance as IObjectStore
                    ?? throw new InvalidOperationException("the harness no longer registers IObjectStore as an instance.");
                services.RemoveAll<IObjectStore>();
                services.AddSingleton<IObjectStore>(new Gated(store, this));
            },
        };

        private async Task PassAsync(string key, CancellationToken ct)
        {
            if (key == GatewayHarness.RegistryKey && Interlocked.Exchange(ref armed, 0) == 1)
            {
                held.TrySetResult();
                await released.Task.WaitAsync(ct);
            }
        }

        private sealed class Gated(IObjectStore inner, ReadGate gate) : IObjectStore
        {
            public async Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct)
            {
                await gate.PassAsync(key, ct);
                return await inner.GetAsync(key, ifNoneMatch, ct);
            }

            public Task<bool> PutAsync(string key, Stream body, string? ifMatch, CancellationToken ct) =>
                inner.PutAsync(key, body, ifMatch, ct);
        }
    }
}
