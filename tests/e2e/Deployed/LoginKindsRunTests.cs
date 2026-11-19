using System.Reflection;
using System.Text.Json;
using Gateway.E2ETests.Stack;
using Gateway.TestHarness;
using Microsoft.Playwright;
using Xunit.Sdk;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// What <see cref="LoginKindsTests"/> does around its two logins (#1305), with no cell, as
/// <see cref="ContinuityRunTests"/> proves it for <see cref="ContinuityTests"/>: its two facts by name,
/// their skip ahead of the stack's start off the cell (#1199 Q2), both reaching the shell when the stack
/// is up, here the local one, and a failure's message naming its step and printing no state, code or
/// cookie (#1199 Q10, N-16's canaries).
/// </summary>
public sealed class LoginKindsRunTests
{
    // N-16's kind of canary, one per kind of value, so a message that lets one through says which.
    private const string CodeCanary = "code-canary-0123456789";
    private const string StateCanary = "state-canary-0123456789";
    private const string CookieCanary = "cookie-canary-0123456789";

    private static MethodInfo[] Facts() =>
        [.. typeof(LoginKindsTests).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(method => method.GetCustomAttribute<FactAttribute>() is not null)];

    /// <summary>Runs one fact as the runner would, and returns what it threw, or null: caught by hand, since Assert.Throws would hand a skip on to the runner.</summary>
    private static async Task<Exception?> InvokeAsync(MethodInfo fact)
    {
        try
        {
            await (Task)fact.Invoke(new LoginKindsTests(), null)!;
            return null;
        }
        catch (Exception thrown)
        {
            return thrown;
        }
    }

    /// <summary>The exception and every exception inside it, an aggregate's included.</summary>
    private static IEnumerable<Exception> Chain(Exception thrown) =>
        [thrown, .. (thrown is AggregateException aggregate ? aggregate.InnerExceptions : thrown.InnerException is { } inner ? [inner] : []).SelectMany(Chain)];

    [Fact]
    public void The_class_holds_exactly_the_two_facts_the_d0_step_names()
    {
        Assert.Equal(["A_login_held_at_the_callback_reaches_the_shell", "A_sign_in_reaches_the_shell"], Facts().Select(fact => fact.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("local")]
    public async Task Each_fact_reports_skipped_naming_the_switch_off_the_cell_before_the_stack_starts(string? stack)
    {
        LoginKindsTests.Environment.Value = name => name == StackSwitch.Variable ? stack : null;
        int started = 0;
        LoginKindsTests.Start.Value = () =>
        {
            started++;
            // Never dereferenced under the skip: the fact opens the stack only after it.
            return Task.FromResult<IBrowserStack>(null!);
        };

        foreach (MethodInfo fact in Facts())
        {
            Exception? skipped = await InvokeAsync(fact);

            Assert.Equal("SkipException", skipped?.GetType().Name);
            Assert.Contains(StackSwitch.Variable, skipped!.Message, StringComparison.Ordinal);
            Assert.True(started == 0, $"{fact.Name} started the stack before its skip");
        }
    }

    [Fact]
    public async Task Each_fact_reaches_the_shell_signed_in_when_its_stack_is_up()
    {
        LoginKindsTests.Environment.Value = name => name == StackSwitch.Variable ? "deployed" : null;
        int started = 0;
        LoginKindsTests.Start.Value = async () =>
        {
            started++;
            return await LocalStack.StartAsync();
        };

        foreach (MethodInfo fact in Facts())
        {
            Exception? failed = await InvokeAsync(fact);

            Assert.True(failed is null, $"{fact.Name} failed: {failed?.Message}");
        }

        Assert.Equal(2, started);
    }

    [Theory]
    [InlineData(nameof(LoginKindsTests.A_sign_in_reaches_the_shell), "sign in")]
    [InlineData(nameof(LoginKindsTests.A_login_held_at_the_callback_reaches_the_shell), "log in held at the callback")]
    public async Task A_failing_facts_message_names_its_step_and_carries_no_state_code_or_cookie(string name, string step)
    {
        LoginKindsTests.Environment.Value = variable => variable == StackSwitch.Variable ? "deployed" : null;
        HarnessApp? app1 = null;
        LoginKindsTests.Start.Value = async () =>
        {
            LocalStack local = await LocalStack.StartAsync();
            app1 = local.App("app1");
            return new RefusingStack(local);
        };

        Exception? thrown = await InvokeAsync(typeof(LoginKindsTests).GetMethod(name)!);

        // The runner prints each inner exception's message beside the failure's own, and an xUnit
        // exception's ToString leaves its inner one out, so the canaries are looked for in every exception
        // of the chain first, whatever the failure's type.
        Assert.NotNull(thrown);
        foreach (Exception printed in Chain(thrown))
        {
            foreach (string canary in new[] { CodeCanary, StateCanary, CookieCanary })
            {
                Assert.DoesNotContain(canary, printed.Message + printed, StringComparison.Ordinal);
            }
        }

        FailException failed = Assert.IsType<FailException>(thrown);
        string healthz = app1!.Url("/healthz").ToString();
        Assert.Equal(
            $"stopped at '{step}', with the page at {healthz}: InvalidOperationException: the provider refused the login at {healthz} with state [redacted] and code [redacted]; Cookie: planted=[redacted]",
            failed.Message);
        Assert.Null(failed.InnerException);
    }

    [Fact]
    public async Task A_step_with_no_page_open_names_its_step_and_prints_no_urls_query()
    {
        FailException failed = await Assert.ThrowsAsync<FailException>(() => LoginKindsTests.StepAsync(null, "start the stack",
            () => throw new InvalidOperationException($"no tunnel at https://app1.spa-poc.test:8443/healthz?state={StateCanary}")));

        Assert.Equal("stopped at 'start the stack', with no page open: InvalidOperationException: no tunnel at https://app1.spa-poc.test:8443/healthz", failed.Message);
    }

    /// <summary>
    /// The local stack, with every login refused the way a cell's provider might refuse one: the page is
    /// sent to an app URL whose query holds a code and a state, the context gets a cookie, and the
    /// failure's message carries all three, in the URL, bare, and as a cookie header written out.
    /// </summary>
    private sealed class RefusingStack(LocalStack inner) : IBrowserStack
    {
        private static async Task<Exception> RefuseAsync(Profile profile, HarnessApp app)
        {
            string leaky = app.Url($"/healthz?code={CodeCanary}&state={StateCanary}").ToString();
            await profile.GotoAsync(leaky);
            await profile.Context.AddCookiesAsync([new Cookie { Name = "planted", Value = CookieCanary, Url = app.Origin.ToString() }]);
            return new InvalidOperationException($"the provider refused the login at {leaky} with state {StateCanary} and code {CodeCanary}; Cookie: planted={CookieCanary}");
        }

        public async Task<Profile> OpenAsync()
        {
            // The profile's own stack is this one, so its sign-in and its hold come here.
            Profile opened = await inner.OpenAsync();
            return new Profile(this, opened.Context, opened.Page);
        }

        public async Task CompleteLoginAsync(Profile profile, HarnessApp app) => throw await RefuseAsync(profile, app);

        public async Task<Uri> StopAtCallbackAsync(Profile profile, HarnessApp app, Func<Task> start) => throw await RefuseAsync(profile, app);

        public StackKind Kind => inner.Kind;

        public HarnessApp App(string appId) => inner.App(appId);

        public Task<Profile> OpenCancellableAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task CompleteLoginCancellableAsync(Profile profile, HarnessApp app, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<Exchange>> ExchangesAsync() => inner.ExchangesAsync();

        public string ProviderHost => inner.ProviderHost;

        public string AuthorizeEndpoint => inner.AuthorizeEndpoint;

        public string EndSessionEndpoint => inner.EndSessionEndpoint;

        public Task<IReadOnlyList<JsonElement>> LogLinesAsync(string message) => inner.LogLinesAsync(message);

        public Task<IReadOnlyList<JsonElement>> SettledLogLinesAsync(string message) => inner.SettledLogLinesAsync(message);

        public Task SignInWithExpiredAccessTokenAsync(Profile profile, HarnessApp app) => throw new NotSupportedException();

        public Task StopAtProviderLoginAsync(Profile profile, Func<Task> action) => throw new NotSupportedException();

        public Task<string> ReleaseOfAsync(string appId) => inner.ReleaseOfAsync(appId);

        public Task PublishReleaseAsync(string appId, string release, IReadOnlyDictionary<string, byte[]> files) => throw new NotSupportedException();

        public Task SwitchReleaseAsync(HarnessApp app, string release, CancellationToken ct) => throw new NotSupportedException();

        public string ParentDomain => inner.ParentDomain;

        public void SignInAs(string user) => inner.SignInAs(user);

        public LogMark MarkLog() => inner.MarkLog();

        public Task AssertRuleLoggedAsync(LogMark after, string rule, string? appId = null) => inner.AssertRuleLoggedAsync(after, rule, appId);

        public Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request) => inner.SendRawAsync(request);

        public Task<HttpResponseMessage> SendRawCancellableAsync(HttpRequestMessage request, CancellationToken ct) => inner.SendRawCancellableAsync(request, ct);

        public Task OpenForeignPageAsync(Profile profile, Uri url, string html) => throw new NotSupportedException();

        public Task<HarnessApp> OnboardAsync(string appId) => throw new NotSupportedException();

        public Task OffboardAsync(string appId) => throw new NotSupportedException();

        public TimeSpan RegistryPropagation => inner.RegistryPropagation;

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
