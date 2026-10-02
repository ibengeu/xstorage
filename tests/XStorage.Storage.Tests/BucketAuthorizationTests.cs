using System.Security.Cryptography;
using System.Text;
using XStorage.Contracts;
using XStorage.Storage;

namespace XStorage.Storage.Tests;

public sealed class BucketAuthorizationTests
{
    [Fact]
    public async Task RequestsAgainstAnotherApplicationsBucketReturnAccessDenied()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"xstorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var reader = await storage.CreateApplicationAsync("reader", 64, CancellationToken.None);
            var owner = await storage.CreateApplicationAsync("owner", 64, CancellationToken.None);
            await storage.CreateBucketAsync(owner.AccessKeyId, "owned-bucket", CancellationToken.None);
            await using var storedBody = new MemoryStream("private"u8.ToArray(), writable: false);
            await storage.PutObjectAsync(
                owner.AccessKeyId,
                "owned-bucket",
                "existing",
                new ObjectWriteRequest(storedBody, storedBody.Length, "text/plain", null, null),
                CancellationToken.None);

            var getError = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.GetObjectAsync(
                reader.AccessKeyId,
                "owned-bucket",
                "missing",
                CancellationToken.None));
            await using var attemptedBody = new MemoryStream(Encoding.UTF8.GetBytes("unauthorized"), writable: false);
            var putError = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.PutObjectAsync(
                reader.AccessKeyId,
                "owned-bucket",
                "new-object",
                new ObjectWriteRequest(attemptedBody, attemptedBody.Length, "text/plain", null, null),
                CancellationToken.None));
            var listError = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.ListObjectsV2Async(
                reader.AccessKeyId,
                "owned-bucket",
                new ObjectListingRequest(),
                CancellationToken.None));

            Assert.Equal("AccessDenied", getError.Code);
            Assert.Equal("AccessDenied", putError.Code);
            Assert.Equal("AccessDenied", listError.Code);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
