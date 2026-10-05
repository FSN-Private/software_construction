using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Npgsql;
using Platform.Api.Features.Gallery.Browse;
using static Platform.Storage.IngestDb;

internal static class GalleryBrowseChecks
{
    public static async Task RunAsync(NpgsqlDataSource db)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Gallery:LibraryId"] = "main" }).Build();
        var protection = new EphemeralDataProtectionProvider();
        var browse = new GalleryBrowseService(db, protection, configuration);
        var first = await browse.BrowseAsync("test-owner", "main", Query(("sort", "filename"), ("limit", "2")), default);
        Check(first.Count == 4 && first.Cards.Count == 2 && first.NextCursor is not null, "First page has exact count and cursor");
        var second = await browse.BrowseAsync("test-owner", "main", Query(("sort", "filename"), ("limit", "2"), ("cursor", first.NextCursor!)), default);
        Check(second.Cards.Count == 2 && !first.Cards.Select(card => card.AssetId).Intersect(second.Cards.Select(card => card.AssetId)).Any(), "Adjacent pages have no repeated assets");
        foreach (var sort in new[] { "captureDate", "importDate", "rating", "dimensions" })
        foreach (var direction in new[] { "asc", "desc" })
        {
            var pageOne = await browse.BrowseAsync("test-owner", "main", Query(("sort", sort), ("direction", direction), ("limit", "2")), default);
            var pageTwo = await browse.BrowseAsync("test-owner", "main", Query(("sort", sort), ("direction", direction), ("limit", "2"), ("cursor", pageOne.NextCursor!)), default);
            Check(pageOne.Cards.Count + pageTwo.Cards.Count == 4 && pageOne.Cards.Select(card => card.AssetId).Concat(pageTwo.Cards.Select(card => card.AssetId)).Distinct().Count() == 4, $"{sort}/{direction} stable page traversal");
        }
        var filtered = await browse.BrowseAsync("test-owner", "main", Query(("fileType", "dng")), default);
        Check(filtered.Count == 1 && filtered.Cards.Single().FileType == "dng", "File type filter");
        await RejectAsync(() => browse.BrowseAsync("test-owner", "main", Query(("fileType", "dng"), ("cursor", first.NextCursor!)), default), "cursor_expired");
        await RejectAsync(() => browse.BrowseAsync("another-owner", "main", Query(("sort", "filename"), ("limit", "2"), ("cursor", first.NextCursor!)), default), "cursor_expired");
        await using (var connection = await db.OpenConnectionAsync())
            await ExecuteAsync(connection, "UPDATE gallery_refresh SET invalidation_revision=invalidation_revision+1 WHERE owner_id='test-owner' AND library_id='main' AND branch_name='main'", default);
        await RejectAsync(() => browse.BrowseAsync("test-owner", "main", Query(("sort", "filename"), ("limit", "2"), ("cursor", first.NextCursor!)), default), "cursor_expired");
        await RejectAsync(() => browse.BrowseAsync("test-owner", "main", Query(("locationPresent", "true")), default), "location_hidden");
        await RejectAsync(() => browse.BrowseAsync("test-owner", "other", Query(), default), "library_unavailable");
        await RejectAsync(() => browse.BrowseAsync("test-owner", "main", Query(("sql", "anything")), default), "invalid_filter");
        var details = new GalleryDetailService(db, configuration);
        var detail = await details.GetAsync("test-owner", "main", "main", first.Cards[0].AssetId, default);
        Check(detail.AssetId == first.Cards[0].AssetId && detail.OriginalSha256.Length == 64 && detail.LifecycleVersion > 0, "Detail binds authoritative identity");
        Check(detail.Thumbnail.State == "pending" && detail.Thumbnail.RequestId is null, "Missing thumbnail is explicitly pending");
        Guid previewAsset;
        await using (var connection = await db.OpenConnectionAsync())
            previewAsset = (Guid)(await ScalarAsync(connection, "SELECT asset_id FROM gallery_projection WHERE owner_id='test-owner' AND xmp_sha256 IS NOT NULL LIMIT 1", default)
                ?? throw new InvalidOperationException("Gallery preview fixture requires an XMP asset"));
        var previewDetail = await details.GetAsync("test-owner", "main", "main", previewAsset, default);
        var requestId = Guid.NewGuid(); var renditionId = Guid.NewGuid();
        await using (var connection = await db.OpenConnectionAsync())
        {
            var otherSha = ((string?)await ScalarAsync(connection, "SELECT sha256 FROM content_objects WHERE sha256<>$1 AND integrity_state='authoritative' LIMIT 1", default, previewDetail.OriginalSha256))?.Trim()
                ?? throw new InvalidOperationException("Gallery mismatch fixture requires another content object");
            await ExecuteAsync(connection, "INSERT INTO rendering_compatibility(matrix_version,renderer_release,renderer_artifact_digest,profile,state) VALUES('gallery-preview-test','test','unapproved','test','pending') ON CONFLICT DO NOTHING", default);
            await ExecuteAsync(connection, "INSERT INTO render_requests(id,owner_id,library_id,request_key,asset_id,edit_id,selected_version,rendition,profile,renderer_release,renderer_artifact_digest,matrix_version,state) VALUES($1,'test-owner','main',$2,$3,$4,'fixture','thumbnail-512','test','test','unapproved','gallery-preview-test','completed')", default,
                requestId, new string('a', 64), previewAsset, previewDetail.EditId!.Value);
            await ExecuteAsync(connection, "INSERT INTO renditions(id,request_id,object_key,object_version,output_sha256,byte_length,media_type,completed_at) VALUES($1,$2,$3,'fixture',$4,1,'image/jpeg',now())", default,
                renditionId, requestId, "gallery-preview-fixture/" + requestId, new string('b', 64));
            await ExecuteAsync(connection, "INSERT INTO render_provenance(rendition_id,source_sha256,xmp_sha256,selected_version,renderer_release,renderer_artifact_digest,profile,matrix_version,completed_at) VALUES($1,$2,$3,'fixture','test','unapproved','test','gallery-preview-test',now())", default,
                renditionId, otherSha, previewDetail.XmpSha256!);
            Check((await details.GetAsync("test-owner", "main", "main", previewAsset, default)).Thumbnail.State == "pending", "Mismatched provenance cannot expose another source preview");
            await ExecuteAsync(connection, "UPDATE render_requests SET state='failed' WHERE id=$1", default, requestId);
            Check((await details.GetAsync("test-owner", "main", "main", previewAsset, default)).Thumbnail.State == "failed", "Failed thumbnail is labelled without a derivative locator");
            await ExecuteAsync(connection, "UPDATE render_requests SET state='completed' WHERE id=$1; UPDATE render_provenance SET source_sha256=$2 WHERE rendition_id=$3", default,
                requestId, previewDetail.OriginalSha256, renditionId);
            Check((await details.GetAsync("test-owner", "main", "main", previewAsset, default)).Thumbnail is { State: "available", RequestId: not null }, "Matching exact provenance enables preview");
        }
        Check(detail.Fields.All(field => field.Name is not ("location" or "locationPresent")), "Default preference removes coordinates from normal detail");
        await RejectAsync(() => details.GetAsync("another-owner", "main", "main", first.Cards[0].AssetId, default), "asset_unavailable");
        var preferences = new GalleryPreferenceService(db, configuration);
        Check((await preferences.GetAsync("test-owner", "main", default)).HideGeolocation, "Geolocation defaults hidden");
        await RejectAsync(() => browse.FilterValuesAsync("test-owner", "main", "main", "latitude", null, default), "location_hidden");
        await RejectAsync(() => browse.FilterValuesAsync("test-owner", "main", "main", "longitude", null, default), "location_hidden");
        await using (var locationFixture = db.CreateCommand("UPDATE gallery_projection SET has_location=true,latitude=40.5,longitude=-74.25 WHERE owner_id='test-owner' AND asset_id=$1"))
        {
            locationFixture.Parameters.AddWithValue(first.Cards[0].AssetId);
            await locationFixture.ExecuteNonQueryAsync();
        }
        var beforeLocationReveal = await RecoverySequenceAsync("test-owner");
        var revealed = await preferences.RevealAsync("test-owner", "main", "main", first.Cards[0].AssetId, "op_gallery_test", default);
        Check(revealed.Latitude == 40.5m && revealed.Longitude == -74.25m, "Explicit reveal returns retained coordinates");
        Check(await RecoverySequenceAsync("test-owner") == beforeLocationReveal + 1,
            "An authorized coordinate reveal commits its access audit and recovery watermark together.");
        await RejectAsync(() => preferences.RevealAsync("another-owner", "main", "main", first.Cards[0].AssetId, "op_gallery_test", default), "location_unavailable");
        Check(await RecoverySequenceAsync("test-owner") == beforeLocationReveal + 1,
            "A foreign coordinate denial creates no successful access watermark.");
        async Task<long> RecoverySequenceAsync(string scopeOwner)
        {
            await using var connection = await db.OpenConnectionAsync();
            return Convert.ToInt64(await ScalarAsync(connection,
                "SELECT last_sequence FROM recovery_change_watermarks WHERE owner_id=$1 AND library_id='main'", default, scopeOwner));
        }
        var watermarkBeforePreference = await RecoverySequenceAsync("test-owner");
        var visible = await preferences.SetAsync("test-owner", "main", new(false, 1), default, "gallery-preference-show");
        Check(!visible.HideGeolocation && visible.Version == 2, "Owner preference updates with CAS");
        Check(await RecoverySequenceAsync("test-owner") == watermarkBeforePreference + 1,
            "An accepted gallery preference mutation commits with one recovery change sequence.");
        Check((await browse.FilterValuesAsync("test-owner", "main", "main", "latitude", null, default)).Values.Any(value => decimal.TryParse(value.Value, System.Globalization.CultureInfo.InvariantCulture, out var latitude) && latitude == 40.5m),
            "Visible owner location exposes existing numeric choices");
        Check((await browse.FilterValuesAsync("test-owner", "main", "main", "longitude", null, default)).Values.Any(value => decimal.TryParse(value.Value, System.Globalization.CultureInfo.InvariantCulture, out var longitude) && longitude == -74.25m),
            "Visible owner longitude exposes existing numeric choices");
        Check(await preferences.SetAsync("test-owner", "main", new(false, 1), default, "gallery-preference-show") == visible, "Preference idempotent replay");
        Check(await RecoverySequenceAsync("test-owner") == watermarkBeforePreference + 1,
            "An idempotent preference replay does not create another recovery ledger entry.");
        await RejectAsync(() => preferences.SetAsync("test-owner", "main", new(true, 1), default, "gallery-preference-stale"), "preference_conflict");
        var locationMatches = await browse.BrowseAsync("test-owner", "main", Query(("locationPresent", "true")), default);
        Check(locationMatches.Count >= 1 && locationMatches.Cards.Any(card => card.AssetId == first.Cards[0].AssetId) && locationMatches.Cards.All(card => card.AssetId != Guid.Empty), "Visible location filter includes retained asset");
        await using (var connection = await db.OpenConnectionAsync())
        {
            await ExecuteAsync(connection, """
                UPDATE gallery_projection SET camera='Unique Camera',lens='Unique Lens',focal_length_mm=50,aperture=2.8,shutter_seconds=0.008,
                  iso=400,width=640,height=480,orientation=6,rating=5,rejected=false,title='Unique Title',description='Unique Description',
                  creator='Unique Creator',captured_at='2024-05-06T10:00:00Z',imported_at='2024-05-07T10:00:00Z'
                WHERE owner_id='test-owner' AND asset_id=$1
                """, default, first.Cards[0].AssetId);
            foreach (var (facet, value) in new[] { ("camera", "UNIQUE CAMERA"), ("lens", "UNIQUE LENS"), ("title", "UNIQUE TITLE"),
                         ("description", "UNIQUE DESCRIPTION"), ("creator", "UNIQUE CREATOR"), ("tag", "UNIQUE TAG"), ("color", "RED") })
                await ExecuteAsync(connection, """
                    INSERT INTO gallery_facet_values(owner_id,library_id,branch_name,generation,asset_id,facet,ordinal,source,value_state,source_sha256,text_value,display_text)
                    SELECT g.owner_id,g.library_id,g.branch_name,g.generation,g.asset_id,$2,0,'derived','present',g.original_sha256,$3,$3
                    FROM gallery_projection g WHERE g.owner_id='test-owner' AND g.asset_id=$1
                    ON CONFLICT(owner_id,library_id,branch_name,generation,asset_id,facet,ordinal)
                    DO UPDATE SET value_state='present',text_value=excluded.text_value,display_text=excluded.display_text
                    """, default, first.Cards[0].AssetId, facet, value);
        }
        await using (var connection = await db.OpenConnectionAsync())
            await ExecuteAsync(connection, "UPDATE gallery_projection SET camera='Off-page Camera' WHERE owner_id='test-owner' AND asset_id=$1", default, second.Cards[1].AssetId);
        var cameraChoices = await browse.FilterValuesAsync("test-owner", "main", "main", "camera", null, default);
        Check(cameraChoices.Values.Any(value => value.Value == "Off-page Camera"), "Filter choices include values outside the first gallery page");
        Check((await browse.FilterValuesAsync("test-owner", "main", "main", "camera", "unique", default)).Values.Count == 1,
            "Filter choices support bounded search");
        Check((await browse.FilterValuesAsync("another-owner", "main", "main", "camera", null, default)).Values.Count == 0,
            "Filter choices cannot expose another owner's records");
        Check((await browse.FilterValuesAsync("test-owner", "main", "main", "iso", null, default)).Values.Any(value => value.Value == "400"),
            "Numeric filter choices use invariant values");
        Check((await browse.FilterValuesAsync("test-owner", "main", "other", "camera", null, default)).Values.Count == 0,
            "Filter choices are branch scoped");
        await RejectAsync(() => browse.FilterValuesAsync("test-owner", "main", "main", "location", null, default), "invalid_filter");
        await RejectAsync(() => browse.FilterValuesAsync("test-owner", "main", "main", "camera", new string('x', 101), default), "invalid_filter");
        var cases = new[] {
            ("captureFrom", "2024-05-06T09:00:00Z"), ("captureTo", "2024-05-06T11:00:00Z"),
            ("importFrom", "2024-05-07T09:00:00Z"), ("importTo", "2024-05-07T11:00:00Z"),
            ("path", first.Cards[0].Filename), ("fileType", first.Cards[0].FileType), ("camera", "Unique Camera"), ("lens", "Unique Lens"),
            ("focalLengthMin", "49"), ("focalLengthMax", "51"), ("apertureMin", "2"), ("apertureMax", "3"),
            ("shutterMin", "0.007"), ("shutterMax", "0.009"), ("isoMin", "399"), ("isoMax", "401"),
            ("widthMin", "639"), ("widthMax", "641"), ("heightMin", "479"), ("heightMax", "481"),
            ("orientation", "6"), ("rating", "5"), ("rejected", "false"), ("color", "red"), ("tag", "unique tag"),
            ("title", "Unique Title"), ("description", "Unique Description"), ("creator", "Unique Creator"),
            ("locationPresent", "true"), ("latitudeMin", "40"), ("latitudeMax", "41"), ("longitudeMin", "-75"), ("longitudeMax", "-74")
        };
        foreach (var (key, value) in cases)
        {
            var match = await browse.BrowseAsync("test-owner", "main", Query((key, value)), default);
            Check(match.Cards.Any(card => card.AssetId == first.Cards[0].AssetId), $"{key} filter includes matching asset");
        }
        var combined = await browse.BrowseAsync("test-owner", "main", Query(("camera", "Unique Camera"), ("tag", "unique tag"), ("rating", "5")), default);
        Check(combined.Count == 1 && combined.Cards.Single().AssetId == first.Cards[0].AssetId, "Combined filters intersect distinct facets");
        foreach (var sort in new[] { "captureDate", "importDate", "rating", "dimensions" })
        foreach (var direction in new[] { "asc", "desc" })
        {
            var pageOne = await browse.BrowseAsync("test-owner", "main", Query(("sort", sort), ("direction", direction), ("limit", "2")), default);
            var pageTwo = await browse.BrowseAsync("test-owner", "main", Query(("sort", sort), ("direction", direction), ("limit", "2"), ("cursor", pageOne.NextCursor!)), default);
            Check(pageOne.Cards.Select(card => card.AssetId).Concat(pageTwo.Cards.Select(card => card.AssetId)).Distinct().Count() == 4, $"{sort}/{direction} mixed null page traversal");
        }
    }

    private static QueryCollection Query(params (string Key, string Value)[] pairs) => new(pairs.ToDictionary(pair => pair.Key, pair => new StringValues(pair.Value)));
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException("Gallery browse: " + label); }
    private static async Task RejectAsync(Func<Task> action, string code)
    {
        try { await action(); }
        catch (GalleryProblem error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Gallery browse: expected " + code);
    }
}
