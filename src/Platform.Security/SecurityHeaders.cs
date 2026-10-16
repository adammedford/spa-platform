using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Security;

/// <summary>
/// <c>UsePlatformSecurityHeaders</c>: the app's content security policy and the fixed header set,
/// on every response, overwriting any copy an upstream sent. Task 4.5; F-06's per-app headers and N-24.
/// The header set and the baseline policy are <c>contracts/http-contract.md</c> -> Response headers.
/// </summary>
internal sealed partial class SecurityHeaders(IConfiguration configuration, ILogger logger)
{
    /// <summary>
    /// The baseline, directive by directive, in the contract's order. An entry's <c>csp</c> may extend
    /// these ten and no others, which is why <c>registry.schema.json</c> lists exactly them.
    /// </summary>
    private static readonly ImmutableArray<(string Directive, string Source)> Baseline =
    [
        ("default-src", "'self'"),
        ("script-src", "'self'"),
        ("style-src", "'self'"),
        ("img-src", "'self'"),
        ("font-src", "'self'"),
        ("connect-src", "'self'"),
        ("frame-ancestors", "'none'"),
        ("base-uri", "'self'"),
        ("object-src", "'none'"),
        ("form-action", "'self'"),
    ];

    private const string None = "'none'";

    /// <summary>
    /// One policy per entry. An <see cref="AppDefinition"/> is replaced whole when the registry swaps,
    /// so a changed entry is a new key, and the old one's policy goes with the old record.
    /// </summary>
    private readonly ConditionalWeakTable<AppDefinition, string> policies = [];

    internal Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Host resolution pins the app before this stage runs. Without it there is no policy to send,
        // and sending none would be failing open, so a misordered pipeline throws, as the request gate
        // and antiforgery do.
        AppDefinition app = context.Features.Get<IAppFeature>()?.App
            ?? throw new InvalidOperationException(
                "UsePlatformSecurityHeaders runs after UsePlatformHostResolution, which sets IAppFeature.");

        string policy = policies.GetValue(app, Build);

        // OnStarting rather than a write here: the headers are applied as the response begins, after
        // every later stage has written its own. That is what makes the contract's "overwrite what the
        // upstream sent" true, because YARP copies the upstream's headers into the response before the
        // body starts, and a refusal written by the request gate or antiforgery gets the same set.
        // https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.httpresponse.onstarting
        context.Response.OnStarting(() =>
        {
            Apply(context.Response.Headers, policy);
            return Task.CompletedTask;
        });

        return next(context);
    }

    private static void Apply(IHeaderDictionary headers, string policy)
    {
        // The indexer replaces every value under the name, so an upstream's copy cannot survive beside
        // the platform's.
        headers.ContentSecurityPolicy = policy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers.StrictTransportSecurity = "max-age=31536000";

        // No Access-Control-* header, ever (contracts/http-contract.md -> Response headers): the gateway
        // grants no cross-origin reads, and an upstream that tries is overruled. Collected first, because
        // removing while enumerating the dictionary throws.
        foreach (string name in headers.Keys.Where(IsAccessControl).ToList())
        {
            headers.Remove(name);
        }
    }

    /// <summary>
    /// False for a source that would end its directive (<c>;</c>) or its policy (<c>,</c>), which the
    /// CSP grammar reserves and a source expression never contains. <c>registry.schema.json</c> rejects
    /// whitespace in a source but not these, and spliced in as written they would add directives the
    /// entry may not set: a directive named earlier wins over a later one, so <c>default-src</c> could
    /// carry a <c>script-src</c> with <c>'unsafe-inline'</c> ahead of the baseline's. Dropping the
    /// source leaves the policy stricter than the entry asked, never looser.
    /// https://w3c.github.io/webappsec-csp/#parse-serialized-policy
    /// </summary>
    private static bool CanSplice(string source) =>
        source.Length > 0 && !source.Any(c => c is ';' or ',' || char.IsWhiteSpace(c) || char.IsControl(c));

    // The app and the directive, not the source: the entry is the registry's to read, and the line says
    // where to look.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "A content security policy source was dropped: it carries a separator or whitespace. app={AppId} directive={Directive}")]
    private static partial void SourceDropped(ILogger logger, string appId, string directive);

    private static bool IsAccessControl(string name) =>
        name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The baseline plus the entry's additions, with the issuer's browser origin
    /// (<c>Issuers:{name}:BrowserOrigin</c>, read as written: a <see cref="Uri"/> would add a trailing
    /// slash) in <c>form-action</c>. The loop walks the baseline, never the entry, so a directive outside
    /// the ten, which the validator already refuses, is never sent.
    /// </summary>
    private string Build(AppDefinition app)
    {
        string origin = configuration[$"Issuers:{app.OAuth.Issuer}:BrowserOrigin"]
            ?? throw new InvalidOperationException(
                $"Issuer '{app.OAuth.Issuer}' has no Issuers:{{name}}:BrowserOrigin, which form-action needs.");

        StringBuilder policy = new();
        foreach ((string directive, string baseline) in Baseline)
        {
            List<string> sources = [baseline];
            if (directive == "form-action")
            {
                sources.Add(origin);
            }

            if (app.Csp.TryGetValue(directive, out ImmutableArray<string> additions))
            {
                // frame-ancestors and object-src start at 'none', and the schema lets an entry extend
                // both. Beside another source 'none' has no effect: "A list consisting of « 'none',
                // https://example.com » ... would match https://example.com/". So the additions replace
                // it, and the header reads as the browser applies it.
                // https://w3c.github.io/webappsec-csp/#match-url-to-source-list
                // Only a source that survives CanSplice replaces it: with every addition dropped, the
                // directive keeps 'none' rather than going out as an empty list.
                List<string> kept = [];
                foreach (string source in additions)
                {
                    if (CanSplice(source))
                    {
                        kept.Add(source);
                    }
                    else
                    {
                        SourceDropped(logger, app.AppId, directive);
                    }
                }

                if (baseline == None && kept.Count > 0)
                {
                    sources.Clear();
                }

                sources.AddRange(kept);
            }

            if (policy.Length > 0)
            {
                policy.Append("; ");
            }

            policy.Append(directive).Append(' ').AppendJoin(' ', sources);
        }

        return policy.ToString();
    }
}
