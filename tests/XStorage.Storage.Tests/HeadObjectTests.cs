using System.Security.Cryptography;
using System.Text;
using XStorage.Contracts;
using XStorage.Storage;

namespace XStorage.Storage.Tests;

public sealed class HeadObjectTests
{
    [Fact]
    public async Task HeadObject_ReturnsCommittedMetadataWithoutAnObjectStream()
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
            var application = await storage.CreateApplicationAsync("head", 16, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "head-bucket", CancellationToken.None);
            var bytes = Encoding.UTF8.GetBytes("metadata only");
            await using var input = new MemoryStream(bytes, writable: false);
            await storage.PutObjectAsync(
                application.AccessKeyId,
                "head-bucket",
                "metadata.txt",
                new ObjectWriteRequest(input, bytes.Length, "text/plain", null, null),
                CancellationToken.None);

            var metadata = await storage.HeadObjectAsync(
                application.AccessKeyId,
                "head-bucket",
                "metadata.txt",
                CancellationToken.None);

            Assert.Equal("metadata.txt", metadata.Key);
            Assert.Equal(bytes.Length, metadata.Size);
            Assert.Equal("text/plain", metadata.ContentType);
            Assert.Equal($"\"{Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant()}\"", metadata.ETag);
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
