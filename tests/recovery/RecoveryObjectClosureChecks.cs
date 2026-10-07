using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryObjectClosureChecks
{
    public static void Run()
    {
        var assetA = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var assetB = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var first = new RecoveryObjectReference(RecoveryDataClass.OriginalImage, "owner", "main",
            "objects/aa", "source-v1", new string('a', 64), 17,
            [new("owner", "main", assetA), new("owner", "main", assetB)]);
        var reordered = first with { LogicalReferences = [new("owner", "main", assetB), new("owner", "main", assetA)] };
        RecoveryObjectClosureValidator.RequireExactMatch([first], [reordered]);

        Rejects(() => RecoveryObjectClosureValidator.RequireExactMatch([first],
            [first with { SourceVersionId = "source-v2" }]));
        Rejects(() => RecoveryObjectClosureValidator.RequireExactMatch([first],
            [first with { Sha256 = new string('b', 64) }]));
        Rejects(() => RecoveryObjectClosureValidator.RequireExactMatch([first],
            [first with { ByteLength = 18 }]));
        Rejects(() => RecoveryObjectClosureValidator.RequireExactMatch([first],
            [first with { LogicalReferences = [new("owner", "main", assetA)] }]));
        Rejects(() => RecoveryObjectClosureValidator.RequireExactMatch([first], []));
    }

    private static void Rejects(Action action)
    {
        try
        {
            action();
        }
        catch (RecoveryWorkerException exception) when (exception.SafeCode == "restored_object_closure_mismatch")
        {
            return;
        }
        throw new InvalidOperationException("Expected exact object-closure mismatch rejection.");
    }
}
