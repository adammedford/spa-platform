using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Platform.Abstractions;

namespace Platform.Abstractions.Tests;

/// <summary>
/// <see cref="Problems"/> through its interface: the status a code derives, and the body
/// <see cref="Problems.WriteAsync"/> writes on a <see cref="DefaultHttpContext"/>. The expected
/// values are the literals of contracts/http-contract.md -> Errors, not the type's own constants.
/// </summary>
public sealed class ProblemsTests
{
    [Theory]
    [InlineData("invalid_request", 400)]
    [InlineData("unauthenticated", 401)]
    [InlineData("session_ended", 401)]
    [InlineData("origin_rejected", 403)]
    [InlineData("csrf_failed", 403)]
    [InlineData("not_found", 404)]
    [InlineData("unknown_api", 404)]
    [InlineData("method_not_allowed", 405)]
    [InlineData("upstream_unavailable", 502)]
    [InlineData("idp_unavailable", 503)]
    [InlineData("upstream_timeout", 504)]
    public void A_code_s_status_is_the_Errors_table_s(string code, int status)
    {
        Assert.Equal(status, Problems.StatusOf(code));
    }

    [Theory]
    [InlineData("Not_Found")]
    [InlineData("not_found ")]
    [InlineData("")]
    [InlineData("internal_error")]
    public void A_code_the_table_does_not_list_has_no_status(string code)
    {
        // Mutation: compare ignoring case, or fall back to 500. A code outside the contract would
        // then reach a client.
        Assert.Throws<ArgumentException>(() => Problems.StatusOf(code));
    }

    [Fact]
    public async Task The_body_is_exactly_status_and_code_as_problem_json()
    {
        // Mutation: add a "detail" or "title" property to the body.
        DefaultHttpContext context = Contexts.WithBody();

        await Problems.WriteAsync(context, Problems.UpstreamTimeout);

        Assert.Equal(504, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        Contexts.AssertProblemBody(context, 504, "upstream_timeout");
    }

    [Fact]
    public async Task Nothing_from_the_request_reaches_the_body()
    {
        // http-contract.md -> Errors: nothing that describes the request. A request carrying a path,
        // query, header and cookie still gets the two fields and nothing else.
        DefaultHttpContext context = Contexts.WithBody();
        context.Request.Path = "/api/orders/canary-path";
        context.Request.QueryString = new QueryString("?q=canary-query");
        context.Request.Headers.Cookie = "__Host-canary=canary-cookie";
        context.Request.Headers["X-Canary"] = "canary-header";

        await Problems.WriteAsync(context, Problems.NotFound);

        string body = Contexts.Body(context);
        Assert.DoesNotContain("canary", body, StringComparison.Ordinal);
        Assert.Equal("""{"status":404,"code":"not_found"}""", body);
    }

    [Fact]
    public async Task An_unlisted_code_writes_nothing()
    {
        // The throw comes before the status is set, so a wrong call leaves the response untouched.
        DefaultHttpContext context = Contexts.WithBody();

        await Assert.ThrowsAsync<ArgumentException>(() => Problems.WriteAsync(context, "internal_error"));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("", Contexts.Body(context));
    }

    [Fact]
    public async Task The_write_is_cancelled_with_the_request()
    {
        // Mutation: write without context.RequestAborted. A client that has gone away would then hold
        // the write open.
        using CancellationTokenSource aborted = new();
        await aborted.CancelAsync();
        DefaultHttpContext context = Contexts.WithBody();
        context.RequestAborted = aborted.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Problems.WriteAsync(context, Problems.NotFound));
    }
}

/// <summary>A <see cref="DefaultHttpContext"/> whose response body can be read back.</summary>
internal static class Contexts
{
    public static DefaultHttpContext WithBody() => new() { Response = { Body = new MemoryStream() } };

    public static string Body(HttpContext context)
    {
        MemoryStream body = (MemoryStream)context.Response.Body;
        return System.Text.Encoding.UTF8.GetString(body.ToArray());
    }

    /// <summary>The body parses as JSON whose properties are exactly <c>status</c> and <c>code</c>.</summary>
    public static void AssertProblemBody(HttpContext context, int status, string code)
    {
        using JsonDocument body = JsonDocument.Parse(Body(context));

        Assert.Equal(
            ["code", "status"],
            body.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(status, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }
}
