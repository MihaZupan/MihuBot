using System.Security.Cryptography;
using System.Text;
using MihuBot.Molly;
using MihuBot.Molly.Alerts;

namespace MihuBot.Tests.Molly;

public sealed class MollyServiceTests : IClassFixture<MollyServiceFixture>
{
    private readonly MollyServiceFixture _fixture;
    private MollyService Molly => _fixture.Service;

    public MollyServiceTests(MollyServiceFixture fixture) => _fixture = fixture;

    private async Task<MollyLoginResult> RegisterAsync(string? keyHash = null) =>
        await Molly.LoginAsync(keyHash ?? MollyTestKeys.NewKeyHash(), default);

    /// <summary>The real entry id behind the opaque token a login hands out.</summary>
    private Guid IdOf(MollyLoginResult result) => _fixture.Unprotect(result.ProtectedId);

    [Fact]
    public async Task Login_UnknownKeyHash_RegistersNewEntry()
    {
        MollyLoginResult result = await RegisterAsync();

        Assert.Equal(MollyResultStatus.Ok, result.Status);
        Assert.NotEqual(Guid.Empty, IdOf(result));
        Assert.Equal(64, result.ServerHmac!.Length);
        Assert.Equal(MollyCommand.None, result.Command);
    }

    [Fact]
    public async Task Login_NewEntry_IsNeitherLockedNorWiped()
    {
        MollyDbEntry entry = await _fixture.GetEntryAsync(IdOf(await RegisterAsync()));

        Assert.False(entry.LockRequested);
        Assert.False(entry.WipeRequested);
    }

    [Fact]
    public async Task Login_NeverExposesTheRealId()
    {
        MollyLoginResult result = await RegisterAsync();

        Assert.NotNull(result.ProtectedId);
        Assert.DoesNotContain(IdOf(result).ToString(), result.ProtectedId);
    }

    [Fact]
    public async Task Login_IssuesADifferentTokenEachTime()
    {
        string keyHash = MollyTestKeys.NewKeyHash();

        MollyLoginResult first = await RegisterAsync(keyHash);
        MollyLoginResult second = await RegisterAsync(keyHash);

        // Same entry, but the tokens must not be reusable as a stable identifier.
        Assert.NotEqual(first.ProtectedId, second.ProtectedId);
        Assert.Equal(IdOf(first), IdOf(second));
    }

    [Fact]
    public async Task Login_SameKeyHash_ReturnsSameIdAndServerHmac()
    {
        string keyHash = MollyTestKeys.NewKeyHash();

        MollyLoginResult first = await RegisterAsync(keyHash);
        MollyLoginResult second = await RegisterAsync(keyHash);

        Assert.Equal(IdOf(first), IdOf(second));
        Assert.Equal(first.ServerHmac, second.ServerHmac);
    }

    [Fact]
    public async Task Login_DifferentKeyHashes_ProduceDifferentEntries()
    {
        MollyLoginResult first = await RegisterAsync();
        MollyLoginResult second = await RegisterAsync();

        Assert.NotEqual(IdOf(first), IdOf(second));
        Assert.NotEqual(first.ServerHmac, second.ServerHmac);
    }

    [Fact]
    public async Task Login_StoresNothingInPlaintext()
    {
        MollyLoginResult result = await RegisterAsync();
        MollyDbEntry entry = await _fixture.GetEntryAsync(IdOf(result));

        Assert.NotNull(entry.EncryptedServerHmac);
        Assert.Equal(entry.DerivedHash[0], entry.HashPrefix);
        Assert.False(entry.EncryptedServerHmac.AsSpan().IndexOf(result.ServerHmac!) >= 0, "The server HMAC must not be stored unencrypted.");

        // The nonce is stored inline, ahead of the ciphertext, and the tag trails it.
        Assert.Equal(24 + 64 + 16, entry.EncryptedServerHmac.Length);
    }

    [Fact]
    public async Task EncryptedValues_UseAFreshNonceEachTime()
    {
        MollyLoginResult first = await RegisterAsync();
        MollyLoginResult second = await RegisterAsync();

        byte[] firstNonce = (await _fixture.GetEntryAsync(IdOf(first))).EncryptedServerHmac![..24];
        byte[] secondNonce = (await _fixture.GetEntryAsync(IdOf(second))).EncryptedServerHmac![..24];

        Assert.NotEqual(firstNonce, secondNonce);

        // Re-associating the same entry has to produce a different nonce too.
        await Molly.AssociateAsync(first.ProtectedId, "same-name", default);
        byte[] nicknameNonce = (await _fixture.GetEntryAsync(IdOf(first))).EncryptedNickname![..24];

        await Molly.AssociateAsync(first.ProtectedId, "same-name", default);
        byte[] reassociatedNonce = (await _fixture.GetEntryAsync(IdOf(first))).EncryptedNickname![..24];

        Assert.NotEqual(nicknameNonce, reassociatedNonce);
    }

    [Fact]
    public void Configuration_RejectsAServerKeyThatIsTooShort()
    {
        string tooShort = Convert.ToBase64String(new byte[16]);

        Assert.Throws<ArgumentOutOfRangeException>(() => _fixture.CreateService(tooShort, MollyTestKeys.TransportPrivateKey));
    }

    [Fact]
    public void Configuration_RejectsSecretsThatArentBase64()
    {
        Assert.Throws<FormatException>(() => _fixture.CreateService("not base64!", MollyTestKeys.TransportPrivateKey));
    }

    [Fact]
    public async Task Login_DifferentServerKey_DoesNotMatchExistingEntry()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        MollyLoginResult original = await RegisterAsync(keyHash);

        MollyService otherServer = _fixture.CreateService(MollyTestKeys.OtherDatabaseKey, MollyTestKeys.TransportPrivateKey);
        MollyLoginResult result = await otherServer.LoginAsync(keyHash, default);

        Assert.NotEqual(IdOf(original), IdOf(result));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]                    // Decodes to 3 bytes, below the minimum.
    [InlineData("AAAAA")]                   // Length isn't a multiple of 4.
    public async Task Login_InvalidKeyHash_IsRejected(string? keyHash)
    {
        Assert.Equal(MollyResultStatus.InvalidRequest, (await Molly.LoginAsync(keyHash, default)).Status);
    }

    [Theory]
    [InlineData(16)]    // Below the 32 byte minimum.
    [InlineData(31)]
    [InlineData(257)]   // Above the 256 byte maximum.
    public async Task Login_KeyHashOutsideLengthBounds_IsRejected(int length)
    {
        string keyHash = Convert.ToBase64String(RandomNumberGenerator.GetBytes(length));

        Assert.Equal(MollyResultStatus.InvalidRequest, (await Molly.LoginAsync(keyHash, default)).Status);
    }

    [Theory]
    [InlineData(32)]    // Exercises single-character base64 padding.
    [InlineData(33)]    // Exercises unpadded base64.
    [InlineData(34)]    // Exercises double-character base64 padding.
    [InlineData(256)]
    public async Task Login_KeyHashWithinLengthBounds_IsAccepted(int length)
    {
        string keyHash = Convert.ToBase64String(RandomNumberGenerator.GetBytes(length));

        Assert.Equal(MollyResultStatus.Ok, (await Molly.LoginAsync(keyHash, default)).Status);
    }

    [Fact]
    public async Task Login_KeyHashWithWhitespace_IsRejected()
    {
        string keyHash = MollyTestKeys.NewKeyHash();

        Assert.Equal(MollyResultStatus.InvalidRequest, (await Molly.LoginAsync(" " + keyHash, default)).Status);
        Assert.Equal(MollyResultStatus.InvalidRequest, (await Molly.LoginAsync(keyHash + "\n", default)).Status);
        Assert.Equal(MollyResultStatus.InvalidRequest, (await Molly.LoginAsync(keyHash.Insert(4, " "), default)).Status);

        // Trailing whitespace that keeps the length a multiple of 4 must not sneak through.
        Assert.Equal(MollyResultStatus.InvalidRequest, (await Molly.LoginAsync(keyHash + "\n\n\n\n", default)).Status);
    }

    [Fact]
    public async Task Associate_StoresEncryptedNickname()
    {
        MollyLoginResult login = await RegisterAsync();

        Assert.Equal(MollyResultStatus.Ok, (await Molly.AssociateAsync(login.ProtectedId, "mihu", default)).Status);

        MollyDbEntry entry = await _fixture.GetEntryAsync(IdOf(login));
        Assert.NotNull(entry.EncryptedNickname);
        Assert.False(entry.EncryptedNickname.AsSpan().IndexOf("mihu"u8) >= 0, "The nickname must not be stored unencrypted.");

        MollyUserInfo user = Assert.Single(await Molly.GetRegisteredUsersAsync(), u => u.Id == IdOf(login));
        Assert.Equal("mihu", user.Nickname);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("mihu")]
    [InlineData("a-considerably-longer-nickname")]
    [InlineData("ünïcödé-námé")]
    [InlineData("😀😀😀")]
    public async Task Associate_NicknamesRoundTrip(string nickname)
    {
        MollyLoginResult login = await RegisterAsync();

        Assert.Equal(MollyResultStatus.Ok, (await Molly.AssociateAsync(login.ProtectedId, nickname, default)).Status);

        MollyUserInfo user = Assert.Single(await Molly.GetRegisteredUsersAsync(), u => u.Id == IdOf(login));
        Assert.Equal(nickname, user.Nickname);
    }

    [Theory]
    [InlineData('a', 1)]        // 1 byte per char.
    [InlineData('é', 2)]        // 2 bytes per char.
    [InlineData('한', 3)]       // 3 bytes per char.
    public async Task Associate_LimitIsMeasuredInUtf8Bytes(char character, int bytesPerChar)
    {
        int maxCharacters = MollyService.MaxNicknameLengthInBytes / bytesPerChar;

        // As many characters as fit within the byte limit.
        string nickname = new(character, maxCharacters);
        Assert.InRange(
            Encoding.UTF8.GetByteCount(nickname),
            MollyService.MaxNicknameLengthInBytes - bytesPerChar + 1,
            MollyService.MaxNicknameLengthInBytes);

        MollyLoginResult login = await RegisterAsync();
        Assert.Equal(MollyResultStatus.Ok, (await Molly.AssociateAsync(login.ProtectedId, nickname, default)).Status);

        MollyUserInfo user = Assert.Single(await Molly.GetRegisteredUsersAsync(), u => u.Id == IdOf(login));
        Assert.Equal(nickname, user.Nickname);

        // One character more is over the byte limit, even though the character count is well under it.
        Assert.Equal(
            MollyResultStatus.InvalidRequest,
            (await Molly.AssociateAsync(login.ProtectedId, nickname + character, default)).Status);
    }

    [Fact]
    public async Task Associate_ManyCharactersOfFewBytes_IsAccepted()
    {
        // 64 ASCII characters is the limit, so a shorter multi-byte name is fine ...
        MollyLoginResult login = await RegisterAsync();

        Assert.Equal(MollyResultStatus.Ok, (await Molly.AssociateAsync(login.ProtectedId, "😀😀😀", default)).Status);
    }

    [Fact]
    public async Task Associate_StoredNicknameLength_DoesNotDependOnTheNickname()
    {
        string[] nicknames =
        [
            "a",
            "mihu",
            "a-considerably-longer-nickname",
            "ünïcödé-námé",
            "😀😀😀",
            new('a', MollyService.MaxNicknameLengthInBytes),
        ];

        var lengths = new HashSet<int>();

        foreach (string nickname in nicknames)
        {
            MollyLoginResult login = await RegisterAsync();
            await Molly.AssociateAsync(login.ProtectedId, nickname, default);

            lengths.Add((await _fixture.GetEntryAsync(IdOf(login))).EncryptedNickname!.Length);
        }

        // A single distinct length means the ciphertext gives nothing away about the name.
        Assert.Single(lengths);
    }

    [Fact]
    public async Task Associate_ReplacingANickname_KeepsTheStoredLengthConstant()
    {
        MollyLoginResult login = await RegisterAsync();

        await Molly.AssociateAsync(login.ProtectedId, "a", default);
        int shortLength = (await _fixture.GetEntryAsync(IdOf(login))).EncryptedNickname!.Length;

        await Molly.AssociateAsync(login.ProtectedId, new string('a', MollyService.MaxNicknameLengthInBytes), default);
        int longLength = (await _fixture.GetEntryAsync(IdOf(login))).EncryptedNickname!.Length;

        Assert.Equal(shortLength, longLength);
    }

    [Fact]
    public async Task Associate_TooLongNickname_IsRejected()
    {
        MollyLoginResult login = await RegisterAsync();
        string nickname = new('a', MollyService.MaxNicknameLengthInBytes + 1);

        Assert.Equal(MollyResultStatus.InvalidRequest, (await Molly.AssociateAsync(login.ProtectedId, nickname, default)).Status);
    }

    [Fact]
    public async Task Associate_MaxLengthNickname_IsAccepted()
    {
        MollyLoginResult login = await RegisterAsync();
        string nickname = new('a', MollyService.MaxNicknameLengthInBytes);

        Assert.Equal(MollyResultStatus.Ok, (await Molly.AssociateAsync(login.ProtectedId, nickname, default)).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Associate_MissingNickname_IsRejected(string? nickname)
    {
        MollyLoginResult login = await RegisterAsync();

        Assert.Equal(MollyResultStatus.InvalidRequest, (await Molly.AssociateAsync(login.ProtectedId, nickname, default)).Status);
    }

    [Fact]
    public async Task Associate_MalformedToken_IsRejected()
    {
        // A raw guid is no longer a usable identifier - only a token issued by this process is.
        Assert.Equal(MollyResultStatus.InvalidRequest, (await Molly.AssociateAsync(Guid.NewGuid().ToString(), "mihu", default)).Status);
        Assert.Equal(MollyResultStatus.InvalidRequest, (await Molly.AssociateAsync("not-a-token", "mihu", default)).Status);
        Assert.Equal(MollyResultStatus.InvalidRequest, (await Molly.AssociateAsync(null, "mihu", default)).Status);
    }

    [Fact]
    public async Task Associate_TokenForADeletedEntry_ReturnsWipe()
    {
        MollyLoginResult login = await RegisterAsync();

        await _fixture.SetLastSeenAsync(IdOf(login), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-90));
        await Molly.DeleteUnassociatedEntriesAsync();

        // The token still decrypts, but its entry - and the key material needed to recover the
        // device's data - is gone, so the device is told to wipe rather than left on a dead token.
        MollyCommandResult result = await Molly.AssociateAsync(login.ProtectedId, "mihu", default);
        Assert.Equal(MollyResultStatus.Command, result.Status);
        Assert.Equal(MollyCommand.Wipe, result.Command);
    }

    [Fact]
    public async Task Ping_TokenForADeletedEntry_ReturnsWipe()
    {
        MollyLoginResult login = await RegisterAsync();

        await _fixture.SetLastSeenAsync(IdOf(login), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-90));
        await Molly.DeleteUnassociatedEntriesAsync();

        MollyCommandResult result = await Molly.PingAsync(login.ProtectedId, default);
        Assert.Equal(MollyResultStatus.Command, result.Status);
        Assert.Equal(MollyCommand.Wipe, result.Command);
    }

    [Fact]
    public async Task Alert_TokenForADeletedEntry_ReturnsWipe()
    {
        MollyLoginResult login = await RegisterAsync();

        await _fixture.SetLastSeenAsync(IdOf(login), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-90));
        await Molly.DeleteUnassociatedEntriesAsync();

        MollyCommandResult result = await Molly.SubmitAlertAsync(login.ProtectedId, Encoding.UTF8.GetBytes("""{"type":"test"}"""), default);
        Assert.Equal(MollyResultStatus.Command, result.Status);
        Assert.Equal(MollyCommand.Wipe, result.Command);
    }

    [Fact]
    public async Task Ping_KnownId_ReturnsOk()
    {
        MollyLoginResult login = await RegisterAsync();

        Assert.Equal(MollyResultStatus.Ok, (await Molly.PingAsync(login.ProtectedId, default)).Status);
    }

    [Fact]
    public async Task Ping_WithoutId_IsStillALivenessCheck()
    {
        Assert.Equal(MollyResultStatus.Ok, (await Molly.PingAsync(null, default)).Status);
        Assert.Equal(MollyResultStatus.Ok, (await Molly.PingAsync("", default)).Status);
    }

    [Fact]
    public async Task Ping_MalformedToken_IsRejected()
    {
        Assert.Equal(MollyResultStatus.InvalidRequest, (await Molly.PingAsync(Guid.NewGuid().ToString(), default)).Status);
        Assert.Equal(MollyResultStatus.InvalidRequest, (await Molly.PingAsync("not-a-token", default)).Status);
    }

    [Fact]
    public async Task Ping_TokenFromAnotherServerKey_IsRejected()
    {
        // Tokens are bound to the server key, so one issued under a different key can't be unprotected.
        string foreignToken = new MollyIdProtector(MollyTestKeys.OtherDatabaseKeyBytes).Protect(Guid.NewGuid());

        Assert.Equal(MollyResultStatus.InvalidRequest, (await Molly.PingAsync(foreignToken, default)).Status);
    }

    [Fact]
    public async Task Lock_BlocksEveryEndpoint()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        MollyLoginResult login = await Molly.LoginAsync(keyHash, default);
        await Molly.AssociateAsync(login.ProtectedId, "locked-user", default);

        await Molly.SetLockRequestedAsync(IdOf(login), lockRequested: true);

        MollyLoginResult locked = await Molly.LoginAsync(keyHash, default);
        Assert.Equal(MollyResultStatus.Command, locked.Status);
        Assert.Equal(MollyCommand.Lock, locked.Command);
        Assert.Null(locked.ServerHmac);
        Assert.Null(locked.ProtectedId);

        Assert.Equal(MollyCommand.Lock, (await Molly.AssociateAsync(login.ProtectedId, "new-name", default)).Command);
        Assert.Equal(MollyCommand.Lock, (await Molly.PingAsync(login.ProtectedId, default)).Command);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedEndpoints_StillRecordThatTheDeviceCheckedIn(bool wipe)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        string keyHash = MollyTestKeys.NewKeyHash();
        MollyLoginResult login = await Molly.LoginAsync(keyHash, default);
        Guid id = IdOf(login);

        await Molly.AssociateAsync(login.ProtectedId, "checked-in-user", default);

        if (wipe)
        {
            await Molly.RequestWipeAsync(id);
        }
        else
        {
            await Molly.SetLockRequestedAsync(id, lockRequested: true);
        }

        MollyCommand expected = wipe ? MollyCommand.Wipe : MollyCommand.Lock;

        // Each blocked endpoint has to refresh LastSeenDay even though it returns a command.
        foreach (Func<Task<MollyCommand>> checkIn in (Func<Task<MollyCommand>>[])
        [
            async () => (await Molly.LoginAsync(keyHash, default)).Command,
            async () => (await Molly.AssociateAsync(login.ProtectedId, "new-name", default)).Command,
            async () => (await Molly.PingAsync(login.ProtectedId, default)).Command,
        ])
        {
            await _fixture.SetLastSeenAsync(id, today.AddDays(-10));

            Assert.Equal(expected, await checkIn());
            Assert.Equal(today, (await _fixture.GetEntryAsync(id)).LastSeenDay);
        }
    }

    [Fact]
    public async Task Unlock_RestoresAccess()    {
        string keyHash = MollyTestKeys.NewKeyHash();
        MollyLoginResult login = await Molly.LoginAsync(keyHash, default);

        await Molly.SetLockRequestedAsync(IdOf(login), lockRequested: true);
        await Molly.SetLockRequestedAsync(IdOf(login), lockRequested: false);

        MollyLoginResult result = await Molly.LoginAsync(keyHash, default);
        Assert.Equal(MollyResultStatus.Ok, result.Status);
        Assert.Equal(login.ServerHmac, result.ServerHmac);
    }

    [Fact]
    public async Task Wipe_DeletesSecretsButKeepsServingTheCommand()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        MollyLoginResult login = await Molly.LoginAsync(keyHash, default);
        await Molly.AssociateAsync(login.ProtectedId, "wiped-user", default);

        await Molly.RequestWipeAsync(IdOf(login));

        MollyDbEntry entry = await _fixture.GetEntryAsync(IdOf(login));
        Assert.Null(entry.EncryptedServerHmac);
        Assert.True(entry.WipeRequested);
        Assert.True(entry.LockRequested);

        // The nickname is kept so the entry still shows up on the dashboard.
        Assert.NotNull(entry.EncryptedNickname);

        Assert.Equal(MollyCommand.Wipe, (await Molly.LoginAsync(keyHash, default)).Command);
        Assert.Equal(MollyCommand.Wipe, (await Molly.PingAsync(login.ProtectedId, default)).Command);
    }

    [Fact]
    public async Task Wipe_CannotBeUndoneByUnlocking()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        MollyLoginResult login = await Molly.LoginAsync(keyHash, default);

        await Molly.RequestWipeAsync(IdOf(login));
        await Molly.SetLockRequestedAsync(IdOf(login), lockRequested: false);

        Assert.Equal(MollyCommand.Wipe, (await Molly.LoginAsync(keyHash, default)).Command);
    }

    [Fact]
    public async Task Delete_RemovesTheEntryAndItsAlerts()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        MollyLoginResult login = await Molly.LoginAsync(keyHash, default);
        await Molly.AssociateAsync(login.ProtectedId, "deleted-user", default);
        await Molly.SubmitAlertAsync(login.ProtectedId, Encoding.UTF8.GetBytes("""{"type":"test"}"""), default);

        Assert.Equal(1, await _fixture.CountAlertsAsync(IdOf(login)));

        await Molly.DeleteEntryAsync(IdOf(login));

        Assert.False(await _fixture.EntryExistsAsync(IdOf(login)));
        Assert.Equal(0, await _fixture.CountAlertsAsync(IdOf(login)));

        // Nothing is left to serve a command from, so the device just registers again.
        Assert.Equal(MollyCommand.None, (await Molly.LoginAsync(keyHash, default)).Command);
    }

    [Fact]
    public async Task TamperedNickname_IsRejectedRatherThanDecryptedToGarbage()
    {
        MollyLoginResult login = await RegisterAsync();
        await Molly.AssociateAsync(login.ProtectedId, "tampered-user", default);

        byte[] encrypted = (await _fixture.GetEntryAsync(IdOf(login))).EncryptedNickname!;

        // Under CBC an attacker with database write access could flip bits straight through into the
        // plaintext. Every byte now has to be covered by the tag.
        for (int i = 0; i < encrypted.Length; i++)
        {
            byte[] tampered = (byte[])encrypted.Clone();
            tampered[i] ^= 0x01;

            await _fixture.SetEncryptedNicknameAsync(IdOf(login), tampered);

            MollyUserInfo user = Assert.Single(await Molly.GetRegisteredUsersAsync(), u => u.Id == IdOf(login));
            Assert.Equal("<unknown>", user.Nickname);
        }
    }

    [Fact]
    public async Task CiphertextsFromAnotherEntry_AreRejected()
    {
        MollyLoginResult first = await RegisterAsync();
        MollyLoginResult second = await RegisterAsync();

        await Molly.AssociateAsync(first.ProtectedId, "first-user", default);
        await Molly.AssociateAsync(second.ProtectedId, "second-user", default);

        byte[] stolen = (await _fixture.GetEntryAsync(IdOf(second))).EncryptedNickname!;

        // The entry id is the associated data, so a ciphertext is bound to the row it was written for.
        await _fixture.SetEncryptedNicknameAsync(IdOf(first), stolen);

        MollyUserInfo user = Assert.Single(await Molly.GetRegisteredUsersAsync(), u => u.Id == IdOf(first));
        Assert.Equal("<unknown>", user.Nickname);
    }

    [Fact]
    public async Task CorruptedServerHmac_IsReportedAsInvalidRatherThanThrowing()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        MollyLoginResult login = await Molly.LoginAsync(keyHash, default);

        // Too short to even contain a nonce and tag.
        await _fixture.SetEncryptedServerHmacAsync(IdOf(login), [1, 2, 3]);

        await Assert.ThrowsAsync<CryptographicException>(() => Molly.LoginAsync(keyHash, default));
    }

    [Fact]
    public async Task CorruptedNickname_DoesNotBreakTheDashboard()
    {
        MollyLoginResult login = await RegisterAsync();
        await Molly.AssociateAsync(login.ProtectedId, "corrupted-user", default);

        await _fixture.SetEncryptedNicknameAsync(IdOf(login), [1, 2, 3]);

        MollyUserInfo user = Assert.Single(await Molly.GetRegisteredUsersAsync(), u => u.Id == IdOf(login));
        Assert.Equal("<unknown>", user.Nickname);
    }

    [Fact]
    public async Task CreateFakeEntry_ShowsUpOnTheDashboard()
    {
        string nickname = $"dummy-{Guid.NewGuid():N}";

        await Molly.CreateFakeEntryAsync(nickname);

        MollyUserInfo user = Assert.Single(await Molly.GetRegisteredUsersAsync(), u => u.Nickname == nickname);

        // It has to be a normal, fully usable entry rather than a special case.
        Assert.False(user.LockRequested);
        Assert.False(user.WipeRequested);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), user.CreatedDay);
    }

    [Fact]
    public async Task CreateFakeEntry_AlsoSeedsALocationAlert()
    {
        string nickname = $"dummy-{Guid.NewGuid():N}"[..24];

        await Molly.CreateFakeEntryAsync(nickname);

        MollyUserInfo user = Assert.Single(await Molly.GetRegisteredUsersAsync(), u => u.Nickname == nickname);
        MollyAlertInfo alert = Assert.Single(await Molly.GetRecentAlertsAsync(), a => a.EntryId == user.Id);

        Assert.Equal(MollyAlertType.Location, alert.Type);
        Assert.NotNull(alert.Summary);
    }

    [Fact]
    public async Task CreateFakeEntry_IsIndependentOfOtherEntries()
    {
        await Molly.CreateFakeEntryAsync("dummy-one");
        await Molly.CreateFakeEntryAsync("dummy-two");

        MollyUserInfo[] users = await Molly.GetRegisteredUsersAsync();

        Assert.NotEqual(
            Assert.Single(users, u => u.Nickname == "dummy-one").Id,
            Assert.Single(users, u => u.Nickname == "dummy-two").Id);
    }

    [Fact]
    public async Task GetRegisteredUsers_OnlyReturnsAssociatedEntries()
    {
        MollyLoginResult associated = await RegisterAsync();
        MollyLoginResult unassociated = await RegisterAsync();

        await Molly.AssociateAsync(associated.ProtectedId, "listed-user", default);

        MollyUserInfo[] users = await Molly.GetRegisteredUsersAsync();

        Assert.Contains(users, u => u.Id == IdOf(associated));
        Assert.DoesNotContain(users, u => u.Id == IdOf(unassociated));
    }

    [Fact]
    public async Task DeleteUnassociatedEntries_OnlyRemovesStaleUnassociatedEntries()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        MollyLoginResult stale = await RegisterAsync();
        MollyLoginResult seenToday = await RegisterAsync();
        MollyLoginResult onTheThreshold = await RegisterAsync();
        MollyLoginResult oldButAssociated = await RegisterAsync();

        await Molly.AssociateAsync(oldButAssociated.ProtectedId, "kept-user", default);

        // Entries are dropped once they haven't been seen for MollyService.UnassociatedEntryRetention (2 days).
        await _fixture.SetLastSeenAsync(IdOf(stale), today.AddDays(-3));
        await _fixture.SetLastSeenAsync(IdOf(seenToday), today);
        await _fixture.SetLastSeenAsync(IdOf(onTheThreshold), today.AddDays(-2));
        await _fixture.SetLastSeenAsync(IdOf(oldButAssociated), today.AddDays(-400));

        await Molly.DeleteUnassociatedEntriesAsync();

        Assert.False(await _fixture.EntryExistsAsync(IdOf(stale)));
        Assert.True(await _fixture.EntryExistsAsync(IdOf(seenToday)));
        Assert.True(await _fixture.EntryExistsAsync(IdOf(onTheThreshold)));
        Assert.True(await _fixture.EntryExistsAsync(IdOf(oldButAssociated)));
    }

    [Fact]
    public async Task LockInactiveEntries_LocksEntriesPastTheThreshold()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        MollyLoginResult inactive = await RegisterAsync();
        MollyLoginResult onTheThreshold = await RegisterAsync();
        MollyLoginResult recent = await RegisterAsync();

        await Molly.AssociateAsync(inactive.ProtectedId, "inactive-user", default);
        await Molly.AssociateAsync(onTheThreshold.ProtectedId, "threshold-user", default);
        await Molly.AssociateAsync(recent.ProtectedId, "recent-user", default);

        // Entries are locked once they haven't been seen for MollyService.InactivityLockThreshold (7 days).
        await _fixture.SetLastSeenAsync(IdOf(inactive), today.AddDays(-8));
        await _fixture.SetLastSeenAsync(IdOf(onTheThreshold), today.AddDays(-7));
        await _fixture.SetLastSeenAsync(IdOf(recent), today.AddDays(-6));

        await Molly.LockInactiveEntriesAsync();

        Assert.True((await _fixture.GetEntryAsync(IdOf(inactive))).LockRequested);
        Assert.False((await _fixture.GetEntryAsync(IdOf(onTheThreshold))).LockRequested);
        Assert.False((await _fixture.GetEntryAsync(IdOf(recent))).LockRequested);
    }

    [Fact]
    public async Task LockInactiveEntries_MakesTheDeviceReceiveTheLockCommand()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        MollyLoginResult login = await Molly.LoginAsync(keyHash, default);
        await Molly.AssociateAsync(login.ProtectedId, "gone-quiet", default);

        await _fixture.SetLastSeenAsync(IdOf(login), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30));
        await Molly.LockInactiveEntriesAsync();

        MollyLoginResult locked = await Molly.LoginAsync(keyHash, default);

        Assert.Equal(MollyCommand.Lock, locked.Command);
        Assert.Null(locked.ServerHmac);
    }

    [Fact]
    public async Task LockInactiveEntries_LeavesWipedEntriesAlone()
    {
        MollyLoginResult login = await RegisterAsync();
        await Molly.AssociateAsync(login.ProtectedId, "wiped-and-inactive", default);
        await Molly.RequestWipeAsync(IdOf(login));

        await _fixture.SetLastSeenAsync(IdOf(login), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30));
        await Molly.LockInactiveEntriesAsync();

        MollyDbEntry entry = await _fixture.GetEntryAsync(IdOf(login));

        // A wipe outranks a lock, and must not be downgraded by the maintenance pass.
        Assert.True(entry.WipeRequested);
        Assert.True(entry.LockRequested);
        Assert.Equal(MollyCommand.Wipe, (await Molly.PingAsync(login.ProtectedId, default)).Command);
    }

    [Fact]
    public async Task LockInactiveEntries_DoesNotUnlockAnythingOrTouchActiveEntries()
    {
        MollyLoginResult active = await RegisterAsync();
        MollyLoginResult manuallyLocked = await RegisterAsync();

        await Molly.AssociateAsync(active.ProtectedId, "still-here", default);
        await Molly.AssociateAsync(manuallyLocked.ProtectedId, "locked-by-admin", default);

        await Molly.SetLockRequestedAsync(IdOf(manuallyLocked), lockRequested: true);

        await Molly.LockInactiveEntriesAsync();

        Assert.False((await _fixture.GetEntryAsync(IdOf(active))).LockRequested);
        Assert.True((await _fixture.GetEntryAsync(IdOf(manuallyLocked))).LockRequested);
    }

    [Fact]
    public async Task LockInactiveEntries_CanBeUndoneByAnAdmin()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        MollyLoginResult login = await Molly.LoginAsync(keyHash, default);
        await Molly.AssociateAsync(login.ProtectedId, "back-again", default);

        await _fixture.SetLastSeenAsync(IdOf(login), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30));
        await Molly.LockInactiveEntriesAsync();

        await Molly.SetLockRequestedAsync(IdOf(login), lockRequested: false);

        Assert.Equal(MollyResultStatus.Ok, (await Molly.LoginAsync(keyHash, default)).Status);
    }

    [Fact]
    public async Task Unlock_RefreshesLastSeen_SoTheSweepDoesNotImmediatelyRelock()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        MollyLoginResult login = await Molly.LoginAsync(keyHash, default);
        Guid id = IdOf(login);

        await Molly.AssociateAsync(login.ProtectedId, "unlocked-user", default);

        await _fixture.SetLastSeenAsync(id, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30));
        await Molly.LockInactiveEntriesAsync();
        Assert.True((await _fixture.GetEntryAsync(id)).LockRequested);

        await Molly.SetLockRequestedAsync(id, lockRequested: false);

        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), (await _fixture.GetEntryAsync(id)).LastSeenDay);

        // The device now gets a full window to check in rather than being locked again straight away.
        await Molly.LockInactiveEntriesAsync();

        Assert.False((await _fixture.GetEntryAsync(id)).LockRequested);
        Assert.Equal(MollyResultStatus.Ok, (await Molly.LoginAsync(keyHash, default)).Status);
    }

    [Fact]
    public async Task Lock_DoesNotChangeLastSeen()
    {
        DateOnly lastSeen = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);

        MollyLoginResult login = await RegisterAsync();
        await Molly.AssociateAsync(login.ProtectedId, "locked-user", default);
        await _fixture.SetLastSeenAsync(IdOf(login), lastSeen);

        await Molly.SetLockRequestedAsync(IdOf(login), lockRequested: true);

        // Locking is not a check-in, so the dashboard keeps showing when the device was last heard from.
        Assert.Equal(lastSeen, (await _fixture.GetEntryAsync(IdOf(login))).LastSeenDay);
    }

    [Fact]
    public async Task DeleteUnassociatedEntries_KeepsWipedEntries()
    {
        MollyLoginResult login = await RegisterAsync();
        await Molly.AssociateAsync(login.ProtectedId, "wiped-but-kept", default);
        await Molly.RequestWipeAsync(IdOf(login));

        await _fixture.SetLastSeenAsync(IdOf(login), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-365));

        await Molly.DeleteUnassociatedEntriesAsync();

        Assert.True(await _fixture.EntryExistsAsync(IdOf(login)));
    }

    [Fact]
    public async Task DeleteUnassociatedEntries_DeletedEntryIsReRegisteredOnNextLogin()
    {
        string keyHash = MollyTestKeys.NewKeyHash();
        MollyLoginResult original = await Molly.LoginAsync(keyHash, default);

        await _fixture.SetLastSeenAsync(IdOf(original), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-90));
        await Molly.DeleteUnassociatedEntriesAsync();

        MollyLoginResult reRegistered = await Molly.LoginAsync(keyHash, default);

        Assert.Equal(MollyResultStatus.Ok, reRegistered.Status);
        Assert.NotEqual(IdOf(original), IdOf(reRegistered));
    }
}
