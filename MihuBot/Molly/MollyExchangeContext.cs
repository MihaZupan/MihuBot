#nullable enable

namespace MihuBot.Molly;

/// <summary>
/// The transport header of the exchange a request arrived in, as authenticated by
/// <see cref="MollyRequestProtector"/>. Attestation proofs are bound to it, so they can't be replayed
/// inside a different exchange (one whose response an attacker can decrypt).
/// </summary>
/// <param name="RecipientKeyId">The unmasked 16-byte recipient key ID.</param>
/// <param name="ClientEphemeralPublicKey">The client's raw 32-byte X25519 ephemeral public key.</param>
/// <param name="RequestNonce">The decoded 16-byte request nonce from the JSON envelope, which the transport only accepts once.</param>
/// <param name="RecipientKeyAge">
/// How long ago the server generated the transport key the request was sealed to, when it was decrypted.
/// Transport keys are unpredictable before they're published, so this bounds how old the attestation can be.
/// </param>
public sealed record MollyExchangeContext(byte[] RecipientKeyId, byte[] ClientEphemeralPublicKey, byte[] RequestNonce, TimeSpan RecipientKeyAge);
