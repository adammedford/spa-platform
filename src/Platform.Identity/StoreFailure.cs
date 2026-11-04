using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// <c>auth.store</c> (contracts/request-gate.md -> Rule ids, its <c>auth.store</c> paragraph; the
/// <c>continuity</c> spec's F17; #727): a session store operation that failed or ran out
/// <c>StoreDeadline</c>, which the store reports as <see cref="SessionStoreUnavailableException"/>. No
/// cookie is cleared and the session, if it exists, is kept.
///
/// Where the failure is met decides who ends it, and this type is how each writer does so with the same
/// one Error line. The cookie handler's read inside <c>UseAuthentication</c> writes no response: it
/// records the failure on the request (<see cref="Record"/>), and <c>UsePlatformShellGate</c>, the next
/// stage of workstream 3's, ends it on every path (<see cref="Recorded"/>, then <see cref="EndAsync"/>).
/// The login callback's create, the <c>/bff</c> endpoints and the token source end or note the failure
/// they meet themselves. The line names the app and the operation, and nothing of the session: no key,
/// no cookie, no token (N-16); the exception's message and inner exception stay off it.
///
/// Only the store's own exception is read as this failure. A store that throws anything else has a fault
/// this type cannot tell from a bug, and ends on the gateway's generic 500 as before.
/// </summary>
internal static partial class StoreFailure
{
    /// <summary>
    /// Records a failure met where no response may be written, the cookie handler's authenticate, for
    /// <c>UsePlatformShellGate</c> to end. Writes nothing and logs nothing.
    /// </summary>
    public static void Record(HttpContext context, SessionStoreUnavailableException failure) =>
        context.Features.Set(new RecordedFailure(failure.Operation));

    /// <summary>The operation <see cref="Record"/> left on this request, or null when none failed.</summary>
    public static string? Recorded(HttpContext context) => context.Features.Get<RecordedFailure>()?.Operation;

    /// <summary>Logs the one Error line and ends the request under <c>auth.store</c>, with its problem body.</summary>
    public static Task EndAsync(HttpContext context, string operation)
    {
        Log(context, operation);
        return Rejection.EndAsync(context, RuleIds.AuthStore);
    }

    /// <summary>
    /// Logs the one Error line and notes <c>auth.store</c> without writing a response: the token source's
    /// half, whose <see cref="AccessTokenResult.StoreUnavailable"/> the proxy's access-token step answers.
    /// </summary>
    public static void Note(HttpContext context, string operation)
    {
        Log(context, operation);
        Rejection.Note(context, RuleIds.AuthStore);
    }

    private static void Log(HttpContext context, string operation)
    {
        ILogger logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(StoreFailure).FullName!);
        Failed(logger, context.Features.Get<IAppFeature>()?.App.AppId ?? "(none)", operation);
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "The session store's {Operation} did not complete for app {AppId}: it failed, or its outcome was unknown when "
            + "StoreDeadline ran out. The request is refused with the session kept and no cookie cleared (F17).")]
    private static partial void Failed(ILogger logger, string appId, string operation);

    /// <summary>The failed operation, private to this type so nothing else can plant or read one.</summary>
    private sealed class RecordedFailure(string operation)
    {
        public string Operation { get; } = operation;
    }
}
