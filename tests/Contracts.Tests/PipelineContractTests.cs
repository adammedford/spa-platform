namespace Contracts.Tests;

/// <summary>
/// The pipeline contract's start-up and shut-down section (#541): the three numbers a stopping task
/// is bounded by, written once where workstream 1, which sets two of them, and workstream 2, which
/// sets the third, both read. The contract names the numbers and their order; each owner pins its
/// own value in its own tests (the cell module's <c>terraform test</c>, and
/// <c>tests/Platform.Hosting.Tests</c>), so this project reads only the contract.
/// </summary>
public sealed class PipelineContractTests
{
    private const string HealthHeading = "## Health endpoints";
    private const string ShutdownHeading = "## Start-up and shut-down";

    [Fact]
    public void The_start_up_and_shut_down_section_follows_the_health_endpoints()
    {
        string text = ContractFiles.ReadText("pipeline.md");

        int health = text.IndexOf(HealthHeading + "\n", StringComparison.Ordinal);
        int shutdown = text.IndexOf(ShutdownHeading + "\n", StringComparison.Ordinal);

        Assert.True(health >= 0, $"pipeline.md has no '{HealthHeading}' heading.");
        Assert.True(shutdown >= 0, $"pipeline.md has no '{ShutdownHeading}' heading.");
        Assert.True(shutdown > health, $"'{ShutdownHeading}' comes before '{HealthHeading}'.");
        Assert.Equal(-1, text.IndexOf("\n## ", health + 1, shutdown - health - 1, StringComparison.Ordinal));
    }

    [Fact]
    public void The_three_numbers_a_stop_is_bounded_by_are_stated_in_the_order_they_act()
    {
        // Drain, then the process's own timeout, then the kill (#332 F1, as #445 settled it).
        string section = Section(ContractFiles.ReadText("pipeline.md"), ShutdownHeading);

        int drain = section.IndexOf("a deregistration delay of 30 seconds", StringComparison.Ordinal);
        int shutdown = section.IndexOf("a shutdown timeout of 25 seconds", StringComparison.Ordinal);
        int stop = section.IndexOf("a stop timeout of 30 seconds", StringComparison.Ordinal);

        Assert.True(drain >= 0, "The section does not state the deregistration delay of 30 seconds.");
        Assert.True(shutdown > drain, "The shutdown timeout of 25 seconds is missing or stated before the drain.");
        Assert.True(stop > shutdown, "The stop timeout of 30 seconds is missing or stated before the shutdown timeout.");
    }

    [Fact]
    public void The_shutdown_timeout_is_stated_against_the_refresh_holders_write_deadline_not_the_lease()
    {
        // #445's correction of the request's draft: the 21-second lease bounds the lease, not a
        // request, and what the 25 seconds promise is the holder's durable write, at most 20 seconds
        // from its acquire's send (change request P2-3, second amendment, decision 2).
        string section = Section(ContractFiles.ReadText("pipeline.md"), ShutdownHeading);

        Assert.Contains("20-second write deadline", section);
        Assert.Contains("durable write", section);
        Assert.DoesNotContain("bounded by the 21-second lease", section);
    }

    private static string Section(string text, string heading)
    {
        int start = text.IndexOf(heading + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"pipeline.md has no '{heading}' heading.");
        int next = text.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        string section = next < 0 ? text[start..] : text[start..next];

        // Prose is wrapped at about 100 columns, so a phrase may break across lines.
        return string.Join(' ', section.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
