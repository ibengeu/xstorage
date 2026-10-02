using System.Security.Cryptography;
using XStorage.Contracts;
using XStorage.Storage;

namespace XStorage.Storage.Tests;

public sealed class BucketNameValidationTests
{
    [Fact]
    public async Task CreateBucket_RejectsNamesOutsideTheDnsStyleRules()
    {
        var root = TestRoot();
        var options = TestOptions(root);
        var invalidNames = new[]
        {
            "ab",
            new string('a', 64),
            "Upper-case",
            "under_score",
            "leading..dots",
            "dot.-hyphen",
            "hyphen-.dot",
            "xn--reserved",
            "bucket-s3alias",
            "192.168.1.1",
            ".leading-dot",
            "trailing-dot.",
            "-leading-hyphen",
            "trailing-hyphen-"
        };

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("bucket-rules", 64, CancellationToken.None);

            foreach (var bucket in invalidNames)
            {
                var error = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.CreateBucketAsync(
                    application.AccessKeyId,
                    bucket,
                    CancellationToken.None));
                Assert.Equal("InvalidBucketName", error.Code);
                Assert.Equal(400, error.StatusCode);
            }

            Assert.Equal(0, Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None)).BucketCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CreateBucket_AcceptsLowercaseDnsNameWithDotsAndHyphens()
    {
        var root = TestRoot();
        var options = TestOptions(root);

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("bucket-rules", 64, CancellationToken.None);

            await storage.CreateBucketAsync(application.AccessKeyId, "valid.bucket-name", CancellationToken.None);

            Assert.Equal(1, Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None)).BucketCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string TestRoot() => Path.Combine(
        OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
        $"xstorage-bucket-rules-{Guid.NewGuid():N}");

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
