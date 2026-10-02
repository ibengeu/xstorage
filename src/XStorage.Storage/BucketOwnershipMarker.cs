using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace XStorage.Storage;

internal static class BucketOwnershipMarker
{
    private static readonly byte[] Magic = "OSOW"u8.ToArray();
    private const ushort Version = 1;
    private const int HashLength = 32;

    public static async Task WriteAsync(string path, string accessKeyId, CancellationToken cancellationToken)
    {
        var id = Encoding.ASCII.GetBytes(accessKeyId);
        if (id.Length is < 1 or > 128)
        {
            throw new IOException("The application identifier cannot be stored in an ownership marker.");
        }

        var content = new byte[8 + id.Length + HashLength];
        Magic.CopyTo(content, 0);
        BinaryPrimitives.WriteUInt16BigEndian(content.AsSpan(4, 2), Version);
        BinaryPrimitives.WriteUInt16BigEndian(content.AsSpan(6, 2), checked((ushort)id.Length));
        id.CopyTo(content, 8);
        var checksum = SHA256.HashData(content.AsSpan(0, 8 + id.Length));
        checksum.CopyTo(content, 8 + id.Length);

        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
        FileSystemSafety.SetPrivateFileMode(path);
        CryptographicOperations.ZeroMemory(content);
        CryptographicOperations.ZeroMemory(checksum);
        CryptographicOperations.ZeroMemory(id);
    }

    public static string Read(string path)
    {
        FileSystemSafety.EnsureFileIsNotLink(path);
        var content = File.ReadAllBytes(path);
        if (content.Length < 8 + HashLength)
        {
            throw new IOException("A bucket ownership marker is incomplete.");
        }

        var idLength = BinaryPrimitives.ReadUInt16BigEndian(content.AsSpan(6, 2));
        if (!content.AsSpan(0, 4).SequenceEqual(Magic)
            || BinaryPrimitives.ReadUInt16BigEndian(content.AsSpan(4, 2)) != Version
            || idLength is < 1 or > 128
            || content.Length != 8 + idLength + HashLength)
        {
            throw new IOException("A bucket ownership marker has an invalid format.");
        }

        var expected = SHA256.HashData(content.AsSpan(0, 8 + idLength));
        var supplied = content.AsSpan(8 + idLength, HashLength);
        // OWASP A05:2025 Cryptographic Failures. Verify the marker checksum in constant time to detect ownership-record corruption.
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
        {
            throw new IOException("A bucket ownership marker failed its integrity check.");
        }

        var owner = Encoding.ASCII.GetString(content, 8, idLength);
        CryptographicOperations.ZeroMemory(expected);
        CryptographicOperations.ZeroMemory(content);
        return owner;
    }
}