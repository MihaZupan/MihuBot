using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using MihuBot.Helpers.Crypto;
using MihuBot.Molly;
using MihuBot.Molly.Api;
using MihuBot.Molly.Sharing;
using static MihuBot.Tests.Molly.MollyShareTestEncoding;

namespace MihuBot.Tests.Molly;

/// <summary>Cross-language vectors (FileSharingVectors.json, from <see cref="FileSharingVectorsTests"/>), Shamir, codec and request MAC.</summary>
public sealed class MollyShareCryptoTests
{
    private static readonly JsonElement s_vectors = JsonDocument.Parse(System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Molly", "FileSharingVectors.json"))).RootElement;

    private static byte[] Hex(JsonElement element) => Convert.FromHexString(element.GetString()!);

    private static byte[] Hex(JsonElement element, string property) => Hex(element.GetProperty(property));

    [Fact]
    public void Vectors_GfMul()
    {
        foreach (JsonElement vector in s_vectors.GetProperty("gfMul").EnumerateArray())
        {
            byte a = vector[0].GetByte(), b = vector[1].GetByte(), expected = vector[2].GetByte();
            Assert.Equal(expected, MollyShamir.Multiply(a, b));
            Assert.Equal(expected, MollyShamir.Multiply(b, a));
        }
    }

    [Fact]
    public void Multiply_MatchesShiftAndAddForEveryPair()
    {
        for (int a = 0; a < 256; a++)
        {
            for (int b = 0; b < 256; b++)
            {
                Assert.Equal(ShiftAndAddMultiply(a, b), MollyShamir.Multiply((byte)a, (byte)b));
            }
        }

        static byte ShiftAndAddMultiply(int a, int b)
        {
            int result = 0;

            for (; b != 0; b >>= 1)
            {
                if ((b & 1) != 0)
                {
                    result ^= a;
                }

                a <<= 1;

                if ((a & 0x100) != 0)
                {
                    a ^= 0x11B;
                }
            }

            return (byte)result;
        }
    }

    [Fact]
    public void Inverse_IsTheMultiplicativeInverseOfEveryNonZeroElement()
    {
        for (int a = 1; a < 256; a++)
        {
            Assert.Equal(1, MollyShamir.Multiply((byte)a, MollyShamir.Inverse((byte)a)));
        }

        Assert.Equal(0, MollyShamir.Inverse(0));
    }

    [Fact]
    public void Vectors_Shamir()
    {
        JsonElement vector = s_vectors.GetProperty("shamir");
        byte[] secret = Hex(vector, "secret");
        byte[][] coefficients = [.. vector.GetProperty("coefficients").EnumerateArray().Select(Hex)];
        (byte Index, byte[] Value)[] shares = [.. vector.GetProperty("shares").EnumerateArray().Select(s => (s.GetProperty("index").GetByte(), Hex(s, "value")))];

        foreach ((byte index, byte[] value) in shares)
        {
            Assert.Equal(value, MollyShamir.Evaluate(secret, coefficients, index));
        }

        Assert.Equal(secret, MollyShamir.Combine([shares[0], shares[1], shares[2]]));
        Assert.Equal(secret, MollyShamir.Combine([shares[4], shares[1], shares[3]]));
        Assert.Equal(secret, MollyShamir.Combine(shares));
        Assert.NotEqual(secret, MollyShamir.Combine([shares[0], shares[1]]));

        JsonElement one = s_vectors.GetProperty("shamirThresholdOne");
        Assert.Equal(Hex(one, "share"), MollyShamir.Evaluate(Hex(one, "secret"), [], 1));
        Assert.Equal(Hex(one, "secret"), MollyShamir.Combine([(1, Hex(one, "share"))]));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 5)]
    [InlineData(2, 3)]
    [InlineData(5, 9)]
    [InlineData(32, 32)]
    public void Shamir_AnyThresholdSubsetReconstructs(int threshold, int count)
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        (byte Index, byte[] Value)[] shares = MollyShamir.Split(secret, threshold, count);

        Assert.Equal(secret, MollyShamir.Combine(shares[..threshold]));
        Assert.Equal(secret, MollyShamir.Combine([.. shares.Reverse().Take(threshold)]));
        Assert.Equal(secret, MollyShamir.Combine(shares));

        if (threshold > 1)
        {
            Assert.NotEqual(secret, MollyShamir.Combine(shares[..(threshold - 1)]));
        }
    }

    [Fact]
    public void Shamir_RejectsZeroDuplicateOrMismatchedShares()
    {
        Assert.Throws<ArgumentException>(() => MollyShamir.Combine([]));
        Assert.Throws<ArgumentException>(() => MollyShamir.Combine([(0, new byte[32])]));
        Assert.Throws<ArgumentException>(() => MollyShamir.Combine([(1, new byte[32]), (1, new byte[32])]));
        Assert.Throws<ArgumentException>(() => MollyShamir.Combine([(1, new byte[32]), (2, new byte[31])]));
    }

    [Fact]
    public void Vectors_Aead()
    {
        foreach (JsonElement vector in s_vectors.GetProperty("aead").EnumerateArray())
        {
            byte[] key = Hex(vector, "key");
            byte[] entry = Hex(vector, "entry");
            string label = vector.GetProperty("label").GetString()!;
            byte[] nonce = Hex(vector, "nonce");
            byte[] plaintext = Hex(vector, "plaintext");
            byte[] ciphertext = Hex(vector, "ciphertext");

            byte[] subkey = new byte[32];
            MollyShareCrypto.DeriveSubkey(key, label, entry, subkey);
            Assert.Equal(Hex(vector, "subkey"), subkey);

            byte[] encrypted = new byte[plaintext.Length + 16];
            using (var aead = new XAesGcm(subkey))
            {
                aead.Encrypt(nonce, plaintext, encrypted.AsSpan(0, plaintext.Length), encrypted.AsSpan(plaintext.Length), MollyShareCrypto.GetAssociatedData(label, entry));
            }

            Assert.Equal(ciphertext, nonce.Concat(encrypted).ToArray());
            Assert.True(MollyShareCrypto.TryDecrypt(key, label, entry, ciphertext, out byte[]? decrypted));
            Assert.Equal(plaintext, decrypted);

            Assert.False(MollyShareCrypto.TryDecrypt(key, label == "skey/share" ? "skey/roster-inner" : "skey/share", entry, ciphertext, out _));
            Assert.False(MollyShareCrypto.TryDecrypt(key, label, [.. entry[..15], (byte)(entry[15] ^ 1)], ciphertext, out _));
        }
    }

    [Fact]
    public void Vectors_RequestAuth()
    {
        JsonElement vector = s_vectors.GetProperty("requestAuth");

        using X25519DiffieHellman identity = X25519DiffieHellman.ImportPrivateKey(Hex(vector, "identityPrivateKey"));
        Assert.Equal(Hex(vector, "identityKey"), new byte[] { 0x05 }.Concat(identity.ExportPublicKey()).ToArray());
        Assert.Equal(Hex(vector, "recipientKeyId"), MollyTestEnvelope.GetRecipientKeyId(Hex(vector, "transportPublicKey")));

        byte[] dh = new byte[32];
        identity.DeriveRawSecretAgreement(Hex(vector, "transportPublicKey"), dh);
        Assert.Equal(Hex(vector, "dh"), dh);

        var exchange = new MollyExchangeContext(Hex(vector, "recipientKeyId"), Hex(vector, "clientEphemeralPublicKey"), Hex(vector, "requestNonce"), TimeSpan.Zero);
        string action = vector.GetProperty("action").GetString()!;
        byte[] mac = MollyShareCrypto.ComputeRequestMac(dh, exchange.RecipientKeyId, exchange.ClientEphemeralPublicKey, exchange.RequestNonce, action, Hex(vector, "aci"), Hex(vector, "identityKey"));
        Assert.Equal(Hex(vector, "mac"), mac);

        Assert.True(MollyShareCrypto.VerifyRequestMac(dh, exchange, action, Hex(vector, "aci"), Hex(vector, "identityKey"), mac));
        Assert.False(MollyShareCrypto.VerifyRequestMac(dh, exchange, MollyApiActions.ShareFetch, Hex(vector, "aci"), Hex(vector, "identityKey"), mac));
        Assert.False(MollyShareCrypto.VerifyRequestMac(dh, exchange, action, Hex(vector, "aci"), Hex(vector, "identityKey"), mac[..63]));
    }

    [Fact]
    public void Vectors_Codec()
    {
        JsonElement codec = s_vectors.GetProperty("codec");
        byte[] entry = Hex(codec, "entry");
        byte[] ownerAci = Hex(codec, "ownerAci"), ownerKey = Hex(codec, "ownerKey");
        byte[] recipientAci = Hex(codec, "recipientAci"), recipientKey = Hex(codec, "recipientKey");
        byte[] trusteeAci = Hex(codec, "trusteeAci"), trusteeKey = Hex(codec, "trusteeKey");

        JsonElement shareT = codec.GetProperty("shareT");
        byte[] encodedShareT = Hex(shareT, "encoded");
        Assert.Equal(encodedShareT, ShareT(Hex(shareT, "value"), entry, trusteeAci, trusteeKey, 2, 3, 5));
        Assert.True(MollyShareCodec.TryParseShareT(encodedShareT, out MollyShareT? parsedShareT));
        Assert.Equal(Hex(shareT, "value"), parsedShareT.Share);
        Assert.Equal(entry, parsedShareT.Entry);
        Assert.Equal(trusteeAci, parsedShareT.Aci);
        Assert.Equal(trusteeKey, parsedShareT.Key);
        Assert.Equal(((byte)2, 3, 5), (parsedShareT.Index, parsedShareT.Threshold, parsedShareT.Count));

        JsonElement descriptor = codec.GetProperty("descriptor");
        byte[] encodedDescriptor = Hex(descriptor, "encoded");
        Assert.True(MollyShareCodec.TryParseDescriptor(encodedDescriptor, out MollyShareDescriptor? parsedDescriptor));
        Assert.Equal(entry, parsedDescriptor.Entry);
        Assert.Equal(descriptor.GetProperty("revision").GetUInt32(), parsedDescriptor.Revision);
        Assert.Equal(ownerAci, parsedDescriptor.OwnerAci);
        Assert.Equal(ownerKey, parsedDescriptor.OwnerKey);
        Assert.Equal(recipientAci, parsedDescriptor.RecipientAci);
        Assert.Equal(recipientKey, parsedDescriptor.RecipientKey);
        Assert.Equal(descriptor.GetProperty("objectLengths").EnumerateArray().Select(l => l.GetInt32()), MollyShareCodec.DescriptorObjectLengths.ToArray());
        Assert.Equal(descriptor.GetProperty("objectSha512").EnumerateArray().Select(Hex), parsedDescriptor.ObjectHashes);

        Assert.Equal(Hex(codec, "grantMessage"), SignedMessage("grant", encodedDescriptor));

        JsonElement approval = codec.GetProperty("approvalMessage");
        byte[] shareTSenc = Hex(approval, "shareTSenc");
        Assert.Equal(Hex(approval, "encoded"), SignedMessage("approval", [.. entry, .. I64(approval.GetProperty("startedAt").GetInt64()), .. trusteeAci, .. trusteeKey, .. SHA512.HashData(shareTSenc)]));

        JsonElement trusteeSealed = codec.GetProperty("trusteeSealedMessage");
        byte[] sealedHash = Hex(trusteeSealed, "sealedSha512");
        Assert.Equal(Hex(trusteeSealed, "encoded"), SignedMessage("trustee-sealed", [.. entry, .. ownerAci, .. ownerKey, .. trusteeAci, .. trusteeKey, .. sealedHash]));

        JsonElement partyHash = codec.GetProperty("partyHash");
        byte[] serverKey = MollyShareCrypto.DeriveServerKey(Hex(partyHash, "databaseKey"));
        Assert.Equal(Hex(partyHash, "serverKey"), serverKey);
        Assert.Equal(Hex(partyHash, "hash"), MollyShareCrypto.ComputePartyHash(serverKey, trusteeAci, trusteeKey));

        JsonElement instructions = codec.GetProperty("instructions");
        Assert.Equal(Hex(instructions, "sha512"), SHA512.HashData(Instructions(instructions.GetProperty("text").GetString()!)));

        JsonElement file = codec.GetProperty("file");
        Assert.Equal(Hex(file, "sha512"), SHA512.HashData(File(Hex(file, "content"))));

        JsonElement metadata = codec.GetProperty("metadata");
        byte[] encodedMetadata = Metadata(metadata.GetProperty("kind").GetInt32(), metadata.GetProperty("name").GetString()!, metadata.GetProperty("mime").GetString()!, metadata.GetProperty("contentLength").GetInt32(), metadata.GetProperty("message").GetString()!);
        Assert.Equal(Hex(metadata, "sha512"), SHA512.HashData(encodedMetadata));
    }

    [Fact]
    public void Codec_ObjectSizesMatchTheContract()
    {
        Assert.Equal(101, MollyShareCodec.ShareTLength);
        Assert.Equal(1652, MollyShareCodec.SlotLength);
        Assert.Equal(52883, MollyShareCodec.ManifestLength);
        Assert.Equal(460, MollyShareCodec.DescriptorLength);
    }

    [Fact]
    public void Codec_ParsesAValidCreateBody_AndRejectsMalformedOnes()
    {
        using var owner = new MollyTestIdentity();
        using var recipient = new MollyTestIdentity();
        using var trustee = new MollyTestIdentity();
        var share = new MollyTestShare(owner, recipient, [trustee], 1);
        byte[] body = share.CreateBody();

        Assert.True(MollyShareCodec.TryParseCreate(body, out MollyShareCreateRequest? request));
        Assert.Equal(share.Skey, request.Skey);
        Assert.Equal(share.EncryptedFile, request.EncryptedFile);

        Assert.False(MollyShareCodec.TryParseCreate([.. body, 0], out _));
        Assert.False(MollyShareCodec.TryParseCreate(body[..^1], out _));
        Assert.False(MollyShareCodec.TryParseCreate(Mutate(body, 0, 2), out _));

        // Descriptor: version, zero ENTRY, key type, capacity, length field.
        Assert.False(MollyShareCodec.TryParseCreate(Mutate(body, 1, 2), out _));
        Assert.False(MollyShareCodec.TryParseCreate(Zero(body, 2, 16), out _));
        Assert.False(MollyShareCodec.TryParseCreate(Mutate(body, 1 + 1 + 16 + 4 + 16, 0x06), out _));
        Assert.False(MollyShareCodec.TryParseCreate(Mutate(body, 1 + 119, 31), out _));
        Assert.False(MollyShareCodec.TryParseCreate(Mutate(body, 1 + 120 + 3, 82), out _));

        // A flipped object byte no longer matches the descriptor hash; SKEY (the last object) isn't in it.
        int fileOffset = body.Length - 32 - 4 - 40;
        Assert.False(MollyShareCodec.TryParseCreate(Mutate(body, fileOffset, (byte)(body[fileOffset] ^ 1)), out _));
        Assert.True(MollyShareCodec.TryParseCreate(Mutate(body, body.Length - 1, (byte)(body[^1] ^ 1)), out _));
    }

    [Fact]
    public void Codec_ParsesAValidUpdateBody_AndRejectsMalformedOnes()
    {
        using var owner = new MollyTestIdentity();
        using var recipient = new MollyTestIdentity();
        using var trustee = new MollyTestIdentity();
        var share = new MollyTestShare(owner, recipient, [trustee], 1);
        byte[] body = share.Update();

        Assert.True(MollyShareCodec.TryParseUpdate(body, out MollyShareUpdateRequest? request));
        Assert.Equal(1u, request.Descriptor.Revision);
        Assert.Equal(share.Skey, request.Skey);
        Assert.Equal(share.RosterSealed, request.RosterSealed);
        Assert.Equal(share.DataKeySealedSharedEnc, request.DataKeySealedSharedEnc);

        // Create and update bodies aren't interchangeable.
        Assert.False(MollyShareCodec.TryParseUpdate(share.CreateBody(), out _));
        Assert.False(MollyShareCodec.TryParseCreate(body, out _));

        Assert.False(MollyShareCodec.TryParseUpdate([.. body, 0], out _));
        Assert.False(MollyShareCodec.TryParseUpdate(Mutate(body, 0, 2), out _));

        int rosterOffset = 1 + 460 + 64 + 4;
        Assert.False(MollyShareCodec.TryParseUpdate(Mutate(body, rosterOffset, (byte)(body[rosterOffset] ^ 1)), out _));
    }

    [Fact]
    public void Codec_RejectsMalformedManifests()
    {
        using var owner = new MollyTestIdentity();
        using var recipient = new MollyTestIdentity();
        using var first = new MollyTestIdentity();
        using var second = new MollyTestIdentity();
        var share = new MollyTestShare(owner, recipient, [first, second], 2);
        Assert.True(MollyShareCodec.TryParseDescriptor(share.Descriptor, out MollyShareDescriptor? descriptor));

        byte[] Slot(MollyTestIdentity identity, int index) => MollyShareTestEncoding.Slot(identity.Aci, identity.Key, index, new byte[1395], new byte[64]);
        byte[] Valid() => Manifest(share.Entry, 2, [Slot(first, 1), Slot(second, 2)]);

        Assert.True(MollyShareCodec.TryParseManifest(Valid(), descriptor, out MollyShareManifest? manifest));
        Assert.Equal(2, manifest.Threshold);
        Assert.Equal(new[] { 1, 2 }, manifest.Slots.Select(s => (int)s.Index));

        int slotsOffset = 1 + 16 + 2;

        Assert.False(MollyShareCodec.TryParseManifest(Valid()[..^1], descriptor, out _));
        Assert.False(MollyShareCodec.TryParseManifest(Mutate(Valid(), 0, 2), descriptor, out _));
        Assert.False(MollyShareCodec.TryParseManifest(Mutate(Valid(), 1, (byte)(share.Entry[0] ^ 1)), descriptor, out _));
        Assert.False(MollyShareCodec.TryParseManifest(Mutate(Valid(), 17, 3), descriptor, out _));
        Assert.False(MollyShareCodec.TryParseManifest(Mutate(Valid(), 17, 0), descriptor, out _));
        Assert.False(MollyShareCodec.TryParseManifest(Manifest(share.Entry, 1, [Slot(first, 2), Slot(second, 1)]), descriptor, out _));
        Assert.False(MollyShareCodec.TryParseManifest(Manifest(share.Entry, 1, [Slot(first, 1), Slot(first, 2)]), descriptor, out _));
        Assert.False(MollyShareCodec.TryParseManifest(Manifest(share.Entry, 1, [Slot(first, 1), Slot(owner, 2)]), descriptor, out _));
        Assert.False(MollyShareCodec.TryParseManifest(Manifest(share.Entry, 1, [Slot(first, 1), Slot(recipient, 2)]), descriptor, out _));
        Assert.False(MollyShareCodec.TryParseManifest(Mutate(Valid(), slotsOffset + 16, 0x06), descriptor, out _));
        Assert.False(MollyShareCodec.TryParseManifest(Mutate(Valid(), slotsOffset + 16 + 33 + 1 + 1, 0x72), descriptor, out _));
        Assert.False(MollyShareCodec.TryParseManifest(Mutate(Valid(), slotsOffset + 16 + 33 + 3 + 1395, 1), descriptor, out _));
        Assert.False(MollyShareCodec.TryParseManifest(Mutate(Valid(), slotsOffset + (5 * 1652) + 100, 1), descriptor, out _));
    }

    [Fact]
    public void Codec_RejectsMalformedSmallBodies()
    {
        Assert.False(MollyShareCodec.TryParseEntry(new byte[16], out _));
        Assert.False(MollyShareCodec.TryParseEntry(new byte[15], out _));
        Assert.True(MollyShareCodec.TryParseEntry([.. new byte[15], 1], out _));

        byte[] entry = RandomNumberGenerator.GetBytes(16);
        byte[] approve = ApproveBody(entry, 1, new byte[141], new byte[64]);
        Assert.True(MollyShareCodec.TryParseApprove(approve, out _));
        Assert.False(MollyShareCodec.TryParseApprove([.. approve, 0], out _));
        Assert.False(MollyShareCodec.TryParseApprove(Mutate(approve, 16 + 8 + 3, 140), out _));

        byte[] start = StartBody(entry, new byte[52923]);
        Assert.True(MollyShareCodec.TryParseStart(start, out _));
        Assert.False(MollyShareCodec.TryParseStart(start[..^1], out _));
        Assert.False(MollyShareCodec.TryParseStart(Zero(start, 0, 16), out _));

        byte[] shareT = ShareT(new byte[32], entry, new byte[16], [0x05, .. new byte[32]], 1, 1, 1);
        Assert.True(MollyShareCodec.TryParseShareT(shareT, out _));
        Assert.False(MollyShareCodec.TryParseShareT(Mutate(shareT, 0, 2), out _));
        Assert.False(MollyShareCodec.TryParseShareT(Mutate(shareT, 1 + 32 + 16 + 16, 0x04), out _));
        Assert.False(MollyShareCodec.TryParseShareT(Mutate(shareT, 98, 2), out _));
        Assert.False(MollyShareCodec.TryParseShareT(Mutate(shareT, 98, 0), out _));
        Assert.False(MollyShareCodec.TryParseShareT(Mutate(shareT, 99, 2), out _));
    }

    [Fact]
    public void ShareAgreement_UsesTheCurrentRotatingKey_AndRejectsDiscoveryAndLowOrderKeys()
    {
        using var protector = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes);
        using var identity = new MollyTestIdentity();
        var client = new MollyShareTestClient(null!, protector);
        byte[] identityPublicKey = identity.Key[1..];
        byte[] shared = new byte[32];

        MollyExchangeContext exchange = client.NewExchange();
        Assert.True(protector.TryDeriveShareAgreement(exchange, identityPublicKey, shared));

        byte[] expected = new byte[32];
        identity.PrivateKey.DeriveRawSecretAgreement(Convert.FromBase64String(protector.GetTransportKey().PublicKey), expected);
        Assert.Equal(expected, shared);

        Assert.False(protector.TryDeriveShareAgreement(client.NewExchange(MollyTestKeys.TransportPublicKeyBytes), identityPublicKey, shared));
        Assert.False(protector.TryDeriveShareAgreement(client.NewExchange(MollyTestKeys.OtherTransportPublicKeyBytes), identityPublicKey, shared));
        Assert.False(protector.TryDeriveShareAgreement(exchange, new byte[32], shared));
        Assert.False(protector.TryDeriveShareAgreement(exchange, identityPublicKey[..31], shared));
    }

    [Fact]
    public void ShareAgreement_UsesTheKeyTheExchangeWasDecryptedWith_EvenAfterItRetires()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var protector = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes, time);
        using var identity = new MollyTestIdentity();
        byte[] identityPublicKey = identity.Key[1..];
        byte[] shared = new byte[32];

        using var envelope = new MollyTestEnvelope(protector.GetTransportKey());
        Assert.True(protector.TryDecryptRequest(envelope.EncryptRequest(MollyApiActions.ShareSync, timestamp: time.GetUtcNow().ToUnixTimeSeconds()), out _, out _, out _, out MollyExchangeContext? exchange));
        Assert.NotNull(exchange.TransportKey);

        using var discovery = new MollyTestEnvelope();
        Assert.True(protector.TryDecryptRequest(discovery.EncryptRequest(MollyApiActions.TransportKey, timestamp: time.GetUtcNow().ToUnixTimeSeconds()), out _, out _, out _, out MollyExchangeContext? discoveryExchange));
        Assert.Null(discoveryExchange.TransportKey);
        Assert.False(protector.TryDeriveShareAgreement(discoveryExchange, identityPublicKey, shared));

        time.Advance(MollyTransportKeyRing.KeyValidity + TimeSpan.FromMinutes(2));

        Assert.True(protector.TryDeriveShareAgreement(exchange, identityPublicKey, shared));
        Assert.False(protector.TryDeriveShareAgreement(exchange with { TransportKey = null }, identityPublicKey, shared));
    }

    private static byte[] Mutate(byte[] value, int offset, byte replacement)
    {
        byte[] copy = [.. value];
        copy[offset] = replacement;
        return copy;
    }

    private static byte[] Zero(byte[] value, int offset, int length)
    {
        byte[] copy = [.. value];
        copy.AsSpan(offset, length).Clear();
        return copy;
    }
}
