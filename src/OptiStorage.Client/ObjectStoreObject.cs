using Amazon.S3.Model;
using OptiStorage.Contracts;

namespace OptiStorage.Client;

/// <summary>Contains object metadata and an AWS SDK response stream.</summary>
/// <remarks>The caller MUST dispose this value after reading its content.</remarks>
public sealed class ObjectStoreObject : IAsyncDisposable
{
    private readonly GetObjectResponse _response;

    internal ObjectStoreObject(ObjectMetadata metadata, GetObjectResponse response)
    {
        Metadata = metadata;
        _response = response;
    }

    /// <summary>Gets object metadata.</summary>
    public ObjectMetadata Metadata { get; }

    /// <summary>Gets the object bytes. The caller MUST NOT dispose the client before reading finishes.</summary>
    public Stream Content => _response.ResponseStream;

    /// <summary>Closes the object response and its content stream.</summary>
    public ValueTask DisposeAsync()
    {
        _response.Dispose();
        return ValueTask.CompletedTask;
    }
}
