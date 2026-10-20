using Microsoft.AspNetCore.Http;
using Platform.Abstractions;

namespace Platform.Hosting;

/// <summary>
/// <c>UsePlatformHostResolution</c> (contracts/pipeline.md; docs/plan/01-plan.md, Request path step
/// 3 and threat T4).
/// </summary>
internal static class HostResolution
{
    public static Task InvokeAsync(HttpContext context, RequestDelegate next, IRegistry registry)
    {
        // One read of Current for the whole request: the snapshot is replaced whole, never mutated
        // (IRegistry), so every stage after this one sees the app and the snapshot this request was
        // resolved against, even when a swap lands mid-request.
        RegistrySnapshot snapshot = registry.Current;
        string authority = context.Request.Host.Value ?? "";

        // Exact means ordinal (GLOSSARY.md, Host), checked here rather than left to the snapshot's
        // index comparer: the registry builds it ordinal, but host resolution is where the rule is
        // enforced, and a host that differs only in case is not the host the registry names.
        // Nothing is normalised; X-Forwarded-Host is never read (N-08; docs/plan/01-plan.md, threat
        // T4).
        if (!snapshot.ByHost.TryGetValue(authority, out AppDefinition? app)
            || !string.Equals(app.Host, authority, StringComparison.Ordinal))
        {
            // Before routing, so no endpoint and no authentication handler runs for a host the registry
            // does not name: the callback path included (S-A3). The module notes the rule for the one
            // request line UsePlatformTelemetry writes and for security.rejections (contracts/
            // telemetry.md -> "How rule and session_ref reach the request line"), and writes the
            // id's problem body, which names nothing about the request.
            //
            // No Warning line, on purpose: the module never logs, and this stage writes none of its
            // own. An unknown host is the cheapest traffic anyone can send, so a line each would let
            // a stranger flood the log; the request line and the counter record it once
            // (contracts/changes/6.4-request-telemetry-feature.md).
            return Rejection.EndAsync(context, RuleIds.HostUnknown);
        }

        context.Features.Set<IAppFeature>(new AppFeature(app, snapshot));
        return next(context);
    }

    private sealed record AppFeature(AppDefinition App, RegistrySnapshot Snapshot) : IAppFeature;
}
