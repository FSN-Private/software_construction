using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal static class RecoveryObjectClosureValidator
{
    internal static void RequireExactMatch(
        IReadOnlyList<RecoveryObjectReference> expected,
        IReadOnlyList<RecoveryObjectReference> actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        try
        {
            foreach (var item in expected) item.Validate();
            foreach (var item in actual) item.Validate();
            if (!CanonicalBytes(expected).AsSpan().SequenceEqual(CanonicalBytes(actual)))
                throw new RecoveryWorkerException("restored_object_closure_mismatch");
        }
        catch (InvalidDataException exception)
        {
            throw new RecoveryWorkerException("restored_object_closure_invalid", exception);
        }
    }

    private static byte[] CanonicalBytes(IEnumerable<RecoveryObjectReference> objects)
    {
        var canonical = objects
            .Select(item => item.Canonicalized())
            .OrderBy(item => item.DataClass)
            .ThenBy(item => item.OwnerId, StringComparer.Ordinal)
            .ThenBy(item => item.LibraryId, StringComparer.Ordinal)
            .ThenBy(item => item.SourceKey, StringComparer.Ordinal)
            .ThenBy(item => item.SourceVersionId, StringComparer.Ordinal)
            .ThenBy(item => item.Sha256, StringComparer.Ordinal)
            .ThenBy(item => item.ByteLength)
            .ToArray();
        return RecoveryEvidenceCodec.SerializeCanonical(canonical);
    }
}
