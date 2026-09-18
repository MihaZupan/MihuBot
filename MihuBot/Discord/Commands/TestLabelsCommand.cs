using System.Globalization;
using MihuBot.RuntimeUtils.AI;

namespace MihuBot.Discord.Commands;

public sealed class TestLabelsCommand(AreaLabelBacktestService backtest) : CommandBase
{
    public override string Command => "testlabels";

    internal const string Usage = "Usage: `!testlabels <issue-url|number> [--labeler-actor login]` or " +
        "`!testlabels backtest <owner/repo> <N> [--labeler-actor login]`. " +
        "Numbers default to dotnet/runtime. N must be 1-1000. This never changes GitHub labels.";

    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (!ctx.IsFromAdmin)
        {
            return;
        }

        if (!TryParseArguments(ctx.Arguments, out var request))
        {
            await ctx.ReplyAsync(Usage);

            return;
        }

        if (!await _semaphore.WaitAsync(0, ctx.CancellationToken))
        {
            await ctx.ReplyAsync("A label evaluation is already running. Please wait for its report.");

            return;
        }

        try
        {
            await ctx.ReplyAsync($"Evaluating {(request.IssueNumber is { } number ? $"issue {number}" : $"the newest {request.Count} issues (open and closed)")} " +
                $"in {request.Repository}. No labels will be changed.", suppressMentions: true);

            var report = await backtest.RunAsync(request, ctx.CancellationToken);
            await ctx.Channel.SendTextFileAsync($"LabelEvaluation-{Snowflake.NextString()}.txt", report.ToText(), report.Summary);
        }
        catch (Exception ex) when (!ctx.CancellationToken.IsCancellationRequested)
        {
            ctx.DebugLog(ex, "Label evaluation failed");
            await ctx.ReplyAsync($"Label evaluation failed: {ex.Message.TruncateWithDotDotDot(1000)}", suppressMentions: true);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    internal static bool TryParseArguments(string[] arguments, out AreaLabelBacktestRequest request)
    {
        request = null;
        string actor = "github-actions[bot]";
        int option = Array.FindIndex(arguments, a => a.Equals("--labeler-actor", StringComparison.OrdinalIgnoreCase));

        if (option >= 0)
        {
            if (option != arguments.Length - 2 || string.IsNullOrWhiteSpace(arguments[^1]) ||
                arguments[^1].StartsWith('-'))
            {
                return false;
            }

            actor = arguments[^1];
            arguments = arguments[..option];
        }

        if (arguments is [var mode, var repo, var countText] &&
            mode.Equals("backtest", StringComparison.OrdinalIgnoreCase) &&
            GitHubHelper.TryParseRepoOwnerAndName(repo, out string owner, out string name, out string[] extra) &&
            extra.Length == 0 &&
            int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out int count) &&
            count is >= 1 and <= 1000)
        {
            request = new($"{owner}/{name}", null, count, actor);

            return true;
        }

        if (arguments.Length == 1 &&
            GitHubHelper.TryParseIssueOrPRNumber(arguments[0], out string repository, out int number))
        {
            request = new(repository ?? "dotnet/runtime", number, 1, actor);

            return true;
        }

        return false;
    }
}
