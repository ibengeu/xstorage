using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using XStorage.Contracts;

namespace XStorage.Storage;

internal sealed class LocalObjectStorage : IObjectStorage
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IComparer<ListedObject> ListingObjectComparer =
        Comparer<ListedObject>.Create(static (left, right) => CompareUtf8Strings(left.Key, right.Key));
    private static readonly string MaximumObjectKey = string.Concat(Enumerable.Repeat("\U0010FFFF", 256));
    private static readonly ListedObject MaximumListingObject = new(MaximumObjectKey, DateTimeOffset.MaxValue, string.Empty, long.MaxValue);
    private readonly string _bucketsRoot;
    private readonly LocalApplicationStore _applications;
    private readonly long _maximumObjectBytes;
    private readonly int _maximumObjectsPerApplication;
    private readonly int _maximumBucketsPerApplication;
    private readonly byte[] _continuationTokenKey;
    private readonly FileStream _processLock;
    private readonly Action<StorageFaultPoint>? _faultInjector;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _applicationLocks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _bucketNamespaceLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _needsReconciliation = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BucketListingIndex> _listingIndexes = new(StringComparer.Ordinal);

    public LocalObjectStorage(
        string bucketsRoot,
        LocalApplicationStore applications,
        long maximumObjectBytes,
        int maximumObjectsPerApplication,
        int maximumBucketsPerApplication,
        byte[] continuationTokenKey,
        FileStream processLock,
        Action<StorageFaultPoint>? faultInjector)
    {
        _bucketsRoot = bucketsRoot;
        _applications = applications;
        _maximumObjectBytes = maximumObjectBytes;
        _maximumObjectsPerApplication = maximumObjectsPerApplication;
        _maximumBucketsPerApplication = maximumBucketsPerApplication;
        _continuationTokenKey = continuationTokenKey.ToArray();
        _processLock = processLock;
        _faultInjector = faultInjector;
    }

    public Task<ApplicationCredentials> CreateApplicationAsync(string name, long quotaBytes, CancellationToken cancellationToken) =>
        _applications.CreateAsync(name, quotaBytes, cancellationToken);

    public ValueTask<ApplicationSigningKey?> FindSigningKeyAsync(string accessKeyId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_applications.FindSigningKey(accessKeyId));
    }

    public async Task<bool> ConfirmSigningKeyAsync(
        string accessKeyId,
        long credentialVersion,
        CancellationToken cancellationToken)
    {
        if (_applications.Get(accessKeyId) is null)
        {
            return false;
        }

        var isCurrent = false;
        await WithApplicationLockAsync(accessKeyId, cancellationToken, () =>
        {
            var current = _applications.Get(accessKeyId);
            isCurrent = current is { Active: true } && current.CredentialVersion == credentialVersion;
            return Task.CompletedTask;
        });
        return isCurrent;
    }

    public Task AuthorizeBucketAsync(string accessKeyId, string bucket, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // OWASP A01:2025 Broken Access Control. Check the ownership marker before unsupported-feature errors can reveal a foreign bucket.
        _ = ResolveAuthorizedBucket(accessKeyId, bucket);
        return Task.CompletedTask;
    }

    public Task AuthorizeBucketCreationAsync(string accessKeyId, string bucket, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BucketNameRules.Validate(bucket);
        _ = RequireActiveApplication(accessKeyId);
        var bucketPath = Path.Combine(_bucketsRoot, bucket);
        if (Directory.Exists(bucketPath))
        {
            RejectDuplicateBucket(bucketPath, accessKeyId);
        }

        return Task.CompletedTask;
    }

    public async Task CreateBucketAsync(string accessKeyId, string bucket, CancellationToken cancellationToken)
    {
        BucketNameRules.Validate(bucket);
        _ = RequireActiveApplication(accessKeyId);
        await _bucketNamespaceLock.WaitAsync(cancellationToken);
        try
        {
            var bucketPath = Path.Combine(_bucketsRoot, bucket);
            if (Directory.Exists(bucketPath))
            {
                RejectDuplicateBucket(bucketPath, accessKeyId);
            }

            await WithApplicationLockAsync(accessKeyId, cancellationToken, async () =>
            {
                var current = RequireActiveApplication(accessKeyId);
                if (!current.OwnedBuckets.Contains(bucket, StringComparer.Ordinal))
                {
                    if (current.OwnedBuckets.Count >= _maximumBucketsPerApplication)
                    {
                        // OWASP A04:2025 Insecure Design. Bound per-application bucket metadata to prevent unbounded directory growth.
                        throw new ObjectStorageException("InvalidArgument", "The application bucket limit would be exceeded.", 400);
                    }

                    await _applications.UpdateAsync(
                        accessKeyId,
                        record => record with { OwnedBuckets = record.OwnedBuckets.Append(bucket).Order(StringComparer.Ordinal).ToList() },
                        cancellationToken);
                }
            });

            var stagingPath = Path.Combine(_bucketsRoot, $".create-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}.tmp");
            var published = false;
            var indexRegistered = false;
            try
            {
                if (!_listingIndexes.TryAdd(bucket, new BucketListingIndex()))
                {
                    throw InternalError("An in-memory object listing index already exists for this bucket.");
                }

                indexRegistered = true;
                FileSystemSafety.CreatePrivateDirectory(stagingPath);
                FileSystemSafety.CreatePrivateDirectory(Path.Combine(stagingPath, "objects"));
                await BucketOwnershipMarker.WriteAsync(Path.Combine(stagingPath, ".owner"), accessKeyId, cancellationToken);
                FileSystemSafety.FlushDirectory(stagingPath);
                _faultInjector?.Invoke(StorageFaultPoint.AfterBucketMarkerFlush);
                FileSystemSafety.MoveDirectory(stagingPath, bucketPath);
                published = true;
                FileSystemSafety.FlushDirectory(_bucketsRoot);
            }
            catch
            {
                if (!published && indexRegistered)
                {
                    _listingIndexes.TryRemove(bucket, out _);
                }

                if (!published && Directory.Exists(stagingPath))
                {
                    try
                    {
                        Directory.Delete(stagingPath, recursive: true);
                    }
                    catch (IOException)
                    {
                    }
                }

                if (!published && !Directory.Exists(bucketPath))
                {
                    try
                    {
                        await WithApplicationLockAsync(accessKeyId, CancellationToken.None, () => _applications.UpdateAsync(
                            accessKeyId,
                            record => record with { OwnedBuckets = record.OwnedBuckets.Where(value => !StringComparer.Ordinal.Equals(value, bucket)).ToList() },
                            CancellationToken.None));
                    }
                    catch (ObjectStorageException)
                    {
                    }
                }

                throw;
            }
        }
        finally
        {
            _bucketNamespaceLock.Release();
        }
    }

    public async Task<ObjectMetadata> PutObjectAsync(
        string accessKeyId,
        string bucket,
        string key,
        ObjectWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var application = RequireActiveApplication(accessKeyId);
        var bucketPath = ResolveAuthorizedBucket(accessKeyId, bucket);
        var objectPath = GetObjectPath(bucketPath, key);
        var objectDirectory = Path.GetDirectoryName(objectPath)!;
        FileSystemSafety.CreatePrivateDirectory(objectDirectory);
        var tempPath = Path.Combine(objectDirectory, $".upload-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}.tmp");
        var committed = false;
        ObjectMetadata? committedMetadata = null;

        try
        {
            await using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                _faultInjector?.Invoke(StorageFaultPoint.AfterObjectTemporaryFileCreated);
                committedMetadata = await ObjectRecordCodec.WriteAsync(temp, key, request, _maximumObjectBytes, cancellationToken, _faultInjector);
                await temp.FlushAsync(cancellationToken);
                temp.Flush(flushToDisk: true);
            }

            FileSystemSafety.SetPrivateFileMode(tempPath);
            _faultInjector?.Invoke(StorageFaultPoint.AfterObjectTemporaryFileFlush);
            // Commits serialize per application: quota and object counts are per application, and buckets have one owner.
            await WithApplicationLockAsync(accessKeyId, cancellationToken, async () =>
            {
                if (_needsReconciliation.ContainsKey(accessKeyId))
                {
                    throw InternalError("Application usage requires reconciliation.");
                }

                var current = RequireActiveApplication(accessKeyId);
                var existing = GetExistingSize(objectPath, key);
                var metadata = committedMetadata!;
                if (!existing.Exists && current.ObjectCount >= _maximumObjectsPerApplication)
                {
                    // OWASP A04:2025 Insecure Design. Bound empty and nonempty records to prevent per-application filesystem and metadata exhaustion.
                    throw new ObjectStorageException("InvalidArgument", "The application object limit would be exceeded.", 400);
                }

                var prospectiveUsage = checked(current.UsedBytes - existing.Size + metadata.Size);
                var prospectiveObjectCount = checked(current.ObjectCount + (existing.Exists ? 0 : 1));
                // OWASP A04:2025 Insecure Design. Block every PUT while usage exceeds a lowered quota to prevent continued resource overuse.
                if (current.UsedBytes > current.QuotaBytes || prospectiveUsage > current.QuotaBytes)
                {
                    throw new ObjectStorageException("InvalidArgument", "The application quota would be exceeded.", 400);
                }

                _faultInjector?.Invoke(StorageFaultPoint.BeforeObjectRename);
                try
                {
                    GetListingIndex(bucket).Write(index =>
                    {
                        FileSystemSafety.MoveFile(tempPath, objectPath, overwrite: true);
                        committed = true;
                        ReplaceListingEntry(index, metadata);
                    });

                    _faultInjector?.Invoke(StorageFaultPoint.AfterObjectRename);
                    FileSystemSafety.FlushDirectory(objectDirectory);
                    _faultInjector?.Invoke(StorageFaultPoint.BeforePutUsageCounterUpdate);
                    await _applications.UpdateAsync(
                        accessKeyId,
                        record => record with { UsedBytes = prospectiveUsage, ObjectCount = prospectiveObjectCount },
                        cancellationToken);
                }
                catch
                {
                    if (committed)
                    {
                        _needsReconciliation.TryAdd(accessKeyId, 0);
                    }

                    throw;
                }
            });
        }
        finally
        {
            if (!committed && File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        return committedMetadata ?? throw new InvalidOperationException("The committed object metadata is missing.");
    }

    public async Task<StoredObject> GetObjectAsync(
        string accessKeyId,
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        var bucketPath = ResolveAuthorizedBucket(accessKeyId, bucket);
        var objectPath = GetObjectPath(bucketPath, key);
        var opened = GetListingIndex(bucket).Read(_ => OpenObjectRecord(objectPath, key));
        var stream = opened.Stream;

        try
        {
            var header = opened.Header;
            await ObjectRecordCodec.VerifyBodyAsync(stream, header, cancellationToken);
            stream.Position = header.BodyOffset;
            return new StoredObject(header.Metadata, new ObjectBodyStream(stream, header.Metadata.Size));
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    public Task<ObjectMetadata> HeadObjectAsync(
        string accessKeyId,
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bucketPath = ResolveAuthorizedBucket(accessKeyId, bucket);
        var objectPath = GetObjectPath(bucketPath, key);
        var opened = GetListingIndex(bucket).Read(_ => OpenObjectRecord(objectPath, key));
        using (opened.Stream)
        {
            return Task.FromResult(opened.Header.Metadata);
        }
    }

    public async Task DeleteObjectAsync(string accessKeyId, string bucket, string key, CancellationToken cancellationToken)
    {
        var bucketPath = ResolveAuthorizedBucket(accessKeyId, bucket);
        var objectPath = GetObjectPath(bucketPath, key);
        FileSystemSafety.EnsureDirectoryPathIsNotLink(Path.GetDirectoryName(objectPath)!);
        var objectDirectory = Path.GetDirectoryName(objectPath)!;

        await WithApplicationLockAsync(accessKeyId, cancellationToken, async () =>
        {
            if (_needsReconciliation.ContainsKey(accessKeyId))
            {
                throw InternalError("Application usage requires reconciliation.");
            }

            if (!File.Exists(objectPath))
            {
                return;
            }

            var current = RequireActiveApplication(accessKeyId);
            var existing = GetExistingSize(objectPath, key);
            if (!existing.Exists)
            {
                return;
            }

            var changed = false;
            try
            {
                GetListingIndex(bucket).Write(index =>
                {
                    FileSystemSafety.DeleteObjectFile(objectPath);
                    changed = true;
                    RemoveListingEntry(index, key);
                });

                _faultInjector?.Invoke(StorageFaultPoint.AfterObjectUnlink);
                FileSystemSafety.FlushDirectory(objectDirectory);
                _faultInjector?.Invoke(StorageFaultPoint.BeforeDeleteUsageCounterUpdate);
                await _applications.UpdateAsync(
                    accessKeyId,
                    record => record with
                    {
                        UsedBytes = checked(current.UsedBytes - existing.Size),
                        ObjectCount = checked(current.ObjectCount - 1)
                    },
                    cancellationToken);
            }
            catch
            {
                if (changed)
                {
                    _needsReconciliation.TryAdd(accessKeyId, 0);
                }

                throw;
            }
        });
    }

    public Task<ObjectListingPage> ListObjectsV2Async(
        string accessKeyId,
        string bucket,
        ObjectListingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var bucketPath = ResolveAuthorizedBucket(accessKeyId, bucket);
        if (request.MaxKeys is < 0 or > 1000)
        {
            throw new ObjectStorageException("InvalidArgument", "max-keys must be between 0 and 1000.", 400);
        }

        if (request.EncodingType is not null && !StringComparer.Ordinal.Equals(request.EncodingType, "url"))
        {
            throw new ObjectStorageException("InvalidArgument", "The requested encoding type is not supported.", 400);
        }

        var continuationPosition = request.ContinuationToken is null
            ? request.StartAfter
            : ContinuationTokenCodec.Read(
                _continuationTokenKey,
                request.ContinuationToken,
                bucket,
                request.Prefix,
                request.Delimiter);
        var listingIndex = GetListingIndex(bucket);
        // OWASP A04:2025 Insecure Design. Read only one bounded page to prevent full-bucket allocations and long service-wide write stalls.
        var listing = listingIndex.Read(index =>
        {
            var selection = ReadListingEntries(index, request, continuationPosition, cancellationToken);
            ValidateListingRecords(bucketPath, selection.Entries, cancellationToken);
            return selection;
        });
        var pageEntries = listing.Entries.Take(request.MaxKeys).ToArray();
        var nextToken = listing.IsTruncated
            ? ContinuationTokenCodec.Create(
                _continuationTokenKey,
                bucket,
                request.Prefix,
                request.Delimiter,
                pageEntries[^1].Key)
            : null;
        return Task.FromResult(CreateListingPage(bucket, request, pageEntries, listing.IsTruncated, nextToken));
    }

    private static ObjectListingPage CreateListingPage(
        string bucket,
        ObjectListingRequest request,
        IReadOnlyList<ListingEntry> entries,
        bool isTruncated,
        string? nextToken)
    {
        var contents = entries.Where(item => item.Content is not null).Select(item => item.Content!).ToArray();
        var prefixes = entries.Where(item => item.CommonPrefix is not null).Select(item => item.CommonPrefix!).ToArray();
        return new ObjectListingPage(
            bucket,
            request.Prefix,
            entries.Count,
            request.MaxKeys,
            isTruncated,
            contents,
            prefixes,
            request.StartAfter,
            request.Delimiter,
            request.ContinuationToken,
            nextToken,
            request.EncodingType);
    }

    private static ListingSelection ReadListingEntries(
        SortedSet<ListedObject> listingIndex,
        ObjectListingRequest request,
        string? continuationPosition,
        CancellationToken cancellationToken)
    {
        var entries = new List<ListingEntry>(request.MaxKeys + 1);
        if (request.MaxKeys == 0)
        {
            return new ListingSelection(entries, false);
        }

        var prefix = request.Prefix ?? string.Empty;
        var startKey = continuationPosition is null ? prefix : GetLaterUtf8Key(prefix, continuationPosition);
        if (CompareUtf8Strings(startKey, MaximumObjectKey) > 0)
        {
            return new ListingSelection(entries, false);
        }

        while (true)
        {
            var skippedGroup = false;
            foreach (var item in listingIndex.GetViewBetween(CreateListingBound(startKey), MaximumListingObject))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!item.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return new ListingSelection(entries, false);
                }

                if (continuationPosition is not null && CompareUtf8Strings(item.Key, continuationPosition) <= 0)
                {
                    continue;
                }

                var commonPrefix = FindCommonPrefix(item.Key, prefix, request.Delimiter);
                if (commonPrefix is null)
                {
                    entries.Add(new ListingEntry(
                        item.Key,
                        new ListedObject(item.Key, item.LastModified, item.ETag, item.Size),
                        null,
                        item));
                    if (entries.Count > request.MaxKeys)
                    {
                        return new ListingSelection(entries, true);
                    }

                    continue;
                }

                if (continuationPosition is null || CompareUtf8Strings(commonPrefix, continuationPosition) > 0)
                {
                    entries.Add(new ListingEntry(commonPrefix, null, commonPrefix, item));
                    if (entries.Count > request.MaxKeys)
                    {
                        return new ListingSelection(entries, true);
                    }
                }

                var nextKey = GetNextStringAfterPrefix(commonPrefix);
                if (nextKey is null || CompareUtf8Strings(nextKey, MaximumObjectKey) > 0)
                {
                    return new ListingSelection(entries, false);
                }

                startKey = nextKey;
                skippedGroup = true;
                break;
            }

            if (!skippedGroup)
            {
                return new ListingSelection(entries, false);
            }
        }
    }

    private static string? FindCommonPrefix(string key, string prefix, string? delimiter)
    {
        if (delimiter is not { Length: > 0 })
        {
            return null;
        }

        var delimiterIndex = key.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal);
        return delimiterIndex < 0 ? null : key[..(delimiterIndex + delimiter.Length)];
    }

    private static string GetLaterUtf8Key(string left, string right) =>
        CompareUtf8Strings(left, right) >= 0 ? left : right;

    private static string? GetNextStringAfterPrefix(string prefix)
    {
        var position = 0;
        var incrementPosition = -1;
        var incrementRune = 0;
        foreach (var rune in prefix.EnumerateRunes())
        {
            if (rune.Value < 0x10FFFF)
            {
                incrementPosition = position;
                incrementRune = rune.Value + 1;
            }

            position += rune.Utf16SequenceLength;
        }

        return incrementPosition < 0
            ? null
            : string.Concat(prefix[..incrementPosition], new Rune(incrementRune).ToString());
    }

    private static ListedObject CreateListingBound(string key) =>
        new(key, DateTimeOffset.MinValue, string.Empty, 0);

    private static void ValidateListingRecords(
        string bucketPath,
        IReadOnlyList<ListingEntry> entries,
        CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateListingRecord(bucketPath, entry.SourceObject);
        }
    }

    private static void ValidateListingRecord(string bucketPath, ListedObject expected)
    {
        var objectPath = GetObjectPath(bucketPath, expected.Key);
        FileSystemSafety.EnsureDirectoryPathIsNotLink(Path.GetDirectoryName(objectPath)!);
        FileSystemSafety.EnsureFileIsNotLink(objectPath);
        using var stream = new FileStream(objectPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var header = ObjectRecordCodec.ReadHeader(stream);
        if (!StringComparer.Ordinal.Equals(header.Metadata.Key, expected.Key)
            || header.Metadata.Size != expected.Size
            || !StringComparer.Ordinal.Equals(header.Metadata.ETag, expected.ETag)
            || header.Metadata.LastModified != expected.LastModified)
        {
            throw new IOException("A committed object differs from the in-memory listing index.");
        }
    }

    private static void ReplaceListingEntry(SortedSet<ListedObject> listingIndex, ObjectMetadata metadata)
    {
        var entry = ToListedObject(metadata);
        listingIndex.Remove(CreateListingBound(metadata.Key));
        listingIndex.Add(entry);
    }

    private static void RemoveListingEntry(SortedSet<ListedObject> listingIndex, string key)
    {
        listingIndex.Remove(CreateListingBound(key));
    }

    private BucketListingIndex GetListingIndex(string bucket) =>
        _listingIndexes.TryGetValue(bucket, out var index)
            ? index
            : throw InternalError("The in-memory object listing index is missing.");

    private static ListedObject ToListedObject(ObjectMetadata metadata) =>
        new(metadata.Key, metadata.LastModified, metadata.ETag, metadata.Size);

    private static int CompareUtf8Strings(string left, string right)
    {
        var leftRunes = left.EnumerateRunes().GetEnumerator();
        var rightRunes = right.EnumerateRunes().GetEnumerator();
        while (true)
        {
            var hasLeft = leftRunes.MoveNext();
            var hasRight = rightRunes.MoveNext();
            if (!hasLeft || !hasRight)
            {
                return hasLeft.CompareTo(hasRight);
            }

            var comparison = leftRunes.Current.Value.CompareTo(rightRunes.Current.Value);
            if (comparison != 0)
            {
                return comparison;
            }
        }
    }

    public async Task<ApplicationCredentials> RotateCredentialsAsync(string accessKeyId, CancellationToken cancellationToken)
    {
        ApplicationCredentials? result = null;
        await WithApplicationLockAsync(accessKeyId, cancellationToken, async () =>
        {
            result = await _applications.RotateAsync(accessKeyId, cancellationToken);
        });
        return result!;
    }

    public async Task UpdateQuotaAsync(string accessKeyId, long quotaBytes, CancellationToken cancellationToken)
    {
        if (quotaBytes < 0)
        {
            throw new ObjectStorageException("InvalidArgument", "The application quota must be zero or greater.", 400);
        }

        await WithApplicationLockAsync(accessKeyId, cancellationToken, () => _applications.UpdateAsync(
            accessKeyId,
            record => _needsReconciliation.ContainsKey(accessKeyId)
                ? throw InternalError("Application usage requires reconciliation.")
                : record with { QuotaBytes = quotaBytes },
            cancellationToken));
    }

    public Task DeactivateApplicationAsync(string accessKeyId, CancellationToken cancellationToken) =>
        WithApplicationLockAsync(accessKeyId, cancellationToken, () => _applications.UpdateAsync(
            accessKeyId,
            record =>
            {
                // OWASP A01:2025 Broken Access Control. Persist inactive status so every later request is denied.
                return record with { Active = false };
            },
            cancellationToken));

    public Task<IReadOnlyList<ApplicationSummary>> ListApplicationsAsync(CancellationToken cancellationToken) =>
        _applications.ListAsync(cancellationToken);

    internal async Task ReconcileAtStartupAsync(CancellationToken cancellationToken)
    {
        _listingIndexes.Clear();
        await RemoveAbandonedBucketCreationsAsync(cancellationToken);
        await RebuildOwnershipIndexFromMarkersAsync(cancellationToken);
        foreach (var application in _applications.GetRecords())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReconcileApplicationAsync(application, cancellationToken);
        }
    }

    private async Task RebuildOwnershipIndexFromMarkersAsync(CancellationToken cancellationToken)
    {
        var bucketsByOwner = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var bucketPath in Directory.EnumerateDirectories(_bucketsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemSafety.EnsureDirectoryIsNotLink(bucketPath);
            var bucket = Path.GetFileName(bucketPath);
            try
            {
                BucketNameRules.Validate(bucket);
            }
            catch (ObjectStorageException exception)
            {
                throw new IOException("The bucket directory has an invalid name.", exception);
            }

            var markerPath = Path.Combine(bucketPath, ".owner");
            if (!File.Exists(markerPath))
            {
                throw new IOException("A committed bucket has no ownership marker.");
            }

            var owner = BucketOwnershipMarker.Read(markerPath);
            if (!bucketsByOwner.TryGetValue(owner, out var ownedBuckets))
            {
                ownedBuckets = [];
                bucketsByOwner.Add(owner, ownedBuckets);
            }

            ownedBuckets.Add(bucket);
        }

        foreach (var (owner, ownedBuckets) in bucketsByOwner)
        {
            await _applications.RecoverFromOwnershipMarkersAsync(owner, ownedBuckets, cancellationToken);
        }

        foreach (var application in _applications.GetRecords())
        {
            var ownedBuckets = bucketsByOwner.GetValueOrDefault(application.AccessKeyId) ?? [];
            if (!application.OwnedBuckets.SequenceEqual(ownedBuckets.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                await _applications.UpdateAsync(
                    application.AccessKeyId,
                    record => record with { OwnedBuckets = ownedBuckets.Order(StringComparer.Ordinal).ToList() },
                    cancellationToken);
            }
        }
    }

    private async Task ReconcileApplicationAsync(StoredApplicationRecord application, CancellationToken cancellationToken)
    {
        var ownedBuckets = new List<string>();
        long usedBytes = 0;
        var objectCount = 0;
        foreach (var bucket in application.OwnedBuckets.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                BucketNameRules.Validate(bucket);
            }
            catch (ObjectStorageException exception)
            {
                throw new IOException("An application record contains an invalid bucket name.", exception);
            }

            var bucketPath = Path.Combine(_bucketsRoot, bucket);
            if (!Directory.Exists(bucketPath))
            {
                continue;
            }

            FileSystemSafety.EnsureDirectoryIsNotLink(bucketPath);
            var markerPath = Path.Combine(bucketPath, ".owner");
            if (!File.Exists(markerPath))
            {
                throw new IOException("A committed bucket has no ownership marker.");
            }

            if (!StringComparer.Ordinal.Equals(BucketOwnershipMarker.Read(markerPath), application.AccessKeyId))
            {
                continue;
            }

            ownedBuckets.Add(bucket);
            var listingIndex = new SortedSet<ListedObject>(ListingObjectComparer);
            usedBytes = checked(usedBytes + await ScanBucketObjectsAsync(
                bucketPath,
                metadata =>
                {
                    // OWASP A04:2025 Insecure Design. Rebuild the object count from committed records so an interrupted counter update cannot bypass the limit after restart.
                    objectCount = checked(objectCount + 1);
                    listingIndex.Add(ToListedObject(metadata));
                },
                cancellationToken));
            _listingIndexes[bucket] = new BucketListingIndex(listingIndex);
        }

        if (usedBytes != application.UsedBytes
            || objectCount != application.ObjectCount
            || !application.OwnedBuckets.SequenceEqual(ownedBuckets, StringComparer.Ordinal))
        {
            await _applications.UpdateAsync(
                application.AccessKeyId,
                record => record with { UsedBytes = usedBytes, ObjectCount = objectCount, OwnedBuckets = ownedBuckets },
                cancellationToken);
        }
    }

    private async Task<long> ScanBucketObjectsAsync(
        string bucketPath,
        Action<ObjectMetadata> onObject,
        CancellationToken cancellationToken)
    {
        var objectsPath = Path.Combine(bucketPath, "objects");
        if (!Directory.Exists(objectsPath))
        {
            throw new IOException("A committed bucket has no object directory.");
        }

        return await ScanObjectDirectoryAsync(objectsPath, bucketPath, onObject, cancellationToken);
    }

    private async Task<long> ScanObjectDirectoryAsync(
        string directory,
        string bucketPath,
        Action<ObjectMetadata> onObject,
        CancellationToken cancellationToken)
    {
        FileSystemSafety.EnsureDirectoryIsNotLink(directory);
        long usedBytes = 0;
        var removedTemporaryFiles = false;
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemSafety.EnsureFileIsNotLink(path);
            var fileName = Path.GetFileName(path);
            if (fileName.StartsWith(".upload-", StringComparison.Ordinal) && fileName.EndsWith(".tmp", StringComparison.Ordinal))
            {
                File.Delete(path);
                removedTemporaryFiles = true;
                continue;
            }

            if (fileName.StartsWith(".delete-", StringComparison.Ordinal) && fileName.EndsWith(".tmp", StringComparison.Ordinal))
            {
                File.Delete(path);
                removedTemporaryFiles = true;
                continue;
            }

            if (!fileName.EndsWith(".obj", StringComparison.Ordinal))
            {
                throw new IOException("The object directory contains an unknown file.");
            }

            var metadata = await ValidateCommittedObjectAsync(path, bucketPath, cancellationToken);
            usedBytes = checked(usedBytes + metadata.Size);
            onObject(metadata);
        }

        foreach (var subdirectory in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
        {
            FileSystemSafety.EnsureDirectoryIsNotLink(subdirectory);
            if (FileSystemSafety.IsDirectoryCreationStagingPath(subdirectory))
            {
                FileSystemSafety.DeleteDirectoryCreationStagingPath(subdirectory);
                removedTemporaryFiles = true;
                continue;
            }

            usedBytes = checked(usedBytes + await ScanObjectDirectoryAsync(subdirectory, bucketPath, onObject, cancellationToken));
        }

        if (removedTemporaryFiles)
        {
            FileSystemSafety.FlushDirectory(directory);
        }

        return usedBytes;
    }

    private async Task<ObjectMetadata> ValidateCommittedObjectAsync(
        string objectPath,
        string bucketPath,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(objectPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var header = ObjectRecordCodec.ReadHeader(stream);
        string expectedPath;
        try
        {
            expectedPath = GetObjectPath(bucketPath, header.Metadata.Key);
        }
        catch (ObjectStorageException exception)
        {
            throw new IOException("A committed object contains an invalid key.", exception);
        }

        if (!PathComparer.Equals(Path.GetFullPath(expectedPath), Path.GetFullPath(objectPath))
            || header.Metadata.Size > _maximumObjectBytes)
        {
            throw new IOException("A committed object record is stored at an invalid path or exceeds the configured size limit.");
        }

        // OWASP A05:2025 Cryptographic Failures. Verify stored SHA-256 and MD5 before serving records after a restart.
        await ObjectRecordCodec.VerifyBodyAtStartupAsync(stream, header, cancellationToken);
        return header.Metadata;
    }

    private Task RemoveAbandonedBucketCreationsAsync(CancellationToken cancellationToken)
    {
        var removed = false;
        foreach (var path in Directory.EnumerateDirectories(_bucketsRoot, ".create-*.tmp", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemSafety.EnsureDirectoryIsNotLink(path);
            Directory.Delete(path, recursive: true);
            removed = true;
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var path in Directory.EnumerateDirectories(_bucketsRoot, ".xstorage-mkdir-*.tmp", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileSystemSafety.DeleteDirectoryCreationStagingPath(path);
                removed = true;
            }
        }

        if (removed)
        {
            FileSystemSafety.FlushDirectory(_bucketsRoot);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        foreach (var gate in _applicationLocks.Values)
        {
            gate.Dispose();
        }

        foreach (var listingIndex in _listingIndexes.Values)
        {
            listingIndex.Dispose();
        }

        _bucketNamespaceLock.Dispose();
        CryptographicOperations.ZeroMemory(_continuationTokenKey);
        _applications.Dispose();
        _processLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private string ResolveAuthorizedBucket(string accessKeyId, string bucket)
    {
        BucketNameRules.Validate(bucket);
        _ = RequireActiveApplication(accessKeyId);
        var path = Path.Combine(_bucketsRoot, bucket);
        if (!Directory.Exists(path))
        {
            throw new ObjectStorageException("NoSuchBucket", "The bucket does not exist.", 404);
        }

        FileSystemSafety.EnsureDirectoryIsNotLink(path);
        var marker = Path.Combine(path, ".owner");
        if (!File.Exists(marker))
        {
            throw InternalError("The bucket ownership marker is missing.");
        }

        // OWASP A01:2025 Broken Access Control. Check the authoritative owner marker before object lookup to prevent cross-application access.
        var owner = BucketOwnershipMarker.Read(marker);
        if (!StringComparer.Ordinal.Equals(owner, accessKeyId))
        {
            throw new ObjectStorageException("AccessDenied", "Access to the bucket is denied.", 403);
        }

        return path;
    }

    private void RejectDuplicateBucket(string bucketPath, string accessKeyId)
    {
        FileSystemSafety.EnsureDirectoryIsNotLink(bucketPath);
        var marker = Path.Combine(bucketPath, ".owner");
        if (!File.Exists(marker))
        {
            throw InternalError("An existing bucket has no ownership marker.");
        }

        var owner = BucketOwnershipMarker.Read(marker);
        if (StringComparer.Ordinal.Equals(owner, accessKeyId))
        {
            throw new ObjectStorageException("BucketAlreadyOwnedByYou", "The bucket is already owned by this application.", 409);
        }

        throw new ObjectStorageException("AccessDenied", "Access to the bucket is denied.", 403);
    }

    private static string GetObjectPath(string bucketPath, string key)
    {
        byte[] keyBytes;
        try
        {
            keyBytes = StrictUtf8.GetBytes(key);
        }
        catch (EncoderFallbackException)
        {
            // OWASP A07:2025 Injection. Reject invalid UTF-16 so malformed keys cannot alias a valid filesystem record.
            throw new ObjectStorageException("InvalidArgument", "The object key is not valid UTF-8.", 400);
        }

        if (keyBytes.Length is < 1 or > 1024 || keyBytes.Any(value => value == 0 || value < 0x20 || value == 0x7f))
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            throw new ObjectStorageException("InvalidArgument", "The object key is invalid.", 400);
        }

        // OWASP A07:2025 Injection. Hash exact key bytes before path construction to prevent path traversal.
        var hash = Convert.ToHexString(SHA256.HashData(keyBytes)).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(keyBytes);
        return Path.Combine(bucketPath, "objects", hash[..2], hash[2..4], $"{hash}.obj");
    }

    private (bool Exists, long Size) GetExistingSize(string objectPath, string key)
    {
        if (!File.Exists(objectPath))
        {
            return (false, 0);
        }

        FileSystemSafety.EnsureFileIsNotLink(objectPath);
        using var stream = new FileStream(objectPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var header = ObjectRecordCodec.ReadHeader(stream);
        VerifyRequestedKey(header, key);
        return (true, header.Metadata.Size);
    }

    private StoredApplicationRecord RequireActiveApplication(string accessKeyId)
    {
        var application = _applications.Get(accessKeyId);
        if (application is null)
        {
            throw new ObjectStorageException("InvalidAccessKeyId", "The access key ID is not valid.", 403);
        }

        if (!application.Active)
        {
            throw new ObjectStorageException("AccessDenied", "The application is inactive.", 403);
        }

        return application;
    }

    private async Task WithApplicationLockAsync(string accessKeyId, CancellationToken cancellationToken, Func<Task> action)
    {
        // OWASP A04:2025 Insecure Design. Reject unknown IDs before a lock is allocated so caller-supplied IDs cannot grow the lock table without bound.
        if (_applications.Get(accessKeyId) is null)
        {
            throw new ObjectStorageException("NoSuchApplication", "The application does not exist.", 404);
        }

        var gate = _applicationLocks.GetOrAdd(accessKeyId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await action();
        }
        finally
        {
            gate.Release();
        }
    }

    private static void VerifyRequestedKey(ObjectRecordHeader header, string requestedKey)
    {
        if (!StringComparer.Ordinal.Equals(header.Metadata.Key, requestedKey))
        {
            throw InternalError("An object hash collision was detected.");
        }
    }

    private static OpenedObject OpenObjectRecord(string objectPath, string key)
    {
        FileSystemSafety.EnsureDirectoryPathIsNotLink(Path.GetDirectoryName(objectPath)!);
        if (!File.Exists(objectPath))
        {
            throw new ObjectStorageException("NoSuchKey", "The object does not exist.", 404);
        }

        FileStream stream;
        try
        {
            FileSystemSafety.EnsureFileIsNotLink(objectPath);
            stream = new FileStream(objectPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            throw new ObjectStorageException("NoSuchKey", "The object does not exist.", 404);
        }

        try
        {
            var header = ObjectRecordCodec.ReadHeader(stream);
            VerifyRequestedKey(header, key);
            return new OpenedObject(stream, header);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record ListingEntry(string Key, ListedObject? Content, string? CommonPrefix, ListedObject SourceObject);

    private sealed record ListingSelection(IReadOnlyList<ListingEntry> Entries, bool IsTruncated);

    private sealed record OpenedObject(FileStream Stream, ObjectRecordHeader Header);

    private sealed class BucketListingIndex : IDisposable
    {
        private readonly ReaderWriterLockSlim _gate = new();
        private readonly SortedSet<ListedObject> _objects;

        public BucketListingIndex()
            : this(new SortedSet<ListedObject>(ListingObjectComparer))
        {
        }

        // Takes ownership of a set that the startup scan built, so a large bucket index is not copied a second time.
        public BucketListingIndex(SortedSet<ListedObject> objects)
        {
            _objects = objects;
        }

        public TResult Read<TResult>(Func<SortedSet<ListedObject>, TResult> read)
        {
            _gate.EnterReadLock();
            try
            {
                return read(_objects);
            }
            finally
            {
                _gate.ExitReadLock();
            }
        }

        public void Write(Action<SortedSet<ListedObject>> update)
        {
            _gate.EnterWriteLock();
            try
            {
                update(_objects);
            }
            finally
            {
                _gate.ExitWriteLock();
            }
        }

        public void Dispose() => _gate.Dispose();
    }

    private static ObjectStorageException InternalError(string message) =>
        new("InternalError", message, 500);
}
