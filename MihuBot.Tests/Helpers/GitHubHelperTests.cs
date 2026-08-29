using MihuBot.Helpers;

namespace MihuBot.Tests.Helpers;

public sealed class GitHubHelperTests
{
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
