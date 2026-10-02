using System.Security.Cryptography;
using XStorage.Contracts;
using XStorage.Storage;

namespace XStorage.Storage.Tests;

public sealed class SymlinkSafetyTests
{
    [Fact]
    public async Task OpenAsync_WhenLockFileIsDanglingSymlinkDoesNotCreateOutsideDataRoot()
    {
        var temporaryRoot = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        var root = Path.Combine(temporaryRoot, $"xstorage-lock-symlink-root-{Guid.NewGuid():N}");
        var outside = Path.Combine(temporaryRoot, $"xstorage-lock-symlink-target-{Guid.NewGuid():N}");
        var state = Path.Combine(root, "state");
        var lockPath = Path.Combine(state, ".service.lock");
        var options = new ObjectStorageOptions(root, 1024, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));

        try
        {
            Directory.CreateDirectory(state);
            Directory.CreateSymbolicLink(lockPath, outside);

            await Assert.ThrowsAsync<IOException>(() => ObjectStorage.OpenAsync(options, CancellationToken.None));

            Assert.False(File.Exists(outside));
        }
        finally
        {
            File.Delete(lockPath);
            if (File.Exists(outside))
            {
                File.Delete(outside);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PutObject_WhenObjectsDirectoryIsDanglingSymlinkDoesNotCreateOutsideDataRoot()
    {
        var temporaryRoot = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        var root = Path.Combine(temporaryRoot, $"xstorage-dangling-symlink-root-{Guid.NewGuid():N}");
        var outside = Path.Combine(temporaryRoot, $"xstorage-dangling-symlink-target-{Guid.NewGuid():N}");
        var objectsPath = Path.Combine(root, "buckets", "dangling-symlink-bucket", "objects");
        var options = new ObjectStorageOptions(root, 1024, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));

        try
        {
            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var application = await storage.CreateApplicationAsync("dangling-symlink", 1024, CancellationToken.None);
                await storage.CreateBucketAsync(application.AccessKeyId, "dangling-symlink-bucket", CancellationToken.None);
                Directory.Delete(objectsPath);
                Directory.CreateSymbolicLink(objectsPath, outside);
                await using var body = new MemoryStream("must stay inside"u8.ToArray(), writable: false);

                await Assert.ThrowsAsync<IOException>(() => storage.PutObjectAsync(
                    application.AccessKeyId, "dangling-symlink-bucket", "object",
                    new ObjectWriteRequest(body, body.Length, "text/plain", null, null), CancellationToken.None));

                Assert.False(Directory.Exists(outside));
                Assert.Equal(0, Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None)).UsedBytes);
            }
        }
        finally
        {
            File.Delete(objectsPath);

            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DeleteObject_WhenObjectsDirectoryIsSymlinkDoesNotDeleteOutsideDataRoot()
    {
        var temporaryRoot = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        var root = Path.Combine(temporaryRoot, $"xstorage-symlink-delete-root-{Guid.NewGuid():N}");
        var outside = Path.Combine(temporaryRoot, $"xstorage-symlink-delete-target-{Guid.NewGuid():N}");
        var objectsPath = Path.Combine(root, "buckets", "symlink-delete-bucket", "objects");
        var options = new ObjectStorageOptions(root, 1024, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        const string objectKey = "object";
        const int bodyLength = 10;

        try
        {
            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var application = await storage.CreateApplicationAsync("symlink-delete", 1024, CancellationToken.None);
                await storage.CreateBucketAsync(application.AccessKeyId, "symlink-delete-bucket", CancellationToken.None);
                await using var body = new MemoryStream("delete me!"u8.ToArray(), writable: false);
                await storage.PutObjectAsync(application.AccessKeyId, "symlink-delete-bucket", objectKey,
                    new ObjectWriteRequest(body, body.Length, "text/plain", null, null), CancellationToken.None);
                Directory.Move(objectsPath, outside);
                Directory.CreateSymbolicLink(objectsPath, outside);

                await Assert.ThrowsAsync<IOException>(() => storage.DeleteObjectAsync(
                    application.AccessKeyId, "symlink-delete-bucket", objectKey, CancellationToken.None));

                Assert.Single(Directory.EnumerateFiles(outside, "*.obj", SearchOption.AllDirectories));
                Assert.Equal(bodyLength, Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None)).UsedBytes);
            }
        }
        finally
        {
            if (Directory.Exists(objectsPath) &&
                (File.GetAttributes(objectsPath) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(objectsPath);
            }

            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task HeadObject_WhenObjectsDirectoryIsSymlinkDoesNotReadOutsideDataRoot()
    {
        var temporaryRoot = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        var root = Path.Combine(temporaryRoot, $"xstorage-symlink-head-root-{Guid.NewGuid():N}");
        var outside = Path.Combine(temporaryRoot, $"xstorage-symlink-head-target-{Guid.NewGuid():N}");
        var objectsPath = Path.Combine(root, "buckets", "symlink-head-bucket", "objects");
        var options = new ObjectStorageOptions(root, 1024, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));

        try
        {
            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var application = await storage.CreateApplicationAsync("symlink-head", 1024, CancellationToken.None);
                await storage.CreateBucketAsync(application.AccessKeyId, "symlink-head-bucket", CancellationToken.None);
                await using var body = new MemoryStream("private metadata"u8.ToArray(), writable: false);
                await storage.PutObjectAsync(application.AccessKeyId, "symlink-head-bucket", "object",
                    new ObjectWriteRequest(body, body.Length, "text/plain", null, null), CancellationToken.None);
                Directory.Move(objectsPath, outside);
                Directory.CreateSymbolicLink(objectsPath, outside);

                await Assert.ThrowsAsync<IOException>(() => storage.HeadObjectAsync(
                    application.AccessKeyId, "symlink-head-bucket", "object", CancellationToken.None));
            }
        }
        finally
        {
            if (Directory.Exists(objectsPath) &&
                (File.GetAttributes(objectsPath) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(objectsPath);
            }

            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ListObjectsV2_WhenHashDirectoryIsSymlinkRejectsTheUnsafeTree()
    {
        var temporaryRoot = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        var root = Path.Combine(temporaryRoot, $"xstorage-symlink-list-root-{Guid.NewGuid():N}");
        var outside = Path.Combine(temporaryRoot, $"xstorage-symlink-list-target-{Guid.NewGuid():N}");
        var objectsPath = Path.Combine(root, "buckets", "symlink-list-bucket", "objects");
        var options = new ObjectStorageOptions(root, 1024, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        string hashDirectoryPath = string.Empty;

        try
        {
            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var application = await storage.CreateApplicationAsync("symlink-list", 1024, CancellationToken.None);
                await storage.CreateBucketAsync(application.AccessKeyId, "symlink-list-bucket", CancellationToken.None);
                await using var body = new MemoryStream("listed bytes"u8.ToArray(), writable: false);
                await storage.PutObjectAsync(application.AccessKeyId, "symlink-list-bucket", "object",
                    new ObjectWriteRequest(body, body.Length, "text/plain", null, null), CancellationToken.None);
                hashDirectoryPath = Directory.EnumerateDirectories(objectsPath).Single();
                Directory.Move(hashDirectoryPath, outside);
                Directory.CreateSymbolicLink(hashDirectoryPath, outside);

                await Assert.ThrowsAsync<IOException>(() => storage.ListObjectsV2Async(
                    application.AccessKeyId, "symlink-list-bucket", new ObjectListingRequest(), CancellationToken.None));
            }
        }
        finally
        {
            if (hashDirectoryPath.Length > 0 && Directory.Exists(hashDirectoryPath) &&
                (File.GetAttributes(hashDirectoryPath) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(hashDirectoryPath);
            }

            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetObject_WhenObjectsDirectoryIsSymlinkDoesNotReadOutsideDataRoot()
    {
        var temporaryRoot = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        var root = Path.Combine(temporaryRoot, $"xstorage-symlink-read-root-{Guid.NewGuid():N}");
        var outside = Path.Combine(temporaryRoot, $"xstorage-symlink-read-target-{Guid.NewGuid():N}");
        var objectsPath = Path.Combine(root, "buckets", "symlink-read-bucket", "objects");
        var options = new ObjectStorageOptions(root, 1024, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));

        try
        {
            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var application = await storage.CreateApplicationAsync("symlink-read", 1024, CancellationToken.None);
                await storage.CreateBucketAsync(application.AccessKeyId, "symlink-read-bucket", CancellationToken.None);
                await using var body = new MemoryStream("private bytes"u8.ToArray(), writable: false);
                await storage.PutObjectAsync(application.AccessKeyId, "symlink-read-bucket", "object",
                    new ObjectWriteRequest(body, body.Length, "text/plain", null, null), CancellationToken.None);
                Directory.Move(objectsPath, outside);
                Directory.CreateSymbolicLink(objectsPath, outside);

                await Assert.ThrowsAsync<IOException>(() => storage.GetObjectAsync(
                    application.AccessKeyId, "symlink-read-bucket", "object", CancellationToken.None));
            }
        }
        finally
        {
            if (Directory.Exists(objectsPath) &&
                (File.GetAttributes(objectsPath) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(objectsPath);
            }

            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PutObject_WhenObjectsDirectoryIsSymlink_DoesNotWriteOutsideDataRoot()
    {
        var temporaryRoot = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        var root = Path.Combine(temporaryRoot, $"xstorage-symlink-root-{Guid.NewGuid():N}");
        var outside = Path.Combine(temporaryRoot, $"xstorage-symlink-target-{Guid.NewGuid():N}");
        var objectsPath = Path.Combine(root, "buckets", "symlink-bucket", "objects");
        var options = new ObjectStorageOptions(root, 1024, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));

        try
        {
            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var application = await storage.CreateApplicationAsync("symlink", 1024, CancellationToken.None);
                await storage.CreateBucketAsync(application.AccessKeyId, "symlink-bucket", CancellationToken.None);
                Directory.Delete(objectsPath);
                Directory.CreateDirectory(outside);
                Directory.CreateSymbolicLink(objectsPath, outside);
                await using var body = new MemoryStream("must stay inside"u8.ToArray(), writable: false);

                await Assert.ThrowsAsync<IOException>(() => storage.PutObjectAsync(
                    application.AccessKeyId,
                    "symlink-bucket",
                    "object",
                    new ObjectWriteRequest(body, body.Length, "text/plain", null, null),
                    CancellationToken.None));

                Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
                Assert.Equal(0, Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None)).UsedBytes);
            }
        }
        finally
        {
            if (Directory.Exists(objectsPath) &&
                (File.GetAttributes(objectsPath) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(objectsPath);
            }

            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
