using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryMaterializedClosureChecks
{
    public static void Run()
    {
        var expected = SourceClosure();
        var receipts = Receipts(expected);
        RecoveryMaterializedClosureValidator.RequireExactMappedMatch(expected, Mapped(expected, receipts),
            receipts, "source-payload", "recovery-payload");

        Reject(() => RecoveryMaterializedClosureValidator.RequireExactMappedMatch(expected,
            WithVersion(Mapped(expected, receipts), "wrong-version"), receipts, "source-payload", "recovery-payload"),
            "A clone locator with the wrong exact target version must be rejected.");
        Reject(() => RecoveryMaterializedClosureValidator.RequireExactMappedMatch(expected,
            TamperDigest(Mapped(expected, receipts)), receipts, "source-payload", "recovery-payload"),
            "A same-size target digest substitution must be rejected.");
        Reject(() => RecoveryMaterializedClosureValidator.RequireExactMappedMatch(expected,
            AddLogicalReference(Mapped(expected, receipts)), receipts, "source-payload", "recovery-payload"),
            "An extra target logical reference must be rejected.");
        Reject(() => RecoveryMaterializedClosureValidator.RequireExactMappedMatch(expected,
            Mapped(expected, receipts), receipts.Take(1).ToArray(), "source-payload", "recovery-payload"),
            "A missing exact materialization receipt must be rejected.");
        Reject(() => RecoveryMaterializedClosureValidator.RequireExactMappedMatch(expected,
            Mapped(expected, receipts), receipts.Append(receipts[0]).ToArray(), "source-payload", "recovery-payload"),
            "Duplicate materialization receipts must be rejected.");
        Reject(() => RecoveryMaterializedClosureValidator.RequireExactMappedMatch(expected,
            Mapped(expected, receipts), receipts, "source-payload", "source-payload"),
            "Materialization into the source bucket must be rejected.");
    }

    private static IReadOnlyList<RecoveryObjectReference> SourceClosure()
    {
        var owner = "candidate-owner";
        var library = "main";
        var asset = Guid.Parse("a0010000-0000-4000-8000-000000000001");
        var edit = Guid.Parse("e0010000-0000-4000-8000-000000000001");
        return
        [
            new(RecoveryDataClass.OriginalImage, owner, library, "objects/original", "source-original-v1",
                new string('a', 64), 3, [new(owner, library, asset)]),
            new(RecoveryDataClass.XmpSidecar, owner, library, "objects/xmp", "source-xmp-v1",
                new string('b', 64), 4, [new(owner, library, asset, edit)])
        ];
    }

    private static IReadOnlyList<RecoveryMaterializedObjectV1> Receipts(IReadOnlyList<RecoveryObjectReference> expected) =>
        expected.Select((item, index) => new RecoveryMaterializedObjectV1("source-payload", item.SourceKey,
            item.SourceVersionId, item.Sha256, item.ByteLength, "recovery-payload", "sha256/" + item.Sha256,
            $"target-v{index + 1}")).ToArray();

    private static IReadOnlyList<RecoveryObjectReference> Mapped(IReadOnlyList<RecoveryObjectReference> expected,
        IReadOnlyList<RecoveryMaterializedObjectV1> receipts) => expected.Select(item =>
    {
        var receipt = receipts.Single(candidate => candidate.SourceKey == item.SourceKey &&
            candidate.SourceVersionId == item.SourceVersionId && candidate.Sha256 == item.Sha256 &&
            candidate.ByteLength == item.ByteLength);
        return item with { SourceKey = receipt.DestinationKey, SourceVersionId = receipt.DestinationVersionId };
    }).ToArray();

    private static IReadOnlyList<RecoveryObjectReference> TamperDigest(IReadOnlyList<RecoveryObjectReference> values) =>
        values.Select((item, index) => index == 0 ? item with { Sha256 = new string('c', 64) } : item).ToArray();

    private static IReadOnlyList<RecoveryObjectReference> AddLogicalReference(IReadOnlyList<RecoveryObjectReference> values) =>
        values.Select((item, index) => index == 0
            ? item with { LogicalReferences = item.LogicalReferences.Append(new("candidate-owner", "main",
                Guid.Parse("a0020000-0000-4000-8000-000000000002"))).ToArray() }
            : item).ToArray();

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (RecoveryWorkerException) { return; }
        throw new InvalidOperationException(message);
    }

    private static IReadOnlyList<RecoveryObjectReference> WithVersion(IReadOnlyList<RecoveryObjectReference> items,
        string version) => items.Select((item, index) => index == 0 ? item with { SourceVersionId = version } : item).ToArray();
}
