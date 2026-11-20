using Microsoft.Extensions.Configuration;
using Platform.Abstractions;

namespace Platform.Registry.Tests;

/// <summary>
/// <c>Policy:FragmentsPerShell</c> as <see cref="PlatformPolicyConfiguration.Read"/> takes it from
/// configuration (#992): the cap is a <c>Policy:</c> value that G9's sitting replaces, so a deployment
/// changes it by configuration alone (the cell module's <c>fragments_per_shell</c> input, #1099).
/// </summary>
public sealed class PolicyConfigurationTests
{
    private static PlatformPolicy Read(string? fragmentsPerShell)
    {
        Dictionary<string, string?> values = new(StringComparer.Ordinal)
        {
            ["Policy:AllowedHostSuffixes:0"] = ".spa-poc.test:8443",
        };
        if (fragmentsPerShell is not null)
        {
            values["Policy:FragmentsPerShell"] = fragmentsPerShell;
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
}
