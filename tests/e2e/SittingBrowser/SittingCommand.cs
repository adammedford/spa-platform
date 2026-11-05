using System.Globalization;
using System.Text.RegularExpressions;

namespace Gateway.E2ETests.SittingBrowser;

/// <summary>
/// One command of the sitting browser's surface (#1024 Q8, #1033), parsed from its words: the client
/// parses it before it connects, so a usage error is exit 2 whether or not a process is running, and
/// the process parses it again from the request line. Nothing parsed here is a secret: there is no
/// password flag, and a user is <c>alice</c> or <c>bob</c> (#1024 Q6).
/// </summary>
public sealed partial record SittingCommand(
    string Verb,
    string Profile,
    string? App = null,
    string? User = null,
    string? ApiPath = null,
    TimeSpan Every = default,
    bool ClearProvider = false,
    bool Relogin = false)
{
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Status = "status";
    public const string Open = "open";
    public const string Close = "close";
    public const string Login = "login";
    public const string Reload = "reload";
    public const string Marker = "marker";
    public const string Session = "session";
    public const string Api = "api";
    public const string ClearProviderVerb = "clear-provider";
    public const string Watch = "watch";
    public const string Unwatch = "unwatch";

    /// <summary>The profile a command acts on when it names none.</summary>
    public const string DefaultProfile = "main";

    /// <summary>The users a login may name (fixtures/cognito/pool.json); the first is the default.</summary>
    public static readonly IReadOnlyList<string> Users = [Stack.DeployedStack.User, Stack.DeployedStack.OtherUser];

    /// <summary>The shortest watch interval: a tick is a reload and a <c>/readyz</c> probe.</summary>
    public static readonly TimeSpan ShortestEvery = TimeSpan.FromSeconds(1);

    /// <summary>The command, or the usage error that is exit 2.</summary>
    public static (SittingCommand? Command, string? Usage) Parse(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            return (null, "no command");
        }

        string verb = words[0];
        List<string> positional = [];
        Dictionary<string, string?> options = new(StringComparer.Ordinal);
        for (int i = 1; i < words.Count; i++)
        {
            string word = words[i];
            if (word.StartsWith("--", StringComparison.Ordinal))
            {
                bool takesValue = word is "--app" or "--as" or "--every";
                if (options.ContainsKey(word))
                {
                    return (null, $"{word} given twice");
                }

                if (takesValue)
                {
                    if (i + 1 >= words.Count)
                    {
                        return (null, $"{word} needs a value");
                    }

                    options[word] = words[++i];
                }
                else
                {
                    options[word] = null;
                }
            }
            else
            {
                positional.Add(word);
            }
        }

        (string[] Allowed, int MaxPositional) shape = verb switch
        {
            Start or Stop or Status => ([], 0),
            Open => (["--app"], 1),
            Login => (["--app", "--as"], 1),
            Close or Reload or Marker or Session or ClearProviderVerb or Unwatch => ([], 1),
            Api => ([], 2),
            Watch => (["--every", "--clear-provider", "--relogin"], 1),
            _ => (null!, -1),
        };

        if (shape.MaxPositional < 0)
        {
            return (null, "unknown command");
        }

        if (options.Keys.FirstOrDefault(option => !shape.Allowed.Contains(option)) is { } unknown)
        {
            return (null, $"{verb} takes no {unknown}");
        }

        if (positional.Count > shape.MaxPositional || (verb == Api && positional.Count == 0))
        {
            return (null, $"{verb}: wrong number of arguments");
        }

        string? apiPath = null;
        if (verb == Api)
        {
            apiPath = positional[^1];
            positional.RemoveAt(positional.Count - 1);
            if (!ApiPathPattern().IsMatch(apiPath) || apiPath.Contains("..", StringComparison.Ordinal))
            {
                return (null, "api: the path is <name>/<path>, letters, digits and . _ ~ - only, no query");
            }
        }

        string profile = positional is [string named] ? named : DefaultProfile;
        if (shape.MaxPositional > 0 && !ProfilePattern().IsMatch(profile))
        {
            return (null, "a profile is lowercase letters, digits and -, at most 32");
        }

        string? app = options.GetValueOrDefault("--app");
        if ((verb is Open or Login) && (app is null || !AppPattern().IsMatch(app)))
        {
            return (null, $"{verb} needs --app <appId>");
        }

        string? user = null;
        if (verb == Login)
        {
            user = options.GetValueOrDefault("--as") ?? Users[0];
            if (!Users.Contains(user))
            {
                return (null, $"--as is one of {string.Join('|', Users)}");
            }
        }

        TimeSpan every = default;
        if (verb == Watch)
        {
            if (options.GetValueOrDefault("--every") is not { } interval
                || EveryPattern().Match(interval) is not { Success: true } seconds
                || TimeSpan.FromSeconds(int.Parse(seconds.Groups[1].Value, CultureInfo.InvariantCulture)) is var parsed && parsed < ShortestEvery)
            {
                return (null, "watch needs --every <seconds>s, at least 1s");
            }

            every = parsed;
        }

        return (new SittingCommand(verb, shape.MaxPositional > 0 ? profile : ObservationLine.NoProfile, app, user, apiPath, every,
            options.ContainsKey("--clear-provider"), options.ContainsKey("--relogin")), null);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.None, 1000)]
    private static partial Regex ProfilePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.None, 1000)]
    private static partial Regex AppPattern();

    [GeneratedRegex("^[a-z0-9-]+(/[A-Za-z0-9._~-]+)+$", RegexOptions.None, 1000)]
    private static partial Regex ApiPathPattern();

    [GeneratedRegex("^([0-9]{1,5})s$", RegexOptions.None, 1000)]
    private static partial Regex EveryPattern();
}
