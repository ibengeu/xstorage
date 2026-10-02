using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Net;
using Microsoft.AspNetCore.Http.Features;
using OptiStorage.Contracts;

namespace OptiStorage.Server;

internal static class S3Endpoints
{
    private static readonly XNamespace S3Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly FrozenSet<string> SupportedListOptions = FrozenSet.Create(
        StringComparer.Ordinal,
        "list-type", "prefix", "delimiter", "start-after", "continuation-token", "max-keys", "encoding-type");

    public static void Map(WebApplication app, IObjectStorage storage, StorageServiceSettings settings)
    {
        app.Use(async (context, next) =>
        {
            var requestId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            context.Items["RequestId"] = requestId;
            context.Response.Headers["x-amz-request-id"] = requestId;
            await next(context);
        });

        app.Map("/{**path}", async context => await HandleAsync(context, storage, settings));
    }

    private static async Task HandleAsync(HttpContext context, IObjectStorage storage, StorageServiceSettings settings)
    {
        var requestId = (string)context.Items["RequestId"]!;
        try
        {
            if (RequiresHttps(settings.DataPlaneUrl) && !context.Request.IsHttps)
            {
                // OWASP A02:2025 Security Misconfiguration. Require trusted HTTPS for externally bound S3 listeners to block plaintext credential replay.
                throw Error("AccessDenied", "HTTPS is required for this listener.", 403);
            }

            var verified = await SigV4Verifier.VerifyAsync(
                context,
                storage,
                settings.TimeProviderOverride ?? TimeProvider.System,
                context.RequestAborted);
            await DispatchAsync(context, storage, settings, verified);
        }
        catch (ObjectStorageException exception)
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OptiStorage.S3");
            // OWASP A08:2025 Security Logging and Monitoring Failures. Record safe error codes and request IDs without payloads or credentials.
            logger.LogWarning("S3 request {RequestId} failed with code {ErrorCode}.", requestId, exception.Code);
            await WriteErrorAsync(context, requestId, exception.Code, exception.Message, exception.StatusCode);
        }
        catch (BadHttpRequestException)
        {
            await WriteErrorAsync(context, requestId, "InvalidArgument", "The request is invalid.", 400);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client closed the connection. The server cannot send a response.
        }
        catch (Exception exception)
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OptiStorage.S3");
            logger.LogError(exception, "S3 request {RequestId} failed with an internal error.", requestId);
            await WriteErrorAsync(context, requestId, "InternalError", "The request failed.", 500);
        }
    }

    private static async Task DispatchAsync(
        HttpContext context,
        IObjectStorage storage,
        StorageServiceSettings settings,
        VerifiedRequest verified)
    {
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path + context.Request.QueryString;
        var (rawPath, rawQuery) = SplitTarget(rawTarget);
        var (bucket, key) = DecodeResourcePath(rawPath);
        if (bucket.Length == 0)
        {
            throw Error("InvalidBucketName", "The bucket name is invalid.", 400);
        }

        var method = context.Request.Method;
        // AWS SDK for .NET sends path-style CreateBucket requests with a trailing slash.
        var createBucket = method == HttpMethods.Put && (key is null || key.Length == 0);
        var listRequest = method == HttpMethods.Get && (key is null || key.Length == 0);
        if (createBucket)
        {
            await storage.AuthorizeBucketCreationAsync(verified.AccessKeyId, bucket, context.RequestAborted);
        }
        else
        {
            await storage.AuthorizeBucketAsync(verified.AccessKeyId, bucket, context.RequestAborted);
        }

        RejectUnsupportedFeatures(context);
        if (rawQuery.Length > 0 && !listRequest)
        {
            throw Error("NotImplemented", "Query options are not supported for this operation.", 501);
        }

        switch (method)
        {
            case "PUT" when createBucket:
                await CreateBucketAsync(context, storage, verified, bucket);
                return;
            case "PUT" when key is { Length: > 0 }:
                await PutObjectAsync(context, storage, settings, verified, bucket, key);
                return;
            case "GET" when key is { Length: > 0 }:
                await GetObjectAsync(context, storage, verified, bucket, key);
                return;
            case "HEAD" when key is { Length: > 0 }:
                await HeadObjectAsync(context, storage, verified, bucket, key);
                return;
            case "DELETE" when key is not null:
                await RequireEmptyRequestBodyAsync(context, verified);
                await storage.DeleteObjectAsync(verified.AccessKeyId, bucket, key, context.RequestAborted);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            case "GET" when listRequest:
                await ListObjectsAsync(context, storage, verified, bucket);
                return;
            default:
                throw Error("NotImplemented", "The requested operation is not supported.", 501);
        }
    }

    private static async Task CreateBucketAsync(HttpContext context, IObjectStorage storage, VerifiedRequest verified, string bucket)
    {
        var body = await ReadLimitedBodyAsync(context, 8192);
        VerifyBodyHash(body, verified.PayloadHash);
        if (body.Length > 0 && !HasSupportedLocationConstraint(body))
        {
            throw Error("InvalidLocationConstraint", "Only the us-east-1 location constraint is supported.", 400);
        }

        await storage.CreateBucketAsync(verified.AccessKeyId, bucket, context.RequestAborted);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.Location = "/" + bucket;
    }

    private static async Task PutObjectAsync(
        HttpContext context,
        IObjectStorage storage,
        StorageServiceSettings settings,
        VerifiedRequest verified,
        string bucket,
        string key)
    {
        if (context.Request.ContentLength is > 0 && context.Request.ContentLength > settings.MaximumObjectBytes)
        {
            throw Error("EntityTooLarge", "The object exceeds the configured size limit.", 413);
        }

        var contentType = context.Request.Headers.ContentType.FirstOrDefault() ?? "application/octet-stream";
        var metadata = await storage.PutObjectAsync(
            verified.AccessKeyId,
            bucket,
            key,
            new ObjectWriteRequest(
                context.Request.Body,
                context.Request.ContentLength,
                contentType,
                context.Request.Headers.ContentMD5.FirstOrDefault(),
                verified.PayloadHash == "UNSIGNED-PAYLOAD" ? null : verified.PayloadHash),
            context.RequestAborted);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.ETag = metadata.ETag;
    }

    private static async Task GetObjectAsync(HttpContext context, IObjectStorage storage, VerifiedRequest verified, string bucket, string key)
    {
        await RequireEmptyRequestBodyAsync(context, verified);
        await using var stored = await storage.GetObjectAsync(verified.AccessKeyId, bucket, key, context.RequestAborted);
        SetObjectHeaders(context, stored.Metadata);
        context.Response.StatusCode = StatusCodes.Status200OK;
        await stored.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static async Task HeadObjectAsync(HttpContext context, IObjectStorage storage, VerifiedRequest verified, string bucket, string key)
    {
        await RequireEmptyRequestBodyAsync(context, verified);
        var metadata = await storage.HeadObjectAsync(verified.AccessKeyId, bucket, key, context.RequestAborted);
        SetObjectHeaders(context, metadata);
        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    private static async Task ListObjectsAsync(HttpContext context, IObjectStorage storage, VerifiedRequest verified, string bucket)
    {
        await RequireEmptyRequestBodyAsync(context, verified);
        var query = ParseQuery(context);
        if (!query.TryGetValue("list-type", out var listType) || listType != "2")
        {
            throw Error("NotImplemented", "Only ListObjectsV2 is supported.", 501);
        }

        if (query.Keys.Any(name => !SupportedListOptions.Contains(name)))
        {
            throw Error("NotImplemented", "The requested list option is not supported.", 501);
        }

        var maxKeys = 1000;
        if (query.TryGetValue("max-keys", out var maxKeysText) &&
            (!int.TryParse(maxKeysText, NumberStyles.None, CultureInfo.InvariantCulture, out maxKeys) || maxKeys is < 0 or > 1000))
        {
            throw Error("InvalidArgument", "max-keys must be between 0 and 1000.", 400);
        }

        if (query.TryGetValue("encoding-type", out var encoding) && encoding != "url")
        {
            throw Error("InvalidArgument", "Only encoding-type=url is supported.", 400);
        }

        var page = await storage.ListObjectsV2Async(
            verified.AccessKeyId,
            bucket,
            new ObjectListingRequest(
                query.GetValueOrDefault("prefix"),
                query.GetValueOrDefault("delimiter"),
                query.GetValueOrDefault("start-after"),
                query.GetValueOrDefault("continuation-token"),
                maxKeys,
                encoding),
            context.RequestAborted);

        var xml = BuildListResult(page);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/xml; charset=utf-8";
        await context.Response.WriteAsync(xml.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, context.RequestAborted);
    }

    private static void RejectUnsupportedFeatures(HttpContext context)
    {
        var method = context.Request.Method;
        if (context.Request.Headers.ContainsKey("Range") ||
            context.Request.Headers.Keys.Any(name => name.StartsWith("If-", StringComparison.OrdinalIgnoreCase)))
        {
            throw Error("NotImplemented", "Range and conditional requests are not supported.", 501);
        }

        if (context.Request.Query.Keys.Any(name => name.StartsWith("response-", StringComparison.Ordinal)))
        {
            throw Error("NotImplemented", "Response override options are not supported.", 501);
        }

        foreach (var headerName in context.Request.Headers.Keys)
        {
            if (headerName.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase) &&
                !headerName.Equals("x-amz-date", StringComparison.OrdinalIgnoreCase) &&
                !headerName.Equals("x-amz-content-sha256", StringComparison.OrdinalIgnoreCase) &&
                !headerName.Equals("x-amz-api-version", StringComparison.OrdinalIgnoreCase))
            {
                throw Error("NotImplemented", "The requested x-amz feature is not supported.", 501);
            }
        }

        if (method != HttpMethods.Put && context.Request.Headers.ContainsKey("Content-MD5"))
        {
            throw Error("NotImplemented", "Content-MD5 is supported only for PUT.", 501);
        }
    }

    private static async Task RequireEmptyRequestBodyAsync(HttpContext context, VerifiedRequest verified)
    {
        var body = await ReadLimitedBodyAsync(context, 1);
        if (body.Length != 0)
        {
            throw Error("NotImplemented", "This operation does not accept a request body.", 501);
        }

        VerifyBodyHash(body, verified.PayloadHash);
    }

    private static async Task<byte[]> ReadLimitedBodyAsync(HttpContext context, int maximumBytes)
    {
        if (context.Request.ContentLength is > 0 && context.Request.ContentLength > maximumBytes)
        {
            throw Error("InvalidArgument", "The request body is too large.", 400);
        }

        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maximumBytes)
            {
                throw Error("InvalidArgument", "The request body is too large.", 400);
            }

            output.Write(buffer, 0, read);
        }
    }

    private static void VerifyBodyHash(byte[] body, string payloadHash)
    {
        if (payloadHash == "UNSIGNED-PAYLOAD")
        {
            return;
        }

        var actual = SHA256.HashData(body);
        var expected = Convert.FromHexString(payloadHash);
        // OWASP A05:2025 Cryptographic Failures. Compare signed body hashes in constant time before a bucket change is published.
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw Error("SignatureDoesNotMatch", "The request payload hash does not match.", 403);
        }
    }

    private static bool HasSupportedLocationConstraint(byte[] body)
    {
        try
        {
            using var stream = new MemoryStream(body, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 8192
            });
            var document = XDocument.Load(reader);
            return document.Root?.Name.LocalName == "CreateBucketConfiguration" &&
                   document.Root.Elements().SingleOrDefault()?.Name.LocalName == "LocationConstraint" &&
                   document.Root.Elements().Single().Value == "us-east-1";
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            return false;
        }
    }

    private static XElement BuildListResult(ObjectListingPage page)
    {
        var root = new XElement(S3Namespace + "ListBucketResult",
            new XElement(S3Namespace + "Name", page.Bucket),
            OptionalElement("Prefix", page.Prefix, page.EncodingType),
            new XElement(S3Namespace + "KeyCount", page.KeyCount),
            new XElement(S3Namespace + "MaxKeys", page.MaxKeys),
            new XElement(S3Namespace + "IsTruncated", page.IsTruncated));
        AddOptional(root, "StartAfter", page.StartAfter, page.EncodingType);
        AddOptional(root, "ContinuationToken", page.ContinuationToken, null);
        AddOptional(root, "NextContinuationToken", page.NextContinuationToken, null);
        AddOptional(root, "Delimiter", page.Delimiter, page.EncodingType);
        AddOptional(root, "EncodingType", page.EncodingType, null);

        foreach (var item in page.Contents)
        {
            root.Add(new XElement(S3Namespace + "Contents",
                OptionalElement("Key", item.Key, page.EncodingType),
                new XElement(S3Namespace + "LastModified", item.LastModified.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)),
                new XElement(S3Namespace + "ETag", item.ETag),
                new XElement(S3Namespace + "Size", item.Size),
                new XElement(S3Namespace + "StorageClass", item.StorageClass)));
        }

        foreach (var prefix in page.CommonPrefixes)
        {
            root.Add(new XElement(S3Namespace + "CommonPrefixes", OptionalElement("Prefix", prefix, page.EncodingType)));
        }

        return root;
    }

    private static XElement? OptionalElement(string name, string? value, string? encodingType)
    {
        if (value is null)
        {
            return null;
        }

        var content = encodingType == "url" ? Uri.EscapeDataString(value) : value;
        return new XElement(S3Namespace + name, content);
    }

    private static void AddOptional(XElement root, string name, string? value, string? encodingType)
    {
        var element = OptionalElement(name, value, encodingType);
        if (element is not null)
        {
            root.Add(element);
        }
    }

    private static void SetObjectHeaders(HttpContext context, ObjectMetadata metadata)
    {
        context.Response.ContentLength = metadata.Size;
        context.Response.ContentType = metadata.ContentType;
        context.Response.Headers.ETag = metadata.ETag;
        context.Response.Headers.LastModified = metadata.LastModified.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
    }

    private static (string Bucket, string? Key) DecodeResourcePath(string rawPath)
    {
        var remainder = rawPath[1..];
        var separator = remainder.IndexOf('/');
        if (separator < 0)
        {
            return (DecodePathPart(remainder), null);
        }

        return (DecodePathPart(remainder[..separator]), DecodePathPart(remainder[(separator + 1)..]));
    }

    private static string DecodePathPart(string value)
    {
        var bytes = new List<byte>(value.Length);
        Span<byte> encoded = stackalloc byte[4];
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '%')
            {
                if (index + 2 >= value.Length || !byte.TryParse(value.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var escaped))
                {
                    throw Error("InvalidArgument", "The request path has an invalid escape.", 400);
                }

                bytes.Add(escaped);
                index += 2;
                continue;
            }

            if (Rune.DecodeFromUtf16(value.AsSpan(index), out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                throw Error("InvalidArgument", "The request path is invalid.", 400);
            }

            var written = rune.EncodeToUtf8(encoded);
            for (var byteIndex = 0; byteIndex < written; byteIndex++)
            {
                bytes.Add(encoded[byteIndex]);
            }

            index += consumed - 1;
        }

        try
        {
            return StrictUtf8.GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException)
        {
            throw Error("InvalidArgument", "The request path is not valid UTF-8.", 400);
        }
    }

    private static Dictionary<string, string> ParseQuery(HttpContext context)
    {
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path + context.Request.QueryString;
        var (_, rawQuery) = SplitTarget(rawTarget);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = DecodePathPart(separator < 0 ? pair : pair[..separator]);
            var value = DecodePathPart(separator < 0 ? string.Empty : pair[(separator + 1)..]);
            if (!result.TryAdd(name, value))
            {
                throw Error("InvalidArgument", "A query parameter is duplicated.", 400);
            }
        }

        return result;
    }

    private static (string Path, string Query) SplitTarget(string target)
    {
        var question = target.IndexOf('?');
        return (question < 0 ? target : target[..question], question < 0 ? string.Empty : target[(question + 1)..]);
    }

    private static void RejectPathForBodylessMethods(HttpContext context)
    {
        _ = context;
    }

    private static async Task WriteErrorAsync(HttpContext context, string requestId, string code, string message, int statusCode)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        context.Response.StatusCode = statusCode;
        context.Response.Headers["x-amz-request-id"] = requestId;
        if (HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.ContentLength = 0;
            return;
        }

        var resource = context.Request.Path.Value ?? "/";
        var xml = new XElement("Error",
            new XElement("Code", code),
            new XElement("Message", message),
            new XElement("Resource", resource),
            new XElement("RequestId", requestId));
        context.Response.ContentType = "application/xml; charset=utf-8";
        await context.Response.WriteAsync(xml.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, context.RequestAborted);
    }

    private static ObjectStorageException Error(string code, string message, int statusCode) => new(code, message, statusCode);

    private static bool RequiresHttps(string address)
    {
        var host = new Uri(address, UriKind.Absolute).Host;
        return !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) &&
               (!IPAddress.TryParse(host, out var ipAddress) || !IPAddress.IsLoopback(ipAddress));
    }
}
