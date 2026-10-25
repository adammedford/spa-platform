using System.Net;
using System.Security.Cryptography;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Testing;

namespace Platform.Hosting.Tests;

/// <summary>
/// The key ring <c>AddPlatformHosting</c> registers (plan row P2-2; ADR-0025; change request P2-2,
/// decisions 1 to 20; issue #340). Each host is a <see cref="RingHost"/>; the Parameter Store each
/// sees is a <see cref="StubRingStore"/>, so two hosts over one stub are two tasks of one cell and two
/// hosts over two stubs share nothing.
/// </summary>
public sealed class KeyRingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Mutation: drop the decorator and the put carries no tags (so the package adds none of its own);
    /// drop <c>PersistKeysToAWSSystemsManager</c> and the stub sees no put at all.
    /// </summary>
    [Fact]
    public async Task A_host_with_the_section_writes_one_tagged_SecureString_and_a_second_host_over_the_same_store_reads_it()
    {
        StubRingStore store = new();

        string protectedPayload;
        await using (StageHost first = await RingHost.StartAsync(RingHost.WithSection(), store, Ct))
        {
            protectedPayload = RingHost.Protect(first, "one payload");
        }

        PutParameterRequest put = Assert.Single(store.Puts);
        Assert.Equal(ParameterType.SecureString, put.Type);
        Assert.Equal(ParameterTier.Standard, put.Tier);
        Assert.Equal(RingHost.KeyId, put.KeyId);
        Assert.StartsWith(RingHost.Prefix, put.Name, StringComparison.Ordinal);
        Assert.Equal(
            [("Cell", "test"), ("ManagedBy", "gateway"), ("Platform", "spa-platform")],
            put.Tags.Select(t => (t.Key, t.Value)).OrderBy(t => t.Key, StringComparer.Ordinal));

        await using StageHost second = await RingHost.StartAsync(RingHost.WithSection(), store, Ct);
        Assert.Equal("one payload", RingHost.Unprotect(second, protectedPayload));
        Assert.Single(store.Puts);
    }

    /// <summary>
    /// The control for the test above: each host's repository is its own stub, set explicitly, so the
    /// user-profile ring cannot make this pass (change request P2-2, decision 6). Mutation: hand both
    /// hosts one stub and the second reads the first's payload.
    /// </summary>
    [Fact]
    public async Task Two_hosts_over_two_stores_cannot_read_each_others_payloads()
    {
        StubRingStore one = new();
        StubRingStore two = new();
        await using StageHost first = await RingHost.StartAsync(RingHost.WithSection(), one, Ct);
        await using StageHost second = await RingHost.StartAsync(RingHost.WithSection(), two, Ct);

        string fromFirst = RingHost.Protect(first, "payload");
        string fromSecond = RingHost.Protect(second, "payload");

        Assert.ThrowsAny<CryptographicException>(() => RingHost.Unprotect(second, fromFirst));
        Assert.ThrowsAny<CryptographicException>(() => RingHost.Unprotect(first, fromSecond));
        Assert.Single(one.Puts);
        Assert.Single(two.Puts);
    }

    /// <summary>
    /// With no section the ring stays the framework's default and nothing touches Parameter Store
    /// (change request P2-2, decision 5). Each host is given a repository of its own, as the control
    /// needs. Mutation: delete the log line and the count is zero; hand both hosts one repository and
    /// the second reads the first's payload. The empty store is decision 5's "nothing is registered"
    /// as Parameter Store sees it; the test's own repository would mask a ring registered here, so the
    /// client check pins it instead. Mutation: register the ring with no section and the client
    /// resolved is the decorator, not the stub.
    /// </summary>
    [Fact]
    public async Task Without_the_section_nothing_reaches_the_store_two_hosts_share_nothing_and_one_line_says_so()
    {
        Dictionary<string, string?> configuration = new() { ["Platform:Cell"] = "test", ["Session:Store"] = "memory" };
        StubRingStore store = new();
        await using StageHost first = await RingHost.StartAsync(configuration, store, Ct, RingHost.OwnRepository);
        await using StageHost second = await RingHost.StartAsync(configuration, store, Ct, RingHost.OwnRepository);

        string fromFirst = RingHost.Protect(first, "payload");

        Assert.ThrowsAny<CryptographicException>(() => RingHost.Unprotect(second, fromFirst));
        Assert.Empty(store.Requests);
        Assert.Same(store, first.Services.GetRequiredService<IAmazonSimpleSystemsManagement>());
        CapturedLine line = Assert.Single(first.Log.Lines, l => l.Category == "Platform.Hosting.KeyRing");
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("DataProtection", line.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The three refusals at start (change request P2-2, decisions 4, 15 and 16), each naming its keys.
    /// Mutation: delete any one check and its row starts a host instead of throwing.
    /// </summary>
    [Theory]
    [InlineData("no-cell", "DataProtection", "Platform:Cell")]
    [InlineData("no-prefix", "DataProtection:ParameterPrefix", null)]
    [InlineData("dynamodb-without-section", "Session:Store", "DataProtection")]
    public async Task A_configuration_the_ring_cannot_honour_fails_the_start_naming_its_keys(
        string shape, string key, string? otherKey)
    {
        Dictionary<string, string?> configuration = RingHost.WithSection();
        switch (shape)
        {
            case "no-cell":
                configuration.Remove("Platform:Cell");
                break;
            case "no-prefix":
                configuration.Remove("DataProtection:ParameterPrefix");
                break;
            default:
                configuration.Remove("DataProtection:ParameterPrefix");
                configuration.Remove("DataProtection:KmsKeyId");
                configuration["Session:Store"] = "dynamodb";
                break;
        }

        StubRingStore store = new();
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RingHost.StartAsync(configuration, store, Ct));

        Assert.Contains(key, refused.Message, StringComparison.Ordinal);
        if (otherKey is not null)
        {
            Assert.Contains(otherKey, refused.Message, StringComparison.Ordinal);
        }

        Assert.Empty(store.Requests);
    }

    /// <summary>
    /// The implementer's fourth refusal: a section with no Parameter Store client registered fails the
    /// start naming <c>AddPlatformRegistry</c>, rather than letting the package build a client of its
    /// own with a second credential chain (change request P2-2, decision 3). Mutation: delete the
    /// empty-registration check and the start fails on something else, or not at all.
    /// </summary>
    [Fact]
    public async Task A_section_with_no_client_registered_fails_the_start_naming_AddPlatformRegistry()
    {
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RingHost.StartAsync(RingHost.WithSection(), client: null, Ct));

        Assert.Contains("AddPlatformRegistry", refused.Message, StringComparison.Ordinal);
        Assert.Contains("IAmazonSimpleSystemsManagement", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty or blank <c>DataProtection:KmsKeyId</c> is no key id (#357): the put carries none, so
    /// Parameter Store encrypts under the account's default key, as it does with the key absent.
    /// Mutation: drop the normalisation in <c>KeyRing.Register</c> and the blank row's put carries the
    /// spaces as its key id. The empty row stays green under that mutation, because the package itself
    /// leaves an empty id off the put (tried on 4.0.3); it pins the outcome, and the blank row is the
    /// one only the normalisation catches.
    /// </summary>
    [Theory]
    [InlineData("empty", "")]
    [InlineData("blank", "   ")]
    [InlineData("absent", null)]
    public async Task A_key_id_that_is_empty_blank_or_absent_puts_with_no_key_id(string shape, string? keyId)
    {
        Dictionary<string, string?> configuration = RingHost.WithSection();
        if (keyId is null)
        {
            configuration.Remove("DataProtection:KmsKeyId");
        }
        else
        {
            configuration["DataProtection:KmsKeyId"] = keyId;
        }

        StubRingStore store = new();
        await using StageHost host = await RingHost.StartAsync(configuration, store, Ct);

        PutParameterRequest put = Assert.Single(store.Puts);
        Assert.Equal(ParameterType.SecureString, put.Type);
        Assert.True(put.KeyId is null, $"The {shape} key id was put as '{put.KeyId}'.");
    }

    public static TheoryData<string> ReadFailures => ["access-denied", "network"];

    /// <summary>
    /// F16: the ring is read at start, so a task that cannot read its prefix fails to start and mints
    /// nothing. Records the package fact the ticket asks for: its repository rethrows the client's
    /// exception rather than answering empty, for a refusal and for a network failure alike.
    /// Mutation: remove the eager read and the host starts (the framework's own start-up read logs and
    /// swallows the failure).
    /// </summary>
    [Theory]
    [MemberData(nameof(ReadFailures))]
    public async Task A_first_read_that_throws_fails_the_start_and_mints_nothing(string failure)
    {
        Exception planted = failure == "access-denied"
            ? new AmazonSimpleSystemsManagementException("User is not authorized to perform: ssm:GetParametersByPath")
            {
                ErrorCode = "AccessDeniedException",
                StatusCode = HttpStatusCode.BadRequest,
            }
            : new HttpRequestException("Connection refused");
        StubRingStore store = new() { ReadFailure = () => planted };

        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(() => RingHost.StartAsync(RingHost.WithSection(), store, Ct));

        // The start fails on the read itself, not on anything else that could throw at start (the
        // refusals included): the planted exception is the one thrown, or one it wraps.
        List<Exception> chain = [];
        for (Exception? e = thrown; e is not null; e = e.InnerException)
        {
            chain.Add(e);
        }

        Assert.Contains(planted, chain);
        Assert.Contains(store.Requests, r => r is GetParametersByPathRequest);
        Assert.Empty(store.Puts);
    }

    /// <summary>
    /// An empty prefix is the bootstrap: the start mints the first key, before anything protects.
    /// Mutation: drop <c>PersistKeysToAWSSystemsManager</c> and the stub sees no put. Removing the eager
    /// read alone leaves this green, because the framework's own start-up read also mints; what the
    /// eager read adds is the failed start, which the test above guards.
    /// </summary>
    [Fact]
    public async Task A_first_read_that_finds_no_parameters_starts_and_mints_one_key()
    {
        StubRingStore store = new();

        await using StageHost host = await RingHost.StartAsync(RingHost.WithSection(), store, Ct);

        Assert.Single(store.Puts);
        Assert.Single(host.Services.GetRequiredService<IKeyManager>().GetAllKeys());
    }

    /// <summary>
    /// The second fact the ticket asks for: two tasks that both find the prefix empty both mint, and the
    /// framework reconciles them when one meets a payload under the other's key, by reloading the ring
    /// once (its unknown-key refresh), so each reads the other's payloads. That refresh runs only in
    /// the two minutes after a host's key ring provider is created (<c>KeyRingProvider</c>'s
    /// <c>AutoRefreshWindowEnd</c>), which is where a bootstrap race happens. Mutation: keep the store
    /// hiding writes after the starts and the reload finds no new key, so the unprotect throws.
    /// </summary>
    [Fact]
    public async Task Two_hosts_that_both_mint_on_an_empty_prefix_read_each_others_payloads()
    {
        StubRingStore store = new() { HideWrites = true };
        await using StageHost first = await RingHost.StartAsync(RingHost.WithSection(), store, Ct);
        await using StageHost second = await RingHost.StartAsync(RingHost.WithSection(), store, Ct);
        Assert.Equal(2, store.Puts.Count);
        store.HideWrites = false;

        string fromFirst = RingHost.Protect(first, "first");
        string fromSecond = RingHost.Protect(second, "second");

        Assert.Equal("first", RingHost.Unprotect(second, fromFirst));
        Assert.Equal("second", RingHost.Unprotect(first, fromSecond));
    }

    /// <summary>
    /// The decorator hands a request it does not tag to the inner client as it was given: the same
    /// object, untouched, and the inner client's answer back. Mutation: copy the request in the
    /// decorator and the reference check fails; drop the decorator and the client is the stub itself.
    /// </summary>
    [Fact]
    public async Task The_decorator_passes_a_secret_read_through_unchanged()
    {
        StubRingStore store = new();
        store.Put("/spa-platform/test/apps/app1/client-secret", "s3cret-canary");
        await using StageHost host = await RingHost.StartAsync(RingHost.WithSection(), store, Ct);
        IAmazonSimpleSystemsManagement client = host.Services.GetRequiredService<IAmazonSimpleSystemsManagement>();
        Assert.NotSame(store, client);

        GetParameterRequest read = new() { Name = "/spa-platform/test/apps/app1/client-secret", WithDecryption = true };
        GetParameterResponse answer = await client.GetParameterAsync(read, Ct);

        Assert.Same(read, store.Requests[^1]);
        Assert.Equal("/spa-platform/test/apps/app1/client-secret", read.Name);
        Assert.True(read.WithDecryption);
        Assert.Equal("s3cret-canary", answer.Parameter.Value);
    }

    /// <summary>
    /// Tags go only on a put under the ring's prefix, and the prefix is a path: a sibling whose name
    /// merely begins with the same characters is not under it. Mutation: compare against the prefix
    /// without its trailing slash, or drop the prefix check, and the sibling or the secret is tagged.
    /// </summary>
    [Theory]
    [InlineData("/spa-platform/test/data-protection/key-1", true)]
    [InlineData("/spa-platform/test/apps/app1/client-secret", false)]
    [InlineData("/spa-platform/test/data-protection-old/key-1", false)]
    public async Task The_decorator_tags_a_put_only_under_the_ring_prefix(string name, bool tagged)
    {
        StubRingStore store = new();
        await using StageHost host = await RingHost.StartAsync(RingHost.WithSection(), store, Ct);
        IAmazonSimpleSystemsManagement client = host.Services.GetRequiredService<IAmazonSimpleSystemsManagement>();

        await client.PutParameterAsync(new PutParameterRequest { Name = name, Value = "v", Type = ParameterType.String }, Ct);

        PutParameterRequest seen = store.Puts[^1];
        Assert.Equal(name, seen.Name);
        Assert.Equal(tagged ? 3 : 0, seen.Tags?.Count ?? 0);
    }

    /// <summary>
    /// The package's repository disposes the client it is given (#216). The decorator does not forward
    /// that, so the registry's client outlives the ring's repository, and the container that owns the
    /// inner client is still the one that disposes it. Mutation: forward <c>Dispose</c> to the inner
    /// client and the read after the repository's disposal throws; let the decorator build the inner
    /// client itself rather than resolve the container's and it is never disposed at shutdown.
    /// </summary>
    [Fact]
    public async Task The_inner_client_outlives_the_rings_repository_and_is_disposed_by_its_container()
    {
        StubRingStore store = new();
        store.Put("/spa-platform/test/apps/app1/client-secret", "value");
        StageHost host = await RingHost.StartAsync(RingHost.WithSection(), store, Ct, ownedClient: true);
        try
        {
            IDisposable repository = Assert.IsAssignableFrom<IDisposable>(
                host.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);
            repository.Dispose();

            Assert.False(store.Disposed);
            IAmazonSimpleSystemsManagement client = host.Services.GetRequiredService<IAmazonSimpleSystemsManagement>();
            GetParameterResponse answer = await client.GetParameterAsync(
                new GetParameterRequest { Name = "/spa-platform/test/apps/app1/client-secret" }, Ct);
            Assert.Equal("value", answer.Parameter.Value);
        }
        finally
        {
            await host.DisposeAsync();
        }

        Assert.True(store.Disposed);
    }

    /// <summary>
    /// The application name is derived from the cell (ADR-0025 decision 2), so a renamed cell cannot
    /// read what the old name protected even over the same ring. Mutation: drop
    /// <c>SetApplicationName</c> and the discriminator is the content root, which both hosts share,
    /// so the second host reads the payload.
    /// </summary>
    [Fact]
    public async Task The_application_name_is_spa_platform_and_the_cell_and_another_cell_cannot_read_it()
    {
        StubRingStore store = new();
        await using StageHost first = await RingHost.StartAsync(RingHost.WithSection("test"), store, Ct);
        Assert.Equal(
            "spa-platform-test",
            first.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
        string protectedPayload = RingHost.Protect(first, "payload");

        await using StageHost renamed = await RingHost.StartAsync(RingHost.WithSection("other"), store, Ct);

        Assert.ThrowsAny<CryptographicException>(() => RingHost.Unprotect(renamed, protectedPayload));
        Assert.Single(store.Puts);
    }
}
