namespace TestIdp;

/// <summary>
/// The switches that make the test identity provider behave in ways a real provider will not produce on
/// demand: a rotated signing key, a refused refresh, a delayed answer, an outage, a token the mock API
/// must refuse. The hermetic tier needs all of them (<c>docs/plan/04-tests.md</c>).
/// <para>
/// Every member is settable while the provider is running, so a test turns a switch and makes the next
/// request. Two exceptions are read by the client only once, when it fetches discovery:
/// <see cref="AdvertiseRevocation"/> and <see cref="TestIdentityProvider.EndSessionStyle"/>. Set those
/// before the gateway under test reads the discovery document.
/// </para>
/// </summary>
public sealed class TestIdpFaults
{
    /// <summary>Lifetime of every access token issued from now on. A negative value issues an expired
    /// token. Tests F-03 and F-10 shorten this instead of moving a clock, because a client library computes
    /// expiry from the system clock (evidence T-B10).</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Lifetime of every ID token issued from now on.</summary>
    public TimeSpan IdTokenLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a rotated-out refresh token still works. Cognito allows 0 to 60 seconds and task
    /// 1.8 configures 10, which is what F-10 runs against.</summary>
    public TimeSpan RefreshRotationGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether the refresh grant rotates. Off models a provider that returns the same refresh
    /// token for ever (evidence T-A13).</summary>
    public bool RotateRefreshTokens { get; set; } = true;

    /// <summary>Extra characters on every refresh token issued from now on, so a test can make one longer
    /// than a client library accepts. The token-management library refuses one over 4,096 characters
    /// (evidence T-B11).</summary>
    public int RefreshTokenPadding { get; set; }

    /// <summary>Whether a refresh answer carries an ID token. Off models a provider that leaves it out,
    /// which blanks the stored one and sends an empty <c>id_token_hint</c> at logout (evidence T-B13).</summary>
    public bool IdTokenOnRefresh { get; set; } = true;

    /// <summary>Whether discovery lists <c>revocation_endpoint</c>. Off models a provider that does not,
    /// where a client library's revoke call throws (evidence T-B14).</summary>
    public bool AdvertiseRevocation { get; set; } = true;

    /// <summary>When set, every refresh grant is refused with <c>invalid_grant</c> and nothing is rotated.
    /// A refusal is the one failure a client treats as "the session is over" (evidence T-A8).</summary>
    public bool RefuseRefresh { get; set; }

    /// <summary>When set, the refresh grant answers with this status and does nothing else: an outage, not
    /// a refusal. A client cannot tell the two apart from the result alone (evidence T-A8, T-A9, T-A15).</summary>
    public int? RefreshOutageStatus { get; set; }

    /// <summary>When set, revocation answers with this status and revokes nothing.</summary>
    public int? RevokeFailureStatus { get; set; }

    /// <summary>Whether revoking a refresh token also revokes the rest of its family: the token it was
    /// rotated from and the ones rotated from it. Off by default, which is what the G0 harness observed
    /// (evidence T-B18). G0 item 2 records what Cognito does.</summary>
    public bool RevokeCascadesToFamily { get; set; }

    /// <summary>When set, <c>resource</c> is accepted and ignored, so the access token carries no
    /// <c>aud</c>. It makes the token the <c>orders</c> listener must refuse for a missing audience (N-17).</summary>
    public bool IgnoreResource { get; set; }

    /// <summary>The <c>token_use</c> claim of every access token issued from now on. Set it to <c>id</c> to
    /// make a token the mock API must refuse on that claim (task 5.3, N-17).</summary>
    public string AccessTokenUse { get; set; } = "access";

    /// <summary>When set, every token is issued with this <c>iss</c> instead of the provider's own base
    /// URL: a token from the wrong issuer (N-17).</summary>
    public string? IssuerOverride { get; set; }

    /// <summary>When set, tokens are signed with a key JWKS never lists, so signature validation fails
    /// (F-08 and N-17 at the listener).</summary>
    public bool SignWithUnpublishedKey { get; set; }

    /// <summary>Whether JWKS still lists keys retired by <see cref="TestIdentityProvider.RotateSigningKey"/>.
    /// Off retires a key for real, so tokens signed before the rotation stop validating.</summary>
    public bool PublishRetiredKeys { get; set; } = true;

    /// <summary>Delay before discovery answers.</summary>
    public TimeSpan DiscoveryDelay { get; set; }

    /// <summary>Delay before JWKS answers.</summary>
    public TimeSpan JwksDelay { get; set; }

    /// <summary>Delay before the authorize endpoint answers.</summary>
    public TimeSpan AuthorizeDelay { get; set; }

    /// <summary>Delay before any token request is looked at, so parallel callers overlap (F-10).</summary>
    public TimeSpan TokenDelay { get; set; }

    /// <summary>Delay between carrying out a refresh grant and answering it. In that gap the refresh token
    /// is already rotated and the client does not know yet, which is the race F-10 covers (evidence T-B6,
    /// T-B15, T-D10).</summary>
    public TimeSpan RefreshAnswerDelay { get; set; }

    /// <summary>Delay before revocation answers.</summary>
    public TimeSpan RevokeDelay { get; set; }

    /// <summary>Delay before either end-session endpoint answers.</summary>
    public TimeSpan EndSessionDelay { get; set; }
}
