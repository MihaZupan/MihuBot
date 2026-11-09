using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MihuBot.Configuration;
using MihuBot.Molly;
using MihuBot.Molly.Api;

namespace MihuBot.Tests.Molly;

public sealed class MollyServiceRegistrationTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    [Fact]
    public void AddMollyServices_RegistersEverythingTheEndpointsNeed()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Configuration(
            ("Molly:DatabaseKey", MollyTestKeys.DatabaseKey),
            ("Molly:TransportPrivateKey", MollyTestKeys.TransportPrivateKey)));
        services.AddMollyServices();

        using ServiceProvider provider = services.BuildServiceProvider();
        var isService = provider.GetRequiredService<IServiceProviderIsService>();

        Assert.NotNull(provider.GetService<MollyRateLimiter>());
        Assert.NotNull(provider.GetService<MollyIdProtector>());
        Assert.NotNull(provider.GetService<MollyRequestProtector>());

        // MollyService also needs a database and loggers, so only its registration is checked.
        Assert.True(isService.IsService(typeof(MollyService)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfiguredDiscoveryKeys_AllAuthenticateDiscovery_AndAdvertiseTheLastKey(bool reverseOrder)
    {
        string[] privateKeys = [MollyTestKeys.TransportPrivateKey, MollyTestKeys.OtherTransportPrivateKey];
        byte[][] publicKeys = [MollyTestKeys.TransportPublicKeyBytes, MollyTestKeys.OtherTransportPublicKeyBytes];

        if (reverseOrder)
        {
            Array.Reverse(privateKeys);
            Array.Reverse(publicKeys);
        }

        using var server = new MollyRequestProtector(Configuration(
            (OptionalFeatures.MollyTransportPrivateKeyName, $" {privateKeys[0]} , {privateKeys[1]} ")));

        foreach (byte[] publicKey in publicKeys)
        {
            using var bootstrap = new MollyTestEnvelope(publicKey);
            Assert.True(server.TryDecryptRequest(bootstrap.EncryptRequest("transport-key"), out _, out byte[]? responseKey, out MollyPublicKeyId recipientKeyId));
            byte[] response = server.EncryptResponse(new MollyApiResponse { Status = "ok", Data = server.GetTransportKey() }, responseKey, recipientKeyId);
            CryptographicOperations.ZeroMemory(responseKey);
            var data = bootstrap.DecryptResponse(response).GetProperty("data");
            Assert.Equal(Convert.ToBase64String(publicKeys[^1]), data.GetProperty("discoveryPublicKey").GetString());
            Assert.Equal(server.GetTransportKey().PublicKey, data.GetProperty("publicKey").GetString());
            Assert.DoesNotContain(Convert.FromBase64String(data.GetProperty("publicKey").GetString()!), publicKeys);
            Assert.False(server.TryDecryptRequest(bootstrap.EncryptRequest("ping"), out _, out _, out _));
            Assert.False(server.TryDecryptRequest(bootstrap.EncryptRequest("transport-key", "{}"), out _, out _, out _));

            using var client = new MollyTestEnvelope(Convert.FromBase64String(data.GetProperty("publicKey").GetString()!));
            Assert.True(server.TryDecryptRequest(client.EncryptRequest("ping"), out _, out responseKey, out _));
            CryptographicOperations.ZeroMemory(responseKey);
        }
    }

    [Theory]
    [InlineData("not-base64", typeof(FormatException))]
    [InlineData("", typeof(ArgumentOutOfRangeException))]
    [InlineData("AA==", typeof(ArgumentOutOfRangeException))]
    public void ConfiguredDiscoveryKeys_RejectAnyInvalidEntry(string invalidKey, Type exceptionType)
    {
        IConfiguration configuration = Configuration(
            (OptionalFeatures.MollyTransportPrivateKeyName, $"{MollyTestKeys.TransportPrivateKey},{invalidKey}"));

        Assert.Throws(exceptionType, () => new MollyRequestProtector(configuration));
    }

    [Fact]
    public void RemovingADiscoveryKey_RejectsItsClients_AndRetainsTheLatestPin()
    {
        using var original = new MollyRequestProtector(Configuration(
            (OptionalFeatures.MollyTransportPrivateKeyName, $"{MollyTestKeys.TransportPrivateKey},{MollyTestKeys.OtherTransportPrivateKey}")));
        byte[] latestPublicKey = Convert.FromBase64String(original.GetTransportKey().DiscoveryPublicKey);
        using var restarted = new MollyRequestProtector(Configuration(
            (OptionalFeatures.MollyTransportPrivateKeyName, MollyTestKeys.OtherTransportPrivateKey)));
        using var oldClient = new MollyTestEnvelope();
        using var updatedClient = new MollyTestEnvelope(latestPublicKey);

        Assert.False(restarted.TryDecryptRequest(oldClient.EncryptRequest("transport-key"), out _, out _, out _));
        Assert.True(restarted.TryDecryptRequest(updatedClient.EncryptRequest("transport-key"), out _, out byte[]? responseKey, out _));
        CryptographicOperations.ZeroMemory(responseKey);
    }

    [Theory]
    [InlineData("Molly:AlertEmailConnectionString")]
    [InlineData("Molly:AlertEmailFrom")]
    public void TheEmailFeature_IsDisabled_WhenAKeyIsMissing(string missingKey)
    {
        (string, string)[] values = [.. new[]
        {
            ("Molly:AlertEmailConnectionString", "connection-string"),
            ("Molly:AlertEmailFrom", "molly@example.com"),
        }.Where(v => v.Item1 != missingKey)];

        Assert.False(Configuration(values).IsConfigured(OptionalFeatures.MollyAlertEmail));
    }

    [Fact]
    public void TheFeature_IsEnabled_WhenBothKeysAreConfigured()
    {
        IConfiguration configuration = Configuration(
            ("Molly:DatabaseKey", "key"),
            ("Molly:TransportPrivateKey", "secret"));

        Assert.True(configuration.IsConfigured(OptionalFeatures.Molly));
    }

    [Fact]
    public void TheFeature_IsDisabled_WhenNothingIsConfigured()
    {
        Assert.False(Configuration().IsConfigured(OptionalFeatures.Molly));
    }

    [Theory]
    [InlineData("Molly:DatabaseKey")]
    [InlineData("Molly:TransportPrivateKey")]
    public void TheFeature_IsDisabled_WhenAKeyIsMissing(string configuredKey)
    {
        // Both keys are required - a half configured deployment must not serve the API.
        Assert.False(Configuration((configuredKey, "value")).IsConfigured(OptionalFeatures.Molly));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TheFeature_IsDisabled_WhenAKeyIsBlank(string value)
    {
        IConfiguration configuration = Configuration(
            ("Molly:DatabaseKey", "key"),
            ("Molly:TransportPrivateKey", value));

        Assert.False(configuration.IsConfigured(OptionalFeatures.Molly));
    }
}
