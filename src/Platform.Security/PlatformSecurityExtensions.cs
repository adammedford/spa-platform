using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace Platform.Security;

/// <summary>
/// Workstream 4's security entry points: stages 1, 5, 6 and 9 of <c>contracts/pipeline.md</c>.
///
/// <b>Stubs.</b> Task I-4 wrote these so <c>Program.cs</c> can call the frozen order on the empty
/// skeleton. Tasks 4.3 to 4.6 fill them in. Keep these signatures and add no others.
///
/// The pipeline contract names the owner of each stage but not the project, and workstream 4 owns
/// two. The split is by concern: the request gate, the headers, the antiforgery check and the
/// forwarded-header and request limits are here, and the YARP configuration and route are in
/// <c>Platform.Proxy</c>.
/// </summary>
public static class PlatformSecurityExtensions
{
    public static IHostApplicationBuilder AddPlatformSecurity(this IHostApplicationBuilder builder) => builder;

    /// <summary>
    /// Stage 1, and it is first for a reason: everything after it reads the scheme and the client
    /// address, and the antiforgery cookie's <c>SecurePolicy = Always</c> has to be applied after it,
    /// because a <c>__Host-</c> cookie sent without <c>Secure</c> is discarded (ADR-0007).
    ///
    /// <c>X-Forwarded-Proto</c> and <c>X-Forwarded-For</c> are trusted from
    /// <c>ForwardedHeaders:TrustedNetworks</c> only, and <c>X-Forwarded-Host</c> is never read.
    /// Task 4.6.
    /// </summary>
    public static IApplicationBuilder UsePlatformForwardedHeaders(this IApplicationBuilder app) => app;

    /// <summary>
    /// The app's content security policy and the fixed header set, on every response, overwriting any
    /// copy an upstream sent. Task 4.5.
    /// </summary>
    public static IApplicationBuilder UsePlatformSecurityHeaders(this IApplicationBuilder app) => app;

    /// <summary>
    /// The rules in <c>contracts/request-gate.md</c>, one rule id per rejection. It runs before
    /// authentication, because no rule in it needs the user. Task 4.3.
    /// </summary>
    public static IApplicationBuilder UsePlatformRequestGate(this IApplicationBuilder app) => app;

    /// <summary>
    /// Unsafe methods on <c>/api</c> need a valid <c>X-CSRF-TOKEN</c>. It runs after the request gate
    /// because the token check needs the user. Header tokens only, so a request body is never read
    /// (finding R-E7). Task 4.4.
    /// </summary>
    public static IApplicationBuilder UsePlatformAntiforgery(this IApplicationBuilder app) => app;
}
