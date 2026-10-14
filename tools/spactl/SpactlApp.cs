using Amazon.Runtime;

namespace Spactl;

/// <summary>
/// spactl's commands (plan row 2.6; docs/plan/05-infrastructure-and-cost.md, "Deployment and rollback"):
/// <c>registry validate</c>, <c>registry apply</c>, <c>registry rollback</c>, <c>release publish</c>,
/// <c>release activate</c>, <c>app add</c>, <c>app remove</c> and <c>wait</c>.
///
/// Exit codes: 0 done, 1 refused (the reason is on standard error, and nothing was written that the
/// reason names), 2 a command line spactl cannot run.
/// </summary>
public static class SpactlApp
{
    public const string Usage = """
        Usage:
          spactl registry validate <registry.json> --config <platform-config.json> [--offline]
          spactl registry apply    <registry.json> --config <platform-config.json>
          spactl registry rollback [--to-revision <N>] --config <platform-config.json>
          spactl release publish   <appId> <directory> <release>
          spactl release activate  <appId> <release> --config <platform-config.json>
          spactl app add           <definition.json> [--release <release>] --config <platform-config.json>
          spactl app remove        <appId> --config <platform-config.json>
          spactl wait              --url <gateway origin> --revision <N> [--timeout <seconds>]

        The bucket and region come from --outputs (default infra/envs/poc/outputs.json), or --bucket
        [--region] names them. --config is a platform configuration file in the shape of
        contracts/platform-config.schema.json; its Policy and Issuers sections bound the registry.
        """;

    /// <summary>The bucket options every command that reaches the store accepts.</summary>
    private static readonly string[] Bucket = ["--outputs", "--bucket", "--region"];

    /// <summary>The options each command reads, as <see cref="Usage"/> lists them; any other is refused.</summary>
    private static readonly Dictionary<string, HashSet<string>> Options = new(StringComparer.Ordinal)
    {
        ["registry validate"] = [.. Bucket, "--config", "--offline"],
        ["registry apply"] = [.. Bucket, "--config"],
        ["registry rollback"] = [.. Bucket, "--config", "--to-revision"],
        ["release publish"] = [.. Bucket],
        ["release activate"] = [.. Bucket, "--config"],
        ["app add"] = [.. Bucket, "--config", "--release"],
        ["app remove"] = [.. Bucket, "--config"],
        ["wait"] = ["--url", "--revision", "--timeout"],
    };

    public static async Task<int> RunAsync(IReadOnlyList<string> args, SpactlEnvironment environment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);

        try
        {
            Arguments arguments = Arguments.Parse(args);
            Commands commands = new(arguments, environment);
            string command = string.Join(' ', arguments.Positional.Take(arguments.Positional.FirstOrDefault() == "wait" ? 1 : 2));
            arguments.AcceptOnly(Options.TryGetValue(command, out HashSet<string>? accepted) ? accepted : throw new UsageException(Usage));
            return command switch
            {
                "registry validate" => await commands.RegistryValidateAsync(ct),
                "registry apply" => await commands.RegistryApplyAsync(ct),
                "registry rollback" => await commands.RegistryRollbackAsync(ct),
                "release publish" => await commands.ReleasePublishAsync(ct),
                "release activate" => await commands.ReleaseActivateAsync(ct),
                "app add" => await commands.AppAddAsync(ct),
                "app remove" => await commands.AppRemoveAsync(ct),
                "wait" => await commands.WaitAsync(ct),
                _ => throw new UsageException(Usage),
            };
        }
        catch (UsageException usage)
        {
            await environment.Error.WriteLineAsync(usage.Message);
            return 2;
        }
        catch (RefusedException refused)
        {
            await environment.Error.WriteLineAsync(refused.Message);
            return 1;
        }
        catch (AmazonServiceException aws)
        {
            // S3's own refusal (access denied, no such bucket). The SDK's message names the error, never
            // a credential.
            await environment.Error.WriteLineAsync($"S3 refused the request: {aws.ErrorCode} {aws.Message}");
            return 1;
        }
        catch (AmazonClientException aws)
        {
            // Nothing was sent: no credentials or no region to send them to.
            await environment.Error.WriteLineAsync($"The AWS request could not be made: {aws.Message}");
            return 1;
        }
    }
}
