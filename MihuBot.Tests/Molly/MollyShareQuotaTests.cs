using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MihuBot.DB;
using MihuBot.Molly;
using MihuBot.Molly.Api;
using MihuBot.Molly.Sharing;

namespace MihuBot.Tests.Molly;

public sealed class MollyShareQuotaTests(MollyServiceFixture fixture) : MollyShareServiceTestBase(fixture)
{
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
        Party[] owners = await NewPartiesAsync(3);

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

        // B is full, whoever shares with it. Grants from many other devices are seeded directly, as creating each
        // through the API is slow; the last one still is.
        int received = MollyShareService.MaxActivePerRecipient + 1;
        await SeedReceivedGrantsAsync(owners[2], recipient, trustee, MollyShareService.MaxReceivedEntries - 1 - received);
        Assert.Equal("ok", await CreateAsync(owners[1], Share(owners[1])));

        Assert.Equal(MollyShareService.MaxReceivedEntries, (await SyncAsync(recipient)).GetProperty("received").GetArrayLength());

        MollyTestShare late = Share(owners[2]);
        Assert.Equal("limit", await CreateAsync(owners[2], late));

        // Dismissing frees a slot, and the reservation is still usable.
        Assert.Equal("ok", Result(await SendAsync(recipient, MollyApiActions.ShareDismiss, own[1].Entry)));
        Assert.Equal("ok", Result(await SendAsync(owners[2], MollyApiActions.ShareCreate, late.CreateBody())));
    }

    /// <summary>Stores active grants from <paramref name="owner"/> to <paramref name="recipient"/>, each from another device, as <c>share-create</c> would.</summary>
    private async Task SeedReceivedGrantsAsync(Party owner, Party recipient, Party trustee, int count)
    {
        byte[] serverKey = MollyShareCrypto.DeriveServerKey(MollyTestKeys.DatabaseKeyBytes);
        byte[] recipientHash = PartyHashOf(recipient.Identity);

        await using MollyDbContext db = _fixture.DbFactory.CreateDbContext();

        for (int i = 0; i < count; i++)
        {
            var share = new MollyTestShare(owner.Identity, recipient.Identity, [trustee.Identity], 1);

            db.ShareEntries.Add(new MollyShareDbEntry
            {
                Id = MollyShareService.ToId(share.Entry),
                OwnerEntryId = Guid.NewGuid(),
                RecipientKeyHash = recipientHash,
                State = MollyShareState.Active,
                CreatedAt = DateTime.UtcNow,
                EncryptedHeader = MollyShareCrypto.EncryptAtRest(serverKey, share.Entry, [(byte)MollyShareService.EntryField.Header], [.. share.Descriptor, .. share.GrantSignature]),
            });
        }

        await db.SaveChangesAsync();
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
}
