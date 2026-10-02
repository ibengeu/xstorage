using System.Net;
using System.Text.RegularExpressions;
using XStorage.Contracts;

namespace XStorage.Storage;

internal static partial class BucketNameRules
{
    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidCharacters();

    public static void Validate(string bucket)
    {
        if (bucket.Length is < 3 or > 63 || !ValidCharacters().IsMatch(bucket))
        {
            throw InvalidName();
        }

        if (HasForbiddenPattern(bucket))
        {
            throw InvalidName();
        }
    }

    private static bool HasForbiddenPattern(string bucket) =>
        bucket.Contains("..", StringComparison.Ordinal)
        || bucket.Contains(".-", StringComparison.Ordinal)
        || bucket.Contains("-.", StringComparison.Ordinal)
        || bucket.StartsWith("xn--", StringComparison.Ordinal)
        || bucket.EndsWith("-s3alias", StringComparison.Ordinal)
        || IPAddress.TryParse(bucket, out _);

    private static ObjectStorageException InvalidName() =>
        new("InvalidBucketName", "The bucket name is invalid.", 400);
}
