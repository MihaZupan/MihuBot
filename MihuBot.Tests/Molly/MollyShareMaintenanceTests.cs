using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MihuBot.DB;
using MihuBot.Molly;
using MihuBot.Molly.Api;
using MihuBot.Molly.Sharing;

namespace MihuBot.Tests.Molly;

public sealed class MollyShareMaintenanceTests(MollyServiceFixture fixture) : MollyShareServiceTestBase(fixture)
{
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
}
