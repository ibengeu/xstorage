using System.Security.Cryptography;
using System.Text;
using XStorage.Contracts;
using XStorage.Storage;

namespace XStorage.Storage.Tests;

public sealed class CredentialRotationTests
{
    [Fact]
    public async Task RotateCredentials_KeepsApplicationIdentityAndStoredObjects()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"xstorage-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
            var created = await storage.CreateApplicationAsync("rotation", 16, CancellationToken.None);
            await storage.CreateBucketAsync(created.AccessKeyId, "rotation-bucket", CancellationToken.None);
            var payload = Encoding.UTF8.GetBytes("kept");
            await using var input = new MemoryStream(payload, writable: false);
            await storage.PutObjectAsync(
                created.AccessKeyId,
                "rotation-bucket",
                "kept.txt",
                new ObjectWriteRequest(input, payload.Length, "text/plain", null, null),
                CancellationToken.None);

            var originalSigningKey = await storage.FindSigningKeyAsync(created.AccessKeyId, CancellationToken.None);
            var rotated = await storage.RotateCredentialsAsync(created.AccessKeyId, CancellationToken.None);
            var signingKey = await storage.FindSigningKeyAsync(created.AccessKeyId, CancellationToken.None);
            var originalKeyIsCurrent = await storage.ConfirmSigningKeyAsync(
                created.AccessKeyId,
                originalSigningKey!.CredentialVersion,
                CancellationToken.None);
            var rotatedKeyIsCurrent = await storage.ConfirmSigningKeyAsync(
                created.AccessKeyId,
                signingKey!.CredentialVersion,
                CancellationToken.None);
            var listed = await storage.ListApplicationsAsync(CancellationToken.None);
            await using var objectResult = await storage.GetObjectAsync(created.AccessKeyId, "rotation-bucket", "kept.txt", CancellationToken.None);
            using var output = new MemoryStream();
            await objectResult.Content.CopyToAsync(output);

            Assert.Equal(created.AccessKeyId, rotated.AccessKeyId);
            Assert.NotEqual(created.SecretAccessKey, rotated.SecretAccessKey);
            Assert.Equal(rotated.SecretAccessKey, signingKey!.SecretAccessKey);
            Assert.False(originalKeyIsCurrent);
            Assert.True(rotatedKeyIsCurrent);
            Assert.Equal(16, listed.Single().QuotaBytes);
            Assert.Equal(payload.Length, listed.Single().UsedBytes);
            Assert.Equal(1, listed.Single().BucketCount);
            Assert.Equal(payload, output.ToArray());
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
