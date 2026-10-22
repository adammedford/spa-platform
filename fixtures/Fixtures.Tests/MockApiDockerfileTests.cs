namespace Fixtures.Tests;

/// <summary>
/// <c>fixtures/MockApi/Dockerfile</c> makes its published tree readable by the runtime user (#233).
/// The final stage runs as <c>$APP_UID</c>, and publish and COPY keep the modes the files had in the
/// build context, so an owner-only file on a workstation reached the gateway image owner-only and the
/// process died at startup (#204). The gateway's Dockerfile answers that with one step after its
/// publish; this image takes the same step, character for character, so the integrator's companion in
/// <c>tools/image_build_tests.py</c> can hold both files to one case without another edit here.
/// </summary>
public sealed class MockApiDockerfileTests
{
    private const string ChmodStep = "RUN chmod -R a+rX,go-w /app";

    private static readonly string[] MockApi =
        File.ReadAllLines(FixtureFiles.Path("fixtures", "MockApi", "Dockerfile"));

    private static readonly string[] Gateway =
        File.ReadAllLines(FixtureFiles.Path("src", "Gateway", "Dockerfile"));

    [Fact]
    public void The_build_stage_opens_the_published_tree_after_publish_and_before_the_final_stage()
    {
        int publish = IndexOf(MockApi, line => line.StartsWith("RUN dotnet publish ", StringComparison.Ordinal) &&
                                               line.EndsWith(" -o /app", StringComparison.Ordinal));
        int chmod = IndexOf(MockApi, line => line == ChmodStep);
        int final = IndexOf(MockApi, line => line.StartsWith("FROM ", StringComparison.Ordinal) &&
                                             line.EndsWith(" AS final", StringComparison.Ordinal));

        Assert.True(publish >= 0, "the mock API Dockerfile has no publish step into /app.");
        Assert.True(chmod > publish, $"'{ChmodStep}' must follow the publish step (line {publish + 1}).");
        Assert.True(chmod < final, $"'{ChmodStep}' must come before the final stage (line {final + 1}).");
    }

    [Fact]
    public void The_step_is_the_gateways_own()
    {
        // #233's second box: the same step as src/Gateway/Dockerfile, so a case written once holds both.
        Assert.Equal(
            Gateway.Where(line => line.Contains("chmod", StringComparison.Ordinal) && line.StartsWith("RUN ", StringComparison.Ordinal)),
            MockApi.Where(line => line.Contains("chmod", StringComparison.Ordinal) && line.StartsWith("RUN ", StringComparison.Ordinal)));
        Assert.Contains(ChmodStep, Gateway);
    }

    private static int IndexOf(string[] lines, Func<string, bool> match) =>
        Array.FindIndex(lines, line => match(line.Trim()));
}
