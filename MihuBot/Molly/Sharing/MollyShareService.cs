using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using MihuBot.Configuration;
using MihuBot.DB;
using MihuBot.Molly.Api;

#nullable enable

namespace MihuBot.Molly.Sharing;

/// <summary>
/// Server side of Molly protected file sharing, see Sharing/README.md for the wire contract.
/// </summary>
/// <remarks>
/// <para>
/// Mutations of one entry (create, update, start, approve, delete, expiry, deletion of a removed or inactive device's grants) are serialized by a per-entry in-process
/// lock, so each read-validate-write sequence (such as decrypting a submitted manifest or reconstructing the
/// secret) works from a stable state and isn't duplicated by a concurrent request for the same entry.
/// Different entries never wait for each other.
/// </para>
/// <para>
/// The lock doesn't replace the DB transactions. They make each multi-statement change atomic even if the
/// process dies midway - recording the M-th approval, releasing and erasing the shares commit together, or
/// the approval is rolled back if reconstruction fails - and they order what spans entries, i.e. the
/// per-identity quotas and update hints, since SQLite runs write transactions one at a time. The state
/// transitions are also conditional updates, so they stay correct without the lock.
/// </para>
/// <para>
/// Reads take no lock. Sync makes several queries, so it reads them from one snapshot.
/// </para>
/// </remarks>
public sealed class MollyShareService
{
    public const int MaxOwnedEntries = 32;
    public const int MaxReceivedEntries = 256;

    /// <summary>
    /// At most this many of a recipient's entries can be active grants from one owner device, so one device can't fill
    /// the recipient's quota with grants it never starts (it needs B to start them, and B can dismiss them).
    /// </summary>
    public const int MaxActivePerRecipient = 4;
    public const int MaxListedTrusteeRequests = 64;

    public static readonly TimeSpan ReleasedRetention = TimeSpan.FromDays(30);

    /// <summary>A reserved ENTRY that no create used within this long is dropped.</summary>
    public static readonly TimeSpan ReservationRetention = TimeSpan.FromHours(1);

    /// <summary>
    /// Active and started grants are deleted once their owner device made no share request for this long, and
    /// a started grant's start is also this old.
    /// </summary>
    public static readonly TimeSpan OwnerInactivityRetention = TimeSpan.FromDays(14);

    private const string ResultOk = "ok";
    private const string ResultNotFound = "not-found";
    private const string ResultRejected = "rejected";
    private const string ResultLimit = "limit";
    private const string ResultUnbound = "unbound";

    private static readonly MollyShareResponse s_ok = new() { Result = ResultOk };
    private static readonly MollyShareResponse s_notFound = new() { Result = ResultNotFound };
    private static readonly MollyShareResponse s_rejected = new() { Result = ResultRejected };
    private static readonly MollyShareResponse s_limit = new() { Result = ResultLimit };
    private static readonly MollyShareResponse s_unbound = new() { Result = ResultUnbound };

    private readonly IDbContextFactory<MollyDbContext> _db;
    private readonly ILogger<MollyShareService> _logger;
    private readonly MollyService _molly;
    private readonly MollyRequestProtector _protector;
    private readonly byte[] _serverKey;
    private readonly EntryLocks _entryLocks = new();

    /// <param name="discordLogger">Null in tests, which drive <see cref="RunMaintenanceAsync"/> explicitly.</param>
    public MollyShareService(IDbContextFactory<MollyDbContext> db, ILogger<MollyShareService> logger, MollyService molly, MollyRequestProtector protector, IConfiguration configuration, Logger? discordLogger = null)
    {
        _db = db;
        _logger = logger;
        _molly = molly;
        _protector = protector;
        _serverKey = MollyShareCrypto.DeriveServerKey(Convert.FromBase64String(configuration[OptionalFeatures.MollyDatabaseKeyName]!));

        if (discordLogger is not null)
        {
            PeriodicTask.Start("MollyShareMaintenance",
                new PeriodicTaskOptions { Interval = TimeSpan.FromHours(1), RunImmediately = true },
                discordLogger, RunMaintenanceAsync);
        }
    }

    public static bool IsShareAction(string? action) => action is
        MollyApiActions.ShareReserve or MollyApiActions.ShareCreate or MollyApiActions.ShareUpdate or MollyApiActions.ShareSync or MollyApiActions.ShareGet or
        MollyApiActions.ShareStart or MollyApiActions.ShareApprove or MollyApiActions.ShareFetch or MollyApiActions.ShareDelete or MollyApiActions.ShareDismiss;

    /// <summary>
    /// Authorizes the session, then runs the action as the entry's current identity. Session and format failures
    /// are <c>invalid</c>, a pending lock or wipe is its <c>command</c>, and everything else is status <c>ok</c>
    /// with a <see cref="MollyShareResponse.Result"/>.
    /// </summary>
    public async Task<MollyApiResponse> ExecuteAsync(string action, MollyShareRequest request, CancellationToken cancellationToken)
    {
        if (!IsShareAction(action) ||
            !MollyService.TryDecodeBase64(request.Body, 0, MollyServiceExtensions.MaxRequestBodyLength, out byte[] body))
        {
            return Invalid();
        }

        try
        {
            MollyCommandResult session = await _molly.AuthorizeShareRequestAsync(request.Id, cancellationToken);

            if (session.Status != MollyResultStatus.Ok)
            {
                return new MollyApiResponse
                {
                    Status = session.Status.ToWireValue(),
                    Data = session.Status == MollyResultStatus.Command ? new MollyCommandResponse { Command = session.Command.ToWireValue() } : null,
                };
            }

            MollyShareResponse result = session.PartyHash is { } partyHash
                ? await ExecuteAsync(action, new Caller(session.EntryId, partyHash), body, cancellationToken)
                : s_unbound;

            return new MollyApiResponse { Status = MollyResultStatus.Ok.ToWireValue(), Data = result };
        }
        finally
        {
            // Create and update bodies carry SKEY.
            CryptographicOperations.ZeroMemory(body);
        }
    }

    private async Task<MollyShareResponse> ExecuteAsync(string action, Caller caller, byte[] body, CancellationToken cancellationToken) => action switch
    {
        MollyApiActions.ShareReserve => body.Length == 0 ? await ReserveAsync(caller, cancellationToken) : s_rejected,
        MollyApiActions.ShareCreate => await CreateAsync(caller, body, cancellationToken),
        MollyApiActions.ShareUpdate => await UpdateAsync(caller, body, cancellationToken),
        MollyApiActions.ShareSync => body.Length == 0 ? await SyncAsync(caller, cancellationToken) : s_rejected,
        MollyApiActions.ShareGet => await GetAsync(caller, body, cancellationToken),
        MollyApiActions.ShareStart => await StartAsync(caller, body, cancellationToken),
        MollyApiActions.ShareApprove => await ApproveAsync(caller, body, cancellationToken),
        MollyApiActions.ShareFetch => await FetchAsync(caller, body, cancellationToken),
        MollyApiActions.ShareDelete => await DeleteAsync(caller, body, cancellationToken),
        MollyApiActions.ShareDismiss => await DismissAsync(caller, body, cancellationToken),
        _ => throw new UnreachableException($"Unknown share action '{action}'."),
    };

    /// <summary>
    /// Runs <c>associate</c>, which binds the entry to the request's Signal identity (Sharing/README.md, "Identity binding")
    /// and applies the nickname. A request without an identity, or whose proof doesn't verify, is invalid and changes
    /// nothing. Successful results report the binding in <see cref="MollyCommandResult.Identity"/>.
    /// </summary>
    public async Task<MollyCommandResult> AssociateAsync(MollyAssociateRequest request, MollyExchangeContext exchange, CancellationToken cancellationToken)
    {
        if (!TryAuthenticate(request.Identity, exchange, out byte[]? partyHash))
        {
            return MollyCommandResult.Invalid;
        }

        return await _molly.AssociateAsync(request.Id, request.Nickname, partyHash, cancellationToken);
    }

    /// <summary>
    /// Verifies an <c>associate</c> identity proof (Sharing/README.md, "Request authentication") within
    /// <paramref name="exchange"/>, returning the identity's party hash.
    /// </summary>
    /// <returns>False if the identity is missing, a field is malformed, the exchange was sealed to a discovery key, or the MAC doesn't verify.</returns>
    private bool TryAuthenticate(MollyAssociateIdentity? identity, MollyExchangeContext exchange, [NotNullWhen(true)] out byte[]? partyHash)
    {
        partyHash = null;

        if (identity is null ||
            !MollyService.TryDecodeBase64(identity.Aci, MollyShareCodec.AciLength, out byte[] aci) ||
            !MollyService.TryDecodeBase64(identity.IdentityKey, MollyShareCodec.IdentityKeyLength, out byte[] identityKey) ||
            !MollyShareCodec.IsIdentityKey(identityKey) ||
            !MollyService.TryDecodeBase64(identity.Mac, MollyShareCrypto.MacLength, out byte[] mac))
        {
            return false;
        }

        Span<byte> agreement = stackalloc byte[X25519DiffieHellman.SecretAgreementSizeInBytes];

        try
        {
            if (!_protector.TryDeriveShareAgreement(exchange, identityKey.AsSpan(1), agreement) ||
                !MollyShareCrypto.VerifyRequestMac(agreement, exchange, MollyApiActions.Associate, aci, identityKey, mac))
            {
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(agreement);
        }

        partyHash = MollyShareCrypto.ComputePartyHash(_serverKey, aci, identityKey);
        return true;
    }

    /// <summary>
    /// Hands out a fresh ENTRY for the calling device's next <c>share-create</c> (Sharing/README.md, <c>share-reserve</c>). The
    /// reservation counts towards the device's quota until it is used or dropped after <see cref="ReservationRetention"/>.
    /// </summary>
    private async Task<MollyShareResponse> ReserveAsync(Caller caller, CancellationToken cancellationToken)
    {
        await using MollyDbContext db = _db.CreateDbContext();

        // The quota spans entries, so it's checked inside the write transaction, which SQLite serializes.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (await db.ShareEntries.CountAsync(e => e.OwnerEntryId == caller.EntryId, cancellationToken) >= MaxOwnedEntries)
        {
            return s_limit;
        }

        byte[] entry = RandomNumberGenerator.GetBytes(MollyShareCodec.EntryLength);

        db.ShareEntries.Add(new MollyShareDbEntry
        {
            Id = ToId(entry),
            OwnerEntryId = caller.EntryId,
            State = MollyShareState.Reserved,
            CreatedAt = UtcNowInSeconds(),
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new MollyShareResponse { Result = ResultOk, EntryId = Convert.ToBase64String(entry) };
    }

    private async Task<MollyShareResponse> CreateAsync(Caller caller, byte[] body, CancellationToken cancellationToken)
    {
        if (!MollyShareCodec.TryParseCreate(body, out MollyShareCreateRequest? request))
        {
            return s_rejected;
        }

        MollyShareDescriptor descriptor = request.Descriptor;

        if (descriptor.Revision != 0 ||
            !caller.Is(MollyShareCrypto.ComputePartyHash(_serverKey, descriptor.OwnerAci, descriptor.OwnerKey)) ||
            descriptor.RecipientAci.AsSpan().SequenceEqual(descriptor.OwnerAci) ||
            descriptor.RecipientKey.AsSpan().SequenceEqual(descriptor.OwnerKey))
        {
            return s_rejected;
        }

        byte[] entry = descriptor.Entry;
        Guid id = ToId(entry);
        byte[] digest = MollyShareCrypto.ComputeRequestDigest(_serverKey, body);
        byte[] recipientHash = MollyShareCrypto.ComputePartyHash(_serverKey, descriptor.RecipientAci, descriptor.RecipientKey);

        using EntryLock entryLock = await _entryLocks.AcquireAsync(id, cancellationToken);
        await using MollyDbContext db = _db.CreateDbContext();

        // The received quota spans entries, so it's checked inside the write transaction, which SQLite serializes.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var existing = await db.ShareEntries
            .Where(e => e.Id == id && e.OwnerEntryId == caller.EntryId)
            .Select(e => new { e.State, e.CreatedAt, e.RequestDigest })
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is null)
        {
            // Never reserved by this device, or deleted since: a deleted ENTRY is never handed out again.
            return s_notFound;
        }

        if (existing.State != MollyShareState.Reserved)
        {
            // Only a retry of the create is accepted, and not once an update replaced the digest.
            return existing.RequestDigest is { } stored && CryptographicOperations.FixedTimeEquals(stored, digest) ? s_ok : s_rejected;
        }

        DateTime now = UtcNowInSeconds();

        if (existing.CreatedAt < now - ReservationRetention)
        {
            return s_notFound;
        }

        if (await db.ShareEntries.CountAsync(e => e.RecipientKeyHash == recipientHash && e.OwnerEntryId == caller.EntryId && e.State == MollyShareState.Active, cancellationToken) >= MaxActivePerRecipient ||
            await db.ShareEntries.CountAsync(e => e.RecipientKeyHash == recipientHash, cancellationToken) >= MaxReceivedEntries)
        {
            return s_limit;
        }

        byte[] encryptedHeader = EncryptAtRest(entry, EntryField.Header, [.. descriptor.Encoded, .. request.Signature]);
        byte[] encryptedObjects = EncryptAtRest(entry, EntryField.Objects, [.. request.RosterSealed, .. request.MetadataSealed, .. request.DataKeySealedSharedEnc]);
        byte[] encryptedFile = EncryptAtRest(entry, EntryField.File, request.EncryptedFile);
        byte[] encryptedSkey = EncryptAtRest(entry, EntryField.Skey, request.Skey);

        CryptographicOperations.ZeroMemory(request.Skey);

        // Conditional, like every transition: maintenance drops expired reservations without taking the entry's lock.
        int created = await db.ShareEntries
            .Where(e => e.Id == id && e.State == MollyShareState.Reserved)
            .ExecuteUpdateAsync(e => e
                .SetProperty(x => x.State, MollyShareState.Active)
                .SetProperty(x => x.RecipientKeyHash, recipientHash)
                .SetProperty(x => x.CreatedAt, now)
                .SetProperty(x => x.RequestDigest, digest)
                .SetProperty(x => x.EncryptedHeader, encryptedHeader)
                .SetProperty(x => x.EncryptedObjects, encryptedObjects)
                .SetProperty(x => x.EncryptedFile, encryptedFile)
                .SetProperty(x => x.EncryptedSkey, encryptedSkey), cancellationToken);

        if (created == 0)
        {
            return s_notFound;
        }

        var updates = new UpdateHints();
        updates.Add(recipientHash);
        await updates.ApplyAsync(db, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("A Molly share was created");

        return s_ok;
    }

    /// <summary>
    /// Replaces an active grant's roster with the next revision (Sharing/README.md, <c>share-update</c>): only the
    /// roster, DATA_KEY_SEALED_SHAREDENC and SKEY may change, and the old SKEY is erased, so rosters of earlier
    /// revisions can't start any more.
    /// </summary>
    private async Task<MollyShareResponse> UpdateAsync(Caller caller, byte[] body, CancellationToken cancellationToken)
    {
        if (!MollyShareCodec.TryParseUpdate(body, out MollyShareUpdateRequest? request))
        {
            return s_rejected;
        }

        MollyShareDescriptor descriptor = request.Descriptor;
        byte[] entry = descriptor.Entry;
        Guid id = ToId(entry);
        byte[] digest = MollyShareCrypto.ComputeRequestDigest(_serverKey, body);

        using EntryLock entryLock = await _entryLocks.AcquireAsync(id, cancellationToken);
        await using MollyDbContext db = _db.CreateDbContext();

        var row = await db.ShareEntries
            .Where(e => e.Id == id && e.OwnerEntryId == caller.EntryId)
            .Select(e => new { e.State, e.RecipientKeyHash, e.RequestDigest, e.EncryptedHeader, e.EncryptedObjects, e.EncryptedSkey })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return s_notFound;
        }

        if (row.RequestDigest is { } stored && CryptographicOperations.FixedTimeEquals(stored, digest))
        {
            // A retry of the update that was accepted last, whatever happened since: it did take effect.
            return s_ok;
        }

        if (row.State != MollyShareState.Active)
        {
            return s_notFound;
        }

        MollyShareDescriptor current = DecryptHeader(entry, row.EncryptedHeader, out _);

        if (!caller.Is(MollyShareCrypto.ComputePartyHash(_serverKey, current.OwnerAci, current.OwnerKey)) ||
            descriptor.Revision != (long)current.Revision + 1 ||
            !descriptor.OwnerAci.AsSpan().SequenceEqual(current.OwnerAci) ||
            !descriptor.OwnerKey.AsSpan().SequenceEqual(current.OwnerKey) ||
            !descriptor.RecipientAci.AsSpan().SequenceEqual(current.RecipientAci) ||
            !descriptor.RecipientKey.AsSpan().SequenceEqual(current.RecipientKey) ||
            !IsUnchanged(MollyShareCodec.MetadataSealedIndex) ||
            !IsUnchanged(MollyShareCodec.EncryptedFileIndex) ||
            !IsUnchanged(MollyShareCodec.DataKeySealedIndex) ||
            IsCurrentSkey(entry, row.EncryptedSkey!, request.Skey))
        {
            return s_rejected;
        }

        ShareObjects objects = DecryptObjects(entry, row.EncryptedObjects);
        byte[] header = [.. descriptor.Encoded, .. request.Signature];
        byte[] encryptedObjects = EncryptAtRest(entry, EntryField.Objects, [.. request.RosterSealed, .. objects.MetadataSealed, .. request.DataKeySealedSharedEnc]);
        byte[] encryptedHeader = EncryptAtRest(entry, EntryField.Header, header);
        byte[] encryptedSkey = EncryptAtRest(entry, EntryField.Skey, request.Skey);

        CryptographicOperations.ZeroMemory(request.Skey);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        int updated = await db.ShareEntries
            .Where(e => e.Id == id && e.State == MollyShareState.Active)
            .ExecuteUpdateAsync(e => e
                .SetProperty(x => x.RequestDigest, digest)
                .SetProperty(x => x.EncryptedHeader, encryptedHeader)
                .SetProperty(x => x.EncryptedObjects, encryptedObjects)
                .SetProperty(x => x.EncryptedSkey, encryptedSkey), cancellationToken);

        if (updated == 0)
        {
            return s_notFound;
        }

        var updates = new UpdateHints();
        updates.Add(row.RecipientKeyHash!);
        await updates.ApplyAsync(db, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("A Molly share was updated");

        return s_ok;

        bool IsUnchanged(int index) => CryptographicOperations.FixedTimeEquals(descriptor.ObjectHashes[index], current.ObjectHashes[index]);
    }

    /// <summary>An update must bring a fresh SKEY: with the current one, B's earlier roster could still start.</summary>
    private bool IsCurrentSkey(byte[] entry, byte[] encryptedSkey, byte[] skey)
    {
        byte[] current = DecryptAtRest(entry, EntryField.Skey, encryptedSkey);

        try
        {
            return CryptographicOperations.FixedTimeEquals(current, skey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(current);
        }
    }

    private async Task<MollyShareResponse> SyncAsync(Caller caller, CancellationToken cancellationToken)
    {
        await using MollyDbContext db = _db.CreateDbContext();

        // All queries read one snapshot, so concurrent changes can't produce torn states, and the update hint
        // matches the lists: a change it doesn't include isn't in them either.
        await using DbTransaction snapshot = await BeginReadSnapshotAsync(db, cancellationToken);

        // Projections, so the large object columns are never loaded. Owned grants are the ones this device created,
        // whatever identity it had then; unused reservations aren't grants yet.
        var owned = await db.ShareEntries
            .AsNoTracking()
            .Where(e => e.OwnerEntryId == caller.EntryId && e.State != MollyShareState.Reserved)
            .OrderBy(e => e.CreatedAt)
            .Select(e => new { e.Id, e.State, e.CreatedAt, e.StartedAt, e.ReleasedAt, e.EncryptedHeader })
            .ToArrayAsync(cancellationToken);

        Guid[] ownedIds = [.. owned.Select(e => e.Id)];

        var approvals = await db.ShareTrustees
            .AsNoTracking()
            .Where(t => ownedIds.Contains(t.EntryId) && t.ApprovedAt != null)
            .OrderBy(t => t.Slot)
            .Select(t => new { t.EntryId, t.Slot, t.ApprovedAt, t.EncryptedSlot, t.EncryptedApproval })
            .ToArrayAsync(cancellationToken);

        var received = await db.ShareEntries
            .AsNoTracking()
            .Where(e => e.RecipientKeyHash == caller.PartyHash)
            .OrderBy(e => e.CreatedAt)
            .Select(e => new { e.Id, e.State, e.CreatedAt, e.EncryptedHeader })
            .ToArrayAsync(cancellationToken);

        var requests = await db.ShareTrustees
            .AsNoTracking()
            .Where(t => t.TrusteeKeyHash == caller.PartyHash && t.ApprovedAt == null && t.Entry.State == MollyShareState.Started)
            .OrderByDescending(t => t.Entry.StartedAt)
            .Take(MaxListedTrusteeRequests)
            .Select(t => new { t.EntryId, t.Slot, t.Entry.StartedAt, t.Entry.EncryptedHeader, t.EncryptedSlot })
            .ToArrayAsync(cancellationToken);

        long updatedAt = await db.ShareUpdates
            .Where(u => u.PartyHash == caller.PartyHash)
            .Select(u => u.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new MollyShareResponse
        {
            Result = ResultOk,
            UpdatedAt = updatedAt,
            Owned = [.. owned.Select(e =>
            {
                byte[] entry = ToEntry(e.Id);
                MollyShareDescriptor descriptor = DecryptHeader(entry, e.EncryptedHeader, out _);

                return new MollyShareOwnedItem
                {
                    EntryId = Convert.ToBase64String(entry),
                    Revision = descriptor.Revision,
                    RecipientAci = Convert.ToBase64String(descriptor.RecipientAci),
                    RecipientIdentityKey = Convert.ToBase64String(descriptor.RecipientKey),
                    State = e.State switch
                    {
                        MollyShareState.Started => "started",
                        MollyShareState.Released => "released",
                        _ => "active",
                    },
                    CreatedAt = ToUnixSeconds(e.CreatedAt),
                    StartedAt = e.StartedAt is { } startedAt ? ToUnixSeconds(startedAt) : null,
                    ReleasedAt = e.ReleasedAt is { } releasedAt ? ToUnixSeconds(releasedAt) : null,
                    Approvals = [.. approvals.Where(a => a.EntryId == e.Id).Select(a =>
                    {
                        (byte[] aci, byte[] key, _, _) = DecryptSlot(entry, a.Slot, a.EncryptedSlot);
                        byte[] approval = DecryptAtRest(entry, TrusteeField.Approval, a.Slot, a.EncryptedApproval!);

                        return new MollyShareApprovalItem
                        {
                            Aci = Convert.ToBase64String(aci),
                            IdentityKey = Convert.ToBase64String(key),
                            ApprovedAt = ToUnixSeconds(a.ApprovedAt!.Value),
                            Signature = Convert.ToBase64String(approval.AsSpan(MollyShareCodec.HashLength)),
                        };
                    })],
                };
            })],
            Received = [.. received.Select(e =>
            {
                byte[] entry = ToEntry(e.Id);
                MollyShareDescriptor descriptor = DecryptHeader(entry, e.EncryptedHeader, out _);

                return new MollyShareReceivedItem
                {
                    EntryId = Convert.ToBase64String(entry),
                    OwnerAci = Convert.ToBase64String(descriptor.OwnerAci),
                    OwnerIdentityKey = Convert.ToBase64String(descriptor.OwnerKey),
                    State = e.State switch
                    {
                        MollyShareState.Started => "started",
                        MollyShareState.Released => "released",
                        _ => "available",
                    },
                    CreatedAt = ToUnixSeconds(e.CreatedAt),
                };
            })],
            TrusteeRequests = [.. requests.Select(r =>
            {
                byte[] entry = ToEntry(r.EntryId);
                MollyShareDescriptor descriptor = DecryptHeader(entry, r.EncryptedHeader, out _);
                (_, _, byte[] sealedValue, byte[] sealedSignature) = DecryptSlot(entry, r.Slot, r.EncryptedSlot);

                return new MollyShareTrusteeRequestItem
                {
                    EntryId = Convert.ToBase64String(entry),
                    OwnerAci = Convert.ToBase64String(descriptor.OwnerAci),
                    OwnerIdentityKey = Convert.ToBase64String(descriptor.OwnerKey),
                    StartedAt = ToUnixSeconds(r.StartedAt!.Value),
                    Sealed = Convert.ToBase64String(sealedValue),
                    SealedSignature = Convert.ToBase64String(sealedSignature),
                };
            })],
        };
    }

    private async Task<MollyShareResponse> GetAsync(Caller caller, byte[] body, CancellationToken cancellationToken)
    {
        if (!MollyShareCodec.TryParseEntry(body, out byte[]? entry))
        {
            return s_rejected;
        }

        Guid id = ToId(entry);

        await using MollyDbContext db = _db.CreateDbContext();

        var row = await db.ShareEntries
            .AsNoTracking()
            .Where(e => e.Id == id && e.RecipientKeyHash == caller.PartyHash)
            .Select(e => new { e.State, e.EncryptedHeader, e.EncryptedObjects })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return s_notFound;
        }

        MollyShareDescriptor descriptor = DecryptHeader(entry, row.EncryptedHeader, out byte[] signature);
        ShareObjects objects = DecryptObjects(entry, row.EncryptedObjects);

        return new MollyShareResponse
        {
            Result = ResultOk,
            Descriptor = Convert.ToBase64String(descriptor.Encoded),
            Signature = Convert.ToBase64String(signature),
            RosterSealed = Convert.ToBase64String(objects.RosterSealed),
            MetadataSealed = Convert.ToBase64String(objects.MetadataSealed),
            Started = row.State is MollyShareState.Started or MollyShareState.Released,
        };
    }

    private async Task<MollyShareResponse> StartAsync(Caller caller, byte[] body, CancellationToken cancellationToken)
    {
        if (!MollyShareCodec.TryParseStart(body, out MollyShareStartRequest? request))
        {
            return s_rejected;
        }

        byte[] entry = request.Entry;
        Guid id = ToId(entry);

        EntryLock entryLock = await _entryLocks.AcquireAsync(id, cancellationToken);

        try
        {
            await using MollyDbContext db = _db.CreateDbContext();

            var row = await db.ShareEntries
                .Where(e => e.Id == id && e.RecipientKeyHash == caller.PartyHash)
                .Select(e => new { e.State, e.OwnerEntryId, e.EncryptedHeader, e.EncryptedSkey })
                .FirstOrDefaultAsync(cancellationToken);

            if (row is null)
            {
                return s_notFound;
            }

            if (row.State != MollyShareState.Active)
            {
                // There's only ever one attempt, so a repeat just confirms it.
                return s_ok;
            }

            MollyShareDescriptor descriptor = DecryptHeader(entry, row.EncryptedHeader, out _);
            byte[] skey = DecryptAtRest(entry, EntryField.Skey, row.EncryptedSkey!);
            byte[]? plaintext = null;

            try
            {
                if (!MollyShareCrypto.TryDecrypt(skey, MollyShareCrypto.SkeyRosterInnerLabel, entry, request.ManifestSenc, out plaintext) ||
                    !MollyShareCodec.TryParseManifest(plaintext, descriptor, out MollyShareManifest? manifest))
                {
                    return s_rejected;
                }

                DateTime now = UtcNowInSeconds();

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

                int updated = await db.ShareEntries
                    .Where(e => e.Id == id && e.State == MollyShareState.Active)
                    .ExecuteUpdateAsync(e => e
                        .SetProperty(x => x.State, MollyShareState.Started)
                        .SetProperty(x => x.StartedAt, now)
                        .SetProperty(x => x.Threshold, manifest.Threshold), cancellationToken);

                if (updated == 0)
                {
                    return s_notFound;
                }

                var updates = new UpdateHints();
                updates.AddOwner(row.OwnerEntryId);

                // B's other devices still list the grant as available.
                updates.Add(caller.PartyHash);

                foreach (MollyShareTrusteeSlot slot in manifest.Slots)
                {
                    byte[] trusteeHash = MollyShareCrypto.ComputePartyHash(_serverKey, slot.Aci, slot.Key);
                    updates.Add(trusteeHash);

                    db.ShareTrustees.Add(new MollyShareTrusteeDbEntry
                    {
                        EntryId = id,
                        Slot = slot.Slot,
                        TrusteeKeyHash = trusteeHash,
                        EncryptedSlot = EncryptAtRest(entry, TrusteeField.Slot, slot.Slot, [.. slot.Aci, .. slot.Key, .. slot.Sealed, .. slot.SealedSignature]),
                    });
                }

                await updates.ApplyAsync(db, cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                _logger.LogInformation("A Molly share recovery was started");

                return s_ok;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(skey);

                if (plaintext is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }
        finally
        {
            entryLock.Dispose();
        }
    }

    private async Task<MollyShareResponse> ApproveAsync(Caller caller, byte[] body, CancellationToken cancellationToken)
    {
        if (!MollyShareCodec.TryParseApprove(body, out MollyShareApproveRequest? request))
        {
            return s_rejected;
        }

        byte[] entry = request.Entry;
        Guid id = ToId(entry);
        byte[] shareDigest = SHA512.HashData(request.ShareTSenc);

        EntryLock entryLock = await _entryLocks.AcquireAsync(id, cancellationToken);

        try
        {
            await using MollyDbContext db = _db.CreateDbContext();

            var row = await db.ShareEntries
                .Where(e => e.Id == id)
                .Select(e => new { e.State, e.StartedAt, e.Threshold, e.OwnerEntryId, e.RecipientKeyHash, e.EncryptedSkey })
                .FirstOrDefaultAsync(cancellationToken);

            var trustee = row is null ? null : await db.ShareTrustees
                .Where(t => t.EntryId == id && t.TrusteeKeyHash == caller.PartyHash)
                .Select(t => new { t.Slot, t.ApprovedAt, t.EncryptedApproval })
                .FirstOrDefaultAsync(cancellationToken);

            if (row is null || trustee is null)
            {
                return s_notFound;
            }

            bool startedAtMatches = row.StartedAt is { } startedAt && ToUnixSeconds(startedAt) == request.StartedAt;

            if (trustee.ApprovedAt is not null)
            {
                byte[] stored = DecryptAtRest(entry, TrusteeField.Approval, trustee.Slot, trustee.EncryptedApproval!);

                if (startedAtMatches && CryptographicOperations.FixedTimeEquals(stored.AsSpan(0, MollyShareCodec.HashLength), shareDigest))
                {
                    // Already counted.
                    return s_ok;
                }

                return row.State == MollyShareState.Released ? s_notFound : s_rejected;
            }

            if (row.State != MollyShareState.Started)
            {
                return s_notFound;
            }

            if (!startedAtMatches)
            {
                return s_rejected;
            }

            int count = await db.ShareTrustees.CountAsync(t => t.EntryId == id, cancellationToken);
            byte[] skey = DecryptAtRest(entry, EntryField.Skey, row.EncryptedSkey!);
            byte[]? plaintext = null;
            MollyShareT? share = null;

            try
            {
                if (!MollyShareCrypto.TryDecrypt(skey, MollyShareCrypto.SkeyShareLabel, entry, request.ShareTSenc, out plaintext) ||
                    !MollyShareCodec.TryParseShareT(plaintext, out share) ||
                    !share.Entry.AsSpan().SequenceEqual(entry) ||
                    !caller.Is(MollyShareCrypto.ComputePartyHash(_serverKey, share.Aci, share.Key)) ||
                    share.Index != trustee.Slot + 1 ||
                    share.Threshold != row.Threshold ||
                    share.Count != count)
                {
                    return s_rejected;
                }

                return await RecordApprovalAsync(db, id, entry, row.Threshold!.Value, trustee.Slot, share.Share, [.. shareDigest, .. request.Signature], row.OwnerEntryId, row.RecipientKeyHash!, caller.PartyHash, cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(skey);

                if (plaintext is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }

                if (share is not null)
                {
                    CryptographicOperations.ZeroMemory(share.Share);
                }
            }
        }
        finally
        {
            entryLock.Dispose();
        }
    }

    /// <summary>
    /// Records a validated approval and, once it is the M-th, reconstructs, validates and releases - all in one
    /// transaction, so a failed reconstruction rolls the threshold-crossing approval back.
    /// </summary>
    private async Task<MollyShareResponse> RecordApprovalAsync(MollyDbContext db, Guid id, byte[] entry, int threshold, int slot, byte[] share, byte[] approval, Guid ownerEntryId, byte[] recipientHash, byte[] trusteeHash, CancellationToken cancellationToken)
    {
        DateTime now = UtcNowInSeconds();
        byte[] encryptedShare = EncryptAtRest(entry, TrusteeField.Share, slot, share);
        byte[] encryptedApproval = EncryptAtRest(entry, TrusteeField.Approval, slot, approval);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        int updated = await db.ShareTrustees
            .Where(t => t.EntryId == id && t.Slot == slot && t.ApprovedAt == null && t.Entry.State == MollyShareState.Started)
            .ExecuteUpdateAsync(t => t
                .SetProperty(x => x.ApprovedAt, now)
                .SetProperty(x => x.EncryptedShare, encryptedShare)
                .SetProperty(x => x.EncryptedApproval, encryptedApproval), cancellationToken);

        if (updated == 0)
        {
            return s_notFound;
        }

        var updates = new UpdateHints();
        updates.AddOwner(ownerEntryId);

        // The trustee's other devices still list the request.
        updates.Add(trusteeHash);

        var approved = await db.ShareTrustees
            .Where(t => t.EntryId == id && t.ApprovedAt != null)
            .OrderBy(t => t.Slot)
            .Select(t => new { t.Slot, t.EncryptedShare })
            .ToArrayAsync(cancellationToken);

        if (approved.Length >= threshold)
        {
            var stored = await db.ShareEntries
                .Where(e => e.Id == id)
                .Select(e => new { e.EncryptedHeader, e.EncryptedObjects })
                .FirstAsync(cancellationToken);

            MollyShareDescriptor descriptor = DecryptHeader(entry, stored.EncryptedHeader, out _);
            byte[]? dataKeySealed = TryReconstructDataKeySealed(entry, descriptor, DecryptObjects(entry, stored.EncryptedObjects), [.. approved.Take(threshold).Select(a => (a.Slot, a.EncryptedShare!))]);

            if (dataKeySealed is null)
            {
                // Rolled back with the transaction: the approval isn't counted and nothing is released.
                _logger.LogWarning("A Molly share reconstruction failed to authenticate, nothing was released");
                return s_rejected;
            }

            byte[] encryptedDataKeySealed = EncryptAtRest(entry, EntryField.DataKeySealed, dataKeySealed);

            int released = await db.ShareEntries
                .Where(e => e.Id == id && e.State == MollyShareState.Started)
                .ExecuteUpdateAsync(e => e
                    .SetProperty(x => x.State, MollyShareState.Released)
                    .SetProperty(x => x.ReleasedAt, now)
                    .SetProperty(x => x.EncryptedDataKeySealed, encryptedDataKeySealed)
                    .SetProperty(x => x.EncryptedSkey, (byte[]?)null), cancellationToken);

            if (released == 0)
            {
                return s_notFound;
            }

            await db.ShareTrustees
                .Where(t => t.EntryId == id)
                .ExecuteUpdateAsync(t => t.SetProperty(x => x.EncryptedShare, (byte[]?)null), cancellationToken);

            updates.Add(recipientHash);

            // Trustees that haven't approved still list the request.
            foreach (byte[] pendingHash in await db.ShareTrustees.Where(t => t.EntryId == id && t.ApprovedAt == null).Select(t => t.TrusteeKeyHash).ToArrayAsync(cancellationToken))
            {
                updates.Add(pendingHash);
            }

            _logger.LogInformation("A Molly share was released");
        }

        await updates.ApplyAsync(db, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return s_ok;
    }

    /// <summary>
    /// Combines the shares, authenticates the result by opening DATA_KEY_SEALED_SHAREDENC with it, and checks the
    /// opened DATA_KEY_SEALED against the descriptor, so B gets exactly what A committed to.
    /// </summary>
    private byte[]? TryReconstructDataKeySealed(byte[] entry, MollyShareDescriptor descriptor, ShareObjects objects, (int Slot, byte[] EncryptedShare)[] approved)
    {
        var shares = new List<(byte Index, byte[] Value)>(approved.Length);
        byte[]? secret = null;

        try
        {
            foreach ((int slot, byte[] encryptedShare) in approved)
            {
                shares.Add(((byte)(slot + 1), DecryptAtRest(entry, TrusteeField.Share, slot, encryptedShare)));
            }

            secret = MollyShamir.Combine(shares);

            return MollyShareCrypto.TryDecrypt(secret, MollyShareCrypto.SharedDataKeyLabel, entry, objects.DataKeySealedSharedEnc, out byte[]? dataKeySealed) &&
                dataKeySealed.Length == MollyShareCodec.DataKeySealedLength &&
                CryptographicOperations.FixedTimeEquals(SHA512.HashData(dataKeySealed), descriptor.ObjectHashes[MollyShareCodec.DataKeySealedIndex])
                ? dataKeySealed
                : null;
        }
        finally
        {
            foreach ((_, byte[] value) in shares)
            {
                CryptographicOperations.ZeroMemory(value);
            }

            if (secret is not null)
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }
    }

    private async Task<MollyShareResponse> FetchAsync(Caller caller, byte[] body, CancellationToken cancellationToken)
    {
        if (!MollyShareCodec.TryParseEntry(body, out byte[]? entry))
        {
            return s_rejected;
        }

        Guid id = ToId(entry);

        await using MollyDbContext db = _db.CreateDbContext();

        // One query, so a delete that committed first leaves nothing to return.
        var row = await db.ShareEntries
            .AsNoTracking()
            .Where(e => e.Id == id && e.RecipientKeyHash == caller.PartyHash && e.State == MollyShareState.Released)
            .Select(e => new { e.EncryptedDataKeySealed, e.EncryptedFile })
            .FirstOrDefaultAsync(cancellationToken);

        if (row?.EncryptedDataKeySealed is null || row.EncryptedFile is null)
        {
            return s_notFound;
        }

        return new MollyShareResponse
        {
            Result = ResultOk,
            DataKeySealed = Convert.ToBase64String(DecryptAtRest(entry, EntryField.DataKeySealed, row.EncryptedDataKeySealed)),
            EncryptedFile = Convert.ToBase64String(DecryptAtRest(entry, EntryField.File, row.EncryptedFile)),
        };
    }

    private async Task<MollyShareResponse> DeleteAsync(Caller caller, byte[] body, CancellationToken cancellationToken)
    {
        if (!MollyShareCodec.TryParseEntry(body, out byte[]? entry))
        {
            return s_rejected;
        }

        Guid id = ToId(entry);

        using EntryLock entryLock = await _entryLocks.AcquireAsync(id, cancellationToken);
        await using MollyDbContext db = _db.CreateDbContext();

        if (!await TryDeleteLockedAsync(db, id, owner => owner == caller.EntryId, cancellationToken))
        {
            return s_notFound;
        }

        _logger.LogInformation("A Molly share was deleted");

        return s_ok;
    }

    /// <summary>
    /// B deletes a grant it hasn't started, freeing its quota and raising A's and B's update hints (an active grant has
    /// no trustees yet). Once released, B can delete its copy instead: the file and everything only B uses are erased and
    /// the entry leaves B's lists and quota, while A keeps the released record and its approvals unchanged, so only B's
    /// hint is raised. While started, only A can end it.
    /// </summary>
    private async Task<MollyShareResponse> DismissAsync(Caller caller, byte[] body, CancellationToken cancellationToken)
    {
        if (!MollyShareCodec.TryParseEntry(body, out byte[]? entry))
        {
            return s_rejected;
        }

        Guid id = ToId(entry);

        using EntryLock entryLock = await _entryLocks.AcquireAsync(id, cancellationToken);
        await using MollyDbContext db = _db.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var row = await db.ShareEntries
            .Where(e => e.Id == id && e.RecipientKeyHash == caller.PartyHash && (e.State == MollyShareState.Active || e.State == MollyShareState.Released))
            .Select(e => new { e.State, e.OwnerEntryId })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return s_notFound;
        }

        var updates = new UpdateHints();
        updates.Add(caller.PartyHash);

        if (row.State == MollyShareState.Active)
        {
            await db.ShareEntries
                .Where(e => e.Id == id && e.State == MollyShareState.Active)
                .ExecuteDeleteAsync(cancellationToken);

            updates.AddOwner(row.OwnerEntryId);
        }
        else
        {
            await db.ShareEntries
                .Where(e => e.Id == id && e.State == MollyShareState.Released)
                .ExecuteUpdateAsync(e => e
                    .SetProperty(x => x.RecipientKeyHash, (byte[]?)null)
                    .SetProperty(x => x.EncryptedObjects, (byte[]?)null)
                    .SetProperty(x => x.EncryptedFile, (byte[]?)null)
                    .SetProperty(x => x.EncryptedDataKeySealed, (byte[]?)null), cancellationToken);
        }

        await updates.ApplyAsync(db, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("A Molly share was dismissed by its recipient");

        return s_ok;
    }

    /// <summary>
    /// Deletes a grant (or reservation) whose owner entry satisfies <paramref name="authorizeOwner"/>, along with its roster,
    /// raising B's (and a started attempt's trustees') update hints in the same transaction. The caller holds the entry's lock.
    /// </summary>
    private static async Task<bool> TryDeleteLockedAsync(MollyDbContext db, Guid id, Func<Guid, bool> authorizeOwner, CancellationToken cancellationToken)
    {
        var row = await db.ShareEntries
            .Where(e => e.Id == id)
            .Select(e => new { e.State, e.OwnerEntryId, e.RecipientKeyHash })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null || !authorizeOwner(row.OwnerEntryId))
        {
            return false;
        }

        var updates = new UpdateHints();

        if (row.RecipientKeyHash is { } recipientHash)
        {
            updates.Add(recipientHash);
        }

        if (row.State == MollyShareState.Started)
        {
            foreach (byte[] trusteeHash in await db.ShareTrustees.Where(t => t.EntryId == id).Select(t => t.TrusteeKeyHash).ToArrayAsync(cancellationToken))
            {
                updates.Add(trusteeHash);
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        await db.ShareTrustees
            .Where(t => t.EntryId == id)
            .ExecuteDeleteAsync(cancellationToken);

        // Conditional on the state that was read, like every transition.
        int deleted = await db.ShareEntries
            .Where(e => e.Id == id && e.State == row.State)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted == 0)
        {
            return false;
        }

        await updates.ApplyAsync(db, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// The file sharing state of every registered device, for the admin dashboard (see <see cref="MollyShareDeviceInfo"/>).
    /// Reads only hashes, states and timestamps: nothing is decrypted.
    /// </summary>
    public async Task<Dictionary<Guid, MollyShareDeviceInfo>> GetDeviceInfoAsync(CancellationToken cancellationToken = default)
    {
        await using MollyDbContext db = _db.CreateDbContext();
        await using DbTransaction snapshot = await BeginReadSnapshotAsync(db, cancellationToken);

        var devices = await db.Entries
            .AsNoTracking()
            .Where(e => e.EncryptedNickname != null)
            .Select(e => new { e.Id, e.WipeRequested, e.SharePartyHash, e.ShareIdentityBoundAt, e.ShareActiveAt })
            .ToArrayAsync(cancellationToken);

        var shares = await db.ShareEntries
            .AsNoTracking()
            .OrderBy(e => e.CreatedAt)
            .Select(e => new { e.Id, e.OwnerEntryId, e.RecipientKeyHash, e.State, e.CreatedAt, e.StartedAt, e.ReleasedAt, e.Threshold })
            .ToArrayAsync(cancellationToken);

        var trustees = await db.ShareTrustees
            .AsNoTracking()
            .Select(t => new { t.EntryId, t.TrusteeKeyHash, Approved = t.ApprovedAt != null, t.Entry.State })
            .ToArrayAsync(cancellationToken);

        Dictionary<byte[], int> ReceivedIn(MollyShareState state) => shares
            .Where(s => s.State == state && s.RecipientKeyHash is not null)
            .CountBy(s => s.RecipientKeyHash!, ByteArrayComparer.Instance)
            .ToDictionary(ByteArrayComparer.Instance);

        Dictionary<byte[], int> available = ReceivedIn(MollyShareState.Active);
        Dictionary<byte[], int> started = ReceivedIn(MollyShareState.Started);
        Dictionary<byte[], int> released = ReceivedIn(MollyShareState.Released);

        Dictionary<byte[], int> pendingRequests = trustees
            .Where(t => !t.Approved && t.State == MollyShareState.Started)
            .CountBy(t => t.TrusteeKeyHash, ByteArrayComparer.Instance)
            .ToDictionary(ByteArrayComparer.Instance);

        var rosters = trustees
            .GroupBy(t => t.EntryId)
            .ToDictionary(g => g.Key, g => (Count: g.Count(), Approvals: g.Count(t => t.Approved)));

        return devices.ToDictionary(d => d.Id, d =>
        {
            byte[]? partyHash = d.SharePartyHash;
            DateTime lastActivity = d.ShareActiveAt ?? DateTime.MinValue;

            int CountFor(Dictionary<byte[], int> counts) => partyHash is not null ? counts.GetValueOrDefault(partyHash) : 0;

            MollyShareOwnedInfo[] owned = [.. shares
                .Where(s => s.OwnerEntryId == d.Id && s.State != MollyShareState.Reserved)
                .Select(s =>
                {
                    (int count, int approvals) = rosters.GetValueOrDefault(s.Id);

                    DateTime? expiresAt =
                        d.WipeRequested ? null :
                        s.State == MollyShareState.Released ? s.ReleasedAt + ReleasedRetention :
                        (s.StartedAt is { } startedAt && startedAt > lastActivity ? startedAt : lastActivity) + OwnerInactivityRetention;

                    return new MollyShareOwnedInfo(s.Id, s.State, s.CreatedAt, s.StartedAt, s.ReleasedAt, s.Threshold, count, approvals, expiresAt);
                })];

            DateTime? boundAt = partyHash is null ? null : d.ShareIdentityBoundAt;
            Guid[] sameIdentity = partyHash is null ? [] : [.. devices.Where(o => o.Id != d.Id && o.SharePartyHash is { } other && ByteArrayComparer.Instance.Equals(other, partyHash)).Select(o => o.Id)];
            int reservations = shares.Count(s => s.OwnerEntryId == d.Id && s.State == MollyShareState.Reserved);

            return new MollyShareDeviceInfo(boundAt, d.ShareActiveAt, sameIdentity, reservations, owned, CountFor(available), CountFor(started), CountFor(released), CountFor(pendingRequests));
        });
    }

    /// <summary>
    /// Deletes a grant or reservation like <c>share-delete</c>, for the admin dashboard: e.g. grants of a device whose app
    /// data was cleared, which no device can manage any more. False if it no longer exists.
    /// </summary>
    public async Task<bool> AdminDeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using EntryLock entryLock = await _entryLocks.AcquireAsync(id, cancellationToken);
        await using MollyDbContext db = _db.CreateDbContext();

        if (!await TryDeleteLockedAsync(db, id, static _ => true, cancellationToken))
        {
            return false;
        }

        _logger.LogInformation("A Molly share was deleted from the dashboard");

        return true;
    }

    /// <summary>
    /// Deletes the grants of removed or inactive devices (see <see cref="RunOwnerMaintenanceAsync"/>), entries released
    /// more than <see cref="ReleasedRetention"/> ago, and reservations older than <see cref="ReservationRetention"/>.
    /// Runs periodically in the background.
    /// </summary>
    public async Task RunMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        // The tables don't exist until migrations have run.
        await DatabaseSetupHelper.MigrationsCompleted;

        await RunOwnerMaintenanceAsync(cancellationToken);

        DateTime now = DateTime.UtcNow;
        DateTime releasedCutoff = now - ReleasedRetention;
        DateTime reservationCutoff = now - ReservationRetention;

        await using MollyDbContext db = _db.CreateDbContext();

        Guid[] expired = await db.ShareEntries
            .Where(e => e.State == MollyShareState.Released && e.ReleasedAt < releasedCutoff)
            .Select(e => e.Id)
            .ToArrayAsync(cancellationToken);

        int expiredCount = 0;

        foreach (Guid id in expired)
        {
            using EntryLock entryLock = await _entryLocks.AcquireAsync(id, cancellationToken);

            // Like share-delete, so B learns it's gone. Only a delete can change a released entry meanwhile.
            if (await TryDeleteLockedAsync(db, id, static _ => true, cancellationToken))
            {
                expiredCount++;
            }
        }

        // One statement, conditional on the state, so it can't race a create using the reservation. Creates check the
        // reservation's age themselves, so an expired one is unusable even before it is dropped here.
        int dropped = await db.ShareEntries
            .Where(e => e.State == MollyShareState.Reserved && e.CreatedAt < reservationCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (expiredCount > 0 || dropped > 0)
        {
            _logger.LogInformation("Expired {Expired} released Molly shares and dropped {Dropped} unused reservations", expiredCount, dropped);
        }
    }
    /// <summary>
    /// Deletes (like <c>share-delete</c>, raising B's and the trustees' update hints) every live grant whose owner device
    /// was deleted or wiped, and every active or started grant whose owner device made no share request within
    /// <see cref="OwnerInactivityRetention"/> and that wasn't started within it either.
    /// </summary>
    private async Task RunOwnerMaintenanceAsync(CancellationToken cancellationToken)
    {
        DateTime cutoff = DateTime.UtcNow - OwnerInactivityRetention;

        await using MollyDbContext db = _db.CreateDbContext();

        Guid[] candidates = await WhereOrphanedOrExpired(db, db.ShareEntries, cutoff)
            .Select(e => e.Id)
            .ToArrayAsync(cancellationToken);

        int deleted = 0;

        foreach (Guid id in candidates)
        {
            using EntryLock entryLock = await _entryLocks.AcquireAsync(id, cancellationToken);

            // Re-checked under the entry's lock, so a recovery that started meanwhile gets its full period.
            if (await WhereOrphanedOrExpired(db, db.ShareEntries.Where(e => e.Id == id), cutoff).AnyAsync(cancellationToken) &&
                await TryDeleteLockedAsync(db, id, static _ => true, cancellationToken))
            {
                deleted++;
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation("Deleted {Deleted} Molly shares of removed or inactive devices", deleted);
        }
    }

    private static IQueryable<MollyShareDbEntry> WhereOrphanedOrExpired(MollyDbContext db, IQueryable<MollyShareDbEntry> entries, DateTime cutoff) => entries
        .Where(e =>
            !db.Entries.Any(o => o.Id == e.OwnerEntryId && !o.WipeRequested) ||
            (e.State != MollyShareState.Released &&
                !db.Entries.Any(o => o.Id == e.OwnerEntryId && o.ShareActiveAt >= cutoff) &&
                (e.StartedAt == null || e.StartedAt < cutoff)));

    /// <summary>
    /// A read-only transaction for consistent multi-query reads. On SQLite it is deferred: it takes a shared
    /// lock with its first read and keeps it, so other readers aren't serialized behind it (unlike the
    /// immediate transactions writers use), while writers can't commit in between.
    /// </summary>
    private static async Task<DbTransaction> BeginReadSnapshotAsync(MollyDbContext db, CancellationToken cancellationToken)
    {
        if (db.Database.GetDbConnection() is not SqliteConnection sqlite)
        {
            return (await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)).GetDbTransaction();
        }

        await db.Database.OpenConnectionAsync(cancellationToken);
        DbTransaction transaction = sqlite.BeginTransaction(deferred: true);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);
        return transaction;
    }

    private MollyShareDescriptor DecryptHeader(byte[] entry, byte[]? encrypted, out byte[] signature)
    {
        byte[] header = DecryptAtRest(entry, EntryField.Header, encrypted ?? throw new CryptographicException("A Molly share is missing its header."));

        if (header.Length != MollyShareCodec.DescriptorLength + MollyShareCodec.SignatureLength ||
            !MollyShareCodec.TryParseDescriptor(header.AsSpan(0, MollyShareCodec.DescriptorLength), out MollyShareDescriptor? descriptor))
        {
            throw new CryptographicException("A stored Molly share header is malformed.");
        }

        signature = header.AsSpan(MollyShareCodec.DescriptorLength).ToArray();
        return descriptor;
    }

    private ShareObjects DecryptObjects(byte[] entry, byte[]? encrypted)
    {
        byte[] objects = DecryptAtRest(entry, EntryField.Objects, encrypted ?? throw new CryptographicException("A Molly share is missing its objects."));
        int offset = 0;

        byte[] Next(int length)
        {
            byte[] value = objects.AsSpan(offset, length).ToArray();
            offset += length;
            return value;
        }

        return new ShareObjects(Next(MollyShareCodec.RosterSealedLength), Next(MollyShareCodec.MetadataSealedLength), Next(MollyShareCodec.DataKeySealedSharedEncLength));
    }

    private (byte[] Aci, byte[] Key, byte[] Sealed, byte[] SealedSignature) DecryptSlot(byte[] entry, int slot, byte[] encrypted)
    {
        byte[] value = DecryptAtRest(entry, TrusteeField.Slot, slot, encrypted);
        int offset = 0;

        byte[] Next(int length)
        {
            byte[] part = value.AsSpan(offset, length).ToArray();
            offset += length;
            return part;
        }

        return (Next(MollyShareCodec.AciLength), Next(MollyShareCodec.IdentityKeyLength), Next(MollyShareCodec.SealedLength), Next(MollyShareCodec.SignatureLength));
    }

    private byte[] EncryptAtRest(byte[] entry, EntryField field, ReadOnlySpan<byte> plaintext) =>
        MollyShareCrypto.EncryptAtRest(_serverKey, entry, [(byte)field], plaintext);

    private byte[] EncryptAtRest(byte[] entry, TrusteeField field, int slot, ReadOnlySpan<byte> plaintext) =>
        MollyShareCrypto.EncryptAtRest(_serverKey, entry, TrusteeFieldId(field, slot), plaintext);

    private byte[] DecryptAtRest(byte[] entry, EntryField field, byte[] ciphertext) =>
        MollyShareCrypto.DecryptAtRest(_serverKey, entry, [(byte)field], ciphertext);

    private byte[] DecryptAtRest(byte[] entry, TrusteeField field, int slot, byte[] ciphertext) =>
        MollyShareCrypto.DecryptAtRest(_serverKey, entry, TrusteeFieldId(field, slot), ciphertext);

    /// <summary><c>field || slot (BE16)</c>: trustee fields bind their slot, so they can't be swapped between slots.</summary>
    private static byte[] TrusteeFieldId(TrusteeField field, int slot)
    {
        byte[] id = new byte[1 + sizeof(ushort)];
        id[0] = (byte)field;
        BinaryPrimitives.WriteUInt16BigEndian(id.AsSpan(1), checked((ushort)slot));
        return id;
    }

    internal static Guid ToId(ReadOnlySpan<byte> entry) => new(entry, bigEndian: true);

    internal static byte[] ToEntry(Guid id) => id.ToByteArray(bigEndian: true);

    /// <summary>Stored times are whole seconds, so the attempt's start round-trips exactly through approvals.</summary>
    private static DateTime UtcNowInSeconds() => DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()).UtcDateTime;

    /// <summary>SQLite reads times back unspecified, they are always stored as UTC.</summary>
    private static long ToUnixSeconds(DateTime value) => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static MollyApiResponse Invalid() => new() { Status = MollyResultStatus.InvalidRequest.ToWireValue() };

    /// <summary>The entry (device) a request acts as, and its current identity.</summary>
    private sealed record Caller(Guid EntryId, byte[] PartyHash)
    {
        public bool Is(byte[] partyHash) => CryptographicOperations.FixedTimeEquals(partyHash, PartyHash);
    }

    private sealed record ShareObjects(byte[] RosterSealed, byte[] MetadataSealed, byte[] DataKeySealedSharedEnc);

    /// <summary>Field IDs of the entry row; the trustee fields' IDs are distinct and also carry the slot.</summary>
    internal enum EntryField : byte
    {
        Header = 1,
        Objects = 2,
        File = 3,
        Skey = 4,
        DataKeySealed = 5,
    }

    private enum TrusteeField : byte
    {
        Slot = 6,
        Share = 7,
        Approval = 8,
    }

    /// <summary>The identities affected by one committed change, whose update hints it raises once each.</summary>
    private sealed class UpdateHints
    {
        private readonly HashSet<byte[]> _parties = new(ByteArrayComparer.Instance);
        private Guid? _ownerEntryId;

        public void Add(byte[] partyHash) => _parties.Add(partyHash);

        /// <summary>Adds the owner device's current identity, if it still has one, when the hints are applied.</summary>
        public void AddOwner(Guid ownerEntryId) => _ownerEntryId = ownerEntryId;

        /// <summary>
        /// Raises each hint to <c>max(previous + 1, now)</c>, so it never repeats or goes backwards even if the clock
        /// does, and saves them along with the context's other pending changes. Runs inside the change's (immediate)
        /// transaction, which also orders concurrent raises. Applies the hints once: it consumes the set.
        /// </summary>
        public async Task ApplyAsync(MollyDbContext db, CancellationToken cancellationToken)
        {
            if (_ownerEntryId is { } ownerEntryId &&
                await db.Entries.Where(e => e.Id == ownerEntryId).Select(e => e.SharePartyHash).FirstOrDefaultAsync(cancellationToken) is { } ownerHash)
            {
                Add(ownerHash);
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            List<MollyShareUpdateDbEntry> existing = await db.ShareUpdates
                .Where(u => _parties.Contains(u.PartyHash))
                .ToListAsync(cancellationToken);

            foreach (MollyShareUpdateDbEntry update in existing)
            {
                update.UpdatedAt = Math.Max(update.UpdatedAt + 1, now);
                _parties.Remove(update.PartyHash);
            }

            foreach (byte[] partyHash in _parties)
            {
                db.ShareUpdates.Add(new MollyShareUpdateDbEntry { PartyHash = partyHash, UpdatedAt = now });
            }

            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// One async lock per entry, created on demand and dropped once nobody holds or waits for it, so the map only
    /// ever holds entries with mutations in flight.
    /// </summary>
    private sealed class EntryLocks
    {
        private readonly Dictionary<Guid, (SemaphoreSlim Semaphore, int Users)> _locks = [];

        public async Task<EntryLock> AcquireAsync(Guid id, CancellationToken cancellationToken)
        {
            SemaphoreSlim semaphore;

            lock (_locks)
            {
                ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_locks, id, out _);
                entry.Semaphore ??= new SemaphoreSlim(1, 1);
                entry.Users++;
                semaphore = entry.Semaphore;
            }

            try
            {
                await semaphore.WaitAsync(cancellationToken);
            }
            catch
            {
                Leave(id, acquired: false);
                throw;
            }

            return new EntryLock(this, id);
        }

        public void Leave(Guid id, bool acquired)
        {
            lock (_locks)
            {
                ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(_locks, id);
                SemaphoreSlim semaphore = entry.Semaphore;

                if (acquired)
                {
                    semaphore.Release();
                }

                if (--entry.Users == 0)
                {
                    _locks.Remove(id);
                    semaphore.Dispose();
                }
            }
        }
    }

    /// <summary>A held <see cref="EntryLocks"/> lock, released on dispose.</summary>
    private sealed class EntryLock(EntryLocks owner, Guid id) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Leave(id, acquired: true);
            }
        }
    }
}
