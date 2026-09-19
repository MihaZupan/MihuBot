using System.Globalization;
using Discord.Net;
using Discord.Rest;
using MihuBot.RuntimeUtils.AI;

namespace MihuBot.Discord.Commands;

public sealed class TestLabelsCommand(AreaLabelBacktestService backtest) : CommandBase
{
    private const int MaxInlineResultCharacters = 2000;

    public override string Command => "testlabels";

    internal const string Usage = "Usage: `!testlabels <issue-or-pr-url|number> [--prompt] [--labeler-actor login]` or " +
        "`!testlabels backtest <owner/repo> <N> [--prs] [--labeler-actor login]`. " +
        "Numbers default to dotnet/runtime. Backtests default to issues; --prs selects pull requests. " +
        "N must be 1-10000. Single items return a compact result (attached if too long); backtests return a detailed report and show progress with 25+ items. " +
        "--prompt also attaches the exact model prompt for a single item.";

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
            statusMessage = await ctx.ReplyAsync($"Evaluating {(request.IssueNumber is { } number ? $"issue or PR {number}" : $"the newest {request.Count} {request.ItemPlural} (open and closed)")} " +
                $"in {request.Repository}.", suppressMentions: true);

            var report = await backtest.RunAsync(request, ctx.CancellationToken, async (completed, total) =>
            {
                latestProgress = FormatProgress(completed, total, request.PullRequests);

                if (latestProgress is null || completed == total || (completed != 0 && progressTimer.Elapsed < TimeSpan.FromSeconds(5)))
                {
                    return;
                }

                await UpdateStatusAsync($"Evaluating labels in {request.Repository}.\n{latestProgress}");
                progressTimer.Restart();
            });

            if (request.IssueNumber is not null)
            {
                await SendResultAsync(report.ToSingleItemText(), replaceStatus: true);

                if (request.IncludePrompt)
                {
                    if (report.Issues.SingleOrDefault()?.Prompt is { } prompt)
                    {
                        await ctx.Channel.SendTextFileAsync($"LabelPrompt-{request.IssueNumber}-{Snowflake.NextString()}.txt", prompt);
                    }
                    else
                    {
                        await ctx.ReplyAsync("No model prompt was submitted for this evaluation.", suppressMentions: true);
                    }
                }
            }
            else
            {
                await UpdateStatusAsync($"Label evaluation {(report.Cancelled ? "cancelled (partial report)" : "complete")}.\n{latestProgress}");
                await ctx.Channel.SendTextFileAsync($"LabelEvaluation-{Snowflake.NextString()}.txt", report.ToText(), report.Summary);
            }
        }
        catch (Exception ex) when (!ctx.CancellationToken.IsCancellationRequested)
        {
            ctx.DebugLog(ex, "Label evaluation failed");
            string error = $"Label evaluation failed: {ex.Message}";

            if (request.IssueNumber is null)
            {
                await UpdateStatusAsync($"Label evaluation failed.\n{latestProgress}");
            }

            await SendResultAsync(error, replaceStatus: request.IssueNumber is not null);
        }
        catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
        {
            await UpdateStatusAsync($"Label evaluation cancelled.\n{latestProgress}", force: request.IssueNumber is not null);

            throw;
        }
        finally
        {
            _semaphore.Release();
        }

        async Task SendResultAsync(string text, bool replaceStatus)
        {
            if (text.Length > MaxInlineResultCharacters)
            {
                if (replaceStatus)
                {
                    await UpdateStatusAsync("Full evaluation output attached.", force: true);
                }

                await ctx.Channel.SendTextFileAsync($"LabelEvaluation-{Snowflake.NextString()}.txt", text);
            }
            else if (!replaceStatus || !await UpdateStatusAsync(text, force: true))
            {
                await ctx.ReplyAsync(text, suppressMentions: true);
            }
        }

        async Task<bool> UpdateStatusAsync(string text, bool force = false)
        {
            if ((!force && latestProgress is null) || progressUnavailable || statusMessage is null)
            {
                return false;
            }

            try
            {
                await statusMessage.ModifyAsync(message =>
                {
                    message.Content = text;
                    message.AllowedMentions = AllowedMentions.None;
                });
                return true;
            }
            catch (Exception ex) when (ex is HttpException or HttpRequestException or TimeoutException)
            {
                progressUnavailable = true;
                ctx.DebugLog(ex, "Failed to update label evaluation progress");
                return false;
            }
        }
    }

    internal static string FormatProgress(int completed, int total, bool pullRequests = false)
    {
        if (total < 25)
        {
            return null;
        }

        const int width = 20;
        int filled = completed * width / total;

        return $"`[{new string('#', filled)}{new string('-', width - filled)}]` {completed * 100 / total}% ({completed}/{total} {(pullRequests ? "pull requests" : "issues")} processed)";
    }

    internal static bool TryParseArguments(string[] arguments, out AreaLabelBacktestRequest request)
    {
        request = null;
        string actor = "github-actions[bot]";
        bool pullRequests = false;
        bool includePrompt = false;
        bool hasActor = false;
        int option = Array.FindIndex(arguments, a => a.StartsWith("--", StringComparison.Ordinal));
        int positionalCount = option < 0 ? arguments.Length : option;

        for (int i = positionalCount; i < arguments.Length; i++)
        {
            if (arguments[i].Equals("--prs", StringComparison.OrdinalIgnoreCase) && !pullRequests)
            {
                pullRequests = true;
            }
            else if (arguments[i].Equals("--prompt", StringComparison.OrdinalIgnoreCase) && !includePrompt)
            {
                includePrompt = true;
            }
            else if (arguments[i].Equals("--labeler-actor", StringComparison.OrdinalIgnoreCase) && !hasActor &&
                i + 1 < arguments.Length && !string.IsNullOrWhiteSpace(arguments[i + 1]) && !arguments[i + 1].StartsWith('-'))
            {
                hasActor = true;
                actor = arguments[++i];
            }
            else
            {
                return false;
            }
        }

        arguments = arguments[..positionalCount];

        if (arguments is [var mode, var repo, var countText] &&
            !includePrompt &&
            mode.Equals("backtest", StringComparison.OrdinalIgnoreCase) &&
            GitHubHelper.TryParseRepoOwnerAndName(repo, out string owner, out string name, out string[] extra) &&
            extra.Length == 0 &&
            int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out int count) &&
            count is >= 1 and <= 10000)
        {
            request = new($"{owner}/{name}", null, count, actor, pullRequests);

            return true;
        }

        if (arguments.Length == 1 &&
            GitHubHelper.TryParseIssueOrPRNumber(arguments[0], out string repository, out int number))
        {
            request = new(repository ?? "dotnet/runtime", number, 1, actor, pullRequests, includePrompt);

            return true;
        }

        return false;
    }
}
