using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Registry;

/// <summary>
/// The registry source of plan row 2.2: a conditional GET of the registry object, then secret preload,
/// validate, swap, then notify. The document is validated before its secrets are preloaded, because
/// the references come from it; the preload is the I/O half of validation (contracts/README.md,
/// <c>clientSecretRef</c>), and both finish before the swap. <see cref="RegistryPoller"/> calls
/// <see cref="RefreshAsync"/> on a jittered interval; a test calls it directly, one poll at a time.
///
/// <b>Any failure keeps the last good snapshot</b> (GLOSSARY.md, Last known good; threat T16). A
/// document is accepted or rejected whole (ADR-0002), and so is a snapshot whose secrets cannot all be
/// read (contracts/README.md, <c>clientSecretRef</c>; decision D3).
///
/// <b>At a cold start</b> there is nothing to keep, so an app whose secret cannot be read is left out
/// and reported, and the rest go live (docs/plan/02-contracts.md, Registry document). The source keeps
/// the document it came from and reads the left-out secrets again on each poll while the object is
/// unchanged; an app joins the live snapshot, under the same revision, once its secret reads.
///
/// <b>Nothing is ready until the first valid snapshot</b>: until then <see cref="IRegistry.Current"/>
/// is <see cref="LiveRegistry.NoSnapshot"/>, revision 0.
/// </summary>
public sealed class RegistrySource
{
    private readonly IObjectStore store;
    private readonly IClientSecretSource secrets;
    private readonly PlatformPolicy policy;
    private readonly LiveRegistry registry;
    private readonly string key;
    private readonly ILogger<RegistrySource> logger;

    // One poll at a time: the state below belongs to the poll in progress.
    private readonly SemaphoreSlim polling = new(1, 1);

    /// <summary>
    /// The ETag of the last document dealt with for good: live in full, or rejected by validation.
    /// Sent as If-None-Match, so an unchanged object costs no body. A document refused only for its
    /// secrets is not settled, so the next poll reads it again.
    /// </summary>
    private string? settledETag;

    /// <summary>The cold-start document that went live without some of its apps, while any are still out.</summary>
    private volatile Partial? partial;

    public RegistrySource(
        IObjectStore store,
        IClientSecretSource secrets,
        PlatformPolicy policy,
        LiveRegistry registry,
        string key,
        ILogger<RegistrySource> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(logger);
        this.store = store;
        this.secrets = secrets;
        this.policy = policy;
        this.registry = registry;
        this.key = key;
        this.logger = logger;
    }

    /// <summary>The appIds left out of the live snapshot at a cold start because their secrets could not be read.</summary>
    public IReadOnlyList<string> LeftOut => partial is { } p ? [.. p.LeftOut.Select(app => app.AppId)] : [];

    /// <summary>One poll. Never throws, except to propagate <paramref name="ct"/>.</summary>
    public async Task<RefreshOutcome> RefreshAsync(CancellationToken ct)
    {
        await polling.WaitAsync(ct);
        try
        {
            return await RefreshOnceAsync(ct);
        }
        finally
        {
            polling.Release();
        }
    }

    private async Task<RefreshOutcome> RefreshOnceAsync(CancellationToken ct)
    {
        RegistrySnapshot live = registry.Current;
        Partial? cold = partial;

        StoredObject? stored;
        try
        {
            stored = await store.GetAsync(key, settledETag ?? cold?.ETag, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "The registry object {Key} could not be read ({Error}: {Message}). Revision {Revision} stays live.",
                key, ex.GetType().Name, ex.Message, live.Revision);
            return RefreshOutcome.Failed;
        }

        if (stored is null)
        {
            logger.LogWarning("The registry object {Key} does not exist. Revision {Revision} stays live.", key, live.Revision);
            return RefreshOutcome.Missing;
        }

        if (stored.NotModified)
        {
            return cold is null ? RefreshOutcome.NotModified : await RetryLeftOutAsync(cold, ct);
        }

        string document;
        try
        {
            using Stream body = stored.Body ?? throw new InvalidDataException("The store returned no body.");
            using StreamReader reader = new(body, Encoding.UTF8);
            document = await reader.ReadToEndAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "The registry object {Key} ({ETag}) could not be read to the end ({Error}: {Message}). Revision {Revision} stays live.",
                key, stored.ETag, ex.GetType().Name, ex.Message, live.Revision);
            return RefreshOutcome.Failed;
        }

        RegistryValidation validation = RegistryValidator.Validate(document, policy, ReferenceEquals(live, LiveRegistry.NoSnapshot) ? null : live.Revision);
        if (!validation.IsValid)
        {
            foreach (RegistryIssue error in validation.Errors)
            {
                logger.LogError("Registry document {ETag} has an error: {Issue}", stored.ETag, error);
            }

            logger.LogError(
                "Registry document {ETag} is rejected with {Count} error(s). Revision {Revision} stays live.",
                stored.ETag, validation.Errors.Length, live.Revision);
            settledETag = stored.ETag;
            return RefreshOutcome.Rejected;
        }

        foreach (RegistryIssue warning in validation.Warnings)
        {
            logger.LogWarning("Registry revision {Revision} has a warning: {Issue}", validation.Revision, warning);
        }

        if (!await TryPreloadAsync(validation.Apps, validation.Revision, ct))
        {
            return RefreshOutcome.Failed;
        }

        (ImmutableArray<AppDefinition> kept, ImmutableArray<AppDefinition> leftOut) = Partition(validation.Apps, validation.Revision);
        bool coldStart = ReferenceEquals(live, LiveRegistry.NoSnapshot);
        if (!leftOut.IsEmpty && (!coldStart || kept.IsEmpty))
        {
            // With a live snapshot the document is rejected whole: one that cannot read a secret never
            // replaces one that works (D3). At a cold start with no app readable there is nothing worth
            // serving, and staying not ready keeps the load balancer on the tasks that still work
            // (decided by implementer). Not settled either way, so the next poll reads it again.
            logger.LogError(
                "Registry revision {Candidate} is not applied: {Count} client secret(s) could not be read. Revision {Revision} stays live.",
                validation.Revision, leftOut.Length, live.Revision);
            return RefreshOutcome.SecretsUnreadable;
        }

        Swap(validation, kept, stored.ETag);
        if (leftOut.IsEmpty)
        {
            partial = null;
            settledETag = stored.ETag;
        }
        else
        {
            partial = new Partial(validation, stored.ETag, leftOut);
            settledETag = null;
            logger.LogError(
                "Registry revision {Revision} went live without {Count} app(s) whose client secrets could not be read: {AppIds}.",
                validation.Revision, leftOut.Length, string.Join(", ", leftOut.Select(app => app.AppId)));
        }

        return RefreshOutcome.Swapped;
    }

    /// <summary>The object is unchanged and some of its apps are out: read their secrets again.</summary>
    private async Task<RefreshOutcome> RetryLeftOutAsync(Partial cold, CancellationToken ct)
    {
        if (!await TryPreloadAsync(cold.LeftOut, cold.Validation.Revision, ct))
        {
            return RefreshOutcome.Failed;
        }

        (ImmutableArray<AppDefinition> joined, ImmutableArray<AppDefinition> stillOut) = Partition(cold.LeftOut, cold.Validation.Revision);
        if (joined.IsEmpty)
        {
            return RefreshOutcome.NotModified;
        }

        HashSet<string> excluded = [.. stillOut.Select(app => app.AppId)];
        Swap(cold.Validation, [.. cold.Validation.Apps.Where(app => !excluded.Contains(app.AppId))], cold.ETag);
        logger.LogInformation(
            "App(s) {AppIds} joined registry revision {Revision}: their client secrets can now be read.",
            string.Join(", ", joined.Select(app => app.AppId)), cold.Validation.Revision);

        if (stillOut.IsEmpty)
        {
            partial = null;
            settledETag ??= cold.ETag;
        }
        else
        {
            partial = cold with { LeftOut = stillOut };
        }

        return RefreshOutcome.Swapped;
    }

    private async Task<bool> TryPreloadAsync(IEnumerable<AppDefinition> apps, long revision, CancellationToken ct)
    {
        try
        {
            // IClientSecretSource: every reference before the swap, because Get never does I/O.
            await secrets.PreloadAsync(apps.Select(app => app.OAuth.ClientSecretRef).Distinct(StringComparer.Ordinal), ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The interface promises one unreadable reference never fails the batch, so this is the
            // source itself failing. Its message carries no value (task 2.5).
            logger.LogError(
                "Client secrets for registry revision {Candidate} could not be preloaded ({Error}: {Message}). Revision {Revision} stays live.",
                revision, ex.GetType().Name, ex.Message, registry.Current.Revision);
            return false;
        }
    }

    /// <summary>
    /// Splits apps into those whose secret reads and those whose secret does not, and reports each of
    /// the second by appId and reference. The value read is discarded here: options setup reads it again
    /// from memory (task 3.2).
    /// </summary>
    private (ImmutableArray<AppDefinition> Readable, ImmutableArray<AppDefinition> Unreadable) Partition(
        IEnumerable<AppDefinition> apps, long revision)
    {
        ImmutableArray<AppDefinition>.Builder readable = ImmutableArray.CreateBuilder<AppDefinition>();
        ImmutableArray<AppDefinition>.Builder unreadable = ImmutableArray.CreateBuilder<AppDefinition>();
        foreach (AppDefinition app in apps)
        {
            try
            {
                _ = secrets.Get(app.OAuth.ClientSecretRef);
                readable.Add(app);
            }
            catch (Exception ex)
            {
                // IClientSecretSource.Get: the message never carries a value (task 2.5, N-16).
                logger.LogWarning(
                    "App {AppId} in registry revision {Revision}: client secret {SecretRef} could not be read. {Reason}",
                    app.AppId, revision, app.OAuth.ClientSecretRef, ex.Message);
                unreadable.Add(app);
            }
        }

        return (readable.ToImmutable(), unreadable.ToImmutable());
    }

    private void Swap(RegistryValidation validation, ImmutableArray<AppDefinition> apps, string etag)
    {
        RegistrySnapshot next = (validation with { Apps = apps }).ToSnapshot(etag);
        registry.Swap(next);
        logger.LogInformation(
            "Registry revision {Revision} ({ETag}) is live with {Count} app(s).", next.Revision, etag, next.ByAppId.Count);
    }

    private sealed record Partial(RegistryValidation Validation, string ETag, ImmutableArray<AppDefinition> LeftOut);
}
