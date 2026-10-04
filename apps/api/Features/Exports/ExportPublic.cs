using Platform.Contracts.Exports;
namespace Platform.Api.Features.Exports;
/// <summary>Storage keys and private object locators never enter public basket, snapshot or manifest payloads.</summary>
public static class ExportPublic
{
    public static object Pin(ExportPin p) => new { p.ItemId, p.AssetId, p.EditId, p.CommitId, OriginalSha256 = p.Original.Sha256, OriginalByteLength = p.Original.ByteLength, XmpSha256 = p.Xmp.Sha256, p.DisplayName, p.VersionLabel, p.Reference, p.AdvancedHistory, p.SelectedAtUtc };
    public static object? Basket(ExportBasket? b) => b is null ? null : new { b.Id, b.Name, b.IsDefault, b.Revision, Items = b.Items.Select(i => new { Pin = Pin(i.Pin), i.Availability, i.PreviewState, i.OutputName, i.Reason, i.OriginalAvailability, i.OriginalOutputName, i.OriginalReason }) };
    public static object Snapshot(ExportSnapshot s) => new { s.SchemaVersion, s.RequestId, s.PresetId, s.MetadataConfirmed, s.WarningAcknowledged, s.CapturedAtUtc, s.Renderer, Items = s.Items.Select(Pin) };
    public static object Manifest(ExportManifest m) => new { m.SchemaVersion, Snapshot = Snapshot(m.Snapshot), m.Outcomes };
    public static object Request(ExportRequestView r) => new { r.Id, r.BasketId, r.State, Snapshot = Snapshot(r.Snapshot), r.Outcomes, r.CreatedAtUtc, r.ExpiresAtUtc, r.PackageSha256, r.PackageByteLength, r.Reason };
}
