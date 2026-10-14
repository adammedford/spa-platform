using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Platform.Abstractions;
using Platform.Registry;

namespace Spactl;

/// <summary>
/// The commands behind <see cref="SpactlApp"/>. Every registry change goes through
/// <see cref="CheckAndWriteAsync"/>: the one validator the gateway uses (ADR-0002), the release prefix
/// check only spactl makes (contracts/README.md), and a write conditional on the live version
/// (contracts/s3-layout.md). Messages name appIds, hosts, releases, keys and parameter names, which
/// the registry already publishes; never a document body (AGENTS.md, N-16).
/// </summary>
internal sealed class Commands(Arguments arguments, SpactlEnvironment environment)
{
    /// <summary>contracts/s3-layout.md: the one registry object.</summary>
    public const string RegistryKey = "registry/registry.json";

    /// <summary>infra-outputs.schema.json's own description: "infra/envs/poc/outputs.json, written by workstream 1".</summary>
    public const string DefaultOutputs = "infra/envs/poc/outputs.json";

    /// <summary>The page the readiness check reads (contracts/pipeline.md, Health endpoints).</summary>
    private const string ReadyzPath = "/readyz";

    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private JsonObject? outputs;
    private IObjectStore? store;

    private TextWriter Out => environment.Out;

    // ---- registry -------------------------------------------------------------------------------

    public async Task<int> RegistryValidateAsync(CancellationToken ct)
    {
        string path = arguments.Operands(2, 1, "registry validate <registry.json> --config <platform-config.json> [--offline]")[0];
        string document = ReadFile(path);
        PlatformPolicy policy = Policy();

        if (arguments.Has("--offline"))
        {
            // The document and the policy only: no live revision to exceed, and no bucket to find the
            // release prefixes in. Useful in a pull request; apply makes every check again.
            RegistryValidation validation = Validate(document, policy, liveRevision: null);
            await Out.WriteLineAsync(
                $"Valid offline: revision {validation.Revision}, {validation.Apps.Length} app(s). " +
                "The live revision and the release prefixes were not checked.");
            return 0;
        }

        return await CheckAndWriteAsync(document, await ReadLiveAsync(ct), policy, write: false, ct);
    }

    public async Task<int> RegistryApplyAsync(CancellationToken ct)
    {
        string path = arguments.Operands(2, 1, "registry apply <registry.json> --config <platform-config.json>")[0];
        string document = ReadFile(path);
        PlatformPolicy policy = Policy();

        // The file's own revision is applied as written; the validator refuses one that does not exceed
        // the live revision (contracts/README.md, "revision must be greater than the live one").
        return await CheckAndWriteAsync(document, await ReadLiveAsync(ct), policy, write: true, ct);
    }

    /// <summary>
    /// Republishes an earlier version of the registry object under a new revision
    /// (docs/plan/05-infrastructure-and-cost.md: "republishes the previous object version under a new
    /// revision"; registry.schema.json: "a rollback is the old content under a new revision").
    ///
    /// The earlier version is chosen by its place in the history, never by ETag: S3 ETags are content
    /// hashes, so a registry that went A, B, A holds two versions with one ETag. Without
    /// <c>--to-revision</c> it is the version before the newest; with it, the newest earlier version
    /// whose document carries that revision. A second rollback therefore returns to the first state,
    /// because the version before a rollback is the one it rolled back from; <c>--to-revision</c> is
    /// how to go further back.
    /// </summary>
    public async Task<int> RegistryRollbackAsync(CancellationToken ct)
    {
        arguments.Operands(2, 0, "registry rollback [--to-revision <N>] --config <platform-config.json>");
        long? toRevision = arguments.Option("--to-revision") is { } value ? ParseRevision("--to-revision", value) : null;
        PlatformPolicy policy = Policy();

        // The live document is read before the history, so the write below is conditional on what was
        // live when the choice was made: a write in between fails it rather than being rolled over.
        LiveDocument live = await ReadLiveAsync(ct);
        if (live.Document is null)
        {
            throw new RefusedException("There is no live registry to roll back.");
        }

        IRegistryHistory history = environment.History(Location());
        IReadOnlyList<string> versions = await history.ListVersionsAsync(RegistryKey, ct);

        JsonObject? earlier = null;
        foreach (string versionId in versions.Skip(1))
        {
            JsonObject candidate = await ReadVersionAsync(history, versionId, ct);
            if (toRevision is null || RevisionOf(candidate) == toRevision)
            {
                earlier = candidate;
                break;
            }
        }

        if (earlier is null)
        {
            throw new RefusedException(toRevision is null
                ? "The registry has no earlier version to roll back to."
                : $"No earlier version of the registry carries revision {toRevision}.");
        }

        long? restored = RevisionOf(earlier);
        earlier["revision"] = live.Revision!.Value + 1;
        int result = await CheckAndWriteAsync(earlier.ToJsonString(Indented), live, policy, write: true, ct);
        await Out.WriteLineAsync($"Rolled back to the content of revision {restored}.");
        return result;
    }

    // ---- release --------------------------------------------------------------------------------

    /// <summary>
    /// Uploads a directory as <c>apps/{appId}/releases/{release}/</c>, every object written once
    /// (contracts/s3-layout.md, "A release prefix is written once"; ADR-0009): the store sends
    /// <c>If-None-Match: *</c> in a single-part upload.
    ///
    /// <c>index.html</c> goes last. The prefix check that <c>activate</c>, <c>app add</c> and
    /// <c>apply</c> make looks for it, so a publish that stops part way leaves a prefix nothing can
    /// activate. An object already present with the same bytes counts as published, so a publish that
    /// stopped can be run again; one with other bytes refuses the whole release, because the bucket
    /// denies deletes under <c>apps/</c> and that release name is spent.
    /// </summary>
    public async Task<int> ReleasePublishAsync(CancellationToken ct)
    {
        IReadOnlyList<string> operands = arguments.Operands(2, 3, "release publish <appId> <directory> <release>");
        string appId = AppId(operands[0]);
        string directory = operands[1];
        string release = Release(operands[2]);

        if (!Directory.Exists(directory))
        {
            throw new RefusedException($"No directory at {directory}.");
        }

        string root = Path.GetFullPath(directory);
        List<string> files = [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];
        if (!files.Remove("index.html"))
        {
            throw new RefusedException($"{directory} has no index.html, which every release requires (contracts/s3-layout.md).");
        }

        files.Add("index.html");

        string prefix = ReleasePrefix(appId, release);
        IObjectStore objects = Store();
        int written = 0;
        int unchanged = 0;
        foreach (string file in files)
        {
            string key = prefix + file;
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(root, file), ct);
            if (await objects.PutAsync(key, new MemoryStream(bytes, writable: false), ifMatch: null, ct))
            {
                written++;
            }
            else if (await SameBytesAsync(objects, key, bytes, ct))
            {
                unchanged++;
            }
            else
            {
                throw new RefusedException(
                    $"{key} already holds other content. A release prefix is written once (ADR-0009): " +
                    $"publish under a new release name. {written} object(s) of this run were written.");
            }
        }

        await Out.WriteLineAsync(
            $"Published {prefix}: {written} object(s) written, {unchanged} already present and identical.");
        return 0;
    }

    /// <summary>
    /// Points an entry at a published release (contracts/s3-layout.md, "Activation is a registry
    /// change"). Rolling a release back is the same command with the previous release.
    /// </summary>
    public async Task<int> ReleaseActivateAsync(CancellationToken ct)
    {
        IReadOnlyList<string> operands = arguments.Operands(2, 2, "release activate <appId> <release> --config <platform-config.json>");
        string appId = AppId(operands[0]);
        string release = Release(operands[1]);

        return await ChangeAsync(apps =>
        {
            JsonObject entry = Find(apps, appId) ?? throw new RefusedException($"The live registry has no app '{appId}'.");
            if (entry["release"]?.GetValue<string>() == release)
            {
                return null;
            }

            entry["release"] = release;
            return $"Activated release {release} of {appId}.";
        }, createIfAbsent: false, ct);
    }

    // ---- app ------------------------------------------------------------------------------------

    /// <summary>
    /// Completes an app definition into a registry entry and applies it (docs/plan/02-contracts.md:
    /// "spactl app add completes it from the infrastructure outputs and applies the registry, so each
    /// app is described once"). The client id and the secret reference come from the outputs' entry
    /// for the appId; the release from <c>--release</c>, or the live entry's when the app is already in
    /// the registry.
    ///
    /// An appId the registry already holds is replaced in place, so the secret-rotation runbook
    /// (docs/plan/05-infrastructure-and-cost.md, "Rotating a client secret") is this same command run
    /// after the parameter version rises.
    /// </summary>
    public async Task<int> AppAddAsync(CancellationToken ct)
    {
        string path = arguments.Operands(2, 1, "app add <definition.json> [--release <release>] --config <platform-config.json>")[0];
        string text = ReadFile(path);
        PlatformPolicy policy = Policy();
        string? release = arguments.Option("--release") is { } value ? Release(value) : null;

        AppDefinitionValidation validation = RegistryValidator.ValidateAppDefinition(text, policy);
        Warn(validation.Warnings);
        if (!validation.IsValid)
        {
            throw Refused($"{path} is not a valid app definition", validation.Errors);
        }

        JsonObject definition = JsonNode.Parse(text)!.AsObject();
        string appId = definition["appId"]!.GetValue<string>();
        JsonObject client = Outputs()["clients"]?[appId]?.AsObject()
            ?? throw new RefusedException(
                $"The infrastructure outputs name no client for '{appId}'. Apply the apps root, which creates it, then run app add again.");

        return await ChangeAsync(apps =>
        {
            JsonObject? existing = Find(apps, appId);
            string entryRelease = release
                ?? existing?["release"]?.GetValue<string>()
                ?? throw new RefusedException($"--release is required: the live registry has no app '{appId}' yet.");
            JsonObject entry = AppEntry.Complete(definition, client, entryRelease);
            if (existing is null)
            {
                apps.Add(entry);
                return $"Added {appId} at release {entryRelease}.";
            }

            apps[apps.IndexOf(existing)] = entry;
            return $"Replaced {appId}, at release {entryRelease}.";
        }, createIfAbsent: true, ct);
    }

    public async Task<int> AppRemoveAsync(CancellationToken ct)
    {
        string appId = AppId(arguments.Operands(2, 1, "app remove <appId> --config <platform-config.json>")[0]);

        // An empty apps array is a valid document (contracts/README.md); deployment, not spactl, refuses
        // to start a gateway against one (task 1.7).
        return await ChangeAsync(apps =>
        {
            JsonObject entry = Find(apps, appId) ?? throw new RefusedException($"The live registry has no app '{appId}'.");
            apps.Remove(entry);
            return $"Removed {appId}.";
        }, createIfAbsent: false, ct);
    }

    // ---- wait -----------------------------------------------------------------------------------

    /// <summary>
    /// Reads <c>/readyz</c> until it reports <c>--revision</c> or a later one (contracts/pipeline.md:
    /// "The ALB health check and spactl wait both read /readyz"). A later revision counts, because
    /// another apply may land between this one and its wait, and the gateway then went past it.
    /// </summary>
    public async Task<int> WaitAsync(CancellationToken ct)
    {
        arguments.Operands(1, 0, "wait --url <gateway origin> --revision <N> [--timeout <seconds>]");
        string url = arguments.Required("--url");
        long revision = ParseRevision("--revision", arguments.Required("--revision"));
        TimeSpan timeout = arguments.Option("--timeout") is { } seconds
            ? TimeSpan.FromSeconds(ParseRevision("--timeout", seconds, minimum: 0))
            : DefaultWaitTimeout;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? origin) || (origin.Scheme != Uri.UriSchemeHttps && origin.Scheme != Uri.UriSchemeHttp))
        {
            throw new UsageException("--url must be an absolute http or https URL.");
        }

        Uri readyz = new(origin, ReadyzPath);
        using HttpClient http = new(environment.Http, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
        DateTimeOffset deadline = environment.Time.GetUtcNow() + timeout;
        string last = "no answer";
        while (true)
        {
            long? reported = null;
            try
            {
                using HttpResponseMessage response = await http.GetAsync(readyz, ct);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    reported = ReadyRevision(await response.Content.ReadAsStringAsync(ct));
                    last = reported is null ? "200 with no readable revision" : $"revision {reported}";
                }
                else
                {
                    last = $"HTTP {(int)response.StatusCode}";
                }
            }
            catch (HttpRequestException exception)
            {
                last = exception.Message;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                last = "no answer within 10 seconds";
            }

            if (reported >= revision)
            {
                await Out.WriteLineAsync($"Ready: {readyz} reports revision {reported}.");
                return 0;
            }

            if (environment.Time.GetUtcNow() >= deadline)
            {
                throw new RefusedException(
                    $"{readyz} did not report revision {revision} or later within {timeout.TotalSeconds} seconds; the last answer was {last}.");
            }

            await Task.Delay(environment.PollInterval, environment.Time, ct);
        }
    }

    // ---- the shared path ------------------------------------------------------------------------

    /// <summary>
    /// Reads the live registry, applies <paramref name="change"/> to its <c>apps</c>, raises the revision
    /// by one and writes it through <see cref="CheckAndWriteAsync"/>. A change that returns null changed
    /// nothing, and nothing is written.
    /// </summary>
    private async Task<int> ChangeAsync(Func<JsonArray, string?> change, bool createIfAbsent, CancellationToken ct)
    {
        PlatformPolicy policy = Policy();
        LiveDocument live = await ReadLiveAsync(ct);

        JsonObject document = live.Document?.DeepClone().AsObject()
            ?? (createIfAbsent
                ? new JsonObject { ["schemaVersion"] = 1, ["revision"] = 0, ["apps"] = new JsonArray() }
                : throw new RefusedException("There is no live registry. Write the first one with registry apply or app add."));
        JsonArray apps = document["apps"] as JsonArray
            ?? throw new RefusedException("The live registry has no apps array; repair it with registry apply.");

        string? done = change(apps);
        if (done is null)
        {
            await Out.WriteLineAsync("Nothing to change: the live registry already says so.");
            return 0;
        }

        document["revision"] = (live.Revision ?? 0) + 1;
        int result = await CheckAndWriteAsync(document.ToJsonString(Indented), live, policy, write: true, ct);
        await Out.WriteLineAsync(done);
        return result;
    }

    /// <summary>
    /// Every check a registry document must pass before it is written, then, when
    /// <paramref name="write"/>, the write: conditional on the live object's ETag, or create-only when
    /// there is no live object (<see cref="IObjectStore.PutAsync"/>). Throws when refused.
    /// </summary>
    private async Task<int> CheckAndWriteAsync(string document, LiveDocument live, PlatformPolicy policy, bool write, CancellationToken ct)
    {
        RegistryValidation validation = Validate(document, policy, live.Revision);

        // The release prefix exists in S3: contracts/README.md gives this rule to spactl alone. A prefix
        // exists when its index.html does, the one object contracts/s3-layout.md requires.
        IObjectStore objects = Store();
        List<string> missing = [];
        foreach (AppDefinition app in validation.Apps)
        {
            string prefix = ReleasePrefix(app.AppId, app.Release);
            if (!await ExistsAsync(objects, prefix + "index.html", ct))
            {
                missing.Add($"{prefix} (release {app.Release} of {app.AppId}) has no index.html: publish it first.");
            }
        }

        if (missing.Count > 0)
        {
            throw new RefusedException("Refused: " + string.Join(Environment.NewLine + "  ", ["a release prefix does not exist.", .. missing]));
        }

        if (!write)
        {
            await Out.WriteLineAsync($"Valid: revision {validation.Revision}, {validation.Apps.Length} app(s), every release published.");
            return 0;
        }

        if (!await objects.PutAsync(RegistryKey, new MemoryStream(Utf8.GetBytes(document), writable: false), live.ETag, ct))
        {
            throw new RefusedException(
                "The live registry changed after spactl read it, so nothing was written. Run the command again " +
                "to apply against the new live version.");
        }

        await Out.WriteLineAsync($"Applied revision {validation.Revision}: {validation.Apps.Length} app(s).");
        return 0;
    }

    private RegistryValidation Validate(string document, PlatformPolicy policy, long? liveRevision)
    {
        RegistryValidation validation = RegistryValidator.Validate(document, policy, liveRevision);
        Warn(validation.Warnings);
        return validation.IsValid ? validation : throw Refused("The registry document is refused", validation.Errors);
    }

    private async Task<LiveDocument> ReadLiveAsync(CancellationToken ct)
    {
        StoredObject? stored = await Store().GetAsync(RegistryKey, ifNoneMatch: null, ct);
        if (stored is null)
        {
            return new LiveDocument(null, null, null);
        }

        string text;
        using (StreamReader reader = new(stored.Body!))
        {
            text = await reader.ReadToEndAsync(ct);
        }

        // Only the revision and the apps array are read from the live document; the validator judges the
        // document that replaces it. One that cannot be read is not written over by a change command.
        JsonObject? document = null;
        try
        {
            document = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
        }

        long? revision = document is null ? null : RevisionOf(document);
        if (revision is null)
        {
            throw new RefusedException($"The live registry at {RegistryKey} has no readable revision; spactl will not write over it.");
        }

        return new LiveDocument(document, stored.ETag, revision);
    }

    private static async Task<JsonObject> ReadVersionAsync(IRegistryHistory history, string versionId, CancellationToken ct)
    {
        await using Stream body = await history.OpenVersionAsync(RegistryKey, versionId, ct);
        try
        {
            return await JsonNode.ParseAsync(body, cancellationToken: ct) as JsonObject
                ?? throw new RefusedException($"Version {versionId} of {RegistryKey} is not a JSON object.");
        }
        catch (JsonException)
        {
            throw new RefusedException($"Version {versionId} of {RegistryKey} is not JSON.");
        }
    }

    private static async Task<bool> ExistsAsync(IObjectStore objects, string key, CancellationToken ct)
    {
        StoredObject? stored = await objects.GetAsync(key, ifNoneMatch: null, ct);
        if (stored?.Body is { } body)
        {
            await body.DisposeAsync();
        }

        return stored is not null;
    }

    private static async Task<bool> SameBytesAsync(IObjectStore objects, string key, byte[] bytes, CancellationToken ct)
    {
        StoredObject? stored = await objects.GetAsync(key, ifNoneMatch: null, ct);
        if (stored?.Body is not { } body)
        {
            return false;
        }

        await using (body)
        {
            using MemoryStream copy = new();
            await body.CopyToAsync(copy, ct);
            return copy.ToArray().AsSpan().SequenceEqual(bytes);
        }
    }

    // ---- inputs ---------------------------------------------------------------------------------

    /// <summary>
    /// <c>Policy:*</c> and <c>Issuers:*</c> from the <c>--config</c> file, checked against
    /// contracts/platform-config.schema.json and then read by the gateway's own reader, so spactl and
    /// the gateway bound the registry the same way.
    /// </summary>
    private PlatformPolicy Policy()
    {
        string path = arguments.Option("--config")
            ?? throw new UsageException("--config <platform-config.json> is required: its Policy and Issuers sections bound the registry.");
        string text = ReadFile(path);
        ImmutableArray<RegistryIssue> issues = ContractDocuments.ValidatePlatformConfiguration(text);
        if (!issues.IsEmpty)
        {
            throw Refused($"{path} is not a valid platform configuration", issues);
        }

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Utf8.GetBytes(text), writable: false))
            .Build();
        return PlatformPolicyConfiguration.Read(configuration);
    }

    /// <summary>The infrastructure outputs, checked against contracts/infra-outputs.schema.json.</summary>
    private JsonObject Outputs()
    {
        if (outputs is not null)
        {
            return outputs;
        }

        string path = arguments.Option("--outputs") ?? DefaultOutputs;
        string text = ReadFile(path);
        ImmutableArray<RegistryIssue> issues = ContractDocuments.ValidateInfraOutputs(text);
        if (!issues.IsEmpty)
        {
            throw Refused($"{path} is not valid infrastructure outputs", issues);
        }

        return outputs = JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>The bucket: <c>--bucket</c> when given, else the infrastructure outputs' <c>bucket</c>.</summary>
    private StoreLocation Location() =>
        arguments.Option("--bucket") is { } bucket
            ? new StoreLocation(bucket, arguments.Option("--region"))
            : new StoreLocation(Outputs()["bucket"]!.GetValue<string>(), Outputs()["region"]!.GetValue<string>());

    private IObjectStore Store() => store ??= environment.Store(Location());

    private static string ReadFile(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : throw new RefusedException($"No file at {path}.");

    /// <summary>contracts/s3-layout.md. Both values are checked against registry.schema.json first.</summary>
    private static string ReleasePrefix(string appId, string release) => $"apps/{appId}/releases/{release}/";

    private static string AppId(string value) =>
        ContractDocuments.IsAppId(value) ? value : throw new UsageException($"'{value}' is not an appId (registry.schema.json#/$defs/appId).");

    private static string Release(string value) =>
        ContractDocuments.IsRelease(value) ? value : throw new UsageException($"'{value}' is not a release name (registry.schema.json#/$defs/release).");

    private static JsonObject? Find(JsonArray apps, string appId) =>
        apps.OfType<JsonObject>().FirstOrDefault(entry => entry["appId"] is JsonValue id && id.TryGetValue(out string? value) && value == appId);

    private static long? RevisionOf(JsonObject document) =>
        // Task 2.6: live and history documents use the same integer spellings as the shared validator.
        document["revision"] is { } value && JsonInteger.TryRead(value, out long revision) ? revision : null;

    private static long? ReadyRevision(string body)
    {
        try
        {
            return JsonNode.Parse(body) is JsonObject document ? RevisionOf(document) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long ParseRevision(string option, string value, long minimum = 1) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) && parsed >= minimum
            ? parsed
            : throw new UsageException($"{option} must be an integer of at least {minimum}.");

    private void Warn(ImmutableArray<RegistryIssue> warnings)
    {
        foreach (RegistryIssue warning in warnings)
        {
            environment.Error.WriteLine($"warning: {warning}");
        }
    }

    private static RefusedException Refused(string what, ImmutableArray<RegistryIssue> errors) =>
        new($"{what}:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", errors)}");

    /// <summary>What was live when a command read it. All null when there is no registry yet.</summary>
    private sealed record LiveDocument(JsonObject? Document, string? ETag, long? Revision);
}
