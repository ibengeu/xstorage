using OptiStorage.Client;
using OptiStorage.Server;
using System.Security.Cryptography;

namespace OptiStorage.Client.Tests;

public sealed class S3ObjectStoreTests
{
    private static readonly string AdminToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    [Fact]
    public async Task ClientPreservesUploadOwnershipAndReturnsAClosableDownload()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "optistorage-client-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await StorageServiceHost.StartAsync(new StorageServiceSettings(
                root, "http://127.0.0.1:0", "http://127.0.0.1:0", AdminToken,
                RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32)));
            var credentials = await host.Storage.CreateApplicationAsync("client", 4096, CancellationToken.None);
            await using var store = new S3ObjectStore(new ObjectStoreClientOptions(
                host.DataAddress.ToString(), credentials.AccessKeyId, credentials.SecretAccessKey));

            await store.CreateBucketAsync("client-bucket");
            var bytes = "client stream body"u8.ToArray();
            await using var upload = new TrackingStream(bytes);
            var put = await store.PutObjectAsync("client-bucket", "folder/file.txt", upload, bytes.Length, "text/plain");
            Assert.False(upload.WasDisposed);
            Assert.StartsWith("\"", put.ETag, StringComparison.Ordinal);

            await using (var downloaded = await store.GetObjectAsync("client-bucket", "folder/file.txt"))
            {
                using var output = new MemoryStream();
                await downloaded.Content.CopyToAsync(output);
                Assert.Equal(bytes, output.ToArray());
                Assert.Equal("text/plain", downloaded.Metadata.ContentType);
            }

            Assert.Equal(bytes.Length, (await store.HeadObjectAsync("client-bucket", "folder/file.txt")).Size);
            Assert.Single((await store.ListObjectsV2Async("client-bucket", new OptiStorage.Contracts.ObjectListingRequest())).Contents);
            await store.DeleteObjectAsync("client-bucket", "folder/file.txt");
            var missing = await Assert.ThrowsAsync<ObjectStoreException>(() => store.GetObjectAsync("client-bucket", "folder/file.txt"));
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

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            WasDisposed = true;
            return base.DisposeAsync();
        }
    }
}
