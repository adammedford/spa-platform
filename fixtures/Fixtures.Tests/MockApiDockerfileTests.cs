using System.Text.RegularExpressions;

namespace Fixtures.Tests;

/// <summary>
/// <c>fixtures/MockApi/Dockerfile</c> builds the image that runs beside the gateway in its ARM64
/// task, and takes the gateway Dockerfile's own lines wherever the two images must agree.
///
/// It makes its published tree readable by the runtime user (#233). The final stage runs as
/// <c>$APP_UID</c>, and publish and COPY keep the modes the files had in the build context, so an
/// owner-only file on a workstation reached the gateway image owner-only and the process died at
/// startup (#204). The gateway's Dockerfile answers that with one step after its publish; this image
/// takes the same step, character for character, so the integrator's companion in
/// <c>tools/image_build_tests.py</c> can hold both files to one case without another edit here.
///
/// It builds on the gateway's bases, pinned by the same digests, with the gateway's diagnostics line
/// and OCI labels (#781, #782). Each case compares this file's lines with <c>src/Gateway/Dockerfile</c>'s
/// rather than with a copy kept here, so the gateway's next digest fails this suite until the mock
/// API's Dockerfile takes it too.
/// </summary>
public sealed partial class MockApiDockerfileTests
{
    private const string ChmodStep = "RUN chmod -R a+rX,go-w /app";

    private const string DiagnosticsLine = "ENV DOTNET_EnableDiagnostics=0";

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

    [Fact]
    public void Both_from_lines_are_the_gateways_own()
    {
        // The build stage and the chiseled -extra runtime stage, each by the gateway's digest (#782).
        string[] gateway = Lines(Gateway, "FROM ");
        Assert.Equal(2, gateway.Length);
        Assert.Equal(gateway, Lines(MockApi, "FROM "));
    }

    [Fact]
    public void Every_from_line_names_its_base_by_digest()
    {
        // Held on this file's own lines, so a digest dropped from both Dockerfiles at once still fails.
        string[] from = Lines(MockApi, "FROM ");
        Assert.NotEmpty(from);
        Assert.All(from, line => Assert.Matches(PinnedFrom(), line));
    }

    [Fact]
    public void The_final_stage_turns_diagnostics_off_as_the_gateway_does()
    {
        Assert.Contains(DiagnosticsLine, Gateway);
        Assert.Equal(Lines(Gateway, "ENV DOTNET_EnableDiagnostics"), Lines(MockApi, "ENV DOTNET_EnableDiagnostics"));
        Assert.True(
            Array.IndexOf(MockApi, DiagnosticsLine) > IndexOf(MockApi, line => line.EndsWith(" AS final", StringComparison.Ordinal)),
            $"'{DiagnosticsLine}' must be set in the final stage, which is the image that runs.");
    }

    [Fact]
    public void The_oci_labels_and_their_build_arguments_are_the_gateways_own()
    {
        string[] labels = Lines(Gateway, "LABEL ");
        Assert.Equal(
            ["source", "revision", "version"],
            labels.Select(line => line["LABEL org.opencontainers.image.".Length..].Split('=')[0]));
        Assert.Equal(labels, Lines(MockApi, "LABEL "));
        Assert.Equal(Lines(Gateway, "ARG "), Lines(MockApi, "ARG "));
    }

    private static string[] Lines(string[] lines, string prefix) =>
        [.. lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal))];

    private static int IndexOf(string[] lines, Func<string, bool> match) =>
        Array.FindIndex(lines, line => match(line.Trim()));

    [GeneratedRegex(@"^FROM (--platform=\S+ )?\S+:\S+@sha256:[0-9a-f]{64} AS [a-z]+$", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex PinnedFrom();
}
