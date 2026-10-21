namespace Gateway.E2ETests.Stack;

/// <summary>Which stack the functional rows run against.</summary>
public enum StackKind
{
    /// <summary>The hermetic cell behind the stand-in load balancer (<see cref="LocalStack"/>): the default, and what CI runs.</summary>
    Local,

    /// <summary>The deployed cell, through the tunnel, against Cognito (<see cref="DeployedStack"/>): a sitting's run (#209).</summary>
    Deployed,
}

/// <summary>
/// The environment switch that picks the stack (#209). Unset or empty is the local stack, so CI and
/// every lane run what they ran before; <c>deployed</c> is the cell. Any other value is refused
/// rather than read as local, so a misspelt switch in a sitting cannot pass the rows on the local
/// stack and be posted as a deployed run.
/// </summary>
public static class StackSwitch
{
    /// <summary>The variable a sitting sets: <c>SPA_E2E_STACK=deployed</c>.</summary>
    public const string Variable = "SPA_E2E_STACK";

    public static StackKind Choose(Func<string, string?> environment) => environment(Variable) switch
    {
        null or "" or "local" => StackKind.Local,
        "deployed" => StackKind.Deployed,
        string other => throw new InvalidOperationException(
            $"{Variable} is '{other}'; it is unset or 'local' for the local stack, or 'deployed' for the cell."),
    };

    /// <summary>The stack this process's environment chooses.</summary>
    public static StackKind FromEnvironment() => Choose(Environment.GetEnvironmentVariable);
}
