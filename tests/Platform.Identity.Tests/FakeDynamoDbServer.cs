using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Platform.Identity.Tests;

/// <summary>
/// DynamoDB's JSON 1.0 protocol for the five operations the session store and the refresh lease send,
/// in memory, behind a real loopback listener, in <c>FakeS3Server</c>'s shape (change request P2-1,
/// Decision 6). The SDK's own client talks to it over HTTP, so a test proves the bytes the store sends
/// and how the SDK surfaces each answer, rather than what a hand-written stub assumes.
///
/// <para>The protocol: every call is <c>POST /</c> with <c>Content-Type: application/x-amz-json-1.0</c>
/// and <c>X-Amz-Target: DynamoDB_20120810.&lt;Operation&gt;</c>, and an error is a 400 whose body names
/// <c>__type</c> (https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Programming.LowLevelAPI.html).
/// No response carries <c>x-amz-crc32</c> unless a test sets <see cref="Crc32"/>: the SDK checks the CRC
/// only when the header is present (shown at the pin by <c>DynamoDbProtocolTests</c>), so a fake that
/// computed none would otherwise fail every call.</para>
///
/// <para>What it evaluates is exactly what the store sends and nothing else: <c>attribute_exists</c>,
/// <c>attribute_not_exists</c>, <c>=</c>, <c>&lt;</c> and <c>&gt;</c> on <c>S</c> and <c>N</c>, joined by
/// <c>AND</c> and <c>OR</c> (<c>AND</c> binding tighter, as DynamoDB's precedence has it:
/// https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.OperatorsAndFunctions.html).
/// Anything else is a 400 <c>ValidationException</c> naming what it does not evaluate. A <c>GetItem</c>
/// without <c>ConsistentRead</c> is refused, and so is a consistent <c>Query</c> on the index, which the
/// real table refuses. Its honest limit is P2-1 item 5's: it proves the expressions' meaning only as far
/// as its author read the documentation; the real table at G6 proves the rest.</para>
///
/// <para>A test schedules a <see cref="Fault"/> for the next call of an operation: the write commits and
/// the response is broken mid-body, the call fails with a 500 before it commits, or the call is held,
/// before or after it commits, until the test releases it or the client gives up.</para>
/// </summary>
internal sealed class FakeDynamoDbServer : IAsyncDisposable
{
    public const string Table = "spa-platform-test-sessions";

    private const string Prefix = "com.amazonaws.dynamodb.v20120810#";

    private readonly WebApplication app;
    private readonly Dictionary<string, JsonObject> items = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<SeenRequest> requests = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Fault>> faults = new(StringComparer.Ordinal);
    private readonly Lock writes = new();
    private bool stopped;

    private FakeDynamoDbServer(WebApplication app) => this.app = app;

    public enum Fault
    {
        /// <summary>Applies the call, then starts a 200 and breaks the connection before the body ends.</summary>
        CommitThenDrop,

        /// <summary>Answers 500 <c>InternalServerError</c> without applying anything.</summary>
        FailBeforeCommit,

        /// <summary>Holds the call, unapplied, until <see cref="Release"/> or the client goes away.</summary>
        HoldBeforeCommit,

        /// <summary>Applies the call, then holds the response until <see cref="Release"/> or the client goes away.</summary>
        HoldAfterCommit,

        /// <summary>
        /// Answers 500 at once and applies the call only after the next <c>GetItem</c> has been answered: a
        /// write still in flight inside the table when the store re-read, which lands before its retry.
        /// </summary>
        CommitAfterNextRead,

        /// <summary>
        /// Plants another writer's item under the <c>PutItem</c>'s own <c>pk</c> just before evaluating it:
        /// the collision a fresh 256-bit key never meets, staged.
        /// </summary>
        ForeignItemFirst,
    }

    private JsonObject? lateCommit;

    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>Every request, in order: the operation its target named and its JSON body.</summary>
    public IReadOnlyList<SeenRequest> Requests => [.. requests];

    /// <summary>The operations of <see cref="Requests"/>, in order.</summary>
    public IReadOnlyList<string> Operations => [.. requests.Select(r => r.Operation)];

    /// <summary>Completes when a held call has arrived; a test waits on it before moving the clock.</summary>
    public TaskCompletionSource Held { get; private set; } = NewSignal();

    /// <summary>Lets a held call finish.</summary>
    public TaskCompletionSource Release { get; private set; } = NewSignal();

    /// <summary>
    /// When set, sent as <c>x-amz-crc32</c> on every response, for the <c>DynamoDbProtocolTests</c> test
    /// that shows the SDK refuses a wrong one.
    /// </summary>
    public string? Crc32 { get; set; }

    public static async Task<FakeDynamoDbServer> StartAsync(CancellationToken ct)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        FakeDynamoDbServer server = new(app);
        app.Run(server.HandleAsync);
        await app.StartAsync(ct);
        server.BaseAddress = new Uri(app.Urls.First());
        return server;
    }

    /// <summary>
    /// An SDK client pointed at this server with anonymous credentials, so it signs nothing and needs no
    /// profile, and with the SDK's own retries off, so a test sees the store's one retry and no other.
    /// <paramref name="maxErrorRetry"/> set to null keeps the SDK's default, for the test of what a client
    /// from the container does.
    /// </summary>
    public AmazonDynamoDBClient Client(int? maxErrorRetry = 0)
    {
        AmazonDynamoDBConfig config = new() { ServiceURL = BaseAddress.ToString() };
        if (maxErrorRetry is { } retries)
        {
            config.MaxErrorRetry = retries;
        }

        return new AmazonDynamoDBClient(new AnonymousAWSCredentials(), config);
    }

    /// <summary>Schedules <paramref name="fault"/> for the next call of <paramref name="operation"/>.</summary>
    public void Next(string operation, Fault fault) =>
        faults.GetOrAdd(operation, _ => new ConcurrentQueue<Fault>()).Enqueue(fault);

    /// <summary>A snapshot of the table, by <c>pk</c>, each item as the JSON the store sent.</summary>
    public IReadOnlyDictionary<string, JsonObject> Items
    {
        get
        {
            lock (writes)
            {
                return items.ToDictionary(i => i.Key, i => (JsonObject)i.Value.DeepClone(), StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Puts an item in the table as given, for a test that plants one the store did not write.</summary>
    public void Plant(JsonObject item)
    {
        lock (writes)
        {
            items[item["pk"]!["S"]!.GetValue<string>()] = (JsonObject)item.DeepClone();
        }
    }

    /// <summary>Stops listening, so the port refuses connections: the table unreachable (F17). The items stay.</summary>
    public async Task StopAsync()
    {
        stopped = true;
        await app.StopAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        Release.TrySetResult();
        if (!stopped)
        {
            await app.StopAsync(CancellationToken.None);
        }

        await app.DisposeAsync();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task HandleAsync(HttpContext context)
    {
        string target = context.Request.Headers["X-Amz-Target"].ToString();
        string operation = target.StartsWith("DynamoDB_20120810.", StringComparison.Ordinal) ? target["DynamoDB_20120810.".Length..] : "";
        JsonObject body;
        using (StreamReader reader = new(context.Request.Body, Encoding.UTF8))
        {
            body = JsonNode.Parse(await reader.ReadToEndAsync(context.RequestAborted))!.AsObject();
        }

        requests.Enqueue(new SeenRequest(operation, context.Request.ContentType ?? "", (JsonObject)body.DeepClone()));

        if (!HttpMethods.IsPost(context.Request.Method) || context.Request.Path != "/"
            || context.Request.ContentType != "application/x-amz-json-1.0")
        {
            await ErrorAsync(context, 400, "SerializationException", "The fake answers POST / in JSON 1.0 only.");
            return;
        }

        Fault? fault = faults.TryGetValue(operation, out ConcurrentQueue<Fault>? queue) && queue.TryDequeue(out Fault next) ? next : null;
        if (fault == Fault.FailBeforeCommit)
        {
            await ErrorAsync(context, 500, "InternalServerError", "The fake failed before committing.");
            return;
        }

        if (fault == Fault.HoldBeforeCommit && !await HoldAsync(context))
        {
            return;
        }

        if (fault == Fault.CommitAfterNextRead)
        {
            lateCommit = (JsonObject)body.DeepClone();
            await ErrorAsync(context, 500, "InternalServerError", "The fake failed, and the write is still in flight.");
            return;
        }

        if (fault == Fault.ForeignItemFirst && body["Item"]?.DeepClone() is JsonObject foreign)
        {
            foreign["foreign"] = new JsonObject { ["S"] = "another writer" };
            Plant(foreign);
        }

        (int status, JsonObject answer) result;
        try
        {
            result = operation switch
            {
                "PutItem" => PutItem(body),
                "GetItem" => GetItemThenLateCommit(body),
                "DeleteItem" => DeleteItem(body),
                "Query" => Query(body),
                "BatchWriteItem" => BatchWriteItem(body),
                _ => throw new Refusal("UnknownOperationException", $"The fake does not answer '{operation}'."),
            };
        }
        catch (Refusal refusal)
        {
            await ErrorAsync(context, 400, refusal.Type, refusal.Message);
            return;
        }

        if (fault == Fault.HoldAfterCommit && !await HoldAsync(context))
        {
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(result.answer.ToJsonString());
        context.Response.StatusCode = result.status;
        context.Response.ContentType = "application/x-amz-json-1.0";
        if (Crc32 is { } crc)
        {
            context.Response.Headers["x-amz-crc32"] = crc;
        }

        if (fault == Fault.CommitThenDrop)
        {
            // The commit's answer, lost: a whole HTTP response whose body breaks off mid-document, so the
            // SDK cannot read it. Not a broken connection: a connection closed before the client has read
            // the response's first bytes makes the HTTP handler send the POST again by itself, a resend
            // rather than a lost answer, and whether the client had read them is a race.
            byte[] broken = Encoding.UTF8.GetBytes("{\"Attributes\":{\"pk\":");
            context.Response.ContentLength = broken.Length;
            await context.Response.Body.WriteAsync(broken, CancellationToken.None);
            return;
        }

        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, CancellationToken.None);
    }

    /// <summary>Holds a call; false when the client gave up first, and then nothing is answered.</summary>
    private async Task<bool> HoldAsync(HttpContext context)
    {
        Held.TrySetResult();
        Task gone = Task.Delay(Timeout.Infinite, context.RequestAborted);
        Task first = await Task.WhenAny(Release.Task, gone);
        Held = NewSignal();
        if (first == gone)
        {
            return false;
        }

        Release = NewSignal();
        return true;
    }

    private (int, JsonObject) PutItem(JsonObject body)
    {
        RequireTable(body);
        JsonObject item = body["Item"]?.AsObject() ?? throw new Refusal("ValidationException", "PutItem needs an Item.");
        string pk = item["pk"]?["S"]?.GetValue<string>() ?? throw new Refusal("ValidationException", "The item has no S pk.");
        lock (writes)
        {
            Check(body, items.GetValueOrDefault(pk));
            items[pk] = (JsonObject)item.DeepClone();
        }

        return (200, []);
    }

    /// <summary>A read, after which a write held back by <see cref="Fault.CommitAfterNextRead"/> lands.</summary>
    private (int, JsonObject) GetItemThenLateCommit(JsonObject body)
    {
        (int, JsonObject) read = GetItem(body);
        if (Interlocked.Exchange(ref lateCommit, null) is { } late)
        {
            PutItem(late);
        }

        return read;
    }

    private (int, JsonObject) GetItem(JsonObject body)
    {
        RequireTable(body);
        if (body["ConsistentRead"]?.GetValue<bool>() != true)
        {
            throw new Refusal("ValidationException", "The fake refuses a GetItem without ConsistentRead: the store reads what was just written.");
        }

        string pk = KeyOf(body);
        lock (writes)
        {
            return (200, items.TryGetValue(pk, out JsonObject? item) ? new JsonObject { ["Item"] = item.DeepClone() } : []);
        }
    }

    private (int, JsonObject) DeleteItem(JsonObject body)
    {
        RequireTable(body);
        string pk = KeyOf(body);
        lock (writes)
        {
            Check(body, items.GetValueOrDefault(pk));
            items.Remove(pk);
        }

        return (200, []);
    }

    private (int, JsonObject) Query(JsonObject body)
    {
        RequireTable(body);
        if (body["IndexName"]?.GetValue<string>() != "appId"
            || body["KeyConditionExpression"]?.GetValue<string>() != "appId = :app")
        {
            throw new Refusal("ValidationException", "The fake answers a Query on the appId index by appId = :app only.");
        }

        if (body["ConsistentRead"]?.GetValue<bool>() == true)
        {
            throw new Refusal("ValidationException", "Consistent reads are not supported on global secondary indexes.");
        }

        string app = body["ExpressionAttributeValues"]?[":app"]?["S"]?.GetValue<string>()
            ?? throw new Refusal("ValidationException", "The Query names no :app.");
        lock (writes)
        {
            // Keys only: the table's key and the index's (the index's projection, infra/modules/cell).
            JsonArray found = [.. items.Values
                .Where(i => i["appId"]?["S"]?.GetValue<string>() == app)
                .Select(i => (JsonNode)new JsonObject { ["pk"] = i["pk"]!.DeepClone(), ["appId"] = i["appId"]!.DeepClone() })];
            return (200, new JsonObject { ["Items"] = found, ["Count"] = found.Count });
        }
    }

    private (int, JsonObject) BatchWriteItem(JsonObject body)
    {
        JsonObject requested = body["RequestItems"]?.AsObject() ?? throw new Refusal("ValidationException", "No RequestItems.");
        if (requested.Count != 1 || requested[Table] is not JsonArray writesForTable)
        {
            throw new ResourceMissing();
        }

        if (writesForTable.Count is 0 or > 25)
        {
            throw new Refusal("ValidationException", "A BatchWriteItem carries 1 to 25 requests.");
        }

        lock (writes)
        {
            foreach (JsonNode? write in writesForTable)
            {
                string pk = write?["DeleteRequest"]?["Key"]?["pk"]?["S"]?.GetValue<string>()
                    ?? throw new Refusal("ValidationException", "The fake answers DeleteRequests by S pk only.");
                items.Remove(pk);
            }
        }

        return (200, new JsonObject { ["UnprocessedItems"] = new JsonObject() });
    }

    private static void RequireTable(JsonObject body)
    {
        if (body["TableName"]?.GetValue<string>() != Table)
        {
            throw new ResourceMissing();
        }
    }

    private static string KeyOf(JsonObject body)
    {
        JsonObject key = body["Key"]?.AsObject() ?? throw new Refusal("ValidationException", "No Key.");
        return key.Count == 1 && key["pk"]?["S"]?.GetValue<string>() is { } pk
            ? pk
            : throw new Refusal("ValidationException", "The key is pk, a string, and nothing else.");
    }

    /// <summary>Evaluates the request's condition, if it has one, against the item it would change.</summary>
    private static void Check(JsonObject body, JsonObject? current)
    {
        if (body["ConditionExpression"]?.GetValue<string>() is not { } expression)
        {
            return;
        }

        JsonObject values = body["ExpressionAttributeValues"]?.AsObject() ?? [];
        if (!new Condition(expression, values, current).Evaluate())
        {
            throw new Refusal("ConditionalCheckFailedException", "The conditional request failed");
        }
    }

    private static async Task ErrorAsync(HttpContext context, int status, string type, string message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(new JsonObject { ["__type"] = Prefix + type, ["message"] = message }.ToJsonString());
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/x-amz-json-1.0";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, CancellationToken.None);
    }

    public sealed record SeenRequest(string Operation, string ContentType, JsonObject Body);

    private class Refusal(string type, string message) : Exception(message)
    {
        public string Type { get; } = type;
    }

    private sealed class ResourceMissing() : Refusal("ResourceNotFoundException", $"Requested resource not found: the fake's table is {Table}.");

    /// <summary>
    /// A recursive-descent reading of the grammar the store uses: <c>or := and (OR and)*</c>,
    /// <c>and := term (AND term)*</c>, <c>term := ( or ) | fn ( name ) | name op :value</c>.
    /// </summary>
    private sealed class Condition(string expression, JsonObject values, JsonObject? item)
    {
        private readonly string[] tokens = Tokenize(expression);
        private int at;

        public bool Evaluate()
        {
            bool result = Or();
            return at == tokens.Length ? result : throw Unsupported($"trailing '{tokens[at]}'");
        }

        private bool Or()
        {
            bool result = And();
            while (Accept("OR"))
            {
                result = And() | result;
            }

            return result;
        }

        private bool And()
        {
            bool result = Term();
            while (Accept("AND"))
            {
                result = Term() & result;
            }

            return result;
        }

        private bool Term()
        {
            if (Accept("("))
            {
                bool inner = Or();
                Expect(")");
                return inner;
            }

            string first = Take();
            if (first is "attribute_exists" or "attribute_not_exists")
            {
                Expect("(");
                string name = Name(Take());
                Expect(")");
                bool exists = item?.ContainsKey(name) == true;
                return first == "attribute_exists" ? exists : !exists;
            }

            string attribute = Name(first);
            string op = Take();
            string placeholder = Take();
            if (!placeholder.StartsWith(':') || values[placeholder] is not JsonObject value)
            {
                throw Unsupported($"'{placeholder}' as a value");
            }

            if (item?[attribute] is not JsonObject stored)
            {
                return false;
            }

            // An unordered pair (two types) satisfies no comparison, so a leaseUntil written as a string is
            // never "< :now" and a free-for-all lease cannot hide behind the fake.
            int? order = Compare(stored, value);
            return op switch
            {
                "=" => order == 0,
                "<" => order < 0,
                ">" => order > 0,
                _ => throw Unsupported($"the operator '{op}'"),
            };
        }

        private static int? Compare(JsonObject stored, JsonObject value)
        {
            if (stored["N"] is JsonNode n && value["N"] is JsonNode m)
            {
                return decimal.Parse(n.GetValue<string>(), CultureInfo.InvariantCulture)
                    .CompareTo(decimal.Parse(m.GetValue<string>(), CultureInfo.InvariantCulture));
            }

            if (stored["S"] is JsonNode s && value["S"] is JsonNode t)
            {
                return string.CompareOrdinal(s.GetValue<string>(), t.GetValue<string>());
            }

            // Different types never compare equal, and the fake does not order them: null, which no
            // operator above accepts.
            return stored.ToJsonString() == value.ToJsonString() ? 0 : null;
        }

        private string Name(string token) =>
            token.Length > 0 && (char.IsLetter(token[0]) || token[0] == '_') ? token : throw Unsupported($"'{token}' as a name");

        private bool Accept(string token)
        {
            if (at < tokens.Length && tokens[at] == token)
            {
                at++;
                return true;
            }

            return false;
        }

        private void Expect(string token)
        {
            if (!Accept(token))
            {
                throw Unsupported($"a missing '{token}'");
            }
        }

        private string Take() => at < tokens.Length ? tokens[at++] : throw Unsupported("an expression that ends early");

        private Refusal Unsupported(string what) =>
            new("ValidationException", $"The fake does not evaluate {what} in '{expression}'.");

        private static string[] Tokenize(string text)
        {
            List<string> found = [];
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (c is '(' or ')' or '=' or '<' or '>')
                {
                    found.Add(c.ToString());
                    i++;
                }
                else
                {
                    int start = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('(' or ')' or '=' or '<' or '>'))
                    {
                        i++;
                    }

                    found.Add(text[start..i]);
                }
            }

            return [.. found];
        }
    }
}
