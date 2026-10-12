using System.Text;
using Platform.Abstractions;

namespace Platform.Testing;

/// <summary>
/// The behaviour every <see cref="IObjectStore"/> must have, whichever store it is. Subclass it once
/// per implementation and override <see cref="CreateStore"/>.
///
/// The conditional-write rules here are the ones the S3 store meets against a real bucket in task
/// 1.5, and P-04 proves on the deployed stack: a second write to an existing key is refused, and so
/// is a delete followed by a write. Keeping them in one suite is what stops the in-memory double and
/// the S3 store from disagreeing — task 2.2 builds against the double and only meets the bucket in
/// task 1.7.
/// </summary>
public abstract class ObjectStoreContract
{
    protected abstract IObjectStore CreateStore();

    [Fact]
    public async Task A_key_that_was_never_written_reads_as_null()
    {
        IObjectStore store = CreateStore();

        Assert.Null(await store.GetAsync("registry/registry.json", ifNoneMatch: null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_write_with_no_condition_creates_a_key_that_is_absent()
    {
        IObjectStore store = CreateStore();

        Assert.True(await Put(store, "apps/app1/releases/r1/index.html", "<!doctype html>", ifMatch: null));

        StoredObject? stored = await store.GetAsync(
            "apps/app1/releases/r1/index.html", ifNoneMatch: null, TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        Assert.Equal("<!doctype html>", await ReadBody(stored));
    }

    [Fact]
    public async Task A_write_with_no_condition_is_refused_when_the_key_is_already_there()
    {
        // Write-once. This is release immutability (ADR-0009) and P-04.
        IObjectStore store = CreateStore();
        await Put(store, "apps/app1/releases/r1/index.html", "first", ifMatch: null);

        Assert.False(await Put(store, "apps/app1/releases/r1/index.html", "second", ifMatch: null));
        Assert.Equal("first", await Read(store, "apps/app1/releases/r1/index.html"));
    }

    [Fact]
    public async Task A_write_conditional_on_the_current_etag_replaces_the_object()
    {
        IObjectStore store = CreateStore();
        await Put(store, "registry/registry.json", "{\"revision\":1}", ifMatch: null);
        string etag = await ETag(store, "registry/registry.json");

        Assert.True(await Put(store, "registry/registry.json", "{\"revision\":2}", ifMatch: etag));
        Assert.Equal("{\"revision\":2}", await Read(store, "registry/registry.json"));
    }

    [Fact]
    public async Task A_write_conditional_on_a_stale_etag_is_refused_and_changes_nothing()
    {
        // Two administrators applying a registry at once: the second one loses. Task 2.6's
        // "registry apply conditional on the live version" is this rule.
        IObjectStore store = CreateStore();
        await Put(store, "registry/registry.json", "{\"revision\":1}", ifMatch: null);
        string stale = await ETag(store, "registry/registry.json");
        await Put(store, "registry/registry.json", "{\"revision\":2}", ifMatch: stale);

        Assert.False(await Put(store, "registry/registry.json", "{\"revision\":3}", ifMatch: stale));
        Assert.Equal("{\"revision\":2}", await Read(store, "registry/registry.json"));
    }

    [Fact]
    public async Task A_write_conditional_on_an_etag_is_refused_when_the_key_is_absent()
    {
        IObjectStore store = CreateStore();

        Assert.False(await Put(store, "registry/registry.json", "{}", ifMatch: "\"whatever\""));
    }

    [Fact]
    public async Task The_etag_changes_when_the_object_does()
    {
        IObjectStore store = CreateStore();
        await Put(store, "registry/registry.json", "{\"revision\":1}", ifMatch: null);
        string first = await ETag(store, "registry/registry.json");
        await Put(store, "registry/registry.json", "{\"revision\":2}", ifMatch: first);

        Assert.NotEqual(first, await ETag(store, "registry/registry.json"));
    }

    [Fact]
    public async Task A_read_whose_etag_still_matches_comes_back_not_modified_with_no_body()
    {
        // The registry source polls on a jittered interval with a conditional GET (task 2.2). The
        // unchanged case must cost no body.
        IObjectStore store = CreateStore();
        await Put(store, "registry/registry.json", "{\"revision\":1}", ifMatch: null);
        string etag = await ETag(store, "registry/registry.json");

        StoredObject? stored = await store.GetAsync(
            "registry/registry.json", ifNoneMatch: etag, TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        Assert.True(stored.NotModified);
        Assert.Null(stored.Body);
        Assert.Equal(etag, stored.ETag);
    }

    [Fact]
    public async Task A_read_whose_etag_no_longer_matches_comes_back_with_the_new_body()
    {
        IObjectStore store = CreateStore();
        await Put(store, "registry/registry.json", "{\"revision\":1}", ifMatch: null);
        string stale = await ETag(store, "registry/registry.json");
        await Put(store, "registry/registry.json", "{\"revision\":2}", ifMatch: stale);

        StoredObject? stored = await store.GetAsync(
            "registry/registry.json", ifNoneMatch: stale, TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        Assert.False(stored.NotModified);
        Assert.Equal("{\"revision\":2}", await ReadBody(stored));
    }

    [Fact]
    public async Task A_stored_object_reports_the_length_of_its_body()
    {
        IObjectStore store = CreateStore();
        await Put(store, "apps/app1/releases/r1/assets/app.js", "console.log(1)", ifMatch: null);

        StoredObject? stored = await store.GetAsync(
            "apps/app1/releases/r1/assets/app.js", ifNoneMatch: null, TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        Assert.Equal(Encoding.UTF8.GetByteCount("console.log(1)"), stored.Length);
    }

    [Fact]
    public async Task Two_keys_hold_two_objects()
    {
        // Two hosts return two different shells: the storage half of F-01.
        IObjectStore store = CreateStore();
        await Put(store, "apps/app1/releases/r1/index.html", "app one", ifMatch: null);
        await Put(store, "apps/app2/releases/r1/index.html", "app two", ifMatch: null);

        Assert.Equal("app one", await Read(store, "apps/app1/releases/r1/index.html"));
        Assert.Equal("app two", await Read(store, "apps/app2/releases/r1/index.html"));
    }

    private static async Task<bool> Put(IObjectStore store, string key, string body, string? ifMatch)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(body));
        return await store.PutAsync(key, stream, ifMatch, TestContext.Current.CancellationToken);
    }

    private static async Task<string> Read(IObjectStore store, string key)
    {
        StoredObject stored = await store.GetAsync(key, ifNoneMatch: null, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"'{key}' is not in the store.");

        return await ReadBody(stored);
    }

    private static async Task<string> ETag(IObjectStore store, string key)
    {
        StoredObject stored = await store.GetAsync(key, ifNoneMatch: null, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"'{key}' is not in the store.");

        return stored.ETag;
    }

    private static async Task<string> ReadBody(StoredObject stored)
    {
        Assert.NotNull(stored.Body);
        using StreamReader reader = new(stored.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }
}
