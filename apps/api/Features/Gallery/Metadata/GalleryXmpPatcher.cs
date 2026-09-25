using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Platform.Api.Features.Gallery.Browse;

namespace Platform.Api.Features.Gallery.Metadata;

public static class GalleryXmpPatcher
{
    private static readonly XNamespace X = "adobe:ns:meta/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Exif = "http://ns.adobe.com/exif/1.0/";
    private static readonly XNamespace Darktable = "http://darktable.sf.net/";
    private static readonly HashSet<string> Allowed = ["rating", "rejected", "color", "tags", "title", "description", "location"];

    public static byte[] Patch(ReadOnlySpan<byte> input, IReadOnlyDictionary<string, JsonElement> changes)
    {
        if (changes.Count is < 1 or > 7 || changes.Keys.Any(key => !Allowed.Contains(key)) || input.Length > GalleryMetadataReader.MaximumXmpBytes)
            throw new GalleryProblem("unsupported_metadata_field");
        XDocument document;
        if (input.IsEmpty)
            document = new XDocument(new XElement(X + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", X),
                new XElement(Rdf + "RDF", new XElement(Rdf + "Description"))));
        else
        {
            try
            {
                using var stream = new MemoryStream(input.ToArray(), writable: false);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    MaxCharactersInDocument = GalleryMetadataReader.MaximumXmpBytes, MaxCharactersFromEntities = 0 });
                document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException) { throw new GalleryProblem("malformed_xmp", 422); }
        }
        if (document.Root is null || document.Root.Name != X + "xmpmeta") throw new GalleryProblem("malformed_xmp", 422);
        var rdf = document.Root.Element(Rdf + "RDF");
        if (rdf is null) { rdf = new XElement(Rdf + "RDF"); document.Root.Add(rdf); }
        var description = rdf.Elements(Rdf + "Description").FirstOrDefault();
        if (description is null) { description = new XElement(Rdf + "Description"); rdf.Add(description); }
        foreach (var change in changes)
        {
            var value = change.Value;
            switch (change.Key)
            {
                case "rating":
                    var rating = value.ValueKind == JsonValueKind.Null ? "" :
                        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var stars) && stars is >= 0 and <= 5
                            ? stars.ToString(CultureInfo.InvariantCulture) : throw new GalleryProblem("invalid_metadata_value");
                    description.SetAttributeValue(Xmp + "Rating", rating);
                    break;
                case "rejected":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new GalleryProblem("invalid_metadata_value");
                    description.SetAttributeValue(Darktable + "rejected", value.GetBoolean() ? "true" : "false");
                    if (value.GetBoolean()) description.SetAttributeValue(Xmp + "Rating", "-1");
                    else if ((string?)description.Attribute(Xmp + "Rating") == "-1") description.SetAttributeValue(Xmp + "Rating", "0");
                    break;
                case "color":
                    if (value.ValueKind == JsonValueKind.Null) SetList(description, Darktable + "colorlabels", []);
                    else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var color) && color is >= 0 and <= 4)
                        SetList(description, Darktable + "colorlabels", [color.ToString(CultureInfo.InvariantCulture)]);
                    else throw new GalleryProblem("invalid_metadata_value");
                    break;
                case "tags":
                    if (value.ValueKind is not JsonValueKind.Array || value.GetArrayLength() > 128) throw new GalleryProblem("invalid_metadata_value");
                    var tags = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null).ToArray();
                    if (tags.Any(tag => tag is null or { Length: 0 or > 256 })) throw new GalleryProblem("invalid_metadata_value");
                    SetList(description, Dc + "subject", tags!);
                    break;
                case "title":
                case "description":
                    var text = value.ValueKind == JsonValueKind.Null ? "" : value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new GalleryProblem("invalid_metadata_value");
                    if (text.Length > 4096) throw new GalleryProblem("invalid_metadata_value");
                    SetAlt(description, change.Key == "title" ? Dc + "title" : Dc + "description", text.Normalize(NormalizationForm.FormKC));
                    break;
                case "location":
                    if (value.ValueKind == JsonValueKind.Null)
                    {
                        description.SetAttributeValue(Exif + "GPSLatitude", "");
                        description.SetAttributeValue(Exif + "GPSLongitude", "");
                        break;
                    }
                    if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("latitude", out var latitude) || !value.TryGetProperty("longitude", out var longitude) ||
                        !latitude.TryGetDouble(out var lat) || !longitude.TryGetDouble(out var lon) || !double.IsFinite(lat) || !double.IsFinite(lon) || lat is < -90 or > 90 || lon is < -180 or > 180)
                        throw new GalleryProblem("invalid_metadata_value");
                    description.SetAttributeValue(Exif + "GPSLatitude", Coordinate(lat, true));
                    description.SetAttributeValue(Exif + "GPSLongitude", Coordinate(lon, false));
                    break;
            }
        }
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, CloseOutput = false }))
            document.Save(writer);
        if (output.Length > GalleryMetadataReader.MaximumXmpBytes) throw new GalleryProblem("xmp_too_large", 422);
        return output.ToArray();
    }

    private static void SetList(XElement description, XName name, IReadOnlyList<string> values)
    {
        var element = description.Element(name);
        if (element is null) { element = new XElement(name); description.Add(element); }
        element.ReplaceNodes(new XElement(Rdf + "Seq", values.Select(value => new XElement(Rdf + "li", value))));
    }

    private static void SetAlt(XElement description, XName name, string value)
    {
        var element = description.Element(name);
        if (element is null) { element = new XElement(name); description.Add(element); }
        element.ReplaceNodes(new XElement(Rdf + "Alt", new XElement(Rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default"), value)));
    }

    private static string Coordinate(double value, bool latitude)
    {
        var absolute = Math.Abs(value);
        var degrees = Math.Floor(absolute);
        var minutes = (absolute - degrees) * 60;
        return $"{degrees.ToString(CultureInfo.InvariantCulture)},{minutes.ToString("0.#######", CultureInfo.InvariantCulture)}{(latitude ? value < 0 ? "S" : "N" : value < 0 ? "W" : "E")}";
    }
}
