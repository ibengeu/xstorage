using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OptiStorage.Server;

namespace OptiStorage.Server.Tests;

public sealed class SigV4SigningTests
{
    [Fact]
    public async Task WrongCredentialRegion_ReturnsMalformedScopeBeforeMutation()
    {
        var root = CreateRoot("region");
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            var credentials = await CreateApplicationAsync(host.AdminAddress);
            using var client = new HttpClient { BaseAddress = host.DataAddress };
            using var request = SignRequest(HttpMethod.Put, "/wrong-region-bucket", host.DataAddress, credentials.AccessKeyId, credentials.SecretAccessKey, [], region: "us-west-2");

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("<Code>AuthorizationHeaderMalformed</Code>", body, StringComparison.Ordinal);
            Assert.Equal(0, Assert.Single(await host.Storage.ListApplicationsAsync(CancellationToken.None)).BucketCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("x-amz-date;host;x-amz-content-sha256")]
    [InlineData("host;host;x-amz-content-sha256;x-amz-date")]
    [InlineData("Host;x-amz-content-sha256;x-amz-date")]
    [InlineData("host;;x-amz-content-sha256;x-amz-date")]
    public async Task MalformedSignedHeaderList_ReturnsMalformedAuthorizationBeforeMutation(string signedHeaders)
    {
        var root = CreateRoot("signed-headers");
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            var credentials = await CreateApplicationAsync(host.AdminAddress);
            using var client = new HttpClient { BaseAddress = host.DataAddress };
            using var request = SignRequest(HttpMethod.Put, "/signed-headers-bucket", host.DataAddress,
                credentials.AccessKeyId, credentials.SecretAccessKey, []);
            var authorization = request.Headers.GetValues("Authorization").Single();
            request.Headers.Remove("Authorization");
            request.Headers.TryAddWithoutValidation("Authorization", authorization.Replace(
                "SignedHeaders=host;x-amz-content-sha256;x-amz-date",
                "SignedHeaders=" + signedHeaders,
                StringComparison.Ordinal));

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("<Code>AuthorizationHeaderMalformed</Code>", body, StringComparison.Ordinal);
            Assert.Equal(0, Assert.Single(await host.Storage.ListApplicationsAsync(CancellationToken.None)).BucketCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task UnsupportedListOption_ReturnsNotImplemented()
    {
        var root = CreateRoot("list-option");
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            var credentials = await CreateApplicationAsync(host.AdminAddress);
            using var client = new HttpClient { BaseAddress = host.DataAddress };
            using (var create = SignRequest(HttpMethod.Put, "/list-option-bucket", host.DataAddress,
                       credentials.AccessKeyId, credentials.SecretAccessKey, []))
            using (var created = await client.SendAsync(create))
            {
                Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            }

            using var request = SignRequest(HttpMethod.Get, "/list-option-bucket?list-type=2&fetch-owner=true", host.DataAddress,
                credentials.AccessKeyId, credentials.SecretAccessKey, []);

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
            Assert.Contains("<Code>NotImplemented</Code>", body, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task UnknownBucket_ReturnsNoSuchBucket()
    {
        var root = CreateRoot("missing-bucket");
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            var credentials = await CreateApplicationAsync(host.AdminAddress);
            using var client = new HttpClient { BaseAddress = host.DataAddress };
            using var request = SignRequest(HttpMethod.Get, "/missing-bucket/object", host.DataAddress,
                credentials.AccessKeyId, credentials.SecretAccessKey, []);

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("<Code>NoSuchBucket</Code>", body, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task InvalidBucketName_ReturnsInvalidBucketNameBeforeCreation()
    {
        var root = CreateRoot("invalid-bucket-name");
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            var credentials = await CreateApplicationAsync(host.AdminAddress);
            using var client = new HttpClient { BaseAddress = host.DataAddress };
            using var request = SignRequest(HttpMethod.Put, "/Invalid_bucket", host.DataAddress,
                credentials.AccessKeyId, credentials.SecretAccessKey, []);

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("<Code>InvalidBucketName</Code>", body, StringComparison.Ordinal);
            Assert.True(response.Headers.Contains("x-amz-request-id"));
            Assert.Equal(0, Assert.Single(await host.Storage.ListApplicationsAsync(CancellationToken.None)).BucketCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task PutBucketWithTrailingSlash_IsAcceptedForAwsSdkCompatibility()
    {
        var root = CreateRoot("empty-object-key");
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            var credentials = await CreateApplicationAsync(host.AdminAddress);
            using var client = new HttpClient { BaseAddress = host.DataAddress };
            using var request = SignRequest(HttpMethod.Put, "/empty-key-bucket/", host.DataAddress,
                credentials.AccessKeyId, credentials.SecretAccessKey, []);

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("/empty-key-bucket", response.Headers.Location?.ToString());
            Assert.Empty(body);
            Assert.Equal(1, Assert.Single(await host.Storage.ListApplicationsAsync(CancellationToken.None)).BucketCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RequestOlderThanFifteenMinutes_ReturnsRequestTimeTooSkewed()
    {
        var root = CreateRoot("clock");
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            var credentials = await CreateApplicationAsync(host.AdminAddress);
            using var client = new HttpClient { BaseAddress = host.DataAddress };
            using var request = SignRequest(HttpMethod.Put, "/stale-request-bucket", host.DataAddress, credentials.AccessKeyId, credentials.SecretAccessKey, [],
                signingTime: DateTimeOffset.UtcNow.AddMinutes(-16));

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("<Code>RequestTimeTooSkewed</Code>", body, StringComparison.Ordinal);
            Assert.Equal(0, Assert.Single(await host.Storage.ListApplicationsAsync(CancellationToken.None)).BucketCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task UnknownAccessKeyId_ReturnsInvalidAccessKeyId()
    {
        var root = CreateRoot("unknown-key");
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            var credentials = await CreateApplicationAsync(host.AdminAddress);
            using var client = new HttpClient { BaseAddress = host.DataAddress };
            using var request = SignRequest(HttpMethod.Put, "/unknown-key-bucket", host.DataAddress, credentials.AccessKeyId, credentials.SecretAccessKey, []);
            var authorization = request.Headers.GetValues("Authorization").Single();
            var unknownKey = "OS" + new string('A', 24);
            request.Headers.Remove("Authorization");
            request.Headers.TryAddWithoutValidation("Authorization", authorization.Replace(credentials.AccessKeyId, unknownKey, StringComparison.Ordinal));

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("<Code>InvalidAccessKeyId</Code>", body, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ChangedSignedDateHeader_ReturnsSignatureDoesNotMatch()
    {
        var root = CreateRoot("signed-header");
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            var credentials = await CreateApplicationAsync(host.AdminAddress);
            using var client = new HttpClient { BaseAddress = host.DataAddress };
            using var request = SignRequest(HttpMethod.Put, "/signed-header-bucket", host.DataAddress, credentials.AccessKeyId, credentials.SecretAccessKey, []);
            var signedDate = request.Headers.GetValues("x-amz-date").Single();
            var changedDate = DateTimeOffset.ParseExact(signedDate, "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture)
                .AddSeconds(1).ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
            request.Headers.Remove("x-amz-date");
            request.Headers.TryAddWithoutValidation("x-amz-date", changedDate);

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("<Code>SignatureDoesNotMatch</Code>", body, StringComparison.Ordinal);
            Assert.Equal(0, Assert.Single(await host.Storage.ListApplicationsAsync(CancellationToken.None)).BucketCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task UnsignedPayloadAcceptedOnlyWhenTrustedProxyReportsHttps()
    {
        var root = CreateRoot("unsigned-payload");
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root, ["127.0.0.1"]));
            var credentials = await CreateApplicationAsync(host.AdminAddress);
            using var client = new HttpClient { BaseAddress = host.DataAddress };
            using var request = SignRequest(HttpMethod.Put, "/unsigned-payload-bucket", host.DataAddress,
                credentials.AccessKeyId, credentials.SecretAccessKey, [], unsignedPayload: true);
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task UnsignedPayloadOverPlainHttp_ReturnsAccessDenied()
    {
        var root = CreateRoot("unsigned-http");
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            var credentials = await CreateApplicationAsync(host.AdminAddress);
            using var client = new HttpClient { BaseAddress = host.DataAddress };
            using var request = SignRequest(HttpMethod.Put, "/unsigned-http-bucket", host.DataAddress,
                credentials.AccessKeyId, credentials.SecretAccessKey, [], unsignedPayload: true);

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("<Code>AccessDenied</Code>", body, StringComparison.Ordinal);
            Assert.Equal(0, Assert.Single(await host.Storage.ListApplicationsAsync(CancellationToken.None)).BucketCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ForeignBucketAuthorizationPrecedesUnsupportedFeatureErrors()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "optistorage-sigv4-owner-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            var owner = await CreateApplicationAsync(host.AdminAddress);
            var reader = await CreateApplicationAsync(host.AdminAddress);
            using var client = new HttpClient { BaseAddress = host.DataAddress };
            using var create = SignRequest(HttpMethod.Put, "/private-owner-bucket", host.DataAddress, owner.AccessKeyId, owner.SecretAccessKey, []);
            using var created = await client.SendAsync(create);
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

            using var request = SignRequest(HttpMethod.Get, "/private-owner-bucket/object", host.DataAddress, reader.AccessKeyId, reader.SecretAccessKey, []);
            request.Headers.TryAddWithoutValidation("x-amz-acl", "private");
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("<Code>AccessDenied</Code>", body, StringComparison.Ordinal);

            using var createRequest = SignRequest(HttpMethod.Put, "/private-owner-bucket", host.DataAddress,
                reader.AccessKeyId, reader.SecretAccessKey, []);
            createRequest.Headers.TryAddWithoutValidation("x-amz-acl", "private");
            using var createResponse = await client.SendAsync(createRequest);
            var createBody = await createResponse.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Forbidden, createResponse.StatusCode);
            Assert.Contains("<Code>AccessDenied</Code>", createBody, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UnsupportedQueryOption_FailsBeforeBucketCreation()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "optistorage-sigv4-feature-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            var credentials = await CreateApplicationAsync(host.AdminAddress);
            using var client = new HttpClient { BaseAddress = host.DataAddress };
            using var request = SignRequest(HttpMethod.Put, "/unsupported-feature-bucket?versionId=latest", host.DataAddress, credentials.AccessKeyId, credentials.SecretAccessKey, []);

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
            Assert.Contains("<Code>NotImplemented</Code>", body, StringComparison.Ordinal);
            Assert.Equal(0, Assert.Single(await host.Storage.ListApplicationsAsync(CancellationToken.None)).BucketCount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CredentialRotationCutsOffOldSecretAndDeactivationBlocksNewSecret()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "optistorage-sigv4-rotate-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            var credentials = await CreateApplicationAsync(host.AdminAddress);
            using var client = new HttpClient { BaseAddress = host.DataAddress };
            using var create = SignRequest(HttpMethod.Put, "/rotate-bucket", host.DataAddress, credentials.AccessKeyId, credentials.SecretAccessKey, []);
            using var created = await client.SendAsync(create);
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

            using var admin = CreateAdminClient(host.AdminAddress);
            using var rotateResponse = await admin.PostAsync($"/admin/applications/{credentials.AccessKeyId}/rotate", content: null);
            using var rotated = JsonDocument.Parse(await rotateResponse.Content.ReadAsStringAsync());
            var newSecret = rotated.RootElement.GetProperty("secretAccessKey").GetString()!;

            using var oldRequest = SignRequest(HttpMethod.Put, "/rotate-bucket", host.DataAddress, credentials.AccessKeyId, credentials.SecretAccessKey, []);
            using var oldResponse = await client.SendAsync(oldRequest);
            var oldBody = await oldResponse.Content.ReadAsStringAsync();
            using var newRequest = SignRequest(HttpMethod.Put, "/rotate-bucket", host.DataAddress, credentials.AccessKeyId, newSecret, []);
            using var newResponse = await client.SendAsync(newRequest);
            var newBody = await newResponse.Content.ReadAsStringAsync();

            using var deactivated = await admin.DeleteAsync($"/admin/applications/{credentials.AccessKeyId}");
            using var inactiveRequest = SignRequest(HttpMethod.Put, "/rotate-bucket", host.DataAddress, credentials.AccessKeyId, newSecret, []);
            using var inactiveResponse = await client.SendAsync(inactiveRequest);
            var inactiveBody = await inactiveResponse.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Forbidden, oldResponse.StatusCode);
            Assert.Contains("<Code>SignatureDoesNotMatch</Code>", oldBody, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Conflict, newResponse.StatusCode);
            Assert.Contains("<Code>BucketAlreadyOwnedByYou</Code>", newBody, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, inactiveResponse.StatusCode);
            Assert.Contains("<Code>AccessDenied</Code>", inactiveBody, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AdminQuotaUpdate_TakesEffectForTheNextPut()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "optistorage-admin-quota-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            var credentials = await CreateApplicationAsync(host.AdminAddress);
            using var dataClient = new HttpClient { BaseAddress = host.DataAddress };
            using var admin = CreateAdminClient(host.AdminAddress);
            using var create = SignRequest(HttpMethod.Put, "/quota-bucket", host.DataAddress, credentials.AccessKeyId, credentials.SecretAccessKey, []);
            using var created = await dataClient.SendAsync(create);
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            using var firstPut = SignRequest(HttpMethod.Put, "/quota-bucket/first", host.DataAddress, credentials.AccessKeyId, credentials.SecretAccessKey, "data"u8.ToArray());
            using var firstResponse = await dataClient.SendAsync(firstPut);
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

            using var quotaResponse = await admin.PatchAsync($"/admin/applications/{credentials.AccessKeyId}",
                new StringContent("{\"quotaBytes\":3}", Encoding.UTF8, "application/json"));
            using var blockedPut = SignRequest(HttpMethod.Put, "/quota-bucket/second", host.DataAddress, credentials.AccessKeyId, credentials.SecretAccessKey, "x"u8.ToArray());
            using var blockedResponse = await dataClient.SendAsync(blockedPut);
            var blockedBody = await blockedResponse.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, quotaResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, blockedResponse.StatusCode);
            Assert.Contains("<Code>InvalidArgument</Code>", blockedBody, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SignedPayloadChangedAfterSigning_IsRejectedWithoutPublishingObject()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "optistorage-sigv4-body-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            var credentials = await CreateApplicationAsync(host.AdminAddress);
            using var client = new HttpClient { BaseAddress = host.DataAddress };
            using var createBucket = SignRequest(HttpMethod.Put, "/digest-bucket", host.DataAddress, credentials.AccessKeyId, credentials.SecretAccessKey, []);
            using var created = await client.SendAsync(createBucket);
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

            using var request = SignRequest(HttpMethod.Put, "/digest-bucket/object", host.DataAddress, credentials.AccessKeyId, credentials.SecretAccessKey, "expected"u8.ToArray());
            request.Content = new ByteArrayContent("tampered"u8.ToArray());
            using var rejected = await client.SendAsync(request);
            var rejectBody = await rejected.Content.ReadAsStringAsync();

            using var missingRequest = SignRequest(HttpMethod.Get, "/digest-bucket/object", host.DataAddress, credentials.AccessKeyId, credentials.SecretAccessKey, []);
            using var missing = await client.SendAsync(missingRequest);
            var missingBody = await missing.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            Assert.Contains("<Code>SignatureDoesNotMatch</Code>", rejectBody, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Contains("<Code>NoSuchKey</Code>", missingBody, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CorruptCommittedObject_ReturnsSafeInternalError()
    {
        var root = CreateRoot("runtime-integrity");
        const string payload = "private object bytes";
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            var credentials = await CreateApplicationAsync(host.AdminAddress);
            using var client = new HttpClient { BaseAddress = host.DataAddress };
            using var createBucket = SignRequest(HttpMethod.Put, "/integrity-bucket", host.DataAddress,
                credentials.AccessKeyId, credentials.SecretAccessKey, []);
            using var bucketResponse = await client.SendAsync(createBucket);
            Assert.Equal(HttpStatusCode.OK, bucketResponse.StatusCode);

            using var put = SignRequest(HttpMethod.Put, "/integrity-bucket/object", host.DataAddress,
                credentials.AccessKeyId, credentials.SecretAccessKey, Encoding.UTF8.GetBytes(payload));
            using var putResponse = await client.SendAsync(put);
            Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

            var objectPath = Directory.EnumerateFiles(
                    Path.Combine(root, "buckets", "integrity-bucket", "objects"),
                    "*.obj",
                    SearchOption.AllDirectories)
                .Single();
            var record = await File.ReadAllBytesAsync(objectPath);
            record[^1] ^= 0xff;
            await File.WriteAllBytesAsync(objectPath, record);

            using var get = SignRequest(HttpMethod.Get, "/integrity-bucket/object", host.DataAddress,
                credentials.AccessKeyId, credentials.SecretAccessKey, []);
            using var response = await client.SendAsync(get);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Contains("<Code>InternalError</Code>", body, StringComparison.Ordinal);
            Assert.DoesNotContain(payload, body, StringComparison.Ordinal);
            Assert.DoesNotContain(credentials.SecretAccessKey, body, StringComparison.Ordinal);
            Assert.True(response.Headers.Contains("x-amz-request-id"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CorrectSigV4Request_CreatesBucket()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "optistorage-sigv4-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await StorageServiceHost.StartAsync(ServerTestSupport.Settings(root));
            using var admin = ServerTestSupport.CreateAdminClient(host.AdminAddress);
            using var appResponse = await admin.PostAsync("/admin/applications", new StringContent("{\"name\":\"sdk-test\",\"quotaBytes\":4096}", Encoding.UTF8, "application/json"));
            using var appJson = JsonDocument.Parse(await appResponse.Content.ReadAsStringAsync());
            var credentials = appJson.RootElement;
            var accessKeyId = credentials.GetProperty("accessKeyId").GetString()!;
            var secretAccessKey = credentials.GetProperty("secretAccessKey").GetString()!;

            using var s3 = new HttpClient { BaseAddress = host.DataAddress };
            using var request = SignRequest(HttpMethod.Put, "/sigv4-bucket", host.DataAddress, accessKeyId, secretAccessKey, []);
            using var response = await s3.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("/sigv4-bucket", response.Headers.Location?.ToString());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static HttpRequestMessage SignRequest(
        HttpMethod method,
        string path,
        Uri endpoint,
        string accessKeyId,
        string secretAccessKey,
        byte[] body,
        string region = "us-east-1",
        DateTimeOffset? signingTime = null,
        bool unsignedPayload = false)
    {
        var timestamp = (signingTime ?? DateTimeOffset.UtcNow).ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var date = timestamp[..8];
        var payloadHash = unsignedPayload ? "UNSIGNED-PAYLOAD" : Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var queryStart = path.IndexOf('?');
        var canonicalPath = queryStart < 0 ? path : path[..queryStart];
        var canonicalQuery = queryStart < 0 ? string.Empty : string.Join('&', path[(queryStart + 1)..].Split('&').Order(StringComparer.Ordinal));
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalHeaders = $"host:{endpoint.Authority}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{timestamp}\n";
        var canonicalRequest = string.Join('\n', method.Method, canonicalPath, canonicalQuery, canonicalHeaders, signedHeaders, payloadHash);
        var canonicalHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant();
        var scope = $"{date}/{region}/s3/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{timestamp}\n{scope}\n{canonicalHash}";
        var dateKey = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), date);
        var regionKey = Hmac(dateKey, region);
        var serviceKey = Hmac(regionKey, "s3");
        var signingKey = Hmac(serviceKey, "aws4_request");
        var signature = Convert.ToHexString(Hmac(signingKey, stringToSign)).ToLowerInvariant();

        var request = new HttpRequestMessage(method, path)
        {
            Content = new ByteArrayContent(body)
        };
        request.Headers.TryAddWithoutValidation("x-amz-date", timestamp);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("Authorization", $"AWS4-HMAC-SHA256 Credential={accessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
        return request;
    }

    private static async Task<(string AccessKeyId, string SecretAccessKey)> CreateApplicationAsync(Uri adminAddress)
    {
        using var admin = ServerTestSupport.CreateAdminClient(adminAddress);
        using var response = await admin.PostAsync("/admin/applications", new StringContent("{\"name\":\"digest\",\"quotaBytes\":4096}", Encoding.UTF8, "application/json"));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (
            document.RootElement.GetProperty("accessKeyId").GetString()!,
            document.RootElement.GetProperty("secretAccessKey").GetString()!);
    }

    private static HttpClient CreateAdminClient(Uri address)
    {
        return ServerTestSupport.CreateAdminClient(address);
    }

    private static string CreateRoot(string purpose) =>
        Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), $"optistorage-sigv4-{purpose}-{Guid.NewGuid():N}");

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
}
