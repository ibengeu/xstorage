using System.Security.Cryptography;
using XStorage.Storage;

namespace XStorage.Storage.Tests;

public sealed class StorageOptionsValidationTests
{
    [Fact]
    public async Task OpenAsync_DataRootIsFilesystemRootIsRejected()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        await Assert.ThrowsAsync<ArgumentException>(() => ObjectStorage.OpenAsync(options, CancellationToken.None));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public async Task OpenAsync_NegativeApplicationResourceLimitIsRejected(int maximumObjects, int maximumBuckets)
    {
        var root = Path.Combine(
            OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            $"xstorage-options-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32),
            maximumObjects,
            maximumBuckets);

        try
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ObjectStorage.OpenAsync(options, CancellationToken.None));
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
    [InlineData(31)]
    [InlineData(33)]
    public async Task OpenAsync_InvalidContinuationTokenKeySizeIsRejected(int keyLength)
    {
        var root = Path.Combine(
            OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            $"xstorage-options-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(keyLength));

        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => ObjectStorage.OpenAsync(options, CancellationToken.None));
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
