using Gateway.E2ETests.Stack;
using Gateway.TestHarness;

namespace Gateway.E2ETests.Deployed;

/// <summary>The project's profile and Region, read from <c>tools/aws-project.env</c> for the onboarding's Terraform and spactl (#212).</summary>
public sealed class AwsProjectTests : IDisposable
{
    private readonly string file = Path.Combine(Directory.CreateTempSubdirectory("aws-project-").FullName, "aws-project.env");

    [Fact]
    public void The_profile_and_region_are_read_past_comments_and_other_keys()
    {
        File.WriteAllText(file, """
            # AWS_PROFILE=commented-out
            AWS_PROFILE=the-project

            SOMETHING_ELSE=x
            AWS_REGION=eu-west-1
            """);

        Assert.Equal(new Dictionary<string, string> { ["AWS_PROFILE"] = "the-project", ["AWS_REGION"] = "eu-west-1" }, AwsProject.Read(file));
    }

    [Theory]
    [InlineData("AWS_REGION=eu-west-1", "AWS_PROFILE")]
    [InlineData("AWS_PROFILE=\nAWS_REGION=eu-west-1", "AWS_PROFILE")]
    [InlineData("AWS_PROFILE=the-project", "AWS_REGION")]
    public void A_file_without_both_is_refused_naming_what_is_missing(string text, string missing)
    {
        File.WriteAllText(file, text);

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => AwsProject.Read(file));

        Assert.Contains(missing, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_repositorys_file_names_a_profile_that_is_not_the_default_one()
    {
        // The file's own rule: "Not "default": that is what went wrong."
        IReadOnlyDictionary<string, string> project = AwsProject.Read(AwsProject.PathIn(FixtureFiles.RepositoryRoot));

        Assert.NotEqual("default", project["AWS_PROFILE"]);
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
}
