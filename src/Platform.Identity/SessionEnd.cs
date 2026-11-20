using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Duende.IdentityModel;
using Duende.IdentityModel.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Platform.Abstractions;

namespace Platform.Identity;

/// <summary>
/// The gateway's half of logout (contracts/http-contract.md -> <c>POST /bff/logout</c>, ADR-0017
/// decision 4): attempt to revoke every token set's refresh token, then remove the session whatever the
/// provider said. What follows was written for the shell's one set and holds for each set; the steps for
/// several are below.
///
/// Under the session's gate (<see cref="AppSessions"/>), so a refresh in flight finishes first and the
/// token revoked is the one the record holds now, which may be newer than the one this request's ticket
/// carried. The revocation is made with the token library's parts (see <see cref="RevokeAsync"/>) in the
/// credential style of the app's issuer, the style Cognito's revocation endpoint insists on (C-2.19, C-2.21), for the app's own
/// scheme, under the provider deadline and never the request's cancellation token:
/// a browser that leaves mid-logout must not stop the session being removed. How the attempt ended is
/// logged by outcome only; the token itself reaches no log line.
///
/// The F11 exception (#386, F11 on #332): contracts/http-contract.md says logout "always removes the
/// session", and a remove the store cannot confirm by <c>StoreDeadline</c> is the one case it cannot
/// show that. The store throws <see cref="SessionStoreUnavailableException"/> for it, and the end
/// catches that here, for the remove alone, logs one line saying the removal is not confirmed, and lets
/// the logout go on as usual: both cookies cleared and the provider's logout, so nothing on the wire
/// changes (#386 Q187, Q191). A store read that fails before the remove, the cookie handler's or the
/// one under the gate, is not caught here: the logout endpoint, or for the cookie handler's read
/// <c>UsePlatformShellGate</c>, ends it under <c>auth.store</c> with both cookies intact (#386 Q188, #727). The end then marks the request's
/// session ended (<see cref="AppSessions.MarkEnded"/>), so the cookie handler's sign-out makes no store
/// call of its own (#386 Q192).
/// <para><b>Every token set</b> (#995; the fragments spec #961 -> "Logout"). One logout ends the shell's set
/// and every fragment's:</para>
/// <list type="number">
/// <item>It enters the shell's gate, then each fragment's in ordinal <c>fragmentId</c> order, through the
/// gate-only entry, holding none to start with and taking no refresh lease. The waits share one
/// <see cref="AccessTokenSourceOptions.LogoutGateBudget"/>, measured from logout's entry; a gate not
/// entered within it is skipped, and its set logged <c>gate_timeout</c>. A refusal of the shell's refresh
/// that ended the record while logout waited enters no further gate.</item>
/// <item>It reads the record again.</item>
/// <item>It revokes every set in parallel under one <see cref="AccessTokenSourceOptions.ProviderDeadline"/>,
/// each through its own scheme in the issuer's credential style. A set whose fragment the entry no longer
/// lists has no live scheme (<c>no_scheme</c>), and one recorded to a client the entry no longer names
/// (<c>client_changed</c>) has no current credential: each is dropped unrevoked.</item>
/// <item>It writes one line per set, with its outcome (contracts/telemetry.md -> Logout revocation line).</item>
/// <item>It removes the record.</item>
/// </list>
/// The provider's logout, by the shell's client with its ID token, follows in the endpoint. From entry to
/// removal it takes at most <c>LogoutGateBudget + StoreDeadline + ProviderDeadline + StoreDeadline</c>,
/// 40 s today.
/// </summary>
internal sealed partial class SessionEnd(
    AppSessions sessions,
    IOpenIdConnectConfigurationService configurations,
    CredentialStyles styles,
    AccessTokenSourceOptions options,
    TimeProvider clock,
    ILogger<SessionEnd> logger)
{
    private const string GateTimeout = "gate_timeout";

    public async Task EndAsync(HttpContext context, AppDefinition app, string key)
    {
        long entered = clock.GetTimestamp();
        List<AppSessions.Hold> holds = [];
        HashSet<string> skipped = new(StringComparer.Ordinal);
        try
        {
            // 1. The gates, the shell's first, sharing one budget from logout's entry.
            if (await sessions.TryHoldAsync(key, null, Remaining(entered), clock) is { } shell)
            {
                holds.Add(shell);
            }
            else
            {
                skipped.Add("");
            }

            if (!(holds.FirstOrDefault()?.EndedWhileWaiting ?? false))
            {
                foreach (FragmentDefinition fragment in app.Fragments.OrderBy(f => f.FragmentId, StringComparer.Ordinal))
                {
                    if (await sessions.TryHoldAsync(key, fragment.FragmentId, Remaining(entered), clock) is { } hold)
                    {
                        holds.Add(hold);
                    }
                    else
                    {
                        skipped.Add(fragment.FragmentId);
                    }
                }
            }

            // 2. The re-read, outside the catch below: a read that fails reaches the logout endpoint, which
            // ends it under auth.store with both cookies intact (#386 Q188, #727). A record that is gone,
            // or is not this app's and this client's, is left alone, as the cookie handler's sign-out
            // leaves it (AppSessions.RemoveAsync).
            if (await sessions.ReadAsync(app, key, CancellationToken.None) is { } record)
            {
                try
                {
                    // 3 and 4. The revocations, then one line per set; the removal runs whatever they did.
                    await RevokeEverySetAsync(app, key, record, skipped);
                }
                finally
                {
                    await RemoveAsync(app, key);
                }
            }
        }
        finally
        {
            for (int i = holds.Count - 1; i >= 0; i--)
            {
                holds[i].Dispose();
            }
        }

        sessions.MarkEnded(context, key);
    }

    /// <summary>What is left of the gate budget, measured from logout's entry on the monotonic clock.</summary>
    private TimeSpan Remaining(long entered) => options.LogoutGateBudget - clock.GetElapsedTime(entered);

    /// <summary>
    /// 5. The removal. The F11 exception (#386): a remove the store could not confirm is logged on one line,
    /// naming the operation and the session's reference and nothing else (N-16), and the logout goes on.
    /// </summary>
    private async Task RemoveAsync(AppDefinition app, string key)
    {
        try
        {
            await sessions.RemoveHeldAsync(key);
        }
        catch (SessionStoreUnavailableException e)
        {
            RemovalNotConfirmed(logger, app.AppId, SessionRef.Of(key), e.Operation);
        }
    }

    /// <summary>
    /// Every set's revocation, in parallel under one provider deadline, and then each set's line, the
    /// shell's first and the fragments' in ordinal order. A fragment the ticket records only a mark for
    /// holds no set and gets no line.
    /// </summary>
    private async Task RevokeEverySetAsync(AppDefinition app, string key, SessionRecord record, IReadOnlySet<string> skipped)
    {
        AuthenticationProperties properties = TicketSerializer.Default.Deserialize(record.Ticket.ToArray())?.Properties
            ?? throw new InvalidOperationException("A session record holds a serialized ticket.");

        using CancellationTokenSource deadline = new(options.ProviderDeadline);
        Task<string> shell = skipped.Contains("") ? Task.FromResult(GateTimeout)
            : properties.GetTokenValue(TokenSet.RefreshTokenName) is { Length: > 0 } refreshToken
                ? RevokeAsync(app, SchemeNames.Oidc(app.AppId), refreshToken, deadline.Token)
                : Task.FromResult("no_refresh_token");

        List<(string FragmentId, Task<string> Outcome)> fragments = [];
        foreach ((string fragmentId, string clientId) in FragmentSets.Recorded(properties))
        {
            if (!FragmentSets.TokenNames(properties, fragmentId).Any())
            {
                continue;
            }

            FragmentDefinition? fragment = app.Fragments.FirstOrDefault(f => string.Equals(f.FragmentId, fragmentId, StringComparison.Ordinal));
            fragments.Add((fragmentId,
                fragment is null ? Task.FromResult("no_scheme")
                : !string.Equals(fragment.OAuth.ClientId, clientId, StringComparison.Ordinal) ? Task.FromResult("client_changed")
                : skipped.Contains(fragmentId) ? Task.FromResult(GateTimeout)
                : FragmentSets.RecordedToken(properties, fragmentId, TokenSet.RefreshTokenName) is { } fragmentToken
                    ? RevokeAsync(app, SchemeNames.Oidc(app.AppId, fragmentId), fragmentToken, deadline.Token)
                    : Task.FromResult("no_refresh_token")));
        }

        Revocation(logger, app.AppId, SessionRef.Of(key), await shell);
        foreach ((string fragmentId, Task<string> outcome) in fragments)
        {
            FragmentRevocation(logger, app.AppId, fragmentId, SessionRef.Of(key), await outcome);
        }
    }

    /// <summary>
    /// The revocation, and how the provider answered it, as a word fit for a log line: <c>revoked</c>, or
    /// why not. The token library's own revoke call answers nothing (4.2.0: it returns no result and only
    /// logs a failure), and ADR-0017 decision 4 asks for the provider's answer to be checked, so the call
    /// is put together from the library's parts instead: its configuration service supplies the
    /// endpoint from discovery, the client and the backchannel for the set's scheme, and its protocol
    /// client (<c>Duende.IdentityModel</c>'s <c>RevokeTokenAsync</c>, the call the library makes itself)
    /// sends the request and reads the answer. Nothing of the protocol is written here. A fragment's
    /// scheme carries the fragment's client and the shell's issuer, whose credential style governs
    /// (ADR-0017 decision 2: Cognito authenticates a client with a secret by a Basic header only).
    /// </summary>
    private async Task<string> RevokeAsync(AppDefinition app, string scheme, string refreshToken, CancellationToken deadline)
    {
        try
        {
            OpenIdConnectClientConfiguration client = await configurations.GetOpenIdConnectConfigurationAsync(Scheme.Parse(scheme), deadline);
            if (client.RevocationEndpoint is not { } endpoint || client.HttpClient is not { } backchannel)
            {
                return "no_revocation_endpoint";
            }

            using TokenRevocationRequest request = new()
            {
                Address = endpoint.ToString(),
                ClientId = client.ClientId.ToString(),
                ClientSecret = client.ClientSecret?.ToString(),
                ClientCredentialStyle = ProfileTokenEndpoints.LibraryStyle(styles.Of(app)),
                Token = refreshToken,
                TokenTypeHint = OidcConstants.TokenTypes.RefreshToken,
            };
            TokenRevocationResponse response = await backchannel.RevokeTokenAsync(request, deadline);
            return !response.IsError ? "revoked"
                : response.HttpStatusCode != 0 ? "http_" + (int)response.HttpStatusCode
                : "unreachable";
        }
        catch (OperationCanceledException)
        {
            return "deadline_exceeded";
        }
        catch (HttpRequestException e)
        {
            return e.StatusCode is { } status ? "http_" + (int)status : "unreachable";
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Logout for app {AppId} is removing session {SessionRef}; revoking its refresh token: {Outcome}.")]
    private static partial void Revocation(ILogger logger, string appId, string sessionRef, string outcome);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Logout for app {AppId} is removing session {SessionRef}; revoking the refresh token of fragment {FragmentId}: {Outcome}.")]
    private static partial void FragmentRevocation(ILogger logger, string appId, string fragmentId, string sessionRef, string outcome);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Logout for app {AppId}: removal of session {SessionRef} not confirmed; the session store's {Operation} "
            + "did not complete by StoreDeadline. The logout goes on and clears the cookies (F11).")]
    private static partial void RemovalNotConfirmed(ILogger logger, string appId, string sessionRef, string operation);
}
