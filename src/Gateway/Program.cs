using Microsoft.AspNetCore.Builder;
using Platform.Hosting;
using Platform.Identity;
using Platform.Proxy;
using Platform.Registry;
using Platform.Security;
using Platform.Telemetry;

// The composition root, written once at task I-4 and frozen with the contracts at G1a. Changed once,
// at G1b, when ADR-0018 swapped the first two stages.
//
// The order below is contracts/pipeline.md. It is security-relevant, not a preference, and the
// contracts index names this file as part of that contract: the entry-point signatures are here.
// Each entry point starts as a stub in its owner's project, and the owner fills it in. A workstream
// fills in the ones the pipeline contract assigns to it and adds no others. Nothing else belongs in
// this file.

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Service registration, in the order the contract's service table gives.
builder.AddPlatformTelemetry();     // 6
builder.AddPlatformRegistry();      // 2
builder.AddPlatformHosting();       // 2
builder.AddPlatformIdentity();      // 3
builder.AddPlatformProxy();         // 4
builder.AddPlatformSecurity();      // 4

WebApplication app = builder.Build();

// The request log line and the trace scope, in place of the framework's default request log, which
// includes the query string. First, so every rejection a later stage answers is logged and counted
// under its rule id, including the method allowlist's 405. The line carries no value forwarded
// headers settle -- no scheme, no client address, no host -- which is why it can run before they
// are settled (ADR-0018, at G1b; until then the two stages below ran the other way round).
app.UsePlatformTelemetry();

// Before everything that reads the scheme and the client address, and before the antiforgery
// cookie's SecurePolicy = Always is applied: a __Host- cookie sent without Secure is discarded
// (ADR-0007). The method allowlist runs here too.
app.UsePlatformForwardedHeaders();

// Before host resolution, so the ALB health check and `spactl wait` need no registry entry and no
// known host.
app.UsePlatformHealth();

// The request authority must equal a registry host exactly. The app and the snapshot are pinned for
// the request as IAppFeature. Any other host gets 404 before routing runs.
app.UsePlatformHostResolution();

// After host resolution, which pins the app whose content security policy this stage sends.
app.UsePlatformSecurityHeaders();

// Before authentication, because no rule in the gate needs the user.
app.UsePlatformRequestGate();

// The framework's own. The schemes come from workstream 3, and the OpenID Connect callback is
// handled here.
app.UseRouting();
app.UseAuthentication();

// A session before any asset (ADR-0008).
app.UsePlatformShellGate();

// After the request gate, because the token check needs the user.
app.UsePlatformAntiforgery();

// /bff/*, then /api/{name}/{rest} through YARP, then everything else from S3.
app.MapPlatformBff();
app.MapPlatformProxy();
app.MapPlatformAssets();

app.Run();
