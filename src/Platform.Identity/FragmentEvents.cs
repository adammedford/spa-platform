using System.Collections;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// The sign-in chain's events (#994; the fragments spec #961 -> "The eager chain" and "Failing hops and
/// the marks"; contracts/http-contract.md -> Fragments -> "Sign-in").
///
/// <list type="bullet">
/// <item>The shell's login callback, on an entry that lists fragments, signs in as the stock handler
/// would and then sends the browser to the chain's first <c>/bff/chain</c> step rather than to
/// <c>returnUrl</c>. An entry with none keeps the stock callback.</item>
/// <item>A fragment's callback never signs in: the stock sign-in would write a whole new ticket, or
/// renew the whole ticket, which <see cref="AppBoundTicketStore"/> refuses. It merges the fragment's set
/// into the session record under that set's gate, within <c>MergeBudget</c>, and goes on to the next
/// step, or, for the fallback login, to <c>returnUrl</c>.</item>
/// <item>A fragment's failed hop whose state was readable continues the chain the same way. Only a
/// definitive refusal marks the fragment <c>unavailable</c>, and only once that write is confirmed;
/// anything else writes nothing and the fragment reads <c>loginRequired</c>. A state that does not read
/// keeps the gateway's one failure page.</item>
/// </list>
///
/// Each fragment write logs one event line with its <c>store_attempts</c>, the versioned replaces it
/// sent, retries included, and its outcome, also when the budget ran out or the store failed: G9's
/// O2 evidence (#961, Q35, Q44).
/// </summary>
internal static partial class FragmentEvents
{
    /// <summary>The hop marker in a fragment challenge's protected state: <see cref="Chain"/> or <see cref="Fallback"/>.</summary>
    internal const string HopItem = ".platform.hop";

    /// <summary>A hop of the eager chain, which never prompts.</summary>
    internal const string Chain = "chain";

    /// <summary><c>GET /bff/login?fragment=</c>'s hop, started by the user, which may prompt.</summary>
    internal const string Fallback = "fallback";

    /// <summary>The continuation the hop goes on with, protected as a <c>/bff/chain</c> step's is.</summary>
    private const string ContinuationItem = ".platform.chain";

    /// <summary>
    /// The refusals that mark a fragment <c>unavailable</c> (#961 -> "Failing hops and the marks"): RFC
    /// 6749's client and scope errors, at the authorize endpoint or the token endpoint, and
    /// <c>invalid_grant</c> on the code. A foreign <c>sub</c> is the fifth, decided at the merge.
    /// https://www.rfc-editor.org/rfc/rfc6749#section-4.1.2.1 and #section-5.2
    /// </summary>
    private static readonly ImmutableHashSet<string> Definitive =
        ImmutableHashSet.Create(StringComparer.Ordinal, "invalid_client", "unauthorized_client", "invalid_scope", "invalid_grant");

    /// <summary>The properties of a fragment challenge for a hop that goes on with <paramref name="continuation"/>.</summary>
    public static AuthenticationProperties HopProperties(HttpContext context, AppDefinition app, string hop, ChainContinuation continuation)
    {
        // A redirect target of its own, never read: with none the handler takes this request's URL, a
        // /bff/chain step's continuation among it, and logs it at Debug (OpenIdConnectHandler's
        // PostAuthenticationLocalRedirect; contracts/telemetry.md: query strings are never logged).
        AuthenticationProperties properties = new() { RedirectUri = "/" };
        properties.Items[HopItem] = hop;
        properties.Items[ContinuationItem] = continuation.Protect(DataProtection(context), app.AppId);
        return properties;
    }

    /// <summary>Whether a challenge's properties are a chain hop's, for a profile deciding on <c>prompt=none</c>.</summary>
    public static bool IsChainHop(AuthenticationProperties? properties) =>
        properties is not null && properties.Items.TryGetValue(HopItem, out string? hop) && hop == Chain;

    /// <summary>The shell's callback: stock, except that an entry with fragments goes on to the chain.</summary>
    public static void ApplyShell(OpenIdConnectOptions options, AppDefinition built)
    {
        if (built.Fragments.IsEmpty)
        {
            return;
        }

        Func<TicketReceivedContext, Task> trimmed = options.Events.OnTicketReceived;
        options.Events.OnTicketReceived = async context =>
        {
            await trimmed(context);
            HttpContext http = context.HttpContext;
            AppDefinition app = Bff.Pinned(http, "the login callback");
            // An earlier event that ended the callback or skipped it keeps it so: the stock handler signs
            // in only when the event left neither (RemoteAuthenticationHandler.HandleRequestAsync, below).
            if (context.Result is { Handled: true } or { Skipped: true }
                || app.Fragments.IsEmpty || context.Principal is not { } principal)
            {
                return;
            }

            // The handler raises this event before it signs in and reads ReturnUri only after, while the
            // session key exists only once the ticket store has stored the session. So the sign-in the
            // handler would make is made here, with its own principal and properties, and the redirect
            // follows it (RemoteAuthenticationHandler.HandleRequestAsync:
            // https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/Core/src/RemoteAuthenticationHandler.cs).
            // A store failure in it leaves the event and reaches PlatformOidcHandler, as the stock one does.
            await http.SignInAsync(SchemeNames.Cookie(app.AppId), principal, context.Properties);
            string key = Sessions(http).OpenedKey(http)
                ?? throw new InvalidOperationException("The cookie handler's sign-in opens the session through the ticket store.");

            string returnUrl = context.ReturnUri is { } back && ReturnUrl.IsLocal(back) ? back : "/";
            ChainContinuation first = new(
                key, [.. app.Fragments.Select(f => f.FragmentId)], 0, returnUrl, Clock(http).GetUtcNow() + ChainContinuation.Lifetime);
            http.Response.Redirect(BffFragments.StepUrl(http, app, first));
            context.HandleResponse();
        };
    }

    /// <summary>A fragment's callback and its failure: the merge, the mark and the next step.</summary>
    public static void ApplyFragment(OpenIdConnectOptions options, FragmentDefinition fragment)
    {
        Func<TicketReceivedContext, Task> trimmed = options.Events.OnTicketReceived;
        options.Events.OnTicketReceived = async context =>
        {
            await trimmed(context);
            // As the shell's: an earlier event that ended or skipped the callback merges nothing.
            if (context.Result is { Handled: true } or { Skipped: true })
            {
                return;
            }

            // Every path below ends the response, so the stock sign-in never runs (#961).
            context.HandleResponse();
            HttpContext http = context.HttpContext;
            AppDefinition app = Bff.Pinned(http, "a fragment callback");
            if (Hop.Read(http, app, context.Properties) is not { } hop)
            {
                await AuthenticationEvents.FailAsync(http);
                return;
            }

            string? subject = context.Principal?.FindFirst("sub")?.Value;
            await WriteAsync(http, app, fragment, hop, record =>
                string.Equals(record.Subject, subject, StringComparison.Ordinal)
                    ? Merge(record, fragment, context.Properties?.GetTokens() ?? [])
                    : null);
        };

        Func<RemoteFailureContext, Task> page = options.Events.OnRemoteFailure;
        options.Events.OnRemoteFailure = async context =>
        {
            HttpContext http = context.HttpContext;
            AppDefinition app = Bff.Pinned(http, "a fragment callback");
            if (Hop.Read(http, app, context.Properties) is not { } hop)
            {
                // A foreign or garbage state: the 400 failure page, and the record is never touched.
                await page(context);
                return;
            }

            context.HandleResponse();
            Rejection.Note(http, RuleIds.AuthCallback);
            if (ErrorOf(context.Failure) is { } error && Definitive.Contains(error))
            {
                await MarkAsync(http, app, fragment, hop);
                return;
            }

            // login_required, interaction_required, consent_required or a transient failure: nothing is
            // written, the fragment reads loginRequired, and the chain goes on.
            Continue(http, app, hop);
        };
    }

    /// <summary>
    /// The merge's write (W1, W2): the set replaces whatever the fragment had, or, for a set issued to
    /// another <c>sub</c>, <paramref name="change"/> answers null and the hop is a definitive refusal.
    /// </summary>
    private static async Task WriteAsync(
        HttpContext http, AppDefinition app, FragmentDefinition fragment, Hop hop, Func<SessionRecord, SessionRecord?> change)
    {
        bool foreign = false;
        MergeResult result;
        using (AppSessions.Turn turn = await EnterAsync(http, app, fragment, hop))
        {
            try
            {
                result = await Bounded(http, turn, record =>
                {
                    SessionRecord? next = change(record);
                    foreign = next is null;
                    return next;
                });
            }
            catch (MergeFailedException e)
            {
                Logged(http, app, fragment, hop.Kind, "unconfirmed", e.Attempts);
                await StoreFailure.EndAsync(http, e.Failure.Operation);
                return;
            }
        }

        if (foreign)
        {
            // The set was issued for another user than the session's: refused, and a definitive refusal.
            Rejection.Note(http, RuleIds.AuthCallback);
            await MarkAsync(http, app, fragment, hop);
            return;
        }

        Logged(http, app, fragment, hop.Kind, Outcome(result.Outcome), result.Attempts);
        if (result.Outcome == MergeOutcome.Exhausted)
        {
            // W1/W2 conflict, exhausted: the winner's record stands, the tokens are dropped unrevoked.
            Exhausted(Logger(http), app.AppId, fragment.FragmentId, SessionRef(http), result.Attempts);
        }

        if (result.Outcome == MergeOutcome.Missing)
        {
            http.Response.Redirect(hop.Continuation.ReturnUrl);
            return;
        }

        Continue(http, app, hop);
    }

    /// <summary>
    /// The <c>unavailable</c> mark (W4): never over a set already signed in, re-applied within the
    /// budget on a conflict, and exhausted, no mark. A missing record stops the chain at <c>returnUrl</c>.
    /// </summary>
    private static async Task MarkAsync(HttpContext http, AppDefinition app, FragmentDefinition fragment, Hop hop)
    {
        MergeResult result;
        using (AppSessions.Turn turn = await EnterAsync(http, app, fragment, hop))
        {
            try
            {
                result = await Bounded(http, turn, record => Changed(record, p => FragmentSets.TryMark(p, fragment)));
            }
            catch (MergeFailedException e)
            {
                Logged(http, app, fragment, "mark", "unconfirmed", e.Attempts);
                await StoreFailure.EndAsync(http, e.Failure.Operation);
                return;
            }
        }

        Logged(http, app, fragment, "mark", Outcome(result.Outcome), result.Attempts);
        if (result.Outcome == MergeOutcome.Missing)
        {
            http.Response.Redirect(hop.Continuation.ReturnUrl);
            return;
        }

        Continue(http, app, hop);
    }

    /// <summary>The fragment's set gate on the hop's session. A read that fails there is the store's failure.</summary>
    private static async Task<AppSessions.Turn> EnterAsync(HttpContext http, AppDefinition app, FragmentDefinition fragment, Hop hop)
    {
        try
        {
            return await Sessions(http).EnterAsync(http, app, hop.Continuation.SessionKey, fragment);
        }
        catch (SessionStoreUnavailableException e)
        {
            throw new MergeFailedException(e, 0);
        }
    }

    private static Task<MergeResult> Bounded(HttpContext http, AppSessions.Turn turn, Func<SessionRecord, SessionRecord?> change)
    {
        IServiceProvider services = http.RequestServices;
        int budget = services.GetService<PlatformPolicy>()?.MergeBudget ?? DefaultBudget;
        TimeSpan deadline = services.GetRequiredService<AccessTokenSourceOptions>().StoreDeadline;
        return turn.MergeAsync(change, budget, deadline, Clock(http));
    }

    /// <summary><c>Policy:MergeBudget</c>'s placeholder, for a host that registered no <see cref="PlatformPolicy"/>.</summary>
    private const int DefaultBudget = 3;

    private static SessionRecord Merge(SessionRecord record, FragmentDefinition fragment, IEnumerable<AuthenticationToken> tokens) =>
        Changed(record, p =>
        {
            FragmentSets.WriteSet(p, fragment, tokens);
            return true;
        })!;

    /// <summary>The record with its ticket's properties changed by <paramref name="change"/>, or null when it changed nothing.</summary>
    private static SessionRecord? Changed(SessionRecord record, Func<AuthenticationProperties, bool> change)
    {
        AuthenticationTicket ticket = TicketSerializer.Default.Deserialize(record.Ticket.ToArray())
            ?? throw new InvalidOperationException("A session record holds a serialized ticket.");
        return change(ticket.Properties) ? record with { Ticket = TicketSerializer.Default.Serialize(ticket) } : null;
    }

    /// <summary>On to the chain's next step, or for the fallback to <c>returnUrl</c>.</summary>
    private static void Continue(HttpContext http, AppDefinition app, Hop hop) =>
        http.Response.Redirect(hop.Kind == Chain
            ? BffFragments.StepUrl(http, app, hop.Continuation.Renewed(hop.Continuation.Position, Clock(http).GetUtcNow()))
            : hop.Continuation.ReturnUrl);

    /// <summary>
    /// The provider's error code, from the protocol exception the handler or the code exchange raised,
    /// never its description, which is untrusted text and may carry a credential (N-16).
    /// </summary>
    private static string? ErrorOf(Exception? failure)
    {
        for (Exception? e = failure; e is not null; e = e.InnerException)
        {
            if (e.Data is IDictionary data && data["error"] is string error && error.Length > 0)
            {
                return error;
            }
        }

        return null;
    }

    private static string Outcome(MergeOutcome outcome) => outcome switch
    {
        MergeOutcome.Written => "written",
        MergeOutcome.Unchanged => "unchanged",
        MergeOutcome.Missing => "missing",
        MergeOutcome.Exhausted => "exhausted",
        _ => throw new InvalidOperationException($"No event outcome for {outcome}."),
    };

    private static void Logged(HttpContext http, AppDefinition app, FragmentDefinition fragment, string writer, string outcome, int attempts) =>
        FragmentWrite(Logger(http), app.AppId, fragment.FragmentId, SessionRef(http), writer, outcome, attempts);

    private static string SessionRef(HttpContext http) =>
        http.Features.Get<IRequestTelemetryFeature>()?.SessionRef ?? "(none)";

    private static ILogger Logger(HttpContext http) =>
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(FragmentEvents).FullName!);

    private static AppSessions Sessions(HttpContext http) => http.RequestServices.GetRequiredService<AppSessions>();

    private static TimeProvider Clock(HttpContext http) => http.RequestServices.GetRequiredService<TimeProvider>();

    private static IDataProtectionProvider DataProtection(HttpContext http) =>
        http.RequestServices.GetRequiredService<IDataProtectionProvider>();

    // App, fragment, session_ref, writer, outcome and a count: nothing here is a credential (N-16).
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Fragment sign-in for app {AppId}, fragment {FragmentId}, session {SessionRef}: the {Writer} write ended {Outcome} "
            + "after {StoreAttempts} versioned replaces.")]
    private static partial void FragmentWrite(
        ILogger logger, string appId, string fragmentId, string sessionRef, string writer, string outcome, int storeAttempts);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Fragment sign-in for app {AppId}, fragment {FragmentId}, session {SessionRef} lost every one of its {StoreAttempts} "
            + "versioned replaces: the other writer's record stands, and this hop's tokens are dropped unrevoked.")]
    private static partial void Exhausted(ILogger logger, string appId, string fragmentId, string sessionRef, int storeAttempts);

    /// <summary>A hop's marker and continuation, read from a fragment callback's protected state.</summary>
    private sealed record Hop(string Kind, ChainContinuation Continuation)
    {
        /// <summary>
        /// The hop the state carries, or null for a state with none, or whose continuation does not read
        /// for this app. The state itself is the handler's to have validated: these properties exist only
        /// when it read.
        /// </summary>
        public static Hop? Read(HttpContext http, AppDefinition app, AuthenticationProperties? properties)
        {
            if (properties is null
                || !properties.Items.TryGetValue(HopItem, out string? kind) || kind is not (Chain or Fallback)
                || !properties.Items.TryGetValue(ContinuationItem, out string? value)
                || ChainContinuation.Read(DataProtection(http), app.AppId, value) is not { } continuation)
            {
                return null;
            }

            return new Hop(kind, continuation);
        }
    }
}
