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
[Index(nameof(DerivedHash), IsUnique = true)]
public sealed class MollyDbEntry
{
    public Guid Id { get; set; }

    /// <summary>HMAC-SHA512 of the client-provided key hash, keyed with the server key.</summary>
    public byte[] DerivedHash { get; set; } = [];

    /// <summary>Secret handed back to the client on login, stored as <c>nonce || ciphertext || tag</c>.</summary>
    public byte[]? EncryptedServerHmac { get; set; }

    /// <summary>
    /// The pinned Android attestation state (<see cref="Attestation.MollyAttestationState"/>), stored as
    /// <c>nonce || ciphertext || tag</c>. Null only for entries that haven't enrolled yet.
    /// </summary>
    public byte[]? EncryptedAttestationState { get; set; }

    /// <summary>The associated nickname, stored as <c>nonce || ciphertext || tag</c>.</summary>
    public byte[]? EncryptedNickname { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    /// <summary>
    /// When the last successful login issued a token, so every later login issues a strictly newer one.
    /// <see cref="DateTime.MinValue"/> for entries that haven't logged in since this was introduced.
    /// </summary>
    public DateTime LastLoginAt { get; set; }

    /// <summary>
    /// When the newest token the device has actually used was issued. Tokens issued before it are
    /// rejected. A login alone doesn't supersede the device's current token: its response may never
    /// arrive, and the device would then lose its session. Only ever moves forward.
    /// <see cref="DateTime.MinValue"/> until a token issued since this was introduced is used.
    /// </summary>
    public DateTime ActiveSessionIssuedAt { get; set; }

    /// <summary>Last reported battery and location availability, encrypted together.</summary>
    public byte[]? EncryptedDeviceStatus { get; set; }

    public bool LockRequested { get; set; }

    public bool WipeRequested { get; set; }

    /// <summary>Suppresses the Discord notification for this device's alerts. They're still stored.</summary>
    public bool AlertsMuted { get; set; }
}
