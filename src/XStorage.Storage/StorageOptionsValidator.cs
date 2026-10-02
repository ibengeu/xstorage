namespace XStorage.Storage;

internal static class StorageOptionsValidator
{
    public static void Validate(ObjectStorageOptions options)
    {
        if (!Path.IsPathFullyQualified(options.DataRoot))
        {
            throw new ArgumentException("The data root must be an absolute path.", nameof(options));
        }

        var fullPath = Path.GetFullPath(options.DataRoot);
        var fileSystemRoot = Path.GetPathRoot(fullPath);
        // OWASP A02:2025 Security Misconfiguration. Reject filesystem roots to prevent changing permissions across the whole volume.
        if (fileSystemRoot is not null
            && (OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase.Equals(fullPath, fileSystemRoot)
                : StringComparer.Ordinal.Equals(fullPath, fileSystemRoot)))
        {
            throw new ArgumentException("The data root cannot be a filesystem root.", nameof(options));
        }

        if (options.MaximumObjectBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum object size must be zero or greater.");
        }

        // OWASP A02:2025 Security Misconfiguration. Reject invalid resource limits instead of disabling metadata bounds by accident.
        if (options.MaximumObjectsPerApplication < 0 || options.MaximumBucketsPerApplication < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Application object and bucket limits must be zero or greater.");
        }

        if (options.ApplicationSecretEncryptionKey.Length != 32)
        {
            throw new ArgumentException("The application secret encryption key must contain 32 bytes.", nameof(options));
        }

        // OWASP A02:2025 Security Misconfiguration. Enforce the documented key size to reject divergent or malformed token-key configuration.
        if (options.ContinuationTokenKey.Length != 32)
        {
            throw new ArgumentException("The continuation token key must contain 32 bytes.", nameof(options));
        }
    }
}
