using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MihuBot.Helpers.Crypto;
using MihuBot.Molly;
using MihuBot.Molly.Api;

namespace MihuBot.Tests.Molly;

public sealed class MollyKeyRotationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Header_ContainsRecipientSha512PrefixAndEphemeralPublicKey(bool bootstrap)
    {
        using var server = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes);
        byte[] recipientPublicKey = bootstrap
            ? MollyTestKeys.TransportPublicKeyBytes
            : Convert.FromBase64String(server.GetTransportKey().PublicKey);
        using var client = new MollyTestEnvelope(recipientPublicKey);
        string plaintext = MollyTestEnvelope.RequestJson(bootstrap ? "transport-key" : "ping");
        byte[] body = client.Encrypt(plaintext);

        Assert.Equal(16, MollyRequestProtector.RecipientKeyIdLength);
        Assert.Equal(48, MollyRequestProtector.HeaderLength);
        Assert.Equal(48 + 24 + 16 + 2 + Encoding.UTF8.GetByteCount(plaintext), body.Length);
        Assert.Equal(SHA512.HashData(recipientPublicKey).AsSpan(0, 16).ToArray(), body.AsSpan(0, 16).ToArray());
        Assert.Equal(-1, body.AsSpan(0, 48).IndexOf(recipientPublicKey));
        Assert.True(server.TryDecryptRequest(body, out _, out byte[]? keys));
        CryptographicOperations.ZeroMemory(keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeyLookup_RequiresExactlyTheFirstSixteenSha512Bytes(bool bootstrap)
    {
        using var keys = new MollyTransportKeyRing(MollyTestKeys.TransportPrivateKeyBytes, TimeProvider.System);
        byte[] publicKey = bootstrap
            ? MollyTestKeys.TransportPublicKeyBytes
            : Convert.FromBase64String(keys.GetCurrentKey().PublicKey);
        byte[] hash = SHA512.HashData(publicKey);

        MollyTransportKeyRing.TransportKey? key = keys.TryGetKey(hash.AsSpan(0, 16), out bool isBootstrap);
        Assert.NotNull(key);
        Assert.Equal(bootstrap, isBootstrap);
        Assert.Equal(publicKey, key.PublicKey);
        Assert.Equal(hash.AsSpan(0, 16).ToArray(), key.KeyId);

        foreach (int length in new[] { 0, 15, 17, 32, 64 })
        {
            Assert.Null(keys.TryGetKey(hash.AsSpan(0, length), out _));
        }

        Assert.Null(keys.TryGetKey(publicKey, out _));
        hash[15] ^= 1;
        Assert.Null(keys.TryGetKey(hash.AsSpan(0, 16), out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Import_RejectsInvalidPrivateKeyLengths(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MollyTransportKeyRing.TransportKey.Import(new byte[length]));
    }

    [Fact]
    public void GenerateCreatesIndependentKeys_AndImportPreservesTheSuppliedIdentity()
    {
        var first = MollyTransportKeyRing.TransportKey.Generate(TimeProvider.System);
        var second = MollyTransportKeyRing.TransportKey.Generate(TimeProvider.System);
        var imported = MollyTransportKeyRing.TransportKey.Import(MollyTestKeys.TransportPrivateKeyBytes);
        Assert.Equal(32, first.PublicKey.Length);
        Assert.NotEqual(first.PublicKey, second.PublicKey);
        Assert.Equal(MollyTestKeys.TransportPublicKeyBytes, imported.PublicKey);
    }

    [Fact]
    public void Import_OwnsACopyOfThePrivateKey()
    {
        byte[] privateKey = MollyTestKeys.TransportPrivateKeyBytes;
        var key = MollyTransportKeyRing.TransportKey.Import(privateKey);
        privateKey.AsSpan().Clear();
        using var peer = X25519DiffieHellman.GenerateKey();
        byte[] expected = new byte[32];
        byte[] actual = new byte[32];
        peer.DeriveRawSecretAgreement(MollyTestKeys.TransportPublicKeyBytes, expected);
        key.DeriveRawSecretAgreement(peer.ExportPublicKey(), actual);
        Assert.Equal(expected, actual);
        CryptographicOperations.ZeroMemory(expected);
        CryptographicOperations.ZeroMemory(actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoredKey_SupportsConcurrentDerivationsWithDifferentPeers(bool imported)
    {
        var material = imported
            ? MollyTransportKeyRing.TransportKey.Import(MollyTestKeys.TransportPrivateKeyBytes)
            : MollyTransportKeyRing.TransportKey.Generate(TimeProvider.System);
        Parallel.For(0, 128, _ =>
        {
            using var peer = X25519DiffieHellman.GenerateKey();
            byte[] peerPublicKey = peer.ExportPublicKey();
            byte[] expected = new byte[32];
            byte[] actual = new byte[32];
            peer.DeriveRawSecretAgreement(material.PublicKey, expected);
            for (int i = 0; i < 4; i++)
            {
                material.DeriveRawSecretAgreement(peerPublicKey, actual);
                Assert.Equal(expected, actual);
            }

            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(actual);
        });
    }

    [Fact]
    public void KeyRotatesAtThirtyMinutes_AdvertisesThirtyOne_AndIsRetiredAtThirtyTwo()
    {
        var time = new ManualTimeProvider();
        using var server = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes, time);
        MollyTransportKeyResponse first = server.GetTransportKey();
        using var firstClient = new MollyTestEnvelope(first);

        Assert.NotEqual(MollyTestKeys.TransportPublicKeyBytes, Convert.FromBase64String(first.PublicKey));
        Assert.Equal(time.GetUtcNow().AddMinutes(31).ToUnixTimeSeconds(), first.ExpiresAt);

        time.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(first.PublicKey, server.GetTransportKey().PublicKey);
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(first.PublicKey, server.GetTransportKey().PublicKey);
        time.Advance(TimeSpan.FromSeconds(1));
        MollyTransportKeyResponse second = server.GetTransportKey();
        Assert.NotEqual(first.PublicKey, second.PublicKey);
        Assert.Equal(first.ExpiresAt + 1800, second.ExpiresAt);
        using var secondClient = new MollyTestEnvelope(second);
        AssertAccepted(server, firstClient, time);
        AssertAccepted(server, secondClient, time);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(first.ExpiresAt, time.GetUtcNow().ToUnixTimeSeconds());
        AssertAccepted(server, firstClient, time);
        AssertAccepted(server, secondClient, time);

        time.Advance(TimeSpan.FromSeconds(59));
        AssertAccepted(server, firstClient, time);
        AssertAccepted(server, secondClient, time);

        time.Advance(TimeSpan.FromSeconds(1));
        AssertRejected(server, firstClient, time);
        AssertAccepted(server, secondClient, time);
    }

    [Fact]
    public void DelayedMaintenance_ReplacesExpiredKeys()
    {
        var time = new ManualTimeProvider();
        using var server = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes, time);
        using var oldClient = new MollyTestEnvelope(server.GetTransportKey());
        time.Advance(TimeSpan.FromMinutes(62));
        MollyTransportKeyResponse current = server.GetTransportKey();
        Assert.Equal(time.GetUtcNow().AddMinutes(31).ToUnixTimeSeconds(), current.ExpiresAt);
        AssertRejected(server, oldClient, time);
        using var client = new MollyTestEnvelope(current);
        AssertAccepted(server, client, time);
    }

    [Fact]
    public void MaintenanceTimer_RotatesAndExpiresKeysWithoutAnyRequests()
    {
        var time = new ManualTimeProvider();
        using var keys = new MollyTransportKeyRing(MollyTestKeys.TransportPrivateKeyBytes, time);
        MollyTransportKeyResponse first = keys.GetCurrentKey();
        time.Advance(TimeSpan.FromMinutes(30));
        MollyTransportKeyResponse second = keys.GetCurrentKey();
        Assert.NotEqual(first.PublicKey, second.PublicKey);
        AssertKeyAvailable(keys, first);
        AssertKeyAvailable(keys, second);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(keys.TryGetKey(MollyTestEnvelope.GetRecipientKeyId(Convert.FromBase64String(first.PublicKey)), out _));
        AssertKeyAvailable(keys, second);
        time.Advance(TimeSpan.FromMinutes(30));
        Assert.Null(keys.TryGetKey(MollyTestEnvelope.GetRecipientKeyId(Convert.FromBase64String(second.PublicKey)), out _));
        Assert.NotEqual(second.PublicKey, keys.GetCurrentKey().PublicKey);
    }

    [Fact]
    public void ReadsUseThePublishedSnapshot_UntilMaintenanceRuns()
    {
        var time = new ManualTimeProvider();
        using var server = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes, time);
        MollyTransportKeyResponse first = server.GetTransportKey();
        using var client = new MollyTestEnvelope(first);
        time.Advance(TimeSpan.FromMinutes(32), runTimers: false);
        Assert.Equal(first.PublicKey, server.GetTransportKey().PublicKey);
        AssertAccepted(server, client, time);
        time.Advance(TimeSpan.Zero);
        Assert.NotEqual(first.PublicKey, server.GetTransportKey().PublicKey);
        AssertRejected(server, client, time);
    }

    [Fact]
    public void BackwardWallClockChange_RetainsKeyUntilItsUtcDeadline()
    {
        var time = new ManualTimeProvider();
        using var keys = new MollyTransportKeyRing(MollyTestKeys.TransportPrivateKeyBytes, time);
        MollyTransportKeyResponse first = keys.GetCurrentKey();
        time.UtcNow -= TimeSpan.FromDays(1);
        time.Advance(TimeSpan.FromMinutes(32));
        AssertKeyAvailable(keys, first);
        Assert.Equal(first.PublicKey, keys.GetCurrentKey().PublicKey);

        time.UtcNow = DateTimeOffset.FromUnixTimeSeconds(first.ExpiresAt).AddSeconds(50);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(keys.TryGetKey(MollyTestEnvelope.GetRecipientKeyId(Convert.FromBase64String(first.PublicKey)), out _));
        Assert.NotEqual(first.PublicKey, keys.GetCurrentKey().PublicKey);
    }

    [Fact]
    public void ForwardWallClockChange_ExpiresKeyOnNextMaintenanceTick()
    {
        var time = new ManualTimeProvider();
        using var server = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes, time);
        using var client = new MollyTestEnvelope(server.GetTransportKey());
        time.UtcNow += TimeSpan.FromDays(1);
        AssertAccepted(server, client, time);
        time.Advance(TimeSpan.FromSeconds(10));
        AssertRejected(server, client, time);
    }

    [Fact]
    public void KeyReads_DoNotConsultTheClock()
    {
        var time = new ManualTimeProvider();
        using var keys = new MollyTransportKeyRing(MollyTestKeys.TransportPrivateKeyBytes, time);
        MollyTransportKeyResponse first = keys.GetCurrentKey();
        using var ephemeral = X25519DiffieHellman.GenerateKey();
        byte[] publicKey = ephemeral.ExportPublicKey();
        byte[] shared = new byte[32];
        time.ThrowOnRead = true;

        Assert.Equal(first.PublicKey, keys.GetCurrentKey().PublicKey);
        Assert.True(keys.TryDeriveSharedSecret(MollyTestEnvelope.GetRecipientKeyId(Convert.FromBase64String(first.PublicKey)), publicKey, shared, out bool bootstrap));
        Assert.False(bootstrap);
        Assert.True(keys.TryDeriveSharedSecret(MollyTestEnvelope.GetRecipientKeyId(MollyTestKeys.TransportPublicKeyBytes), publicKey, shared, out bootstrap));
        Assert.True(bootstrap);
        Assert.False(keys.TryDeriveSharedSecret(MollyTestEnvelope.GetRecipientKeyId(MollyTestKeys.OtherTransportPublicKeyBytes), publicKey, shared, out _));
        CryptographicOperations.ZeroMemory(shared);
    }

    [Fact]
    public async Task Readers_DoNotWaitForMaintenance()
    {
        var time = new ManualTimeProvider();
        using var keys = new MollyTransportKeyRing(MollyTestKeys.TransportPrivateKeyBytes, time);
        MollyTransportKeyResponse first = keys.GetCurrentKey();
        using var ephemeral = X25519DiffieHellman.GenerateKey();
        byte[] publicKey = ephemeral.ExportPublicKey();
        var enteredMaintenance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeMaintenance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        time.BeforeUtcNowRead = () =>
        {
            enteredMaintenance.TrySetResult();
            resumeMaintenance.Task.GetAwaiter().GetResult();
        };

        Task maintenance = Task.Run(() => time.Advance(TimeSpan.FromMinutes(30)));
        try
        {
            await enteredMaintenance.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Run(() =>
            {
                Assert.Equal(first.PublicKey, keys.GetCurrentKey().PublicKey);
                byte[] shared = new byte[32];
                Assert.True(keys.TryDeriveSharedSecret(MollyTestEnvelope.GetRecipientKeyId(Convert.FromBase64String(first.PublicKey)), publicKey, shared, out _));
                Assert.True(keys.TryDeriveSharedSecret(MollyTestEnvelope.GetRecipientKeyId(MollyTestKeys.TransportPublicKeyBytes), publicKey, shared, out _));
                CryptographicOperations.ZeroMemory(shared);
            }).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            resumeMaintenance.TrySetResult();
            await maintenance.WaitAsync(TimeSpan.FromSeconds(10));
            time.BeforeUtcNowRead = null;
        }

        Assert.NotEqual(first.PublicKey, keys.GetCurrentKey().PublicKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingReferences_RemainUsableAfterRetirementOrShutdown(bool bootstrap)
    {
        var time = new ManualTimeProvider();
        using var keys = new MollyTransportKeyRing(MollyTestKeys.TransportPrivateKeyBytes, time);
        MollyTransportKeyResponse first = keys.GetCurrentKey();
        byte[] recipientPublicKey = bootstrap ? MollyTestKeys.TransportPublicKeyBytes : Convert.FromBase64String(first.PublicKey);
        byte[] recipientKeyId = MollyTestEnvelope.GetRecipientKeyId(recipientPublicKey);
        MollyTransportKeyRing.TransportKey? key = keys.TryGetKey(recipientKeyId, out bool isBootstrap);
        Assert.NotNull(key);
        Assert.Equal(bootstrap, isBootstrap);
        using var ephemeral = X25519DiffieHellman.GenerateKey();
        byte[] expected = new byte[32];
        byte[] actual = new byte[32];
        ephemeral.DeriveRawSecretAgreement(key.PublicKey, expected);

        if (bootstrap)
        {
            keys.Dispose();
        }
        else
        {
            time.Advance(TimeSpan.FromMinutes(32));
            Assert.Null(keys.TryGetKey(recipientKeyId, out _));
        }

        key.DeriveRawSecretAgreement(ephemeral.ExportPublicKey(), actual);
        Assert.Equal(expected, actual);
        CryptographicOperations.ZeroMemory(expected);
        CryptographicOperations.ZeroMemory(actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetirementBetweenLookupAndDerivation_DoesNotInvalidateReaders(bool bootstrap)
    {
        var time = new ManualTimeProvider();
        using var keys = new MollyTransportKeyRing(MollyTestKeys.TransportPrivateKeyBytes, time);
        byte[] recipientPublicKey = bootstrap
            ? MollyTestKeys.TransportPublicKeyBytes
            : Convert.FromBase64String(keys.GetCurrentKey().PublicKey);
        byte[] recipientKeyId = MollyTestEnvelope.GetRecipientKeyId(recipientPublicKey);
        MollyTransportKeyRing.TransportKey? candidate = keys.TryGetKey(recipientKeyId, out _);
        Assert.NotNull(candidate);

        if (bootstrap)
        {
            keys.Dispose();
        }
        else
        {
            time.Advance(TimeSpan.FromMinutes(32));
        }

        using var peer = X25519DiffieHellman.GenerateKey();
        byte[] publicKey = peer.ExportPublicKey();
        byte[] expected = new byte[32];
        peer.DeriveRawSecretAgreement(recipientPublicKey, expected);
        Parallel.For(0, 128, _ =>
        {
            byte[] shared = new byte[32];
            candidate.DeriveRawSecretAgreement(publicKey, shared);
            Assert.Equal(expected, shared);
            CryptographicOperations.ZeroMemory(shared);
        });
        CryptographicOperations.ZeroMemory(expected);

        if (!bootstrap)
        {
            Assert.False(keys.TryDeriveSharedSecret(recipientKeyId, publicKey, new byte[32], out _));
            AssertKeyAvailable(keys, keys.GetCurrentKey());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreferencedKeys_CanBeCollectedAfterRetirementOrShutdown(bool shutdown)
    {
        var time = new ManualTimeProvider();
        using var keys = new MollyTransportKeyRing(MollyTestKeys.TransportPrivateKeyBytes, time);
        WeakReference reference = CaptureCurrentKeyReference(keys);
        if (shutdown)
        {
            keys.Dispose();
        }
        else
        {
            time.Advance(TimeSpan.FromMinutes(32));
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(reference.IsAlive);
        GC.KeepAlive(keys);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureCurrentKeyReference(MollyTransportKeyRing keys)
    {
        MollyTransportKeyRing.TransportKey? key = keys.TryGetKey(MollyTestEnvelope.GetRecipientKeyId(Convert.FromBase64String(keys.GetCurrentKey().PublicKey)), out _);
        Assert.NotNull(key);
        return new WeakReference(key);
    }

    [Fact]
    public void Restart_DoesNotRecoverPreviousTransportKeysFromTheIdentity()
    {
        var time = new ManualTimeProvider();
        MollyTransportKeyResponse previous;
        using (var server = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes, time))
        {
            previous = server.GetTransportKey();
        }

        using var restarted = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes, time);
        using var oldClient = new MollyTestEnvelope(previous);
        AssertRejected(restarted, oldClient, time);
        Assert.NotEqual(previous.PublicKey, restarted.GetTransportKey().PublicKey);
        using var newClient = new MollyTestEnvelope(restarted.GetTransportKey());
        AssertAccepted(restarted, newClient, time);
    }

    [Fact]
    public void ConcurrentDiscovery_PublishesOnlyOneKeyPerRotation()
    {
        var time = new ManualTimeProvider();
        using var server = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes, time);
        MollyTransportKeyResponse first = server.GetTransportKey();
        time.Advance(TimeSpan.FromMinutes(30));
        string[] publicKeys = new string[32];
        Parallel.For(0, publicKeys.Length, i =>
        {
            MollyTransportKeyResponse key = server.GetTransportKey();
            publicKeys[i] = key.PublicKey;
            using var client = new MollyTestEnvelope(key);
            AssertAccepted(server, client, time);
        });
        Assert.Single(publicKeys.Distinct());
        Assert.DoesNotContain(first.PublicKey, publicKeys);
    }

    [Theory]
    [InlineData("login", null)]
    [InlineData("ping", null)]
    [InlineData("alert", null)]
    [InlineData("associate", null)]
    [InlineData("unknown", null)]
    [InlineData("transport-key", "{}")]
    public void StaticKey_CannotCarryApplicationRequestsOrData(string action, string? data)
    {
        using var server = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes);
        using var bootstrap = new MollyTestEnvelope();
        Assert.False(server.TryDecryptRequest(bootstrap.EncryptRequest(action, data), out _, out _));
    }

    [Fact]
    public void Bootstrap_ReturnsOnlyPublicKeyMetadata_AndIsFreshAndReplayProtected()
    {
        var time = new ManualTimeProvider();
        using var server = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes, time);
        using var bootstrap = new MollyTestEnvelope();
        byte[] body = bootstrap.EncryptRequest("transport-key", timestamp: time.GetUtcNow().ToUnixTimeSeconds());
        Assert.True(server.TryDecryptRequest(body, out MollyApiRequest? request, out byte[]? sessionKey));
        Assert.Equal("transport-key", request.Action);
        byte[] response = server.EncryptResponse(new MollyApiResponse { Status = "ok", Data = server.GetTransportKey() }, sessionKey);
        CryptographicOperations.ZeroMemory(sessionKey);
        JsonElement data = bootstrap.DecryptResponse(response).GetProperty("data");
        Assert.Equal(new[] { "expiresAt", "publicKey" },
            data.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.False(server.TryDecryptRequest(body, out _, out _));
        Assert.False(server.TryDecryptRequest(bootstrap.EncryptRequest("transport-key",
            timestamp: time.GetUtcNow().AddMinutes(-1).ToUnixTimeSeconds()), out _, out _));
    }

    [Fact]
    public void RotatingKey_CannotBeUsedForBootstrap()
    {
        using var server = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes);
        using var client = new MollyTestEnvelope(server.GetTransportKey());
        Assert.False(server.TryDecryptRequest(client.EncryptRequest("transport-key"), out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(47)]
    public void ModifiedRecipientKeyIdOrEphemeralPublicKey_IsRejected(int offset)
    {
        using var server = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes);
        using var client = new MollyTestEnvelope(server.GetTransportKey());
        byte[] body = client.EncryptRequest("ping");
        body[offset] ^= 0x80;
        Assert.False(server.TryDecryptRequest(body, out _, out _));
    }

    [Fact]
    public void SubstitutionWithAnotherValidRecipientKey_IsRejected()
    {
        var time = new ManualTimeProvider();
        using var server = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes, time);
        using var oldClient = new MollyTestEnvelope(server.GetTransportKey());
        time.Advance(TimeSpan.FromMinutes(30));
        MollyTransportKeyResponse next = server.GetTransportKey();
        byte[] body = oldClient.EncryptRequest("ping", timestamp: time.GetUtcNow().ToUnixTimeSeconds());
        MollyTestEnvelope.GetRecipientKeyId(Convert.FromBase64String(next.PublicKey)).CopyTo(body, 0);
        Assert.False(server.TryDecryptRequest(body, out _, out _));
    }

    [Fact]
    public void EnvelopeWithoutItsKeyHeader_IsRejected()
    {
        using var server = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes);
        using var client = new MollyTestEnvelope(server.GetTransportKey());
        byte[] body = client.EncryptRequest("ping");
        Assert.False(server.TryDecryptRequest(body.AsSpan(16), out _, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResponseKeys_AreSeparatedFromRequestsAndOtherExchanges(bool bootstrap)
    {
        using var server = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes);
        using var client = bootstrap ? new MollyTestEnvelope() : new MollyTestEnvelope(server.GetTransportKey());
        string action = bootstrap ? "transport-key" : "ping";
        byte[] body = client.EncryptRequest(action);
        Assert.True(server.TryDecryptRequest(body, out _, out byte[]? responseKey));
        Assert.Equal(32, responseKey.Length);
        byte[] response = server.EncryptResponse(new MollyApiResponse { Status = "ok" }, responseKey);
        using var responseCipher = new XAesGcm(responseKey);
        Assert.False(responseCipher.TryDecrypt(body.AsSpan(48), out _));
        Assert.Equal("ok", client.DecryptResponse(response).GetProperty("status").GetString());
        CryptographicOperations.ZeroMemory(responseKey);

        Assert.True(server.TryDecryptRequest(client.EncryptRequest(action), out _, out byte[]? nextKeys));
        Assert.Equal(32, nextKeys.Length);
        using var nextResponseCipher = new XAesGcm(nextKeys);
        Assert.False(nextResponseCipher.TryDecrypt(response, out _));
        CryptographicOperations.ZeroMemory(nextKeys);
    }

    [Fact]
    public void CompromisedIdentity_CannotDecryptRecordedApplicationTraffic()
    {
        using var server = new MollyRequestProtector(MollyTestKeys.TransportPrivateKeyBytes);
        MollyTransportKeyResponse descriptor = server.GetTransportKey();
        using var client = new MollyTestEnvelope(descriptor);
        byte[] body = client.EncryptRequest("ping");
        Assert.True(server.TryDecryptRequest(body, out _, out byte[]? sessionKeys));
        byte[] response = server.EncryptResponse(new MollyApiResponse { Status = "ok" }, sessionKeys);
        CryptographicOperations.ZeroMemory(sessionKeys);

        using var stolenIdentity = X25519DiffieHellman.ImportPrivateKey(MollyTestKeys.TransportPrivateKeyBytes);
        byte[] shared = new byte[32];
        stolenIdentity.DeriveRawSecretAgreement(body.AsSpan(16, 32), shared);
        byte[] info = [.. "MihuBot.Molly.MollyRequestProtector.v2"u8, .. body.AsSpan(0, 48)];
        byte[] guessedKeys = HKDF.DeriveKey(HashAlgorithmName.SHA512, shared, 64, info: info);
        using var requestCipher = new XAesGcm(guessedKeys.AsSpan(0, 32));
        using var responseCipher = new XAesGcm(guessedKeys.AsSpan(32));
        Assert.False(requestCipher.TryDecrypt(body.AsSpan(48), out _));
        Assert.False(responseCipher.TryDecrypt(response, out _));
        CryptographicOperations.ZeroMemory(shared);
        CryptographicOperations.ZeroMemory(guessedKeys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void Dispose_StopsMaintenanceAndPreventsFurtherLookup(int minutes)
    {
        var time = new ManualTimeProvider();
        var keys = new MollyTransportKeyRing(MollyTestKeys.TransportPrivateKeyBytes, time);
        MollyTransportKeyResponse current = keys.GetCurrentKey();
        time.Advance(TimeSpan.FromMinutes(minutes));
        keys.Dispose();
        keys.Dispose();
        Assert.Throws<ObjectDisposedException>(() => keys.GetCurrentKey());
        Assert.Throws<ObjectDisposedException>(() => keys.TryGetKey(MollyTestEnvelope.GetRecipientKeyId(Convert.FromBase64String(current.PublicKey)), out _));
        time.ThrowOnRead = true;
        time.Advance(TimeSpan.FromHours(2));
    }

    private static void AssertKeyAvailable(MollyTransportKeyRing keys, MollyTransportKeyResponse descriptor)
    {
        MollyTransportKeyRing.TransportKey? material = keys.TryGetKey(MollyTestEnvelope.GetRecipientKeyId(Convert.FromBase64String(descriptor.PublicKey)), out bool bootstrap);
        Assert.NotNull(material);
        Assert.False(bootstrap);
    }

    private static void AssertAccepted(MollyRequestProtector server, MollyTestEnvelope client, TimeProvider time)
    {
        Assert.True(server.TryDecryptRequest(client.EncryptRequest("ping", timestamp: time.GetUtcNow().ToUnixTimeSeconds()), out _, out byte[]? keys));
        CryptographicOperations.ZeroMemory(keys);
    }

    private static void AssertRejected(MollyRequestProtector server, MollyTestEnvelope client, TimeProvider time)
    {
        Assert.False(server.TryDecryptRequest(client.EncryptRequest("ping", timestamp: time.GetUtcNow().ToUnixTimeSeconds()), out MollyApiRequest? request, out byte[]? keys));
        Assert.Null(request);
        Assert.Null(keys);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;
        public bool ThrowOnRead { get; set; }
        public Action? BeforeUtcNowRead { get; set; }
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        public override DateTimeOffset GetUtcNow()
        {
            Assert.False(ThrowOnRead);
            BeforeUtcNowRead?.Invoke();
            return UtcNow;
        }

        public override long GetTimestamp()
        {
            Assert.False(ThrowOnRead);
            return _timestamp;
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan duration, bool runTimers = true)
        {
            UtcNow += duration;
            _timestamp += duration.Ticks;
            if (runTimers)
            {
                foreach (ManualTimer timer in _timers.ToArray())
                {
                    timer.FireIfDue();
                }
            }
        }

        private sealed class ManualTimer(ManualTimeProvider time, TimerCallback callback, object? state) : ITimer
        {
            private long _due = long.MaxValue;
            private long _period;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(TimeSpan.FromSeconds(10), period);
                if (_disposed)
                {
                    return false;
                }

                _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : time._timestamp + dueTime.Ticks;
                _period = period.Ticks;
                return true;
            }

            public void FireIfDue()
            {
                if (!_disposed && _due <= time._timestamp)
                {
                    _due += ((time._timestamp - _due) / _period + 1) * _period;
                    callback(state);
                }
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
