using Microsoft.Extensions.Configuration;
using Platform.Abstractions;

namespace Platform.Registry.Tests;

/// <summary>
/// <c>Policy:FragmentsPerShell</c> and <c>Policy:MergeBudget</c> as <see cref="PlatformPolicyConfiguration.Read"/>
/// takes them from configuration (#992, #1445): each is a <c>Policy:</c> value that G9's sitting replaces,
/// so a deployment changes it by configuration alone (the cell module's <c>fragments_per_shell</c> and
/// <c>merge_budget</c> inputs, #1099).
/// </summary>
public sealed class PolicyConfigurationTests
{
    private static PlatformPolicy Read(string? fragmentsPerShell, string? mergeBudget = null)
    {
        Dictionary<string, string?> values = new(StringComparer.Ordinal)
        {
            ["Policy:AllowedHostSuffixes:0"] = ".spa-poc.test:8443",
        };
        if (fragmentsPerShell is not null)
        {
            values["Policy:FragmentsPerShell"] = fragmentsPerShell;
        }

        if (mergeBudget is not null)
        {
            values["Policy:MergeBudget"] = mergeBudget;
        }

        return PlatformPolicyConfiguration.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    [Fact]
    public void An_absent_value_is_the_placeholder_5() => Assert.Equal(5, Read(null).FragmentsPerShell);

    [Theory]
    [InlineData("0", 0)]
    [InlineData("2", 2)]
    [InlineData("6", 6)]
    public void A_configured_value_is_the_cap(string value, int cap) => Assert.Equal(cap, Read(value).FragmentsPerShell);

    [Theory]
    [InlineData("five")]
    [InlineData("-1")]
    [InlineData("2.5")]
    [InlineData("99999999999")]
    public void A_value_that_is_not_a_non_negative_integer_fails_closed_at_0(string value) =>
        Assert.Equal(0, Read(value).FragmentsPerShell);

    [Fact]
    public void An_absent_merge_budget_is_the_placeholder_3() => Assert.Equal(3, Read(null).MergeBudget);

    [Theory]
    [InlineData("1", 1)]
    [InlineData("7", 7)]
    public void A_configured_merge_budget_is_the_budget(string value, int budget) =>
        Assert.Equal(budget, Read(null, value).MergeBudget);

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("three")]
    [InlineData("2.5")]
    [InlineData("99999999999")]
    public void A_merge_budget_that_is_not_an_integer_of_at_least_1_fails_closed_at_1(string value) =>
        Assert.Equal(1, Read(null, value).MergeBudget);
}
