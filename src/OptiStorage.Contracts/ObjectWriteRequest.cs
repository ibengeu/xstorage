namespace OptiStorage.Contracts;

/// <summary>Describes a single whole-object upload.</summary>
/// <remarks>The caller owns <see cref="Content"/> and MUST keep it open until the call completes.</remarks>
public sealed record ObjectWriteRequest(
    Stream Content,
    long? ContentLength,
    string ContentType,
    string? ContentMd5Base64,
    string? ExpectedPayloadSha256);