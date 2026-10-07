using System.Text.Json;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

/// <summary>
/// Verifies a materialized clone without weakening the source-snapshot closure check.
/// Every original logical reference must resolve to its exact receipt-bound target
/// locator, and there may be no extra, missing, or substituted logical objects.
/// </summary>
internal static class RecoveryMaterializedClosureValidator
{
    internal static void RequireExactMappedMatch(
        IReadOnlyList<RecoveryObjectReference> source,
        IReadOnlyList<RecoveryObjectReference> materialized,
        IReadOnlyList<RecoveryMaterializedObjectV1> receipts,
        string sourceBucket,
        string targetBucket)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(materialized);
        ArgumentNullException.ThrowIfNull(receipts);
        try
        {
            ValidateBucket(sourceBucket, nameof(sourceBucket));
            ValidateBucket(targetBucket, nameof(targetBucket));
            if (sourceBucket == targetBucket) throw new InvalidDataException("candidate_bucket_not_isolated");
            foreach (var reference in source) reference.Validate();
            foreach (var reference in materialized) reference.Validate();
            foreach (var receipt in receipts) receipt.Validate();
            if (source.Count != materialized.Count || source.Count != receipts.Count)
                throw new InvalidDataException("candidate_materialization_count_mismatch");

            var receiptBySource = new Dictionary<SourceIdentity, RecoveryMaterializedObjectV1>();
            foreach (var receipt in receipts)
            {
                if (receipt.SourceBucket != sourceBucket || receipt.DestinationBucket != targetBucket ||
                    receipt.DestinationKey != "sha256/" + receipt.Sha256)
                    throw new InvalidDataException("candidate_materialization_bucket_mismatch");
                var identity = new SourceIdentity(receipt.SourceKey, receipt.SourceVersionId,
                    receipt.Sha256, receipt.ByteLength);
                if (!receiptBySource.TryAdd(identity, receipt))
                    throw new InvalidDataException("candidate_materialization_duplicate_source");
            }

            var sourceByIdentity = new Dictionary<SourceIdentity, RecoveryObjectReference>();
            foreach (var reference in source)
            {
                var identity = new SourceIdentity(reference.SourceKey, reference.SourceVersionId,
                    reference.Sha256, reference.ByteLength);
                if (!sourceByIdentity.TryAdd(identity, reference))
                    throw new InvalidDataException("candidate_source_closure_duplicate");
            }
            if (sourceByIdentity.Count != receiptBySource.Count ||
                sourceByIdentity.Keys.Any(identity => !receiptBySource.ContainsKey(identity)))
                throw new InvalidDataException("candidate_materialization_source_mismatch");

            var actualByLocator = new Dictionary<TargetIdentity, RecoveryObjectReference>();
            foreach (var reference in materialized)
            {
                var identity = new TargetIdentity(reference.SourceKey, reference.SourceVersionId,
                    reference.Sha256, reference.ByteLength);
                if (!actualByLocator.TryAdd(identity, reference))
                    throw new InvalidDataException("candidate_materialized_closure_duplicate");
            }

            foreach (var (sourceIdentity, expected) in sourceByIdentity)
            {
                var receipt = receiptBySource[sourceIdentity];
                var targetIdentity = new TargetIdentity(receipt.DestinationKey, receipt.DestinationVersionId,
                    receipt.Sha256, receipt.ByteLength);
                if (!actualByLocator.TryGetValue(targetIdentity, out var actual) ||
                    !LogicalIdentityBytes(expected).AsSpan().SequenceEqual(LogicalIdentityBytes(actual)))
                    throw new InvalidDataException("candidate_materialized_closure_mismatch");
            }
        }
        catch (InvalidDataException exception)
        {
            throw new RecoveryWorkerException(exception.Message, exception);
        }
    }

    private static byte[] LogicalIdentityBytes(RecoveryObjectReference reference)
    {
        var canonical = reference.Canonicalized();
        return RecoveryEvidenceCodec.SerializeCanonical(new LogicalIdentity(
            canonical.DataClass,
            canonical.OwnerId,
            canonical.LibraryId,
            canonical.Sha256,
            canonical.ByteLength,
            canonical.LogicalReferences));
    }

    private static void ValidateBucket(string bucket, string name)
    {
        if (string.IsNullOrWhiteSpace(bucket) || bucket.Length > 255 || bucket.Any(char.IsControl))
            throw new InvalidDataException($"candidate_{name}_invalid");
    }

    private readonly record struct SourceIdentity(string Key, string VersionId, string Sha256, long ByteLength);
    private readonly record struct TargetIdentity(string Key, string VersionId, string Sha256, long ByteLength);
    private sealed record LogicalIdentity(RecoveryDataClass DataClass, string OwnerId, string LibraryId,
        string Sha256, long ByteLength, IReadOnlyList<RecoveryLogicalReference> LogicalReferences);
}
