using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OptiStorage.Contracts;

namespace OptiStorage.Storage;

internal sealed class LocalApplicationStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly byte[] _encryptionKey;
    private readonly ConcurrentDictionary<string, StoredApplicationRecord> _records = new(StringComparer.Ordinal);
    // Each record is a separate file, so writes serialize per access key ID and different applications persist in parallel.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _recordLocks = new(StringComparer.Ordinal);

    public LocalApplicationStore(string directory, byte[] encryptionKey)
    {
        _directory = directory;
        _encryptionKey = encryptionKey.ToArray();
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var removedTemporaryFiles = false;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.tmp", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemSafety.EnsureFileIsNotLink(path);
            File.Delete(path);
            removedTemporaryFiles = true;
        }

        if (removedTemporaryFiles)
        {
            FileSystemSafety.FlushDirectory(_directory);
        }

        foreach (var path in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemSafety.EnsureFileIsNotLink(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var record = await JsonSerializer.DeserializeAsync<StoredApplicationRecord>(stream, JsonOptions, cancellationToken)
                ?? throw new IOException("An application record is empty.");
            ValidateRecord(path, record);
            _ = DecryptSecret(record);
            if (!_records.TryAdd(record.AccessKeyId, record))
            {
                throw new IOException("The application store contains a duplicate access key ID.");
            }
        }
    }

    public async Task<ApplicationCredentials> CreateAsync(string name, long quotaBytes, CancellationToken cancellationToken)
    {
        if (quotaBytes < 0)
        {
            throw new ObjectStorageException("InvalidArgument", "The application quota must be zero or greater.", 400);
        }

        var credentials = GenerateCredentials(name, quotaBytes);
        var record = EncryptCredentials(credentials);
        await WithRecordLockAsync(record.AccessKeyId, cancellationToken, async () =>
        {
            await PersistAsync(record, cancellationToken);
            _records[record.AccessKeyId] = record;
        });
        return credentials;
    }

    public async Task<ApplicationCredentials> RotateAsync(string accessKeyId, CancellationToken cancellationToken)
    {
        RequireKnownApplication(accessKeyId);
        ApplicationCredentials? credentials = null;
        await WithRecordLockAsync(accessKeyId, cancellationToken, async () =>
        {
            if (!_records.TryGetValue(accessKeyId, out var current))
            {
                throw new ObjectStorageException("NoSuchApplication", "The application does not exist.", 404);
            }

            credentials = GenerateCredentialsForExisting(current);
            var updated = EncryptCredentials(credentials) with
            {
                UsedBytes = current.UsedBytes,
                ObjectCount = current.ObjectCount,
                OwnedBuckets = current.OwnedBuckets,
                Active = current.Active,
                CredentialVersion = checked(current.CredentialVersion + 1)
            };
            await PersistAsync(updated, cancellationToken);
            _records[accessKeyId] = updated;
        });
        return credentials!;
    }

    public StoredApplicationRecord? Get(string accessKeyId) =>
        _records.TryGetValue(accessKeyId, out var record) ? record : null;

    public IReadOnlyList<StoredApplicationRecord> GetRecords() =>
        _records.Values.OrderBy(record => record.AccessKeyId, StringComparer.Ordinal).ToArray();

    public async Task RecoverFromOwnershipMarkersAsync(
        string accessKeyId,
        IReadOnlyList<string> ownedBuckets,
        CancellationToken cancellationToken)
    {
        if (!IsGeneratedAccessKeyId(accessKeyId))
        {
            throw new IOException("A bucket ownership marker contains an invalid application ID.");
        }

        await WithRecordLockAsync(accessKeyId, cancellationToken, async () =>
        {
            if (_records.ContainsKey(accessKeyId))
            {
                return;
            }

            // OWASP A05:2025 Cryptographic Failures. Generate an unissued replacement secret so lost credentials cannot be guessed during recovery.
            var secretBytes = RandomNumberGenerator.GetBytes(32);
            var secret = Convert.ToBase64String(secretBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            CryptographicOperations.ZeroMemory(secretBytes);
            var credentials = new ApplicationCredentials(accessKeyId, secret, "recovered", 0);
            var record = EncryptCredentials(credentials) with
            {
                OwnedBuckets = ownedBuckets.Order(StringComparer.Ordinal).ToList()
            };
            await PersistAsync(record, cancellationToken);
            _records[accessKeyId] = record;
        });
    }

    public static bool IsGeneratedAccessKeyId(string accessKeyId) =>
        accessKeyId.Length == 26 && accessKeyId.StartsWith("OS", StringComparison.Ordinal) &&
        accessKeyId.AsSpan(2).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-".AsSpan()) < 0;

    public ApplicationSigningKey? FindSigningKey(string accessKeyId)
    {
        var record = Get(accessKeyId);
        return record is null
            ? null
            : new ApplicationSigningKey(record.AccessKeyId, DecryptSecret(record), record.Active, record.CredentialVersion);
    }

    public Task<IReadOnlyList<ApplicationSummary>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ApplicationSummary> result = _records.Values
            .OrderBy(record => record.AccessKeyId, StringComparer.Ordinal)
            .Select(record => new ApplicationSummary(
                record.AccessKeyId,
                record.Name,
                record.QuotaBytes,
                record.UsedBytes,
                record.OwnedBuckets.Count,
                record.Active))
            .ToArray();
        return Task.FromResult(result);
    }

    public async Task UpdateAsync(
        string accessKeyId,
        Func<StoredApplicationRecord, StoredApplicationRecord> update,
        CancellationToken cancellationToken)
    {
        RequireKnownApplication(accessKeyId);
        await WithRecordLockAsync(accessKeyId, cancellationToken, async () =>
        {
            if (!_records.TryGetValue(accessKeyId, out var current))
            {
                throw new ObjectStorageException("NoSuchApplication", "The application does not exist.", 404);
            }

            var updated = update(current);
            await PersistAsync(updated, cancellationToken);
            _records[accessKeyId] = updated;
        });
    }

    private void RequireKnownApplication(string accessKeyId)
    {
        // OWASP A04:2025 Insecure Design. Reject unknown IDs before a lock is allocated so caller-supplied IDs cannot grow the lock table without bound.
        if (!_records.ContainsKey(accessKeyId))
        {
            throw new ObjectStorageException("NoSuchApplication", "The application does not exist.", 404);
        }
    }

    private async Task WithRecordLockAsync(string accessKeyId, CancellationToken cancellationToken, Func<Task> action)
    {
        var gate = _recordLocks.GetOrAdd(accessKeyId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await action();
        }
        finally
        {
            gate.Release();
        }
    }

    private ApplicationCredentials GenerateCredentials(string name, long quotaBytes)
    {
        // OWASP A06:2025 Identification and Authentication Failures. Use a cryptographic random generator for unguessable application credentials.
        var accessKeyBytes = RandomNumberGenerator.GetBytes(18);
        var accessKeyId = "OS" + Convert.ToBase64String(accessKeyBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var secret = Convert.ToBase64String(secretBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        CryptographicOperations.ZeroMemory(secretBytes);
        CryptographicOperations.ZeroMemory(accessKeyBytes);
        return new ApplicationCredentials(accessKeyId, secret, name, quotaBytes);
    }

    private static ApplicationCredentials GenerateCredentialsForExisting(StoredApplicationRecord record)
    {
        // OWASP A06:2025 Identification and Authentication Failures. Replace credentials with a cryptographically random secret during rotation.
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var secret = Convert.ToBase64String(secretBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        CryptographicOperations.ZeroMemory(secretBytes);
        return new ApplicationCredentials(record.AccessKeyId, secret, record.Name, record.QuotaBytes);
    }

    private StoredApplicationRecord EncryptCredentials(ApplicationCredentials credentials)
    {
        var secretBytes = Encoding.UTF8.GetBytes(credentials.SecretAccessKey);
        // OWASP A05:2025 Cryptographic Failures. Use a fresh random GCM nonce and authenticated application ID to protect stored credentials.
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[secretBytes.Length];
        var tag = new byte[16];
        var associatedData = Encoding.ASCII.GetBytes(credentials.AccessKeyId);

        // OWASP A05:2025 Cryptographic Failures. Encrypt recoverable SigV4 secrets at rest to prevent credential disclosure.
        try
        {
            using var aes = new AesGcm(_encryptionKey, tag.Length);
            aes.Encrypt(nonce, secretBytes, ciphertext, tag, associatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }

        return new StoredApplicationRecord
        {
            AccessKeyId = credentials.AccessKeyId,
            Name = credentials.Name,
            QuotaBytes = credentials.QuotaBytes,
            SecretNonce = Convert.ToBase64String(nonce),
            SecretCiphertext = Convert.ToBase64String(ciphertext),
            SecretTag = Convert.ToBase64String(tag)
        };
    }

    private string DecryptSecret(StoredApplicationRecord record)
    {
        var nonce = Convert.FromBase64String(record.SecretNonce);
        var ciphertext = Convert.FromBase64String(record.SecretCiphertext);
        var tag = Convert.FromBase64String(record.SecretTag);
        var plaintext = new byte[ciphertext.Length];
        var associatedData = Encoding.ASCII.GetBytes(record.AccessKeyId);
        try
        {
            using var aes = new AesGcm(_encryptionKey, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_encryptionKey);
        foreach (var gate in _recordLocks.Values)
        {
            gate.Dispose();
        }
    }

    private async Task PersistAsync(StoredApplicationRecord record, CancellationToken cancellationToken)
    {
        var fileName = $"{record.AccessKeyId}.json";
        var destination = Path.Combine(_directory, fileName);
        var temporary = Path.Combine(_directory, $".{fileName}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}.tmp");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            FileSystemSafety.SetPrivateFileMode(temporary);
            FileSystemSafety.MoveFile(temporary, destination, overwrite: true);
            FileSystemSafety.FlushDirectory(_directory);
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void ValidateRecord(string path, StoredApplicationRecord record)
    {
        if (record.SchemaVersion != 1 || !StringComparer.Ordinal.Equals(Path.GetFileNameWithoutExtension(path), record.AccessKeyId))
        {
            throw new IOException("An application record has an unsupported version or identity.");
        }

        if (record.QuotaBytes < 0 || record.UsedBytes < 0 || record.ObjectCount < 0 || record.CredentialVersion < 0 || record.OwnedBuckets is null)
        {
            throw new IOException("An application record has invalid counters.");
        }
    }
}
