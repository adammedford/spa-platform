using System.Net;
using Gateway.TestHarness;
using Platform.Abstractions;
using TestIdp;

namespace Gateway.IntegrationTests.Fragments;

/// <summary>
/// F-48 (#1529; the behaviour #1544 built, contracts/http-contract.md -> "Logout", contracts/telemetry.md ->
/// Orphaned successor line), composed: a refresh whose write finds the session gone revokes the successor it
/// obtained. Two gateway hosts over one store (<see cref="TwoHostShell"/>): B refreshes orders' set, and in
/// the hook before B's versioned replace, after the provider issued the successor, A's logout revokes the sets
/// and removes the record, so B's write ends <c>missing</c>, as N-38 races it on the cell. Workstream 3
/// proved the same at its seam (tests/Platform.Identity.Tests/OrphanedSuccessorTests.cs).
/// </summary>
public sealed class OrphanedSuccessorTests
{
    private const string OrphanMarker = " found the session gone at its write; revoking the successor it obtained: ";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The orphaned successor is revoked at TestIdp by orders' own client in the profile's credential style (the
    /// form body under <c>standard</c>, a Basic header under <c>cognito</c>), one orphaned successor line reads
    /// <c>revoked</c>, and the call answers 401 <c>unauthenticated</c>. A write that ends <c>superseded</c>
    /// (Trace 2(i)) or <c>exhausted</c> (Trace 1's variant past the write deadline) leaves the record holding
    /// the set's grant, so it revokes nothing and writes no such line.
    /// </summary>
    [Theory]
    [InlineData("missing", "standard", ClientAuth.Post)]
    [InlineData("missing", "cognito", ClientAuth.Basic)]
    [InlineData("superseded", "standard", ClientAuth.Post)]
    [InlineData("exhausted", "standard", ClientAuth.Post)]
    public async Task G9_a_refresh_whose_write_finds_the_session_gone_revokes_its_successor(string write, string profile, ClientAuth style)
    {
        // Mutation that turns it red: a gateway that drops the orphaned successor unrevoked (AccessTokenSource.GetAsync
        // without its RevokeOrphanAsync call) leaves F1 redeemable and writes no orphan line, so both missing rows fail.
        // Mutation: revoking on every write that does not land fails the superseded and exhausted rows.
        await using TwoHostShell cell = await TwoHostShell.StartAsync(Ct, profile: profile);
        if (write == "missing")
        {
            await MissingAsync(cell, style);
        }
        else
        {
            await LeftInTheRecordAsync(cell, write);
        }

        string all = string.Join("\n", cell.Harness.Log.Lines.Select(line => line.Text));
        Assert.DoesNotContain(cell.Key, all, StringComparison.Ordinal);
        Assert.All(cell.Harness.Idp.RefreshGrants, grant => Assert.DoesNotContain(grant.RefreshTokenIssued!, all, StringComparison.Ordinal));
    }

    private static async Task MissingAsync(TwoHostShell cell, ClientAuth style)
    {
        BrowserResponse? logout = null;
        cell.B.SessionStore!.BeforeReplace = async attempt =>
        {
            if (attempt == 1)
            {
                logout = await cell.LogoutAsync(cell.A, Ct);
            }
        };

        (BrowserResponse late, string? sent) = await cell.CallAsync(cell.B, "orders", Ct);

        Assert.Equal(HttpStatusCode.Redirect, logout?.StatusCode);
        Assert.Equal((HttpStatusCode.Unauthorized, "unauthenticated"), (late.StatusCode, TwoHostShell.Code(late)));
        Assert.Null(sent);
        Assert.Null(await cell.RecordAsync(Ct));

        TokenGrant successor = Assert.Single(cell.Harness.Idp.RefreshGrants);
        Assert.Equal(cell.Orders.ClientId, successor.ClientId);
        Revocation revoked = Assert.Single(cell.Harness.Idp.Revocations, r => r.Token == successor.RefreshTokenIssued);
        Assert.Equal((cell.Orders.ClientId, style), (revoked.ClientId, revoked.Auth));
        Assert.True(revoked is { Known: true, Revoked: > 0 }, revoked.ToString());
        Assert.False(cell.Harness.Idp.IsRedeemable(successor.RefreshTokenIssued));

        string line = Assert.Single(OrphanLines(cell));
        Assert.Equal(
            $"The refresh for app shell, fragment orders, session {SessionRef.Of(cell.Key)}{OrphanMarker}revoked.",
            line);
        Assert.Contains(cell.Harness.Log.Lines, l => l.Text.EndsWith($"session {SessionRef.Of(cell.Key)}: ended missing after 1 versioned replaces.", StringComparison.Ordinal));
    }

    /// <summary>Trace 2(i) for <c>superseded</c>, Trace 1's variant for <c>exhausted</c>: the refresh's own successor is revoked by nobody.</summary>
    private static async Task LeftInTheRecordAsync(TwoHostShell cell, string write)
    {
        HostSessionStore writer = write == "superseded" ? cell.A.SessionStore! : cell.B.SessionStore!;
        int signIn = writer.Replaces;
        writer.BeforeReplace = async attempt =>
        {
            if (attempt == signIn + 1)
            {
                if (write == "superseded")
                {
                    await cell.FallbackAsync(cell.B, Ct);
                }
                else
                {
                    await cell.EchoAsync(cell.A, fragmentId: null, Ct);
                    cell.Clock.Advance(TimeSpan.FromSeconds(11));
                }
            }
        };

        (BrowserResponse answer, _) = await cell.CallAsync(write == "superseded" ? cell.A : cell.B, "orders", Ct);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains(cell.Harness.Log.Lines, l => l.Text.Contains($"fragment orders, session {SessionRef.Of(cell.Key)}: ended {write} after", StringComparison.Ordinal));
        TokenGrant refreshed = Assert.Single(cell.Harness.Idp.RefreshGrants, g => g.ClientId == cell.Orders.ClientId);
        Assert.Empty(cell.Harness.Idp.Revocations);
        Assert.True(cell.Harness.Idp.IsRedeemable(refreshed.RefreshTokenIssued));
        Assert.Empty(OrphanLines(cell));
        Assert.NotNull(await cell.RecordAsync(Ct));
    }

    private static string[] OrphanLines(TwoHostShell cell) =>
        [.. cell.Harness.Log.Lines.Select(line => line.Text).Where(text => text.Contains(OrphanMarker, StringComparison.Ordinal))];
}
