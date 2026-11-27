using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using MihuBot.Configuration;
using MihuBot.Molly.Attestation;

#nullable enable

namespace MihuBot.Molly.Provisioning;

/// <summary>
/// Issues and redeems registration tokens (Provisioning/README.md). Issued tokens aren't stored: they're sealed records of
/// their id and issuance time (see <see cref="Protect"/>). The ids of redeemed tokens and the issuance counts for
/// devices are only kept in memory, so after a restart a token redeemed within its lifetime could be redeemed again,
/// and the limits start over.
/// </summary>
/// <remarks>
/// Sealed by <see cref="MollySealedIdTokens"/> under a key derived from the database key, so tokens survive restarts.
/// Encoded as unpadded base64url so they fit into URLs and QR codes.
/// </remarks>
public sealed class MollyRegistrationTokens
{
    /// <summary>How long a registration token can be redeemed for.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);

    /// <summary>How many registration tokens one device may request per 24 hours.</summary>
    public const int MaxPerDevicePerDay = 10;

    /// <summary>How many registration tokens all devices together may request per 24 hours. The dashboard isn't limited.</summary>
    public const int MaxForAllDevicesPerDay = 100;

    // Kept from when this was a separate protector type, so tokens issued before stay valid.
    private static ReadOnlySpan<byte> DerivationInfo => "MihuBot.Molly.MollyRegistrationTokenProtector.v1"u8;

    private readonly MollySealedIdTokens _tokens;
    private readonly ILogger _logger;

    /// <summary>Null in tests. Holds the admin switch that stops devices from requesting registration tokens.</summary>
    private readonly ServiceConfiguration? _serviceConfiguration;

    /// <summary>Redeemed tokens by id, kept until they expire, after which they can't be redeemed anyway.</summary>
    private readonly ConcurrentDictionary<Guid, Redemption> _redeemed = new();

    /// <summary>When tokens were issued to each device, and to all devices, in the last day. Guarded by the lock.</summary>
    private readonly Lock _issuanceLock = new();
    private readonly Dictionary<Guid, Queue<DateTime>> _issuedByDevice = [];
    private readonly Queue<DateTime> _issuedToDevices = new();

    public MollyRegistrationTokens(ReadOnlySpan<byte> databaseKey, ILogger logger, ServiceConfiguration? serviceConfiguration)
    {
        _tokens = MollySealedIdTokens.CreateFromDatabaseKey(databaseKey, DerivationInfo, urlSafe: true);
        _logger = logger;
        _serviceConfiguration = serviceConfiguration;
    }

    /// <summary>A token being redeemed: its id, until when it can be redeemed, and the key hash it registers.</summary>
    public readonly record struct Redemption(Guid Id, DateTime ExpiresAt, byte[] DerivedHash);

    /// <summary>Issues a token from the dashboard, which isn't limited.</summary>
    public MollyRegistrationToken IssueForDashboard()
    {
        _logger.LogInformation("Issued a Molly registration token from the dashboard");

        return Issue();
    }

    /// <summary>
    /// Issues a token on behalf of a device, or returns null while <see cref="ServiceConfiguration.DisableMollyDeviceRegistrationTokens"/>
    /// is set or the device or all devices reached their limit (<see cref="MaxPerDevicePerDay"/>, <see cref="MaxForAllDevicesPerDay"/>).
    /// </summary>
    public MollyRegistrationToken? TryIssueForDevice(Guid entryId)
    {
        if (_serviceConfiguration?.DisableMollyDeviceRegistrationTokens == true)
        {
            return null;
        }

        if (!TryReserveForDevice(entryId))
        {
            _logger.LogWarning("A Molly device hit the registration token limit");
            return null;
        }

        _logger.LogInformation("Issued a Molly registration token to a device");

        return Issue();
    }

    private MollyRegistrationToken Issue()
    {
        DateTime now = DateTime.UtcNow;

        return new MollyRegistrationToken(Protect(Guid.NewGuid(), now), now + Lifetime);
    }

    /// <summary>Returns <c>base64url(nonce || encrypted (id || issuedAt ticks) || tag)</c>.</summary>
    internal string Protect(Guid id, DateTime issuedAt) => _tokens.Protect(id, issuedAt);

    /// <summary>Counts a token towards the issuance limits if neither is reached.</summary>
    private bool TryReserveForDevice(Guid entryId)
    {
        DateTime now = DateTime.UtcNow;

        lock (_issuanceLock)
        {
            PruneIssuance(now);

            if (!_issuedByDevice.TryGetValue(entryId, out Queue<DateTime>? issued))
            {
                _issuedByDevice[entryId] = issued = new Queue<DateTime>();
            }

            if (issued.Count >= MaxPerDevicePerDay || _issuedToDevices.Count >= MaxForAllDevicesPerDay)
            {
                return false;
            }

            issued.Enqueue(now);
            _issuedToDevices.Enqueue(now);
            return true;
        }
    }

    /// <summary>
    /// Checks that <paramref name="token"/> is a valid, unexpired token that hasn't been redeemed. Returns null and the
    /// <paramref name="redemption"/> to pass to <see cref="TryRedeem"/> if so, otherwise the login result to reject with.
    /// </summary>
    public MollyLoginResult? CheckRedeemable(string? token, byte[] derivedHash, out Redemption redemption)
    {
        redemption = default;

        if (!_tokens.TryUnprotect(token, out Guid id, out DateTime issuedAt))
        {
            return MollyLoginResult.Unregistered;
        }

        DateTime expiresAt = issuedAt + Lifetime;

        if (expiresAt <= DateTime.UtcNow)
        {
            return MollyLoginResult.Unregistered;
        }

        if (_redeemed.TryGetValue(id, out Redemption redeemed))
        {
            return Reject(redeemed, derivedHash);
        }

        redemption = new Redemption(id, expiresAt, derivedHash);
        return null;
    }

    /// <summary>
    /// Marks the token as redeemed. Returns null if it wasn't already, otherwise the login result to reject with (a
    /// concurrent login redeemed it first). Pass it to <see cref="Release"/> if the registration then fails.
    /// </summary>
    public MollyLoginResult? TryRedeem(Redemption redemption) =>
        _redeemed.TryAdd(redemption.Id, redemption)
            ? null
            : Reject(_redeemed.GetValueOrDefault(redemption.Id, redemption), redemption.DerivedHash);

    /// <summary>Makes the token redeemable again: it's only spent if the entry was actually registered.</summary>
    public void Release(Redemption redemption) => _redeemed.TryRemove(redemption.Id, out _);

    /// <summary>
    /// A redeemed token is only refused for good for other key hashes. With the same one, it's a retry of a registration
    /// that may still be in progress (e.g. the client timed out): <c>invalid</c>, so the retry logs in once it's done.
    /// </summary>
    private static MollyLoginResult Reject(Redemption redeemed, byte[] derivedHash) =>
        CryptographicOperations.FixedTimeEquals(redeemed.DerivedHash, derivedHash)
            ? MollyLoginResult.Failed(MollyAttestationFailure.ConcurrentLogin)
            : MollyLoginResult.Unregistered;

    /// <summary>
    /// Forgets redeemed tokens once they've expired, as they can't be redeemed anymore anyway, and issuance times that
    /// no longer count towards the limits.
    /// </summary>
    public void Prune()
    {
        DateTime now = DateTime.UtcNow;

        lock (_issuanceLock)
        {
            PruneIssuance(now);
        }

        foreach ((Guid id, Redemption redeemed) in _redeemed)
        {
            if (redeemed.ExpiresAt <= now)
            {
                _redeemed.TryRemove(new KeyValuePair<Guid, Redemption>(id, redeemed));
            }
        }
    }

    /// <summary>Drops issuance times older than a day, and devices with none left. The caller holds the lock.</summary>
    private void PruneIssuance(DateTime now)
    {
        DateTime windowStart = now - TimeSpan.FromDays(1);

        while (_issuedToDevices.TryPeek(out DateTime oldest) && oldest <= windowStart)
        {
            _issuedToDevices.Dequeue();
        }

        foreach ((Guid entryId, Queue<DateTime> issued) in _issuedByDevice)
        {
            while (issued.TryPeek(out DateTime oldest) && oldest <= windowStart)
            {
                issued.Dequeue();
            }

            if (issued.Count == 0)
            {
                _issuedByDevice.Remove(entryId);
            }
        }
    }
}
