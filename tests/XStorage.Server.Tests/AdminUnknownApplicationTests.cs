using System.Net;
using System.Net.Http.Json;
using XStorage.Server;

namespace XStorage.Server.Tests;

public sealed class AdminUnknownApplicationTests
{
    [Theory]
    [InlineData("POST", "/admin/applications/OSAAAAAAAAAAAAAAAAAAAAAAAA/rotate")]
    [InlineData("PATCH", "/admin/applications/OSAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("DELETE", "/admin/applications/OSAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task MutationForUnknownApplication_ReturnsNotFound(string method, string path)
    {
        var root = Path.Combine(
            OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            $"xstorage-admin-unknown-{Guid.NewGuid():N}");

        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            using var admin = ServerTestSupport.CreateAdminClient(host.AdminAddress);
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method == "PATCH")
            {
                request.Content = JsonContent.Create(new { quotaBytes = 128 });
            }

            using var response = await admin.SendAsync(request);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
