using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MihuBot.DB;
using MihuBot.Helpers.Crypto;
using MihuBot.Molly;
using MihuBot.Molly.Api;
using MihuBot.Molly.Sharing;

namespace MihuBot.Tests.Molly;

public sealed class MollyShareServiceTests : IClassFixture<MollyServiceFixture>, IDisposable
{
    private readonly MollyServiceFixture _fixture;
    private readonly MollyRequestProtector _protector = new(MollyTestKeys.TransportPrivateKeyBytes);
    private readonly MollyShareService _shares;
    private readonly MollyShareTestClient _client;
    private readonly List<IDisposable> _disposables = [];

    public MollyShareServiceTests(MollyServiceFixture fixture)
    {
        _fixture = fixture;

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Molly:DatabaseKey"] = MollyTestKeys.DatabaseKey })
            .Build();

        _shares = new MollyShareService(fixture.DbFactory, NullLogger<MollyShareService>.Instance, fixture.Service, _protector, configuration);
        _client = new MollyShareTestClient(_shares, _protector);
    }

    public void Dispose()
    {
        _protector.Dispose();

        foreach (IDisposable disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    /// <summary>A party with its own identity and device session.</summary>
    private sealed record Party(MollyTestIdentity Identity, string Session);

    /// <summary>A party with its own identity and device session, bound to that identity unless <paramref name="bind"/> is false.</summary>
    private async Task<Party> NewPartyAsync(bool bind = true)
    {
        var identity = new MollyTestIdentity();
        _disposables.Add(identity);

        MollyLoginResult login = await _fixture.LoginAsync(MollyTestKeys.NewKeyHash());
        Assert.Equal(MollyResultStatus.Ok, login.Status);

        if (bind)
        {
            Assert.Equal(MollyIdentityBinding.Bound, (await _client.AssociateAsync(login.ProtectedId!, identity)).Identity);
        }

        return new Party(identity, login.ProtectedId!);
    }

    private async Task<Party[]> NewPartiesAsync(int count)
    {
        var parties = new Party[count];

        for (int i = 0; i < count; i++)
        {
            parties[i] = await NewPartyAsync();
        }

        return parties;
    }

    private Task<JsonElement> SendAsync(Party party, string action, byte[] body) => _client.SendAsync(party.Session, action, body);

    private static string Status(JsonElement response) => response.GetProperty("status").GetString()!;

    private static string Result(JsonElement response)
    {
        Assert.Equal("ok", Status(response));
        return response.GetProperty("data").GetProperty("result").GetString()!;
    }

    private static JsonElement Data(JsonElement response) => response.GetProperty("data");

    private static byte[] Bytes(JsonElement element, string property) => Convert.FromBase64String(element.GetProperty(property).GetString()!);

    private async Task<JsonElement> SyncAsync(Party party)
    {
        JsonElement response = await SendAsync(party, MollyApiActions.ShareSync, []);
        Assert.Equal("ok", Result(response));
        return Data(response);
    }

    private async Task<long> StartAsync(Party recipient, MollyTestShare share)
    {
        Assert.Equal("ok", Result(await SendAsync(recipient, MollyApiActions.ShareStart, share.StartBody())));

        JsonElement owned = Assert.Single((await SyncAsync(recipient)).GetProperty("received").EnumerateArray(), r => r.GetProperty("entryId").GetString() == Convert.ToBase64String(share.Entry));
        Assert.Equal("started", owned.GetProperty("state").GetString());

        await using MollyDbContext db = _fixture.DbFactory.CreateDbContext();
        Guid id = MollyShareService.ToId(share.Entry);
        DateTime startedAt = (await db.ShareEntries.SingleAsync(e => e.Id == id)).StartedAt!.Value;
        return new DateTimeOffset(DateTime.SpecifyKind(startedAt, DateTimeKind.Utc)).ToUnixTimeSeconds();
    }

    private async Task<(Party Owner, Party Recipient, Party[] Trustees, MollyTestShare Share)> CreateShareAsync(int threshold, int count, bool corruptSharedSecret = false, Func<int, byte[], byte[]>? shareTOverride = null, bool wrongDataKeySealed = false)
    {
        Party owner = await NewPartyAsync();
        Party recipient = await NewPartyAsync();
        Party[] trustees = await NewPartiesAsync(count);
        var share = new MollyTestShare(owner.Identity, recipient.Identity, [.. trustees.Select(t => t.Identity)], threshold, corruptSharedSecret, shareTOverride, wrongDataKeySealed);

        Assert.Equal("ok", await CreateAsync(owner, share));

        return (owner, recipient, trustees, share);
    }

    /// <summary>Reserves the share's ENTRY for the party's device, as if <c>share-reserve</c> had handed it out.</summary>
    private async Task ReserveAsync(Party party, MollyTestShare share)
    {
        await using MollyDbContext db = _fixture.DbFactory.CreateDbContext();

        db.ShareEntries.Add(new MollyShareDbEntry
        {
            Id = MollyShareService.ToId(share.Entry),
            OwnerEntryId = _fixture.Unprotect(party.Session),
            State = MollyShareState.Reserved,
            CreatedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>Reserves the share's ENTRY for the party's device and sends its <c>share-create</c>, returning the result.</summary>
    private async Task<string> CreateAsync(Party party, MollyTestShare share)
    {
        await ReserveAsync(party, share);
        return Result(await SendAsync(party, MollyApiActions.ShareCreate, share.CreateBody()));
    }

    private async Task<byte[]> ReserveEntryAsync(Party party)
    {
        JsonElement response = await SendAsync(party, MollyApiActions.ShareReserve, []);
        Assert.Equal("ok", Result(response));
        return Bytes(Data(response), "entryId");
    }

    private async Task<MollyShareDbEntry?> GetEntryAsync(MollyTestShare share)
    {
        await using MollyDbContext db = _fixture.DbFactory.CreateDbContext();
        Guid id = MollyShareService.ToId(share.Entry);
        return await db.ShareEntries.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id);
    }

    [Fact]
    public async Task FullFlow_ReleasesAtThreshold_AndRelaysApprovalsUnchanged()
    {
        (Party owner, Party recipient, Party[] trustees, MollyTestShare share) = await CreateShareAsync(threshold: 2, count: 3);
        string entryId = Convert.ToBase64String(share.Entry);

        // B sees a generic entry: only who shared it and an opaque state.
        JsonElement received = Assert.Single((await SyncAsync(recipient)).GetProperty("received").EnumerateArray());
        Assert.Equal(new[] { "entryId", "ownerAci", "ownerIdentityKey", "state", "createdAt" }, received.EnumerateObject().Select(p => p.Name));
        Assert.Equal(entryId, received.GetProperty("entryId").GetString());
        Assert.Equal(share.Owner.Aci, Bytes(received, "ownerAci"));
        Assert.Equal(share.Owner.Key, Bytes(received, "ownerIdentityKey"));
        Assert.Equal("available", received.GetProperty("state").GetString());

        JsonElement owned = Assert.Single((await SyncAsync(owner)).GetProperty("owned").EnumerateArray());
        Assert.Equal("active", owned.GetProperty("state").GetString());
        Assert.Equal(0, owned.GetProperty("revision").GetInt64());
        Assert.Equal(share.Recipient.Aci, Bytes(owned, "recipientAci"));
        Assert.Equal(share.Recipient.Key, Bytes(owned, "recipientIdentityKey"));
        Assert.False(owned.TryGetProperty("startedAt", out _));
        Assert.Empty(owned.GetProperty("approvals").EnumerateArray());

        JsonElement get = Data(await SendAsync(recipient, MollyApiActions.ShareGet, share.Entry));
        Assert.Equal(new[] { "result", "descriptor", "signature", "rosterSealed", "metadataSealed", "started" }, get.EnumerateObject().Select(p => p.Name));
        Assert.Equal(share.Descriptor, Bytes(get, "descriptor"));
        Assert.Equal(share.GrantSignature, Bytes(get, "signature"));
        Assert.Equal(share.RosterSealed, Bytes(get, "rosterSealed"));
        Assert.Equal(share.MetadataSealed, Bytes(get, "metadataSealed"));
        Assert.False(get.GetProperty("started").GetBoolean());

        // Only B may read it, and nothing is fetchable before release.
        Assert.Equal("not-found", Result(await SendAsync(owner, MollyApiActions.ShareGet, share.Entry)));
        Assert.Equal("not-found", Result(await SendAsync(trustees[0], MollyApiActions.ShareGet, share.Entry)));
        Assert.Equal("not-found", Result(await SendAsync(recipient, MollyApiActions.ShareFetch, share.Entry)));
        Assert.Empty((await SyncAsync(trustees[0])).GetProperty("trusteeRequests").EnumerateArray());

        // Only B may start it, and the attempt can't be restarted.
        Assert.Equal("not-found", Result(await SendAsync(owner, MollyApiActions.ShareStart, share.StartBody())));
        long startedAt = await StartAsync(recipient, share);
        Assert.Equal("ok", Result(await SendAsync(recipient, MollyApiActions.ShareStart, share.StartBody())));
        Assert.True(Data(await SendAsync(recipient, MollyApiActions.ShareGet, share.Entry)).GetProperty("started").GetBoolean());

        for (int i = 0; i < trustees.Length; i++)
        {
            JsonElement request = Assert.Single((await SyncAsync(trustees[i])).GetProperty("trusteeRequests").EnumerateArray());
            Assert.Equal(entryId, request.GetProperty("entryId").GetString());
            Assert.Equal(share.Owner.Aci, Bytes(request, "ownerAci"));
            Assert.Equal(share.Owner.Key, Bytes(request, "ownerIdentityKey"));
            Assert.Equal(startedAt, request.GetProperty("startedAt").GetInt64());
            Assert.Equal(share.Sealed[i], Bytes(request, "sealed"));
            Assert.Equal(share.SealedSignatures[i], Bytes(request, "sealedSignature"));
        }

        Assert.Equal("ok", Result(await SendAsync(trustees[0], MollyApiActions.ShareApprove, share.ApproveBody(0, startedAt))));

        // A repeat isn't counted again, so one approval still releases nothing.
        Assert.Equal("ok", Result(await SendAsync(trustees[0], MollyApiActions.ShareApprove, share.ApproveBody(0, startedAt))));
        Assert.Equal("not-found", Result(await SendAsync(recipient, MollyApiActions.ShareFetch, share.Entry)));
        Assert.Empty((await SyncAsync(trustees[0])).GetProperty("trusteeRequests").EnumerateArray());
        Assert.Equal("started", Assert.Single((await SyncAsync(recipient)).GetProperty("received").EnumerateArray()).GetProperty("state").GetString());

        Assert.Equal("ok", Result(await SendAsync(trustees[2], MollyApiActions.ShareApprove, share.ApproveBody(2, startedAt))));

        // B gets exactly the DATA_KEY_SEALED the descriptor commits to, and opens it itself.
        JsonElement fetched = Data(await SendAsync(recipient, MollyApiActions.ShareFetch, share.Entry));
        Assert.Equal(new[] { "result", "dataKeySealed", "encryptedFile" }, fetched.EnumerateObject().Select(p => p.Name));
        Assert.Equal(share.DataKeySealed, Bytes(fetched, "dataKeySealed"));
        Assert.True(MollyShareCrypto.TryDecrypt(share.DataKey, MollyShareCrypto.DataFileLabel, share.Entry, Bytes(fetched, "encryptedFile"), out byte[]? file));
        Assert.Equal(MollyShareTestEncoding.File(share.FileContent), file);

        owned = Assert.Single((await SyncAsync(owner)).GetProperty("owned").EnumerateArray());
        Assert.Equal("released", owned.GetProperty("state").GetString());
        Assert.Equal(startedAt, owned.GetProperty("startedAt").GetInt64());
        Assert.True(owned.TryGetProperty("releasedAt", out _));
        JsonElement[] approvals = [.. owned.GetProperty("approvals").EnumerateArray()];
        Assert.Equal(2, approvals.Length);
        Assert.Equal(share.Trustees[0].Aci, Bytes(approvals[0], "aci"));
        Assert.Equal(share.Trustees[0].Key, Bytes(approvals[0], "identityKey"));
        Assert.Equal(share.ApprovalSignatures[0], Bytes(approvals[0], "signature"));
        Assert.Equal(share.Trustees[2].Key, Bytes(approvals[1], "identityKey"));
        Assert.Equal(share.ApprovalSignatures[2], Bytes(approvals[1], "signature"));

        received = Assert.Single((await SyncAsync(recipient)).GetProperty("received").EnumerateArray());
        Assert.Equal(new[] { "entryId", "ownerAci", "ownerIdentityKey", "state", "createdAt" }, received.EnumerateObject().Select(p => p.Name));
        Assert.Equal("released", received.GetProperty("state").GetString());
        Assert.True(Data(await SendAsync(recipient, MollyApiActions.ShareGet, share.Entry)).GetProperty("started").GetBoolean());

        // After release: repeats are still ok, anything new is not-found, and the remaining request is gone.
        Assert.Equal("ok", Result(await SendAsync(trustees[0], MollyApiActions.ShareApprove, share.ApproveBody(0, startedAt))));
        Assert.Equal("ok", Result(await SendAsync(recipient, MollyApiActions.ShareStart, share.StartBody())));
        Assert.Equal("not-found", Result(await SendAsync(trustees[1], MollyApiActions.ShareApprove, share.ApproveBody(1, startedAt))));
        Assert.Empty((await SyncAsync(trustees[1])).GetProperty("trusteeRequests").EnumerateArray());

        // The shares and SKEY are erased, the approvals kept.
        await using MollyDbContext db = _fixture.DbFactory.CreateDbContext();
        Guid id = MollyShareService.ToId(share.Entry);
        Assert.Null((await db.ShareEntries.SingleAsync(e => e.Id == id)).EncryptedSkey);
        Assert.All(await db.ShareTrustees.Where(t => t.EntryId == id).ToArrayAsync(), t => Assert.Null(t.EncryptedShare));
        Assert.Equal(2, await db.ShareTrustees.CountAsync(t => t.EntryId == id && t.EncryptedApproval != null));
    }

    [Fact]
    public async Task ThresholdOne_ReleasesOnTheFirstApproval()
    {
        (_, Party recipient, Party[] trustees, MollyTestShare share) = await CreateShareAsync(threshold: 1, count: 1);
        long startedAt = await StartAsync(recipient, share);

        Assert.Equal("ok", Result(await SendAsync(trustees[0], MollyApiActions.ShareApprove, share.ApproveBody(0, startedAt))));
        Assert.Equal("ok", Result(await SendAsync(recipient, MollyApiActions.ShareFetch, share.Entry)));
    }

    [Fact]
    public async Task Approvals_ThatDontMatchTheAttempt_AreRejected()
    {
        (Party owner, Party recipient, Party[] trustees, MollyTestShare share) = await CreateShareAsync(threshold: 2, count: 3, shareTOverride: (i, shareT) =>
        {
            // The third trustee's share claims the wrong index.
            if (i == 2)
            {
                shareT[98] = 1;
            }

            return shareT;
        });

        Assert.Equal("not-found", Result(await SendAsync(trustees[0], MollyApiActions.ShareApprove, share.ApproveBody(0, 0))));

        long startedAt = await StartAsync(recipient, share);

        Assert.Equal("rejected", Result(await SendAsync(trustees[0], MollyApiActions.ShareApprove, share.ApproveBody(0, startedAt + 1))));
        Assert.Equal("rejected", Result(await SendAsync(trustees[0], MollyApiActions.ShareApprove, share.ApproveBody(1, startedAt))));
        Assert.Equal("rejected", Result(await SendAsync(trustees[2], MollyApiActions.ShareApprove, share.ApproveBody(2, startedAt))));
        Assert.Equal("rejected", Result(await SendAsync(trustees[0], MollyApiActions.ShareApprove, MollyShareTestEncoding.ApproveBody(share.Entry, startedAt, RandomNumberGenerator.GetBytes(141), new byte[64]))));
        Assert.Equal("rejected", Result(await SendAsync(trustees[0], MollyApiActions.ShareApprove, share.ApproveBody(0, startedAt)[..^1])));
        Assert.Equal("not-found", Result(await SendAsync(owner, MollyApiActions.ShareApprove, share.ApproveBody(0, startedAt))));
        Assert.Equal("not-found", Result(await SendAsync(recipient, MollyApiActions.ShareApprove, share.ApproveBody(0, startedAt))));

        Assert.Empty(Assert.Single((await SyncAsync(owner)).GetProperty("owned").EnumerateArray()).GetProperty("approvals").EnumerateArray());

        // An accepted approval can't be swapped for a different submission.
        Assert.Equal("ok", Result(await SendAsync(trustees[1], MollyApiActions.ShareApprove, share.ApproveBody(1, startedAt))));
        Assert.Equal("rejected", Result(await SendAsync(trustees[1], MollyApiActions.ShareApprove, share.ApproveBody(1, startedAt + 1))));
        Assert.Single(Assert.Single((await SyncAsync(owner)).GetProperty("owned").EnumerateArray()).GetProperty("approvals").EnumerateArray());
    }

    /// <param name="wrongDataKeySealed">
    /// The shares reconstruct the secret and it opens DATA_KEY_SEALED_SHAREDENC, but the result isn't the
    /// DATA_KEY_SEALED the descriptor commits to.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReconstruction_RollsBackTheApproval_AndReleasesNothing(bool wrongDataKeySealed)
    {
        (Party owner, Party recipient, Party[] trustees, MollyTestShare share) = await CreateShareAsync(threshold: 2, count: 2, corruptSharedSecret: !wrongDataKeySealed, wrongDataKeySealed: wrongDataKeySealed);
        long startedAt = await StartAsync(recipient, share);

        Assert.Equal("ok", Result(await SendAsync(trustees[0], MollyApiActions.ShareApprove, share.ApproveBody(0, startedAt))));
        Assert.Equal("rejected", Result(await SendAsync(trustees[1], MollyApiActions.ShareApprove, share.ApproveBody(1, startedAt))));

        JsonElement owned = Assert.Single((await SyncAsync(owner)).GetProperty("owned").EnumerateArray());
        Assert.Equal("started", owned.GetProperty("state").GetString());
        Assert.Single(owned.GetProperty("approvals").EnumerateArray());
        Assert.Single((await SyncAsync(trustees[1])).GetProperty("trusteeRequests").EnumerateArray());
        Assert.Equal("not-found", Result(await SendAsync(recipient, MollyApiActions.ShareFetch, share.Entry)));
        Assert.NotNull((await GetEntryAsync(share))!.EncryptedSkey);
    }

    [Fact]
    public async Task Create_IsIdempotentForIdenticalBodies_AndRejectsAnythingElse()
    {
        (Party owner, Party recipient, Party[] trustees, MollyTestShare share) = await CreateShareAsync(threshold: 1, count: 1);

        Assert.Equal("ok", Result(await SendAsync(owner, MollyApiActions.ShareCreate, share.CreateBody())));

        // SKEY isn't covered by the descriptor, but the create digest covers the whole body.
        byte[] otherSkey = share.CreateBody();
        otherSkey[^1] ^= 1;
        Assert.Equal("rejected", Result(await SendAsync(owner, MollyApiActions.ShareCreate, otherSkey)));
        Assert.Single((await SyncAsync(recipient)).GetProperty("received").EnumerateArray());

        // Only A itself may create, and not for itself.
        var other = new MollyTestShare(owner.Identity, recipient.Identity, [trustees[0].Identity], 1);
        Assert.Equal("rejected", Result(await SendAsync(recipient, MollyApiActions.ShareCreate, other.CreateBody())));

        var self = new MollyTestShare(owner.Identity, owner.Identity, [trustees[0].Identity], 1);
        Assert.Equal("rejected", Result(await SendAsync(owner, MollyApiActions.ShareCreate, self.CreateBody())));
        Assert.Equal("rejected", Result(await SendAsync(owner, MollyApiActions.ShareCreate, share.CreateBody()[..^1])));
        Assert.Equal("rejected", Result(await SendAsync(owner, MollyApiActions.ShareSync, [1])));
    }

    [Fact]
    public async Task Start_WithAnInvalidManifest_CommitsNothing()
    {
        (_, Party recipient, Party[] trustees, MollyTestShare share) = await CreateShareAsync(threshold: 1, count: 1);

        byte[] tampered = share.StartBody();
        tampered[^1] ^= 1;
        Assert.Equal("rejected", Result(await SendAsync(recipient, MollyApiActions.ShareStart, tampered)));

        // Authentic under SKEY, but listing B as a trustee.
        byte[] manifest = MollyShareTestEncoding.Manifest(share.Entry, 1, [MollyShareTestEncoding.Slot(recipient.Identity.Aci, recipient.Identity.Key, 1, new byte[1395], new byte[64])]);
        Assert.Equal("rejected", Result(await SendAsync(recipient, MollyApiActions.ShareStart, MollyShareTestEncoding.StartBody(share.Entry, share.EncryptManifest(manifest)))));

        Assert.Equal(MollyShareState.Active, (await GetEntryAsync(share))!.State);
        Assert.Empty((await SyncAsync(trustees[0])).GetProperty("trusteeRequests").EnumerateArray());
    }

    [Fact]
    public async Task Quotas_LimitOwnedEntriesIncludingReservations()
    {
        Party owner = await NewPartyAsync();
        Party trustee = await NewPartyAsync();

        for (int i = 0; i < MollyShareService.MaxOwnedEntries - 1; i++)
        {
            var recipient = new MollyTestIdentity();
            _disposables.Add(recipient);

            var share = new MollyTestShare(owner.Identity, recipient, [trustee.Identity], 1, entry: await ReserveEntryAsync(owner));
            Assert.Equal("ok", Result(await SendAsync(owner, MollyApiActions.ShareCreate, share.CreateBody())));
        }

        // An unused reservation counts too.
        await ReserveEntryAsync(owner);
        Assert.Equal("limit", Result(await SendAsync(owner, MollyApiActions.ShareReserve, [])));

        // Deleting frees a slot.
        JsonElement first = (await SyncAsync(owner)).GetProperty("owned")[0];
        Assert.Equal("ok", Result(await SendAsync(owner, MollyApiActions.ShareDelete, Bytes(first, "entryId"))));
        Assert.Equal("ok", Result(await SendAsync(owner, MollyApiActions.ShareReserve, [])));
    }

    [Fact]
    public async Task Quotas_LimitReceivedEntries_AndActiveGrantsFromOneOwnerDevice()
    {
        Party recipient = await NewPartyAsync();
        Party trustee = await NewPartyAsync();
        Party[] owners = await NewPartiesAsync(MollyShareService.MaxReceivedEntries / MollyShareService.MaxActivePerRecipient + 1);

        MollyTestShare Share(Party owner) => new(owner.Identity, recipient.Identity, [trustee.Identity], 1);

        // One device can't have more active grants to B, but a started one no longer counts.
        MollyTestShare[] own = [.. Enumerable.Range(0, MollyShareService.MaxActivePerRecipient).Select(_ => Share(owners[0]))];

        foreach (MollyTestShare share in own)
        {
            Assert.Equal("ok", await CreateAsync(owners[0], share));
        }

        MollyTestShare extra = Share(owners[0]);
        Assert.Equal("limit", await CreateAsync(owners[0], extra));

        await StartAsync(recipient, own[0]);
        Assert.Equal("ok", Result(await SendAsync(owners[0], MollyApiActions.ShareCreate, extra.CreateBody())));

        // B is full, whoever shares with it.
        int received = MollyShareService.MaxActivePerRecipient + 1;

        for (int i = 1; received < MollyShareService.MaxReceivedEntries; i++)
        {
            for (int j = 0; j < MollyShareService.MaxActivePerRecipient && received < MollyShareService.MaxReceivedEntries; j++, received++)
            {
                Assert.Equal("ok", await CreateAsync(owners[i], Share(owners[i])));
            }
        }

        Assert.Equal(MollyShareService.MaxReceivedEntries, (await SyncAsync(recipient)).GetProperty("received").GetArrayLength());

        MollyTestShare late = Share(owners[^1]);
        Assert.Equal("limit", await CreateAsync(owners[^1], late));

        // Dismissing frees a slot, and the reservation is still usable.
        Assert.Equal("ok", Result(await SendAsync(recipient, MollyApiActions.ShareDismiss, own[1].Entry)));
        Assert.Equal("ok", Result(await SendAsync(owners[^1], MollyApiActions.ShareCreate, late.CreateBody())));
    }

    [Fact]
    public async Task Dismiss_LetsTheRecipientDeleteAGrantItHasntStarted()
    {
        (Party owner, Party recipient, Party[] trustees, MollyTestShare share) = await CreateShareAsync(threshold: 1, count: 1);

        // Only B, and only while active.
        Assert.Equal("not-found", Result(await SendAsync(owner, MollyApiActions.ShareDismiss, share.Entry)));
        Assert.Equal("not-found", Result(await SendAsync(trustees[0], MollyApiActions.ShareDismiss, share.Entry)));
        Assert.Equal("rejected", Result(await SendAsync(recipient, MollyApiActions.ShareDismiss, [.. share.Entry, 0])));

        var started = new MollyTestShare(owner.Identity, recipient.Identity, [trustees[0].Identity], 1);
        Assert.Equal("ok", await CreateAsync(owner, started));
        await StartAsync(recipient, started);
        Assert.Equal("not-found", Result(await SendAsync(recipient, MollyApiActions.ShareDismiss, started.Entry)));

        long ownerBefore = (await PingUpdatedAtAsync(owner))!.Value;
        long recipientBefore = (await PingUpdatedAtAsync(recipient))!.Value;

        Assert.Equal("ok", Result(await SendAsync(recipient, MollyApiActions.ShareDismiss, share.Entry)));
        Assert.Equal("not-found", Result(await SendAsync(recipient, MollyApiActions.ShareDismiss, share.Entry)));

        Assert.Null(await GetEntryAsync(share));
        Assert.Equal(MollyShareState.Started, (await GetEntryAsync(started))!.State);
        Assert.Equal(Convert.ToBase64String(started.Entry), Assert.Single((await SyncAsync(owner)).GetProperty("owned").EnumerateArray()).GetProperty("entryId").GetString());
        Assert.Equal(Convert.ToBase64String(started.Entry), Assert.Single((await SyncAsync(recipient)).GetProperty("received").EnumerateArray()).GetProperty("entryId").GetString());
        Assert.True(await PingUpdatedAtAsync(owner) > ownerBefore);
        Assert.True(await PingUpdatedAtAsync(recipient) > recipientBefore);
    }

    [Fact]
    public async Task Dismiss_OfAReleasedEntry_ErasesOnlyTheRecipientsCopy()
    {
        (Party owner, Party recipient, Party[] trustees, MollyTestShare share) = await CreateShareAsync(threshold: 1, count: 1);
        long startedAt = await StartAsync(recipient, share);
        Assert.Equal("ok", Result(await SendAsync(trustees[0], MollyApiActions.ShareApprove, share.ApproveBody(0, startedAt))));

        long ownerBefore = (await PingUpdatedAtAsync(owner))!.Value;
        long recipientBefore = (await PingUpdatedAtAsync(recipient))!.Value;

        Assert.Equal("not-found", Result(await SendAsync(owner, MollyApiActions.ShareDismiss, share.Entry)));
        Assert.Equal("ok", Result(await SendAsync(recipient, MollyApiActions.ShareDismiss, share.Entry)));
        Assert.Equal("not-found", Result(await SendAsync(recipient, MollyApiActions.ShareDismiss, share.Entry)));

        // B's copy is gone, from its lists and quota too.
        Assert.Empty((await SyncAsync(recipient)).GetProperty("received").EnumerateArray());
        Assert.Equal("not-found", Result(await SendAsync(recipient, MollyApiActions.ShareGet, share.Entry)));
        Assert.Equal("not-found", Result(await SendAsync(recipient, MollyApiActions.ShareFetch, share.Entry)));

        MollyShareDbEntry stored = (await GetEntryAsync(share))!;
        Assert.Equal(MollyShareState.Released, stored.State);
        Assert.Null(stored.RecipientKeyHash);
        Assert.Null(stored.EncryptedObjects);
        Assert.Null(stored.EncryptedFile);
        Assert.Null(stored.EncryptedDataKeySealed);

        // A's record and approvals are unchanged, and A isn't told.
        JsonElement owned = Assert.Single((await SyncAsync(owner)).GetProperty("owned").EnumerateArray());
        Assert.Equal("released", owned.GetProperty("state").GetString());
        Assert.Equal(share.ApprovalSignatures[0], Bytes(Assert.Single(owned.GetProperty("approvals").EnumerateArray()), "signature"));
        Assert.Equal(ownerBefore, await PingUpdatedAtAsync(owner));
        Assert.True(await PingUpdatedAtAsync(recipient) > recipientBefore);

        // A can still delete it, and the approving trustee's repeat still confirms.
        Assert.Equal("ok", Result(await SendAsync(trustees[0], MollyApiActions.ShareApprove, share.ApproveBody(0, startedAt))));
        Assert.Equal("ok", Result(await SendAsync(owner, MollyApiActions.ShareDelete, share.Entry)));
        Assert.Null(await GetEntryAsync(share));
    }

    [Fact]
    public async Task Reserve_HandsOutFreshEntries_ThatOnlyTheReservingDeviceCanUse_WithinTheHour()
    {
        Party owner = await NewPartyAsync();
        Party recipient = await NewPartyAsync();
        Party trustee = await NewPartyAsync();

        byte[] entry = await ReserveEntryAsync(owner);
        Assert.Equal(16, entry.Length);
        Assert.NotEqual(entry, await ReserveEntryAsync(owner));
        Assert.Equal("rejected", Result(await SendAsync(owner, MollyApiActions.ShareReserve, [0])));

        // Reservations aren't grants yet.
        Assert.Empty((await SyncAsync(owner)).GetProperty("owned").EnumerateArray());
        Assert.Equal("not-found", Result(await SendAsync(owner, MollyApiActions.ShareUpdate, new MollyTestShare(owner.Identity, recipient.Identity, [trustee.Identity], 1, entry: entry).Update())));

        // Neither another device bound to the same identity nor an ENTRY that was never reserved will do.
        Party linked = await NewPartyAsync(bind: false);
        Assert.Equal(MollyIdentityBinding.Bound, (await _client.AssociateAsync(linked.Session, owner.Identity)).Identity);
        var share = new MollyTestShare(owner.Identity, recipient.Identity, [trustee.Identity], 1, entry: entry);
        Assert.Equal("not-found", Result(await SendAsync(linked, MollyApiActions.ShareCreate, share.CreateBody())));
        Assert.Equal("not-found", Result(await SendAsync(owner, MollyApiActions.ShareCreate, new MollyTestShare(owner.Identity, recipient.Identity, [trustee.Identity], 1).CreateBody())));

        Assert.Equal("ok", Result(await SendAsync(owner, MollyApiActions.ShareCreate, share.CreateBody())));
        Assert.Equal("ok", Result(await SendAsync(owner, MollyApiActions.ShareCreate, share.CreateBody())));
        Assert.Single((await SyncAsync(recipient)).GetProperty("received").EnumerateArray());

        // A reservation older than an hour can't be used, even before maintenance drops it.
        var expired = new MollyTestShare(owner.Identity, recipient.Identity, [trustee.Identity], 1, entry: await ReserveEntryAsync(owner));
        Guid expiredId = MollyShareService.ToId(expired.Entry);

        await using (MollyDbContext db = _fixture.DbFactory.CreateDbContext())
        {
            await db.ShareEntries.Where(e => e.Id == expiredId).ExecuteUpdateAsync(e => e.SetProperty(x => x.CreatedAt, DateTime.UtcNow - MollyShareService.ReservationRetention - TimeSpan.FromMinutes(1)));
        }

        Assert.Equal("not-found", Result(await SendAsync(owner, MollyApiActions.ShareCreate, expired.CreateBody())));
        Assert.Equal(MollyShareState.Reserved, (await GetEntryAsync(expired))!.State);

        await _shares.RunMaintenanceAsync();
        Assert.Null(await GetEntryAsync(expired));
        Assert.Equal(MollyShareState.Active, (await GetEntryAsync(share))!.State);
    }

    [Theory]
    [InlineData(MollyShareState.Active)]
    [InlineData(MollyShareState.Started)]
    [InlineData(MollyShareState.Released)]
    public async Task Delete_RemovesTheEntry_InEveryState(MollyShareState state)
    {
        (Party owner, Party recipient, Party[] trustees, MollyTestShare share) = await CreateShareAsync(threshold: 1, count: 2);
        long startedAt = 0;

        if (state != MollyShareState.Active)
        {
            startedAt = await StartAsync(recipient, share);
        }

        if (state == MollyShareState.Released)
        {
            Assert.Equal("ok", Result(await SendAsync(trustees[0], MollyApiActions.ShareApprove, share.ApproveBody(0, startedAt))));
        }

        Assert.Equal("not-found", Result(await SendAsync(recipient, MollyApiActions.ShareDelete, share.Entry)));
        Assert.Equal("ok", Result(await SendAsync(owner, MollyApiActions.ShareDelete, share.Entry)));
        Assert.Equal("not-found", Result(await SendAsync(owner, MollyApiActions.ShareDelete, share.Entry)));

        Assert.Equal("not-found", Result(await SendAsync(recipient, MollyApiActions.ShareGet, share.Entry)));
        Assert.Equal("not-found", Result(await SendAsync(recipient, MollyApiActions.ShareFetch, share.Entry)));
        Assert.Equal("not-found", Result(await SendAsync(recipient, MollyApiActions.ShareStart, share.StartBody())));
        Assert.Equal("not-found", Result(await SendAsync(trustees[1], MollyApiActions.ShareApprove, share.ApproveBody(1, startedAt))));
        Assert.Empty((await SyncAsync(owner)).GetProperty("owned").EnumerateArray());
        Assert.Empty((await SyncAsync(recipient)).GetProperty("received").EnumerateArray());
        Assert.Empty((await SyncAsync(trustees[1])).GetProperty("trusteeRequests").EnumerateArray());

        // A delayed create finds no reservation.
        Assert.Equal("not-found", Result(await SendAsync(owner, MollyApiActions.ShareCreate, share.CreateBody())));
        Assert.Null(await GetEntryAsync(share));

        await using MollyDbContext db = _fixture.DbFactory.CreateDbContext();
        Guid id = MollyShareService.ToId(share.Entry);
        Assert.False(await db.ShareTrustees.AnyAsync(t => t.EntryId == id));
    }

    [Fact]
    public async Task ReleaseAfterDelete_ReleasesNothing()
    {
        (Party owner, Party recipient, Party[] trustees, MollyTestShare share) = await CreateShareAsync(threshold: 1, count: 1);
        long startedAt = await StartAsync(recipient, share);

        Assert.Equal("ok", Result(await SendAsync(owner, MollyApiActions.ShareDelete, share.Entry)));
        Assert.Equal("not-found", Result(await SendAsync(trustees[0], MollyApiActions.ShareApprove, share.ApproveBody(0, startedAt))));
        Assert.Null(await GetEntryAsync(share));
    }

    [Fact]
    public async Task ConcurrentDeleteAndApproval_HaveADefiniteOrder()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            (Party owner, Party recipient, Party[] trustees, MollyTestShare share) = await CreateShareAsync(threshold: 1, count: 1);
            long startedAt = await StartAsync(recipient, share);

            Task<JsonElement> approve = SendAsync(trustees[0], MollyApiActions.ShareApprove, share.ApproveBody(0, startedAt));
            Task<JsonElement> delete = SendAsync(owner, MollyApiActions.ShareDelete, share.Entry);
            await Task.WhenAll(approve, delete);

            // Whichever won, the delete committed and nothing is fetchable afterwards.
            Assert.Equal("ok", Result(await delete));
            Assert.Contains(Result(await approve), new[] { "ok", "not-found" });
            Assert.Equal("not-found", Result(await SendAsync(recipient, MollyApiActions.ShareFetch, share.Entry)));
            Assert.Null(await GetEntryAsync(share));
        }
    }

    [Fact]
    public async Task Maintenance_ExpiresReleasedEntries()
    {
        (Party owner, Party recipient, Party[] trustees, MollyTestShare share) = await CreateShareAsync(threshold: 1, count: 1);
        long startedAt = await StartAsync(recipient, share);
        Assert.Equal("ok", Result(await SendAsync(trustees[0], MollyApiActions.ShareApprove, share.ApproveBody(0, startedAt))));
        Guid id = MollyShareService.ToId(share.Entry);

        await _shares.RunMaintenanceAsync();
        Assert.Equal(MollyShareState.Released, (await GetEntryAsync(share))!.State);

        await using (MollyDbContext db = _fixture.DbFactory.CreateDbContext())
        {
            await db.ShareEntries.Where(e => e.Id == id).ExecuteUpdateAsync(e => e.SetProperty(x => x.ReleasedAt, DateTime.UtcNow - MollyShareService.ReleasedRetention - TimeSpan.FromMinutes(1)));
        }

        long recipientBefore = (await PingUpdatedAtAsync(recipient))!.Value;

        await _shares.RunMaintenanceAsync();
        Assert.Null(await GetEntryAsync(share));
        Assert.True(await PingUpdatedAtAsync(recipient) > recipientBefore);
        Assert.Equal("not-found", Result(await SendAsync(recipient, MollyApiActions.ShareFetch, share.Entry)));
        Assert.Equal("not-found", Result(await SendAsync(owner, MollyApiActions.ShareCreate, share.CreateBody())));
    }

    private async Task<long?> PingUpdatedAtAsync(Party party) => (await _fixture.Service.PingAsync(party.Session, null, null, null, default)).ShareUpdatedAt;

    private async Task<long[]> PingUpdatedAtAsync(Party[] parties)
    {
        var updatedAt = new long[parties.Length];

        for (int i = 0; i < parties.Length; i++)
        {
            updatedAt[i] = (await PingUpdatedAtAsync(parties[i]))!.Value;
        }

        return updatedAt;
    }

    [Fact]
    public async Task Pings_CarryTheCurrentIdentitysUpdateHint()
    {
        Party owner = await NewPartyAsync();
        Party recipient = await NewPartyAsync(bind: false);
        Party[] trustees = await NewPartiesAsync(3);

        Assert.Null(await PingUpdatedAtAsync(recipient));

        // Bound by associate, with nothing affecting it yet.
        Assert.Equal(MollyIdentityBinding.Bound, (await _client.AssociateAsync(recipient.Session, recipient.Identity)).Identity);
        Assert.Equal(0, await PingUpdatedAtAsync(recipient));
        Assert.Equal(0, (await SyncAsync(recipient)).GetProperty("updatedAt").GetInt64());

        // Create affects B only.
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var share = new MollyTestShare(owner.Identity, recipient.Identity, [.. trustees.Select(t => t.Identity)], 2);
        Assert.Equal("ok", await CreateAsync(owner, share));
        long recipientCreated = (await PingUpdatedAtAsync(recipient))!.Value;
        Assert.InRange(recipientCreated, before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(0, await PingUpdatedAtAsync(owner));

        // Sync reports the same hint as pings.
        Assert.Equal(recipientCreated, (await SyncAsync(recipient)).GetProperty("updatedAt").GetInt64());

        // Start affects A, B and every trustee.
        long startedAt = await StartAsync(recipient, share);
        long ownerStarted = (await PingUpdatedAtAsync(owner))!.Value;
        long recipientStarted = (await PingUpdatedAtAsync(recipient))!.Value;
        long[] trusteesStarted = await PingUpdatedAtAsync(trustees);
        Assert.True(ownerStarted > 0);
        Assert.True(recipientStarted > recipientCreated);
        Assert.All(trusteesStarted, t => Assert.True(t > 0));

        // An approval below the threshold affects A and the approving trustee.
        Assert.Equal("ok", Result(await SendAsync(trustees[0], MollyApiActions.ShareApprove, share.ApproveBody(0, startedAt))));
        long ownerApproved = (await PingUpdatedAtAsync(owner))!.Value;
        long firstApproved = (await PingUpdatedAtAsync(trustees[0]))!.Value;
        Assert.True(ownerApproved > ownerStarted);
        Assert.True(firstApproved > trusteesStarted[0]);
        Assert.Equal(recipientStarted, await PingUpdatedAtAsync(recipient));
        Assert.Equal(trusteesStarted[1], await PingUpdatedAtAsync(trustees[1]));
        Assert.Equal(trusteesStarted[2], await PingUpdatedAtAsync(trustees[2]));

        // The releasing approval is one committed change affecting A, B, the approving trustee and those that haven't approved.
        Assert.Equal("ok", Result(await SendAsync(trustees[1], MollyApiActions.ShareApprove, share.ApproveBody(1, startedAt))));
        long ownerReleased = (await PingUpdatedAtAsync(owner))!.Value;
        long recipientReleased = (await PingUpdatedAtAsync(recipient))!.Value;
        long[] trusteesReleased = await PingUpdatedAtAsync(trustees);
        Assert.True(ownerReleased > ownerApproved);
        Assert.True(recipientReleased > recipientStarted);
        Assert.Equal(firstApproved, trusteesReleased[0]);
        Assert.True(trusteesReleased[1] > trusteesStarted[1]);
        Assert.True(trusteesReleased[2] > trusteesStarted[2]);

        // A hint never repeats or goes backwards, even if the clock is behind it.
        long future = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();

        await using (MollyDbContext db = _fixture.DbFactory.CreateDbContext())
        {
            byte[] recipientHash = PartyHashOf(recipient.Identity);
            Assert.Equal(1, await db.ShareUpdates.Where(u => u.PartyHash == recipientHash).ExecuteUpdateAsync(u => u.SetProperty(x => x.UpdatedAt, future)));
        }

        // Delete of a released share affects B only.
        Assert.Equal("ok", Result(await SendAsync(owner, MollyApiActions.ShareDelete, share.Entry)));
        Assert.Equal(future + 1, await PingUpdatedAtAsync(recipient));
        Assert.Equal(ownerReleased, await PingUpdatedAtAsync(owner));
        Assert.Equal(trusteesReleased, await PingUpdatedAtAsync(trustees));

        // A wipe unbinds the entry, and the wiped entry can't be bound again.
        Guid entryId = _fixture.Unprotect(recipient.Session);
        await _fixture.Service.RequestWipeAsync(entryId);
        Assert.Equal((null, null), await GetBindingAsync(entryId));
        Assert.Equal(MollyCommandResult.Blocked(MollyCommand.Wipe), await _client.AssociateAsync(recipient.Session, recipient.Identity));
        Assert.Equal((null, null), await GetBindingAsync(entryId));
    }

    [Fact]
    public async Task DeviceInfo_SummarizesEachDevice_AndAdminDeleteRemovesAGrant()
    {
        (Party owner, Party recipient, Party[] trustees, MollyTestShare share) = await CreateShareAsync(threshold: 2, count: 3);
        long startedAt = await StartAsync(recipient, share);
        Assert.Equal("ok", Result(await SendAsync(trustees[0], MollyApiActions.ShareApprove, share.ApproveBody(0, startedAt))));
        await ReserveEntryAsync(owner);

        Party linked = await NewPartyAsync(bind: false);
        Assert.Equal(MollyIdentityBinding.Bound, (await _client.AssociateAsync(linked.Session, recipient.Identity)).Identity);

        Guid ownerId = _fixture.Unprotect(owner.Session);
        Guid recipientId = _fixture.Unprotect(recipient.Session);
        Guid linkedId = _fixture.Unprotect(linked.Session);

        Dictionary<Guid, MollyShareDeviceInfo> info = await _shares.GetDeviceInfoAsync();

        MollyShareDeviceInfo ownerInfo = info[ownerId];
        Assert.NotNull(ownerInfo.IdentityBoundAt);
        Assert.Equal(1, ownerInfo.Reservations);
        Assert.Empty(ownerInfo.SameIdentityDevices);

        MollyShareOwnedInfo owned = Assert.Single(ownerInfo.Owned);
        Assert.Equal((MollyShareService.ToId(share.Entry), MollyShareState.Started, 2, 3, 1), (owned.Id, owned.State, owned.Threshold, owned.Trustees, owned.Approvals));
        DateTime expected = DateTime.UtcNow + MollyShareService.OwnerInactivityRetention;
        Assert.InRange(owned.ExpiresAt!.Value, expected - TimeSpan.FromMinutes(1), expected);

        // Received counts and trustee requests follow the identity, on every device bound to it.
        foreach ((Guid device, Guid other) in new[] { (recipientId, linkedId), (linkedId, recipientId) })
        {
            Assert.Equal((0, 1, 0), (info[device].ReceivedAvailable, info[device].ReceivedStarted, info[device].ReceivedReleased));
            Assert.Equal([other], info[device].SameIdentityDevices);
            Assert.Empty(info[device].Owned);
        }

        Assert.Equal(0, info[_fixture.Unprotect(trustees[0].Session)].PendingTrusteeRequests);
        Assert.Equal(1, info[_fixture.Unprotect(trustees[1].Session)].PendingTrusteeRequests);

        long recipientBefore = (await PingUpdatedAtAsync(recipient))!.Value;

        Assert.True(await _shares.AdminDeleteAsync(owned.Id));
        Assert.False(await _shares.AdminDeleteAsync(owned.Id));
        Assert.Null(await GetEntryAsync(share));
        Assert.True(await PingUpdatedAtAsync(recipient) > recipientBefore);

        info = await _shares.GetDeviceInfoAsync();
        Assert.Empty(info[ownerId].Owned);
        Assert.Equal(0, info[_fixture.Unprotect(trustees[1].Session)].PendingTrusteeRequests);
    }

    [Fact]
    public async Task Login_CantMakeTheServerStoreAnAtRestKey()
    {
        (_, _, _, MollyTestShare share) = await CreateShareAsync(threshold: 1, count: 1);

        // Login stores HMAC-SHA512 of any client-chosen key hash under the database key.
        byte[] keyHash = [.. MollyShareCrypto.Prefix, .. "at-rest"u8, .. share.Entry];
        MollyLoginResult login = await _fixture.LoginAsync(Convert.ToBase64String(keyHash));
        Assert.Equal(MollyResultStatus.Ok, login.Status);

        byte[] derivedHash;

        await using (MollyDbContext db = _fixture.DbFactory.CreateDbContext())
        {
            Guid entryId = _fixture.Unprotect(login.ProtectedId!);
            derivedHash = (await db.Entries.SingleAsync(e => e.Id == entryId)).DerivedHash;
        }

        MollyShareDbEntry stored = (await GetEntryAsync(share))!;
        const byte SkeyField = 4;

        using var aead = new XAesGcm(derivedHash.AsSpan(0, XAesGcm.KeySizeInBytes));
        Assert.False(aead.TryDecrypt(stored.EncryptedSkey!, out _, [.. share.Entry, SkeyField]));
    }

    private static byte[] PartyHashOf(MollyTestIdentity identity) => MollyShareCrypto.ComputePartyHash(MollyShareCrypto.DeriveServerKey(MollyTestKeys.DatabaseKeyBytes), identity.Aci, identity.Key);

    private async Task<(byte[]? PartyHash, DateTime? BoundAt)> GetBindingAsync(Guid entryId)
    {
        await using MollyDbContext db = _fixture.DbFactory.CreateDbContext();
        MollyDbEntry entry = await db.Entries.AsNoTracking().SingleAsync(e => e.Id == entryId);
        return (entry.SharePartyHash, entry.ShareIdentityBoundAt);
    }

    /// <summary>Moves the entry's binding time back by <paramref name="age"/>, as if it was bound that long ago.</summary>
    private async Task AgeBindingAsync(Guid entryId, TimeSpan age)
    {
        await using MollyDbContext db = _fixture.DbFactory.CreateDbContext();
        MollyDbEntry entry = await db.Entries.SingleAsync(e => e.Id == entryId);
        entry.ShareIdentityBoundAt -= age;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Sessions_FollowTheUsualRules_AndBlockedRequestsPerformNothing()
    {
        Party owner = await NewPartyAsync();
        Party recipient = await NewPartyAsync();
        Party trustee = await NewPartyAsync();
        var share = new MollyTestShare(owner.Identity, recipient.Identity, [trustee.Identity], 1);

        Assert.Equal("invalid", Status(await SendAsync(owner with { Session = "not-a-token" }, MollyApiActions.ShareSync, [])));

        await ReserveAsync(owner, share);
        await _fixture.Service.SetLockRequestedAsync(_fixture.Unprotect(owner.Session), true);
        JsonElement blocked = await SendAsync(owner, MollyApiActions.ShareCreate, share.CreateBody());
        Assert.Equal("command", Status(blocked));
        Assert.Equal("lock", Data(blocked).GetProperty("command").GetString());
        Assert.Equal(MollyShareState.Reserved, (await GetEntryAsync(share))!.State);
    }

    [Fact]
    public async Task Associate_BindsTheFirstIdentity_AndRebindsOnlyAfterTheCooldown()
    {
        Party party = await NewPartyAsync(bind: false);
        Guid entryId = _fixture.Unprotect(party.Session);
        using var other = new MollyTestIdentity();

        Assert.Equal(MollyCommandResult.Ok() with { Identity = MollyIdentityBinding.Bound }, await _client.AssociateAsync(party.Session, party.Identity));
        (byte[]? firstHash, DateTime? firstBoundAt) = await GetBindingAsync(entryId);
        Assert.Equal(PartyHashOf(party.Identity), firstHash);
        Assert.NotNull(firstBoundAt);

        // Re-associating confirms the binding without touching it, and the nickname still applies either way.
        Assert.Equal(MollyIdentityBinding.Bound, (await _client.AssociateAsync(party.Session, party.Identity, "renamed")).Identity);
        Assert.Equal(MollyCommandResult.Ok() with { Identity = MollyIdentityBinding.Cooldown }, await _client.AssociateAsync(party.Session, other, "renamed-again"));
        (byte[]? unchangedHash, DateTime? unchangedBoundAt) = await GetBindingAsync(entryId);
        Assert.Equal(firstHash, unchangedHash);
        Assert.Equal(firstBoundAt, unchangedBoundAt);
        Assert.Equal("renamed-again", Assert.Single(await _fixture.Service.GetRegisteredUsersAsync(), u => u.Id == entryId).Nickname);

        // Share requests act as the current identity, so a grant of the other identity can't be created.
        Assert.Equal("ok", Result(await SendAsync(party, MollyApiActions.ShareSync, [])));
        Party recipient = await NewPartyAsync();
        Party trustee = await NewPartyAsync();
        var share = new MollyTestShare(other, recipient.Identity, [trustee.Identity], 1);
        Assert.Equal("rejected", Result(await SendAsync(party, MollyApiActions.ShareCreate, share.CreateBody())));
        Assert.Null(await GetEntryAsync(share));

        // Once the cooldown has passed, a different identity replaces the current one.
        await AgeBindingAsync(entryId, MollyService.IdentityRebindCooldown + TimeSpan.FromMinutes(1));
        Assert.Equal(MollyCommandResult.Ok() with { Identity = MollyIdentityBinding.Bound }, await _client.AssociateAsync(party.Session, other));
        (byte[]? secondHash, DateTime? secondBoundAt) = await GetBindingAsync(entryId);
        Assert.Equal(PartyHashOf(other), secondHash);
        Assert.True(secondBoundAt > firstBoundAt);

        Assert.Equal("ok", await CreateAsync(party, share));
        var previous = new MollyTestShare(party.Identity, recipient.Identity, [trustee.Identity], 1);
        Assert.Equal("rejected", Result(await SendAsync(party, MollyApiActions.ShareCreate, previous.CreateBody())));

        // Switching straight back is subject to the cooldown again.
        Assert.Equal(MollyIdentityBinding.Cooldown, (await _client.AssociateAsync(party.Session, party.Identity)).Identity);

        // A locked entry reports the lock and isn't bound.
        Party locked = await NewPartyAsync(bind: false);
        await _fixture.Service.SetLockRequestedAsync(_fixture.Unprotect(locked.Session), true);
        Assert.Equal(MollyCommandResult.Blocked(MollyCommand.Lock), await _client.AssociateAsync(locked.Session, locked.Identity));
        Assert.Equal((null, null), await GetBindingAsync(_fixture.Unprotect(locked.Session)));
    }

    [Fact]
    public async Task OneIdentity_CanBeBoundToSeveralEntries_WhichOnlyManageTheirOwnGrants()
    {
        Party primary = await NewPartyAsync();
        Party linked = await NewPartyAsync(bind: false);
        linked = linked with { Identity = primary.Identity };
        Assert.Equal(MollyIdentityBinding.Bound, (await _client.AssociateAsync(linked.Session, primary.Identity)).Identity);

        Party recipient = await NewPartyAsync();
        var share = new MollyTestShare(primary.Identity, recipient.Identity, [(await NewPartyAsync()).Identity], 1);
        Assert.Equal("ok", await CreateAsync(linked, share));

        // Only the creating device lists and manages the grant.
        Assert.Empty((await SyncAsync(primary)).GetProperty("owned").EnumerateArray());
        Assert.Single((await SyncAsync(linked)).GetProperty("owned").EnumerateArray());
        Assert.Equal("not-found", Result(await SendAsync(primary, MollyApiActions.ShareDelete, share.Entry)));

        // Both carry the identity's update hint, which the owner's updates raise too.
        await StartAsync(recipient, share);
        Assert.True(await PingUpdatedAtAsync(linked) > 0);
        Assert.Equal(await PingUpdatedAtAsync(primary), await PingUpdatedAtAsync(linked));

        Assert.Equal("ok", Result(await SendAsync(linked, MollyApiActions.ShareDelete, share.Entry)));
    }

    [Fact]
    public async Task OwnedGrants_StayWithTheCreatingDevice_AcrossIdentityChanges()
    {
        Party party = await NewPartyAsync();
        Guid entryId = _fixture.Unprotect(party.Session);
        Party recipient = await NewPartyAsync();
        Party trustee = await NewPartyAsync();
        var kept = new MollyTestShare(party.Identity, recipient.Identity, [trustee.Identity], 1);
        var deleted = new MollyTestShare(party.Identity, recipient.Identity, [trustee.Identity], 1);
        var received = new MollyTestShare(recipient.Identity, party.Identity, [trustee.Identity], 1);
        Assert.Equal("ok", await CreateAsync(party, kept));
        Assert.Equal("ok", await CreateAsync(party, deleted));
        Assert.Equal("ok", await CreateAsync(recipient, received));
        long startedAt = await StartAsync(recipient, kept);

        // The device's Signal identity changes.
        var newIdentity = new MollyTestIdentity();
        _disposables.Add(newIdentity);
        await AgeBindingAsync(entryId, MollyService.IdentityRebindCooldown + TimeSpan.FromMinutes(1));
        Assert.Equal(MollyIdentityBinding.Bound, (await _client.AssociateAsync(party.Session, newIdentity)).Identity);
        Party current = party with { Identity = newIdentity };

        var own = new MollyTestShare(newIdentity, recipient.Identity, [trustee.Identity], 1);
        Assert.Equal("ok", await CreateAsync(current, own));

        // The device keeps managing everything it created, and the owner's updates now reach its new identity.
        Assert.Equal(3, (await SyncAsync(current)).GetProperty("owned").GetArrayLength());
        Assert.Equal(0, await PingUpdatedAtAsync(current));
        Assert.Equal("ok", Result(await SendAsync(trustee, MollyApiActions.ShareApprove, kept.ApproveBody(0, startedAt))));
        Assert.True(await PingUpdatedAtAsync(current) > 0);

        JsonElement keptItem = Assert.Single((await SyncAsync(current)).GetProperty("owned").EnumerateArray(), o => o.GetProperty("entryId").GetString() == Convert.ToBase64String(kept.Entry));
        Assert.Equal("released", keptItem.GetProperty("state").GetString());
        Assert.Single(keptItem.GetProperty("approvals").EnumerateArray());

        Assert.Equal("ok", Result(await SendAsync(current, MollyApiActions.ShareDelete, deleted.Entry)));
        Assert.Null(await GetEntryAsync(deleted));

        // It can no longer act as its former identity: no new grants, and grants to it aren't its own any more.
        var another = new MollyTestShare(party.Identity, recipient.Identity, [trustee.Identity], 1);
        Assert.Equal("rejected", Result(await SendAsync(current, MollyApiActions.ShareCreate, another.CreateBody())));
        Assert.Null(await GetEntryAsync(another));
        Assert.Empty((await SyncAsync(current)).GetProperty("received").EnumerateArray());
        Assert.Equal("not-found", Result(await SendAsync(current, MollyApiActions.ShareGet, received.Entry)));
    }

    [Fact]
    public async Task Maintenance_DeletesGrantsOfDeletedOrWipedDevices()
    {
        Party owner = await NewPartyAsync();
        Party wiped = await NewPartyAsync();
        Party deleted = await NewPartyAsync();
        Party recipient = await NewPartyAsync();
        Party[] trustees = await NewPartiesAsync(2);
        var keptShare = new MollyTestShare(owner.Identity, recipient.Identity, [trustees[0].Identity], 1);
        var wipedShare = new MollyTestShare(wiped.Identity, recipient.Identity, [.. trustees.Select(t => t.Identity)], 1);
        var deletedShare = new MollyTestShare(deleted.Identity, recipient.Identity, [trustees[0].Identity], 1);
        Assert.Equal("ok", await CreateAsync(owner, keptShare));
        Assert.Equal("ok", await CreateAsync(wiped, wipedShare));
        Assert.Equal("ok", await CreateAsync(deleted, deletedShare));
        await StartAsync(recipient, wipedShare);

        // Another device bound to the same identity doesn't keep a wiped device's grants.
        Party linked = await NewPartyAsync(bind: false);
        Assert.Equal(MollyIdentityBinding.Bound, (await _client.AssociateAsync(linked.Session, wiped.Identity)).Identity);

        await _fixture.Service.RequestWipeAsync(_fixture.Unprotect(wiped.Session));
        await _fixture.Service.DeleteEntryAsync(_fixture.Unprotect(deleted.Session));

        long recipientBefore = (await PingUpdatedAtAsync(recipient))!.Value;
        long trusteeBefore = (await PingUpdatedAtAsync(trustees[1]))!.Value;

        await _shares.RunMaintenanceAsync();

        Assert.Equal(MollyShareState.Active, (await GetEntryAsync(keptShare))!.State);
        Assert.Null(await GetEntryAsync(wipedShare));
        Assert.Null(await GetEntryAsync(deletedShare));

        // Deleted like share-delete: B's and the started attempt's trustees' hints are raised.
        Assert.True(await PingUpdatedAtAsync(recipient) > recipientBefore);
        Assert.True(await PingUpdatedAtAsync(trustees[1]) > trusteeBefore);
    }

    [Fact]
    public async Task Update_ReplacesTheRosterOfAnActiveGrant_AndOldRostersCanNoLongerStart()
    {
        (Party owner, Party recipient, Party[] trustees, MollyTestShare share) = await CreateShareAsync(threshold: 1, count: 2);
        byte[] createBody = share.CreateBody();
        byte[] staleStart = share.StartBody();
        byte[] metadataSealed = share.MetadataSealed;
        Party replacement = await NewPartyAsync();
        long recipientBefore = (await PingUpdatedAtAsync(recipient))!.Value;

        byte[] update = share.Update([trustees[0].Identity, replacement.Identity], threshold: 2);
        Assert.Equal("ok", Result(await SendAsync(owner, MollyApiActions.ShareUpdate, update)));
        Assert.True(await PingUpdatedAtAsync(recipient) > recipientBefore);

        // Identical retries are fine, the replaced create isn't, and only the owner device may update.
        Assert.Equal("ok", Result(await SendAsync(owner, MollyApiActions.ShareUpdate, update)));
        Assert.Equal("rejected", Result(await SendAsync(owner, MollyApiActions.ShareCreate, createBody)));
        Assert.Equal("not-found", Result(await SendAsync(recipient, MollyApiActions.ShareUpdate, update)));

        Assert.Equal(1, Assert.Single((await SyncAsync(owner)).GetProperty("owned").EnumerateArray()).GetProperty("revision").GetInt64());
        JsonElement get = Data(await SendAsync(recipient, MollyApiActions.ShareGet, share.Entry));
        Assert.Equal(share.Descriptor, Bytes(get, "descriptor"));
        Assert.Equal(share.GrantSignature, Bytes(get, "signature"));
        Assert.Equal(share.RosterSealed, Bytes(get, "rosterSealed"));
        Assert.Equal(metadataSealed, Bytes(get, "metadataSealed"));

        // The old roster was encrypted under the replaced SKEY.
        Assert.Equal("rejected", Result(await SendAsync(recipient, MollyApiActions.ShareStart, staleStart)));
        Assert.Equal(MollyShareState.Active, (await GetEntryAsync(share))!.State);

        long startedAt = await StartAsync(recipient, share);
        Assert.Single((await SyncAsync(replacement)).GetProperty("trusteeRequests").EnumerateArray());
        Assert.Empty((await SyncAsync(trustees[1])).GetProperty("trusteeRequests").EnumerateArray());

        // Once started, the roster is fixed.
        Assert.Equal("ok", Result(await SendAsync(trustees[0], MollyApiActions.ShareApprove, share.ApproveBody(0, startedAt))));
        byte[] secondApproval = share.ApproveBody(1, startedAt);
        Assert.Equal("not-found", Result(await SendAsync(owner, MollyApiActions.ShareUpdate, share.Update())));

        Assert.Equal("ok", Result(await SendAsync(replacement, MollyApiActions.ShareApprove, secondApproval)));
        Assert.Equal(share.DataKeySealed, Bytes(Data(await SendAsync(recipient, MollyApiActions.ShareFetch, share.Entry)), "dataKeySealed"));
    }

    [Fact]
    public async Task Update_RejectsSkippedRevisionsChangedContentAndAFormerIdentity()
    {
        (Party owner, Party recipient, Party[] trustees, MollyTestShare share) = await CreateShareAsync(threshold: 1, count: 1);

        byte[] currentSkey = share.Skey;
        byte[] next = share.Update();
        Assert.Equal("rejected", Result(await SendAsync(owner, MollyApiActions.ShareUpdate, [.. next[..^32], .. currentSkey])));

        // Descriptor offsets of the metadata, file and DATA_KEY_SEALED hashes, which an update must keep.
        foreach (int offset in (int[])[1 + 120 + (2 * 68) + 4, 1 + 120 + (3 * 68) + 4, 1 + 120 + (4 * 68) + 4])
        {
            byte[] changed = [.. next];
            changed[offset] ^= 1;
            Assert.Equal("rejected", Result(await SendAsync(owner, MollyApiActions.ShareUpdate, changed)));
        }

        byte[] skipped = share.Update();
        Assert.Equal("rejected", Result(await SendAsync(owner, MollyApiActions.ShareUpdate, skipped)));
        Assert.Equal("ok", Result(await SendAsync(recipient, MollyApiActions.ShareGet, share.Entry)));
        Assert.Equal(0, Assert.Single((await SyncAsync(owner)).GetProperty("owned").EnumerateArray()).GetProperty("revision").GetInt64());

        // After an identity change the device still owns the grant, but it can no longer update it as A.
        var newIdentity = new MollyTestIdentity();
        _disposables.Add(newIdentity);
        await AgeBindingAsync(_fixture.Unprotect(owner.Session), MollyService.IdentityRebindCooldown + TimeSpan.FromMinutes(1));
        Assert.Equal(MollyIdentityBinding.Bound, (await _client.AssociateAsync(owner.Session, newIdentity)).Identity);

        Assert.Equal("rejected", Result(await SendAsync(owner, MollyApiActions.ShareUpdate, next)));
        Assert.Equal("ok", Result(await SendAsync(owner, MollyApiActions.ShareDelete, share.Entry)));
    }

    [Fact]
    public async Task Maintenance_ExpiresGrantsOfInactiveOwners_ButGivesStartedRecoveriesTheirPeriod()
    {
        Party owner = await NewPartyAsync();
        Party recipient = await NewPartyAsync();
        Party trustee = await NewPartyAsync();
        var idle = new MollyTestShare(owner.Identity, recipient.Identity, [trustee.Identity], 1);
        var startedRecently = new MollyTestShare(owner.Identity, recipient.Identity, [trustee.Identity], 1);
        var startedLongAgo = new MollyTestShare(owner.Identity, recipient.Identity, [trustee.Identity], 1);
        var released = new MollyTestShare(owner.Identity, recipient.Identity, [trustee.Identity], 1);

        foreach (MollyTestShare share in (MollyTestShare[])[idle, startedRecently, startedLongAgo, released])
        {
            Assert.Equal("ok", await CreateAsync(owner, share));
        }

        await StartAsync(recipient, startedRecently);
        await StartAsync(recipient, startedLongAgo);
        Assert.Equal("ok", Result(await SendAsync(trustee, MollyApiActions.ShareApprove, released.ApproveBody(0, await StartAsync(recipient, released)))));

        // Another device that keeps using the app keeps its grant.
        Party active = await NewPartyAsync();
        var kept = new MollyTestShare(active.Identity, recipient.Identity, [trustee.Identity], 1);
        Assert.Equal("ok", await CreateAsync(active, kept));

        TimeSpan longAgo = MollyShareService.OwnerInactivityRetention + TimeSpan.FromMinutes(1);
        await AgeOwnerActivityAsync(owner, longAgo);
        await AgeStartAsync(startedLongAgo, longAgo);
        long recipientBefore = (await PingUpdatedAtAsync(recipient))!.Value;

        await _shares.RunMaintenanceAsync();

        Assert.Null(await GetEntryAsync(idle));
        Assert.Null(await GetEntryAsync(startedLongAgo));
        Assert.Equal(MollyShareState.Started, (await GetEntryAsync(startedRecently))!.State);
        Assert.Equal(MollyShareState.Released, (await GetEntryAsync(released))!.State);
        Assert.Equal(MollyShareState.Active, (await GetEntryAsync(kept))!.State);
        Assert.True(await PingUpdatedAtAsync(recipient) > recipientBefore);

        // Any share request from the owner device counts as activity again.
        await SyncAsync(owner);
        await AgeStartAsync(startedRecently, longAgo);
        await _shares.RunMaintenanceAsync();
        Assert.Equal(MollyShareState.Started, (await GetEntryAsync(startedRecently))!.State);
    }

    private async Task AgeOwnerActivityAsync(Party owner, TimeSpan age)
    {
        Guid id = _fixture.Unprotect(owner.Session);

        await using MollyDbContext db = _fixture.DbFactory.CreateDbContext();
        MollyDbEntry entry = await db.Entries.SingleAsync(e => e.Id == id);
        entry.ShareActiveAt -= age;
        await db.SaveChangesAsync();
    }

    private async Task AgeStartAsync(MollyTestShare share, TimeSpan age)
    {
        await using MollyDbContext db = _fixture.DbFactory.CreateDbContext();
        Guid id = MollyShareService.ToId(share.Entry);
        MollyShareDbEntry entry = await db.ShareEntries.SingleAsync(e => e.Id == id);
        entry.StartedAt -= age;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Associate_WithAnUnverifiedIdentity_BindsNothing()
    {
        Party party = await NewPartyAsync(bind: false);
        using var other = new MollyTestIdentity();
        byte[] transportPublicKey = Convert.FromBase64String(_protector.GetTransportKey().PublicKey);

        async Task<MollyCommandResult> AssociateAsync(Func<MollyExchangeContext, MollyAssociateIdentity> createIdentity, MollyExchangeContext? exchange = null)
        {
            exchange ??= _client.NewExchange(transportPublicKey);
            var request = new MollyAssociateRequest { Id = party.Session, Nickname = "unverified", Identity = createIdentity(exchange) };
            return await _shares.AssociateAsync(request, exchange, default);
        }

        MollyAssociateIdentity Proof(MollyExchangeContext exchange, string action = MollyApiActions.Associate, byte[]? sealedTo = null) =>
            MollyShareTestClient.CreateIdentityProof(party.Identity, exchange, sealedTo ?? transportPublicKey, action);

        Assert.Equal(MollyCommandResult.Invalid, await AssociateAsync(e => new MollyAssociateIdentity { Aci = Proof(e).Aci, IdentityKey = Proof(e).IdentityKey, Mac = MollyShareTestClient.Base64(new byte[64]) }));
        Assert.Equal(MollyCommandResult.Invalid, await AssociateAsync(e => new MollyAssociateIdentity { Aci = MollyShareTestClient.Base64(other.Aci), IdentityKey = Proof(e).IdentityKey, Mac = Proof(e).Mac }));
        Assert.Equal(MollyCommandResult.Invalid, await AssociateAsync(e => new MollyAssociateIdentity { Aci = Proof(e).Aci, IdentityKey = MollyShareTestClient.Base64(other.Key), Mac = Proof(e).Mac }));
        Assert.Equal(MollyCommandResult.Invalid, await AssociateAsync(e => new MollyAssociateIdentity { Aci = Proof(e).Aci, IdentityKey = null, Mac = Proof(e).Mac }));
        Assert.Equal(MollyCommandResult.Invalid, await AssociateAsync(e => Proof(e, action: MollyApiActions.ShareSync)));
        Assert.Equal(MollyCommandResult.Invalid, await AssociateAsync(e => Proof(_client.NewExchange(transportPublicKey))));

        // A MAC for the discovery key is rejected, even though it's correct for that key.
        Assert.Equal(MollyCommandResult.Invalid, await AssociateAsync(e => Proof(e, sealedTo: MollyTestKeys.TransportPublicKeyBytes), _client.NewExchange(MollyTestKeys.TransportPublicKeyBytes)));

        // Identity keys are 0x05-prefixed X25519 keys.
        byte[] wrongType = [0x06, .. party.Identity.Key[1..]];
        Assert.Equal(MollyCommandResult.Invalid, await AssociateAsync(e => new MollyAssociateIdentity { Aci = Proof(e).Aci, IdentityKey = MollyShareTestClient.Base64(wrongType), Mac = Proof(e).Mac }));

        Assert.Null((await GetBindingAsync(_fixture.Unprotect(party.Session))).PartyHash);

        // Nothing else was applied either: the entry still has no nickname.
        Assert.DoesNotContain(await _fixture.Service.GetRegisteredUsersAsync(), u => u.Id == _fixture.Unprotect(party.Session));

        // An associate without an identity is malformed.
        var anonymous = new MollyAssociateRequest { Id = party.Session, Nickname = "anonymous" };
        Assert.Equal(MollyCommandResult.Invalid, await _shares.AssociateAsync(anonymous, _client.NewExchange(transportPublicKey), default));

        // Share requests from an unbound entry perform nothing.
        Party recipient = await NewPartyAsync();
        var share = new MollyTestShare(party.Identity, recipient.Identity, [(await NewPartyAsync()).Identity], 1);
        Assert.Equal("unbound", Result(await SendAsync(party, MollyApiActions.ShareCreate, share.CreateBody())));
        Assert.Equal("unbound", Result(await SendAsync(party, MollyApiActions.ShareSync, [])));
        Assert.Null(await GetEntryAsync(share));

        Assert.Equal(MollyIdentityBinding.Bound, (await AssociateAsync(e => Proof(e))).Identity);
        Assert.Equal("ok", await CreateAsync(party, share));
    }

    [Fact]
    public async Task Sync_ReadsOneSnapshot_EvenIfAWriteCommitsBetweenItsQueries()
    {
        (Party owner, Party recipient, Party[] trustees, MollyTestShare share) = await CreateShareAsync(threshold: 2, count: 2);
        long startedAt = await StartAsync(recipient, share);
        Assert.Equal("ok", Result(await SendAsync(trustees[0], MollyApiActions.ShareApprove, share.ApproveBody(0, startedAt))));

        string connectionString;

        await using (MollyDbContext db = _fixture.DbFactory.CreateDbContext())
        {
            connectionString = db.Database.GetConnectionString()!;
        }

        // Right before sync's second query, another connection removes the approval. Within one snapshot
        // the write can't land in between (on SQLite it waits for the read to finish).
        var interceptor = new BeforeCommandInterceptor(2, () =>
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM mollyShareTrustees WHERE EntryId = $id";
            command.Parameters.AddWithValue("$id", MollyShareService.ToId(share.Entry).ToString().ToUpperInvariant());
            command.ExecuteNonQuery();
        });

        var factory = new PooledDbContextFactory<MollyDbContext>(new DbContextOptionsBuilder<MollyDbContext>().UseSqlite(connectionString).AddInterceptors(interceptor).Options);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Molly:DatabaseKey"] = MollyTestKeys.DatabaseKey })
            .Build();
        var client = new MollyShareTestClient(new MollyShareService(factory, NullLogger<MollyShareService>.Instance, _fixture.Service, _protector, configuration), _protector);

        JsonElement response = await client.SendAsync(owner.Session, MollyApiActions.ShareSync, []);
        await interceptor.Write!;

        JsonElement owned = Assert.Single(Data(response).GetProperty("owned").EnumerateArray());
        Assert.Equal("started", owned.GetProperty("state").GetString());
        Assert.Single(owned.GetProperty("approvals").EnumerateArray());

        // The write did happen, just not in the middle of the snapshot.
        Assert.Empty(Assert.Single((await SyncAsync(owner)).GetProperty("owned").EnumerateArray()).GetProperty("approvals").EnumerateArray());
    }

    /// <summary>Starts a write on another connection before the n-th command and gives it a moment to commit.</summary>
    private sealed class BeforeCommandInterceptor(int commandNumber, Action write) : DbCommandInterceptor
    {
        private int _commands;

        public Task? Write { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _commands) == commandNumber)
            {
                Write = Task.Run(write, CancellationToken.None);
                await Task.WhenAny(Write, Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None));
            }

            return result;
        }
    }

    [Fact]
    public async Task ShareRequests_NeedAUsableSessionAndABase64Body()
    {
        Party party = await NewPartyAsync();

        Task<JsonElement> SendAsync(string? id, string? body) => _client.SendAsync(MollyApiActions.ShareSync, new MollyShareRequest { Id = id, Body = body });

        Assert.Equal("ok", Result(await SendAsync(party.Session, "")));
        Assert.Equal("rejected", Result(await SendAsync(party.Session, "AA==")));
        Assert.Equal("invalid", Status(await SendAsync(party.Session, null)));
        Assert.Equal("invalid", Status(await SendAsync(party.Session, "not base64")));
        Assert.Equal("invalid", Status(await SendAsync(null, "")));
        Assert.Equal("invalid", Status(await SendAsync(party.Session[..^4] + "AAAA", "")));
    }
}
