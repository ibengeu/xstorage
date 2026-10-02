namespace XStorage.Storage;

/// <summary>Configures local object storage and the keys used to protect persistent state.</summary>
public sealed class ObjectStorageOptions
{
    public ObjectStorageOptions(
        string dataRoot,
        long maximumObjectBytes,
        byte[] applicationSecretEncryptionKey,
        byte[] continuationTokenKey,
        int maximumObjectsPerApplication = 10_000,
        int maximumBucketsPerApplication = 100)
    {
        DataRoot = dataRoot;
        MaximumObjectBytes = maximumObjectBytes;
        ApplicationSecretEncryptionKey = applicationSecretEncryptionKey.ToArray();
        ContinuationTokenKey = continuationTokenKey.ToArray();
        MaximumObjectsPerApplication = maximumObjectsPerApplication;
        MaximumBucketsPerApplication = maximumBucketsPerApplication;
    }

    public string DataRoot { get; }

    public long MaximumObjectBytes { get; }

    public byte[] ApplicationSecretEncryptionKey { get; }

    public byte[] ContinuationTokenKey { get; }

    /// <summary>Gets the maximum committed object count for one application.</summary>
    public int MaximumObjectsPerApplication { get; }

    /// <summary>Gets the maximum owned bucket count for one application.</summary>
    public int MaximumBucketsPerApplication { get; }

    internal Action<StorageFaultPoint>? FaultInjector { get; set; }
}
