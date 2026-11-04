using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Identity.Tests;

/// <summary>
/// #727: a session store operation that fails or runs out <c>StoreDeadline</c> answers 503
/// <c>session_store_unavailable</c> under <c>auth.store</c>, with one Error line naming the operation,
/// nothing of the session on it, and no cookie set or cleared (contracts/http-contract.md -> Errors;
/// contracts/request-gate.md -> Rule ids, the <c>auth.store</c> paragraph; the <c>continuity</c> spec's
/// F17). Each path is driven through the identity gateway's own pipeline over <see cref="ScriptedReads"/>,
/// which throws the DynamoDB store's own exception on cue. Reads are counted per request: the first is
/// always the cookie handler's, inside <c>UseAuthentication</c>.
/// </summary>
public sealed class StoreFailureTests
{
    private static readonly AppDefinition App1 = TestApps.Entry("app1", issuer: IdentityGateway.IssuerName);

    [Theory]
    [InlineData("/bff/session", "GET", 1)]
    [InlineData("/bff/session", "GET", 2)]
    [InlineData("/", "GET", 1)]
    [InlineData(IdentityGateway.ApiPath, "GET", 1)]
    [InlineData(IdentityGateway.ApiPath, "POST", 1)]
    [InlineData(IdentityGateway.LogoutPath, "POST", 1)]
    [InlineData("/bff/login", "GET", 1)]
    [InlineData("/bff/login", "GET", 2)]
    [InlineData(IdentityGateway.TokenPath, "GET", 2)]
    public async Task A_failed_store_read_answers_503_session_store_unavailable_under_auth_store_and_sends_no_cookie(
        string path, string method, int failingRead)
    {
        // Read 1 is the cookie handler's, recorded inside UseAuthentication and ended by
        // UsePlatformShellGate on every path and method: an asset navigation is not challenged, /bff and
        // /api are not read as anonymous, and an unsafe /api call never reaches the antiforgery stage,
        // which would answer 403 csrf_failed (here the stand-in API, which would answer 401). Read 2 is the
        // writer's own: /bff/session's read, /bff/login's discard under the gate, the token source's read
        // under the gate. Mutations: leave the cookie handler's read uncaught (the framework's handler),
        // and every read-1 row ends on an exception; keep UsePlatformShellGate's old first check, the
        // gated paths only, and the /bff and /api read-1 rows answer 401 or reach the endpoint; drop an
        // endpoint's catch, and its read-2 row ends on an exception.
        (IdentityGateway gateway, ScriptedReads store, CapturedLog log, Dictionary<string, string> jar) = await SignedInAsync();
        await using (gateway)
        {
            int before = log.Lines.Count;
            store.FailRead = failingRead;

            IdentityGateway.Answer answer = await gateway.SendAsync(App1, path, jar, method, IdentityGateway.Navigation);

            AssertStoreUnavailable(answer);
            AssertOneErrorLine(log, before, "read (GetItem)", jar);
            store.FailRead = null;
            Assert.Equal(200, (await gateway.SendAsync(App1, "/bff/session", jar)).Status);
        }
    }

    [Fact]
    public async Task A_failed_create_on_the_login_callback_answers_503_session_store_unavailable_not_the_failure_page_and_sets_no_session_cookie()
    {
        // The callback's sign-in creates the session after the remote handler's own try, so the
        // remote-failure event's static page never sees it; the app's OpenID Connect handler ends it.
        // Mutation: the framework's OpenID Connect handler; the callback ends on an exception.
        (IdentityGateway gateway, ScriptedReads store, CapturedLog log, _) = await SignedInAsync();
        await using (gateway)
        {
            IdentityGateway.Authorized authorized = await gateway.AuthorizeAsync(App1);
            int before = log.Lines.Count;
            store.FailCreate = true;

            IdentityGateway.Answer callback = await gateway.SendAsync(
                App1, authorized.Callback.PathAndQuery, new Dictionary<string, string>(authorized.LoginState));

            AssertStoreUnavailable(callback, sessionCookieAllowed: false);
            Assert.Equal("", callback.Location);
            AssertOneErrorLine(log, before, "create (PutItem)", authorized.LoginState.ToDictionary());
        }
    }

    [Fact]
    public async Task A_failed_remove_in_the_bff_login_discard_answers_503_session_store_unavailable_and_starts_no_login()
    {
        // GET /bff/login's discard takes no F11 exception (Q189). Mutation: drop LoginAsync's catch; the
        // discard ends on an exception.
        (IdentityGateway gateway, ScriptedReads store, CapturedLog log, Dictionary<string, string> jar) = await SignedInAsync();
        await using (gateway)
        {
            int before = log.Lines.Count;
            store.FailRemove = true;

            IdentityGateway.Answer answer = await gateway.SendAsync(App1, "/bff/login", jar, headers: IdentityGateway.Navigation);

            AssertStoreUnavailable(answer);
            Assert.Equal("", answer.Location);
            AssertOneErrorLine(log, before, "remove (DeleteItem)", jar);
        }
    }

    [Fact]
    public async Task A_failed_remove_after_the_provider_refuses_the_refresh_answers_session_store_unavailable_and_clears_no_cookie()
    {
        // The token source's end on invalid_grant takes no F11 exception (Q189): the remove under the
        // gate fails, the source notes auth.store and answers StoreUnavailable, which the access-token step
        // answers. The sign-out that would clear the cookie never runs. Mutation: drop the source's catch;
        // the call ends on an exception.
        (IdentityGateway gateway, ScriptedReads store, CapturedLog log, Dictionary<string, string> jar) =
            await SignedInAsync(due: true);
        await using (gateway)
        {
            gateway.Idp.Faults.RefuseRefresh = true;
            int before = log.Lines.Count;
            store.FailRemove = true;

            IdentityGateway.Answer answer = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

            Assert.Equal((503, "session_store_unavailable", "auth.store"), (answer.Status, answer.Body, answer.Rule));
            Assert.Empty(answer.Cookies);
            AssertOneErrorLine(log, before, "remove (DeleteItem)", jar);
        }
    }

    [Fact]
    public async Task A_failed_lease_acquire_answers_session_store_unavailable_and_clears_no_cookie()
    {
        // Item 4's lease acquire (#727): the refresh lease's acquire is a session store operation too, and
        // the DynamoDB lease table throws the store's own exception when it fails. The source notes
        // auth.store and answers StoreUnavailable; no grant is asked for and the session stands.
        // Mutation: drop the source's catch; the call ends on an exception.
        CapturedLog log = new();
        ScriptedLeases leases = new(new InMemoryRefreshLeases(TimeProvider.System))
        {
            BeforeAcquire = _ => throw new SessionStoreUnavailableException(
                "lease acquire (PutItem)", DynamoDbSessionTable.DefaultStoreDeadline, null),
        };
        await using IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1], services => services.AddSingleton(log.Provider), leases: leases);
        Dictionary<string, string> jar = await RefreshLeaseTests.LoginDueAsync(gateway, App1);
        int before = log.Lines.Count;

        IdentityGateway.Answer answer = await gateway.SendAsync(App1, IdentityGateway.TokenPath, jar);

        Assert.Equal((503, "session_store_unavailable", "auth.store"), (answer.Status, answer.Body, answer.Rule));
        Assert.Empty(answer.Cookies);
        Assert.Empty(gateway.Idp.RefreshGrants);
        AssertOneErrorLine(log, before, "lease acquire (PutItem)", jar);
        leases.BeforeAcquire = null;
        Assert.Equal(200, (await gateway.SendAsync(App1, "/bff/session", jar)).Status);
    }

    private static async Task<(IdentityGateway, ScriptedReads, CapturedLog, Dictionary<string, string>)> SignedInAsync(bool due = false)
    {
        ScriptedReads store = new(new InMemorySessionStore());
        CapturedLog log = new();
        IdentityGateway gateway = await IdentityGateway.StartAsync(
            [App1], services => services.AddSingleton(log.Provider), sessions: new RecordingSessionStore(store));
        Dictionary<string, string> jar = due
            ? await RefreshLeaseTests.LoginDueAsync(gateway, App1)
            : (await gateway.LoginAsync(App1)).Jar;
        return (gateway, store, log, jar);
    }

    /// <summary>
    /// 503 with the problem body, the request's rule <c>auth.store</c>, and no session cookie set or
    /// cleared: no cookie at all, or on the callback, whose login-state cookies the handler has already
    /// deleted, none by the session cookie's name.
    /// </summary>
    private static void AssertStoreUnavailable(IdentityGateway.Answer answer, bool sessionCookieAllowed = true)
    {
        Assert.Equal((503, "auth.store"), (answer.Status, answer.Rule));
        // The token probe answers the source's outcome by its code alone, as text; every other writer
        // writes the problem body.
        Assert.Equal("session_store_unavailable", answer.Body.StartsWith('{')
            ? JsonDocument.Parse(answer.Body).RootElement.GetProperty("code").GetString()
            : answer.Body);
        if (sessionCookieAllowed)
        {
            Assert.Empty(answer.Cookies);
        }
        else
        {
            Assert.DoesNotContain(answer.Cookies, c => c.Name.Value == CookieOptionsSetup.CookieName);
        }
    }

    /// <summary>
    /// One Error line since <paramref name="before"/>, the identity chain's own, naming the operation and
    /// carrying no cookie value (N-16).
    /// </summary>
    private static void AssertOneErrorLine(CapturedLog log, int before, string operation, Dictionary<string, string> cookies)
    {
        // The token library's own Error line for a refused refresh is not counted: its category is held at
        // no level in the gateway (Platform.Telemetry's LogFloors), so no deployed log carries it.
        CapturedLine error = Assert.Single(log.Lines.Skip(before), line => line.Level >= LogLevel.Error
            && !line.Category.StartsWith("Duende.AccessTokenManagement", StringComparison.Ordinal));
        Assert.Equal(typeof(StoreFailure).FullName, error.Category);
        Assert.Contains(operation, error.Text, StringComparison.Ordinal);
        Assert.All(cookies.Values, value => Assert.DoesNotContain(value, error.Text, StringComparison.Ordinal));
    }
}
