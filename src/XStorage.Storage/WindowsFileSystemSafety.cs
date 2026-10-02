using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace XStorage.Storage;

[SupportedOSPlatform("windows")]
internal static partial class WindowsFileSystemSafety
{
    private const uint MoveFileWriteThrough = 0x00000008;
    private const uint MoveFileReplaceExisting = 0x00000001;
    private const string DirectoryCreationPrefix = ".xstorage-mkdir-";

    public static void CreatePrivateDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath)
                     ?? throw new IOException("The data path has no parent directory.");
        var stagingPath = Path.Combine(parent, $"{DirectoryCreationPrefix}{Guid.NewGuid():N}.tmp");
        new DirectoryInfo(stagingPath).Create(CreatePrivateDirectorySecurity());

        try
        {
            MoveDirectory(stagingPath, fullPath);
        }
        catch (IOException) when (Directory.Exists(fullPath))
        {
            FileSystemSafety.EnsureDirectoryIsNotLink(fullPath);
        }
        finally
        {
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath);
            }
        }
    }

    public static void SetPrivateDirectoryMode(string path) =>
        new DirectoryInfo(path).SetAccessControl(CreatePrivateDirectorySecurity());

    public static void SetPrivateFileMode(string path) =>
        new FileInfo(path).SetAccessControl(CreatePrivateFileSecurity());

    public static void MoveFile(string sourcePath, string destinationPath, bool overwrite)
    {
        var flags = MoveFileWriteThrough | (overwrite ? MoveFileReplaceExisting : 0);
        Move(ToNativePath(sourcePath), ToNativePath(destinationPath), flags,
            "Could not durably move a storage file.");
    }

    public static void MoveDirectory(string sourcePath, string destinationPath) =>
        Move(ToNativePath(sourcePath), ToNativePath(destinationPath), MoveFileWriteThrough,
            "Could not durably move a storage directory.");

    public static void DeleteObjectFile(string path)
    {
        var parent = Path.GetDirectoryName(path)
                     ?? throw new IOException("The object path has no parent directory.");
        var tombstone = Path.Combine(parent, $".delete-{Guid.NewGuid():N}.tmp");
        Move(ToNativePath(path), ToNativePath(tombstone), MoveFileWriteThrough,
            "Could not durably remove a stored object.");

        try
        {
            File.Delete(tombstone);
        }
        catch (IOException)
        {
            // The write-through rename commits the deletion. Startup recovery removes a leftover tombstone.
        }
        catch (UnauthorizedAccessException)
        {
            // The write-through rename commits the deletion. Startup recovery removes a leftover tombstone.
        }
    }

    public static bool IsDirectoryCreationStagingPath(string path) =>
        Path.GetFileName(path).StartsWith(DirectoryCreationPrefix, StringComparison.Ordinal)
        && path.EndsWith(".tmp", StringComparison.Ordinal);

    public static void DeleteDirectoryCreationStagingPath(string path)
    {
        FileSystemSafety.EnsureDirectoryIsNotLink(path);
        Directory.Delete(path);
    }

    private static DirectorySecurity CreatePrivateDirectorySecurity()
    {
        var user = GetCurrentUserSid();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        AddDirectoryFullControlRule(security, user);
        AddDirectoryFullControlRule(security, GetLocalSystemSid());
        return security;
    }

    private static FileSecurity CreatePrivateFileSecurity()
    {
        var user = GetCurrentUserSid();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(GetLocalSystemSid(), FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static void AddDirectoryFullControlRule(DirectorySecurity security, SecurityIdentifier sid)
    {
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    private static SecurityIdentifier GetCurrentUserSid() =>
        WindowsIdentity.GetCurrent().User
        ?? throw new IOException("The Windows service identity has no security identifier.");

    private static SecurityIdentifier GetLocalSystemSid() =>
        new(WellKnownSidType.LocalSystemSid, null);

    private static string ToNativePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || fullPath.StartsWith("\\\\.\\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        return fullPath.StartsWith("\\\\", StringComparison.Ordinal)
            ? "\\\\?\\UNC\\" + fullPath[2..]
            : "\\\\?\\" + fullPath;
    }

    private static void Move(string sourcePath, string destinationPath, uint flags, string message)
    {
        if (!MoveFileEx(sourcePath, destinationPath, flags))
        {
            throw NativeFailure(message);
        }
    }

    private static IOException NativeFailure(string message) =>
        new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existingName, string newName, uint flags);
}
