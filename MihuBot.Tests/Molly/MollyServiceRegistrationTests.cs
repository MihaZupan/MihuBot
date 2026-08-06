using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MihuBot.Configuration;
using MihuBot.Molly;

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
            ("Molly:ServerKey", MollyTestKeys.ServerKey),
            ("Molly:AppSecret", MollyTestKeys.AppSecret)));
        services.AddMollyServices();

        using ServiceProvider provider = services.BuildServiceProvider();
        var isService = provider.GetRequiredService<IServiceProviderIsService>();

        Assert.NotNull(provider.GetService<MollyRateLimiter>());
        Assert.NotNull(provider.GetService<MollyIdProtector>());

        // MollyService also needs a database and loggers, so only its registration is checked.
        Assert.True(isService.IsService(typeof(MollyService)));
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
            ("Molly:ServerKey", "key"),
            ("Molly:AppSecret", "secret"));

        Assert.True(configuration.IsConfigured(OptionalFeatures.Molly));
    }

    [Fact]
    public void TheFeature_IsDisabled_WhenNothingIsConfigured()
    {
        Assert.False(Configuration().IsConfigured(OptionalFeatures.Molly));
    }

    [Theory]
    [InlineData("Molly:ServerKey")]
    [InlineData("Molly:AppSecret")]
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
            ("Molly:ServerKey", "key"),
            ("Molly:AppSecret", value));

        Assert.False(configuration.IsConfigured(OptionalFeatures.Molly));
    }
}
