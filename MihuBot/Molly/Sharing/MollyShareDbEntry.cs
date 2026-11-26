using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

#nullable enable

namespace MihuBot.Molly.Sharing;

public enum MollyShareState
{
    /// <summary>An ENTRY handed out by <c>share-reserve</c> that no <c>share-create</c> has used yet. Carries no data.</summary>
    Reserved = 0,
    Active = 1,
    Started = 2,
    Released = 3,
}

/// <summary>
/// A protected file share (Sharing/README.md). Parties are stored as keyed party hashes and every other field
/// is encrypted under the entry's at-rest key (<see cref="MollyShareCrypto.EncryptAtRest"/>). The large
/// objects live in their own columns, so listing entries never has to load or decrypt them.
/// </summary>
[Table("mollyShareEntries")]
[Index(nameof(OwnerEntryId))]
[Index(nameof(RecipientKeyHash))]
public sealed class MollyShareDbEntry
{
    /// <summary>The ENTRY, chosen by S and read as a big-endian GUID.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The Molly entry (device) that reserved and created the grant and alone manages it, whatever its current identity. No
    /// foreign key: grants of a deleted or wiped entry are deleted by maintenance, raising B's and the trustees' hints.
    /// </summary>
    public Guid OwnerEntryId { get; set; }

    /// <summary>B's party hash. Null while reserved, and once B deleted its copy of a released entry (<c>share-dismiss</c>).</summary>
    public byte[]? RecipientKeyHash { get; set; }

    public MollyShareState State { get; set; }

    /// <summary>When the ENTRY was reserved, then when the grant was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>The attempt's start, in whole seconds - approvals sign it.</summary>
    public DateTime? StartedAt { get; set; }

    public DateTime? ReleasedAt { get; set; }

    /// <summary>M, once started.</summary>
    public int? Threshold { get; set; }

    /// <summary>A keyed digest of the last accepted create or update body, so only an identical retry is accepted.</summary>
    public byte[]? RequestDigest { get; set; }

    /// <summary>GRANT_DESCRIPTOR || grant signature, of the current revision.</summary>
    public byte[]? EncryptedHeader { get; set; }

    /// <summary>ROSTER_SEALED || FILE_METADATA_SEALED || DATA_KEY_SEALED_SHAREDENC.</summary>
    public byte[]? EncryptedObjects { get; set; }

    /// <summary>ENCRYPTED_FILE.</summary>
    public byte[]? EncryptedFile { get; set; }

    /// <summary>SKEY of the current revision, erased once released.</summary>
    public byte[]? EncryptedSkey { get; set; }

    /// <summary>DATA_KEY_SEALED, set when released.</summary>
    public byte[]? EncryptedDataKeySealed { get; set; }
}

/// <summary>A used roster slot of a started entry. Deleted along with the entry.</summary>
[Table("mollyShareTrustees")]
[PrimaryKey(nameof(EntryId), nameof(Slot))]
[Index(nameof(TrusteeKeyHash))]
public sealed class MollyShareTrusteeDbEntry
{
    public Guid EntryId { get; set; }

    /// <summary>Required, so the slots are cascade deleted with the entry.</summary>
    public MollyShareDbEntry Entry { get; set; } = null!;

    /// <summary>0-based slot in the manifest. The trustee's Shamir index is always <c>Slot + 1</c>.</summary>
    public int Slot { get; set; }

    public byte[] TrusteeKeyHash { get; set; } = [];

    public DateTime? ApprovedAt { get; set; }

    /// <summary>T ACI || T KEY || KEY_SHARE_T_SEALED || trustee-sealed signature.</summary>
    public byte[] EncryptedSlot { get; set; } = [];

    /// <summary>The trustee's 32-byte share, erased once released.</summary>
    public byte[]? EncryptedShare { get; set; }

    /// <summary>SHA-512(SHARE_T_SENC) || approval signature, kept for A.</summary>
    public byte[]? EncryptedApproval { get; set; }
}

/// <summary>
/// Per-identity update hint (Sharing/README.md, "Update hints"), carried by pings of devices whose entry has the
/// identity as its current one. Identities without a row have never been affected by a change.
/// </summary>
[Table("mollyShareUpdates")]
public sealed class MollyShareUpdateDbEntry
{
    [Key]
    public byte[] PartyHash { get; set; } = [];

    /// <summary>Unix milliseconds, raised to <c>max(previous + 1, now)</c> by every committed change affecting the identity.</summary>
    public long UpdatedAt { get; set; }
}
