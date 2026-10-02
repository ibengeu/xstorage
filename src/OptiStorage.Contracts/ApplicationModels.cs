namespace OptiStorage.Contracts;

/// <summary>Application credentials returned once at creation or rotation.</summary>
public sealed record ApplicationCredentials(
    string AccessKeyId,
    string SecretAccessKey,
    string Name,
    long QuotaBytes);

/// <summary>Application status that does not contain credentials.</summary>
public sealed record ApplicationSummary(
    string AccessKeyId,
    string Name,
    long QuotaBytes,
    long UsedBytes,
    int BucketCount,
    bool Active);

/// <summary>Credential material used only by the server's SigV4 verifier.</summary>
public sealed record ApplicationSigningKey(
    string AccessKeyId,
    string SecretAccessKey,
    bool Active,
    long CredentialVersion = 0);
