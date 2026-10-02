using System.Security.Cryptography;
using OptiStorage.Contracts;
using OptiStorage.Storage;

namespace OptiStorage.Storage.Tests;

public sealed class ObjectKeyValidationTests
{
    [Fact]
    public async Task PutObject_EnforcesThe1024ByteUtf8KeyLimit()
    {
        var root = Path.Combine(
            OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            $"optistorage-key-limit-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
        var validKey = string.Concat(Enumerable.Repeat("é", 512));
        var invalidKey = validKey + "é";

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("key-limit", 16, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "key-limit-bucket", CancellationToken.None);
            await using var validInput = new MemoryStream("v"u8.ToArray(), writable: false);
            await storage.PutObjectAsync(
                application.AccessKeyId,
                "key-limit-bucket",
                validKey,
                new ObjectWriteRequest(validInput, validInput.Length, "text/plain", null, null),
                CancellationToken.None);

            await using var invalidInput = new MemoryStream("i"u8.ToArray(), writable: false);
            var error = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.PutObjectAsync(
                application.AccessKeyId,
                "key-limit-bucket",
                invalidKey,
                new ObjectWriteRequest(invalidInput, invalidInput.Length, "text/plain", null, null),
                CancellationToken.None));
            var metadata = await storage.HeadObjectAsync(
                application.AccessKeyId,
                "key-limit-bucket",
                validKey,
                CancellationToken.None);

            Assert.Equal("InvalidArgument", error.Code);
            Assert.Equal(400, error.StatusCode);
            Assert.Equal(validKey, metadata.Key);
            Assert.Equal(1, Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None)).UsedBytes);
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
