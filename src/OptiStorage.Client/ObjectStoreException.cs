namespace OptiStorage.Client;

/// <summary>Reports an S3 API error returned by the storage service.</summary>
public sealed class ObjectStoreException : Exception
{
    /// <summary>Creates an error with the S3 error code, message, HTTP status, and request ID.</summary>
    public ObjectStoreException(string code, string message, int statusCode, string? requestId, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        StatusCode = statusCode;
        RequestId = requestId;
    }

    /// <summary>Gets the S3 error code.</summary>
    public string Code { get; }

    /// <summary>Gets the HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>Gets the S3 request ID when the server returned one.</summary>
    public string? RequestId { get; }
}
