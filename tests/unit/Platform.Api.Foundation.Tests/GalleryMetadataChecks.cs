using System.Text;
using Platform.Api.Features.Gallery.Metadata;
using System.Text.Json;

internal static class GalleryMetadataChecks
{
    public static void Run()
    {
        foreach (var file in new[] { "sample.jpg", "sample.png", "sample.tiff", "sample.dng" })
        {
            using var original = File.OpenRead($"tests/fixtures/ingest/{file}");
            var metadata = GalleryMetadataReader.Read(original, file);
            Check(metadata.ParseStatus == "complete", $"{file} must parse without errors");
            Check(metadata.Fields.ContainsKey("width") && metadata.Fields.ContainsKey("height"), $"{file} dimensions");
        }

        using var jpeg = File.OpenRead("tests/fixtures/ingest/sample.jpg");
        using var sidecar = Xmp("""
            <x:xmpmeta xmlns:x="adobe:ns:meta/" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:exif="http://ns.adobe.com/exif/1.0/">
              <rdf:RDF><rdf:Description xmp:Rating="4" exif:DateTimeOriginal="2024-05-06T12:00:00+02:00">
                <dc:title><rdf:Alt><rdf:li xml:lang="x-default">Gallery title</rdf:li></rdf:Alt></dc:title>
                <dc:subject><rdf:Bag><rdf:li>Landscapes</rdf:li><rdf:li>Night</rdf:li></rdf:Bag></dc:subject>
              </rdf:Description></rdf:RDF>
            </x:xmpmeta>
            """);
        var merged = GalleryMetadataReader.Read(jpeg, "sample.jpg", sidecar);
        Check(merged.Fields["rating"].Value == "4" && merged.Fields["rating"].Source == "xmp", "XMP rating provenance");
        Check(merged.Fields["title"].Value == "Gallery title", "XMP title");
        Check(merged.Fields["captureDate"].Value!.StartsWith("2024-05-06T10:00:00", StringComparison.Ordinal) && merged.Fields["captureDate"].TimestampBasis == "explicit_offset", "XMP capture time offset");
        Check(merged.Tags.Select(x => x.Value).SequenceEqual(["LANDSCAPES", "NIGHT"]), "XMP tags normalize independently");

        using var clearedOriginal = File.OpenRead("tests/fixtures/ingest/sample.jpg");
        using var clearedXmp = Xmp("""
            <x:xmpmeta xmlns:x="adobe:ns:meta/" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:dc="http://purl.org/dc/elements/1.1/"><rdf:RDF><rdf:Description dc:title="" /></rdf:RDF></x:xmpmeta>
            """);
        var cleared = GalleryMetadataReader.Read(clearedOriginal, "sample.jpg", clearedXmp);
        Check(cleared.Fields["title"].State == "cleared" && cleared.Fields["title"].Source == "xmp", "Explicit XMP clear overrides embedded value");
        Check(cleared.Fields["rating"].State == "missing", "Absent rating stays missing");

        using var rejectedOriginal = File.OpenRead("tests/fixtures/ingest/sample.jpg");
        using var rejectedXmp = Xmp("""
            <x:xmpmeta xmlns:x="adobe:ns:meta/" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmlns:exif="http://ns.adobe.com/exif/1.0/"><rdf:RDF><rdf:Description xmp:Rating="-1" exif:DateTimeOriginal="2024-05-06T12:00:00" exif:GPSLatitude="40,30N" exif:GPSLongitude="74,15W" /></rdf:RDF></x:xmpmeta>
            """);
        var rejected = GalleryMetadataReader.Read(rejectedOriginal, "sample.jpg", rejectedXmp);
        Check(rejected.Fields["rejected"].Value == "true" && rejected.Fields["rating"].State == "cleared", "Darktable rejection convention");
        Check(rejected.Fields["captureDate"].TimestampBasis == "offset_unknown", "Missing timezone stays explicit in provenance");
        Check(rejected.Fields["location"].Value == "40.5,-74.25", "XMP GPS degrees and minutes");

        using var allOriginal = File.OpenRead("tests/fixtures/ingest/sample.png");
        using var allXmp = Xmp("""
            <x:xmpmeta xmlns:x="adobe:ns:meta/" xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:exif="http://ns.adobe.com/exif/1.0/" xmlns:tiff="http://ns.adobe.com/tiff/1.0/" xmlns:darktable="http://darktable.sf.net/"><rdf:RDF><rdf:Description xmp:Rating="5" tiff:Model="Camera X" tiff:Orientation="6" exif:LensModel="Lens Y" exif:FocalLength="50/1" exif:FNumber="28/10" exif:ExposureTime="1/125" exif:ISOSpeedRatings="400" exif:PixelXDimension="640" exif:PixelYDimension="480" darktable:rejected="false"><dc:description><rdf:Alt><rdf:li xml:lang="x-default">Text</rdf:li></rdf:Alt></dc:description><dc:creator><rdf:Seq><rdf:li>Author</rdf:li></rdf:Seq></dc:creator><darktable:colorlabels><rdf:Seq><rdf:li>2</rdf:li></rdf:Seq></darktable:colorlabels></rdf:Description></rdf:RDF></x:xmpmeta>
            """);
        var all = GalleryMetadataReader.Read(allOriginal, "sample.png", allXmp);
        foreach (var name in new[] { "camera", "lens", "focalLength", "aperture", "shutter", "iso", "width", "height", "orientation", "rating", "rejected", "description", "creator" })
            Check(all.Fields[name].Source == "xmp" && all.Fields[name].State == "present", $"{name} XMP override");
        Check(all.Colors.Single().Value == "2", "Color label");
        Check(all.Fields["width"].Value == "640" && all.Fields["height"].Value == "480", "XMP dimensions override PNG header");

        using var brokenOriginal = File.OpenRead("tests/fixtures/ingest/sample.jpg");
        using var broken = Xmp("<!DOCTYPE x [<!ENTITY probe SYSTEM 'file:///c:/windows/win.ini'>]><x>&probe;</x>");
        Check(GalleryMetadataReader.Read(brokenOriginal, "sample.jpg", broken).ParseStatus == "xmp_invalid", "External XML entities are rejected");

        using var patch = JsonDocument.Parse("""
            {"rating":4,"rejected":false,"color":2,"tags":["Night","Archive"],"title":"Edited title","description":"Edited description","location":{"latitude":40.5,"longitude":-74.25}}
            """);
        var patched = GalleryXmpPatcher.Patch(File.ReadAllBytes("tests/fixtures/ingest/sample.dng.xmp"),
            patch.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => item.Value));
        var xml = Encoding.UTF8.GetString(patched);
        Check(xml.Contains("history_modversion", StringComparison.Ordinal), "Unknown darktable history survives XMP patch");
        using var patchedOriginal = File.OpenRead("tests/fixtures/ingest/sample.dng");
        using var patchedSidecar = new MemoryStream(patched);
        var parsedPatch = GalleryMetadataReader.Read(patchedOriginal, "sample.dng", patchedSidecar);
        Check(parsedPatch.Fields["title"].Value == "Edited title" && parsedPatch.Fields["rating"].Value == "4" &&
            parsedPatch.Fields["location"].State == "present" && parsedPatch.Tags.Count == 2 && parsedPatch.Colors.Single().Value == "2", "Patched values round trip through metadata reader");
        using var unsupported = JsonDocument.Parse("{" + "\"history\":true}");
        try { GalleryXmpPatcher.Patch([], unsupported.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => item.Value)); }
        catch (Platform.Api.Features.Gallery.Browse.GalleryProblem error) when (error.Code == "unsupported_metadata_field") { return; }
        throw new InvalidOperationException("Gallery metadata: unsupported XMP field accepted");
    }

    private static MemoryStream Xmp(string value) => new(Encoding.UTF8.GetBytes(value));
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException($"Gallery metadata: {label}");
    }
}
