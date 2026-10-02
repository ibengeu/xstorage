using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using XStorage.Contracts;

namespace XStorage.Client;

/// <summary>Implements <see cref="IObjectStore"/> over HTTP with AWSSDK.S3.</summary>
public sealed class S3ObjectStore : IObjectStore, IDisposable, IAsyncDisposable
{
    private readonly AmazonS3Client _client;

    /// <summary>Creates a path-style S3 client for the service's fixed us-east-1 region.</summary>
    public S3ObjectStore(ObjectStoreClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out var serviceUri) ||
            serviceUri.Scheme is not "http" and not "https" ||
            string.IsNullOrWhiteSpace(options.AccessKeyId) ||
            string.IsNullOrWhiteSpace(options.SecretAccessKey))
        {
            throw new ArgumentException("The service URL and application credentials are required.", nameof(options));
        }

        var config = new AmazonS3Config
        {
            ServiceURL = serviceUri.ToString().TrimEnd('/'),
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };
        _client = new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey),
            config);
    }

    /// <inheritdoc />
    public async Task CreateBucketAsync(string bucket, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(() => _client.PutBucketAsync(new PutBucketRequest
        {
            BucketName = bucket
        }, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<ObjectStorePutResult> PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        long contentLength,
        string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        ValidateUploadStream(content, contentLength);
        var response = await ExecuteAsync(() => _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
            AutoResetStreamPosition = false,
            UseChunkEncoding = false,
            ContentType = contentType
        }, cancellationToken));
        return new ObjectStorePutResult(response.ETag);
    }

    /// <inheritdoc />
    public async Task<ObjectStoreObject> GetObjectAsync(string bucket, string key,
        CancellationToken cancellationToken = default)
    {
        var response = await ExecuteAsync(() => _client.GetObjectAsync(bucket, key, cancellationToken));
        return new ObjectStoreObject(
            new ObjectMetadata(key, response.ContentLength, response.Headers.ContentType, response.ETag,
                ToDateTimeOffset(response.LastModified)),
            response);
    }

    /// <inheritdoc />
    public async Task<ObjectMetadata> HeadObjectAsync(string bucket, string key,
        CancellationToken cancellationToken = default)
    {
        var response = await ExecuteAsync(() => _client.GetObjectMetadataAsync(bucket, key, cancellationToken));
        return new ObjectMetadata(key, response.ContentLength, response.Headers.ContentType, response.ETag,
            ToDateTimeOffset(response.LastModified));
    }

    /// <inheritdoc />
    public async Task DeleteObjectAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(() => _client.DeleteObjectAsync(bucket, key, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<ObjectListingPage> ListObjectsV2Async(
        string bucket,
        ObjectListingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await ExecuteAsync(() => _client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket,
            Prefix = request.Prefix,
            Delimiter = request.Delimiter,
            StartAfter = request.StartAfter,
            ContinuationToken = request.ContinuationToken,
            MaxKeys = request.MaxKeys,
            Encoding = request.EncodingType == "url" ? EncodingType.Url : null
        }, cancellationToken));

        var contents = response.S3Objects.Select(item => new ListedObject(
            item.Key,
            ToDateTimeOffset(item.LastModified),
            item.ETag,
            item.Size ?? 0,
            item.StorageClass?.Value ?? "STANDARD")).ToArray();
        return new ObjectListingPage(
            bucket,
            response.Prefix,
            response.KeyCount ?? 0,
            response.MaxKeys ?? 0,
            response.IsTruncated ?? false,
            contents,
            response.CommonPrefixes,
            response.StartAfter,
            response.Delimiter,
            response.ContinuationToken,
            response.NextContinuationToken,
            response.Encoding);
    }

    /// <summary>Closes the underlying AWS SDK client.</summary>
    public void Dispose() => _client.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private static void ValidateUploadStream(Stream content, long contentLength)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead || !content.CanSeek || content.Position != 0 || contentLength < 0 ||
            content.Length != contentLength)
        {
            throw new ArgumentException(
                "Uploads require a readable seekable stream at position zero with the declared length.",
                nameof(content));
        }
    }

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (AmazonS3Exception exception)
        {
            throw MapException(exception);
        }
    }

    private static async Task ExecuteAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (AmazonS3Exception exception)
        {
            throw MapException(exception);
        }
    }

    private static ObjectStoreException MapException(AmazonS3Exception exception) => new(
        exception.ErrorCode ?? "InternalError",
        exception.Message,
        (int)exception.StatusCode,
        exception.RequestId,
        exception);

    private static DateTimeOffset ToDateTimeOffset(DateTime? value) =>
        value is null
            ? DateTimeOffset.UnixEpoch
            : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
}