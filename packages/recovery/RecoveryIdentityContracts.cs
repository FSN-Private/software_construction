namespace Platform.Recovery;

/// <summary>The exact mapping bytes independently approved by the recovery MFA
/// key and verified by the recovery API. Moving this shared type preserves its
/// canonical JSON contract; the API never accepts a caller's mapped owner claim.</summary>
public sealed record RecoveryIdentityApprovalV1(
    int SchemaVersion,
    Guid MappingId,
    string Issuer,
    string Audience,
    string Subject,
    string PreservedOwnerId,
    string ApprovedBy,
    string ApprovalKeyId,
    DateTimeOffset ApprovedAtUtc);

public sealed record RecoveryIdentityApprovalArtifactV1(
    RecoveryIdentityApprovalV1 Payload,
    string SignatureBase64);
