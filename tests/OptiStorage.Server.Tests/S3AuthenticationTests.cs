using System.Net;
using OptiStorage.Server;

namespace OptiStorage.Server.Tests;

public sealed class S3AuthenticationTests
{
    [Fact]
    public async Task RequestWithoutSigV4_ReturnsS3AccessDeniedAndRequestId()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "optistorage-s3-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            using var client = new HttpClient { BaseAddress = host.DataAddress };

            using var response = await client.GetAsync("/private-bucket/private-object");
            var content = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.True(response.Headers.Contains("x-amz-request-id"));
            Assert.Contains("<Code>AccessDenied</Code>", content, StringComparison.Ordinal);
            Assert.DoesNotContain("<Applications", content, StringComparison.Ordinal);
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
