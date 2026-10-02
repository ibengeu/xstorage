namespace XStorage.Contracts;

/// <summary>Identifies an error at the object storage boundary.</summary>
public sealed class ObjectStorageException : Exception
{
    public ObjectStorageException(string code, string message, int statusCode)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }

    public int StatusCode { get; }
}
