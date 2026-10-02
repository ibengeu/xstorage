namespace XStorage.Storage;

internal sealed record StoredApplicationRecord
{
    public int SchemaVersion { get; init; } = 1;

    public required string AccessKeyId { get; init; }

    public required string Name { get; init; }

    public required long QuotaBytes { get; init; }

    public long UsedBytes { get; init; }

    public int ObjectCount { get; init; }

    public List<string> OwnedBuckets { get; init; } = [];

    public bool Active { get; init; } = true;

    public long CredentialVersion { get; init; }

    public required string SecretNonce { get; init; }

    public required string SecretCiphertext { get; init; }

    public required string SecretTag { get; init; }
}
