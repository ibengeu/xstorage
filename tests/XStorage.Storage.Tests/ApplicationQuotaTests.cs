using System.Security.Cryptography;
using System.Text;
using XStorage.Contracts;
using XStorage.Storage;

namespace XStorage.Storage.Tests;

public sealed class ApplicationQuotaTests
{
    [Fact]
    public async Task EmptyObjects_CountTowardConfiguredApplicationObjectLimit()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"xstorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32),
            maximumObjectsPerApplication: 1);

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("empty-object-limit", 0, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "empty-object-limit-bucket", CancellationToken.None);
            await using var emptyObject = new MemoryStream([], writable: false);
            await storage.PutObjectAsync(
                application.AccessKeyId,
                "empty-object-limit-bucket",
                "first",
                new ObjectWriteRequest(emptyObject, 0, "application/octet-stream", null, null),
                CancellationToken.None);
            await using var secondEmptyObject = new MemoryStream([], writable: false);

            var error = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.PutObjectAsync(
                application.AccessKeyId,
                "empty-object-limit-bucket",
                "second",
                new ObjectWriteRequest(secondEmptyObject, 0, "application/octet-stream", null, null),
                CancellationToken.None));

            Assert.Equal("InvalidArgument", error.Code);

            await using var replacement = new MemoryStream([], writable: false);
            await storage.PutObjectAsync(
                application.AccessKeyId,
                "empty-object-limit-bucket",
                "first",
                new ObjectWriteRequest(replacement, 0, "application/octet-stream", null, null),
                CancellationToken.None);
            await storage.DeleteObjectAsync(
                application.AccessKeyId,
                "empty-object-limit-bucket",
                "first",
                CancellationToken.None);
            await using var afterDelete = new MemoryStream([], writable: false);
            await storage.PutObjectAsync(
                application.AccessKeyId,
                "empty-object-limit-bucket",
                "second",
                new ObjectWriteRequest(afterDelete, 0, "application/octet-stream", null, null),
                CancellationToken.None);
            var page = await storage.ListObjectsV2Async(
                application.AccessKeyId,
                "empty-object-limit-bucket",
                new ObjectListingRequest(),
                CancellationToken.None);
            Assert.Equal(new[] { "second" }, page.Contents.Select(item => item.Key));
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
    public async Task RotatingCredentials_PreservesTheApplicationObjectCount()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"xstorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32),
            maximumObjectsPerApplication: 1);

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("rotation-object-limit", 0, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "rotation-object-limit-bucket", CancellationToken.None);
            await using var firstObject = new MemoryStream([], writable: false);
            await storage.PutObjectAsync(
                application.AccessKeyId,
                "rotation-object-limit-bucket",
                "first",
                new ObjectWriteRequest(firstObject, 0, "application/octet-stream", null, null),
                CancellationToken.None);
            await storage.RotateCredentialsAsync(application.AccessKeyId, CancellationToken.None);
            await using var secondObject = new MemoryStream([], writable: false);

            var error = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.PutObjectAsync(
                application.AccessKeyId,
                "rotation-object-limit-bucket",
                "second",
                new ObjectWriteRequest(secondObject, 0, "application/octet-stream", null, null),
                CancellationToken.None));

            Assert.Equal("InvalidArgument", error.Code);
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
    public async Task ConcurrentPuts_DoNotExceedSharedApplicationQuota()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"xstorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(root, 1024, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("concurrent-quota", 50, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "concurrent-quota-bucket", CancellationToken.None);

            var writes = Enumerable.Range(0, 20).Select(async number =>
            {
                var bytes = Enumerable.Repeat((byte)number, 10).ToArray();
                await using var body = new MemoryStream(bytes, writable: false);
                try
                {
                    await storage.PutObjectAsync(application.AccessKeyId, "concurrent-quota-bucket", $"object-{number}",
                        new ObjectWriteRequest(body, bytes.Length, "application/octet-stream", null, null), CancellationToken.None);
                    return true;
                }
                catch (ObjectStorageException exception) when (exception.Code == "InvalidArgument")
                {
                    return false;
                }
            });

            var accepted = (await Task.WhenAll(writes)).Count(value => value);
            var summary = Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None));
            var page = await storage.ListObjectsV2Async(application.AccessKeyId, "concurrent-quota-bucket", new ObjectListingRequest(), CancellationToken.None);

            Assert.Equal(5, accepted);
            Assert.Equal(50, summary.UsedBytes);
            Assert.Equal(5, page.KeyCount);
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
    public async Task LoweringQuotaBelowUsage_BlocksNewObjectsAndKeepsCommittedData()
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
            var application = await storage.CreateApplicationAsync("quota", 16, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "quota-bucket", CancellationToken.None);
            var existingBytes = Encoding.UTF8.GetBytes("kept");
            await using var existingInput = new MemoryStream(existingBytes, writable: false);
            await storage.PutObjectAsync(
                application.AccessKeyId,
                "quota-bucket",
                "existing",
                new ObjectWriteRequest(existingInput, existingBytes.Length, "application/octet-stream", null, null),
                CancellationToken.None);

            await storage.UpdateQuotaAsync(application.AccessKeyId, 2, CancellationToken.None);
            await using var newInput = new MemoryStream([1], writable: false);
            var error = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.PutObjectAsync(
                application.AccessKeyId,
                "quota-bucket",
                "new",
                new ObjectWriteRequest(newInput, 1, "application/octet-stream", null, null),
                CancellationToken.None));
            await using var result = await storage.GetObjectAsync(application.AccessKeyId, "quota-bucket", "existing", CancellationToken.None);
            using var output = new MemoryStream();
            await result.Content.CopyToAsync(output);
            var summary = Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None));

            Assert.Equal("InvalidArgument", error.Code);
            Assert.Equal(2, summary.QuotaBytes);
            Assert.Equal(existingBytes.Length, summary.UsedBytes);
            Assert.Equal(existingBytes, output.ToArray());
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
    public async Task LoweringQuotaBelowUsage_BlocksSmallerReplacementUntilDeleteReducesUsage()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"xstorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(root, 1024, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("lowered-replacement", 16, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "lowered-replacement-bucket", CancellationToken.None);
            await using var original = new MemoryStream("original"u8.ToArray(), writable: false);
            await storage.PutObjectAsync(application.AccessKeyId, "lowered-replacement-bucket", "object",
                new ObjectWriteRequest(original, original.Length, "text/plain", null, null), CancellationToken.None);
            await storage.UpdateQuotaAsync(application.AccessKeyId, 2, CancellationToken.None);
            await using var smaller = new MemoryStream("x"u8.ToArray(), writable: false);

            var blocked = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.PutObjectAsync(
                application.AccessKeyId, "lowered-replacement-bucket", "object",
                new ObjectWriteRequest(smaller, smaller.Length, "text/plain", null, null), CancellationToken.None));
            await storage.DeleteObjectAsync(application.AccessKeyId, "lowered-replacement-bucket", "object", CancellationToken.None);
            await using var afterDelete = new MemoryStream("x"u8.ToArray(), writable: false);
            await storage.PutObjectAsync(application.AccessKeyId, "lowered-replacement-bucket", "object",
                new ObjectWriteRequest(afterDelete, afterDelete.Length, "text/plain", null, null), CancellationToken.None);

            Assert.Equal("InvalidArgument", blocked.Code);
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

    [Fact]
    public async Task ReplacingObject_UsesIncomingSizeInsteadOfAddingTheReplacement()
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
            var application = await storage.CreateApplicationAsync("replacement-quota", 10, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "replacement-quota-bucket", CancellationToken.None);
            await using var original = new MemoryStream("12345678"u8.ToArray(), writable: false);
            await storage.PutObjectAsync(
                application.AccessKeyId,
                "replacement-quota-bucket",
                "object",
                new ObjectWriteRequest(original, original.Length, "text/plain", null, null),
                CancellationToken.None);
            await using var replacement = new MemoryStream("small"u8.ToArray(), writable: false);

            await storage.PutObjectAsync(
                application.AccessKeyId,
                "replacement-quota-bucket",
                "object",
                new ObjectWriteRequest(replacement, replacement.Length, "text/plain", null, null),
                CancellationToken.None);

            var summary = Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None));
            Assert.Equal(5, summary.UsedBytes);
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
