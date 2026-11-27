using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MihuBot.Molly;
using MihuBot.Molly.Api;
using MihuBot.Molly.Sharing;

namespace MihuBot.Tests.Molly;

/// <summary>A Signal identity for tests: a random ACI and an X25519 identity key pair (<c>KEY = 0x05 || public</c>).</summary>
public sealed class MollyTestIdentity : IDisposable
{
    public byte[] Aci { get; } = RandomNumberGenerator.GetBytes(16);

    public X25519DiffieHellman PrivateKey { get; } = X25519DiffieHellman.GenerateKey();

    public byte[] Key => [0x05, .. PrivateKey.ExportPublicKey()];

    public void Dispose() => PrivateKey.Dispose();
}

/// <summary>
/// Client-side encodings of the Sharing/README.md objects, written independently of <see cref="MollyShareCodec"/>
/// (and checked against the cross-language vectors), so tests can build real requests.
/// </summary>
public static class MollyShareTestEncoding
{
    public static ReadOnlySpan<byte> Prefix => "MihuBot.Molly.Share.v1/"u8;

    public static byte[] U16(int value)
    {
        byte[] bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)value));
        return bytes;
    }

    public static byte[] U32(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, checked((uint)value));
        return bytes;
    }

    public static byte[] I64(long value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return bytes;
    }

    public static byte[] Descriptor(byte[] entry, uint revision, byte[] ownerAci, byte[] ownerKey, byte[] recipientAci, byte[] recipientKey, IReadOnlyList<byte[]> objects)
    {
        List<byte> descriptor = [1, .. entry, .. U32(checked((int)revision)), .. ownerAci, .. ownerKey, .. recipientAci, .. recipientKey, 32];

        foreach (byte[] value in objects)
        {
            descriptor.AddRange(U32(value.Length));
            descriptor.AddRange(SHA512.HashData(value));
        }

        return [.. descriptor];
    }

    public static byte[] ShareT(byte[] share, byte[] entry, byte[] aci, byte[] key, int index, int threshold, int count) =>
        [1, .. share, .. entry, .. aci, .. key, (byte)index, (byte)threshold, (byte)count];

    public static byte[] Slot(byte[] aci, byte[] key, int index, byte[] sealedValue, byte[] signature) =>
        [.. aci, .. key, (byte)index, .. U16(sealedValue.Length), .. sealedValue, .. new byte[1536 - sealedValue.Length], .. signature];

    public static byte[] Manifest(byte[] entry, int threshold, IReadOnlyList<byte[]> slots)
    {
        List<byte> manifest = [1, .. entry, (byte)threshold, (byte)slots.Count];

        foreach (byte[] slot in slots)
        {
            manifest.AddRange(slot);
        }

        manifest.AddRange(new byte[(32 - slots.Count) * 1652]);
        return [.. manifest];
    }

    /// <summary>A create or update body: <c>version || descriptor || signature</c> and the length-prefixed objects.</summary>
    public static byte[] SignedBody(byte[] descriptor, byte[] signature, IReadOnlyList<byte[]> objects)
    {
        List<byte> body = [1, .. descriptor, .. signature];

        foreach (byte[] value in objects)
        {
            body.AddRange(U32(value.Length));
            body.AddRange(value);
        }

        return [.. body];
    }

    public static byte[] StartBody(byte[] entry, byte[] manifestSenc) => [.. entry, .. U32(manifestSenc.Length), .. manifestSenc];

    public static byte[] ApproveBody(byte[] entry, long startedAt, byte[] shareTSenc, byte[] signature) =>
        [.. entry, .. I64(startedAt), .. U32(shareTSenc.Length), .. shareTSenc, .. signature];

    /// <summary><c>P || purpose || 0x00 || fields</c>, what clients sign.</summary>
    public static byte[] SignedMessage(string purpose, byte[] fields) => [.. Prefix, .. Encoding.ASCII.GetBytes(purpose), 0, .. fields];

    public static byte[] Instructions(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        return [.. U16(utf8.Length), .. utf8, .. new byte[1024 - utf8.Length]];
    }

    public static byte[] File(byte[] content) => [.. U32(content.Length), .. content, .. new byte[65536 - content.Length]];

    public static byte[] Metadata(int kind, string name, string mime, int contentLength, string message)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] mimeBytes = Encoding.ASCII.GetBytes(mime);
        byte[] messageBytes = Encoding.UTF8.GetBytes(message);
        byte[] metadata = [(byte)kind, .. U16(nameBytes.Length), .. nameBytes, .. U16(mimeBytes.Length), .. mimeBytes, .. U32(contentLength), .. U16(messageBytes.Length), .. messageBytes];
        return [.. metadata, .. new byte[4096 - metadata.Length]];
    }
}

/// <summary>
/// Everything A builds for one share: the real SKEY layers, Shamir shares of SHARED_SECRET, SHARE_T_SENC per
/// trustee and DATA_KEY_SEALED_SHAREDENC. HPKE blobs and XEdDSA signatures are random bytes of the right length,
/// which S never opens or verifies. <see cref="Update"/> builds the next revision like a client's update.
/// </summary>
public sealed class MollyTestShare
{
    public byte[] Entry { get; }
    public MollyTestIdentity Owner { get; }
    public MollyTestIdentity Recipient { get; }
    public MollyTestIdentity[] Trustees { get; private set; }
    public int Threshold { get; private set; }
    public uint Revision { get; private set; }

    public byte[] Skey { get; private set; } = [];
    public byte[] SharedSecret { get; private set; } = [];
    public byte[] DataKey { get; } = RandomNumberGenerator.GetBytes(32);
    public byte[] FileContent { get; } = Encoding.UTF8.GetBytes("protected note");

    public byte[] Descriptor { get; private set; } = [];
    public byte[] GrantSignature { get; private set; } = [];
    public byte[] DataKeySealed { get; } = RandomNumberGenerator.GetBytes(81);
    public byte[] MetadataSealed { get; } = RandomNumberGenerator.GetBytes(4145);
    public byte[] EncryptedFile { get; }
    public byte[] RosterSealed { get; private set; } = [];
    public byte[] DataKeySealedSharedEnc { get; private set; } = [];
    public byte[] ManifestSenc { get; private set; } = [];
    public byte[][] ShareTSenc { get; private set; } = [];
    public byte[][] Sealed { get; private set; } = [];
    public byte[][] SealedSignatures { get; private set; } = [];
    public byte[][] ApprovalSignatures { get; private set; } = [];

    /// <param name="corruptSharedSecret">Encrypts DATA_KEY_SEALED_SHAREDENC under a key the shares don't reconstruct.</param>
    /// <param name="shareTOverride">Replaces a trustee's SHARE_T plaintext before encryption, for invalid shares.</param>
    /// <param name="wrongDataKeySealed">Encrypts a DATA_KEY_SEALED other than the one the descriptor commits to.</param>
    /// <param name="entry">The ENTRY from <c>share-reserve</c>, or null for a random one that tests reserve directly.</param>
    public MollyTestShare(MollyTestIdentity owner, MollyTestIdentity recipient, MollyTestIdentity[] trustees, int threshold, bool corruptSharedSecret = false, Func<int, byte[], byte[]>? shareTOverride = null, bool wrongDataKeySealed = false, byte[]? entry = null)
    {
        if (entry is null)
        {
            entry = RandomNumberGenerator.GetBytes(16);
            entry[0] |= 1;
        }

        Entry = entry;
        Owner = owner;
        Recipient = recipient;
        Trustees = trustees;
        Threshold = threshold;
        EncryptedFile = MollyShareCrypto.Encrypt(DataKey, MollyShareCrypto.DataFileLabel, Entry, MollyShareTestEncoding.File(FileContent));

        BuildRoster(corruptSharedSecret, shareTOverride, wrongDataKeySealed);
    }

    /// <summary>A fresh SKEY, SHARED_SECRET, shares and roster for the current trustees, and the descriptor committing to them.</summary>
    private void BuildRoster(bool corruptSharedSecret, Func<int, byte[], byte[]>? shareTOverride, bool wrongDataKeySealed)
    {
        Skey = RandomNumberGenerator.GetBytes(32);
        SharedSecret = RandomNumberGenerator.GetBytes(32);
        GrantSignature = RandomNumberGenerator.GetBytes(64);

        byte[] sharedSecret = corruptSharedSecret ? RandomNumberGenerator.GetBytes(32) : SharedSecret;
        byte[] dataKeySealed = wrongDataKeySealed ? RandomNumberGenerator.GetBytes(81) : DataKeySealed;
        DataKeySealedSharedEnc = MollyShareCrypto.Encrypt(sharedSecret, MollyShareCrypto.SharedDataKeyLabel, Entry, dataKeySealed);

        (byte Index, byte[] Value)[] shares = MollyShamir.Split(SharedSecret, Threshold, Trustees.Length);
        ShareTSenc = new byte[Trustees.Length][];
        Sealed = new byte[Trustees.Length][];
        SealedSignatures = new byte[Trustees.Length][];
        ApprovalSignatures = new byte[Trustees.Length][];
        var slots = new List<byte[]>();

        for (int i = 0; i < Trustees.Length; i++)
        {
            byte[] shareT = MollyShareTestEncoding.ShareT(shares[i].Value, Entry, Trustees[i].Aci, Trustees[i].Key, i + 1, Threshold, Trustees.Length);
            shareT = shareTOverride?.Invoke(i, shareT) ?? shareT;
            ShareTSenc[i] = MollyShareCrypto.Encrypt(Skey, MollyShareCrypto.SkeyShareLabel, Entry, shareT);
            Sealed[i] = RandomNumberGenerator.GetBytes(1395);
            SealedSignatures[i] = RandomNumberGenerator.GetBytes(64);
            ApprovalSignatures[i] = RandomNumberGenerator.GetBytes(64);
            slots.Add(MollyShareTestEncoding.Slot(Trustees[i].Aci, Trustees[i].Key, i + 1, Sealed[i], SealedSignatures[i]));
        }

        ManifestSenc = EncryptManifest(MollyShareTestEncoding.Manifest(Entry, Threshold, slots));
        RosterSealed = RandomNumberGenerator.GetBytes(52972);
        Descriptor = MollyShareTestEncoding.Descriptor(Entry, Revision, Owner.Aci, Owner.Key, Recipient.Aci, Recipient.Key, [RosterSealed, DataKeySealedSharedEnc, MetadataSealed, EncryptedFile, DataKeySealed]);
    }

    public byte[] CreateBody() => MollyShareTestEncoding.SignedBody(Descriptor, GrantSignature, [RosterSealed, DataKeySealedSharedEnc, MetadataSealed, EncryptedFile, Skey]);

    /// <summary>Moves to the next revision with a new roster, and returns its <c>share-update</c> body.</summary>
    public byte[] Update(MollyTestIdentity[]? trustees = null, int? threshold = null)
    {
        Trustees = trustees ?? Trustees;
        Threshold = threshold ?? Threshold;
        Revision++;

        BuildRoster(corruptSharedSecret: false, shareTOverride: null, wrongDataKeySealed: false);

        return UpdateBody();
    }

    /// <summary>The current revision's <c>share-update</c> body.</summary>
    public byte[] UpdateBody() => MollyShareTestEncoding.SignedBody(Descriptor, GrantSignature, [RosterSealed, DataKeySealedSharedEnc, Skey]);

    public byte[] EncryptManifest(byte[] manifest) => MollyShareCrypto.Encrypt(Skey, MollyShareCrypto.SkeyRosterInnerLabel, Entry, manifest);

    public byte[] StartBody() => MollyShareTestEncoding.StartBody(Entry, ManifestSenc);

    public byte[] ApproveBody(int trustee, long startedAt) => MollyShareTestEncoding.ApproveBody(Entry, startedAt, ShareTSenc[trustee], ApprovalSignatures[trustee]);
}

/// <summary>Sends share requests straight to a <see cref="MollyShareService"/>, as a client would.</summary>
public sealed class MollyShareTestClient(MollyShareService service, MollyRequestProtector protector)
{
    public static string Base64(byte[] value) => Convert.ToBase64String(value);

    /// <summary>An exchange sealed to the server's current rotating transport key.</summary>
    public MollyExchangeContext NewExchange(byte[]? recipientPublicKey = null)
    {
        recipientPublicKey ??= Convert.FromBase64String(protector.GetTransportKey().PublicKey);
        return new MollyExchangeContext(MollyTestEnvelope.GetRecipientKeyId(recipientPublicKey), RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16), MollyTestEnvelope.Now(), TimeSpan.Zero);
    }

    public static MollyShareRequest CreateRequest(string sessionId, byte[] body) => new() { Id = sessionId, Body = Base64(body) };

    public async Task<JsonElement> SendAsync(string sessionId, string action, byte[] body) =>
        await SendAsync(action, CreateRequest(sessionId, body), MollyTestSessions.ProvenExchange(sessionId, action, NewExchange()));

    public async Task<JsonElement> SendAsync(string action, MollyShareRequest request, MollyExchangeContext exchange)
    {
        MollyApiResponse response = await service.ExecuteAsync(action, request, exchange, CancellationToken.None);
        return JsonSerializer.SerializeToElement(response);
    }

    /// <summary>
    /// The <c>identity</c> of an <c>associate</c> request: its MAC, normally for action <c>associate</c> (others only to
    /// test that they are rejected).
    /// </summary>
    public static MollyAssociateIdentity CreateIdentityProof(MollyTestIdentity identity, MollyExchangeContext exchange, byte[] transportPublicKey, string action = MollyApiActions.Associate)
    {
        byte[] agreement = new byte[32];
        identity.PrivateKey.DeriveRawSecretAgreement(transportPublicKey, agreement);
        byte[] mac = MollyShareCrypto.ComputeRequestMac(agreement, exchange.RecipientKeyId, exchange.ClientEphemeralPublicKey, exchange.RequestNonce, exchange.RequestTimestamp, action, identity.Aci, identity.Key);

        return new MollyAssociateIdentity { Aci = Base64(identity.Aci), IdentityKey = Base64(identity.Key), Mac = Base64(mac) };
    }

    /// <summary>Associates the session, asking to bind its entry to <paramref name="identity"/>.</summary>
    public async Task<MollyCommandResult> AssociateAsync(string sessionId, MollyTestIdentity identity, string nickname = "share-user")
    {
        byte[] transportPublicKey = Convert.FromBase64String(protector.GetTransportKey().PublicKey);
        MollyExchangeContext exchange = MollyTestSessions.ProvenExchange(sessionId, MollyApiActions.Associate, NewExchange(transportPublicKey));

        var request = new MollyAssociateRequest { Id = sessionId, Nickname = nickname, Identity = CreateIdentityProof(identity, exchange, transportPublicKey) };
        return await service.AssociateAsync(request, exchange, CancellationToken.None);
    }
}
