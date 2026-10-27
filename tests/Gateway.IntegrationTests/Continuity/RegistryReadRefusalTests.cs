using System.Net;
using Gateway.TestHarness;
using Platform.Registry;
using Platform.Testing;

namespace Gateway.IntegrationTests.Continuity;

/// <summary>
/// <c>/readyz</c> when the registry read is refused (#429; change request
/// contracts/changes/issue-381-object-read-refusal.md, "What it leaves unchanged": the registry fails
/// loud already). <c>RegistrySource</c> reads a refusal as <see cref="RefreshOutcome.Failed"/>, never as
/// <see cref="RefreshOutcome.Missing"/>, and workstream 2's tests pin that; what <c>/readyz</c> answers
/// is <c>UsePlatformHealth</c>'s, in <c>Platform.Hosting</c>, which the registry's tests cannot reach
/// (A-03), so it is asserted here through the composed gateway.
///
/// <para>Before the first snapshot the bucket holds no registry object and answers in refusal mode, as
/// S3 answers the GetObject-only role for a key that is missing. After one, the object is there and the
/// role is denied it (<see cref="SwitchableObjectStore"/>): the other case a refusal stands for.</para>
/// </summary>
public sealed class RegistryReadRefusalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(MissingKeyAnswer.AmbiguousRefusal, RefreshOutcome.Failed)]
    [InlineData(MissingKeyAnswer.ConfirmedAbsence, RefreshOutcome.Missing)]
    public async Task Before_the_first_snapshot_an_unreadable_registry_leaves_readyz_503(MissingKeyAnswer bucket, RefreshOutcome outcome)
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(
            new GatewayHarnessOptions { MissingKeys = bucket, RegistryAtStart = false }, Ct);

        RefreshOutcome polled = await harness.RefreshRegistryAsync(Ct);
        (HttpStatusCode status, long? revision, _) = await harness.Hosts[0].ReadyzAsync(Ct);

        // The refusal row is the one #429 asks for, and the confirmed-absence row is its control: both
        // are not ready, and only the outcome tells a refused read from a missing registry. Mutations
        // that turn it red: RegistrySource reading ObjectReadRefusedException as null makes the refusal
        // row's outcome Missing; the harness writing the registry whatever RegistryAtStart says makes
        // both rows 200 at revision 1.
        Assert.Equal(outcome, polled);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Null(revision);
        Assert.Equal(0, harness.Registry.Current.Revision);

        // Once the registry is published, the same host goes ready at its revision: the refusal was the
        // missing key, not a fault that outlives it. Mutation that turns it red: the harness reading the
        // object's ETag before its first write, which the refusal-mode bucket refuses.
        Assert.Equal(RefreshOutcome.Swapped, await harness.UpdateRegistryAsync(_ => { }, Ct));
        Assert.Equal((HttpStatusCode.OK, 1L), Ready(await harness.Hosts[0].ReadyzAsync(Ct)));
    }

    [Fact]
    public async Task After_a_snapshot_a_refused_registry_read_keeps_readyz_200_at_the_live_revision()
    {
        await using GatewayHarness harness = await GatewayHarness.StartAsync(
            new GatewayHarnessOptions { MissingKeys = MissingKeyAnswer.AmbiguousRefusal }, Ct);
        long live = harness.Registry.Current.Revision;
        Assert.Equal((HttpStatusCode.OK, live), Ready(await harness.Hosts[0].ReadyzAsync(Ct)));

        harness.GatewayStore.Refuse(GatewayHarness.RegistryKey);
        int refusedBefore = harness.GatewayStore.Refusals;
        RefreshOutcome polled = await harness.RefreshRegistryAsync(Ct);

        // The poll reached the refusal, read it as a failure, and the live snapshot stayed: a gateway
        // that cannot read its registry keeps serving the last one (contracts/pipeline.md, Health
        // endpoints). Mutations that turn it red: the switch not refusing (Refuse a no-op) makes the
        // outcome NotModified and the refusal count unchanged; a failed poll that cleared the live
        // snapshot would answer 503.
        Assert.Equal(RefreshOutcome.Failed, polled);
        Assert.True(harness.GatewayStore.Refusals > refusedBefore, "the registry poll never reached the refusal.");
        Assert.Equal((HttpStatusCode.OK, live), Ready(await harness.Hosts[0].ReadyzAsync(Ct)));
        Assert.Equal(1, live);
    }

    private static (HttpStatusCode, long?) Ready((HttpStatusCode Status, long? Revision, string? Instance) answer) =>
        (answer.Status, answer.Revision);
}
