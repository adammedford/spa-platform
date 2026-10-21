using Microsoft.Playwright;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// Every response a browser context received from an app host, with the request the browser sent, as
/// the browser itself reports them: the deployed stack's record (#209), where there is no stand-in
/// load balancer to keep one (<see cref="TlsLoadBalancer.Exchanges"/>). A listener only observes, so
/// the requests are the browser's own, with the Fetch Metadata a route would have lost.
///
/// <para>A call CORS refused reaches Playwright as a failed request carrying only the headers the
/// page asked for, with no Fetch Metadata, no cookie and no response (seen on Chromium 153 while
/// building #211, <c>BrowserExchangesTests</c>). So the page's own DevTools Protocol session is read for
/// those: the headers the network stack sent and the status and headers it received
/// (https://chromedevtools.github.io/devtools-protocol/tot/Network/#event-requestWillBeSentExtraInfo,
/// #event-responseReceivedExtraInfo), with no body, which the browser never read.</para>
/// </summary>
public sealed class BrowserExchanges(string domain)
{
    private readonly List<Task<Exchange?>> exchanges = [];

    private readonly Dictionary<string, (string Method, Uri Url)> requested = [];
    private readonly Dictionary<string, Dictionary<string, string>> sentByNetwork = [];
    private readonly Dictionary<string, (int Status, List<KeyValuePair<string, string>> Headers)> receivedByNetwork = [];
    private readonly List<string> failed = [];
    private readonly HashSet<string> delivered = [];

    /// <summary>
    /// Starts recording the page's context's responses, and the page's failed requests, for hosts
    /// under <c>domain</c>. Call it before the page's first request.
    /// </summary>
    public async Task AttachAsync(IPage page)
    {
        page.Context.Response += (_, response) =>
        {
            lock (exchanges)
            {
                exchanges.Add(RecordAsync(response));
            }
        };

        ICDPSession network = await page.Context.NewCDPSessionAsync(page);
        network.Event("Network.requestWillBeSent").OnEvent += (_, e) => Note(e, id =>
            requested[id] = (e!.Value.GetProperty("request").GetProperty("method").GetString()!, new Uri(e.Value.GetProperty("request").GetProperty("url").GetString()!)));
        network.Event("Network.requestWillBeSentExtraInfo").OnEvent += (_, e) => Note(e, id =>
            sentByNetwork[id] = HeadersOf(e!.Value.GetProperty("headers")).GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(header => header.Value)), StringComparer.OrdinalIgnoreCase));
        network.Event("Network.responseReceivedExtraInfo").OnEvent += (_, e) => Note(e, id =>
            receivedByNetwork[id] = (e!.Value.GetProperty("statusCode").GetInt32(), HeadersOf(e.Value.GetProperty("headers"))));
        network.Event("Network.responseReceived").OnEvent += (_, e) => Note(e, id => delivered.Add(id));
        network.Event("Network.loadingFailed").OnEvent += (_, e) => Note(e, failed.Add);
        await network.SendAsync("Network.enable");
    }

    private void Note(System.Text.Json.JsonElement? e, Action<string> record)
    {
        if (e is { } value && value.TryGetProperty("requestId", out System.Text.Json.JsonElement id))
        {
            lock (exchanges)
            {
                record(id.GetString()!);
            }
        }
    }

    private static List<KeyValuePair<string, string>> HeadersOf(System.Text.Json.JsonElement headers) =>
        [.. headers.EnumerateObject().SelectMany(header => header.Value.GetString()!.Split('\n').Select(value => KeyValuePair.Create(header.Name, value)))];

    /// <summary>The status of a failed request that received no response at all.</summary>
    public const int NotReceived = 0;

    /// <summary>Every exchange recorded so far: those the browser reported, oldest first, then the failed ones.</summary>
    public async Task<IReadOnlyList<Exchange>> AllAsync()
    {
        Task<Exchange?>[] pending;
        List<Exchange> refused = [];
        lock (exchanges)
        {
            pending = [.. exchanges];
            // A failed request the page was handed a response for (a frame the browser then refused to
            // render) is among Playwright's already. One with no network record of its own never left the
            // browser (the POST behind a refused preflight), so it is no exchange with the cell.
            foreach (string id in failed.Where(id => !delivered.Contains(id)))
            {
                if (requested.TryGetValue(id, out (string Method, Uri Url) request) && request.Url.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)
                    && sentByNetwork.TryGetValue(id, out Dictionary<string, string>? sent))
                {
                    (int status, List<KeyValuePair<string, string>> headers) = receivedByNetwork.TryGetValue(id, out var received) ? received : (NotReceived, []);
                    refused.Add(new Exchange(request.Method, request.Url, sent, status, headers, []));
                }
            }
        }

        return [.. (await Task.WhenAll(pending)).OfType<Exchange>(), .. refused];
    }

    private static Dictionary<string, string> Headers(IReadOnlyList<Header> sent) =>
        sent.GroupBy(header => header.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(header => header.Value)), StringComparer.OrdinalIgnoreCase);

    private async Task<Exchange?> RecordAsync(IResponse response)
    {
        Uri url = new(response.Url);
        if (!url.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
        {
            return null; // The provider's own pages are not the cell's exchanges.
        }

        try
        {
            IReadOnlyList<Header> received = await response.HeadersArrayAsync();
            IReadOnlyList<Header> sent = await response.Request.HeadersArrayAsync();
            byte[] body;
            try
            {
                body = await response.BodyAsync();
            }
            catch (PlaywrightException)
            {
                body = []; // A redirect's body is not kept by the browser.
            }

            return new Exchange(
                response.Request.Method,
                url,
                Headers(sent),
                response.Status,
                [.. received.Select(header => KeyValuePair.Create(header.Name, header.Value))],
                body);
        }
        catch (PlaywrightException)
        {
            return null; // The page closed before the response could be read.
        }
    }
}
