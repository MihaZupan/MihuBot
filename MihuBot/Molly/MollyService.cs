using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
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

    /// <summary>Entries that never completed an association are dropped after this long.</summary>
    private static readonly TimeSpan UnassociatedEntryRetention = TimeSpan.FromDays(2);

    /// <summary>Entries that haven't checked in for this long are locked automatically.</summary>
    private static readonly TimeSpan InactivityLockThreshold = TimeSpan.FromDays(7);

    /// <summary>Alerts are small status uploads, so anything larger is a mistake or abuse.</summary>
    public const int MaxAlertLength = 1024;

    /// <summary>Only the newest alerts are kept per device.</summary>
    private const int MaxAlertsPerEntry = 100;

    /// <summary>The admin dashboard, linked from alert emails.</summary>
    private const string DashboardUrl = "https://mihubot.xyz/molly";

    private const int ServerHmacLength = 64;
    private const int NonceLength = XAesGcm.NonceSizeInBytes; // 24
    private const int TagLength = XAesGcm.TagSizeInBytes; // 16
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
    private readonly byte[] _databaseKey;

    public MollyService(IDbContextFactory<MollyDbContext> db, ILogger<MollyService> logger, MollyIdProtector idProtector, IConfiguration configuration, Logger? discordLogger, IConfigurationService? runtimeConfiguration = null, ProtonMailEncryptor? protonEncryptor = null)
    {
        _db = db;
        _logger = logger;
        _discordLogger = discordLogger;
        _runtimeConfiguration = runtimeConfiguration;
        _idProtector = idProtector;

        if (configuration.IsConfigured(OptionalFeatures.MollyAlertEmail))
        {
            _emailClient = new EmailClient(configuration[OptionalFeatures.MollyAlertEmailConnectionStringName]);
            _emailFrom = configuration[OptionalFeatures.MollyAlertEmailFromName];
            _protonEncryptor = protonEncryptor;
        }

        // Trusted configuration, so a malformed value can just throw on startup.
        _databaseKey = Convert.FromBase64String(configuration[OptionalFeatures.MollyDatabaseKeyName]!);

        ArgumentOutOfRangeException.ThrowIfLessThan(_databaseKey.Length, MinKeyHashLength, OptionalFeatures.MollyDatabaseKeyName);

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

        DateOnly cutoff = DateOnly.FromDateTime(DateTime.UtcNow - UnassociatedEntryRetention);

        await using MollyDbContext db = _db.CreateDbContext();

        int deleted = await db.Entries
            .Where(e => e.EncryptedNickname == null && e.LastSeenDay < cutoff)
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

        DateOnly cutoff = DateOnly.FromDateTime(DateTime.UtcNow - InactivityLockThreshold);

        await using MollyDbContext db = _db.CreateDbContext();

        int locked = await db.Entries
            .Where(e => !e.LockRequested && e.LastSeenDay < cutoff)
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
        if (!_idProtector.TryUnprotect(protectedId, out Guid entryId) ||
            payload.IsEmpty ||
            payload.Length > MaxAlertLength)
        {
            return MollyCommandResult.Invalid;
        }

        await using MollyDbContext db = _db.CreateDbContext();

        MollyDbEntry? entry = await db.Entries.FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken);

        if (entry is null)
        {
            // The token decrypts but the entry is gone (deleted by an admin, or dropped by the
            // unassociated-entry cleanup). Its server-side key material went with it, so the device's
            // data is unrecoverable - tell it to wipe rather than leaving it stuck on a dead token.
            return MollyCommandResult.Blocked(MollyCommand.Wipe);
        }

        // The id is the caller's session token rather than part of the alert, and the row is already
        // linked to the entry, so it isn't worth storing.
        byte[] stored = StripId(payload.Span);

        db.Alerts.Add(new MollyAlertDbEntry
        {
            EntryId = entry.Id,
            CreatedAt = DateTime.UtcNow,
            EncryptedPayload = Encrypt(entry.Id, stored, EncryptedField.Alert),
        });

        entry.LastSeenDay = DateOnly.FromDateTime(DateTime.UtcNow);

        await db.SaveChangesAsync(cancellationToken);

        HandleAlertPayload(entry, stored);

        MollyCommand command = GetPendingCommand(entry);

        return command.IsBlocking()
            ? MollyCommandResult.Blocked(command)
            : MollyCommandResult.Ok(command);
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
            _logger.LogWarning(ex, "Failed to decrypt Molly alert {Id}", alert.Id);
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

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        await DeleteUnassociatedEntriesAsync(cancellationToken);
        await LockInactiveEntriesAsync(cancellationToken);
        await DeleteExcessAlertsAsync(cancellationToken);
    }

    public async Task<MollyLoginResult> LoginAsync(string? keyHash, CancellationToken cancellationToken)
    {
        if (!TryDecodeBase64(keyHash, MinKeyHashLength, MaxKeyHashLength, out byte[] keyHashBytes))
        {
            return MollyLoginResult.Invalid;
        }

        byte[] derivedHash = HMACSHA512.HashData(_databaseKey, keyHashBytes);
        int hashPrefix = derivedHash[0];

        await using MollyDbContext db = _db.CreateDbContext();

        // Partial lookup: only the hashes of entries sharing the first byte are fetched, so a prefix
        // collision doesn't drag every candidate's encrypted columns across the wire.
        var candidates = await db.Entries
            .Where(e => e.HashPrefix == hashPrefix)
            .Select(e => new { e.Id, e.DerivedHash })
            .ToArrayAsync(cancellationToken);

        Guid? matchId = null;

        foreach (var candidate in candidates)
        {
            if (CryptographicOperations.FixedTimeEquals(candidate.DerivedHash, derivedHash))
            {
                matchId = candidate.Id;
            }
        }

        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (matchId is null)
        {
            byte[] newServerHmac = RandomNumberGenerator.GetBytes(ServerHmacLength);

            var entry = new MollyDbEntry
            {
                Id = Guid.NewGuid(),
                HashPrefix = hashPrefix,
                DerivedHash = derivedHash,
                CreatedDay = today,
                LastSeenDay = today,
            };

            entry.EncryptedServerHmac = Encrypt(entry.Id, newServerHmac, EncryptedField.ServerHmac);

            db.Entries.Add(entry);
            await db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Registered a new Molly entry {Id}", entry.Id);

            return MollyLoginResult.Success(_idProtector.Protect(entry.Id), newServerHmac);
        }

        // The projection above isn't tracked, so the matched entry is loaded properly to update it.
        MollyDbEntry? match = await db.Entries.FirstOrDefaultAsync(e => e.Id == matchId.Value, cancellationToken);

        if (match is null)
        {
            // Only reachable if the maintenance sweep deleted the entry in between the two queries.
            return MollyLoginResult.Invalid;
        }

        MollyCommand command = GetPendingCommand(match);

        // The device is checking in either way, so record it before the command short-circuits.
        await MarkSeenAsync(db, match, cancellationToken);

        if (command.IsBlocking())
        {
            return MollyLoginResult.Blocked(command);
        }

        if (match.EncryptedServerHmac is null)
        {
            _logger.LogWarning("Molly entry {Id} is missing its server HMAC", match.Id);
            return MollyLoginResult.Invalid;
        }

        byte[] serverHmac = Decrypt(match.Id, match.EncryptedServerHmac, EncryptedField.ServerHmac);

        return MollyLoginResult.Success(_idProtector.Protect(match.Id), serverHmac, command);
    }

    /// <param name="protectedId">The token handed out by <see cref="LoginAsync"/>.</param>
    public async Task<MollyCommandResult> AssociateAsync(string? protectedId, string? nickname, CancellationToken cancellationToken)
    {
        if (!_idProtector.TryUnprotect(protectedId, out Guid entryId) ||
            string.IsNullOrWhiteSpace(nickname) ||
            Encoding.UTF8.GetByteCount(nickname) > MaxNicknameLengthInBytes)
        {
            return MollyCommandResult.Invalid;
        }

        await using MollyDbContext db = _db.CreateDbContext();

        MollyDbEntry? entry = await db.Entries.FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken);

        if (entry is null)
        {
            // The token decrypts but the entry is gone (deleted by an admin, or dropped by the
            // unassociated-entry cleanup). Its server-side key material went with it, so the device's
            // data is unrecoverable - tell it to wipe rather than leaving it stuck on a dead token.
            return MollyCommandResult.Blocked(MollyCommand.Wipe);
        }

        MollyCommand command = GetPendingCommand(entry);

        if (command.IsBlocking())
        {
            await MarkSeenAsync(db, entry, cancellationToken);
            return MollyCommandResult.Blocked(command);
        }

        entry.EncryptedNickname = Encrypt(entry.Id, PadNickname(nickname), EncryptedField.Nickname);
        entry.LastSeenDay = DateOnly.FromDateTime(DateTime.UtcNow);

        await db.SaveChangesAsync(cancellationToken);

        return MollyCommandResult.Ok(command);
    }

    /// <param name="protectedId">The token handed out by <see cref="LoginAsync"/>, if the device has one.</param>
    public async Task<MollyCommandResult> PingAsync(string? protectedId, CancellationToken cancellationToken)
    {
        // A ping without an id is still a valid liveness check, it just can't carry a command.
        if (string.IsNullOrEmpty(protectedId))
        {
            return MollyCommandResult.Ok();
        }

        if (!_idProtector.TryUnprotect(protectedId, out Guid entryId))
        {
            return MollyCommandResult.Invalid;
        }

        await using MollyDbContext db = _db.CreateDbContext();

        MollyDbEntry? entry = await db.Entries.FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken);

        if (entry is null)
        {
            // The token decrypts but the entry is gone (deleted by an admin, or dropped by the
            // unassociated-entry cleanup). Its server-side key material went with it, so the device's
            // data is unrecoverable - tell it to wipe rather than leaving it stuck on a dead token.
            return MollyCommandResult.Blocked(MollyCommand.Wipe);
        }

        MollyCommand command = GetPendingCommand(entry);

        await MarkSeenAsync(db, entry, cancellationToken);

        return command.IsBlocking()
            ? MollyCommandResult.Blocked(command)
            : MollyCommandResult.Ok(command);
    }

    /// <summary>
    /// Records that the device checked in today. This happens even when the entry is locked or wiped,
    /// so the dashboard shows whether the device is still reachable and the cleanup doesn't drop it
    /// while it is still asking for its command.
    /// </summary>
    private static async Task MarkSeenAsync(MollyDbContext db, MollyDbEntry entry, CancellationToken cancellationToken)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (entry.LastSeenDay != today)
        {
            entry.LastSeenDay = today;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Lists every entry that has completed an association, for the admin dashboard.</summary>
    public async Task<MollyUserInfo[]> GetRegisteredUsersAsync(CancellationToken cancellationToken = default)
    {
        await using MollyDbContext db = _db.CreateDbContext();

        MollyDbEntry[] entries = await db.Entries
            .Where(e => e.EncryptedNickname != null)
            .OrderByDescending(e => e.LastSeenDay)
            .ToArrayAsync(cancellationToken);

        return [.. entries
            .Select(e => new MollyUserInfo(e.Id, TryDecryptNickname(e), e.CreatedDay, e.LastSeenDay, e.LockRequested, e.WipeRequested, e.AlertsMuted))
            .OrderByDescending(u => u.CreatedDay)];
    }

    /// <summary>
    /// Registers and associates a throw-away entry by going through the normal login/associate/alert
    /// flow, so the admin dashboard has something to show when developing locally.
    /// </summary>
    public async Task CreateFakeEntryAsync(string nickname, CancellationToken cancellationToken = default)
    {
        string keyHash = Convert.ToBase64String(RandomNumberGenerator.GetBytes(MinKeyHashLength));

        MollyLoginResult login = await LoginAsync(keyHash, cancellationToken);

        await AssociateAsync(login.ProtectedId, nickname, cancellationToken);

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

        _logger.LogInformation("Molly entry {Id} alerts are now {State}", id, muted ? "muted" : "unmuted");
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
            entry.LastSeenDay = DateOnly.FromDateTime(DateTime.UtcNow);
        }

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Molly entry {Id} is now {State}", id, lockRequested ? "locked" : "unlocked");
    }

    /// <summary>
    /// Marks the entry for a remote wipe and destroys everything the device could still be
    /// authenticated with. The entry itself is kept so logins keep returning the wipe command.
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

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Molly entry {Id} was marked for a wipe", id);
    }

    /// <summary>
    /// Deletes the entry and its alerts outright. Unlike a wipe this leaves nothing behind, so the
    /// device could register again from scratch and no pending command is ever delivered to it.
    /// </summary>
    public async Task DeleteEntryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using MollyDbContext db = _db.CreateDbContext();

        // Alerts are cascade deleted along with the entry.
        int deleted = await db.Entries
            .Where(e => e.Id == id)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            _logger.LogInformation("Molly entry {Id} was deleted", id);
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
            _logger.LogWarning(ex, "Failed to decrypt the nickname for Molly entry {Id}", entry.Id);
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

        byte[] result = new byte[NonceLength + plaintext.Length + TagLength];

        Span<byte> nonce = result.AsSpan(0, NonceLength);
        Span<byte> ciphertext = result.AsSpan(NonceLength, plaintext.Length);
        Span<byte> tag = result.AsSpan(NonceLength + plaintext.Length);

        RandomNumberGenerator.Fill(nonce);

        using XAesGcm aead = CreateAead(id);
        aead.Encrypt(nonce, plaintext, ciphertext, tag, idBytes);

        return result;
    }

    /// <summary>Reverses <see cref="Encrypt"/>, splitting the nonce and tag back off the payload.</summary>
    /// <exception cref="CryptographicException">The payload is malformed, tampered with, or from a different entry.</exception>
    private byte[] Decrypt(Guid id, ReadOnlySpan<byte> nonceCiphertextAndTag, EncryptedField field)
    {
        if (nonceCiphertextAndTag.Length < NonceLength + TagLength)
        {
            throw new CryptographicException("The encrypted payload is too short to contain a nonce and tag.");
        }

        Span<byte> idBytes = stackalloc byte[IdLength + 1];
        bool wrote = id.TryWriteBytes(idBytes);
        Debug.Assert(wrote);

        idBytes[IdLength] = (byte)field;

        int plaintextLength = nonceCiphertextAndTag.Length - NonceLength - TagLength;
        byte[] plaintext = new byte[plaintextLength];

        using XAesGcm aead = CreateAead(id);
        aead.Decrypt(
            nonceCiphertextAndTag.Slice(0, NonceLength),
            nonceCiphertextAndTag.Slice(NonceLength, plaintextLength),
            nonceCiphertextAndTag.Slice(NonceLength + plaintextLength),
            plaintext,
            idBytes);

        return plaintext;
    }

    /// <summary>Strict base64, as sent by the app. Whitespace and other slack encodings are rejected.</summary>
    private static bool TryDecodeBase64(string? value, int minLength, int maxLength, out byte[] bytes)
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
    }
}
