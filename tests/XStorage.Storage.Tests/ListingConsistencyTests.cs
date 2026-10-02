using System.Security.Cryptography;
using XStorage.Contracts;
using XStorage.Storage;

namespace XStorage.Storage.Tests;

public sealed class ListingConsistencyTests
{
    [Fact]
    public async Task ListObjectsV2_ReturnsAConsistentPageWhileAnOverwriteIsCommitting()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"xstorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
        using var releaseWriter = new ManualResetEventSlim();
        var writerReachedCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdWriter = false;
        var writerPauseUsed = 0;
        options.FaultInjector = point =>
        {
            if (point == StorageFaultPoint.BeforeObjectRename
                && holdWriter
                && Interlocked.Exchange(ref writerPauseUsed, 1) == 0)
            {
                writerReachedCommit.SetResult();
                releaseWriter.Wait(TimeSpan.FromSeconds(10));
            }
        };

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("listing-consistency", 64, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "listing-consistency-bucket", CancellationToken.None);
            await using var original = new MemoryStream("old"u8.ToArray(), writable: false);
            var oldMetadata = await storage.PutObjectAsync(
                application.AccessKeyId,
                "listing-consistency-bucket",
                "key",
                new ObjectWriteRequest(original, original.Length, "text/plain", null, null),
                CancellationToken.None);

            holdWriter = true;
            var writerTask = Task.Run(async () =>
            {
                await using var replacement = new MemoryStream("new"u8.ToArray(), writable: false);
                await storage.PutObjectAsync(
                    application.AccessKeyId,
                    "listing-consistency-bucket",
                    "key",
                    new ObjectWriteRequest(replacement, replacement.Length, "text/plain", null, null),
                    CancellationToken.None);
            });
            await writerReachedCommit.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var listingTask = Task.Run(() => storage.ListObjectsV2Async(
                application.AccessKeyId,
                "listing-consistency-bucket",
                new ObjectListingRequest(),
                CancellationToken.None));
            var listingCompletedBeforeWriterCommit = await Task.WhenAny(
                listingTask,
                Task.Delay(TimeSpan.FromSeconds(2))) == listingTask;
            releaseWriter.Set();
            await writerTask;
            var page = await listingTask;

            Assert.True(listingCompletedBeforeWriterCommit);
            Assert.Equal(oldMetadata.ETag, Assert.Single(page.Contents).ETag);
            Assert.Equal(1, page.KeyCount);
        }
        finally
        {
            releaseWriter.Set();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
