using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// What the deployed stack reads from AWS while it runs (#209): a test user's password from Parameter
/// Store and the gateway's log lines from CloudWatch Logs. Both go through the AWS CLI v2, the tool
/// <c>tools/scripts/</c> already reach the cell with (docs/dependencies.md), because the project's
/// profile signs in with <c>login_session</c>, which the CLI resolves on its own and the .NET SDK
/// resolves only with an assembly no project references yet (#206). Every call names the cell's
/// Region from the outputs document.
/// </summary>
public sealed partial class CellAws(Cell cell, ICommandRunner runner)
{
    /// <summary>The stream prefix the gateway container writes under; the mock API shares the group under its own (infra/modules/cell/ecs.tf, <c>awslogs-stream-prefix</c>).</summary>
    public const string GatewayStreamPrefix = "gateway/";

    /// <summary>How often a wait for a log line asks CloudWatch again.</summary>
    public TimeSpan PollEvery { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A test user's generated password, read decrypted from the parameter the outputs name
    /// (infra/modules/idp-cognito/main.tf, <c>aws_ssm_parameter.test_user_password</c>). It is held in
    /// a <see cref="Password"/>, which prints as nothing, and goes from there into the login form alone.
    /// </summary>
    public async Task<Password> PasswordAsync(string user, CancellationToken ct)
    {
        // https://docs.aws.amazon.com/cli/latest/reference/ssm/get-parameter.html
        string value = (await runner.RunAsync("aws",
            ["ssm", "get-parameter", "--name", cell.PasswordParameter(user), "--with-decryption",
             "--query", "Parameter.Value", "--output", "text", "--region", cell.Region], ct)).TrimEnd('\r', '\n');
        return value.Length > 0 ? new Password(value) : throw new InvalidOperationException($"the password parameter of test user {user} is empty.");
    }

    /// <summary>
    /// Every JSON line the gateway container wrote from <paramref name="since"/>, oldest first. A line
    /// that is not JSON (the host's own start-up text) is not a log line of the contract
    /// (contracts/telemetry.md) and is left out, as is any stream of the group that is not the gateway's.
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> GatewayLinesAsync(DateTimeOffset since, CancellationToken ct)
    {
        // The CLI follows nextToken itself and merges the pages, unless --no-paginate is given:
        // https://docs.aws.amazon.com/cli/latest/userguide/cli-usage-pagination.html
        string output = await runner.RunAsync("aws",
            ["logs", "filter-log-events", "--log-group-name", cell.LogGroup, "--log-stream-name-prefix", GatewayStreamPrefix,
             "--start-time", since.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), "--output", "json", "--region", cell.Region], ct);

        using JsonDocument answer = JsonDocument.Parse(output);
        List<(long At, JsonElement Line)> lines = [];
        foreach (JsonElement logEvent in answer.RootElement.GetProperty("events").EnumerateArray())
        {
            if (logEvent.GetProperty("logStreamName").GetString()?.StartsWith(GatewayStreamPrefix, StringComparison.Ordinal) != true
                || logEvent.GetProperty("message").GetString() is not { } message
                || !message.TrimStart().StartsWith('{'))
            {
                continue;
            }

            try
            {
                using JsonDocument line = JsonDocument.Parse(message);
                lines.Add((logEvent.GetProperty("timestamp").GetInt64(), line.RootElement.Clone()));
            }
            catch (JsonException)
            {
                // A line that opens with a brace and is not JSON is still not a contract line.
            }
        }

        return [.. lines.OrderBy(entry => entry.At).Select(entry => entry.Line)];
    }

    /// <summary>
    /// Waits for a gateway line from <paramref name="since"/> that matches, and fails naming
    /// <paramref name="what"/> if none comes within <paramref name="timeout"/>. A line reaches CloudWatch
    /// some seconds after the gateway writes it, and the gateway writes a request line after the
    /// response (contracts/telemetry.md), so the browser is always ahead of the log.
    /// </summary>
    public async Task<JsonElement> WaitForGatewayLineAsync(DateTimeOffset since, Func<JsonElement, bool> match, TimeSpan timeout, string what, CancellationToken ct)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (true)
        {
            foreach (JsonElement line in await GatewayLinesAsync(since, ct))
            {
                if (match(line))
                {
                    return line;
                }
            }

            if (waited.Elapsed >= timeout)
            {
                throw new TimeoutException($"no line in {cell.LogGroup} since {since:O} is {what}, after {waited.Elapsed.TotalSeconds:F0}s.");
            }

            await Task.Delay(PollEvery, ct);
        }
    }

    /// <summary>
    /// The registry object's key in the bucket: contracts/platform-config.schema.json -> Registry.Key,
    /// "registry/registry.json in every environment so far".
    /// </summary>
    public const string RegistryKey = "registry/registry.json";

    /// <summary>The release the live registry in the cell's bucket gives the app.</summary>
    public async Task<string> ReleaseOfAsync(string appId, CancellationToken ct)
    {
        // https://docs.aws.amazon.com/cli/latest/reference/s3/cp.html: "-" streams the object to stdout.
        string document = await runner.RunAsync("aws",
            ["s3", "cp", $"s3://{cell.Bucket}/{RegistryKey}", "-", "--region", cell.Region], ct);
        System.Text.Json.Nodes.JsonArray apps = System.Text.Json.Nodes.JsonNode.Parse(document)?["apps"]?.AsArray()
            ?? throw new InvalidOperationException($"s3://{cell.Bucket}/{RegistryKey} has no apps.");
        return apps.SingleOrDefault(entry => entry?["appId"]?.GetValue<string>() == appId)?["release"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"the live registry names no release for {appId}.");
    }

    /// <summary>A twelve-digit run, which is how an account id appears in an AWS error.</summary>
    [GeneratedRegex("[0-9]{12}")]
    internal static partial Regex AccountId();
}

/// <summary>
/// A secret value on its way to a form field. It prints, formats and serializes as nothing, so an
/// assertion message, an exception or a log line that names it shows no part of it (AGENTS.md: never
/// log a credential).
/// </summary>
public sealed class Password
{
    private readonly string value;

    internal Password(string value) => this.value = value;

    /// <summary>The value, for the one call that types it into the provider's form.</summary>
    public string Reveal() => value;

    public override string ToString() => "[password]";
}

/// <summary>Runs a program and returns its standard output; the deployed stack's one way out to a tool.</summary>
public interface ICommandRunner
{
    Task<string> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken ct);
}

/// <summary>A program that exited non-zero, with its error output and every account id in it masked.</summary>
public sealed class CommandFailedException(string program, IReadOnlyList<string> arguments, int exitCode, string error)
    : Exception($"{program} {string.Join(' ', arguments.Take(2))} exited {exitCode}: {CellAws.AccountId().Replace(error.Trim(), "************")}");

/// <summary>
/// The real runner: a child process with the caller's environment, so it signs in as the operator's
/// own session (the <c>spa-platform</c> profile, with <c>tools/aws-project.env</c> sourced), and with
/// the AWS CLI's pager off, as the scripts set it
/// (https://docs.aws.amazon.com/cli/latest/userguide/cli-usage-pagination.html#cli-usage-pagination-clientside).
/// Nothing it reads is written anywhere but the returned string.
/// </summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<string> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ProcessStartInfo start = new(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["AWS_PAGER"] = "";

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0
            ? await output
            : throw new CommandFailedException(program, arguments, process.ExitCode, await error);
    }
}
