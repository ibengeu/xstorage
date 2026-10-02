using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Buffers;
using Microsoft.AspNetCore.Http.Features;
using XStorage.Contracts;

namespace XStorage.Server;

internal sealed record VerifiedRequest(string AccessKeyId, string PayloadHash);

internal static class SigV4Verifier
{
    private const string Algorithm = "AWS4-HMAC-SHA256";
    private const string Terminator = "aws4_request";
    private const string Region = "us-east-1";
    private const string Service = "s3";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private sealed record CredentialScope(
        string AccessKeyId,
        string Date,
        string Region,
        string Service,
        string Terminator);

    public static async Task<VerifiedRequest> VerifyAsync(
        HttpContext context,
        IObjectStorage storage,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        var fields = ParseAuthorization(authorization);
        var credential = ParseCredentialScope(fields["Credential"]);
        var signingKey = await FindSigningKeyAsync(storage, credential.AccessKeyId, cancellationToken);
        var timestamp = ValidateTimestamp(context, credential.Date, timeProvider);
        var payloadHash = GetPayloadHash(context);
        RequireSecureUnsignedPayload(context, payloadHash);
        var signedHeaderNames = ParseSignedHeaders(fields["SignedHeaders"]);
        ValidateSignedHeaders(context, signedHeaderNames);
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ??
                        context.Request.Path + context.Request.QueryString;
        var (rawPath, rawQuery) = SplitTarget(rawTarget);
        var canonicalHeaders = BuildCanonicalHeaders(context, signedHeaderNames);
        var canonicalRequest =
            CreateCanonicalRequest(context, rawPath, rawQuery, canonicalHeaders, signedHeaderNames, payloadHash);
        VerifySignature(fields["Signature"], signingKey.SecretAccessKey, credential, timestamp, canonicalRequest);
        // OWASP A07:2025 Identification and Authentication Failures.
        // Confirm the key version after HMAC verification to prevent a rotated key snapshot from authenticating late.
        if (!await storage.ConfirmSigningKeyAsync(
                credential.AccessKeyId,
                signingKey.CredentialVersion,
                cancellationToken))
        {
            throw Error("SignatureDoesNotMatch", "The signing credential is no longer active.", 403);
        }

        return new VerifiedRequest(credential.AccessKeyId, payloadHash);
    }

    private static CredentialScope ParseCredentialScope(string credentialText)
    {
        var values = credentialText.Split('/');
        if (values.Length != 5 || values[1].Length != 8 || values[2] != Region || values[3] != Service ||
            values[4] != Terminator)
        {
            throw Error("AuthorizationHeaderMalformed", "The authorization scope is invalid.", 400);
        }

        return new CredentialScope(values[0], values[1], values[2], values[3], values[4]);
    }

    private static async Task<ApplicationSigningKey> FindSigningKeyAsync(
        IObjectStorage storage,
        string accessKeyId,
        CancellationToken cancellationToken)
    {
        var signingKey = await storage.FindSigningKeyAsync(accessKeyId, cancellationToken);
        if (signingKey is null)
        {
            throw Error("InvalidAccessKeyId", "The access key ID is not valid.", 403);
        }

        if (!signingKey.Active)
        {
            throw Error("AccessDenied", "The application is inactive.", 403);
        }

        return signingKey;
    }

    private static string ValidateTimestamp(HttpContext context, string credentialDate, TimeProvider timeProvider)
    {
        var timestamp = context.Request.Headers["x-amz-date"].ToString();
        if (!DateTimeOffset.TryParseExact(timestamp, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var requestTime) ||
            !timestamp.StartsWith(credentialDate, StringComparison.Ordinal))
        {
            throw Error("RequestTimeTooSkewed", "The request date is invalid.", 403);
        }

        if ((timeProvider.GetUtcNow() - requestTime).Duration() > TimeSpan.FromMinutes(15))
        {
            throw Error("RequestTimeTooSkewed", "The request is outside the allowed time window.", 403);
        }

        return timestamp;
    }

    private static string GetPayloadHash(HttpContext context)
    {
        var payloadHash = context.Request.Headers["x-amz-content-sha256"].ToString();
        if (!IsPayloadHash(payloadHash))
        {
            throw Error("InvalidArgument", "The payload hash is invalid.", 400);
        }

        return payloadHash;
    }

    private static void RequireSecureUnsignedPayload(HttpContext context, string payloadHash)
    {
        // OWASP A05:2025 Cryptographic Failures. Require HTTPS when payload bytes lack a signed digest to prevent transit tampering.
        if (payloadHash == "UNSIGNED-PAYLOAD" && !context.Request.IsHttps)
        {
            throw Error("AccessDenied", "Unsigned payloads require HTTPS.", 403);
        }
    }

    private static void ValidateSignedHeaders(HttpContext context, IReadOnlyCollection<string> headerNames)
    {
        // OWASP A06:2025 Identification and Authentication Failures. Require signed host and date headers to prevent request-scope substitution.
        if (!headerNames.Contains("host", StringComparer.Ordinal) ||
            !headerNames.Contains("x-amz-date", StringComparer.Ordinal))
        {
            throw Error("AccessDenied", "The request must sign host and x-amz-date.", 403);
        }

        foreach (var headerName in headerNames)
        {
            if (headerName != "host" && !context.Request.Headers.ContainsKey(headerName))
            {
                throw Error("SignatureDoesNotMatch", "A signed header is missing.", 403);
            }
        }
    }

    private static string CreateCanonicalRequest(
        HttpContext context,
        string rawPath,
        string rawQuery,
        string canonicalHeaders,
        IReadOnlyList<string> signedHeaderNames,
        string payloadHash)
    {
        return string.Join('\n',
            context.Request.Method.ToUpperInvariant(),
            CanonicalizePath(rawPath),
            CanonicalizeQuery(rawQuery),
            canonicalHeaders,
            string.Join(';', signedHeaderNames),
            payloadHash);
    }

    private static void VerifySignature(
        string suppliedSignature,
        string secret,
        CredentialScope credential,
        string timestamp,
        string canonicalRequest)
    {
        var canonicalHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))
            .ToLowerInvariant();
        var scope = string.Join('/', credential.Date, credential.Region, credential.Service, credential.Terminator);
        var stringToSign = string.Join('\n', Algorithm, timestamp, scope, canonicalHash);
        var expectedSignature = Sign(secret, credential.Date, credential.Region, credential.Service, stringToSign);
        // OWASP A06:2025 Identification and Authentication Failures. Compare signatures in constant time to reduce timing-based forgery risk.
        if (suppliedSignature.Length != expectedSignature.Length ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(suppliedSignature),
                Encoding.ASCII.GetBytes(expectedSignature)))
        {
            throw Error("SignatureDoesNotMatch", "The request signature does not match.", 403);
        }
    }

    private static Dictionary<string, string> ParseAuthorization(string authorization)
    {
        const string prefix = Algorithm + " ";
        if (!authorization.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw Error("AccessDenied", "A valid SigV4 Authorization header is required.", 403);
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in authorization[prefix.Length..].Split(','))
        {
            var separator = segment.IndexOf('=');
            if (separator < 1 || !fields.TryAdd(segment[..separator].Trim(), segment[(separator + 1)..].Trim()))
            {
                throw Error("AuthorizationHeaderMalformed", "The authorization header is invalid.", 400);
            }
        }

        if (!fields.ContainsKey("Credential") || !fields.ContainsKey("SignedHeaders") ||
            !fields.ContainsKey("Signature") || fields.Count != 3)
        {
            throw Error("AuthorizationHeaderMalformed", "The authorization header is incomplete.", 400);
        }

        return fields;
    }

    private static string[] ParseSignedHeaders(string value)
    {
        var names = value.Split(';');
        for (var index = 0; index < names.Length; index++)
        {
            var name = names[index];
            // A strictly increasing ordinal sequence is both sorted and duplicate-free, so one adjacent comparison checks both.
            if (name.Length == 0 || name != name.ToLowerInvariant() ||
                index > 0 && string.CompareOrdinal(names[index - 1], name) >= 0)
            {
                throw Error("AuthorizationHeaderMalformed", "The signed header list is invalid.", 400);
            }
        }

        return names;
    }

    private static string BuildCanonicalHeaders(HttpContext context, IReadOnlyList<string> signedHeaderNames)
    {
        var builder = new StringBuilder();
        foreach (var name in signedHeaderNames)
        {
            var value = name == "host"
                ? context.Request.Host.Value
                : string.Join(',',
                    context.Request.Headers[name].Where(value => value is not null)
                        .Select(value => NormalizeHeaderValue(value!)));
            builder.Append(name).Append(':').Append(value).Append('\n');
        }

        return builder.ToString();
    }

    private static string NormalizeHeaderValue(string value)
    {
        var output = new StringBuilder(value.Length);
        var hasContent = false;
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (character is ' ' or '\t')
            {
                pendingSpace = hasContent;
                continue;
            }

            if (pendingSpace)
            {
                output.Append(' ');
                pendingSpace = false;
            }

            output.Append(character);
            hasContent = true;
        }

        return output.ToString();
    }

    private static (string Path, string Query) SplitTarget(string target)
    {
        var question = target.IndexOf('?');
        var path = question < 0 ? target : target[..question];
        if (path.Length == 0 || path[0] != '/')
        {
            throw Error("InvalidArgument", "The request target is invalid.", 400);
        }

        return (path, question < 0 ? string.Empty : target[(question + 1)..]);
    }

    private static string CanonicalizePath(string path)
    {
        var result = new StringBuilder(path.Length);
        for (var index = 0; index < path.Length; index++)
        {
            if (path[index] == '%')
            {
                if (index + 2 >= path.Length || !byte.TryParse(path.AsSpan(index + 1, 2), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out var decoded))
                {
                    throw Error("InvalidArgument", "The request path has an invalid escape.", 400);
                }

                result.Append('%').Append(decoded.ToString("X2", CultureInfo.InvariantCulture));
                index += 2;
            }
            else if (path[index] <= 0x7f && IsPathLiteral(path[index]))
            {
                result.Append(path[index]);
            }
            else
            {
                foreach (var value in Encoding.UTF8.GetBytes(path[index].ToString()))
                {
                    result.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));
                }
            }
        }

        return result.ToString();
    }

    private static string CanonicalizeQuery(string rawQuery)
    {
        if (rawQuery.Length == 0)
        {
            return string.Empty;
        }

        var pairs = new List<(string Key, string Value)>();
        foreach (var part in rawQuery.Split('&'))
        {
            var separator = part.IndexOf('=');
            var key = separator < 0 ? part : part[..separator];
            var value = separator < 0 ? string.Empty : part[(separator + 1)..];
            pairs.Add((AwsEncode(DecodeQueryPart(key)), AwsEncode(DecodeQueryPart(value))));
        }

        return string.Join('&', pairs.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + pair.Value));
    }

    private static string DecodeQueryPart(string value)
    {
        var bytes = new List<byte>(value.Length);
        Span<byte> encoded = stackalloc byte[4];
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '%')
            {
                if (index + 2 >= value.Length || !byte.TryParse(value.AsSpan(index + 1, 2), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out var escaped))
                {
                    throw Error("InvalidArgument", "The request query is invalid.", 400);
                }

                bytes.Add(escaped);
                index += 2;
                continue;
            }

            var status = Rune.DecodeFromUtf16(value.AsSpan(index), out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                throw Error("InvalidArgument", "The request query is invalid.", 400);
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
            throw Error("InvalidArgument", "The request query is invalid.", 400);
        }
    }

    private static string AwsEncode(string value)
    {
        var output = new StringBuilder();
        foreach (var valueByte in Encoding.UTF8.GetBytes(value))
        {
            if (valueByte is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
            {
                output.Append((char)valueByte);
            }
            else
            {
                output.Append('%').Append(valueByte.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return output.ToString();
    }

    private static bool IsPathLiteral(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '/' or '-' or '_' or '.' or '~';

    private static bool IsPayloadHash(string value) =>
        string.Equals(value, "UNSIGNED-PAYLOAD", StringComparison.Ordinal) ||
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Sign(string secret, string date, string region, string service, string stringToSign)
    {
        // OWASP A05:2025 Cryptographic Failures. Derive the scoped AWS4 HMAC key to prevent one signing context from authorizing another.
        var initialKey = Encoding.UTF8.GetBytes("AWS4" + secret);
        byte[] dateKey;
        try
        {
            dateKey = Hmac(initialKey, date);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(initialKey);
        }

        var regionKey = Hmac(dateKey, region);
        var serviceKey = Hmac(regionKey, service);
        var signingKey = Hmac(serviceKey, Terminator);
        try
        {
            return Convert.ToHexString(Hmac(signingKey, stringToSign)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dateKey);
            CryptographicOperations.ZeroMemory(regionKey);
            CryptographicOperations.ZeroMemory(serviceKey);
            CryptographicOperations.ZeroMemory(signingKey);
        }
    }

    private static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    private static ObjectStorageException Error(string code, string message, int status) => new(code, message, status);
}