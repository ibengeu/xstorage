using System.Runtime.Versioning;
using System.Security.Cryptography;
using OptiStorage.Contracts;
using OptiStorage.Storage;

namespace OptiStorage.Storage.Tests;

public sealed class ObjectDirectoryPermissionTests
{
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public async Task PutIntoExistingObjectDirectoryWithBroadMode_RestoresPrivateMode()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(root, 1024, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("modes", 4096, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "mode-bucket", CancellationToken.None);
            await PutAsync(storage, application.AccessKeyId);
            var objectDirectory = Path.GetDirectoryName(Directory
                .EnumerateFiles(Path.Combine(root, "buckets", "mode-bucket", "objects"), "*.obj", SearchOption.AllDirectories)
                .Single())!;
            File.SetUnixFileMode(objectDirectory, PrivateDirectoryMode | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            await PutAsync(storage, application.AccessKeyId);

            Assert.Equal(PrivateDirectoryMode, File.GetUnixFileMode(objectDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task PutAsync(IObjectStorage storage, string accessKeyId)
    {
        var bytes = "private-directory"u8.ToArray();
        await using var content = new MemoryStream(bytes, writable: false);
        await storage.PutObjectAsync(accessKeyId, "mode-bucket", "mode-key",
            new ObjectWriteRequest(content, bytes.Length, "application/octet-stream", null, null), CancellationToken.None);
    }
}

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Skip = "The Unix directory mode test requires Linux or macOS.";
        }
    }
}
