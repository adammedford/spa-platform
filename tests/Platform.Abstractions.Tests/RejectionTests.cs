using Microsoft.AspNetCore.Http;
using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Abstractions.Tests;

/// <summary>
/// <see cref="Rejection"/> through its interface, on a <see cref="DefaultHttpContext"/> with and
/// without Platform.Testing's <see cref="SettableRequestTelemetryFeature"/>, which stands in for
/// <c>UsePlatformTelemetry</c>. The expected statuses and codes are contract literals.
///
/// The "no feature installed" cases are where the six per-stage proofs of the null-conditional write
/// move when their stages adopt this module (#142, Testing Decisions, "Retire"): a stage's own test
/// project runs without <c>UsePlatformTelemetry</c>, so a missing feature must cost nothing.
/// </summary>
public sealed class RejectionTests
{
    [Theory]
    [InlineData("host.unknown", 404, "not_found")]
    [InlineData("path.invalid", 400, "invalid_request")]
    [InlineData("path.reserved", 404, "not_found")]
    [InlineData("gate.cookie-name", 400, "invalid_request")]
    [InlineData("gate.fetch-site", 403, "origin_rejected")]
    [InlineData("gate.origin", 403, "origin_rejected")]
    [InlineData("gate.navigation", 403, "origin_rejected")]
    [InlineData("csrf.token", 403, "csrf_failed")]
    [InlineData("auth.session", 401, "unauthenticated")]
    [InlineData("api.unknown", 404, "unknown_api")]
    public async Task Ending_with_a_feature_notes_the_rule_and_writes_its_problem_body(string rule, int status, string code)
    {
        // Mutation: add a "detail" property to the body, or skip the feature write.
        (DefaultHttpContext context, SettableRequestTelemetryFeature feature) = WithFeature();

        await Rejection.EndAsync(context, rule);

        Assert.Equal(rule, feature.Rule);
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        Contexts.AssertProblemBody(context, status, code);
    }

    [Theory]
    [InlineData("gate.origin", 403, "origin_rejected")]
    [InlineData("host.unknown", 404, "not_found")]
    [InlineData("csrf.token", 403, "csrf_failed")]
    public async Task Ending_with_no_feature_installed_writes_the_same_status_and_body(string rule, int status, string code)
    {
        // Mutation: make the feature write unconditional (Features.Get<...>()!.Rule = rule).
        DefaultHttpContext context = Contexts.WithBody();

        await Rejection.EndAsync(context, rule);

        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        Contexts.AssertProblemBody(context, status, code);
    }

    [Fact]
    public async Task The_feature_is_written_before_the_body()
    {
        // Change request issue-145, section 3: a stage host reading the recorded feature must not
        // race the body back to the client. Mutation: write the body, then note the rule.
        DefaultHttpContext context = new();
        SettableRequestTelemetryFeature feature = new();
        context.Features.Set<IRequestTelemetryFeature>(feature);
        ObservingStream body = new(() => feature.Rule);
        context.Response.Body = body;

        await Rejection.EndAsync(context, RuleIds.GateOrigin);

        Assert.True(body.Length > 0);
        Assert.Equal(RuleIds.GateOrigin, body.RuleAtFirstWrite);
    }

    [Fact]
    public async Task Ending_a_method_rejection_sets_Allow_and_ends_under_gate_method()
    {
        // Mutation: drop the Allow line. RFC 9110 section 15.5.6: a 405 must carry Allow.
        (DefaultHttpContext context, SettableRequestTelemetryFeature feature) = WithFeature();

        await Rejection.EndMethodAsync(context, "GET", "HEAD");

        Assert.Equal("GET, HEAD", context.Response.Headers.Allow.ToString());
        Assert.Equal("gate.method", feature.Rule);
        Assert.Equal(405, context.Response.StatusCode);
        Contexts.AssertProblemBody(context, 405, "method_not_allowed");
    }

    [Fact]
    public async Task Ending_a_method_rejection_with_no_feature_installed_still_sets_Allow()
    {
        DefaultHttpContext context = Contexts.WithBody();

        await Rejection.EndMethodAsync(context, "GET");

        Assert.Equal("GET", context.Response.Headers.Allow.ToString());
        Contexts.AssertProblemBody(context, 405, "method_not_allowed");
    }

    [Fact]
    public async Task A_method_rejection_with_no_allowed_method_is_refused_before_anything_is_written()
    {
        // Mutation: drop the emptiness check, and a 405 ships with an empty Allow.
        (DefaultHttpContext context, SettableRequestTelemetryFeature feature) = WithFeature();

        await Assert.ThrowsAsync<ArgumentException>(() => Rejection.EndMethodAsync(context));
        await Assert.ThrowsAsync<ArgumentException>(() => Rejection.EndMethodAsync(context, "GET", ""));

        Assert.Null(feature.Rule);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Allow"));
    }

    [Fact]
    public void Noting_sets_the_feature_and_writes_no_response()
    {
        // Mutation: make Note write the body. Note is for a rejection another writer ends.
        (DefaultHttpContext context, SettableRequestTelemetryFeature feature) = WithFeature();

        Rejection.Note(context, RuleIds.AuthCallback);

        Assert.Equal("auth.callback", feature.Rule);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Null(context.Response.ContentType);
        Assert.Equal("", Contexts.Body(context));
    }

    [Fact]
    public void Noting_with_no_feature_installed_costs_nothing()
    {
        DefaultHttpContext context = Contexts.WithBody();

        Rejection.Note(context, RuleIds.AuthSession);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("", Contexts.Body(context));
    }

    [Theory]
    [InlineData("gate.method", "EndMethodAsync")]
    [InlineData("auth.callback", "remote-failure")]
    public async Task Ending_under_an_id_with_another_entry_throws_naming_it(string rule, string named)
    {
        // Mutation: drop either throw from EndAsync. (Adding auth.callback to Rejection.Rows is a
        // different mutation, and the table test catches it: the contract's cell for it is a dash.)
        (DefaultHttpContext context, SettableRequestTelemetryFeature feature) = WithFeature();

        InvalidOperationException thrown =
            await Assert.ThrowsAsync<InvalidOperationException>(() => Rejection.EndAsync(context, rule));

        Assert.Contains(named, thrown.Message, StringComparison.Ordinal);
        Assert.Null(feature.Rule);
        Assert.Equal(200, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("host.Unknown")]
    [InlineData("gate.unknown")]
    [InlineData("")]
    public async Task Ending_or_noting_under_an_id_the_contract_does_not_list_throws(string rule)
    {
        (DefaultHttpContext context, SettableRequestTelemetryFeature feature) = WithFeature();

        await Assert.ThrowsAsync<ArgumentException>(() => Rejection.EndAsync(context, rule));
        Assert.Throws<ArgumentException>(() => Rejection.Note(context, rule));

        Assert.Null(feature.Rule);
        Assert.Equal(200, context.Response.StatusCode);
    }

    private static (DefaultHttpContext Context, SettableRequestTelemetryFeature Feature) WithFeature()
    {
        DefaultHttpContext context = Contexts.WithBody();
        SettableRequestTelemetryFeature feature = new();
        context.Features.Set<IRequestTelemetryFeature>(feature);
        return (context, feature);
    }

    /// <summary>A body that records what the feature held when the first byte arrived.</summary>
    private sealed class ObservingStream(Func<string?> rule) : MemoryStream
    {
        private bool written;

        public string? RuleAtFirstWrite { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Observe();
            base.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Observe();
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Observe();
            return base.WriteAsync(buffer, cancellationToken);
        }

        private void Observe()
        {
            if (!written)
            {
                written = true;
                RuleAtFirstWrite = rule();
            }
        }
    }
}
