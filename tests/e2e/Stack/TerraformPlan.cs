using System.Text.Json;
using System.Text.RegularExpressions;
using Gateway.TestHarness;

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
    ///
    /// <para>A block keyed by fragment (<c>for_each = local.fragments</c>) is the app's too: #961 has the
    /// apps root create each fragment's client, style and secret from the shell's definition, so
    /// onboarding one is one change to that definition (stories 15 and 28), and #999 keys them
    /// <c>"&lt;appId&gt;.&lt;fragmentId&gt;"</c>. Such a block adds one address per id in
    /// <paramref name="fragmentIds"/>, and none for an app with no fragments.</para>
    /// </summary>
    public static string[] PerAppAddresses(string mainTf, string appId, params string[] fragmentIds)
    {
        List<string> addresses = [];
        foreach (Match block in ResourceBlock().Matches(mainTf))
        {
            string address = $"{block.Groups["type"].Value}.{block.Groups["name"].Value}";
            string body = block.Groups["body"].Value;
            if (PerApp().IsMatch(body))
            {
                addresses.Add($"{address}[\"{appId}\"]");
            }
            else if (PerFragment().IsMatch(body))
            {
                addresses.AddRange(fragmentIds.Select(fragmentId => $"{address}[\"{appId}.{fragmentId}\"]"));
            }
            else
            {
                throw new InvalidOperationException($"the apps root's {address} is not keyed by app (for_each = local.apps) or by an app's fragment (for_each = local.fragments), so onboarding an app would plan more than that app's resources.");
            }
        }

        return [.. addresses.Order(StringComparer.Ordinal)];
    }

    [GeneratedRegex(@"^resource\s+""(?<type>[\w-]+)""\s+""(?<name>[\w-]+)""\s*\{(?<body>.*?)^\}", RegexOptions.Multiline | RegexOptions.Singleline, RegexTimeout.Milliseconds)]
    private static partial Regex ResourceBlock();

    [GeneratedRegex(@"^\s*for_each\s*=\s*local\.apps\s*$", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex PerApp();

    [GeneratedRegex(@"^\s*for_each\s*=\s*local\.fragments\s*$", RegexOptions.Multiline, RegexTimeout.Milliseconds)]
    private static partial Regex PerFragment();
}

/// <summary>One change a plan makes: the resource's address and its actions joined by commas (<c>create</c>, <c>delete,create</c>). No value.</summary>
public readonly record struct PlannedChange(string Address, string Actions)
{
    public override string ToString() => $"{Address} {Actions}";
}
