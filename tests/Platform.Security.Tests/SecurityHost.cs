using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Platform.Testing;

namespace Platform.Security.Tests;

/// <summary>
/// Workstream 4's own gateway, cut down to what task 4.6 fills: <c>AddPlatformSecurity</c> and
/// <c>UsePlatformForwardedHeaders</c>. Behind them is a terminal that echoes what the later
/// stages would read: the scheme, the client address and the host.
///
/// It listens on loopback through <see cref="StageHost"/> (#140), so a test's client address is
/// 127.0.0.1, and the trusted networks are the test's to choose: one that leaves loopback out shows a
/// header from outside being ignored, and one that lists it shows the same header being applied,
/// which is what makes the first assertion able to fail.
///
/// The composed gateway cannot show <c>UsePlatformForwardedHeaders</c> on its own yet: everything after
/// it stops at the host-resolution stub until task 2.3. Workstream 6 proves these behaviours again in
/// the composed suites.
/// </summary>
public sealed class SecurityHost : IAsyncDisposable
{
    private readonly StageHost stage;

    private SecurityHost(StageHost stage)
    {
        this.stage = stage;
    }

    public Uri BaseAddress => stage.BaseAddress;

    /// <summary>Every line the host logged, captured at Trace, so a test can look for a rule id.</summary>
    public CapturedLog Log => stage.Log;

    /// <summary>A host whose <c>ForwardedHeaders:TrustedNetworks</c> are the ones given.</summary>
    public static async Task<SecurityHost> StartAsync(CancellationToken ct, params string[] trustedNetworks) =>
        new(await StageHost.StartAsync(new StageHostOptions
        {
            Services = builder =>
            {
                builder.Configuration.AddInMemoryCollection(
                    trustedNetworks.Select((n, i) => new KeyValuePair<string, string?>($"ForwardedHeaders:TrustedNetworks:{i}", n)));
                builder.AddPlatformSecurity();
            },
            Pipeline = web =>
            {
                web.UsePlatformForwardedHeaders();
                web.Run(context => context.Response.WriteAsync(
                    $"{context.Request.Scheme}|{context.Connection.RemoteIpAddress}|{context.Request.Host}", ct));
            },
        }, ct));

    /// <summary>
    /// Sends <paramref name="head"/> as written, then a blank line, over a raw socket. A raw socket
    /// because <c>HttpClient</c> will not send an origin server an HTTP/1.1 <c>CONNECT</c>, and an
    /// oversized header is plainer this way.
    /// </summary>
    public Task<RawResponse> RawAsync(string head, CancellationToken ct) => stage.RawAsync(head + "\r\n\r\n", ct);

    public ValueTask DisposeAsync() => stage.DisposeAsync();
}
