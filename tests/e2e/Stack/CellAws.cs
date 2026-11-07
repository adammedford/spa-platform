using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// What the deployed stack reads from AWS while it runs (#209): a test user's password from Parameter
/// Store, the gateway's log lines from CloudWatch Logs, the live registry from the bucket, and the
/// gateway service's running tasks and rollout from ECS (F-07, #212, #873). Each goes through the AWS CLI v2, the tool
/// <c>tools/scripts/</c> already reach the cell with (docs/dependencies.md), because the project's
/// profile signs in with <c>login_session</c>, which the CLI resolves on its own and the .NET SDK
/// resolves only through the AWSSDK.Signin assembly (#206), and the CLI needs no package here. Every
/// call names the cell's Region from the outputs document.
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
    public async Task<string> ReleaseOfAsync(string appId, CancellationToken ct) =>
        (await RegistryDocumentAsync(ct))["apps"]!.AsArray().SingleOrDefault(entry => entry?["appId"]?.GetValue<string>() == appId)?["release"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"the live registry names no release for {appId}.");

    /// <summary>The live registry's revision and the apps in it, in its order.</summary>
    public async Task<LiveRegistry> LiveRegistryAsync(CancellationToken ct)
    {
        JsonNode document = await RegistryDocumentAsync(ct);
        long revision = document["revision"]?.GetValue<long>() ?? throw new InvalidOperationException($"s3://{cell.Bucket}/{RegistryKey} has no revision.");
        return new LiveRegistry(revision, [.. document["apps"]!.AsArray().Select(entry => entry?["appId"]?.GetValue<string>() ?? "")]);
    }

    private async Task<JsonNode> RegistryDocumentAsync(CancellationToken ct)
    {
        // https://docs.aws.amazon.com/cli/latest/reference/s3/cp.html: "-" streams the object to stdout.
        string text = await runner.RunAsync("aws",
            ["s3", "cp", $"s3://{cell.Bucket}/{RegistryKey}", "-", "--region", cell.Region], ct);
        JsonNode document = JsonNode.Parse(text) ?? throw new InvalidOperationException($"s3://{cell.Bucket}/{RegistryKey} is empty.");
        return document["apps"] is JsonArray ? document : throw new InvalidOperationException($"s3://{cell.Bucket}/{RegistryKey} has no apps.");
    }

    /// <summary>
    /// The gateway service's running tasks, as a set (F-07: "the task id, start time and task definition
    /// are unchanged", read per running task, #873). A task the service is draining has desired status
    /// STOPPED and is not listed (https://docs.aws.amazon.com/cli/latest/reference/ecs/list-tasks.html),
    /// and one still starting is listed and is not running yet, so it is left out. How many there should
    /// be is <see cref="ServiceRolloutAsync"/>'s to say: two are a two-task cell's steady state
    /// (infra/modules/cell/ecs.tf, <c>desired_count</c>), and this read refuses no count. A listed task
    /// ECS gives no state for is refused by <see cref="ContinuityEcs.DescribeAsync"/> (#1142), since it
    /// may be running and a set without it would be waited on as the whole service.
    /// </summary>
    public async Task<IReadOnlySet<RunningTask>> RunningTasksAsync(CancellationToken ct) =>
        (await new ContinuityEcs(cell, runner).ListedAsync(ct))
            .Where(task => task.LastStatus == "RUNNING")
            .Select(task => new RunningTask(
                task.Id,
                task.Revision ?? throw new InvalidOperationException($"ECS gave running task {task.Id} no taskDefinitionArn, which F-07 compares."),
                task.StartedAt ?? throw new InvalidOperationException($"ECS gave running task {task.Id} no startedAt, which F-07 compares.")))
            .ToHashSet();

    /// <summary>
    /// The gateway service's deployments as <c>describe-services</c> gives them, the reads
    /// tools/scripts/cell-common.sh makes. The waiter <c>services-stable</c> does not read
    /// <c>rolloutState</c> and returned while it was still IN_PROGRESS (#467), so this reads the field
    /// itself (https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_Deployment.html). An answer
    /// with no service in it, only <c>failures</c>
    /// (https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_DescribeServices.html), reads as no deployments.
    /// </summary>
    public async Task<ServiceRollout> ServiceRolloutAsync(CancellationToken ct)
    {
        string described = await runner.RunAsync("aws",
            ["ecs", "describe-services", "--cluster", cell.Cluster, "--services", cell.Service, "--output", "json", "--region", cell.Region], ct);
        using JsonDocument answer = JsonDocument.Parse(described);
        JsonElement[] deployments = [.. answer.RootElement.GetProperty("services").EnumerateArray().SelectMany(service => service.GetProperty("deployments").EnumerateArray())];
        JsonElement[] primary = [.. deployments.Where(deployment => deployment.GetProperty("status").GetString() == "PRIMARY")];
        return primary is [JsonElement found]
            ? new ServiceRollout(
                deployments.Length,
                found.TryGetProperty("rolloutState", out JsonElement rollout) ? rollout.GetString() : null,
                found.GetProperty("runningCount").GetInt32(),
                found.GetProperty("desiredCount").GetInt32())
            : new ServiceRollout(deployments.Length, null, 0, 0);
    }

    /// <summary>A twelve-digit run, which is how an account id appears in an AWS error.</summary>
    [GeneratedRegex("[0-9]{12}", RegexOptions.None, RegexTimeout.Milliseconds)]
    internal static partial Regex AccountId();
}

/// <summary>A gateway task as F-07 compares it, per running task, before and after an onboarding: its id, its task definition as family:revision, and when it started. No account id.</summary>
public sealed record RunningTask(string TaskId, string Revision, DateTimeOffset StartedAt);

/// <summary>The gateway service's deployment count and its PRIMARY deployment's rollout state, running count and desired count; a null state and zero counts when it has no PRIMARY.</summary>
public sealed record ServiceRollout(int Deployments, string? PrimaryRollout, int PrimaryRunning, int PrimaryDesired)
{
    /// <summary>
    /// Why a service with this rollout and this many running tasks listed is not steady, or null when
    /// it is. "Exactly one task" used to double as this check and on a two-task service no longer can
    /// (#873). Each clause has a defect only it sees: one deployment with the wrong state, a deployment
    /// count, the deployment's own counts, the list against them, and a service steady at nothing.
    /// </summary>
    public string? Unsteady(int listedRunning)
    {
        if (Deployments != 1)
        {
            return $"it has {Deployments} deployments, not one";
        }

        if (PrimaryRollout != "COMPLETED")
        {
            return $"its primary deployment's rollout is {PrimaryRollout ?? "absent"}, not COMPLETED";
        }

        if (PrimaryRunning != PrimaryDesired)
        {
            return $"its primary deployment runs {PrimaryRunning} of {PrimaryDesired} desired tasks";
        }

        if (listedRunning != PrimaryDesired)
        {
            return $"{listedRunning} RUNNING tasks are listed and its primary deployment desires {PrimaryDesired}";
        }

        return listedRunning == 0 ? "it has no running task" : null;
    }
}

/// <summary>The live registry's revision and the ids of the apps in it.</summary>
public sealed record LiveRegistry(long Revision, IReadOnlyList<string> AppIds);

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

/// <summary>Runs a program and returns its exit and standard output; the deployed stack's one way out to a tool.</summary>
public interface ICommandRunner
{
    /// <summary>Runs the command, and throws <see cref="CommandFailedException"/> for an exit it does not accept.</summary>
    Task<CommandResult> RunAsync(Command command, CancellationToken ct);
}

/// <summary>The common call: exit 0 only, the caller's environment, and the standard output.</summary>
public static class CommandRunning
{
    public static async Task<string> RunAsync(this ICommandRunner runner, string program, IReadOnlyList<string> arguments, CancellationToken ct) =>
        (await runner.RunAsync(new Command(program, arguments), ct)).Output;
}

/// <summary>
/// A program, its arguments, what it adds to the caller's environment, and the exits that are not a
/// failure: 0 alone unless a tool gives another a meaning, as <c>terraform plan -detailed-exitcode</c>
/// gives 2 "changes present" (https://developer.hashicorp.com/terraform/cli/commands/plan#detailed-exitcode).
/// </summary>
public sealed record Command(string Program, IReadOnlyList<string> Arguments)
{
    /// <summary>Variables set over the caller's own, such as the project's profile (tools/aws-project.env).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<int> AcceptedExits { get; init; } = [0];
}

/// <summary>An accepted exit and what the program printed on standard output.</summary>
public sealed record CommandResult(int ExitCode, string Output);

/// <summary>A program that exited with an exit its command does not accept, with its error output and every account id in it masked.</summary>
public sealed class CommandFailedException(string program, IReadOnlyList<string> arguments, int exitCode, string error)
    : Exception($"{program} {string.Join(' ', arguments.Take(2))} exited {exitCode}: {CellAws.AccountId().Replace(error.Trim(), "************")}");

/// <summary>
/// The real runner: a child process with the caller's environment, so it signs in as the operator's
/// own session (the <c>spa-platform</c> profile, with <c>tools/aws-project.env</c> sourced), the
/// command's own variables set over it, and with
/// the AWS CLI's pager off, as the scripts set it
/// (https://docs.aws.amazon.com/cli/latest/userguide/cli-usage-pagination.html#cli-usage-pagination-clientside).
/// Nothing it reads is written anywhere but the returned string. A cancelled run gives its program
/// <see cref="StopGrace"/> to stop by itself, then kills it and everything it started, before the
/// cancellation reaches the caller.
/// </summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    /// <summary>
    /// How long a cancelled run's program has to stop by itself before it is killed. Ctrl-C in a
    /// terminal interrupts the whole foreground process group, the program included
    /// (https://pubs.opengroup.org/onlinepubs/9699919799/basedefs/V1_chap11.html#tag_11_01_09), and an
    /// interrupted Terraform stops at its own pace and gives its state lock back, where a killed one
    /// leaves the lock behind for <c>force-unlock</c> (infra/bootstrap/README.md). A run cancelled any
    /// other way sends the program nothing, so it is killed once this has passed rather than left
    /// running with the lock after its test has gone.
    /// </summary>
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<CommandResult> RunAsync(Command command, CancellationToken ct)
    {
        (string program, IReadOnlyList<string> arguments) = (command.Program, command.Arguments);
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
        foreach ((string name, string value) in command.Environment)
        {
            start.Environment[name] = value;
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> error = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await StopAsync(process);
            throw;
        }

        return command.AcceptedExits.Contains(process.ExitCode)
            ? new CommandResult(process.ExitCode, await output)
            : throw new CommandFailedException(program, arguments, process.ExitCode, await error);
    }

    /// <summary>
    /// Waits <see cref="StopGrace"/> for the program to exit, then kills it with everything it started:
    /// disposing the Process alone leaves it running
    /// (https://learn.microsoft.com/dotnet/api/system.diagnostics.process.kill#system-diagnostics-process-kill(system-boolean)).
    /// </summary>
    private async Task StopAsync(Process process)
    {
        using (CancellationTokenSource grace = new(StopGrace))
        {
            try
            {
                await process.WaitForExitAsync(grace.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                // Still running when the grace ran out.
            }
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It exited between the grace and the kill.
        }

        await process.WaitForExitAsync(CancellationToken.None);
    }
}
