using System.Text.Json;
using System.Text.Json.Serialization;
using Platform.Storage;

namespace Platform.Ingest.Worker;

public sealed class PublicationPromoter(S3ObjectStore storage)
{
    public async Task<ContentAddress> PromoteAsync(PublicationIntent intent, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<PublicationPayload>(intent.Payload) ?? throw new InvalidOperationException("invalid_publication_payload");
        var address = new ContentAddress(payload.Sha256, payload.Length);
        if (!address.IsValid || payload.SessionId == Guid.Empty) throw new InvalidOperationException("invalid_publication_payload");
        await storage.PromoteStagedAsync(payload.SessionId, address, cancellationToken);
        return address;
    }
}

public sealed record PublicationPayload([property: JsonPropertyName("sessionId")] Guid SessionId, [property: JsonPropertyName("sha256")] string Sha256, [property: JsonPropertyName("length")] long Length);
