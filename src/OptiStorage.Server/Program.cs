using OptiStorage.Server;
using System.Runtime.InteropServices;
using System.Globalization;

var dataRoot = RequiredSetting("OPTISTORAGE_DATA_ROOT");
var adminToken = RequiredSetting("OPTISTORAGE_ADMIN_TOKEN");
var appEncryptionKey = Convert.FromBase64String(RequiredSetting("OPTISTORAGE_APP_ENCRYPTION_KEY"));
var continuationTokenKey = Convert.FromBase64String(RequiredSetting("OPTISTORAGE_CONTINUATION_TOKEN_KEY"));
var maximumObjectBytes = ReadMaximumObjectBytes();
var maximumObjectsPerApplication = ReadNonNegativeLimit("OPTISTORAGE_MAX_OBJECTS_PER_APPLICATION", 10_000);
var maximumBucketsPerApplication = ReadNonNegativeLimit("OPTISTORAGE_MAX_BUCKETS_PER_APPLICATION", 100);
var dataPlaneUrl = Environment.GetEnvironmentVariable("OPTISTORAGE_DATA_URL") ?? "http://127.0.0.1:9000";
var adminUrl = Environment.GetEnvironmentVariable("OPTISTORAGE_ADMIN_URL") ?? "http://127.0.0.1:9001";
var trustedProxies = (Environment.GetEnvironmentVariable("OPTISTORAGE_TRUSTED_PROXIES") ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

await using var host = await StorageServiceHost.StartAsync(new StorageServiceSettings(
    dataRoot,
    dataPlaneUrl,
    adminUrl,
    adminToken,
    appEncryptionKey,
    continuationTokenKey,
    maximumObjectBytes,
    trustedProxies,
    maximumObjectsPerApplication,
    maximumBucketsPerApplication));

Console.WriteLine($"S3 endpoint: {host.DataAddress}");
Console.WriteLine($"Admin endpoint: {host.AdminAddress}");
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
using var terminationSignal = OperatingSystem.IsWindows()
    ? null
    : PosixSignalRegistration.Create(PosixSignal.SIGTERM, signalContext =>
    {
        signalContext.Cancel = true;
        shutdown.Cancel();
    });

try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
}
catch (OperationCanceledException)
{
}

static string RequiredSetting(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Required environment setting {name} is missing.");

static long ReadMaximumObjectBytes()
{
    var configuredValue = Environment.GetEnvironmentVariable("OPTISTORAGE_MAX_OBJECT_BYTES");
    if (configuredValue is null)
    {
        return 26_214_400;
    }

    if (!long.TryParse(configuredValue, NumberStyles.None, CultureInfo.InvariantCulture, out var maximumObjectBytes) ||
        maximumObjectBytes < 0)
    {
        throw new InvalidOperationException("OPTISTORAGE_MAX_OBJECT_BYTES must be a zero or positive integer.");
    }

    return maximumObjectBytes;
}

static int ReadNonNegativeLimit(string settingName, int defaultValue)
{
    var configuredValue = Environment.GetEnvironmentVariable(settingName);
    if (configuredValue is null)
    {
        return defaultValue;
    }

    if (!int.TryParse(configuredValue, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit < 0)
    {
        throw new InvalidOperationException($"{settingName} must be a zero or positive integer.");
    }

    return limit;
}
