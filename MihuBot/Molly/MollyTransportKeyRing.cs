using System.Security.Cryptography;
using MihuBot.Molly.Api;

#nullable enable

namespace MihuBot.Molly;

/// <summary>Independent, memory-only transport keys. The static key authenticates discovery only.</summary>
internal sealed class MollyTransportKeyRing : IDisposable
{
    internal static readonly TimeSpan RotationInterval = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan RotationGrace = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan KeyValidity = RotationInterval + (RotationGrace / 2);

    internal static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RotationThreshold = KeyValidity - RotationInterval;
    private static readonly TimeSpan RetirementThreshold = RotationThreshold - RotationGrace;

    private readonly TimeProvider _time;
    private readonly ITimer _maintenanceTimer;
    private readonly Lock _maintenanceLock = new();
    private KeySnapshot? _snapshot;

    public MollyTransportKeyRing(ReadOnlySpan<byte> identityPrivateKey, TimeProvider time)
    {
        _time = time;
        TransportKey identity = TransportKey.Import(identityPrivateKey);
        _snapshot = new KeySnapshot(identity, [TransportKey.Generate(time)]);
        _maintenanceTimer = time.CreateTimer(_ => UpdateSnapshot(), null, MaintenanceInterval, MaintenanceInterval);
    }

    public MollyTransportKeyResponse GetCurrentKey() => ReadSnapshot().Keys[^1].Descriptor;

    public bool TryDeriveSharedSecret(ReadOnlySpan<byte> recipientKeyId, ReadOnlySpan<byte> ephemeralPublicKey,
        Span<byte> shared, out bool bootstrap)
    {
        TransportKey? key = TryGetKey(recipientKeyId, out bootstrap);
        if (key is null)
        {
            return false;
        }

        key.DeriveRawSecretAgreement(ephemeralPublicKey, shared);
        return true;
    }

    internal TransportKey? TryGetKey(ReadOnlySpan<byte> recipientKeyId, out bool bootstrap)
    {
        KeySnapshot snapshot = ReadSnapshot();
        bootstrap = CryptographicOperations.FixedTimeEquals(recipientKeyId, snapshot.Identity.KeyId);
        if (bootstrap)
        {
            return snapshot.Identity;
        }

        foreach (TransportKey key in snapshot.Keys)
        {
            if (CryptographicOperations.FixedTimeEquals(recipientKeyId, key.KeyId))
            {
                return key;
            }
        }

        return null;
    }

    private KeySnapshot ReadSnapshot() =>
        Volatile.Read(ref _snapshot) ?? throw new ObjectDisposedException(nameof(MollyTransportKeyRing));

    private void UpdateSnapshot()
    {
        lock (_maintenanceLock)
        {
            if (_snapshot is not { } snapshot)
            {
                return;
            }

            DateTimeOffset now = _time.GetUtcNow();

            TimeSpan RemainingValidity(TransportKey key) => DateTimeOffset.FromUnixTimeSeconds(key.Descriptor.ExpiresAt) - now;

            List<TransportKey> retained = snapshot.Keys.Where(key => RemainingValidity(key) > RetirementThreshold).ToList();

            if (retained.Count == 0 || RemainingValidity(retained[^1]) <= RotationThreshold)
            {
                retained.Add(TransportKey.Generate(_time));
            }

            Volatile.Write(ref _snapshot, new KeySnapshot(snapshot.Identity, [.. retained]));
        }
    }

    public void Dispose()
    {
        lock (_maintenanceLock)
        {
            if (Interlocked.Exchange(ref _snapshot, null) is null)
            {
                return;
            }

            _maintenanceTimer.Dispose();
        }
    }

    private sealed record KeySnapshot(TransportKey Identity, TransportKey[] Keys);

    internal sealed class TransportKey
    {
        private readonly byte[] _privateKey;
        public readonly byte[] PublicKey;
        public readonly byte[] KeyId;
        public readonly MollyTransportKeyResponse Descriptor;

        public static TransportKey Import(ReadOnlySpan<byte> privateKey)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(privateKey.Length, X25519DiffieHellman.PrivateKeySizeInBytes);
            return new TransportKey(privateKey.ToArray(), expiresAt: DateTimeOffset.MaxValue);
        }

        public static TransportKey Generate(TimeProvider time) => new(RandomNumberGenerator.GetBytes(X25519DiffieHellman.PrivateKeySizeInBytes), time.GetUtcNow() + KeyValidity);

        private TransportKey(byte[] privateKey, DateTimeOffset expiresAt)
        {
            _privateKey = privateKey;
            using var key = X25519DiffieHellman.ImportPrivateKey(_privateKey);
            PublicKey = key.ExportPublicKey();
            KeyId = SHA512.HashData(PublicKey).AsSpan(0, MollyRequestProtector.RecipientKeyIdLength).ToArray();
            Descriptor = new MollyTransportKeyResponse
            {
                PublicKey = Convert.ToBase64String(PublicKey),
                ExpiresAt = expiresAt.ToUnixTimeSeconds(),
            };
        }

        public void DeriveRawSecretAgreement(ReadOnlySpan<byte> ephemeralPublicKey, Span<byte> shared)
        {
            using var key = X25519DiffieHellman.ImportPrivateKey(_privateKey);
            key.DeriveRawSecretAgreement(ephemeralPublicKey, shared);
        }
    }
}
