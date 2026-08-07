using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

#nullable enable

namespace MihuBot.Molly;

/// <summary>
/// A single Molly device registration. Everything sensitive is stored with authenticated encryption
/// under a key derived from the entry <see cref="Id"/> and the server key, so the database on its own
/// is useless and rows can't be tampered with or swapped between entries.
/// </summary>
[Table("mollyEntries")]
[Index(nameof(HashPrefix))]
public sealed class MollyDbEntry
{
    public Guid Id { get; set; }

    /// <summary>
    /// First byte of <see cref="DerivedHash"/>. Indexed so that a login only has to fetch
    /// a small slice of the table before doing the constant-time scan.
    /// </summary>
    public int HashPrefix { get; set; }

    /// <summary>HMAC-SHA512 of the client-provided key hash, keyed with the server key.</summary>
    public byte[] DerivedHash { get; set; } = [];

    /// <summary>Secret handed back to the client on login, stored as <c>nonce || ciphertext || tag</c>.</summary>
    public byte[]? EncryptedServerHmac { get; set; }

    /// <summary>The associated nickname, stored as <c>nonce || ciphertext || tag</c>.</summary>
    public byte[]? EncryptedNickname { get; set; }

    public DateOnly CreatedDay { get; set; }

    public DateOnly LastSeenDay { get; set; }

    public bool LockRequested { get; set; }

    public bool WipeRequested { get; set; }

    /// <summary>Suppresses the Discord notification for this device's alerts. They're still stored.</summary>
    public bool AlertsMuted { get; set; }
}
