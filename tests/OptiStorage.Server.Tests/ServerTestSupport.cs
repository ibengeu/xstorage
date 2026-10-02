using System.Net.Http.Headers;
using System.Security.Cryptography;
using OptiStorage.Server;

namespace OptiStorage.Server.Tests;

internal static class ServerTestSupport
{
    public static string AdminToken { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static StorageServiceSettings Settings(string root, IReadOnlyList<string>? trustedProxies = null) => new(
        root,
        "http://127.0.0.1:0",
        "http://127.0.0.1:0",
        AdminToken,
        RandomNumberGenerator.GetBytes(32),
        RandomNumberGenerator.GetBytes(32),
        TrustedProxyAddresses: trustedProxies);

    public static HttpClient CreateAdminClient(Uri address)
    {
        var client = new HttpClient { BaseAddress = address };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        return client;
    }
}
