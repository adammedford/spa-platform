using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Platform.Abstractions;

namespace Platform.Registry;

/// <summary>
/// <see cref="IObjectStore"/> over one S3 bucket (plan row 2.2, <c>contracts/s3-layout.md</c>). The
/// registry source reads through it with a conditional GET; <c>spactl</c> (task 2.6) writes through it.
/// It passes the same <c>ObjectStoreContract</c> suite as the in-memory double, and first meets a real
/// bucket in task 1.7.
///
/// The SDK answers S3's conditional statuses with exceptions, not responses, so each one is read back
/// into the interface's own terms here: 304 is <see cref="StoredObject.NotModified"/>, a missing key is
/// null, and a refused write is false. Every other failure propagates to the caller.
/// </summary>
public sealed class S3ObjectStore : IObjectStore
{
    private readonly IAmazonS3 s3;
    private readonly string bucket;

    public S3ObjectStore(IAmazonS3 s3, string bucket)
    {
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentException.ThrowIfNullOrEmpty(bucket);
        this.s3 = s3;
        this.bucket = bucket;
    }

    /// <inheritdoc />
    public async Task<StoredObject?> GetAsync(string key, string? ifNoneMatch, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        GetObjectResponse response;
        try
        {
            // If-None-Match: S3 answers 304 with no body when the ETag still matches
            // (https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObject.html, "If-None-Match").
            response = await s3.GetObjectAsync(
                new GetObjectRequest { BucketName = bucket, Key = key, EtagToNotMatch = ifNoneMatch },
                ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotModified && ifNoneMatch is not null)
        {
            return new StoredObject(Body: null, ifNoneMatch, Length: null, NotModified: true);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound && ex.ErrorCode == "NoSuchKey")
        {
            // Only NoSuchKey is a missing key. NoSuchBucket is a configuration fault, and so is the 403
            // S3 returns for a missing key when the caller lacks s3:ListBucket (same page, "Permissions"):
            // both propagate rather than read as "no registry".
            return null;
        }

        // The caller owns the stream (IObjectStore). Disposing it releases the connection.
        return new StoredObject(response.ResponseStream, response.ETag, response.ContentLength, NotModified: false);
    }

    /// <inheritdoc />
    public async Task<bool> PutAsync(string key, Stream body, string? ifMatch, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(body);

        // A single-part PutObject, as s3-layout.md requires: the write-once rule is If-None-Match: * and
        // the replace rule If-Match, both evaluated by S3 itself
        // (https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes.html).
        PutObjectRequest request = new()
        {
            BucketName = bucket,
            Key = key,
            InputStream = body,
            // The stream is the caller's: the SDK would otherwise close it once read.
            AutoCloseStream = false,
        };
        if (ifMatch is null)
        {
            request.IfNoneMatch = "*";
        }
        else
        {
            request.IfMatch = ifMatch;
        }

        try
        {
            await s3.PutObjectAsync(request, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (IsRefusal(ex, ifMatch))
        {
            return false;
        }
    }

    /// <summary>
    /// The three ways S3 declines a conditional write (same page as above): 412 when the condition
    /// fails, 409 <c>ConditionalRequestConflict</c> when another conditional write to the key is in
    /// flight, and 404 <c>NoSuchKey</c> when <c>If-Match</c> names a key that does not exist.
    /// </summary>
    private static bool IsRefusal(AmazonS3Exception ex, string? ifMatch) =>
        ex.StatusCode == HttpStatusCode.PreconditionFailed
        || (ex.StatusCode == HttpStatusCode.Conflict && ex.ErrorCode == "ConditionalRequestConflict")
        || (ifMatch is not null && ex.StatusCode == HttpStatusCode.NotFound && ex.ErrorCode == "NoSuchKey");
}
