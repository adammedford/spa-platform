using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Security;

/// <summary>
/// Stage 1's forwarded-header options, from <c>ForwardedHeaders:TrustedNetworks</c> in
/// <c>contracts/platform-config.schema.json</c>. Threat T6 and ADR-0010: the scheme and the client
/// address are taken from the ALB subnets only, with a forward limit of one.
/// </summary>
internal static class PlatformForwardedHeaders
{
    internal const string TrustedNetworksKey = "ForwardedHeaders:TrustedNetworks";

    internal static void Add(IServiceCollection services) =>
        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                // XForwardedHost is left out: stage 4 resolves the app from the request authority, and
                // a header that moved it would let a client pick the app (contracts/pipeline.md).
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

                // The ALB appends the address it saw, so only the last entry in X-Forwarded-For is
                // the ALB's own; anything before it is whatever the client sent (T6).
                options.ForwardLimit = 1;

                // The framework trusts loopback by default (KnownProxies holds ::1 and the known
                // networks 127.0.0.0/8). Neither is an ALB subnet, so both go.
                // https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer#forwarded-headers-middleware-options
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();

                // KnownNetworks is obsolete in .NET 10; KnownIPNetworks takes System.Net.IPNetwork.
                string[] trusted = configuration.GetSection(TrustedNetworksKey).Get<string[]>() ?? [];
                foreach (string cidr in trusted)
                {
                    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
                }

                // With both lists empty the middleware trusts every sender, not none: an empty list
                // is "no restriction". The test that sends from loopback with no setting found it, and
                // the middleware's source says so: it checks the sender only when either list is non-empty.
                // https://github.com/dotnet/aspnetcore/blob/main/src/Middleware/HttpOverrides/src/ForwardedHeadersMiddleware.cs
                // So no trusted network means no forwarded header is processed at all.
                if (trusted.Length == 0)
                {
                    options.ForwardedHeaders = ForwardedHeaders.None;
                }
            });

    internal static void Use(IApplicationBuilder app) => app.UseForwardedHeaders();
}
