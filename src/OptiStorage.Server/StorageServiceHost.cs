using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Net;
using OptiStorage.Contracts;
using OptiStorage.Storage;

namespace OptiStorage.Server;

/// <summary>Runs separate S3 and operator-only HTTP listeners over one storage engine.</summary>
public sealed class StorageServiceHost : IAsyncDisposable
{
    private readonly WebApplication _dataPlane;
    private readonly WebApplication _adminPlane;

    private StorageServiceHost(
        WebApplication dataPlane,
        WebApplication adminPlane,
        IObjectStorage storage,
        Uri dataAddress,
        Uri adminAddress)
    {
        _dataPlane = dataPlane;
        _adminPlane = adminPlane;
        Storage = storage;
        DataAddress = dataAddress;
        AdminAddress = adminAddress;
    }

    /// <summary>Gets the shared storage contract for diagnostics and hosted integrations.</summary>
    public IObjectStorage Storage { get; }

    /// <summary>Gets the bound S3 listener address.</summary>
    public Uri DataAddress { get; }

    /// <summary>Gets the bound administrative listener address.</summary>
    public Uri AdminAddress { get; }

    /// <summary>Starts both listeners and loads durable storage state.</summary>
    public static async Task<StorageServiceHost> StartAsync(
        StorageServiceSettings settings,
        CancellationToken cancellationToken = default)
    {
        ValidateSettings(settings);
        var storage = await ObjectStorage.OpenAsync(new ObjectStorageOptions(
            settings.DataRoot,
            settings.MaximumObjectBytes,
            settings.ApplicationSecretEncryptionKey,
            settings.ContinuationTokenKey,
            settings.MaximumObjectsPerApplication,
            settings.MaximumBucketsPerApplication), cancellationToken);
        return await StartListenersAsync(settings, storage, cancellationToken);
    }

    internal static Task<StorageServiceHost> StartWithStorageForTestsAsync(
        StorageServiceSettings settings,
        IObjectStorage storage,
        CancellationToken cancellationToken = default)
    {
        ValidateSettings(settings);
        ArgumentNullException.ThrowIfNull(storage);
        return StartListenersAsync(settings, storage, cancellationToken);
    }

    private static async Task<StorageServiceHost> StartListenersAsync(
        StorageServiceSettings settings,
        IObjectStorage storage,
        CancellationToken cancellationToken)
    {
        WebApplication? dataPlane = null;
        WebApplication? adminPlane = null;
        try
        {
            dataPlane = CreateApplication(settings.DataPlaneUrl, settings.TrustedProxyAddresses);
            S3Endpoints.Map(dataPlane, storage, settings);
            await dataPlane.StartAsync(cancellationToken);

            adminPlane = CreateApplication(settings.AdminUrl, settings.TrustedProxyAddresses);
            AdminEndpoints.Map(adminPlane, storage, settings);
            await adminPlane.StartAsync(cancellationToken);

            return new StorageServiceHost(
                dataPlane,
                adminPlane,
                storage,
                GetAddress(dataPlane),
                GetAddress(adminPlane));
        }
        catch
        {
            if (adminPlane is not null)
            {
                await adminPlane.DisposeAsync();
            }

            if (dataPlane is not null)
            {
                await dataPlane.DisposeAsync();
            }

            await storage.DisposeAsync();
            throw;
        }
    }

    /// <summary>Stops both listeners and releases the data root.</summary>
    public async ValueTask DisposeAsync()
    {
        await _adminPlane.DisposeAsync();
        await _dataPlane.DisposeAsync();
        await Storage.DisposeAsync();
    }

    private static WebApplication CreateApplication(string url, IReadOnlyList<string>? trustedProxyAddresses)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ApplicationName = typeof(StorageServiceHost).Assembly.FullName
        });
        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(options =>
        {
            // OWASP A04:2025 Insecure Design. Enforce request-target, header, and timeout limits; object bodies have operation-specific streaming limits.
            options.Limits.MaxRequestBodySize = null;
            options.Limits.MaxRequestLineSize = 16_384;
            options.Limits.MaxRequestHeadersTotalSize = 32_768;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
        });
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // OWASP A02:2025 Security Misconfiguration. Trust forwarding headers only from configured proxy IPs to block client-spoofed HTTPS state.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var address in trustedProxyAddresses ?? [])
            {
                if (!IPAddress.TryParse(address, out var parsed))
                {
                    throw new ArgumentException("Trusted proxy addresses must be IP addresses.",
                        nameof(trustedProxyAddresses));
                }

                options.KnownProxies.Add(parsed);
            }
        });
        var app = builder.Build();
        app.UseForwardedHeaders();
        return app;
    }

    private static Uri GetAddress(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.SingleOrDefault()
                      ?? throw new InvalidOperationException("The listener did not publish one bound address.");
        return new Uri(address);
    }

    private static void ValidateSettings(StorageServiceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.DataRoot) || !Path.IsPathFullyQualified(settings.DataRoot))
        {
            throw new ArgumentException("DataRoot must be an absolute path.", nameof(settings));
        }

        if (string.IsNullOrEmpty(settings.AdminToken) || settings.AdminToken.Length < 32 ||
            settings.AdminToken.Any(character => character is < '!' or > '~'))
        {
            throw new ArgumentException(
                "AdminToken must contain at least 32 printable ASCII characters without spaces.", nameof(settings));
        }

        if (settings.ApplicationSecretEncryptionKey.Length != 32 || settings.ContinuationTokenKey.Length != 32)
        {
            throw new ArgumentException("Configured encryption and token keys must each contain 32 bytes.",
                nameof(settings));
        }

        if (settings.MaximumObjectBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "MaximumObjectBytes must be zero or greater.");
        }

        // OWASP A02:2025 Security Misconfiguration. Reject invalid limits that could disable per-application metadata bounds.
        if (settings.MaximumObjectsPerApplication < 0 || settings.MaximumBucketsPerApplication < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings),
                "Application object and bucket limits must be zero or greater.");
        }

        var dataUri = new Uri(settings.DataPlaneUrl, UriKind.Absolute);
        var adminUri = new Uri(settings.AdminUrl, UriKind.Absolute);
        // OWASP A02:2025 Security Misconfiguration. Keep operator and data-plane listeners on separate ports to prevent port overlap.
        if (dataUri.Port != 0 && dataUri.Port == adminUri.Port)
        {
            throw new ArgumentException("The data and admin listeners must use separate ports.", nameof(settings));
        }
    }
}