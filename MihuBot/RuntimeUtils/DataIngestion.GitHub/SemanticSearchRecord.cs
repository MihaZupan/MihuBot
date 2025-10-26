using Microsoft.Extensions.VectorData;
using MihuBot.DB.GitHub;

namespace MihuBot.RuntimeUtils.DataIngestion.GitHub;

public sealed class SemanticSearchRecord
{
    [VectorStoreKey]
    public Guid Id { get; set; }

    [VectorStoreData(IsIndexed = true)]
    public long RepositoryId { get; set; }

    [VectorStoreData]
    public string IssueId { get; set; }

    [VectorStoreData]
    public string SubIdentifier { get; set; }

    [VectorStoreVector(GitHubDbContext.Defaults.EmbeddingDimensions, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Vector { get; set; }
}
