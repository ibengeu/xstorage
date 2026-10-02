using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using XStorage.Server;

namespace XStorage.Server.Tests;

public sealed class ListenerConfigurationTests
{
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public async Task StartAsync_RejectsNegativeApplicationResourceLimits(int maximumObjects, int maximumBuckets)
    {
        var root = Path.Combine(
            OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            $"xstorage-listener-config-{Guid.NewGuid():N}");
        var settings = new StorageServiceSettings(
            root,
            "http://127.0.0.1:0",
            "http://127.0.0.1:1",
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32),
            MaximumObjectsPerApplication: maximumObjects,
            MaximumBucketsPerApplication: maximumBuckets);

        try
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => StorageServiceHost.StartAsync(settings));
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
    public async Task StartAsync_RejectsTheSamePortForDifferentHosts()
    {
        var root = Path.Combine(
            OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            $"xstorage-listener-config-{Guid.NewGuid():N}");
        using var reservedPort = new TcpListener(IPAddress.Loopback, 0);
        reservedPort.Start();
        var port = ((IPEndPoint)reservedPort.LocalEndpoint).Port;
        var settings = new StorageServiceSettings(
            root,
            $"http://127.0.0.1:{port}",
            $"http://0.0.0.0:{port}",
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));

        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => StorageServiceHost.StartAsync(settings));
        }
        finally
        {
            reservedPort.Stop();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
