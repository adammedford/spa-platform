using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Spike;

/// <summary>
/// Everything the harness prints goes through here (Q4). A line that holds a value the harness has seen as a
/// credential is withheld, and the whole buffer is checked again before any file is written. The no-credentials
/// rule that test N-16 checks applies to the recorded output, which is committed.
/// </summary>
public sealed partial class Redactor
{
    private readonly HashSet<string> _canaries = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>The values held, by count only.</summary>
    public int Count { get { lock (_lock) return _canaries.Count; } }

    /// <summary>Remember a value that must never be printed: a secret, password, token, code, state or cookie value.</summary>
    public void Add(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 8) return;   // shorter strings are too common to search for
        lock (_lock) _canaries.Add(value);
    }

    /// <summary>Read before every check: the tokens the gateway and the harness hold at that moment.</summary>
    public Func<IEnumerable<string>>? Harvest;

    /// <summary>Whether a line holds a remembered value, or anything shaped like a credential.</summary>
    public string? Leak(string line)
    {
        if (Harvest is { } harvest) foreach (var t in harvest()) Add(t);
        lock (_lock)
        {
            foreach (var c in _canaries)
                if (line.Contains(c, StringComparison.Ordinal)) return "a value the harness holds as a credential";
        }
        if (Jwt().IsMatch(line)) return "a JWT";
        if (CredentialParameter().IsMatch(line)) return "a code, state, token or secret parameter with a value";
        return null;
    }

    /// <summary>The short, non-reversible name for a value: eight hex characters of its SHA-256.</summary>
    public static string Hash(string? value)
        => string.IsNullOrEmpty(value) ? "(none)" : "h:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];

    /// <summary>A URL with its query and fragment removed: the query of an authorize or callback URL carries state and code.</summary>
    public static string Strip(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "(none)";
        return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : url.Split('?', '#')[0];
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?i)[?&;\s](code|state|access_token|refresh_token|id_token|client_secret|password)=[^&\s;]{6,}")]
    private static partial Regex CredentialParameter();
}

/// <summary>One line of the run's result.</summary>
public sealed record Result(string Id, string Kind, string Name, string Detail, string? Route);

/// <summary>
/// The run's printed output. "OK" and "BAD" mark an expectation; "FINDING" marks a probe that confirmed something the
/// plan must allow for, and "not-rep" one that did not; "skip" marks a check this mode cannot make. Only "BAD" fails.
/// </summary>
public sealed class Output(Redactor redactor)
{
    private readonly StringBuilder _buffer = new();
    public readonly List<Result> Results = [];
    public int Withheld { get; private set; }

    public Redactor Redactor => redactor;

    public void Line(string text = "")
    {
        if (redactor.Leak(text) is { } why)
        {
            Withheld++;
            text = $"[line withheld: it held {why}]";
        }
        Console.WriteLine(text);
        _buffer.AppendLine(text);
    }

    /// <summary>For the leak test only: puts a line in the buffer without the per-line check, as a careless print would.</summary>
    public void Unchecked(string text) => _buffer.AppendLine(text);

    public void Report(string id, string kind, string name, string detail = "", string? route = null)
    {
        Results.Add(new Result(id, kind, name, detail, route));
        Line($"{kind,-8}{id,-7} {name}");
        if (detail.Length > 0) Line("               [" + detail + "]");
        if (route is not null) Line("               -> " + route);
    }

    public void Check(string id, string name, bool held, string detail = "") => Report(id, held ? "OK" : "BAD", name, detail);
    public void Finding(string id, string name, bool confirmed, string detail, string routeIfConfirmed, string routeIfNot)
        => Report(id, confirmed ? "FINDING" : "not-rep", name, detail, confirmed ? routeIfConfirmed : routeIfNot);
    public void Skip(string id, string name, string why) => Report(id, "skip", name, why);

    public bool Failed => Results.Any(r => r.Kind == "BAD");

    /// <summary>The whole buffer, checked again. Null when it is clean; otherwise why it is not.</summary>
    public string? FinalCheck()
    {
        var text = _buffer.ToString();
        foreach (var line in text.Split('\n'))
            if (redactor.Leak(line) is { } why) return why;
        return null;
    }

    public string Text => _buffer.ToString();
}
