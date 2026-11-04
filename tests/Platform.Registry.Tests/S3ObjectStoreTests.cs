using System.Net;
using System.Text;
using Amazon.S3;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Registry.Tests;

/// <summary>
/// The S3 store passes the same suite as the in-memory double (<see cref="ObjectStoreContract"/>), so
/// task 2.2's registry source, built against the double, meets the same rules over S3. Here the SDK
/// talks HTTP to <see cref="FakeS3Server"/>; the real bucket is task 1.7's.
/// </summary>
public sealed class S3ObjectStoreContractTests : ObjectStoreContract, IAsyncLifetime
{
    private FakeS3Server server = null!;
    private int buckets;

    public async ValueTask InitializeAsync() => server = await FakeS3Server.StartAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => server.DisposeAsync();

    // A bucket per store, so no two stores in one test share keys.
    protected override IObjectStore CreateStore() =>
        new S3ObjectStore(server.Client(), $"bucket-{Interlocked.Increment(ref buckets)}");
}

/// <summary>
/// The same suite over the S3 store as the gateway's deployed role meets it: <c>s3:GetObject</c> and no
/// <c>s3:ListBucket</c> (P-03), so S3 answers a missing key with 403 <c>AccessDenied</c> and the store
/// declares <see cref="MissingKeyAnswer.AmbiguousRefusal"/> (change request
/// contracts/changes/issue-381-object-read-refusal.md, item 2).
///
/// Mutation: drop the store's <c>AccessDenied</c> catch, and both missing-key cases fail here with an
/// <c>AmazonS3Exception</c> where the refusal was declared.
/// </summary>
public sealed class S3ObjectStoreGetObjectOnlyContractTests : ObjectStoreContract, IAsyncLifetime
{
    private FakeS3Server server = null!;
    private int buckets;

    public async ValueTask InitializeAsync()
    {
        server = await FakeS3Server.StartAsync(TestContext.Current.CancellationToken);
        server.ListBucketGranted = false;
    }

    public ValueTask DisposeAsync() => server.DisposeAsync();

    protected override MissingKeyAnswer MissingKeys => MissingKeyAnswer.AmbiguousRefusal;

    protected override IObjectStore CreateStore() =>
        new S3ObjectStore(server.Client(), $"bucket-{Interlocked.Increment(ref buckets)}");
}

/// <summary>What the S3 store sends, and how it reads S3's answers, beyond the shared contract.</summary>
public sealed class S3ObjectStoreTests : IAsyncLifetime
{
    private FakeS3Server server = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => server = await FakeS3Server.StartAsync(Ct);

    public ValueTask DisposeAsync() => server.DisposeAsync();

    [Fact]
    public async Task A_write_with_no_condition_sends_if_none_match_star()
    {
        // s3-layout.md: spactl sends If-None-Match: * on every release object, and the bucket policy
        // refuses a PUT under apps/ without it.
        S3ObjectStore store = new(server.Client(), "bucket");

        await Put(store, "apps/app1/releases/r1/index.html", "x", ifMatch: null);

        FakeS3Server.SeenRequest put = Assert.Single(server.Requests);
        Assert.Equal("PUT", put.Method);
        Assert.Equal("/bucket/apps/app1/releases/r1/index.html", put.Path);
        Assert.Equal("*", put.IfNoneMatch);
        Assert.Equal("", put.IfMatch);
    }

    [Fact]
    public async Task A_conditional_write_sends_if_match_and_not_if_none_match()
    {
        S3ObjectStore store = new(server.Client(), "bucket");
        await Put(store, "registry/registry.json", "{}", ifMatch: null);
        StoredObject first = (await store.GetAsync("registry/registry.json", null, Ct))!;
        first.Body!.Dispose();

        await Put(store, "registry/registry.json", "{ }", ifMatch: first.ETag);

        FakeS3Server.SeenRequest put = server.Requests[^1];
        Assert.Equal(first.ETag, put.IfMatch);
        Assert.Equal("", put.IfNoneMatch);
    }

    [Fact]
    public async Task A_read_sends_the_etag_it_already_has()
    {
        S3ObjectStore store = new(server.Client(), "bucket");

        await store.GetAsync("registry/registry.json", "\"abc\"", Ct);

        Assert.Equal("\"abc\"", Assert.Single(server.Requests).IfNoneMatch);
    }

    [Fact]
    public async Task The_store_leaves_the_callers_stream_open()
    {
        // IObjectStore.PutAsync takes a stream it does not own; the SDK closes it by default.
        S3ObjectStore store = new(server.Client(), "bucket");
        using MemoryStream body = new(Encoding.UTF8.GetBytes("x"));

        await store.PutAsync("apps/app1/releases/r1/index.html", body, ifMatch: null, Ct);

        Assert.True(body.CanRead);
    }

    [Fact]
    public async Task A_conflicting_concurrent_write_is_a_refusal_not_an_error()
    {
        // 409 ConditionalRequestConflict: another conditional write to the key is in flight
        // (https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes.html).
        S3ObjectStore store = new(server.Client(), "bucket");
        server.FailWith = (HttpStatusCode.Conflict, "ConditionalRequestConflict");

        Assert.False(await Put(store, "registry/registry.json", "{}", ifMatch: "\"abc\""));
    }

    [Fact]
    public async Task Access_denied_on_a_read_is_a_refusal_not_a_missing_key()
    {
        // Without s3:ListBucket, S3 answers a missing key with 403 rather than 404. Reading that as
        // "no registry" would hide a permissions fault behind a not-ready gateway, so the store names it
        // a refusal and leaves the meaning to the caller (change request issue-381, item 1).
        // Mutation: catch AccessDenied and return null, and this fails on the null.
        S3ObjectStore store = new(server.Client(), "bucket");
        server.FailWith = (HttpStatusCode.Forbidden, "AccessDenied");

        ObjectReadRefusedException refused = await Assert.ThrowsAsync<ObjectReadRefusedException>(
            () => store.GetAsync("registry/registry.json", null, Ct));
        Assert.Equal("registry/registry.json", refused.Key);
        AmazonS3Exception inner = Assert.IsType<AmazonS3Exception>(refused.InnerException);
        Assert.Equal(HttpStatusCode.Forbidden, inner.StatusCode);
        Assert.Equal("AccessDenied", inner.ErrorCode);
    }

    [Fact]
    public async Task An_existing_key_the_role_may_not_read_is_a_refusal()
    {
        // The other half of what a 403 AccessDenied can mean: the key is there and the role may not read
        // it. The store cannot tell the two apart and does not try.
        // Mutation: drop the store's AccessDenied catch, and this fails with the AmazonS3Exception.
        S3ObjectStore store = new(server.Client(), "bucket");
        await Put(store, "apps/app1/releases/r1/assets/app.js", "console.log(1)", ifMatch: null);
        server.DenyRead("bucket", "apps/app1/releases/r1/assets/app.js");

        ObjectReadRefusedException refused = await Assert.ThrowsAsync<ObjectReadRefusedException>(
            () => store.GetAsync("apps/app1/releases/r1/assets/app.js", null, Ct));
        Assert.Equal("apps/app1/releases/r1/assets/app.js", refused.Key);
    }

    /// <summary>
    /// Only 403 <c>AccessDenied</c> is a refusal. Every other failure propagates as the SDK raised it,
    /// never as null and never as <see cref="ObjectReadRefusedException"/>: another 403 code is a
    /// signature, credential, clock or storage-class fault; <c>NoSuchBucket</c> is configuration; 5xx
    /// is S3 itself (https://docs.aws.amazon.com/AmazonS3/latest/API/ErrorResponses.html, "List of
    /// error codes").
    ///
    /// Mutation: classify on the status alone (any 403 is a refusal), and the five 403 rows fail.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "InvalidObjectState")]
    [InlineData(HttpStatusCode.Forbidden, "SignatureDoesNotMatch")]
    [InlineData(HttpStatusCode.Forbidden, "InvalidAccessKeyId")]
    [InlineData(HttpStatusCode.Forbidden, "AllAccessDisabled")]
    [InlineData(HttpStatusCode.Forbidden, "RequestTimeTooSkewed")]
    [InlineData(HttpStatusCode.NotFound, "NoSuchBucket")]
    [InlineData(HttpStatusCode.InternalServerError, "InternalError")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "SlowDown")]
    public async Task Any_other_failure_propagates_as_the_sdk_raised_it(HttpStatusCode status, string code)
    {
        S3ObjectStore store = new(server.Client(), "bucket");
        server.FailWith = (status, code);

        // Any: the SDK raises some codes as a modelled subclass (InvalidObjectState is
        // InvalidObjectStateException), and that is still the SDK's own exception, unwrapped.
        AmazonS3Exception thrown = await Assert.ThrowsAnyAsync<AmazonS3Exception>(
            () => store.GetAsync("registry/registry.json", null, Ct));
        Assert.Equal(status, thrown.StatusCode);
        Assert.Equal(code, thrown.ErrorCode);
    }

    [Fact]
    public async Task A_cancelled_read_surfaces_as_cancellation()
    {
        // Mutation: catch the read's OperationCanceledException and wrap it as a refusal, and this
        // fails on the ObjectReadRefusedException.
        S3ObjectStore store = new(server.Client(), "bucket");
        server.ListBucketGranted = false;
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.GetAsync("registry/registry.json", null, cancelled.Token));
    }

    private static async Task<bool> Put(S3ObjectStore store, string key, string body, string? ifMatch)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(body));
        return await store.PutAsync(key, stream, ifMatch, Ct);
    }
}
