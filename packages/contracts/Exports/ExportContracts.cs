using System.Security.Cryptography;
using System.Text;
using Platform.Contracts.Rendering;
namespace Platform.Contracts.Exports;
public sealed class ExportProblem(string code, int status = 409) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
public sealed record ExportPreset(string Id, string Format, int? MaximumEdge, string Color, string Metadata, bool ConfirmationRequired)
{
    public static IReadOnlyList<ExportPreset> Catalog { get; } = [new("originals-v1", "original", null, "unchanged", "unchanged", true), new("jpeg-2048-private-v1", "jpeg", 2048, "sRGB", "private-v1", false), new("jpeg-2048-retain-v1", "jpeg", 2048, "sRGB", "retain-v1", true)];
    public static ExportPreset Get(string id) => Catalog.SingleOrDefault(p => p.Id == id) ?? throw new ExportProblem("unsupported_preset", 400);
}
public sealed record ExportPin(Guid ItemId, Guid AssetId, Guid EditId, string CommitId, RenderInputObject Original, RenderInputObject Xmp, string DisplayName, string VersionLabel, string? Reference, bool AdvancedHistory, DateTimeOffset SelectedAtUtc)
{
    public void Validate()
    {
        if (ItemId == Guid.Empty || AssetId == Guid.Empty || EditId == Guid.Empty || !ExportIntegrity.IsSha256(CommitId) || SelectedAtUtc.Offset != TimeSpan.Zero || string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 255 || string.IsNullOrWhiteSpace(VersionLabel) || VersionLabel.Length > 256) throw new ExportProblem("invalid_pin", 400);
        Original.Validate(); Xmp.Validate();
    }
}
public sealed record ExportBasket(Guid Id, string Name, bool IsDefault, long Revision, IReadOnlyList<ExportItemView> Items);
public sealed record ExportItemView(ExportPin Pin, string Availability, string PreviewState, string OutputName, string? Reason);
public sealed record ExportSelection(Guid AssetId, Guid EditId, string SelectedVersion, bool AdvancedHistory = false);
public sealed record ExportSubmit(string PresetId, long Revision, bool MetadataConfirmed = false, bool WarningAcknowledged = false);
public sealed record ExportRenderer(string Profile, string Release, string Digest, string Matrix);
public sealed record ExportSnapshot(int SchemaVersion, Guid RequestId, string PresetId, bool MetadataConfirmed, DateTimeOffset CapturedAtUtc, IReadOnlyList<ExportPin> Items, ExportRenderer? Renderer = null, bool WarningAcknowledged = false);
public sealed record ExportTransform(string Id, string InputSha256, string OutputSha256);
public sealed record ExportOutcome(Guid ItemId, string Status, string? Reason, string? Filename, long? ByteLength, string? Sha256, RenderProvenance? Render = null, ExportTransform? Transform = null);
public sealed record ExportManifest(int SchemaVersion, ExportSnapshot Snapshot, IReadOnlyList<ExportOutcome> Outcomes)
{
    public void Validate()
    {
        if (SchemaVersion != 1 || Snapshot.SchemaVersion != 1 || Snapshot.Items.Count == 0 || Outcomes.Count != Snapshot.Items.Count || Outcomes.Select(o => o.ItemId).Distinct().Count() != Outcomes.Count) throw new ExportProblem("integrity_error");
        var preset = ExportPreset.Get(Snapshot.PresetId);
        if (preset.ConfirmationRequired && !Snapshot.MetadataConfirmed) throw new ExportProblem("integrity_error");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in Snapshot.Items)
        {
            pin.Validate();
            var item = Outcomes.SingleOrDefault(o => o.ItemId == pin.ItemId) ?? throw new ExportProblem("integrity_error");
            if (!ExportStates.Item.Contains(item.Status) || item.Status == "pending") throw new ExportProblem("integrity_error");
            if (item.Status != "completed")
            {
                if (!ExportStates.Reasons.Contains(item.Reason ?? "") || item.Filename is not null || item.Sha256 is not null || item.ByteLength is not null) throw new ExportProblem("integrity_error");
                continue;
            }
            if (item.Filename != ExportNames.For(pin, preset) || !names.Add(item.Filename) || !ExportIntegrity.IsSha256(item.Sha256) || item.ByteLength is null or < 1) throw new ExportProblem("integrity_error");
            if (preset.Format == "original")
            {
                if (item.Sha256 != pin.Original.Sha256 || item.ByteLength != pin.Original.ByteLength || item.Render is not null || item.Transform is not null) throw new ExportProblem("integrity_error");
            }
            else
            {
                var render = item.Render ?? throw new ExportProblem("integrity_error"); render.Validate();
                if (render.SourceSha256 != pin.Original.Sha256 || render.XmpSha256 != pin.Xmp.Sha256 || render.SelectedVersion != pin.CommitId || item.Transform?.Id != preset.Metadata || item.Transform.OutputSha256 != item.Sha256 || !ExportIntegrity.IsSha256(item.Transform.InputSha256)) throw new ExportProblem("integrity_error");
                if (Snapshot.Renderer is not { } selected || render.Profile != selected.Profile || render.RendererRelease != selected.Release || render.RendererArtifactDigest != selected.Digest || render.CompatibilityMatrix != selected.Matrix) throw new ExportProblem("integrity_error");
            }
        }
    }
}
public sealed record ExportRequestView(Guid Id, Guid BasketId, string State, ExportSnapshot Snapshot, IReadOnlyList<ExportOutcome> Outcomes, DateTimeOffset CreatedAtUtc, DateTimeOffset? ExpiresAtUtc, string? PackageSha256, long? PackageByteLength, string? Reason);
public sealed record ExportLimits(int Items = 250, int Baskets = 20, int Requests = 10, long PackageBytes = 10L * 1024 * 1024 * 1024, int PreparationSeconds = 7200, int RetentionHours = 24)
{
    public void Validate() { if (Items is < 1 or > 250 || Baskets is < 20 or > 100 || Requests is < 10 or > 100 || PackageBytes is < 1 or > 10L * 1024 * 1024 * 1024 || PreparationSeconds is < 1 or > 7200 || RetentionHours != 24) throw new ExportProblem("invalid_export_configuration", 503); }
}
public static class ExportIntegrity
{
    public static bool IsSha256(string? value) => value is { Length: 64 } && value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string Fingerprint(ExportSubmit v) => Hash(Encoding.UTF8.GetBytes($"export-submit-v1\n{v.PresetId}\n{v.Revision}\n{v.MetadataConfirmed}\n{v.WarningAcknowledged}"));
}
public static class ExportNames
{
    public static string For(ExportPin pin, ExportPreset preset)
    {
        var leaf = pin.DisplayName.Replace('\\', '/').Split('/')[^1]; var dot = leaf.LastIndexOf('.');
        var basename = dot > 0 ? leaf[..dot] : leaf; var extension = preset.Format == "jpeg" ? "jpg" : dot > 0 ? leaf[(dot + 1)..] : "original";
        var stem = new string(basename.Normalize(NormalizationForm.FormC).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').Take(80).ToArray()).Trim('_', '.', ' ');
        if (stem.Length == 0) stem = "image";
        var suffix = new string(extension.Where(char.IsAsciiLetterOrDigit).Take(12).ToArray()); if (suffix.Length == 0) suffix = "original";
        return $"image-{stem}-{pin.ItemId:N}.{suffix.ToLowerInvariant()}";
    }
}
public static class ExportStates
{
    public static readonly HashSet<string> Item = new(StringComparer.Ordinal) { "pending", "completed", "failed", "unavailable", "skipped", "cancelled" };
    public static readonly HashSet<string> Reasons = new(StringComparer.Ordinal) { "source_unavailable", "selected_version_unavailable", "permanently_unavailable", "integrity_error", "incompatible_renderer", "renderer_timeout", "resource_limit", "storage_unavailable", "cancelled", "expired", "retryable", "output_invalid" };
}
public sealed record ExportPurgeEvent(Guid EventId, string OwnerId, string LibraryId, Guid AssetId, Guid? EditId, string Category, string ContentSha256, long Generation, Guid EvidenceId)
{
    public void Validate() { if (EventId == Guid.Empty || string.IsNullOrWhiteSpace(OwnerId) || string.IsNullOrWhiteSpace(LibraryId) || AssetId == Guid.Empty || Category is not ("original" or "xmp") || Category == "xmp" && EditId is null || !ExportIntegrity.IsSha256(ContentSha256) || Generation < 1 || EvidenceId == Guid.Empty) throw new ExportProblem("invalid_purge_event", 400); }
    public bool Affects(ExportPin pin, string presetId) => pin.AssetId == AssetId && (Category == "original" ? pin.Original.Sha256 == ContentSha256 : ExportPreset.Get(presetId).Format != "original" && pin.EditId == EditId && pin.Xmp.Sha256 == ContentSha256);
}
