using System.Collections.Immutable;
using System.Net;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Security.Tests;

/// <summary>
/// Stage 5, the security headers: <c>contracts/http-contract.md</c> -> Response headers and "The
/// baseline content security policy". Task 4.5; F-06's per-app headers and N-24.
///
/// Every expected policy below is written out by hand from the contract, not built the way the code
/// builds it, so a builder that drifted from the contract cannot agree with itself here.
/// </summary>
public sealed class SecurityHeadersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AppDefinition App1 = TestApps.Entry("app1");

    /// <summary>The contract's baseline, with <see cref="HeadersHost.BrowserOrigin"/> in <c>form-action</c>.</summary>
    private const string Baseline =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; "
        + "connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none'; "
        + "form-action 'self' https://login.idp.test";

    private static string Single(HttpResponseMessage response, string name) =>
        Assert.Single(response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values : []);

    // ---- F-06: the app's own policy ----

    [Fact]
    public async Task F06_an_app_with_no_additions_gets_the_baseline_with_the_provider_origin_in_form_action()
    {
        await using HeadersHost host = await HeadersHost.StartAsync(Ct, [App1]);

        using HttpResponseMessage response = await host.SendAsync(App1, "GET", "/", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Baseline, Single(response, "Content-Security-Policy"));
    }

    [Fact]
    public async Task F06_two_apps_on_one_gateway_get_their_own_additions_after_the_baseline_sources()
    {
        // App 2 adds what contracts/registry.example.json's entry adds, data: in img-src, and an origin
        // in connect-src and form-action. App 1 adds nothing, and is asked second so that a policy
        // cached for the wrong app would show.
        AppDefinition app2 = TestApps.Entry("app2", csp: Csp(
            ("img-src", ["data:"]),
            ("connect-src", ["https://telemetry.example.test", "wss://live.example.test"]),
            ("form-action", ["https://pay.example.test"])));
        await using HeadersHost host = await HeadersHost.StartAsync(Ct, [App1, app2]);

        using HttpResponseMessage second = await host.SendAsync(app2, "GET", "/", Ct);
        using HttpResponseMessage first = await host.SendAsync(App1, "GET", "/", Ct);

        Assert.Equal(
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; "
            + "connect-src 'self' https://telemetry.example.test wss://live.example.test; frame-ancestors 'none'; "
            + "base-uri 'self'; object-src 'none'; form-action 'self' https://login.idp.test https://pay.example.test",
            Single(second, "Content-Security-Policy"));
        Assert.Equal(Baseline, Single(first, "Content-Security-Policy"));
    }

    [Fact]
    public async Task F06_an_addition_to_a_none_directive_replaces_none_rather_than_sitting_beside_it()
    {
        // The schema lets an entry extend frame-ancestors and object-src, whose baseline is 'none'.
        // 'none' beside a source is ignored by the browser (CSP3, "Does url match source list"), so the
        // header says what the browser would do: the entry's sources alone.
        AppDefinition portal = TestApps.Entry("portal", csp: Csp(
            ("frame-ancestors", ["https://intranet.example.test"]),
            ("object-src", ["https://media.example.test"])));
        await using HeadersHost host = await HeadersHost.StartAsync(Ct, [portal]);

        using HttpResponseMessage response = await host.SendAsync(portal, "GET", "/", Ct);

        Assert.Equal(
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; "
            + "connect-src 'self'; frame-ancestors https://intranet.example.test; base-uri 'self'; "
            + "object-src https://media.example.test; form-action 'self' https://login.idp.test",
            Single(response, "Content-Security-Policy"));
    }

    [Fact]
    public async Task A_source_carrying_a_directive_or_policy_separator_is_dropped_and_logged_not_spliced_in()
    {
        // registry.schema.json allows any source without whitespace, so ';' and ',' get through it. A
        // directive named earlier wins over a later duplicate, so spliced in as written these two would
        // put 'unsafe-inline' into script-src ahead of the baseline's, which the contract forbids, and
        // a second policy beside the first.
        AppDefinition app = TestApps.Entry("app1", csp: Csp(
            ("default-src", ["https://cdn.example.test;script-src", "'unsafe-inline'"]),
            ("img-src", ["https://img.example.test,img-src", "https://ok.example.test"])));
        await using HeadersHost host = await HeadersHost.StartAsync(Ct, [app]);

        using HttpResponseMessage response = await host.SendAsync(app, "GET", "/", Ct);

        Assert.Equal(
            "default-src 'self' 'unsafe-inline'; script-src 'self'; style-src 'self'; "
            + "img-src 'self' https://ok.example.test; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; "
            + "base-uri 'self'; object-src 'none'; form-action 'self' https://login.idp.test",
            Single(response, "Content-Security-Policy"));
        lock (host.Log)
        {
            Assert.Single(host.Log, line => line.Contains("app=app1", StringComparison.Ordinal)
                && line.Contains("directive=default-src", StringComparison.Ordinal));
            Assert.Single(host.Log, line => line.Contains("app=app1", StringComparison.Ordinal)
                && line.Contains("directive=img-src", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_none_directive_whose_every_addition_is_dropped_keeps_none()
    {
        // 'none' gives way only to a source that is actually sent. Cleared first and then left empty,
        // the directive would read "frame-ancestors ;", which says nothing a reader can check.
        AppDefinition app = TestApps.Entry("app1", csp: Csp(
            ("frame-ancestors", ["https://intranet.example.test;script-src"]),
            ("object-src", ["https://media.example.test,x"])));
        await using HeadersHost host = await HeadersHost.StartAsync(Ct, [app]);

        using HttpResponseMessage response = await host.SendAsync(app, "GET", "/", Ct);

        Assert.Equal(Baseline, Single(response, "Content-Security-Policy"));
    }

    [Fact]
    public async Task An_issuer_with_no_browser_origin_fails_the_request_rather_than_send_a_policy_without_it()
    {
        // The validator admits only issuers that configuration names, so this is a misconfigured
        // gateway. A policy without the provider in form-action would break logout quietly (the
        // contract's note on form-action); a 500 says so at once.
        AppDefinition app = TestApps.Entry("app1", issuer: "unconfigured");
        await using HeadersHost host = await HeadersHost.StartAsync(Ct, [app]);

        using HttpResponseMessage response = await host.SendAsync(app, "GET", "/", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_request_no_app_was_pinned_for_fails_rather_than_pass_without_a_policy()
    {
        // The stand-in for stage 4 pins nothing for an unknown host, as a misordered pipeline would
        // leave it. The real stage 4 answers 404 itself and never calls stage 5.
        await using HeadersHost host = await HeadersHost.StartAsync(Ct, [App1]);

        using HttpResponseMessage response = await host.SendAsync(TestApps.Entry("stranger"), "GET", "/", Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(Ct));
    }

    // ---- N-24 and the fixed set: every response, upstream copies overwritten ----

    /// <summary>
    /// <c>contracts/http-contract.md</c> -> Response headers, less the policy, which the F-06 tests
    /// pin. The terminal in <see cref="HeadersHost"/> sends a different value under every one of these
    /// names first.
    /// </summary>
    private static readonly (string Name, string Value)[] FixedSet =
    [
        ("X-Content-Type-Options", "nosniff"),
        ("Referrer-Policy", "strict-origin-when-cross-origin"),
        ("Cross-Origin-Opener-Policy", "same-origin"),
        ("Cross-Origin-Resource-Policy", "same-origin"),
        ("Strict-Transport-Security", "max-age=31536000"),
    ];

    /// <summary>
    /// The four responses N-24 names, and a proxied API success. The API error is a real one: stage 6
    /// refuses a cross-site call after stage 5 has run, so the set has to reach a refusal a later stage
    /// wrote.
    /// </summary>
    public static TheoryData<string, string, string?, int, string> Responses() => new()
    {
        { "shell", "/", null, 200, "text/html" },
        { "asset", "/assets/app-3f2a9c.js", null, 200, "text/javascript" },
        { "bff", "/bff/session", "same-origin", 200, "application/json" },
        { "api error", "/api/orders/42", "cross-site", 403, "application/problem+json" },
        { "api success", "/api/orders/42", "same-origin", 200, "application/json" },
    };

    [Theory]
    [MemberData(nameof(Responses))]
    public async Task N24_every_response_carries_the_fixed_set_once_and_no_access_control_header(
        string kind, string path, string? fetchSite, int status, string contentType)
    {
        await using HeadersHost host = await HeadersHost.StartAsync(Ct, [App1]);

        using HttpResponseMessage response = fetchSite is null
            ? await host.SendAsync(App1, "GET", path, Ct)
            : await host.SendAsync(App1, "GET", path, Ct, ("Sec-Fetch-Site", fetchSite));

        // The control: the request reached the response it is meant to stand for.
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);

        // N-24's three, by what they say rather than by string, then the whole set by value. Single
        // fails if the terminal's copy survived beside the platform's; the value fails if it replaced it.
        string policy = Single(response, "Content-Security-Policy");
        Assert.Contains("; frame-ancestors 'none';", policy, StringComparison.Ordinal);
        Assert.Equal(Baseline, policy);
        foreach ((string name, string value) in FixedSet)
        {
            Assert.True(value == Single(response, name), $"{kind}: {name}");
        }

        // No Access-Control-* header, ever: the terminal sent two.
        Assert.DoesNotContain(response.Headers, h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    private static ImmutableDictionary<string, ImmutableArray<string>> Csp(params (string Directive, string[] Sources)[] additions) =>
        additions.ToImmutableDictionary(a => a.Directive, a => a.Sources.ToImmutableArray(), StringComparer.Ordinal);
}
