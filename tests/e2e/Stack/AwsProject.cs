namespace Gateway.E2ETests.Stack;

/// <summary>
/// The project's AWS session as <c>tools/aws-project.env</c> names it: <c>AWS_PROFILE</c> and
/// <c>AWS_REGION</c>, the names the AWS CLI, Terraform and the SDK's default chain already read. The
/// file is the integrator's and is sourced by hand before an apply; F-07's onboarding sets the two over
/// whatever the shell exported, so it writes to the project the file names (#212, #235). The file says
/// why the profile is pinned: on 2026-10-15 a sitting applied into the wrong project.
///
/// <para>The profile is what pins the project. The SDKs and the AWS CLI use the profile
/// <c>AWS_PROFILE</c> names (https://docs.aws.amazon.com/sdkref/latest/guide/file-format.html#set-named-profile),
/// and so does the Terraform AWS provider when its block sets no <c>profile</c>. The Region is only a
/// fallback for Terraform: the provider applies its own arguments before environment variables
/// (https://registry.terraform.io/providers/hashicorp/aws/latest/docs#configuration-precedence), and
/// the apps root sets <c>region = var.region</c>, so its <c>terraform.tfvars</c> decides the Region there.</para>
/// </summary>
public static class AwsProject
{
    private static readonly string[] Keys = ["AWS_PROFILE", "AWS_REGION"];

    /// <summary>The repository's own file.</summary>
    public static string PathIn(string repositoryRoot) => Path.Combine(repositoryRoot, "tools", "aws-project.env");

    /// <summary>The two variables, read from KEY=VALUE lines; a file without both, or with an empty one, is refused.</summary>
    public static IReadOnlyDictionary<string, string> Read(string path)
    {
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(path))
        {
            string text = line.Trim();
            int equals = text.IndexOf('=', StringComparison.Ordinal);
            if (text.StartsWith('#') || equals <= 0)
            {
                continue;
            }

            string key = text[..equals];
            if (Keys.Contains(key))
            {
                found[key] = text[(equals + 1)..];
            }
        }

        string[] missing = [.. Keys.Where(key => !found.TryGetValue(key, out string? value) || value.Length == 0)];
        return missing.Length == 0
            ? found
            : throw new InvalidOperationException($"{path} names no {string.Join(" or ", missing)}, so the onboarding cannot run as the project's profile.");
    }
}
