using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Platform.Telemetry.Tests;

/// <summary>
/// N-16 at component level: canary values for tokens, secrets, cookies, codes and state travel every
/// way a request can carry them, through the real OpenID Connect handler and an upstream call, with
/// configuration asking every logger for Trace. No canary appears in any line. Workstream 6 runs the
/// same search across the composed suites.
///
/// The control runs the same traffic with no telemetry and finds the canaries, which is what makes
/// "none appear" an assertion that can fail.
/// </summary>
public sealed class LogCanaryTests
{
    private const string Code = "CANARY-CODE-6f1d";
    private const string State = "CANARY-STATE-93ab";
    private const string Token = "CANARY-TOKEN-27c4";
    private const string Cookie = "CANARY-COOKIE-b850";
    private const string Secret = "CANARY-SECRET-e1e9";

    private static readonly string[] Canaries = [Code, State, Token, Cookie, Secret];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Configuration a careless operator could ship: everything at Trace, and the OpenID Connect
    // handler named on its own, which is more specific than any rule the gateway adds in code.
    private static Dictionary<string, string?> EverythingAtTrace() => new()
    {
        ["Logging:LogLevel:Default"] = "Trace",
        ["Logging:LogLevel:Microsoft.AspNetCore.Authentication.OpenIdConnect"] = "Trace",
        ["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Trace",
        ["Logging:LogLevel:System.Net.Http.HttpClient"] = "Trace",
    };

    private static async Task SendCanaries(TelemetryHost host)
    {
        using HttpClient client = new(new HttpClientHandler { UseCookies = false });

        // The callback: code and state in the query, as Cognito sends them (ADR-0007).
        await client.GetAsync(new Uri(host.BaseAddress, $"/signin-oidc?code={Code}&state={State}"), Ct);

        // An API call: a bearer token and a cookie in headers, and canaries in a query string the
        // terminal forwards to the upstream.
        using HttpRequestMessage api = new(HttpMethod.Get,
            new Uri(host.BaseAddress, $"/api/orders/items?access_token={Token}&client_secret={Secret}"));
        api.Headers.Add("Authorization", "Bearer " + Token);
        api.Headers.Add("Cookie", "__Host-spa-session=" + Cookie);
        await client.SendAsync(api, Ct);

        // A logger in the authentication namespace writing at Debug and Trace, as the handler does,
        // and an application logger that names its properties plainly.
        ILoggerFactory loggers = host.Services.GetRequiredService<ILoggerFactory>();
        ILogger handler = loggers.CreateLogger("Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler");
        handler.LogTrace("MessageReceived: '{RedirectUrl}'.", $"https://app1.spa-poc.test/signin-oidc?code={Code}&state={State}");
        handler.LogDebug("Redirect headers: Set-Cookie: {Cookie}", Cookie);

        ILogger application = loggers.CreateLogger("Platform.Identity.Example");
        application.LogInformation("Refreshed {AccessToken} with {ClientSecret} for {SessionKey}", Token, Secret, Cookie);
        application.LogInformation("Callback {Url}", $"/signin-oidc?code={Code}&state={State}");
    }

    [Fact]
    public async Task N16_no_canary_appears_in_any_log_line()
    {
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct, configuration: EverythingAtTrace());

        await SendCanaries(host);

        Assert.NotEmpty(host.Log.Lines);
        Assert.Contains(host.Log.Json, line => line.GetProperty("msg").GetString() == "request");
        foreach (string line in host.Log.Lines)
        {
            foreach (string canary in Canaries)
            {
                Assert.DoesNotContain(canary, line, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Without_platform_telemetry_the_canaries_do_reach_the_log()
    {
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct, telemetry: false, configuration: EverythingAtTrace());

        await SendCanaries(host);

        // The framework's request log and the handler's own Trace line carry the callback's code and
        // state; the plainly named properties carry the rest.
        Assert.Contains(host.Log.Lines, line =>
            line.Contains("Request starting", StringComparison.Ordinal) && line.Contains(Code, StringComparison.Ordinal));
        Assert.All(Canaries, canary =>
            Assert.Contains(host.Log.Lines, line => line.Contains(canary, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task N16_no_canary_appears_in_any_span()
    {
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct, configuration: EverythingAtTrace());

        await SendCanaries(host);
        host.Flush();

        Assert.NotEmpty(host.Spans);
        foreach (System.Diagnostics.Activity span in host.Spans)
        {
            string rendered = span.DisplayName + " " + string.Join(" ", span.TagObjects.Select(t => t.Key + "=" + t.Value));
            foreach (string canary in Canaries)
            {
                Assert.DoesNotContain(canary, rendered, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Authentication_loggers_are_capped_at_information_whatever_configuration_says()
    {
        await using TelemetryHost host = await TelemetryHost.StartAsync(Ct, configuration: EverythingAtTrace());
        ILogger handler = host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler");

        handler.LogDebug("held back at debug");
        handler.LogTrace("held back at trace");
        handler.LogInformation("written at information");

        Assert.Contains(host.Log.Lines, line => line.Contains("written at information", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Log.Lines, line => line.Contains("held back", StringComparison.Ordinal));
    }
}
