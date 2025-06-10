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
        if (OperatingSystem.IsLinux())
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
        }

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

        foreach (string arg in ctx.Arguments)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();

            if (GitHubHelper.TryParseIssueOrPRNumber(arg, out int number))
            {
                issues.Add(await GitHub.Issue.Get("dotnet", "runtime", number));
            }
        }

        int subscribedTo = await SubscribeToRuntimeIssuesAsync(issues.ToArray(), ctx.CancellationToken);

        await ctx.ReplyAsync($"Subscribed to {subscribedTo} new issues");
    }

    private async Task RescanAsync(SocketTextChannel channel, DateTimeOffset since, ItemStateFilter state, CancellationToken cancellationToken = default)
    {
        foreach (string area in Constants.NetworkingLabels)
        {
            var request = new RepositoryIssueRequest
            {
                State = state,
                Filter = IssueFilter.All,
                Since = since,
            };

            request.Labels.Add(area);

            var issues = await GitHub.Issue.GetAllForRepository("dotnet", "runtime", request);

            int subscribedTo = await SubscribeToRuntimeIssuesAsync(issues.ToArray(), cancellationToken);

            if (subscribedTo > 0)
            {
                await channel.SendMessageAsync($"Found {issues.Count} issues for `{area}` since {since.UtcDateTime.ToISODateTime()}, subscribed to {subscribedTo} new ones");
            }
        }
    }

    private async Task<int> SubscribeToRuntimeIssuesAsync(Issue[] issues, CancellationToken cancellationToken)
    {
        User currentUser = await GitHub.User.Current();

        int counter = 0;

        foreach (Issue issue in issues)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await SubscribeToRuntimeIssueAsync(currentUser, issue))
            {
                counter++;
                await Task.Delay(2_000, cancellationToken);
            }

            await Task.Delay(10, cancellationToken);
        }

        return counter;
    }

    private async Task<bool> SubscribeToRuntimeIssueAsync(User currentUser, Issue issue)
    {
        return await _gitHubNotifications.ProcessGitHubMentionAsync(new DB.GitHub.CommentInfo
        {
            Id = "210716005/1",
            HtmlUrl = issue.HtmlUrl,
            Body = "@dotnet/ncl  --  SubscribeToRuntimeIssueAsync",
            Issue = new DB.GitHub.IssueInfo
            {
                Number = issue.Number,
                NodeIdentifier = issue.NodeId,
                HtmlUrl = issue.HtmlUrl,
                Repository = new DB.GitHub.RepositoryInfo
                {
                    Owner = new DB.GitHub.UserInfo { Login = "dotnet" },
                    Name = "runtime"
                },
            },
            UserId = currentUser.Id,
            User = new DB.GitHub.UserInfo
            {
                Id = currentUser.Id,
                Login = currentUser.Login,
                Name = currentUser.Name,
            },
            IsPrReviewComment = false,
        });
    }
}
