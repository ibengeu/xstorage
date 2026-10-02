using System.Security.Cryptography;
using System.Text;
using XStorage.Contracts;
using XStorage.Storage;

namespace XStorage.Storage.Tests;

public sealed class ConcurrentObjectAccessTests
{
    [Fact]
    public async Task ConcurrentPutGetHeadDeleteOnOneKey_AlwaysReturnsWholeCommittedVersions()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"xstorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(root, 1024, RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("same-key-stress", 4096, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "same-key-stress-bucket", CancellationToken.None);
            var candidates = Enumerable.Range(0, 24).Select(index => Encoding.UTF8.GetBytes($"whole-version-{index:D2}")).ToArray();
            var operations = Enumerable.Range(0, 96).Select(async index =>
            {
                switch (index % 4)
                {
                    case 0:
                        {
                            var bytes = candidates[index / 4];
                            await using var upload = new MemoryStream(bytes, writable: false);
                            await storage.PutObjectAsync(application.AccessKeyId, "same-key-stress-bucket", "shared-key",
                                new ObjectWriteRequest(upload, bytes.Length, "application/octet-stream", null, null), CancellationToken.None);
                            break;
                        }
                    case 1:
                        await VerifyConcurrentReadAsync(storage, application.AccessKeyId, candidates);
                        break;
                    case 2:
                        await VerifyConcurrentHeadAsync(storage, application.AccessKeyId, candidates);
                        break;
                    case 3:
                        await storage.DeleteObjectAsync(application.AccessKeyId, "same-key-stress-bucket", "shared-key", CancellationToken.None);
                        break;
                }
            });
            await Task.WhenAll(operations);

            var finalBytes = "final-whole-version"u8.ToArray();
            await using var finalUpload = new MemoryStream(finalBytes, writable: false);
            await storage.PutObjectAsync(application.AccessKeyId, "same-key-stress-bucket", "shared-key",
                new ObjectWriteRequest(finalUpload, finalBytes.Length, "application/octet-stream", null, null), CancellationToken.None);
            await VerifyConcurrentReadAsync(storage, application.AccessKeyId, [finalBytes]);
            var summary = Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None));

            Assert.Equal(finalBytes.Length, summary.UsedBytes);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task VerifyConcurrentReadAsync(IObjectStorage storage, string accessKeyId, IReadOnlyList<byte[]> validVersions)
    {
        try
        {
            await using var stored = await storage.GetObjectAsync(accessKeyId, "same-key-stress-bucket", "shared-key", CancellationToken.None);
            using var output = new MemoryStream();
            await stored.Content.CopyToAsync(output);
            var actual = output.ToArray();
            Assert.Contains(validVersions, version => version.AsSpan().SequenceEqual(actual));
            Assert.Equal(actual.Length, stored.Metadata.Size);
            Assert.Equal($"\"{Convert.ToHexString(MD5.HashData(actual)).ToLowerInvariant()}\"", stored.Metadata.ETag);
        }
        catch (ObjectStorageException exception) when (exception.Code == "NoSuchKey")
        {
        }
    }

    private static async Task VerifyConcurrentHeadAsync(IObjectStorage storage, string accessKeyId, IReadOnlyList<byte[]> validVersions)
    {
        try
        {
            var metadata = await storage.HeadObjectAsync(accessKeyId, "same-key-stress-bucket", "shared-key", CancellationToken.None);
            Assert.Contains(validVersions, version =>
                version.Length == metadata.Size
                && $"\"{Convert.ToHexString(MD5.HashData(version)).ToLowerInvariant()}\"" == metadata.ETag);
        }
        catch (ObjectStorageException exception) when (exception.Code == "NoSuchKey")
        {
        }
    }
}
