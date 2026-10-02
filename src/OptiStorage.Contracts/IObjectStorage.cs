namespace OptiStorage.Contracts;

/// <summary>Public storage boundary used by the S3 and admin listeners.</summary>
public interface IObjectStorage : IAsyncDisposable
{
    Task<ApplicationCredentials> CreateApplicationAsync(string name, long quotaBytes, CancellationToken cancellationToken);

    Task<ApplicationCredentials> RotateCredentialsAsync(string accessKeyId, CancellationToken cancellationToken);

    Task UpdateQuotaAsync(string accessKeyId, long quotaBytes, CancellationToken cancellationToken);

    Task DeactivateApplicationAsync(string accessKeyId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApplicationSummary>> ListApplicationsAsync(CancellationToken cancellationToken);

    ValueTask<ApplicationSigningKey?> FindSigningKeyAsync(string accessKeyId, CancellationToken cancellationToken);

    Task<bool> ConfirmSigningKeyAsync(string accessKeyId, long credentialVersion, CancellationToken cancellationToken);

    Task AuthorizeBucketAsync(string accessKeyId, string bucket, CancellationToken cancellationToken);

    Task AuthorizeBucketCreationAsync(string accessKeyId, string bucket, CancellationToken cancellationToken);

    Task CreateBucketAsync(string accessKeyId, string bucket, CancellationToken cancellationToken);

    Task<ObjectMetadata> PutObjectAsync(
        string accessKeyId,
        string bucket,
        string key,
        ObjectWriteRequest request,
        CancellationToken cancellationToken);

    Task<StoredObject> GetObjectAsync(
        string accessKeyId,
        string bucket,
        string key,
        CancellationToken cancellationToken);

    Task<ObjectMetadata> HeadObjectAsync(
        string accessKeyId,
        string bucket,
        string key,
        CancellationToken cancellationToken);

    Task DeleteObjectAsync(string accessKeyId, string bucket, string key, CancellationToken cancellationToken);

    Task<ObjectListingPage> ListObjectsV2Async(
        string accessKeyId,
        string bucket,
        ObjectListingRequest request,
        CancellationToken cancellationToken);
}
