namespace OptiStorage.Contracts;

/// <summary>Specifies one ListObjectsV2 request.</summary>
public sealed record ObjectListingRequest(
    string? Prefix = null,
    string? Delimiter = null,
    string? StartAfter = null,
    string? ContinuationToken = null,
    int MaxKeys = 1000,
    string? EncodingType = null);

/// <summary>One object entry in a ListObjectsV2 result.</summary>
public sealed record ListedObject(
    string Key,
    DateTimeOffset LastModified,
    string ETag,
    long Size,
    string StorageClass = "STANDARD");

/// <summary>A single ListObjectsV2 page.</summary>
public sealed record ObjectListingPage(
    string Bucket,
    string? Prefix,
    int KeyCount,
    int MaxKeys,
    bool IsTruncated,
    IReadOnlyList<ListedObject> Contents,
    IReadOnlyList<string> CommonPrefixes,
    string? StartAfter,
    string? Delimiter,
    string? ContinuationToken,
    string? NextContinuationToken,
    string? EncodingType);
