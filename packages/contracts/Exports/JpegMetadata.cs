namespace Platform.Contracts.Exports;
public static class JpegMetadata
{
    public static byte[] Transform(ReadOnlySpan<byte> input, bool retain)
    {
        if (input.Length < 4 || input[0] != 0xff || input[1] != 0xd8) throw new ExportProblem("output_invalid");
        using var output = new MemoryStream(); output.Write(input[..2]); var offset = 2;
        while (offset < input.Length)
        {
            var start = offset;
            if (input[offset++] != 0xff) throw new ExportProblem("output_invalid");
            while (offset < input.Length && input[offset] == 0xff) offset++;
            if (offset >= input.Length) throw new ExportProblem("output_invalid");
            var marker = input[offset++];
            if (marker == 0xd9) { if (offset != input.Length) throw new ExportProblem("output_invalid"); output.Write(input[start..offset]); return output.ToArray(); }
            if (marker is 0x00 or 0xd8 || marker is >= 0xd0 and <= 0xd7 || offset + 2 > input.Length) throw new ExportProblem("output_invalid");
            var length = input[offset] * 256 + input[offset + 1]; if (length < 2 || offset + length > input.Length) throw new ExportProblem("output_invalid"); offset += length;
            var payload = input.Slice(offset - length + 2, length - 2);
            var safeApp = marker == 0xe0 && payload.StartsWith("JFIF\0"u8) || marker == 0xe2 && payload.StartsWith("ICC_PROFILE\0"u8);
            if (retain || (marker is < 0xe0 or > 0xef && marker != 0xfe) || safeApp) output.Write(input[start..offset]);
            if (marker != 0xda) continue;
            var scanStart = offset;
            while (offset < input.Length)
            {
                if (input[offset] != 0xff) { offset++; continue; }
                var next = offset + 1; while (next < input.Length && input[next] == 0xff) next++;
                if (next >= input.Length) throw new ExportProblem("output_invalid");
                if (input[next] == 0 || input[next] is >= 0xd0 and <= 0xd7) { offset = next + 1; continue; }
                break;
            }
            output.Write(input[scanStart..offset]);
        }
        throw new ExportProblem("output_invalid");
    }
}
