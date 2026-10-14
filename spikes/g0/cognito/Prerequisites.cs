using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Spike;

/// <summary>
/// Q9's six prerequisites for the Cognito run, plus the browser. Every one is checked and reported before anything starts,
/// so a refusal names everything missing at once. Nothing is bound: port 8443 is tested by connecting to it.
/// </summary>
public static class Prerequisites
{
    public sealed record Item(string Name, bool Present, string Detail);

    public static async Task<(List<Item> Items, IdentityOutputs? Outputs, List<AppFixture> Apps)> CheckAsync(
        string root, string? outputsPath, string certsDir, string user, bool manual, Redactor redactor)
    {
        var items = new List<Item>();

        // 1 and 2: the identity root (1.1) and the apps root (1.8), seen through the outputs document they feed.
        var appProblems = new List<string>();
        var apps = Config.LoadApps(root, appProblems);
        var candidates = outputsPath is not null ? [outputsPath] : Config.DefaultOutputsPaths(root);
        var path = candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        var outputProblems = new List<string>();
        var outputs = Config.LoadOutputs(path, user, outputProblems);
        if (outputs is null && outputsPath is null && !candidates.Any(File.Exists))
            outputProblems[0] = $"no infra outputs document at {string.Join(" or ", candidates.Select(c => Path.GetRelativePath(root, c)))}; run spikes/g0/cognito/outputs-from-terraform.sh after applying infra/envs/poc/idp and infra/envs/poc/apps";

        var idpProblems = outputProblems.Where(p => !p.Contains("clients", StringComparison.Ordinal)).ToList();
        if (outputs is not null)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var r = await http.GetAsync(outputs.Issuer.TrimEnd('/') + "/.well-known/openid-configuration");
                var issuer = "";
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync());
                    issuer = doc.RootElement.TryGetProperty("issuer", out var i) ? i.GetString() ?? "" : "";
                }
                catch (System.Text.Json.JsonException) { }
                if (!r.IsSuccessStatusCode) idpProblems.Add($"the issuer's discovery document answered {(int)r.StatusCode}");
                else if (issuer.TrimEnd('/') != outputs.Issuer.TrimEnd('/')) idpProblems.Add("the issuer's discovery document does not name that issuer");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                idpProblems.Add("the issuer's discovery document did not load: " + AwsCredentials.OneLine(ex.Message));
            }
        }
        items.Add(new Item("1.1 applied: the user pool, its managed-login domain and the test users (infra/envs/poc/idp)", idpProblems.Count == 0,
            idpProblems.Count == 0 ? $"issuer and cognitoDomain from {Config.Display(root, path)}; discovery loads and names the issuer" : string.Join("; ", idpProblems)));

        var clientProblems = outputProblems.Where(p => p.Contains("clients", StringComparison.Ordinal)).Concat(appProblems).ToList();
        if (outputProblems.FirstOrDefault(p => p.StartsWith("no infra outputs", StringComparison.Ordinal) || p.StartsWith("the infra outputs document", StringComparison.Ordinal)) is { } noDocument)
            clientProblems.Insert(0, noDocument);
        items.Add(new Item("1.8 applied: an OAuth client, branding style and secret parameter for app1 and app2 (infra/envs/poc/apps)", clientProblems.Count == 0,
            clientProblems.Count == 0 ? "clients.app1 and clients.app2 in the outputs document; fixtures/apps/app1.json and app2.json. The 1.8 README's manual code-flow login is the person's to confirm" : string.Join("; ", clientProblems)));

        // 3: certs.sh issue and trust (1.6a). The browser checks the trust; the harness can only check the files.
        var chain = Path.Combine(certsDir, "spa-platform-poc-wildcard.fullchain.pem");
        var key = Path.Combine(certsDir, "spa-platform-poc-wildcard.key");
        string? certProblem = null;
        if (!File.Exists(chain) || !File.Exists(key)) certProblem = $"no certificate and key at {Path.GetRelativePath(root, chain)} and {Path.GetFileName(key)}: run tools/scripts/certs.sh issue, then tools/scripts/certs.sh trust";
        else
        {
            try { using var c = X509Certificate2.CreateFromPemFile(chain, key); }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException) { certProblem = "the certificate does not load: " + AwsCredentials.OneLine(ex.Message); }
        }
        items.Add(new Item("certs.sh run on this workstation (1.6a): the *.spa-poc.test certificate, trusted", certProblem is null,
            certProblem ?? $"{Path.GetFileName(chain)} and its key load; trust is checked by the browser at the first login"));

        // 4: the hosts-file line.
        var hostProblems = new List<string>();
        foreach (var a in apps)
        {
            var name = a.Host.Split(':')[0];
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(name);
                if (!addresses.Any(IPAddress.IsLoopback)) hostProblems.Add($"{name} resolves to {string.Join(", ", addresses.Select(x => x.ToString()))}, not 127.0.0.1");
            }
            catch (SocketException) { hostProblems.Add($"{name} does not resolve"); }
        }
        items.Add(new Item("the hosts-file line: 127.0.0.1 app1.spa-poc.test app2.spa-poc.test app3.spa-poc.test", hostProblems.Count == 0,
            hostProblems.Count == 0 ? "app1 and app2 resolve to loopback" : string.Join("; ", hostProblems) + ". Add the line to /etc/hosts"));

        // 5: port 8443 free. Connecting, not binding: something accepting there (tunnel.sh) means it is taken.
        var taken = false;
        try
        {
            using var probe = new TcpClient();
            var connect = probe.ConnectAsync(IPAddress.Loopback, 8443);
            taken = await Task.WhenAny(connect, Task.Delay(1500)) == connect && probe.Connected;
        }
        catch (SocketException) { }
        items.Add(new Item("local port 8443 free (stop tunnel.sh first: the harness takes 8443 itself)", !taken,
            taken ? "something accepts connections on 127.0.0.1:8443" : "nothing accepts connections on 127.0.0.1:8443"));

        // 6: AWS credentials (assumption A7), then the parameters the run reads.
        var missing = AwsCredentials.Missing();
        string? parameterProblem = null;
        if (missing is null && outputs is not null && outputProblems.Count == 0)
        {
            var secrets = new ParameterStoreSecrets(outputs, redactor);
            try
            {
                foreach (var id in Config.AppIds.Where(outputs.Clients.ContainsKey)) await secrets.ClientSecretAsync(id);
                await secrets.PasswordAsync(user);
            }
            catch (Exception ex) when (ex is Amazon.Runtime.AmazonServiceException or Amazon.Runtime.AmazonClientException)
            {
                parameterProblem = "Parameter Store refused a read: " + AwsCredentials.OneLine(ex.Message);
            }
        }
        items.Add(new Item("AWS credentials as in assumption A7, able to read the secret and password parameters", missing is null && parameterProblem is null,
            missing is not null ? "no AWS credentials: " + missing
            : parameterProblem ?? (outputs is null || outputProblems.Count > 0 ? "credentials found; the parameters are read once the outputs document is complete" : "credentials found; every parameter the run needs was read")));

        // The browser (Q2), unless a person drives the login.
        if (!manual)
        {
            var browser = await PlaywrightDriver.MissingAsync();
            items.Add(new Item("Chromium for Microsoft.Playwright 1.63.0 (or pass --manual)", browser is null,
                browser is null ? "it launches" : browser + ". Run: dotnet run -c Release -- --install-browser"));
        }

        return (items, outputs, apps);
    }
}
