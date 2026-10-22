using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Platform.Hosting;

/// <summary>
/// The gateway instance <c>/readyz</c> names (GLOSSARY.md, Instance; contracts/pipeline.md -> Health
/// endpoints; change request P2-5). In a cell its id is the ECS task id; anywhere else it is an id
/// generated for this host, never the machine name or the pid, so two hosts in one test process are two
/// instances.
///
/// <c>AddPlatformHosting</c> registers one only when none is registered, so a harness gives a host a
/// deterministic id by registering its own first (P2-5, decision 2). There is no configuration key for
/// it.
/// </summary>
public sealed class GatewayInstance
{
    /// <summary>
    /// The variable the ECS agent sets in every container with the task metadata endpoint v4's base URI
    /// (https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-metadata-endpoint-v4.html).
    /// </summary>
    public const string MetadataVariable = "ECS_CONTAINER_METADATA_URI_V4";

    /// <summary>
    /// How long the one read at start-up may take. The endpoint is link-local and answers in
    /// milliseconds; a read that has not answered by then is not going to, and the id falls back.
    /// </summary>
    internal static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(2);

    private readonly Task<string> id;

    /// <summary>An instance with a fixed id, as a harness registers one.</summary>
    public GatewayInstance(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        this.id = Task.FromResult(id);
    }

    private GatewayInstance(Task<string> id) => this.id = id;

    /// <summary>
    /// The id. Complete once the start-up read has answered, failed or timed out; it never faults, so a
    /// reader awaits at most <see cref="MetadataTimeout"/>, once.
    /// </summary>
    internal Task<string> IdAsync => id;

    /// <summary>
    /// Starts the one read of the metadata endpoint and returns without waiting for it, so the start is
    /// never held up by it (P2-5, decision 2). The handler is disposed when the read ends.
    /// </summary>
    internal static GatewayInstance Start(string? metadataUri, ILogger log)
    {
        HttpMessageHandler handler = new SocketsHttpHandler();
        return new GatewayInstance(ReadThenDispose());

        async Task<string> ReadThenDispose()
        {
            try
            {
                return await ResolveAsync(metadataUri, handler, MetadataTimeout, log, CancellationToken.None);
            }
            finally
            {
                handler.Dispose();
            }
        }
    }

    /// <summary>
    /// The ECS task id from <c>${ECS_CONTAINER_METADATA_URI_V4}/task</c>, whose <c>TaskARN</c> ends in
    /// it, or a generated id when the variable is absent or the read fails in any way. Never throws for
    /// the endpoint's sake: an instance that cannot learn its task id still serves (P2-5, decision 2).
    /// </summary>
    internal static async Task<string> ResolveAsync(
        string? metadataUri, HttpMessageHandler handler, TimeSpan timeout, ILogger log, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(metadataUri))
        {
            string generated = Generated();
            log.LogInformation(
                "The ECS task metadata endpoint is not present ({Variable} is not set); this instance is {Instance}.",
                MetadataVariable, generated);
            return generated;
        }

        string? reason;
        try
        {
            using HttpClient http = new(handler, disposeHandler: false) { Timeout = timeout };
            using HttpResponseMessage response = await http.GetAsync(metadataUri.TrimEnd('/') + "/task", ct);
            reason = response.IsSuccessStatusCode
                ? null
                : $"it answered HTTP {(int)response.StatusCode}";
            if (reason is null)
            {
                string? taskId = TaskId(await response.Content.ReadAsStringAsync(ct));
                if (taskId is not null)
                {
                    return taskId;
                }

                reason = "its answer carried no task id";
            }
        }
        catch (HttpRequestException exception)
        {
            reason = $"the read failed ({exception.HttpRequestError})";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            reason = $"it did not answer within {timeout.TotalSeconds} seconds";
        }

        // The reason names a status or an error kind, never the answer: the ARN in it carries the
        // account id and the Region (P2-5, decision 1).
        string fallback = Generated();
        log.LogInformation(
            "The ECS task metadata endpoint was not usable: {Reason}; this instance is {Instance}.", reason, fallback);
        return fallback;
    }

    /// <summary>
    /// <c>TaskARN</c>'s last path segment, accepted only as lowercase hex, which is the shape of an ECS
    /// task id (arn:aws:ecs:region:account:task/cluster/id). Anything else is not used: the value is
    /// written unauthenticated on <c>/readyz</c>, so it must never be the ARN or a piece of it.
    /// </summary>
    private static string? TaskId(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("TaskARN", out JsonElement arn)
                || arn.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string value = arn.GetString()!;
            string last = value[(value.LastIndexOf('/') + 1)..];
            return last.Length > 0 && value.StartsWith("arn:", StringComparison.Ordinal) && last.All(char.IsAsciiHexDigitLower)
                ? last
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Generated() => Guid.NewGuid().ToString("N");
}
