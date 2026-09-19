using MihuBot.Helpers;

namespace MihuBot.Tests.Helpers;

public sealed class GitHubHelperTests
{
    [Theory]
    [InlineData("See #123.", "dotnet/runtime", 123)]
    [InlineData("Backport dotnet/aspnetcore#456", "dotnet/aspnetcore", 456)]
    [InlineData("[original](https://github.com/dotnet/runtime/pull/123)", "dotnet/runtime", 123)]
    [InlineData("<https://github.com/dotnet/runtime/issues/123#issuecomment-1>", "dotnet/runtime", 123)]
    [InlineData("https://github.com/dotnet/runtime/pull/123/files", "dotnet/runtime", 123)]
    [InlineData("https://github.com/dotnet/runtime/issues/123.", "dotnet/runtime", 123)]
    [InlineData("HTTP://GITHUB.COM/dotnet/runtime/issues/123/", "dotnet/runtime", 123)]
    [InlineData("See `#7` or the description.", "dotnet/runtime", 7)]
    public void ExtractIssueOrPullRequestReferencesSupportsLinksAndNumbers(string text, string repository, int number)
    {
        Assert.Equal((repository, number), Assert.Single(GitHubHelper.ExtractIssueOrPullRequestReferences(text, "dotnet/runtime")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("# Heading\n123\n#0 #2147483648 #123abc")]
    [InlineData("https://example.com/o/r/issues/123#456")]
    [InlineData("https://github.com.evil.example/o/r/pull/123#456")]
    [InlineData("https://github.com/o/r/discussions/123")]
    [InlineData("https://github.com/o/r/blob/main/file.cs#123")]
    [InlineData("https://github.com/o/r/issues/not-a-number")]
    [InlineData("abc#123 /path#456")]
    public void ExtractIssueOrPullRequestReferencesIgnoresOtherUrlsAndNonReferences(string text)
    {
        Assert.Empty(GitHubHelper.ExtractIssueOrPullRequestReferences(text, "dotnet/runtime"));
    }

    [Theory]
    [InlineData("https://github.com/dotnet/runtime/commit/0123456789abcdef0123456789abcdef01234567", "dotnet/runtime", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("https://github.com/owner/repo.name/commit/ABCDEF1", "owner/repo.name", "ABCDEF1")]
    public void TryParseGitHubCommit_ValidUrl(string input, string expectedRepository, string expectedCommit)
    {
        Assert.True(GitHubHelper.TryParseGitHubCommit(input, out string? repository, out string? commit));
        Assert.Equal(expectedRepository, repository);
        Assert.Equal(expectedCommit, commit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("dotnet/runtime@0123456")]
    [InlineData("https://example.com/dotnet/runtime/commit/0123456")]
    [InlineData("https://github.com/dotnet/runtime/commit/not-a-commit")]
    public void TryParseGitHubCommit_InvalidUrl(string input)
    {
        Assert.False(GitHubHelper.TryParseGitHubCommit(input, out _, out _));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("release/11.0")]
    [InlineData("feature/issue-123")]
    public void IsSafeGitHubBranchName_ValidName(string branch)
    {
        Assert.True(GitHubHelper.IsSafeGitHubBranchName(branch));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-branch")]
    [InlineData("feature//branch")]
    [InlineData("feature/../main")]
    [InlineData("branch;command")]
    public void IsSafeGitHubBranchName_InvalidName(string branch)
    {
        Assert.False(GitHubHelper.IsSafeGitHubBranchName(branch));
    }
}
