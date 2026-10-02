using System.Net;
using System.Security.Cryptography;
using System.Text;
using XStorage.Contracts;
using XStorage.Server;

namespace XStorage.Server.Tests;

public sealed class CredentialCutoverRaceTests
{
    private const string AccessKeyId = "OSAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public async Task RequestWaitingForSigV4VerificationAfterRotation_ReturnsSignatureDoesNotMatch()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            $"xstorage-cutover-race-{Guid.NewGuid():N}");
        var originalSecret = NewSecret();
        var storage = new RotationRaceStorage(originalSecret, NewSecret());

        await using var host = await StorageServiceHost.StartWithStorageForTestsAsync(
            ServerTestSupport.Settings(root), storage);
        using var dataClient = new HttpClient { BaseAddress = host.DataAddress };
        using var request = CreateSignedGet(host.DataAddress, originalSecret);
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-9");
        var pendingRequest = dataClient.SendAsync(request);
        await storage.SigningKeyCaptured.WaitAsync(TimeSpan.FromSeconds(5));

        using var admin = ServerTestSupport.CreateAdminClient(host.AdminAddress);
        using var rotated = await admin.PostAsync($"/admin/applications/{AccessKeyId}/rotate", content: null);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        storage.ReleaseSigningKey();

        using var response = await pendingRequest;
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("<Code>SignatureDoesNotMatch</Code>", body, StringComparison.Ordinal);
    }

    private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static HttpRequestMessage CreateSignedGet(Uri endpoint, string secret)
    {
        var path = "/race-bucket/object";
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var date = timestamp[..8];
        var payloadHash = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalHeaders = $"host:{endpoint.Authority}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{timestamp}\n";
        var canonicalRequest = string.Join('\n', "GET", path, string.Empty, canonicalHeaders, signedHeaders, payloadHash);
        var canonicalHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant();
        var scope = $"{date}/us-east-1/s3/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{timestamp}\n{scope}\n{canonicalHash}";
        var dateKey = Hmac(Encoding.UTF8.GetBytes("AWS4" + secret), date);
        var regionKey = Hmac(dateKey, "us-east-1");
        var serviceKey = Hmac(regionKey, "s3");
        var signingKey = Hmac(serviceKey, "aws4_request");
        var signature = Convert.ToHexString(Hmac(signingKey, stringToSign)).ToLowerInvariant();
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("x-amz-date", timestamp);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("Authorization",
            $"AWS4-HMAC-SHA256 Credential={AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
        return request;
    }

    private static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    private sealed class RotationRaceStorage(string originalSecret, string rotatedSecret) : IObjectStorage
    {
        private readonly TaskCompletionSource _signingKeyCaptured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSigningKey = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string _currentSecret = originalSecret;
        private long _credentialVersion;

        public Task SigningKeyCaptured => _signingKeyCaptured.Task;

        public void ReleaseSigningKey() => _releaseSigningKey.TrySetResult();

        public async ValueTask<ApplicationSigningKey?> FindSigningKeyAsync(string accessKeyId, CancellationToken cancellationToken)
        {
            if (!StringComparer.Ordinal.Equals(accessKeyId, AccessKeyId))
            {
                return null;
            }

            var snapshot = new ApplicationSigningKey(accessKeyId, _currentSecret, true, _credentialVersion);
            _signingKeyCaptured.TrySetResult();
            await _releaseSigningKey.Task.WaitAsync(cancellationToken);
            return snapshot;
        }

        public Task<ApplicationCredentials> RotateCredentialsAsync(string accessKeyId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!StringComparer.Ordinal.Equals(accessKeyId, AccessKeyId))
            {
                throw new ObjectStorageException("NoSuchApplication", "The application does not exist.", 404);
            }

            _currentSecret = rotatedSecret;
            _credentialVersion++;
            return Task.FromResult(new ApplicationCredentials(accessKeyId, rotatedSecret, "rotation-race", 1024));
        }

        public Task<bool> ConfirmSigningKeyAsync(string accessKeyId, long credentialVersion, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(StringComparer.Ordinal.Equals(accessKeyId, AccessKeyId) &&
                credentialVersion == _credentialVersion);
        }

        public Task AuthorizeBucketAsync(string accessKeyId, string bucket, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ApplicationCredentials> CreateApplicationAsync(string name, long quotaBytes, CancellationToken cancellationToken) => throw NotUsed();
        public Task UpdateQuotaAsync(string accessKeyId, long quotaBytes, CancellationToken cancellationToken) => throw NotUsed();
        public Task DeactivateApplicationAsync(string accessKeyId, CancellationToken cancellationToken) => throw NotUsed();
        public Task<IReadOnlyList<ApplicationSummary>> ListApplicationsAsync(CancellationToken cancellationToken) => throw NotUsed();
        public Task AuthorizeBucketCreationAsync(string accessKeyId, string bucket, CancellationToken cancellationToken) => throw NotUsed();
        public Task CreateBucketAsync(string accessKeyId, string bucket, CancellationToken cancellationToken) => throw NotUsed();
        public Task<ObjectMetadata> PutObjectAsync(string accessKeyId, string bucket, string key, ObjectWriteRequest request, CancellationToken cancellationToken) => throw NotUsed();
        public Task<StoredObject> GetObjectAsync(string accessKeyId, string bucket, string key, CancellationToken cancellationToken) => throw NotUsed();
        public Task<ObjectMetadata> HeadObjectAsync(string accessKeyId, string bucket, string key, CancellationToken cancellationToken) => throw NotUsed();
        public Task DeleteObjectAsync(string accessKeyId, string bucket, string key, CancellationToken cancellationToken) => throw NotUsed();
        public Task<ObjectListingPage> ListObjectsV2Async(string accessKeyId, string bucket, ObjectListingRequest request, CancellationToken cancellationToken) => throw NotUsed();

        private static NotSupportedException NotUsed() => new("This test only exercises credential lookup, rotation, and authorization.");
    }
}
