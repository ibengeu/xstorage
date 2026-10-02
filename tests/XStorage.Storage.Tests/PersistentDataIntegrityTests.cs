using System.Security.Cryptography;
using XStorage.Contracts;
using XStorage.Storage;

namespace XStorage.Storage.Tests;

public sealed class PersistentDataIntegrityTests
{
    [Theory]
    [InlineData("ownership-marker")]
    [InlineData("object-record")]
    public async Task OpenAsync_RefusesCorruptCommittedData(string recordType)
    {
        var root = Path.Combine(
            OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            $"xstorage-corrupt-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var application = await storage.CreateApplicationAsync("integrity", 64, CancellationToken.None);
                await storage.CreateBucketAsync(application.AccessKeyId, "integrity-bucket", CancellationToken.None);
                await using var input = new MemoryStream("committed"u8.ToArray(), writable: false);
                await storage.PutObjectAsync(
                    application.AccessKeyId,
                    "integrity-bucket",
                    "object",
                    new ObjectWriteRequest(input, input.Length, "text/plain", null, null),
                    CancellationToken.None);
            }

            CorruptCommittedRecord(root, recordType);

            await Assert.ThrowsAsync<IOException>(() => ObjectStorage.OpenAsync(options, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void CorruptCommittedRecord(string root, string recordType)
    {
        var path = recordType == "ownership-marker"
            ? Path.Combine(root, "buckets", "integrity-bucket", ".owner")
            : Directory.EnumerateFiles(
                    Path.Combine(root, "buckets", "integrity-bucket", "objects"),
                    "*.obj",
                    SearchOption.AllDirectories)
                .Single();
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xff;
        File.WriteAllBytes(path, bytes);
    }
}
