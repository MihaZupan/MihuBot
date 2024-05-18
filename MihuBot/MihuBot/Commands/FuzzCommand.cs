using MihuBot.RuntimeUtils;
using Octokit;

namespace MihuBot.Commands;

public sealed class FuzzCommand : CommandBase
{
    private readonly GitHubClient _github;
    private readonly RuntimeUtilsService _runtimeUtilsService;

    public override string Command => "fuzz";

    public FuzzCommand(GitHubClient github, RuntimeUtilsService runtimeUtilsService)
    {
        _github = github;
        _runtimeUtilsService = runtimeUtilsService;
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (!ctx.IsFromAdmin)
        {
            return;
        }

        if (ctx.Arguments.Length != 2 ||
            !uint.TryParse(ctx.Arguments[0], out uint prNumber) ||
            ctx.Arguments[1] is not { Length: > 3 } fuzzerName)
        {
            await ctx.ReplyAsync("Invalid args");
            return;
        }

        var job = _runtimeUtilsService.StartFuzzLibrariesJob(
            await _github.PullRequest.Get("dotnet", "runtime", (int)prNumber),
            "MihaZupan",
            $"fuzz {fuzzerName} -NoPRLink");

        await ctx.ReplyAsync(job.ProgressDashboardUrl);
    }
}
