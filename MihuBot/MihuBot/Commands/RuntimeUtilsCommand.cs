using MihuBot.RuntimeUtils;
using Octokit;

namespace MihuBot.Commands;

public sealed class RuntimeUtilsCommand : CommandBase
{
    public override string Command => "jit-diff";
    public override string[] Aliases => new[] { "jitdiff" };

    private readonly RuntimeUtilsService _runtimeUtilsService;

    public RuntimeUtilsCommand(RuntimeUtilsService runtimeUtilsService)
    {
        _runtimeUtilsService = runtimeUtilsService;
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (!await ctx.RequirePermissionAsync(Command))
        {
            return;
        }

        if (ctx.Arguments.Length < 1 ||
            !int.TryParse(ctx.Arguments[0].Split('/').Last().Split('#').First(), out _))
        {
            await ctx.ReplyAsync("Need the pull request number");
            return;
        }

        await ExecuteAsync(ctx.Channel, ctx.Content);
    }

    public async Task ExecuteAsync(ISocketMessageChannel channel, string messageContent)
    {
        string[] parts = messageContent.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (TryParsePRNumber(parts[0], out int prNumber))
        {
            await channel.SendMessageAsync($"Can't recognize the PR link.");
            return;
        }

        PullRequest pullRequest;
        try
        {
            pullRequest = await _runtimeUtilsService.GetPullRequestAsync(prNumber);
        }
        catch
        {
            await channel.SendMessageAsync($"Failed to fetch PR #{prNumber} from GitHub's API.");
            return;
        }

        string arguments = parts.Length > 1 ? parts[1] : null;

        RuntimeUtilsJob job = _runtimeUtilsService.StartJob(pullRequest, arguments: arguments);

        await channel.SendMessageAsync(job.ProgressDashboardUrl);
    }

    public static bool TryParsePRNumber(string input, out int prNumber)
    {
        string[] parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (int.TryParse(parts[0], out prNumber) && prNumber > 0)
        {
            return true;
        }

        return Uri.TryCreate(parts[0], UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            uri.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith("/dotnet/runtime/pull/", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(uri.AbsolutePath.Split('/').Last(), out prNumber) &&
            prNumber > 0;
    }
}
