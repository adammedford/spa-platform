using Microsoft.Extensions.Logging;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Hosting.Tests;

/// <summary>
/// The asset endpoint over a store that answers a missing key with null or refuses the read
/// (<see cref="ObjectReadRefusedException"/>), as S3 refuses every missing key to the gateway's
/// GetObject-only role (P-03): change request contracts/changes/issue-381-object-read-refusal.md,
/// items 1, 3, 4 and 5, and the Hosting table of the proposal's Tests section (#381, revision 4).
///
/// Each row runs once per store answer, null or refusal, never "either", and once per method: HEAD
/// answers with the status and headers GET answers, and no body. The feature is
/// <c>Platform.Testing</c>'s settable one, standing in for <c>UsePlatformTelemetry</c>, so each row
/// reads what the route left on <see cref="IRequestTelemetryFeature.ReadRefused"/>.
/// </summary>
public sealed class AssetReadRefusalTests
{
    private const string Release = "2026-10-12-abc1234";

    private static readonly AppDefinition App1 = TestApps.Entry("app1", release: Release);

    private const string Navigation = "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8\r\n";

    private const string NoStore = "private, no-store";

    private static readonly string Prefix = $"apps/{App1.AppId}/releases/{App1.Release}/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public enum Answer
    {
        Null,
        Refused,
    }

    /// <summary>
    /// A release that holds one asset, <c>assets/app-3f2a.js</c>, and the shell only when
    /// <paramref name="withShell"/>. A key it does not hold reads as <paramref name="answer"/> says.
    /// </summary>
    private static async Task<(HostingHost Gateway, RecordingObjectStore Store)> StartAsync(Answer answer, bool withShell)
    {
        RecordingObjectStore store = new(answer == Answer.Refused ? MissingKeyAnswer.AmbiguousRefusal : MissingKeyAnswer.ConfirmedAbsence);
        await store.SeedAsync(Prefix + "assets/app-3f2a.js", "console.log(1);");
        if (withShell)
        {
            await store.SeedAsync(Prefix + "index.html", "<p>shell one</p>");
        }

        HostingHost gateway = await HostingHost.StartAsync(Snapshots.With(1, App1), Ct, store, telemetry: true);
        return (gateway, store);
    }

    /// <summary>
    /// Any key but the shell, absent or refused, is 404 <c>not_found</c> and never cached (item 4).
    /// <c>/Index.html</c> and <c>/assets/index.html</c> are the controls: the shell is the release's
    /// <c>index.html</c> compared ordinally, so these are ordinary assets.
    ///
    /// Mutation: leave the 404's <c>Cache-Control</c> unset, as it was, and every row fails on the
    /// header (12 of 12, with the next test's two: 14 of 223); write <c>ReadRefused</c> on a null
    /// answer as well, and the six null rows fail, with the eight null rows of the shell test (14 of
    /// 223).
    /// </summary>
    [Theory]
    [InlineData("GET", "/assets/missing-9c9c.js", Answer.Null)]
    [InlineData("GET", "/assets/missing-9c9c.js", Answer.Refused)]
    [InlineData("GET", "/Index.html", Answer.Null)]
    [InlineData("GET", "/Index.html", Answer.Refused)]
    [InlineData("GET", "/assets/index.html", Answer.Null)]
    [InlineData("GET", "/assets/index.html", Answer.Refused)]
    [InlineData("HEAD", "/assets/missing-9c9c.js", Answer.Null)]
    [InlineData("HEAD", "/assets/missing-9c9c.js", Answer.Refused)]
    [InlineData("HEAD", "/Index.html", Answer.Null)]
    [InlineData("HEAD", "/Index.html", Answer.Refused)]
    [InlineData("HEAD", "/assets/index.html", Answer.Null)]
    [InlineData("HEAD", "/assets/index.html", Answer.Refused)]
    public async Task An_asset_absent_or_refused_is_404_not_found_with_no_store(string method, string path, Answer answer)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync(answer, withShell: true);
        await using HostingHost _ = gateway;

        RawResponse response = await gateway.SendAsync(method, App1.Host, path, Ct, Navigation);

        AssertNotFound(response, method);
        Assert.Equal([Prefix + path.TrimStart('/')], store.KeysRead);
        Assert.Equal(answer == Answer.Refused ? "asset" : null, gateway.LastTelemetry?.ReadRefused);
    }

    /// <summary>
    /// An asset that is there and the role may not read: S3's same 403, so the same 404. The release
    /// holds the key; the store refuses it.
    ///
    /// Mutation: answer a refusal by rethrowing it, the behaviour before item 4, and both rows fail on
    /// the status (500), with the six refused rows above (8 of 223).
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task An_asset_that_exists_but_is_refused_is_404_not_found_with_no_store(string method)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync(Answer.Null, withShell: true);
        await using HostingHost _ = gateway;
        store.Refuse(Prefix + "assets/app-3f2a.js");

        RawResponse response = await gateway.SendAsync(method, App1.Host, "/assets/app-3f2a.js", Ct, "Accept: */*\r\n");

        AssertNotFound(response, method);
        Assert.Equal("asset", gateway.LastTelemetry?.ReadRefused);
    }

    /// <summary>
    /// The shell the store cannot give: the release's <c>index.html</c>, reached directly, at <c>/</c>,
    /// and by the fallback for an extensionless path that accepts HTML, including one under
    /// <c>/assets/</c>. Absent or refused, it answers 500 <c>shell_unavailable</c>, never cached
    /// (items 3 and 4): a 404 would read as an ordinary missing file, and the same absent shell must
    /// not answer 404 or 500 depending on the role's permissions. Only a refusal is marked, as
    /// <c>shell</c>. Each writes one Error line naming the app, the release and the store's answer,
    /// and no line of the platform's own carries the client's path: the fallback rows' paths carry a
    /// canary a line that logged the path would contain.
    ///
    /// Mutations, each 16, 8 or fewer of 223: leave the 500's <c>Cache-Control</c> unset, and every
    /// row fails on the header (16); answer a null shell 404 <c>not_found</c>, as before item 3, and
    /// the eight null rows fail (8); rethrow a refused shell, as before item 3, and the eight refused
    /// rows fail on the header (8); drop the Error line, and every row fails (16); log the client's
    /// path on a line of its own, and the eight canary rows fail (8); log every answer as
    /// <c>refused</c>, and the eight null rows fail (8); classify the shell by the request path
    /// (<c>/</c> or <c>/index.html</c>) rather than by the key read, and the eight canary rows fail, with
    /// four <c>/Index.html</c> rows of the asset test, since <c>PathString</c> compares without case,
    /// and one row of <c>AssetTests.F_06_the_fallback_matrix</c> (13).
    /// </summary>
    [Theory]
    [InlineData("GET", "/", Answer.Null)]
    [InlineData("GET", "/index.html", Answer.Null)]
    [InlineData("GET", "/canary-7f3e/42", Answer.Null)]
    [InlineData("GET", "/assets/canary-7f3e", Answer.Null)]
    [InlineData("HEAD", "/", Answer.Null)]
    [InlineData("HEAD", "/index.html", Answer.Null)]
    [InlineData("HEAD", "/canary-7f3e/42", Answer.Null)]
    [InlineData("HEAD", "/assets/canary-7f3e", Answer.Null)]
    [InlineData("GET", "/", Answer.Refused)]
    [InlineData("GET", "/index.html", Answer.Refused)]
    [InlineData("GET", "/canary-7f3e/42", Answer.Refused)]
    [InlineData("GET", "/assets/canary-7f3e", Answer.Refused)]
    [InlineData("HEAD", "/", Answer.Refused)]
    [InlineData("HEAD", "/index.html", Answer.Refused)]
    [InlineData("HEAD", "/canary-7f3e/42", Answer.Refused)]
    [InlineData("HEAD", "/assets/canary-7f3e", Answer.Refused)]
    public async Task An_unreadable_shell_is_500_shell_unavailable_with_no_store(string method, string path, Answer answer)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync(answer, withShell: false);
        await using HostingHost _ = gateway;

        RawResponse response = await gateway.SendAsync(method, App1.Host, path, Ct, Navigation);

        Assert.Equal(500, response.StatusCode);
        Assert.Equal(NoStore, response.Header("cache-control"));
        Assert.Equal("application/problem+json", response.Header("content-type"));
        Assert.Equal(method == "HEAD" ? "" : "{\"status\":500,\"code\":\"shell_unavailable\"}", response.BodyText);
        Assert.Equal([Prefix + "index.html"], store.KeysRead);
        Assert.Equal(answer == Answer.Refused ? "shell" : null, gateway.LastTelemetry?.ReadRefused);

        CapturedLine error = Assert.Single(gateway.Log.Lines, line => line.Level >= LogLevel.Error);
        Assert.Equal("Platform.Hosting.Assets", error.Category);
        Assert.Contains(new KeyValuePair<string, object?>("AppId", App1.AppId), error.State);
        Assert.Contains(new KeyValuePair<string, object?>("Release", Release), error.State);
        Assert.Contains(new KeyValuePair<string, object?>("Answer", answer == Answer.Refused ? "refused" : "null"), error.State);
        Assert.DoesNotContain(gateway.Log.Lines, line =>
            line.Category.StartsWith("Platform.", StringComparison.Ordinal)
            && line.Text.Contains("canary-7f3e", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A reserved path is refused before any read (<c>path.reserved</c>), and its 404 is not cached
    /// either. Nothing was read, so nothing was refused, even over a store that refuses every missing
    /// key.
    ///
    /// Mutation: set <c>Cache-Control</c> only on the 404s that follow a read, and both rows fail on the
    /// header.
    /// </summary>
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task A_reserved_path_404_is_never_cached_and_marks_no_refusal(string method)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync(Answer.Refused, withShell: true);
        await using HostingHost _ = gateway;

        RawResponse response = await gateway.SendAsync(method, App1.Host, "/BFF/x", Ct, Navigation);

        AssertNotFound(response, method);
        Assert.Equal("path.reserved", gateway.LastTelemetry?.Rule);
        Assert.Null(gateway.LastTelemetry?.ReadRefused);
        Assert.Empty(store.KeysRead);
    }

    /// <summary>
    /// A failure that is neither absence nor a refusal is not a 404: the S3 store lets another 403
    /// code, <c>NoSuchBucket</c>, a 5xx, a transport failure and cancellation propagate, and the route
    /// does not turn any of them into "not found". These stand for them here, where the AWS SDK is not
    /// referenced: whatever the store throws that is not <see cref="ObjectReadRefusedException"/>.
    ///
    /// Mutation: catch every exception of the read as a refusal, and every row fails on the 404 and
    /// on <c>asset</c> (6 of 6).
    /// </summary>
    [Theory]
    [InlineData("GET", "transport")]
    [InlineData("GET", "configuration")]
    [InlineData("GET", "cancelled")]
    [InlineData("HEAD", "transport")]
    [InlineData("HEAD", "configuration")]
    [InlineData("HEAD", "cancelled")]
    public async Task Any_other_store_failure_is_not_a_404(string method, string failure)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync(Answer.Refused, withShell: true);
        await using HostingHost _ = gateway;
        store.FailWith = failure switch
        {
            "transport" => new IOException("The connection was reset."),
            "configuration" => new InvalidOperationException("The bucket does not exist."),
            _ => new OperationCanceledException(),
        };

        RawResponse response = await gateway.SendAsync(method, App1.Host, "/assets/missing-9c9c.js", Ct, "Accept: */*\r\n");

        Assert.NotEqual(404, response.StatusCode);
        Assert.NotEqual(200, response.StatusCode);
        Assert.Null(gateway.LastTelemetry?.ReadRefused);
    }

    private static void AssertNotFound(RawResponse response, string method)
    {
        Assert.Equal(404, response.StatusCode);
        Assert.Equal(NoStore, response.Header("cache-control"));
        Assert.Equal("application/problem+json", response.Header("content-type"));
        if (method == "HEAD")
        {
            Assert.Equal("", response.BodyText);
        }
        else
        {
            Assert.Contains("\"code\":\"not_found\"", response.BodyText, StringComparison.Ordinal);
        }
    }
}
