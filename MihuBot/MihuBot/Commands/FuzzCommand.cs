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

        var job = _runtimeUtilsService.StartFuzzLibrariesJob(
            await _github.PullRequest.Get("dotnet", "runtime", 101993),
            "MihaZupan",
            "fuzz HttpHeadersFuzzer -NoPRLink");

        await ctx.ReplyAsync(job.ProgressDashboardUrl);
    }
}
