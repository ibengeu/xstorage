using System.Security.Cryptography;
using XStorage.Contracts;
using XStorage.Storage;

namespace XStorage.Storage.Tests;

public sealed class TemporaryFileRecoveryTests
{
    [Fact]
    public async Task OpenAsync_RemovesAbandonedTemporaryFilesAndKeepsCommittedObjects()
    {
        var root = Path.Combine(
            OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            $"xstorage-temp-recovery-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
        string accessKeyId;

        try
        {
            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var application = await storage.CreateApplicationAsync("temp-recovery", 64, CancellationToken.None);
                accessKeyId = application.AccessKeyId;
                await storage.CreateBucketAsync(accessKeyId, "temp-recovery-bucket", CancellationToken.None);
                await using var input = new MemoryStream("committed"u8.ToArray(), writable: false);
                await storage.PutObjectAsync(
                    accessKeyId,
                    "temp-recovery-bucket",
                    "object",
                    new ObjectWriteRequest(input, input.Length, "text/plain", null, null),
                    CancellationToken.None);
            }

            var applicationTemp = Path.Combine(root, "state", "applications", "abandoned.json.tmp");
            var bucketTemp = Path.Combine(root, "buckets", ".create-abandoned.tmp");
            var objectTemp = Directory.EnumerateDirectories(
                    Path.Combine(root, "buckets", "temp-recovery-bucket", "objects"),
                    "*",
                    SearchOption.AllDirectories)
                .First();
            var uploadTemp = Path.Combine(objectTemp, ".upload-abandoned.tmp");
            var deleteTemp = Path.Combine(objectTemp, ".delete-abandoned.tmp");
            await File.WriteAllBytesAsync(applicationTemp, [1]);
            Directory.CreateDirectory(bucketTemp);
            await File.WriteAllBytesAsync(uploadTemp, [2]);
            await File.WriteAllBytesAsync(deleteTemp, [3]);

            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                await using var stored = await storage.GetObjectAsync(
                    accessKeyId,
                    "temp-recovery-bucket",
                    "object",
                    CancellationToken.None);
                using var output = new MemoryStream();
                await stored.Content.CopyToAsync(output);

                Assert.Equal("committed"u8.ToArray(), output.ToArray());
            }

            Assert.False(File.Exists(applicationTemp));
            Assert.False(Directory.Exists(bucketTemp));
            Assert.False(File.Exists(uploadTemp));
            Assert.False(File.Exists(deleteTemp));
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
