using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XStorage.Contracts;

namespace XStorage.Storage;

internal static class ContinuationTokenCodec
{
    private const int SignatureLength = 32;
    private const int MaximumTokenLength = 32 * 1024;

    public static string Create(
        byte[] key,
        string bucket,
        string? prefix,
        string? delimiter,
        string lastKey)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new TokenPayload(1, bucket, prefix, delimiter, lastKey));
        var signature = HMACSHA256.HashData(key, payload);
        var tokenBytes = new byte[payload.Length + signature.Length];
        payload.CopyTo(tokenBytes, 0);
        signature.CopyTo(tokenBytes, payload.Length);
        try
        {
            // OWASP A05:2025 Cryptographic Failures. Sign cursor scope and position to prevent forged page skips or cross-list replay.
            return Convert.ToBase64String(tokenBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }

    public static string Read(
        byte[] key,
        string token,
        string bucket,
        string? prefix,
        string? delimiter)
    {
        if (token.Length is < 1 or > MaximumTokenLength
            || token.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw InvalidToken();
        }

        byte[] tokenBytes;
        try
        {
            var standardBase64 = token.Replace('-', '+').Replace('_', '/');
            standardBase64 += (token.Length % 4) switch
            {
                0 => string.Empty,
                2 => "==",
                3 => "=",
                _ => throw InvalidToken()
            };
            tokenBytes = Convert.FromBase64String(standardBase64);
        }
        catch (FormatException)
        {
            throw InvalidToken();
        }

        try
        {
            if (tokenBytes.Length <= SignatureLength)
            {
                throw InvalidToken();
            }

            var payload = tokenBytes.AsSpan(0, tokenBytes.Length - SignatureLength);
            var suppliedSignature = tokenBytes.AsSpan(tokenBytes.Length - SignatureLength);
            var expectedSignature = HMACSHA256.HashData(key, payload);
            try
            {
                // OWASP A05:2025 Cryptographic Failures. Constant-time verification prevents timing leakage while validating token integrity.
                if (!CryptographicOperations.FixedTimeEquals(expectedSignature, suppliedSignature))
                {
                    throw InvalidToken();
                }

                var claims = JsonSerializer.Deserialize<TokenPayload>(payload);
                if (claims is null
                    || claims.Version != 1
                    || !StringComparer.Ordinal.Equals(claims.Bucket, bucket)
                    || !StringComparer.Ordinal.Equals(claims.Prefix, prefix)
                    || !StringComparer.Ordinal.Equals(claims.Delimiter, delimiter)
                    || string.IsNullOrEmpty(claims.LastKey)
                    || Encoding.UTF8.GetByteCount(claims.LastKey) > 1024)
                {
                    throw InvalidToken();
                }

                return claims.LastKey;
            }
            catch (JsonException)
            {
                throw InvalidToken();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedSignature);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }

    private static ObjectStorageException InvalidToken() =>
        new("InvalidArgument", "The continuation token is invalid for this listing.", 400);

    private sealed record TokenPayload(int Version, string Bucket, string? Prefix, string? Delimiter, string LastKey);
}
