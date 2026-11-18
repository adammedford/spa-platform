using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// What the fragments rows read through the AWS CLI (<see cref="ICommandRunner"/>), each turned into a
/// figure the observer judges and the evidence file can hold: N from the live registry, the session
/// item's sizes, the table's consumed write capacity, and the gateway tasks with their image digests.
/// No attribute value, key or object body read here is returned.
/// </summary>
public sealed class FragmentsReaders(Cell cell, ICommandRunner runner)
{
    /// <summary>The runner the reads go through, for a driver's read of its own.</summary>
    public ICommandRunner Runner => runner;

    /// <summary>
    /// N, the number of fragments the shell's live registry entry names (#1005 "The rows"): its
    /// <c>fragments</c> array's length, 0 when the entry has none (#961: a document without fragments
    /// keeps its meaning). An app the registry does not hold is refused, never read as 0.
    /// </summary>
    public async Task<int> FragmentsOfAsync(string appId, CancellationToken ct)
    {
        // https://docs.aws.amazon.com/cli/latest/reference/s3/cp.html: "-" streams the object to stdout.
        string text = await runner.RunAsync("aws", ["s3", "cp", $"s3://{cell.Bucket}/{CellAws.RegistryKey}", "-", "--region", cell.Region], ct);
        JsonNode? entry = (JsonNode.Parse(text)?["apps"] as JsonArray)?.FirstOrDefault(each => each?["appId"]?.GetValue<string>() == appId);
        return entry is null
            ? throw new InvalidOperationException($"the live registry has no entry for {appId}, so N cannot be read.")
            : (entry["fragments"] as JsonArray)?.Count ?? 0;
    }

    /// <summary>
    /// O4's sizes from one <c>aws dynamodb scan</c> of the isolated table: the largest item that holds
    /// <paramref name="payload"/> (the session record's protected ticket, DynamoDbSessionStore's
    /// <c>ticket</c>), its stored bytes by <see cref="StoredBytes"/>, and the payload's raw bytes.
    /// </summary>
    public async Task<ItemSize> ItemSizeAsync(string table, string payload, CancellationToken ct)
    {
        string text = await runner.RunAsync("aws", ["dynamodb", "scan", "--table-name", table, "--output", "json", "--region", cell.Region], ct);
        return ItemSizeOf(text, payload);
    }

    public static ItemSize ItemSizeOf(string scan, string payload)
    {
        using JsonDocument answer = JsonDocument.Parse(scan);
        (long Stored, long Payload)[] sized = [.. answer.RootElement.GetProperty("Items").EnumerateArray()
            .Where(item => item.TryGetProperty(payload, out _))
            .Select(item => (StoredBytes(item), (long)ValueBytes(item.GetProperty(payload))))];
        return sized.Length == 0 ? new ItemSize(null, null) : new ItemSize(sized.Max(each => each.Stored), sized.MaxBy(each => each.Stored).Payload);
    }

    /// <summary>
    /// An item's stored size by DynamoDB's rules by attribute type
    /// (https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/CapacityUnitCalculations.html):
    /// each attribute's name in UTF-8 bytes plus its value's size.
    /// </summary>
    public static long StoredBytes(JsonElement item) =>
        item.EnumerateObject().Sum(attribute => (long)Encoding.UTF8.GetByteCount(attribute.Name) + ValueBytes(attribute.Value));

    /// <summary>
    /// A value's size: a string's UTF-8 bytes; a binary's raw bytes, which the CLI prints in base64; a
    /// number's one byte per two significant digits plus one; a Boolean or null one byte; a list or map
    /// three bytes, plus one per element, plus its elements (a map's names counted); a set its elements.
    /// </summary>
    public static int ValueBytes(JsonElement value)
    {
        JsonProperty typed = value.EnumerateObject().Single();
        return typed.Name switch
        {
            "S" => Encoding.UTF8.GetByteCount(typed.Value.GetString()!),
            "B" => Convert.FromBase64String(typed.Value.GetString()!).Length,
            "N" => NumberBytes(typed.Value.GetString()!),
            "BOOL" or "NULL" => 1,
            "SS" => typed.Value.EnumerateArray().Sum(each => Encoding.UTF8.GetByteCount(each.GetString()!)),
            "BS" => typed.Value.EnumerateArray().Sum(each => Convert.FromBase64String(each.GetString()!).Length),
            "NS" => typed.Value.EnumerateArray().Sum(each => NumberBytes(each.GetString()!)),
            "L" => 3 + typed.Value.EnumerateArray().Sum(each => 1 + ValueBytes(each)),
            "M" => 3 + typed.Value.EnumerateObject().Sum(each => 1 + Encoding.UTF8.GetByteCount(each.Name) + ValueBytes(each.Value)),
            _ => throw new InvalidOperationException($"an attribute of type {typed.Name}, which DynamoDB's size rules do not name"),
        };
    }

    /// <summary>Leading and trailing zeroes trimmed, one byte per two significant digits, and one more.</summary>
    private static int NumberBytes(string number)
    {
        string digits = new([.. number.Where(char.IsAsciiDigit)]);
        string significant = digits.TrimStart('0').TrimEnd('0');
        return ((Math.Max(significant.Length, 1) + 1) / 2) + 1;
    }

    /// <summary>
    /// The table's <c>ConsumedWriteCapacityUnits</c> Sum for each one-minute period from
    /// <paramref name="from"/> to <paramref name="to"/>
    /// (https://docs.aws.amazon.com/cli/latest/reference/cloudwatch/get-metric-statistics.html;
    /// https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/metrics-dimensions.html).
    /// </summary>
    public async Task<IReadOnlyList<CapacityPeriod>> WriteCapacityAsync(string table, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        string text = await runner.RunAsync("aws",
            ["cloudwatch", "get-metric-statistics", "--namespace", "AWS/DynamoDB", "--metric-name", "ConsumedWriteCapacityUnits",
             "--dimensions", $"Name=TableName,Value={table}", "--start-time", from.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
             "--end-time", to.UtcDateTime.ToString("o", CultureInfo.InvariantCulture), "--period", "60", "--statistics", "Sum",
             "--output", "json", "--region", cell.Region], ct);
        using JsonDocument answer = JsonDocument.Parse(text);
        return [.. answer.RootElement.GetProperty("Datapoints").EnumerateArray()
            .Select(point => new CapacityPeriod(
                DateTimeOffset.Parse(point.GetProperty("Timestamp").GetString()!, CultureInfo.InvariantCulture).ToUniversalTime(),
                point.GetProperty("Sum").GetDouble()))
            .OrderBy(period => period.Start)];
    }

    /// <summary>The gateway service's tasks and each one's gateway image digest, through <see cref="ContinuityEcs"/>'s list and describe.</summary>
    public async Task<IReadOnlyList<TaskImage>> TaskImagesAsync(CancellationToken ct) =>
        [.. (await new ContinuityEcs(cell, runner).ListedAsync(ct)).Select(task => new TaskImage(task.Id, task.Digest))];
}
