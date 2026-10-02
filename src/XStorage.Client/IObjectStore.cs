using XStorage.Contracts;

namespace XStorage.Client;

/// <summary>Provides typed object operations over the standalone S3 HTTP API.</summary>
public interface IObjectStore
{
    /// <summary>Creates a private bucket for the configured application.</summary>
    Task CreateBucketAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>Uploads one whole object from a seekable stream positioned at zero.</summary>
    /// <remarks>The caller owns <paramref name="content"/> and MUST keep it open until this call completes.</remarks>
    Task<ObjectStorePutResult> PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        long contentLength,
        string? contentType = null,
        CancellationToken cancellationToken = default);

    /// <summary>Opens one complete object and returns its metadata and readable stream.</summary>
    /// <remarks>The caller owns the returned object. The caller MUST dispose it to close the response stream.</remarks>
    Task<ObjectStoreObject> GetObjectAsync(string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>Reads object metadata without downloading the object body.</summary>
    Task<ObjectMetadata> HeadObjectAsync(string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes one object. Deleting a missing key succeeds.</summary>
    Task DeleteObjectAsync(string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>Reads one ListObjectsV2 page.</summary>
    Task<ObjectListingPage> ListObjectsV2Async(
        string bucket,
        ObjectListingRequest request,
        CancellationToken cancellationToken = default);
}
