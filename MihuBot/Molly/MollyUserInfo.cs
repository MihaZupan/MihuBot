using MihuBot.Molly.Attestation;

#nullable enable

namespace MihuBot.Molly;

/// <summary>An entry as shown on the admin dashboard. The nickname and pinned attestation state are decrypted.</summary>
/// <param name="LastLoginAt">The last successful (attested) login.</param>
/// <param name="Attestation">Null for a wiped entry, or one whose state is unreadable.</param>
public sealed record MollyUserInfo(Guid Id, string Nickname, DateTime CreatedAt, DateTime LastSeenAt, DateTime LastLoginAt, bool LockRequested, bool WipeRequested, bool AlertsMuted, int? BatteryLevel, bool? LocationEnabled, string? AppVersion, MollyAttestationState? Attestation);