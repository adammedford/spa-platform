using Platform.Abstractions;

namespace Platform.Registry.Tests;

/// <summary>The snapshot a validated document becomes, for the registry source of task 2.2.</summary>
public sealed class SnapshotTests
{
    [Fact]
    public void A_valid_document_indexes_by_host_and_by_appId()
    {
        RegistryValidation result = RegistryValidator.Validate(Examples.RegistryWithSecondEntry().ToJsonString(), Examples.Policy(), null);

        RegistrySnapshot snapshot = result.ToSnapshot("\"etag-1\"");

        Assert.Equal(42, snapshot.Revision);
        Assert.Equal("\"etag-1\"", snapshot.ETag);
        Assert.Equal("app2", snapshot.ByHost["app2.spa-poc.test:8443"].AppId);
        Assert.Equal("app1.spa-poc.test:8443", snapshot.ByAppId["app1"].Host);
        Assert.Equal(2, snapshot.ByHost.Count);
    }

    [Fact]
    public void A_rejected_document_has_no_snapshot()
    {
        RegistryValidation result = RegistryValidator.Validate(
            Examples.RegistryWithSecondEntry(e => e["host"] = "app1.spa-poc.test:8443").ToJsonString(), Examples.Policy(), null);

        Assert.Throws<InvalidOperationException>(() => result.ToSnapshot("\"etag-1\""));
    }

    [Fact]
    public void The_revision_rule_reads_the_live_snapshot()
    {
        RegistrySnapshot live = RegistryValidator.Validate(Examples.Registry().ToJsonString(), Examples.Policy(), null).ToSnapshot("\"etag-1\"");

        Assert.False(RegistryValidator.Validate(Examples.Registry().ToJsonString(), Examples.Policy(), live.Revision).IsValid);
        Assert.True(RegistryValidator.Validate(Examples.Registry(d => d["revision"] = live.Revision + 1).ToJsonString(), Examples.Policy(), live.Revision).IsValid);
    }
}
