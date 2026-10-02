using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using XStorage.Server;

namespace XStorage.SdkCompatibility.Tests;

public sealed class S3SdkAcceptanceTests
{
    private static readonly string AdminToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    [Fact]
    public async Task AwsSdkPerformsSupportedOperationsForEachApplicationAndEnforcesIsolation()
    {
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "xstorage-sdk-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await StorageServiceHost.StartAsync(new StorageServiceSettings(
                root, "http://127.0.0.1:0", "http://127.0.0.1:0", AdminToken,
                RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32)));
            var mainCredentials = await CreateApplicationAsync(host.AdminAddress, "primary", 4096);
            var otherCredentials = await CreateApplicationAsync(host.AdminAddress, "secondary", 10);
            using var primary = CreateS3Client(host.DataAddress, mainCredentials);
            using var secondary = CreateS3Client(host.DataAddress, otherCredentials);
            var bucket = "sdk-acceptance-" + Guid.NewGuid().ToString("N")[..12];

            await primary.PutBucketAsync(new PutBucketRequest { BucketName = bucket });

            var objectBytes = Encoding.UTF8.GetBytes("sdk body with exact bytes");
            var objectKey = "nested/percent%/café object.txt";
            await PutAsync(primary, bucket, objectKey, objectBytes);
            var first = await primary.GetObjectAsync(bucket, objectKey);
            string firstText;
            await using (first.ResponseStream.ConfigureAwait(false))
            using (var output = new MemoryStream())
            {
                await first.ResponseStream.CopyToAsync(output);
                firstText = Encoding.UTF8.GetString(output.ToArray());
            }

            Assert.Equal("sdk body with exact bytes", firstText);
            Assert.Equal(objectBytes.Length, (await primary.GetObjectMetadataAsync(bucket, objectKey)).ContentLength);

            await PutAsync(primary, bucket, "page/a", "a"u8.ToArray());
            await PutAsync(primary, bucket, "page/b", "b"u8.ToArray());
            await PutAsync(primary, bucket, "page/c", "c"u8.ToArray());
            var pageOne = await primary.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "page/", MaxKeys = 2 });
            var pageTwo = await primary.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = "page/",
                MaxKeys = 2,
                ContinuationToken = pageOne.NextContinuationToken
            });
            Assert.Equal(2, pageOne.S3Objects.Count);
            Assert.Single(pageTwo.S3Objects);

            var replacement = Encoding.UTF8.GetBytes("replacement body");
            await PutAsync(primary, bucket, objectKey, replacement);
            var overwritten = await primary.GetObjectAsync(bucket, objectKey);
            await using (overwritten.ResponseStream.ConfigureAwait(false))
            using (var output = new MemoryStream())
            {
                await overwritten.ResponseStream.CopyToAsync(output);
                Assert.Equal(replacement, output.ToArray());
            }

            await Assert.ThrowsAsync<AmazonS3Exception>(() => secondary.GetObjectAsync(bucket, objectKey));
            await primary.DeleteObjectAsync(bucket, objectKey);
            await primary.DeleteObjectAsync(bucket, objectKey);
            await Assert.ThrowsAsync<NoSuchKeyException>(() => primary.GetObjectAsync(bucket, objectKey));

            var tinyBucket = bucket + "-tiny";
            await secondary.PutBucketAsync(new PutBucketRequest { BucketName = tinyBucket });
            var smallObjectKey = "secondary-object";
            var smallObjectBytes = "small"u8.ToArray();
            await PutAsync(secondary, tinyBucket, smallObjectKey, smallObjectBytes);
            var secondaryDownload = await secondary.GetObjectAsync(tinyBucket, smallObjectKey);
            await using (secondaryDownload.ResponseStream.ConfigureAwait(false))
            using (var output = new MemoryStream())
            {
                await secondaryDownload.ResponseStream.CopyToAsync(output);
                Assert.Equal(smallObjectBytes, output.ToArray());
            }

            Assert.Equal(smallObjectBytes.Length, (await secondary.GetObjectMetadataAsync(tinyBucket, smallObjectKey)).ContentLength);
            var secondaryListing = await secondary.ListObjectsV2Async(new ListObjectsV2Request { BucketName = tinyBucket });
            Assert.Contains(secondaryListing.S3Objects, item => item.Key == smallObjectKey);
            await secondary.DeleteObjectAsync(tinyBucket, smallObjectKey);
            await secondary.DeleteObjectAsync(tinyBucket, smallObjectKey);
            await Assert.ThrowsAsync<NoSuchKeyException>(() => secondary.GetObjectAsync(tinyBucket, smallObjectKey));

            var tooLarge = new MemoryStream(new byte[11], writable: false);
            var quotaError = await Assert.ThrowsAsync<AmazonS3Exception>(() => secondary.PutObjectAsync(new PutObjectRequest
            {
                BucketName = tinyBucket,
                Key = "too-large",
                InputStream = tooLarge,
                AutoCloseStream = false,
                UseChunkEncoding = false
            }));
            Assert.Equal("InvalidArgument", quotaError.ErrorCode);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<Credentials> CreateApplicationAsync(Uri adminAddress, string name, long quota)
    {
        using var client = new HttpClient { BaseAddress = adminAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        using var response = await client.PostAsync("/admin/applications", new StringContent(
            JsonSerializer.Serialize(new { name, quotaBytes = quota }), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Credentials>())!;
    }

    private static AmazonS3Client CreateS3Client(Uri endpoint, Credentials credentials)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = endpoint.ToString().TrimEnd('/'),
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };
        return new AmazonS3Client(new BasicAWSCredentials(credentials.AccessKeyId, credentials.SecretAccessKey), config);
    }

    private static Task<PutObjectResponse> PutAsync(AmazonS3Client client, string bucket, string key, byte[] bytes)
    {
        return client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = new MemoryStream(bytes, writable: false),
            AutoCloseStream = true,
            UseChunkEncoding = false,
            ContentType = "text/plain"
        });
    }

    private sealed record Credentials(string AccessKeyId, string SecretAccessKey);
}
