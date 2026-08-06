#nullable enable

namespace MihuBot.Molly;

/// <summary>An entry as shown on the admin dashboard. The nickname is decrypted, nothing else is exposed.</summary>
public sealed record MollyUserInfo(Guid Id, string Nickname, DateOnly CreatedDay, DateOnly LastSeenDay, bool LockRequested, bool WipeRequested, bool AlertsMuted);
