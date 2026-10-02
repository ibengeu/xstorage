using System.Security.Cryptography;
using XStorage.Contracts;
using XStorage.Storage;

namespace XStorage.Storage.Tests;

public sealed class StartupReconciliationTests
{
    [Fact]
    public async Task MissingApplicationRecords_RebuildsOwnershipFromBucketMarkers()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"xstorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(root, 1024, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        string accessKeyId;
        try
        {
            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var application = await storage.CreateApplicationAsync("recovery", 128, CancellationToken.None);
                accessKeyId = application.AccessKeyId;
                await storage.CreateBucketAsync(accessKeyId, "recovered-bucket", CancellationToken.None);
                await using var body = new MemoryStream("disk data"u8.ToArray(), writable: false);
                await storage.PutObjectAsync(accessKeyId, "recovered-bucket", "object", new ObjectWriteRequest(body, body.Length, "text/plain", null, null), CancellationToken.None);
            }

            foreach (var record in Directory.EnumerateFiles(Path.Combine(root, "state", "applications"), "*.json"))
            {
                File.Delete(record);
            }

            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var recovered = Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None));
                Assert.Equal(accessKeyId, recovered.AccessKeyId);
                Assert.Equal(9, recovered.UsedBytes);
                Assert.Equal(1, recovered.BucketCount);
                Assert.Equal(0, recovered.QuotaBytes);
                Assert.True(recovered.Active);
                var listing = await storage.ListObjectsV2Async(
                    accessKeyId,
                    "recovered-bucket",
                    new ObjectListingRequest(),
                    CancellationToken.None);
                Assert.Equal("object", Assert.Single(listing.Contents).Key);
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

    [Theory]
    [InlineData(nameof(StorageFaultPoint.AfterObjectRename))]
    [InlineData(nameof(StorageFaultPoint.BeforePutUsageCounterUpdate))]
    public async Task PutFailureAfterCommit_StartupReconcilesCommittedUsage(string failurePoint)
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"xstorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32),
            maximumObjectsPerApplication: 1);
        var injectFailure = false;
        options.FaultInjector = point =>
        {
            if (injectFailure && point.ToString() == failurePoint)
            {
                throw new IOException("Injected failure after object commit.");
            }
        };
        string accessKeyId;

        try
        {
            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var application = await storage.CreateApplicationAsync("reconcile", 64, CancellationToken.None);
                accessKeyId = application.AccessKeyId;
                await storage.CreateBucketAsync(accessKeyId, "reconcile-bucket", CancellationToken.None);
                injectFailure = true;
                await using var input = new MemoryStream("committed"u8.ToArray(), writable: false);

                await Assert.ThrowsAsync<IOException>(() => storage.PutObjectAsync(
                    accessKeyId,
                    "reconcile-bucket",
                    "object",
                    new ObjectWriteRequest(input, input.Length, "text/plain", null, null),
                    CancellationToken.None));
                Assert.Equal(9, (await storage.HeadObjectAsync(
                    accessKeyId,
                    "reconcile-bucket",
                    "object",
                    CancellationToken.None)).Size);
                Assert.Equal(0, Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None)).UsedBytes);
            }

            injectFailure = false;
            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var summary = Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None));
                Assert.Equal(accessKeyId, summary.AccessKeyId);
                Assert.Equal(9, summary.UsedBytes);
                Assert.Equal(1, summary.BucketCount);
                var listing = await storage.ListObjectsV2Async(
                    accessKeyId,
                    "reconcile-bucket",
                    new ObjectListingRequest(),
                    CancellationToken.None);
                Assert.Equal("object", Assert.Single(listing.Contents).Key);
                await using var secondInput = new MemoryStream([], writable: false);
                var limitError = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.PutObjectAsync(
                    accessKeyId,
                    "reconcile-bucket",
                    "second-object",
                    new ObjectWriteRequest(secondInput, 0, "application/octet-stream", null, null),
                    CancellationToken.None));
                Assert.Equal("InvalidArgument", limitError.Code);
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

    [Theory]
    [InlineData(nameof(StorageFaultPoint.AfterObjectUnlink))]
    [InlineData(nameof(StorageFaultPoint.BeforeDeleteUsageCounterUpdate))]
    public async Task DeleteFailureAfterRemoval_StartupReconcilesCommittedUsage(string failurePoint)
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"xstorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
        var injectFailure = false;
        options.FaultInjector = point =>
        {
            if (injectFailure && point.ToString() == failurePoint)
            {
                throw new IOException("Injected failure after object removal.");
            }
        };
        string accessKeyId;

        try
        {
            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var application = await storage.CreateApplicationAsync("delete-reconcile", 64, CancellationToken.None);
                accessKeyId = application.AccessKeyId;
                await storage.CreateBucketAsync(accessKeyId, "delete-reconcile-bucket", CancellationToken.None);
                await using var input = new MemoryStream("remove"u8.ToArray(), writable: false);
                await storage.PutObjectAsync(
                    accessKeyId,
                    "delete-reconcile-bucket",
                    "object",
                    new ObjectWriteRequest(input, input.Length, "text/plain", null, null),
                    CancellationToken.None);
                injectFailure = true;

                await Assert.ThrowsAsync<IOException>(() => storage.DeleteObjectAsync(
                    accessKeyId,
                    "delete-reconcile-bucket",
                    "object",
                    CancellationToken.None));
                var missing = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.HeadObjectAsync(
                    accessKeyId,
                    "delete-reconcile-bucket",
                    "object",
                    CancellationToken.None));
                Assert.Equal("NoSuchKey", missing.Code);
                Assert.Equal(6, Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None)).UsedBytes);
            }

            injectFailure = false;
            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var summary = Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None));
                Assert.Equal(accessKeyId, summary.AccessKeyId);
                Assert.Equal(0, summary.UsedBytes);
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
}
