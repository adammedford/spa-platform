using System.Globalization;
using System.Text;
using Gateway.TestHarness;

namespace Gateway.SecurityTests.Negative;

/// <summary>
/// The body of a raw response, with HTTP/1.1 chunked framing taken off.
///
/// <para><see cref="RawHttp"/> hands back "the bytes on the wire: a chunked response is not
/// decoded", which is the right default for a client that exists to see exactly what was sent. But
/// the gateway sends a short JSON body with no <c>Content-Length</c> wherever it writes one directly
/// — a problem document, or <c>/readyz</c>'s <c>{"revision": N}</c> — and Kestrel then chunks it, so
/// the bytes carry a hex length line and a terminator around the JSON. A test that searched for a
/// substring would not notice; a test that parses the body has to.</para>
///
/// <para>Framing only: the bytes between the markers are returned as they arrived. Nothing here
/// interprets the body.</para>
/// </summary>
internal static class RawBody
{
    /// <summary>The body as UTF-8 text, de-chunked when the response says it is chunked.</summary>
    public static string Text(RawResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Header("Transfer-Encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true
            ? Encoding.UTF8.GetString(Dechunk(response.Body))
            : response.BodyText;
    }

    /// <summary>
    /// RFC 9112 section 7.1: each chunk is a hex size, CRLF, that many bytes, CRLF, and a zero-sized
    /// chunk ends the body. Trailers after it are not part of the body and are ignored.
    /// </summary>
    private static byte[] Dechunk(byte[] body)
    {
        List<byte> decoded = [];
        int at = 0;
        while (at < body.Length)
        {
            int eol = IndexOfCrLf(body, at);
            if (eol < 0)
            {
                break;
            }

            // The size line may carry chunk extensions after a ';', which are not part of the size.
            string line = Encoding.ASCII.GetString(body, at, eol - at).Split(';')[0].Trim();
            if (!int.TryParse(line, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int size) || size <= 0)
            {
                break;
            }

            int start = eol + 2;
            if (start + size > body.Length)
            {
                break;
            }

            decoded.AddRange(body[start..(start + size)]);
            at = start + size + 2;
        }

        return [.. decoded];
    }

    private static int IndexOfCrLf(byte[] body, int from)
    {
        int at = body.AsSpan(from).IndexOf("\r\n"u8);
        return at < 0 ? -1 : from + at;
    }
}
