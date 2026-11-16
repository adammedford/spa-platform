using System.Text.RegularExpressions;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// Row 6's driver decisions with no cell (#1203): when it submits the straddling login, when it tries a
/// failed sign-in again, and when it tells the operator to launch <c>deploy.sh</c>, each against a
/// double for ECS and the tunnel (<see cref="ScriptedCell"/>) on a clock the double moves.
/// </summary>
public sealed partial class RollingDeployLoopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(300, 60, 60, 180)]
    [InlineData(600, 60, 120, 420)]
    [InlineData(3600, 300, 60, 3240)]
    public void The_launch_moment_is_the_first_refresh_less_one_sign_in_period(int validity, int before, int every, int launch)
    {
        // Worked by hand: the first session refreshes validity - before after its sign-in, one more
        // every `every` after that, so the last moment one `every`-long interval can start and still
        // reach the first refresh is one period before it.
        Assert.Equal(TimeSpan.FromSeconds(launch),
            ContinuityBounds.Row6Launch(TimeSpan.FromSeconds(validity), TimeSpan.FromSeconds(before), TimeSpan.FromSeconds(every)));
    }

    [Theory]
    [InlineData(300, 60, 60)]
    [InlineData(600, 60, 120)]
    public void An_interval_one_period_long_that_starts_from_the_launch_holds_a_refresh_and_one_started_a_second_sooner_need_not(int validity, int before, int every)
    {
        TimeSpan period = TimeSpan.FromSeconds(every);
        TimeSpan firstRefresh = TimeSpan.FromSeconds(validity - before);
        TimeSpan launch = ContinuityBounds.Row6Launch(TimeSpan.FromSeconds(validity), TimeSpan.FromSeconds(before), period);

        // Refreshes fall at firstRefresh + k * period, one per session, sessions signing in every period.
        bool HoldsRefresh(TimeSpan from) => Enumerable.Range(0, 100)
            .Select(k => firstRefresh + (k * period))
            .Any(refresh => refresh >= from && refresh <= from + period);

        for (int lag = 0; lag <= 3 * every; lag++)
        {
            Assert.True(HoldsRefresh(launch + TimeSpan.FromSeconds(lag)), $"an interval starting {lag} s after the launch holds no refresh");
        }

        Assert.False(HoldsRefresh(launch - TimeSpan.FromSeconds(1)), "the launch moment is not the earliest: an interval starting a second sooner still holds a refresh");
    }

    [Fact]
    public void The_refresh_margin_is_the_one_the_gateway_refreshes_by()
    {
        string source = File.ReadAllText(Path.Combine(FixtureFiles.RepositoryRoot, "src", "Platform.Identity", "AccessTokenSource.cs"));
        Match margin = RefreshBeforeExpiry().Match(source);
        Assert.True(margin.Success, "src/Platform.Identity/AccessTokenSource.cs no longer sets RefreshBeforeExpiry with TimeSpan.From<unit>(<n>).");

        double n = double.Parse(margin.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        TimeSpan gateway = margin.Groups[1].Value switch
        {
            "Seconds" => TimeSpan.FromSeconds(n),
            "Minutes" => TimeSpan.FromMinutes(n),
            "Hours" => TimeSpan.FromHours(n),
            string other => throw new InvalidOperationException($"unit {other}"),
        };
        Assert.Equal(gateway, ContinuityBounds.Row6RefreshBeforeExpiry);
    }

    private static TimeSpan S(int seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public async Task The_straddling_login_waits_for_every_old_task_to_have_a_stoppedAt_not_only_a_stopped_desired_status()
    {
        // ECS asks the old task to stop at 30 s and it stops at 90 s; it answers while it drains (#348, 2026-11-14).
        ScriptedCell cell = new()
        {
            Old = at => at < S(30) ? ScriptedCell.Running("old") : at < S(90) ? ScriptedCell.Draining("old") : ScriptedCell.Stopped("old", S(90)),
            New = at => at < S(20) ? [] : [at < S(40) ? ScriptedCell.Pending("new") : ScriptedCell.Running("new")],
        };

        await new RollingDeployLoop(cell, cell.Clock, TextWriter.Null).RunAsync(S(300), Ct);

        Assert.NotNull(cell.LoginStartedAt);
        TimeSpan submitted = Assert.NotNull(cell.LoginSubmittedAt);
        Assert.True(submitted >= S(90), $"the login was submitted at {submitted}, while the old task's desired status read STOPPED and it had no stoppedAt");
    }

    [Fact]
    public async Task The_straddling_login_waits_for_the_tunnel_to_answer_after_the_old_tasks_stop()
    {
        // The tunnel's old hop goes with the old task at 90 s; the restart answers from 120 s.
        ScriptedCell cell = new()
        {
            Old = at => at < S(30) ? ScriptedCell.Running("old") : at < S(90) ? ScriptedCell.Draining("old") : ScriptedCell.Stopped("old", S(90)),
            New = at => at < S(20) ? [] : [at < S(40) ? ScriptedCell.Pending("new") : ScriptedCell.Running("new")],
            Tunnel = at => at < S(90) || at >= S(120),
        };

        await new RollingDeployLoop(cell, cell.Clock, TextWriter.Null).RunAsync(S(300), Ct);

        TimeSpan submitted = Assert.NotNull(cell.LoginSubmittedAt);
        Assert.True(submitted >= S(120), $"the login was submitted at {submitted}, while the tunnel did not answer");
    }

    [Fact]
    public async Task A_sign_in_that_fails_while_the_tunnel_does_not_answer_is_tried_again_and_the_row_goes_on()
    {
        // The tunnel refuses from 55 s to 80 s, the restart's gap: the sign-in due at 60 s fails each turn until it answers.
        ScriptedCell cell = new() { Tunnel = at => at < S(55) || at >= S(80) };

        await new RollingDeployLoop(cell, cell.Clock, TextWriter.Null).RunAsync(S(150), Ct);

        Assert.Contains(cell.SignInsFailed, at => at >= S(60) && at < S(80));
        Assert.Contains(cell.SignedIn, at => at >= S(80) && at < S(90));
        Assert.Contains(cell.SignedIn, at => at >= S(120));
    }

    [Theory]
    [InlineData(55, 80)]
    [InlineData(65, 80)]
    [InlineData(75, 89)]
    public async Task A_sign_in_that_fails_after_the_tunnel_is_back_is_still_the_gap_when_the_tunnel_did_not_answer_while_it_ran(int down, int back)
    {
        // The sign-in due at 60 s starts inside the tunnel's restart gap (the first case, as #1211's
        // routing names it) or is in flight when the old hop stops (the other two), and its Playwright
        // wait runs out 30 s later, at 90 s, after the tunnel has restarted: the gaps #348 records ran 8
        // to 16 s, and Playwright's default timeout is 30 s
        // (https://playwright.dev/dotnet/docs/api/class-page#page-set-default-timeout). A probe taken at
        // the failure reads the tunnel up in every case, the last's back only a second before it.
        ScriptedCell cell = new()
        {
            Tunnel = at => at < S(down) || at >= S(back),
            SignInTakes = at => at == S(60) ? S(30) : TimeSpan.Zero,
        };

        await new RollingDeployLoop(cell, cell.Clock, TextWriter.Null).RunAsync(S(200), Ct);

        Assert.Equal([S(60)], cell.SignInsFailed);
        Assert.Contains(cell.SignedIn, at => at >= S(back));
    }

    [Fact]
    public async Task A_sign_in_that_fails_while_the_tunnel_answers_ends_the_row()
    {
        ScriptedCell cell = new() { SignInFails = at => at >= S(60) && at < S(80) };

        await Assert.ThrowsAsync<TimeoutException>(() => new RollingDeployLoop(cell, cell.Clock, TextWriter.Null).RunAsync(S(150), Ct));
        Assert.Equal([S(60)], cell.SignInsFailed);
    }

    [Fact]
    public async Task The_driver_tells_the_operator_once_to_launch_the_deployment_at_the_launch_moment_after_the_first_sign_in()
    {
        // The first sign-in takes 17 s, as on 2026-11-14, and the later ones 2 s: the moment counts from
        // when the first completed, not from the row's start.
        ScriptedCell cell = new() { SignInTakes = at => at == TimeSpan.Zero ? S(17) : S(2) };
        StringWriter output = new();
        List<TimeSpan> written = [];
        cell.Turned = at => written.AddRange(Enumerable.Repeat(at, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - written.Count));

        await new RollingDeployLoop(cell, cell.Clock, output).RunAsync(S(400), Ct);

        string line = Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith(RollingDeployLoop.LaunchLine, line, StringComparison.Ordinal);
        TimeSpan at = Assert.Single(written);
        TimeSpan due = S(17) + ContinuityBounds.Row6LaunchAfterFirstSignIn;
        Assert.InRange(at, due, due + RollingDeployLoop.Turn + S(2));
    }

    [GeneratedRegex(@"\bRefreshBeforeExpiry\s*=\s*TimeSpan\.From(\w+)\((\d+)\)", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex RefreshBeforeExpiry();
}
