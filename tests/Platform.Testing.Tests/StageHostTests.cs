using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Testing.Tests;

/// <summary>
/// The stage host a test host is built on (#140, #145), proven over a real socket, through
/// <see cref="StageHost.StartAsync"/> the way a test host crosses it. Each test names the change to
/// the module that turns it red: the code it replaces had no tests of its own, and its defects sat in
/// exactly the places these pin (the stand-in's position, the log floor, the capture's survival).
/// </summary>
public sealed class StageHostTests
{
    [Fact]
    public async Task The_telemetry_stand_in_is_off_unless_asked()
    {
        // Mutation: default TelemetryStandIn to true. Six proofs in the migrated suites that a stage
        // writes the feature null-conditionally rest on this default (change request 6.4, decision 1):
        // a host that installed the stand-in unasked would turn each into a test of nothing.
        await using StageHost host = await StageHost.StartAsync(new StageHostOptions
        {
            Pipeline = app => app.Run(context => context.Response.WriteAsync(
                context.Features.Get<IRequestTelemetryFeature>() is null ? "no feature" : "a feature")),
        }, Ct);

        string answer = await host.Client.GetStringAsync("/", Ct);

        Assert.Equal("no feature", answer);
        Assert.Empty(host.TelemetryRules);
        Assert.Null(host.LastTelemetry);
    }

    [Fact]
    public async Task The_telemetry_stand_in_runs_before_the_pipeline_delegate()
    {
        // Mutation: install the stand-in after the Pipeline delegate, AntiforgeryHost's order before
        // #140. The delegate's first middleware ends the request, as the method allowlist's 405 does,
        // so a stand-in behind it never runs and no rule id is recorded.
        await using StageHost host = await StageHost.StartAsync(new StageHostOptions
        {
            TelemetryStandIn = true,
            Pipeline = app => app.Use((HttpContext context, RequestDelegate _) =>
            {
                context.Features.Get<IRequestTelemetryFeature>()?.Rule = "gate.method";
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return Task.CompletedTask;
            }),
        }, Ct);

        using HttpResponseMessage response = await host.Client.GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(["gate.method"], host.TelemetryRules);
        Assert.Equal("gate.method", host.LastTelemetry?.Rule);
    }

    [Fact]
    public async Task A_feature_is_recorded_while_the_request_is_in_flight()
    {
        // Mutation: record the feature after the next middleware returns. Each stage sets its rule id
        // before it writes the response, so a feature recorded on entry cannot race the body back to
        // the client; one recorded on the way out would be missing here, mid-request.
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using StageHost host = await StageHost.StartAsync(new StageHostOptions
        {
            TelemetryStandIn = true,
            Pipeline = app => app.Run(async context =>
            {
                context.Features.Get<IRequestTelemetryFeature>()?.Rule = "gate.origin";
                entered.SetResult();
                await release.Task.WaitAsync(context.RequestAborted);
            }),
        }, Ct);

        Task<HttpResponseMessage> inFlight = host.Client.GetAsync("/", Ct);
        await entered.Task.WaitAsync(Ct);

        Assert.Equal(["gate.origin"], host.TelemetryRules);

        release.SetResult();
        (await inFlight).Dispose();
    }

    [Fact]
    public async Task Every_line_is_captured_at_Trace_with_its_exception_text()
    {
        // Mutations: leave the floor at the framework's Information, and the Trace record is absent;
        // drop the exception from Text, and the inner exception's canary is absent. N-16 in a stage's
        // own test project searches these lines, and the OpenID Connect handler writes its callback
        // URL at Debug and Trace.
        await using StageHost host = await StageHost.StartAsync(new StageHostOptions
        {
            Pipeline = app => app.Run(context =>
            {
                ILogger logger = context.RequestServices.GetRequiredService<ILogger<StageHostTests>>();
                logger.LogTrace("a trace line carrying trace-canary");
                logger.LogError(
                    new InvalidOperationException("outer", new InvalidOperationException("inner-canary")),
                    "a failure");
                return Task.CompletedTask;
            }),
        }, Ct);

        (await host.Client.GetAsync("/", Ct)).Dispose();

        CapturedLine trace = Assert.Single(host.Log.Lines, line => line.Message.Contains("trace-canary", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Trace, trace.Level);
        Assert.Equal(typeof(StageHostTests).FullName, trace.Category);
        CapturedLine failure = Assert.Single(host.Log.Lines, line => line.Message == "a failure");
        Assert.Contains("inner-canary", failure.Text, StringComparison.Ordinal);
        Assert.True(host.Log.Contains("inner-canary"));
    }

    [Fact]
    public async Task The_capture_survives_a_services_delegate_that_clears_providers()
    {
        // Mutation: register the capture before the Services delegate. AddPlatformTelemetry clears
        // the providers (src/Platform.Telemetry/PlatformTelemetryExtensions.cs), so a capture
        // registered first would be removed by any host that composes the real telemetry stage.
        await using StageHost host = await StageHost.StartAsync(new StageHostOptions
        {
            Services = builder => builder.Logging.ClearProviders(),
            Pipeline = app => app.Run(context =>
            {
                context.RequestServices.GetRequiredService<ILogger<StageHostTests>>().LogInformation("survived-canary");
                return Task.CompletedTask;
            }),
        }, Ct);

        (await host.Client.GetAsync("/", Ct)).Dispose();

        Assert.True(host.Log.Contains("survived-canary"));
    }

    [Fact]
    public async Task The_client_keeps_no_cookie_and_follows_no_redirect()
    {
        // Mutation: build the client on the default handler, which keeps cookies and follows
        // redirects. A test writes the Cookie header itself, case variants and all, and reads a
        // challenge as an answer rather than following it.
        await using StageHost host = await StageHost.StartAsync(new StageHostOptions
        {
            Pipeline = app => app.Run(context =>
            {
                if (context.Request.Path == "/challenge")
                {
                    context.Response.Headers.SetCookie = "kept=1; Path=/";
                    context.Response.Redirect("/cookie");
                    return Task.CompletedTask;
                }

                return context.Response.WriteAsync("cookie:" + context.Request.Headers.Cookie.ToString());
            }),
        }, Ct);

        using HttpResponseMessage challenge = await host.Client.GetAsync("/challenge", Ct);
        string second = await host.Client.GetStringAsync("/cookie", Ct);

        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.Equal("cookie:", second);
    }

    [Fact]
    public async Task A_connect_after_dispose_fails()
    {
        // Mutation: make DisposeAsync release nothing, neither stopping nor disposing the application.
        // Either alone releases the port, so skipping one leaves this green. A host that kept its port
        // after a test would answer the next test's stray request.
        StageHost host = await StageHost.StartAsync(new StageHostOptions
        {
            Pipeline = app => app.Run(context => Task.CompletedTask),
        }, Ct);
        Uri address = host.BaseAddress;
        (await host.Client.GetAsync("/", Ct)).Dispose();

        await host.DisposeAsync();

        using TcpClient client = new();
        await Assert.ThrowsAnyAsync<SocketException>(async () => await client.ConnectAsync(address.Host, address.Port, Ct));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
