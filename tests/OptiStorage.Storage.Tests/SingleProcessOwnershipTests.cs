using System.Security.Cryptography;
using OptiStorage.Storage;

namespace OptiStorage.Storage.Tests;

public sealed class SingleProcessOwnershipTests
{
    [Fact]
    public async Task OpenAsync_RejectsAnotherStorageInstanceForTheSameRootUntilDisposed()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            var first = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            try
            {
                await Assert.ThrowsAsync<IOException>(() => ObjectStorage.OpenAsync(options, CancellationToken.None));
            }
            finally
            {
                await first.DisposeAsync();
            }

            await using var reopened = await ObjectStorage.OpenAsync(options, CancellationToken.None);
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
