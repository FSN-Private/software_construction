using System.Text;
using System.Xml;
using System.Xml.Linq;
using Platform.Api.Features.Gallery.Metadata;

namespace Platform.Api.Features.Lineage;

public sealed record XmpFieldDifference(string Field, string? Base, string? Target, string? Source,
    bool TargetChanged, bool SourceChanged, bool RequiresResolution);

public sealed record XmpMergeAssessment(string PolicyVersion, IReadOnlyList<XmpFieldDifference> Fields,
    bool CanCompose, string? UnavailableReason);

// This policy deliberately treats everything outside the seven gallery fields as opaque.
// A caller must show every RequiresResolution entry and pass the owner's chosen side to Compose.
public static class XmpMergePolicy
{
    public const string Version = "xmp-merge-v1";
    private static readonly XNamespace X = "adobe:ns:meta/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Exif = "http://ns.adobe.com/exif/1.0/";
    private static readonly XNamespace Darktable = "http://darktable.sf.net/";
    private static readonly IReadOnlyDictionary<string, XName[]> Fields = new Dictionary<string, XName[]>
    {
        ["rating"] = [Xmp + "Rating"],
        ["rejected"] = [Darktable + "rejected"],
        ["color"] = [Darktable + "colorlabels"],
        ["tags"] = [Dc + "subject"],
        ["title"] = [Dc + "title"],
        ["description"] = [Dc + "description"],
        ["location"] = [Exif + "GPSLatitude", Exif + "GPSLongitude"]
    };

    public static XmpMergeAssessment Assess(byte[]? ancestor, byte[] target, byte[] source)
    {
        if (ancestor is null) return new(Version, [], false, "no_common_ancestor");
        try
        {
            var baseline = Parse(ancestor);
            var left = Parse(target);
            var right = Parse(source);
            var differences = new List<XmpFieldDifference>();
            foreach (var (field, names) in Fields)
            {
                var original = Value(baseline.Description, names);
                var targetValue = Value(left.Description, names);
                var sourceValue = Value(right.Description, names);
                var targetChanged = targetValue != original;
                var sourceChanged = sourceValue != original;
                if (targetChanged || sourceChanged)
                    differences.Add(new(field, original, targetValue, sourceValue, targetChanged, sourceChanged,
                        targetChanged && sourceChanged));
            }
            // Rejection and rating share darktable's Rating representation. Never combine
            // edits to those two fields from opposite sides without an owner choice.
            var rating = differences.FindIndex(item => item.Field == "rating");
            var rejected = differences.FindIndex(item => item.Field == "rejected");
            if (rating >= 0 && rejected >= 0 &&
                differences[rating].TargetChanged != differences[rejected].TargetChanged)
            {
                differences[rating] = differences[rating] with { RequiresResolution = true };
                differences[rejected] = differences[rejected] with { RequiresResolution = true };
            }
            var opaqueBase = Opaque(baseline.Document);
            var opaqueTarget = Opaque(left.Document);
            var opaqueSource = Opaque(right.Document);
            if (opaqueTarget != opaqueBase || opaqueSource != opaqueBase)
                differences.Add(new("opaque", opaqueBase, opaqueTarget, opaqueSource,
                    opaqueTarget != opaqueBase, opaqueSource != opaqueBase, true));
            return new(Version, differences, true, null);
        }
        catch (Exception error) when (error is XmlException or InvalidDataException or ArgumentException)
        {
            return new(Version, [], false, "malformed_or_unsupported_xmp");
        }
    }

    public static XmpMergeAssessment Compare(byte[] target, byte[] source) => Assess(target, target, source);

    public static byte[] Compose(byte[]? ancestor, byte[] target, byte[] source,
        IReadOnlyDictionary<string, string> resolutions)
    {
        var assessment = Assess(ancestor, target, source);
        if (!assessment.CanCompose) throw new InvalidDataException(assessment.UnavailableReason);
        foreach (var conflict in assessment.Fields.Where(item => item.RequiresResolution))
            if (!resolutions.TryGetValue(conflict.Field, out var choice) || choice is not ("target" or "source"))
                throw new InvalidDataException("unresolved_divergence");
        var opaqueSide = resolutions.GetValueOrDefault("opaque") == "source" ? "source" : "target";
        var selected = Parse(opaqueSide == "source" ? source : target);
        var left = Parse(target);
        var right = Parse(source);
        foreach (var difference in assessment.Fields.Where(item => item.Field != "opaque"))
        {
            var choice = difference.RequiresResolution ? resolutions[difference.Field] :
                difference.SourceChanged ? "source" : "target";
            CopyField(selected.Description, choice == "source" ? right.Description : left.Description,
                Fields[difference.Field]);
        }
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false), Indent = false, CloseOutput = false
        })) selected.Document.Save(writer);
        if (output.Length > GalleryMetadataReader.MaximumXmpBytes)
            throw new InvalidDataException("xmp_too_large");
        return output.ToArray();
    }

    private static (XDocument Document, XElement Description) Parse(byte[] content)
    {
        if (content.Length > GalleryMetadataReader.MaximumXmpBytes)
            throw new InvalidDataException("xmp_too_large");
        using var stream = new MemoryStream(content, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = GalleryMetadataReader.MaximumXmpBytes,
            MaxCharactersFromEntities = 0
        });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var descriptions = document.Root?.Name == X + "xmpmeta"
            ? document.Root.Element(Rdf + "RDF")?.Elements(Rdf + "Description").ToArray() : null;
        if (descriptions is not { Length: 1 }) throw new InvalidDataException("unsupported_xmp_shape");
        foreach (var names in Fields.Values)
            foreach (var name in names)
                if (descriptions[0].Elements(name).Skip(1).Any() ||
                    descriptions[0].Attribute(name) is not null && descriptions[0].Element(name) is not null)
                    throw new InvalidDataException("duplicate_metadata_field");
        return (document, descriptions[0]);
    }

    private static string? Value(XElement description, XName[] names)
    {
        var values = names.Select(name => description.Attribute(name)?.Value ??
            description.Element(name)?.ToString(SaveOptions.DisableFormatting)).ToArray();
        return values.All(value => value is null) ? null : string.Join("\u001f", values.Select(value => value ?? ""));
    }

    private static string Opaque(XDocument document)
    {
        var copy = new XDocument(document);
        var description = copy.Root!.Element(Rdf + "RDF")!.Element(Rdf + "Description")!;
        foreach (var name in Fields.Values.SelectMany(names => names))
        {
            description.Attribute(name)?.Remove();
            description.Elements(name).Remove();
        }
        return copy.ToString(SaveOptions.DisableFormatting);
    }

    private static void CopyField(XElement destination, XElement source, XName[] names)
    {
        foreach (var name in names)
        {
            destination.Attribute(name)?.Remove();
            destination.Elements(name).Remove();
            if (source.Attribute(name) is { } attribute)
                destination.Add(new XAttribute(attribute));
            if (source.Element(name) is { } element)
                destination.Add(new XElement(element));
        }
    }
}
