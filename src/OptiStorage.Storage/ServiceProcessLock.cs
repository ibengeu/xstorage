namespace OptiStorage.Storage;

internal static class ServiceProcessLock
{
    public static FileStream Acquire(string stateDirectory)
    {
        var path = Path.Combine(stateDirectory, ".service.lock");
        if (File.Exists(path))
        {
            FileSystemSafety.EnsureFileIsNotLink(path);
        }

        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        try
        {
            FileSystemSafety.EnsureFileIsNotLink(path);
            FileSystemSafety.SetPrivateFileMode(path);
            FileSystemSafety.FlushDirectory(stateDirectory);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
