using MihuBot.RuntimeUtils;

namespace MihuBot.Commands;

public sealed class NclMentionsCommand : CommandBase
{
    public override string Command => "ncl-mentions";

    private readonly GitHubNotificationsService _gitHubNotifications;

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

        if (ctx.Arguments.Length < 1)
        {
            await ctx.ReplyAsync("Invalid args");
            return;
        }

        var currentUser = await _gitHubNotifications.Github.User.Current();

        foreach (string arg in ctx.Arguments)
        {
            if (!GitHubHelper.TryParseDotnetRuntimeIssueOrPRNumber(arg, out int number))
            {
                continue;
            }

            await _gitHubNotifications.ProcessGitHubMentionAsync(new GitHubComment(
                _gitHubNotifications.Github,
                "dotnet", "runtime", 1,
                $"https://github.com/dotnet/runtime/issues/{number}",
                "@dotnet/ncl",
                currentUser,
                IsPrReviewComment: false));
        }
    }
}
