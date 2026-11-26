#nullable enable

namespace MihuBot.Molly;

/// <summary>An issued registration token (Provisioning.md) and when it stops being redeemable.</summary>
public sealed record MollyRegistrationToken(string Token, DateTime ExpiresAt);
