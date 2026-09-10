using System.Buffers.Binary;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Platform.Ingest.Worker.Verification;

public static partial class ContentInspector
{
    public static string Inspect(Stream stream, string path)
    {
        stream.Position = 0;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (extension == ".xmp")
            {
                if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException("malformed_xmp");
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024, CloseInput = false });
                var xml = XDocument.Load(reader);
                if (xml.Root is null || !xml.Descendants().Any(e => e.Name.NamespaceName == "http://darktable.sf.net/" || e.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name.NamespaceName == "http://darktable.sf.net/"))) throw new InvalidDataException("unsupported_format");
                return "xmp";
            }
            using var binary = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            if (extension == ".png") { CheckPng(binary); return "png"; }
            if (extension is ".jpg" or ".jpeg") { CheckJpeg(binary); return "jpeg"; }
            if (extension is ".tif" or ".tiff" or ".dng")
            {
                var dng = CheckTiff(binary);
                if (extension == ".dng" && !dng) throw new InvalidDataException("unsupported_format");
                return extension == ".dng" ? "dng" : "tiff";
            }
            throw new InvalidDataException("unsupported_format");
        }
        catch (XmlException) { throw new InvalidDataException("malformed_xmp"); }
        catch (EndOfStreamException) { throw new InvalidDataException("unsupported_format"); }
        finally { stream.Position = 0; }
    }

    public static IReadOnlyList<(string Path, int Ordinal)> SourceCandidates(string path)
    {
        if (!path.EndsWith(".xmp", StringComparison.OrdinalIgnoreCase)) return [];
        var source = path[..^4];
        var candidates = new List<(string, int)> { (source, 0) };
        var directory = source.Contains('/') ? source[..(source.LastIndexOf('/') + 1)] : "";
        var match = DuplicateName().Match(source[directory.Length..]);
        if (match.Success && int.TryParse(match.Groups[2].Value, out var ordinal) && ordinal > 0)
            candidates.Add((directory + match.Groups[1].Value + match.Groups[3].Value, ordinal));
        return candidates;
    }

    [GeneratedRegex(@"^(.+)_([0-9]{2,})(\.[^.]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DuplicateName();

    private static void CheckPng(BinaryReader r)
    {
        if (!Exact(r, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException("unsupported_format");
        var first = true; var data = false;
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(Exact(r, 4));
            if (length > r.BaseStream.Length - r.BaseStream.Position - 8) throw new InvalidDataException("unsupported_format");
            var type = Exact(r, 4); var name = System.Text.Encoding.ASCII.GetString(type);
            if (first && (name != "IHDR" || length != 13)) throw new InvalidDataException("unsupported_format");
            uint crc = 0xffffffff;
            foreach (var b in type) crc = Crc(crc, b);
            for (uint i = 0; i < length; i++) crc = Crc(crc, r.ReadByte());
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(Exact(r, 4))) throw new InvalidDataException("unsupported_format");
            first = false; data |= name == "IDAT";
            if (name == "IEND")
            {
                if (!data || length != 0 || r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("unsupported_format");
                return;
            }
        }
        throw new InvalidDataException("unsupported_format");
    }

    private static uint Crc(uint crc, byte b)
    {
        crc ^= b;
        for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        return crc;
    }

    private static void CheckJpeg(BinaryReader r)
    {
        if (r.ReadByte() != 0xff || r.ReadByte() != 0xd8) throw new InvalidDataException("unsupported_format");
        var frame = false; var scan = false;
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            if (r.ReadByte() != 0xff) { if (scan) continue; throw new InvalidDataException("unsupported_format"); }
            byte marker; do { marker = r.ReadByte(); } while (marker == 0xff);
            if (scan && (marker == 0 || marker is >= 0xd0 and <= 0xd7)) continue;
            if (marker == 0xd9 && frame && scan) return;
            var length = BinaryPrimitives.ReadUInt16BigEndian(Exact(r, 2));
            if (length < 2 || r.BaseStream.Position + length - 2 > r.BaseStream.Length) throw new InvalidDataException("unsupported_format");
            frame |= marker is 0xc0 or 0xc1 or 0xc2;
            scan |= marker == 0xda;
            r.BaseStream.Position += length - 2;
        }
        throw new InvalidDataException("unsupported_format");
    }

    private static byte[] Exact(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        return bytes.Length == count ? bytes : throw new EndOfStreamException();
    }

    private static bool CheckTiff(BinaryReader r)
    {
        var signature = r.ReadBytes(2);
        var little = signature.SequenceEqual("II"u8.ToArray());
        if (!little && !signature.SequenceEqual("MM"u8.ToArray())) throw new InvalidDataException("unsupported_format");
        byte[] Bytes(int count) { var bytes = r.ReadBytes(count); return bytes.Length == count ? bytes : throw new EndOfStreamException(); }
        ushort FromU16(ReadOnlySpan<byte> bytes) => little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
        uint FromU32(ReadOnlySpan<byte> bytes) => little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
        ushort U16() => FromU16(Bytes(2));
        uint U32() => FromU32(Bytes(4));
        ulong[] Values(ushort type, uint count, uint offset, byte[] inline)
        {
            var width = type switch { 1 => 1, 3 => 2, 4 or 13 => 4, _ => throw new InvalidDataException("unsupported_format") };
            if (count > 1_000_000) throw new InvalidDataException("unsupported_format");
            var length = checked((int)(count * (uint)width));
            byte[] data;
            if (length <= 4) data = inline;
            else
            {
                var position = r.BaseStream.Position;
                r.BaseStream.Position = offset;
                data = Bytes(length);
                r.BaseStream.Position = position;
            }
            var values = new ulong[count];
            for (var i = 0; i < count; i++) values[i] = type switch
            {
                1 => data[i],
                3 => FromU16(data.AsSpan(i * 2, 2)),
                _ => FromU32(data.AsSpan(i * 4, 4))
            };
            return values;
        }
        if (U16() != 42) throw new InvalidDataException("unsupported_format");
        var pending = new Queue<uint>(); pending.Enqueue(U32());
        var visited = new HashSet<uint>(); var dng = false; var payload = false;
        while (pending.TryDequeue(out var offset))
        {
            if (offset == 0) continue;
            if (!visited.Add(offset) || visited.Count > 256 || offset > r.BaseStream.Length - 2) throw new InvalidDataException("unsupported_format");
            r.BaseStream.Position = offset; var count = U16();
            if (count == 0 || r.BaseStream.Position + count * 12L + 4 > r.BaseStream.Length) throw new InvalidDataException("unsupported_format");
            var values = new Dictionary<ushort, ulong[]>();
            for (var i = 0; i < count; i++)
            {
                var tag = U16(); var type = U16(); var n = U32(); var inline = Bytes(4); var value = FromU32(inline);
                var size = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 or 13 => 4, 5 or 10 or 12 => 8, _ => 0 };
                if (size == 0 || n == 0 || ((long)n * size > 4 && value + (long)n * size > r.BaseStream.Length)) throw new InvalidDataException("unsupported_format");
                dng |= tag == 50706 && type == 1 && n == 4;
                if (tag is 273 or 279 or 324 or 325 or 330 or 513 or 514 or 34665 or 34853 or 40965)
                    values[tag] = Values(type, n, value, inline);
            }
            pending.Enqueue(U32());
            foreach (var tag in new ushort[] { 330, 34665, 34853, 40965 })
                if (values.TryGetValue(tag, out var offsets)) foreach (var child in offsets) pending.Enqueue(checked((uint)child));
            ValidatePayload(273, 279); ValidatePayload(324, 325); ValidatePayload(513, 514);

            void ValidatePayload(ushort offsetsTag, ushort lengthsTag)
            {
                var hasOffsets = values.TryGetValue(offsetsTag, out var offsets);
                var hasLengths = values.TryGetValue(lengthsTag, out var lengths);
                if (!hasOffsets && !hasLengths) return;
                if (!hasOffsets || !hasLengths || offsets!.Length != lengths!.Length) throw new InvalidDataException("unsupported_format");
                for (var i = 0; i < offsets.Length; i++)
                    if (lengths[i] == 0 || offsets[i] > (ulong)r.BaseStream.Length || lengths[i] > (ulong)r.BaseStream.Length - offsets[i]) throw new InvalidDataException("unsupported_format");
                payload = true;
            }
        }
        if (visited.Count == 0 || !payload) throw new InvalidDataException("unsupported_format");
        return dng;
    }
}
