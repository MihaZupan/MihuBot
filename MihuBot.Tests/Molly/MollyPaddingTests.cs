using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MihuBot.Helpers.Crypto;
using MihuBot.Molly;
using MihuBot.Molly.Api;

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

            Assert.True(_server.TryDecryptRequest(body, out MollyApiRequest? request, out _));
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

        Assert.True(_server.TryDecryptRequest(_client.EncryptRawPlaintext(plaintext), out MollyApiRequest? request, out _));
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
        Assert.False(_server.TryDecryptRequest(body, out MollyApiRequest? request, out byte[]? responseKey));
        Assert.Null(request);
        Assert.Null(responseKey);
    }

    [Fact]
    public void PlainJsonWithoutTheLengthPrefix_IsRejected()
    {
        byte[] json = Encoding.UTF8.GetBytes(MollyTestEnvelope.RequestJson("ping"));
        Assert.False(_server.TryDecryptRequest(_client.EncryptRawPlaintext(json), out _, out _));
    }

    [Fact]
    public void RejectedPadding_DoesNotConsumeTheRequestNonce()
    {
        string json = MollyTestEnvelope.RequestJson("ping");
        byte[] malformed = [0xFF, 0xFF, .. Encoding.UTF8.GetBytes(json)];

        Assert.False(_server.TryDecryptRequest(_client.EncryptRawPlaintext(malformed), out _, out _));
        Assert.True(_server.TryDecryptRequest(_client.Encrypt(json), out _, out _));
    }

    [Fact]
    public void PaddingBytes_AreAuthenticated()
    {
        byte[] body = _client.EncryptRequest("ping", paddingLength: 256);
        body[48 + 24 + 2] ^= 0x80;

        Assert.False(_server.TryDecryptRequest(body, out _, out _));
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
        Assert.True(_server.TryDecryptRequest(_client.EncryptRequest("ping"), out _, out byte[]? responseKey));
        int overhead = JsonSerializer.SerializeToUtf8Bytes(new MollyApiResponse { Status = "ok", Data = "" }).Length;
        string value = new('a', dataLength - overhead);
        var response = new MollyApiResponse { Status = "ok", Data = value };
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(response);
        Assert.Equal(dataLength, data.Length);
        byte[] body = _server.EncryptResponse(response, responseKey);

        using var aead = new XAesGcm(responseKey);
        Assert.True(aead.TryDecrypt(body, out byte[]? plaintext));
        int paddingLength = plaintext.Length - 2 - data.Length;
        if (dataLength <= 1024)
        {
            Assert.Equal(1024 - dataLength, paddingLength);
            Assert.Equal(1026, plaintext.Length);
            Assert.Equal(24 + 1026 + 16, body.Length);
        }
        else
        {
            Assert.InRange(paddingLength, 0, 512);
        }

        Assert.Equal((byte)(paddingLength >> 8), plaintext[0]);
        Assert.Equal((byte)(paddingLength & 0xFF), plaintext[1]);
        Assert.Equal(data, plaintext.AsSpan(2 + paddingLength).ToArray());
        Assert.Equal(value, _client.DecryptResponse(body).GetProperty("data").GetString());
    }

    [Fact]
    public void ResponsePadding_ContainsFreshRandomBytes()
    {
        byte[] responseKey = RandomNumberGenerator.GetBytes(32);
        var response = new MollyApiResponse { Status = "ok" };
        byte[] first = _server.EncryptResponse(response, responseKey);
        byte[] second = _server.EncryptResponse(response, responseKey);

        using var aead = new XAesGcm(responseKey);
        Assert.True(aead.TryDecrypt(first, out byte[]? firstPlaintext));
        Assert.True(aead.TryDecrypt(second, out byte[]? secondPlaintext));
        Assert.NotEqual(firstPlaintext.AsSpan(2, 64).ToArray(), secondPlaintext.AsSpan(2, 64).ToArray());
    }

    [Fact]
    public void LargeResponses_UseRandomPaddingLengths()
    {
        byte[] responseKey = RandomNumberGenerator.GetBytes(32);
        var response = new MollyApiResponse { Status = "ok", Data = new string('a', 2048) };
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(response);
        using var aead = new XAesGcm(responseKey);
        var lengths = new HashSet<int>();

        for (int i = 0; i < 32; i++)
        {
            byte[] body = _server.EncryptResponse(response, responseKey);
            Assert.True(aead.TryDecrypt(body, out byte[]? plaintext));
            int paddingLength = (plaintext[0] << 8) | plaintext[1];
            Assert.InRange(paddingLength, 0, 512);
            Assert.Equal(2 + paddingLength + data.Length, plaintext.Length);
            Assert.Equal(data, plaintext.AsSpan(2 + paddingLength).ToArray());
            lengths.Add(paddingLength);
        }

        Assert.True(lengths.Count > 1);
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }
}
