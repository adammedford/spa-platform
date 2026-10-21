using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// What F-07 reads of the apps root's plan before applying it (#212; docs/plan/04-tests.md: "the
/// onboarding plan shows one client, one branding style and one parameter").
///
/// <para>A saved plan of an applied apps root holds every client secret in cleartext, in its prior
/// state and in the values of its changes (infra/envs/poc/apps/README.md; the plan representation,
/// https://developer.hashicorp.com/terraform/internals/json-format#plan-representation). So nothing
/// here keeps or prints more than a change's address and actions, and a document that does not parse
/// is refused without a word of it.</para>
/// </summary>
public static partial class TerraformPlan
{
    /// <summary>
    /// The changes a <c>terraform show -json</c> document of a saved plan makes: every entry of
    /// <c>resource_changes</c> whose actions are neither <c>no-op</c> nor a data source's <c>read</c>,
    /// in the plan's order.
    /// </summary>
    public static IReadOnlyList<PlannedChange> Changes(string showJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(showJson);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("terraform show -json did not give a JSON document.");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("resource_changes", out JsonElement changes))
            {
                return [];
            }

            List<PlannedChange> made = [];
            foreach (JsonElement change in changes.EnumerateArray())
            {
                string[] actions = [.. change.GetProperty("change").GetProperty("actions").EnumerateArray().Select(action => action.GetString()!)];
                if (actions is ["no-op"] or ["read"])
                {
                    continue;
                }

                made.Add(new PlannedChange(change.GetProperty("address").GetString()!, string.Join(',', actions)));
            }

            return made;
        }
    }

    /// <summary>
    /// The address of each resource the apps root makes for one app: every <c>resource</c> block of
    /// <c>main.tf</c>, each of which must be keyed by app (<c>for_each = local.apps</c>), with the app's
    /// id as its key, in ordinal order. A block keyed any other way is refused, because onboarding
    /// would then plan something that is not the app's (P-02's rule, infra/envs/poc/apps/README.md -> Tests).
    /// </summary>
    public static string[] PerAppAddresses(string mainTf, string appId)
    {
        List<string> addresses = [];
        foreach (Match block in ResourceBlock().Matches(mainTf))
        {
            string address = $"{block.Groups["type"].Value}.{block.Groups["name"].Value}";
            if (!PerApp().IsMatch(block.Groups["body"].Value))
            {
                throw new InvalidOperationException($"the apps root's {address} is not keyed by app (for_each = local.apps), so onboarding an app would plan more than that app's resources.");
            }

            addresses.Add($"{address}[\"{appId}\"]");
        }

        return [.. addresses.Order(StringComparer.Ordinal)];
    }

    [GeneratedRegex(@"^resource\s+""(?<type>[\w-]+)""\s+""(?<name>[\w-]+)""\s*\{(?<body>.*?)^\}", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex ResourceBlock();

    [GeneratedRegex(@"^\s*for_each\s*=\s*local\.apps\s*$", RegexOptions.Multiline)]
    private static partial Regex PerApp();
}

/// <summary>One change a plan makes: the resource's address and its actions joined by commas (<c>create</c>, <c>delete,create</c>). No value.</summary>
public readonly record struct PlannedChange(string Address, string Actions)
{
    public override string ToString() => $"{Address} {Actions}";
}
