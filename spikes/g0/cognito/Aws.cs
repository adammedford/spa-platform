using Amazon;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace Spike;

/// <summary>Where the client secrets and the test users' passwords come from (Q4). Every value read becomes a canary.</summary>
public interface ISecrets
{
    Task<string> ClientSecretAsync(string appId);
    Task<string> PasswordAsync(string user);
}

/// <summary>How each OAuth client looks at the provider, for item 3 (Q18). Read only: the harness creates nothing (Q23).</summary>
public sealed record ClientDescription(string[] CallbackUrls, string[] LogoutUrls, string Rotation, int? GraceSeconds, string? BrandingId, bool? CognitoProvidedValues, string Source);

public interface IClientDescriber
{
    Task<ClientDescription> DescribeAsync(string appId, string clientId);
}

/// <summary>Parameter Store, with the workstation's credentials (assumption A7). Reads only.</summary>
public sealed class ParameterStoreSecrets(IdentityOutputs outputs, Redactor redactor) : ISecrets
{
    private readonly AmazonSimpleSystemsManagementClient _ssm = new(RegionEndpoint.GetBySystemName(outputs.Region));

    public async Task<string> ClientSecretAsync(string appId)
    {
        var c = outputs.Clients[appId];
        return await ReadAsync($"{c.SecretParameter}:{c.SecretVersion}");   // the version the outputs name, as the registry's clientSecretRef will
    }

    public Task<string> PasswordAsync(string user) => ReadAsync(outputs.PasswordParameters[user]);

    private async Task<string> ReadAsync(string name)
    {
        var response = await _ssm.GetParameterAsync(new GetParameterRequest { Name = name, WithDecryption = true });
        var value = response.Parameter.Value;
        redactor.Add(value);
        return value;
    }
}

/// <summary>DescribeUserPoolClient and DescribeManagedLoginBrandingByClient. The client description carries the secret, which goes to the redactor at once.</summary>
public sealed class CognitoDescriber(IdentityOutputs outputs, Redactor redactor) : IClientDescriber
{
    private readonly AmazonCognitoIdentityProviderClient _cognito = new(RegionEndpoint.GetBySystemName(outputs.Region));

    public async Task<ClientDescription> DescribeAsync(string appId, string clientId)
    {
        var client = (await _cognito.DescribeUserPoolClientAsync(new DescribeUserPoolClientRequest { UserPoolId = outputs.UserPoolId, ClientId = clientId })).UserPoolClient;
        redactor.Add(client.ClientSecret);
        string? brandingId = null;
        bool? provided = null;
        try
        {
            var branding = (await _cognito.DescribeManagedLoginBrandingByClientAsync(new DescribeManagedLoginBrandingByClientRequest { UserPoolId = outputs.UserPoolId, ClientId = clientId })).ManagedLoginBranding;
            brandingId = branding?.ManagedLoginBrandingId;
            provided = branding?.UseCognitoProvidedValues;
        }
        catch (Amazon.CognitoIdentityProvider.Model.ResourceNotFoundException) { }
        return new ClientDescription(
            client.CallbackURLs?.ToArray() ?? [], client.LogoutURLs?.ToArray() ?? [],
            client.RefreshTokenRotation?.Feature?.Value ?? "(not set)", client.RefreshTokenRotation?.RetryGracePeriodSeconds,
            brandingId, provided, "DescribeUserPoolClient and DescribeManagedLoginBrandingByClient");
    }
}

public static class AwsCredentials
{
    /// <summary>Null when the SDK finds credentials the usual way; otherwise why not. Nothing is printed from the credentials.</summary>
    public static string? Missing()
    {
        try
        {
            var credentials = DefaultAWSCredentialsIdentityResolver.GetCredentials();
            return credentials is null ? "the AWS SDK found no credentials" : null;
        }
        catch (Exception ex) when (ex is AmazonClientException or AmazonServiceException or InvalidOperationException)
        {
            return OneLine(ex.Message);
        }
    }

    public static string OneLine(string? s) => s is null ? "" : string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
