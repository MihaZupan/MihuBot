using MihuBot.RuntimeUtils;
using Octokit;

namespace MihuBot.Commands;

public sealed class NclMentionsCommand : CommandBase
{
    public override string Command => "ncl-mentions";
    public override string[] Aliases => ["ncl-mentions-rescan"];

    private readonly GitHubNotificationsService _gitHubNotifications;
    private GitHubClient GitHub => _gitHubNotifications.Github;

    public NclMentionsCommand(GitHubNotificationsService gitHubNotifications)
    {
        _gitHubNotifications = gitHubNotifications;
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (!ctx.Author.IsAdmin())
        {
            return;
        }

        if (ctx.Command == "ncl-mentions-rescan")
        {
            var now = DateTime.UtcNow;
            if (!ReminderCommand.TryParseRemindTimeCore(ctx.ArgumentStringTrimmed, now, out DateTime time))
            {
                await ctx.ReplyAsync("Failed to parse the duration");
                return;
            }

            TimeSpan duration = time - now;
            if (duration < TimeSpan.FromSeconds(1))
            {
                await ctx.ReplyAsync("Failed to parse the duration");
                return;
            }

            await RescanAsync(ctx, now - duration);
            return;
        }

        if (ctx.Arguments.Length < 1)
        {
            await ctx.ReplyAsync("Invalid args");
            return;
        }

        var currentUser = await GitHub.User.Current();

        foreach (string arg in ctx.Arguments)
        {
            if (!GitHubHelper.TryParseDotnetRuntimeIssueOrPRNumber(arg, out int number))
            {
                continue;
            }

            await SubscribeToRuntimeIssueAsync(currentUser, number);
        }
    }

    private async Task RescanAsync(CommandContext ctx, DateTimeOffset since)
    {
        var request = new RepositoryIssueRequest
        {
            Filter = IssueFilter.All,
            Since = since,
        };

        foreach (string area in new string[] { "System.Net", "System.Net.Http", "System.Net.Security", "System.Net.Sockets", "System.Net.Quic", "Extensions-HttpClientFactory" })
        {
            request.Labels.Add($"area-{area}");
        }

        var currentUser = await GitHub.User.Current();

        var issues = await GitHub.Issue.GetAllForRepository("dotnet", "runtime", request);
        await ctx.ReplyAsync($"Found {issues.Count} issues");

        foreach (var issue in issues)
        {
            if (await SubscribeToRuntimeIssueAsync(currentUser, issue.Number))
            {
                await Task.Delay(2_000);
            }

            await Task.Delay(100);
        }

        await ctx.ReplyAsync($"Finished subscribing to {issues.Count} issues");
    }

    private async Task<bool> SubscribeToRuntimeIssueAsync(User currentUser, int number)
    {
        return await _gitHubNotifications.ProcessGitHubMentionAsync(new GitHubComment(
            GitHub,
            "dotnet", "runtime", 1,
            $"https://github.com/dotnet/runtime/issues/{number}",
            "@dotnet/ncl  --  SubscribeToRuntimeIssueAsync",
            currentUser,
            IsPrReviewComment: false));
    }
}
