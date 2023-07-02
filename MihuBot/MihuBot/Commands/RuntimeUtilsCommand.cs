﻿using Octokit;

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

            await ExecuteAsync(ctx.Channel, $"https://github.com/dotnet/runtime/pull/{prNumber}");
        }

        public async Task ExecuteAsync(ISocketMessageChannel channel, string dotnetRuntimePR)
        {
            if (!Uri.TryCreate(dotnetRuntimePR, UriKind.Absolute, out var uri) ||
                !(uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ||
                !uri.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                !uri.AbsolutePath.StartsWith("/dotnet/runtime/pull/", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(uri.AbsolutePath.Split('/').Last(), out int prNumber))
            {
                await channel.SendMessageAsync($"Can't recognize the PR link.");
                return;
            }

            PullRequest pullRequest;
            try
            {
                pullRequest = await _github.PullRequest.Get("dotnet", "runtime", prNumber);
                ArgumentNullException.ThrowIfNull(pullRequest);
            }
            catch
            {
                await channel.SendMessageAsync($"Failed to fetch PR #{prNumber} from GitHub's API.");
                return;
            }

            RuntimeUtilsJob job = _runtimeUtilsService.StartJob(pullRequest);

            await channel.SendMessageAsync(job.ProgressDashboardUrl);
        }
    }
}
