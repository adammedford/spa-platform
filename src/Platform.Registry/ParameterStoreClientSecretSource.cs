using System.Collections.Concurrent;
using System.Globalization;
using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Registry;

/// <summary>
/// <see cref="IClientSecretSource"/> over SSM Parameter Store (ADR-0003, plan row 2.5): a registry
/// entry's <c>clientSecretRef</c> is <c>ssm:{name}:{version}</c>, read once and then served from
/// memory.
///
/// <b>Cached by reference, never invalidated.</b> A reference pins a parameter version, and a
/// version's value never changes, so a value read once is the value for the life of the process.
/// Rotation is a new reference, not a new value under the old one
/// (<c>docs/plan/05-infrastructure-and-cost.md</c> -> Rotating a client secret), and the old
/// reference stays readable for the live snapshot until the swap.
///
/// <b>Nothing a value touches is logged or thrown</b> (task 2.5, test N-16, <c>contracts/telemetry.md</c>
/// -> Never logged). A failure is reported by reference and by the SDK's error code; the SDK's
/// exception is neither logged nor chained as an inner exception, so what its message carries never
/// reaches a log line or a caller.
/// </summary>
public sealed class ParameterStoreClientSecretSource : IClientSecretSource
{
    private const string Prefix = "ssm:";

    // Parameter Store's default throughput for GetParameter is 40 transactions per second per account
    // and region (https://docs.aws.amazon.com/general/latest/gr/ssm.html, "GetParameter"). A cold start
    // reads one parameter per app; four at a time stays well inside that while not serialising a
    // registry with many apps.
    private const int Concurrency = 4;

    private readonly IAmazonSimpleSystemsManagement ssm;
    private readonly ILogger<ParameterStoreClientSecretSource> logger;

    // Preload runs on the registry source's refresh loop while Get runs on request threads (task 2.2),
    // so both maps are concurrent. A reference is in at most one of them.
    private readonly ConcurrentDictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> failures = new(StringComparer.Ordinal);

    public ParameterStoreClientSecretSource(
        IAmazonSimpleSystemsManagement ssm, ILogger<ParameterStoreClientSecretSource> logger)
    {
        ArgumentNullException.ThrowIfNull(ssm);
        ArgumentNullException.ThrowIfNull(logger);
        this.ssm = ssm;
        this.logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads only references not already held. A reference that failed before is read again, because a
    /// parameter can become readable (written late, or its access fixed) and the next candidate
    /// snapshot should not inherit the old failure. Cancellation is not a failure of any reference: it
    /// propagates, and the references it interrupted are neither held nor marked failed.
    /// </remarks>
    public async Task PreloadAsync(IEnumerable<string> secretRefs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(secretRefs);

        string[] pending = secretRefs
            .Where(secretRef => !values.ContainsKey(secretRef))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = ct },
            async (secretRef, token) => await LoadAsync(secretRef, token));
    }

    /// <inheritdoc />
    public string Get(string secretRef)
    {
        ArgumentNullException.ThrowIfNull(secretRef);

        if (values.TryGetValue(secretRef, out string? value))
        {
            return value;
        }

        if (failures.TryGetValue(secretRef, out string? reason))
        {
            throw new InvalidOperationException(
                $"Client secret '{secretRef}' could not be read ({reason}). The app it belongs to is left out of the snapshot.");
        }

        throw new InvalidOperationException(
            $"Client secret '{secretRef}' was not preloaded. Get does no I/O: preload a reference before reading it.");
    }

    private async Task LoadAsync(string secretRef, CancellationToken ct)
    {
        if (!TryParse(secretRef, out string? name, out long version))
        {
            Fail(secretRef, "not an ssm:{name}:{version} reference");
            return;
        }

        GetParameterResponse response;
        try
        {
            // "name:version" is Parameter Store's version selector, so the read is pinned on the
            // server, not filtered here. WithDecryption returns a SecureString's plaintext.
            // https://docs.aws.amazon.com/systems-manager/latest/APIReference/API_GetParameter.html
            // https://docs.aws.amazon.com/systems-manager/latest/userguide/sysman-paramstore-versions.html
            response = await ssm.GetParameterAsync(
                new GetParameterRequest
                {
                    Name = string.Create(CultureInfo.InvariantCulture, $"{name}:{version}"),
                    WithDecryption = true,
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AmazonServiceException ex)
        {
            // The error code (ParameterNotFound, ParameterVersionNotFound, AccessDeniedException) is
            // what an operator needs, and it carries no value. The message and the exception do not
            // leave this method.
            Fail(secretRef, ex.ErrorCode ?? ex.GetType().Name);
            return;
        }
        catch (Exception ex)
        {
            // Anything else that stops one read: AmazonClientException (no credentials, no region), and
            // transport failures the SDK does not wrap. A refused connection reaches here as a bare
            // HttpRequestException and an HTTP timeout as a TaskCanceledException the caller's token did
            // not raise (both seen in the task 2.5 review). The interface promises one unreadable
            // reference never fails the batch, and none of these messages may leave this method.
            Fail(secretRef, ex.GetType().Name);
            return;
        }

        Parameter? parameter = response.Parameter;
        if (parameter?.Value is null)
        {
            Fail(secretRef, "no value returned");
            return;
        }

        if (parameter.Version != version)
        {
            // A selector answered with another version would make the reference a lie about which
            // secret the client uses. Refuse it rather than cache it under the wrong key.
            Fail(secretRef, string.Create(CultureInfo.InvariantCulture,
                $"version {parameter.Version?.ToString(CultureInfo.InvariantCulture) ?? "none"} returned"));
            return;
        }

        values[secretRef] = parameter.Value;
        failures.TryRemove(secretRef, out _);
    }

    private void Fail(string secretRef, string reason)
    {
        failures[secretRef] = reason;
        logger.LogWarning("Client secret {SecretRef} could not be read: {Reason}", secretRef, reason);
    }

    /// <summary>
    /// Splits <c>ssm:{name}:{version}</c> at the last colon, as <c>contracts/registry.schema.json</c>
    /// (<c>clientSecretRef</c>) shapes it. The validator has already checked the pattern for a
    /// registry entry; this is the guard for any other caller.
    /// </summary>
    private static bool TryParse(string secretRef, out string? name, out long version)
    {
        name = null;
        version = 0;

        if (!secretRef.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        int colon = secretRef.LastIndexOf(':');
        if (colon <= Prefix.Length)
        {
            return false;
        }

        string candidate = secretRef[Prefix.Length..colon];
        string digits = secretRef[(colon + 1)..];
        if (!candidate.StartsWith('/')
            || digits.Length == 0
            || digits[0] == '0'
            || !digits.All(char.IsAsciiDigit)
            || !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out version))
        {
            return false;
        }

        name = candidate;
        return true;
    }
}
