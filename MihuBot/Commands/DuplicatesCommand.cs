using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MihuBot.Configuration;
using MihuBot.DB.GitHub;
using MihuBot.RuntimeUtils;
using Octokit;

namespace MihuBot.Commands;

public sealed class DuplicatesCommand : CommandBase
{
    public override string Command => "duplicates";
    public override string[] Aliases => ["forcetriage"];

    private readonly FileBackedHashSet _processedIssuesForDuplicateDetection = new("ProcessedIssuessForDuplicateDetection.txt");
    private readonly ConcurrentDictionary<string, (IssueInfo Issue, string DuplicatesSummary)> _duplicatesToPost = [];

    private readonly IDbContextFactory<GitHubDbContext> _db;
    private readonly GitHubClient _github;
    private readonly IssueTriageService _triageService;
    private readonly IssueTriageHelper _triageHelper;
    private readonly ServiceConfiguration _serviceConfiguration;
    private readonly Logger _logger;
    private readonly IConfigurationService _configuration;

    private bool SkipManualVerificationBeforePosting => _configuration.GetOrDefault(null, $"{Command}.AutoPost", false);

    public DuplicatesCommand(IDbContextFactory<GitHubDbContext> db, IssueTriageService triageService, IssueTriageHelper triageHelper, ServiceConfiguration serviceConfiguration, Logger logger, IConfigurationService configuration, GitHubClient github)
    {
        _db = db;
        _triageService = triageService;
        _triageHelper = triageHelper;
        _serviceConfiguration = serviceConfiguration;
        _logger = logger;
        _configuration = configuration;
        _github = github;
    }

    public override async Task HandleMessageComponentAsync(SocketMessageComponent component)
    {
        if (_duplicatesToPost.TryRemove(component.Data.CustomId, out (IssueInfo Issue, string DuplicatesSummary) data))
        {
            await component.UpdateAsync(m => m.Components = null);
            await PostGhCommentSummary(data.Issue, data.DuplicatesSummary);
        }
        else if (component.Data.CustomId.EndsWith("-no", StringComparison.Ordinal))
        {
            await component.UpdateAsync(m => m.Components = null);
        }
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (ctx.IsFromAdmin)
        {
            return;
        }

        if (ctx.Arguments.Length != 1 || !GitHubHelper.TryParseIssueOrPRNumber(ctx.Arguments[0], out string repoName, out int issueNumber))
        {
            await ctx.ReplyAsync("Invalid issue/PR URL. Use the number or the full link.");
            return;
        }

        IssueInfo issue = await _triageHelper.GetIssueAsync(repoName ?? "dotnet/runtime", issueNumber, ctx.CancellationToken);

        if (issue is null)
        {
            await ctx.ReplyAsync("Issue not found in database.");
            return;
        }

        if (ctx.Command == "duplicates")
        {
            (IssueInfo Issue, double Certainty, string Summary)[] duplicates = await DetectIssueDuplicatesAsync(issue, ctx.CancellationToken);

            string reply = duplicates.Length == 0
                ? "No duplicates found."
                : FormatDuplicatesSummary(issue, duplicates);

            if (reply.Length <= 1800)
            {
                await ctx.ReplyAsync(reply);
            }
            else
            {
                await ctx.Channel.SendTextFileAsync($"Duplicates-{issue.Number}.txt", reply);
            }
        }
        else
        {
            Uri issueUrl = await _triageService.ManualTriageAsync(issue, ctx.CancellationToken);
            await ctx.ReplyAsync($"Triage completed. See the issue at <{issueUrl.AbsoluteUri}>.");
        }
    }

    public override Task InitAsync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
                var sempahore = new SemaphoreSlim(2, 2);

                while (await timer.WaitForNextTickAsync())
                {
                    try
                    {
                        if (_serviceConfiguration.PauseAutoDuplicateDetection)
                        {
                            continue;
                        }

                        await using GitHubDbContext db = _db.CreateDbContext();

                        DateTime startDate = DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(5));

                        IQueryable<IssueInfo> query = db.Issues
                            .AsNoTracking()
                            .Where(i => i.CreatedAt >= startDate)
                            .OrderByDescending(i => i.CreatedAt);

                        query = IssueTriageHelper.AddIssueInfoIncludes(query);

                        IssueInfo[] issues = await query
                            .Take(100)
                            .AsSplitQuery()
                            .ToArrayAsync();

                        foreach (IssueInfo issue in issues)
                        {
                            if (issue.PullRequest is not null || !issue.User.IsLikelyARealUser())
                            {
                                continue;
                            }

                            if (!_processedIssuesForDuplicateDetection.TryAdd(issue.Id))
                            {
                                continue;
                            }

                            _ = Task.Run(async () =>
                            {
                                await sempahore.WaitAsync();
                                try
                                {
                                    (IssueInfo Issue, double Certainty, string Summary)[] duplicates = await DetectIssueDuplicatesAsync(issue, CancellationToken.None);

                                    if (duplicates.Length > 0)
                                    {
                                        SocketTextChannel channel = _logger.Options.Discord.GetTextChannel(1396832159888703498UL);
                                        MessageComponent components = null;

                                        string reply = FormatDuplicatesSummary(issue, duplicates, includeSummary: false);

                                        if (duplicates.Any(d => IsLikelyUsefulToReport(d.Issue, d.Certainty)))
                                        {
                                            reply = $"{MentionUtils.MentionUser(KnownUsers.Miha)} {reply}";

                                            string ghComment =
                                                $"""
                                                Possible related and/or duplicate issue{(duplicates.Length > 1 ? "s" : "")}:
                                                {string.Join('\n', duplicates.Where(d => d.Certainty >= 0.9).Take(5).Select(d => $"- {d.Issue.HtmlUrl}"))}
                                                """;

                                            if (SkipManualVerificationBeforePosting)
                                            {
                                                await PostGhCommentSummary(issue, ghComment);
                                            }
                                            else
                                            {
                                                string id = $"{Command}-{issue.Id}";
                                                _duplicatesToPost.TryAdd(id, (issue, ghComment));

                                                components = new ComponentBuilder()
                                                    .WithButton("Post", id, ButtonStyle.Success)
                                                    .WithButton("Cancel", $"{Command}-no", ButtonStyle.Danger)
                                                    .Build();
                                            }
                                        }

                                        reply = reply.TruncateWithDotDotDot(1800);

                                        await channel.SendTextFileAsync($"Duplicates-{issue.Number}.txt", FormatDuplicatesSummary(issue, duplicates), reply, components);
                                    }

                                    bool IsLikelyUsefulToReport(IssueInfo duplicate, double certainty)
                                    {
                                        if (certainty < 0.95)
                                        {
                                            return false;
                                        }

                                        if (duplicate.UserId == issue.UserId && duplicate.UserId != GitHubDataService.GhostUserId)
                                        {
                                            // Same author? They're likely aware of the other issue.
                                            return false;
                                        }

                                        if (string.IsNullOrEmpty(issue.Title))
                                        {
                                            // Shouldn't really happen?
                                            return false;
                                        }

                                        if (duplicate.PullRequest is not null)
                                        {
                                            // Let's not report it if only PRs are duplicates.
                                            return false;
                                        }

                                        if (issue.Title.Contains(duplicate.Number.ToString(), StringComparison.Ordinal))
                                        {
                                            // Likely already mentioned as related.
                                            return false;
                                        }

                                        if (string.IsNullOrWhiteSpace(issue.Body))
                                        {
                                            // Similar just by title.
                                            return true;
                                        }

                                        if (issue.Body.Contains(duplicate.Number.ToString(), StringComparison.Ordinal))
                                        {
                                            // Likely already mentioned as related.
                                            return false;
                                        }

                                        return true;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    await _logger.DebugAsync($"{nameof(AdminCommands)}: Error during duplicate detection for issue <{issue.HtmlUrl}>", ex);
                                }
                                finally
                                {
                                    sempahore.Release();
                                }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        await _logger.DebugAsync($"{nameof(AdminCommands)}: Error during periodic duplicate detection", ex);
                    }
                }
            }
            catch { }
        });

        return Task.CompletedTask;
    }

    private async Task<(IssueInfo Issue, double Certainty, string Summary)[]> DetectIssueDuplicatesAsync(IssueInfo issue, CancellationToken cancellationToken)
    {
        IssueTriageHelper.ModelInfo model = _triageHelper.DefaultModel;

        if (_configuration.TryGet(null, "Duplicates.Model", out string modelName))
        {
            model = _triageHelper.AvailableModels.FirstOrDefault(m => m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase)) ?? model;
        }

        var options = new IssueTriageHelper.TriageOptions(model, "MihaZupan", issue, OnToolLog: i => { }, SkipCommentsOnCurrentIssue: true);

        int attemptCount = 0;
        while (true)
        {
            try
            {
                return await _triageHelper.DetectDuplicateIssuesAsync(options, cancellationToken);
            }
            catch (JsonException) when (++attemptCount < 3) { }
        }
    }

    private static string FormatDuplicatesSummary(IssueInfo issue, (IssueInfo Issue, double Certainty, string Summary)[] duplicates, bool includeSummary = true)
    {
        return $"Duplicate issues for [{issue.Repository.FullName}#{issue.Number}](<{issue.HtmlUrl}>) - {issue.Title}:\n" +
            string.Join('\n', duplicates.Select(r => $"- ({r.Certainty:F2}) [#{r.Issue.Number} - {r.Issue.Title}](<{r.Issue.HtmlUrl}>){(includeSummary ? $"\n  - {r.Summary}" : null)}"));
    }

    private async Task PostGhCommentSummary(IssueInfo issue, string comment)
    {
        try
        {
            await _github.Issue.Comment.Create(issue.Repository.Id, issue.Number, comment);
        }
        catch (Exception ex)
        {
            await _logger.DebugAsync($"{nameof(DuplicatesCommand)}: Error posting duplicate summary comment for issue <{issue.HtmlUrl}>", ex);
        }
    }
}
