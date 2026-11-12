using System.Security.Cryptography;
using MihuBot.Molly.Api;

#nullable enable

namespace MihuBot.Molly;

/// <summary>Independent, memory-only transport keys. Static keys authenticate discovery only.</summary>
internal sealed class MollyTransportKeyRing : IDisposable
{
    internal static readonly TimeSpan RotationInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan KeyValidity = TimeSpan.FromMinutes(30);

    internal static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RotationThreshold = KeyValidity - RotationInterval;
    private static readonly TimeSpan RetirementThreshold = -TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time;
    private readonly ITimer _maintenanceTimer;
    private readonly Lock _maintenanceLock = new();
    private KeySnapshot? _snapshot;

    public MollyTransportKeyRing(IEnumerable<byte[]> identityPrivateKeys, TimeProvider time)
    {
        _time = time;
        TransportKey[] identities = identityPrivateKeys.Select(key => TransportKey.Import(key)).ToArray();
        ArgumentOutOfRangeException.ThrowIfZero(identities.Length);

        TransportKey current = TransportKey.Generate(time);
        _snapshot = new KeySnapshot(identities, new() { [current.KeyId] = current }, current, CreateDescriptor(current, identities[^1]));
        _maintenanceTimer = time.CreateTimer(_ => UpdateSnapshot(), null, MaintenanceInterval, MaintenanceInterval);
    }

    public MollyTransportKeyResponse GetCurrentKey() => ReadSnapshot().Descriptor;

    public MollyTransportKeyResponse? GetKeyUpdate(MollyPublicKeyId recipientKeyId)
    {
        KeySnapshot snapshot = ReadSnapshot();
        if (recipientKeyId == snapshot.Current.KeyId)
        {
            return null;
        }

        foreach (TransportKey identity in snapshot.Identities)
        {
            if (recipientKeyId == identity.KeyId)
            {
                return null;
            }
        }

        return snapshot.Descriptor;
    }

    private static MollyTransportKeyResponse CreateDescriptor(TransportKey current, TransportKey identity)
    {
        return new MollyTransportKeyResponse
        {
            PublicKey = Convert.ToBase64String(current.PublicKey),
            ExpiresAt = current.ExpiresAt,
            DiscoveryPublicKey = Convert.ToBase64String(identity.PublicKey),
        };
    }

    /// <param name="createdAt">When the key was generated; <see cref="DateTimeOffset.MinValue"/> for identity keys.</param>
    public bool TryDeriveSharedSecret(MollyPublicKeyId recipientKeyId, ReadOnlySpan<byte> ephemeralPublicKey, Span<byte> shared, out bool bootstrap, out DateTimeOffset createdAt)
    {
        TransportKey? key = TryGetKey(recipientKeyId, out bootstrap);
        if (key is null)
        {
            createdAt = default;
            return false;
        }

        createdAt = key.CreatedAt;
        key.DeriveRawSecretAgreement(ephemeralPublicKey, shared);
        return true;
    }

    internal TransportKey? TryGetKey(MollyPublicKeyId recipientKeyId, out bool bootstrap)
    {
        KeySnapshot snapshot = ReadSnapshot();
        foreach (TransportKey identity in snapshot.Identities)
        {
            if (recipientKeyId == identity.KeyId)
            {
                bootstrap = true;
                return identity;
            }
        }

        bootstrap = false;

        return snapshot.Keys.GetValueOrDefault(recipientKeyId);
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

            TimeSpan RemainingValidity(TransportKey key) => DateTimeOffset.FromUnixTimeSeconds(key.ExpiresAt) - now;

            Dictionary<MollyPublicKeyId, TransportKey> retained = snapshot.Keys
                .Where(pair => RemainingValidity(pair.Value) > RetirementThreshold)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            TransportKey current = snapshot.Current;
            MollyTransportKeyResponse descriptor = snapshot.Descriptor;

            if (retained.Count == 0 || RemainingValidity(current) <= RotationThreshold)
            {
                current = TransportKey.Generate(_time);
                retained.Add(current.KeyId, current);
                descriptor = CreateDescriptor(current, snapshot.Identities[^1]);
            }

            Volatile.Write(ref _snapshot, new KeySnapshot(snapshot.Identities, retained, current, descriptor));
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

    private sealed record KeySnapshot(TransportKey[] Identities, Dictionary<MollyPublicKeyId, TransportKey> Keys, TransportKey Current, MollyTransportKeyResponse Descriptor);

    internal sealed class TransportKey
    {
        private readonly byte[] _privateKey;
        public readonly byte[] PublicKey;
        public readonly MollyPublicKeyId KeyId;
        public readonly DateTimeOffset CreatedAt;
        public readonly long ExpiresAt;

        public static TransportKey Import(ReadOnlySpan<byte> privateKey)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(privateKey.Length, X25519DiffieHellman.PrivateKeySizeInBytes);
            return new TransportKey(privateKey.ToArray(), createdAt: DateTimeOffset.MinValue, expiresAt: DateTimeOffset.MaxValue);
        }

        public static TransportKey Generate(TimeProvider time)
        {
            DateTimeOffset now = time.GetUtcNow();
            return new(RandomNumberGenerator.GetBytes(X25519DiffieHellman.PrivateKeySizeInBytes), now, now + KeyValidity);
        }

        private TransportKey(byte[] privateKey, DateTimeOffset createdAt, DateTimeOffset expiresAt)
        {
            _privateKey = privateKey;
            using var key = X25519DiffieHellman.ImportPrivateKey(_privateKey);
            PublicKey = key.ExportPublicKey();
            KeyId = new MollyPublicKeyId(SHA512.HashData(PublicKey).AsSpan(0, MollyRequestProtector.RecipientKeyIdLength));
            CreatedAt = createdAt;
            ExpiresAt = expiresAt.ToUnixTimeSeconds();
        }

        public void DeriveRawSecretAgreement(ReadOnlySpan<byte> ephemeralPublicKey, Span<byte> shared)
        {
            using var key = X25519DiffieHellman.ImportPrivateKey(_privateKey);
            key.DeriveRawSecretAgreement(ephemeralPublicKey, shared);
        }
    }
}
