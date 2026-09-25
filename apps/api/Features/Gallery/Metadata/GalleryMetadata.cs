using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace Platform.Api.Features.Gallery.Metadata;

public sealed record GalleryFacet(string Name, string? Value, string Source, string State, string? TimestampBasis = null);

public sealed record GalleryMetadata(IReadOnlyDictionary<string, GalleryFacet> Fields, IReadOnlyList<GalleryFacet> Tags,
    IReadOnlyList<GalleryFacet> Colors, string ParseStatus);

public static class GalleryMetadataReader
{
    public const long MaximumOriginalBytes = 128L * 1024 * 1024;
    public const int MaximumXmpBytes = 2 * 1024 * 1024;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Exif = "http://ns.adobe.com/exif/1.0/";
    private static readonly XNamespace Tiff = "http://ns.adobe.com/tiff/1.0/";
    private static readonly XNamespace Darktable = "http://darktable.sf.net/";

    public static GalleryMetadata Read(Stream original, string fileType, Stream? sidecar = null)
    {
        if (!original.CanRead || (original.CanSeek && original.Length > MaximumOriginalBytes))
            throw new InvalidDataException("original_too_large");
        var fields = new Dictionary<string, GalleryFacet>(StringComparer.Ordinal);
        var tags = new List<GalleryFacet>();
        var colors = new List<GalleryFacet>();
        var status = "complete";
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(new LimitedReadStream(original, MaximumOriginalBytes), fileType);
            var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            var sub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
            var rasterHeader = directories.FirstOrDefault(directory => directory.Name is "PNG-IHDR" or "JPEG");
            PutText(fields, "camera", Join(ifd0?.GetString(0x010F), ifd0?.GetString(0x0110)), "embedded");
            PutText(fields, "lens", sub?.GetString(0xA434), "embedded");
            PutNumber(fields, "focalLength", Rational(sub, 0x920A), "embedded");
            PutNumber(fields, "aperture", Rational(sub, 0x829D), "embedded");
            PutNumber(fields, "shutter", Rational(sub, 0x829A), "embedded");
            PutInteger(fields, "iso", sub?.GetString(0x8827), "embedded", 1, 10000000);
            PutInteger(fields, "width", sub?.GetString(0xA002) ?? ifd0?.GetString(0x0100) ?? HeaderValue(rasterHeader, "Image Width"), "embedded", 1, 1000000);
            PutInteger(fields, "height", sub?.GetString(0xA003) ?? ifd0?.GetString(0x0101) ?? HeaderValue(rasterHeader, "Image Height"), "embedded", 1, 1000000);
            PutInteger(fields, "orientation", ifd0?.GetString(0x0112), "embedded", 1, 8);
            PutDate(fields, sub?.GetString(0x9003) ?? ifd0?.GetString(0x0132), sub?.GetString(0x9011), "embedded");
            PutText(fields, "title", ifd0?.GetString(0x010E), "embedded");
            PutText(fields, "creator", ifd0?.GetString(0x013B), "embedded");
            if (gps?.GetGeoLocation() is { } location)
                PutLocation(fields, location.Latitude, location.Longitude, "embedded");
        }
        catch (Exception error) when (error is ImageProcessingException or IOException or InvalidDataException)
        {
            status = "embedded_invalid";
        }

        if (sidecar is not null)
        {
            try
            {
                if (sidecar.CanSeek && sidecar.Length > MaximumXmpBytes)
                    throw new InvalidDataException("xmp_too_large");
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    MaxCharactersInDocument = MaximumXmpBytes, MaxCharactersFromEntities = 0 };
                using var xml = XmlReader.Create(new LimitedReadStream(sidecar, MaximumXmpBytes), settings);
                var document = XDocument.Load(xml);
                PutXmpText(fields, document, "title", Dc + "title");
                PutXmpText(fields, document, "description", Dc + "description");
                PutXmpText(fields, document, "creator", Dc + "creator");
                PutXmpInteger(fields, document, "rating", Xmp + "Rating", 0, 5);
                if (Find(document, Xmp + "Rating") == "-1")
                {
                    fields["rating"] = new("rating", null, "xmp", "cleared");
                    fields["rejected"] = new("rejected", "true", "xmp", "present");
                }
                PutXmpBool(fields, document, "rejected", Darktable + "rejected");
                PutXmpDate(fields, document, Exif + "DateTimeOriginal");
                PutXmpText(fields, document, "camera", Tiff + "Model");
                PutXmpText(fields, document, "lens", Exif + "LensModel");
                PutXmpNumber(fields, document, "focalLength", Exif + "FocalLength");
                PutXmpNumber(fields, document, "aperture", Exif + "FNumber");
                PutXmpNumber(fields, document, "shutter", Exif + "ExposureTime");
                PutXmpInteger(fields, document, "iso", Exif + "ISOSpeedRatings", 1, 10000000);
                PutXmpInteger(fields, document, "width", Exif + "PixelXDimension", 1, 1000000);
                PutXmpInteger(fields, document, "height", Exif + "PixelYDimension", 1, 1000000);
                PutXmpInteger(fields, document, "orientation", Tiff + "Orientation", 1, 8);
                ReadXmpList(document, Dc + "subject", "tag", tags);
                ReadXmpList(document, Darktable + "colorlabels", "color", colors);
                var latitude = Find(document, Exif + "GPSLatitude");
                var longitude = Find(document, Exif + "GPSLongitude");
                if (latitude is not null || longitude is not null)
                {
                    if (latitude is null || longitude is null || latitude.Length == 0 || longitude.Length == 0)
                        fields["location"] = new("location", null, "xmp", "cleared");
                    else if (TryCoordinate(latitude, true, out var lat) && TryCoordinate(longitude, false, out var lon))
                        PutLocation(fields, lat, lon, "xmp");
                    else fields["location"] = new("location", null, "xmp", "invalid");
                }
            }
            catch (Exception error) when (error is XmlException or IOException or InvalidDataException)
            {
                status = status == "complete" ? "xmp_invalid" : "embedded_and_xmp_invalid";
            }
        }
        foreach (var name in new[] { "captureDate", "camera", "lens", "focalLength", "aperture", "shutter", "iso",
                     "width", "height", "orientation", "rating", "rejected", "title", "description", "creator", "location" })
            fields.TryAdd(name, new(name, null, "derived", "missing"));
        if (tags.Count == 0) tags.Add(new("tag", null, "derived", "missing"));
        if (colors.Count == 0) colors.Add(new("color", null, "derived", "missing"));
        return new GalleryMetadata(fields, tags, colors, status);
    }

    private static string? Find(XDocument document, XName name)
    {
        var element = document.Descendants(name).FirstOrDefault();
        if (element is not null)
            return element.Descendants().FirstOrDefault(child => child.Name.LocalName == "li")?.Value ?? element.Value;
        return document.Descendants().Attributes(name).FirstOrDefault()?.Value;
    }

    private static void PutXmpText(Dictionary<string, GalleryFacet> fields, XDocument xml, string name, XName key)
    {
        var value = Find(xml, key);
        if (value is not null) PutText(fields, name, value, "xmp");
    }

    private static void PutXmpInteger(Dictionary<string, GalleryFacet> fields, XDocument xml, string name, XName key, int min, int max)
    {
        var value = Find(xml, key);
        if (value is not null) PutInteger(fields, name, value, "xmp", min, max);
    }

    private static void PutXmpNumber(Dictionary<string, GalleryFacet> fields, XDocument xml, string name, XName key)
    {
        var value = Find(xml, key);
        if (value is not null) PutNumber(fields, name, ParseNumber(value), "xmp", value);
    }

    private static void PutXmpDate(Dictionary<string, GalleryFacet> fields, XDocument xml, XName key)
    {
        var value = Find(xml, key);
        if (value is not null) PutDate(fields, value, null, "xmp");
    }

    private static void PutXmpBool(Dictionary<string, GalleryFacet> fields, XDocument xml, string name, XName key)
    {
        var value = Find(xml, key);
        if (value is null) return;
        fields[name] = value.Length == 0 ? new(name, null, "xmp", "cleared") :
            value is "1" or "true" ? new(name, "true", "xmp", "present") :
            value is "0" or "false" ? new(name, "false", "xmp", "present") : new(name, null, "xmp", "invalid");
    }

    private static void ReadXmpList(XDocument xml, XName key, string name, List<GalleryFacet> result)
    {
        var raw = Find(xml, key);
        if (raw is null) return;
        var element = xml.Descendants(key).FirstOrDefault();
        var items = element?.Descendants().Where(child => child.Name.LocalName == "li").Select(child => child.Value).ToArray()
            ?? [raw];
        if (items.Length == 0 || items.All(string.IsNullOrWhiteSpace))
        {
            result.Add(new(name, null, "xmp", "cleared"));
            return;
        }
        foreach (var item in items.Where(item => !string.IsNullOrWhiteSpace(item)).Take(128))
            result.Add(new(name, Normalize(item), "xmp", "present"));
    }

    private static void PutText(Dictionary<string, GalleryFacet> fields, string name, string? value, string source)
    {
        if (value is null) return;
        fields[name] = string.IsNullOrWhiteSpace(value) ? new(name, null, source, "cleared") :
            new(name, value.Trim().Normalize(NormalizationForm.FormKC), source, "present");
    }

    private static void PutInteger(Dictionary<string, GalleryFacet> fields, string name, string? value, string source, int min, int max)
    {
        if (value is null) return;
        fields[name] = value.Length == 0 ? new(name, null, source, "cleared") :
            int.TryParse(value, NumberStyles.Integer, Invariant, out var parsed) && parsed >= min && parsed <= max
                ? new(name, parsed.ToString(Invariant), source, "present") : new(name, null, source, "invalid");
    }

    private static void PutNumber(Dictionary<string, GalleryFacet> fields, string name, double? value, string source, string? raw = null)
    {
        if (raw is null && value is null) return;
        fields[name] = raw?.Length == 0 ? new(name, null, source, "cleared") :
            value is > 0 and < 10000000 && double.IsFinite(value.Value)
                ? new(name, value.Value.ToString("G17", Invariant), source, "present") : new(name, null, source, "invalid");
    }

    private static void PutDate(Dictionary<string, GalleryFacet> fields, string? value, string? offset, string source)
    {
        if (value is null) return;
        if (value.Length == 0) { fields["captureDate"] = new("captureDate", null, source, "cleared"); return; }
        if (value.Length > 10 && value[4] == ':' && value[7] == ':')
            value = $"{value[..4]}-{value.Substring(5, 2)}-{value.Substring(8, 2)}{value[10..]}";
        value = value.Replace(' ', 'T');
        if (offset is not null && !value.EndsWith('Z') && !HasOffset(value)) value += offset;
        var basis = value.EndsWith('Z') || HasOffset(value) ? "explicit_offset" : "offset_unknown";
        if (basis == "offset_unknown" && DateTime.TryParse(value, Invariant, DateTimeStyles.None, out var local))
            fields["captureDate"] = new("captureDate", DateTime.SpecifyKind(local, DateTimeKind.Utc).ToString("O", Invariant), source, "present", basis);
        else if (basis == "explicit_offset" && DateTimeOffset.TryParse(value, Invariant, DateTimeStyles.None, out var parsed))
            fields["captureDate"] = new("captureDate", parsed.ToUniversalTime().ToString("O", Invariant), source, "present", basis);
        else fields["captureDate"] = new("captureDate", null, source, "invalid");
    }

    private static bool HasOffset(string value) => value.Length > 10 && (value.LastIndexOf('+') > 9 || value.LastIndexOf('-') > 9);

    private static void PutLocation(Dictionary<string, GalleryFacet> fields, double latitude, double longitude, string source)
    {
        fields["location"] = double.IsFinite(latitude) && double.IsFinite(longitude) && latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180
            ? new("location", $"{latitude.ToString("G17", Invariant)},{longitude.ToString("G17", Invariant)}", source, "present")
            : new("location", null, source, "invalid");
    }

    private static double? Rational(MetadataExtractor.Directory? directory, int tag) => directory?.TryGetRational(tag, out var value) == true ? value.ToDouble() : null;
    private static double? ParseNumber(string raw)
    {
        var parts = raw.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, Invariant, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, Invariant, out var denominator) && denominator != 0) return numerator / denominator;
        return double.TryParse(raw, NumberStyles.Float, Invariant, out var number) ? number : null;
    }
    private static bool TryCoordinate(string raw, bool latitude, out double value)
    {
        value = 0;
        var negative = raw.EndsWith(latitude ? 'S' : 'W');
        var positive = raw.EndsWith(latitude ? 'N' : 'E');
        var number = negative || positive ? raw[..^1] : raw;
        var parts = number.Split(',');
        if (parts.Length is < 1 or > 3 || !double.TryParse(parts[0], NumberStyles.Float, Invariant, out value)) return false;
        for (var i = 1; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, Invariant, out var fraction) || fraction is < 0 or >= 60) return false;
            value += fraction / (i == 1 ? 60 : 3600);
        }
        if (negative) value = -value;
        return double.IsFinite(value) && (latitude ? value is >= -90 and <= 90 : value is >= -180 and <= 180);
    }
    private static string? Join(string? first, string? second) => string.IsNullOrWhiteSpace(first) ? second :
        string.IsNullOrWhiteSpace(second) ? first : $"{first.Trim()} {second.Trim()}";
    private static string? HeaderValue(MetadataExtractor.Directory? directory, string name) =>
        directory?.Tags.FirstOrDefault(tag => tag.Name == name)?.Description;
    public static string Normalize(string value) => value.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    private sealed class LimitedReadStream(Stream inner, long maximum) : Stream
    {
        private long read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var result = inner.Read(buffer, offset, count);
            read += result;
            if (read > maximum) throw new InvalidDataException("metadata_input_too_large");
            return result;
        }
        public override int Read(Span<byte> buffer)
        {
            var result = inner.Read(buffer);
            read += result;
            if (read > maximum) throw new InvalidDataException("metadata_input_too_large");
            return result;
        }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
