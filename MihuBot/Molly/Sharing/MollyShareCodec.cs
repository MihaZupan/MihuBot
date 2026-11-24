using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

#nullable enable

namespace MihuBot.Molly.Sharing;

/// <summary>A parsed GRANT_DESCRIPTOR. <see cref="Encoded"/> holds the exact signed bytes.</summary>
internal sealed record MollyShareDescriptor(byte[] Encoded, byte[] Entry, uint Revision, byte[] OwnerAci, byte[] OwnerKey, byte[] RecipientAci, byte[] RecipientKey, byte[][] ObjectHashes);

/// <summary>A parsed <c>share-create</c> body, whose objects match the descriptor's lengths and hashes.</summary>
internal sealed record MollyShareCreateRequest(MollyShareDescriptor Descriptor, byte[] Signature, byte[] RosterSealed, byte[] DataKeySealedSharedEnc, byte[] MetadataSealed, byte[] EncryptedFile, byte[] Skey);

/// <summary>A parsed <c>share-update</c> body, whose new objects match the descriptor's lengths and hashes.</summary>
internal sealed record MollyShareUpdateRequest(MollyShareDescriptor Descriptor, byte[] Signature, byte[] RosterSealed, byte[] DataKeySealedSharedEnc, byte[] Skey);

internal sealed record MollyShareStartRequest(byte[] Entry, byte[] ManifestSenc);

internal sealed record MollyShareApproveRequest(byte[] Entry, long StartedAt, byte[] ShareTSenc, byte[] Signature);

/// <summary>A used TRUSTEE_MANIFEST slot. <see cref="Index"/> is <see cref="Slot"/> + 1.</summary>
internal sealed record MollyShareTrusteeSlot(int Slot, byte[] Aci, byte[] Key, byte[] Sealed, byte[] SealedSignature)
{
    public byte Index => (byte)(Slot + 1);
}

internal sealed record MollyShareManifest(int Threshold, MollyShareTrusteeSlot[] Slots);

internal sealed record MollyShareT(byte[] Share, byte[] Entry, byte[] Aci, byte[] Key, byte Index, int Threshold, int Count);

/// <summary>
/// Strict parsing of the binary file sharing objects (FileSharing.md). Every object has a fixed size, so
/// lengths are checked exactly and trailing bytes, unknown versions and malformed keys are rejected.
/// </summary>
internal static class MollyShareCodec
{
    public const byte Version = 1;
    public const int EntryLength = 16;
    public const int AciLength = 16;
    public const int IdentityKeyLength = 33;
    public const byte IdentityKeyType = 0x05;
    public const int SignatureLength = 64;
    public const int HashLength = SHA512.HashSizeInBytes;
    public const int Capacity = 32;

    public const int RosterSealedLength = 52972;
    public const int DataKeySealedSharedEncLength = 121;
    public const int MetadataSealedLength = 4145;
    public const int EncryptedFileLength = 65580;
    public const int DataKeySealedLength = 81;
    public const int SkeyLength = 32;

    public const int ShareLength = 32;
    public const int ShareTLength = 1 + ShareLength + EntryLength + AciLength + IdentityKeyLength + 3;
    public const int ShareTSencLength = 141;

    public const int SealedLength = 1395;
    public const int SealedAreaLength = 1536;
    public const int SlotLength = AciLength + IdentityKeyLength + 1 + sizeof(ushort) + SealedAreaLength + SignatureLength;
    public const int ManifestLength = 1 + EntryLength + 2 + (Capacity * SlotLength);
    public const int ManifestSencLength = 52923;

    /// <summary>Indices of the objects the descriptor commits to, in its order.</summary>
    public const int RosterSealedIndex = 0;
    public const int DataKeySealedSharedEncIndex = 1;
    public const int MetadataSealedIndex = 2;
    public const int EncryptedFileIndex = 3;
    public const int DataKeySealedIndex = 4;

    public static ReadOnlySpan<int> DescriptorObjectLengths => [RosterSealedLength, DataKeySealedSharedEncLength, MetadataSealedLength, EncryptedFileLength, DataKeySealedLength];

    public const int DescriptorObjectCount = 5;
    public const int DescriptorLength = 1 + EntryLength + sizeof(uint) + (2 * (AciLength + IdentityKeyLength)) + 1 + (DescriptorObjectCount * (sizeof(uint) + HashLength));

    /// <summary>A create body's objects: the descriptor's first four, then SKEY.</summary>
    public static ReadOnlySpan<int> CreateObjectLengths => [RosterSealedLength, DataKeySealedSharedEncLength, MetadataSealedLength, EncryptedFileLength, SkeyLength];

    /// <summary>An update body's objects: the descriptor's first two, then SKEY.</summary>
    public static ReadOnlySpan<int> UpdateObjectLengths => [RosterSealedLength, DataKeySealedSharedEncLength, SkeyLength];

    public const int CreateBodyLength = 1 + DescriptorLength + SignatureLength + (5 * sizeof(uint)) + RosterSealedLength + DataKeySealedSharedEncLength + MetadataSealedLength + EncryptedFileLength + SkeyLength;
    public const int UpdateBodyLength = 1 + DescriptorLength + SignatureLength + (3 * sizeof(uint)) + RosterSealedLength + DataKeySealedSharedEncLength + SkeyLength;
    public const int StartBodyLength = EntryLength + sizeof(uint) + ManifestSencLength;
    public const int ApproveBodyLength = EntryLength + sizeof(long) + sizeof(uint) + ShareTSencLength + SignatureLength;

    /// <summary>A body that is exactly a non-zero ENTRY.</summary>
    public static bool TryParseEntry(ReadOnlySpan<byte> body, [NotNullWhen(true)] out byte[]? entry)
    {
        entry = null;

        if (body.Length != EntryLength || !body.ContainsAnyExcept((byte)0))
        {
            return false;
        }

        entry = body.ToArray();
        return true;
    }

    public static bool TryParseDescriptor(ReadOnlySpan<byte> encoded, [NotNullWhen(true)] out MollyShareDescriptor? descriptor)
    {
        descriptor = null;

        if (encoded.Length != DescriptorLength || encoded[0] != Version)
        {
            return false;
        }

        var reader = new Reader(encoded.Slice(1));

        if (!TryParseEntry(reader.Read(EntryLength), out byte[]? entry))
        {
            return false;
        }

        uint revision = reader.ReadUInt32();
        byte[] ownerAci = reader.Read(AciLength).ToArray();
        ReadOnlySpan<byte> ownerKey = reader.Read(IdentityKeyLength);
        byte[] recipientAci = reader.Read(AciLength).ToArray();
        ReadOnlySpan<byte> recipientKey = reader.Read(IdentityKeyLength);

        if (!IsIdentityKey(ownerKey) || !IsIdentityKey(recipientKey) || reader.ReadByte() != Capacity)
        {
            return false;
        }

        byte[][] hashes = new byte[DescriptorObjectCount][];

        for (int i = 0; i < DescriptorObjectCount; i++)
        {
            if (reader.ReadUInt32() != DescriptorObjectLengths[i])
            {
                return false;
            }

            hashes[i] = reader.Read(HashLength).ToArray();
        }

        Debug.Assert(reader.Remaining == 0);

        descriptor = new MollyShareDescriptor(encoded.ToArray(), entry, revision, ownerAci, ownerKey.ToArray(), recipientAci, recipientKey.ToArray(), hashes);
        return true;
    }

    /// <summary>
    /// <c>version || GRANT_DESCRIPTOR || signature</c> and the five length-prefixed create objects, which must match
    /// the descriptor's lengths and SHA-512 hashes.
    /// </summary>
    public static bool TryParseCreate(ReadOnlySpan<byte> body, [NotNullWhen(true)] out MollyShareCreateRequest? request)
    {
        request = null;

        if (body.Length != CreateBodyLength || !TryParseSigned(body, CreateObjectLengths, out MollyShareDescriptor? descriptor, out byte[]? signature, out byte[][]? objects))
        {
            return false;
        }

        request = new MollyShareCreateRequest(descriptor, signature, objects[0], objects[1], objects[2], objects[3], objects[4]);
        return true;
    }

    /// <summary>
    /// <c>version || GRANT_DESCRIPTOR || signature</c> and the three length-prefixed update objects, which must match
    /// the descriptor's lengths and SHA-512 hashes.
    /// </summary>
    public static bool TryParseUpdate(ReadOnlySpan<byte> body, [NotNullWhen(true)] out MollyShareUpdateRequest? request)
    {
        request = null;

        if (body.Length != UpdateBodyLength || !TryParseSigned(body, UpdateObjectLengths, out MollyShareDescriptor? descriptor, out byte[]? signature, out byte[][]? objects))
        {
            return false;
        }

        request = new MollyShareUpdateRequest(descriptor, signature, objects[0], objects[1], objects[2]);
        return true;
    }

    /// <summary>
    /// A create or update body of the right total length. Its objects are the descriptor's in order, then SKEY,
    /// which isn't in the descriptor.
    /// </summary>
    private static bool TryParseSigned(ReadOnlySpan<byte> body, ReadOnlySpan<int> objectLengths, [NotNullWhen(true)] out MollyShareDescriptor? descriptor, [NotNullWhen(true)] out byte[]? signature, [NotNullWhen(true)] out byte[][]? objects)
    {
        descriptor = null;
        signature = null;
        objects = null;

        var reader = new Reader(body.Slice(1));

        if (body[0] != Version || !TryParseDescriptor(reader.Read(DescriptorLength), out MollyShareDescriptor? parsed))
        {
            return false;
        }

        byte[] parsedSignature = reader.Read(SignatureLength).ToArray();
        byte[][] values = new byte[objectLengths.Length][];

        for (int i = 0; i < values.Length; i++)
        {
            if (reader.ReadUInt32() != objectLengths[i])
            {
                return false;
            }

            ReadOnlySpan<byte> value = reader.Read(objectLengths[i]);

            if (i < values.Length - 1 && !CryptographicOperations.FixedTimeEquals(SHA512.HashData(value), parsed.ObjectHashes[i]))
            {
                return false;
            }

            values[i] = value.ToArray();
        }

        Debug.Assert(reader.Remaining == 0);

        (descriptor, signature, objects) = (parsed, parsedSignature, values);
        return true;
    }

    public static bool TryParseStart(ReadOnlySpan<byte> body, [NotNullWhen(true)] out MollyShareStartRequest? request)
    {
        request = null;

        if (body.Length != StartBodyLength)
        {
            return false;
        }

        var reader = new Reader(body);

        if (!TryParseEntry(reader.Read(EntryLength), out byte[]? entry) || reader.ReadUInt32() != ManifestSencLength)
        {
            return false;
        }

        request = new MollyShareStartRequest(entry, reader.Read(ManifestSencLength).ToArray());
        return true;
    }

    public static bool TryParseApprove(ReadOnlySpan<byte> body, [NotNullWhen(true)] out MollyShareApproveRequest? request)
    {
        request = null;

        if (body.Length != ApproveBodyLength)
        {
            return false;
        }

        var reader = new Reader(body);

        if (!TryParseEntry(reader.Read(EntryLength), out byte[]? entry))
        {
            return false;
        }

        long startedAt = reader.ReadInt64();

        if (reader.ReadUInt32() != ShareTSencLength)
        {
            return false;
        }

        byte[] shareTSenc = reader.Read(ShareTSencLength).ToArray();
        request = new MollyShareApproveRequest(entry, startedAt, shareTSenc, reader.Read(SignatureLength).ToArray());
        return true;
    }

    /// <summary>
    /// A decrypted TRUSTEE_MANIFEST for <paramref name="descriptor"/>'s entry: the policy, slot indices,
    /// pinned sealed lengths, zero padding, unused zero slots, and trustees that are pairwise distinct and
    /// differ from A and B.
    /// </summary>
    public static bool TryParseManifest(ReadOnlySpan<byte> manifest, MollyShareDescriptor descriptor, [NotNullWhen(true)] out MollyShareManifest? result)
    {
        result = null;

        if (manifest.Length != ManifestLength || manifest[0] != Version)
        {
            return false;
        }

        var reader = new Reader(manifest.Slice(1));

        if (!reader.Read(EntryLength).SequenceEqual(descriptor.Entry))
        {
            return false;
        }

        int threshold = reader.ReadByte();
        int count = reader.ReadByte();

        if (threshold < 1 || threshold > count || count > Capacity)
        {
            return false;
        }

        var slots = new MollyShareTrusteeSlot[count];

        for (int i = 0; i < Capacity; i++)
        {
            ReadOnlySpan<byte> slot = reader.Read(SlotLength);

            if (i >= count)
            {
                if (slot.ContainsAnyExcept((byte)0))
                {
                    return false;
                }

                continue;
            }

            var slotReader = new Reader(slot);
            byte[] aci = slotReader.Read(AciLength).ToArray();
            byte[] key = slotReader.Read(IdentityKeyLength).ToArray();

            if (!IsIdentityKey(key) || slotReader.ReadByte() != i + 1 || slotReader.ReadUInt16() != SealedLength)
            {
                return false;
            }

            byte[] sealedValue = slotReader.Read(SealedLength).ToArray();

            if (slotReader.Read(SealedAreaLength - SealedLength).ContainsAnyExcept((byte)0))
            {
                return false;
            }

            byte[] signature = slotReader.Read(SignatureLength).ToArray();

            if (IsParty(aci, key, descriptor.OwnerAci, descriptor.OwnerKey) || IsParty(aci, key, descriptor.RecipientAci, descriptor.RecipientKey))
            {
                return false;
            }

            for (int j = 0; j < i; j++)
            {
                if (IsParty(aci, key, slots[j].Aci, slots[j].Key))
                {
                    return false;
                }
            }

            slots[i] = new MollyShareTrusteeSlot(i, aci, key, sealedValue, signature);
        }

        Debug.Assert(reader.Remaining == 0);

        result = new MollyShareManifest(threshold, slots);
        return true;
    }

    public static bool TryParseShareT(ReadOnlySpan<byte> encoded, [NotNullWhen(true)] out MollyShareT? share)
    {
        share = null;

        if (encoded.Length != ShareTLength || encoded[0] != Version)
        {
            return false;
        }

        var reader = new Reader(encoded.Slice(1));
        byte[] value = reader.Read(ShareLength).ToArray();
        byte[] entry = reader.Read(EntryLength).ToArray();
        byte[] aci = reader.Read(AciLength).ToArray();
        byte[] key = reader.Read(IdentityKeyLength).ToArray();
        byte index = reader.ReadByte();
        int threshold = reader.ReadByte();
        int count = reader.ReadByte();

        if (!IsIdentityKey(key) || index == 0 || index > count || threshold < 1 || threshold > count || count > Capacity)
        {
            CryptographicOperations.ZeroMemory(value);
            return false;
        }

        share = new MollyShareT(value, entry, aci, key, index, threshold, count);
        return true;
    }

    public static bool IsIdentityKey(ReadOnlySpan<byte> key) => key.Length == IdentityKeyLength && key[0] == IdentityKeyType;

    /// <summary>Whether either the ACI or the key matches, which is enough for two parties to be considered the same.</summary>
    private static bool IsParty(ReadOnlySpan<byte> aci, ReadOnlySpan<byte> key, ReadOnlySpan<byte> otherAci, ReadOnlySpan<byte> otherKey) =>
        aci.SequenceEqual(otherAci) || key.SequenceEqual(otherKey);

    /// <summary>Sequential reads from a buffer whose total length was already validated.</summary>
    private ref struct Reader
    {
        private ReadOnlySpan<byte> _remaining;

        public Reader(ReadOnlySpan<byte> buffer) => _remaining = buffer;

        public readonly int Remaining => _remaining.Length;

        public ReadOnlySpan<byte> Read(int length)
        {
            ReadOnlySpan<byte> value = _remaining.Slice(0, length);
            _remaining = _remaining.Slice(length);
            return value;
        }

        public byte ReadByte() => Read(1)[0];

        public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(Read(sizeof(ushort)));

        public uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(Read(sizeof(uint)));

        public long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(Read(sizeof(long)));
    }
}
