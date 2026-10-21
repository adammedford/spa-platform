using System.Text.Json.Nodes;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// The deployed cell as the browser suite sees it (#209), read from the infrastructure outputs
/// document (contracts/infra-outputs.schema.json: "The scripts and the deployed tests read it, and
/// none of them reads Terraform state") and from the app definitions in <c>fixtures/apps/</c>, whose
/// <c>host</c> is the authority the registry and every callback URL carry, port included
/// (docs/plan/05-infrastructure-and-cost.md, Workstation access, step 5).
///
/// <para>It holds names only. Every secret the document points at (a client secret, a test user's
/// password) is a parameter name here, and the value is read from Parameter Store when it is used.</para>
/// </summary>
public sealed class Cell
{
    private readonly JsonObject document;

    private Cell(JsonObject document)
    {
        this.document = document;
        Region = Field("region");
        Bucket = Field("bucket");
        LogGroup = Field("logGroup");
        ProviderHost = Field("cognitoDomain");
        Section("clients");
        Section("testUsers");
    }

    /// <summary>The cell's Region, which every AWS CLI call names (docs/agents/aws-rules.md: one Region).</summary>
    public string Region { get; }

    /// <summary>The one private bucket: the registry and every release.</summary>
    public string Bucket { get; }

    /// <summary>The task's CloudWatch log group, which both containers write to under their own stream prefix (infra/modules/cell/ecs.tf).</summary>
    public string LogGroup { get; }

    /// <summary>The managed-login domain, host only.</summary>
    public string ProviderHost { get; }

    /// <summary>
    /// Cognito's authorization endpoint, <c>/oauth2/authorize</c> on the managed-login domain
    /// (https://docs.aws.amazon.com/cognito/latest/developerguide/authorization-endpoint.html).
    /// </summary>
    public string AuthorizeEndpoint => $"https://{ProviderHost}/oauth2/authorize";

    /// <summary>
    /// Cognito's logout endpoint, where the gateway's Cognito profile sends a logout
    /// (src/Platform.Identity/IdpProfiles.cs; https://docs.aws.amazon.com/cognito/latest/developerguide/logout-endpoint.html).
    /// </summary>
    public string EndSessionEndpoint => $"https://{ProviderHost}/logout";

    /// <summary>The parent domain of every app host, without the leading dot, from app 1's definition.</summary>
    public string Domain
    {
        get
        {
            string host = HostOf("app1").Split(':')[0];
            return host[(host.IndexOf('.', StringComparison.Ordinal) + 1)..];
        }
    }

    public static Cell Parse(string json) =>
        new(JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("the infrastructure outputs document is not a JSON object."));

    /// <summary>
    /// The resolver rule that sends every app host to the tunnel's local port, as the hosts file and
    /// <c>tools/scripts/tunnel.sh</c> do for a person's browser. Chromium's <c>MAP</c> replaces the
    /// port too, so the URL keeps the registry's port while the connection goes to the tunnel's.
    /// </summary>
    public string ResolverRule(int tunnelPort) => $"MAP *.{Domain} 127.0.0.1:{tunnelPort}";

    /// <summary>An app: its definition's host, and the client the apps root made for it. It carries no secret.</summary>
    public HarnessApp App(string appId)
    {
        string clientId = (document["clients"] as JsonObject)?[appId]?["clientId"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"the infrastructure outputs name no client for {appId}: is it applied in the apps root?");
        string host = HostOf(appId);
        return new HarnessApp(appId, host, new Uri("https://" + host + "/"), clientId, ClientSecret: "", SecretRef: "");
    }

    /// <summary>The Parameter Store name of a test user's generated password, never the password.</summary>
    public string PasswordParameter(string user) =>
        (document["testUsers"] as JsonObject)?[user]?["passwordParameter"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"the infrastructure outputs name no test user {user}.");

    private static string HostOf(string appId) => FixtureFiles.AppDefinition(appId)["host"]!.GetValue<string>();

    /// <summary>A name the document gives as a string; anything else there (an object, a number) is refused, never read as empty.</summary>
    private string Field(string name) => document[name] is JsonValue value && value.TryGetValue(out string? text) && text.Length > 0
        ? text
        : throw new InvalidOperationException($"the infrastructure outputs document has no {name}.");

    /// <summary>A section the document gives as an object, keyed by app or by user.</summary>
    private void Section(string name)
    {
        if (document[name] is not JsonObject)
        {
            throw new InvalidOperationException($"the infrastructure outputs document has no {name}.");
        }
    }
}
