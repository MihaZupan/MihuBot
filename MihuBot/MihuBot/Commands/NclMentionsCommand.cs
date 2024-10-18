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

            ctx.DebugLog($"Parsed '{ctx.ArgumentStringTrimmed}' to {duration.ToElapsedTime()}");

            await RescanAsync(ctx, now - duration);
            return;
        }

        if (ctx.Arguments.Length < 1)
        {
            await ctx.ReplyAsync("Invalid args");
            return;
        }

        await SubscribeToRuntimeIssuesAsync(ctx, ctx.Arguments
            .Select(arg => GitHubHelper.TryParseDotnetRuntimeIssueOrPRNumber(arg, out int number) ? number : -1)
            .Where(n => n > 0)
            .ToArray());
    }

    private async Task RescanAsync(CommandContext ctx, DateTimeOffset since)
    {
        foreach (string area in new string[] { "System.Net", "System.Net.Http", "System.Net.Security", "System.Net.Sockets", "System.Net.Quic", "Extensions-HttpClientFactory" })
        {
            var request = new RepositoryIssueRequest
            {
                State = ItemStateFilter.All,
                Filter = IssueFilter.All,
                Since = since,
            };

            request.Labels.Add($"area-{area}");

            var issues = await GitHub.Issue.GetAllForRepository("dotnet", "runtime", request);
            await ctx.ReplyAsync($"Found {issues.Count} issues for `{area}`");

            await SubscribeToRuntimeIssuesAsync(ctx, issues.Select(i => i.Number).ToArray());
        }
    }

    private async Task SubscribeToRuntimeIssuesAsync(CommandContext ctx, int[] numbers)
    {
        var currentUser = await GitHub.User.Current();

        int counter = 0;

        foreach (var issue in numbers)
        {
            if (await SubscribeToRuntimeIssueAsync(currentUser, issue))
            {
                counter++;
                await Task.Delay(2_000);
            }

            await Task.Delay(100);
        }

        await ctx.ReplyAsync($"Subscribed to {counter} new issues");
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
