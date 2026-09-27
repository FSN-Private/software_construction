using Amazon.S3;
using Npgsql;
using Platform.Api.Features.Gallery.Metadata;
using Platform.Contracts.VersionControl;
using Platform.Storage;
using static Platform.Storage.IngestDb;

namespace Platform.Api.Features.Lineage;

internal static class VerifiedXmpReader
{
    public static async Task<byte[]> ReadAsync(NpgsqlConnection connection, S3ObjectStore storage, string digest, CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT byte_length,storage_key,storage_version FROM content_objects
            WHERE sha256=$1 AND category='xmp' AND integrity_state='authoritative' AND storage_key<>'' AND storage_version IS NOT NULL
            """, digest);
        long length;
        string key;
        string version;
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new VersionControlProblem("content_not_authoritative");
            length = reader.GetInt64(0);
            key = reader.GetString(1);
            version = reader.GetString(2);
        }
        if (length > GalleryMetadataReader.MaximumXmpBytes) throw new VersionControlProblem("content_not_authoritative");
        try
        {
            await using var stream = await storage.ReadVerifiedAsync(key, version, new(digest, length), ct);
            var bytes = new byte[checked((int)length)];
            await stream.ReadExactlyAsync(bytes, ct);
            return bytes;
        }
        catch (Exception error) when (error is AmazonS3Exception or InvalidDataException or ArgumentException)
        { throw new VersionControlProblem("content_not_authoritative"); }
    }
}
