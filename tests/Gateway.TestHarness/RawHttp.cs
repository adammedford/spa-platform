using System.Collections.Immutable;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Gateway.TestHarness;

/// <summary>
/// HTTP/1.1 over a bare socket, for a request no client library will send: a <c>Host</c> header that is
/// not the URL's, a path with dot segments, backslashes or encoded slashes that <see cref="Uri"/> would
/// normalise away, a <c>TRACE</c>, an oversized header (N-08, N-13, N-20). It also bypasses any proxy
/// the environment configures, which in an agent sandbox answers on the <c>Host</c> header itself
/// (AGENTS.md).
///
/// The request goes out exactly as given; the response is read until the server closes the connection,
/// which <c>Connection: close</c> asks it to do. The body is the bytes on the wire: a chunked response is
/// not decoded.
/// </summary>
public static class RawHttp
{
    /// <summary>Sends <paramref name="request"/> as written: request line, headers, blank line and any body.</summary>
    public static async Task<RawResponse> SendAsync(Uri endpoint, string request, CancellationToken ct = default) =>
        await SendAsync(endpoint, Encoding.ASCII.GetBytes(request), ct);

    /// <summary>Sends <paramref name="request"/> as bytes, for a request that is not ASCII (a raw null byte, say).</summary>
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
            // The server answered and then reset the connection, which Kestrel may do after refusing a
            // request it did not read to the end. The answer is what the test wants.
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
        CancellationToken ct = default)
    {
        StringBuilder request = new();
        request.Append(CultureInfo.InvariantCulture, $"{method} {target} HTTP/1.1\r\n");
        request.Append(CultureInfo.InvariantCulture, $"Host: {host}\r\n");
        foreach ((string name, string value) in headers ?? [])
        {
            request.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
        }

        request.Append("Connection: close\r\n\r\n");
        return SendAsync(endpoint, request.ToString(), ct);
    }
}

/// <summary>A response read off the wire.</summary>
public sealed record RawResponse(
    int StatusCode,
    string StatusLine,
    ImmutableArray<(string Name, string Value)> Headers,
    byte[] Body)
{
    /// <summary>The body as UTF-8 text.</summary>
    public string BodyText => Encoding.UTF8.GetString(Body);

    /// <summary>Every value of one header, in order.</summary>
    public IEnumerable<string> Values(string name) =>
        Headers.Where(header => string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase)).Select(header => header.Value);

    /// <summary>One header's values joined with ", ", or null when absent.</summary>
    public string? Header(string name) => Values(name).ToArray() is { Length: > 0 } values ? string.Join(", ", values) : null;

    public override string ToString() => StatusLine;

    internal static RawResponse Parse(byte[] bytes)
    {
        int end = IndexOf(bytes, "\r\n\r\n"u8);
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

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle);
}
