#nullable enable

namespace MihuBot.Molly;

/// <summary>
/// The transport header and envelope of the exchange a request arrived in, as authenticated by
/// <see cref="MollyRequestProtector"/>. Proofs are bound to it, so they can't be replayed
/// inside a different exchange (one whose response an attacker can decrypt).
/// </summary>
/// <param name="RecipientKeyId">The unmasked recipient key ID.</param>
/// <param name="ClientEphemeralPublicKey">The client's raw 32-byte X25519 ephemeral public key.</param>
/// <param name="RequestNonce">The decoded 16-byte request nonce from the JSON envelope, which the transport only accepts once.</param>
/// <param name="RequestTimestamp">
/// The JSON envelope's <c>timestamp</c> (Unix seconds), which the transport only accepts while fresh. Proofs bind it along with
/// <see cref="RequestNonce"/>, so a nonce only remembered for a limited time can't be reused with an old proof later.
/// </param>
/// <param name="RecipientKeyAge">
/// How long ago the server generated the transport key the request was sealed to, when it was decrypted.
/// Transport keys are unpredictable before they're published, so this bounds how old the attestation can be.
/// </param>
public sealed record MollyExchangeContext(MollyPublicKeyId RecipientKeyId, byte[] ClientEphemeralPublicKey, byte[] RequestNonce, long RequestTimestamp, TimeSpan RecipientKeyAge)
{
    /// <summary>The envelope's <c>action</c>, which session proofs are bound to.</summary>
    public string Action { get; init; } = "";

    /// <summary>
    /// The envelope's <c>proof</c>: that the request comes from the unlocked app holding the session it names
    /// (<see cref="MollySessionProof"/>). Only checked for the actions that require it.
    /// </summary>
    public string? SessionProof { get; init; }

    /// <summary>
    /// The rotating (never discovery) key the request was decrypted with, so later steps of the exchange
    /// don't depend on it still being retained. Null for discovery exchanges and contexts built elsewhere.
    /// </summary>
    internal MollyTransportKeyRing.TransportKey? TransportKey { get; init; }
}
