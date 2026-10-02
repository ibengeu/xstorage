# XStorage

XStorage is a single-node object storage server for applications that need private buckets and S3-style object operations. It runs on .NET 10 and stores data on a local filesystem on Linux, macOS, or Windows. Each application gets its own credentials and byte quota.

The server exposes a small [S3 API subset](#s3-api-subset) and a separate [admin API](#provision-applications). It does not call AWS. You can connect another project with the [optional .NET client](#optional-net-client-library) or a compatible S3 client. This is not a full S3 implementation.

To use it:

1. [Start the server](#run-locally).
2. [Create an application](#provision-applications) and save its access key and secret.
3. [Connect your project](#connect-another-project) to the S3 endpoint.

## Requirements

- Linux, macOS, or Windows on one host.
- .NET 10 SDK to build or run from source.
- Linux or macOS: one local filesystem with atomic same-directory rename and working file and directory flush operations.
- Windows: a local NTFS volume with atomic same-volume rename and working write-through operations.
- A dedicated service account that owns the data root.
- A reverse proxy that terminates HTTPS for network clients.

Network filesystems are not supported. Windows storage uses protected ACLs for the service identity and LocalSystem. See the [durability guarantee](#durability-guarantee) before storing important data.

## Run locally

From the repository root, set the four required variables and start the server. These examples create temporary secrets for a local trial. For lasting data, keep the same keys across restarts in a protected secret store.

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

The S3 endpoint listens on `http://127.0.0.1:9000`. The admin API listens on `http://127.0.0.1:9001`. Keep the process running while clients use it. Ctrl+C stops both listeners. The service allows only one process per data root.

## Configuration

The process reads these environment variables at startup:

- **`XSTORAGE_DATA_ROOT` (required):** Absolute path to the service-owned data root.
- **`XSTORAGE_ADMIN_TOKEN` (required):** Static bearer token with at least 32 printable ASCII characters and no spaces. Store it in a secret manager.
- **`XSTORAGE_APP_ENCRYPTION_KEY` (required):** Base64 encoding of 32 random bytes. This key encrypts application secrets at rest.
- **`XSTORAGE_CONTINUATION_TOKEN_KEY` (required):** Base64 encoding of 32 random bytes. Keep this value stable across restarts so list tokens remain valid.
- **`XSTORAGE_DATA_URL` (optional):** S3 listener URL. Default: `http://127.0.0.1:9000`.
- **`XSTORAGE_ADMIN_URL` (optional):** Admin listener URL. Default: `http://127.0.0.1:9001`.
- **`XSTORAGE_MAX_OBJECT_BYTES` (optional):** Maximum object size. Default: `26214400` bytes (25 MiB).
- **`XSTORAGE_MAX_OBJECTS_PER_APPLICATION` (optional):** Maximum committed object records for one application. Default: `10000`. Empty objects count.
- **`XSTORAGE_MAX_BUCKETS_PER_APPLICATION` (optional):** Maximum owned buckets for one application. Default: `100`.
- **`XSTORAGE_TRUSTED_PROXIES` (optional):** Comma-separated proxy IP addresses allowed to set forwarded scheme and client address headers. Default: no trusted proxies.

The encryption and continuation keys must each decode to exactly 32 bytes. Keep all four required settings stable for a persistent deployment. The application encryption key is needed to read stored credentials after a restart. Do not place secrets in source control, command arguments, logs, or browser code. Keep them in a secret manager or equivalent protected service configuration.

The server refuses relative data roots, filesystem roots, unsafe data trees, invalid key lengths, invalid limits, and listener addresses that use the same host and port. The data tree rejects symbolic links and other reparse points. The service applies owner-only filesystem permissions on Linux and macOS. On Windows, it applies protected ACLs for the service identity and LocalSystem.

The process releases the data root lock on normal shutdown. Unix processes also handle SIGTERM.

## Network and TLS

Keep both listeners on loopback for the default deployment. Do not expose the admin port through the data-plane proxy. Operators can reach the admin port through a local provisioning script or an SSH tunnel.

Terminate HTTPS at a trusted reverse proxy for network clients. Configure the proxy to preserve the original `Host`, path, raw query, and signed headers. Set `XSTORAGE_TRUSTED_PROXIES` to the proxy IP addresses. The server trusts `X-Forwarded-Proto` only from those addresses. An S3 request that uses `UNSIGNED-PAYLOAD` succeeds only when the request is HTTPS or a configured proxy reports HTTPS.

If an operator binds either listener to a non-loopback address, the server requires the request to be HTTPS after trusted forwarded headers are applied. Do not bind the admin listener to a public interface unless the operator has configured HTTPS termination and restricted access to the operator network.

## S3 API subset

All requests use path-style addressing and SigV4 with region `us-east-1`, service `s3`, and static application credentials. The server requires signed `host` and `x-amz-date` headers. It accepts an actual lowercase SHA-256 payload hash or `UNSIGNED-PAYLOAD`. It rejects timestamps more than 15 minutes from server time.

- **Create bucket — `PUT /{bucket}`:** Creates an empty bucket. A body may contain only the `us-east-1` location constraint.
- **Put object — `PUT /{bucket}/{key}`:** Writes or replaces one whole object. Returns the quoted MD5 ETag.
- **Get object — `GET /{bucket}/{key}`:** Returns object bytes and metadata.
- **Head object — `HEAD /{bucket}/{key}`:** Returns metadata without a body.
- **Delete object — `DELETE /{bucket}/{key}`:** Removes the object. A missing key succeeds.
- **ListObjectsV2 — `GET /{bucket}?list-type=2`:** Returns one S3 XML page.

The server decodes an object key once. Encoded slashes become slashes in the key. Keys remain case-sensitive and use exact UTF-8 bytes. Listings sort by UTF-8 byte order. Delimiter prefixes count as page entries. Continuation tokens are signed, scoped to the list request, URL-safe, and stable across restarts while the token key stays unchanged. Pages are not snapshots across separate requests. CreateBucket also accepts `PUT /{bucket}/` because the unmodified AWS SDK for .NET uses that path-style form.

Every S3 response includes `x-amz-request-id`. Errors use S3 XML, except HEAD errors, which have no body. The server checks bucket ownership before key lookup. A different application's bucket returns `AccessDenied`. An unknown bucket returns `NoSuchBucket`.

Callers can distinguish an unknown bucket from a bucket owned by another application. The service does not return object keys or metadata across applications.

The service rejects unsupported S3 operations, range and conditional requests, presigned URLs, virtual-hosted addressing, ACLs, object metadata headers, storage-class and tagging headers, request checksums, checksum trailers, session tokens, and SigV4 streaming chunks. It also rejects unknown query options and response override queries. The AWS SDK's `x-amz-api-version` protocol header is accepted as SDK metadata; it does not enable an S3 operation.

Other unsupported features include bucket deletion, multipart upload, resumable upload, versioning, lifecycle rules, replication, sharding, cross-application bucket sharing, public object URLs, and AWS S3 control-plane calls.

## Provision applications

Each consuming application receives a separate access key, secret, and quota. Application names are display labels and can repeat. Access key IDs identify applications. The admin listener accepts only the bearer token configured at startup. Create one application per consuming project or trust boundary.

For Unix scripts, store a line such as `Authorization: Bearer <token>` in a protected file outside the repository. Set file permissions to `0600`. Pass the file path to curl with `--header @/path/to/admin-header`; do not pass the token as a command argument. The paths below are examples: create the protected header file and credential directory before using them. Store each create or rotate response in a protected credential store because the response contains the secret.

Create an application:

```bash
umask 077
curl --silent --show-error --fail-with-body \
  --header @/run/secrets/xstorage-admin-header \
  --header 'Content-Type: application/json' \
  --data '{"name":"billing","quotaBytes":1073741824}' \
  http://127.0.0.1:9001/admin/applications \
  > /secure/credential-store/billing.json
```

On Windows, load the admin token from a protected store into the PowerShell session. Then create an application without putting the token on an external command line:

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

Rotation is an immediate hard cutover. A request can finish only if it passes SigV4 verification before the rotation response. All other requests that use the old secret fail. Coordinate the update across every running consumer instance before triggering rotation. There is no grace period.

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

Deactivation blocks later requests. It keeps the application's buckets and objects. Permanent data deletion is outside this release.

## Connect another project

Use the `accessKeyId` and `secretAccessKey` from the application creation response. Point the client at the **S3 endpoint** on port `9000`, not the admin endpoint on port `9001`. Create a bucket for that application before uploading objects. Store credentials in the consuming project's secret store or server environment. Never expose them to browser code.

For a .NET 10 project, choose one of these clients:

- Add a project reference to `src/XStorage.Client/XStorage.Client.csproj` and use the [typed client](#optional-net-client-library).
- Add `AWSSDK.S3` and use the [AWS SDK example](#aws-sdk-for-net). This repository tests version `4.0.103.4`.

For another language, configure an S3 client with a custom service URL, path-style addressing, region `us-east-1`, and the application's static credentials. The client must support the [limited request set](#s3-api-subset). Use HTTPS when connecting across a network.

## AWS SDK for .NET

Consumers can use `AWSSDK.S3` with a custom service URL, path-style addressing, `us-east-1`, and static credentials. Replace the service URL with the HTTPS address of your reverse proxy for remote access:

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

PUT streams must be readable, seekable, positioned at zero, and have a known length. The caller owns upload streams and must keep them open until the SDK call completes. The caller must dispose each get response to close its response stream.

## Optional .NET client library

`XStorage.Client` provides the `IObjectStore` contract and an `AWSSDK.S3` backed `S3ObjectStore`. It sends HTTP requests to the standalone service. It does not access server storage in process. Add a project reference from your .NET 10 application to `src/XStorage.Client/XStorage.Client.csproj`.

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

The caller owns upload streams and must keep them open until `PutObjectAsync` completes. Upload streams must be seekable and positioned at zero. The caller owns each returned `ObjectStoreObject` and must dispose it after reading. `ObjectStoreException` reports the S3 error code, HTTP status, request ID, and message.

The `IObjectStore` interface uses `System.IO.Stream`, typed metadata and results, and `CancellationToken`. It exposes no filesystem paths or ASP.NET types. A future S3 or MinIO adapter can implement the same interface.

## Build and test

From the repository root:

```bash
dotnet restore XStorage.sln
dotnet build XStorage.sln --no-restore
dotnet test XStorage.sln --no-restore --verbosity minimal -m:1 /p:UseSharedCompilation=false
```

The SDK acceptance tests use `AWSSDK.S3` version `4.0.103.4`. They exercise all six supported operations. The Linux child-process crash test runs only on Linux. The Windows ACL test runs only on Windows. Windows filesystem and ACL behavior still needs a Windows test run; power-loss testing has not been done.

## Quota and object size

The configured maximum object size applies to every application. The default is 25 MiB. A zero-byte limit permits empty objects only. The service rejects an oversized declared length before reading the body and enforces the same limit while it streams an unknown-length body.

Each application's byte quota is shared across all buckets that application owns. The service counts committed object bytes. An overwrite replaces the previous size in the quota total. A delete subtracts the object's size. If an operator lowers quota below current usage, every PUT is blocked until deletes bring usage under quota. Existing objects remain readable and deletable. A PUT that would exceed byte quota returns `InvalidArgument`.

The service also applies per-application object-count and bucket-count limits. The defaults are 10,000 committed objects and 100 buckets. Operators can set each limit at startup with the environment variables above. Empty objects count toward the object limit. Replacing an existing key does not increase the count. Deleting an object releases one slot. A PUT or CreateBucket request that exceeds its count limit returns `InvalidArgument`. Startup rebuilds the object count from committed records.

## Data format, recovery, and backups

The data root contains:

- `buckets/`: bucket directories with an authoritative `.owner` marker and immutable object records.
- `state/applications/`: one encrypted JSON record per application.
- `state/.service.lock`: a process lock that prevents concurrent server processes.

Object records store the exact key, content type, size, SHA-256, MD5 ETag, UTC creation time, and object bytes. The key never becomes a filesystem path. The server hashes exact UTF-8 key bytes to choose the record path. Ownership markers determine bucket ownership. Application bucket indexes and usage counters are derived from those markers and committed records.

At startup, the service removes abandoned temporary files, validates committed records and ownership markers, rebuilds ownership indexes and the in-memory sorted listing index, and reconciles byte usage and object counts. The listing index is a cache. Committed object records remain authoritative. Corrupt committed records stop startup. Runtime integrity failures return `InternalError` and are logged without object bytes or credentials.

Stop the service before copying the data root for a backup. Back up the entire data root and both configured keys. The application encryption key is required to read stored application credentials. Keep the continuation-token key unchanged if existing continuation tokens must remain valid.

If the application record store is lost while bucket data remains, startup rebuilds application-to-bucket mappings from ownership markers and recalculates usage. Recovered applications appear with the display name `recovered` and a zero-byte quota. Their previous secrets cannot be recovered. Use the admin API to rotate each recovered access key ID, set its quota, and update the consumer's secret. Applications with no bucket marker cannot be reconstructed from bucket data; provision those applications again.

## Durability guarantee

For PUT, the server writes a temporary record in the destination directory, computes hashes, flushes the record, and atomically renames it over the committed path before it updates the durable usage counter. Linux and macOS flush the containing directory. Windows uses `MOVEFILE_WRITE_THROUGH` for committed file and directory renames. For DELETE, Windows durably renames the committed record to a temporary tombstone before cleanup. Startup removes abandoned upload files and tombstones, then reconciles usage. Bucket creation flushes its marker and publishes the directory with the platform's durable rename operation.

A crash before object rename leaves only a temporary file, which startup removes. A failure after object rename or deletion rename but before the counter update can return an error while the object change is already visible. Startup reconciliation repairs the derived usage counter. The service does not promise power-loss behavior for hardware that lies about flush completion. Power-loss testing is not claimed.

## Known limits

- One server process and one local filesystem only.
- No replication, sharding, online backup guarantee, lifecycle cleanup, multipart upload, versioning, bucket deletion, presigned URLs, or virtual-hosted addressing.
- Application authorization does not authorize end users. Each consuming application must authorize its own users.
- An unknown bucket returns `NoSuchBucket`, while a bucket owned by another application returns `AccessDenied`. This follows the S3 status rules and exposes whether a bucket name exists, as described in the S3 subset section.
- The Linux-only child-process crash test is skipped on macOS and Windows. Power-loss behavior is not tested.
