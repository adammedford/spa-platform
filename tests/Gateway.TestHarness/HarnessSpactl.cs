using System.Text.Json.Nodes;
using Spactl;

namespace Gateway.TestHarness;

/// <summary>
/// <c>spactl</c> run in process against a <see cref="GatewayHarness"/>, through its own entry point
/// (<see cref="SpactlApp.RunAsync"/>), as an adopter's operator runs it against a cell (#1516):
///
/// <list type="bullet">
/// <item>its bucket is the harness's <see cref="GatewayHarness.Store"/>, which spactl's writes stand for,
/// whatever bucket the command names;</item>
/// <item><c>wait</c> reads <c>/readyz</c> through the harness's stand-in load balancer at <see cref="Url"/>,
/// so each read reaches whichever host it picks, as through the ALB;</item>
/// <item><see cref="ConfigPath"/> is the harness's platform configuration as a file, for <c>--config</c>,
/// so a write is bounded as the gateway reads it;</item>
/// <item><see cref="OutputsPath"/> is an infrastructure outputs document the test writes as an adopter's
/// own tooling writes it (<see cref="WriteClient"/>, <see cref="RemoveClient"/>), for <c>--outputs</c>.</item>
/// </list>
///
/// The arguments are passed as the test writes them, with nothing added, because spactl refuses an
/// option a command does not read: the test names <c>--config</c> and <c>--outputs</c> where the
/// reference procedure does. Nothing under <c>tools/spactl</c> is changed for it.
/// </summary>
public sealed class HarnessSpactl
{
    /// <summary>The origin <c>wait</c> is given: any gateway name reaches the hosts through the load balancer.</summary>
    public const string Url = "https://cell.spa-poc.test:8443/";

    /// <summary>The bucket and Region the outputs document names. The harness's store answers for any bucket.</summary>
    public const string Bucket = "harness-bucket";

    private const string Region = "us-east-1";

    private readonly GatewayHarness harness;
    private readonly string directory;
    private readonly JsonObject clients = [];

    public HarnessSpactl(GatewayHarness harness)
    {
        ArgumentNullException.ThrowIfNull(harness);
        this.harness = harness;
        directory = Path.Combine(AppContext.BaseDirectory, "scratch", "harness-spactl", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ConfigPath = Path.Combine(directory, "platform-config.json");
        File.WriteAllText(ConfigPath, harness.PlatformConfiguration().ToJsonString());
        OutputsPath = Path.Combine(directory, "outputs.json");
        WriteOutputs();
    }

    /// <summary>The harness's platform configuration, for <c>--config</c>.</summary>
    public string ConfigPath { get; }

    /// <summary>The infrastructure outputs document, for <c>--outputs</c>: the bucket, the Region and the clients written so far.</summary>
    public string OutputsPath { get; }

    /// <summary>
    /// Writes <c>clients.{appId}</c> into the outputs document, as whatever registers the client and
    /// writes its secret writes it (contracts/infra-outputs.schema.json, <c>clients</c>).
    /// </summary>
    public void WriteClient(string appId, string clientId, string secretParameter, long secretVersion)
    {
        clients[appId] = new JsonObject
        {
            ["clientId"] = clientId,
            ["secretParameter"] = secretParameter,
            ["secretVersion"] = secretVersion,
        };
        WriteOutputs();
    }

    /// <summary>Deletes <c>clients.{appId}</c> from the outputs document.</summary>
    public void RemoveClient(string appId)
    {
        clients.Remove(appId);
        WriteOutputs();
    }

    /// <summary>Writes a document into a file of its own, an app definition for instance, and answers its path.</summary>
    public string WriteFile(string name, JsonNode document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + "-" + name);
        File.WriteAllText(path, document.ToJsonString());
        return path;
    }

    /// <summary>Runs one command line and answers its exit code and both streams.</summary>
    public async Task<SpactlRun> RunAsync(CancellationToken ct, params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        using LoadBalancerTransport http = harness.CreateLoadBalancer();
        int exit = await SpactlApp.RunAsync(
            args,
            new SpactlEnvironment
            {
                Store = _ => harness.Store,
                History = _ => throw new InvalidOperationException("the harness keeps no registry history: no row here rolls the registry back."),
                Http = http,
                Out = output,
                Error = error,
                PollInterval = TimeSpan.FromMilliseconds(20),
            },
            ct);
        return new SpactlRun(args, exit, output.ToString(), error.ToString());
    }

    private void WriteOutputs() =>
        File.WriteAllText(OutputsPath, new JsonObject
        {
            ["region"] = Region,
            ["bucket"] = Bucket,
            ["clients"] = clients.DeepClone(),
        }.ToJsonString());
}

/// <summary>One <c>spactl</c> run: its command line, exit code and both streams. Its text names the run, for assertion output.</summary>
public sealed record SpactlRun(IReadOnlyList<string> Args, int Exit, string Out, string Error)
{
    public override string ToString() =>
        $"spactl {string.Join(' ', Args)} exited {Exit}.{Environment.NewLine}stdout: {Out}{Environment.NewLine}stderr: {Error}";
}
