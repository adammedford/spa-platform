namespace Spactl;

/// <summary>
/// The command line, read by hand: positional words, <c>--name value</c> options and the
/// <see cref="Flags"/>. No parsing package is in Directory.Packages.props, and this surface is small.
/// </summary>
internal sealed class Arguments
{
    /// <summary>Options that take no value.</summary>
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "--offline" };

    private readonly Dictionary<string, string?> options = new(StringComparer.Ordinal);

    private Arguments(List<string> positional) => Positional = positional;

    public List<string> Positional { get; }

    public static Arguments Parse(IReadOnlyList<string> args)
    {
        Arguments parsed = new([]);
        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                parsed.Positional.Add(arg);
                continue;
            }

            if (Flags.Contains(arg))
            {
                parsed.options[arg] = null;
                continue;
            }

            if (index + 1 >= args.Count)
            {
                throw new UsageException($"{arg} needs a value.");
            }

            parsed.options[arg] = args[++index];
        }

        return parsed;
    }

    public bool Has(string flag) => options.ContainsKey(flag);

    public string? Option(string name) => options.TryGetValue(name, out string? value) ? value : null;

    public string Required(string name) =>
        Option(name) ?? throw new UsageException($"{name} is required.");

    /// <summary>The positional words after the two-word command, exactly <paramref name="count"/> of them.</summary>
    public IReadOnlyList<string> Operands(int skip, int count, string shape)
    {
        if (Positional.Count != skip + count)
        {
            throw new UsageException($"Usage: spactl {shape}");
        }

        return Positional.GetRange(skip, count);
    }
}

/// <summary>A command line spactl cannot run: exit code 2, and the message names what is wrong.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>A command that ran and was refused: exit code 1. The message is safe to print.</summary>
internal sealed class RefusedException(string message) : Exception(message);
