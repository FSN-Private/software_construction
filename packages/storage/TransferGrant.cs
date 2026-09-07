namespace Platform.Storage;

public sealed record TransferGrant(Guid SessionId, string OwnerId, ContentAddress ExpectedContent, DateTime ExpiresAtUtc)
{
    public void Validate(DateTime nowUtc)
    {
        if (SessionId == Guid.Empty || string.IsNullOrWhiteSpace(OwnerId) || !ExpectedContent.IsValid || ExpiresAtUtc <= nowUtc || ExpiresAtUtc > nowUtc.AddMinutes(15)) throw new ArgumentException("invalid_transfer_grant");
    }

    public string ExpectedChecksumBase64 => Convert.ToBase64String(Convert.FromHexString(ExpectedContent.Sha256));
}

public sealed record PresignedTransfer(string Url, IReadOnlyDictionary<string, string> RequiredHeaders);
