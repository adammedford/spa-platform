using Gateway.E2ETests.Stack;

namespace Gateway.E2ETests.Deployed;

/// <summary>A command runner that records each call and answers from the test: the deployed stack's tools, with no AWS and no Terraform.</summary>
internal sealed class RecordedRunner(Func<string, string[], string> answer) : ICommandRunner
{
    public RecordedRunner(Func<string[], string> answer)
        : this((_, arguments) => answer(arguments))
    {
    }

    public List<(string Program, string[] Arguments)> Calls { get; } = [];

    public Task<string> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        string[] copy = [.. arguments];
        Calls.Add((program, copy));
        return Task.FromResult(answer(program, copy));
    }
}
