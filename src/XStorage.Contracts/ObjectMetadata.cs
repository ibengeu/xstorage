namespace XStorage.Contracts;

/// <summary>Describes one committed object version.</summary>
public sealed record ObjectMetadata(
    string Key,
    long Size,
    string ContentType,
    string ETag,
    DateTimeOffset LastModified);
