namespace XStorage.Server;

/// <summary>Runtime settings for the standalone storage service.</summary>
public sealed record StorageServiceSettings(
    string DataRoot,
    string DataPlaneUrl,
    string AdminUrl,
    string AdminToken,
    byte[] ApplicationSecretEncryptionKey,
    byte[] ContinuationTokenKey,
    long MaximumObjectBytes = 26_214_400,
    IReadOnlyList<string>? TrustedProxyAddresses = null,
    int MaximumObjectsPerApplication = 10_000,
    int MaximumBucketsPerApplication = 100)
{
    internal TimeProvider? TimeProviderOverride { get; init; }
}