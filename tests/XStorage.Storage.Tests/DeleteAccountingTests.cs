using System.Security.Cryptography;
using XStorage.Contracts;
using XStorage.Storage;

namespace XStorage.Storage.Tests;

public sealed class DeleteAccountingTests
{
    [Fact]
    public async Task DeletingTheSameObjectTwice_ReleasesItsQuotaOnce()
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
            var application = await storage.CreateApplicationAsync("delete", 8, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "delete-bucket", CancellationToken.None);
            await using var input = new MemoryStream(new byte[6], writable: false);
            await storage.PutObjectAsync(
                application.AccessKeyId,
                "delete-bucket",
                "item",
                new ObjectWriteRequest(input, 6, "application/octet-stream", null, null),
                CancellationToken.None);

            await storage.DeleteObjectAsync(application.AccessKeyId, "delete-bucket", "item", CancellationToken.None);
            await storage.DeleteObjectAsync(application.AccessKeyId, "delete-bucket", "item", CancellationToken.None);
            var beforeReuse = Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None));
            await using var fullQuotaInput = new MemoryStream(new byte[8], writable: false);
            await storage.PutObjectAsync(
                application.AccessKeyId,
                "delete-bucket",
                "replacement",
                new ObjectWriteRequest(fullQuotaInput, 8, "application/octet-stream", null, null),
                CancellationToken.None);

            var afterReuse = Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None));
            Assert.Equal(0, beforeReuse.UsedBytes);
            Assert.Equal(8, afterReuse.UsedBytes);
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
