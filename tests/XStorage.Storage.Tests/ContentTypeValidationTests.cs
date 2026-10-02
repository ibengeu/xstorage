using System.Security.Cryptography;
using XStorage.Contracts;
using XStorage.Storage;

namespace XStorage.Storage.Tests;

public sealed class ContentTypeValidationTests
{
    [Fact]
    public async Task PutObject_RejectsContentTypesOutsidePrintableAsciiAnd255Bytes()
    {
        var root = TestRoot();
        var options = TestOptions(root);
        var invalidTypes = new[]
        {
            new string('x', 256),
            "line\rbreak",
            "line\nbreak",
            "non-ascii café",
            "control\u0001byte"
        };

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("content-type", 128, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "content-type-bucket", CancellationToken.None);

            for (var index = 0; index < invalidTypes.Length; index++)
            {
                await using var input = new MemoryStream("body"u8.ToArray(), writable: false);
                var key = $"invalid-{index}";
                var error = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.PutObjectAsync(
                    application.AccessKeyId,
                    "content-type-bucket",
                    key,
                    new ObjectWriteRequest(input, input.Length, invalidTypes[index], null, null),
                    CancellationToken.None));
                var missing = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.HeadObjectAsync(
                    application.AccessKeyId,
                    "content-type-bucket",
                    key,
                    CancellationToken.None));

                Assert.Equal("InvalidArgument", error.Code);
                Assert.Equal(400, error.StatusCode);
                Assert.Equal("NoSuchKey", missing.Code);
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task PutObject_PreservesPrintableContentTypeAt255ByteLimit()
    {
        var root = TestRoot();
        var options = TestOptions(root);
        var contentType = " application/" + new string('x', 238) + " ";
        Assert.Equal(252, contentType.Length);
        contentType += "xxx";
        Assert.Equal(255, contentType.Length);

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("content-type", 128, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "content-type-bucket", CancellationToken.None);
            await using var input = new MemoryStream("body"u8.ToArray(), writable: false);

            await storage.PutObjectAsync(
                application.AccessKeyId,
                "content-type-bucket",
                "exact-limit",
                new ObjectWriteRequest(input, input.Length, contentType, null, null),
                CancellationToken.None);
            var metadata = await storage.HeadObjectAsync(
                application.AccessKeyId,
                "content-type-bucket",
                "exact-limit",
                CancellationToken.None);

            Assert.Equal(contentType, metadata.ContentType);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string TestRoot() => Path.Combine(
        OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        $"xstorage-content-type-{Guid.NewGuid():N}");

    private static ObjectStorageOptions TestOptions(string root) => new(
        root,
        1024,
        RandomNumberGenerator.GetBytes(32),
        RandomNumberGenerator.GetBytes(32));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
