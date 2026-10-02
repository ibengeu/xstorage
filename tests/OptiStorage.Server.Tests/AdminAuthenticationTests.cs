using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OptiStorage.Server;

namespace OptiStorage.Server.Tests;

public sealed class AdminAuthenticationTests
{
    [Fact]
    public async Task CreateApplication_WithoutBearerToken_ReturnsUnauthorizedBeforeBodyValidation()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "optistorage-admin-missing-token-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            using var client = new HttpClient { BaseAddress = host.AdminAddress };
            using var response = await client.PostAsync("/admin/applications", new StringContent("not-json"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(await host.Storage.ListApplicationsAsync(CancellationToken.None));
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
    public async Task CreateApplication_WithInvalidBearerToken_ReturnsUnauthorizedBeforeBodyValidation()
    {
        var tempRoot = OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath();
        var root = Path.Combine(tempRoot, "optistorage-admin-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root)))
            {
                using var client = new HttpClient { BaseAddress = host.AdminAddress };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-" + ServerTestSupport.AdminToken);
                using var response = await client.PostAsync("/admin/applications", new StringContent("not-json"));

                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Empty(await host.Storage.ListApplicationsAsync(CancellationToken.None));
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
    public async Task AdminSecretIsReturnedOnCreateAndNeverAppearsInListOrDataPlane()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "optistorage-admin-create-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            using var admin = ServerTestSupport.CreateAdminClient(host.AdminAddress);

            using var created = await admin.PostAsync("/admin/applications", new StringContent("{\"name\":\"billing\",\"quotaBytes\":4096}", System.Text.Encoding.UTF8, "application/json"));
            var credentials = await created.Content.ReadAsStringAsync();
            using var listed = await admin.GetAsync("/admin/applications");
            var summaries = await listed.Content.ReadAsStringAsync();
            using var client = new HttpClient { BaseAddress = host.DataAddress };
            using var dataPlaneProbe = await client.GetAsync("/admin/applications");
            var probeBody = await dataPlaneProbe.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Contains("\"secretAccessKey\"", credentials, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            Assert.DoesNotContain("secretAccessKey", summaries, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(HttpStatusCode.Forbidden, dataPlaneProbe.StatusCode);
            Assert.Contains("<Code>AccessDenied</Code>", probeBody, StringComparison.Ordinal);
            Assert.DoesNotContain("billing", probeBody, StringComparison.Ordinal);
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
    public async Task CreateApplication_AllowsDuplicateDisplayNamesWithDifferentAccessKeys()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "optistorage-admin-duplicate-name-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            using var admin = ServerTestSupport.CreateAdminClient(host.AdminAddress);
            using var firstResponse = await admin.PostAsync("/admin/applications", new StringContent(
                "{\"name\":\"shared-label\",\"quotaBytes\":64}", System.Text.Encoding.UTF8, "application/json"));
            using var secondResponse = await admin.PostAsync("/admin/applications", new StringContent(
                "{\"name\":\"shared-label\",\"quotaBytes\":128}", System.Text.Encoding.UTF8, "application/json"));
            using var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
            using var second = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
            Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
            Assert.Equal("shared-label", first.RootElement.GetProperty("name").GetString());
            Assert.Equal("shared-label", second.RootElement.GetProperty("name").GetString());
            Assert.NotEqual(
                first.RootElement.GetProperty("accessKeyId").GetString(),
                second.RootElement.GetProperty("accessKeyId").GetString());
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
