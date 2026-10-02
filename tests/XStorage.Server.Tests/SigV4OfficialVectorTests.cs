using System.Net;
using XStorage.Contracts;
using XStorage.Server;

namespace XStorage.Server.Tests;

public sealed class SigV4OfficialVectorTests
{
    private const string AccessKeyId = "AKIAIOSFODNN7EXAMPLE";
    private const string SecretAccessKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

    [Fact]
    public async Task OfficialAwsGetObjectVectorPassesAuthenticationBeforeUnsupportedRangeHandling()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            $"xstorage-sigv4-vector-{Guid.NewGuid():N}");
        var settings = ServerTestSupport.Settings(root) with
        {
            TimeProviderOverride = new FixedTimeProvider(new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero))
        };

        await using var host = await StorageServiceHost.StartWithStorageForTestsAsync(settings, new VectorStorage());
        using var client = new HttpClient { BaseAddress = host.DataAddress };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/test.txt");
        request.Headers.Host = "examplebucket.s3.amazonaws.com";
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-9");
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        request.Headers.TryAddWithoutValidation("x-amz-date", "20130524T000000Z");
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request," +
            "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date," +
            "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains("<Code>NotImplemented</Code>", body, StringComparison.Ordinal);
        Assert.True(response.Headers.Contains("x-amz-request-id"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class VectorStorage : IObjectStorage
    {
        public ValueTask<ApplicationSigningKey?> FindSigningKeyAsync(string accessKeyId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplicationSigningKey? key = StringComparer.Ordinal.Equals(accessKeyId, AccessKeyId)
                ? new ApplicationSigningKey(AccessKeyId, SecretAccessKey, true)
                : null;
            return ValueTask.FromResult(key);
        }

        public Task<bool> ConfirmSigningKeyAsync(string accessKeyId, long credentialVersion, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(StringComparer.Ordinal.Equals(accessKeyId, AccessKeyId) && credentialVersion == 0);
        }

        public Task AuthorizeBucketAsync(string accessKeyId, string bucket, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ApplicationCredentials> CreateApplicationAsync(string name, long quotaBytes, CancellationToken cancellationToken) =>
            throw NotUsed();

        public Task<ApplicationCredentials> RotateCredentialsAsync(string accessKeyId, CancellationToken cancellationToken) =>
            throw NotUsed();

        public Task UpdateQuotaAsync(string accessKeyId, long quotaBytes, CancellationToken cancellationToken) =>
            throw NotUsed();

        public Task DeactivateApplicationAsync(string accessKeyId, CancellationToken cancellationToken) =>
            throw NotUsed();

        public Task<IReadOnlyList<ApplicationSummary>> ListApplicationsAsync(CancellationToken cancellationToken) =>
            throw NotUsed();

        public Task AuthorizeBucketCreationAsync(string accessKeyId, string bucket, CancellationToken cancellationToken) =>
            throw NotUsed();

        public Task CreateBucketAsync(string accessKeyId, string bucket, CancellationToken cancellationToken) =>
            throw NotUsed();

        public Task<ObjectMetadata> PutObjectAsync(string accessKeyId, string bucket, string key, ObjectWriteRequest request, CancellationToken cancellationToken) =>
            throw NotUsed();

        public Task<StoredObject> GetObjectAsync(string accessKeyId, string bucket, string key, CancellationToken cancellationToken) =>
            throw NotUsed();

        public Task<ObjectMetadata> HeadObjectAsync(string accessKeyId, string bucket, string key, CancellationToken cancellationToken) =>
            throw NotUsed();

        public Task DeleteObjectAsync(string accessKeyId, string bucket, string key, CancellationToken cancellationToken) =>
            throw NotUsed();

        public Task<ObjectListingPage> ListObjectsV2Async(string accessKeyId, string bucket, ObjectListingRequest request, CancellationToken cancellationToken) =>
            throw NotUsed();

        private static NotSupportedException NotUsed() =>
            new("This official SigV4 vector stops at request authentication.");
    }
}
