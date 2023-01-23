using Octokit;

namespace MihuBot.Commands
{
    public sealed class RuntimeUtilsCommand : CommandBase
    {
        public override string Command => "jit-diff";
        public override string[] Aliases => new[] { "jitdiff" };

        private readonly RuntimeUtilsService _runtimeUtilsService;
        private readonly GitHubClient _github;

        public RuntimeUtilsCommand(RuntimeUtilsService runtimeUtilsService, GitHubClient github)
        {
            _runtimeUtilsService = runtimeUtilsService;
            _github = github;
        }

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!await ctx.RequirePermissionAsync(Command))
            {
                return;
            }

            if (ctx.Arguments.Length != 1 ||
                !int.TryParse(ctx.Arguments[0].Split('/').Last().Split('#').First(), out int prNumber))
            {
                await ctx.ReplyAsync("Need the pull request number");
                return;
            }

            PullRequest pullRequest;
            try
            {
                pullRequest = await _github.PullRequest.Get("dotnet", "runtime", prNumber);
            }
            catch
            {
                await ctx.ReplyAsync($"Failed to get PR #{prNumber}");
                return;
            }

            RuntimeUtilsJob job = _runtimeUtilsService.StartJob(pullRequest);

            await ctx.ReplyAsync($"{job.ProgressUrl}\nPrivate id: `{job.JobId}`");
        }
    }
}
