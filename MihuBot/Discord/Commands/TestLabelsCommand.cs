using System.Globalization;
using Discord.Net;
using Discord.Rest;
using MihuBot.RuntimeUtils.AI;

namespace MihuBot.Discord.Commands;

public sealed class TestLabelsCommand(AreaLabelBacktestService backtest) : CommandBase
{
    public override string Command => "testlabels";

    internal const string Usage = "Usage: `!testlabels <issue-url|number> [--labeler-actor login]` or " +
        "`!testlabels backtest <owner/repo> <N> [--labeler-actor login]`. " +
        "Numbers default to dotnet/runtime. N must be 1-10000. Backtests with 25+ issues show progress. This never changes GitHub labels.";

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

        RestUserMessage statusMessage = null;
        string latestProgress = null;
        bool progressUnavailable = false;
        var progressTimer = Stopwatch.StartNew();

        try
        {
            statusMessage = await ctx.ReplyAsync($"Evaluating {(request.IssueNumber is { } number ? $"issue {number}" : $"the newest {request.Count} issues (open and closed)")} " +
                $"in {request.Repository}. No labels will be changed.", suppressMentions: true);

            var report = await backtest.RunAsync(request, ctx.CancellationToken, async (completed, total) =>
            {
                latestProgress = FormatProgress(completed, total);

                if (latestProgress is null || completed == total || (completed != 0 && progressTimer.Elapsed < TimeSpan.FromSeconds(5)))
                {
                    return;
                }

                await UpdateStatusAsync($"Evaluating labels in {request.Repository}. No labels will be changed.\n{latestProgress}");
                progressTimer.Restart();
            });

            await UpdateStatusAsync($"Label evaluation {(report.Cancelled ? "cancelled (partial report)" : "complete")}.\n{latestProgress}");
            await ctx.Channel.SendTextFileAsync($"LabelEvaluation-{Snowflake.NextString()}.txt", report.ToText(), report.Summary);
        }
        catch (Exception ex) when (!ctx.CancellationToken.IsCancellationRequested)
        {
            ctx.DebugLog(ex, "Label evaluation failed");
            await UpdateStatusAsync($"Label evaluation failed.\n{latestProgress}");
            await ctx.ReplyAsync($"Label evaluation failed: {ex.Message.TruncateWithDotDotDot(1000)}", suppressMentions: true);
        }
        catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
        {
            await UpdateStatusAsync($"Label evaluation cancelled.\n{latestProgress}");

            throw;
        }
        finally
        {
            _semaphore.Release();
        }

        async Task UpdateStatusAsync(string text)
        {
            if (latestProgress is null || progressUnavailable)
            {
                return;
            }

            try
            {
                await statusMessage.ModifyAsync(message =>
                {
                    message.Content = text;
                    message.AllowedMentions = AllowedMentions.None;
                });
            }
            catch (Exception ex) when (ex is HttpException or HttpRequestException or TimeoutException)
            {
                progressUnavailable = true;
                ctx.DebugLog(ex, "Failed to update label evaluation progress");
            }
        }
    }

    internal static string FormatProgress(int completed, int total)
    {
        if (total < 25)
        {
            return null;
        }

        const int width = 20;
        int filled = completed * width / total;

        return $"`[{new string('#', filled)}{new string('-', width - filled)}]` {completed * 100 / total}% ({completed}/{total} issues processed)";
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
            count is >= 1 and <= 10000)
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
