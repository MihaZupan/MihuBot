using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MihuBot.DB;
using MihuBot.Helpers.Crypto;
using MihuBot.Molly;
using MihuBot.Molly.Api;
using MihuBot.Molly.Sharing;

namespace MihuBot.Tests.Molly;

public sealed class MollyShareIdentityTests(MollyServiceFixture fixture) : MollyShareServiceTestBase(fixture)
{
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
    public async Task Maintenance_ClearsOnlyExpiredBindingTimes_AndPreservesRebindingRules()
    {
        Party expired = await NewPartyAsync();
        Party recent = await NewPartyAsync();
        Party unbound = await NewPartyAsync(bind: false);
        Guid expiredId = _fixture.Unprotect(expired.Session);
        Guid recentId = _fixture.Unprotect(recent.Session);
        Guid unboundId = _fixture.Unprotect(unbound.Session);
        (byte[]? recentHash, DateTime? recentBoundAt) = await GetBindingAsync(recentId);

        await AgeBindingAsync(expiredId, MollyService.IdentityRebindCooldown + TimeSpan.FromMinutes(1));
        await _fixture.Service.ClearExpiredIdentityBindingTimesAsync();
        await _fixture.Service.ClearExpiredIdentityBindingTimesAsync();

        (byte[]? expiredHash, DateTime? expiredBoundAt) = await GetBindingAsync(expiredId);
        Assert.Equal(PartyHashOf(expired.Identity), expiredHash);
        Assert.Equal(DateTime.UnixEpoch, expiredBoundAt);
        (byte[]? unchangedHash, DateTime? unchangedBoundAt) = await GetBindingAsync(recentId);
        Assert.Equal(recentHash, unchangedHash);
        Assert.Equal(recentBoundAt, unchangedBoundAt);
        Assert.Equal((null, null), await GetBindingAsync(unboundId));

        Assert.Equal(MollyIdentityBinding.Bound, (await _client.AssociateAsync(expired.Session, expired.Identity)).Identity);
        Assert.Equal(DateTime.UnixEpoch, (await GetBindingAsync(expiredId)).BoundAt);

        using var other = new MollyTestIdentity();
        Assert.Equal(MollyIdentityBinding.Cooldown, (await _client.AssociateAsync(recent.Session, other)).Identity);
        Assert.Equal(MollyIdentityBinding.Bound, (await _client.AssociateAsync(expired.Session, other)).Identity);
        Assert.True((await GetBindingAsync(expiredId)).BoundAt > DateTime.UtcNow - TimeSpan.FromMinutes(1));
        Assert.Equal(MollyIdentityBinding.Cooldown, (await _client.AssociateAsync(expired.Session, expired.Identity)).Identity);
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
    public async Task Associate_WithAnUnverifiedIdentity_BindsNothing()
    {
        Party party = await NewPartyAsync(bind: false);
        using var other = new MollyTestIdentity();
        byte[] transportPublicKey = Convert.FromBase64String(_protector.GetTransportKey().PublicKey);

        async Task<MollyCommandResult> AssociateAsync(Func<MollyExchangeContext, MollyAssociateIdentity> createIdentity, MollyExchangeContext? exchange = null)
        {
            exchange = MollyTestSessions.ProvenExchange(party.Session, MollyApiActions.Associate, exchange ?? _client.NewExchange(transportPublicKey));
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
    public async Task ShareRequests_NeedAUsableSessionItsProofAndABase64Body()
    {
        Party party = await NewPartyAsync();

        Task<JsonElement> SendAsync(string? id, string? body, string proofAction = MollyApiActions.ShareSync, bool withProof = true)
        {
            MollyExchangeContext exchange = MollyTestSessions.ProvenExchange(id, proofAction, _client.NewExchange());
            exchange = withProof ? exchange with { Action = MollyApiActions.ShareSync } : exchange with { Action = MollyApiActions.ShareSync, SessionProof = null };
            return _client.SendAsync(MollyApiActions.ShareSync, new MollyShareRequest { Id = id, Body = body }, exchange);
        }

        Assert.Equal("ok", Result(await SendAsync(party.Session, "")));
        Assert.Equal("rejected", Result(await SendAsync(party.Session, "AA==")));
        Assert.Equal("invalid", Status(await SendAsync(party.Session, null)));
        Assert.Equal("invalid", Status(await SendAsync(party.Session, "not base64")));
        Assert.Equal("invalid", Status(await SendAsync(null, "")));
        Assert.Equal("invalid", Status(await SendAsync(party.Session[..^4] + "AAAA", "")));
        Assert.Equal("invalid", Status(await SendAsync(party.Session, "", withProof: false)));
        Assert.Equal("invalid", Status(await SendAsync(party.Session, "", proofAction: MollyApiActions.ShareGet)));
    }
}
