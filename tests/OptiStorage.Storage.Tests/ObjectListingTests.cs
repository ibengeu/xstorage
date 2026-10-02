using System.Security.Cryptography;
using System.Text;
using OptiStorage.Contracts;
using OptiStorage.Storage;

namespace OptiStorage.Storage.Tests;

public sealed class ObjectListingTests
{
    [Fact]
    public async Task ListObjectsV2_OrdersKeysByUtf8Bytes()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("listing", 64, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "listing-bucket", CancellationToken.None);
            foreach (var key in new[] { "é", "z", "a" })
            {
                await using var input = new MemoryStream(Encoding.UTF8.GetBytes(key), writable: false);
                await storage.PutObjectAsync(
                    application.AccessKeyId,
                    "listing-bucket",
                    key,
                    new ObjectWriteRequest(input, input.Length, "text/plain", null, null),
                    CancellationToken.None);
            }

            var page = await storage.ListObjectsV2Async(
                application.AccessKeyId,
                "listing-bucket",
                new ObjectListingRequest(MaxKeys: 3),
                CancellationToken.None);

            Assert.Equal(new[] { "a", "z", "é" }, page.Contents.Select(item => item.Key));
            Assert.Equal(3, page.KeyCount);
            Assert.False(page.IsTruncated);
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
    public async Task ListObjectsV2_OrdersSupplementaryKeysByUtf8Bytes()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
        var supplementaryKey = char.ConvertFromUtf32(0x10000);

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("supplementary-listing", 64, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "supplementary-listing-bucket", CancellationToken.None);
            foreach (var key in new[] { supplementaryKey, "\uE000" })
            {
                await using var input = new MemoryStream(Encoding.UTF8.GetBytes(key), writable: false);
                await storage.PutObjectAsync(
                    application.AccessKeyId,
                    "supplementary-listing-bucket",
                    key,
                    new ObjectWriteRequest(input, input.Length, "text/plain", null, null),
                    CancellationToken.None);
            }

            var page = await storage.ListObjectsV2Async(
                application.AccessKeyId,
                "supplementary-listing-bucket",
                new ObjectListingRequest(),
                CancellationToken.None);

            Assert.Equal(new[] { "\uE000", supplementaryKey }, page.Contents.Select(item => item.Key));
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
    public async Task ListObjectsV2_PrefixMatchesLiteralKeyPrefixes()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("prefix", 64, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "prefix-bucket", CancellationToken.None);
            foreach (var key in new[] { "photos/a.jpg", "photos-old/b.jpg", "docs/a.txt" })
            {
                await using var input = new MemoryStream(Encoding.UTF8.GetBytes(key), writable: false);
                await storage.PutObjectAsync(
                    application.AccessKeyId,
                    "prefix-bucket",
                    key,
                    new ObjectWriteRequest(input, input.Length, "text/plain", null, null),
                    CancellationToken.None);
            }

            var page = await storage.ListObjectsV2Async(
                application.AccessKeyId,
                "prefix-bucket",
                new ObjectListingRequest(Prefix: "photos/"),
                CancellationToken.None);

            Assert.Equal(new[] { "photos/a.jpg" }, page.Contents.Select(item => item.Key));
            Assert.Equal(1, page.KeyCount);
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
    public async Task ListObjectsV2_DelimiterGroupsMatchingKeysIntoCommonPrefixes()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("delimiter", 128, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "delimiter-bucket", CancellationToken.None);
            foreach (var key in new[] { "photos/a.jpg", "photos/nested/b.jpg", "photos/nested/deep/c.jpg", "text.txt" })
            {
                await using var input = new MemoryStream(Encoding.UTF8.GetBytes(key), writable: false);
                await storage.PutObjectAsync(
                    application.AccessKeyId,
                    "delimiter-bucket",
                    key,
                    new ObjectWriteRequest(input, input.Length, "text/plain", null, null),
                    CancellationToken.None);
            }

            var page = await storage.ListObjectsV2Async(
                application.AccessKeyId,
                "delimiter-bucket",
                new ObjectListingRequest(Prefix: "photos/", Delimiter: "/"),
                CancellationToken.None);

            Assert.Equal(new[] { "photos/a.jpg" }, page.Contents.Select(item => item.Key));
            Assert.Equal(new[] { "photos/nested/" }, page.CommonPrefixes);
            Assert.Equal(2, page.KeyCount);
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
    public async Task ListObjectsV2_DelimiterPrefixesParticipateInPageBoundaries()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("delimiter-pages", 128, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "delimiter-pages-bucket", CancellationToken.None);
            foreach (var key in new[] { "alpha/a", "alpha/b", "beta/a", "z" })
            {
                await using var input = new MemoryStream(Encoding.UTF8.GetBytes(key), writable: false);
                await storage.PutObjectAsync(
                    application.AccessKeyId,
                    "delimiter-pages-bucket",
                    key,
                    new ObjectWriteRequest(input, input.Length, "text/plain", null, null),
                    CancellationToken.None);
            }

            var first = await storage.ListObjectsV2Async(
                application.AccessKeyId,
                "delimiter-pages-bucket",
                new ObjectListingRequest(Delimiter: "/", MaxKeys: 1),
                CancellationToken.None);
            var second = await storage.ListObjectsV2Async(
                application.AccessKeyId,
                "delimiter-pages-bucket",
                new ObjectListingRequest(Delimiter: "/", MaxKeys: 1, ContinuationToken: first.NextContinuationToken),
                CancellationToken.None);
            var third = await storage.ListObjectsV2Async(
                application.AccessKeyId,
                "delimiter-pages-bucket",
                new ObjectListingRequest(Delimiter: "/", MaxKeys: 1, ContinuationToken: second.NextContinuationToken),
                CancellationToken.None);

            Assert.Equal(new[] { "alpha/" }, first.CommonPrefixes);
            Assert.Equal(new[] { "beta/" }, second.CommonPrefixes);
            Assert.Equal(new[] { "z" }, third.Contents.Select(item => item.Key));
            Assert.False(third.IsTruncated);
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
    public async Task ListObjectsV2_StartAfterExcludesEntriesAtOrBeforeValue()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("start-after", 64, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "start-after-bucket", CancellationToken.None);
            foreach (var key in new[] { "a", "b", "c" })
            {
                await using var input = new MemoryStream(Encoding.UTF8.GetBytes(key), writable: false);
                await storage.PutObjectAsync(
                    application.AccessKeyId,
                    "start-after-bucket",
                    key,
                    new ObjectWriteRequest(input, input.Length, "text/plain", null, null),
                    CancellationToken.None);
            }

            var page = await storage.ListObjectsV2Async(
                application.AccessKeyId,
                "start-after-bucket",
                new ObjectListingRequest(StartAfter: "b"),
                CancellationToken.None);

            Assert.Equal(new[] { "c" }, page.Contents.Select(item => item.Key));
            Assert.Equal("b", page.StartAfter);
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
    public async Task ListObjectsV2_MaxKeysZeroReturnsNoEntriesAndNoNextToken()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("zero-keys", 64, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "zero-keys-bucket", CancellationToken.None);
            await using var input = new MemoryStream("value"u8.ToArray(), writable: false);
            await storage.PutObjectAsync(
                application.AccessKeyId,
                "zero-keys-bucket",
                "a",
                new ObjectWriteRequest(input, input.Length, "text/plain", null, null),
                CancellationToken.None);

            var page = await storage.ListObjectsV2Async(
                application.AccessKeyId,
                "zero-keys-bucket",
                new ObjectListingRequest(MaxKeys: 0),
                CancellationToken.None);

            Assert.Empty(page.Contents);
            Assert.Empty(page.CommonPrefixes);
            Assert.Equal(0, page.KeyCount);
            Assert.False(page.IsTruncated);
            Assert.Null(page.NextContinuationToken);
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
    public async Task ListObjectsV2_ContinuationReturnsEntriesAfterThePreviousPage()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("pages", 64, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "pages-bucket", CancellationToken.None);
            foreach (var key in new[] { "a", "b", "c" })
            {
                await using var input = new MemoryStream(Encoding.UTF8.GetBytes(key), writable: false);
                await storage.PutObjectAsync(
                    application.AccessKeyId,
                    "pages-bucket",
                    key,
                    new ObjectWriteRequest(input, input.Length, "text/plain", null, null),
                    CancellationToken.None);
            }

            var firstPage = await storage.ListObjectsV2Async(
                application.AccessKeyId,
                "pages-bucket",
                new ObjectListingRequest(MaxKeys: 1),
                CancellationToken.None);
            var secondPage = await storage.ListObjectsV2Async(
                application.AccessKeyId,
                "pages-bucket",
                new ObjectListingRequest(MaxKeys: 1, ContinuationToken: firstPage.NextContinuationToken),
                CancellationToken.None);

            Assert.Equal(new[] { "a" }, firstPage.Contents.Select(item => item.Key));
            Assert.True(firstPage.IsTruncated);
            Assert.False(string.IsNullOrWhiteSpace(firstPage.NextContinuationToken));
            Assert.Equal(new[] { "b" }, secondPage.Contents.Select(item => item.Key));
            Assert.True(secondPage.IsTruncated);
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
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task ListObjectsV2_InvalidMaxKeysReturnsInvalidArgument(int maxKeys)
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("invalid-max-keys", 64, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "invalid-max-keys-bucket", CancellationToken.None);

            var error = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.ListObjectsV2Async(
                application.AccessKeyId,
                "invalid-max-keys-bucket",
                new ObjectListingRequest(MaxKeys: maxKeys),
                CancellationToken.None));

            Assert.Equal("InvalidArgument", error.Code);
            Assert.Equal(400, error.StatusCode);
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
    public async Task ListObjectsV2_ContinuationTokenRemainsValidAfterStorageRestart()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
        string? continuationToken = null;
        string accessKeyId;

        try
        {
            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var application = await storage.CreateApplicationAsync("restart-token", 64, CancellationToken.None);
                accessKeyId = application.AccessKeyId;
                await storage.CreateBucketAsync(accessKeyId, "restart-token-bucket", CancellationToken.None);
                foreach (var key in new[] { "a", "b" })
                {
                    await using var input = new MemoryStream(Encoding.UTF8.GetBytes(key), writable: false);
                    await storage.PutObjectAsync(
                        accessKeyId,
                        "restart-token-bucket",
                        key,
                        new ObjectWriteRequest(input, input.Length, "text/plain", null, null),
                        CancellationToken.None);
                }

                var firstPage = await storage.ListObjectsV2Async(
                    accessKeyId,
                    "restart-token-bucket",
                    new ObjectListingRequest(MaxKeys: 1),
                    CancellationToken.None);
                continuationToken = firstPage.NextContinuationToken;
                Assert.True(firstPage.IsTruncated);
            }

            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var nextPage = await storage.ListObjectsV2Async(
                    accessKeyId,
                    "restart-token-bucket",
                    new ObjectListingRequest(MaxKeys: 1, ContinuationToken: continuationToken),
                    CancellationToken.None);

                Assert.Equal(new[] { "b" }, nextPage.Contents.Select(item => item.Key));
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

    [Fact]
    public async Task ListObjectsV2_TamperedContinuationTokenReturnsInvalidArgument()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("tampered-token", 64, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "tampered-token-bucket", CancellationToken.None);
            foreach (var key in new[] { "a", "b" })
            {
                await using var input = new MemoryStream(Encoding.UTF8.GetBytes(key), writable: false);
                await storage.PutObjectAsync(
                    application.AccessKeyId,
                    "tampered-token-bucket",
                    key,
                    new ObjectWriteRequest(input, input.Length, "text/plain", null, null),
                    CancellationToken.None);
            }

            var firstPage = await storage.ListObjectsV2Async(
                application.AccessKeyId,
                "tampered-token-bucket",
                new ObjectListingRequest(MaxKeys: 1),
                CancellationToken.None);
            var token = firstPage.NextContinuationToken!;
            var signatureIndex = token.Length - 8;
            var replacement = token[signatureIndex] == 'A' ? 'B' : 'A';
            var tamperedToken = token[..signatureIndex] + replacement + token[(signatureIndex + 1)..];

            var error = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.ListObjectsV2Async(
                application.AccessKeyId,
                "tampered-token-bucket",
                new ObjectListingRequest(MaxKeys: 1, ContinuationToken: tamperedToken),
                CancellationToken.None));

            Assert.Equal("InvalidArgument", error.Code);
            Assert.Equal(400, error.StatusCode);
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
    public async Task ListObjectsV2_ContinuationTokenCannotBeReusedForAnotherOwnedBucket()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("scoped-token", 64, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "source-bucket", CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "target-bucket", CancellationToken.None);
            foreach (var key in new[] { "a", "b" })
            {
                await using var input = new MemoryStream(Encoding.UTF8.GetBytes(key), writable: false);
                await storage.PutObjectAsync(
                    application.AccessKeyId,
                    "source-bucket",
                    key,
                    new ObjectWriteRequest(input, input.Length, "text/plain", null, null),
                    CancellationToken.None);
            }

            var firstPage = await storage.ListObjectsV2Async(
                application.AccessKeyId,
                "source-bucket",
                new ObjectListingRequest(MaxKeys: 1),
                CancellationToken.None);
            var error = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.ListObjectsV2Async(
                application.AccessKeyId,
                "target-bucket",
                new ObjectListingRequest(MaxKeys: 1, ContinuationToken: firstPage.NextContinuationToken),
                CancellationToken.None));

            Assert.Equal("InvalidArgument", error.Code);
            Assert.Equal(400, error.StatusCode);
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
    public async Task ListObjectsV2_UnsupportedEncodingTypeReturnsInvalidArgument()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("encoding", 64, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "encoding-bucket", CancellationToken.None);

            var error = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.ListObjectsV2Async(
                application.AccessKeyId,
                "encoding-bucket",
                new ObjectListingRequest(EncodingType: "base64"),
                CancellationToken.None));

            Assert.Equal("InvalidArgument", error.Code);
            Assert.Equal(400, error.StatusCode);
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
