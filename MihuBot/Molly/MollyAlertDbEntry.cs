using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

#nullable enable

namespace MihuBot.Molly;

/// <summary>
/// An arbitrary payload uploaded by a device, e.g. its location. Encrypted under the same per-entry
/// key as the rest of the entry, and deleted along with it.
/// </summary>
[Table("mollyAlerts")]
[Index(nameof(EntryId))]
public sealed class MollyAlertDbEntry
{
    public long Id { get; set; }

    public Guid EntryId { get; set; }

    /// <summary>Required, so the alerts are cascade deleted when the entry goes away.</summary>
    public MollyDbEntry Entry { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    /// <summary>The raw JSON the device sent, stored as <c>nonce || ciphertext || tag</c>.</summary>
    public byte[] EncryptedPayload { get; set; } = [];
}
