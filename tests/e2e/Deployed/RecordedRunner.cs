using Gateway.E2ETests.Stack;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// A command runner that records each call and answers from the test: the deployed stack's tools, with
/// no AWS and no Terraform. An answer whose exit the command does not accept fails as the process
/// runner fails it.
/// </summary>
internal sealed class RecordedRunner : ICommandRunner
{
    private readonly Func<Command, CommandResult> answer;

    /// <summary>Answers every call with exit 0 and the output the test gives for its program and arguments.</summary>
    public RecordedRunner(Func<string, string[], string> answer)
        : this(command => new CommandResult(0, answer(command.Program, [.. command.Arguments])))
    {
    }

    public RecordedRunner(Func<string[], string> answer)
        : this((_, arguments) => answer(arguments))
    {
    }

    private RecordedRunner(Func<Command, CommandResult> answer) => this.answer = answer;

    /// <summary>Answers each command with an exit and output of the test's choosing, as <c>terraform plan -detailed-exitcode</c> needs.</summary>
    public static RecordedRunner WithExits(Func<Command, CommandResult> answer) => new(answer);

    public List<(string Program, string[] Arguments)> Calls { get; } = [];

    /// <summary>Every command as it was asked for, its environment and accepted exits included.</summary>
    public List<Command> Commands { get; } = [];

    public Task<CommandResult> RunAsync(Command command, CancellationToken ct)
    {
        Command copy = command with { Arguments = [.. command.Arguments] };
        Calls.Add((copy.Program, [.. copy.Arguments]));
        Commands.Add(copy);
        CommandResult result = answer(copy);
        return command.AcceptedExits.Contains(result.ExitCode)
            ? Task.FromResult(result)
            : throw new CommandFailedException(copy.Program, copy.Arguments, result.ExitCode, "the recorded answer's exit");
    }
}
