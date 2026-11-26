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
            // release prefixes, the fragment entry files or the shells' import maps in. Useful in a pull
            // request; apply makes every check again.
            RegistryValidation validation = Validate(document, policy, liveRevision: null);
            await Out.WriteLineAsync(
                $"Valid offline: revision {validation.Revision}, {validation.Apps.Length} app(s). " +
                "The live revision and the release prefixes were not checked, and neither the entry file check " +
                "nor the import-map check ran: both need the bucket.");
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
        KeepSchemaVersion(earlier, live.Document);
        int result = await CheckAndWriteAsync(earlier.ToJsonString(Indented), live, policy, write: true, ct);
        await Out.WriteLineAsync($"Rolled back to the content of revision {restored}.");
        return result;
    }

    // ---- release --------------------------------------------------------------------------------

    /// <summary>
    /// Uploads a directory as <c>apps/{appId}/releases/{release}/</c>, every object written once
    /// (contracts/s3-layout.md, "Each key under a release is written once"; ADR-0009), through
    /// <see cref="PublishAsync"/>.
    ///
    /// <c>index.html</c> goes last. The prefix check every registry write makes looks for it, so a
    /// publish that stops part way leaves a prefix nothing can activate.
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
        List<string> files = ReleaseFiles(root);
        if (!files.Remove("index.html"))
        {
            throw new RefusedException($"{directory} has no index.html, which every release requires (contracts/s3-layout.md).");
        }

        files.Add("index.html");
        return await PublishAsync(AppNamespace(appId), release, root, files, ct);
    }

    /// <summary>
    /// Uploads a directory as <c>apps/{appId}/fragments/{fragmentId}/releases/{release}/</c>, as
    /// <see cref="ReleasePublishAsync"/> uploads a shell's, with the entry file in the place of
    /// <c>index.html</c> (contracts/s3-layout.md, "A fragment release is complete when its entry file
    /// exists"). It reads no registry, so a fragment's owner can publish before the fragment is
    /// onboarded, and a top-level <c>api/</c> is refused because <c>/fragments/{fragmentId}/api/**</c>
    /// is the fragment's APIs and maps to no key (<see cref="PlatformPaths.IsPublishableInFragment"/>).
    /// </summary>
    public async Task<int> FragmentPublishAsync(CancellationToken ct)
    {
        IReadOnlyList<string> operands = arguments.Operands(2, 4, "fragment publish <appId> <fragmentId> <directory> <release> [--entry <file>]");
        string appId = AppId(operands[0]);
        string fragmentId = FragmentId(operands[1]);
        string directory = operands[2];
        string release = Release(operands[3]);
        string entry = arguments.Option("--entry") ?? PlatformPaths.DefaultEntry;
        if (!PlatformPaths.IsEntryName(entry))
        {
            throw new UsageException(
                $"'{entry}' is not an entry file name: one .js file at the release root, at most " +
                $"{PlatformPaths.EntryMaxLength} characters (registry.schema.json#/$defs/entry).");
        }

        if (!Directory.Exists(directory))
        {
            throw new RefusedException($"No directory at {directory}.");
        }

        string root = Path.GetFullPath(directory);
        List<string> files = ReleaseFiles(root);
        if (!files.Remove(entry))
        {
            throw new RefusedException($"{directory} has no {entry}, the entry file a fragment release requires (contracts/s3-layout.md).");
        }

        if (files.FirstOrDefault(file => !PlatformPaths.IsPublishableInFragment(file)) is { } reserved)
        {
            throw new RefusedException(
                $"{directory} has {reserved} under a top-level api/: /fragments/{fragmentId}/api/ is reserved for the " +
                "fragment's APIs and maps to no release key (contracts/s3-layout.md). Nothing was written.");
        }

        files.Add(entry);
        return await PublishAsync(FragmentNamespace(appId, fragmentId), release, root, files, ct);
    }

    /// <summary>
    /// Writes <paramref name="files"/> under <c>{ns}releases/{release}/</c> in their order, the last
    /// being the file whose presence marks the release complete, every object written once: the store
    /// sends <c>If-None-Match: *</c> in a single-part upload. An object already present with the same
    /// bytes counts as published, so a publish that stopped can be run again; one with other bytes
    /// refuses the whole release, because the bucket denies deletes under <c>apps/</c> and that release
    /// name is spent.
    ///
    /// Each <c>assets/</c> file is claimed first (<see cref="ClaimAsync"/>), so an <c>assets/</c> URL
    /// names the same bytes in every release of its namespace.
    /// </summary>
    private async Task<int> PublishAsync(string ns, string release, string root, List<string> files, CancellationToken ct)
    {
        string prefix = ns + "releases/" + release + "/";
        IObjectStore objects = Store();
        int written = 0;
        int unchanged = 0;
        foreach (string file in files)
        {
            string key = prefix + file;
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(root, file), ct);
            if (file.StartsWith(AssetsPrefix, StringComparison.Ordinal))
            {
                await ClaimAsync(objects, ns, file, release, bytes, written, ct);
            }

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
    /// Creates the write-once claim <c>{ns}claims/{file}</c> for one <c>assets/</c> file, body
    /// <c>{"sha256", "release"}</c>, with <c>If-None-Match: *</c>, before the file is written
    /// (contracts/s3-layout.md, "An assets/ URL names the same bytes in its namespace"). Of two racing
    /// publishers exactly one create succeeds. A claim already there with the same digest lets the
    /// publish go on, which is how a stopped publish resumes and how a later release reuses an
    /// unchanged chunk; one with another digest refuses the release before its object is written. A
    /// claim that cannot be read back after its create was refused is answered "run the command
    /// again". A key published before claims existed has none, so its first claim is made now: there
    /// is no backfill.
    /// </summary>
    private static async Task ClaimAsync(IObjectStore objects, string ns, string file, string release, byte[] bytes, int written, CancellationToken ct)
    {
        string claimKey = ns + "claims/" + file;
        string digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        byte[] claim = Utf8.GetBytes(new JsonObject { ["sha256"] = digest, ["release"] = release }.ToJsonString());
        if (await objects.PutAsync(claimKey, new MemoryStream(claim, writable: false), ifMatch: null, ct))
        {
            return;
        }

        StoredObject? stored = await objects.GetAsync(claimKey, ifNoneMatch: null, ct);
        if (stored?.Body is not { } body)
        {
            throw new RefusedException(
                $"The claim at {claimKey} was refused but could not be read back: run the command again. " +
                $"{written} object(s) of this run were written.");
        }

        JsonObject? held;
        await using (body)
        {
            try
            {
                held = await JsonNode.ParseAsync(body, cancellationToken: ct) as JsonObject;
            }
            catch (JsonException)
            {
                held = null;
            }
        }

        string? heldDigest = held?["sha256"] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        if (string.Equals(heldDigest, digest, StringComparison.Ordinal))
        {
            return;
        }

        string holder = held?["release"] is JsonValue named && named.TryGetValue(out string? name) ? $"release {name}" : "an unreadable claim";
        throw new RefusedException(
            $"{ns}releases/{release}/{file} is refused: {ns}{file} is claimed by {holder} at {claimKey} with other bytes, " +
            "and an assets/ URL names the same bytes in every release (contracts/s3-layout.md). Give the changed " +
            $"file a new name. Nothing was written for that key; {written} object(s) of this run were written.");
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

    /// <summary>
    /// Points one fragment of a live entry at a published release: a change to that fragment's
    /// <c>release</c> and nothing else, so the shell's release and every policy the entry sends stay
    /// as they are (contracts/s3-layout.md, "a fragment's activation leaves its shell's release
    /// alone"). Rolling a fragment back is the same command with the earlier release; there is no
    /// fragment rollback command. Like every write it goes through <see cref="CheckAndWriteAsync"/>,
    /// which checks every entry and every fragment of the document, not only this one.
    /// </summary>
    public async Task<int> FragmentActivateAsync(CancellationToken ct)
    {
        IReadOnlyList<string> operands = arguments.Operands(2, 3, "fragment activate <appId> <fragmentId> <release> --config <platform-config.json>");
        string appId = AppId(operands[0]);
        string fragmentId = FragmentId(operands[1]);
        string release = Release(operands[2]);

        return await ChangeAsync(apps =>
        {
            JsonObject entry = Find(apps, appId) ?? throw new RefusedException($"The live registry has no app '{appId}'.");
            JsonObject fragment = FindFragment(entry, fragmentId)
                ?? throw new RefusedException($"The live entry of '{appId}' has no fragment '{fragmentId}'. Add it with app add first.");
            if (fragment["release"]?.GetValue<string>() == release)
            {
                return null;
            }

            fragment["release"] = release;
            return $"Activated release {release} of fragment {fragmentId} of {appId}.";
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
    ///
    /// Each fragment is completed the same way, from the outputs' <c>clients.{appId}.fragments.{fragmentId}</c>,
    /// and its release from <c>--fragment-release id=rel,id=rel</c> or the live entry's fragment of that
    /// id: a fragment new to the entry must be named there, and a live fragment's release it does not
    /// name is kept (contracts/app-definition.schema.json, the fragment definition).
    /// </summary>
    public async Task<int> AppAddAsync(CancellationToken ct)
    {
        string path = arguments.Operands(2, 1, "app add <definition.json> [--release <release>] [--fragment-release <id=release,...>] --config <platform-config.json>")[0];
        string text = ReadFile(path);
        PlatformPolicy policy = Policy();
        string? release = arguments.Option("--release") is { } value ? Release(value) : null;
        Dictionary<string, string> named = arguments.Option("--fragment-release") is { } listed ? FragmentReleases(listed) : [];

        AppDefinitionValidation validation = RegistryValidator.ValidateAppDefinition(text, policy);
        Warn(validation.Warnings);
        if (!validation.IsValid)
        {
            throw Refused($"{path} is not a valid app definition", validation.Errors);
        }

        JsonObject definition = JsonNode.Parse(text)!.AsObject();
        string appId = definition["appId"]!.GetValue<string>();
        JsonObject client = Outputs(ClientsNeedOutputs)["clients"]?[appId]?.AsObject()
            ?? throw new RefusedException(
                $"The infrastructure outputs name no client for '{appId}'. Create the app's client, write the outputs document again, then run app add again.");

        IReadOnlyList<string> fragmentIds = AppEntry.FragmentIds(definition);
        if (fragmentIds.FirstOrDefault(id => client["fragments"]?[id] is not JsonObject) is { } unknown)
        {
            throw new RefusedException(
                $"The infrastructure outputs name no client for fragment '{unknown}' of '{appId}' " +
                $"(clients.{appId}.fragments.{unknown}). Create the fragment's client, write the outputs document again, then run app add again.");
        }

        if (named.Keys.FirstOrDefault(id => !fragmentIds.Contains(id, StringComparer.Ordinal)) is { } stray)
        {
            throw new RefusedException($"--fragment-release names '{stray}', which {path} has no fragment for.");
        }

        return await ChangeAsync(apps =>
        {
            JsonObject? existing = Find(apps, appId);
            string entryRelease = release
                ?? existing?["release"]?.GetValue<string>()
                ?? throw new RefusedException($"--release is required: the live registry has no app '{appId}' yet.");
            Dictionary<string, string> fragmentReleases = new(StringComparer.Ordinal);
            foreach (string fragmentId in fragmentIds)
            {
                fragmentReleases[fragmentId] = named.GetValueOrDefault(fragmentId)
                    ?? (existing is null ? null : FindFragment(existing, fragmentId))?["release"]?.GetValue<string>()
                    ?? throw new RefusedException(
                        $"Fragment '{fragmentId}' is new to the entry of '{appId}': name its release with " +
                        $"--fragment-release {fragmentId}=<release>.");
            }

            JsonObject entry = AppEntry.Complete(definition, client, entryRelease, fragmentReleases);
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
    ///
    /// With <c>--instances</c>, every instance listed must have answered at that revision or later,
    /// each read going through the one URL to whichever instance the ALB picks (change request P2-5,
    /// decision 5). Without it, nothing here differs from before that option.
    /// </summary>
    public async Task<int> WaitAsync(CancellationToken ct)
    {
        arguments.Operands(1, 0, "wait --url <gateway origin> --revision <N> [--instances <id,id,...>] [--timeout <seconds>]");
        string url = arguments.Required("--url");
        long revision = ParseRevision("--revision", arguments.Required("--revision"));
        TimeSpan timeout = arguments.Option("--timeout") is { } seconds
            ? TimeSpan.FromSeconds(ParseRevision("--timeout", seconds, minimum: 0))
            : DefaultWaitTimeout;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? origin) || (origin.Scheme != Uri.UriSchemeHttps && origin.Scheme != Uri.UriSchemeHttp))
        {
            throw new UsageException("--url must be an absolute http or https URL.");
        }

        InstanceWait? instances = arguments.Option("--instances") is { } listed ? InstanceWait.Parse(listed) : null;

        Uri readyz = new(origin, ReadyzPath);
        using HttpClient http = new(environment.Http, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
        DateTimeOffset deadline = environment.Time.GetUtcNow() + timeout;
        string last = "no answer";
        while (true)
        {
            ReadyAnswer? answer = null;
            try
            {
                using HttpResponseMessage response = await http.GetAsync(readyz, ct);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    answer = ReadyAnswer.Parse(await response.Content.ReadAsStringAsync(ct));
                    last = answer.Revision is null ? "200 with no readable revision" : $"revision {answer.Revision}";
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

            if (instances is null)
            {
                if (answer?.Revision >= revision)
                {
                    await Out.WriteLineAsync($"Ready: {readyz} reports revision {answer.Revision}.");
                    return 0;
                }

                if (environment.Time.GetUtcNow() >= deadline)
                {
                    throw new RefusedException(
                        $"{readyz} did not report revision {revision} or later within {timeout.TotalSeconds} seconds; the last answer was {last}.");
                }
            }
            else
            {
                if (instances.Record(answer, last, revision) is { } unlisted)
                {
                    await Out.WriteLineAsync(
                        $"Instance {unlisted} answered at {readyz} but is not listed; it is not waited for.");
                }

                if (instances.AllAt(revision))
                {
                    await Out.WriteLineAsync($"Ready: {readyz} reports revision {revision} or later from {instances.Summary()}.");
                    return 0;
                }

                if (environment.Time.GetUtcNow() >= deadline)
                {
                    throw new RefusedException(
                        $"{readyz} did not report revision {revision} or later from every listed instance within " +
                        $"{timeout.TotalSeconds} seconds: {instances.Behind(revision)}{instances.Unattributed()}.");
                }
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
        KeepSchemaVersion(document, live.Document);
        int result = await CheckAndWriteAsync(document.ToJsonString(Indented), live, policy, write: true, ct);
        await Out.WriteLineAsync(done);
        return result;
    }

    /// <summary>
    /// Every check a registry document must pass before it is written, then, when
    /// <paramref name="write"/>, the write: conditional on the live object's ETag, or create-only when
    /// there is no live object (<see cref="IObjectStore.PutAsync"/>). Throws when refused.
    ///
    /// The checks run on the proposed document, every shell and every fragment of it, not only the one
    /// a command changed, so a write never leaves a live entry the gateway cannot serve
    /// (contracts/s3-layout.md, "A fragment release is complete when its entry file exists"; the
    /// fragments spec, #961, "The import map").
    /// </summary>
    private async Task<int> CheckAndWriteAsync(string document, LiveDocument live, PlatformPolicy policy, bool write, CancellationToken ct)
    {
        RegistryValidation validation = Validate(document, policy, live.Revision);
        JsonObject proposed = JsonNode.Parse(document)!.AsObject();

        // registry.schema.json#/properties/schemaVersion: "spactl writes 2 on a document's first fragment
        // and never lowers it". Every command that builds a document keeps it (KeepSchemaVersion);
        // registry apply writes the operator's file as written, so a file that declares less is refused
        // rather than rewritten.
        if (live.Document is { } current && SchemaVersionOf(proposed) < SchemaVersionOf(current))
        {
            throw new RefusedException(
                $"Refused: the document declares schemaVersion {SchemaVersionOf(proposed)}, below the live registry's " +
                $"{SchemaVersionOf(current)}, and spactl never lowers it (registry.schema.json#/properties/schemaVersion). " +
                "Nothing was written.");
        }

        IObjectStore objects = Store();
        List<string> missing = [];
        List<string> maps = [];
        foreach (AppDefinition app in validation.Apps)
        {
            // The release prefix exists in S3: contracts/README.md gives this rule to spactl alone. A
            // prefix exists when its index.html does, the one object contracts/s3-layout.md requires.
            // The same read gives the document the import-map check scans.
            string prefix = ReleasePrefix(app.AppId, app.Release);
            string? index = await ReadTextAsync(objects, prefix + "index.html", ct);
            if (index is null)
            {
                missing.Add($"{prefix} (release {app.Release} of {app.AppId}) has no index.html: publish it first.");
            }

            // Only the entry file of a fragment is read, never its index.html: the shell's document is
            // the one the browser loads, and the one place an import map may be.
            foreach (FragmentDefinition fragment in app.Fragments)
            {
                string key = FragmentReleasePrefix(app.AppId, fragment.FragmentId, fragment.Release) + fragment.Entry;
                if (!await ExistsAsync(objects, key, ct))
                {
                    missing.Add(
                        $"{key} (release {fragment.Release} of fragment {fragment.FragmentId} of {app.AppId}): " +
                        "no entry file: publish it first.");
                }
            }

            if (index is not null)
            {
                CheckImportMap(app, index, policy, live.Document, maps);
            }
        }

        if (missing.Count > 0 || maps.Count > 0)
        {
            List<string> reasons = [];
            if (missing.Count > 0)
            {
                reasons.Add("a release is not published.");
                reasons.AddRange(missing);
            }

            if (maps.Count > 0)
            {
                reasons.Add("an import map would not load under the policy the gateway would send.");
                reasons.AddRange(maps);
            }

            throw new RefusedException("Refused: " + string.Join(Environment.NewLine + "  ", reasons));
        }

        if (!write)
        {
            await Out.WriteLineAsync(
                $"Valid: revision {validation.Revision}, {validation.Apps.Length} app(s), every release published, " +
                "every fragment's entry file present and every import map admitted.");
            return 0;
        }

        if (!await objects.PutAsync(RegistryKey, new MemoryStream(Utf8.GetBytes(document), writable: false), live.ETag, ct))
        {
            throw new RefusedException(
                "The live registry changed after spactl read it, so nothing was written: run the command again " +
                "to apply against the new live version.");
        }

        await Out.WriteLineAsync($"Applied revision {validation.Revision}: {validation.Apps.Length} app(s).");
        return 0;
    }

    /// <summary>
    /// The import-map check of one shell of the proposed document (the fragments spec, #961, "The
    /// import map"): its <c>index.html</c>, from the proposed release, is scanned by
    /// <see cref="ImportMap.Read"/>, and a map it holds must be admitted by a hash source in the
    /// <c>script-src</c> the gateway would send for the entry, the pure merge of
    /// <see cref="ContentSecurityPolicy.Merge"/>: the baseline, the shell's <c>csp</c>, then each
    /// fragment's in registry order. A refusal names the source to add. Two things warn and do not
    /// refuse: a CSP <c>&lt;meta&gt;</c> in the document, and a hash source the live entry lists that
    /// the proposed one drops, after which a release whose map it admitted cannot be activated until it
    /// is added back (re-add with <c>app add</c>, then activate).
    /// </summary>
    private void CheckImportMap(AppDefinition shell, string index, PlatformPolicy policy, JsonObject? live, List<string> refusals)
    {
        string at = ReleasePrefix(shell.AppId, shell.Release) + "index.html";
        ImportMap.Scan scan = ImportMap.Read(index);
        if (scan.CspMeta)
        {
            WarnLine(
                $"{at} carries a Content-Security-Policy <meta>, which a browser enforces beside the gateway's header; " +
                "this check reads only the header's policy.");
        }

        ImmutableArray<string> proposed = shell.Csp.TryGetValue("script-src", out ImmutableArray<string> sources) ? sources : [];
        foreach (string hash in LiveHashSources(live, shell.AppId).Where(hash => !proposed.Contains(hash, StringComparer.Ordinal)))
        {
            WarnLine(
                $"the write removes {hash} from the csp.script-src of {shell.AppId}, which the live registry lists: a release " +
                "whose import map it admits cannot be activated until app add lists it again.");
        }

        if (scan.Refusal is { } refusal)
        {
            refusals.Add($"{at} (release {shell.Release} of {shell.AppId}) is refused: {refusal}");
            return;
        }

        if (scan.Map is not { } map)
        {
            return;
        }

        // form-action is the only directive the issuer's origin reaches; script-src, read here, is
        // the same whatever it is. The validator has refused an issuer the policy does not configure.
        string formAction = policy.Issuers[shell.OAuth.Issuer].BrowserOrigin.OriginalString;
        if (!ImportMap.Admits(map, ContentSecurityPolicy.Merge(shell, formAction).SourcesOf("script-src")))
        {
            refusals.Add(
                $"{at} (release {shell.Release} of {shell.AppId}): no script-src source admits its import map. " +
                $"Add {ImportMap.Sha256Source(map)} to the entry's csp.script-src with app add, then activate.");
        }
    }

    /// <summary>The hash sources the live entry of <paramref name="appId"/> lists in its own <c>csp.script-src</c>.</summary>
    private static IEnumerable<string> LiveHashSources(JsonObject? live, string appId) =>
        live?["apps"] is JsonArray apps && Find(apps, appId)?["csp"]?["script-src"] is JsonArray listed
            ? listed.OfType<JsonValue>()
                .Select(value => value.TryGetValue(out string? source) ? source : null)
                .OfType<string>()
                .Where(ImportMap.IsHashSource)
            : [];

    /// <summary>
    /// registry.schema.json#/properties/schemaVersion: "spactl writes 2 on a document's first fragment
    /// and never lowers it". A document a command builds or restores declares 2 once any entry carries
    /// fragments, and never less than the live document declared.
    /// </summary>
    private static void KeepSchemaVersion(JsonObject document, JsonObject? live)
    {
        long version = SchemaVersionOf(document);
        bool fragments = document["apps"] is JsonArray apps && apps.OfType<JsonObject>().Any(entry => entry["fragments"] is not null);
        long kept = Math.Max(Math.Max(version, fragments ? 2 : 1), live is null ? 1 : SchemaVersionOf(live));
        if (kept != version)
        {
            document["schemaVersion"] = kept;
        }
    }

    private static long SchemaVersionOf(JsonObject document) =>
        document["schemaVersion"] is { } value && JsonInteger.TryRead(value, out long version) ? version : 1;

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

    /// <summary>A key's text, or null when the store confirms it absent. A refused read propagates.</summary>
    private static async Task<string?> ReadTextAsync(IObjectStore objects, string key, CancellationToken ct)
    {
        StoredObject? stored = await objects.GetAsync(key, ifNoneMatch: null, ct);
        if (stored?.Body is not { } body)
        {
            return null;
        }

        using StreamReader reader = new(body, Utf8);
        return await reader.ReadToEndAsync(ct);
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

    /// <summary>
    /// The infrastructure outputs named by <c>--outputs</c>, checked against
    /// contracts/infra-outputs.schema.json. There is no default path: a command that needs the document
    /// and is not given one is refused, before any store is built. <paramref name="missing"/> is the
    /// refusal, which names what the command could have been given instead.
    /// </summary>
    private JsonObject Outputs(string missing)
    {
        if (outputs is not null)
        {
            return outputs;
        }

        string path = arguments.Option("--outputs") ?? throw new UsageException(missing);
        string text = ReadFile(path);
        ImmutableArray<RegistryIssue> issues = ContractDocuments.ValidateInfraOutputs(text);
        if (!issues.IsEmpty)
        {
            throw Refused($"{path} is not valid infrastructure outputs", issues);
        }

        return outputs = JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>The refusal of <c>app add</c> with no <c>--outputs</c>: the client comes only from its <c>clients</c>.</summary>
    private const string ClientsNeedOutputs =
        "--outputs <outputs.json> is required: app add reads the app's client id and secret reference from its clients.";

    /// <summary>The refusal of a store command with neither <c>--outputs</c> nor <c>--bucket</c>.</summary>
    private const string StoreNeedsOutputs =
        "--outputs <outputs.json> is required to find the bucket and Region, or --bucket with --region names them.";

    /// <summary>The bucket: <c>--bucket</c> when given, else the infrastructure outputs' <c>bucket</c>.</summary>
    private StoreLocation Location() =>
        arguments.Option("--bucket") is { } bucket
            ? new StoreLocation(bucket, arguments.Option("--region"))
            : new StoreLocation(
                Outputs(StoreNeedsOutputs)["bucket"]!.GetValue<string>(),
                Outputs(StoreNeedsOutputs)["region"]!.GetValue<string>());

    private IObjectStore Store() => store ??= environment.Store(Location());

    private static string ReadFile(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : throw new RefusedException($"No file at {path}.");

    /// <summary>The first segment of a release key that names the immutable, claimed files (contracts/s3-layout.md).</summary>
    private const string AssetsPrefix = "assets/";

    /// <summary>contracts/s3-layout.md: a shell's namespace. Every value is checked against registry.schema.json first.</summary>
    private static string AppNamespace(string appId) => $"apps/{appId}/";

    /// <summary>contracts/s3-layout.md: one fragment's namespace, under its shell's.</summary>
    private static string FragmentNamespace(string appId, string fragmentId) => $"apps/{appId}/fragments/{fragmentId}/";

    private static string ReleasePrefix(string appId, string release) => AppNamespace(appId) + $"releases/{release}/";

    private static string FragmentReleasePrefix(string appId, string fragmentId, string release) =>
        FragmentNamespace(appId, fragmentId) + $"releases/{release}/";

    /// <summary>Every file under a release directory, relative and with '/' separators, in ordinal order.</summary>
    private static List<string> ReleaseFiles(string root) =>
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];

    private static string AppId(string value) =>
        ContractDocuments.IsAppId(value) ? value : throw new UsageException($"'{value}' is not an appId (registry.schema.json#/$defs/appId).");

    /// <summary>registry.schema.json#/$defs/fragmentId: the appId rule, so the same check.</summary>
    private static string FragmentId(string value) =>
        ContractDocuments.IsAppId(value) ? value : throw new UsageException($"'{value}' is not a fragmentId (registry.schema.json#/$defs/fragmentId).");

    /// <summary>
    /// <c>--fragment-release id=rel,id=rel</c>: one comma-separated option, not a repeated one, because
    /// the parser keeps only a repeated option's last value (as <c>wait --instances</c>).
    /// </summary>
    private static Dictionary<string, string> FragmentReleases(string listed)
    {
        Dictionary<string, string> releases = new(StringComparer.Ordinal);
        foreach (string item in listed.Split(','))
        {
            int equals = item.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                throw new UsageException("--fragment-release must be a comma-separated list of <fragmentId>=<release>.");
            }

            string fragmentId = FragmentId(item[..equals]);
            if (!releases.TryAdd(fragmentId, Release(item[(equals + 1)..])))
            {
                throw new UsageException($"--fragment-release names '{fragmentId}' twice.");
            }
        }

        return releases;
    }

    private static JsonObject? FindFragment(JsonObject entry, string fragmentId) =>
        (entry["fragments"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(fragment => fragment["fragmentId"] is JsonValue id && id.TryGetValue(out string? value) && value == fragmentId);

    private static string Release(string value) =>
        ContractDocuments.IsRelease(value) ? value : throw new UsageException($"'{value}' is not a release name (registry.schema.json#/$defs/release).");

    private static JsonObject? Find(JsonArray apps, string appId) =>
        apps.OfType<JsonObject>().FirstOrDefault(entry => entry["appId"] is JsonValue id && id.TryGetValue(out string? value) && value == appId);

    private static long? RevisionOf(JsonObject document) =>
        // Task 2.6: live and history documents use the same integer spellings as the shared validator.
        document["revision"] is { } value && JsonInteger.TryRead(value, out long revision) ? revision : null;

    /// <summary>
    /// A <c>/readyz</c> 200 body, read by member name: a reader takes the members it names and ignores
    /// the rest (contracts/pipeline.md, Health endpoints).
    /// </summary>
    private sealed record ReadyAnswer(long? Revision, string? Instance)
    {
        public static ReadyAnswer Parse(string body)
        {
            try
            {
                return JsonNode.Parse(body) is JsonObject document
                    ? new ReadyAnswer(
                        RevisionOf(document),
                        document["instance"] is JsonValue value && value.TryGetValue(out string? instance) && instance.Length > 0 ? instance : null)
                    : new ReadyAnswer(null, null);
            }
            catch (JsonException)
            {
                return new ReadyAnswer(null, null);
            }
        }
    }

    /// <summary>
    /// The bookkeeping of <c>wait --instances</c> (change request P2-5, decision 5). An answer counts for
    /// the instance it names; one that names none (a non-200, which carries no body, or a 200 with no
    /// readable revision or instance) counts for none and is kept as the last unattributed answer. An
    /// instance once seen at the revision stays counted.
    /// </summary>
    private sealed class InstanceWait
    {
        private readonly Dictionary<string, long?> lastRevision;
        private readonly HashSet<string> unlistedSeen = new(StringComparer.Ordinal);
        private string? unattributed;

        private InstanceWait(IEnumerable<string> ids) =>
            lastRevision = ids.Distinct(StringComparer.Ordinal).ToDictionary(id => id, _ => (long?)null, StringComparer.Ordinal);

        /// <summary>
        /// One comma-separated option, not a repeated one: today's parser keeps a repeated option's last
        /// value silently. An empty element is the only usage error (P2-5, decision 5).
        /// </summary>
        public static InstanceWait Parse(string listed)
        {
            string[] ids = listed.Split(',');
            return ids.Any(string.IsNullOrWhiteSpace)
                ? throw new UsageException("--instances must be a comma-separated list of instance ids with no empty element.")
                : new InstanceWait(ids);
        }

        /// <summary>Records an answer; returns the id of an unlisted instance the first time it is heard.</summary>
        public string? Record(ReadyAnswer? answer, string description, long revision)
        {
            if (answer is not { Instance: { } id, Revision: { } reported })
            {
                unattributed = description;
                return null;
            }

            if (lastRevision.TryGetValue(id, out long? before))
            {
                // Stays counted once at the revision: a later read cannot unsee it.
                lastRevision[id] = before >= revision ? before : reported;
                return null;
            }

            return unlistedSeen.Add(id) ? id : null;
        }

        public bool AllAt(long revision) => lastRevision.Values.All(reported => reported >= revision);

        public string Summary() =>
            string.Join(", ", lastRevision.Select(entry => $"instance {entry.Key} at revision {entry.Value}"));

        public string Behind(long revision) =>
            string.Join("; ", lastRevision
                .Where(entry => !(entry.Value >= revision))
                .Select(entry => entry.Value is { } reported
                    ? $"instance {entry.Key} last answered revision {reported}"
                    : $"instance {entry.Key} never answered"));

        public string Unattributed() =>
            unattributed is null ? "" : $"; the last answer that named no instance was {unattributed}";
    }

    private static long ParseRevision(string option, string value, long minimum = 1) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) && parsed >= minimum
            ? parsed
            : throw new UsageException($"{option} must be an integer of at least {minimum}.");

    private void Warn(ImmutableArray<RegistryIssue> warnings)
    {
        foreach (RegistryIssue warning in warnings)
        {
            WarnLine(warning.ToString());
        }
    }

    private void WarnLine(string warning) => environment.Error.WriteLine($"warning: {warning}");

    private static RefusedException Refused(string what, ImmutableArray<RegistryIssue> errors) =>
        new($"{what}:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", errors)}");

    /// <summary>What was live when a command read it. All null when there is no registry yet.</summary>
    private sealed record LiveDocument(JsonObject? Document, string? ETag, long? Revision);
}
