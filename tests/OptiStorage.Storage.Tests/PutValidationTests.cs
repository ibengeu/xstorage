using System.Security.Cryptography;
using OptiStorage.Contracts;
using OptiStorage.Storage;

namespace OptiStorage.Storage.Tests;

public sealed class PutValidationTests
{
    [Fact]
    public async Task PutObject_AtMaximumObjectBytesIsStoredAndCounted()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            3,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("at-limit", 3, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "at-limit-bucket", CancellationToken.None);
            await using var input = new MemoryStream("max"u8.ToArray(), writable: false);
            var metadata = await storage.PutObjectAsync(
                application.AccessKeyId,
                "at-limit-bucket",
                "object",
                new ObjectWriteRequest(input, 3, "text/plain", null, null),
                CancellationToken.None);
            await using var result = await storage.GetObjectAsync(
                application.AccessKeyId,
                "at-limit-bucket",
                "object",
                CancellationToken.None);
            using var output = new MemoryStream();
            await result.Content.CopyToAsync(output);

            Assert.Equal(3, metadata.Size);
            Assert.Equal(3, Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None)).UsedBytes);
            Assert.Equal("max"u8.ToArray(), output.ToArray());
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
    public async Task PutObject_ContentMd5MismatchRejectsUploadWithoutPublishingObject()
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
            var application = await storage.CreateApplicationAsync("bad-md5", 64, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "bad-md5-bucket", CancellationToken.None);
            await using var input = new MemoryStream("body"u8.ToArray(), writable: false);
            var wrongMd5 = Convert.ToBase64String(new byte[16]);

            var error = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.PutObjectAsync(
                application.AccessKeyId,
                "bad-md5-bucket",
                "object",
                new ObjectWriteRequest(input, input.Length, "text/plain", wrongMd5, null),
                CancellationToken.None));
            var missing = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.HeadObjectAsync(
                application.AccessKeyId,
                "bad-md5-bucket",
                "object",
                CancellationToken.None));

            Assert.Equal("BadDigest", error.Code);
            Assert.Equal(400, error.StatusCode);
            Assert.Equal("NoSuchKey", missing.Code);
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
    public async Task PutObject_ExceedingMaximumObjectBytesRejectsWithoutPublishing()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            3,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("oversize", 64, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "oversize-bucket", CancellationToken.None);
            var declaredError = await PutOversizeAsync(storage, application.AccessKeyId, "declared", 4);
            var streamedError = await PutOversizeAsync(storage, application.AccessKeyId, "streamed", null);
            var missingDeclared = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.HeadObjectAsync(
                application.AccessKeyId,
                "oversize-bucket",
                "declared",
                CancellationToken.None));
            var missingStreamed = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.HeadObjectAsync(
                application.AccessKeyId,
                "oversize-bucket",
                "streamed",
                CancellationToken.None));

            Assert.Equal("EntityTooLarge", declaredError.Code);
            Assert.Equal("EntityTooLarge", streamedError.Code);
            Assert.Equal("NoSuchKey", missingDeclared.Code);
            Assert.Equal("NoSuchKey", missingStreamed.Code);
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
    public async Task PutObject_SignedPayloadHashMismatchRejectsWithoutPublishing()
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
            var application = await storage.CreateApplicationAsync("bad-payload-hash", 64, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "bad-payload-hash-bucket", CancellationToken.None);
            await using var input = new MemoryStream("body"u8.ToArray(), writable: false);
            var wrongHash = Convert.ToHexString(SHA256.HashData("other"u8)).ToLowerInvariant();

            var error = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.PutObjectAsync(
                application.AccessKeyId,
                "bad-payload-hash-bucket",
                "object",
                new ObjectWriteRequest(input, input.Length, "text/plain", null, wrongHash),
                CancellationToken.None));
            var missing = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.HeadObjectAsync(
                application.AccessKeyId,
                "bad-payload-hash-bucket",
                "object",
                CancellationToken.None));

            Assert.Equal("SignatureDoesNotMatch", error.Code);
            Assert.Equal(403, error.StatusCode);
            Assert.Equal("NoSuchKey", missing.Code);
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
    [InlineData(nameof(StorageFaultPoint.AfterObjectTemporaryFileCreated))]
    [InlineData(nameof(StorageFaultPoint.AfterObjectHeaderPlaceholderWritten))]
    [InlineData(nameof(StorageFaultPoint.AfterObjectBodyCopied))]
    [InlineData(nameof(StorageFaultPoint.AfterObjectRecordFinalized))]
    [InlineData(nameof(StorageFaultPoint.AfterObjectTemporaryFileFlush))]
    [InlineData(nameof(StorageFaultPoint.BeforeObjectRename))]
    public async Task PutObject_FailureBeforeRenameKeepsOldVersionAndUsageVisible(string failurePoint)
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
        var injectFailure = false;
        options.FaultInjector = point =>
        {
            if (injectFailure && point.ToString() == failurePoint)
            {
                throw new IOException("Injected failure before object rename.");
            }
        };

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var application = await storage.CreateApplicationAsync("pre-rename", 64, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "pre-rename-bucket", CancellationToken.None);
            await using var original = new MemoryStream("old"u8.ToArray(), writable: false);
            await storage.PutObjectAsync(
                application.AccessKeyId,
                "pre-rename-bucket",
                "object",
                new ObjectWriteRequest(original, original.Length, "text/plain", null, null),
                CancellationToken.None);
            injectFailure = true;
            await using var replacement = new MemoryStream("new-value"u8.ToArray(), writable: false);

            await Assert.ThrowsAsync<IOException>(() => storage.PutObjectAsync(
                application.AccessKeyId,
                "pre-rename-bucket",
                "object",
                new ObjectWriteRequest(replacement, replacement.Length, "text/plain", null, null),
                CancellationToken.None));

            await using StoredObject result = await storage.GetObjectAsync(
                application.AccessKeyId,
                "pre-rename-bucket",
                "object",
                CancellationToken.None);
            using var output = new MemoryStream();
            await result.Content.CopyToAsync(output);
            Assert.Equal("old"u8.ToArray(), output.ToArray());
            Assert.Equal(3, result.Metadata.Size);
            Assert.Equal(3, Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None)).UsedBytes);
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
    public async Task PutObject_InvalidUtf8KeyReturnsInvalidArgument()
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
            var application = await storage.CreateApplicationAsync("invalid-key", 64, CancellationToken.None);
            await storage.CreateBucketAsync(application.AccessKeyId, "invalid-key-bucket", CancellationToken.None);
            await using var input = new MemoryStream("body"u8.ToArray(), writable: false);

            var error = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.PutObjectAsync(
                application.AccessKeyId,
                "invalid-key-bucket",
                "invalid-\ud800",
                new ObjectWriteRequest(input, input.Length, "text/plain", null, null),
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

    private static async Task<ObjectStorageException> PutOversizeAsync(
        IObjectStorage storage,
        string accessKeyId,
        string key,
        long? declaredLength)
    {
        await using var input = new MemoryStream("four"u8.ToArray(), writable: false);
        var error = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.PutObjectAsync(
            accessKeyId,
            "oversize-bucket",
            key,
            new ObjectWriteRequest(input, declaredLength, "text/plain", null, null),
            CancellationToken.None));
        Assert.Equal(413, error.StatusCode);
        return error;
    }
}
