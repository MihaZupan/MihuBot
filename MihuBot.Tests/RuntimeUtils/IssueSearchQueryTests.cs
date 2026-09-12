using MihuBot.DB.GitHub;
using MihuBot.Helpers.AI;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;
using MihuBot.RuntimeUtils.Search;

namespace MihuBot.Tests.RuntimeUtils;

public sealed class IssueSearchQueryTests
{
    [Theory]
    [InlineData(IssueType.Issue, "Issue")]
    [InlineData(IssueType.PullRequest, "Pull Request")]
    [InlineData(IssueType.Discussion, "Discussion")]
    public void IncludesLocalIssueMetadataAndTrimmedBody(IssueType type, string displayType)
    {
        var issue = CreateIssue("  Reproduction steps\nMore details. \r\n");
        issue.IssueType = type;

        string query = GitHubSearchService.CreateIssueQuery(issue);

        Assert.Equal($"dotnet/runtime#123: Example title\n{displayType} author: author\n\nReproduction steps\nMore details.", query.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n ")]
    public void EmptyBodyStillProducesAUsefulQuery(string? body)
    {
        string query = GitHubSearchService.CreateIssueQuery(CreateIssue(body));

        Assert.Equal("dotnet/runtime#123: Example title\nIssue author: author\n\n", query.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void LongBodyIsTruncatedToTheSearchTokenLimit()
    {
        var tokenizer = GitHubSemanticSearchIngestionService.Tokenizer;
        var issue = CreateIssue(string.Concat(Enumerable.Repeat("Some detailed reproduction steps. ", 10_000)));

        string query = GitHubSearchService.CreateIssueQuery(issue);

        Assert.StartsWith("dotnet/runtime#123: Example title\nIssue author: author\n\n", query.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.InRange(tokenizer.CountTokens(query), 1, SemanticMarkdownChunker.MaxSectionTokens);
        Assert.True(query.Length < issue.Body.Length);
    }

    [Fact]
    public void FullIssueQueryUsesSeparateRerankingContext()
    {
        string query = GitHubSearchService.CreateIssueQuery(CreateIssue("Long body containing detailed logs and reproduction steps."));
        const string context = "Find issues related to Example title and socket timeouts.";
        var filters = new IssueSearchBulkFilters
        {
            PostProcessingContextOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [query] = context,
            },
        };

        Assert.Equal(context, filters.GetPostProcessingContext(query));
        Assert.DoesNotContain("Long body", filters.GetPostProcessingContext(query), StringComparison.Ordinal);
        Assert.Contains("Long body", query, StringComparison.Ordinal);
        Assert.Equal("The user searched for 'socket timeouts'.", filters.GetPostProcessingContext("socket timeouts"));
    }

    [Theory]
    [InlineData(null, "The user searched for 'socket timeouts'.")]
    [InlineData("Search for {{SearchTerm}} about networking.", "Search for socket timeouts about networking.")]
    public void ExistingRerankingTemplatesArePreserved(string? template, string expected)
    {
        var filters = new IssueSearchBulkFilters { PostProcessingContext = template };

        Assert.Equal(expected, filters.GetPostProcessingContext("socket timeouts"));
    }

    [Fact]
    public void RerankingOverrideDoesNotInterpolateTheRetrievalQuery()
    {
        const string context = "An issue about literal {{SearchTerm}} placeholders.";
        var filters = new IssueSearchBulkFilters
        {
            PostProcessingContextOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["whole issue text"] = context,
            },
        };

        Assert.Equal(context, filters.GetPostProcessingContext("whole issue text"));
    }

    private static IssueInfo CreateIssue(string? body) => new()
    {
        Repository = new RepositoryInfo { FullName = "dotnet/runtime" },
        Number = 123,
        Title = "Example title",
        User = new UserInfo { Login = "author" },
        Body = body!,
        IssueType = IssueType.Issue,
    };
}
