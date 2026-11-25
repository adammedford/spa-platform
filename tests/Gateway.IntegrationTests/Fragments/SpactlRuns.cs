using System.Text;
using System.Text.Json.Nodes;
using Gateway.TestHarness;
using Platform.Abstractions;
using Spactl;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>
/// <c>spactl</c> run in process over the harness's bucket (<see cref="GatewayHarness.Store"/>), as an
/// operator runs it over the cell's: <c>--bucket</c> names it, and a command that reads a platform
/// configuration gets the gateway's own (<see cref="GatewayHarness.PlatformConfiguration"/>), so the
/// bounds a write is checked against are the ones the gateway reads it under. A test reads a registry
/// write back into the harness and polls it with <see cref="GatewayHarness.ReloadRegistryDocumentAsync"/>.
/// </summary>
internal static class SpactlRuns
{
    private static readonly string Scratch = Path.Combine(AppContext.BaseDirectory, "scratch", "spactl");

    /// <summary>The commands that read <c>--config</c>; spactl refuses the option on any other.</summary>
    private static readonly string[][] WithConfig =
    [
        ["registry", "validate"], ["registry", "apply"], ["registry", "rollback"], ["release", "activate"],
        ["fragment", "activate"], ["app", "add"], ["app", "remove"],
    ];

    /// <summary>Runs one command and answers its exit code and both streams.</summary>
    public static Task<(int Exit, string Out, string Error)> RunAsync(GatewayHarness harness, CancellationToken ct, params string[] args) =>
        RunOverAsync(harness, harness?.Store!, ct, args);

    /// <summary>
    /// As <see cref="RunAsync"/>, with spactl's bucket reached through <paramref name="store"/>: a wrapper
    /// over <see cref="GatewayHarness.Store"/> that records or holds its writes.
    /// </summary>
    public static async Task<(int Exit, string Out, string Error)> RunOverAsync(
        GatewayHarness harness, IObjectStore store, CancellationToken ct, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(harness);
        List<string> full = [.. args, "--bucket", "harness-bucket"];
        if (WithConfig.Any(command => args.Length >= 2 && args[0] == command[0] && args[1] == command[1]))
        {
            full.AddRange(["--config", Write("platform-config.json", harness.PlatformConfiguration().ToJsonString())]);
        }

        using StringWriter output = new();
        using StringWriter error = new();
        using HttpClientHandler http = new();
        int exit = await SpactlApp.RunAsync(
            full,
            new SpactlEnvironment
            {
                Store = _ => store,
                History = _ => new NoHistory(),
                Http = http,
                Out = output,
                Error = error,
                PollInterval = TimeSpan.Zero,
            },
            ct);
        return (exit, output.ToString(), error.ToString());
    }

    /// <summary>Writes <paramref name="files"/> into a fresh directory, by relative path, and returns it.</summary>
    public static string Directory(IReadOnlyDictionary<string, byte[]> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        string root = Path.Combine(Scratch, Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        foreach ((string path, byte[] bytes) in files)
        {
            string file = Path.Combine(root, path);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, bytes);
        }

        return root;
    }

    /// <summary>Writes a document to a file of its own and returns its path, for <c>registry apply</c>.</summary>
    public static string Document(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Write("registry.json", document.ToJsonString());
    }

    /// <summary>The text of one object in the harness's bucket, or null when it holds none.</summary>
    public static async Task<string?> TextAsync(GatewayHarness harness, string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(harness);
        StoredObject? stored = await harness.Store.GetAsync(key, ifNoneMatch: null, ct);
        if (stored?.Body is not { } body)
        {
            return null;
        }

        using StreamReader reader = new(body, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>Text files, as the bytes <see cref="Directory"/> writes.</summary>
    public static Dictionary<string, byte[]> Files(params (string Path, string Text)[] files) =>
        files.ToDictionary(file => file.Path, file => Encoding.UTF8.GetBytes(file.Text), StringComparer.Ordinal);

    private static string Write(string name, string text)
    {
        System.IO.Directory.CreateDirectory(Scratch);
        string path = Path.Combine(Scratch, Guid.NewGuid().ToString("N") + "-" + name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>None of these rows rolls the registry back, so no history is read.</summary>
    private sealed class NoHistory : IRegistryHistory
    {
        public Task<IReadOnlyList<string>> ListVersionsAsync(string key, CancellationToken ct) =>
            throw new InvalidOperationException("these rows read no registry history.");

        public Task<Stream> OpenVersionAsync(string key, string versionId, CancellationToken ct) =>
            throw new InvalidOperationException("these rows read no registry history.");
    }
}
