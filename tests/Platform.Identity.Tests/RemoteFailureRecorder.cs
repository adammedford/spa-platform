using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Platform.Identity.Tests;

/// <summary>
/// Remembers why the handler refused each callback. The gateway's own remote-failure event answers
/// with one fixed page that says nothing of the reason (plan row 3.5), so a test that needs the reason
/// reads it here. Registered as post-configuration, it runs after <c>AddPlatformIdentity</c>'s setup
/// and wraps the event that setup installed rather than replacing it.
/// </summary>
internal sealed class RemoteFailureRecorder : IPostConfigureOptions<OpenIdConnectOptions>
{
    private readonly ConcurrentQueue<Exception> failures = new();

    /// <summary>Every refusal's reason, oldest first.</summary>
    public IReadOnlyList<Exception> Failures => [.. failures];

    public static Action<IServiceCollection> Install(out RemoteFailureRecorder recorder)
    {
        RemoteFailureRecorder installed = new();
        recorder = installed;
        return services => services.AddSingleton<IPostConfigureOptions<OpenIdConnectOptions>>(installed);
    }

    public void PostConfigure(string? name, OpenIdConnectOptions options)
    {
        Func<Microsoft.AspNetCore.Authentication.RemoteFailureContext, Task> gateway = options.Events.OnRemoteFailure;
        options.Events.OnRemoteFailure = context =>
        {
            if (context.Failure is { } failure)
            {
                failures.Enqueue(failure);
            }

            return gateway(context);
        };
    }
}
