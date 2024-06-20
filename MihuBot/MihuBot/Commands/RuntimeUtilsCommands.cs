using MihuBot.RuntimeUtils;
using Octokit;

namespace MihuBot.Commands;

public sealed class RuntimeUtilsCommands : CommandBase
{
    private readonly GitHubClient _github;
    private readonly RuntimeUtilsService _runtimeUtilsService;

    public override string Command => "cancel";
    public override string[] Aliases => ["fuzz", "benchmark", "rebase", "merge", "format"];

    public RuntimeUtilsCommands(GitHubClient github, RuntimeUtilsService runtimeUtilsService)
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

        if (ctx.Command == "cancel")
        {
            if (ctx.Arguments.Length < 1)
            {
                await ctx.ReplyAsync("Invalid args");
                return;
            }

            if (!_runtimeUtilsService.TryGetJob(ctx.Arguments[0], true, out JobBase jobToCancel) &&
                !_runtimeUtilsService.TryGetJob(ctx.Arguments[0], false, out jobToCancel))
            {
                await ctx.ReplyAsync("Couldn't find that job");
                return;
            }

            jobToCancel.FailFast("Terminated by admin");
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

        const string Initiator = "MihaZupan";
        string arguments = $"{ctx.Command} {string.Join(' ', ctx.Arguments.Skip(1)).Trim()} -NoPRLink";

        if (ctx.Command is "fuzz" or "benchmark")
        {
            if (ctx.Arguments.Length < 2)
            {
                await ctx.ReplyAsync("Invalid args");
                return;
            }

            if (ctx.Command == "fuzz")
            {
                job = _runtimeUtilsService.StartFuzzLibrariesJob(pr, Initiator, arguments);
            }
            else
            {
                job = _runtimeUtilsService.StartBenchmarkJob(pr, Initiator, arguments);
            }
        }
        else if (ctx.Command is "rebase" or "merge" or "format")
        {
            job = _runtimeUtilsService.StartRebaseJob(pr, Initiator, arguments);
        }
        else
        {
            throw new NotImplementedException(ctx.Command);
        }

        await ctx.ReplyAsync(job.ProgressDashboardUrl);
    }
}
