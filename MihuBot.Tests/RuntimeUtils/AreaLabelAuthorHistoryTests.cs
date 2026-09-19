using Microsoft.EntityFrameworkCore;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.AI;
using static MihuBot.RuntimeUtils.AI.AreaLabelDetector;

namespace MihuBot.Tests.RuntimeUtils;

public sealed class AreaLabelAuthorHistoryTests
{
    private static readonly DateTime TargetTime = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] AreaLabels = ["area-A", "area-B", "area-Legacy"];
    private static readonly string[] CandidateLabels = ["area-A", "area-B"];

    [Theory]
    [InlineData(2026, 9, 19)]
    [InlineData(2024, 2, 29)]
    public void AuthorQueryUsesTheYearBeforeTheTargetPr(int year, int month, int day)
    {
        DateTime createdAt = new(year, month, day, 12, 0, 0, DateTimeKind.Utc);
        DateTime cutoff = createdAt.AddYears(-1);
        IssueInfo[] items =
        [
            Item(1, cutoff),
            Item(2, cutoff.AddTicks(-1)),
            Item(3, createdAt.AddMonths(-6)),
            Item(4, createdAt),
            Item(5, createdAt.AddTicks(1)),
        ];
        var results = QueryAuthorPullRequests(items.AsQueryable(), Item(100, createdAt), AreaLabels, CandidateLabels);

        Assert.Equal([3, 1], results.Select(pr => pr.Number));
    }

    [Fact]
    public void AuthorQuerySelectsOnlyPreviousPrsFromTheSameAuthorAndRepository()
    {
        var target = Item(100, TargetTime);
        var previous = Item(99, TargetTime.AddDays(-1));
        var sameTimePrevious = Item(98, TargetTime);
        var sameTimeLater = Item(101, TargetTime);
        var newer = Item(97, TargetTime.AddDays(1));
        var otherAuthor = Item(96, TargetTime.AddDays(-1));
        otherAuthor.UserId++;
        var otherRepository = Item(95, TargetTime.AddDays(-1));
        otherRepository.RepositoryId++;
        var issue = Item(94, TargetTime.AddDays(-1));
        issue.IssueType = IssueType.Issue;
        var discussion = Item(93, TargetTime.AddDays(-1));
        discussion.IssueType = IssueType.Discussion;
        previous.State = Octokit.ItemState.Closed;

        IssueInfo[] items = [newer, otherAuthor, target, previous, sameTimeLater, discussion, sameTimePrevious, otherRepository, issue];
        var result = Assert.Single(QueryAuthorPullRequests(items.AsQueryable(), target, AreaLabels, CandidateLabels));

        Assert.Equal(99, result.Number);
        Assert.Equal("area-A", result.AreaLabel);
    }

    [Fact]
    public void AuthorQueryFiltersToExactlyOneActiveAreaBeforeTakingTheLatestFifty()
    {
        var target = Item(200, TargetTime);
        var items = Enumerable.Range(1, 120).Select(number =>
        {
            var item = Item(number, TargetTime.AddMinutes(number - 200));
            item.Labels = number switch
            {
                <= 60 => [new() { Name = "area-A" }, new() { Name = "bug" }],
                <= 80 => [],
                <= 100 => [new() { Name = "area-A" }, new() { Name = "area-B" }],
                <= 110 => [new() { Name = "area-Legacy" }],
                _ => [new() { Name = "area-A" }, new() { Name = "area-Legacy" }],
            };
            return item;
        }).AsQueryable();
        var results = QueryAuthorPullRequests(items, target, AreaLabels, CandidateLabels).ToArray();

        Assert.Equal(AuthorHistorySampleSize, results.Length);
        Assert.Equal(Enumerable.Range(11, 50).Reverse(), results.Select(r => r.Number));
        Assert.All(results, r => Assert.Equal("area-A", r.AreaLabel));
    }

    [Fact]
    public void AuthorQuerySupportsOtherRequestedLabelPrefixes()
    {
        var item = Item(1, TargetTime.AddDays(-1));
        item.Labels = [new() { Name = "component:VM" }, new() { Name = "area-A" }];
        var result = Assert.Single(QueryAuthorPullRequests(new[] { item }.AsQueryable(), Item(100, TargetTime),
            ["component:VM", "component:other"], ["component:VM"]));

        Assert.Equal("component:VM", result.AreaLabel);
    }

    [Fact]
    public void AuthorQueryUsesStableUserIdsRatherThanLogins()
    {
        var target = Item(100, TargetTime);
        target.User = new() { Login = "new-login" };
        var previous = Item(99, TargetTime.AddDays(-1));
        previous.User = new() { Login = "old-login" };

        Assert.Single(QueryAuthorPullRequests(new[] { previous }.AsQueryable(), target, AreaLabels, CandidateLabels));
    }

    [Fact]
    public void AuthorQueryTranslatesItsAreaFilterToPostgresWithoutLoadingBodiesOrComments()
    {
        using var db = new GitHubDbContext(new DbContextOptionsBuilder<GitHubDbContext>()
            .UseNpgsql("Host=localhost;Database=author_history_query_test").Options);
        string sql = QueryAuthorPullRequests(db.Issues.AsNoTracking(), Item(100, TargetTime), AreaLabels, CandidateLabels).ToQueryString();

        Assert.Contains("LIMIT", sql, StringComparison.Ordinal);
        Assert.Contains("count(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"UserId\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"RepositoryId\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"CreatedAt\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Body\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("comments", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthorAreasAreCanonicalAndUseOneSampleDenominator()
    {
        AuthorPullRequest[] previous =
        [
            .. Enumerable.Range(1, 5).Select(n => Previous(n, n % 2 == 0 ? "area-test" : "AREA-TEST")),
        ];
        previous[0] = previous[0] with { Title = new string('t', 300) };
        var history = AnalyzeAuthorHistory("author", previous, ["area-Test"]);

        Assert.Equal("author", history.Author);
        Assert.Equal(5, history.SampledPullRequests);
        Assert.Equal(new AuthorAreaFrequency("area-Test", 5, 1), Assert.Single(history.Areas));
        Assert.Equal(["area-Test"], history.CommonAreas);
        Assert.All(history.PullRequests, pr => Assert.Equal("area-Test", pr.AreaLabel));
        Assert.Equal(MaxRelatedTitleCharacters, history.PullRequests[0].Title.Length);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void CommonAreaRequiresAtLeastFiveSupportingPrs(int count, bool common)
    {
        var history = AnalyzeAuthorHistory("author",
            [.. Enumerable.Range(1, count).Select(n => Previous(n, "area-A"))], ["area-A"]);

        Assert.Equal(common ? ["area-A"] : Array.Empty<string>(), history.CommonAreas);
    }

    [Theory]
    [InlineData(50, 5, false)]
    [InlineData(50, 9, false)]
    [InlineData(50, 10, true)]
    [InlineData(50, 11, true)]
    [InlineData(25, 5, true)]
    [InlineData(26, 5, false)]
    [InlineData(20, 4, false)]
    [InlineData(10, 2, false)]
    [InlineData(10, 4, false)]
    [InlineData(10, 5, true)]
    public void CommonAreaMustCoverTwentyPercentOfTheEligibleHistory(int sampled, int count, bool common)
    {
        var previous = Enumerable.Range(0, sampled)
            .Select(i => Previous(i, i < count ? "area-A" : "area-B"))
            .ToArray();
        var history = AnalyzeAuthorHistory("author", previous, ["area-A", "area-B"]);

        Assert.Equal(sampled, history.SampledPullRequests);
        Assert.Equal(count / (double)sampled, history.Areas.Single(a => a.Label == "area-A").FractionOfPullRequests);
        Assert.Equal(common, history.CommonAreas.Contains("area-A", StringComparer.Ordinal));
    }

    [Fact]
    public void AllAreasMeetingTheBarAreIncludedWithoutDominanceOrTopThreeRestrictions()
    {
        string[] labels = ["area-A", "area-B", "area-C", "area-D", "area-E"];
        var previous = Enumerable.Range(0, 50).Select(i => Previous(i, labels[i % labels.Length])).ToArray();
        var history = AnalyzeAuthorHistory("author", previous, labels);

        Assert.All(history.Areas, area => Assert.Equal(0.2, area.FractionOfPullRequests));
        Assert.Equal(labels, history.CommonAreas);
    }

    [Fact]
    public void AreasDoNotNeedATwofoldGap()
    {
        var previous = Enumerable.Repeat("area-A", 13)
            .Concat(Enumerable.Repeat("area-B", 11))
            .Concat(Enumerable.Repeat("area-C", 6))
            .Select((label, i) => Previous(i, label))
            .ToArray();
        var history = AnalyzeAuthorHistory("author", previous, ["area-A", "area-B", "area-C"]);

        Assert.Equal(["area-A", "area-B", "area-C"], history.CommonAreas);
    }

    [Fact]
    public void EmptyHistoryHasNoAreaFrequencies()
    {
        var history = AnalyzeAuthorHistory("author", [], ["area-A"]);

        Assert.Equal(0, history.SampledPullRequests);
        Assert.Empty(history.Areas);
        Assert.Empty(history.CommonAreas);
    }

    private static IssueInfo Item(int number, DateTime createdAt) => new()
    {
        Id = $"PR-{number}", Number = number, RepositoryId = 1, UserId = 7,
        IssueType = IssueType.PullRequest, CreatedAt = createdAt, Title = $"PR {number}",
        HtmlUrl = $"https://github.com/o/r/pull/{number}", Labels = [new() { Name = "area-A" }],
    };

    private static AuthorPullRequest Previous(int number, string areaLabel) =>
        new(number, $"https://github.com/o/r/pull/{number}", $"PR {number}", TargetTime.AddDays(-1), areaLabel);
}
