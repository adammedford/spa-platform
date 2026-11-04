using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Testing;

namespace Platform.Registry.Tests;

/// <summary>
/// Task 2.5: <c>IClientSecretSource</c> over Parameter Store, version-pinned references, cached in
/// memory by reference. Done when no value appears in a log or exception message (N-16 holds here).
/// The SDK client is the one seam: <see cref="StubParameterStore"/> overrides its read and answers
/// as Parameter Store does.
/// </summary>
public sealed class ParameterStoreClientSecretSourceTests
{
    private const string App1Name = "/spa-platform/poc/apps/app1/client-secret";
    private const string App2Name = "/spa-platform/poc/apps/app2/client-secret";
    private const string App1Ref = "ssm:" + App1Name + ":1";
    private const string App2Ref = "ssm:" + App2Name + ":1";
    private const string Secret = "correct-horse-battery-staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ParameterStoreClientSecretSource Source(StubParameterStore ssm) =>
        new(ssm, NullLogger<ParameterStoreClientSecretSource>.Instance);

    [Fact]
    public async Task A_preloaded_reference_returns_the_value_of_the_pinned_version()
    {
        StubParameterStore ssm = new();
        ssm.Put(App1Name, 1, Secret);
        ssm.Put(App1Name, 2, "a-later-version");
        ParameterStoreClientSecretSource source = Source(ssm);

        await source.PreloadAsync([App1Ref], Ct);

        Assert.Equal(Secret, source.Get(App1Ref));
        GetParameterRequest sent = Assert.Single(ssm.Requests);
        Assert.Equal(App1Name + ":1", sent.Name);
        Assert.True(sent.WithDecryption);
    }

    [Fact]
    public async Task A_rotated_reference_reads_the_new_version_while_the_old_one_still_answers()
    {
        // docs/plan/05-infrastructure-and-cost.md -> Rotating a client secret: the live snapshot keeps
        // the old reference until the swap, so both must answer from memory.
        StubParameterStore ssm = new();
        ssm.Put(App1Name, 1, Secret);
        ssm.Put(App1Name, 2, "the-rotated-secret");
        ParameterStoreClientSecretSource source = Source(ssm);

        await source.PreloadAsync([App1Ref], Ct);
        await source.PreloadAsync(["ssm:" + App1Name + ":2"], Ct);

        Assert.Equal(Secret, source.Get(App1Ref));
        Assert.Equal("the-rotated-secret", source.Get("ssm:" + App1Name + ":2"));
    }

    [Fact]
    public void A_reference_read_before_any_preload_throws_and_does_no_io()
    {
        StubParameterStore ssm = new();
        ssm.Put(App1Name, 1, Secret);
        ParameterStoreClientSecretSource source = Source(ssm);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => source.Get(App1Ref));

        Assert.Contains("not preloaded", ex.Message);
        Assert.Empty(ssm.Requests);
    }

    [Fact]
    public async Task One_unreadable_reference_costs_that_reference_and_not_the_batch()
    {
        // Task 2.2: "one unreadable secret at start-up costs one app, not all".
        StubParameterStore ssm = new();
        ssm.Put(App1Name, 1, Secret);
        ParameterStoreClientSecretSource source = Source(ssm);

        await source.PreloadAsync([App2Ref, App1Ref], Ct);

        Assert.Equal(Secret, source.Get(App1Ref));
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => source.Get(App2Ref));
        Assert.Contains("could not be read", ex.Message);
        Assert.Contains(App2Ref, ex.Message);
    }

    [Fact]
    public async Task Access_denied_on_one_reference_is_a_failure_of_that_reference()
    {
        StubParameterStore ssm = new();
        ssm.Put(App1Name, 1, Secret);
        ssm.FailWith(App2Name + ":1", () => new AmazonSimpleSystemsManagementException("denied")
        {
            ErrorCode = "AccessDeniedException",
        });
        ParameterStoreClientSecretSource source = Source(ssm);

        await source.PreloadAsync([App1Ref, App2Ref], Ct);

        Assert.Equal(Secret, source.Get(App1Ref));
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => source.Get(App2Ref));
        Assert.Contains("AccessDeniedException", ex.Message);
    }

    [Fact]
    public async Task A_client_failure_such_as_no_credentials_is_a_failure_of_that_reference()
    {
        StubParameterStore ssm = new();
        ssm.FailWith(App1Name + ":1", () => new AmazonClientException("Unable to get IAM security credentials"));
        ParameterStoreClientSecretSource source = Source(ssm);

        await source.PreloadAsync([App1Ref], Ct);

        Assert.Throws<InvalidOperationException>(() => source.Get(App1Ref));
    }

    /// <summary>
    /// The SDK does not wrap every transport failure: a refused connection reaches the caller as a bare
    /// <see cref="HttpRequestException"/> (seen by calling a real client at a closed port during the
    /// task 2.5 review), and an HTTP timeout as a <see cref="TaskCanceledException"/> nobody asked for.
    /// Either one, on one reference, must still cost only that reference.
    /// </summary>
    [Theory]
    [InlineData("network")]
    [InlineData("timeout")]
    public async Task A_transport_failure_on_one_reference_is_a_failure_of_that_reference(string kind)
    {
        StubParameterStore ssm = new();
        ssm.Put(App1Name, 1, Secret);
        ssm.FailWith(App2Name + ":1", () => kind == "network"
            ? new HttpRequestException("Connection refused (parameter store)")
            : new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
        ParameterStoreClientSecretSource source = Source(ssm);

        await source.PreloadAsync([App1Ref, App2Ref], Ct);

        Assert.Equal(Secret, source.Get(App1Ref));
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => source.Get(App2Ref));
        Assert.Contains("could not be read", ex.Message);
        Assert.DoesNotContain("Connection refused", ex.Message);
        Assert.DoesNotContain("HttpClient.Timeout", ex.Message);
    }

    [Fact]
    public async Task A_held_reference_is_served_from_memory_and_never_read_again()
    {
        StubParameterStore ssm = new();
        ssm.Put(App1Name, 1, Secret);
        ParameterStoreClientSecretSource source = Source(ssm);

        await source.PreloadAsync([App1Ref, App1Ref], Ct);
        await source.PreloadAsync([App1Ref], Ct);
        _ = source.Get(App1Ref);
        _ = source.Get(App1Ref);

        Assert.Single(ssm.Requests);
    }

    [Fact]
    public async Task A_reference_that_failed_is_read_again_by_the_next_preload()
    {
        StubParameterStore ssm = new();
        ParameterStoreClientSecretSource source = Source(ssm);
        await source.PreloadAsync([App1Ref], Ct);
        Assert.Throws<InvalidOperationException>(() => source.Get(App1Ref));

        ssm.Put(App1Name, 1, Secret);
        await source.PreloadAsync([App1Ref], Ct);

        Assert.Equal(Secret, source.Get(App1Ref));
        Assert.Equal(2, ssm.Requests.Count);
    }

    [Fact]
    public async Task An_answer_for_another_version_is_refused()
    {
        StubParameterStore ssm = new() { VersionOverride = 2 };
        ssm.Put(App1Name, 1, Secret);
        ParameterStoreClientSecretSource source = Source(ssm);

        await source.PreloadAsync([App1Ref], Ct);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => source.Get(App1Ref));
        Assert.Contains("version 2", ex.Message);
    }

    [Theory]
    [InlineData("/spa-platform/poc/apps/app1/client-secret:1")]
    [InlineData("ssm:/spa-platform/poc/apps/app1/client-secret")]
    [InlineData("ssm:/spa-platform/poc/apps/app1/client-secret:0")]
    [InlineData("ssm:/spa-platform/poc/apps/app1/client-secret:latest")]
    [InlineData("ssm:spa-platform/poc/apps/app1/client-secret:1")]
    [InlineData("ssm::1")]
    public async Task A_reference_that_pins_no_version_is_refused_without_a_read(string secretRef)
    {
        StubParameterStore ssm = new();
        ssm.Put(App1Name, 1, Secret);
        ParameterStoreClientSecretSource source = Source(ssm);

        await source.PreloadAsync([secretRef], Ct);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => source.Get(secretRef));
        Assert.Contains("could not be read", ex.Message);
        Assert.Empty(ssm.Requests);
    }

    [Fact]
    public async Task Cancellation_propagates_and_leaves_the_reference_unread_rather_than_failed()
    {
        StubParameterStore ssm = new();
        ssm.Put(App1Name, 1, Secret);
        ParameterStoreClientSecretSource source = Source(ssm);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.PreloadAsync([App1Ref], cts.Token));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => source.Get(App1Ref));
        Assert.Contains("not preloaded", ex.Message);
    }

    /// <summary>
    /// N-16 for this component: a canary value goes through every path that touches one, including an
    /// SDK exception whose message carries it, and appears in no log line and no exception. The log
    /// lines are asserted non-empty and to name the references, so the search has something to search.
    /// </summary>
    [Fact]
    public async Task No_value_appears_in_a_log_line_or_an_exception()
    {
        const string Canary = "CANARY-client-secret-7f3a9c";
        const string App3Name = "/spa-platform/poc/apps/app3/client-secret";
        const string App4Name = "/spa-platform/poc/apps/app4/client-secret";
        StubParameterStore ssm = new();
        ssm.Put(App1Name, 1, Canary);
        ssm.Put(App4Name, 1, Canary);
        ssm.FailWith(App3Name + ":1", () => new AmazonSimpleSystemsManagementException($"value {Canary} rejected")
        {
            ErrorCode = "ValidationException",
        });
        CapturedLog logs = new();
        ParameterStoreClientSecretSource source = new(ssm, logs.CreateLogger<ParameterStoreClientSecretSource>());
        string[] refs = [App1Ref, App2Ref, "ssm:" + App3Name + ":1", "ssm:" + App1Name + ":9"];

        await source.PreloadAsync(refs, Ct);
        StubParameterStore wrongVersion = new() { VersionOverride = 3 };
        wrongVersion.Put(App4Name, 1, Canary);
        ParameterStoreClientSecretSource mismatched = new(wrongVersion, logs.CreateLogger<ParameterStoreClientSecretSource>());
        await mismatched.PreloadAsync(["ssm:" + App4Name + ":1"], Ct);

        List<string> thrown = [];
        foreach (string secretRef in refs.Skip(1).Append("ssm:/never/preloaded:1"))
        {
            thrown.Add(Assert.Throws<InvalidOperationException>(() => source.Get(secretRef)).ToString());
        }

        thrown.Add(Assert.Throws<InvalidOperationException>(() => mismatched.Get("ssm:" + App4Name + ":1")).ToString());

        Assert.Equal(Canary, source.Get(App1Ref));
        Assert.Equal(4, logs.Lines.Count);
        Assert.All(refs.Skip(1), secretRef => Assert.Contains(logs.Lines, line => line.Text.Contains(secretRef)));
        Assert.All(logs.Lines, line => Assert.DoesNotContain(Canary, line.Text));
        Assert.All(thrown, text => Assert.DoesNotContain(Canary, text));
    }
}
