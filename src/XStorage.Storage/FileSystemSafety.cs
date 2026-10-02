using System.Runtime.InteropServices;

namespace XStorage.Storage;

internal static partial class FileSystemSafety
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const int LinuxX86OpenDirectory = 0x10000;
    private const int LinuxX86OpenNoFollow = 0x20000;
    private const int LinuxArmOpenDirectory = 0x4000;
    private const int LinuxArmOpenNoFollow = 0x8000;
    private const int LinuxOpenCloseOnExec = 0x80000;
    private const int MacOpenDirectory = 0x100000;
    private const int MacOpenNoFollow = 0x100;
    private const int MacOpenCloseOnExec = 0x1000000;

    public static void CreatePrivateDirectoryTree(string absolutePath)
    {
        var fullPath = Path.GetFullPath(absolutePath);
        EnsurePathIsNotFileSystemRoot(fullPath);
        CreateDirectoryHierarchy(fullPath);
        SetPrivateDirectoryMode(fullPath);
        FlushDirectory(fullPath);
    }

    public static void CreatePrivateDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        EnsurePathIsNotFileSystemRoot(fullPath);
        var created = CreateDirectoryHierarchy(fullPath);
        // An existing directory that already has the private mode needs no mode change, so the directory fsync is skipped.
        // A new directory, or one with a broader mode, still gets the mode change and a durable flush.
        if (!created && HasPrivateUnixDirectoryMode(fullPath))
        {
            return;
        }

        SetPrivateDirectoryMode(fullPath);
        FlushDirectory(fullPath);
    }

    private static bool HasPrivateUnixDirectoryMode(string path) =>
        (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && File.GetUnixFileMode(path) == PrivateDirectoryMode;

    private static bool CreateDirectoryHierarchy(string absolutePath)
    {
        var root = Path.GetPathRoot(absolutePath)
                   ?? throw new IOException("The data path has no filesystem root.");
        var current = root;
        var created = false;

        foreach (var part in absolutePath[root.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (TryEnsureDirectoryIsNotLink(current))
            {
                continue;
            }

            if (OperatingSystem.IsWindows())
            {
                // OWASP A02:2025 Security Misconfiguration. Create the directory with a protected ACL before publishing it.
                WindowsFileSystemSafety.CreatePrivateDirectory(current);
            }
            else
            {
                Directory.CreateDirectory(current);
            }

            EnsureDirectoryIsNotLink(current);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                // Flush each parent so newly created nested hash directories survive a power failure with their entries intact.
                FlushDirectory(Path.GetDirectoryName(current) ?? current);
            }

            created = true;
        }

        return created;
    }

    private static bool TryEnsureDirectoryIsNotLink(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        // OWASP A07:2025 Injection. Check every existing path component before traversal to prevent symlink escape from the data root.
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The data tree contains a non-directory or symbolic link.");
        }

        return true;
    }

    public static void EnsureDirectoryIsNotLink(string path)
    {
        var attributes = File.GetAttributes(path);
        // OWASP A07:2025 Injection. Reject symbolic links so key-derived operations cannot escape the data root.
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The data tree contains a non-directory or symbolic link.");
        }
    }

    public static void EnsureDirectoryPathIsNotLink(string absolutePath)
    {
        var fullPath = Path.GetFullPath(absolutePath);
        var root = Path.GetPathRoot(fullPath)
                   ?? throw new IOException("The data path has no filesystem root.");
        var current = root;

        foreach (var part in fullPath[root.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!TryEnsureDirectoryIsNotLink(current))
            {
                return;
            }
        }
    }

    public static void EnsureFileIsNotLink(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The data tree contains a non-file or symbolic link.");
        }
    }

    public static void SetPrivateFileMode(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, PrivateFileMode);
        }
        else if (OperatingSystem.IsWindows())
        {
            // OWASP A02:2025 Security Misconfiguration. Restrict stored files to the service identity and LocalSystem.
            WindowsFileSystemSafety.SetPrivateFileMode(path);
        }
    }

    private static void SetPrivateDirectoryMode(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // OWASP A02:2025 Security Misconfiguration. Restrict service-owned directories to the service account.
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }
        else if (OperatingSystem.IsWindows())
        {
            // OWASP A02:2025 Security Misconfiguration. Protect the storage directory from inherited broad access.
            WindowsFileSystemSafety.SetPrivateDirectoryMode(path);
        }
    }

    public static void MoveFile(string sourcePath, string destinationPath, bool overwrite)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsFileSystemSafety.MoveFile(sourcePath, destinationPath, overwrite);
            return;
        }

        File.Move(sourcePath, destinationPath, overwrite);
    }

    public static void MoveDirectory(string sourcePath, string destinationPath)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsFileSystemSafety.MoveDirectory(sourcePath, destinationPath);
            return;
        }

        Directory.Move(sourcePath, destinationPath);
    }

    public static void DeleteObjectFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsFileSystemSafety.DeleteObjectFile(path);
            return;
        }

        File.Delete(path);
    }

    public static bool IsDirectoryCreationStagingPath(string path) =>
        OperatingSystem.IsWindows() && WindowsFileSystemSafety.IsDirectoryCreationStagingPath(path);

    public static void DeleteDirectoryCreationStagingPath(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsFileSystemSafety.DeleteDirectoryCreationStagingPath(path);
        }
    }

    public static void FlushDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Committed Windows namespace changes use MOVEFILE_WRITE_THROUGH at the operation boundary.
            return;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Durable storage operations are supported only on Linux, macOS, and Windows.");
        }

        var flags = OperatingSystem.IsLinux()
            ? GetLinuxDirectoryOpenFlags()
            : MacOpenDirectory | MacOpenNoFollow | MacOpenCloseOnExec;
        var fileDescriptor = OpenDirectory(path, flags);
        if (fileDescriptor < 0)
        {
            throw NativeFailure("Could not open a data directory for a durable flush.");
        }

        try
        {
            if (Fsync(fileDescriptor) != 0)
            {
                throw NativeFailure("Could not flush a data directory.");
            }
        }
        finally
        {
            Close(fileDescriptor);
        }
    }

    private static void EnsurePathIsNotFileSystemRoot(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        // OWASP A02:2025 Security Misconfiguration. Prevent volume-wide permission changes from an unsafe data-root setting.
        if (root is not null && StringComparer.OrdinalIgnoreCase.Equals(fullPath, root))
        {
            throw new ArgumentException("The data root cannot be a filesystem root.", nameof(fullPath));
        }
    }

    private static int GetLinuxDirectoryOpenFlags()
    {
        // Linux ARM uses different O_DIRECTORY and O_NOFOLLOW values than Linux x86.
        var architectureFlags = RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64
            ? LinuxArmOpenDirectory | LinuxArmOpenNoFollow
            : LinuxX86OpenDirectory | LinuxX86OpenNoFollow;

        // OWASP A07:2025 Injection. O_NOFOLLOW prevents symlink redirection; O_CLOEXEC prevents descriptor leakage to child processes.
        return architectureFlags | LinuxOpenCloseOnExec;
    }

    private static IOException NativeFailure(string message)
    {
        var error = Marshal.GetLastPInvokeError();
        return new IOException(message, new System.ComponentModel.Win32Exception(error));
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenDirectory(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fileDescriptor);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fileDescriptor);
}
