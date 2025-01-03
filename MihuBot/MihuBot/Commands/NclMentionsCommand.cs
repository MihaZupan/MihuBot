using MihuBot.RuntimeUtils;
using Octokit;

namespace MihuBot.Commands;

public sealed class NclMentionsCommand : CommandBase
{
    public override string Command => "ncl-mentions";
    public override string[] Aliases => ["ncl-mentions-rescan", "ncl-mentions-rescan-open"];

    private readonly GitHubNotificationsService _gitHubNotifications;
    private readonly Logger _logger;

    private GitHubClient GitHub => _gitHubNotifications.Github;

    public NclMentionsCommand(GitHubNotificationsService gitHubNotifications, Logger logger)
    {
        _gitHubNotifications = gitHubNotifications;
        _logger = logger;
    }

    public override Task InitAsync()
    {
        _ = Task.Run(async () =>
        {
            Stopwatch lastLongScan = Stopwatch.StartNew();

            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            while (await timer.WaitForNextTickAsync())
            {
                try
                {
                    _logger.DebugLog("Running periodic NCL mentions rescan");

                    TimeSpan duration = TimeSpan.FromHours(2);

                    if (lastLongScan.Elapsed.TotalDays > 2)
                    {
                        lastLongScan.Restart();
                        duration = TimeSpan.FromDays(90);
                    }

                    await RescanAsync(
                        _logger.Options.DebugTextChannel,
                        DateTime.UtcNow - duration,
                        ItemStateFilter.All);
                }
                catch (Exception ex)
                {
                    await _logger.DebugAsync(ex.ToString());
                }
            }
        });

        return Task.CompletedTask;
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (!ctx.Author.IsAdmin())
        {
            return;
        }

        if (ctx.Command is "ncl-mentions-rescan" or "ncl-mentions-rescan-open")
        {
            var now = DateTime.UtcNow;
            if (!ReminderCommand.TryParseRemindTimeCore(ctx.ArgumentStringTrimmed, now, out DateTime time))
            {
                await ctx.ReplyAsync("Failed to parse the duration");
                return;
            }

            TimeSpan duration = time - now;

            ctx.DebugLog($"Parsed '{ctx.ArgumentStringTrimmed}' to {duration.ToElapsedTime()}");

            await ctx.Message.AddReactionAsync(Emotes.ThumbsUp);

            await RescanAsync(ctx.Channel, now - duration, ctx.Command == "ncl-mentions-rescan-open" ? ItemStateFilter.Open : ItemStateFilter.All);
            return;
        }

        if (ctx.Arguments.Length < 1)
        {
            await ctx.ReplyAsync("Invalid args");
            return;
        }

        List<Issue> issues = [];

        foreach (var arg in ctx.Arguments)
        {
            if (GitHubHelper.TryParseDotnetRuntimeIssueOrPRNumber(arg, out int number))
            {
                issues.Add(await GitHub.Issue.Get("dotnet", "runtime", number));
            }
        }

        int subscribedTo = await SubscribeToRuntimeIssuesAsync(issues.ToArray());

        await ctx.ReplyAsync($"Subscribed to {subscribedTo} new issues");
    }

    private async Task RescanAsync(SocketTextChannel channel, DateTimeOffset since, ItemStateFilter state)
    {
        foreach (string area in new string[] { "System.Net", "System.Net.Http", "System.Net.Security", "System.Net.Sockets", "System.Net.Quic", "Extensions-HttpClientFactory" })
        {
            var request = new RepositoryIssueRequest
            {
                State = state,
                Filter = IssueFilter.All,
                Since = since,
            };

            request.Labels.Add($"area-{area}");

            var issues = await GitHub.Issue.GetAllForRepository("dotnet", "runtime", request);

            int subscribedTo = await SubscribeToRuntimeIssuesAsync(issues.ToArray());

            if (subscribedTo > 0)
            {
                await channel.SendMessageAsync($"Found {issues.Count} issues for `{area}` since {since.UtcDateTime.ToISODateTime()}, subscribed to {subscribedTo} new ones");
            }
        }
    }

    private async Task<int> SubscribeToRuntimeIssuesAsync(Issue[] issues)
    {
        var currentUser = await GitHub.User.Current();

        int counter = 0;

        foreach (var issue in issues)
        {
            if (await SubscribeToRuntimeIssueAsync(currentUser, issue))
            {
                counter++;
                await Task.Delay(2_000);
            }

            await Task.Delay(10);
        }

        return counter;
    }

    private async Task<bool> SubscribeToRuntimeIssueAsync(User currentUser, Issue issue)
    {
        return await _gitHubNotifications.ProcessGitHubMentionAsync(new GitHubComment(
            GitHub,
            "dotnet", "runtime", 1,
            issue.HtmlUrl,
            "@dotnet/ncl  --  SubscribeToRuntimeIssueAsync",
            currentUser,
            IsPrReviewComment: false),
            issue);
    }
}
