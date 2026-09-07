namespace Platform.Ingest.Worker;
public sealed record PublicationIntent(Guid Id, string State, int RetryCount)
{
    public PublicationIntent Repair() => State == "pending-publication" ? this with { State = "reconciled", RetryCount = RetryCount + 1 } : this;
}
