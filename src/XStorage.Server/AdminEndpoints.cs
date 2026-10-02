using System.Security.Cryptography;
using System.Text;
using System.Net;
using XStorage.Contracts;

namespace XStorage.Server;

internal static class AdminEndpoints
{
    public static void Map(WebApplication app, IObjectStorage storage, StorageServiceSettings settings)
    {
        var requiresHttps = RequiresHttps(settings.AdminUrl);
        app.Use(async (context, next) =>
        {
            if (!HasValidBearer(context.Request.Headers.Authorization, settings.AdminToken))
            {
                // OWASP A01:2025 Broken Access Control. Authenticate before route or JSON handling to block unauthenticated administration.
                // OWASP A08:2025 Security Logging and Monitoring Failures. Record bearer failures without token values so operators can detect unauthorized access.
                app.Logger.LogWarning("Admin API request rejected because bearer authentication failed.");
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (requiresHttps && !context.Request.IsHttps)
            {
                // OWASP A02:2025 Security Misconfiguration. Require trusted HTTPS for externally bound admin listeners to protect the bearer token in transit.
                app.Logger.LogWarning("Admin API request rejected because HTTPS is required for this listener.");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
        });

        app.MapPost("/admin/applications",
            async (CreateApplicationRequest request, CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128 || request.QuotaBytes < 0)
                {
                    return Results.BadRequest(new { error = "Name and quotaBytes are invalid." });
                }

                try
                {
                    var credentials =
                        await storage.CreateApplicationAsync(request.Name, request.QuotaBytes, cancellationToken);
                    // OWASP A08:2025 Security Logging and Monitoring Failures. Audit credential and quota changes without logging secret values.
                    app.Logger.LogInformation("Created storage application {AccessKeyId}.", credentials.AccessKeyId);
                    return Results.Json(credentials, statusCode: StatusCodes.Status201Created);
                }
                catch (ObjectStorageException exception)
                {
                    return WriteAdminError(app.Logger, exception);
                }
            });

        app.MapPost("/admin/applications/{accessKeyId}/rotate",
            async (string accessKeyId, CancellationToken cancellationToken) =>
            {
                try
                {
                    var credentials = await storage.RotateCredentialsAsync(accessKeyId, cancellationToken);
                    app.Logger.LogInformation("Rotated credentials for storage application {AccessKeyId}.",
                        accessKeyId);
                    return Results.Json(new { credentials.AccessKeyId, credentials.SecretAccessKey });
                }
                catch (ObjectStorageException exception)
                {
                    return WriteAdminError(app.Logger, exception);
                }
            });

        app.MapPatch("/admin/applications/{accessKeyId}",
            async (string accessKeyId, UpdateQuotaRequest request, CancellationToken cancellationToken) =>
            {
                try
                {
                    await storage.UpdateQuotaAsync(accessKeyId, request.QuotaBytes, cancellationToken);
                    app.Logger.LogInformation(
                        "Updated quota for storage application {AccessKeyId} to {QuotaBytes} bytes.", accessKeyId,
                        request.QuotaBytes);
                    return Results.Json(new { accessKeyId, request.QuotaBytes });
                }
                catch (ObjectStorageException exception)
                {
                    return WriteAdminError(app.Logger, exception);
                }
            });

        app.MapDelete("/admin/applications/{accessKeyId}",
            async (string accessKeyId, CancellationToken cancellationToken) =>
            {
                try
                {
                    await storage.DeactivateApplicationAsync(accessKeyId, cancellationToken);
                    app.Logger.LogInformation("Deactivated storage application {AccessKeyId}.", accessKeyId);
                    return Results.NoContent();
                }
                catch (ObjectStorageException exception)
                {
                    return WriteAdminError(app.Logger, exception);
                }
            });

        app.MapGet("/admin/applications", async (CancellationToken cancellationToken) =>
            Results.Ok(await storage.ListApplicationsAsync(cancellationToken)));

        app.MapFallback(() => Results.NotFound());
    }

    private static IResult WriteAdminError(ILogger logger, ObjectStorageException exception)
    {
        // OWASP A08:2025 Security Logging and Monitoring Failures. Record administrative failures by safe code without credentials or bearer tokens.
        logger.LogWarning("Admin operation failed with code {ErrorCode}.", exception.Code);
        return Results.Json(new { error = exception.Code, message = exception.Message },
            statusCode: exception.StatusCode);
    }

    private static bool HasValidBearer(string? authorization, string expectedToken)
    {
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var supplied = Encoding.UTF8.GetBytes(authorization[prefix.Length..]);
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        try
        {
            return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
        }
        finally
        {
            // OWASP A05:2025 Cryptographic Failures. Clear temporary bearer-token buffers to reduce residual secret material in memory.
            CryptographicOperations.ZeroMemory(supplied);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static bool RequiresHttps(string address)
    {
        var host = new Uri(address, UriKind.Absolute).Host;
        return !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) &&
               (!IPAddress.TryParse(host, out var ipAddress) || !IPAddress.IsLoopback(ipAddress));
    }
}

internal sealed record CreateApplicationRequest(string Name, long QuotaBytes);

internal sealed record UpdateQuotaRequest(long QuotaBytes);