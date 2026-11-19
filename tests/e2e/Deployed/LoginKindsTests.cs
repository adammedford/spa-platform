using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The two kinds of login the continuity rows use, each alone on app1 of the deployed cell with no row
/// set up (#1305; #1199 Q1, Q9): a sign-in (<see cref="Profile.SignInAsync"/>), as rows 3, 8 and 9 start,
/// and a login held at the callback (the static <see cref="ContinuityDriver.LoginAsync(Profile, HarnessApp, Func{Task{Uri}})"/>),
/// as rows 5 and 7 drive it. Sitting D's D0 runs the class before D2's apply, so a login that does not
/// work on the cell is seen before a row spends its attempts on it (#1199):
/// <c>dotnet test --project tests/e2e -- --filter-class Gateway.E2ETests.Deployed.LoginKindsTests</c>,
/// with <c>SPA_E2E_STACK=deployed</c>. It needs no evidence directory and no AWS runner.
///
/// <para>With <c>SPA_E2E_STACK</c> unset or <c>local</c> each fact reports skipped, naming the switch,
/// before the stack starts (#1199 Q2). A failing fact names the step it stopped at and prints the URL
/// the page reached only as <see cref="Redacted.Url(string)"/> prints it, with no state, code or cookie
/// (#1199 Q10, N-16): its reason goes on #348. What the class does around its logins is proved in
/// <see cref="LoginKindsRunTests"/>, which needs no cell.</para>
/// </summary>
public sealed class LoginKindsTests
{
    /// <summary>The session read whose 200 says the gateway holds a session for the profile (contracts/pipeline.md).</summary>
    private const string SessionPath = "/bff/session";

    /// <summary>A test's own environment, in place of the process's (the companion's skip test).</summary>
    internal static readonly AsyncLocal<Func<string, string?>?> Environment = new();

    /// <summary>The reason each fact is skipped off the cell, naming the switch.</summary>
    internal static string SkipReason =>
        $"{StackSwitch.Variable} is unset or 'local': the login kinds are proved on the deployed cell only, with {StackSwitch.Variable}=deployed";

    [Fact]
    public Task A_sign_in_reaches_the_shell() =>
        OnApp1Async("sign in", (profile, app) => profile.SignInAsync(app));

    [Fact]
    public Task A_login_held_at_the_callback_reaches_the_shell() =>
        OnApp1Async("log in held at the callback", (profile, app) => ContinuityDriver.LoginAsync(profile, app, () => profile.StopAtCallbackAsync(app)));

    /// <summary>
    /// One login on app1 of a fresh profile, then the session read: skipped first, off the cell, so
    /// nothing is started; then each step through <see cref="StepAsync"/>, so a failure says where it stopped.
    /// </summary>
    private static async Task OnApp1Async(string login, Func<Profile, HarnessApp, Task> act)
    {
        if (StackSwitch.Choose(Environment.Value ?? System.Environment.GetEnvironmentVariable) != StackKind.Deployed)
        {
            Assert.Skip(SkipReason);
        }

        // Past the skip the switch names the cell, so Stacks.StartAsync starts a DeployedStack; the
        // companion puts its own stack in through Stacks.Instead, the seam DeployedRunTests uses (#211).
        IBrowserStack stack = null!;
        await StepAsync(null, "start the stack", async () => stack = await Stacks.StartAsync());
        await using (stack)
        {
            HarnessApp app = stack.App("app1");
            Profile profile = null!;
            await StepAsync(null, "open a profile", async () => profile = await stack.OpenAsync());
            await using (profile)
            {
                await StepAsync(profile, login, () => act(profile, app));
                await StepAsync(profile, "read the session", async () =>
                {
                    // The shell says "signed in" once its own read answered 200; this read is the gateway's
                    // answer at the end of the login, which the fact reports in its own words.
                    FetchResult session = await profile.FetchAsync(SessionPath);
                    Assert.True(session.Status == 200, $"{SessionPath} answered {session.Status}, not 200: the profile is not signed in");
                });
            }
        }
    }

    /// <summary>
    /// Runs one step of a fact, and when it fails, fails the fact with a message that names the step,
    /// prints the page's URL as <see cref="Redacted.Url(string)"/> does, and carries the failure's type
    /// and its message through <see cref="Redacted.Text"/>, with the page URL's query and fragment values
    /// and the context's cookie values taken out (N-16). The failure goes on as no inner exception,
    /// whose message xUnit would print as it came. A cancellation passes through as it came.
    /// </summary>
    internal static async Task StepAsync(Profile? profile, string step, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception failed) when (failed is not OperationCanceledException)
        {
            Assert.Fail(await FailureAsync(profile, step, failed));
        }
    }

    private static async Task<string> FailureAsync(Profile? profile, string step, Exception failed)
    {
        if (profile is null)
        {
            return $"stopped at '{step}', with no page open: {failed.GetType().Name}: {Redacted.Text(failed.Message, [])}";
        }

        string url = profile.Page.Url;
        string where = $"stopped at '{step}', with the page at {Redacted.Url(url)}";
        IEnumerable<string> cookies;
        try
        {
            cookies = [.. (await profile.Context.CookiesAsync()).Select(cookie => cookie.Value)];
        }
        catch (Exception unread) when (unread is not OperationCanceledException)
        {
            // Without the cookies nothing can take them out of the message, so it is withheld rather
            // than printed as it came (CODING_STANDARDS.md "A check fails closed"); the step's own
            // failure is still the one reported.
            return $"{where}: {failed.GetType().Name} (its message withheld: the context's cookies could not be read to take them out of it)";
        }

        return $"{where}: {failed.GetType().Name}: {Redacted.Text(failed.Message, [.. Redacted.ParameterValues(url), .. cookies])}";
    }
}
