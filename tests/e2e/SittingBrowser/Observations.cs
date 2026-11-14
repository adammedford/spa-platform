namespace Gateway.E2ETests.SittingBrowser;

/// <summary>
/// Every line shape the sitting browser prints (#1033), one member each, all through
/// <see cref="ObservationLine.Format"/>. The navigation tokens are #1024 Q9's; the fetch commands
/// print <c>http=</c> and <c>problem=</c> in place of one; the rest are the process's and the
/// commands' own answers. A test feeds each member's line through <c>tools/wiz_redact.py</c>, so a
/// shape added here without a sample there fails that test.
/// </summary>
public static class Observations
{
    /// <summary>The app's <c>/</c> with <c>#session-state</c> reading <c>signed in</c>.</summary>
    public const string SignedInToken = "signed-in";

    /// <summary>The provider's host with a visible username or password field.</summary>
    public const string LoginPromptToken = "login-prompt";

    /// <summary>A navigation answered 404.</summary>
    public const string NotFoundToken = "not-found";

    /// <summary>A navigation that failed before a response.</summary>
    public const string TunnelDownToken = "tunnel-down";

    /// <summary>The command's bound passed with no other token reached.</summary>
    public const string TimeoutToken = "timeout";

    /// <summary>Anything else a navigation ended on.</summary>
    public const string OtherToken = "other";

    /// <summary>The navigation tokens, which a <c>status</c> line names as its profile's last observation among the rest.</summary>
    public static readonly IReadOnlyList<string> NavigationTokens = [SignedInToken, LoginPromptToken, NotFoundToken, TunnelDownToken, TimeoutToken, OtherToken];

    public static string SignedIn(DateTimeOffset at, string profile, string app, string? user, Readiness ready) =>
        ObservationLine.Format(SignedInToken, at, profile, [("app", app), .. user is null ? [] : new[] { ("as", user) }, .. ready.Fields]);

    public static string LoginPrompt(DateTimeOffset at, string profile, string app, Readiness ready) =>
        ObservationLine.Format(LoginPromptToken, at, profile, [("app", app), .. ready.Fields]);

    public static string NotFound(DateTimeOffset at, string profile, string app, Readiness ready) =>
        ObservationLine.Format(NotFoundToken, at, profile, [("app", app), .. ready.Fields]);

    public static string TunnelDown(DateTimeOffset at, string profile, string app) =>
        ObservationLine.Format(TunnelDownToken, at, profile, [("app", app)]);

    public static string Timeout(DateTimeOffset at, string profile, string app) =>
        ObservationLine.Format(TimeoutToken, at, profile, [("app", app)]);

    /// <summary>
    /// <c>host=</c> is the app's id, <c>provider</c> or <c>elsewhere</c>, never a hostname; <c>shell=</c>,
    /// on an app host only, is the free text. At the app's callback path the line also says what the
    /// callback was answered with (<paramref name="answered"/>, #1195): <c>http=</c>, <c>problem=</c>,
    /// and a problem document's <c>title=</c> as the free text in <c>shell=</c>'s place, since a problem
    /// document is no shell.
    /// </summary>
    public static string Other(DateTimeOffset at, string profile, string app, string host, string path, Readiness ready, string? shell, Answered? answered = null) =>
        ObservationLine.Format(OtherToken, at, profile,
            [("app", app), ("host", host), ("path", path), .. answered is { } answer ? answer.Fields : [], .. ready.Fields],
            answered?.Title is { } title ? ("title", title) : shell is null ? null : ("shell", shell));

    public static string Marker(DateTimeOffset at, string profile, string app, string text) =>
        ObservationLine.Format("marker", at, profile, [("app", app)], ("text", text));

    /// <summary>A page fetch of <c>/bff/session</c>: its status and its problem's code, never its body.</summary>
    public static string Session(DateTimeOffset at, string profile, string app, int status, string? problem) =>
        ObservationLine.Format("session", at, profile, [("app", app), ("http", Http(status)), ("problem", problem ?? ObservationLine.Dash)]);

    /// <summary>A page fetch of an <c>/api/</c> path, GET only: its status and its problem's code, never its body.</summary>
    public static string Api(DateTimeOffset at, string profile, string app, string path, int status, string? problem) =>
        ObservationLine.Format("api", at, profile, [("app", app), ("path", path), ("http", Http(status)), ("problem", problem ?? ObservationLine.Dash)]);

    public static string ClearProvider(DateTimeOffset at, string profile, string app, int removed) =>
        ObservationLine.Format("clear-provider", at, profile, [("app", app), ("removed", Number(removed))]);

    /// <summary>One open profile: its app and its last observation's token and time, <c>-</c> before any.</summary>
    public static string Status(DateTimeOffset at, string profile, string app, string? last, DateTimeOffset? lastAt) =>
        ObservationLine.Format("status", at, profile, [("app", app), ("last", last ?? ObservationLine.Dash), ("at", lastAt is { } when ? ObservationLine.Time(when) : ObservationLine.Dash)]);

    /// <summary>A <c>status</c> with no profile open.</summary>
    public static string NoProfiles(DateTimeOffset at) =>
        ObservationLine.Format("status", at, ObservationLine.NoProfile, [("profiles", "0")]);

    public static string Closed(DateTimeOffset at, string profile, string app) =>
        ObservationLine.Format("closed", at, profile, [("app", app)]);

    public static string Unwatched(DateTimeOffset at, string profile, string app, int ticks) =>
        ObservationLine.Format("unwatched", at, profile, [("app", app), ("ticks", Number(ticks))]);

    /// <summary>The process listens and its stack has started.</summary>
    public static string Ready(DateTimeOffset at, string stack) =>
        ObservationLine.Format("ready", at, ObservationLine.NoProfile, [("stack", stack)]);

    /// <summary>The process stopped: <c>stop</c>, or <c>cancelled</c> by a signal.</summary>
    public static string Stopped(DateTimeOffset at, string reason) =>
        ObservationLine.Format("stopped", at, ObservationLine.NoProfile, [("reason", reason)]);

    /// <summary>A start refused: a live socket, or a Playwright debug switch that would print what the page holds.</summary>
    public static string Refused(DateTimeOffset at, string reason) =>
        ObservationLine.Format("refused", at, ObservationLine.NoProfile, [("reason", reason)]);

    /// <summary>A client call that found no process at the socket.</summary>
    public static string NotRunning(DateTimeOffset at, string profile) =>
        ObservationLine.Format("not-running", at, profile, []);

    /// <summary>
    /// A command that made no observation: its reason, or the failure's type. Only an AWS CLI failure (the
    /// password's SSM read) carries its message, as <c>text=</c>, so an <c>AccessDenied</c> reaches the
    /// stream verbatim (#1024 Q10); the message has the account masked already
    /// (<see cref="Stack.CommandFailedException"/>).
    /// </summary>
    public static string Failed(DateTimeOffset at, string profile, string command, string reason, string? text = null) =>
        ObservationLine.Format("failed", at, profile, [("command", command), ("reason", reason)], text is null ? null : ("text", text));

    private static string Http(int status) => Number(status);

    private static string Number(int number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// What the paired <c>/readyz</c> probe after a navigation answered (#1024 Q4): the revision and the
/// instance that answered that probe, never the task that served the page; dashes on a 503, a failed
/// probe, or a page off the app's host.
/// </summary>
public readonly record struct Readiness(string Revision, string Instance)
{
    public static readonly Readiness Unknown = new(ObservationLine.Dash, ObservationLine.Dash);

    public IEnumerable<(string Key, string Value)> Fields => [("revision", Revision), ("instance", Instance)];
}

/// <summary>
/// What a login callback's document was answered with (#1195): the response's status, and of a
/// problem document its <c>code</c> and its <c>title</c>, the title through the page text's scrub;
/// dashes for a document that could not be read, and for a body that is no problem document, as the
/// gateway's failure page for a refused callback is not (contracts/http-contract.md -> Errors).
/// </summary>
public readonly record struct Answered(int? Status, string? Problem, string? Title)
{
    public static readonly Answered Unknown = new(null, null, null);

    public IEnumerable<(string Key, string Value)> Fields =>
    [
        ("http", Status is { } status ? status.ToString(System.Globalization.CultureInfo.InvariantCulture) : ObservationLine.Dash),
        ("problem", Problem ?? ObservationLine.Dash),
    ];
}
