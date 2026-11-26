using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MihuBot.DB;
using MihuBot.Molly;
using MihuBot.Molly.Api;
using MihuBot.Molly.Sharing;

namespace MihuBot.Tests.Molly;

/// <summary>Shared parties and helpers for the <see cref="MollyShareService"/> tests, split into several classes so xUnit runs them in parallel.</summary>
public abstract class MollyShareServiceTestBase : IClassFixture<MollyServiceFixture>, IDisposable
{
    protected readonly MollyServiceFixture _fixture;
    protected readonly MollyRequestProtector _protector = new(MollyTestKeys.TransportPrivateKeyBytes);
    protected readonly MollyShareService _shares;
    protected readonly MollyShareTestClient _client;
    protected readonly List<IDisposable> _disposables = [];

    protected MollyShareServiceTestBase(MollyServiceFixture fixture)
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
    protected sealed record Party(MollyTestIdentity Identity, string Session);

    /// <summary>A party with its own identity and device session, bound to that identity unless <paramref name="bind"/> is false.</summary>
    protected async Task<Party> NewPartyAsync(bool bind = true)
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

    protected async Task<Party[]> NewPartiesAsync(int count)
    {
        var parties = new Party[count];

        for (int i = 0; i < count; i++)
        {
            parties[i] = await NewPartyAsync();
        }

        return parties;
    }

    protected Task<JsonElement> SendAsync(Party party, string action, byte[] body) => _client.SendAsync(party.Session, action, body);

    protected static string Status(JsonElement response) => response.GetProperty("status").GetString()!;

    protected static string Result(JsonElement response)
    {
        Assert.Equal("ok", Status(response));
        return response.GetProperty("data").GetProperty("result").GetString()!;
    }

    protected static JsonElement Data(JsonElement response) => response.GetProperty("data");

    protected static byte[] Bytes(JsonElement element, string property) => Convert.FromBase64String(element.GetProperty(property).GetString()!);

    protected async Task<JsonElement> SyncAsync(Party party)
    {
        JsonElement response = await SendAsync(party, MollyApiActions.ShareSync, []);
        Assert.Equal("ok", Result(response));
        return Data(response);
    }

    protected async Task<long> StartAsync(Party recipient, MollyTestShare share)
    {
        Assert.Equal("ok", Result(await SendAsync(recipient, MollyApiActions.ShareStart, share.StartBody())));

        JsonElement owned = Assert.Single((await SyncAsync(recipient)).GetProperty("received").EnumerateArray(), r => r.GetProperty("entryId").GetString() == Convert.ToBase64String(share.Entry));
        Assert.Equal("started", owned.GetProperty("state").GetString());

        await using MollyDbContext db = _fixture.DbFactory.CreateDbContext();
        Guid id = MollyShareService.ToId(share.Entry);
        DateTime startedAt = (await db.ShareEntries.SingleAsync(e => e.Id == id)).StartedAt!.Value;
        return new DateTimeOffset(DateTime.SpecifyKind(startedAt, DateTimeKind.Utc)).ToUnixTimeSeconds();
    }

    protected async Task<(Party Owner, Party Recipient, Party[] Trustees, MollyTestShare Share)> CreateShareAsync(int threshold, int count, bool corruptSharedSecret = false, Func<int, byte[], byte[]>? shareTOverride = null, bool wrongDataKeySealed = false)
    {
        Party owner = await NewPartyAsync();
        Party recipient = await NewPartyAsync();
        Party[] trustees = await NewPartiesAsync(count);
        var share = new MollyTestShare(owner.Identity, recipient.Identity, [.. trustees.Select(t => t.Identity)], threshold, corruptSharedSecret, shareTOverride, wrongDataKeySealed);

        Assert.Equal("ok", await CreateAsync(owner, share));

        return (owner, recipient, trustees, share);
    }

    /// <summary>Reserves the share's ENTRY for the party's device, as if <c>share-reserve</c> had handed it out.</summary>
    protected async Task ReserveAsync(Party party, MollyTestShare share)
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
    protected async Task<string> CreateAsync(Party party, MollyTestShare share)
    {
        await ReserveAsync(party, share);
        return Result(await SendAsync(party, MollyApiActions.ShareCreate, share.CreateBody()));
    }

    protected async Task<byte[]> ReserveEntryAsync(Party party)
    {
        JsonElement response = await SendAsync(party, MollyApiActions.ShareReserve, []);
        Assert.Equal("ok", Result(response));
        return Bytes(Data(response), "entryId");
    }

    protected async Task<MollyShareDbEntry?> GetEntryAsync(MollyTestShare share)
    {
        await using MollyDbContext db = _fixture.DbFactory.CreateDbContext();
        Guid id = MollyShareService.ToId(share.Entry);
        return await db.ShareEntries.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id);
    }

    protected async Task<long?> PingUpdatedAtAsync(Party party) => (await _fixture.Service.PingAsync(party.Session, null, null, null, default)).ShareUpdatedAt;

    protected async Task<long[]> PingUpdatedAtAsync(Party[] parties)
    {
        var updatedAt = new long[parties.Length];

        for (int i = 0; i < parties.Length; i++)
        {
            updatedAt[i] = (await PingUpdatedAtAsync(parties[i]))!.Value;
        }

        return updatedAt;
    }

    protected static byte[] PartyHashOf(MollyTestIdentity identity) => MollyShareCrypto.ComputePartyHash(MollyShareCrypto.DeriveServerKey(MollyTestKeys.DatabaseKeyBytes), identity.Aci, identity.Key);

    protected async Task<(byte[]? PartyHash, DateTime? BoundAt)> GetBindingAsync(Guid entryId)
    {
        await using MollyDbContext db = _fixture.DbFactory.CreateDbContext();
        MollyDbEntry entry = await db.Entries.AsNoTracking().SingleAsync(e => e.Id == entryId);
        return (entry.SharePartyHash, entry.ShareIdentityBoundAt);
    }

    /// <summary>Moves the entry's binding time back by <paramref name="age"/>, as if it was bound that long ago.</summary>
    protected async Task AgeBindingAsync(Guid entryId, TimeSpan age)
    {
        await using MollyDbContext db = _fixture.DbFactory.CreateDbContext();
        MollyDbEntry entry = await db.Entries.SingleAsync(e => e.Id == entryId);
        entry.ShareIdentityBoundAt -= age;
        await db.SaveChangesAsync();
    }

    protected async Task AgeOwnerActivityAsync(Party owner, TimeSpan age)
    {
        Guid id = _fixture.Unprotect(owner.Session);

        await using MollyDbContext db = _fixture.DbFactory.CreateDbContext();
        MollyDbEntry entry = await db.Entries.SingleAsync(e => e.Id == id);
        entry.ShareActiveAt -= age;
        await db.SaveChangesAsync();
    }

    protected async Task AgeStartAsync(MollyTestShare share, TimeSpan age)
    {
        await using MollyDbContext db = _fixture.DbFactory.CreateDbContext();
        Guid id = MollyShareService.ToId(share.Entry);
        MollyShareDbEntry entry = await db.ShareEntries.SingleAsync(e => e.Id == id);
        entry.StartedAt -= age;
        await db.SaveChangesAsync();
    }
}
