namespace MihuBot.RuntimeUtils.Search;

public sealed record SearchTimings
{
    public TimeSpan EmbeddingGeneration { get; set; }

    public TimeSpan VectorSearch { get; set; }

    public TimeSpan FullTextSearch { get; set; }

    public TimeSpan Database { get; set; }

    public TimeSpan Reclassification { get; set; }

    public override string ToString()
    {
        return $"{nameof(EmbeddingGeneration)}={(int)EmbeddingGeneration.TotalMilliseconds} ms, " +
               $"{nameof(VectorSearch)}={(int)VectorSearch.TotalMilliseconds} ms, " +
               $"{nameof(FullTextSearch)}={(int)FullTextSearch.TotalMilliseconds} ms, " +
               $"{nameof(Database)}={(int)Database.TotalMilliseconds} ms" +
               (Reclassification == default ? "" : $", {nameof(Reclassification)}={(int)Reclassification.TotalMilliseconds} ms");
    }
}
