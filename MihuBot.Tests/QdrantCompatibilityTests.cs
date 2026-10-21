using System.Net;
using System.Net.Http.Headers;
using Grpc.Net.Client;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.Qdrant;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;
using Qdrant.Client;
using Qdrant.Client.Grpc;

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SemanticSearchCanEnumerateResults(bool filterByRepository)
    {
        using var handler = new EmptySearchResponseHandler();
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
        using var client = new QdrantClient(new QdrantGrpcClient(channel));
        using var store = new QdrantVectorStore(client, ownsClient: false);
        using var collection = store.GetCollection<Guid, SemanticSearchRecord>("compatibility-test");

        var options = new VectorSearchOptions<SemanticSearchRecord>
        {
            Filter = filterByRepository ? record => record.RepositoryId == 123 : null,
            IncludeVectors = false,
        };
        ReadOnlyMemory<float> embedding = new float[GitHubDbContext.Defaults.EmbeddingDimensions];
        var results = new List<VectorSearchResult<SemanticSearchRecord>>();

        await foreach (var result in collection.SearchAsync(embedding, 10, options))
        {
            results.Add(result);
        }

        Assert.Empty(results);
        Assert.Equal(1, handler.RequestCount);
    }

    private sealed class EmptySearchResponseHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/qdrant.Points/Query", request.RequestUri?.AbsolutePath);
            RequestCount++;

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Version = HttpVersion.Version20,
                Content = new ByteArrayContent(new byte[5]),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
            response.TrailingHeaders.Add("grpc-status", "0");
            return Task.FromResult(response);
        }
    }
}
