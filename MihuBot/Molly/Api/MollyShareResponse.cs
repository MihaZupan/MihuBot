using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot.Molly.Api;

/// <summary>
/// The <c>data</c> of a file sharing response with status <c>ok</c>. Only the fields of the action are set.
/// Byte fields are standard padded base64 and times are Unix seconds.
/// </summary>
public sealed class MollyShareResponse
{
    /// <summary><c>ok</c>, <c>not-found</c>, <c>rejected</c>, <c>limit</c> or <c>unbound</c>.</summary>
    [JsonPropertyName("result")]
    public required string Result { get; init; }

    /// <summary><c>share-reserve</c> only: the reserved ENTRY.</summary>
    [JsonPropertyName("entryId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntryId { get; init; }

    [JsonPropertyName("owned")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MollyShareOwnedItem[]? Owned { get; init; }

    [JsonPropertyName("received")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MollyShareReceivedItem[]? Received { get; init; }

    [JsonPropertyName("trusteeRequests")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MollyShareTrusteeRequestItem[]? TrusteeRequests { get; init; }

    /// <summary><c>share-sync</c> only: the caller's update hint (Unix milliseconds) as of the listed snapshot, 0 if none.</summary>
    [JsonPropertyName("updatedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? UpdatedAt { get; init; }

    [JsonPropertyName("descriptor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Descriptor { get; init; }

    [JsonPropertyName("signature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Signature { get; init; }

    [JsonPropertyName("rosterSealed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RosterSealed { get; init; }

    [JsonPropertyName("metadataSealed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MetadataSealed { get; init; }

    [JsonPropertyName("started")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Started { get; init; }

    [JsonPropertyName("dataKeySealed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DataKeySealed { get; init; }

    [JsonPropertyName("encryptedFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptedFile { get; init; }
}

public sealed class MollyShareOwnedItem
{
    [JsonPropertyName("entryId")]
    public required string EntryId { get; init; }

    /// <summary>The current descriptor's revision.</summary>
    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("recipientAci")]
    public required string RecipientAci { get; init; }

    [JsonPropertyName("recipientIdentityKey")]
    public required string RecipientIdentityKey { get; init; }

    /// <summary><c>active</c>, <c>started</c> or <c>released</c>.</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; init; }

    [JsonPropertyName("startedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? StartedAt { get; init; }

    [JsonPropertyName("releasedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ReleasedAt { get; init; }

    [JsonPropertyName("approvals")]
    public required MollyShareApprovalItem[] Approvals { get; init; }
}

/// <summary>A trustee approval, relayed to A exactly as the trustee submitted it.</summary>
public sealed class MollyShareApprovalItem
{
    [JsonPropertyName("aci")]
    public required string Aci { get; init; }

    [JsonPropertyName("identityKey")]
    public required string IdentityKey { get; init; }

    /// <summary>When S recorded the approval. Informational, not signed.</summary>
    [JsonPropertyName("approvedAt")]
    public long ApprovedAt { get; init; }

    [JsonPropertyName("signature")]
    public required string Signature { get; init; }
}

/// <summary>What B learns about an entry: deliberately no counts, threshold or trustee data.</summary>
public sealed class MollyShareReceivedItem
{
    [JsonPropertyName("entryId")]
    public required string EntryId { get; init; }

    [JsonPropertyName("ownerAci")]
    public required string OwnerAci { get; init; }

    [JsonPropertyName("ownerIdentityKey")]
    public required string OwnerIdentityKey { get; init; }

    /// <summary><c>available</c>, <c>started</c> or <c>released</c>.</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("createdAt")]
    public long CreatedAt { get; init; }
}

public sealed class MollyShareTrusteeRequestItem
{
    [JsonPropertyName("entryId")]
    public required string EntryId { get; init; }

    [JsonPropertyName("ownerAci")]
    public required string OwnerAci { get; init; }

    [JsonPropertyName("ownerIdentityKey")]
    public required string OwnerIdentityKey { get; init; }

    [JsonPropertyName("startedAt")]
    public long StartedAt { get; init; }

    /// <summary>KEY_SHARE_T_SEALED.</summary>
    [JsonPropertyName("sealed")]
    public required string Sealed { get; init; }

    /// <summary>A's <c>trustee-sealed</c> signature.</summary>
    [JsonPropertyName("sealedSignature")]
    public required string SealedSignature { get; init; }
}
