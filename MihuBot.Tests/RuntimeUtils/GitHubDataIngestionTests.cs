using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils.DataIngestion.GitHub;
using Octokit;
using Octokit.Internal;

namespace MihuBot.Tests.RuntimeUtils;

public sealed class GitHubDataIngestionTests
{
    [Fact]
    public async Task RepositoryFailuresDoNotPreventLaterRepositoriesFromUpdating()
    {
        RepositoryInfo[] repositories =
        [
            new() { FullName = "dotnet/android" },
            new() { FullName = "dotnet/aspnetcore" },
            new() { FullName = "dotnet/runtime" },
        ];
        List<string> updated = [];
        var missing = new NotFoundException("Not found.", HttpStatusCode.NotFound);
        var unavailable = new HttpRequestException("Bad gateway.", null, HttpStatusCode.BadGateway);

        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            GitHubDataIngestionService.UpdateRepositoriesAsync(repositories, (repo, _) =>
            {
                updated.Add(repo.FullName);
                return repo.FullName switch
                {
                    "dotnet/android" => Task.FromException(unavailable),
                    "dotnet/aspnetcore" => Task.FromException(missing),
                    _ => Task.CompletedTask,
                };
            }, CancellationToken.None));

        Assert.Equal(repositories.Select(r => r.FullName), updated);
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Same(unavailable, error.InnerExceptions[0].InnerException);
        Assert.Contains("dotnet/android", error.InnerExceptions[0].Message, StringComparison.Ordinal);
        Assert.Same(missing, error.InnerExceptions[1].InnerException);
        Assert.Contains("dotnet/aspnetcore", error.InnerExceptions[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulRepositoryUpdatesDoNotThrow()
    {
        int updated = 0;

        await GitHubDataIngestionService.UpdateRepositoriesAsync([new(), new()], (_, _) =>
        {
            updated++;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(2, updated);
    }

    [Fact]
    public async Task CancellationStopsRepositoryUpdatesWithoutBeingAggregated()
    {
        using var cancellation = new CancellationTokenSource();
        int updated = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GitHubDataIngestionService.UpdateRepositoriesAsync([new(), new()], (_, token) =>
            {
                Assert.Equal(cancellation.Token, token);
                updated++;
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }, cancellation.Token));

        Assert.Equal(1, updated);
    }

    [Fact]
    public async Task MissingPullRequestDetailsPreserveIssueMetadataWithoutCreatingEmptyDetails()
    {
        using var transport = new PullRequestTransport(HttpStatusCode.NotFound);
        using var parent = CreateService(transport.Client);
        var context = new GitHubDataIngestionService.UpdateContext
        {
            Parent = parent,
            DbContext = null!, // A missing PR must not query or create a PR record.
            RepoId = 123,
        };
        var issue = new IssueInfo
        {
            Id = "issue-node",
            Number = 42,
            HtmlUrl = "https://github.com/dotnet/aspnetcore/pull/42",
            Title = "Current title",
            Body = "Current body",
        };

        await context.UpdatePullRequestInfoAsync(new PullRequest(), issue, CancellationToken.None);

        Assert.Equal(IssueType.PullRequest, issue.IssueType);
        Assert.Equal("issue-node", issue.Id);
        Assert.Equal("Current title", issue.Title);
        Assert.Equal("Current body", issue.Body);
        Assert.Equal("/repositories/123/pulls/42", Assert.Single(transport.Requests).AbsolutePath);
        Assert.Equal(1, context.ApiCallsPerformed);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task OtherPullRequestFailuresAreNotTreatedAsMissingDetails(HttpStatusCode status)
    {
        using var transport = new PullRequestTransport(status);
        using var parent = CreateService(transport.Client);
        var context = new GitHubDataIngestionService.UpdateContext
        {
            Parent = parent,
            DbContext = null!,
            RepoId = 123,
        };

        var error = await Assert.ThrowsAnyAsync<ApiException>(() =>
            context.UpdatePullRequestInfoAsync(new PullRequest(), new IssueInfo { Number = 42 }, CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public void NonRateLimitFailuresDoNotDelayHealthyRepositories()
    {
        var error = new AggregateException(
            new InvalidOperationException("Repository failed.", new NotFoundException("Not found.", HttpStatusCode.NotFound)),
            new HttpRequestException("Bad gateway.", null, HttpStatusCode.BadGateway));

        Assert.Equal(TimeSpan.Zero, GitHubDataIngestionService.GetRateLimitDelay(error));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public async Task AggregatedFailuresPreserveCappedGitHubRateLimitDelay(int resetMinutes)
    {
        using var transport = new PullRequestTransport(HttpStatusCode.Forbidden, DateTimeOffset.UtcNow.AddMinutes(resetMinutes));
        var rateLimit = await Assert.ThrowsAsync<RateLimitExceededException>(() => transport.Client.PullRequest.Get(123, 42));
        var error = new AggregateException(
            new HttpRequestException("Bad gateway."),
            new AggregateException(new InvalidOperationException("Repository failed.", rateLimit)));

        TimeSpan delay = GitHubDataIngestionService.GetRateLimitDelay(error);

        if (resetMinutes > 5)
        {
            Assert.Equal(TimeSpan.FromMinutes(5), delay);
        }
        else
        {
            Assert.InRange(delay, TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(resetMinutes));
        }
    }

    private static GitHubDataIngestionService CreateService(GitHubClient github) =>
        new(NullLogger<GitHubDataIngestionService>.Instance, null!, null!, github,
            Options.Create(new GitHubClientOptions { ProductName = "tests", Token = "test-token" }), null!);

    private sealed class PullRequestTransport(HttpStatusCode status, DateTimeOffset? rateLimitReset = null) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public GitHubClient Client => new(new Connection(new ProductHeaderValue("tests"), new HttpClientAdapter(() => this)));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            string body = rateLimitReset.HasValue
                ? """{"message":"API rate limit exceeded"}"""
                : """{"message":"Request failed"}""";
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };

            if (rateLimitReset is { } reset)
            {
                response.Headers.Add("X-RateLimit-Limit", "5000");
                response.Headers.Add("X-RateLimit-Remaining", "0");
                response.Headers.Add("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return Task.FromResult(response);
        }
    }
}
