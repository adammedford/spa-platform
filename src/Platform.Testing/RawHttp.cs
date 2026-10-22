using System.Collections.Immutable;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Platform.Testing;

/// <summary>
/// HTTP/1.1 over a bare socket, for a request no client library will send: a <c>Host</c> header that is
/// not the URL's, a path with dot segments, backslashes or encoded slashes that <see cref="Uri"/> would
/// normalise away, a <c>TRACE</c>, an oversized header (N-08, N-13, N-20). It also bypasses any proxy
/// the environment configures, which in an agent sandbox answers on the <c>Host</c> header itself
/// (AGENTS.md -> Build and test).
///
/// The request goes out exactly as given; the response is read until the server closes the connection,
/// which <c>Connection: close</c> asks it to do. These are the gateway test harness's endpoint-taking
/// forms, kept so a composed negative test sends to the gateway's address with no stage host;
/// <see cref="StageHost.RawAsync(string, CancellationToken)"/> is this sender bound to a stage host's
/// address (change request issue-145, section 5).
/// </summary>
public static class RawHttp
{
    /// <summary>Sends <paramref name="request"/> as written: request line, headers, blank line and any body.</summary>
    public static async Task<RawResponse> SendAsync(Uri endpoint, string request, CancellationToken ct = default) =>
        await SendAsync(endpoint, Encoding.ASCII.GetBytes(request), ct);

    /// <summary>Sends <paramref name="request"/> as bytes, for a request that is not ASCII (a raw null byte, say).</summary>
    /// <exception cref="InvalidDataException">The server closed the connection without answering.</exception>
    public static async Task<RawResponse> SendAsync(Uri endpoint, byte[] request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using TcpClient client = new();
        await client.ConnectAsync(endpoint.Host, endpoint.Port, ct);
        await using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(request, ct);
        await stream.FlushAsync(ct);

        using MemoryStream received = new();
        try
        {
            await stream.CopyToAsync(received, ct);
        }
        catch (IOException) when (received.Length > 0)
        {
            // The server answered and then reset the connection. Kestrel closes a connection whose
            // request body it could not drain after answering
            // (https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/request-draining),
            // and a close with received data still unread sends RST, not FIN (RFC 1122, section
            // 4.2.2.13), which this side reads as an IOException. The answer is what the test wants.
            // Kept and deliberately untested: no deterministic way to make Kestrel reset after
            // answering was found (#140, Testing Decisions).
        }

        return RawResponse.Parse(received.ToArray());
    }

    /// <summary>
    /// A request with <paramref name="method"/> on <paramref name="target"/> (sent exactly as written),
    /// the given <c>Host</c>, any further headers in order, and <c>Connection: close</c>.
    /// </summary>
    public static Task<RawResponse> SendAsync(
        Uri endpoint,
        string method,
        string target,
        string host,
        IEnumerable<(string Name, string Value)>? headers = null,
        CancellationToken ct = default) =>
        SendAsync(endpoint, Head(method, target, host, headers), ct);

    internal static string Head(string method, string target, string host, IEnumerable<(string Name, string Value)>? headers)
    {
        StringBuilder request = new();
        request.Append(CultureInfo.InvariantCulture, $"{method} {target} HTTP/1.1\r\n");
        request.Append(CultureInfo.InvariantCulture, $"Host: {host}\r\n");
        foreach ((string name, string value) in headers ?? [])
        {
            request.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
        }

        request.Append("Connection: close\r\n\r\n");
        return request.ToString();
    }
}

/// <summary>
/// A response read off the wire. <see cref="Headers"/> keeps the order and the repeats, because a
/// security test exists to see the second <c>Set-Cookie</c>; <see cref="Body"/> is the bytes on the
/// wire, framing included, and <see cref="BodyText"/> is the body with chunked framing taken off.
/// </summary>
public sealed record RawResponse(
    int StatusCode,
    string StatusLine,
    ImmutableArray<(string Name, string Value)> Headers,
    byte[] Body)
{
    /// <summary>
    /// The body as UTF-8 text, de-chunked when the response says it is chunked. Kestrel chunks a body
    /// written with no <c>Content-Length</c>, which is how the gateway writes a problem document or
    /// <c>/readyz</c>'s revision, so a test that parses a body reads this.
    /// </summary>
    /// <exception cref="InvalidDataException">The response says it is chunked, and its framing is malformed or cut off.</exception>
    public string BodyText =>
        Header("Transfer-Encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true
            ? Encoding.UTF8.GetString(Dechunk(Body))
            : Encoding.UTF8.GetString(Body);

    /// <summary>Every value of one header, in order.</summary>
    public IEnumerable<string> Values(string name) =>
        Headers.Where(header => string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase)).Select(header => header.Value);

    /// <summary>One header's values joined with ", ", or null when absent.</summary>
    public string? Header(string name) => Values(name).ToArray() is { Length: > 0 } values ? string.Join(", ", values) : null;

    public override string ToString() => StatusLine;

    internal static RawResponse Parse(byte[] bytes)
    {
        int end = bytes.AsSpan().IndexOf("\r\n\r\n"u8);
        if (end < 0)
        {
            throw new InvalidDataException(bytes.Length == 0
                ? "the server closed the connection without answering."
                : "the response has no end of headers.");
        }

        string[] lines = Encoding.ASCII.GetString(bytes, 0, end).Split("\r\n");
        string statusLine = lines[0];
        string[] parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int status))
        {
            throw new InvalidDataException($"not an HTTP status line: '{statusLine}'.");
        }

        ImmutableArray<(string, string)> headers = [.. lines.Skip(1)
            .Where(line => line.Contains(':', StringComparison.Ordinal))
            .Select(line => (line[..line.IndexOf(':', StringComparison.Ordinal)].Trim(), line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim()))];

        return new RawResponse(status, statusLine, headers, bytes[(end + 4)..]);
    }

    /// <summary>
    /// RFC 9112 section 7.1: each chunk is a hex size, CRLF, that many bytes, CRLF, and a zero-sized
    /// chunk ends the body. Trailers after it are not part of the body and are ignored. Framing only:
    /// the bytes between the markers are returned as they arrived.
    ///
    /// Framing that does not parse, or that ends before the last chunk, throws rather than returning
    /// what was decoded so far: a test that parsed part of a problem document as the whole would pass
    /// or fail on bytes the server never finished sending. An empty body is empty, framing or not: a
    /// <c>HEAD</c> answer carries the header and no body.
    /// </summary>
    /// <exception cref="InvalidDataException">The chunked framing is malformed or cut off.</exception>
    private static byte[] Dechunk(byte[] body)
    {
        List<byte> decoded = [];
        int at = 0;
        while (body.Length > 0)
        {
            int found = body.AsSpan(at).IndexOf("\r\n"u8);
            if (found < 0)
            {
                throw new InvalidDataException("the chunked body ends before its last chunk.");
            }

            int eol = at + found;

            // The size line may carry chunk extensions after a ';', which are not part of the size.
            string line = Encoding.ASCII.GetString(body, at, eol - at).Split(';')[0].Trim();
            if (!int.TryParse(line, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int size) || size < 0)
            {
                throw new InvalidDataException($"not a chunk size: '{line}'.");
            }

            if (size == 0)
            {
                break;
            }

            int start = eol + 2;
            if ((long)start + size + 2 > body.Length || !body.AsSpan(start + size, 2).SequenceEqual("\r\n"u8))
            {
                throw new InvalidDataException("a chunk is shorter than its size says.");
            }

            decoded.AddRange(body[start..(start + size)]);
            at = start + size + 2;
        }

        return [.. decoded];
    }
}
