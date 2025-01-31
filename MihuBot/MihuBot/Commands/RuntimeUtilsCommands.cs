using MihuBot.RuntimeUtils;
using Octokit;

namespace MihuBot.Commands;

public sealed class RuntimeUtilsCommands : CommandBase
{
    private readonly GitHubClient _github;
    private readonly RuntimeUtilsService _runtimeUtilsService;

    public override string Command => "cancel";
    public override string[] Aliases => ["fuzz", "benchmark", "rebase", "merge", "format", "regexdiff", "backport", "corerootgen"];

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

            jobToCancel.FailFast("Terminated by admin", jobToCancel.GithubCommenterLogin == "MihaZupan");
            return;
        }

        const string Initiator = "MihaZupan";
        string arguments = $"{ctx.Command} {string.Join(' ', ctx.Arguments.Skip(1)).Trim()} -NoPRLink";

        if (ctx.Command == "corerootgen")
        {
            await ctx.ReplyAsync(_runtimeUtilsService.StartCoreRootGenerationJob(Initiator, arguments).ProgressDashboardUrl);
            return;
        }

        PullRequest pr = null;
        BranchReference branch = null;

        if (ctx.Arguments.Length < 1)
        {
            await ctx.ReplyAsync("Invalid args");
            return;
        }

        if (uint.TryParse(ctx.Arguments[0], out uint prNumber))
        {
            pr = ctx.Command == "backport"
                ? await _github.PullRequest.Get("microsoft", "reverse-proxy", (int)prNumber)
                : await _github.PullRequest.Get("dotnet", "runtime", (int)prNumber);
        }
        else if ((branch = await GitHubHelper.TryParseGithubRepoAndBranch(_github, ctx.Arguments[0])) is null && ctx.Command != "benchmark")
        {
            await ctx.ReplyAsync($"Failed to parse '{ctx.Arguments[0]}'");
            return;
        }

        JobBase job;
        GitHubComment comment = null;

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
                    ? _runtimeUtilsService.StartFuzzLibrariesJob(branch, Initiator, arguments)
                    : _runtimeUtilsService.StartFuzzLibrariesJob(pr, Initiator, arguments, comment);
            }
            else if (ctx.Command == "benchmark")
            {
                job =
                    branch is not null ? _runtimeUtilsService.StartBenchmarkJob(branch, Initiator, arguments) :
                    pr is not null ? _runtimeUtilsService.StartBenchmarkJob(pr, Initiator, arguments, comment) :
                    _runtimeUtilsService.StartBenchmarkJob(Initiator, arguments, comment: null);
            }
            else throw new UnreachableException(ctx.Command);
        }
        else if (ctx.Command is "regexdiff")
        {
            job = pr is null
                ? _runtimeUtilsService.StartRegexDiffJob(branch, Initiator, arguments)
                : _runtimeUtilsService.StartRegexDiffJob(pr, Initiator, arguments, comment);
        }
        else if (ctx.Command is "rebase" or "merge" or "format")
        {
            if (pr is null)
            {
                await ctx.ReplyAsync("Unsupported command on a custom branch");
                return;
            }

            job = _runtimeUtilsService.StartRebaseJob(pr, Initiator, arguments, comment);
        }
        else if (ctx.Command == "backport")
        {
            if (pr is null)
            {
                await ctx.ReplyAsync("Unsupported command on a custom branch");
                return;
            }

            job = _runtimeUtilsService.StartBackportJob(pr, Initiator, arguments, comment);
        }
        else
        {
            throw new NotImplementedException(ctx.Command);
        }

        await ctx.ReplyAsync(job.ProgressDashboardUrl);
    }
}
