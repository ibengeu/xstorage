using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OptiStorage.Contracts;

namespace OptiStorage.Storage;

internal static class ObjectRecordCodec
{
    private static readonly byte[] Magic = "OSTO"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const ushort Version = 1;
    private const int FixedHeaderLength = 78;
    private const int Sha256Offset = 30;
    private const int Md5Offset = 62;
    private const int BodyBufferSize = 64 * 1024;

    public static async Task<ObjectMetadata> WriteAsync(
        FileStream destination,
        string key,
        ObjectWriteRequest request,
        long maximumObjectBytes,
        CancellationToken cancellationToken,
        Action<StorageFaultPoint>? faultInjector)
    {
        var keyBytes = EncodeKey(key);
        var contentTypeBytes = ValidateContentType(request.ContentType);
        ValidateDeclaredLength(request.ContentLength, maximumObjectBytes);
        var headerLength = checked(FixedHeaderLength + keyBytes.Length + contentTypeBytes.Length);
        var placeholder = CreateHeader(headerLength, keyBytes.Length, contentTypeBytes.Length, 0, 0, new byte[32], new byte[16]);
        await destination.WriteAsync(placeholder, cancellationToken);
        await destination.WriteAsync(keyBytes, cancellationToken);
        await destination.WriteAsync(contentTypeBytes, cancellationToken);
        faultInjector?.Invoke(StorageFaultPoint.AfterObjectHeaderPlaceholderWritten);

        var (size, sha256, md5) = await CopyAndHashAsync(
            request.Content,
            destination,
            maximumObjectBytes,
            cancellationToken);
        faultInjector?.Invoke(StorageFaultPoint.AfterObjectBodyCopied);
        VerifyDeclaredLength(request.ContentLength, size);
        VerifyOptionalHashes(request, sha256, md5);

        var modified = DateTimeOffset.UtcNow;
        var header = CreateHeader(headerLength, keyBytes.Length, contentTypeBytes.Length, size, modified.UtcDateTime.Ticks, sha256, md5);
        destination.Position = 0;
        await destination.WriteAsync(header, cancellationToken);
        destination.Position = destination.Length;
        faultInjector?.Invoke(StorageFaultPoint.AfterObjectRecordFinalized);
        CryptographicOperations.ZeroMemory(placeholder);
        return new ObjectMetadata(key, size, Encoding.ASCII.GetString(contentTypeBytes), FormatEtag(md5), modified);
    }

    public static ObjectRecordHeader ReadHeader(FileStream stream)
    {
        stream.Position = 0;
        Span<byte> fixedHeader = stackalloc byte[FixedHeaderLength];
        stream.ReadExactly(fixedHeader);
        ValidateMagicAndVersion(fixedHeader);
        var headerLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(fixedHeader[6..10]));
        var keyLength = BinaryPrimitives.ReadUInt16BigEndian(fixedHeader[10..12]);
        var contentTypeLength = BinaryPrimitives.ReadUInt16BigEndian(fixedHeader[12..14]);
        var objectSize = BinaryPrimitives.ReadInt64BigEndian(fixedHeader[14..22]);
        var modifiedTicks = BinaryPrimitives.ReadInt64BigEndian(fixedHeader[22..30]);
        ValidateHeaderLengths(stream, headerLength, keyLength, contentTypeLength, objectSize, modifiedTicks);

        var keyBytes = new byte[keyLength];
        var contentTypeBytes = new byte[contentTypeLength];
        stream.ReadExactly(keyBytes);
        stream.ReadExactly(contentTypeBytes);
        var key = DecodeKey(keyBytes);
        var contentType = DecodeContentType(contentTypeBytes);
        var sha256 = fixedHeader[Sha256Offset..(Sha256Offset + 32)].ToArray();
        var md5 = fixedHeader[Md5Offset..(Md5Offset + 16)].ToArray();
        var metadata = new ObjectMetadata(
            key,
            objectSize,
            contentType,
            FormatEtag(md5),
            new DateTimeOffset(modifiedTicks, TimeSpan.Zero));
        return new ObjectRecordHeader(metadata, headerLength, sha256, md5);
    }

    public static async Task VerifyBodyAsync(FileStream stream, ObjectRecordHeader header, CancellationToken cancellationToken)
    {
        var originalPosition = stream.Position;
        stream.Position = header.BodyOffset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BodyBufferSize);
        byte[]? actual = null;
        try
        {
            var remaining = header.Metadata.Size;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(BodyBufferSize, remaining)), cancellationToken);
                if (count == 0)
                {
                    throw new IOException("An object record ended before its declared size.");
                }

                hash.AppendData(buffer, 0, count);
                remaining -= count;
            }

            actual = hash.GetHashAndReset();
            // OWASP A05:2025 Cryptographic Failures. Verify stored bytes before serving them to detect committed-record corruption.
            if (!CryptographicOperations.FixedTimeEquals(actual, header.Sha256))
            {
                throw new IOException("An object record failed its SHA-256 integrity check.");
            }

            stream.Position = originalPosition;
        }
        finally
        {
            if (actual is not null)
            {
                CryptographicOperations.ZeroMemory(actual);
            }

            // OWASP A05:2025 Cryptographic Failures. Clear pooled object bytes before reuse to prevent cross-request data exposure.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public static async Task VerifyBodyAtStartupAsync(FileStream stream, ObjectRecordHeader header, CancellationToken cancellationToken)
    {
        stream.Position = header.BodyOffset;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = ArrayPool<byte>.Shared.Rent(BodyBufferSize);
        byte[]? actualSha = null;
        byte[]? actualMd5 = null;
        try
        {
            var remaining = header.Metadata.Size;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(BodyBufferSize, remaining)), cancellationToken);
                if (count == 0)
                {
                    throw new IOException("An object record ended before its declared size.");
                }

                sha.AppendData(buffer, 0, count);
                md5.AppendData(buffer, 0, count);
                remaining -= count;
            }

            actualSha = sha.GetHashAndReset();
            actualMd5 = md5.GetHashAndReset();
            var valid = CryptographicOperations.FixedTimeEquals(actualSha, header.Sha256)
                && CryptographicOperations.FixedTimeEquals(actualMd5, header.Md5);
            if (!valid)
            {
                throw new IOException("An object record failed its stored integrity check.");
            }
        }
        finally
        {
            if (actualSha is not null)
            {
                CryptographicOperations.ZeroMemory(actualSha);
            }

            if (actualMd5 is not null)
            {
                CryptographicOperations.ZeroMemory(actualMd5);
            }

            // OWASP A05:2025 Cryptographic Failures. Clear pooled object bytes before reuse to prevent startup data exposure.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task<(long Size, byte[] Sha256, byte[] Md5)> CopyAndHashAsync(
        Stream source,
        Stream destination,
        long maximumObjectBytes,
        CancellationToken cancellationToken)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buffer = ArrayPool<byte>.Shared.Rent(BodyBufferSize);
        try
        {
            long total = 0;
            while (true)
            {
                var count = await source.ReadAsync(buffer.AsMemory(0, BodyBufferSize), cancellationToken);
                if (count == 0)
                {
                    break;
                }

                total = checked(total + count);
                // OWASP A04:2025 Insecure Design. Enforce a hard streaming byte cap to limit disk use by oversized uploads.
                if (total > maximumObjectBytes)
                {
                    throw new ObjectStorageException("EntityTooLarge", "The object exceeds the configured size limit.", 413);
                }

                sha.AppendData(buffer, 0, count);
                md5.AppendData(buffer, 0, count);
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }

            return (total, sha.GetHashAndReset(), md5.GetHashAndReset());
        }
        finally
        {
            // OWASP A05:2025 Cryptographic Failures. Clear pooled object bytes before reuse to prevent cross-request data exposure.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static byte[] CreateHeader(
        int headerLength,
        int keyLength,
        int contentTypeLength,
        long size,
        long modifiedTicks,
        byte[] sha256,
        byte[] md5)
    {
        var header = new byte[FixedHeaderLength];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), Version);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(6, 4), (uint)headerLength);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10, 2), checked((ushort)keyLength));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(12, 2), checked((ushort)contentTypeLength));
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(14, 8), size);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(22, 8), modifiedTicks);
        sha256.CopyTo(header, Sha256Offset);
        md5.CopyTo(header, Md5Offset);
        return header;
    }

    private static void ValidateMagicAndVersion(ReadOnlySpan<byte> header)
    {
        if (!header[..4].SequenceEqual(Magic) || BinaryPrimitives.ReadUInt16BigEndian(header[4..6]) != Version)
        {
            throw new IOException("An object record has an unsupported signature or version.");
        }
    }

    private static void ValidateHeaderLengths(
        FileStream stream,
        int headerLength,
        int keyLength,
        int contentTypeLength,
        long objectSize,
        long modifiedTicks)
    {
        if (keyLength is < 1 or > 1024 || contentTypeLength > 255
            || headerLength != FixedHeaderLength + keyLength + contentTypeLength
            || objectSize < 0 || modifiedTicks < 0
            || stream.Length != headerLength + objectSize)
        {
            throw new IOException("An object record contains invalid or inconsistent lengths.");
        }
    }

    private static byte[] EncodeKey(string key)
    {
        try
        {
            var bytes = StrictUtf8.GetBytes(key);
            if (bytes.Length is < 1 or > 1024 || bytes.Any(value => value == 0 || value < 0x20 || value == 0x7f))
            {
                throw new ObjectStorageException("InvalidArgument", "The object key is invalid.", 400);
            }

            return bytes;
        }
        catch (EncoderFallbackException)
        {
            throw new ObjectStorageException("InvalidArgument", "The object key is not valid UTF-8.", 400);
        }
    }

    private static string DecodeKey(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new IOException("An object record contains an invalid UTF-8 key.", exception);
        }
    }

    private static byte[] ValidateContentType(string contentType)
    {
        if (contentType.Length == 0)
        {
            contentType = "application/octet-stream";
        }

        if (contentType.Length > 255 || contentType.Any(character => character is < ' ' or > '~' || character is '\r' or '\n'))
        {
            throw new ObjectStorageException("InvalidArgument", "The content type is invalid.", 400);
        }

        return Encoding.ASCII.GetBytes(contentType);
    }

    private static string DecodeContentType(byte[] bytes)
    {
        if (bytes.Any(value => value is < 0x20 or > 0x7e))
        {
            throw new IOException("An object record contains an invalid content type.");
        }

        return bytes.Length == 0 ? "application/octet-stream" : Encoding.ASCII.GetString(bytes);
    }

    private static void ValidateDeclaredLength(long? declaredLength, long maximumObjectBytes)
    {
        if (declaredLength is < 0)
        {
            throw new ObjectStorageException("InvalidArgument", "The declared content length is invalid.", 400);
        }

        if (declaredLength > maximumObjectBytes)
        {
            throw new ObjectStorageException("EntityTooLarge", "The object exceeds the configured size limit.", 413);
        }

    }

    private static void VerifyDeclaredLength(long? declaredLength, long actualLength)
    {
        if (declaredLength.HasValue && declaredLength.Value != actualLength)
        {
            throw new ObjectStorageException("IncompleteBody", "The request body length did not match Content-Length.", 400);
        }
    }

    private static void VerifyOptionalHashes(ObjectWriteRequest request, byte[] sha256, byte[] md5)
    {
        if (request.ContentMd5Base64 is not null)
        {
            byte[] supplied;
            try
            {
                supplied = Convert.FromBase64String(request.ContentMd5Base64);
            }
            catch (FormatException exception)
            {
                throw new ObjectStorageException("InvalidDigest", exception.Message, 400);
            }

            // OWASP A05:2025 Cryptographic Failures. Compare the client digest in constant time before publishing the object.
            if (supplied.Length != md5.Length || !CryptographicOperations.FixedTimeEquals(supplied, md5))
            {
                throw new ObjectStorageException("BadDigest", "The Content-MD5 value does not match the request body.", 400);
            }
        }

        if (request.ExpectedPayloadSha256 is not null)
        {
            byte[] supplied;
            try
            {
                supplied = Convert.FromHexString(request.ExpectedPayloadSha256);
            }
            catch (FormatException exception)
            {
                throw new ObjectStorageException("InvalidArgument", exception.Message, 400);
            }

            // OWASP A05:2025 Cryptographic Failures. Compare the signed payload digest in constant time before publication.
            if (supplied.Length != sha256.Length || !CryptographicOperations.FixedTimeEquals(supplied, sha256))
            {
                throw new ObjectStorageException("SignatureDoesNotMatch", "The request payload hash does not match.", 403);
            }
        }
    }

    private static string FormatEtag(byte[] md5) => $"\"{Convert.ToHexString(md5).ToLowerInvariant()}\"";
}

internal sealed record ObjectRecordHeader(ObjectMetadata Metadata, int BodyOffset, byte[] Sha256, byte[] Md5);
