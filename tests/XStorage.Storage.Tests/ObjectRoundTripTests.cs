using System.Security.Cryptography;
using System.Text;
using XStorage.Contracts;
using XStorage.Storage;

namespace XStorage.Storage.Tests;

public sealed class ObjectRoundTripTests
{
    [Fact]
    public async Task ZeroObjectLimit_AllowsEmptyObjectsOnly()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"xstorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(root, 0, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("zero-object-limit", 0, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "zero-object-limit-bucket", CancellationToken.None);
            await using var empty = new MemoryStream([], writable: false);
            await storage.PutObjectAsync(application.AccessKeyId, "zero-object-limit-bucket", "empty",
                new ObjectWriteRequest(empty, 0, "application/octet-stream", null, null), CancellationToken.None);
            await using var nonempty = new MemoryStream([1], writable: false);

            var blocked = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.PutObjectAsync(
                application.AccessKeyId, "zero-object-limit-bucket", "one-byte",
                new ObjectWriteRequest(nonempty, 1, "application/octet-stream", null, null), CancellationToken.None));

            Assert.Equal("EntityTooLarge", blocked.Code);
            Assert.Equal(0, (await storage.HeadObjectAsync(application.AccessKeyId, "zero-object-limit-bucket", "empty", CancellationToken.None)).Size);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PutAndGet_PreservesExactBytesAndMetadata()
    {
        var tempRoot = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        var root = Path.Combine(tempRoot, $"xstorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("round-trip", 4096, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "round-trip-bucket", CancellationToken.None);

            var key = "folder/percent%/café.txt";
            var bytes = Encoding.UTF8.GetBytes("exact object bytes");
            await using var input = new MemoryStream(bytes, writable: false);
            await storage.PutObjectAsync(
                application.AccessKeyId,
                "round-trip-bucket",
                key,
                new ObjectWriteRequest(input, bytes.Length, "text/plain", null, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
                CancellationToken.None);

            await using StoredObject result = await storage.GetObjectAsync(
                application.AccessKeyId,
                "round-trip-bucket",
                key,
                CancellationToken.None);
            using var output = new MemoryStream();
            await result.Content.CopyToAsync(output);

            Assert.Equal(bytes, output.ToArray());
            Assert.Equal(key, result.Metadata.Key);
            Assert.Equal(bytes.Length, result.Metadata.Size);
            Assert.Equal("text/plain", result.Metadata.ContentType);
            Assert.Equal($"\"{Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant()}\"", result.Metadata.ETag);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PutAndDelete_KeepTheirPublicStateAfterRestart()
    {
        var root = Path.Combine(
            OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            $"xstorage-restart-{Guid.NewGuid():N}");
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
                var application = await storage.CreateApplicationAsync("restart", 1024, CancellationToken.None);
                accessKeyId = application.AccessKeyId;
                await storage.CreateBucketAsync(accessKeyId, "restart-bucket", CancellationToken.None);
                await using var body = new MemoryStream("durable bytes"u8.ToArray(), writable: false);
                await storage.PutObjectAsync(
                    accessKeyId,
                    "restart-bucket",
                    "object",
                    new ObjectWriteRequest(body, body.Length, "text/plain", null, null),
                    CancellationToken.None);
            }

            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                await using StoredObject stored = await storage.GetObjectAsync(
                    accessKeyId,
                    "restart-bucket",
                    "object",
                    CancellationToken.None);
                using var output = new MemoryStream();
                await stored.Content.CopyToAsync(output);

                Assert.Equal("durable bytes"u8.ToArray(), output.ToArray());
                Assert.Equal(13, stored.Metadata.Size);

                await storage.DeleteObjectAsync(accessKeyId, "restart-bucket", "object", CancellationToken.None);
            }

            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var listing = await storage.ListObjectsV2Async(
                    accessKeyId,
                    "restart-bucket",
                    new ObjectListingRequest(),
                    CancellationToken.None);

                Assert.Empty(listing.Contents);
                Assert.Equal(0, Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None)).UsedBytes);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PutAndGet_AllowsEmptyObjectsAtZeroQuota()
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
            var application = await storage.CreateApplicationAsync("empty", 0, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "empty-bucket", CancellationToken.None);
            await using var input = new MemoryStream([], writable: false);
            await storage.PutObjectAsync(
                application.AccessKeyId,
                "empty-bucket",
                "empty-object",
                new ObjectWriteRequest(input, 0, "application/octet-stream", null, null),
                CancellationToken.None);

            await using StoredObject result = await storage.GetObjectAsync(
                application.AccessKeyId,
                "empty-bucket",
                "empty-object",
                CancellationToken.None);
            using var output = new MemoryStream();
            await result.Content.CopyToAsync(output);
            var summary = Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None));

            Assert.Empty(output.ToArray());
            Assert.Equal(0, result.Metadata.Size);
            Assert.Equal($"\"{Convert.ToHexString(MD5.HashData([])).ToLowerInvariant()}\"", result.Metadata.ETag);
            Assert.Equal(0, summary.UsedBytes);
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
