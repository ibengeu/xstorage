# XStorage

XStorage is a single-node object storage server for applications. It stores objects on a local filesystem and runs on .NET 10 on Linux, macOS, or Windows. Each application gets separate credentials and a byte quota. A bucket groups an application's objects.

XStorage provides a limited S3-compatible API for object operations and a separate [admin API](#provision-applications) for application setup. It does not connect to AWS. Connect another project with the [optional .NET client](#optional-net-client-library) or an S3-compatible client. See the [supported operations](#s3-api-subset); XStorage does not implement all of Amazon S3. For the architecture article and implementation plans, see the [XStorage documentation](https://app.notion.com/p/3eda5d68fcdd81798780da0c8d5689c7).

To connect a project:

1. [Start the server](#run-locally).
2. [Create an application](#provision-applications) and save its access key and secret.
3. [Connect your project](#connect-another-project) to the S3 endpoint.

## Requirements

- Linux, macOS, or Windows on one host.
- .NET 10 SDK to build or run from source.
- Linux or macOS: a local filesystem that supports atomic same-directory rename and file and directory flush operations.
- Windows: a local NTFS volume that supports atomic same-volume rename and write-through operations.
- A dedicated service account that owns the data root.
- A reverse proxy that terminates HTTPS for network clients.

XStorage does not support network filesystems. On Windows, it protects data with access control lists (ACLs) for the service identity and LocalSystem. Review the [durability guarantee](#durability-guarantee) before storing important data.

## Run locally

From the repository root, set the four required environment variables and start the server. These commands generate keys for a local trial. For persistent data, store the keys in a protected secret store and reuse the same values after each restart.

Linux or macOS:

```bash
export XSTORAGE_DATA_ROOT="$HOME/xstorage-data"
export XSTORAGE_ADMIN_TOKEN="$(openssl rand -hex 32)"
export XSTORAGE_APP_ENCRYPTION_KEY="$(openssl rand -base64 32)"
export XSTORAGE_CONTINUATION_TOKEN_KEY="$(openssl rand -base64 32)"
dotnet run --project src/XStorage.Server
```

Windows PowerShell:

```powershell
$env:XSTORAGE_DATA_ROOT = Join-Path $env:LOCALAPPDATA 'XStorage\data'
$env:XSTORAGE_ADMIN_TOKEN = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:XSTORAGE_APP_ENCRYPTION_KEY = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:XSTORAGE_CONTINUATION_TOKEN_KEY = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet run --project .\src\XStorage.Server
```

The S3 endpoint listens on `http://127.0.0.1:9000`. The admin API listens on `http://127.0.0.1:9001`. Keep the server running while clients use it. Press Ctrl+C to stop both listeners. Only one server process can use a data root at a time.

## Configuration

XStorage reads these environment variables when it starts:

| Variable | Required | Purpose / default |
| --- | --- | --- |
| `XSTORAGE_DATA_ROOT` | Yes | Absolute path to the service-owned data root. |
| `XSTORAGE_ADMIN_TOKEN` | Yes | Static bearer token with at least 32 printable ASCII characters and no spaces. |
| `XSTORAGE_APP_ENCRYPTION_KEY` | Yes | Base64-encoded 32-byte key used to encrypt application secrets at rest. |
| `XSTORAGE_CONTINUATION_TOKEN_KEY` | Yes | Base64-encoded 32-byte key used to sign list continuation tokens. |
| `XSTORAGE_DATA_URL` | No | S3 listener URL. Default: `http://127.0.0.1:9000`. |
| `XSTORAGE_ADMIN_URL` | No | Admin listener URL. Default: `http://127.0.0.1:9001`. |
| `XSTORAGE_MAX_OBJECT_BYTES` | No | Maximum object size. Default: `26214400` bytes (25 MiB). |
| `XSTORAGE_MAX_OBJECTS_PER_APPLICATION` | No | Maximum committed objects per application. Default: `10000`. Empty objects count. |
| `XSTORAGE_MAX_BUCKETS_PER_APPLICATION` | No | Maximum owned buckets per application. Default: `100`. |
| `XSTORAGE_TRUSTED_PROXIES` | No | Comma-separated proxy IPs trusted to set forwarded scheme and client address headers. Default: none. |

Both encryption keys must decode to exactly 32 bytes. For persistent deployments, keep all four required values unchanged between restarts. XStorage needs the application encryption key to read stored application credentials. Store secrets in a secret manager or protected service configuration. Do not put them in source control, command arguments, logs, or browser code.

XStorage rejects relative paths, filesystem roots, unsafe data trees, invalid key lengths, invalid limits, and listener addresses that share the same host and port. It also rejects symbolic links and other reparse points inside the data tree. On Linux and macOS, only the data-root owner can access the data. On Windows, protected ACLs grant access to the service identity and LocalSystem.

XStorage releases its data-root lock during normal shutdown. On Unix, it also handles SIGTERM.

## Network and TLS

Keep both listeners on loopback by default. Do not expose the admin port through the reverse proxy used for S3 traffic. Operators can use a local script or an SSH tunnel to reach the admin API.

For network access, terminate HTTPS at a trusted reverse proxy. Configure it to preserve the original `Host`, path, raw query, and signed headers. Set `XSTORAGE_TRUSTED_PROXIES` to the proxy's IP addresses. XStorage trusts `X-Forwarded-Proto` only from those addresses. Requests that use `UNSIGNED-PAYLOAD` must use HTTPS, either directly or as reported by a trusted proxy.

If either listener uses a non-loopback address, requests must use HTTPS after XStorage applies trusted forwarded headers. Do not expose the admin listener on a public interface. If you bind it to a network interface, use HTTPS termination and restrict access to the operator network.

## S3 API subset

Requests use path-style addressing and AWS Signature Version 4 (SigV4). Sign requests with region `us-east-1`, service `s3`, and the application's static credentials. Include signed `host` and `x-amz-date` headers. For the payload, use its lowercase SHA-256 hash or `UNSIGNED-PAYLOAD`. XStorage rejects request timestamps that differ from server time by more than 15 minutes.

| Operation | Route | Result |
| --- | --- | --- |
| Create bucket | `PUT /{bucket}` | Creates an empty bucket. The body may contain only the `us-east-1` location constraint. |
| Put object | `PUT /{bucket}/{key}` | Writes or replaces one whole object and returns a quoted MD5 ETag. |
| Get object | `GET /{bucket}/{key}` | Returns object bytes and metadata. |
| Head object | `HEAD /{bucket}/{key}` | Returns metadata without a body. |
| Delete object | `DELETE /{bucket}/{key}` | Removes the object. Deleting a missing key succeeds. |
| ListObjectsV2 | `GET /{bucket}?list-type=2` | Returns one page of S3 XML results. |

XStorage decodes each object key once. An encoded slash becomes a slash in the key. Keys are case-sensitive and use their exact UTF-8 bytes. Listings sort keys by UTF-8 byte order. A delimiter prefix counts as a page entry. Continuation tokens are signed, URL-safe, and tied to the list request. They remain valid after a restart if the continuation-token key stays the same. Separate list requests do not share a snapshot. The CreateBucket operation also accepts `PUT /{bucket}/`, which the unmodified AWS SDK for .NET sends for path-style requests.

Every S3 response includes `x-amz-request-id`. Errors use S3 XML, except for HEAD requests, which return no error body. XStorage checks bucket ownership before it looks up an object key. A bucket owned by another application returns `AccessDenied`; an unknown bucket returns `NoSuchBucket`.

These responses let callers distinguish an unknown bucket from a bucket owned by another application. XStorage never returns another application's object keys or metadata.

XStorage rejects unsupported operations and request features. These include range and conditional requests, presigned URLs, virtual-hosted addressing, ACLs, object metadata headers, storage-class and tagging headers, request checksums and checksum trailers, session tokens, and SigV4 streaming chunks. It also rejects unknown query options and response-override queries. The AWS SDK's `x-amz-api-version` header is accepted as protocol metadata only; it does not enable an S3 operation.

XStorage also does not support bucket deletion, multipart or resumable uploads, versioning, lifecycle rules, replication, sharding, bucket sharing between applications, public object URLs, or AWS S3 control-plane calls.

## Provision applications

Create one XStorage application for each consuming project or trust boundary. Each application gets a separate access key, secret, and quota. Names are display labels and can repeat; the access key ID identifies the application. The admin API accepts only the bearer token configured at startup.

Every admin request must include `Authorization: Bearer <admin-token>`. The admin API uses the `XSTORAGE_ADMIN_URL` listener. Its default address is `http://127.0.0.1:9001`.

| Method and route | Purpose | Request / response |
| --- | --- | --- |
| `POST /admin/applications` | Create an application. | Send `{"name":"billing","quotaBytes":1073741824}`. Returns the access key and secret once. |
| `GET /admin/applications` | List applications. | Returns IDs, names, quotas, usage, bucket counts, and active status. Does not return secrets. |
| `POST /admin/applications/{accessKeyId}/rotate` | Rotate an application's secret. | Returns the new access key ID and secret. |
| `PATCH /admin/applications/{accessKeyId}` | Change an application's quota. | Send `{"quotaBytes":2147483648}`. |
| `DELETE /admin/applications/{accessKeyId}` | Deactivate an application. | Returns `204 No Content`. Existing objects remain stored. |

For Unix scripts, put `Authorization: Bearer <token>` in a protected file outside the repository and set its permissions to `0600`. Pass the file path to curl with `--header @/path/to/admin-header`. This keeps the token out of the command arguments. The paths below are examples; create the protected header file and credential directory first. Create and rotate responses contain application secrets, so save them in a protected credential store.

Create an application with a display name and a quota in bytes. This example requests a 1 GiB quota:

```bash
umask 077
curl --silent --show-error --fail-with-body \
  --header @/run/secrets/xstorage-admin-header \
  --header 'Content-Type: application/json' \
  --data '{"name":"billing","quotaBytes":1073741824}' \
  http://127.0.0.1:9001/admin/applications \
  > /secure/credential-store/billing.json
```

On Windows, load the admin token from a protected store into your PowerShell session. This example creates an application without placing the token on an external command line:

```powershell
$headers = @{ Authorization = "Bearer $env:XSTORAGE_ADMIN_TOKEN" }
$body = @{ name = 'billing'; quotaBytes = 1073741824 } | ConvertTo-Json
$credentials = Invoke-RestMethod -Method Post `
    -Uri 'http://127.0.0.1:9001/admin/applications' `
    -Headers $headers -ContentType 'application/json' -Body $body
```

Save `$credentials.accessKeyId` and `$credentials.secretAccessKey` in the consuming application's protected secret store. The server returns the secret only at creation or rotation.

List applications:

```bash
curl --silent --show-error --fail-with-body \
  --header @/run/secrets/xstorage-admin-header \
  http://127.0.0.1:9001/admin/applications
```

Rotate an application's secret:

```bash
curl --silent --show-error --fail-with-body \
  --header @/run/secrets/xstorage-admin-header \
  --request POST \
  http://127.0.0.1:9001/admin/applications/ACCESS_KEY_ID/rotate \
  > /secure/credential-store/billing-new.json
```

Secret rotation takes effect immediately. A request using the old secret succeeds only if SigV4 verification finishes before the rotation response. Other requests with the old secret fail. Update every running consumer before you rotate the secret. XStorage provides no overlap period.

Set a quota:

```bash
curl --silent --show-error --fail-with-body \
  --header @/run/secrets/xstorage-admin-header \
  --header 'Content-Type: application/json' \
  --request PATCH \
  --data '{"quotaBytes":2147483648}' \
  http://127.0.0.1:9001/admin/applications/ACCESS_KEY_ID
```

Deactivate an application:

```bash
curl --silent --show-error --fail-with-body \
  --header @/run/secrets/xstorage-admin-header \
  --request DELETE \
  http://127.0.0.1:9001/admin/applications/ACCESS_KEY_ID
```

Deactivation blocks future requests from the application but keeps its buckets and objects. This release does not permanently delete application data.

## Connect another project

Use the `accessKeyId` and `secretAccessKey` returned when you create the application. Configure the client to use the **S3 endpoint** on port `9000`. Do not use the admin endpoint on port `9001` for object requests. Create a bucket before uploading objects. Store credentials in the consuming project's secret store or server environment. Never put them in browser code.

For a .NET 10 project, choose one of these clients:

- Add a project reference to `src/XStorage.Client/XStorage.Client.csproj` and use the [typed client](#optional-net-client-library).
- Add `AWSSDK.S3` and use the [AWS SDK example](#aws-sdk-for-net). This repository tests version `4.0.103.4`.

For other languages, configure an S3 client with a custom service URL, path-style addressing, region `us-east-1`, and the application's static credentials. The client must support the [operations listed above](#s3-api-subset). Use HTTPS for network connections.

## AWS SDK for .NET

A .NET application can use `AWSSDK.S3` with a custom service URL, path-style addressing, region `us-east-1`, and the application's static credentials. For remote access, set the service URL to your reverse proxy's HTTPS address:

```c#
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

var serviceUrl = Environment.GetEnvironmentVariable("XSTORAGE_SERVICE_URL")
    ?? throw new InvalidOperationException("Missing storage service URL.");
var accessKeyId = Environment.GetEnvironmentVariable("XSTORAGE_ACCESS_KEY_ID")
    ?? throw new InvalidOperationException("Missing storage access key ID.");
var secretAccessKey = Environment.GetEnvironmentVariable("XSTORAGE_SECRET_ACCESS_KEY")
    ?? throw new InvalidOperationException("Missing storage secret access key.");

var config = new AmazonS3Config
{
    ServiceURL = serviceUrl,
    ForcePathStyle = true,
    AuthenticationRegion = "us-east-1",
    RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
    ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
};

using var client = new AmazonS3Client(
    new BasicAWSCredentials(accessKeyId, secretAccessKey), config);

await client.PutBucketAsync(new PutBucketRequest { BucketName = "billing-files" });

await using var input = File.OpenRead("invoice.pdf");
await client.PutObjectAsync(new PutObjectRequest
{
    BucketName = "billing-files",
    Key = "invoices/2026/invoice.pdf",
    InputStream = input,
    AutoCloseStream = false,
    UseChunkEncoding = false,
    ContentType = "application/pdf"
});

using var download = await client.GetObjectAsync("billing-files", "invoices/2026/invoice.pdf");
await using var output = File.Create("downloaded-invoice.pdf");
await download.ResponseStream.CopyToAsync(output);
```

For PUT requests, the upload stream must be readable, seekable, positioned at the start, and have a known length. The caller owns the upload stream and must keep it open until the SDK call completes. Dispose each get response to close its response stream.

## Optional .NET client library

`XStorage.Client` provides the `IObjectStore` interface and an `S3ObjectStore` implementation backed by `AWSSDK.S3`. The client sends HTTP requests to the standalone server; it does not access the server's filesystem directly. Add a project reference to `src/XStorage.Client/XStorage.Client.csproj` from your .NET 10 application.

```c#
using XStorage.Client;

var serviceUrl = Environment.GetEnvironmentVariable("XSTORAGE_SERVICE_URL")
    ?? throw new InvalidOperationException("Missing storage service URL.");
var accessKeyId = Environment.GetEnvironmentVariable("XSTORAGE_ACCESS_KEY_ID")
    ?? throw new InvalidOperationException("Missing storage access key ID.");
var secretAccessKey = Environment.GetEnvironmentVariable("XSTORAGE_SECRET_ACCESS_KEY")
    ?? throw new InvalidOperationException("Missing storage secret access key.");

await using var store = new S3ObjectStore(new ObjectStoreClientOptions(
    serviceUrl, accessKeyId, secretAccessKey));

await store.CreateBucketAsync("billing-files");

await using var upload = File.OpenRead("invoice.pdf");
await store.PutObjectAsync(
    "billing-files", "invoices/2026/invoice.pdf", upload, upload.Length,
    "application/pdf");

await using var download = await store.GetObjectAsync(
    "billing-files", "invoices/2026/invoice.pdf");
await using var output = File.Create("downloaded-invoice.pdf");
await download.Content.CopyToAsync(output);
```

Keep each upload stream open until `PutObjectAsync` completes. Upload streams must be seekable and positioned at the start. The caller owns each returned `ObjectStoreObject` and must dispose it after reading. `ObjectStoreException` includes the S3 error code, HTTP status, request ID, and message.

`IObjectStore` uses `System.IO.Stream`, typed metadata and results, and `CancellationToken`. It exposes no filesystem paths or ASP.NET types. An adapter for another service, such as S3 or MinIO, can implement the same interface.

## Build and test

From the repository root:

```bash
dotnet restore XStorage.sln
dotnet build XStorage.sln --no-restore
dotnet test XStorage.sln --no-restore --verbosity minimal -m:1 /p:UseSharedCompilation=false
```

The SDK acceptance tests use `AWSSDK.S3` version `4.0.103.4` and cover all six supported operations. The child-process crash test runs only on Linux. The ACL test runs only on Windows. Windows filesystem and ACL behavior still needs a Windows test run. Power-loss testing has not been done.

## Quota and object size

The maximum object size applies to every application. The default is 25 MiB. Set the limit to zero to allow empty objects only. XStorage rejects a request whose declared size is too large before it reads the body. It also enforces the limit while reading a body with unknown length.

An application's byte quota covers all buckets it owns. XStorage counts bytes in committed objects. Overwriting an object replaces its previous size in the total; deleting an object subtracts its size. If you lower a quota below current usage, PUT requests remain blocked until deletes bring usage under the new limit. Existing objects stay readable and deletable. A PUT that would exceed the quota returns `InvalidArgument`.

XStorage also limits the number of objects and buckets per application. The defaults are 10,000 committed objects and 100 buckets. Set these limits at startup with the environment variables above. Empty objects count. Replacing an existing object does not increase the count, and deleting one frees a slot. A PUT or CreateBucket request that exceeds a limit returns `InvalidArgument`. At startup, XStorage rebuilds the object count from committed records.

## Data format, recovery, and backups

The data root contains:

- `buckets/`: bucket directories with an authoritative `.owner` marker and immutable object records.
- `state/applications/`: one encrypted JSON record per application.
- `state/.service.lock`: a process lock that prevents concurrent server processes.

Each object record stores the exact key, content type, size, SHA-256 hash, MD5 ETag, UTC creation time, and object bytes. XStorage never uses an object key as a filesystem path. It hashes the key's exact UTF-8 bytes to choose a record path. Ownership markers determine which application owns each bucket. XStorage derives bucket indexes and usage counters from those markers and committed object records.

At startup, XStorage removes abandoned temporary files and validates committed records and ownership markers. It then rebuilds ownership indexes and the in-memory sorted listing index, and recalculates byte usage and object counts. The listing index is a cache; committed object records remain authoritative. A corrupt committed record stops startup. Runtime integrity failures return `InternalError`. Logs do not include object bytes or credentials.

Stop XStorage before copying the data root. Back up the entire data root and both configured encryption and continuation-token keys. XStorage needs the encryption key to read stored application credentials. Keep the continuation-token key unchanged if clients must continue using existing list tokens.

If XStorage loses its application records but bucket data remains, it rebuilds application-to-bucket mappings from ownership markers and recalculates usage. Recovered applications use the display name `recovered` and a zero-byte quota. Their old secrets cannot be recovered. Use the admin API to rotate each recovered access key, set its quota, and update the consuming project with the new secret. XStorage cannot recover an application that has no bucket ownership marker; create that application again.

## Durability guarantee

For PUT, XStorage writes a temporary record in the destination directory, computes hashes, flushes the record, and atomically renames it to the committed path. It then updates the durable usage counter. Linux and macOS flush the containing directory. Windows uses `MOVEFILE_WRITE_THROUGH` for committed file and directory renames. For DELETE on Windows, XStorage first renames the committed record to a temporary tombstone using a durable rename, then cleans it up. At startup, XStorage removes abandoned uploads and tombstones, then recalculates usage. Bucket creation flushes the ownership marker and publishes the directory with the platform's durable rename operation.

A crash before the object rename leaves a temporary file. XStorage removes it at startup. A failure after an object or deletion rename but before the usage-counter update can return an error even though the object change is visible. Startup reconciliation repairs the derived counter. XStorage cannot guarantee power-loss behavior if storage hardware reports a flush as complete before data is durable. Power-loss testing has not been done.

## Known limits

- One server process and one local filesystem.
- No replication, sharding, online backup guarantee, lifecycle cleanup, multipart upload, versioning, bucket deletion, presigned URLs, or virtual-hosted addressing.
- Application credentials identify the consuming application. Each application must authorize its own users.
- An unknown bucket returns `NoSuchBucket`; a bucket owned by another application returns `AccessDenied`. This follows the API behavior described in [S3 API subset](#s3-api-subset) and reveals whether a bucket name exists.
- The Linux-only child-process crash test is skipped on macOS and Windows. Power-loss behavior is not tested.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| The server exits during startup. | Confirm that all four required environment variables are set. Check that both keys decode to 32 bytes, the data root is an absolute path, limits are zero or greater, and the listener URLs do not use the same host and port. |
| An admin request returns `401`. | Send the exact token configured in `XSTORAGE_ADMIN_TOKEN` as `Authorization: Bearer <admin-token>`. |
| An admin request returns `403`. | If the admin listener is bound outside loopback, use HTTPS through the trusted proxy. |
| An S3 request returns `RequestTimeTooSkewed`. | Check that the client clock is synchronized. Request timestamps must be within 15 minutes of server time. |
| An S3 request returns `SignatureDoesNotMatch`. | Check the access key, secret, region (`us-east-1`), service (`s3`), signed headers, and payload hash. Recheck the canonical path and query if a proxy is in use. |
| An S3 request returns `AccessDenied`. | Confirm that the application is active and owns the bucket. Sign `host` and `x-amz-date`. Requests using `UNSIGNED-PAYLOAD` require HTTPS. |
| An S3 request returns `NoSuchBucket`. | Create the bucket with the application's credentials, or check the bucket name. A bucket owned by another application returns `AccessDenied`. |
| A PUT returns `InvalidArgument`. | Check the maximum object size, application byte quota, object-count limit, bucket-count limit, and object key. Empty objects still count toward the object-count limit. |
| The old credentials stop working after rotation. | Rotation takes effect immediately. Update every consumer with the new secret. There is no overlap period. |
| Stored application credentials cannot be read after restart. | Restore the same `XSTORAGE_APP_ENCRYPTION_KEY` used when the credentials were stored. |
| Startup stops because a committed record is corrupt. | Check the server logs and restore the data root from a known-good backup. Do not edit committed records manually. |

S3 errors use S3 XML. HEAD errors have no body. Admin operation errors use JSON. A missing or invalid admin bearer token returns an empty `401` response.
