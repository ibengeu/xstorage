using System.Security.Cryptography;
using OptiStorage.Contracts;
using OptiStorage.Storage;

namespace OptiStorage.Storage.Tests;

public sealed class CrossApplicationCommitIsolationTests
{
    private static readonly TimeSpan IsolationTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task PutFromOtherApplication_CompletesWhileAnotherApplicationCommitIsStalled()
    {
        await WithStalledCommitAsync(async (storage, other) =>
        {
            var metadata = await PutAsync(storage, other, "other-bucket", "other-key").WaitAsync(IsolationTimeout);

            Assert.Equal("other-key", metadata.Key);
        });
    }

    [Fact]
    public async Task CreateBucketFromOtherApplication_CompletesWhileAnotherApplicationCommitIsStalled()
    {
        await WithStalledCommitAsync(async (storage, other) =>
        {
            await storage.CreateBucketAsync(other, "other-new-bucket", CancellationToken.None).WaitAsync(IsolationTimeout);

            var summary = (await storage.ListApplicationsAsync(CancellationToken.None)).Single(item => item.AccessKeyId == other);
            Assert.Equal(2, summary.BucketCount);
        });
    }

    private static async Task WithStalledCommitAsync(Func<IObjectStorage, string, Task> assertIsolated)
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(root, 1024, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        var stallArmed = 0;
        var stalledReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseStalled = new ManualResetEventSlim();
        options.FaultInjector = point =>
        {
            if (point == StorageFaultPoint.BeforeObjectRename && Interlocked.Exchange(ref stallArmed, 2) == 1)
            {
                stalledReached.SetResult();
                releaseStalled.Wait(TimeSpan.FromSeconds(30));
            }
        };

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var stalled = await storage.CreateApplicationAsync("stalled", 4096, CancellationToken.None);
            var other = await storage.CreateApplicationAsync("other", 4096, CancellationToken.None);
            await storage.CreateBucketAsync(stalled.AccessKeyId, "stalled-bucket", CancellationToken.None);
            await storage.CreateBucketAsync(other.AccessKeyId, "other-bucket", CancellationToken.None);

            Interlocked.Exchange(ref stallArmed, 1);
            var stalledPut = Task.Run(() => PutAsync(storage, stalled.AccessKeyId, "stalled-bucket", "stalled-key"));
            await stalledReached.Task.WaitAsync(IsolationTimeout);
            try
            {
                await assertIsolated(storage, other.AccessKeyId);
            }
            finally
            {
                releaseStalled.Set();
                await stalledPut;
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

    private static async Task<ObjectMetadata> PutAsync(IObjectStorage storage, string accessKeyId, string bucket, string key)
    {
        var bytes = "isolated-commit"u8.ToArray();
        await using var content = new MemoryStream(bytes, writable: false);
        return await storage.PutObjectAsync(accessKeyId, bucket, key,
            new ObjectWriteRequest(content, bytes.Length, "application/octet-stream", null, null), CancellationToken.None);
    }
}
