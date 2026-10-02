using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
using OptiStorage.Contracts;
using OptiStorage.Storage;

namespace OptiStorage.Storage.Tests;

public sealed class WindowsStorageSecurityTests
{
    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task OpenAsync_WindowsStorageAclAllowsOnlyTheServiceIdentityAndLocalSystem()
    {
        var root = Path.Combine(Path.GetTempPath(), $"optistorage-acl-{Guid.NewGuid():N}");
        var options = new ObjectStorageOptions(
            root,
            1024,
            RandomNumberGenerator.GetBytes(32),
            RandomNumberGenerator.GetBytes(32));
        var allowedSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            WindowsIdentity.GetCurrent().User!.Value,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value
        };

        try
        {
            await using (IObjectStorage storage = await ObjectStorage.OpenAsync(options, CancellationToken.None))
            {
                var application = await storage.CreateApplicationAsync("acl", 1024, CancellationToken.None);
                await storage.CreateBucketAsync(application.AccessKeyId, "acl-bucket", CancellationToken.None);
                await using var body = new MemoryStream("private"u8.ToArray(), writable: false);
                await storage.PutObjectAsync(
                    application.AccessKeyId,
                    "acl-bucket",
                    "object",
                    new ObjectWriteRequest(body, body.Length, "text/plain", null, null),
                    CancellationToken.None);
            }

            var rootSecurity = new DirectoryInfo(root).GetAccessControl();
            Assert.True(rootSecurity.AreAccessRulesProtected);
            AssertAclContainsOnlyAllowedIdentities(rootSecurity, allowedSids);

            var objectPath = Directory.EnumerateFiles(
                Path.Combine(root, "buckets"),
                "*.obj",
                SearchOption.AllDirectories).Single();
            var objectSecurity = new FileInfo(objectPath).GetAccessControl();
            Assert.True(objectSecurity.AreAccessRulesProtected);
            AssertAclContainsOnlyAllowedIdentities(objectSecurity, allowedSids);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AssertAclContainsOnlyAllowedIdentities(FileSystemSecurity security, HashSet<string> allowedSids)
    {
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>();

        Assert.NotEmpty(rules);
        Assert.All(rules, rule => Assert.Contains(((SecurityIdentifier)rule.IdentityReference).Value, allowedSids));
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "The ACL security test requires Windows.";
        }
    }
}
