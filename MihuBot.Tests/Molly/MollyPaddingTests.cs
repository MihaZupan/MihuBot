using System.Text;
using System.Text.Json;
using MihuBot.Helpers.Crypto;
using MihuBot.Molly;
using MihuBot.Molly.Api;
using MihuBot.Molly.Sharing;

namespace MihuBot.Tests.Molly;

public sealed class MollyPaddingTests : IDisposable
{
    private readonly MollyRequestProtector _server = new(MollyTestKeys.TransportPrivateKeyBytes);
    private readonly MollyTestEnvelope _client;

    public MollyPaddingTests()
    {
        _client = new MollyTestEnvelope(_server.GetTransportKey());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(65535)]
    public void RequestPadding_RoundTripsForApplicationAndDiscovery(int paddingLength)
    {
        foreach (bool bootstrap in new[] { false, true })
        {
            using var client = bootstrap ? new MollyTestEnvelope() : new MollyTestEnvelope(_server.GetTransportKey());
            string action = bootstrap ? "transport-key" : "ping";
            byte[] body = client.EncryptRequest(action, paddingLength: paddingLength);

            Assert.True(_server.TryDecryptRequest(body, out MollyApiRequest? request, out _, out _, out _));
            Assert.Equal(action, request.Action);
        }
    }

    [Fact]
    public void PaddingIsSkippedAsBytes_UsingABigEndianLength()
    {
        byte[] data = Encoding.UTF8.GetBytes(MollyTestEnvelope.RequestJson("ping", """{"value":42}"""));
        byte[] plaintext = new byte[2 + 258 + data.Length];
        plaintext[0] = 0x01;
        plaintext[1] = 0x02;
        plaintext.AsSpan(2, 258).Fill(0xFF);
        data.CopyTo(plaintext, 2 + 258);

        Assert.True(_server.TryDecryptRequest(_client.EncryptRawPlaintext(plaintext), out MollyApiRequest? request, out _, out _, out _));
        Assert.Equal(42, request.Data.GetProperty("value").GetInt32());
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("0000")]
    [InlineData("0001")]
    [InlineData("0003010203")]
    [InlineData("0004010203")]
    [InlineData("FFFF0000")]
    public void MissingTruncatedOrDataLessFrames_AreRejected(string hex)
    {
        byte[] body = _client.EncryptRawPlaintext(Convert.FromHexString(hex));
        Assert.False(_server.TryDecryptRequest(body, out MollyApiRequest? request, out byte[]? responseKey, out MollyPublicKeyId recipientKeyId, out _));
        Assert.Null(request);
        Assert.Null(responseKey);
        Assert.Equal(default(MollyPublicKeyId), recipientKeyId);
    }

    [Fact]
    public void PlainJsonWithoutTheLengthPrefix_IsRejected()
    {
        byte[] json = Encoding.UTF8.GetBytes(MollyTestEnvelope.RequestJson("ping"));
        Assert.False(_server.TryDecryptRequest(_client.EncryptRawPlaintext(json), out _, out _, out _, out _));
    }

    [Fact]
    public void RejectedPadding_DoesNotConsumeTheRequestNonce()
    {
        string json = MollyTestEnvelope.RequestJson("ping");
        byte[] malformed = [0xFF, 0xFF, .. Encoding.UTF8.GetBytes(json)];

        Assert.False(_server.TryDecryptRequest(_client.EncryptRawPlaintext(malformed), out _, out _, out _, out _));
        Assert.True(_server.TryDecryptRequest(_client.Encrypt(json), out _, out _, out _, out _));
    }

    [Fact]
    public void PaddingBytes_AreAuthenticated()
    {
        byte[] body = _client.EncryptRequest("ping", paddingLength: 256);
        body[48 + 24 + 2] ^= 0x80;

        Assert.False(_server.TryDecryptRequest(body, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(768)]
    [InlineData(769)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(4096)]
    [InlineData(65536)]
    public void ResponsePadding_UsesTheSizePolicyAndBigEndianFrame(int dataLength)
    {
        Assert.True(_server.TryDecryptRequest(_client.EncryptRequest("ping"), out _, out byte[]? responseKey, out MollyPublicKeyId recipientKeyId, out _));
        int overhead = JsonSerializer.SerializeToUtf8Bytes(new MollyApiResponse { Status = "ok", Data = "" }).Length;
        string value = new('a', dataLength - overhead);
        var response = new MollyApiResponse { Status = "ok", Data = value };
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(response);
        Assert.Equal(dataLength, data.Length);
        byte[] body = _server.EncryptResponse(response, responseKey, recipientKeyId);

        using var aead = new XAesGcm(responseKey);
        Assert.True(aead.TryDecrypt(body, out byte[]? plaintext));
        int paddingLength = plaintext.Length - 2 - data.Length;
        int minimumPaddingLength = Math.Max(0, 1024 - dataLength);
        Assert.InRange(paddingLength, minimumPaddingLength, minimumPaddingLength + MollyRequestProtector.MaxRandomPaddingLength);

        Assert.Equal((byte)(paddingLength >> 8), plaintext[0]);
        Assert.Equal((byte)(paddingLength & 0xFF), plaintext[1]);
        Assert.Equal(data, plaintext.AsSpan(2 + paddingLength).ToArray());
        Assert.Equal(value, _client.DecryptResponse(body).GetProperty("data").GetString());
    }

    [Fact]
    public void ResponsePadding_ContainsFreshRandomBytes()
    {
        Assert.True(_server.TryDecryptRequest(_client.EncryptRequest("ping"), out _, out byte[]? responseKey, out MollyPublicKeyId recipientKeyId, out _));
        var response = new MollyApiResponse { Status = "ok" };
        byte[] first = _server.EncryptResponse(response, responseKey, recipientKeyId);
        byte[] second = _server.EncryptResponse(response, responseKey, recipientKeyId);

        using var aead = new XAesGcm(responseKey);
        Assert.True(aead.TryDecrypt(first, out byte[]? firstPlaintext));
        Assert.True(aead.TryDecrypt(second, out byte[]? secondPlaintext));
        Assert.NotEqual(firstPlaintext.AsSpan(2, 64).ToArray(), secondPlaintext.AsSpan(2, 64).ToArray());
    }

    [Theory]
    [InlineData(64, false)]
    [InlineData(2048, false)]
    [InlineData(64, true)]
    [InlineData(2048, true)]
    public void Responses_AlwaysGetRandomPaddingOnTopOfTheirSizeClass(int dataLength, bool bucketed)
    {
        Assert.True(_server.TryDecryptRequest(_client.EncryptRequest("ping"), out _, out byte[]? responseKey, out MollyPublicKeyId recipientKeyId, out _));
        var response = new MollyApiResponse { Status = "ok", Data = new string('a', dataLength) };
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(response);
        int sizeClass = data.Length <= 1024 ? 1026 : bucketed ? MollyRequestProtector.PaddingBucketLength : 2 + data.Length;
        using var aead = new XAesGcm(responseKey);
        var lengths = new HashSet<int>();

        for (int i = 0; i < 32; i++)
        {
            byte[] body = _server.EncryptResponse(response, responseKey, recipientKeyId, bucketed);
            Assert.True(aead.TryDecrypt(body, out byte[]? plaintext));
            int paddingLength = (plaintext[0] << 8) | plaintext[1];
            Assert.InRange(plaintext.Length, sizeClass, sizeClass + MollyRequestProtector.MaxRandomPaddingLength);
            Assert.Equal(2 + paddingLength + data.Length, plaintext.Length);
            Assert.Equal(data, plaintext.AsSpan(2 + paddingLength).ToArray());
            lengths.Add(paddingLength);
        }

        Assert.True(lengths.Count > 1);
    }

    [Theory]
    [InlineData(64, 1026)]
    [InlineData(1024, 1026)]
    [InlineData(1025, 16384)]
    [InlineData(16382, 16384)]
    [InlineData(16383, 32768)]
    [InlineData(100_000, 114688)]
    public void SharePadding_RoundsUpToSizeClasses_SmallLikePingsAndLargerIn16KiBBuckets(int dataLength, int expectedLength)
    {
        Assert.True(_server.TryDecryptRequest(_client.EncryptRequest("share-get"), out _, out byte[]? responseKey, out MollyPublicKeyId recipientKeyId, out _));
        int overhead = JsonSerializer.SerializeToUtf8Bytes(new MollyApiResponse { Status = "ok", Data = "" }).Length;
        string value = new('a', dataLength - overhead);
        var response = new MollyApiResponse { Status = "ok", Data = value };
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(response);
        Assert.Equal(dataLength, data.Length);

        byte[] body = _server.EncryptResponse(response, responseKey, recipientKeyId, bucketed: true);

        using var aead = new XAesGcm(responseKey);
        Assert.True(aead.TryDecrypt(body, out byte[]? plaintext));
        Assert.InRange(plaintext.Length, expectedLength, expectedLength + MollyRequestProtector.MaxRandomPaddingLength);

        int paddingLength = (plaintext[0] << 8) | plaintext[1];
        Assert.Equal(plaintext.Length - 2 - data.Length, paddingLength);
        Assert.Equal(data, plaintext.AsSpan(2 + paddingLength).ToArray());
        Assert.Equal(value, _client.DecryptResponse(body).GetProperty("data").GetString());
    }

    [Fact]
    public void ShareFetchResponses_StayInTheirSizeClass_WhateverTheirBase64Content()
    {
        Assert.True(_server.TryDecryptRequest(_client.EncryptRequest("share-fetch"), out _, out byte[]? responseKey, out MollyPublicKeyId recipientKeyId, out _));

        // 0xFB bytes encode as "+/v7", whose '+' the default JSON encoder would escape to six bytes.
        int[] lengths = [.. new byte[] { 0x00, 0xFB }.Select(fill =>
        {
            var data = new MollyShareResponse
            {
                Result = "ok",
                DataKeySealed = Convert.ToBase64String(Enumerable.Repeat(fill, 81).ToArray()),
                EncryptedFile = Convert.ToBase64String(Enumerable.Repeat(fill, 65580).ToArray()),
            };

            return _server.EncryptResponse(new MollyApiResponse { Status = "ok", Data = data }, responseKey, recipientKeyId, bucketed: true).Length;
        })];

        int sizeClass = XAesGcm.CombinedOverheadInBytes + (6 * MollyRequestProtector.PaddingBucketLength);
        Assert.All(lengths, length => Assert.InRange(length, sizeClass, sizeClass + MollyRequestProtector.MaxRandomPaddingLength));
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }
}
