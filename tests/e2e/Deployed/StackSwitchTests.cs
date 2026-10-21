using Gateway.E2ETests.Stack;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// The environment switch that picks the stack the functional rows run against (#209): the local
/// stack unless the run asks for the deployed cell, and a refusal for anything else, so a typo never
/// quietly runs the local stack in a sitting.
/// </summary>
public sealed class StackSwitchTests
{
    [Fact]
    public void With_the_switch_unset_the_local_stack_is_chosen()
    {
        Assert.Equal(StackKind.Local, StackSwitch.Choose(_ => null));
        Assert.Equal(StackKind.Local, StackSwitch.Choose(_ => ""));
    }

    [Theory]
    [InlineData("local", StackKind.Local)]
    [InlineData("deployed", StackKind.Deployed)]
    public void The_switch_names_the_stack(string value, StackKind expected) =>
        Assert.Equal(expected, StackSwitch.Choose(name => name == StackSwitch.Variable ? value : null));

    [Fact]
    public void Another_value_is_refused_and_named()
    {
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => StackSwitch.Choose(name => name == StackSwitch.Variable ? "Deployed " : null));
        Assert.Contains(StackSwitch.Variable, refused.Message, StringComparison.Ordinal);
        Assert.Contains("'Deployed '", refused.Message, StringComparison.Ordinal);
    }
}
