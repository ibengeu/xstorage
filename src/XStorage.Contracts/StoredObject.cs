namespace XStorage.Contracts;

/// <summary>A committed object and the readable stream that contains its bytes.</summary>
/// <remarks>
/// The caller owns the returned object and MUST dispose it. Disposing this value disposes
/// its <see cref="Content"/> stream. The storage service keeps no ownership of this stream
/// after returning it.
/// </remarks>
public sealed class StoredObject : IAsyncDisposable
{
    public StoredObject(ObjectMetadata metadata, Stream content)
    {
        Metadata = metadata;
        Content = content;
    }

    public ObjectMetadata Metadata { get; }

    /// <summary>Gets the object bytes. The caller MUST dispose this stream.</summary>
    public Stream Content { get; }

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}