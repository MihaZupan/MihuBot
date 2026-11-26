using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MihuBot.DB;
using MihuBot.Molly;
using MihuBot.Molly.Api;
using MihuBot.Molly.Sharing;

namespace MihuBot.Tests.Molly;

public sealed class MollyShareServiceTests(MollyServiceFixture fixture) : MollyShareServiceTestBase(fixture)
{
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
}
