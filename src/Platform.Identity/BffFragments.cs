using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// The fragment parts of <c>/bff/*</c> (#994; contracts/http-contract.md -> Endpoints and Fragments):
/// the sign-in chain's step <c>GET /bff/chain</c>, the fallback <c>GET /bff/login?fragment=</c>,
/// <c>GET /bff/fragments</c> and the <c>fragments</c> member of <c>GET /bff/session</c>.
/// <c>UsePlatformRequestGate</c> has already held each to its method and fetch metadata
/// (contracts/request-gate.md: <c>/bff/chain</c> a navigation with any <c>Sec-Fetch-Site</c>,
/// ADR-0040), so nothing here re-checks them.
/// </summary>
internal static class BffFragments
{
    public const string ChainPath = "/bff/chain";
    public const string FragmentsPath = "/bff/fragments";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ChainPath, ChainAsync);
        endpoints.MapGet(FragmentsPath, FragmentsAsync);
    }

    /// <summary>The URL of the step <paramref name="continuation"/> names: <c>/bff/chain?c=</c>, nothing else in the query.</summary>
    public static string StepUrl(HttpContext context, AppDefinition app, ChainContinuation continuation) =>
        ChainPath + QueryString.Create("c", continuation.Protect(DataProtection(context), app.AppId));

    /// <summary>
    /// <c>GET /bff/chain?c=</c> (ADR-0040 decision 3; contracts/http-contract.md -> "<c>/bff/chain</c>"). The
    /// step requires the host session and that its key is the continuation's; it skips every fragment of
    /// the snapshot the current entry no longer lists and every one the record already holds signed in
    /// with its current client, so a replayed step does no work twice, and challenges the next one's
    /// scheme. Nothing left ends the chain at the continuation's <c>returnUrl</c>.
    /// </summary>
    private static async Task ChainAsync(HttpContext context)
    {
        AppDefinition app = Bff.Pinned(context, "MapPlatformBff");
        AppSessions sessions = context.RequestServices.GetRequiredService<AppSessions>();
        AuthenticateResult session = await context.AuthenticateAsync(SchemeNames.Cookie(app.AppId));
        if (sessions.KeyOf(context, session) is not { } key)
        {
            // The shell gate's usual challenge, back to the root rather than to this step, whose
            // continuation names a session that is not this browser's.
            await context.ChallengeAsync(SchemeNames.Oidc(app.AppId), new AuthenticationProperties { RedirectUri = "/" });
            return;
        }

        TimeProvider clock = context.RequestServices.GetRequiredService<TimeProvider>();
        DateTimeOffset now = clock.GetUtcNow();
        if (context.Request.Query["c"] is not { Count: 1 } values
            || ChainContinuation.Read(DataProtection(context), app.AppId, values[0]) is not { } continuation
            || !string.Equals(continuation.SessionKey, key, StringComparison.Ordinal)
            || continuation.ExpiresAt <= now)
        {
            // auth.chain: no problem body, no state change, and never a caller-supplied URL (ADR-0040 decision 4).
            Rejection.Note(context, RuleIds.AuthChain);
            context.Response.Redirect("/");
            return;
        }

        SessionRecord? record;
        try
        {
            record = await sessions.ReadAsync(app, key, context.RequestAborted);
        }
        catch (SessionStoreUnavailableException e)
        {
            await StoreFailure.EndAsync(context, e.Operation);
            return;
        }

        if (record is null)
        {
            context.Response.Redirect(continuation.ReturnUrl);
            return;
        }

        AuthenticationProperties held = Properties(record);
        for (int position = Math.Max(continuation.Position, 0); position < continuation.Fragments.Length; position++)
        {
            string fragmentId = continuation.Fragments[position];
            if (Find(app, fragmentId) is not { } fragment || FragmentSets.StateOf(held, fragment) == FragmentSets.SignedIn)
            {
                continue;
            }

            await context.ChallengeAsync(
                SchemeNames.Oidc(app.AppId, fragment.FragmentId),
                FragmentEvents.HopProperties(context, app, FragmentEvents.Chain, continuation.Renewed(position + 1, now)));
            return;
        }

        context.Response.Redirect(continuation.ReturnUrl);
    }

    /// <summary>
    /// <c>GET /bff/login?fragment={fragmentId}</c> when the browser has a session: signs in to that one
    /// fragment's client and returns to <c>returnUrl</c>, discarding nothing and never silent. True when it
    /// answered; false when there is no session, and the ordinary login, chain included, follows.
    /// </summary>
    public static async Task<bool> FallbackAsync(HttpContext context, AppDefinition app, string fragmentId, string returnUrl)
    {
        AuthenticateResult session = await context.AuthenticateAsync(SchemeNames.Cookie(app.AppId));
        if (context.RequestServices.GetRequiredService<AppSessions>().KeyOf(context, session, refusesDeadSession: false) is not { } key)
        {
            return false;
        }

        if (Find(app, fragmentId) is not { } fragment)
        {
            context.Response.Redirect(returnUrl);
            return true;
        }

        DateTimeOffset now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        ChainContinuation back = new(key, [], 0, returnUrl, now + ChainContinuation.Lifetime);
        await context.ChallengeAsync(
            SchemeNames.Oidc(app.AppId, fragment.FragmentId),
            FragmentEvents.HopProperties(context, app, FragmentEvents.Fallback, back));
        return true;
    }

    /// <summary>
    /// <c>GET /bff/fragments</c>: every fragment of the host's shell in registry order with its entry URL,
    /// which names no release, or 401 <c>unauthenticated</c> with no session. Not cached: the list follows
    /// the live entry.
    /// </summary>
    private static async Task FragmentsAsync(HttpContext context)
    {
        AppDefinition app = Bff.Pinned(context, "MapPlatformBff");
        context.Response.Headers.CacheControl = "no-store";

        SessionRecord? record;
        try
        {
            AppSessions sessions = context.RequestServices.GetRequiredService<AppSessions>();
            AuthenticateResult session = await context.AuthenticateAsync(SchemeNames.Cookie(app.AppId));
            record = sessions.KeyOf(context, session) is { } key ? await sessions.ReadAsync(app, key, context.RequestAborted) : null;
        }
        catch (SessionStoreUnavailableException e)
        {
            await StoreFailure.EndAsync(context, e.Operation);
            return;
        }

        if (record is null)
        {
            await Problems.WriteAsync(context, Problems.Unauthenticated);
            return;
        }

        await context.Response.WriteAsJsonAsync(
            new FragmentsAnswer([.. app.Fragments.Select(f => new FragmentItem(f.FragmentId, PlatformPaths.EntryUrl(f.FragmentId, f.Entry)))]),
            context.RequestAborted);
    }

    /// <summary>
    /// <c>GET /bff/session</c>'s <c>fragments</c>: every fragment of the current entry, as the record holds
    /// it; <c>{}</c> for an entry with none.
    /// </summary>
    public static Dictionary<string, string> StatesOf(AppDefinition app, SessionRecord record)
    {
        AuthenticationProperties held = Properties(record);
        return app.Fragments.ToDictionary(f => f.FragmentId, f => FragmentSets.StateOf(held, f), StringComparer.Ordinal);
    }

    private static AuthenticationProperties Properties(SessionRecord record) =>
        (TicketSerializer.Default.Deserialize(record.Ticket.ToArray())
            ?? throw new InvalidOperationException("A session record holds a serialized ticket.")).Properties;

    private static FragmentDefinition? Find(AppDefinition app, string fragmentId) =>
        app.Fragments.FirstOrDefault(f => string.Equals(f.FragmentId, fragmentId, StringComparison.Ordinal));

    private static IDataProtectionProvider DataProtection(HttpContext context) =>
        context.RequestServices.GetRequiredService<IDataProtectionProvider>();

    private sealed record FragmentsAnswer(IReadOnlyList<FragmentItem> Fragments);

    private sealed record FragmentItem(string FragmentId, string EntryUrl);
}
