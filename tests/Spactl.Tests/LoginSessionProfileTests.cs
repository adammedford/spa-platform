using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.S3;

namespace Spactl.Tests;

/// <summary>
/// #235 (raised by #206): the project's profile signs in with <c>login_session</c>, which the .NET SDK
/// resolves only when the <c>AWSSDK.Signin</c> assembly is present at runtime. Without it every provider
/// in the chain fails with <c>Assembly AWSSDK.Signin could not be found or loaded</c>, which is how
/// spactl failed at sitting C. spactl's live store builds its S3 client from the SDK's default chain
/// (<see cref="SpactlEnvironment.Live"/>), so this test loads that chain against a profile file and needs
/// the assembly to arrive through spactl's own references: this project references no AWS package.
/// AWS documents the requirement under "Login using console credentials":
/// https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/creds-idc.html#login-con-creds
/// </summary>
public sealed class LoginSessionProfileTests : IDisposable
{
    // A name no workstation's own ~/.aws/config carries, so the chain can only find the profile here.
    private const string ProfileName = "spactl-tests-login-session";

    private readonly string directory = Directory.CreateTempSubdirectory("spactl-login-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void The_default_chain_resolves_a_login_session_profile_without_an_AWS_call()
    {
        string file = Path.Combine(directory, "credentials");
        File.WriteAllText(file, $"""
            [{ProfileName}]
            login_session = arn:aws:iam::000000000000:user/spactl-tests
            region = eu-west-2
            """);

        // The profile on the client configuration is the chain's first source, so neither the
        // environment's credentials nor instance metadata are consulted. Building LoginAWSCredentials
        // loads the Signin client, which is where the missing assembly threw; nothing asks it for a
        // token, so no request leaves the process.
        var config = new AmazonS3Config
        {
            Profile = new Profile(ProfileName, file),
            RegionEndpoint = RegionEndpoint.EUWest2,
        };

        AWSCredentials credentials = DefaultAWSCredentialsIdentityResolver.GetCredentials(config);

        Assert.IsType<LoginAWSCredentials>(credentials);
    }
}
