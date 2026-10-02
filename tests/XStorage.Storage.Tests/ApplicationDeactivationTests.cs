using System.Security.Cryptography;
using XStorage.Contracts;
using XStorage.Storage;

namespace XStorage.Storage.Tests;

public sealed class ApplicationDeactivationTests
{
    [Fact]
    public async Task DeactivateApplication_BlocksAccessAndKeepsStoredData()
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
            var application = await storage.CreateApplicationAsync("deactivate", 64, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "deactivate-bucket", CancellationToken.None);
            await using var input = new MemoryStream("stored"u8.ToArray(), writable: false);
            await storage.PutObjectAsync(
                application.AccessKeyId,
                "deactivate-bucket",
                "object",
                new ObjectWriteRequest(input, input.Length, "text/plain", null, null),
                CancellationToken.None);

            await storage.DeactivateApplicationAsync(application.AccessKeyId, CancellationToken.None);

            var error = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.GetObjectAsync(
                application.AccessKeyId,
                "deactivate-bucket",
                "object",
                CancellationToken.None));
            var summary = Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None));
            Assert.Equal("AccessDenied", error.Code);
            Assert.False(summary.Active);
            Assert.Equal(6, summary.UsedBytes);
            Assert.Equal(1, summary.BucketCount);
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
