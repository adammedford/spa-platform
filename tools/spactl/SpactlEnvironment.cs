using Amazon;
using Amazon.S3;
using Platform.Abstractions;
using Platform.Registry;

namespace Spactl;

/// <summary>Which bucket a command works on, and its region when the infrastructure outputs name one.</summary>
public sealed record StoreLocation(string Bucket, string? Region);

/// <summary>
/// Everything a command reaches outside the process. <see cref="Live"/> is the real one; the tests pass
/// the Platform.Testing in-memory store and a <c>/readyz</c> stub instead, so a whole round trip runs in
/// memory (plan row 2.6, "Done when").
/// </summary>
public sealed class SpactlEnvironment
{
    public required Func<StoreLocation, IObjectStore> Store { get; init; }

    public required Func<StoreLocation, IRegistryHistory> History { get; init; }

    /// <summary>What <c>spactl wait</c> sends its <c>/readyz</c> requests through.</summary>
    public required HttpMessageHandler Http { get; init; }

    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    /// <summary>The pause between two <c>/readyz</c> reads.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// S3 with the credentials of whoever runs spactl (contracts/s3-layout.md, "spactl writes, with the
    /// credentials of whoever runs it"), from the SDK's default chain
    /// (https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/creds-assign.html).
    /// </summary>
    public static SpactlEnvironment Live() => new()
    {
        Store = location => new S3ObjectStore(Client(location), location.Bucket),
        History = location => new S3RegistryHistory(Client(location), location.Bucket),
        Http = new SocketsHttpHandler(),
        Out = Console.Out,
        Error = Console.Error,
    };

    private static AmazonS3Client Client(StoreLocation location) =>
        location.Region is null
            ? new AmazonS3Client()
            : new AmazonS3Client(RegionEndpoint.GetBySystemName(location.Region));
}
