using Amazon.S3;
using Amazon.S3.Model;

namespace Spactl;

/// <summary>
/// <see cref="IRegistryHistory"/> over one versioned S3 bucket. Unproven against S3 here by design: the
/// plan row of task 2.6 hands it to task 6.9, which runs rollback on the deployed bucket.
/// </summary>
public sealed class S3RegistryHistory(IAmazonS3 s3, string bucket) : IRegistryHistory
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListVersionsAsync(string key, CancellationToken ct)
    {
        List<string> ids = [];
        ListVersionsRequest request = new() { BucketName = bucket, Prefix = key };
        while (true)
        {
            ListVersionsResponse response = await s3.ListVersionsAsync(request, ct);

            // ListObjectVersions returns a key's versions most recent first
            // (https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListObjectVersions.html, "Versions":
            // "the versions are listed ... with the most recent version first"), and pages continue in that
            // order. Prefix also matches longer keys, so only the exact key is kept. SDK v4 leaves an empty
            // collection null (https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/net-dg-v4.html).
            foreach (S3ObjectVersion version in response.Versions ?? [])
            {
                if (version.Key == key && version.IsDeleteMarker != true)
                {
                    ids.Add(version.VersionId);
                }
            }

            if (response.IsTruncated != true)
            {
                return ids;
            }

            request.KeyMarker = response.NextKeyMarker;
            request.VersionIdMarker = response.NextVersionIdMarker;
        }
    }

    /// <inheritdoc />
    public async Task<Stream> OpenVersionAsync(string key, string versionId, CancellationToken ct)
    {
        GetObjectResponse response = await s3.GetObjectAsync(
            new GetObjectRequest { BucketName = bucket, Key = key, VersionId = versionId }, ct);
        return response.ResponseStream;
    }
}
