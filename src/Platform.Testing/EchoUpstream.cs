using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace Platform.Testing;

/// <summary>One request as the upstream received it.</summary>
public sealed record EchoedRequest(
    string Method,
    string Path,
    string Query,
    ImmutableDictionary<string, ImmutableArray<string>> Headers,
    string Body)
{
    /// <summary>For the JSON round trip when the upstream echoes itself back.</summary>
    [JsonConstructor]
    public EchoedRequest() : this("GET", "/", string.Empty, Empty, string.Empty)
    {
    }

    private static ImmutableDictionary<string, ImmutableArray<string>> Empty { get; } =
        ImmutableDictionary.Create<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>An arranged response, in place of the default echo.</summary>
public sealed record EchoResponse(HttpStatusCode Status, string Body, string ContentType = "text/plain");

/// <summary>
/// An upstream API that records what reached it and echoes it back. The proxy tests in workstream 4
/// assert on <see cref="Requests"/>: a header the gateway was supposed to strip must not be there,
/// and one it was supposed to set must be.
///
/// Two forms, one recorder. <see cref="Handler"/> plugs into an <c>HttpClient</c> or YARP's message
/// invoker, with no socket. <see cref="ListenAsync"/> puts the same upstream behind a real loopback
/// listener, which is what task 6.1's harness needs; a header only becomes wrong once it has been
/// serialized onto a wire, so the composed suites use that form.
/// </summary>
public sealed class EchoUpstream
{
    private readonly ConcurrentQueue<EchoedRequest> requests = new();

    /// <summary>Every request that arrived, in order.</summary>
    public IReadOnlyList<EchoedRequest> Requests => [.. requests];

    /// <summary>Arrange a response. Null echoes the request back as JSON with 200.</summary>
    public Func<EchoedRequest, EchoResponse>? Respond { get; set; }

    /// <summary>The upstream as a message handler, with no socket.</summary>
    public HttpMessageHandler Handler => new EchoHandler(this);

    /// <summary>The upstream behind a real loopback listener. Dispose it to stop listening.</summary>
    public async Task<EchoUpstreamListener> ListenAsync(CancellationToken ct) =>
        await EchoUpstreamListener.StartAsync(this, ct);

    internal EchoedRequest Record(
        string method,
        string path,
        string query,
        ImmutableDictionary<string, ImmutableArray<string>> headers,
        string body)
    {
        EchoedRequest received = new(method, path, query, headers, body);
        requests.Enqueue(received);
        return received;
    }

    internal EchoResponse ResponseFor(EchoedRequest received) => Respond?.Invoke(received)
        ?? new EchoResponse(HttpStatusCode.OK, Body: string.Empty);

    private sealed class EchoHandler(EchoUpstream upstream) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("the request has no URI.");

            ImmutableDictionary<string, ImmutableArray<string>>.Builder headers =
                ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                headers[header.Key] = [.. header.Value];
            }

            string body = string.Empty;
            if (request.Content is not null)
            {
                foreach (KeyValuePair<string, IEnumerable<string>> header in request.Content.Headers)
                {
                    headers[header.Key] = [.. header.Value];
                }

                body = await request.Content.ReadAsStringAsync(ct);
            }

            EchoedRequest received = upstream.Record(
                request.Method.Method, uri.AbsolutePath, uri.Query, headers.ToImmutable(), body);

            EchoResponse arranged = upstream.ResponseFor(received);

            return arranged.Body.Length == 0 && arranged.Status == HttpStatusCode.OK
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(received) }
                : new HttpResponseMessage(arranged.Status)
                {
                    Content = new StringContent(arranged.Body, Encoding.UTF8, arranged.ContentType),
                };
        }
    }
}
