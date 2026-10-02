using System.Security.Cryptography;
using OptiStorage.Contracts;
using OptiStorage.Storage;

namespace OptiStorage.Storage.Tests;

public sealed class BucketCreationFailureTests
{
    [Fact]
    public async Task CreateBucket_RejectsBucketsBeyondTheConfiguredApplicationLimit()
    {
        var root = Path.Combine(
            OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            $"optistorage-bucket-limit-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32),
            maximumBucketsPerApplication: 1);

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("bucket-limit", 64, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "first-limited-bucket", CancellationToken.None);

            var error = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.CreateBucketAsync(
                application.AccessKeyId,
                "second-limited-bucket",
                CancellationToken.None));

            Assert.Equal("InvalidArgument", error.Code);
            Assert.Equal(1, Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None)).BucketCount);
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
    public async Task CreateBucket_FailureBeforePublicationLeavesNoBucketAndCanBeRetried()
    {
        var root = Path.Combine(
            OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            $"optistorage-bucket-failure-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
        var injectFailure = true;
        options.FaultInjector = point =>
        {
            if (injectFailure && point == StorageFaultPoint.AfterBucketMarkerFlush)
            {
                throw new IOException("Injected failure before bucket publication.");
            }
        };

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("bucket-failure", 64, CancellationToken.None);

            await Assert.ThrowsAsync<IOException>(() => storage.CreateBucketAsync(
                application.AccessKeyId,
                "retryable-bucket",
                CancellationToken.None));
            Assert.Equal(0, Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None)).BucketCount);
            Assert.False(Directory.Exists(Path.Combine(root, "buckets", "retryable-bucket")));

            injectFailure = false;
            await storage.CreateBucketAsync(application.AccessKeyId, "retryable-bucket", CancellationToken.None);

            Assert.Equal(1, Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None)).BucketCount);
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
