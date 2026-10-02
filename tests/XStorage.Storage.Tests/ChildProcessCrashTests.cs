using System.Diagnostics;
using System.Security.Cryptography;
using XStorage.Contracts;
using XStorage.Storage;

namespace XStorage.Storage.Tests;

public sealed class ChildProcessCrashTests
{
    private const string ChildFlag = "XSTORAGE_PRE_RENAME_CRASH_CHILD";

    [LinuxFact]
    public async Task ProcessCrashAfterTemporaryFileFlush_DoesNotPublishObject()
    {
        if (Environment.GetEnvironmentVariable(ChildFlag) == "1")
        {
            await CrashChildBeforeRenameAsync();
            throw new InvalidOperationException("The crash hook did not terminate the child process.");
        }

        var root = Path.Combine(Path.GetTempPath(), $"xstorage-crash-{Guid.NewGuid():N}");
        var encryptionKey = RandomNumberGenerator.GetBytes(32);
        var tokenKey = RandomNumberGenerator.GetBytes(32);
        var options = new ObjectStorageOptions(root, 1024, encryptionKey, tokenKey);
        string accessKeyId;
        try
        {
            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var application = await storage.CreateApplicationAsync("crash", 100, CancellationToken.None);
                accessKeyId = application.AccessKeyId;
                await storage.CreateBucketAsync(accessKeyId, "crash-bucket", CancellationToken.None);
            }

            var projectPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../XStorage.Storage.Tests.csproj"));
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(projectPath)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("test");
            start.ArgumentList.Add(projectPath);
            start.ArgumentList.Add("--no-build");
            start.ArgumentList.Add("--no-restore");
            start.ArgumentList.Add("--verbosity");
            start.ArgumentList.Add("quiet");
            start.ArgumentList.Add("-m:1");
            start.ArgumentList.Add("/p:UseSharedCompilation=false");
            start.ArgumentList.Add("--filter");
            start.ArgumentList.Add("FullyQualifiedName~ProcessCrashAfterTemporaryFileFlush_DoesNotPublishObject");
            start.Environment[ChildFlag] = "1";
            start.Environment["XSTORAGE_CRASH_ROOT"] = root;
            start.Environment["XSTORAGE_CRASH_ACCESS_KEY_ID"] = accessKeyId;
            start.Environment["XSTORAGE_CRASH_ENCRYPTION_KEY"] = Convert.ToBase64String(encryptionKey);
            start.Environment["XSTORAGE_CRASH_TOKEN_KEY"] = Convert.ToBase64String(tokenKey);

            using var child = Process.Start(start)
                ?? throw new InvalidOperationException("The crash-test child process did not start.");
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            _ = await stdout;
            _ = await stderr;
            Assert.NotEqual(0, child.ExitCode);

            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var summary = Assert.Single(await storage.ListApplicationsAsync(CancellationToken.None));
                Assert.Equal(0, summary.UsedBytes);
                var missing = await Assert.ThrowsAsync<ObjectStorageException>(() => storage.HeadObjectAsync(
                    accessKeyId, "crash-bucket", "interrupted", CancellationToken.None));
                Assert.Equal("NoSuchKey", missing.Code);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task CrashChildBeforeRenameAsync()
    {
        var root = Environment.GetEnvironmentVariable("XSTORAGE_CRASH_ROOT")!;
        var accessKeyId = Environment.GetEnvironmentVariable("XSTORAGE_CRASH_ACCESS_KEY_ID")!;
        var options = new ObjectStorageOptions(
            root,
            1024,
            Convert.FromBase64String(Environment.GetEnvironmentVariable("XSTORAGE_CRASH_ENCRYPTION_KEY")!),
            Convert.FromBase64String(Environment.GetEnvironmentVariable("XSTORAGE_CRASH_TOKEN_KEY")!));
        options.FaultInjector = point =>
        {
            if (point == StorageFaultPoint.AfterObjectTemporaryFileFlush)
            {
                // OWASP A04:2025 Insecure Design. Terminate before rename to prove crash recovery never publishes temporary bytes.
                using var process = Process.GetCurrentProcess();
                process.Kill();
            }
        };

        await using IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None);
        await using var body = new MemoryStream("uncommitted"u8.ToArray(), writable: false);
        await storage.PutObjectAsync(accessKeyId, "crash-bucket", "interrupted",
            new ObjectWriteRequest(body, body.Length, "application/octet-stream", null, null), CancellationToken.None);
    }
}

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "The abrupt child-process crash test requires Linux.";
        }
    }
}
