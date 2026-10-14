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
    public async Task Access_denied_on_a_read_is_an_error_not_a_missing_key()
    {
        // Without s3:ListBucket, S3 answers a missing key with 403 rather than 404. Reading that as
        // "no registry" would hide a permissions fault behind a not-ready gateway.
        S3ObjectStore store = new(server.Client(), "bucket");
        server.FailWith = (HttpStatusCode.Forbidden, "AccessDenied");

        AmazonS3Exception thrown = await Assert.ThrowsAsync<AmazonS3Exception>(
            () => store.GetAsync("registry/registry.json", null, Ct));
        Assert.Equal(HttpStatusCode.Forbidden, thrown.StatusCode);
    }

    [Fact]
    public async Task A_missing_bucket_is_an_error_not_a_missing_key()
    {
        S3ObjectStore store = new(server.Client(), "bucket");
        server.FailWith = (HttpStatusCode.NotFound, "NoSuchBucket");

        await Assert.ThrowsAsync<AmazonS3Exception>(() => store.GetAsync("registry/registry.json", null, Ct));
    }

    private static async Task<bool> Put(S3ObjectStore store, string key, string body, string? ifMatch)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(body));
        return await store.PutAsync(key, stream, ifMatch, Ct);
    }
}
