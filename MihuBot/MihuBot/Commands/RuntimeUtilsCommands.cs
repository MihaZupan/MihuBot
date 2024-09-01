using MihuBot.RuntimeUtils;
using Octokit;

namespace MihuBot.Commands;

public sealed class RuntimeUtilsCommands : CommandBase
{
    private readonly GitHubClient _github;
    private readonly RuntimeUtilsService _runtimeUtilsService;

    public override string Command => "cancel";
    public override string[] Aliases => ["fuzz", "benchmark", "rebase", "merge", "format", "regexdiff"];

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

        PullRequest pr = null;
        string repository = null;
        string branch = null;

        if (ctx.Arguments.Length < 1)
        {
            await ctx.ReplyAsync("Invalid args");
            return;
        }

        if (uint.TryParse(ctx.Arguments[0], out uint prNumber))
        {
            pr = await _github.PullRequest.Get("dotnet", "runtime", (int)prNumber);
        }
        else if (!GitHubHelper.TryParseGithubRepoAndBranch(ctx.Arguments[0], out repository, out branch))
        {
            await ctx.ReplyAsync($"Failed to parse '{ctx.Arguments[0]}'");
            return;
        }

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
                job = pr is null
                    ? _runtimeUtilsService.StartFuzzLibrariesJob(repository, branch, Initiator, arguments)
                    : _runtimeUtilsService.StartFuzzLibrariesJob(pr, Initiator, arguments);
            }
            else if (ctx.Command == "benchmark")
            {
                job = pr is null
                    ? _runtimeUtilsService.StartBenchmarkJob(repository, branch, Initiator, arguments)
                    : _runtimeUtilsService.StartBenchmarkJob(pr, Initiator, arguments);
            }
            else throw new UnreachableException(ctx.Command);
        }
        else if (ctx.Command is "regexdiff")
        {
            job = pr is null
                ? _runtimeUtilsService.StartRegexDiffJob(repository, branch, Initiator, arguments)
                : _runtimeUtilsService.StartRegexDiffJob(pr, Initiator, arguments);
        }
        else if (ctx.Command is "rebase" or "merge" or "format")
        {
            if (pr is null)
            {
                await ctx.ReplyAsync("Unsupported command on a custom branch");
                return;
            }

            job = _runtimeUtilsService.StartRebaseJob(pr, Initiator, arguments);
        }
        else
        {
            throw new NotImplementedException(ctx.Command);
        }

        await ctx.ReplyAsync(job.ProgressDashboardUrl);
    }
}
