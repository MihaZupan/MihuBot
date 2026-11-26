#nullable enable

namespace MihuBot.Molly.Provisioning;

/// <summary>An issued registration token (Provisioning/README.md) and when it stops being redeemable.</summary>
public sealed record MollyRegistrationToken(string Token, DateTime ExpiresAt);
