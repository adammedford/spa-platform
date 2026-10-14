using System.Text;

namespace Spike;

/// <summary>
/// The "Gate G0, as run" table (Q7): one row per G0 item and one per item 2 question, rendered from the run's results.
/// The Cognito run writes it into spikes/g0/README.md between two markers; the dry run writes it beside its output only.
/// </summary>
public static class Report
{
    public const string Begin = "<!-- g0-as-run:begin (written by spikes/g0/cognito --cognito) -->";
    public const string End = "<!-- g0-as-run:end -->";

    /// <summary>
    /// Q21: item 2 fails only if login, callback, refresh or logout does not complete for either OAuth client. C-2.15 is
    /// app2's refresh after app1's logout, the expectation Q19 adds. Discovery (C-2.1) and the item 2 questions are not here.
    /// </summary>
    public static readonly string[] ItemTwoGate = ["C-2.4", "C-2.5", "C-2.8", "C-2.10", "C-2.11", "C-2.12", "C-2.15", "C-2.16"];
    public static readonly string[] ItemThreeGate = ["C-3.1", "C-3.2", "C-3.3", "C-3.4", "C-3.5"];
    private static readonly string[] PortChecks = ["C-3.1", "C-3.2", "C-3.3"];
    private static readonly string[] BrandingChecks = ["C-3.4", "C-3.5"];

    public sealed record Verdict(string Item2, string Item3, bool Item2Passed, bool Item3Passed);

    private static bool Held(IReadOnlyList<Result> r, string id) => r.Any(x => x.Id == id && x.Kind == "OK");
    private static bool Skipped(IReadOnlyList<Result> r, string id) => r.Any(x => x.Id == id && x.Kind == "skip");
    private static bool Reached(IReadOnlyList<Result> r, string id) => r.Any(x => x.Id == id);
    private static bool Stopped(IReadOnlyList<Result> r) => r.Any(x => x.Id == "C-!");

    public static Verdict Verdicts(IReadOnlyList<Result> r)
    {
        // A section that stopped on an exception fails item 2 only through the gate checks it kept from running (Q21).
        var failedTwo = ItemTwoGate.Where(id => !Held(r, id) && !Skipped(r, id)).ToList();
        var two = failedTwo.Count == 0 && !Skipped(r, "C-2.11");
        var stopped = Stopped(r) ? ". A section stopped on an exception (C-!), so not every item 2 question or item 3 check ran: see the output" : "";
        var item2 = failedTwo.Count > 0
            ? "FAILED: " + string.Join(", ", failedTwo) + " did not hold. The way out: identity work stops, and the integrator compares Finbuckle's per-tenant options, Duende BFF v4 and a dedicated tenant-aware OIDC client stack (docs/plan/01-plan.md)"
            : !two ? "not decided: C-2.11, the natural expiry Q20 asks for, was skipped (--skip-natural-expiry). Run again without it" + stopped
            : "passed: both OAuth clients completed login, callback, refresh and logout" + stopped;

        var skipped = ItemThreeGate.Where(id => Skipped(r, id)).ToList();
        var unreached = ItemThreeGate.Where(id => !Reached(r, id)).ToList();
        var three = ItemThreeGate.All(id => Held(r, id));
        string item3;
        if (three) item3 = "passed: the provider accepted the callback and sign-out URLs with the port, and managed login worked for both clients Terraform made";
        else if (skipped.Count + unreached.Count > 0 && ItemThreeGate.Except(skipped).Except(unreached).All(id => Held(r, id)))
            item3 = "not decided: " + string.Join("; ", new[]
            {
                skipped.Count > 0 ? $"{string.Join(", ", skipped)} skipped in this mode" : null,
                unreached.Count > 0 ? $"{string.Join(", ", unreached)} did not run" : null,
            }.Where(s => s is not null));
        else
        {
            var failed = ItemThreeGate.Where(id => !Held(r, id) && !skipped.Contains(id) && !unreached.Contains(id)).ToList();
            var ways = new List<string>();
            var owners = new List<string>();
            if (failed.Intersect(PortChecks).Any())
            {
                ways.Add("the port: the proof of concept binds local port 443 instead");
                owners.Add("a ticket for workstream 1 (the callback URLs in 1.8 and tunnel.sh's local port), a ticket for workstream 5 (the hosts in fixtures/apps), and a change request for the host examples in contracts/");
            }
            if (failed.Intersect(BrandingChecks).Any())
            {
                ways.Add("managed login: the branding style is created through the AWS CLI");
                owners.Add("a ticket for workstream 1 (the branding-style script)");
            }
            item3 = $"FAILED: {string.Join(", ", failed)} did not hold{(unreached.Count > 0 ? $", and {string.Join(", ", unreached)} did not run" : "")}. The way out the plan gives, for {string.Join("; and for ", ways)}. Task 3.0b does not take it (Q22): it records the failure and opens {string.Join("; and ", owners)}";
        }
        return new Verdict(item2, item3, two, three);
    }

    public static string Render(bool dryRun, IReadOnlyList<Result> r, Verdict v, string outputFile, string driver)
    {
        // In a dry run every verdict and answer cell says so itself, so no row copied out of the table reads as evidence.
        var dry = dryRun ? "DRY RUN, not evidence: " : "";
        string Cell(string? s) => (s ?? "").Replace("|", "\\|");
        Result? Get(string id) => r.FirstOrDefault(x => x.Id == id);
        string Answer(string id) => Get(id) is { } x ? $"{dry}{x.Kind}: {Cell(x.Detail)}" : "not run";
        string Route(string id) => Get(id) is { } x ? Cell(x.Route ?? "") : "";

        var sb = new StringBuilder();
        if (dryRun)
        {
            sb.AppendLine("> **DRY RUN.** Rendered from a run against `fixtures/TestIdp`, not Cognito. It proves the harness and the report writing.");
            sb.AppendLine("> It is not G0 evidence, and nothing here may be cited as an answer to item 2 or item 3.");
            sb.AppendLine();
        }
        sb.AppendLine(dryRun
            ? $"Run: `spikes/g0/cognito/{outputFile}`, driver `{driver}`. The check ids `C-<item>.<n>` are listed in that file; in this run they name no evidence."
            : $"Run: `spikes/g0/cognito/{outputFile}`, driver `{driver}`. Evidence ids `C-<item>.<n>` are listed in that file.");
        sb.AppendLine();
        sb.AppendLine("| G0 item | Verdict | Where |");
        sb.AppendLine("| --- | --- | --- |");
        sb.AppendLine("| 1. The shipped handler, with the overrides | Passed on 11 October 2026 (cited, not run again: Q8) | `real-handler/RealOidcNuGet/output-nuget.txt` |");
        sb.AppendLine($"| 2. Two OAuth clients complete login, callback, refresh and logout | {dry}{Cell(v.Item2)} | `cognito/{outputFile}`: {string.Join(", ", ItemTwoGate)} |");
        sb.AppendLine($"| 3. A callback URL with a port; managed login for a client Terraform made | {dry}{Cell(v.Item3)} | `cognito/{outputFile}`: {string.Join(", ", ItemThreeGate)} |");
        sb.AppendLine("| 4. Duende.AccessTokenManagement under derived schemes | Passed on 11 October 2026 (cited, not run again: Q8)."
            + (dryRun ? " This dry run used its configuration against the test identity provider only" : " The run above uses its configuration against a real provider")
            + " | `token-and-proxy/output-token-and-proxy.txt` |");
        sb.AppendLine("| 5. YARP 2.3.0 on .NET 10 | Passed on 11 October 2026 (cited, not run again: Q8) | `token-and-proxy/output-token-and-proxy.txt` |");
        sb.AppendLine();
        sb.AppendLine("| Item 2 question | Evidence | Answer | Route |");
        sb.AppendLine("| --- | --- | --- | --- |");
        sb.AppendLine($"| What does a rotated-out refresh token get inside the 10-second grace window? | C-2.17 | {Answer("C-2.17")} | {Route("C-2.17")} |");
        sb.AppendLine($"| Does a replay after the window harm the current token? | C-2.18 | {Answer("C-2.18")} | {Route("C-2.18")} |");
        sb.AppendLine($"| Does revoking a refresh token revoke its successor? | C-2.19 | {Answer("C-2.19")} | {Route("C-2.19")} |");
        sb.AppendLine($"| How long are refresh tokens, at login and after a refresh? | C-2.20 | {Answer("C-2.20")} | {Route("C-2.20")} |");
        sb.AppendLine($"| Does `aud` survive a refresh that sends no `resource`? | C-2.9 | {Answer("C-2.9")} | {Route("C-2.9")} |");
        sb.AppendLine($"| Does discovery list a revocation endpoint? | C-2.3 | {Answer("C-2.3")} | {Route("C-2.3")} |");
        sb.AppendLine();
        sb.AppendLine("| Also recorded | Evidence | Answer | Route |");
        sb.AppendLine("| --- | --- | --- | --- |");
        sb.AppendLine($"| What discovery advertises for logout | C-2.2 | {Answer("C-2.2")} | {Route("C-2.2")} |");
        sb.AppendLine($"| Resource binding sets `aud` (Q15) | C-2.7 | {Answer("C-2.7")} | {Route("C-2.7")} |");
        sb.AppendLine($"| Client credential style (Q17) | C-2.21 | {Answer("C-2.21")} | {Route("C-2.21")} |");
        sb.AppendLine($"| Logout is per app against a global provider logout (Q19) | C-2.6, C-2.14 | {Answer("C-2.6")}; {Answer("C-2.14")} | {Route("C-2.14")} |");
        sb.AppendLine($"| Logout's revocation took effect | C-2.13 | {Answer("C-2.13")} | {Route("C-2.13")} |");
        return sb.ToString();
    }

    /// <summary>Replaces the text between the markers in the README. False when the markers are missing.</summary>
    public static bool WriteIntoReadme(string readme, string table)
    {
        var text = File.ReadAllText(readme);
        int b = text.IndexOf(Begin, StringComparison.Ordinal), e = text.IndexOf(End, StringComparison.Ordinal);
        if (b < 0 || e < b) return false;
        File.WriteAllText(readme, text[..(b + Begin.Length)] + "\n" + table + text[e..]);
        return true;
    }
}
