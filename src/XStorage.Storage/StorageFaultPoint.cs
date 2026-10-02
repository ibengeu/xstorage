namespace XStorage.Storage;

internal enum StorageFaultPoint
{
    AfterObjectTemporaryFileCreated,
    AfterObjectHeaderPlaceholderWritten,
    AfterObjectBodyCopied,
    AfterObjectRecordFinalized,
    AfterObjectTemporaryFileFlush,
    BeforeObjectRename,
    AfterObjectRename,
    BeforePutUsageCounterUpdate,
    AfterObjectUnlink,
    BeforeDeleteUsageCounterUpdate,
    AfterBucketMarkerFlush
}
