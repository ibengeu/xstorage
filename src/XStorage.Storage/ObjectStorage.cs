using XStorage.Contracts;

namespace XStorage.Storage;

/// <summary>Creates a storage engine for the configured local data root.</summary>
public static class ObjectStorage
{
    public static async Task<IObjectStorage> OpenAsync(
        ObjectStorageOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        StorageOptionsValidator.Validate(options);
        var rootPath = Path.GetFullPath(options.DataRoot);
        FileSystemSafety.CreatePrivateDirectoryTree(rootPath);

        var statePath = Path.Combine(rootPath, "state");
        var applicationsPath = Path.Combine(statePath, "applications");
        var bucketsPath = Path.Combine(rootPath, "buckets");
        FileSystemSafety.CreatePrivateDirectory(statePath);
        FileSystemSafety.CreatePrivateDirectory(applicationsPath);
        FileSystemSafety.CreatePrivateDirectory(bucketsPath);

        // OWASP A04:2025 Insecure Design. Allow one process to mutate a data root so quota and durable state cannot diverge.
        var processLock = ServiceProcessLock.Acquire(statePath);
        LocalApplicationStore? applicationStore = null;
        LocalObjectStorage? storage = null;
        try
        {
            applicationStore = new LocalApplicationStore(
                applicationsPath,
                options.ApplicationSecretEncryptionKey);
            await applicationStore.LoadAsync(cancellationToken);

            storage = new LocalObjectStorage(
                bucketsPath,
                applicationStore,
                options.MaximumObjectBytes,
                options.MaximumObjectsPerApplication,
                options.MaximumBucketsPerApplication,
                options.ContinuationTokenKey,
                processLock,
                options.FaultInjector);
            await storage.ReconcileAtStartupAsync(cancellationToken);
            return storage;
        }
        catch
        {
            if (storage is null)
            {
                applicationStore?.Dispose();
                await processLock.DisposeAsync();
            }
            else
            {
                await storage.DisposeAsync();
            }

            throw;
        }
    }
}
