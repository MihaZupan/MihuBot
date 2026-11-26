using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Communication.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MihuBot.Configuration;
using MihuBot.DB;
using MihuBot.Helpers.Crypto;
using MihuBot.Molly.Alerts;
using MihuBot.Molly.Api;
using MihuBot.Molly.Attestation;
using MihuBot.Molly.Provisioning;
using MihuBot.Molly.Sharing;

#nullable enable

namespace MihuBot.Molly;

/// <summary>
/// Backend for the remote lockout support in the Molly Android app.
/// </summary>
/// <remarks>
/// The server never sees the user's key, only a hash of it. That hash is mixed with the static database
/// key (<c>HMAC-SHA512(keyHash, databaseKey)</c>) before it's stored or compared, so a database leak alone
/// can't be used to brute-force weak client keys.
/// </remarks>
public sealed class MollyService
{
    /// <summary>Nicknames are limited by their encoded size, not their character count.</summary>
    public const int MaxNicknameLengthInBytes = 64;

    public const int MaxAppVersionLength = 32;

    private static readonly SearchValues<char> s_appVersionChars = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-");

    /// <summary>Entries that never completed an association are dropped after this long.</summary>
    private static readonly TimeSpan UnassociatedEntryRetention = TimeSpan.FromDays(2);

    /// <summary>Entries that haven't checked in for this long are locked automatically.</summary>
    private static readonly TimeSpan InactivityLockThreshold = TimeSpan.FromDays(7);

    /// <summary>Alerts are small status uploads, so anything larger is a mistake or abuse.</summary>
    public const int MaxAlertLength = 1024;

    /// <summary>Only the newest alerts are kept per device.</summary>
    private const int MaxAlertsPerEntry = 100;

    private static readonly TimeSpan StatusAlertRetention = TimeSpan.FromDays(2);

    /// <summary>An entry bound to an identity can't be rebound to a different one for this long.</summary>
    public static readonly TimeSpan IdentityRebindCooldown = TimeSpan.FromHours(24);

    /// <summary>The admin dashboard, linked from alert emails.</summary>
    private const string DashboardUrl = "https://mihubot.xyz/molly";

    private const int ServerHmacLength = 64;
    private const int IdLength = 16; // Guid
    private const int MinKeyHashLength = 32;
    private const int MaxKeyHashLength = 256;

    /// <summary>
    /// Nicknames are padded to a fixed size before they're encrypted. GCM is a stream cipher, so
    /// without this the stored length would give away the length of the nickname.
    /// </summary>
    private const int PaddedNicknameLength = sizeof(ushort) + MaxNicknameLengthInBytes;

    private readonly IDbContextFactory<MollyDbContext> _db;
    private readonly ILogger<MollyService> _logger;

    /// <summary>
    /// Null in tests, which have no Discord connection. Without it, alerts are still stored and shown
    /// on the dashboard, they just aren't announced, and maintenance has to be driven manually.
    /// </summary>
    private readonly Logger? _discordLogger;

    /// <summary>Null in tests. Without it the alert email recipients can't be resolved.</summary>
    private readonly IConfigurationService? _runtimeConfiguration;

    /// <summary>The sender the alert emails go out as, and the client to send them with.</summary>
    private readonly EmailClient? _emailClient;
    private readonly string? _emailFrom;

    /// <summary>Null in tests. Encrypts alert bodies for Proton recipients so Azure can't read them.</summary>
    private readonly ProtonMailEncryptor? _protonEncryptor;

    /// <summary>One alert email every 30 minutes, across all devices.</summary>
    private readonly SimpleRateLimiter _emailRateLimiter = new(TimeSpan.FromMinutes(30), maxTolerance: 1);

    private readonly MollyIdProtector _idProtector;
    private readonly MollyAttestationService _attestation;
    private readonly byte[] _databaseKey;

    /// <summary>How long after a login its session can still move the entry to a new key hash (a passphrase change).</summary>
    public static readonly TimeSpan RekeyWindow = TimeSpan.FromMinutes(10);

    public MollyRegistrationTokens RegistrationTokens { get; }

    public MollyService(IDbContextFactory<MollyDbContext> db, ILogger<MollyService> logger, MollyIdProtector idProtector, MollyAttestationService attestation, IConfiguration configuration, Logger? discordLogger, IConfigurationService? runtimeConfiguration = null, ProtonMailEncryptor? protonEncryptor = null, ServiceConfiguration? serviceConfiguration = null)
    {
        _db = db;
        _logger = logger;
        _discordLogger = discordLogger;
        _runtimeConfiguration = runtimeConfiguration;
        _idProtector = idProtector;
        _attestation = attestation;

        if (configuration.IsConfigured(OptionalFeatures.MollyAlertEmail))
        {
            _emailClient = new EmailClient(configuration[OptionalFeatures.MollyAlertEmailConnectionStringName]);
            _emailFrom = configuration[OptionalFeatures.MollyAlertEmailFromName];
            _protonEncryptor = protonEncryptor;
        }

        // Trusted configuration, so a malformed value can just throw on startup.
        _databaseKey = Convert.FromBase64String(configuration[OptionalFeatures.MollyDatabaseKeyName]!);

        ArgumentOutOfRangeException.ThrowIfLessThan(_databaseKey.Length, MinKeyHashLength, OptionalFeatures.MollyDatabaseKeyName);

        RegistrationTokens = new MollyRegistrationTokens(_databaseKey, logger, serviceConfiguration);

        if (discordLogger is not null)
        {
            PeriodicTask.Start("MollyMaintenance",
                new PeriodicTaskOptions { Interval = TimeSpan.FromHours(1), RunImmediately = true },
                discordLogger, RunMaintenanceAsync);
        }
    }

    /// <summary>
    /// Drops entries that registered but never associated a nickname within
    /// <see cref="UnassociatedEntryRetention"/>, so that abandoned or probing registrations don't
    /// accumulate (and don't keep slowing down the constant-time scan on login).
    /// Runs periodically in the background.
    /// </summary>
    public async Task DeleteUnassociatedEntriesAsync(CancellationToken cancellationToken = default)
    {
        // The table doesn't exist until migrations have run.
        await DatabaseSetupHelper.MigrationsCompleted;

        DateTime cutoff = DateTime.UtcNow - UnassociatedEntryRetention;

        await using MollyDbContext db = _db.CreateDbContext();

        int deleted = await db.Entries
            .Where(e => e.EncryptedNickname == null && e.LastSeenAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            _logger.LogInformation("Deleted {Count} Molly entries that never associated a nickname", deleted);
        }
    }

    /// <summary>
    /// Locks entries that haven't checked in for <see cref="InactivityLockThreshold"/>. A device that
    /// stopped talking to the server may well be lost, so it has to be unlocked deliberately before it
    /// can log in again. Runs periodically in the background.
    /// </summary>
    public async Task LockInactiveEntriesAsync(CancellationToken cancellationToken = default)
    {
        // The table doesn't exist until migrations have run.
        await DatabaseSetupHelper.MigrationsCompleted;

        DateTime cutoff = DateTime.UtcNow - InactivityLockThreshold;

        await using MollyDbContext db = _db.CreateDbContext();

        int locked = await db.Entries
            .Where(e => !e.LockRequested && e.LastSeenAt < cutoff)
            .ExecuteUpdateAsync(e => e.SetProperty(entry => entry.LockRequested, true), cancellationToken);

        if (locked > 0)
        {
            _logger.LogInformation("Locked {Count} Molly entries that haven't been seen in {Days} days",
                locked, InactivityLockThreshold.TotalDays);
        }
    }

    /// <summary>
    /// Records an arbitrary small payload uploaded by a device, e.g. its location.
    /// </summary>
    /// <remarks>
    /// Alerts are stored even when the entry is locked or wiped - a device reporting where it is
    /// after being locked is exactly what the feature is for - and the pending command still comes back.
    /// </remarks>
    /// <param name="protectedId">The token handed out by <see cref="LoginAsync"/>.</param>
    /// <param name="payload">The raw JSON body the device sent.</param>
    public async Task<MollyCommandResult> SubmitAlertAsync(string? protectedId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.IsEmpty || payload.Length > MaxAlertLength)
        {
            return MollyCommandResult.Invalid;
        }

        await using MollyDbContext db = _db.CreateDbContext();

        Session session = await OpenSessionAsync(db, protectedId, stopOnBlockingCommand: false, cancellationToken);

        if (!session.IsOpen)
        {
            return session.Rejection;
        }

        MollyDbEntry entry = session.Entry;

        // The id is the caller's session token rather than part of the alert, and the row is already
        // linked to the entry, so it isn't worth storing.
        byte[] stored = StripId(payload.Span);

        db.Alerts.Add(new MollyAlertDbEntry
        {
            EntryId = entry.Id,
            CreatedAt = DateTime.UtcNow,
            EncryptedPayload = Encrypt(entry.Id, stored, EncryptedField.Alert),
        });

        entry.LastSeenAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        HandleAlertPayload(entry, stored);

        return session.Command.IsBlocking()
            ? MollyCommandResult.Blocked(session.Command)
            : MollyCommandResult.Ok(session.Command);
    }

    /// <summary>
    /// Removes the <c>id</c> property from an alert body. Everything else is left exactly as sent,
    /// so a payload the server doesn't understand still round-trips to the dashboard.
    /// </summary>
    private static byte[] StripId(ReadOnlySpan<byte> payload)
    {
        try
        {
            if (JsonNode.Parse(payload) is JsonObject json && json.Remove("id"))
            {
                return Encoding.UTF8.GetBytes(json.ToJsonString());
            }
        }
        catch (JsonException)
        {
            // Alerts are arbitrary data, so anything unparseable is stored as it arrived.
        }

        return payload.ToArray();
    }

    /// <summary>
    /// Reacts to the alert types the server knows about, after the alert has already been stored.
    /// Adding support for a new type means adding a case here and a data class under Molly/Alerts.
    /// </summary>
    private void HandleAlertPayload(MollyDbEntry entry, byte[] payload)
    {
        MollyAlertEnvelope? envelope = MollyAlertEnvelope.TryParse(payload);

        if (envelope is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                switch (envelope.AlertType)
                {
                    case MollyAlertType.Location:
                        if (envelope.TryGetData<MollyLocationAlert>() is { IsValid: true } location)
                        {
                            await ReportLocationAlertAsync(entry, location);
                        }
                        break;

                    case MollyAlertType.Status:
                        // Status reports are only reviewed on the dashboard, not announced.
                        break;

                    case MollyAlertType.Unknown:
                    default:
                        // Unrecognized alerts are still stored and shown on the dashboard.
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle a Molly alert");
            }
        });
    }

    /// <summary>Pings Discord for alerts that are worth a human looking at.</summary>
    private async Task ReportLocationAlertAsync(MollyDbEntry entry, MollyLocationAlert location)
    {
        await ReportAlertAsync(entry,
            subject: "Molly location alert",
            emailBody:
                $"""
                # Molly location alert from `{FormatNickname(entry)}`

                {location}

                {location.MapUrl}
                """);
    }

    /// <summary>Percent-escapes a nickname so it can't break out of the Markdown code span it's shown in.</summary>
    private string FormatNickname(MollyDbEntry entry) =>
        Uri.EscapeDataString(TryDecryptNickname(entry)).Replace("%20", " ", StringComparison.Ordinal);

    /// <summary>
    /// Notifies Discord (and email, if configured) about an alert, unless the device has been muted
    /// or there's no Discord connection. Muting only silences the notification - the alert is still
    /// stored and shown on the dashboard.
    /// </summary>
    private async Task ReportAlertAsync(MollyDbEntry entry, string subject, string emailBody)
    {
        if (entry.AlertsMuted)
        {
            return;
        }

        if (_discordLogger is not null)
        {
            await _discordLogger.DebugAsync(subject);
        }

        // The recipients are runtime configuration, so they can be changed without a restart.
        if (_emailClient is not null &&
            _runtimeConfiguration is not null &&
            _runtimeConfiguration.TryGet(null, OptionalFeatures.MollyAlertEmailToName, out string to))
        {
            // Emails are throttled across every device, so a chatty (or hostile) one can't flood the
            // inbox. Discord is still notified about every alert.
            if (!_emailRateLimiter.TryEnter())
            {
                _logger.LogInformation("Skipping the Molly alert email, one was sent too recently");
                return;
            }

            string body =
                $"""
                {emailBody}

                See {DashboardUrl} for the full history.
                """;

            // One message per recipient, so that they don't see who else is being notified.
            foreach (string address in to.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    // Alerts are only ever sent encrypted, so a recipient without a Proton key is
                    // skipped rather than mailed in the clear. Inline PGP can't hide the subject, so
                    // it's kept generic to avoid leaking the device nickname.
                    if (_protonEncryptor is null ||
                        await _protonEncryptor.TryEncryptAsync(address, body, CancellationToken.None) is not { } encryptedBody)
                    {
                        _logger.LogWarning("Skipping the Molly alert email, no Proton key for the recipient");
                        continue;
                    }

                    // Not waiting for delivery - the alert is already stored either way.
                    await _emailClient.SendAsync(WaitUntil.Started, _emailFrom, address, subject, htmlContent: null, plainTextContent: encryptedBody);
                }
                catch (Exception ex)
                {
                    // A mail failure must not fail the device's request, or stop the other recipients.
                    _logger.LogError(ex, "Failed to send a Molly alert email");
                }
            }
        }
    }

    /// <summary>A short description of the alert for the dashboard, if the type is one we understand.</summary>
    private static (MollyAlertType Type, string? Summary, string? MapUrl) DescribeAlert(string payload)
    {
        MollyAlertEnvelope? envelope = MollyAlertEnvelope.TryParse(Encoding.UTF8.GetBytes(payload));

        if (envelope is null)
        {
            return (MollyAlertType.Unknown, null, null);
        }

        switch (envelope.AlertType)
        {
            case MollyAlertType.Location:
                MollyLocationAlert? location = envelope.TryGetData<MollyLocationAlert>();
                return (MollyAlertType.Location, location?.ToString(), location?.MapUrl);

            case MollyAlertType.Status:
                MollyStatusAlert? status = envelope.TryGetData<MollyStatusAlert>();
                return (MollyAlertType.Status, status?.Summary, null);

            case MollyAlertType.Unknown:
            default:
                return (MollyAlertType.Unknown, null, null);
        }
    }

    /// <summary>The most recent alerts across all devices, for the admin dashboard.</summary>
    public async Task<MollyAlertInfo[]> GetRecentAlertsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        await using MollyDbContext db = _db.CreateDbContext();

        var alerts = await db.Alerts
            .OrderByDescending(a => a.Id)
            .Take(limit)
            .Join(db.Entries, a => a.EntryId, e => e.Id, (a, e) => new { Alert = a, Entry = e })
            .ToArrayAsync(cancellationToken);

        return [.. alerts.Select(a =>
        {
            string payload = TryDecryptAlert(a.Alert);
            (MollyAlertType type, string? summary, string? mapUrl) = DescribeAlert(payload);

            return new MollyAlertInfo(
                a.Alert.Id,
                a.Alert.EntryId,
                TryDecryptNickname(a.Entry),
                a.Alert.CreatedAt,
                type,
                summary,
                mapUrl,
                payload);
        })];
    }

    private string TryDecryptAlert(MollyAlertDbEntry alert)
    {
        try
        {
            return Encoding.UTF8.GetString(Decrypt(alert.EntryId, alert.EncryptedPayload, EncryptedField.Alert));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt a Molly alert");
            return "<unknown>";
        }
    }

    /// <summary>
    /// Keeps only the newest <see cref="MaxAlertsPerEntry"/> alerts per device, so a chatty (or
    /// misbehaving) client can't grow the table without bound. Runs periodically in the background.
    /// </summary>
    public async Task DeleteExcessAlertsAsync(CancellationToken cancellationToken = default)
    {
        // The table doesn't exist until migrations have run.
        await DatabaseSetupHelper.MigrationsCompleted;

        await using MollyDbContext db = _db.CreateDbContext();

        Guid[] noisyEntries = await db.Alerts
            .GroupBy(a => a.EntryId)
            .Where(g => g.Count() > MaxAlertsPerEntry)
            .Select(g => g.Key)
            .ToArrayAsync(cancellationToken);

        int deleted = 0;

        foreach (Guid entryId in noisyEntries)
        {
            // Ids are monotonic, so the newest alerts are simply the highest ones.
            long oldestKeptId = await db.Alerts
                .Where(a => a.EntryId == entryId)
                .OrderByDescending(a => a.Id)
                .Select(a => a.Id)
                .Skip(MaxAlertsPerEntry - 1)
                .FirstAsync(cancellationToken);

            deleted += await db.Alerts
                .Where(a => a.EntryId == entryId && a.Id < oldestKeptId)
                .ExecuteDeleteAsync(cancellationToken);
        }

        if (deleted > 0)
        {
            _logger.LogInformation("Deleted {Count} Molly alerts beyond the per-device limit", deleted);
        }
    }

    /// <summary>Deletes status alerts older than two days, based on their server receipt time.</summary>
    public async Task DeleteExpiredStatusAlertsAsync(CancellationToken cancellationToken = default)
    {
        await DatabaseSetupHelper.MigrationsCompleted;

        DateTime cutoff = DateTime.UtcNow - StatusAlertRetention;
        await using MollyDbContext db = _db.CreateDbContext();

        long lastId = 0;
        int deleted = 0;

        while (true)
        {
            // The type is encrypted inside the payload, so inspect old alerts in bounded batches.
            MollyAlertDbEntry[] alerts = await db.Alerts
                .AsNoTracking()
                .Where(a => a.Id > lastId && a.CreatedAt < cutoff)
                .OrderBy(a => a.Id)
                .Take(1000)
                .ToArrayAsync(cancellationToken);

            if (alerts.Length == 0)
            {
                break;
            }

            long[] expiredIds = alerts
                .Where(a => MollyAlertEnvelope.TryParse(Encoding.UTF8.GetBytes(TryDecryptAlert(a)))?.AlertType == MollyAlertType.Status)
                .Select(a => a.Id)
                .ToArray();

            if (expiredIds.Length > 0)
            {
                deleted += await db.Alerts
                    .Where(a => expiredIds.Contains(a.Id))
                    .ExecuteDeleteAsync(cancellationToken);
            }

            lastId = alerts[^1].Id;
        }

        if (deleted > 0)
        {
            _logger.LogInformation("Deleted {Count} expired Molly status alerts", deleted);
        }
    }

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        await DeleteUnassociatedEntriesAsync(cancellationToken);
        await LockInactiveEntriesAsync(cancellationToken);
        await DeleteExpiredStatusAlertsAsync(cancellationToken);
        await DeleteExcessAlertsAsync(cancellationToken);
        RegistrationTokens.Prune();
    }

    /// <summary>
    /// Releases the entry's server secret to a device that proves, with fresh Android hardware key
    /// attestation, that it is the device the entry was enrolled from (see <see cref="MollyAttestationVerifier"/>).
    /// An unknown key hash registers a new entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pending blocking commands are returned without requiring attestation, except that a lock is answered like
    /// a wrong passphrase (<see cref="MollyLoginResult.Locked"/>). A failed attestation registers,
    /// pins and releases nothing, and isn't recorded as a check-in. An enrolled device whose evidence is
    /// rejected for good (<see cref="MollyAttestationFailureExtensions.IsPermanent"/>) is also locked.
    /// </para>
    /// <para>
    /// There is no lock: verification runs concurrently, and the pinned state is only replaced if it is
    /// still exactly what was verified against, and the entry is neither locked nor wiped. A login that
    /// loses such a race (or a race to register the same key hash) gets
    /// <see cref="MollyAttestationFailure.ConcurrentLogin"/> and retries in a new exchange.
    /// </para>
    /// </remarks>
    /// <param name="exchange">The transport exchange the request arrived in, which the proof must be bound to.</param>
    public async Task<MollyLoginResult> LoginAsync(MollyLoginRequest request, MollyExchangeContext exchange, CancellationToken cancellationToken)
    {
        if (!TryDecodeBase64(request.KeyHash, MinKeyHashLength, MaxKeyHashLength, out byte[] keyHashBytes))
        {
            return MollyLoginResult.Invalid;
        }

        byte[] derivedHash = HMACSHA512.HashData(_databaseKey, keyHashBytes);

        await using MollyDbContext db = _db.CreateDbContext();

        MollyDbEntry? match = await db.Entries.FirstOrDefaultAsync(e => e.DerivedHash == derivedHash || e.PreviousDerivedHash == derivedHash, cancellationToken);

        if (match is null)
        {
            if (request.Id is not null)
            {
                return await RekeyAsync(db, request, keyHashBytes, derivedHash, exchange, cancellationToken);
            }

            return await RegisterWithTokenAsync(db, request, keyHashBytes, derivedHash, exchange, cancellationToken);
        }

        MollyCommand pending = GetPendingCommand(match);

        if (pending.IsBlocking())
        {
            // The device is checking in, and it gets a wipe whether or not it can attest. A lock is answered like a wrong passphrase.
            await MarkSeenAsync(db, match, cancellationToken);
            return pending == MollyCommand.Lock ? MollyLoginResult.Locked() : MollyLoginResult.Blocked(pending);
        }

        MollyAttestationFailure failure = _attestation.TryDecode(request.Attestation, exchange, out MollyAttestationEvidence? evidence);

        if (failure != MollyAttestationFailure.None)
        {
            return MollyLoginResult.Failed(failure);
        }

        PinnedVerification verified = await VerifyPinnedAttestationAsync(db, match, evidence!, keyHashBytes, exchange, lockOnFailure: true, cancellationToken);

        if (verified.Failure is not null)
        {
            return verified.Failure;
        }

        if (match.EncryptedServerHmac is null)
        {
            _logger.LogWarning("A Molly entry is missing its server HMAC");
            return MollyLoginResult.Invalid;
        }

        Guid id = match.Id;
        byte[] currentHash = match.DerivedHash;
        byte[] newState = EncryptAttestationState(id, verified.State!);
        DateTime now = NextLoginTime(match);

        // The hash of the latest login is always the current one: logging in with the other hash of an unconfirmed
        // passphrase change means the device kept (or went back to) it, so the two swap until a session confirms one.
        bool viaPrevious = !CryptographicOperations.FixedTimeEquals(currentHash, derivedHash);
        byte[]? otherHash = viaPrevious ? currentHash : match.PreviousDerivedHash;
        byte[]? previousHash = match.PreviousDerivedHash;
        byte[] pinnedState = verified.PinnedState!;

        // Only succeeds if the entry is still neither locked nor wiped, so that never has to be read back, and if neither
        // hash changed meanwhile (e.g. a session confirming a passphrase change dropped the previous one).
        int updated = await db.Entries
            .Where(e => e.Id == id && e.DerivedHash == currentHash && e.PreviousDerivedHash == previousHash && e.EncryptedAttestationState == pinnedState && !e.LockRequested && !e.WipeRequested)
            .ExecuteUpdateAsync(e => e
                .SetProperty(entry => entry.DerivedHash, derivedHash)
                .SetProperty(entry => entry.PreviousDerivedHash, otherHash)
                .SetProperty(entry => entry.EncryptedAttestationState, newState)
                .SetProperty(entry => entry.LastSeenAt, now)
                .SetProperty(entry => entry.LastLoginAt, now), cancellationToken);

        if (updated == 0)
        {
            // A concurrent login changed the pinned state first, or the entry was locked or wiped
            // meanwhile. Either way the retry sees the current state.
            return MollyLoginResult.Failed(MollyAttestationFailure.ConcurrentLogin);
        }

        byte[] serverHmac = Decrypt(id, match.EncryptedServerHmac, EncryptedField.ServerHmac);

        return MollyLoginResult.Success(_idProtector.Protect(id, now), serverHmac);
    }

    /// <summary>
    /// Ids issued at the previous login must be superseded, even within one clock tick or after the clock stepped back.
    /// </summary>
    private static DateTime NextLoginTime(MollyDbEntry entry)
    {
        DateTime now = DateTime.UtcNow;

        return now <= entry.LastLoginAt
            ? new DateTime(entry.LastLoginAt.Ticks + 1, DateTimeKind.Utc)
            : now;
    }

    /// <summary>The outcome of <see cref="VerifyPinnedAttestationAsync"/>: the verified state, or the result to answer the login with.</summary>
    private sealed record PinnedVerification(MollyAttestationState? State, byte[]? PinnedState, MollyLoginResult? Failure);

    /// <summary>
    /// Verifies a login of an enrolled entry against its pinned attestation state. With <paramref name="lockOnFailure"/>,
    /// evidence rejected for good locks the entry. On success, <see cref="PinnedVerification.PinnedState"/> is the stored
    /// state that was verified against.
    /// </summary>
    private async Task<PinnedVerification> VerifyPinnedAttestationAsync(MollyDbContext db, MollyDbEntry entry, MollyAttestationEvidence evidence, byte[] keyHashBytes, MollyExchangeContext exchange, bool lockOnFailure, CancellationToken cancellationToken)
    {
        byte[]? verifiedState = entry.EncryptedAttestationState;

        if (verifiedState is null || !TryDecryptAttestationState(entry.Id, verifiedState, out MollyAttestationState? pinned))
        {
            // Entries are enrolled when they register, and only a (pending, so handled by the caller) wipe clears that.
            // Never treated as "not enrolled yet" - that would let anyone with the key hash re-enroll.
            return new(null, null, lockOnFailure
                ? await LockAfterAttestationFailureAsync(db, entry.Id, MollyAttestationFailure.UnreadablePinnedState, cancellationToken)
                : MollyLoginResult.Failed(MollyAttestationFailure.UnreadablePinnedState));
        }

        MollyAttestationResult outcome = _attestation.Verify(evidence, keyHashBytes, exchange, pinned);

        if (outcome.Failure != MollyAttestationFailure.None)
        {
            _logger.LogWarning("Molly attestation for an enrolled device was rejected: {Failure}", outcome.Failure);

            return new(null, null, lockOnFailure && outcome.Failure.IsPermanent()
                ? await LockAfterAttestationFailureAsync(db, entry.Id, outcome.Failure, cancellationToken)
                : MollyLoginResult.Failed(outcome.Failure));
        }

        return new(outcome.State!, verifiedState, null);
    }

    /// <summary>
    /// An enrolled device failing attestation for good may mean it was tampered with, so the entry is
    /// locked until an admin unlocks it. Like any login of a locked entry, it's answered like a wrong passphrase.
    /// Like alert notifications, the Discord message doesn't say which device it was, nor why - that's in the server log.
    /// Retryable and temporary failures don't get here.
    /// </summary>
    private async Task<MollyLoginResult> LockAfterAttestationFailureAsync(MollyDbContext db, Guid id, MollyAttestationFailure failure, CancellationToken cancellationToken)
    {
        await db.Entries
            .Where(e => e.Id == id)
            .ExecuteUpdateAsync(e => e.SetProperty(entry => entry.LockRequested, true), cancellationToken);

        _logger.LogWarning("Locked a Molly entry after its device failed attestation: {Failure}", failure);

        if (_discordLogger is not null)
        {
            await _discordLogger.DebugAsync("Molly login rejected: an enrolled device failed attestation and was locked");
        }

        return MollyLoginResult.Locked(failure);
    }

    /// <summary>
    /// Moves the entry of the session in <see cref="MollyLoginRequest.Id"/> to the request's unknown key hash: a
    /// passphrase change (Attestation/README.md). The login has to be attested by the entry's pinned keys, which stay pinned,
    /// and gets the entry's server secret. The previous key hash keeps logging in until the change is confirmed
    /// (<see cref="MollyDbEntry.PreviousDerivedHash"/>), so a lost response can't lock the device out.
    /// </summary>
    private async Task<MollyLoginResult> RekeyAsync(MollyDbContext db, MollyLoginRequest request, byte[] keyHashBytes, byte[] derivedHash, MollyExchangeContext exchange, CancellationToken cancellationToken)
    {
        Session session = await OpenSessionAsync(db, request.Id, stopOnBlockingCommand: true, cancellationToken);

        if (!session.IsOpen)
        {
            return session.Rejection.Status == MollyResultStatus.Command
                ? MollyLoginResult.Blocked(session.Rejection.Command)
                : MollyLoginResult.Invalid;
        }

        MollyDbEntry entry = session.Entry;

        // The session has to come from the entry's latest login, moments ago: proof that the device just logged in with
        // the old passphrase. Session ids alone are kept in the clear on devices, so they mustn't release the secret.
        if (session.IssuedAt.Ticks != entry.LastLoginAt.Ticks || DateTime.UtcNow - entry.LastLoginAt > RekeyWindow)
        {
            return MollyLoginResult.Invalid;
        }

        MollyAttestationFailure failure = _attestation.TryDecode(request.Attestation, exchange, out MollyAttestationEvidence? evidence);

        if (failure != MollyAttestationFailure.None)
        {
            return MollyLoginResult.Failed(failure);
        }

        // Session ids are less secret than key hashes (devices keep them in the clear), so a failed move only gets
        // rejected: locking would let anyone holding a copied id lock the entry. The device's own logins still lock it.
        PinnedVerification verified = await VerifyPinnedAttestationAsync(db, entry, evidence!, keyHashBytes, exchange, lockOnFailure: false, cancellationToken);

        if (verified.Failure is not null)
        {
            return verified.Failure;
        }

        if (entry.EncryptedServerHmac is null)
        {
            _logger.LogWarning("A Molly entry is missing its server HMAC");
            return MollyLoginResult.Invalid;
        }

        Guid id = entry.Id;
        byte[] currentHash = entry.DerivedHash;
        byte[] pinnedState = verified.PinnedState!;
        byte[] newState = EncryptAttestationState(id, verified.State!);
        DateTime now = NextLoginTime(entry);
        int updated;

        try
        {
            updated = await db.Entries
                .Where(e => e.Id == id && e.DerivedHash == currentHash && e.EncryptedAttestationState == pinnedState && !e.LockRequested && !e.WipeRequested)
                .ExecuteUpdateAsync(e => e
                    .SetProperty(x => x.DerivedHash, derivedHash)
                    .SetProperty(x => x.PreviousDerivedHash, currentHash)
                    .SetProperty(x => x.EncryptedAttestationState, newState)
                    .SetProperty(x => x.LastSeenAt, now)
                    .SetProperty(x => x.LastLoginAt, now), cancellationToken);
        }
        catch (DbException)
        {
            // The key hash was registered concurrently. The retry logs in with it like any known key hash.
            return MollyLoginResult.Failed(MollyAttestationFailure.ConcurrentLogin);
        }

        if (updated == 0)
        {
            return MollyLoginResult.Failed(MollyAttestationFailure.ConcurrentLogin);
        }

        _logger.LogInformation("Moved a Molly entry to a new key hash");

        byte[] serverHmac = Decrypt(id, entry.EncryptedServerHmac, EncryptedField.ServerHmac);

        return MollyLoginResult.Success(_idProtector.Protect(id, now), serverHmac);
    }

    /// <summary>
    /// Registers a new entry for the unknown key hash if <see cref="MollyLoginRequest.RegistrationToken"/> is a valid,
    /// unused token (Provisioning/README.md), which a successful registration redeems.
    /// </summary>
    private async Task<MollyLoginResult> RegisterWithTokenAsync(MollyDbContext db, MollyLoginRequest request, byte[] keyHashBytes, byte[] derivedHash, MollyExchangeContext exchange, CancellationToken cancellationToken)
    {
        if (RegistrationTokens.CheckRedeemable(request.RegistrationToken, derivedHash, out MollyRegistrationTokens.Redemption redemption) is { } rejected)
        {
            return rejected;
        }

        MollyAttestationFailure failure = _attestation.TryDecode(request.Attestation, exchange, out MollyAttestationEvidence? evidence);

        if (failure != MollyAttestationFailure.None)
        {
            return MollyLoginResult.Failed(failure);
        }

        MollyAttestationResult outcome = _attestation.Verify(evidence!, keyHashBytes, exchange, pinned: null);

        if (outcome.Failure != MollyAttestationFailure.None)
        {
            _logger.LogWarning("Molly attestation for a new registration was rejected: {Failure}", outcome.Failure);
            return MollyLoginResult.Failed(outcome.Failure);
        }

        if (RegistrationTokens.TryRedeem(redemption) is { } concurrent)
        {
            return concurrent;
        }

        try
        {
            return await RegisterAsync(db, derivedHash, outcome.State!, cancellationToken);
        }
        catch (Exception ex)
        {
            RegistrationTokens.Release(redemption);

            if (ex is DbUpdateException)
            {
                // A concurrent login registered the same key hash first. The retry verifies against its pins.
                return MollyLoginResult.Failed(MollyAttestationFailure.ConcurrentLogin);
            }

            throw;
        }
    }

    private async Task<MollyLoginResult> RegisterAsync(MollyDbContext db, byte[] derivedHash, MollyAttestationState state, CancellationToken cancellationToken)
    {
        byte[] serverHmac = RandomNumberGenerator.GetBytes(ServerHmacLength);

        DateTime now = DateTime.UtcNow;

        var entry = new MollyDbEntry
        {
            Id = Guid.NewGuid(),
            DerivedHash = derivedHash,
            CreatedAt = now,
            LastSeenAt = now,
            LastLoginAt = now,
        };

        entry.EncryptedServerHmac = Encrypt(entry.Id, serverHmac, EncryptedField.ServerHmac);
        entry.EncryptedAttestationState = EncryptAttestationState(entry.Id, state);

        db.Entries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Registered a new Molly entry in {Mode} attestation mode", state.Mode);

        return MollyLoginResult.Success(_idProtector.Protect(entry.Id, now), serverHmac);
    }

    private byte[] EncryptAttestationState(Guid id, MollyAttestationState state) =>
        Encrypt(id, JsonSerializer.SerializeToUtf8Bytes(state), EncryptedField.AttestationState);

    private bool TryDecryptAttestationState(Guid id, byte[] encrypted, [NotNullWhen(true)] out MollyAttestationState? state)
    {
        state = null;

        try
        {
            state = JsonSerializer.Deserialize<MollyAttestationState>(Decrypt(id, encrypted, EncryptedField.AttestationState));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to decrypt the attestation state of a Molly entry");
        }

        return state is not null;
    }

    /// <param name="protectedId">The token handed out by <see cref="LoginAsync"/>.</param>
    /// <param name="identityPartyHash">
    /// The party hash of the Signal identity whose <c>associate</c> proof verified, to bind the entry to (see
    /// <see cref="BindIdentity"/>).
    /// </param>
    internal async Task<MollyCommandResult> AssociateAsync(string? protectedId, string? nickname, byte[] identityPartyHash, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nickname) || Encoding.UTF8.GetByteCount(nickname) > MaxNicknameLengthInBytes)
        {
            return MollyCommandResult.Invalid;
        }

        await using MollyDbContext db = _db.CreateDbContext();

        Session session = await OpenSessionAsync(db, protectedId, stopOnBlockingCommand: true, cancellationToken);

        if (!session.IsOpen)
        {
            return session.Rejection;
        }

        MollyDbEntry entry = session.Entry;

        entry.EncryptedNickname = Encrypt(entry.Id, PadNickname(nickname), EncryptedField.Nickname);
        entry.LastSeenAt = DateTime.UtcNow;

        MollyIdentityBinding binding = BindIdentity(entry, identityPartyHash);

        await db.SaveChangesAsync(cancellationToken);

        return MollyCommandResult.Ok(session.Command) with { Identity = binding };
    }

    /// <summary>
    /// Makes <paramref name="partyHash"/> the entry's current identity (Sharing/README.md, "Identity binding"):
    /// binds it if the entry has none, or replaces a different identity bound at least
    /// <see cref="IdentityRebindCooldown"/> ago. A more recently bound identity stays and the result is
    /// <see cref="MollyIdentityBinding.Cooldown"/>. Only updates the tracked <paramref name="entry"/>; the caller saves it.
    /// </summary>
    private MollyIdentityBinding BindIdentity(MollyDbEntry entry, byte[] partyHash)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(partyHash.Length, MollyShareCrypto.PartyHashLength);

        DateTime now = DateTime.UtcNow;

        if (entry.SharePartyHash is { } currentHash)
        {
            if (CryptographicOperations.FixedTimeEquals(currentHash, partyHash))
            {
                return MollyIdentityBinding.Bound;
            }

            if (now - entry.ShareIdentityBoundAt < IdentityRebindCooldown)
            {
                return MollyIdentityBinding.Cooldown;
            }
        }

        _logger.LogInformation("A Molly entry was bound to a {Kind} Signal identity", entry.SharePartyHash is null ? "first" : "new");

        entry.SharePartyHash = partyHash;
        entry.ShareIdentityBoundAt = now;

        return MollyIdentityBinding.Bound;
    }

    /// <param name="protectedId">The token handed out by <see cref="LoginAsync"/>, if the device has one.</param>
    /// <param name="batteryLevel">Optional battery percentage, 0-100.</param>
    /// <param name="locationEnabled">Optional: whether the device can currently get a location fix.</param>
    /// <param name="appVersion">Optional current app version: up to 32 ASCII letters, digits, or ._-.</param>
    public async Task<MollyCommandResult> PingAsync(string? protectedId, int? batteryLevel, bool? locationEnabled, string? appVersion, CancellationToken cancellationToken)
    {
        if (batteryLevel is < 0 or > 100 ||
            (appVersion is not null && (appVersion.Length > MaxAppVersionLength || appVersion.AsSpan().ContainsAnyExcept(s_appVersionChars))))
        {
            return MollyCommandResult.Invalid;
        }

        // A ping without an id is still a valid liveness check, it just can't carry a command.
        if (string.IsNullOrEmpty(protectedId))
        {
            return MollyCommandResult.Ok();
        }

        await using MollyDbContext db = _db.CreateDbContext();

        // The status is recorded even while a lock or wipe is pending, so the dashboard shows how the device is doing.
        Session session = await OpenSessionAsync(db, protectedId, stopOnBlockingCommand: false, cancellationToken);

        if (!session.IsOpen)
        {
            return session.Rejection;
        }

        MollyDbEntry entry = session.Entry;

        if (batteryLevel.HasValue || locationEnabled.HasValue || appVersion is not null)
        {
            MollyDeviceStatus status = TryDecryptDeviceStatus(entry);
            status = status with
            {
                BatteryLevel = batteryLevel ?? status.BatteryLevel,
                LocationEnabled = locationEnabled ?? status.LocationEnabled,
                AppVersion = (appVersion ?? status.AppVersion)?.PadRight(MaxAppVersionLength),
            };
            entry.EncryptedDeviceStatus = Encrypt(entry.Id, JsonSerializer.SerializeToUtf8Bytes(status), EncryptedField.DeviceStatus);
        }

        await MarkSeenAsync(db, entry, cancellationToken);

        if (session.Command.IsBlocking())
        {
            return MollyCommandResult.Blocked(session.Command);
        }

        return MollyCommandResult.Ok(session.Command) with { ShareUpdatedAt = await TryGetShareUpdatedAtAsync(db, entry, cancellationToken) };
    }

    /// <summary>
    /// Issues a registration token for a setup code another device can use to register (Provisioning/README.md), on behalf of
    /// the device with the session <paramref name="protectedId"/>. Subject to the usual session rules and to
    /// <see cref="MollyRegistrationTokens.TryIssueForDevice"/>; a limited or disabled request succeeds without a
    /// <see cref="MollyCommandResult.RegistrationToken"/>.
    /// </summary>
    public async Task<MollyCommandResult> IssueRegistrationTokenAsync(string? protectedId, CancellationToken cancellationToken)
    {
        await using MollyDbContext db = _db.CreateDbContext();

        Session session = await OpenSessionAsync(db, protectedId, stopOnBlockingCommand: true, cancellationToken);

        if (!session.IsOpen)
        {
            return session.Rejection;
        }

        await MarkSeenAsync(db, session.Entry, cancellationToken);

        return MollyCommandResult.Ok(session.Command) with { RegistrationToken = RegistrationTokens.TryIssueForDevice(session.Entry.Id) };
    }

    /// <summary>
    /// Runs the usual session rules for a file sharing request. An ok result carries the entry, which the request
    /// acts as along with its current identity, in <see cref="MollyCommandResult.EntryId"/> and
    /// <see cref="MollyCommandResult.PartyHash"/> (null while unbound). A pending lock or wipe is returned as a blocked result.
    /// </summary>
    /// <param name="protectedId">The token handed out by <see cref="LoginAsync"/>.</param>
    internal async Task<MollyCommandResult> AuthorizeShareRequestAsync(string? protectedId, CancellationToken cancellationToken)
    {
        await using MollyDbContext db = _db.CreateDbContext();

        Session session = await OpenSessionAsync(db, protectedId, stopOnBlockingCommand: true, cancellationToken);

        if (!session.IsOpen)
        {
            return session.Rejection;
        }

        MollyDbEntry entry = session.Entry;

        // Owner activity, which keeps the device's grants alive.
        entry.ShareActiveAt = DateTime.UtcNow;

        await MarkSeenAsync(db, entry, cancellationToken);

        return MollyCommandResult.Ok(session.Command) with { EntryId = entry.Id, PartyHash = entry.SharePartyHash };
    }

    /// <summary>The update hint of the entry's current identity (0 if nothing affected it yet), or null if it has none.</summary>
    private static async Task<long?> TryGetShareUpdatedAtAsync(MollyDbContext db, MollyDbEntry entry, CancellationToken cancellationToken)
    {
        if (entry.SharePartyHash is not { } partyHash)
        {
            return null;
        }

        return await db.ShareUpdates
            .Where(u => u.PartyHash == partyHash)
            .Select(u => u.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The entry behind a session token with its pending command (<see cref="OpenSessionAsync"/>), or the
    /// <see cref="Rejection"/> to answer with instead.
    /// </summary>
    private readonly record struct Session(MollyDbEntry? Entry, DateTime IssuedAt, MollyCommand Command, MollyCommandResult? Rejection)
    {
        [MemberNotNullWhen(true, nameof(Entry))]
        [MemberNotNullWhen(false, nameof(Rejection))]
        public bool IsOpen => Rejection is null;

        public static Session Rejected(MollyCommandResult rejection) => new(null, default, MollyCommand.None, rejection);
    }

    /// <summary>
    /// Applies the rules every request carrying a session token shares: the token has to be one this server issued, for
    /// a session no newer one superseded, and using it activates its session. A token whose entry is gone gets a wipe:
    /// the entry's server-side key material went with it (deleted by an admin, or dropped by the unassociated-entry
    /// cleanup), so the device's data is unrecoverable, and it mustn't be left stuck on a dead token.
    /// </summary>
    /// <param name="stopOnBlockingCommand">
    /// Whether a pending lock or wipe ends the request: the check-in is recorded and the command is the answer. Otherwise
    /// the caller handles <see cref="Session.Command"/> itself, e.g. alerts are stored either way.
    /// </param>
    private async Task<Session> OpenSessionAsync(MollyDbContext db, string? protectedId, bool stopOnBlockingCommand, CancellationToken cancellationToken)
    {
        if (!_idProtector.TryUnprotect(protectedId, out Guid entryId, out DateTime issuedAt))
        {
            return Session.Rejected(MollyCommandResult.Invalid);
        }

        MollyDbEntry? entry = await db.Entries.FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken);

        if (entry is null)
        {
            return Session.Rejected(MollyCommandResult.Blocked(MollyCommand.Wipe));
        }

        if (IsSuperseded(entry, issuedAt))
        {
            return Session.Rejected(MollyCommandResult.Invalid);
        }

        await ActivateSessionAsync(db, entry, issuedAt, cancellationToken);

        MollyCommand command = GetPendingCommand(entry);

        if (stopOnBlockingCommand && command.IsBlocking())
        {
            await MarkSeenAsync(db, entry, cancellationToken);
            return Session.Rejected(MollyCommandResult.Blocked(command));
        }

        return new Session(entry, issuedAt, command, null);
    }

    /// <summary>
    /// Whether a newer session replaced the one the token was issued for. Tokens from before the
    /// entry's last login could have been copied off the device, so they stop working once the device
    /// uses a token from a newer login. Until then the previous token keeps working, so a login whose
    /// response is lost doesn't cost the device its session.
    /// </summary>
    private static bool IsSuperseded(MollyDbEntry entry, DateTime issuedAt) =>
        issuedAt.Ticks < entry.ActiveSessionIssuedAt.Ticks;

    /// <summary>
    /// Records that the device uses the token issued at <paramref name="issuedAt"/>, superseding older
    /// ones. Conditional, so a concurrent request with an older token can never move it back.
    /// </summary>
    /// <remarks>
    /// A session from the entry's latest login also confirms a passphrase change: the device kept the key hash that
    /// login used (<see cref="MollyDbEntry.DerivedHash"/>), so the other one stops logging in.
    /// </remarks>
    private static async Task ActivateSessionAsync(MollyDbContext db, MollyDbEntry entry, DateTime issuedAt, CancellationToken cancellationToken)
    {
        if (issuedAt.Ticks <= entry.ActiveSessionIssuedAt.Ticks)
        {
            return;
        }

        await db.Entries
            .Where(e => e.Id == entry.Id && e.ActiveSessionIssuedAt < issuedAt)
            .ExecuteUpdateAsync(e => e
                .SetProperty(x => x.ActiveSessionIssuedAt, issuedAt)
                .SetProperty(x => x.PreviousDerivedHash, x => x.LastLoginAt == issuedAt ? null : x.PreviousDerivedHash), cancellationToken);
    }

    /// <summary>
    /// Records that the device just checked in. This happens even when the entry is locked or wiped,
    /// so the dashboard shows whether the device is still reachable and the cleanup doesn't drop it
    /// while it is still asking for its command.
    /// </summary>
    private static async Task MarkSeenAsync(MollyDbContext db, MollyDbEntry entry, CancellationToken cancellationToken)
    {
        entry.LastSeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }


    /// <summary>How current the attestation revocation data is, for the admin dashboard.</summary>
    public MollyRevocationStatus AttestationRevocationStatus => _attestation.RevocationStatus;

    /// <summary>Lists every entry that has completed an association, for the admin dashboard.</summary>
    public async Task<MollyUserInfo[]> GetRegisteredUsersAsync(CancellationToken cancellationToken = default)
    {
        await using MollyDbContext db = _db.CreateDbContext();

        MollyDbEntry[] entries = await db.Entries
            .Where(e => e.EncryptedNickname != null)
            .ToArrayAsync(cancellationToken);

        return [.. entries
            .Select(e =>
            {
                MollyDeviceStatus status = TryDecryptDeviceStatus(e);
                MollyAttestationState? attestation = null;

                if (e.EncryptedAttestationState is { } encryptedState)
                {
                    TryDecryptAttestationState(e.Id, encryptedState, out attestation);
                }

                return new MollyUserInfo(e.Id, TryDecryptNickname(e), e.CreatedAt, e.LastSeenAt, e.LastLoginAt, e.LockRequested, e.WipeRequested, e.AlertsMuted, status.BatteryLevel, status.LocationEnabled, status.AppVersion, attestation);
            })
            .OrderByDescending(u => u.CreatedAt)];
    }

    /// <summary>
    /// Registers a throw-away entry and goes through the normal associate/ping/alert flow with it,
    /// so the admin dashboard has something to show when developing locally.
    /// </summary>
    public async Task CreateFakeEntryAsync(string nickname, CancellationToken cancellationToken = default)
    {
        // There's no device to attest, so the entry is registered directly with plausible pinned state.
        // Nobody knows its random key hash, so it can never log in.
        byte[] derivedHash = HMACSHA512.HashData(_databaseKey, RandomNumberGenerator.GetBytes(MinKeyHashLength));

        DateTime now = DateTime.UtcNow;

        var state = new MollyAttestationState
        {
            Version = MollyAttestationState.CurrentVersion,
            Mode = MollyAttestationMode.Strict,
            SigningPublicKey = RandomNumberGenerator.GetBytes(91),
            AttestPublicKey = RandomNumberGenerator.GetBytes(91),
            SecurityLevel = AndroidSecurityLevel.StrongBox,
            // GrapheneOS on a Pixel 8 Pro, so the dashboard can show a known device.
            VerifiedBootKey = Convert.FromHexString("896DB2D09D84E1D6BB747002B8A114950B946E5825772A9D48BA7EB01D118C1C"),
            VerifiedBootState = AndroidVerifiedBootState.SelfSigned,
            PackageName = "im.molly.app",
            SignatureDigest = RandomNumberGenerator.GetBytes(32),
            AppVersion = 100,
            OsVersion = 160000,
            OsPatchLevel = 20261000,
            VendorPatchLevel = 20261005,
            BootPatchLevel = 20261005,
            PinnedCertificateSerials = [],
            EnrolledAt = now,
            LastAttestedAt = now,
        };

        MollyLoginResult login;

        await using (MollyDbContext db = _db.CreateDbContext())
        {
            login = await RegisterAsync(db, derivedHash, state, cancellationToken);
        }

        // Bound to a random identity, as associating requires one.
        await AssociateAsync(login.ProtectedId, nickname, RandomNumberGenerator.GetBytes(SHA512.HashSizeInBytes), cancellationToken);

        // Muted from the start - a local dummy device shouldn't be announcing its seeded alert.
        if (_idProtector.TryUnprotect(login.ProtectedId, out Guid entryId, out _))
        {
            await SetAlertsMutedAsync(entryId, muted: true, cancellationToken);
        }

        // Random-but-plausible device stats, so the dashboard shows the whole range of states locally.
        await PingAsync(login.ProtectedId,
            batteryLevel: Random.Shared.Next(0, 101),
            locationEnabled: Random.Shared.Next(4) != 0,
            appVersion: "1.0.0",
            cancellationToken);

        // Populates the alerts table too, and exercises the reporting path end to end.
        byte[] alert = Encoding.UTF8.GetBytes(
            $$$"""{"id":"{{{login.ProtectedId}}}","type":"location","data":{"latitude":51.5007,"longitude":-0.1246,"accuracy":12.5}}""");

        await SubmitAlertAsync(login.ProtectedId, alert, cancellationToken);
    }

    /// <summary>
    /// Silences the Discord notification for this device's alerts. They keep being stored and shown
    /// on the dashboard, so a device that reports constantly can be quietened without losing data.
    /// </summary>
    public async Task SetAlertsMutedAsync(Guid id, bool muted, CancellationToken cancellationToken = default)
    {
        await using MollyDbContext db = _db.CreateDbContext();

        MollyDbEntry? entry = await db.Entries.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

        if (entry is null)
        {
            return;
        }

        entry.AlertsMuted = muted;
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("A Molly entry's alerts are now {State}", muted ? "muted" : "unmuted");
    }

    public async Task SetLockRequestedAsync(Guid id, bool lockRequested, CancellationToken cancellationToken = default)
    {
        await using MollyDbContext db = _db.CreateDbContext();

        MollyDbEntry? entry = await db.Entries.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

        if (entry is null || entry.WipeRequested)
        {
            return;
        }

        entry.LockRequested = lockRequested;

        if (!lockRequested)
        {
            // Otherwise the inactivity sweep would just re-lock it before the device can check in.
            entry.LastSeenAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("A Molly entry is now {State}", lockRequested ? "locked" : "unlocked");
    }

    /// <summary>
    /// Marks the entry for a remote wipe and destroys everything the device could still be
    /// authenticated with, including its pinned attestation state. The entry itself is kept so logins keep returning the wipe command.
    /// </summary>
    public async Task RequestWipeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using MollyDbContext db = _db.CreateDbContext();

        MollyDbEntry? entry = await db.Entries.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

        if (entry is null)
        {
            return;
        }

        entry.WipeRequested = true;
        entry.LockRequested = true;
        entry.EncryptedServerHmac = null;
        entry.EncryptedAttestationState = null;

        // The grants this device created are deleted by the file sharing maintenance.
        entry.SharePartyHash = null;
        entry.ShareIdentityBoundAt = null;

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("A Molly entry was marked for a wipe");
    }

    /// <summary>
    /// Deletes the entry and its alerts outright. Unlike a wipe this leaves nothing behind, so the
    /// device could register again from scratch and no pending command is ever delivered to it.
    /// </summary>
    public async Task DeleteEntryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using MollyDbContext db = _db.CreateDbContext();

        // Alerts are cascade deleted along with the entry, and the grants it created by the file sharing maintenance.
        int deleted = await db.Entries
            .Where(e => e.Id == id)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            _logger.LogInformation("A Molly entry was deleted");
        }
    }

    /// <summary>
    /// Announces a dashboard action taken by someone who only had the dashboard password rather than
    /// by an admin. Which device it was is deliberately left out - Discord learns no more than it does
    /// from an alert notification.
    /// </summary>
    public async Task ReportPasswordDashboardActionAsync(string action, CancellationToken cancellationToken = default)
    {
        if (_discordLogger is null)
        {
            return;
        }

        await _discordLogger.DebugAsync($"Molly dashboard: `{action}` performed");
    }

    /// <summary>Deletes the stored alerts for a single device. The entry itself is left untouched.</summary>
    public async Task DeleteAlertsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using MollyDbContext db = _db.CreateDbContext();

        int deleted = await db.Alerts
            .Where(a => a.EntryId == id)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            _logger.LogInformation("Deleted {Count} Molly alerts of an entry", deleted);
        }
    }

    /// <summary>Deletes a single stored alert. The device it came from is left untouched.</summary>
    public async Task DeleteAlertAsync(long alertId, CancellationToken cancellationToken = default)
    {
        await using MollyDbContext db = _db.CreateDbContext();

        int deleted = await db.Alerts
            .Where(a => a.Id == alertId)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            _logger.LogInformation("Deleted a Molly alert");
        }
    }

    /// <summary>The command the entry currently has pending, if any.</summary>
    private static MollyCommand GetPendingCommand(MollyDbEntry entry) =>
        entry.WipeRequested ? MollyCommand.Wipe :
        entry.LockRequested ? MollyCommand.Lock :
        MollyCommand.None;

    /// <summary>
    /// Lays the nickname out as <c>length || utf8 || zero padding</c> in a fixed size buffer, so that
    /// every stored nickname ciphertext is exactly the same length regardless of the name.
    /// </summary>
    private static byte[] PadNickname(string nickname)
    {
        byte[] padded = new byte[PaddedNicknameLength];

        int written = Encoding.UTF8.GetBytes(nickname, padded.AsSpan(sizeof(ushort)));
        BinaryPrimitives.WriteUInt16LittleEndian(padded, (ushort)written);

        return padded;
    }

    /// <summary>Reverses <see cref="PadNickname"/>. The padding is covered by the AEAD tag, so it can be trusted.</summary>
    private static string UnpadNickname(ReadOnlySpan<byte> padded)
    {
        if (padded.Length != PaddedNicknameLength)
        {
            throw new CryptographicException("The stored nickname has an unexpected length.");
        }

        int length = BinaryPrimitives.ReadUInt16LittleEndian(padded);

        if (length > padded.Length - sizeof(ushort))
        {
            throw new CryptographicException("The stored nickname has an invalid length prefix.");
        }

        return Encoding.UTF8.GetString(padded.Slice(sizeof(ushort), length));
    }

    private string TryDecryptNickname(MollyDbEntry entry)
    {
        try
        {
            if (entry.EncryptedNickname is { } encrypted)
            {
                return UnpadNickname(Decrypt(entry.Id, encrypted, EncryptedField.Nickname));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt the nickname of a Molly entry");
        }

        return "<unknown>";
    }

    /// <summary>The per-entry XAES-256-GCM key. Derived from the entry id so every entry gets a distinct key.</summary>
    private byte[] GetEntryKey(Guid id)
    {
        Span<byte> idBytes = stackalloc byte[IdLength];
        bool wrote = id.TryWriteBytes(idBytes);
        Debug.Assert(wrote);

        Span<byte> hash = stackalloc byte[HMACSHA512.HashSizeInBytes];
        HMACSHA512.HashData(_databaseKey, idBytes, hash);

        return hash.Slice(0, XAesGcm.KeySizeInBytes).ToArray();
    }

    private XAesGcm CreateAead(Guid id)
    {
        byte[] key = GetEntryKey(id);

        try
        {
            return new XAesGcm(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal MollyDeviceStatus TryDecryptDeviceStatus(MollyDbEntry entry)
    {
        try
        {
            if (entry.EncryptedDeviceStatus is { } encrypted)
            {
                MollyDeviceStatus status = JsonSerializer.Deserialize<MollyDeviceStatus>(Decrypt(entry.Id, encrypted, EncryptedField.DeviceStatus))
                    ?? throw new CryptographicException("The stored device status is null.");
                return status with { AppVersion = status.AppVersion?.TrimEnd(' ') };
            }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to decrypt the device status of a Molly entry");
        }

        return new();
    }

    /// <summary>
    /// Encrypts to <c>nonce || ciphertext || tag</c>, so the nonce travels with the data it belongs to.
    /// </summary>
    /// <remarks>
    /// Authenticated encryption rather than raw CBC: without a tag an attacker who can write to the
    /// database could flip bits in the decrypted plaintext, and the padding check would act as an oracle
    /// that recovers plaintext without ever needing the server key. The entry id is passed as associated
    /// data so a ciphertext can't be moved to a different row.
    /// </remarks>
    private byte[] Encrypt(Guid id, ReadOnlySpan<byte> plaintext, EncryptedField field)
    {
        Span<byte> idBytes = stackalloc byte[IdLength + 1];
        bool wrote = id.TryWriteBytes(idBytes);
        Debug.Assert(wrote);

        idBytes[IdLength] = (byte)field;

        using XAesGcm aead = CreateAead(id);
        return aead.Encrypt(plaintext, idBytes);
    }

    /// <summary>Reverses <see cref="Encrypt"/>, splitting the nonce and tag back off the payload.</summary>
    /// <exception cref="CryptographicException">The payload is malformed, tampered with, or from a different entry.</exception>
    private byte[] Decrypt(Guid id, ReadOnlySpan<byte> nonceCiphertextAndTag, EncryptedField field)
    {
        Span<byte> idBytes = stackalloc byte[IdLength + 1];
        bool wrote = id.TryWriteBytes(idBytes);
        Debug.Assert(wrote);

        idBytes[IdLength] = (byte)field;

        using XAesGcm aead = CreateAead(id);

        if (!aead.TryDecrypt(nonceCiphertextAndTag, out byte[]? plaintext, idBytes))
        {
            throw new CryptographicException("The encrypted payload is malformed or failed authentication.");
        }

        return plaintext;
    }

    /// <summary>Strict base64 of exactly <paramref name="length"/> bytes. See <see cref="TryDecodeBase64(string?, int, int, out byte[])"/>.</summary>
    internal static bool TryDecodeBase64(string? value, int length, out byte[] bytes) => TryDecodeBase64(value, length, length, out bytes);

    /// <summary>Strict base64, as sent by the app. Whitespace and other slack encodings are rejected.</summary>
    internal static bool TryDecodeBase64(string? value, int minLength, int maxLength, out byte[] bytes)
    {
        bytes = [];

        if (value is null || value.Length % 4 != 0)
        {
            return false;
        }

        int padding = value.EndsWith("==", StringComparison.Ordinal) ? 2 : value.EndsWith('=') ? 1 : 0;
        int decodedLength = Base64.GetMaxDecodedFromUtf8Length(value.Length) - padding;

        if (decodedLength < minLength || decodedLength > maxLength)
        {
            return false;
        }

        // Convert silently skips whitespace, so the decoded length has to match what the input implies.
        byte[] decoded = new byte[decodedLength];

        if (!Convert.TryFromBase64Chars(value, decoded, out int written) || written != decodedLength)
        {
            return false;
        }

        bytes = decoded;
        return true;
    }

    private enum EncryptedField : byte
    {
        ServerHmac = 0,
        Nickname = 1,
        Alert = 2,
        DeviceStatus = 3,
        AttestationState = 4,
    }
}
