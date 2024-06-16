using MihuBot.RuntimeUtils;
using Octokit;

namespace MihuBot.Commands;

public sealed class FuzzCommand : CommandBase
{
    private readonly GitHubClient _github;
    private readonly RuntimeUtilsService _runtimeUtilsService;

    public override string Command => "fuzz";
    public override string[] Aliases => ["rebase", "merge", "format"];

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

        if (ctx.Arguments.Length < 1 ||
            !uint.TryParse(ctx.Arguments[0], out uint prNumber))
        {
            await ctx.ReplyAsync("Invalid args");
            return;
        }

        PullRequest pr = await _github.PullRequest.Get("dotnet", "runtime", (int)prNumber);

        JobBase job;

        if (ctx.Command == "fuzz")
        {
            if (ctx.Arguments.Length != 2 ||
                ctx.Arguments[1] is not { Length: > 0 } fuzzerName)
            {
                await ctx.ReplyAsync("Invalid args");
                return;
            }

            job = _runtimeUtilsService.StartFuzzLibrariesJob(
                pr,
                "MihaZupan",
                $"fuzz {fuzzerName} -NoPRLink");
        }
        else if (ctx.Command is "rebase" or "merge" or "format")
        {
            job = _runtimeUtilsService.StartRebaseJob(
                pr,
                "MihaZupan",
                $"{ctx.Command} -NoPRLink {string.Join(' ', ctx.Arguments.Skip(1))}".Trim());
        }
        else
        {
            throw new NotImplementedException(ctx.Command);
        }

        await ctx.ReplyAsync(job.ProgressDashboardUrl);
    }
}
