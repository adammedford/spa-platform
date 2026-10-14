using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Platform.Registry;

/// <summary>
/// <c>Registry:*</c> from platform configuration (contracts/platform-config.schema.json, <c>Registry</c>).
/// Bound by <c>AddPlatformRegistry</c>.
/// </summary>
public sealed class RegistrySourceOptions
{
    public string? Bucket { get; set; }

    public string? Key { get; set; }

    /// <summary>The conditional-GET interval, jittered by up to 20%.</summary>
    public int PollSeconds { get; set; }

    internal bool IsComplete => !string.IsNullOrEmpty(Bucket) && !string.IsNullOrEmpty(Key) && PollSeconds >= 1;
}

/// <summary>
/// Runs <see cref="RegistrySource.RefreshAsync"/> once at start-up and then every
/// <c>Registry:PollSeconds</c>, jittered (plan row 2.2). It never stops the host: a
/// <see cref="BackgroundService"/> that throws stops it by default
/// (https://learn.microsoft.com/dotnet/core/compatibility/core-libraries/6.0/hosting-exception-handling),
/// and a gateway that cannot read its registry must keep answering <c>/healthz</c> and serving the last
/// good snapshot.
///
/// The source is resolved here, inside the loop, rather than injected: building it builds the S3 and
/// Parameter Store clients, whose constructors throw with no region configured. With no
/// <c>Registry:*</c> configuration at all, as in CI's G1a smoke test, it is never built.
/// </summary>
public sealed class RegistryPoller : BackgroundService
{
    /// <summary>docs/plan/02-contracts.md, Platform configuration: "jittered by up to 20%".</summary>
    private const double Jitter = 0.2;

    private readonly IServiceProvider services;
    private readonly IOptions<RegistrySourceOptions> options;
    private readonly TimeProvider time;
    private readonly ILogger<RegistryPoller> logger;

    public RegistryPoller(
        IServiceProvider services,
        IOptions<RegistrySourceOptions> options,
        TimeProvider time,
        ILogger<RegistryPoller> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        this.services = services;
        this.options = options;
        this.time = time;
        this.logger = logger;
    }

    /// <summary>
    /// The wait before the next poll: the interval less up to a fifth of it. Jitter only ever shortens
    /// the wait, so a registry change is live within one poll interval (contracts/s3-layout.md,
    /// "Activation is a registry change"), while gateway tasks started together drift apart rather
    /// than reading S3 in step.
    /// </summary>
    /// <param name="sample">A uniform sample in [0, 1).</param>
    public static TimeSpan NextDelay(TimeSpan interval, double sample) =>
        interval * (1 - (Jitter * Math.Clamp(sample, 0, 1)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Return to the host at once: the first poll must not hold up start-up, or /healthz with it.
        await Task.Yield();

        TimeSpan interval;
        try
        {
            RegistrySourceOptions settings = options.Value;
            if (!settings.IsComplete)
            {
                logger.LogError(
                    "Registry:Bucket, Registry:Key and Registry:PollSeconds (at least 1) are not all configured. " +
                    "The registry is not polled, nothing goes live, and /readyz stays not ready.");
                return;
            }

            interval = TimeSpan.FromSeconds(settings.PollSeconds);
        }
        catch (Exception ex)
        {
            // The binder throws on a value it cannot convert, such as a PollSeconds that is not a number.
            logger.LogError(
                "Registry:* could not be read ({Error}: {Message}). The registry is not polled, nothing goes live, and /readyz stays not ready.",
                ex.GetType().Name, ex.Message);
            return;
        }

        RegistrySource? source = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                source ??= services.GetRequiredService<RegistrySource>();
                await source.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // RefreshAsync reports its own failures; what reaches here is building the source, such as
                // an AWS client with no region. The SDK's messages here name configuration, not values.
                logger.LogError(
                    "The registry could not be polled ({Error}: {Message}). Revision {Revision} stays live; the next poll is within {Interval}.",
                    ex.GetType().Name, ex.Message, services.GetRequiredService<LiveRegistry>().Current.Revision, interval);
            }

            try
            {
                await Task.Delay(NextDelay(interval, Random.Shared.NextDouble()), time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
