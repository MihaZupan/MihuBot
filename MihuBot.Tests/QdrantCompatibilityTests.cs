using Microsoft.SemanticKernel.Connectors.Qdrant;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;
using Qdrant.Client;

namespace MihuBot.Tests;

public sealed class QdrantCompatibilityTests
{
    [Fact]
    public void SemanticSearchCollectionCanBeCreatedWithoutConnecting()
    {
        using var client = new QdrantClient("localhost");
        using var store = new QdrantVectorStore(client, ownsClient: false);
        using var collection = store.GetCollection<Guid, SemanticSearchRecord>("compatibility-test");

        Assert.NotNull(collection);
    }
}
