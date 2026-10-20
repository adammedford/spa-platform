using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Hosting.Tests;

/// <summary>
/// The asset endpoint, <c>MapPlatformAssets</c> (plan row 2.4; contracts/s3-layout.md;
/// contracts/http-contract.md), behind the real <c>UsePlatformHealth</c> and
/// <c>UsePlatformHostResolution</c> and over a recording in-memory store.
/// </summary>
public sealed class AssetTests
{
    private const string Release1 = "2026-10-12-abc1234";
    private const string Release2 = "2026-10-13-def5678";

    private static readonly AppDefinition App1 = TestApps.Entry("app1", release: Release1);
    private static readonly AppDefinition App2 = TestApps.Entry("app2", release: Release2);

    private const string Leak = "LEAKED:";

    private const string Navigation = "Accept: text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8\r\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Prefix(AppDefinition app) => $"apps/{app.AppId}/releases/{app.Release}/";

    private static async Task<(HostingHost Gateway, RecordingObjectStore Store)> StartAsync(
        RecordingLoggerProvider? logs = null, bool telemetry = false)
    {
        RecordingObjectStore store = new();
        await store.SeedAsync(Prefix(App1) + "index.html", "<p>shell one</p>");
        await store.SeedAsync(Prefix(App2) + "index.html", "<p>shell two</p>");
        await store.SeedAsync(Prefix(App1) + "assets/app-3f2a.js", "console.log(1);");
        await store.SeedAsync(Prefix(App1) + "assets/app-3f2a.css", "p{}");
        await store.SeedAsync(Prefix(App1) + "favicon.ico", "ico");
        await store.SeedAsync(Prefix(App1) + "robots.txt", "User-agent: *");
        await store.SeedAsync(Prefix(App1) + "data.weird", "?");
        await store.SeedAsync(Prefix(App1) + "config", "{\"extensionless\":true}");

        // Objects outside app1's release prefix, each with a body a test can recognise if it leaked.
        await store.SeedAsync("registry/registry.json", Leak + "registry");
        await store.SeedAsync("apps/app1/releases/2026-01-01-old0000/index.html", Leak + "old release");
        await store.SeedAsync("apps/app1/secret.txt", Leak + "app1 root");
        await store.SeedAsync(Prefix(App2) + "assets/app-3f2a.js", Leak + "app2 asset");

        HostingHost gateway = await HostingHost.StartAsync(Snapshots.With(1, App1, App2), Ct, store, logs, telemetry);
        return (gateway, store);
    }

    /// <summary>F-01, the hosting half (docs/plan/04-tests.md): two hosts, two different shells.</summary>
    [Fact]
    public async Task F_01_two_hosts_return_two_different_shells()
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync();
        await using HostingHost _ = gateway;

        RawResponse one = await gateway.GetAsync(App1.Host, "/", Ct);
        RawResponse two = await gateway.GetAsync(App2.Host, "/", Ct);

        Assert.Equal(200, one.Status);
        Assert.Equal("<p>shell one</p>", one.Body);
        Assert.Equal(200, two.Status);
        Assert.Equal("<p>shell two</p>", two.Body);
        Assert.Equal([Prefix(App1) + "index.html", Prefix(App2) + "index.html"], store.KeysRead);
    }

    /// <summary>
    /// The key comes from the app pinned for this request: a registry change to <c>release</c> is a
    /// release switch with no deployment, and back again is the rollback (F-06; ADR-0009).
    /// </summary>
    [Fact]
    public async Task F_06_a_release_switch_and_back_changes_the_shell_with_no_restart()
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync();
        await using HostingHost _ = gateway;
        AppDefinition next = App1 with { Release = "2026-10-14-0a0a0a0" };
        await store.SeedAsync(Prefix(next) + "index.html", "<p>shell one, next release</p>");

        Assert.Equal("<p>shell one</p>", (await gateway.GetAsync(App1.Host, "/", Ct)).Body);

        gateway.Registry.Swap(Snapshots.With(2, next, App2));
        Assert.Equal("<p>shell one, next release</p>", (await gateway.GetAsync(App1.Host, "/", Ct)).Body);

        gateway.Registry.Swap(Snapshots.With(3, App1, App2));
        Assert.Equal("<p>shell one</p>", (await gateway.GetAsync(App1.Host, "/", Ct)).Body);
    }

    /// <summary>
    /// F-06, the fallback matrix (docs/plan/04-tests.md; contracts/http-contract.md, Endpoints):
    /// extensionless paths that accept HTML return <c>index.html</c>, an existing file is itself, and a
    /// missing asset is 404, never the shell.
    /// </summary>
    [Theory]
    [InlineData("/", Navigation, 200, "<p>shell one</p>")]
    [InlineData("/orders", Navigation, 200, "<p>shell one</p>")]
    [InlineData("/orders/42/edit", Navigation, 200, "<p>shell one</p>")]
    [InlineData("/orders/", Navigation, 200, "<p>shell one</p>")]
    [InlineData("/assets/vendor", Navigation, 200, "<p>shell one</p>")]
    [InlineData("/orders", "", 200, "<p>shell one</p>")]
    [InlineData("/orders", "Accept: */*\r\n", 200, "<p>shell one</p>")]
    [InlineData("/orders", "Accept: text/*\r\n", 200, "<p>shell one</p>")]
    [InlineData("/index.html", Navigation, 200, "<p>shell one</p>")]
    [InlineData("/assets/app-3f2a.js", "Accept: */*\r\n", 200, "console.log(1);")]
    [InlineData("/robots.txt", Navigation, 200, "User-agent: *")]
    [InlineData("/config", "Accept: application/json\r\n", 200, "{\"extensionless\":true}")]
    [InlineData("/orders", "Accept: application/json\r\n", 404, null)]
    [InlineData("/orders", "Accept: text/html;q=0, application/json\r\n", 404, null)]
    [InlineData("/orders", "Accept: text/html;q=0, */*;q=1\r\n", 404, null)]
    [InlineData("/orders", "Accept: text/html;q=0, text/*;q=1\r\n", 404, null)]
    [InlineData("/orders", "Accept: text/*;q=0, */*;q=1\r\n", 404, null)]
    [InlineData("/orders", "Accept: */*;q=1, text/html;q=0\r\n", 404, null)]
    [InlineData("/orders", "Accept: text/html;q=1, */*;q=0\r\n", 200, "<p>shell one</p>")]
    [InlineData("/orders", "Accept: text/html;q=0.5, text/*;q=0\r\n", 200, "<p>shell one</p>")]
    [InlineData("/assets/missing-9c9c.js", "Accept: */*\r\n", 404, null)]
    [InlineData("/assets/missing-9c9c.js", Navigation, 404, null)]
    [InlineData("/missing.html", Navigation, 404, null)]
    [InlineData("/Index.html", Navigation, 404, null)]
    public async Task F_06_the_fallback_matrix(string path, string accept, int status, string? body)
    {
        (HostingHost gateway, RecordingObjectStore _) = await StartAsync();
        await using HostingHost __ = gateway;

        RawResponse response = await gateway.GetAsync(App1.Host, path, Ct, accept);

        Assert.Equal(status, response.Status);
        if (body is null)
        {
            AssertProblem(response, "not_found");
        }
        else
        {
            Assert.Equal(body, response.Body);
        }
    }

    /// <summary>
    /// Content type by extension, never the stored one (contracts/s3-layout.md), and an unknown
    /// extension as <c>application/octet-stream</c>. The in-memory store keeps no type at all, so a
    /// type that reaches the response came from the extension.
    /// </summary>
    [Theory]
    [InlineData("/", "text/html")]
    [InlineData("/orders", "text/html")]
    [InlineData("/assets/app-3f2a.js", "text/javascript")]
    [InlineData("/assets/app-3f2a.css", "text/css")]
    [InlineData("/favicon.ico", "image/x-icon")]
    [InlineData("/robots.txt", "text/plain")]
    [InlineData("/data.weird", "application/octet-stream")]
    public async Task Content_type_comes_from_the_extension_with_nosniff(string path, string type)
    {
        (HostingHost gateway, RecordingObjectStore _) = await StartAsync();
        await using HostingHost __ = gateway;

        RawResponse response = await gateway.GetAsync(App1.Host, path, Ct, Navigation);

        Assert.Equal(200, response.Status);
        Assert.Equal(type, response.Headers["content-type"]);
        Assert.Equal("nosniff", response.Headers["x-content-type-options"]);
    }

    /// <summary>
    /// The cache policy follows the key served (contracts/s3-layout.md; ADR-0009): a year and
    /// <c>immutable</c> under <c>assets/</c>, <c>no-cache</c> with an ETag elsewhere, and <c>private</c>
    /// on both (ADR-0008). <c>/assets/vendor</c> is the shell by fallback, so it is not immutable.
    /// </summary>
    [Theory]
    [InlineData("/assets/app-3f2a.js", "private, max-age=31536000, immutable")]
    [InlineData("/", "private, no-cache")]
    [InlineData("/index.html", "private, no-cache")]
    [InlineData("/robots.txt", "private, no-cache")]
    [InlineData("/assets/vendor", "private, no-cache")]
    public async Task The_cache_policy_follows_the_key_served(string path, string cacheControl)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync();
        await using HostingHost _ = gateway;

        RawResponse response = await gateway.GetAsync(App1.Host, path, Ct, Navigation);

        Assert.Equal(200, response.Status);
        Assert.Equal(cacheControl, response.Headers["cache-control"]);
        Assert.Equal(await store.ETagOfAsync(store.KeysRead[^1]), response.Headers["etag"]);
    }

    /// <summary>
    /// Conditional requests pass through to the store: the browser's <c>If-None-Match</c> reaches S3 as
    /// sent, and a match is 304 with no body and the same ETag and cache policy (RFC 9110, 15.4.5).
    /// </summary>
    [Fact]
    public async Task If_None_Match_is_passed_through_and_a_match_is_304()
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync();
        await using HostingHost _ = gateway;
        string etag = await store.ETagOfAsync(Prefix(App1) + "index.html");

        RawResponse matched = await gateway.GetAsync(App1.Host, "/orders", Ct, Navigation + $"If-None-Match: {etag}\r\n");

        Assert.Equal(304, matched.Status);
        Assert.Equal("", matched.Body);
        Assert.Equal(etag, matched.Headers["etag"]);
        Assert.Equal("private, no-cache", matched.Headers["cache-control"]);
        Assert.Equal(etag, store.IfNoneMatchSent[^1]);

        RawResponse stale = await gateway.GetAsync(App1.Host, "/orders", Ct, Navigation + "If-None-Match: \"stale\"\r\n");

        Assert.Equal(200, stale.Status);
        Assert.Equal("<p>shell one</p>", stale.Body);
        Assert.Equal("\"stale\"", store.IfNoneMatchSent[^1]);

        await gateway.GetAsync(App1.Host, "/orders", Ct, Navigation);
        Assert.Null(store.IfNoneMatchSent[^1]);
    }

    /// <summary>HEAD answers as GET does, with the length, and no body (RFC 9110, section 9.3.2).</summary>
    [Fact]
    public async Task HEAD_has_the_headers_of_GET_and_no_body()
    {
        (HostingHost gateway, RecordingObjectStore _) = await StartAsync();
        await using HostingHost __ = gateway;

        RawResponse response = await gateway.SendAsync("HEAD", App1.Host, "/assets/app-3f2a.js", Ct);

        Assert.Equal(200, response.Status);
        Assert.Equal("15", response.Headers["content-length"]);
        Assert.Equal("text/javascript", response.Headers["content-type"]);
        Assert.Equal("", response.Body);
    }

    /// <summary>
    /// Only GET and HEAD reach an asset (contracts/http-contract.md, Endpoints): anything else is the
    /// contract's 405, and the store is not read.
    /// </summary>
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task Other_methods_get_405_and_read_nothing(string method)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync();
        await using HostingHost _ = gateway;

        RawResponse response = await gateway.SendAsync(method, App1.Host, "/index.html", Ct, "Content-Length: 0\r\n");

        Assert.Equal(405, response.Status);
        Assert.Equal("GET, HEAD", response.Headers["allow"]);
        AssertProblem(response, "method_not_allowed");
        Assert.Empty(store.KeysRead);
    }

    /// <summary>
    /// The backup 405 is a rejection, and it ends under the id the rule-id table gives its case,
    /// <c>gate.method</c> (contracts/request-gate.md, Rule ids), so that it reaches the request line
    /// and <c>security.rejections</c> like every other rejection the application answers
    /// (contracts/telemetry.md -> "How <c>rule</c> and <c>session_ref</c> reach the request line").
    /// <c>UsePlatformRequestGate</c> refuses the method first in the composed gateway, which therefore
    /// never reaches this answer: this project's tests are the only place it shows. The feature is
    /// <c>Platform.Testing</c>'s settable one, standing in for <c>UsePlatformTelemetry</c>.
    ///
    /// Mutation: revert the endpoint's 405 to the plain body writer (set <c>Allow</c> and call
    /// <c>Problems.WriteAsync</c> with <c>method_not_allowed</c>), and every row fails on the rule
    /// while <see cref="Other_methods_get_405_and_read_nothing"/> still passes.
    /// </summary>
    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task The_backup_405_sets_rule_gate_method_on_the_telemetry_feature(string method)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync(telemetry: true);
        await using HostingHost _ = gateway;

        RawResponse response = await gateway.SendAsync(method, App1.Host, "/index.html", Ct, "Content-Length: 0\r\n");

        Assert.Equal(405, response.Status);
        Assert.Equal("GET, HEAD", response.Headers["allow"]);
        AssertProblem(response, "method_not_allowed");
        Assert.Equal("gate.method", gateway.LastTelemetry?.Rule);
        Assert.Empty(store.KeysRead);
    }

    /// <summary>
    /// The reserved paths (contracts/http-contract.md: an SPA cannot serve content under them) are 404
    /// and never read, even when the release holds a file there. They match without regard to case,
    /// as routing and the request gate match them.
    /// </summary>
    [Theory]
    [InlineData("/bff/anything.js")]
    [InlineData("/bff")]
    [InlineData("/BFF/session")]
    [InlineData("/api/orders.json")]
    [InlineData("/Api/x")]
    [InlineData("/signin-oidc")]
    [InlineData("/signout-callback-oidc")]
    [InlineData("/signout-oidc")]
    [InlineData("/healthz/x.txt")]
    [InlineData("/HEALTHZ")]
    [InlineData("/readyz/x")]
    public async Task A_reserved_path_is_404_and_reads_nothing(string path)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync();
        await using HostingHost _ = gateway;
        await store.SeedAsync(Prefix(App1) + path.TrimStart('/'), Leak + "reserved");

        RawResponse response = await gateway.GetAsync(App1.Host, path, Ct, Navigation);

        Assert.Equal(404, response.Status);
        AssertProblem(response, "not_found");
        Assert.Empty(store.KeysRead);
    }

    /// <summary>
    /// The control for the reserved paths: a name that only begins like one is the SPA's own.
    /// </summary>
    [Theory]
    [InlineData("/apidocs.txt")]
    [InlineData("/bffx.txt")]
    public async Task A_path_that_only_begins_like_a_reserved_one_is_served(string path)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync();
        await using HostingHost _ = gateway;
        await store.SeedAsync(Prefix(App1) + path.TrimStart('/'), "mine");

        RawResponse response = await gateway.GetAsync(App1.Host, path, Ct, Navigation);

        Assert.Equal(200, response.Status);
        Assert.Equal("mine", response.Body);
    }

    /// <summary>
    /// A refusal is logged under its rule id (docs/plan/02-contracts.md, Request gate: "Tests assert on
    /// both the code and the rule id"), and the line carries the rule id only, never the client's path.
    /// The canary in each path is what a line that logged the path would contain. Only the platform's
    /// own categories are held to that: ASP.NET Core's request-start line carries the URL at
    /// Information, and which framework lines the gateway keeps is workstream 6's to configure
    /// (Platform.Telemetry).
    /// </summary>
    [Theory]
    [InlineData("/canary-7f3e%2Fsecret.js", 400, "invalid_request", "path.invalid")]
    [InlineData("/bff/canary-7f3e.js", 404, "not_found", "path.reserved")]
    public async Task A_refusal_logs_its_rule_id_and_not_the_path(string path, int status, string code, string rule)
    {
        RecordingLoggerProvider logs = new();
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync(logs);
        await using HostingHost _ = gateway;

        RawResponse response = await gateway.GetAsync(App1.Host, path, Ct, Navigation);

        Assert.Equal(status, response.Status);
        AssertProblem(response, code);
        Assert.Empty(store.KeysRead);
        Assert.Contains(logs.Lines, line =>
            line.Category == "Platform.Hosting.Assets"
            && line.Level == Microsoft.Extensions.Logging.LogLevel.Warning
            && line.Message == $"Asset request refused under {rule}");
        Assert.DoesNotContain(logs.Lines, line =>
            line.Category.StartsWith("Platform.", StringComparison.Ordinal)
            && line.Message.Contains("canary-7f3e", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The same two refusals also leave their rule id on <see cref="IRequestTelemetryFeature"/>,
    /// for the one request line <c>UsePlatformTelemetry</c> writes and for
    /// <c>security.rejections</c> counted from the same field (contracts/telemetry.md -> "How
    /// <c>rule</c> and <c>session_ref</c> reach the request line"). <c>UsePlatformTelemetry</c> is
    /// workstream 6's and is not in this pipeline, so the feature is <c>Platform.Testing</c>'s
    /// settable one, installed by the test host in its place.
    ///
    /// The endpoint's own Warning line stays: it names which check refused the request, which the
    /// request line cannot, and the test above asserts on it. The row for each id is the same path
    /// that test refuses under, so a stage that set one id for both, or none, fails here.
    /// </summary>
    [Theory]
    [InlineData("/canary-7f3e%2Fsecret.js", 400, "path.invalid")]
    [InlineData("/bff/canary-7f3e.js", 404, "path.reserved")]
    public async Task A_refusal_sets_its_rule_id_on_the_telemetry_feature(string path, int status, string rule)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync(telemetry: true);
        await using HostingHost _ = gateway;

        RawResponse response = await gateway.GetAsync(App1.Host, path, Ct, Navigation);

        Assert.Equal(status, response.Status);
        Assert.Equal(rule, gateway.LastTelemetry?.Rule);
        Assert.Empty(store.KeysRead);

        // The control: a path this endpoint serves is not a rejection and leaves the field null, so a
        // stage that wrote a rule id on every request would not pass.
        Assert.Equal(200, (await gateway.GetAsync(App1.Host, "/index.html", Ct)).Status);
        Assert.NotNull(gateway.LastTelemetry);
        Assert.Null(gateway.LastTelemetry.Rule);
    }

    /// <summary>
    /// N-13 (docs/plan/04-tests.md): dot segments, encoded slashes, backslashes, doubled slashes and
    /// null bytes give 400 or 404, and no object outside the app's release prefix is read. The
    /// request line goes over the socket as written, so these reach the server as an attacker sends
    /// them. Which layer refuses (Kestrel or the endpoint) is not the claim; the outcome is.
    /// </summary>
    [Theory]
    [InlineData("/../../../registry/registry.json")]
    [InlineData("/assets/../../2026-01-01-old0000/index.html")]
    [InlineData("/./index.html")]
    [InlineData("/%2e%2e/%2e%2e/secret.txt")]
    [InlineData("/%2E%2E/%2E%2E/%2E%2E/registry/registry.json")]
    [InlineData("/.%2e/.%2e/secret.txt")]
    [InlineData("/..%2f..%2fsecret.txt")]
    [InlineData("/%2e%2e%2f%2e%2e%2fsecret.txt")]
    [InlineData("/assets%2fapp-3f2a.js")]
    [InlineData("/assets%2Fapp-3f2a.js")]
    [InlineData("/%252e%252e/%252e%252e/secret.txt")]
    [InlineData("/..\\..\\secret.txt")]
    [InlineData("/assets\\app-3f2a.js")]
    [InlineData("/%5c..%5c..%5csecret.txt")]
    [InlineData("//registry/registry.json")]
    [InlineData("/assets//app-3f2a.js")]
    [InlineData("///apps/app2/releases/2026-10-13-def5678/assets/app-3f2a.js")]
    [InlineData("/index.html%00")]
    [InlineData("/index.html%00.js")]
    [InlineData("/index.html\0")]
    [InlineData("/..;/..;/secret.txt")]
    public async Task N_13_a_path_that_would_leave_the_release_prefix_reads_nothing_outside_it(string path)
    {
        (HostingHost gateway, RecordingObjectStore store) = await StartAsync();
        await using HostingHost _ = gateway;

        RawResponse response = await gateway.GetAsync(App1.Host, path, Ct, Navigation);

        Assert.True(response.Status is 400 or 404, $"{path} answered {response.Status}.");
        Assert.DoesNotContain(Leak, response.Body, StringComparison.Ordinal);
        Assert.All(store.KeysRead, key => AssertInsideReleasePrefix(key, App1));
    }

    /// <summary>
    /// A key is inside the prefix only if it starts with it and nothing after could climb out of it.
    /// S3 itself does not resolve <c>..</c>, so a textual prefix check alone would pass whether or not
    /// the path was validated; this one also fails a key that a path-resolving store, a proxy or a
    /// future cache keyed on paths would read somewhere else.
    /// </summary>
    private static void AssertInsideReleasePrefix(string key, AppDefinition app)
    {
        Assert.StartsWith(Prefix(app), key, StringComparison.Ordinal);
        string rest = key[Prefix(app).Length..];
        Assert.DoesNotContain('\\', rest);
        Assert.DoesNotContain('%', rest);
        Assert.DoesNotContain(rest, char.IsControl);
        Assert.All(rest.Split('/').SkipLast(1), segment => Assert.NotEqual("", segment));
        Assert.All(rest.Split('/'), segment => Assert.DoesNotMatch(@"^\.\.?(;.*)?$", segment));
    }

    /// <summary>
    /// N-13's paths decoded and handed straight to the validator, so the endpoint's own rule is shown
    /// to refuse each of them, not only the server in front of it. Kestrel collapses dot segments and
    /// refuses a NUL before the endpoint runs, which would otherwise leave those rows vacuous here.
    /// </summary>
    [Theory]
    [InlineData("/../registry/registry.json")]
    [InlineData("/assets/../index.html")]
    [InlineData("/./index.html")]
    [InlineData("/..")]
    [InlineData("/.")]
    [InlineData("/assets%2Fapp.js")]
    [InlineData("/%2e%2e/secret.txt")]
    [InlineData("/..\\secret.txt")]
    [InlineData("/assets\\app.js")]
    [InlineData("//registry/registry.json")]
    [InlineData("/assets//app.js")]
    [InlineData("/index.html\0")]
    [InlineData("/index.html\u0001")]
    [InlineData("")]
    [InlineData("index.html")]
    public void N_13_the_validator_refuses_each_shape_itself(string path)
    {
        Assert.Null(AssetPath.Validate(path));
    }

    /// <summary>
    /// N-13's paths as sent, before Kestrel decodes them: the raw check refuses each shape, and passes
    /// an ordinary escape the decoded check then judges.
    /// </summary>
    [Theory]
    [InlineData("/./index.html", false)]
    [InlineData("/assets/../index.html", false)]
    [InlineData("/..;/secret.txt", false)]
    [InlineData("/.%2e/secret.txt", false)]
    [InlineData("/%2E%2E/secret.txt", false)]
    [InlineData("/assets%2Fapp.js", false)]
    [InlineData("/%5c..%5csecret.txt", false)]
    [InlineData("/assets\\app.js", false)]
    [InlineData("/index.html%00", false)]
    [InlineData("//registry/registry.json", false)]
    [InlineData("/assets//app.js", false)]
    [InlineData("http://app1.spa-poc.test/index.html", false)]
    [InlineData("*", false)]
    [InlineData("/", true)]
    [InlineData("/orders/", true)]
    [InlineData("/my%20file.png?v=1", true)]
    [InlineData("/index.html?next=/../x", true)]
    public void N_13_the_raw_target_check(string raw, bool normal)
    {
        Assert.Equal(normal, AssetPath.RawIsNormal(raw));
    }

    [Theory]
    [InlineData("/", "")]
    [InlineData("/index.html", "index.html")]
    [InlineData("/assets/app-3f2a.js", "assets/app-3f2a.js")]
    [InlineData("/orders/", "orders/")]
    [InlineData("/.well-known/x.json", ".well-known/x.json")]
    [InlineData("/a..b.js", "a..b.js")]
    public void The_validator_passes_an_ordinary_path_unchanged_but_for_the_leading_slash(string path, string relative)
    {
        Assert.Equal(relative, AssetPath.Validate(path));
    }

    private static void AssertProblem(RawResponse response, string code)
    {
        Assert.Equal("application/problem+json", response.Headers["content-type"]);
        Assert.Contains($"\"code\":\"{code}\"", response.Body, StringComparison.Ordinal);
    }
}
