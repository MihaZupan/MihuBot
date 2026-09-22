using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using MihuBot.Helpers.AI;
using MihuBot.Tests.Configuration;

namespace MihuBot.Tests.Helpers.AI;

public sealed class OpenAIServiceTests
{
    private const string Personal1 = "mihubotai8467177614.openai.azure.com";
    private const string Personal2 = "mihaz-m30zd4gd-eastus.openai.azure.com";
    private const string Personal3 = "mizup-mud33obs-swedencentral.openai.azure.com";
    private const string Work1 = "issueshelperhu5783781236.openai.azure.com";
    private const string Work2 = "mizup-ma441ssi-eastus2.openai.azure.com";

    [Theory]
    [InlineData("gpt-6-luna", Work1, Personal3)]
    [InlineData("gpt-6-sol", Work1, Personal3)]
    [InlineData("gpt-6-astra", Work2, Personal1, Personal2)]
    [InlineData("gpt-5.6-luna", Work2, Personal1, Personal2)]
    [InlineData("gpt-5.6-terra", Work2, Personal1, Personal2)]
    [InlineData("gpt-5.6-sol", Work2, Personal1, Personal2)]
    public void GetChat_RoutesByDeploymentAndSubscription(string deployment, string workHost, params string[] personalHosts)
    {
        OpenAIService service = CreateService("Key2", "Key3", "WorkKey1", "WorkKey2");

        AssertChat(service.GetChat(deployment, work: false), deployment, personalHosts);
        AssertChat(service.GetChat(deployment, work: true), deployment, workHost);
        AssertResponsesChat(service.GetResponsesChat(deployment, work: false), deployment, personalHosts);
        AssertResponsesChat(service.GetResponsesChat(deployment, work: true), deployment, workHost);
    }

    [Theory]
    [InlineData("gpt-6-luna", "WorkKey2", Personal3)]
    [InlineData("gpt-6-sol", "WorkKey2", Personal3)]
    [InlineData("gpt-6-astra", "WorkKey1", Personal1)]
    [InlineData("gpt-5.6-luna", "WorkKey1", Personal1)]
    [InlineData("gpt-5.6-terra", "WorkKey1", Personal1)]
    [InlineData("gpt-5.6-sol", "WorkKey1", Personal1)]
    public void GetChat_UnavailableWorkDeploymentFallsBackToMatchingPersonalEndpoint(string deployment, string otherWorkKey, string host)
    {
        OpenAIService service = CreateService("Key3", otherWorkKey);

        AssertChat(service.GetChat(deployment, work: true), deployment, host);
        AssertResponsesChat(service.GetResponsesChat(deployment, work: true), deployment, host);
    }

    [Theory]
    [InlineData("gpt-6-luna", Personal3)]
    [InlineData("gpt-6-sol", Personal3)]
    [InlineData("gpt-6-astra", Personal1)]
    public void GetChat_NoWorkCredentialsFallsBackToPersonal(string deployment, string host)
    {
        OpenAIService service = CreateService("Key3");

        AssertChat(service.GetChat(deployment, work: true), deployment, host);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetChat_MissingDeploymentFailsInsteadOfUsingWrongEndpoint(bool work)
    {
        OpenAIService service = CreateService("Key2", "WorkKey2");

        var error = Assert.Throws<InvalidOperationException>(() => service.GetChat("gpt-6-luna", work));

        Assert.Contains("gpt-6-luna", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => service.GetResponsesChat("gpt-6-luna", work));
    }

    [Fact]
    public void GetChat_PersonalRequestsNeverUseWorkEndpoints()
    {
        OpenAIService service = CreateService("WorkKey1");

        Assert.Throws<InvalidOperationException>(() => service.GetChat("gpt-6-luna", work: false));
        Assert.Throws<InvalidOperationException>(() => service.GetResponsesChat("gpt-6-luna", work: false));
        AssertChat(service.GetChat("gpt-6-luna", work: true), "gpt-6-luna", Work1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetChat_UnknownDeploymentFailsExplicitly(bool work)
    {
        OpenAIService service = CreateService("Key2", "Key3", "WorkKey1", "WorkKey2");

        Assert.Throws<InvalidOperationException>(() => service.GetChat("unknown", work));
        Assert.Throws<InvalidOperationException>(() => service.GetResponsesChat("unknown", work));
    }

    [Theory]
    [InlineData(false, Personal3)]
    [InlineData(true, Work1)]
    public void GetChat_ContextUsesWorkSettingAndDefaultModel(bool work, string host)
    {
        var configuration = new TestConfigurationService();
        configuration.Set(42, "ChatGPT.Work", work.ToString());
        OpenAIService service = CreateService(configuration, "Key3", "WorkKey1");

        AssertChat(service.GetChat(42ul), OpenAIService.DefaultModel, host);
    }

    [Fact]
    public void GetChat_ContextHonorsDeploymentAndIgnoresOldRoutingSettings()
    {
        var configuration = new TestConfigurationService();
        configuration.Set(42, "ChatGPT.Deployment", "gpt-6-astra");
        configuration.Set(42, "ChatGPT.Secondary", "true");
        configuration.Set(42, "ChatGPT.Tertiary", "true");
        OpenAIService service = CreateService(configuration, "Key2", "WorkKey2");

        AssertChat(service.GetChat(42ul), "gpt-6-astra", Personal1, Personal2);

        configuration.Set(42, "ChatGPT.Work", "true");

        AssertChat(service.GetChat(42ul), "gpt-6-astra", Work2);
    }

    [Theory]
    [InlineData("text-embedding-3-small", false, false, Personal1)]
    [InlineData("text-embedding-3-small", false, true, Personal1)]
    [InlineData("text-embedding-3-small", true, false, Personal1)]
    [InlineData("text-embedding-3-small", true, true, Work1)]
    [InlineData("text-embedding-3-large", false, false, Personal1)]
    [InlineData("text-embedding-3-large", false, true, Personal1)]
    [InlineData("text-embedding-3-large", true, false, Personal1)]
    [InlineData("text-embedding-3-large", true, true, Work1)]
    public void GetEmbeddingGenerator_RoutesBySubscription(string deployment, bool work, bool workConfigured, string host)
    {
        OpenAIService service = workConfigured ? CreateService("WorkKey1", "WorkKey2") : CreateService("WorkKey2");
        using var generator = service.GetEmbeddingGenerator(deployment, work);
        var metadata = generator.GetService<EmbeddingGeneratorMetadata>();

        Assert.NotNull(metadata);
        Assert.Equal(host, metadata.ProviderUri?.Host);
        Assert.Equal("/openai/v1/", metadata.ProviderUri?.AbsolutePath);
        Assert.Equal(deployment, metadata.DefaultModelId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("dall-e-3")]
    public void GetImage_UnavailableDeploymentReturnsNull(string? deployment)
    {
        var configuration = new TestConfigurationService();

        if (deployment is not null)
        {
            configuration.Set(42, "ChatGPT.ImageDeployment", deployment);
        }

        OpenAIService service = CreateService(configuration, "Key2");

        Assert.Null(service.GetImage(42ul));
    }

    private static void AssertChat(IChatClient client, string deployment, params string[] hosts)
    {
        using (client)
        {
            var metadata = client.GetService<ChatClientMetadata>();

            Assert.NotNull(metadata);
            Assert.Contains(metadata.ProviderUri?.Host, hosts);
            Assert.Equal("/openai/v1/", metadata.ProviderUri?.AbsolutePath);
            Assert.Equal(deployment, metadata.DefaultModelId);
            Assert.IsType<OpenAI.Chat.ChatClient>(client.GetService<OpenAI.Chat.ChatClient>());
        }
    }

    private static void AssertResponsesChat(IChatClient client, string deployment, params string[] hosts)
    {
        using (client)
        {
            var metadata = client.GetService<ChatClientMetadata>();

            Assert.NotNull(metadata);
            Assert.Contains(metadata.ProviderUri?.Host, hosts);
            Assert.Equal("/openai/v1/", metadata.ProviderUri?.AbsolutePath);
            Assert.Equal(deployment, metadata.DefaultModelId);
#pragma warning disable OPENAI001
            Assert.IsAssignableFrom<OpenAI.Responses.ResponsesClient>(client.GetService<OpenAI.Responses.ResponsesClient>());
#pragma warning restore OPENAI001
            Assert.Null(client.GetService<OpenAI.Chat.ChatClient>());
        }
    }

    private static OpenAIService CreateService(params string[] optionalKeys) =>
        CreateService(new TestConfigurationService(), optionalKeys);

    private static OpenAIService CreateService(TestConfigurationService runtimeConfiguration, params string[] optionalKeys)
    {
        var keys = new Dictionary<string, string?> { ["AzureOpenAI:Key"] = "test-key" };

        foreach (string key in optionalKeys)
        {
            keys[$"AzureOpenAI:{key}"] = "test-key";
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(keys).Build();

        // Routing only inspects SDK metadata, never calls the logger or starts its background services.
        var logger = (Logger)RuntimeHelpers.GetUninitializedObject(typeof(Logger));

        return new OpenAIService(configuration, runtimeConfiguration, logger);
    }
}
